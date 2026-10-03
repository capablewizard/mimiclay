//=========================================================================================================================
// Log-average luminance of a DDGI irradiance atlas, the pivot for ddgi_grade_cs's contrast. One group of 64 threads
// strides the whole atlas (it's tiny — a few hundred K texels), reduces in groupshared, and writes
// (exp(mean log lum), texel count) to a 1x1 texture. Log-average so a few hot texels near a light don't drag the
// pivot up. Dead probes (inside geometry) are skipped. Dispatched by ProbeRadiosityBoost as Dispatch( 8, 8, 1 ).
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"

	Texture3D<float4>   g_tIn         < Attribute( "LumIn" ); >;         // boosted atlas, RGBA16F
	Texture3D<float4>   g_tRelocation < Attribute( "LumRelocation" ); >; // w > 0.5 = probe active
	RWTexture2D<float4> g_tStats      < Attribute( "LumStats" ); >;      // 1x1: x = log-average lum, y = count
	float3 g_vDims < Attribute( "LumDims" ); >;
	int g_nUseRelocation < Attribute( "LumUseRelocation" ); Default( 1 ); >;

	#define IRR_RES 8
	#define THREADS 64

	groupshared float2 g_Sum[THREADS];

	[numthreads( 8, 8, 1 )]
	void MainCs( uint3 threadId : SV_GroupThreadID )
	{
		uint tid = threadId.y * 8 + threadId.x;
		uint3 dims = (uint3)( g_vDims + 0.5f );
		uint total = dims.x * dims.y * dims.z;

		float2 acc = 0.0f;
		for ( uint i = tid; i < total; i += THREADS )
		{
			uint3 c = uint3( i % dims.x, ( i / dims.x ) % dims.y, i / ( dims.x * dims.y ) );

			if ( g_nUseRelocation != 0 && g_tRelocation.Load( int4( c.xy / IRR_RES, c.z, 0 ) ).w <= 0.5f )
				continue;

			float lum = dot( g_tIn.Load( int4( c, 0 ) ).rgb, float3( 0.2126f, 0.7152f, 0.0722f ) );
			acc += float2( log( max( lum, 1e-4f ) ), 1.0f );
		}

		g_Sum[tid] = acc;
		GroupMemoryBarrierWithGroupSync();

		[unroll]
		for ( uint s = THREADS / 2; s > 0; s >>= 1 )
		{
			if ( tid < s )
				g_Sum[tid] += g_Sum[tid + s];
			GroupMemoryBarrierWithGroupSync();
		}

		if ( tid == 0 )
		{
			float2 sum = g_Sum[0];
			float avg = sum.y > 0.0f ? exp( sum.x / sum.y ) : 0.0f;
			g_tStats[uint2( 0, 0 )] = float4( avg, sum.y, 0.0f, 0.0f );
		}
	}
}
