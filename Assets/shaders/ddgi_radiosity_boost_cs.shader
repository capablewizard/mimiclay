//=========================================================================================================================
// Fake radiosity on the DDGI irradiance atlas. The engine's probe captures don't feed the volume's own GI back
// into the bake (verified empirically — seeded irradiance provably lands in the bake target and the result never
// changes), so extra bounces can't come from re-rendering. Instead this diffuses light directly between probes:
// each irradiance texel gathers from its 6 axis-neighbour probes, weighted by how much the texel faces them and
// by a Chebyshev visibility test against the probe's own baked distance moments (so light doesn't glow through
// walls). One iteration ~= one fake bounce: out = base * baseScale + gain * gather(prev). Runs in milliseconds on the tiny
// atlas, so ProbeRadiosityBoost re-runs it live as sliders move.
//
// Atlas layout facts (must match ddgi_integrate_cs): irradiance = 8x8 borderless octahedral tiles, LINEAR values;
// distance = 16x16 tiles, rg = (mean, mean^2); texel i faces OctahedralDecode((i + 0.5) / N); edge texels are
// seam-blended 50/50 with their mirrored partner, replicated here so boosted light doesn't crack at tile seams.
//=========================================================================================================================
MODES
{
	Default();
}

CS
{
	#include "system.fxc"

	Texture3D<float4>   g_tBase       < Attribute( "BoostBase" ); >;       // saved bake, BC6H ok (HW decode on Load)
	Texture3D<float4>   g_tPrev       < Attribute( "BoostPrev" ); >;       // previous iteration, RGBA16F
	RWTexture3D<float4> g_tOut        < Attribute( "BoostOut" ); >;        // this iteration's output, RGBA16F
	Texture3D<float4>   g_tDistance   < Attribute( "BoostDistance" ); >;   // 16x16 moment tiles
	Texture3D<float4>   g_tRelocation < Attribute( "BoostRelocation" ); >; // w > 0.5 = probe active

	float3 g_vProbeCounts  < Attribute( "BoostCounts" ); >;                // probes per axis
	float3 g_vProbeSpacing < Attribute( "BoostSpacing" ); >;               // world units between probes per axis
	float  g_flBaseScale   < Attribute( "BoostBaseScale" ); Default( 1.0 ); >; // Level <= 1: blend bake -> fallback

	// What the scene's ambient would be with NO DDGI volume (AmbientLight::FromEnvMapProbe): the envmap cubemap at
	// its blurriest mip × the probe's tint. Level < 1 blends the bake toward this instead of toward black.
	TextureCube<float4> g_tFallbackEnv < Attribute( "BoostFallbackEnv" ); >;
	SamplerState g_sFallbackEnv < Filter( TRILINEAR ); AddressU( WRAP ); AddressV( WRAP ); AddressW( WRAP ); >;
	float  g_flFallbackMip  < Attribute( "BoostFallbackMip" ); Default( 0.0 ); >;
	float3 g_vFallbackTint  < Attribute( "BoostFallbackTint" ); Default3( 1.0, 1.0, 1.0 ); >;
	int    g_nUseFallback   < Attribute( "BoostUseFallback" ); Default( 0 ); >;
	float  g_flGain        < Attribute( "BoostGain" ); Default( 0.5 ); >;      // Level - 1
	float  g_flWall        < Attribute( "BoostWall" ); Default( 1.0 ); >;  // 0 = light ignores geometry
	// 1 = physically coloured bounce; 0 = bounce carries the same energy but white. Kills the compounding
	// tint takeover (yellow floor -> everything yellow after a few iterations) without losing the fill.
	float  g_flBounceSat   < Attribute( "BoostBounceSat" ); Default( 1.0 ); >;
	// Hue-only bounce tint (unit luminance). Applied every iteration, so it compounds like a coloured surface would.
	// Ambient floor + contrast live in ddgi_grade_cs, run once after the last iteration.
	float3 g_vBounceTint   < Attribute( "BoostTint" ); Default3( 1.0, 1.0, 1.0 ); >;
	int    g_nUseRelocation < Attribute( "BoostUseRelocation" ); Default( 1 ); >;
	int    g_nUseDistance   < Attribute( "BoostUseDistance" ); Default( 1 ); >;

	#define IRR_RES 8
	#define DIST_RES 16

	groupshared float3 g_Tile[IRR_RES][IRR_RES];

	float3 OctahedralDecode( float2 octCoord )
	{
		float2 oct = ( octCoord * 2.0f ) - 1.0f;
		float3 direction = float3( oct.xy, 1.0f - abs( oct.x ) - abs( oct.y ) );
		if ( direction.z < 0.0f )
		{
			float2 signNotZero = float2( ( direction.x >= 0.0f ) ? 1.0f : -1.0f, ( direction.y >= 0.0f ) ? 1.0f : -1.0f );
			direction.xy = ( 1.0f - abs( direction.yx ) ) * signNotZero;
		}
		return normalize( direction );
	}

	float2 OctahedralEncode( float3 direction )
	{
		float l1norm = abs( direction.x ) + abs( direction.y ) + abs( direction.z );
		float2 result = direction.xy * ( 1.0f / l1norm );
		if ( direction.z < 0.0f )
		{
			float2 signNotZero = float2( ( result.x >= 0.0f ) ? 1.0f : -1.0f, ( result.y >= 0.0f ) ? 1.0f : -1.0f );
			result = ( 1.0f - abs( result.yx ) ) * signNotZero;
		}
		return ( result * 0.5f ) + 0.5f;
	}

	// Chebyshev visibility of a point at range r given this probe's (mean, mean^2) distance moments toward it.
	// Same shape as DDGI::ComputeVisibility, with the variance actually derived from the moments.
	float Visibility( float r, float2 moments )
	{
		float mean = moments.x;
		if ( r <= mean * 1.01f )
			return 1.0f;

		float variance = max( moments.y - mean * mean, 1.0f );
		float delta = r - mean;
		float cheb = variance / ( variance + delta * delta );
		return cheb * cheb * cheb;
	}

	[numthreads( IRR_RES, IRR_RES, 1 )]
	void MainCs( uint3 groupId : SV_GroupID, uint3 threadId : SV_GroupThreadID )
	{
		int3 probe = int3( groupId );                // one group per probe tile
		int2 texel = int2( threadId.xy );
		int3 atlasCoord = int3( probe.xy * IRR_RES + texel, probe.z );

		int3 probeCounts = int3( g_vProbeCounts + 0.5f );

		float3 dir = OctahedralDecode( ( float2( texel ) + 0.5f ) / float( IRR_RES ) );
		float4 basePx = g_tBase.Load( int4( atlasCoord, 0 ) );
		// Sanitize: the bake can hold Inf texels (BC6H decode / integrate overflow). Harmless when sampled raw, but
		// lerp( fallback, base, 0 ) = fallback + 0 * Inf = NaN — black splotches smeared by TAA at Level 0.
		basePx.rgb = clamp( basePx.rgb, 0.0f, 65504.0f );

		float3 gathered = 0.0f;
		float weightSum = 0.0f;

		const int3 axes[6] = { int3( 1, 0, 0 ), int3( -1, 0, 0 ), int3( 0, 1, 0 ), int3( 0, -1, 0 ), int3( 0, 0, 1 ), int3( 0, 0, -1 ) };

		[unroll]
		for ( int i = 0; i < 6; i++ )
		{
			int3 axis = axes[i];
			int3 n = probe + axis;
			if ( any( n < 0 ) || any( n >= probeCounts ) )
				continue;

			// Dead probes (inside geometry) neither give nor take light
			if ( g_nUseRelocation != 0 && g_tRelocation.Load( int4( n, 0 ) ).w <= 0.5f )
				continue;

			float3 toNeighbour = float3( axis );
			float w = saturate( dot( dir, toNeighbour ) );  // texels facing the neighbour receive its light
			if ( w <= 0.0f )
				continue;

			// Wall check: this probe's own baked distance moments toward the neighbour vs the gap distance
			if ( g_nUseDistance != 0 && g_flWall > 0.0f )
			{
				float2 octN = OctahedralEncode( toNeighbour );
				int2 dTexel = clamp( int2( octN * DIST_RES ), 0, DIST_RES - 1 );
				float2 moments = g_tDistance.Load( int4( probe.xy * DIST_RES + dTexel, probe.z, 0 ) ).rg;
				float gap = dot( abs( toNeighbour ), g_vProbeSpacing );
				w *= lerp( 1.0f, Visibility( gap, moments ), g_flWall );
			}

			// Sample the neighbour's tile at the SAME direction — light flowing through the grid
			int3 nCoord = int3( n.xy * IRR_RES + texel, n.z );
			gathered += w * g_tPrev.Load( int4( nCoord, 0 ) ).rgb;
			weightSum += w;
		}

		float3 bounce = weightSum > 0.0f ? gathered / weightSum : 0.0f;

		// Desaturate the bounce toward equal-energy white (engine Luminance helper exists in raster includes
		// but not here, so weigh it ourselves).
		float bounceLum = dot( bounce, float3( 0.2126f, 0.7152f, 0.0722f ) );
		bounce = lerp( bounceLum.xxx, bounce, g_flBounceSat );

		bounce *= g_vBounceTint;

		// The engine looks a surface up at texel = -normal (DDGI::Evaluate), so texel dir d serves surfaces facing -d:
		// sample the envmap at -d, as FromEnvMapProbe would for that normal. Verified against the real no-DDGI render
		// (CharadesZoo_art2, sky fallback): -d matches, +d visibly doesn't.
		float3 fallback = 0.0f;
		if ( g_nUseFallback != 0 )
			fallback = g_tFallbackEnv.SampleLevel( g_sFallbackEnv, -dir, g_flFallbackMip ).rgb * g_vFallbackTint;

		float3 result = lerp( fallback, basePx.rgb, g_flBaseScale ) + g_flGain * bounce;

		// Seam blend, same scheme as the integrator: borderless octahedral tiles are discontinuous at the
		// edges, so edge texels average 50/50 with their mirrored partner on the same edge.
		g_Tile[threadId.y][threadId.x] = result;
		GroupMemoryBarrierWithGroupSync();

		bool onX = ( texel.x == 0 || texel.x == IRR_RES - 1 );
		bool onY = ( texel.y == 0 || texel.y == IRR_RES - 1 );
		if ( onX || onY )
		{
			int2 mirror = texel;
			if ( onY ) mirror.x = ( IRR_RES - 1 ) - texel.x;
			if ( onX ) mirror.y = ( IRR_RES - 1 ) - texel.y;
			result = 0.5f * result + 0.5f * g_Tile[mirror.y][mirror.x];
		}

		g_tOut[atlasCoord] = float4( result, basePx.a );
	}
}
