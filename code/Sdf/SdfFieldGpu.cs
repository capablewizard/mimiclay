using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// GPU field evaluator — the Dreams "evaluator / CS of doom" ported to mimiclay. Instead of re-baking the
/// distance field on the CPU and uploading it every edit (which scales with the dirty VOLUME on one thread),
/// this dispatches a compute shader (shaders/sdf_field_cs) that evaluates the whole brush list into a UAV 3D
/// volume in parallel — one thread per voxel. The cost becomes GPU throughput, so it scales to big shapes and
/// hundreds of brushes, and there's no CPU eval and no CPU→GPU upload of the field at all.
///
/// Brushes are packed in the prop's LOCAL (model) space (identity transform), so the field is placement-
/// invariant and the raymarch's existing WorldToModelPos sampling is unchanged. The per-brush AABB cull is
/// left OFF here: with it on, voxels far from every brush keep the 1e9 "empty" sentinel (an over-estimate that
/// makes sphere tracing overshoot) — evaluating every brush per voxel is cheap on the GPU and gives an exact,
/// valid field everywhere.
/// </summary>
public sealed class SdfFieldGpu
{
	const int MaxBrushes = SdfBrushPacker.MaxBrushes;
	const int TexelsPerBrush = 7;
	const int MaxSplinePoints = SdfBrushPacker.MaxSplinePoints;

	/// <summary>Voxels per brick edge (the sparse-brick block size). The occupancy volume marks which of these
	/// 8³ bricks the surface passes through.</summary>
	public const int Block = 8;

	/// <summary>Resolution of the low-res GUIDE field built for the sparse path (empty-space sphere-tracing). The
	/// brick grid + atlas stay at the full surface resolution; the guide only needs to be coarse enough to take big
	/// steps through open space, where the two-phase march hands off to the high-res atlas near the surface. Keeping
	/// this small is what drops the per-edit eval cost (and memory) once the surface resolution climbs past it.</summary>
	public const int GuideResolution = 64;

	// Sparse atlas (approach B). Each tile holds Block+1 samples per axis (the inclusive far corner, shared with
	// the neighbour brick, so trilinear is correct up to the boundary). Tiles pack into a 16×16×Z grid whose DEPTH
	// (and thus the tile budget + atlas memory) scales with resolution: ~6 MB at 128, up to ~95 MB at 512 — instead of
	// a fixed worst-case allocation. Z is capped at MaxTilesZ (=128 → 32768 tiles, atlas (144,144,1152) ≈ 95 MB).
	public const int TileSize = Block + 1;
	public const int TilesX = 16, TilesY = 16;
	const int MaxTilesZ = 128; // atlas-Z capacity cap (tiles = TilesX*TilesY*MaxTilesZ = 32768 max)

	/// <summary>Tile budget per unit of brick face area (≈ max brick face). The shell scales with surface area, so the
	/// atlas is sized as TileBudgetPerFace × maxBrickDim². Lower = less VRAM, but too low risks a denser prop's shell
	/// overflowing the atlas (overflow tiles get -1 → holes). 6 keeps ~2× headroom over a typical head at 512.</summary>
	const int TileBudgetPerFace = 6;

	/// <summary>Extra padding (inches) added around the content bounds so a smooth-blend bulge stays inside the
	/// evaluated volume rather than being clipped at the grid edge. The proxy is grown to match (see the renderer).</summary>
	public const float BlendPad = 16f;

	// What the raymarch binds (same contract as SdfFieldBaker.BakedField).
	public Texture Texture { get; private set; }
	public Vector3 Mins { get; private set; }
	public Vector3 Maxs { get; private set; }
	public Vector3 Dims { get; private set; }
	// High-res surface grid that sizes the brick grid + atlas. Equals Dims today; in 4d-2 the field Texture drops to a
	// lower-res guide while this stays high, so the brick/atlas keep full detail.
	public Vector3 SurfaceDims { get; private set; }

	public bool IsValid => Texture.IsValid();

	Texture _brushTex, _splineTex;
	readonly SdfTextAtlas _textAtlas = new(); // baked text distance fields (Text brushes), bound as "TextSdf"
	ComputeShader _cs;
	int _nx, _ny, _nz;
	float _heldCell;   // voxel size carried across Evaluate calls (see the hold band in Evaluate)
	int _heldCellRes;  // resolution the held cell was computed for — a resolution change re-derives it
	int _brushCount;        // packed brush count from the last Evaluate (the fill re-evals the same set)
	float[] _data, _spline;

