using System;
using Sandbox.Physics;

namespace Mimiclay;

/// <summary>
/// The hunter's prop grab — hold RMB on a nearby prop to pick it up on a swingy spring joint, release to let it
/// fly with the drag's momentum, LMB while holding to launch it. Works on map props (<see cref="GrabbableProp"/>,
/// the networked physics clones <see cref="MapPropPhysics"/> makes) and on PLAYER props (<see cref="HiderController"/>
/// pawns — which ragdoll while held and recover control once they settle, see the hider's ragdoll state).
///
/// Mechanism is the sandbox physgun's, retuned to feel like CARRYING rather than a tractor beam: an invisible
/// keyframed anchor body rides the aim at the grab distance, and ONE control joint ties the prop's grab point to
/// it — but anchored at the HIT point (not the mass centre), with NO angular spring (the prop dangles and swings
/// like a pendulum), a low spring frequency (it lags a fast turn), and a FIXED force budget instead of the
/// physgun's mass-scaled one. That last constant is the whole weight fantasy: force to hold mass m against
/// gravity is m·g, so anything under <see cref="HoldForce"/>/g lifts freely and heavier things sag and drag
/// along the floor. Mass itself comes from clay volume (<see cref="PropMass"/>), identical for decoys and
/// players, so weight is never a tell.
///
/// Networking is the physgun's model verbatim: <see cref="State"/> is a small [Sync] struct on this (networked)
/// pawn component; every machine runs <see cref="OnFixedUpdate"/>, and the joint exists only on the machine where
/// the target's Rigidbody is NOT a proxy — the host for map props, the hider's own machine for player props —
/// reading this hunter's replicated eye. No ownership ever transfers. Reassigning the whole struct per change is
/// deliberate (in-place mutation doesn't trip [Sync] change detection — the sandbox repo hit the same thing).
/// </summary>
[Title( "Prop Grabber" )]
[Category( "Mimiclay" )]
[Icon( "pan_tool" )]
public sealed class PropGrabber : Component
{
	/// <summary>How far the grab reaches. Short on purpose — this is "pick up the thing near you", not a beam.</summary>
	[Property] public float GrabRange { get; set; } = 300f;

	/// <summary>The spring's force budget — the hunter's fixed "strength" (see class summary). At gravity 800,
	/// masses under ~50 lift freely; heavier props sag toward the floor and mostly drag.</summary>
	[Property] public float HoldForce { get; set; } = 40000f;

	/// <summary>Spring frequency in Hz. The physgun uses 32 (rigid tracking); this low value is what makes the
	/// held prop lag your aim and swing around like a pendulum on a soft rope.</summary>
	[Property] public float SpringFrequency { get; set; } = 4.5f;

	/// <summary>Spring damping ratio — under 1 so the swing overshoots and wobbles a little.</summary>
	[Property] public float SpringDamping { get; set; } = 0.6f;

	/// <summary>Launch speed (units/s) for LMB-while-holding. The impulse is MASS-SCALED (sandbox style):
	/// everything leaves at this speed regardless of weight.</summary>
	[Property] public float LaunchSpeed { get; set; } = 900f;

	/// <summary>Random tumble (rad/s) added to a launch so it reads as a hurl, not a slide.</summary>
	[Property] public float LaunchSpin { get; set; } = 8f;

	const float MinHoldDistance = 45f;    // the target point can't be pushed into your face
	const float ThrowMassRef = 25f;       // mass at/below which a release inherits the full drag velocity
	const float MaxAnchorSpeed = 1500f;   // cap on the tracked drag velocity (a warped mouse flick is huge)

	static readonly Color HoverColor = new( 0.85f, 0.93f, 1f );
	static readonly Color HeldColor = new( 1f, 0.85f, 0.3f );

