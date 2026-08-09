//=========================================================================================================================
// Shadow-catcher / shadow-only surface. The geometry itself is invisible (fully transparent where it's lit); it only
// becomes visible where a shadow falls on it, darkening whatever is drawn behind it. Useful for grounding a prop on an
// otherwise-empty/real backdrop (AR-style "catch the shadow on the floor").
//
// How: for every light reaching the pixel we know (a) how much it WOULD illuminate this surface (N·L, weighted by the
// light's luminance/attenuation) and (b) the shadow visibility from the engine's shadow maps. The opacity we output is
// the fraction of that potential illumination that is currently BLOCKED. Fully lit -> opacity 0 (invisible). Fully
// shadowed by the only light -> opacity 1 (solid shadow tint). The colour is just the (black) shadow tint, so the
// alpha-over blend below multiplies the background by (1 - shadow) and darkens it.
//
// Deliberately Forward-only (NO Depth() mode): the catcher must NOT cast its own shadow and must NOT write the depth
// prepass — it is supposed to be invisible. It still depth-TESTS so real geometry in front of it occludes it.
//=========================================================================================================================
HEADER
{
	Description = "Shadow Catcher";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	// Mark the whole shader translucent. This is what the material compiler keys on (Material.IsTranslucent ->
	// the "translucent" bool attribute that sbox_pixel.fxc emits under S_TRANSLUCENT), so the object is sorted
	// into the TRANSLUCENT pass and drawn AFTER the skybox. As a bonus sbox_pixel.fxc then sets our blend
	// (SRC_ALPHA / INV_SRC_ALPHA) and disables depth-write for us, so we don't set those by hand below.
	#define S_TRANSLUCENT 1
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
	#include "common/pixel.hlsl"
	#include "common/classes/Light.hlsl" // Light::* (cluster point/spot lights) + DirectionalLightShadow (sun)

	// S_TRANSLUCENT (set in COMMON) already gave us: BlendEnable + SRC_ALPHA/INV_SRC_ALPHA "over" blend (black
	// source colour darkens the background by the opacity), DepthWriteEnable off (translucent, must not block
	// what's behind), and DepthFunc GREATER_EQUAL (s&box reverse-Z) so geometry in front still occludes us.
	// We only override cull: NONE so a single-sided ground plane catches from either facing.
	RenderState( CullMode, NONE );

	// Fold the cluster (point/spot) lights into the catch too. Off by default: most shadow-catchers only want the
	// sun/key directional shadow, and skipping the cluster loop keeps it free. Turn on for point/spot-lit scenes.
	DynamicCombo( D_POINT_LIGHTS, 0..1, Sys( All ) );

	float3 g_vShadowColor    < UiType( Color ); Default3( 0.0, 0.0, 0.0 ); UiGroup( "Shadow,10/10" ); >; // tint of the caught shadow
	float  g_flShadowStrength < Default( 1.0 ); Range( 0.0, 1.0 );          UiGroup( "Shadow,10/20" ); >; // overall opacity multiplier
	float  g_flMaxOpacity     < Default( 1.0 ); Range( 0.0, 1.0 );          UiGroup( "Shadow,10/30" ); >; // clamp so shadows never go 100% solid
	float  g_flShadowPower    < Default( 1.0 ); Range( 0.25, 4.0 );         UiGroup( "Shadow,10/40" ); >; // gamma on the shadow ramp (>1 = softer edges)

	// Luminance() is already provided by common/math_general.fxc (pulled in via pixel.hlsl) — don't redefine it.

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// Material::Init gives the camera-relative world position + screen position the shadow/light queries expect.
		Material m = Material::Init( i );
		float3 worldPos  = m.WorldPosition;
		float4 screenPos = m.ScreenPosition;
		float3 N = normalize( i.vNormalWs );

		// Ratio formulation: sum each light's would-be illumination (potential) and the part that actually arrives
		// (received). shadow = 1 - received/potential -> correctly handles one light (full shadow = 1) AND several
		// lights (a shadow from one of two equal lights reads as ~0.5), with no per-light double-darkening.
		float potential = 0.0;
		float received  = 0.0;

		// Key / sun directional light + its shadow map.
		if ( g_DirectionalLightEnabled )
		{
			float3 L = -normalize( g_DirectionalLightDirection.xyz ); // toward the light
			float ndotl = saturate( dot( N, L ) );
			if ( ndotl > 0.0 )
			{
				float w = ndotl * Luminance( g_DirectionalLightColor.rgb );
				float vis = DirectionalLightShadow::GetVisibility( worldPos, screenPos );
				potential += w;
				received  += w * vis;
			}
		}

	#if ( D_POINT_LIGHTS )
		// Cluster point/spot lights affecting this screen tile (a handful, not the whole scene). Each carries its
		// own attenuation + shadow visibility; the directional is handled above, so skip it here to avoid double count.
		uint lightCount = Light::Count( m.ScreenPosition );
		[loop]
		for ( uint li = 0; li < lightCount; li++ )
		{
			Light light = Light::From( worldPos, m.ScreenPosition, li );
			if ( light.LightData.Type == LightType::LightTypeDirectional )
				continue;

			float ndotl = saturate( dot( N, light.Direction ) );
			if ( ndotl <= 0.0 )
				continue;

			float w = ndotl * light.Attenuation * Luminance( light.Color );
			potential += w;
			received  += w * light.Visibility;
		}
	#endif

		// No light reaching this surface at all -> nothing to shadow -> fully transparent.
		float shadow = potential > 1e-5 ? saturate( 1.0 - received / potential ) : 0.0;

		shadow = pow( shadow, g_flShadowPower );
		float opacity = min( shadow * g_flShadowStrength, g_flMaxOpacity );

		return float4( g_vShadowColor, opacity );
	}
}
