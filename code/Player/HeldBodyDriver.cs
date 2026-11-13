using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// The grab's HOLD, for any body being carried — a player's prop (HiderController) and a decoy (RoundManager.Grab) run
/// the same one, on whichever machine simulates the hold, so the two can't be told apart by how they move in the hand.
///
/// Through a spring joint in free air, with velocities while touching. The joint, as sandbox's PhysicsHold: a keyframed
/// anchor body moved toward the target each tick, tied to the body's mass centre by a control joint with linear +
/// angular springs. The springs are solved INSIDE the physics step alongside the contacts, so a body pressed into a wall
/// settles against it instead of the solver and a per-tick velocity write fighting (the jitter); the force is capped by
/// the body's mass, so a heavy one swings with weight; and the stepwise targets a client streams are smoothed by the
/// spring. Touching anything (<see cref="ContactWindow"/>) drops the joint for the velocity chase — which is what lays a
/// prop FLUSH when pressed into a surface and SLIDES it when dragged; leaving contact remakes the joint at the body's
/// current pose, so the hand-over doesn't kick. A held body has no friction (it slides rather than grips).
/// </summary>
public sealed class HeldBodyDriver
{
	// The velocity chase: how hard the body is pulled toward the target (1/s), how fast it turns, and the speed cap that
	// keeps a fast swing from tunnelling it through a thin wall (the joint's anchor moves no faster either).
	public const float Pull = 18f;
	public const float Turn = 14f;
	public const float MaxSpeed = 1800f;

	// Force ≤ mass × this (torque scaled from the inertia). Sandbox uses 700000; that out-pulls the contacts, so a fast
	// swing dragged a small prop straight through a 2u wall. Measured on that wall: 80000 tunnels, 40000 and 30000 hold —
	// and 30000 still settles a 90° swing in 0.2s, no slower than 700000.
	const float MaxAccel = 30000f;
	const float LinearHz = 32f;
	const float AngularHz = 64f;
	const float Damping = 4f;

	/// <summary>Seconds since the last contact event that still count as touching (see the class summary). Measured (pot
	/// tilted 30°, pressed into the floor): chase → 1°; joint with a soft rotation spring → 0° but dragged props ROLL
	/// (knob 41–47° off); joint with the rotation chase only → stuck at 26°, at any linear stiffness.</summary>
	public const float ContactWindow = 0.1f;

	const float HeldFriction = 0f; // (a climbing prop's — ApplyFriction; a HELD one keeps its own unless mimi_hold_friction 0)

	// ── The chase (both holds: SimHoldDriver in the sim, Drive's velocity mode here) ─────────────────────────────
	// Was: velocity SET to gap × Pull every tick, about the body's origin, with the spin about its mass centre. Three problems
	// (user, 2026-11-12): a long prop didn't turn about where it was grabbed (the pivot was its middle, or somewhere between
	// origin and mass centre); a target that jumped (an obstruction sweep snapping in) took the body from rest to 1800 u/s in
	// one tick and threw a carried container's contents everywhere; and the held body had no friction, so nothing sat on it.
	//  • GRIP: the point the holder took it by (root-local). The grip goes to its target; the mass centre moves however the
	//    spin needs it to for that (v = grip's velocity − ω × (grip − mass centre)), so the body turns about the grip.
	//  • ACCELERATION LIMIT: the velocity changes by at most HoldAccel per second (spin: TurnAccel), and the approach speed is
	//    capped at √(2·a·gap) so it can always brake in time — a jump becomes a quick, smooth catch-up, no overshoot.
	//  • FRICTION: kept (HeldFrictionKept) — the chase sets the velocities every tick, so a wall can't make it grip or roll.
	// Replicated: the sim's hold must run the same numbers on the host and every predicting client.

	[ConVar( "mimi_hold_accel", ConVarFlags.Replicated, Help = "Grab: the most a held prop's velocity changes per second (u/s²) — lower = gentler on what it carries." )]
	public static float HoldAccel { get; set; } = 4000f;

	[ConVar( "mimi_hold_turn_accel", ConVarFlags.Replicated, Help = "Grab: the most a held prop's spin changes per second (rad/s²)." )]
	public static float TurnAccel { get; set; } = 120f;

