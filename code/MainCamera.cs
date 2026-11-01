using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// The game's camera, exposed through a static accessor so any system can read or drive the view without
/// hunting down the <see cref="CameraComponent"/>. Drop one on the camera GameObject in every scene; while
/// enabled it registers itself as <see cref="Current"/>.
///
/// Deliberately not a singleton — nothing enforces uniqueness and last-enabled simply wins — but the
/// convention is exactly one live instance per scene, so callers can treat <see cref="Current"/> as "the
/// camera". Use the static pass-throughs (<see cref="Position"/>, <see cref="Rotation"/>,
/// <see cref="Angles"/>, <see cref="Fov"/>) for quick reads/writes, or grab <see cref="Camera"/> for the
/// full s&amp;box API.
/// </summary>
[Title( "Main Camera" )]
[Category( "Mimiclay" )]
[Icon( "videocam" )]
public sealed class MainCamera : Component
{
	/// <summary>The active game camera, or null if none is enabled. Last-enabled wins.</summary>
	public static MainCamera Current { get; private set; }

	/// <summary>The underlying s&amp;box camera, resolved from this GameObject (created if missing).</summary>
	public CameraComponent Camera { get; private set; }

	/// <summary>The camera's depth-of-field post-process; falls back to one on this GameObject if left unset.
	/// Don't write its fields directly — set the focus targets below and they blend toward them each frame.
	/// Don't author the look on it either: author the Authored* properties in this group (the global default)
	/// or a <see cref="DofProfile"/> (per map / per game) instead. The component's live fields are runtime
	/// state, and a join snapshot ships those live values to new instances — anything seeded from them can
	/// leak the host's edit-session focus onto joiners. Its BlurSize is resolution-independent on its own:
	/// the engine scales it by viewport height / 1080, so nothing here compensates for resolution.</summary>
	[Property] public DepthOfField DepthOfField { get; set; }

	/// <summary>The bottom layer of the resting DoF look: on or off. Profiles and drivers layer on top (see
	/// <see cref="DofProfile"/>). Off eases the blur to zero rather than disabling the component, so a driver
	/// (the edit session's focus pull) can still rack it in on top.</summary>
	[Property, Group( "Depth of Field" ), Title( "Authored Enabled" )] public bool AuthoredEnabled { get; set; } = true;

	/// <summary>Baseline blur strength (engine units — relative to 1080p, so the same at any resolution).
	/// Re-resolved every frame whenever nothing is driving the DoF (see <see cref="Dof"/>), so tweaking it in
	/// the inspector during play applies live. These properties are never mutated at runtime, so they arrive
	/// authored on every machine regardless of what live state the join snapshot carried.</summary>
	[Property, Group( "Depth of Field" )] public float AuthoredBlurSize { get; set; } = 6.33f;

	/// <summary>Baseline focal distance (world units), live-tweakable. See <see cref="AuthoredBlurSize"/>.</summary>
	[Property, Group( "Depth of Field" )] public float AuthoredFocalDistance { get; set; } = 705f;

	/// <summary>Baseline focus range (world units), live-tweakable. See <see cref="AuthoredBlurSize"/>.</summary>
	[Property, Group( "Depth of Field" )] public float AuthoredFocusRange { get; set; } = 616f;

	/// <summary>How fast the live depth-of-field eases toward its targets, per second (exponential). Higher
	/// snaps faster; lower is a slower, softer rack.</summary>
	[Property, Group( "Depth of Field" )] public float FocusLerpSpeed { get; set; } = 6f;

	// ── Edit-mode look ───────────────────────────────────────────────────────────────────────────────
	// THE one place the sculpt edit focus pull is tuned (every SculptEditSession reads these off the live camera).
	// Deliberately independent of the resting layers above: edit mode looks the same on every map and in every
	// game, whatever the Authored*/DofProfile look is. Live-tweakable while editing.

	/// <summary>Blur strength while editing (engine units, relative to 1080p).</summary>
	[Property, Group( "Edit Depth of Field" )] public float EditBlurSize { get; set; } = 7f;

