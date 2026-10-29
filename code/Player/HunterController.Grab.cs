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

		if ( !held.IsValid() )
		{
			_grabbed = null;
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
		if ( !Input.Down( "Attack2" ) && !DebugGrabHold )
		{
			LetGo( held );
			return true;
		}

		if ( held != _grabbed )
			SeedGrab( held, eye );

		// LMB: freeze it right where it hangs and let go (the hold ends even with RMB still down — re-grabbing
		// takes a fresh press, so the freeze can't be undone by accident).
		if ( Input.Pressed( "Attack1" ) )
		{
			LastFiredSlot = InteractSlot.Sculpt;
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
			if ( Input.Down( "Reload" ) )
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
		_grabCentre = held.WorldTransform.PointToLocal( PropClaims.PopSpot( held ) );

		// Where we want it: the clay's centre on the aim at the hold distance, turned with our yaw — pulled in to
		// wherever the prop's own shape would first meet the world on the way out (see ClampedRoot), so the
		// target is never somewhere the body can't be.
		var rotation = yawFrame * _grabRelative;
		var root = ClampedRoot( held, eye, rotation );
		PropClaims.Current.UpdateGrab( held, root, rotation, _grabScale );

		PublishGrabHover( held, root + rotation * _grabCentre );
		return true;
	}

	// Keep the hold target (nearly) OUT of the world. A target deep inside a wall or under the floor can never be
	// reached, so the host's chase pushes the body into the surface at full strength every tick while the solver
	// shoves it back — the jitter. Instead, sweep the prop's own collision shape (its live physics body, so the
	// exact collider — carves, scale and all) along the aim from just in front of us out to the chosen distance,
	// at the rotation we're holding it, and stop at the first hit PLUS GrabPressDepth: a shallow overlap the
	// chase answers with a gentle, steady push (depth × the hold pull), so the prop presses flush against the
	// surface — settling onto it — instead of hovering a hair off it or being rammed through it. _grabDistance
	// stays the chosen maximum: step back from the obstruction and the prop springs back out to it.
	//
	// The aim sweep starts a short way out, which can already be inside something when we stand against a
	// wall or the prop is big; then the fallback sweeps from where the prop actually IS toward the target, so it
	// still stops at the surface rather than being asked through it — and if even that starts inside
	// something, the prop is simply held where it is.
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
		if ( body is null || !body.IsValid() || far <= near )
			return desired;

		var aim = GrabTrace( held ).Sweep( body, RootAt( near ), RootAt( far ) ).Run();
		if ( !aim.StartedSolid )
		{
			if ( !aim.Hit )
				return desired;

			float span = far - near;
			float reach = MathF.Min( aim.Fraction * span + GrabPressDepth, span );
			return RootAt( near + reach ).Position;
		}

		var from = new Transform( held.WorldPosition, rotation );
		var path = GrabTrace( held ).Sweep( body, from, new Transform( desired, rotation ) ).Run();

		// Starting inside something too (resting contact, the press, a prop bigger than the gap we're in):
		// there's no clear point to aim for, so ask for a short step toward the target. Not "stay where it
		// is" — a prop merely touching the floor and a counter then could never be lifted off them, and a
		// pressed one flipped between the press and standing still every frame. Not the unclamped target
		// either — that's the full-strength shove into the wall this whole clamp exists to stop. Once the
		// step frees it, the sweeps above take over again. Checked before Hit: a sweep that starts solid
		// needn't report a hit.
		if ( path.StartedSolid )
			return held.WorldPosition + (desired - held.WorldPosition).ClampLength( GrabUnstickStep );

		if ( !path.Hit )
			return desired;

		var travel = desired - from.Position;
		float length = travel.Length;
		float keep = length > 1e-3f ? MathF.Min( path.Fraction + GrabPressDepth / length, 1f ) : 0f;
		return from.Position + travel * keep;
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

	// How far past the first hit the target goes (units along the sweep): the "press". Small enough that the
	// chase into it is a soft push (× HiderController's HoldPull ≈ 70 u/s at 4) the solver absorbs quietly;
	// 0 = stop exactly at contact.
	const float GrabPressDepth = 4f;

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

		var centre = PropClaims.PopSpot( held ); // the middle of its clay
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

	void LetGo( GameObject held, bool freeze = false )
	{
		_grabLetGo = held.Id;
		_sinceGrabLetGo = 0f;
		_grabbed = null;
		PropClaims.MarkGrabPending( false );
		PropClaims.Current?.DropGrab( freeze );
	}

	// The held prop's verbs, as the crosshair's cards (and its outline, through the local hover).
	void PublishGrabHover( GameObject held, Vector3 at )
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
			_grabChoices.Add( new InteractChoice( GrabHints.Instance, new InteractOption( "grab.freeze", "Freeze", InteractSlot.Sculpt ) ) );
			_grabChoices.Add( new InteractChoice( GrabHints.Instance, new InteractOption( "grab.scale", "Scale", InteractSlot.Scale ) ) );
		}

		_hoverChoices.Clear();
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
