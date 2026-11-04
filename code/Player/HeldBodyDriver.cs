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

	const float HeldFriction = 0f;

	Sandbox.Physics.ControlJoint _joint;
	PhysicsBody _anchor;
	PhysicsBody _on;        // the body the joint was made on — a re-made body needs a new joint
	Vector3 _massCentre;    // its local mass centre then — a scale commit moves it, so the joint is remade
	readonly Dictionary<Collider, float?> _friction = new();

	/// <summary>One tick of the hold: steer <paramref name="body"/> toward <paramref name="target"/> (its ROOT pose).
	/// <paramref name="touching"/>: a contact within <see cref="ContactWindow"/>. Never teleports, so the solver still
	/// stops it at walls and it leaves the hand with whatever motion it had.</summary>
	public void Drive( Rigidbody body, Transform target, bool touching )
	{
		if ( !body.IsValid() )
			return;
		if ( body.Sleeping )
			body.Sleeping = false;

		ApplyFriction( body.GameObject );

		if ( HiderController.GrabJoint && !touching )
		{
			DriveJoint( body, target );
			return;
		}
		ReleaseJoint();

		var dt = MathF.Max( Time.Delta, 0.001f );
		body.Velocity = ((target.Position - body.WorldPosition) * MathF.Min( Pull, 1f / dt )).ClampLength( MaxSpeed );
		body.AngularVelocity = TurnToward( body.WorldRotation, target.Rotation ) * MathF.Min( Turn, 1f / dt );
	}

	void DriveJoint( Rigidbody body, Transform target )
	{
		var pb = body.PhysicsBody;
		if ( pb is null || !pb.IsValid() )
			return;

		if ( _joint is null || _on != pb || !_massCentre.AlmostEqual( pb.LocalMassCenter, 0.01f ) )
		{
			ReleaseJoint();
			_on = pb;
			_massCentre = pb.LocalMassCenter;
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
