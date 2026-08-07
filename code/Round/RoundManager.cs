using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Drives one prop-hunt round inside a map scene: the host-authoritative state machine that walks
/// <see cref="RoundPhase.Starting"/> → <see cref="RoundPhase.Hide"/> → <see cref="RoundPhase.Hunt"/> →
/// <see cref="RoundPhase.Reveal"/> → <see cref="RoundPhase.Consolidation"/> and then changes scene back to the
/// lobby. Replaces the throwaway <see cref="DebugGameMode"/> for the real loop (that stays only for the perf scene).
///
/// <b>Networking.</b> A NetworkSpawn'd singleton (NetworkMode.Object, created by <see cref="RoundManagerSpawner"/> —
/// NOT scene-placed, since a scene component's [Sync] CHANGES don't replicate here), so its <c>[Sync]</c> state works
/// the MiniMotors way: the host writes <c>Phase</c> / <c>PhaseEndsAt</c> / <see cref="Settings"/> / <see cref="Players"/>
/// and every client reads them — including late-joiners, via the spawn snapshot. Only the host decides *when* to
/// advance (timers, win-checks) and owns the <see cref="Players"/> roster. The one client→host message is
/// <see cref="ReportPropHit"/> (a hunter's shot landing on a prop).
///
/// <b>Pawn ownership.</b> The host does NOT spawn pawns. Each machine spawns + owns its OWN pawn from the synced
/// roster (<see cref="EnsureOwnPawn"/>). For infection mode that pawn stays purely LOCAL (off the network) through the
/// prep period (Starting + Hide) — so no other player can see or collide with it — then goes on the wire the instant
/// the Hunt begins (<see cref="PublishOwnPawn"/>). Concealment is therefore "the pawn doesn't exist for anyone else
/// yet", not a render/collider hack. Non-concealed modes just publish immediately.
///
/// <b>Test bots.</b> The one exception to "the host doesn't spawn pawns": a bot is a roster row with no machine
/// behind it (<see cref="PlayerInfo.Bot"/>), so the host spawns its body too (<see cref="EnsureBotPawns"/>) — same
/// polling shape, same concealment rule, just nobody at the controls. Seat them from
/// <see cref="RoundManagerSpawner"/> to play the whole loop solo. See <see cref="BotCount"/>.
///
/// <b>Stubs</b> (clearly marked below, to fill next): the size-weighted prop scoring, the reveal flash, and the
/// consolidation gallery. The loop runs end-to-end without them — they're polish on a working skeleton.
/// </summary>
[Title( "Round Manager" )]
[Category( "Mimiclay" )]
[Icon( "sports_esports" )]
public sealed class RoundManager : Component, IRoundContext
{
	/// <summary>The active round manager in this scene (null in the lobby/menu). UI + the hunter's shot read this.</summary>
	public static RoundManager Current { get; private set; }

	/// <summary>True during the <see cref="RoundPhase.Starting"/> countdown: pawns are spawned but their controls are
	/// locked, so players can see their team + get ready without moving. Read by the pawn controllers each frame.</summary>
	public static bool ControlsLocked => Current.IsValid() && Current.Phase == RoundPhase.Starting;

	/// <summary>True when hunter guns may fire: no round running (lobby / debug scenes), the Hunt is on, or the
	/// round is over (<see cref="RoundPhase.Consolidation"/> — shots there are harmless fun; the hit report is
	/// still phase-gated host-side). Read by <see cref="HunterController"/> before each shot. During Hide and
	/// Reveal the whole shot is suppressed — not just the hit report (<see cref="ReportPropHit"/> already gates
	/// that) — because the cosmetic pellet carve permanently craters clay that props have no way to heal.</summary>
	public static bool HuntingAllowed => !Current.IsValid()
		|| Current.Phase == RoundPhase.Hunt
		|| Current.Phase == RoundPhase.Consolidation;

	/// <summary>Lobby-data key carrying the chosen hunter connection ids from the lobby into the map scene
	/// (comma-separated guids). Written by <see cref="LobbyController"/>, read in <see cref="AssignRoles"/>.</summary>
	public const string HunterIdsKey = "r.hids";

	// Scoring + debug config. NOT [Property]: this component is only ever code-created by RoundManagerSpawner
	// (never scene-placed), so the inspector never sees it — author these on the SPAWNER, which copies them here.
	/// <summary>Points a hunter earns per prop found.</summary>
	public int FindReward { get; set; } = 50;
	/// <summary>Base points a surviving prop earns each second (before the size weighting — see the scoring stub).</summary>
	public float PropPointsPerSecond { get; set; } = 1f;

	/// <summary>DEBUG: everyone spawns as a prop and the Hide phase never ends — endless time to sculpt + test hiding,
	/// no hunters, no round progression. Set on <see cref="RoundManagerSpawner"/> (it's the editor-placed object) and
	/// copied here. Leave off for real play.</summary>
	public bool DebugSoloHide { get; set; }

	// ── Test bots ─────────────────────────────────────────────────────────────────────────────────────────
	// All authored on RoundManagerSpawner and copied here (see the [Property] note above). A bot is a ROSTER ROW
	// with a synthetic id and PlayerInfo.Bot set — that alone makes it a player everywhere the roster is the truth
	// (role assignment, the alive/hunter counts, the win checks, scoring, the HUD tally), which is most of what
	// you want to exercise. The one thing a row can't do is spawn its own body — there's no machine behind it — so
	// the HOST spawns bot pawns (EnsureBotPawns) instead. They stand still; they exist to be found.
	/// <summary>How many bots to seat alongside the real connections at round start.</summary>
	public int BotCount { get; set; }

	/// <summary>Give bot HUNTERS a body too. On by default, because it's what a found bot prop CONVERTS INTO —
	/// without it a shot bot simply pops and the round has nothing to show for it. Bot PROPS always get one:
	/// being shootable is the whole point of them.</summary>
	public bool BotHunterPawns { get; set; } = true;

	/// <summary>Dress bot props in a random shape from this machine's <see cref="SculptLibrary"/> instead of the
	/// prop prefab's default blob, so a test map fills with varied silhouettes. Silently keeps the default when
	/// nothing's saved.</summary>
	public bool BotRandomDisguises { get; set; }

