//=========================================================================================================================
// Depth-TESTED variant of gizmo.shader, for the brush wireframe overlay. Same unlit vertex-colour, two-sided,
// alpha-blended look, but it keeps the engine's normal depth test (no DepthFunc override) and doesn't write
// depth — so the SDF surface occludes the wireframe's back half, exactly like the editor scene view. The
// manipulation gizmos keep gizmo.shader (DepthFunc ALWAYS) to stay on top.
//=========================================================================================================================
HEADER
{
	Description = "Mimiclay Gizmo (unlit, depth-tested, vertex colour)";
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

	float g_flDepthBias < Attribute( "DepthBias" ); Default( 0 ); >;

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = ProcessVertex( v );
		o.vColor = v.vColor;
		o = FinalizeVertex( o );

		// Bias toward the camera (reversed-Z: larger z = nearer) so the wireframe shows through the thin
		// shell of blended/rounded SDF surface it sits just inside, while the far back half stays occluded.
		o.vPositionPs.z += g_flDepthBias * o.vPositionPs.w;
		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"

	// Two-sided, alpha blended, depth-TESTED (matches the editor gizmo_line.shader): depth read ON, default
	// DepthFunc so closer geometry (the SDF) occludes us, but no depth write (we're transparent).
	RenderState( CullMode, NONE );
	RenderState( DepthEnable, true );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		return i.vColor;
	}
}
