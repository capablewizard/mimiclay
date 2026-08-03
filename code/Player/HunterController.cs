using System;

namespace Mimiclay;

/// <summary>
/// Hunter: a plain first-person seeker. Movement/look/crouch/jump/camera all come from the stock s&amp;box
/// <see cref="PlayerController"/> (capsule, ground/step handling, landing sounds, eye transform) — this component
/// adds a hitscan shot on attack1, walk footstep sounds (see <see cref="EnableFootsteps"/> — the stock ones are
/// animation-event driven and this pawn has no animated model), and an edit mode for sculpting its own face.
///
/// <b>Edit mode</b> (Q): suspends first-person control (look + movement frozen) and hands the shared camera to
/// an <see cref="OrbitCameraController"/> framed on the hunter's <see cref="Face"/> sculpture, so you orbit and
/// edit your own head with the same Maya nav + sculpt gizmo the hider uses. The whole flow is the shared
/// <see cref="SculptEditSession"/> — this controller only supplies the target + camera and gates play input.
///
/// Tuning lives on the <c>hunter.prefab</c>'s <see cref="PlayerController"/> (speeds, body height, camera) —
/// edit it there. When this component is dropped on a bare GameObject instead (no prefab), it falls back to
/// creating a sensible first-person controller so it still works.
/// </summary>
[Title( "Hunter Controller" )]
[Category( "Mimiclay" )]
[Icon( "sports_esports" )]
public sealed class HunterController : Component
{
	[Property, Group( "Weapon" )] public float Range { get; set; } = 4096f;

	/// <summary>Seconds between shots — the hunt's "shoot on a cooldown".</summary>
	[Property, Group( "Weapon" )] public float ShootCooldown { get; set; } = 1f;

	// Owner-side gate: the next moment a shot is allowed.
	TimeUntil _nextShot;

	/// <summary>Emit footstep sounds while walking. The stock controller only plays WALK steps from animation
	/// events on a SkinnedModelRenderer — this pawn has none (capsule + SDF head), so without this the only
	/// footstep sound you'd ever hear is the engine's physics-driven landing thump. We reproduce the walk half
	/// ourselves: distance-gated calls into the controller's own PlayFootstepSound (surface sounds + mixer).</summary>
	[Property, Group( "Footsteps" )] public bool EnableFootsteps { get; set; } = true;

	/// <summary>World units of horizontal travel per step when walking (the stride at WalkSpeed and below).</summary>
	[Property, Group( "Footsteps" )] public float StepDistance { get; set; } = 60f;

	/// <summary>Stride at full run (RunSpeed) — longer than the walk stride so sprinting doesn't sound like a
	/// drum roll. The actual stride blends between the two with speed, so in-between speeds ease smoothly
	/// rather than snapping cadence at some threshold.</summary>
	[Property, Group( "Footsteps" )] public float RunStepDistance { get; set; } = 100f;

	// Footstep emitter state: distance walked since the last step, where we last measured from (invalid until
	// re-seeded on the first grounded frame), and which foot lands next (alternates left/right sounds).
	float _stepAccum;
	Vector3 _stepFrom;
	bool _stepSeeded;
	int _stepFoot;

	/// <summary>The run dust effect (the "RunPFX" child): its emitters are enabled only while moving at running
	/// speed — including a running jump, since horizontal speed carries through the air. Auto-resolved by child
	/// name in <see cref="OnStart"/> when left unset.</summary>
	[Property, Group( "Run Effect" )] public GameObject RunEffect { get; set; }

	// Run-effect state: the emitters we toggle (cached once — the ParticleEffect itself stays enabled so live
	// puffs finish their lifetime instead of popping), our own position-delta tracker (separate from the footstep
	// one, which resets while airborne — a running JUMP must keep the effect on), and the current on/off state
	// for hysteresis.
	ParticleEmitter[] _runEmitters;
	ParticleModelRenderer[] _runRenderers;
	Vector3 _runFrom;
	bool _runSeeded;
	bool _runOn;

	/// <summary>The SDF sculpture edited in face-edit mode (the "Head"). Auto-resolved in <see cref="OnStart"/>
	/// when left unset: a child named "Head" with a sculpture, else the first sculpture in the hierarchy.</summary>
	[Property, Group( "Edit" )] public SdfSculpture Face { get; set; }