	[ConVar( "mimi_hold_friction", ConVarFlags.Replicated, Help = "Grab: a held prop keeps its own friction (1 — things rest on it) or has none (0 — the old slide)." )]
	public static bool HeldFrictionKept { get; set; } = true;

	/// <summary>One tick of the hold chase: the linear (mass centre) and angular velocity that move <paramref name="gripNow"/>
	/// toward <paramref name="gripTarget"/> and the rotation toward <paramref name="rotTarget"/>, from the body's current
	/// motion, within the acceleration limits (see above).</summary>
	// The braking speed's safety factor: per-tick steps brake a little late, so the full √(2·a·gap) overshot ~4u on a 150u jump.
	const float BrakeMargin = 0.85f;

	public static (Vector3 Linear, Vector3 Angular) Chase( Vector3 massCentre, Vector3 gripNow, Vector3 gripTarget,
		Rotation rotNow, Rotation rotTarget, Vector3 linearNow, Vector3 angularNow, float dt )
	{
		float accel = MathF.Max( HoldAccel, 1f ), turnAccel = MathF.Max( TurnAccel, 0.1f );

		var turn = TurnToward( rotNow, rotTarget );
		float angle = turn.Length;
		var spinWant = angle > 1e-5f ? turn / angle * MathF.Min( angle * Turn, MathF.Sqrt( 2f * turnAccel * angle ) ) : Vector3.Zero;
		var spin = angularNow + (spinWant - angularNow).ClampLength( turnAccel * dt );

		// The limit is on the GRIP's velocity, not the mass centre's: limiting the centre let the grip drift 14u through a
		// quick 90° turn of a broom held by its end (the centre couldn't swing round fast enough). The far end's sweep is
		// bounded by the spin limit instead.
		var arm = gripNow - massCentre;
		var gripVelNow = linearNow + Vector3.Cross( angularNow, arm );
		var gap = gripTarget - gripNow;
		float d = gap.Length;
		float speed = MathF.Min( MathF.Min( d * Pull, MathF.Sqrt( 2f * accel * d ) * BrakeMargin ), MaxSpeed );
		var gripWant = d > 1e-4f ? gap / d * speed : Vector3.Zero;
		var gripVel = gripVelNow + (gripWant - gripVelNow).ClampLength( accel * dt );
		// The mass centre's velocity that lands the grip exactly where gripVel takes it after this tick's TURN (the arm swung
		// by spin·dt about the mass centre) — the straight-line ω × arm left the grip off by the arc's sag every tick, ~8u
		// through a quick 90° turn at arm's length.
		float stepAngle = spin.Length * dt;
		var swung = stepAngle > 1e-6f ? Rotation.FromAxis( spin / spin.Length, stepAngle.RadianToDegree() ) * arm : arm;
		var linear = (gripVel + (arm - swung) / dt).ClampLength( MaxSpeed );
		return (linear, spin);
	}

	Sandbox.Physics.ControlJoint _joint;
	PhysicsBody _anchor;
	PhysicsBody _on;        // the body the joint was made on — a re-made body needs a new joint
	Vector3 _massCentre;    // its local mass centre then — a scale commit moves it, so the joint is remade
	readonly Dictionary<Collider, float?> _friction = new();

	/// <summary>One tick of the hold: steer <paramref name="body"/> toward <paramref name="target"/> (its ROOT pose).
	/// <paramref name="touching"/>: a contact within <see cref="ContactWindow"/>. Never teleports, so the solver still
	/// stops it at walls and it leaves the hand with whatever motion it had.</summary>
	public void Drive( Rigidbody body, Transform target, bool touching, Vector3? grip = null )
	{
		if ( !body.IsValid() )
			return;
		if ( body.Sleeping )
			body.Sleeping = false;

		if ( !HeldFrictionKept )
			ApplyFriction( body.GameObject );

		var pb = body.PhysicsBody;
		// The point it's held by (root-local): the holder's grip, else its mass centre as before.
		var gripLocal = grip ?? (pb is not null && pb.IsValid() ? pb.LocalMassCenter : Vector3.Zero);

		if ( HiderController.GrabJoint && !touching )
		{
			DriveJoint( body, target, gripLocal );
			return;
		}
		ReleaseJoint();
		if ( pb is null || !pb.IsValid() )
			return;

		var dt = MathF.Max( Time.Delta, 0.001f );
		var rootNow = body.WorldTransform.WithScale( 1f );
		var (linear, angular) = Chase( pb.MassCenter, rootNow.PointToWorld( gripLocal ), target.WithScale( 1f ).PointToWorld( gripLocal ),
			body.WorldRotation, target.Rotation, body.Velocity, body.AngularVelocity, dt );
		body.Velocity = linear;
		body.AngularVelocity = angular;
	}

