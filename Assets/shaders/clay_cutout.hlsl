//=========================================================================================================================
// Camera-occlusion cutout (the Baldur's Gate 3 "peek hole") — WORLD-SPACE version: a noise-eroded 3D tunnel
// (capsule) carved along the camera -> prop sight line, discarded from any opaque fragment nearer the camera
// than the prop. World-space is what makes it read as a physical opening rather than a screen decal (see
// Brendan Sullivan's BG3 breakdown): the dissolve noise is sampled at the fragment's WORLD position, so the
// pattern is glued to the wall and parallaxes correctly, and the hole has real thickness at the cut edge.
// Unlike his single spherecast-anchored sphere, the capsule needs no physics trace and no update delay — the
// shader measures distance to the sight segment directly — and it handles several occluders along the line.
//
// Driven per frame by code/Rendering/ClayCutout.cs via Scene.RenderAttributes (global — binds in ALL passes):
//   ClayCutoutHole  xyz = prop centre WS               w = tunnel radius in world units (<= 0 disables; eased
//                                                          open/shut by the driver, so the hole GROWS in)
//   ClayCutoutCam   xyz = the GAME camera's world pos  w = cut camera-distance: only fragments RADIALLY nearer
//                                                          than this get discarded (computed from the FULL prop
//                                                          radius, never the eased one — an easing-in radius
//                                                          would otherwise punch a pinhole in the prop's face)
//
// Include from a PS block, AFTER the common includes (needs g_vCameraPositionWs), then at the TOP of MainPs:
//     float cutRim;
//     if ( ClayCutoutHit( i.vPositionSs.xy, worldPos, normalWs, cutRim ) ) clip( -1.0 );
//     ... later, forward pass: m.Albedo *= 1.0 - cutRim * 0.35;   // darkened cross-section rim
// ClayCutoutHit is deliberately a PURE TEST with the clip() left to the caller: a discard buried inside a
// nested helper has ICE'd DXC before (0x80004005, no diagnostic — see shader-compile-debugging notes).
// Two invariants (see the camera-occlusion-cutout notes):
//   1. The discard must run in the depth prepass too — declare Depth( S_MODE_DEPTH ) so this same PS compiles
//      into it. A hole in colour but not in prepass depth still occludes everything it should reveal.
//   2. Shadow / reflection / probe views must NOT cut (light leaks through the hole). There is no engine
//      "is shadow pass" define, so we gate on the per-view camera position differing from the game camera's.
//=========================================================================================================================
#ifndef CLAY_CUTOUT_HLSL
#define CLAY_CUTOUT_HLSL

float4 g_vClayCutoutHole < Attribute( "ClayCutoutHole" ); Default4( 0.0, 0.0, 0.0, -1.0 ); >;
float4 g_vClayCutoutCam  < Attribute( "ClayCutoutCam" );  Default4( 0.0, 0.0, 0.0, 0.0 ); >;

