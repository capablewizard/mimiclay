using System;
using System.Collections.Generic;
using Mimiclay.Sim;

namespace Mimiclay;

// The prop's shoulder camera, crosshair and grab (user, 2026-11-08: "make the prop view a third person camera setup with a
// crosshair, and allow them to grab, rotate and scale props in creative mode like hunters can").
//
// CAMERA: the play camera's orbit boom hangs off a point to the right of the shape (OrbitCameraController.ShoulderOffset,
// ShoulderRatio × the boom, eased in and out with edit mode), so the prop sits left of centre and the middle of the screen
// is free for the crosshair HUD — the hunter's own HunterCrosshair, made per prop at runtime (EnsureCrosshairHud).
//
// AIM: the crosshair is the camera's centre ray. Its "eye" is where that ray passes our shape (the pivot's depth along it)
// — so the hover, the reach and the hold distance all start at us, not at the lens behind us, and nothing between the
// camera and our shape can be picked.
//
// GRAB: where the mode lets props grab (IPropClaimHost.PropsGrab — creative), the same GrabHolder the hunter carries with:
// RMB over clay picks it up (hold to carry), E+mouse rotates, R+mouse scales, the wheel pushes/pulls, LMB freezes. The
// held prop keeps clear of our own shape (the clearance is our extent along the aim). While a grab owns RMB/LMB the play
// camera's RMB dolly and LMB camera-only orbit stand down; E/R freeze the look.
public sealed partial class HiderController : ICrosshairOwner
{
	/// <summary>Play camera over the right shoulder, with a crosshair (off = the old centred orbit, no crosshair).</summary>
	[Property, Group( "Camera" )] public bool ShoulderCamera { get; set; } = true;

	/// <summary>How far right of the shape the boom hangs, as a fraction of the boom's length (so it frames any size of prop
	/// the same). The hunter's shoulder boom is 30 at 120 = 0.25.</summary>
	[Property, Group( "Camera" ), Range( 0f, 0.6f )] public float ShoulderRatio { get; set; } = 0.3f;

	/// <summary>How fast the shoulder offset eases in and out (edit mode centres the shape again), per second.</summary>
	[Property, Group( "Camera" ), Range( 1f, 20f )] public float ShoulderBlendSpeed { get; set; } = 8f;

	float _shoulderBlend;

	GrabHolder _grabHolder;
	GrabHolder PropGrab => _grabHolder ??= new GrabHolder( this, UpdateSimControl );

	// RMB / LMB belong to the grab until they're released: RMB from the press that asked for a grab (the grant is a round trip
	// away on a client), LMB from the press that froze the held prop — so the camera's dolly / camera-only orbit don't also
	// run off the same press.
	bool _rmbForGrab, _lmbForGrab;

	GameObject _crosshairHud;
	readonly List<InteractChoice> _hoverChoices = new();

	/// <summary>Is this prop allowed to grab right now (the mode's say — creative)?</summary>
	static bool PropsGrabOpen => PropClaims.Current is { } claims && claims.PropGrabOpen;

	/// <summary>Ask the host to put <paramref name="target"/> in our hands (the RMB Grab card, aimed by our crosshair).</summary>
	internal void BeginGrab( GameObject target, Vector3? grip = null )
	{
		_rmbForGrab = true;
		PropGrab.Begin( target, grip );
	}

	/// <summary>E or R held over a grabbed prop: the mouse is turning or scaling it, so the view must not move.</summary>
	bool GrabLookFrozen => _grabHolder?.LookFrozen ?? false;

	bool GrabOwnsRmb => _rmbForGrab || (_grabHolder?.Grabbed).IsValid() || PropClaims.LocalGrabPending;
	bool GrabOwnsLmb => _lmbForGrab || (_grabHolder?.Grabbed).IsValid();

	// ── ICrosshairOwner ──────────────────────────────────────────────────────────────────────────────────────

	/// <inheritdoc/>
	public bool CrosshairVisible => !IsProxy && !Scenery && ShoulderCamera && _shoulderBlend > 0.5f
		&& !EditMode && !FreeCamming && !CharadesManager.LocalPicking
		&& !TauntPieOpen; // the pie sits dead centre over it, and the mouse is steering the pie, not aiming