	/// <summary>How tightly the edit camera frames the face on open. The distance is derived from the head's
	/// bounding sphere + the camera FOV so any size of head opens at the same apparent size; this is the breathing
	/// room around it — 1 = head exactly fills the frame, 1.2 = ~20% margin. (You can still dolly from there.)</summary>
	[Property, Group( "Edit" ), Range( 1f, 3f )] public float EditFramingMargin { get; set; } = 1.4f;

	/// <summary>Pitch the edit camera opens at (degrees; positive looks slightly down at the face).</summary>
	[Property, Group( "Edit" )] public float EditCameraPitch { get; set; } = 10f;

	/// <summary>Debug "eyes" marker (a child pivot) parked at the eye and aimed where we're looking each frame, so
	/// the look direction is visible on remote clients (the capsule body parts are static children of the root).
	/// Optional — leave unset to skip.</summary>
	[Property, Group( "Debug" )] public GameObject Eyes { get; set; }

	PlayerController _controller;
	ModelRenderer[] _bodyRenderers;
	SdfRaymarchRenderer[] _sdfRenderers;
	SculptEditSession _session;
	OrbitCameraController _orbit;

	// Grounded eye-z smoothing (same treatment as the stock camera path): walking up a step teleports the body
	// vertically in one physics tick, so the eye z is lerped toward the new height instead of snapping. 0 = unseeded.
	float _eyez;

	// Internal: the crosshair HUD (HunterCrosshair) reads this to hide the dot while sculpting.
	internal bool EditMode => _session?.IsEditing ?? false;

	protected override void OnAwake()
	{
		// Prefer the prefab's authored controller; only build one if this was dropped on a bare GameObject. Either
		// way ensure a walk move-mode exists.
		_controller = Components.Get<PlayerController>();
		if ( !_controller.IsValid() )
			_controller = Components.Create<PlayerController>();

		// We own the shared scene camera ourselves and set ONLY its transform (see DriveCamera). The stock
		// controller's camera mode mutates that shared camera — FieldOfView, RenderExcludeTags, mode hooks — and
		// never restores it, which left it dirty for whatever drove it next (the prop's HiderController), breaking
		// the prop's raymarched SDF. Turning it off keeps the one camera clean across pawn switches. EyePosition /
		// EyeAngles still update from the input path, so we can read them to position the camera.
		_controller.UseCameraControls = false;

		Components.GetOrCreate<Sandbox.Movement.MoveModeWalk>();
	}

	protected override void OnStart()
	{
		// Cache what we hide in first person (see HideOwnBody). SDF visuals manage their own sibling mesh
		// ModelRenderer per-frame (raymarch vs. shadow-only by LOD band), so we must NOT sweep those into
		// _bodyRenderers — forcing their RenderType from here would fight that and double-draw on proxies.
		// We hide them through RenderHidden instead. Plain body renderers (the capsule parts) we own directly.
		_sdfRenderers = Components.GetAll<SdfRaymarchRenderer>( FindMode.EnabledInSelfAndDescendants ).ToArray();
		_bodyRenderers = Components.GetAll<ModelRenderer>( FindMode.EnabledInSelfAndDescendants )
			.Where( r => !r.GameObject.Components.Get<SdfRaymarchRenderer>().IsValid() )
			.ToArray();

		// Face-edit mode. Resolve the editable sculpture (the head), then stand up the shared edit machinery:
		// an orbit camera (idle until edit mode hands it the view) and a SculptEditSession pointed at the face.
		// Wiring OrbitCamera onto the session is what makes SetActive enable/disable the camera for us.
		Face ??= ResolveFace();

		_orbit = Components.GetOrCreate<OrbitCameraController>();
		_orbit.Enabled = false;       // only live while editing; the session toggles it
		_orbit.MinDistance = 8f;      // let the player get right up to the face

		// Edit session + network sync, both bound to the face — SculptablePawn keeps the "session and sync
		// always target the same sculpture" invariant in one place (the owner publishes on commit; proxies
		// apply). Passing the orbit rig makes the session enable/disable the camera around edit mode.
		_session = SculptablePawn.AttachEditing( this, Face, _orbit );

		// Run dust: cache the emitters we gate by speed (see UpdateRunEffect). EverythingInSelf so a re-cache
		// still finds them after we've disabled them; they start enabled in the prefab, so force the initial
		// off state — the pawn spawns standing still.
		RunEffect ??= GameObject.Children.FirstOrDefault( c => c.Name == "RunPFX" );
		_runEmitters = RunEffect.IsValid()
			? RunEffect.Components.GetAll<ParticleEmitter>( FindMode.EverythingInSelfAndDescendants ).ToArray()
			: Array.Empty<ParticleEmitter>();
		_runRenderers = RunEffect.IsValid()
			? RunEffect.Components.GetAll<ParticleModelRenderer>( FindMode.EverythingInSelfAndDescendants ).ToArray()
			: Array.Empty<ParticleModelRenderer>();

		foreach ( var e in _runEmitters )
			e.Enabled = false;
	}

