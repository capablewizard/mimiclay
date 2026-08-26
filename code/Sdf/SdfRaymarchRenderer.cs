using System;
using System.Threading.Tasks;

namespace Mimiclay;

/// <summary>Render-path candidates for a first-person viewmodel (see <see cref="SdfRaymarchRenderer.ViewLayer"/>).
/// <see cref="OverlayFlag"/> is the production path; the others are kept as live-tweakable diagnostics.</summary>
public enum SdfViewLayer
{
	/// <summary>Normal game passes — depth-tested against the world (a viewmodel clips into walls).</summary>
	Normal,
	/// <summary>Native viewmodel layer (RenderLayer match + ViewModelLayer flag): renders invisible here,
	/// and a dead end — the engine's own BaseCombatWeapon viewmodels don't use this layer either.</summary>
	Viewmodel,
	/// <summary>OverlayWithoutDepth layer match: after post with NO depth — pure draw-over, but no
	/// GameOverlayLayer flag, so it misses the engine's overlay depth prepass (depth chain sees nothing).</summary>
	OverlayNoDepth,
	/// <summary>GameOverlayLayer FLAG (ModelRenderer RenderOptions.Overlay): THE viewmodel path. The engine
	/// runs a dedicated overlay depth prepass for this flag — before the world prepasses, stencil-claiming
	/// the object's pixels (bit 0x80) so nearer world depth can't overwrite them. The forward overlay pass
	/// (after post, scene depth) then wins at REAL depth: no wall clipping, and the depth buffer stays
	/// honest for screen UI (which depth-tests), contact shadows and screen AO.</summary>
	OverlayFlag,
}

/// <summary>How the sibling meshed ModelRenderer is used while raymarching. The raymarch writes
/// the depth+normals prepass itself (depth sorting, hardware occlusion, and AO) and — with
/// <see cref="SdfRaymarchRenderer.SdfShadows"/> on — casts its own shadows too, so the mesh's only
/// remaining job in the SDF band is the legacy shadow path (SdfShadows off).</summary>
public enum SdfMeshMode
{
	/// <summary>Invisible shadow caster (ShadowsOnly) while SdfShadows is OFF; fully disabled in the
	/// SDF band while SdfShadows is ON (the raymarch casts its own shadow). Default. (The name is
	/// historical — it no longer feeds a depth proxy.)</summary>
	DepthProxy,
	/// <summary>Disabled entirely (no shadows).</summary>
	Hidden,
	/// <summary>Shown normally (the meshed path).</summary>
	Visible,
}

/// <summary>
/// Experimental raymarched renderer for an <see cref="SdfSculpture"/>'s brushes — an A/B test
/// against the meshed path. Add this to the SAME GameObject as an SdfSculpture; it reads that
/// component's brushes, packs them into a data texture, and renders a bounding box whose pixel
/// shader sphere-traces the field (shaders/sdf_raymarch.shader).
///
/// Brush data is baked to WORLD space here so the shader can march in plain world space.
/// Assumes the GameObject has unit scale.
/// </summary>
[Title( "SDF Raymarch Renderer" )]
[Category( "SDF" )]
[Icon( "blur_circular" )]
public sealed class SdfRaymarchRenderer : Component, Component.ExecuteInEditor
{
	const int MaxBrushes = SdfBrushPacker.MaxBrushes;
	const int TexelsPerBrush = 7; // pos+shape, size+blend, quat, colour+op, rounding+metal+rough+mirrormask, aabbMin, aabbMax
	const int MaxSplinePoints = SdfBrushPacker.MaxSplinePoints; // shared pool of spline control points (xyz world pos, w radius) across all spline brushes

	/// <summary>Material to render with. Leave empty to use the built-in sdf_raymarch shader.</summary>
	[Property] public Material Material { get; set; }

	[Property, Range( 16, 256 )] public int MaxSteps { get; set; } = 96;
	[Property, Range( 0.005f, 1f )] public float Epsilon { get; set; } = 0.05f;

	/// <summary>Cast shadows by re-marching the SDF in each shadow view — the shader detects the sun's
	/// ORTHOGRAPHIC cascade cameras per-view and traces parallel rays there. Shadows then match the
	/// raymarched surface exactly (displacement lumps, live edits, subtractions) and stop depending on
	/// the remesh entirely; the sibling mesh is fully disabled in the SDF band and only takes over
	/// (shadows included) past the mesh handoff. OFF = the legacy path: the mesh renders ShadowsOnly
	/// and the raymarch casts nothing — kept for A/B (mesh shadows are cheaper: no per-cascade march).</summary>
	[Property, Group( "Shadows" )] public bool SdfShadows { get; set; } = true;

	// The shadow handoff distance (SdfShadowRadii) lives with the rest of the distance chain, down in
	// the LOD / Distance region — it's one link in that chain, not a separate dial.

	/// <summary>Extra caster-side depth bias (world units, along the light ray) written by the shadow
	/// march. The engine's receiver-side bias (per-cascade + receiver-plane) normally suffices — leave
	/// at 0 and raise slightly only if a prop shows shadow acne on itself.</summary>
	[Property, Group( "Shadows" ), Range( 0f, 4f )] public float ShadowBias { get; set; }

	/// <summary>Slope-scaled caster bias for PERSPECTIVE (spot/point) shadow views — the SV_Depth
	/// march bypasses the rasterizer's slope-scale bias meshes get (r.shadows.slopescale = 1.5), so
	/// the shader replicates it: the push grows by this many multiples of the measured per-texel
	/// depth slope at the surface. 1.5 matches the engine's mesh value; raise if grazing surfaces
	/// still band under spotlights, drop toward 0 if spot shadows visibly detach from the prop.</summary>
	[Property, Group( "Shadows" ), Range( 0f, 8f )] public float ShadowSlopeScale { get; set; } = 1.5f;

	/// <summary>Bake the whole brush field to a 3D distance texture and march by sampling it (one trilinear
	/// fetch per step) instead of re-evaluating every brush each step. Decouples render cost from brush
	/// count — the headline win for multi-brush props (a 31-brush giraffe then marches like a 1-brush blob).
	/// Trilinear interpolation slightly rounds sharp corners; raise <see cref="FieldResolution"/> for crisper
	/// edges at the cost of memory + bake time. Off = the exact per-brush march.</summary>
	[Property, Group( "Distance Field" )] public bool UseFieldCache { get; set; } = true;

	/// <summary>Global diagnostic kill switch for the field-cache compute path.</summary>
	[ConVar( "mimiclay_sdf_field_cache" )]
	public static bool FieldCacheEnabled { get; set; } = true;

	/// <summary>Voxels along the prop's longest axis in the GPU-evaluated field. Higher = sharper silhouette but
	/// memory grows with the CUBE (R32F: 64³≈1MB, 128³≈8MB, 256³≈67MB), and each edit re-dispatches every voxel.
	/// 64 is a good middle for organic clay props; 128–256 for crisp detail. (Past ~256 dense gets prohibitive —
	/// that's sparse-brick territory.)</summary>
	[Property, Group( "Distance Field" ), Range( 8, 512 )] public int FieldResolution { get; set; } = 64;

	/// <summary>Shading smoothness dial for the cached path: the surface-normal central difference spans this
	/// fraction of a voxel on each side. Lower (~0.35) keeps more detail but can show cell-scale faceting;
	/// higher (~0.7) reads smoother but can wash detail out. No effect on the silhouette — lighting only.</summary>
	[Property, Group( "Distance Field" ), Range( 0.25f, 1f )] public float FieldNormalScale { get; set; } = 0.5f;

	/// <summary>Render ONLY the distance field (live while editing, baked when settled) — never the analytic
	/// per-brush march. This is the Dreams model: the evaluated field is the single thing ever shown, kept fresh
	/// incrementally as edits arrive. Lets you see exactly what the cache produces without the brush-loop
	/// fallback masking a wrong or still-baking field. While the FIRST build is in flight there's no field yet,
	/// so the surface is briefly hidden (shadow only) rather than flashing the march. Needs <see cref="UseFieldCache"/>.</summary>
	[Property, Group( "Distance Field" )] public bool FieldCacheOnly { get; set; }

	/// <summary>Sample the sparse brick ATLAS (only surface bricks stored) instead of the dense field volume —
	/// the memory win that lets resolution scale (512+) without paying for empty space. A runtime toggle so you
	/// can A/B it against the dense path; the result should be visually identical. (Built by SdfFieldGpu's
	/// classify→alloc→fill passes.)</summary>
	[Property, Group( "Distance Field" )] public bool SparseField { get; set; }

	/// <summary>Global diagnostic kill switch for the sparse classify/allocate/fill path.</summary>
	[ConVar( "mimiclay_sdf_sparse_field" )]
	public static bool SparseFieldEnabled { get; set; } = true;

	bool EffectiveUseFieldCache => UseFieldCache && FieldCacheEnabled;
	bool EffectiveSparseField => SparseField && SparseFieldEnabled;

	/// <summary>Runtime override (NOT authored — a [Property] would leak into network spawn snapshots): scales
	/// <see cref="FieldResolution"/> for this prop's field bakes. Set below 1 by <see cref="SdfNetworkSync"/> on
	/// PROXIES during a remote live-drag, where per-frame sample interpolation re-dispatches the field every
	/// frame — cost scales with the CUBE, so 0.5 makes each dispatch 8× cheaper and a whole prep phase of
	/// remote sculptors costs about one local edit. Reset to 1 on commit: the settled shape re-bakes once at
	/// full resolution. The trade while below 1 is transient softness (small brushes melt at coarse voxels)
	/// that snaps crisp on commit. This replaced the old SuppressFieldCache analytic fallback — the field path
	/// now stays on for remote drags AND healing craters (SdfShrinkSystem), so the march cost never regresses
	/// to O(brush count) per pixel.</summary>
	public float FieldResolutionScale { get; set; } = 1f;

	/// <summary>The resolution actually dispatched: <see cref="FieldResolution"/> × <see cref="FieldResolutionScale"/>,
	/// floored at the property's own minimum so a tiny scale can't produce a degenerate volume.</summary>
	int EffectiveFieldResolution => Math.Max( 8, (int)MathF.Round( FieldResolution * Math.Clamp( FieldResolutionScale, 0.05f, 1f ) ) );

	/// <summary>Runtime override (NOT authored), the second live-drag lever beside <see cref="FieldResolutionScale"/>:
	/// minimum seconds between field re-dispatches while the brushes are changing every frame. 0 (the default,
	/// and every settled/locally-edited prop) = re-bake on every change. <see cref="SdfNetworkSync"/> sets it on
	/// PROXIES during a remote live-drag so the bake cadence follows the ~20 Hz sample stream instead of the
	/// frame rate — between bakes the marched surface holds the last baked shape, so the drag steps at stream
	/// rate rather than gliding, which is exactly what it looked like before interpolation existed. A first
	/// bake never waits (no field yet beats a fresh one), and the throttle self-heals: whenever it lapses with
	/// the hash still stale, the next elapsed frame re-bakes the current shape.</summary>
	public float FieldRebakeInterval { get; set; }

	RealTimeSince _sinceFieldDispatch; // throttle clock for FieldRebakeInterval

	// ── Adaptive Quality: the SHAPE of the SDF quality ramp (how cheap the floor is). WHERE it ramps is
	// two links of the LOD / Distance chain below — Full Quality (ramp starts) and Mesh Handoff (ramp
	// bottoms out, and the SDF hands over). The two used to be mixed into one group, which is what made
	// them feel like they were fighting: a quality knob and a handoff distance sitting side by side with
	// no indication that one of them also moved the mesh switch.

	/// <summary>Scale step count / epsilon down as the object shrinks on screen (a pure-SDF LOD —
	/// distant props march far cheaper while staying raymarched). Quality floors below.</summary>
	[Property, Group( "Adaptive Quality" )] public bool AdaptiveQuality { get; set; } = true;

	/// <summary>Step-count floor for small/far objects. Practical minimum is ~2-4 (paired with a
	/// large FarEpsilon); below that, far objects start dropping out as the march can't reach the
	/// surface. 0 would make them invisible.</summary>
	[Property, Group( "Adaptive Quality" ), Range( 1, 128 )] public int MinSteps { get; set; } = 28;

	/// <summary>Epsilon (hit threshold, world units) used for small/far objects — bigger = cheaper,
	/// softer/more inflated. Useful up to roughly the object's bounding radius; past that the march
	/// hits at the box face and the prop just renders as its bounding box.</summary>
	[Property, Group( "Adaptive Quality" ), Range( 0.01f, 16f )] public float FarEpsilon { get; set; } = 0.4f;