	/// <summary>The replicated grab: what's held, where on it, and how far out it rides. Small on purpose —
	/// everything else (the joint, the anchor, the throw bookkeeping) is derived per machine.</summary>
	public struct GrabState
	{
		public bool Active { get; set; }
		public GameObject Target { get; set; }
		public Vector3 LocalOffset { get; set; }
		public float Distance { get; set; }

		public readonly bool IsValid() => Active && Target.IsValid();

		// [Sync] change detection compares hashes — without this, two states differing only in a field could
		// read as unchanged (and the reassign-whole-struct pattern relies on it firing).
		public override readonly int GetHashCode() => HashCode.Combine( Active, Target, LocalOffset, Distance );
	}

	[Sync] public GrabState State { get; set; }

	HunterController _hunter;
	PlayerController _controller;

	bool _preventReselect;   // after any release, both buttons must come up before a re-grab (physgun pattern)
	bool _launchSuppress;    // the launch click must not ALSO fire the gun — held until attack1 releases
	bool _launched;          // body-owner side: joint must not re-form until the synced state clears

	PhysicsBody _anchor;                    // keyframed body the aim drives — no GameObject, pure physics
	Sandbox.Physics.ControlJoint _joint;    // fully qualified: Sandbox.ControlJoint is the scene component
	Rigidbody _heldBody;                    // the body the live joint holds (for the release throw)
	Vector3 _anchorPos;
	Vector3 _anchorVel;                     // tracked drag velocity — what a released prop inherits
	bool _anchorSeeded;

	SdfHighlightOutline _outlined;          // the outline we're currently driving (local hover/held feedback)

	/// <summary>The outline this machine's local hover/held claim currently owns (null = none). Read by
	/// <see cref="RoundOutlineSystem"/> each StartUpdate so IT can apply the claim before any member renderer
	/// consumes Hidden — our own OnUpdate can land after the renderers' (order is a HashSet), and a claim
	/// applied only there loses the race and never draws. Only ever set on the owning machine.</summary>
	internal SdfHighlightOutline ClaimedOutline => _outlined.IsValid() ? _outlined : null;

	/// <inheritdoc cref="ClaimedOutline"/>
	internal bool ClaimedHeld { get; private set; }

	/// <summary>True while a grab is live — the gun holds fire and HUD can read it.</summary>
	internal bool Holding => State.IsValid();

	/// <summary>The gun's gate: don't shoot while holding a prop, and don't let the LAUNCH click double as a
	/// shot whichever component updates first (order is a HashSet — never rely on it).</summary>
	internal bool SuppressShot => Holding || _launchSuppress;

	/// <summary>Is this pawn currently held by ANY hunter's grab? Read by <see cref="HiderController"/> each
	/// fixed step to enter/leave its ragdoll state. [Sync] state, so it answers on every machine.</summary>
	public static bool IsHeldByHunter( GameObject pawnRoot )
	{
		if ( !pawnRoot.IsValid() || !pawnRoot.Scene.IsValid() )
			return false;

		foreach ( var grabber in pawnRoot.Scene.GetAllComponents<PropGrabber>() )
		{
			var s = grabber.State;
			if ( s.Active && s.Target == pawnRoot )
				return true;
		}
		return false;
	}

	protected override void OnStart()
	{
		_hunter = Components.Get<HunterController>();
		_controller = Components.Get<PlayerController>();
	}

	protected override void OnDisabled()
	{
		RemoveJoint();
		DriveOutline( null, false );
		if ( !IsProxy )
			State = default;
	}

	// Same live-gating as the rest of the pawn: a host-spawned pawn's ownership replicates after OnStart, and a
	// bot is host-owned with nobody home.
	bool Owned => !IsProxy && !(_hunter.IsValid() && _hunter.Bot);

	// When grabbing is allowed at all: play mode (not sculpting your face, not alt-orbiting the camera), controls
	// unlocked, and the same phase gate as the gun — no rearranging the furniture during Hide.
	bool PlayActive => Owned && _controller.IsValid()
		&& !(_hunter.IsValid() && (_hunter.EditMode || _hunter.AltOrbiting))
		&& !RoundManager.ControlsLocked
		&& RoundManager.HuntingAllowed;