	// Sparse-brick classification: a small per-brick occupancy volume (1 = surface brick) produced by a second
	// compute pass after the field eval. The foundation for the brick atlas + render.
	public Texture Occupancy { get; private set; }
	int _bx, _by, _bz; // occupancy (brick) dims
	ComputeShader _classifyCs;

	// Sparse atlas: surface bricks compacted into atlas tiles + an indirection table (brick→tile). All GPU-built
	// each edit (no readback), so they stay in sync with the field. Bound to the raymarch sampler in Step 3.
	public Texture Atlas { get; private set; }
	public Texture IndirectionTex { get; private set; } // brick -> tile (R32F, -1 = empty); a TEXTURE not a buffer
	GpuBuffer _counter;                                  // because structured buffers don't bind to a material PS
	GpuBuffer _tileToBrick; int _tileToBrickCount;       // reverse map tile→brick, so the fill runs one thread per voxel
	ComputeShader _allocCs, _fillCs;
	int _indBx, _indBy, _indBz, _atlasX, _atlasY, _atlasZ;

	public Vector3 BrickDims => new( _bx, _by, _bz );
	public Vector3 AtlasDims => new( _atlasX, _atlasY, _atlasZ );

	/// <summary>Re-evaluate the field for the given brushes over the local AABB [localMin,localMax]. Sizes the
	/// volume, packs the brushes (local), and dispatches the compute shader over every voxel. Returns false if it
	/// couldn't (no compute shader / texture creation failed) so the caller can fall back to the analytic march.</summary>
	public bool Evaluate( List<SdfBrush> brushes, Vector3 localMin, Vector3 localMax, int resolution, bool buildSparse )
	{
		if ( brushes is null || brushes.Count == 0 )
			return false;

		// Sparse stores only the surface shell, so it can go to 512; the dense path stays capped at 256 because a full
		// 512³ R32F volume is ~537 MB (the whole reason the sparse path exists).
		resolution = Math.Clamp( resolution, 8, buildSparse ? 512 : 256 );

		// Pad the bounds so blend bulges stay inside, then size a grid that keeps voxels ~cubic.
		var mn = localMin - BlendPad;
		var mx = localMax + BlendPad;
		var span = mx - mn;
		float maxAxis = MathF.Max( span.x, MathF.Max( span.y, span.z ) );
		if ( maxAxis <= 0f )
			return false;

		// Surface grid (high res): sizes the brick grid + atlas. Its inclusive far corner sets Maxs, which the guide
		// shares so both sample the same [Mins,Maxs] bounds.
		float surfCell = maxAxis / resolution;

		// HOLD the previous cell size while the bounds wobble within a band. Dragging one brush stretches the
		// union bounds continuously, and a cell that tracks them rescales the whole field every frame — thin
		// features (text strokes especially) visibly fatten/thin while a brush merely MOVES. Held until the
		// finer cell would overrun the voxel budget by >10% per axis, or leave >20% of it unused.
		if ( resolution == _heldCellRes && _heldCell > 0f && surfCell < _heldCell * 1.1f && surfCell > _heldCell * 0.8f )
			surfCell = _heldCell;
		_heldCell = surfCell;
		_heldCellRes = resolution;

		// Snap the grid origin down to a cell multiple, so static brushes resample at identical phases while a
		// drag slides the bounds min around inside one cell (costs at most one voxel per axis).
		mn = new Vector3(
			MathF.Floor( mn.x / surfCell ) * surfCell,
			MathF.Floor( mn.y / surfCell ) * surfCell,
			MathF.Floor( mn.z / surfCell ) * surfCell );
		span = mx - mn;

		int snx = Math.Max( 2, (int)MathF.Ceiling( span.x / surfCell ) + 1 );
		int sny = Math.Max( 2, (int)MathF.Ceiling( span.y / surfCell ) + 1 );
		int snz = Math.Max( 2, (int)MathF.Ceiling( span.z / surfCell ) + 1 );

		Mins = mn;
		// Maxs lands on the inclusive far corner sample (so the shader's UVW remap matches the baker).
		Maxs = mn + new Vector3( (snx - 1) * surfCell, (sny - 1) * surfCell, (snz - 1) * surfCell );
		SurfaceDims = new Vector3( snx, sny, snz );

		EnsureBrushTextures();
		int count = SdfBrushPacker.Pack( brushes, Transform.Zero, _data, _spline, MaxBrushes, TexelsPerBrush, MaxSplinePoints, _textAtlas );
		_brushCount = count;
		_brushTex.Update<float>( _data, 0, 0, MaxBrushes * TexelsPerBrush, 1 );
		_splineTex.Update<float>( _spline, 0, 0, MaxSplinePoints, 1 );

		// The field TEXTURE is the full dense field (dense path) OR a low-res GUIDE for empty-space stepping (sparse
		// path) — the brick grid + atlas keep the high surface res either way. Both span the same [Mins,Maxs] bounds.
		int fnx = snx, fny = sny, fnz = snz;
		if ( buildSparse )
		{
			int gres = Math.Min( resolution, GuideResolution );
			float gcell = maxAxis / gres;
			fnx = Math.Max( 2, (int)MathF.Round( span.x / gcell ) + 1 );
			fny = Math.Max( 2, (int)MathF.Round( span.y / gcell ) + 1 );
			fnz = Math.Max( 2, (int)MathF.Round( span.z / gcell ) + 1 );
		}

		if ( !EnsureVolume( fnx, fny, fnz ) )
			return false;
		Dims = new Vector3( fnx, fny, fnz );

		_cs ??= new ComputeShader( "sdf_field_cs" );
		if ( _cs is null )
			return false;

		_cs.Attributes.Set( "FieldOut", Texture );
		_cs.Attributes.Set( "BrushData", _brushTex );
		_cs.Attributes.Set( "SplineData", _splineTex );
		_cs.Attributes.Set( "TextSdf", _textAtlas.Texture );
		_cs.Attributes.Set( "BrushCount", count );
		_cs.Attributes.Set( "SdfCull", 0 ); // off — exact field everywhere (see class summary)
		_cs.Attributes.Set( "FieldMin", Mins );
		_cs.Attributes.Set( "FieldMax", Maxs ); // guide/field spans the SAME bounds as the surface grid
		_cs.Attributes.Set( "FieldDims", Dims );
		_cs.Dispatch( fnx, fny, fnz ); // thread counts (s&box divides by the shader's numthreads)

		// The classify→alloc→fill passes only feed the sparse atlas. Skip them entirely when the prop isn't using
		// sparse — otherwise every edit pays to build an atlas the dense path never samples. Run at the SURFACE res.
		if ( buildSparse )
			ClassifyBricks( snx, sny, snz, surfCell );
		return true;
	}