	// ── LOD / Distance ─────────────────────────────────────────────────────────────────────────────────
	//
	// ONE ordered chain of camera distances, nearest → farthest. Every threshold is in BOUNDING RADII
	// (camera distance ÷ the prop's bounding radius), so it's scale-invariant: a giraffe and a bottle cap
	// hand off at the same apparent size, and one set of numbers tunes a whole scene.
	//
	//   FullQuality ─────────────────────► MeshHandoff ─────► Lod1 ─────► Lod2 ─────► Cull
	//   │ full-quality march               │ SDF stops,       │           │           │ nothing
	//   │                                  │ mesh LOD0 starts │ mesh LOD1 │ mesh LOD2 │ renders
	//   └── SDF step count / epsilon ramp ─┘
	//         (quality bottoms out exactly AT the mesh handoff — one distance, by design: the
	//          raymarch hands over as it runs out of quality, so there's no window where it's
	//          paying to march a surface it can no longer resolve)
	//
	//        ShadowHandoff sits anywhere in [0, MeshHandoff] — a rider, not a link (see its doc).
	//
	// The chain is ordered BY CONSTRUCTION: setting any threshold pushes its neighbours out of the way
	// (Unity LODGroup style), so the numbers you see are the numbers that run. It used to be clamped at
	// USE time instead — dragging one value past another silently collapsed a band to zero width while
	// the inspector still showed the value you'd typed, and the thresholds were split across three
	// property groups, so nothing showed you the ordering you were actually fighting.
	//
	// This component is the sole owner/driver of all of it.

	// Chain slots, nearest → farthest. Keep in sync with ChainGet/ChainStore.
	// The shadow handoff is deliberately NOT a slot — see SdfShadowRadii.
	const int ChainFullQuality = 0, ChainMeshHandoff = 1;
	const int ChainLod1 = 2, ChainLod2 = 3, ChainCull = 4, ChainCount = 5;

	float ChainGet( int i ) => i switch
	{
		ChainFullQuality => FullQualityRadii,
		ChainMeshHandoff => MinQualityRadii,
		ChainLod1 => MeshLod1Radii,
		ChainLod2 => MeshLod2Radii,
		_ => CullRadii,
	};

	void ChainStore( int i, float v )
	{
		switch ( i )
		{
			case ChainFullQuality: FullQualityRadii = v; break;
			case ChainMeshHandoff: MinQualityRadii = v; break;
			case ChainLod1: MeshLod1Radii = v; break;
			case ChainLod2: MeshLod2Radii = v; break;
			default: CullRadii = v; break;
		}
	}

	// The chain as of the last sync, so the next one can tell WHICH threshold moved. Null until the
	// first sync (a fresh enable, or a hotload) — that pass normalises instead of diffing.
	float[] _chainPrev;

	/// <summary>Keep the distance chain ordered nearest→farthest, LODGroup style: whichever threshold
	/// moved since last frame pushes the others out of its way.
	///
	/// Deliberately a per-frame DIFF rather than logic in the property setters, and that matters twice
	/// over. Deserialization writes these members one at a time in alphabetical order (see
	/// ReflectionQueryCache.OrderedSerializableMembers) — a setter that shoved its neighbours mid-load
	/// would resolve an authored chain differently depending on the order it happened to be written in.
	/// And keeping every threshold a plain AUTO-property keeps its compiler-generated backing field name
	/// stable, which is what a code hotload matches state on: renaming the storage to a hand-written
	/// field silently resets every live prop in the open scene to code defaults — measured, not
	/// theorised, and one save would then bake those defaults over the authored values on disk.</summary>
	void SyncChain()
	{
		if ( _chainPrev is null )
		{
			// No previous state to diff (first enable / post-hotload): just put an authored chain in
			// order — the monotonic clamp the distance switch used to apply at use time, except it lands
			// in the STORED values now, so the inspector can't show a threshold that isn't running.
			_chainPrev = new float[ChainCount];
			float floor = 0f;
			for ( int i = 0; i < ChainCount; i++ )
			{
				floor = MathF.Max( ChainGet( i ), floor );
				ChainStore( i, floor );
				_chainPrev[i] = floor;
			}

			ClampShadowHandoff();
			return;
		}

		for ( int i = 0; i < ChainCount; i++ )
		{
			float v = MathF.Max( ChainGet( i ), 0f );
			if ( v == _chainPrev[i] )
				continue;

			ChainStore( i, v );
			for ( int j = i + 1; j < ChainCount; j++ )
				if ( ChainGet( j ) < v ) ChainStore( j, v );
			for ( int j = i - 1; j >= 0; j-- )
				if ( ChainGet( j ) > v ) ChainStore( j, v );

			break; // one drag at a time; a second edit in the same frame is caught by the next sync
		}

		for ( int i = 0; i < ChainCount; i++ )
			_chainPrev[i] = ChainGet( i );

		ClampShadowHandoff();
	}

	// The shadow handoff rides on the chain's upper bound without being IN it (see SdfShadowRadii):
	// past the mesh handoff the mesh is the caster anyway, so a shadow threshold beyond it means nothing.
	void ClampShadowHandoff()
	{
		if ( SdfShadowRadii > MinQualityRadii )
			SdfShadowRadii = MinQualityRadii;
	}

	/// <summary>Master switch for the distance coordinator: drive SDF↔mesh↔cull and the mesh LOD from
	/// camera distance each frame. Off = static behaviour (SDF always on, MeshMode applied literally) for
	/// A/B testing. The shadow handoff runs either way — it stands alone.</summary>
	[Property, Group( "LOD / Distance" )] public bool DistanceSwitching { get; set; } = true;

	/// <summary>Chain link 1. Inside this the raymarch runs at full quality (MaxSteps / Epsilon); past it
	/// step count and epsilon ramp toward the floor, reaching it at the mesh handoff.</summary>
	[Property, Group( "LOD / Distance" ), Title( "Full Quality (radii)" )]
	public float FullQualityRadii { get; set; } = 6f;

	/// <summary>Chain link 2 — the shadow handoff. Beyond this a SETTLED prop hands its shadow to the
	/// sibling mesh (ShadowsOnly) instead of re-marching the field in every shadow view: by far the
	/// biggest per-prop render cost in a crowd (measured ~4× the forward march itself on a screen of
	/// props). Inside the band, while boiling, and for a couple of seconds after any shape change (a live
	/// edit) the marched shadow stays, so the silhouette never lags the surface where it could be seen
	/// to. The swap is hard behind the hysteresis — shadow maps can't crossfade casters — but
	/// displacement is already off in shadow views (see SetupDisplacement), so the two silhouettes agree
	/// to well under a cascade texel at any sane threshold. 0 = never hand off (marched shadows at any
	/// distance). Runs with DistanceSwitching off too.
	///
	/// NOT a link in the push chain, on purpose — only a rider on its upper bound. Handing the shadow
	/// over early while the surface still marches at full quality is a perfectly good trade (shadows are
	/// the expensive half), so this must be free to sit anywhere inside the SDF band; making it a link
	/// would drag Full Quality around with it — precisely the toe-stepping the chain exists to stop. The
	/// one real constraint is the upper bound: past the mesh handoff the mesh is casting anyway.</summary>
	[Property, Group( "LOD / Distance" ), Title( "Shadow Handoff (radii)" ), Range( 0f, 64f )]
	public float SdfShadowRadii { get; set; } = 10f;

	/// <summary>Chain link 3 — the SDF→mesh handoff, and the same distance at which SDF quality bottoms
	/// out (see the chain diagram: one distance does both jobs deliberately). The raymarch turns off here
	/// and mesh LOD0 takes over. (Serialized under its historical name MinQualityRadii.)</summary>
	[Property, Group( "LOD / Distance" ), Title( "Mesh Handoff (radii)" )]
	public float MinQualityRadii { get; set; } = 60f;

	/// <summary>Chain link 4: the visible mesh drops from LOD0 to LOD1 here.</summary>
	[Property, Group( "LOD / Distance" ), Title( "Mesh LOD 1 (radii)" )]
	public float MeshLod1Radii { get; set; } = 90f;

	/// <summary>Chain link 5: the mesh drops from LOD1 to LOD2 here.</summary>
	[Property, Group( "LOD / Distance" ), Title( "Mesh LOD 2 (radii)" )]
	public float MeshLod2Radii { get; set; } = 140f;

	/// <summary>Chain link 6: past this the prop is culled entirely — no SDF, no mesh, no shadow.</summary>
	[Property, Group( "LOD / Distance" ), Title( "Cull (radii)" )]
	public float CullRadii { get; set; } = 220f;

	/// <summary>Dead-band around every threshold (fraction of the threshold) so a prop sitting on a
	/// boundary doesn't flicker between states. 0.06 = ±6%.</summary>
	[Property, Group( "LOD / Distance" ), Range( 0f, 0.4f )] public float LodHysteresis { get; set; } = 0.06f;

	/// <summary>Debug view of the WHOLE chain on this prop, in the scene view:
	/// <list type="bullet">
	/// <item>while raymarched — the in-shader quality heatmap (red = floor/cheapest → green = full);</item>
	/// <item>once handed off — a flat tint per mesh LOD (cyan LOD0, blue LOD1, magenta LOD2), so the
	/// handoff itself is visible instead of the heatmap simply vanishing at it;</item>
	/// <item>either way — a label with the band and the bounding-radii ratio that picked it, so you can
	/// read a threshold straight off the prop rather than guessing which side of it you're on.</item>
	/// </list>
	/// (Absorbed the old separate DebugSwitchState toggle — one chain, one debug switch.)</summary>
	[Property, Group( "LOD / Distance" )] public bool DebugLod { get; set; }

	/// <summary>What to do with the sibling meshed ModelRenderer while raymarching.
	/// <list type="bullet">
	/// <item><b>DepthProxy</b> (default): invisible ShadowsOnly mesh — casts shadows for the prop.</item>
	/// <item><b>Hidden</b>: disable it (no shadows).</item>
	/// <item><b>Visible</b>: show the mesh normally (the meshed path).</item>
	/// </list></summary>
	[Property] public SdfMeshMode MeshMode { get; set; } = SdfMeshMode.DepthProxy;

	/// <summary>Hide the rendered surface while still casting a shadow — first-person parity with a
	/// ModelRenderer set to ShadowsOnly. With <see cref="SdfShadows"/> on, the raymarched scene object
	/// STAYS enabled but is clipped from every perspective view in-shader (SdfShadowOnly) and excluded
	/// from the game passes, so only its ortho sun-shadow march survives; the mesh carries the
	/// shadows-only job past the handoff. With SdfShadows off it's the old path: scene object off, mesh
	/// ShadowsOnly. Resolved with everything else in ApplyVisibility (the single visibility writer), so it
	/// wins in every band. Used to hide a pawn's own SDF body on the machine that controls it. Not a
	/// [Property] — it's driven at runtime by the controlling pawn, not authored on the prefab.</summary>
	public bool RenderHidden { get; set; }

	/// <summary>Which render path the raymarched surface draws through — the candidates for a first-person
	/// viewmodel that can't clip into world geometry. Runtime-driven by the owning pawn (the hunter's gun),
	/// like <see cref="RenderHidden"/> — not a [Property].</summary>
	public SdfViewLayer ViewLayer { get; set; } = SdfViewLayer.Normal;

	/// <summary>Render into a SceneWorld other than the scene's own. The component still lives in — and ticks
	/// with — the real scene (that's the only place a Component runs), but its scene object is created in this
	/// world instead, so the game camera cannot see it at any position and only that world's lights reach it.
	/// This is the hook the offscreen thumbnail stage (<see cref="SdfStage"/>) hangs off. Null = the scene's own
	/// world, which is every normal prop. Not a [Property] — it's a code-only handle.</summary>
	public SceneWorld TargetWorld { get; set; }

	SceneWorld RenderWorld => TargetWorld ?? Scene.SceneWorld;

	/// <summary>Viewmodel FOV ratio — tan(cameraHalfFov)/tan(viewmodelHalfFov). The shader warps the proxy's
	/// raster footprint by this and unwarps per pixel, so the marched rays belong to the VIEWMODEL's own
	/// projection: camera FOV and gun FOV are fully independent. 1 = render in the camera's projection.
	/// Runtime-driven by the owning pawn, like <see cref="ViewLayer"/> — not a [Property].</summary>
	public float ViewmodelFovScale { get; set; } = 1f;

	// The layer state actually applied to _so (null = never applied / _so recreated). Tracked OURSELVES because
	// SceneObject.RenderLayer's setter is buggy: its early-out compares a cached value that the Default branch
	// clears natively but never writes back — after one ViewModel→Default round-trip the cache reads ViewModel
	// while the native match is null, and re-setting ViewModel early-outs forever (observed as the debug toggle
	// working once each way, then going dead).
	SdfViewLayer? _appliedViewLayer;

