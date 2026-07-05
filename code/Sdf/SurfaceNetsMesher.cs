using System;

namespace Mimiclay;

/// <summary>
/// Naive Surface Nets: sample the SDF on a regular grid, drop one vertex inside every
/// cell the surface passes through (at the average of its edge crossings), then stitch
/// neighbouring cell vertices into quads. No lookup tables, smooth output, easy to extend.
/// </summary>
public static class SurfaceNetsMesher
{
	// Cube corner layout (matches EdgeCorners below).
	static readonly Vector3[] CornerOffset =
	{
		new( 0, 0, 0 ), new( 1, 0, 0 ), new( 1, 1, 0 ), new( 0, 1, 0 ),
		new( 0, 0, 1 ), new( 1, 0, 1 ), new( 1, 1, 1 ), new( 0, 1, 1 ),
	};

	// The 12 edges of a cube as pairs of corner indices.
	static readonly int[,] EdgeCorners =
	{
		{ 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
		{ 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
		{ 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 },
	};

	/// <summary>Pre-computed mesh data — vertices, indices, bounds. Holds NO engine/GPU resources, so
	/// it can be produced on a worker thread; call <see cref="Upload"/> on the main thread to realise it.</summary>
	public readonly struct MeshData
	{
		public readonly Vertex[] Vertices;
		public readonly int[] Indices;
		public readonly BBox Bounds;

		public MeshData( Vertex[] vertices, int[] indices, BBox bounds )
		{
			Vertices = vertices;
			Indices = indices;
			Bounds = bounds;
		}

		public bool IsEmpty => Vertices is null || Vertices.Length == 0;
	}

	/// <summary>Convenience: compute + upload in one call (MAIN THREAD). Null if empty.</summary>
	public static Mesh Build( List<SdfBrush> brushes, Material material, int resolution, bool flip )
		=> Upload( ComputeData( brushes, resolution, flip ), material );

	/// <summary>Turn pre-computed data into a renderable <see cref="Mesh"/>. MAIN THREAD only — creates
	/// GPU vertex/index buffers.</summary>
	public static Mesh Upload( in MeshData data, Material material )
	{
		if ( data.IsEmpty )
			return null;

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( data.Vertices.Length, data.Vertices );
		mesh.CreateIndexBuffer( data.Indices.Length, data.Indices );
		mesh.Bounds = data.Bounds;
		return mesh;
	}

	/// <summary>Sample the field and build the vertex/index lists. PURE CPU — no engine or GPU calls —
	/// so this is safe to run on a worker thread. Returns <c>default</c> (IsEmpty) for an empty field.</summary>
	public static MeshData ComputeData( List<SdfBrush> brushes, int resolution, bool flip )
	{
		if ( brushes == null || brushes.Count == 0 )
			return default;
		if ( !Sdf.TryGetBounds( brushes, out var bounds ) )
			return default;

		resolution = Math.Clamp( resolution, 4, 96 );

		var size = bounds.Size;
		float maxAxis = MathF.Max( size.x, MathF.Max( size.y, size.z ) );
		float cell = maxAxis / resolution;
		if ( cell <= 0 )
			return default;

		// Pad by a cell so the surface never clips the volume edge.
		var mins = bounds.Mins - cell;
		var maxs = bounds.Maxs + cell;
		var span = maxs - mins;

		int nx = Math.Max( 1, (int)MathF.Ceiling( span.x / cell ) );
		int ny = Math.Max( 1, (int)MathF.Ceiling( span.y / cell ) );
		int nz = Math.Max( 1, (int)MathF.Ceiling( span.z / cell ) );

		// --- 1. Sample the scalar field at every grid point (n+1 per axis). ---
		int gx = nx + 1, gy = ny + 1, gz = nz + 1;
		var grid = new float[gx * gy * gz];

		int Gi( int i, int j, int k ) => i + gx * (j + gy * k);
		Vector3 GP( int i, int j, int k ) => mins + new Vector3( i * cell, j * cell, k * cell );

		for ( int k = 0; k < gz; k++ )
		for ( int j = 0; j < gy; j++ )
		for ( int i = 0; i < gx; i++ )
			grid[Gi( i, j, k )] = Sdf.Sample( brushes, GP( i, j, k ) );

		// --- 2. One vertex per surface-crossing cell. ---
		var cellVert = new int[nx * ny * nz];
		for ( int i = 0; i < cellVert.Length; i++ )
			cellVert[i] = -1;

		int Ci( int i, int j, int k ) => i + nx * (j + ny * k);

		var verts = new List<Vertex>();
		float ge = cell * 0.5f;
		Span<float> cv = stackalloc float[8];

		for ( int k = 0; k < nz; k++ )
		for ( int j = 0; j < ny; j++ )
		for ( int i = 0; i < nx; i++ )
		{
			int neg = 0;
			for ( int c = 0; c < 8; c++ )
			{
				var o = CornerOffset[c];
				float v = grid[Gi( i + (int)o.x, j + (int)o.y, k + (int)o.z )];
				cv[c] = v;
				if ( v < 0 )
					neg++;
			}

			if ( neg == 0 || neg == 8 )
				continue; // fully inside or fully outside — no surface

			var basePos = GP( i, j, k );
			Vector3 sum = Vector3.Zero;
			int count = 0;

			for ( int e = 0; e < 12; e++ )
			{
				int a = EdgeCorners[e, 0], b = EdgeCorners[e, 1];
				float va = cv[a], vb = cv[b];
				if ( (va < 0) == (vb < 0) )
					continue;

				float t = va / (va - vb);
				var pa = basePos + CornerOffset[a] * cell;
				var pb = basePos + CornerOffset[b] * cell;
				sum += Vector3.Lerp( pa, pb, t );
				count++;
			}

			if ( count == 0 )
				continue;

			var pos = sum / count;
			var normal = Sdf.Gradient( brushes, pos, ge );

			var tangent = Vector3.Cross( normal, Vector3.Up );
			if ( tangent.LengthSquared < 0.001f )
				tangent = Vector3.Cross( normal, Vector3.Forward );
			tangent = tangent.Normal;

			cellVert[Ci( i, j, k )] = verts.Count;
			var surf = Sdf.SampleSurface( brushes, pos );
			// Per-brush metalness/roughness ride in TexCoord0.xy (this surface has no real UVs —
			// it's triplanar-shaded), blended across seams the same way the vertex Colour is.
			verts.Add( new Vertex( pos, normal, tangent, new Vector4( surf.Metallic, surf.Roughness, 0f, 0f ) )
			{
				Color = surf.Color,
			} );
		}

		if ( verts.Count == 0 )
			return default;

		// --- 3. Stitch quads across every sign-changing grid edge. ---
		var indices = new List<int>();
		int sy = nx, sz = nx * ny;

		void Quad( int q0, int q1, int q2, int q3, bool rev )
		{
			if ( q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0 )
				return;

			if ( rev ^ flip )
			{
				indices.Add( q0 ); indices.Add( q2 ); indices.Add( q1 );
				indices.Add( q0 ); indices.Add( q3 ); indices.Add( q2 );
			}
			else
			{
				indices.Add( q0 ); indices.Add( q1 ); indices.Add( q2 );
				indices.Add( q0 ); indices.Add( q2 ); indices.Add( q3 );
			}
		}

		// Edges along X — quad spans the four cells around the edge in the Y/Z plane.
		for ( int k = 1; k < nz; k++ )
		for ( int j = 1; j < ny; j++ )
		for ( int i = 0; i < nx; i++ )
		{
			float g0 = grid[Gi( i, j, k )], g1 = grid[Gi( i + 1, j, k )];
			if ( (g0 < 0) == (g1 < 0) )
				continue;

			int b = Ci( i, j, k );
			Quad( cellVert[b], cellVert[b - sy], cellVert[b - sy - sz], cellVert[b - sz], g0 > g1 );
		}

		// Edges along Y.
		for ( int k = 1; k < nz; k++ )
		for ( int j = 0; j < ny; j++ )
		for ( int i = 1; i < nx; i++ )
		{
			float g0 = grid[Gi( i, j, k )], g1 = grid[Gi( i, j + 1, k )];
			if ( (g0 < 0) == (g1 < 0) )
				continue;

			int b = Ci( i, j, k );
			Quad( cellVert[b], cellVert[b - sz], cellVert[b - sz - 1], cellVert[b - 1], g0 > g1 );
		}

		// Edges along Z.
		for ( int k = 0; k < nz; k++ )
		for ( int j = 1; j < ny; j++ )
		for ( int i = 1; i < nx; i++ )
		{
			float g0 = grid[Gi( i, j, k )], g1 = grid[Gi( i, j, k + 1 )];
			if ( (g0 < 0) == (g1 < 0) )
				continue;

			int b = Ci( i, j, k );
			Quad( cellVert[b], cellVert[b - 1], cellVert[b - 1 - sy], cellVert[b - sy], g0 > g1 );
		}

		// --- 4. Hand back raw data; the caller uploads it to a Mesh on the main thread. ---
		return new MeshData( verts.ToArray(), indices.ToArray(), bounds );
	}
}
