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
/// Splines carve PER SWEPT SPHERE — the sweep is already a decomposition, so a sphere a subtract fully
/// swallows is dropped, one it only grazes stays an exact sphere, and one it genuinely bites is voxelised on
/// its own small sculpture-aligned grid. Only the spheres that need it pay; a long spline never voxelises as
/// a whole. TEXT is the one exemption: glyph strokes are thinner than a voxel, so a carved voxelisation would
/// dissolve the letters — a text brush keeps its ink-box hull, and the foot probes skip its carvers the same way.
///
/// Cheap enough to rebuild synchronously on the main thread whenever the shape is committed (e.g. on gizmo
/// release) rather than every frame. Pair with a <see cref="ModelCollider"/> that references the result.
/// </summary>
public static class SdfCollisionBuilder
{
	// Tessellation of the round primitives' hull point clouds. Collision only needs a coarse convex
	// approximation, so these are far lower than the render mesher's.
	const int Ring = 12;   // segments around a cylinder/cone
	const int EllipsoidStacks = 3; // horizontal rings between the poles
	const float SplineSweepSpacing = 1.0f; // swept-sphere step, in local radii (hover ghost uses 0.6)

	// Carve-aware path (see BuildCarved). All biases are fractions of the smallest voxel edge.
	const float CarveGateFraction = 0.005f; // swap to voxels only when subtracts remove at least this fraction.
	                                        // Deliberately near zero — just a guard against a lone noisy sample.
	                                        // A kept-exact copy still collides with its carved-off volume (a
	                                        // flattened sphere rests on the missing pole and visibly hovers), so
	                                        // any carve that registers at all should swap; the real "too shallow
	                                        // to matter" filter is CarveBias, not this.
	const float VoxelTarget = 5f;          // target voxel edge (sculpture units)
	const int VoxelMaxPerAxis = 14;        // grid cap — 14³ ≈ 2.7k field samples per copy, commit-time only
	const float SolidBias = 0.25f;         // a cell centre must be this deep INSIDE the brush to count as solid
	const float CarveBias = 0.35f;         // a subtract must reach this deep to empty a cell — biased SOLID so a
	                                       // wall thinner than a voxel survives (the cavity shrinks a hair
	                                       // instead of the wall dissolving, which would be far worse)
	const float SplineCarveGraze = 0.25f;  // fraction of a swept sphere's radius a subtract may bite before the
	                                       // sphere trades its exact primitive for voxels
	const int SplineSphereCells = 5;       // per-axis grid for ONE voxelised swept sphere (cell = 2r/5)

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

	// Optional capture of everything Build emits, as sculpture-space points (hull vertices, voxel-box corners,
	// points on each collision sphere). Every emitted shape passes through builder.AddCollisionHull /
	// AddCollisionSphere, so recording at those call sites frames the EXACT collider — carve decisions and all
	// — with no second pass. A static rather than a parameter threaded through every helper: Build is
	// main-thread-only (it creates physics shapes) and the field lives only for the duration of one call.
	static List<Vector3> _frame;

	static void FrameHull( List<Vector3> hull ) => _frame?.AddRange( hull );

	static void FrameSphere( Vector3 c, float r )
	{
		if ( _frame is null )
			return;

		// 6 axis extremes + 8 diagonal points, all ON the sphere — enough that its projected rect hugs the
		// silhouette from any angle (worst-case slack ~8% of r, vs 41% for its bounding box's corners).
		_frame.Add( c + new Vector3( r, 0f, 0f ) );
		_frame.Add( c - new Vector3( r, 0f, 0f ) );
		_frame.Add( c + new Vector3( 0f, r, 0f ) );
		_frame.Add( c - new Vector3( 0f, r, 0f ) );
		_frame.Add( c + new Vector3( 0f, 0f, r ) );
		_frame.Add( c - new Vector3( 0f, 0f, r ) );

		float d = r * 0.577350269f; // r / √3 — the on-sphere diagonal
		for ( int i = 0; i < 8; i++ )
			_frame.Add( c + new Vector3( (i & 1) != 0 ? d : -d, (i & 2) != 0 ? d : -d, (i & 4) != 0 ? d : -d ) );
	}

