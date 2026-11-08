using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// Being GRABBED while wearing this prop (see PropClaims.Grab, "players' props"), when the sim doesn't carry it (the sim
// off, or our shape mid-edit — HiderController.SimHoldApplies; otherwise the sim holds it and StepSimWalking presents it).
//
// The body stays ours and simulates HERE, so being carried is local physics on our own machine: the camera follows a
// body that never waits on the network, and edit mode keeps working on a shape we still own. The host relays the
// holder's pose (ReceiveGrabTarget) and we chase it with the same hold the host uses for scenery (DriveHeld).
//
// While held we can't move; Space breaks free (EscapeGrab) and stands us straight back up.
// If the holder lets go instead, we're a loose object — thrown props fly — until we move or jump, which stands us up
// (SettleUpright: the clay stays exactly where it landed, the root goes upright) and hands back to walking with
// whatever speed we had.
public sealed partial class HiderController
{
	Transform? _grabbedTarget;  // the latest pose the holder asked for (null until the first lands)
	bool _grabbedObject;        // held, or tumbling since a holder let go — an object, not a character
	bool _escapeSent;           // we jumped free; the host's Held row lingers a round-trip
	List<PhysicsFilter> _grabbedFilters;
	System.Guid _grabbedFilterHolder;
	float? _grabCamLift;        // camera pivot height above the clay centre while an object (see UpdateGrabbedCamera)

	// Held by another player: for the first moment after the grab nothing breaks free (user, 2026-11-06: "a short window after
	// being grabbed where you can't escape even by pressing jump"); the host enforces the same window (PropClaims.EscapeGrab).
	// Only Space breaks free — the move keys never do (user, 2026-11-06).
	System.Guid? _lastHolder;
	RealTimeSince _sinceHeld = 999f;

	// Owner, every frame (HiderController.OnUpdate): the moment a hold starts — a new holder, or the same hunter again after a
	// gap (tracked every frame, not only while held, or a regrab by the same hunter had no edge and no window: measured).
	void TrackHold()
	{
		var holder = PropClaims.HolderOf( this );
		if ( holder is { } && holder != _lastHolder )
			_sinceHeld = 0f;
		_lastHolder = holder;
	}

	bool EscapeGraceActive() => PropClaims.HolderOf( this ) is { } && _sinceHeld < PropClaims.EscapeHoldGrace;

	/// <summary>Is this body being carried, or tumbling from a carry, on its owner's machine?</summary>
	public bool IsGrabbedObject => _grabbedObject;

	/// <summary>Owner: the pose the holder wants this body at.</summary>
	internal void SetGrabTarget( Transform target ) => _grabbedTarget = target;

