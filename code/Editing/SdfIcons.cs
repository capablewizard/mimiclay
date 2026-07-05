namespace Mimiclay;

/// <summary>
/// The single place that maps sculpt concepts to icon glyphs for the edit HUD. Values are Material Icons
/// ligature names (https://fonts.google.com/icons) — change one here and every layer row updates.
/// </summary>
public static class SdfIcons
{
	/// <summary>Glyph for a shape kind (the shape symbol on each layer row).</summary>
	public static string Shape( SdfShape shape ) => shape switch
	{
		SdfShape.Sphere => "circle",
		SdfShape.Box => "square",
		SdfShape.Cylinder => "database",        // closest stock glyph to a cylinder
		SdfShape.Cone => "change_history",      // triangle
		SdfShape.Extruded => "details",         // filled triangle
		SdfShape.Spline => "polyline",          // multi-point path
		SdfShape.Text => "text_fields",         // "T" glyph
		_ => "category",
	};

	/// <summary>Glyph for an extruded brush's 2D profile (the cross-section picker chips under the sliders).</summary>
	public static string CrossSection( SdfCrossSection xs ) => xs switch
	{
		SdfCrossSection.Star => "star",
		SdfCrossSection.Hexagon => "hexagon",
		SdfCrossSection.Pentagon => "pentagon",
		_ => "change_history", // triangle
	};

	/// <summary>Path to the rendered clay icon for a shape kind (used by the "Add Shape" tiles).</summary>
	public static string ShapeImage( SdfShape shape ) => shape switch
	{
		SdfShape.Sphere => "/ui/shapeicons/shape_sphere_large.png",
		SdfShape.Box => "/ui/shapeicons/shape_cube_large.png",
		SdfShape.Cylinder => "/ui/shapeicons/shape_cylinder_large.png",
		SdfShape.Cone => "/ui/shapeicons/shape_cone_large.png",
		SdfShape.Extruded => "/ui/shapeicons/shape_prism_large.png",
		_ => "/ui/shapeicons/shape_sphere_large.png",
	};

	/// <summary>
	/// Small white shape icon for a layer row — drawn on transparency and tinted by the brush colour
	/// (multiply) so one image serves as both shape + colour swatch.
	/// </summary>
	public static string ShapeImageSmall( SdfShape shape ) => shape switch
	{
		SdfShape.Sphere => "/ui/shapeicons/icon_sphere_small.png",
		SdfShape.Box => "/ui/shapeicons/icon_cube_small.png",
		SdfShape.Cylinder => "/ui/shapeicons/icon_cylinder_small.png",
		SdfShape.Cone => "/ui/shapeicons/icon_cone_small.png",
		SdfShape.Extruded => "/ui/shapeicons/icon_prism_small.png",
		_ => "/ui/shapeicons/icon_sphere_small.png",
	};

	// Fixed glyphs the row uses, kept here so all icon choices live in one spot.
	public const string Visible = "visibility";
	public const string Hidden = "visibility_off";
	public const string Add = "add";
	public const string Subtract = "remove";
	public const string Symmetry = "crop_free"; // corner brackets, like the reference
	public const string Delete = "delete";
	public const string Duplicate = "content_copy";
	public const string DragHandle = "drag_indicator"; // the ":::" grip shown at the left of a draggable row
}
