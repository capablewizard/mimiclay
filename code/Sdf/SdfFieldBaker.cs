using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mimiclay;

/// <summary>
/// DORMANT (not currently called): the renderer now generates every prop's field on the GPU
/// (<see cref="SdfFieldGpu"/>), so this CPU baker is unused. It's kept because its one advantage the GPU path
/// doesn't have is a CONTENT-HASH SHARED cache — many identical clones share ONE baked texture (memory) and
/// bake once. To re-enable for static/repeated props: in SdfRaymarchRenderer's field block, branch non-edited
/// props to GetOrBake()/RequestField() (the small async-bind glue was removed in the GPU transition) and bind
/// its texture instead of the per-instance GPU one. See the [[gpu-field-evaluator]] memory note.
///
/// Bakes a brush list's signed-distance field into a 3D (volume) texture so the raymarcher samples
/// ONE trilinear texture fetch per step instead of re-evaluating every brush. This is the "distance
/// cache" technique: the heavy O(brushCount) work is done once per voxel at bake time, and rendering
/// cost stops scaling with brush count — a 31-brush giraffe then marches as cheaply as a 1-brush blob.
///
/// The field is sampled in the sculpture's LOCAL frame (the same frame brush positions live in and the
/// mesher uses), so identical brush lists produce an identical bake regardless of world placement — a
/// content-hash cache therefore lets clones (and a perf grid) share one texture. The shader converts the
/// world-space march point back into this frame (WorldToModelPos) before sampling.
/// </summary>
public static class SdfFieldBaker
{
	/// <summary>A finished volume texture plus the local-space box it spans (sample points sit on the
	/// inclusive corners) and its per-axis texel counts — everything the shader needs to map a point to a UVW.</summary>
	public sealed class BakedField
	{
		public Texture Texture;
		public Vector3 Mins, Maxs; // local AABB: sample 0 -> Mins, sample (dim-1) -> Maxs
		public Vector3 Dims;       // texel counts per axis (float, fed straight to the shader)
	}

	// CPU-only result of the worker-thread sampling pass — no GPU handles, safe off the main thread.
	sealed class FieldData
	{
		public float[] Distances;
		public int Nx, Ny, Nz;
		public Vector3 Mins, Maxs;
	}

	// Shared bake cache keyed by brush content + resolution, mirroring SdfSculpture's model cache: a level
	// full of repeated props (or a clone grid) bakes each UNIQUE shape once. Value is the in-flight Task so
	// concurrent identical requests join one bake. Bounded with FIFO eviction so an editing churn can't leak
	// textures (a live renderer keeps its own reference alive regardless; eviction only drops the cache's).
	static readonly Dictionary<int, Task<BakedField>> _cache = new();
	static readonly Queue<int> _order = new();
	const int CacheCap = 128;

	/// <summary>Cache key for a brush list at a given field resolution. Identical brushes → identical key →
	/// shared bake (the local-space bounds are a deterministic function of the brushes, so they're implied).</summary>
	public static int Key( List<SdfBrush> brushes, int resolution )
		=> SdfSculpture.ContentHash( brushes, resolution, false );

	/// <summary>Get the cached bake for <paramref name="key"/> or start one. <paramref name="snapshot"/> must be a
	/// standalone copy of the brushes (it's read on a worker thread); <paramref name="mins"/>/<paramref name="maxs"/>
	/// are the local AABB to span. Only the FIRST caller's snapshot/bounds are used per key (identical anyway).</summary>
	public static Task<BakedField> GetOrBake( int key, List<SdfBrush> snapshot, Vector3 mins, Vector3 maxs, int resolution )
	{
		if ( _cache.TryGetValue( key, out var existing ) && !existing.IsFaulted )
			return existing;

		SdfTextSdf.EnsureBaked( snapshot ); // still the main thread here — text samples as a box without it
		var task = BakeAsync( snapshot, mins, maxs, resolution );
		_cache[key] = task;
		_order.Enqueue( key );

		while ( _order.Count > CacheCap )
			_cache.Remove( _order.Dequeue() );

		return task;
	}

