//=========================================================================================================================
// SDF raymarcher. Renders a bounding box; the pixel shader sphere-traces the SDF brush list along the
// world-space view ray, folding each sample into the prop's LOCAL frame where the brushes are packed and
// evaluated (the shared sdf_eval.hlsl — one evaluator with the GPU field baker), then reconstructs the hit
// position + normal and hands them to the standard shading model. Modelled on base/shaders/lpv_debug.shader.
//=========================================================================================================================
HEADER
{
	Description = "SDF Raymarch";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"

	// Viewmodel lighting variant (see S_TRANSLUCENT in PS): materials opt in — the first-person gun's
	// material sets this so its shading skips the screen-space passes; world props stay opaque.
	Feature( F_TRANSLUCENT, 0..1, "Translucent" );
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

	// Viewmodel-FOV warp inputs (mirrored declarations of the per-object attributes the PS reads —
	// VS and PS are separate programs). Scale = tan(cameraHalfFov)/tan(viewmodelHalfFov), 1 = off.
	int   g_nSdfViewmodel   < Attribute( "SdfViewmodel" );  Default( 0 ); >;
	float g_flSdfVmFovScale < Attribute( "SdfVmFovScale" ); Default( 1.0 ); >;

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );

		// Viewmodel FOV: inflate the proxy's LATERAL view-space extent so its raster footprint covers
		// exactly the pixels the gun-FOV rays can reach (narrower gun FOV = magnified gun = bigger
		// footprint). The PS undoes this per pixel, turning each pixel's ray into the ray the GUN's
		// projection would have generated — a true second FOV without a second camera. The warp is
		// AFFINE, so the box stays a clean parallelepiped and interpolation stays exact. Position here
		// is TRUE world space (FinalizeVertex subtracts the high-precision offset after us, and
		// Position3WsToPs takes true world in). Only ever runs in the camera forward view: the
		// viewmodel casts no shadows, and its translucent variant clips the whole Depth mode.
		if ( g_nSdfViewmodel != 0 && g_flSdfVmFovScale > 0.001 )
		{
			float3 fwd = normalize( g_vCameraDirWs );
			float3 rel = o.vPositionWs.xyz - g_vCameraPositionWs;
			float axial = dot( rel, fwd );
			float3 warped = g_vCameraPositionWs + fwd * axial + ( rel - fwd * axial ) * g_flSdfVmFovScale;
			o.vPositionWs.xyz = warped;
			o.vPositionPs = Position3WsToPs( warped );
		}

		return FinalizeVertex( o );
	}
}

