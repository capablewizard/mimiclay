using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Builds a CHEAP collision <see cref="Model"/> from a brush list — convex shapes only, because the disguise
/// rides a DYNAMIC <see cref="Rigidbody"/> and concave mesh collision is static/keyframed-only.
///
/// Detail brushes are gated out first: an additive brush under <see cref="MinBrushExtent"/> on EVERY axis of
/// its local bounding box (an eye, a button, a stud) gets no collider at all — it can only add shapes for the
/// body to snag on. All three axes must be small, so a thin plate stays solid. If EVERYTHING is that small the
/// gate turns off wholesale, or a marble-sized prop would have no collider and fall through the world.
///
/// Then two paths per surviving additive brush (subtractive brushes are never solid themselves):
///
///  • Untouched by subtracts → one convex shape per primitive: a sphere (round), swept spheres (spline) or a
///    small convex hull (box/cylinder/cone/prism/ellipsoid), mirror-symmetry copies included.
///  • Significantly hollowed by LATER subtracts → a coarse brush-aligned voxelisation of the CARVED field,
///    greedy-merged into boxes, so a carved-out bin is really open inside (things can fall in). Judged per
///    mirror copy — a copy the subtracts barely graze (&lt; <see cref="CarveGateFraction"/>) keeps its exact
///    convex shape. List order matters: <c>SdfBrush.Sample</c> folds in order, so a subtract only
///    carves brushes BEFORE it, and an add placed after a subtract re-fills the hole with its own collider.
///
/// Splines collide as as few CAPSULES as reproduce the tube to within <see cref="SplineCapsuleTolerance"/>
/// (see <see cref="FitSplineCapsules"/>): a straight run is one capsule however long, and only the bends spend
/// more. They carve PER CAPSULE SAMPLE — spheres along each capsule's axis: a sample a subtract fully swallows
/// is dropped, unbitten runs re-merge into capsules, and one it genuinely bites is voxelised on its own small
/// sculpture-aligned grid. Only the stretches that need it pay; a long spline never voxelises as a whole. TEXT is the one exemption: glyph strokes are thinner than a voxel, so a carved voxelisation would
/// dissolve the letters — a text brush keeps its ink-box hull, and the foot probes skip its carvers the same way.
///
/// Cheap enough to rebuild synchronously on the main thread whenever the shape is committed (e.g. on gizmo
/// release) rather than every frame. Pair with a <see cref="ModelCollider"/> that references the result.
/// </summary>
public static class SdfCollisionBuilder
{
	// The physics engine SILENTLY drops a convex hull with more than 255 half-edges — about 127 edges once
	// coplanar faces merge (measured 2026-10-28: a 44-vertex triangulated sphere passes, 45 vanishes).
	// FlushPending thins any hull over this, so none can disappear.
	const int HullEdgeLimit = 120;   // a little under the 127 ceiling
	const int HullThinVertices = 40; // a thinned hull's vertex count — a triangulated 40 has 114 edges

	// Tessellation of the round primitives' hull point clouds. Collision only needs a coarse convex
	// approximation, so these are far lower than the render mesher's. (Denser was tried 2026-10-28: within
	// HullEdgeLimit a sphere caps near 12 × 4, and the teapot body only tightened 3.98 → 3.87u for more shapes
	// and build time.)
	const int Ring = 12;   // segments around a cylinder/cone
	const int EllipsoidStacks = 3; // horizontal rings between the poles
	const float SplineSweepSpacing = 1.0f; // sample-sphere step along a capsule axis, in local radii (carve
	                                       // classification, clamp samples, foot probes — not physics shapes)
	const float SplineCapsuleTolerance = 0.25f;  // capsule fit: worst surface error allowed, in local radii
	const float SplineCapsuleMinTolerance = 1f;  // ...floored in sculpture units, so a hair-thin tube isn't
	                                             // chased into slivers it can't visibly use

	// Carve-aware path (see BuildCarved). All biases are fractions of the smallest voxel edge.
	const float CarveGateFraction = 0.005f; // swap to voxels only when subtracts remove at least this fraction.
	                                        // Deliberately near zero — just a guard against a lone noisy sample.
	                                        // A kept-exact copy still collides with its carved-off volume (a
	                                        // flattened sphere rests on the missing pole and visibly hovers), so
	                                        // any carve that registers at all should swap; the real "too shallow
	                                        // to matter" filter is CarveBias, not this.
	// ...EXCEPT a cut that is both shallow and small AND leaves the copy non-convex (a dimple, a button hole, an
	// engraved line — a convex result already gets its own exact hull from TryCarvedHull first). Such a cut
	// can't change where the prop rests or open a cavity, but without this it turned one hull into ~50 voxel
	// boxes. Depth is the deepest carved cell CENTRE inside the brush, in cells (a surface cell's centre sits
	// SolidBias..1 cell deep, so anything under one cell can only be the skin); both tests must pass.
	[SkipHotload] public static float CarveShallowCells = 1.0f; // × the smallest voxel edge
	const float CarveShallowFraction = 0.05f;                    // of the brush copy's cells
	// ONE voxel size for every carved collider — solid brushes and spline runs alike — so the boxes read the
	// same everywhere. A box can't follow a CURVED cut, so it can sit up to about one cell into the cut-away
	// space: this edge length IS the flush error. Script-tunable (A/B sweeps); [SkipHotload] so a recompile
	// applies the default.
	[SkipHotload] public static float VoxelTarget = 5f; // target voxel edge (sculpture units)
	[SkipHotload] public static int VoxelMaxPerAxis = 14; // grid cap per axis — bounds the field samples per copy;
	                                                      // past it a big brush's cells stretch
	const float SolidBias = 0.25f;         // a cell centre must be this deep INSIDE the brush to count as solid
	const float CarveBias = 0.35f;         // a subtract must reach this deep to empty a cell — biased SOLID so a
	                                       // wall thinner than a voxel survives (the cavity shrinks a hair
	                                       // instead of the wall dissolving, which would be far worse)
	const float SplineCarveGraze = 0.25f;  // fraction of a swept sphere's radius a subtract may bite before the
	                                       // sphere trades its exact primitive for voxels
	const int SplineMinCellsAcross = 3;    // a voxelised tube gets at least this many cells across its thinnest
	                                       // diameter — the fewest that can hold a bore (wall, hole, wall); only
	                                       // tubes thinner than 3 × VoxelTarget go finer than VoxelTarget

	/// <summary>Differs per compile of this code: [SkipHotload] keeps hotload from carrying the old value over, so
	/// the new assembly's initializer stands. Fold it into any cache key over a build's output — a brush hash alone
	/// survives a hotload, and the cache would keep serving what the OLD builder made.</summary>
	[SkipHotload] public static readonly int CodeVersion = Guid.NewGuid().GetHashCode();

	// Detail gate (see EffectiveMinExtent): an additive brush whose local bounding box is under this on EVERY
	// axis is decoration — an eye, a button, a stud — and contributes no collider. All three axes must be
	// small, so a thin plate (100×100×2) is still a surface you can stand on and stays solid.
	const float MinBrushExtent = 10f;

	/// <summary>One mirror copy that <see cref="Build"/> swapped to carved voxel boxes, recorded so
	/// <see cref="ComputeFootPoints"/> can place ground probes on the ACTUAL emitted collider instead of the
	/// smooth carved field it approximates — the two disagree by up to a voxel (the occupancy biases), which
	/// is enough to hover the body on a phantom probe or bury one under the floor.</summary>
	public sealed class CarvedCopy
	{
		public int BrushIndex;
		public Vector3 Sign;
		public Vector3 Position;   // the frame the boxes live in — the brush transform for solid shapes;
		public Rotation Rotation;  // identity (with Sign folded into the stored data) for a spline copy
		public readonly List<(Vector3 lo, Vector3 hi)> Boxes = new(); // frame-local, corner-to-corner
		public readonly List<Vector4> Spheres = new(); // spline only: the copy's KEPT swept spheres (xyz +
		                                               // radius, sculpture space) — the probe sync must see the
		                                               // copy's FULL collider, or probes over a kept sphere
		                                               // inside the boxes' footprint would be wrongly dropped
	}

	/// <summary>What kind of primitive one emitted collision shape is — for the collision debug view.</summary>
	public enum ShapeKind
	{
		/// <summary>An exact sphere: a uniform sphere brush, or a single-point spline.</summary>
		Sphere,
		/// <summary>One fitted capsule of a spline tube.</summary>
		Capsule,
		/// <summary>An exact convex hull: box/cylinder/cone/prism/ellipsoid/text, or one star kite.</summary>
		Hull,
		/// <summary>A greedy-merged voxel box from the carve-aware path.</summary>
		VoxelBox,
	}

	/// <summary>One shape <see cref="Build"/> emitted, in emission order. Spheres line up with the built model's
	/// <c>Physics.Parts[0].Spheres</c> and hulls/boxes with its <c>Hulls</c>, each in order — the engine keeps
	/// the order, so the debug view can colour the real collider by where each shape came from.
	/// <see cref="BrushIndex"/> indexes the GROUP-FLATTENED brush list (<c>SdfGroups.Flatten</c>).</summary>
	public readonly record struct EmittedShape( ShapeKind Kind, int BrushIndex );

	// Every shape a build makes is RECORDED here first (RecordHull / RecordSphere / RecordCapsule — the only
	// emission points), then FlushPending culls the buried ones and hands the rest to the ModelBuilder in order.
	// The optional captures ride the flush, so they describe exactly the shapes physics gets: _frame as
	// sculpture-space points (hull vertices, voxel-box corners, points on each sphere/capsule end), _emitted as
	// kind + source brush (tracked in _brushIndex). Statics rather than parameters threaded through every helper:
	// Build is main-thread-only (it creates physics shapes) and they live only for the duration of one call.
	sealed class PendingShape
	{
		public ShapeKind Kind;
		public int Brush;
		public List<Vector3> Hull;    // hull / voxel box points; null for a sphere or capsule
		public Vector3 A, B;          // sphere: A == B; capsule: the segment
		public float R;
		public bool Culled;
	}

	static List<Vector3> _frame;
	static List<EmittedShape> _emitted;
	static List<PendingShape> _pending;
	static int _brushIndex;

	static void RecordHull( List<Vector3> hull, ShapeKind kind = ShapeKind.Hull ) =>
		_pending.Add( new PendingShape { Kind = kind, Brush = _brushIndex, Hull = hull } );

	static void RecordSphere( Vector3 c, float r ) =>
		_pending.Add( new PendingShape { Kind = ShapeKind.Sphere, Brush = _brushIndex, A = c, B = c, R = r } );

	static void RecordCapsule( Vector3 a, Vector3 b, float r ) =>
		_pending.Add( new PendingShape { Kind = ShapeKind.Capsule, Brush = _brushIndex, A = a, B = b, R = r } );

	// Thin any hull physics would silently drop, cull the buried shapes, then hand the survivors to the builder in
	// recording order (the debug view relies on the order) along with their frame points and emitted tags. Thinning
	// runs FIRST so the cull judges coverage against the hulls physics actually gets. Null when nothing survives.
	static Model FlushPending( ModelBuilder builder )
	{
		foreach ( var p in _pending )
			if ( p.Hull is { Count: > HullThinVertices } && ConvexHull.EdgeCount( p.Hull ) > HullEdgeLimit )
				p.Hull = ConvexHull.Thin( p.Hull, HullThinVertices );

		if ( CullBuried )
			CullBuriedShapes( _pending );

		bool any = false;
		foreach ( var p in _pending )
		{
			if ( p.Culled )
				continue;
			switch ( p.Kind )
			{
				case ShapeKind.Sphere:
					builder.AddCollisionSphere( p.R, p.A );
					FrameSpherePoints( p.A, p.R );
					break;
				case ShapeKind.Capsule:
					// A capsule's silhouette is its two end spheres' — so their points frame it exactly as well.
					builder.AddCollisionCapsule( p.A, p.B, p.R );
					FrameSpherePoints( p.A, p.R );
					FrameSpherePoints( p.B, p.R );
					break;
				default:
					builder.AddCollisionHull( p.Hull );
					_frame?.AddRange( p.Hull );
					break;
			}
			_emitted?.Add( new EmittedShape( p.Kind, p.Brush ) );
			any = true;
		}
		return any ? builder.Create() : null;
	}

	// ── Buried-shape culling ─────────────────────────────────────────────────────────────────────────────

	// Drop shapes the rest of the collider already covers (CullBuriedShapes). Script-tunable for A/B;
	// [SkipHotload] so a recompile applies the default.
	[SkipHotload] public static bool CullBuried = true;
	[SkipHotload] public static float CullTolerance = 0.5f; // a culled shape may stick out of the rest by at most this
	const int CullMaxSamples = 1500;    // per shape; a bigger surface samples coarser and must sit deeper inside
	const float CullCoverRadius = 0.75f; // any surface point lies within about this × spacing of a sample

	// What the cull reads per shape: bounds, a 1-Lipschitz signed distance (≤ 0 inside), and its surface.
	sealed class CullShape
	{
		public PendingShape P;
		public Vector3 Lo, Hi;
		public float Volume;                                   // of the bounds — the processing order
		public float Area;
		public List<(Vector3 n, float d)> Planes;              // hulls
		public List<(Vector3 a, Vector3 b, Vector3 c)> Tris;   // hulls
		public List<Vector3> Extremes;                         // hull vertices / points on the sphere ends

		// Hull: the max plane distance (exact inside, a lower bound of the true distance outside — still 1-Lipschitz).
		public float Sd( Vector3 p )
		{
			if ( Planes is not null )
			{
				float d = float.MinValue;
				foreach ( var (n, o) in Planes )
					d = MathF.Max( d, Vector3.Dot( n, p ) - o );
				return d;
			}
			return SegmentDistance( p, P.A, P.B ) - P.R;
		}
	}