	static async Task<BakedField> BakeAsync( List<SdfBrush> brushes, Vector3 mins, Vector3 maxs, int resolution )
	{
		await GameTask.WorkerThread();
		var fd = Compute( brushes, mins, maxs, resolution );
		await GameTask.MainThread();

		if ( fd is null )
			return null;

		// R32F = one float distance per voxel. Half the memory of RGBA but exact for sphere tracing.
		// TODO (future, memory opt): 1-byte compact distances (the trick from the SDF video) — store an
		// I8/R8 distance NORMALIZED to a narrow band (±~half a voxel diagonal, the only range that matters
		// for the surface) instead of a full float. Cuts the volume to a quarter of R32F. Needs: clamp+encode
		// here, and a decode (un-normalize back to world units) in SampleFieldTex before sphere tracing.
		var tex = Texture.CreateVolume( fd.Nx, fd.Ny, fd.Nz, ImageFormat.R32F )
			.WithDynamicUsage()
			.WithName( "sdf_field" )
			.Finish();

		if ( !tex.IsValid() )
			return null;

		// float[] -> byte[] via Buffer.BlockCopy (MemoryMarshal is blocked by the game-code whitelist).
		var bytes = new byte[fd.Distances.Length * sizeof( float )];
		Buffer.BlockCopy( fd.Distances, 0, bytes, 0, bytes.Length );
		tex.Update3D( bytes, 0, 0, 0, fd.Nx, fd.Ny, fd.Nz );

		return new BakedField
		{
			Texture = tex,
			Mins = fd.Mins,
			Maxs = fd.Maxs,
			Dims = new Vector3( fd.Nx, fd.Ny, fd.Nz ),
		};
	}

	// Sample the combined field on a regular grid over [mins,maxs] (sample points on the inclusive corners).
	// PURE CPU (Sdf.Sample only) — safe on a worker thread. Texel layout is x-fastest then y then z, matching
	// what Update3D expects: index = x + Nx*(y + Ny*z).
	static FieldData Compute( List<SdfBrush> brushes, Vector3 mins, Vector3 maxs, int resolution )
	{
		if ( brushes is null || brushes.Count == 0 )
			return null;

		var span = maxs - mins;
		float maxAxis = MathF.Max( span.x, MathF.Max( span.y, span.z ) );
		if ( maxAxis <= 0f )
			return null;

		resolution = Math.Clamp( resolution, 8, 256 );
		float cell = maxAxis / resolution;

		// dims = cells + 1 (samples on both ends); keep voxels ~cubic by deriving each axis from the same cell.
		int nx = Math.Max( 2, (int)MathF.Round( span.x / cell ) + 1 );
		int ny = Math.Max( 2, (int)MathF.Round( span.y / cell ) + 1 );
		int nz = Math.Max( 2, (int)MathF.Round( span.z / cell ) + 1 );

		var dist = new float[nx * ny * nz];

		// Step so sample 0 lands on mins and sample (n-1) lands on maxs exactly (matches the shader's UVW remap).
		var step = new Vector3(
			nx > 1 ? span.x / (nx - 1) : 0f,
			ny > 1 ? span.y / (ny - 1) : 0f,
			nz > 1 ? span.z / (nz - 1) : 0f );

		for ( int k = 0; k < nz; k++ )
		{
			float pz = mins.z + k * step.z;
			for ( int j = 0; j < ny; j++ )
			{
				float py = mins.y + j * step.y;
				int row = (k * ny + j) * nx;
				for ( int i = 0; i < nx; i++ )
					dist[row + i] = Sdf.Sample( brushes, new Vector3( mins.x + i * step.x, py, pz ) );
			}
		}

		return new FieldData { Distances = dist, Nx = nx, Ny = ny, Nz = nz, Mins = mins, Maxs = maxs };
	}
}