	/// <summary>Build a collision-only Model (no render mesh) from the additive brushes, carving the ones that
	/// later subtractive brushes significantly hollow. MAIN THREAD — creates physics shapes. Returns null when
	/// there's nothing solid to collide with. Pass <paramref name="carvedCopies"/> to receive the voxelised
	/// copies' boxes — hand the same list to <see cref="ComputeFootPoints"/> so the ground probes sit exactly
	/// on the emitted collider. Pass <paramref name="framePoints"/> to receive sculpture-space points outlining
	/// every shape actually emitted — the carve-aware extent, for consumers that must match the VISIBLE clay
	/// (the creative-mode prompt frames the prop on screen with them).</summary>
	public static Model Build( List<SdfBrush> brushes, List<CarvedCopy> carvedCopies = null, List<Vector3> framePoints = null )
	{
		carvedCopies?.Clear();
		framePoints?.Clear();
		if ( brushes is null || brushes.Count == 0 )
			return null;

		_frame = framePoints;
		try
		{
			return BuildInternal( brushes, carvedCopies );
		}
		finally
		{
			_frame = null;
		}
	}

	static Model BuildInternal( List<SdfBrush> brushes, List<CarvedCopy> carvedCopies )
	{
		var builder = new ModelBuilder();
		bool any = false;
		Span<Vector3> centres = stackalloc Vector3[8];
		var pts = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 ); // reused swept-sphere buffer (spline brushes)
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

			// Subtracts that can hollow THIS brush — only ones AFTER it in the list (see the class summary for
			// the ordering rule), pre-filtered by AABB overlap. Text is exempt: glyph strokes are thinner than
			// a voxel, so carving would dissolve the letters — it keeps its ink-box hull no matter what
			// (ComputeFootPoints skips Text's carvers the same way, keeping the probes in sync).
			carvers.Clear();
			if ( b.Shape != SdfShape.Text )
				CollectCarvers( brushes, bi, centres, sweep, carvers, carverBounds );

			// A spline: spheres SWEPT along the drawn curve (curve-aware, radius-interpolated), at every mirror
			// copy. Spheres are the natural fit for the variable-radius tube and need no orientation; sweeping
			// (rather than one sphere per control point) keeps the tube solid however sparse the control points
			// are. SplineSweepSpacing spaces them at one local radius — overlapping (gap-free) but coarser than
			// the 0.6 the hover ghost uses, so long splines don't flood the physics scene with shapes. When
			// subtracts bite, BuildCarvedSpline decides per swept sphere: drop, keep, or voxelise (see summary).
			if ( b.Shape == SdfShape.Spline )
			{
				if ( carvers.Count > 0 )
				{
					any |= BuildCarvedSpline( b, bi, carvers, carverBounds, sweep, builder, carvedCopies );
					continue;
				}

				b.BuildSplineSweep( sweep, SplineSweepSpacing );
				int snx = b.EffectiveMirrorX ? 1 : 0, sny = b.EffectiveMirrorY ? 1 : 0, snz = b.EffectiveMirrorZ ? 1 : 0;
				for ( int sx = 0; sx <= snx; sx++ )
				for ( int sy = 0; sy <= sny; sy++ )
				for ( int sz = 0; sz <= snz; sz++ )
				{
					var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
					foreach ( var pt in sweep )
					{
						var c = new Vector3( pt.x, pt.y, pt.z ) * sign;
						builder.AddCollisionSphere( MathF.Max( pt.w, 0.5f ), c );
						FrameSphere( c, MathF.Max( pt.w, 0.5f ) );
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
					builder.AddCollisionSphere( MathF.Max( b.Size.x, 0.5f ), centres[i] );
					FrameSphere( centres[i], MathF.Max( b.Size.x, 0.5f ) );
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

				builder.AddCollisionHull( hull );
				FrameHull( hull );
				any = true;
			}
		}

		return any ? builder.Create() : null;
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
			builder.AddCollisionHull( hull );
			FrameHull( hull );
			any = true;
		}
		return any;
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
			// the surface like the hull path's sharp primitives) and no carver eats it (CarveBias deep).
			int kept = 0, carved = 0;
			Array.Clear( solid, 0, solid.Length );
			for ( int iz = 0; iz < nz; iz++ )
			for ( int iy = 0; iy < ny; iy++ )
			for ( int ix = 0; ix < nx; ix++ )
			{
				var lp = new Vector3( lo.x + (ix + 0.5f) * cell.x, lo.y + (iy + 0.5f) * cell.y, lo.z + (iz + 0.5f) * cell.z );
				var p = (b.Position + b.Rotation * lp) * sign;
				if ( b.Distance( p ) > solidAt )
					continue; // outside (or in the skin of) the brush — not part of it either way

				bool cut = false;
				foreach ( var s in carvers )
				{
					if ( s.Distance( p ) < carveAt )
					{
						cut = true;
						break;
					}
				}

				if ( cut ) { carved++; continue; }
				solid[(iz * ny + iy) * nx + ix] = true;
				kept++;
			}