	/// <summary>Sharp margin (world units) in front of the sculpt's near surface — how much space toward
	/// the camera stays in focus before the blur starts.</summary>
	[Property, Group( "Edit Depth of Field" )] public float EditFrontPadding { get; set; } = 16f;

	/// <summary>Sharp margin (world units) behind the sculpt's far surface — bigger keeps more of the
	/// backdrop in focus before the background blur ramps in.</summary>
	[Property, Group( "Edit Depth of Field" )] public float EditBackPadding { get; set; } = 48f;

	/// <summary>How much of the sculpt's bounds radius counts toward the sharp band. 1 = the whole sculpt
	/// stays in focus no matter how big it gets; 0 = ignore size (only the pads around its centre are sharp).</summary>
	[Property, Group( "Edit Depth of Field" )] public float EditSizeScale { get; set; } = 1f;

	/// <summary>When the focus distance changes faster than this (units/sec, i.e. zooming) the focus snaps
	/// instead of lerping, so it doesn't trail the camera. Orbiting changes it slowly, so that still eases.</summary>
	[Property, Group( "Edit Depth of Field" )] public float EditZoomSnapSpeed { get; set; } = 10f;

	/// <summary>How fast the live field of view eases toward its target, per second (exponential). The ease is
	/// what makes pawn/mode switches (hunter 90 ↔ orbit 60) read as a smooth zoom instead of a pop.
	/// 0 = no easing: the FOV snaps to its target the moment a driver asserts it.</summary>
	[Property, Group( "Field of View" ), Range( 0f, 50f )] public float FovLerpSpeed { get; set; } = 8f;

	// Targets the live DepthOfField blends toward every frame. Set them from anywhere (directly, via the
	// static pass-throughs, or SetFocus) and the rack happens here so it always eases instead of popping.
	float _targetBlurSize;
	float _targetFocalDistance;
	float _targetFocusRange;

	// Every external target write stamps a claim. While claims stay fresh (a driver like the sculpt edit
	// session asserts per frame) the baseline backs off; when they stop for a beat, the Authored* baseline
	// re-asserts and the live DoF eases back to the gameplay look. That makes exit automatic (no
	// save/restore handshake with drivers) and keeps the baseline live-editable in the inspector.
	// The grace is BOTH wall time and frames: a time-only grace lapses across a single long frame (a click
	// that commits an edit → rebuild/bake hitch of >0.1s), so the camera's next update — unordered against the
	// driver's — saw a "stale" claim, re-asserted the authored focus, and the hitch-sized dt lerped most of
	// the way there in one step: a visible focus pop on clicks. A driver asserting per frame never misses
	// two of OUR updates, whatever the frame time.
	const float DofClaimGrace = 0.1f;
	const int DofClaimGraceFrames = 2;
	float _lastDofClaim = float.MinValue;
	int _dofFrame;
	int _lastDofClaimFrame = int.MinValue / 2;
	void ClaimDof() { _lastDofClaim = Time.Now; _lastDofClaimFrame = _dofFrame; }
	bool DofClaimed => Time.Now - _lastDofClaim <= DofClaimGrace || _dofFrame - _lastDofClaimFrame <= DofClaimGraceFrames;

	// Target FOV, same scheme as the DoF targets: whoever drives the camera this frame declares it (per
	// frame, from GameSettings) and the live camera eases toward it here. Seeded from the authored camera
	// FOV on awake so scenes where nothing asserts (the menu) keep their authored value.
	float _targetFov;

	// Static pass-throughs to Current so callers can write `MainCamera.Position = ...` from anywhere.
	// Reads no-op to sane defaults and writes no-op when there's no live camera, so callers never have to
	// null-check Current themselves.

	/// <summary>World position of the active camera. Writes are ignored when none is live.</summary>
	public static Vector3 Position
	{
		get => Current.IsValid() ? Current.WorldPosition : Vector3.Zero;
		set { if ( Current.IsValid() ) Current.WorldPosition = value; }
	}

	/// <summary>World rotation of the active camera. Writes are ignored when none is live.</summary>
	public static Rotation Rotation
	{
		get => Current.IsValid() ? Current.WorldRotation : Rotation.Identity;
		set { if ( Current.IsValid() ) Current.WorldRotation = value; }
	}

