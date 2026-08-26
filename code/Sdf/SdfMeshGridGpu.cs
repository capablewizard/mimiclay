using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Samples the brush union on the CPU mesher's grid using the GPU (shaders/sdf_mesh_grid_cs), so surface nets can
/// build a mesh without the CPU ever evaluating a brush.
///
/// This exists to retire the duplicate evaluator. <see cref="SurfaceNetsMesher"/> used to walk the brush list through
/// <c>Sdf.Sample</c> — a second, independent implementation of the field the GPU already knows how to compute — and the
/// two drifted: Text brushes meshed as a plain box for months because the CPU path falls back to a box when the glyph
/// field isn't baked, while the GPU path never did. Everything now descends from sdf_eval.hlsl.
///
/// Deliberately NOT part of <see cref="SdfFieldGpu"/>: that one is per-renderer, sized and padded for RAYMARCHING (a
/// 16-unit blend pad, a resolution chosen for surface detail, a sparse brick atlas). This samples exactly the mesher's
/// own grid — its bounds, its cell size, its sample points — which is both much smaller and the only way the result
/// drops into the existing surface-nets code unchanged.
///
/// MAIN THREAD ONLY: dispatch and the readback are both main-thread, and the readback is a synchronous GPU stall. The
/// meshing itself stays on a worker; see <see cref="SurfaceNetsMesher.MeshGrid"/> for how the two are staged.
/// </summary>
public static class SdfMeshGridGpu
{
	const int MaxBrushes = SdfBrushPacker.MaxBrushes;
	const int TexelsPerBrush = 7;
	const int MaxSplinePoints = SdfBrushPacker.MaxSplinePoints;

	/// <summary>How many grids one <see cref="TrySampleBatch"/> can do in a single GPU sync. Three, because that's
	/// the LOD chain a sculpture builds; a fourth slot would just sit idle costing VRAM.</summary>
	public const int MaxSlots = 3;

	/// <summary>Kill switch. Off falls the mesher back to the CPU brush evaluator — the old path, kept working so a
	/// driver that won't give us a UAV texture (or a headless/server context with no compute) still meshes. Turning
	/// it back ON also clears the give-up latch, so a session that failed once can be retried without a restart —
	/// the latch is a static and would otherwise survive a hotload.</summary>
	[ConVar( "mimiclay_mesh_gpu" )]
	public static bool Enabled
	{
		get => _enabled;
		set { _enabled = value; if ( value ) _failed = false; }
	}
	static bool _enabled = true;

	/// <summary>A/B switch for the per-brush AABB early-out in the mesh grid pass. The cull is exact wherever a
	/// brush could still change the union, so it cannot move a sign change — but it is the first thing to rule out
	/// if a meshed shape ever disagrees with the marched one, which is what mimi_mesh_verify measures.</summary>
	[ConVar( "mimiclay_mesh_gpu_cull" )]
	public static bool CullEnabled { get; set; } = true;

	/// <summary>Milliseconds the last sample spent queuing dispatches versus waiting for readbacks. Worth keeping
	/// split: a dispatch is fire-and-forget, but the FIRST readback is a hard sync that drains everything already
	/// queued for the GPU — measured at a near-constant few milliseconds regardless of grid size, which is why
	/// <see cref="TrySampleBatch"/> exists. This is the main-thread cost the mesher now pays, and the number to
	/// watch if scene loads start hitching.</summary>
	public static double LastDispatchMs, LastReadbackMs;

	/// <summary>One grid to evaluate: where it starts, how big its cells are, how many points on each axis, and how
	/// far from the surface material attributes are still worth computing.</summary>
	public readonly struct GridSpec
	{
		public readonly Vector3 Mins;
		public readonly float Cell;
		public readonly int Gx, Gy, Gz;
		public readonly float AttrBand;

		public GridSpec( Vector3 mins, float cell, int gx, int gy, int gz, float attrBand )
		{
			Mins = mins; Cell = cell; Gx = gx; Gy = gy; Gz = gz; AttrBand = attrBand;
		}

		public bool IsValid => Gx >= 2 && Gy >= 2 && Gz >= 2 && Cell > 0f;
		public int Floats => Gx * Gy * Gz * 4;
	}

