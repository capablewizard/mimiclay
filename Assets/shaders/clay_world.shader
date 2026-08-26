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
	// ORDER MATTERS: Material.CommonInputs.hlsl must come first — its include guard is the #define that
	// switches Material::From (inside pixel.hlsl) onto the complex-style texture path.
	#include "common/utils/Material.CommonInputs.hlsl"
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
