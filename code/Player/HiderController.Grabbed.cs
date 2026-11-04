using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// Being GRABBED while wearing this prop (see PropClaims.Grab, "players' props"). Two flavours:
//
// CREATIVE: the body stays ours and simulates HERE, so being carried is local physics on our own machine: the camera
// follows a body that never waits on the network, and edit mode keeps working on a shape we still own. The host relays
// the holder's pose (ReceiveGrabTarget) and we chase it with the same hold the host uses for scenery (DriveHeld).
//
// PROP HUNT (the inspect grab): the body is LENT to the hunter holding it (PropClaims.BorrowedBodies), whose machine
// simulates it — held, then settling after the drop until it's at rest (StepLentBody) — so it answers the hunter's hand
// with no lag, exactly like a held decoy. Our copy is a proxy meanwhile: we keep the camera on it and our keys watched
// (UpdateBorrowedView). It comes back WITH its exact pose (ReceiveHandback), and we don't move until we have both the
// body and the pose, whichever lands first (StepHandback) — then we stand straight up as a character, mouse turning it.
//
// Creative, while held we can't move; jump or a movement key breaks free (EscapeGrab) and stands us straight back up.
// If the holder lets go instead, we're a loose object — thrown props fly — until we move or jump, which stands us up
// (SettleUpright: the clay stays exactly where it landed, the root goes upright) and hands back to walking with
// whatever speed we had.
public sealed partial class HiderController
{
	Transform? _grabbedTarget;  // the latest pose the holder asked for (null until the first lands)
	bool _grabbedObject;        // held, or tumbling since a holder let go — an object, not a character
	bool _escapeSent;           // we jumped free; the host's Held row lingers a round-trip
	bool _moveEscapeQueued;     // a movement key went down this frame while held (latched for the fixed step)
	bool _moveWasDown;          // …edge detection for it: holding W through the grab isn't a press
	bool _wasBorrowed;          // we watched our body lent out — the first owned tick after is the hand-back
	bool _escapeJump;           // that escape (while lent out) was SPACE: jump on the hand-back (see EscapeJump)
	(Vector3 Position, Rotation Rotation, Vector3 Velocity, bool Escaped)? _handback; // the pose it came back at
	RealTimeSince _sinceOwnedBack; // owned again but no pose yet: how long we've waited for it (see StepHandback)
	bool _awaitingHandback;
	bool _simLent;              // THIS machine is simulating a body lent to it (the holder)
	bool _lentReported;         // …and has reported it at rest (FinishBorrow sent once)
	RealTimeSince _lentStill;   // …how long it's been still since the hold ended
	List<PhysicsFilter> _grabbedFilters;
	System.Guid _grabbedFilterHolder;
	float? _grabCamLift;        // camera pivot height above the clay centre while an object (see UpdateGrabbedCamera)

