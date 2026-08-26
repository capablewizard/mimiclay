//=========================================================================================================================
// Mesh counterpart to sdf_raymarch.shader — same plasticine surface (triplanar albedo/normal/roughness, tint/roughness/
// metalness controls) but for ordinary geometry, tinted by VERTEX COLOUR instead of the SDF data texture. Use this on the
// meshed (SurfaceNets) LODs so close-up raymarched props and their baked meshes match. Clear the material's
// "Use Vertex Data" checkbox for meshes that carry no mesher data (engine primitives, imported models) —
// otherwise their missing/black COLOR0 zeroes the albedo. See Materials/plasticine_tint.vmat.
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
	// The editor mesh-geometry paint channel (MeshComponent puts its per-corner Colors here, same
	// semantic blendable.shader reads). MeshCurvatureBaker bakes signed curvature into its ALPHA:
	// 128 = flat, 255 = full ridge, 1 = full crevice, 0 = no data. Meshes without the attribute
	// (SurfaceNets bakes) read all zeros = no data.
	float4 vPaintColor : TEXCOORD5 < Semantic( VertexPaintTintColor ); >;
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
	// xyz: per-instance tint (the renderer's Tint / SceneObject.ColorTint). ProcessVertex normally
	// delivers this in vVertexColor, but we need that slot for the mesher's COLOR0, so it rides its
	// own interpolant. Already LINEAR — unlike the vertex colour, which is gamma and decoded in the PS.
	// w: baked signed curvature decoded from the paint channel's alpha (0 when the mesh carries none) —
	// interpolating the per-vertex value is exactly what makes it smooth where the ddx estimate is blocky.
	float4 vInstanceTint : TEXCOORD14;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		// Grab the per-instance tint before overwriting vVertexColor with the mesh's own COLOR0 —
		// ProcessVertex seeds that slot from it, so assigning i.vColor is what used to make the
		// renderer's Tint property a no-op on this shader.
		ExtraShaderData_t extraShaderData = GetExtraPerInstanceShaderData( i.nInstanceTransformID );
		o.vInstanceTint.rgb = extraShaderData.vTint.rgb;
		// Decode baked curvature from the paint alpha here so the PS interpolates the SIGNED value.
		// 0 is reserved for "no data" (unpainted corners and meshes without the attribute) -> flat.
		o.vInstanceTint.w = i.vPaintColor.a > 0.001 ? i.vPaintColor.a * 2.0 - 1.0 : 0.0;
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
	#include "common/classes/Depth.hlsl" // scene depth (prepass) — drives the world-cavity curvature
	#include "clay_cutout.hlsl"          // camera-occlusion peek hole (see ClayCutout.cs)

	RenderState( CullMode, DEFAULT );

	// Camera-occlusion cutout (clay_cutout.hlsl): lets this prop dissolve open when it stands between the
	// camera and the local hider's disguise. The hider's OWN prop is protected by the include's distance
	// test, so leaving this on everywhere is safe — untick to make a material never cut.
	bool g_bClayCutout < UiType( CheckBox ); Default( 1 ); UiGroup( "Surface,10/01" ); >;

	// --- Same material inputs as sdf_raymarch.shader ---
	SamplerState g_sRepeat < Filter( ANISOTROPIC ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( TextureColor,     Srgb,   8, "",                 "_color",  "Surface,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureNormal,    Linear, 8, "NormalizeNormals", "_normal", "Surface,10/20", Default3( 0.5, 0.5, 1.0 ) );
	CreateInputTexture2D( TextureRoughness, Linear, 8, "",                 "_rough",  "Surface,10/30", Default( 1.0 ) );

	Texture2D g_tAlbedo    < Channel( RGB, Box( TextureColor ),     Srgb );   OutputFormat( BC7 ); SrgbRead( true ); >;
	Texture2D g_tNormalTex < Channel( RGB, Box( TextureNormal ),    Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;
	Texture2D g_tRoughTex  < Channel( R,   Box( TextureRoughness ), Linear ); OutputFormat( BC7 ); SrgbRead( false ); >;

	// Off = ignore the two per-vertex channels the SurfaceNets mesher writes (COLOR0 clay colour, and the
	// per-brush metalness/roughness it stows in TexCoord0.xy) and fall back to white / the sliders below.
	// Needed for ordinary geometry: an engine cube primitive has no COLOR0, so it reads black and the tint
	// multiply kills the albedo, and its real UVs would otherwise be misread as a material pair.
	bool g_bUseVertexData < UiType( CheckBox ); Default( 1 ); UiGroup( "Surface,10/05" ); >;

	float3 g_vTintColor  < UiType( Color ); Default3( 1.0, 1.0, 1.0 ); UiGroup( "Surface,10/40" ); >;
	// How strongly the base colour texture multiplies over the tint: 1 = full multiply (default),
	// 0 = texture ignored, pure tint. Fades the SAMPLE toward white, so the dark-sat boost below
	// (driven by the texel's darkness) scales down with it automatically.
	float  g_flBaseTexAmount < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,10/45" ); >;
	float  g_flRoughness < Default( 0.7 ); Range( 0.0, 2.0 ); UiGroup( "Surface,10/50" ); >;
	float  g_flMetalness < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,10/60" ); >;
	float  g_flTriTile   < Default( 8.0 ); Range( 0.01, 64.0 ); UiGroup( "Surface,10/70" ); >;
	// Tiling for the normal map, fully independent of Tri Tile (which covers base colour + roughness).
	float  g_flTriTileNormal < Default( 8.0 ); Range( 0.01, 64.0 ); UiGroup( "Surface,10/72" ); >;
	float  g_flTriBlend  < Default( 4.0 ); Range( 1.0, 16.0 ); UiGroup( "Surface,10/80" ); >;
	float  g_flNormalStrength < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Surface,10/90" ); >; // 0 = ignore normal map, 1 = full
	// The albedo-texture multiply pulls dark tints toward the texture's own grey (chroma dies faster than
	// brightness in linear space). This re-saturates where the TEXTURE texel is dark — clay colours that
	// are dark by choice are untouched. 1 = off. Mirrored in sdf_raymarch.shader — keep the two in sync.
	float  g_flDarkSatBoost < Default( 1.0 ); Range( 1.0, 2.0 ); UiGroup( "Surface,10/95" ); >;

	// Curvature shading — the geometry counterpart of sdf_raymarch's field-Laplacian version (same
	// controls, same albedo dark/light application): crevices darken (seam grime), ridges lighten
	// (worn edges). Default source is a WORLD CAVITY estimate à la Blender's viewport "World" mode:
	// depth-prepass taps in a world-radius ring around the pixel, measuring whether the local scene
	// sits above the tangent plane (valley) or falls away below it (ridge). Texture taps cross
	// triangle/object boundaries — unlike derivatives — so it's smooth over hard-edged geometry,
	// needs no bake, and is gated to this material by construction (it runs IN the material).
	// Defaults OFF so existing materials are untouched.
	//
	// On = take curvature from the mesh-paint channel's alpha instead (MeshCurvatureBaker):
	// per-vertex, cheaper (no depth taps), feature scale fixed at bake time.
	bool g_bCurveFromVertexPaint < UiType( CheckBox ); Default( 0 ); UiGroup( "Curvature,13/05" ); >;
	// World radius of the cavity neighbourhood — the feature size it responds to (ignored by the
	// baked path, whose feature scale is baked in).
	float g_flCurveRadius < Default( 1.5 );  Range( 0.25, 16.0 ); UiGroup( "Curvature,13/10" ); >;
	float g_flCurveDark   < Default( 0.0 );  Range( 0.0, 1.0 );  UiGroup( "Curvature,13/20" ); >;
	float g_flCurveLight  < Default( 0.0 );  Range( 0.0, 1.0 );  UiGroup( "Curvature,13/30" ); >;
	// Re-saturation for the cavity darkening above, scaled by how much shade was applied. 1 = off.
	float g_flCurveSatBoost < Default( 1.0 ); Range( 1.0, 2.0 ); UiGroup( "Curvature,13/40" ); >;
	// Same, for the ridge LIGHTENING: the lift (and its saturate clamp) washes worn edges toward
	// white — this gives them their colour back, scaled by how much lift was applied. 1 = off.
	float g_flCurveLightSatBoost < Default( 1.0 ); Range( 1.0, 2.0 ); UiGroup( "Curvature,13/45" ); >;
	// Offsets applied on RIDGES only (convex/worn edges — cavities are untouched): added to the
	// final roughness and to the normal-map strength, scaled by the ridge response.
	// Negative = edges read handled/polished (shinier, fingerprint grain pressed out); positive =
	// edges read chalky/broken (rougher, extra grain). 0 = off.
	float g_flCurveRoughBoost  < Default( 0.0 ); Range( -1.0, 1.0 ); UiGroup( "Curvature,13/50" ); >;
	float g_flCurveNormalBoost < Default( 0.0 ); Range( -1.0, 1.0 ); UiGroup( "Curvature,13/60" ); >;

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
		// Camera-occlusion cutout, before ANY shading work — and in the S_MODE_DEPTH compile too (the prepass
		// hole is what actually reveals what's behind; shadow views are gated inside the include). clip()
		// lives HERE, not in a helper — a nested discard has ICE'd DXC before (see clay_cutout.hlsl).
		float cutRim = 0.0, cutOutline = 0.0;
		if ( g_bClayCutout && ClayCutoutHit( i.vPositionSs.xy, i.vPositionWithOffsetWs.xyz + g_vCameraPositionWs,
		                                     normalize( i.vNormalWs.xyz ), cutRim, cutOutline ) )
			clip( -1.0 );

		// MODEL-space position + normal drive the triplanar projection, so the plasticine pattern is
		// locked to the prop (and matches the raymarched LOD, which projects in this same model frame,
		// so the pattern lines up across an LOD switch). Albedo/roughness are space-agnostic; the
		// normal map is projected in model space then rotated back to world (below) for lighting.
		float3x3 matObjToWorld = float3x3( i.vObjToWorld0, i.vObjToWorld1, i.vObjToWorld2 );
		float3 modelP = i.vPositionOs + SeedTexOffset();
		// World normal -> model normal via the inverse rotation. For an orthonormal (unit-scale) basis
		// the inverse is the transpose, which mul(vector, matrix) applies as R^T * worldN.
		float3 modelN = normalize( mul( i.vNormalWs, matObjToWorld ) );
		// Smooth vertex normal in world space, captured before the normal-map overwrite below — the
		// cavity estimate wants the clean geometric tangent plane, not the map's fake bumps.
		float3 smoothNWs = normalize( i.vNormalWs );

		// --- Curvature estimate, up front: it feeds the albedo shade AND the roughness/normal-strength
		// offsets, so it must exist before the normal map is sampled. Branches away (curv stays 0) when
		// nothing consumes it. Compiled out of depth/shadow views — no valid depth chain to tap there
		// (and nothing it feeds survives those passes anyway).
		float curv = 0.0;
	#if ( S_MODE_DEPTH == 0 )
		if ( g_flCurveDark + g_flCurveLight + abs( g_flCurveRoughBoost ) + abs( g_flCurveNormalBoost ) > 1e-3 )
		{
			if ( g_bCurveFromVertexPaint )
			{
				// Baked per-vertex curvature (MeshCurvatureBaker), already signed [-1,1] and smoothed
				// over the mesh — the interpolator does the rest. ×1.5 so a full bake reaches the
				// response clamp below.
				curv = i.vInstanceTint.w * 1.5;
			}
			else
			{
				// World cavity from the depth prepass (Blender viewport "World" mode): 12 taps on two
				// rings in the tangent plane at g_flCurveRadius, each resolved back to a world position
				// through the depth buffer. Mean elevation of those points against the tangent plane is
				// the signed cavity — scene above the plane = valley, falling away below = ridge.
				// Depth TAPS cross triangle/object boundaries where derivatives can't, which is what
				// keeps this smooth over hard-edged geometry with no bake and no per-prop workflow.
				float3 P = i.vPositionWithOffsetWs.xyz + g_vCameraPositionWs;
				float3 t1 = normalize( abs( smoothNWs.z ) < 0.99
				                       ? cross( smoothNWs, float3( 0, 0, 1 ) )
				                       : cross( smoothNWs, float3( 1, 0, 0 ) ) );
				float3 t2 = cross( smoothNWs, t1 );

				// Each pixel of the hardware 2x2 quad rotates the ring pattern by a different step
				// inside its 60° period; the quad-share blur after the loop folds the lanes together,
				// so a pixel effectively sees 4 rotations = 48 tap directions for 12 samples.
				float2 parity = frac( floor( i.vPositionSs.xy ) * 0.5 ) * 2.0; // 0 even / 1 odd per axis
				float quadRot = ( parity.x + parity.y * 2.0 ) * ( 6.2831853 / 6.0 / 4.0 );

				float sum = 0.0, wsum = 0.0;
				[unroll]
				for ( int t = 0; t < 12; t++ )
				{
					// Outer ring of 6 + inner ring (0.55r) rotated 30° — one feature scale still sees
					// both the wide shape and the near shape; cheap banding insurance.
					float ring = t < 6 ? 1.0 : 0.55;
					float ang = quadRot + ( t % 6 ) * ( 6.2831853 / 6.0 ) + ( t < 6 ? 0.0 : 0.5235988 );
					float3 tapWs = P + ( t1 * cos( ang ) + t2 * sin( ang ) ) * ( g_flCurveRadius * ring );

					float4 proj = Position4WsToPs( float4( tapWs, 1.0 ) );
					float2 uv = ( proj.xy / proj.w ) * float2( 0.5, -0.5 ) + 0.5;
					if ( proj.w <= 0.0 || any( saturate( uv ) != uv ) )
						continue; // behind the camera / offscreen — no information

					float3 T = Depth::GetWorldPosition( uv * g_vViewportSize.xy );
					float3 d = T - P;
					float dist = length( d );
					if ( dist < 1e-4 )
						continue;

					// Elevation above the tangent plane as a pure direction, so magnitude can't blow
					// it up. The weight fades taps whose depth resolved far from the shell — unrelated
					// geometry through a doorway, foreground occluders, sky — those say nothing about
					// LOCAL curvature. A flat floor resolves taps at ~radius distance, so w = 1 there.
					float w = saturate( 2.0 - dist / g_flCurveRadius );
					sum += ( dot( d, smoothNWs ) / dist ) * w;
					wsum += w;
				}

				// Negated: elevation-positive means concave, our convention is ridge-positive. ×4 gain:
				// a hard 90° interior corner averages ~0.35 elevation — that maps to full response
				// while gentle slopes stay subtle.
				curv = wsum > 1e-3 ? -( sum / wsum ) * 4.0 : 0.0;

				// Quad-share blur (SM5 QuadReadAcross): _fine derivatives hand us the horizontal and
				// vertical quad-neighbours' estimates — each computed with a different ring rotation —
				// for ~3 ALU and zero extra depth taps. Averaging self + both neighbours is a 1px blur
				// AND a 3x wider tap set in one move. dir flips the derivative toward the other lane
				// of each pair (ddx_fine returns odd-minus-even for both lanes).
				float2 dir = 1.0 - 2.0 * parity;
				curv += ( dir.x * ddx_fine( curv ) + dir.y * ddy_fine( curv ) ) / 3.0;
			}
			// Clamp BEFORE the response curves so few-tap spikes can't exceed full effect; 1.5 still
			// reaches 1.0 after the knee.
			curv = clamp( curv, -1.5, 1.5 );
		}
	#endif // S_MODE_DEPTH == 0
		// Soft-knee responses (smoothstep) so every consumer eases in/out instead of banding at the
		// effect's edges. Ridges drive the albedo lift AND the roughness/normal offsets; valleys
		// only ever darken the albedo.
		float curvRidge  = smoothstep( 0.0, 1.0, curv );   // convex — worn edges
		float curvValley = smoothstep( 0.0, 1.0, -curv );  // concave — seams, contacts

		float3 albedo = Tex2DTriplanar( g_tAlbedo, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend ).rgb;
		// Fade the base texture toward white by g_flBaseTexAmount — at 0 the tint colours the surface
		// alone. Done on the sample itself so everything downstream that reads the texel (the tint
		// multiply AND BoostDarkSat's darkness estimate) sees the weakened texture consistently.
		albedo = lerp( float3( 1.0, 1.0, 1.0 ), albedo, g_flBaseTexAmount );
		float roughness = Tex2DTriplanar( g_tRoughTex, g_sRepeat, modelP, modelN, g_flTriTile, g_flTriBlend ).r;

		// Ridge curvature offsets the normal-map strength: negative boost presses the grain out of
		// worn edges (handled clay), positive roughs them up. Cavities keep the base strength.
		float normalStrength = saturate( g_flNormalStrength + g_flCurveNormalBoost * curvRidge );
		float3 modelNormal = TriplanarNormal( g_tNormalTex, g_sRepeat, modelP, modelN, g_flTriTileNormal, g_flTriBlend, normalStrength );
		i.vNormalWs = normalize( mul( matObjToWorld, modelNormal ) );

		// Per-brush material from the vertex (blended across seams by the mesher): x = metalness, y = roughness.
		// Neutral pair (no metal, unscaled roughness) when this mesh carries no mesher data.
		float2 vMR = g_bUseVertexData ? i.vTextureCoords.xy : float2( 0.0, 1.0 );
		float3 vVertexTint = g_bUseVertexData ? SrgbToLinear( i.vVertexColor.rgb ) : float3( 1.0, 1.0, 1.0 );

		Material m = Material::Init( i );
		// g_vTintColor = material-wide tint; vInstanceTint = per-renderer Tint (already linear).
		m.Albedo = albedo * g_vTintColor * i.vInstanceTint.rgb * vVertexTint;
		m.Albedo = BoostDarkSat( m.Albedo, albedo, g_flDarkSatBoost );

		// Curvature → diffuse, mirroring sdf_raymarch (crevices darken, ridges lighten). All-zero
		// no-op when the strengths are off; the expensive estimate already ran (or didn't) above.
		float cavity = g_flCurveDark * curvValley;
		float ridgeLift = g_flCurveLight * curvRidge;
		float shade = (1.0 - cavity) * (1.0 + ridgeLift);
		m.Albedo = saturate( m.Albedo * shade );
		// Give both shades their colour back, each scaled by how much was applied — flats untouched.
		// Cavity and ridge are mutually exclusive, so folding the two lerps into one factor is exact.
		m.Albedo = BoostSat( m.Albedo, lerp( 1.0, g_flCurveSatBoost, cavity )
		                             * lerp( 1.0, g_flCurveLightSatBoost, saturate( ridgeLift ) ) );

		// Floor roughness so near-zero samples don't produce pinpoint specular fireflies. The global
		// g_flRoughness stays a master multiplier; the triplanar texture adds micro-detail. The
		// curvature offset is added last, after all multipliers, so it bites even on dark/rough setups.
		m.Roughness = max( saturate( roughness * g_flRoughness * vMR.y + g_flCurveRoughBoost * curvRidge ), 0.08 );
		m.Metalness = saturate( vMR.x + g_flMetalness );

		// Cutout rim (clay_cutout.hlsl): darkened cross-section band where the tunnel's eroded edge grazes
		// this fragment. Zero everywhere the cutout isn't biting. (The outline composites after Shade below.)
		m.Albedo *= 1.0 - cutRim * g_flClayCutoutRimDarken;

		float4 c = ShadingModelStandard::Shade( i, m );
		// Stop NaN/fireflies before the DoF gather smears them into bright bokeh discs.
		c.rgb = c.rgb == c.rgb ? c.rgb : 0.0; // NaN != NaN
		c.rgb = min( c.rgb, 64.0 );           // firefly clamp — tune to your exposure
		// Cutout outline AFTER the lighting (and the clamps, so the line is exact): a flat unlit colour
		// tracing the eroded edge — matches SdfHighlightOutline's read.
		c.rgb = lerp( c.rgb, g_vClayCutoutOutline.rgb, cutOutline );
		return c;
	}
}
