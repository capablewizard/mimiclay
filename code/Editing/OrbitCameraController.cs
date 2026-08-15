using System;

namespace Mimiclay;

/// <summary>
/// Create-mode orbit camera. Left-drag orbits around the pivot (a clean left CLICK stays a select — the drag
/// only claims after a few px of travel), right-drag dollies (zoom), middle-drag pans vertically — presses
/// that land on an edit drag-target (HUD, gizmo handle, stamp) go to editing instead, and alt+button always
/// navigates; see <see cref="AltNav"/> for the claim rules. Drives the scene's main camera.
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

	/// <summary>Tag the collision boom passes straight through, on top of <see cref="IgnoreCollision"/>'s
	/// hierarchy. Null = the boom stops on everything. The hider sets it to the prop-body tag so a fellow prop —
	/// which its own physics already ignores — can't shove its camera either; a boom that stopped on one would
	/// collapse onto the pivot the moment two props shared a spot (the trace starts inside that collider).</summary>
	public string IgnoreCollisionTag { get; set; }

	public Vector3 Pivot { get; set; }
	public float Distance { get; set; }

	/// <summary>When set, the pivot's world X/Y is pinned to this point every frame — height (Z) still comes
	/// from the follow target + offset + pan as normal, so the player keeps their up/down framing. Lets an
	/// owner orbit something other than the follow origin: the hider pins this to its disguise shape's bounds
	/// centre so the camera always orbits the clay itself. Null = off (pivot behaves exactly as before).</summary>
	public Vector2? PivotXYOverride { get; set; }

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

		if ( PivotXYOverride is { } xy )
			Pivot = new Vector3( xy.x, xy.y, Pivot.z );

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
		var desired = Pivot - rot.Forward * Distance;

		// Pull the boom in if it would clip through geometry (gameplay only).
		if ( BoomCollision && IgnoreCollision.IsValid() )
		{
			var trace = Scene.Trace.Ray( Pivot, desired )
				.Radius( 8f )
				.IgnoreGameObjectHierarchy( IgnoreCollision );

			if ( !string.IsNullOrEmpty( IgnoreCollisionTag ) )
				trace = trace.WithoutTags( IgnoreCollisionTag );

			var tr = trace.Run();
			if ( tr.Hit )
				desired = tr.EndPosition;
		}

		cam.WorldPosition = desired;
		cam.WorldRotation = rot;

		// Every rig consumer (sculpt/edit, the prop's play camera) runs at the orbit FOV. Declared through
		// MainCamera — never written to the CameraComponent directly — and asserted every frame, so the ease
		// back from the hunter's first-person FOV happens centrally and a live settings change just applies.
		MainCamera.Fov = GameSettings.OrbitFov;
	}
}
