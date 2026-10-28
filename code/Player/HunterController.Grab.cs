using System;
using System.Collections.Generic;

namespace Mimiclay;

// The holder's half of the grab (see PropClaims.Grab for the host half). Owner-only: once the host's Held row
// names our prop, every frame we work out where we want it — floating at a distance along the aim, turned with
// our view's yaw, at the scale we've dialled — and stream that pose to the host, which steers the body there.
// The grab lasts exactly as long as RMB is held: press to pick up, release to drop. While holding: LMB throws,
// E+mouse rotates, R+mouse scales, the wheel pushes/pulls. The hover prompts are replaced by those verbs as
// cards under the held prop.
public sealed partial class HunterController
{
	const float GrabMinDistance = 30f;
	const float GrabMaxDistance = 300f;
	const float GrabWheelStep = 12f;     // units per wheel notch
	const float GrabThrowSpeed = 900f;
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
			LetGo( held, Vector3.Zero );
			return false;
		}

		// Hold-to-carry: RMB up = let go. Checked before seeding, so a grant that lands after a quick click
		// (a client's round-trip) drops straight back where it was instead of jumping into the hand.
		if ( !Input.Down( "Attack2" ) )
		{
			LetGo( held, Vector3.Zero );
			return true;
		}

		if ( held != _grabbed )
			SeedGrab( held, eye );

		if ( Input.Pressed( "Attack1" ) )
		{
			LastFiredSlot = InteractSlot.Sculpt;
			SinceFired = 0;
			LetGo( held, _aimDir * GrabThrowSpeed );
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

		// Where we want it: the clay's centre on the aim at the hold distance, turned with our yaw.
		var rotation = yawFrame * _grabRelative;
		var hold = eye + _aimDir * _grabDistance;
		var root = hold - rotation * _grabCentre;
		PropClaims.Current.UpdateGrab( held, root, rotation, _grabScale );

		PublishGrabHover( held, hold );
		return true;
	}

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

	// Would the shape at this scale pass the size limits? The same stateless check the host makes before it
	// applies one (SculptBounds.ValidateIncoming), so the holder's factor stops exactly where the prop does.
	bool CanScaleTo( GameObject held, float factor )
	{
		if ( _grabBase is null )
			return false;

		var limits = held.Components.Get<SculptBounds>( FindMode.EverythingInSelfAndDescendants );
		if ( !limits.IsValid() )
			return true;

		var work = new List<SdfBrush>( _grabBase.Count );
		foreach ( var b in _grabBase )
		{
			var scaled = b.Copy();
			SdfScaling.ScaleUniform( scaled, _grabPivot, factor );
			work.Add( scaled );
		}
		return limits.ValidateIncoming( work, withVolume: true );
	}

	void LetGo( GameObject held, Vector3 throwVelocity )
	{
		_grabLetGo = held.Id;
		_sinceGrabLetGo = 0f;
		_grabbed = null;
		PropClaims.MarkGrabPending( false );
		PropClaims.Current?.DropGrab( throwVelocity );
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
			_grabChoices.Add( new InteractChoice( GrabHints.Instance, new InteractOption( "grab.throw", "Throw", InteractSlot.Sculpt ) ) );
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