	void DriveJoint( Rigidbody body, Transform target, Vector3 gripLocal )
	{
		var pb = body.PhysicsBody;
		if ( pb is null || !pb.IsValid() )
			return;

		// Tied at the GRIP (it used to be the mass centre): the body hangs and turns about where it was taken.
		if ( _joint is null || _on != pb || !_massCentre.AlmostEqual( gripLocal, 0.01f ) )
		{
			ReleaseJoint();
			_on = pb;
			_massCentre = gripLocal;
			_anchor = new PhysicsBody( pb.World ) { BodyType = PhysicsBodyType.Keyframed, AutoSleep = false };
			_anchor.Transform = new Transform( body.WorldTransform.WithScale( 1f ).PointToWorld( _massCentre ), body.WorldRotation );

			var inertia = pb.Inertia;
			float maxForce = pb.Mass * MaxAccel;
			float maxTorque = MaxAccel * MathF.Sqrt( pb.Mass * MathF.Max( inertia.x, MathF.Max( inertia.y, inertia.z ) ) );

			_joint = Sandbox.Physics.PhysicsJoint.CreateControl(
				new Sandbox.Physics.PhysicsPoint( _anchor ), new Sandbox.Physics.PhysicsPoint( pb, _massCentre ) );
			_joint.LinearSpring = new Sandbox.Physics.PhysicsSpring( LinearHz, Damping, maxForce );
			_joint.AngularSpring = new Sandbox.Physics.PhysicsSpring( AngularHz, Damping, maxTorque );
		}

		// The target is the ROOT pose; the joint holds the mass centre, so the anchor goes where that lands — at most
		// MaxSpeed units/s rather than jumping there. A fast swing can put the target behind a wall the aim sweep never
		// saw (it sweeps along the aim, not along the prop's path); the joint then pulls the body at it, and only the
		// MaxAccel cap keeps the contact winning.
		var dt = MathF.Max( Time.Delta, 0.001f );
		var want = target.PointToWorld( _massCentre );
		var from = _anchor.Position;
		_anchor.Transform = new Transform( from + (want - from).ClampLength( MaxSpeed * dt ), target.Rotation );
	}

	/// <summary>Drop the joint (contact, the hold ending, the body going away).</summary>
	public void ReleaseJoint()
	{
		_joint?.Remove();
		_joint = null;
		if ( _anchor is not null && _anchor.IsValid() )
			_anchor.Remove();
		_anchor = null;
		_on = null;
	}

	/// <summary>No friction on every collider under <paramref name="root"/>; each one's own override is remembered for
	/// <see cref="RestoreFriction"/> (a rebuilt collider is a new component, picked up on the next tick).</summary>
	public void ApplyFriction( GameObject root )
	{
		if ( !root.IsValid() )
			return;
		foreach ( var c in root.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( _friction.ContainsKey( c ) )
				continue;
			_friction[c] = c.Friction;
			c.Friction = HeldFriction;
		}
	}

	public void RestoreFriction()
	{
		foreach ( var (c, friction) in _friction )
		{
			if ( c.IsValid() )
				c.Friction = friction;
		}
		_friction.Clear();
	}

	/// <summary>The hold is over: joint gone, friction back.</summary>
	public void Release()
	{
		ReleaseJoint();
		RestoreFriction();
	}

	/// <summary>Shortest-arc axis × angle (radians) from one rotation to another.</summary>
	public static Vector3 TurnToward( Rotation from, Rotation to )
	{
		var q = to * from.Inverse;
		if ( q.w < 0f )
			q = new Rotation( -q.x, -q.y, -q.z, -q.w );
		var sinHalf = MathF.Sqrt( q.x * q.x + q.y * q.y + q.z * q.z );
		if ( sinHalf < 1e-5f )
			return Vector3.Zero;
		var angle = 2f * MathF.Atan2( sinHalf, q.w );
		return new Vector3( q.x, q.y, q.z ) / sinHalf * angle;
	}
}