	/// <summary>Mark every recorded shape that the REST of the collider already covers — the teapot lid's sphere
	/// sitting inside the body and the dome, a spline root sunk into the body — so physics never carries it.
	/// Smallest first, and a culled shape never vouches for a later one, so whatever is culled stays covered by
	/// what's left.
	///
	/// The test is a bound, not a guess: the shape's surface is sampled at spacing h, and every cover's signed
	/// distance is 1-Lipschitz, so if each sample sits at least <see cref="CullCoverRadius"/>·h −
	/// <see cref="CullTolerance"/> inside the union of the covers, no point of the dropped shape sticks out of the
	/// rest by more than the tolerance. (Only the SURFACE is checked: a void enclosed entirely inside the shape by
	/// other shapes would go unseen — not a shape clay produces.) Fixed cost per shape: bounds overlap, a reject
	/// on its extreme points (most shapes stop there), then ≤ <see cref="CullMaxSamples"/> samples.</summary>
	static void CullBuriedShapes( List<PendingShape> shapes )
	{
		if ( shapes.Count < 2 )
			return;

		var cs = new List<CullShape>( shapes.Count );
		foreach ( var p in shapes )
		{
			var c = PrepareCull( p );
			if ( c is not null )
				cs.Add( c );
		}

		var order = new List<int>( cs.Count );
		for ( int i = 0; i < cs.Count; i++ )
			order.Add( i );
		order.Sort( ( x, y ) => cs[x].Volume != cs[y].Volume ? cs[x].Volume.CompareTo( cs[y].Volume ) : x.CompareTo( y ) );

		var covers = new List<CullShape>( 16 );
		var samples = new List<Vector3>( 512 );
		foreach ( int si in order )
		{
			var s = cs[si];
			covers.Clear();
			Vector3 ulo = new( float.MaxValue ), uhi = new( float.MinValue );
			foreach ( var t in cs )
			{
				if ( t == s || t.P.Culled )
					continue;
				if ( t.Lo.x > s.Hi.x || t.Hi.x < s.Lo.x || t.Lo.y > s.Hi.y || t.Hi.y < s.Lo.y || t.Lo.z > s.Hi.z || t.Hi.z < s.Lo.z )
					continue;
				covers.Add( t );
				ulo = Vector3.Min( ulo, t.Lo );
				uhi = Vector3.Max( uhi, t.Hi );
			}
			if ( covers.Count == 0 )
				continue;

			// Cheap rejects: the covers' joint bounds must contain this shape's, and so must its extreme points.
			var tol = new Vector3( CullTolerance );
			if ( s.Lo.x < ulo.x - tol.x || s.Lo.y < ulo.y - tol.y || s.Lo.z < ulo.z - tol.z
				|| s.Hi.x > uhi.x + tol.x || s.Hi.y > uhi.y + tol.y || s.Hi.z > uhi.z + tol.z )
				continue;
			if ( !AllCovered( s.Extremes, covers, CullTolerance ) )
				continue;

			float h = MathF.Max( CullTolerance / CullCoverRadius, MathF.Sqrt( s.Area / CullMaxSamples ) );
			samples.Clear();
			if ( !SampleSurface( s, h, samples ) )
				continue; // couldn't sample it within budget — keep it
			if ( AllCovered( samples, covers, CullTolerance - CullCoverRadius * h ) )
				s.P.Culled = true;
		}
	}

	static bool AllCovered( List<Vector3> pts, List<CullShape> covers, float maxSd )
	{
		foreach ( var p in pts )
		{
			bool ok = false;
			foreach ( var t in covers )
			{
				if ( t.Sd( p ) <= maxSd )
				{
					ok = true;
					break;
				}
			}
			if ( !ok )
				return false;
		}
		return true;
	}

	static CullShape PrepareCull( PendingShape p )
	{
		var c = new CullShape { P = p };
		if ( p.Hull is not null )
		{
			c.Planes = new List<(Vector3 n, float d)>( 32 );
			c.Tris = new List<(Vector3 a, Vector3 b, Vector3 c)>( 32 );
			c.Extremes = new List<Vector3>( 16 );
			if ( !ConvexHull.TryBuild( p.Hull, c.Planes, c.Extremes, c.Tris ) )
				return null; // degenerate — neither cullable nor a trustworthy cover
			c.Lo = new Vector3( float.MaxValue );
			c.Hi = new Vector3( float.MinValue );
			foreach ( var v in c.Extremes )
			{
				c.Lo = Vector3.Min( c.Lo, v );
				c.Hi = Vector3.Max( c.Hi, v );
			}
			foreach ( var (a, b, t) in c.Tris )
				c.Area += 0.5f * Vector3.Cross( b - a, t - a ).Length;
		}
		else
		{
			c.Lo = Vector3.Min( p.A, p.B ) - new Vector3( p.R );
			c.Hi = Vector3.Max( p.A, p.B ) + new Vector3( p.R );
			float len = (p.B - p.A).Length;
			c.Area = 4f * MathF.PI * p.R * p.R + 2f * MathF.PI * p.R * len;
			c.Extremes = new List<Vector3>( 28 );
			AddSpherePoints( c.Extremes, p.A, p.R );
			if ( len > 1e-3f )
				AddSpherePoints( c.Extremes, p.B, p.R );
		}
		var size = c.Hi - c.Lo;
		c.Volume = size.x * size.y * size.z;
		return c;
	}

	// 6 axis extremes + 8 diagonal points, all ON the sphere — enough that its projected rect hugs the silhouette
	// from any angle (worst-case slack ~8% of r, vs 41% for its bounding box's corners). Also the cull's cheap
	// first check of a sphere or capsule end.
	static void AddSpherePoints( List<Vector3> dst, Vector3 c, float r )
	{
		dst.Add( c + new Vector3( r, 0f, 0f ) );
		dst.Add( c - new Vector3( r, 0f, 0f ) );
		dst.Add( c + new Vector3( 0f, r, 0f ) );
		dst.Add( c - new Vector3( 0f, r, 0f ) );
		dst.Add( c + new Vector3( 0f, 0f, r ) );
		dst.Add( c - new Vector3( 0f, 0f, r ) );
		float d = r * 0.577350269f; // r / √3 — the on-sphere diagonal
		for ( int i = 0; i < 8; i++ )
			dst.Add( c + new Vector3( (i & 1) != 0 ? d : -d, (i & 2) != 0 ? d : -d, (i & 4) != 0 ? d : -d ) );
	}

	// Surface samples at spacing ≈ h. False when a hull's triangles would need more than twice the sample budget
	// (long slivers) — the caller then keeps the shape rather than test it coarsely.
	static bool SampleSurface( CullShape s, float h, List<Vector3> dst )
	{
		var p = s.P;
		if ( s.Tris is not null )
		{
			foreach ( var (a, b, c) in s.Tris )
			{
				float edge = MathF.Max( (b - a).Length, MathF.Max( (c - b).Length, (a - c).Length ) );
				int n = Math.Max( 1, (int)MathF.Ceiling( edge / h ) );
				for ( int i = 0; i <= n; i++ )
				for ( int j = 0; j <= n - i; j++ )
					dst.Add( a + (b - a) * (i / (float)n) + (c - a) * (j / (float)n) );
				if ( dst.Count > 2 * CullMaxSamples )
					return false;
			}
			return true;
		}

		// Sphere / capsule: Fibonacci points on the end spheres (only their outward halves for a capsule), rings
		// down the shaft.
		var axis = p.B - p.A;
		float len = axis.Length;
		var dir = len > 1e-3f ? axis / len : Vector3.Up;
		int nCap = Math.Max( 14, (int)MathF.Ceiling( 4f * MathF.PI * p.R * p.R / (h * h) ) );
		for ( int i = 0; i < nCap; i++ )
		{
			float z = 1f - 2f * (i + 0.5f) / nCap;
			float rr = MathF.Sqrt( MathF.Max( 0f, 1f - z * z ) );
			float phi = i * 2.399963229f; // golden angle
			var u = new Vector3( MathF.Cos( phi ) * rr, MathF.Sin( phi ) * rr, z );
			if ( len <= 1e-3f )
				dst.Add( p.A + u * p.R );
			else
				dst.Add( (Vector3.Dot( u, dir ) < 0f ? p.A : p.B) + u * p.R );
		}
		if ( len > 1e-3f )
		{
			var side = Vector3.Cross( dir, MathF.Abs( dir.z ) < 0.9f ? Vector3.Up : Vector3.Forward ).Normal;
			var up = Vector3.Cross( side, dir );
			int rings = Math.Max( 1, (int)MathF.Ceiling( len / h ) );
			int around = Math.Max( 6, (int)MathF.Ceiling( 2f * MathF.PI * p.R / h ) );
			for ( int i = 0; i <= rings; i++ )
			for ( int j = 0; j < around; j++ )
			{
				float ang = MathF.Tau * j / around;
				dst.Add( p.A + axis * (i / (float)rings) + (side * MathF.Cos( ang ) + up * MathF.Sin( ang )) * p.R );
			}
		}
		return true;
	}

	static void FrameSpherePoints( Vector3 c, float r )
	{
		if ( _frame is not null )
			AddSpherePoints( _frame, c, r );
	}

	/// <summary>Build a collision-only Model (no render mesh) from the additive brushes, carving the ones that
	/// later subtractive brushes significantly hollow. MAIN THREAD — creates physics shapes. Returns null when
	/// there's nothing solid to collide with. Pass <paramref name="carvedCopies"/> to receive the voxelised
	/// copies' boxes — hand the same list to <see cref="ComputeFootPoints"/> so the ground probes sit exactly
	/// on the emitted collider. Pass <paramref name="framePoints"/> to receive sculpture-space points outlining
	/// every shape actually emitted — the carve-aware extent, for consumers that must match the VISIBLE clay
	/// (the creative-mode prompt frames the prop on screen with them). Pass <paramref name="emitted"/> to receive
	/// each emitted shape's kind and source brush (see <see cref="EmittedShape"/>).</summary>
	public static Model Build( List<SdfBrush> brushes, List<CarvedCopy> carvedCopies = null, List<Vector3> framePoints = null,
		List<EmittedShape> emitted = null )
	{
		carvedCopies?.Clear();
		framePoints?.Clear();
		emitted?.Clear();
		if ( brushes is null || brushes.Count == 0 )
			return null;

		// Groups collide as their members. Flattened at the entry (and identically in ComputeFootPoints) so
		// CarvedCopy.BrushIndex lines up between the two for the same input list.
		brushes = SdfGroups.Flatten( brushes );

		var cached = GetOrBuild( brushes );
		carvedCopies?.AddRange( cached.Carved );
		framePoints?.AddRange( cached.Frame );
		emitted?.AddRange( cached.Emitted );
		return cached.Model;
	}

	/// <summary>The clay's solid volume (sculpture units³) for the same brush list <see cref="Build"/> takes —
	/// the mass a prop should have, free of the collider's overlapping shapes (every buried sphere, every voxel
	/// box under a hull adds its own volume to the engine's automatic mass). Sampled once per unique shape on
	/// the same cache as the collider.</summary>
	public static float ClayVolume( List<SdfBrush> brushes )
	{
		if ( brushes is null || brushes.Count == 0 )
			return 0f;
		var flat = SdfGroups.Flatten( brushes );
		var built = GetOrBuild( flat );
		built.Volume ??= EstimateVolume( flat ); // lazily: only pawns weigh anything, scenery never asks
		return built.Volume.Value;
	}

	// ── Build cache ─────────────────────────────────────────────────────────────────────────────────────
	// One build per unique shape: identical prefab instances (three plates, fourteen knobs, twelve fridge letters)
	// share a Model — a Model is a resource, and the engine makes each ModelCollider's shapes from it — plus the
	// carved copies, frame points and tags that go with it. Keyed on the brush content plus every setting that
	// changes a build (and CodeVersion, so a hotload never serves the old builder's shapes). Cleared when the
	// play session ends (SessionResetSystem) — statics outlive Stop→Play in the editor — and wholesale when it
	// grows past CacheLimit (sculpting churns through hashes; nothing here is worth an LRU).
	sealed class CachedBuild
	{
		public Model Model;
		public float? Volume; // filled on the first ClayVolume request
		public readonly List<CarvedCopy> Carved = new();
		public readonly List<Vector3> Frame = new();
		public readonly List<EmittedShape> Emitted = new();
	}

	const int CacheLimit = 512;
	static readonly Dictionary<int, CachedBuild> _cache = new();

	/// <summary>Builds that came from the cache rather than running. Diagnostics.</summary>
	public static int CacheHits { get; private set; }

	/// <summary>Drop every cached build (play session end; a settings sweep).</summary>
	public static void ClearCache() => _cache.Clear();

	static int SettingsHash() => HashCode.Combine( CodeVersion, VoxelTarget, VoxelMaxPerAxis, CarvedHulls, CullBuried,
		CullTolerance, CarveShallowCells );

	static CachedBuild GetOrBuild( List<SdfBrush> flat )
	{
		int key = HashCode.Combine( SdfSculpture.ContentHashPrefix( flat, flat.Count, 0, false ), SettingsHash() );
		if ( _cache.TryGetValue( key, out var hit ) )
		{
			CacheHits++;
			return hit;
		}

		if ( _cache.Count >= CacheLimit )
			_cache.Clear();

		var built = new CachedBuild();
		_frame = built.Frame;
		_emitted = built.Emitted;
		_pending = new List<PendingShape>( 64 );
		try
		{
			built.Model = BuildInternal( flat, built.Carved );
		}
		finally
		{
			_frame = null;
			_emitted = null;
			_pending = null;
		}
		_cache[key] = built;
		return built;
	}

	// The clay's solid volume from a fixed grid of field samples over its bounds (~VolumeSamples in all — a few
	// percent accurate, which is all mass needs). Fixed cost: the sample count never grows with the shape.
	// Measured 2026-10-28: ~0.7 ms per unique shape in the kitchen, paid once, lazily, by pawns only.
	const int VolumeSamples = 1024;

