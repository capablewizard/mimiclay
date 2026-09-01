using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Drives a Charades game: the host-authoritative phase machine that walks
/// <see cref="CharadesPhase.Waiting"/> → (<see cref="CharadesPhase.Choosing"/> →
/// <see cref="CharadesPhase.Sculpting"/> → <see cref="CharadesPhase.TurnReveal"/>) per turn until someone
/// reaches the target score → <see cref="CharadesPhase.Podium"/> → back to the lobby (a direct Play, which has
/// no lobby behind it, loops to Waiting instead). Runs on a picked charades MAP — one with a
/// <see cref="CharadesStage"/> prefab placed in it — spawned by <see cref="RoundManagerSpawner"/> like every
/// other game's manager.
///
/// <b>Networking.</b> Same shape as <see cref="RoundManager"/>: a NetworkSpawn'd singleton (never scene-placed —
/// a scene component's [Sync] CHANGES don't replicate here). Host writes <c>Phase</c>/<c>PhaseEndsAt</c>/
/// <c>Players</c>/<c>MimicId</c>/<c>WordHint</c>; every client reads them (late joiners via the spawn snapshot).
/// Client→host messages: <see cref="ChooseWord"/> — guesses ride the ENGINE chat instead (see below).
///
/// <b>The secret word</b> only ever travels by TARGETED RPC (<see cref="OfferWords"/>/<see cref="TellWord"/>
/// inside <c>Rpc.FilterInclude</c>) — never [Sync], because any client can read synced state and the word IS
/// the game. The synced <see cref="WordHint"/> carries only the masked shape.
///
/// <b>Guessing = s&amp;box's own chat.</b> The engine chat routes every message through the HOST before
/// broadcasting, and fires <see cref="IChatEvent"/> there — so this manager judges chat pre-delivery:
/// a correct guess is SUPPRESSED (the word never goes on the wire) and replaced with the censored
/// "guessed it!" announcement; the mimic and already-correct guessers are muted (with a private shush);
/// everything else passes as ordinary chat and grows a speech bubble at the speaker's mouth.
///
/// <b>Pawns.</b> Everyone is a hunter pawn, spawned and owned by each machine from its own roster row —
/// the simple no-roles version of prop hunt's model, on the wire immediately. Test bots get host-owned bodies
/// (<see cref="RoundBots"/>). The mimic's pawn is TELEPORTED onto the stage for their turn (the stage plinth's
/// colliders keep the guessing crowd from shoving in) and back to their spawn spot after.
///
/// <b>The canvas.</b> A per-turn networked sculpture: the mimic's machine clones the stage's canvas prefab at
/// the canvas spot, owns it (its SdfNetworkSync streams their edits live to everyone), and destroys it when
/// the next turn begins. The mimic edits it through a real <see cref="SculptEditSession"/> on a runtime rig,
/// registered via <see cref="HunterController.ExternalSession"/> (the tutorial NPC's freeze seam) so the pawn
/// stands still while the camera orbits the canvas.
///
/// <b>Stubs / TODO:</b> mimic voice mute during their turn (engine voice is global — the word leak),
/// "you're close" replies.
/// </summary>
[Title( "Charades Manager" )]
[Category( "Mimiclay" )]
[Icon( "theater_comedy" )]
public sealed class CharadesManager : Component, IChatEvent
{
	/// <summary>The active manager in this scene (null elsewhere). The HUD and the spawner read this.</summary>
	public static CharadesManager Current { get; private set; }

	// ── Networked state (host writes, everyone reads — see the class summary) ─────────────────────────────
	[Sync] public CharadesPhase Phase { get; set; } = CharadesPhase.Waiting;

	/// <summary>When the current phase's timer elapses (clock-skew-corrected per client). Waiting isn't timed.</summary>
	[Sync] public TimeUntil PhaseEndsAt { get; set; }

	/// <summary>Per-player game state, keyed by connection id (or bot seat id). Survives across turns.</summary>
	[Sync] public NetDictionary<Guid, CharadesPlayer> Players { get; private set; } = new();

	/// <summary>Whose turn it is to sculpt (<see cref="Guid.Empty"/> outside a turn).</summary>
	[Sync] public Guid MimicId { get; set; }

	/// <summary>The masked word everyone may see ("___ _____"). Only the shape — never the word itself.</summary>
	[Sync] public string WordHint { get; set; } = "";

	/// <summary>The word, revealed to everyone — set only for <see cref="CharadesPhase.TurnReveal"/>.</summary>
	[Sync] public string RevealedWord { get; set; } = "";

	/// <summary>The rules, resolved by the host in OnStart (lobby courier / card override) and synced.</summary>
	[Sync] public CharadesSettings Settings { get; set; } = CharadesSettings.Default;

	// ── Config copied on by RoundManagerSpawner before the NetworkSpawn (host-only fields) ────────────────
	/// <summary>Test bots to seat (the map card's count; a lobby launch's courier count overrides in OnStart).</summary>
	public int BotCount { get; set; }

	/// <summary>Dress bot pawns in random saved heads from the host's sculpt library.</summary>
	public bool BotRandomLooks { get; set; } = true;

	/// <summary>Direct-play rules override from the map card (null = lobby courier / defaults).</summary>
	public CharadesSettings? RulesOverride { get; set; }

	// ── Host-only bookkeeping ─────────────────────────────────────────────────────────────────────────────
	string _currentWord;                                  // the secret — lives on the host and the mimic only
	List<string> _offeredThisTurn = new();                // what the mimic was offered (validates ChooseWord)
	readonly List<Guid> _turnQueue = new();               // take-turns rotation (winner-stays-on's fallback)
	readonly HashSet<string> _usedWords = new();          // no repeats until the pool runs dry
	int _nextSeat;                                        // monotonic join counter → CharadesPlayer.Seat
	int _correctThisTurn;                                 // how many have guessed it (places + mimic score cap)
	Guid _firstCorrectThisTurn;                           // winner-stays-on's next mimic
	bool _gameOver;                                       // someone reached the target — podium after the reveal

