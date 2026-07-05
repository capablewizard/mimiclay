using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Builds a real boolean (CSG) mesh from the brush list: each brush's primitive is a convex solid, unioned
/// (additive) or subtracted (subtractive) in order via <see cref="Csg"/>. Because the primitives are simple
/// and convex this is fast and gives clean, flat-shaded geometry — an alternative to the surface-nets remesh.
/// In sculpture-LOCAL space (assigned to a ModelRenderer that applies the GameObject transform).
/// </summary>
public static class SdfCsgMesher
{
	const int Slices = 16, Stacks = 10, Ring = 20; // tessellation of the round primitives
	const int MaxPolygons = 80000;                 // runaway-explosion guard

	/// <summary>Geometry produced on a worker thread (no GPU calls) — fed to <see cref="BuildModel"/> on main.</summary>
	public sealed class CsgGeometry
	{
		public List<Vertex> Verts;
		public List<int> Indices;
		public BBox Bounds;
	}

	/// <summary>Sync convenience: geometry + model in one call (main thread only).</summary>
	public static Model Build( List<SdfBrush> brushes, Material material )
	{
		var geo = BuildGeometry( brushes );
		return geo is null ? null : BuildModel( geo, material );
	}

	/// <summary>WORKER-SAFE: run the CSG and triangulate into CPU vertex/index buffers. No engine/GPU calls,
	/// so it's safe off the main thread. Returns null on empty input or any error (never throws).</summary>
	public static CsgGeometry BuildGeometry( List<SdfBrush> brushes )
	{
		if ( brushes is null )
			return null;

		try
		{
			Csg acc = null;
			foreach ( var b in brushes )
			{
				if ( !b.Enabled )
					continue;

				var solid = BrushSolid( b );
				if ( solid is null || solid.Polygons.Count == 0 )
					continue;

				if ( b.Operation == SdfOperation.Add )
					acc = acc is null ? solid : acc.Union( solid );
				else if ( acc is not null ) // subtract only carves existing material
					acc = acc.Subtract( solid );

				// Runaway guard: degenerate configs can explode the polygon count. Bail (skip this build)
				// rather than chew memory / stall.
				if ( acc is not null && acc.Polygons.Count > MaxPolygons )
					return null;
			}

			if ( acc is null || acc.Polygons.Count == 0 )
				return null;

			return Triangulate( acc );
		}
		catch
		{
			return null; // a degenerate sculpt must never crash the build
		}
	}

	/// <summary>MAIN THREAD: upload worker-built geometry into a Model.</summary>
	public static Model BuildModel( CsgGeometry g, Material material )
	{
		if ( g is null || g.Verts.Count < 3 || g.Indices.Count < 3 )
			return null;

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( g.Verts.Count, g.Verts );
		mesh.CreateIndexBuffer( g.Indices.Count, g.Indices );
		mesh.Bounds = g.Bounds;
		return new ModelBuilder().AddMesh( mesh ).Create();
	}

	// A brush + its mirror copies, unioned into one solid.
	static Csg BrushSolid( SdfBrush b )
	{
		Csg solid = null;
		int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
		for ( int sy = 0; sy <= ny; sy++ )
		for ( int sz = 0; sz <= nz; sz++ )
		{
			var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
			var copy = PrimitiveSolid( b, sign );
			if ( copy is null )
				continue;
			solid = solid is null ? copy : solid.Union( copy );
		}
		return solid;
	}

