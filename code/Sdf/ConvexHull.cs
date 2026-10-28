using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Small incremental 3D convex hull — enough for collider decomposition, where we need the hull's PLANES
/// (point-in-hull tests) and its VERTICES (what goes to the physics hull) for a few hundred points at commit
/// time. Faces are triangles with outward normals; coplanar faces aren't merged (the inside test doesn't care).
/// </summary>
public static class ConvexHull
{
	struct Face
	{
		public int A, B, C;
		public Vector3 N;
		public float D;
		public bool Alive;
	}

	/// <summary>Hull <paramref name="points"/>. Fills <paramref name="planes"/> (outward unit normal n and offset d:
	/// inside ⇔ n·p − d ≤ 0 for every plane), <paramref name="vertices"/> (the points on the hull) and, when given,
	/// <paramref name="triangles"/> (the hull's surface). False when the points are degenerate (fewer than 4, or all
	/// on a plane/line).</summary>
	public static bool TryBuild( List<Vector3> points, List<(Vector3 n, float d)> planes, List<Vector3> vertices,
		List<(Vector3 a, Vector3 b, Vector3 c)> triangles = null )
	{
		planes.Clear();
		vertices.Clear();
		triangles?.Clear();
		if ( points.Count < 4 )
			return false;

		// Dedupe (grid corners repeat across neighbouring cells; projected points can coincide).
		var pts = new List<Vector3>( points.Count );
		var seen = new HashSet<(int, int, int)>();
		Vector3 lo = new( float.MaxValue ), hi = new( float.MinValue );
		foreach ( var p in points )
		{
			lo = Vector3.Min( lo, p );
			hi = Vector3.Max( hi, p );
		}
		float scale = MathF.Max( (hi - lo).Length, 1e-3f );
		float q = scale * 1e-4f;
		foreach ( var p in points )
			if ( seen.Add( ((int)MathF.Round( p.x / q ), (int)MathF.Round( p.y / q ), (int)MathF.Round( p.z / q )) ) )
				pts.Add( p );
		if ( pts.Count < 4 )
			return false;

		// Points strictly inside the hull of the 26 directional extremes can't be hull vertices — drop them
		// first (Akl–Toussaint), so the incremental pass mostly sees real candidates.
		pts = DiscardInterior( pts );

		float eps = scale * 1e-5f;

		// Initial tetrahedron from extreme points.
		int i0 = 0;
		for ( int i = 1; i < pts.Count; i++ )
			if ( pts[i].x < pts[i0].x )
				i0 = i;
		int i1 = Farthest( pts, i => (pts[i] - pts[i0]).LengthSquared );
		var e01 = pts[i1] - pts[i0];
		if ( e01.Length < eps )
			return false;
		int i2 = Farthest( pts, i => Vector3.Cross( e01, pts[i] - pts[i0] ).LengthSquared );
		var n012 = Vector3.Cross( e01, pts[i2] - pts[i0] );
		if ( n012.Length < eps * e01.Length )
			return false;
		n012 = n012.Normal;
		int i3 = Farthest( pts, i => MathF.Abs( Vector3.Dot( n012, pts[i] - pts[i0] ) ) );
		if ( MathF.Abs( Vector3.Dot( n012, pts[i3] - pts[i0] ) ) < eps )
			return false;

		var faces = new List<Face>( 64 );
		var edges = new Dictionary<(int, int), int>(); // directed edge → the alive face owning it
		var centroid = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25f;

		void AddFace( int a, int b, int c )
		{
			var n = Vector3.Cross( pts[b] - pts[a], pts[c] - pts[a] );
			if ( Vector3.Dot( n, pts[a] - centroid ) < 0f )
			{
				(b, c) = (c, b);
				n = -n;
			}
			n = n.Normal;
			int id = faces.Count;
			faces.Add( new Face { A = a, B = b, C = c, N = n, D = Vector3.Dot( n, pts[a] ), Alive = true } );
			edges[(a, b)] = id;
			edges[(b, c)] = id;
			edges[(c, a)] = id;
		}

		AddFace( i0, i1, i2 );
		AddFace( i0, i1, i3 );
		AddFace( i0, i2, i3 );
		AddFace( i1, i2, i3 );

		var alive = new List<int> { 0, 1, 2, 3 };   // only the live faces are ever scanned
		var visible = new List<int>( 32 );
		var visibleSet = new HashSet<int>();
		var horizon = new List<(int, int)>( 32 );
		for ( int pi = 0; pi < pts.Count; pi++ )
		{
			if ( pi == i0 || pi == i1 || pi == i2 || pi == i3 )
				continue;
			var p = pts[pi];

			visible.Clear();
			foreach ( int f in alive )
				if ( Vector3.Dot( faces[f].N, p ) - faces[f].D > eps )
					visible.Add( f );
			if ( visible.Count == 0 )
				continue; // inside (or on) the current hull

			// Horizon: edges of visible faces whose twin belongs to a face that isn't visible.
			visibleSet.Clear();
			visibleSet.UnionWith( visible );
			horizon.Clear();
			foreach ( int f in visible )
			{
				var fc = faces[f];
				if ( !visibleSet.Contains( Twin( fc.B, fc.A ) ) ) horizon.Add( (fc.A, fc.B) );
				if ( !visibleSet.Contains( Twin( fc.C, fc.B ) ) ) horizon.Add( (fc.B, fc.C) );
				if ( !visibleSet.Contains( Twin( fc.A, fc.C ) ) ) horizon.Add( (fc.C, fc.A) );
			}

			foreach ( int f in visible )
			{
				var fc = faces[f];
				fc.Alive = false;
				faces[f] = fc;
				edges.Remove( (fc.A, fc.B) );
				edges.Remove( (fc.B, fc.C) );
				edges.Remove( (fc.C, fc.A) );
			}
			alive.RemoveAll( visibleSet.Contains );

			// New faces keep the dead faces' winding (a→b seen from outside), so they face outward already.
			foreach ( var (a, b) in horizon )
			{
				var n = Vector3.Cross( pts[b] - pts[a], p - pts[a] );
				if ( n.LengthSquared < 1e-20f )
					continue;
				n = n.Normal;
				int id = faces.Count;
				faces.Add( new Face { A = a, B = b, C = pi, N = n, D = Vector3.Dot( n, pts[a] ), Alive = true } );
				edges[(a, b)] = id;
				edges[(b, pi)] = id;
				edges[(pi, a)] = id;
				alive.Add( id );
			}
		}

		// The face across a directed edge's reverse (-1 when none — only mid-update, never on a closed hull).
		int Twin( int a, int b ) => edges.TryGetValue( (a, b), out int f ) ? f : -1;

		var used = new HashSet<int>();
		foreach ( var f in faces )
		{
			if ( !f.Alive )
				continue;
			planes.Add( (f.N, f.D) );
			triangles?.Add( (pts[f.A], pts[f.B], pts[f.C]) );
			used.Add( f.A );
			used.Add( f.B );
			used.Add( f.C );
		}
		foreach ( int i in used )
			vertices.Add( pts[i] );
		return planes.Count >= 4;
	}