	// Mark which 8³ bricks the surface passes through (the shell). Runs after the field dispatch, reading it back.
	void ClassifyBricks( int nx, int ny, int nz, float cell )
	{
		int bx = ((nx - 1) + Block - 1) / Block;
		int by = ((ny - 1) + Block - 1) / Block;
		int bz = ((nz - 1) + Block - 1) / Block;

		if ( !Occupancy.IsValid() || _bx != bx || _by != by || _bz != bz )
		{
			Occupancy = Texture.CreateVolume( bx, by, bz, ImageFormat.R32F )
				.WithUAVBinding().WithName( "sdf_brick_occupancy" ).Finish();
			if ( !Occupancy.IsValid() )
				return;
			_bx = bx; _by = by; _bz = bz;
		}

		_classifyCs ??= new ComputeShader( "sdf_brick_classify_cs" );
		if ( _classifyCs is null )
			return;

		// Direct eval (no dense-field read) — same brush inputs + bounds as the field eval. Sample grid matches the
		// field exactly so occupancy is identical.
		_classifyCs.Attributes.Set( "BrushData", _brushTex );
		_classifyCs.Attributes.Set( "SplineData", _splineTex );
		_classifyCs.Attributes.Set( "TextSdf", _textAtlas.Texture );
		_classifyCs.Attributes.Set( "BrushCount", _brushCount );
		// Cull ON: classify/fill evaluate near the surface, where each point is dominated by 1-2 brushes, so skipping
		// far brushes' AABBs is exact AND much cheaper. (The guide eval keeps cull OFF — far voxels need the exact field.)
		_classifyCs.Attributes.Set( "SdfCull", 1 );
		_classifyCs.Attributes.Set( "Occupancy", Occupancy );
		_classifyCs.Attributes.Set( "FieldMin", Mins );
		_classifyCs.Attributes.Set( "FieldMax", Maxs );
		_classifyCs.Attributes.Set( "FieldDims", SurfaceDims ); // high-res grid (Dims is now the low-res guide)
		_classifyCs.Attributes.Set( "Block", Block );
		_classifyCs.Attributes.Set( "Band", cell ); // one-voxel surface band
		_classifyCs.Dispatch( bx, by, bz );

		AllocateAndFill();
	}

