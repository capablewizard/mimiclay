using System;

namespace Mimiclay;

public enum SdfShape
{
	Sphere,
	Box,
	Cylinder,
	Cone,
	Extruded,
	/// <summary>Legacy alias for <see cref="Extruded"/> — kept so saved prefabs / .sculpt JSON (and the
	/// PropGen tool) that serialised "TriangularPrism" still parse. Same value, so it's the same shape.</summary>
	TriangularPrism = Extruded,
	Spline,
	/// <summary>Extruded text: the brush's <see cref="SdfBrush.Text"/> string rendered to a baked 2D distance
	/// field (see <see cref="SdfTextData"/>) and swept along local Z like the other extruded profiles.</summary>
	Text,
}

/// <summary>The 2D profile an <see cref="SdfShape.Extruded"/> brush sweeps along its local Z. Triangle keeps the
/// old prism's isosceles Size.x/Size.y params; star and hexagon are regular, with Size.x as the circumradius
/// (Size.y unused — like the cylinder). New profiles slot in here + the shader dispatch, nowhere else.</summary>
public enum SdfCrossSection
{
	Triangle,
	Star,
	Hexagon,
	Pentagon, // appended (packed as its enum value) — keep order stable for saved brushes
}

public enum SdfOperation
{
	Add,
	Subtract,
}

/// <summary>
/// A single signed-distance primitive plus how it combines with the ones before it.
/// This is the unit of authoring: a sculpt is just an ordered list of these.
/// </summary>
public class SdfBrush
{
	[Property] public SdfShape Shape { get; set; } = SdfShape.Sphere;
	[Property] public SdfOperation Operation { get; set; } = SdfOperation.Add;

	/// <summary>The 2D profile the <see cref="SdfShape.Extruded"/> shape sweeps along its local Z (ignored by
	/// every other shape). Defaults to Triangle so pre-cross-section saves deserialise to the old prism.</summary>
	[Property] public SdfCrossSection CrossSection { get; set; } = SdfCrossSection.Triangle;

	/// <summary>The string the <see cref="SdfShape.Text"/> shape renders (ignored by every other shape).
	/// Serialises/networks like any brush field; each machine bakes the same distance field locally.</summary>
	[Property, Group( "Text" )] public string Text { get; set; } = "clay";

	/// <summary>Font family for the <see cref="SdfShape.Text"/> shape. Fonts shipped in Assets/fonts resolve
	/// by their embedded family name, same as UI styles — e.g. "Super Joyful" (the theme's display font,
	/// the default), "Winky Sans", "Super Cartoon", or the engine's "Inter".</summary>
	[Property, Group( "Text" )] public string Font { get; set; } = "Super Joyful";

	/// <summary>The baked distance field for <see cref="Text"/>/<see cref="Font"/> — set on the MAIN thread
	/// by the pack/rebuild paths (via <see cref="SdfTextSdf.Get"/>) and only read elsewhere; worker-thread
	/// brush snapshots share the immutable instance. A plain field so the JSON serializer skips it.</summary>
	public SdfTextData TextData;

	/// <summary>Visibility toggle (the eye button in the edit HUD). A disabled brush is skipped entirely by
	/// the field, so it contributes nothing to the mesh — like temporarily removing it without losing it.</summary>
	[Property] public bool Enabled { get; set; } = true;

	[Property] public Vector3 Position { get; set; }
	[Property] public Rotation Rotation { get; set; } = Rotation.Identity;

	/// <summary>Sphere uses <c>Size.x</c> as the radius. Box uses <c>Size</c> as half-extents. Triangular
	/// prism uses <c>Size.x</c> as base half-width, <c>Size.y</c> as triangle half-height, <c>Size.z</c> as depth.</summary>
	[Property] public Vector3 Size { get; set; } = 24f;

	/// <summary>Spline control points, in SCULPTURE-LOCAL space: <c>xyz</c> = position, <c>w</c> = radius. The
	/// shape is a variable-radius tube — consecutive points joined by rounded cones (straight segments for now;
	/// a curvature pass can tessellate a curve through these later). Each point is edited by its own move + radius
	/// dots, so there's no whole-spline transform gizmo; <see cref="Position"/>/<see cref="Rotation"/> are unused
	/// by spline geometry. Stored as Vector4 (not a custom type) so it serialises and networks like the rest.</summary>
	[Property] public List<Vector4> Points { get; set; }

	/// <summary>Spline curvature: 0 = straight segments between control points (each span a rounded cone);
	/// 1 = a smooth Catmull-Rom curve through them (tangent strength scales with this). Tessellated into
	/// <see cref="SplineTessellation"/> sub-segments per span when &gt; 0.</summary>
	[Property, Range( 0f, 1f ), Group( "Spline" )] public float Curvature { get; set; } = 1f;

	/// <summary>Closed spline: also join the last control point back to the first (a loop / ring), with the
	/// Catmull-Rom tangents wrapping around instead of clamping at the ends. Toggled by the ghost-arc handle on
	/// the gizmo.</summary>
	[Property, Group( "Spline" )] public bool SplineClosed { get; set; }

	/// <summary>Sub-segments per span when the spline is curved (Curvature &gt; 0). Higher = smoother but more
	/// cost in the live per-brush march (the field cache / mesh bake it once, so they don't care).</summary>
	public const int SplineTessellation = 8;

	/// <summary>Hard cap on <see cref="Blend"/> — beyond this the smooth-union bulge balloons the shape.</summary>
	public const float MaxBlend = 15f;

	/// <summary>Ceiling for <see cref="Slice"/>: the deepest allowed cut still leaves a sliver of shape, so a
	/// maxed slider can never make the brush vanish (an empty field would just silently drop the brush).</summary>
	public const float MaxSlice = 0.95f;

	/// <summary>Planar slice for the sliceable shapes (sphere and cone for now; ignored by the rest): cut the
	/// shape flat from its local +Z top. 0 = untouched, 0.5 = cut at the mid-plane (hemisphere / half-height
	/// frustum), <see cref="MaxSlice"/> leaves a sliver. Slicing a cone turns its tip into a flat frustum top.</summary>
	[Property, Range( 0f, MaxSlice )] public float Slice { get; set; } = 0f;

	/// <summary>The slice plane's height as a fraction of the shape's local half-height (+1 = the untouched
	/// top, −1 = the bottom): <c>z = Size.z * SlicePlaneN</c>. 1 when unsliced. The single conversion the SDFs,
	/// mesh proxies and colliders all share, so their cut planes can't disagree.</summary>
	public float SlicePlaneN => 1f - 2f * MathF.Min( MathF.Max( Slice, 0f ), MaxSlice );

	/// <summary>The shape's geometric centre, offset from <see cref="Position"/> in the brush's LOCAL frame.
	/// Zero for the centred shapes; the cone pivots at its BASE (the gizmo sits where the cone stands and
	/// scaling grows it upward), so its centre is half a height up local Z. Bounds, mirror centres and every
	/// mesh proxy share this one definition so they can't disagree with the SDF about where the shape sits.</summary>
	public Vector3 LocalCentre => Shape == SdfShape.Cone ? new Vector3( 0f, 0f, Size.z ) : Vector3.Zero;

	/// <summary>Smooth-blend radius when merging/cutting against the rest of the field. An ABSOLUTE distance
	/// in the sculpture's local space (not relative to the brush's size). Capped at <see cref="MaxBlend"/> via
	/// the inspector range and the gizmo slider max (kept an auto-property so it serialises like the rest).</summary>
	[Property, Range( 0f, MaxBlend )] public float Blend { get; set; } = 6f;

	/// <summary>Corner-rounding radius (box and future shapes). Kept at a 0.75 minimum — SDF shapes look
	/// odd perfectly sharp.</summary>
	[Property, Range( 0.75f, 64f )] public float Rounding { get; set; } = 0.75f;

	[Property] public Color Color { get; set; } = Color.White;

	/// <summary>PBR metalness of this brush's surface. Blended across smooth-union seams exactly like Color.</summary>
	[Property, Range( 0f, 1f )] public float Metallic { get; set; } = 0f;

	/// <summary>PBR roughness of this brush's surface (1 = matte clay, the default). Blended like Color.</summary>
	[Property, Range( 0f, 1f )] public float Roughness { get; set; } = 1f;

