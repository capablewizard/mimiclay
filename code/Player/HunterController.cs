using System;

namespace Mimiclay;

/// <summary>
/// Hunter: a plain first-person seeker. Movement/look/crouch/jump/camera all come from the stock s&amp;box
/// <see cref="PlayerController"/> (capsule, ground/step handling, landing sounds, eye transform) — this component
/// adds a hitscan shot on attack1, walk footstep sounds (see <see cref="EnableFootsteps"/> — the stock ones are
/// animation-event driven and this pawn has no animated model), an edit mode for sculpting its own face, and a
/// third-person view toggle (Tab in play mode — an aim-locked shoulder boom, see <see cref="DriveCamera"/>).
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

	/// <summary>Played on EVERY machine when this hunter fires (broadcast RPC, positioned at the shooter) —
	/// a gunshot is public information, props tracking hunters by ear included.</summary>
	[Property, Group( "Weapon" )] public SoundEvent ShootSound { get; set; }

	/// <summary>Played at each crater a pellet carves out of clay (broadcast with the carve, positioned at the
	/// hole) — one splat per pellet, so a full scatter hit lands as a meaty multi-splat. Scatter pellets play
	/// quieter with their smaller craters.</summary>
	[Property, Group( "Weapon" )] public SoundEvent SplatSound { get; set; }

	/// <summary>Seconds between a pellet landing and its splat — breathing room so the gunshot's crack doesn't
	/// swallow the wet hits; they read as the clay reacting a beat later. Each pellet adds its own small random
	/// stagger (up to ~60ms) on top, so a scatter volley patters instead of hitting as one chord.</summary>
	[Property, Group( "Weapon" ), Range( 0f, 1f )] public float SplatDelay { get; set; } = 0.15f;

	/// <summary>Radius (world units) of the CENTRAL pellet's crater — a subtractive sphere appended to the
	/// top of the hit sculpture's brush stack, on every machine. Scatter pellets carve at 45–80% of this.
	/// 0 = shots don't carve.</summary>
	[Property, Group( "Weapon" ), Range( 0f, 16f )] public float CarveRadius { get; set; } = 6f;

	/// <summary>Pellets per shot. The FIRST always flies straight down the crosshair at full size and is the
	/// only one that counts for catching props (random scatter must never decide a catch); the rest scatter
	/// inside <see cref="CarveScatter"/> with varied smaller craters. 1 = the old single carve.</summary>
	[Property, Group( "Weapon" ), Range( 1, 8 )] public int CarvePellets { get; set; } = 4;

	/// <summary>Scatter cone half-angle (degrees) for the non-central pellets.</summary>
	[Property, Group( "Weapon" ), Range( 0f, 10f )] public float CarveScatter { get; set; } = 2.5f;

	/// <summary>Smooth-subtract blend of the carve crater — 0 is a hard-edged bite, higher melts the rim
	/// into the surrounding clay.</summary>
	[Property, Group( "Weapon" ), Range( 0f, 8f )] public float CarveBlend { get; set; } = 1.5f;

	/// <summary>Tint of the carved crater walls (a subtract brush's colour paints the surface it exposes) —
	/// darker than the clay reads as scorched/fresh-cut material.</summary>
	[Property, Group( "Weapon" )] public Color CarveColor { get; set; } = new( 0.42f, 0.26f, 0.2f );

	/// <summary>Recoil: degrees the shooter's own view kicks up per shot. Render-only (a CameraEffectSystem
	/// punch composed into the view, never the camera transform) — the actual aim never moves, so holding
	/// the crosshair on a prop through the kick still hits. 0 = no kick.</summary>
	[Property, Group( "Recoil" ), Range( 0f, 10f )] public float ShotKick { get; set; } = 2f;

	/// <summary>Random sideways lean per shot, as a fraction of <see cref="ShotKick"/> — so back-to-back
	/// shots don't look machined. 0 = dead straight up every time.</summary>
	[Property, Group( "Recoil" ), Range( 0f, 1f )] public float ShotKickYawJitter { get; set; } = 0.2f;

	/// <summary>Seconds the kick takes to settle back to rest.</summary>
	[Property, Group( "Recoil" ), Range( 0.05f, 1f )] public float ShotKickTime { get; set; } = 0.25f;

	/// <summary>How springy the return is: ~0.5 kicks up and eases straight back down, 1 overshoots once,
	/// higher wobbles. (The engine punch oscillates ~1.5× this many times over <see cref="ShotKickTime"/>.)</summary>
	[Property, Group( "Recoil" ), Range( 0.25f, 3f )] public float ShotKickBounce { get; set; } = 1f;

	/// <summary>Degrees of FOV punch riding the kick — positive widens the view for a blink (reads as the
	/// gun shoving you back), negative zooms in. 0 = none.</summary>
	[Property, Group( "Recoil" ), Range( -20f, 20f )] public float ShotKickFov { get; set; } = 0f;

	/// <summary>Range (world units) of the shake OTHER players feel from this gun — full strength at the
	/// shooter fading to nothing at this distance, on every machine (a gunshot is public information,
	/// rattling nearby props included). 0 = no shake.</summary>
	[Property, Group( "Shot Shake" )] public float ShotShakeRadius { get; set; } = 500f;

	/// <summary>Camera throw (world units) of that shake at point blank.</summary>
	[Property, Group( "Shot Shake" ), Range( 0f, 8f )] public float ShotShakeAmplitude { get; set; } = 2.5f;

	/// <summary>Direction changes per second — low is a slow lurch (distant artillery), high is a sharp
	/// rattle (gunfire next to your ear).</summary>
	[Property, Group( "Shot Shake" ), Range( 1f, 100f )] public float ShotShakeFrequency { get; set; } = 40f;

	/// <summary>Seconds the rattle takes to die out.</summary>
	[Property, Group( "Shot Shake" ), Range( 0.05f, 2f )] public float ShotShakeTime { get; set; } = 0.4f;


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

	/// <summary>Volume of the push-off step played the moment a JUMP leaves the ground (the engine's
	/// physics-driven landing thump covers the other half of the pair). 0 = silent jumps.</summary>
	[Property, Group( "Footsteps" ), Range( 0f, 1f )] public float JumpStepVolume { get; set; } = 0.8f;

	// Footstep emitter state: distance walked since the last step, where we last measured from (invalid until
	// re-seeded on the first grounded frame), and which foot lands next (alternates left/right sounds).
	float _stepAccum;
	Vector3 _stepFrom;
	bool _stepSeeded;
	int _stepFoot;

	// Jump push-off detection: on the grounded→airborne transition we open a short WATCH instead of deciding
	// immediately — IsOnGround flips on the fixed tick but WorldPosition here is the engine-INTERPOLATED
	// transform (see the fixed-tick eye-cache rule), which lags the tick, so the transition frame itself
	// shows no rise yet and a same-frame velocity gate never fires. Rising past the threshold within the
	// window = jump (play, ~20ms late — imperceptible); dropping or timing out = a fall (silent).
	bool _wasGrounded;
	bool _takeoffWatch;
	float _takeoffZ;
	TimeSince _sinceTakeoff;

	// The surface underfoot on the LAST grounded frame. The engine's PlayFootstepSound silently no-ops when
	// airborne (its first line bails if GroundSurface is invalid — cleared by the same tick that un-grounds),
	// so the jump push-off must remember what it jumped OFF and play through the pipeline itself.
	Surface _takeoffSurface;

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

	/// <summary>Boom length of the third-person camera (world units behind the eye). The camera stays aim-locked
	/// — same eye angles, shots still leave from the eye — so third person is purely a view change.</summary>
	[Property, Group( "Third Person" ), Range( 40f, 300f )] public float ThirdPersonDistance { get; set; } = 100f;

	/// <summary>Sideways offset of the boom, positive = over the RIGHT shoulder — so your own head doesn't sit
	/// exactly on the crosshair line. Shots still trace from the eye, so at very close range the crosshair sits
	/// a hair right of where the pellet lands; the offset is kept small so it never reads as a miss.</summary>
	[Property, Group( "Third Person" ), Range( -48f, 48f )] public float ThirdPersonShoulder { get; set; } = 20f;

	/// <summary>Vertical offset of the boom above the eye.</summary>
	[Property, Group( "Third Person" ), Range( -32f, 64f )] public float ThirdPersonRise { get; set; } = 8f;

	/// <summary>Debug "eyes" marker (a child pivot) parked at the eye and aimed where we're looking each frame, so
	/// the look direction is visible on remote clients (the capsule body parts are static children of the root).
	/// Optional — leave unset to skip.</summary>
	[Property, Group( "Debug" )] public GameObject Eyes { get; set; }

	/// <summary>How far below the eye the head object is parked. The Head's origin is its rotation pivot and sits
	/// at the NECK (the sculpture's brushes are authored +16 above the origin), so the head pitches around the neck
	/// rather than its centre. This drop keeps the neck fixed at eye − 16 while the camera eye stays untouched.</summary>
	[Property, Group( "Debug" )] public float NeckDrop { get; set; } = 16f;

	PlayerController _controller;
	ModelRenderer[] _bodyRenderers;
	SdfRaymarchRenderer[] _sdfRenderers;
	SculptEditSession _session;
	OrbitCameraController _orbit;
	HunterGun _gun;

	// The pawn's OTHER sculptures (body spheres, the fist) — kept dressed in the face's material, see
	// MatchBodyMaterialToFace.
	SdfSculpture[] _bodySculpts;

	// Grounded eye-z smoothing (same treatment as the stock camera path): walking up a step teleports the body
	// vertically in one physics tick, so the eye z is lerped toward the new height instead of snapping. 0 = unseeded.
	float _eyez;

	// Internal: the crosshair HUD (HunterCrosshair) reads this to hide the dot while sculpting.
	internal bool EditMode => _session?.IsEditing ?? false;

	// Alt-orbit (third person only): while Walk/alt is held the mouse swings the CAMERA boom around the pawn
	// — aim stays frozen (look controls off), so you can turn around and look at your own hunter — and on
	// release the view snaps straight back to the aim-locked shoulder boom. Mirrors the hider's alt gesture,
	// minus the no-snap-back adoption: the hunter's facing IS its aim, so the camera must return to it.
	Angles _altOrbitAngles;
	bool _altOrbiting;

	// Alt-orbit boom overrides, seeded on grab (aim distance, no pan) and discarded on release — the dolly
	// (alt+RMB) and vertical pan (alt+MMB) only live as long as the inspection gesture, so the snap-back
	// always returns to the plain shoulder boom.
	float _altDistance;
	Vector3 _altPan;

	// Dolly/pan feel, matched to the hider's play camera (OrbitCameraController defaults: ZoomSpeed 0.01,
	// PanSpeed 1 → distance × 0.001 per pixel). Distance clamps roughly bracket ThirdPersonDistance's range.
	const float AltZoomSpeed = 0.01f;
	const float AltMinDistance = 20f, AltMaxDistance = 400f;

	// Internal: the crosshair HUD hides the dot while alt-orbiting — the screen centre stops meaning "where
	// the shot goes" the moment the camera leaves the aim axis.
	internal bool AltOrbiting => _altOrbiting;

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

		// Voice chat rides the pawn. Created in OnAwake so the owner has it BEFORE NetworkSpawn — it ships in
		// the spawn snapshot and proxies receive the same component (the engine's voice RPC needs that shared
		// identity). On a proxy the snapshot copy already exists and GetOrCreate just adopts it.
		Components.GetOrCreate<PlayerVoice>();
	}

	protected override void OnStart()
	{
		// Cache what we hide in first person (see HideOwnBody). SDF visuals manage their own sibling mesh
		// ModelRenderer per-frame (raymarch vs. shadow-only by LOD band), so we must NOT sweep those into
		// _bodyRenderers — forcing their RenderType from here would fight that and double-draw on proxies.
		// We hide them through RenderHidden instead. Plain body renderers (the capsule parts) we own directly.
		// Gun clones (tagged) are skipped: HunterGun owns their visibility with different rules (the viewmodel
		// must SHOW in first person — sweeping it here would force it shadows-only, i.e. fully off).
		_sdfRenderers = Components.GetAll<SdfRaymarchRenderer>( FindMode.EnabledInSelfAndDescendants )
			.Where( r => !r.GameObject.Tags.Has( HunterGun.CloneTag ) )
			.ToArray();
		_bodyRenderers = Components.GetAll<ModelRenderer>( FindMode.EnabledInSelfAndDescendants )
			.Where( r => !r.GameObject.Components.Get<SdfRaymarchRenderer>().IsValid() )
			.Where( r => !r.GameObject.Tags.Has( HunterGun.CloneTag ) )
			.ToArray();

		// The detector gun display (world model + owner viewmodel). Optional — a pawn without the component
		// just has no gun; placement is pushed from OnUpdate so it shares the smoothed eye with the camera.
		_gun = Components.Get<HunterGun>();

		// Face-edit mode. Resolve the editable sculpture (the head), then stand up the shared edit machinery:
		// an orbit camera (idle until edit mode hands it the view) and a SculptEditSession pointed at the face.
		// Wiring OrbitCamera onto the session is what makes SetActive enable/disable the camera for us.
		Face ??= ResolveFace();

		// Everything sculpted on the pawn EXCEPT the face (the body spheres and the fist) mirrors the face's
		// clay — gun clones excluded, the gun keeps its own authored colours.
		_bodySculpts = Components.GetAll<SdfSculpture>( FindMode.EnabledInSelfAndDescendants )
			.Where( s => s != Face && !s.GameObject.Tags.Has( HunterGun.CloneTag ) )
			.ToArray();

		_orbit = Components.GetOrCreate<OrbitCameraController>();
		_orbit.Enabled = false;       // only live while editing; the session toggles it
		_orbit.MinDistance = 8f;      // let the player get right up to the face

		// Edit session + network sync, both bound to the face — SculptablePawn keeps the "session and sync
		// always target the same sculpture" invariant in one place (the owner publishes on commit; proxies
		// apply). Passing the orbit rig makes the session enable/disable the camera around edit mode.
		_session = SculptablePawn.AttachEditing( this, Face, _orbit );

		// Persistent appearance: bind the session to the shared head slot, so the face auto-loads your saved
		// head on spawn (that's what carries it across scenes — every machine spawns its pawn fresh) and saves
		// it back when you leave edit mode. The menu sculpt head shares the slot; see SculptEditSession.PersistSlot.
		_session.PersistSlot = SculptLibrary.HeadSlot;

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
	// Gun clones carry sculptures too — filtered by tag so the fallback can't hand you the gun to face-edit.
	SdfSculpture ResolveFace()
	{
		var head = GameObject.Children.FirstOrDefault( c => c.Name == "Head" );
		var onHead = head.IsValid() ? head.Components.Get<SdfSculpture>( FindMode.EnabledInSelfAndDescendants ) : null;
		return onHead.IsValid()
			? onHead
			: Components.GetAll<SdfSculpture>( FindMode.EnabledInSelfAndDescendants )
				.FirstOrDefault( s => !s.GameObject.Tags.Has( HunterGun.CloneTag ) );
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

		// ToggleView (Tab) flips first/third person — play mode only, since in edit mode Tab is the wireframe
		// toggle above (the two share the key, split by mode). Stored in GameSettings (per-machine, never
		// networked — proxies render the same either way) so the choice survives the respawn a prop→hunter
		// conversion goes through.
		if ( !IsProxy && !EditMode && Input.Pressed( "ToggleView" ) )
			GameSettings.HunterThirdPerson = !GameSettings.HunterThirdPerson;

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

			// Alt-orbit runs before the look gate below so grabbing alt freezes the aim the SAME frame the
			// orbit starts reading the mouse — otherwise one frame of look would leak into both.
			UpdateAltOrbit( play );

			_controller.UseLookControls = play && !_altOrbiting;
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
		MatchBodyMaterialToFace();
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
			// No shooting while controls are locked (the Starting countdown) or outside the Hunt — during Hide a
			// shot would still carve permanent craters into disguises the props can't heal, even though the host
			// ignores the hit report. A denied press gets a local error blip instead, so the trigger doesn't feel
			// broken. The shot leaves from the same eye the camera sits at, so the trace always matches what the
			// crosshair shows. During an alt-orbit the trigger is swallowed entirely (no blip): alt+mouse is a
			// camera gesture here — same as the hider — and the aim is frozen off-camera, so a shot would land
			// somewhere the hidden crosshair can't show.
			if ( Input.Pressed( "attack1" ) && !_altOrbiting )
			{
				if ( !locked && RoundManager.HuntingAllowed )
				{
					if ( _nextShot <= 0f )
						Shoot( eye );
				}
				else
					PlayShotDeniedSound();
			}
		}

		// Park the head at the eye, aimed where we're looking — on ALL machines (the eye transform is networked),
		// so remote hunters' heads track their aim too. (Eyes is wired to the sculpted Head in hunter.prefab.)
		// MUST be placed from the same smoothed eye as the camera: positioning it from the controller's cached
		// EyePosition (stamped raw during fixed update) made the head — visible only as its raymarched shadow in
		// first person — step at the physics tick rate against the now-gliding camera.
		if ( Eyes.IsValid() && _controller.IsValid() )
		{
			// Parked at the NECK, not the eye: the head sculpture's origin is its neck pivot (brushes authored
			// +NeckDrop above it), so dropping the object here keeps the head visual where it always was while
			// pitch swings it around the neck. World-space drop, not eye-relative — the neck is the fixed point.
			Eyes.WorldPosition = eye + Vector3.Up * -NeckDrop;
			Eyes.WorldRotation = _controller.EyeAngles.ToRotation();
		}

		// Gun display, from the SAME smoothed eye (and after DriveCamera, so the viewmodel can never lag the
		// camera by a frame). Runs on every machine — proxies swing the arm/world model from the networked eye
		// transform; firstPerson gates the viewmodel to the owning machine outside edit mode AND third person
		// (there you see your own world-model gun on the arm, exactly what proxies see).
		if ( _gun.IsValid() && _controller.IsValid() )
			_gun.Place( eye, _controller.EyeAngles, !IsProxy && !EditMode && !GameSettings.HunterThirdPerson );
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

	// Advance the third-person alt-orbit gesture. Seeded from the live aim on the frame alt is grabbed, so
	// the camera never jumps — it starts exactly where the shoulder boom was and swings from there; the same
	// seed-from-aim is why release snaps cleanly (DriveCamera just resumes reading the untouched EyeAngles).
	// Accumulates Input.AnalogLook the same way the orbit rig's ApplyLook does (yaw free, pitch clamped).
	// Movement stays live during the orbit and keeps steering relative to the FROZEN aim, not the swung
	// camera — walking while inspecting yourself doesn't re-aim you.
	void UpdateAltOrbit( bool play )
	{
		bool want = play && GameSettings.HunterThirdPerson && Input.Down( "Walk" );

		if ( want && !_altOrbiting )
		{
			_altOrbitAngles = _controller.EyeAngles;
			_altDistance = ThirdPersonDistance;
			_altPan = Vector3.Zero;
		}
		_altOrbiting = want;

		if ( !_altOrbiting )
			return;

		// Alt+RMB dollies, alt+MMB pans up/down — the hider's exact drag gestures (exponential zoom so it
		// feels even at any distance; pan scaled by distance, along the swung camera's up). Each is exclusive
		// with the orbit rotation, mirroring the hider's early-outs, so a drag never also spins the view.
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

	// Position the shared scene camera — at the eye in first person, or on an over-the-shoulder boom behind
	// it in third person (GameSettings.HunterThirdPerson). The rotation is the eye angles EITHER WAY: third
	// person only moves the viewpoint, never the aim, so shots keep leaving from the eye along the crosshair.
	// The boom pulls in when geometry blocks it (same trace as the orbit rig's boom) so it can't see through
	// walls. Transform is written directly; FOV is declared through MainCamera (which owns the ease) and
	// asserted every frame, so whatever the previous driver left targeted — the orbit rig runs at
	// GameSettings.OrbitFov — glides back to hunter FOV rather than sticking. Still no render-setting
	// changes: one camera, left clean for the next pawn that drives it.
	void DriveCamera( Vector3 eye )
	{
		var cam = Scene.Camera;
		if ( !cam.IsValid() )
			return;

		// Boom orientation: the aim, unless an alt-orbit has swung the camera off it (third person only —
		// _altOrbiting can't be true in first person). The camera ROTATION rides the same angles, so the
		// orbit looks back along the boom at the pawn.
		var rot = (_altOrbiting ? _altOrbitAngles : _controller.EyeAngles).ToRotation();
		var pos = eye;

		if ( GameSettings.HunterThirdPerson )
		{
			// y is LEFT in s&box, so the right-shoulder offset goes in negated. During an alt-orbit the boom
			// length is the live dolly distance and the anchor carries the pan offset (both discarded on
			// release — see UpdateAltOrbit). Boom traced from the anchor with a small radius so the near
			// plane can't peek through a wall the ray barely misses; own hierarchy ignored (the head trigger
			// + move capsule live under the pawn). Triggers aren't hit by default, so other hunters' head
			// colliders can't yank the boom in.
			float dist = _altOrbiting ? _altDistance : ThirdPersonDistance;
			var anchor = _altOrbiting ? eye + _altPan : eye;

			// During an alt-orbit the shoulder/rise offsets scale WITH the dolly (1 at the seed distance, since
			// _altDistance seeds from ThirdPersonDistance): the whole boom vector shrinks/grows proportionally,
			// so zooming moves the camera along a straight ray toward the player instead of sliding past them
			// beside the fixed shoulder offset. Play mode keeps the authored offsets untouched.
			float offsetScale = _altOrbiting ? dist / MathF.Max( ThirdPersonDistance, 1f ) : 1f;
			var desired = anchor + rot * new Vector3( -dist, -ThirdPersonShoulder * offsetScale, ThirdPersonRise * offsetScale );
			var tr = Scene.Trace.Ray( anchor, desired )
				.Radius( 8f )
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();
			pos = tr.Hit ? tr.EndPosition : desired;
		}

		cam.WorldPosition = pos;
		cam.WorldRotation = rot;
		MainCamera.Fov = GameSettings.HunterFov;
	}

	// First person: hide our OWN body so we don't see it but it still casts a shadow, while proxies (other
	// players' hunters) stay fully visible. Plain body parts switch to ShadowsOnly; SDF visuals do the same
	// via RenderHidden (they own their sibling mesh, so we can't just set its RenderType). Done by renderer
	// state, NOT by excluding a tag on the shared camera (tag-excluding mutates the shared camera — the bug
	// we just fixed — and would affect every pawn). Live each frame because ownership resolves after OnStart;
	// setting a value to its current value is a no-op.
	void HideOwnBody()
	{
		// Hidden in first person — but while editing your own face you need to SEE yourself, so show it then,
		// and in third person the whole point is seeing your own hunter, so show it there too. Always false
		// on proxies: other players' hunters render fully.
		var hideOwn = !IsProxy && !EditMode && !GameSettings.HunterThirdPerson;

		// Run puffs go shadows-only in first person too. ParticleModelRenderer has no ShadowRenderType, but
		// shadows-only IS just CastShadows + ExcludeGameLayer at the SceneObject level, and its RenderOptions.Game
		// maps to ExcludeGameLayer — re-applied to every live particle each frame, so this flips existing puffs
		// as well as new ones. CastShadows stays on from the prefab, untouched.
		//
		// Asserted on EVERY machine — unlike the body below — because the state a proxy ARRIVES with can't be
		// trusted: network spawn/refresh serializes the owner's LIVE GameObject JSON, RenderOptions.GameLayer
		// included, so a pawn shipped after its owner has been in first person lands with Game=false baked in.
		// Bailing on proxies left nothing to restore it and remote puffs rendered as shadows only. Rendering
		// flags are per-machine (no sync), so a proxy re-asserting its own copy networks nothing.
		if ( _runRenderers is not null )
		{
			foreach ( var r in _runRenderers )
			{
				if ( r.IsValid() )
					r.RenderOptions.Game = !hideOwn;
			}
		}

		// Body/SDF first-person hide: our own pawn only — a proxy pawn (another player's) isn't ours to touch.
		if ( IsProxy )
			return;

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
	}

	// The body and fist are always made of the same clay as the head: every frame, copy the face's FIRST
	// authored brush's material (colour/metallic/roughness) onto every authored brush of the other pawn
	// sculptures, rebuilding only when something actually changed (material is baked into the field texture
	// and mesh vertex colours, so a repack is required — but these sculptures are tiny). Checked per frame
	// rather than hooked, so every way the face can change is covered: the persist-slot load on spawn, live
	// palette edits, and network sync on proxies — each machine derives the same body material from the same
	// synced face brushes. Damage craters keep their scorched CarveColor on both ends: they're skipped as a
	// source (index 0 can only be one if the head was carved down to nothing) and as a target.
	void MatchBodyMaterialToFace()
	{
		if ( _bodySculpts is not { Length: > 0 } || !Face.IsValid() )
			return;

		var src = Face.Brushes?.FirstOrDefault( b => !b.Damage );
		if ( src is null )
			return;

		foreach ( var sculpt in _bodySculpts )
		{
			if ( !sculpt.IsValid() || sculpt.Brushes is null )
				continue;

			bool changed = false;
			foreach ( var b in sculpt.Brushes )
			{
				if ( b.Damage )
					continue;
				if ( b.Color == src.Color && b.Metallic == src.Metallic && b.Roughness == src.Roughness )
					continue;

				b.Color = src.Color;
				b.Metallic = src.Metallic;
				b.Roughness = src.Roughness;
				changed = true;
			}

			if ( changed )
				sculpt.Rebuild();
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

		// Jump push-off: the takeoff half of the pair whose landing half the engine's physics thump already
		// covers. Leaving the ground OPENS a watch; the verdict lands a few frames later when the observed
		// (interpolated, proxy-safe) transform shows which way we went — up = jump, play; down/stall = a
		// ledge walk-off, silent. (Step-up teleports move z sharply but never drop grounded, so no false
		// fires; a grounded flicker on a stair lip just cancels the watch.)
		bool groundedNow = _controller.IsOnGround;
		var livePos = _controller.WorldPosition;

		if ( groundedNow )
			_takeoffSurface = _controller.GroundSurface; // remembered for the push-off, see _takeoffSurface

		if ( _wasGrounded && !groundedNow )
		{
			_takeoffWatch = true;
			_takeoffZ = livePos.z;
			_sinceTakeoff = 0f;
		}
		_wasGrounded = groundedNow;

		if ( _takeoffWatch )
		{
			if ( groundedNow || livePos.z - _takeoffZ < -6f || _sinceTakeoff > 0.25f )
			{
				_takeoffWatch = false; // re-grounded, falling, or stalled: not a jump
			}
			else if ( livePos.z - _takeoffZ > 6f )
			{
				_takeoffWatch = false;
				if ( JumpStepVolume > 0f )
				{
					_stepFoot = 1 - _stepFoot;
					PlayJumpStep();
				}
			}
		}

		if ( !groundedNow )
		{
			_stepSeeded = false;
			_stepAccum = 0f;
			return;
		}

		var pos = livePos;

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

	// The jump push-off sound. Mirrors the tail of the engine's PlayFootstepSound (surface sound event →
	// footstep mixer → FootstepVolume) but sources the surface from _takeoffSurface — the engine method
	// reads the LIVE GroundSurface, which is already invalid by the time an airborne jump verdict lands,
	// making it silently no-op mid-air.
	void PlayJumpStep()
	{
		var soundEvent = _takeoffSurface?.SoundCollection is { } collection
			? (_stepFoot == 0 ? collection.FootLeft : collection.FootRight)
			: null;
		if ( soundEvent is null )
			return;

		var handle = GameObject.PlaySound( soundEvent, 0 );
		if ( !handle.IsValid() )
			return;

		handle.FollowParent = false;
		handle.TargetMixer = _controller.FootstepMixer.GetOrDefault();
		handle.Volume *= JumpStepVolume * _controller.FootstepVolume;
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
		else if ( _orbit.IsValid() ) // leaving: remember the view (face-relative), zoom and pan for the next edit session
			_lastEditView = (
				new Angles( _orbit.Angles.pitch, _orbit.Angles.yaw - FaceYaw(), 0f ),
				_orbit.Distance,
				Rotation.FromYaw( FaceYaw() ).Inverse * (_orbit.Pivot - FaceCenterWorld()) );
	}

	// The last edit session's view, zoom and pan, stored RELATIVE to the face (yaw-relative angles; the panned
	// pivot as an offset from the face centre in the face's yaw frame) — so the restored view stays glued to
	// the head even if the pawn turned or moved between edits. Null until an edit session has ended: the very
	// first entry frames from the front at the auto-fit distance, centred.
	(Angles view, float distance, Vector3 panOffset)? _lastEditView;

	float FaceYaw() => _controller.IsValid() ? _controller.EyeAngles.yaw : WorldRotation.Angles().yaw;

	// Park the orbit camera on the face: the FIRST entry frames it from the front (along the head's facing,
	// aiming back at it); later entries restore wherever you last left the view. Must run AFTER the session
	// enables the camera, since OrbitCameraController.OnEnabled seeds pivot/angles from the (first-person) view.
	void FrameFace()
	{
		if ( !_orbit.IsValid() )
			return;

		if ( _lastEditView is { } last )
		{
			_orbit.Pivot = FaceCenterWorld() + Rotation.FromYaw( FaceYaw() ) * last.panOffset;
			_orbit.Distance = last.distance;
			_orbit.Angles = new Angles( last.view.pitch, FaceYaw() + last.view.yaw, 0f );
		}
		else
		{
			_orbit.Pivot = FaceCenterWorld();
			_orbit.Distance = FramingDistance();
			_orbit.Angles = new Angles( EditCameraPitch, FaceYaw() + 180f, 0f ); // +180: stand in front, look back at the face
		}
	}

	// Distance that fits the head's bounding sphere in the frame with EditFramingMargin breathing room, derived
	// from the FOV edit mode settles at (GameSettings.OrbitFov) — NOT the live camera, which at edit entry is
	// still easing away from the hunter's first-person FOV and would over-frame. Standard fit-sphere math: a
	// sphere of radius r is tangent to the view cone at distance r / sin(halfFov). The FOV is the VERTICAL fov
	// (the tighter axis on a wide screen), so fitting against it guarantees the head fits horizontally too.
	// Falls back to a fixed distance if bounds aren't ready.
	float FramingDistance()
	{
		const float fallback = 60f;

		if ( !Face.IsValid() || !Sdf.TryGetBounds( Face.Brushes, out var bounds, SculptEditSession.PendingStamp( Face ) ) )
			return fallback;

		// Bounding-sphere radius = half the box diagonal, in world units.
		float radius = bounds.Size.Length * 0.5f * Face.WorldScale.x;
		if ( radius <= 0.01f )
			return fallback;

		float halfFov = GameSettings.OrbitFov.DegreeToRadian() * 0.5f;
		float sin = MathF.Sin( halfFov );
		if ( sin <= 0.001f )
			return fallback;

		return radius * EditFramingMargin / sin;
	}

	// World-space centre of the face's sculpted shape (its brush bounds), so the camera frames the head itself
	// rather than its pivot at the neck. Falls back to the object/eye position if bounds aren't available yet.
	Vector3 FaceCenterWorld()
	{
		if ( Face.IsValid() && Sdf.TryGetBounds( Face.Brushes, out var bounds, SculptEditSession.PendingStamp( Face ) ) )
			return Face.WorldTransform.PointToWorld( bounds.Center );

		if ( Face.IsValid() )
			return Face.WorldPosition;

		return _controller.IsValid() ? _controller.EyePosition : WorldPosition;
	}

	void Shoot( Vector3 from )
	{
		if ( !_controller.IsValid() )
			return;

		var aimRot = _controller.EyeAngles.ToRotation();
		var dir = aimRot.Forward;

		_nextShot = ShootCooldown;

		// Every machine plays the bang and nearby cameras rattle (the RPC also runs locally). The EVENT is
		// what's broadcast — each machine plays its own prefab-authored ShootSound, so no asset reference
		// crosses the wire.
		BroadcastShotEffects();

		// Recoil for the shooter. Owner-side only — proxies feel this shot through the epicenter shake in
		// the RPC instead.
		if ( ShotKick > 0f )
			Scene.Camera?.AddPunch(
				new Angles( -ShotKick, Game.Random.Float( -ShotKickYawJitter, ShotKickYawJitter ) * ShotKick, 0f ),
				frequency: ShotKickBounce, duration: ShotKickTime, fovAmplitude: ShotKickFov );

		// CENTRAL pellet: exactly the crosshair ray, full carve size, and the ONLY pellet that counts for
		// catching props — hit registration must never depend on a random scatter roll.
		var tr = TraceShot( from, dir );
		if ( tr.Hit )
		{
			// Cosmetic carve on ANY sdf surface the shot lands on (decoys, world props, disguises, faces —
			// a live prop that's about to pop just doesn't get to enjoy its crater). Before the hit report,
			// so wrong guesses leave visible evidence in the clay.
			PelletCarve( from, dir, CarveRadius, tr.Distance );

			// A hit on a prop pawn (its disguise collider belongs to a HiderController up the hierarchy) is
			// reported to the host, which validates the phase + that it's a live prop, then pops it and
			// converts that player to a hunter. Anything else is just map geometry. [Rpc.Host] routes it.
			// With no round running, a DebugGameMode scene handles the hit instead (the prop just pops).
			var hider = FindHider( tr.GameObject );
			if ( hider.IsValid() )
			{
				if ( RoundManager.Current.IsValid() )
					RoundManager.Current.ReportPropHit( hider.GameObject );
				else if ( DebugGameMode.Current.IsValid() )
					DebugGameMode.Current.ReportPropHit( hider.GameObject );
			}
		}

		// SCATTER pellets: purely cosmetic buckshot — random directions inside the CarveScatter cone, each
		// carving a smaller crater. The shooter rolls the randomness and broadcasts concrete positions and
		// radii, so every machine ends up with the identical formation without any seed syncing.
		for ( int i = 1; i < CarvePellets; i++ )
		{
			// Uniform disc sample in angle space (sqrt for area-uniform), rotated into the aim frame.
			float ang = Game.Random.Float( 0f, MathF.Tau );
			float off = MathF.Sqrt( Game.Random.Float( 0f, 1f ) ) * CarveScatter;
			var pelletDir = (aimRot * Rotation.From( new Angles(
				MathF.Sin( ang ) * off, MathF.Cos( ang ) * off, 0f ) )).Forward;

			var ptr = TraceShot( from, pelletDir );
			if ( ptr.Hit )
				PelletCarve( from, pelletDir, CarveRadius * Game.Random.Float( 0.45f, 0.8f ), ptr.Distance );
		}
	}

	// UI blip for a trigger pull denied by the round phase (Hide/Reveal). Local feedback only — nothing is
	// broadcast, so other players never hear a denied click. Played as a raw file (ListenLocal, no world
	// spatialisation) rather than a SoundEvent asset: it's pure interface feedback, not a world sound.
	const string ShotDeniedSoundPath = "sounds/kenney/ui/error_005.wav";

	void PlayShotDeniedSound()
	{
		var handle = Sound.PlayFile( SoundFile.Load( ShotDeniedSoundPath ) );
		if ( !handle.IsValid() )
			return;

		handle.ListenLocal = true;
		handle.DistanceAttenuation = false;
		handle.AirAbsorption = false;
		handle.OcclusionEnabled = false;
		handle.ReverbEnabled = false;
	}

	// Both gun rays HitTriggers(): hunter heads are TRIGGER colliders (SdfCollider.BuildAsTrigger — physically
	// contactless, bullet-visible only), so without it a shot could never land on a face. The WithoutTags guard
	// keeps actual volume triggers bullet-transparent — any map trigger volume must carry the "trigger" (or
	// "water") tag or it will eat shots.
	SceneTraceResult TraceShot( Vector3 from, Vector3 dir ) => Scene.Trace
		.Ray( from, from + dir * Range )
		.IgnoreGameObjectHierarchy( GameObject )
		.HitTriggers()
		.WithoutTags( "trigger", "water" )
		.Run();

	// The GameObject tag on invisible MOVEMENT colliders (the hunter's capsule) that the carve trace sees
	// through: the capsule fully encloses the sculpted head, so the gameplay trace always stops on it and a
	// carve aimed at a face would never reach the head's own collider. The head's own collider is the inverse:
	// a trigger (see SdfCollider.BuildAsTrigger), so it never physically collides with the world or blocks
	// anyone — it exists ONLY for these traces, which opt in via HitTriggers(). Its "headcollider" tag +
	// default-Ignore collision rule (ProjectSettings/Collision.config) stay on top of that to suppress even
	// trigger-touch events as it sweeps through walls and players.
	const string MoveColliderTag = "movecollider";

	// How far past the gameplay hit the carve trace may land and still carve. Covers "the face is a little
	// behind the capsule surface" without letting a chest shot carve the wall metres behind the pawn.
	const float CarvePassDepth = 25f;

	// The carve half of one pellet: re-trace ignoring movement capsules (so a face shot reaches the head's
	// sculpture collider behind the capsule), bounded to just past wherever the gameplay ray stopped.
	void PelletCarve( Vector3 from, Vector3 dir, float radius, float blockDistance )
	{
		var tr = Scene.Trace
			.Ray( from, from + dir * Range )
			.IgnoreGameObjectHierarchy( GameObject )
			.HitTriggers()
			.WithoutTags( MoveColliderTag, "trigger", "water" )
			.Run();

		if ( tr.Hit && tr.Distance <= blockDistance + CarvePassDepth )
			TryCarve( tr, dir, radius );
	}

	// ── Shot carving ─────────────────────────────────────────────────────────────────────────────────
	// A shot that lands on an SDF surface appends a subtractive sphere to the TOP of that sculpture's brush
	// stack (applied after everything below it — a true carve out of the union), then rebuilds. Applied on
	// EVERY machine via broadcast so the world stays consistent; for a synced disguise the owner's Committed
	// republish then makes the carve durable/authoritative (late joiners of synced sculptures get it too).
	//
	// Addressing across machines is the subtle part: a disguise/head is a RUNTIME clone with a different
	// GameObject id on every machine, so the RPC can't reference it directly — it references the NETWORKED
	// pawn root instead, and each machine re-resolves the pawn's own sculpture locally. Scene-placed
	// sculptures (decoys, world props) share their scene-file ids on every machine, so they pass directly.
	void TryCarve( SceneTraceResult tr, Vector3 dir, float radius )
	{
		if ( radius <= 0.1f )
			return;

		// Walk up from the hit collider: the first sculpture found is the carve target; if the chain tops
		// out in a pawn controller, the PAWN is the network-safe address for it.
		var go = tr.GameObject;
		SdfSculpture sculpt = null;
		GameObject anchor = null;
		while ( go.IsValid() )
		{
			if ( sculpt is null )
			{
				var s = go.Components.Get<SdfSculpture>();
				if ( s.IsValid() )
					sculpt = s;
			}

			if ( go.Components.Get<HiderController>().IsValid() || go.Components.Get<HunterController>().IsValid() )
			{
				anchor = go;
				break;
			}

			go = go.Parent;
		}

		// Hit a pawn but found no sculpture on the chain: shapes aggregated into the pawn's RIGIDBODY report
		// the PAWN ROOT as the hit GameObject (the stock controller's own body shape does — the lobby debug
		// log showed exactly this), so the child-collider walk can't see them. Fall back to the pawn's
		// canonical sculpture — the same mapping the receiver uses — and let the bounds gate below keep only
		// hits that actually land near it.
		if ( sculpt is null && anchor.IsValid() )
			sculpt = ResolveCarveSculpture( anchor );

		if ( sculpt is null || !sculpt.IsValid() )
			return;
		anchor ??= sculpt.GameObject;

		// Centre pushed a little INTO the surface along the shot for a meatier bite than a surface-tangent
		// hemisphere. LOCAL space, so pawn interpolation between send and receive can't smear the crater.
		var local = sculpt.WorldTransform.PointToLocal( tr.EndPosition + dir * ( radius * 0.35f ) );

		// Only carve where the sculpture actually IS. A body shot resolved through the pawn fallback maps
		// far from the head — an invisible crater in empty space would silently burn a brush slot.
		if ( sculpt.Brushes is { Count: > 0 } && Sdf.TryGetBounds( sculpt.Brushes, out var bounds ) )
		{
			var grown = new BBox( bounds.Mins - radius, bounds.Maxs + radius );
			if ( !grown.Contains( local ) )
				return;
		}

		// Whether (and how fast) this crater heals is the TARGET's policy — a DamageProfile composed beside
		// its sculpture (gameplay data, kept off the mode-agnostic SDF components; absent = scars persist).
		// The shooter reads the target's prefab-authored config and rolls the timing HERE, once, shipping
		// concrete values — every machine must agree on when each crater vanishes, so the randomness can't
		// be rolled per machine. Staggered, not lockstep.
		var profile = sculpt.GameObject.Components.Get<DamageProfile>();
		bool heals = profile.IsValid() && profile.Heals;
		float delay = heals ? Game.Random.Float( profile.HealDelay.x, profile.HealDelay.y ) : 0f;
		float duration = heals ? Game.Random.Float( profile.HealDuration.x, profile.HealDuration.y ) : 0f;
		BroadcastCarve( anchor, local, radius, heals, delay, duration );
	}

	[Rpc.Broadcast]
	void BroadcastCarve( GameObject anchor, Vector3 localPos, float radius, bool shrinks, float shrinkDelay, float shrinkDuration )
	{
		var sculpt = ResolveCarveSculpture( anchor );
		if ( !sculpt.IsValid() || sculpt.Brushes is null )
			return;

		// The wet hit, one per crater at the hole itself — before the brush-cap bail, because the pellet
		// physically hit clay whether or not the crater still fits the stack.
		PlaySplat( sculpt.WorldTransform.PointToWorld( localPos ), radius );

		// Past the packer cap brushes silently don't pack — the raymarch would diverge from mesh/collision
		// with no warning. A missing crater beats an inconsistent one.
		if ( sculpt.Brushes.Count >= SdfBrushPacker.MaxBrushes )
			return;

		sculpt.Brushes.Add( new SdfBrush
		{
			Shape = SdfShape.Sphere,
			Operation = SdfOperation.Subtract,
			Position = localPos,
			Size = radius,
			Blend = CarveBlend,
			Color = CarveColor,
			Damage = true, // edit UI skips it; authored brushes insert below the damage tail
			Shrinks = shrinks, // the target's HealsDamage, with the shooter's rolled timing
			ShrinkDelay = shrinkDelay,
			ShrinkDuration = shrinkDuration,
		} );

		// Full rebuild: mesh LODs, field redispatch, and Committed — which re-solidifies the collider
		// (the carve-aware path keeps hollows passable) and, on a synced disguise's owner, republishes.
		sculpt.Rebuild();
	}

	// A splat at a crater, SplatDelay (+ a small per-pellet stagger) after the pellet lands, so the gunshot's
	// crack decays before the wet hits arrive. Detached point sound at the hole's world position — captured
	// before the wait, so the crater the ear is told about is the one the eye saw carved. Volume rides the
	// crater size, so the central pellet's full-radius hit leads and the smaller scatter craters layer under
	// it instead of four identical splats stacking into one loud blob. Component.Task cancels the wait on
	// pawn teardown — same pattern as the muzzle flash expiry.
	async void PlaySplat( Vector3 position, float radius )
	{
		if ( SplatSound is null )
			return;

		float delay = SplatDelay + Game.Random.Float( 0f, 0.06f );
		if ( delay > 0f )
			await Task.DelaySeconds( delay );

		var handle = Sound.Play( SplatSound, position );
		if ( handle.IsValid() )
			handle.Volume *= MathF.Min( radius / MathF.Max( CarveRadius, 0.1f ), 1f );
	}

	// The pawn-anchor → sculpture mapping, mirrored on every machine: a hider pawn carves its disguise, a
	// hunter pawn its face, anything else is a scene sculpture addressed directly.
	static SdfSculpture ResolveCarveSculpture( GameObject anchor )
	{
		if ( !anchor.IsValid() )
			return null;

		var hider = anchor.Components.Get<HiderController>();
		if ( hider.IsValid() )
			return hider.DisguiseSculpture;

		var hunter = anchor.Components.Get<HunterController>();
		if ( hunter.IsValid() )
			return hunter.Face;

		return anchor.Components.Get<SdfSculpture>();
	}

	// The gunshot, on every machine, from the shooter's pawn. Detached from the pawn so the tail of the
	// sound stays where the shot happened instead of chasing a sprinting hunter. The SHOOTER's own copy
	// plays flat 2D (SpacialBlend 0): spatializing your own shot against your own head panned/attenuated
	// it oddly — the classic FPS split is punchy 2D for the local player, positioned 3D for everyone else.
	[Rpc.Broadcast]
	void BroadcastShotEffects()
	{
		// The gun models buck — the viewmodel through its springs (owner), the world model's hand jolt
		// (everyone). Lives on the RPC so proxies see remote hunters' guns recoil too.
		if ( _gun.IsValid() )
			_gun.Kick();

		// Everyone NEAR the shot feels it, falling off with distance from the shooter — a prop hiding by a
		// hunter gets rattled. The shooter is excluded (IsProxy): they get the recoil punch in Shoot instead,
		// and stacking both reads as double vision.
		if ( IsProxy && ShotShakeRadius > 0f && ShotShakeAmplitude > 0f )
			CameraEffectSystem.Get( Scene )?.AddShake( WorldPosition, ShotShakeRadius, ShotShakeAmplitude, ShotShakeFrequency, ShotShakeTime );

		if ( ShootSound is null )
			return;

		var handle = GameObject.PlaySound( ShootSound, 0 );
		if ( !handle.IsValid() )
			return;

		handle.FollowParent = false;

		if ( !IsProxy )
			handle.SpacialBlend = 0f;
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