	// ── Local (per-machine) state ─────────────────────────────────────────────────────────────────────────
	GameObject _ownPawn;
	bool _ownPawnIsProp;                                  // which prefab our pawn currently is (mimic = prop)
	CharadesPhase _observedPhase = (CharadesPhase)(-1);

	/// <summary>The words offered to THIS machine's player for the current Choosing (empty on everyone else —
	/// they arrive by targeted RPC). The HUD draws them as buttons.</summary>
	public List<string> OfferedWords { get; } = new();

	/// <summary>The word THIS machine's player is sculpting (null on everyone else). Set by targeted RPC when
	/// the turn starts, cleared with the turn.</summary>
	public string LocalWord { get; private set; }

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	/// <summary>True when this machine's player is the current mimic.</summary>
	public bool LocalIsMimic => MimicId != Guid.Empty && Connection.Local?.Id == MimicId;

	/// <summary>The current mimic is a test bot (its body and canvas are the host's to drive).</summary>
	bool MimicIsBot => Players.TryGetValue( MimicId, out var p ) && p.Bot;

	public string MimicName => Players.TryGetValue( MimicId, out var p ) ? p.Name : "";

	/// <summary>Everyone, highest score first (stable within ties by seat) — the HUD scoreboard order.</summary>
	public List<CharadesPlayer> Scoreboard => Players.Values
		.OrderByDescending( p => p.Score ).ThenBy( p => p.Seat ).ToList();

	protected override void OnEnabled()
	{
		Current = this;
		CharadesHud.Ensure( Scene );
	}

	protected override void OnDisabled()
	{
		if ( Current == this )
			Current = null;
	}

	protected override void OnStart()
	{
		if ( !IsHostAuthority )
			return;

		// Lobby-launched: the host's setup-dialog rules ride the courier; the per-key fallback (and the whole
		// lot on a direct Play, where no keys exist) is the card's override or plain defaults.
		Settings = CharadesSettings.ReadFromLobby( RulesOverride ?? CharadesSettings.Default );
		if ( RulesOverride is not null )
			Log.Info( $"CharadesManager: map card rules override active — first to {Settings.TargetScore}, {Settings.Rotation}." );

		// A lobby that seated bots hands its count over (same courier as prop hunt); no key = direct play,
		// the card's count stands.
		if ( int.TryParse( Networking.GetData( RoundManager.BotCountKey ), out var n ) )
			BotCount = Math.Max( 0, n );

		TransitionTo( CharadesPhase.Waiting );
	}

	protected override void OnUpdate()
	{
		// EVERY machine: react once to a synced phase flip (clear turn-local state, enter/leave the editor,
		// move on/off the stage).
		if ( _observedPhase != Phase )
		{
			var from = _observedPhase;
			_observedPhase = Phase;
			ReactToPhase( from, Phase );
		}

		// EVERY machine: our own pawn from our own roster row — the right KIND of pawn (the mimic's machine
		// swaps its hunter for a prop on the stage for the turn, and back after). Polling, same as prop hunt,
		// so [Sync] arrival order can't strand anything.
		EnsureOwnPawn();
		KeepMimicOnStage();
		TickBubbles();

		if ( !IsHostAuthority )
			return;

		ReconcileConnections();
		EnsureBotPawns();
		TickBots();
		TickHostPhase();
	}

	// ── Host: phase ticking ───────────────────────────────────────────────────────────────────────────────
	void TickHostPhase()
	{
		// The mimic vanished mid-turn (left the session) → cut the turn short and move on.
		if ( Phase is CharadesPhase.Choosing or CharadesPhase.Sculpting
			&& MimicId != Guid.Empty && !Players.ContainsKey( MimicId ) )
		{
			Announce( "The mimic left — skipping the turn." );
			AdvanceTurn();
			return;
		}

		// Mid-game player drought → back to Waiting (scores keep; the game resumes when people return).
		if ( Phase is not CharadesPhase.Waiting && Players.Count < Settings.MinPlayers )
		{
			Announce( "Not enough players — waiting for more." );
			TransitionTo( CharadesPhase.Waiting );
			return;
		}

		switch ( Phase )
		{
			case CharadesPhase.Waiting:
				if ( Players.Count >= Settings.MinPlayers )
					StartGame();
				break;

			case CharadesPhase.Starting:
				if ( PhaseEndsAt <= 0f )
					TransitionTo( CharadesPhase.Choosing );
				break;

			case CharadesPhase.Choosing:
				if ( PhaseEndsAt <= 0f )
					HostPickWord( 0 ); // the mimic dithered → the first offer plays
				break;

			case CharadesPhase.Sculpting:
				// Early end when every guesser has it. Solo debug has zero guessers — trivially "everyone",
				// which would skip the phase instantly, so it needs at least one actual correct guess.
				var guessers = Players.Values.Count( p => p.Connection != MimicId );
				var guessed = Players.Values.Count( p => p.Connection != MimicId && p.GuessedPlace > 0 );
				if ( PhaseEndsAt <= 0f || (guessers > 0 && guessed >= guessers) )
					TransitionTo( CharadesPhase.TurnReveal );
				break;

			case CharadesPhase.TurnReveal:
				if ( PhaseEndsAt <= 0f )
					AdvanceTurn();
				break;

			case CharadesPhase.Podium:
				if ( PhaseEndsAt <= 0f )
				{
					// A lobby-launched game hands the group back to the hub to reconfigure + go again; a
					// direct Play (no lobby behind it) loops in-scene forever for solo testing.
					if ( CharadesSettings.CameFromLobby )
					{
						ReturnToLobby();
						break;
					}

					ResetScores();
					TransitionTo( CharadesPhase.Waiting );
				}
				break;
		}
	}

