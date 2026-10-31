HEADER
{
	Description = "Ground material whose tile size grows with camera distance (blended power-of-two octaves), plus macro variation and a far fade.";
	DevShader = false;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "vr_tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/normal.hlsl"

	CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Material,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureNormal, Linear, 8, "NormalizeNormals", "_normal", "Material,10/20", Default3( 0.5, 0.5, 1.0 ) );
	CreateInputTexture2D( TextureRoughness, Linear, 8, "", "_rough", "Material,10/30", Default( 0.5 ) );
	CreateInputTexture2D( TextureAmbientOcclusion, Linear, 8, "", "_ao", "Material,10/40", Default( 1.0 ) );

	float3 g_flTintColor < Default3( 1.0, 1.0, 1.0 ); UiGroup( "Material,10/90" ); UiType( Color ); >;

	Texture2D g_tColor < Channel( RGB, Box( TextureColor ), Srgb ); OutputFormat( BC7 ); SrgbRead( true ); >;
	Texture2D g_tNormal < Channel( RGB, Box( TextureNormal ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
	// R = roughness, G = ambient occlusion.
	Texture2D g_tRa < Channel( R, Box( TextureRoughness ), Linear ); Channel( G, Box( TextureAmbientOcclusion ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	TextureAttribute( LightSim_DiffuseAlbedoTexture, g_tColor );
	TextureAttribute( RepresentativeTexture, g_tColor );

	BoolAttribute( DoNotCastShadows, F_DO_NOT_CAST_SHADOWS ? true : false );
	BoolAttribute( renderbackfaces, F_RENDER_BACKFACES ? true : false );

	SamplerState TextureFiltering < Filter( ( F_TEXTURE_FILTERING == 0 ? ANISOTROPIC : ( F_TEXTURE_FILTERING == 1 ? BILINEAR : ( F_TEXTURE_FILTERING == 2 ? TRILINEAR : ( F_TEXTURE_FILTERING == 3 ? POINT : NEAREST ) ) ) ) ); MaxAniso( 8 ); >;

	#include "common/pixel.hlsl"

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	// Tiling: world units covered by one tile near the camera (x = horizontal, y = vertical); doubles every octave.
	float2 g_vBaseTile < Default2( 128.0, 128.0 ); Range2( 1.0, 1.0, 4096.0, 4096.0 ); UiGroup( "Distance Tiling,20/10" ); >;
	// Distance at which the first octave starts blending to double size; each later octave at 2x the distance.
	float g_flBaseDistance < Default( 256.0 ); Range( 1.0, 8192.0 ); UiGroup( "Distance Tiling,20/20" ); >;
	float g_flMaxOctaves < Default( 6.0 ); Range( 0.0, 12.0 ); UiGroup( "Distance Tiling,20/30" ); >;
	// 0 = smooth blend across the whole octave, 1 = hard switch at the octave boundary.
	float g_flOctaveBlendSharpness < Default( 0.0 ); Range( 0.0, 0.95 ); UiGroup( "Distance Tiling,20/40" ); >;

	// Low-frequency value/hue wobble so the tiled pattern doesn't read as a grid.
	float g_flMacroScale < Default( 3000.0 ); Range( 64.0, 65536.0 ); UiGroup( "Macro Variation,30/10" ); >;
	float g_flMacroBrightness < Default( 0.15 ); Range( 0.0, 1.0 ); UiGroup( "Macro Variation,30/20" ); >;
	float3 g_vMacroTint < Default3( 0.85, 1.0, 0.6 ); UiType( Color ); UiGroup( "Macro Variation,30/30" ); >;
	float g_flMacroTintAmount < Default( 0.2 ); Range( 0.0, 1.0 ); UiGroup( "Macro Variation,30/40" ); >;

	// Far away: lerp albedo toward the texture's average colour and flatten the normal map.
	float g_flFarFadeStart < Default( 3000.0 ); Range( 0.0, 100000.0 ); UiGroup( "Far Fade,40/10" ); >;
	float g_flFarFadeEnd < Default( 20000.0 ); Range( 1.0, 200000.0 ); UiGroup( "Far Fade,40/20" ); >;
	float g_flFarFadeAmount < Default( 0.6 ); Range( 0.0, 1.0 ); UiGroup( "Far Fade,40/30" ); >;

	float g_flNormalStrength < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Surface,50/10" ); >;
	float g_flRoughnessScale < Default( 1.0 ); Range( 0.0, 2.0 ); UiGroup( "Surface,50/20" ); >;
	float g_flMetalnessValue < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,50/30" ); >;
	// Exponent on the AO texture: 0 = no AO, 1 = as authored, >1 = deeper.
	float g_flAmbientOcclusionAmount < Default( 1.0 ); Range( 0.0, 4.0 ); UiGroup( "Surface,50/40" ); >;

	float Hash12( float2 p )
	{
		float3 p3 = frac( float3( p.xyx ) * 0.1031 );
		p3 += dot( p3, p3.yzx + 33.33 );
		return frac( ( p3.x + p3.y ) * p3.z );
	}

	float ValueNoise( float2 p )
	{
		float2 c = floor( p );
		float2 f = frac( p );
		f = f * f * ( 3.0 - 2.0 * f );
		float a = Hash12( c );
		float b = Hash12( c + float2( 1, 0 ) );
		float d = Hash12( c + float2( 0, 1 ) );
		float e = Hash12( c + float2( 1, 1 ) );
		return lerp( lerp( a, b, f.x ), lerp( d, e, f.x ), f.y );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::Init( i );

		float3 posWs = m.WorldPosition;
		float dist = distance( posWs, g_vCameraPositionWs );

		// Octave level: 0 inside BaseDistance, +1 per doubling of distance.
		float level = clamp( log2( max( dist, 1.0 ) / g_flBaseDistance ), 0.0, g_flMaxOctaves );
		float lo = floor( level );
		float t = frac( level );
		float s = g_flOctaveBlendSharpness * 0.5;
		t = smoothstep( s, 1.0 - s, t );
		if ( lo >= g_flMaxOctaves ) t = 0.0;

		// World-projected top-down UVs; each octave doubles the tile size.
		float2 uvA = posWs.xy / ( g_vBaseTile * exp2( lo ) );
		float2 uvB = uvA * 0.5;

		float4 colA = g_tColor.Sample( TextureFiltering, uvA );
		float4 colB = g_tColor.Sample( TextureFiltering, uvB );
		float4 nrmA = g_tNormal.Sample( TextureFiltering, uvA );
		float4 nrmB = g_tNormal.Sample( TextureFiltering, uvB );
		float2 raA = g_tRa.Sample( TextureFiltering, uvA ).rg;
		float2 raB = g_tRa.Sample( TextureFiltering, uvB ).rg;

		float3 albedo = lerp( colA.rgb, colB.rgb, t );
		float3 normalTs = lerp( DecodeNormal( nrmA.xyz ), DecodeNormal( nrmB.xyz ), t );
		float2 ra = lerp( raA, raB, t );

		// Macro variation.
		float2 macroUv = posWs.xy / g_flMacroScale;
		float n1 = ValueNoise( macroUv );
		float n2 = ValueNoise( macroUv * 2.7 + 17.3 );
		albedo *= 1.0 + ( n1 - 0.5 ) * 2.0 * g_flMacroBrightness;
		albedo = lerp( albedo, albedo * g_vMacroTint, n2 * g_flMacroTintAmount );

		// Far fade toward the texture's average colour (smallest mip).
		float far = smoothstep( g_flFarFadeStart, g_flFarFadeEnd, dist ) * g_flFarFadeAmount;
		float3 avg = g_tColor.SampleLevel( TextureFiltering, float2( 0.5, 0.5 ), 16.0 ).rgb;
		avg *= 1.0 + ( n1 - 0.5 ) * 2.0 * g_flMacroBrightness;
		albedo = lerp( albedo, avg, far );

		float normalStrength = g_flNormalStrength * ( 1.0 - far );
		normalTs = normalize( float3( normalTs.xy * normalStrength, normalTs.z ) );

		// Tangent frame for the world-XY projection, flattened onto the surface (handles gentle hills).
		float3 nWs = normalize( i.vNormalWs.xyz );
		float3 tanU = normalize( float3( 1, 0, 0 ) - nWs * nWs.x );
		float3 tanV = normalize( float3( 0, 1, 0 ) - nWs * nWs.y );

		m.Albedo = albedo * g_flTintColor * i.vVertexColor.rgb;
		m.Normal = TransformNormal( normalTs, nWs, tanU, tanV );
		m.Roughness = saturate( ra.r * g_flRoughnessScale );
		m.Metalness = g_flMetalnessValue;
		m.AmbientOcclusion = pow( saturate( ra.g ), g_flAmbientOcclusionAmount );
		m.Opacity = 1.0;

		return ShadingModelStandard::Shade( i, m );
	}
}
