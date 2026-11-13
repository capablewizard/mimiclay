using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// The holder's half of the grab (see PropClaims.Grab for the host half), shared by every pawn that can carry things —
/// the hunter (<see cref="HunterController"/>) and, in creative, a prop (<see cref="HiderController"/>). Owner-only: once
/// the host's Held row names our prop, every frame we work out where we want it — floating at a distance along the aim,
/// turned with our view's yaw, at the scale we've dialled — and stream that pose to the host, which steers the body there
/// (and, for a body the sim carries, our own sim input predicts the same hold — SimHoldDriver).
///
/// The grab lasts exactly as long as RMB is held: press to pick up, release to drop. While holding: LMB freezes it in
/// place (and ends the hold), E+mouse rotates, R+mouse scales, the wheel pushes/pulls. The hover prompts are replaced by
/// those verbs as cards under the held prop (<see cref="FillHints"/>).
///
/// A plain object the pawn keeps: <see cref="Begin"/> from the Grab card, <see cref="Update"/> once per owner frame with
/// the pawn's eye and aim, <see cref="WriteSimInput"/> from the pawn's sim input reader, <see cref="Release"/> whenever the
/// pawn stops being able to hold (free cam, destroyed). The pawn tells it how to re-point the sim's local input when the
/// hold starts or stops riding it (the constructor's callback).
/// </summary>
public sealed class GrabHolder
{
	public const float MinDistance = 30f;
	public const float MaxDistance = 300f;
	const float WheelStep = 12f;     // units per wheel notch
	const float SnapDeg = 90f;       // Shift snap while carrying: square to the view (front/back/sides/top/bottom)
	const float RotateScale = 0.5f;  // E rotate speed relative to edit mode's scrub (BrushScrub.DegPerPx) — calmer for a carry

	/// <summary>How far in front of the eye (along the aim) a held prop's nearest surface stays, for a pawn whose eye is
	/// a face (the hunter). A prop holder passes its own reach past its centre instead.</summary>
	public const float EyeClearance = 24f;

	readonly Component _owner;
	readonly Action _simInputChanged;

	public GrabHolder( Component owner, Action simInputChanged )
	{
		_owner = owner;
		_simInputChanged = simInputChanged;
	}

	Scene Scene => _owner.Scene;

	GameObject _grabbed;          // the prop the registry says we hold, once seeded
	float _distance;              // eye → hold point
	Rotation _relative;           // the prop's rotation in our view-yaw frame — turning carries it round
	Rotation _raw;                // the E rotate's unsnapped orientation (same frame); Shift applies it snapped
	Vector3 _centre;              // root-local point that sits at the hold point: the GRIP (re-derived every frame
	                              // from _gripClay — see Update)
	Vector3 _gripClay;            // the grip in the CLAY's own space (the brushes' — survives origin rebases), at grab-time size
	Vector3? _pendingGrip;        // world point the crosshair met the clay at, from Begin until Seed takes it
	RealTimeSince _sincePendingGrip;
	float _scale = 1f;            // shape scale relative to the size at grab time (applied to the brushes by the host)
	List<SdfBrush> _base;         // the shape at grab time + the host's pivot for it — only to test a scale step
	Vector3 _pivot;               // against the size limits here, so the factor stops AT the limit instead of
	                              // running on past it while the host refuses (see CanScaleTo)

	// This frame's aim and near clearance (Update's arguments), read by the sweeps.
	Vector3 _aim;
	float _clearance = EyeClearance;
	float _minDistance = MinDistance;

	// A drop/throw is ours the instant we send it, but the host's row lingers a round-trip on a client: ignore
	// the row for the prop we just let go of until it's gone (or this times out). Ended EARLY — the moment the
	// row is seen gone, or we ask for a new grab — or re-grabbing the prop you just dropped sat ignored for the
	// rest of the timeout: granted, but hanging where it was until the latch ran out.
	Guid _letGo;
	RealTimeSince _sinceLetGo = 999f;

	readonly List<InteractChoice> _choices = new();

	/// <summary>The prop in our hands (seeded), or null.</summary>
	public GameObject Grabbed => _grabbed;

	/// <summary>E or R held over a grabbed prop: the mouse is turning or scaling it, so the view must not move.</summary>
	public bool LookFrozen => _grabbed.IsValid() && (Input.Down( "Use" ) || Input.Down( "Reload" ));

