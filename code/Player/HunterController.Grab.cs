using System;
using System.Collections.Generic;

namespace Mimiclay;

// The holder's half of the grab (see PropClaims.Grab for the host half). Owner-only: once the host's Held row
// names our prop, every frame we work out where we want it — floating at a distance along the aim, turned with
// our view's yaw, at the scale we've dialled — and stream that pose to the host, which steers the body there.
// The grab lasts exactly as long as RMB is held: press to pick up, release to drop. While holding: LMB freezes
// it in place (and ends the hold), E+mouse rotates, R+mouse scales, the wheel pushes/pulls. The hover prompts
// are replaced by those verbs as cards under the held prop.
public sealed partial class HunterController
{
	const float GrabMinDistance = 30f;
	const float GrabMaxDistance = 300f;
	const float GrabWheelStep = 12f;     // units per wheel notch
	const float GrabSnapDeg = 90f;       // Shift snap while carrying: square to the view (front/back/sides/top/bottom)
	const float GrabRotateScale = 0.5f;  // E rotate speed relative to edit mode's scrub (BrushScrub.DegPerPx) — calmer for a carry

	GameObject _grabbed;          // the prop the registry says we hold, once seeded
	float _grabDistance;          // eye → hold point
	Rotation _grabRelative;       // the prop's rotation in our view-yaw frame — turning carries it round
	Rotation _grabRaw;            // the E rotate's unsnapped orientation (same frame); Shift applies it snapped
	Vector3 _grabCentre;          // root-local point that sits at the hold point: the clay's centre (the host
	                              // scales the shape about this same point, so it never moves as the prop grows)
	float _grabScale = 1f;        // shape scale relative to the size at grab time (applied to the brushes by the host)
	List<SdfBrush> _grabBase;     // the shape at grab time + the host's pivot for it — only to test a scale step
	Vector3 _grabPivot;           // against the size limits here, so the factor stops AT the limit instead of
	                              // running on past it while the host refuses (see CanScaleTo)

	// A drop/throw is ours the instant we send it, but the host's row lingers a round-trip on a client: ignore
	// the row for the prop we just let go of until it's gone (or this times out). Ended EARLY — the moment the
	// row is seen gone, or we ask for a new grab — or re-grabbing the prop you just dropped sat ignored for the
	// rest of the timeout: granted, but hanging where it was until the latch ran out.
	Guid _grabLetGo;
	RealTimeSince _sinceGrabLetGo = 999f;

	readonly List<InteractChoice> _grabChoices = new();

	/// <summary>Test harness: a grab keeps holding as if RMB were down (scripts can't hold buttons).</summary>
	[ConVar( "mimi_dbg_grab_hold", Help = "Debug: grabs hold as if RMB were held down." )]
	public static bool DebugGrabHold { get; set; }

	/// <summary>Testing: RMB toggles the grab — one press picks up, the next press drops — instead of hold-to-carry.
	/// Local preference (each player's own feel), saved between sessions.</summary>
	[ConVar( "mimi_grab_toggle", ConVarFlags.Saved, Help = "Grab toggles on RMB press (press again to drop) instead of holding RMB." )]
	public static bool GrabToggle { get; set; }

	/// <summary>E or R held over a grabbed prop: the mouse is turning or scaling it, so the view must not move.</summary>
	internal bool GrabLookFrozen => _grabbed.IsValid() && (Input.Down( "Use" ) || Input.Down( "Reload" ));