// Style knobs, published by ClayCutout.cs from the ClayCutoutSettings component (defaults mirror it — an
// unset attribute behaves identically): x/y = the two noise octave scales, ALREADY chosen for the active
// mode by the driver (1/world-units in 3D, 1/pixels in 2D — so the shader uses them the same way in both),
// z = erosion depth (how raggedly the noise eats the edge; 0 = clean cylinder/circle), w = rim band width
// as a fraction of the radius.
float4 g_vClayCutoutStyle < Attribute( "ClayCutoutStyle" ); Default4( 0.15, 0.45, 0.45, 0.15 ); >;
// Mode switch (w): 0 = 3D tunnel (constant world radius — near walls open wider, the perspective cue),
// 1 = 2D disc (radius grows linearly from the camera end — a CONE, so every occluder shows the same
// screen-size hole). Both modes share the same lagged world segment and world-anchored noise.
// x = 2D orbit noise drive (fraction of segment length): slides the noise plane's coordinates along the
// sight AXIS, whose direction rotates at orbit rate — orbit churn in 2D is otherwise only hole-radius ×
// orbit rate (the plane's centre IS the pivot), far slower than 3D's wall-distance × orbit rate. Walking
// keeps the axis direction constant, so this adds nothing there.
// y = outline width as a fraction of the radius (<= 0 = no outline; colour in ClayCutoutOutline).
// z = depth-taper width as a fraction of the radius (how fast the hole pinches shut at the depth boundary).
float4 g_vClayCutoutScreen < Attribute( "ClayCutoutScreen" ); Default4( 0.5, 0.0, 0.25, 0.0 ); >;
// Ground guard: x = on/off, y = height slack as a fraction of the radius, z = "counts as floor" normal Z.
// w = edge feather width as a fraction of the radius: the band inside the eroded edge where the cut fades
// in by DITHER (clip is binary — an alpha fade can't hole the depth prepass). 0 = hard 1px edge.
float4 g_vClayCutoutGuard < Attribute( "ClayCutoutGuard" ); Default4( 1.0, 0.5, 0.7, 0.12 ); >;
// LAGGED camera position — the tunnel's origin end. Separate from ClayCutoutCam (which is the TRUE camera,
// and must stay true: it's what the secondary-view gate below compares against, and fast motion pushes the
// lagged position far enough away to trip that gate's 4-unit tolerance). Lagging BOTH ends is what makes the
// lag visible while orbiting — the prop centre barely moves in world space there, only the camera does.
// w = 3D tunnel cone spread, as tan(half-angle): widens the tunnel toward the CAMERA end by this much per
// world unit back from the prop (0 = plain cylinder). 3D mode only — 2D's cone comes from its own radius law.
float4 g_vClayCutoutOrigin < Attribute( "ClayCutoutOrigin" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
// Albedo darkening at the rim band (the clay cross-section) — read by the consumer shaders, not the test.
float g_flClayCutoutRimDarken < Attribute( "ClayCutoutRimDarken" ); Default( 0.35 ); >;
// Outline drawn along the eroded edge: rgb = colour, a = opacity. Width rides ClayCutoutScreen.y (as a
// fraction of the radius, like the rim; <= 0 disables). Consumers lerp their albedo toward the colour by
// the outline weight ClayCutoutHit hands back.
float4 g_vClayCutoutOutline < Attribute( "ClayCutoutOutline" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
// PER-RENDERER exemption (object attributes override the scene default): the local hider stamps 1 onto its
// own disguise's renderers every frame, so the prop is protected by IDENTITY — never cut, no matter how the
// lag sweeps or how small the cut margin gets. This is what frees CutSlack to be purely a scenery knob.
int g_nClayCutoutExempt < Attribute( "ClayCutoutExempt" ); Default( 0 ); >;

// Random unit-ish gradient per lattice point (sin-hash). The rsqrt guard keeps a pathological
// near-zero hash from turning into a NaN that clip() would smear.
float3 ClayCutoutGrad( float3 p )
{
	float3 g = frac( sin( float3(
		dot( p, float3( 127.1, 311.7,  74.7 ) ),
		dot( p, float3( 269.5, 183.3, 246.1 ) ),
		dot( p, float3( 113.5, 271.9, 124.6 ) ) ) ) * 43758.5453 ) * 2.0 - 1.0;
	return g * rsqrt( dot( g, g ) + 1e-6 );
}

// Interleaved gradient noise (Jimenez) — the per-pixel threshold for the dithered edge feather. Stable for
// a given pixel, so the depth prepass and the forward pass (same SV_Position) always agree on the discard.
float ClayCutoutDither( float2 px )
{
	return frac( 52.9829189 * frac( 0.06711056 * px.x + 0.00583715 * px.y ) );
}

// Smooth 3D GRADIENT (Perlin) noise, sampled at WORLD position — the dissolve pattern sticks to the
// geometry it's eating (the core of the "physical hole" read), and the churn as the hole edge crawls over
// it comes from the capsule moving, not the pattern. Gradient noise specifically, not value noise: value
// noise pins random VALUES at lattice points, so its iso-contours kink at cell walls and clump along the
// grid — thresholded into a cut edge, every kink is a sharp corner. Gradient noise is zero at every
// lattice point with near-isotropic contours, so the same threshold traces rounded blobs.
float ClayCutoutNoise( float3 p )
{
	float3 i = floor( p ), f = frac( p );
	// Quintic fade (Perlin's improved curve): C2-continuous across cell boundaries — the cubic 3-2f fade
	// has gradient breaks at every lattice plane, which the clip threshold traces as creases.
	float3 u = f * f * f * ( f * ( f * 6.0 - 15.0 ) + 10.0 );

	float n000 = dot( ClayCutoutGrad( i ),                      f );
	float n100 = dot( ClayCutoutGrad( i + float3( 1, 0, 0 ) ), f - float3( 1, 0, 0 ) );
	float n010 = dot( ClayCutoutGrad( i + float3( 0, 1, 0 ) ), f - float3( 0, 1, 0 ) );
	float n110 = dot( ClayCutoutGrad( i + float3( 1, 1, 0 ) ), f - float3( 1, 1, 0 ) );
	float n001 = dot( ClayCutoutGrad( i + float3( 0, 0, 1 ) ), f - float3( 0, 0, 1 ) );
	float n101 = dot( ClayCutoutGrad( i + float3( 1, 0, 1 ) ), f - float3( 1, 0, 1 ) );
	float n011 = dot( ClayCutoutGrad( i + float3( 0, 1, 1 ) ), f - float3( 0, 1, 1 ) );
	float n111 = dot( ClayCutoutGrad( i + float3( 1, 1, 1 ) ), f - float3( 1, 1, 1 ) );

	float n = lerp( lerp( lerp( n000, n100, u.x ), lerp( n010, n110, u.x ), u.y ),
	                lerp( lerp( n001, n101, u.x ), lerp( n011, n111, u.x ), u.y ), u.z );
	// Perlin comes out roughly ±0.8 — remap into [0,1] so the erosion threshold maths is unchanged.
	return saturate( n * 0.65 + 0.5 );
}

// Pure test: should the fragment at screen pixel px (SV_Position.xy) and ABSOLUTE world position worldPos,
// with (geometric) world normal nWs, be discarded? Two band weights come back for surviving fragments:
// rim — 1 at the eroded edge fading to 0 over the rim band, multiplied into albedo (the darkened clay
// cross-section); outline — the ClayCutoutOutline colour band hugging the edge, lerp the albedo toward the
// colour by it (opacity already folded in). Every gate up to the mode switch is shared by both modes.
bool ClayCutoutHit( float2 px, float3 worldPos, float3 nWs, out float rim, out float outline )
{
	rim = 0.0;
	outline = 0.0;

	float radius = g_vClayCutoutHole.w;
	if ( radius <= 0.0 )
		return false; // disabled — the common case, so it's the first test

	if ( g_nClayCutoutExempt != 0 )
		return false; // this renderer IS the prop (or opted out) — identity protection, distance-free

	// Secondary-view gate: shadow, reflection and probe views render with their OWN per-view camera position;
	// only the real game view sits (within slop) at the position the driver stamped. Cutting a shadow view
	// leaks light through walls.
	if ( distance( g_vCameraPositionWs, g_vClayCutoutCam.xyz ) > 4.0 )
		return false;

	// Ground guard (Sullivan separates walkable ground the same way): an up-facing fragment at or below the
	// prop's own level is floor the prop is standing on / the camera skims — dissolving it reads as the
	// world falling away. Up-facing geometry ABOVE the prop (an upstairs floor between camera and prop)
	// still cuts. Ceilings (down-facing) always cut. Turn the whole guard off with ClayCutoutGuard.x = 0.
	if ( g_vClayCutoutGuard.x > 0.5 && nWs.z > g_vClayCutoutGuard.z
	     && worldPos.z < g_vClayCutoutHole.z + radius * g_vClayCutoutGuard.y )
		return false;

	// The lagged sight segment — SHARED by both modes, which is what makes them behave identically under
	// lag and orbit: same trailing endpoints, same world-anchored noise crawling over the geometry. BOTH
	// endpoints are the LAGGED ones (origin, not the true camera) so the whole tunnel trails.
	float3 origin = g_vClayCutoutOrigin.xyz;
	float3 prop = g_vClayCutoutHole.xyz;
	float segLen = distance( prop, origin );
	float3 axis = ( prop - origin ) / max( segLen, 0.001 );
	float along = dot( worldPos - origin, axis );

	// Depth gate, PER-RAY (Thales): a fragment is cut only while its own view ray is still APPROACHING the
	// prop — equivalently, while it lies inside the sphere whose DIAMETER runs camera -> boundary point
	// (one dot product). This is the third gate design and the keeper: geometry behind the prop is past its
	// rays' closest approach, so it never cuts (the cube-behind-the-prop case); there's no camera-radial
	// "lean" case (a wall behind a low prop is receding along its rays too); and no axial-plane oblique
	// slicing — the boundary is view-symmetric by construction. The margin (Cam.w) pulls the boundary point
	// toward the camera from the prop centre. `ahead` = world units this fragment sits before the boundary
	// along its own ray — the hole TAPERS shut over the last ~3/4 radius of it (below) instead of chopping,
	// so the truncation edge gets erosion, feather, rim and outline like any other part of the hole.
	// Camera inside the prop puts the boundary behind the camera — ahead <= 0 everywhere, nothing cuts.
	float3 camT = g_vClayCutoutCam.xyz;
	float3 boundary = prop + normalize( camT - prop ) * g_vClayCutoutCam.w;
	float3 toFrag = worldPos - camT;
	float ahead = -dot( toFrag, worldPos - boundary ) / max( length( toFrag ), 0.001 );
	if ( ahead <= 0.0 )
		return false;

	float t = clamp( along, 0.0, segLen );
	float distToAxis = distance( worldPos, origin + axis * t );

	// The two modes differ in the radius law and in where the noise is anchored:
	//   3D tunnel — constant world radius (near walls open proportionally wider), noise at the fragment's
	//               own world position: each occluder is eaten where it stands, holes parallax between
	//               layers — a volumetric bite through the world.
	//   2D disc   — a COOKIE CUTTER: radius grows linearly from the origin (a cone — constant angular size,
	//               d is invariant along a view ray), and the noise is sampled where the fragment's RAY
	//               crosses the plane at the prop's depth, so every layer a ray passes through is cut with
	//               the IDENTICAL silhouette — one flat hole through everything, no 3D layering. That plane
	//               is still world-anchored, so the pattern crawls with orbit/lag/prop movement like 3D.
	float d;
	float3 noiseP;
	if ( g_vClayCutoutScreen.w < 0.5 )
	{
		// Cylinder by default; Origin.w > 0 flares it toward the camera (radius at the prop stays `radius`,
		// growing by tan(half-angle) per unit back from it), so near scenery opens proportionally wider.
		d = distToAxis / max( radius + ( segLen - t ) * g_vClayCutoutOrigin.w, 0.001 );
		noiseP = worldPos;
	}
	else
	{
		d = distToAxis / max( radius * ( t / max( segLen, 0.001 ) ), 0.001 );
		noiseP = origin + ( worldPos - origin ) * ( segLen / max( along, 0.001 ) ); // ray extended to the prop's depth plane
		// Orbit drive: the axis direction turns at orbit rate, so this slide makes the pattern churn on
		// orbit the way 3D's does — see the attribute comment. Zero-cost while walking (axis constant).
		noiseP += axis * ( segLen * g_vClayCutoutScreen.x );
	}

	if ( d >= 1.0 )
		return false;

	// Noise-eroded edge: two octaves of gradient noise pull the effective radius inward, so the edge
	// crumbles raggedly instead of showing a clean cylinder/cone. World-anchored in both modes — the crumble
	// pattern is glued to the world and the hole edge crawls over it as the tunnel moves. Each octave is
	// SHEARED off-axis (and the fine one swizzled) so the two lattices can't reinforce.
	float3 p1 = noiseP * g_vClayCutoutStyle.x;
	float3 p2 = noiseP * g_vClayCutoutStyle.y;
	float n = ClayCutoutNoise( p1 + 0.37 * p1.yzx ) * 0.75
	        + ClayCutoutNoise( p2.yzx + 0.31 * p2.zxy + 19.7 ) * 0.25;
	float edge = 1.0 - n * g_vClayCutoutStyle.z;

	// Depth taper: the hole pinches smoothly shut over the last (Screen.z × radius) before the depth
	// boundary, so scenery crossing it shows a shrinking eroded hole instead of a hard truncation arc.
	// Width is a KNOB (DepthTaper) because it decides how close to the prop a thin occluder still opens:
	// a fence you hide right behind sits at a tiny `ahead`, and a wide taper strangles its hole entirely
	// (the behind-the-prop protection comes from ahead's SIGN, not from this width).
	edge *= smoothstep( 0.0, radius * max( g_vClayCutoutScreen.z, 0.01 ), ahead );

	if ( d < edge )
	{
		// Dithered feather: inside the eroded edge the cut fades in over a band (Guard.w × radius) by
		// stochastic discard — t reaches 1 (always cut) a full band inside, 0 (never) at the edge itself.
		// clip is binary and must also hole the depth PREPASS, so a true alpha fade isn't available; the
		// dither reads as a crumbling fringe up close and TAA averages it into a soft fade.
		float t = ( edge - d ) / max( g_vClayCutoutGuard.w, 0.0001 );
		if ( t >= ClayCutoutDither( px ) )
			return true;
		// Feather survivors fall through with rim = 1 (below): the speckled fringe darkens fully, which is
		// exactly the crumbled-clay cross-section read.
	}

	// Survived, but close to the eroded edge: hand back a rim weight (1 at/inside the edge -> 0 over the rim
	// band outside it) for the caller's cross-section darkening. tipFade quiets both bands as the depth
	// taper pinches the hole shut — with edge near 0 they'd otherwise paint a floating blob on the surface
	// where there's barely a hole at all.
	float tipFade = smoothstep( 0.02, 0.15, edge );
	rim = saturate( 1.0 - ( d - edge ) / max( g_vClayCutoutStyle.w, 0.001 ) ) * tipFade;

	// Outline band hugging the edge, [edge .. edge + width] with a quarter-width ease at each end (fixed
	// fractions, not fwidth — the early returns above leave quads partially dead, so derivatives here are
	// unreliable). It follows the NOISED edge, so the line traces the crumble contour, not a clean circle.
	float ow = g_vClayCutoutScreen.y;
	if ( ow > 0.0001 )
	{
		outline = smoothstep( edge - ow * 0.25, edge, d )
		        * ( 1.0 - smoothstep( edge + ow, edge + ow * 1.25, d ) )
		        * g_vClayCutoutOutline.a * tipFade;
	}
	return false;
}

#endif // CLAY_CUTOUT_HLSL