	// Host-only. Fresh game: zero the scores, refill the rotation, then the "get ready" countdown into the
	// first turn.
	void StartGame()
	{
		ResetScores();
		_gameOver = false;
		_usedWords.Clear();
		RefillTurnQueue();
		TransitionTo( CharadesPhase.Starting );
	}

	// Host-only. Someone won → podium; otherwise the next mimic takes the stage.
	void AdvanceTurn()
	{
		if ( _gameOver )
		{
			TransitionTo( CharadesPhase.Podium );
			return;
		}

		TransitionTo( CharadesPhase.Choosing );
	}

	void RefillTurnQueue()
	{
		_turnQueue.Clear();
		_turnQueue.AddRange( Players.Values.OrderBy( p => p.Seat ).Select( p => p.Connection ) );
	}

	void ResetScores()
	{
		foreach ( var id in Players.Keys.ToList() )
		{
			var p = Players[id];
			p.Score = 0;
			p.GuessedPlace = 0;
			Players[id] = p;
		}
	}

	// Host-only: set the phase's timer + entry effects, flipping the synced Phase LAST (a client must never
	// see the new phase with the old phase's timer) — same discipline as RoundManager.TransitionTo.
	void TransitionTo( CharadesPhase next )
	{
		switch ( next )
		{
			case CharadesPhase.Waiting:
				MimicId = Guid.Empty;
				WordHint = "";
				RevealedWord = "";
				_currentWord = null;
				PhaseEndsAt = 0f;
				break;

			case CharadesPhase.Starting:
				MimicId = Guid.Empty;
				WordHint = "";
				RevealedWord = "";
				_currentWord = null;
				PhaseEndsAt = Settings.StartCountdownSeconds;
				break;

			case CharadesPhase.Choosing:
				BeginTurn();
				PhaseEndsAt = Settings.ChooseSeconds;
				// A bot can't dither over a menu — it "picks" almost immediately (a short beat so the
				// "Bot N is choosing" caption is legible before the sculpt opens).
				if ( MimicIsBot )
					PhaseEndsAt = 1.5f;
				break;

			case CharadesPhase.Sculpting:
				// Bot mimics run a shortened clock: the scripted sculpt takes under a minute, and nobody
				// enjoys watching a bot idle out a five-minute timer.
				PhaseEndsAt = MimicIsBot ? MathF.Min( Settings.SculptSeconds, 50f ) : Settings.SculptSeconds;
				break;

			case CharadesPhase.TurnReveal:
				RevealedWord = _currentWord ?? "";
				PhaseEndsAt = Settings.RevealSeconds;
				break;

			case CharadesPhase.Podium:
				MimicId = Guid.Empty;
				WordHint = "";
				RevealedWord = "";
				PhaseEndsAt = Settings.PodiumSeconds;
				break;
		}

		Phase = next;
	}

	// Host-only. Seat the next mimic and offer them their words.
	void BeginTurn()
	{
		WordHint = "";
		RevealedWord = "";
		_currentWord = null;
		_correctThisTurn = 0;

		// Clear the per-turn guess places.
		foreach ( var id in Players.Keys.ToList() )
		{
			var p = Players[id];
			p.GuessedPlace = 0;
			Players[id] = p;
		}

		MimicId = PickNextMimic();
		_firstCorrectThisTurn = Guid.Empty;

		if ( MimicId == Guid.Empty )
			return; // nobody left to sculpt — the drought check will bounce us to Waiting

		_offeredThisTurn = CharadesWords.Draw( 3, Settings.Topics, _usedWords );

		// A bot mimic prepares its scripted turn instead of being offered a choice.
		if ( MimicIsBot )
		{
			PrepareBotMimicTurn();
			return;
		}

		// The offer goes ONLY to the mimic. A broadcast body still runs locally on the host, so the RPC
		// itself re-checks "am I the mimic" — that guard (not the filter) is what keeps a hosting
		// non-mimic's HUD from seeing the words.
		var conn = Connection.All.FirstOrDefault( c => c.Id == MimicId );
		if ( conn is not null && Networking.IsActive )
		{
			using ( Rpc.FilterInclude( conn ) )
				OfferWords( MimicId, _offeredThisTurn[0], _offeredThisTurn[1], _offeredThisTurn[2] );
		}
		else
		{
			OfferWords( MimicId, _offeredThisTurn[0], _offeredThisTurn[1], _offeredThisTurn[2] );
		}
	}

	// Host-only. The next mimic under the configured rotation. Winner-stays-on hands the stage to the turn's
	// first correct guesser; no winner (or a fresh game) falls back to the seat rotation, so the game can
	// never stall on one mimic.
	Guid PickNextMimic()
	{
		if ( Settings.Rotation == MimicRotation.WinnerStaysOn
			&& _firstCorrectThisTurn != Guid.Empty
			&& Players.ContainsKey( _firstCorrectThisTurn ) )
			return _firstCorrectThisTurn;

		// Seat order, skipping leavers; refill when the cycle completes.
		_turnQueue.RemoveAll( id => !Players.ContainsKey( id ) );
		if ( _turnQueue.Count == 0 )
			RefillTurnQueue();

		while ( _turnQueue.Count > 0 )
		{
			var id = _turnQueue[0];
			_turnQueue.RemoveAt( 0 );
			if ( Players.ContainsKey( id ) )
				return id;
		}

		return Guid.Empty;
	}

	// Host-only. Lock the turn's word in and open the sculpt.
	void HostPickWord( int index )
	{
		if ( Phase != CharadesPhase.Choosing || _offeredThisTurn.Count == 0 )
			return;

		// A bot mimic's word was locked in by PrepareBotMimicTurn.
		if ( _currentWord is null )
			_currentWord = _offeredThisTurn[Math.Clamp( index, 0, _offeredThisTurn.Count - 1 )];

		_usedWords.Add( _currentWord );

		// The masked shape for the guessers — only when the host turned hints on: when off it's never
		// written, so nothing about the word's length ever reaches a client (the HUD hides an empty strip).
		WordHint = Settings.WordLengthHints ? CharadesWords.Mask( _currentWord ) : "";

		// Tell the mimic their final word — they need it even when the pick was the timeout's, and this is
		// the one line that makes the auto-pick path identical to the chosen one. (Bots don't need telling.)
		if ( !MimicIsBot )
		{
			var conn = Connection.All.FirstOrDefault( c => c.Id == MimicId );
			if ( conn is not null && Networking.IsActive )
			{
				using ( Rpc.FilterInclude( conn ) )
					TellWord( MimicId, _currentWord );
			}
			else
			{
				TellWord( MimicId, _currentWord );
			}
		}

		TransitionTo( CharadesPhase.Sculpting );
	}

