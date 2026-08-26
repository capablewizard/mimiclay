namespace Mimiclay;

/// <summary>How the cutout hole is shaped — see <see cref="ClayCutoutSettings.Mode"/>. Both modes are carved
/// from the same lagged world segment with the same world-anchored noise (identical lag and edge-crawl
/// behaviour); the radius law is their only difference.</summary>
public enum ClayCutoutMode
{
	/// <summary>Constant world radius along the camera → prop sight line — a real tunnel: near walls open
	/// proportionally wider on screen (the perspective cue). The Baldur's Gate 3 look.</summary>
	Tunnel3D,
	/// <summary>Radius grows linearly from the camera end — a cone, i.e. constant ANGULAR size: every
	/// occluder shows the same screen-size hole, the classic screen-disc look.</summary>
	Disc2D,
}

/// <summary>
/// Inspector-tweakable settings for the camera-occlusion cutout (see <see cref="ClayCutout"/> and
/// Assets/shaders/clay_cutout.hlsl). Lives on the hider pawn next to <see cref="HiderController"/>, which
/// GetOrCreates one — so the component is optional in the prefab, but keeping it there is what makes tuned
/// values persist. Every value re-publishes each frame, so slider drags show immediately in play mode.
/// The scene-wide kill switch stays the <c>mimiclay_cutout</c> convar; per-material opt-out is the shader's
/// own "Camera Cutout" checkbox.
/// </summary>
[Title( "Clay Cutout Settings" ), Icon( "hide_source" )]
public sealed class ClayCutoutSettings : Component
{
	/// <summary>3D world-space tunnel (the BG3 look) or flat 2D screen-space disc. Switchable live.</summary>
	[Property, Group( "Mode" )] public ClayCutoutMode Mode { get; set; } = ClayCutoutMode.Tunnel3D;

	// ── Hole ────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Hole radius as a multiple of the prop's bounding radius — a little air around the shape so
	/// the reveal reads before the prop's own silhouette fills the hole. Used by both modes (3D scales the
	/// world radius, 2D scales the projected screen radius).</summary>
	[Property, Group( "Hole" ), Range( 1f, 3f )] public float RadiusScale { get; set; } = 1.2f;

	/// <summary>Radius floor in world units, so a tiny disguise still opens a readable hole (in 2D/cone mode
	/// this floors the radius at the prop's depth, i.e. the hole's angular size).</summary>
	[Property, Group( "Hole" ), Range( 0f, 64f )] public float MinRadius { get; set; } = 16f;

	/// <summary>Exponential ease rate for the radius opening/shutting — the dissolve-in/out animation speed.
	/// Higher = snappier. This is the hole's SIZE animation; <see cref="LagTime"/> is its position.</summary>
	[Property, Group( "Hole" ), Range( 1f, 30f )] public float EaseSpeed { get; set; } = 10f;

	/// <summary>How long the hole takes to catch up, in seconds — it drags behind while you move, then
	/// settles. 0 = rigidly locked (no lag). 0.1–0.2 is the soft trailing feel BG3 has; push past 0.4 for an
	/// obvious rubber-band. This one governs the PROP end of the tunnel, so it's what you feel while walking.
	/// The true camera position is never lagged: the shader needs it to tell the game view from
	/// shadow/reflection views.</summary>
	[Property, Group( "Hole" ), Range( 0f, 1f )] public float LagTime { get; set; } = 0.15f;

	/// <summary>Multiplies <see cref="LagTime"/> for the CAMERA end of the tunnel — the orbit lag. Needs to be
	/// bigger than 1 because orbit lag is geometrically weaker: the tunnel's far end is pinned to the prop, so
	/// the hole only shifts by (occluder's distance from the prop) × orbit speed × lag time, which vanishes for
	/// something you're hiding right behind. Raise until orbiting reads right; it won't touch the walk feel.</summary>
	[Property, Group( "Hole" ), Range( 1f, 10f )] public float OrbitLagScale { get; set; } = 3f;

	/// <summary>How far in FRONT of the prop centre (along the sight line, in prop-radii) cutting stops.
	/// Purely a scenery knob: the prop itself is exempt by identity (its renderers carry an exemption
	/// attribute), and geometry BEHIND the prop never cuts regardless (per-ray gate) — so go as low as you
	/// like to open scenery hugging the shape; raise it to keep a solid buffer in front of the prop.
	/// A thin fence/railing you hide right behind needs this low (~0.1) AND a modest DepthTaper.</summary>
	[Property, Group( "Hole" ), Range( 0f, 2f )] public float CutSlack { get; set; } = 0.1f;