	/// <summary>Host → this body's wearer (filtered — see PropClaims.RelayGrabTarget): the holder's latest pose.</summary>
	[Rpc.Broadcast( NetFlags.UnreliableNoDelay )]
	public void ReceiveGrabTarget( Vector3 position, Rotation rotation )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;
		if ( IsProxy )
			return;
		SetGrabTarget( new Transform( position, rotation ) );
	}

	// Owner, fixed step, for a body someone wears (not scenery). True = this tick is handled (held or tumbling).
	bool StepGrabbed()
	{
		var holder = PropClaims.HolderOf( this );
		if ( holder is null )
		{
			_escapeSent = false;
			_grabbedTarget = null;
		}

		if ( EscapeGraceActive() )
			_jumpQueued = false; // (a press inside the no-escape window is simply lost)
		bool escapePressed = _jumpQueued;

		if ( holder is { } h && !_escapeSent )
		{
			// Jump breaks free: ask the host to end the hold and stand up right away — no waiting on
			// the reply.
			if ( escapePressed )
			{
				bool jumped = _jumpQueued;
				_jumpQueued = false;
				_escapeSent = true;
				PropClaims.Current?.EscapeGrab( GameObject );
				RecoverFromGrab();
				if ( jumped )
					EscapeJump();
				return false;
			}

			if ( _climbing )
				EndClimb();

			_grabbedObject = true;
			IgnoreGrabHolder( h );
			SetPhysicsMode( PhysicsMode.Held );
			DriveHeld( _grabbedTarget ??= WorldTransform ); // hang where we are until the first pose lands
			return true;
		}

		ClearGrabbedFilters();
		if ( !_grabbedObject )
			return false;

		// Let go of: a loose object (a throw keeps flying) until we move or jump.
		if ( ControlActive && (_jumpQueued || !Input.AnalogMove.IsNearZeroLength) )
		{
			_jumpQueued = false;
			RecoverFromGrab();
			return false;
		}

		SetPhysicsMode( PhysicsMode.Loose );
		return true;
	}

	// Space out of a hold: a real jump, launched as if we stood on the floor — the walk's own launch speed, size-scaled
	// like any jump (HiderController.Walk) — even though we were hanging in someone's hands. Sideways we keep what the
	// hold was doing. After RecoverFromGrab: the walk picks _velocity up on its next tick, and the jump bookkeeping keeps
	// its ground spring from pulling us straight back down.
	void EscapeJump()
	{
		_velocity = _velocity.WithZ( JumpPower * System.MathF.Sqrt( SizeSpeedScale ) );
		IsGrounded = false;
		_groundObject = null;
		_sinceWalkJump = 0f;
		_airFromJump = true; // a jump at a wall grabs it (see HiderController.Climb)
	}

	// Back to being a character: root upright (tumble folded into the disguise), keeping the speed we had.
	void RecoverFromGrab()
	{
		var velocity = Body.IsValid() ? Body.Velocity : Vector3.Zero;
		_grabbedObject = false;
		_grabbedTarget = null;
		ClearGrabbedFilters();
		SettleUpright();
		_velocity = velocity; // SettleUpright parks it keyframed at rest; walking picks this back up next tick

		// Face where the body actually points now. The mouse kept steering the facing while we were carried (and the
		// holder turned the body meanwhile), so the walk would otherwise swing the prop round to that stale yaw the
		// moment we move — the little jump. The mouse turns it from here, as usual.
		_bodyYaw = WorldRotation.Angles().yaw;

		// …and re-seed the walk's turn tracking from it. The walk turns the body by how much the facing CHANGED since its
		// last tick (ReadTurn), and while the body was carried the walk never ran — its last facing was from before the
		// grab, so the reset above read as one big mouse turn (measured as a 51° spin and a 5.5u swing of the clay).
		_turnSeeded = false;
	}

	// Our body and the holder's never collide (up close the two shove each other — the same reason the host
	// filters held scenery against its holder). Made here because this is the machine simulating the body.
	void IgnoreGrabHolder( System.Guid holder )
	{
		if ( _grabbedFilters is { Count: > 0 } && _grabbedFilterHolder == holder )
			return;

		ClearGrabbedFilters();
		_grabbedFilterHolder = holder;
		var pawn = Scene.GetAllComponents<HunterController>().FirstOrDefault( x => x.Network.Owner?.Id == holder )?.GameObject;
		_grabbedFilters = PropClaims.FilterAgainst( GameObject, pawn );
	}

	void ClearGrabbedFilters()
	{
		if ( _grabbedFilters is null )
			return;
		foreach ( var filter in _grabbedFilters )
		{
			if ( filter.IsValid() )
				filter.Destroy();
		}
		_grabbedFilters = null;
	}

	// The orbit pivot rides the disguise's origin, which swings round the clay as the body tumbles — so while we're
	// an object the pivot follows the clay CENTRE instead, at the height above it we had when the grab began. On
	// standing up the disguise origin has moved relative to the clay; the difference goes into the rig's pan so
	// the camera doesn't jump.
	void UpdateGrabbedCamera()
	{
		if ( !_body.IsValid() || !_orbit.IsValid() )
			return;

		var centre = PropClaims.PopSpot( GameObject );
		if ( _grabbedObject )
		{
			_grabCamLift ??= _body.WorldPosition.z - centre.z;
			_orbit.FollowPointOverride = centre + Vector3.Up * _grabCamLift.Value;
			return;
		}

		if ( _grabCamLift is { } lift )
		{
			_orbit.AddPanOffset( Vector3.Up * (lift - (_body.WorldPosition.z - centre.z)) );
			_grabCamLift = null;
		}
		_orbit.FollowPointOverride = null;
	}
}
