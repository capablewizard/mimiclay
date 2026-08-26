//=========================================================================================================================
// World-geometry stand-in for the engine's complex.shader, plus the camera-occlusion cutout (clay_cutout.hlsl).
// Facepunch don't ship complex.shader source, but they DO ship the whole common/ include library it wraps:
// Material.CommonInputs.hlsl declares the exact complex texture set (TextureColor/Normal/Roughness/Metalness/AO,
// g_flTintColor...) and Material::From + ShadingModelStandard::Shade do the sampling and lighting. So an existing
// complex vmat keeps its textures and sliders across a one-line shader swap to this.
//
// Swap a material to this shader for any geometry that can stand between the camera and a hider's prop (walls,
// large furniture, doorframes). Geometry left on complex.shader simply never gets a hole — opting out is free.
// The "Camera Cutout" checkbox below turns the discard off per material without swapping back.
//=========================================================================================================================
HEADER
{
	Description = "Clay World — complex-compatible + camera-occlusion cutout";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	// The same MainPs compiles into the depth prepass (and shadow views) — invariant #1: the cutout must hole
	// the PREPASS depth too, or the cut wall still occludes everything it was supposed to reveal. Shadow views
	// also run this mode; the include's per-view camera gate keeps them solid (invariant #2).
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
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
	// Complex-compatible material inputs — a local copy of common/utils/Material.CommonInputs.hlsl with ONE
	// change: g_tColor's mips are plain Box-filtered instead of AlphaWeighted( colour, translucency ).
	// AlphaWeighted mip generation hard-fails on non-power-of-two sources (CTextureFrame::
	// GenerateMips_AlphaWeighted — bit fabric.vmat's 700x700 gingham), and alpha-weighted mips only matter
	// for translucency-weighted colour bleed, which opaque world geometry doesn't use. Input names are
	// identical, so vmats stay complex-compatible. ORDER MATTERS: defining the include guard BEFORE
	// common/pixel.hlsl is what switches Material::From onto this texture set.
	#define MATERIAL_COMMON_INPUTS_HLSL
	#include "common/utils/normal.hlsl"

	CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Material,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureNormal, Linear, 8, "NormalizeNormals", "_normal", "Material,10/20", Default3( 0.5, 0.5, 1.0 ) );
	CreateInputTexture2D( TextureRoughness, Linear, 8, "", "_rough", "Material,10/30", Default( 0.5 ) );
	CreateInputTexture2D( TextureMetalness, Linear, 8, "", "_metal", "Material,10/40", Default( 1.0 ) );
	CreateInputTexture2D( TextureAmbientOcclusion, Linear, 8, "", "_ao", "Material,10/50", Default( 1.0 ) );
	CreateInputTexture2D( TextureBlendMask, Linear, 8, "", "_blend", "Material,10/60", Default( 1.0 ) );
	CreateInputTexture2D( TextureTranslucency, Linear, 8, "", "_trans", "Material,10/70", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureTintMask, Linear, 8, "", "_tint", "Material,10/70", Default( 1.0 ) );

	float3 g_flTintColor < Default3( 1.0, 1.0, 1.0 ); UiGroup( "Material,10/90" ); UiType( Color ); >;
	float  g_flSelfIllumScale < Default( 1.0 ); UiGroup( "Material,10/91" ); Range( 0.0, 16.0 ); >;

	Texture2D g_tColor < Channel( RGB, Box( TextureColor ), Srgb ); Channel( A, Box( TextureTranslucency ), Linear ); OutputFormat( BC7 ); SrgbRead( true ); >;
	Texture2D g_tNormal < Channel( RGB, Box( TextureNormal ), Linear ); Channel( A, Box( TextureTintMask ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
	Texture2D g_tRma < Channel( R, Box( TextureRoughness ), Linear ); Channel( G, Box( TextureMetalness ), Linear ); Channel( B, Box( TextureAmbientOcclusion ), Linear ); Channel( A, Box( TextureBlendMask ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	// For VRAD3 / thumbnails, same as the engine include.
	TextureAttribute( LightSim_DiffuseAlbedoTexture, g_tColor );
	TextureAttribute( RepresentativeTexture, g_tColor );

	BoolAttribute( DoNotCastShadows, F_DO_NOT_CAST_SHADOWS ? true : false );
	BoolAttribute( SupportsMappingDimensions, true );
	BoolAttribute( renderbackfaces, F_RENDER_BACKFACES ? true : false );

	SamplerState TextureFiltering < Filter( (F_TEXTURE_FILTERING == 0 ? ANISOTROPIC : (F_TEXTURE_FILTERING == 1 ? BILINEAR : (F_TEXTURE_FILTERING == 2 ? TRILINEAR : (F_TEXTURE_FILTERING == 3 ? POINT : NEAREST)))) ); MaxAniso( 8 ); >;

	#include "common/pixel.hlsl"
	#include "clay_cutout.hlsl"

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	// Per-material opt-out for the camera-occlusion hole (scene-wide state stays in ClayCutout.cs / the
	// mimiclay_cutout convar). On a material that never occludes the player, prefer plain complex.shader.
	bool g_bClayCutout < UiType( CheckBox ); Default( 1 ); UiGroup( "Camera Cutout,20/10" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// First thing, before any shading work — and in every mode this PS compiles into (forward + prepass).
		// clip() lives HERE, not in a helper — a nested discard has ICE'd DXC before (see clay_cutout.hlsl).
		float cutRim = 0.0, cutOutline = 0.0;
		if ( g_bClayCutout )
		{
			float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vCameraPositionWs;
			if ( ClayCutoutHit( i.vPositionSs.xy, worldPos, normalize( i.vNormalWs.xyz ), cutRim, cutOutline ) )
				clip( -1.0 );
		}

		Material m = Material::From( i ); // samples complex's standard g_tColor / g_tNormal / g_tRma set
		m.Albedo *= 1.0 - cutRim * g_flClayCutoutRimDarken; // darkened band at the cut edge — the clay's cross-section

		float4 c = ShadingModelStandard::Shade( i, m );
		// Outline AFTER the lighting: a flat unlit line (matches SdfHighlightOutline's read) — an albedo band
		// would dim in shadow and pick up bounce colour.
		c.rgb = lerp( c.rgb, g_vClayCutoutOutline.rgb, cutOutline );
		return c;
	}
}
