using System;

namespace Mimiclay;

/// <summary>
/// Hider: a third-person controller whose body is an <see cref="SdfSculpture"/> disguise. As a character its
/// <see cref="Rigidbody"/> is KEYFRAMED: it moves by sweeping one convex move hull (see
/// <c>HiderController.Movement</c> and <see cref="PropMoveHull"/>) and nothing in the physics world can push it.
/// The disguise's generated <see cref="ModelCollider"/> rides that body, so to everyone else a prop is exactly as
/// solid as it looks. Held, loose and frozen props (creative's grab) switch the same body to a dynamic object.
/// None of this is shared with the hunter, which is a vanilla first-person <see cref="PlayerController"/>.
///
/// <b>Play mode:</b> WASD to move, free mouse-look turns the camera AND the disguise together. Holding Alt
/// decouples them — the mouse orbits only the camera (the disguise keeps facing where it was); release Alt and
/// the disguise simply adopts wherever the camera now points (no snap-back).
///
/// <b>Edit mode</b> (tab): locomotion input stops and the sculpt gizmo appears; the camera switches to
/// Maya-style alt navigation (alt+LMB orbit, alt+RMB dolly, alt+MMB vertical pan), cursor freed. The mover keeps
/// running without input, so the prop re-settles onto its new shape as you sculpt.
/// </summary>
[Title( "Hider Controller" )]
[Category( "Mimiclay" )]
[Icon( "directions_run" )]
public sealed partial class HiderController : Component, IGameObjectNetworkEvents
{
	/// <summary>Collision tag stamped on every player prop's disguise. ProjectSettings/Collision.config pairs it
	/// with ITSELF as Ignore, so two player props never generate contacts — they pass through each other and
	/// stay exactly as solid as before against the world, decoys and hunters (an unlisted pair falls back to the
	/// two tags' defaults, and this one defaults to Collide).
	///
	/// <b>Why.</b> Props can't see each other while hiding (in Infection they aren't even on the wire until the
	/// Hunt — see RoundManager), so two of them choosing the same spot is a thing the game actively prevents them
	/// from noticing. Non-colliding props just overlap visually, which is rare and harmless. The prop mover's
	/// sweeps look through the tag for the same reason.
	///
	/// Applied at RUNTIME to the pawn's disguise clone rather than authored on disguise.prefab: saved props and
	/// decoys are exported from that same template, and they must stay solid to players. Physics shapes take
	/// their tags from the collider's GameObject and follow it live, so tagging the object is enough however the
	/// collider was built.
	///
	/// The tag means "this disguise is currently someone's BODY", so a RELEASED prop (creative P-drop, lobby
	/// furniture hand-back) must LOSE it — it's scenery now, and scenery is solid to props. Asserted per-frame on
	/// every machine from <see cref="PropClaims.IsReleased"/> (see <see cref="UpdateReleasedSolidity"/>), so a
	/// claim re-tags it everywhere just as automatically.</summary>
	public const string PropBodyTag = "propbody";

	// ── Movement ──────────────────────────────────────────────────────────────────────────────────────
	// Props have no sprint: they always move at the hunter's RUN speed (the stock PlayerController's RunSpeed 320 the
	// hunter prefab runs on), so a prop can always break a chase, and Shift is free (it lets go of a climb). Crouch is
	// the fine-positioning speed — the hunter's walk, 110 — for lining a prop up exactly where it should hide.
	[Property, Group( "Movement" )] public float WalkSpeed { get; set; } = 320f;
	[Property, Group( "Movement" )] public float CrouchSpeed { get; set; } = 110f;

	/// <summary>Launch speed of a jump (u/s) — the hunter's JumpSpeed, against the same 800 rise gravity, so both
	/// pawns clear exactly the same height (~56u).</summary>
	[Property, Group( "Movement" )] public float JumpPower { get; set; } = 300f;

	/// <summary>Upward gravity while rising — tuned so the jump arc up feels right. We integrate gravity
	/// ourselves (engine gravity off) so the rise and fall can differ.</summary>
	[Property, Group( "Movement" )] public float Gravity { get; set; } = 800f;

	/// <summary>Extra gravity multiplier once falling, so you drop snappily instead of floating down. 1 = same
	/// as the rise; higher = quicker, punchier landings.</summary>
	[Property, Group( "Movement" )] public float FallGravityMult { get; set; } = 1.9f;

	/// <summary>How quickly horizontal velocity chases the input target on the ground (per second, exponential).
	/// Higher = snappier starts/stops; this also serves as the "friction" (input zero decays velocity to zero).
	/// The hunter has effectively NO ramp — the stock controller's AddVelocity adds several times the wish per tick
	/// and clamps, so it hits full speed within a tick or two. 30 reaches 95% in ~0.1s, close enough to read as
	/// instant while still easing the prop into motion.</summary>
	[Property, Group( "Movement" )] public float GroundControl { get; set; } = 30f;

	/// <summary>Weaker horizontal control while airborne, so a jump keeps most of its momentum.</summary>
	[Property, Group( "Movement" )] public float AirControl { get; set; } = 2.5f;

	// ── Look / Camera ─────────────────────────────────────────────────────────────────────────────────
	[Property, Group( "Look" ), Range( -89f, 0f )] public float MinPitch { get; set; } = -89f;
	[Property, Group( "Look" ), Range( 0f, 89f )] public float MaxPitch { get; set; } = 89f;

	[Property, Group( "Camera" )] public float CameraDistance { get; set; } = 180f;
	[Property, Group( "Camera" )] public float CameraHeightOffset { get; set; } = 16f;
	[Property, Group( "Camera" )] public float ZoomSpeed { get; set; } = 0.01f;
	[Property, Group( "Camera" )] public float PanSpeed { get; set; } = 1.0f;
	[Property, Group( "Camera" )] public float MinDistance { get; set; } = 24f;
	[Property, Group( "Camera" )] public float MaxDistance { get; set; } = 500f;

	/// <summary>Off = the camera boom no longer pulls in when geometry blocks it, so it keeps its full
	/// distance and clips through walls instead.</summary>
	[Property, Group( "Camera" )] public bool CameraCollision { get; set; } = true;

	/// <summary>In play mode, holding Alt alone orbits the camera (no mouse button needed) — a shortcut on top
	/// of the bare drag gestures (LMB orbit / MMB pan / RMB zoom, which never need alt). Off = only the drags
	/// navigate.</summary>
	[Property, Group( "Camera" )] public bool AltHoldOrbits { get; set; } = true;

	/// <summary>The disguise the hider sculpts. A prefab so its mesh/SDF renderer + materials are authored in the
	/// editor (a single source of truth), not duplicated in code.</summary>
	[Property, Group( "Disguise" )] public PrefabFile DisguisePrefab { get; set; }

	/// <summary>The "pop" played at a body swap — possessing a prop, popping out of one, and the hunter ⇄ prop
	/// swap. Lives on the prop pawn PREFAB (every mode's PropPrefab) rather than in code so the asset ships with
	/// the game (a runtime string path isn't picked up by the packager); <see cref="PropClaims"/> reads it off
	/// the prefab on every machine. See <see cref="PropClaims.PlaySwapPop"/>.</summary>
	[Property, Group( "Audio" )] public SoundEvent SwapPopSound { get; set; }

	/// <summary>Assets-relative path of the prefab this pawn's disguise was possessed from (set by
	/// <see cref="PropClaims"/> when a scene-placed prefab instance is converted); null for a shape sculpted
	/// from scratch. The editor HUD's Prefab "Save" writes over THIS prefab, never the disguise template.</summary>
	[Sync] public string DisguiseSource { get; set; }

	// Shape networking lives on a reusable SdfNetworkSync component (in the prop prefab), pointed at the disguise
	// in OnStart. The SDF core + this controller stay networking-agnostic — see SdfNetworkSync.

	/// <summary>Pin the orbit camera's pivot X/Y to the disguise shape's bounds centre (world) instead of the
	/// disguise origin — so the camera orbits the clay itself even when editing has walked the shape away from
	/// the pivot. Height (Z) is untouched: it stays the follow height plus your pan, and the shape itself
	/// never moves.</summary>
	[Property, Group( "Camera" )] public bool CenterPivotOnShape { get; set; } = true;

	/// <summary>How fast the pinned pivot eases onto the shape's centre (per second, exponential). The centre
	/// is smoothed in the body's local frame — moving and turning never lag — so this purely softens the
	/// camera's tracking while shapes are dragged/edited. Higher = tighter tracking.</summary>
	[Property, Group( "Camera" ), Range( 1f, 30f )] public float PivotSmoothSpeed { get; set; } = 8f;

	// ── Debug ─────────────────────────────────────────────────────────────────────────────────────────
	/// <summary>Draw a marker at the orbit camera's pivot every frame (magenta sphere + vertical line), so you
	/// can see exactly what point the camera is orbiting.</summary>
	[Property, Group( "Debug" )] public bool DebugCameraPivot { get; set; }

	// ── Runtime state ─────────────────────────────────────────────────────────────────────────────────
	/// <summary>Where the player is looking, seeded on spawn. Yaw drives movement + body facing.</summary>
	public Angles EyeAngles { get; private set; }

	/// <summary>Benchmark helper: aim the prop (PerfScenario views).</summary>
	internal void PerfSetEye( Angles a ) { EyeAngles = a with { roll = 0f }; _bodyYaw = a.yaw; }

	public bool IsCrouching { get; private set; }

	/// <summary>True when the move hull is resting on walkable ground (gates jumping + full ground control).</summary>
	public bool IsGrounded { get; private set; }

	Rigidbody Body;          // our physics body (the disguise's ModelCollider aggregates into it)
	Transform _fallAnchor;   // spawn placement, recorded in OnStart — where a fall respawn puts us back
	SdfSculpture _body;      // the sculpted disguise

	/// <summary>External access to the simulated physics body — e.g. <see cref="JumpPad"/> setting an outright
	/// launch velocity, same as it does via <c>PlayerController.Body</c> on the hunter side. Only meaningful
	/// (and safe to write) on the owning machine — see <see cref="Component.IsProxy"/> gating elsewhere in this
	/// file: a proxy's body isn't simulated, so a write here would just be overwritten by the next networked
	/// transform update.</summary>
	public Rigidbody PhysicsBody => Body;

