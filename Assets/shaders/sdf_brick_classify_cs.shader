//=========================================================================================================================
// Brick classifier (compute), DIRECT EVAL, CONSERVATIVE SINGLE-SAMPLE. Marks which 8³ bricks the iso-surface passes
// through — the surface "shell". One thread per brick, ONE SDF eval at the brick centre (not 729 over a 9³ scan): the
// field is ~1-Lipschitz, so if the surface crosses the brick the distance at the centre is at most the brick
// circumradius (centre→farthest corner). Mark occupied if |d_centre| ≤ circumradius + band. This over-includes a thin
// extra layer (harmless — those tiles still hold correct values) but never drops a surface brick, and it costs one eval
// per brick instead of 729 — the difference between "GPU pegged on every edit" and "barely noticeable". No dense field.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"
	#include "sdf_eval.hlsl"

	RWTexture3D<float> g_tOccupancy < Attribute( "Occupancy" ); >;  // out: 1 = surface brick, 0 = empty/interior

	float3 g_vFieldMin  < Attribute( "FieldMin" ); >;              // local pos of voxel (0,0,0)
	float3 g_vFieldMax  < Attribute( "FieldMax" ); >;              // local pos of voxel (Dims-1)
	float3 g_vFieldDims < Attribute( "FieldDims" ); >;             // voxel counts per axis (cast to int)
	int    g_nBlock     < Attribute( "Block" ); Default( 8 ); >;
	float  g_flBand     < Attribute( "Band" ); Default( 1.0 ); >;  // extra slack (≈ one voxel) on the circumradius test

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 bid : SV_DispatchThreadID )
	{
		int3 dims = (int3)( g_vFieldDims + 0.5 );
		int3 cells = max( dims - 1, 1 );                       // cell counts (samples on inclusive corners)
		int3 brickDims = ( cells + g_nBlock - 1 ) / g_nBlock;  // ceil(cells / block)
		if ( bid.x >= (uint)brickDims.x || bid.y >= (uint)brickDims.y || bid.z >= (uint)brickDims.z )
			return;

		float3 cell = (g_vFieldMax - g_vFieldMin) / max( g_vFieldDims - 1.0, 1.0 );

		// Brick centre in local space, and the circumradius (centre to farthest corner of the Block³ cell span).
		float3 cLocal = g_vFieldMin + ( (float3)bid * g_nBlock + g_nBlock * 0.5 ) * cell;
		float  R      = 0.5 * length( (float3)g_nBlock * cell );
		float  d      = SdfDist( cLocal );

		g_tOccupancy[bid] = ( abs( d ) <= R + g_flBand ) ? 1.0 : 0.0;
	}
}
