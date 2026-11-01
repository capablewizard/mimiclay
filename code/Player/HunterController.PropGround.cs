using System;
using Sandbox.Movement;

namespace Mimiclay;

// Standing on a prop pawn (user, 2026-11-01: "even flat ones I roll off"). A walking prop stands on its move hull, a
// shape with ZERO friction (HiderController.UseHullCollision — props slide along walls), and a prop is a dynamic body.
// On dynamic ground the engine walk brakes and steers through the ground's friction (GroundUsesContactFriction), and
// with no keys held it doesn't brake at all, trusting the physics contact. At friction 0 a hunter that walked onto a
// prop kept its speed forever and slid off the far side, and couldn't steer while on it.
//
// So, owner-side, between the engine's ground check (PostPhysicsStep) and its next AddVelocity (PrePhysicsStep):
// floor the ground friction, and with no keys held brake as the walk would on ordinary ground. Relative to the
// ground's own velocity, so a moving prop still carries you.
public sealed partial class HunterController
{
	const float MinPropGroundFriction = 0.8f; // the clay surface's own friction

	void GripSlipperyGround()
	{
		if ( !_controller.IsOnGround || !_controller.GroundIsDynamic || _controller.GroundFriction >= MinPropGroundFriction
			|| _controller.Mode is not MoveModeWalk walk )
			return;

		_controller.GroundFriction = MinPropGroundFriction;

		if ( !_controller.WishVelocity.IsNearZeroLength )
			return; // the walk steers and brakes itself, now with real friction

		var body = _controller.Body;
		var normal = _controller.GroundNormal;
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
}