	/// <inheritdoc/>
	public InteractContext? HoverContext { get; private set; }

	/// <inheritdoc/>
	public IReadOnlyList<InteractChoice> Hover => _hoverChoices;

	/// <inheritdoc/>
	public InteractSlot? LastFiredSlot { get; private set; }

	/// <inheritdoc/>
	public RealTimeSince SinceFired { get; private set; }

	// ── Camera ───────────────────────────────────────────────────────────────────────────────────────────────

	// ── Two views, each with its own framing ──
	// The CENTRED view is the old orbit: its zoom and pan are the player's (dolly/pan), and edit mode / the charades pick use
	// it. The SHOULDER view frames itself from the prop's size — the boom ShoulderFramingScale × the shape's radius (in
	// reference-FOV units), the pivot at the top of the shape, the shoulder offset clear of our own side — numbers taken from
	// the toy train, which the user picked as the framing to match (radius 64 → Distance 310, pivot at its top, shoulder 63).
	// It reframes on possession and after every edit (the shape may have changed size); a dolly or pan in between stays.
	// The rig holds one zoom and one pan: each frame we write the blend of the two views', and read back next frame whatever
	// the player's gestures changed since, into the view being aimed for.

	/// <summary>Shoulder view zoom: the boom as a multiple of the shape's radius (half its bounds' diagonal), in
	/// reference-FOV units (OrbitCameraController.Distance). 4.84 is the toy train's.</summary>
	[Property, Group( "Camera" ), Range( 2f, 10f )] public float ShoulderFramingScale { get; set; } = 4.84f;

	float _centredDistance, _shoulderDistance;
	Vector3 _centredPan, _shoulderPan;
	float _writtenDistance;
	Vector3 _writtenPan;
	bool _framingSeeded, _reframeShoulder;

	/// <summary>The shoulder view is what we're aiming for (the setting, not the blend).</summary>
	bool WantShoulder => ShoulderCamera && GameSettings.PropShoulderView && !EditMode && !CharadesManager.LocalPicking;

	/// <summary>Frame the shoulder view afresh from the shape's current size (possession, an edit just ended).</summary>
	void ReframeShoulder() => _reframeShoulder = true;

	// UpdateCamera, before the play camera reads input and the rig ticks.
	void UpdateShoulder()
	{
		bool want = WantShoulder;
		if ( !_framingSeeded )
		{
			// Whatever the rig holds now (the spawn default, or a lobby swap's carried zoom) is the centred view's.
			_centredDistance = _orbit.Distance;
			_centredPan = _orbit.PanOffset;
			_shoulderPan = Vector3.Zero;
			_shoulderDistance = _centredDistance;
			_reframeShoulder = true;
			_framingSeeded = true;
			_shoulderBlend = want ? 1f : 0f; // a fresh pawn starts in its view, no glide in
		}
		else
		{
			// The player's dolly / pan (and anything else that moved the rig) since last frame → the view aimed for.
			if ( _writtenDistance > 0f && MathF.Abs( _orbit.Distance - _writtenDistance ) > 0.001f )
			{
				float ratio = _orbit.Distance / _writtenDistance;
				if ( want ) _shoulderDistance *= ratio; else _centredDistance *= ratio;
			}
			var panDelta = _orbit.PanOffset - _writtenPan;
			if ( panDelta.LengthSquared > 1e-6f )
			{
				if ( want ) _shoulderPan += panDelta; else _centredPan += panDelta;
			}
		}

		// Size-based framing (the shape's bounds, in the disguise's own frame).
		float lift = CameraHeightOffset;
		float radius = 0f;
		if ( _body.IsValid() && Sdf.TryGetBounds( _body.Brushes, out var b, SculptEditSession.PendingStamp( _body ) ) )
		{
			radius = b.Size.Length * 0.5f;
			var top = _body.WorldTransform.PointToWorld( new Vector3( b.Center.x, b.Center.y, b.Maxs.z ) );
			lift = top.z - _body.WorldPosition.z;
			if ( _reframeShoulder )
			{
				_shoulderDistance = Math.Clamp( ShoulderFramingScale * radius, MinDistance, MaxDistance );
				_shoulderPan = Vector3.Zero;
				_reframeShoulder = false;
			}
		}

		float target = want ? 1f : 0f;
		_shoulderBlend = MathX.Lerp( _shoulderBlend, target, 1f - MathF.Exp( -ShoulderBlendSpeed * Time.Delta ) );
		if ( MathF.Abs( _shoulderBlend - target ) < 0.002f )
			_shoulderBlend = target;
		float t = _shoulderBlend;

		_orbit.Distance = _writtenDistance = MathX.Lerp( _centredDistance, _shoulderDistance, t );
		_orbit.PanOffset = _writtenPan = Vector3.Lerp( _centredPan, _shoulderPan, t );
		_orbit.FollowOffset = Vector3.Up * MathX.Lerp( CameraHeightOffset, lift, t );

		// The shoulder: a share of the boom, but at least our own reach to the camera's right plus a margin — a wide prop
		// held at the ratio alone still covered the middle of the screen, so the crosshair sat on our own shape.
		var right = _orbit.Angles.ToRotation().Right;
		float side = MathF.Max( ShoulderRatio * _orbit.Reach, ShapeReachAlong( right ) + ShoulderMargin * radius );
		_orbit.ShoulderOffset = t * side;
	}