	// ── Word delivery (targeted — see the class summary) ─────────────────────────────────────────────────
	/// <summary>Host→mimic: your three words to pick from. Guarded by recipient check, not just the RPC
	/// filter: a broadcast body always runs on the calling host too.</summary>
	[Rpc.Broadcast]
	void OfferWords( Guid mimic, string a, string b, string c )
	{
		if ( Connection.Local?.Id != mimic )
			return;

		OfferedWords.Clear();
		OfferedWords.AddRange( new[] { a, b, c } );
		LocalWord = null;
	}

	/// <summary>Host→mimic: the word your turn is sculpting (covers the auto-pick timeout too).</summary>
	[Rpc.Broadcast]
	void TellWord( Guid mimic, string word )
	{
		if ( Connection.Local?.Id != mimic )
			return;

		LocalWord = word;
		OfferedWords.Clear();
	}

	/// <summary>Mimic→host: I pick offered word <paramref name="index"/>. The HUD's word buttons call this.</summary>
	[Rpc.Host]
	public void ChooseWord( int index )
	{
		var caller = Networking.IsActive ? Rpc.Caller?.Id : Connection.Local?.Id;
		if ( caller != MimicId )
			return;

		HostPickWord( index );
	}

	// ── Guessing — the engine chat IS the guess box ───────────────────────────────────────────────────────
	// Players type into s&box's own chat (the familiar Enter overlay). Every message is validated on the
	// HOST before the engine broadcasts it — IChatEvent fires inside Chat's OnHostReceive, where Suppress
	// still stops delivery — so a correct guess is censored at the source: the word itself never goes on
	// the wire. Wrong guesses flow through as ordinary chat (near-misses being public is half the game),
	// and every DELIVERED line floats a speech bubble over its speaker on every machine.
	void IChatEvent.OnChatMessage( ChatMessageEvent e )
	{
		if ( e.Sender is null )
			return; // system lines (join/leave, our own announcements) — no verdicts, no bubbles

		// HOST: the verdict, pre-broadcast. Clients never receive a suppressed message, so on their
		// machines this event only ever fires for chat that already passed.
		if ( IsHostAuthority && !e.Suppress )
			JudgeChat( e );

		// EVERY machine, for whatever survived: the bubble at the speaker's mouth.
		if ( !e.Suppress )
			ShowBubble( e.Sender.Id, e.Message );
	}

	// Host-only. The censor + scoring funnel for real players' chat (bots score through the same
	// ScoreCorrectGuess, but their chatter never rides the engine chat — see BotSay).
	void JudgeChat( ChatMessageEvent e )
	{
		var rosterId = e.Sender.Id;
		if ( !Players.TryGetValue( rosterId, out var guesser ) )
			return; // not seated (a mid-join edge) — plain chat

		// The mimic knows the word — nothing they type mid-turn is safe to echo.
		if ( rosterId == MimicId && Phase is CharadesPhase.Choosing or CharadesPhase.Sculpting )
		{
			e.Suppress = true;
			Shush( rosterId, "🤫 No chatting during your own turn!" );
			return;
		}

		// Outside a live sculpt everything is plain chat.
		if ( Phase != CharadesPhase.Sculpting || _currentWord is null )
			return;

		// Already scored this turn: swallowed until the reveal (they know the word — spoilers included),
		// but TOLD so, so a vanishing message doesn't read as broken chat.
		if ( guesser.GuessedPlace > 0 )
		{
			e.Suppress = true;
			Shush( rosterId, "🤫 You've got it — keep it secret until the reveal!" );
			return;
		}

		if ( CharadesWords.Normalize( e.Message ) == CharadesWords.Normalize( _currentWord ) )
		{
			e.Suppress = true; // the answer itself never reaches chat
			ScoreCorrectGuess( rosterId, ref guesser );
		}
	}

	// Host-only. Points by finishing place — faster is worth more: 1st = 3, 2nd = 2, everyone after = 1.
	// The mimic earns +1 per convert (readable sculpts pay), capped at +3 so a big lobby doesn't turn the
	// stage into the only scoring seat. First to Settings.TargetScore ends the game after the reveal.
	void ScoreCorrectGuess( Guid rosterId, ref CharadesPlayer guesser )
	{
		_correctThisTurn++;
		if ( _firstCorrectThisTurn == Guid.Empty )
			_firstCorrectThisTurn = rosterId;

		guesser.GuessedPlace = _correctThisTurn;
		guesser.Score += _correctThisTurn switch { 1 => 3, 2 => 2, _ => 1 };
		Players[rosterId] = guesser;

		if ( _correctThisTurn <= 3 && Players.TryGetValue( MimicId, out var mimic ) )
		{
			mimic.Score += 1;
			Players[MimicId] = mimic;
		}

		AnnounceCorrect( rosterId, guesser.Name );

		if ( Players.Values.Any( p => p.Score >= Settings.TargetScore ) )
			_gameOver = true;
	}

	// ── Chat-line delivery (host → every machine's engine chat; Chat.AddText is local-only by design) ────
	/// <summary>Host→everyone: the censored "got it" — a chat line + a bubble; never the word itself.</summary>
	[Rpc.Broadcast]
	void AnnounceCorrect( Guid rosterId, string name )
	{
		Sandbox.Platform.Chat.AddText( $"⭐ {name} guessed it!" );
		ShowBubble( rosterId, "Got it! ⭐" );
	}