	/// <summary>Edges of the hull of <paramref name="points"/> once coplanar triangles merge into polygon faces
	/// (Euler: E = V + F − 2) — what the physics engine's half-edge limit counts. −1 when degenerate.</summary>
	public static int EdgeCount( List<Vector3> points )
	{
		var planes = new List<(Vector3 n, float d)>( 64 );
		var verts = new List<Vector3>( 64 );
		if ( !TryBuild( points, planes, verts ) )
			return -1;

		var faces = new List<(Vector3 n, float d)>( planes.Count );
		foreach ( var (n, d) in planes )
		{
			bool merged = false;
			foreach ( var (fn, fd) in faces )
			{
				if ( Vector3.Dot( n, fn ) > 0.9999f && MathF.Abs( d - fd ) < 1e-3f )
				{
					merged = true;
					break;
				}
			}
			if ( !merged )
				faces.Add( (n, d) );
		}
		return verts.Count + faces.Count - 2;
	}

	/// <summary>Up to <paramref name="count"/> of the hull's vertices, spread as evenly as possible (farthest-point
	/// sampling from the vertex farthest from the centroid). Their hull lies inside the original.</summary>
	public static List<Vector3> Thin( List<Vector3> points, int count )
	{
		var planes = new List<(Vector3 n, float d)>( 64 );
		var verts = new List<Vector3>( 64 );
		if ( !TryBuild( points, planes, verts ) || verts.Count <= count )
			return verts.Count > 0 ? verts : points;

		var centroid = Vector3.Zero;
		foreach ( var v in verts )
			centroid += v;
		centroid /= verts.Count;

		var minD = new float[verts.Count];
		Array.Fill( minD, float.MaxValue );
		var picked = new List<Vector3>( count );
		int next = Farthest( verts, i => (verts[i] - centroid).LengthSquared );
		while ( picked.Count < count )
		{
			picked.Add( verts[next] );
			float best = -1f;
			for ( int i = 0; i < verts.Count; i++ )
			{
				minD[i] = MathF.Min( minD[i], (verts[i] - verts[next]).LengthSquared );
				if ( minD[i] > best )
				{
					best = minD[i];
					next = i;
				}
			}
			if ( best <= 0f )
				break;
		}
		return picked;
	}

	// Akl–Toussaint prefilter: the extreme points along 26 directions (±axes, face and corner diagonals) are hull
	// vertices, so their own hull lies INSIDE the full hull — any point strictly inside it can't be a vertex and
	// is dropped. Small inputs skip it (not worth the extra pass).
	static List<Vector3> DiscardInterior( List<Vector3> pts )
	{
		if ( pts.Count < 64 )
			return pts;

		Span<Vector3> dirs = stackalloc Vector3[26];
		int nd = 0;
		for ( int x = -1; x <= 1; x++ )
		for ( int y = -1; y <= 1; y++ )
		for ( int z = -1; z <= 1; z++ )
			if ( x != 0 || y != 0 || z != 0 )
				dirs[nd++] = new Vector3( x, y, z ).Normal;

		var extremes = new List<Vector3>( 26 );
		for ( int d = 0; d < nd; d++ )
		{
			int best = 0;
			float bestV = float.MinValue;
			for ( int i = 0; i < pts.Count; i++ )
			{
				float v = Vector3.Dot( dirs[d], pts[i] );
				if ( v > bestV ) { bestV = v; best = i; }
			}
			extremes.Add( pts[best] );
		}

		var inner = new List<(Vector3 n, float d)>( 64 );
		var verts = new List<Vector3>( 26 );
		if ( !TryBuild( extremes, inner, verts ) )
			return pts;

		var kept = new List<Vector3>( pts.Count / 2 );
		foreach ( var p in pts )
		{
			bool interior = true;
			foreach ( var (n, d) in inner )
			{
				if ( Vector3.Dot( n, p ) - d >= -1e-4f )
				{
					interior = false;
					break;
				}
			}
			if ( !interior )
				kept.Add( p );
		}
		return kept.Count >= 4 ? kept : pts;
	}

	static int Farthest( List<Vector3> pts, Func<int, float> metric )
	{
		int best = 0;
		float bestV = float.MinValue;
		for ( int i = 0; i < pts.Count; i++ )
		{
			float v = metric( i );
			if ( v > bestV )
			{
				bestV = v;
				best = i;
			}
		}
		return best;
	}
}
