using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// Being GRABBED while wearing this prop (creative — see PropClaims.Grab, "players' props"). The body stays ours and
// simulates HERE, so being carried is local physics on our own machine: the camera follows a body that never waits
// on the network, and edit mode keeps working on a shape we still own. The host relays the holder's pose
// (ReceiveGrabTarget) and we chase it with the same hold the host uses for scenery (DriveHeld). While held we can't
// move; Space breaks free (EscapeGrab) and stands us straight back up. If the holder lets go instead, we're a loose
// object — thrown props fly — until we move or jump, which stands us up (SettleUpright: the clay stays exactly where
// it landed, the root goes upright) and hands back to walking with whatever speed we had.
public sealed partial class HiderController
{
	Transform? _grabbedTarget;  // the latest pose the holder asked for (null until the first lands)
	bool _grabbedObject;        // held, or tumbling since a holder let go — an object, not a character
	bool _escapeSent;           // we jumped free; the host's Held row lingers a round-trip
	List<PhysicsFilter> _grabbedFilters;
	System.Guid _grabbedFilterHolder;
	float? _grabCamLift;        // camera pivot height above the clay centre while an object (see UpdateGrabbedCamera)

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

		if ( holder is { } h && !_escapeSent )
		{
			// Space breaks free: ask the host to end the hold and stand up right away — no waiting on the reply.
			if ( _jumpQueued )
			{
				_jumpQueued = false;
				_escapeSent = true;
				PropClaims.Current?.EscapeGrab( GameObject );
				RecoverFromGrab();
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

	// Back to being a character: root upright (tumble folded into the disguise), keeping the speed we had.
	void RecoverFromGrab()
	{
		var velocity = Body.IsValid() ? Body.Velocity : Vector3.Zero;
		_grabbedObject = false;
		_grabbedTarget = null;
		ClearGrabbedFilters();
		SettleUpright();
		_velocity = velocity; // SettleUpright parks it keyframed at rest; walking picks this back up next tick
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
