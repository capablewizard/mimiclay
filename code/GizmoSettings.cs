using System;

namespace Mimiclay;

/// <summary>
/// Scene-wide tuning for the editor SDF gizmos. Drop one of these on any GameObject and the sculpt
/// tool will pick it up — every handle, ring, dot, plane square and brush outline reads from here.
///
/// Sizing is screen-based: pixel-ish values are converted to world space at the gizmo's distance each
/// frame, so the gizmo stays the same size on screen regardless of camera distance or brush size (like
/// a standard transform gizmo). <see cref="GizmoScale"/> scales every on-screen size at once;
/// <see cref="Thickness"/> scales the line weights. If no GizmoSettings exists in the scene the tool
/// falls back to these defaults.
/// </summary>
[Title( "Gizmo Settings" )]
[Category( "SDF" )]
[Icon( "straighten" )]
public sealed class GizmoSettings : Component
{
	// --- Features ---

	/// <summary>Show the per-axis move lines (drag along X/Y/Z to translate the brush).</summary>
	[Property, Group( "Features" )] public bool ShowTranslation { get; set; } = true;

	/// <summary>Show the light-grey rounded square in the centre that moves the brush across the screen
	/// (view) plane — a free-move handle facing the camera.</summary>
	[Property, Group( "Features" )] public bool ShowScreenMove { get; set; } = true;

	/// <summary>Show the rotation rings (drag to spin the brush around each axis).</summary>
	[Property, Group( "Features" )] public bool ShowRotation { get; set; } = true;

	/// <summary>Show the outer white ring that rotates the brush around the screen (view) axis.</summary>
	[Property, Group( "Features" )] public bool ShowScreenRotation { get; set; } = true;

	/// <summary>Show the scale dots at the ends of the move lines (drag to resize per axis).</summary>
	[Property, Group( "Features" )] public bool ShowScale { get; set; } = true;

	/// <summary>Show the white uniform-scale circle pinned to the top-right of the gizmo — drag up/right
	/// to scale the whole shape up, down/left to scale it down.</summary>
	[Property, Group( "Features" )] public bool ShowUniformScale { get; set; } = true;

	/// <summary>Show the two horizontal 3D sliders below the gizmo (blend + rounding). Used only by the
	/// editor gizmo (BrushTransformGizmo); the in-game runtime gizmo replaces them with the 2D HUD
	/// ClaySliders and ignores this flag.</summary>
	[Property, Group( "Features" )] public bool ShowSliders { get; set; } = true;

	/// <summary>Show the corner plane-move squares (drag to slide the brush in a single plane).</summary>
	[Property, Group( "Features" )] public bool ShowPlaneSquares { get; set; } = true;

	/// <summary>Hide the far half of each coloured rotation ring — only the camera-facing side of an
	/// imaginary sphere at the gizmo centre is drawn, so the rings don't read as cluttered ovals.</summary>
	[Property, Group( "Features" )] public bool OccludeRingBackHalf { get; set; } = true;

	/// <summary>Opacity of the whole gizmo (and brush outlines) while you're dragging a handle, for a
	/// clearer view of the result. 1 = no change, 0 = fully hidden. The drag keeps working regardless.</summary>
	[Property, Range( 0f, 1f ), Group( "Features" )] public float DragOpacity { get; set; } = 0.2f;

	/// <summary>Seconds to fade handle families in/out when a drag starts/ends (the non-active families
	/// dim away — e.g. rotating hides the sliders). 0 = instant.</summary>
	[Property, Range( 0f, 1f ), Group( "Features" )] public float FadeTime { get; set; } = 0.12f;

	// --- Master ---

	/// <summary>Overall on-screen size of the gizmo — multiplies every screen size below. 1 = default,
	/// 2 = twice as big on screen. Doesn't affect line thickness (use <see cref="Thickness"/>).</summary>
	[Property, Range( 0.1f, 5f ), Group( "Master" )] public float GizmoScale { get; set; } = 1f;

	/// <summary>Viewport height (px) at which the gizmo's pixel sizes are taken literally. The whole gizmo —
	/// sizes AND line thicknesses — scales by Screen.Height / this value, so it keeps the same fraction of
	/// the viewport on any screen size instead of a fixed pixel count. Set it to your design resolution
	/// (1080 = tuned for a 1080p-tall viewport); on a smaller screen the gizmo shrinks to match.</summary>
	[Property, Range( 240f, 2160f ), Group( "Master" )] public float ReferenceHeight { get; set; } = 1080f;

