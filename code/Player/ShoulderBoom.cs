using System;

namespace Mimiclay;

/// <summary>
/// The over-the-shoulder third-person camera, shared by the hunter (<see cref="HunterController"/>) and the free cam
/// (<see cref="SpectatorController"/>) so the two behave identically:
/// <list type="bullet">
/// <item><b>Aim-locked boom</b> — the camera sits behind the eye at <see cref="Distance"/>, offset over the right
/// shoulder (<see cref="Shoulder"/>) and up (<see cref="Rise"/>), looking along the aim. One sphere sweep pulls it in
/// at walls; clay never pushes it (<see cref="WorldOnly"/>, the same rule as the prop's orbit boom).</item>
/// <item><b>First ↔ third blend</b> — the boom eases in and out (<see cref="BlendSpeed"/>) instead of cutting, and
/// <see cref="Blend"/> tells the owner how far out the view is (0 = on the eye).</item>
/// <item><b>Alt-orbit</b> — while Walk (alt) is held in third person, the mouse swings the boom around the eye
/// with the aim frozen (alt+RMB dollies, alt+MMB pans up/down), and on release it snaps back to the aim.</item>
/// </list>
/// A plain object the owner keeps and drives from its own update: <see cref="UpdateAltOrbit"/> with the live aim,
/// then <see cref="Place"/> for this frame's camera pose.
/// </summary>
public sealed class ShoulderBoom
{
	/// <summary>Default boom tuning — the hunter prefab's values. The hunter's properties start from these, and an
	/// owner with no hunter to copy (the free cam of a prop or a spectator) uses them as they are.</summary>
	public const float DefaultDistance = 120f, DefaultShoulder = 30f, DefaultRise = 0.675f, DefaultBlendSpeed = 8f;

	/// <summary>Boom length behind the eye (world units).</summary>
	public float Distance { get; set; } = DefaultDistance;

	/// <summary>Sideways offset, positive = over the RIGHT shoulder.</summary>
	public float Shoulder { get; set; } = DefaultShoulder;

	/// <summary>Vertical offset above the eye.</summary>
	public float Rise { get; set; } = DefaultRise;

	/// <summary>Only world geometry pulls the boom in — clay (<see cref="SdfCollider.ClayTag"/>) never does.</summary>
	public bool WorldOnly { get; set; } = true;

	/// <summary>How fast the view eases between first and third person (exponential, per second).</summary>
	public float BlendSpeed { get; set; } = DefaultBlendSpeed;

	/// <summary>How far out the view is: 0 = first person (on the eye), 1 = fully on the boom.</summary>
	public float Blend => _blend;

	/// <summary>Is the camera off the eye at all (third person, or easing to/from it)?</summary>
	public bool OffEye => _blend > 0f;

	/// <summary>Is an alt-orbit swinging the camera off the aim right now?</summary>
	public bool AltOrbiting => _altOrbiting;

	// Alt-orbit feel, matched to the prop's play camera (OrbitCameraController defaults: ZoomSpeed 0.01, PanSpeed 1 →
	// distance × 0.001 per pixel). Distance clamps roughly bracket the boom's own range.
	const float AltZoomSpeed = 0.01f;
	const float AltMinDistance = 20f, AltMaxDistance = 400f;

	float _blend;
	Angles _altOrbitAngles;
	bool _altOrbiting;
	float _altDistance;  // the alt-orbit's dolly, seeded from Distance and discarded on release
	Vector3 _altPan;     // the alt-orbit's vertical pan, likewise

	/// <summary>Snap the blend (no ease) — for an owner that must start already in or out.</summary>
	public void SnapBlend( bool thirdPerson ) => _blend = thirdPerson ? 1f : 0f;