	static float EstimateVolume( List<SdfBrush> flat )
	{
		if ( !Sdf.TryGetBounds( flat, out var bounds ) )
			return 0f;

		var size = bounds.Size;
		if ( size.x <= 0f || size.y <= 0f || size.z <= 0f )
			return 0f;

		// Cells roughly cubic: n_i ∝ size_i, with n_x·n_y·n_z ≈ VolumeSamples.
		float scale = MathF.Cbrt( VolumeSamples / (size.x * size.y * size.z) );
		int nx = Math.Clamp( (int)MathF.Round( size.x * scale ), 2, 64 );
		int ny = Math.Clamp( (int)MathF.Round( size.y * scale ), 2, 64 );
		int nz = Math.Clamp( (int)MathF.Round( size.z * scale ), 2, 64 );
		var cell = new Vector3( size.x / nx, size.y / ny, size.z / nz );

		int solid = 0;
		for ( int iz = 0; iz < nz; iz++ )
		for ( int iy = 0; iy < ny; iy++ )
		for ( int ix = 0; ix < nx; ix++ )
		{
			var p = bounds.Mins + new Vector3( (ix + 0.5f) * cell.x, (iy + 0.5f) * cell.y, (iz + 0.5f) * cell.z );
			if ( Sdf.Sample( flat, p ) <= 0f )
				solid++;
		}
		return solid * cell.x * cell.y * cell.z;
	}

	static Model BuildInternal( List<SdfBrush> brushes, List<CarvedCopy> carvedCopies )
	{
		var builder = new ModelBuilder();
		bool any = false;
		Span<Vector3> centres = stackalloc Vector3[8];
		var pts = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 ); // reused swept-sphere buffer (spline brushes)
		var capsules = new List<SplineCapsule>( 16 ); // the current spline's fitted capsules
		var carvers = new List<SdfBrush>( 4 );                      // subtracts that can hollow the current brush
		var carverBounds = new List<(Vector3 lo, Vector3 hi)>( 4 ); // their sculpture-space AABBs (all copies)
		float minExtent = EffectiveMinExtent( brushes );