	static ComputeShader _cs;
	static readonly Texture[] Grids = new Texture[MaxSlots];
	static readonly int[] GridX = new int[MaxSlots], GridY = new int[MaxSlots];
	static Texture _brushTex, _splineTex;
	static readonly SdfTextAtlas TextAtlas = new();
	static float[] _data, _spline;
	static bool _failed; // a hard failure (no shader / no texture) — stop retrying every mesh

	/// <summary>Evaluate up to <see cref="MaxSlots"/> grids of the SAME brush list in one go, writing RGBA per point
	/// into each <paramref name="dst"/> (length gx*gy*gz*4): distance, metalness, roughness, packed sRGB colour.
	/// <paramref name="results"/> gets true per slot that the GPU actually filled; the caller must sample the rest
	/// on the CPU.
	///
	/// The batching is the whole point. Every dispatch goes out first, then the readbacks: only the first readback
	/// waits for the GPU to drain (a near-constant few milliseconds, and the dominant cost of meshing now), and the
	/// rest are copies off an already-idle GPU. The brush pack is also done once instead of per grid. MAIN THREAD.</summary>
	public static void TrySampleBatch( List<SdfBrush> brushes, GridSpec[] specs, float[][] dst, bool[] results )
	{
		Array.Clear( results, 0, results.Length );
		LastDispatchMs = LastReadbackMs = 0;

		if ( !Enabled || _failed || brushes is null || brushes.Count == 0 )
			return;
		if ( !ThreadSafe.IsMainThread )
			return; // a worker can't dispatch; the caller falls back rather than crashing

		int slots = Math.Min( specs.Length, MaxSlots );

		try
		{
			_cs ??= new ComputeShader( "sdf_mesh_grid_cs" );
			if ( _cs is null ) { _failed = true; return; }

			EnsureBrushTextures();
			// Pack in LOCAL space (identity transform), the same contract every other consumer uses — and the one
			// main-thread point that bakes Text brushes' glyph fields into the atlas.
			int count = SdfBrushPacker.Pack( brushes, Transform.Zero, _data, _spline, MaxBrushes, TexelsPerBrush, MaxSplinePoints, TextAtlas );
			if ( count == 0 )
				return;

			_brushTex.Update<float>( _data, 0, 0, MaxBrushes * TexelsPerBrush, 1 );
			_splineTex.Update<float>( _spline, 0, 0, MaxSplinePoints, 1 );

			_cs.Attributes.Set( "BrushData", _brushTex );
			_cs.Attributes.Set( "SplineData", _splineTex );
			_cs.Attributes.Set( "TextSdf", TextAtlas.Texture );
			_cs.Attributes.Set( "BrushCount", count );
			// Cull ON. The early-out is incremental and exact wherever a brush could still change the result, so
			// every sign change — the only thing surface nets reads — is exact. Far from the surface it may
			// over-estimate |d|, which the march would hate and the mesher cannot see.
			_cs.Attributes.Set( "SdfCull", CullEnabled ? 1 : 0 );
			// Displacement stays OFF (its bake attributes default to no boil): the meshed LODs have never carried
			// the clay lumps, only the marched surface does, and baking them in would change how the LOD looks.

			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

			// Pass 1: every dispatch, into its OWN volume so they can't overwrite each other.
			Span<bool> queued = stackalloc bool[MaxSlots];
			for ( int s = 0; s < slots; s++ )
			{
				ref readonly var spec = ref specs[s];
				if ( !spec.IsValid || dst[s] is null || dst[s].Length < spec.Floats )
					continue;
				if ( !EnsureGrid( s, spec.Gx, spec.Gy, spec.Gz ) )
					continue;

				var maxs = spec.Mins + new Vector3( (spec.Gx - 1) * spec.Cell, (spec.Gy - 1) * spec.Cell, (spec.Gz - 1) * spec.Cell );
				_cs.Attributes.Set( "MeshGridOut", Grids[s] );
				_cs.Attributes.Set( "GridMin", spec.Mins );
				_cs.Attributes.Set( "GridMax", maxs );
				_cs.Attributes.Set( "GridDims", new Vector3( spec.Gx, spec.Gy, spec.Gz ) );
				// Only points that could be a corner of a surface-crossing cell need real attributes; the caller's
				// band covers every such corner, and skipping the rest skips most of the grid.
				_cs.Attributes.Set( "AttrBand", spec.AttrBand );
				_cs.Dispatch( spec.Gx, spec.Gy, spec.Gz );
				queued[s] = true;
			}

			long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

			// Pass 2: every readback. The first one pays the drain for all of them.
			for ( int s = 0; s < slots; s++ )
			{
				if ( !queued[s] )
					continue;
				ref readonly var spec = ref specs[s];
				// One call for the whole grid. The 3D readback is implemented as one engine call per Z slice, so
				// reading a volume costs tens of GPU round trips; stacking the slices down Y costs one.
				// The dstSize overload specifically: the dstRect/dstStride one sizes its bounds check with a
				// format table that has no RGBA32F entry and throws NotImplementedException on sight of one.
				Grids[s].GetPixels<float>( (0, 0, spec.Gx, spec.Gy * spec.Gz), 0, 0,
					dst[s].AsSpan( 0, spec.Floats ), ImageFormat.RGBA32323232F, (spec.Gx, spec.Gy * spec.Gz) );
				results[s] = true;
			}

			double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			LastDispatchMs = (t1 - t0) * toMs;
			LastReadbackMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * toMs;
		}
		catch ( Exception e )
		{
			// One warning, then stay on the CPU path for the rest of the session — a per-mesh exception spam
			// during a scene load is worse than the slow path.
			_failed = true;
			Array.Clear( results, 0, results.Length );
			Log.Warning( $"[SdfMeshGridGpu] GPU mesh sampling unavailable, falling back to the CPU evaluator: {e.Message}" );
		}
	}