	/// <summary>Soft leash: a horizontal circle the WHOLE body is kept inside WITHOUT a collider. Null =
	/// off. Two consumers share the one circle: movement here, and the clay in
	/// <see cref="BrushWorldClamp"/>, which reads it off the pawn root and stops additive brushes being
	/// dragged, grown or stamped past it — so what fits inside is the shape AND where it can walk.
	///
	/// Movement applies it inside the fixed step as a velocity rule, measured on the physics BOUNDS (the
	/// collider's flat extent is subtracted from the radius, so a wide sculpt has less room to roam and the
	/// shape never pokes past the line): the outward radial component of the move wish and of the resulting
	/// velocity is stripped at the edge, and a body shoved or re-sculpted past it gets a gentle inward pull
	/// back. Never a teleport, so it can't fight the solver the way a position snap against a wall did;
	/// vertical motion is untouched, so jumping on the spot still works. Set every tick by whoever owns the
	/// rule (the charades stage keeps the mimic on its plinth) — it live-reads, so a re-tuned radius just
	/// follows.</summary>
	public Vector3? LeashCentre { get; set; }

	/// <summary>Radius of <see cref="LeashCentre"/>'s circle — the line the body's bounds and its clay stay
	/// inside. Keep it a little inside any invisible fence the circle is standing in for.</summary>
	public float LeashRadius { get; set; } = 60f;

	// Inward pull when the root is outside the leash circle: u/s per unit of overshoot, and its cap.
	const float LeashPullRate = 4f, LeashPullMax = 160f;

	// Internal: the disguise sculpture, for code that needs THE pawn's body explicitly (PlayerNameplates
	// anchors above it) — resolved on every machine in OnStart, so proxies can read it too.
	internal SdfSculpture DisguiseSculpture => _body;

	/// <summary>True while this prop's player is in edit mode, on every machine — the roster HUD badges their
	/// pip with it and holds their icon still until they exit. Mirrored from the session each owner frame
	/// (rather than written at the toggle site) because the session can also exit itself — revert dialog,
	/// forced teardown — and a one-shot write would miss those.</summary>
	[Sync] public bool NetEditing { get; private set; }

	/// <summary>Camera orientation for this prop's roster icon, in DISGUISE-LOCAL space — the angle the orbit
	/// camera was at when the player last left edit mode, i.e. the view they signed the shape off from. The
	/// icon stage renders brushes in sculpture-local space, so local angles frame the same portrait on every
	/// machine no matter where the pawn has walked or turned since. <see cref="IconPoseSet"/> gates it.</summary>
	[Sync] public Angles IconPose { get; private set; }

	/// <summary>False until the first edit exit — icons fall back to the rig prefab's default pose.</summary>
	[Sync] public bool IconPoseSet { get; private set; }

	bool _editWas; // last frame's edit state, so the owner can catch the exit edge (see OnUpdate)
	bool _iconPoseSeeded; // owner: the portrait pose has been taken from our starting view (see OnUpdate)
	SdfCollider _collider;   // the disguise's physics (footprint snapshot + ModelCollider), if it's solid
	SculptEditSession _session;

	// The shared orbit rig owns the camera (orientation, zoom, pan, collision boom + the dot cursor) for BOTH
	// modes, so toggling edit never jumps the view and our camera runs the exact same code as the hunter's
	// face-edit camera. We Tick it ourselves rather than letting it run its own OnUpdate (see UpdateCamera).
	OrbitCameraController _orbit;
	float _bodyYaw;   // disguise + movement yaw — turns with the camera unless a camera-only orbit holds it still

	// Jump is an edge event read on the frame; consumed on the next fixed step so it can't be missed.
	bool _jumpQueued;

	// The facing yaw last fixed step — the delta is how much the mouse asked to turn this step. No absolute target
	// is stored, so we never try to force the body back to a "correct" yaw (that was the feedback loop).
	float _prevFacingYaw;
	bool _turnSeeded; // false until the first step / after a freeze, so resuming doesn't snap from a stale yaw

	internal bool EditMode => _session?.IsEditing ?? false;

	// Edit mode AND the Starting-countdown freeze (RoundManager.ControlsLocked) both stop locomotion input, but the
	// mover keeps running and the camera keeps running — so a frozen prop still settles onto the ground and
	// you can look around, you just can't walk or jump until the round begins. Free cam (see UpdateFreeCam) stops
	// it the same way: the body just sits/settles wherever it was left while the camera flies off without it.
	// The charades mimic's phrase pick (CharadesManager.LocalPicking) holds the prop too, until they've chosen.
	bool ControlActive => !EditMode && !RoundManager.ControlsLocked && !CharadesManager.LocalPicking && !_freeCam;

	// ── Free cam ──────────────────────────────────────────────────────────────────────────────────────
	/// <summary>Detaches the camera from the disguise entirely and flies it around the map, noclip-style —
	/// a scouting/spectating tool, not a movement mode: the body itself is untouched (see <see cref="ControlActive"/>),
	/// so it's purely a local camera change and needs no networking whatsoever (same reasoning as EditMode's
	/// camera swap into Maya-nav — nobody else's machine ever sees "your camera"). Toggled with a raw keyboard
	/// check (<see cref="Input.Keyboard"/>) rather than a named input action, mirroring RoundSetup.razor's "G" —
	/// this repo has no input-action manifest file to hand-register a new one in (only the s&box Editor's
	/// Project Settings UI can do that), so a direct key check needs no extra setup.</summary>
	[Property, Group( "Free Cam" )] public float FreeCamSpeed { get; set; } = 600f;

	[Property, Group( "Free Cam" )] public float FreeCamFastMultiplier { get; set; } = 3f;

	/// <summary>Sweep radius for the free-cam collision trace — small enough to thread doorways, big enough
	/// that the view doesn't poke through thin geometry.</summary>
	[Property, Group( "Free Cam" )] public float FreeCamCollisionRadius { get; set; } = 8f;

	/// <summary>Movement momentum, in seconds to (mostly) reach or shed full speed — the editor viewport's
	/// fly-camera feel, where the camera accelerates in and glides to a stop instead of starting/stopping
	/// dead on the key edge. 0 = the old instant response.</summary>
	[Property, Group( "Free Cam" ), Range( 0f, 1f )] public float FreeCamSmoothing { get; set; } = 0.3f;

	/// <summary>How fast the FOV eases toward the free cam's wide view (<see cref="GameSettings.SpectateFov"/>,
	/// the player's FOV preference) on entry and back toward <see cref="GameSettings.PropPlayFov"/> on exit
	/// (exponential, per second — lower = slower/smoother). Our OWN blend (<see cref="ApplySmoothFov"/>),
	/// independent of MainCamera's shared <c>FovLerpSpeed</c> (used by every other FOV transition in the game —
	/// hunter zoom, orbit/hunter swap — so tuning free cam's feel here can't accidentally change those too).</summary>
	[Property, Group( "Free Cam" )] public float FreeCamFovLerpSpeed { get; set; } = 3f;

	float _fovBlend;       // our own live-eased FOV value, independent of MainCamera's internal target-lerp
	bool _fovBlendSeeded;  // false until first driven, so the very first frame starts from whatever's live (no pop)

	bool _freeCam;
	Vector3 _freeCamVelocity; // smoothed fly velocity (momentum) — lives only while free cam is active

	// Released into the level (see ReleaseControl): the prop keeps simulating but takes no input and no longer owns
	// the camera, so a freshly-spawned pawn can take over while this one stays as scenery.
	bool _dormant;

	/// <summary>People are sculpting this released prop IN PLACE (<see cref="PropClaims.Editing"/>). It stays
	/// host-owned scenery throughout — the editors send brush ops to the host, whose copy is the one that
	/// changes — so this only matters on the HOST: a body being reshaped must stay awake to re-settle on each
	/// commit's new collider, where plain scenery sleeps. Registry-driven, so the answer is the same on every
	/// machine.</summary>
	bool BeingEdited => PropClaims.IsBeingEdited( this );

	/// <summary>Host-set BEFORE the NetworkSpawn (so it rides the spawn snapshot, never an ownership edge):
	/// this pawn was minted as SCENERY — scene clay converted for an in-place sculpt lease (see
	/// <see cref="PropClaims.LeaseFor"/>) — and spawned owned by its sculptor without anyone ever wearing it.
	/// Their copy must not run the body's control path on its first frames, before the claim registry rows
	/// (which also say so) have arrived. Cleared machine-locally by <see cref="ResumeControl"/> (a later
	/// possession IS someone wearing it).</summary>
	[Sync] public bool BornScenery { get; set; }
	bool _woken; // ResumeControl ran on this machine — BornScenery no longer gates

	// Is this body SCENERY on this machine — driven by nobody here, whatever ownership says? The single gate
	// for the control path (OnUpdate/OnFixedUpdate). Three reasons, each covering a window the others can't:
	// _dormant (this machine released it), the released registry (any machine, once the row lands), and
	// born-scenery (a converted shared prop's first frames on the host, before the row lands). Ownership alone
	// must never mean "I drive this".
	bool Scenery => _dormant || PropClaims.IsReleased( this ) || (BornScenery && !_woken);

	/// <summary>Hand this prop off, HOST-SIDE: mark it dormant so the host keeps simulating it as scenery (gravity +
	/// ground-snap still settle it) without driving it with host input/camera, and finish any host-side sculpt.
	/// One-way — used to scatter player-sculpted props around the level. Pairs with a <c>pawn.Network.DropOwnership()</c>
	/// in <c>DebugGameMode.Retire</c>: this only reaches the HOST's copy, so the owning CLIENT's own teardown (leaving
	/// edit mode) is driven by <c>StopControl</c> when that drop turns its copy into a proxy. Splitting it this way is
	/// deliberate — each machine tears down the state it actually holds (<c>_dormant</c>/edit are machine-local).</summary>
	public void ReleaseControl()
	{
		_dormant = true;

		// A possession the host granted but this copy hasn't consumed yet (BeginPossession → next OnUpdate) is
		// void: the prop has just been let go of again. Without this the latch fired AFTER the release — on the
		// host an unowned pawn reads !IsProxy — and ResumeControl un-dormanted a released prop, which then ran
		// on host input beside the player's real body (the "I control two props" bug).
		_possessionPending = false;

		// Scenery holds no microphone. This runs on the HOST, where the about-to-be-unowned pawn will read
		// !IsProxy — without the mute, the engine Voice on every released prop opens the host's mic (see
		// PlayerVoice.Muted). Machine-local on purpose: the ex-owner's copy becomes a proxy (never records)
		// and needs nothing.
		var voice = Components.Get<PlayerVoice>();
		if ( voice.IsValid() )
			voice.Muted = true;

		if ( EditMode )
			_session?.SetActive( false ); // forced teardown — no exit-confirm dialog, revert runs silently
	}

	// Fired by the engine on THIS machine when it stops being the pawn's controller — here, the host retired this
	// prop and dropped our ownership, so our copy just became a proxy. Tear down the control state that lives on
	// this machine: leave edit mode, which restores the camera depth-of-field + tears down the gizmo. Input, camera
	// and physics need nothing explicit — the IsProxy gating in OnUpdate/OnFixedUpdate already silences them. This
	// is why retire teardown rides the ownership event instead of the host poking at proxy state it can't reach.
	void IGameObjectNetworkEvents.StopControl()
	{
		if ( _session.IsValid() && _session.IsEditing )
			_session.SetActive( false );
	}

