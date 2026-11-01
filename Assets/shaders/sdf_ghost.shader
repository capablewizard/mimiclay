//=========================================================================================================================
// SDF ghost — a translucent, glowing apparition of a group of SDF props (e.g. a player's head + body), for eliminated
// players flying the spectator cam. Same field-only union march as sdf_highlight.shader (sdf_field_union.hlsl: up to 4
// baked fields blended with min(), so the parts read as ONE body), shaded as unlit emissive layers instead of clay:
//
//  - RIM      fresnel off the field gradient: faint where the surface faces the camera, bright where it turns away —
//             silhouettes AND interior creases (eye sockets, mouth) light up.
//  - BODY     thickness: after the hit the ray keeps stepping through the volume and integrates how much of it is
//             inside, so thick parts read denser and thin parts (ears, fingers) almost vanish — the X-ray look.
//  - HALO     the sphere-trace near-miss: rays that pass close to the surface glow with distance falloff, a soft aura
//             past the silhouette (the proxy box is padded by HaloRadius so it has room).
//  - SMOKE    domain-warped world-space fBm drifting upward, and a fade toward the bottom of the group (trails off).
//
// Translucent (no depth write, no prepass, no shadows). Depth test is OFF and occlusion is resolved in-shader against the
// scene depth chain, so behind-a-wall pixels can still show at ObscuredOpacity (0 = hidden by walls). Driven by SdfGhost,
// which mirrors each member SdfRaymarchRenderer's field bindings and switches the members' own surfaces off.
//=========================================================================================================================
HEADER
{
	Description = "SDF Ghost";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward(); // no Depth() mode: a ghost must not enter the prepass or cast shadows
}

