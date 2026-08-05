using System;

namespace Mimiclay;

/// <summary>
/// Hider: a third-person controller whose body is an <see cref="SdfSculpture"/> disguise. It owns its own
/// physics — a <see cref="Rigidbody"/> driven like a character (we integrate gravity ourselves, lock the body
/// upright, and turn it through the solver so an obstacle can physically block a turn) — and collides using the
/// disguise's generated <see cref="ModelCollider"/>, so a prop is exactly as solid as it looks. None of this is
/// shared with the hunter, which is a vanilla first-person <see cref="PlayerController"/>; the prop's needs
/// (footprint ground-probing, flush ground-snap, slope handling, compliance turning, live-while-sculpting) are
/// specific enough that it stands alone.
///
/// <b>Play mode:</b> WASD to move, free mouse-look turns the camera AND the disguise together. Holding Alt
/// decouples them — the mouse orbits only the camera (the disguise keeps facing where it was); release Alt and
/// the disguise simply adopts wherever the camera now points (no snap-back).
///
/// <b>Edit mode</b> (tab): locomotion input stops and the sculpt gizmo appears; the camera switches to
/// Maya-style alt navigation (alt+LMB orbit, alt+RMB dolly, alt+MMB vertical pan), cursor freed. The body stays
/// physically live so it re-settles onto its new shape as you sculpt.
/// </summary>
[Title( "Hider Controller" )]
[Category( "Mimiclay" )]
[Icon( "directions_run" )]
public sealed class HiderController : Component, IGameObjectNetworkEvents
{
	// ── Movement ──────────────────────────────────────────────────────────────────────────────────────
	[Property, Group( "Movement" )] public float WalkSpeed { get; set; } = 110f;
	[Property, Group( "Movement" )] public float RunSpeed { get; set; } = 200f;
	[Property, Group( "Movement" )] public float CrouchSpeed { get; set; } = 60f;
	[Property, Group( "Movement" )] public float JumpPower { get; set; } = 300f;

	/// <summary>Upward gravity while rising — tuned so the jump arc up feels right. We integrate gravity
	/// ourselves (engine gravity off) so the rise and fall can differ.</summary>
	[Property, Group( "Movement" )] public float Gravity { get; set; } = 800f;

	/// <summary>Extra gravity multiplier once falling, so you drop snappily instead of floating down. 1 = same
	/// as the rise; higher = quicker, punchier landings.</summary>
	[Property, Group( "Movement" )] public float FallGravityMult { get; set; } = 1.9f;

	/// <summary>How quickly horizontal velocity chases the input target on the ground (per second, exponential).
	/// Higher = snappier starts/stops; this also serves as the "friction" (input zero decays velocity to zero).</summary>
	[Property, Group( "Movement" )] public float GroundControl { get; set; } = 12f;

	/// <summary>Weaker horizontal control while airborne, so a jump keeps most of its momentum.</summary>
	[Property, Group( "Movement" )] public float AirControl { get; set; } = 2.5f;

	/// <summary>Max speed (u/s) the body eases DOWN to sit flush when a foot is hovering above the floor. A foot
	/// reads grounded slightly before it touches; rather than freeze the fall there (the old hover), we close the
	/// remaining gap at up to this speed. The disguise collider arrests the descent at contact, so it can't bury
	/// the prop — higher just snaps a residual gap shut more sharply.</summary>
	[Property, Group( "Movement" )] public float GroundSnapSpeed { get; set; } = 300f;

	/// <summary>On a slope, the tiny gap (units, at full tilt) the body floats above the surface instead of pressing
	/// flush — pressing straight down onto an incline is what the solver deflects into a slow downhill slide, so a
	/// hair of float removes the contact and kills it. Scaled by steepness, so FLAT ground stays perfectly flush
	/// (zero gap). Keep small to stay unnoticeable.</summary>
	[Property, Group( "Movement" )] public float GroundRestGap { get; set; } = 4f;

	/// <summary>Below this horizontal speed (u/s), a grounded body with no move input stops dead — clears any residual
	/// drift so it can't creep down an incline.</summary>
	[Property, Group( "Movement" )] public float StopSpeed { get; set; } = 6f;