	/// <summary>Ask the host to put <paramref name="target"/> in our hands (the RMB Grab card).</summary>
	internal void BeginGrab( GameObject target )
	{
		if ( !PropClaims.Current.IsValid() || !target.IsValid() )
			return;

		_grabLetGo = default; // a new request: the next row naming any prop is this grant, not the old hold
		PropClaims.MarkGrabPending( true );
		PropClaims.Current.RequestGrab( target );

		// Anything already in our sim — a loose prop, someone's walking body, a bot, a prop-hunt decoy: hold it now, predicted
		// (SimWorld.PredictHold) — the host's grant becomes the same registration a round trip later and confirms it. Scene
		// clay in creative (converted on the host first) still waits for the grant.
		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
		var simObject = hider.IsValid() ? hider.GameObject : sculpture.IsValid() ? sculpture.GameObject : null;
		if ( simObject.IsValid() && !PropClaims.IsHeldObject( simObject )
			&& Scene.GetSystem<Mimiclay.Sim.SimWorld>() is { } sim && sim.PredictHold( simObject.Id ) )
		{
			_grabPredicted = simObject;
			_sinceGrabPredicted = 0f;
		}
	}

	// The prop we hold on our own say-so until the host's grant names it (see BeginGrab); given up after GrabPredictWindow.
	GameObject _grabPredicted;
	RealTimeSince _sinceGrabPredicted = 999f;
	const float GrabPredictWindow = 1f;

	void EndPredictedGrab( bool revert )
	{
		if ( !_grabPredicted.IsValid() )
			return;
		if ( revert )
			Scene.GetSystem<Mimiclay.Sim.SimWorld>()?.RevertPredictedHold( _grabPredicted.Id );
		_grabPredicted = null;
	}

