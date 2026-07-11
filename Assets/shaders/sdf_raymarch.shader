//=========================================================================================================================
// SDF raymarcher. Renders a bounding box; the pixel shader sphere-traces the SDF brush list
// (fed via a data texture) in world space, reconstructs the hit position + normal, and hands
// them to the standard shading model. Modelled on base/shaders/lpv_debug.shader.
//=========================================================================================================================
HEADER
{
	Description = "SDF Raymarch";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH ); // march into the depth+normals prepass -> SDF enters the engine G-buffer
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
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

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "common/utils/triplanar.hlsl"
	#include "common/classes/Depth.hlsl" // scene depth (Hi-Z chain) for occlusion clamping
	#include "common/gbuffer.hlsl"       // DepthNormals::Output + the S_MODE_DEPTH combo
	#include "common/classes/Light.hlsl" // Light::* + DirectionalLightShadow (transmission lighting)

	RenderState( CullMode, NONE );

	// Overdraw optimisation toggle (runtime-switchable for A/B). When on: skip the redundant
	// back-face march (the box renders both faces so it survives the camera being inside it),
	// and use conservative depth so the GPU keeps early-Z and rejects hidden fragments.
	DynamicCombo( D_OVERDRAW_OPT, 0..1, Sys( All ) );

	// Tight-bounds toggle: bracket the march against a bounding SPHERE proxy (tighter for round
	// props) instead of the AABB. Off = AABB box (can be tighter for long/thin props).
	DynamicCombo( D_TIGHT_BOUNDS, 0..1, Sys( All ) );

	// Experimental: clamp the march to the scene depth buffer (occlude against whatever's already
	// drawn, ~1 frame stale). Fixes the "inside many bounding volumes" crowd case that early-Z
	// can't. Only works if the depth chain is actually readable in this pass.
	DynamicCombo( D_DEPTH_CLAMP, 0..1, Sys( All ) );

	// Plasticine displacement: subtract a low-frequency 3D noise field from the SDF so the
	// raymarched SILHOUETTE goes lumpy (the meshed LOD is untouched). Gated as a combo so it's
	// free when off, and so the step-size safety factor (below) only costs when it's on.
	DynamicCombo( D_DISPLACE, 0..1, Sys( All ) );

		// Distance-field cache: sample the whole brush field from a baked 3D (volume) texture — ONE
		// trilinear fetch per march step instead of looping every brush. This decouples march cost from
		// brush count (a 31-brush prop marches as cheaply as a 1-brush one). The texture is baked in the
		// prop's MODEL/LOCAL space by SdfFieldBaker; the march point is folded back into that frame to
		// sample. SdfShade (colour, once per pixel) still uses the per-brush path — only the hot per-step
		// distance eval is replaced. Off = the original per-step brush loop (exact, brush-count-bound).
		DynamicCombo( D_FIELD_TEX, 0..1, Sys( All ) );

		// Transmission / subsurface scattering. When on: after the surface hit, derive a thickness
		// from the SDF itself (an inward field tap + a short march toward the key light) and add a
		// back-scatter lobe so thin parts glow when back-lit (foliage leaves, skin, ears). Piggybacks
		// the existing forward shade — runs ONCE per pixel, not per march step — so the march cost is
		// untouched. Free when off. Strongly favours D_FIELD_TEX (the thickness march is texture
		// fetches there, vs a full brush-loop per step on the analytic path).
		DynamicCombo( D_TRANSMISSION, 0..1, Sys( All ) );

		// (Per-brush / per-spline-segment AABB cull is toggled by the g_nSdfCull RUNTIME uniform below, NOT a
		// dynamic combo: a 7th combo doubled the variant count and crashed the Vfx shader compiler on a full
		// recompile, which then poisoned the whole package compile. A runtime branch keeps the toggle without
		// multiplying variants. Brush world AABBs are packed in texels 5/6; the spline span bound is a sphere.)

	Texture2D g_tBrushData < Attribute( "BrushData" ); >;
	Texture2D g_tSplineData < Attribute( "SplineData" ); >; // spline control points: xyz = world pos, w = radius
	Texture2D g_tTextSdf < Attribute( "TextSdf" ); >;       // baked text distance fields (R32F, stacked slots)
	SamplerState g_sTextSdf < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	int       g_nBrushCount < Attribute( "BrushCount" ); Default( 0 ); >;
	int       g_nSdfCull    < Attribute( "SdfCull" ); Default( 1 ); >; // 1 = AABB early-out on (runtime toggle, not a combo)
	float3    g_vBoundsMin    < Attribute( "BoundsMin" ); >;
	float3    g_vBoundsMax    < Attribute( "BoundsMax" ); >;
	float3    g_vBoundsCenter < Attribute( "BoundsCenter" ); >;
	float     g_flBoundsRadius < Attribute( "BoundsRadius" ); Default( 1.0 ); >;
	// Object placement, so the pixel shader can re-base the triplanar projection from world into
	// the prop's MODEL space (texture locked to the prop). The march itself stays in world space;
	// only the surface-texturing coordinate frame changes. Assumes unit scale (rotation-only).
	float3    g_vModelOrigin   < Attribute( "ModelOrigin" ); >;
	float4    g_vModelRotation < Attribute( "ModelRotation" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	int       g_nMaxSteps   < Attribute( "MaxSteps" ); Default( 96 ); >;   // close-up step ceiling
	float     g_flEpsilon   < Attribute( "Epsilon" ); Default( 0.05 ); >; // close-up hit threshold
	// Shadow casting (the scene object renders into the sun's ORTHOGRAPHIC cascade views; the march
	// detects those per-view and traces parallel rays). Bias pushes the written shadow depth a touch
	// deeper along the light ray — caster-side acne insurance on top of the engine's receiver-side
	// bias, normally 0. ShadowOnly = render ONLY in ortho (sun-shadow) views: every perspective view
	// (camera forward, camera depth prepass, spot-light shadows) is clipped — the RenderHidden pawn
	// body, mirroring ModelRenderer.ShadowsOnly.
	float     g_flSdfShadowBias < Attribute( "SdfShadowBias" ); Default( 0.0 ); >;
	int       g_nSdfShadowOnly  < Attribute( "SdfShadowOnly" ); Default( 0 ); >;
	// Adaptive quality (scaled by on-screen size): floors used when the object is small/far, and
	// the near/far LOD distances measured in BOUNDING-RADII. Defaults keep quality maxed (LOD off).
	int       g_nMinSteps   < Attribute( "MinSteps" );   Default( 96 ); >;
	float     g_flFarEpsilon< Attribute( "FarEpsilon" ); Default( 0.05 ); >;
	float     g_flLodNear   < Attribute( "LodNear" );    Default( 9999.0 ); >;
	float     g_flLodFar    < Attribute( "LodFar" );     Default( 10000.0 ); >;
	float     g_flDebugLod  < Attribute( "DebugLod" );   Default( 0.0 ); >; // >0.5 = paint LOD heatmap

	// Plasticine surface maps, sampled triplanar (no UVs on a procedural surface).
	SamplerState g_sRepeat < Filter( ANISOTROPIC ); AddressU( WRAP ); AddressV( WRAP ); >;

	// Material-editable texture inputs (show as slots in the material editor, with defaults).
	CreateInputTexture2D( TextureColor,     Srgb,   8, "",                 "_color",  "Surface,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureNormal,    Linear, 8, "NormalizeNormals", "_normal", "Surface,10/20", Default3( 0.5, 0.5, 1.0 ) );
	CreateInputTexture2D( TextureRoughness, Linear, 8, "",                 "_rough",  "Surface,10/30", Default( 1.0 ) );

	Texture2D g_tAlbedo    < Channel( RGB, Box( TextureColor ),     Srgb );   OutputFormat( BC7 ); SrgbRead( true ); >;
	Texture2D g_tNormalTex < Channel( RGB, Box( TextureNormal ),    Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
	Texture2D g_tRoughTex  < Channel( R,   Box( TextureRoughness ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	// Editable multipliers applied on top of the textures.
	float3 g_vTintColor  < UiType( Color ); Default3( 1.0, 1.0, 1.0 ); UiGroup( "Surface,10/40" ); >;
	float  g_flRoughness < Default( 0.7 ); Range( 0.0, 2.0 ); UiGroup( "Surface,10/50" ); >;
	float  g_flMetalness < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,10/60" ); >;

	// Triplanar projection controls.
	float g_flTriTile  < Default( 8.0 ); Range( 0.5, 64.0 ); UiGroup( "Surface,10/70" ); >;
	float g_flTriBlend < Default( 4.0 ); Range( 1.0, 16.0 ); UiGroup( "Surface,10/80" ); >;
	float g_flNormalStrength < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,10/90" ); >; // 0 = ignore normal map, 1 = full

	// --- Transmission / subsurface (used when D_TRANSMISSION is on). Through-colour = Tint * lerp(1,
	// deepened albedo, FromAlbedo): Tint is a warm/cool bias, FromAlbedo dials in the surface's own
	// diffuse (free — albedo is already computed at the hit), Deepen darkens/saturates that albedo so
	// it reads as denser pigment (light absorbs as it travels through). Thickness controls are in
	// inches. Declared unconditionally (NOT inside #if D_TRANSMISSION): gating cbuffer constants on a
	// dynamic combo shifts every later constant's offset between variants -> Vfx "same constant in
	// different locations" errors. Combos gate CODE, not constant declarations. ---
	float3 g_vTransTint       < UiType( Color ); Default3( 1.0, 1.0, 1.0 );          UiGroup( "Transmission,11/10" ); >;
	float  g_flTransFromAlbedo< Default( 1.0 );  Range( 0.0, 1.0 );  UiGroup( "Transmission,11/12" ); >; // 0 = flat tint, 1 = from diffuse
	float  g_flTransDeepen    < Default( 1.5 );  Range( 1.0, 4.0 );  UiGroup( "Transmission,11/14" ); >; // albedo saturate/darken
	float  g_flTransScale     < Default( 1.0 );  Range( 0.0, 8.0 );  UiGroup( "Transmission,11/20" ); >; // overall strength
	float  g_flTransPower     < Default( 3.0 );  Range( 1.0, 12.0 ); UiGroup( "Transmission,11/30" ); >; // back-scatter tightness
	float  g_flTransDistortion< Default( 0.2 );  Range( 0.0, 1.0 );  UiGroup( "Transmission,11/40" ); >; // normal-bend of the scatter dir
	float  g_flTransAmbient   < Default( 0.1 );  Range( 0.0, 1.0 );  UiGroup( "Transmission,11/50" ); >; // view-independent fill
	float  g_flTransProbe     < Default( 2.0 );  Range( 0.1, 32.0 ); UiGroup( "Transmission,11/60" ); >; // inward-tap depth
	float  g_flTransMaxDist   < Default( 16.0 ); Range( 1.0, 64.0 ); UiGroup( "Transmission,11/70" ); >; // thickness-march reach
	float  g_flTransFalloff   < Default( 8.0 );  Range( 0.5, 64.0 ); UiGroup( "Transmission,11/80" ); >; // absorption distance

	// Displacement look (raymarch silhouette only — the D_DISPLACE toggle stays on SdfRaymarchRenderer,
	// it's a runtime combo). MATERIAL params, tuned in the .vmat like the transmission look. Amp = lump
	// depth (inches); Freq = lump density. Keep Amp*Freq modest (~<0.15) or the field stops being a valid
	// distance for sphere tracing and the surface develops holes; outward lumps ride the proxy's ~2" pad,
	// so keep Amp under ~1.5.
	float g_flDispAmp  < Default( 0.35 ); Range( 0.0, 4.0 );  UiGroup( "Displacement,12/10" ); >;
	float g_flDispFreq < Default( 0.25 ); Range( 0.01, 2.0 ); UiGroup( "Displacement,12/20" ); >;

	// Curvature shading: the field's Laplacian at the hit approximates surface curvature — negative in
	// crevices, positive on ridges. Crevices darken the diffuse (grime in the clay seams), ridges lighten
	// it (worn-edge highlight). Radius = the feature size it responds to (world units); both strengths at
	// 0 branch the extra field taps away entirely. MATERIAL params, tuned in the .vmat.
	float g_flCurveRadius < Default( 1.5 );  Range( 0.25, 8.0 ); UiGroup( "Curvature,13/10" ); >;
	float g_flCurveDark   < Default( 0.35 ); Range( 0.0, 1.0 );  UiGroup( "Curvature,13/20" ); >;
	float g_flCurveLight  < Default( 0.15 ); Range( 0.0, 1.0 );  UiGroup( "Curvature,13/30" ); >;

		// --- Baked distance-field volume (D_FIELD_TEX). One R32F distance per voxel, spanning the prop's
		// local AABB [FieldMin, FieldMax] with samples on the inclusive corners; Dims = texel counts. ---
		Texture3D    g_tField     < Attribute( "FieldTex" ); >;
		float3       g_vFieldMin  < Attribute( "FieldMin" ); >;
		float3       g_vFieldMax  < Attribute( "FieldMax" ); >;
		float3       g_vFieldDims < Attribute( "FieldDims" ); Default3( 2.0, 2.0, 2.0 ); >;
		// Normal central-difference spread as a fraction of a voxel (the faceting<->over-smoothing dial; see
		// SdfNormal). Lower = crisper/more detail (risk faceting), higher = smoother (risk washed-out detail).
		float        g_flFieldNormalScale < Attribute( "FieldNormalScale" ); Default( 0.5 ); >;
		SamplerState g_sField < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); AddressW( CLAMP ); >;
		SamplerState g_sPoint < Filter( POINT ); AddressU( CLAMP ); AddressV( CLAMP ); AddressW( CLAMP ); >; // for the indirection lookup

		// --- Sparse brick atlas (g_nSdfSparse runtime toggle, NOT a combo — a 7th DynamicCombo crashes the Vfx
		// compiler). Replaces the dense field with surface-brick tiles packed in an atlas + an indirection buffer
		// (brick -> tile). Block/TileSize/TilesX/TilesY MUST match SdfFieldGpu. ---
		Texture3D g_tAtlas       < Attribute( "Atlas" ); >;          // the surface-brick tiles (a volume — works)
		Texture2D g_tIndirection < Attribute( "IndirectionTex" ); >; // 2D brick->tile map: a 2nd volume here aliases the
		                                                            // dense field in the PS; 2D uses the brush-data slot pool
		float3 g_vBrickDims < Attribute( "BrickDims" ); >;
		float3 g_vAtlasDims < Attribute( "AtlasDims" ); >;
		float3 g_vSurfaceDims < Attribute( "SurfaceDims" ); >; // high-res brick-grid dims (g_tField may be a lower-res guide)
		int    g_nSdfSparse < Attribute( "SdfSparse" ); Default( 0 ); >;
		#define SDF_BLOCK    8.0
		#define SDF_TILESIZE 9.0
		#define SDF_TILESX   16u
		#define SDF_TILESY   16u

	// --- SDF primitives (mirror of the C# Sdf evaluator) ---

	float sdSphere( float3 p, float r ) { return length( p ) - r; }

	// Ellipsoid with per-axis radii r (IQ approximation). Uniform r = sphere.
	float sdEllipsoid( float3 p, float3 r )
	{
		r = max( r, 1e-3 );
		float k0 = length( p / r );
		float k1 = length( p / (r * r) );
		return k1 > 1e-6 ? k0 * (k0 - 1.0) / k1 : -min( r.x, min( r.y, r.z ) );
	}

	float sdBox( float3 p, float3 b, float r )
	{
		r = max( 0.0, min( r, min( b.x, min( b.y, b.z ) ) ) );
		float3 q = abs( p ) - b + r;
		return length( max( q, 0.0 ) ) + min( max( q.x, max( q.y, q.z ) ), 0.0 ) - r;
	}

	float dot2( float2 v ) { return dot( v, v ); }

	// Capped cylinder along Z. radius = B.x, half-height = B.z.
	float sdCylinder( float3 p, float rad, float h, float r )
	{
		r = max( 0.0, min( r, min( rad, h ) ) );
		float dx = length( p.xy ) - (rad - r);
		float dz = abs( p.z ) - (h - r);
		return min( max( dx, dz ), 0.0 ) + length( max( float2( dx, dz ), 0.0 ) ) - r;
	}

	// IQ capped cone, centred on Z. qx radial, qy axial; bottom radius r1 at qy=-h, top r2 at qy=+h.
	float cappedCone( float qx, float qy, float h, float r1, float r2 )
	{
		float2 k2 = float2( r2 - r1, 2.0 * h );
		float2 ca = float2( qx - min( qx, (qy < 0.0) ? r1 : r2 ), abs( qy ) - h );
		float t = clamp( dot( float2( r2 - qx, h - qy ), k2 ) / dot( k2, k2 ), 0.0, 1.0 );
		float2 cb = float2( qx - r2, qy - h ) + k2 * t;
		float s = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;
		return s * sqrt( min( dot( ca, ca ), dot( cb, cb ) ) );
	}

	#define MAX_SLICE 0.95 // must match SdfBrush.MaxSlice (deepest cut still leaves a sliver)

	// Cone (base radius R at z=-H, apex at +H), optionally SLICED flat by the slice fraction — the slice is
	// part of the primitive (a frustum), so ONE Minkowski opening rounds every edge together: erode all faces
	// (slant, base, cut) by r, exact capped-cone distance of the eroded trapezoid, dilate by r. The cut rim
	// rounds exactly like the base rim and the flat survives any legal r. Unsliced (Rt = 0) this reproduces
	// the classic pointed-cone formula exactly. Matches SdfBrush.ConeDistance.
	float sdCone( float3 p, float R, float H, float rounding, float slice )
	{
		float qx = length( p.xy );

		float s = clamp( slice, 0.0, MAX_SLICE );
		float zcut = H * (1.0 - 2.0 * s); // top cap height (= +H when unsliced)
		float Rt = R * s;                 // top cap radius (0 = pointed)
		float hh = (zcut + H) * 0.5;      // frustum half-height
		float zc0 = (zcut - H) * 0.5;     // frustum centre

		if ( hh < 1e-4 || R < 1e-4 )
			return length( float2( qx, p.z - zc0 ) ) - max( R, max( Rt, 1e-3 ) );

		float L = sqrt( (R - Rt) * (R - Rt) + 4.0 * hh * hh ); // slant length
		float k = (Rt - R) / (2.0 * hh);                       // slant slope dq/dz (< 0)
		// Erosion limit: caps can't cross, eroded base radius can't go negative (= inradius when unsliced).
		float r = clamp( rounding, 0.0, min( hh, 2.0 * hh * R / max( L + R - Rt, 1e-4 ) ) );

		if ( r < 1e-4 )
			return cappedCone( qx, p.z - zc0, hh, R, Rt );

		// Erode: caps move in by r; the slant shifts horizontally by r/cos(tilt) = r·L/(2hh).
		float dq = r * L / (2.0 * hh);
		float zb = -H + r, zt = zcut - r;
		float r1 = R + r * k - dq;  // eroded radius at the bottom cap
		float r2 = Rt - r * k - dq; // eroded radius at the top cap

		if ( r2 < 0.0 )
		{
			zt = (dq - R) / k - H; // slant meets the axis below the top cap — eroded shape is pointed
			r2 = 0.0;              // (the classic zApex = H − r/sinB when unsliced)
		}

		if ( zt <= zb ) // eroded away entirely — the fully-rounded limit is a ball
			return length( float2( qx, p.z - (zb + zt) * 0.5 ) ) - r;

		return cappedCone( qx, p.z - (zb + zt) * 0.5, (zt - zb) * 0.5, r1, r2 ) - r;
	}

	// Exact signed distance to a 2D triangle from its three vertices (IQ). Componentwise min pairs
	// nearest squared distance (.x) with an inside/outside sign from edge winding (.y).
	float sdTriangle2D( float2 p, float2 p0, float2 p1, float2 p2 )
	{
		float2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
		float2 w0 = p - p0, w1 = p - p1, w2 = p - p2;
		float2 pq0 = w0 - e0 * clamp( dot( w0, e0 ) / dot( e0, e0 ), 0.0, 1.0 );
		float2 pq1 = w1 - e1 * clamp( dot( w1, e1 ) / dot( e1, e1 ), 0.0, 1.0 );
		float2 pq2 = w2 - e2 * clamp( dot( w2, e2 ) / dot( e2, e2 ), 0.0, 1.0 );
		float s = sign( e0.x * e2.y - e0.y * e2.x );
		float2 d = min( min( float2( dot( pq0, pq0 ), s * (w0.x * e0.y - w0.y * e0.x) ),
		                     float2( dot( pq1, pq1 ), s * (w1.x * e1.y - w1.y * e1.x) ) ),
		                     float2( dot( pq2, pq2 ), s * (w2.x * e2.y - w2.y * e2.x) ) );
		return -sqrt( d.x ) * sign( d.y );
	}

	float triInradius( float2 a, float2 b, float2 c )
	{
		float per = length( a - b ) + length( b - c ) + length( c - a );
		float area = abs( (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y) ) * 0.5;
		return per > 1e-5 ? 2.0 * area / per : 0.0;
	}

	// Inset vertex vi along its angle bisector so adjacent edges shift in by r (polygon inset).
	float2 insetVertex( float2 vi, float2 n1, float2 n2, float r )
	{
		float2 a = normalize( n1 - vi ), b = normalize( n2 - vi );
		float2 bis = a + b;
		float bl = length( bis );
		if ( bl < 1e-5 ) return vi;
		bis /= bl;
		float sinHalf = sqrt( max( (1.0 - dot( a, b )) * 0.5, 1e-6 ) );
		return vi + bis * (r / sinHalf);
	}

	// Isosceles triangular prism: triangle in XY (apex (0,+hy), base corners (±wx,-hy)) extruded along
	// Z to half-depth hz. Rounding insets the triangle then dilates by r (stays inside the silhouette).
	float sdTriPrism( float3 p, float wx, float hy, float hz, float rounding )
	{
		wx = max( wx, 1e-3 ); hy = max( hy, 1e-3 );
		float2 v0 = float2( 0.0, hy ), v1 = float2( -wx, -hy ), v2 = float2( wx, -hy );
		float r = clamp( rounding, 0.0, min( triInradius( v0, v1, v2 ), hz ) );
		if ( r > 1e-4 )
		{
			float2 i0 = insetVertex( v0, v1, v2, r ), i1 = insetVertex( v1, v0, v2, r ), i2 = insetVertex( v2, v0, v1, r );
			v0 = i0; v1 = i1; v2 = i2;
		}
		float d2 = sdTriangle2D( p.xy, v0, v1, v2 );
		float dz = abs( p.z ) - (hz - r);
		return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
	}

	// IQ exact regular hexagon, a = apothem (centre to flat edge). Vertices on ±X (must match
	// SdfBrush.Hexagon2D / CrossSectionOutline).
	float sdHexagon2D( float2 p, float a )
	{
		const float3 k = float3( -0.86602540, 0.5, 0.57735027 );
		p = abs( p );
		p -= 2.0 * min( dot( k.xy, p ), 0.0 ) * k.xy;
		p -= float2( clamp( p.x, -k.z * a, k.z * a ), a );
		return length( p ) * sign( p.y );
	}

	// IQ exact regular pentagon, a = apothem, one vertex pointing +Y (IQ's original is flat-top/vertex-down,
	// hence the Y negate) — must match SdfBrush.Pentagon2D / CrossSectionOutline.
	float sdPentagon2D( float2 p, float a )
	{
		const float3 k = float3( 0.80901699, 0.58778525, 0.72654253 ); // cos36, sin36, tan36
		p.y = -p.y; // vertex-up convention
		p.x = abs( p.x );
		p -= 2.0 * min( dot( float2( -k.x, k.y ), p ), 0.0 ) * float2( -k.x, k.y );
		p -= 2.0 * min( dot( float2( k.x, k.y ), p ), 0.0 ) * float2( k.x, k.y );
		p -= float2( clamp( p.x, -k.z * a, k.z * a ), a );
		return length( p ) * sign( p.y );
	}

	// IQ exact 5-point star (sdStar, n = 5, m = 3), R = tip circumradius, one tip along +Y (must match
	// SdfBrush.Star2D / CrossSectionOutline).
	float sdStar2D( float2 p, float R )
	{
		const float an = 0.62831853;                        // PI/5
		const float2 acs = float2( 0.80901699, 0.58778525 ); // (cos, sin) PI/5
		const float2 ecs = float2( 0.5, 0.86602540 );        // (cos, sin) PI/3 — the edge direction
		float bn = atan2( p.x, p.y );
		bn -= 2.0 * an * floor( bn / (2.0 * an) );           // positive mod (fmod mirrors negatives)
		bn -= an;
		p = length( p ) * float2( cos( bn ), abs( sin( bn ) ) );
		p -= R * acs;
		p += ecs * clamp( -dot( p, ecs ), 0.0, R * acs.y / ecs.y );
		return length( p ) * sign( p.x );
	}

	#define STAR_EDGE_FACTOR 0.40673664 // sin(60°−36°): star edge-to-centre / circumradius (SdfBrush.StarEdgeFactor)

	// Extruded profile (shape 4): triangle (xs 0), 5-point star (1), regular hexagon (2). Triangle keeps the
	// isosceles wx/hy params; star/hexagon are regular with circumradius wx. Rounding = erode the profile then
	// dilate (Minkowski opening), so it stays inside the sharp silhouette — matches SdfBrush.ExtrudedDistance.
	float sdExtruded( float3 p, float wx, float hy, float hz, float rounding, int xs )
	{
		if ( xs == 1 )
		{
			float R = max( wx, 1e-3 );
			float r = clamp( rounding, 0.0, min( R * STAR_EDGE_FACTOR, hz ) );
			float d2 = sdStar2D( p.xy, R - r / STAR_EDGE_FACTOR ); // uniform scale = exact all-edges inset
			float dz = abs( p.z ) - (hz - r);
			return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
		}
		if ( xs == 2 )
		{
			float a = max( wx, 1e-3 ) * 0.86602540; // apothem from circumradius
			float r = clamp( rounding, 0.0, min( a, hz ) );
			float d2 = sdHexagon2D( p.xy, a - r );
			float dz = abs( p.z ) - (hz - r);
			return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
		}
		if ( xs == 3 )
		{
			float a = max( wx, 1e-3 ) * 0.80901699; // apothem from circumradius
			float r = clamp( rounding, 0.0, min( a, hz ) );
			float d2 = sdPentagon2D( p.xy, a - r );
			float dz = abs( p.z ) - (hz - r);
			return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
		}
		return sdTriPrism( p, wx, hy, hz, rounding );
	}

	// Planar slice for the sphere (the cone folds its slice into sdCone): intersect with the half-space below
	// z = halfH·(1 − 2·slice), the rim rounded by the Round slider (erode shape + plane by r, rounded-corner
	// join, dilate by r — the same combine the extrusions use). Matches SdfBrush.SliceRound. Callers pre-clamp
	// r so the fillet fits the cut disc (see SdfShapeDist).
	float sliceRound( float d, float z, float halfH, float slice, float rounding )
	{
		if ( slice <= 0.0 ) return d;
		float zcut = halfH * (1.0 - 2.0 * min( slice, MAX_SLICE ));
		float dz = z - zcut;
		float r = clamp( rounding, 0.0, (zcut + halfH) * 0.5 ); // ≤ half the remaining thickness
		if ( r <= 1e-3 ) return max( d, dz ); // hard slice
		float2 w = float2( d, dz ) + r;
		return min( max( w.x, w.y ), 0.0 ) + length( max( w, 0.0 ) ) - r;
	}

	// Extruded TEXT: the 2D profile is the brush's baked distance field (one atlas slot, texel units), sampled
	// at the local XY and pushed through the same rounded extrusion as the analytic profiles. Outside the text
	// quad the clamped sample is repaired with the quad distance (both are lower bounds on the true glyph
	// distance; max keeps marching safe). Round insets the glyphs — a real distance field, so letter edges and
	// corners round like clay. Matches SdfBrush.TextDistance.
	#define TEXT_SLOT_W 256.0 // must match SdfTextData.Width/Height and SdfTextSdf.MaxSlots
	#define TEXT_SLOT_H 128.0
	#define TEXT_SLOTS  8.0

	float sdText( float3 p, float3 he, int slot, float rounding )
	{
		float2 h2 = max( he.xy, 1e-3 );
		float2 uv = saturate( p.xy / (2.0 * h2) + 0.5 );
		uv.y = 1.0 - uv.y; // bitmap rows run top-down

		// Half-texel-inset remap into this brush's atlas slot, so bilinear never bleeds into a neighbour slot.
		float2 st = float2( (uv.x * (TEXT_SLOT_W - 1.0) + 0.5) / TEXT_SLOT_W,
		                    ((uv.y * (TEXT_SLOT_H - 1.0) + 0.5) / TEXT_SLOT_H + slot) / TEXT_SLOTS );
		float ts = min( 2.0 * h2.x / TEXT_SLOT_W, 2.0 * h2.y / TEXT_SLOT_H ); // world per texel (conservative)
		float d2 = g_tTextSdf.SampleLevel( g_sTextSdf, st, 0 ).r * ts;

		// Outside the quad there's no field data (the sample is edge-clamped). Nearest glyph is inward of the
		// clamp point while p is outward of it, so those legs can't oppose: d_true² ≥ dq² + sample². This
		// Pythagorean bound is tight and SMOOTH across the rim — a cruder max() bound kinks the field along the
		// quad rectangle, which reads as a crease wherever the rim blends with nearby clay.
		float2 q = abs( p.xy ) - h2;
		float dq = length( max( q, 0.0 ) );
		if ( dq > 0.0 )
		{
			float d2o = max( d2, 0.0 );
			d2 = sqrt( dq * dq + d2o * d2o );
		}

		float hz = max( he.z, 1e-3 );
		float r = clamp( rounding, 0.0, hz );
		float d2e = d2 + r; // erode the glyphs; the -r below dilates — rounds every letter edge
		float dz = abs( p.z ) - (hz - r);
		return min( max( d2e, dz ), 0.0 ) + length( max( float2( d2e, dz ), 0.0 ) ) - r;
	}

	float SdfShapeDist( float shapeId, float3 pl, float4 B, float rr, int xs, float slice )
	{
		int shape = (int)(shapeId + 0.5);
		if ( shape == 1 ) return sdBox( pl, B.xyz, rr );
		if ( shape == 2 ) return sdCylinder( pl, B.x, B.z, rr );
		if ( shape == 3 )
		{
			pl.z -= B.z; // base-pivot cone: Position sits on the base; shift into the centred cone frame
			return sdCone( pl, B.x, B.z, rr, slice ); // slice is part of the primitive (rounded frustum)
		}
		if ( shape == 4 ) return sdExtruded( pl, B.x, B.y, B.z, rr, xs );
		// Shape 5 = Spline is handled in OneBrushDist (it reads the control-point pool, not this local-space
		// path), so it never reaches here — return empty as a safety fallback rather than a wrong ellipsoid.
		if ( shape == 5 ) return 1e9;
		if ( shape == 6 ) return sdText( pl, B.xyz, xs, rr ); // xs lane doubles as the text atlas slot
		// Sphere/ellipsoid: rim fillet capped by the CAP DEPTH (material under the cut plane, 2·rz·slice) and
		// the shape's radii — past the depth the fillet consumes the flat and the top sinks below the cut. At
		// the limit the slice rounds into a smooth dome kissing the plane (SdfBrush.SphereSliceMaxRounding).
		float ss = min( max( slice, 0.0 ), MAX_SLICE );
		float rs = min( rr, min( min( B.x, min( B.y, B.z ) ), 2.0 * max( B.z, 1e-3 ) * ss ) );
		return sliceRound( sdEllipsoid( pl, B.xyz ), pl.z, max( B.z, 1e-3 ), slice, rs );
	}

	float smin( float a, float b, float k )
	{
		if ( k <= 0.0 ) return min( a, b );
		float h = saturate( 0.5 + 0.5 * (b - a) / k );
		return lerp( b, a, h ) - k * h * (1.0 - h);
	}

	float ssub( float d, float s, float k )
	{
		if ( k <= 0.0 ) return max( d, -s );
		float h = saturate( 0.5 - 0.5 * (d + s) / k );
		return lerp( d, -s, h ) + k * h * (1.0 - h);
	}

	// Rotate vector v by quaternion q.
	float3 qrot( float4 q, float3 v )
	{
		return v + 2.0 * cross( q.xyz, cross( q.xyz, v ) + q.w * v );
	}

	// World <-> object(model) space, for re-basing the triplanar projection onto the prop. Rigid
	// (rotation + translation, unit scale assumed), so the inverse rotation is the quat conjugate.
	float3 WorldToModelPos( float3 wp )  { return qrot( float4( -g_vModelRotation.xyz, g_vModelRotation.w ), wp - g_vModelOrigin ); }
	float3 WorldToModelDir( float3 wd )  { return qrot( float4( -g_vModelRotation.xyz, g_vModelRotation.w ), wd ); }
	float3 ModelToWorldPos( float3 mp )  { return qrot( g_vModelRotation, mp ) + g_vModelOrigin; }
		float3 ModelToWorldDir( float3 md )  { return qrot( g_vModelRotation, md ); }

	#define TEXELS_PER_BRUSH 7 // must match SdfRaymarchRenderer.TexelsPerBrush (slots 5/6 = world AABB min/max)
	float4 LoadBrush( int k, int slot ) { return g_tBrushData.Load( int3( k * TEXELS_PER_BRUSH + slot, 0, 0 ) ); }

	// Signed distance to an axis-aligned box [mn,mx]: 0 inside, positive outside. The per-brush cull bound.
	float sdAabb( float3 p, float3 mn, float3 mx )
	{
		float3 c = (mn + mx) * 0.5, e = (mx - mn) * 0.5;
		float3 q = abs( p - c ) - e;
		return length( max( q, 0.0 ) ) + min( max( q.x, max( q.y, q.z ) ), 0.0 );
	}

		float4 LoadSplinePoint( int i ) { return g_tSplineData.Load( int3( i, 0, 0 ) ); } // xyz world pos, w radius

		// IQ exact signed distance to a rounded cone (tapered capsule) from sphere a (a.xyz, a.w) to b.
		float sdRoundCone( float3 p, float4 a, float4 b )
		{
			float3 ba = b.xyz - a.xyz;
			float l2 = dot( ba, ba );
			if ( l2 < 1e-6 ) return length( p - a.xyz ) - max( a.w, b.w ); // coincident — larger sphere

			float r1 = a.w, r2 = b.w;
			float rr = r1 - r2;
			float a2 = l2 - rr * rr;
			float il2 = 1.0 / l2;

			float3 pa = p - a.xyz;
			float y = dot( pa, ba );
			float z = y - l2;
			float3 xv = pa * l2 - ba * y;
			float x2 = dot( xv, xv );
			float y2 = y * y * l2;
			float z2 = z * z * l2;

			float k = sign( rr ) * rr * rr * x2;
			if ( sign( z ) * a2 * z2 > k ) return sqrt( x2 + z2 ) * il2 - r2;
			if ( sign( y ) * a2 * y2 < k ) return sqrt( x2 + y2 ) * il2 - r1;
			return ( sqrt( x2 * a2 * il2 ) + y * rr ) * il2 - r1;
		}

		#define SPLINE_TESS 8 // sub-segments per span when curved (must match SdfBrush.SplineTessellation)

		// Hermite/Catmull-Rom point (xyz pos, w radius) at t along span p1→p2; tangents from p0/p3 scaled by
		// curvature (0 = straight, 1 = full Catmull-Rom). Matches SdfBrush.CatmullRomPoint.
		float4 CatmullRomPoint( float4 p0, float4 p1, float4 p2, float4 p3, float t, float curv )
		{
			float t2 = t * t, t3 = t2 * t;
			float h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
			float h10 = t3 - 2.0 * t2 + t;
			float h01 = -2.0 * t3 + 3.0 * t2;
			float h11 = t3 - t2;

			float4 m1 = (p2 - p0) * (0.5 * curv);
			float4 m2 = (p3 - p1) * (0.5 * curv);
			float4 r = h00 * p1 + h10 * m1 + h01 * p2 + h11 * m2;
			r.w = max( r.w, 0.1 ); // curve can overshoot — keep radius positive
			return r;
		}

		// Variable-radius tube through count control points starting at `off` in the shared pool. Straight
		// (curv 0) → one rounded cone per span; curved → SPLINE_TESS Catmull-Rom sub-segments per span. p and
		// the points are in WORLD space (baked there). Matches SdfBrush.SplineDistance.
		float SplineDist( float3 p, int off, int count, float curv, bool closed )
		{
			if ( count < 1 ) return 1e9;
			if ( count == 1 ) { float4 a = LoadSplinePoint( off ); return length( p - a.xyz ) - a.w; }

			bool loop = closed && count >= 3; // a closed ring needs >= 3 points; else treat as open
			int segs = loop ? count : count - 1;
			int k = curv <= 1e-4 ? 1 : SPLINE_TESS;
			float d = 1e9;
			[loop] for ( int i = 0; i < segs; i++ )
			{
				// Catmull-Rom control indices: closed wraps modularly (last→first); open clamps the phantoms.
				int j0 = loop ? (i - 1 + count) % count : (i > 0 ? i - 1 : 0);
				int j2 = loop ? (i + 1) % count : i + 1;
				int j3 = loop ? (i + 2) % count : (i < count - 2 ? i + 2 : count - 1);
				float4 p0 = LoadSplinePoint( off + j0 );
				float4 p1 = LoadSplinePoint( off + i );
				float4 p2 = LoadSplinePoint( off + j2 );
				float4 p3 = LoadSplinePoint( off + j3 );

			if ( g_nSdfCull != 0 )
			{
				// Per-span cull: a bounding sphere around this span's tube gives a lower bound on the distance
				// to it; if that's already >= the best so far, this span's cone(s) can't lower the min — skip
				// them. Generous margins (chord half-length + max radius + a curve bulge) keep the curved tube
				// inside the sphere, so the cull never removes a span that could actually be nearest.
				float3 sc = (p1.xyz + p2.xyz) * 0.5;
				float sr = 0.5 * length( p2.xyz - p1.xyz ) + max( p1.w, p2.w );
				if ( curv > 1e-4 )
				{
					sr += 0.5 * curv * max( length( p2.xyz - p0.xyz ), length( p3.xyz - p1.xyz ) ); // position bulge
					sr += 0.5 * curv * max( abs( p2.w - p0.w ), abs( p3.w - p1.w ) );                 // radius overshoot
				}
				if ( length( p - sc ) - sr >= d )
					continue;
			}

				float4 prev = p1;
				[loop] for ( int s = 1; s <= k; s++ )
				{
					float4 cur = CatmullRomPoint( p0, p1, p2, p3, s / (float)k, curv );
					d = min( d, sdRoundCone( p, prev, cur ) );
					prev = cur;
				}
			}
			return d;
		}

		// Bare distance to one brush at world point wp (no symmetry). Spline (shape 5) is evaluated directly
		// in world from its control-point pool (offset=B.x, count=B.y); every other shape is transformed into
		// the brush's local frame first. xs = extruded cross-section id (slot 5 .w); slice = planar top-slice
		// fraction (slot 6 .w).
		float OneBrushDist( float3 wp, float4 A, float4 B, float4 C, float rr, int xs, float slice )
		{
			if ( (int)(A.w + 0.5) == 5 )
				return SplineDist( wp, (int)(B.x + 0.5), (int)(B.y + 0.5), B.z, rr > 0.5 ); // B.z = curvature, rr (E.x) = closed flag
			return SdfShapeDist( A.w, qrot( float4( -C.xyz, C.w ), wp - A.xyz ), B, rr, xs, slice );
		}

		// Sample the baked field at world point p: fold into the prop's local frame (where the field was
		// baked), map to a UVW, and do one trilinear fetch. Samples sit on the inclusive corners, so the
		// 0..1 position is stretched across (Dims-1) cells and offset by the half-texel before sampling.
		float SampleFieldTex( float3 p )
		{
			float3 lp = WorldToModelPos( p );
			float3 t  = (lp - g_vFieldMin) / max( g_vFieldMax - g_vFieldMin, 1e-4 );
			float3 uvw = (t * (g_vFieldDims - 1.0) + 0.5) / g_vFieldDims; // sampler CLAMP guards t outside [0,1]
			return g_tField.SampleLevel( g_sField, uvw, 0 ).r;
		}

		// Sparse path (two-phase, GigaVoxels-style). g_tField here is a LOW-RES guide field (empty-space distance),
		// while the surface detail lives in the high-res atlas. Most march steps are far from the surface: sample the
		// cheap guide and take a big step with NO brick lookup. Only once within ~2 bricks of the surface do we look
		// up the point's brick — occupied → its high-res atlas tile (the real surface); empty → keep stepping on the
		// guide. The brick grid is sized by g_vSurfaceDims (high), independent of the guide texture's resolution.
		float SampleFieldSparse( float3 p )
		{
			float guide = SampleFieldTex( p );  // low-res guide (g_tField); one trilinear fetch, like the dense path
			float3 scell = (g_vFieldMax - g_vFieldMin) / max( g_vSurfaceDims - 1.0, 1.0 );
			float  nearBand = 2.0 * SDF_BLOCK * max( scell.x, max( scell.y, scell.z ) ); // ~2 bricks, world units
			if ( guide > nearBand )
				return guide;                   // far from the surface: cheap guide step, no brick lookup

			// Near the surface: locate the brick on the HIGH-RES grid and consult the atlas.
			float3 lp    = WorldToModelPos( p );
			float3 fv    = (lp - g_vFieldMin) / scell;     // continuous surface-voxel coordinate
			int3   bd    = (int3)( g_vBrickDims + 0.5 );
			int3   brick = (int3)floor( fv / SDF_BLOCK );
			if ( any( brick < 0 ) || any( brick >= bd ) )
				return guide;

			float ind = g_tIndirection.Load( int3( brick.x, brick.y + bd.y * brick.z, 0 ) ).r;
			if ( ind < 0.0 )
				return guide;                   // empty brick near the surface: keep stepping on the guide

			// Occupied brick: sample its atlas tile. Decode the tile's cell in the atlas tile-grid (matches the fill
			// compute exactly), offset by the voxel-within-brick, and trilinearly sample. Tiles are Block+1 wide so the
			// shared far corner makes interpolation correct up to the brick boundary.
			float3 vit = fv - (float3)brick * SDF_BLOCK;             // [0, Block]
			uint   tile = (uint)( ind + 0.5 );
			int3   tc  = int3( tile % SDF_TILESX, ( tile / SDF_TILESX ) % SDF_TILESY, tile / ( SDF_TILESX * SDF_TILESY ) );
			float3 av  = (float3)tc * SDF_TILESIZE + vit;
			float3 uvw = ( av + 0.5 ) / g_vAtlasDims;
			return g_tAtlas.SampleLevel( g_sField, uvw, 0 ).r;
		}

		// Distance to one brush's primitive at world point p, including its mirror-symmetry copies.
		// A/B/C are the packed pos+shape / size+blend / quat slots; `rr` is rounding; `mask` is the
		// mirror bitmask (1=X, 2=Y, 4=Z). Mirroring happens across the SCULPTURE origin, so the world
		// point is folded into model space, reflected, and folded back — matching SdfBrush.Distance.
		float BrushDist( float3 p, float4 A, float4 B, float4 C, float rr, int mask, int xs, float slice )
		{
			float bd = OneBrushDist( p, A, B, C, rr, xs, slice );
			if ( mask == 0 )
				return bd;

			// Union the primitive with its reflection across every enabled sculpture-origin plane.
			// Enumerate each sign combination of the enabled axes (1, 3, or 7 extra copies); the seam
			// fuses with the brush's own blend (B.w), exactly like the CPU evaluator.
			float3 L = WorldToModelPos( p );
			int nx = (mask & 1) ? 1 : 0;
			int ny = (mask & 2) ? 1 : 0;
			int nz = (mask & 4) ? 1 : 0;
			[loop] for ( int sx = 0; sx <= nx; sx++ )
			[loop] for ( int sy = 0; sy <= ny; sy++ )
			[loop] for ( int sz = 0; sz <= nz; sz++ )
			{
				if ( sx + sy + sz == 0 )
					continue; // the un-mirrored primitive, already in bd

				float3 Lm = L * float3( sx ? -1.0 : 1.0, sy ? -1.0 : 1.0, sz ? -1.0 : 1.0 );
				bd = smin( bd, OneBrushDist( ModelToWorldPos( Lm ), A, B, C, rr, xs, slice ), B.w );
			}
			return bd;
		}

	// --- Plasticine displacement noise (cheap hash-based 3D value noise, no texture fetch) ---

	float hash13( float3 p3 )
	{
		p3 = frac( p3 * 0.1031 );
		p3 += dot( p3, p3.zyx + 31.32 );
		return frac( (p3.x + p3.y) * p3.z );
	}

	// Trilinearly-interpolated value noise, smoothstep-faded per cell. Output ~[0,1].
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

	// Signed displacement (centred on 0): big lump + a half-scale octave for an uneven, hand-pressed
	// feel. Deliberately LOW frequency — fine grain is left to the triplanar normal map, and fine
	// displacement would just alias against the SdfNormal finite-difference epsilon (0.05) anyway.
	// Sampled in MODEL space (like the triplanar surface), so the lumps are locked to the prop and
	// move/rotate with it instead of the prop sliding through a fixed world-space noise field. The
	// transform is rigid, so the field's gradient magnitude (the L safety factor below) is unchanged.
	float Displacement( float3 p )
	{
		float3 x = WorldToModelPos( p ) * g_flDispFreq;
		float n = vnoise( x ) * 0.67 + vnoise( x * 2.03 + 11.7 ) * 0.33; // ~[0,1]
		return (n * 2.0 - 1.0) * g_flDispAmp;
	}

	float SdfDist( float3 p )
	{
	#if ( D_FIELD_TEX )
		// Cached path: one trilinear fetch of the baked field instead of the per-brush loop. Sparse-atlas vs dense
		// volume is a RUNTIME branch (g_nSdfSparse), not a combo, to avoid a 7th DynamicCombo.
		float d = g_nSdfSparse != 0 ? SampleFieldSparse( p ) : SampleFieldTex( p );
	#else
		float d = 1e9;
		[loop]
		for ( int k = 0; k < g_nBrushCount; k++ )
		{
			float4 A = LoadBrush( k, 0 ); // pos.xyz, shape (0 sphere / 1 box)
			float4 B = LoadBrush( k, 1 ); // size.xyz, blend
			float4 C = LoadBrush( k, 2 ); // rotation quat xyzw
			float4 D = LoadBrush( k, 3 ); // color.rgb, op (0 add / 1 subtract)
			float4 E = LoadBrush( k, 4 ); // rounding .x, metalness .y, roughness .z, mirror mask .w
			float4 F = LoadBrush( k, 5 ); // cull AABB min .xyz, extruded cross-section id .w
			float4 G = LoadBrush( k, 6 ); // cull AABB max .xyz, slice fraction .w

			// AABB early-out (runtime-toggled by g_nSdfCull, not a combo).
			// Skip this brush when it can't change the running distance d. sdAabb is a safe lower bound on the
			// brush distance (the packed box covers the primitive, mirror copies and blend padding).
			// Add: a brush only matters within blend of becoming the new nearest (bd >= d+k). Subtract: a
			// carver only matters while material is within blend. One threshold covers both: k+d for add,
			// k-d for subtract (a single k+d over-culls subtracts where d is negative, i.e. deep inside).
			if ( g_nSdfCull != 0 && sdAabb( p, F.xyz, G.xyz ) > (D.w < 0.5 ? d + B.w : B.w - d) )
				continue;

			float bd = BrushDist( p, A, B, C, E.x, (int)(E.w + 0.5), (int)(F.w + 0.5), G.w );

			d = (D.w < 0.5) ? smin( d, bd, B.w ) : ssub( d, bd, B.w );
		}
	#endif
	#if ( D_DISPLACE )
		// Subtracting noise along the field pushes the whole iso-surface in/out -> bumpy silhouette.
		// SdfNormal differences this same field, so shading + the depth-prepass normal track the lumps.
		d -= Displacement( p );
	#endif
		return d;
	}

	struct SdfSurface { float3 col; float metal; float rough; };

	// Re-runs the union, blending colour AND per-brush metalness/roughness by the same smooth-min
	// factor as the geometry, so merged brushes share smooth material seams instead of a hard
	// nearest-brush boundary (a sharp specular edge reads far worse than a colour edge).
	SdfSurface SdfShade( float3 p )
	{
		float d = 1e9;
		SdfSurface s;
		s.col = float3( 1, 1, 1 ); s.metal = 0.0; s.rough = 1.0; // empty-space material; first brush overrides
		[loop]
		for ( int k = 0; k < g_nBrushCount; k++ )
		{
			float4 A = LoadBrush( k, 0 );
			float4 B = LoadBrush( k, 1 );
			float4 C = LoadBrush( k, 2 );
			float4 D = LoadBrush( k, 3 );
			float4 E = LoadBrush( k, 4 ); // rounding .x, metalness .y, roughness .z, mirror mask .w
			float4 F = LoadBrush( k, 5 ); // cull AABB min .xyz, extruded cross-section id .w
			float4 G = LoadBrush( k, 6 ); // cull AABB max .xyz, slice fraction .w

			// AABB early-out (runtime-toggled by g_nSdfCull, not a combo).
			// Same early-out as SdfDist: a far brush can't change the blended distance/colour here either.
			// Add: a brush only matters within blend of becoming the new nearest (bd >= d+k). Subtract: a
			// carver only matters while material is within blend. One threshold covers both: k+d for add,
			// k-d for subtract (a single k+d over-culls subtracts where d is negative, i.e. deep inside).
			if ( g_nSdfCull != 0 && sdAabb( p, F.xyz, G.xyz ) > (D.w < 0.5 ? d + B.w : B.w - d) )
				continue;

			float bd = BrushDist( p, A, B, C, E.x, (int)(E.w + 0.5), (int)(F.w + 0.5), G.w );

			float kk = B.w;

			if ( D.w >= 0.5 )
			{
				// Subtraction tints the cut walls with the brush material (blended by the
				// same smooth-subtract factor as the geometry).
				if ( kk <= 0.0 )
				{
					if ( -bd > d ) { s.col = D.rgb; s.metal = E.y; s.rough = E.z; }
					d = max( d, -bd );
				}
				else
				{
					float h = saturate( 0.5 - 0.5 * (d + bd) / kk );
					s.col = lerp( s.col, D.rgb, h );    // h->1 takes the cut material
					s.metal = lerp( s.metal, E.y, h );
					s.rough = lerp( s.rough, E.z, h );
					d = lerp( d, -bd, h ) + kk * h * (1.0 - h);
				}
				continue;
			}

			if ( kk <= 0.0 )
			{
				if ( bd < d ) { d = bd; s.col = D.rgb; s.metal = E.y; s.rough = E.z; }
			}
			else
			{
				float h = saturate( 0.5 + 0.5 * (bd - d) / kk );
				s.col = lerp( D.rgb, s.col, h );        // h->1 keeps accumulated, h->0 takes new
				s.metal = lerp( E.y, s.metal, h );
				s.rough = lerp( E.z, s.rough, h );
				d = lerp( bd, d, h ) - kk * h * (1.0 - h);
			}
		}
		return s;
	}

	float3 SdfNormal( float3 p )
	{
		float e = 0.05;
	#if ( D_FIELD_TEX )
		// The baked field is trilinear, so its gradient is piecewise-constant per voxel — a tiny epsilon
		// reads back faceted normals at cell scale. Widen the central difference to ~half a voxel so it
		// straddles cell boundaries and averages the two cells' gradients into a smooth normal. Cell size is
		// local-space, but the transform is rigid (unit scale) so it's a valid world-space step too. Keep the
		// 0.05 floor for tiny/high-res fields where half a voxel would itself be small enough to facet again.
		// Use the SURFACE (atlas) resolution, not g_vFieldDims: in the sparse path g_vFieldDims is the low-res guide,
		// but normals are read from the high-res atlas — sizing e off the guide over-smooths them vs the dense path.
		float3 cell = (g_vFieldMax - g_vFieldMin) / max( g_vSurfaceDims - 1.0, 1.0 );
		e = max( g_flFieldNormalScale * max( cell.x, max( cell.y, cell.z ) ), 0.05 );
	#endif
		float3 g = float3(
			SdfDist( p + float3( e, 0, 0 ) ) - SdfDist( p - float3( e, 0, 0 ) ),
			SdfDist( p + float3( 0, e, 0 ) ) - SdfDist( p - float3( 0, e, 0 ) ),
			SdfDist( p + float3( 0, 0, e ) ) - SdfDist( p - float3( 0, 0, e ) ) );
		// Guard the gradient: at grazing/silhouette hits the central difference can collapse to ~0, and
		// normalize(0) is NaN. A NaN here lands in the SHARED depth+normals prepass and poisons every
		// screen-space pass that samples it (noise over all SDFs at once). Fall back to facing the camera.
		float l = length( g );
		return l > 1e-6 ? g / l : float3( 0, 0, 1 );
	}

	// Mean-curvature estimate at the hit: the field's Laplacian via four tetrahedral taps at radius e,
	// against the centre sample. Positive on ridges/convex edges, negative in crevices; dividing by e makes
	// it a unitless "how curved at this feature scale" that saturates nicely into the shading factors.
	// 5 field samples — one trilinear fetch each on the baked-field path.
	float SdfCurvature( float3 p, float e )
	{
	#if ( D_FIELD_TEX )
		// The baked field is trilinear -> C0 but NOT C1: its gradient jumps at every voxel boundary, so a
		// second-difference (this Laplacian) at a narrow radius reads back the CELL GRID, not real curvature.
		// SdfNormal widens its stencil to ~half a voxel for the milder (1st-derivative) faceting; curvature is
		// a 2nd derivative and needs more support, so floor the tap radius to ~2 voxels. This straddles several
		// cells (decorrelating the per-cell error) and, since the grid term scales ~1/e, attenuates what's left.
		// Above this floor g_flCurveRadius still sets the feature scale (bigger = cleaner but softer curvature).
		float3 cell = (g_vFieldMax - g_vFieldMin) / max( g_vSurfaceDims - 1.0, 1.0 );
		e = max( e, 2.0 * max( cell.x, max( cell.y, cell.z ) ) );
	#endif
		float2 k = float2( 1.0, -1.0 );
		float t1 = SdfDist( p + k.xyy * e );
		float t2 = SdfDist( p + k.yyx * e );
		float t3 = SdfDist( p + k.yxy * e );
		float t4 = SdfDist( p + k.xxx * e );
		return (t1 + t2 + t3 + t4 - 4.0 * SdfDist( p )) / e;
	}

	// --- Transmission / subsurface (D_TRANSMISSION). The whole reason this is cheap: meshes need a
	// baked thickness map because they don't know their own interior — we have the actual field, so we
	// read thickness straight out of it. Forward pass only (the depth prepass returns before shading,
	// and these sample shadow maps which the prepass shouldn't). ---
#if ( D_TRANSMISSION && S_MODE_DEPTH == 0 )
	// Inward tap: how far the solid extends straight below the surface (along -N). One field sample,
	// direction-agnostic, nearly free. Clamped to the probe depth — a single tap can't see past it.
	float SdfThicknessTap( float3 p, float3 N )
	{
		return clamp( -SdfDist( p - N * g_flTransProbe ), 0.0, g_flTransProbe );
	}

	// Short march toward the light: sum the length spent inside the solid between the surface and the
	// light side -> the real path light travels through the body. Directional, so it captures back-lit
	// thinness the normal tap misses. TRANS_MARCH_STEPS fetches: cheap on D_FIELD_TEX, a brush-loop
	// per step on the analytic path (hence: prefer the cached field when using this).
	#define TRANS_MARCH_STEPS 6
	float SdfThicknessMarch( float3 p, float3 L )
	{
		float stepLen = g_flTransMaxDist / TRANS_MARCH_STEPS;
		float t = stepLen * 0.5; // start half a step in so the surface sample (d~=0) doesn't count
		float inside = 0.0;
		[loop]
		for ( int s = 0; s < TRANS_MARCH_STEPS; s++ )
		{
			if ( SdfDist( p + L * t ) < 0.0 )
				inside += stepLen;
			t += stepLen;
		}
		return inside;
	}

	// One light's back-scatter contribution. The Dice/Frostbite wrap term
	// ( https://colinbarrebrisebois.com ) gives the soft "light bleeding through" look; thickness
	// (inches) -> exponential absorption. Added as emission so it sits on top of the standard direct
	// lighting that ShadingModelStandard::Shade still computes for the front face.
	//
	// The directional thickness march is GATED: it's the only real per-light cost, and it only runs
	// when the light both reaches this pixel (lightMask) AND is behind the surface relative to the
	// camera (backlit > 0). Most lights at most pixels fail that test and bail for the price of a dot
	// product. `ambient` is the always-on view-independent fill — pass 0 for fill lights so it doesn't
	// accumulate per light and wash the prop out.
	void ApplyTransLight( inout Material m, float3 transColor, float3 p, float3 L, float3 lightColor, float lightMask, float tapThick, float ambient, float3 viewDir )
	{
		if ( lightMask <= 0.001 )
			return; // attenuated to nothing / fully shadowed

		float3 scatterDir = normalize( L + m.Normal * g_flTransDistortion );
		float backlit = pow( saturate( dot( viewDir, -scatterDir ) ), g_flTransPower );

		// Only pay for the march when there's a back-scatter lobe to shape. Off the lobe we still emit
		// the cheap ambient fill from the inward tap, so flat/edge-on areas stay consistent.
		float thick = tapThick;
		if ( backlit > 0.002 )
			thick = max( tapThick, SdfThicknessMarch( p, L ) );

		float atten = exp( -thick / max( g_flTransFalloff, 1e-3 ) ); // thin -> ~1, thick -> ~0
		m.Emission += transColor * lightColor * ( backlit * g_flTransScale + ambient ) * atten * lightMask;
	}

	// p is the raw world hit (the frame SdfDist marches in); m.WorldPosition / m.ScreenPosition are
	// what the engine's shadow + light queries expect (camera-relative + the high-precision offset).
	void ApplyTransmission( inout Material m, float3 p, float3 viewDir )
	{
		// Through-colour from our own diffuse (free — albedo is already shaded at this point): deepen
		// the albedo so it reads as denser pigment, blend it against the flat tint, bias by the tint.
		float3 deepened = pow( max( m.Albedo, 1e-4 ), g_flTransDeepen );
		float3 transColor = g_vTransTint * lerp( float3( 1, 1, 1 ), deepened, g_flTransFromAlbedo );

		float tapThick = SdfThicknessTap( p, m.Normal );

		// Key light (sun). Owns the ambient fill so it's added once, not once per light.
		if ( g_DirectionalLightEnabled )
		{
			float3 L = -normalize( g_DirectionalLightDirection.xyz );
			// Cascade-only sun visibility (NOT GetVisibility): the engine's GetVisibility now also
			// composites the screen-space contact-shadow mask (bindless + MSAA gather), and inlining
			// that here CRASHES DXC on the heavy D_TRANSMISSION combo variants — no diagnostic, the
			// compiler just dies at ~90% of the sweep. The cascade PCF sample is the part that matters
			// for dimming transmission in shadow; a contact mask on a soft back-scatter term is noise.
			float vis = 1.0;
			if ( g_DirectionalLightCascadeCount > 0 )
			{
				float3 posLs;
				int cascade = FindCascade( m.WorldPosition, posLs );
				if ( cascade >= 0 )
					vis = DirectionalLightShadow::SampleCascade( cascade, m.WorldPosition, m.ScreenPosition.xy );
			}
			// Let some sun scatter through even when the front face is shadowed (back-lit leaves/skin
			// still glow), like foliage.shader does for its sun term.
			ApplyTransLight( m, transColor, p, L, g_DirectionalLightColor.rgb, lerp( 0.2, 1.0, vis ), tapThick, g_flTransAmbient, viewDir );
		}

		// Cluster (point/spot) lights affecting this screen tile — each gets its own gated directional
		// march via ApplyTransLight. The count is bounded by the cluster (a handful of lights per tile,
		// not the whole scene), and the march only fires for the ones genuinely back-lighting this
		// pixel, so off-lobe lights cost ~nothing. No ambient (it would accumulate per light); the sun
		// is handled above, so skip any directional light here to avoid double-counting it.
		uint lightCount = Light::Count( m.ScreenPosition );
		[loop]
		for ( uint li = 0; li < lightCount; li++ )
		{
			Light light = Light::From( m.WorldPosition, m.ScreenPosition, li );
			if ( light.LightData.Type == LightType::LightTypeDirectional )
				continue;
			ApplyTransLight( m, transColor, p, light.Direction, light.Color, light.Attenuation * light.Visibility, tapThick, 0.0, viewDir );
		}
	}
#endif

	// Triplanar normal mapping (Ben Golus "whiteout" blend). Perturbs the surface normal by a
	// tangent-space normal map without needing real tangents/UVs. Space-agnostic: feed position and
	// normal in the SAME frame and the result comes back in that frame. We feed MODEL space here, so
	// the perturbed normal is model-space and the caller rotates it to world for lighting.
	float3 TriplanarNormal( Texture2D tex, SamplerState s, float3 wp, float3 wn, float tile, float blend, float strength )
	{
		wp /= 39.3701; // inches -> metres, matching Tex2DTriplanar

		float3 bw = pow( abs( normalize( wn ) ), blend );
		bw /= dot( bw, 1.0 );

		float3 tnX = tex.Sample( s, wp.zy * tile ).xyz * 2.0 - 1.0;
		float3 tnY = tex.Sample( s, wp.xz * tile ).xyz * 2.0 - 1.0;
		float3 tnZ = tex.Sample( s, wp.xy * tile ).xyz * 2.0 - 1.0;

		// Normal-map influence. The whiteout fold below discards the sampled z and rebuilds it from
		// the surface normal, so scaling just xy is an exact slope lerp: 0 = pure surface normal.
		tnX.xy *= strength;
		tnY.xy *= strength;
		tnZ.xy *= strength;

		// Whiteout blend: fold the surface normal into each projection, then recombine.
		tnX = float3( tnX.xy + wn.zy, wn.x );
		tnY = float3( tnY.xy + wn.xz, wn.y );
		tnZ = float3( tnZ.xy + wn.xy, wn.z );

		return normalize( tnX.zyx * bw.x + tnY.xzy * bw.y + tnZ.xyz * bw.z );
	}

	// Orthographic view? The projection's w-generating row is (0,0,0,1) for ortho (w is constant) and
	// has a non-zero xyz for any perspective projection. The per-view constants are rebound per pass,
	// so inside a directional-light shadow cascade this flips to the LIGHT's (ortho) camera — which is
	// exactly how the march knows to trace parallel rays there. Also correct for an ortho editor view.
	bool IsOrthoView()
	{
		return abs( g_matWorldToProjection[3].x ) + abs( g_matWorldToProjection[3].y ) + abs( g_matWorldToProjection[3].z ) < 1e-6;
	}

	// Adaptive-quality factor for this object: 1 = full quality (near / large on screen), 0 = the
	// floor (small / far). ratio = camera distance measured in bounding-radii (scale-invariant).
	float LodQuality( float3 ro )
	{
		float3 c = 0.5 * (g_vBoundsMin + g_vBoundsMax);
		float r = max( 0.5 * length( g_vBoundsMax - g_vBoundsMin ), 0.001 );
		float ratio = distance( c, ro ) / r;
		return saturate( (g_flLodFar - ratio) / max( g_flLodFar - g_flLodNear, 0.001 ) );
	}

	// Sphere-trace the field along the current view ray (camera in the forward pass, light
	// in the shadow/depth pass). Returns false on a miss, else the world-space hit point.
	bool RayMarchHit( PixelInput i, out float3 hitP )
	{
		hitP = float3( 0, 0, 0 );

		// Projection-aware ray. vPositionWithOffsetWs = worldPos − g_vHighPrecisionLightingOffsetWs
		// (subtracted in the shared VS with the SAME constant this pass sees), so adding the constant
		// back recovers the exact fragment world position in ANY view — including shadow views, where
		// the offset is NOT the view's camera position (it stays the frame's game camera).
		float3 ro, rd;
		bool ortho = IsOrthoView();
		if ( ortho )
		{
			// Ortho view (directional-light shadow cascade, or an ortho editor viewport): rays are
			// PARALLEL along the view forward. Pull the origin back past the whole bounds so the
			// tEnter bracket below always enters from outside, wherever the ortho near plane sits.
			float3 wp = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
			rd = normalize( g_vCameraDirWs );
			ro = wp - rd * ( length( g_vBoundsMax - g_vBoundsMin ) + 8.0 );
		}
		else
		{
			// In camera views the offset IS the camera position, so the correction term is exactly
			// zero and this matches the original camera-relative ray bit-for-bit. In a PERSPECTIVE
			// shadow view (spot light) ro rebinds to the light while the offset stays the game
			// camera — the correction re-bases the interpolant onto the light so the ray is right
			// there too.
			ro = g_vCameraPositionWs;
			rd = normalize( i.vPositionWithOffsetWs + ( g_vHighPrecisionLightingOffsetWs.xyz - g_vCameraPositionWs ) );
		}

		// Bracket the march: a tight bounding sphere (round props) or the bounds AABB.
	#if ( D_TIGHT_BOUNDS )
		float3 oc = ro - g_vBoundsCenter;
		float bb = dot( oc, rd );
		float cc = dot( oc, oc ) - g_flBoundsRadius * g_flBoundsRadius;
		float disc = bb * bb - cc;
		if ( disc < 0.0 )
			return false;
		float sq = sqrt( disc );
		float tEnter = max( -bb - sq, 0.0 );
		float tExit = -bb + sq;
	#else
		float3 t0 = (g_vBoundsMin - ro) / rd;
		float3 t1 = (g_vBoundsMax - ro) / rd;
		float3 tsmall = min( t0, t1 );
		float3 tbig = max( t0, t1 );
		float tEnter = max( max( max( tsmall.x, tsmall.y ), tsmall.z ), 0.0 );
		float tExit = min( min( tbig.x, tbig.y ), tbig.z );
	#endif

	#if ( D_DEPTH_CLAMP && S_MODE_DEPTH == 0 )
		// FORWARD pass only: occlusion test. The depth buffer holds the nearest surface already
		// drawn — world/meshed geometry AND other SDF props (the raymarch writes the prepass), so
		// this is all the SDF-vs-SDF + SDF-vs-world occlusion with no separate buffer. If that
		// surface is IN FRONT of this object's bounding box (nearer than tEnter) the pixel is fully
		// occluded -> bail the whole march. We compare against tEnter, NOT the object's own surface:
		// an object's surface is always >= tEnter, so it can never occlude ITSELF — testing against
		// its own (Hi-Z-approximate) depth was the speckled noise. The -2 absorbs chain/precision
		// wobble at box-tangent pixels. Skipped in the prepass (it must write accurate depth).
		float3 sceneWp = Depth::GetWorldPosition( i.vPositionSs.xy );
		if ( length( sceneWp - ro ) < tEnter - 2.0 )
			return false;
	#endif

		if ( tExit < tEnter )
			return false;

		// Adaptive quality by projected size: full steps/epsilon when big on screen, the floors
		// when small/far. Per object (LodQuality uses the bounds centre), so every pixel of one
		// object marches at the same budget. Ortho (shadow) views have no meaningful camera
		// distance — the pulled-back per-pixel origin would read as "far" and melt the shadow to
		// the quality floor — so they march at full quality.
		float q = ortho ? 1.0 : LodQuality( ro );
		int   maxSteps = (int)lerp( (float)g_nMinSteps, (float)g_nMaxSteps, q );
		float eps      = lerp( g_flFarEpsilon, g_flEpsilon, q );

		float stepScale = 1.0;
	#if ( D_FIELD_TEX )
		// Trilinear interpolation of sampled distances isn't perfectly conservative (it can slightly
		// over-estimate in concave cells), so understep a touch to keep the trace from skipping the
		// surface. Cheap insurance — a texture step is ~brushCount× cheaper than the loop it replaced.
		// Grow the budget by the SAME factor so the understep doesn't cost the march its reach: without
		// this, grazing/silhouette rays run ~11% short of the surface and flicker hit<->miss = edge sparkle.
		stepScale = 0.9;
		maxSteps  = (int)( maxSteps / 0.9 );
	#endif
	#if ( D_DISPLACE )
		// A displaced field is no longer a true distance field: subtracting the noise inflates its
		// gradient by ~L, so a full sphere-trace step can overshoot the surface. Understep by 1/(1+L)
		// AND grow the step budget by the same (1+L) so grazing/silhouette rays still converge instead
		// of running out of steps (= edge misses). L ~= Amp*Freq*4 (the noise's max slope), so at
		// Amp=0 this is exactly a no-op: stepScale 1, budget unchanged -> identical to displace OFF.
		float L   = g_flDispAmp * g_flDispFreq * 4.0;
		stepScale *= 1.0 / (1.0 + L);
		maxSteps  = (int)( maxSteps * min( 1.0 + L, 2.0 ) ); // cap the budget growth so cost stays bounded
	#endif

		float t = tEnter;
		[loop]
		for ( int s = 0; s < maxSteps; s++ )
		{
			float d = SdfDist( ro + rd * t );
			if ( d < eps ) { hitP = ro + rd * ( t + d ); return true; } // refine onto surface (d~=0), not the eps-shell: kills field-path contour banding
			t += d * stepScale;
			if ( t > tExit ) break;
		}
		return false;
	}

	// World position -> depth-buffer value, matching how the rasterizer maps mesh depth into
	// the current viewport's Z range (correct for both the camera and shadow viewports).
	float DepthFromWorld( float3 p )
	{
		float4 vPs = Position4WsToPs( float4( p, 1.0 ) );
		return lerp( g_flViewportMinZ, g_flViewportMaxZ, vPs.z / vPs.w );
	}

	// Plain SV_Depth — NO conservative-depth promise. The promise direction depends on which box
	// face is rasterised (surface is behind the front faces but IN FRONT of the back faces), so no
	// single hint is ever valid; a violated hint lets the GPU wrongly early-cull marched pixels
	// (seen as background punching through the prop). Occlusion is handled in software by
	// D_DEPTH_CLAMP instead, which needs no hardware early-Z.
	struct SdfPixelOutput { float4 vColor : SV_Target0; float flDepth : SV_Depth; };

	SdfPixelOutput MainPs( PixelInput i, bool isFrontFace : SV_IsFrontFace )
	{
		SdfPixelOutput o;
		o.vColor = float4( 0, 0, 0, 0 );
		o.flDepth = 1.0;

	#if ( D_OVERDRAW_OPT )
		// March BACK faces only. Both faces of the proxy box march the identical ray, so culling
		// one halves the work; back faces are the robust pick because they exist from any camera
		// position (front faces near-clip away when the camera enters the box). No inside/outside
		// special case needed.
		if ( isFrontFace )
		{
			clip( -1 );
			return o;
		}
	#endif

		// RenderHidden pawn body: render ONLY into ortho (sun-shadow) views. Every perspective view —
		// camera forward, camera depth prepass, spot-light shadows — is clipped, so the hidden body
		// can't occlude or appear anywhere; only its sun shadow survives (ModelRenderer.ShadowsOnly
		// parity for the raymarched surface).
		if ( g_nSdfShadowOnly != 0 && !IsOrthoView() )
		{
			clip( -1 );
			return o;
		}

		float3 p;
		if ( !RayMarchHit( i, p ) )
		{
			clip( -1 );
			return o;
		}

	#if ( S_MODE_DEPTH )
		// Depth+normals prepass: output the SDF surface normal into the G-buffer (for SSAO/SSR) and
		// the surface depth — no shading. This is what gives the raymarch AO and puts it in the
		// engine depth buffer (so depth sorting + DepthClamp occlusion work via the engine chain).
		// This same mode also renders the SHADOW views: in an ortho view (a sun cascade) the written
		// depth is the shadow map. The optional caster bias pushes it a touch deeper along the light
		// ray there — acne insurance the camera prepass must never get (its depth must stay exact,
		// the forward pass depth-tests against it).
		float3 pd = p;
		if ( IsOrthoView() )
			pd += normalize( g_vCameraDirWs ) * g_flSdfShadowBias;
		o.vColor = DepthNormals::Output( SdfNormal( p ) );
		o.flDepth = DepthFromWorld( pd );
		return o;
	#endif

		// Forward depth, biased a hair TOWARD the camera. The prepass (above) wrote the true surface
		// depth; Forward and Depth are separately-compiled programs, so their re-marched hit points can
		// differ by sub-ULP, and the forward fragment is then depth-tested against the prepass value.
		// On the wrong side of that test (esp. with an EQUAL self-test) the fragment is discarded and
		// the background punches through — speckle/flicker over every SDF. Nudging toward the camera is
		// always "nearer" regardless of the depth convention, so forward never loses its own self-test.
		// The cached field is NOT unit-gradient (|∇| often < 1) and runs a bigger step budget, so its
		// prepass<->forward hit spread is wider than the analytic path's — it needs a larger nudge to stay
		// on the winning side. Both are negligible for sorting against real geometry.
	#if ( D_FIELD_TEX )
		float flDepthNudge = 0.5;
	#else
		float flDepthNudge = 0.1;
	#endif
		float flSurfaceDepth = DepthFromWorld( p - normalize( i.vPositionWithOffsetWs ) * flDepthNudge );

		// LOD debug: flat heatmap by quality (red = floor / cheapest, yellow = mid, green = full).
		if ( g_flDebugLod > 0.5 )
		{
			float q = LodQuality( g_vCameraPositionWs );
			float3 heat = q < 0.5
				? lerp( float3( 1, 0, 0 ), float3( 1, 1, 0 ), saturate( q * 2.0 ) )
				: lerp( float3( 1, 1, 0 ), float3( 0, 1, 0 ), saturate( q * 2.0 - 1.0 ) );
			o.vColor = float4( heat, 1.0 );
			o.flDepth = flSurfaceDepth;
			return o;
		}

		float3 baseN = SdfNormal( p ); // world-space gradient normal

		// Re-base the triplanar projection into the prop's MODEL space, so the plasticine pattern is
		// locked to the prop (moves/rotates with it) instead of the prop sliding through a fixed
		// world-space 3D texture. Albedo/roughness are space-agnostic; the normal map is projected in
		// model space then rotated back to world (below) since lighting needs a world normal. This is
		// the SAME model frame the meshed LOD uses (vPositionOs), so the two stay aligned on switch.
		float3 modelP = WorldToModelPos( p );
		float3 modelN = normalize( WorldToModelDir( baseN ) );

		float3 albedo = Tex2DTriplanar( g_tAlbedo, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend ).rgb;
		float roughness = Tex2DTriplanar( g_tRoughTex, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend ).r;
		float3 worldN = normalize( ModelToWorldDir( TriplanarNormal( g_tNormalTex, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend, g_flNormalStrength ) ) );

		i.vPositionWithOffsetWs = p - g_vCameraPositionWs;
		i.vNormalWs = worldN;

		// Blended colour + per-brush metalness/roughness at the hit point. Matches the mesh shader's
		// semantics so the meshed LODs and the raymarch agree: global sliders stay master controls.
		SdfSurface surf = SdfShade( p );

		Material m = Material::Init( i );
		m.Albedo = albedo * g_vTintColor * surf.col;
		m.Roughness = max( saturate( roughness * g_flRoughness * surf.rough ), 0.08 );
		m.Metalness = saturate( surf.metal + g_flMetalness );

		// Curvature → diffuse: darken crevices (seam grime), lift ridges (worn edges) — clay reads its
		// shape through these cues even under flat light. Albedo only, so lighting/spec stay physical.
		// Branched away entirely when both strengths are zero (saves 5 field taps).
		if ( g_flCurveDark + g_flCurveLight > 1e-3 )
		{
			float curv = SdfCurvature( p, max( g_flCurveRadius, 0.05 ) );
			float shade = (1.0 - g_flCurveDark * saturate( -curv )) * (1.0 + g_flCurveLight * saturate( curv ));
			m.Albedo = saturate( m.Albedo * shade );
		}

	#if ( D_TRANSMISSION && S_MODE_DEPTH == 0 )
		// Back-scatter using SDF-derived thickness. Runs once here, after the hit — the march above
		// is untouched. viewDir points from the surface toward the camera (toward -scatterDir = glow).
		ApplyTransmission( m, p, normalize( g_vCameraPositionWs - p ) );
	#endif

		o.vColor = ShadingModelStandard::Shade( i, m );
		o.flDepth = flSurfaceDepth;
		return o;
	}
}
