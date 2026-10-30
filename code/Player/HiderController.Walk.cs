using System;

namespace Mimiclay;

// Walking: the prop is a DYNAMIC physics body — a "floating" controller (user, 2026-10-30: the old keyframed, hand-swept
// mover "really doesn't work well with other rigid bodies … lots of props are rigid bodies and we'll need to navigate
// around and over them"). It shoved loose props with infinite force, saw them where they were before the physics step
// (then overlapped them and had to unstick), and couldn't be pushed back. Now:
//
//  • The collision shape is the move hull with the bottom cut off (ScaledStep high, see PropMoveHull.AttachTo): steps,
//    small debris and slopes pass UNDER it. A ride spring holds the body so the hull's real base sits on the ground —
//    the visible prop stays flush.
//  • The ground comes from the cut-off FOOT slab swept down from where the floating shape starts (PropMoveHull.SweepFoot):
//    anything under that part of the footprint, a step included, sets the ride height, so stepping up is the spring lifting.
//  • Walking is a velocity change capped by an acceleration (WalkAccel), i.e. a force; while something pushes back, only
//    WalkPushAccel — a light prop gets shoved, one heavier than us blocks us, and things can push us back. Standing on a
//    loose prop presses our weight into it; we ride along only on bodies at least half our mass.
//  • Gravity, jump and air control use the movement numbers (Gravity, FallGravityMult, JumpPower, AirControl). Pitch and
//    roll are locked; the mouse turns it (ApplySurfaceTurn). Overlaps are the solver's job — no unstick, no buried mode.
//  • At rest with no input it PARKS: left to physics (which can sleep it) until input, a new shape, or being moved wakes it.
//
// Climbing grabs from here (TryAirGrab) and hands back when it lets go; the possession cling runs first. Everything that
// isn't held, loose, frozen, climbing or being sculpted in place walks: players, bots, released scenery.
public sealed partial class HiderController
{
	/// <summary>Fastest the prop may fall (u/s).</summary>
	[Property, Group( "Movement" )] public float MaxFallSpeed { get; set; } = 3000f;

	/// <summary>The most a step of walking can change our velocity (u/s²) — the push we can put on
	/// things. Higher = snappier starts and stops, and heavier props get shoved.</summary>
	[Property, Group( "Walk" )] public float WalkAccel { get; set; } = 3000f;

	/// <summary>The acceleration (u/s²) we keep up while something is pushing BACK (a box we're shoving) — our
	/// push force is this × our mass. A light prop moves easily; one heavier than us, sitting on the floor, barely does.</summary>
	[Property, Group( "Walk" )] public float WalkPushAccel { get; set; } = 500f;

	/// <summary>How fast the ride spring closes the gap to the ground (1/s).</summary>
	[Property, Group( "Walk" )] public float RideSpring { get; set; } = 18f;

	/// <summary>Fastest the ride spring lifts us (u/s) — a tall step is climbed at this rate at most.</summary>
	[Property, Group( "Walk" )] public float RideMaxLift { get; set; } = 400f;

	/// <summary>Ground this far (u) BELOW the hull still holds us (down steps and slopes, over bumps).</summary>
	[Property, Group( "Walk" )] public float RideSnap { get; set; } = 6f;

	RealTimeSince _sinceWalkJump = 999f;
	Vector3 _walkCommanded; // the horizontal velocity we set last tick — what contacts took off it is the push-back
	RealTimeSince _sinceWalkPushBack = 999f; // pushing sticks for a moment, or a stalled push flips back to full acceleration
	int _parkedHullVersion; // the hull a park was made with — a new shape wakes it

	// A mantle's guided carry (see TryMantle, TryMantleFromUnder): for _mantleCarryTime after the climb hands over, until the ground spring finds
	// the top, move in over it at _mantleSpeed and up to _mantleTopZ (root height resting on it) — no gravity.
	Vector3 _mantleDir;
	float _mantleTopZ, _mantleSpeed;
	RealTimeSince _sinceMantle = 999f;
	float _mantleCarryTime;
	bool MantleCarrying => _sinceMantle < _mantleCarryTime;

	// Start the guided carry: in along `dir` at climbing pace, up to `topZ`, for at most `time` seconds (see above).
	void StartMantleCarry( Vector3 dir, float topZ, float time )
	{
		_mantleDir = dir;
		_mantleTopZ = topZ;
		_mantleSpeed = MathF.Max( ScaledClimbSpeed, WalkSpeed * SizeSpeedScale * 0.5f );
		_mantleCarryTime = time;
		_sinceMantle = 0f;
		_velocity = dir * _mantleSpeed;
		IsGrounded = false;
		Unpark();
	}
	const float WalkJumpGrace = 0.2f; // after a jump, the ground spring doesn't grab us straight back

