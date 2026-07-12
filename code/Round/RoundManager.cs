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

	/// <summary>Lobby-data key carrying the chosen hunter connection ids from the lobby into the map scene
	/// (comma-separated guids). Written by <see cref="LobbyController"/>, read in <see cref="AssignRoles"/>.</summary>
	public const string HunterIdsKey = "r.hids";

	/// <summary>Points a hunter earns per prop found.</summary>
	[Property, Group( "Scoring" )] public int FindReward { get; set; } = 50;
	/// <summary>Base points a surviving prop earns each second (before the size weighting — see the scoring stub).</summary>
	[Property, Group( "Scoring" )] public float PropPointsPerSecond { get; set; } = 1f;

	/// <summary>DEBUG: everyone spawns as a prop and the Hide phase never ends — endless time to sculpt + test hiding,
	/// no hunters, no round progression. Set on <see cref="RoundManagerSpawner"/> (it's the editor-placed object) and
	/// copied here. Leave off for real play.</summary>
	[Property, Group( "Debug" )] public bool DebugSoloHide { get; set; }

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
	bool IRoundContext.HasTimer => true; // every in-map phase is timed

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
		Settings = RoundSettings.ReadFromLobby();
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

		// HOST: keep the roster in sync with who's actually connected, then tick the active phase.
		ReconcileConnections();
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

		if ( to == RoundPhase.Reveal )
			FlashSurvivingProps();

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

		// DEBUG: everyone's a prop, no hunters — just spawn in and hide forever (see the Hide handling below).
		if ( DebugSoloHide )
		{
			Players.Clear();
			var idx = 0;
			foreach ( var c in conns )
				Players[c.Id] = new PlayerInfo
				{
					Connection = c.Id,
					Name = c.DisplayName,
					Role = PlayerRole.Prop,
					Alive = true,
					Found = false,
					Nominated = false,
					Score = 0,
					SpawnIndex = idx++,
				};
			return;
		}

		var hunters = ReadHunterIds();
		// Only keep nominees who are still connected — the lobby stamped these ids 10+ seconds ago (countdown + map
		// load), so a nominee may have left. Without this, a stale non-empty set that matches NO connection would
		// skip the random fallback and start a zero-hunter round.
		hunters.IntersectWith( conns.Select( c => c.Id ) );
		if ( hunters.Count == 0 )
			hunters = ChooseRandomHunters( conns, Settings.HunterCount );

		// Never let everyone be a hunter — demote one to keep a prop in play.
		if ( conns.All( c => hunters.Contains( c.Id ) ) )
			hunters.Remove( conns[Random.Shared.Next( conns.Count )].Id );

		Players.Clear();
		var hunterIdx = 0;
		var propIdx = 0;
		foreach ( var c in conns )
		{
			var isHunter = hunters.Contains( c.Id );
			Players[c.Id] = new PlayerInfo
			{
				Connection = c.Id,
				Name = c.DisplayName,
				Role = isHunter ? PlayerRole.Hunter : PlayerRole.Prop,
				Alive = true,
				Found = false,
				Nominated = isHunter,
				Score = 0,
				SpawnIndex = isHunter ? hunterIdx++ : propIdx++,
			};
		}
	}

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

	static HashSet<Guid> ChooseRandomHunters( List<Connection> conns, int count )
	{
		count = Math.Clamp( count, 1, Math.Max( 1, conns.Count - 1 ) ); // ≥1 hunter, ≥1 prop
		return conns.OrderBy( _ => Random.Shared.Next() ).Take( count ).Select( c => c.Id ).ToHashSet();
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

		var wantNetworked = WantNetworked();

		// No pawn yet → spawn at our assigned spot.
		if ( !_ownPawn.IsValid() )
		{
			SpawnOwnPawn( info.Role, SpotFor( info ), wantNetworked );
			return;
		}

		// Role changed (we were a prop, a hunter found us) → respawn in our new role, right where we stand.
		if ( _ownPawnRole != info.Role )
		{
			SpawnOwnPawn( info.Role, _ownPawn.WorldTransform, wantNetworked );
			return;
		}

		// We've been driving a purely-local pawn through prep; now it's time to be seen → put it on the wire as-is
		// (keeps the disguise we sculpted and the spot we hid in).
		if ( wantNetworked && !_ownPawn.Network.Active )
			PublishOwnPawn();
	}

	// Infection conceals during prep, so our pawn stays off the network until the hunt; every other mode (and any
	// non-networked solo test) publishes as soon as it spawns. A pawn that's never networked is simply invisible to
	// everyone else — which is exactly the concealment we want during prep.
	bool WantNetworked()
		=> Networking.IsActive
		&& (Settings.Mode != RoundMode.Infection || Phase is RoundPhase.Hunt or RoundPhase.Reveal or RoundPhase.Consolidation);

	// Our spawn transform: our assigned spot for our current role.
	Transform SpotFor( PlayerInfo info )
	{
		var spots = RoundSpawnPoint.AllOfKind( Scene, info.Role == PlayerRole.Hunter );
		return PickSpot( spots, info.SpawnIndex );
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
			var shooterPawn = Scene.GetAllComponents<HunterController>()
				.FirstOrDefault( h => h.GameObject.Network.Owner?.Id == hunter.Id );
			if ( shooterPawn.IsValid()
				&& shooterPawn.GameObject.WorldPosition.Distance( propPawn.WorldPosition ) > shooterPawn.Range * 1.25f )
				return;
		}

		var ownerId = propPawn.Network.Owner?.Id;
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

	/// <summary>Host→everyone: burst the caught-prop smoke at <paramref name="position"/>. Cloned LOCALLY per machine
	/// from the scene-placed spawner's prefab (the spawner exists on every machine; this manager's own [Property]s
	/// wouldn't). Purely cosmetic — losing it (no spawner, prefab unset) loses nothing but the poof.</summary>
	[Rpc.Broadcast]
	void PlayCaughtPuff( Vector3 position )
	{
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

	int AliveProps => Players.Values.Count( p => p.Role == PlayerRole.Prop && p.Alive );

	// Hunters on the roster. Alive is meaningless for hunters (a converted prop is Role=Hunter, Alive=false), so
	// this counts rows by role only.
	int Hunters => Players.Values.Count( p => p.Role == PlayerRole.Hunter );

	// ── Stubs to flesh out next ──────────────────────────────────────────────────────────────────────────────
	// Reveal flash: surviving props pulse with the outline shader so spectators can see where they were hiding.
	void FlashSurvivingProps()
	{
		// TODO: enable the outline/flash effect on every pawn whose roster row is an Alive prop.
	}

	// ── Roster upkeep ────────────────────────────────────────────────────────────────────────────────────────
	// Host-only. Add late joiners, drop leavers. Pawns aren't tracked here — a leaver's networked pawn is removed by
	// its NetworkOrphaned.Destroy, and a joiner spawns their own pawn via EnsureOwnPawn once they have a row.
	void ReconcileConnections()
	{
		// Drop connections that left.
		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Connection.All.Any( c => c.Id == id ) )
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