PS
{
	// Viewmodel lighting variant: S_TRANSLUCENT tells the ENGINE's lighting code to treat this surface
	// as not-being-in-the-depth-buffer — vr_lighting skips the screen-space AO sample (and translucent
	// practice skips the screen-space shadow terms), which is exactly what an overlay-drawn viewmodel
	// needs. Selected per MATERIAL via F_TRANSLUCENT (plasticine_viewmodel.vmat); world props keep the
	// opaque variant. Translucency here is a LIGHTING configuration, NOT a blend mode: the guard
	// defines below pre-empt the engine's S_TRANSLUCENT defaults (depth write OFF, alpha blending ON —
	// see sbox_pixel.fxc) so the draw stays opaque and depth-writing in BOTH variants — the same trick
	// the engine's own glass.shader uses.
	StaticCombo( S_TRANSLUCENT, F_TRANSLUCENT, Sys( ALL ) );

	#define DEPTH_STATE_ALREADY_SET
	RenderState( DepthEnable, true );
	RenderState( DepthFunc, GREATER_EQUAL );
	RenderState( DepthWriteEnable, true );
	#define BLEND_MODE_ALREADY_SET 1

	#include "common/pixel.hlsl"
	#include "common/utils/triplanar.hlsl"
	#include "common/classes/Depth.hlsl" // scene depth (Hi-Z chain) for occlusion clamping
	#include "common/gbuffer.hlsl"       // DepthNormals::Output + the S_MODE_DEPTH combo
	#include "common/classes/Light.hlsl" // Light::* + DirectionalLightShadow (transmission lighting)

	RenderState( CullMode, NONE );

	// (D_OVERDRAW_OPT and D_TIGHT_BOUNDS were combos; they're the g_nSdfOverdrawOpt / g_nSdfTightBounds
	// RUNTIME uniforms now — see the g_nSdfCull note below for why combos are a scarce resource here.
	// D_DEPTH_CLAMP (software occlusion against the depth chain) was deleted outright: nothing shipped
	// used it — inter-object occlusion rides the engine depth chain via the prepass.)

	// Plasticine displacement, LIVE (analytic-fallback) path: subtract a low-frequency 3D noise
	// field from the SDF per sample so the raymarched SILHOUETTE goes lumpy (the meshed LOD is
	// untouched). On the NORMAL path the renderer bakes these same lumps into the distance field
	// (SdfDistBaked in the compute evaluators, re-baked per boil tick) and sets this combo OFF —
	// the march then samples them in its one trilinear fetch and only pays the g_flDispGradL
	// understep below. This combo is only 1 while Displace is on but no baked field is bound.
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
		// multiplying variants. Brush LOCAL-space AABBs are packed in texels 5/6; the spline span bound is a sphere.)

	// Shared analytic evaluator (LOCAL/model space): the brush/spline/text inputs, every primitive, the
	// combine ops and the packed-brush loop (SdfDist at a LOCAL point) all live in sdf_eval.hlsl — ONE
	// definition shared with the GPU field baker's compute shaders (sdf_field_cs / sdf_atlas_fill_cs /
	// sdf_brick_classify_cs). The renderer packs brushes in the prop's local frame; this shader folds
	// each world-space sample into that frame once (SdfDistWs below), so there is no world-space
	// evaluator copy to keep in sync any more.
	#include "sdf_eval.hlsl"

	float3    g_vBoundsMin    < Attribute( "BoundsMin" ); >;
	float3    g_vBoundsMax    < Attribute( "BoundsMax" ); >;
	float3    g_vBoundsCenter < Attribute( "BoundsCenter" ); >;
	float     g_flBoundsRadius < Attribute( "BoundsRadius" ); Default( 1.0 ); >;
	// Object placement — the world<->model fold. The ray marches in world space, but the brushes and
	// the baked field live in the prop's LOCAL frame, so every field sample folds through this
	// transform (and the triplanar projection re-bases through it too). Assumes unit scale: the
	// transform is rigid, so the inverse rotation is the quat conjugate and distances are preserved.
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
	// Viewmodel (the hunter's first-person gun, drawn via the overlay layer): 1 = viewmodel-FOV warp on,
	// and the material AO swaps to a 5-tap AO computed from the DISTANCE FIELD itself (the engine min()s
	// it against screen AO). Depth is REAL in both passes: the engine's overlay depth prepass runs before
	// the world prepasses and stencil-claims the gun's pixels (bit 0x80), so nearer world depth can never
	// overwrite them — anti-clip without the old near-camera depth squash, whose near blob used to punch
	// the gun through the depth-testing screen UI and poison contact shadows/SSAO.
	int       g_nSdfViewmodel   < Attribute( "SdfViewmodel" );  Default( 0 ); >;
	// Viewmodel FOV ratio, tan(cameraHalfFov)/tan(viewmodelHalfFov): the VS inflates the proxy's lateral
	// footprint by this, and MainPs divides it back out of the interpolated position so the marched rays
	// belong to the VIEWMODEL's projection — camera FOV and gun FOV fully decoupled. 1 = off.
	float     g_flSdfVmFovScale < Attribute( "SdfVmFovScale" ); Default( 1.0 ); >;
	// Ex-combos, runtime now (variant budget — see the g_nSdfCull note): skip the redundant back-face
	// march when the box's front faces are on screen, and bracket the march with the bounding SPHERE
	// (round props) instead of the AABB. Uniform per draw, so the branches are effectively free.
	int       g_nSdfOverdrawOpt < Attribute( "SdfOverdrawOpt" ); Default( 1 ); >;
	int       g_nSdfTightBounds < Attribute( "SdfTightBounds" ); Default( 0 ); >;
	// Scene-depth occlusion clamp for the FORWARD march (see RayMarchHit). Runtime toggle, same
	// variant-budget reasoning as the two above.
	int       g_nSdfDepthCull   < Attribute( "SdfDepthCull" ); Default( 1 ); >;
	// Gradient bound of the BAKED displaced field: the bake subtracts the lumps into the volume,
	// steepening its slope by up to this L (= worst-tick amp · freq · 4). 0 = field is a true
	// distance field (no displacement baked). Drives the same understep + budget growth the live
	// D_DISPLACE path computes for itself — pushed by the renderer because only IT knows whether
	// the bound field was baked with displacement.
	float     g_flDispGradL     < Attribute( "DispGradL" ); Default( 0.0 ); >;
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
	// The albedo-texture multiply pulls dark tints toward the texture's own grey (chroma dies faster than
	// brightness in linear space). This re-saturates where the TEXTURE texel is dark — clay colours that
	// are dark by choice are untouched. 1 = off. Mirrored in sdf_mesh.shader — keep the two in sync.
	float g_flDarkSatBoost < Default( 1.0 ); Range( 1.0, 2.0 ); UiGroup( "Surface,10/95" ); >;

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

	// Displacement look — ATTRIBUTES pushed by SdfRaymarchRenderer (its DispAmp/DispFreq properties),
	// NOT material params like the transmission look. They used to be: but the lumps are baked into
	// the field by C# now, which must read the values back — and Material.Attributes is a wrapper
	// captured at LOAD, so material-editor edits never reached the bake and tuning silently did
	// nothing. The renderer owns them instead; only the LIVE fallback path reads these constants
	// here (the baked path gets the same values via the field bake). Amp = lump depth (inches);
	// Freq = lump density. Keep Amp*Freq modest (~<0.15) or the field stops being a valid distance
	// for sphere tracing and the surface develops holes; outward lumps ride the proxy's ~2" pad, so
	// keep Amp under ~1.5 — and keep the wavelength (1/Freq) at least ~2-3 field VOXELS or the bake
	// can't resolve the lumps (they fade/alias, differently per prop size).
	float g_flDispAmp  < Attribute( "DispAmp" );  Default( 0.35 ); >;
	float g_flDispFreq < Attribute( "DispFreq" ); Default( 0.25 ); >;

	// Whether the lumps also bend the prop's SHADOW silhouette. An ATTRIBUTE, not a material param —
	// it's a per-prop cost/look call pushed by SdfRaymarchRenderer.DisplaceShadows, not part of the
	// plasticine look shared by every prop on the material. Defaults to 0 so any path that forgets to
	// push it gets the cheap behaviour rather than silently paying for shadow lumps (see
	// SetupDisplacement for why ortho views are the expensive place to displace).
	int g_nDispShadows < Attribute( "DispShadows" ); Default( 0 ); >;

	// Claymation boil (see Displacement below). Pushed per-object by SdfRaymarchRenderer from the
	// prop's OWN ClayBoil component — it's opt-in per GameObject, not a scene-wide look, because a
	// whole room boiling at once reads as an unstable picture rather than as handmade props.
	// ALL DEFAULT TO 0 = still. That matters: these are what a prop with NO ClayBoil renders with, so
	// a non-zero default here would silently boil every prop in the game.
	float g_flBoilFps       < Attribute( "BoilFps" );       Default( 0.0 ); >; // ticks/sec; 0 = boil off
	float g_flBoilJitter    < Attribute( "BoilJitter" );    Default( 0.0 ); >; // offset per tick, noise CELLS
	float g_flBoilAmpJitter < Attribute( "BoilAmpJitter" ); Default( 0.0 ); >; // +/- lump-depth wobble per tick
	float g_flBoilSeed      < Attribute( "BoilSeed" );      Default( 0.0 ); >; // per-prop, so props don't lockstep
	// The current boil pose — wrapped, unseeded — computed on the CPU (ClayBoil.TickAt) and pushed
	// every frame. CPU-authored rather than derived from g_flTime here so an impact burst can splice
	// in an off-grid jolt pose the moment a shot lands, and so the live tick and the baked field
	// agree by construction (both read the same value).
	float g_flBoilTick      < Attribute( "BoilTick" );      Default( 0.0 ); >;
	// Surface-detail boil: shifts the triplanar sample point so the fingerprints/imperfections in the
	// normal map re-form each tick too. Measured in texture REPEATS. Shading-only (once at the hit),
	// so unlike the displacement boil it costs no march steps and needs no step-safety adjustment.
	float g_flBoilTexJitter < Attribute( "BoilTexJitter" ); Default( 0.0 ); >;

	// Curvature shading: the field's Laplacian at the hit approximates surface curvature — negative in
	// crevices, positive on ridges. Crevices darken the diffuse (grime in the clay seams), ridges lighten
	// it (worn-edge highlight). Radius = the feature size it responds to (world units); both strengths at
	// 0 branch the extra field taps away entirely. MATERIAL params, tuned in the .vmat.
	float g_flCurveRadius < Default( 1.5 );  Range( 0.25, 8.0 ); UiGroup( "Curvature,13/10" ); >;
	float g_flCurveDark   < Default( 0.35 ); Range( 0.0, 1.0 );  UiGroup( "Curvature,13/20" ); >;
	float g_flCurveLight  < Default( 0.15 ); Range( 0.0, 1.0 );  UiGroup( "Curvature,13/30" ); >;
	// Re-saturation for the cavity darkening above — same grey-out problem as the texture multiply, but
	// scaled by how much crevice shade was applied instead of by luminance. 1 = off.
	float g_flCurveSatBoost < Default( 1.0 ); Range( 1.0, 2.0 ); UiGroup( "Curvature,13/40" ); >;

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

	// World <-> object(model) space — the fold every field sample and the triplanar projection go
	// through. Rigid (rotation + translation, unit scale), so the inverse rotation is the quat
	// conjugate and distances survive the fold unchanged. (qrot comes from sdf_eval.hlsl.)
	float3 WorldToModelPos( float3 wp )  { return qrot( float4( -g_vModelRotation.xyz, g_vModelRotation.w ), wp - g_vModelOrigin ); }
	float3 WorldToModelDir( float3 wd )  { return qrot( float4( -g_vModelRotation.xyz, g_vModelRotation.w ), wd ); }
	float3 ModelToWorldPos( float3 mp )  { return qrot( g_vModelRotation, mp ) + g_vModelOrigin; }
		float3 ModelToWorldDir( float3 md )  { return qrot( g_vModelRotation, md ); }

		// Sample the baked field at LOCAL point lp (the field is baked in the prop's local frame — the
		// caller already folded): map to a UVW and do one trilinear fetch. Samples sit on the inclusive
		// corners, so the 0..1 position is stretched across (Dims-1) cells and offset by the half-texel.
		float SampleFieldTex( float3 lp )
		{
			float3 t  = (lp - g_vFieldMin) / max( g_vFieldMax - g_vFieldMin, 1e-4 );
			float3 uvw = (t * (g_vFieldDims - 1.0) + 0.5) / g_vFieldDims; // sampler CLAMP guards t outside [0,1]
			return g_tField.SampleLevel( g_sField, uvw, 0 ).r;
		}

		// Sparse path (two-phase, GigaVoxels-style). g_tField here is a LOW-RES guide field (empty-space distance),
		// while the surface detail lives in the high-res atlas. Most march steps are far from the surface: sample the
		// cheap guide and take a big step with NO brick lookup. Only once within ~2 bricks of the surface do we look
		// up the point's brick — occupied → its high-res atlas tile (the real surface); empty → keep stepping on the
		// guide. The brick grid is sized by g_vSurfaceDims (high), independent of the guide texture's resolution.
		float SampleFieldSparse( float3 lp )
		{
			float guide = SampleFieldTex( lp ); // low-res guide (g_tField); one trilinear fetch, like the dense path
			float3 scell = (g_vFieldMax - g_vFieldMin) / max( g_vSurfaceDims - 1.0, 1.0 );
			float  nearBand = 2.0 * SDF_BLOCK * max( scell.x, max( scell.y, scell.z ) ); // ~2 bricks, world units
			if ( guide > nearBand )
				return guide;                   // far from the surface: cheap guide step, no brick lookup

			// Near the surface: locate the brick on the HIGH-RES grid and consult the atlas.
			float3 fv    = (lp - g_vFieldMin) / scell;     // continuous surface-voxel coordinate
			int3   bd    = (int3)( g_vBrickDims + 0.5 );
			int3   brick = (int3)floor( fv / SDF_BLOCK );
			if ( any( brick < 0 ) || any( brick >= bd ) )
				return guide;

			float ind = g_tIndirection.Load( int3( brick.x, brick.y + bd.y * brick.z, 0 ) ).r;
			float maxTiles = (g_vAtlasDims.x / SDF_TILESIZE) * (g_vAtlasDims.y / SDF_TILESIZE) * (g_vAtlasDims.z / SDF_TILESIZE);
			if ( !(ind >= 0.0 && ind < maxTiles) )
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

	// --- Plasticine displacement noise: hash13 / vnoise / BoilOffset come from sdf_eval.hlsl now
	// (shared with the field-bake compute shaders, which bake these same lumps into the volume —
	// the LIVE path below is only the analytic fallback while no baked field is bound). ---

	// Per-tick offset for the triplanar surface maps, returned in MODEL-SPACE INCHES.
	//
	// Expressed in texture REPEATS and converted here, so the dial is independent of g_flTriTile —
	// Tex2DTriplanar scales position by tile/39.3701, so a fixed inch offset would mean a totally
	// different amount of detail change at tile 2 vs tile 32. Same reasoning as the displacement
	// boil living in noise cells.
	//
	// The two regimes are worth knowing, because they look different: well under one repeat the
	// texture SLIDES (detail keeps its identity and crawls), around/above one repeat it lands on
	// uncorrelated texels and the detail genuinely RE-FORMS. Re-forming is the honest claymation
	// read — the animator's fingers really do land somewhere new between frames — but at 12 ticks a
	// second it also fizzes and gives TAA nothing to track. The default sits in between.
	float3 BoilTexOffset()
	{
		if ( g_flBoilFps <= 0.0 || g_flBoilTexJitter <= 0.0 )
			return 0.0;

		float tick = g_flBoilTick + g_flBoilSeed;
		// Salted away from the lump offsets so the fine detail re-forms on its own schedule instead
		// of sliding in lockstep with the silhouette (which reads as the whole prop swimming).
		return BoilOffset( tick + 307.0 ) * g_flBoilTexJitter * ( 39.3701 / max( g_flTriTile, 0.001 ) );
	}

	// STATIC per-instance offset for the same maps, also in model-space inches: identical models (every
	// hunter pawn) otherwise project identical texels, so they all wear the same dark patch in the same
	// place. Spread over 16 texture repeats via frac() of the per-object seed against the R3 irrationals —
	// whole repeats apart means fully uncorrelated texels, and unlike the boil jitter this never animates.
	// Seed 0 (nothing pushed the attribute: thumbnails, menu head) keeps the authored placement.
	// Mirrored in sdf_mesh.shader — keep the two in sync.
	float3 SeedTexOffset()
	{
		return frac( g_flBoilSeed * float3( 0.8191725134, 0.6710436067, 0.5497004779 ) )
		     * 16.0 * ( 39.3701 / max( g_flTriTile, 0.001 ) );
	}

	// Signed displacement (centred on 0): big lump + a half-scale octave for an uneven, hand-pressed
	// feel. Deliberately LOW frequency — fine grain is left to the triplanar normal map, and fine
	// displacement would just alias against the SdfNormal finite-difference epsilon (0.05) anyway.
	// Takes the LOCAL point (like the triplanar surface), so the lumps are locked to the prop and
	// move/rotate with it instead of the prop sliding through a fixed world-space noise field. The
	// transform is rigid, so the field's gradient magnitude (the L safety factor below) is unchanged.
	//
	// CLAYMATION BOIL: the sample point is nudged by a new random offset each TICK (12/sec = animating
	// "on twos" at 24fps), so the lumps re-form in discrete steps like a re-pressed clay model instead
	// of sitting perfectly still. Three things make it read right rather than read as a bug:
	//
	//  * floor() of a pure g_flTime expression, so the tick is IDENTICAL in the depth prepass, the
	//    colour pass, every shadow view and sdf_highlight within one frame. That agreement is not
	//    optional — SdfNormal finite-differences this same field and the shadow views re-march it, so
	//    a per-pass mismatch cracks the silhouette and floats the shadow off the prop.
	//  * the offset is added AFTER the freq multiply (i.e. in noise-cell units), so retuning DispFreq
	//    doesn't silently retune the boil strength.
	//  * the two octaves get DIFFERENT offsets. A shared offset translates the whole lump field
	//    sideways, which reads as the prop swimming; separate offsets make the lumps re-form in place.
	//
	// BoilFps 0 makes this an exact no-op (both offsets 0, amp untouched) — identical to boil off.
	//
	// HOISTED PER PIXEL: the boil terms below (b0/b1/amp) derive ONLY from uniforms — g_flTime, the four
	// Boil* attributes and g_flDispAmp — so they are identical for every one of the ~150 SdfDistWs calls a
	// single pixel makes (the march loop, SdfNormal's 6 taps, SdfCurvature's 5, SdfAO's 5, the transmission
	// probe and its march). Recomputing them per sample cost 7 of the 23 hash13 evaluations each sample
	// paid for. They live in the statics below, filled once by SetupDisplacement.
	//
	// Deliberately still computed IN THE SHADER from g_flTime rather than pushed from C#: the tick must be
	// bit-identical in the depth prepass, the colour pass, every shadow view AND sdf_highlight within one
	// frame (see the note above — a mismatch cracks the silhouette and floats the shadow off the prop), and
	// a CPU-side hash13 reproduction risks diverging from the GPU's by an ULP. Hoisting keeps one source of
	// truth while still paying for it once.
	static float3 s_boilB0    = 0.0;
	static float3 s_boilB1    = 0.0;
	static float  s_dispAmp   = 0.0; // this tick's amp, wobble applied
	static float  s_dispBound = 0.0; // worst-case |Displacement| — the conservative bound SdfDistMarchWs skips with
	static bool   s_dispOn    = false;

	// Fill the statics above. MUST run before anything marches or shades; RayMarchHit calls it as soon as
	// it knows the projection, which is ahead of both its own loop and all of the shading taps.
	//
	// ORTHO (sun shadow cascade) views switch displacement OFF unless g_nDispShadows says otherwise. The
	// lumps are at most g_flDispAmp (~0.8 units) of silhouette wobble, which is usually invisible in a
	// cast shadow, but ortho views march at FULL quality by definition (LodQuality is meaningless there,
	// so q is forced to 1) — so they pay the per-step noise at the maximum step count of any view in the
	// frame. Off there also drops the understep and the budget growth in RayMarchHit, which compound.
	// Per-prop, because whether a lumpy shadow edge is worth that is a per-prop judgement: a hero prop
	// throwing a big sharp shadow onto a flat floor can be worth it, a pawn's own body is not.
	void SetupDisplacement( bool ortho )
	{
		s_boilB0 = 0.0;
		s_boilB1 = 0.0;
		s_dispAmp = 0.0;
		s_dispBound = 0.0;
		s_dispOn = !ortho || g_nDispShadows != 0;

		if ( !s_dispOn )
			return;

		float amp = g_flDispAmp;

		if ( g_flBoilFps > 0.0 )
		{
			// The tick comes from the CPU (ClayBoil.TickAt): the GLOBAL clock normally — real
			// stop-motion advances every model on the same frame; only the seed is per-prop, so a
			// room full of props boils together but not identically — or an off-grid impact pose
			// while a shot's jolt plays. Already wrapped to 1024 ticks CPU-side (hash13's frac()
			// runs out of float32 mantissa on an unwrapped tick in a long match — the boil would
			// quietly slow down and then freeze).
			float tick = g_flBoilTick + g_flBoilSeed;
			s_boilB0 = BoilOffset( tick )         * g_flBoilJitter;
			s_boilB1 = BoilOffset( tick + 101.0 ) * g_flBoilJitter * 2.7; // fine octave rolls harder
			// Per-tick lump-depth wobble: the surface reading slightly deeper/shallower frame to
			// frame is the cheaper half of the effect. Bounded by BoilAmpJitter — the step-safety L
			// below uses that bound, not this instantaneous value.
			amp *= 1.0 + ( hash13( float3( tick, 7.77, 1.23 ) ) - 0.5 ) * g_flBoilAmpJitter;
		}

		s_dispAmp = amp;
		// The BOUND, not this tick's value: sized from the same worst case L uses, so it holds for every
		// tick and never has to be recomputed when the boil rolls.
		s_dispBound = g_flDispAmp * ( 1.0 + max( g_flBoilAmpJitter, 0.0 ) * 0.5 );
	}

	float Displacement( float3 lp )
	{
		float3 x = lp * g_flDispFreq;
		float n = vnoise( x + s_boilB0 ) * 0.67 + vnoise( x * 2.03 + 11.7 + s_boilB1 ) * 0.33; // ~[0,1]
		return (n * 2.0 - 1.0) * s_dispAmp;
	}

	// Combined field at WORLD point p: fold once into the prop's LOCAL frame — where the brushes are
	// packed and the field is baked — and evaluate there. The transform is rigid (unit scale), so the
	// local distance IS the world distance and sphere-trace steps along the world ray stay valid. The
	// analytic loop is the shared sdf_eval.hlsl SdfDist — the same code the field baker dispatches.
	// The UNDISPLACED field, plus the local point (so callers needing the noise don't fold twice).
	float SdfRawWs( float3 p, out float3 lp )
	{
		lp = WorldToModelPos( p );
	#if ( D_FIELD_TEX )
		// Cached path: one trilinear fetch of the baked field instead of the per-brush loop. Sparse-atlas vs dense
		// volume is a RUNTIME branch (g_nSdfSparse), not a combo, to avoid a 7th DynamicCombo.
		return g_nSdfSparse != 0 ? SampleFieldSparse( lp ) : SampleFieldTex( lp );
	#else
		return SdfDist( lp );
	#endif
	}

	float SdfDistWs( float3 p )
	{
		float3 lp;
		float d = SdfRawWs( p, lp );
	#if ( D_DISPLACE )
		// Subtracting noise along the field pushes the whole iso-surface in/out -> bumpy silhouette.
		// SdfNormal differences this same field, so shading + the depth-prepass normal track the lumps.
		if ( s_dispOn )
			d -= Displacement( lp );
	#endif
		return d;
	}

	// MARCH-ONLY distance. Same surface as SdfDistWs, far cheaper away from it.
	//
	// Displacement can move the iso-surface by at most +/-s_dispBound, so once a sample is comfortably
	// outside that band, subtracting the BOUND is still a valid sphere-trace distance — it can only ever
	// UNDER-estimate (s_dispBound >= Displacement() everywhere), which is the safe direction — and the
	// noise can be skipped entirely. On a sphere trace most steps are the long approach through empty
	// space, so this is where the bulk of the ~400 ALU/sample went.
	//
	// The traced surface is unchanged. The skip branch can only fire when d > 2*s_dispBound, which leaves
	// d - s_dispBound > s_dispBound — orders of magnitude above the hit epsilon (0.05 vs ~1.2), so it can
	// never register a hit or step past one. Only the march uses this: SdfNormal, SdfCurvature, SdfAO and
	// the transmission taps all still go through the exact SdfDistWs above, so shading is bit-identical.
	float SdfDistMarchWs( float3 p )
	{
		float3 lp;
		float d = SdfRawWs( p, lp );
	#if ( D_DISPLACE )
		if ( s_dispOn )
			d -= ( d > s_dispBound * 2.0 ) ? s_dispBound : Displacement( lp );
	#endif
		return d;
	}

	struct SdfSurface { float3 col; float metal; float rough; };

	// Re-runs the union at the WORLD hit point (folded local once, like SdfDistWs), blending colour AND
	// per-brush metalness/roughness by the same smooth-min factor as the geometry, so merged brushes share
	// smooth material seams instead of a hard nearest-brush boundary (a sharp specular edge reads far
	// worse than a colour edge). Runs ONCE per pixel at the hit, so it always walks the analytic brushes
	// (sdf_eval.hlsl's BrushDist) even on the D_FIELD_TEX path — the field has no colour.
	SdfSurface SdfShade( float3 p )
	{
		float3 lp = WorldToModelPos( p );
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
			// Same early-out as the shared SdfDist: a far brush can't change the blended distance/colour
			// here either. Add: a brush only matters within blend of becoming the new nearest (bd >= d+k).
			// Subtract: a carver only matters while material is within blend. One threshold covers both:
			// k+d for add, k-d for subtract (a single k+d over-culls subtracts where d is negative).
			if ( g_nSdfCull != 0 && sdAabb( lp, F.xyz, G.xyz ) > (D.w < 0.5 ? d + B.w : B.w - d) )
				continue;

			float bd = BrushDist( lp, A, B, C, E.x, (int)(E.w + 0.5), (int)(F.w + 0.5), G.w );

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
			SdfDistWs( p + float3( e, 0, 0 ) ) - SdfDistWs( p - float3( e, 0, 0 ) ),
			SdfDistWs( p + float3( 0, e, 0 ) ) - SdfDistWs( p - float3( 0, e, 0 ) ),
			SdfDistWs( p + float3( 0, 0, e ) ) - SdfDistWs( p - float3( 0, 0, e ) ) );
		// Guard the gradient: at grazing/silhouette hits the central difference can collapse to ~0, and
		// normalize(0) is NaN. A NaN here lands in the SHARED depth+normals prepass and poisons every
		// screen-space pass that samples it (noise over all SDFs at once). Fall back to facing the camera.
		float l = length( g );
		return l > 1e-6 ? g / l : float3( 0, 0, 1 );
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
		float t1 = SdfDistWs( p + k.xyy * e );
		float t2 = SdfDistWs( p + k.yyx * e );
		float t3 = SdfDistWs( p + k.yxy * e );
		float t4 = SdfDistWs( p + k.xxx * e );
		return (t1 + t2 + t3 + t4 - 4.0 * SdfDistWs( p )) / e;
	}

	// 5-tap distance-field ambient occlusion (the classic ladder): step outward along the normal and
	// compare how much free space the field reports against how far we walked — the shortfall, weighted
	// toward the near taps, is occlusion. Exact against the object's own geometry and fully screen-space
	// independent, so it carries the VIEWMODEL's self-occlusion: screen AO only sees the gun's smooth
	// outer shell in the depth buffer, this reads the crevices the field actually has.
	// Tap heights (0.8..4 units) suit gun-scale crevices. 5 field samples, same cost family as curvature.
	float SdfAO( float3 p, float3 n )
	{
		float occ = 0.0;
		float sca = 1.0;
		[unroll]
		for ( int k2 = 1; k2 <= 5; k2++ )
		{
			float h = 0.8 * k2;
			occ += max( h - SdfDistWs( p + n * h ), 0.0 ) / h * sca;
			sca *= 0.6;
		}
		return saturate( 1.0 - 0.55 * occ ); // occ tops out ~2.3 (the weight sum) -> full enclosure clamps to 0
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
		return clamp( -SdfDistWs( p - N * g_flTransProbe ), 0.0, g_flTransProbe );
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
			if ( SdfDistWs( p + L * t ) < 0.0 )
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

	// p is the raw world hit (the frame SdfDistWs marches in); m.WorldPosition / m.ScreenPosition are
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

		// Fill the hoisted displacement statics for this pixel (and switch the noise off entirely in ortho
		// shadow views). Must precede the march AND the shading taps that follow the hit, both of which
		// read them — this is the earliest point where the projection is known.
		SetupDisplacement( ortho );

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

		// Bracket the march: a tight bounding sphere (round props) or the bounds AABB. Runtime-branched
		// (uniform per draw) — this was the D_TIGHT_BOUNDS combo.
		float tEnter, tExit;
		if ( g_nSdfTightBounds != 0 )
		{
			float3 oc = ro - g_vBoundsCenter;
			float bb = dot( oc, rd );
			float cc = dot( oc, oc ) - g_flBoundsRadius * g_flBoundsRadius;
			float disc = bb * bb - cc;
			if ( disc < 0.0 )
				return false;
			float sq = sqrt( disc );
			tEnter = max( -bb - sq, 0.0 );
			tExit = -bb + sq;
		}
		else
		{
			float3 t0 = (g_vBoundsMin - ro) / rd;
			float3 t1 = (g_vBoundsMax - ro) / rd;
			float3 tsmall = min( t0, t1 );
			float3 tbig = max( t0, t1 );
			tEnter = max( max( max( tsmall.x, tsmall.y ), tsmall.z ), 0.0 );
			tExit = min( min( tbig.x, tbig.y ), tbig.z );
		}

		if ( tExit < tEnter )
			return false;

	#if ( S_MODE_DEPTH == 0 )
		// Scene-depth occlusion clamp — FORWARD pass only (S_MODE_DEPTH covers the camera prepass,
		// where the chain is still being written this frame, and every shadow view, where the bound
		// depth belongs to a different projection). Writing SV_Depth disables early-Z, so the depth
		// test runs AFTER this shader: without this clamp a prop fully hidden behind a wall — or
		// behind another SDF's overlapping proxy — still pays its whole march per covered pixel, and
		// a MISS ray (the proxy area around the silhouette) marches the full box even when everything
		// past the first occluder is invisible. The prepass depth already contains every opaque
		// surface INCLUDING our own, so clamp the bracket to it; the slack keeps our own re-marched
		// surface (prepass<->forward hit spread — see the depth nudge before MainPs' output) on the
		// reachable side. Skipped for the viewmodel: its rays belong to the gun's warped projection,
		// not this pixel's scene ray, and its pixels are stencil-claimed by the overlay prepass anyway.
		if ( g_nSdfDepthCull != 0 && !ortho && g_nSdfViewmodel == 0 )
		{
			float3 sceneWp = Depth::GetWorldPosition( i.vPositionSs.xy );
			float tScene = dot( sceneWp - ro, rd ) + 2.0;
			if ( tEnter > tScene )
				return false;
			tExit = min( tExit, tScene );
		}
	#endif

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

		// Baked displacement: the lumps were subtracted INTO this field by the baker, inflating its
		// slope by up to g_flDispGradL — understep by 1/(1+L) and grow the budget by the same, exactly
		// the insurance the live D_DISPLACE block below applies to the analytic path. Unlike that
		// block this runs in EVERY view, ortho shadow cascades included: the lumps are in the texture,
		// so there is no per-view way to march the smooth surface (the shadow silhouette now always
		// matches the camera one). 0 (field baked without displacement) is an exact no-op.
		if ( g_flDispGradL > 0.0 )
		{
			stepScale *= 1.0 / (1.0 + g_flDispGradL);
			maxSteps  = (int)( maxSteps * min( 1.0 + g_flDispGradL, 2.0 ) );
		}
	#endif
	#if ( D_DISPLACE )
		// A displaced field is no longer a true distance field: subtracting the noise inflates its
		// gradient by ~L, so a full sphere-trace step can overshoot the surface. Understep by 1/(1+L)
		// AND grow the step budget by the same (1+L) so grazing/silhouette rays still converge instead
		// of running out of steps (= edge misses). L ~= Amp*Freq*4 (the noise's max slope), so at
		// Amp=0 this is exactly a no-op: stepScale 1, budget unchanged -> identical to displace OFF.
		// The boil's per-tick amp wobble can push amp above g_flDispAmp, so size L from the WORST
		// case (+half of BoilAmpJitter) — a per-tick L would understep on the ticks that wobble up,
		// which shows as edge misses that flicker in time with the boil.
		//
		// Skipped entirely when SetupDisplacement turned the noise off (ortho shadow views): with no
		// displacement the field is a true distance field again, so the understep and the extra budget
		// would both be pure waste on top of the full step count those views already march at.
		if ( s_dispOn )
		{
			float L   = s_dispBound * g_flDispFreq * 4.0;
			stepScale *= 1.0 / (1.0 + L);
			maxSteps  = (int)( maxSteps * min( 1.0 + L, 2.0 ) ); // cap the budget growth so cost stays bounded
		}
	#endif

		float t = tEnter;
		[loop]
		for ( int s = 0; s < maxSteps; s++ )
		{
			float d = SdfDistMarchWs( ro + rd * t );
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
	// (seen as background punching through the prop). Inter-object occlusion rides the engine depth
	// chain via the prepass instead — no early-Z promise needed.
	struct SdfPixelOutput { float4 vColor : SV_Target0; float flDepth : SV_Depth; };

	SdfPixelOutput MainPs( PixelInput i, bool isFrontFace : SV_IsFrontFace )
	{
		SdfPixelOutput o;
		o.vColor = float4( 0, 0, 0, 0 );
		o.flDepth = 1.0;

	#if ( S_TRANSLUCENT && S_MODE_DEPTH )
		// Translucent (viewmodel) variant: never in the depth prepass or shadow views — the whole point
		// is being invisible to the screen-space passes (AO/DoF/contact shadows) that read them.
		clip( -1 );
		return o;
	#endif

		// March BACK faces only. Both faces of the proxy box march the identical ray, so culling
		// one halves the work; back faces are the robust pick because they exist from any camera
		// position (front faces near-clip away when the camera enters the box). No inside/outside
		// special case needed. Runtime-toggled (was the D_OVERDRAW_OPT combo).
		if ( g_nSdfOverdrawOpt != 0 && isFrontFace )
		{
			clip( -1 );
			return o;
		}

		// RenderHidden pawn body: render ONLY into ortho (sun-shadow) views. Every perspective view —
		// camera forward, camera depth prepass, spot-light shadows — is clipped, so the hidden body
		// can't occlude or appear anywhere; only its sun shadow survives (ModelRenderer.ShadowsOnly
		// parity for the raymarched surface).
		if ( g_nSdfShadowOnly != 0 && !IsOrthoView() )
		{
			clip( -1 );
			return o;
		}

		// Viewmodel FOV: undo the VS's lateral warp on this pixel's interpolated position. The recovered
		// point is the one whose GUN-projection lands on this pixel, so the ray built through it (in
		// RayMarchHit, and the depth nudge below) IS the gun-FOV ray — marched in plain world space, so
		// the field, normals, AO and lighting all stay exactly as they are. Affine warp + affine
		// interpolation → the round trip is exact.
		if ( g_nSdfViewmodel != 0 && g_flSdfVmFovScale > 0.001 )
		{
			float3 fwd = normalize( g_vCameraDirWs );
			float3 rel = i.vPositionWithOffsetWs + ( g_vHighPrecisionLightingOffsetWs.xyz - g_vCameraPositionWs );
			float axial = dot( rel, fwd );
			rel = fwd * axial + ( rel - fwd * axial ) / g_flSdfVmFovScale;
			i.vPositionWithOffsetWs = rel - ( g_vHighPrecisionLightingOffsetWs.xyz - g_vCameraPositionWs );
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
		// engine depth buffer (so depth sorting + occlusion work via the engine chain).
		// This same mode also renders the SHADOW views: in an ortho view (a sun cascade) the written
		// depth is the shadow map. The optional caster bias pushes it a touch deeper along the light
		// ray there — acne insurance the camera prepass must never get (its depth must stay exact,
		// the forward pass depth-tests against it).
		float3 pd = p;
		if ( IsOrthoView() )
			pd += normalize( g_vCameraDirWs ) * g_flSdfShadowBias;
		// Viewmodel: REAL depth, on purpose. This Depth pass runs in the engine's dedicated OVERLAY
		// prepass (GameOverlayLayer flag), which runs before the world prepasses and stencil-claims
		// these pixels (bit 0x80) — nearer world depth can't overwrite them, so the forward overlay
		// pass wins here at true depth and the gun still never clips into geometry. Real depth also
		// keeps the depth chain honest for its consumers: screen UI (which depth-tests, and which the
		// old near-camera squash used to lose against), contact shadows and screen AO.
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

		// Viewmodel: no special depth — the ordinary nudged depth above already wins. The overlay
		// prepass stencil-claimed these pixels at the gun's real depth, world prepasses couldn't
		// overwrite it, so forward's scene-depth test compares against our OWN prepass value and the
		// nudge keeps us on the winning side, same as the normal path. (The old 98%-to-camera squash
		// that lived here drew over walls too, but its near-bound depth also beat the screen UI's
		// depth test — the gun rendered on top of the HUD.)

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
		// The boil offset goes on the SHARED sample point, so albedo, roughness and normal all shift
		// together and stay registered — a plasticine imperfection is a colour AND a shape, and
		// jittering only the normal map would peel the fingerprint off its own smudge.
		// modelP feeds nothing but these three samples (SdfShade/SdfAO/SdfCurvature all work from
		// world p), so this can't disturb the geometry.
		float3 modelP = WorldToModelPos( p ) + BoilTexOffset() + SeedTexOffset();
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
		m.Albedo = BoostDarkSat( m.Albedo, albedo, g_flDarkSatBoost );
		m.Roughness = max( saturate( roughness * g_flRoughness * surf.rough ), 0.08 );
		m.Metalness = saturate( surf.metal + g_flMetalness );

		// Viewmodel: self-AO computed from the field. The engine min()s material AO with screen AO —
		// now that the prepass writes real depth, screen AO contributes real world occlusion too, and
		// this field term carries the crevice detail the depth buffer's outer shell can't show.
		if ( g_nSdfViewmodel != 0 )
			m.AmbientOcclusion = SdfAO( p, baseN );

		// Curvature → diffuse: darken crevices (seam grime), lift ridges (worn edges) — clay reads its
		// shape through these cues even under flat light. Albedo only, so lighting/spec stay physical.
		// Branched away entirely when both strengths are zero (saves 5 field taps).
		if ( g_flCurveDark + g_flCurveLight > 1e-3 )
		{
			float curv = SdfCurvature( p, max( g_flCurveRadius, 0.05 ) );
			float cavity = g_flCurveDark * saturate( -curv );
			float shade = (1.0 - cavity) * (1.0 + g_flCurveLight * saturate( curv ));
			m.Albedo = saturate( m.Albedo * shade );
			// Give the crevice shade back its colour: boost scales with how much darkening was applied,
			// so ridges and flats are untouched.
			m.Albedo = BoostSat( m.Albedo, lerp( 1.0, g_flCurveSatBoost, cavity ) );
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