	/// <summary>How much of the spin the physics solver introduces on its own (a snagged part being pivoted by
	/// our forward push, or a blocked turn being cancelled) is kept, 0..1. 1 = mouse input fully defers to
	/// physics: blocked turns simply don't happen and the shape works itself free. Lower = the body asserts the
	/// mouse turn more even against a contact (less compliant). Compliance-first turning lives or dies on this.</summary>
	[Property, Group( "Movement" ), Range( 0f, 1f )] public float SnagCompliance { get; set; } = 1f;

	// ── Look / Camera ─────────────────────────────────────────────────────────────────────────────────
	[Property, Group( "Look" ), Range( -89f, 0f )] public float MinPitch { get; set; } = -89f;
	[Property, Group( "Look" ), Range( 0f, 89f )] public float MaxPitch { get; set; } = 89f;

	[Property, Group( "Camera" )] public float CameraDistance { get; set; } = 180f;
	[Property, Group( "Camera" )] public float CameraHeightOffset { get; set; } = 16f;
	[Property, Group( "Camera" )] public float ZoomSpeed { get; set; } = 0.01f;
	[Property, Group( "Camera" )] public float PanSpeed { get; set; } = 1.0f;
	[Property, Group( "Camera" )] public float MinDistance { get; set; } = 24f;
	[Property, Group( "Camera" )] public float MaxDistance { get; set; } = 2000f;

	/// <summary>Off = the camera boom no longer pulls in when geometry blocks it, so it keeps its full
	/// distance and clips through walls instead.</summary>
	[Property, Group( "Camera" )] public bool CameraCollision { get; set; } = true;

	/// <summary>In play mode, holding Alt orbits the camera without also holding a mouse button (alt+RMB still
	/// dollies, alt+MMB still pans). Off = Maya-style alt+LMB to orbit. Edit mode always needs the click (so the
	/// cursor stays free for the gizmo).</summary>
	[Property, Group( "Camera" )] public bool AltHoldOrbits { get; set; } = true;

	/// <summary>The disguise the hider sculpts. A prefab so its mesh/SDF renderer + materials are authored in the
	/// editor (a single source of truth), not duplicated in code.</summary>
	[Property, Group( "Disguise" )] public PrefabFile DisguisePrefab { get; set; }

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
	/// <summary>Draw the ground-probe points + traces each step (green = found ground, red = nothing) — including
	/// in edit mode — so you can see the footprint snapshot the body is standing on.</summary>
	[Property, Group( "Debug" )] public bool DebugGroundProbes { get; set; }

	/// <summary>Draw a marker at the orbit camera's pivot every frame (magenta sphere + vertical line), so you
	/// can see exactly what point the camera is orbiting.</summary>
	[Property, Group( "Debug" )] public bool DebugCameraPivot { get; set; }

	// ── Runtime state ─────────────────────────────────────────────────────────────────────────────────
	/// <summary>Where the player is looking, seeded on spawn. Yaw drives movement + body facing.</summary>
	public Angles EyeAngles { get; private set; }

	public bool IsCrouching { get; private set; }

	/// <summary>True when a downward probe found ground this fixed step (gates jumping + full ground control).</summary>
	public bool IsGrounded { get; private set; }

	Rigidbody Body;          // our physics body (the disguise's ModelCollider aggregates into it)
	SdfSculpture _body;      // the sculpted disguise

	// Internal: the disguise sculpture, for code that needs THE pawn's body explicitly (PlayerNameplates
	// anchors above it) — resolved on every machine in OnStart, so proxies can read it too.
	internal SdfSculpture DisguiseSculpture => _body;
	SdfCollider _collider;   // the disguise's physics (footprint snapshot + ModelCollider), if it's solid
	SculptEditSession _session;

	// The shared orbit rig owns the camera (orientation, zoom, pan, collision boom + the dot cursor) for BOTH
	// modes, so toggling edit never jumps the view and our camera runs the exact same code as the hunter's
	// face-edit camera. We Tick it ourselves rather than letting it run its own OnUpdate (see UpdateCamera).
	OrbitCameraController _orbit;
	float _bodyYaw;   // disguise + movement yaw — turns with the camera unless alt holds it still

