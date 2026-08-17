using System;

namespace Mimiclay;

/// <summary>
/// The hunter's detector gun — DISPLAY ONLY for now (the actual hitscan lives in
/// <see cref="HunterController.Shoot"/>). Two local clones of <see cref="GunPrefab"/> are spawned on every
/// machine (owner AND proxies — same pattern as the Eyes/footstep proxies use: purely cosmetic, driven by the
/// networked eye transform, so nothing here needs its own network spawn):
///
/// <list type="bullet">
/// <item>WORLD model ("GunWorld"): what everyone else sees — held on an ARM: a <see cref="Shoulder"/> pivot is
/// pinned to the body (an eye-relative offset swung by yaw only, so it stays on the pawn's right side and rides
/// crouches) and rotated by the full aim, and the gun hangs off its <see cref="Hand"/> child. Pitching the view
/// arcs the gun around the shoulder like an arm swinging, instead of the gun rigidly tracking the head. On the
/// owning machine it goes <see cref="SdfRaymarchRenderer.RenderHidden"/> in first person (shadows-only),
/// exactly like the rest of the pawn's own body.</item>
/// <item>VIEW model ("GunView"): what the owner sees — hung off the camera eye at a classic bottom-right
/// viewmodel offset, enabled only on the owning machine outside edit mode. Its SDF shadows are turned OFF
/// (a gun shadow floating at head height would read as an object in the world).</item>
/// </list>
///
/// Placement runs from <see cref="HunterController"/>'s single smoothed per-frame eye via <see cref="Place"/> —
/// never from a cached EyePosition (see the fixed-tick jitter rule) and never self-driven from OnUpdate, so the
/// gun can't lag or fight the camera by update order.
///
/// The clones are cosmetic: colliders and the through-wall outline are stripped (a gun outline under a hunter
/// pawn would glow for everyone via RoundOutlineSystem). The raymarch renderer assumes unit GameObject scale
/// (brush data is baked in world space), so the scales are applied by scaling BRUSH data, not the GameObject —
/// and each model's brushes are re-derived from a pristine prefab-scale source list whenever its scale property
/// changes, so <see cref="WorldGunScale"/> / <see cref="ViewGunScale"/> are live-tweakable while playing.
/// Editable guns later can build on the same clones.
/// </summary>
[Title( "Hunter Gun" )]
[Category( "Mimiclay" )]
[Icon( "colorize" )]
public sealed class HunterGun : Component
{
	/// <summary>Tag stamped on both gun clones so <see cref="HunterController"/>'s body-hide sweep skips them —
	/// the world gun must not be re-shown in edit mode by that sweep's rules, and the view gun must never be
	/// forced shadows-only (it manages its own visibility here).</summary>
	public const string CloneTag = "hunter_gun";

	/// <summary>The gun prefab both models are cloned from (SdfSculpture + raymarch renderer authored there).</summary>
	[Property] public PrefabFile GunPrefab { get; set; }

	/// <summary>World-model scale, applied to its brush data (the sculpt was authored at edit size, ~80 units —
	/// 2 metres; 0.5 is rifle size against the 72-unit pawn). Live: changing it rebuilds the gun on the spot.</summary>
	[Property, Range( 0.1f, 1f ), Group( "Scale" )] public float WorldGunScale { get; set; } = 0.5f;

	/// <summary>Viewmodel scale, independent of the world model's — a viewmodel usually reads better a touch
	/// smaller than the "real" gun. Live-tweakable like <see cref="WorldGunScale"/>.</summary>
	[Property, Range( 0.1f, 1f ), Group( "Scale" )] public float ViewGunScale { get; set; } = 0.5f;

	/// <summary>Field-cache resolution for the world model (voxels along the padded longest axis — overrides
	/// what the gun prefab authored). Matters because scaling the gun down does NOT shrink the field's fixed
	/// 16-unit BlendPad, so a small gun keeps fewer of these voxels for itself; raise this to claw detail back.
	/// Live-tweakable (the renderer re-bakes on change).</summary>
	[Property, Range( 8, 512 ), Group( "Scale" )] public int WorldFieldResolution { get; set; } = 256;

	/// <summary>Field-cache resolution for the viewmodel — it sits inches from the camera, so it earns the top
	/// end. 512 needs the sparse field (forced on when exceeded; dense caps at 256).</summary>
	[Property, Range( 8, 512 ), Group( "Scale" )] public int ViewFieldResolution { get; set; } = 512;

	/// <summary>The arm's pivot — the world gun rotates around THIS point with the aim. Its position is driven
	/// every frame (eye + yaw · <see cref="ShoulderOffset"/>, then the full aim rotation), so where it sits in
	/// the prefab is cosmetic; wire it so the <see cref="Hand"/> under it is authorable. Auto-resolved by child
	/// name ("Shoulder"), created if missing.</summary>
	[Property, Group( "Arm" )] public GameObject Shoulder { get; set; }

	/// <summary>The grip point, a child of <see cref="Shoulder"/> — the world gun is parented here, so its
	/// LOCAL position (how far in front of the shoulder the hand holds the gun) sets where the gun sits.
	/// Auto-resolved by child name ("Hand"), created if missing.</summary>
	[Property, Group( "Arm" )] public GameObject Hand { get; set; }

	/// <summary>Where the shoulder pivot sits relative to the EYE, in yaw space (x forward, y left, z up) — yaw
	/// only, never pitch, so looking down doesn't ride the shoulder up the body. Eye-relative so crouching
	/// carries the arm down automatically.</summary>
	[Property, Group( "Arm" )] public Vector3 ShoulderOffset { get; set; } = new( 0f, -9f, -13f );

	/// <summary>Extra offset of the gun from the <see cref="Hand"/> point, in hand space (x forward, y left,
	/// z up) — tune where the grip sits in the hand without moving the hand (and the arm arc) itself.</summary>
	[Property, Group( "Arm" )] public Vector3 HandOffset { get; set; } = Vector3.Zero;

