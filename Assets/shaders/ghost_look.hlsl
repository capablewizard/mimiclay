//=========================================================================================================================
// The ghost LOOK — colours, the facing rim, the base opacity and the soft drifting smoke — shared by sdf_ghost.shader
// (the raymarched ghost head) and ghost_mesh.shader (ordinary meshes worn as ghosts: the ghost's run puffs), so both
// read as one material. Each shader works out its own surface (a marched hit, or a mesh's normal) and its own extras
// (the SDF ghost adds thickness, halo and occlusion), then composes through GhostShade / GhostFinish.
//
// The defaults ARE the tuned ghost look: per-object attributes (SdfGhost sets these on the SDF ghost) override them,
// and anything that can't carry per-object attributes — particle models — draws with them as they are. Retune here.
// Include inside the PS block, after common/pixel.hlsl.
//=========================================================================================================================
float4 g_vBodyColor      < Attribute( "GhostBodyColor" ); Default4( 0.27695, 0.80465, 0.5408, 1.0 ); >; // rgb = colour, a = opacity scale
float4 g_vRimColor       < Attribute( "GhostRimColor" );  Default4( 0.6093, 1.0, 0.76558, 1.0 ); >;
float  g_flRimPower      < Attribute( "GhostRimPower" ); Default( 1.8 ); >;
float  g_flRimStrength   < Attribute( "GhostRimStrength" ); Default( 1.5 ); >;
float  g_flBaseOpacity   < Attribute( "GhostBaseOpacity" ); Default( 0.1 ); >;     // flat floor where the surface faces the camera
float  g_flSmokeScale    < Attribute( "GhostSmokeScale" ); Default( 0.1 ); >;      // per world unit (smaller = bigger blobs)
float  g_flSmokeSpeed    < Attribute( "GhostSmokeSpeed" ); Default( 14.0 ); >;     // world units / s, upward
float  g_flSmokeStrength < Attribute( "GhostSmokeStrength" ); Default( 0.75 ); >;
float  g_flBrightness    < Attribute( "GhostBrightness" ); Default( 1.5 ); >;      // emissive multiplier (feeds bloom)

// Random unit-ish gradient per lattice corner, -1..1.
float3 Hash33( float3 p )
{
	p = frac( p * float3( 0.1031, 0.1030, 0.0973 ) );
	p += dot( p, p.yxz + 33.33 );
	return frac( ( p.xxy + p.yxx ) * p.zyx ) * 2.0 - 1.0;
}

float GradCorner( float3 i, float3 f, float3 o )
{
	return dot( Hash33( i + o ), f - o );
}

// Perlin (gradient) noise, 0..1. Gradient rather than value noise: value noise forms square,
// grid-aligned blobs that read as hard edges once the smoke is scaled up.
float Noise3( float3 p )
{
	float3 i = floor( p );
	float3 f = frac( p );
	float3 u = f * f * f * ( f * ( f * 6.0 - 15.0 ) + 10.0 );
	float n = lerp(
		lerp( lerp( GradCorner( i, f, float3( 0, 0, 0 ) ), GradCorner( i, f, float3( 1, 0, 0 ) ), u.x ),
		      lerp( GradCorner( i, f, float3( 0, 1, 0 ) ), GradCorner( i, f, float3( 1, 1, 0 ) ), u.x ), u.y ),
		lerp( lerp( GradCorner( i, f, float3( 0, 0, 1 ) ), GradCorner( i, f, float3( 1, 0, 1 ) ), u.x ),
		      lerp( GradCorner( i, f, float3( 0, 1, 1 ) ), GradCorner( i, f, float3( 1, 1, 1 ) ), u.x ), u.y ), u.z );
	return saturate( 0.5 + 0.8 * n ); // raw Perlin sits in about ±0.6
}

// The facing rim: faint where the surface faces the camera, bright where it turns away. n and toCamera normalized.
float GhostRim( float3 n, float3 toCamera )
{
	return pow( 1.0 - saturate( dot( n, toCamera ) ), g_flRimPower ) * g_flRimStrength;
}

// The smoke's opacity multiplier at p — soft low-frequency noise drifting up, a gentle breathing rather than wisps.
// p is in whatever frame the smoke should ride: the SDF ghost passes its own local frame so the smoke travels with
// it; short-lived meshes (puffs) just pass world space.
float GhostSmoke( float3 p )
{
	float3 sp = ( p - float3( 0, 0, g_flTime * g_flSmokeSpeed ) ) * g_flSmokeScale;
	float smoke = Noise3( sp ) * 0.75 + Noise3( sp * 1.9 + 7.3 ) * 0.25;
	return lerp( 1.0, 0.35 + 1.0 * smoke, g_flSmokeStrength );
}

// A ghost surface: colour leans to the rim colour as the rim rises; opacity is the base floor plus the rim plus
// whatever else the caller adds (the SDF ghost's thickness). Returns rgb (unlit) + alpha, before GhostFinish.
float4 GhostShade( float rim, float extraOpacity )
{
	return float4( lerp( g_vBodyColor.rgb, g_vRimColor.rgb, saturate( rim ) ),
		saturate( g_flBaseOpacity + extraOpacity + rim ) );
}

// Last step for every ghost pixel: brightness (feeds bloom) and the body colour's alpha as the overall opacity.
float4 GhostFinish( float3 rgb, float alpha )
{
	return float4( rgb * g_flBrightness, alpha * g_vBodyColor.a );
}
