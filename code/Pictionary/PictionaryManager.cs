using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Drives a Pictionary session: the host-authoritative phase machine that walks
/// <see cref="PictionaryPhase.Waiting"/> → (<see cref="PictionaryPhase.Choosing"/> →
/// <see cref="PictionaryPhase.Sculpting"/> → <see cref="PictionaryPhase.TurnReveal"/>) per turn →
/// <see cref="PictionaryPhase.Podium"/> → back to Waiting. One scene for the whole session — no lobby/map
/// split, no scene changes, so the rules ride as a plain [Sync] struct instead of prop hunt's lobby-data courier.
///
/// <b>Networking.</b> Same shape as <see cref="RoundManager"/>: a NetworkSpawn'd singleton (created by
/// <see cref="PictionarySpawner"/> — NOT scene-placed, since a scene component's [Sync] CHANGES don't replicate
/// here). Host writes <c>Phase</c>/<c>PhaseEndsAt</c>/<c>Players</c>/<c>SculptorId</c>/<c>WordHint</c>; every
/// client reads them (late joiners via the spawn snapshot). Client→host messages: <see cref="ChooseWord"/> and
/// <see cref="SubmitGuess"/>.
///
/// <b>The secret word</b> only ever travels by TARGETED RPC (<see cref="OfferWords"/>/<see cref="TellWord"/>
/// inside <c>Rpc.FilterInclude</c>) — never [Sync], because any client can read synced state and the word IS
/// the game. The synced <see cref="WordHint"/> carries only the masked shape.
///
/// <b>Pawns.</b> Everyone is a hunter pawn (crowding around the stage is the point), spawned and owned by
/// each machine from its own roster row — the simple version of prop hunt's model: no roles, no concealment,
/// on the wire immediately. Shooting stays enabled (<see cref="RoundManager.HuntingAllowed"/> is true with no
/// round manager); the canvas protects itself by healing (see the canvas prefab's DamageProfile).
///
/// <b>The canvas.</b> A per-turn networked sculpture: the sculptor's machine clones the canvas prefab at the
/// stage spot, owns it (so its SdfNetworkSync streams their edits live to everyone), and destroys it when the
/// next turn begins. The sculptor's own pawn stands at the stage while their camera enters edit-orbit on the
/// canvas (<see cref="HunterController.BeginExternalEdit"/> — the same pawn-stands/camera-detaches move as
/// lobby face-editing).
///
/// <b>Stubs / TODO:</b> sculptor voice mute during their turn (engine voice is global — the word leak),
/// "you're close" replies, per-scene podium dressing.
/// </summary>
[Title( "Pictionary Manager" )]
[Category( "Mimiclay" )]
[Icon( "draw" )]
public sealed class PictionaryManager : Component
{
	/// <summary>The active manager in this scene (null elsewhere). The HUD and the spawner read this.</summary>
	public static PictionaryManager Current { get; private set; }

	// ── Networked state (host writes, everyone reads — see the class summary) ─────────────────────────────
	[Sync] public PictionaryPhase Phase { get; set; } = PictionaryPhase.Waiting;

	/// <summary>When the current phase's timer elapses (clock-skew-corrected per client). Waiting isn't timed.</summary>
	[Sync] public TimeUntil PhaseEndsAt { get; set; }

	/// <summary>Per-player session state, keyed by connection id. Survives across turns.</summary>
	[Sync] public NetDictionary<Guid, PictionaryPlayer> Players { get; private set; } = new();

	/// <summary>Whose turn it is to sculpt (<see cref="Guid.Empty"/> outside a turn).</summary>
	[Sync] public Guid SculptorId { get; set; }

	/// <summary>The masked word everyone may see ("___ _____"). Only the shape — never the word itself.</summary>
	[Sync] public string WordHint { get; set; } = "";

	/// <summary>The word, revealed to everyone — set only for <see cref="PictionaryPhase.TurnReveal"/>.</summary>
	[Sync] public string RevealedWord { get; set; } = "";

	/// <summary>1-based cycle number ("Round 1 of 2") for the HUD.</summary>
	[Sync] public int RoundNumber { get; set; }

	/// <summary>The rules, seeded from the spawner by the host and synced to everyone.</summary>
	[Sync] public PictionarySettings Settings { get; set; } = PictionarySettings.Default;