	/// <summary>Owner, per frame, before the interaction hover: run the hold. True = the grab owns this frame's
	/// input (no hover prompts, and the LMB press was a throw, not a shot).</summary>
	bool UpdateGrab( Vector3 eye )
	{
		var held = PropClaims.LocalHeld;
		if ( !held.IsValid() || held.Id != _grabLetGo )
			_grabLetGo = default; // the old hold's row is gone (or replaced) — nothing left to ignore
		else if ( _sinceGrabLetGo < 1f )
			held = null;

		// A grab we predicted (BeginGrab): hold that prop until the grant names it — or something else, or never.
		if ( held.IsValid() )
			EndPredictedGrab( revert: held != _grabPredicted );
		else if ( _grabPredicted.IsValid() )
		{
			if ( _sinceGrabPredicted < GrabPredictWindow )
				held = _grabPredicted;
			else
			{
				EndPredictedGrab( revert: true );
				EndSimHold();
			}
		}

		if ( !held.IsValid() )
		{
			_grabbed = null;
			EndSimHold(); // the row went without our letting go (its wearer broke free, a sweep): stop sending targets
			return false;
		}

		PropClaims.MarkGrabPending( false );

		// Sculpting (face edit or in place) takes the mouse and the keys — put the prop down first.
		if ( EditMode || LeaseEditing )
		{
			LetGo( held );
			return false;
		}

		// Hold-to-carry: RMB up = let go. Checked before seeding, so a grant that lands after a quick click
		// (a client's round-trip) drops straight back where it was instead of jumping into the hand.
		// With mimi_grab_toggle on, a fresh RMB press drops it instead — never the press that picked it up (that one
		// was this hold's first frame, before it's seeded).
		bool letGo = GrabToggle
			? held == _grabbed && Input.Pressed( "Attack2" )
			: !Input.Down( "Attack2" );
		if ( letGo && !DebugGrabHold )
		{
			LetGo( held );
			return true;
		}

		if ( held != _grabbed )
			SeedGrab( held, eye );

		// Someone's body (see PropClaims.Grab, "players' props"): carried and turned only — the shape is theirs to
		// scale, and a person can't be frozen in place. Prop hunt's inspect grab is carry-and-turn for EVERYTHING
		// (PropClaims.GrabExtras) — the same verbs on a decoy and a player, so the cards can't tell them apart.
		bool body = PropClaims.IsPlayerBody( held.Components.Get<HiderController>() )
			|| !(PropClaims.Current?.GrabExtras ?? true);

		// LMB: freeze it right where it hangs and let go (the hold ends even with RMB still down — re-grabbing
		// takes a fresh press, so the freeze can't be undone by accident).
		if ( Input.Pressed( "Attack1" ) && !body )
		{
			LastFiredSlot = InteractSlot.Fire;
			SinceFired = 0;
			LetGo( held, freeze: true );
			return true;
		}

		_grabDistance = Math.Clamp( _grabDistance + Input.MouseWheel.y * GrabWheelStep, GrabMinDistance, GrabMaxDistance );

		var yawFrame = Rotation.FromYaw( _controller.EyeAngles.yaw );
		if ( Input.Down( "Use" ) )
		{
			// The edit mode's E scrub (BrushScrub's Rotate): mouse X turns about the camera's up, mouse Y about
			// its right, at its degrees per pixel times GrabRotateScale. Accumulated on the RAW orientation; Shift
			// applies it snapped ABSOLUTELY to 90° steps (coarser than edit mode's 22.5° — a carried prop only
			// needs squaring up) in our view-yaw frame, so the grid is "relative to where we're facing":
			// snapped, the prop shows us a clean front, side or top. Releasing Shift mid-turn returns to the
			// raw value, exactly like edit mode.
			var cam = Scene.Camera;
			if ( cam.IsValid() )
			{
				var delta = BrushScrub.CapturedDelta * GrabRotateScale;
				var worldRaw = yawFrame * _grabRaw;
				worldRaw = Rotation.FromAxis( cam.WorldRotation.Up, delta.x * BrushScrub.DegPerPx )
					* Rotation.FromAxis( cam.WorldRotation.Right, delta.y * BrushScrub.DegPerPx )
					* worldRaw;
				_grabRaw = yawFrame.Inverse * worldRaw;
			}

			_grabRelative = Input.Keyboard.Down( "shift" ) ? SculptEditSession.SnapRotation( _grabRaw, GrabSnapDeg ) : _grabRaw;
		}
		else
		{
			// Not turning: whatever was applied sticks (a Shift-snapped turn stays snapped), and the next
			// turn starts from it.
			_grabRaw = _grabRelative;

			// The edit mode's R scrub, same rule: mouse right grows, every ScaleHalfDoublePx doubles.
			if ( Input.Down( "Reload" ) && !body )
			{
				var next = Math.Clamp( _grabScale * MathF.Pow( 2f, BrushScrub.CapturedDelta.x / BrushScrub.ScaleHalfDoublePx ),
					PropClaims.MinGrabScale, PropClaims.MaxGrabScale );
				if ( next != _grabScale && CanScaleTo( held, next ) )
					_grabScale = next;
			}
		}

		// The clay's centre in root space, re-read every frame: a committed scale re-centres the pawn's origin on
		// the new feet (HiderController.RecenterOriginOnShape — root moves, clay counter-shifts, nothing moves in
		// the world), so a centre cached at grab time would yank the prop by that offset. The host scales about
		// this same centre, so it never drifts while R is held.
		_grabCentre = held.WorldTransform.PointToLocal( PropClaims.GrabSpot( held ) );

		// Where we want it: the clay's centre on the aim at the hold distance, turned with our yaw — pulled in to
		// wherever the prop's own shape would first meet the world on the way out (see ClampedRoot), so the
		// target is never somewhere the body can't be.
		var rotation = yawFrame * _grabRelative;
		var root = ClampedRoot( held, eye, rotation );
		// A prop the SIM carries (HiderController.SimHoldApplies): this pose goes out as our sim input every tick, and our own
		// prediction of the hold answers it at once (SimHoldDriver). The UpdateGrab stream below still carries the scale.
		SetSimHold( held, new Transform( root, rotation ) );
		// Here first, for whatever this machine simulates of the hold (a body lent to us, a decoy we predict — see
		// PropClaims.BorrowedBodies / RoundManager.Grab): this frame's target, no round-trip. Then to the host.
		PropClaims.Current.SetLocalGrabTarget( held, root, rotation );
		PropClaims.Current.ApplyLocalGrabScale( held, _grabScale ); // a prop lent to us scales here (no-op otherwise)
		PropClaims.Current.UpdateGrab( held, root, rotation, _grabScale );

		PublishGrabHover( held, root + rotation * _grabCentre, body );
		return true;
	}

