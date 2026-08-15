using System;

namespace Mimiclay;

/// <summary>
/// Drag navigation for a framed sculpture — the main-menu sculpt toy (and a soft tutorial for the in-game
/// create-mode navigation):
/// <list type="bullet">
/// <item><b>LMB</b> rotates the <see cref="Target"/> in place (yaw + clamped pitch).</item>
/// <item><b>RMB</b> zooms by moving the CAMERA along the camera→object line.</item>
/// <item><b>MMB</b> pans the CAMERA (vertical).</item>
/// </list>
/// Plus a slow idle turntable when nothing's selected.
///
/// Why zoom/pan move the camera, not the object: the sculpture stays static in the world, so it never sweeps
/// under the (static) light — its shadows stay frozen while you zoom, instead of shimmering. Moving the camera
/// along the camera→object line keeps an OFF-CENTRE model pinned to its screen spot (a plain forward dolly would
/// drift it sideways). Rotation still moves the object so the gizmo/brushes follow its transform.
///
/// Input comes from the shared <see cref="AltNav"/> (this is its sole consumer here, so it ticks it); that owns the
/// cursor dot + the drag/anchor state the HUD reads for its mouse-capture. Conflict-free with editing: a press
/// that lands on the HUD or on an edit target never claims a nav gesture (AltNav's press-edge claims), and the
/// gizmo + the session's hover/pick stand down while a claimed drag runs. Knows nothing about the SDF system itself.
/// </summary>
[Title( "Sculpt View Controller" )]
[Category( "Mimiclay" )]
[Icon( "3d_rotation" )]
public sealed class SculptViewController : Component
{
	/// <summary>The object to rotate (and the point the camera zooms toward). Defaults to this GameObject.</summary>
	[Property] public GameObject Target { get; set; }

	/// <summary>Rotate drag sensitivity — matches OrbitCameraController so the gesture feels identical.</summary>
	[Property, Range( 0.05f, 2f )] public float OrbitSpeed { get; set; } = 0.3f;

	/// <summary>Zoom sensitivity (exponential per drag pixel), matching the camera dolly's feel.</summary>
	[Property, Range( 0.001f, 0.05f )] public float ZoomSpeed { get; set; } = 0.01f;

	/// <summary>Pan sensitivity, matching the camera pan's feel.</summary>
	[Property, Range( 0.1f, 5f )] public float PanSpeed { get; set; } = 1.0f;

	/// <summary>Pitch limits, same as the camera orbit's (stays just shy of flipping over the poles).</summary>
	[Property, Range( -89f, 0f )] public float MinPitch { get; set; } = -89f;
	[Property, Range( 0f, 89f )] public float MaxPitch { get; set; } = 89f;

	/// <summary>How close / far zoom can bring the camera to the object (world units).</summary>
	[Property] public float MinZoomDistance { get; set; } = 40f;
	[Property] public float MaxZoomDistance { get; set; } = 400f;

	/// <summary>Idle auto-spin speed (degrees/sec) around the vertical (= the camera's up, since the menu camera is
	/// level) — a slow turntable that shows the model off. 0 disables it.</summary>
	[Property, Group( "Turntable" )] public float TurntableSpeed { get; set; } = 10f;

	/// <summary>Seconds to wait before the FIRST spin after the menu opens — longer than <see cref="ResumeDelay"/>,
	/// to let people settle into the menu before anything starts moving.</summary>
	[Property, Group( "Turntable" )] public float InitialDelay { get; set; } = 8f;

	/// <summary>Seconds with nothing selected (and no drag) before the turntable eases back in after an interaction.</summary>
	[Property, Group( "Turntable" )] public float ResumeDelay { get; set; } = 4f;

	/// <summary>How fast the turntable eases IN (spins up) — per second, exponential.</summary>
	[Property, Group( "Turntable" )] public float EaseInSpeed { get; set; } = 1.5f;

	/// <summary>How fast the turntable eases OUT (spins down when a shape is selected, or you grab the model).</summary>
	[Property, Group( "Turntable" )] public float EaseOutSpeed { get; set; } = 3.5f;

	GameObject Obj => Target.IsValid() ? Target : GameObject;

	Angles _angles;
	bool _owned;       // we've claimed the current alt-drag (it began clear of the HUD)
	bool _wasDragging; // AltNav.Dragging last frame, for the begin edge
	float _turntable;  // 0..1 current auto-spin strength (eased in/out)
	float _idleTime;   // seconds since the last "busy" moment (a selection or a drag)
	bool _interacted;  // has the user selected/dragged yet? until then the longer InitialDelay applies