	/// <summary>Mirror this brush across the sculpture's local YZ plane (x=0): the primitive is unioned with
	/// its reflection. In an SDF this needs no second brush — the shape is just sampled again at the mirrored
	/// point, so there's nothing to keep in sync. Combine axes for quadrant/octant symmetry.</summary>
	[Property, Group( "Symmetry" )] public bool MirrorX { get; set; }

	/// <summary>Mirror across the sculpture's local XZ plane (y=0). See <see cref="MirrorX"/>.</summary>
	[Property, Group( "Symmetry" )] public bool MirrorY { get; set; }

	/// <summary>Mirror across the sculpture's local XY plane (z=0). See <see cref="MirrorX"/>.</summary>
	[Property, Group( "Symmetry" )] public bool MirrorZ { get; set; }

	/// <summary>A standalone copy (every field is a value type). Used to snapshot brushes before meshing
	/// on a worker thread, so a main-thread edit can't race the build.</summary>
	public SdfBrush Copy() => new()
	{
		Shape = Shape,
		CrossSection = CrossSection,
		Text = Text,
		Font = Font,
		TextData = TextData, // immutable once baked — sharing the reference is thread-safe
		Operation = Operation,
		Enabled = Enabled,
		Position = Position,
		Rotation = Rotation,
		Size = Size,
		Blend = Blend,
		Rounding = Rounding,
		Slice = Slice,
		Color = Color,
		Metallic = Metallic,
		Roughness = Roughness,
		MirrorX = MirrorX,
		MirrorY = MirrorY,
		MirrorZ = MirrorZ,
		Points = Points is null ? null : new List<Vector4>( Points ), // deep copy (Vector4 is a value type)
		Curvature = Curvature,
		SplineClosed = SplineClosed,
	};

	/// <summary>Set this brush to the blend between <paramref name="a"/> and <paramref name="b"/> at
	/// <paramref name="t"/> — IN PLACE (no allocation), for smoothing the networked live-drag stream on proxies.
	/// Only the values a gizmo drag moves CONTINUOUSLY are interpolated: Position/Size/Blend/Rounding lerp,
	/// Rotation slerps. Everything else SNAPS to <paramref name="b"/> — Color/Metallic/Roughness and the
	/// structural fields (Shape/Operation/Enabled/Mirror) are only ever set discretely, so blending them would
	/// smear through in-between values that never existed (a fade between two picked colours, a half-applied
	/// shape swap), which looks worse than a clean snap.</summary>
	public void LerpFrom( SdfBrush a, SdfBrush b, float t )
	{
		Position = Vector3.Lerp( a.Position, b.Position, t );
		Rotation = Rotation.Slerp( a.Rotation, b.Rotation, t );
		Size = Vector3.Lerp( a.Size, b.Size, t );
		Blend = MathX.Lerp( a.Blend, b.Blend, t );
		Rounding = MathX.Lerp( a.Rounding, b.Rounding, t );
		Slice = MathX.Lerp( a.Slice, b.Slice, t );
		Curvature = MathX.Lerp( a.Curvature, b.Curvature, t );

		// Spline points lerp position+radius continuously when the layout matches (a moved/resized point);
		// a count change is structural, so snap to b (interpolating across an add/remove has no meaning).
		if ( b.Points is null )
			Points = null;
		else if ( a.Points is { } pa && pa.Count == b.Points.Count )
		{
			Points ??= new List<Vector4>( b.Points.Count );
			Points.Clear();
			for ( int i = 0; i < b.Points.Count; i++ )
			{
				var u = pa[i];
				var w = b.Points[i];
				Points.Add( new Vector4( MathX.Lerp( u.x, w.x, t ), MathX.Lerp( u.y, w.y, t ),
					MathX.Lerp( u.z, w.z, t ), MathX.Lerp( u.w, w.w, t ) ) );
			}
		}
		else
			Points = new List<Vector4>( b.Points );

		// Discrete / material — snap to the target, never interpolated (see summary).
		Shape = b.Shape;
		CrossSection = b.CrossSection;
		Text = b.Text;
		Font = b.Font;
		TextData = b.TextData;
		Operation = b.Operation;
		Enabled = b.Enabled;
		Color = b.Color;
		Metallic = b.Metallic;
		Roughness = b.Roughness;
		MirrorX = b.MirrorX;
		MirrorY = b.MirrorY;
		MirrorZ = b.MirrorZ;
		SplineClosed = b.SplineClosed; // structural — snap, never interpolate
	}

	/// <summary>Signed distance from <paramref name="p"/> (sculpture-local space) to this brush, including
	/// any mirror-symmetry copies.</summary>
	public float Distance( Vector3 p )
	{
		float d = PrimitiveDistance( p );

		if ( !MirrorX && !MirrorY && !MirrorZ )
			return d;

		// Mirror symmetry: union the primitive with its reflection across each enabled sculpture-origin
		// plane. Enumerate every sign combination of the enabled axes — 1 extra copy for one axis, 3 for
		// two, 7 for three — so e.g. X+Y gives true quadrant symmetry (all four corners), not just two
		// halves. The seam fuses with the brush's own Blend (smooth-min), exactly like the rest of the field.
		int nx = MirrorX ? 1 : 0, ny = MirrorY ? 1 : 0, nz = MirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
			for ( int sy = 0; sy <= ny; sy++ )
				for ( int sz = 0; sz <= nz; sz++ )
				{
					if ( sx == 0 && sy == 0 && sz == 0 )
						continue; // the un-mirrored primitive, already in d

					var q = new Vector3( sx == 1 ? -p.x : p.x, sy == 1 ? -p.y : p.y, sz == 1 ? -p.z : p.z );
					d = Sdf.SmoothUnion( d, PrimitiveDistance( q ), Blend );
				}

		return d;
	}

	/// <summary>Signed distance to the bare primitive (no symmetry). <paramref name="p"/> is in
	/// sculpture-local space.</summary>
	float PrimitiveDistance( Vector3 p )
	{
		// The spline evaluates directly in sculpture-local space — its control points carry their own
		// positions, so Position/Rotation don't apply (and shouldn't move the point cloud).
		if ( Shape == SdfShape.Spline )
			return SplineDistance( p );

		// Move the sample point into the brush's local space so the primitive math is axis-aligned.
		var lp = Rotation.Inverse * (p - Position);

		return Shape switch
		{
			SdfShape.Box => BoxDistance( lp, Size, Rounding ),
			SdfShape.Cylinder => CylinderDistance( lp, Size, Rounding ),
			SdfShape.Cone => ConeBasePivot( lp, Size, Rounding, Slice ),
			SdfShape.Extruded => ExtrudedDistance( lp, Size, Rounding, CrossSection ),
			SdfShape.Text => TextDistance( lp, Size, Rounding, TextData ),
			_ => SliceRound( EllipsoidDistance( lp, Size ), lp.z, MathF.Max( Size.z, 1e-3f ), Slice,
				MathF.Min( Rounding, SphereSliceMaxRounding( Size, Slice ) ) ),
		};
	}

	// The biggest rim fillet a sliced sphere/ellipsoid fits: no more than the smallest radius, half the
	// remaining thickness (below that the erode-dilate eats the piece from below), or the CAP DEPTH — the
	// material depth under the cut plane on the axis (2·rz·slice). The fillet's flat region needs the shape
	// to be at least r deep below the cut; past that the whole top rounds off and SINKS below the cut plane
	// (the "rounding fights the slice" failure). At exactly the depth limit the flat shrinks to a point and
	// the slice becomes a smooth dome kissing the cut plane — the ideal fully-rounded look. Shared by
	// MaxRounding (the slider max) and the SDF's clamp.
	static float SphereSliceMaxRounding( Vector3 size, float slice )
	{
		float s = Math.Clamp( slice, 0f, MaxSlice );
		if ( s <= 0f )
			return float.MaxValue; // unsliced: rounding is a no-op, nothing to cap
		float rz = MathF.Max( size.z, 1e-3f );
		return MathF.Min( MathF.Min( size.x, MathF.Min( size.y, size.z ) ),
			MathF.Min( 2f * rz * s, rz * (1f - s) ) );
	}

	// The cone pivots at its BASE: Position sits on the base disc and the shape grows up local +Z (so the
	// gizmo sits where the cone stands, and scaling grows it upward). Shift into the centred frame the cone
	// math uses. LocalCentre/LocalBounds/proxies share this same offset. Slice is folded INTO the cone
	// primitive (a frustum) rather than composed as a plane cut, so one rounding pass covers every edge.
	static float ConeBasePivot( Vector3 lp, Vector3 size, float rounding, float slice )
	{
		lp.z -= size.z;
		return ConeDistance( lp, size, rounding, slice );
	}

