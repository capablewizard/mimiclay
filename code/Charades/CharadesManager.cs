using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Drives a Charades game: the host-authoritative phase machine that walks
/// <see cref="CharadesPhase.Waiting"/> → (<see cref="CharadesPhase.Choosing"/> →
/// <see cref="CharadesPhase.Sculpting"/> → <see cref="CharadesPhase.TurnReveal"/>) per turn, one turn each
/// per ROUND (players-write rounds open with a <see cref="CharadesPhase.Writing"/> beat where everyone types a
/// phrase for someone else), for the configured number of rounds → <see cref="CharadesPhase.Podium"/> → back
/// to the lobby (a direct Play, which has no lobby behind it, loops to Waiting instead). Runs on a picked
/// charades MAP — one with a
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
public sealed class CharadesManager : Component, IChatEvent, IPropClaimHost, Component.INetworkListener
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

	/// <summary>Players-write rounds: who WROTE the phrase being sculpted (<see cref="Guid.Empty"/> when it
	/// came from a topic list). They know the answer, so they sit the turn out — and get the ✏️ badge.</summary>
	[Sync] public Guid AuthorId { get; set; }

	/// <summary>The round in progress, 1-based (0 before the first). Everyone seated takes one turn per round;
	/// after <see cref="CharadesSettings.Rounds"/> of them the podium shows.</summary>
	[Sync] public int Round { get; set; }

	/// <summary>The masked word everyone may see ("___ _____"). Only the shape — never the word itself.</summary>
	[Sync] public string WordHint { get; set; } = "";

	/// <summary>The word, revealed to everyone — set only for <see cref="CharadesPhase.TurnReveal"/>.</summary>
	[Sync] public string RevealedWord { get; set; } = "";

	/// <summary>The sculptor hint that went with <see cref="RevealedWord"/> ("" when the phrase had none) — shown
	/// under the answer at the reveal, now that it can't give anything away.</summary>
	[Sync] public string RevealedHint { get; set; } = "";

	/// <summary>The rules, resolved by the host in OnStart (lobby courier / card override) and synced.</summary>
	[Sync] public CharadesSettings Settings { get; set; } = CharadesSettings.Default;

	/// <summary>The ticked Community list's title in a Topics game ("" otherwise) — the HUD's top-left theme
	/// heading. Host-resolved and synced: guests needn't have the list installed.</summary>
	[Sync] public string ThemeTitle { get; set; } = "";

	/// <summary>True while the first game on this map waits for every machine to finish loading (see
	/// <see cref="LoadGate"/>); Waiting holds until it clears. Counts are for the HUD.</summary>
	[Sync] public bool AwaitingPlayers { get; set; }
	[Sync] public int LoadedPlayers { get; set; }
	[Sync] public int ExpectedPlayers { get; set; }

	/// <summary>Host-computed: every connection has finished loading the scene (or someone has been stuck
	/// loading for so long we stop waiting). Gates every pawn publish — see <see cref="MayPublishPawns"/>.</summary>
	[Sync] public bool AllPlayersLoaded { get; set; }

	// ── Config copied on by RoundManagerSpawner before the NetworkSpawn (host-only fields) ────────────────
	/// <summary>Test bots to seat (the map card's count; a lobby launch's courier count overrides in OnStart).</summary>
	public int BotCount { get; set; }

	/// <summary>Dress bot pawns in random saved heads from the host's sculpt library.</summary>
	public bool BotRandomLooks { get; set; } = true;

	/// <summary>Direct-play rules override from the map card (null = lobby courier / defaults).</summary>
	public CharadesSettings? RulesOverride { get; set; }

	// ── Host-only bookkeeping ─────────────────────────────────────────────────────────────────────────────
	string _currentWord;                                  // the secret — lives on the host and the mimic only
	string _currentHint = "";                             // its bracketed sculptor hint ("" = none)
	List<string> _offeredThisTurn = new();                // what the mimic was offered (validates ChooseWord)
	readonly List<Guid> _turnQueue = new();               // who still has to take the stage this round (seat order)
	readonly HashSet<string> _usedWords = new();          // no repeats until the pool runs dry
	LoadGate _loadGate;                                   // holds the first StartGame until everyone's loaded
	int _nextSeat;                                        // monotonic join counter → CharadesPlayer.Seat
	int _correctThisTurn;                                 // how many have guessed it (places + mimic score cap)

	// Players-write rounds: what each player typed this round, and who sculpts whose. Host-only — a phrase
	// is a secret until its reveal, so none of this is ever synced.
	readonly Dictionary<Guid, string> _submissions = new();
	readonly Dictionary<Guid, (string Phrase, Guid Author)> _assigned = new();

	readonly Dictionary<Guid, GameObject> _borrowed = new(); // who's wearing which claimed map prop (see Borrowed props)

	// ── Local (per-machine) state ─────────────────────────────────────────────────────────────────────────
	GameObject _ownPawn;
	bool _ownPawnIsProp;                                  // which prefab our pawn currently is (mimic = prop)
	bool _ownPawnBorrowed;                                // _ownPawn is a CLAIMED map prop, not a body we spawned
	Transform? _hunterSpawnAt;                            // the next hunter spawns here (clear of a prop we let go)
	CharadesPhase _observedPhase = (CharadesPhase)(-1);

	// ── Network heal state (see "Pawn-presence heal" + NetStateResync) ────────────────────────────────────
	RealTimeSince _someoneLoadingFor = 0f;                                    // host: how long a connection has been inactive (seeded: a default reads as since-engine-start → gate never held)
	bool _republishPending;                                              // a machine asked us to republish our pawn
	RealTimeUntil _republishCooldown;                                    // collapse request storms into one respawn
	RealTimeUntil _botRepublishCooldown;
	RealTimeUntil _nextPresenceScan;
	readonly Dictionary<Guid, RealTimeSince> _missingFor = new();       // rosterId → how long its pawn's been absent here
	readonly Dictionary<Guid, RealTimeUntil> _requestBackoff = new();   // rosterId → wait before re-asking
	readonly Dictionary<Guid, RealTimeSince> _staleFor = new();         // pawn id → how long the roster has disowned it
	readonly Dictionary<Guid, Guid> _pawnIdScratch = new();
	readonly HashSet<Guid> _presentScratch = new();
	readonly Dictionary<Guid, List<SdfBrush>> _botRedress = new();      // host: disguise to put back on a republished bot
	readonly NetStateResync.RowWatch _rosterWatch = new();              // client: "my roster copy is incomplete"

	/// <summary>The words offered to THIS machine's player for the current Choosing (empty on everyone else —
	/// they arrive by targeted RPC). The HUD draws them as buttons.</summary>
	public List<string> OfferedWords { get; } = new();

	/// <summary>The word THIS machine's player is sculpting (null on everyone else). Set by targeted RPC when
	/// the turn starts, cleared with the turn.</summary>
	public string LocalWord { get; private set; }

	/// <summary>The word THIS machine's player just guessed correctly (null until they do). Set by targeted RPC on
	/// their correct guess so the HUD can type it into the word box; cleared when the sculpt ends.</summary>
	public string SolvedWord { get; private set; }

	/// <summary>The sculptor hint behind <see cref="SolvedWord"/> ("" when there was none) — shown under the
	/// answer once it has typed itself out.</summary>
	public string SolvedHint { get; private set; } = "";

	/// <summary>Players-write rounds: the phrase THIS machine's player handed in this round (null until they do).
	/// Echoed back by targeted RPC so the HUD can show it on the word card; cleared when Writing ends.</summary>
	public string LocalSubmission { get; private set; }

	/// <summary>
	/// True while THIS machine's player is the mimic choosing between offered phrases (more than one — a single
	/// offer is a read-and-go, not a pick). While it's on, the HUD's centre buttons own the screen: the cursor
	/// is forced visible (PauseMenuSystem) and the prop's movement + camera hold still (HiderController), until
	/// the pick lands (TellWord clears the offers). Static so the controllers can ask without a manager lookup.
	/// </summary>
	public static bool LocalPicking => Current.IsValid() && Current.Phase == CharadesPhase.Choosing
		&& Current.LocalIsMimic && Current.OfferedWords.Count > 1;

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	/// <summary>The selection every draw uses: in a Topics game the ticked Standard topics plus the ticked
	/// Community lists; in write-your-own games, where the pool is only filler for late joiners, non-writers and
	/// bots, every built-in topic.</summary>
	string PoolTopics => Settings.Source == PhraseSource.Topics
		? CharadesTopics.Combined( Settings.Topics, Settings.WorkshopLists )
		: CharadesTopics.Everything;

	/// <summary>How many phrases the mimic is offered in a Topics game: three, or one when Topic Choices is off
	/// (same read-it-and-go card as write-your-own).</summary>
	/// <summary>How long a single-phrase turn gives the mimic to read it before the sculpt opens (the HUD's clock
	/// counts it down 5-4-3-2-1).</summary>
	const float ReadSeconds = 5f;

	int OfferCount => Settings.Source != PhraseSource.Players && !Settings.TopicChoices ? 1 : 3;

	/// <summary>True when this machine's player is the current mimic.</summary>
	public bool LocalIsMimic => MimicId != Guid.Empty && Connection.Local?.Id == MimicId;

	/// <summary>The current mimic is a test bot (its body and canvas are the host's to drive).</summary>
	bool MimicIsBot => Players.TryGetValue( MimicId, out var p ) && p.Bot;

	public string MimicName => Players.TryGetValue( MimicId, out var p ) ? p.Name : "";

	/// <summary>The mimic's prop pawn on this machine while they're on stage, else null. Guns trace straight
	/// through it (HunterController.ShotTrace) — the sculptor's work is not a target, and the stage fence no
	/// longer stops shots on the way in.</summary>
	public GameObject ShotProofPawn
	{
		get
		{
			if ( MimicId == Guid.Empty )
				return null;

			var pawn = FindPawnOf( MimicId );
			return pawn.IsValid() && pawn.Components.Get<HiderController>().IsValid() ? pawn : null;
		}
	}

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

	// Host: fetch any selected Community list this machine lacks, then publish its title for the HUD.
	async Task ResolveCommunityLists()
	{
		var selection = PoolTopics;
		await CharadesWorkshop.EnsureSelected( selection );
		if ( !this.IsValid() )
			return;

		var titles = CharadesTopics.Parse( selection )
			.Select( id => CharadesTopics.TryWorkshopId( id, out var fid ) && CharadesWorkshop.TryGet( fid, out var list ) ? list.Title : null )
			.Where( t => !string.IsNullOrWhiteSpace( t ) )
			.OrderBy( t => t, StringComparer.OrdinalIgnoreCase );
		ThemeTitle = string.Join( " + ", titles );
	}

	protected override void OnStart()
	{
		if ( !IsHostAuthority )
			return;

		// Lobby-launched: the host's setup-dialog rules ride the courier; the per-key fallback (and the whole
		// lot on a direct Play, where no keys exist) is the card's override or plain defaults.
		Settings = CharadesSettings.ReadFromLobby( RulesOverride ?? CharadesSettings.Default );
		if ( RulesOverride is not null )
			Log.Info( $"CharadesManager: map card rules override active — {Settings.Rounds} round(s), phrases from {Settings.Source}." );

		// Community lists in the selection are normally cached from the lobby; fetch any that aren't before
		// the first draw needs them (fire-and-forget — PoolFor falls back to the built-ins meanwhile).
		_ = ResolveCommunityLists();

		// A lobby that seated bots hands its count over (same courier as prop hunt); no key = direct play,
		// the card's count stands.
		if ( int.TryParse( Networking.GetData( RoundManager.BotCountKey ), out var n ) )
			BotCount = Math.Max( 0, n );

		_loadGate = new LoadGate( GameObject );
		AwaitingPlayers = true;
		TransitionTo( CharadesPhase.Waiting );
	}

	// Host-only. False until everyone has loaded the map (or the gate timed out). Only the first game on the
	// map waits: once open it stays open, so a mid-game drought back to Waiting resumes on player count alone.
	bool EveryoneLoaded()
	{
		if ( !AwaitingPlayers )
			return true;

		var open = _loadGate?.Tick() ?? true;
		LoadedPlayers = _loadGate?.Loaded ?? 0;
		ExpectedPlayers = _loadGate?.Expected ?? 0;
		if ( open )
			AwaitingPlayers = false;
		return open;
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
		HandleBorrowedInput();
		EnsureOwnPawn();
		KeepMimicOnStage();
		TickBubbles();
		ReconcilePawnPresence();
		WatchRoster();

		if ( !IsHostAuthority )
			return;

		ReconcileConnections();
		EnsureBotPawns();
		StampPawnIds();
		TickPublishGate();

		// Returning to the lobby: the game is frozen — the bots stop guessing/chattering and the phase machine
		// stands still. (The roster/pawn upkeep above keeps running so leavers and heals stay correct.)
		if ( LobbyReturn.Active )
			return;

		TickBots();
		TickHostPhase();
	}

	// ── Network listener (host): a connection finished loading the scene ──────────────────────────────────
	// Everything reliable the host sent this machine while it was mid-load — roster row adds included — was
	// dropped by the engine; re-send the whole manager now that it can receive. See NetStateResync.
	void Component.INetworkListener.OnActive( Connection channel ) => NetStateResync.OnPlayerActive( this, channel );

	/// <summary>Client → host: my copy of the roster is incomplete — re-send the manager's state.</summary>
	[Rpc.Host]
	void RequestStateResync()
		=> NetStateResync.Refresh( this, $"{Rpc.Caller?.DisplayName ?? "a client"} reports an incomplete roster" );

	// Every machine: in charades every connection has a roster row (ReconcileConnections seats joiners at once),
	// so a client whose roster has fewer non-bot rows than there are connections — or no row of its OWN — is
	// provably missing dropped adds. Ask for a re-send after a grace, with backoff.
	void WatchRoster()
	{
		if ( !Networking.IsActive || IsHostAuthority || Connection.Local is not { } me )
			return;

		var rowsComplete = Players.ContainsKey( me.Id ) && Players.Values.Count( p => !p.Bot ) >= Connection.All.Count();

		// Pawn-id stamps are container deltas too, so the same mid-load window that drops a row add drops the
		// stamp — and a row whose PawnId reads empty here is invisible to ReconcilePawnPresence (it can't ask for a
		// pawn it doesn't know should exist). Once the publish gate is open every machine publishes its pawn
		// within a frame and the host stamps it the next, so an empty stamp past the watch's grace is a dropped
		// delta, not a pawn that hasn't spawned yet. Gated on AllPlayersLoaded so a legitimately held pawn (gate
		// closed for a joiner) never counts.
		var stampsComplete = !AllPlayersLoaded || Players.Values.All( p => p.PawnId != Guid.Empty );

		if ( _rosterWatch.Tick( rowsComplete && stampsComplete ) )
		{
			var why = !rowsComplete
				? $"{Players.Count} rows, {Connection.All.Count()} connections, own row {(Players.ContainsKey( me.Id ) ? "present" : "MISSING")}"
				: $"{Players.Values.Count( p => p.PawnId == Guid.Empty )} row(s) with no pawn id stamped";
			Log.Info( $"CharadesManager: roster incomplete here ({why}) — asking the host to re-send." );
			RequestStateResync();
		}
	}

	// ── Host: phase ticking ───────────────────────────────────────────────────────────────────────────────
	void TickHostPhase()
	{
		// Returning to the lobby (the podium ran out, or the host ended it from the pause menu): the game is
		// FROZEN for the countdown — no turns, no transitions, no drought check. LobbyReturn changes the scene.
		if ( LobbyReturn.Active )
			return;

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
				if ( EveryoneLoaded() && Players.Count >= Settings.MinPlayers )
					StartGame();
				break;

			case CharadesPhase.Starting:
				if ( PhaseEndsAt <= 0f )
					BeginRound();
				break;

			case CharadesPhase.Writing:
				// Everyone (with a keyboard — bots submit on entry) has handed one in, or time's up.
				if ( PhaseEndsAt <= 0f || Players.Values.All( p => p.Bot || p.Submitted ) )
				{
					AssignPhrases();
					TransitionTo( CharadesPhase.Choosing );
				}
				break;

			case CharadesPhase.Choosing:
				if ( PhaseEndsAt <= 0f )
					HostPickWord( 0 ); // the mimic dithered → the first offer plays
				break;

			case CharadesPhase.Sculpting:
				// Early end when every guesser has it. Solo debug has zero guessers — trivially "everyone",
				// which would skip the phase instantly, so it needs at least one actual correct guess. The
				// phrase's author isn't a guesser — they wrote it.
				var guessers = Players.Values.Count( p => p.Connection != MimicId && p.Connection != AuthorId );
				var guessed = Players.Values.Count( p => p.Connection != MimicId && p.Connection != AuthorId && p.GuessedPlace > 0 );
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

	// Host-only. Fresh game: zero the scores, then the "get ready" countdown into round one.
	void StartGame()
	{
		ResetScores();
		_usedWords.Clear();
		Round = 0;
		TransitionTo( CharadesPhase.Starting );
	}

	// Host-only. Open the next round: everyone seated right now gets a turn, in seat order. Players-write
	// rounds collect the phrases first; topic rounds go straight to the first mimic.
	void BeginRound()
	{
		Round++;
		RefillTurnQueue();
		_assigned.Clear();
		_submissions.Clear();

		if ( Settings.Source == PhraseSource.Players )
		{
			TransitionTo( CharadesPhase.Writing );
			return;
		}

		TransitionTo( CharadesPhase.Choosing );
	}

	// Host-only. The turn is over: the next mimic takes the stage, or — the round's queue is spent — the
	// next round opens, or the last one just ended and the podium shows.
	void AdvanceTurn()
	{
		_turnQueue.RemoveAll( id => !Players.ContainsKey( id ) );
		if ( _turnQueue.Count > 0 )
		{
			TransitionTo( CharadesPhase.Choosing );
			return;
		}

		if ( Round >= Settings.Rounds )
		{
			TransitionTo( CharadesPhase.Podium );
			return;
		}

		BeginRound();
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
			p.Submitted = false;
			Players[id] = p;
		}
	}

	// ── Players-write rounds (host) ───────────────────────────────────────────────────────────────────────
	// Everyone types a phrase during Writing (through the chat, which the host swallows — see JudgeChat); at
	// the end each phrase is handed to someone ELSE on the stage queue. Bots "write" a topic-list phrase so
	// they're authors too; anyone without a phrase written for them draws a topic-list filler at their turn.

	// Host-only, on entering Writing: clear last round's hand-ins, and let the bots submit at once.
	void OpenWriting()
	{
		_submissions.Clear();
		foreach ( var id in Players.Keys.ToList() )
		{
			var p = Players[id];
			p.Submitted = false;
			if ( p.Bot )
			{
				var filler = CharadesWords.Draw( 1, PoolTopics, _usedWords );
				if ( filler.Count > 0 )
				{
					_submissions[id] = filler[0];
					p.Submitted = true;
				}
			}
			Players[id] = p;
		}
	}

	// Host-only: a player's phrase for this round (typed in chat during Writing). Re-typing replaces it.
	void AcceptSubmission( Guid rosterId, string text )
	{
		var phrase = CharadesWords.SanitizePhrase( text );
		if ( phrase is null )
		{
			Shush( rosterId, "✏️ That's a bit short — type a phrase with a couple of letters in it." );
			return;
		}

		_submissions[rosterId] = phrase;
		if ( Players.TryGetValue( rosterId, out var p ) )
		{
			p.Submitted = true;
			Players[rosterId] = p;
		}

		// Echo the answer and, when they bracketed one, the hint the sculptor will get alongside it.
		var (answer, hint) = CharadesWords.Split( phrase );
		Shush( rosterId, hint.Length > 0
			? $"✏️ Got it: “{answer}” (sculptor hint: {hint}) — type again to change it."
			: $"✏️ Got it: “{phrase}” — type again to change it." );

		// And privately back to their HUD, which shows it on the word card while everyone writes.
		var conn = Connection.All.FirstOrDefault( c => c.Id == rosterId );
		if ( conn is not null && Networking.IsActive )
		{
			using ( Rpc.FilterInclude( conn ) )
				TellSubmission( rosterId, phrase );
		}
		else
		{
			TellSubmission( rosterId, phrase );
		}
	}

	/// <summary>Host→writer: the phrase you handed in (sanitised), for your HUD's word card. Same targeted
	/// pattern as <see cref="TellWord"/> — a phrase is a secret until its reveal.</summary>
	[Rpc.Broadcast]
	void TellSubmission( Guid writer, string phrase )
	{
		if ( Connection.Local?.Id != writer )
			return;

		LocalSubmission = phrase;
	}

	// Host-only, at the end of Writing: deal the phrases out so nobody sculpts their own. The submitters
	// are lined up in seat order and each phrase goes to the submitter a fixed (random) number of places
	// along — a cyclic shift, which is a perfect derangement for any two or more. A lone submitter's phrase
	// goes to someone who didn't write (or is dropped, solo). Non-submitters draw fillers at their turn.
	void AssignPhrases()
	{
		_assigned.Clear();

		var authors = Players.Values
			.Where( p => _submissions.ContainsKey( p.Connection ) )
			.OrderBy( p => p.Seat )
			.Select( p => p.Connection )
			.ToList();

		if ( authors.Count >= 2 )
		{
			var shift = Random.Shared.Int( 1, authors.Count - 1 );
			for ( var i = 0; i < authors.Count; i++ )
			{
				var author = authors[i];
				var sculptor = authors[(i + shift) % authors.Count];
				_assigned[sculptor] = (_submissions[author], author);
			}
		}
		else if ( authors.Count == 1 )
		{
			var author = authors[0];
			var someoneElse = Players.Values.Where( p => p.Connection != author ).Select( p => p.Connection ).ToList();
			if ( someoneElse.Count > 0 )
				_assigned[someoneElse[Random.Shared.Next( someoneElse.Count )]] = (_submissions[author], author);
		}

		_submissions.Clear();
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
				RevealedWord = ""; RevealedHint = "";
				_currentWord = null; _currentHint = "";
				PhaseEndsAt = 0f;
				break;

			case CharadesPhase.Starting:
				MimicId = Guid.Empty;
				AuthorId = Guid.Empty;
				WordHint = "";
				RevealedWord = ""; RevealedHint = "";
				_currentWord = null; _currentHint = "";
				PhaseEndsAt = Settings.StartCountdownSeconds;
				break;

			case CharadesPhase.Writing:
				MimicId = Guid.Empty;
				AuthorId = Guid.Empty;
				WordHint = "";
				RevealedWord = ""; RevealedHint = "";
				_currentWord = null; _currentHint = "";
				OpenWriting();
				PhaseEndsAt = Settings.WriteSeconds;
				break;

			case CharadesPhase.Choosing:
				BeginTurn();
				// One phrase (players-write, or Topic Choices off) is nothing to choose — the beat is just the
				// mimic READING their phrase and getting ready, then the sculpt opens on its own (the timeout's
				// HostPickWord( 0 )). Several get the full pick window.
				PhaseEndsAt = _offeredThisTurn.Count == 1 ? ReadSeconds : Settings.ChooseSeconds;
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
				RevealedHint = _currentHint;
				PhaseEndsAt = Settings.RevealSeconds;
				break;

			case CharadesPhase.Podium:
				MimicId = Guid.Empty;
				AuthorId = Guid.Empty;
				WordHint = "";
				RevealedWord = ""; RevealedHint = "";
				PhaseEndsAt = Settings.PodiumSeconds;
				break;
		}

		Phase = next;
	}

	// Host-only. Seat the next mimic and offer them their words.
	void BeginTurn()
	{
		WordHint = "";
		RevealedWord = ""; RevealedHint = "";
		_currentWord = null; _currentHint = "";
		_correctThisTurn = 0;

		// Clear the per-turn guess places.
		foreach ( var id in Players.Keys.ToList() )
		{
			var p = Players[id];
			p.GuessedPlace = 0;
			Players[id] = p;
		}

		MimicId = PickNextMimic();
		AuthorId = Guid.Empty;

		if ( MimicId == Guid.Empty )
			return; // nobody left to sculpt — the drought check will bounce us to Waiting

		// Topic rounds offer three to pick from. Players-write rounds hand over the ONE phrase written for
		// this mimic (a filler from the topic lists when nobody wrote for them — a late joiner, or a round
		// where too few people typed) — the Choosing beat is just them reading it.
		if ( Settings.Source == PhraseSource.Players )
		{
			if ( _assigned.Remove( MimicId, out var dealt ) )
			{
				_offeredThisTurn = new List<string> { dealt.Phrase };
				AuthorId = Players.ContainsKey( dealt.Author ) ? dealt.Author : Guid.Empty;
			}
			else
			{
				_offeredThisTurn = CharadesWords.Draw( 1, PoolTopics, _usedWords );
			}
		}
		else
		{
			_offeredThisTurn = CharadesWords.Draw( OfferCount, PoolTopics, _usedWords );
		}

		// A bot mimic prepares its scripted turn instead of being offered a choice.
		if ( MimicIsBot )
		{
			PrepareBotMimicTurn();
			return;
		}

		// The offer goes ONLY to the mimic. A broadcast body still runs locally on the host, so the RPC
		// itself re-checks "am I the mimic" — that guard (not the filter) is what keeps a hosting
		// non-mimic's HUD from seeing the words.
		var packed = string.Join( '\n', _offeredThisTurn );
		var conn = Connection.All.FirstOrDefault( c => c.Id == MimicId );
		if ( conn is not null && Networking.IsActive )
		{
			using ( Rpc.FilterInclude( conn ) )
				OfferWords( MimicId, packed );
		}
		else
		{
			OfferWords( MimicId, packed );
		}
	}

	// Host-only. The next mimic: the round's queue in seat order, skipping leavers. Empty = the round is
	// over (AdvanceTurn opens the next one or the podium) — this never refills by itself.
	Guid PickNextMimic()
	{
		_turnQueue.RemoveAll( id => !Players.ContainsKey( id ) );

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

		// The offered line, sculptor hint included ("Steve (Minecraft)") — what the mimic is told and what the
		// pool remembers as used. The secret the guessers chase is its answer alone. A bot mimic's word was
		// locked in by PrepareBotMimicTurn.
		var phrase = _currentWord;
		if ( phrase is null )
		{
			phrase = _offeredThisTurn[Math.Clamp( index, 0, _offeredThisTurn.Count - 1 )];
			(_currentWord, _currentHint) = CharadesWords.Split( phrase );
		}

		_usedWords.Add( phrase );

		// The masked shape for the guessers — only when the host turned hints on: when off it's never
		// written, so nothing about the word's length ever reaches a client (the HUD hides an empty strip).
		WordHint = Settings.WordLengthHints ? CharadesWords.Mask( _currentWord ) : "";

		// Tell the mimic their final phrase (hint and all) — they need it even when the pick was the timeout's,
		// and this is the one line that makes the auto-pick path identical to the chosen one. (Bots don't need
		// telling.)
		if ( !MimicIsBot )
		{
			var conn = Connection.All.FirstOrDefault( c => c.Id == MimicId );
			if ( conn is not null && Networking.IsActive )
			{
				using ( Rpc.FilterInclude( conn ) )
					TellWord( MimicId, phrase );
			}
			else
			{
				TellWord( MimicId, phrase );
			}
		}

		TransitionTo( CharadesPhase.Sculpting );
	}

	// ── Word delivery (targeted — see the class summary) ─────────────────────────────────────────────────
	/// <summary>Host→mimic: your phrases to pick from (three from the topics, or the one written for you),
	/// newline-packed. Guarded by recipient check, not just the RPC filter: a broadcast body always runs on
	/// the calling host too.</summary>
	[Rpc.Broadcast]
	void OfferWords( Guid mimic, string packed )
	{
		if ( Connection.Local?.Id != mimic )
			return;

		OfferedWords.Clear();
		OfferedWords.AddRange( (packed ?? "").Split( '\n', StringSplitOptions.RemoveEmptyEntries ) );
		LocalWord = null;
	}

	/// <summary>Host→mimic: the phrase your turn is sculpting, sculptor hint included (covers the auto-pick
	/// timeout too).</summary>
	[Rpc.Broadcast]
	void TellWord( Guid mimic, string word )
	{
		if ( Connection.Local?.Id != mimic )
			return;

		LocalWord = word;
		OfferedWords.Clear();
	}

	/// <summary>Host→guesser: you got it — here's the word, for your HUD to type into the word box. Same
	/// targeted pattern as <see cref="TellWord"/> (the recipient guard is the load-bearing part).</summary>
	[Rpc.Broadcast]
	void TellSolved( Guid guesser, string word, string hint )
	{
		if ( Connection.Local?.Id != guesser )
			return;

		SolvedWord = word;
		SolvedHint = hint ?? "";
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
	/// <summary>Log every step of the chat→guess funnel (host-side verdicts included, so it PRINTS THE
	/// SECRET WORD on the host — debugging only). Console: <c>charades_debug_chat true</c>.</summary>
	[ConVar( "charades_debug_chat" )]
	public static bool DebugChat { get; set; }

	void IChatEvent.OnChatMessage( ChatMessageEvent e )
	{
		if ( DebugChat )
			Log.Info( $"[charades chat] sender={e.Sender?.DisplayName ?? "<system>"} msg='{e.Message}' hostAuth={IsHostAuthority} phase={Phase} suppressed={e.Suppress}" );

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
		// Returning to the lobby: the game is frozen, so nothing scores or gets swallowed — every line is
		// plain chat (the word was revealed at the last TurnReveal anyway; the podium has no secret).
		if ( LobbyReturn.Active )
			return;

		var rosterId = e.Sender.Id;
		if ( !Players.TryGetValue( rosterId, out var guesser ) )
		{
			if ( DebugChat )
				Log.Info( $"[charades judge] sender {rosterId} has NO roster row ({Players.Count} rows) — plain chat" );
			return; // not seated (a mid-join edge) — plain chat
		}

		if ( DebugChat )
			Log.Info( $"[charades judge] roster ok, phase={Phase}, word='{_currentWord ?? "<null>"}', place={guesser.GuessedPlace}, norm-guess='{CharadesWords.Normalize( e.Message )}' vs norm-word='{CharadesWords.Normalize( _currentWord ?? "" )}'" );

		// Writing: the chat box IS the hand-in slot. Swallowed at the source — the phrase is a secret until
		// its reveal — and acknowledged privately so the vanishing line reads as "received", not broken.
		if ( Phase == CharadesPhase.Writing )
		{
			e.Suppress = true;
			AcceptSubmission( rosterId, e.Message );
			return;
		}

		// The mimic knows the word — nothing they type mid-turn is safe to echo.
		if ( rosterId == MimicId && Phase is CharadesPhase.Choosing or CharadesPhase.Sculpting )
		{
			e.Suppress = true;
			Shush( rosterId, "🤫 No chatting during your own turn!" );
			return;
		}

		// So does whoever wrote it.
		if ( rosterId == AuthorId && AuthorId != Guid.Empty && Phase is CharadesPhase.Choosing or CharadesPhase.Sculpting )
		{
			e.Suppress = true;
			Shush( rosterId, "✏️ You wrote this one — sit back and enjoy the show!" );
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

		if ( CharadesWords.Matches( e.Message, _currentWord ) )
		{
			e.Suppress = true; // the answer itself never reaches chat
			ScoreCorrectGuess( rosterId, ref guesser );
		}
	}

	// Host-only. Points by finishing place — faster is worth more: 1st = 3, 2nd = 2, everyone after = 1.
	// The mimic earns +1 per convert (readable sculpts pay), capped at +3 so a big lobby doesn't turn the
	// stage into the only scoring seat. A players-write phrase's author gets +1 the first time it's guessed
	// — a sculptable phrase pays, an impossible one doesn't. The game ends when the rounds run out.
	void ScoreCorrectGuess( Guid rosterId, ref CharadesPlayer guesser )
	{
		_correctThisTurn++;

		guesser.GuessedPlace = _correctThisTurn;
		var points = _correctThisTurn switch { 1 => 3, 2 => 2, _ => 1 };
		guesser.Score += points;
		Players[rosterId] = guesser;

		var mimicPoints = 0;
		if ( _correctThisTurn <= 3 && Players.TryGetValue( MimicId, out var mimic ) )
		{
			mimicPoints = 1;
			mimic.Score += mimicPoints;
			Players[MimicId] = mimic;
		}

		if ( _correctThisTurn == 1 && AuthorId != Guid.Empty && AuthorId != MimicId && Players.TryGetValue( AuthorId, out var author ) )
		{
			author.Score += 1;
			Players[AuthorId] = author;
		}

		AnnounceCorrect( rosterId, guesser.Name, points, MimicId, mimicPoints );

		// The guesser's HUD types the word into their box — filtered to THEIR connection like TellWord, so the
		// answer never goes on the wire to anyone still guessing. Bots have no machine to tell.
		if ( !guesser.Bot )
		{
			var conn = Connection.All.FirstOrDefault( c => c.Id == rosterId );
			if ( conn is not null && Networking.IsActive )
			{
				using ( Rpc.FilterInclude( conn ) )
					TellSolved( rosterId, _currentWord, _currentHint );
			}
			else
			{
				TellSolved( rosterId, _currentWord, _currentHint );
			}
		}
	}

	// ── Chat-line delivery (host → every machine's engine chat; Chat.AddText is local-only by design) ────
	/// <summary>Host→everyone: the censored "got it" — a chat line + a bubble; never the word itself.</summary>
	[Rpc.Broadcast]
	void AnnounceCorrect( Guid rosterId, string name, int points, Guid mimicId, int mimicPoints )
	{
		Sandbox.Platform.Chat.AddText( $"⭐ {name} guessed it!" );
		ShowBubble( rosterId, "Got it! ⭐" );

		// The success sting + the guesser's scoreboard head springs and jitters, a green +points beside it
		// (every machine runs this RPC, so the points ride along rather than waiting on the Players sync).
		UiSounds.PlayEvent( UiSounds.Success );
		foreach ( var hud in Scene.GetAllComponents<CharadesHud>() )
		{
			hud.Celebrate( rosterId, points );
			if ( mimicPoints > 0 )
				hud.AwardPoints( mimicId, mimicPoints ); // the sculptor's +1 for a readable sculpt — a pop, no head celebration
		}
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

		// A guesser's solved word is only theirs for the rest of the sculpt (the reveal shows everyone anyway).
		if ( to != CharadesPhase.Sculpting )
		{
			SolvedWord = null;
			SolvedHint = "";
		}

		// Your hand-in shows only while everyone's writing (it becomes someone else's secret after).
		if ( to != CharadesPhase.Writing )
			LocalSubmission = null;

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
		// A guesser sculpting a borrowed map prop isn't on the clock — leave them be.
		if ( from == CharadesPhase.Sculpting && !_ownPawnBorrowed )
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

		// Only the mimic's prop is on the clock — a guesser's borrowed map prop edits freely.
		if ( !m._ownPawn.IsValid() || hider.GameObject != m._ownPawn || m._ownPawnBorrowed )
			return false;

		return m.Phase != CharadesPhase.Sculpting;
	}

	// Owner-side: the mimic's prop must not leave the stage. The fence ring is crowd-only (Collision.config
	// pairs its "stagefence" tag with "propbody" as Ignore) — a prop pawn's collider is its live sculpt, and a
	// sculpt that grew across a wall was stuck there, straddling it, while a position snap back fought the
	// solver every tick. So the prop is kept on by HiderController's soft leash instead, live-set here from
	// the stage so a re-tuned radius just follows: movement keeps the collider's footprint inside the
	// circle, and BrushWorldClamp reads the same circle to stop clay being sculpted past it — a wide
	// sculpt simply has less room to walk, and nothing overhangs.
	void KeepMimicOnStage()
	{
		if ( !_ownPawnIsProp || !_ownPawn.IsValid() )
			return;

		var hider = OwnHider();
		if ( !hider.IsValid() )
			return;

		var stage = CharadesStage.FindIn( Scene );
		if ( !stage.IsValid() )
		{
			hider.LeashCentre = null;
			return;
		}

		hider.LeashCentre = stage.WorldPosition;
		hider.LeashRadius = stage.StageRadius;
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

		// A claim is in flight: the host destroys our hunter and hands us the prop (AdoptBorrowed) — respawning
		// a hunter into that gap would leave us with two bodies.
		if ( !_ownPawn.IsValid() && PropClaims.LocalClaimPending )
			return;

		if ( _ownPawnBorrowed )
		{
			// Wearing the map's clay as a guesser: that's our body until we let go (E or R) or get picked.
			if ( _ownPawn.IsValid() && !wantProp )
				return;

			// Picked as the mimic (or the prop died under us): drop it where it stands — it goes back to being
			// claimable scenery — and fall through to spawn the mimic's blank prop on the stage.
			LetGoOfBorrowed( stepClear: false );
		}

		if ( _ownPawn.IsValid() && _ownPawnIsProp == wantProp )
		{
			// Right kind already. A machine that never received our pawn's create asked us to republish
			// (see ReconcilePawnPresence) — respawn-in-place under a fresh network identity.
			if ( _republishPending )
			{
				_republishPending = false;
				RepublishOwnPawn();
				return;
			}

			// Spawned while the publish gate held (someone was still loading) → go on the wire as-is.
			if ( MayPublishPawns )
				PublishOwnPawn();
			return;
		}

		RetireOwnPawn(); // wrong kind (or none) — the swap destroys the old body outright

		Transform spot;
		if ( wantProp )
		{
			var stage = CharadesStage.FindIn( Scene );
			spot = stage.IsValid() ? stage.MimicTransform : SpotFor( info.SpawnIndex );
		}
		else
		{
			spot = _hunterSpawnAt ?? SpotFor( info.SpawnIndex );
		}

		SpawnOwnPawn( wantProp, spot );
	}

	// Clone our pawn of the given kind at `at`, dress it, enable it and (gate permitting) publish it. `disguise`
	// carries a mimic prop's sculpted shape onto the fresh clone — the republish path; a normal mimic spawn is
	// the prefab's default blob, which IS the blank canvas. Dressed pawns clone DISABLED, dress, then enable: an
	// enabled clone has the DEFAULT shape's build in flight before any post-clone dress, and that build landing
	// first is the prefab-default flash (and what the spawn snapshot would ship).
	void SpawnOwnPawn( bool wantProp, Transform at, List<SdfBrush> disguise = null )
	{
		var me = Connection.Local;
		var spawner = RoundManagerSpawner.Current;
		var prefab = spawner.IsValid() ? (wantProp ? spawner.PropPrefab : spawner.HunterPrefab) : null;
		if ( me is null || !prefab.IsValid() )
			return;

		var dress = !wantProp || disguise is { Count: > 0 };
		var name = wantProp ? $"Charades Mimic {me.DisplayName}" : $"Charades Pawn {me.DisplayName}";
		_ownPawn = prefab.Clone( new CloneConfig( at, startEnabled: !dress, name: name ) );
		if ( !_ownPawn.IsValid() )
			return;

		if ( dress )
		{
			if ( wantProp )
				HiderController.WearDisguise( _ownPawn, disguise );
			else
				HunterController.WearSavedHead( _ownPawn );
			_ownPawn.Enabled = true;
		}

		_ownPawnIsProp = wantProp;
		_hunterSpawnAt = null; // one-shot: only the spawn right after letting go of a prop

		if ( MayPublishPawns )
			PublishOwnPawn();
	}

	/// <summary>Gate on putting ANY pawn on the wire: hold until every connection has finished loading the
	/// scene. The engine DROPS object create/destroy messages that arrive while a machine is mid-scene-load,
	/// and the sender marks them delivered — so a pawn published into that window never exists on the loading
	/// machine. Holding the publish closes the window in the common case (a mid-game joiner re-closes it for
	/// NEW spawns while they load); <see cref="ReconcilePawnPresence"/> heals whatever still slips through. The
	/// pawn exists and plays locally meanwhile — the mimic's canvas just reaches the guessers a beat later.</summary>
	bool MayPublishPawns => !Networking.IsActive || AllPlayersLoaded;

	// Host-only. Someone stuck loading forever must not hold everyone's spawns hostage: the gate opens on its
	// own after a generous wait, same spirit as LoadGate's timeout.
	const float PublishGateTimeout = 20f;

	void TickPublishGate()
	{
		if ( !Networking.IsActive )
		{
			AllPlayersLoaded = true;
			return;
		}

		var everyoneActive = Connection.All.All( c => c.IsActive );
		if ( everyoneActive )
			_someoneLoadingFor = 0f;

		AllPlayersLoaded = everyoneActive || _someoneLoadingFor > PublishGateTimeout;
	}

	// Put our local pawn on the network, owned by us. Orphaned → Destroy so a disconnect cleanly removes it for
	// everyone. No-op offline, for a borrowed prop (the host networked it), or if it's already on the wire.
	void PublishOwnPawn()
	{
		if ( !Networking.IsActive || _ownPawnBorrowed || !_ownPawn.IsValid() || _ownPawn.Network.Active )
			return;

		_ownPawn.NetworkSpawn( new NetworkSpawnOptions
		{
			Owner = Connection.Local,
			OrphanedMode = NetworkOrphaned.Destroy,
		} );
	}

	// A machine told us it has no copy of our pawn. The engine sends an object's create exactly once per
	// connection and drops it if it lands mid-scene-load, so the ONLY way to reach that machine again is a
	// fresh network identity: respawn-in-place, carrying the sculpt. Machines that had us see a one-frame swap;
	// the machine that didn't finally gets a body. Rate-limited so a storm of requests collapses into one. The
	// mimic mid-sculpt re-enters the editor on the fresh canvas (undo history is lost — better than a turn
	// nobody can see).
	void RepublishOwnPawn()
	{
		if ( _republishCooldown > 0f || !_ownPawn.IsValid() || _ownPawnBorrowed )
			return;
		_republishCooldown = 5f;

		var at = _ownPawn.WorldTransform;
		var wasProp = _ownPawnIsProp;
		var hider = OwnHider();
		var editing = hider.IsValid() && hider.EditMode;
		var disguise = wasProp && hider.IsValid() && hider.DisguiseSculpture.IsValid()
			? hider.DisguiseSculpture.Brushes?.Select( b => b.Copy() ).ToList()
			: null;

		Log.Info( $"CharadesManager: republishing our {(wasProp ? "mimic prop" : "pawn")} (a machine was missing it)." );

		if ( editing )
			hider.ExitEditing();

		RetireOwnPawn();
		SpawnOwnPawn( wasProp, at, disguise );
		_reenterEditing = editing && Phase == CharadesPhase.Sculpting && LocalIsMimic;
	}

	bool _reenterEditing; // after a mimic republish: resume sculpting once the fresh canvas has resolved

	// ── Pawn-presence heal (the charades port of RoundManager's) ─────────────────────────────────────────
	// The HOST (which misses nothing — every message routes through it) stamps each row's live pawn object id
	// into the roster; every CLIENT compares that against the pawns it actually holds — asking owners to
	// republish what's missing, and dropping local copies the roster says were superseded.

	// Host-only. Written only on change, so a stable game networks nothing.
	void StampPawnIds()
	{
		_pawnIdScratch.Clear();
		foreach ( var h in Scene.GetAllComponents<HunterController>() )
			MapPawn( h?.GameObject );
		foreach ( var h in Scene.GetAllComponents<HiderController>() )
			MapPawn( h?.GameObject );

		foreach ( var key in Players.Keys.ToList() )
		{
			var row = Players[key];
			var pawnId = _pawnIdScratch.GetValueOrDefault( key );
			if ( row.PawnId == pawnId )
				continue;

			row.PawnId = pawnId;
			Players[key] = row;
		}

		void MapPawn( GameObject go )
		{
			if ( !go.IsValid() || !go.Network.Active )
				return;

			// Released map props belong to nobody (their release dropped ownership) — rightly ignored.
			var id = RoundManager.RosterIdOf( go );
			if ( id is not null )
				_pawnIdScratch[id.Value] = go.Id;
		}
	}

	// Clients only (the host's scene IS the truth the roster is stamped from).
	void ReconcilePawnPresence()
	{
		// After a mimic republish (host or client — the host owns a pawn too): back into the editor once the
		// fresh prop has resolved its canvas.
		if ( _reenterEditing && OwnHider() is { } own && own.DisguiseSculpture.IsValid() )
		{
			_reenterEditing = false;
			own.EnterEditing();
		}

		if ( !Networking.IsActive || IsHostAuthority )
			return;

		if ( _nextPresenceScan > 0f )
			return;
		_nextPresenceScan = 0.5f;

		var me = Connection.Local?.Id;

		_presentScratch.Clear();
		foreach ( var h in Scene.GetAllComponents<HunterController>() )
			Collect( h?.GameObject );
		foreach ( var h in Scene.GetAllComponents<HiderController>() )
			Collect( h?.GameObject );

		// MISSING: the roster names a pawn object we don't hold. Grace first — the roster delta can outrun the
		// create by a beat — then ask the owner to republish, with a per-row backoff.
		foreach ( var id in Players.Keys.ToList() )
		{
			var row = Players[id];

			if ( row.PawnId == Guid.Empty || id == me || _presentScratch.Contains( row.PawnId ) )
			{
				_missingFor.Remove( id );
				continue;
			}

			if ( !_missingFor.ContainsKey( id ) )
			{
				_missingFor[id] = 0f;
				continue;
			}

			if ( _missingFor[id] < 3f )
				continue;

			if ( _requestBackoff.TryGetValue( id, out var wait ) && wait > 0f )
				continue;

			_requestBackoff[id] = 8f;
			Log.Info( $"CharadesManager: missing {row.Name}'s pawn (dropped while loading?) — requesting a republish." );
			RequestPawnRepublish( id );
		}

		// GHOSTS: a networked pawn whose row now names a DIFFERENT object — the host explicitly saying "that one
		// is gone" (its destroy dropped while we loaded). Needs its own grace: a fresh pawn's create can land
		// before the roster re-stamps its id. Never our own body, never a rowless pawn (a released map prop has
		// no row and is legitimately there).
		foreach ( var h in Scene.GetAllComponents<HunterController>().ToList() )
			Sweep( h?.GameObject );
		foreach ( var h in Scene.GetAllComponents<HiderController>().ToList() )
			Sweep( h?.GameObject );

		void Collect( GameObject go )
		{
			if ( go.IsValid() && go.Network.Active )
				_presentScratch.Add( go.Id );
		}

		void Sweep( GameObject go )
		{
			if ( !go.IsValid() || !go.Network.Active )
				return;

			var owner = RoundManager.RosterIdOf( go );
			if ( owner is null || owner == me || !Players.TryGetValue( owner.Value, out var row ) )
				return;

			if ( row.PawnId != Guid.Empty && row.PawnId != go.Id )
			{
				if ( !_staleFor.ContainsKey( go.Id ) )
				{
					_staleFor[go.Id] = 0f;
				}
				else if ( _staleFor[go.Id] > 3f )
				{
					_staleFor.Remove( go.Id );
					Log.Info( $"CharadesManager: dropping a stale copy of {row.Name}'s pawn (superseded by a republish)." );
					go.Destroy();
				}
			}
			else
			{
				_staleFor.Remove( go.Id );
			}
		}
	}

	/// <summary>Any machine → everyone: "I have no copy of this player's pawn — republish it." The OWNER
	/// respawns-in-place under a fresh network identity; the HOST does the same for a bot's body. Broadcast
	/// because machine→machine needs a shared networked object to ride — this manager is it.</summary>
	[Rpc.Broadcast]
	void RequestPawnRepublish( Guid rosterId )
	{
		if ( Connection.Local is { } local && local.Id == rosterId )
			_republishPending = true; // handled in EnsureOwnPawn

		if ( IsHostAuthority && Players.TryGetValue( rosterId, out var row ) && row.Bot )
			RepublishBotPawn( rosterId, row );
	}

	// Host-only: destroy the bot's body and let EnsureBotPawns mint a fresh one next frame, dressed back in
	// its sculpt (a mimic bot's scripted strokes keep building on it).
	void RepublishBotPawn( Guid id, CharadesPlayer row )
	{
		if ( _botRepublishCooldown > 0f )
			return;

		var pawn = _botPawns.GetValueOrDefault( id );
		if ( !pawn.IsValid() || !pawn.Network.Active )
			return;

		_botRepublishCooldown = 5f;

		var hider = pawn.Components.Get<HiderController>();
		if ( hider.IsValid() && hider.DisguiseSculpture.IsValid() && hider.DisguiseSculpture.Brushes is { Count: > 0 } brushes )
			_botRedress[id] = brushes.Select( b => b.Copy() ).ToList();

		Log.Info( $"CharadesManager: republishing bot pawn '{row.Name}' (a machine was missing it)." );
		pawn.Destroy();
		_botPawns.Remove( id );
	}

	void RetireOwnPawn()
	{
		// A borrowed map prop is never destroyed — it's let go back into the world.
		if ( _ownPawnBorrowed )
			LetGoOfBorrowed( stepClear: false );

		if ( _ownPawn.IsValid() )
			_ownPawn.Destroy();
		_ownPawn = null;
		_ownPawnIsProp = false;
	}

	// ── Borrowed props: guessers possessing the map's clay (the PropClaims spawned beside us) ─────────────
	// Any time, any phase: a guesser aims at zoo clay, E Edit, and wears it — the standard claim flow (host
	// arbitration, scene-prop conversion). The one charades twist is pawn OWNERSHIP: here every machine spawns
	// its own pawn (EnsureOwnPawn), so the host's grant has to tell the claimant "this prop is your body now"
	// (AdoptBorrowed), or the kind-poll would respawn a hunter the moment the host destroyed ours. Letting go —
	// E/R, being picked as the mimic, leaving — RELEASES the prop where it stands (claimable again) rather than
	// destroying it: it's map furniture.

	bool IPropClaimHost.ClaimsAllowed => true; // the mimic is a prop already, so only guessers ever hover clay

	GameObject IPropClaimHost.PropPrefab
		=> RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.PropPrefab : null;

	// Host-side: charades keeps no pawn bookkeeping for others (each machine owns its own), so find the caller's
	// hunter by network owner. Bots' hunters are host-owned and never claim.
	GameObject IPropClaimHost.ClaimantPawn( Connection c )
	{
		if ( !Players.ContainsKey( c.Id ) )
			return null;

		foreach ( var hunter in Scene.GetAllComponents<HunterController>() )
		{
			if ( !hunter.Bot && hunter.Network.Owner?.Id == c.Id )
				return hunter.GameObject;
		}
		return null;
	}

	void IPropClaimHost.OnClaimGranted( Connection c, GameObject hunterPawn, HiderController prop )
	{
		_borrowed[c.Id] = prop.GameObject;

		// Before PropClaims destroys the hunter, so the claimant usually knows its new body first (the pending
		// latch covers the other order).
		using ( Rpc.FilterInclude( c ) )
		{
			AdoptBorrowed( prop.GameObject );
		}
	}

	/// <summary>Host → claimant: the prop you claimed is your body now (your hunter is being destroyed).</summary>
	[Rpc.Broadcast]
	void AdoptBorrowed( GameObject pawn )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;

		// LocalClaimPending is deliberately NOT cleared here — HiderController.ResumeControl clears it once the
		// prop is actually ours to drive. Clearing it now let PawnSwapKeys.LeavePressed read the claiming E press
		// again later this same frame on a listen host and pop us straight back out. The kind-poll's gate on it
		// only matters while _ownPawn is invalid, and it's set right below.
		if ( !pawn.IsValid() )
			return;

		_ownPawn = pawn; // the old hunter is already on its way out (host-side destroy) — just forget it
		_ownPawnIsProp = false;
		_ownPawnBorrowed = true;
	}

	// E or R lets go of a borrowed prop — the lobby/creative keys (PawnSwapKeys, which stands down while the edit row
	// would use them, and while typing: guesses go through chat here). Charades has no prop role to swap INTO, so R on a hunter
	// does nothing.
	void HandleBorrowedInput()
	{
		PawnSwapKeys.Tick();
		if ( !_ownPawnBorrowed || !_ownPawn.IsValid() )
			return;

		// Mid-edit, the session exits through its own gate first (PawnSwapKeys.Run). The borrow may have ended
		// while the revert dialog was up (picked as mimic), so re-check before letting go.
		if ( PawnSwapKeys.SwapPressed || PawnSwapKeys.LeavePressed( OwnHider() ) )
			PawnSwapKeys.Run( () =>
			{
				if ( this.IsValid() && _ownPawnBorrowed )
					LetGoOfBorrowed( stepClear: true );
			} );
	}

	// Local: stop wearing the borrowed prop. It's silenced here at once (the host's release drops our ownership a
	// beat later), the host releases it into the world, and EnsureOwnPawn brings back our hunter — stepped clear of
	// the prop's hull when we let go by choice (spawning inside its collider gets solver-shoved), or the mimic body
	// on the stage when it's our turn.
	void LetGoOfBorrowed( bool stepClear )
	{
		if ( !_ownPawnBorrowed )
			return;

		var hider = OwnHider();
		if ( hider.IsValid() )
		{
			var claims = Components.Get<PropClaims>();
			if ( stepClear && claims.IsValid() )
			{
				var yaw = Scene.Camera.IsValid() ? Scene.Camera.WorldRotation.Yaw() : hider.WorldRotation.Yaw();
				_hunterSpawnAt = claims.HunterSpotClearOf( hider, yaw );
			}
			hider.ReleaseControl();
		}

		_ownPawn = null;
		_ownPawnIsProp = false;
		_ownPawnBorrowed = false;
		RequestReleaseBorrowed();
	}

	/// <summary>Claimant → host: release the prop I'm wearing back into the world.</summary>
	[Rpc.Host]
	void RequestReleaseBorrowed()
	{
		var id = Rpc.Caller?.Id ?? Connection.Local?.Id;
		if ( id is { } who )
			ReleaseBorrowedFor( who, pop: true );
	}

	// Host-only. The _borrowed map is the authority on who wears what, so a caller can only release their own.
	// pop = the player chose to leave (or was picked as mimic) — the swap pop plays; a leaver's tidy-up is silent.
	void ReleaseBorrowedFor( Guid id, bool pop = false )
	{
		if ( !_borrowed.Remove( id, out var pawn ) || !pawn.IsValid() )
			return;

		var hider = pawn.Components.Get<HiderController>();
		var claims = Components.Get<PropClaims>();
		if ( hider.IsValid() && claims.IsValid() )
		{
			if ( pop )
				claims.PlaySwapPop( PropClaims.PopSpot( pawn ) );
			claims.Release( hider );
		}
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

				// Spawned while the publish gate was closed → put it on the wire now that everyone has loaded.
				if ( Networking.IsActive && MayPublishPawns && !pawn.Network.Active )
					pawn.NetworkSpawn();
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

			// A republished bot gets its sculpt back (see RepublishBotPawn) — before the first bake, like a player.
			if ( _botRedress.Remove( id, out var redress ) && redress is { Count: > 0 } )
				HiderController.WearDisguise( body, redress );

			if ( Networking.IsActive && MayPublishPawns )
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
		var pool = CharadesWords.PoolFor( PoolTopics );
		var norm = CharadesWords.Normalize( _currentWord ?? "" );
		var decoys = pool.Select( CharadesWords.Answer ).Where( w => CharadesWords.Normalize( w ) != norm ).ToList();
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
			_currentWord = CharadesWords.Answer( _offeredThisTurn[0] );
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
	// Same shape as RoundManager.ReturnToLobby: arm the shared "Returning to Lobby" countdown (LobbyReturn,
	// riding this manager's GameObject) — it freezes the game for five seconds, then changes scene for everyone.
	// No instance to arm (a misconfigured debug scene) pushes the phase timer back so this retries in a few
	// seconds instead of once per frame.
	void ReturnToLobby()
	{
		if ( LobbyReturn.Begin( "game over" ) )
			return;

		Log.Warning( "CharadesManager: no LobbyReturn to arm — is this manager spawner-made? Retrying in 5s." );
		PhaseEndsAt = 5f;
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
			_submissions.Remove( id );
			_assigned.Remove( id ); // the phrase dealt to a leaver goes unsculpted (its author still gets nothing — fine)
			ReleaseBorrowedFor( id ); // a leaver's borrowed prop goes back to being claimable scenery
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
		// roll happens host-side here so a brain always exists by the first TickBots). A bot that wrote the
		// phrase doesn't guess it.
		if ( Phase == CharadesPhase.Sculpting )
		{
			foreach ( var row in Players.Values.Where( p => p.Bot && p.Connection != MimicId && p.Connection != AuthorId ) )
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