	/// <summary>How fast the hole pinches shut approaching the depth boundary, in prop-radii along each view
	/// ray. Big = soft gradual closes but strangles the hole on thin items you hide right behind (they sit
	/// almost at the boundary); small = thin fences/railings open fully, closes are snappier. The
	/// behind-the-prop protection is unaffected — that's the gate's sign test, not this width.</summary>
	[Property, Group( "Hole" ), Range( 0.05f, 1.5f )] public float DepthTaper { get; set; } = 0.25f;

	// ── Edge ────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Coarse noise-octave scale (1/world-units — 0.15 ≈ 7u features). Lower = bigger, chunkier
	/// bites. World units in BOTH modes — the noise is anchored to the geometry either way.</summary>
	[Property, Group( "Edge" ), Range( 0.01f, 1f )] public float NoiseScale { get; set; } = 0.15f;

	/// <summary>Fine noise-octave scale — the grain on top of the chunky bites.</summary>
	[Property, Group( "Edge" ), Range( 0.01f, 2f )] public float NoiseScaleFine { get; set; } = 0.45f;

	/// <summary>2D mode only: how strongly ORBITING churns the noise, as a fraction of the camera→prop
	/// distance. The flat mode's pattern otherwise barely moves on orbit (its anchor plane is centred on the
	/// orbit pivot), while walking churns it at full speed — this buys orbit back up to parity with 3D.
	/// 0 = off; ~0.5 ≈ how 3D churns on a wall halfway to the prop. No effect while walking or in 3D mode.</summary>
	[Property, Group( "Edge" ), Range( 0f, 2f )] public float OrbitNoiseDrive { get; set; } = 0.5f;

	/// <summary>Erosion depth: how raggedly the noise eats the edge. 0 = clean cylinder/circle.</summary>
	[Property, Group( "Edge" ), Range( 0f, 1f )] public float Erode { get; set; } = 0.45f;

	/// <summary>Dithered feather width, as a fraction of the radius: the band inside the eroded edge where
	/// the cut fades in by stochastic per-pixel discard (a real alpha fade can't hole the depth prepass).
	/// Reads as a crumbling fringe up close; TAA averages it into a soft fade. 0 = hard 1px edge.</summary>
	[Property, Group( "Edge" ), Range( 0f, 0.5f )] public float EdgeFeather { get; set; } = 0.12f;

	/// <summary>Rim band width as a fraction of the radius — how far the cross-section darkening reaches.</summary>
	[Property, Group( "Edge" ), Range( 0f, 0.5f )] public float RimWidth { get; set; } = 0.15f;

	/// <summary>Rim darkening strength. 0 = no visible cross-section band.</summary>
	[Property, Group( "Edge" ), Range( 0f, 1f )] public float RimDarken { get; set; } = 0.35f;

	// ── Outline ─────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Coloured line traced along the eroded cut edge (it follows the crumble contour, not a clean
	/// circle). Alpha = opacity. Painted over the rim darkening where the two overlap.</summary>
	[Property, Group( "Outline" )] public Color OutlineColor { get; set; } = Color.White;

	/// <summary>Outline thickness as a fraction of the hole radius. 0 = no outline.</summary>
	[Property, Group( "Outline" ), Range( 0f, 0.3f )] public float OutlineWidth { get; set; } = 0f;

	// ── Ground guard ────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Keep up-facing geometry at or below the prop's own level solid — the floor you're standing on
	/// shouldn't dissolve. Up-facing geometry ABOVE the prop (an upstairs floor between camera and prop) still
	/// cuts either way, as do ceilings. Off = cut everything, floors included.</summary>
	[Property, Group( "Ground Guard" )] public bool GroundGuard { get; set; } = true;

	/// <summary>How far above the prop's centre the guard still protects up-facing geometry, as a fraction of
	/// the hole radius. Lower this if a table/step right beside the prop refuses to open.</summary>
	[Property, Group( "Ground Guard" ), Range( 0f, 2f )] public float GroundGuardHeight { get; set; } = 0.5f;

	/// <summary>How up-facing a surface must be to count as floor (world normal Z). 1 = only perfectly flat
	/// ground is protected; lower values protect ramps and bevels too.</summary>
	[Property, Group( "Ground Guard" ), Range( 0.1f, 1f )] public float GroundGuardSlope { get; set; } = 0.7f;
}