	// Planar slice shared by the sliceable shapes: intersect the primitive with the half-space below
	// z = halfH·SlicePlaneN, with the rim rounded by the Round slider. Rounding is the extrusions' combine:
	// erode the shape (d + r insets a well-behaved SDF by r) and the plane by r, intersect with the
	// rounded-corner join, dilate by r — so the flat cut stays put, the rim gets a quarter-round of radius r,
	// and the result stays inside the sharp silhouette. r is clamped to half the remaining thickness so a
	// deep slice can't erode the piece away. max() is a slight distance underestimate near the edge, which
	// the sphere-tracer and mesher both tolerate (steps just shorten, never overshoot).
	static float SliceRound( float d, float z, float halfH, float slice, float rounding )
	{
		if ( slice <= 0f )
			return d;
		float zcut = halfH * (1f - 2f * MathF.Min( slice, MaxSlice ));
		float dz = z - zcut;
		float r = Math.Clamp( rounding, 0f, (zcut + halfH) * 0.5f );
		if ( r <= 1e-3f )
			return MathF.Max( d, dz ); // hard slice
		float wx = d + r, wy = dz + r;
		float ox = MathF.Max( wx, 0f ), oy = MathF.Max( wy, 0f );
		return MathF.Min( MathF.Max( wx, wy ), 0f ) + MathF.Sqrt( ox * ox + oy * oy ) - r;
	}

	/// <summary>Variable-radius tube through the control points: the nearest of the rounded cones joining
	/// each consecutive pair. Points share endpoints, so a plain min welds them seam-free (no inter-segment
	/// blend needed). <paramref name="p"/> and the points are both sculpture-local.</summary>
	float SplineDistance( Vector3 p )
	{
		var pts = Points;
		if ( pts is null || pts.Count == 0 )
			return 1e9f;

		if ( pts.Count == 1 )
			return (p - new Vector3( pts[0].x, pts[0].y, pts[0].z )).Length - pts[0].w;

		// Straight (Curvature 0) → one rounded cone per span (K=1). Curved → tessellate each span into K
		// Catmull-Rom sub-segments. K=1 with t=1 returns the span endpoint, so both paths share this loop.
		int n = pts.Count;
		int k = Curvature <= 1e-4f ? 1 : SplineTessellation;
		bool closed = IsSplineLoop;
		int segs = SplineSpanCount( n, closed );
		float d = 1e9f;

		for ( int i = 0; i < segs; i++ )
		{
			SplineSpan( i, n, closed, out int i0, out int i1, out int i2, out int i3 );
			var p0 = pts[i0];
			var p1 = pts[i1];
			var p2 = pts[i2];
			var p3 = pts[i3];

			var prev = p1;
			for ( int s = 1; s <= k; s++ )
			{
				var cur = CatmullRomPoint( p0, p1, p2, p3, s / (float)k, Curvature );
				d = MathF.Min( d, RoundedCone( p, prev, cur ) );
				prev = cur;
			}
		}
		return d;
	}

	/// <summary>Hermite/Catmull-Rom interpolation of a control point (xyz position + w radius) at
	/// <paramref name="t"/> in [0,1] along the span p1→p2, with neighbours p0/p3 setting the tangents scaled
	/// by <paramref name="curvature"/> (0 = straight line, 1 = full Catmull-Rom). Radius is clamped &gt; 0.</summary>
	public static Vector4 CatmullRomPoint( Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3, float t, float curvature )
	{
		float t2 = t * t, t3 = t2 * t;
		float h00 = 2f * t3 - 3f * t2 + 1f;
		float h10 = t3 - 2f * t2 + t;
		float h01 = -2f * t3 + 3f * t2;
		float h11 = t3 - t2;

		float C( float a0, float a1, float a2, float a3 )
		{
			float m1 = (a2 - a0) * 0.5f * curvature; // tangent at p1
			float m2 = (a3 - a1) * 0.5f * curvature; // tangent at p2
			return h00 * a1 + h10 * m1 + h01 * a2 + h11 * m2;
		}

		return new Vector4(
			C( p0.x, p1.x, p2.x, p3.x ),
			C( p0.y, p1.y, p2.y, p3.y ),
			C( p0.z, p1.z, p2.z, p3.z ),
			MathF.Max( C( p0.w, p1.w, p2.w, p3.w ), 0.1f ) ); // curve can overshoot — keep radius positive
	}

	/// <summary>Whether this spline is actually treated as a closed loop (needs at least 3 points).</summary>
	public bool IsSplineLoop => SplineClosed && Points is { Count: >= 3 };

	// Span count for an n-point spline: a closed loop adds the wrap-around span (last → first).
	static int SplineSpanCount( int n, bool closed ) => closed ? n : n - 1;

	// The four Catmull-Rom control indices for span i. Closed wraps modularly; open clamps the phantom
	// endpoints to the ends. i1→i2 is the span itself; i0/i3 are the tangent neighbours.
	static void SplineSpan( int i, int n, bool closed, out int i0, out int i1, out int i2, out int i3 )
	{
		i1 = i;
		if ( closed )
		{
			i0 = (i - 1 + n) % n;
			i2 = (i + 1) % n;
			i3 = (i + 2) % n;
		}
		else
		{
			i0 = i > 0 ? i - 1 : 0;
			i2 = i + 1;
			i3 = i < n - 2 ? i + 2 : n - 1;
		}
	}

	/// <summary>Fills <paramref name="dst"/> with the spline's drawn polyline (xyz position + w radius) —
	/// the control points when straight, the tessellated Catmull-Rom curve when curved. Used by the
	/// wireframe/outline drawers so they match the rendered tube.</summary>
	public void BuildSplinePolyline( List<Vector4> dst )
	{
		dst.Clear();
		var pts = Points;
		if ( pts is null || pts.Count == 0 )
			return;
		if ( pts.Count == 1 )
		{
			dst.Add( pts[0] );
			return;
		}

		int n = pts.Count;
		int k = Curvature <= 1e-4f ? 1 : SplineTessellation;
		bool closed = IsSplineLoop;
		int segs = SplineSpanCount( n, closed );
		dst.Add( pts[0] );
		for ( int i = 0; i < segs; i++ )
		{
			SplineSpan( i, n, closed, out int i0, out int i1, out int i2, out int i3 );
			var p0 = pts[i0];
			var p1 = pts[i1];
			var p2 = pts[i2];
			var p3 = pts[i3];
			for ( int s = 1; s <= k; s++ )
				dst.Add( CatmullRomPoint( p0, p1, p2, p3, s / (float)k, Curvature ) );
		}
		// closed: the final span ends back at pts[0], so the polyline returns to start and reads as a loop.
	}

	/// <summary>Fills <paramref name="dst"/> with OVERLAPPING spheres (xyz centre, w radius) swept along the
	/// curve — spaced finer than each local radius so they merge into a continuous tube. This is the spline's
	/// selectable / hover shape (translucent ghost + click hitboxes) AND its collision shape (a sphere per
	/// sample). <paramref name="spacingFactor"/> sets the step as a fraction of the local radius: 0.6 (default)
	/// reads as a smooth tube for hover; collision passes 1.0 — coarser, still gap-free (spheres overlap while
	/// spacing &lt; 2r) but fewer physics shapes.</summary>
	public void BuildSplineSweep( List<Vector4> dst, float spacingFactor = 0.6f )
	{
		dst.Clear();
		var pts = Points;
		if ( pts is null || pts.Count == 0 )
			return;
		if ( pts.Count == 1 )
		{
			dst.Add( pts[0] );
			return;
		}

		int n = pts.Count;
		bool closed = IsSplineLoop;
		int segs = SplineSpanCount( n, closed );
		dst.Add( pts[0] );
		for ( int i = 0; i < segs; i++ )
		{
			SplineSpan( i, n, closed, out int i0, out int i1, out int i2, out int i3 );
			var p0 = pts[i0];
			var p1 = pts[i1];
			var p2 = pts[i2];
			var p3 = pts[i3];

			// Step finer than the local radius so adjacent spheres overlap into a tube (no gaps); follow the
			// curve too when curved. Capped so a long thin span can't explode the sphere count.
			float spanLen = (new Vector3( p2.x, p2.y, p2.z ) - new Vector3( p1.x, p1.y, p1.z )).Length;
			float minR = MathF.Max( 0.5f, MathF.Min( p1.w, p2.w ) );
			int k = Math.Clamp( (int)MathF.Ceiling( spanLen / (minR * spacingFactor) ), 1, 64 );
			if ( Curvature > 1e-4f )
				k = Math.Max( k, SplineTessellation );

			for ( int s = 1; s <= k; s++ )
				dst.Add( CatmullRomPoint( p0, p1, p2, p3, s / (float)k, Curvature ) );
		}
	}