	// Compact surface bricks into atlas tiles + indirection (pass 1), then copy their voxels from the dense field
	// into the atlas (pass 2). Both GPU-driven, no readback — the atlas/indirection track the field each edit.
	void AllocateAndFill()
	{
		if ( !Occupancy.IsValid() || _bx <= 0 )
			return;

		// Size the tile budget to this resolution's shell. The shell scales ~with surface area (the largest brick
		// face ≈ maxBrickDim²). Capped at MaxTilesZ layers so memory stays bounded. Round up to whole 16×16 layers.
		int maxBrickDim = Math.Max( _bx, Math.Max( _by, _bz ) );
		int tilesZ = Math.Clamp( (TileBudgetPerFace * maxBrickDim * maxBrickDim + TilesX * TilesY - 1) / (TilesX * TilesY), 8, MaxTilesZ );
		int maxTiles = TilesX * TilesY * tilesZ;
		int ax = TilesX * TileSize, ay = TilesY * TileSize, az = tilesZ * TileSize;

		_counter ??= new GpuBuffer( 1, 4, GpuBuffer.UsageFlags.Structured, "sdf_brick_counter" );
		if ( _tileToBrick is null || _tileToBrickCount != maxTiles )
		{
			// Reverse map tile→brick: lets the fill run one thread per tile-voxel (the alloc writes it on claim).
			_tileToBrick?.Dispose();
			_tileToBrick = new GpuBuffer( maxTiles, 4, GpuBuffer.UsageFlags.Structured, "sdf_tile_to_brick" );
			_tileToBrickCount = maxTiles;
		}
		if ( !IndirectionTex.IsValid() || _indBx != _bx || _indBy != _by || _indBz != _bz )
		{
			// 2D (NOT a volume): a second user volume texture bound to the material PS aliases the dense field
			// (engine limit). A 2D texture uses the separate slot pool the brush-data texture already reads from.
			// Layout: x = brick.x, y = brick.y + brickDims.y*brick.z. Width=bx, height=by*bz.
			IndirectionTex = Texture.Create( _bx, _by * _bz ).WithFormat( ImageFormat.R32F )
				.WithUAVBinding().WithName( "sdf_brick_indirection" ).Finish();
			if ( !IndirectionTex.IsValid() )
				return;
			_indBx = _bx; _indBy = _by; _indBz = _bz;
		}
		if ( !Atlas.IsValid() || _atlasZ != az ) // ax/ay are fixed; only the depth scales with resolution
		{
			Atlas = Texture.CreateVolume( ax, ay, az, ImageFormat.R32F ).WithUAVBinding().WithName( "sdf_atlas" ).Finish();
			if ( !Atlas.IsValid() )
				return;
			_atlasX = ax; _atlasY = ay; _atlasZ = az;
		}

		_counter.SetData<uint>( new uint[] { 0u } ); // reset the atomic tile counter

		_allocCs ??= new ComputeShader( "sdf_brick_alloc_cs" );
		if ( _allocCs is null )
			return;
		_allocCs.Attributes.Set( "Occupancy", Occupancy );
		_allocCs.Attributes.Set( "IndirectionTex", IndirectionTex );
		_allocCs.Attributes.Set( "Counter", _counter );
		_allocCs.Attributes.Set( "TileToBrick", _tileToBrick );
		_allocCs.Attributes.Set( "BrickDims", BrickDims );
		_allocCs.Attributes.Set( "MaxTiles", maxTiles );
		_allocCs.Dispatch( _bx, _by, _bz );

		_fillCs ??= new ComputeShader( "sdf_atlas_fill_cs" );
		if ( _fillCs is null )
			return;
		// Direct eval, one thread per tile-voxel (no dense field read). The reverse map + GPU tile counter let the fill
		// dispatch over (maxTiles × TileSize³) threads and process only allocated tiles — saturating the GPU instead of
		// ~10K threads each running a 9³ serial loop.
		_fillCs.Attributes.Set( "BrushData", _brushTex );
		_fillCs.Attributes.Set( "SplineData", _splineTex );
		_fillCs.Attributes.Set( "TextSdf", _textAtlas.Texture );
		_fillCs.Attributes.Set( "BrushCount", _brushCount );
		_fillCs.Attributes.Set( "SdfCull", 1 ); // near-surface evals — cull far brushes (exact + much cheaper)
		_fillCs.Attributes.Set( "Counter", _counter );
		_fillCs.Attributes.Set( "TileToBrick", _tileToBrick );
		_fillCs.Attributes.Set( "Atlas", Atlas );
		_fillCs.Attributes.Set( "FieldMin", Mins );
		_fillCs.Attributes.Set( "FieldMax", Maxs );
		_fillCs.Attributes.Set( "FieldDims", SurfaceDims ); // high-res grid (Dims is now the low-res guide)
		_fillCs.Attributes.Set( "BrickDims", BrickDims );
		_fillCs.Attributes.Set( "Block", Block );
		_fillCs.Attributes.Set( "TileSize", TileSize );
		_fillCs.Attributes.Set( "TilesX", TilesX );
		_fillCs.Attributes.Set( "TilesY", TilesY );
		_fillCs.Dispatch( maxTiles, TileSize * TileSize * TileSize, 1 ); // x=tile, y=voxel-in-tile
	}