	/// <summary>Set for the frame an LMB freeze ended the hold.</summary>
	public bool FrozeThisFrame { get; private set; }

	/// <summary>The hint card whose key went down this frame — E Rotate, R Scale, LMB Freeze — or null. The pawn stamps it as
	/// its LastFiredSlot so the crosshair springs that card: the cards are hints (the hold reads its keys itself), so the
	/// hover's own press dispatch never sees these presses.</summary>
	public InteractSlot? PressedSlot { get; private set; }

	/// <summary>Where the held prop's verbs hang this frame (its clay centre at the target), and whether it's someone's
	/// body / an inspect grab (turn only) — valid while <see cref="Update"/> returned true with a prop in hand.</summary>
	public Vector3 HintAt { get; private set; }
	public bool HintTurnOnly { get; private set; }

	/// <summary>Ask the host to put <paramref name="target"/> in our hands (the RMB Grab card). <paramref name="grip"/>: where
	/// the crosshair met it (world) — the point it's held by and turns about; none = its mass centre.</summary>
	public void Begin( GameObject target, Vector3? grip = null )
	{
		if ( !PropClaims.Current.IsValid() || !target.IsValid() )
			return;

		_pendingGrip = grip;
		_sincePendingGrip = 0f;

		_letGo = default; // a new request: the next row naming any prop is this grant, not the old hold
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
			_predicted = simObject;
			_sincePredicted = 0f;
		}
	}

	// The prop we hold on our own say-so until the host's grant names it (see Begin); given up after PredictWindow.
	GameObject _predicted;
	RealTimeSince _sincePredicted = 999f;
	const float PredictWindow = 1f;

	void EndPredicted( bool revert )
	{
		if ( !_predicted.IsValid() )
			return;
		if ( revert )
			Scene.GetSystem<Mimiclay.Sim.SimWorld>()?.RevertPredictedHold( _predicted.Id );
		_predicted = null;
	}

	/// <summary>Owner, per frame, before the interaction hover: run the hold. True = the grab owns this frame's input (no
	/// hover prompts, and the LMB press was a freeze, not a shot). <paramref name="eye"/> + <paramref name="aim"/> are the
	/// crosshair's ray, <paramref name="viewYaw"/> the frame the prop turns with, <paramref name="editing"/> drops it
	/// (sculpting takes the mouse and keys), <paramref name="clearance"/> how far ahead of the eye the prop's nearest
	/// surface must stay (<see cref="EyeClearance"/> for a face; a prop holder's own reach past its centre).</summary>
	public bool Update( Vector3 eye, Vector3 aim, float viewYaw, bool editing, float clearance = EyeClearance )
	{
		FrozeThisFrame = false;
		PressedSlot = null;
		_aim = aim;
		_clearance = clearance;
		_minDistance = MathF.Max( MinDistance, clearance );

		var held = PropClaims.LocalHeld;
		if ( !held.IsValid() || held.Id != _letGo )
			_letGo = default; // the old hold's row is gone (or replaced) — nothing left to ignore
		else if ( _sinceLetGo < 1f )
			held = null;

		// A grab we predicted (Begin): hold that prop until the grant names it — or something else, or never.
		if ( held.IsValid() )
			EndPredicted( revert: held != _predicted );
		else if ( _predicted.IsValid() )
		{
			if ( _sincePredicted < PredictWindow )
				held = _predicted;
			else
			{
				EndPredicted( revert: true );
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
		if ( editing )
		{
			LetGo( held );
			return false;
		}

		// Hold-to-carry: RMB up = let go. Checked before seeding, so a grant that lands after a quick click
		// (a client's round-trip) drops straight back where it was instead of jumping into the hand.
		// With mimi_grab_toggle on, a fresh RMB press drops it instead — never the press that picked it up (that one
		// was this hold's first frame, before it's seeded).
		bool letGo = HunterController.GrabToggle
			? held == _grabbed && Input.Pressed( "Attack2" )
			: !Input.Down( "Attack2" );
		if ( letGo && !HunterController.DebugGrabHold )
		{
			LetGo( held );
			return true;
		}

		if ( held != _grabbed )
			Seed( held, eye, viewYaw );

		// Someone's body (see PropClaims.Grab, "players' props"): carried and turned only — the shape is theirs to
		// scale, and a person can't be frozen in place. Prop hunt's inspect grab is carry-and-turn for EVERYTHING
		// (PropClaims.GrabExtras) — the same verbs on a decoy and a player, so the cards can't tell them apart.
		bool body = PropClaims.IsPlayerBody( held.Components.Get<HiderController>() )
			|| !(PropClaims.Current?.GrabExtras ?? true);

		// The cards' press feedback — only for the cards actually shown (a body shows Rotate alone; see FillHints).
		if ( Input.Pressed( "Use" ) )
			PressedSlot = InteractSlot.Primary;
		else if ( Input.Pressed( "Reload" ) && !body )
			PressedSlot = InteractSlot.Scale;

		// LMB: freeze it right where it hangs and let go (the hold ends even with RMB still down — re-grabbing
		// takes a fresh press, so the freeze can't be undone by accident).
		if ( Input.Pressed( "Attack1" ) && !body )
		{
			FrozeThisFrame = true;
			PressedSlot = InteractSlot.Fire;
			LetGo( held, freeze: true );
			return true;
		}

		_distance = Math.Clamp( _distance + Input.MouseWheel.y * WheelStep, _minDistance, MathF.Max( MaxDistance, _minDistance ) );

		var yawFrame = Rotation.FromYaw( viewYaw );
		if ( Input.Down( "Use" ) )
		{
			// The edit mode's E scrub (BrushScrub's Rotate): mouse X turns about the camera's up, mouse Y about
			// its right, at its degrees per pixel times RotateScale. Accumulated on the RAW orientation; Shift
			// applies it snapped ABSOLUTELY to 90° steps (coarser than edit mode's 22.5° — a carried prop only
			// needs squaring up) in our view-yaw frame, so the grid is "relative to where we're facing":
			// snapped, the prop shows us a clean front, side or top. Releasing Shift mid-turn returns to the
			// raw value, exactly like edit mode.
			var cam = Scene.Camera;
			if ( cam.IsValid() )
			{
				var delta = BrushScrub.CapturedDelta * RotateScale;
				var worldRaw = yawFrame * _raw;
				worldRaw = Rotation.FromAxis( cam.WorldRotation.Up, delta.x * BrushScrub.DegPerPx )
					* Rotation.FromAxis( cam.WorldRotation.Right, delta.y * BrushScrub.DegPerPx )
					* worldRaw;
				_raw = yawFrame.Inverse * worldRaw;
			}

			_relative = Input.Keyboard.Down( "shift" ) ? SculptEditSession.SnapRotation( _raw, SnapDeg ) : _raw;
		}
		else
		{
			// Not turning: whatever was applied sticks (a Shift-snapped turn stays snapped), and the next
			// turn starts from it.
			_raw = _relative;

			// The edit mode's R scrub, same rule: mouse right grows, every ScaleHalfDoublePx doubles.
			if ( Input.Down( "Reload" ) && !body )
			{
				var next = Math.Clamp( _scale * MathF.Pow( 2f, BrushScrub.CapturedDelta.x / BrushScrub.ScaleHalfDoublePx ),
					PropClaims.MinGrabScale, PropClaims.MaxGrabScale );
				if ( next != _scale && CanScaleTo( held, next ) )
				{
					_scale = next;
					PreviewScale( held );
				}
			}
		}

		// The grip in root space, re-derived every frame from the CLAY's space: a committed scale re-centres the pawn's origin
		// on the new feet (HiderController.RecenterOriginOnShape — root moves, clay counter-shifts, nothing moves in the
		// world), so a root-local grip cached at grab time would yank the prop by that offset. Scaled with the clay about the
		// host's pivot (PropClaims.ApplyGrabScale), so the spot you hold stays the same spot on the clay as it grows.
		_centre = held.WorldTransform.PointToLocal( ClayOf( held ).WorldTransform.PointToWorld( GripInClay() ) );

		// Where we want it: the grip on the aim at the hold distance, turned with our yaw — pulled in to
		// wherever the prop's own shape would first meet the world on the way out (see ClampedRoot), so the
		// target is never somewhere the body can't be.
		var rotation = yawFrame * _relative;
		var root = ClampedRoot( held, eye, rotation );
		// A prop the SIM carries (HiderController.SimHoldApplies): this pose goes out as our sim input every tick, and our own
		// prediction of the hold answers it at once (SimHoldDriver). The UpdateGrab stream below still carries the scale.
		SetSimHold( held, new Transform( root, rotation ) );
		PropClaims.Current.UpdateGrab( held, root, rotation, _scale, _centre ); // the fallback hold's target + grip, and the scale (host)

		HintAt = root + rotation * _centre;
		HintTurnOnly = body;
		return true;
	}

	// Keep the hold target (nearly) OUT of the world. A target deep inside a wall or under the floor can never be
	// reached, so the host's chase pushes the body into the surface at full strength every tick while the solver
	// shoves it back — the jitter. Instead, sweep the prop's own collision shape (its live physics body, so the
	// exact collider — carves, scale and all) along the aim from just in front of us out to the chosen distance,
	// at the rotation we're holding it; at the first hit, slide the rest along the surface and press
	// PressDepth into it: a shallow overlap the chase answers with a steady push (depth × the hold pull),
	// so the prop settles flush against the surface instead of hovering a hair off it or being rammed through
	// it. _distance stays the chosen maximum: step back from the obstruction and the prop springs back out.
	//
	// The aim sweep starts a short way out, which can already be inside something when we stand against a
	// wall, look down, or the prop is big; then the fallback sweeps from where the prop actually IS toward the
	// target, sliding along whatever it meets rather than being asked through it — and if even that starts
	// inside something, the target only steps a little way toward where it should be.
	[ConVar( "mimi_dbg_grab", Help = "Debug: log the grab's hold every 0.1 s — the aim distance, where the clamp put the target, and why." )]
	public static bool DebugLog { get; set; }
	string _clampWhy = "";
	RealTimeSince _sinceDebugLog;

	Vector3 ClampedRoot( GameObject held, Vector3 eye, Rotation rotation )
	{
		var root = ClampedRootInner( held, eye, rotation );
		if ( DebugLog && _sinceDebugLog > 0.1f )
		{
			_sinceDebugLog = 0f;
			var grip = held.WorldTransform.PointToWorld( _centre );
			var target = root + rotation * _centre;
			var want = eye + _aim * _distance;
			Log.Info( $"[grab] {held.Name}: grip now {eye.Distance( grip ):F0}u from eye, aim wants {_distance:F0}u → target {eye.Distance( target ):F0}u "
				+ $"({want.Distance( target ):F0}u off the aim point) via {_clampWhy} | grip(root) {_centre} | pitch {Rotation.LookAt( _aim ).Pitch():F0}" );
		}
		return root;
	}

	Vector3 ClampedRootInner( GameObject held, Vector3 eye, Rotation rotation )
	{
		Transform RootAt( float distance ) => new( eye + _aim * distance - rotation * _centre, rotation );

		// Both ends pushed out so the prop's NEAREST surface stays the clearance in front of the eye, not just
		// its centre (sandbox's PhysicsHold.ClampGrabDistance): a big or scaled-up prop held close otherwise
		// swallows the camera — and it doesn't collide with its holder, so nothing else stops it.
		var rigidbody = held.Components.Get<Rigidbody>();
		float near = ClearOfEye( rigidbody, held, eye, _minDistance );
		float far = MathF.Max( ClearOfEye( rigidbody, held, eye, _distance ), near );
		var desired = RootAt( far ).Position;

		var body = rigidbody?.PhysicsBody;
		_clampWhy = near > _minDistance + 0.5f || far > _distance + 0.5f ? $"eye clearance (near {near:F0}, far {far:F0})" : "clear";
		if ( body is null || !body.IsValid() )
		{
			_clampWhy = "no body";
			return desired;
		}

		// No span to sweep along the aim (a big prop held close: the clearance pushes near out to the hold
		// distance) → straight to the sweep from where the prop is. Never the unswept target: big props close up
		// flipped between it and the swept ones every few frames, and each flip threw the prop (the jitter).
		// An aim sweep that hits at its very START is already touching (looking down, the near end sits on the
		// floor) — treated like starting inside: the slide from there would start in the floor too and only step
		// UnstickStep from the near end, so the target flipped between that and the fallback's every frame
		// at the pitch where the two met (a 29u jump each way: the jitter there).
		bool aimSpan = far > near + 0.5f;
		var aim = aimSpan ? Sweep( held, body, RootAt( near ), RootAt( far ) ) : default;
		if ( aimSpan && !aim.StartedSolid && (!aim.Hit || aim.Fraction > 0.001f) )
		{
			if ( !aim.Hit )
				return desired;
			_clampWhy = $"aim sweep hit '{aim.GameObject?.Name}' at {aim.Fraction:F2}";

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
		var centreNow = held.WorldTransform.PointToWorld( _centre );
		var travel = desired + rotation * _centre - centreNow;
		Vector3 RootFor( Vector3 move ) => centreNow + move - rotation * _centre;
		var path = Sweep( held, body, now, now.WithPosition( now.Position + travel ) );

		// Starting inside something too (resting contact, the press, a prop bigger than the gap we're in):
		// there's no clear point to aim for, so ask for a short step toward the target. Not "stay where it
		// is" — a prop merely touching the floor and a counter then could never be lifted off them, and a
		// pressed one flipped between the press and standing still every frame. Not the unclamped target
		// either — that's the full-strength shove into the wall this whole clamp exists to stop. Once the
		// step frees it, the sweeps above take over again. Checked before Hit: a sweep that starts solid
		// needn't report a hit.
		var aimWhy = !aimSpan ? "no aim span" : aim.StartedSolid ? $"aim sweep started inside '{aim.GameObject?.Name}'" : "aim sweep hit at its start";
		if ( path.StartedSolid )
		{
			_clampWhy = $"{aimWhy}; STUCK inside '{path.GameObject?.Name}' (step {UnstickStep}u)";
			return RootFor( travel.ClampLength( UnstickStep ) );
		}

		if ( !path.Hit )
		{
			_clampWhy = $"{aimWhy}; path from the prop clear";
			return desired;
		}

		_clampWhy = $"{aimWhy}; path from the prop hit '{path.GameObject?.Name}' at {path.Fraction:F2}";
		return RootFor( SlideAndPress( held, body, now, travel, path ) );
	}

	// A sweep from `start` along `travel` hit something: SLIDE the rest of the way along what it hit (one
	// collide-and-slide step), then press into it (PressDepth along the normal). Returns the total move from
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
		var slid = Sweep( held, body, at, at.WithPosition( at.Position + slide ) );
		var moved = toContact + (slid.StartedSolid ? slide.ClampLength( UnstickStep ) : slide * slid.Fraction);

		// The press scales with how hard you PUSH: how far past the surface the aim wants the prop (the part of
		// the travel the slide took away), up to PressDepth. Touching lightly rests the prop against the
		// surface at the angle you hold it; aim further in and it levers flush. A fixed full press snapped
		// everything flush the instant it touched.
		float push = MathF.Max( Vector3.Dot( rest, -hit.Normal ), 0f );
		return moved - hit.Normal * MathF.Min( push * PushScale, PressDepth );
	}

	// The hold distance (eye → clay centre along the aim), lengthened if the prop's closest point to the eye would
	// sit nearer than the clearance along the aim. Measured on the body as it is now — close enough, since
	// the prop is turning with the view and its extent past the centre barely changes frame to frame.
	float ClearOfEye( Rigidbody rigidbody, GameObject held, Vector3 eye, float distance )
	{
		if ( !rigidbody.IsValid() )
			return distance;

		var centre = held.WorldTransform.PointToWorld( _centre );
		var closest = rigidbody.FindClosestPoint( eye );
		float along = distance + Vector3.Dot( closest - centre, _aim );
		return along < _clearance ? distance + (_clearance - along) : distance;
	}

	// The most "press" there is: how far into the surface hit the target goes, straight along its normal, when
	// you push hard (see SlideAndPress — it scales with the push) — the same in both of ClampedRoot's branches,
	// so switching between them doesn't move the target. It's what levers a prop flush (the contact hold chases
	// it at × HoldPull). Measured (copper pot tilted 30°, pressed into the floor): ≤10 → stays ~23°, 12 →
	// 0.0°, resting at ~7 u/s; along the travel instead didn't help.
	const float PressDepth = 12f;

	// Press per unit of push (see SlideAndPress): 1 still snapped props flush on a light touch; at 0.25 the
	// full press needs the aim ~48u into the surface.
	const float PushScale = 0.25f;

	// How far toward the target a stuck prop (both sweeps start inside something) is asked to move per frame.
	const float UnstickStep = 8f;

	// What a held prop is swept against: the world as its body collides with it, minus the prop itself, us,
	// and things nothing physical stops at.
	//
	// LOOSE props are left out too: they're free bodies — the held prop's physics pushes them aside, or they ride on it —
	// and the clamp only exists to keep the target out of things that WON'T move. Swept against, a cushion sitting on a
	// stool (woken with it — PropContacts) made every sweep of the stool start inside it: the hold fell into its stuck
	// creep and the stool couldn't be lifted or follow the aim (user, 2026-11-12: "when I pick it up it flies towards me").
	SceneTrace Trace( GameObject held )
	{
		var trace = Scene.Trace
			.IgnoreGameObjectHierarchy( held )
			.IgnoreGameObjectHierarchy( _owner.GameObject )
			.WithoutTags( "trigger", "headcollider", "water" );
		if ( PropClaims.Current is { } claims )
		{
			foreach ( var (id, _) in claims.LooseProps )
			{
				if ( id == held.Id || Scene.Directory.FindByGuid( id ) is not { } loose || !loose.IsValid() )
					continue;
				if ( loose.WorldPosition.DistanceSquared( held.WorldPosition ) < LooseIgnoreRadius * LooseIgnoreRadius )
					trace = trace.IgnoreGameObjectHierarchy( loose );
			}
		}
		return trace;
	}

	// Loose props further than this from the held one can't be in its sweeps' way (the hold reaches MaxDistance from the eye).
	const float LooseIgnoreRadius = 800f;

	// Sweep the held prop from `from` to `to` (root transforms). A prop pawn sweeps its single convex move hull: the live
	// body is the full compound collider — a leafy prop (lettuce) is 217 hulls at ~16 ms a sweep, and a frame here runs
	// two, which pinned Update at ~20 ms while carrying it. The hull encloses the clay, so the clamp only gets slightly
	// more conservative (concavities ignored — the same trade the prop's own movement makes). A prop pawn with no hull yet
	// (nothing built) reports a clear path rather than paying for the compound body: the clamp just skips that frame.
	// Anything else (a scene decoy held as it is): the body.
	SceneTraceResult Sweep( GameObject held, PhysicsBody body, Transform from, Transform to )
	{
		var trace = Trace( held );
		if ( held.Components.Get<HiderController>() is { } hider && hider.IsValid() )
			return hider.TrySweepMoveHull( trace, from, to, out var tr ) ? tr : default;
		return trace.Sweep( body, from, to ).Run();
	}

	// The object whose space the brushes live in: a pawn's disguise clay, else (a scene decoy held as it is) the object.
	// A pawn converted this frame hasn't run OnStart yet, so DisguiseSculpture isn't bound — find the clay itself, or the grip
	// was stored in ROOT space and read back in clay space a frame later: the prop jumped by the disguise's offset at every grab.
	static GameObject ClayOf( GameObject held )
	{
		if ( held.Components.Get<HiderController>()?.DisguiseSculpture is { } bound && bound.IsValid() )
			return bound.GameObject;
		var clay = held.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndDescendants );
		return clay.IsValid() ? clay.GameObject : held;
	}

	// The grip in clay space at the current scale (the host scales the brushes about _pivot — see PropClaims.ApplyGrabScale).
	Vector3 GripInClay() => _base is null ? _gripClay : _pivot + (_gripClay - _pivot) * _scale;

	void Seed( GameObject held, Vector3 eye, float viewYaw )
	{
		_grabbed = held;

		// The GRIP: where the crosshair met the clay at the grab (Begin) — the point it hangs and turns about, so a broom held
		// by its end swings from the end. Scene clay converted for the grab is replaced by a pawn wearing the same clay at the
		// same spot, so the world point still lands on it. No hit (or a stale one): its mass centre, as before.
		var clayGo = ClayOf( held );
		var grip = _pendingGrip is { } hit && _sincePendingGrip < 3f ? hit : PropClaims.GrabSpot( held );
		_pendingGrip = null;
		_gripClay = clayGo.WorldTransform.PointToLocal( grip );
		_centre = held.WorldTransform.PointToLocal( grip );
		var centre = grip;
		_scale = 1f;

		// Mirror the host's scale snapshot (PropClaims.ApplyGrabScale): authored brushes + the symmetric pivot.
		var clay = held.Components.Get<HiderController>()?.DisguiseSculpture;
		_base = null;
		if ( clay.IsValid() && clay.Brushes is { Count: > 0 } all && Sdf.TryGetBounds( all, out var bounds ) )
		{
			_base = all.GetRange( 0, clay.AuthoredBrushCount ).Select( b => b.Copy() ).ToList();
			_pivot = SdfScaling.SymmetricPivot( _base, bounds.Center );
		}
		_distance = Math.Clamp( eye.Distance( centre ), _minDistance, MathF.Max( MaxDistance, _minDistance ) );
		if ( HunterController.DebugGrabHold ) // the chase bot holds its catch out at arm's length rather than wherever it closed in to
			_distance = Math.Clamp( HunterController.DebugChaseHoldDistance, MinDistance, MaxDistance );
		_relative = Rotation.FromYaw( viewYaw ).Inverse * held.WorldRotation;
		_raw = _relative;
	}

	// SCALE, predicted (user, 2026-11-07: "scaling a held prop … I think it's doing the roundtrip"): a client holder shows the
	// new size at once — the brushes the host will apply (PropClaims.ApplyGrabScale: the same snapshot, pivot and group rule,
	// mirrored at the grab — Seed), plus whatever damage tail is live — and its copy of the prop ignores the host's live
	// frames meanwhile (SdfNetworkSync.LocalPreview: they lag behind ours). The host's commit (when the factor holds still, or
	// at the drop) lands the same shape. The host applies it itself (UpdateGrab), so nothing to preview there.
	SdfNetworkSync _scalePreview;

	void PreviewScale( GameObject held )
	{
		if ( !Networking.IsActive || Networking.IsHost || _base is null )
			return;
		var clay = held.Components.Get<HiderController>()?.DisguiseSculpture;
		if ( !clay.IsValid() || clay.Brushes is not { } current )
			return;
		var sync = held.Components.GetAll<SdfNetworkSync>( FindMode.EverythingInSelfAndDescendants ).FirstOrDefault( s => s.Target == clay ); // (on the pawn root, aimed at its clay)
		if ( sync.IsValid() && sync != _scalePreview )
		{
			EndScalePreview();
			_scalePreview = sync;
			sync.BeginLocalPreview();
		}
		var work = PropClaims.ScaledCopy( _base, _pivot, _scale );
		work.AddRange( current.Skip( clay.AuthoredBrushCount ) );
		clay.Brushes = work;
		clay.RebuildShadowProxy();
	}

	/// <summary>Hand a previewed scale back to the host's frames (the hold ended, or the pawn is going away).</summary>
	public void EndScalePreview()
	{
		if ( _scalePreview.IsValid() )
			_scalePreview.EndLocalPreview();
		_scalePreview = null;
	}

	// May the scale step to this factor? The exact rule the host applies (PropClaims.GrabScaleStepAllowed:
	// stop at the limits, but a prop already outside them may always move back toward them), so the holder's
	// factor stops exactly where the prop does.
	bool CanScaleTo( GameObject held, float factor )
	{
		if ( _base is null )
			return false;

		var limits = held.Components.Get<SculptBounds>( FindMode.EverythingInSelfAndDescendants );
		return PropClaims.GrabScaleStepAllowed( limits, _base, _pivot, _scale, factor );
	}

	// ── The hold as sim input (phase 3 — SimHoldDriver) ───────────────────────────────────────────────────────
	// While we carry a prop the sim owns, this machine's sim input carries the pose we want it at: the sim predicts the
	// hold here and the host simulates it from the same input. Letting go simply stops sending it (the driver lets the
	// body fly on), so a drop or a throw is immediate too.
	Transform _simTarget;
	bool _simHolding;
	bool _simFreezeQueued; // the next sim input carries Freeze (the let-go tick of a frozen drop)

	/// <summary>Does the sim's local input have to come from our pawn for the hold (a target to send, or a frozen drop's
	/// let-go tick still to go out)?</summary>
	public bool NeedsSimInput => _simHolding || _simFreezeQueued;

	/// <summary>Add the hold's part to one sim tick's input: the target while we carry a sim body, or Freeze on the let-go tick
	/// of a frozen drop.</summary>
	public void WriteSimInput( ref Mimiclay.Sim.SimInput input )
	{
		if ( _simHolding )
		{
			input.Buttons |= Mimiclay.Sim.SimInput.Hold;
			input.HoldPos = _simTarget.Position;
			input.HoldRot = _simTarget.Rotation;
			input.HoldGrip = Mimiclay.Sim.SimInput.QuantGrip( _centre ); // the wire's steps here too, so we predict what the host gets
		}
		else if ( _simFreezeQueued )
		{
			input.Buttons |= Mimiclay.Sim.SimInput.Freeze; // the let-go tick of a frozen drop (EndSimHold)
			_simFreezeQueued = false;
		}
	}

	void SetSimHold( GameObject held, Transform target )
	{
		_simTarget = target;
		// Only once OUR sim has the body in our hands (a pawn or a decoy — predicted, or the host's grant): inputs are stamped
		// by tick, so a target sent before that had the host moving the prop for ticks our prediction never ran (measured:
		// 15 replays, corrections to 65u at every grab start). Until then the host holds it still (SimCtrl.Holding).
		var sim = Scene.GetSystem<Mimiclay.Sim.SimWorld>();
		bool applies = sim is { Active: true } && sim.Find( held.Id ) is { } b && b.Spec.Driver == Mimiclay.Sim.SimHoldDriver.Id && b.IsLocalOwned;
		if ( applies == _simHolding )
			return;
		_simHolding = applies;
		_simInputChanged?.Invoke(); // the hold target rides this machine's input
	}

	void EndSimHold( GameObject held = null, bool freeze = false )
	{
		EndScalePreview();
		if ( !_simHolding )
			return;
		_simHolding = false;
		// Frozen (LMB): the release tick's input says so (SimInput.Freeze), and the hold driver parks the prop on that tick — in
		// our prediction and on the host alike — rather than letting it fall loose until the freeze lands a round trip later.
		_simFreezeQueued = freeze;
		var sim = Scene.GetSystem<Mimiclay.Sim.SimWorld>();
		if ( sim is null )
			return;
		_simInputChanged?.Invoke(); // the next ticks carry no target: the prop is let go of, here and on the host
		if ( held.IsValid() && !freeze )
			sim.PredictRelease( held.Id ); // loose here from this tick — the host switches at the same tick of our inputs
	}

	/// <summary>Let go of <paramref name="held"/> (frozen in place with <paramref name="freeze"/>).</summary>
	public void LetGo( GameObject held, bool freeze = false )
	{
		_letGo = held.Id;
		_sinceLetGo = 0f;
		_grabbed = null;
		EndPredicted( revert: false ); // let go of before the grant: the host's redrive (or its absence) settles it
		EndSimHold( held, freeze );
		PropClaims.MarkGrabPending( false );
		PropClaims.Current?.DropGrab( freeze );
	}

	/// <summary>Drop whatever we hold (the pawn can't hold any more: free cam, swapped away).</summary>
	public void Release()
	{
		if ( _grabbed.IsValid() )
			LetGo( _grabbed );
	}

	/// <summary>The held prop's verbs as hint cards (Rotate, plus Freeze and Scale unless it's turn-only).</summary>
	public void FillHints( List<InteractChoice> into )
	{
		if ( _choices.Count == 0 )
		{
			_choices.Add( new InteractChoice( Hints.Instance, new InteractOption( "grab.rotate", "Rotate", InteractSlot.Primary ) ) );
			_choices.Add( new InteractChoice( Hints.Instance, new InteractOption( "grab.freeze", "Freeze", InteractSlot.Fire ) ) );
			_choices.Add( new InteractChoice( Hints.Instance, new InteractOption( "grab.scale", "Scale", InteractSlot.Scale ) ) );
		}

		into.Clear();
		if ( HintTurnOnly )
			into.Add( _choices[0] ); // a body only turns
		else
			into.AddRange( _choices );
	}

	// The cards while holding are hints — the grab reads its keys itself, so choosing one does nothing here.
	sealed class Hints : IInteractable
	{
		public static readonly Hints Instance = new();
		public void GetInteractions( in InteractContext ctx, List<InteractOption> options ) { }
		public void Interact( in InteractContext ctx, string optionId ) { }
	}
}