	// IQ exact signed distance to a rounded cone: a tapered capsule from sphere (a.xyz, a.w) to (b.xyz, b.w).
	static float RoundedCone( Vector3 p, Vector4 a, Vector4 b )
	{
		Vector3 pa3 = new( a.x, a.y, a.z ), pb3 = new( b.x, b.y, b.z );
		float r1 = a.w, r2 = b.w;

		Vector3 ba = pb3 - pa3;
		float l2 = Vector3.Dot( ba, ba );
		if ( l2 < 1e-6f ) // coincident points — fall back to the larger sphere
			return (p - pa3).Length - MathF.Max( r1, r2 );

		float rr = r1 - r2;
		float a2 = l2 - rr * rr;
		float il2 = 1f / l2;

		Vector3 pa = p - pa3;
		float y = Vector3.Dot( pa, ba );
		float z = y - l2;
		Vector3 xv = pa * l2 - ba * y;
		float x2 = Vector3.Dot( xv, xv );
		float y2 = y * y * l2;
		float z2 = z * z * l2;

		float k = MathF.Sign( rr ) * rr * rr * x2;
		if ( MathF.Sign( z ) * a2 * z2 > k ) return MathF.Sqrt( x2 + z2 ) * il2 - r2;
		if ( MathF.Sign( y ) * a2 * y2 < k ) return MathF.Sqrt( x2 + y2 ) * il2 - r1;
		return (MathF.Sqrt( x2 * a2 * il2 ) + y * rr) * il2 - r1;
	}

	/// <summary>Writes the sculpture-local centres of this brush and its mirror images into
	/// <paramref name="dst"/> (capacity 8) and returns the count (1, 2, 4, or 8). Used to inflate bounds so
	/// the mesher's sample grid covers every mirrored copy.</summary>
	public int MirrorCentres( Span<Vector3> dst )
	{
		var c = Position + Rotation * LocalCentre; // the shape's true centre (base-pivot cone sits above Position)
		int n = 0;
		int nx = MirrorX ? 1 : 0, ny = MirrorY ? 1 : 0, nz = MirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
			for ( int sy = 0; sy <= ny; sy++ )
				for ( int sz = 0; sz <= nz; sz++ )
					dst[n++] = new Vector3( sx == 1 ? -c.x : c.x, sy == 1 ? -c.y : c.y, sz == 1 ? -c.z : c.z );
		return n;
	}

	/// <summary>The largest rounding this shape can take at its current size — the inscribed limit each
	/// Distance function clamps to (beyond it the shape is just fully rounded). Used to map a slider so
	/// "full" always means "fully rounded", whatever the shape.</summary>
	public float MaxRounding()
	{
		float wx = MathF.Max( Size.x, 1e-3f ), hy = MathF.Max( Size.y, 1e-3f );

		return Shape switch
		{
			SdfShape.Box => MathF.Min( Size.x, MathF.Min( Size.y, Size.z ) ),
			SdfShape.Cylinder => MathF.Min( Size.x, Size.z ),
			// Sliced-cone-aware: the erosion limit of the frustum (= the classic inradius when unsliced).
			SdfShape.Cone => MathF.Max( FrustumMaxRounding( Size.x, Size.z, Slice ), 0.75f ),
			// Text: rounding insets glyph strokes, which are far thinner than the quad — keep the cap modest
			// so full-slider is "chunky rounded letters", not "letters eroded to nothing".
			SdfShape.Text => MathF.Max( MathF.Min( Size.z, MathF.Min( Size.x, Size.y ) * 0.25f ), 0.75f ),
			SdfShape.Extruded => MathF.Min( CrossSection switch
			{
				SdfCrossSection.Star => wx * StarEdgeFactor,       // erosion limit — the star shrinks to a point
				SdfCrossSection.Hexagon => wx * 0.86602540f,       // the apothem — hexagon shrinks to a point
				SdfCrossSection.Pentagon => wx * 0.80901699f,      // the apothem — pentagon shrinks to a point
				_ => TriInradius( new Vector2( 0f, hy ), new Vector2( -wx, -hy ), new Vector2( wx, -hy ) ),
			}, Size.z ),
			// Sphere/ellipsoid: rounding only shows on the sliced rim (fillet); unsliced it's a no-op, so
			// keep the old sane range. Sliced, the fillet is capped by SphereSliceMaxRounding (floored at
			// the slider min so a deep slice can't invert the slider range).
			_ => Slice > 0f
				? MathF.Max( SphereSliceMaxRounding( Size, Slice ), 0.75f )
				: MathF.Min( Size.x, MathF.Min( Size.y, Size.z ) ),
		};
	}

	// The most rounding a (possibly sliced) cone fits: caps can't cross (half the sliced height) and the
	// eroded base radius can't go negative. Unsliced this reduces EXACTLY to the classic cone inradius
	// 2HR/(slant+R). Shared by MaxRounding (the slider max) and ConeDistance's internal clamp.
	static float FrustumMaxRounding( float R, float H, float slice )
	{
		float s = Math.Clamp( slice, 0f, MaxSlice );
		float zcut = H * (1f - 2f * s);
		float Rt = R * s;                 // frustum top radius — the sharp cone's radius at the cut plane
		float hh = (zcut + H) * 0.5f;     // frustum half-height
		float L = MathF.Sqrt( (R - Rt) * (R - Rt) + 4f * hh * hh ); // slant length
		if ( hh < 1e-4f )
			return R;
		return MathF.Min( hh, 2f * hh * R / MathF.Max( L + R - Rt, 1e-4f ) );
	}

	// Ellipsoid with per-axis radii = Size (IQ approximation — exact distance has no closed form).
	// Uniform Size gives a sphere; non-uniform Size stretches it.
	static float EllipsoidDistance( Vector3 p, Vector3 r )
	{
		float rx = MathF.Max( r.x, 1e-3f ), ry = MathF.Max( r.y, 1e-3f ), rz = MathF.Max( r.z, 1e-3f );

		float ax = p.x / rx, ay = p.y / ry, az = p.z / rz;
		float k0 = MathF.Sqrt( ax * ax + ay * ay + az * az );

		float bx = p.x / (rx * rx), by = p.y / (ry * ry), bz = p.z / (rz * rz);
		float k1 = MathF.Sqrt( bx * bx + by * by + bz * bz );

		return k1 > 1e-6f ? k0 * (k0 - 1f) / k1 : -MathF.Min( rx, MathF.Min( ry, rz ) );
	}

	static float BoxDistance( Vector3 p, Vector3 b, float r )
	{
		r = MathF.Max( 0f, MathF.Min( r, MathF.Min( b.x, MathF.Min( b.y, b.z ) ) ) );

		float qx = MathF.Abs( p.x ) - b.x + r;
		float qy = MathF.Abs( p.y ) - b.y + r;
		float qz = MathF.Abs( p.z ) - b.z + r;

		float ox = MathF.Max( qx, 0f ), oy = MathF.Max( qy, 0f ), oz = MathF.Max( qz, 0f );
		float outside = MathF.Sqrt( ox * ox + oy * oy + oz * oz );
		float inside = MathF.Min( MathF.Max( qx, MathF.Max( qy, qz ) ), 0f );
		return outside + inside - r;
	}

	// Capped cylinder along Z. radius = Size.x, half-height = Size.z.
	static float CylinderDistance( Vector3 p, Vector3 size, float r )
	{
		float rad = size.x, h = size.z;
		r = MathF.Max( 0f, MathF.Min( r, MathF.Min( rad, h ) ) );

		float dx = MathF.Sqrt( p.x * p.x + p.y * p.y ) - (rad - r);
		float dz = MathF.Abs( p.z ) - (h - r);
		float ox = MathF.Max( dx, 0f ), oz = MathF.Max( dz, 0f );
		return MathF.Min( MathF.Max( dx, dz ), 0f ) + MathF.Sqrt( ox * ox + oz * oz ) - r;
	}