	// Apply ViewLayer on CHANGE only, never trusting the scene object's own change detection. Match-based modes
	// route through a second real layer first so the setter's stale cache can't early-out the transition,
	// whatever state previous toggles left it in.
	void ApplyViewLayer()
	{
		if ( _appliedViewLayer == ViewLayer )
			return;

		_appliedViewLayer = ViewLayer;

		// Reset the flag-based bits; each mode below re-asserts what it needs.
		_so.Flags.ViewModelLayer = false;
		_so.Flags.OverlayLayer = false;

		switch ( ViewLayer )
		{
			case SdfViewLayer.Viewmodel:
				_so.RenderLayer = SceneRenderLayer.OverlayWithDepth; // cache-mover; both assignments land natively
				_so.RenderLayer = SceneRenderLayer.ViewModel;
				_so.Flags.ViewModelLayer = true;
				break;

			case SdfViewLayer.OverlayNoDepth:
				_so.RenderLayer = SceneRenderLayer.OverlayWithDepth; // cache-mover
				_so.RenderLayer = SceneRenderLayer.OverlayWithoutDepth;
				break;

			case SdfViewLayer.OverlayFlag:
				_so.RenderLayer = SceneRenderLayer.Default;
				_so.Flags.OverlayLayer = true;
				break;

			default:
				_so.RenderLayer = SceneRenderLayer.Default;
				break;
		}
	}

	// Any reason the raymarched surface should be off this frame. Every place that turns _so.RenderingEnabled ON
	// honours this, so a per-frame Refresh (the props edit constantly) can't override a hide. _fieldOnlyHidden is
	// included so this is a strict superset of the old "&& !_fieldOnlyHidden" guard. RenderHidden only forces the
	// scene object OFF on the legacy path — with SdfShadows the object must keep rendering (into shadow views
	// only, gated in-shader by SdfShadowOnly + ExcludeGameLayer), or it would cast no shadow at all.
	bool ForceHidden => (RenderHidden && !MarchedShadowsNow) || _fieldOnlyHidden;

	// Hold marched shadows this long after a shape change: long enough to ride out a live edit's
	// per-frame churn without flapping, short enough that a settled prop hands off promptly.
	const float ShadowShapeHold = 2f;

	/// <summary>Does the raymarch cast its own shadow THIS frame? SdfShadows off = never (the legacy
	/// mesh-ShadowsOnly path). On, it still marches only where the mesh couldn't stand in: near the
	/// camera (inside <see cref="SdfShadowRadii"/>), while the sibling ClayBoil churns the surface, or
	/// just after the shape changed (a live edit) — everywhere else the settled field and the mesh have
	/// the same silhouette, and the mesh casts for a fraction of the cost. Consumed by the scene-object
	/// flags, the mesh-mode config AND ApplyVisibility, so all three flip together the frame the band
	/// (or the boil/edit state) does.</summary>
	bool MarchedShadowsNow => SdfShadows
		&& ( SdfShadowRadii <= 0f || !_aboveShadowRadii || _boilActive || _sinceShapeChange < ShadowShapeHold );

	/// <summary>Overdraw optimisation: skip the redundant back-face march (the box draws both faces)
	/// and use conservative depth so the GPU keeps early-Z and rejects hidden fragments before
	/// marching. Toggle off to A/B the cost.
	///
	/// Auto-disabled while the camera is within <see cref="OverdrawNearRadii"/> bounding radii of the
	/// prop (with the usual hysteresis): despite the shader's back-face rationale, a camera inside or
	/// nearly inside the proxy makes the prop vanish with this on (reproduced 2026-08-25 — only its
	/// shadow survives, since the light's views aren't inside the box). The march is a runtime uniform,
	/// so the per-frame flip costs nothing, and the prop the camera is touching is the one place the
	/// double-faced march is cheapest to afford (it's one prop, however big on screen).</summary>
	[Property, Group( "Overdraw" )] public bool OverdrawOptimization { get; set; } = true;

	// The camera band inside which OverdrawOptimization is forced off (bounding radii). The vanish
	// reproduces with the camera just inside the bounding sphere (ratio ~1); 2 leaves comfortable
	// margin for the near plane and FOV without giving up anything measurable — props inside 2 radii
	// of the camera are rarely more than a handful.
	const float OverdrawNearRadii = 2f;

	/// <summary>Per-brush + per-spline-segment AABB early-out in the analytic march: at each step, skip a
	/// brush's (or spline span's) distance math when its bounds are too far to change the result. Only affects
	/// the per-brush path (off when the field cache is active) — toggle to A/B its cost while editing a
	/// multi-brush or high-segment-count prop.</summary>
	[Property, Group( "Overdraw" )] public bool BrushCulling { get; set; } = true;

	/// <summary>Clamp the FORWARD march against the scene depth the prepass already wrote: a pixel whose
	/// ray enters the proxy behind nearer opaque geometry returns before marching at all, and miss rays
	/// stop at the first occluder instead of marching the rest of the proxy. This matters because the
	/// shader writes SV_Depth, which disables early-Z — without it a prop fully hidden behind a wall (or
	/// behind another SDF prop's overlapping proxy) still pays its complete march per covered pixel.
	/// Toggle off to A/B the cost.</summary>
	[Property, Group( "Overdraw" )] public bool DepthOcclusionCull { get; set; } = true;

	/// <summary>Render a tight bounding-SPHERE proxy instead of the AABB box, so round props march
	/// fewer wasted pixels around their silhouette. Best for round/blobby props; an AABB box can be
	/// tighter for long/thin props, so toggle off for those.</summary>
	[Property, Group( "Overdraw" )] public bool TightBounds { get; set; }

	/// <summary>Lumpy plasticine displacement on the raymarched surface — bends the SILHOUETTE, not
	/// just the lighting (the meshed LOD is unaffected). The lumps are baked INTO the distance field
	/// (re-baked per claymation-boil tick), so the per-frame cost is only a step-safety understep —
	/// the march itself stays one texture fetch per step. The LOOK (lump depth/density) is tuned on
	/// the MATERIAL, alongside the transmission and curvature-shading params.</summary>
	[Property, Group( "Displacement" )] public bool Displace { get; set; }

	/// <summary>Lump depth, inches. Lives HERE, not on the material: the lumps are baked into the
	/// distance field by this component, and a material param can't be read back reliably —
	/// Material.Attributes is captured at load, so material-editor edits never reached the bake
	/// (tuning silently did nothing). Edits here re-bake on the next frame. Keep Amp × Freq under
	/// ~0.15 or the field stops being a valid sphere-trace distance (holes); keep under ~1.5 so
	/// outward lumps stay inside the proxy pad.</summary>
	[Property, Group( "Displacement" ), Range( 0f, 1.5f )] public float DispAmp { get; set; } = 0.8f;

	/// <summary>Lump density, cells per inch (wavelength = 1/this). Same ownership story as
	/// <see cref="DispAmp"/>. Keep the wavelength at least ~2–3 field VOXELS (voxel ≈ padded prop
	/// span / FieldResolution) or the bake can't resolve the lumps — they fade or alias, and do so
	/// differently on differently-sized props. The stock 0.073 (~14" lumps) is safe everywhere.</summary>
	[Property, Group( "Displacement" ), Range( 0.01f, 1f )] public float DispFreq { get; set; } = 0.073f;

	/// <summary>Let the lumps bend this prop's cast SHADOW too on the LIVE (analytic-fallback) path —
	/// the ortho shadow cascades march at full quality, so per-step noise is at its most expensive
	/// there and off (the default) skips it. On the normal BAKED-field path this has no effect: the
	/// lumps are in the texture, every view samples them, and the shadow silhouette always matches
	/// the camera one (which is a look upgrade, and the understep it costs the shadow views is far
	/// cheaper than the per-step noise ever was). No effect with <see cref="Displace"/> off.</summary>
	[Property, Group( "Displacement" )] public bool DisplaceShadows { get; set; }

	/// <summary>This prop's offset into the claymation boil (see <see cref="ClayBoil"/>). The boil
	/// TICK is global — real stop-motion advances every model on the same frame — so only the random
	/// offset varies per prop, otherwise a room full of props visibly re-forms in lockstep.
	/// Derived from GameObject.Id, NOT the transform: a position-derived seed would re-roll the boil
	/// pattern as the prop moved. The irrational multiplier keeps seeds off the integer tick grid, so
	/// two props can't share a sequence by landing a whole number apart. Kept under 64 to stay in the
	/// range where the shader's frac()-based hash has mantissa left (the tick is wrapped for the same
	/// reason) — a big seed reads as "all props share one offset" once precision runs out.</summary>
	private float BoilSeed => BoilSeedFor( GameObject );

	/// <summary>Shared with SdfSculpture, which stamps the same seed on the meshed path — the shaders'
	/// SeedTexOffset (per-instance triplanar offset) must agree across the raymarch/mesh swap.</summary>
	internal static float BoilSeedFor( GameObject go ) => ( ( go.Id.GetHashCode() & 0xFFFF ) * 0.6180339887f ) % 64f;

	/// <summary>Subsurface / back-scatter lighting — thin parts glow when back-lit (foliage, skin,
	/// ears). Thickness is read from the SDF itself, so it's cheap (runs once after the hit, not per
	/// march step) and needs no baked thickness map. The look (tint, strength, thickness falloff) is
	/// tuned on the MATERIAL. Best paired with the baked field (the thickness march samples it cheaply);
	/// on the analytic brush path that march re-runs the brush loop per step.</summary>
	[Property, Group( "Transmission" )] public bool Transmission { get; set; }

	/// <summary>Draw the raymarch bounding box (green; red when the game camera is inside it).</summary>
	[Property, Group( "Overdraw" )] public bool DebugBounds { get; set; }

	/// <summary>Debug: draw the LIVE FIELD's grid bounds (cyan) plus its 8³ brick lattice. Compare against the
	/// rendered surface and the proxy box (enable <see cref="DebugBounds"/> too): if the surface clips at the
	/// CYAN box, the field grid is too small; if it clips at the green/red PROXY box, the march bracket is.</summary>
	[Property, Group( "Overdraw" )] public bool DebugLiveField { get; set; }

	SceneObject _so;
	Texture _dataTex;
	Texture _splineTex; // spline control points (xyz world pos, w radius); spline brushes index into it
	readonly SdfTextAtlas _textAtlas = new(); // baked text distance fields (Text brushes), bound as "TextSdf"
	Material _fallbackMaterial;
	int _lastHash;
	ModelRenderer _meshRenderer;
	bool _aboveShadowRadii;          // shadow-band hysteresis: camera is beyond SdfShadowRadii
	bool _overdrawSafe;              // camera is beyond OverdrawNearRadii (hysteresis) — overdraw opt may run

	/// <summary>The editor scene-view camera position, pumped every editor frame by the editor
	/// assembly's SdfEditorViewPump (null outside the editor, or with no scene view). Public only as
	/// that plumbing's landing spot — game code has no legitimate reader. Exists because DrawGizmos
	/// (the only in-component access to the editor camera) doesn't tick with the gizmo pass off, and
	/// the camera bands would silently freeze at their full-quality defaults.</summary>
	public static Vector3? EditorViewPos;
	bool _boilActive;                // sibling ClayBoil boiling this frame (stamped in Refresh's attribute block)
	int _lastShapeHash;              // brush SHAPE hash only — movement must not re-arm the edit hold
	RealTimeSince _sinceShapeChange; // arms ShadowShapeHold on shape edits
	Vector3 _curMins, _curMaxs;       // world AABB (shader march bracket + frustum bounds)
	Vector3 _curLocalMins, _curLocalMaxs; // local AABB (the oriented proxy box, for debug draw)
	Vector3 _curProxyMins, _curProxyMaxs; // padded local proxy bounds actually built (highlight proxy rebuilds from these)
	Vector3 _curCenter;
	float _curRadius;
	int _curCount;
	bool _fieldReady; // this frame's field-cache readiness (the highlight marches the baked field only)
	bool _forceRepack;
	bool _released; // set when we've torn down _so; stops OnUpdate/Refresh from resurrecting it

	bool _destroyed; // set in OnDestroy; cancels the deferred sibling-mesh hand-back during a delete