	/// <summary>Host→everyone: a system line into every machine's chat (turn skipped, waiting, …).</summary>
	[Rpc.Broadcast]
	void Announce( string text )
	{
		Sandbox.Platform.Chat.AddText( text );
	}

	/// <summary>Host→everyone: a BOT's chatter — bots have no connection, so their guesses can't ride the
	/// engine chat's client→host path; the host speaks for them into every machine's chat + bubble.</summary>
	[Rpc.Broadcast]
	void BotSay( Guid rosterId, string name, string text )
	{
		Sandbox.Platform.Chat.AddText( $"{name}: {text}" );
		ShowBubble( rosterId, text );
	}

	// Host→one machine: private feedback when their message was swallowed (a silently-vanishing message
	// reads as broken chat). Same targeted-RPC pattern as TellWord: the filter routes it, the recipient
	// guard is what's load-bearing (a broadcast body always runs on the calling host too).
	void Shush( Guid target, string text )
	{
		var conn = Connection.All.FirstOrDefault( c => c.Id == target );
		if ( conn is not null && Networking.IsActive )
		{
			using ( Rpc.FilterInclude( conn ) )
				ShushMsg( target, text );
		}
		else
		{
			ShushMsg( target, text );
		}
	}

	[Rpc.Broadcast]
	void ShushMsg( Guid target, string text )
	{
		if ( Connection.Local?.Id != target )
			return;

		Sandbox.Platform.Chat.AddText( text );
	}

	// ── Speech bubbles (per-machine presentation on top of the feed) ──────────────────────────────────────
	// One SpeechBubble per speaking pawn, created on demand on a runtime anchor above the head and driven
	// through TextOverride (never the serialized Text — the SdfHighlightOutline.Hidden rule). The anchor is
	// parented to the pawn, so it follows them and dies with them.
	readonly Dictionary<Guid, (SpeechBubble Bubble, RealTimeSince Shown)> _bubbles = new();
	const float BubbleSeconds = 4.5f;

	void ShowBubble( Guid rosterId, string text )
	{
		var pawn = FindPawnOf( rosterId );
		if ( !pawn.IsValid() )
			return;

		if ( !_bubbles.TryGetValue( rosterId, out var slot ) || !slot.Bubble.IsValid()
			|| slot.Bubble.GameObject.Parent != pawn )
		{
			var anchor = new GameObject( true, "Charades Bubble" );
			anchor.Flags |= GameObjectFlags.NotSaved; // runtime-only: never serialised into anything
			anchor.SetParent( pawn, false );
			anchor.LocalPosition = Vector3.Up * 72f;  // just above the head — where the tail points

			var bubble = anchor.Components.Create<SpeechBubble>();
			bubble.Text = "";
			bubble.MaxDistance = 0f;   // a charades room is small — bubbles always read
			bubble.TypeVolume = 0.25f; // quieter than the tutorial's narration; many people chat at once
			slot = (bubble, 0f);
		}

		slot.Bubble.TextOverride = text;
		slot.Shown = 0f;
		_bubbles[rosterId] = slot;
	}

	void TickBubbles()
	{
		foreach ( var id in _bubbles.Keys.ToList() )
		{
			var slot = _bubbles[id];
			if ( !slot.Bubble.IsValid() )
			{
				_bubbles.Remove( id );
				continue;
			}

			if ( slot.Shown > BubbleSeconds && !string.IsNullOrEmpty( slot.Bubble.TextOverride ) )
				slot.Bubble.TextOverride = ""; // said nothing again — the bubble pops out
		}
	}

	// The pawn a roster id is wearing on THIS machine: our own, a proxy, or a host-owned bot body —
	// RosterIdOf answers all three (it's the bot-safe ownership resolver). Both controller kinds, since the
	// mimic wears a prop pawn for their turn.
	GameObject FindPawnOf( Guid rosterId )
	{
		foreach ( var hunter in Scene.GetAllComponents<HunterController>() )
		{
			var go = hunter?.GameObject;
			if ( go.IsValid() && RoundManager.RosterIdOf( go ) == rosterId )
				return go;
		}

		foreach ( var hider in Scene.GetAllComponents<HiderController>() )
		{
			var go = hider?.GameObject;
			if ( go.IsValid() && RoundManager.RosterIdOf( go ) == rosterId )
				return go;
		}

		return null;
	}

	// ── Phase reactions (every machine) ───────────────────────────────────────────────────────────────────
	void ReactToPhase( CharadesPhase from, CharadesPhase to )
	{
		// Turn-local secrets don't outlive their phases.
		if ( to is not (CharadesPhase.Choosing or CharadesPhase.Sculpting) )
		{
			OfferedWords.Clear();
			LocalWord = null;
		}

		// A new turn opens and we're the mimic AGAIN (solo debug, or the rotation's fallback re-picked us):
		// the kind-poll in EnsureOwnPawn wouldn't respawn an already-prop pawn, so force it — every turn
		// starts from the blank default blob, never last turn's sculpt.
		if ( to == CharadesPhase.Choosing && LocalIsMimic && _ownPawnIsProp )
			RetireOwnPawn();

		// The sculpt opens: drop the mimic straight into edit mode on their own disguise — the word is locked
		// in, the clock is running, and reaching for Q first would just be a beat of dead air. Q out (to walk
		// the sculpt around, size it up from the crowd's side) and back in stays free for the whole phase —
		// EditLockedFor only bites OUTSIDE Sculpting.
		if ( to == CharadesPhase.Sculpting && LocalIsMimic )
			OwnHider()?.EnterEditing();

		// The sculpt closes (reveal, or the turn was cut short): the editor shuts and the lock comes back
		// down. The prop pawn — the sculpt itself — stays up through the reveal for everyone to admire.
		if ( from == CharadesPhase.Sculpting )
			OwnHider()?.ExitEditing();

		// Pawn KIND changes (mimic ⇄ guesser) are handled by EnsureOwnPawn polling WantsPropPawn — nothing
		// to teleport here: the mimic's prop SPAWNS on the stage and the swap back spawns at the spot.
	}