	// ── Host-only bookkeeping ─────────────────────────────────────────────────────────────────────────────
	string _currentWord;                                  // the secret — lives on the host and the sculptor only
	List<string> _offeredThisTurn = new();                // what the sculptor was offered (validates ChooseWord)
	readonly List<Guid> _turnQueue = new();               // who still sculpts this cycle, in seat order
	readonly HashSet<string> _usedWords = new();          // no repeats until the pool runs dry
	int _nextSeat;                                        // monotonic join counter → PictionaryPlayer.Seat

	// ── Local (per-machine) state ─────────────────────────────────────────────────────────────────────────
	GameObject _ownPawn;
	GameObject _ownCanvas;                                // only ever set on the sculptor's machine
	RealTimeSince _sinceEditKick;                         // re-enter cooldown after the sculptor exits the editor
	PictionaryPhase _observedPhase = (PictionaryPhase)(-1);

	/// <summary>The words offered to THIS machine's player for the current Choosing (empty on everyone else —
	/// they arrive by targeted RPC). The HUD draws them as buttons.</summary>
	public List<string> OfferedWords { get; } = new();

	/// <summary>The word THIS machine's player is sculpting (null on everyone else). Set by targeted RPC when
	/// the turn starts, cleared with the turn.</summary>
	public string LocalWord { get; private set; }

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	/// <summary>True when this machine's player is the current sculptor.</summary>
	public bool LocalIsSculptor => SculptorId != Guid.Empty && Connection.Local?.Id == SculptorId;

	public string SculptorName => Players.TryGetValue( SculptorId, out var p ) ? p.Name : "";

	/// <summary>Everyone, highest score first (stable within ties by seat) — the HUD scoreboard order.</summary>
	public List<PictionaryPlayer> Scoreboard => Players.Values
		.OrderByDescending( p => p.Score ).ThenBy( p => p.Seat ).ToList();

	// ── Guess/chat feed (local list, filled by broadcast RPC; instance so it dies with the scene) ─────────
	public enum FeedKind { Chat, Correct, System }
	public readonly record struct FeedEntry( string Name, string Text, FeedKind Kind );

	readonly List<FeedEntry> _feed = new();
	public IReadOnlyList<FeedEntry> FeedEntries => _feed;

	/// <summary>Bumped per feed append so the HUD's BuildHash notices new lines.</summary>
	public int FeedVersion { get; private set; }

	protected override void OnEnabled()
	{
		Current = this;
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

		// Lobby-launched: the host's setup-dialog rules ride the courier; per-key fallback (and the whole lot
		// on a direct Play, where no keys exist) is the spawner's scene-authored config.
		var spawner = PictionarySpawner.Current;
		var authored = spawner.IsValid() ? spawner.BuildSettings() : PictionarySettings.Default;
		Settings = PictionarySettings.ReadFromLobby( authored );

		TransitionTo( PictionaryPhase.Waiting );
	}

	protected override void OnUpdate()
	{
		// EVERY machine: react once to a synced phase flip (clear turn-local state, enter/leave the editor).
		if ( _observedPhase != Phase )
		{
			var from = _observedPhase;
			_observedPhase = Phase;
			ReactToPhase( from, Phase );
		}

		// EVERY machine: our own pawn from our own roster row, and — on the sculptor's machine — the canvas
		// and the edit session. Polling, same as prop hunt, so [Sync] arrival order can't strand anything.
		EnsureOwnPawn();
		EnsureCanvas();
		EnsureSculptorEditing();

		if ( !IsHostAuthority )
			return;

		ReconcileConnections();
		TickHostPhase();
	}