	Vector3 EyePos => _controller.IsValid() ? _controller.EyePosition : WorldPosition;
	Vector3 EyeForward => _controller.IsValid() ? _controller.EyeAngles.ToRotation().Forward : WorldRotation.Forward;

	// Owner-side aim for the trace: the hunter's converged crosshair direction (matters in third person, where
	// the eye forward isn't where the dot is). Remote machines never trace — their fixed update holds along the
	// plain replicated eye, which only places the carry point.
	Vector3 AimForward => _hunter.IsValid() ? _hunter.AimDirection : EyeForward;

	protected override void OnUpdate()
	{
		if ( !Owned )
			return;

		if ( _launchSuppress && !Input.Down( "attack1" ) )
			_launchSuppress = false;

		bool play = PlayActive;

		// ── Holding ───────────────────────────────────────────────────────────────────────────────────
		if ( State.IsValid() )
		{
			if ( !play || !Input.Down( "Attack2" ) )
			{
				// Let go — the joint's owner machine sees the cleared state next fixed tick, removes the
				// joint and hands the prop the drag velocity (the flick-throw lives there, not here).
				State = default;
				_preventReselect = true;
			}
			else if ( Input.Pressed( "attack1" ) )
			{
				Launch();
			}

			DriveOutline( State.IsValid() ? State.Target : null, held: true );
			return;
		}

		// ── Between grabs ─────────────────────────────────────────────────────────────────────────────
		if ( _preventReselect )
		{
			if ( !Input.Down( "Attack2" ) && !Input.Down( "attack1" ) )
				_preventReselect = false;

			DriveOutline( null, false );
			return;
		}

		// ── Hover + grab ──────────────────────────────────────────────────────────────────────────────
		GameObject hover = null;
		if ( play )
		{
			var eye = EyePos;
			var tr = Scene.Trace.Ray( eye, eye + AimForward * GrabRange )
				.IgnoreGameObjectHierarchy( GameObject.Root )
				.Run();

			var body = ResolveGrabbable( tr );
			if ( body.IsValid() )
			{
				hover = body.GameObject;

				if ( Input.Pressed( "Attack2" ) )
				{
					// Grab frames copied from the physgun: the hit point in scaled body-local space (scale is
					// built into physics, so strip it consistently), held at the distance it was grabbed.
					var bodyTransform = tr.Body.Transform.WithScale( body.GameObject.WorldScale );
					float distance = Vector3.DistanceBetween( eye, tr.HitPosition );

					State = new GrabState
					{
						Active = true,
						Target = body.GameObject,
						LocalOffset = bodyTransform.PointToLocal( tr.HitPosition ),
						Distance = ClampGrabDistance( body, tr.HitPosition, eye, AimForward, distance ),
					};
				}
			}
		}

		DriveOutline( State.IsValid() ? State.Target : hover, held: State.IsValid() );
	}

