using System;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Diagnostic for the third-person head/body jitter. Drop on the hunter pawn root, play in third
/// person, then MOVE AND ROTATE (the only combination that reproduces it).
///
/// The insight it tests: in third person every visible part of the pawn is rigidly boomed to the
/// camera — head, body and the gun arm are all placed from the same eye, and the camera sits at
/// eye + rot * boom. So each part's CAMERA-RELATIVE position is a mathematical constant. It cannot
/// change. Any frame where it does change is the bug, and WHICH part moves tells us the cause:
///
///   head / body / arm all move together  → the camera itself is popping (boom trace, see camDist)
///   only some of them move               → those parts' placement is desynced from the camera's
///   camDist changes                      → the boom collision trace is snapping the camera in/out
///                                          (our DriveCamera does this UNSMOOTHED; the stock
///                                          controller lerps _cameraDistance precisely to avoid it)
///
/// Sampled at PreRender so it reads the FINAL rendered state, after every writer has had its say.
/// Console logs a burst whenever a part shifts more than the threshold, including that frame's
/// movement and look deltas so the move+rotate correlation is visible in the numbers.
/// </summary>
public sealed class JitterProbe : Component
{
	/// <summary>World units of camera-relative movement that counts as a jitter event.</summary>
	[Property] public float Threshold { get; set; } = 0.5f;

	PlayerController _pc;
	GameObject _head, _body, _arm;

	Vector3 _lastHead, _lastBody, _lastArm, _lastPawnPos;
	Angles _lastAngles;
	float _lastCamDist;
	bool _seeded;

	float _maxHead, _maxBody, _maxArm, _maxCamDist;

	protected override void OnStart()
	{
		_pc = Components.Get<PlayerController>();
		_head = GameObject.Children.FirstOrDefault( c => c.Name == "Head" );
		_body = GameObject.Children.FirstOrDefault( c => c.Name == "Body" );
		_arm = GameObject.Children.FirstOrDefault( c => c.Name == "Shoulder" );
	}

	protected override void OnPreRender()
	{
		var cam = Scene.Camera;
		if ( !cam.IsValid() || !_pc.IsValid() )
			return;

		// Camera-relative position of each part. Constant every frame if placement is coherent.
		var inv = cam.WorldRotation.Inverse;
		Vector3 camPos = cam.WorldPosition;
		Vector3 head = _head.IsValid() ? inv * (_head.WorldPosition - camPos) : Vector3.Zero;
		Vector3 body = _body.IsValid() ? inv * (_body.WorldPosition - camPos) : Vector3.Zero;
		Vector3 arm = _arm.IsValid() ? inv * (_arm.WorldPosition - camPos) : Vector3.Zero;

		// Boom length: how far the camera sits from the pawn. Changes = the collision trace is
		// pulling the camera in and out, which our DriveCamera applies with no smoothing at all.
		float camDist = (camPos - _pc.WorldPosition).Length;

		Vector3 pawnPos = _pc.WorldPosition;
		Angles angles = _pc.EyeAngles;

		if ( !_seeded )
		{
			_seeded = true;
			_lastHead = head; _lastBody = body; _lastArm = arm;
			_lastPawnPos = pawnPos; _lastAngles = angles; _lastCamDist = camDist;
			return;
		}

		float dHead = (head - _lastHead).Length;
		float dBody = (body - _lastBody).Length;
		float dArm = (arm - _lastArm).Length;
		float dCamDist = MathF.Abs( camDist - _lastCamDist );

		// This frame's movement and look, so the move+rotate correlation shows up in the log.
		float moved = (pawnPos - _lastPawnPos).Length;
		float turned = MathF.Abs( angles.yaw - _lastAngles.yaw ) + MathF.Abs( angles.pitch - _lastAngles.pitch );

		_maxHead = MathF.Max( _maxHead, dHead );
		_maxBody = MathF.Max( _maxBody, dBody );
		_maxArm = MathF.Max( _maxArm, dArm );
		_maxCamDist = MathF.Max( _maxCamDist, dCamDist );

		if ( dHead > Threshold || dBody > Threshold || dArm > Threshold || dCamDist > Threshold )
		{
			Log.Warning( $"JITTER  head={dHead:F2} body={dBody:F2} arm={dArm:F2} camDist={dCamDist:F2}" +
				$"  | moved={moved:F2} turned={turned:F2}deg dt={Time.Delta * 1000f:F1}ms" );
		}

		DebugOverlay.ScreenText( new Vector2( 20, 200 ),
			$"camera-relative drift (should be ~0)\n" +
			$"  head {dHead:F2}  max {_maxHead:F2}\n" +
			$"  body {dBody:F2}  max {_maxBody:F2}\n" +
			$"  arm  {dArm:F2}  max {_maxArm:F2}\n" +
			$"boom len {camDist:F1}  drift {dCamDist:F2}  max {_maxCamDist:F2}\n" +
			$"moved {moved:F2}  turned {turned:F2}deg",
			size: 14, flags: TextFlag.LeftTop );

		_lastHead = head; _lastBody = body; _lastArm = arm;
		_lastPawnPos = pawnPos; _lastAngles = angles; _lastCamDist = camDist;
	}
}
