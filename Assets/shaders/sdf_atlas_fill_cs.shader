//=========================================================================================================================
// Atlas fill (compute), DIRECT EVAL, ONE THREAD PER BRICK-VOXEL. Dispatched over (brick count × TileSize³) threads:
// x = brick, y = voxel-within-tile. The allocator's indirection texture supplies the compact tile index. Reading that
// texture directly avoids carrying an atomic counter and reverse-map buffer across dispatches, which produced stale
// mappings on some drivers. Empty and overflow bricks return before evaluating the SDF. Tiles hold TileSize = Block+1
// samples per axis (the inclusive far corner shared with the neighbour brick) so trilinear is correct at boundaries.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"
	#include "sdf_eval.hlsl"

	Texture2D<float>         g_tIndirection < Attribute( "IndirectionTex" ); >; // brick -> compact tile index
	RWTexture3D<float>       g_tAtlas      < Attribute( "Atlas" ); >;

	float3 g_vBrickDims < Attribute( "BrickDims" ); >;
	float3 g_vFieldMin  < Attribute( "FieldMin" ); >;   // local pos of voxel (0,0,0)
	float3 g_vFieldMax  < Attribute( "FieldMax" ); >;   // local pos of voxel (Dims-1)
	float3 g_vFieldDims < Attribute( "FieldDims" ); >;  // voxel counts per axis
	int    g_nBlock     < Attribute( "Block" ); >;
	int    g_nTileSize  < Attribute( "TileSize" ); >;
	int    g_nTilesX    < Attribute( "TilesX" ); >;
	int    g_nTilesY    < Attribute( "TilesY" ); >;
	int    g_nMaxTiles  < Attribute( "MaxTiles" ); >;

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint brickXY = id.x;
		uint vlin = id.y;
		uint ts   = (uint)g_nTileSize;
		int3 bd = (int3)( g_vBrickDims + 0.5 );
		uint brickPlane = (uint)( bd.x * bd.y );
		if ( brickXY >= brickPlane || id.z >= (uint)bd.z || vlin >= ts * ts * ts )
			return; // past the bricks, or padding threads from the numthreads round-up

		uint brickLin = brickXY + brickPlane * id.z;
		int3 brick = int3( brickLin % (uint)bd.x, ( brickLin / (uint)bd.x ) % (uint)bd.y, brickLin / (uint)( bd.x * bd.y ) );
		float ind = g_tIndirection.Load( int3( brick.x, brick.y + bd.y * brick.z, 0 ) ).r;
		if ( !(ind >= 0.0 && ind < (float)g_nMaxTiles) )
			return;
		uint tile = (uint)( ind + 0.5 );
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