	// One primitive copy as a CSG solid: build raw polygons, orient them outward (robust to construction
	// winding), transform into sculpture-local space, and flip winding for a reflected (mirror) copy.
	static Csg PrimitiveSolid( SdfBrush b, Vector3 sign )
	{
		var raw = ShapePolys( b );
		if ( raw.Count == 0 )
			return null;

		// A point inside the convex primitive — the average of all its vertices.
		var inside = Vector3.Zero;
		int n = 0;
		foreach ( var poly in raw )
			foreach ( var v in poly )
			{
				inside += v;
				n++;
			}
		inside /= MathF.Max( 1, n );

		bool reflect = sign.x * sign.y * sign.z < 0f;
		var polys = new List<CsgPolygon>( raw.Count );

		foreach ( var lp in raw )
		{
			// Orient outward in local space (normal points away from the inside point).
			var faceN = Vector3.Cross( lp[1] - lp[0], lp[2] - lp[0] );
			var centroid = Centroid( lp );
			bool outward = Vector3.Dot( faceN, centroid - inside ) >= 0f;

			var verts = new List<CsgVertex>( lp.Count );
			for ( int i = 0; i < lp.Count; i++ )
			{
				// Local → sculpture-local (brush transform, then mirror reflection).
				var p = (b.Position + b.Rotation * lp[i]) * sign;
				verts.Add( new CsgVertex( p, Vector3.Up ) );
			}

			// A reflection flips handedness, so reverse to keep faces outward.
			if ( outward == reflect )
				verts.Reverse();

			// CSG requires PLANAR polygons. Box/cap faces are flat, but sphere/ellipsoid quads are curved
			// (non-planar) — used as split planes they corrupt the boolean (holes/slivers). Fan-triangulate
			// every face into triangles, which are always planar. (Clone verts so polygons don't share.)
			for ( int k = 1; k < verts.Count - 1; k++ )
				polys.Add( new CsgPolygon( new List<CsgVertex> { verts[0].Clone(), verts[k].Clone(), verts[k + 1].Clone() } ) );
		}

		return Csg.FromPolygons( polys );
	}

	// ── primitive polygons (brush-local frame, axis = Z; winding is fixed up afterwards) ─────────────

	static List<List<Vector3>> ShapePolys( SdfBrush b )
	{
		// Clamp to a positive minimum extent: a zero (or negative, while scaling through) size makes
		// zero-area faces → NaN planes → the CSG blows up. Abs handles a flipped scale.
		var s = b.Size;
		s = new Vector3( MathF.Max( MathF.Abs( s.x ), 0.05f ), MathF.Max( MathF.Abs( s.y ), 0.05f ), MathF.Max( MathF.Abs( s.z ), 0.05f ) );

		return b.Shape switch
		{
			SdfShape.Box => Box( s ),
			SdfShape.Cylinder => Cylinder( s.x, s.z ),
			SdfShape.Cone => Cone( s.x, s.z ),
			SdfShape.Extruded => Extrusion( b.CrossSection, s ),
			_ => Ellipsoid( s ),
		};
	}

