//=========================================================================================================================
// Triplanar normal mapping (Ben Golus "whiteout" blend) — perturbs a surface normal by a tangent-space normal map without
// real tangents/UVs. Space-agnostic: position and normal in the SAME frame, result in that frame (callers feed MODEL
// space and rotate the result to world). Positions in inches, tiled per metre like Tex2DTriplanar.
//
// Shared by sdf_mesh.shader and sdf_ghost.shader. sdf_raymarch.shader keeps its own (unguarded) copy on purpose: its
// forward PS is register-bound, so any change there has to be measured first (see forward-ps-register-pressure).
// Include inside the PS block.
//=========================================================================================================================
float3 TriplanarNormal( Texture2D tex, SamplerState s, float3 wp, float3 wn, float tile, float blend, float strength )
{
	wp /= 39.3701;

	// SurfaceNets can emit zero/degenerate normals on thin features; normalize() of
	// those is NaN, which DoF then gathers into bright bokeh discs. Guard it.
	float nl = length( wn );
	float3 n = nl > 1e-5 ? wn / nl : float3( 0, 0, 1 );

	float3 bw = pow( abs( n ), blend );
	bw /= dot( bw, 1.0 );

	float3 tnX = tex.Sample( s, wp.zy * tile ).xyz * 2.0 - 1.0;
	float3 tnY = tex.Sample( s, wp.xz * tile ).xyz * 2.0 - 1.0;
	float3 tnZ = tex.Sample( s, wp.xy * tile ).xyz * 2.0 - 1.0;

	// Normal-map influence. The whiteout fold below discards the sampled z and rebuilds it from
	// the surface normal, so scaling just xy is an exact slope lerp: 0 = pure surface normal.
	tnX.xy *= strength;
	tnY.xy *= strength;
	tnZ.xy *= strength;

	tnX = float3( tnX.xy + n.zy, n.x );
	tnY = float3( tnY.xy + n.xz, n.y );
	tnZ = float3( tnZ.xy + n.xy, n.z );

	return normalize( tnX.zyx * bw.x + tnY.xzy * bw.y + tnZ.xyz * bw.z );
}