	/// <summary>World orientation of the active camera as <see cref="Angles"/>.</summary>
	public static Angles Angles
	{
		get => Rotation.Angles();
		set => Rotation = value.ToRotation();
	}

	/// <summary>Position and aim the active camera in one call (the common case for controllers).</summary>
	public static void Set( Vector3 position, Rotation rotation )
	{
		if ( !Current.IsValid() )
			return;

		Current.WorldPosition = position;
		Current.WorldRotation = rotation;
	}

	/// <summary>Target depth-of-field blur strength. The live camera eases toward it. Writing claims the
	/// DoF — keep asserting per frame, or the authored baseline takes back over.</summary>
	public static float TargetBlurSize
	{
		get => Current.IsValid() ? Current._targetBlurSize : 0f;
		set { if ( Current.IsValid() ) { Current.ClaimDof(); Current._targetBlurSize = value; } }
	}

	/// <summary>Target focal distance — how far from the camera is in sharp focus. The live camera eases
	/// toward it. Writing claims the DoF — keep asserting per frame, or the authored baseline takes back over.</summary>
	public static float TargetFocalDistance
	{
		get => Current.IsValid() ? Current._targetFocalDistance : 0f;
		set { if ( Current.IsValid() ) { Current.ClaimDof(); Current._targetFocalDistance = value; } }
	}

	/// <summary>Target focus range — the depth band that stays sharp around the focal distance. The live
	/// camera eases toward it. Writing claims the DoF — keep asserting per frame, or the authored baseline
	/// takes back over.</summary>
	public static float TargetFocusRange
	{
		get => Current.IsValid() ? Current._targetFocusRange : 0f;
		set { if ( Current.IsValid() ) { Current.ClaimDof(); Current._targetFocusRange = value; } }
	}

	/// <summary>Set all three depth-of-field targets at once; they blend in over the next frames. Claims the
	/// DoF — keep asserting per frame, or the authored baseline takes back over.</summary>
	public static void SetFocus( float focalDistance, float focusRange, float blurSize )
	{
		if ( !Current.IsValid() )
			return;

		Current.ClaimDof();
		Current._targetFocalDistance = focalDistance;
		Current._targetFocusRange = focusRange;
		Current._targetBlurSize = blurSize;
	}

	/// <summary>Target field of view (degrees, along the camera's FovAxis — horizontal in every scene). The live camera eases toward it; assert it every
	/// frame from whatever is driving the camera. Writes are ignored when no camera is live.</summary>
	public static float Fov
	{
		get => Current.IsValid() ? Current._targetFov : 60f;
		set { if ( Current.IsValid() ) Current._targetFov = value; }
	}

	/// <summary>Set the target field of view. lerp:true (default) eases toward it; lerp:false snaps the live
	/// camera immediately (e.g. on a hard scene cut where a zoom would look wrong).</summary>
	public static void SetFov( float fov, bool lerp = true )
	{
		if ( !Current.IsValid() )
			return;

		Current._targetFov = fov;
		if ( !lerp && Current.Camera.IsValid() )
			Current.Camera.FieldOfView = fov;
	}

	DofControl _dofControl;
	static DofControl _noopDof;

	/// <summary>Depth-of-field handle for the active camera: <c>MainCamera.Dof.Set( focal, range, blur, lerp )</c>.
	/// <c>lerp:true</c> (default) eases toward the values; <c>lerp:false</c> snaps. No-ops when no camera is live.
	/// Writing through this claims the DoF: assert every frame while you want control — stop, and the camera
	/// eases back to its authored baseline on its own.</summary>
	public static DofControl Dof => Current.IsValid()
		? (Current._dofControl ??= new DofControl( Current ))
		: (_noopDof ??= new DofControl( null ));

	/// <summary>Fluent control over the camera's depth of field. Setting a value (or calling <see cref="Set"/>
	/// with lerp:true) eases toward it over the next frames; lerp:false snaps the live DoF immediately.
	/// Every write claims the DoF (see <see cref="Dof"/>).</summary>
	public sealed class DofControl
	{
		readonly MainCamera _cam;
		internal DofControl( MainCamera cam ) => _cam = cam;