	// The sculpture face-edit mode targets: the authored Face, else a child named "Head" carrying a sculpture,
	// else the first sculpture anywhere under the pawn (so a bare/renamed setup still finds something to edit).
	SdfSculpture ResolveFace()
	{
		var head = GameObject.Children.FirstOrDefault( c => c.Name == "Head" );
		var onHead = head.IsValid() ? head.Components.Get<SdfSculpture>( FindMode.EnabledInSelfAndDescendants ) : null;
		return onHead.IsValid() ? onHead : Components.Get<SdfSculpture>( FindMode.EnabledInSelfAndDescendants );
	}

	protected override void OnUpdate()
	{
		// Owner-only: toggle face-edit mode (Q, the same "Edit" action the hider uses). Done before the input
		// gates below so entering/leaving takes effect this same frame (no one-frame camera gap on exit).
		if ( !IsProxy && Input.Pressed( "Edit" ) )
			ToggleEdit();

		// Tab toggles the brush wireframe overlay, same as the hider's props. The session no-ops this unless it's
		// actually editing, so it's only meaningful in face-edit mode.
		if ( !IsProxy && Input.Pressed( "ToggleWireframes" ) )
			_session?.ToggleWireframes();

		// Only the owning client reads look/movement + drives the shared camera; on every other machine the pawn is
		// a proxy and gets the networked eye transform. Editing also suspends look + movement (the body holds still
		// while you sculpt your face) and releases the camera to the orbit controller. Gate LIVE each frame, not
		// once in OnStart: a host-spawned pawn runs OnStart on the owning client BEFORE ownership replicates, so a
		// one-shot IsProxy read is stale. (IsProxy is false in non-networked play, so solo still works.)
		// Round freeze: during the Starting countdown locomotion is locked (you can still LOOK around to scope out
		// your team + surroundings) — same "stop input, clear momentum" treatment as edit mode.
		bool locked = RoundManager.ControlsLocked;

		if ( _controller.IsValid() )
		{
			bool play = !IsProxy && !EditMode;
			_controller.UseLookControls = play;
			_controller.UseInputControls = play && !locked;

			// UseInputControls=false stops the controller READING input, but the last WishVelocity stays latched
			// and the walk move-mode keeps applying it — so a key held when you entered edit/freeze would coast the
			// hunter away. Clear the wish + any HORIZONTAL momentum every such frame so the body holds still — but keep
			// the vertical component so gravity still settles a freshly-spawned pawn onto the floor (zeroing all of it
			// every frame wipes the fall velocity, leaving it to drift down in slow motion).
			if ( EditMode || locked )
			{
				_controller.WishVelocity = Vector3.Zero;
				if ( _controller.Body.IsValid() )
					_controller.Body.Velocity = _controller.Body.Velocity.WithX( 0f ).WithY( 0f );
			}
		}

		HideOwnBody();
		UpdateFootsteps();
		UpdateRunEffect();

		// ONE smoothed live eye per frame, shared by the camera and the head placement below, so the two can
		// never disagree mid-frame. Computed on every machine (proxies place the head from their network-
		// interpolated pawn transform); SmoothedEyePosition advances _eyez, so call it exactly once per frame.
		Vector3 eye = _controller.IsValid() ? SmoothedEyePosition() : WorldPosition;

		if ( !IsProxy && !EditMode && _controller.IsValid() )
		{
			// Re-lock the cursor for first-person look. Edit mode (the orbit camera) frees it for the gizmo and
			// never re-hides it on exit, so assert it here every play frame — otherwise look can't capture the
			// mouse after leaving edit mode.
			Mouse.Visibility = MouseVisibility.Hidden;

			DriveCamera( eye );

			// Owner-only: otherwise every machine would shoot when ITS local player clicked, from a remote pawn's
			// eye. The trace is owner-side; a prop hit is reported to the host (authoritative) via RoundManager.
			// No shooting while controls are locked (the Starting countdown). The shot leaves from the same eye
			// the camera sits at, so the trace always matches what the crosshair shows.
			if ( !locked && Input.Pressed( "attack1" ) && _nextShot <= 0f )
				Shoot( eye );
		}

		// Park the head at the eye, aimed where we're looking — on ALL machines (the eye transform is networked),
		// so remote hunters' heads track their aim too. (Eyes is wired to the sculpted Head in hunter.prefab.)
		// MUST be placed from the same smoothed eye as the camera: positioning it from the controller's cached
		// EyePosition (stamped raw during fixed update) made the head — visible only as its raymarched shadow in
		// first person — step at the physics tick rate against the now-gliding camera.
		if ( Eyes.IsValid() && _controller.IsValid() )
		{
			Eyes.WorldPosition = eye;
			Eyes.WorldRotation = _controller.EyeAngles.ToRotation();
		}
	}

