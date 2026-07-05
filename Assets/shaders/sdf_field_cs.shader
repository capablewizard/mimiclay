//=========================================================================================================================
// SDF field evaluator (compute). The Dreams "evaluator / CS of doom" port: evaluate the whole brush list into a 3D
// distance volume on the GPU, in parallel, instead of the CPU re-baking + uploading it per edit. Brushes are packed in
// the prop's LOCAL (model) space (identity transform) so the field is placement-invariant and the raymarch's existing
// WorldToModelPos sampling is unchanged. One thread = one voxel. The distance math lives in the shared sdf_eval.hlsl
// (also used by the sparse atlas-fill and brick-classify compute shaders); colour/displacement are NOT baked — the
// raymarch still computes those per-pixel.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"
	#include "sdf_eval.hlsl"

	RWTexture3D<float> g_tField     < Attribute( "FieldOut" ); >;   // output volume (R32F, UAV)
	float3 g_vFieldMin  < Attribute( "FieldMin" ); >;              // local pos of voxel (0,0,0)
	float3 g_vFieldMax  < Attribute( "FieldMax" ); >;              // local pos of voxel (Dims-1)
	float3 g_vFieldDims < Attribute( "FieldDims" ); Default3( 2, 2, 2 ); >; // voxel counts per axis

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= (uint)g_vFieldDims.x || id.y >= (uint)g_vFieldDims.y || id.z >= (uint)g_vFieldDims.z )
			return;

		// Voxel -> local sample point. Samples sit on the inclusive corners (sample 0 -> FieldMin, sample
		// Dims-1 -> FieldMax), matching the CPU baker and the shader's UVW remap.
		float3 cell = (g_vFieldMax - g_vFieldMin) / max( g_vFieldDims - 1.0, 1.0 );
		float3 lp = g_vFieldMin + float3( id ) * cell;
		g_tField[id] = SdfDist( lp );
	}
}
