//=========================================================================================================================
// Mesh counterpart to sdf_raymarch.shader — same plasticine surface (triplanar albedo/normal/roughness, tint/roughness/
// metalness controls) but for ordinary geometry, tinted by VERTEX COLOUR instead of the SDF data texture. Use this on the
// meshed (SurfaceNets) LODs so close-up raymarched props and their baked meshes match.
//=========================================================================================================================
HEADER
{
	Description = "SDF Mesh";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
	float4 vColor : COLOR0 < Semantic( Color ); >; // mesh vertex colour (set by the SurfaceNets mesher)
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	// Model-space position carried so the triplanar projection runs in the prop's MODEL space (texture
	// locked to the prop) — the same frame the raymarched LOD uses, so they align on switch. The
	// object->world rotation rows do double duty: the PS derives the model-space normal by applying
	// their inverse to the world normal, and rotates the perturbed model normal back to world for
	// lighting. (TEXCOORD0-7/11 are taken by pixelinput.hlsl; 8/10/12 are free.)
	float3 vPositionOs  : TEXCOORD8;
	float3 vObjToWorld0 : TEXCOORD10;
	float3 vObjToWorld1 : TEXCOORD12;
	float3 vObjToWorld2 : TEXCOORD13;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		o.vVertexColor = i.vColor;
		// The mesher stows per-brush (metalness, roughness) in TexCoord0.xy — this surface is
		// triplanar so it has no real UVs to clobber. Carry it through to the pixel shader.
		o.vTextureCoords.xy = i.vTexCoord;

		// Model-space position drives the triplanar projection (locks the texture to the prop). The
		// object->world matrix's rotation rows let the PS move normals between model and world. Unit
		// scale assumed (the rows are then orthonormal, so the transpose is the inverse rotation).
		// vPositionOs is a plain position; vNormalOs is a compressed tangent frame, so we DON'T carry
		// it — the PS recovers the model normal from the decoded world normal instead.
		o.vPositionOs = i.vPositionOs;
		float3x4 matObjectToWorld = GetTransformMatrix( i.nInstanceTransformID );
		o.vObjToWorld0 = matObjectToWorld[0].xyz;
		o.vObjToWorld1 = matObjectToWorld[1].xyz;
		o.vObjToWorld2 = matObjectToWorld[2].xyz;
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "common/utils/triplanar.hlsl"

	RenderState( CullMode, DEFAULT );

	// --- Same material inputs as sdf_raymarch.shader ---
	SamplerState g_sRepeat < Filter( ANISOTROPIC ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( TextureColor,     Srgb,   8, "",                 "_color",  "Surface,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureNormal,    Linear, 8, "NormalizeNormals", "_normal", "Surface,10/20", Default3( 0.5, 0.5, 1.0 ) );
	CreateInputTexture2D( TextureRoughness, Linear, 8, "",                 "_rough",  "Surface,10/30", Default( 1.0 ) );

	Texture2D g_tAlbedo    < Channel( RGB, Box( TextureColor ),     Srgb );   OutputFormat( BC7 ); SrgbRead( true ); >;
	Texture2D g_tNormalTex < Channel( RGB, Box( TextureNormal ),    Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
	Texture2D g_tRoughTex  < Channel( R,   Box( TextureRoughness ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	float3 g_vTintColor  < UiType( Color ); Default3( 1.0, 1.0, 1.0 ); UiGroup( "Surface,10/40" ); >;
	float  g_flRoughness < Default( 0.7 ); Range( 0.0, 2.0 ); UiGroup( "Surface,10/50" ); >;
	float  g_flMetalness < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,10/60" ); >;
	float  g_flTriTile   < Default( 8.0 ); Range( 0.5, 64.0 ); UiGroup( "Surface,10/70" ); >;
	float  g_flTriBlend  < Default( 4.0 ); Range( 1.0, 16.0 ); UiGroup( "Surface,10/80" ); >;
	float  g_flNormalStrength < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,10/90" ); >; // 0 = ignore normal map, 1 = full
	// The albedo-texture multiply pulls dark tints toward the texture's own grey (chroma dies faster than
	// brightness in linear space). This re-saturates where the TEXTURE texel is dark — clay colours that
	// are dark by choice are untouched. 1 = off. Mirrored in sdf_raymarch.shader — keep the two in sync.
	float  g_flDarkSatBoost < Default( 1.0 ); Range( 1.0, 2.0 ); UiGroup( "Surface,10/95" ); >;

	// Per-object seed, same "BoilSeed" attribute the raymarcher gets (stamped by SdfRaymarchRenderer on
	// the sibling renderer and by SdfSculpture on model assignment). Drives SeedTexOffset below; 0 (unset)
	// = authored texture placement.
	float  g_flBoilSeed < Attribute( "BoilSeed" ); Default( 0.0 ); >;

	// Accurate sRGB->linear, so the gamma vertex colour matches the SrgbRead texture albedo.
	float3 SrgbToLinear( float3 c )
	{
		float3 lo = c / 12.92;
		float3 hi = pow( (c + 0.055) / 1.055, 2.4 );
		return lerp( lo, hi, step( 0.04045, c ) );
	}

	// Extrapolated saturation: lerp away from the colour's own grey (t > 1 oversaturates). Clamped low
	// side so already-saturated hues don't drive a channel negative. ~5 ALU, no fetches.
	float3 BoostSat( float3 col, float sat )
	{
		float luma = dot( col, float3( 0.2126, 0.7152, 0.0722 ) );
		return max( lerp( luma.xxx, col, sat ), 0.0 );
	}

	// Dark-tone re-saturation, driven by the TEXTURE sample's darkness — NOT the tinted result — so it
	// only gives back the chroma the texture multiply took, and an intrinsically dark clay colour under
	// a bright texel keeps exactly its picked colour. Boost scales with how much the texel darkens
	// (1 - luma), with a ×3 gain so it bites on bright grime maps: plasticine_basecolor averages ~0.83
	// LINEAR luma, so a plain 1-luma ramp (or the old 0.5-luma cutoff) left the slider a near-no-op.
	// Full boost from texel luma ~0.67 down. 1 = off.
	float3 BoostDarkSat( float3 col, float3 tex, float boost )
	{
		float texLuma = dot( tex, float3( 0.2126, 0.7152, 0.0722 ) );
		return BoostSat( col, lerp( 1.0, boost, saturate( (1.0 - texLuma) * 3.0 ) ) );
	}

	// STATIC per-instance offset for the triplanar maps, in model-space inches: identical models (every
	// hunter pawn) otherwise project identical texels, so they all wear the same dark patch in the same
	// place. Spread over 16 texture repeats via frac() of the per-object seed against the R3 irrationals —
	// whole repeats apart means fully uncorrelated texels. Seed 0 keeps the authored placement.
	// Mirrored in sdf_raymarch.shader — keep the two in sync.
	float3 SeedTexOffset()
	{
		return frac( g_flBoilSeed * float3( 0.8191725134, 0.6710436067, 0.5497004779 ) )
		     * 16.0 * ( 39.3701 / max( g_flTriTile, 0.001 ) );
	}

	// Triplanar normal mapping (Ben Golus whiteout blend) — identical to the raymarcher. Space-agnostic:
	// position and normal in the SAME frame, result in that frame. We feed MODEL space, so the result is
	// a model-space normal the caller rotates to world.
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

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// MODEL-space position + normal drive the triplanar projection, so the plasticine pattern is
		// locked to the prop (and matches the raymarched LOD, which projects in this same model frame,
		// so the pattern lines up across an LOD switch). Albedo/roughness are space-agnostic; the
		// normal map is projected in model space then rotated back to world (below) for lighting.
		float3x3 matObjToWorld = float3x3( i.vObjToWorld0, i.vObjToWorld1, i.vObjToWorld2 );
		float3 modelP = i.vPositionOs + SeedTexOffset();
		// World normal -> model normal via the inverse rotation. For an orthonormal (unit-scale) basis
		// the inverse is the transpose, which mul(vector, matrix) applies as R^T * worldN.
		float3 modelN = normalize( mul( i.vNormalWs, matObjToWorld ) );

		float3 albedo = Tex2DTriplanar( g_tAlbedo, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend ).rgb;
		float roughness = Tex2DTriplanar( g_tRoughTex, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend ).r;

		float3 modelNormal = TriplanarNormal( g_tNormalTex, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend, g_flNormalStrength );
		i.vNormalWs = normalize( mul( matObjToWorld, modelNormal ) );

		// Per-brush material from the vertex (blended across seams by the mesher): x = metalness, y = roughness.
		float2 vMR = i.vTextureCoords.xy;

		Material m = Material::Init( i );
		m.Albedo = albedo * g_vTintColor * SrgbToLinear( i.vVertexColor.rgb );
		m.Albedo = BoostDarkSat( m.Albedo, albedo, g_flDarkSatBoost );
		// Floor roughness so near-zero samples don't produce pinpoint specular fireflies. The global
		// g_flRoughness stays a master multiplier; the triplanar texture adds micro-detail.
		m.Roughness = max( saturate( roughness * g_flRoughness * vMR.y ), 0.08 );
		m.Metalness = saturate( vMR.x + g_flMetalness );

		float4 c = ShadingModelStandard::Shade( i, m );
		// Stop NaN/fireflies before the DoF gather smears them into bright bokeh discs.
		c.rgb = c.rgb == c.rgb ? c.rgb : 0.0; // NaN != NaN
		c.rgb = min( c.rgb, 64.0 );           // firefly clamp — tune to your exposure
		return c;
	}
}