	// Released-and-claimable state deliberately does NOT live here: _dormant above is machine-local (no other
	// machine can tell a released prop from a live one), and a [Sync] flag on the pawn would have to be written
	// across the release's ownership edge. It's a registry on the host-owned claim service instead — see
	// PropClaims.ReleasedProps / IsReleased.

	// Set by BeginPossession, consumed in OnUpdate once ownership has actually replicated to us. A latch rather
	// than acting inside the RPC because the ownership packet and the RPC race: acting while we're still a proxy
	// would resume control of a pawn we can't drive yet.
	bool _possessionPending;

	/// <summary>Host→claimant (sent filtered to the new owner by <see cref="PropClaims"/>): you've been
	/// handed this released prop — resume control once the ownership change lands. Not IGameObjectNetworkEvents
	/// .StartControl, deliberately: that fires on every normal spawn (owner handoff) and on the HOST when a
	/// release's DropOwnership makes it the controller — both would wrongly resume a pawn.</summary>
	[Rpc.Broadcast]
	public void BeginPossession()
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return; // only the host hands out props
		_possessionPending = true;
	}

	/// <summary>The counterpart to <see cref="ReleaseControl"/>, run on the machine that just took ownership of
	/// a released prop: wake the control path and re-seed the state that went stale while it was scenery. Most
	/// control state self-heals off the dormant/proxy gates (_turnSeeded, Body.MotionEnabled, _pivotOffsetXY);
	/// what doesn't is the orbit rig (still aimed wherever this pawn's spawn left it) and the facing yaw (the
	/// body has been turned by physics since). The camera comes up along the possessing hunter's view — the E
	/// press captured it into <see cref="LobbySwapCarry"/> — so whatever you were looking at, you still are.</summary>
	public void ResumeControl()
	{
		_dormant = false;
		_woken = true; // somebody's wearing it now — see BornScenery
		_jumpQueued = false;
		ResetMover(); // scenery was parked by another machine's mover — start this one from rest

		// The claim this machine asked for has landed (we're driving the prop) — the swap keys may read E again
		// (PawnSwapKeys.LeavePressed stands down while a claim is pending, so the press that took us in can't
		// also take us out).
		PropClaims.ClearLocalClaimPending();

		// Somebody's driving again — give the voice back (a no-op on a claimant whose copy was never muted;
		// the mute only ever landed on the machine that ran the release).
		var voice = Components.Get<PlayerVoice>();
		if ( voice.IsValid() )
			voice.Muted = false;

		EyeAngles = WorldRotation.Angles() with { pitch = 0f, roll = 0f };
		_bodyYaw = EyeAngles.yaw;

		if ( _orbit.IsValid() )
		{
			_orbit.Angles = new Angles( 15f, _bodyYaw, 0f );
			if ( LobbySwapCarry.TakeCamera() is { } view )
				_orbit.Angles = new Angles( Math.Clamp( view.pitch, MinPitch, MaxPitch ), view.yaw, 0f );
			if ( LobbySwapCarry.PropZoom is { } zoom )
				_orbit.Distance = Math.Clamp( zoom, MinDistance, MaxDistance );
		}

		// The portrait starts from the angle we possessed it at — a re-claimed prop still carries its previous
		// owner's pose, and a converted one has none at all (it would render from the rig's default).
		CaptureIconPose();
		_iconPoseSeeded = true;

		// Straight into sculpting — the prompt that got us here said "E to Edit".
		if ( _session.IsValid() && !_session.IsEditing )
			_session.SetActive( true );
	}

	/// <summary>Open the sculpt editor on this (owned) pawn's own disguise — the charades manager drops the
	/// mimic straight into edit when their sculpt phase opens. A no-op on proxies or while already editing.</summary>
	internal void EnterEditing()
	{
		if ( IsProxy || _dormant )
			return;
		if ( _session.IsValid() && !_session.IsEditing )
		{
			SetFreeCam( false ); // the sculpt rig owns navigation in edit mode — don't fight it for the camera
			_session.SetActive( true );
		}
	}

	/// <summary>Force-close the sculpt editor — the phase moved on. Same silent forced teardown as
	/// <see cref="ReleaseControl"/>'s (no exit-confirm dialog).</summary>
	internal void ExitEditing()
	{
		if ( EditMode )
			_session?.SetActive( false );
	}

	// Yaw WASD is measured against = the camera (the rig's yaw); yaw the disguise visually faces = its own
	// (frozen during alt orbit).
	float MoveYaw => _orbit.Angles.yaw;
	float FacingYaw => _bodyYaw;

	/// <summary>The orbit camera's current zoom — read at a lobby swap press so the next prop pawn can come
	/// back at the same framing (see <see cref="LobbySwapCarry.PropZoom"/>).</summary>
	internal float OrbitDistance => _orbit.IsValid() ? _orbit.Distance : CameraDistance;

	protected override void OnAwake()
	{
		Body = GameObject.Components.GetOrCreate<Rigidbody>();
		Body.Gravity = false; // the mover integrates its own gravity so rise/fall can differ (snappy landings)
		Body.RigidbodyFlags = RigidbodyFlags.DisableCollisionSounds; // a prop is silent — no clatter scraping the world
		Body.MotionEnabled = false; // keyframed from birth: the mover moves it, nothing else may (see SetPhysicsMode)

		// Seed look from however the pawn was spawned, flattened (no starting pitch/roll).
		EyeAngles = WorldRotation.Angles() with { pitch = 0f, roll = 0f };

		// Voice chat rides the pawn — OnAwake so the owner has it before NetworkSpawn and it ships in the spawn
		// snapshot with a shared identity (see HunterController.OnAwake). A concealed infection-prep pawn isn't
		// networked yet, so a hiding prop's voice reaches nobody until Hunt puts the pawn on the wire.
		Components.GetOrCreate<PlayerVoice>();
	}

	protected override void OnStart()
	{
		_body = EnsureDisguiseBody();

		// Guarantee the disguise is solid regardless of how the body was made (prefab vs in-code fallback) — the
		// prefab might not carry an SdfCollider, and forcing a rebuild here doesn't depend on the clone's
		// OnEnabled timing. GetOrCreate + Rebuild is idempotent.
		if ( _body.IsValid() )
		{
			// Asserted on EVERY machine, before the collider is built: a proxy's disguise may have arrived in the
			// pawn's spawn snapshot (already tagged by its owner) or been cloned fresh here, and prop-vs-prop
			// contacts are only ignored when BOTH shapes carry the tag. Tags.Add is idempotent either way.
			// From here on UpdateReleasedSolidity keeps the tag tracking the released/body state per-frame.
			_body.GameObject.Tags.Add( PropBodyTag );

			_collider = _body.GameObject.Components.GetOrCreate<SdfCollider>();
			_collider.Rebuild();

			// Re-bind the underlying ModelCollider to THIS pawn's Rigidbody. A Collider binds to its ancestor
			// Rigidbody in OnEnabled, but the disguise's collider was first enabled mid-clone — before it was
			// parented under the pawn — so it bound to nothing and the dynamic body had no shape (and fell through
			// the world). Toggling it off→on now, while it's under the pawn, makes OnEnabled re-walk up and bind.
			var modelCollider = _body.GameObject.Components.Get<ModelCollider>();
			if ( modelCollider.IsValid() )
			{
				modelCollider.Enabled = false;
				modelCollider.Enabled = true;
			}
		}

		_bodyYaw = EyeAngles.yaw;

		// Stand up the shared orbit rig in follow mode, pointed at the disguise. It owns the camera for play AND
		// edit. Kept disabled as a component (it never runs its own OnUpdate) — we Tick it from UpdateCamera so the
		// ordering against our look input is deterministic and a proxy/dormant prop never drives the camera.
		_orbit = Components.GetOrCreate<OrbitCameraController>();
		_orbit.Enabled = false;
		_orbit.FollowTarget = _body.GameObject;
		_orbit.FollowOffset = Vector3.Up * CameraHeightOffset;
		_orbit.IgnoreCollision = GameObject; // boom ignores the pawn + its disguise, same as before
		_orbit.IgnoreCollisionTag = PropBodyTag; // …and fellow props, which our physics ignores too (see PropBodyTag)
		_orbit.MinDistance = MinDistance;
		_orbit.MaxDistance = MaxDistance;
		_orbit.ZoomSpeed = ZoomSpeed;
		_orbit.PanSpeed = PanSpeed;
		_orbit.MinPitch = MinPitch;
		_orbit.MaxPitch = MaxPitch;
		_orbit.Angles = new Angles( 15f, EyeAngles.yaw, 0f );
		_orbit.Distance = CameraDistance;

		// Lobby swap continuity — the split that makes a swap feel like returning to a parked body: the BODY
		// keeps its remembered facing (the spawn rotation, restored host-side from swap memory, seeded into
		// EyeAngles/_bodyYaw above — the cone still points where the prop pointed), while the CAMERA comes up
		// looking along the view we swapped from, at the zoom we last had as a prop. Both are owner-only
		// state (the host never knows our pitch or zoom); RosterIdOf so a host's bot prop can't eat the
		// carry meant for the host's own pawn, and the manager gate keeps it out of round spawns. Creative
		// swaps/conversions carry the view the same way (its P and E presses both Capture).
		if ( (LobbyManager.Current.IsValid() || CreativeManager.Current.IsValid())
			&& RoundManager.RosterIdOf( GameObject ) == Connection.Local?.Id )
		{
			if ( LobbySwapCarry.TakeCamera() is { } view )
				_orbit.Angles = new Angles( Math.Clamp( view.pitch, MinPitch, MaxPitch ), view.yaw, 0f );
			if ( LobbySwapCarry.PropZoom is { } zoom )
				_orbit.Distance = Math.Clamp( zoom, MinDistance, MaxDistance );
		}

		// Edit session + network sync, both bound to this machine's disguise — SculptablePawn keeps the
		// "session and sync always target the same sculpture" invariant in one place. No orbit rig handed to
		// the session: the hider's always-on rig above owns the camera for play AND edit. The disguise is a
		// local clone, so the target is wired here rather than in the prefab.
		_session = SculptablePawn.AttachEditing( this, _body );

		// Committed fires on discrete edits + gizmo release locally, and on an applied commit on proxies — never
		// mid-drag — so both sides rebase on the same settled shape states (see RecenterOriginOnShape).
		if ( _body.IsValid() )
			_body.Committed += RecenterOriginOnShape;

		// Where a fall respawn returns this body: the spawn placement (every mode hands the clone its spot as
		// the spawn transform, so recording it here needs no mode-specific wiring). See OnFixedUpdate.
		_fallAnchor = WorldTransform;
	}

	protected override void OnDestroy()
	{
		ReleaseHoldJoint();
		_scrapeHandle?.Stop();
		_hull.Dispose();
		if ( _body.IsValid() )
			_body.Committed -= RecenterOriginOnShape;
	}

	protected override void OnUpdate()
	{
		// FIRST, and before every gate below: these two exist to run on OTHER players' pawns too — concealment
		// (see UpdateConcealment) and the released-vs-body collision tag (see UpdateReleasedSolidity). Everything
		// after this point is owner-only.
		UpdateConcealment();
		UpdateReleasedSolidity();
		UpdateScrape();

		// A granted possession resumes here — before the dormant gate, because a HOST possessing a prop it
		// released itself still has _dormant latched on its own copy (ResumeControl clears it).
		if ( _possessionPending && !IsProxy )
		{
			_possessionPending = false;
			ResumeControl();
		}

		// Released into the level: no input, no camera — just let OnFixedUpdate keep the physics settling.
		if ( Scenery )
		{
			// BOT props are dormant by construction (RoundBots.Prepare releases the controls), but they're still
			// roster members the hunt is played against — so they whistle like any surviving prop. Without this
			// a solo playtest's hunt was SILENT, which read as "taunts are broken". Host-side only (bot pawns
			// are host-owned; every other machine sees them as proxies and gets the whistle through the
			// broadcast). Released SCENERY is dormant too and must never whistle — it has no BotPawn row.
			// Manual is withheld: the host's T key belongs to the host's own pawn, not the whole bot flock.
			if ( !IsProxy && Components.Get<BotPawn>() is not null )
				UpdateTaunts( manual: false );

			return;
		}

		// Only the owning client reads input + drives the ONE shared scene camera. On every other machine this pawn
		// is a proxy: the engine moves its body from the networked transform, and the local player's own pawn owns
		// the camera — so a proxy must not touch input or the camera (two props would otherwise fight over it).
		// Gate LIVE each frame: ownership replicates after OnStart, so a one-shot read would be stale (same reason
		// HunterController gates live). The streamed disguise shape is applied by SdfNetworkSync, not here.
		if ( IsProxy )
			return;

		// The roster portrait starts from the angle we START as this prop — the camera we came in with, not the
		// rig's default — until the first edit exit replaces it with the angle the shape was signed off from.
		// First owned frame, after OnStart/ResumeControl have aimed the orbit (swap carry included).
		if ( !_iconPoseSeeded )
		{
			CaptureIconPose();
			_iconPoseSeeded = true;
		}

		// Read the toggles even while control is suspended, so edit mode can be exited. Charades LOCKS the
		// mimic's editor outside the sculpt phase (no sculpting before the word is chosen or after the
		// reveal) — exiting is always allowed, only opening is gated.
		if ( Input.Pressed( "Edit" ) )
		{
			if ( EditMode || !CharadesManager.EditLockedFor( this ) )
			{
				SetFreeCam( false ); // the sculpt rig owns navigation in edit mode — don't fight it for the camera
				_session?.Toggle();
			}
		}
		if ( Input.Pressed( "ToggleWireframes" ) )
			_session?.ToggleWireframes();

		// Keep the wire in step with the session, whatever path entered or left edit mode (see NetEditing) —
		// and on the way OUT, stamp the camera's angle as the icon portrait pose: the view the player was
		// looking from when they signed the shape off is the best angle we'll ever get for it.
		NetEditing = EditMode;
		if ( _editWas && !EditMode )
			CaptureIconPose();
		_editWas = EditMode;

		UpdateTaunts();

		if ( ControlActive && Input.Pressed( "jump" ) )
		{
			_jumpQueued = true;
			_sinceJumpPress = 0f; // also an air-grab request (see HiderController.Climb)
		}

		// Shift lets go of a climb — latched here so a quick tap can't fall between fixed ticks.
		if ( _climbing && ControlActive && Input.Pressed( "run" ) )
			_letGoQueued = true;

		// Free cam toggle — blocked while editing (the sculpt rig already owns navigation there) or while a
		// cursor-owning panel is open (same guard PlayCamera uses, so F doesn't fire behind a menu). Only where
		// it's part of the game (FreeCamAllowed); dropped on the spot if we stop being in one.
		if ( _freeCam && !FreeCamAllowed )
			SetFreeCam( false );
		else if ( FreeCamAllowed && !EditMode && !PauseMenu.IsOpen && !RoundSetup.IsOpen && !CharadesManager.LocalPicking && Input.Keyboard.Pressed( "F" ) )
			SetFreeCam( !_freeCam );

		// Always drive the camera (per-frame, for smoothness) — needed during edit mode too, where movement is frozen.
		if ( _freeCam )
			UpdateFreeCam();
		else
			UpdateCamera();

		// Drawn per frame (not in the fixed step) so it tracks the interpolated body, edit mode included.
		if ( DebugMovement || SdfCollisionDebug.MoveHull )
			DrawMovement();
		if ( DebugCameraPivot )
			DrawCameraPivot(); // after UpdateCamera, so it shows this frame's pivot (override included)
	}

	// Owner-only, on the edit-exit edge. World camera angle → disguise-local, so the synced pose stays
	// meaningful however the pawn moves afterwards. Roll is whatever the orbit rig held (always 0).
	void CaptureIconPose()
	{
		if ( !_body.IsValid() || !_orbit.IsValid() )
			return;

		IconPose = (_body.WorldRotation.Inverse * _orbit.Angles.ToRotation()).Angles();
		IconPoseSet = true;
	}

	// ── Concealment ───────────────────────────────────────────────────────────────────────────────────
	/// <summary>True when this prop is concealed FROM US: somebody else's prop — a proxy, or a host-held bot
	/// body, which is neither a proxy nor ours — while <see cref="RoundManager.PropsConcealed"/> says props
	/// are hidden from what we're playing (Infection: a fellow prop before the Reveal; Teams: any prop on a
	/// hunter's machine during prep). Never true for our own pawn — concealment is about what OTHER machines
	/// perceive.</summary>
	public bool Concealed => (IsProxy || RoundManager.IsBotPawn( GameObject )) && RoundManager.PropsConcealed;

	// Keep other players' props out of our world until the Reveal. Asserted every frame on every machine and
	// unconditionally, not on change: GameObject.Enabled is serialized, so a spawn snapshot or a network refresh
	// can hand us the owner's (enabled) state at any moment, and a "only when it changes" guard would decide it
	// had already applied concealment and never restore it. Writing a bool to the value it already holds is free.
	//
	// It switches the whole DISGUISE BRANCH off rather than flipping renderer flags, which is the lesson from the
	// hunter's equivalent (HunterController.UpdateConcealment): RenderHidden on an SdfRaymarchRenderer means
	// SHADOWS-ONLY, so a prop "hidden" that way would still lay its silhouette across the floor in plain sight.
	// Disabling the branch takes the raymarch, its shadow, the sibling mesh, the collider and the through-wall
	// outline together, and costs nothing while concealed (no field bakes, no physics shapes). It's also the only
	// safe way down: disabling the raymarch renderer ALONE hands the sibling mesh back as a visible one
	// (SdfRaymarchRenderer.RestoreSiblingMeshDeferred) — which that method skips only because the branch is
	// inactive, exactly as it is here.
	//
	// The streamed shape keeps arriving while we're concealing it: SdfNetworkSync sits on the pawn ROOT, so it
	// stays live and writes the sculpture's brushes regardless of this branch, and re-enabling rebuilds from them
	// (the renderer clears its change-hash in OnEnabled, the collider rebuilds on its own). A prop shown at the
	// Reveal is therefore its final sculpted shape, not whatever it wore when we stopped drawing it.
	//
	// NOT covered: sound. A concealed prop's taunt whistle and voice still play from where it's hiding — the same
	// accepted leak as a hidden prop's speech (see PlayerNameplates), and the loudest hint props get about each
	// other. Gate BroadcastTaunt on this if that turns out to be too much.
	void UpdateConcealment()
	{
		if ( !_body.IsValid() )
			return;

		bool conceal = Concealed;
		var branch = _body.GameObject;
		if ( branch.IsValid() && branch.Enabled == conceal )
			branch.Enabled = !conceal;
	}

	// The propbody tag tracks "this disguise is someone's BODY" (see PropBodyTag): released scenery must shed it
	// so player props collide with it (and can stand on it — the mover's sweeps skip the tag). Asserted per-frame on
	// EVERY machine from PropClaims.IsReleased — the one released-state signal that provably replicates — rather
	// than written at the release/claim seams: those run host-side across an ownership edge, and the contacts
	// that matter happen on OTHER machines (each machine simulates its own dynamic pawn against its local copy
	// of this prop). Same assert-everywhere shape as UpdateConcealment above. Modes with no claim service never
	// register a release (bots and DebugGameMode retirees included), so there the tag simply stays on.
	void UpdateReleasedSolidity()
	{
		if ( !_body.IsValid() )
			return;

		bool isBody = !PropClaims.IsReleased( this );
		var tags = _body.GameObject.Tags;
		if ( tags.Has( PropBodyTag ) != isBody )
			tags.Set( PropBodyTag, isBody );
	}

	// ── Taunts ────────────────────────────────────────────────────────────────────────────────────────
	// During the Hunt every surviving prop periodically whistles from wherever it's hiding — the classic
	// prop-hunt tension dial, tuned by the host (RoundSettings.TauntSeconds). T taunts on demand (and resets
	// the clock, so a manual whistle buys quiet until the next auto one). Owner-side only: this runs after
	// OnUpdate's dormant/IsProxy gates, and the sound reaches everyone through the broadcast RPC below. Any
	// pawn still wearing a HiderController during the Hunt IS a live prop (a found prop respawns as a hunter),
	// so no per-frame roster lookup is needed.

	const string TauntSoundPath = "sounds/game/tauntwhistyle.sound";

	/// <summary>Extra seconds a manual (T) taunt must wait after ANY taunt, so the key can't be drummed into a
	/// continuous whistle.</summary>
	const float ManualTauntCooldown = 2f;

	TimeUntil _nextAutoTaunt;
	RealTimeSince _sinceTaunt;
	bool _tauntClockSeeded; // seeded on the first Hunt frame; reset outside the Hunt so a next round re-seeds

	void UpdateTaunts( bool manual = true )
	{
		var round = RoundManager.Current;
		if ( !round.IsValid() || round.Phase != RoundPhase.Hunt )
		{
			_tauntClockSeeded = false;
			return;
		}

		// 0 = the host chose "None": no automatic whistling at all (manual T still works — taunting on purpose
		// is the prop's own risk to take). Anything else keeps the same 5s floor as LobbyManager.SetTauntSeconds,
		// which also catches a sub-floor value from any stale settings path (e.g. an old serialized struct) that
		// Max(0) would turn into per-frame whistling.
		float interval = round.Settings.TauntSeconds <= 0f ? 0f : MathF.Max( 5f, round.Settings.TauntSeconds );

		// First Hunt frame: every prop starts its clock at a RANDOM fraction of the interval. All machines flip
		// into the Hunt within a frame or two of each other, so without this offset the whole lobby would
		// whistle in chorus every cycle — the random phase is the stagger.
		if ( !_tauntClockSeeded )
		{
			_tauntClockSeeded = true;
			// A manual taunt is allowed immediately at the whistle-phase start (with auto taunts off there's no
			// interval to wait out, so just clear the manual cooldown).
			_sinceTaunt = MathF.Max( interval, ManualTauntCooldown );
			if ( interval > 0f )
				_nextAutoTaunt = Game.Random.Float( 0.3f, 1f ) * interval;
		}

		if ( manual && Input.Pressed( "Taunt" ) && _sinceTaunt > ManualTauntCooldown )
			Taunt( interval );
		else if ( interval > 0f && _nextAutoTaunt <= 0f )
			Taunt( interval );
	}

	// Fire one taunt and rewind the auto clock — jittered ±15% so two props whose clocks happened to land in
	// step drift apart again instead of whistling together for the whole hunt. With auto taunts off (interval 0)
	// the auto clock is left alone — the `interval > 0` gate above never reads it.
	void Taunt( float interval )
	{
		_sinceTaunt = 0f;
		if ( interval > 0f )
			_nextAutoTaunt = interval * Game.Random.Float( 0.85f, 1.15f );
		BroadcastTaunt();
	}

	// The whistle, on every machine, from wherever this machine sees the prop (no position argument: proxies
	// place it from their network-interpolated pawn transform, which is exactly where they see the disguise).
	// The pawn is networked for the whole Hunt in every mode, so the broadcast always reaches everyone.
	[Rpc.Broadcast]
	void BroadcastTaunt()
	{
		Sound.Play( TauntSoundPath, WorldPosition + Vector3.Up * 16f );
	}

	protected override void OnFixedUpdate()
	{
		// Proxy: the engine drives this body from the networked transform (see Rigidbody.ShouldSimulatePhysics —
		// a synced proxy is moved, not simulated). Skip the mover entirely for a body we're not simulating.
		if ( IsProxy )
			return;

		WatchMoveJumps();

		// Fell out of the world (blockout floor holes, physics tunnelling): put the body back at its spawn
		// spot, upright and at rest — keeping its current heading so the player's camera relation survives.
		// After the proxy gate so it runs exactly on the machine simulating this body: the owner, or the
		// host for its bots and released scenery props. Dormant/editing states included on purpose — a
		// scenery prop that escapes the map should come back too.
		ApplyClayMass();

		// Grabbed (creative's pickup — see PropClaims.Grab): chase the holder's pose, free to rotate, no gravity.
		// Ahead of everything else: a held prop is nobody's body and nobody's sculpt.
		Transform held = default;
		bool isHeld = Scenery && PropClaims.Current.IsValid() && PropClaims.Current.TryGetGrabTarget( this, out held );

		// Frozen from a grab (LMB while holding): exactly where it was let go, with motion OFF — solid, but no
		// physics at all (no gravity, nothing can push it). Ahead of the fall check and the motion recovery
		// below, both of which would otherwise move or re-enable it. A new grab unfreezes it (the registry row
		// goes on the grant), so a held prop never reads frozen.
		if ( !isHeld && Scenery && PropClaims.IsFrozen( this ) )
		{
			SetPhysicsMode( PhysicsMode.Frozen );
			return;
		}

		if ( FallRespawn.Fell( WorldPosition.z, _fallAnchor.Position.z ) )
		{
			WorldPosition = _fallAnchor.Position;
			WorldRotation = Rotation.FromYaw( WorldRotation.Angles().yaw );
			if ( Body.MotionEnabled )
			{
				Body.Velocity = Vector3.Zero;
				Body.AngularVelocity = Vector3.Zero;
			}
			ResetMover();
			return; // let the teleport land before running movement from the new spot
		}

		if ( isHeld )
		{
			SetPhysicsMode( PhysicsMode.Held );
			DriveHeld( held );
			return;
		}

		// Dropped from a grab: a FREE rigidbody — engine gravity, every axis unlocked, the engine's own sleep.
		// The mover doesn't run. An in-place sculpt still pins it below.
		if ( Scenery && PropClaims.IsLoose( this ) && !BeingEdited )
		{
			SetPhysicsMode( PhysicsMode.Loose );
			return;
		}

		// Stuck to a wall or ceiling (see HiderController.Climb). Edit mode and control locks just hold on; only
		// becoming scenery lets go.
		if ( _climbing )
		{
			if ( Scenery )
			{
				EndClimb();
			}
			else
			{
				StepClimb( controlled: ControlActive );
				return;
			}
		}

		// Pressed into a wall: physics is turning it flush (see HiderController.Press). Only while it's someone's
		// body under control — anything else hands it straight back to the mover.
		if ( _pressing )
		{
			if ( Scenery || !ControlActive )
			{
				EndPress();
			}
			else
			{
				IsCrouching = Input.Down( "duck" );
				StepPress();
				return;
			}
		}

		SetPhysicsMode( PhysicsMode.Character );

		if ( Scenery )
		{
			// Being sculpted in place (see BeingEdited) — the host simulates it, PINNED: it hangs exactly where
			// the sculptors found it (a converted door knob stays on its door) for as long as anyone is on it.
			// Normal settling resumes the moment the last editor leaves.
			if ( BeingEdited )
			{
				ResetMover();
				return;
			}

			// Released into the level (dormant), and bots: no input — the mover settles it onto the ground and
			// then PARKS it, after which it costs nothing per tick until its shape or its ground changes.
			StepMover( controlled: false );
			return;
		}

		// No locomotion input when editing (control suspended): the mover still runs, so an editing prop
		// re-settles onto its shape as soon as a commit rebuilds collision.
		if ( !ControlActive )
		{
			StepMover( controlled: false );
			return;
		}

		IsCrouching = Input.Down( "duck" ); // crouch is the slow, fine-positioning speed (a prop's shape never changes)
		StepMover( controlled: true );
	}

	// ── Grab / loose physics (see PropClaims.Grab) ─────────────────────────────────────────────────────────
	// The body runs in one of four modes. CHARACTER is a KEYFRAMED body driven by the mover (see
	// HiderController.Movement): nothing pushes it. HELD chases the grab's target pose with velocities — all axes
	// free, no gravity, still colliding on the way. LOOSE is a plain dynamic rigidbody: engine gravity, every axis
	// free, the engine's own sleep. FROZEN is keyframed too, and simply never moved. Motion is asserted every call
	// (a compare); everything else only on a change.

	// The body weighs what the clay weighs (SdfCollider.ClayMass), not what its overlapping collision shapes add
	// up to. Re-asserted every tick on the simulating machine: the collider rebuilds on commit without telling
	// us, and the engine recomputes mass from the shapes whenever the collider rebinds. One float compare when
	// nothing changed. Mass only shows in Loose and Held (the character step integrates its own motion), but a
	// steady value everywhere is cheaper than tracking mode changes.
	void ApplyClayMass()
	{
		if ( !Body.IsValid() || !_collider.IsValid() )
			return;
		float mass = _collider.ClayMass;
		if ( mass > 0f && MathF.Abs( Body.MassOverride - mass ) > 1e-3f )
			Body.MassOverride = mass;
	}

	// Pressing = a character body pushed into a wall, briefly dynamic so contacts turn it flush (see
	// HiderController.Press). Climbing = stuck to a wall or ceiling, dynamic with no gravity; ClimbHold = the same,
	// held still (keyframed) while there's no input (see HiderController.Climb).
	enum PhysicsMode { Character, Held, Loose, Frozen, Pressing, Climbing, ClimbHold }
	PhysicsMode _physicsMode = PhysicsMode.Character;

	// Hold feel: how hard the prop is pulled toward the target (1/s) and the speed cap that keeps a fast swing
	// from tunnelling it through a thin wall.
	const float HoldPull = 18f;
	const float HoldTurn = 14f;
	const float HoldMaxSpeed = 1800f;

	void SetPhysicsMode( PhysicsMode mode )
	{
		if ( !Body.IsValid() )
			return;

		// Dynamic only as an object (held / loose) or while pressed into a wall. Asserted every call, not just on a
		// change: the field starts at Character, which no change ever announces, and nothing else may leave a
		// character body dynamic.
		bool dynamic = mode is PhysicsMode.Held or PhysicsMode.Loose or PhysicsMode.Pressing or PhysicsMode.Climbing;
		if ( Body.MotionEnabled != dynamic )
		{
			if ( dynamic )
			{
				Body.MotionEnabled = true;
				Body.Velocity = _velocity; // leave the character step at the speed it was going
				Body.AngularVelocity = Vector3.Zero;
			}
			else
			{
				// Into Character, keep an object's momentum (a flung prop possessed mid-air keeps flying).
				_velocity = mode == PhysicsMode.Character ? Body.Velocity : Vector3.Zero;
				Body.MotionEnabled = false; // keyframed (this clears the body's own velocity)
			}
		}

		if ( _physicsMode == mode )
			return;

		_physicsMode = mode;
		if ( mode != PhysicsMode.Held )
			ReleaseHoldJoint();
		bool againstSurface = mode is PhysicsMode.Pressing or PhysicsMode.Climbing;
		if ( mode is not PhysicsMode.Held && !againstSurface )
			RestoreHeldFriction(); // pressing and climbing slide frictionless like a held prop
		ApplyPressDamping( againstSurface );
		UseHullCollision( againstSurface ); // press and climb collide as the mover sweeps (see AlignWithMoveHull)

		// Pressed or climbing, a body turns only about the vertical — surfaces lay it flush, they never tip it over.
		Body.Locking = againstSurface ? new PhysicsLock { Pitch = true, Roll = true } : default;
		Body.Gravity = mode is PhysicsMode.Loose or PhysicsMode.Pressing; // a climber's pull replaces gravity

		// Continuous collision while it moves as an OBJECT — carried (the hold can swing it fast) or loose (falling,
		// tumbling, flung). Without it a step longer than the prop's own thickness passes straight through thin
		// geometry: a lemon slice falling off a counter tunnelled through shelves. It only costs anything when a
		// body moves far relative to its size, so it's on for every loose prop rather than gated by size. Off for
		// a character body (its own steps never move fast enough) and a frozen one (it doesn't move).
		Body.EnhancedCcd = mode is PhysicsMode.Held or PhysicsMode.Loose or PhysicsMode.Pressing or PhysicsMode.Climbing; // pressed into walls, a body is one bad tick from tunnelling

		// Persisting-contact events while held or loose: the hold switches to the velocity chase while touching
		// something, and both squelch when sliding (see HiderController.Impacts).
		Body.CollisionUpdateEventsEnabled = mode is PhysicsMode.Held or PhysicsMode.Loose;

		if ( dynamic && Body.Sleeping )
			Body.Sleeping = false;

		// Back to being a character: forget the old ground and settle from wherever the object left it.
		if ( mode == PhysicsMode.Character )
		{
			IsGrounded = false;
			_groundObject = null;
			Unpark();
		}
		_turnSeeded = false;
	}

	/// <summary>Hold a grabbed prop with a control joint (the sandbox physgun's way) instead of overwriting its
	/// velocity every tick. Off = the old velocity chase, kept for A/B-ing the feel.</summary>
	[ConVar( "mimi_grab_joint", Help = "Grabbed props: 1 = spring joint hold (sandbox physgun), 0 = velocity chase." )]
	public static bool GrabJoint { get; set; } = true;

	// The joint hold, as sandbox's PhysicsHold: a keyframed anchor body we teleport to the target each tick, tied
	// to the prop's mass centre by a control joint with linear + angular springs. The springs are solved INSIDE
	// the physics step alongside the contacts, so a prop pressed into a wall settles against it instead of the
	// solver and our per-tick velocity write fighting (the jitter); the force is capped by the prop's mass, so a
	// heavy one swings with weight; and the stepwise targets a client streams are smoothed by the spring.
	// Force ≤ mass × this (torque scaled from the inertia). Sandbox uses 700000; that out-pulls the contacts, so
	// a fast swing dragged a small prop straight through a 2u wall. Measured on that wall: 80000 tunnels, 40000
	// and 30000 hold — and 30000 still settles a 90° swing in 0.2s, no slower than 700000.
	const float HoldMaxAccel = 30000f;
	const float HoldLinearHz = 32f;
	const float HoldAngularHz = 64f;
	const float HoldDamping = 4f;

	// The joint is for FREE AIR only. The moment the held prop touches something (a contact event within
	// HoldContactWindow) it drops the joint and goes back to the velocity chase — which is what lays a prop
	// FLUSH when pressed into a surface and SLIDES it when dragged. Rewriting the velocity every tick pushes
	// through the contact solver in a way a capped spring can't copy. Measured (pot tilted 30°, pressed into
	// the floor): chase → 1°; joint with a soft rotation spring → 0° but dragged props ROLL (knob 41–47° off);
	// joint with the rotation chase only → stuck at 26°, at any linear stiffness. Free air keeps the joint's
	// wins: fast swings, no wall tunnelling, the client's stepwise targets smoothed.
	const float HoldContactWindow = 0.1f;  // seconds since the last contact event that still count as touching

	Sandbox.Physics.ControlJoint _holdJoint;
	PhysicsBody _holdAnchor;
	PhysicsBody _holdOn;        // the body the joint was made on — a re-made body needs a new joint
	Vector3 _holdMassCentre;    // its local mass centre then — a scale commit moves it, so the joint is remade

	void ReleaseHoldJoint()
	{
		_holdJoint?.Remove();
		_holdJoint = null;
		if ( _holdAnchor is not null && _holdAnchor.IsValid() )
			_holdAnchor.Remove();
		_holdAnchor = null;
		_holdOn = null;
	}

	void DriveHeldJoint( Transform target )
	{
		var pb = Body.PhysicsBody;
		if ( pb is null || !pb.IsValid() )
			return;

		if ( _holdJoint is null || _holdOn != pb || !_holdMassCentre.AlmostEqual( pb.LocalMassCenter, 0.01f ) )
		{
			ReleaseHoldJoint();
			_holdOn = pb;
			_holdMassCentre = pb.LocalMassCenter;
			_holdAnchor = new PhysicsBody( pb.World ) { BodyType = PhysicsBodyType.Keyframed, AutoSleep = false };
			_holdAnchor.Transform = new Transform( WorldTransform.WithScale( 1f ).PointToWorld( _holdMassCentre ), WorldRotation );

			var inertia = pb.Inertia;
			float maxForce = pb.Mass * HoldMaxAccel;
			float maxTorque = HoldMaxAccel * MathF.Sqrt( pb.Mass * MathF.Max( inertia.x, MathF.Max( inertia.y, inertia.z ) ) );

			_holdJoint = Sandbox.Physics.PhysicsJoint.CreateControl(
				new Sandbox.Physics.PhysicsPoint( _holdAnchor ), new Sandbox.Physics.PhysicsPoint( pb, _holdMassCentre ) );
			_holdJoint.LinearSpring = new Sandbox.Physics.PhysicsSpring( HoldLinearHz, HoldDamping, maxForce );
			_holdJoint.AngularSpring = new Sandbox.Physics.PhysicsSpring( HoldAngularHz, HoldDamping, maxTorque );
		}

		// The target is the ROOT pose; the joint holds the mass centre, so the anchor goes where that lands — at
		// most HoldMaxSpeed units/s (the old chase's cap) rather than jumping there. A fast swing can put the
		// target behind a wall the aim sweep never saw (it sweeps along the aim, not along the prop's path); the
		// joint then pulls the prop at it, and only the HoldMaxAccel cap keeps the contact winning.
		var dt = MathF.Max( Time.Delta, 0.001f );
		var want = target.PointToWorld( _holdMassCentre );
		var from = _holdAnchor.Position;
		_holdAnchor.Transform = new Transform( from + (want - from).ClampLength( HoldMaxSpeed * dt ), target.Rotation );
	}

	// A held prop has NO friction: it slides over whatever it's pressed against or dragged across instead of
	// gripping it. Every collider's own override is remembered and put back when the hold ends (a scale
	// commit's rebuilt collider is a new component, picked up on the next tick).
	const float HeldFriction = 0f;
	readonly Dictionary<Collider, float?> _heldFriction = new();

	void ApplyHeldFriction()
	{
		foreach ( var c in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( _heldFriction.ContainsKey( c ) )
				continue;
			_heldFriction[c] = c.Friction;
			c.Friction = HeldFriction;
		}
	}

	void RestoreHeldFriction()
	{
		foreach ( var (c, friction) in _heldFriction )
		{
			if ( c.IsValid() )
				c.Friction = friction;
		}
		_heldFriction.Clear();
	}

	// Host (the simulating machine): steer the body toward the holder's pose — through the joint in free air,
	// with velocities while touching (see HoldContactWindow). Never teleports, so the solver still stops it at
	// walls and it leaves the hand with whatever motion it had. Leaving contact remakes the joint at the body's
	// current pose, so the hand-over doesn't kick.
	void DriveHeld( Transform target )
	{
		if ( Body.Sleeping )
			Body.Sleeping = false;

		ApplyHeldFriction();

		// (No scale here: the grab scales the SHAPE, host-side — see PropClaims.Grab.)
		if ( GrabJoint && _sinceHeldContact >= HoldContactWindow )
		{
			DriveHeldJoint( target );
			return;
		}
		ReleaseHoldJoint();

		var dt = MathF.Max( Time.Delta, 0.001f );
		var toTarget = target.Position - WorldPosition;
		Body.Velocity = (toTarget * MathF.Min( HoldPull, 1f / dt )).ClampLength( HoldMaxSpeed );
		ChaseHeldRotation( target.Rotation, dt );
	}

	// Turn the body toward the target rotation by writing its angular velocity: shortest arc, at HoldTurn per
	// second. The velocity hold's rotation, and the joint hold's while touching (see HoldContactWindow).
	void ChaseHeldRotation( Rotation target, float dt )
	{
		var q = target * WorldRotation.Inverse;
		if ( q.w < 0f )
			q = new Rotation( -q.x, -q.y, -q.z, -q.w );
		var sinHalf = MathF.Sqrt( q.x * q.x + q.y * q.y + q.z * q.z );
		if ( sinHalf < 1e-5f )
		{
			Body.AngularVelocity = Vector3.Zero;
			return;
		}

		var angle = 2f * MathF.Atan2( sinHalf, q.w );
		var axis = new Vector3( q.x, q.y, q.z ) / sinHalf;
		Body.AngularVelocity = axis * angle * MathF.Min( HoldTurn, 1f / dt );
	}

	/// <summary>Host-only, as a loose prop is possessed: stand the ROOT back up (yaw kept) and move the tumble
	/// onto the Disguise, so the clay stays exactly where it lay — the shape never moves on its own — while the
	/// body becomes an upright character again; its move hull is re-derived from the new disguise placement on
	/// the next mover tick (see <see cref="PropMoveHull"/>).</summary>
	internal void SettleUpright()
	{
		var disguise = _body.IsValid() ? _body.GameObject : null;
		var keep = disguise.IsValid() ? disguise.WorldTransform : default;

		WorldRotation = Rotation.FromYaw( WorldRotation.Angles().yaw );
		if ( disguise.IsValid() )
			disguise.WorldTransform = keep;

		if ( Body.IsValid() )
		{
			Body.Velocity = Vector3.Zero;
			Body.AngularVelocity = Vector3.Zero;
		}
		SetPhysicsMode( PhysicsMode.Character );
	}

	// The leash rule on one velocity-shaped vector (see LeashCentre). Horizontal only — Z passes straight
	// through. Measured on the physics bounds: the bounds CENTRE against the radius less the bounds' flat
	// half-diagonal, so the circumscribed circle of the collider's footprint is what stays inside the line.
	// At or beyond that the outward radial component is removed (tangential motion kept); with pullIn, a
	// body already outside also gets an inward component proportional to the overshoot, so a shove — or a
	// collider rebuilt wider by an undo — eases it back without a snap. Inside the circle it's a no-op.
	Vector3 ApplyLeash( Vector3 v, bool pullIn )
	{
		if ( LeashCentre is not { } centre )
			return v;

		var bounds = Body.GetWorldBounds();
		float reach = MathF.Max( LeashRadius - bounds.Extents.WithZ( 0f ).Length, 0f );
		var flat = (bounds.Center - centre).WithZ( 0f );
		float dist = flat.Length;
		if ( dist < reach || dist < 0.001f )
			return v;

		var outward = flat / dist;
		float radial = Vector3.Dot( v.WithZ( 0f ), outward );
		if ( radial > 0f )
			v -= outward * radial;

		if ( pullIn && dist > reach )
		{
			float pull = MathF.Min( (dist - reach) * LeashPullRate, LeashPullMax );
			v -= outward * pull;
		}

		return v;
	}

	// Smoothed X/Y of the shape's bounds centre in the BODY'S LOCAL frame (null = pin off). Smoothing locally
	// — not the world point — keeps the camera glued to the body while it moves AND turns; only edits ease.
	Vector2? _pivotOffsetXY;

	// Draw the orbit pivot (magenta sphere + vertical line) so you can see exactly what the camera orbits.
	void DrawCameraPivot()
	{
		var p = _orbit.Pivot;
		Scene.DebugOverlay.Sphere( new Sphere( p, 2f ), Color.Magenta, 0f );
		Scene.DebugOverlay.Line( p - Vector3.Up * 24f, p + Vector3.Up * 24f, Color.Magenta.WithAlpha( 0.5f ), 0f );
	}

	// ── Camera ────────────────────────────────────────────────────────────────────────────────────────
	// Both modes are served by the shared orbit rig; the only hider-specific concern is whether the disguise body
	// turns with the camera (play) or stays frozen (edit, while sculpting).
	void UpdateCamera()
	{
		_orbit.BoomCollision = CameraCollision; // mirrored every frame so the inspector toggle takes effect live

		// Pin the pivot's X/Y to the shape's centre (the rig keeps the height + pan). The smoothing happens in
		// the BODY'S LOCAL frame: the local bounds centre only changes when the sculpt is edited, so walking
		// AND turning feed through raw (camera stays glued while moving/rotating) and only edits ease in
		// softly. Recomputed per frame; cleared when toggled off so the rig falls back to plain follow.
		if ( CenterPivotOnShape && _body.IsValid() && Sdf.TryGetBounds( _body.Brushes, out var b, SculptEditSession.PendingStamp( _body ) ) )
		{
			var targetLocal = new Vector2( b.Center.x, b.Center.y );
			_pivotOffsetXY = _pivotOffsetXY is { } current
				? Vector2.Lerp( current, targetLocal, 1f - MathF.Exp( -PivotSmoothSpeed * Time.Delta ) )
				: targetLocal; // first frame (spawn / toggled back on): start on target, no glide from stale state

			// Rotate the smoothed local offset out by the body's LIVE transform. At the bounds' own height, not
			// local zero: a prop lying on its side (flush alignment tilts the root) would otherwise pin the
			// camera to a point beside the shape.
			var local = _pivotOffsetXY.Value;
			var world = _body.WorldTransform.PointToWorld( new Vector3( local.x, local.y, b.Center.z ) );
			_orbit.PivotXYOverride = new Vector2( world.x, world.y );
		}
		else
		{
			_pivotOffsetXY = null;
			_orbit.PivotXYOverride = null;
		}

		if ( EditMode )
		{
			// Edit: the rig reads create-mode navigation itself (orbit/dolly/pan + the dot cursor). We leave _bodyYaw alone,
			// so the disguise stays put and re-settles onto its shape in place while we sculpt.
			_orbit.Tick( handleAltDrag: true );

			// The rig just asserted EditFov through MainCamera's own ease; drop our blend so leaving edit
			// re-seeds it from the live camera instead of popping back to the stale play-camera value.
			_fovBlendSeeded = false;
		}
		else if ( CharadesManager.LocalPicking )
		{
			// The charades mimic picking a phrase: the cursor is out for the centre buttons (PauseMenuSystem), so
			// look around the stage the edit-mode way — the rig's own click-drag nav (LMB orbit / RMB dolly / MMB
			// pan, a plain click is a tap that never orbits) — while the body holds still. Keeps the PLAY fov, so
			// the view doesn't zoom as the pick opens and closes.
			_orbit.Tick( handleAltDrag: true );
			ApplySmoothFov( GameSettings.PropPlayFov );
		}
		else
		{
			PlayCamera();                        // free-look + bare drag nav feed the rig; may turn the disguise too
			_orbit.Tick( handleAltDrag: false );

			// The play camera's FOV (GameSettings.PropPlayFov) overrides the rig's EditFov. Keep
			// easing the SAME blend here too — otherwise leaving free cam would hand off from our custom glide
			// to MainCamera's own (differently-paced) shared lerp mid-transition, reading as a speed change
			// partway through instead of one continuous ease.
			ApplySmoothFov( GameSettings.PropPlayFov );
		}
	}

	// Free cam: detaches the SAME orbit rig from the body (FollowTarget -> null, so Tick treats Pivot as a free
	// world-space point instead of "body position + offset") and flies that pivot around under raw WASD/mouse-look,
	// with Distance forced to 0 so the camera sits exactly AT the pivot rather than orbiting it — turning the
	// rig into a plain noclip flycam for as long as this runs. SetFreeCam below does the actual attach/detach
	// exactly once, at the toggle edge — not every frame — so this only ever handles movement/look.
	void UpdateFreeCam()
	{
		if ( PauseMenu.IsOpen || RoundSetup.IsOpen )
			return;

		Mouse.Visibility = MouseVisibility.Hidden;

		_orbit.ApplyLook( Input.AnalogLook );

		var move = Vector3.Zero;
		if ( Input.Down( "forward" ) ) move += Vector3.Forward;
		if ( Input.Down( "backward" ) ) move += Vector3.Backward;
		if ( Input.Down( "left" ) ) move += Vector3.Left;
		if ( Input.Down( "right" ) ) move += Vector3.Right;

		var speed = FreeCamSpeed * (Input.Down( "run" ) ? FreeCamFastMultiplier : 1f);

		// Horizontal + pitch fly relative to view (look up, fly up); vertical (jump/duck) is world-space on top —
		// the standard noclip/spectator feel, matching FlyCamera.cs's own Q/E-for-down/up convention.
		var wish = Vector3.Zero;
		if ( move.LengthSquared > 0.001f )
			wish += _orbit.Angles.ToRotation() * move.Normal;
		if ( Input.Down( "jump" ) ) wish += Vector3.Up;
		if ( Input.Down( "duck" ) ) wish += Vector3.Down;

		// Momentum: the keys steer a WISH velocity; the actual velocity eases toward it (editor fly-cam
		// feel — accelerate in, glide to a stop). The time constant is a third of FreeCamSmoothing so the
		// ramp mostly completes within that many seconds; 0 = instant, exactly the old behaviour.
		var wishVel = wish * speed;
		if ( FreeCamSmoothing > 0.001f )
			_freeCamVelocity = Vector3.Lerp( _freeCamVelocity, wishVel, 1f - MathF.Exp( -Time.Delta * 3f / FreeCamSmoothing ) );
		else
			_freeCamVelocity = wishVel;

		var from = _orbit.Pivot;
		var delta = _freeCamVelocity * Time.Delta;

		_orbit.Pivot = SlideFreeCam( from, delta );

		// Re-derive velocity from the distance actually covered, so a wall bleeds off the blocked component
		// (the slide keeps the tangential part) instead of momentum silently pressing into geometry.
		if ( Time.Delta > 0f )
			_freeCamVelocity = (_orbit.Pivot - from) / Time.Delta;

		_orbit.Tick( handleAltDrag: false );

		// After Tick/Apply, which just asserted GameSettings.EditFov via MainCamera's own target-lerp —
		// override it with our own independently-smoothed widen to the player's FOV (see ApplySmoothFov).
		ApplySmoothFov( GameSettings.SpectateFov );
	}

	// Eases MainCamera's live FOV toward target at our own speed (FreeCamFovLerpSpeed), then snaps the ACTUAL
	// camera to that pre-eased value (SetFov(..., lerp:false)) rather than handing MainCamera a raw target and
	// trusting its shared FovLerpSpeed — so free cam's transition speed is tunable without touching every other
	// FOV change in the game (hunter zoom, orbit/hunter swap) that also rides that shared setting. Called both
	// entering free cam (target GameSettings.SpectateFov) and leaving it (target GameSettings.PropPlayFov, from UpdateCamera's
	// normal play branch below), so the widen AND the return glide are both smooth and share one continuous
	// blend value — no seam at the toggle.
	void ApplySmoothFov( float target )
	{
		if ( !_fovBlendSeeded )
		{
			// Start from what's actually on screen (the live camera, not MainCamera's target, which may still be
			// mid-ease — e.g. just out of edit) — no pop on the first frame.
			_fovBlend = Scene.Camera.IsValid() ? Scene.Camera.FieldOfView : MainCamera.Fov;
			_fovBlendSeeded = true;
		}

		_fovBlend = MathX.Lerp( _fovBlend, target, 1f - MathF.Exp( -FreeCamFovLerpSpeed * Time.Delta ) );
		MainCamera.SetFov( _fovBlend, lerp: false );
	}

	// Solid, not noclip, but glides along whatever it hits rather than stopping dead — and, unlike a single
	// slide pass, keeps re-deflecting off EVERY surface it meets along the way (up to FreeCamSlideIterations
	// bumps), so a corner (two planes) slides along the second wall too instead of arresting dead the moment
	// the one-pass version's correction trace also hit something. Mirrors BrushWorldClamp's own multi-bump
	// slide loop (same shape, plain world-space here instead of sculpture-local). A small skin is held back
	// from each hit so the NEXT iteration's trace starts clear of the surface instead of immediately
	// re-reporting the same hit — without it, a shallow corner reads as "no room to slide" and sticks.
	const int FreeCamSlideIterations = 4;
	const float FreeCamSlideSkin = 0.25f;

	Vector3 SlideFreeCam( Vector3 from, Vector3 delta )
	{
		var pos = from;
		var remaining = delta;

		for ( int i = 0; i < FreeCamSlideIterations && remaining.LengthSquared > 0.0001f; i++ )
		{
			var to = pos + remaining;
			var tr = TraceFreeCam( pos, to );

			if ( !tr.Hit )
			{
				pos = to;
				break;
			}

			// Advance to just short of the hit (the skin), then project whatever distance was LEFT (the
			// portion the hit fraction didn't cover) onto the hit plane for the next iteration to attempt.
			var stepLen = remaining.Length;
			var keepFrac = stepLen > FreeCamSlideSkin ? MathF.Max( 0f, tr.Fraction - FreeCamSlideSkin / stepLen ) : 0f;
			pos += remaining * keepFrac;

			var leftover = remaining * (1f - tr.Fraction);
			remaining = leftover - Vector3.Dot( leftover, tr.Normal ) * tr.Normal;
		}

		return pos;
	}

	SceneTraceResult TraceFreeCam( Vector3 from, Vector3 to )
		=> Scene.Trace.Ray( from, to )
			.Radius( FreeCamCollisionRadius )
			.IgnoreGameObjectHierarchy( _body?.GameObject )
			.Run();

	// The only place _freeCam actually changes — attaches/detaches the orbit rig's FollowTarget exactly once at
	// the transition (rather than every frame in UpdateFreeCam), and restores the normal boom Distance on the
	// way out so the next UpdateCamera() frame doesn't open with the camera jammed at zero boom length.
	/// <summary>Is the scouting free cam part of the game right now? Only in a prop-hunt round (a live
	/// <see cref="RoundManager"/>) — the lobby, creative and charades have no use for it, and it was just a way to
	/// fly around the map there.</summary>
	public static bool FreeCamAllowed => RoundManager.Current.IsValid();

	void SetFreeCam( bool on )
	{
		if ( _freeCam == on )
			return;

		_freeCam = on;

		if ( on )
		{
			// Pivot is the ORBIT pivot (roughly the disguise's body position) — the actual camera sits
			// Distance units BEHIND it. Forcing Distance to 0 without correcting Pivot first would snap the
			// view onto the pivot (the prop's own origin) instead of preserving wherever the camera was
			// actually looking from a moment ago — capture the real camera world position first and take
			// off from there instead, so there's no jump cut.
			var cam = Scene.Camera;
			var takeoff = cam.IsValid() ? cam.WorldPosition : _orbit.Pivot;

			_orbit.FollowTarget = null;
			_orbit.Pivot = takeoff;

			// CenterPivotOnShape (UpdateCamera) can leave PivotXYOverride set from the last normal-camera
			// frame — Tick() forcibly rewrites Pivot's X/Y from it EVERY frame regardless of FollowTarget,
			// which otherwise pins free cam's horizontal position in place and makes WASD look like it does
			// nothing (only jump/duck's pure-Z movement would have been visible).
			_orbit.PivotXYOverride = null;
			_pivotOffsetXY = null;
			_orbit.Distance = 0f;
			_freeCamVelocity = Vector3.Zero; // take off from rest — no stale momentum from the last session
		}
		else
		{
			_orbit.FollowTarget = _body?.GameObject;
			_orbit.Distance = CameraDistance;
		}
	}

	// Play: free mouse-look turns the camera AND disguise together (same delta to both, so the camera's offset from
	// the body is preserved). A bare mouse-button drag switches to Maya nav, no alt needed — LMB orbits (camera
	// moves alone, disguise stays put, no snap on release), RMB dollies, MMB pans — the same button mapping the
	// edit mode / menu use (see AltNav), so the vocabulary is one scheme everywhere. The mouse is captured out
	// here (no cursor, no UI to click), which is why the buttons are simply free to claim on Down, without the
	// press-edge/claim machinery the cursor modes need.
	void PlayCamera()
	{
		// A cursor overlay owns the mouse — don't hide it or let its clicks/drags navigate the camera behind it
		// (the same panels PauseMenuSystem forces the cursor visible for).
		if ( PauseMenu.IsOpen || RoundSetup.IsOpen )
			return;

		Mouse.Visibility = MouseVisibility.Hidden;

		// Dolly / pan are drag-based (RMB/MMB) and don't rotate.
		if ( Input.Down( "Attack2" ) ) { _orbit.Dolly( Mouse.Delta ); return; }
		if ( Input.Down( "CameraPan" ) ) { _orbit.Pan( Mouse.Delta ); return; }

		// Free mouse-look turns the camera; the disguise comes along UNLESS we're orbiting camera-only (LMB drag,
		// or the alt-hold shortcut). The same look input drives both, so an orbit and a free-look turn at the
		// identical speed.
		var look = Input.AnalogLook;
		_orbit.ApplyLook( look );

		bool cameraOnly = Input.Down( "Attack1" ) || (AltHoldOrbits && Input.Down( "Walk" ));
		if ( !cameraOnly )
			_bodyYaw += look.yaw;
	}

	// ── Origin recentring ─────────────────────────────────────────────────────────────────────────────
	// Sculpting walks the shape away from the pawn origin in LOCAL space, and the footprint ground-snap then
	// keeps the SHAPE on the floor — dragging the origin wherever the leftover offset demands, including under
	// the map (delete the starter sphere beneath a taller build and the origin ends up buried). The origin is
	// what conversion spawns and yaw rotation use, so on every commit we rebase it back to the shape's feet:
	// move the pawn to the bounds bottom-centre and counter-shift the disguise child by the exact inverse in
	// the same frame. The brushes and the disguise's WORLD transform are untouched — the clay never moves on
	// screen, nothing remeshes, and the camera (which follows the disguise object + shape bounds) sees no
	// change at all. Proxies only counter-shift the child from their synced brushes (the owner's matching
	// origin shift arrives via the pawn's transform sync; child transforms don't live-replicate).
	void RecenterOriginOnShape()
	{
		// The pending stamp ghost is excluded: the pivot-pin HARD RULE (the shape must never move on its own)
		// extends here — a ghost riding the cursor must not shift the origin until it's actually committed.
		if ( !_body.IsValid() || !Sdf.TryGetBounds( _body.Brushes, out var b, SculptEditSession.PendingStamp( _body ) ) )
			return;

		// NEVER recenter on an INVALID shape. The recenter is one transaction split across two transports:
		// the root move rides the engine's always-on transform sync, but the cancelling child shift only
		// happens on a machine when ITS brushes commit — and an invalid shape is deliberately never published
		// (see SdfNetworkSync's bounds gate), so proxies would receive the root move alone and watch the
		// whole prop slide. Proxies only ever recenter on applied (valid) commits; the owner must match
		// them. Skipping costs nothing visible locally — the pair cancels on screen by construction — and
		// needs no catch-up bookkeeping: the next VALID commit (fixing the shape, or the exit revert)
		// recenters from wherever the shape then sits, covering the whole accumulated offset, and publishes.
		// Same verdict the sync and collider gates pull (EvaluateNow is hash-cached), so the gates can never
		// disagree — and, like them, it's asked on the PUBLISHING side only. A proxy re-deriving its own strict
		// verdict for an applied commit would be the same bug mirrored: receive-side validation is deliberately
		// lenient and stateless (ValidateIncoming), so a shape sitting near the limit can read invalid here and
		// cost the proxy its counter-shift while the owner's root move still arrives — the prop slides.
		var boundsCfg = _body.GameObject.Components.Get<SculptBounds>();
		if ( !IsProxy && boundsCfg.IsValid() && !boundsCfg.EvaluateNow() )
			return;

		// The shape's feet (bounds bottom-centre) in PAWN space. Purely local — ground contact and slopes never
		// feed in, so this is zero except right after an edit changed the bounds.
		var feetLocal = _body.GameObject.LocalTransform.PointToWorld( new Vector3( b.Center.x, b.Center.y, b.Mins.z ) );
		if ( feetLocal.Length < 0.01f )
			return;

		var worldDelta = WorldTransform.PointToWorld( feetLocal ) - WorldPosition;
		_body.GameObject.LocalPosition -= feetLocal;

		if ( IsProxy )
			return;

		WorldPosition += worldDelta;
		Transform.ClearInterpolation(); // both writes land this exact frame — no one-frame shear on the pawn root
	}

	/// <summary>World position of the sculpted shape's feet (bounds bottom-centre), computed live from the
	/// brushes — correct even if a mid-drag edit has left the pawn origin stale. False when there's no shape.</summary>
	public bool TryGetShapeFeet( out Vector3 feet )
	{
		feet = default;
		if ( !_body.IsValid() || !Sdf.TryGetBounds( _body.Brushes, out var b, SculptEditSession.PendingStamp( _body ) ) )
			return false;

		feet = _body.WorldTransform.PointToWorld( new Vector3( b.Center.x, b.Center.y, b.Mins.z ) );
		return true;
	}

	// ── Disguise body ─────────────────────────────────────────────────────────────────────────────────
	// Clone the prefab (mesh/SDF renderer + materials authored there), parent it to the pawn, and lift it so the
	// BOTTOM of its sculpted shape rests at the pawn's feet (origin) rather than centred half underground.
	/// <summary>Host-side lobby restore: put a remembered disguise onto a fresh, still-DISABLED prop clone —
	/// the same pre-enable dress as <see cref="HunterController.WearFace"/>, for the same reason (the first
	/// build ever started is the remembered shape, and the spawn snapshot then carries it to everyone —
	/// including its owner, because a disguise has no disk anywhere and the host's swap memory is the only
	/// copy). Creates the disguise branch early; OnStart's <see cref="EnsureDisguiseBody"/> then finds it
	/// exactly like it finds a proxy's snapshot copy. Re-lifts the branch onto the restored shape's feet —
	/// the creation-time lift measured the prefab's default shape, and a taller/shorter restore would
	/// otherwise sink into or hover over the ground.</summary>
	internal static void WearDisguise( GameObject pawn, List<SdfBrush> brushes )
	{
		if ( brushes is not { Count: > 0 } )
			return;

		var hider = pawn.Components.Get<HiderController>( includeDisabled: true );
		if ( !hider.IsValid() )
			return;

		var sculpture = hider.EnsureDisguiseBody();
		if ( !sculpture.IsValid() )
			return;

		sculpture.Brushes = brushes.Select( b => b.Copy() ).ToList();

		if ( Sdf.TryGetBounds( sculpture.Brushes, out var bounds ) )
			sculpture.GameObject.LocalPosition = Vector3.Up * -bounds.Mins.z;

		if ( sculpture.Active )
			sculpture.Rebuild();
	}

	SdfSculpture EnsureDisguiseBody()
	{
		var existing = GameObject.Children.FirstOrDefault( c => c.Name == "Disguise" );
		if ( existing.IsValid() )
			return existing.Components.Get<SdfSculpture>();

		var go = ClonePrefab() ?? new GameObject( true, "Disguise" );
		go.Name = "Disguise";
		go.Parent = GameObject;
		go.LocalRotation = Rotation.Identity;

		// GetOrCreate so the in-code fallback (prefab missing) still yields a usable sculpture.
		var sculpture = go.Components.GetOrCreate<SdfSculpture>();

		// Every disguise carries sculpt size limits — the prefab's authored values win, this is only the
		// fallback so a bounds-less disguise can't slip through as unconstrained (the component's defaults
		// are the game-wide disguise allowance; see SculptBounds).
		go.Components.GetOrCreate<SculptBounds>();

		// Lift from the shape's OWN bounds — no fixed body height to keep in sync. Mins.z is negative (the shape
		// extends below local origin), so -Mins.z puts its lowest point at the feet.
		float lift = Sdf.TryGetBounds( sculpture.Brushes, out var bounds ) ? -bounds.Mins.z : 0f;
		go.LocalPosition = Vector3.Up * lift;

		return sculpture;
	}

	GameObject ClonePrefab()
	{
		if ( DisguisePrefab is null )
		{
			Log.Warning( "Disguise prefab not set — falling back to a bare sculpture." );
			return null;
		}

		return SceneUtility.GetPrefabScene( DisguisePrefab )?.Clone();
	}
}