	// IQ capped cone, centred on Z. qx = radial, qy = axial; bottom radius r1 at qy=-h, top r2 at qy=+h.
	static float CappedCone( float qx, float qy, float h, float r1, float r2 )
	{
		float k2x = r2 - r1, k2y = 2f * h;
		float cax = qx - MathF.Min( qx, (qy < 0f) ? r1 : r2 );
		float cay = MathF.Abs( qy ) - h;
		float t = Math.Clamp( ((r2 - qx) * k2x + (h - qy) * k2y) / (k2x * k2x + k2y * k2y), 0f, 1f );
		float cbx = qx - r2 + k2x * t;
		float cby = qy - h + k2y * t;
		float s = (cbx < 0f && cay < 0f) ? -1f : 1f;
		return s * MathF.Sqrt( MathF.Min( cax * cax + cay * cay, cbx * cbx + cby * cby ) );
	}

	// Cone (base radius Size.x at z=-Size.z, apex at z=+Size.z), optionally SLICED flat at the plane the
	// slice fraction picks — the slice is part of the primitive (a frustum), so ONE Minkowski opening rounds
	// every edge together: erode all faces (slant, base, cut) inward by r, take the exact capped-cone
	// distance of the eroded trapezoid, dilate by r. The cut rim therefore rounds exactly like the base rim,
	// the flat survives any legal r, and the shape stays INSIDE the sharp silhouette. Unsliced (Rt = 0) this
	// reproduces the old pointed-cone formula exactly (the eroded top collapses to the shortened apex).
	static float ConeDistance( Vector3 p, Vector3 size, float rounding, float slice )
	{
		float R = size.x, H = size.z;
		float qx = MathF.Sqrt( p.x * p.x + p.y * p.y );

		float s = Math.Clamp( slice, 0f, MaxSlice );
		float zcut = H * (1f - 2f * s); // top cap height (= +H when unsliced)
		float Rt = R * s;               // top cap radius (0 = pointed)
		float hh = (zcut + H) * 0.5f;   // frustum half-height
		float zc0 = (zcut - H) * 0.5f;  // frustum centre

		if ( hh < 1e-4f || R < 1e-4f )
			return MathF.Sqrt( qx * qx + (p.z - zc0) * (p.z - zc0) ) - MathF.Max( R, MathF.Max( Rt, 1e-3f ) );

		float L = MathF.Sqrt( (R - Rt) * (R - Rt) + 4f * hh * hh ); // slant length
		float k = (Rt - R) / (2f * hh);                             // slant slope dq/dz (< 0)
		float r = Math.Clamp( rounding, 0f, FrustumMaxRounding( R, H, slice ) );

		if ( r < 1e-4f )
			return CappedCone( qx, p.z - zc0, hh, R, Rt );

		// Erode: caps move in by r; the slant shifts horizontally by r/cos(tilt) = r·L/(2hh).
		float dq = r * L / (2f * hh);
		float zb = -H + r, zt = zcut - r;
		float r1 = R + r * k - dq;  // eroded radius at the bottom cap
		float r2 = Rt - r * k - dq; // eroded radius at the top cap

		if ( r2 < 0f )
		{
			zt = (dq - R) / k - H; // slant meets the axis below the top cap — eroded shape is pointed
			r2 = 0f;               // (this is the old zApex = H − r/sinB when unsliced)
		}

		if ( zt <= zb ) // eroded away entirely — the fully-rounded limit is a ball
			return MathF.Sqrt( qx * qx + (p.z - (zb + zt) * 0.5f) * (p.z - (zb + zt) * 0.5f) ) - r;

		return CappedCone( qx, p.z - (zb + zt) * 0.5f, (zt - zb) * 0.5f, r1, r2 ) - r;
	}

	// Exact signed distance to a 2D triangle given its three vertices (IQ). Works for ANY triangle,
	// so the prism's cross-section can be any shape (we feed it an isosceles one). Componentwise min
	// pairs nearest-squared-distance (.x) with an inside/outside sign from edge winding (.y).
	static float Triangle2D( float px, float py, Vector2 p0, Vector2 p1, Vector2 p2 )
	{
		Vector2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
		Vector2 w0 = new Vector2( px, py ) - p0, w1 = new Vector2( px, py ) - p1, w2 = new Vector2( px, py ) - p2;
		Vector2 pq0 = w0 - e0 * Math.Clamp( Vector2.Dot( w0, e0 ) / Vector2.Dot( e0, e0 ), 0f, 1f );
		Vector2 pq1 = w1 - e1 * Math.Clamp( Vector2.Dot( w1, e1 ) / Vector2.Dot( e1, e1 ), 0f, 1f );
		Vector2 pq2 = w2 - e2 * Math.Clamp( Vector2.Dot( w2, e2 ) / Vector2.Dot( e2, e2 ), 0f, 1f );
		float s = MathF.Sign( e0.x * e2.y - e0.y * e2.x );

		float dx = MathF.Min( Vector2.Dot( pq0, pq0 ), MathF.Min( Vector2.Dot( pq1, pq1 ), Vector2.Dot( pq2, pq2 ) ) );
		float dy = MathF.Min( s * (w0.x * e0.y - w0.y * e0.x ),
			MathF.Min( s * (w1.x * e1.y - w1.y * e1.x), s * (w2.x * e2.y - w2.y * e2.x) ) );
		return -MathF.Sqrt( dx ) * MathF.Sign( dy );
	}

	// Incircle radius — the most a triangle can be inset before it collapses (caps rounding).
	static float TriInradius( Vector2 a, Vector2 b, Vector2 c )
	{
		float per = (a - b).Length + (b - c).Length + (c - a).Length;
		float area = MathF.Abs( (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y) ) * 0.5f;
		return per > 1e-5f ? 2f * area / per : 0f;
	}

	// Move vertex vi inward (along its angle bisector) so each adjacent edge shifts in by r — i.e. the
	// polygon-inset position. Used to erode the triangle before the rounding dilation, so rounding stays
	// INSIDE the silhouette. n1/n2 are the two neighbouring vertices (order doesn't matter).
	static Vector2 InsetVertex( Vector2 vi, Vector2 n1, Vector2 n2, float r )
	{
		Vector2 a = (n1 - vi).Normal, b = (n2 - vi).Normal;
		Vector2 bis = a + b;
		float bl = bis.Length;
		if ( bl < 1e-5f ) return vi; // straight angle — can't happen for a real triangle
		bis /= bl;
		float sinHalf = MathF.Sqrt( MathF.Max( (1f - Vector2.Dot( a, b )) * 0.5f, 1e-6f ) );
		return vi + bis * (r / sinHalf);
	}

	// ── Extruded cross-sections ──────────────────────────────────────────────────────────────────────
	// The star's fixed proportions (5 tips, IQ sdStar with m = 3). All of a regular star's edges sit at the
	// same distance from the centre, so a uniform scale IS an exact all-edges inset — which makes the
	// erode-then-dilate rounding trick a pure radius change (see StarPrismDistance).
	internal const float StarEdgeFactor = 0.40673664f;  // sin(60° − 36°): edge-to-centre distance / circumradius
	internal const float StarInnerFactor = 0.46966082f; // sin(24°) / sin(60°): notch radius / circumradius
	const float HexApothemFactor = 0.86602540f;         // cos(30°): apothem / circumradius
	const float PentApothemFactor = 0.80901699f;        // cos(36°): apothem / circumradius

