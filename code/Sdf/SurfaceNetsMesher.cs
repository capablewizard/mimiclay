using System;

namespace Mimiclay;

/// <summary>
/// Naive Surface Nets: sample the SDF on a regular grid, drop one vertex inside every
/// cell the surface passes through (at the average of its edge crossings), then stitch
/// neighbouring cell vertices into quads. No lookup tables, smooth output, easy to extend.
///
/// The sampling and the meshing are separate steps. <see cref="SampleGrid"/> produces a <see cref="MeshGrid"/> —
/// preferably on the GPU via <see cref="SdfMeshGridGpu"/>, which is what stops this file from being a second
/// implementation of the field the shaders already evaluate — and <see cref="ComputeData(in MeshGrid, bool)"/>
/// turns that grid into vertices and indices with no knowledge of brushes at all. Sampling is main-thread
/// (GPU dispatch + readback); meshing is pure CPU and worker-safe.
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

	/// <summary>How far from the surface, in cells, grid points still get real material attributes. A corner of a
	/// sign-changing cell lies within one cell diagonal (about 1.73 cells) of the surface, and smooth-min fields
	/// under-estimate distance rather than over-estimate it, so 3 is comfortable insurance. Everywhere else the
	/// attribute evaluation is skipped — on both the GPU and CPU paths — which is most of the grid.</summary>
	const float AttrBandCells = 3f;

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

	/// <summary>The sampled field on the mesher's grid: four floats per point — signed distance, metalness,
	/// roughness, and sRGB colour packed as r + g*256 + b*65536 (8 bits a channel, exact in a float32, and
	/// unpacked per corner BEFORE interpolation because lerping packed values is meaningless). Plain arrays and
	/// numbers, so it crosses to a worker thread freely.</summary>
	public readonly struct MeshGrid
	{
		public readonly float[] Data;          // (i + gx*(j + gy*k)) * 4
		public readonly int Gx, Gy, Gz;
		public readonly Vector3 Mins;          // local position of grid point (0,0,0)
		public readonly float Cell;
		public readonly BBox Bounds;           // the brush-union bounds this grid was sized from
		public readonly int BrushCount;        // for diagnostics only
		public readonly bool FromGpu;

		public MeshGrid( float[] data, int gx, int gy, int gz, Vector3 mins, float cell, BBox bounds, int brushCount, bool fromGpu )
		{
			Data = data; Gx = gx; Gy = gy; Gz = gz;
			Mins = mins; Cell = cell; Bounds = bounds; BrushCount = brushCount; FromGpu = fromGpu;
		}

		public bool IsEmpty => Data is null || Gx < 2 || Gy < 2 || Gz < 2;
	}

	/// <summary>Where the last mesh spent its time, in milliseconds, split by the phases that scale
	/// differently: <c>Sample</c> is the O(res^3) grid pass, <c>Vertex</c> is the per-vertex pass, and
	/// <c>Stitch</c> is the pure index work. Written unconditionally — two timestamp reads per phase — because
	/// the split is what tells us where the cost actually is. Statics, so only the most recent call is
	/// readable; <c>mimi_mesh_bench</c> serialises its runs.</summary>
	public static double LastSampleMs, LastVertexMs, LastStitchMs;
	public static int LastGridPoints, LastVertexCount, LastIndexCount, LastBrushCount;
	public static bool LastFromGpu;

	/// <summary>Convenience: sample + compute + upload in one call (MAIN THREAD). Null if empty.</summary>
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

	/// <summary>Sample and mesh in one call. Safe anywhere, but only reaches the GPU sampler when it happens to
	/// be on the main thread — a worker silently falls back to the CPU evaluator. Callers that mesh a lot (the
	/// sculpture's LOD chain) should stage <see cref="SampleGrid"/> on the main thread themselves and hand the
	/// grids to the worker instead.</summary>
	public static MeshData ComputeData( List<SdfBrush> brushes, int resolution, bool flip )
		=> ComputeData( SampleGrid( brushes, resolution ), flip );

	/// <summary>Evaluate the field on the grid this resolution implies. Prefers the GPU (see
	/// <see cref="SdfMeshGridGpu"/>) and falls back to the CPU brush evaluator when that isn't available —
	/// off the main thread, without compute support, or after a GPU failure. Returns an empty grid for an
	/// empty brush list. MAIN THREAD for the GPU path.</summary>
	public static MeshGrid SampleGrid( List<SdfBrush> brushes, int resolution )
		=> SampleGrids( brushes, resolution )[0];

	/// <summary>Evaluate several resolutions of the same brush list — a sculpture's whole LOD chain — in ONE GPU
	/// round trip. Meshing is no longer bound by evaluating the field but by the sync that reads it back, and that
	/// sync costs the same whether it drains one grid or three, so sampling the chain together is most of the
	/// difference between a smooth scene load and a hitchy one. MAIN THREAD for the GPU path.</summary>
	public static MeshGrid[] SampleGrids( List<SdfBrush> brushes, params int[] resolutions )
	{
		var grids = new MeshGrid[resolutions.Length];
		if ( brushes == null || brushes.Count == 0 )
			return grids;
		if ( !Sdf.TryGetBounds( brushes, out var bounds ) )
			return grids;

		long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

		int n = Math.Min( resolutions.Length, SdfMeshGridGpu.MaxSlots );
		var specs = new SdfMeshGridGpu.GridSpec[n];
		var data = new float[n][];
		var ok = new bool[n];

		for ( int i = 0; i < n; i++ )
		{
			if ( !TryGridSpec( bounds, resolutions[i], out specs[i] ) )
				continue;
			data[i] = new float[specs[i].Floats];
		}

		SdfMeshGridGpu.TrySampleBatch( brushes, specs, data, ok );

		for ( int i = 0; i < n; i++ )
		{
			ref readonly var spec = ref specs[i];
			if ( !spec.IsValid || data[i] is null )
				continue;

			if ( !ok[i] )
				SampleGridCpu( brushes, spec.Mins, spec.Cell, spec.Gx, spec.Gy, spec.Gz, spec.AttrBand, data[i] );

			BreakSurfaceTies( data[i], spec.Gx * spec.Gy * spec.Gz, spec.Cell );
			grids[i] = new MeshGrid( data[i], spec.Gx, spec.Gy, spec.Gz, spec.Mins, spec.Cell, bounds, brushes.Count, ok[i] );
		}

		// Anything past the batch's slot count still gets sampled, just on its own round trip.
		for ( int i = n; i < resolutions.Length; i++ )
			grids[i] = SampleGrid( brushes, resolutions[i] );

		LastSampleMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		LastGridPoints = grids.Length > 0 && !grids[0].IsEmpty ? grids[0].Gx * grids[0].Gy * grids[0].Gz : 0;
		LastBrushCount = brushes.Count;
		LastFromGpu = ok.Length > 0 && ok[0];

		return grids;
	}

	// The grid a resolution implies for these bounds: cell size, padded origin, point counts. Shared by every
	// sampling path so the CPU fallback and the GPU pass can never disagree about WHERE the samples are.
	static bool TryGridSpec( BBox bounds, int resolution, out SdfMeshGridGpu.GridSpec spec )
	{
		spec = default;
		resolution = Math.Clamp( resolution, 4, 96 );

		var size = bounds.Size;
		float maxAxis = MathF.Max( size.x, MathF.Max( size.y, size.z ) );
		float cell = maxAxis / resolution;
		if ( cell <= 0 )
			return false;

		// Pad by a cell so the surface never clips the volume edge.
		var mins = bounds.Mins - cell;
		var maxs = bounds.Maxs + cell;
		var span = maxs - mins;

		int nx = Math.Max( 1, (int)MathF.Ceiling( span.x / cell ) );
		int ny = Math.Max( 1, (int)MathF.Ceiling( span.y / cell ) );
		int nz = Math.Max( 1, (int)MathF.Ceiling( span.z / cell ) );

		// Grid points sit on the inclusive cell corners: n+1 per axis.
		spec = new SdfMeshGridGpu.GridSpec( mins, cell, nx + 1, ny + 1, nz + 1, cell * AttrBandCells );
		return true;
	}

	// A flat face that lands exactly on a grid plane samples to zero, and zero has no side. Which way each of
	// those points rounds is then pure float noise — it differs between the CPU and the GPU, and even between
	// two runs of the same evaluator with different instruction scheduling — so an axis-aligned box could mesh
	// differently every time. Push every tie OUTSIDE by a fraction of a cell, which is both deterministic and
	// the better answer: the edge crossing then lands on the face plane itself, instead of a whole cell out.
	//
	// The band has to clear the noise floor, not just exact zeros. Half a percent of a cell is roughly 5x the
	// worst disagreement measured between the two evaluators (the text field, where one side does its own
	// bilinear and the other uses a hardware sampler), and still far too small to move a vertex visibly — the
	// edge crossing shifts by at most that same half percent. Too tight and a single flipped sample renumbers
	// every vertex after it, which is a nightmare to read as anything but a huge divergence.
	static void BreakSurfaceTies( float[] data, int points, float cell )
	{
		float eps = cell * 5e-3f;
		for ( int i = 0; i < points; i++ )
		{
			int o = i * 4;
			if ( data[o] > -eps && data[o] < eps )
				data[o] = eps;
		}
	}

	// The fallback producer: the CPU brush evaluator writing the SAME layout the compute shader writes, so the
	// meshing below never has to know which one ran. Attributes are banded exactly like the shader's, or this
	// would be markedly slower than the per-vertex sampling it replaced.
	static void SampleGridCpu( List<SdfBrush> brushes, Vector3 mins, float cell, int gx, int gy, int gz, float band, float[] data )
	{
		SdfTextSdf.EnsureBaked( brushes ); // the GPU path bakes glyphs during the brush pack; match it here

		float white = PackColor( Color.White );
		int o = 0;
		for ( int k = 0; k < gz; k++ )
		for ( int j = 0; j < gy; j++ )
		for ( int i = 0; i < gx; i++, o += 4 )
		{
			var p = mins + new Vector3( i * cell, j * cell, k * cell );
			float d = Sdf.Sample( brushes, p );

			data[o + 0] = d;
			if ( MathF.Abs( d ) <= band )
			{
				var s = Sdf.SampleSurface( brushes, p );
				data[o + 1] = s.Metallic;
				data[o + 2] = s.Roughness;
				data[o + 3] = PackColor( s.Color );
			}
			else
			{
				// Empty-space material, matching the shader's seed: white, dielectric, fully rough.
				data[o + 1] = 0f;
				data[o + 2] = 1f;
				data[o + 3] = white;
			}
		}
	}

	static float PackColor( Color c )
	{
		float r = MathF.Floor( Math.Clamp( c.r, 0f, 1f ) * 255f + 0.5f );
		float g = MathF.Floor( Math.Clamp( c.g, 0f, 1f ) * 255f + 0.5f );
		float b = MathF.Floor( Math.Clamp( c.b, 0f, 1f ) * 255f + 0.5f );
		return r + g * 256f + b * 65536f;
	}

	/// <summary>Build the vertex/index lists from an already-sampled grid. PURE CPU — no engine or GPU calls, and
	/// no brush evaluation at all — so this is safe to run on a worker thread. Returns <c>default</c> (IsEmpty)
	/// for an empty grid or a grid the surface never crosses.</summary>
	public static MeshData ComputeData( in MeshGrid g, bool flip )
	{
		if ( g.IsEmpty )
			return default;

		int gx = g.Gx, gy = g.Gy, gz = g.Gz;
		int nx = gx - 1, ny = gy - 1, nz = gz - 1;
		float cell = g.Cell;
		var data = g.Data;
		var gridMins = g.Mins;   // hoisted: an `in` parameter can't be captured by the local functions below
		var bounds = g.Bounds;

		float Dist( int i, int j, int k ) => data[(i + gx * (j + gy * k)) * 4];
		Vector3 GP( int i, int j, int k ) => gridMins + new Vector3( i * cell, j * cell, k * cell );

		long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

		// --- 1. One vertex per surface-crossing cell. ---
		var cellVert = new int[nx * ny * nz];
		for ( int i = 0; i < cellVert.Length; i++ )
			cellVert[i] = -1;

		int Ci( int i, int j, int k ) => i + nx * (j + ny * k);

		var verts = new List<Vertex>();
		Span<float> cv = stackalloc float[8];

		for ( int k = 0; k < nz; k++ )
		for ( int j = 0; j < ny; j++ )
		for ( int i = 0; i < nx; i++ )
		{
			int neg = 0;
			for ( int c = 0; c < 8; c++ )
			{
				var o = CornerOffset[c];
				float v = Dist( i + (int)o.x, j + (int)o.y, k + (int)o.z );
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

			// Normal from the SAMPLED field, not from a fresh analytic gradient: it's the surface the mesh
			// actually has (this IS the field the vertices were placed in), it costs six trilinear fetches
			// instead of six brush-list walks, and it keeps the brush evaluator out of the worker thread.
			var normal = GridGradient( g, pos );

			var tangent = Vector3.Cross( normal, Vector3.Up );
			if ( tangent.LengthSquared < 0.001f )
				tangent = Vector3.Cross( normal, Vector3.Forward );
			tangent = tangent.Normal;

			SampleAttributes( g, pos, out var color, out float metal, out float rough );

			cellVert[Ci( i, j, k )] = verts.Count;
			// Per-brush metalness/roughness ride in TexCoord0.xy (this surface has no real UVs —
			// it's triplanar-shaded), blended across seams the same way the vertex Colour is.
			verts.Add( new Vertex( pos, normal, tangent, new Vector4( metal, rough, 0f, 0f ) )
			{
				Color = color,
			} );
		}

		long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

		if ( verts.Count == 0 )
			return default;

		// --- 2. Stitch quads across every sign-changing grid edge. ---
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
			float g0 = Dist( i, j, k ), g1 = Dist( i + 1, j, k );
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
			float g0 = Dist( i, j, k ), g1 = Dist( i, j + 1, k );
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
			float g0 = Dist( i, j, k ), g1 = Dist( i, j, k + 1 );
			if ( (g0 < 0) == (g1 < 0) )
				continue;

			int b = Ci( i, j, k );
			Quad( cellVert[b], cellVert[b - 1], cellVert[b - 1 - sy], cellVert[b - sy], g0 > g1 );
		}

		// --- 3. Hand back raw data; the caller uploads it to a Mesh on the main thread. ---
		long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
		double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		LastVertexMs = (t2 - t1) * toMs;
		LastStitchMs = (t3 - t2) * toMs;
		LastVertexCount = verts.Count;
		LastIndexCount = indices.Count;

		return new MeshData( verts.ToArray(), indices.ToArray(), bounds );
	}

	// --- Reading the sampled grid -----------------------------------------------------------------------

	// Trilinear distance at a local point, clamped to the grid. Vertices always sit inside a cell, but the
	// gradient below steps half a cell either way, which can reach past the outermost grid point.
	static float SampleDistance( in MeshGrid g, Vector3 p )
	{
		Cell( g, p, out int i0, out int j0, out int k0, out float fx, out float fy, out float fz );
		int gx = g.Gx, gy = g.Gy;
		var d = g.Data;

		float D( int i, int j, int k ) => d[(i + gx * (j + gy * k)) * 4];

		float c00 = MathX.Lerp( D( i0, j0, k0 ), D( i0 + 1, j0, k0 ), fx );
		float c10 = MathX.Lerp( D( i0, j0 + 1, k0 ), D( i0 + 1, j0 + 1, k0 ), fx );
		float c01 = MathX.Lerp( D( i0, j0, k0 + 1 ), D( i0 + 1, j0, k0 + 1 ), fx );
		float c11 = MathX.Lerp( D( i0, j0 + 1, k0 + 1 ), D( i0 + 1, j0 + 1, k0 + 1 ), fx );
		return MathX.Lerp( MathX.Lerp( c00, c10, fy ), MathX.Lerp( c01, c11, fy ), fz );
	}

	// Central-difference gradient of the sampled field. Half a cell matches the epsilon the old analytic
	// gradient used, so normals come out the same smoothness they always did.
	static Vector3 GridGradient( in MeshGrid g, Vector3 p )
	{
		float e = g.Cell * 0.5f;
		float dx = SampleDistance( g, p + new Vector3( e, 0, 0 ) ) - SampleDistance( g, p - new Vector3( e, 0, 0 ) );
		float dy = SampleDistance( g, p + new Vector3( 0, e, 0 ) ) - SampleDistance( g, p - new Vector3( 0, e, 0 ) );
		float dz = SampleDistance( g, p + new Vector3( 0, 0, e ) ) - SampleDistance( g, p - new Vector3( 0, 0, e ) );
		var grad = new Vector3( dx, dy, dz );
		return grad.LengthSquared > 1e-12f ? grad.Normal : Vector3.Up;
	}

	// Trilinear material attributes. Colour is unpacked at each of the eight corners first — the packed
	// value is a bit field, and interpolating it directly would blend the channels into each other.
	static void SampleAttributes( in MeshGrid g, Vector3 p, out Color color, out float metal, out float rough )
	{
		Cell( g, p, out int i0, out int j0, out int k0, out float fx, out float fy, out float fz );
		int gx = g.Gx, gy = g.Gy;
		var d = g.Data;

		float r = 0f, gr = 0f, b = 0f;
		metal = 0f; rough = 0f;

		for ( int c = 0; c < 8; c++ )
		{
			int i = i0 + (c & 1), j = j0 + ((c >> 1) & 1), k = k0 + ((c >> 2) & 1);
			float w = ((c & 1) != 0 ? fx : 1f - fx)
			        * (((c >> 1) & 1) != 0 ? fy : 1f - fy)
			        * (((c >> 2) & 1) != 0 ? fz : 1f - fz);
			if ( w <= 0f )
				continue;

			int o = (i + gx * (j + gy * k)) * 4;
			metal += d[o + 1] * w;
			rough += d[o + 2] * w;

			int packed = (int)(d[o + 3] + 0.5f);
			r += (packed & 0xFF) * w;
			gr += ((packed >> 8) & 0xFF) * w;
			b += ((packed >> 16) & 0xFF) * w;
		}

		color = new Color( r / 255f, gr / 255f, b / 255f );
	}

	// Local point -> base grid index + fractional offset, clamped so the 8-corner reads are always in range.
	static void Cell( in MeshGrid g, Vector3 p, out int i0, out int j0, out int k0, out float fx, out float fy, out float fz )
	{
		var rel = (p - g.Mins) / g.Cell;
		i0 = Math.Clamp( (int)MathF.Floor( rel.x ), 0, g.Gx - 2 );
		j0 = Math.Clamp( (int)MathF.Floor( rel.y ), 0, g.Gy - 2 );
		k0 = Math.Clamp( (int)MathF.Floor( rel.z ), 0, g.Gz - 2 );
		fx = Math.Clamp( rel.x - i0, 0f, 1f );
		fy = Math.Clamp( rel.y - j0, 0f, 1f );
		fz = Math.Clamp( rel.z - k0, 0f, 1f );
	}
}