	// Jump is an edge event read on the frame; consumed on the next fixed step so it can't be missed.
	bool _jumpQueued;

	// The yaw spin we set last fixed step. Comparing it against the body's actual spin this step tells us what
	// the physics solver added on its own (a snag pivoting us, or a blocked turn cancelled) so we can keep it.
	float _lastSetYawRate;
	// The facing yaw last fixed step — the delta is how much the mouse asked to turn this step. No absolute target
	// is stored, so we never try to force the body back to a "correct" yaw (that was the feedback loop).
	float _prevFacingYaw;
	bool _turnSeeded; // false until the first step / after a freeze, so resuming doesn't snap from a stale yaw

	bool EditMode => _session?.IsEditing ?? false;

	// Edit mode AND the Starting-countdown freeze (RoundManager.ControlsLocked) both stop locomotion input, but the
	// body stays physically live and the camera keeps running — so a frozen prop still settles onto the ground and
	// you can look around, you just can't walk or jump until the round begins.
	bool ControlActive => !EditMode && !RoundManager.ControlsLocked;

	// Released into the level (see ReleaseControl): the prop keeps simulating but takes no input and no longer owns
	// the camera, so a freshly-spawned pawn can take over while this one stays as scenery.
	bool _dormant;

	/// <summary>Hand this prop off, HOST-SIDE: mark it dormant so the host keeps simulating it as scenery (gravity +
	/// ground-snap still settle it) without driving it with host input/camera, and finish any host-side sculpt.
	/// One-way — used to scatter player-sculpted props around the level. Pairs with a <c>pawn.Network.DropOwnership()</c>
	/// in <c>DebugGameMode.Retire</c>: this only reaches the HOST's copy, so the owning CLIENT's own teardown (leaving
	/// edit mode) is driven by <c>StopControl</c> when that drop turns its copy into a proxy. Splitting it this way is
	/// deliberate — each machine tears down the state it actually holds (<c>_dormant</c>/edit are machine-local).</summary>
	public void ReleaseControl()
	{
		_dormant = true;
		if ( EditMode )
			_session?.Toggle();
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

	// Yaw WASD is measured against = the camera (the rig's yaw); yaw the disguise visually faces = its own
	// (frozen during alt orbit).
	float MoveYaw => _orbit.Angles.yaw;
	float FacingYaw => _bodyYaw;

	protected override void OnAwake()
	{
		Body = GameObject.Components.GetOrCreate<Rigidbody>();
		Body.Gravity = false; // we integrate gravity ourselves so rise/fall can differ (snappy landings)
		Body.RigidbodyFlags = RigidbodyFlags.DisableCollisionSounds; // a prop is silent — no clatter scraping the world
		// Lock pitch/roll so the body stays upright, but leave YAW free so we can turn it through the physics solver
		// — that's what lets an obstacle physically block the turn.
		Body.Locking = new PhysicsLock { Pitch = true, Yaw = false, Roll = true };

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
		_orbit.MinDistance = MinDistance;
		_orbit.MaxDistance = MaxDistance;
		_orbit.ZoomSpeed = ZoomSpeed;
		_orbit.PanSpeed = PanSpeed;
		_orbit.MinPitch = MinPitch;
		_orbit.MaxPitch = MaxPitch;
		_orbit.Angles = new Angles( 15f, EyeAngles.yaw, 0f );
		_orbit.Distance = CameraDistance;

		// Edit session + network sync, both bound to this machine's disguise — SculptablePawn keeps the
		// "session and sync always target the same sculpture" invariant in one place. No orbit rig handed to
		// the session: the hider's always-on rig above owns the camera for play AND edit. The disguise is a
		// local clone, so the target is wired here rather than in the prefab.
		_session = SculptablePawn.AttachEditing( this, _body );

		// Committed fires on discrete edits + gizmo release locally, and on an applied commit on proxies — never
		// mid-drag — so both sides rebase on the same settled shape states (see RecenterOriginOnShape).
		if ( _body.IsValid() )
			_body.Committed += RecenterOriginOnShape;
	}

	protected override void OnDestroy()
	{
		if ( _body.IsValid() )
			_body.Committed -= RecenterOriginOnShape;
	}

	protected override void OnUpdate()
	{
		// Released into the level: no input, no camera — just let OnFixedUpdate keep the physics settling.
		if ( _dormant )
			return;

		// Only the owning client reads input + drives the ONE shared scene camera. On every other machine this pawn
		// is a proxy: the engine moves its body from the networked transform, and the local player's own pawn owns
		// the camera — so a proxy must not touch input or the camera (two props would otherwise fight over it).
		// Gate LIVE each frame: ownership replicates after OnStart, so a one-shot read would be stale (same reason
		// HunterController gates live). The streamed disguise shape is applied by SdfNetworkSync, not here.
		if ( IsProxy )
			return;

		// Read the toggles even while control is suspended, so edit mode can be exited.
		if ( Input.Pressed( "Edit" ) )
			_session?.Toggle();
		if ( Input.Pressed( "ToggleWireframes" ) )
			_session?.ToggleWireframes();

		UpdateTaunts();

		if ( ControlActive && Input.Pressed( "jump" ) )
			_jumpQueued = true;

		// Always drive the camera (per-frame, for smoothness) — needed during edit mode too, where movement is frozen.
		UpdateCamera();

		// Drawn here (not in the fixed step) so it shows in edit mode too, where movement is suspended.
		if ( DebugGroundProbes )
			DrawGroundProbes();
		if ( DebugCameraPivot )
			DrawCameraPivot(); // after UpdateCamera, so it shows this frame's pivot (override included)
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

	void UpdateTaunts()
	{
		var round = RoundManager.Current;
		if ( !round.IsValid() || round.Phase != RoundPhase.Hunt )
		{
			_tauntClockSeeded = false;
			return;
		}

		// Same 5s floor as LobbyManager.SetTauntSeconds — also catches a zeroed TauntSeconds from any stale
		// settings path (e.g. an old serialized struct), which Max(0) would turn into per-frame whistling.
		float interval = MathF.Max( 5f, round.Settings.TauntSeconds );

		// First Hunt frame: every prop starts its clock at a RANDOM fraction of the interval. All machines flip
		// into the Hunt within a frame or two of each other, so without this offset the whole lobby would
		// whistle in chorus every cycle — the random phase is the stagger.
		if ( !_tauntClockSeeded )
		{
			_tauntClockSeeded = true;
			_sinceTaunt = interval; // a manual taunt is allowed immediately at the whistle-phase start
			_nextAutoTaunt = Game.Random.Float( 0.3f, 1f ) * interval;
		}

		if ( Input.Pressed( "Taunt" ) && _sinceTaunt > ManualTauntCooldown )
			Taunt( interval );
		else if ( _nextAutoTaunt <= 0f )
			Taunt( interval );
	}

	// Fire one taunt and rewind the auto clock — jittered ±15% so two props whose clocks happened to land in
	// step drift apart again instead of whistling together for the whole hunt.
	void Taunt( float interval )
	{
		_sinceTaunt = 0f;
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
		// a synced proxy is moved, not simulated, and our velocity writes are no-ops on it anyway). Skip our
		// character physics entirely so we don't run ground-probe traces for a body we're not simulating.
		if ( IsProxy )
			return;

		if ( !Body.MotionEnabled )
			Body.MotionEnabled = true;

		// No locomotion input when editing (control suspended) OR when released into the level (dormant): the body
		// stays LIVE — gravity, collision and ground-snap still run, so an editing prop re-settles onto its shape as
		// soon as a commit rebuilds collision, and a released prop keeps resting where it was left.
		if ( !ControlActive || _dormant )
		{
			UpdateMovement( controlled: false );
			return;
		}

		IsCrouching = Input.Down( "duck" ); // a prop's shape never changes, so crouch just slows it down
		UpdateMovement( controlled: true );
	}

	void UpdateMovement( bool controlled )
	{
		IsGrounded = CheckGround( out float groundGap, out Vector3 groundNormal );

		// No locomotion input while suspended-but-simulating: the wish stays zero so friction settles the body in
		// place — it still falls and ground-snaps onto its current shape, it just won't walk while you sculpt.
		var wish = Vector3.Zero;
		if ( controlled )
		{
			float speed = IsCrouching ? CrouchSpeed : (Input.Down( "run" ) ? RunSpeed : WalkSpeed);
			// WASD is in local space; orient it by yaw only so look-up doesn't slow you down.
			wish = Rotation.FromYaw( MoveYaw ) * Input.AnalogMove * speed;
		}

		var vel = Body.Velocity;

		// Chase the target velocity exponentially — snappy on the ground, looser in the air. Input of zero decays it
		// back to rest, which doubles as friction.
		float control = IsGrounded ? GroundControl : AirControl;
		float t = 1f - MathF.Exp( -control * Time.Delta );

		Vector3 newVel;
		if ( IsGrounded )
		{
			// Walk ALONG the slope: reproject the (flat) wish onto the ground plane so we don't drive into an incline
			// and get ejected upward (the pop that launched the prop when you stopped). On a slope this gives the wish
			// a vertical component — uphill climbs, downhill descends — so the body follows the surface in full 3D.
			var slopeWish = ProjectOntoSlope( wish, groundNormal );
			newVel = Vector3.Lerp( vel, slopeWish, t );

			// Ground-snap: ease DOWN to close the gap so the prop sits flush instead of hovering — the collider arrests
			// the descent at contact, so it can't bury the prop. But on a slope we keep a SMALL rest gap (scaled by how
			// steep it is): pressing straight onto an incline is what the solver deflects into a slow downhill slide, so
			// floating a hair clear of the surface removes the contact and stops it. Flat ground has zero rest gap and
			// still sits perfectly flush.
			float tilt = MathF.Sqrt( MathF.Max( 0f, 1f - groundNormal.z * groundNormal.z ) ); // sin(slope): 0 flat, 1 wall
			float snapGap = MathF.Max( groundGap - GroundRestGap * tilt, 0f );
			newVel = newVel.WithZ( newVel.z - MathF.Min( snapGap / Time.Delta, GroundSnapSpeed ) );

			// With no move input, once we're crawling, stop dead — the rest gap removes the slide source, this clears
			// any leftover numerical drift so a parked prop can't creep down a slope.
			if ( wish.IsNearZeroLength && newVel.WithZ( 0f ).Length < StopSpeed )
				newVel = new Vector3( 0f, 0f, newVel.z );

			if ( controlled && _jumpQueued )
			{
				newVel = newVel.WithZ( JumpPower );
				IsGrounded = false; // so CheckGround's rising-reject lets the jump actually leave next step
			}
		}
		else
		{
			// Airborne: chase horizontally only (weaker control keeps a jump's momentum) and integrate our own gravity
			// — falling faster than we rose (FallGravityMult) so the descent is snappy, not floaty.
			var horiz = Vector3.Lerp( vel.WithZ( 0f ), wish.WithZ( 0f ), t );
			float g = Gravity * (vel.z < 0f ? FallGravityMult : 1f);
			newVel = horiz.WithZ( vel.z - g * Time.Delta );
		}

		Body.Velocity = newVel;
		_jumpQueued = false;

		if ( !controlled )
		{
			// Locked while editing: don't apply mouse-turn, and kill any residual yaw spin so the prop holds still as
			// you sculpt. Leave the turn un-seeded so resuming play re-seeds from the live facing (no snap).
			Body.AngularVelocity = Body.AngularVelocity.WithZ( 0f );
			_turnSeeded = false;
			return;
		}

		// Compliance-first turning: rotate the body by exactly how much the mouse turned THIS step, plus only the spin
		// the physics solver contributed on its own (a snag pivoting us, or a blocked turn it cancelled). There is NO
		// absolute facing target, so nothing ever forces a rotation against a contact — a blocked turn just doesn't
		// happen, the snag pivot survives, and nothing accumulates. So mouse input always defers to physics and we
		// can't fight ourselves into a loop. FacingYaw is the disguise's own yaw, so it keeps its facing while WASD
		// moves relative to the camera.
		if ( !_turnSeeded )
		{
			_prevFacingYaw = FacingYaw;
			_lastSetYawRate = Body.AngularVelocity.z;
			_turnSeeded = true;
		}

		// AngularVelocity is in RADIANS/s, so convert the mouse's degrees-of-turn before using it as a rate.
		float mouseRate = MathX.DeltaDegrees( _prevFacingYaw, FacingYaw ) * (MathF.PI / 180f) / Time.Delta;
		_prevFacingYaw = FacingYaw;

		float contactSpin = Body.AngularVelocity.z - _lastSetYawRate; // spin the solver introduced on its own
		_lastSetYawRate = mouseRate + contactSpin * SnagCompliance;
		Body.AngularVelocity = Body.AngularVelocity.WithZ( _lastSetYawRate );
	}

	// Ground test: sphere-probe straight down from EACH footprint point (see GroundProbePoints), so a body made of
	// several uneven / off-centre / floating colliders is grounded when ANY part of its underside is at the floor —
	// not just a single guess at the bounds centre. Rejects hits while rising so a jump actually leaves. Also reports
	// the SMALLEST gap (closest foot to the floor below it) so the body can settle flush instead of hovering, and the
	// averaged surface NORMAL across the feet that hit, so movement can run along a slope.
	bool CheckGround( out float gap, out Vector3 normal )
	{
		gap = 0f;
		normal = Vector3.Up;
		if ( Body.Velocity.z > 50f )
			return false;

		bool grounded = false;
		float minGap = float.MaxValue;
		var normalSum = Vector3.Zero;
		foreach ( var p in GroundProbePoints() )
		{
			var tr = ProbeGround( p );
			if ( !tr.Hit )
				continue;

			grounded = true;
			// The sphere starts GroundProbeUp above the foot point; tr.Distance it travelled before contact equals how
			// far that foot sits above the floor directly beneath it (0 = touching).
			minGap = MathF.Min( minGap, tr.Distance );
			normalSum += tr.Normal; // averaged below so an uneven multi-foot body reads the overall slope, not one tri
		}

		if ( grounded )
		{
			gap = minGap;
			if ( !normalSum.IsNearZeroLength )
				normal = normalSum.Normal;
		}
		return grounded;
	}

	// Reproject a flat, world-space wish velocity onto the ground plane so movement runs ALONG a slope instead of
	// driving into it (which makes the solver eject the body upward). Speed is preserved — walking uphill covers the
	// surface at the same rate, trading slower horizontal progress for the climb, as it should. Flat ground is a no-op.
	static Vector3 ProjectOntoSlope( Vector3 wish, Vector3 normal )
	{
		float speed = wish.Length;
		if ( speed < 0.01f )
			return wish;

		var projected = wish - normal * Vector3.Dot( wish, normal );
		return projected.IsNearZeroLength ? Vector3.Zero : projected.Normal * speed;
	}

	// World-space points to probe straight down for ground: the disguise's footprint snapshot (one point per
	// underside cell), transformed to world — so an uneven multi-collider prop is probed under each foot. Falls back
	// to the bottom centre of the body's bounds until the disguise + its snapshot exist.
	IEnumerable<Vector3> GroundProbePoints()
	{
		var feet = _collider.IsValid() ? _collider.FootPoints : null;
		if ( feet is { Count: > 0 } )
		{
			var tx = _body.WorldTransform;
			foreach ( var f in feet )
				yield return tx.PointToWorld( f );
			yield break;
		}

		var b = Body.GetWorldBounds();
		yield return new Vector3( b.Center.x, b.Center.y, b.Mins.z );
	}

	// One downward sphere-probe from a footprint point. Small + tight so the body only reads grounded when a foot is
	// right at the floor (a generous probe hovers). Ignores our own hierarchy (the disguise collider is a child).
	const float GroundProbeRadius = 3f, GroundProbeUp = 3f, GroundProbeDown = 4f;

	SceneTraceResult ProbeGround( Vector3 p ) => Scene.Trace
		.Sphere( GroundProbeRadius, p + Vector3.Up * GroundProbeUp, p - Vector3.Up * GroundProbeDown )
		.IgnoreGameObjectHierarchy( GameObject )
		.Run();

	// Draw every ground probe (green = found ground, red = nothing). Called every frame from OnUpdate — including edit
	// mode — so you can watch the footprint snapshot change as you sculpt.
	void DrawGroundProbes()
	{
		foreach ( var p in GroundProbePoints() )
		{
			var col = ProbeGround( p ).Hit ? Color.Green : Color.Red;
			Scene.DebugOverlay.Sphere( new Sphere( p, GroundProbeRadius ), col, 0f );
			Scene.DebugOverlay.Line( p + Vector3.Up * GroundProbeUp, p - Vector3.Up * GroundProbeDown, col, 0f );
		}
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
		if ( CenterPivotOnShape && _body.IsValid() && Sdf.TryGetBounds( _body.Brushes, out var b ) )
		{
			var targetLocal = new Vector2( b.Center.x, b.Center.y );
			_pivotOffsetXY = _pivotOffsetXY is { } current
				? Vector2.Lerp( current, targetLocal, 1f - MathF.Exp( -PivotSmoothSpeed * Time.Delta ) )
				: targetLocal; // first frame (spawn / toggled back on): start on target, no glide from stale state

			// Rotate the smoothed local offset out by the body's LIVE transform (upright-locked, so local Z
			// never bleeds into world X/Y).
			var local = _pivotOffsetXY.Value;
			var world = _body.WorldTransform.PointToWorld( new Vector3( local.x, local.y, 0f ) );
			_orbit.PivotXYOverride = new Vector2( world.x, world.y );
		}
		else
		{
			_pivotOffsetXY = null;
			_orbit.PivotXYOverride = null;
		}

		if ( EditMode )
		{
			// Edit: the rig reads Maya alt-nav itself (orbit/dolly/pan + the dot cursor). We leave _bodyYaw alone,
			// so the disguise stays put and re-settles onto its shape in place while we sculpt.
			_orbit.Tick( handleAltDrag: true );
		}
		else
		{
			PlayCamera();                        // free-look + alt dolly/pan feed the rig; may turn the disguise too
			_orbit.Tick( handleAltDrag: false );
		}
	}

	// Play: free mouse-look turns the camera AND disguise together (same delta to both, so the camera's offset from
	// the body is preserved). Alt + a mouse button switches to Maya nav (camera moves alone, disguise stays put, no
	// snap on release). Alt with NO button still free-looks as normal.
	void PlayCamera()
	{
		Mouse.Visibility = MouseVisibility.Hidden;
		bool alt = Input.Down( "Walk" );

		// Dolly / pan are drag-based (alt + RMB/MMB) and don't rotate.
		if ( alt && Input.Down( "Attack2" ) ) { _orbit.Dolly( Mouse.Delta ); return; }
		if ( alt && Input.Down( "CameraPan" ) ) { _orbit.Pan( Mouse.Delta ); return; }

		// Free mouse-look turns the camera; the disguise comes along UNLESS we're orbiting camera-only (alt-hold,
		// or alt+LMB). The same look input drives both, so an alt-orbit and a free-look turn at the identical speed.
		var look = Input.AnalogLook;
		_orbit.ApplyLook( look );

		bool cameraOnly = alt && (AltHoldOrbits || Input.Down( "Attack1" ));
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
		if ( !_body.IsValid() || !Sdf.TryGetBounds( _body.Brushes, out var b ) )
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
		if ( !_body.IsValid() || !Sdf.TryGetBounds( _body.Brushes, out var b ) )
			return false;

		feet = _body.WorldTransform.PointToWorld( new Vector3( b.Center.x, b.Center.y, b.Mins.z ) );
		return true;
	}

	// ── Disguise body ─────────────────────────────────────────────────────────────────────────────────
	// Clone the prefab (mesh/SDF renderer + materials authored there), parent it to the pawn, and lift it so the
	// BOTTOM of its sculpted shape rests at the pawn's feet (origin) rather than centred half underground.
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
