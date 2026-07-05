using System;

namespace Mimiclay;

/// <summary>
/// Noclip / fly camera for play mode that behaves like the s&amp;box editor camera:
/// hold right-mouse to look, WASD to fly, Q/E (or Ctrl/Space) for down/up,
/// hold Shift to move faster, and scroll the wheel to change the base speed.
/// Drop this on a GameObject that has a <see cref="CameraComponent"/>.
/// </summary>
[Title( "Fly Camera" )]
[Category( "Camera" )]
[Icon( "videocam" )]
public sealed class FlyCamera : Component
{
	/// <summary>Base movement speed in units/second, before the speed multiplier.</summary>
	[Property, Range( 50f, 2000f )] public float MoveSpeed { get; set; } = 400f;

	/// <summary>How much faster movement is while holding Shift.</summary>
	[Property, Range( 1f, 10f )] public float FastMultiplier { get; set; } = 3f;

	/// <summary>Mouse look sensitivity (degrees per mouse unit).</summary>
	[Property, Range( 0.01f, 1f )] public float LookSensitivity { get; set; } = 0.1f;

	/// <summary>Multiplier applied to MoveSpeed each scroll-wheel notch.</summary>
	[Property, Range( 1.01f, 2f )] public float ScrollSpeedStep { get; set; } = 1.15f;

	// Tracked separately from GameObject.WorldRotation so roll never accumulates.
	Angles _angles;

	protected override void OnEnabled()
	{
		_angles = WorldRotation.Angles();
	}

	protected override void OnUpdate()
	{
		// Editor camera only looks/moves while the right mouse button is held.
		var looking = Input.Down( "attack2" );

		Mouse.Visibility = looking ? MouseVisibility.Hidden : MouseVisibility.Auto;

		if ( !looking )
			return;

		Look();
		AdjustSpeed();
		Move();
	}

	void Look()
	{
		var delta = Mouse.Delta;
		_angles.pitch += delta.y * LookSensitivity;
		_angles.yaw -= delta.x * LookSensitivity;
		_angles.pitch = _angles.pitch.Clamp( -89f, 89f );
		_angles.roll = 0f;

		WorldRotation = _angles.ToRotation();
	}

	void AdjustSpeed()
	{
		var scroll = Input.MouseWheel.y;
		if ( scroll == 0f )
			return;

		MoveSpeed = (MoveSpeed * MathF.Pow( ScrollSpeedStep, scroll )).Clamp( 10f, 10000f );
	}

	void Move()
	{
		var move = Vector3.Zero;

		if ( Input.Down( "forward" ) ) move += Vector3.Forward;
		if ( Input.Down( "backward" ) ) move += Vector3.Backward;
		if ( Input.Down( "left" ) ) move += Vector3.Left;
		if ( Input.Down( "right" ) ) move += Vector3.Right;

		// Vertical: Q/E for down/up, plus Space/Ctrl as alternates.
		if ( Input.Down( "jump" ) ) move += Vector3.Up;
		if ( Input.Down( "duck" ) ) move += Vector3.Down;

		var speed = MoveSpeed * (Input.Down( "run" ) ? FastMultiplier : 1f);

		// Move relative to where the camera is looking.
		var wish = WorldRotation * move.Normal;
		WorldPosition += wish * speed * Time.Delta;
	}
}