	/// <summary>DEBUG: force the local player onto a side at role assignment (see <see cref="PlayAsChoice"/>).</summary>
	public PlayAsChoice PlayAs { get; set; }

	/// <summary>DEBUG: rules that override whatever came across from the lobby (phase lengths, hunter count, mode).
	/// Null = use the lobby's. Set from <see cref="RoundManagerSpawner"/> when its rules override is ticked, so a
	/// map opened straight from the editor can run 20-second phases without touching the menu flow.</summary>
	public RoundSettings? RulesOverride { get; set; }

	// ── Networked state ───────────────────────────────────────────────────────────────────────────────────
	// This manager is NetworkSpawn'd as a NetworkMode.Object by RoundManagerSpawner (NOT scene-placed), so [Sync]
	// works properly: the host writes, every client reads — including late-joiners, who get it from the spawn
	// snapshot. Mirrors how MiniMotors networks its singletons.
	[Sync] public RoundPhase Phase { get; set; } = RoundPhase.Starting;

	/// <summary>When the current phase's timer elapses. <c>TimeUntil</c> is clock-skew-corrected per client, so the
	/// countdown reads correctly everywhere without streaming the value each frame.</summary>
	[Sync] public TimeUntil PhaseEndsAt { get; set; }

	/// <summary>Per-player round state, keyed by connection id. Survives pawn respawns across phases.</summary>
	[Sync] public NetDictionary<Guid, PlayerInfo> Players { get; private set; } = new();

	/// <summary>The rules for this round — read from session data by the host in <see cref="OnStart"/>, then [Sync]'d
	/// to everyone (incl. late-joiners). Fixed for the round once set.</summary>
	[Sync] public RoundSettings Settings { get; set; }

	// ── Host-only bookkeeping (not networked) ────────────────────────────────────────────────────────────────
	readonly Dictionary<Guid, float> _scoreAccum = new();      // fractional prop score carried between integer ticks

	// Host-side per-shooter gate on ReportPropHit. The real cooldown (HunterController.ShootCooldown, 1s) runs
	// owner-side and is therefore client trust; this re-enforces it at the RPC boundary. Slightly looser than the
	// real cooldown so an honest client's shots never trip it through timing jitter.
	const float HostShotCooldown = 0.8f;
	readonly Dictionary<Guid, RealTimeUntil> _shotGate = new();

	// Whether this Hunt opened with any hunters at all. Gates the "every hunter left → end early" check so a round
	// that legitimately never had hunters (solo direct Play spawns the lone player as a prop) keeps its timer.
	bool _huntHadHunters;

	// ── Per-machine pawn state (every machine, incl. clients, owns exactly one pawn — its own) ─────────────────
	GameObject _ownPawn;
	PlayerRole _ownPawnRole = PlayerRole.Unassigned;

	// All-machines: the phase we last reacted to, so a [Sync] Phase change fires IRoundPhaseChanged + local effects on
	// every client (not just the host that flipped it). Starts at a sentinel so the opening phase also fires.
	RoundPhase _observedPhase = (RoundPhase)(-1);

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	// ── IRoundContext (for the phase-agnostic HUD) ───────────────────────────────────────────────────────────
	RoundPhase IRoundContext.Phase => Phase;
	float IRoundContext.TimeRemaining => MathF.Max( 0f, PhaseEndsAt );
	// Every in-map phase is timed — except DebugSoloHide's endless Hide, whose 999999 s would otherwise
	// render as a "16666:39" clock.
	bool IRoundContext.HasTimer => !(DebugSoloHide && Phase == RoundPhase.Hide);