	// Live-edit field (Dreams "evaluator / CS of doom"): a per-instance volume re-evaluated on the GPU by a
	// compute shader whenever the brushes change, so the march stays on D_FIELD_TEX instead of the analytic
	// per-brush path — with no CPU eval and no upload. Remote drags re-dispatch every frame at a reduced
	// resolution (FieldResolutionScale; see the property doc). Null when settled (shared bake).
	SdfFieldGpu _fieldGpu;
	int _lastFieldHash; // (brushes + resolution + sparse) hash of the last GPU dispatch — re-dispatch when it changes
	bool _fieldOnlyHidden;   // FieldCacheOnly + no field ready this frame -> hide the surface (last-word visibility)
	string _lastLiveDebug;   // last DebugLiveField state string, logged only on change
	float[] _dbgOcc; int _dbgBx, _dbgBy, _dbgBz, _dbgOccHash = -1; // cached brick-occupancy readback for the debug view
	int _dbgReadCooldown; // throttles the debug readbacks so dragging a brush (field rebuilds/frame) doesn't stall/frame

	// Distance-switch state. The four "above" flags carry hysteresis across frames (each only flips once
	// the ratio clears its threshold ± the dead-band). _switchState is the resolved stage for the label.
	bool _aboveHandoff, _aboveLod1, _aboveLod2, _aboveCull;
	int _switchState; // 0 SDF, 1 LOD0, 2 LOD1, 3 LOD2, 4 culled

	Material ActiveMaterial => Material ?? (_fallbackMaterial ??= Material.FromShader( "shaders/sdf_raymarch.shader" ));

	protected override void OnEnabled()
	{
		_released = false; // re-enabling revives a previously released renderer
		_destroyed = false;
		_chainPrev = null; // every authored threshold has landed by now — normalise, then track edits
		SyncChain();
		AdoptMeshRenderer();

		_lastHash = 0;
		Refresh();
	}

	/// <summary>The sibling meshed renderer — the LOD/shadow stand-in this component owns outright.
	///
	/// Looked up INCLUDING DISABLED components, and that is load-bearing: turning the mesh off is normal
	/// operation here (<see cref="ApplyVisibility"/> writes Enabled every frame), but Enabled is a
	/// SERIALIZED property — it gets written into scenes and prefabs, and copied by GameObject.Clone. The
	/// old lookup was <c>Components.Get&lt;ModelRenderer&gt;()</c>, which skips disabled components, so it
	/// returned null in exactly the case that needed fixing: a prefab saved with its mesh off (half the prop
	/// library ships that way) or a perf-grid clone stamped from one could never find its mesh again and
	/// spent its whole life with no mesh and no mesh shadows. GetOrCreate covers a prop that has no
	/// ModelRenderer yet — SdfSculpture makes one anyway when it meshes.</summary>
	ModelRenderer MeshRenderer
	{
		get
		{
			if ( _meshRenderer.IsValid() )
				return _meshRenderer;
			if ( !GameObject.IsValid() )
				return null;
			return _meshRenderer = GameObject.Components.GetOrCreate<ModelRenderer>();
		}
	}

	// Take ownership of the sibling mesh: clear anything a previous owner (or a serialized/cloned state)
	// left on it that ApplyVisibility does NOT rewrite every frame. Visibility itself needs no seeding —
	// it's resolved from scratch each frame — so this only has to undo the sticky bits.
	void AdoptMeshRenderer()
	{
		var mesh = MeshRenderer;
		if ( !mesh.IsValid() )
			return;

		mesh.MaterialOverride = null; // e.g. SdfSculpture's drag-proxy wireframe, or a stale clone override
	}

	void EnsureResources()
	{
		// Recreate if missing OR the width changed (e.g. TexelsPerBrush changed across a
		// hot-reload that preserved the old, smaller texture — otherwise Update overruns it).
		int width = MaxBrushes * TexelsPerBrush;
		if ( _dataTex is null || _dataTex.Width != width )
		{
			_dataTex = Texture.Create( width, 1 )
				.WithFormat( ImageFormat.RGBA32323232F )
				.WithDynamicUsage()
				.Finish();

			_forceRepack = true; // new (empty) texture must be repacked before it's valid
		}

		if ( _splineTex is null || _splineTex.Width != MaxSplinePoints )
		{
			_splineTex = Texture.Create( MaxSplinePoints, 1 )
				.WithFormat( ImageFormat.RGBA32323232F )
				.WithDynamicUsage()
				.Finish();

			_forceRepack = true;
		}
	}

	protected override void OnDisabled()
	{
		_so?.Delete();
		_so = null;

		// Hand the sibling mesh back as a normal visible renderer when the raymarch renderer is
		// disabled but the object lives on (e.g. the user unchecks this component). We CAN'T tell
		// that apart from the first step of a GameObject delete synchronously — GameObject.Active is
		// true either way, and on delete OnDestroy fires immediately after this. So defer one tick:
		// if it was a real delete, _destroyed is set by then and we skip. Re-enabling the sibling
		// during teardown spawns a fresh render object for it that the engine then orphans, leaving
		// an unselectable solid mesh in the viewport with no GameObject behind it.
		RestoreSiblingMeshDeferred();
	}

	async void RestoreSiblingMeshDeferred()
	{
		var go = GameObject;
		var mesh = MeshRenderer; // including-disabled: we're most likely handing back a mesh WE turned off

		try { await Task.Yield(); }
		catch { return; }

		if ( _destroyed || !go.IsValid() || !go.Active || !mesh.IsValid() )
			return;

		mesh.Enabled = true;
		mesh.MaterialOverride = null;
		mesh.RenderType = ModelRenderer.ShadowRenderType.On;
	}

	// Backstop: _so is a raw SceneObject living in Scene.SceneWorld, NOT in the GameObject graph,
	// so destroying the GameObject doesn't free it. OnDisabled normally handles that, but it isn't
	// guaranteed to run for every teardown (e.g. cloned ExecuteInEditor objects destroyed in bulk),
	// which orphans the scene object — it keeps rendering with no GameObject behind it. Delete here
	// too so cleanup never hangs on a single hook firing.
	protected override void OnDestroy()
	{
		_destroyed = true;
		ReleaseSceneObject();
	}

	/// <summary>Delete the raw scene object now, without waiting for a lifecycle hook. Lets a caller
	/// destroying this GameObject (e.g. SdfPerfGrid.Clear) guarantee the SceneWorld primitive goes
	/// with it, rather than relying on OnDisabled/OnDestroy firing during bulk teardown.</summary>
	internal void ReleaseSceneObject()
	{
		_released = true;
		_so?.Delete();
		_so = null;

		// Drop the per-instance GPU edit field (re-created on the next edit if this renderer comes back).
		_fieldGpu = null;
	}

	protected override void OnUpdate()
	{
		SyncChain(); // before anything reads a threshold, so the bands and the inspector never disagree

		// Bands FIRST — before anything reads them. Refresh writes band-derived state onto the scene object
		// (CastShadows from MarchedShadowsNow, the overdraw and depth-cull toggles from _overdrawSafe), and
		// it used to run BEFORE this: on the frame a band flipped it wrote the OLD answer while
		// ApplyVisibility below wrote the NEW one, so the two disagreed for exactly one frame. Crossing the
		// shadow handoff inward that produced a visible SHADOW GAP — Refresh left CastShadows false (stale:
		// "the mesh is casting") in the same frame ApplyVisibility disabled the mesh renderer, so for one
		// frame nothing cast at all and the prop's shadow blinked out.
		//
		// The ratio is measured against LAST frame's centre/radius (Refresh computes those). That's fine:
		// bounds barely move in a frame, the thresholds are radii apart, and the hysteresis dead-band is
		// ±6% — it can shift a crossing by a frame, never split one across two.
		if ( !_released && BandViewPos is Vector3 view )
			UpdateBands( view );

		Refresh();

		// ONE resolved visibility application per frame, after every input (Refresh state, band state) is
		// current. Replaces the old chain of order-dependent writers.
		ApplyVisibility();

		DrawLodLabel(); // reads this frame's resolved band, so it can never disagree with what's drawn

		// Sync the sibling highlight LAST, from this frame's refreshed state. Driven from here (not
		// the highlight's own OnUpdate) so component update order can never leave the outline
		// mirroring a frame-old bracket/field — during sculpt edits everything changes per frame,
		// and a one-frame-stale mirror reads as outline jitter against the live surface.
		if ( Highlight.IsValid() )
			Highlight.SyncFromRenderer( this );
	}

	// THE one place that decides what renders — for the raymarched scene object AND the sibling mesh. Every
	// input (the distance-switch band, ForceHidden, RenderHidden, SdfShadows, MeshMode, the field-only hide)
	// resolves to one state written in one pass, every frame, LEVEL-driven: nothing here reads the current
	// state, so there is no edge to miss and no order to get wrong. It replaced six separate writers (Refresh,
	// UpdateDistanceSwitch, ApplyRenderHidden, ApplyFieldOnlyHide, RestoreStaticState, ApplyMeshMode) whose
	// correctness depended on their call order — and, worse, on having SEEN every transition. Keep it that
	// way: a new visibility input belongs HERE, not in a new "runs last" or "runs on change" method.
	void ApplyVisibility()
	{
		if ( !_so.IsValid() )
			return;

		// Band state from the distance switch's hysteresis flags; static mode (DistanceSwitching off) counts
		// as permanently inside the SDF band.
		bool culled = DistanceSwitching && _aboveCull;
		bool sdf = !culled && (!DistanceSwitching || !_aboveHandoff);
		int meshLod = _aboveLod2 ? 2 : _aboveLod1 ? 1 : 0;

		// The raymarched surface: its band, minus every hide (ForceHidden folds in RenderHidden-without-
		// SdfShadows and the field-only hide). With RenderHidden AND SdfShadows the object stays on as the
		// in-band shadow-only caster — the SdfShadowOnly + ExcludeGameLayer attrs set in Refresh keep it
		// out of every colour pass.
		bool soOn = sdf && !ForceHidden;
		_so.RenderingEnabled = soOn;

		var mesh = MeshRenderer;
		if ( !mesh.IsValid() )
			return;

		// Resolve the mesh's WHOLE state, then write all of it — every frame, in every mode. Nothing here
		// reads the renderer's current state, so it converges from any starting point in one frame: a clone,
		// a freshly loaded prefab, a hotload, a mode toggle. (This used to be split with ApplyMeshMode, which
		// only ran on MeshMode/shadow EDGES and owned static mode outright — so a prop that entered a frame
		// in the wrong state with no edge to correct it just stayed wrong.)
		var want = ResolveMeshState( soOn, culled, sdf, meshLod );

		// A ModelRenderer with a null Model doesn't draw nothing — the engine substitutes models/dev/box.vmdl
		// (ModelRenderer.UpdateObject) — so a sculpture whose mesh build hasn't landed yet, or produced no
		// geometry at all, would stand in for the prop as a grey box, shadow and all. Wait for a real model.
		if ( want.Enabled && mesh.Model is null )
			want = want with { Enabled = false };

		mesh.Enabled = want.Enabled;
		mesh.RenderType = want.RenderType;
		mesh.LodOverride = want.Lod;

		// DebugLod: the SDF band paints its own quality heatmap in-shader, but that shader stops running
		// the instant the prop hands off to the mesh — exactly when you most want to see what happened.
		// Tint the mesh per band so the whole chain reads in one view. SceneObject.ColorTint is TRANSIENT
		// (the component's authored Tint is never written, nothing is serialized), and it's rewritten from
		// the resolved band every frame, so it also can't stick around once the debug goes off.
		var meshSo = mesh.SceneObject;
		if ( meshSo.IsValid() )
			meshSo.ColorTint = DebugLod ? DebugBandTint( culled, sdf, meshLod ) : mesh.Tint;
	}

	// Flat per-band tints for DebugLod, picking up where the shader's SDF heatmap (red = quality floor →
	// green = full) leaves off: the mesh bands get cool hues, so "still marching" and "handed off to the
	// mesh" can never be mistaken for one another.
	static Color DebugBandTint( bool culled, bool sdf, int meshLod )
	{
		if ( culled || sdf )
			return Color.White; // culled draws nothing; in-band the mesh is an invisible shadow caster

		return meshLod switch
		{
			0 => new Color( 0.2f, 0.9f, 1f ),  // LOD0 — cyan
			1 => new Color( 0.3f, 0.45f, 1f ), // LOD1 — blue
			_ => new Color( 0.85f, 0.3f, 1f ), // LOD2 — magenta
		};
	}

	// What the sibling mesh should be doing this frame. Pure: same inputs -> same state, no history.
	readonly record struct MeshState( bool Enabled, ModelRenderer.ShadowRenderType RenderType, int? Lod );