	// The joint pump — runs on EVERY machine; only the one where the target's body is locally simulated (not a
	// proxy) actually builds and drives the joint. This is what makes player-prop grabs work with no ownership
	// transfer: the hider's own machine runs this from the hunter's synced state.
	protected override void OnFixedUpdate()
	{
		var state = State;
		var body = state.IsValid() ? state.Target.Components.Get<Rigidbody>() : null;

		if ( !CanMove( body ) )
		{
			ReleaseJoint();
			_launched = false;
			return;
		}

		// Just launched: the impulse RPC can land before the cleared state replicates — never re-joint the prop
		// we just hurled (physgun's _launched flag, same race).
		if ( _launched )
			return;

		_anchor ??= new PhysicsBody( Scene.PhysicsWorld ) { BodyType = PhysicsBodyType.Keyframed, AutoSleep = false };

		var eye = EyePos;
		var fwd = EyeForward;
		var grabWorld = state.Target.WorldTransform.PointToWorld( state.LocalOffset );
		float distance = ClampGrabDistance( body, grabWorld, eye, fwd, state.Distance );
		var target = eye + fwd * distance;

		// Track how fast the carry point is being dragged — this is the "mouse impulse" a release inherits.
		_anchorVel = _anchorSeeded ? ((target - _anchorPos) / Time.Delta).ClampLength( MaxAnchorSpeed ) : Vector3.Zero;
		_anchorSeeded = true;
		_anchorPos = target;
		_anchor.Transform = new Transform( target, Rotation.Identity );

		if ( _joint is null )
		{
			_heldBody = body;

			// Scale is built into physics, remove it (physgun comment, kept verbatim — it's load-bearing).
			var bodyTransform = body.WorldTransform.WithScale( 1.0f );

			var point1 = new PhysicsPoint( _anchor );
			var point2 = new PhysicsPoint( body.PhysicsBody, bodyTransform.PointToLocal( grabWorld ) );

			_joint = PhysicsJoint.CreateControl( point1, point2 );
			_joint.LinearSpring = new PhysicsSpring( SpringFrequency, SpringDamping, HoldForce );
			// NO angular spring: the prop hangs off the grab point and swings freely — the whole "carrying a
			// thing on a soft rope" feel, and why grabbing a chair by a leg makes it dangle leg-up.
			_joint.AngularSpring = new PhysicsSpring( 0f, 0f, 0f );
		}
	}

	protected override void OnDestroy() => RemoveJoint();

	// ── Launch ────────────────────────────────────────────────────────────────────────────────────────

	void Launch()
	{
		var target = State.Target;
		State = default;
		_preventReselect = true;
		_launchSuppress = true; // this same click must not fire the gun, whichever component saw it first

		LaunchRpc( target, AimForward );
	}

	// Broadcast, guarded to the body-owner machine (the physgun's Freeze pattern) — Rpc.Host would miss player
	// props, whose bodies the HIDER's client simulates. Mass-scaled, so everything flies at the same speed.
	[Rpc.Broadcast]
	void LaunchRpc( GameObject target, Vector3 dir )
	{
		_launched = true;
		RemoveJoint();

		var body = target.IsValid() ? target.Components.Get<Rigidbody>() : null;
		if ( !body.IsValid() || body.IsProxy || !body.PhysicsBody.IsValid() )
			return;

		body.ApplyImpulse( dir.Normal * (body.Mass * LaunchSpeed) );
		body.PhysicsBody.ApplyAngularImpulse( Vector3.Random * (body.Mass * LaunchSpin) );
	}

	// ── Joint teardown ────────────────────────────────────────────────────────────────────────────────

	// A NORMAL release (state cleared, joint still up): hand the prop the tracked drag velocity so whipping it
	// around and letting go flings it. Weight-sensitive, unlike the launch: light props inherit the full flick,
	// heavy ones shrug most of it off — consistent with the fixed hold force.
	void ReleaseJoint()
	{
		if ( _joint is not null && !_launched
			&& _heldBody.IsValid() && !_heldBody.IsProxy && _heldBody.PhysicsBody.IsValid() )
		{
			float inherit = Math.Clamp( ThrowMassRef / MathF.Max( _heldBody.Mass, 1f ), 0.15f, 1f );
			var want = _anchorVel * inherit;
			if ( want.Length > _heldBody.Velocity.Length )
				_heldBody.Velocity = Vector3.Lerp( _heldBody.Velocity, want, 0.6f );
		}

		RemoveJoint();
	}

	void RemoveJoint()
	{
		_joint?.Remove();
		_joint = null;
		_anchor?.Remove();
		_anchor = null;
		_heldBody = null;
		_anchorSeeded = false;
	}

