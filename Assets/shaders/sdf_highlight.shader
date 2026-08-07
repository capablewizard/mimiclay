//=========================================================================================================================
// SDF highlight outline — the raymarched counterpart of the engine's Highlight/HighlightOutline pair, which re-draws MESH
// geometry with a stencil material and so can only ever outline our proxy box, not the sculpted surface. This renders ONE
// translucent proxy box over a GROUP of SDF props (a highlight component's self + descendant renderers — e.g. the hunter's
// head + body) and re-marches the UNION of their baked distance fields (min across up to 4 field slots), so the group gets
// a single combined outline with no interior lines where the parts overlap:
//
//  - ray HITS the union surface       -> inside fill (InsideColor / InsideObscuredColor)
//  - ray passes within Width PIXELS   -> outline (OutlineColor / OutlineObscuredColor)
//
// Field-only (no copy of the analytic brush evaluator to keep in sync with sdf_raymarch/sdf_eval). "Within Width pixels"
// is the classic sphere-trace near-miss trick: track the minimum union distance along the ray, converted to screen pixels
// at that depth (d / (t * worldPerPixelAtUnitDist)), so the outline hugs the true silhouette at a constant on-screen width.
//
// Each slot's sample is REPAIRED outside its own grid: the union bracket spans every member's region, so slot A gets
// sampled far outside its grid where the CLAMP sampler returns the tiny boundary value — raw, that would smear a false
// outline (and a crawling march) across the whole bracket. Combining the clamped sample with the distance to the grid
// region (the same Pythagorean lower bound sdText uses for its quad) keeps the union field valid everywhere.
//
// Obscured-vs-visible comes from the scene depth chain, which can be a frame stale while sculpting — so occluders that the
// union field says are ON (or within SelfOcclusionBand of) the group's own surface don't count as obscuring (kills the
// edit-time colour flicker). Depth test is OFF so the obscured pair shows through walls; S_TRANSLUCENT sorts us after
// opaques. Driven by SdfHighlightOutline, which mirrors the field bindings from each SdfRaymarchRenderer per frame.
//=========================================================================================================================
HEADER
{
	Description = "SDF Highlight Outline";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward(); // no Depth() mode: the highlight must not enter the prepass or cast shadows
}

