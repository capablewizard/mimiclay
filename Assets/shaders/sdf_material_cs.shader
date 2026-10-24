//=========================================================================================================================
// SDF material bake (compute). The field stores distance only, so the raymarch used to re-walk every brush after each hit
// to blend colour/metal/rough (sdf_eval SdfSurfaceLocal) — 0.7–1.0 ms per crowded frame on the 256-prop benchmark,
// and it costs that only while it runs (compiling it out matched skipping it). This runs that SAME walk once per
// voxel at bake time over the field's bounds, so the forward pass can read the material with a few texture fetches
// (sdf_raymarch SdfMaterialSample) instead. One thread = one voxel.
//
// Layout: 2D textures with the Z slices stacked vertically (texel (x, y + z*DimsY)) — a second user VOLUME bound to the
// raymarch PS aliases the field (engine limit), 2D textures use the separate slot pool. Samples sit on the inclusive
// corners of [FieldMin, FieldMax] like the field. Colour is stored sqrt-encoded (≈ gamma) so 8 bits don't band in
// the darks; the raymarch squares it back.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"
	#include "sdf_eval.hlsl"

	RWTexture2D<float4> g_tMaterialOut < Attribute( "MaterialOut" ); >; // RGBA8: sqrt(colour).rgb, roughness
	RWTexture2D<float4> g_tMetalOut    < Attribute( "MetalOut" ); >;    // RGBA8: metalness, boundary flag
	float3 g_vFieldMin     < Attribute( "FieldMin" ); >;
	float3 g_vFieldMax     < Attribute( "FieldMax" ); >;
	float3 g_vMaterialDims < Attribute( "MaterialDims" ); Default3( 2, 2, 2 ); >;

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= (uint)g_vMaterialDims.x || id.y >= (uint)g_vMaterialDims.y || id.z >= (uint)g_vMaterialDims.z )
			return;

		float3 cell = (g_vFieldMax - g_vFieldMin) / max( g_vMaterialDims - 1.0, 1.0 );
		float3 lp = g_vFieldMin + float3( id ) * cell;

		// The material walk (cull OFF — exact), plus a BOUNDARY flag: does any brush that would CHANGE the material
		// sit within its transition band of this voxel, widened by R (two cell diagonals — the trilinear footprint
		// plus the distance field's ≤1 slope)? Interpolating across such a boundary smears it (bowl rims, grape
		// against grape), so the raymarch walks the brushes wherever a flagged voxel is in its trilinear footprint
		// and reads the bake only where the material is constant — exact there up to 8-bit rounding.
		float R = 2.0 * length( cell );
		float d = 1e9;
		SdfSurface s = SdfSurfaceEmpty();
		bool boundary = false;
		[loop]
		for ( int k = 0; k < g_nBrushCount; k++ )
		{
			float4 A = LoadBrush( k, 0 ), B = LoadBrush( k, 1 ), C = LoadBrush( k, 2 ), D = LoadBrush( k, 3 );
			float4 E = LoadBrush( k, 4 ), F = LoadBrush( k, 5 ), G = LoadBrush( k, 6 );
			float roughQ = floor( E.y / 1024.0 );
			float metal = (E.y - roughQ * 1024.0) / 1023.0;
			float rough = roughQ / 1023.0;
			float change = dot( abs( D.rgb - s.col ), 1.0 ) + abs( metal - s.metal ) + abs( rough - s.rough );
			if ( !boundary && change > 0.004 )
			{
				float bd = BrushDist( lp, A, B, C, E.x, (int)(E.w + 0.5), (int)(F.w + 0.5), G.w );
				float kk = max( B.w, 0.0 );
				// Distance from this voxel to where the brush's own blend factor leaves 0/1 (see SdfSurfaceBrush).
				float band = (D.w >= 2.5) ? abs( bd ) - 0.5 * max( B.w, SDF_COLOUR_EDGE_FLOOR )  // colour feather
					: (D.w >= 1.5) ? abs( bd ) - 0.5 * SDF_CUTOUT_COLOR_EDGE                    // cutout recolour edge
					: (D.w >= 0.5) ? abs( d + bd ) - kk                                          // subtract wall tint
					: abs( bd - d ) - kk;                                                        // union blend
				boundary = band < R;
			}
			SdfSurfaceBrush( k, lp, d, s );
		}

		uint2 px = uint2( id.x, id.y + id.z * (uint)g_vMaterialDims.y );
		g_tMaterialOut[px] = float4( sqrt( saturate( s.col ) ), saturate( s.rough ) );
		g_tMetalOut[px] = float4( saturate( s.metal ), boundary ? 1.0 : 0.0, 0, 0 );
	}
}
