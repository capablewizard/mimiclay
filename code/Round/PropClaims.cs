using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// The mode-side half of the prop-claim contract: implemented by the game-mode manager that HOSTS a
/// <see cref="PropClaims"/> service, on the SAME GameObject (so one NetworkSpawn ships both). The service owns
/// everything mode-agnostic — classification, hover, arbitration, conversion, the released registry — and calls
/// back through this for the things only the mode knows: its pawn bookkeeping, its roster, and its policy.
/// </summary>
public interface IPropClaimHost
{
	/// <summary>Are claims currently open? Policy, read on every machine (the hover prompt) and re-checked on
	/// the host at the claim itself. Creative never closes; the lobby closes during the launch countdown.</summary>
	bool ClaimsAllowed { get; }

	/// <summary>The prop-pawn prefab a claimed SCENE prop converts into (read live off scene furniture — a
	/// NetworkSpawn'd manager's [Property] refs only exist on the host).</summary>
	GameObject PropPrefab { get; }

	/// <summary>The caller's current pawn, from the host's bookkeeping. Null denies the claim; the service
	/// itself requires it to be a hunter (only hunters have the crosshair that claims).</summary>
	GameObject ClaimantPawn( Connection c );

	/// <summary>A claim was granted: update roster + pawn bookkeeping (the claimant is a prop now, driving
	/// <paramref name="prop"/>). Called BEFORE <paramref name="hunterPawn"/> is destroyed, so the host can
	/// still read state off it (the face snapshot).</summary>
	void OnClaimGranted( Connection c, GameObject hunterPawn, HiderController prop );
}

/// <summary>
/// The prop-claim service: what lets a hunter aim at clay in the world and press E to take it over for editing.
/// Extracted from CreativeManager so any mode can host it — creative and the lobby today — by spawning one
/// beside its manager (which implements <see cref="IPropClaimHost"/>) on the networked singleton. A mode that
/// doesn't want editable props simply doesn't spawn one: presence IS the policy, and maps carry no flags.
///
/// Owns, mode-agnostically:
/// <list type="bullet">
/// <item>CLASSIFICATION — <see cref="IsClaimable"/>/<see cref="IsScenery"/>: any sculpture with brushes that
/// isn't someone's body. The <see cref="SdfSculpture"/> component is the marker; there is no prop tag to keep
/// in sync across prefabs and maps.</item>
/// <item>HOVER — the per-frame local hover published by the owning hunter (<see cref="SetLocalHover"/>),
/// freshness-gated for the outline system and the "E to Edit" toast.</item>
/// <item>ARBITRATION — <see cref="RequestPossess"/>, THE claim point: requests arrive serially on the host,
/// with the <see cref="ReleasedProps"/> registry remove (pawn props) and <see cref="_claimedScene"/> (scene
/// props) as idempotency guards, so two players pressing E on the same prop in the same instant get exactly
/// one winner — the loser keeps their hunter.</item>
/// <item>CONVERSION — <see cref="ConvertSceneProp"/>: a scene-placed prop can't be taken over directly (it was
/// never networked, and you can't NetworkSpawn a scene object without duplicating it on clients), so a
/// prop-pawn clone is dressed in its brushes at its exact spot and the original is destroyed everywhere.</item>
/// <item>RELEASE — <see cref="Release"/>: hand a pawn prop off into the world as claimable scenery, registered
/// in this component's [Sync] registry (the same provably-replicating mechanism the rosters use — a flag on
/// the pawn itself would have to be written across the release's ownership edge, which proved unreliable).</item>
/// </list>
/// </summary>
[Title( "Prop Claims" )]
[Category( "Mimiclay" )]
[Icon( "pan_tool" )]
public sealed class PropClaims : Component
{
	/// <summary>The live claim service (null wherever props aren't editable — round maps, the menu). The
	/// hunter's hover detection and the outline system read this to know claim rules apply. Named Current like
	/// every other singleton here — NOT Active, which would shadow Component.Active (the enabled state).</summary>
	public static PropClaims Current { get; private set; }

	/// <summary>How far a hunter can reach to hover (and so claim) clay, measured from the eye to the point the
	/// crosshair ray lands on — NOT to the prop's origin, so a big prop is reachable by its near face. The gun's
	/// own ray is map-length (4096u); without this bound every distant prop across the room outlines and offers
	/// "E to Edit", which reads as noise and lets you claim things you can't see properly. Authored by whoever
	/// spawns the service (RoundManagerSpawner for creative maps, LobbyController for the lobby), BEFORE the
	/// NetworkSpawn so the snapshot ships it to every client's hover.</summary>
	[Property, Range( 64f, 4096f )]
	public float HoverRange { get; set; } = 300f;

	/// <summary>The reach the host validates a claim against, in origin-to-origin terms. Slack over
	/// <see cref="HoverRange"/> on two counts: the client measured to a SURFACE, and the origin of a large prop
	/// can sit well behind it; plus the usual latency margin the shot validation uses.</summary>
	float PossessRange => HoverRange * 1.25f + 256f;