	// Keep the hold target (nearly) OUT of the world. A target deep inside a wall or under the floor can never be
	// reached, so the host's chase pushes the body into the surface at full strength every tick while the solver
	// shoves it back — the jitter. Instead, sweep the prop's own collision shape (its live physics body, so the
	// exact collider — carves, scale and all) along the aim from just in front of us out to the chosen distance,
	// at the rotation we're holding it; at the first hit, slide the rest along the surface and press
	// GrabPressDepth into it: a shallow overlap the chase answers with a steady push (depth × the hold pull),
	// so the prop settles flush against the surface instead of hovering a hair off it or being rammed through
	// it. _grabDistance stays the chosen maximum: step back from the obstruction and the prop springs back out.
	//
	// The aim sweep starts a short way out, which can already be inside something when we stand against a
	// wall, look down, or the prop is big; then the fallback sweeps from where the prop actually IS toward the
	// target, sliding along whatever it meets rather than being asked through it — and if even that starts
	// inside something, the target only steps a little way toward where it should be.
	Vector3 ClampedRoot( GameObject held, Vector3 eye, Rotation rotation )
	{
		Transform RootAt( float distance ) => new( eye + _aimDir * distance - rotation * _grabCentre, rotation );

		// Both ends pushed out so the prop's NEAREST surface stays GrabNearClearance in front of the eye, not just
		// its centre (sandbox's PhysicsHold.ClampGrabDistance): a big or scaled-up prop held close otherwise
		// swallows the camera — and it doesn't collide with its holder, so nothing else stops it.
		var rigidbody = held.Components.Get<Rigidbody>();
		float near = ClearOfEye( rigidbody, held, eye, GrabMinDistance );
		float far = MathF.Max( ClearOfEye( rigidbody, held, eye, _grabDistance ), near );
		var desired = RootAt( far ).Position;

		var body = rigidbody?.PhysicsBody;
		if ( body is null || !body.IsValid() )
			return desired;

		// No span to sweep along the aim (a big prop held close: the clearance pushes near out to the hold
		// distance) → straight to the sweep from where the prop is. Never the unswept target: big props close up
		// flipped between it and the swept ones every few frames, and each flip threw the prop (the jitter).
		// An aim sweep that hits at its very START is already touching (looking down, the near end sits on the
		// floor) — treated like starting inside: the slide from there would start in the floor too and only step
		// GrabUnstickStep from the near end, so the target flipped between that and the fallback's every frame
		// at the pitch where the two met (a 29u jump each way: the jitter there).
		bool aimSpan = far > near + 0.5f;
		var aim = aimSpan ? GrabTrace( held ).Sweep( body, RootAt( near ), RootAt( far ) ).Run() : default;
		if ( aimSpan && !aim.StartedSolid && (!aim.Hit || aim.Fraction > 0.001f) )
		{
			if ( !aim.Hit )
				return desired;

			// Blocked along the aim: slide the rest along what it hit, like the fallback below — so both land on
			// the same spot (the hold point's projection onto the surface: looking down, the prop sits on the
			// floor out at the hold distance and comes in as you look further down). Stopping AT the hit pulled
			// it in toward you instead, and the fallback then snapped it back out — two drags, and jitter
			// where they met.
			return RootAt( near ).Position + SlideAndPress( held, body, RootAt( near ), desired - RootAt( near ).Position, aim );
		}

		// The prop AS IT IS (its real rotation — the contact hold may have turned it flush against something, and
		// the aimed rotation placed here would already be inside the floor), its clay centre moved toward where
		// the centre should end up; the target root is then wherever that centre lands, at the aimed rotation.
		var now = held.WorldTransform.WithScale( 1f );
		var centreNow = held.WorldTransform.PointToWorld( _grabCentre );
		var travel = desired + rotation * _grabCentre - centreNow;
		Vector3 RootFor( Vector3 move ) => centreNow + move - rotation * _grabCentre;
		var path = GrabTrace( held ).Sweep( body, now, now.WithPosition( now.Position + travel ) ).Run();

		// Starting inside something too (resting contact, the press, a prop bigger than the gap we're in):
		// there's no clear point to aim for, so ask for a short step toward the target. Not "stay where it
		// is" — a prop merely touching the floor and a counter then could never be lifted off them, and a
		// pressed one flipped between the press and standing still every frame. Not the unclamped target
		// either — that's the full-strength shove into the wall this whole clamp exists to stop. Once the
		// step frees it, the sweeps above take over again. Checked before Hit: a sweep that starts solid
		// needn't report a hit.
		if ( path.StartedSolid )
			return RootFor( travel.ClampLength( GrabUnstickStep ) );

		if ( !path.Hit )
			return desired;

		return RootFor( SlideAndPress( held, body, now, travel, path ) );
	}

