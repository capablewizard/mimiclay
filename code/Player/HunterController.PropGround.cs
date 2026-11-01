using System;
using Sandbox.Movement;

namespace Mimiclay;

// Standing on a prop pawn (user, 2026-11-01: "even flat ones I roll off"; it gives away that it's not a real prop). A
// walking prop stands on its move hull, a shape with ZERO friction (HiderController.UseHullCollision — props slide along
// walls), and a prop is a dynamic body. On dynamic ground the engine walk brakes and steers through the ground's
// friction (GroundUsesContactFriction), and with no keys held it doesn't brake at all, trusting the physics contact —
// which is frictionless. So a hunter that walked onto a prop kept its speed and slid off the far side.
//
// The engine's GroundFriction can't be trusted to spot it: it read 0 in the kitchen but the clay surface's 0.8 on the
// Dev Prop Hunt bots, while the contact stayed frictionless either way. So any prop pawn underfoot counts, as well as
// any ground reading (near) frictionless.
//
// Owner-side, between the engine's ground check (PostPhysicsStep) and its next AddVelocity (PrePhysicsStep): floor the
// ground friction, and with no keys held on dynamic ground brake as the walk would on ordinary ground. Relative to the
// ground's own velocity, so a moving prop still carries you.
public sealed partial class HunterController
{
	const float MinPropGroundFriction = 0.8f; // the clay surface's own friction

	void GripSlipperyGround()
	{
		if ( !_controller.IsOnGround || _controller.Mode is not MoveModeWalk walk )
			return;

		bool slippery = _controller.GroundFriction < MinPropGroundFriction;
		if ( !slippery && !IsPropPawn( _controller.GroundObject ) )
			return;

		if ( slippery )
			_controller.GroundFriction = MinPropGroundFriction;

		// Static (or still keyframed) ground: the walk brakes itself, now with real friction. On dynamic ground it only
		// does while you're steering.
		if ( !_controller.GroundIsDynamic || !_controller.WishVelocity.IsNearZeroLength )
			return;

		var body = _controller.Body;
		var normal = _controller.GroundNormal;

		// Hold on a slope too: the walk cancels gravity's pull down the surface only while you steer (the contact's
		// friction is meant to do it otherwise), so on a domed prop's shoulder you slid off even standing still.
		if ( body.Gravity )
		{
			var gravity = Scene.PhysicsWorld.Gravity * body.GravityScale;
			body.Velocity -= (gravity - normal * Vector3.Dot( gravity, normal )) * Time.Delta;
		}
		var rel = body.Velocity - _controller.GroundVelocity;
		var along = rel - normal * Vector3.Dot( rel, normal ); // keep whatever presses into / lifts off the surface

		// MovementMath.Friction (engine-internal), with the walk's own Friction and StopSpeed.
		float speed = along.Length;
		if ( speed < 0.1f )
			return;
		float surface = MathF.Min( MinPropGroundFriction * 1.25f, 1f );
		float drop = MathF.Max( speed, walk.StopSpeed ) * walk.Friction * _controller.BrakePower * surface * Time.Delta;
		float kept = MathF.Max( speed - drop, 0f );
		body.Velocity += along * (kept / speed - 1f);
	}

	static bool IsPropPawn( GameObject go )
	{
		for ( var g = go; g.IsValid(); g = g.Parent )
			if ( g.Components.Get<HiderController>().IsValid() )
				return true;
		return false;
	}
}