	// The eye everything visual hangs off this frame, computed LIVE — never read _controller.EyePosition for
	// placement: that property is a cached EyeTransform the stock controller re-stamps at the end of its
	// OnFixedUpdate, where transform reads are the raw end-of-tick position. Sampling that cache steps whatever
	// it drives at the physics tick rate instead of gliding (first seen as the whole scene jittering via the
	// camera, then as the head shadow jittering via the Eyes placement). WorldPosition read here (outside fixed
	// update) is the engine-interpolated transform; the formula mirrors MoveMode.CalculateEyeTransform.
	//
	// Grounded step smoothing (same as the stock camera path): stepping up moves the body vertically in one
	// tick, so glide the eye z toward it rather than snapping. Airborne follows raw so jumps/falls stay 1:1.
	// Advances _eyez — call exactly once per frame (OnUpdate does), and use the returned eye everywhere.
	Vector3 SmoothedEyePosition()
	{
		var eye = _controller.WorldPosition
			+ Vector3.Up * (_controller.CurrentHeight - _controller.EyeDistanceFromTop);

		if ( !_controller.IsAirborne && _eyez != 0f )
			eye.z = _eyez.LerpTo( eye.z, Time.Delta * 50f );
		_eyez = eye.z;

		return eye;
	}

	// Position the shared scene camera at the eye — FIRST PERSON, position + rotation ONLY. No FieldOfView /
	// render-setting changes, so the camera is left exactly as clean as we found it for the next pawn that drives
	// it. This is the whole point: one camera, every controller sets only its transform.
	void DriveCamera( Vector3 eye )
	{
		var cam = Scene.Camera;
		if ( !cam.IsValid() )
			return;

		cam.WorldPosition = eye;
		cam.WorldRotation = _controller.EyeAngles.ToRotation();
	}

	// First person: hide our OWN body so we don't see it but it still casts a shadow, while proxies (other
	// players' hunters) stay fully visible. Plain body parts switch to ShadowsOnly; SDF visuals do the same
	// via RenderHidden (they own their sibling mesh, so we can't just set its RenderType). Done by renderer
	// state, NOT by excluding a tag on the shared camera (tag-excluding mutates the shared camera — the bug
	// we just fixed — and would affect every pawn). Live each frame because ownership resolves after OnStart;
	// setting a value to its current value is a no-op.
	void HideOwnBody()
	{
		// Only manages our OWN body's first-person hide; a proxy pawn (another player's) isn't ours to touch — bail.
		if ( IsProxy )
			return;

		// Hidden in first person — but while editing your own face you need to SEE yourself, so show it then.
		var hideOwn = !IsProxy && !EditMode;

		if ( _bodyRenderers is not null )
		{
			var type = hideOwn ? ModelRenderer.ShadowRenderType.ShadowsOnly : ModelRenderer.ShadowRenderType.On;
			foreach ( var r in _bodyRenderers )
			{
				if ( r.IsValid() )
					r.RenderType = type;
			}
		}

		if ( _sdfRenderers is not null )
		{
			foreach ( var r in _sdfRenderers )
			{
				if ( r.IsValid() )
					r.RenderHidden = hideOwn;
			}
		}

		// Run puffs go shadows-only in first person too. ParticleModelRenderer has no ShadowRenderType, but
		// shadows-only IS just CastShadows + ExcludeGameLayer at the SceneObject level, and its RenderOptions.Game
		// maps to ExcludeGameLayer — re-applied to every live particle each frame, so this flips existing puffs
		// as well as new ones. CastShadows stays on from the prefab, untouched.
		if ( _runRenderers is not null )
		{
			foreach ( var r in _runRenderers )
			{
				if ( r.IsValid() )
					r.RenderOptions.Game = !hideOwn;
			}
		}
	}

