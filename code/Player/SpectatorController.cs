using System;

namespace Mimiclay;

/// <summary>
/// The free-fly camera an ELIMINATED player is left with — Teams mode's bench: a found prop is out for the
/// round, their pawn pops under the caught puff, and this takes over the view. Spawned LOCALLY by
/// <see cref="RoundManager"/> (never networked — a ghost nobody else can see, collide with or shoot) and
/// destroyed by the manager or the end-of-round scene change.
///
/// Deliberately the HIDER'S free cam feel — the same speed/momentum/slide tuning as the F-key cam on
/// <see cref="HiderController"/> — but self-contained: the pawn that owned that orbit rig is gone, so this
/// drives the one shared camera directly (<see cref="MainCamera.Set"/>, transform only, per the shared-camera
/// rule). It takes off from wherever the camera was the moment the pawn died, so the caught puff → ghost cam
/// handoff is one continuous view.
/// </summary>
[Title( "Spectator Controller" )]
[Category( "Mimiclay" )]
[Icon( "visibility" )]
public sealed class SpectatorController : Component
{
	/// <summary>Base fly speed, world units/sec (matches the hider free cam).</summary>
	[Property] public float FlySpeed { get; set; } = 600f;

	/// <summary>Speed multiplier while "run" is held.</summary>
	[Property] public float FastMultiplier { get; set; } = 3f;

	/// <summary>Collision radius — solid like the hider's free cam, gliding along whatever it hits rather
	/// than stopping dead (or phasing out of the map).</summary>
	[Property] public float CollisionRadius { get; set; } = 8f;

	/// <summary>Momentum ease time (s): keys steer a wish velocity and the actual velocity eases toward it
	/// (accelerate in, glide to a stop). 0 = instant, no glide.</summary>
	[Property, Range( 0f, 1f )] public float Smoothing { get; set; } = 0.3f;

	/// <summary>Field of view while spectating — the free cam's wide view.</summary>
	[Property] public float SpectateFov { get; set; } = 90f;

	Vector3 _pos;
	Angles _angles;
	Vector3 _velocity;

	protected override void OnStart()
	{
		// Take off from the live camera — the exact view the player had the moment they were caught.
		_pos = MainCamera.Position;
		_angles = MainCamera.Angles with { roll = 0f };
	}

	protected override void OnUpdate()
	{
		// A cursor overlay owns the mouse — hold the view rather than navigate behind the menu (the same
		// gate every other camera driver uses).
		if ( PauseMenu.IsOpen || RoundSetup.IsOpen )
		{
			Apply();
			return;
		}

		Mouse.Visibility = MouseVisibility.Hidden;

		_angles += Input.AnalogLook;
		_angles = _angles with { pitch = Math.Clamp( _angles.pitch, -89f, 89f ), roll = 0f };

		var move = Vector3.Zero;
		if ( Input.Down( "forward" ) ) move += Vector3.Forward;
		if ( Input.Down( "backward" ) ) move += Vector3.Backward;
		if ( Input.Down( "left" ) ) move += Vector3.Left;
		if ( Input.Down( "right" ) ) move += Vector3.Right;

		// Horizontal + pitch fly relative to view (look up, fly up); vertical (jump/duck) is world-space on
		// top — the standard noclip/spectator feel, matching the hider free cam.
		var wish = Vector3.Zero;
		if ( move.LengthSquared > 0.001f )
			wish += _angles.ToRotation() * move.Normal;
		if ( Input.Down( "jump" ) ) wish += Vector3.Up;
		if ( Input.Down( "duck" ) ) wish += Vector3.Down;

		var wishVel = wish * FlySpeed * (Input.Down( "run" ) ? FastMultiplier : 1f);
		_velocity = Smoothing > 0.001f
			? Vector3.Lerp( _velocity, wishVel, 1f - MathF.Exp( -Time.Delta * 3f / Smoothing ) )
			: wishVel;

		var from = _pos;
		_pos = Slide( _pos, _velocity * Time.Delta );

		// Re-derive velocity from the distance actually covered, so a wall bleeds off the blocked component
		// (the slide keeps the tangential part) instead of momentum silently pressing into geometry.
		if ( Time.Delta > 0f )
			_velocity = (_pos - from) / Time.Delta;

		Apply();
	}

	void Apply()
	{
		MainCamera.Set( _pos, _angles.ToRotation() );
		MainCamera.SetFov( SpectateFov ); // eased by MainCamera's own FovLerpSpeed
	}

	// Multi-bump slide, the free-cam shape (see HiderController.SlideFreeCam): keep re-deflecting off every
	// surface met along the step so corners glide instead of sticking, holding a small skin back from each
	// hit so the next iteration's trace starts clear of the surface.
	const int SlideIterations = 4;
	const float SlideSkin = 0.25f;

	Vector3 Slide( Vector3 from, Vector3 delta )
	{
		var pos = from;
		var remaining = delta;

		for ( int i = 0; i < SlideIterations && remaining.LengthSquared > 0.0001f; i++ )
		{
			var to = pos + remaining;
			var tr = Scene.Trace.Ray( pos, to ).Radius( CollisionRadius ).Run();

			if ( !tr.Hit )
			{
				pos = to;
				break;
			}

			// Advance to just short of the hit (the skin), then project whatever distance was LEFT onto the
			// hit plane for the next iteration to attempt.
			var stepLen = remaining.Length;
			var keepFrac = stepLen > SlideSkin ? MathF.Max( 0f, tr.Fraction - SlideSkin / stepLen ) : 0f;
			pos += remaining * keepFrac;

			var leftover = remaining * (1f - tr.Fraction);
			remaining = leftover - Vector3.Dot( leftover, tr.Normal ) * tr.Normal;
		}

		return pos;
	}
}