	// The extruded shape: a 2D profile (picked by CrossSection) swept along Z to half-depth Size.z. Triangle
	// keeps the old prism's isosceles Size.x/Size.y; star/hexagon are regular with circumradius Size.x
	// (Size.y unused, like the cylinder). Rounding for all three is the same Minkowski opening as box/cone:
	// erode the profile geometrically by r, extrude to (Size.z − r), then dilate by r — so the rounded shape
	// stays INSIDE the sharp silhouette.
	static float ExtrudedDistance( Vector3 p, Vector3 size, float rounding, SdfCrossSection xs )
	{
		switch ( xs )
		{
			case SdfCrossSection.Star:
			{
				float R = MathF.Max( size.x, 1e-3f ), hz = size.z;
				float r = Math.Clamp( rounding, 0f, MathF.Min( R * StarEdgeFactor, hz ) );
				// Uniform scale = exact edge inset (all edges equidistant from the centre); r = R·StarEdgeFactor
				// shrinks the star to a point, which then dilates to a sphere — same limit behaviour as the cone.
				float d2 = Star2D( p.x, p.y, R - r / StarEdgeFactor );
				return ExtrudeRound( d2, p.z, hz, r );
			}
			case SdfCrossSection.Hexagon:
			{
				float a = MathF.Max( size.x, 1e-3f ) * HexApothemFactor, hz = size.z; // apothem from circumradius
				float r = Math.Clamp( rounding, 0f, MathF.Min( a, hz ) );
				float d2 = Hexagon2D( p.x, p.y, a - r ); // regular-polygon inset = smaller apothem
				return ExtrudeRound( d2, p.z, hz, r );
			}
			case SdfCrossSection.Pentagon:
			{
				float a = MathF.Max( size.x, 1e-3f ) * PentApothemFactor, hz = size.z;
				float r = Math.Clamp( rounding, 0f, MathF.Min( a, hz ) );
				float d2 = Pentagon2D( p.x, p.y, a - r );
				return ExtrudeRound( d2, p.z, hz, r );
			}
			default:
				return TriPrismDistance( p, size, rounding );
		}
	}

	// Extruded TEXT: the 2D profile is the brush's baked distance texture (texel units), sampled at the
	// local XY and pushed through the same rounded extrusion as the analytic profiles. Outside the text quad
	// the clamped sample is repaired with the quad distance (both are lower bounds on the true glyph
	// distance; max keeps marching safe). Round insets the glyphs — a real distance field, so letter edges
	// and corners round like clay. Grid not baked yet (first frame / headless) → box placeholder.
	static float TextDistance( Vector3 p, Vector3 size, float rounding, SdfTextData data )
	{
		if ( data?.Grid is null )
			return BoxDistance( p, size, rounding );

		float hx = MathF.Max( size.x, 1e-3f ), hy = MathF.Max( size.y, 1e-3f ), hz = MathF.Max( size.z, 1e-3f );
		float u = p.x / (2f * hx) + 0.5f;
		float v = 1f - (p.y / (2f * hy) + 0.5f); // bitmap rows run top-down
		float ts = MathF.Min( 2f * hx / SdfTextData.Width, 2f * hy / SdfTextData.Height ); // world per texel (conservative)
		float d2 = data.Sample( u, v ) * ts;

		// Outside the quad there's no field data (the sample is edge-clamped). Nearest glyph is inward of
		// the clamp point while p is outward of it, so those legs can't oppose: d_true² ≥ dq² + sample² —
		// a tight bound that stays SMOOTH across the rim (a max() bound kinks the field along the quad
		// rectangle, creasing blends near the rim). Mirrors the shaders' sdText.
		float qx = MathF.Abs( p.x ) - hx, qy = MathF.Abs( p.y ) - hy;
		float ox = MathF.Max( qx, 0f ), oy = MathF.Max( qy, 0f );
		float dq = MathF.Sqrt( ox * ox + oy * oy );
		if ( dq > 0f )
		{
			float d2o = MathF.Max( d2, 0f );
			d2 = MathF.Sqrt( dq * dq + d2o * d2o );
		}

		float r = Math.Clamp( rounding, 0f, hz );
		return ExtrudeRound( d2 + r, p.z, hz, r );
	}

	// Combine an (already eroded-by-r) 2D profile distance with the Z extrusion, then dilate by r — the
	// shared tail of every rounded extrusion (identical to the box/cylinder combine).
	static float ExtrudeRound( float d2, float pz, float hz, float r )
	{
		float dz = MathF.Abs( pz ) - (hz - r);
		float ox = MathF.Max( d2, 0f ), oz = MathF.Max( dz, 0f );
		return MathF.Min( MathF.Max( d2, dz ), 0f ) + MathF.Sqrt( ox * ox + oz * oz ) - r;
	}

	// IQ exact regular hexagon, a = apothem (centre to flat edge). Vertices on the ±X axis (+ four more at
	// ±60°), flat edges facing ±Y — must stay in sync with CrossSectionOutline and the HLSL copy.
	static float Hexagon2D( float px, float py, float a )
	{
		const float kx = -0.86602540f, ky = 0.5f, kz = 0.57735027f;
		px = MathF.Abs( px ); py = MathF.Abs( py );
		float dk = MathF.Min( kx * px + ky * py, 0f );
		px -= 2f * dk * kx;
		py -= 2f * dk * ky;
		px -= Math.Clamp( px, -kz * a, kz * a );
		py -= a;
		return MathF.Sqrt( px * px + py * py ) * MathF.Sign( py );
	}

	// IQ exact regular pentagon, a = apothem, one vertex pointing +Y (IQ's original is flat-top/vertex-down,
	// hence the Y negate) — must stay in sync with CrossSectionOutline and the HLSL copy.
	static float Pentagon2D( float px, float py, float a )
	{
		const float kx = 0.80901699f, ky = 0.58778525f, kz = 0.72654253f; // cos36, sin36, tan36
		py = -py; // vertex-up convention
		px = MathF.Abs( px );
		float d1 = MathF.Min( -kx * px + ky * py, 0f );
		px -= 2f * d1 * -kx;
		py -= 2f * d1 * ky;
		float d2 = MathF.Min( kx * px + ky * py, 0f );
		px -= 2f * d2 * kx;
		py -= 2f * d2 * ky;
		px -= Math.Clamp( px, -kz * a, kz * a );
		py -= a;
		return MathF.Sqrt( px * px + py * py ) * MathF.Sign( py );
	}

	// IQ exact 5-point star (sdStar, n = 5, m = 3), R = tip circumradius, one tip along +Y — must stay in
	// sync with CrossSectionOutline and the HLSL copy.
	static float Star2D( float px, float py, float R )
	{
		const float an = MathF.PI / 5f;
		const float acsX = 0.80901699f, acsY = 0.58778525f; // (cos, sin) π/5
		const float ecsX = 0.5f, ecsY = 0.86602540f;        // (cos, sin) π/3 — the edge direction

		float bn = MathF.Atan2( px, py );
		bn -= 2f * an * MathF.Floor( bn / (2f * an) ); // positive mod (fmod would mirror negatives)
		bn -= an;

		float len = MathF.Sqrt( px * px + py * py );
		float qx = len * MathF.Cos( bn ) - R * acsX;
		float qy = len * MathF.Abs( MathF.Sin( bn ) ) - R * acsY;

		float t = Math.Clamp( -(qx * ecsX + qy * ecsY), 0f, R * acsY / ecsY );
		qx += ecsX * t;
		qy += ecsY * t;
		return MathF.Sqrt( qx * qx + qy * qy ) * MathF.Sign( qx );
	}

	/// <summary>The sharp 2D outline (XY, CCW) of an extruded cross-section at the given size — the single
	/// polygon definition the ghost / wireframe / shadow / collision proxies mesh, so they all match the
	/// analytic SDF's orientation. Star vertices alternate tip (even index) / notch (odd index).</summary>
	public static void CrossSectionOutline( SdfCrossSection xs, Vector3 size, List<Vector2> dst )
	{
		dst.Clear();
		float wx = MathF.Max( size.x, 1e-3f ), hy = MathF.Max( size.y, 1e-3f );
		switch ( xs )
		{
			case SdfCrossSection.Star: // tips at 90° + k·72° (one straight up, matching Star2D), notches midway
				for ( int k = 0; k < 10; k++ )
				{
					float r = (k & 1) == 0 ? wx : wx * StarInnerFactor;
					float ang = MathF.PI * 0.5f + k * (MathF.PI / 5f);
					dst.Add( new Vector2( MathF.Cos( ang ) * r, MathF.Sin( ang ) * r ) );
				}
				break;
			case SdfCrossSection.Hexagon: // vertices at k·60°, first on +X (matching Hexagon2D)
				for ( int k = 0; k < 6; k++ )
				{
					float ang = k * (MathF.PI / 3f);
					dst.Add( new Vector2( MathF.Cos( ang ) * wx, MathF.Sin( ang ) * wx ) );
				}
				break;
			case SdfCrossSection.Pentagon: // vertices at 90° + k·72°, one straight up (matching Pentagon2D)
				for ( int k = 0; k < 5; k++ )
				{
					float ang = MathF.PI * 0.5f + k * (MathF.PI * 2f / 5f);
					dst.Add( new Vector2( MathF.Cos( ang ) * wx, MathF.Sin( ang ) * wx ) );
				}
				break;
			default: // isosceles triangle: apex (0, +hy), base corners (±wx, −hy)
				dst.Add( new Vector2( 0f, hy ) );
				dst.Add( new Vector2( -wx, -hy ) );
				dst.Add( new Vector2( wx, -hy ) );
				break;
		}
	}

