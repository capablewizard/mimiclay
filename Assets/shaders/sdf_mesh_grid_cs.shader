//=========================================================================================================================
// SDF MESH GRID (compute). Samples the brush union on the CPU mesher's OWN grid — its bounds, its cell size, its sample
// points — so surface nets can stitch a mesh without the CPU ever evaluating a brush. This is what retires the duplicate
// evaluator: the mesh, the raymarch and the collision proxy all descend from sdf_eval.hlsl now, and a new brush property
// added there shows up in the meshed LODs for free (the class of bug that made Text brushes mesh as a plain box).
//
// One thread = one grid point, and each writes ONE RGBA32F texel:
//   .r = signed distance (the only channel the surface-nets sign test and edge interpolation read)
//   .g = metalness, .b = roughness
//   .a = sRGB colour packed as r + g*256 + b*65536 over 8-bit channels
//
// The colour is PACKED rather than given its own texture to keep this to a single dispatch and a single readback: a
// GetPixels3D is a synchronous GPU stall, and the mesher runs three times per prop (one per LOD). 8 bits per channel is
// no loss — it lands in a Color32 vertex attribute either way. The packed integer stays under 2^24, so float32 holds it
// exactly; the CPU unpacks the eight corners BEFORE interpolating (lerping packed values would be nonsense).
//
// Surface attributes are only evaluated within a couple of cells of the surface. Everywhere else the geometry pass has
// already told us there is no vertex to colour, and skipping the second brush loop there is most of the grid.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"
	#include "sdf_eval.hlsl"

	// A 2D texture, NOT a volume: slices are stacked down Y (y = point.y + Dims.y * point.z). The readback is a
	// per-slice engine call, so a volume costs one GPU round trip PER Z SLICE — tens of syncs where this needs one.
	RWTexture2D<float4> g_tMeshGrid < Attribute( "MeshGridOut" ); >;         // output (RGBA32F, UAV)
	float3 g_vGridMin  < Attribute( "GridMin" ); >;                          // local pos of grid point (0,0,0)
	float3 g_vGridMax  < Attribute( "GridMax" ); >;                          // local pos of grid point (Dims-1)
	float3 g_vGridDims < Attribute( "GridDims" ); Default3( 2, 2, 2 ); >;    // grid point counts per axis
	float  g_flAttrBand< Attribute( "AttrBand" ); Default( 0.0 ); >;         // |d| under this gets real attributes

	// Gamma-encode for the 8-bit vertex colour: the brush pack linearises, and sdf_mesh.shader does
	// SrgbToLinear on the way back in, so the byte we store has to be the sRGB one.
	float3 LinearToSrgbApprox( float3 c )
	{
		c = saturate( c );
		return c <= 0.0031308 ? c * 12.92 : 1.055 * pow( c, 1.0 / 2.4 ) - 0.055;
	}

	[numthreads( 4, 4, 4 )]
	void MainCs( uint3 id : SV_DispatchThreadID )
	{
		if ( id.x >= (uint)g_vGridDims.x || id.y >= (uint)g_vGridDims.y || id.z >= (uint)g_vGridDims.z )
			return;

		// Grid point -> local sample point. Samples sit on the inclusive corners (point 0 -> GridMin,
		// point Dims-1 -> GridMax), matching exactly what SurfaceNetsMesher.GP used to compute.
		float3 cell = (g_vGridMax - g_vGridMin) / max( g_vGridDims - 1.0, 1.0 );
		float3 lp = g_vGridMin + float3( id ) * cell;

		float d = SdfDist( lp );

		// Empty-space material, matching Sdf.SampleSurface's seed (white, dielectric, fully rough).
		float3 col = float3( 1, 1, 1 );
		float metal = 0.0, rough = 1.0;
		if ( abs( d ) <= g_flAttrBand )
		{
			SdfSurface s = SdfSurfaceLocal( lp );
			col = LinearToSrgbApprox( s.col );
			metal = s.metal;
			rough = s.rough;
		}

		float3 c255 = floor( saturate( col ) * 255.0 + 0.5 );
		float packed = c255.x + c255.y * 256.0 + c255.z * 65536.0;

		g_tMeshGrid[int2( id.x, id.y + (uint)g_vGridDims.y * id.z )] = float4( d, metal, rough, packed );
	}
}