COMMON
{
	// Sort into the translucent pass (drawn after opaques + skybox) and get the standard
	// SRC_ALPHA/INV_SRC_ALPHA over-blend + depth-write-off for free (see shadow_catcher.shader).
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
	#include "common/classes/Depth.hlsl" // scene depth: picks the visible vs obscured colour pair

	RenderState( CullMode, NONE );
	// Depth test OFF (S_TRANSLUCENT only disabled the write): the obscured colours must draw
	// through whatever covers the group — occlusion is resolved in-shader against the depth chain.
	RenderState( DepthEnable, false );

	// ── Shared march state (set by SdfHighlightOutline: union bracket + march budget) ──
	int    g_nFieldCount    < Attribute( "FieldCount" ); Default( 1 ); >; // occupied slots (1..4)
	float3 g_vBoundsMin     < Attribute( "BoundsMin" ); >;                // union world AABB bracket
	float3 g_vBoundsMax     < Attribute( "BoundsMax" ); >;
	int    g_nMaxSteps      < Attribute( "MaxSteps" ); Default( 96 ); >;
	float  g_flEpsilon      < Attribute( "Epsilon" ); Default( 0.05 ); >;

	SamplerState g_sField < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); AddressW( CLAMP ); >;

	// ── Field slots, one per grouped SdfRaymarchRenderer (mirrored by ApplyHighlightAttributes).
	// MUST match SdfHighlightOutline.MaxTargets. Same layout per slot: dense volume + optional
	// sparse brick atlas + the prop's model fold (the field is baked in the prop's local frame). ──
	#define SDF_BLOCK    8.0
	#define SDF_TILESIZE 9.0
	#define SDF_TILESX   16u
	#define SDF_TILESY   16u

	Texture3D g_tField0 < Attribute( "FieldTex0" ); >;
	float3 g_vFieldMin0   < Attribute( "FieldMin0" ); >;
	float3 g_vFieldMax0   < Attribute( "FieldMax0" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims0  < Attribute( "FieldDims0" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims0< Attribute( "SurfaceDims0" ); >;
	float3 g_vModelOrigin0< Attribute( "ModelOrigin0" ); >;
	float4 g_vModelRot0   < Attribute( "ModelRotation0" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas0       < Attribute( "Atlas0" ); >;
	Texture2D g_tIndirection0 < Attribute( "IndirectionTex0" ); >;
	float3 g_vBrickDims0 < Attribute( "BrickDims0" ); >;
	float3 g_vAtlasDims0 < Attribute( "AtlasDims0" ); >;
	int    g_nSdfSparse0 < Attribute( "SdfSparse0" ); Default( 0 ); >;
	float  g_flDispAmp0  < Attribute( "DispAmp0" ); Default( 0.0 ); >;  // 0 = member's Displace off
	float  g_flDispFreq0 < Attribute( "DispFreq0" ); Default( 0.25 ); >;
	float  g_flBoilSeed0 < Attribute( "BoilSeed0" ); Default( 0.0 ); >; // must match the member's own seed

	Texture3D g_tField1 < Attribute( "FieldTex1" ); >;
	float3 g_vFieldMin1   < Attribute( "FieldMin1" ); >;
	float3 g_vFieldMax1   < Attribute( "FieldMax1" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims1  < Attribute( "FieldDims1" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims1< Attribute( "SurfaceDims1" ); >;
	float3 g_vModelOrigin1< Attribute( "ModelOrigin1" ); >;
	float4 g_vModelRot1   < Attribute( "ModelRotation1" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas1       < Attribute( "Atlas1" ); >;
	Texture2D g_tIndirection1 < Attribute( "IndirectionTex1" ); >;
	float3 g_vBrickDims1 < Attribute( "BrickDims1" ); >;
	float3 g_vAtlasDims1 < Attribute( "AtlasDims1" ); >;
	int    g_nSdfSparse1 < Attribute( "SdfSparse1" ); Default( 0 ); >;
	float  g_flDispAmp1  < Attribute( "DispAmp1" ); Default( 0.0 ); >;
	float  g_flDispFreq1 < Attribute( "DispFreq1" ); Default( 0.25 ); >;
	float  g_flBoilSeed1 < Attribute( "BoilSeed1" ); Default( 0.0 ); >;

	Texture3D g_tField2 < Attribute( "FieldTex2" ); >;
	float3 g_vFieldMin2   < Attribute( "FieldMin2" ); >;
	float3 g_vFieldMax2   < Attribute( "FieldMax2" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims2  < Attribute( "FieldDims2" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims2< Attribute( "SurfaceDims2" ); >;
	float3 g_vModelOrigin2< Attribute( "ModelOrigin2" ); >;
	float4 g_vModelRot2   < Attribute( "ModelRotation2" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas2       < Attribute( "Atlas2" ); >;
	Texture2D g_tIndirection2 < Attribute( "IndirectionTex2" ); >;
	float3 g_vBrickDims2 < Attribute( "BrickDims2" ); >;
	float3 g_vAtlasDims2 < Attribute( "AtlasDims2" ); >;
	int    g_nSdfSparse2 < Attribute( "SdfSparse2" ); Default( 0 ); >;
	float  g_flDispAmp2  < Attribute( "DispAmp2" ); Default( 0.0 ); >;
	float  g_flDispFreq2 < Attribute( "DispFreq2" ); Default( 0.25 ); >;
	float  g_flBoilSeed2 < Attribute( "BoilSeed2" ); Default( 0.0 ); >;

	Texture3D g_tField3 < Attribute( "FieldTex3" ); >;
	float3 g_vFieldMin3   < Attribute( "FieldMin3" ); >;
	float3 g_vFieldMax3   < Attribute( "FieldMax3" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims3  < Attribute( "FieldDims3" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims3< Attribute( "SurfaceDims3" ); >;
	float3 g_vModelOrigin3< Attribute( "ModelOrigin3" ); >;
	float4 g_vModelRot3   < Attribute( "ModelRotation3" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas3       < Attribute( "Atlas3" ); >;
	Texture2D g_tIndirection3 < Attribute( "IndirectionTex3" ); >;
	float3 g_vBrickDims3 < Attribute( "BrickDims3" ); >;
	float3 g_vAtlasDims3 < Attribute( "AtlasDims3" ); >;
	int    g_nSdfSparse3 < Attribute( "SdfSparse3" ); Default( 0 ); >;
	float  g_flDispAmp3  < Attribute( "DispAmp3" ); Default( 0.0 ); >;
	float  g_flDispFreq3 < Attribute( "DispFreq3" ); Default( 0.25 ); >;
	float  g_flBoilSeed3 < Attribute( "BoilSeed3" ); Default( 0.0 ); >;

	// Claymation boil, PER SLOT — it's opt-in per GameObject, so one group can hold a boiling member
	// and a still one (a hunter's head and body) and a single shared set would be wrong for one of
	// them. Each slot's values must match what that member's own sdf_raymarch pass is using, or the
	// outline swims off the silhouette by the difference. Default 0 = still, which is what an
	// unoccupied slot and a member with no ClayBoil both get.
	float g_flBoilFps0 < Attribute( "BoilFps0" ); Default( 0.0 ); >;
	float g_flBoilFps1 < Attribute( "BoilFps1" ); Default( 0.0 ); >;
	float g_flBoilFps2 < Attribute( "BoilFps2" ); Default( 0.0 ); >;
	float g_flBoilFps3 < Attribute( "BoilFps3" ); Default( 0.0 ); >;
	float g_flBoilJitter0 < Attribute( "BoilJitter0" ); Default( 0.0 ); >;
	float g_flBoilJitter1 < Attribute( "BoilJitter1" ); Default( 0.0 ); >;
	float g_flBoilJitter2 < Attribute( "BoilJitter2" ); Default( 0.0 ); >;
	float g_flBoilJitter3 < Attribute( "BoilJitter3" ); Default( 0.0 ); >;
	float g_flBoilAmpJitter0 < Attribute( "BoilAmpJitter0" ); Default( 0.0 ); >;
	float g_flBoilAmpJitter1 < Attribute( "BoilAmpJitter1" ); Default( 0.0 ); >;
	float g_flBoilAmpJitter2 < Attribute( "BoilAmpJitter2" ); Default( 0.0 ); >;
	float g_flBoilAmpJitter3 < Attribute( "BoilAmpJitter3" ); Default( 0.0 ); >;

	// ── Highlight look (set per-object by SdfHighlightOutline) ──
	float4 g_vOutlineColor         < Attribute( "OutlineColor" );         Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vOutlineObscuredColor < Attribute( "OutlineObscuredColor" ); Default4( 0.0, 0.0, 0.0, 0.4 ); >;
	float4 g_vInsideColor          < Attribute( "InsideColor" );          Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vInsideObscuredColor  < Attribute( "InsideObscuredColor" );  Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float  g_flLineWidthPx < Attribute( "LineWidthPx" ); Default( 4.0 ); >;   // outline width, screen pixels
	float  g_flPixelScale  < Attribute( "PixelScale" );  Default( 0.001 ); >; // world units per pixel at unit distance
	// Outline placement (screen px): the union field is ERODED by this much before marching, which
	// slides the whole band across the silhouette — 0 = outside (band beyond the edge), Width/2 =
	// centred, Width = inside (band on the prop's own pixels; they carry the prop's depth, so DOF
	// and fog treat the line as part of the prop instead of a halo at background depth). The fill
	// region becomes the eroded object, so fill and band always tile with no gap or overlap.
	float  g_flErodePx     < Attribute( "ErodePx" ); Default( 0.0 ); >;
	// Occluders within this union-field distance of the group's own surface don't count as
	// "obscuring" — see the self-occlusion test in MainPs. Sized to cover a frame of brush-drag travel.
	float  g_flSelfBand    < Attribute( "SelfOcclusionBand" ); Default( 6.0 ); >;

	// Rotate vector v by quaternion q (same as sdf_raymarch).
	float3 qrot( float4 q, float3 v )
	{
		return v + 2.0 * cross( q.xyz, cross( q.xyz, v ) + q.w * v );
	}

	// --- Plasticine displacement noise — EXACT copies of sdf_raymarch's hash13/vnoise/Displacement
	// so the highlight's silhouette tracks the main shader's lumps sample-for-sample. Members with
	// D_DISPLACE off (or the component's MatchDisplacement off) arrive with amp 0 and skip it. ---

	float hash13( float3 p3 )
	{
		p3 = frac( p3 * 0.1031 );
		p3 += dot( p3, p3.zyx + 31.32 );
		return frac( (p3.x + p3.y) * p3.z );
	}

	float vnoise( float3 x )
	{
		float3 i = floor( x );
		float3 f = x - i;
		f = f * f * (3.0 - 2.0 * f);
		return lerp(
			lerp( lerp( hash13( i + float3( 0, 0, 0 ) ), hash13( i + float3( 1, 0, 0 ) ), f.x ),
			      lerp( hash13( i + float3( 0, 1, 0 ) ), hash13( i + float3( 1, 1, 0 ) ), f.x ), f.y ),
			lerp( lerp( hash13( i + float3( 0, 0, 1 ) ), hash13( i + float3( 1, 0, 1 ) ), f.x ),
			      lerp( hash13( i + float3( 0, 1, 1 ) ), hash13( i + float3( 1, 1, 1 ) ), f.x ), f.y ),
			f.z );
	}

	float3 BoilOffset( float tick )
	{
		return float3( hash13( float3( tick, 17.13, 3.71 ) ),
		               hash13( float3( tick, 53.77, 8.29 ) ),
		               hash13( float3( tick, 91.31, 5.13 ) ) ) - 0.5;
	}

	// Signed displacement at a MODEL-space point (matches sdf_raymarch.Displacement, which samples
	// in model space so the lumps are locked to the prop). The claymation boil is reproduced here
	// line-for-line — see the long comment on sdf_raymarch.Displacement for why each piece is shaped
	// this way. Same g_flTime, same quantisation, same per-prop seed => the same surface this frame.
	float SlotDisplacement( float3 lp, float amp, float freq, float seed, float fps, float jitter, float ampJitter )
	{
		float3 x = lp * freq;
		float3 b0 = 0.0, b1 = 0.0;

		if ( fps > 0.0 )
		{
			float tick = fmod( floor( g_flTime * fps ), 1024.0 ) + seed; // wrap: see sdf_raymarch
			b0 = BoilOffset( tick )         * jitter;
			b1 = BoilOffset( tick + 101.0 ) * jitter * 2.7;
			amp *= 1.0 + ( hash13( float3( tick, 7.77, 1.23 ) ) - 0.5 ) * ampJitter;
		}

		float n = vnoise( x + b0 ) * 0.67 + vnoise( x * 2.03 + 11.7 + b1 ) * 0.33;
		return (n * 2.0 - 1.0) * amp;
	}

	// Sample ONE slot's baked field at world point wp: fold into that prop's local frame, fetch the
	// dense volume (or the sparse brick atlas near the surface), then REPAIR the sample outside the
	// slot's grid. The repair is what makes the union valid: the shared bracket spans every member's
	// region, and outside its own grid a slot's CLAMP sampler returns the tiny boundary value — raw,
	// that false "always near" reading smears outline across the whole box and stalls the march.
	// dist² ≥ distToRegion² + clampedSample² is the same smooth lower bound sdText uses for its quad
	// (the legs can't oppose: the nearest surface is inward of the clamp point, wp is outward of it).
	float SampleSlot(
		Texture3D field, Texture3D atlas, Texture2D indirection, int sparse,
		float3 fmin, float3 fmax, float3 fdims, float3 sdims, float3 bdims, float3 adims,
		float3 origin, float4 rot, float dispAmp, float dispFreq,
		float boilSeed, float boilFps, float boilJitter, float boilAmpJitter, float3 wp )
	{
		float3 lp = qrot( float4( -rot.xyz, rot.w ), wp - origin );

		// Dense fetch (the sparse path's low-res guide is the same texture).
		float3 ext = max( fmax - fmin, 1e-4 );
		float3 t   = (lp - fmin) / ext;
		float3 uvw = (t * (fdims - 1.0) + 0.5) / fdims;
		float d = field.SampleLevel( g_sField, uvw, 0 ).r;

		if ( sparse != 0 )
		{
			// Near the surface, swap the guide for the high-res brick atlas (see sdf_raymarch).
			float3 scell = ext / max( sdims - 1.0, 1.0 );
			float  nearBand = 2.0 * SDF_BLOCK * max( scell.x, max( scell.y, scell.z ) );
			if ( d <= nearBand )
			{
				float3 fv    = (lp - fmin) / scell;
				int3   bd    = (int3)( bdims + 0.5 );
				int3   brick = (int3)floor( fv / SDF_BLOCK );
				if ( !( any( brick < 0 ) || any( brick >= bd ) ) )
				{
					float ind = indirection.Load( int3( brick.x, brick.y + bd.y * brick.z, 0 ) ).r;
					if ( ind >= 0.0 )
					{
						float3 vit = fv - (float3)brick * SDF_BLOCK;
						uint   tile = (uint)( ind + 0.5 );
						int3   tc  = int3( tile % SDF_TILESX, ( tile / SDF_TILESX ) % SDF_TILESY, tile / ( SDF_TILESX * SDF_TILESY ) );
						float3 av  = (float3)tc * SDF_TILESIZE + vit;
						d = atlas.SampleLevel( g_sField, ( av + 0.5 ) / adims, 0 ).r;
					}
				}
			}
		}

		// Displacement: subtract the same noise the main shader subtracts, so the band tracks the
		// LUMPY silhouette instead of the smooth baked one (D_DISPLACE bends the silhouette, and
		// the noise is deliberately NOT baked into the field). Applied before the repair, at the
		// query point in model space — exactly where sdf_raymarch applies it.
		if ( dispAmp > 0.0 )
			d -= SlotDisplacement( lp, dispAmp, dispFreq, boilSeed, boilFps, boilJitter, boilAmpJitter );

		// Out-of-region repair (see above). Inside the grid dq = 0 and the sample passes through
		// untouched (including negative, inside-the-prop values).
		float3 q  = max( max( fmin - lp, lp - fmax ), 0.0 );
		float  dq = length( q );
		if ( dq > 0.0 )
		{
			float dz = max( d, 0.0 );
			d = sqrt( dq * dq + dz * dz );
		}
		return d;
	}

	// Union distance over the occupied slots — min() of per-slot distances, the combined group surface.
	float SdfDistAll( float3 p )
	{
		float d = SampleSlot( g_tField0, g_tAtlas0, g_tIndirection0, g_nSdfSparse0,
			g_vFieldMin0, g_vFieldMax0, g_vFieldDims0, g_vSurfaceDims0, g_vBrickDims0, g_vAtlasDims0,
			g_vModelOrigin0, g_vModelRot0, g_flDispAmp0, g_flDispFreq0,
				g_flBoilSeed0, g_flBoilFps0, g_flBoilJitter0, g_flBoilAmpJitter0, p );

		if ( g_nFieldCount > 1 )
			d = min( d, SampleSlot( g_tField1, g_tAtlas1, g_tIndirection1, g_nSdfSparse1,
				g_vFieldMin1, g_vFieldMax1, g_vFieldDims1, g_vSurfaceDims1, g_vBrickDims1, g_vAtlasDims1,
				g_vModelOrigin1, g_vModelRot1, g_flDispAmp1, g_flDispFreq1,
				g_flBoilSeed1, g_flBoilFps1, g_flBoilJitter1, g_flBoilAmpJitter1, p ) );

		if ( g_nFieldCount > 2 )
			d = min( d, SampleSlot( g_tField2, g_tAtlas2, g_tIndirection2, g_nSdfSparse2,
				g_vFieldMin2, g_vFieldMax2, g_vFieldDims2, g_vSurfaceDims2, g_vBrickDims2, g_vAtlasDims2,
				g_vModelOrigin2, g_vModelRot2, g_flDispAmp2, g_flDispFreq2,
				g_flBoilSeed2, g_flBoilFps2, g_flBoilJitter2, g_flBoilAmpJitter2, p ) );

		if ( g_nFieldCount > 3 )
			d = min( d, SampleSlot( g_tField3, g_tAtlas3, g_tIndirection3, g_nSdfSparse3,
				g_vFieldMin3, g_vFieldMax3, g_vFieldDims3, g_vSurfaceDims3, g_vBrickDims3, g_vAtlasDims3,
				g_vModelOrigin3, g_vModelRot3, g_flDispAmp3, g_flDispFreq3,
				g_flBoilSeed3, g_flBoilFps3, g_flBoilJitter3, g_flBoilAmpJitter3, p ) );

		return d;
	}

	float4 MainPs( PixelInput i, bool isFrontFace : SV_IsFrontFace ) : SV_Target0
	{
		// March back faces only: both proxy faces raster the identical ray (and depth test is off,
		// so nothing culls the far one) — marching one halves the cost. Back faces are the robust
		// pick because they survive the camera entering the proxy (same trick as sdf_raymarch).
		// The proxy is a single convex box, so exactly one back face rasters per pixel — which is
		// also why the group shares ONE proxy: overlapping per-member proxies would double-blend.
		if ( isFrontFace )
		{
			clip( -1 );
			return 0;
		}

		float3 ro = g_vCameraPositionWs;
		float3 rd = normalize( i.vPositionWithOffsetWs );

		// Bracket the march against the union world AABB.
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

		// Sphere-trace the union field ERODED by ErodePx (see its declaration — this is what slides
		// the band inside/centre/outside the silhouette), tracking the closest approach in SCREEN
		// PIXELS (world distance divided by the pixel footprint at that depth) — that's what makes
		// the outline a constant on-screen width instead of a world-unit shell. A "hit" is a hit on
		// the ERODED surface, so in inside mode the band pixels are rays that pierce the real
		// surface but miss the eroded one. The erosion grows with t (screen-constant), which adds
		// only ~ErodePx*PixelScale (~0.03) to the field's directional slope — comfortably inside
		// the 0.9 understep insurance already there for trilinear interpolation (as sdf_raymarch).
		// A displaced field is no longer unit-gradient (subtracting the noise inflates its slope by
		// ~L = amp·freq·4, the steepest member winning): understep by 1/(1+L) and grow the budget
		// by the same so grazing rays still converge — the same insurance as sdf_raymarch. Amp 0
		// everywhere (no displacing member / MatchDisplacement off) makes this an exact no-op.
		// The boil's amp wobble can push a member above its authored amp, so size L from that bound
		// (matches sdf_raymarch — a per-tick L would understep on the ticks that wobble up). Folded in
		// PER SLOT: with boil opt-in, a still member must not be inflated by a boiling sibling's
		// wobble, and the steepest member still has to win.
		float L = 4.0 * max(
			max( g_flDispAmp0 * g_flDispFreq0 * ( 1.0 + max( g_flBoilAmpJitter0, 0.0 ) * 0.5 ),
			     g_flDispAmp1 * g_flDispFreq1 * ( 1.0 + max( g_flBoilAmpJitter1, 0.0 ) * 0.5 ) ),
			max( g_flDispAmp2 * g_flDispFreq2 * ( 1.0 + max( g_flBoilAmpJitter2, 0.0 ) * 0.5 ),
			     g_flDispAmp3 * g_flDispFreq3 * ( 1.0 + max( g_flBoilAmpJitter3, 0.0 ) * 0.5 ) ) );
		float stepScale = 0.9 / (1.0 + L);

		bool  hit = false;
		float minPx = 1e9;
		float tRef = tEnter; // ray distance at the closest approach / hit, for the occlusion test
		float t = tEnter;
		int   maxSteps = (int)( (g_nMaxSteps / 0.9) * min( 1.0 + L, 2.0 ) );

		[loop]
		for ( int s = 0; s < maxSteps; s++ )
		{
			float d = SdfDistAll( ro + rd * t ) + g_flErodePx * t * g_flPixelScale;

			float px = d / max( t * g_flPixelScale, 1e-4 );
			if ( px < minPx )
			{
				minPx = px;
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

		// Visible or obscured? Compare against the nearest opaque surface already in the depth
		// chain. The group wrote its own (camera-nudged) prepass depth, so give it 2 units of
		// grace — its own surface must not count as "obscuring" its fill (absorbs chain/precision
		// wobble at silhouettes).
		//
		// That fixed grace is NOT enough while a prop is being EDITED: the chain this pass samples
		// can be a frame stale, and a dragged brush
		// moves the surface many units per frame — the raw compare then flips visible<->obscured
		// every frame (colour flicker). So identify self-occlusion through the FIELD instead of a
		// bigger bias: sample the union AT the occluding point — on (or within a band of) the
		// group's own surface means the occluder is the group itself, or its one-frame-old ghost,
		// never a wall -> not obscured. The out-of-region repair in SampleSlot keeps this honest
		// for distant occluders (they read their true large distance, not a clamped edge value).
		// The trade: a genuine occluder hugging the group closer than the band reads as visible —
		// fine for a selection highlight.
		float3 sceneWp = Depth::GetWorldPosition( i.vPositionSs.xy );
		float sceneDist = length( sceneWp - ro );
		bool obscured = sceneDist < tRef - 2.0;
		if ( obscured && abs( SdfDistAll( sceneWp ) ) < g_flSelfBand )
			obscured = false;

		float4 col;
		if ( hit )
		{
			col = obscured ? g_vInsideObscuredColor : g_vInsideColor;
		}
		else
		{
			col = obscured ? g_vOutlineObscuredColor : g_vOutlineColor;

			// INSIDE placement seam fix: the band's outer boundary coincides with the silhouette
			// there — but the prop's own forward march draws its edge FAT by ~epsilon (a pixel is
			// shaded whenever a ray passes within eps of the surface, not exactly 0), and the AA
			// fade used to END at the ideal silhouette. Both left a 1-2px rim of the original
			// surface peeking past the line. When the silhouette sits at the outer edge (silEdge
			// -> 1 only when ErodePx reaches the width, i.e. Inside), overshoot the outer boundary
			// by eps-in-pixels-at-this-depth + the 1px AA so the line fully covers the prop's
			// rendered edge; the fringe past the true silhouette is a <=1px fade over background.
			// Outside/Center are untouched (silEdge = 0).
			float epsPx = g_flEpsilon / max( tRef * g_flPixelScale, 1e-4 );
			float silEdge = saturate( 1.0 - (g_flLineWidthPx - g_flErodePx) );
			float outer = g_flLineWidthPx + (epsPx + 1.0) * silEdge;

			// Hard-edged line like the engine outline, with ~1px of smoothstep for anti-aliasing on
			// each side. The inner fade matters for inside/centre placement, where the band's inner
			// boundary (the eroded surface) sits on the prop's own pixels and would otherwise alias
			// against the fill / transparency; outside placement it just softens the line against
			// the prop's edge by the same 1px.
			col.a *= 1.0 - smoothstep( max( outer - 1.0, 0.0 ), outer, minPx );
			col.a *= smoothstep( 0.0, 1.0, minPx );
		}

		if ( col.a <= 0.001 )
		{
			clip( -1 );
			return 0;
		}

		return col;
	}
}