		/// <summary>Target focal distance — how far from the camera is in sharp focus.</summary>
		public float FocalDistance { get => _cam?._targetFocalDistance ?? 0f; set { if ( _cam is not null ) { _cam.ClaimDof(); _cam._targetFocalDistance = value; } } }

		/// <summary>Target focus range — the sharp band around the focal distance.</summary>
		public float FocusRange { get => _cam?._targetFocusRange ?? 0f; set { if ( _cam is not null ) { _cam.ClaimDof(); _cam._targetFocusRange = value; } } }

		/// <summary>Target blur strength.</summary>
		public float BlurSize { get => _cam?._targetBlurSize ?? 0f; set { if ( _cam is not null ) { _cam.ClaimDof(); _cam._targetBlurSize = value; } } }

		/// <summary>Set focal distance, focus range and blur together. lerp:true eases; lerp:false snaps now.</summary>
		public void Set( float focalDistance, float focusRange, float blurSize, bool lerp = true )
		{
			if ( _cam is null )
				return;

			_cam.ClaimDof();
			_cam._targetFocalDistance = focalDistance;
			_cam._targetFocusRange = focusRange;
			_cam._targetBlurSize = blurSize;

			if ( !lerp )
				_cam.SnapToTargets();
		}

		/// <summary>Set just the blur strength. lerp:true eases; lerp:false snaps now.</summary>
		public void SetBlur( float blurSize, bool lerp = true )
		{
			if ( _cam is null )
				return;

			_cam.ClaimDof();
			_cam._targetBlurSize = blurSize;
			if ( !lerp )
				_cam.SnapToTargets();
		}

		/// <summary>Set just the focal distance. lerp:true eases; lerp:false snaps now (e.g. while zooming, so
		/// the focus doesn't trail the camera).</summary>
		public void SetFocal( float focalDistance, bool lerp = true )
		{
			if ( _cam is null )
				return;

			_cam.ClaimDof();
			_cam._targetFocalDistance = focalDistance;
			if ( !lerp )
				_cam.SnapToTargets();
		}
	}

	// Jump the live DoF straight to its targets (used by DofControl when lerp is off).
	void SnapToTargets()
	{
		if ( !DepthOfField.IsValid() )
			return;

		DepthOfField.BlurSize = _targetBlurSize;
		DepthOfField.FocalDistance = _targetFocalDistance;
		DepthOfField.FocusRange = _targetFocusRange;
	}

	// ── Resting look: the camera's Authored* values with every matching DofProfile folded on top ──────
	static readonly List<DofProfile> _layerScratch = new();

	/// <summary>The resolved resting DoF — what the camera eases to whenever no driver is asserting.</summary>
	public (bool Enabled, float BlurSize, float FocalDistance, float FocusRange) RestingDof
	{
		get
		{
			bool enabled = AuthoredEnabled;
			float blur = AuthoredBlurSize, focal = AuthoredFocalDistance, range = AuthoredFocusRange;

			foreach ( var p in ActiveProfiles() )
			{
				enabled = p.DofEnabled ?? enabled;
				blur = p.BlurSize ?? blur;
				focal = p.FocalDistance ?? focal;
				range = p.FocusRange ?? range;
			}

			return (enabled, blur, focal, range);
		}
	}

	// Profiles in this camera's scene that match the running game, lowest layer first. Reuses one scratch list —
	// read it before calling again.
	List<DofProfile> ActiveProfiles()
	{
		_layerScratch.Clear();
		var game = GameModes.Active;
		foreach ( var p in DofProfile.All )
			if ( p.IsValid() && p.Scene == Scene && p.Matches( game ) )
				_layerScratch.Add( p );

		_layerScratch.Sort( DofProfile.Compare );
		return _layerScratch;
	}

	void TargetRestingDof()
	{
		var rest = RestingDof;
		_targetBlurSize = rest.Enabled ? rest.BlurSize : 0f;
		_targetFocalDistance = rest.FocalDistance;
		_targetFocusRange = rest.FocusRange;
	}

