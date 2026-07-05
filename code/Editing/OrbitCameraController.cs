using System;

namespace Mimiclay;

/// <summary>
/// Maya-style orbit camera. Hold Alt and drag: LMB orbits around the pivot, RMB dollies (zoom), MMB pans
/// (vertical). Drives the scene's main camera.
///
/// Two modes:
/// <list type="bullet">
/// <item><b>Free pivot</b> (<see cref="FollowTarget"/> null): the pivot is fixed in world space, seeded on
/// enable from the current view (optionally near <see cref="FocusHint"/>). Used for edit mode / the future
/// creative mode — enable it and it takes over the camera, disable to release.</item>
/// <item><b>Follow</b> (<see cref="FollowTarget"/> set): the pivot tracks that object every frame (plus
/// <see cref="FollowOffset"/> and any panning), so it works as a third-person gameplay camera with the
/// same alt navigation. Set <see cref="IgnoreCollision"/> to pull the boom in past geometry.</item>
/// </list>
/// Still knows nothing about prop hunt or the SDF system.
/// </summary>
[Title( "Orbit Camera Controller" )]
[Category( "Mimiclay" )]
[Icon( "3d_rotation" )]
public sealed class OrbitCameraController : Component
{
	[Property, Range( 0.05f, 2f )] public float OrbitSpeed { get; set; } = 0.3f;
	[Property, Range( 0.1f, 5f )] public float PanSpeed { get; set; } = 1.0f;
	[Property, Range( 0.001f, 0.05f )] public float ZoomSpeed { get; set; } = 0.01f;

	[Property] public float DefaultDistance { get; set; } = 200f;
	[Property] public float MinDistance { get; set; } = 24f;
	[Property] public float MaxDistance { get; set; } = 2000f;

	/// <summary>Pitch limits for orbit and free-look. Default to near-vertical; an owner (e.g. the hider) can
	/// tighten them.</summary>
	[Property, Range( -89f, 0f )] public float MinPitch { get; set; } = -89f;
	[Property, Range( 0f, 89f )] public float MaxPitch { get; set; } = 89f;

	/// <summary>Optional world point the pivot snaps near when enabled (free-pivot mode only). The current
	/// view is preserved exactly; this only chooses how far down the look direction the pivot sits.</summary>
	public Vector3? FocusHint { get; set; }

	/// <summary>When set, the pivot tracks this object every frame (third-person gameplay camera).</summary>
	public GameObject FollowTarget { get; set; }

	/// <summary>Offset added to the follow target's position to place the pivot (e.g. up to torso height).</summary>
	public Vector3 FollowOffset { get; set; }

	/// <summary>When set, the boom collides with geometry and pulls in, ignoring this object's hierarchy
	/// (the pawn). Null = no collision (edit / creative).</summary>
	public GameObject IgnoreCollision { get; set; }

	/// <summary>Master switch for the collision boom. Off = the camera keeps its full distance and clips
	/// through geometry, even with <see cref="IgnoreCollision"/> set.</summary>
	[Property] public bool BoomCollision { get; set; } = true;

	public Vector3 Pivot { get; set; }
	public float Distance { get; set; }

	/// <summary>Camera orientation. Yaw drives the follow camera's facing (the hider reads this for movement).</summary>
	public Angles Angles
	{
		get => _angles;
		set { _angles = value; _angles.roll = 0f; }
	}

	Angles _angles;
	Vector3 _panOffset; // accumulated pan, relative to the follow target

	bool _seeded; // false until Pivot/Distance/_angles have been derived from a live camera

	protected override void OnEnabled() => _seeded = TrySeed();

	// Seed the rig from the current view: preserve the exact camera transform and derive the pivot along the look
	// direction. Returns false if there's no camera yet (so the caller can retry) — at scene load this component
	// can enable before the CameraComponent registers, and seeding from a zeroed state would snap the camera to
	// the origin on the first Apply. (In follow mode the owner configures Angles/Distance; the pivot is recomputed
	// in OnUpdate.)
	bool TrySeed()
	{
		var cam = Scene.Camera;
		if ( cam is null )
			return false;

		_angles = cam.WorldRotation.Angles();
		var fwd = cam.WorldRotation.Forward;
		float dist = FocusHint is Vector3 f ? Vector3.Dot( f - cam.WorldPosition, fwd ) : DefaultDistance;
		Distance = dist.Clamp( MinDistance, MaxDistance );
		Pivot = cam.WorldPosition + fwd * Distance;
		_recenterDebt = Vector3.Zero; // a fresh seed IS the desired view — nothing left to ease out
		return true;
	}

	// Clear the shared alt-nav state if we're torn down mid-drag (e.g. the hunter leaving edit mode disables the
	// rig), so the HUD doesn't keep drawing a stale dot. Drop the seed too, so a re-enable re-seeds from the view.
	protected override void OnDisabled()
	{
		AltNav.Reset();
		_seeded = false;
	}

	protected override void OnUpdate() => Tick( handleAltDrag: true );