	/// <summary>Master multiplier for every line's thickness — move axes, rotation rings and brush
	/// outlines. 1 = default, 2 = twice as chunky. Doesn't change how big the gizmo is.</summary>
	[Property, Range( 0.1f, 5f ), Group( "Master" )] public float Thickness { get; set; } = 1f;

	/// <summary>How much a handle emphasizes while hovered or dragged — lines thicken and dots/squares
	/// grow by this factor. 1 = no change, 1.3 = 30% bigger. Applies to every handle.</summary>
	[Property, Range( 1f, 3f ), Group( "Master" )] public float HotScale { get; set; } = 1.3f;

	/// <summary>Extra clickable padding around every handle, in screen pixels beyond its visible edge.
	/// 0 = a handle goes hot exactly when the cursor is over the shape; positive = easier to grab (hot
	/// before you reach it); negative = you must be further inside the shape.</summary>
	[Property, Range( -10f, 20f ), Group( "Master" )] public float HitboxPadding { get; set; } = 0f;

	/// <summary>Pushes the gizmo's click targets forward in depth so they win selection over the shape
	/// behind them. 0 = neutral; higher = more aggressively in front. Mostly a safety/debug lever — the
	/// handles already beat the shape, but raise this if a handle ever loses hover to the surface.</summary>
	[Property, Range( 0f, 10f ), Group( "Master" )] public float GizmoDepthBias { get; set; } = 0f;

	// --- Rotation (screen sizes) ---

	/// <summary>On-screen radius of the rotation rings. The screen-axis ring and the plane squares are
	/// sized relative to this.</summary>
	[Property, Group( "Rotation" )] public float RotationRadius { get; set; } = 80f;

	/// <summary>Radius of the outer screen-axis ring relative to the per-axis rings. 1 = same size,
	/// 1.2 = 20% larger. Keep above 1 so it sits outside the coloured rings.</summary>
	[Property, Group( "Rotation" )] public float ScreenRingScale { get; set; } = 1.2f;

	/// <summary>How many straight segments make up each rotation ring. Higher = smoother circle for a
	/// small drawing cost. Raise this if the rings look faceted.</summary>
	[Property, Range( 16, 256 ), Group( "Rotation" )] public int RingSegments { get; set; } = 64;

	// --- Translation (screen sizes) ---

	/// <summary>On-screen distance from the centre to the inner end (start) of each move line. Leaves
	/// room for the centre handle.</summary>
	[Property, Group( "Translation" )] public float TranslationMin { get; set; } = 16f;

	/// <summary>On-screen distance from the centre to the outer end of each move line — where the tip
	/// and scale dot sit.</summary>
	[Property, Group( "Translation" )] public float TranslationMax { get; set; } = 90f;

	/// <summary>On-screen distance to push both ends of the move lines outward. Added to Min and Max.</summary>
	[Property, Group( "Translation" )] public float TranslationOffset { get; set; } = 0f;

	// --- Centre handle ---

	/// <summary>Half-size of the centre screen-move square, as a fraction of the translation reach. Keep
	/// it below <see cref="TranslationMin"/> so it sits inside the gap between the move lines.</summary>
	[Property, Range( 0.02f, 0.5f ), Group( "Screen Move" )] public float ScreenMoveScale { get; set; } = 0.12f;

	/// <summary>Corner rounding of the screen-move square, as a fraction of its half-size. 0 = sharp
	/// square, 1 = fully rounded (a circle).</summary>
	[Property, Range( 0f, 1f ), Group( "Screen Move" )] public float ScreenMoveRounding { get; set; } = 0.35f;


	// --- Uniform scale (screen sizes) ---

	/// <summary>On-screen distance up and right from the centre to pin the uniform-scale circle.</summary>
	[Property, Group( "Uniform Scale" )] public float UniformScaleOffset { get; set; } = 95f;

	/// <summary>On-screen radius of the uniform-scale circle.</summary>
	[Property, Group( "Uniform Scale" )] public float UniformScaleRadius { get; set; } = 7f;

	/// <summary>On-screen drag distance (px) that doubles or halves the shape. Smaller = more sensitive.</summary>
	[Property, Group( "Uniform Scale" )] public float UniformScaleSensitivity { get; set; } = 150f;

	// --- Sliders (screen sizes) ---
	// NOTE: only the editor gizmo (BrushTransformGizmo) still draws these 3D sliders. The in-game runtime
	// gizmo edits blend/rounding via the 2D ClaySliders in the HUD instead, so these settings are
	// editor-only — kept here because GizmoSettings is shared by both gizmos.

	/// <summary>On-screen distance below the gizmo centre to the first (blend) slider.</summary>
	[Property, Group( "Sliders" )] public float SliderOffset { get; set; } = 180f;