	MeshState ResolveMeshState( bool soOn, bool culled, bool sdf, int meshLod )
	{
		const ModelRenderer.ShadowRenderType ShadowsOnly = ModelRenderer.ShadowRenderType.ShadowsOnly;
		const ModelRenderer.ShadowRenderType On = ModelRenderer.ShadowRenderType.On;

		// Invisible-but-shadow-casting pawn: the mesh is a ShadowsOnly caster — everywhere when the SDF
		// doesn't cast its own shadow this frame (SdfShadows off OR the shadow LOD handed off), otherwise
		// only outside the SDF band (in-band the scene object casts; two casters would double the shadow).
		if ( RenderHidden )
			return new MeshState(
				MarchedShadowsNow ? !soOn : true,
				ShadowsOnly,
				DistanceSwitching && !sdf && !culled ? meshLod : null );

		// Static mode (no distance coordinator): MeshMode applies literally. DepthProxy is the legacy
		// ShadowsOnly caster, and it stands down while the raymarch is casting its own shadow.
		if ( !DistanceSwitching )
			return MeshMode switch
			{
				SdfMeshMode.DepthProxy => new MeshState( !MarchedShadowsNow, ShadowsOnly, null ),
				SdfMeshMode.Hidden => new MeshState( false, On, null ),
				_ => new MeshState( true, On, null ), // Visible: the meshed path
			};

		// Past the cull band nothing renders at all.
		if ( culled )
			return new MeshState( false, ShadowsOnly, null );

		// SDF is the visible surface. While the raymarch casts its own shadow the mesh has no job at all;
		// otherwise (SdfShadows off, or the shadow LOD handed off) the mesh is the ShadowsOnly caster —
		// unless MeshMode hides it.
		if ( sdf )
			return new MeshState( MeshMode != SdfMeshMode.Hidden && !MarchedShadowsNow, ShadowsOnly, null );

		// Mesh band: show the mesh at the LOD this distance selects.
		return new MeshState( true, On, meshLod );
	}

	// Editor: the scene-view camera is only reachable as Gizmo.Camera inside a gizmo scope, so the
	// coordinator runs here in edit mode (Refresh already ran in OnUpdate this frame). Also draws the
	// optional state label. Runs every frame while scene gizmos are enabled.
	protected override void DrawGizmos()
	{
		// The visible surface is a hand-built raw SceneObject (_so, below), not the kind of renderer the
		// editor's click-to-select path resolves back to a GameObject — and the one component that WOULD be
		// pickable that way, the sibling ModelRenderer, is deliberately disabled by default (see
		// ApplyVisibility: MeshMode defaults to DepthProxy with SdfShadows on). Without an
		// explicit hitbox there was nothing under the cursor to click at all. Registered unconditionally
		// (before the DistanceSwitching early-out below) so clicking still works even with that feature off.
		if ( _curRadius > 0.01f )
			Gizmo.Hitbox.BBox( new BBox( _curLocalMins, _curLocalMaxs ) );

		if ( _released || !Scene.IsEditor )
			return;

		// Fallback driver only. OnUpdate already ran both bands off BandViewPos; this covers the case
		// where the editor assembly isn't pumping EditorViewPos, since the gizmo pass always has a
		// camera of its own. Same helper, so there's never a second policy — just a second source.
		if ( EditorViewPos is null )
		{
			UpdateBands( Gizmo.Camera.Position );
			ApplyVisibility(); // OnUpdate applied before this ran (editor order) — re-apply with the fresh band
		}

	}

	/// <summary>The DebugLod label: the band, the bounding-radii ratio that picked it, and which caster is
	/// currently casting. Walk the camera until the name changes and you've read a threshold straight off
	/// the prop — no arithmetic, no guessing which side of a value you're on.
	///
	/// Drawn through DebugOverlay rather than Gizmo.Draw, deliberately: gizmo drawing only happens inside
	/// the editor's gizmo pass, which does NOT run for every prop every frame — the same asymmetry that
	/// was hiding the mesh handoff itself. The overlay is a plain scene object, so the label shows up
	/// wherever the prop does, selected or not, in the editor and in game.</summary>
	void DrawLodLabel()
	{
		if ( !DebugLod || _released || _curRadius <= 0.01f || BandViewPos is not Vector3 view )
			return;

		float ratio = (view - _curCenter).Length / _curRadius;
		bool culled = DistanceSwitching && _aboveCull;
		bool marching = !culled && (!DistanceSwitching || !_aboveHandoff);
		int meshLod = _aboveLod2 ? 2 : _aboveLod1 ? 1 : 0;

		string band = !DistanceSwitching
			? "SDF (static)"
			: _switchState switch
			{
				0 => "SDF",
				1 => "LOD0",
				2 => "LOD1",
				3 => "LOD2",
				_ => "CULLED",
			};

		var sb = new System.Text.StringBuilder();
		// Line 1: which band, and the ratio that picked it — plus the radius it's a ratio OF, because
		// "12.4r" is unreadable without knowing what one r is worth on this prop.
		sb.Append( $"{band}   {ratio:0.0}r   (r={_curRadius:0}u)" );

		// Line 2: the adaptive-quality budget the march is ACTUALLY running at this frame. Computed the
		// shader's way, not ours: LodQuality measures the ratio against the half-diagonal of the world AABB
		// (g_vBoundsMin/Max), while the band thresholds above measure against _curRadius — two different
		// radii, so reproducing the shader's arithmetic is the only way these numbers mean anything. Ortho
		// (sun-shadow) views always march at full quality; this is the camera view's budget.
		if ( marching )
		{
			float q = ShaderLodQuality( view );
			int steps = (int)MathX.Lerp( Math.Min( MinSteps, MaxSteps ), MaxSteps, q );
			float eps = MathX.Lerp( MathF.Max( FarEpsilon, Epsilon ), Epsilon, q );
			sb.Append( $"\nq {q:0.00}   steps {steps}/{MaxSteps}   eps {eps:0.000}" );
		}
		else
		{
			sb.Append( culled ? "\nnot rendering" : $"\nmeshed — LOD{meshLod}" );
		}

		// Line 3: the two handoffs that are easy to mistake for each other. The shadow caster is an
		// independent threshold on the same ratio, and a shadow that changes shape at a distance you didn't
		// set is the confusing one — so name who is casting, always.
		sb.Append( $"\nshadow: {(MarchedShadowsNow ? "march" : "mesh")}" );
		if ( MeshRenderer is { } mr && mr.IsValid() )
			sb.Append( $"   mesh: {(mr.Active ? mr.RenderType.ToString() : "off")}{(mr.LodOverride is int l ? $" LOD{l}" : "")}" );

		// Line 4: what the march is actually sampling, and how much of it. A prop that's secretly on the
		// analytic per-brush path (or whose field hasn't landed) reads as "slow for no reason" otherwise.
		// No "live vs baked" distinction here on purpose: the shared CPU bake is retired, so every prop
		// marches its own SdfFieldGpu volume. The useful facts are the resolution it's paying for, whether
		// it's sparse, and whether it has landed yet.
		string field = !EffectiveUseFieldCache ? "analytic (per-brush)"
			: _fieldGpu is { IsValid: true } && _fieldReady ? $"{EffectiveFieldResolution}³{(SparseField ? " sparse" : "")}"
			: "baking…";
		sb.Append( $"\n{_curCount} brushes   field: {field}" );

		// This text is drawn in WORLD space, so it shrinks with distance — and the mesh bands are by
		// definition far away, which made the readout illegible at exactly the range it's about. Scale the
		// size with camera distance so it holds a roughly constant size on screen at any range.
		float size = Math.Clamp( 16f * (view - _curCenter).Length / 300f, 8f, 600f );

		// Sit the block ABOVE the prop rather than through it, anchored to the TIGHT local bounds — not
		// _curMaxs, which is the padded proxy box and can stand well clear of the surface (this frying pan
		// proxies at 100u). Bottom-aligned, so the text grows upward off the top of the shape and the prop
		// itself stays unobscured however many lines this ends up being.
		var tx = WorldTransform;
		float top = float.MinValue;
		for ( int i = 0; i < 8; i++ )
		{
			var corner = new Vector3(
				(i & 1) != 0 ? _curLocalMaxs.x : _curLocalMins.x,
				(i & 2) != 0 ? _curLocalMaxs.y : _curLocalMins.y,
				(i & 4) != 0 ? _curLocalMaxs.z : _curLocalMins.z );
			top = MathF.Max( top, tx.PointToWorld( corner ).z );
		}

		var anchor = new Vector3( _curCenter.x, _curCenter.y, top + size * 0.25f );

		// overlay: true = draw THROUGH geometry, so a prop in front can't swallow the readout for the prop
		// you're actually reading. A debug label that hides behind the scene is no label.
		DebugOverlay?.Text( anchor, sb.ToString(), size, TextFlag.CenterHorizontally | TextFlag.Bottom,
			color: marching ? Color.White : DebugBandTint( culled, false, meshLod ), overlay: true );
	}

	/// <summary>The shader's own LodQuality for this view position — 1 = full quality, 0 = the floor.
	/// Deliberately mirrors sdf_raymarch.shader's LodQuality() including its choice of radius (half the
	/// world AABB's diagonal, NOT <c>_curRadius</c>), so the steps/epsilon the label reports are the ones
	/// the march really used. Keep the two in sync if that function changes.</summary>
	float ShaderLodQuality( Vector3 viewPos )
	{
		if ( !AdaptiveQuality )
			return 1f;

		var centre = (_curMins + _curMaxs) * 0.5f;
		float r = MathF.Max( 0.5f * (_curMaxs - _curMins).Length, 0.001f );
		float ratio = (centre - viewPos).Length / r;

		float near = FullQualityRadii;
		float far = MathF.Max( MinQualityRadii, near + 0.001f );
		return Math.Clamp( (far - ratio) / MathF.Max( far - near, 0.001f ), 0f, 1f );
	}

	/// <summary>The camera every distance band measures from. At runtime it's the game camera; in the
	/// editor there IS no Scene.Camera for the scene view, so it's <see cref="EditorViewPos"/>, pumped
	/// every editor frame by the editor assembly. Null = no view this frame — the bands hold.</summary>
	Vector3? BandViewPos => Scene.IsEditor
		? EditorViewPos
		: (Scene.Camera.IsValid() ? Scene.Camera.WorldPosition : null);

	/// <summary>Both camera-distance bands, from one position, in one call.
	///
	/// They used to be driven from different places in the editor: the shadow band from EditorViewPos in
	/// OnUpdate, the distance switch ONLY from DrawGizmos — which doesn't run for every prop every frame.
	/// So in the editor a prop would correctly hand its SHADOW to the mesh at distance while never handing
	/// over the SURFACE: no mesh LOD, no cull, the raymarch running at any distance. One source, both
	/// bands, and the two can't disagree about where the camera is. The shadow band updates regardless of
	/// DistanceSwitching — the shadow LOD stands alone in static mode.</summary>
	void UpdateBands( Vector3 viewPos )
	{
		UpdateShadowBand( viewPos );
		if ( DistanceSwitching )
			UpdateDistanceSwitch( viewPos );
	}

	// Resolve the pipeline stage from camera distance — COMPUTE ONLY: it updates the hysteresis band flags
	// and the debug label state; ApplyVisibility (the one visibility writer) turns those into what actually
	// renders. One ratio drives all of it, so the label can never disagree with what's drawn, and there's a
	// single set of thresholds to tune.
	// The camera-distance bands that must run with DistanceSwitching off too (static mode is the
	// shipped state) — deliberately NOT part of UpdateDistanceSwitch. Same ratio, same hysteresis
	// helper, so a prop parked on a threshold doesn't flap its caster (or its overdraw path).
	void UpdateShadowBand( Vector3 viewPos )
	{
		if ( _curRadius <= 0.01f )
			return;

		float ratio = (viewPos - _curCenter).Length / _curRadius;
		if ( SdfShadowRadii > 0f )
			_aboveShadowRadii = Above( ratio, SdfShadowRadii, _aboveShadowRadii, LodHysteresis );
		// Overdraw opt is unsafe with the camera at/inside the proxy (the prop vanishes) — see the
		// property doc. Gated here rather than in the shader so the fix can't drift from the bug.
		_overdrawSafe = Above( ratio, OverdrawNearRadii, _overdrawSafe, LodHysteresis );
	}

	void UpdateDistanceSwitch( Vector3 viewPos )
	{
		if ( !_so.IsValid() || _curRadius <= 0.01f )
			return;

		float ratio = (viewPos - _curCenter).Length / _curRadius;
		float h = LodHysteresis;

		// No clamping here any more: SyncChain orders the chain where it's STORED, so these are the
		// authored values and the authored values are what run.
		_aboveHandoff = Above( ratio, MinQualityRadii, _aboveHandoff, h );
		_aboveLod1 = Above( ratio, MeshLod1Radii, _aboveLod1, h );
		_aboveLod2 = Above( ratio, MeshLod2Radii, _aboveLod2, h );
		_aboveCull = Above( ratio, CullRadii, _aboveCull, h );

		_switchState = _aboveCull ? 4 : !_aboveHandoff ? 0 : 1 + (_aboveLod2 ? 2 : _aboveLod1 ? 1 : 0);
	}