COMMON
{
	// Translucent pass + SRC_ALPHA/INV_SRC_ALPHA blend + depth-write off (see sdf_highlight.shader).
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
	#include "common/classes/Depth.hlsl" // scene depth: walls in front of the ghost

	RenderState( CullMode, NONE );
	RenderState( DepthEnable, false ); // occlusion resolved in-shader (ObscuredOpacity)

	#include "sdf_field_union.hlsl" // field slots + SdfDistAll (union of the members' baked fields)

	// ── Ghost look (set per-object by SdfGhost) ──
	float4 g_vBodyColor      < Attribute( "GhostBodyColor" ); Default4( 0.35, 0.95, 0.65, 1.0 ); >; // rgb = colour, a = opacity scale
	float4 g_vRimColor       < Attribute( "GhostRimColor" );  Default4( 0.75, 1.0, 0.85, 1.0 ); >;
	float  g_flRimPower      < Attribute( "GhostRimPower" ); Default( 2.5 ); >;
	float  g_flRimStrength   < Attribute( "GhostRimStrength" ); Default( 1.0 ); >;
	float  g_flBaseOpacity   < Attribute( "GhostBaseOpacity" ); Default( 0.08 ); >; // flat floor where the surface faces the camera
	float  g_flDensity       < Attribute( "GhostDensity" ); Default( 0.04 ); >;     // per world unit of thickness
	float  g_flHaloRadius    < Attribute( "GhostHaloRadius" ); Default( 6.0 ); >;   // world units
	float  g_flHaloStrength  < Attribute( "GhostHaloStrength" ); Default( 0.35 ); >;
	float  g_flSmokeScale    < Attribute( "GhostSmokeScale" ); Default( 0.05 ); >;    // per world unit (smaller = bigger blobs)
	float  g_flSmokeSpeed    < Attribute( "GhostSmokeSpeed" ); Default( 4.0 ); >;     // world units / s, upward
	float  g_flSmokeStrength < Attribute( "GhostSmokeStrength" ); Default( 0.5 ); >;
	// The ghost's own frame (SdfGhost's transform): the smoke lives in it, so it rides along with the body
	// instead of the body sliding through world-fixed noise.
	float3 g_vGhostOrigin    < Attribute( "GhostOrigin" ); >;
	float4 g_vGhostRotation  < Attribute( "GhostRotation" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float  g_flFadeHeight    < Attribute( "GhostFadeHeight" ); Default( 0.35 ); >;  // fraction of the group height that fades out at the bottom
	float  g_flObscured      < Attribute( "GhostObscuredOpacity" ); Default( 0.0 ); >;
	float  g_flBrightness    < Attribute( "GhostBrightness" ); Default( 1.5 ); >;   // emissive multiplier (feeds bloom)

	#define GHOST_THICK_STEPS 24

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

	// Union field gradient (tetrahedral, 4 taps). The baked field is trilinear, so h of about a voxel keeps it smooth.
	float3 GhostNormal( float3 p )
	{
		const float h = 0.5;
		const float2 k = float2( 1.0, -1.0 );
		return normalize(
			k.xyy * SdfDistAll( p + k.xyy * h ) +
			k.yyx * SdfDistAll( p + k.yyx * h ) +
			k.yxy * SdfDistAll( p + k.yxy * h ) +
			k.xxx * SdfDistAll( p + k.xxx * h ) );
	}

	float4 MainPs( PixelInput i, bool isFrontFace : SV_IsFrontFace ) : SV_Target0
	{
		// Exit faces only — one per pixel on the convex proxy, and they survive the camera entering it.
		// SV_IsFrontFace reads INVERTED on these proxy boxes (sdf_raymarch, measured 2026-10-25): the exit
		// faces report as front, so keep isFrontFace. Clipping it instead kept the entry faces, and the
		// ghost vanished whenever the camera flew inside its box.
		if ( !isFrontFace )
		{
			clip( -1 );
			return 0;
		}

		float3 ro = g_vCameraPositionWs;
		float3 rd = normalize( i.vPositionWithOffsetWs );

		float3 t0 = (g_vBoundsMin - ro) / rd;
		float3 t1 = (g_vBoundsMax - ro) / rd;
		float3 tsmall = min( t0, t1 );
		float3 tbig = max( t0, t1 );
		float tEnter = max( max( max( tsmall.x, tsmall.y ), tsmall.z ), 0.0 );
		float tExit = min( min( tbig.x, tbig.y ), tbig.z );

		if ( tExit < tEnter )
		{
			clip( -1 );
			return 0;
		}

		// Sphere-trace the union, tracking the closest approach for the halo (understep as sdf_highlight).
		float L = max( max( g_flDispGradL0, g_flDispGradL1 ), max( g_flDispGradL2, g_flDispGradL3 ) );
		float stepScale = 0.9 / (1.0 + L);

		bool  hit = false;
		float minD = 1e9;
		float tRef = tEnter;
		float t = tEnter;
		int   maxSteps = (int)( (g_nMaxSteps / 0.9) * min( 1.0 + L, 2.0 ) );

		[loop]
		for ( int s = 0; s < maxSteps; s++ )
		{
			float d = SdfDistAll( ro + rd * t );
			if ( d < minD )
			{
				minD = d;
				tRef = t;
			}

			if ( d < g_flEpsilon )
			{
				hit = true;
				tRef = t;
				break;
			}

			t += d * stepScale;
			if ( t > tExit )
				break;
		}

		float3 p = ro + rd * tRef;

		float3 rgb;
		float alpha;
		if ( hit )
		{
			float3 n = GhostNormal( p );
			float facing = saturate( dot( n, -rd ) );
			float rim = pow( 1.0 - facing, g_flRimPower ) * g_flRimStrength;

			// Thickness: fixed steps from the hit to the far side of the bracket, summing the inside length
			// (soft edge over ~1 unit so the bands don't step). Back parts (an ear behind the head) count too.
			float span = tExit - tRef;
			float dt = span / GHOST_THICK_STEPS;
			float thick = 0.0;
			[loop]
			for ( int k = 0; k < GHOST_THICK_STEPS; k++ )
			{
				float dk = SdfDistAll( ro + rd * ( tRef + ( k + 0.5 ) * dt ) );
				thick += saturate( 0.5 - dk ) * dt;
			}
			float body = 1.0 - exp( -thick * g_flDensity );

			alpha = saturate( g_flBaseOpacity + body + rim );
			rgb = lerp( g_vBodyColor.rgb, g_vRimColor.rgb, saturate( rim ) );
		}
		else
		{
			float halo = exp( -max( minD, 0.0 ) / max( g_flHaloRadius, 1e-3 ) ) * g_flHaloStrength;
			// Soft-start so the box faces never show a hard edge where the halo is still non-zero.
			halo *= saturate( 1.0 - minD / ( 3.0 * g_flHaloRadius ) );
			alpha = halo;
			rgb = g_vRimColor.rgb;
		}

		// Smoke: soft low-frequency noise drifting up through the body — a gentle opacity breathing, not
		// wisps. Sampled in the ghost's own frame so it travels with the ghost.
		float3 lp = qrot( float4( -g_vGhostRotation.xyz, g_vGhostRotation.w ), p - g_vGhostOrigin );
		float3 sp = ( lp - float3( 0, 0, g_flTime * g_flSmokeSpeed ) ) * g_flSmokeScale;
		float  smoke = Noise3( sp ) * 0.75 + Noise3( sp * 1.9 + 7.3 ) * 0.25;
		alpha *= lerp( 1.0, 0.35 + 1.0 * smoke, g_flSmokeStrength );

		// Trail off toward the bottom of the group.
		float height = max( g_vBoundsMax.z - g_vBoundsMin.z, 1e-3 );
		float fade = smoothstep( g_vBoundsMin.z, g_vBoundsMin.z + height * max( g_flFadeHeight, 1e-3 ), p.z );
		alpha *= fade;

		// Walls in front of the ghost.
		float3 sceneWp = Depth::GetWorldPosition( i.vPositionSs.xy );
		if ( length( sceneWp - ro ) < tRef - 1.0 )
			alpha *= g_flObscured;

		alpha *= g_vBodyColor.a;
		if ( alpha <= 0.002 )
		{
			clip( -1 );
			return 0;
		}

		return float4( rgb * g_flBrightness, alpha );
	}
}
