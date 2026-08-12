using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Creative mode: a group of hunters populating the map with props, sandbox style. Spawned into the chosen map
/// by <see cref="RoundManagerSpawner"/> (the mode key the lobby stamped decides between this and
/// <see cref="RoundManager"/>). No phases, no timers, no win state — the host's pause-menu "Return to Lobby" is
/// the way out.
///
/// The loop: everyone spawns as a HUNTER. P swaps you to a fresh prop pawn to sculpt; P again returns you to a
/// hunter and the prop is RELEASED — it persists in the world as scenery (<see cref="HiderController.ReleaseControl"/>,
/// host keeps simulating it). Aiming at a released prop as a hunter outlines it with an "E to Edit" prompt; E
/// POSSESSES it — your hunter despawns and you take the prop over, landing in edit mode.
///
/// <b>Networking.</b> This is the LOBBY's pawn model, not the round's: a NetworkSpawn'd singleton (a scene-placed
/// component's [Sync] changes don't replicate) where the HOST spawns every pawn and hands each to its owner —
/// because possession is an ownership CLAIM, and claims must be arbitrated in one place. The host processes
/// <see cref="RequestPossess"/> calls serially with the <see cref="ReleasedProps"/> registry remove as the
/// idempotency guard, so two players pressing E on the same prop in the same instant get exactly one winner —
/// the loser keeps their hunter.
/// </summary>
[Title( "Creative Manager" )]
[Category( "Mimiclay" )]
[Icon( "brush" )]
public sealed class CreativeManager : Component, IRoundContext
{
	/// <summary>The live creative manager (null everywhere but a creative map). HUD + input + the hunter's
	/// hover detection read this to know creative rules apply.</summary>
	public static CreativeManager Current { get; private set; }

	/// <summary>The claimable clay the LOCAL hunter is currently aiming at, published per-frame by
	/// <see cref="HunterController"/> via <see cref="SetLocalHover"/>. Read through
	/// <see cref="LocalHoverSculpture"/>, which is freshness-gated: the publisher can vanish mid-hover (the
	/// hunter pawn is destroyed by a granted possession, or stops updating in edit mode), and component update
	/// order is a HashSet — so staleness is told by age, never by relying on someone clearing it.</summary>
	public static SdfSculpture LocalHover { get; private set; }
	static RealTimeSince _hoverAge;

	/// <summary>Stamp this frame's hover (null = aiming at nothing claimable).</summary>
	public static void SetLocalHover( SdfSculpture hover )
	{
		LocalHover = hover;
		_hoverAge = 0f;
	}

	/// <summary>The hover target if it's still current and still claimable, else null — what the outline gate
	/// and the toast actually consume.</summary>
	public static SdfSculpture LocalHoverSculpture
		=> IsClaimable( LocalHover ) && _hoverAge < 0.1f ? LocalHover : null;