	// Hysteresis gate: returns whether `ratio` is above `threshold`, but only flips state once the ratio
	// has cleared the threshold by ±h (the dead-band), so a prop parked on a boundary doesn't flicker.
	static bool Above( float ratio, float threshold, bool wasAbove, float h )
	{
		if ( wasAbove )
			return ratio >= threshold * (1f - h); // stay above until it drops below the lower edge
		return ratio > threshold * (1f + h);       // go above only once past the upper edge
	}

	/// <summary>Force a full repack + rebuild, ignoring the change-hash.</summary>
	[Button( "Refresh" )]
	public void ForceRefresh()
	{
		_forceRepack = true;
		Refresh();
	}

	public void Refresh()
	{
		// Don't rebuild a scene object we've deliberately released (e.g. a clone mid-teardown).
		// GameObject.Destroy() is deferred, so OnUpdate can still tick us after ReleaseSceneObject;
		// without this guard that tick resurrects _so and it outlives the GameObject.
		if ( _released )
			return;

		var sculpt = GameObject.Components.Get<SdfSculpture>();
		var brushes = sculpt?.Brushes;
		if ( brushes is not { Count: > 0 } )
			return;

		EnsureResources();

		// A SceneObject belongs to the world it was constructed in, so a TargetWorld swap can't be patched onto
		// the live one — drop it and let the block below rebuild it in the new world.
		if ( _so.IsValid() && _so.World != RenderWorld )
		{
			_so.Delete();
			_so = null;
		}

		var tx = WorldTransform;

		// Rebuild geometry + packed brush data only when the brushes / transform change. UseFieldCache is in
		// here because the proxy's padding (liveBounds below) depends on it — toggling it used to leave a
		// stale proxy until the next brush edit.
		// Shape-only hash feeds the shadow LOD's edit hold: brush changes re-arm it, movement doesn't
		// (the mesh moves with the prop; only a shape divergence needs the marched shadow back). The
		// first sight of a shape (spawn/enable) is not an edit — otherwise every prop in a freshly
		// loaded map would open with ShadowShapeHold seconds of full marched shadows.
		int shapeHash = Hash( brushes );
		if ( shapeHash != _lastShapeHash )
		{
			_sinceShapeChange = _lastShapeHash == 0 ? ShadowShapeHold : 0f;
			_lastShapeHash = shapeHash;
		}

		int hash = HashCode.Combine( shapeHash, tx.Position, tx.Rotation, Material, TightBounds, EffectiveUseFieldCache );
		if ( hash != _lastHash || !_so.IsValid() || _forceRepack )
		{
			// Bounds computed in the object's LOCAL frame, so the proxy can be ORIENTED to the object
			// instead of a world-axis-aligned cube (a rotated/flat prop's world AABB is hugely bloated).
			if ( !TryLocalBounds( brushes, out var lmins, out var lmaxs ) )
				return;

			_forceRepack = false;
			_lastHash = hash;
			_curCount = PackBrushes( brushes );

			// World centre/radius (LOD + sphere proxy) from the local AABB centre.
			_curCenter = tx.PointToWorld( (lmins + lmaxs) * 0.5f );
			_curRadius = WorldBoundingRadius( brushes, tx, _curCenter );

			// Default proxy = a box ORIENTED to the object (local AABB transformed to world), so a
			// rotated/flat prop gets a tight box rather than a giant world-axis cube. TightBounds =
			// a bounding sphere instead (round props). Either way the shader marches against a world
			// AABB that contains the proxy, so its bracket stays valid.
			// The PROXY + march bracket must ENCLOSE the field's DEFINED region. The live field pads its
				// grid beyond the tight bounds for incremental headroom, so surface out in that padding (a blend
				// bulge, or the brush near the edge) gets sliced flat by the proxy silhouette — those are the
				// angle-dependent cut-offs. Settled mode never hits this (its field spans exactly the tight
				// bounds and clamps at the edge). So when a live field is active, grow the proxy to cover it.
				var pmins = lmins;
				var pmaxs = lmaxs;
				bool liveBounds = EffectiveUseFieldCache; // the GPU field is padded by BlendPad, so grow the proxy to enclose it
				if ( liveBounds )
				{
					pmins -= SdfFieldGpu.BlendPad;
					pmaxs += SdfFieldGpu.BlendPad;
					for ( int i = 0; i < 8; i++ ) // grow the LOD/sphere radius to reach the padded corners too
					{
						var corner = new Vector3( (i & 1) != 0 ? pmaxs.x : pmins.x,
							(i & 2) != 0 ? pmaxs.y : pmins.y, (i & 4) != 0 ? pmaxs.z : pmins.z );
						_curRadius = MathF.Max( _curRadius, (tx.PointToWorld( corner ) - _curCenter).Length );
					}
				}

				_curProxyMins = pmins;
				_curProxyMaxs = pmaxs;

				Mesh proxy;
			BBox worldBb;
			if ( TightBounds )
			{
				proxy = BuildSphere( _curCenter, _curRadius, ActiveMaterial );
				worldBb = new BBox( _curCenter - _curRadius, _curCenter + _curRadius );
			}
			else
			{
				proxy = BuildOrientedBox( pmins, pmaxs, tx, ActiveMaterial, out worldBb );
			}

			_curMins = worldBb.Mins;
			_curMaxs = worldBb.Maxs;
			_curLocalMins = lmins;
			_curLocalMaxs = lmaxs;

			var model = new ModelBuilder().AddMesh( proxy ).Create();

			// Reuse the scene object (swap its model) rather than delete + recreate it.
			if ( _so.IsValid() )
			{
				_so.Model = model;
			}
			else
			{
				_so = new SceneObject( RenderWorld, model );
				// Per-object attributes (BrushData/Bounds/Count) don't survive batching, so two
				// SDF objects sharing the material would collapse into one draw and hide together.
				_so.Batchable = false;
				_appliedViewLayer = null; // fresh object, fresh layer state — re-apply whatever's wanted
				// Shadow casting: the shader's Depth mode detects the sun's orthographic cascade
				// views per-view and traces PARALLEL rays there, so the marched surface casts its
				// own exact shadow. Kept in sync with MarchedShadowsNow every frame below; OFF = the
				// legacy sibling-mesh (ShadowsOnly) path — which the shadow LOD also hands off to at
				// distance (see SdfShadowRadii).
				_so.Flags.CastShadows = MarchedShadowsNow;
			}

			_so.Bounds = worldBb;
		}

		// Re-apply ALL per-object attributes every frame. If a binding is dropped (e.g. when
		// selection rebinds the shared material), BrushCount reads 0 -> this clip()-heavy
		// shader discards every pixel and the object vanishes while still "valid". Setting
		// them each frame restores it immediately. (RenderingEnabled is NOT set here — that's
		// ApplyVisibility's job, the single visibility writer.)
		// Shadow wiring, kept live so toggling SdfShadows / RenderHidden lands immediately. ShadowOnly
		// (the hidden pawn body) clips every PERSPECTIVE view in-shader — only the ortho sun-shadow
		// march survives; ExcludeGameLayer additionally keeps it out of the game colour passes.
		bool shadowOnly = RenderHidden && MarchedShadowsNow;
		_so.Flags.CastShadows = MarchedShadowsNow;
		_so.Flags.ExcludeGameLayer = shadowOnly;
		ApplyViewLayer();
		// Viewmodel shader behaviour rides any non-Normal view layer: the viewmodel-FOV ray warp, plus the
		// material AO term switching to the 5-tap field-computed self-AO (min()ed with screen AO by the
		// engine). Depth is REAL in both passes — anti-clip comes from the engine's overlay depth prepass
		// (OverlayFlag's stencil claim), not from the old near-camera depth squash, whose near blob used
		// to beat the screen UI's depth test and draw the gun over the HUD.
		_so.Attributes.Set( "SdfViewmodel", ViewLayer != SdfViewLayer.Normal ? 1 : 0 );
		_so.Attributes.Set( "SdfVmFovScale", MathF.Max( ViewmodelFovScale, 0.01f ) );
		_so.Attributes.Set( "SdfShadowOnly", shadowOnly ? 1 : 0 );
		_so.Attributes.Set( "SdfShadowBias", ShadowBias );
		_so.Attributes.Set( "SdfShadowSlopeScale", ShadowSlopeScale );
		_so.Attributes.Set( "BrushData", _dataTex );
		_so.Attributes.Set( "SplineData", _splineTex );
		_so.Attributes.Set( "TextSdf", _textAtlas.Texture );
		_so.Attributes.Set( "BrushCount", _curCount );
		_so.Attributes.Set( "BoundsMin", _curMins );
		_so.Attributes.Set( "BoundsMax", _curMaxs );
		_so.Attributes.Set( "BoundsCenter", _curCenter );
		_so.Attributes.Set( "BoundsRadius", _curRadius );
		// Object placement — the shader's world<->model fold. Brushes are packed in LOCAL space now, so
		// the march folds every sample through this transform (and the triplanar projection re-bases
		// through it too). Rotation-only inverse in the shader assumes unit scale.
		_so.Attributes.Set( "ModelOrigin", tx.Position );
		_so.Attributes.Set( "ModelRotation", new Vector4( tx.Rotation.x, tx.Rotation.y, tx.Rotation.z, tx.Rotation.w ) );
		_so.Attributes.Set( "MaxSteps", MaxSteps );
		_so.Attributes.Set( "Epsilon", Epsilon );

		if ( AdaptiveQuality )
		{
			_so.Attributes.Set( "MinSteps", Math.Min( MinSteps, MaxSteps ) );
			_so.Attributes.Set( "FarEpsilon", MathF.Max( FarEpsilon, Epsilon ) );
			_so.Attributes.Set( "LodNear", FullQualityRadii );
			_so.Attributes.Set( "LodFar", MathF.Max( MinQualityRadii, FullQualityRadii + 0.001f ) );
		}
		else
		{
			// Floor == ceiling -> constant full quality regardless of distance.
			_so.Attributes.Set( "MinSteps", MaxSteps );
			_so.Attributes.Set( "FarEpsilon", Epsilon );
			_so.Attributes.Set( "LodNear", 9999f );
			_so.Attributes.Set( "LodFar", 10000f );
		}

		_so.Attributes.Set( "DebugLod", DebugLod ? 1f : 0f );
		// Runtime uniforms, not combos — a 7th combo once crashed the Vfx compiler, and none of these
		// gate resources or per-step inner-loop cost, so per-draw uniform branches are effectively free.
		// (D_DEPTH_CLAMP was deleted with its property: nothing shipped used it, and inter-object
		// occlusion rides the engine depth chain via the prepass.)
		_so.Attributes.Set( "SdfOverdrawOpt", OverdrawOptimization && _overdrawSafe ? 1 : 0 );
		_so.Attributes.Set( "SdfDepthCull", DepthOcclusionCull ? 1 : 0 );
		_so.Attributes.Set( "SdfCull", BrushCulling ? 1 : 0 );
		_so.Attributes.Set( "SdfTightBounds", TightBounds ? 1 : 0 );
		// Displacement look (amp/freq) lives on THIS component (see the DispAmp doc for why it left
		// the material); pushed as attributes for the live-fallback shader path — the baked path
		// gets the same values through the field bake below. The D_DISPLACE combo (live per-step
		// noise) is decided AFTER the field block: it's the fallback for when no baked field is
		// available. DispShadows only affects that fallback (a baked field can't be smooth per-view).
		_so.Attributes.Set( "DispAmp", DispAmp );
		_so.Attributes.Set( "DispFreq", DispFreq );
		_so.Attributes.Set( "DispShadows", DisplaceShadows ? 1 : 0 );

		// Claymation boil — OPT-IN per prop: only a GameObject carrying an enabled, ACTIVE ClayBoil
		// boils (a WhileDamaged boil with no shrinking crater is treated exactly like no component).
		// The else branch is not optional; see ClayBoil.ApplyOff (attributes persist, so a removed,
		// disabled or deactivated component would otherwise leave the prop boiling forever). Pushed
		// every frame so live tuning, add/remove in play mode, and activation flips all take effect
		// immediately.
		var boil = GameObject.Components.Get<ClayBoil>(); // self-only + enabled-only
		bool boilActive = boil is { Boiling: true };
		_boilActive = boilActive; // feeds MarchedShadowsNow: a boiling surface keeps its marched shadow
		if ( boilActive )
			boil.Apply( _so.Attributes );
		else
			ClayBoil.ApplyOff( _so.Attributes );
		_so.Attributes.Set( "BoilSeed", BoilSeed );
		// The sibling meshed renderer samples the same triplanar maps — stamp the same seed so the
		// per-instance texture offset doesn't pop when the raymarch<->mesh role swaps.
		var meshSo = MeshRenderer?.SceneObject;
		if ( meshSo.IsValid() )
			meshSo.Attributes.Set( "BoilSeed", BoilSeed );

		// Transmission look (tint, strength, thickness) lives on the material; this just gates the combo.
		_so.Attributes.SetCombo( "D_TRANSMISSION", Transmission ? 1 : 0 );

		// Field cache (D_FIELD_TEX): the GPU compute evaluator (SdfFieldGpu) writes this prop's distance volume,
		// which the march samples. Re-dispatched only when brushes change — or, for a displaced prop with a
		// ClayBoil, when the boil TICK rolls (the tick is in the hash below): the lumps are baked INTO the field
		// at the current tick, so a boiling prop costs a few dispatches a second at BoilFps instead of paying
		// two-octave noise per march step in every view every frame. The shared CPU bake (SdfFieldBaker) is
		// retired for now (see the note in that file).
		float bakeAmp = 0f, bakeFreq = 0.25f, bakeTick = -1f, bakeJitter = 0f, bakeAmpJitter = 0f;
		// An ACTIVE boil borrows displacement for its duration (ClayBoil.ForceDisplace): the churn is
		// displacement movement, so on a smooth prop there'd be nothing to move. Both edges are just
		// bakeAmp changing in the field hash below — the lumps land with the boil's first tick and
		// settle with its last, no extra machinery.
		bool displace = Displace || (boilActive && boil.Fps > 0f && boil.ForceDisplace);
		if ( displace )
		{
			bakeAmp = DispAmp;
			bakeFreq = DispFreq;
			if ( boilActive && boil.Fps > 0f )
			{
				// ClayBoil.TickAt is THE clock (global tick normally, an off-grid impact pose during
				// a shot's jolt); the shader's live tick reads the same value via the BoilTick
				// attribute, so the analytic fallback and the baked field agree by construction.
				// (An activation flip changes bakeTick between -1 and a tick, so the hash below
				// re-dispatches the field once on each edge — lumps appear/settle within a frame.)
				bakeTick = boil.TickAt( Time.Now ) + BoilSeed;
				bakeJitter = boil.Jitter;
				bakeAmpJitter = boil.AmpJitter;
			}
		}
		_fieldReady = false;
		if ( EffectiveUseFieldCache && _curCount > 0 && _curRadius > 0.01f )
		{
			_fieldGpu ??= new SdfFieldGpu();
			// Re-dispatch when anything the build depends on changes: the brushes, the resolution (including its
			// live-drag scale), the sparse toggle, OR the baked displacement (its look params + the boil tick).
			// Folding the field parameters into the hash means tweaking them in the inspector updates the field
			// live, instead of only when a brush is moved.
			int res = EffectiveFieldResolution;
			int fieldHash = HashCode.Combine( Hash( brushes ), res, EffectiveSparseField, bakeAmp, bakeFreq, bakeTick, bakeJitter, bakeAmpJitter );
			if ( (fieldHash != _lastFieldHash || !_fieldGpu.IsValid)
				&& (!_fieldGpu.IsValid || _sinceFieldDispatch >= FieldRebakeInterval) )
			{
				if ( _fieldGpu.Evaluate( brushes, _curLocalMins, _curLocalMaxs, res, EffectiveSparseField,
					bakeAmp, bakeFreq, bakeTick, bakeJitter, bakeAmpJitter ) )
				{
					_lastFieldHash = fieldHash;
					_sinceFieldDispatch = 0f;
				}
			}
			if ( _fieldGpu.IsValid )
			{
				_so.Attributes.Set( "FieldTex", _fieldGpu.Texture );
				_so.Attributes.Set( "FieldMin", _fieldGpu.Mins );
				_so.Attributes.Set( "FieldMax", _fieldGpu.Maxs );
				_so.Attributes.Set( "FieldDims", _fieldGpu.Dims );
				// High-res brick grid dims for the sparse sampler. Equals FieldDims today; once the guide field drops to
				// a lower resolution (4d-2) the brick grid stays high while g_tField (the guide) goes low.
				_so.Attributes.Set( "SurfaceDims", _fieldGpu.SurfaceDims );
				_so.Attributes.Set( "FieldNormalScale", FieldNormalScale );

				// Sparse atlas (runtime toggle). Always bind the atlas + indirection when present (so the declared
				// StructuredBuffer is never left unbound), and flip SdfSparse to select sparse vs dense.
				if ( _fieldGpu.Atlas.IsValid() && _fieldGpu.IndirectionTex.IsValid() )
				{
					_so.Attributes.Set( "Atlas", _fieldGpu.Atlas );
					_so.Attributes.Set( "IndirectionTex", _fieldGpu.IndirectionTex );
					_so.Attributes.Set( "BrickDims", _fieldGpu.BrickDims );
					_so.Attributes.Set( "AtlasDims", _fieldGpu.AtlasDims );
					_so.Attributes.Set( "AtlasEncode", _fieldGpu.AtlasEncodeBand ); // 8-bit tile decode band (0 = raw R32F)
				}
				_so.Attributes.Set( "SdfSparse", EffectiveSparseField && _fieldGpu.Atlas.IsValid() ? 1 : 0 );

				_fieldReady = true;
			}
		}
		_so.Attributes.SetCombo( "D_FIELD_TEX", _fieldReady ? 1 : 0 );
		// Live per-step displacement ONLY as the analytic fallback: with a baked field the lumps are
		// already in the texture, and the live subtract would displace the surface twice.
		_so.Attributes.SetCombo( "D_DISPLACE", displace && !_fieldReady ? 1 : 0 );
		// Baked-field gradient safety: subtracting the noise at bake steepens the field's slope by up
		// to L = bound·freq·4 (bound = the worst boil tick's amp), so the march understeps by 1/(1+L)
		// and grows its budget to match — the same insurance the live path computes for itself.
		_so.Attributes.Set( "DispGradL", displace && _fieldReady
			? bakeAmp * (1f + MathF.Max( bakeAmpJitter, 0f ) * 0.5f) * bakeFreq * 4f : 0f );

		if ( DebugLiveField && _fieldGpu is { IsValid: true } )
			DrawLiveFieldDebug( tx );

		// FieldCacheOnly: suppress the analytic march entirely. When the field path is active but nothing's
		// ready yet (first build in flight), hide the surface this frame rather than revealing the brush loop —
		// applied as the last word on visibility in OnUpdate/UpdateDistanceSwitch.
		_fieldOnlyHidden = FieldCacheOnly && EffectiveUseFieldCache && _curCount > 0 && _curRadius > 0.01f && !_fieldReady;

		if ( DebugLiveField )
		{
			bool valid = _fieldGpu is { IsValid: true };
			var fsz = valid ? (_fieldGpu.Maxs - _fieldGpu.Mins) : Vector3.Zero;
			var psz = _curMaxs - _curMins;
			bool ren = _so.IsValid() && _so.RenderingEnabled;
			string st = $"[SDF live] resScale={FieldResolutionScale} valid={valid} ready={_fieldReady} hidden={_fieldOnlyHidden} render={ren} field={fsz} proxy={psz}";
			if ( st != _lastLiveDebug ) { _lastLiveDebug = st; Log.Info( st ); }
		}

		if ( DebugBounds )
		{
			// The actual ORIENTED proxy box (local AABB drawn through the object transform). Red
			// when the game camera is inside it (the state that flips to the back-face path), green
			// otherwise — so you see both its real size/orientation and when "camera inside" fires.
			var bb = new BBox( _curLocalMins, _curLocalMaxs );
			var cam = Scene.Camera;
			bool inside = cam.IsValid() && bb.Contains( tx.PointToLocal( cam.WorldPosition ) );
			DebugOverlay.Box( bb, inside ? Color.Red : Color.Green, duration: 0, transform: tx, overlay: true );
		}
	}