	// ── Target rules ──────────────────────────────────────────────────────────────────────────────────

	// What a grab may hold: a converted map prop (GrabbableProp) or a player prop (HiderController pawn).
	// tr.Body.Component is whoever CREATED the physics body — a Rigidbody for dynamic props, the collider
	// itself for static world geometry — so this one check also rejects walls and floors for free.
	static Rigidbody ResolveGrabbable( in SceneTraceResult tr )
	{
		if ( !tr.Hit || tr.Body is null )
			return null;
		if ( tr.Body.Component is not Rigidbody rb || !rb.IsValid() )
			return null;

		var root = rb.GameObject;
		if ( !root.IsValid() )
			return null;

		if ( root.Components.Get<GrabbableProp>().IsValid() )
			return rb;
		if ( root.Components.Get<HiderController>().IsValid() )
			return rb;

		return null;
	}

	static bool CanMove( Rigidbody body )
	{
		if ( !body.IsValid() ) return false;
		if ( body.IsProxy ) return false;              // some other machine simulates it — it runs the joint
		if ( !body.MotionEnabled ) return false;
		if ( !body.PhysicsBody.IsValid() ) return false;
		return true;
	}

	// Physgun's clamp: if the prop's nearest surface would reach within MinHoldDistance of the eye at this
	// grab distance, push the carry point out so a big prop can't be shoved into your face.
	static float ClampGrabDistance( Rigidbody body, Vector3 point, Vector3 eye, Vector3 fwd, float distance )
	{
		distance = MathF.Max( 0f, distance );
		var closest = body.FindClosestPoint( eye );
		var along = distance + Vector3.Dot( closest - point, fwd );
		return along < MinHoldDistance ? distance + (MinHoldDistance - along) : distance;
	}

	// ── Hover / held highlight (local feedback only) ──────────────────────────────────────────────────

	// Tracks the claim and styles it directly. The DIRECT styling only reliably lands in scenes with no
	// RoundOutlineSystem verdict running (menu/debug); in lobby/round scenes the outline's draw decision is
	// consumed from the member renderers' OnUpdate, which can run before ours (order is a HashSet) — there the
	// system re-applies the claim at StartUpdate, before any renderer, via ClaimedOutline. Both writers style
	// through StyleGrabHighlight so they can never disagree.
	void DriveOutline( GameObject target, bool held )
	{
		SdfHighlightOutline outline = null;
		if ( target.IsValid() )
			outline = target.Components.Get<SdfHighlightOutline>( FindMode.EnabledInSelfAndDescendants );

		if ( _outlined.IsValid() && _outlined != outline )
			ClearOutline( _outlined );
		_outlined = outline;
		ClaimedHeld = held;

		if ( outline.IsValid() )
			StyleGrabHighlight( outline, held );
	}

	// Overrides ALL the look slots so player props and decoys highlight identically (authored colours differ —
	// leaving any slot through would be a tell), and the obscured pair stays transparent: a hover must never
	// become a through-wall x-ray.
	internal static void StyleGrabHighlight( SdfHighlightOutline outline, bool held )
	{
		outline.Hidden = false;
		outline.ColorOverride = held ? HeldColor : HoverColor;
		outline.ObscuredColorOverride = Color.Transparent;
		outline.InsideColorOverride = held ? HeldColor.WithAlpha( 0.06f ) : Color.Transparent;
		outline.InsideObscuredColorOverride = Color.Transparent;
		outline.WidthOverride = held ? 4f : 2.5f;
	}

	static void ClearOutline( SdfHighlightOutline outline )
	{
		outline.Hidden = true; // RoundOutlineSystem re-asserts its own verdict next frame either way
		outline.ColorOverride = null;
		outline.ObscuredColorOverride = null;
		outline.InsideColorOverride = null;
		outline.InsideObscuredColorOverride = null;
		outline.WidthOverride = null;
	}
}