	// A sweep from `start` along `travel` hit something: SLIDE the rest of the way along what it hit (one
	// collide-and-slide step), then press into it (GrabPressDepth along the normal). Returns the total move from
	// `start`. Stopping at the hit instead pinned the target ~the press from wherever the prop already was: look
	// down with a big prop and back away, and the prop crawled after you at ~70 u/s, catching up in a rush when
	// you looked up. The slide starts where the sweep stopped (sweeps stop just short of contact); if even that
	// reads as inside something — a second surface it's touching, like a counter's edge above a prop on the
	// floor — it still steps a little along the surface rather than not at all.
	Vector3 SlideAndPress( GameObject held, PhysicsBody body, Transform start, Vector3 travel, SceneTraceResult hit )
	{
		var toContact = travel * hit.Fraction;
		var rest = travel * (1f - hit.Fraction);
		var slide = rest - hit.Normal * Vector3.Dot( rest, hit.Normal );
		var at = start.WithPosition( start.Position + toContact );
		var slid = GrabTrace( held ).Sweep( body, at, at.WithPosition( at.Position + slide ) ).Run();
		var moved = toContact + (slid.StartedSolid ? slide.ClampLength( GrabUnstickStep ) : slide * slid.Fraction);

		// The press scales with how hard you PUSH: how far past the surface the aim wants the prop (the part of
		// the travel the slide took away), up to GrabPressDepth. Touching lightly rests the prop against the
		// surface at the angle you hold it; aim further in and it levers flush. A fixed full press snapped
		// everything flush the instant it touched.
		float push = MathF.Max( Vector3.Dot( rest, -hit.Normal ), 0f );
		return moved - hit.Normal * MathF.Min( push * GrabPushScale, GrabPressDepth );
	}

	// The hold distance (eye → clay centre along the aim), lengthened if the prop's closest point to the eye would
	// sit nearer than GrabNearClearance along the aim. Measured on the body as it is now — close enough, since
	// the prop is turning with the view and its extent past the centre barely changes frame to frame.
	float ClearOfEye( Rigidbody rigidbody, GameObject held, Vector3 eye, float distance )
	{
		if ( !rigidbody.IsValid() )
			return distance;

		var centre = held.WorldTransform.PointToWorld( _grabCentre );
		var closest = rigidbody.FindClosestPoint( eye );
		float along = distance + Vector3.Dot( closest - centre, _aimDir );
		return along < GrabNearClearance ? distance + (GrabNearClearance - along) : distance;
	}

	// How far in front of the eye (along the aim) the held prop's nearest surface must stay.
	const float GrabNearClearance = 24f;