	/// <summary>Viewmodel offset from the camera eye, in aim space (x forward, y left, z up). Rendered under
	/// <see cref="ViewmodelFov"/>'s own projection, so where this puts the gun on screen is independent of
	/// the camera's FOV — tune it once, at any camera FOV, and it stays put.</summary>
	[Property, Group( "Placement" )] public Vector3 ViewOffset { get; set; } = new( 15f, -8f, -12f );

	/// <summary>The viewmodel's OWN field of view — a true second projection for the gun's rays (the SDF
	/// raymarcher regenerates them per pixel), fully independent of the camera FOV: changing either never
	/// moves the other. Low values (20–40) give the classic flat, telephoto gun look with minimal
	/// wide-angle distortion; higher pushes toward the camera's natural perspective.</summary>
	[Property, Group( "Placement" ), Range( 10f, 120f )] public float ViewmodelFov { get; set; } = 54f;

	/// <summary>Which render path the viewmodel draws through. OverlayFlag is the production path: the
	/// engine's overlay depth prepass stencil-claims the gun's pixels at REAL depth (no wall clipping,
	/// UI/contact-shadows/AO all see honest depth). The rest are live-tweakable diagnostics — Normal =
	/// game pass (clips into walls), Viewmodel = native layer (renders invisible, dead end), OverlayNoDepth
	/// = draw-over with no overlay prepass.</summary>
	[Property, Group( "Placement" )] public SdfViewLayer ViewLayerMode { get; set; } = SdfViewLayer.Normal;

	/// <summary>Mounting rotation of the gun on its parent (the hand for the world model, the aim for the view
	/// model). The sculpt's barrel runs along its local +y, so the default -90 yaw points it forward.</summary>
	[Property, Group( "Placement" )] public Angles RotationOffset { get; set; } = new( 0f, -90f, 0f );

	/// <summary>Strength of the walk/run bob, in world units of gun travel at full run speed (rendered in
	/// the gun's own FOV space, so it reads the same at any camera FOV). 0 = off.</summary>
	[Property, Group( "View Motion" ), Range( 0f, 3f )] public float BobAmount { get; set; } = 1f;

	/// <summary>Bob cadence multiplier. 1 = EXACTLY one vertical bob per footstep at any speed — the phase
	/// advances π per stride using the same speed→stride blend the footstep sounds use (HunterController's
	/// StepDistance→RunStepDistance), so the dip and the step sound share a clock. The lateral sway runs at
	/// half rate, alternating sides with alternating feet. 2 = double-time, 0.5 = half-time.</summary>
	[Property, Group( "View Motion" ), Range( 0.25f, 3f )] public float BobFrequency { get; set; } = 1f;

	/// <summary>Hooke stiffness k (per s², mass folded to 1) of the RECOIL POSITION spring. Unlike the
	/// rotation spring this one lives in AIM space and rests at zero — mouse look and movement never
	/// displace it (no position lag), only <see cref="KickBack"/> impulses do; k/c shape the kick's
	/// return ride. 0 = position recoil off, rigid attach.</summary>
	[Property, Group( "View Spring" ), Range( 0f, 2000f )] public float PositionStiffness { get; set; } = 500f;

	/// <summary>Hooke damping c (per s) of the recoil position spring. Critical damping is 2·√k (≈45 at
	/// the default k=500) — below that the kick overshoots and bounces, above it it oozes back.</summary>
	[Property, Group( "View Spring" ), Range( 0f, 100f )] public float PositionDamping { get; set; } = 40f;

	/// <summary>Hooke stiffness k of the ROTATION spring — the gun's orientation chases the aim (the
	/// loose-gimbal lag: it points where you were aiming a beat ago and catches up). Damping here is
	/// ABSOLUTE, so a steady turn holds a steady lag angle — the classic look. 0 = rigid.</summary>
	[Property, Group( "View Spring" ), Range( 0f, 2000f )] public float RotationStiffness { get; set; } = 350f;

	/// <summary>Hooke damping c of the rotation spring. Critical is 2·√k (≈37 at the default k=350);
	/// the default sits just under for a subtle overshoot.</summary>
	[Property, Group( "View Spring" ), Range( 0f, 100f )] public float RotationDamping { get; set; } = 30f;

	/// <summary>Overall strength of the RECOIL POSITION spring's visible effect: the spring simulates
	/// exactly as tuned (k/c set the character), and this scales how much of the resulting offset is
	/// rendered. 0 = rigid attach, 1 = the full simulated kick, 2 = exaggerated.</summary>
	[Property, Group( "View Spring" ), Range( 0f, 2f )] public float PositionStrength { get; set; } = 1f;

	/// <summary>Overall strength of the ROTATION spring's visible effect — same idea as
	/// <see cref="PositionStrength"/>: scales the rendered lag angle without touching the dynamics.</summary>
	[Property, Group( "View Spring" ), Range( 0f, 2f )] public float RotationStrength { get; set; } = 1f;

	/// <summary>Hard cap (world units) on the recoil spring's offset — the sanity clamp that stops an
	/// extreme tuning leaving the gun across the screen. Recoil throws larger than this get visibly
	/// flattened; raise it if the kick caps out.</summary>
	[Property, Group( "View Spring" ), Range( 1f, 100f )] public float MaxPositionLag { get; set; } = 15f;

	/// <summary>Hard cap (degrees) on the sprung gun's rotation lag. Also the recoil ceiling for
	/// <see cref="KickUp"/>. Keep it under ~90° — the spring's error math assumes small angles.</summary>
	[Property, Group( "View Spring" ), Range( 5f, 90f )] public float MaxRotationLag { get; set; } = 25f;

	/// <summary>The point the rotation spring pivots around, in the gun PREFAB's local units (auto-scaled
	/// with <see cref="ViewGunScale"/>, so it stays on the same spot of the gun at any size). Put it at the
	/// GRIP: lag then reads as wrist flex — the hand stays planted while the muzzle sweeps, instead of the
	/// gun hinging about its arbitrary sculpt origin. Zero = the model origin (wherever the sculpt author
	/// left it). ViewOffset positions this pivot point.</summary>
	[Property, Group( "View Spring" )] public Vector3 ViewRotationPivot { get; set; }