	void StepWalk( bool controlled )
	{
		var pb = Body.IsValid() ? Body.PhysicsBody : null;
		if ( pb is null || !pb.IsValid() )
			return;
		_hull.Refresh( _collider, Scene.PhysicsWorld );
		if ( !_hull.Ready )
			return; // no shape yet

		var pos = WorldPosition;
		var rot = WorldRotation;
		if ( _clingCheckPending && TryClingOnPossess( pos, rot ) )
			return;

		float clearance = ScaledStep( StepHeight );
		SetPhysicsMode( PhysicsMode.Walking );
		UseHullCollision( true, clearance );

		if ( controlled )
			IsCrouching = Input.Down( "duck" );
		var wish = controlled ? ApplyLeash( ReadWish(), pullIn: false ) : Vector3.Zero;
		bool jump = controlled && _jumpQueued;
		_jumpQueued = false;
		float turn = controlled ? ReadTurn() : 0f;

		// Parked: left to physics and its sleep. Woken by input, a new shape, or being moved (pushed, launched, carried).
		bool idle = wish.IsNearZeroLength && !jump && MathF.Abs( turn ) < 0.001f;
		if ( _parked )
		{
			if ( idle && _hull.Version == _parkedHullVersion && pb.Velocity.LengthSquared < 4f && pb.AngularVelocity.LengthSquared < 0.01f )
				return;
			Unpark();
		}
		if ( Body.Sleeping )
			Body.Sleeping = false;

		float dt = MathF.Max( Time.Delta, 0.001f );
		var v = pb.Velocity;

		// Ground: the hull's cut-off FOOT (see PropMoveHull.SweepFoot) swept down from where the floating collision shape
		// starts, to RideSnap below its base. (The whole hull started solid on a box stack the floating shape was already
		// shoving: "no ground", so it fell until the floating shape sat on the floor — the train sunk 18u into it.)
		var from = pos + Vector3.Up * clearance;
		var tr = _hull.SweepFoot( MoveTrace, new Transform( from, rot ), new Transform( from - Vector3.Up * (clearance + RideSnap), rot ), clearance );
		// Still starting inside something (pressed hard into a wall as we land): a ray down from the hull's centre instead.
		if ( tr.StartedSolid )
		{
			var c = pos + rot * _hull.Centre;
			var rayFrom = c.WithZ( pos.z + _hull.LocalBounds.Mins.z + clearance ); // where the foot slab starts
			var ray = MoveTrace.Ray( rayFrom, rayFrom - Vector3.Up * (clearance + RideSnap) ).Run();
			if ( !ray.StartedSolid )
				tr = ray;
		}
		float gap = tr.Hit ? tr.Distance - clearance : float.MaxValue; // + = base above the ground, − = ground above base
		bool ground = tr.Hit && !tr.StartedSolid && WalkableHit( tr ) && _sinceWalkJump > WalkJumpGrace
			&& (IsGrounded ? gap <= RideSnap : gap <= 1f && v.z <= 50f); // landing needs touching; staying allows a step down
		// Still starting inside something while grounded: hold our height rather than fall into the floor.
		if ( tr.StartedSolid && IsGrounded && _sinceWalkJump > WalkJumpGrace )
		{
			ground = true;
			gap = 0f;
		}

		// What we stand on. A platform (keyframed) or a body at least half our mass carries us along; a lighter loose prop
		// doesn't — inheriting its velocity while our feet push it the other way was a feedback loop that flung a 3 kg box
		// 680u out from under the train.
		var gb = ground && !tr.StartedSolid && tr.Body is { } b && b.IsValid() && b.BodyType != PhysicsBodyType.Static ? b : null;
		float myMass = MathF.Max( pb.Mass, 0.01f );
		float groundMassRatio = gb is null ? 0f : gb.BodyType == PhysicsBodyType.Dynamic ? gb.Mass / myMass : 1f;
		Vector3 groundVel = gb is not null && groundMassRatio >= 0.5f ? gb.GetVelocityAtPoint( tr.HitPosition ) : Vector3.Zero;

		if ( ground )
		{
			_airFromJump = false; // on the ground: no jump in progress
			IsGrounded = true;
			if ( !tr.StartedSolid )
			{
				_groundNormal = tr.Normal;
				SetGround( tr );
			}

			// Horizontal: chase the wish (riding along with whatever we stand on) — the GroundControl response,
			// but no harder than WalkAccel: that cap is what makes it a force rather than a teleport.
			var flat = v.WithZ( 0f );
			var target = wish.WithZ( 0f ) + groundVel.WithZ( 0f );
			if ( MantleCarrying && Vector3.Dot( wish, _mantleDir ) < _mantleSpeed )
				target += _mantleDir * (_mantleSpeed - MathF.Max( Vector3.Dot( wish, _mantleDir ), 0f )); // finish getting onto the top
			var dv = (target - flat) * (1f - MathF.Exp( -GroundControl * dt ));
			// Pushing against something (it took a share of last tick's command off us): only WalkPushAccel more, so we lean
			// into it with a body's worth of force instead of shoving it with the full walking acceleration.
			var cmd = _walkCommanded.WithZ( 0f );
			if ( cmd.Length > 5f && Vector3.Dot( cmd - flat, cmd.Normal ) > cmd.Length * 0.2f )
				_sinceWalkPushBack = 0f;
			bool pushing = _sinceWalkPushBack < 0.25f && !(target - flat).IsNearZeroLength;
			dv = dv.ClampLength( (pushing ? WalkPushAccel : WalkAccel) * dt );
			flat += dv;

			// Vertical: the ride spring puts the hull's base on the ground (a step under the footprint lifts us).
			float vz = groundVel.z + Math.Clamp( -gap * RideSpring, -RideMaxLift, RideMaxLift );

			if ( jump )
			{
				// Jump HEIGHT scales with size like walking speed does (user, 2026-10-30) — a knob hops, the train jumps
				// ~56u. Height goes with launch speed squared, so the speed scales by the square root.
				vz = groundVel.z + JumpPower * MathF.Sqrt( SizeSpeedScale );
				IsGrounded = false;
				_groundObject = null;
				_sinceWalkJump = 0f;
				_airFromJump = true; // a jump at a wall grabs it (see HiderController.Climb)
			}

			// A loose body we stand on carries our weight (capped at a few times its own, so a heavy prop presses a light one
			// down instead of crushing it out sideways) and the push of our feet, scaled down by how much lighter it is.
			if ( gb is not null && gb.BodyType == PhysicsBodyType.Dynamic )
			{
				float weight = MathF.Min( myMass, gb.Mass * 4f ) * Gravity * dt;
				var impulse = -Vector3.Up * weight - dv.WithZ( 0f ) * myMass * MathF.Min( groundMassRatio, 1f );
				gb.ApplyImpulseAt( tr.HitPosition, impulse );
			}

			v = flat.WithZ( vz );
		}
		else
		{
			IsGrounded = false;
			_groundObject = null;

			if ( MantleCarrying )
			{
				// Over a mantle's lip: in toward the top and up to its height, until the ground spring above takes it.
				float up = Math.Clamp( (_mantleTopZ - pos.z) * RideSpring, 0f, RideMaxLift );
				v = (_mantleDir * _mantleSpeed).WithZ( up );
			}
			else
			{
				// Airborne: AirControl and its own gravity (falling faster than rising, terminal speed).
				float t = 1f - MathF.Exp( -AirControl * dt );
				var flat = Vector3.Lerp( v.WithZ( 0f ), wish.WithZ( 0f ), t );
				float g = Gravity * (v.z < 0f ? FallGravityMult : 1f);
				v = flat.WithZ( MathF.Max( v.z - g * dt, -MaxFallSpeed ) );
			}
		}

		v = ApplyLeash( v, pullIn: true );
		pb.Velocity = v;
		_walkCommanded = v;
		_velocity = v; // for the climb's grab checks and anything else that reads the velocity

		// Park once there's nothing left to do: grounded on still ground, no input, at rest, at ride height.
		bool still = IsGrounded && idle && (v - groundVel).Length < 1f && MathF.Abs( gap ) < 0.5f && groundVel.Length < 0.5f;
		_stillTicks = still ? _stillTicks + 1 : 0;
		if ( _stillTicks >= ParkAfterTicks )
		{
			_parked = true;
			_parkedHullVersion = _hull.Version;
			pb.Velocity = Vector3.Zero;
			_velocity = Vector3.Zero;
		}

		ApplySurfaceTurn( turn );

		// Holding Space against a wall or ceiling in the air: grab it (see HiderController.Climb).
		if ( controlled && !IsGrounded && !MantleCarrying ) // not mid-mantle: that re-grabbed the wall we just left
			TryAirGrab( pos, rot, wish );
	}
}
