using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Builds a CHEAP collision <see cref="Model"/> from a brush list — one convex shape per additive primitive,
/// no field sampling, no surface-nets meshing. Each brush becomes a sphere (round) or a small convex hull
/// (box/cylinder/cone/prism/ellipsoid); subtractive brushes are ignored (a convex collider can't carve a
/// hole, and approximating the disguise as its solid additive parts is exactly what we want). Mirror-symmetry
/// copies are included so a symmetric sculpt is solid on both sides.
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

	/// <summary>Build a collision-only Model (no render mesh) from the additive brushes. MAIN THREAD — creates
	/// physics shapes. Returns null when there's nothing solid to collide with.</summary>
	public static Model Build( List<SdfBrush> brushes )
	{
		if ( brushes is null || brushes.Count == 0 )
			return null;

		var builder = new ModelBuilder();
		bool any = false;
		Span<Vector3> centres = stackalloc Vector3[8];
		var pts = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 ); // reused swept-sphere buffer (spline brushes)

		foreach ( var b in brushes )
		{
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue; // hidden or subtractive — contributes no solid volume

			// A uniform sphere is the one shape with an exact, cheaper-than-a-hull primitive collider, and it
			// needs no orientation — so just place one per mirror centre. A SLICED sphere isn't a sphere any
			// more (the flat cut must collide flat), so it falls through to the clipped hull below.
			if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) && b.Slice <= 0f )
			{
				int n = b.MirrorCentres( centres );
				for ( int i = 0; i < n; i++ )
				{
					builder.AddCollisionSphere( MathF.Max( b.Size.x, 0.5f ), centres[i] );
					any = true;
				}
				continue;
			}

			// A spline: spheres SWEPT along the drawn curve (curve-aware, radius-interpolated), at every mirror
			// copy. Spheres are the natural fit for the variable-radius tube and need no orientation; sweeping
			// (rather than one sphere per control point) keeps the tube solid however sparse the control points
			// are. SplineSweepSpacing spaces them at one local radius — overlapping (gap-free) but coarser than
			// the 0.6 the hover ghost uses, so long splines don't flood the physics scene with shapes.
			if ( b.Shape == SdfShape.Spline )
			{
				b.BuildSplineSweep( sweep, SplineSweepSpacing );
				int snx = b.MirrorX ? 1 : 0, sny = b.MirrorY ? 1 : 0, snz = b.MirrorZ ? 1 : 0;
				for ( int sx = 0; sx <= snx; sx++ )
				for ( int sy = 0; sy <= sny; sy++ )
				for ( int sz = 0; sz <= snz; sz++ )
				{
					var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
					foreach ( var pt in sweep )
					{
						var c = new Vector3( pt.x, pt.y, pt.z ) * sign;
						builder.AddCollisionSphere( MathF.Max( pt.w, 0.5f ), c );
						any = true;
					}
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

			int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= nx; sx++ )
			for ( int sy = 0; sy <= ny; sy++ )
			for ( int sz = 0; sz <= nz; sz++ )
			{
				var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				var hull = new List<Vector3>( pts.Count );
				foreach ( var lp in pts )
					hull.Add( (b.Position + b.Rotation * lp) * sign );

				builder.AddCollisionHull( hull );
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
		int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
		for ( int sy = 0; sy <= ny; sy++ )
		for ( int szn = 0; szn <= nz; szn++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, szn == 1 ? -1f : 1f );
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
				any = true;
			}
		}
		return any;
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
	static void LocalPoints( SdfBrush b, List<Vector3> dst )
	{
		dst.Clear();
		var s = b.Size;
		float sx = MathF.Max( MathF.Abs( s.x ), 0.5f );
		float sy = MathF.Max( MathF.Abs( s.y ), 0.5f );
		float sz = MathF.Max( MathF.Abs( s.z ), 0.5f );

		switch ( b.Shape )
		{
			case SdfShape.Box:
			case SdfShape.Text: // text collides as its quad — coarse, like every other collider approximation
				for ( int i = 0; i < 8; i++ )
					dst.Add( new Vector3( (i & 1) != 0 ? sx : -sx, (i & 2) != 0 ? sy : -sy, (i & 4) != 0 ? sz : -sz ) );
				break;

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
	/// never affect the collider. Returned in sculpture-local space (transform by the sculpture's world transform).</summary>
	public static List<Vector3> ComputeFootPoints( List<SdfBrush> brushes, float spacing = 12f )
	{
		var pts = new List<Vector3>();
		if ( brushes is null || brushes.Count == 0 )
			return pts;

		spacing = MathF.Max( spacing, 2f );
		const int maxPerAxis = 10;            // bound the probe count for a huge brush (spacing just gets coarser)
		float snapRadius = spacing * 0.75f;   // pull a cell centre onto a vertex if it's within this
		float mergeSqr = MathF.Pow( spacing * 0.5f, 2f ); // probes closer than this (XY) are the same column

		var verts = new List<Vector3>( 32 );
		var tmp = new List<Vector3>( 32 );
		var sweep = new List<Vector4>( 64 ); // reused swept-sphere buffer (spline brushes)
		Span<Vector3> centres = stackalloc Vector3[8];

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

		foreach ( var b in brushes )
		{
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue;

			BrushVertices( b, verts, tmp, sweep, centres );
			if ( verts.Count == 0 )
				continue;

			start = pts.Count;

			// 1. The lowest vertices — guaranteed probes right on the lowest contact points.
			float minz = float.MaxValue;
			foreach ( var v in verts )
				minz = MathF.Min( minz, v.z );
			foreach ( var v in verts )
				if ( v.z <= minz + 0.5f )
					Add( v );

			// 2. Even grid coverage of the underside; each cell centre snaps toward a nearby vertex (so it lands
			//    on a corner, not beside it) and merges into existing probes, so corners aren't probed twice.
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

			int nx = Math.Clamp( (int)MathF.Round( (hi.x - lo.x) / spacing ), 2, maxPerAxis );
			int ny = Math.Clamp( (int)MathF.Round( (hi.y - lo.y) / spacing ), 2, maxPerAxis );
			float cw = (hi.x - lo.x) / nx, ch = (hi.y - lo.y) / ny;

			for ( int iy = 0; iy < ny; iy++ )
			for ( int ix = 0; ix < nx; ix++ )
			{
				float x = lo.x + (ix + 0.5f) * cw;
				float y = lo.y + (iy + 0.5f) * ch;
				SnapToVertex( ref x, ref y, verts, snapRadius );
				if ( TryFindUnderside( b, x, y, lo.z, hi.z, out float z ) )
					Add( new Vector3( x, y, z ) );
			}
		}

		return pts;
	}

	// March up the column (x,y) from just below the brush to the first (lowest) surface — the underside — using
	// the brush's own field (rotation + mirror symmetry already folded in). Columns the brush doesn't cover reach
	// the top without crossing and return false; top surfaces are never reached, so they're discarded.
	static bool TryFindUnderside( SdfBrush b, float x, float y, float zmin, float zmax, out float z )
	{
		z = 0f;
		float cz = zmin - 1f;
		for ( int i = 0; i < 64; i++ )
		{
			float d = b.Distance( new Vector3( x, y, cz ) );
			if ( d <= 0.25f ) { z = cz; return true; } // crossed into the shape — this is its underside
			cz += MathF.Max( d, 0.5f );                 // sphere-step up toward the surface
			if ( cz > zmax + 1f )
				return false;
		}
		return false;
	}

	// All of a brush's collider vertices in sculpture-local space (hull points + mirror copies; for a uniform
	// sphere just its bottom point). Used to anchor probes to real corners and to find the lowest contact points.
	static void BrushVertices( SdfBrush b, List<Vector3> dst, List<Vector3> tmp, List<Vector4> sweep, Span<Vector3> centres )
	{
		dst.Clear();

		if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) )
		{
			float r = MathF.Max( b.Size.x, 0.5f );
			int n = b.MirrorCentres( centres );
			for ( int i = 0; i < n; i++ )
				dst.Add( centres[i] - Vector3.Up * r );
			return;
		}

		// Spline: the bottom point of every swept collision sphere, per mirror copy — its ground-contact
		// candidates (the generic hull path below reads Position/Size, which mean nothing for a spline).
		if ( b.Shape == SdfShape.Spline )
		{
			b.BuildSplineSweep( sweep, SplineSweepSpacing );
			int mx = b.MirrorX ? 1 : 0, my = b.MirrorY ? 1 : 0, mz = b.MirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= mx; sx++ )
			for ( int sy = 0; sy <= my; sy++ )
			for ( int sz = 0; sz <= mz; sz++ )
			{
				var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				foreach ( var pt in sweep )
					dst.Add( new Vector3( pt.x, pt.y, pt.z ) * sign - Vector3.Up * MathF.Max( pt.w, 0.5f ) );
			}
			return;
		}

		LocalPoints( b, tmp );
		int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
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