	// The most "press" there is: how far into the surface hit the target goes, straight along its normal, when
	// you push hard (see SlideAndPress — it scales with the push) — the same in both of ClampedRoot's branches,
	// so switching between them doesn't move the target. It's what levers a prop flush (the contact hold chases
	// it at × HoldPull). Measured (copper pot tilted 30°, pressed into the floor): ≤10 → stays ~23°, 12 →
	// 0.0°, resting at ~7 u/s; along the travel instead didn't help.
	const float GrabPressDepth = 12f;

	// Press per unit of push (see SlideAndPress): 1 still snapped props flush on a light touch; at 0.25 the
	// full press needs the aim ~48u into the surface.
	const float GrabPushScale = 0.25f;

	// How far toward the target a stuck prop (both sweeps start inside something) is asked to move per frame.
	const float GrabUnstickStep = 8f;

	// What a held prop is swept against: the world as its body collides with it, minus the prop itself, us,
	// and things nothing physical stops at.
	SceneTrace GrabTrace( GameObject held ) => Scene.Trace
		.IgnoreGameObjectHierarchy( held )
		.IgnoreGameObjectHierarchy( GameObject )
		.WithoutTags( "trigger", "headcollider", "water" );

	void SeedGrab( GameObject held, Vector3 eye )
	{
		_grabbed = held;

		var centre = PropClaims.GrabSpot( held ); // the middle of its clay (its mass centre)
		_grabCentre = held.WorldTransform.PointToLocal( centre );
		_grabScale = 1f;

		// Mirror the host's scale snapshot (PropClaims.ApplyGrabScale): authored brushes + the symmetric pivot.
		var clay = held.Components.Get<HiderController>()?.DisguiseSculpture;
		_grabBase = null;
		if ( clay.IsValid() && clay.Brushes is { Count: > 0 } all && Sdf.TryGetBounds( all, out var bounds ) )
		{
			_grabBase = all.GetRange( 0, clay.AuthoredBrushCount ).Select( b => b.Copy() ).ToList();
			_grabPivot = SdfScaling.SymmetricPivot( _grabBase, bounds.Center );
		}
		_grabDistance = Math.Clamp( eye.Distance( centre ), GrabMinDistance, GrabMaxDistance );
		_grabRelative = Rotation.FromYaw( _controller.EyeAngles.yaw ).Inverse * held.WorldRotation;
		_grabRaw = _grabRelative;
	}

	// May the scale step to this factor? The exact rule the host applies (PropClaims.GrabScaleStepAllowed:
	// stop at the limits, but a prop already outside them may always move back toward them), so the holder's
	// factor stops exactly where the prop does.
	bool CanScaleTo( GameObject held, float factor )
	{
		if ( _grabBase is null )
			return false;

		var limits = held.Components.Get<SculptBounds>( FindMode.EverythingInSelfAndDescendants );
		return PropClaims.GrabScaleStepAllowed( limits, _grabBase, _grabPivot, _grabScale, factor );
	}

	// ── The hold as sim input (phase 3 — SimHoldDriver) ───────────────────────────────────────────────────────
	// While we carry a prop the sim owns, this machine's sim input carries the pose we want it at: the sim predicts the
	// hold here and the host simulates it from the same input. Letting go simply stops sending it (the driver lets the
	// body fly on), so a drop or a throw is immediate too.
	Transform _simHoldTarget;
	bool _simHolding;
	Func<int, Mimiclay.Sim.SimInput> _simHoldInput;