	/// <summary>On-screen gap between the blend slider and the rounding slider.</summary>
	[Property, Group( "Sliders" )] public float SliderSpacing { get; set; } = 26f;

	/// <summary>Thickness of the slider lines, in screen pixels.</summary>
	[Property, Group( "Sliders" )] public float SliderThickness { get; set; } = 4f;

	/// <summary>On-screen length of the value fill at 0 — a small buffer so an empty slider still shows a
	/// dot rather than vanishing.</summary>
	[Property, Group( "Sliders" )] public float SliderBuffer { get; set; } = 8f;

	/// <summary>Brush blend value mapped to the far-right end of the top slider (the brush itself hard-caps
	/// at <see cref="SdfBrush.MaxBlend"/>, so going higher than that just wastes slider travel).</summary>
	[Property, Group( "Sliders" )] public float SliderBlendMax { get; set; } = SdfBrush.MaxBlend;

	/// <summary>Brush rounding value mapped to the LEFT end of the bottom slider. SDF shapes look odd
	/// perfectly sharp, so this defaults to 0.75 rather than 0. The right end is the shape's own maximum
	/// rounding, so a full slider is always fully rounded.</summary>
	[Property, Group( "Sliders" )] public float SliderRoundingMin { get; set; } = 0.75f;

	// --- Lines (thickness, screen pixels) ---

	/// <summary>Thickness of the move axis lines (X/Y/Z) when idle.</summary>
	[Property, Group( "Lines" )] public float MoveWidth { get; set; } = 5f;

	/// <summary>Thickness of the rotation rings when idle.</summary>
	[Property, Group( "Lines" )] public float RingWidth { get; set; } = 4f;

	/// <summary>Thickness of the wireframe outline drawn around each brush shape.</summary>
	[Property, Group( "Lines" )] public float OutlineWidth { get; set; } = 2.5f;

	/// <summary>Width of the rounded line-end caps as a multiple of the line's own width: 0 = off,
	/// 1 = exactly as wide as the line, 2 = twice as wide.</summary>
	[Property, Range( 0f, 3f ), Group( "Lines" )] public float LineCapScale { get; set; } = 1f;

	// --- Scale dot (screen size) ---

	/// <summary>On-screen radius of the scale dots at the move-line tips.</summary>
	[Property, Group( "Scale Dot" )] public float DotRadius { get; set; } = 6f;

	/// <summary>On-screen distance to shift the scale dots along their axis, from the move-line tip.
	/// Positive pushes them outward (past the tip), negative pulls them inward.</summary>
	[Property, Group( "Scale Dot" )] public float ScaleDotOffset { get; set; } = 0f;


	// --- Plane square (relative to ring radius) ---

	/// <summary>How far the corner plane-move squares sit from the centre, as a fraction of the ring
	/// radius. Higher = pushed further out toward the ring.</summary>
	[Property, Group( "Plane Square" )] public float PlaneOffsetFrac { get; set; } = 0.35f;

	/// <summary>Half-size of the plane-move squares, as a fraction of the translation reach — the same
	/// basis as <see cref="ScreenMoveScale"/>, so setting the two equal makes the squares match the
	/// centre handle's size.</summary>
	[Property, Range( 0.02f, 0.5f ), Group( "Plane Square" )] public float PlaneScale { get; set; } = 0.12f;

	/// <summary>Corner rounding of the plane-move squares, as a fraction of their half-size. 0 = sharp
	/// square, 1 = fully rounded (a circle).</summary>
	[Property, Range( 0f, 1f ), Group( "Plane Square" )] public float PlaneRounding { get; set; } = 0.35f;


	// --- Spline (per-control-point handles + the connecting line) ---

	/// <summary>Thickness of the spline's centre line, in screen pixels (× <see cref="Thickness"/>).</summary>
	[Property, Group( "Spline" )] public float SplineLineWidth { get; set; } = 4f;

	/// <summary>Opacity of the spline centre line when idle.</summary>
	[Property, Range( 0f, 1f ), Group( "Spline" )] public float SplineLineOpacity { get; set; } = 0.5f;

	/// <summary>Opacity of the spline centre line while it's hovered (the add-point affordance).</summary>
	[Property, Range( 0f, 1f ), Group( "Spline" )] public float SplineLineHoverOpacity { get; set; } = 1f;

	/// <summary>Colour of the spline centre line, the control-point move dots and the in-between dots.</summary>
	[Property, Group( "Spline" )] public Color SplineLineColor { get; set; } = new( 0.85f, 0.85f, 0.9f );