	/// <summary>True when <paramref name="hider"/>'s sculpt-edit toggle should be refused — charades locks
	/// the mimic's disguise outside the Sculpting phase (no sculpting before the word is chosen, none after
	/// the reveal). HiderController consults this on the Q press. Non-charades scenes are never locked.</summary>
	public static bool EditLockedFor( HiderController hider )
	{
		var m = Current;
		if ( !m.IsValid() || !hider.IsValid() )
			return false;

		// Only the mimic's own prop pawn exists as an editable hider in charades, but scope the check anyway.
		if ( !m._ownPawn.IsValid() || hider.GameObject != m._ownPawn )
			return false;

		return m.Phase != CharadesPhase.Sculpting;
	}

	// Owner-side backstop to the stage fence: the mimic's prop must not leave the stage (the mirror image of
	// the fence keeping the crowd out). The fence colliders are the real wall; this leash only catches what
	// physics lets slip (a solver shove off a big disguise, a gap grazed mid-jump).
	void KeepMimicOnStage()
	{
		if ( !_ownPawnIsProp || !_ownPawn.IsValid() )
			return;

		var stage = CharadesStage.FindIn( Scene );
		if ( !stage.IsValid() )
			return;

		var centre = stage.WorldPosition;
		var offset = _ownPawn.WorldPosition - centre;
		var flat = offset.WithZ( 0f );
		var radius = stage.StageRadius;

		if ( flat.LengthSquared <= radius * radius )
			return;

		_ownPawn.WorldPosition = centre + flat.Normal * radius + Vector3.Up * offset.z;
	}

	// ── Pawns (every machine spawns + owns its own — the simple, no-roles version of prop hunt's model,
	// with one twist: the MIMIC's machine wears a PROP pawn for the whole turn) ───────────────────────────
	// The prop pawn IS the sculpt: the mimic edits their own disguise (the standard hider edit-anytime flow —
	// Q toggles between sculpting and walking/jumping the shape around the stage), its SdfNetworkSync streams
	// every stroke to the guessers, and it spawns at the prefab's default blob — the fresh canvas. Spawned ON
	// the stage; swapped back to a hunter at the spawn ring when the turn is truly over.
	bool WantsPropPawn => LocalIsMimic && Phase is CharadesPhase.Choosing or CharadesPhase.Sculpting or CharadesPhase.TurnReveal;

	void EnsureOwnPawn()
	{
		var me = Connection.Local;
		if ( me is null )
			return;

		if ( !Players.TryGetValue( me.Id, out var info ) )
		{
			RetireOwnPawn(); // we left the roster — drop our pawn
			return;
		}

		var wantProp = WantsPropPawn;
		if ( _ownPawn.IsValid() && _ownPawnIsProp == wantProp )
			return;

		RetireOwnPawn(); // wrong kind (or none) — the swap destroys the old body outright

		var spawner = RoundManagerSpawner.Current;
		var prefab = spawner.IsValid() ? (wantProp ? spawner.PropPrefab : spawner.HunterPrefab) : null;
		if ( !prefab.IsValid() )
			return;

		if ( wantProp )
		{
			// The mimic's turn: a fresh default prop on the stage. No dress — the prefab's default blob IS
			// the blank canvas, so the enabled clone's first build is already the right shape.
			var stage = CharadesStage.FindIn( Scene );
			var at = stage.IsValid() ? stage.MimicTransform : SpotFor( info.SpawnIndex );
			_ownPawn = prefab.Clone( new CloneConfig( at, startEnabled: true, name: $"Charades Mimic {me.DisplayName}" ) );
		}
		else
		{
			// Guessing (or between turns): the usual hunter. Clone DISABLED, dress the saved face, then
			// enable — the ordering that stops the prefab-default face flash everywhere else.
			_ownPawn = prefab.Clone( new CloneConfig( SpotFor( info.SpawnIndex ), startEnabled: false, name: $"Charades Pawn {me.DisplayName}" ) );
			if ( _ownPawn.IsValid() )
			{
				HunterController.WearSavedHead( _ownPawn );
				_ownPawn.Enabled = true;
			}
		}

		if ( !_ownPawn.IsValid() )
			return;

		_ownPawnIsProp = wantProp;

		if ( Networking.IsActive )
		{
			_ownPawn.NetworkSpawn( new NetworkSpawnOptions
			{
				Owner = Connection.Local,
				OrphanedMode = NetworkOrphaned.Destroy,
			} );
		}
	}

	void RetireOwnPawn()
	{
		if ( _ownPawn.IsValid() )
			_ownPawn.Destroy();
		_ownPawn = null;
		_ownPawnIsProp = false;
	}

	HunterController OwnHunter()
		=> _ownPawn.IsValid() ? _ownPawn.Components.Get<HunterController>() : null;

	HiderController OwnHider()
		=> _ownPawn.IsValid() ? _ownPawn.Components.Get<HiderController>() : null;

	Transform SpotFor( int index )
	{
		var spots = RoundSpawnPoint.AllOfKind( Scene, hunterStart: true );
		var origin = spots.Count > 0 ? spots[index % spots.Count].GameObject : GameObject;
		var stack = spots.Count > 0 ? index / spots.Count : index;
		return new Transform(
			origin.WorldPosition + RoundSpawnPoint.StackOffset( stack ) + Vector3.Up * 64f,
			Rotation.FromYaw( origin.WorldRotation.Yaw() ) );
	}

