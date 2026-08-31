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
	[Property] public float MaxDistance { get; set; } = 500f;

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

	/// <summary>How fast the boom eases IN toward an obstruction (per-second exponential rate). Kept brisk —
	/// the hard clamp below still guarantees no clipping, this only softens the approach.</summary>
	[Property, Range( 1f, 30f )] public float BoomInSpeed { get; set; } = 14f;

	/// <summary>How fast the boom eases back OUT once an obstruction clears. Deliberately slow — the instant
	/// snap-out was what made sweeping the camera over bumpy geometry pump in/out nauseatingly.</summary>
	[Property, Range( 0.5f, 15f )] public float BoomOutSpeed { get; set; } = 4f;

	/// <summary>Lateral spread of the whisker rays that judge whether a boom obstruction really blocks the
	/// view (a wall) or is just a thin pole / small prop crossing the sight line. Obstructions the whiskers
	/// see around barely pull the camera at all — it's allowed to briefly clip past them instead.</summary>
	[Property, Range( 8f, 64f )] public float BoomWhiskerSpread { get; set; } = 24f;

	/// <summary>Only collide with world geometry (walls, scene geo). SDF clay props — decoys, released
	/// disguises, map clay props (everything tagged <see cref="SdfCollider.ClayTag"/>) — never push the
	/// camera; it clips past them instead. Walls always collide regardless.</summary>
	[Property] public bool BoomWorldOnly { get; set; } = true;

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
	float _boomPull;    // smoothed obstruction pull-in (how far short of Distance the boom currently sits)
	float _boomBlocked; // smoothed whisker occlusion 0..1 (raw value is quantized to quarters and jumps)

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
		_boomPull = 0f;
		_boomBlocked = 0f;
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

	// Drag up zooms in, down zooms out — exponential so it feels even at any distance. Zoom works on where
	// the camera ACTUALLY is (boom = Distance minus the collision pull-in), not the virtual full distance:
	// zooming in while a wall has the boom clamped starts from the camera's real position (and rebases
	// Distance there, dropping the pull), while zooming out against a wall can't inflate Distance past the
	// remembered maximum — orbiting away restores the old framing instead of flying out to a padded value.
	public void Dolly( Vector2 d )
	{
		float factor = MathF.Pow( 1f + ZoomSpeed, d.y );
		float boom = Distance - _boomPull;
		float target = (boom * factor).Clamp( MinDistance, MaxDistance );

		if ( factor < 1f )
		{
			Distance = target;
			_boomPull = 0f; // the new distance is in front of the obstruction; let any pull re-establish
		}
		else
		{
			Distance = MathF.Max( Distance, target );
		}
	}

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
		float boom = Distance;

		// Pull the boom in if it would clip through geometry (gameplay only). Two traces: a wide one gives a
		// resting target with headroom off walls, eased toward (fast in, slow out) so sweeping over bumpy
		// geometry doesn't pump the camera in/out at frame rate; the narrow one is a hard same-frame clamp,
		// so the no-clip guarantee is exactly what the old instant boom had. The smoothed value is the
		// pull-in, not the distance itself, so player dolly zoom stays instant.
		if ( BoomCollision && IgnoreCollision.IsValid() )
		{
			var softTr = TraceBoom( rot, 16f );
			var hardTr = TraceBoom( rot, 8f );

			float hard = hardTr.Hit ? hardTr.Fraction * Distance : Distance;
			// A start-solid soft sweep (pivot within 16u of a wall — a prop hiding against one) carries no
			// direction info and would read as "pull all the way in"; defer to the hard trace instead.
			float soft = softTr.StartedSolid ? hard
				: softTr.Hit ? softTr.Fraction * Distance : Distance;

			float wantPull = MathF.Max( 0f, Distance - soft );

			// Context sensitivity: only pull in as much as the obstruction actually blocks the view. Thin
			// poles and small props near the pivot block just the centre ray — the whiskers see straight
			// past them — so the boom stays out and the camera clips briefly past instead of zooming in.
			// The raw whisker fraction is quantized to quarters and jumps as rays cross an edge one at a
			// time (orbiting into a cube), so it's smoothed before use — a genuine wall still reads 1.0 on
			// the very first frame, so detection isn't delayed, only the transitions are.
			float rawBlocked = wantPull > 0f ? WhiskerOcclusion( rot ) : 0f;
			_boomBlocked = MathX.Lerp( _boomBlocked, rawBlocked, 1f - MathF.Exp( -12f * Time.Delta ) );
			wantPull *= _boomBlocked;

			float rate = wantPull > _boomPull ? BoomInSpeed : BoomOutSpeed;
			_boomPull = MathX.Lerp( _boomPull, wantPull, 1f - MathF.Exp( -rate * Time.Delta ) );

			// The hard clamp FADES in with occlusion instead of arming at a threshold — a binary arm was a
			// visible teleport (eased boom far out, clamp suddenly live at the trace distance). Fully
			// occluded = the old instant no-clip clamp; mostly clear = no clamp at all.
			float clampWeight = MathX.Clamp( (_boomBlocked - 0.25f) / 0.5f, 0f, 1f );
			float hardEffective = MathX.Lerp( Distance, hard, clampWeight );

			boom = MathF.Min( Distance - _boomPull, hardEffective );
			_boomPull = Distance - boom; // fold the clamp back in, so easing out starts from where we really are
		}
		else
		{
			_boomPull = 0f;
			_boomBlocked = 0f;
		}

		cam.WorldPosition = Pivot - rot.Forward * boom;
		cam.WorldRotation = rot;

		// Every rig consumer (sculpt/edit, the prop's play camera) runs at the orbit FOV. Declared through
		// MainCamera — never written to the CameraComponent directly — and asserted every frame, so the ease
		// back from the hunter's first-person FOV happens centrally and a live settings change just applies.
		MainCamera.Fov = GameSettings.OrbitFov;
	}

	// How much of the view around the sight line the obstruction really blocks, 0..1. Four thin rays fan
	// from just beside the pivot (half spread — so clutter sitting right next to the disguise doesn't block
	// them) out to beside the full-distance camera position (full spread). A wall crosses all of them at any
	// depth; a thin pole or a small prop near the pivot crosses none. Only run when the centre trace hit.
	float WhiskerOcclusion( Rotation rot )
	{
		var camPos = Pivot - rot.Forward * Distance;
		var offsets = new Vector3[]
		{
			rot.Right * BoomWhiskerSpread, rot.Right * -BoomWhiskerSpread,
			rot.Up * BoomWhiskerSpread, rot.Up * -BoomWhiskerSpread,
		};

		int blockedCount = 0;
		foreach ( var off in offsets )
		{
			var trace = FilterBoomTrace( Scene.Trace.Ray( Pivot + off * 0.5f, camPos + off ) );
			if ( trace.Run().Hit )
				blockedCount++;
		}

		return blockedCount / 4f;
	}

	// Sweep a sphere from the pivot out along the full boom length; the caller reads Fraction/StartedSolid.
	SceneTraceResult TraceBoom( Rotation rot, float radius )
	{
		var trace = FilterBoomTrace( Scene.Trace.Ray( Pivot, Pivot - rot.Forward * Distance ).Radius( radius ) );
		return trace.Run();
	}

	// The shared exclusion set for every boom/whisker trace: the pawn's own hierarchy, the owner's ignore
	// tag (fellow prop bodies), invisible stage-fence walls (a camera shoved by a wall that isn't there
	// reads as a glitch), and — in world-only mode — all SDF clay props.
	SceneTrace FilterBoomTrace( SceneTrace trace )
	{
		trace = trace.IgnoreGameObjectHierarchy( IgnoreCollision );
		trace = trace.WithoutTags( CharadesStageFence.WallTag );

		if ( !string.IsNullOrEmpty( IgnoreCollisionTag ) )
			trace = trace.WithoutTags( IgnoreCollisionTag );

		if ( BoomWorldOnly )
			trace = trace.WithoutTags( SdfCollider.ClayTag );

		return trace;
	}
}
