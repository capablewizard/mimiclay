//=========================================================================================================================
// Ghost mesh — the ghost LOOK (ghost_look.hlsl, shared with sdf_ghost.shader) on ordinary geometry: the facing rim from
// the mesh normal, the base opacity and the drifting smoke, unlit and translucent. Worn by meshes that belong to a ghost
// but aren't SDF clay — the run puffs trailing a free-cam ghost (materials/ghost_puff.vmat on its particle models).
//
// Particle models can't carry per-object attributes, so this draws with ghost_look.hlsl's defaults — the tuned look.
// Depth-tested against the scene (walls hide it), no depth write, no prepass, no shadows: a ghost casts nothing.
//=========================================================================================================================
HEADER
{
	Description = "Ghost Mesh";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward(); // no Depth() mode: a ghost must not enter the prepass or cast shadows
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	// Translucent pass + SRC_ALPHA/INV_SRC_ALPHA blend + depth-write off, depth test still on (see shadow_catcher.shader).
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
	#include "ghost_look.hlsl"

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 worldPos = i.vPositionWithOffsetWs + g_vCameraPositionWs;
		float3 toCamera = normalize( -i.vPositionWithOffsetWs );

		float4 shade = GhostShade( GhostRim( normalize( i.vNormalWs ), toCamera ), 0.0 );

		// Short-lived pieces: the smoke in world space is fine (they don't live long enough to slide through it).
		// The vertex colour's alpha carries any per-instance fade the owner applies (a particle's Alpha).
		float alpha = shade.a * GhostSmoke( worldPos ) * i.vVertexColor.a;

		float4 o = GhostFinish( shade.rgb, alpha );
		if ( o.a <= 0.002 )
		{
			clip( -1 );
			return 0;
		}

		return o;
	}
}