	void SetSimHold( GameObject held, Transform target )
	{
		_simHoldTarget = target;
		// Only once OUR sim has the body in our hands (a pawn or a decoy — predicted, or the host's grant): inputs are stamped
		// by tick, so a target sent before that had the host moving the prop for ticks our prediction never ran (measured:
		// 15 replays, corrections to 65u at every grab start). Until then the host holds it still (SimCtrl.Holding).
		var sim = Scene.GetSystem<Mimiclay.Sim.SimWorld>();
		bool applies = sim is { Active: true } && sim.Find( held.Id ) is { } b && b.Spec.Driver == Mimiclay.Sim.SimHoldDriver.Id && b.IsLocalOwned;
		if ( applies == _simHolding )
			return;
		_simHolding = applies;
		if ( sim is null )
			return;
		_simHoldInput ??= ReadSimHoldInput;
		if ( applies )
			sim.LocalInput = _simHoldInput;
		else if ( sim.LocalInput == _simHoldInput )
			sim.LocalInput = null;
	}

	Mimiclay.Sim.SimInput ReadSimHoldInput( int tick )
		=> _simHolding
			? new Mimiclay.Sim.SimInput { Buttons = Mimiclay.Sim.SimInput.Hold, HoldPos = _simHoldTarget.Position, HoldRot = _simHoldTarget.Rotation }
			: Mimiclay.Sim.SimInput.None;

	void EndSimHold( GameObject held = null )
	{
		if ( !_simHolding )
			return;
		_simHolding = false;
		var sim = Scene.GetSystem<Mimiclay.Sim.SimWorld>();
		if ( sim is null )
			return;
		if ( sim.LocalInput == _simHoldInput )
			sim.LocalInput = null; // the next ticks carry no target: the prop is let go of, here and on the host
		if ( held.IsValid() )
			sim.PredictRelease( held.Id ); // loose here from this tick — the host switches at the same tick of our inputs
	}

	void LetGo( GameObject held, bool freeze = false )
	{
		_grabLetGo = held.Id;
		_sinceGrabLetGo = 0f;
		_grabbed = null;
		EndPredictedGrab( revert: false ); // let go of before the grant: the host's redrive (or its absence) settles it
		EndSimHold( held );
		PropClaims.MarkGrabPending( false );
		PropClaims.Current?.EndLocalGrabScale( held ); // lands a scale we applied ourselves on a lent prop
		PropClaims.Current?.ClearLocalGrabTarget( held ); // stop holding it here this tick (a throw keeps its swing)
		if ( freeze )
			PropClaims.MarkLocalFreeze( held ); // frozen here this tick, not a round-trip later (no little drop)
		PropClaims.Current?.DropGrab( freeze );
	}

	// The held prop's verbs, as the crosshair's cards (and its outline, through the local hover).
	void PublishGrabHover( GameObject held, Vector3 at, bool body )
	{
		var hider = held.Components.Get<HiderController>();
		var ctx = new InteractContext
		{
			Hunter = this,
			Target = held,
			Sculpture = hider.IsValid() ? hider.DisguiseSculpture : null,
			HitPosition = at,
		};

		if ( _grabChoices.Count == 0 )
		{
			_grabChoices.Add( new InteractChoice( GrabHints.Instance, new InteractOption( "grab.rotate", "Rotate", InteractSlot.Primary ) ) );
			_grabChoices.Add( new InteractChoice( GrabHints.Instance, new InteractOption( "grab.freeze", "Freeze", InteractSlot.Fire ) ) );
			_grabChoices.Add( new InteractChoice( GrabHints.Instance, new InteractOption( "grab.scale", "Scale", InteractSlot.Scale ) ) );
		}

		_hoverChoices.Clear();
		if ( body )
			_hoverChoices.Add( _grabChoices[0] ); // a body only turns
		else
			_hoverChoices.AddRange( _grabChoices );
		HoverContext = ctx;
		Interactions.PublishLocal( ctx, _hoverChoices );
	}

	// The cards while holding are hints — the grab reads its keys itself, so choosing one does nothing here.
	sealed class GrabHints : IInteractable
	{
		public static readonly GrabHints Instance = new();
		public void GetInteractions( in InteractContext ctx, List<InteractOption> options ) { }
		public void Interact( in InteractContext ctx, string optionId ) { }
	}
}