	// Seed from the object's current facing so the authored pose is preserved and the drag continues from there.
	// Idle timer starts at 0 and _interacted is false, so the first spin waits the longer InitialDelay.
	protected override void OnEnabled()
	{
		_angles = Obj.WorldRotation.Angles();
		_idleTime = 0f;
		_interacted = false;
	}

	protected override void OnDisabled()
	{
		_owned = false;
		AltNav.Reset();
	}

	protected override void OnUpdate()
	{
		AltNav.Tick(); // we're the sole alt-nav consumer in the menu — advance the shared state + dot

		// Claim a drag only when it begins clear of the HUD (so grabbing a swatch doesn't drive the view); hold it
		// until the buttons release even if the cursor drifts over a panel.
		bool dragging = AltNav.Dragging;
		if ( dragging && !_wasDragging )
			_owned = !EditHud.PointerOverUi;
		else if ( !dragging )
			_owned = false;
		_wasDragging = dragging;

		if ( _owned )
		{
			var d = AltNav.Delta;
			switch ( AltNav.Current )
			{
				case AltNav.Gesture.Orbit: Rotate( d ); break;
				case AltNav.Gesture.Dolly: Zoom( d ); break;
				case AltNav.Gesture.Pan: Pan( d ); break;
			}
		}

		UpdateTurntable();
	}

	// Slow idle turntable: spins the object around the vertical axis (= the camera's up, since the menu camera is
	// level) when nothing's selected and no alt-drag is in progress. Eases OUT the instant a shape is selected (or
	// you grab the model), and eases back IN only once the selection's been clear — and no drag — for ResumeDelay.
	void UpdateTurntable()
	{
		bool selected = SculptEditSession.Current?.HasSelection ?? false;
		bool busy = selected || AltNav.Dragging;

		if ( busy )
		{
			_idleTime = 0f;
			_interacted = true; // after the first interaction, resuming uses the shorter ResumeDelay
		}
		else
			_idleTime += Time.Delta;

		// Longer settle-in delay before the very first spin; the shorter resume delay after any interaction.
		float delay = _interacted ? ResumeDelay : InitialDelay;
		float target = (!busy && _idleTime >= delay) ? 1f : 0f;

		// Separate spin-up / spin-down rates: ease in when ramping toward the target, ease out when ramping down.
		float ease = (target > _turntable) ? EaseInSpeed : EaseOutSpeed;
		_turntable = MathX.Lerp( _turntable, target, 1f - MathF.Exp( -ease * Time.Delta ) );

		// Don't drive the spin during an alt-drag (manual rotate already owns _angles) — just let the factor ease
		// out; it's ~0 by the time the drag ends. Below the threshold there's nothing to add.
		if ( AltNav.Dragging || _turntable <= 0.001f )
			return;

		_angles.yaw += TurntableSpeed * _turntable * Time.Delta;
		_angles.roll = 0f;
		Obj.WorldRotation = _angles.ToRotation();
	}

	// Rotate the OBJECT (so the gizmo/brushes follow). Same formula/speed/clamp as OrbitCameraController.Orbit;
	// signs inverted so turning the object reads the same on screen as orbiting the view would.
	void Rotate( Vector2 d )
	{
		_angles.yaw += d.x * OrbitSpeed;
		_angles.pitch = (_angles.pitch + d.y * OrbitSpeed).Clamp( MinPitch, MaxPitch );
		_angles.roll = 0f;
		Obj.WorldRotation = _angles.ToRotation();
	}

	// Move the CAMERA along the camera→object line (keeping its orientation), so the object stays static (frozen
	// shadows) and an off-centre model holds its screen position while it grows/shrinks. Exponential, like the
	// camera dolly: drag up moves the camera closer (zoom in).
	void Zoom( Vector2 d )
	{
		var cam = Scene.Camera;
		if ( cam is null )
			return;

		var target = Obj.WorldPosition;
		var toCam = cam.WorldPosition - target;
		float dist = toCam.Length;
		if ( dist < 1e-3f )
			return;

		float newDist = (dist * MathF.Pow( 1f + ZoomSpeed, d.y )).Clamp( MinZoomDistance, MaxZoomDistance );
		cam.WorldPosition = target + toCam / dist * newDist;
	}

	// Slide the CAMERA vertically so the (static) object appears to move up/down (grab-and-move: drag up → object
	// up). Scaled by distance so the on-screen speed is consistent. Locked to vertical for now.
	void Pan( Vector2 d )
	{
		var cam = Scene.Camera;
		if ( cam is null )
			return;

		var rot = cam.WorldRotation;
		float dist = (cam.WorldPosition - Obj.WorldPosition).Length;
		float scale = PanSpeed * dist * 0.001f;
		cam.WorldPosition += rot.Up * d.y * scale;
	}
}
