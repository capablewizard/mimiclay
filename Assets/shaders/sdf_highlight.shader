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

	#include "sdf_field_union.hlsl" // field slots + SdfDistAll (union of the members' baked fields)

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

	float4 MainPs( PixelInput i, bool isFrontFace : SV_IsFrontFace ) : SV_Target0
	{
		// March exit faces only: both proxy faces raster the identical ray (and depth test is off,
		// so nothing culls the far one) — marching one halves the cost. Exit faces are the robust
		// pick because they survive the camera entering the proxy. SV_IsFrontFace reads INVERTED on
		// these proxy boxes (see sdf_raymarch, measured 2026-10-25): the exit faces report as front, so
		// keep isFrontFace — clipping it kept the entry faces and the highlight vanished up close.
		// The proxy is a single convex box, so exactly one exit face rasters per pixel — which is
		// also why the group shares ONE proxy: overlapping per-member proxies would double-blend.
		if ( !isFrontFace )
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
		// A displaced field is no longer unit-gradient — the BAKE subtracted the noise into it,
		// inflating its slope by up to that member's L (pushed per slot as DispGradL, sized from the
		// worst boil tick — see ApplyHighlightAttributes): understep by 1/(1+L) and grow the budget
		// by the same so grazing rays still converge — the same insurance as sdf_raymarch. PER SLOT
		// with the steepest member winning; 0 everywhere (no displacing member) is an exact no-op.
		float L = max( max( g_flDispGradL0, g_flDispGradL1 ), max( g_flDispGradL2, g_flDispGradL3 ) );
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