	/// <summary>Recoil: how far (world units) the viewmodel snaps BACK along the aim per shot — delivered
	/// as a velocity impulse into the position spring, so the return ride is the spring's own (k/c set the
	/// character, this sets the throw; the value is the approximate peak travel at critical damping).
	/// Does nothing while the position spring is rigid (stiffness 0).</summary>
	[Property, Group( "Recoil" ), Range( 0f, 30f )] public float KickBack { get; set; } = 3f;

	/// <summary>Degrees the viewmodel's muzzle flips UP per shot — an impulse into the rotation spring,
	/// pivoting around <see cref="ViewRotationPivot"/> (the grip), so it reads as the wrist absorbing the
	/// shot. Approximate peak angle; <see cref="MaxRotationLag"/> is the ceiling rapid fire stacks into.</summary>
	[Property, Group( "Recoil" ), Range( 0f, 45f )] public float KickUp { get; set; } = 7f;

	/// <summary>World-model recoil: units the HAND (gun and all) jolts back along the aim per shot — what
	/// OTHER players see (runs on every machine from the shot RPC). Instant hit, exponential recovery.</summary>
	[Property, Group( "Recoil" ), Range( 0f, 10f )] public float WorldKickBack { get; set; } = 3f;

	/// <summary>World-model recoil: degrees the hand flips up per shot (pivoting at the wrist — the
	/// <see cref="Hand"/> point — so the barrel sweeps up around the grip).</summary>
	[Property, Group( "Recoil" ), Range( 0f, 30f )] public float WorldKickUp { get; set; } = 10f;

	/// <summary>Recovery rate (per second) of the world-model kick — the jolt decays by ~63% every
	/// 1/rate seconds. Higher = snappier reset.</summary>
	[Property, Group( "Recoil" ), Range( 1f, 30f )] public float WorldKickRecover { get; set; } = 8f;

	/// <summary>The muzzle-flash prefab, cloned at the gun's authored "Muzzle" point on every shot — on the
	/// VIEW model where that's what's on screen (owner, first person), the WORLD model everywhere else.
	/// Local and purely cosmetic, same deal as the caught puff: every machine clones its own from the shot
	/// RPC, nothing is networked. Empty/missing prefab just loses the flash.</summary>
	[Property, Group( "Effects" )] public PrefabFile MuzzleFlashPrefab { get; set; }

	/// <summary>Size multiplier on the VIEWMODEL's flash only (the world model plays the prefab as authored)
	/// — the first-person burst often wants to read bigger than a strictly world-accurate one. Applied at
	/// spawn, per shot, so it's live-tweakable between shots.</summary>
	[Property, Group( "Effects" ), Range( 0.25f, 4f )] public float ViewFlashScale { get; set; } = 1f;

	/// <summary>Material override for the VIEW clone's raymarch (world model keeps the prefab's). Point it
	/// at plasticine_viewmodel.vmat — its F_TRANSLUCENT feature selects the shader's translucent LIGHTING
	/// variant, whose shading skips the screen-space passes (SSAO sample, contact-shadow mask) entirely.
	/// Since the prepass writes the gun's REAL depth, sampling them is legitimate again — this is now an
	/// A/B knob (skip = flatter but immune to screen-space artifacts), not a correctness requirement.
	/// Leave null to keep the shared opaque material. Live-swappable.</summary>
	[Property, Group( "Placement" )] public Material ViewMaterial { get; set; }

	/// <summary>While this machine renders the first-person gun, switch the sun's screen-space CONTACT
	/// shadows off (restored the moment we're not first-person: edit mode, death, pawn teardown).
	/// Escape hatch from the depth-squash era, when the viewmodel's near-camera depth blob read as a
	/// solid occluder in the contact-shadow march and self-shadowed the gun out of the directional
	/// light. The prepass writes the gun's REAL depth now, so contact shadows behave and this should
	/// stay OFF; flip it back on only if a contact-shadow artifact shows up on the gun.</summary>
	[Property, Group( "Placement" )] public bool DisableSunContactShadows { get; set; } = false;

	GameObject _world;
	GameObject _view;
	SdfRaymarchRenderer _worldSdf;
	SdfRaymarchRenderer _viewSdf;
	SdfSculpture _worldSculpt;
	SdfSculpture _viewSculpt;

	// Each clone's "Muzzle" child (authored in the gun prefab; optional — an older gun sculpt without one
	// just has no flash), plus the PREFAB-authored local position it's re-derived from. Pristine for the
	// same reason as _source: the gun scales by scaling BRUSH data, not the GameObject, so the muzzle point
	// must be repinned to the live scale in Place — and a snapshot-received clone arrives with the owner's
	// scale already applied, making its live LocalPosition an unusable baseline.
	GameObject _worldMuzzle;
	GameObject _viewMuzzle;
	Vector3 _muzzleSource;

	// The last eye/aim/mode Place ran with — Kick (fired from the shot RPC, outside the placement call) uses
	// these to pick which model's muzzle flashes and to aim the burst along the shot.
	Vector3 _lastEye;
	Rotation _lastAim = Rotation.Identity;
	bool _lastFirstPerson;

	// The sun whose ContactShadows we override while first-person, and whether we currently hold an
	// override (only ever restore a value WE changed — if the scene authored them off, stay hands-off).
	DirectionalLight _sun;
	bool _sunOverridden;

	// Viewmodel motion state. The pawn's controller feeds bob speed/grounding. The ROTATION spring tracks
	// the WORLD aim (that's what makes lag exist at all — a purely local-space spring settles and never
	// lags); the POSITION spring is deliberately the opposite, a local aim-space offset resting at zero,
	// so look/movement never displace the gun and only recoil impulses animate it.
	// _motionSeeded false = re-seed on the next first-person frame, so entering first person (spawn,
	// leaving edit mode) snaps the springs onto their targets instead of twanging across the map.
	PlayerController _controller;
	HunterController _hunter; // stride tuning (StepDistance/RunStepDistance) lives here, shared with footsteps
	float _bobPhase;
	float _bobBlend;
	Vector3 _recoilPos; // aim-space offset from the rigid mount (x forward, so recoil pushes −x)
	Vector3 _recoilVel;
	Rotation _springRot = Rotation.Identity;
	Vector3 _angVel; // degrees/s, in the spring's local frame (pitch/yaw/roll)
	bool _motionSeeded;
	float _worldKick; // world-model recoil envelope, 1 at the shot → 0, decayed in Place
	Vector3 _handRestPos; // the Hand's authored local pose, re-read while no kick is active (see Place)
	Rotation _handRestRot = Rotation.Identity;