	[ConCmd( "mimi_dbg_dof", Help = "Debug: print the camera's DoF layers, the resolved resting look and the live values." )]
	static void DebugDof()
	{
		var cam = Current;
		if ( !cam.IsValid() || !cam.DepthOfField.IsValid() )
		{
			Log.Info( "mimi_dbg_dof: no live MainCamera with a DepthOfField." );
			return;
		}

		static string F( float? v ) => v is { } f ? f.ToString( "0.##" ) : "-";
		static string B( bool? v ) => v is { } b ? (b ? "on" : "off") : "-";

		Log.Info( $"game={GameModes.Active?.ToString() ?? "none"} claimed={cam.DofClaimed}" );
		Log.Info( $"  camera  enabled={B( cam.AuthoredEnabled )} blur={F( cam.AuthoredBlurSize )} focal={F( cam.AuthoredFocalDistance )} range={F( cam.AuthoredFocusRange )}" );
		foreach ( var p in cam.ActiveProfiles() )
			Log.Info( $"  profile '{p.GameObject.Name}' prio={p.Priority} modes=[{string.Join( ",", p.Modes ?? new() )}] enabled={B( p.DofEnabled )} blur={F( p.BlurSize )} focal={F( p.FocalDistance )} range={F( p.FocusRange )}" );

		var r = cam.RestingDof;
		var d = cam.DepthOfField;
		Log.Info( $"  resting enabled={B( r.Enabled )} blur={F( r.BlurSize )} focal={F( r.FocalDistance )} range={F( r.FocusRange )}" );
		Log.Info( $"  target  blur={F( cam._targetBlurSize )} focal={F( cam._targetFocalDistance )} range={F( cam._targetFocusRange )}" );
		Log.Info( $"  live    blur={F( d.BlurSize )} focal={F( d.FocalDistance )} range={F( d.FocusRange )} (engine scales blur by {Screen.Height / 1080f:0.##} at {Screen.Height}px)" );
	}

	protected override void OnAwake()
	{
		Camera = GameObject.Components.GetOrCreate<CameraComponent>();
		_targetFov = Camera.FieldOfView;

		// Prefer the inspector-assigned one; fall back to whatever's on this GameObject if it was left unset.
		DepthOfField ??= GameObject.Components.Get<DepthOfField>();

		// Assert the resolved resting DoF on this machine, ignoring whatever live values the component woke
		// with. On a joined instance those are the HOST's live values from the scene snapshot (possibly
		// mid-lerp or mid-edit-session), so seeding from them inherits the host's transient focus. On/off is
		// carried by the blur target (0 = off), so the component itself always stays enabled.
		if ( DepthOfField.IsValid() )
		{
			DepthOfField.Enabled = true;
			TargetRestingDof();
			SnapToTargets();
		}
	}

	protected override void OnUpdate()
	{
		// Frame-rate-independent exponential eases, so the zoom/rack feels the same at any FPS.
		if ( Camera.IsValid() )
			Camera.FieldOfView = FovLerpSpeed > 0f
				? MathX.Lerp( Camera.FieldOfView, _targetFov, 1f - MathF.Exp( -FovLerpSpeed * Time.Delta ) )
				: _targetFov;

		if ( !DepthOfField.IsValid() )
			return;

		// Nothing has claimed the DoF recently → rest on the resolved camera + profile look. Re-resolving it
		// every frame is what makes the authored values and profiles live-editable (and lets a profile that
		// only applies once the game manager arrives take over without a restart).
		_dofFrame++;
		if ( !DofClaimed )
			TargetRestingDof();

		float t = 1f - MathF.Exp( -FocusLerpSpeed * Time.Delta );

		DepthOfField.BlurSize = MathX.Lerp( DepthOfField.BlurSize, _targetBlurSize, t );
		DepthOfField.FocalDistance = MathX.Lerp( DepthOfField.FocalDistance, _targetFocalDistance, t );
		DepthOfField.FocusRange = MathX.Lerp( DepthOfField.FocusRange, _targetFocusRange, t );
	}

	protected override void OnEnabled()
	{
		Current = this;
	}

	protected override void OnDisabled()
	{
		// Only relinquish if we're still the registered one (another camera may have taken over).
		if ( Current == this )
			Current = null;
	}
}
