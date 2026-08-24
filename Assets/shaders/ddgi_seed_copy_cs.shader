//=========================================================================================================================
// DDGI bounce-bake seed copy. The engine saves baked irradiance to disk as BC6H, so the previous pass can't be
// Graphics.CopyTexture'd into the next pass's RGBA16F bake target (format mismatch) and can't be read back on the
// CPU either. The GPU decompresses BC6H natively on Load, so this trivially converts: one thread per voxel, load
// from the compressed volume, store into the UAV volume. Dispatched by ProbeBounceBaker between bake passes.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"

	Texture3D<float4>   g_tSource < Attribute( "SeedSource" ); >; // previous pass, any sampleable format (BC6H)
	RWTexture3D<float4> g_tDest   < Attribute( "SeedDest" ); >;   // in-progress bake target, RGBA16F UAV
	float3 g_vDims < Attribute( "SeedDims" ); >;
	// Radiosity gain: multiplies the seeded irradiance, so every capture in the next pass sees an amplified
	// version of last pass's lighting. Compounds across passes — the fake-radiosity dial. 1 = physical.
	float g_flGain < Attribute( "SeedGain" ); Default( 1.0 ); >;

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint3 dims = (uint3)( g_vDims + 0.5 );
		if ( any( id >= dims ) )
			return;

		float4 seed = g_tSource.Load( int4( id, 0 ) );
		g_tDest[id] = float4( seed.rgb * g_flGain, seed.a );
	}
}
