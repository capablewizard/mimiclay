//=========================================================================================================================
// Atlas fill (compute), DIRECT EVAL, ONE THREAD PER TILE-VOXEL. Dispatched over (maxTiles × TileSize³) threads: x = tile,
// y = voxel-within-tile. Each thread evaluates the SDF (shared sdf_eval.hlsl) at its single voxel and writes it — so the
// GPU stays saturated like the dense field eval, instead of ~10K threads each grinding a 9³ serial loop. The tile→brick
// reverse map (from the allocator) gives each tile its brick; the GPU tile counter bounds the work to allocated tiles
// only. No dense field is read, so the full dense volume is never required. Tiles hold TileSize = Block+1 samples per
// axis (the inclusive far corner shared with the neighbour brick) so trilinear is correct up to the brick boundary.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"
	#include "sdf_eval.hlsl"

	RWStructuredBuffer<uint> g_Counter     < Attribute( "Counter" ); >;      // [0] = allocated tile count (read-only here)
	RWStructuredBuffer<uint> g_TileToBrick < Attribute( "TileToBrick" ); >;  // tile -> brick linear index (read-only here)
	RWTexture3D<float>       g_tAtlas      < Attribute( "Atlas" ); >;

	float3 g_vBrickDims < Attribute( "BrickDims" ); >;
	float3 g_vFieldMin  < Attribute( "FieldMin" ); >;   // local pos of voxel (0,0,0)
	float3 g_vFieldMax  < Attribute( "FieldMax" ); >;   // local pos of voxel (Dims-1)
	float3 g_vFieldDims < Attribute( "FieldDims" ); >;  // voxel counts per axis
	int    g_nBlock     < Attribute( "Block" ); >;
	int    g_nTileSize  < Attribute( "TileSize" ); >;
	int    g_nTilesX    < Attribute( "TilesX" ); >;
	int    g_nTilesY    < Attribute( "TilesY" ); >;

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint tile = id.x;
		uint vlin = id.y;
		uint ts   = (uint)g_nTileSize;
		if ( tile >= g_Counter[0] || vlin >= ts * ts * ts )
			return; // past the allocated tiles, or padding threads from the numthreads round-up

		// Reverse map tile -> brick, then unflatten both the brick index and the voxel-within-tile.
		uint  brickLin = g_TileToBrick[tile];
		int3  bd    = (int3)( g_vBrickDims + 0.5 );
		int3  brick = int3( brickLin % (uint)bd.x, ( brickLin / (uint)bd.x ) % (uint)bd.y, brickLin / (uint)( bd.x * bd.y ) );
		int3  voxel = int3( vlin % ts, ( vlin / ts ) % ts, vlin / ( ts * ts ) );

		int3   dims = (int3)( g_vFieldDims + 0.5 );
		float3 cell = (g_vFieldMax - g_vFieldMin) / max( g_vFieldDims - 1.0, 1.0 );
		int3   gv   = min( brick * g_nBlock + voxel, dims - 1 );      // global voxel (clamped, matches field grid)
		float3 lp   = g_vFieldMin + (float3)gv * cell;

		int3 tc = int3( tile % (uint)g_nTilesX, ( tile / (uint)g_nTilesX ) % (uint)g_nTilesY, tile / (uint)( g_nTilesX * g_nTilesY ) );
		int3 av = tc * g_nTileSize + voxel;
		g_tAtlas[av] = SdfDistBaked( lp ); // displaced union — same value the dense/guide field bakes
	}
}
