using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// ── The grab (creative's gravity-gun pickup) ─────────────────────────────────────────────────────────────────
// Holding RMB on claimable clay carries it: the prop floats in front of the holder, E+mouse rotates it, R+mouse
// scales it, the wheel sets the distance; releasing RMB drops it and LMB throws it. A dropped prop becomes LOOSE — a free
// rigidbody (engine gravity, all rotation unlocked) instead of the upright character body released props
// normally are — so it tumbles, stacks and rolls. That's the point: a way to throw clay at the world and watch
// what the collider actually does.
//
// HOST-SIMULATED, like all released scenery. The prop never changes owner: the holder computes where they want
// it (a full root transform, scale included) and streams that to the host every frame (UpdateGrab, unreliable),
// and the host's HiderController chases it with velocities — so the held prop still collides with the world on
// its way, and keeps its momentum the moment it's let go (a swing becomes a throw). Holding one costs the host
// nothing it isn't already doing for scenery; a client holder sees their own hold one round-trip late.
//
// Scene clay is converted on the first grab exactly as an in-place sculpt converts it (host-owned released
// scenery), so everything after that only ever deals with prop pawns.
public sealed partial class PropClaims
{
	const string GrabOption = "claims.grab";

	/// <summary>May props be grabbed right now (claims open AND the host mode allows the grab)?</summary>
	public bool GrabOpen => ClaimsOpen && (Host?.GrabAllowed ?? false);

	/// <summary>Who is holding what: connection id → pawn GameObject id. One prop per player.</summary>
	[Sync] public NetDictionary<Guid, Guid> Held { get; private set; } = new();

	/// <summary>Props that were dropped from a grab and now simulate as free rigidbodies, by pawn GameObject id.
	/// Cleared when someone possesses the prop (it stands back up — see <see cref="HiderController.SettleUpright"/>).</summary>
	[Sync] public NetDictionary<Guid, bool> LooseProps { get; private set; } = new();

	/// <summary>Is anyone holding this prop?</summary>
	public static bool IsHeld( HiderController hider )
	{
		if ( !hider.IsValid() || !Current.IsValid() )
			return false;
		var id = hider.GameObject.Id;
		foreach ( var (_, pawn) in Current.Held )
		{
			if ( pawn == id )
				return true;
		}
		return false;
	}

	/// <summary>Is this prop loose — a free rigidbody since it was last dropped?</summary>
	public static bool IsLoose( HiderController hider )
		=> hider.IsValid() && Current.IsValid() && Current.LooseProps.ContainsKey( hider.GameObject.Id );

	/// <summary>The prop this machine's player holds (registry answer — the host may have refused or ended it).</summary>
	public static GameObject LocalHeld
		=> Current.IsValid() && Connection.Local is { } me && Current.Held.TryGetValue( me.Id, out var pawn )
			? Current.Scene.Directory.FindByGuid( pawn )
			: null;

	/// <summary>Is this machine's player holding (or asking to hold) a prop? E and R belong to the grab then.</summary>
	public static bool LocalGrabActive
		=> LocalGrabPending || (Current.IsValid() && Connection.Local is { } me && Current.Held.ContainsKey( me.Id ));

	/// <summary>Set by the hunter the instant it asks for a grab, until the host's row lands (or it times out).</summary>
	internal static bool LocalGrabPending => _grabPendingSince < 1f;
	static RealTimeSince _grabPendingSince = float.MaxValue;
	internal static void MarkGrabPending( bool pending ) => _grabPendingSince = pending ? 0f : float.MaxValue;

	// Host-only: the latest pose each holder asked for, by pawn id. Seeded with the prop's own transform at the
	// grab so it hangs still until the first stream lands.
	readonly Dictionary<Guid, Transform> _grabTargets = new();

	/// <summary>Host: the pose a held prop should chase, or false when nobody holds it.</summary>
	internal bool TryGetGrabTarget( HiderController hider, out Transform target )
	{
		target = default;
		return hider.IsValid() && IsHeld( hider ) && _grabTargets.TryGetValue( hider.GameObject.Id, out target );
	}

	/// <summary>Caller picks up the clay under their crosshair. Arbitrated like <see cref="RequestEdit"/>: a
	/// released pawn prop is taken as-is; scene clay is CONVERTED first (host-owned released scenery). Refused
	/// for anything a claim couldn't take — someone's body, a prop being sculpted, one already held.</summary>
	[Rpc.Host]
	public void RequestGrab( GameObject target ) => GrabFor( Rpc.Caller ?? Connection.Local, target );