	// ── Host: phase ticking ───────────────────────────────────────────────────────────────────────────────
	void TickHostPhase()
	{
		// The sculptor vanished mid-turn (left the session) → cut the turn short and move on.
		if ( Phase is PictionaryPhase.Choosing or PictionaryPhase.Sculpting
			&& SculptorId != Guid.Empty && !Players.ContainsKey( SculptorId ) )
		{
			Feed( "", "The sculptor left — skipping the turn.", FeedKind.System );
			AdvanceTurn();
			return;
		}

		// Mid-game player drought → back to Waiting (scores keep; the game resumes when people return).
		if ( Phase is not PictionaryPhase.Waiting && Players.Count < Settings.MinPlayers )
		{
			Feed( "", "Not enough players — waiting for more.", FeedKind.System );
			TransitionTo( PictionaryPhase.Waiting );
			return;
		}

		switch ( Phase )
		{
			case PictionaryPhase.Waiting:
				if ( Players.Count >= Settings.MinPlayers )
					StartGame();
				break;

			case PictionaryPhase.Choosing:
				if ( PhaseEndsAt <= 0f )
					HostPickWord( 0 ); // sculptor dithered → the first offer plays
				break;

			case PictionaryPhase.Sculpting:
				// Early end when every guesser has it. Solo debug has zero guessers — trivially "everyone",
				// which would skip the phase instantly, so it needs at least one actual correct guess.
				var guessers = Players.Values.Count( p => p.Connection != SculptorId );
				var guessed = Players.Values.Count( p => p.Connection != SculptorId && p.GuessedThisTurn );
				if ( PhaseEndsAt <= 0f || (guessers > 0 && guessed >= guessers) )
					TransitionTo( PictionaryPhase.TurnReveal );
				break;

			case PictionaryPhase.TurnReveal:
				if ( PhaseEndsAt <= 0f )
					AdvanceTurn();
				break;

			case PictionaryPhase.Podium:
				if ( PhaseEndsAt <= 0f )
				{
					// A lobby-launched game hands the group back to the hub to reconfigure + go again; a
					// direct Play (no lobby behind it) loops in-scene forever, sandbox-style.
					if ( PictionarySettings.CameFromLobby )
					{
						ReturnToLobby();
						break;
					}

					ResetScores();
					TransitionTo( PictionaryPhase.Waiting );
				}
				break;
		}
	}

	// Host-only. Fresh game: zero the scores, refill the rotation, first turn.
	void StartGame()
	{
		ResetScores();
		RoundNumber = 1;
		RefillTurnQueue();
		TransitionTo( PictionaryPhase.Choosing );
	}