	/// <summary>Host → the wearer of a lent body (filtered — see PropClaims.HandBack): it's yours again, at exactly this
	/// pose. <paramref name="escaped"/> = you broke free (moving at <paramref name="velocity"/> — zero from the host).</summary>
	[Rpc.Broadcast]
	public void ReceiveHandback( Vector3 position, Rotation rotation, Vector3 velocity, bool escaped )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;
		TakeHandback( position, rotation, velocity, escaped );
	}

	/// <summary>The hand-back pose, applied on our first owned fixed step (StepHandback) — straight from the host when it
	/// wears the body itself.</summary>
	internal void TakeHandback( Vector3 position, Rotation rotation, Vector3 velocity, bool escaped )
		=> _handback = (position, rotation, velocity, escaped);

	// Fixed step, on the machine that owns the body. True = this tick is handled.
	//  - HOLDER, simulating a lent body: held (OnFixedUpdate's held branch) and then, once the hold ends, a loose
	//    tumble until it's been still for a moment — then report the resting pose so the host hands it back.
	//  - WEARER, just given it back: wait until the pose is here too, put the body exactly there, stand up.
	// Also cleans up after a lend on the holder once the body has gone (it's a proxy here again).
	bool StepHandback()
	{
		if ( _handback is { } back )
		{
			_handback = null;
			_wasBorrowed = false;
			_awaitingHandback = false;
			_escapeSent = PropClaims.HolderOf( this ) is not null; // a lingering Held row mustn't re-grab us
			_grabbedTarget = null;
			PlaceExactly( back.Position, back.Rotation );
			RecoverFromGrab();
			_velocity = back.Velocity;
			if ( back.Escaped && _escapeJump )
				EscapeJump();
			_escapeJump = false;
			return false;
		}

		// Owned again, pose not here yet: stay exactly where we are (keyframed) — a short wait beats a jump. If it never
		// comes, carry on from here.
		if ( _wasBorrowed )
		{
			if ( !_awaitingHandback )
			{
				_awaitingHandback = true;
				_sinceOwnedBack = 0f;
			}
			if ( _sinceOwnedBack < HandbackWait )
			{
				SetPhysicsMode( PhysicsMode.Character );
				return true;
			}
			_wasBorrowed = false;
			_awaitingHandback = false;
			RecoverFromGrab();
			return false;
		}

		return false;
	}

	// Put a body we've just been handed exactly here — the GameObject AND its physics body, with the walk's own state
	// cleared. Setting the transform alone wasn't enough: while we were a proxy the walk had the body PARKED (left to its
	// physics sleep at the spot it rested BEFORE the grab), and on taking over the physics body pulled the prop straight
	// back there — measured as a 13-unit snap a ping after every hand-back.
	void PlaceExactly( Vector3 position, Rotation rotation )
	{
		WorldPosition = position;
		WorldRotation = rotation;
		Transform.ClearInterpolation();
		if ( Body.IsValid() && Body.PhysicsBody is { } pb && pb.IsValid() )
		{
			pb.Position = position;
			pb.Rotation = rotation;
			pb.Velocity = Vector3.Zero;
			pb.AngularVelocity = Vector3.Zero;
		}
		ResetMover(); // unparks, clears the walk's velocity and ground
	}

	// How long a returned body waits for its pose (see StepHandback) before carrying on regardless.
	const float HandbackWait = 0.5f;

	// Speed (u/s) under which a dropped lent body counts as still, and for how long, before it's handed back.
	const float LentRestSpeed = 4f;
	const float LentRestTime = 0.25f;

	// Holder, fixed step: a lent body nobody holds any more — tumble on (a throw still flies) until it's been still for a
	// moment, then report where it rests (once). The host hands it back; the wearer doesn't move until it has the pose.
	void StepLentBody()
	{
		_simLent = true;
		ClearGrabbedFilters();
		SetPhysicsMode( PhysicsMode.Loose );

		if ( Body.Velocity.Length > LentRestSpeed || Body.AngularVelocity.Length > 0.2f )
			_lentStill = 0f;
		if ( _lentReported || _lentStill < LentRestTime )
			return;

		_lentReported = true;
		PropClaims.Current?.FinishBorrow( GameObject, WorldPosition, WorldRotation );
	}

	// Holder: a lent prop put down FROZEN (creative's LMB) is at rest the moment it's let go — report it at once.
	void ReportLentFrozen()
	{
		_simLent = true;
		if ( _lentReported )
			return;
		_lentReported = true;
		ClearGrabbedFilters();
		PropClaims.Current?.FinishBorrow( GameObject, WorldPosition, WorldRotation );
	}

	/// <summary>Host, taking back creative's released scenery from its holder (PropClaims.HandBack): put it exactly where
	/// the holder left it. It stays an object — loose or frozen, as it was put down (the next fixed step picks the mode
	/// from the registries) — rather than standing up like a player's body.</summary>
	internal void TakeBackScenery( Vector3 position, Rotation rotation ) => PlaceExactly( position, rotation );

	// Holder, held: the body never collides with OUR hunter (the host's filter for held scenery, made here because this
	// is the machine simulating it).
	void HoldLentBody()
	{
		_simLent = true;
		_lentReported = false;
		_lentStill = 0f;
		if ( Connection.Local is { } me )
			IgnoreGrabHolder( me.Id );
	}

	// Holder, the lend over (the body's a proxy here again): leave nothing of the hold behind on this copy.
	void EndLentBody()
	{
		if ( !_simLent )
			return;
		_simLent = false;
		_lentReported = false;
		ClearGrabbedFilters();
		SetPhysicsMode( PhysicsMode.Character );
	}

	// Per frame, wherever we read our own keys (owner, or watching our borrowed body): latch a FRESH movement press.
	void WatchMoveEscape()
	{
		bool down = !Input.AnalogMove.IsNearZeroLength;
		if ( down && !_moveWasDown && ControlActive )
			_moveEscapeQueued = true;
		_moveWasDown = down;
	}

	// Our body, borrowed by the host for a hold (we're a proxy of it): keep the camera on it, and break free on jump or
	// a movement key.
	void UpdateBorrowedView()
	{
		_grabbedObject = true; // the camera follows the clay centre while we're an object (UpdateGrabbedCamera)
		_wasBorrowed = true;   // the first owned tick after this is the hand-back (StepHandback)

		WatchMoveEscape();
		bool jumped = ControlActive && Input.Pressed( "jump" );
		bool escape = _moveEscapeQueued || jumped;
		_moveEscapeQueued = false;
		if ( escape && !_escapeSent )
		{
			_escapeSent = true;
			_escapeJump = jumped; // jumps once the body is ours again (StepHandback)
			PropClaims.Current?.EscapeGrab( GameObject );
		}

		if ( !FreeCamming )
			UpdateCamera();
	}

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

		bool escapePressed = _jumpQueued || _moveEscapeQueued;
		_moveEscapeQueued = false;

		if ( holder is { } h && !_escapeSent )
		{
			// Jump or a movement key breaks free: ask the host to end the hold and stand up right away — no waiting on
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
		// last tick (ReadTurn), and while the body was lent out the walk never ran — its last facing was from before the
		// grab, so the reset above read as one big mouse turn: measured as a 51° spin (and a 5.5u swing of the clay) a
		// ping after every hand-back.
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