	/// <summary>The Hand's AUTHORED local pose, published by the owner and serialized so it rides into spawn
	/// snapshots. Not for hand-editing — it exists so a client joining mid-recoil can tell the difference between
	/// the pose the hand rests at and the kicked one it happened to arrive in. See <see cref="Place"/>.</summary>
	[Property, Hide] public Vector3 HandRestPosition { get; set; }

	/// <inheritdoc cref="HandRestPosition"/>
	[Property, Hide] public Rotation HandRestRotation { get; set; } = Rotation.Identity;

	/// <summary>Whether <see cref="HandRestPosition"/> has been published yet — a plain "is this trustworthy",
	/// since a zero pose is indistinguishable from an unset one.</summary>
	[Property, Hide] public bool HandRestSaved { get; set; }

	// The pristine prefab-scale brush list both models derive from, and the scale each model last applied
	// (0 = never — the first Place() always applies). Deriving from the SOURCE every time (instead of scaling
	// the live brushes in place) is what makes the scales freely re-tweakable: no compounding, no baseline
	// drift, and a snapshot-received clone (streamed at the owner's scale) self-heals to ours.
	List<SdfBrush> _source;
	float _worldApplied;
	float _viewApplied;

	protected override void OnStart()
	{
		// Bob reads real locomotion (speed + grounding) off the pawn's own controller — owner-side only,
		// which is the only place the viewmodel exists — and the stride tuning off the HunterController,
		// so the bob and the footstep sounds derive their cadence from the same numbers.
		_controller = Components.Get<PlayerController>();
		_hunter = Components.Get<HunterController>();

		// The arm hierarchy: pawn → Shoulder → Hand → GunWorld. Authored in hunter.prefab (so the hand offset
		// is tweakable in the editor); created here when dropped on a bare GameObject so it still works.
		Shoulder = Resolve( Shoulder, GameObject, "Shoulder", new Vector3( 0f, -9f, 50f ) );
		Hand = Resolve( Hand, Shoulder, "Hand", new Vector3( 26f, 0f, -2f ) );

		// By-name existence check first (like the hider's Disguise clone): a late joiner may receive the owner's
		// clones inside the scene snapshot, and spawning a second pair on top would double the gun.
		_world = EnsureClone( "GunWorld", Hand );
		_view = EnsureClone( "GunView", GameObject );

		_worldSdf = _world.IsValid() ? _world.Components.Get<SdfRaymarchRenderer>( FindMode.EverythingInSelfAndDescendants ) : null;
		_viewSdf = _view.IsValid() ? _view.Components.Get<SdfRaymarchRenderer>( FindMode.EverythingInSelfAndDescendants ) : null;
		_worldSculpt = _world.IsValid() ? _world.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndDescendants ) : null;
		_viewSculpt = _view.IsValid() ? _view.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndDescendants ) : null;

		_source = LoadSourceBrushes();

		_worldMuzzle = FindMuzzle( _world );
		_viewMuzzle = FindMuzzle( _view );
		_muzzleSource = LoadMuzzleSource();

		// Viewmodel: no shadow of any kind — SdfShadows off stops the raymarch casting, MeshMode.Hidden stops
		// the legacy shadows-only sibling mesh taking over the job. Applied to snapshot-received clones too
		// (idempotent), since the owner's instance streams with these already set but a fresh one doesn't.
		// (ViewmodelLayer is asserted per frame in Place, from the live-tweakable UseViewmodelLayer.)
		if ( _viewSdf.IsValid() )
		{
			_viewSdf.SdfShadows = false;
			_viewSdf.MeshMode = SdfMeshMode.Hidden;
			// Keep the viewmodel out of the pawn's highlight union: it's an owner-only overlay, not part
			// of the silhouette, and as a fifth renderer it overflows the outline shader's 4 slots.
			_viewSdf.ExcludeFromHighlight = true;
		}

		// Starts hidden everywhere; Place() enables it once this machine is known to be the owner. Ownership
		// resolves after OnStart (see HunterController), so this can't be decided here.
		if ( _view.IsValid() )
			_view.Enabled = false;
	}

	/// <summary>Drive the arm and viewmodel off the given eye — called by <see cref="HunterController"/> every
	/// frame on every machine, AFTER it has placed the camera, with the same smoothed eye.
	/// <paramref name="firstPerson"/> is true only on the owning machine outside edit mode: the viewmodel shows
	/// and the world model self-hides (shadows-only), mirroring the body treatment.
	/// <paramref name="armOffset"/> displaces the ARM only (world space, world model), leaving the viewmodel on
	/// the eye. It carries everything the body does that the view deliberately doesn't: the crouch lift (the eye
	/// drops further than the chest, and the arm hangs off the chest), the jump's launch trail, and the walk bob.
	/// The viewmodel keeps its own bob and springs, so nothing here double-counts.</summary>
	/// <summary>Per-machine runtime hide for the WHOLE gun display (world model and viewmodel alike) — the
	/// tutorial's edit session sets it on the owning machine, where the pawn's body is hidden too and a
	/// floating gun beside the sculpture would just be clutter. Asserted every frame by
	/// <see cref="HunterController.HideOwnBody"/>; never networked (proxies keep their view of the pawn).</summary>
	public bool HideAll { get; set; }

	public void Place( Vector3 eye, Angles aim, bool firstPerson, Vector3 armOffset = default )
	{
		// Scale-on-change, so the properties are live while playing. The world model on every machine; the
		// viewmodel only where it's shown (proxies never enable theirs — no point building it).
		ApplyScale( _worldSculpt, WorldGunScale, ref _worldApplied );
		if ( firstPerson )
			ApplyScale( _viewSculpt, ViewGunScale, ref _viewApplied );

		ApplySunContactShadows( firstPerson );

		var aimRot = aim.ToRotation();

		// What Kick's muzzle flash spawns from (see SpawnMuzzleFlash — the shot RPC lands between frames).
		_lastEye = eye;
		_lastAim = aimRot;
		_lastFirstPerson = firstPerson;

		// The arm: pin the shoulder to the body (yaw-only swing so it stays at the pawn's side), rotate it with
		// the FULL aim. The hand — and the gun parented under it — arcs around this pivot like an arm would.
		if ( Shoulder.IsValid() )
		{
			Shoulder.WorldPosition = eye + armOffset + Rotation.FromYaw( aim.yaw ) * ShoulderOffset;
			Shoulder.WorldRotation = aimRot;
		}

		// The recoil jolt moves the HAND, not the gun — the whole hand (and anything else attached to it)
		// snaps back through the shoulder frame and flips up around the hand pivot, with the gun riding
		// along rigid in the grip. Shoulder space has x forward, so −x drives the hand straight back along
		// the aim.
		//
		// The OWNER re-reads the rest pose from the live transform whenever no kick is riding, which is what
		// keeps the authored pose live-tweakable in the editor — and PUBLISHES it, because a proxy can't safely
		// do the same. A spawn snapshot ships the owner's live component state, so a client joining mid-recoil
		// receives the Hand already kicked; sampling that would enshrine the recoil pose as rest and leave that
		// player's gun stuck high forever. _worldKick is a plain field and starts at 0 on a fresh proxy, so it
		// can't be used to detect the case either. Proxies therefore take the authored pose from the snapshot
		// and never sample what they happened to arrive holding.
		_worldKick = _worldKick < 0.001f ? 0f : _worldKick * MathF.Exp( -WorldKickRecover * MathF.Min( Time.Delta, 0.1f ) );
		if ( Hand.IsValid() )
		{
			if ( !IsProxy && _worldKick <= 0f )
			{
				_handRestPos = Hand.LocalPosition;
				_handRestRot = Hand.LocalRotation;
				HandRestPosition = _handRestPos;
				HandRestRotation = _handRestRot;
				HandRestSaved = true;
			}
			else if ( HandRestSaved )
			{
				_handRestPos = HandRestPosition;
				_handRestRot = HandRestRotation;
			}
			else if ( _worldKick <= 0f )
			{
				// Nothing published yet (a proxy that arrived before the owner's first placement). Best effort
				// from the live pose, which is right as long as they aren't mid-recoil — the old behaviour.
				_handRestPos = Hand.LocalPosition;
				_handRestRot = Hand.LocalRotation;
			}

			Hand.LocalPosition = _handRestPos + new Vector3( -WorldKickBack * _worldKick, 0f, 0f );
			Hand.LocalRotation = new Angles( -WorldKickUp * _worldKick, 0f, 0f ).ToRotation() * _handRestRot;
		}

		// The world gun rides the Shoulder→Hand hierarchy; only its mount needs asserting (per frame so
		// HandOffset / RotationOffset stay live-tweakable in the editor).
		if ( _world.IsValid() )
		{
			_world.LocalPosition = HandOffset;
			_world.LocalRotation = RotationOffset.ToRotation();
		}

		if ( _worldSdf.IsValid() )
		{
			_worldSdf.RenderHidden = firstPerson || HideAll;
			ApplyFieldResolution( _worldSdf, WorldFieldResolution );
		}

		// Repin the muzzle points to the live scales (per frame, so the scale sliders stay live): the brush
		// data scales, the GameObject hierarchy doesn't, so the authored Muzzle child would otherwise sit at
		// full prefab-scale distance off the end of a half-scale barrel.
		if ( _worldMuzzle.IsValid() )
			_worldMuzzle.LocalPosition = _muzzleSource * WorldGunScale;
		if ( _viewMuzzle.IsValid() && firstPerson )
			_viewMuzzle.LocalPosition = _muzzleSource * ViewGunScale;

		if ( _view.IsValid() )
		{
			_view.Enabled = firstPerson && !HideAll;
			if ( firstPerson )
			{
				if ( _viewSdf.IsValid() )
				{
					ApplyFieldResolution( _viewSdf, ViewFieldResolution );
					_viewSdf.ViewLayer = ViewLayerMode;
					_viewSdf.ViewmodelFovScale = FovScale();

					// The translucent-lighting material variant, live-swappable for A/B (the renderer
					// hashes its Material — same reference re-set is free, a change repacks).
					if ( ViewMaterial is not null )
						_viewSdf.Material = ViewMaterial;
				}

				// The rigid mount (offset + bob), with the recoil spring's aim-space offset riding on
				// top of the PIVOT point (grip); the rotation lag swings the gun AROUND that pivot —
				// so the hand stays planted while the muzzle sweeps.
				var targetPos = eye + aimRot * ( ViewOffset + UpdateBob() );
				UpdateViewSprings( aimRot );

				// Strength scales the RENDERED effect, not the simulation — the spring's character
				// (speed, bounce) is untouched, only how much of its offset shows. Rotation lag scales
				// in angle space (small + clamped, so per-component scaling is safe).
				var pivotPos = targetPos + aimRot * ( _recoilPos * PositionStrength );
				var lagAngles = ( aimRot.Inverse * _springRot ).Angles();
				var lagRot = aimRot * Rotation.From( new Angles(
					lagAngles.pitch * RotationStrength,
					lagAngles.yaw * RotationStrength,
					lagAngles.roll * RotationStrength ) );

				var rot = lagRot * RotationOffset.ToRotation();
				_view.WorldRotation = rot;
				_view.WorldPosition = pivotPos - rot * ( ViewRotationPivot * ViewGunScale );
			}
			else
			{
				_motionSeeded = false; // next first-person frame re-seeds instead of twanging from stale state
			}
		}

		// Push the renderers' placement snapshot NOW, with every transform above final. Component OnUpdate
		// order is hash-set enumeration order, which reshuffles when a pawn is destroyed and respawned (slot
		// reuse) — so the clone renderers' own update can land BEFORE this placement, packing LAST frame's
		// transform while the camera (composed after all updates) is current: the gun renders a frame behind
		// the view. Pushing from here is order-proof; when the renderer's update also runs after us, its own
		// Refresh sees an unchanged hash and only re-sets attributes.
		if ( _worldSdf.IsValid() )
			_worldSdf.Refresh();
		if ( firstPerson && _viewSdf.IsValid() )
			_viewSdf.Refresh();
	}

	/// <summary>One shot's recoil — called on EVERY machine from the shot RPC (the camera's own punch is
	/// separate, HunterController's ShotKick). The world model starts its decaying hand jolt everywhere;
	/// the viewmodel takes a velocity impulse into its live springs (owner-side only — elsewhere the
	/// springs are dormant and reseed on the next first-person frame, which would wipe it anyway).</summary>
	public void Kick()
	{
		_worldKick = 1f;

		// Before the seeded gate: proxies never seed the view springs, but their world gun still flashes.
		SpawnMuzzleFlash();

		if ( !_motionSeeded )
			return;

		// Impulses sized so the PEAK excursion lands near the property value at critical damping
		// (x(t) = v·t·e^(−ωt) peaks at v/(e·ω), ω = √k) — retuning a spring keeps the throw and only
		// changes the ride. The position impulse is aim-space −x = straight back at the camera;
		// negative pitch rate = muzzle up, same convention as the camera punch.
		if ( PositionStiffness > 0f && KickBack > 0f )
			_recoilVel += new Vector3( -KickBack * MathF.E * MathF.Sqrt( PositionStiffness ), 0f, 0f );

		if ( RotationStiffness > 0f && KickUp > 0f )
			_angVel += new Vector3( -KickUp * MathF.E * MathF.Sqrt( RotationStiffness ), 0f, 0f );
	}

	// One flash clone per shot at the gun's Muzzle point — the VIEW model's where that's what's on screen
	// (owner, first person), the WORLD model's everywhere else (proxies, and the owner in edit mode).
	// Parented to the gun clone so the burst rides the recoil jolt for its short life; oriented along the
	// shot so the prefab's cone emitters (which emit along their object's forward) spray downrange.
	void SpawnMuzzleFlash()
	{
		var muzzle = _lastFirstPerson ? _viewMuzzle : _worldMuzzle;
		if ( !muzzle.IsValid() )
			return;

		var flash = MuzzleFlashPrefab is null ? null : SceneUtility.GetPrefabScene( MuzzleFlashPrefab )?.Clone();
		if ( !flash.IsValid() )
			return;

		flash.Parent = muzzle;
		flash.Tags.Add( CloneTag ); // same skip-tag as the gun clones, so pawn-wide sweeps never adopt it
		flash.WorldPosition = MuzzleFlashPosition( muzzle );
		flash.WorldRotation = _lastAim;

		if ( _lastFirstPerson && !ViewFlashScale.AlmostEqual( 1f ) )
			ScaleFlash( flash, ViewFlashScale );

		ExpireFlash( flash );
	}

	// Particle SIZES are world-unit values that ignore transform scale (the sim writes p.Size straight from
	// the effect's Scale parameter), so a bare GameObject scale only grows the flash's GEOMETRY — emitter
	// offsets, cone shapes, local-space travel. Do both: transform scale for the geometry, and each sprite
	// renderer's render multiplier for the sprites themselves.
	static void ScaleFlash( GameObject flash, float s )
	{
		flash.LocalScale = s;

		foreach ( var r in flash.Components.GetAll<ParticleSpriteRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			r.Scale *= s;
	}

	// Where the flash must spawn to sit on the ON-SCREEN barrel tip. The world model renders at its true
	// position, and so does the viewmodel in the Normal layer — but in any other layer the raymarch shader
	// re-projects the gun into ViewmodelFov's projection (lateral offsets from the eye scaled by FovScale).
	// A world-space particle doesn't get that warp, so apply the same one here or the flash lands visibly
	// off the muzzle. Matches the shader's gate exactly (warp only when the viewmodel flag is set).
	Vector3 MuzzleFlashPosition( GameObject muzzle )
	{
		var pos = muzzle.WorldPosition;
		if ( !_lastFirstPerson || ViewLayerMode == SdfViewLayer.Normal )
			return pos;

		var rel = _lastAim.Inverse * ( pos - _lastEye );
		float s = FovScale();
		return _lastEye + _lastAim * new Vector3( rel.x, rel.y * s, rel.z * s );
	}

	// The flash is a one-shot (the smoke tail lives ~2s) and GameObject has no delayed destroy — retire the
	// clone once every particle is long dead, same as the caught puff. Component.Task cancels this on pawn
	// teardown, and the clone hangs under the gun hierarchy, so it dies with the pawn either way.
	async void ExpireFlash( GameObject flash )
	{
		await Task.DelaySeconds( 2.5f );
		if ( flash.IsValid() )
			flash.Destroy();
	}

	// A clone's authored "Muzzle" child — a direct child of the gun prefab's root, cloned along with it.
	static GameObject FindMuzzle( GameObject clone ) =>
		clone.IsValid() ? clone.Children.FirstOrDefault( c => c.Name == "Muzzle" ) : null;

	// The muzzle point's PREFAB-authored local position, read from the prefab scene like LoadSourceBrushes —
	// see _muzzleSource for why the live clone's value can't be the baseline. Falls back to the live child
	// (fresh clones still carry the authored value before the first Place), else zero (flash at the grip —
	// wrong but visible, and only reachable with an unreadable prefab AND no muzzle child).
	Vector3 LoadMuzzleSource()
	{
		var authored = GunPrefab is null
			? null
			: SceneUtility.GetPrefabScene( GunPrefab )?.Children.FirstOrDefault( c => c.Name == "Muzzle" );

		if ( authored.IsValid() )
			return authored.LocalPosition;

		if ( _worldMuzzle.IsValid() )
			return _worldMuzzle.LocalPosition;

		return _viewMuzzle.IsValid() ? _viewMuzzle.LocalPosition : Vector3.Zero;
	}

	// Walk bob, as an AIM-SPACE offset added to ViewOffset. Runs only on the owner's first-person frames.
	Vector3 UpdateBob()
	{
		float dt = MathF.Min( Time.Delta, 0.05f );

		if ( !_motionSeeded )
			_bobBlend = 0f;

		// ── Bob ── cadence LOCKED to the footstep sounds: the phase advances π per stride, and the
		// stride is the same speed-blended value UpdateFootsteps uses (StepDistance at walk →
		// RunStepDistance at run), so the vertical dip and the step sound tick the same clock at every
		// speed. Amplitude blends smoothly with movement and fades while airborne — a jump glides, it
		// doesn't drum. (Airborne time resets the footstep accumulator but not the bob phase, so the
		// phase RELATIONSHIP can shift across a jump — the shared cadence is what the ear locks onto.)
		float speed = _controller.IsValid() ? _controller.Velocity.WithZ( 0f ).Length : 0f;
		bool grounded = !_controller.IsValid() || _controller.IsOnGround;
		float walk = _controller.IsValid() ? _controller.WalkSpeed : 110f;
		float run = _controller.IsValid() ? MathF.Max( _controller.RunSpeed, 1f ) : 320f;
		float stepDist = _hunter.IsValid() ? _hunter.StepDistance : 60f;
		float runStepDist = _hunter.IsValid() ? _hunter.RunStepDistance : 100f;

		float bobTarget = grounded ? MathF.Min( speed / run, 1f ) : 0f;
		_bobBlend = _bobBlend.LerpTo( bobTarget, 1f - MathF.Exp( -8f * dt ) );

		float stride = MathF.Max( speed.Remap( walk, run, stepDist, runStepDist ), 1f );
		_bobPhase += MathF.PI * ( speed * dt / stride ) * BobFrequency;

		// Lateral sway at step rate, vertical bounce at double rate (two foot-falls per lateral cycle).
		return new Vector3(
			0f,
			MathF.Sin( _bobPhase ) * 0.6f,
			MathF.Sin( _bobPhase * 2f ) * 0.45f ) * ( BobAmount * _bobBlend );
	}

	// The two Hooke springs (F = k·error − c·velocity, mass 1). The ROTATION spring tracks the WORLD aim —
	// that's what makes lag exist: the gun is left behind by real turns and pulled back in; its damping is
	// absolute, so a steady turn holds a steady lag angle (the classic loose-gimbal look). The POSITION
	// spring is a LOCAL aim-space offset resting at zero: look/movement can't displace it, only Kick()'s
	// impulses do, and it just rings back down — pure recoil, no view lag. Integrated semi-implicitly in
	// fixed ≤1/120s substeps: stability is independent of frame rate for any stiffness the sliders allow.
	void UpdateViewSprings( Rotation targetRot )
	{
		if ( !_motionSeeded )
		{
			_motionSeeded = true;
			_recoilPos = Vector3.Zero;
			_recoilVel = Vector3.Zero;
			_springRot = targetRot;
			_angVel = Vector3.Zero;
		}

		float frame = MathF.Min( Time.Delta, 0.1f );

		// Stiffness 0 = that spring is off -> rigid attach.
		bool posRigid = PositionStiffness <= 0f;
		bool rotRigid = RotationStiffness <= 0f;

		for ( float remaining = frame; remaining > 0f; remaining -= 1f / 120f )
		{
			float h = MathF.Min( remaining, 1f / 120f );

			if ( !posRigid )
			{
				_recoilVel -= ( _recoilPos * PositionStiffness + _recoilVel * PositionDamping ) * h;
				_recoilPos += _recoilVel * h;
			}

			if ( !rotRigid )
			{
				// Error as small local angles (spring -> target). Lag stays clamped well under 180°,
				// so Euler on the error is wrap-safe.
				var err = ( _springRot.Inverse * targetRot ).Angles();
				_angVel += ( new Vector3( err.pitch, err.yaw, err.roll ) * RotationStiffness
					- _angVel * RotationDamping ) * h;
				_springRot *= Rotation.From( new Angles( _angVel.x * h, _angVel.y * h, _angVel.z * h ) );
			}
		}

		if ( posRigid )
			_recoilPos = Vector3.Zero;
		if ( rotRigid )
			_springRot = targetRot;

		// Sanity bounds: a hitch or an extreme tuning can't leave the gun across the screen or facing
		// backwards — clamp the ERROR, keep the velocity (the spring still animates from the clamp).
		if ( _recoilPos.Length > MaxPositionLag )
			_recoilPos = _recoilPos.Normal * MaxPositionLag;

		var lag = ( targetRot.Inverse * _springRot ).Angles();
		var lagV = new Vector3( lag.pitch, lag.yaw, lag.roll );
		if ( lagV.Length > MaxRotationLag )
		{
			lagV = lagV.Normal * MaxRotationLag;
			_springRot = targetRot * Rotation.From( new Angles( lagV.x, lagV.y, lagV.z ) );
		}
	}

	// tan(cameraHalf)/tan(viewmodelHalf) — the shader's warp/unwarp ratio that re-projects the gun's rays
	// into ViewmodelFov's own projection. Reads the live camera FOV each frame, so a camera FOV change
	// re-derives the ratio and the gun's on-screen rendering stays EXACTLY as authored (full decoupling —
	// contrast with the old ViewBaseFov offset trick, which only stabilized size/position, not distortion).
	float FovScale()
	{
		var cam = Scene.Camera;
		if ( !cam.IsValid() )
			return 1f;

		float camTan = MathF.Tan( cam.FieldOfView.DegreeToRadian() * 0.5f );
		float vmTan = MathF.Tan( Math.Clamp( ViewmodelFov, 5f, 170f ).DegreeToRadian() * 0.5f );
		return vmTan > 0.001f ? camTan / vmTan : 1f;
	}

	// Assert a field resolution on a clone's renderer. Safe to call every frame: the renderer folds
	// FieldResolution into its field hash and only re-bakes when the value actually changes. Above 256 the
	// dense field would silently clamp (a full 512³ volume is ~537 MB), so the sparse path is forced on.
	static void ApplyFieldResolution( SdfRaymarchRenderer sdf, int resolution )
	{
		sdf.FieldResolution = resolution;
		if ( resolution > 256 )
			sdf.SparseField = true;
	}

	// Hold the sun's contact shadows off while the first-person gun is on screen (see DisableSunContactShadows),
	// give them back the moment it isn't. Purely local rendering state — scene-component property changes
	// don't replicate, so hiders/other machines keep their contact shadows untouched.
	void ApplySunContactShadows( bool firstPerson )
	{
		bool want = firstPerson && DisableSunContactShadows && ViewLayerMode != SdfViewLayer.Normal;

		if ( want && !_sunOverridden )
		{
			if ( !_sun.IsValid() )
				_sun = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault( l => l.ContactShadows );

			if ( _sun.IsValid() )
			{
				_sun.ContactShadows = false;
				_sunOverridden = true;
			}
		}
		else if ( !want && _sunOverridden )
		{
			if ( _sun.IsValid() )
				_sun.ContactShadows = true;
			_sunOverridden = false;
		}
	}

	protected override void OnDisabled()
	{
		// Pawn teardown/disable while first-person — give the sun its contact shadows back.
		ApplySunContactShadows( false );
	}

	// Swap the sculpture's brushes for a freshly scaled derivation of the source list, only when the wanted
	// scale actually changed (float-equal compare: a slider tweak lands as one rebuild). Builds are async and
	// model-cached, and the sculpt editor rebuilds far more aggressively than a slider drag ever will.
	void ApplyScale( SdfSculpture sculpt, float scale, ref float applied )
	{
		if ( !sculpt.IsValid() || _source is null || applied == scale )
			return;

		applied = scale;
		sculpt.Brushes = _source.Select( b => ScaledCopy( b, scale ) ).ToList();
		sculpt.Rebuild();
	}

	// A copy of the brush with every length-like field scaled together, so the shape is a true uniform
	// miniature of the source. BoundingRadius/LocalCentre are computed properties — nothing to touch.
	static SdfBrush ScaledCopy( SdfBrush src, float s )
	{
		var b = src.Copy();
		b.Position *= s;
		b.Size *= s;
		b.Blend *= s;
		b.Rounding = MathF.Max( 0.05f, b.Rounding * s );

		if ( b.Points is not null )
		{
			for ( int i = 0; i < b.Points.Count; i++ )
			{
				var p = b.Points[i];
				b.Points[i] = new Vector4( p.x * s, p.y * s, p.z * s, p.w * s );
			}
		}

		return b;
	}

	// The pristine prefab-scale brushes, read straight from the prefab's loaded scene — no instantiation, and
	// it works even when BOTH clones pre-existed at some already-applied scale (the late-join snapshot case,
	// where the live brushes are not a usable baseline). Copies, so nothing here can mutate the prefab's data.
	List<SdfBrush> LoadSourceBrushes()
	{
		var sculpt = GunPrefab is null
			? null
			: SceneUtility.GetPrefabScene( GunPrefab )?.GetAllComponents<SdfSculpture>().FirstOrDefault();

		if ( sculpt is null || sculpt.Brushes is null )
		{
			// Fallback: a fresh clone still carries unscaled prefab brushes at this point (scaling only ever
			// happens in Place). Only a snapshot-received clone would be wrong here, and then we'd rather show
			// the owner's scale than nothing.
			var live = _worldSculpt.IsValid() ? _worldSculpt : _viewSculpt;
			if ( !live.IsValid() || live.Brushes is null )
				return null;

			Log.Warning( $"Gun prefab '{GunPrefab?.ResourcePath}' scene unreadable — sourcing brushes from the live clone." );
			return live.Brushes.Select( b => b.Copy() ).ToList();
		}

		return sculpt.Brushes.Select( b => b.Copy() ).ToList();
	}

	// Resolve an arm point: the wired property, else an existing child by name, else a freshly created one at
	// the given local offset (the bare-GameObject fallback — the prefab authors these).
	static GameObject Resolve( GameObject wired, GameObject parent, string name, Vector3 localPos )
	{
		if ( wired.IsValid() )
			return wired;

		var existing = parent.Children.FirstOrDefault( c => c.Name == name );
		if ( existing.IsValid() )
			return existing;

		var go = new GameObject( true, name );
		go.Parent = parent;
		go.LocalPosition = localPos;
		go.LocalRotation = Rotation.Identity;
		return go;
	}

	// Find an existing clone by name under the given parent, else spawn a fresh one there (unscaled — Place()
	// applies the wanted scale on the first frame, superseding the clone's own initial build).
	GameObject EnsureClone( string name, GameObject parent )
	{
		if ( !parent.IsValid() )
			return null;

		var existing = parent.Children.FirstOrDefault( c => c.Name == name );
		if ( existing.IsValid() )
			return existing;

		if ( GunPrefab is null )
		{
			Log.Warning( "Gun prefab not set — hunter gets no gun model." );
			return null;
		}

		var go = SceneUtility.GetPrefabScene( GunPrefab )?.Clone();
		if ( !go.IsValid() )
			return null;

		go.Name = name;
		go.Parent = parent;
		go.Tags.Add( CloneTag );
		go.WorldPosition = WorldPosition; // parked on the pawn until the first Place() this same frame

		StripNonVisuals( go );

		return go;
	}

	// The prefab is a full sculpt-save export; as an attachment only the visual stack survives. Colliders would
	// bind into the pawn's physics, and the outline would glow through walls for everyone (RoundOutlineSystem's
	// hunter rule applies to every outline under the pawn).
	static void StripNonVisuals( GameObject go )
	{
		foreach ( var c in go.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ).ToArray() )
		{
			if ( c is SdfCollider or ModelCollider or SdfHighlightOutline )
				c.Destroy();
		}
	}
}