	// Host-only. Next sculptor, next cycle, or the podium.
	void AdvanceTurn()
	{
		// Drop queued ids that left while waiting their turn.
		_turnQueue.RemoveAll( id => !Players.ContainsKey( id ) );

		if ( _turnQueue.Count == 0 )
		{
			if ( RoundNumber >= Settings.Rounds )
			{
				TransitionTo( PictionaryPhase.Podium );
				return;
			}

			RoundNumber++;
			RefillTurnQueue();
		}

		TransitionTo( PictionaryPhase.Choosing );
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
			p.GuessedThisTurn = false;
			Players[id] = p;
		}
	}

	// Host-only: set the phase's timer + entry effects, flipping the synced Phase LAST (a client must never
	// see the new phase with the old phase's timer) — same discipline as RoundManager.TransitionTo.
	void TransitionTo( PictionaryPhase next )
	{
		switch ( next )
		{
			case PictionaryPhase.Waiting:
				SculptorId = Guid.Empty;
				WordHint = "";
				RevealedWord = "";
				RoundNumber = 0;
				_currentWord = null;
				PhaseEndsAt = 0f;
				break;

			case PictionaryPhase.Choosing:
				BeginTurn();
				PhaseEndsAt = Settings.ChooseSeconds;
				break;

			case PictionaryPhase.Sculpting:
				PhaseEndsAt = Settings.SculptSeconds;
				break;

			case PictionaryPhase.TurnReveal:
				RevealedWord = _currentWord ?? "";
				PhaseEndsAt = Settings.RevealSeconds;
				break;

			case PictionaryPhase.Podium:
				SculptorId = Guid.Empty;
				WordHint = "";
				RevealedWord = "";
				PhaseEndsAt = Settings.PodiumSeconds;
				break;
		}

		Phase = next;
	}

	// Host-only. Seat the next sculptor and offer them their words.
	void BeginTurn()
	{
		WordHint = "";
		RevealedWord = "";
		_currentWord = null;

		// Clear the per-turn guess flags.
		foreach ( var id in Players.Keys.ToList() )
		{
			var p = Players[id];
			p.GuessedThisTurn = false;
			Players[id] = p;
		}

		// Next seated sculptor (AdvanceTurn already pruned leavers; guard anyway).
		do
		{
			SculptorId = _turnQueue.Count > 0 ? _turnQueue[0] : Guid.Empty;
			if ( _turnQueue.Count > 0 )
				_turnQueue.RemoveAt( 0 );
		}
		while ( SculptorId != Guid.Empty && !Players.ContainsKey( SculptorId ) );

		if ( SculptorId == Guid.Empty )
			return; // nobody left to sculpt — the drought check will bounce us to Waiting

		_offeredThisTurn = PictionaryWords.Draw( 3, _usedWords );

		// The offer goes ONLY to the sculptor. A broadcast body still runs locally on the host, so the RPC
		// itself re-checks "am I the sculptor" — that guard (not the filter) is what keeps a hosting
		// non-sculptor's HUD from seeing the words.
		var conn = Connection.All.FirstOrDefault( c => c.Id == SculptorId );
		if ( conn is not null && Networking.IsActive )
		{
			using ( Rpc.FilterInclude( conn ) )
				OfferWords( SculptorId, _offeredThisTurn[0], _offeredThisTurn[1], _offeredThisTurn[2] );
		}
		else
		{
			OfferWords( SculptorId, _offeredThisTurn[0], _offeredThisTurn[1], _offeredThisTurn[2] );
		}
	}

	// Host-only. Lock the turn's word in and open the sculpt.
	void HostPickWord( int index )
	{
		if ( Phase != PictionaryPhase.Choosing || _offeredThisTurn.Count == 0 )
			return;

		_currentWord = _offeredThisTurn[Math.Clamp( index, 0, _offeredThisTurn.Count - 1 )];
		_usedWords.Add( _currentWord );
		WordHint = PictionaryWords.Mask( _currentWord );

		// Tell the sculptor their final word — they need it even when the pick was the timeout's, and this
		// is the one line that makes the auto-pick path identical to the chosen one.
		var conn = Connection.All.FirstOrDefault( c => c.Id == SculptorId );
		if ( conn is not null && Networking.IsActive )
		{
			using ( Rpc.FilterInclude( conn ) )
				TellWord( SculptorId, _currentWord );
		}
		else
		{
			TellWord( SculptorId, _currentWord );
		}

		TransitionTo( PictionaryPhase.Sculpting );
	}

	// ── Word delivery (targeted — see the class summary) ─────────────────────────────────────────────────
	/// <summary>Host→sculptor: your three words to pick from. Guarded by recipient check, not just the RPC
	/// filter: a broadcast body always runs on the calling host too.</summary>
	[Rpc.Broadcast]
	void OfferWords( Guid sculptor, string a, string b, string c )
	{
		if ( Connection.Local?.Id != sculptor )
			return;

		OfferedWords.Clear();
		OfferedWords.AddRange( new[] { a, b, c } );
		LocalWord = null;
	}

	/// <summary>Host→sculptor: the word your turn is sculpting (covers the auto-pick timeout too).</summary>
	[Rpc.Broadcast]
	void TellWord( Guid sculptor, string word )
	{
		if ( Connection.Local?.Id != sculptor )
			return;

		LocalWord = word;
		OfferedWords.Clear();
	}

	/// <summary>Sculptor→host: I pick offered word <paramref name="index"/>. The HUD's word buttons call this.</summary>
	[Rpc.Host]
	public void ChooseWord( int index )
	{
		var caller = Networking.IsActive ? Rpc.Caller?.Id : Connection.Local?.Id;
		if ( caller != SculptorId )
			return;

		HostPickWord( index );
	}

	// ── Guessing ──────────────────────────────────────────────────────────────────────────────────────────
	/// <summary>Anyone→host: a guess (or just chat, outside the sculpt). Correct guesses are validated and
	/// scored here; the guessed word itself is never echoed — the feed shows "got it!" instead.</summary>
	[Rpc.Host]
	public void SubmitGuess( string text )
	{
		text = (text ?? "").Trim();
		if ( text.Length is 0 or > 64 )
			return;

		var callerId = Networking.IsActive ? Rpc.Caller?.Id : Connection.Local?.Id;
		if ( callerId is null || !Players.TryGetValue( callerId.Value, out var guesser ) )
			return;

		// Outside a live sculpt everything is plain chat. So is anything the sculptor types (they know the
		// word — their "guess" would just leak letters people trust).
		var guessing = Phase == PictionaryPhase.Sculpting && callerId != SculptorId && _currentWord is not null;
		if ( !guessing )
		{
			Feed( guesser.Name, text, FeedKind.Chat );
			return;
		}

		if ( guesser.GuessedThisTurn )
			return; // already scored this turn — nothing left to say until the reveal

		if ( PictionaryWords.Normalize( text ) == PictionaryWords.Normalize( _currentWord ) )
		{
			// Guesser: base + up-to-double for speed. Sculptor: a cut per convert.
			guesser.GuessedThisTurn = true;
			guesser.Score += 100 + (int)(100f * MathF.Max( 0f, PhaseEndsAt ) / MathF.Max( 1f, Settings.SculptSeconds ));
			Players[callerId.Value] = guesser;

			if ( Players.TryGetValue( SculptorId, out var sculptor ) )
			{
				sculptor.Score += 40;
				Players[SculptorId] = sculptor;
			}

			Feed( guesser.Name, "got it!", FeedKind.Correct );
		}
		else
		{
			Feed( guesser.Name, text, FeedKind.Chat );
		}
	}

	/// <summary>Host→everyone: append a line to the guess/chat feed.</summary>
	[Rpc.Broadcast]
	void Feed( string name, string text, FeedKind kind )
	{
		_feed.Add( new FeedEntry( name, text, kind ) );
		if ( _feed.Count > 64 )
			_feed.RemoveAt( 0 );
		FeedVersion++;
	}

	// ── Phase reactions (every machine) ───────────────────────────────────────────────────────────────────
	void ReactToPhase( PictionaryPhase from, PictionaryPhase to )
	{
		// Turn-local secrets don't outlive their phases.
		if ( to is not (PictionaryPhase.Choosing or PictionaryPhase.Sculpting) )
		{
			OfferedWords.Clear();
			LocalWord = null;
		}

		// Leaving the sculpt: the sculptor steps out of the editor (the canvas stays up through the reveal).
		if ( from == PictionaryPhase.Sculpting )
			OwnHunter()?.EndExternalEdit();
	}

	// ── Pawns (every machine spawns + owns its own — the simple, no-roles version of prop hunt's model) ───
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

		if ( _ownPawn.IsValid() )
			return;

		var prefab = PictionarySpawner.Current.IsValid() ? PictionarySpawner.Current.PawnPrefab : null;
		if ( !prefab.IsValid() )
			return;

		var at = SpotFor( info.SpawnIndex );

		// Clone DISABLED, dress the saved face, then enable — the same ordering that stops the prefab-default
		// face flash everywhere else (SdfSculpture.OnEnabled fires the first build; it must be the real head).
		_ownPawn = prefab.Clone( new CloneConfig( at, startEnabled: false, name: $"Pawn {me.DisplayName}" ) );
		if ( !_ownPawn.IsValid() )
			return;

		HunterController.WearSavedHead( _ownPawn );
		_ownPawn.Enabled = true;

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
	}

	HunterController OwnHunter()
		=> _ownPawn.IsValid() ? _ownPawn.Components.Get<HunterController>() : null;

	Transform SpotFor( int index )
	{
		var spots = RoundSpawnPoint.AllOfKind( Scene, hunterStart: true );
		var origin = spots.Count > 0 ? spots[index % spots.Count].GameObject : GameObject;
		var stack = spots.Count > 0 ? index / spots.Count : index;
		return new Transform(
			origin.WorldPosition + RoundSpawnPoint.StackOffset( stack ) + Vector3.Up * 64f,
			Rotation.FromYaw( origin.WorldRotation.Yaw() ) );
	}

	// ── The canvas (sculptor's machine only) ──────────────────────────────────────────────────────────────
	// Spawned when our sculpt begins, owned by us (its SdfNetworkSync streams our edits to everyone), kept
	// through the reveal so people can admire it, destroyed when the turn is truly over. Owner-spawned rather
	// than ownership-transferred: clearing between turns comes free with the destroy.
	void EnsureCanvas()
	{
		var want = LocalIsSculptor && Phase is PictionaryPhase.Sculpting or PictionaryPhase.TurnReveal;

		if ( want && !_ownCanvas.IsValid() )
			SpawnCanvas();

		if ( !want && _ownCanvas.IsValid() )
		{
			OwnHunter()?.EndExternalEdit();
			_ownCanvas.Destroy();
			_ownCanvas = null;
		}
	}

	void SpawnCanvas()
	{
		var spawner = PictionarySpawner.Current;
		var prefab = spawner.IsValid() ? spawner.CanvasPrefab : null;
		if ( !prefab.IsValid() )
		{
			Log.Warning( "PictionaryManager: no canvas prefab on the spawner — can't start the sculpt." );
			return;
		}

		var at = spawner.CanvasSpot.IsValid()
			? spawner.CanvasSpot.WorldTransform
			: spawner.WorldTransform;

		_ownCanvas = prefab.Clone( at.WithScale( 1f ) );
		if ( !_ownCanvas.IsValid() )
			return;

		_ownCanvas.Name = "Pictionary Canvas";

		if ( Networking.IsActive )
		{
			_ownCanvas.NetworkSpawn( new NetworkSpawnOptions
			{
				Owner = Connection.Local,
				OrphanedMode = NetworkOrphaned.Destroy,
			} );
		}
	}

	// Keep the sculptor in the editor for the whole sculpt. Re-asserted on a cooldown rather than every frame
	// so the session's own exit paths (Q, the HUD back button) don't get hard-fought — you bounce back in a
	// moment later, because being the sculptor isn't optional, but nothing visibly fights the gesture.
	void EnsureSculptorEditing()
	{
		if ( Phase != PictionaryPhase.Sculpting || !LocalIsSculptor || !_ownCanvas.IsValid() )
			return;

		var hunter = OwnHunter();
		if ( hunter is null || !hunter.IsValid() )
			return;

		if ( hunter.ExternalEditing )
		{
			_sinceEditKick = 0f;
			return;
		}

		if ( _sinceEditKick < 0.75f )
			return;

		var sculpture = _ownCanvas.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndDescendants );
		if ( sculpture.IsValid() )
			hunter.BeginExternalEdit( sculpture );

		_sinceEditKick = 0f;
	}

	// ── Back to the lobby (host) ──────────────────────────────────────────────────────────────────────────
	// Same shape as RoundManager.ReturnToLobby: the spawner's SceneFile REFERENCE is the reliable path, the
	// string lookup only a fallback, and a failed resolve pushes the phase timer back so this retries in a
	// few seconds instead of once per frame forever.
	void ReturnToLobby()
	{
		if ( !Networking.IsHost )
			return;

		var options = new SceneLoadOptions();
		var lobby = PictionarySpawner.Current.IsValid() ? PictionarySpawner.Current.LobbyScene : null;
		var resolved = lobby is not null ? options.SetScene( lobby ) : options.SetScene( LobbyController.LobbyScene );
		if ( !resolved )
		{
			Log.Warning( "PictionaryManager: couldn't resolve the lobby scene — retrying in 5s. Wire LobbyScene on the scene's PictionarySpawner." );
			PhaseEndsAt = 5f;
			return;
		}

		Game.ChangeScene( options );
	}

	// ── Roster upkeep (host) ──────────────────────────────────────────────────────────────────────────────
	// Pictionary is drop-in/drop-out friendly: joiners get a row (and thus a pawn + guess rights) immediately,
	// and mid-game they're appended to the CURRENT cycle's rotation so they sculpt too.
	void ReconcileConnections()
	{
		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Connection.All.Any( c => c.Id == id ) )
				continue;

			Players.Remove( id );
			_turnQueue.Remove( id );
		}

		foreach ( var c in Connection.All )
		{
			if ( Players.ContainsKey( c.Id ) )
				continue;

			Players[c.Id] = new PictionaryPlayer
			{
				Connection = c.Id,
				Name = c.DisplayName,
				Score = 0,
				Seat = _nextSeat++,
				SpawnIndex = Players.Count,
				GuessedThisTurn = false,
			};

			if ( Phase is not PictionaryPhase.Waiting )
				_turnQueue.Add( c.Id );
		}
	}
}