	// Walk footsteps: every StepDistance units of grounded horizontal travel, play the ground surface's step
	// sound through the controller's own PlayFootstepSound (which handles surface lookup, left/right selection,
	// the footstep mixer and FootstepVolume). Runs on EVERY machine — proxies categorize ground too and their
	// pawn transform is networked, so remote hunters' steps are audible to hiders, same as stock animation-event
	// footsteps would be. Volume scales with observed speed like the engine's event handler does. Landing thumps
	// stay the engine's job (physics-driven, already working) — going airborne resets the accumulator so a jump
	// doesn't bank distance toward an instant step on top of the land sound.
	void UpdateFootsteps()
	{
		if ( !EnableFootsteps || !_controller.IsValid() )
			return;

		if ( !_controller.IsOnGround )
		{
			_stepSeeded = false;
			_stepAccum = 0f;
			return;
		}

		var pos = _controller.WorldPosition;

		if ( !_stepSeeded )
		{
			_stepFrom = pos;
			_stepSeeded = true;
			return;
		}

		float moved = (pos - _stepFrom).WithZ( 0f ).Length;
		_stepFrom = pos;

		// Observed horizontal speed, from the same delta — works identically for owner and proxy (proxies read
		// their network-interpolated transform; the controller's WishVelocity/Velocity aren't reliable there).
		float speed = Time.Delta > 0f ? moved / Time.Delta : 0f;

		// Stride lengthens with speed: StepDistance at WalkSpeed (and below), RunStepDistance at RunSpeed,
		// blended in between. The controller's speed properties are prefab values, identical on every machine,
		// so owner and proxies compute the same cadence.
		float stride = speed.Remap( _controller.WalkSpeed, _controller.RunSpeed, StepDistance, RunStepDistance );

		_stepAccum += moved;
		if ( _stepAccum < stride )
			return;

		_stepAccum = 0f;

		// Same speed→volume mapping as the stock animation-event path; near-still shuffles stay silent.
		float volume = speed.Remap( 0f, 400f, 0f, 1f );
		if ( volume <= 0.1f )
			return;

		_stepFoot = 1 - _stepFoot;
		_controller.PlayFootstepSound( pos, volume, _stepFoot );
	}

	// Run dust: enable the RunPFX emitters only while moving at running speed. Speed is OBSERVED horizontal
	// travel per frame (same proxy-safe technique as UpdateFootsteps — input and controller velocity aren't
	// readable on remote machines, but the networked transform is), and deliberately NOT gated on IsOnGround:
	// a running jump keeps its horizontal speed through the air so the effect stays on, while a standing/walking
	// jump never crosses the threshold. Hysteresis (turn on above 65% of the walk→run band, off below 45%) stops
	// the effect flickering while hovering around one cutoff. Edit mode and the round freeze zero velocity, so
	// the effect switches itself off there with no special casing.
	void UpdateRunEffect()
	{
		if ( _runEmitters is not { Length: > 0 } || !_controller.IsValid() )
			return;

		var pos = _controller.WorldPosition;

		if ( !_runSeeded )
		{
			_runFrom = pos;
			_runSeeded = true;
			return;
		}

		float speed = Time.Delta > 0f ? (pos - _runFrom).WithZ( 0f ).Length / Time.Delta : 0f;
		_runFrom = pos;

		float onAt = _controller.WalkSpeed.LerpTo( _controller.RunSpeed, 0.65f );
		float offAt = _controller.WalkSpeed.LerpTo( _controller.RunSpeed, 0.45f );

		_runOn = _runOn ? speed > offAt : speed >= onAt;

		// Toggle the EMITTERS, not the effect/GameObject — live puffs keep simulating and fade out naturally.
		foreach ( var e in _runEmitters )
		{
			if ( e.IsValid() )
				e.Enabled = _runOn;
		}
	}

	// Enter/leave face-edit mode. On enter, the session enables the orbit camera (seeding it from the current
	// first-person view); we immediately reframe it onto the face so you're looking AT your head, not out of it.
	// On leave, the session disables the orbit camera and DriveCamera resumes this same frame (see OnUpdate order).
	void ToggleEdit()
	{
		if ( !_session.IsValid() )
			return;

		_session.Toggle();

		if ( _session.IsEditing )
			FrameFace();
		else if ( _orbit.IsValid() ) // leaving: remember the view (face-relative) for the next edit session
			_lastEditView = new Angles( _orbit.Angles.pitch, _orbit.Angles.yaw - FaceYaw(), 0f );
	}