	// Packs the enabled brushes into the data texture and returns how many were written. Disabled brushes
	// (eye toggle off) are skipped so they vanish in the raymarch path too; order is preserved among the rest.
	// The layout lives in SdfBrushPacker — ONE definition shared with the GPU field baker, so the two packs
	// can't drift apart (a divergence here once shipped the extruded profile id to the field but not the march).
	int PackBrushes( List<SdfBrush> brushes )
	{
		var data = new float[MaxBrushes * TexelsPerBrush * 4];
		var spline = new float[MaxSplinePoints * 4]; // shared control-point pool (xyz local pos, w radius)

		// LOCAL-space pack (Transform.Zero) — the exact same data the GPU field baker consumes. The march
		// shader folds each world sample into the prop's local frame (ModelOrigin/ModelRotation) instead of
		// the brushes being baked to world, so both consumers share one evaluator (sdf_eval.hlsl) and the
		// packed data is placement-invariant.
		int written = SdfBrushPacker.Pack( brushes, global::Transform.Zero, data, spline, MaxBrushes, TexelsPerBrush, MaxSplinePoints, _textAtlas );

		_dataTex.Update<float>( data, 0, 0, MaxBrushes * TexelsPerBrush, 1 );
		_splineTex.Update<float>( spline, 0, 0, MaxSplinePoints, 1 );
		return written;
	}

	// AABB over the additive brushes in the object's LOCAL frame (no transform). The proxy box is
	// then oriented to the object, so a rotated/flat prop gets a tight box instead of a world cube.
	// Shares Sdf.TryGetBounds so every shape (incl. the spline tube) and symmetry stay in one place.
	static bool TryLocalBounds( List<SdfBrush> brushes, out Vector3 mins, out Vector3 maxs )
	{
		mins = default; maxs = default;
		if ( !Sdf.TryGetBounds( brushes, out var bb ) )
			return false;

		// Pad a touch so the surface never sits exactly on the box face.
		mins = bb.Mins - 2f;
		maxs = bb.Maxs + 2f;
		return true;
	}

	// Bounding-sphere radius about `center` (world). Derived from the (mirror-correct, spline-correct) local
	// AABB so every shape is covered: the distance to its farthest corner in world space, plus the AABB pad.
	static float WorldBoundingRadius( List<SdfBrush> brushes, Transform tx, Vector3 center )
	{
		if ( !Sdf.TryGetBounds( brushes, out var bb ) )
			return 2f;

		float r = 0f;
		for ( int i = 0; i < 8; i++ )
		{
			var corner = new Vector3(
				(i & 1) != 0 ? bb.Maxs.x : bb.Mins.x,
				(i & 2) != 0 ? bb.Maxs.y : bb.Mins.y,
				(i & 4) != 0 ? bb.Maxs.z : bb.Mins.z );
			r = MathF.Max( r, (tx.PointToWorld( corner ) - center).Length );
		}
		return r + 2f; // match TryLocalBounds' pad
	}

	// A low-poly UV sphere proxy, inflated slightly so the faceted hull still contains the true
	// bounding sphere. Normals/UVs are unused (the shader reconstructs them), so they're filler.
	static Mesh BuildSphere( Vector3 center, float radius, Material material )
	{
		const int lon = 16, lat = 10;
		float r = radius * 1.05f;

		var verts = new Vertex[(lat + 1) * (lon + 1)];
		int vi = 0;
		for ( int y = 0; y <= lat; y++ )
		{
			float theta = (y / (float)lat) * MathF.PI;
			float st = MathF.Sin( theta ), ct = MathF.Cos( theta );
			for ( int x = 0; x <= lon; x++ )
			{
				float phi = (x / (float)lon) * MathF.PI * 2f;
				var p = center + new Vector3( st * MathF.Cos( phi ), st * MathF.Sin( phi ), ct ) * r;
				verts[vi++] = new Vertex( p, Vector3.Up, Vector3.Forward, Vector4.Zero );
			}
		}

		int stride = lon + 1;
		var indices = new int[lat * lon * 6];
		int ii = 0;
		for ( int y = 0; y < lat; y++ )
		for ( int x = 0; x < lon; x++ )
		{
			int i0 = y * stride + x, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
			indices[ii++] = i0; indices[ii++] = i2; indices[ii++] = i1;
			indices[ii++] = i1; indices[ii++] = i2; indices[ii++] = i3;
		}

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( verts.Length, verts );
		mesh.CreateIndexBuffer( indices.Length, indices );
		mesh.Bounds = new BBox( center - r, center + r );
		return mesh;
	}