	// The mode manager beside us. Cached — same GameObject, same lifetime by construction.
	IPropClaimHost _host;
	IPropClaimHost Host => _host ??= Components.Get<IPropClaimHost>();

	/// <summary>Is the whole flow currently open? The hover/prompt gate on every machine — policy comes from
	/// the host mode (its component replicates beside this one, and its policy inputs are [Sync], so clients
	/// answer correctly too). The host re-checks at the claim itself; this just keeps the UI honest.</summary>
	public bool ClaimsOpen => Host?.ClaimsAllowed ?? false;

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

	/// <summary>What a claim can take: any clay in the map that isn't currently BEING someone.
	/// A pawn prop only once its player let it go (<see cref="IsReleased"/>); a hunter's face never;
	/// everything else with brushes — scene decoys, prop-builder balls, blockset pieces — always
	/// (claiming one CONVERTS it into a prop pawn, see <see cref="RequestPossess"/>).</summary>
	public static bool IsClaimable( SdfSculpture sculpture )
	{
		if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } )
			return false;

		if ( sculpture.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false; // someone's face (or their gun's clay) — never claimable

		if ( sculpture.Components.Get<TutorialNpc>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false; // the tutorial character: his E opens the guided session locally (see TutorialNpc), never a claim

		var hider = sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
			return IsReleased( hider ); // a pawn's body: only once released into the world

		return IsScenery( sculpture ); // scene-placed clay — claimable by conversion
	}

	/// <summary>Scene-placed clay: a sculpture that is nobody's pawn (no controller above it) and genuinely
	/// part of the scene — NotSaved excludes runtime-made rigs, most importantly SdfStage's thumbnail hosts,
	/// which live in the GAME scene while rendering to their own SceneWorld and would otherwise read as props.
	/// The component IS the marker (no prop tag to keep in sync across prefabs); this is the classification
	/// both the claim rule and creative's spawn-props sweep bottom out in, so "what the sweep deletes" and
	/// "what a hunter can take" can never drift apart.</summary>
	public static bool IsScenery( SdfSculpture sculpture )
		=> sculpture.IsValid()
		&& !sculpture.GameObject.Flags.HasFlag( GameObjectFlags.NotSaved )
		&& !sculpture.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid()
		&& !sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ).IsValid();

	/// <summary>The released props, by pawn GameObject id — the registry every machine's hover/claim reads.
	/// Lives HERE, on the host-owned service, rather than as a [Sync] flag on the pawn itself: a released pawn
	/// is UNOWNED (its release just dropped ownership), and a per-pawn flag written across that ownership edge
	/// proved unreliable on other machines — while this component's [Sync] state is the same provably-replicating
	/// mechanism the rosters use. Host adds on release, removes on claim (the remove is the claim's idempotency
	/// guard). Destroyed pawns' ids linger harmlessly — nothing resolves them again.</summary>
	[Sync] public NetDictionary<Guid, bool> ReleasedProps { get; private set; } = new();

	/// <summary>Is this pawn prop released scenery (claimable, driven by nobody)? Safe anywhere — false wherever
	/// no claim service runs.</summary>
	public static bool IsReleased( HiderController hider )
		=> hider.IsValid() && Current.IsValid() && Current.ReleasedProps.ContainsKey( hider.GameObject.Id );

	// Host-only: pawns minted by ConvertSceneProp, by pawn GameObject id. The lobby reads this to tell borrowed
	// map furniture (release it back into the world on a role swap) from a player's own practice body (destroy
	// it, remembering the disguise). Host-only is enough — every consumer runs inside a host RPC.
	readonly HashSet<Guid> _converted = new();

	/// <summary>Was this pawn converted from a scene prop (it's map furniture being worn, not a body a player
	/// built from scratch)? Host-side answer only.</summary>
	public bool IsConverted( GameObject pawn )
		=> pawn.IsValid() && _converted.Contains( pawn.Id );

	// Host-side per-caller gate on RequestPossess, same shape as RoundManager's shot gate: the RPC is the trust
	// boundary, so re-enforce a sane rate at it rather than trusting the client's own key repeat.
	const float PossessCooldown = 0.3f;
	readonly Dictionary<Guid, RealTimeUntil> _possessGate = new();

	// Scene props already converted (or mid-conversion) this session, by the ORIGINAL scene object's id. The
	// original's Destroy is deferred to end-of-frame, so without this two same-frame claims on one scene prop
	// would both pass the IsValid check and mint two clones.
	readonly HashSet<Guid> _claimedScene = new();

	protected override void OnEnabled()
	{
		Current = this;
	}

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
	}

	/// <summary>Caller claims the clay under their crosshair — the E press. THE arbitration point: requests
	/// arrive serially on the host. A released PAWN prop is handed over directly, with the
	/// <see cref="ReleasedProps"/> registry remove doubling as the idempotency guard (it succeeds for the first
	/// claim only; every later claim is rejected). A SCENE prop is CONVERTED (see
	/// <see cref="ConvertSceneProp"/>), guarded by <see cref="_claimedScene"/>. Either way two players pressing
	/// E together get exactly one winner; the loser keeps their hunter. Validated like
	/// <see cref="RoundManager.ReportPropHit"/>: the caller must actually be a hunter here, within reach, and
	/// not spamming.</summary>
	[Rpc.Host]
	public void RequestPossess( GameObject target )
	{
		var c = Rpc.Caller;
		var host = Host;
		if ( c is null || !target.IsValid() || host is null || !host.ClaimsAllowed )
			return;

		// The claimant must currently be a hunter (their pawn is how we range-check, too).
		var pawn = host.ClaimantPawn( c );
		if ( !pawn.IsValid() || !pawn.Components.Get<HunterController>().IsValid() )
			return;

		if ( _possessGate.TryGetValue( c.Id, out var gate ) && gate > 0f )
			return;
		_possessGate[c.Id] = PossessCooldown;

		if ( pawn.WorldPosition.Distance( target.WorldPosition ) > PossessRange )
			return;

		// A released pawn prop: claim it as-is. The registry Remove is the idempotency guard — it succeeds for
		// exactly one caller, so the loser of a same-frame race returns here and keeps their hunter.
		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
		{
			if ( !ReleasedProps.Remove( hider.GameObject.Id ) )
				return; // already claimed (or still being worn)

			HandOver( c, pawn, hider, assignOwnership: true );
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

		HandOver( c, pawn, converted.Components.Get<HiderController>(), assignOwnership: false );
	}

	// The shared possession tail: swap the claimant's hunter for the prop. The host's bookkeeping callback runs
	// FIRST (it still needs the doomed hunter pawn — the face snapshot). assignOwnership is false for a
	// freshly-converted clone (it NetworkSpawned already owned by the claimant); true for a released pawn prop,
	// which is UNOWNED after its release's DropOwnership — and everything that maps a body to a player
	// (RosterIdOf → the roster pips, own-prop lookups) reads Network.Owner, so even a host self-claim assigns.
	void HandOver( Connection c, GameObject hunterPawn, HiderController prop, bool assignOwnership )
	{
		Host.OnClaimGranted( c, hunterPawn, prop );
		hunterPawn.Destroy();

		if ( assignOwnership && Networking.IsActive )
			prop.GameObject.Network.AssignOwnership( c );

		// Tell the claimant (and only them) to resume control once the ownership change lands on their machine.
		// Their copy consumes it in OnUpdate — acting inside the RPC could race the ownership packet.
		using ( Rpc.FilterInclude( c ) )
		{
			prop.BeginPossession();
		}
	}

	// Host-only. Turn a scene-placed sculpture into a live prop pawn: clone the prop prefab dressed in the
	// scene shape's brushes, positioned so the clay lands EXACTLY where it stood (the pawn root sits upright at
	// the shape's feet; any tilt/scale the scene object carried moves onto the disguise child — the shape must
	// never move on its own), then remove the original everywhere. Spawned owned by the claimant with ClearOwner
	// orphan mode, so the prop outlives a leaver and can be released back into the world.
	GameObject ConvertSceneProp( SdfSculpture sculpture, Connection owner )
	{
		var prefab = Host?.PropPrefab;
		if ( !prefab.IsValid() )
		{
			Log.Warning( "PropClaims: the host mode supplied no prop prefab — can't convert a scene prop." );
			return null;
		}

		var sceneT = sculpture.WorldTransform;
		var feet = Sdf.TryGetBounds( sculpture.Brushes, out var b )
			? sceneT.PointToWorld( new Vector3( b.Center.x, b.Center.y, b.Mins.z ) )
			: sceneT.Position;
		var rootT = new Transform( feet, Rotation.FromYaw( sceneT.Rotation.Yaw() ) );

		var pawn = prefab.Clone( new CloneConfig( rootT, startEnabled: false, name: $"Claimed Prop {owner.DisplayName}" ) );
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
		_converted.Add( pawn.Id );
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

	/// <summary>Host-only: hand a prop off into the world as claimable scenery. Dormant on the host (which keeps
	/// simulating it — gravity and ground-snap still settle it), ownership dropped (the ex-owner's copy becomes
	/// a proxy; StopControl tears down their edit state), then registered released — in THIS component's [Sync]
	/// registry, whose replication doesn't depend on the pawn's just-changed ownership.</summary>
	public void Release( HiderController hider )
	{
		if ( !hider.IsValid() )
			return;

		hider.ReleaseControl();
		if ( Networking.IsActive && hider.GameObject.Network.Active )
			hider.GameObject.Network.DropOwnership();
		ReleasedProps[hider.GameObject.Id] = true;
	}

	/// <summary>Where a hunter appears when its player releases a prop in place: stepped back from the prop
	/// along the caller's view (so the prop they just placed is right in front of them), clear of the disguise's
	/// hull by its own horizontal radius plus a body's worth of margin — a pawn spawned inside the released
	/// disguise's collider gets solver-shoved, sometimes through the floor — grounded on whatever the prop
	/// stands on.</summary>
	public Transform HunterSpotClearOf( HiderController hider, float viewYaw )
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
}