	/// <summary>Read the allocated surface-brick (tile) count back to the CPU. Debug/validation only — a sync stall.</summary>
	public int ReadBrickCount()
	{
		if ( _counter is null )
			return -1;
		var c = new uint[1];
		_counter.GetData<uint>( c );
		return (int)c[0];
	}

	/// <summary>Read the brick occupancy back to the CPU (1 = surface brick), x-fastest. Debug/inspection only —
	/// a synchronous GPU→CPU stall; don't call in the hot path. Returns null if not ready.</summary>
	public float[] ReadOccupancy( out int bx, out int by, out int bz )
	{
		bx = _bx; by = _by; bz = _bz;
		if ( !Occupancy.IsValid() || bx <= 0 )
			return null;

		var data = new float[bx * by * bz];
		Occupancy.GetPixels3D( (0, 0, 0, bx, by, bz), 0, data.AsSpan(), ImageFormat.R32F );
		return data;
	}

	/// <summary>Read back the indirection + atlas and report what's actually on the GPU — to diagnose the sparse
	/// path directly instead of inferring from pixels. Reports how many bricks the indirection marks occupied
	/// (should match the surface count), the max tile index, and an end-to-end voxel check: the first occupied
	/// brick's atlas tile vs the dense field at the same voxel (they should be equal if the fill is correct).
	/// Synchronous readbacks — debug only.</summary>
	public string DebugStats()
	{
		if ( !IndirectionTex.IsValid() || _indBx <= 0 )
			return "no indirection";

		// Indirection is a 2D texture (width=_indBx, height=_indBy*_indBz); read it flat. The linear index still
		// decodes as the 3D brick coord, so the occupied count is unchanged.
		var ind = new float[_indBx * _indBy * _indBz];
		IndirectionTex.GetPixels3D( (0, 0, 0, _indBx, _indBy * _indBz, 1), 0, ind.AsSpan(), ImageFormat.R32F );

		int occ = 0, fb = -1;
		float maxTile = -1f;
		for ( int i = 0; i < ind.Length; i++ )
			if ( ind[i] >= 0f ) { occ++; if ( ind[i] > maxTile ) maxTile = ind[i]; if ( fb < 0 ) fb = i; }

		string s = $"indir occupied={occ} maxTile={maxTile:0}";

		// Sample the first occupied brick's atlas tile (sanity: a finite distance, not garbage). No longer compared to
		// the field texture — that's now the low-res guide, not the surface field the atlas was built from.
		if ( fb >= 0 && Atlas.IsValid() )
		{
			int tile = (int)(ind[fb] + 0.5f);
			int tcx = tile % TilesX, tcy = (tile / TilesX) % TilesY, tcz = tile / (TilesX * TilesY);
			var av = new float[1];
			Atlas.GetPixels3D( (tcx * TileSize + 1, tcy * TileSize + 1, tcz * TileSize + 1, 1, 1, 1), 0, av.AsSpan(), ImageFormat.R32F );
			s += $"; tile{tile} centre atlas={av[0]:0.###}";
		}

		return s;
	}