	// ── Test bots (host only) ─────────────────────────────────────────────────────────────────────────────
	// A charades bot is a roster row + a host-owned hunter body. It plays both sides: as a guesser it
	// chatters wrong guesses and (usually) lands the right one after a while; as the mimic it "sculpts" a
	// random shape from the host's sculpt library onto the canvas, brush by brush — and the secret word is
	// that save's NAME, so the full guess loop is testable solo. No saves = a random word and a blob
	// scribble (the turn times out; still walks every phase).
	readonly Dictionary<Guid, GameObject> _botPawns = new();
	readonly HashSet<Guid> _botPropBodies = new();   // which bots currently wear a prop body (the mimic swap)
	readonly HashSet<Guid> _botLooksPending = new();

	sealed class BotGuesser
	{
		public RealTimeUntil NextChatter;
		public RealTimeUntil CorrectAt;
		public bool WillGuess;
		public bool Done;
	}

	sealed class BotMimicPlan
	{
		public List<SdfBrush> BaseBrushes;   // the canvas prefab's authored base, captured at spawn
		public List<SdfBrush> Brushes;       // the shape to build up
		public int Applied;
		public RealTimeUntil NextStroke;
	}

	readonly Dictionary<Guid, BotGuesser> _botGuessers = new();
	readonly BotMimicPlan _botMimic = new();

	// Rows for the configured bots (idempotent), and the right KIND of body for each row — the same
	// mimic-wears-a-prop swap the local player gets, host-owned: a bot whose turn it is stands on the stage
	// as a fresh default prop (its disguise is the canvas the scripted strokes build on), everyone else is a
	// hunter at the spawn ring.
	void EnsureBotPawns()
	{
		for ( var i = 0; i < BotCount; i++ )
		{
			var id = RoundBots.IdFor( i );

			if ( !Players.ContainsKey( id ) )
			{
				Players[id] = new CharadesPlayer
				{
					Connection = id,
					Name = RoundBots.NameFor( i ),
					Score = 0,
					Seat = _nextSeat++,
					SpawnIndex = Players.Count,
					GuessedPlace = 0,
					Bot = true,
				};

				if ( Phase is not CharadesPhase.Waiting )
					_turnQueue.Add( id );
			}

			var wantProp = id == MimicId && Phase is CharadesPhase.Choosing or CharadesPhase.Sculpting or CharadesPhase.TurnReveal;

			if ( _botPawns.TryGetValue( id, out var pawn ) && pawn.IsValid() && _botPropBodies.Contains( id ) == wantProp )
			{
				if ( _botLooksPending.Contains( id ) )
					TryDressBot( id );
				continue;
			}

			if ( pawn.IsValid() )
				pawn.Destroy(); // wrong kind — swap outright, same as the player path

			var spawner = RoundManagerSpawner.Current;
			var prefab = spawner.IsValid() ? (wantProp ? spawner.PropPrefab : spawner.HunterPrefab) : null;
			if ( !prefab.IsValid() )
				return;

			var row = Players[id];
			var stage = CharadesStage.FindIn( Scene );
			var at = wantProp && stage.IsValid() ? stage.MimicTransform : SpotFor( row.SpawnIndex );
			var body = prefab.Clone( new CloneConfig( at, startEnabled: true, name: $"Charades Bot {row.Name}" ) );
			if ( !body.IsValid() )
				continue;

			RoundBots.Prepare( body, id ); // stamp + take the controls away, before it goes on the wire

			if ( Networking.IsActive )
				body.NetworkSpawn();

			_botPawns[id] = body;
			if ( wantProp )
				_botPropBodies.Add( id );
			else
				_botPropBodies.Remove( id );

			if ( !wantProp && BotRandomLooks )
				_botLooksPending.Add( id ); // random faces are hunter dressing — a mimic prop stays the blank blob
		}
	}

	void TryDressBot( Guid id )
	{
		if ( !_botPawns.TryGetValue( id, out var pawn ) || !pawn.IsValid() )
		{
			_botLooksPending.Remove( id );
			return;
		}

		var hunter = pawn.Components.Get<HunterController>();
		if ( !hunter.IsValid() || RoundBots.TryWearRandomSculpt( hunter.Face ) )
			_botLooksPending.Remove( id );
	}

	// The bot mimic's sculpting surface: its prop body's own disguise (resolved by the hider in OnStart, so
	// this can be briefly null right after the swap — callers retry next frame).
	SdfSculpture BotMimicSculpture()
		=> MimicIsBot && _botPawns.TryGetValue( MimicId, out var body ) && body.IsValid()
			? body.Components.Get<HiderController>()?.DisguiseSculpture
			: null;

	// Host-only, per frame. Bot guess chatter during a sculpt, and the scripted mimic strokes.
	void TickBots()
	{
		if ( Phase != CharadesPhase.Sculpting )
			return;

		foreach ( var id in _botGuessers.Keys.ToList() )
		{
			var brain = _botGuessers[id];
			if ( brain.Done || !Players.TryGetValue( id, out var row ) || row.GuessedPlace > 0 )
				continue;

			if ( brain.WillGuess && brain.CorrectAt <= 0f )
			{
				// Straight to the scoring funnel — the censored announcement comes from there, exactly as
				// it would for a person (the word itself is never spoken anywhere).
				if ( _currentWord is not null )
					ScoreCorrectGuess( id, ref row );
				brain.Done = true;
				continue;
			}

			if ( brain.NextChatter <= 0f )
			{
				brain.NextChatter = Random.Shared.Float( 6f, 14f );
				BotSay( id, row.Name, RandomDecoyWord() );
			}
		}

		TickBotMimic();
	}

	string RandomDecoyWord()
	{
		var pool = CharadesWords.PoolFor( Settings.Topics );
		var norm = CharadesWords.Normalize( _currentWord ?? "" );
		var decoys = pool.Where( w => CharadesWords.Normalize( w ) != norm ).ToList();
		return decoys.Count > 0 ? decoys[Random.Shared.Next( decoys.Count )] : "hmm…";
	}