	// Triangular prism: an isosceles triangle in XY — apex at (0, +Size.y), base corners at
	// (±Size.x, -Size.y) — extruded along Z to half-depth Size.z. Independent Size.x/Size.y give any
	// isosceles cross-section (Size.y ≈ 0.866·Size.x is equilateral). Rounding insets the triangle by r
	// then dilates by r, so it stays INSIDE the silhouette (same Minkowski-opening trick as box/cone).
	static float TriPrismDistance( Vector3 p, Vector3 size, float rounding )
	{
		float wx = MathF.Max( size.x, 1e-3f ), hy = MathF.Max( size.y, 1e-3f ), hz = size.z;
		Vector2 v0 = new( 0f, hy ), v1 = new( -wx, -hy ), v2 = new( wx, -hy );

		float r = Math.Clamp( rounding, 0f, MathF.Min( TriInradius( v0, v1, v2 ), hz ) );
		if ( r > 1e-4f )
		{
			Vector2 i0 = InsetVertex( v0, v1, v2, r ), i1 = InsetVertex( v1, v0, v2, r ), i2 = InsetVertex( v2, v0, v1, r );
			v0 = i0; v1 = i1; v2 = i2;
		}

		float d2 = Triangle2D( p.x, p.y, v0, v1, v2 );
		float dz = MathF.Abs( p.z ) - (hz - r);
		float ox = MathF.Max( d2, 0f ), oz = MathF.Max( dz, 0f );
		return MathF.Min( MathF.Max( d2, dz ), 0f ) + MathF.Sqrt( ox * ox + oz * oz ) - r;
	}

	/// <summary>Conservative bounding-sphere radius.</summary>
	public float BoundingRadius
	{
		get
		{
			float r = Shape switch
			{
				SdfShape.Box or SdfShape.Text => Size.Length, // text spans its quad like a box
				// Extruded: triangle spans Size.x/Size.y; star/hexagon are radius-Size.x discs (like the cylinder)
				SdfShape.Extruded => CrossSection == SdfCrossSection.Triangle
					? Size.Length
					: MathF.Sqrt( Size.x * Size.x + Size.z * Size.z ),
				SdfShape.Cylinder => MathF.Sqrt( Size.x * Size.x + Size.z * Size.z ),
				// Base-pivot cone: measured from Position (the base), the apex is a FULL height (2·Size.z) away.
				SdfShape.Cone => MathF.Sqrt( Size.x * Size.x + 4f * Size.z * Size.z ),
				_ => MathF.Max( Size.x, MathF.Max( Size.y, Size.z ) ), // ellipsoid: largest radius

			};
			return r + Blend * BlendBoundsFactor; // smooth-union bulge ≈ k/4 for one blend (see AabbExtents)
		}
	}

	/// <summary>Tight AABB half-extents for this brush given its rotation (pass <c>Rotation</c> for
	/// local space, or <c>parentRotation * Rotation</c> for world space). Unlike
	/// <see cref="BoundingRadius"/> — a sphere radius that inflates a flat/elongated shape into a
	/// diagonal-sized cube — this uses the shape's real per-axis extents, so a thin wall gets a thin
	/// box instead of a giant cube.</summary>
	public Vector3 AabbExtents( Rotation rotation, bool includeBlend = true )
	{
		// Local half-extents per shape.
		Vector3 e = Shape switch
		{
			SdfShape.Cylinder or SdfShape.Cone => new Vector3( Size.x, Size.x, Size.z ),
			// Star/hexagon profiles fit a radius-Size.x disc; the triangle keeps its Size.x/Size.y extents.
			SdfShape.Extruded when CrossSection != SdfCrossSection.Triangle => new Vector3( Size.x, Size.x, Size.z ),
			_ => Size, // Box/triangle half-extents; sphere/ellipsoid per-axis radii
		};

		// Standard oriented-box -> AABB: sum the absolute contributions of the rotated local axes.
		var ax = rotation * new Vector3( e.x, 0f, 0f );
		var ay = rotation * new Vector3( 0f, e.y, 0f );
		var az = rotation * new Vector3( 0f, 0f, e.z );

		var ext = new Vector3(
			MathF.Abs( ax.x ) + MathF.Abs( ay.x ) + MathF.Abs( az.x ),
			MathF.Abs( ax.y ) + MathF.Abs( ay.y ) + MathF.Abs( az.y ),
			MathF.Abs( ax.z ) + MathF.Abs( ay.z ) + MathF.Abs( az.z ) );

		// A single smooth-union bulges the surface out by k/4 (its -k·h·(1-h) term peaks at h=0.5). The
		// full Blend over-estimated this ~4x for the common case, so inflate by Blend/4. Caveat: many
		// additive brushes coinciding at one point stack their blends and can push past k/4 (toward k in
		// the limit) — raise this factor if a heavily-overlapped seam ever clips. The mesher/field bounds
		// need this padding; a VISUAL bounds (UI anchor) wants the bare primitive, so it passes false.
		return includeBlend ? ext + Blend * BlendBoundsFactor : ext;
	}

	// Bounds inflation as a fraction of Blend. k/4 = exact for one blend; see AabbExtents for the caveat.
	const float BlendBoundsFactor = 0.25f;

	/// <summary>The brush's own AABB in SCULPTURE-LOCAL space, ignoring symmetry (the bounds caller reflects it
	/// per mirror axis). A spline spans its control points (± each radius); every other shape is
	/// <see cref="Position"/> ± <see cref="AabbExtents"/>. <paramref name="includeBlend"/> adds the smooth-union
	/// bulge padding (true for the mesher/field grid; false for a visual bounds — the bare primitive extent).</summary>
	public void LocalBounds( out Vector3 mn, out Vector3 mx, bool includeBlend = true )
	{
		if ( Shape == SdfShape.Spline )
		{
			var pts = Points;
			if ( pts is { Count: > 0 } )
			{
				// Span the DRAWN curve (tessellated when curved), not just the control points — Catmull-Rom
				// bulges past them, so control-point bounds alone would clip the curve.
				var lo = new Vector3( float.MaxValue );
				var hi = new Vector3( float.MinValue );
				float blend = includeBlend ? Blend * BlendBoundsFactor : 0f;
				int n = pts.Count;
				int k = (n == 1 || Curvature <= 1e-4f) ? 1 : SplineTessellation;
				bool closed = IsSplineLoop;
				int segs = SplineSpanCount( n, closed );

				var c0 = new Vector3( pts[0].x, pts[0].y, pts[0].z );
				float r0 = pts[0].w + blend;
				lo = Vector3.Min( lo, c0 - r0 );
				hi = Vector3.Max( hi, c0 + r0 );

				for ( int i = 0; i < segs; i++ )
				{
					SplineSpan( i, n, closed, out int i0, out int i1, out int i2, out int i3 );
					var p0 = pts[i0];
					var p1 = pts[i1];
					var p2 = pts[i2];
					var p3 = pts[i3];
					for ( int s = 1; s <= k; s++ )
					{
						var cur = CatmullRomPoint( p0, p1, p2, p3, s / (float)k, Curvature );
						var c = new Vector3( cur.x, cur.y, cur.z );
						float r = cur.w + blend;
						lo = Vector3.Min( lo, c - r );
						hi = Vector3.Max( hi, c + r );
					}
				}

				mn = lo;
				mx = hi;
				return;
			}
			mn = mx = Position; // empty spline — degenerate
			return;
		}

		var centre = Position + Rotation * LocalCentre; // base-pivot cone: the AABB wraps the shape, not Position
		var ext = AabbExtents( Rotation, includeBlend );
		mn = centre - ext;
		mx = centre + ext;
	}
}

/// <summary>The blended surface attributes at a point — what the shaders need to shade a pixel.</summary>
public struct SdfSurface
{
	public Color Color;
	public float Metallic;
	public float Roughness;
}