	bool EnsureVolume( int nx, int ny, int nz )
	{
		if ( Texture.IsValid() && _nx == nx && _ny == ny && _nz == nz )
			return true;

		Texture = Texture.CreateVolume( nx, ny, nz, ImageFormat.R32F )
			.WithUAVBinding()
			.WithName( "sdf_field_gpu" )
			.Finish();

		if ( !Texture.IsValid() )
			return false;

		_nx = nx; _ny = ny; _nz = nz;
		return true;
	}

	void EnsureBrushTextures()
	{
		if ( _brushTex is null || _brushTex.Width != MaxBrushes * TexelsPerBrush )
			_brushTex = Texture.Create( MaxBrushes * TexelsPerBrush, 1 ).WithFormat( ImageFormat.RGBA32323232F ).WithDynamicUsage().Finish();
		if ( _splineTex is null || _splineTex.Width != MaxSplinePoints )
			_splineTex = Texture.Create( MaxSplinePoints, 1 ).WithFormat( ImageFormat.RGBA32323232F ).WithDynamicUsage().Finish();
		_data ??= new float[MaxBrushes * TexelsPerBrush * 4];
		_spline ??= new float[MaxSplinePoints * 4];
	}
}

/// <summary>
/// Packs an <see cref="SdfBrush"/> list into the flat RGBA32F arrays the raymarch / compute shaders read (7
/// texels per brush + a shared spline control-point pool). ONE definition shared by every consumer — and since
/// the raymarch went local-space (it folds world samples via ModelOrigin/ModelRotation), every consumer packs
/// with <c>Transform.Zero</c>: the renderer's march + colour AND the GPU field evaluator read identical
/// local-space data evaluated by the same sdf_eval.hlsl. The <c>tx</c> parameter remains for generality.
/// </summary>
public static class SdfBrushPacker
{
	/// <summary>The hard cap on brushes one sculpture can render — the GPU brush texture's size. THE canonical
	/// limit: <see cref="SdfSculpture.AddBrush"/> refuses past it, shot carving skips at it, and
	/// <see cref="SdfNetworkSync"/> rejects oversized payloads, because a brush past the cap is silently NOT
	/// packed — the raymarched shape would diverge from the meshed/collision/networked shape with no warning.
	/// Was 64; raised to 128 (headroom for shot carving on sculpted disguises) once the sync payloads were
	/// gzip'd — the commit snapshot scales linearly with this cap and was the binding constraint. The other
	/// linear costs (per-edit field/mesh/collider rebuild, the per-pixel colour loop) grow with ACTUAL brush
	/// count, not the cap, and the per-brush AABB cull keeps small carve spheres cheap.</summary>
	public const int MaxBrushes = 128;

	/// <summary>Cap on spline control points pooled across all spline brushes of one sculpture (GPU pool size).</summary>
	public const int MaxSplinePoints = 256;

	// Truncation warnings, throttled — Pack runs per frame during drags, one line every few seconds is plenty.
	static RealTimeSince _sinceTruncWarn = 999f;