	// Host-only, at turn start (BeginTurn) when the mimic is a bot: pick what it will "sculpt". A random
	// sculpt-library save makes the word its NAME (guessable!); an empty library falls back to a drawn word
	// and a blob scribble nobody can reasonably guess (the turn just times out).
	void PrepareBotMimicTurn()
	{
		_botMimic.Brushes = null;
		_botMimic.Applied = 0;
		_botMimic.BaseBrushes = null;

		// A repeat bot mimic keeps its prop KIND, which the kind-poll wouldn't respawn — force a fresh body
		// so its scripted sculpt builds on a blank blob, not last turn's shape.
		if ( _botPawns.TryGetValue( MimicId, out var previous ) && previous.IsValid() && _botPropBodies.Contains( MimicId ) )
			previous.Destroy();

		var names = SculptLibrary.List();
		if ( names is { Count: > 0 } )
		{
			var entry = SculptLibrary.Load( names[Random.Shared.Next( names.Count )] );
			if ( entry?.Brushes is { Count: > 0 } )
			{
				_currentWord = CharadesWords.Normalize( entry.Name );
				_botMimic.Brushes = entry.Brushes.Select( b => b.Copy() ).ToList();
			}
		}

		if ( _botMimic.Brushes is null )
		{
			_currentWord = _offeredThisTurn[0];
			_botMimic.Brushes = RandomScribble();
		}
	}

	// A handful of random soft blobs around the canvas origin — the "bot with no library" fallback sculpt.
	static List<SdfBrush> RandomScribble()
	{
		var brushes = new List<SdfBrush>();
		var count = Random.Shared.Int( 8, 13 );
		for ( var i = 0; i < count; i++ )
		{
			brushes.Add( new SdfBrush
			{
				Shape = SdfShape.Sphere,
				Position = Vector3.Random * Random.Shared.Float( 4f, 18f ),
				Size = Random.Shared.Float( 4f, 10f ),
				Color = new ColorHsv( Random.Shared.Float( 0f, 360f ), 0.55f, 0.9f ),
			} );
		}
		return brushes;
	}

	// Host-only, per frame during a bot mimic's sculpt: lay the next brush every couple of seconds onto the
	// bot's own prop disguise. Direct Brushes+Rebuild, the same move as RoundBots.TryWearRandomSculpt — the
	// host owns the body, so its SdfNetworkSync streams each step to everyone, and the guessers watch the
	// shape grow.
	void TickBotMimic()
	{
		if ( !MimicIsBot || _botMimic.Brushes is not { Count: > 0 } )
			return;

		var sculpture = BotMimicSculpture();
		if ( !sculpture.IsValid() )
			return;

		// The blank prop the strokes build on — captured on first sight of the resolved disguise (the hider
		// wires it in OnStart, a beat after the body swap).
		_botMimic.BaseBrushes ??= sculpture.Brushes?.Select( b => b.Copy() ).ToList();

		if ( _botMimic.Applied >= _botMimic.Brushes.Count || _botMimic.NextStroke > 0f )
			return;

		_botMimic.Applied++;
		_botMimic.NextStroke = Random.Shared.Float( 1.2f, 2.4f );

		var built = new List<SdfBrush>( _botMimic.BaseBrushes ?? Enumerable.Empty<SdfBrush>() );
		built.AddRange( _botMimic.Brushes.Take( _botMimic.Applied ).Select( b => b.Copy() ) );
		sculpture.Brushes = built;
		sculpture.Rebuild();
	}

	// ── Back to the lobby (host) ──────────────────────────────────────────────────────────────────────────
	// Same shape as RoundManager.ReturnToLobby: the spawner's SceneFile REFERENCE is the reliable path, and a
	// failed resolve pushes the phase timer back so this retries in a few seconds instead of once per frame.
	void ReturnToLobby()
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;

		var options = new SceneLoadOptions();
		var lobby = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.LobbyScene : null;
		var resolved = lobby is not null ? options.SetScene( lobby ) : options.SetScene( LobbyController.LobbyScene );
		if ( !resolved )
		{
			Log.Warning( "CharadesManager: couldn't resolve the lobby scene — retrying in 5s." );
			PhaseEndsAt = 5f;
			return;
		}

		Game.ChangeScene( options );
	}

	// ── Roster upkeep (host) ──────────────────────────────────────────────────────────────────────────────
	// Charades is drop-in/drop-out friendly: joiners get a row (and thus a pawn + guess rights) immediately,
	// and mid-game they're appended to the current rotation so they take the stage too.
	void ReconcileConnections()
	{
		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Players[id].Bot || Connection.All.Any( c => c.Id == id ) )
				continue;

			Players.Remove( id );
			_turnQueue.Remove( id );
			_botGuessers.Remove( id );
		}

		foreach ( var c in Connection.All )
		{
			if ( Players.ContainsKey( c.Id ) )
				continue;

			Players[c.Id] = new CharadesPlayer
			{
				Connection = c.Id,
				Name = c.DisplayName,
				Score = 0,
				Seat = _nextSeat++,
				SpawnIndex = Players.Count,
				GuessedPlace = 0,
			};

			if ( Phase is not CharadesPhase.Waiting )
				_turnQueue.Add( c.Id );
		}

		// Bot guess brains: one per seated bot, re-rolled at each sculpt start (see ReactToPhase — but the
		// roll happens host-side here so a brain always exists by the first TickBots).
		if ( Phase == CharadesPhase.Sculpting )
		{
			foreach ( var row in Players.Values.Where( p => p.Bot && p.Connection != MimicId ) )
			{
				if ( _botGuessers.ContainsKey( row.Connection ) )
					continue;

				_botGuessers[row.Connection] = new BotGuesser
				{
					WillGuess = Random.Shared.Float( 0f, 1f ) < 0.8f,
					CorrectAt = Random.Shared.Float( 0.25f, 0.85f ) * MathF.Max( 10f, PhaseEndsAt ),
					NextChatter = Random.Shared.Float( 3f, 9f ),
				};
			}
		}
		else
		{
			_botGuessers.Clear();
		}
	}
}