	static List<List<Vector3>> Box( Vector3 b )
	{
		Vector3 C( int i ) => new( (i & 1) != 0 ? b.x : -b.x, (i & 2) != 0 ? b.y : -b.y, (i & 4) != 0 ? b.z : -b.z );
		int[][] faces =
		{
			new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 },
			new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 },
			new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },
		};

		var polys = new List<List<Vector3>>();
		foreach ( var f in faces )
			polys.Add( new List<Vector3> { C( f[0] ), C( f[1] ), C( f[2] ), C( f[3] ) } );
		return polys;
	}

	static List<List<Vector3>> Ellipsoid( Vector3 r )
	{
		Vector3 V( float theta, float phi )
		{
			theta *= MathF.PI * 2f;
			phi *= MathF.PI;
			return new Vector3( MathF.Cos( theta ) * MathF.Sin( phi ) * r.x,
				MathF.Sin( theta ) * MathF.Sin( phi ) * r.y, MathF.Cos( phi ) * r.z );
		}

		var polys = new List<List<Vector3>>();
		for ( int i = 0; i < Slices; i++ )
		for ( int j = 0; j < Stacks; j++ )
		{
			var v = new List<Vector3> { V( i / (float)Slices, j / (float)Stacks ) };
			if ( j > 0 ) v.Add( V( (i + 1) / (float)Slices, j / (float)Stacks ) );
			if ( j < Stacks - 1 ) v.Add( V( (i + 1) / (float)Slices, (j + 1) / (float)Stacks ) );
			v.Add( V( i / (float)Slices, (j + 1) / (float)Stacks ) );
			if ( v.Count >= 3 )
				polys.Add( v );
		}
		return polys;
	}

	static List<List<Vector3>> Cylinder( float rad, float h )
	{
		var polys = new List<List<Vector3>>();
		Vector3 R( int k, float z ) { float a = k / (float)Ring * MathF.PI * 2f; return new Vector3( MathF.Cos( a ) * rad, MathF.Sin( a ) * rad, z ); }

		var top = new List<Vector3>();
		var bot = new List<Vector3>();
		for ( int k = 0; k < Ring; k++ )
		{
			int nk = (k + 1) % Ring;
			polys.Add( new List<Vector3> { R( k, -h ), R( nk, -h ), R( nk, h ), R( k, h ) } ); // side
			top.Add( R( k, h ) );
			bot.Add( R( k, -h ) );
		}
		polys.Add( top );
		polys.Add( bot );
		return polys;
	}

	static List<List<Vector3>> Cone( float radius, float h )
	{
		var polys = new List<List<Vector3>>();
		var apex = new Vector3( 0, 0, h );
		Vector3 R( int k ) { float a = k / (float)Ring * MathF.PI * 2f; return new Vector3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, -h ); }

		var bot = new List<Vector3>();
		for ( int k = 0; k < Ring; k++ )
		{
			int nk = (k + 1) % Ring;
			polys.Add( new List<Vector3> { R( k ), R( nk ), apex } ); // side
			bot.Add( R( k ) );
		}
		polys.Add( bot );
		return polys;
	}

	// Extruded cross-section (triangle/star/hexagon) swept along Z. Caps are triangle FANS from the centre
	// — not one cap polygon — because the CSG expects convex polygons and the star profile is concave (the
	// fan is valid for it: a star polygon is star-shaped about its centre).
	static List<List<Vector3>> Extrusion( SdfCrossSection xs, Vector3 s )
	{
		var outline = new List<Vector2>( 10 );
		SdfBrush.CrossSectionOutline( xs, s, outline );
		int n = outline.Count;

		var polys = new List<List<Vector3>>( n * 3 );
		var ct = new Vector3( 0, 0, s.z );
		var cb = new Vector3( 0, 0, -s.z );
		for ( int i = 0; i < n; i++ )
		{
			int j = (i + 1) % n;
			var ti = new Vector3( outline[i].x, outline[i].y, s.z );
			var tj = new Vector3( outline[j].x, outline[j].y, s.z );
			var bi = new Vector3( outline[i].x, outline[i].y, -s.z );
			var bj = new Vector3( outline[j].x, outline[j].y, -s.z );
			polys.Add( new List<Vector3> { ct, ti, tj } );     // top cap fan
			polys.Add( new List<Vector3> { cb, bj, bi } );     // bottom cap fan
			polys.Add( new List<Vector3> { ti, bi, bj, tj } ); // side wall
		}
		return polys;
	}

	static bool Finite( Vector3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );

	static Vector3 Centroid( List<Vector3> pts )
	{
		var c = Vector3.Zero;
		foreach ( var p in pts )
			c += p;
		return c / pts.Count;
	}

	// ── output ───────────────────────────────────────────────────────────────────────────────────────

	static CsgGeometry Triangulate( Csg csg )
	{
		var verts = new List<Vertex>();
		var indices = new List<int>();
		var mn = new Vector3( float.MaxValue );
		var mx = new Vector3( float.MinValue );

		foreach ( var poly in csg.Polygons )
		{
			if ( poly.Vertices.Count < 3 )
				continue;

			var n = poly.Plane.Normal;
			if ( !Finite( n ) || n.LengthSquared < 1e-10f )
				n = Vector3.Up; // degenerate plane — keep the tri but give it a sane normal

			bool ok = true;
			foreach ( var v in poly.Vertices )
				if ( !Finite( v.Pos ) ) { ok = false; break; }
			if ( !ok )
				continue; // drop polygons with NaN/Inf positions rather than corrupt the buffer

			int b0 = verts.Count;
			foreach ( var v in poly.Vertices )
			{
				mn = Vector3.Min( mn, v.Pos );
				mx = Vector3.Max( mx, v.Pos );
				verts.Add( new Vertex( v.Pos, n, Vector3.Forward, Vector4.Zero ) );
			}

			// Fan-triangulate the convex polygon.
			for ( int k = 1; k < poly.Vertices.Count - 1; k++ )
			{
				indices.Add( b0 );
				indices.Add( b0 + k );
				indices.Add( b0 + k + 1 );
			}
		}

		if ( verts.Count < 3 || indices.Count < 3 )
			return null;

		return new CsgGeometry { Verts = verts, Indices = indices, Bounds = new BBox( mn, mx ) };
	}
}