	/// <summary>Advance the alt-orbit gesture. Seeded from the live aim on the frame alt is grabbed, so the camera
	/// never jumps — it starts exactly where the shoulder boom was and swings from there; release snaps cleanly
	/// back to the aim. <paramref name="allowed"/> is the owner's gate (third person, playing, no cursor overlay).
	/// Owners freeze their own aim while <see cref="AltOrbiting"/>.</summary>
	public void UpdateAltOrbit( bool allowed, Angles aim )
	{
		bool want = allowed && Input.Down( "Walk" );

		if ( want && !_altOrbiting )
		{
			_altOrbitAngles = aim;
			_altDistance = Distance;
			_altPan = Vector3.Zero;
		}
		_altOrbiting = want;

		if ( !_altOrbiting )
			return;

		// Alt+RMB dollies, alt+MMB pans up/down — the prop's exact drag gestures (exponential zoom so it feels even at
		// any distance; pan scaled by distance, along the swung camera's up). Each is exclusive with the orbit
		// rotation, so a drag never also spins the view.
		if ( Input.Down( "Attack2" ) )
		{
			_altDistance = (_altDistance * MathF.Pow( 1f + AltZoomSpeed, Mouse.Delta.y )).Clamp( AltMinDistance, AltMaxDistance );
			return;
		}

		if ( Input.Down( "CameraPan" ) )
		{
			_altPan += _altOrbitAngles.ToRotation().Up * Mouse.Delta.y * (_altDistance * 0.001f);
			return;
		}

		var look = Input.AnalogLook;
		_altOrbitAngles = new Angles(
			(_altOrbitAngles.pitch + look.pitch).Clamp( -89f, 89f ),
			_altOrbitAngles.yaw + look.yaw,
			0f );
	}

	/// <summary>This frame's camera pose: eases the blend toward <paramref name="thirdPerson"/>, then places the boom
	/// behind <paramref name="eye"/> along <paramref name="aim"/> (or the alt-orbit's angles), swept so it can't see
	/// through walls. The sweep ignores <paramref name="ignore"/> / <paramref name="ignore2"/> hierarchies (the
	/// owner's own colliders) and the charades stage fence.</summary>
	public Transform Place( Scene scene, Vector3 eye, Angles aim, bool thirdPerson, GameObject ignore, GameObject ignore2 = null )
	{
		float want = thirdPerson ? 1f : 0f;
		_blend = MathX.Lerp( _blend, want, 1f - MathF.Exp( -BlendSpeed * Time.Delta ) );
		if ( MathF.Abs( _blend - want ) < 0.005f )
			_blend = want;

		// The camera ROTATION rides the boom angles, so an alt-orbit looks back along the boom at the owner.
		var rot = (_altOrbiting ? _altOrbitAngles : aim).ToRotation();
		if ( _blend <= 0f )
			return new Transform( eye, rot );

		// y is LEFT in s&box, so the right-shoulder offset goes in negated. During an alt-orbit the boom length is the
		// live dolly and the anchor carries the pan (both discarded on release), and the shoulder/rise offsets scale
		// WITH the dolly (1 at the seed distance): the whole boom vector shrinks/grows proportionally, so zooming moves
		// along a straight ray toward the owner instead of sliding past beside a fixed offset. The blend scales the
		// whole boom too, so the view glides straight out from (and back into) the eye.
		float dist = _altOrbiting ? _altDistance : Distance;
		var anchor = _altOrbiting ? eye + _altPan : eye;
		float offsetScale = _altOrbiting ? dist / MathF.Max( Distance, 1f ) : 1f;
		var desired = anchor + rot * (new Vector3( -dist, -Shoulder * offsetScale, Rise * offsetScale ) * _blend);

		// Traced from the anchor with a small radius so the near plane can't peek through a wall the ray barely
		// misses. Triggers aren't hit by default, so other hunters' head colliders can't yank the boom in.
		var trace = scene.Trace.Ray( anchor, desired )
			.Radius( 8f )
			.WithoutTags( CharadesStageFence.WallTag );
		if ( ignore.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( ignore );
		if ( ignore2.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( ignore2 );
		if ( WorldOnly )
			trace = trace.WithoutTags( SdfCollider.ClayTag );

		var tr = trace.Run();
		return new Transform( tr.Hit ? tr.EndPosition : desired, rot );
	}
}
