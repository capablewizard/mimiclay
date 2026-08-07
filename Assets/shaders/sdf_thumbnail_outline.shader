HEADER
{
    Description = "Thumbnail ink outline. Dilates the subject's alpha and fills the fringe with a flat colour.";
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

    // Runs on a thumbnail rendered over TRANSPARENCY, so alpha is the subject mask: solid on the prop, zero
    // everywhere else. The outline is just that mask dilated outward and filled behind the prop.
    Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;

    float3 g_vOutlineColor < Attribute( "OutlineColor" ); Default3( 0.173, 0.141, 0.094 ); >;
    float  g_flOutlineWidth < Attribute( "OutlineWidth" ); Default( 0.02 ); >;

    // Ring taps. 12 is enough that the outline reads as round rather than polygonal at icon sizes.
    #define OUTLINE_TAPS 12

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float2 uv = CalculateViewportUv( i.vPositionSs.xy );
        float4 src = g_tColorBuffer.Sample( g_sPointClamp, uv );

        if ( g_flOutlineWidth <= 0.0f )
            return src;

        // Width is a fraction of the image, not a pixel count, so the outline stays visually the same weight
        // whether this is a 56px HUD pip or a 256px library thumbnail. Corrected for aspect so it's a circle in
        // PIXELS rather than in UV.
        float2 radius = g_flOutlineWidth * float2( 1.0f, g_vRenderTargetSize.x / max( g_vRenderTargetSize.y, 1.0f ) );

        // Largest alpha anywhere within the radius. Two rings rather than one: a single ring at full radius
        // leaves a thick outline hollow in the middle, since nothing samples the gap between the taps.
        float dilated = src.a;

        [unroll]
        for ( int t = 0; t < OUTLINE_TAPS; t++ )
        {
            float a = ( 6.2831853f * t ) / OUTLINE_TAPS;
            float2 dir = float2( cos( a ), sin( a ) );

            dilated = max( dilated, g_tColorBuffer.Sample( g_sPointClamp, uv + dir * radius ).a );
            dilated = max( dilated, g_tColorBuffer.Sample( g_sPointClamp, uv + dir * radius * 0.5f ).a );
        }

        // Composite the subject over its own outline. Where the prop is solid this resolves to exactly the
        // source, so the pass only ever adds a fringe — it can't tint the prop itself.
        // The colour arrives as authored (sRGB) but we're working in linear here, hence the conversion.
        float3 ink = pow( g_vOutlineColor, 2.2f );

        return float4( lerp( ink, src.rgb, src.a ), max( src.a, dilated ) );
    }
}
