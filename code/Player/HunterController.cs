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
	/// — same eye angles — so third person is purely a view change. Shots still LEAVE from the eye, but they aim
	/// at whatever the crosshair is over (see <see cref="ResolveAim"/>), so the boom offsets below are free to
	/// be whatever frames best without pulling the pellets off the dot.</summary>
	[Property, Group( "Third Person" ), Range( 40f, 300f )] public float ThirdPersonDistance { get; set; } = 100f;

	/// <summary>Sideways offset of the boom, positive = over the RIGHT shoulder — so your own head doesn't sit
	/// exactly on the crosshair line.</summary>
	[Property, Group( "Third Person" ), Range( -48f, 48f )] public float ThirdPersonShoulder { get; set; } = 20f;

	/// <summary>Vertical offset of the boom above the eye.</summary>
	[Property, Group( "Third Person" ), Range( -32f, 64f )] public float ThirdPersonRise { get; set; } = 8f;

	/// <summary>Crosshair distance over which the pawn's VISUALS fade INTO convergence: at this distance and
	/// beyond they point exactly where the pellet goes, smoothstepping down to plain aim-parallel as the target
	/// approaches the muzzle. There's no upper limit — convergence is full for everything past this. Shots still
	/// converge exactly at every range (see <see cref="ResolveAim"/>); the fade exists purely to stop the gun and
	/// head swinging hard inward when you aim at something right in front of you, where the convergence angle is
	/// at its steepest and reads worst. 0 = no fade, always fully converged.</summary>
	[Property, Group( "Third Person" ), Range( 0f, 1000f )] public float ConvergeFade { get; set; } = 400f;

	/// <summary>How fast the visual aim eases toward its target (per second, exponential). This is what stops
	/// the gun snapping when the crosshair crosses between a near object and a far one — the convergence angle
	/// jumps, and this glides the pawn across it. Higher = tighter tracking, lower = lazier. 0 = no easing.</summary>
	[Property, Group( "Third Person" ), Range( 0f, 30f )] public float AimEaseRate { get; set; } = 10f;

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

	// This frame's aim, in its two forms — both written by ResolveAim (see it for why third person can't just use
	// the eye forward, and why the shot and the visuals don't share one direction). _aimDir is the exact
	// crosshair ray the SHOT takes; _visualAimDir is the eased, near-faded one the head and gun POINT along.
	// Zero = never resolved (any proxy, and the frames before the first owner update), and every reader falls
	// back to the raw eye angles. Deliberately NOT cleared while alt-orbiting or editing: both freeze the aim, so
	// holding the last resolved values keeps the pawn still instead of snapping it back to parallel.
	Vector3 _aimDir;
	Vector3 _visualAimDir;

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

		// Move the head + body off the physics root onto a written pivot — see PlaceVisualPivot. After Face is
		// resolved (Eyes/Face are the same object) and before anything caches renderers, so the sweeps below
		// still find them: they stay under this pawn, one level deeper.
		EnsureVisualPivot();

		// The head's bullet-hit collider, gated to proxies every frame — see UpdateHeadCollider for why ours has
		// to be off. Resolved from the face's object (Eyes and Face are the same GameObject).
		_headCollider = Face.IsValid() ? Face.GameObject.Components.Get<SdfCollider>() : null;

		// The torso sculpture the duck squashes — see UpdateBodyDeform. Resolved after EnsureVisualPivot so
		// _bodyObject is populated.
		_bodySculpt = _bodyObject.IsValid() ? _bodyObject.Components.Get<SdfSculpture>() : null;

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

	// The sculpture face-edit mode targets: the authored Face, else one on an object named "Head", else the first
	// sculpture anywhere under the pawn (so a bare/renamed setup still finds something to edit). Gun clones carry
	// sculptures too — filtered by tag so the fallback can't hand you the gun to face-edit.
	//
	// Searched at ANY depth, not just direct children: the head hangs off the Visuals pivot now (see
	// EnsureVisualPivot), and a direct-children lookup would miss it and quietly hand back the BODY instead —
	// the first sculpture it happened to find — leaving you sculpting the wrong part of yourself.
	SdfSculpture ResolveFace()
	{
		var sculpts = Components.GetAll<SdfSculpture>( FindMode.EnabledInSelfAndDescendants )
			.Where( s => !s.GameObject.Tags.Has( HunterGun.CloneTag ) )
			.ToArray();

		return sculpts.FirstOrDefault( s => s.GameObject.Name == "Head" ) ?? sculpts.FirstOrDefault();
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

		// Advance the springs FIRST — the head drop, the pivot height and the torso squash all read them this
		// frame, so they have to be current before any of them run.
		UpdateDuckSpring();
		UpdateJumpSpring();
		UpdateHeadBob();

		HideOwnBody();
		UpdateHeadCollider();
		UpdateBodyDeform();
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

			// Resolve the aim for this frame — read below by both the shot and the head/gun placement. Must come
			// after DriveCamera: it reads the camera pose the crosshair is drawn over. Skipped while alt-orbiting,
			// which freezes the aim and swings the camera off it: re-running would converge the pawn onto the
			// ORBIT's crosshair and drag its head around with the gesture, and zeroing would snap it back to
			// parallel the instant you grabbed alt. Holding the last values leaves it exactly where it was.
			if ( !_altOrbiting )
				ResolveAim( eye );

			// Owner-only: otherwise every machine would shoot when ITS local player clicked, from a remote pawn's
			// eye. The trace is owner-side; a prop hit is reported to the host (authoritative) via RoundManager.
			// No shooting while controls are locked (the Starting countdown) or outside the Hunt — during Hide a
			// shot would still carve permanent craters into disguises the props can't heal, even though the host
			// ignores the hit report. A denied press gets a local error blip instead, so the trigger doesn't feel
			// broken. The shot leaves from the eye along _aimDir, which is converged onto the crosshair, so the
			// trace always matches what the dot shows. During an alt-orbit the trigger is swallowed (no blip): alt+mouse is a
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

		ComposePawn( eye );
	}

	/// <summary>Pivot the pawn's VISUALS hang off. Found by name ("Visuals") or created; the head and body are
	/// reparented under it at start. See <see cref="PlaceVisualPivot"/>.</summary>
	[Property, Group( "Debug" )] public GameObject VisualPivot { get; set; }

	// The head's bullet-hit collider — enabled on proxies only, see UpdateHeadCollider.
	SdfCollider _headCollider;

	// The body, resolved once so EnsureVisualPivot can move it under the pivot.
	GameObject _bodyObject;

	// Smoothed eye height with the duck taken OUT — what the visual pivot rides. See SmoothedEyePosition.
	float _standZ;

	/// <summary>Stiffness of the duck spring, in radians/sec — higher snaps down faster. Drives the head drop and
	/// the torso squash together.</summary>
	[Property, Group( "Ducking" ), Range( 4f, 40f )] public float DuckSpringRate { get; set; } = 16f;

	/// <summary>Damping of the duck spring. 1 = no overshoot at all, lower = more bounce as it settles and more
	/// rebound on standing up. Around 0.6 gives a slight, weighty spring.</summary>
	[Property, Group( "Ducking" ), Range( 0.2f, 1f )] public float DuckSpringDamping { get; set; } = 0.6f;

	// The duck springs: _duckSpring drives the EYE height (snapped while airborne — the engine compensates the
	// root there), _duckVisual drives the torso squash, the chest drop and the mid-air tuck (always springs).
	// Identical on the ground; they only diverge in the air. See UpdateDuckSpring.
	float _duckSpring;
	float _duckVelocity;
	float _duckVisual;
	float _duckVisualVelocity;

	// The engine's instant mid-air lift, banked so the tuck can be eased in rather than teleported. See
	// UpdateDuckSpring / AirTuckOffset.
	float _airLift;
	bool _wasDucking;

	/// <summary>How far the body trails below the pawn on a full-speed jump, in world units. It springs back up
	/// to catch you — the body having weight rather than being welded to your feet. 0 disables it.</summary>
	[Property, Group( "Jump" ), Range( 0f, 32f )] public float JumpLagDistance { get; set; } = 8f;

	/// <summary>How far the body dips on landing, in world units, at an impact as fast as a full jump. Scales
	/// with how hard you actually hit, so a small hop barely registers and a long drop lands heavy.</summary>
	[Property, Group( "Jump" ), Range( 0f, 48f )] public float LandDipDistance { get; set; } = 12f;

	/// <summary>Stiffness of the jump/land spring, in radians/sec — higher catches up faster.</summary>
	[Property, Group( "Jump" ), Range( 4f, 40f )] public float JumpSpringRate { get; set; } = 13f;

	/// <summary>Damping of the jump/land spring. Lower = more wobble as it settles.</summary>
	[Property, Group( "Jump" ), Range( 0.2f, 1f )] public float JumpSpringDamping { get; set; } = 0.45f;

	/// <summary>How much of the LAUNCH follow-through the chest takes, against the belly taking all of it. The
	/// difference between the two is the stretch: 0 pins the chest to the head for the most exaggerated pull,
	/// 1 moves the whole torso as one piece and there's no stretch at all.</summary>
	[Property, Group( "Jump" ), Range( 0f, 1f )] public float JumpChestFollow { get; set; } = 0.25f;

	/// <summary>How far the CHEST dips on landing, in world units — its own distance, independent of both the
	/// belly's and the head's. Defaults to 0: the chest sits an impact out and the belly compresses up into it,
	/// which reads as the impact travelling through the soft part. Raising it drives the chest down on top of
	/// whatever the crouch has already done to it, so keep it modest.</summary>
	[Property, Group( "Jump" ), Range( 0f, 24f )] public float LandChestDipDistance { get; set; }

	/// <summary>How far the HEAD — and with it the gun arm and the camera — dips on landing, in world units. Its
	/// own distance rather than a share of the chest's, so the two are independent: you can hold the chest
	/// perfectly still (<see cref="LandChestDipDistance"/> = 0) and still have the impact land in the view.
	/// Whatever gap this opens against the chest reads as the neck compressing.</summary>
	[Property, Group( "Jump" ), Range( 0f, 24f )] public float LandHeadDipDistance { get; set; } = 1.5f;

	/// <summary>How much of that head dip survives when you land fully CROUCHED, as a fraction of the standing
	/// amount. A folded body has already absorbed part of the impact, so a view answering it as hard as a
	/// standing landing reads as too much. 1 makes crouched landings hit the view exactly as standing ones do;
	/// 0 removes the head dip from them entirely. Eased by how crouched you actually are.</summary>
	[Property, Group( "Jump" ), Range( 0f, 1f )] public float LandCrouchHeadAmount { get; set; } = 0.5f;

	/// <summary>Let the landing dip move the FIRST PERSON view. Off, the impact plays out on the body and the
	/// world model exactly as before and only the view sits still — nothing else changes, so it's purely a taste
	/// call. Third person always takes the dip regardless: there you can see the landing, and a view that ignored
	/// it would read as disconnected. The launch trail never touches the camera either way.</summary>
	[Property, Group( "Jump" )] public bool FirstPersonLandDip { get; set; } = true;

	/// <summary>The same, for the chest's LAUNCH trail — but the CAMERA sits this one out entirely, so it moves the
	/// head and the gun arm only. That's the point: you watch your own head lag behind on takeoff while the view
	/// stays snappy, where a dipping camera would fight the leap. Defaults to 1, since off the ground everything
	/// above the belly tends to move as one and the stretch happens below it. Sits DOWNSTREAM of
	/// <see cref="JumpChestFollow"/>, so raise that first if the takeoff needs more travel overall.</summary>
	[Property, Group( "Jump" ), Range( 0f, 1f )] public float JumpHeadFollow { get; set; } = 1f;

	/// <summary>How far the BELLY flattens at a full-strength landing, as a fraction of its authored height. The
	/// chest is never squashed by this — the impact travels up through the soft part of the body.</summary>
	[Property, Group( "Jump" ), Range( 0f, 0.9f )] public float LandSquash { get; set; } = 0.3f;

	/// <summary>How far the belly spreads sideways as it takes a landing — squash-and-stretch, as a fraction of
	/// its authored width.</summary>
	[Property, Group( "Jump" ), Range( 0f, 0.9f )] public float LandBulge { get; set; } = 0.15f;

	/// <summary>Stiffness of the landing SQUASH, separate from the dip's. Lower holds the blob longer — the
	/// squash starts at full and only decays, so at the dip's rate it would vanish almost on arrival.</summary>
	[Property, Group( "Jump" ), Range( 2f, 30f )] public float LandSquashRate { get; set; } = 7f;

	/// <summary>Damping of the landing squash. Well under 1 lets it wobble past flat into a stretch and back a
	/// couple of times before settling, which is what reads as soft clay rather than a rigid pop.</summary>
	[Property, Group( "Jump" ), Range( 0.15f, 1f )] public float LandSquashDamping { get; set; } = 0.35f;

	// Vertical follow-through offsets for the body, in world units (negative = below rest). Launch and landing
	// are tracked separately so the chest can take a different share of each — see UpdateJumpSpring.
	float _jumpLag;
	float _jumpLagVelocity;
	float _landLag;
	float _landLagVelocity;
	float _landChestLag;
	float _landChestLagVelocity;
	float _landHeadDip;
	float _landHeadDipVelocity;
	float _fallSpeed;
	bool _wasAirborne;

	// The landing squash, in impact units (1 = landing as fast as a full jump). Set outright at contact and
	// sprung back to 0, so it hits hardest the instant you touch down — see UpdateJumpSpring.
	float _landSquash;
	float _landSquashVelocity;

	/// <summary>Strength of the walk bob, in world units of travel at full run. The gun arm gets the full motion,
	/// sway included; the head gets the vertical only. The camera never takes any of it — see
	/// <see cref="UpdateHeadBob"/> — so this is purely something you watch on your own body in third person and
	/// on other players. 0 = off.</summary>
	[Property, Group( "Head Bob" ), Range( 0f, 8f )] public float HeadBobAmount { get; set; } = 1.5f;

	/// <summary>Bob cadence multiplier. 1 = exactly one bounce per footstep at any speed, sharing the footstep
	/// sounds' stride clock, so the body and the audio tick together. 2 = double-time, 0.5 = half.</summary>
	[Property, Group( "Head Bob" ), Range( 0.25f, 3f )] public float HeadBobFrequency { get; set; } = 1f;

	/// <summary>How far the hand swings FORWARD and BACK, relative to its sideways sway. 1 = as much fore-aft as
	/// lateral, which traces a round arc; 0 = sway only. Real arm swing is mostly this axis, so it's the one to
	/// lean on if the walk looks stiff — though the gun is held out in front, so a large value pumps it toward
	/// and away from the camera more than an empty hand would.</summary>
	[Property, Group( "Head Bob" ), Range( 0f, 2f )] public float HandBobForward { get; set; } = 0.6f;

	/// <summary>How far the HAND's bob runs behind the head's, in footsteps. The head is the one kept locked to
	/// the step clock — a head dipping is your weight arriving on a foot, so that's what has to land with the
	/// step sound — and the hand drifts off it, the way arms swing in counterpoint to legs rather than in
	/// lockstep. A real time shift, so the sway and the bounce both move together by the same amount. 0 = welded
	/// to the head (which is the mechanical-looking one), 1 = a whole step behind, which is back in sync.</summary>
	[Property, Group( "Head Bob" ), Range( 0f, 1f )] public float HandBobOffset { get; set; } = 0.25f;

	// Walk bob. The ARM takes the full thing in aim space (y lateral, z vertical); the HEAD takes the vertical
	// only. Same phase and blend, so they're one motion with the sway filtered out of the head. See UpdateHeadBob.
	Vector3 _armBob;
	float _headBob;
	float _bobPhase;
	float _bobBlend;

	/// <summary>How far the torso flattens when ducked, as a fraction of its authored height. 0 = no squash.</summary>
	[Property, Group( "Ducking" ), Range( 0f, 0.9f )] public float DuckSquash { get; set; } = 0.35f;

	/// <summary>How far the torso spreads sideways as it flattens — the squash-and-stretch bulge, as a fraction
	/// of its authored width. 0 = it just gets shorter.</summary>
	[Property, Group( "Ducking" ), Range( 0f, 0.9f )] public float DuckBulge { get; set; } = 0.15f;

	/// <summary>How much of the duck's eye drop the chest follows down. 1 = exactly as far as the head.</summary>
	[Property, Group( "Ducking" ), Range( 0f, 1f )] public float DuckChestFollow { get; set; } = 1f;

	// The torso sculpture we deform when ducking, plus its authored brush poses — see UpdateBodyDeform.
	SdfSculpture _bodySculpt;
	(SdfBrush Brush, Vector3 Pos, Vector3 Size)[] _bodyRest;
	bool _deformApplied;

	// One place the pawn's visuals hang off, so the head and body share a single anchor instead of each deriving
	// their own from the pawn root. It makes the visual hierarchy mirror the gun's (pawn → Shoulder → Hand → gun),
	// which is easier to reason about — it is NOT what fixed the jitter; that was the head's collider, see
	// UpdateHeadCollider.
	//
	// YAW ONLY: the body turns to face where you look, while pitch stays out of it so looking up and down doesn't
	// tip the torso over. The head is unaffected either way — it writes its own full aim in world space, so it
	// keeps pitching around the neck on top of whatever this does.
	//
	// The height deliberately IGNORES THE DUCK: it rides _standZ, the eye height with the crouch taken out (see
	// SmoothedEyePosition). Crouching drops the eye, and the body — which simply rides this pivot — would sink
	// with it, but the body used to hang off the pawn root, which doesn't move vertically when you duck, so it
	// never did. The head still drops, because it's placed from the real ducked eye, and the torso answers by
	// deforming instead (UpdateBodyDeform). The gun arm is left alone on purpose — it's eye-relative so that
	// crouching carries it down (see HunterGun.ShoulderOffset).
	void PlaceVisualPivot( Vector3 eye, Angles visualAim )
	{
		if ( !VisualPivot.IsValid() || !_controller.IsValid() )
			return;

		// Minus the unspent mid-air lift, so a jump-crouch eases the body up into the tuck instead of teleporting
		// it (see AirTuckOffset). Zero on the ground, where _standZ alone is the answer. The jump follow-through
		// deliberately does NOT live here — it's applied per-brush so the chest and belly can take different
		// amounts of it and stretch apart (see UpdateBodyDeform).
		VisualPivot.WorldPosition = eye.WithZ( _standZ - AirTuckOffset );
		VisualPivot.WorldRotation = Rotation.FromYaw( visualAim.yaw );
	}

	// Build the pivot and move the head + body under it. Runs on every machine (each spawns its own pawn), and
	// is idempotent — a child already under the pivot is left alone, so a proxy that received the reparented
	// hierarchy in its spawn snapshot doesn't shuffle it again.
	void EnsureVisualPivot()
	{
		VisualPivot ??= GameObject.Children.FirstOrDefault( c => c.Name == "Visuals" )
			?? new GameObject( true, "Visuals" );

		if ( VisualPivot.Parent != GameObject )
			VisualPivot.Parent = GameObject;

		// The head is wired through Eyes (it's also the Face sculpture). The body may sit under the pawn root or
		// already under the pivot, depending on how the prefab is authored — look in both places.
		_bodyObject ??= VisualPivot.Children.FirstOrDefault( c => c.Name == "Body" )
			?? GameObject.Children.FirstOrDefault( c => c.Name == "Body" );

		// Capture the AUTHORED world poses before touching anything. The pivot's authored position is cosmetic —
		// PlaceVisualPivot parks it at the eye every frame — so seeding it below drags anything ALREADY parented
		// under it (the prefab-authored case) away from where it was authored. Restoring these afterwards keeps
		// both cases identical: whatever pose the prefab shows is the pose you get.
		var headWorld = Eyes.IsValid() ? Eyes.WorldTransform : global::Transform.Zero;
		var bodyWorld = _bodyObject.IsValid() ? _bodyObject.WorldTransform : global::Transform.Zero;

		// Seed the pivot at the pose it will actually hold, so the offsets measured below are the RUNTIME ones —
		// seed it anywhere else (the origin, the pawn's feet, wherever the prefab parks it) and the body would
		// sit at that wrong offset forever, since nothing writes the body's transform after this. BodyHeight, not
		// CurrentHeight: the pivot holds the STANDING height whether or not we happen to spawn ducked.
		//
		// Rotation stays identity here even though PlaceVisualPivot applies yaw — that's what makes the body's
		// measured local rotation come out as its AUTHORED one, so at runtime it ends up facing the look yaw
		// (plus any offset the prefab gave it) rather than being permanently skewed by the yaw we spawned at.
		if ( _controller.IsValid() )
		{
			VisualPivot.WorldPosition = _controller.WorldPosition
				+ Vector3.Up * (_controller.BodyHeight - _controller.EyeDistanceFromTop);
			VisualPivot.WorldRotation = Rotation.Identity;
		}

		Adopt( Eyes, headWorld );
		Adopt( _bodyObject, bodyWorld );

		// Parent under the pivot if it isn't already, then restore the authored world pose EITHER WAY, so the
		// local offset comes out relative to the pivot's runtime pose rather than its authored one.
		void Adopt( GameObject go, global::Transform world )
		{
			if ( !go.IsValid() || go == VisualPivot )
				return;

			if ( go.Parent != VisualPivot )
				go.Parent = VisualPivot;

			go.WorldTransform = world;
		}
	}

	// Place everything that hangs off the eye — head, body and gun — from ONE eye, on every machine (the eye
	// transform is networked, so remote hunters' heads and guns track their aim too).
	void ComposePawn( Vector3 eye )
	{
		// The angles the pawn's VISUALS point along this frame. In first person — and on every proxy — that IS the
		// eye angles; in third person it's the eased, near-faded convergence, so your own gun and head track what
		// you're aiming at rather than running parallel to it, offset by the boom's shoulder/rise. Roll-free via
		// EulerAngles, matching the eye-angles convention. Edit mode never reaches ResolveAim, so it just holds
		// whatever was last resolved — and the eye angles are frozen there too, so nothing drifts apart.
		//
		// Deliberately LOCAL: other machines keep rendering this hunter along its networked EyeAngles, since the
		// convergence depends on OUR boom, which they can't know. Nothing gameplay-facing rides on it — shots at
		// this hunter are registered on the SHOOTER's machine against their own proxy copy of it, whose head
		// collider is placed from those same raw eye angles.
		var visualAim = !_controller.IsValid() ? default
			: _visualAimDir.LengthSquared > 0.5f ? _visualAimDir.EulerAngles
			: _controller.EyeAngles;

		// The pivot the head and body hang off — placed BEFORE them, since they ride it. The body needs nothing
		// more than this: its facing and height both come from the pivot.
		PlaceVisualPivot( eye, visualAim );

		// Park the head at the eye, aimed where we're looking. MUST be placed from the same smoothed eye as the
		// camera: positioning it from the controller's cached EyePosition (stamped raw during fixed update) made
		// the head step at the physics tick rate against the now-gliding camera. The landing dip is already baked
		// into that eye, so head and camera ride it together; the LAUNCH trail is added here instead, which is
		// what lets you watch the head lag behind on takeoff without the view doing the same.
		// Walk bob, taken by the head and the arm but never by the eye. The arm's sway is aim-space, converted
		// yaw-only so looking up and down can't tip it out of the horizontal — matching how ShoulderOffset is
		// applied. The head's is straight up/down and needs no conversion at all.
		var armBob = Rotation.FromYaw( visualAim.yaw ) * _armBob;
		var headBob = Vector3.Up * _headBob;

		if ( Eyes.IsValid() && _controller.IsValid() )
		{
			// Parked at the NECK, not the eye: the head sculpture's origin is its neck pivot (brushes authored
			// +NeckDrop above it), so dropping the object here keeps the head visual where it always was while
			// pitch swings it around the neck. World-space drop, not eye-relative — the neck is the fixed point.
			Eyes.WorldPosition = eye + Vector3.Up * (HeadOnlyDip - NeckDrop) + headBob;
			Eyes.WorldRotation = visualAim.ToRotation();
		}

		// Gun display, from the SAME smoothed eye (and after DriveCamera, so the viewmodel can never lag the
		// camera by a frame). Runs on every machine — proxies swing the arm/world model from the networked eye
		// transform; firstPerson gates the viewmodel to the owning machine outside edit mode AND third person
		// (there you see your own world-model gun on the arm, exactly what proxies see).
		// The arm hangs off the CHEST, not the head. ShoulderOffset is eye-relative, so it would otherwise drop
		// the full duck with the eye while the chest only follows DuckChestFollow of it — lifting the arm back by
		// the difference keeps the gun planted on the shoulder it's supposed to be attached to. Exactly zero at
		// DuckChestFollow = 1 (arm and chest drop together) and while standing. The viewmodel stays on the real
		// eye, since in first person there's no body for it to disagree with.
		// The arm needs no landing term — the eye already carries that — but it does take the launch trail and the
		// walk bob, so the gun rides the body the view has left behind rather than snapping to the camera.
		if ( _gun.IsValid() && _controller.IsValid() )
		{
			float armLift = (_controller.BodyHeight - _controller.DuckedHeight)
				* _duckVisual * (1f - DuckChestFollow)
				+ HeadOnlyDip;

			_gun.Place( eye, visualAim, !IsProxy && !EditMode && !GameSettings.HunterThirdPerson,
				Vector3.Up * armLift + armBob );
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
	// Advances _eyez and _standZ — call exactly once per frame (OnUpdate does), and use the results everywhere.
	Vector3 SmoothedEyePosition()
	{
		// Height comes from the duck SPRING, not the instantaneous CurrentHeight, so crouching eases down and
		// settles with a little overshoot instead of stepping. UpdateDuckSpring snaps it while airborne, which
		// keeps the engine's mid-air duck exact — that one raises the root by the same delta to hold the head
		// still, so a height that eased there instead would drift the view.
		float height = _controller.BodyHeight
			- (_controller.BodyHeight - _controller.DuckedHeight) * _duckSpring;

		var eye = _controller.WorldPosition
			+ Vector3.Up * (height - _controller.EyeDistanceFromTop);

		if ( !_controller.IsAirborne && _eyez != 0f )
			eye.z = _eyez.LerpTo( eye.z, Time.Delta * 50f );
		_eyez = eye.z;

		// The landing dip rides on top — the camera takes it just like it takes the duck, and so does everything
		// else placed from the eye. The LAUNCH trail is not in here; the head and arm add that themselves, so the
		// view stays snappy off the ground while the head visibly lags behind it. Added AFTER the step smoothing
		// and deliberately NOT stored in _eyez: that field is the stair-glide state, and feeding a spring through
		// it would both damp the impact and leave it filtering its own output.
		eye.z += EyeDip;

		// The same eye WITHOUT the duck — the height the visual pivot rides, so the body holds its standing
		// height while the head dips (see PlaceVisualPivot). Smoothed identically so steps glide for it too.
		// Tracked separately rather than added back as a correction: any mismatch between the two would show up
		// as the body bobbing against the head, and a spring makes that far easier to get wrong than a lerp did.
		float stand = _controller.WorldPosition.z
			+ (_controller.BodyHeight - _controller.EyeDistanceFromTop);

		if ( !_controller.IsAirborne && _standZ != 0f )
			stand = _standZ.LerpTo( stand, Time.Delta * 50f );
		_standZ = stand;

		return eye;
	}

	// Advance the duck spring — ONE value, driving the camera/head drop, the torso squash and (by its absence)
	// the pivot's standing height, so nothing can drift apart. A plain lerp reads mechanical: it decelerates into
	// the target and stops dead. This is a damped harmonic spring, so crouching drops with weight and settles
	// with a small overshoot, and standing up carries a little rebound past neutral — which the squash turns into
	// a brief stretch, since a negative spring value inverts its flatten/spread terms.
	//
	// Semi-implicit Euler with a clamped timestep: stable at these rates even through a frame spike, and the
	// value is clamped so a pathological stiffness can't fling the torso inside out. Snapped instantly while
	// airborne, matching how the eye smoothing has always treated jumps — the engine's mid-air duck moves the
	// root to compensate, and springing against that would fight it.
	void UpdateDuckSpring()
	{
		if ( !_controller.IsValid() )
			return;

		bool ducking = _controller.IsDucking;
		bool airborne = _controller.IsAirborne;
		float target = ducking ? 1f : 0f;
		float delta = _controller.BodyHeight - _controller.DuckedHeight;
		float dt = MathF.Min( Time.Delta, 0.05f );

		// Crouching MID-AIR is a different move: the engine raises the whole pawn by the duck delta at the same
		// instant the height shrinks, so your head holds still and your feet tuck up (PlayerController.UpdateDucking).
		// Catch that transition and bank the lift, so the pivot can cancel it and then ease it back in — that's
		// what turns an instant teleport into a visible tuck. Un-ducking mid-air is refused by the engine, so
		// there is no reverse case to handle.
		if ( ducking && !_wasDucking && airborne )
			_airLift = delta;
		_wasDucking = ducking;

		// Back on the ground the tuck no longer means anything — the body holds standing height and the chest
		// drops with the head instead. Fade the banked lift rather than dropping it, so landing mid-tuck (or
		// standing up straight after) transitions instead of popping.
		if ( !airborne )
			_airLift = _airLift.LerpTo( 0f, dt * MathF.Max( DuckSpringRate, 0.01f ) );

		// The EYE's spring has to snap while airborne. The root has already jumped up by the full delta, so a
		// height that eased down instead of matching it instantly would pop the camera up and glide it back.
		if ( airborne )
		{
			_duckSpring = target;
			_duckVelocity = 0f;
		}
		else
		{
			Spring( ref _duckSpring, ref _duckVelocity, target, dt );
		}

		// The VISUAL spring always runs, airborne included — it drives the torso squash, the chest drop and (via
		// _airLift) the body's rise into the tuck. Grounded it integrates identically to the eye's spring, so the
		// head and chest stay welded; only in the air do the two deliberately diverge.
		Spring( ref _duckVisual, ref _duckVisualVelocity, target, dt );

		void Spring( ref float value, ref float velocity, float to, float step )
		{
			float w = MathF.Max( DuckSpringRate, 0.01f );
			velocity += (w * w * (to - value) - 2f * DuckSpringDamping * w * velocity) * step;
			value = (value + velocity * step).Clamp( -0.5f, 1.5f );
		}
	}

	// How much to pull the visual pivot back DOWN from where the pawn root now sits — the unspent part of the
	// engine's mid-air lift. It starts at the full delta (cancelling the instant jump, so the body doesn't
	// teleport) and reaches zero as the visual spring completes, letting the body rise into the tuck.
	//
	// Deliberately complementary to the chest's drop, which is the same delta times the same spring: as the pivot
	// rises by the lift, the chest sinks within it by exactly as much, so the chest stays welded to the head
	// through the whole tuck while the body visibly pulls up into it. Zero on the ground.
	float AirTuckOffset => _airLift * (1f - _duckVisual);

	// ── Where each part sits, vertically, relative to rest ───────────────────────────────────────────────────
	//
	// Three things move: the BELLY (bottom brush), the CHEST (top brush) and the HEAD (which carries the gun arm,
	// and the camera with it). The two events driving them work differently on purpose:
	//
	//   LAUNCH  — one motion propagating down a body being yanked upward, so the chest and head take SHARES of
	//             the belly's trail. Tuning the launch is then a single distance plus how far up it reaches.
	//   LANDING — the floor stops each part on its own terms, so each gets its own DISTANCE. That independence is
	//             the point: any one can be zeroed without disturbing the others.
	//
	// The camera takes the landing only. Everything else about which part reads which value lives here — the
	// placement code just asks for the offset it needs.
	float BellyOffset => _jumpLag + _landLag;
	float ChestOffset => _jumpLag * JumpChestFollow + _landChestLag;

	// The eye's share — camera, shot origin, and anything else placed from the eye. LANDING ONLY: an impact dip
	// sells the weight, but dipping the view on the launch fights the leap and makes a jump feel sluggish.
	float EyeDip => _landHeadDip;

	// What the head and gun arm take ON TOP of the eye: the launch trail the camera sits out. Applied at their own
	// placement sites, the same way the duck's arm lift is, so the visuals follow the body through the whole arc
	// while the view only answers the landing.
	float HeadOnlyDip => _jumpLag * JumpChestFollow * JumpHeadFollow;

	// Walk bob for the HEAD and the gun arm — never for the eye, so the camera stays dead still while your body
	// bounces along under it. The viewmodel is placed from the eye and runs its own bob (HunterGun.UpdateBob), so
	// it neither loses that nor double-counts this one.
	//
	// The ARM takes the full bob, sway included — a gun swinging across as you walk is part of the look, and it's
	// the same motion the viewmodel makes. The HEAD takes the vertical component ONLY: side to side, it read as
	// the head wandering off the spine rather than as a body walking.
	//
	// The cadence matches the viewmodel's: the phase advances π per stride, and the stride is the same
	// speed-blended StepDistance→RunStepDistance the footstep sounds use, so head, gun and audio all tick one
	// clock. The doubled phase is what puts one bounce on EVERY footfall — at plain phase it would dip once per
	// pair of steps, which reads as a limp. The clock lives HERE rather than in HunterGun because the gun's only
	// advances on the owner's first-person frames — the head has to bob in third person and on every proxy.
	//
	// Amplitude blends in and out with speed and drops to nothing airborne: a jump glides, it doesn't drum.
	void UpdateHeadBob()
	{
		if ( !_controller.IsValid() )
			return;

		float dt = MathF.Min( Time.Delta, 0.05f );
		float speed = _controller.Velocity.WithZ( 0f ).Length;
		float run = MathF.Max( _controller.RunSpeed, 1f );

		float target = _controller.IsOnGround ? MathF.Min( speed / run, 1f ) : 0f;
		_bobBlend = _bobBlend.LerpTo( target, 1f - MathF.Exp( -8f * dt ) );

		float stride = MathF.Max( speed.Remap( _controller.WalkSpeed, run, StepDistance, RunStepDistance ), 1f );
		_bobPhase += MathF.PI * (speed * dt / stride) * HeadBobFrequency;

		// The viewmodel's own 0.6 / 0.45 split, kept so the arm matches the gun in hand and the head's vertical
		// is unchanged by having the sway filtered out of it.
		float scale = HeadBobAmount * _bobBlend;

		// The hand runs on the same clock, offset in TIME. The phase advances π per step, so adding
		// HandBobOffset·π shifts the hand by that fraction of a footstep — and because the shift is applied to
		// the phase itself, the sway and the bounce both move by the same amount of time even though they run at
		// different rates. Offsetting the two components separately would have skewed the hand's own motion
		// against itself instead of delaying it.
		float handPhase = _bobPhase + HandBobOffset * MathF.PI;

		// Fore-aft swing on COS while the sway runs on sin — a quarter cycle apart, so the hand traces a flat
		// ellipse through the horizontal instead of shearing back and forth along one diagonal line. Both run at
		// step rate (one full push-pull per pair of steps), which is the real gait: an arm goes forward with the
		// opposite leg and back with its own, so the cycle is two footfalls, not one.
		_armBob = new Vector3(
			MathF.Cos( handPhase ) * 0.6f * HandBobForward,
			MathF.Sin( handPhase ) * 0.6f,
			MathF.Sin( handPhase * 2f ) * 0.45f ) * scale;

		_headBob = MathF.Sin( _bobPhase * 2f ) * 0.45f * scale;
	}

	// Vertical follow-through: the body has weight, so it trails when you launch and compresses when you land,
	// then springs back onto the pawn. Only the BODY — the head is written straight to the eye and the camera is
	// the eye, so neither can ever wobble; a jump that bounced the view would be nauseating rather than juicy.
	//
	// Driven off IsAirborne transitions read locally, so it runs identically on every machine (proxies categorize
	// ground too) without a byte of networking — the engine's OnJumped/OnLanded events are owner-only and would
	// have left remote hunters stiff. Never gate this kind of transition on same-frame INTERPOLATED motion: on
	// the frame the flag flips, the interpolated transform hasn't moved yet. PlayerController.Velocity is the
	// tick-fresh body velocity, which is the whole point of reading it rather than differencing positions.
	void UpdateJumpSpring()
	{
		if ( !_controller.IsValid() )
			return;

		bool airborne = _controller.IsAirborne;
		float vz = _controller.Velocity.z;
		float jumpSpeed = MathF.Max( _controller.JumpSpeed, 1f );
		float w = MathF.Max( JumpSpringRate, 0.01f );

		// Launch and landing ride SEPARATE springs, identical in stiffness and damping. Not for the physics — one
		// spring would sum them fine — but so the chest can take a share of the launch while sitting the landing
		// out completely. Once both impulses are in the same value there is no way to tell afterwards how much of
		// it came from which, so the split has to happen here.
		//
		// An impulse of distance × w peaks at roughly `distance` world units, so the properties above read as the
		// actual travel rather than as an opaque force.
		if ( airborne && !_wasAirborne )
		{
			// Left the ground. Scaled by how hard we pushed off, so walking off a ledge doesn't kick at all.
			_jumpLagVelocity -= JumpLagDistance * w * (MathF.Max( vz, 0f ) / jumpSpeed);
			_fallSpeed = 0f;
		}
		else if ( !airborne && _wasAirborne )
		{
			// Landed. Uses the fastest descent we saw while falling, NOT the velocity now — the collision has
			// already killed that by the frame we register as grounded, which would read every landing as soft.
			float impact = MathF.Min( _fallSpeed / jumpSpeed, 2f );

			// The CHEST alone gets scaled back by how CROUCHED we are. It's the one part the duck has already
			// driven down — by DuckChestFollow of the full duck delta — so a landing dip stacks on top of that and
			// puts it through the floor. A body folded that far down has genuinely spent its compression travel.
			// Nothing else wants this: the belly rides the pivot at standing height whatever the crouch is doing,
			// and the head has the whole eye height of clearance under it, so both dip at full strength crouched
			// or not. (Scaling everything by the crouch was the first cut, and it silently killed the head dip on
			// any landing you were holding crouch through.)
			float crouch = _duckVisual.Clamp( 0f, 1f );
			float chestImpact = impact * (1f - crouch);

			// The head keeps its dip when crouched, but eased off — a folded body has taken some of the impact
			// already, so a view answering it as hard as a standing landing reads as too much. See
			// LandCrouchHeadAmount; at 1 this is a no-op and crouched landings hit the view exactly as standing
			// ones do.
			float headImpact = impact * MathX.Lerp( 1f, LandCrouchHeadAmount, crouch );

			// The DIP is a velocity kick: the body carries its momentum through the impact and swings past. Belly,
			// chest and head each get their OWN distance — no part is a share of another, so any one of them can be
			// zeroed without disturbing the rest.
			_landLagVelocity -= LandDipDistance * w * impact;
			_landChestLagVelocity -= LandChestDipDistance * w * chestImpact;
			_landHeadDipVelocity -= LandHeadDipDistance * w * headImpact;

			// The SQUASH is set directly instead, because an impact squash is hardest at the moment of contact
			// and recovers from there — a velocity kick would ramp it in and peak it partway through the bounce,
			// which is the opposite shape. Starting at the value also means it actually REACHES it: a kick only
			// peaks at ~64% of nominal at this damping, so LandSquash was quietly worth two thirds of its number.
			_landSquash = impact;
			_landSquashVelocity = 0f;

			_fallSpeed = 0f;
		}

		if ( airborne )
			_fallSpeed = MathF.Max( _fallSpeed, MathF.Max( -vz, 0f ) );

		_wasAirborne = airborne;

		float dt = MathF.Min( Time.Delta, 0.05f );

		// The two POSITION springs share the jump settings. The SQUASH gets its own, slower pair, because the
		// two start differently and would otherwise read as different speeds at identical settings: a velocity
		// kick has to travel out to its peak and back, so the dip's excursion takes time, while the squash starts
		// AT its peak and only decays — at the dip's rate it vanishes almost as fast as it appears. Slower and
		// looser here gives the clay some wobble on the way back instead of snapping flat.
		Settle( ref _jumpLag, ref _jumpLagVelocity, w, JumpSpringDamping );
		Settle( ref _landLag, ref _landLagVelocity, w, JumpSpringDamping );
		Settle( ref _landChestLag, ref _landChestLagVelocity, w, JumpSpringDamping );
		Settle( ref _landHeadDip, ref _landHeadDipVelocity, w, JumpSpringDamping );
		Settle( ref _landSquash, ref _landSquashVelocity, MathF.Max( LandSquashRate, 0.01f ), LandSquashDamping );

		void Settle( ref float value, ref float velocity, float rate, float damping )
		{
			velocity += (rate * rate * (0f - value) - 2f * damping * rate * velocity) * dt;
			value = (value + velocity * dt).Clamp( -64f, 64f );
		}
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
	// person only moves the viewpoint, never the aim, so shots keep leaving from the eye — converged onto the
	// crosshair, which off the boom is no longer the eye forward (see ResolveAim).
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

		// First person can opt out of the landing dip: the body animation still plays in full, the view just
		// doesn't ride it. Third person always takes it — there you're watching the character land, and a view
		// that ignored an impact it can plainly see would read as disconnected. Removed here rather than left out
		// of the eye entirely, because the eye also places the head and the gun on EVERY machine, and a proxy's
		// visuals must not depend on whichever camera mode the local player happens to be in.
		if ( !GameSettings.HunterThirdPerson && !FirstPersonLandDip )
			eye.z -= EyeDip;

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

	// Deform the torso — the crouch squash and the jump/land stretch, both written into the same brushes.
	//
	// DUCK: the pivot deliberately holds the standing height while the head drops from the real ducked eye (see
	// PlaceVisualPivot), which alone would leave the head sinking into a body still standing at full height — so
	// the sculpture deforms instead: the chest rides down with the head and everything below it flattens and
	// spreads to take up the difference.
	//
	// JUMP: the chest takes a small fraction of the follow-through and the belly takes all of it, so the two pull
	// apart on launch and compress together on landing. Doing it per-brush rather than on the pivot is what makes
	// it a stretch instead of the whole character sliding down rigidly.
	//
	// The brushes are mutated DIRECTLY and Rebuild() is deliberately never called — exactly how SdfShrinkSystem
	// animates healing craters. The renderer notices through its brush hash and re-dispatches the field, while
	// Committed stays silent, so nothing republishes over the network or rebuilds a collider. Every machine runs
	// this from its own read of the synced IsDucking, so proxies squash in step without a byte being sent.
	void UpdateBodyDeform()
	{
		if ( !_controller.IsValid() || !_bodySculpt.IsValid() )
			return;

		var brushes = _bodySculpt.Brushes;
		if ( brushes is not { Count: > 0 } )
			return;

		// Authored brushes only, captured once. Carve craters (Damage) are skipped on purpose: they appear and
		// heal away at runtime, and measuring a rest pose against a list whose size changes would let the squash
		// compound on itself. A crater therefore stays put on the surface it was shot into while we deform —
		// they're transient and shallow, so it reads fine.
		_bodyRest ??= brushes.Where( b => !b.Damage )
			.Select( b => (Brush: b, Pos: b.Position, Size: b.Size) )
			.ToArray();

		if ( _bodyRest.Length == 0 )
			return;

		// Driven by the shared VISUAL spring, so the torso deforms in exact lockstep with the chest rather than
		// trailing it — and inherits its overshoot for free: past 1 the squash goes slightly deeper than the pose
		// calls for, and the negative rebound on standing up inverts the terms into a brief stretch. This is the
		// spring that keeps running in mid-air, so a jump-crouch squashes as it tucks.
		float duck = _duckVisual;

		// The jump/land follow-through, applied to the BRUSHES rather than the pivot (see UpdateJumpSpring for the
		// springs), so the chest and the belly can take different amounts of it — the same split DuckChestFollow
		// gives the crouch. The difference is the whole effect: the belly trails, the chest barely does, and the
		// blend between them necks out so the body reads as STRETCHING off the launch and compressing into the
		// landing. Moving the pivot instead just slid the character down as one rigid piece.
		//
		// The belly takes both impulses in full; the chest takes its own share of each, and by default none at
		// all of the landing — so an impact squashes the belly up into a chest that stays put.
		float lag = BellyOffset;
		float chestLag = ChestOffset;
		float landAmount = _landSquash;

		// Settled: nothing to do, but only after one final pass has written the authored values back EXACTLY. A
		// spring never lands precisely on zero, and we must not leave the torso a hair squashed or a hair sunk.
		// BOTH springs have to be idle — the duck can be at rest while a landing is still ringing out.
		bool duckIdle = MathF.Abs( duck ) < 0.001f && MathF.Abs( _duckVisualVelocity ) < 0.001f;
		bool lagIdle = MathF.Abs( _jumpLag ) < 0.01f && MathF.Abs( _jumpLagVelocity ) < 0.01f
			&& MathF.Abs( _landLag ) < 0.01f && MathF.Abs( _landLagVelocity ) < 0.01f
			&& MathF.Abs( _landChestLag ) < 0.01f && MathF.Abs( _landChestLagVelocity ) < 0.01f
			&& MathF.Abs( _landHeadDip ) < 0.01f && MathF.Abs( _landHeadDipVelocity ) < 0.01f
			&& MathF.Abs( _landSquash ) < 0.001f && MathF.Abs( _landSquashVelocity ) < 0.001f;

		if ( duckIdle && lagIdle )
		{
			if ( !_deformApplied )
				return;

			duck = 0f;
			lag = 0f;
			chestLag = 0f;
			landAmount = 0f;
			_deformApplied = false;
		}
		else
		{
			_deformApplied = true;
		}

		// The chest is simply the highest authored brush; everything under it is torso to flatten.
		int chest = 0;
		for ( int i = 1; i < _bodyRest.Length; i++ )
		{
			if ( _bodyRest[i].Pos.z > _bodyRest[chest].Pos.z )
				chest = i;
		}

		// Brush positions are sculpture-LOCAL, but the pivot only ever yaws, so local up is still world up and
		// the drop needs no conversion. (It would if the body object were ever pitched or non-uniformly scaled.)
		// The chest's drop. In the air this is what cancels the pivot's rise (AirTuckOffset is the same delta
		// times the same spring, inverted), keeping the chest welded to the head while the body pulls up.
		float drop = (_controller.BodyHeight - _controller.DuckedHeight) * DuckChestFollow * duck;
		float flatten = MathF.Max( 1f - DuckSquash * duck, 0.05f );
		float spread = MathF.Max( 1f + DuckBulge * duck, 0.05f );

		// The landing's own squash, BELLY ONLY — the impact travels up through the soft part while the chest
		// keeps its shape. Read straight off its own spring rather than derived from the dip, so LandSquash means
		// exactly what it says at a full-jump landing and is independent of however the dip is tuned. The rebound
		// carries it past zero, which inverts the terms into a brief stretch as the body recovers.
		float landFlatten = MathF.Max( 1f - LandSquash * landAmount, 0.05f );
		float landSpread = MathF.Max( 1f + LandBulge * landAmount, 0.05f );

		for ( int i = 0; i < _bodyRest.Length; i++ )
		{
			var (brush, restPos, restSize) = _bodyRest[i];
			if ( brush is null )
				continue;

			if ( i == chest )
			{
				// The chest rides down with the head on a duck, keeping its shape — it reads as the shoulders
				// dropping rather than the chest itself deflating. On a jump it takes only its share of the
				// follow-through, so it stays near the head while the belly falls away from it.
				brush.Position = restPos + Vector3.Up * (chestLag - drop);
				brush.Size = restSize;
				continue;
			}

			// The belly takes the follow-through in full. The gap this opens against the chest is the stretch.
			// Duck and landing squash compose multiplicatively, so crouching as you land compounds rather than
			// one overwriting the other.
			brush.Position = restPos + Vector3.Up * lag;
			brush.Size = new Vector3(
				restSize.x * spread * landSpread,
				restSize.y * spread * landSpread,
				restSize.z * flatten * landFlatten );
		}
	}

	// The head's hit collider — PROXIES ONLY, and the reason for that is the jitter that took this whole hunt.
	//
	// SdfCollider builds a sibling ModelCollider (a trigger — it blocks nothing; the gun's rays reach it via
	// HitTriggers) purely so bullets can hit a face. But a Collider binds to its nearest ancestor Rigidbody,
	// which is the pawn root — so that mesh shape is a shape ON our physics body, and since the head is
	// re-placed every frame we were moving a shape on a live, simulating body every frame while the movement
	// controller's Reground/TryStep traced against it. The result was a pawn that jittered while moving, which
	// is why deleting the head cured the BODY: it was never a rendering problem at all.
	//
	// We don't need it on our own pawn. Shots at this hunter are registered on the SHOOTER's machine against
	// THEIR proxy copy of us (see the visualAim note in ComposePawn), our own shot trace ignores our own
	// hierarchy, and so does the camera boom. Proxies keep it and are unaffected: a proxy's rigidbody doesn't
	// simulate, it's driven straight from the networked transform, so shapes moving on it perturb nothing.
	//
	// Asserted live every frame on EVERY machine rather than once at start, for two reasons: ownership resolves
	// after OnStart, and a spawn snapshot ships the owner's live component state — so a proxy can arrive with
	// this already disabled and would otherwise stay unhittable. Guarded so it's a no-op once settled.
	void UpdateHeadCollider()
	{
		if ( !_headCollider.IsValid() )
			return;

		if ( _headCollider.Enabled != IsProxy )
			_headCollider.Enabled = IsProxy;
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

		// Where the shot goes: this frame's resolved aim (_aimDir), which in third person is the converged
		// crosshair ray, NOT the eye forward — the same direction the gun and head were just pointed at, so the
		// pellet always leaves along the visible barrel. The scatter cone is rebuilt around it, keeping the eye's
		// up so the disc sample stays upright (better conditioned than world up at steep pitch).
		var eyeRot = _controller.EyeAngles.ToRotation();
		var dir = _aimDir.LengthSquared > 0.5f ? _aimDir : eyeRot.Forward;
		var aimRot = Rotation.LookAt( dir, eyeRot.Up );

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

	// Resolve this frame's aim into its two forms: _aimDir, the EXACT direction a shot must travel to land under
	// the crosshair, and _visualAimDir, the eased and near-faded direction the head and gun POINT along.
	//
	// The crosshair is dead screen centre, so the ray the player is aiming is the CAMERA's centre ray. In first
	// person the camera sits exactly on the shot origin, so the eye forward already IS that ray — nothing to
	// converge. In third person the camera is out on its boom (back along the aim, over the shoulder, risen), and
	// its centre ray runs PARALLEL to the eye forward, displaced by the shoulder/rise. A shot along the eye
	// forward therefore lands a fixed ~20 units to the side of the dot at EVERY range — which subtends a bigger
	// and bigger on-screen error the closer the target is. So converge: trace the real crosshair ray from the
	// camera to find the point being pointed at, and aim from the eye at that point.
	//
	// The two forms exist because convergence is most NEEDED and most UGLY at the same place — up close, where
	// the swing angle is largest. Accuracy can't be traded away there (that's the whole bug), so the SHOT stays
	// exactly converged at all ranges and only the VISUALS relax: they fade back toward parallel as the target
	// gets near (ConvergeFade) and ease over time (AimEaseRate) so sweeping across a foreground object and the
	// far wall glides instead of snapping. Past ConvergeFade the two are the same direction, at any range.
	void ResolveAim( Vector3 eye )
	{
		var eyeRot = _controller.EyeAngles.ToRotation();
		var cam = Scene.Camera;

		// Gated so this stays safe wherever it's called from: HunterThirdPerson is a LOCAL setting, so a proxy
		// pawn on a third-person player's machine must never converge on THAT player's camera — it would swing a
		// remote hunter's head around with our own view.
		if ( IsProxy || !GameSettings.HunterThirdPerson || !cam.IsValid() )
		{
			// Nothing to converge — and critically, nothing to EASE either. Zeroing the visual aim drops the
			// head and gun back onto the raw live EyeAngles, exactly as they were before any of this existed;
			// running them through EaseVisualAim here instead put a ~10/s lag on the first-person viewmodel
			// (which then fed its own rotation spring on top) and made the gun feel like it was wading.
			// Zero also re-seeds the ease, so the next third-person frame snaps rather than swinging in — right,
			// since toggling the view teleports the camera anyway.
			_aimDir = eyeRot.Forward;
			_visualAimDir = Vector3.Zero;
			return;
		}

		// Read AFTER DriveCamera, which OnUpdate runs immediately before this from the same eye — so it's exactly
		// the pose the crosshair is about to be drawn over, boom pull-in included.
		var camPos = cam.WorldPosition;
		var camDir = cam.WorldRotation.Forward;

		// Our own hierarchy ignored — in third person the pawn is directly in front of the lens and would
		// otherwise swallow every crosshair ray. Other filters match TraceShot, so the crosshair converges on
		// exactly the set of things a shot can hit.
		var look = Scene.Trace
			.Ray( camPos, camPos + camDir * Range )
			.IgnoreGameObjectHierarchy( GameObject )
			.HitTriggers()
			.WithoutTags( "trigger", "water" )
			.Run();

		// Distance from the CAMERA — only ever used to find the point under the crosshair. Everything about how
		// far away that point is, for fading purposes, has to be measured from the eye instead (see below).
		float camDist = look.Hit ? look.Distance : Range;
		var to = camPos + camDir * camDist - eye;

		// Degenerate: the crosshair landed on top of the muzzle (something pressed against the lens). There's no
		// sane direction to converge on, so keep the eye ray rather than fire somewhere arbitrary.
		_aimDir = to.Length < 1f ? eyeRot.Forward : to.Normal;

		// Near fade: ramps 0 (target at the muzzle) → 1 (target at ConvergeFade and anything beyond it, with no
		// upper limit). Smoothstepped, whose zero derivative at both ends is what keeps it from kinking as it
		// arrives at full convergence.
		//
		// Measured from the EYE, not from the camera. In aim space the eye-to-target vector is
		// (D, -shoulder, rise) where D is the eye's distance to the target and the lateral offset stays fixed at
		// the shoulder width however close you get — so the swing is atan(shoulder / D), which is 45° at 20 units
		// and runs to 90° as you close in. That angle is governed by D alone. Fading on the CAMERA's distance
		// added the whole boom length to every reading (~100 units), so walking up to a wall reported ~105 and
		// faded as though the target were mid-range, leaving a sizeable share of a near-90° swing applied — the
		// pawn turning to face along its own shoulder as it approached anything.
		float eyeDist = to.Length;
		float w = ConvergeFade <= 0f ? 1f : ( eyeDist / ConvergeFade ).Clamp( 0f, 1f );
		w = w * w * ( 3f - 2f * w );

		_visualAimDir = EaseVisualAim( Vector3.Lerp( eyeRot.Forward, _aimDir, w ).Normal );
	}

	// Exponential ease of the visual aim toward its target — frame-rate independent, and the reason a crosshair
	// jumping between a foreground prop and the far wall swings the gun smoothly instead of snapping. Unseeded
	// (zero) snaps straight to the target, so spawning and leaving edit mode don't swing in from nowhere.
	Vector3 EaseVisualAim( Vector3 target )
	{
		if ( _visualAimDir.LengthSquared < 0.5f || AimEaseRate <= 0f )
			return target;

		var eased = Vector3.Lerp( _visualAimDir, target,
			1f - MathF.Exp( -AimEaseRate * MathF.Min( Time.Delta, 0.1f ) ) );

		// Only ever near-antipodal directions can cancel here, which the eye-pitch clamp rules out — but a
		// zero-length normal would blow up the placement, so fall back to the target.
		return eased.LengthSquared < 0.0001f ? target : eased.Normal;
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