/// <summary>
/// Pure-math evaluation of a brush list. No engine/render dependencies — this is the
/// part we'll reuse identically in the editor tool and the in-game player UI.
/// </summary>
public static class Sdf
{
	/// <summary>
	/// Pick the brush under a cursor ray (origin/dir in SCULPTURE-LOCAL space). Returns the brush index or
	/// -1. It hits the combined visible surface (the additive result), but ALSO lets a SUBTRACTIVE brush be
	/// picked by its own primitive volume — so a negative volume is selectable even with nothing behind it
	/// (carved empty space has no surface to trace). The nearest of {surface owner, subtractive volume} wins.
	/// </summary>
	public static int PickBrush( List<SdfBrush> brushes, Vector3 o, Vector3 d )
	{
		if ( brushes is null || brushes.Count == 0 )
			return -1;

		// 1) Combined surface hit → the brush whose primitive owns that surface point.
		float tSurface = float.MaxValue;
		int surfaceBrush = -1;
		if ( RayMarch( p => Sample( brushes, p ), o, d, out float ts ) )
		{
			tSurface = ts;
			var hp = o + d * ts;
			float bestDist = float.MaxValue;
			for ( int i = 0; i < brushes.Count; i++ )
			{
				if ( !brushes[i].Enabled )
					continue;
				float bd = MathF.Abs( brushes[i].Distance( hp ) );
				if ( bd < bestDist ) { bestDist = bd; surfaceBrush = i; }
			}
		}

		// 2) Nearest SUBTRACTIVE brush whose own primitive volume the ray enters (additive brushes already
		//    show up as surface above; only carvers need this fallback).
		float tSub = float.MaxValue;
		int subBrush = -1;
		for ( int i = 0; i < brushes.Count; i++ )
		{
			var b = brushes[i];
			if ( !b.Enabled || b.Operation != SdfOperation.Subtract )
				continue;

			if ( RayMarch( b.Distance, o, d, out float te ) && te < tSub )
			{
				tSub = te;
				subBrush = i;
			}
		}

		// A subtractive volume in front of the visible surface (or when there's no surface) wins.
		return (subBrush >= 0 && tSub < tSurface) ? subBrush : surfaceBrush;
	}

	// Sphere-trace a signed-distance function along a ray; true + entry distance if it enters the surface.
	static bool RayMarch( System.Func<Vector3, float> sdf, Vector3 o, Vector3 d, out float t )
	{
		t = 0f;
		for ( int s = 0; s < 128; s++ )
		{
			float dist = sdf( o + d * t );
			if ( dist < 0.1f )
				return true;
			t += MathF.Max( dist, 0.05f );
			if ( t > 100000f )
				break;
		}
		return false;
	}

	/// <summary>Combined signed distance of the whole brush list at <paramref name="p"/>.</summary>
	public static float Sample( List<SdfBrush> brushes, Vector3 p )
	{
		float d = 1e9f; // empty space
		foreach ( var b in brushes )
		{
			if ( !b.Enabled )
				continue; // hidden brush — skip (eye toggle)

			float bd = b.Distance( p );
			d = b.Operation == SdfOperation.Add
				? SmoothUnion( d, bd, b.Blend )
				: SmoothSubtract( d, bd, b.Blend );
		}
		return d;
	}

	/// <summary>Colour blended across smooth-union seams by the same factor as the geometry.</summary>
	public static Color SampleColor( List<SdfBrush> brushes, Vector3 p ) => SampleSurface( brushes, p ).Color;

	/// <summary>The full per-point surface — colour, metalness and roughness — each blended across
	/// smooth-union seams by the SAME smooth-min factor as the geometry, so material attributes share
	/// the seam instead of popping at a hard nearest-brush boundary (a sharp specular discontinuity
	/// reads far worse than a colour edge).</summary>
	public static SdfSurface SampleSurface( List<SdfBrush> brushes, Vector3 p )
	{
		float d = 1e9f;
		Color col = Color.White;
		float metal = 0f, rough = 1f; // empty-space material; the first brush overrides it
		foreach ( var b in brushes )
		{
			if ( !b.Enabled )
				continue; // hidden brush — skip (eye toggle)

			float bd = b.Distance( p );
			float k = b.Blend;

			if ( b.Operation == SdfOperation.Subtract )
			{
				// Subtraction tints the cut walls with the brush material.
				if ( k <= 0f )
				{
					if ( -bd > d ) { col = b.Color; metal = b.Metallic; rough = b.Roughness; }
					d = MathF.Max( d, -bd );
				}
				else
				{
					float hs = Math.Clamp( 0.5f - 0.5f * (d + bd) / k, 0f, 1f );
					col = Color.Lerp( col, b.Color, hs ); // hs->1 takes the cut material
					metal = MathX.Lerp( metal, b.Metallic, hs );
					rough = MathX.Lerp( rough, b.Roughness, hs );
					d = (d * (1f - hs) + (-bd) * hs) + k * hs * (1f - hs);
				}
				continue;
			}

			if ( k <= 0f )
			{
				if ( bd < d ) { d = bd; col = b.Color; metal = b.Metallic; rough = b.Roughness; }
			}
			else
			{
				float h = Math.Clamp( 0.5f + 0.5f * (bd - d) / k, 0f, 1f );
				col = Color.Lerp( b.Color, col, h ); // h->1 keeps accumulated, h->0 takes new
				metal = MathX.Lerp( b.Metallic, metal, h );
				rough = MathX.Lerp( b.Roughness, rough, h );
				d = (bd * (1f - h) + d * h) - k * h * (1f - h);
			}
		}
		return new SdfSurface { Color = col, Metallic = metal, Roughness = rough };
	}

	/// <summary>Surface normal via central-difference gradient of the field.</summary>
	public static Vector3 Gradient( List<SdfBrush> brushes, Vector3 p, float e )
	{
		float dx = Sample( brushes, p + new Vector3( e, 0, 0 ) ) - Sample( brushes, p - new Vector3( e, 0, 0 ) );
		float dy = Sample( brushes, p + new Vector3( 0, e, 0 ) ) - Sample( brushes, p - new Vector3( 0, e, 0 ) );
		float dz = Sample( brushes, p + new Vector3( 0, 0, e ) ) - Sample( brushes, p - new Vector3( 0, 0, e ) );
		var g = new Vector3( dx, dy, dz );
		return g.LengthSquared > 0.0001f ? g.Normal : Vector3.Up;
	}

	/// <summary>World-space box that encloses every additive brush (plus blend).</summary>
	public static bool TryGetBounds( List<SdfBrush> brushes, out BBox bounds )
	{
		bounds = default;
		bool any = false;
		Vector3 mn = default, mx = default;

		foreach ( var b in brushes )
		{
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue;

			b.LocalBounds( out var lo0, out var hi0 );

			// Union the local AABB and a reflected copy across each enabled mirror plane. Reflecting an
			// axis-aligned box across a coordinate plane just swaps that axis's min/max (and negates them).
			int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= nx; sx++ )
				for ( int sy = 0; sy <= ny; sy++ )
					for ( int sz = 0; sz <= nz; sz++ )
					{
						var lo = lo0;
						var hi = hi0;
						if ( sx == 1 ) (lo.x, hi.x) = (-hi.x, -lo.x);
						if ( sy == 1 ) (lo.y, hi.y) = (-hi.y, -lo.y);
						if ( sz == 1 ) (lo.z, hi.z) = (-hi.z, -lo.z);

						if ( !any )
						{
							mn = lo;
							mx = hi;
						}
						else
						{
							mn = Vector3.Min( mn, lo );
							mx = Vector3.Max( mx, hi );
						}
						any = true;
					}
		}

		if ( any )
			bounds = new BBox( mn, mx );

		return any;
	}

	// Inigo Quilez smooth-union / smooth-subtraction. Internal so SdfBrush can fuse mirror-symmetry copies
	// with the same blend it uses against the rest of the field.
	internal static float SmoothUnion( float a, float b, float k )
	{
		if ( k <= 0 ) return MathF.Min( a, b );
		float h = Math.Clamp( 0.5f + 0.5f * (b - a) / k, 0f, 1f );
		return (b * (1f - h) + a * h) - k * h * (1f - h);
	}

	static float SmoothSubtract( float d, float sub, float k )
	{
		if ( k <= 0 ) return MathF.Max( d, -sub );
		float h = Math.Clamp( 0.5f - 0.5f * (d + sub) / k, 0f, 1f );
		return (d * (1f - h) + (-sub) * h) + k * h * (1f - h);
	}
}