	// How far past our own side the boom hangs at least, as a share of the shape's radius (the train: ~10u at radius 64).
	const float ShoulderMargin = 0.16f;

	// Tab in play (the hunter's "View" key): centred ⇄ over the shoulder.
	void UpdateViewToggle()
	{
		if ( ShoulderCamera && !EditMode && !CharadesManager.LocalPicking && !PauseMenu.IsOpen && !RoundSetup.IsOpen
			&& Input.Pressed( "ToggleView" ) )
			GameSettings.PropShoulderView = !GameSettings.PropShoulderView;
	}

	// The crosshair HUD, made the first time we play as this prop: the hunter's crosshair panel under a screen panel of our
	// own, parented to us so it goes with the pawn. Runtime-only and ours alone.
	void EnsureCrosshairHud()
	{
		if ( _crosshairHud.IsValid() || !ShoulderCamera )
			return;

		var go = new GameObject( false, "Crosshair HUD" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.SetParent( GameObject, false );
		var screen = go.Components.Create<ScreenPanel>();
		screen.ZIndex = 50; // (the hunter prefab's HUD)
		go.Components.Create<HunterCrosshair>();
		go.Enabled = true;
		_crosshairHud = go;
	}

	// ── Aim, hover and grab (owner, per frame, after UpdateCamera) ───────────────────────────────────────────

	void UpdatePropAim()
	{
		if ( !Input.Down( "Attack2" ) )
			_rmbForGrab = false;
		if ( !Input.Down( "Attack1" ) )
			_lmbForGrab = false;

		EnsureCrosshairHud();

		var cam = Scene.Camera;
		bool play = ControlActive && !PauseMenu.IsOpen && !RoundSetup.IsOpen && cam.IsValid() && _body.IsValid();
		bool open = PropsGrabOpen && WantShoulder; // the grab aims with the crosshair, so only in the shoulder view

		if ( !play || !open )
		{
			// Sculpting drops the prop through the holder's own rule (Update's `editing`); anything else that stops us
			// (free cam, a menu, the mode closing the grab) lets go here.
			if ( _grabHolder is { } g )
			{
				if ( EditMode && cam.IsValid() )
					g.Update( cam.WorldPosition, cam.WorldRotation.Forward, cam.WorldRotation.Angles().yaw, editing: true );
				else
					g.Release();
			}
			ClearHover();
			return;
		}

		// The crosshair's ray, from where it passes our shape.
		var camPos = cam.WorldPosition;
		var aim = cam.WorldRotation.Forward;
		var eye = camPos + aim * MathF.Max( Vector3.Dot( _orbit.Pivot - camPos, aim ), 0f );
		float clearance = ShapeReachAlong( aim ) + 12f;

		var grab = PropGrab;
		if ( grab.Update( eye, aim, _orbit.Angles.yaw, editing: false, clearance ) )
		{
			if ( grab.FrozeThisFrame )
				_lmbForGrab = true;
			if ( grab.PressedSlot is { } pressed ) // E Rotate / R Scale / LMB Freeze: spring that card
			{
				LastFiredSlot = pressed;
				SinceFired = 0;
			}
			if ( grab.Grabbed is { } held && held.IsValid() )
				PublishGrabHints( held, grab );
			else
				ClearHover();
			return;
		}

		UpdatePropHover( eye, aim, clearance );
	}

	// How far our shape reaches past its centre along `dir`: its bounds box's extent in that direction (exact for the box,
	// generous for a round shape — which only keeps a held prop a little further out).
	float ShapeReachAlong( Vector3 dir )
	{
		if ( !_body.IsValid() || !Sdf.TryGetBounds( _body.Brushes, out var b ) )
			return 24f;
		var local = _body.WorldRotation.Inverse * dir;
		var half = b.Size * 0.5f;
		return MathF.Abs( local.x ) * half.x + MathF.Abs( local.y ) * half.y + MathF.Abs( local.z ) * half.z;
	}

	// What the crosshair is over: the hunter's hover, cut down to the grab — the only verb a prop has. The ray starts at our
	// shape and reaches Interactions.Reach past it.
	void UpdatePropHover( Vector3 eye, Vector3 aim, float clearance )
	{
		_hoverChoices.Clear();
		InteractContext? ctx = null;

		var tr = Scene.Trace.Ray( eye, eye + aim * (Interactions.Reach + clearance) )
			.IgnoreGameObjectHierarchy( GameObject )
			.HitTriggers()
			.WithoutTags( "trigger", "water", CharadesStageFence.WallTag )
			.Run();
		if ( tr.Hit && tr.GameObject.IsValid() )
		{
			var probe = new InteractContext
			{
				Prop = this,
				Target = tr.GameObject,
				Sculpture = HunterController.FindSculpture( tr.GameObject ),
				HitPosition = tr.HitPosition,
			};
			Interactions.Collect( probe, _hoverChoices );
			_hoverChoices.RemoveAll( c => c.Option.Slot != InteractSlot.Pickup );
			if ( _hoverChoices.Count > 0 )
				ctx = probe;
		}

		HoverContext = ctx;
		Interactions.PublishLocal( ctx, _hoverChoices );

		if ( ctx is not { } at || !Input.Pressed( "Attack2" ) )
			return;

		var choice = _hoverChoices[0];
		LastFiredSlot = choice.Option.Slot;
		SinceFired = 0;
		choice.Provider.Interact( at, choice.Option.Id ); // PropClaims → BeginGrab
	}

	void PublishGrabHints( GameObject held, GrabHolder grab )
	{
		var hider = held.Components.Get<HiderController>();
		var ctx = new InteractContext
		{
			Prop = this,
			Target = held,
			Sculpture = hider.IsValid() ? hider.DisguiseSculpture : null,
			HitPosition = grab.HintAt,
		};
		grab.FillHints( _hoverChoices );
		HoverContext = ctx;
		Interactions.PublishLocal( ctx, _hoverChoices );
	}

	void ClearHover()
	{
		if ( HoverContext is null && _hoverChoices.Count == 0 )
			return;
		HoverContext = null;
		_hoverChoices.Clear();
		Interactions.PublishLocal( null, _hoverChoices );
	}

	// ── Sim + teardown ───────────────────────────────────────────────────────────────────────────────────────

	// What we carry in the sim never collides with our own walker (SimHoldFilters) — from SimStep / SimStepDown.
	readonly SimHoldFilters _holdFilters = new();

	// Destroyed / no longer ours: put down what we hold, hand any scale preview back, drop the filters.
	void EndPropGrab()
	{
		if ( !IsProxy )
			_grabHolder?.Release();
		_grabHolder?.EndScalePreview();
		_holdFilters.Clear();
	}
}