	/// <summary>How many evenly-spaced dots are drawn ALONG the line between each pair of control points (the
	/// "in-between" dots that hint the line is interactive). 0 = none — just the solid line.</summary>
	[Property, Range( 0, 16 ), Group( "Spline" )] public int SplineMidDots { get; set; } = 3;

	/// <summary>On-screen radius of those in-between dots.</summary>
	[Property, Group( "Spline" )] public float SplineMidDotRadius { get; set; } = 2.5f;

	/// <summary>On-screen radius of the control-point MOVE dots (drag to reposition the point).</summary>
	[Property, Group( "Spline" )] public float SplinePointRadius { get; set; } = 6f;

	/// <summary>Radius of the rim RADIUS dot relative to the move dot. 1 = same size, 0.8 = 20% smaller.</summary>
	[Property, Range( 0.2f, 1.5f ), Group( "Spline" )] public float SplineRadiusDotScale { get; set; } = 0.8f;

	/// <summary>Colour of the rim radius dot (drag in/out to resize that control point).</summary>
	[Property, Group( "Spline" )] public Color SplineRadiusColor { get; set; } = new( 1f, 0.8f, 0.25f );

	/// <summary>How close to the spline's centre line the cursor must be to hover it (for the add-point cursor
	/// + click-to-insert), as a fraction of the tube radius. 1 = anywhere on the tube body; lower = must be
	/// nearer the centre line. A small screen-pixel grab margin is always added on top so thin tubes stay
	/// grabbable.</summary>
	[Property, Range( 0.1f, 1f ), Group( "Spline" )] public float SplineLineHitScale { get; set; } = 0.45f;

	// --- resolved values (no camera needed — pure multipliers / fractions) ---

	/// <summary>Spline line thickness (px), hovered or idle, with the master Thickness applied.</summary>
	public float SplineThickness( bool hot ) => SplineLineWidth * (hot ? HotScale : 1f) * Thickness;

	/// <summary>Spline line opacity for the given hover state.</summary>
	public float SplineOpacity( bool hot ) => hot ? SplineLineHoverOpacity : SplineLineOpacity;

	public float MoveThickness( bool hot ) => MoveWidth * (hot ? HotScale : 1f) * Thickness;
	public float RingThickness( bool hot ) => RingWidth * (hot ? HotScale : 1f) * Thickness;
	public float OutlineThickness => OutlineWidth * Thickness;

	public float PlaneOffset( float r ) => r * PlaneOffsetFrac;

	/// <summary>Hash of every value that affects the drawn gizmo, so the runtime gizmo re-uploads its mesh
	/// the instant a setting is tweaked (its draw cache keys on this).</summary>
	public int VisualHash()
	{
		var h = new HashCode();
		h.Add( ShowTranslation ); h.Add( ShowScreenMove ); h.Add( ShowRotation ); h.Add( ShowScreenRotation );
		h.Add( ShowScale ); h.Add( ShowUniformScale ); h.Add( ShowSliders ); h.Add( ShowPlaneSquares );
		h.Add( OccludeRingBackHalf ); h.Add( GizmoScale ); h.Add( ReferenceHeight ); h.Add( Thickness ); h.Add( HotScale );
		h.Add( HitboxPadding ); h.Add( RotationRadius ); h.Add( ScreenRingScale ); h.Add( RingSegments );
		h.Add( TranslationMin ); h.Add( TranslationMax ); h.Add( TranslationOffset );
		h.Add( ScreenMoveScale ); h.Add( ScreenMoveRounding );
		h.Add( UniformScaleOffset ); h.Add( UniformScaleRadius );
		h.Add( SliderOffset ); h.Add( SliderSpacing ); h.Add( SliderThickness ); h.Add( SliderBuffer );
		h.Add( SliderBlendMax ); h.Add( SliderRoundingMin );
		h.Add( MoveWidth ); h.Add( RingWidth ); h.Add( OutlineWidth ); h.Add( LineCapScale );
		h.Add( DotRadius ); h.Add( ScaleDotOffset );
		h.Add( PlaneOffsetFrac ); h.Add( PlaneScale ); h.Add( PlaneRounding );
		h.Add( SplineLineWidth ); h.Add( SplineLineOpacity ); h.Add( SplineLineHoverOpacity ); h.Add( SplineLineColor );
		h.Add( SplineMidDots ); h.Add( SplineMidDotRadius ); h.Add( SplinePointRadius );
		h.Add( SplineRadiusDotScale ); h.Add( SplineRadiusColor );
		return h.ToHashCode();
	}
}
