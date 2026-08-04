using System;

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
	/// Don't author the look on it either: author the Authored* properties in this group instead. The
	/// component's live fields are runtime state (the blur is screen-scaled every frame), and a join
	/// snapshot ships those live values to new instances — anything seeded from them double-scales the blur
	/// and can leak the host's edit-session focus onto joiners.</summary>
	[Property] public DepthOfField DepthOfField { get; set; }

	/// <summary>Authored blur strength, asserted onto the depth of field on every machine at startup.
	/// This (not the DepthOfField component) is the place to author the scene's DoF look — these
	/// properties are never mutated at runtime, so they arrive authored on every machine regardless of
	/// what live state the join snapshot carried.</summary>
	[Property, Group( "Depth of Field" )] public float AuthoredBlurSize { get; set; } = 6.33f;

	/// <summary>Authored focal distance (world units), asserted on every machine at startup. See <see cref="AuthoredBlurSize"/>.</summary>
	[Property, Group( "Depth of Field" )] public float AuthoredFocalDistance { get; set; } = 705f;

	/// <summary>Authored focus range (world units), asserted on every machine at startup. See <see cref="AuthoredBlurSize"/>.</summary>
	[Property, Group( "Depth of Field" )] public float AuthoredFocusRange { get; set; } = 616f;

	/// <summary>Whether to switch the depth-of-field on when this camera awakes. Defaults to true so gameplay
	/// scenes get the racked-focus look; turn it off on cameras where DoF is a distraction (e.g. the menu).</summary>
	[Property, Group( "Depth of Field" )] public bool EnableDepthOfFieldOnStart { get; set; } = true;

	/// <summary>How fast the live depth-of-field eases toward its targets, per second (exponential). Higher
	/// snaps faster; lower is a slower, softer rack.</summary>
	[Property, Group( "Depth of Field" )] public float FocusLerpSpeed { get; set; } = 6f;

	/// <summary>Viewport height (px) at which the authored <see cref="DepthOfField.BlurSize"/> is taken
	/// literally. s&amp;box's DOF kernel reach is a fixed pixel count (≈2×BlurSize px), so without this it
	/// covers a bigger fraction of the screen — looks blurrier — on smaller viewports. The applied blur is
	/// scaled by Screen.Height / this value so it stays the same fraction of the frame at any resolution.
	/// Only the blur is scaled; focal distance/range are world-space and already resolution-independent.</summary>
	[Property, Group( "Depth of Field" ), Range( 240f, 2160f )] public float BlurReferenceHeight { get; set; } = 1080f;

	/// <summary>How fast the live field of view eases toward its target, per second (exponential). The ease is
	/// what makes pawn/mode switches (hunter 90 ↔ orbit 60) read as a smooth zoom instead of a pop.
	/// 0 = no easing: the FOV snaps to its target the moment a driver asserts it.</summary>
	[Property, Group( "Field of View" ), Range( 0f, 50f )] public float FovLerpSpeed { get; set; } = 8f;

	// Resolution-aware blur multiplier: keeps the pixel-based DOF the same fraction of the screen everywhere.
	float ScreenBlurScale => BlurReferenceHeight > 1f ? Screen.Height / BlurReferenceHeight : 1f;

	// Targets the live DepthOfField blends toward every frame. Set them from anywhere (directly, via the
	// static pass-throughs, or SetFocus) and the rack happens here so it always eases instead of popping.
	// Seeded from the authored DoF values on awake so play mode starts wherever the editor left it.
	float _targetBlurSize;
	float _targetFocalDistance;
	float _targetFocusRange;

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

	/// <summary>Target depth-of-field blur strength. The live camera eases toward it.</summary>
	public static float TargetBlurSize
	{
		get => Current.IsValid() ? Current._targetBlurSize : 0f;
		set { if ( Current.IsValid() ) Current._targetBlurSize = value; }
	}

	/// <summary>Target focal distance — how far from the camera is in sharp focus. The live camera eases toward it.</summary>
	public static float TargetFocalDistance
	{
		get => Current.IsValid() ? Current._targetFocalDistance : 0f;
		set { if ( Current.IsValid() ) Current._targetFocalDistance = value; }
	}

	/// <summary>Target focus range — the depth band that stays sharp around the focal distance. The live camera eases toward it.</summary>
	public static float TargetFocusRange
	{
		get => Current.IsValid() ? Current._targetFocusRange : 0f;
		set { if ( Current.IsValid() ) Current._targetFocusRange = value; }
	}

	/// <summary>Set all three depth-of-field targets at once; they blend in over the next frames.</summary>
	public static void SetFocus( float focalDistance, float focusRange, float blurSize )
	{
		if ( !Current.IsValid() )
			return;

		Current._targetFocalDistance = focalDistance;
		Current._targetFocusRange = focusRange;
		Current._targetBlurSize = blurSize;
	}

	/// <summary>Target vertical field of view (degrees). The live camera eases toward it; assert it every
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
	/// <c>lerp:true</c> (default) eases toward the values; <c>lerp:false</c> snaps. No-ops when no camera is live.</summary>
	public static DofControl Dof => Current.IsValid()
		? (Current._dofControl ??= new DofControl( Current ))
		: (_noopDof ??= new DofControl( null ));

	/// <summary>Fluent control over the camera's depth of field. Setting a value (or calling <see cref="Set"/>
	/// with lerp:true) eases toward it over the next frames; lerp:false snaps the live DoF immediately.</summary>
	public sealed class DofControl
	{
		readonly MainCamera _cam;
		internal DofControl( MainCamera cam ) => _cam = cam;

		/// <summary>Target focal distance — how far from the camera is in sharp focus.</summary>
		public float FocalDistance { get => _cam?._targetFocalDistance ?? 0f; set { if ( _cam is not null ) _cam._targetFocalDistance = value; } }

		/// <summary>Target focus range — the sharp band around the focal distance.</summary>
		public float FocusRange { get => _cam?._targetFocusRange ?? 0f; set { if ( _cam is not null ) _cam._targetFocusRange = value; } }

		/// <summary>Target blur strength.</summary>
		public float BlurSize { get => _cam?._targetBlurSize ?? 0f; set { if ( _cam is not null ) _cam._targetBlurSize = value; } }

		/// <summary>Set focal distance, focus range and blur together. lerp:true eases; lerp:false snaps now.</summary>
		public void Set( float focalDistance, float focusRange, float blurSize, bool lerp = true )
		{
			if ( _cam is null )
				return;

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

		DepthOfField.BlurSize = _targetBlurSize * ScreenBlurScale;
		DepthOfField.FocalDistance = _targetFocalDistance;
		DepthOfField.FocusRange = _targetFocusRange;
	}

	protected override void OnAwake()
	{
		Camera = GameObject.Components.GetOrCreate<CameraComponent>();
		_targetFov = Camera.FieldOfView;

		// Prefer the inspector-assigned one; fall back to whatever's on this GameObject if it was left unset.
		DepthOfField ??= GameObject.Components.Get<DepthOfField>();

		// Assert the authored DoF on this machine, ignoring whatever live values the component woke with.
		// On a joined instance those are the HOST's live values from the scene snapshot — already
		// screen-scaled once (and possibly mid-lerp or mid-edit-session) — so seeding from them compounds
		// the blur scale and inherits the host's transient focus.
		if ( DepthOfField.IsValid() )
		{
			DepthOfField.Enabled = EnableDepthOfFieldOnStart;

			_targetBlurSize = AuthoredBlurSize;
			_targetFocalDistance = AuthoredFocalDistance;
			_targetFocusRange = AuthoredFocusRange;
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

		float t = 1f - MathF.Exp( -FocusLerpSpeed * Time.Delta );

		DepthOfField.BlurSize = MathX.Lerp( DepthOfField.BlurSize, _targetBlurSize * ScreenBlurScale, t );
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