	// The last edit session's view, with yaw stored RELATIVE to the face's yaw — so the restored view stays
	// glued to the head even if the pawn turned between edits. Null until an edit session has ended: the
	// very first entry frames from the front.
	Angles? _lastEditView;

	float FaceYaw() => _controller.IsValid() ? _controller.EyeAngles.yaw : WorldRotation.Angles().yaw;

	// Park the orbit camera on the face: the FIRST entry frames it from the front (along the head's facing,
	// aiming back at it); later entries restore wherever you last left the view. Must run AFTER the session
	// enables the camera, since OrbitCameraController.OnEnabled seeds pivot/angles from the (first-person) view.
	void FrameFace()
	{
		if ( !_orbit.IsValid() )
			return;

		_orbit.Pivot = FaceCenterWorld();
		_orbit.Distance = FramingDistance();
		_orbit.Angles = _lastEditView is { } last
			? new Angles( last.pitch, FaceYaw() + last.yaw, 0f )
			: new Angles( EditCameraPitch, FaceYaw() + 180f, 0f ); // +180: stand in front, look back at the face
	}

	// Distance that fits the head's bounding sphere in the frame with EditFramingMargin breathing room, derived
	// from the camera's FOV — so any size of head opens at the same apparent size instead of a hardcoded guess.
	// Standard fit-sphere math: a sphere of radius r is tangent to the view cone at distance r / sin(halfFov).
	// CameraComponent.FieldOfView is the VERTICAL fov (the tighter axis on a wide screen), so fitting against it
	// guarantees the head fits horizontally too. Falls back to a fixed distance if bounds/camera aren't ready.
	float FramingDistance()
	{
		const float fallback = 60f;

		if ( !Face.IsValid() || !Sdf.TryGetBounds( Face.Brushes, out var bounds ) )
			return fallback;

		// Bounding-sphere radius = half the box diagonal, in world units.
		float radius = bounds.Size.Length * 0.5f * Face.WorldScale.x;
		if ( radius <= 0.01f )
			return fallback;

		float halfFov = (Scene.Camera.IsValid() ? Scene.Camera.FieldOfView : 60f).DegreeToRadian() * 0.5f;
		float sin = MathF.Sin( halfFov );
		if ( sin <= 0.001f )
			return fallback;

		return radius * EditFramingMargin / sin;
	}

	// World-space centre of the face's sculpted shape (its brush bounds), so the camera frames the head itself
	// rather than its pivot at the neck. Falls back to the object/eye position if bounds aren't available yet.
	Vector3 FaceCenterWorld()
	{
		if ( Face.IsValid() && Sdf.TryGetBounds( Face.Brushes, out var bounds ) )
			return Face.WorldTransform.PointToWorld( bounds.Center );

		if ( Face.IsValid() )
			return Face.WorldPosition;

		return _controller.IsValid() ? _controller.EyePosition : WorldPosition;
	}

	void Shoot( Vector3 from )
	{
		if ( !_controller.IsValid() )
			return;

		var dir = _controller.EyeAngles.ToRotation().Forward;

		_nextShot = ShootCooldown;

		var tr = Scene.Trace
			.Ray( from, from + dir * Range )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( !tr.Hit )
			return;

		// A hit on a prop pawn (its disguise collider belongs to a HiderController up the hierarchy) is reported to
		// the host, which validates the phase + that it's a live prop, then pops it and converts that player to a
		// hunter. Anything else is just map geometry. The call is [Rpc.Host] so it routes to the host from here.
		// With no round running, a DebugGameMode scene handles the hit instead (no phases — the prop just pops).
		var hider = FindHider( tr.GameObject );
		if ( !hider.IsValid() )
			return;

		if ( RoundManager.Current.IsValid() )
			RoundManager.Current.ReportPropHit( hider.GameObject );
		else if ( DebugGameMode.Current.IsValid() )
			DebugGameMode.Current.ReportPropHit( hider.GameObject );
	}

	// Walk up from the traced object to the pawn root that carries the HiderController (the collider we hit is the
	// disguise, a child). Null when we hit the world or a hunter.
	static HiderController FindHider( GameObject go )
	{
		while ( go.IsValid() )
		{
			var hider = go.Components.Get<HiderController>();
			if ( hider.IsValid() )
				return hider;
			go = go.Parent;
		}

		return null;
	}
}