		for ( int bi = 0; bi < brushes.Count; bi++ )
		{
			var b = brushes[bi];
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue; // hidden or subtractive — contributes no solid volume
			if ( TooSmall( b, minExtent, pts, sweep ) )
				continue; // decoration — no shape, and ComputeFootPoints skips it identically
			_brushIndex = bi;

			// Subtracts that can hollow THIS brush — only ones AFTER it in the list (see the class summary for
			// the ordering rule), pre-filtered by AABB overlap. Text is exempt: glyph strokes are thinner than
			// a voxel, so carving would dissolve the letters — it keeps its ink-box hull no matter what
			// (ComputeFootPoints skips Text's carvers the same way, keeping the probes in sync).
			carvers.Clear();
			if ( b.Shape != SdfShape.Text )
				CollectCarvers( brushes, bi, centres, sweep, carvers, carverBounds );

			// A spline: the fewest capsules that reproduce the drawn tube to within tolerance (FitSplineCapsules),
			// at every mirror copy. A capsule is exactly a tube stretch with round ends, so straight runs cost one
			// shape however long, and the curvature alone decides how many more. When subtracts bite,
			// BuildCarvedSpline decides along each capsule: drop, keep, or voxelise (see summary).
			if ( b.Shape == SdfShape.Spline )
			{
				FitSplineCapsules( b, sweep, capsules );
				if ( carvers.Count > 0 )
				{
					any |= BuildCarvedSpline( b, bi, carvers, carverBounds, capsules, sweep, builder, carvedCopies );
					continue;
				}

				int snx = b.EffectiveMirrorX ? 1 : 0, sny = b.EffectiveMirrorY ? 1 : 0, snz = b.EffectiveMirrorZ ? 1 : 0;
				for ( int sx = 0; sx <= snx; sx++ )
				for ( int sy = 0; sy <= sny; sy++ )
				for ( int sz = 0; sz <= snz; sz++ )
				{
					var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
					foreach ( var cap in capsules )
					{
						var m = cap.Mirrored( sign );
						EmitCapsule( m.A, m.B, m.Radius );
						any = true;
					}
				}
				continue;
			}

			// When any carver might bite, BuildCarved decides per mirror copy between the exact convex shape
			// and carved voxel boxes.
			if ( carvers.Count > 0 )
			{
				any |= BuildCarved( b, bi, carvers, carverBounds, pts, builder, carvedCopies );
				continue;
			}

			// A uniform sphere is the one shape with an exact, cheaper-than-a-hull primitive collider, and it
			// needs no orientation — so just place one per mirror centre. A SLICED sphere isn't a sphere any
			// more (the flat cut must collide flat), so it falls through to the clipped hull below.
			if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) && b.Slice <= 0f )
			{
				int n = b.MirrorCentres( centres );
				for ( int i = 0; i < n; i++ )
				{
					RecordSphere( centres[i], MathF.Max( b.Size.x, 0.5f ) );
					any = true;
				}
				continue;
			}

			// A star cross-section is CONCAVE — one convex hull would fill the notches in. Decompose it into
			// five convex "kite" hulls instead (centre → notch → tip → notch, extruded), which union into the
			// exact sharp star.
			if ( b.Shape == SdfShape.Extruded && b.CrossSection == SdfCrossSection.Star )
			{
				any |= StarHulls( b, builder );
				continue;
			}

			// Everything else: a small convex hull per copy. Build the brush-local point cloud once, then for
			// each mirror sign transform it into sculpture-local space (Position + Rotation, then reflect) — the
			// same mapping the CSG mesher uses. A hull is orientation-agnostic, so reflection needs no winding fix.
			LocalPoints( b, pts );
			if ( pts.Count < 4 )
				continue;

			int nx = b.EffectiveMirrorX ? 1 : 0, ny = b.EffectiveMirrorY ? 1 : 0, nz = b.EffectiveMirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= nx; sx++ )
			for ( int sy = 0; sy <= ny; sy++ )
			for ( int sz = 0; sz <= nz; sz++ )
			{
				var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				var hull = new List<Vector3>( pts.Count );
				foreach ( var lp in pts )
					hull.Add( (b.Position + b.Rotation * lp) * sign );

				RecordHull( hull );
				any = true;
			}
		}

		return any ? FlushPending( builder ) : null;
	}

	// Five convex kite hulls covering a star-profile brush (per mirror copy): each is the extruded quad
	// centre → left notch → tip → right notch. Outline layout: even index = tip, odd = notch (see
	// SdfBrush.CrossSectionOutline). Returns whether anything was added.
	static bool StarHulls( SdfBrush b, ModelBuilder builder )
	{
		var outline = new List<Vector2>( 10 );
		SdfBrush.CrossSectionOutline( b.CrossSection, b.Size, outline );
		if ( outline.Count != 10 )
			return false;

		float sz = MathF.Max( MathF.Abs( b.Size.z ), 0.5f );
		bool any = false;
		int nx = b.EffectiveMirrorX ? 1 : 0, ny = b.EffectiveMirrorY ? 1 : 0, nz = b.EffectiveMirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
		for ( int sy = 0; sy <= ny; sy++ )
		for ( int szn = 0; szn <= nz; szn++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, szn == 1 ? -1f : 1f );
			any |= StarKites( b, builder, sign, outline, sz );
		}
		return any;
	}

	// One mirror copy's worth of star kites (see StarHulls).
	static bool StarKites( SdfBrush b, ModelBuilder builder, Vector3 sign, List<Vector2> outline, float sz )
	{
		bool any = false;
		for ( int t = 0; t < 5; t++ )
		{
			var tip = outline[t * 2];
			var nl = outline[(t * 2 + 9) % 10];
			var nr = outline[t * 2 + 1];

			var hull = new List<Vector3>( 8 );
			foreach ( var v in new[] { Vector2.Zero, nl, tip, nr } )
			{
				hull.Add( (b.Position + b.Rotation * new Vector3( v.x, v.y, sz )) * sign );
				hull.Add( (b.Position + b.Rotation * new Vector3( v.x, v.y, -sz )) * sign );
			}
			RecordHull( hull );
			any = true;
		}
		return any;
	}

	/// <summary>Populate <paramref name="body"/> with ONE brush's collision shapes (all mirror copies), in
	/// SCULPTURE-LOCAL space, for <c>Scene.Trace.Sweep</c> world-overlap tests — the edit-time world clamp and
	/// the commit-time embed backstop (see <see cref="BrushWorldClamp"/>). Any shapes already on the body are
	/// cleared first. Returns false when the brush yields no collision at all (disabled, subtractive, or under
	/// the detail gate) — such a brush can never embed the pawn, so it's free to go anywhere.
	///
	/// Deliberately the UNCARVED primitives — same point clouds / spheres as <see cref="Build"/>'s exact path,
	/// so the tests agree with the real collider by construction. Carved voxel boxes are always INSIDE the
	/// uncarved primitive, so testing the primitive is only ever stricter, never leakier. Two other deliberate
	/// over-covers, same one-sided logic: the concave star tests as its filled convex hull (no kite
	/// decomposition), and the detail gate here is the SOLO <see cref="MinBrushExtent"/> check without
	/// <see cref="EffectiveMinExtent"/>'s all-detail fallback — an all-tiny sculpt goes untested, and the worst
	/// an untested brush can embed is under 10 units, a gentle depenetration rather than a through-wall eject.
	///
	/// <paramref name="inset"/> shrinks every shape (sphere radii down, hull points scaled toward their bounds
	/// centre) so CONTACT never reads as PENETRATION: clay resting flush on the floor — which ground-snap
	/// actively produces — must test clear, or the clamp would disable itself (a blocked baseline releases the
	/// brush) and the backstop would freeze every commit of a grounded prop.
	///
	/// <paramref name="samples"/>, when supplied, is cleared and refilled with a point-sample mirror of the
	/// SAME geometry (sculpture-local; xyz = position, w = sphere radius; each hull contributes its centroid
	/// at w = -1 followed by its corners at w = 0) — the clamp's own contact math for MESH world bodies,
	/// which the native overlap/MTV queries can't see (see <see cref="BrushWorldClamp"/>). Filled from the
	/// same values the shapes are built from, insets included, so the two representations can never
	/// disagree.</summary>
	public static bool BuildSweepShapes( SdfBrush b, PhysicsBody body, float inset, List<Vector4> samples = null )
	{
		body.ClearShapes();
		samples?.Clear();
		if ( b is null || !b.Enabled || b.Operation != SdfOperation.Add )
			return false;

		// A group sweeps as the union of its ADD members' shapes on the one body.
		if ( b.Shape == SdfShape.Group )
		{
			bool anyMember = false;
			foreach ( var m in b.Members() )
				if ( m.Enabled && m.Operation == SdfOperation.Add )
					anyMember |= AppendSweepShapes( m, body, inset, samples );
			return anyMember;
		}

		return AppendSweepShapes( b, body, inset, samples );
	}

	// BuildSweepShapes for ONE plain brush, adding to whatever the body already holds (no ClearShapes).
	static bool AppendSweepShapes( SdfBrush b, PhysicsBody body, float inset, List<Vector4> samples )
	{
		var pts = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 );
		if ( TooSmall( b, MinBrushExtent, pts, sweep ) )
			return false;

		bool any = false;

		// Spline: the same fitted capsules the collider emits, per mirror copy. The sample mirror is spheres
		// along each inset capsule's axis (both ends included) — inside the capsule, so never leakier than it.
		if ( b.Shape == SdfShape.Spline )
		{
			var capsules = new List<SplineCapsule>( 16 );
			FitSplineCapsules( b, sweep, capsules );
			int snx = b.EffectiveMirrorX ? 1 : 0, sny = b.EffectiveMirrorY ? 1 : 0, snz = b.EffectiveMirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= snx; sx++ )
			for ( int sy = 0; sy <= sny; sy++ )
			for ( int sz = 0; sz <= snz; sz++ )
			{
				var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				foreach ( var cap in capsules )
				{
					var m = cap.Mirrored( sign ) with { Radius = MathF.Max( cap.Radius - inset, 0.5f ) };
					if ( m.IsSphere )
						body.AddSphereShape( m.A, m.Radius, rebuildMass: false );
					else
						body.AddCapsuleShape( m.A, m.B, m.Radius, rebuildMass: false );
					if ( samples is not null )
						CapsuleSpheres( m, samples );
					any = true;
				}
			}
			return any;
		}

		// Uniform unsliced sphere: exact primitive, one per mirror centre (same special case as Build).
		if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) && b.Slice <= 0f )
		{
			Span<Vector3> centres = stackalloc Vector3[8];
			int n = b.MirrorCentres( centres );
			float r = MathF.Max( b.Size.x - inset, 0.5f );
			for ( int i = 0; i < n; i++ )
			{
				body.AddSphereShape( centres[i], r, rebuildMass: false );
				samples?.Add( new Vector4( centres[i].x, centres[i].y, centres[i].z, r ) );
				any = true;
			}
			return any;
		}

		// Everything else: the shared brush-local point cloud, inset, then one hull per mirror copy — the
		// exact mapping Build uses ((Position + Rotation·p) · sign; hulls are orientation-agnostic under
		// reflection). pts was filled by TooSmall above.
		if ( pts.Count < 4 )
			return false;
		InsetLocalPoints( pts, inset );

		int nx = b.EffectiveMirrorX ? 1 : 0, ny = b.EffectiveMirrorY ? 1 : 0, nz = b.EffectiveMirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
		for ( int sy = 0; sy <= ny; sy++ )
		for ( int sz = 0; sz <= nz; sz++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
			var hull = new List<Vector3>( pts.Count );
			var centroid = Vector3.Zero;
			foreach ( var lp in pts )
			{
				var p = (b.Position + b.Rotation * lp) * sign;
				hull.Add( p );
				centroid += p;
			}
			centroid /= hull.Count;

			// Sample mirror: this hull's centroid FIRST, marked with w = -1 — the "hull anchor" the mesh
			// contact path keys its straddle rays on (centroid → each corner) — then every corner at w = 0.
			if ( samples is not null )
			{
				samples.Add( new Vector4( centroid.x, centroid.y, centroid.z, -1f ) );
				foreach ( var p in hull )
					samples.Add( new Vector4( p.x, p.y, p.z, 0f ) );
			}

			body.AddHullShape( Vector3.Zero, Rotation.Identity, hull, rebuildMass: false );
			any = true;
		}
		return any;
	}

	// Shrink a brush-local point cloud by `inset` per axis, toward its own bounds centre (the cloud isn't
	// always centred — the base-pivot cone runs 0..2h). Clamped so a thin axis never inverts: past the floor
	// the shape just stops shrinking on that axis, which only makes the test stricter.
	static void InsetLocalPoints( List<Vector3> pts, float inset )
	{
		if ( inset <= 0f || pts.Count == 0 )
			return;

		Vector3 lo = new( float.MaxValue ), hi = new( float.MinValue );
		foreach ( var p in pts )
		{
			lo = Vector3.Min( lo, p );
			hi = Vector3.Max( hi, p );
		}

		var centre = (lo + hi) * 0.5f;
		var half = (hi - lo) * 0.5f;
		var scale = new Vector3(
			half.x > 1e-3f ? MathF.Max( half.x - inset, 0.25f ) / half.x : 1f,
			half.y > 1e-3f ? MathF.Max( half.y - inset, 0.25f ) / half.y : 1f,
			half.z > 1e-3f ? MathF.Max( half.z - inset, 0.25f ) / half.z : 1f );

		for ( int i = 0; i < pts.Count; i++ )
			pts[i] = centre + (pts[i] - centre) * scale;
	}

	// Subtractive brushes AFTER index that could overlap brushes[index] — any mirror copy of either side.
	// The order constraint mirrors SdfBrush.Sample's list-order fold: a subtract only carves what's already
	// there, and an add placed after a subtract re-fills the hole (with its own collider, so it needs no
	// special handling here). The AABB test is purely a skip-work filter — a false positive just means
	// BuildCarved samples, finds nothing carved, and keeps the exact shape.
	static void CollectCarvers( List<SdfBrush> brushes, int index, Span<Vector3> centres, List<Vector4> sweep,
		List<SdfBrush> dst, List<(Vector3 lo, Vector3 hi)> bounds )
	{
		dst.Clear();
		bounds.Clear();

		var b = brushes[index];
		Vector3 plo = new( float.MaxValue ), phi = new( float.MinValue );
		if ( b.Shape == SdfShape.Spline )
		{
			// A spline's Position/Size mean nothing — bound it by its swept spheres, then union in the AABB's
			// reflection across each enabled mirror axis (reflecting an interval [lo,hi] gives [-hi,-lo]).
			b.BuildSplineSweep( sweep, SplineSweepSpacing );
			if ( sweep.Count == 0 )
				return;
			foreach ( var pt in sweep )
			{
				var c = new Vector3( pt.x, pt.y, pt.z );
				plo = Vector3.Min( plo, c - new Vector3( pt.w ) );
				phi = Vector3.Max( phi, c + new Vector3( pt.w ) );
			}
			var baseLo = plo;
			var baseHi = phi;
			int mx = b.EffectiveMirrorX ? 1 : 0, my = b.EffectiveMirrorY ? 1 : 0, mz = b.EffectiveMirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= mx; sx++ )
			for ( int sy = 0; sy <= my; sy++ )
			for ( int sz = 0; sz <= mz; sz++ )
			{
				var rlo = new Vector3( sx == 1 ? -baseHi.x : baseLo.x, sy == 1 ? -baseHi.y : baseLo.y, sz == 1 ? -baseHi.z : baseLo.z );
				var rhi = new Vector3( sx == 1 ? -baseLo.x : baseHi.x, sy == 1 ? -baseLo.y : baseHi.y, sz == 1 ? -baseLo.z : baseHi.z );
				plo = Vector3.Min( plo, rlo );
				phi = Vector3.Max( phi, rhi );
			}
		}
		else
		{
			var ext = b.AabbExtents( b.Rotation, false );
			int n = b.MirrorCentres( centres );
			for ( int i = 0; i < n; i++ )
			{
				plo = Vector3.Min( plo, centres[i] - ext );
				phi = Vector3.Max( phi, centres[i] + ext );
			}
		}

		for ( int j = index + 1; j < brushes.Count; j++ )
		{
			var s = brushes[j];
			if ( !s.Enabled || s.Operation != SdfOperation.Subtract )
				continue;

			// A subtractive spline has no Position/Size to bound cheaply — treat it as everywhere (its
			// Distance() is exact where it matters; this AABB only gates the sampling).
			if ( s.Shape == SdfShape.Spline )
			{
				dst.Add( s );
				bounds.Add( (new Vector3( -1e9f ), new Vector3( 1e9f )) );
				continue;
			}

			var se = s.AabbExtents( s.Rotation ); // blend-padded — a smooth subtract erodes past the primitive
			int m = s.MirrorCentres( centres );
			Vector3 slo = new( float.MaxValue ), shi = new( float.MinValue );
			for ( int i = 0; i < m; i++ )
			{
				slo = Vector3.Min( slo, centres[i] - se );
				shi = Vector3.Max( shi, centres[i] + se );
			}

			if ( slo.x > phi.x || shi.x < plo.x || slo.y > phi.y || shi.y < plo.y || slo.z > phi.z || shi.z < plo.z )
				continue; // can't touch this brush anywhere

			dst.Add( s );
			bounds.Add( (slo, shi) );
		}
	}

	// Carve-aware collider for one additive brush: per mirror copy, sample the brush's field minus its
	// carvers on a coarse brush-aligned voxel grid (grid axes = brush axes, so a box cavity's walls land
	// voxel-aligned). Copies the subtracts barely graze keep their exact convex shape (no chunky downgrade
	// for a dent); significantly hollowed copies become greedy-merged voxel boxes, so cavities are real —
	// things can fall into an open bin. Everything stays convex for the dynamic Rigidbody.
	static bool BuildCarved( SdfBrush b, int index, List<SdfBrush> carvers, List<(Vector3 lo, Vector3 hi)> carverBounds,
		List<Vector3> pts, ModelBuilder builder, List<CarvedCopy> carvedCopies )
	{
		LocalPoints( b, pts );
		if ( pts.Count < 4 )
			return false;

		// Brush-local AABB of the hull cloud = the voxel grid bounds.
		Vector3 lo = new( float.MaxValue ), hi = new( float.MinValue );
		foreach ( var lp in pts )
		{
			lo = Vector3.Min( lo, lp );
			hi = Vector3.Max( hi, lp );
		}

		var span = hi - lo;
		int nx = Math.Clamp( (int)MathF.Ceiling( span.x / VoxelTarget ), 2, VoxelMaxPerAxis );
		int ny = Math.Clamp( (int)MathF.Ceiling( span.y / VoxelTarget ), 2, VoxelMaxPerAxis );
		int nz = Math.Clamp( (int)MathF.Ceiling( span.z / VoxelTarget ), 2, VoxelMaxPerAxis );
		var cell = new Vector3( span.x / nx, span.y / ny, span.z / nz );
		float cellMin = MathF.Min( cell.x, MathF.Min( cell.y, cell.z ) );
		float solidAt = -SolidBias * cellMin; // required depth inside the brush
		float carveAt = -CarveBias * cellMin; // required depth inside a subtract
		float emptyAt = HullPhantomFraction * cellMin; // clearly outside the clay (TryCarvedHull's rejection test)

		// This copy's sculpture-space AABB pieces, for the per-copy carver skip. The extents are the abs-sums
		// of the rotated grid half-axes, which reflection can't change — only the centre moves per copy.
		var cLocal = (lo + hi) * 0.5f;
		var ax = b.Rotation * new Vector3( span.x * 0.5f, 0f, 0f );
		var ay = b.Rotation * new Vector3( 0f, span.y * 0.5f, 0f );
		var az = b.Rotation * new Vector3( 0f, 0f, span.z * 0.5f );
		var cext = new Vector3(
			MathF.Abs( ax.x ) + MathF.Abs( ay.x ) + MathF.Abs( az.x ),
			MathF.Abs( ax.y ) + MathF.Abs( ay.y ) + MathF.Abs( az.y ),
			MathF.Abs( ax.z ) + MathF.Abs( ay.z ) + MathF.Abs( az.z ) );

		var solid = new bool[nx * ny * nz];
		var empty = new bool[nx * ny * nz];
		bool any = false;

		int mx = b.EffectiveMirrorX ? 1 : 0, my = b.EffectiveMirrorY ? 1 : 0, mz = b.EffectiveMirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= mx; sx++ )
		for ( int sy = 0; sy <= my; sy++ )
		for ( int sz = 0; sz <= mz; sz++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );

			// A copy no carver's AABB reaches keeps its exact shape without sampling — the carve is judged
			// PER COPY because a one-sided subtract must not hollow the untouched mirror image.
			var ccentre = (b.Position + b.Rotation * cLocal) * sign;
			bool touched = false;
			foreach ( var (nlo, nhi) in carverBounds )
			{
				if ( ccentre.x - cext.x <= nhi.x && ccentre.x + cext.x >= nlo.x &&
					 ccentre.y - cext.y <= nhi.y && ccentre.y + cext.y >= nlo.y &&
					 ccentre.z - cext.z <= nhi.z && ccentre.z + cext.z >= nlo.z )
				{
					touched = true;
					break;
				}
			}
			if ( !touched )
			{
				any |= EmitExactCopy( b, sign, pts, builder );
				continue;
			}

			// Occupancy: a cell is solid where the brush is (SolidBias deep, so the boxes sit a hair inside
			// the surface like the hull path's sharp primitives) and no carver eats it (CarveBias deep). Cells
			// CLEARLY outside the clay (past emptyAt, either way) are flagged for TryCarvedHull's check.
			int kept = 0, carved = 0;
			float maxCut = 0f; // deepest carved cell centre inside the brush — how far the exact hull would over-cover
			Array.Clear( solid, 0, solid.Length );
			Array.Clear( empty, 0, empty.Length );
			for ( int iz = 0; iz < nz; iz++ )
			for ( int iy = 0; iy < ny; iy++ )
			for ( int ix = 0; ix < nx; ix++ )
			{
				int ci = (iz * ny + iy) * nx + ix;
				var lp = new Vector3( lo.x + (ix + 0.5f) * cell.x, lo.y + (iy + 0.5f) * cell.y, lo.z + (iz + 0.5f) * cell.z );
				var p = (b.Position + b.Rotation * lp) * sign;
				float bd = b.Distance( p );
				if ( bd > solidAt )
				{
					empty[ci] = bd > emptyAt;
					continue; // outside (or in the skin of) the brush — not part of it either way
				}

				float depth = float.MinValue; // deepest a carver reaches here
				foreach ( var s in carvers )
					depth = MathF.Max( depth, -s.Distance( p ) );

				if ( depth > -carveAt )
				{
					carved++;
					maxCut = MathF.Max( maxCut, -bd );
					empty[ci] = depth > emptyAt;
					continue;
				}
				solid[ci] = true;
				kept++;
			}

			// Untouched (or a degenerate brush the grid straddled — total == 0) → the exact convex shape.
			int total = kept + carved;
			if ( total == 0 || carved == 0 )
			{
				any |= EmitExactCopy( b, sign, pts, builder );
				continue;
			}
			if ( kept == 0 )
				continue; // the subtracts ate the whole copy — nothing solid left

			// Convex after the cut → one exact hull of the cut shape, however small the cut: a sphere with 3u
			// shaved off its bottom rests on the shave, not 3u in the air on the pole it no longer has.
			if ( CarvedHulls && TryCarvedHull( b, sign, pts, carvers, lo, cell, nx, ny, nz, empty, builder ) )
			{
				any = true;
				continue;
			}

			// Not convex, but a graze (CarveGateFraction) or a shallow, small cut (CarveShallowCells) → the uncut
			// shape, over-covering the cut by at most about a cell, instead of a voxel box per dent.
			bool graze = carved < total * CarveGateFraction;
			bool shallow = maxCut <= CarveShallowCells * cellMin && carved <= total * CarveShallowFraction;
			if ( graze || shallow )
			{
				any |= EmitExactCopy( b, sign, pts, builder );
				continue;
			}

			CarvedCopy record = null;
			if ( carvedCopies is not null )
			{
				record = new CarvedCopy { BrushIndex = index, Sign = sign, Position = b.Position, Rotation = b.Rotation };
				carvedCopies.Add( record );
			}
			any |= GreedyBoxes( b.Position, b.Rotation, sign, lo, cell, solid, nx, ny, nz, builder, record, carvers );
		}

		return any;
	}

	// ── Carved copy → one convex hull ────────────────────────────────────────────────────────────────────

	// A carved copy whose result is still CONVEX — a sphere with a cap sliced off (the teapot body and lid) —
	// collides as ONE hull instead of a staircase of voxel boxes. Timed 2026-10-28 on kitchen_creative: same build
	// cost as the voxel path (≤ +0.3 ms per prop), carved props 821 → 684 shapes, fit equal or better. A switch
	// rather than a const so it can be A/B'd from a script; [SkipHotload] so a recompile applies the default.
	[SkipHotload] public static bool CarvedHulls = true;
	const float HullPhantomFraction = 0.5f; // a hull may cover no cell more than this many cells outside the clay
	const int CarvedHullBisect = 8;         // bisection steps locating the cut rim (1/256 of a point-cloud gap)

	/// <summary>Collide a carved copy as ONE convex hull when the carved result is convex. Deliberately a single
	/// attempt with a fixed cost — no search, no splitting: Build runs synchronously on load and in the
	/// collision preview, and a multi-hull decomposition here froze the editor.
	///
	/// Points: the brush's own coarse hull cloud (<paramref name="pts"/>, the same one the uncarved collider
	/// uses) minus the points the carvers remove, plus the cut rim — for each removed point, where the carve
	/// surface crosses the segment to its nearest kept point (bisected). Cost: one carver evaluation per cloud
	/// point, <see cref="CarvedHullBisect"/> per removed point, a hull of ~100 points, and one pass of the
	/// occupancy cells the voxel path already sampled.
	///
	/// Accepted only if no cell the hull covers is <paramref name="empty"/> (clearly outside the clay), so a real
	/// cavity — a hollowed bowl — always falls back to voxel boxes. No CarvedCopy record: the hull follows the
	/// carved field, which the foot probes already march for carved brushes.</summary>
	static bool TryCarvedHull( SdfBrush b, Vector3 sign, List<Vector3> pts, List<SdfBrush> carvers, Vector3 lo, Vector3 cell,
		int nx, int ny, int nz, bool[] empty, ModelBuilder builder )
	{
		float CarveDepth( Vector3 lp )
		{
			var p = (b.Position + b.Rotation * lp) * sign;
			float d = float.MinValue;
			foreach ( var s in carvers )
				d = MathF.Max( d, -s.Distance( p ) );
			return d;
		}

		var keptPts = new List<Vector3>( pts.Count );
		var removed = new List<Vector3>( pts.Count );
		foreach ( var lp in pts )
			(CarveDepth( lp ) > 0f ? removed : keptPts).Add( lp );
		if ( keptPts.Count < 4 )
			return false;

		var hullPts = new List<Vector3>( keptPts.Count + removed.Count );
		hullPts.AddRange( keptPts );
		foreach ( var r in removed )
		{
			var k = keptPts[0];
			float best = float.MaxValue;
			foreach ( var c in keptPts )
			{
				float d2 = (c - r).LengthSquared;
				if ( d2 < best )
				{
					best = d2;
					k = c;
				}
			}

			// k is outside every carver, r inside one: keep the outside end of the bracket.
			Vector3 outside = k, inside = r;
			for ( int i = 0; i < CarvedHullBisect; i++ )
			{
				var m = (outside + inside) * 0.5f;
				if ( CarveDepth( m ) > 0f )
					inside = m;
				else
					outside = m;
			}
			hullPts.Add( outside );
		}

		var planes = new List<(Vector3 n, float d)>( 128 );
		var verts = new List<Vector3>( 64 );
		if ( !ConvexHull.TryBuild( hullPts, planes, verts ) )
			return false;

		// Reject if the hull covers any clearly-empty cell — the carve left a concavity one hull would fill.
		for ( int iz = 0; iz < nz; iz++ )
		for ( int iy = 0; iy < ny; iy++ )
		for ( int ix = 0; ix < nx; ix++ )
		{
			if ( !empty[(iz * ny + iy) * nx + ix] )
				continue;
			var lp = new Vector3( lo.x + (ix + 0.5f) * cell.x, lo.y + (iy + 0.5f) * cell.y, lo.z + (iz + 0.5f) * cell.z );
			bool inside = true;
			foreach ( var (n, d) in planes )
			{
				if ( Vector3.Dot( n, lp ) - d > 1e-3f )
				{
					inside = false;
					break;
				}
			}
			if ( inside )
				return false;
		}

		var hull = new List<Vector3>( verts.Count );
		foreach ( var v in verts )
			hull.Add( (b.Position + b.Rotation * v) * sign );
		RecordHull( hull );
		return true;
	}

	// Carve-aware collider for a spline: carve ALONG each fitted capsule, judged at sample spheres spaced down
	// its axis — a sample a subtract fully swallows is dropped, one it merely grazes (< SplineCarveGraze of the
	// radius) is kept, and one it genuinely bites is voxelised. Consecutive samples of one class merge: a kept
	// run within one capsule becomes ONE capsule (first to last kept centre); a bitten run — across capsule
	// joints too, since a carve tends to sit on a tightly-fitted bend or flare — becomes ONE chord-aligned voxel
	// grid, so the greedy merge runs boxes down the tube (a bored-out spout is a ring of long staves, not a
	// pile of cubes). Judged per mirror copy like everything else (an asymmetric subtract must not hollow the
	// untouched reflection).
	static bool BuildCarvedSpline( SdfBrush b, int index, List<SdfBrush> carvers, List<(Vector3 lo, Vector3 hi)> carverBounds,
		List<SplineCapsule> capsules, List<Vector4> line, ModelBuilder builder, List<CarvedCopy> carvedCopies )
	{
		bool any = false;
		var cls = new List<int>( 32 );    // per sample: 0 swallowed, 1 kept, 2 bitten
		var owner = new List<int>( 32 );  // per sample: its capsule (a joint appears once per capsule)
		var runs = new List<CarvedCopy>( 4 );

		int mx = b.EffectiveMirrorX ? 1 : 0, my = b.EffectiveMirrorY ? 1 : 0, mz = b.EffectiveMirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= mx; sx++ )
		for ( int sy = 0; sy <= my; sy++ )
		for ( int sz = 0; sz <= mz; sz++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );

			// The copy's kept samples (sign-folded sculpture space, frame = identity), so the probe sync sees
			// the copy's FULL collider; each voxelised run gets its own record in its own capsule frame. Only
			// published if anything actually voxelised — an untouched copy needs no sync.
			var kept = new CarvedCopy { BrushIndex = index, Sign = Vector3.One, Position = Vector3.Zero, Rotation = Rotation.Identity };
			runs.Clear();

			// The whole chain of samples, capsule after capsule (w = that capsule's radius).
			line.Clear();
			owner.Clear();
			for ( int ki = 0; ki < capsules.Count; ki++ )
			{
				int before = line.Count;
				CapsuleSpheres( capsules[ki].Mirrored( sign ), line );
				for ( int k = before; k < line.Count; k++ )
					owner.Add( ki );
			}

			// Classify: the deepest bite among the carvers whose AABB reaches the sample, at its centre —
			// the nearest carver surface being r away in either direction classifies it outright.
			cls.Clear();
			foreach ( var s in line )
			{
				float r = s.w, d = float.MaxValue;
				for ( int ci = 0; ci < carvers.Count; ci++ )
				{
					var (nlo, nhi) = carverBounds[ci];
					if ( s.x + r < nlo.x || s.x - r > nhi.x ||
						 s.y + r < nlo.y || s.y - r > nhi.y ||
						 s.z + r < nlo.z || s.z - r > nhi.z )
						continue;
					d = MathF.Min( d, carvers[ci].Distance( new Vector3( s.x, s.y, s.z ) ) );
				}
				cls.Add( d <= -r ? 0 : d >= r * (1f - SplineCarveGraze) ? 1 : 2 );
			}

			// Emit each same-class run whole — kept runs stop at capsule joints (a capsule is straight).
			for ( int i = 0; i < line.Count; )
			{
				int j = i;
				while ( j + 1 < line.Count && cls[j + 1] == cls[i] && (cls[i] != 1 || owner[j + 1] == owner[i]) )
					j++;

				if ( cls[i] == 1 )
				{
					var a = new Vector3( line[i].x, line[i].y, line[i].z );
					var e = new Vector3( line[j].x, line[j].y, line[j].z );
					EmitCapsule( a, e, line[i].w );
					for ( int k = i; k <= j; k++ )
						kept.Spheres.Add( line[k] );
					any = true;
				}
				else if ( cls[i] == 2 )
				{
					any |= VoxeliseSplineRun( line, i, j, index, carvers, carverBounds, builder, runs );
				}
				i = j + 1;
			}

			if ( carvedCopies is not null && runs.Count > 0 )
			{
				carvedCopies.Add( kept );
				carvedCopies.AddRange( runs );
			}
		}
		return any;
	}

	/// <summary>One collision capsule of a spline tube, in sculpture space. A == B is a plain sphere (a
	/// single-point spline).</summary>
	readonly record struct SplineCapsule( Vector3 A, Vector3 B, float Radius )
	{
		public bool IsSphere => (B - A).LengthSquared < 1e-4f;
		public SplineCapsule Mirrored( Vector3 sign ) => this with { A = A * sign, B = B * sign };
	}

	// Record one capsule (or the sphere a degenerate one is).
	static void EmitCapsule( Vector3 a, Vector3 b, float r )
	{
		if ( (b - a).LengthSquared < 1e-4f )
		{
			RecordSphere( a, r );
			return;
		}
		RecordCapsule( a, b, r );
	}

	// Sample spheres down one capsule's axis, both ends included, at most SplineSweepSpacing radii apart —
	// what the carve classifier, the clamp's sample mirror and the foot probes read in place of the capsule.
	static void CapsuleSpheres( SplineCapsule cap, List<Vector4> dst )
	{
		float len = (cap.B - cap.A).Length;
		int n = Math.Clamp( (int)MathF.Ceiling( len / (cap.Radius * SplineSweepSpacing) ), 1, 64 );
		if ( cap.IsSphere )
			n = 0;
		for ( int i = 0; i <= n; i++ )
		{
			var c = n == 0 ? cap.A : Vector3.Lerp( cap.A, cap.B, i / (float)n );
			dst.Add( new Vector4( c.x, c.y, c.z, cap.Radius ) );
		}
	}

	/// <summary>Fit the FEWEST capsules that reproduce <paramref name="b"/>'s tube to within
	/// <see cref="SplineCapsuleTolerance"/> of the local radius: a greedy longest-fit walk over the drawn skeleton
	/// (<see cref="SdfBrush.BuildSplinePolyline"/> — the exact rounded-cone chain the renderer marches), where a
	/// stretch's error at each skeleton sphere is its distance off the capsule axis PLUS its radius' difference
	/// from the capsule's (the best radius for the stretch). A straight run collapses to one capsule however long;
	/// curvature and taper alone spend more. The skeleton is densified first, so a tapered straight span (two
	/// skeleton points) still has interior points to split at. Unmirrored, sculpture space.</summary>
	static void FitSplineCapsules( SdfBrush b, List<Vector4> scratch, List<SplineCapsule> dst )
	{
		dst.Clear();
		b.BuildSplinePolyline( scratch );
		if ( scratch.Count == 0 )
			return;

		var dense = new List<Vector4>( scratch.Count * 2 ) { scratch[0] };
		for ( int i = 1; i < scratch.Count; i++ )
		{
			var a = scratch[i - 1];
			var c = scratch[i];
			float len = (new Vector3( c.x, c.y, c.z ) - new Vector3( a.x, a.y, a.z )).Length;
			float step = MathF.Max( 0.5f * MathF.Min( a.w, c.w ), SplineCapsuleMinTolerance );
			int k = Math.Clamp( (int)MathF.Ceiling( len / step ), 1, 32 );
			for ( int s = 1; s <= k; s++ )
			{
				float t = s / (float)k;
				dense.Add( new Vector4( a.x + (c.x - a.x) * t, a.y + (c.y - a.y) * t, a.z + (c.z - a.z) * t, a.w + (c.w - a.w) * t ) );
			}
		}

		if ( dense.Count == 1 )
		{
			var p = new Vector3( dense[0].x, dense[0].y, dense[0].z );
			dst.Add( new SplineCapsule( p, p, MathF.Max( dense[0].w, 0.5f ) ) );
			return;
		}

		// Greedy: from each start, the LONGEST capsule that still fits. (Not Douglas–Peucker: on a steady taper
		// the worst radius error always sits beside an end, so splitting at it peels one sliver per step — the
		// teapot spout came out as a run of 1.4u capsules.) The error isn't strictly monotone in the end point
		// (a chord can re-straighten past a wiggle), so look a few samples past the first miss before settling.
		const int lookahead = 6;
		int lo = 0, last = dense.Count - 1;
		while ( lo < last )
		{
			int best = lo + 1;
			float bestR = Fits( lo, best, out float r1 ) ? r1 : (dense[lo].w + dense[best].w) * 0.5f;
			for ( int hi = lo + 2; hi <= last && hi - best <= lookahead; hi++ )
			{
				if ( Fits( lo, hi, out float r ) )
				{
					best = hi;
					bestR = r;
				}
			}

			var a = new Vector3( dense[lo].x, dense[lo].y, dense[lo].z );
			var c = new Vector3( dense[best].x, dense[best].y, dense[best].z );
			dst.Add( new SplineCapsule( a, c, MathF.Max( bestR, 0.5f ) ) );
			lo = best;
		}

		// Does one capsule lo→hi hold every skeleton sphere in between to tolerance? Each sphere i, sitting d_i
		// off the axis, needs |r_i − r| ≤ tol − d_i — an interval for r. They fit iff the intervals intersect;
		// r is then the intersection's middle (the best radius, not just the mid one).
		bool Fits( int lo, int hi, out float r )
		{
			var a = new Vector3( dense[lo].x, dense[lo].y, dense[lo].z );
			var c = new Vector3( dense[hi].x, dense[hi].y, dense[hi].z );
			float rMin = float.MaxValue;
			for ( int i = lo; i <= hi; i++ )
				rMin = MathF.Min( rMin, dense[i].w );
			float tol = MathF.Max( SplineCapsuleTolerance * rMin, SplineCapsuleMinTolerance );

			float low = float.MinValue, high = float.MaxValue;
			r = 0f;
			for ( int i = lo; i <= hi; i++ )
			{
				float slack = tol - SegmentDistance( new Vector3( dense[i].x, dense[i].y, dense[i].z ), a, c );
				if ( slack < 0f )
					return false;
				low = MathF.Max( low, dense[i].w - slack );
				high = MathF.Min( high, dense[i].w + slack );
				if ( low > high )
					return false;
			}
			r = (low + high) * 0.5f;
			return true;
		}
	}

	static float SegmentDistance( Vector3 p, Vector3 a, Vector3 b )
	{
		var ab = b - a;
		float l2 = ab.LengthSquared;
		float t = l2 > 1e-8f ? Math.Clamp( Vector3.Dot( p - a, ab ) / l2, 0f, 1f ) : 0f;
		return (p - (a + ab * t)).Length;
	}

	// One bitten run of the spline's capsule-sample chain (line[i..j], sign-folded sculpture space, w = radius)
	// → carved voxel boxes on ONE grid aligned to the run's chord (frame x = first → last sample, origin the
	// first), so the greedy merge runs boxes down the tube even across capsule joints. The solid is the union
	// of the capsules between consecutive samples — exactly the emitted collider being carved. Cells are the
	// shared VoxelTarget, finer only when the thinnest diameter would get fewer than SplineMinCellsAcross; each
	// axis is capped (then the cells stretch). Occupancy biases match the brush voxeliser's. The run's boxes
	// are recorded in their own frame on a new CarvedCopy appended to `runs`.
	static bool VoxeliseSplineRun( List<Vector4> line, int i, int j, int index, List<SdfBrush> carvers,
		List<(Vector3 lo, Vector3 hi)> carverBounds, ModelBuilder builder, List<CarvedCopy> runs )
	{
		const int maxAlong = 48, maxAcross = 12;
		static Vector3 P( Vector4 v ) => new( v.x, v.y, v.z );

		// Frame: x down the chord — or, for a run that closes on itself, toward its farthest sample.
		var a = P( line[i] );
		var far = P( line[j] );
		for ( int k = i; k <= j && (far - a).LengthSquared < 1e-4f; k++ )
			far = P( line[k] );
		var rot = (far - a).LengthSquared > 1e-6f ? Rotation.LookAt( (far - a).Normal ) : Rotation.Identity;
		var inv = rot.Inverse;

		// Frame-local bounds of every sample sphere, the thinnest radius, and the sculpture-space AABB.
		Vector3 lo = new( float.MaxValue ), hi = new( float.MinValue );
		Vector3 lo3 = new( float.MaxValue ), hi3 = new( float.MinValue );
		float rMin = float.MaxValue;
		for ( int k = i; k <= j; k++ )
		{
			var p = P( line[k] );
			float r = line[k].w;
			var lp = inv * (p - a);
			lo = Vector3.Min( lo, lp - new Vector3( r ) );
			hi = Vector3.Max( hi, lp + new Vector3( r ) );
			lo3 = Vector3.Min( lo3, p - new Vector3( r ) );
			hi3 = Vector3.Max( hi3, p + new Vector3( r ) );
			rMin = MathF.Min( rMin, r );
		}

		// Only the carvers whose AABB reaches the run's.
		var near = new List<SdfBrush>( 4 );
		for ( int ci = 0; ci < carvers.Count; ci++ )
		{
			var (nlo, nhi) = carverBounds[ci];
			if ( hi3.x < nlo.x || lo3.x > nhi.x || hi3.y < nlo.y || lo3.y > nhi.y || hi3.z < nlo.z || lo3.z > nhi.z )
				continue;
			near.Add( carvers[ci] );
		}

		float target = MathF.Min( VoxelTarget, 2f * rMin / SplineMinCellsAcross );
		var ext = hi - lo;
		int nx = Math.Clamp( (int)MathF.Ceiling( ext.x / target ), 1, maxAlong );
		int ny = Math.Clamp( (int)MathF.Ceiling( ext.y / target ), 1, maxAcross );
		int nz = Math.Clamp( (int)MathF.Ceiling( ext.z / target ), 1, maxAcross );
		var cell = new Vector3( ext.x / nx, ext.y / ny, ext.z / nz );
		float cellMin = MathF.Min( cell.x, MathF.Min( cell.y, cell.z ) );
		float solidAt = -SolidBias * cellMin;
		float carveAt = -CarveBias * cellMin;

		var solid = new bool[nx * ny * nz];
		int count = 0;
		for ( int iz = 0; iz < nz; iz++ )
		for ( int iy = 0; iy < ny; iy++ )
		for ( int ix = 0; ix < nx; ix++ )
		{
			var p = a + rot * (lo + new Vector3( (ix + 0.5f) * cell.x, (iy + 0.5f) * cell.y, (iz + 0.5f) * cell.z ));

			// Inside the union of the run's capsule pieces (a lone sample is just its sphere)?
			float dist = (p - P( line[i] )).Length - line[i].w;
			for ( int k = i; k < j && dist > solidAt; k++ )
				dist = MathF.Min( dist, SegmentDistance( p, P( line[k] ), P( line[k + 1] ) ) - MathF.Max( line[k].w, line[k + 1].w ) );
			if ( dist > solidAt )
				continue; // outside (or in the skin of) the tube

			bool cut = false;
			foreach ( var s in near )
			{
				if ( s.Distance( p ) < carveAt )
				{
					cut = true;
					break;
				}
			}
			if ( cut )
				continue;

			solid[(iz * ny + iy) * nx + ix] = true;
			count++;
		}

		if ( count == 0 )
			return false; // biased sampling ate it whole — close enough to fully swallowed

		var record = new CarvedCopy { BrushIndex = index, Sign = Vector3.One, Position = a, Rotation = rot };
		runs.Add( record );
		return GreedyBoxes( a, rot, Vector3.One, lo, cell, solid, nx, ny, nz, builder, record, near );
	}

	// One mirror copy's EXACT convex collider — the same shapes the uncarved path builds, emitted for a
	// single reflection (used when only some copies of a brush are actually carved). LocalCentre is zero for
	// every shape but the cone (which has no primitive shortcut), so the plain sphere sits on Position.
	static bool EmitExactCopy( SdfBrush b, Vector3 sign, List<Vector3> pts, ModelBuilder builder )
	{
		if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) && b.Slice <= 0f )
		{
			RecordSphere( b.Position * sign, MathF.Max( b.Size.x, 0.5f ) );
			return true;
		}

		if ( b.Shape == SdfShape.Extruded && b.CrossSection == SdfCrossSection.Star )
		{
			var outline = new List<Vector2>( 10 );
			SdfBrush.CrossSectionOutline( b.CrossSection, b.Size, outline );
			if ( outline.Count != 10 )
				return false;
			return StarKites( b, builder, sign, outline, MathF.Max( MathF.Abs( b.Size.z ), 0.5f ) );
		}

		var hull = new List<Vector3>( pts.Count );
		foreach ( var lp in pts )
			hull.Add( (b.Position + b.Rotation * lp) * sign );
		RecordHull( hull );
		return true;
	}

	// Merge the solid cells into as few boxes as possible (greedy run → rect → slab growth, consuming cells
	// as it goes) and emit each as an 8-corner hull in sculpture space. Merging matters beyond shape count:
	// an unmerged floor is a row of boxes whose internal seams catch anything sliding across them — merged,
	// a bin floor is ONE box. Each box is then face-clipped against the carvers (ClipBoxToCarvers) — the
	// clipped extents are what's emitted AND recorded, so the foot probes ride the clipped collider.
	static bool GreedyBoxes( Vector3 framePos, Rotation frameRot, Vector3 sign, Vector3 lo, Vector3 cell,
		bool[] solid, int nx, int ny, int nz, ModelBuilder builder, CarvedCopy record, List<SdfBrush> carvers )
	{
		bool any = false;
		for ( int iz = 0; iz < nz; iz++ )
		for ( int iy = 0; iy < ny; iy++ )
		for ( int ix = 0; ix < nx; ix++ )
		{
			if ( !solid[(iz * ny + iy) * nx + ix] )
				continue;

			int ex = ix;
			while ( ex + 1 < nx && solid[(iz * ny + iy) * nx + ex + 1] )
				ex++;

			int ey = iy;
			while ( ey + 1 < ny && RunSolid( solid, nx, ny, ix, ex, ey + 1, iz ) )
				ey++;

			int ez = iz;
			while ( ez + 1 < nz && RectSolid( solid, nx, ny, ix, ex, iy, ey, ez + 1 ) )
				ez++;

			for ( int cz = iz; cz <= ez; cz++ )
			for ( int cy = iy; cy <= ey; cy++ )
			for ( int cx = ix; cx <= ex; cx++ )
				solid[(cz * ny + cy) * nx + cx] = false;

			var blo = new Vector3( lo.x + ix * cell.x, lo.y + iy * cell.y, lo.z + iz * cell.z );
			var bhi = new Vector3( lo.x + (ex + 1) * cell.x, lo.y + (ey + 1) * cell.y, lo.z + (ez + 1) * cell.z );
			ClipBoxToCarvers( ref blo, ref bhi, framePos, frameRot, sign, cell, carvers );

			var hull = new List<Vector3>( 8 );
			for ( int c = 0; c < 8; c++ )
			{
				var corner = new Vector3(
					(c & 1) != 0 ? bhi.x : blo.x,
					(c & 2) != 0 ? bhi.y : blo.y,
					(c & 4) != 0 ? bhi.z : blo.z );
				hull.Add( (framePos + frameRot * corner) * sign );
			}
			RecordHull( hull, ShapeKind.VoxelBox );
			record?.Boxes.Add( (blo, bhi) );
			any = true;
		}
		return any;
	}

	// Sub-cell refinement of one merged box against the carve surface. Cell-centre occupancy leaves each face
	// up to half a cell PLUS the CarveBias skin inside a subtract (~0.85× a cell edge) — on a flat bottom cut
	// that whole overlap is resting height, so the prop visibly hovers. Shrink each face inward by the depth
	// the carve union is GUARANTEED to reach across the whole face — the min over its corner+centre samples,
	// which is exact for a face-parallel flat cut and conservative (never eats true solid; SDF Lipschitz ≤ 1)
	// for oblique or curved ones. Per-face shrink is capped at the overlap the kept boundary cell permits, and
	// opposing faces can't cross — a thin wall still survives, same philosophy as CarveBias itself.
	static void ClipBoxToCarvers( ref Vector3 blo, ref Vector3 bhi, Vector3 framePos, Rotation frameRot, Vector3 sign,
		Vector3 cell, List<SdfBrush> carvers )
	{
		if ( carvers is null || carvers.Count == 0 )
			return;

		float cellMin = MathF.Min( cell.x, MathF.Min( cell.y, cell.z ) );
		var mid = (blo + bhi) * 0.5f;
		Span<float> shrink = stackalloc float[6]; // -x, +x, -y, +y, -z, +z

		for ( int a = 0; a < 3; a++ )
		{
			float ca = a == 0 ? cell.x : a == 1 ? cell.y : cell.z;
			float maxShrink = 0.5f * ca + CarveBias * cellMin; // deeper would contradict the kept boundary cell
			for ( int side = 0; side < 2; side++ )
			{
				float fa = side == 0
					? (a == 0 ? blo.x : a == 1 ? blo.y : blo.z)
					: (a == 0 ? bhi.x : a == 1 ? bhi.y : bhi.z);

				// 4 face corners + face centre; the min depth over them bounds the depth over the whole face
				// for any near-linear carve field (a plane cut exactly, a convex carver conservatively).
				float depth = float.MaxValue;
				for ( int k = 0; k < 5 && depth > 0f; k++ )
				{
					float u = k == 4 ? (a == 0 ? mid.y : mid.x) : ((k & 1) != 0 ? (a == 0 ? bhi.y : bhi.x) : (a == 0 ? blo.y : blo.x));
					float v = k == 4 ? (a == 2 ? mid.y : mid.z) : ((k & 2) != 0 ? (a == 2 ? bhi.y : bhi.z) : (a == 2 ? blo.y : blo.z));
					var lp = a == 0 ? new Vector3( fa, u, v ) : a == 1 ? new Vector3( u, fa, v ) : new Vector3( u, v, fa );
					var p = (framePos + frameRot * lp) * sign;

					float d = float.MinValue; // how deep inside the carve union this sample sits
					foreach ( var s in carvers )
						d = MathF.Max( d, -s.Distance( p ) );
					depth = MathF.Min( depth, d );
				}

				shrink[a * 2 + side] = Math.Clamp( depth, 0f, maxShrink );
			}
		}

		for ( int a = 0; a < 3; a++ )
		{
			float ca = a == 0 ? cell.x : a == 1 ? cell.y : cell.z;
			float loS = shrink[a * 2], hiS = shrink[a * 2 + 1];
			float la = a == 0 ? blo.x : a == 1 ? blo.y : blo.z;
			float ha = a == 0 ? bhi.x : a == 1 ? bhi.y : bhi.z;

			float room = (ha - la) - 0.25f * ca; // keep at least a quarter-cell of thickness
			if ( room <= 0f )
				continue;
			float sum = loS + hiS;
			if ( sum > room )
			{
				float k = room / sum;
				loS *= k;
				hiS *= k;
			}

			if ( a == 0 ) { blo.x += loS; bhi.x -= hiS; }
			else if ( a == 1 ) { blo.y += loS; bhi.y -= hiS; }
			else { blo.z += loS; bhi.z -= hiS; }
		}
	}

	static bool RunSolid( bool[] solid, int nx, int ny, int x0, int x1, int y, int z )
	{
		for ( int x = x0; x <= x1; x++ )
			if ( !solid[(z * ny + y) * nx + x] )
				return false;
		return true;
	}

	static bool RectSolid( bool[] solid, int nx, int ny, int x0, int x1, int y0, int y1, int z )
	{
		for ( int y = y0; y <= y1; y++ )
			if ( !RunSolid( solid, nx, ny, x0, x1, y, z ) )
				return false;
		return true;
	}

	// The detail gate's threshold for THIS brush list — MinBrushExtent normally, or 0 (gate off) when every
	// additive brush is under it. Without that fallback a genuinely tiny prop (a marble, a die, a single sculpt
	// dot) would build no collider at all and fall through the world. Both Build and ComputeFootPoints call
	// this and get the same answer, so the probes can never be placed on a brush the collider discarded.
	static float EffectiveMinExtent( List<SdfBrush> brushes )
	{
		var pts = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 );

		foreach ( var b in brushes )
		{
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue;
			if ( !TooSmall( b, MinBrushExtent, pts, sweep ) )
				return MinBrushExtent; // something substantial survives — safe to drop the details
		}

		return 0f; // it's ALL detail: the whole shape is the prop, collide with every bit of it
	}

	/// <summary>Is each brush of a GROUP-FLATTENED list (<c>SdfGroups.Flatten</c>) dropped by the detail gate?
	/// Same answer <see cref="Build"/> gets — for the collision debug view's "why no collider" labels.</summary>
	public static bool[] DetailGated( List<SdfBrush> flat )
	{
		var gated = new bool[flat.Count];
		float minExtent = EffectiveMinExtent( flat );
		var pts = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 );
		for ( int i = 0; i < flat.Count; i++ )
		{
			var b = flat[i];
			gated[i] = b.Enabled && b.Operation == SdfOperation.Add && TooSmall( b, minExtent, pts, sweep );
		}
		return gated;
	}

	// Is this additive brush pure decoration — under minExtent on all three axes of its LOCAL bounding box?
	// Local, not sculpture-space: a rotated 8³ cube is still an 8³ cube. Splines are measured over their swept
	// spheres (Position/Size mean nothing there), so a long thin tube — a handle, an antenna — stays solid
	// while a stray squiggle doesn't.
	static bool TooSmall( SdfBrush b, float minExtent, List<Vector3> pts, List<Vector4> sweep )
	{
		if ( minExtent <= 0f )
			return false;

		Vector3 lo = new( float.MaxValue ), hi = new( float.MinValue );
		if ( b.Shape == SdfShape.Spline )
		{
			b.BuildSplineSweep( sweep, SplineSweepSpacing );
			if ( sweep.Count == 0 )
				return true;
			foreach ( var pt in sweep )
			{
				var c = new Vector3( pt.x, pt.y, pt.z );
				var r = new Vector3( MathF.Max( pt.w, 0.5f ) );
				lo = Vector3.Min( lo, c - r );
				hi = Vector3.Max( hi, c + r );
			}
		}
		else
		{
			LocalPoints( b, pts );
			if ( pts.Count < 4 )
				return true; // degenerate — Build would emit nothing for it anyway
			foreach ( var lp in pts )
			{
				lo = Vector3.Min( lo, lp );
				hi = Vector3.Max( hi, lp );
			}
		}

		var span = hi - lo;
		return span.x < minExtent && span.y < minExtent && span.z < minExtent;
	}

	static bool IsUniform( Vector3 s )
	{
		float m = MathF.Max( s.x, MathF.Max( s.y, s.z ) );
		float n = MathF.Min( s.x, MathF.Min( s.y, s.z ) );
		return m - n <= 1e-3f * MathF.Max( 1f, m );
	}

	// Brush-local hull points per shape (axis = Z, matching the SDF/CSG conventions). Coarse on purpose —
	// a convex collider doesn't benefit from fine tessellation. Rounding/Blend are ignored, so the collider
	// is the sharp primitive (a hair smaller than the blended visual surface — fine for gameplay).
	// Internal: SculptBounds measures its max-size region against these same support points (a bounding
	// box/sphere reads far past the visible surface for flat/rotated/sliced shapes).
	internal static void LocalPoints( SdfBrush b, List<Vector3> dst )
	{
		dst.Clear();
		var s = b.Size;
		float sx = MathF.Max( MathF.Abs( s.x ), 0.5f );
		float sy = MathF.Max( MathF.Abs( s.y ), 0.5f );
		float sz = MathF.Max( MathF.Abs( s.z ), 0.5f );

		switch ( b.Shape )
		{
			case SdfShape.Box:
				for ( int i = 0; i < 8; i++ )
					dst.Add( new Vector3( (i & 1) != 0 ? sx : -sx, (i & 2) != 0 ? sy : -sy, (i & 4) != 0 ? sz : -sz ) );
				break;

			case SdfShape.Text: // collide as the baked INK rectangle's box, not the letterboxed quad
			{
				var e = b.TextInkExtents();
				float tx = MathF.Max( e.x, 0.5f ), ty = MathF.Max( e.y, 0.5f ), tz = MathF.Max( e.z, 0.5f );
				for ( int i = 0; i < 8; i++ )
					dst.Add( new Vector3( (i & 1) != 0 ? tx : -tx, (i & 2) != 0 ? ty : -ty, (i & 4) != 0 ? tz : -tz ) );
				break;
			}

			case SdfShape.Cylinder:
				for ( int k = 0; k < Ring; k++ )
				{
					float a = k / (float)Ring * MathF.PI * 2f;
					float cx = MathF.Cos( a ) * sx, cy = MathF.Sin( a ) * sx;
					dst.Add( new Vector3( cx, cy, sz ) );
					dst.Add( new Vector3( cx, cy, -sz ) );
				}
				break;

			case SdfShape.Cone: // BASE-pivot: base radius Size.x at z=0 (on Position), apex at z=+2·Size.z
			{
				for ( int k = 0; k < Ring; k++ )
				{
					float a = k / (float)Ring * MathF.PI * 2f;
					dst.Add( new Vector3( MathF.Cos( a ) * sx, MathF.Sin( a ) * sx, 0f ) );
				}
				// Sliced cone = frustum: the apex becomes a ring on the flat cut, so the collider is flat-topped
				// like the rendered shape. Unsliced keeps the single apex point.
				float coneSlice = b.SlicePlaneN;
				if ( coneSlice < 0.999f )
				{
					float zc = sz * coneSlice + sz, rc = sx * (1f - coneSlice) * 0.5f;
					for ( int k = 0; k < Ring; k++ )
					{
						float a = k / (float)Ring * MathF.PI * 2f;
						dst.Add( new Vector3( MathF.Cos( a ) * rc, MathF.Sin( a ) * rc, zc ) );
					}
				}
				else
					dst.Add( new Vector3( 0f, 0f, 2f * sz ) );
				break;
			}

			case SdfShape.Extruded: // 2D profile extruded along Z; exact for the convex profiles (triangle,
			{                       // hexagon) — the concave star is decomposed in Build/StarHulls (here its
				// outline just anchors footprint probes to real corners).
				var outline = new List<Vector2>( 10 );
				SdfBrush.CrossSectionOutline( b.CrossSection, new Vector3( sx, sy, sz ), outline );
				foreach ( var v in outline )
				{
					dst.Add( new Vector3( v.x, v.y, sz ) );
					dst.Add( new Vector3( v.x, v.y, -sz ) );
				}
				break;
			}

			default: // Sphere/ellipsoid (uniform sliced, or non-uniform) — lat/long point cloud, clipped to the slice
			{
				float sliceN = b.SlicePlaneN;
				bool cut = sliceN < 0.999f;

				if ( !cut )
					dst.Add( new Vector3( 0f, 0f, sz ) ); // +Z pole only survives unsliced
				dst.Add( new Vector3( 0f, 0f, -sz ) );    // -Z pole

				if ( cut )
				{
					// Ring on the cut circle so the hull's flat top sits exactly on the slice plane.
					float cr = MathF.Sqrt( MathF.Max( 1f - sliceN * sliceN, 0f ) );
					for ( int k = 0; k < Ring; k++ )
					{
						float a = k / (float)Ring * MathF.PI * 2f;
						dst.Add( new Vector3( MathF.Cos( a ) * cr * sx, MathF.Sin( a ) * cr * sy, sliceN * sz ) );
					}
				}

				for ( int j = 1; j <= EllipsoidStacks; j++ )
				{
					float phi = j / (float)(EllipsoidStacks + 1) * MathF.PI; // (0, PI)
					float sinP = MathF.Sin( phi ), cosP = MathF.Cos( phi );
					if ( cut && cosP > sliceN )
						continue; // ring is above the cut — sliced away
					for ( int k = 0; k < Ring; k++ )
					{
						float a = k / (float)Ring * MathF.PI * 2f;
						dst.Add( new Vector3( MathF.Cos( a ) * sinP * sx, MathF.Sin( a ) * sinP * sy, cosP * sz ) );
					}
				}
				break;
			}
		}
	}

	/// <summary>Snapshot the shape's "footprint" — sculpture-local points covering the UNDERSIDE of every additive
	/// brush, for multi-point ground probing. Per brush we (1) add a probe at each of its lowest vertices (a box's
	/// coplanar bottom corners, a rotated box's single low corner, a sphere's bottom point) and (2) tile its
	/// footprint with cells of ~<paramref name="spacing"/> (min 2×2), snapping each cell centre toward a nearby
	/// vertex and merging by XY so corners aren't probed twice. The top is discarded (we stop at the lowest
	/// surface), and EVERY brush contributes — a raised/floating one just gets probes up high that never reach
	/// ground unless it's the lowest thing. Pure computation, no engine state — and the caller guards it so it can
	/// never affect the collider. Returned in sculpture-local space (transform by the sculpture's world transform).
	///
	/// Pass the <paramref name="carvedCopies"/> list <see cref="Build"/> filled to keep the probes in lockstep
	/// with a carved collider: every voxel box contributes its lowest corners as guaranteed contact probes, and
	/// probes falling inside a voxelised copy's footprint snap onto the boxes' underside (or drop, if their
	/// column was hollowed clean through) instead of using the smooth field the boxes only approximate.
	///
	/// "Underside" means WORLD-down: a sculpture standing tilted (a scene prop placed on its side, whose tilt
	/// conversion moves onto the disguise child) passes <paramref name="probeFrame"/> — the rotation taking
	/// sculpture-local space to a frame whose −Z is world down — and the whole computation is conjugated through
	/// it: geometry is rotated in, columns march that frame's Z, and the probes are rotated back, so they land
	/// on the face actually resting on the floor. Null (upright) skips the conjugation entirely.</summary>
	public static List<Vector3> ComputeFootPoints( List<SdfBrush> brushes, float spacing = 12f,
		List<CarvedCopy> carvedCopies = null, Rotation? probeFrame = null )
	{
		var pts = new List<Vector3>();
		if ( brushes is null || brushes.Count == 0 )
			return pts;

		brushes = SdfGroups.Flatten( brushes ); // same expansion as Build — keeps CarvedCopy indices aligned

		// Conjugation frame (identity when upright): q takes sculpture-local points INTO the down-aligned probe
		// frame, qi brings probes back out; downLocal is where the round shapes' "bottom point" really is.
		bool tilted = probeFrame.HasValue;
		var q = probeFrame ?? Rotation.Identity;
		var qi = q.Inverse;
		var downLocal = qi * Vector3.Down;

		spacing = MathF.Max( spacing, 2f );
		const int maxPerAxis = 10;            // bound the probe count for a huge brush (spacing just gets coarser)
		float snapRadius = spacing * 0.75f;   // pull a cell centre onto a vertex if it's within this
		float mergeSqr = MathF.Pow( spacing * 0.5f, 2f ); // probes closer than this (XY) are the same column

		var verts = new List<Vector3>( 32 );
		var tmp = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 ); // reused swept-sphere buffer (spline brushes)
		var carvers = new List<SdfBrush>( 4 );                      // subtracts that can hollow the current brush
		var carverBounds = new List<(Vector3 lo, Vector3 hi)>( 4 ); // scratch for CollectCarvers' overlap filter
		var copyEntries = new List<CarvedCopy>( 4 );                // this brush's voxelised copies, if any
		var copyRects = new List<(Vector2 lo, Vector2 hi)>( 4 );    // their sculpture-space XY footprints
		var cornerProbes = new List<Vector3>( 32 );                 // voxel-box contact corners, gathered per brush
		Span<Vector3> centres = stackalloc Vector3[8];
		float minExtent = EffectiveMinExtent( brushes ); // the SAME gate Build applied — see EffectiveMinExtent

		int start = 0; // dedup only within the current brush (overlapping brushes may legitimately both probe)
		void Add( Vector3 p )
		{
			for ( int i = start; i < pts.Count; i++ )
			{
				float dx = pts[i].x - p.x, dy = pts[i].y - p.y;
				if ( dx * dx + dy * dy < mergeSqr )
					return; // merge — don't probe the same column twice
			}
			pts.Add( p );
		}

		for ( int bi = 0; bi < brushes.Count; bi++ )
		{
			var b = brushes[bi];
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue;
			if ( TooSmall( b, minExtent, tmp, sweep ) )
				continue; // Build emitted no collider here — probing it would hover the body on nothing

			BrushVertices( b, verts, tmp, sweep, centres, downLocal );
			if ( verts.Count == 0 )
				continue;
			if ( tilted )
				for ( int i = 0; i < verts.Count; i++ )
					verts[i] = q * verts[i];

			// Subtracts that can hollow this brush (same list-order rule as the collider). The probes must read
			// carved space as EMPTY: a chopped-off bottom otherwise leaves phantom probes floating below the real
			// collider — the body hovers on them mid-air, then buries them under the floor and reads stuck.
			// Splines are carve-aware too — their carved copies are recorded like any other's, with the KEPT
			// capsule samples included, so probes ride the emitted capsules+boxes. Text is exempt,
			// matching its uncarved ink-box hull.
			carvers.Clear();
			if ( b.Shape != SdfShape.Text )
				CollectCarvers( brushes, bi, centres, sweep, carvers, carverBounds );

			// This brush's voxelised copies as Build actually emitted them, plus their XY footprints — probes
			// in those regions must ride the boxes, not the smooth field the boxes only approximate.
			copyEntries.Clear();
			copyRects.Clear();
			if ( carvedCopies is not null )
			{
				foreach ( var e in carvedCopies )
				{
					if ( e.BrushIndex != bi )
						continue;
					copyEntries.Add( e );
					copyRects.Add( EntryRectXY( e, q ) );
				}
			}

			// Brush bounds over every copy — the grid's span, and the carved vertex probes' march range.
			Vector3 lo, hi;
			if ( b.Shape == SdfShape.Spline )
			{
				// Spline: Position/Size (hence AabbExtents/MirrorCentres) mean nothing — span the grid over
				// the swept-sphere bottoms instead (verts already includes every mirror copy).
				lo = new Vector3( float.MaxValue );
				hi = new Vector3( float.MinValue );
				foreach ( var v in verts )
				{
					lo = Vector3.Min( lo, v );
					hi = Vector3.Max( hi, v );
				}

				// Bottoms only bound the UNDERSIDE; a carved-away bottom re-derives higher (anywhere up to the
				// tube top), so raise the march ceiling by the tube diameter or those probes get dropped.
				if ( carvers.Count > 0 )
				{
					float maxR = 0f;
					foreach ( var pt in sweep )
						maxR = MathF.Max( maxR, pt.w );
					hi = hi.WithZ( hi.z + 2f * maxR );
				}
			}
			else if ( tilted )
			{
				// The analytic AABBs live in sculpture axes and mean nothing in the probe frame — span the
				// (already rotated) hull points instead, padded because the round shapes' coarse point clouds sit
				// up to ~8% inside the true surface (the pad only widens the grid/march range; always safe).
				lo = new Vector3( float.MaxValue );
				hi = new Vector3( float.MinValue );
				foreach ( var v in verts )
				{
					lo = Vector3.Min( lo, v );
					hi = Vector3.Max( hi, v );
				}

				var pad = new Vector3( 1f + 0.08f * (hi - lo).Length * 0.5f );
				lo -= pad;
				hi += pad;
			}
			else
			{
				var ext = b.AabbExtents( b.Rotation );
				int n = b.MirrorCentres( centres );
				lo = new Vector3( float.MaxValue );
				hi = new Vector3( float.MinValue );
				for ( int i = 0; i < n; i++ )
				{
					lo = Vector3.Min( lo, centres[i] - ext );
					hi = Vector3.Max( hi, centres[i] + ext );
				}
			}

			start = pts.Count;

			// 0. Every voxel box's lowest corners — the carved collider's true contact points (the voxel
			//    counterpart of the lowest-vertex pass below). Added first and sorted lowest-first, so the
			//    real contacts win the XY merge against the higher coverage probes that follow.
			if ( copyEntries.Count > 0 )
			{
				cornerProbes.Clear();
				foreach ( var e in copyEntries )
					foreach ( var box in e.Boxes )
						LowestBoxCorners( e, box, cornerProbes, q );
				cornerProbes.Sort( ( u, v ) => u.z.CompareTo( v.z ) );
				foreach ( var p in cornerProbes )
					Add( p );
			}

			// 1. The lowest vertices — guaranteed probes right on the lowest contact points. Under carvers a
			//    lowest vertex may no longer exist: re-derive its z by marching the carved field up its column
			//    (a chopped sphere's bottom pole becomes a probe on the flat cut), or drop it if the column is
			//    hollowed through. Then synced onto the voxel boxes where a copy went voxel.
			float minz = float.MaxValue;
			foreach ( var v in verts )
				minz = MathF.Min( minz, v.z );
			foreach ( var v in verts )
			{
				if ( v.z > minz + 0.5f )
					continue;
				if ( carvers.Count == 0 )
					Add( v );
				else if ( TryFindUnderside( b, carvers, qi, v.x, v.y, lo.z, hi.z, out float vz ) )
				{
					var probe = new Vector3( v.x, v.y, vz );
					if ( TrySyncToVoxels( copyEntries, copyRects, ref probe, qi ) )
						Add( probe );
				}
			}

			// 2. Even grid coverage of the underside; each cell centre snaps toward a nearby vertex (so it lands
			//    on a corner, not beside it) and merges into existing probes, so corners aren't probed twice.

			int nx = Math.Clamp( (int)MathF.Round( (hi.x - lo.x) / spacing ), 2, maxPerAxis );
			int ny = Math.Clamp( (int)MathF.Round( (hi.y - lo.y) / spacing ), 2, maxPerAxis );
			float cw = (hi.x - lo.x) / nx, ch = (hi.y - lo.y) / ny;

			for ( int iy = 0; iy < ny; iy++ )
			for ( int ix = 0; ix < nx; ix++ )
			{
				float x = lo.x + (ix + 0.5f) * cw;
				float y = lo.y + (iy + 0.5f) * ch;
				SnapToVertex( ref x, ref y, verts, snapRadius );
				if ( TryFindUnderside( b, carvers, qi, x, y, lo.z, hi.z, out float z ) )
				{
					var probe = new Vector3( x, y, z );
					if ( TrySyncToVoxels( copyEntries, copyRects, ref probe, qi ) )
						Add( probe );
				}
			}
		}

		// Probes were placed in the down-aligned frame — bring them back to sculpture-local, the space the
		// consumer transforms to world by the sculpture's transform.
		if ( tilted )
			for ( int i = 0; i < pts.Count; i++ )
				pts[i] = qi * pts[i];

		return pts;
	}

	// March up the column (x,y) from just below the brush to the first (lowest) surface — the underside — using
	// the brush's own field (rotation + mirror symmetry already folded in) MINUS its carvers, so carved space
	// reads EMPTY and a chopped-off bottom yields the new flat underside, not the vanished one. Columns the
	// carved brush doesn't cover reach the top without crossing and return false; top surfaces are never
	// reached, so they're discarded. The column lives in the probe frame; toSculpture (identity when upright)
	// maps each sample back for evaluation — SDF distances are rotation-invariant, so the stepping is unchanged.
	static bool TryFindUnderside( SdfBrush b, List<SdfBrush> carvers, Rotation toSculpture, float x, float y, float zmin, float zmax, out float z )
	{
		z = 0f;
		float cz = zmin - 1f;
		for ( int i = 0; i < 64; i++ )
		{
			var p = toSculpture * new Vector3( x, y, cz );
			float d = b.Distance( p );
			foreach ( var s in carvers )
				d = MathF.Max( d, -s.Distance( p ) ); // hard-subtract fold, same shape the collider voxelises

			if ( d <= 0.25f ) { z = cz; return true; } // crossed into the shape — this is its underside
			cz += MathF.Max( d, 0.5f );                 // sphere-step up toward the surface
			if ( cz > zmax + 1f )
				return false;
		}
		return false;
	}

	// Probe-frame XY bounds of one voxelised copy's boxes — the region whose probes must ride the emitted
	// collider. Spheres don't extend the rect: probes over a kept sphere OUTSIDE it keep their marched z (the
	// pre-carve behaviour), while ones inside it join the underside min alongside the boxes.
	static (Vector2 lo, Vector2 hi) EntryRectXY( CarvedCopy e, Rotation toProbe )
	{
		float lox = float.MaxValue, loy = float.MaxValue, hix = float.MinValue, hiy = float.MinValue;
		foreach ( var (blo, bhi) in e.Boxes )
		{
			for ( int c = 0; c < 8; c++ )
			{
				var corner = new Vector3(
					(c & 1) != 0 ? bhi.x : blo.x,
					(c & 2) != 0 ? bhi.y : blo.y,
					(c & 4) != 0 ? bhi.z : blo.z );
				var w = toProbe * ((e.Position + e.Rotation * corner) * e.Sign);
				lox = MathF.Min( lox, w.x );
				loy = MathF.Min( loy, w.y );
				hix = MathF.Max( hix, w.x );
				hiy = MathF.Max( hiy, w.y );
			}
		}
		return (new Vector2( lox, loy ), new Vector2( hix, hiy ));
	}

	// A probe inside a voxelised copy's footprint must sit ON the boxes, not on the smooth field they
	// approximate (they disagree by up to a voxel — enough to hover the body or bury a probe): snap its z to
	// the boxes' underside along its column, or drop it (false) when the column is hollowed clean through.
	// Probes outside every footprint pass through untouched.
	static bool TrySyncToVoxels( List<CarvedCopy> entries, List<(Vector2 lo, Vector2 hi)> rects, ref Vector3 p, Rotation toSculpture )
	{
		if ( entries.Count == 0 )
			return true;

		bool inside = false;
		for ( int i = 0; i < rects.Count && !inside; i++ )
			inside = p.x >= rects[i].lo.x && p.x <= rects[i].hi.x && p.y >= rects[i].lo.y && p.y <= rects[i].hi.y;
		if ( !inside )
			return true;

		float best = float.MaxValue;
		foreach ( var e in entries )
			if ( TryCopyUnderside( e, p.x, p.y, out float z, toSculpture ) )
				best = MathF.Min( best, z );

		if ( best == float.MaxValue )
			return false;
		p.z = best;
		return true;
	}

	// Lowest probe-frame z where the frame's vertical column (x,y) meets the copy's emitted collider — its boxes
	// AND (spline copies) its kept spheres, so the underside is the FULL collider's, not just the voxels'.
	// The column is expressed in sculpture space first (toSculpture, identity when upright), then mapped to the
	// entry frame: w = (Position + R·lp)·sign  ⇒  lp = R⁻¹(w·sign − Position) (sign is componentwise and its
	// own inverse), so the column becomes o + t·d below, with the probe-frame z as the ray parameter.
	static bool TryCopyUnderside( CarvedCopy e, float x, float y, out float z, Rotation toSculpture )
	{
		z = float.MaxValue;

		// The column in sculpture space: origin at probe-frame z=0, direction = the frame's up.
		var colO = toSculpture * new Vector3( x, y, 0f );
		var colD = toSculpture * Vector3.Up;

		// Kept swept spheres (sculpture space, sign already folded): standard ray–sphere, keeping the LOWER
		// root — the underside. On an upright frame this reduces to centre.z − √(r² − dxy²).
		foreach ( var s in e.Spheres )
		{
			var m = colO - new Vector3( s.x, s.y, s.z );
			float half = Vector3.Dot( m, colD );
			float disc = half * half - (m.LengthSquared - s.w * s.w);
			if ( disc >= 0f )
				z = MathF.Min( z, -half - MathF.Sqrt( disc ) );
		}

		var inv = e.Rotation.Inverse;
		var o = inv * (colO * e.Sign - e.Position);
		var d = inv * (colD * e.Sign);

		foreach ( var (blo, bhi) in e.Boxes )
		{
			float t0 = float.MinValue, t1 = float.MaxValue;
			bool miss = false;
			for ( int a = 0; a < 3 && !miss; a++ )
			{
				float oa = a == 0 ? o.x : a == 1 ? o.y : o.z;
				float da = a == 0 ? d.x : a == 1 ? d.y : d.z;
				float la = a == 0 ? blo.x : a == 1 ? blo.y : blo.z;
				float ha = a == 0 ? bhi.x : a == 1 ? bhi.y : bhi.z;

				if ( MathF.Abs( da ) < 1e-6f )
				{
					miss = oa < la || oa > ha; // column parallel to this slab — inside it or not at all
					continue;
				}

				float ta = (la - oa) / da, tb = (ha - oa) / da;
				if ( ta > tb ) (ta, tb) = (tb, ta);
				t0 = MathF.Max( t0, ta );
				t1 = MathF.Min( t1, tb );
			}
			if ( !miss && t0 <= t1 )
				z = MathF.Min( z, t0 );
		}
		return z < float.MaxValue;
	}

	// The four lowest probe-frame corners of one voxel box — its resting contact candidates (for the
	// common unrotated brush on an upright frame that's exactly the bottom face).
	static void LowestBoxCorners( CarvedCopy e, (Vector3 lo, Vector3 hi) box, List<Vector3> dst, Rotation toProbe )
	{
		Span<Vector3> c = stackalloc Vector3[8];
		for ( int i = 0; i < 8; i++ )
		{
			var corner = new Vector3(
				(i & 1) != 0 ? box.hi.x : box.lo.x,
				(i & 2) != 0 ? box.hi.y : box.lo.y,
				(i & 4) != 0 ? box.hi.z : box.lo.z );
			c[i] = toProbe * ((e.Position + e.Rotation * corner) * e.Sign);
		}

		// Insertion-sort the 8 by z, keep the lowest 4.
		for ( int i = 1; i < 8; i++ )
		{
			var v = c[i];
			int j = i - 1;
			while ( j >= 0 && c[j].z > v.z ) { c[j + 1] = c[j]; j--; }
			c[j + 1] = v;
		}
		for ( int i = 0; i < 4; i++ )
			dst.Add( c[i] );
	}

	// All of a brush's collider vertices in sculpture-local space (hull points + mirror copies; for a uniform
	// sphere just its bottom point). Used to anchor probes to real corners and to find the lowest contact points.
	// "Bottom" for the round shapes is along `down` — sculpture-local world-down, so a tilted sculpture's sphere
	// contact points sit where the sphere actually meets the floor (plain −Z when upright).
	static void BrushVertices( SdfBrush b, List<Vector3> dst, List<Vector3> tmp, List<Vector4> sweep, Span<Vector3> centres, Vector3 down )
	{
		dst.Clear();

		if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) )
		{
			float r = MathF.Max( b.Size.x, 0.5f );
			int n = b.MirrorCentres( centres );
			for ( int i = 0; i < n; i++ )
				dst.Add( centres[i] + down * r );
			return;
		}

		// Spline: the bottom point of every sample sphere down the collision capsules, per mirror copy — its
		// ground-contact candidates (the generic hull path below reads Position/Size, which mean nothing for a
		// spline).
		if ( b.Shape == SdfShape.Spline )
		{
			var capsules = new List<SplineCapsule>( 16 );
			FitSplineCapsules( b, sweep, capsules );
			sweep.Clear();
			foreach ( var cap in capsules )
				CapsuleSpheres( cap, sweep );
			int mx = b.EffectiveMirrorX ? 1 : 0, my = b.EffectiveMirrorY ? 1 : 0, mz = b.EffectiveMirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= mx; sx++ )
			for ( int sy = 0; sy <= my; sy++ )
			for ( int sz = 0; sz <= mz; sz++ )
			{
				var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				foreach ( var pt in sweep )
					dst.Add( new Vector3( pt.x, pt.y, pt.z ) * sign + down * MathF.Max( pt.w, 0.5f ) );
			}
			return;
		}

		LocalPoints( b, tmp );
		int nx = b.EffectiveMirrorX ? 1 : 0, ny = b.EffectiveMirrorY ? 1 : 0, nz = b.EffectiveMirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
		for ( int sy = 0; sy <= ny; sy++ )
		for ( int sz = 0; sz <= nz; sz++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
			foreach ( var lp in tmp )
				dst.Add( (b.Position + b.Rotation * lp) * sign );
		}
	}

	// Pull (x,y) onto the nearest vertex within radius (XY only), so a grid probe lands exactly on a corner.
	static void SnapToVertex( ref float x, ref float y, List<Vector3> verts, float radius )
	{
		float best = radius * radius, rx = x, ry = y;
		foreach ( var v in verts )
		{
			float dx = v.x - x, dy = v.y - y, d2 = dx * dx + dy * dy;
			if ( d2 < best ) { best = d2; rx = v.x; ry = v.y; }
		}
		x = rx; y = ry;
	}
}