	protected override void OnEnabled()
	{
		Current = this;
		RoundContext.Active = this;
	}

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
		if ( ReferenceEquals( RoundContext.Active, this ) ) RoundContext.Active = null;
	}

	protected override void OnStart()
	{
		// We're spawned (by RoundManagerSpawner) only after the session is live, so no self-hosting here. Clients
		// just receive this networked manager and read its [Sync] state.
		if ( !IsHostAuthority )
			return;

		// Host pulls the lobby's choices across the scene change, decides who's hunting, and opens the countdown.
		// Settings + Players are [Sync], so clients (and late-joiners) get them from this one write — and each client
		// reads its OWN row to spawn its own pawn.
		Settings = RulesOverride ?? RoundSettings.ReadFromLobby();
		AssignRoles();
		TransitionTo( RoundPhase.Starting );
	}

	protected override void OnUpdate()
	{
		// EVERY machine: react to a phase change (the host's [Sync] sets Phase here). Do this first so the frame the
		// phase flips, all clients run the local side-effects (event, hunter return) for the new phase.
		if ( _observedPhase != Phase )
		{
			var from = _observedPhase;
			_observedPhase = Phase;
			ReactToPhase( from, Phase );
		}

		// EVERY machine: keep our own pawn in step with our synced roster row (spawn it, convert it, put it on the
		// wire when it's time). This is the whole spawn model now — the host does not spawn pawns.
		EnsureOwnPawn();

		if ( !IsHostAuthority )
			return;

		// HOST: keep the roster in sync with who's actually connected, stand up the bodies for the players who have
		// no machine to do it themselves, then tick the active phase.
		ReconcileConnections();
		EnsureBotPawns();
		TickHostPhase();
	}

	// ── Host: phase ticking + transitions ────────────────────────────────────────────────────────────────────
	void TickHostPhase()
	{
		switch ( Phase )
		{
			case RoundPhase.Starting:
				if ( PhaseEndsAt <= 0f ) TransitionTo( RoundPhase.Hide );
				break;

			case RoundPhase.Hide:
				if ( !DebugSoloHide && PhaseEndsAt <= 0f ) TransitionTo( RoundPhase.Hunt );
				break;

			case RoundPhase.Hunt:
				TickHuntScoring();
				if ( AliveProps == 0 )
					TransitionTo( RoundPhase.Consolidation ); // every prop found → hunters win, nothing to reveal
				else if ( _huntHadHunters && Hunters == 0 )
					TransitionTo( RoundPhase.Reveal );         // every hunter left → survivors win now, not after 3
					                                           // minutes of dead air
				else if ( PhaseEndsAt <= 0f )
					TransitionTo( RoundPhase.Reveal );         // time up with survivors → show them off
				break;

			case RoundPhase.Reveal:
				if ( PhaseEndsAt <= 0f ) TransitionTo( RoundPhase.Consolidation );
				break;

			case RoundPhase.Consolidation:
				if ( PhaseEndsAt <= 0f ) ReturnToLobby();
				break;
		}
	}

	// Host-only: set the new phase's duration, then flip the synced Phase. Phase is set LAST so a client never sees the
	// new phase with the previous phase's timer. Spawning is NOT here anymore — each machine self-spawns from the
	// roster (EnsureOwnPawn) in response to the synced Phase + its row.
	void TransitionTo( RoundPhase next )
	{
		switch ( next )
		{
			case RoundPhase.Starting:    PhaseEndsAt = Settings.StartCountdownSeconds; break;
			case RoundPhase.Hide:        PhaseEndsAt = DebugSoloHide ? 999999f : Settings.HideSeconds; break;
			case RoundPhase.Hunt:        PhaseEndsAt = Settings.HuntSeconds; _huntHadHunters = Hunters > 0; break;
			case RoundPhase.Reveal:      PhaseEndsAt = Settings.RevealSeconds; break;
			case RoundPhase.Consolidation: PhaseEndsAt = Settings.ConsolidationSeconds; break;
		}

		Phase = next;
	}

	// Every machine: local reactions to entering a phase. Safe on host + clients.
	void ReactToPhase( RoundPhase from, RoundPhase to )
	{
		// At the hunt, send our own hunter back to its start point ("the hunter is returned to the spawn"). Props stay
		// exactly where they hid. We own our pawn locally, so this is a plain authoritative teleport — no respawn.
		if ( to == RoundPhase.Hunt )
			ReturnOwnHunterToSpawn();

		// (The Reveal outline flash isn't triggered here: RoundOutlineSystem asserts it per machine
		// every frame from the synced Phase, alongside the outline visibility rules.)
		IRoundPhaseChanged.Post( x => x.OnRoundPhaseChanged( from, to ) );
	}

	// ── Roles ────────────────────────────────────────────────────────────────────────────────────────────────
	// Host-only. Build the Players roster: hunters are the connections the lobby nominated (carried in lobby data);
	// if none came across — e.g. the map scene was opened directly — pick HunterCount at random. We always leave at
	// least one prop, or there's no game. Each row also gets a per-role SpawnIndex so that when the players spawn
	// their own pawns they land on distinct spots.
	void AssignRoles()
	{
		var conns = Connection.All.ToList();
		if ( conns.Count == 0 )
			return;

		// One seat per participant: the real connections first, then the test bots. Order matters — it's what fixes
		// each row's per-role SpawnIndex in WriteRoster, so the humans take the front spawn points.
		var seats = conns.Select( c => new Seat( c.Id, c.DisplayName, false ) ).ToList();
		for ( var i = 0; i < BotCount; i++ )
			seats.Add( new Seat( BotId( i ), $"Bot {i + 1}", true ) );

		// DEBUG: everyone's a prop, no hunters — just spawn in and hide forever (see the Hide handling below).
		if ( DebugSoloHide )
		{
			WriteRoster( seats, new HashSet<Guid>() );
			return;
		}

		// DEBUG: the local player's forced side is settled FIRST, so the draw and the quota top-up below fill in
		// around it. Deciding it last instead would land the wrong number of hunters — forcing yourself to prop
		// after the quota was already met takes a hunter back out of it.
		var me = Connection.Local?.Id;
		var forcedHunter = PlayAs == PlayAsChoice.Hunter ? me : null;
		var forcedProp = PlayAs == PlayAsChoice.Prop ? me : null;

		var hunters = ReadHunterIds();
		// Only keep nominees who are still connected — the lobby stamped these ids 10+ seconds ago (countdown + map
		// load), so a nominee may have left. Without this, a stale non-empty set that matches NO connection would
		// skip the random fallback and start a zero-hunter round.
		hunters.IntersectWith( conns.Select( c => c.Id ) );
		if ( forcedHunter is not null ) hunters.Add( forcedHunter.Value );
		if ( forcedProp is not null ) hunters.Remove( forcedProp.Value );

		// Nobody nominated (a map opened straight from the editor) → draw at random from everyone seated, bots
		// included. With no bots that's the connections, exactly as before.
		if ( hunters.Count == 0 )
			hunters = ChooseRandomHunters( seats.Where( s => s.Id != forcedProp ).ToList(), Settings.HunterCount );

		// Bots fill out the rest of the hunter quota. Only bots top it up, never more humans: with no bots seated
		// this is a no-op, so a real lobby's nominations still decide its hunters exactly as before — HunterCount
		// stays the fallback draw size there. With bots, it's the knob that says "make this a 3-hunter round".
		foreach ( var seat in seats.Where( s => s.Bot ).OrderBy( _ => Random.Shared.Next() ) )
		{
			if ( hunters.Count >= Settings.HunterCount )
				break;
			hunters.Add( seat.Id );
		}

		// Never let everyone be a hunter — demote one to keep a prop in play. A bot takes the demotion before a
		// person does, and a forced local hunter is exempt (spawning you as a prop after you asked to hunt is the
		// one thing PlayAs must never do). Nobody left to demote — a lone forced hunter — is left alone: it's what
		// was asked for, and the Hunt just ends on its own with no props to find.
		if ( seats.All( s => hunters.Contains( s.Id ) ) )
		{
			var victim = seats.Where( s => s.Id != forcedHunter )
				.OrderByDescending( s => s.Bot )
				.ThenBy( _ => Random.Shared.Next() )
				.Select( s => (Guid?)s.Id )
				.FirstOrDefault();
			if ( victim is not null )
				hunters.Remove( victim.Value );
		}

		WriteRoster( seats, hunters );
	}

	// A participant before roles are decided: a real connection or a bot.
	readonly record struct Seat( Guid Id, string Name, bool Bot );

	// Replace the roster with one row per seat, splitting spawn indices per role so no two of a kind share a spot.
	void WriteRoster( List<Seat> seats, HashSet<Guid> hunters )
	{
		Players.Clear();
		var hunterIdx = 0;
		var propIdx = 0;
		foreach ( var seat in seats )
		{
			var isHunter = hunters.Contains( seat.Id );
			Players[seat.Id] = new PlayerInfo
			{
				Connection = seat.Id,
				Name = seat.Name,
				Role = isHunter ? PlayerRole.Hunter : PlayerRole.Prop,
				Alive = true,
				Found = false,
				Nominated = isHunter && !seat.Bot,
				Score = 0,
				Bot = seat.Bot,
				SpawnIndex = isHunter ? hunterIdx++ : propIdx++,
			};
		}
	}

	// Bot row ids: the seat index in the last field of an otherwise-zero guid. Deterministic (so a bot keeps its
	// identity across a re-assign and the HunterRoster ordering is stable), sorts in seat order, and can't collide
	// with a real connection's guid.
	static Guid BotId( int index ) => new( $"00000000-0000-0000-0000-{index:D12}" );

	static HashSet<Guid> ReadHunterIds()
	{
		var raw = Networking.GetData( HunterIdsKey );
		var set = new HashSet<Guid>();
		if ( string.IsNullOrWhiteSpace( raw ) )
			return set;

		foreach ( var part in raw.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			if ( Guid.TryParse( part, out var id ) )
				set.Add( id );

		return set;
	}

	// Draw hunters from everyone seated — connections and bots alike, so "10 players, 2 hunters" is a fair draw
	// rather than one that always picks the humans first.
	static HashSet<Guid> ChooseRandomHunters( List<Seat> seats, int count )
	{
		count = Math.Clamp( count, 1, Math.Max( 1, seats.Count - 1 ) ); // ≥1 hunter, ≥1 prop
		return seats.OrderBy( _ => Random.Shared.Next() ).Take( count ).Select( s => s.Id ).ToHashSet();
	}

	// ── Spawning (every machine spawns + owns ITS OWN pawn) ───────────────────────────────────────────────────
	// Reconcile the local player's pawn against their synced roster row, once we're in a map phase. Polling rather
	// than a one-shot RPC makes it robust to [Sync] ordering: the moment we have both a Phase and our own row, we
	// spawn; if our role later changes (a found prop becoming a hunter) we respawn; when it's time to be seen we go
	// on the wire. Runs on host and clients alike.
	void EnsureOwnPawn()
	{
		if ( Phase == RoundPhase.Lobby )
			return;

		var me = Connection.Local;
		if ( me is null || !Players.TryGetValue( me.Id, out var info ) )
		{
			RetireOwnPawn(); // we left the roster (e.g. disconnect mid-resolve) — drop our pawn
			return;
		}

		var wantNetworked = WantNetworked( info.Role );

		// No pawn yet → spawn at our assigned spot.
		if ( !_ownPawn.IsValid() )
		{
			SpawnOwnPawn( info.Role, SpotFor( info ), wantNetworked );
			return;
		}

		// Role changed (we were a prop, a hunter found us) → respawn in our new role, right where we stand.
		if ( _ownPawnRole != info.Role )
		{
			SpawnOwnPawn( info.Role, ConversionSpot( _ownPawn ), wantNetworked );
			return;
		}

		// We've been driving a purely-local pawn through prep; now it's time to be seen → put it on the wire as-is
		// (keeps the disguise we sculpted and the spot we hid in).
		if ( wantNetworked && !_ownPawn.Network.Active )
			PublishOwnPawn();
	}

	// Infection conceals during prep. PROPS do it by staying off the network entirely until the hunt — a pawn that
	// was never networked is simply invisible to everyone else, and unlike any rendering trick it cannot be leaked
	// by a shadow, a sound, or a modified client. That's the concealment that actually matters, since finding props
	// is the game.
	//
	// HUNTERS ride the wire from the start and are concealed by rendering instead (see HuntersConcealed). They need
	// to be networked for the same reason props need not to be: hunters roam during Hide, and an unnetworked pawn
	// can't be shown to its own team later, can't be shot the instant Hunt begins, and — the tell that led here —
	// makes the engine's own PlayerController.OnJumped broadcast unresolvable on every other machine.
	bool WantNetworked( PlayerRole role )
		=> Networking.IsActive
		&& (Settings.Mode != RoundMode.Infection
			|| role == PlayerRole.Hunter
			|| Phase is RoundPhase.Hunt or RoundPhase.Reveal or RoundPhase.Consolidation);

	/// <summary>True while a hunter pawn must be invisible and intangible to everyone but its owner: Infection's
	/// prep phases, where hunters are on the network (so the round can talk about them) but must not be seen,
	/// bumped into or shot. Read per-frame by <see cref="HunterController"/> on every machine — concealment is
	/// per-machine rendering/collision state, never networked.</summary>
	public static bool HuntersConcealed => Current.IsValid()
		&& Current.Settings.Mode == RoundMode.Infection
		&& Current.Phase is RoundPhase.Starting or RoundPhase.Hide;

	/// <summary>What THIS machine is currently playing as: the role of the pawn we actually own. Read from the
	/// pawn rather than our roster row because the row flips FIRST — a caught prop is marked Hunter a moment
	/// before <see cref="EnsureOwnPawn"/> respawns it — and anything asking "what am I" wants the answer that
	/// matches the body it's looking through. <see cref="PlayerRole.Unassigned"/> when we have no pawn.</summary>
	public static PlayerRole LocalRole => Current.IsValid() && Current._ownPawn.IsValid()
		? Current._ownPawnRole
		: PlayerRole.Unassigned;

	/// <summary>True while OTHER players' props must be invisible and intangible TO US, because we're a prop
	/// ourselves and it isn't the Reveal yet. Props go on the wire at the Hunt so HUNTERS can see and shoot them
	/// (<see cref="WantNetworked"/>), and that necessarily hands every other prop their positions too — so from
	/// the Hunt onward the hiding is per-machine rendering state, exactly like <see cref="HuntersConcealed"/>.
	/// Props learning where the other props are is the Reveal's payoff and shouldn't leak before it.
	///
	/// Unlike <see cref="HuntersConcealed"/> this is asymmetric — it depends on who WE are, so the same prop is
	/// concealed on a fellow prop's machine and fully visible on every hunter's. Teams mode is exempt: props are
	/// a team there and see each other all round (nameplates already work that way).</summary>
	public static bool PropsConcealed => Current.IsValid()
		&& Current.Settings.Mode == RoundMode.Infection
		&& Current.Phase is RoundPhase.Starting or RoundPhase.Hide or RoundPhase.Hunt
		&& LocalRole == PlayerRole.Prop;

	// Our spawn transform: our assigned spot for our current role.
	Transform SpotFor( PlayerInfo info )
	{
		var spots = RoundSpawnPoint.AllOfKind( Scene, info.Role == PlayerRole.Hunter );
		return PickSpot( spots, info.SpawnIndex );
	}

	// Where a pawn respawns when its role flips under it — right where it stands. "Where it stands" = the SHAPE's
	// feet, not the raw pawn origin: sculpting can leave the origin buried under the floor (the commit-time recenter
	// fixes it, but a tag can still land on a stale origin), and a hunter spawned from a buried origin falls out of
	// the map. The feet are the shape's LOWEST point though, and on a slope the floor directly under the bounds-centre
	// XY can sit higher than that — so trace down onto whatever the prop was standing on and spawn exactly there (no
	// drop). No hit within the window = the prop was airborne when tagged; spawn at the feet and fall naturally.
	Transform ConversionSpot( GameObject pawn )
	{
		var at = pawn.WorldTransform;

		var hider = pawn.Components.Get<HiderController>();
		if ( !hider.IsValid() || !hider.TryGetShapeFeet( out var feet ) )
			return at;

		var tr = Scene.Trace.Ray( feet + Vector3.Up * 64f, feet - Vector3.Up * 8f )
			.IgnoreGameObjectHierarchy( pawn ) // the old pawn's disguise collider is still live — the ray starts above it
			.Run();

		return at.WithPosition( tr.Hit ? tr.HitPosition : feet );
	}

	Transform PickSpot( List<RoundSpawnPoint> spots, int index )
	{
		var origin = spots.Count > 0 ? spots[index % spots.Count].GameObject : GameObject;
		// When there are more pawns than markers the index wraps back onto used spots — ring the extras around the
		// marker so nobody spawns inside anybody (overlapping hulls get solver-shoved, sometimes through the floor).
		var stack = spots.Count > 0 ? index / spots.Count : index;
		// Lifted +64 like DebugGameMode so the pawn drops onto the floor and the hider seeds its yaw from spawn.
		return new Transform(
			origin.WorldPosition + RoundSpawnPoint.StackOffset( stack ) + Vector3.Up * 64f,
			Rotation.FromYaw( origin.WorldRotation.Yaw() ) );
	}

	// Clone our role's prefab locally and (optionally) publish it. The pawn is owned by THIS machine; while it's
	// unpublished it exists nowhere else, which is the concealment.
	void SpawnOwnPawn( PlayerRole role, Transform at, bool networked )
	{
		var prefab = PrefabFor( role );
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"RoundManager: no {role} prefab on the spawner — can't spawn local pawn." );
			return;
		}

		RetireOwnPawn();

		_ownPawn = prefab.Clone( at, name: $"Pawn ({role}) {Connection.Local.DisplayName}" );
		_ownPawnRole = role;

		if ( networked && _ownPawn.IsValid() )
			PublishOwnPawn();
	}

	// Put our local pawn on the network, owned by us. Orphaned → Destroy so a disconnect cleanly removes it for
	// everyone (the host no longer tracks pawns to retire them).
	void PublishOwnPawn()
	{
		if ( !_ownPawn.IsValid() || _ownPawn.Network.Active )
			return;

		_ownPawn.NetworkSpawn( new NetworkSpawnOptions
		{
			Owner = Connection.Local,
			OrphanedMode = NetworkOrphaned.Destroy,
		} );
	}

	// Destroy our current pawn (a converted prop pops its disguise; a leaver's pawn is removed). Scene change handles
	// end-of-round cleanup, so a plain destroy is enough here.
	void RetireOwnPawn()
	{
		if ( _ownPawn.IsValid() )
			_ownPawn.Destroy();
		_ownPawn = null;
		_ownPawnRole = PlayerRole.Unassigned;
	}

	// ── Bot pawns (host-only: the bodies for roster rows with no machine behind them) ──────────────────────────
	readonly Dictionary<Guid, GameObject> _botPawns = new();
	readonly Dictionary<Guid, PlayerRole> _botPawnRoles = new();
	readonly HashSet<Guid> _botDressPending = new();   // bots still waiting for their disguise (see WearRandomDisguise)

	/// <summary>Whose roster row a pawn belongs to, on ANY machine: the bot row it stands in for, else its network
	/// owner, else — for a pawn that hasn't gone on the wire yet — us, if we're the ones driving it. Everything that
	/// maps a body back to a row has to come through here: bot pawns are all host-owned, so Network.Owner alone
	/// reports every bot (and the host's own pawn) as the same player.</summary>
	public static Guid? RosterIdOf( GameObject pawn )
	{
		if ( !pawn.IsValid() )
			return null;

		// Ancestors too, so a hit on a disguise/head child resolves the same as a hit on the pawn root.
		var bot = pawn.Components.Get<BotPawn>( FindMode.EverythingInSelfAndAncestors );
		if ( bot.IsValid() )
			return bot.RosterId;

		var owner = pawn.Network.Owner?.Id;
		if ( owner is not null )
			return owner;

		// No owner: either a pawn that hasn't gone on the wire (so it's ours — nobody else has it at all) or an
		// orphan on the network that belongs to nobody. Network.Active is what tells those apart.
		return pawn.Network.Active ? null : Connection.Local?.Id;
	}

	/// <summary>True if this pawn is a bot's body — nobody is driving it, on any machine. The counterpart to
	/// "is it a proxy" for code asking "is this someone else's pawn": on the HOST a bot pawn is neither a proxy
	/// nor ours.</summary>
	public static bool IsBotPawn( GameObject pawn )
		=> pawn.IsValid() && pawn.Components.Get<BotPawn>( FindMode.EverythingInSelfAndAncestors ).IsValid();

	// Host-only. The bot half of EnsureOwnPawn: reconcile each bot row against the body the host holds for it —
	// spawn it, respawn it when a hunter finds it, put it on the wire when its role says it should be seen. Same
	// polling shape and the same WantNetworked rule as a real player's pawn, so bots go on the wire exactly when
	// players do. Where it necessarily differs is concealment: a real prop hides by not being networked, which the
	// host can't do with a body it holds itself, so a bot prop simply isn't spawned until the Hunt (see below).
	void EnsureBotPawns()
	{
		if ( Phase == RoundPhase.Lobby )
			return;

		// Rows that went away (a re-assign, bots switched off) take their bodies with them.
		foreach ( var id in _botPawns.Keys.ToList() )
			if ( !Players.TryGetValue( id, out var row ) || !row.Bot )
				RetireBotPawn( id );

		foreach ( var id in Players.Keys.ToList() )
		{
			var info = Players[id];
			if ( !info.Bot )
				continue;

			var pawn = _botPawns.GetValueOrDefault( id );

			// Switched off, a bot hunter is a roster row with no body at all — the round still counts it, a shot
			// bot prop just leaves nothing standing where it was.
			if ( info.Role == PlayerRole.Hunter && !BotHunterPawns )
			{
				if ( pawn.IsValid() )
					RetireBotPawn( id );
				continue;
			}

			if ( !pawn.IsValid() )
			{
				// A bot prop's body doesn't exist until it would have gone on the wire — the same moment a real
				// prop's does. Bots don't sculpt and don't move, so there is nothing for one to do during prep
				// except stand on the host's screen announcing where it is; not existing yet is the cheapest and
				// most complete concealment there is, and it needs no rendering rules at all. (Real props get
				// this for free by never being networked; the host holds bot bodies locally, so it has to be
				// spelled out.) Any non-concealing mode spawns them straight away with everyone else.
				if ( info.Role == PlayerRole.Prop && !WantNetworked( PlayerRole.Prop ) && Networking.IsActive )
					continue;

				SpawnBotPawn( id, info, SpotFor( info ) );
				continue;
			}

			// Found by a hunter → respawn in its new role where it stood, like a player's conversion.
			if ( _botPawnRoles.GetValueOrDefault( id, PlayerRole.Unassigned ) != info.Role )
			{
				SpawnBotPawn( id, info, ConversionSpot( pawn ) );
				continue;
			}

			if ( _botDressPending.Contains( id ) )
				WearRandomDisguise( id, pawn );

			// Time to be seen: on the wire as-is, host-owned.
			if ( WantNetworked( info.Role ) && !pawn.Network.Active )
				pawn.NetworkSpawn();
		}
	}

	// Host-only. Clone the role's prefab and hand nobody the controls.
	void SpawnBotPawn( Guid id, PlayerInfo info, Transform at )
	{
		var prefab = PrefabFor( info.Role );
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"RoundManager: no {info.Role} prefab on the spawner — can't spawn bot pawn." );
			return;
		}

		RetireBotPawn( id );

		var pawn = prefab.Clone( at, name: $"Pawn ({info.Role}) {info.Name}" );
		if ( !pawn.IsValid() )
			return;

		// Which row this body is. Stamped before it can go on the wire so it ships in the spawn snapshot — every
		// machine resolves bot pawns through it (see RosterIdOf).
		pawn.Components.Create<BotPawn>().RosterId = id;

		// Nobody is driving this: no input, no camera, no mouse capture. The prop already has that state — it's
		// what a disguise released into the level does — and the hunter grew the same flag for bots. Without it a
		// host-owned pawn is indistinguishable from the host's own and would fight it for the shared camera.
		var hider = pawn.Components.Get<HiderController>();
		if ( hider.IsValid() )
		{
			hider.ReleaseControl();
			if ( BotRandomDisguises )
				_botDressPending.Add( id ); // deferred: the disguise sculpture resolves in the hider's OnStart
		}

		var hunter = pawn.Components.Get<HunterController>();
		if ( hunter.IsValid() )
			hunter.Bot = true;

		StripBotPawn( pawn );

		_botPawns[id] = pawn;
		_botPawnRoles[id] = info.Role;

		if ( WantNetworked( info.Role ) )
			pawn.NetworkSpawn();
	}

	// A bot has no screen and no microphone. Both pawn prefabs carry per-player hardware that only makes sense for
	// the person holding the controller, and every piece of it decides "is this mine?" by asking whether the pawn is
	// a proxy — which a host-owned bot is NOT. Left alone, eight bots means eight full-screen HUD overlays stacked
	// on the real player's (the tell: nameplates painted once per overlay look heavier than a real player's, which
	// every overlay skips as its own pawn), eight crosshairs, eight pause menus racing for Escape, and eight voices
	// broadcasting whenever the host holds push-to-talk. Taking the hardware away is both cheaper and more honest
	// than teaching each consumer what a bot is.
	static void StripBotPawn( GameObject pawn )
	{
		// The whole HUD branch, not just the panel: its UI components run per-frame scene scans of their own.
		foreach ( var screen in pawn.Components.GetAll<Sandbox.ScreenPanel>( FindMode.EverythingInSelfAndDescendants ) )
			screen.GameObject.Enabled = false;

		// Attached by both controllers in OnAwake — which has already run, since Clone initialises synchronously
		// (SpawnOwnPawn relies on the same thing when it publishes a pawn the instant it clones it).
		foreach ( var voice in pawn.Components.GetAll<PlayerVoice>( FindMode.EverythingInSelfAndDescendants ) )
			voice.Enabled = false;
	}

	void RetireBotPawn( Guid id )
	{
		if ( _botPawns.Remove( id, out var pawn ) && pawn.IsValid() )
			pawn.Destroy();
		_botPawnRoles.Remove( id );
		_botDressPending.Remove( id );
	}

	// Host-only. Dress a bot prop in a random shape from this machine's sculpt library so a test map fills with
	// varied silhouettes instead of a row of identical blobs. Retried each frame until the hider's OnStart has
	// resolved its disguise; an empty library settles it permanently on the prefab's default shape. Setting the
	// brushes and rebuilding is the same move SculptEditSession.Load makes — the Committed that Rebuild fires is
	// what re-cuts the collider and recentres the origin, so the bot ends up solid and standing on its feet.
	void WearRandomDisguise( Guid id, GameObject pawn )
	{
		var hider = pawn.Components.Get<HiderController>();
		var sculpture = hider.IsValid() ? hider.DisguiseSculpture : null;
		if ( !sculpture.IsValid() )
			return; // OnStart hasn't run yet — try again next frame

		_botDressPending.Remove( id );

		var names = SculptLibrary.List();
		if ( names.Count == 0 )
			return;

		var entry = SculptLibrary.Load( names[Random.Shared.Next( names.Count )] );
		if ( entry is null )
			return;

		sculpture.Brushes = entry.Brushes;
		sculpture.Resolution = entry.Resolution;
		sculpture.FlipFaces = entry.FlipFaces;
		sculpture.Rebuild();
	}

	// At the hunt, teleport our own hunter pawn back to its start point. We own it, so setting the transform is
	// authoritative — no respawn, no fighting replication. Props are untouched.
	void ReturnOwnHunterToSpawn()
	{
		var me = Connection.Local;
		if ( me is null || !Players.TryGetValue( me.Id, out var info ) )
			return;
		if ( info.Role != PlayerRole.Hunter || !_ownPawn.IsValid() )
			return;

		var at = SpotFor( info );
		_ownPawn.WorldPosition = at.Position;
		_ownPawn.WorldRotation = at.Rotation;

		var body = _ownPawn.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndDescendants );
		if ( body.IsValid() )
		{
			body.Velocity = 0f;
			body.AngularVelocity = 0f;
		}
	}

	static GameObject PrefabFor( PlayerRole role )
	{
		var spawner = RoundManagerSpawner.Current;
		if ( !spawner.IsValid() )
			return null;

		return role == PlayerRole.Hunter ? spawner.HunterPrefab : spawner.PropPrefab;
	}

	// ── Hunt: shooting + scoring ─────────────────────────────────────────────────────────────────────────────
	/// <summary>Client→host: a hunter's shot landed on <paramref name="propPawn"/>. Validated + resolved on the host
	/// as pure ROSTER changes: the prop is marked found, rewarded to the shooter, and flipped to the Hunter role. The
	/// found player's OWN machine then sees its role change and respawns itself as a hunter where it stood (popping
	/// the disguise) — the host no longer spawns or destroys pawns. Called from <see cref="HunterController"/> when
	/// its trace hits a live prop pawn.</summary>
	[Rpc.Host]
	public void ReportPropHit( GameObject propPawn )
	{
		if ( Phase != RoundPhase.Hunt || !propPawn.IsValid() )
			return;

		var hunter = Rpc.Caller;

		// Validate the SHOOTER, not just the target — this RPC is the trust boundary: the trace, cooldown and range
		// all run owner-side, so a modified client can call it directly with any pawn. (Alive is deliberately NOT
		// checked: a converted prop is Role=Hunter with Alive=false and hunts legitimately.) Skipped when there's no
		// session (solo direct Play has no Rpc.Caller to validate).
		if ( Networking.IsActive )
		{
			if ( hunter is null || !Players.TryGetValue( hunter.Id, out var shooter ) || shooter.Role != PlayerRole.Hunter )
				return;

			// Host-side re-check of the shot cooldown.
			if ( _shotGate.TryGetValue( hunter.Id, out var gate ) && gate > 0f )
				return;
			_shotGate[hunter.Id] = HostShotCooldown;

			// Range sanity: the claimed hit must be within the shooter's weapon reach (+ slack for latency movement).
			// If the shooter's pawn isn't visible to the host yet (the respawn frame after a conversion), skip this
			// check rather than reject — the role + rate gates above still hold.
			// RosterIdOf, not Network.Owner: every bot pawn is host-owned, so on a host that seated bot hunters an
			// owner match would happily hand back a BOT's body as the shooter's and range-check against that.
			var shooterPawn = Scene.GetAllComponents<HunterController>()
				.FirstOrDefault( h => RosterIdOf( h.GameObject ) == hunter.Id );
			if ( shooterPawn.IsValid()
				&& shooterPawn.GameObject.WorldPosition.Distance( propPawn.WorldPosition ) > shooterPawn.Range * 1.25f )
				return;
		}

		// Whose prop this is — a bot's row for a bot body, the owning connection's for a player's.
		var ownerId = RosterIdOf( propPawn );
		if ( ownerId is null || !Players.TryGetValue( ownerId.Value, out var prop ) )
			return;

		if ( prop.Role != PlayerRole.Prop || !prop.Alive )
			return; // already found, or not actually a prop

		// Mark the prop found + convert it (struct: copy-mutate-write back, the NetDictionary setter replicates it).
		// The converted player's machine reacts to Role flipping to Hunter and respawns its own pawn accordingly.
		prop.Alive = false;
		prop.Found = true;
		prop.Role = PlayerRole.Hunter;
		Players[ownerId.Value] = prop;

		// Reward the shooter.
		if ( hunter is not null && Players.TryGetValue( hunter.Id, out var seeker ) )
		{
			seeker.Score += FindReward;
			Players[hunter.Id] = seeker;
		}

		// Poof — everyone sees the substitution smoke where the prop stood, masking the prop→hunter pawn swap.
		PlayCaughtPuff( propPawn.WorldPosition + Vector3.Up * 20f );

		// All props found ends the round immediately — TickHostPhase picks that up next frame via AliveProps.
	}

	/// <summary>Host→everyone: play the found-a-prop sound and burst the caught-prop smoke at
	/// <paramref name="position"/>. The puff is cloned LOCALLY per machine
	/// from the scene-placed spawner's prefab (the spawner exists on every machine; this manager's own [Property]s
	/// wouldn't). Purely cosmetic — losing it (no spawner, prefab unset) loses nothing but the poof.</summary>
	[Rpc.Broadcast]
	void PlayCaughtPuff( Vector3 position )
	{
		Sound.Play( "sounds/game/success.sound", position );

		var prefab = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.CaughtPuffPrefab : null;
		if ( !prefab.IsValid() )
			return;

		ExpirePuff( prefab.Clone( position ) );
	}

	// The burst is a one-shot (~0.7s max particle life) and GameObject has no delayed destroy — retire the clone once
	// every particle is long dead. Component.Task cancels this on scene change, taking the puff with the scene anyway.
	async void ExpirePuff( GameObject puff )
	{
		await Task.DelaySeconds( 2f );
		if ( puff.IsValid() )
			puff.Destroy();
	}

	// Host-only. Surviving props earn points each frame. Size weighting is a STUB: real rule is "bigger prop hiding
	// in plain sight scores more", derived from the disguise's bounds. For now every prop scores the flat rate.
	void TickHuntScoring()
	{
		var dt = Time.Delta;
		foreach ( var id in Players.Keys.ToList() )
		{
			var p = Players[id];
			if ( p.Role != PlayerRole.Prop || !p.Alive )
				continue;

			const float sizeFactor = 1f; // TODO: scale by the prop's disguise bounds (reward big, plain-sight props)
			var acc = _scoreAccum.GetValueOrDefault( id ) + PropPointsPerSecond * sizeFactor * dt;
			var whole = (int)acc;
			if ( whole > 0 )
			{
				_scoreAccum[id] = acc - whole;
				p.Score += whole;
				Players[id] = p;
			}
			else
			{
				_scoreAccum[id] = acc;
			}
		}
	}

	/// <summary>Props still hiding — the ones no hunter has found yet. The win-check counts these, and the HUD
	/// draws one icon per survivor. The roster is the only truth here: in Infection, props stay off the network
	/// until the Hunt, so counting pawns in the scene under-reports on every machine but the host's.</summary>
	public int AliveProps => Players.Values.Count( p => p.Role == PlayerRole.Prop && p.Alive );

	// Hunters on the roster. Alive is meaningless for hunters (a converted prop is Role=Hunter, Alive=false), so
	// this counts rows by role only.
	public int Hunters => Players.Values.Count( p => p.Role == PlayerRole.Hunter );

	/// <summary>The hunter rows in a STABLE order. NetDictionary enumeration order isn't guaranteed, so anything
	/// drawing one element per hunter has to sort or the row reshuffles between frames (and, for the HUD's
	/// per-hunter thumbnails, throws away a SceneWorld every time it does).</summary>
	public List<PlayerInfo> HunterRoster => Players.Values
		.Where( p => p.Role == PlayerRole.Hunter )
		.OrderBy( p => p.Connection )
		.ToList();

	/// <summary>The surviving prop rows, same stable ordering as <see cref="HunterRoster"/>.
	/// <para>
	/// Naming these in the HUD gives nothing away: it says WHO is still hiding, never which object they are.
	/// And in Infection it isn't even new information — the lobby lists everyone, and a caught prop reappears
	/// by name on the hunter side, so the survivors are just everyone minus the hunters shown.
	/// </para></summary>
	public List<PlayerInfo> PropRoster => Players.Values
		.Where( p => p.Role == PlayerRole.Prop && p.Alive )
		.OrderBy( p => p.Connection )
		.ToList();

	// ── Roster upkeep ────────────────────────────────────────────────────────────────────────────────────────
	// Host-only. Add late joiners, drop leavers. Pawns aren't tracked here — a leaver's networked pawn is removed by
	// its NetworkOrphaned.Destroy, and a joiner spawns their own pawn via EnsureOwnPawn once they have a row.
	void ReconcileConnections()
	{
		// Drop connections that left. Bot rows are exempt — there was never a connection behind them to leave, and
		// this sweep would otherwise wipe every bot the frame after they're seated.
		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Players[id].Bot || Connection.All.Any( c => c.Id == id ) )
				continue;

			Players.Remove( id );
			_scoreAccum.Remove( id );
			_shotGate.Remove( id );
		}

		// Add anyone who joined mid-round: a prop through prep (Starting + Hide — the round hasn't begun, they should
		// hide like everyone else), a hunter during the Hunt. Once the round is wrapping up (Reveal/Consolidation)
		// there's nothing left to join — no row, no pawn; the lobby rosters them for the next round.
		foreach ( var c in Connection.All )
		{
			if ( Players.ContainsKey( c.Id ) )
				continue;

			if ( Phase is RoundPhase.Reveal or RoundPhase.Consolidation )
				continue;

			var role = Phase is RoundPhase.Starting or RoundPhase.Hide ? PlayerRole.Prop : PlayerRole.Hunter;
			Players[c.Id] = new PlayerInfo
			{
				Connection = c.Id,
				Name = c.DisplayName,
				Role = role,
				Alive = true,
				Found = false,
				Nominated = false,
				Score = 0,
				SpawnIndex = NextSpawnIndex( role ),
			};
		}
	}

	// Next free per-role spawn index = how many rows already hold that role.
	int NextSpawnIndex( PlayerRole role ) => Players.Values.Count( p => p.Role == role );

	// ── Loop back to the lobby ───────────────────────────────────────────────────────────────────────────────
	// Host-only. After consolidation, change scene back to the lobby so the group can reconfigure + play again.
	void ReturnToLobby()
	{
		if ( !Networking.IsHost )
			return;

		// TODO: carry the round's results (cumulative scores, who won) back to the lobby via lobby data so the
		// lobby can show a running scoreboard. For the block-out we just return.
		// The spawner's SceneFile REFERENCE is the reliable path; the string lookup is only a fallback for a map
		// whose spawner hasn't wired LobbyScene (ResourceLibrary-by-path has returned null mid-session before).
		var options = new SceneLoadOptions();
		var lobby = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.LobbyScene : null;
		var resolved = lobby is not null ? options.SetScene( lobby ) : options.SetScene( LobbyController.LobbyScene );
		if ( !resolved )
		{
			// Push the phase timer back so TickHostPhase retries in a few seconds instead of every frame —
			// a failed resolve otherwise re-runs this (and its engine warning) once per frame, forever.
			Log.Warning( $"RoundManager: couldn't resolve the lobby scene — retrying in 5s. Wire LobbyScene on the map's RoundManagerSpawner." );
			PhaseEndsAt = 5f;
			return;
		}

		Game.ChangeScene( options );
	}
}
