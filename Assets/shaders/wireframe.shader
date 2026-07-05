//=========================================================================================================================
// Unlit WIREFRAME-fill debug shader. Used as a MaterialOverride to view a mesh's triangulation (e.g. the SDF
// shadow proxy). FillMode WIREFRAME draws triangle edges only; two-sided + no depth write so you see the whole
// structure. Emits a fixed bright colour (vertex colours on the proxy mesh are unset).
//=========================================================================================================================
HEADER
{
	Description = "Mimiclay Wireframe (unlit debug)";
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
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		return FinalizeVertex( ProcessVertex( v ) );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( FillMode, WIREFRAME );
	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		return float4( 0.3, 1.0, 0.5, 1.0 ); // bright green wireframe
	}
}