	// A box whose 8 corners come from the LOCAL AABB transformed to world — i.e. oriented to the
	// object. Also returns the world-space AABB enclosing it (for the shader march bracket + engine
	// frustum bounds, both of which are axis-aligned).
	static Mesh BuildOrientedBox( Vector3 lmins, Vector3 lmaxs, Transform tx, Material material, out BBox worldBounds )
	{
		var c = new Vector3[8];
		var wmin = new Vector3( float.MaxValue );
		var wmax = new Vector3( float.MinValue );

		for ( int i = 0; i < 8; i++ )
		{
			var local = new Vector3(
				(i & 1) != 0 ? lmaxs.x : lmins.x,
				(i & 2) != 0 ? lmaxs.y : lmins.y,
				(i & 4) != 0 ? lmaxs.z : lmins.z );

			c[i] = tx.PointToWorld( local );
			wmin = Vector3.Min( wmin, c[i] );
			wmax = Vector3.Max( wmax, c[i] );
		}

		worldBounds = new BBox( wmin, wmax );

		int[] faces =
		{
			0,1,3, 0,3,2,  4,6,7, 4,7,5,  // -Z, +Z
			0,4,5, 0,5,1,  2,3,7, 2,7,6,  // -Y, +Y
			0,2,6, 0,6,4,  1,5,7, 1,7,3,  // -X, +X
		};

		var verts = new Vertex[8];
		for ( int i = 0; i < 8; i++ )
			verts[i] = new Vertex( c[i], Vector3.Up, Vector3.Forward, Vector4.Zero );

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( 8, verts );
		mesh.CreateIndexBuffer( faces.Length, faces );
		mesh.Bounds = worldBounds;
		return mesh;
	}

	void DrawLiveFieldDebug( Transform tx )
	{
		var mn = _fieldGpu.Mins;
		var mx = _fieldGpu.Maxs;
		var dims = _fieldGpu.Dims;

		// Cyan = where the field is DEFINED (the evaluated volume).
		DebugOverlay.Box( new BBox( mn, mx ), Color.Cyan, duration: 0, transform: tx, overlay: true );

		// Surface bricks (the shell sparse storage would keep). Re-read occupancy only when the field changed AND at
		// most every ~12 frames: ReadOccupancy/DebugStats are synchronous GPU→CPU stalls, so running them every frame
		// while a brush is dragged (the field rebuilds each frame) flushes the pipeline and spikes the frame time.
		// This is sparse-only — the dense path has no occupancy/indirection to read back, hence no spike there.
		if ( _lastFieldHash != _dbgOccHash && ++_dbgReadCooldown >= 12 )
		{
			_dbgReadCooldown = 0;
			_dbgOcc = _fieldGpu.ReadOccupancy( out _dbgBx, out _dbgBy, out _dbgBz );
			_dbgOccHash = _lastFieldHash;
			if ( _dbgOcc is not null )
			{
				int occ = 0;
				foreach ( var v in _dbgOcc ) if ( v > 0.5f ) occ++;
				int total = _dbgBx * _dbgBy * _dbgBz;
				float pct = total > 0 ? 100f * occ / total : 0f;
				int gpuCount = _fieldGpu.ReadBrickCount(); // GPU tile allocator's count — should equal occ
				Log.Info( $"[SDF bricks] {occ}/{total} surface ({pct:0.#}%); GPU alloc={gpuCount} (should == {occ}); drops {total - occ}" );
				Log.Info( $"[SDF bricks] {_fieldGpu.DebugStats()}" ); // direct GPU-data inspection of indirection + atlas

				// Memory: atlas (surface shell) + guide field, vs what a dense volume at this resolution would cost.
				var ad = _fieldGpu.AtlasDims;
				var gd = _fieldGpu.Dims;
				var sd = _fieldGpu.SurfaceDims;
				float atlasMb = ad.x * ad.y * ad.z * 4f / (1024f * 1024f);
				float guideMb = gd.x * gd.y * gd.z * 4f / (1024f * 1024f);
				float denseMb = sd.x * sd.y * sd.z * 4f / (1024f * 1024f);
				Log.Info( $"[SDF mem] sparse: atlas {ad.x:0}×{ad.y:0}×{ad.z:0}={atlasMb:0.#}MB + guide {gd.x:0}³={guideMb:0.#}MB = {atlasMb + guideMb:0.#}MB; dense @ {sd.x:0}³ would be {denseMb:0.#}MB" );
			}
		}

		if ( _dbgOcc is null || _dbgBx <= 0 )
			return;

		// Skip the per-brick boxes at high resolution: thousands of DebugOverlay.Box calls PER FRAME would dominate the
		// frame and make the sparse path look slow (it isn't). This is a low-res inspection aid; above ~128 the shell
		// reads as a solid mass anyway, and the [SDF bricks]/[SDF mem] logs carry the useful numbers.
		if ( _dbgBx * _dbgBy * _dbgBz > 8192 )
			return;

		int blk = SdfFieldGpu.Block;
		float cx = dims.x > 1 ? (mx.x - mn.x) / (dims.x - 1) : 1f;
		float cy = dims.y > 1 ? (mx.y - mn.y) / (dims.y - 1) : 1f;
		float cz = dims.z > 1 ? (mx.z - mn.z) / (dims.z - 1) : 1f;
		var bcol = new Color( 0.2f, 1f, 0.4f, 1f );

		for ( int k = 0; k < _dbgBz; k++ )
		for ( int j = 0; j < _dbgBy; j++ )
		for ( int i = 0; i < _dbgBx; i++ )
		{
			if ( _dbgOcc[i + _dbgBx * (j + _dbgBy * k)] < 0.5f )
				continue;
			var blo = new Vector3( mn.x + i * blk * cx, mn.y + j * blk * cy, mn.z + k * blk * cz );
			var bhi = new Vector3(
				MathF.Min( mn.x + (i + 1) * blk * cx, mx.x ),
				MathF.Min( mn.y + (j + 1) * blk * cy, mx.y ),
				MathF.Min( mn.z + (k + 1) * blk * cz, mx.z ) );
			DebugOverlay.Box( new BBox( blo, bhi ), bcol, duration: 0, transform: tx, overlay: true );
		}
	}

	// ── Highlight-outline support (SdfHighlightOutline) ──────────────────────────────────────────
	// The outline is a SECOND translucent scene object over the same proxy geometry, whose shader
	// re-marches the BAKED FIELD ONLY (shaders/sdf_highlight.shader). Field-only keeps that shader
	// free of the analytic brush evaluator (no third copy of the brush list to keep in sync with
	// sdf_raymarch/sdf_eval); the trade is no highlight while UseFieldCache is off or the first
	// bake is still in flight. These members hand the highlight the proxy model, the march bracket
	// and the field bindings, mirrored from this renderer's per-frame state.

	/// <summary>The highlight this renderer is grouped under (registered by SdfHighlightOutline's
	/// target scan — self OR an ancestor). Pinged at the end of OnUpdate so the highlight's mirror
	/// is rebuilt from refreshed state; the LAST group member to update each frame leaves it fully
	/// same-frame coherent.</summary>
	internal SdfHighlightOutline Highlight;

	/// <summary>Opt out of any ancestor <see cref="SdfHighlightOutline"/>'s target scan. For renderers
	/// that live under an outlined pawn but aren't part of its silhouette — e.g. the hunter's
	/// first-person gun clone, an owner-only overlay that would waste one of the shader's few union
	/// slots (and can evict a real member). Runtime-driven like <see cref="RenderHidden"/>, not a
	/// [Property].</summary>
	public bool ExcludeFromHighlight { get; set; }

	/// <summary>Changes whenever the proxy geometry is rebuilt (brushes / transform / bounds mode).</summary>
	internal int ProxyVersion => _lastHash;

	/// <summary>Whether the highlight can include this renderer this frame: a live proxy and a valid baked field.</summary>
	internal bool HighlightReady => !_released && _so.IsValid() && _fieldReady && _fieldGpu is { IsValid: true };

	/// <summary>The highlight follows the surface's own hide flags (a concealed pawn body must not glow).
	/// Checked against the raw flags, NOT ForceHidden: with SdfShadows a RenderHidden body keeps its scene
	/// object enabled (shadow-only), but it still must not outline.</summary>
	internal bool HighlightVisible => !RenderHidden && !_fieldOnlyHidden;

	/// <summary>Padded local proxy bounds — the highlight unions these (through each member's
	/// transform) into its single group proxy box.</summary>
	internal Vector3 ProxyLocalMins => _curProxyMins;
	internal Vector3 ProxyLocalMaxs => _curProxyMaxs;

	/// <summary>Build the highlight group's single proxy box: lmins/lmaxs in tx-local space,
	/// oriented by tx (one CONVEX box for the whole group — overlapping per-member proxies would
	/// double-blend the translucent outline).</summary>
	internal static Model BuildHighlightProxyBox( Vector3 lmins, Vector3 lmaxs, Transform tx, Material mat, out BBox worldBb )
	{
		var mesh = BuildOrientedBox( lmins, lmaxs, tx, mat, out worldBb );
		return new ModelBuilder().AddMesh( mesh ).Create();
	}

	/// <summary>Mirror this renderer's field bindings + model fold into ONE SLOT of the highlight
	/// scene object (the highlight shader unions up to 4 slots). Must track what Refresh binds on
	/// the main scene object — the highlight samples the same field in the same local frame. The
	/// shared attributes (union bracket, march budget, colours) are set by the highlight itself.</summary>
	internal void ApplyHighlightAttributes( SceneObject so, int slot )
	{
		if ( _fieldGpu is not { IsValid: true } )
			return; // shouldn't happen — the highlight only assigns slots to HighlightReady members

		var tx = WorldTransform;
		var a = so.Attributes;

		a.Set( $"ModelOrigin{slot}", tx.Position );
		a.Set( $"ModelRotation{slot}", new Vector4( tx.Rotation.x, tx.Rotation.y, tx.Rotation.z, tx.Rotation.w ) );

		// Displacement (and its boil) is baked INTO the field now, so the outline tracks the lumpy
		// silhouette by construction — no per-sample noise to mirror. What remains is the step-safety
		// factor: a displaced field's slope is inflated by up to L = bound·freq·4, and the highlight's
		// march must understep by the same 1/(1+L) the main march uses. PER-SLOT, because one group can
		// mix a displaced member with a smooth one and the steepest member has to win.
		float gradL = 0f;
		// Active-gated: a dormant WhileDamaged boil mustn't tax the outline march with the
		// worst-tick understep. Re-pushed per frame, so an activation flip retightens it in step
		// with the field re-bake. A boil can also BORROW displacement while active (ForceDisplace) —
		// the same condition the field bake uses, so this bound tracks that field exactly.
		var boil = GameObject.Components.Get<ClayBoil>();
		bool boiling = boil is { Boiling: true } && boil.Fps > 0f;
		if ( Displace || (boiling && boil.ForceDisplace) )
		{
			float ampJitter = boiling ? MathF.Max( boil.AmpJitter, 0f ) : 0f;
			gradL = DispAmp * (1f + ampJitter * 0.5f) * DispFreq * 4f;
		}
		a.Set( $"DispGradL{slot}", gradL );

		a.Set( $"FieldTex{slot}", _fieldGpu.Texture );
		a.Set( $"FieldMin{slot}", _fieldGpu.Mins );
		a.Set( $"FieldMax{slot}", _fieldGpu.Maxs );
		a.Set( $"FieldDims{slot}", _fieldGpu.Dims );
		a.Set( $"SurfaceDims{slot}", _fieldGpu.SurfaceDims );

		if ( _fieldGpu.Atlas.IsValid() && _fieldGpu.IndirectionTex.IsValid() )
		{
			a.Set( $"Atlas{slot}", _fieldGpu.Atlas );
			a.Set( $"IndirectionTex{slot}", _fieldGpu.IndirectionTex );
			a.Set( $"BrickDims{slot}", _fieldGpu.BrickDims );
			a.Set( $"AtlasDims{slot}", _fieldGpu.AtlasDims );
			a.Set( $"AtlasEncode{slot}", _fieldGpu.AtlasEncodeBand ); // 8-bit tile decode band (0 = raw R32F)
		}
		a.Set( $"SdfSparse{slot}", EffectiveSparseField && _fieldGpu.Atlas.IsValid() ? 1 : 0 );
	}

	// The repack / field-dispatch change hash. Built on the ONE canonical per-brush hash (SdfBrush.HashInto) —
	// the same mixing SdfSculpture.ContentHash uses — so a new brush property added there is automatically
	// picked up here too, instead of silently rendering stale until someone notices the missing hash line.
	static int Hash( List<SdfBrush> brushes )
	{
		unchecked
		{
			int h = unchecked((int)2166136261);
			h = (h ^ brushes.Count) * 16777619;
			foreach ( var b in brushes )
				b.HashInto( ref h );
			return h;
		}
	}
}