	// Grow-only, per slot: the texture is reallocated when a grid needs more room, and smaller grids write and read
	// back the top-left sub-rect of it. Every prop in a level would otherwise be a texture creation per LOD. Slots
	// keep their own texture because the LODs are all dispatched before any of them is read, so they must not share
	// storage. 2D with the Z slices stacked down Y — see the readback comment for why that matters.
	static bool EnsureGrid( int slot, int gx, int gy, int gz )
	{
		int w = gx, h = gy * gz;
		if ( Grids[slot].IsValid() && GridX[slot] >= w && GridY[slot] >= h )
			return true;

		int nw = Math.Max( GridX[slot], w ), nh = Math.Max( GridY[slot], h );
		var tex = Texture.Create( nw, nh ).WithFormat( ImageFormat.RGBA32323232F )
			.WithUAVBinding().WithName( $"sdf_mesh_grid_{slot}" ).Finish();
		if ( !tex.IsValid() )
			return false;

		Grids[slot] = tex;
		GridX[slot] = nw; GridY[slot] = nh;
		return true;
	}

	static void EnsureBrushTextures()
	{
		if ( _brushTex is null || _brushTex.Width != MaxBrushes * TexelsPerBrush )
			_brushTex = Texture.Create( MaxBrushes * TexelsPerBrush, 1 ).WithFormat( ImageFormat.RGBA32323232F ).WithDynamicUsage().Finish();
		if ( _splineTex is null || _splineTex.Width != MaxSplinePoints )
			_splineTex = Texture.Create( MaxSplinePoints, 1 ).WithFormat( ImageFormat.RGBA32323232F ).WithDynamicUsage().Finish();
		_data ??= new float[MaxBrushes * TexelsPerBrush * 4];
		_spline ??= new float[MaxSplinePoints * 4];
	}
}
