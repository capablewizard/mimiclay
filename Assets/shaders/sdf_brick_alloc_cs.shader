//=========================================================================================================================
// Brick tile allocator (compute). One thread per brick: if the brick holds surface, claim the next free atlas tile via
// an atomic counter, write its index into the 2D indirection map AND record the reverse map tile→brick (so the fill can
// run one thread per tile-voxel instead of one serial thread per brick). Empty/overflow bricks write -1. The counter
// must be reset to 0 before each dispatch (SdfFieldGpu does this), and its final value is the allocated tile count.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"

	Texture3D<float>         g_tOccupancy   < Attribute( "Occupancy" ); >;      // 1 = surface brick
	RWTexture2D<float>       g_tIndirection < Attribute( "IndirectionTex" ); >; // brick -> tile index (float), or -1
	RWStructuredBuffer<uint> g_Counter      < Attribute( "Counter" ); >;        // [0] atomic tile counter (reset each frame)
	RWStructuredBuffer<uint> g_TileToBrick  < Attribute( "TileToBrick" ); >;    // tile -> brick linear index (reverse map)

	float3 g_vBrickDims < Attribute( "BrickDims" ); >;
	int    g_nMaxTiles  < Attribute( "MaxTiles" ); >;

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 bid : SV_DispatchThreadID )
	{
		int3 bd = (int3)( g_vBrickDims + 0.5 );
		if ( bid.x >= (uint)bd.x || bid.y >= (uint)bd.y || bid.z >= (uint)bd.z )
			return;

		int2 ic = int2( bid.x, bid.y + bd.y * bid.z ); // 2D indirection coord
		float occ = g_tOccupancy.Load( int4( bid, 0 ) );

		if ( occ > 0.5 )
		{
			uint tile;
			InterlockedAdd( g_Counter[0], 1u, tile );
			if ( tile < (uint)g_nMaxTiles )
			{
				g_tIndirection[ic] = (float)tile;
				g_TileToBrick[tile] = (uint)( bid.x + bd.x * ( bid.y + bd.y * bid.z ) ); // reverse map for the fill
			}
			else
			{
				g_tIndirection[ic] = -1.0; // atlas overflow — drop this brick (renders via the guide)
			}
		}
		else
		{
			g_tIndirection[ic] = -1.0;
		}
	}
}
