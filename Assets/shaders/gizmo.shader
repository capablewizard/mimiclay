//=========================================================================================================================
// Unlit vertex-colour shader for the in-game transform gizmo. Draws on top of everything (DepthFunc ALWAYS,
// no depth write), two-sided (CullMode NONE), straight-alpha blended. The runtime gizmo bakes per-handle
// colour into vertex colours and renders one mesh through this — so the whole gizmo is a single draw call.
//=========================================================================================================================
HEADER
{
	Description = "Mimiclay Gizmo (unlit, on-top, vertex colour)";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float4 vColor : COLOR0;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = ProcessVertex( v );
		o.vColor = v.vColor;
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	// Two-sided, always-on-top, alpha blended — the gizmo overlay.
	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( DepthFunc, ALWAYS );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		return i.vColor;
	}
}
