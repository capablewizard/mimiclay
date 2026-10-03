//=========================================================================================================================
// Final grade on the boosted DDGI irradiance atlas, run once after ddgi_radiosity_boost_cs's iterations:
//   1. Contrast — luminance pushed away from (or toward) the volume's log-average (ddgi_lum_average_cs), as a power
//      curve around that pivot so it works the same at any exposure. Scales rgb by the luminance ratio, so hue holds.
//   2. Ambient floor — texels darker than the floor get lifted toward it, brighter ones untouched. After contrast,
//      so contrast can't crush the corners the floor is there to rescue.
// Both are per-texel, and the boost pass already seam-blended edge texels to equal values, so seams stay intact.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"

	Texture3D<float4>   g_tIn    < Attribute( "GradeIn" ); >;    // boosted atlas, RGBA16F
	RWTexture3D<float4> g_tOut   < Attribute( "GradeOut" ); >;   // graded atlas, RGBA16F
	Texture2D<float4>   g_tStats < Attribute( "GradeStats" ); >; // 1x1: x = log-average lum
	float3 g_vDims < Attribute( "GradeDims" ); >;
	float  g_flContrast < Attribute( "GradeContrast" ); Default( 1.0 ); >;    // 1 = unchanged
	float3 g_vAmbientFloor < Attribute( "GradeAmbientFloor" ); Default3( 0.0, 0.0, 0.0 ); >;

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		uint3 dims = (uint3)( g_vDims + 0.5f );
		if ( any( id >= dims ) )
			return;

		const float3 lumW = float3( 0.2126f, 0.7152f, 0.0722f );

		float4 px = g_tIn.Load( int4( id, 0 ) );
		float3 result = px.rgb;

		float pivot = g_tStats.Load( int3( 0, 0, 0 ) ).x;
		float lum = dot( result, lumW );
		if ( g_flContrast != 1.0f && pivot > 0.0f && lum > 0.0f )
		{
			float graded = pivot * pow( lum / pivot, g_flContrast );
			result *= graded / lum;
		}

		float floorLum = dot( g_vAmbientFloor, lumW );
		if ( floorLum > 0.0f )
		{
			float resultLum = dot( result, lumW );
			result += g_vAmbientFloor * saturate( 1.0f - resultLum / floorLum );
		}

		g_tOut[id] = float4( result, px.a );
	}
}