	/// <summary>Advance the rig one frame and write the result to the scene camera. Pass <paramref
	/// name="handleAltDrag"/> = true to let the rig read Maya alt-nav itself (orbit/dolly/pan + the dot cursor);
	/// pass false when an owning controller drives rotation/zoom via <see cref="ApplyLook"/>/<see cref="Dolly"/>/
	/// <see cref="Pan"/> instead (e.g. the hider's free-look play camera). Lets one rig serve both pawns.</summary>
	public void Tick( bool handleAltDrag )
	{
		var cam = Scene.Camera;
		if ( cam is null )
			return;

		// Read the shared Maya alt-nav (sets AltNav state + dot) and apply the gesture to the rig. The hider's play
		// camera passes false and drives the rig itself via ApplyLook/Dolly/Pan instead.
		if ( handleAltDrag )
		{
			AltNav.Tick();
			switch ( AltNav.Current )
			{
				case AltNav.Gesture.Orbit: Orbit( AltNav.Delta ); break;
				case AltNav.Gesture.Dolly: Dolly( AltNav.Delta ); break;
				case AltNav.Gesture.Pan: Pan( AltNav.Delta ); break;
			}
		}

		// Seed now if we couldn't on enable (camera registered after us this load). Until we have, don't drive
		// the view — Apply from a zeroed Pivot/Distance would jump the camera to the origin.
		if ( !_seeded )
			_seeded = TrySeed();
		if ( !_seeded )
			return;

		if ( FollowTarget.IsValid() )
			Pivot = FollowTarget.WorldPosition + FollowOffset + _panOffset;

		// Ease out any absorbed recenter shift (paused mid-operation) so the view settles onto the moved
		// pivot. Runs every Tick — play mode included — so an ease that starts in edit mode always finishes.
		if ( !HoldRecenterEase )
			_recenterDebt = Vector3.Lerp( _recenterDebt, Vector3.Zero, 1f - MathF.Exp( -RecenterEaseSpeed * Time.Delta ) );

		Apply( cam );
	}

	/// <summary>Apply a free-look delta (already sensitivity-scaled, e.g. <see cref="Input.AnalogLook"/>) to the
	/// camera orientation. For owners that drive rotation themselves rather than via alt-drag.</summary>
	public void ApplyLook( Angles delta )
	{
		_angles.yaw += delta.yaw;
		_angles.pitch = (_angles.pitch + delta.pitch).Clamp( MinPitch, MaxPitch );
		_angles.roll = 0f;
	}

	public void Orbit( Vector2 d )
	{
		_angles.yaw -= d.x * OrbitSpeed;
		_angles.pitch = (_angles.pitch + d.y * OrbitSpeed).Clamp( MinPitch, MaxPitch );
		_angles.roll = 0f;
	}

	// Drag up zooms in, down zooms out — exponential so it feels even at any distance.
	public void Dolly( Vector2 d ) => Distance = (Distance * MathF.Pow( 1f + ZoomSpeed, d.y )).Clamp( MinDistance, MaxDistance );

	/// <summary>The follow target just teleported by <paramref name="worldDelta"/> without its geometry
	/// visibly moving (a sculpt recenter shifted the brushes one way and the root the other): keep the camera
	/// exactly where it was by absorbing the shift as debt, then ease the debt out over time so the view
	/// gently drifts onto the new pivot. No-op in free-pivot mode — the pivot doesn't track the object there,
	/// so it never jumped.</summary>
	public void AbsorbPivotShift( Vector3 worldDelta )
	{
		if ( FollowTarget.IsValid() )
			_recenterDebt -= worldDelta;
	}

	/// <summary>How fast the absorbed shift eases out (per second, exponential). Kept gentle — it's a slow
	/// drift back onto the sculpt, not a snap.</summary>
	[Property] public float RecenterEaseSpeed { get; set; } = 1.5f;

	/// <summary>Freeze the recenter ease this frame. The edit session holds this while any operation is in
	/// progress (gizmo drag, alt-nav, held click) so the view never moves under the user's cursor.</summary>
	public bool HoldRecenterEase { get; set; }

	Vector3 _recenterDebt; // camera offset left over after AbsorbPivotShift; eased to zero every Tick

	// Vertical pan only. In follow mode it offsets the follow point; otherwise it moves the world pivot.
	public void Pan( Vector2 d )
	{
		float scale = PanSpeed * Distance * 0.001f;
		var delta = _angles.ToRotation().Up * d.y * scale;
		if ( FollowTarget.IsValid() )
			_panOffset += delta;
		else
			Pivot += delta;
	}

	void Apply( CameraComponent cam )
	{
		var rot = _angles.ToRotation();
		var pivot = Pivot + _recenterDebt; // view-only offset — Pivot itself stays the true orbit centre
		var desired = pivot - rot.Forward * Distance;

		// Pull the boom in if it would clip through geometry (gameplay only).
		if ( BoomCollision && IgnoreCollision.IsValid() )
		{
			var tr = Scene.Trace.Ray( pivot, desired )
				.Radius( 8f )
				.IgnoreGameObjectHierarchy( IgnoreCollision )
				.Run();
			if ( tr.Hit )
				desired = tr.EndPosition;
		}

		cam.WorldPosition = desired;
		cam.WorldRotation = rot;
	}
}
