HEADER
{
    Description = "FXAA (quality-style edge search). Image-space AA for the edges MSAA and the raymarch's own coverage AA can't reach.";
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

    // Runs after tonemapping, so the buffer holds display values. SrgbRead hands them back linear; Luma() takes the
    // square root to get back to a roughly perceptual scale, which is what the contrast thresholds are tuned for.
    Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;

    float g_flEdgeThreshold    < Attribute( "EdgeThreshold" );    Default( 0.125 ); >;  // local contrast needed, relative to the brightest tap
    float g_flEdgeThresholdMin < Attribute( "EdgeThresholdMin" ); Default( 0.0312 ); >; // absolute floor, skips dark noise
    float g_flSubpixel         < Attribute( "Subpixel" );         Default( 0.75 ); >;   // sub-pixel blend: 0 = edges only, 1 = softest

    #define FXAA_SEARCH_STEPS 10
    static const float s_searchStep[ FXAA_SEARCH_STEPS ] = { 1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 4.0, 4.0, 8.0, 12.0 };

    float3 Fetch( float2 uv )
    {
        return g_tColorBuffer.SampleLevel( g_sBilinearClamp, uv, 0 ).rgb;
    }

    float Luma( float3 c )
    {
        return sqrt( dot( c, float3( 0.299, 0.587, 0.114 ) ) );
    }

    float LumaAt( float2 uv )
    {
        return Luma( Fetch( uv ) );
    }

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float2 uv = CalculateViewportUv( i.vPositionSs.xy );
        float2 texel = 1.0 / g_vRenderTargetSize;

        float4 centre = g_tColorBuffer.SampleLevel( g_sBilinearClamp, uv, 0 );
        float lM = Luma( centre.rgb );
        float lN = LumaAt( uv + float2( 0, -texel.y ) );
        float lS = LumaAt( uv + float2( 0,  texel.y ) );
        float lE = LumaAt( uv + float2(  texel.x, 0 ) );
        float lW = LumaAt( uv + float2( -texel.x, 0 ) );

        // Early out on low contrast. This is almost every pixel, so the cost of the pass is mostly these five taps.
        float lMax = max( lM, max( max( lN, lS ), max( lE, lW ) ) );
        float lMin = min( lM, min( min( lN, lS ), min( lE, lW ) ) );
        float range = lMax - lMin;
        if ( range < max( g_flEdgeThresholdMin, lMax * g_flEdgeThreshold ) )
            return centre;

        float lNW = LumaAt( uv + float2( -texel.x, -texel.y ) );
        float lNE = LumaAt( uv + float2(  texel.x, -texel.y ) );
        float lSW = LumaAt( uv + float2( -texel.x,  texel.y ) );
        float lSE = LumaAt( uv + float2(  texel.x,  texel.y ) );

        // Sub-pixel aliasing: how far the centre stands out from its 3x3 neighbourhood (single-pixel specks, thin lines).
        float lAvg = ( 2.0 * ( lN + lS + lE + lW ) + ( lNW + lNE + lSW + lSE ) ) / 12.0;
        float sub = saturate( abs( lAvg - lM ) / range );
        sub = smoothstep( 0.0, 1.0, sub );
        sub = sub * sub * g_flSubpixel;

        // Edge orientation from the second derivative along each axis.
        float edgeH = abs( lNW + lSW - 2.0 * lW ) + 2.0 * abs( lN + lS - 2.0 * lM ) + abs( lNE + lSE - 2.0 * lE );
        float edgeV = abs( lNW + lNE - 2.0 * lN ) + 2.0 * abs( lW + lE - 2.0 * lM ) + abs( lSW + lSE - 2.0 * lS );
        bool horizontal = edgeH >= edgeV;

        // Which side of the centre the edge lies on: the neighbour with the steeper gradient.
        float lNeg = horizontal ? lN : lW;
        float lPos = horizontal ? lS : lE;
        float gNeg = abs( lNeg - lM );
        float gPos = abs( lPos - lM );
        float stepLen = horizontal ? texel.y : texel.x;
        float lPair;
        if ( gNeg >= gPos )
        {
            stepLen = -stepLen;
            lPair = lNeg;
        }
        else
        {
            lPair = lPos;
        }
        float gScaled = 0.25 * max( gNeg, gPos );
        float lLocal = 0.5 * ( lPair + lM );

        // Walk along the edge (both ways, from the boundary between the two pixels) until the luma leaves the pair's
        // average — that's where the edge ends, and the distance to the nearer end sets how much to blend.
        float2 edgeUv = uv;
        float2 along = horizontal ? float2( texel.x, 0 ) : float2( 0, texel.y );
        if ( horizontal ) edgeUv.y += 0.5 * stepLen; else edgeUv.x += 0.5 * stepLen;

        float2 uvNeg = edgeUv - along;
        float2 uvPos = edgeUv + along;
        float endNeg = LumaAt( uvNeg ) - lLocal;
        float endPos = LumaAt( uvPos ) - lLocal;
        bool doneNeg = abs( endNeg ) >= gScaled;
        bool donePos = abs( endPos ) >= gScaled;

        [loop]
        for ( int s = 1; s < FXAA_SEARCH_STEPS && !( doneNeg && donePos ); s++ )
        {
            if ( !doneNeg )
            {
                uvNeg -= along * s_searchStep[ s ];
                endNeg = LumaAt( uvNeg ) - lLocal;
                doneNeg = abs( endNeg ) >= gScaled;
            }
            if ( !donePos )
            {
                uvPos += along * s_searchStep[ s ];
                endPos = LumaAt( uvPos ) - lLocal;
                donePos = abs( endPos ) >= gScaled;
            }
        }

        float distNeg = horizontal ? ( uv.x - uvNeg.x ) : ( uv.y - uvNeg.y );
        float distPos = horizontal ? ( uvPos.x - uv.x ) : ( uvPos.y - uv.y );
        bool negCloser = distNeg < distPos;
        float dist = min( distNeg, distPos );
        float edgeOffset = 0.5 - dist / ( distNeg + distPos );

        // Only blend if the luma at the nearer end moves the right way relative to the centre; otherwise this pixel is
        // on the far side of the edge's span and must stay put.
        bool centreSmaller = lM < lLocal;
        bool correct = ( ( negCloser ? endNeg : endPos ) < 0.0 ) != centreSmaller;
        float offset = max( correct ? edgeOffset : 0.0, sub );

        float2 finalUv = uv;
        if ( horizontal ) finalUv.y += offset * stepLen; else finalUv.x += offset * stepLen;
        return float4( Fetch( finalUv ), centre.a );
    }
}