	internal void GrabFor( Connection c, GameObject target )
	{
		var host = Host;
		if ( c is null || !target.IsValid() || host is null || !host.ClaimsAllowed || !host.GrabAllowed )
			return;

		var pawn = host.ClaimantPawn( c );
		if ( !pawn.IsValid() || !pawn.Components.Get<HunterController>().IsValid() )
			return;

		if ( _possessGate.TryGetValue( c.Id, out var gate ) && gate > 0f )
			return;
		_possessGate[c.Id] = PossessCooldown;

		if ( pawn.WorldPosition.Distance( target.WorldPosition ) > PossessRange )
			return;

		HiderController prop;
		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
		{
			if ( !IsClaimable( hider.DisguiseSculpture ) )
				return; // someone's body, being sculpted, or already held
			prop = hider;
		}
		else
		{
			var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
			if ( !IsClaimable( sculpture ) || !_claimedScene.Add( sculpture.GameObject.Id ) )
				return;

			var converted = ConvertSceneProp( sculpture, null, scenery: true );
			if ( !converted.IsValid() )
			{
				_claimedScene.Remove( sculpture.GameObject.Id );
				return;
			}

			prop = converted.Components.Get<HiderController>();
			prop.ReleaseControl();
			ReleasedProps[prop.GameObject.Id] = true;
		}

		DropFor( c ); // one prop per player
		Held[c.Id] = prop.GameObject.Id;
		_grabTargets[prop.GameObject.Id] = prop.WorldTransform;
		PlaySwapPop( PopSpot( prop.GameObject ) );
	}

	/// <summary>Holder → host, every frame while holding: the root transform (scale included) the prop should
	/// chase. Unreliable — the next frame's pose replaces a lost one. Ignored from anyone not holding it.</summary>
	[Rpc.Host( NetFlags.UnreliableNoDelay )]
	public void UpdateGrab( GameObject prop, Vector3 position, Rotation rotation, float scale )
	{
		var c = Rpc.Caller ?? Connection.Local;
		if ( c is null || !prop.IsValid() || !Held.TryGetValue( c.Id, out var id ) || id != prop.Id )
			return;

		_grabTargets[id] = new Transform( position, rotation, Math.Clamp( scale, MinGrabScale, MaxGrabScale ) );
	}

	/// <summary>Holder lets go. <paramref name="throwVelocity"/> is added to whatever the hold was already doing
	/// (zero for a plain drop — a swing still carries). The prop goes LOOSE.</summary>
	[Rpc.Host]
	public void DropGrab( Vector3 throwVelocity )
	{
		var c = Rpc.Caller ?? Connection.Local;
		var hider = HeldBy( c );
		DropFor( c );

		if ( hider.IsValid() && hider.PhysicsBody.IsValid() && !throwVelocity.IsNearZeroLength )
			hider.PhysicsBody.Velocity += throwVelocity.ClampLength( MaxThrowSpeed );
	}

	/// <summary>The grab's scale limits, relative to the prop's authored size.</summary>
	public const float MinGrabScale = 0.2f, MaxGrabScale = 5f;

	const float MaxThrowSpeed = 2000f;

	HiderController HeldBy( Connection c )
		=> c is not null && Held.TryGetValue( c.Id, out var id )
			? Scene.Directory.FindByGuid( id )?.Components.Get<HiderController>()
			: null;

	// Host-only: end a connection's hold. The prop stays where it is, released and now loose.
	void DropFor( Connection c )
	{
		if ( c is null || !Held.TryGetValue( c.Id, out var id ) )
			return;

		Held.Remove( c.Id );
		_grabTargets.Remove( id );
		if ( Scene.Directory.FindByGuid( id ).IsValid() )
			LooseProps[id] = true;
	}

	// Host-only, per frame (beside SweepEditors): a hold must belong to a connected player whose pawn is still a
	// hunter, on a prop that still exists. Anything else drops it where it is.
	void SweepHolders()
	{
		if ( Held.Count == 0 )
			return;

		foreach ( var (holder, pawnId) in Held.ToList() )
		{
			var conn = Connection.All.FirstOrDefault( x => x.Id == holder );
			var claimant = conn is not null ? Host?.ClaimantPawn( conn ) : null;
			bool holderIsHunter = claimant.IsValid() && claimant.Components.Get<HunterController>().IsValid();
			if ( holderIsHunter && Scene.Directory.FindByGuid( pawnId ).IsValid() )
				continue;

			Held.Remove( holder );
			_grabTargets.Remove( pawnId );
			if ( Scene.Directory.FindByGuid( pawnId ).IsValid() )
				LooseProps[pawnId] = true;
		}
	}
}