			// The gate: a graze keeps the exact convex shape; a real hollow goes voxel. total == 0 means the
			// grid straddled a degenerate brush — the exact shape is at least as good, fall back to it.
			int total = kept + carved;
			if ( total == 0 || carved < total * CarveGateFraction )
			{
				any |= EmitExactCopy( b, sign, pts, builder );
				continue;
			}
			if ( kept == 0 )
				continue; // the subtracts ate the whole copy — nothing solid left

			CarvedCopy record = null;
			if ( carvedCopies is not null )
			{
				record = new CarvedCopy { BrushIndex = index, Sign = sign, Position = b.Position, Rotation = b.Rotation };
				carvedCopies.Add( record );
			}
			any |= GreedyBoxes( b.Position, b.Rotation, sign, lo, cell, solid, nx, ny, nz, builder, record );
		}

		return any;
	}

	// Carve-aware collider for a spline: the sweep is already a decomposition, so carve PER SWEPT SPHERE — a
	// sphere a subtract fully swallows is dropped, one it merely grazes (< SplineCarveGraze of the radius)
	// stays an exact sphere, and one it genuinely bites is voxelised on its own small grid. Only the spheres
	// that need it pay, so a long spline never voxelises as a whole. Judged per mirror copy like everything
	// else (an asymmetric subtract must not hollow the untouched reflection).
	static bool BuildCarvedSpline( SdfBrush b, int index, List<SdfBrush> carvers, List<(Vector3 lo, Vector3 hi)> carverBounds,
		List<Vector4> sweep, ModelBuilder builder, List<CarvedCopy> carvedCopies )
	{
		b.BuildSplineSweep( sweep, SplineSweepSpacing );
		bool any = false;
		var near = new List<SdfBrush>( 4 ); // carvers whose AABB reaches the current sphere

		int mx = b.EffectiveMirrorX ? 1 : 0, my = b.EffectiveMirrorY ? 1 : 0, mz = b.EffectiveMirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= mx; sx++ )
		for ( int sy = 0; sy <= my; sy++ )
		for ( int sz = 0; sz <= mz; sz++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );

			// The copy's record: everything stored sign-folded in sculpture space (frame = identity), kept
			// spheres included so the probe sync sees the copy's FULL collider. Only published if anything
			// actually voxelised — an untouched copy needs no sync.
			var record = new CarvedCopy { BrushIndex = index, Sign = Vector3.One, Position = Vector3.Zero, Rotation = Rotation.Identity };

			foreach ( var pt in sweep )
			{
				float r = MathF.Max( pt.w, 0.5f );
				var c = new Vector3( pt.x, pt.y, pt.z ) * sign;

				// Carvers whose AABB reaches this sphere, and the deepest bite among them at its centre —
				// the nearest carver surface being r away in either direction classifies the sphere outright.
				near.Clear();
				float d = float.MaxValue;
				for ( int ci = 0; ci < carvers.Count; ci++ )
				{
					var (nlo, nhi) = carverBounds[ci];
					if ( c.x + r < nlo.x || c.x - r > nhi.x ||
						 c.y + r < nlo.y || c.y - r > nhi.y ||
						 c.z + r < nlo.z || c.z - r > nhi.z )
						continue;
					near.Add( carvers[ci] );
					d = MathF.Min( d, carvers[ci].Distance( c ) );
				}

				if ( d <= -r )
					continue; // fully inside a subtract — the sphere is deactivated
				if ( d >= r * (1f - SplineCarveGraze) )
				{
					builder.AddCollisionSphere( r, c ); // untouched, or only a sliver lost — keep it exact
					FrameSphere( c, r );
					record.Spheres.Add( new Vector4( c.x, c.y, c.z, r ) );
					any = true;
					continue;
				}

				any |= VoxeliseSphere( c, r, near, builder, record );
			}

			if ( carvedCopies is not null && record.Boxes.Count > 0 )
				carvedCopies.Add( record );
		}
		return any;
	}

	// One partially-carved swept sphere → carved voxel boxes on its own small grid, directly in sculpture
	// space (a spline evaluates there — no brush frame). Cells are 2r/SplineSphereCells, so the carve
	// resolution scales with the tube radius; occupancy biases match the brush voxeliser's.
	static bool VoxeliseSphere( Vector3 centre, float r, List<SdfBrush> carvers, ModelBuilder builder, CarvedCopy record )
	{
		const int n = SplineSphereCells;
		float cell = 2f * r / n;
		float solidAt = -SolidBias * cell;
		float carveAt = -CarveBias * cell;
		var lo = centre - new Vector3( r );

		var solid = new bool[n * n * n];
		int kept = 0;
		for ( int iz = 0; iz < n; iz++ )
		for ( int iy = 0; iy < n; iy++ )
		for ( int ix = 0; ix < n; ix++ )
		{
			var p = lo + new Vector3( (ix + 0.5f) * cell, (iy + 0.5f) * cell, (iz + 0.5f) * cell );
			if ( (p - centre).Length - r > solidAt )
				continue; // outside (or in the skin of) the sphere

			bool cut = false;
			foreach ( var s in carvers )
			{
				if ( s.Distance( p ) < carveAt )
				{
					cut = true;
					break;
				}
			}
			if ( cut )
				continue;

			solid[(iz * n + iy) * n + ix] = true;
			kept++;
		}

		if ( kept == 0 )
			return false; // biased sampling ate it whole — close enough to fully swallowed
		return GreedyBoxes( Vector3.Zero, Rotation.Identity, Vector3.One, lo, new Vector3( cell ), solid, n, n, n, builder, record );
	}

	// One mirror copy's EXACT convex collider — the same shapes the uncarved path builds, emitted for a
	// single reflection (used when only some copies of a brush are actually carved). LocalCentre is zero for
	// every shape but the cone (which has no primitive shortcut), so the plain sphere sits on Position.
	static bool EmitExactCopy( SdfBrush b, Vector3 sign, List<Vector3> pts, ModelBuilder builder )
	{
		if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) && b.Slice <= 0f )
		{
			builder.AddCollisionSphere( MathF.Max( b.Size.x, 0.5f ), b.Position * sign );
			FrameSphere( b.Position * sign, MathF.Max( b.Size.x, 0.5f ) );
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
		builder.AddCollisionHull( hull );
		FrameHull( hull );
		return true;
	}

	// Merge the solid cells into as few boxes as possible (greedy run → rect → slab growth, consuming cells
	// as it goes) and emit each as an 8-corner hull in sculpture space. Merging matters beyond shape count:
	// an unmerged floor is a row of boxes whose internal seams catch anything sliding across them — merged,
	// a bin floor is ONE box.
	static bool GreedyBoxes( Vector3 framePos, Rotation frameRot, Vector3 sign, Vector3 lo, Vector3 cell,
		bool[] solid, int nx, int ny, int nz, ModelBuilder builder, CarvedCopy record )
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

			var hull = new List<Vector3>( 8 );
			for ( int c = 0; c < 8; c++ )
			{
				var corner = new Vector3(
					(c & 1) != 0 ? bhi.x : blo.x,
					(c & 2) != 0 ? bhi.y : blo.y,
					(c & 4) != 0 ? bhi.z : blo.z );
				hull.Add( (framePos + frameRot * corner) * sign );
			}
			builder.AddCollisionHull( hull );
			FrameHull( hull );
			record?.Boxes.Add( (blo, bhi) );
			any = true;
		}
		return any;
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
			// swept spheres included, so probes ride the emitted spheres+boxes exactly. Text is exempt,
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

		// Spline: the bottom point of every swept collision sphere, per mirror copy — its ground-contact
		// candidates (the generic hull path below reads Position/Size, which mean nothing for a spline).
		if ( b.Shape == SdfShape.Spline )
		{
			b.BuildSplineSweep( sweep, SplineSweepSpacing );
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
