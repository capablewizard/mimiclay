HEADER
{
    Description = "Thumbnail HDR unpack + bloom. Undoes the raymarch's c/(1+c) icon encode into a float target, ahead of the tonemap.";
    DevShader = true;
}

MODES
{
    Default();
    Forward();
}

COMMON
{
    #include "postprocess/shared.hlsl"
}

struct VertexInput
{
    float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
    float2 vTexCoord : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct PixelInput
{
    float2 uv : TEXCOORD0;

    #if ( PROGRAM == VFX_PROGRAM_VS )
        float4 vPositionPs : SV_Position;
    #endif

    #if ( PROGRAM == VFX_PROGRAM_PS )
        float4 vPositionSs : SV_Position;
    #endif
};

VS
{
    PixelInput MainVs( VertexInput i )
    {
        PixelInput o;

        o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
        o.uv = i.vTexCoord;

        return o;
    }
}

PS
{
    #include "postprocess/common.hlsl"

    // Why this exists: an icon stage renders into the ScenePanel's 8-bit target, so HDR (a full glow is ~8x)
    // clipped to 1 before SdfStagePost's tonemap ever saw it — glows came out flat yellow instead of AgX's
    // white-hot core. The subject's raymarch now writes c/(1+c) (SdfRaymarchRenderer.HdrEncode), which never
    // clips; this unpacks it into a float target so the engine's own tonemap runs on true HDR, like the main
    // camera's. The frame grab carries a blurred mip chain, which also gives us a cheap bloom.
    Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( false ); >; // g_sBilinearClamp comes from common_samplers

    // Mirrors the engine Bloom component (postprocess_bloom_cs): soft-knee luma threshold, then the mips
    // summed at Strength / (2 * MipCount). Strength 0 = no bloom.
    float  g_flBloomStrength  < Attribute( "BloomStrength" ); Default( 0.0 ); >;
    float  g_flBloomThreshold < Attribute( "BloomThreshold" ); Default( 1.0 ); >;
    float3 g_vBloomTint       < Attribute( "BloomTint" ); Default3( 1.0, 1.0, 1.0 ); >;
    int    g_nBloomMips       < Attribute( "BloomMips" ); Default( 0 ); >;

    // Inverse of the raymarch's c/(1+c). Capped just below 1 so an 8-bit 255 (which the encode can't produce
    // from finite input, but bilinear mips and the empty background can) decodes to a large finite value.
    float3 Unpack( float3 y )
    {
        y = min( max( y, 0.0 ), 254.0 / 255.0 );
        return y / ( 1.0 - y );
    }

    float Luma( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

    float3 SoftThreshold( float3 color )
    {
        float threshold = max( g_flBloomThreshold, 0.0 );
        float luma = Luma( color );
        float knee = max( threshold * 0.5, 1e-4 );
        float soft = clamp( luma - threshold + knee, 0.0, 2.0 * knee );
        soft = soft * soft / ( 4.0 * knee );
        return color * ( max( soft, luma - threshold ) / max( luma, 1e-4 ) );
    }

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float2 uv = CalculateViewportUv( i.vPositionSs.xy );
        float4 src = g_tColorBuffer.SampleLevel( g_sPointClamp, uv, 0 );
        float3 hdr = Unpack( src.rgb );

        if ( g_flBloomStrength > 0.0 && g_nBloomMips > 1 )
        {
            // Mip 0 is the sharp image; 1.. are the grab's Gaussian chain, each twice as wide as the last.
            // Each mip is unpacked AFTER filtering — not exact HDR averaging, but bloom is a soft glow and the
            // error only shades its falloff.
            float3 bloom = 0.0;
            for ( int m = 1; m < g_nBloomMips; m++ )
                bloom += SoftThreshold( Unpack( g_tColorBuffer.SampleLevel( g_sBilinearClamp, uv, m ).rgb ) );

            // Only where there's subject: the icon floats on transparency, and a halo out in alpha-0 space is
            // invisible anyway (and would feed the ink outline's alpha dilation if we raised alpha for it).
            hdr += bloom * ( g_flBloomStrength / ( 2.0 * ( g_nBloomMips - 1 ) ) ) * g_vBloomTint * src.a;
        }

        return float4( hdr, src.a );
    }
}