	public static int Pack( List<SdfBrush> brushes, Transform tx, float[] data, float[] spline,
		int maxBrushes, int texelsPerBrush, int maxSplinePoints, SdfTextAtlas textAtlas = null )
	{
		Array.Clear( data, 0, data.Length );
		Array.Clear( spline, 0, spline.Length );
		int splineWritten = 0;
		int textSlot = 0; // atlas slots assigned in brush order — deterministic, so every pack agrees
		int written = 0;

		int k = 0;
		for ( ; k < brushes.Count && written < maxBrushes; k++ )
		{
			var b = brushes[k];
			if ( !b.Enabled )
				continue;

			var pos = tx.PointToWorld( b.Position );
			var rot = tx.Rotation * b.Rotation;
			int o = written * texelsPerBrush * 4;

			data[o + 0] = pos.x; data[o + 1] = pos.y; data[o + 2] = pos.z;
			data[o + 3] = (int)b.Shape;

			if ( b.Shape == SdfShape.Spline )
			{
				int count = 0, offset = splineWritten;
				if ( b.Points is { } pts )
				{
					for ( int i = 0; i < pts.Count && splineWritten < maxSplinePoints; i++ )
					{
						var wp = tx.PointToWorld( new Vector3( pts[i].x, pts[i].y, pts[i].z ) );
						int so = splineWritten * 4;
						spline[so + 0] = wp.x; spline[so + 1] = wp.y; spline[so + 2] = wp.z; spline[so + 3] = pts[i].w;
						splineWritten++;
						count++;
					}
				}
				data[o + 4] = offset; data[o + 5] = count; data[o + 6] = b.Curvature;
			}
			else
			{
				data[o + 4] = b.Size.x; data[o + 5] = b.Size.y; data[o + 6] = b.Size.z;
			}
			data[o + 7] = b.Blend;

			data[o + 8] = rot.x; data[o + 9] = rot.y; data[o + 10] = rot.z; data[o + 11] = rot.w;

			data[o + 12] = SrgbToLinear( b.Color.r );
			data[o + 13] = SrgbToLinear( b.Color.g );
			data[o + 14] = SrgbToLinear( b.Color.b );
			data[o + 15] = b.Operation == SdfOperation.Subtract ? 1f : 0f;

			data[o + 16] = b.Shape == SdfShape.Spline ? (b.SplineClosed ? 1f : 0f) : b.Rounding;
			data[o + 17] = b.Metallic;
			data[o + 18] = b.Roughness;
			data[o + 19] = (b.MirrorX ? 1 : 0) | (b.MirrorY ? 2 : 0) | (b.MirrorZ ? 4 : 0);

			BrushWorldAabb( b, tx, out var wmn, out var wmx );
			data[o + 20] = wmn.x; data[o + 21] = wmn.y; data[o + 22] = wmn.z;

			// The AABB-min texel's free lane is shape-dependent: the extruded profile id, or the text
			// brush's atlas slot. Text also bakes/refreshes its distance field here — this is the ONE
			// main-thread point every consumer passes through, so worker snapshots always see TextData.
			if ( b.Shape == SdfShape.Text )
			{
				b.TextData = SdfTextSdf.Get( b.Text, b.Font );
				int slot = Math.Min( textSlot++, SdfTextSdf.MaxSlots - 1 );
				textAtlas?.Set( slot, b.TextData );
				data[o + 23] = slot;
			}
			else
				data[o + 23] = (int)b.CrossSection;

			data[o + 24] = wmx.x; data[o + 25] = wmx.y; data[o + 26] = wmx.z;
			data[o + 27] = b.Slice; // planar slice fraction (sphere/cone), riding the AABB-max texel's free lane
			written++;
		}

		// Anything enabled left unpacked means the rendered shape has silently diverged from the authored one
		// (authoring + network apply cap at MaxBrushes, so reaching this is a bug or a hand-edited asset) — say so.
		for ( ; k < brushes.Count; k++ )
		{
			if ( !brushes[k].Enabled )
				continue;
			if ( _sinceTruncWarn > 5f )
			{
				_sinceTruncWarn = 0f;
				Log.Warning( $"SdfBrushPacker: {brushes.Count} brushes exceed the {maxBrushes}-brush cap — extras are NOT rendered and the visible shape no longer matches the mesh/collision." );
			}
			break;
		}

		return written;
	}

	static float SrgbToLinear( float c )
		=> c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );

	// World/transformed AABB enclosing the brush, its mirror copies and its blend bulge — the per-brush cull bound.
	static void BrushWorldAabb( SdfBrush b, Transform tx, out Vector3 wmn, out Vector3 wmx )
	{
		b.LocalBounds( out var lmn, out var lmx );

		var mn = new Vector3( float.MaxValue );
		var mx = new Vector3( float.MinValue );
		int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
			for ( int sy = 0; sy <= ny; sy++ )
				for ( int sz = 0; sz <= nz; sz++ )
				{
					var lo = lmn;
					var hi = lmx;
					if ( sx == 1 ) (lo.x, hi.x) = (-hi.x, -lo.x);
					if ( sy == 1 ) (lo.y, hi.y) = (-hi.y, -lo.y);
					if ( sz == 1 ) (lo.z, hi.z) = (-hi.z, -lo.z);
					mn = Vector3.Min( mn, lo );
					mx = Vector3.Max( mx, hi );
				}

		wmn = new Vector3( float.MaxValue );
		wmx = new Vector3( float.MinValue );
		for ( int i = 0; i < 8; i++ )
		{
			var corner = new Vector3( (i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z );
			var w = tx.PointToWorld( corner );
			wmn = Vector3.Min( wmn, w );
			wmx = Vector3.Max( wmx, w );
		}
	}
}