	/// <summary>What creative lets you take over: any clay in the map that isn't currently BEING someone.
	/// A pawn prop only once its player let it go (<see cref="IsReleased"/>); a hunter's face never;
	/// everything else with brushes — scene decoys, prop-builder balls, blockset pieces — always
	/// (claiming one CONVERTS it into a prop pawn, see <see cref="RequestPossess"/>).</summary>
	public static bool IsClaimable( SdfSculpture sculpture )
	{
		if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } )
			return false;

		if ( sculpture.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false; // someone's face (or their gun's clay) — never claimable

		var hider = sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
			return IsReleased( hider ); // a pawn's body: only once released into the world

		return IsScenery( sculpture ); // scene-placed clay — claimable by conversion
	}

	/// <summary>Scene-placed clay: a sculpture that is nobody's pawn (no controller above it) and genuinely
	/// part of the scene — NotSaved excludes runtime-made rigs, most importantly SdfStage's thumbnail hosts,
	/// which live in the GAME scene while rendering to their own SceneWorld and would otherwise read as props.
	/// The component IS the marker (no prop tag to keep in sync across prefabs); this is the classification
	/// both the claim rule and the spawn-props sweep bottom out in, so "what the sweep deletes" and "what a
	/// hunter can take" can never drift apart.</summary>
	static bool IsScenery( SdfSculpture sculpture )
		=> sculpture.IsValid()
		&& !sculpture.GameObject.Flags.HasFlag( GameObjectFlags.NotSaved )
		&& !sculpture.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid()
		&& !sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ).IsValid();

	// ── Networked state (host writes, everyone reads — incl. late-joiners via the spawn snapshot) ─────────────
	/// <summary>Per-player state for the roster row at the top of the HUD. Role mirrors what each player is
	/// currently being (hunter or prop) so the pips read right; nominations/scores don't exist here.</summary>
	[Sync] public NetDictionary<Guid, PlayerInfo> Players { get; private set; } = new();

	/// <summary>The released props, by pawn GameObject id — the registry every machine's hover/claim reads.
	/// Lives HERE, on the host-owned manager, rather than as a [Sync] flag on the pawn itself: a released pawn
	/// is UNOWNED (its release just dropped ownership), and a per-pawn flag written across that ownership edge
	/// proved unreliable on other machines — while this manager's [Sync] state is the same provably-replicating
	/// mechanism the roster uses. Host adds on release, removes on claim (the remove is the claim's idempotency
	/// guard). Destroyed pawns' ids linger harmlessly — nothing resolves them again.</summary>
	[Sync] public NetDictionary<Guid, bool> ReleasedProps { get; private set; } = new();

	/// <summary>Is this pawn prop released scenery (claimable, driven by nobody)? Safe anywhere — false outside
	/// creative maps.</summary>
	public static bool IsReleased( HiderController hider )
		=> hider.IsValid() && Current.IsValid() && Current.ReleasedProps.ContainsKey( hider.GameObject.Id );

	// ── Host-only pawn bookkeeping ─────────────────────────────────────────────────────────────────────────────
	// The pawn each player currently IS. Released props leave this map — they belong to nobody.
	readonly Dictionary<Guid, GameObject> _pawns = new();
	readonly HashSet<Guid> _known = new();

	// What each player's last HUNTER pawn's face looked like, snapshotted as that pawn is destroyed and put on
	// their next one — the host can't read a client's saved head off their disk, so without this every P/E swap
	// would flash the prefab-default head at everyone until the client's own dress published back. Faces only:
	// there is no disguise memory here, because a swapped-away prop isn't destroyed — it PERSISTS in the world.
	readonly Dictionary<Guid, List<SdfBrush>> _faces = new();

	// Host-side per-caller gate on RequestPossess, same shape as RoundManager's shot gate: the RPC is the trust
	// boundary, so re-enforce a sane rate at it rather than trusting the client's own key repeat.
	const float PossessCooldown = 0.3f;
	readonly Dictionary<Guid, RealTimeUntil> _possessGate = new();

	// How far a hunter can claim a prop from. Generous — the hover raycast is the gun's 4096u ray — but bounded,
	// with the same latency slack the shot validation uses.
	const float PossessRange = 4096f;

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	// ── IRoundContext ──────────────────────────────────────────────────────────────────────────────────────────
	// Lobby phase, deliberately: it's the phase with no round furniture, and it's what makes RoundHud lay out
	// the all-players roster row (the "player list from lobby mode"). Never a timer — creative doesn't end.
	RoundPhase IRoundContext.Phase => RoundPhase.Lobby;
	float IRoundContext.TimeRemaining => 0f;
	bool IRoundContext.HasTimer => false;

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
		// EVERY machine (clients run this when the spawned manager replicates — after their scene is up): read
		// the host's creative rules off session data. Lobby data reaches all members, and what it drives here
		// is machine-local work, so nothing needs to ride [Sync].
		var settings = CreativeSettings.ReadFromLobby();

		// "Spawn Props" off = blank canvas: delete the map's pre-placed clay. A LOCAL, deterministic sweep on
		// each machine rather than host broadcasts — scene objects come identical from the scene file, so every
		// machine (late joiners included) resolves the same set, with zero network traffic. Player-made and
		// converted props all live under pawn controllers, so they can never match; running at start-only is
		// therefore also correct for a joiner arriving mid-session.
		if ( !settings.SpawnProps )
		{
			foreach ( var sculpture in Scene.GetAllComponents<SdfSculpture>().ToList() )
			{
				if ( IsScenery( sculpture ) )
					sculpture.GameObject.Destroy();
			}
		}
	}

	protected override void OnUpdate()
	{
		HandleInput();

		if ( !IsHostAuthority )
			return;

		ReconcileConnections();
	}

	// Every machine: the P swap, same raw-key shape as LobbyController.HandleDebugInput (the request is
	// [Rpc.Host], so a client's press routes to the host). The camera carry is owner-side state the host never
	// sees — captured at the press, consumed by our replacement pawn as it starts.
	void HandleInput()
	{
		if ( PauseMenu.IsOpen )
			return;

		if ( Input.Keyboard.Pressed( "P" ) )
		{
			LobbySwapCarry.Capture( Scene, OwnProp() );
			RequestSwap( Scene.Camera.IsValid() ? Scene.Camera.WorldRotation.Angles().yaw : 0f );
		}
	}

	// Our own hider pawn if we're currently a prop — RosterIdOf, not IsProxy, for symmetry with the lobby's
	// version (and so a future host-held pawn can never answer for the host's own).
	HiderController OwnProp()
	{
		if ( Connection.Local?.Id is not { } id )
			return null;

		foreach ( var p in Scene.GetAllComponents<HiderController>() )
			if ( p.IsValid() && !IsReleased( p ) && RoundManager.RosterIdOf( p.GameObject ) == id )
				return p;

		return null;
	}

	// ── Host: roster + join/leave ──────────────────────────────────────────────────────────────────────────────
	// Auto-spawn a hunter for each connection; drop leavers' rows. A leaver's PROP is released into the world —
	// their work survives them (its spawn options ClearOwner instead of the prefab's destroy-on-orphan, and this
	// sweep finishes the job by marking it dormant + Released) — while a leaver's hunter just goes away.
	void ReconcileConnections()
	{
		_known.RemoveWhere( id => !Connection.All.Any( c => c.Id == id ) );
		foreach ( var c in Connection.All )
		{
			if ( _known.Add( c.Id ) )
			{
				Players[c.Id] = new PlayerInfo
				{
					Connection = c.Id,
					Name = c.DisplayName,
					Role = PlayerRole.Hunter,
					Alive = true,
					SpawnIndex = NextFreeSpawnIndex(),
				};
				SpawnHunter( c, HunterSpawnSpot( Players[c.Id].SpawnIndex ) );
			}
		}

		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Connection.All.Any( c => c.Id == id ) )
				continue;

			Players.Remove( id );
			_faces.Remove( id );
			_possessGate.Remove( id );
			if ( _pawns.Remove( id, out var pawn ) && pawn.IsValid() )
			{
				var hider = pawn.Components.Get<HiderController>();
				if ( hider.IsValid() )
					ReleaseProp( hider ); // the prop stays — creative worlds keep what leavers built
				else
					pawn.Destroy();
			}
		}
	}

	// Lowest roster slot no current row holds, so joiners fill gaps instead of marching down the spawn ring.
	int NextFreeSpawnIndex()
	{
		var used = Players.Values.Select( p => p.SpawnIndex ).ToHashSet();
		var i = 0;
		while ( used.Contains( i ) ) i++;
		return i;
	}

	// ── Client → host requests ─────────────────────────────────────────────────────────────────────────────────
	/// <summary>Caller toggles hunter ↔ prop. Hunter → a fresh default prop where they stand (sculpt away).
	/// Prop → back to a hunter — and unlike the lobby, the prop is NOT destroyed: it's released into the world
	/// (that's the whole game — populating the map). <paramref name="viewYaw"/> is the caller's camera yaw at
	/// the press, so the new hunter spawns aiming along the view they had.</summary>
	[Rpc.Host]
	public void RequestSwap( float viewYaw )
	{
		var c = Rpc.Caller;
		if ( c is null || !Players.TryGetValue( c.Id, out var row ) )
			return;

		var pawn = _pawns.GetValueOrDefault( c.Id );
		var hider = pawn.IsValid() ? pawn.Components.Get<HiderController>() : null;

		if ( hider.IsValid() )
		{
			// Prop → hunter. Release the prop first (it persists), then spawn the hunter CLEAR of its hull —
			// a pawn spawned inside the released disguise's collider gets solver-shoved, sometimes through the
			// floor (the lobby destroys the old pawn for exactly this reason; we can't, so we step aside).
			var at = HunterSpotClearOf( hider, viewYaw );
			_pawns.Remove( c.Id );
			ReleaseProp( hider );

			row.Role = PlayerRole.Hunter;
			Players[c.Id] = row;
			SpawnHunter( c, at );
		}
		else
		{
			// Hunter → prop, in place. The face is remembered off the departing pawn so the next hunter spawn
			// doesn't flash the default head.
			var at = pawn.IsValid() ? pawn.WorldTransform : HunterSpawnSpot( row.SpawnIndex );
			if ( _pawns.Remove( c.Id, out var old ) && old.IsValid() )
			{
				RememberFace( c.Id, old );
				old.Destroy();
			}

			row.Role = PlayerRole.Prop;
			Players[c.Id] = row;
			SpawnProp( c, at );
		}
	}

	// Scene props already converted (or mid-conversion) this session, by the ORIGINAL scene object's id. The
	// original's Destroy is deferred to end-of-frame, so without this two same-frame claims on one scene prop
	// would both pass the IsValid check and mint two clones.
	readonly HashSet<Guid> _claimedScene = new();

	/// <summary>Caller claims the clay under their crosshair — the E press. THE arbitration point: requests
	/// arrive serially on the host. A released PAWN prop is handed over directly, with the
	/// <see cref="ReleasedProps"/> registry remove doubling as the idempotency guard (it succeeds for the first
	/// claim only; every later claim is rejected). A SCENE prop (decoy / prop-builder
	/// clay — never networked, and you can't NetworkSpawn a scene object without duplicating it on clients) is
	/// CONVERTED: a prop-pawn clone is dressed in its brushes at its exact spot, spawned owned by the claimant,
	/// and the original is destroyed on every machine — guarded by <see cref="_claimedScene"/>. Either way two
	/// players pressing E together get exactly one winner; the loser keeps their hunter. Validated like
	/// <see cref="RoundManager.ReportPropHit"/>: the caller must actually be a hunter here, within reach, and
	/// not spamming.</summary>
	[Rpc.Host]
	public void RequestPossess( GameObject target )
	{
		var c = Rpc.Caller;
		if ( c is null || !target.IsValid() || !Players.TryGetValue( c.Id, out var row ) )
			return;

		// The claimant must currently be a hunter (their pawn is how we range-check, too).
		var pawn = _pawns.GetValueOrDefault( c.Id );
		if ( !pawn.IsValid() || !pawn.Components.Get<HunterController>().IsValid() )
			return;

		if ( _possessGate.TryGetValue( c.Id, out var gate ) && gate > 0f )
			return;
		_possessGate[c.Id] = PossessCooldown;

		if ( pawn.WorldPosition.Distance( target.WorldPosition ) > PossessRange * 1.25f )
			return;

		// A released pawn prop: claim it as-is. The registry Remove is the idempotency guard — it succeeds for
		// exactly one caller, so the loser of a same-frame race returns here and keeps their hunter.
		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
		{
			if ( !ReleasedProps.Remove( hider.GameObject.Id ) )
				return; // already claimed (or still being worn)

			HandOver( c, row, pawn, hider, assignOwnership: true );
			return;
		}

		// Scene-placed clay: convert it into a pawn the claimant owns.
		var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
		if ( !IsClaimable( sculpture ) || !_claimedScene.Add( sculpture.GameObject.Id ) )
			return; // not clay, someone's body, or a same-frame race already took it

		var converted = ConvertSceneProp( sculpture, c );
		if ( !converted.IsValid() )
		{
			_claimedScene.Remove( sculpture.GameObject.Id ); // conversion failed — the original is still there
			return;
		}

		HandOver( c, row, pawn, converted.Components.Get<HiderController>(), assignOwnership: false );
	}

	// The shared possession tail: swap the claimant's hunter for the prop. assignOwnership is false for a
	// freshly-converted clone (it NetworkSpawned already owned by the claimant); true for a released pawn prop,
	// which is UNOWNED after its release's DropOwnership — and everything that maps a body to a player
	// (RosterIdOf → the roster pips, own-prop lookups) reads Network.Owner, so even a host self-claim assigns.
	void HandOver( Connection c, PlayerInfo row, GameObject hunterPawn, HiderController prop, bool assignOwnership )
	{
		RememberFace( c.Id, hunterPawn );
		_pawns.Remove( c.Id );
		hunterPawn.Destroy();

		if ( assignOwnership && Networking.IsActive )
			prop.GameObject.Network.AssignOwnership( c );

		// Tell the claimant (and only them) to resume control once the ownership change lands on their machine.
		// Their copy consumes it in OnUpdate — acting inside the RPC could race the ownership packet.
		using ( Rpc.FilterInclude( c ) )
		{
			prop.BeginPossession();
		}

		row.Role = PlayerRole.Prop;
		Players[c.Id] = row;
		_pawns[c.Id] = prop.GameObject;
	}

	// Host-only. Turn a scene-placed sculpture into a live prop pawn: clone the prop prefab dressed in the
	// scene shape's brushes, positioned so the clay lands EXACTLY where it stood (the pawn root sits upright at
	// the shape's feet; any tilt/scale the scene object carried moves onto the disguise child — the shape must
	// never move on its own), then remove the original everywhere. Spawned owned by the claimant with the same
	// ClearOwner orphan mode as every creative prop.
	GameObject ConvertSceneProp( SdfSculpture sculpture, Connection owner )
	{
		var prefab = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.PropPrefab : null;
		if ( !prefab.IsValid() )
		{
			Log.Warning( "CreativeManager: no prop prefab on the spawner — can't convert a scene prop." );
			return null;
		}

		var sceneT = sculpture.WorldTransform;
		var feet = Sdf.TryGetBounds( sculpture.Brushes, out var b )
			? sceneT.PointToWorld( new Vector3( b.Center.x, b.Center.y, b.Mins.z ) )
			: sceneT.Position;
		var rootT = new Transform( feet, Rotation.FromYaw( sceneT.Rotation.Yaw() ) );

		var pawn = prefab.Clone( new CloneConfig( rootT, startEnabled: false, name: $"Creative Prop {owner.DisplayName}" ) );
		if ( !pawn.IsValid() )
			return null;

		// Dress while disabled (the first build ever started is the right shape), then override WearDisguise's
		// feet-at-origin lift with the exact composed placement — the lift assumes an upright unscaled shape,
		// and the scene original may be neither.
		HiderController.WearDisguise( pawn, sculpture.Brushes );
		var disguise = pawn.Children.FirstOrDefault( ch => ch.Name == "Disguise" );
		if ( disguise.IsValid() )
		{
			var local = rootT.ToLocal( sceneT );
			disguise.LocalPosition = local.Position;
			disguise.LocalRotation = local.Rotation;
			disguise.LocalScale = local.Scale;
		}

		// The clone replaces the original for everyone. Scene objects share ids from the scene file, so the
		// broadcast resolves the same object on every machine; joiners never see it — they receive the host's
		// live scene snapshot, where it's already gone.
		DestroySceneProp( sculpture.GameObject );

		pawn.Enabled = true;
		pawn.NetworkSpawn( new NetworkSpawnOptions
		{
			Owner = owner,
			OrphanedMode = NetworkOrphaned.ClearOwner,
		} );
		return pawn;
	}

	/// <summary>Host→everyone: remove a scene prop that was just converted — each machine destroys its own copy
	/// of the scene object (a scene object can't be despawned through the network; it was never on it).</summary>
	[Rpc.Broadcast]
	void DestroySceneProp( GameObject go )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return; // only the host converts

		if ( go.IsValid() )
			go.Destroy();
	}

	// ── Release (the persistence move) ─────────────────────────────────────────────────────────────────────────
	// Host-only. Hand a prop off into the world: dormant on the host (which keeps simulating it as scenery),
	// ownership dropped (the ex-owner's copy becomes a proxy; StopControl tears down their edit state), then
	// registered released — in THIS manager's [Sync] registry, whose replication doesn't depend on the pawn's
	// just-changed ownership.
	void ReleaseProp( HiderController hider )
	{
		if ( !hider.IsValid() )
			return;

		hider.ReleaseControl();
		if ( Networking.IsActive && hider.GameObject.Network.Active )
			hider.GameObject.Network.DropOwnership();
		ReleasedProps[hider.GameObject.Id] = true;
	}

	// ── Spawning (host-only; prefabs + spawn points read off the scene) ────────────────────────────────────────
	void SpawnHunter( Connection owner, Transform at )
	{
		var prefab = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.HunterPrefab : null;
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"CreativeManager: no hunter prefab on the spawner — can't spawn for {owner.DisplayName}." );
			return;
		}

		// A hunter spawns facing its owner's view (the engine controller seeds EyeAngles from spawn rotation).
		// Dress before enable, same as everywhere: an enabled clone already has the default face's build in
		// flight before a post-clone dress could swap the brushes — that build landing first is the flash.
		var dressLocalHead = owner.Id == Connection.Local?.Id;
		var dressCachedFace = !dressLocalHead && _faces.TryGetValue( owner.Id, out var face ) && face is { Count: > 0 };
		var dress = dressLocalHead || dressCachedFace;

		var pawn = prefab.Clone( new CloneConfig( at, startEnabled: !dress, name: $"Creative Hunter {owner.DisplayName}" ) );
		if ( !pawn.IsValid() )
			return;

		if ( dress )
		{
			if ( dressLocalHead )
				HunterController.WearSavedHead( pawn );
			else
				HunterController.WearFace( pawn, _faces[owner.Id] );
			pawn.Enabled = true;
		}

		pawn.NetworkSpawn( owner );
		_pawns[owner.Id] = pawn;
	}

	void SpawnProp( Connection owner, Transform at )
	{
		var prefab = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.PropPrefab : null;
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"CreativeManager: no prop prefab on the spawner — can't spawn for {owner.DisplayName}." );
			return;
		}

		var pawn = prefab.Clone( new CloneConfig( at, startEnabled: true, name: $"Creative Prop {owner.DisplayName}" ) );
		if ( !pawn.IsValid() )
			return;

		// ClearOwner, not the prefab's authored Destroy: creative props must OUTLIVE their player — a leaver's
		// prop would otherwise be engine-destroyed at the disconnect, before the reconcile sweep can release it
		// into the world. ClearOwner (not Host) so the ownerless frame before the sweep runs can't read as the
		// HOST's own pawn anywhere (RosterIdOf answers null for unowned, host-id for host-owned).
		pawn.NetworkSpawn( new NetworkSpawnOptions
		{
			Owner = owner,
			OrphanedMode = NetworkOrphaned.ClearOwner,
		} );
		_pawns[owner.Id] = pawn;
	}

	// The initial hunter spawn spot for a roster slot: the map's hunter start points, ringed once they run out
	// (same de-stack rule as RoundManager.PickSpot). Lifted +64 so the pawn drops onto the floor.
	Transform HunterSpawnSpot( int index )
	{
		var spots = RoundSpawnPoint.AllOfKind( Scene, hunterStart: true );
		var origin = spots.Count > 0 ? spots[index % spots.Count].GameObject : GameObject;
		var stack = spots.Count > 0 ? index / spots.Count : index;
		return new Transform(
			origin.WorldPosition + RoundSpawnPoint.StackOffset( stack ) + Vector3.Up * 64f,
			Rotation.FromYaw( origin.WorldRotation.Yaw() ) );
	}

	// Where the hunter appears when its player releases a prop: stepped back from the prop along the caller's
	// view (so the prop they just placed is right in front of them), clear of the disguise's hull by its own
	// horizontal radius plus a body's worth of margin, grounded on whatever the prop stands on.
	Transform HunterSpotClearOf( HiderController hider, float viewYaw )
	{
		var basePos = hider.TryGetShapeFeet( out var feet ) ? feet : hider.WorldPosition;

		// Horizontal half-extent of the disguise, world-scaled — how far the hull reaches from the origin.
		var radius = 24f;
		var disguise = hider.DisguiseSculpture;
		if ( disguise.IsValid() && Sdf.TryGetBounds( disguise.Brushes, out var bounds ) )
			radius = MathF.Max( bounds.Size.x, bounds.Size.y ) * 0.5f * disguise.WorldScale.x;

		var back = Rotation.FromYaw( viewYaw ).Backward;
		var pos = basePos + back * (radius + 40f);

		// Ground the spot so a big prop on a slope doesn't leave the hunter floating; +64 lift for the drop.
		var tr = Scene.Trace.Ray( pos + Vector3.Up * 96f, pos - Vector3.Up * 128f )
			.IgnoreGameObjectHierarchy( hider.GameObject )
			.Run();
		if ( tr.Hit )
			pos = tr.HitPosition;

		return new Transform( pos + Vector3.Up * 64f, Rotation.FromYaw( viewYaw ) );
	}

	// Snapshot the face off a departing hunter pawn (see _faces). Brush-by-brush copies, so nothing that later
	// mutates the about-to-die live list can reach the cache.
	void RememberFace( Guid id, GameObject pawn )
	{
		var face = HunterController.ResolveFaceOf( pawn );
		if ( face.IsValid() && face.Brushes is { Count: > 0 } )
			_faces[id] = face.Brushes.Select( b => b.Copy() ).ToList();
	}
}
