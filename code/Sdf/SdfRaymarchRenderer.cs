using System;
using System.Threading.Tasks;

namespace Mimiclay;

/// <summary>How the sibling meshed ModelRenderer is used while raymarching. The raymarch now writes
/// the depth+normals prepass itself (depth sorting, occlusion via DepthClamp, and AO), so the mesh's
/// only remaining job is casting shadows (the raymarch can't — its shadow-camera ray would be wrong).</summary>
public enum SdfMeshMode
{
	/// <summary>Invisible shadow caster (ShadowsOnly): casts exact shadows, never appears in the
	/// camera's colour pass. Default. (The name is historical — it no longer feeds a depth proxy.)</summary>
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
	const int MaxBrushes = 64;
	const int TexelsPerBrush = 7; // pos+shape, size+blend, quat, colour+op, rounding+metal+rough+mirrormask, aabbMin, aabbMax
	const int MaxSplinePoints = 256; // shared pool of spline control points (xyz world pos, w radius) across all spline brushes

	/// <summary>Material to render with. Leave empty to use the built-in sdf_raymarch shader.</summary>
	[Property] public Material Material { get; set; }

	[Property, Range( 16, 256 )] public int MaxSteps { get; set; } = 96;
	[Property, Range( 0.005f, 1f )] public float Epsilon { get; set; } = 0.05f;

	/// <summary>Bake the whole brush field to a 3D distance texture and march by sampling it (one trilinear
	/// fetch per step) instead of re-evaluating every brush each step. Decouples render cost from brush
	/// count — the headline win for multi-brush props (a 31-brush giraffe then marches like a 1-brush blob).
	/// Trilinear interpolation slightly rounds sharp corners; raise <see cref="FieldResolution"/> for crisper
	/// edges at the cost of memory + bake time. Off = the exact per-brush march.</summary>
	[Property, Group( "Distance Field" )] public bool UseFieldCache { get; set; } = true;

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

	/// <summary>Runtime override (NOT authored): while true, skip baking and force the exact per-brush march,
	/// even if <see cref="UseFieldCache"/> is on. The in-game sculpt editor sets this while a prop is being
	/// edited so each change shows instantly with no per-edit bake; clearing it on exit lets the settled shape
	/// bake once and switch to the cache. Static scene props never touch it, so they stay cached the whole time.</summary>
	public bool SuppressFieldCache { get; set; }

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

	/// <summary>Within this camera distance (measured in bounding-radii) the object is full quality.</summary>
	[Property, Group( "Adaptive Quality" )] public float FullQualityRadii { get; set; } = 6f;

	/// <summary>Beyond this camera distance (in bounding-radii) the object is at the quality floor. This
	/// is ALSO the SDF→mesh handoff: the raymarch turns off and mesh LOD0 takes over right here, so the
	/// SDF hands off exactly as it bottoms out (no separate "LOD0 kicks in" value to keep in sync).</summary>
	[Property, Group( "Adaptive Quality" )] public float MinQualityRadii { get; set; } = 60f;

	/// <summary>Paint each object by its LOD quality (red = floor/cheapest, yellow = mid, green = full).</summary>
	[Property, Group( "Adaptive Quality" )] public bool DebugLod { get; set; }

	// ── LOD / Distance: the single ordered distance chain (all in bounding-radii). Below MinQualityRadii
	// the SDF renders; from there to CullRadii the mesh renders (LOD0→1→2); past CullRadii it's culled.
	// The thresholds are clamped monotonic on use, so dragging one past another just pushes it — there's
	// never anything to "balance" across components. This component is the sole owner/driver of all of it.

	/// <summary>Master switch for the distance coordinator: drive SDF↔mesh↔cull and the mesh LOD from
	/// camera distance each frame. Off = static behaviour (SDF always on, MeshMode applied literally) for
	/// A/B testing.</summary>
	[Property, Group( "LOD / Distance" )] public bool DistanceSwitching { get; set; } = true;

	/// <summary>Camera distance (bounding-radii) at which the visible mesh drops from LOD0 to LOD1.
	/// Auto-clamped to be ≥ the SDF handoff (MinQualityRadii).</summary>
	[Property, Group( "LOD / Distance" )] public float MeshLod1Radii { get; set; } = 90f;

	/// <summary>Camera distance (bounding-radii) at which the mesh drops from LOD1 to LOD2. Auto-clamped ≥ LOD1.</summary>
	[Property, Group( "LOD / Distance" )] public float MeshLod2Radii { get; set; } = 140f;

	/// <summary>Camera distance (bounding-radii) past which the prop is culled entirely (no SDF, no mesh).
	/// Auto-clamped ≥ LOD2.</summary>
	[Property, Group( "LOD / Distance" )] public float CullRadii { get; set; } = 220f;

	/// <summary>Dead-band around every threshold (fraction of the threshold) so a prop sitting on a
	/// boundary doesn't flicker between states. 0.06 = ±6%.</summary>
	[Property, Group( "LOD / Distance" ), Range( 0f, 0.4f )] public float LodHysteresis { get; set; } = 0.06f;

	/// <summary>Label the current pipeline state (SDF / LOD0 / LOD1 / LOD2 / CULLED) over the prop in the
	/// scene view, so you can see exactly which stage the coordinator has picked.</summary>
	[Property, Group( "LOD / Distance" )] public bool DebugSwitchState { get; set; }

	/// <summary>What to do with the sibling meshed ModelRenderer while raymarching.
	/// <list type="bullet">
	/// <item><b>DepthProxy</b> (default): invisible ShadowsOnly mesh — casts shadows for the prop.</item>
	/// <item><b>Hidden</b>: disable it (no shadows).</item>
	/// <item><b>Visible</b>: show the mesh normally (the meshed path).</item>
	/// </list></summary>
	[Property] public SdfMeshMode MeshMode { get; set; } = SdfMeshMode.DepthProxy;

	/// <summary>Hide the rendered surface while still casting a shadow — first-person parity with a
	/// ModelRenderer set to ShadowsOnly. Forces the raymarched scene object off and the sibling mesh to
	/// ShadowsOnly, re-applied every frame AFTER the distance switch so it overrides whatever band the
	/// renderer is in. Used to hide a pawn's own SDF body on the machine that controls it. Not a
	/// [Property] — it's driven at runtime by the controlling pawn, not authored on the prefab.</summary>
	public bool RenderHidden { get; set; }

	// Any reason the raymarched surface should be off this frame. Every place that turns _so.RenderingEnabled ON
	// honours this, so a per-frame Refresh (the props edit constantly) can't override a hide. _fieldOnlyHidden is
	// included so this is a strict superset of the old "&& !_fieldOnlyHidden" guard.
	bool ForceHidden => RenderHidden || _fieldOnlyHidden;

	/// <summary>Overdraw optimisation: skip the redundant back-face march (the box draws both faces)
	/// and use conservative depth so the GPU keeps early-Z and rejects hidden fragments before
	/// marching. Toggle off to A/B the cost.</summary>
	[Property, Group( "Overdraw" )] public bool OverdrawOptimization { get; set; } = true;

	/// <summary>Per-brush + per-spline-segment AABB early-out in the analytic march: at each step, skip a
	/// brush's (or spline span's) distance math when its bounds are too far to change the result. Only affects
	/// the per-brush path (off when the field cache is active) — toggle to A/B its cost while editing a
	/// multi-brush or high-segment-count prop.</summary>
	[Property, Group( "Overdraw" )] public bool BrushCulling { get; set; } = true;

	/// <summary>Render a tight bounding-SPHERE proxy instead of the AABB box, so round props march
	/// fewer wasted pixels around their silhouette. Best for round/blobby props; an AABB box can be
	/// tighter for long/thin props, so toggle off for those.</summary>
	[Property, Group( "Overdraw" )] public bool TightBounds { get; set; }

	/// <summary>Early-bail the forward march for pixels occluded by something already in the depth
	/// buffer. Now that the raymarch writes the depth prepass, occlusion is already correct via the
	/// hardware depth test — this is only a march-cost optimisation, and reading the SDF's own
	/// marched depth back out of the (Hi-Z) chain causes speckle at silhouettes, so it's OFF by
	/// default. The real perf path is reconstruct-from-depth (no forward re-march at all).</summary>
	[Property, Group( "Overdraw" )] public bool DepthClamp { get; set; }

	/// <summary>Lumpy plasticine displacement on the raymarched surface — bends the SILHOUETTE, not
	/// just the lighting (the meshed LOD is unaffected). Costs extra march steps (the field needs a
	/// safety understep), so it's a toggle. The LOOK (lump depth/density) is tuned on the MATERIAL,
	/// alongside the transmission and curvature-shading params.</summary>
	[Property, Group( "Displacement" )] public bool Displace { get; set; }

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
	SdfMeshMode _lastMeshMode = (SdfMeshMode)(-1);
	Vector3 _curMins, _curMaxs;       // world AABB (shader march bracket + frustum bounds)
	Vector3 _curLocalMins, _curLocalMaxs; // local AABB (the oriented proxy box, for debug draw)
	Vector3 _curCenter;
	float _curRadius;
	int _curCount;
	bool _forceRepack;
	bool _released; // set when we've torn down _so; stops OnUpdate/Refresh from resurrecting it

	bool _destroyed; // set in OnDestroy; cancels the deferred sibling-mesh hand-back during a delete

	// Live-edit field (Dreams "evaluator / CS of doom"): a per-instance volume re-evaluated on the GPU by a
	// compute shader while this prop is being edited (SuppressFieldCache), so the march stays on D_FIELD_TEX
	// instead of the analytic per-brush path — with no CPU eval and no upload. Null when settled (shared bake).
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
	bool _lastDistanceSwitching = true;

	Material ActiveMaterial => Material ?? (_fallbackMaterial ??= Material.FromShader( "shaders/sdf_raymarch.shader" ));

	protected override void OnEnabled()
	{
		_released = false; // re-enabling revives a previously released renderer
		_destroyed = false;
		_meshRenderer = GameObject.Components.Get<ModelRenderer>();
		ApplyMeshMode();

		_lastHash = 0;
		Refresh();
	}

	// Configure the sibling mesh per MeshMode. DepthProxy = ShadowsOnly: it casts exact (unshrunk)
	// shadows and stays out of the camera's colour/depth passes. The raymarch handles its own
	// depth+normals prepass now, so the mesh's only job here is shadows.
	void ApplyMeshMode()
	{
		if ( !_meshRenderer.IsValid() )
			_meshRenderer = GameObject.Components.Get<ModelRenderer>();
		if ( !_meshRenderer.IsValid() )
			return;

		switch ( MeshMode )
		{
			case SdfMeshMode.DepthProxy:
				_meshRenderer.Enabled = true;
				_meshRenderer.MaterialOverride = null;
				_meshRenderer.RenderType = ModelRenderer.ShadowRenderType.ShadowsOnly;
				break;
			case SdfMeshMode.Hidden:
				_meshRenderer.Enabled = false;
				_meshRenderer.MaterialOverride = null;
				_meshRenderer.RenderType = ModelRenderer.ShadowRenderType.On;
				break;
			default: // Visible
				_meshRenderer.Enabled = true;
				_meshRenderer.MaterialOverride = null;
				_meshRenderer.RenderType = ModelRenderer.ShadowRenderType.On;
				break;
		}

		_lastMeshMode = MeshMode;
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
		_lastMeshMode = (SdfMeshMode)(-1);

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
		var mesh = _meshRenderer;

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
		// Toggling the coordinator off restores the static behaviour (mesh per MeshMode, SDF on).
		if ( DistanceSwitching != _lastDistanceSwitching )
		{
			_lastDistanceSwitching = DistanceSwitching;
			if ( !DistanceSwitching )
				RestoreStaticState();
		}

		Refresh();

		// Runtime: drive the switch from the game camera. In the editor there's no Scene.Camera for the
		// scene view, so that path runs from DrawGizmos (Gizmo.Camera) instead — see below.
		if ( DistanceSwitching && !_released && !Scene.IsEditor )
		{
			var cam = Scene.Camera;
			if ( cam.IsValid() )
				UpdateDistanceSwitch( cam.WorldPosition );
		}

		// Last word on visibility, after the distance switch / Refresh have set their band state.
		ApplyRenderHidden();
		ApplyFieldOnlyHide();
	}

	// Invisible-but-shadow-casting override (see RenderHidden). Runs after the per-frame band logic so it
	// wins regardless of which LOD band the renderer landed in this frame.
	void ApplyRenderHidden()
	{
		if ( !RenderHidden )
			return;

		if ( _so.IsValid() )
			_so.RenderingEnabled = false;

		if ( !_meshRenderer.IsValid() )
			_meshRenderer = GameObject.Components.Get<ModelRenderer>();
		if ( _meshRenderer.IsValid() )
		{
			_meshRenderer.Enabled = true;
			_meshRenderer.RenderType = ModelRenderer.ShadowRenderType.ShadowsOnly;
		}
	}

	// FieldCacheOnly hide (see _fieldOnlyHidden): force the raymarched surface off when there's no field ready,
	// so the analytic march never appears. Only ever hides — never re-enables — so it composes with the distance
	// switch and RenderHidden (whichever wants it off, it stays off). Runs last in OnUpdate.
	void ApplyFieldOnlyHide()
	{
		if ( _fieldOnlyHidden && _so.IsValid() )
			_so.RenderingEnabled = false;
	}

	// Editor: the scene-view camera is only reachable as Gizmo.Camera inside a gizmo scope, so the
	// coordinator runs here in edit mode (Refresh already ran in OnUpdate this frame). Also draws the
	// optional state label. Runs every frame while scene gizmos are enabled.
	protected override void DrawGizmos()
	{
		if ( !DistanceSwitching || _released || !Scene.IsEditor )
			return;

		UpdateDistanceSwitch( Gizmo.Camera.Position );

		if ( DebugSwitchState && _curRadius > 0.01f )
		{
			string label = _switchState switch
			{
				0 => "SDF",
				1 => "LOD0",
				2 => "LOD1",
				3 => "LOD2",
				_ => "CULLED",
			};

			using ( Gizmo.Scope( "sdf-switch-label" ) )
			{
				Gizmo.Draw.Color = Color.Black;
				Gizmo.Draw.Text( label, new Transform( _curCenter ), size: 24 );
			}
		}
	}

	// Resolve the pipeline stage from camera distance and apply it: SDF on/off, sibling mesh
	// visible/shadows-only/disabled, and the forced mesh LOD. One ratio drives all of it, so the label
	// can never disagree with what's drawn, and there's a single set of thresholds to tune.
	void UpdateDistanceSwitch( Vector3 viewPos )
	{
		if ( !_so.IsValid() || _curRadius <= 0.01f )
			return;

		if ( !_meshRenderer.IsValid() )
			_meshRenderer = GameObject.Components.Get<ModelRenderer>();

		float ratio = (viewPos - _curCenter).Length / _curRadius;
		float h = LodHysteresis;

		// Effective thresholds, clamped monotonic so the inspector values can be in any order.
		float tHandoff = MathF.Max( MinQualityRadii, FullQualityRadii );
		float tLod1 = MathF.Max( MeshLod1Radii, tHandoff );
		float tLod2 = MathF.Max( MeshLod2Radii, tLod1 );
		float tCull = MathF.Max( CullRadii, tLod2 );

		_aboveHandoff = Above( ratio, tHandoff, _aboveHandoff, h );
		_aboveLod1 = Above( ratio, tLod1, _aboveLod1, h );
		_aboveLod2 = Above( ratio, tLod2, _aboveLod2, h );
		_aboveCull = Above( ratio, tCull, _aboveCull, h );

		bool culled = _aboveCull;
		bool sdf = !culled && !_aboveHandoff;
		int meshLod = _aboveLod2 ? 2 : _aboveLod1 ? 1 : 0;

		// SDF scene object renders only in its band (and not while any hide flag is set).
		_so.RenderingEnabled = sdf && !ForceHidden;

		if ( _meshRenderer.IsValid() )
		{
			if ( culled )
			{
				_meshRenderer.Enabled = false;
			}
			else if ( sdf )
			{
				// SDF is the visible surface; the mesh only casts shadows (unless MeshMode hides it).
				_meshRenderer.Enabled = MeshMode != SdfMeshMode.Hidden;
				_meshRenderer.RenderType = ModelRenderer.ShadowRenderType.ShadowsOnly;
				_meshRenderer.LodOverride = null;
			}
			else
			{
				// Mesh band: show the mesh at the LOD this distance selects.
				_meshRenderer.Enabled = true;
				_meshRenderer.RenderType = ModelRenderer.ShadowRenderType.On;
				_meshRenderer.LodOverride = meshLod;
			}
		}

		_switchState = culled ? 4 : sdf ? 0 : 1 + meshLod;
	}

	// Hysteresis gate: returns whether `ratio` is above `threshold`, but only flips state once the ratio
	// has cleared the threshold by ±h (the dead-band), so a prop parked on a boundary doesn't flicker.
	static bool Above( float ratio, float threshold, bool wasAbove, float h )
	{
		if ( wasAbove )
			return ratio >= threshold * (1f - h); // stay above until it drops below the lower edge
		return ratio > threshold * (1f + h);       // go above only once past the upper edge
	}

	// Undo the coordinator's per-frame state when DistanceSwitching is turned off, so the renderer
	// returns to its static MeshMode behaviour instead of being stuck wherever the switch left it.
	void RestoreStaticState()
	{
		if ( _meshRenderer.IsValid() )
			_meshRenderer.LodOverride = null;
		ApplyMeshMode();
		if ( _so.IsValid() )
			_so.RenderingEnabled = !ForceHidden;
	}

	/// <summary>Force a full repack + rebuild, ignoring the change-hash.</summary>
	[Button( "Refresh" )]
	public void ForceRefresh()
	{
		_forceRepack = true;
		ApplyMeshMode();
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

		if ( MeshMode != _lastMeshMode )
			ApplyMeshMode();

		EnsureResources();
		var tx = WorldTransform;

		// Rebuild geometry + packed brush data only when the brushes / transform change.
		int hash = HashCode.Combine( Hash( brushes ), tx.Position, tx.Rotation, Material, TightBounds );
		if ( hash != _lastHash || !_so.IsValid() || _forceRepack )
		{
			// Bounds computed in the object's LOCAL frame, so the proxy can be ORIENTED to the object
			// instead of a world-axis-aligned cube (a rotated/flat prop's world AABB is hugely bloated).
			if ( !TryLocalBounds( brushes, out var lmins, out var lmaxs ) )
				return;

			_forceRepack = false;
			_lastHash = hash;
			_curCount = PackBrushes( brushes, tx );

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
				bool liveBounds = UseFieldCache; // the GPU field is padded by BlendPad, so grow the proxy to enclose it
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
				_so = new SceneObject( Scene.SceneWorld, model );
				// Per-object attributes (BrushData/Bounds/Count) don't survive batching, so two
				// SDF objects sharing the material would collapse into one draw and hide together.
				_so.Batchable = false;
				// The raymarch writes the camera depth+normals prepass (for AO + depth sorting), but
				// it must NOT cast shadows: the shadow camera is orthographic and our ray
				// reconstruction assumes perspective, so it'd cast wrong shadows. The sibling mesh
				// (ShadowsOnly) handles shadows instead.
				_so.Flags.CastShadows = false;
			}

			_so.Bounds = worldBb;
		}

		// Re-apply ALL per-object attributes every frame. If a binding is dropped (e.g. when
		// selection rebinds the shared material), BrushCount reads 0 -> this clip()-heavy
		// shader discards every pixel and the object vanishes while still "valid". Setting
		// them each frame restores it immediately.
		_so.RenderingEnabled = !ForceHidden;
		_so.Attributes.Set( "BrushData", _dataTex );
		_so.Attributes.Set( "SplineData", _splineTex );
		_so.Attributes.Set( "TextSdf", _textAtlas.Texture );
		_so.Attributes.Set( "BrushCount", _curCount );
		_so.Attributes.Set( "BoundsMin", _curMins );
		_so.Attributes.Set( "BoundsMax", _curMaxs );
		_so.Attributes.Set( "BoundsCenter", _curCenter );
		_so.Attributes.Set( "BoundsRadius", _curRadius );
		// Object placement, so the shader can re-base the triplanar projection into model space (the
		// plasticine texture stays locked to the prop). Brushes are still baked to world for the march;
		// only the surface texturing uses this. Rotation-only inverse in the shader assumes unit scale.
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
		_so.Attributes.SetCombo( "D_OVERDRAW_OPT", OverdrawOptimization ? 1 : 0 );
		_so.Attributes.Set( "SdfCull", BrushCulling ? 1 : 0 ); // runtime uniform, not a combo (a 7th combo crashed the Vfx compiler)
		_so.Attributes.SetCombo( "D_TIGHT_BOUNDS", TightBounds ? 1 : 0 );
		_so.Attributes.SetCombo( "D_DEPTH_CLAMP", DepthClamp ? 1 : 0 );
		// Displacement look (amp/freq) and curvature shading are MATERIAL params now — only the combo
		// toggle lives here.
		_so.Attributes.SetCombo( "D_DISPLACE", Displace ? 1 : 0 );

		// Transmission look (tint, strength, thickness) lives on the material; this just gates the combo.
		_so.Attributes.SetCombo( "D_TRANSMISSION", Transmission ? 1 : 0 );

		// Field cache (D_FIELD_TEX): the GPU compute evaluator (SdfFieldGpu) writes this prop's distance volume,
		// which the march samples. Re-dispatched only when brushes change, so a static prop pays a single dispatch
		// and an edited one updates instantly. The shared CPU bake (SdfFieldBaker) is retired for now (see the note
		// in that file); it can return later to share one texture across many identical clones if that's worth it.
		bool fieldReady = false;
		if ( UseFieldCache && _curCount > 0 && _curRadius > 0.01f )
		{
			_fieldGpu ??= new SdfFieldGpu();
			// Re-dispatch when anything the build depends on changes: the brushes, the resolution, OR the sparse
			// toggle. Folding the field parameters into the hash means tweaking them in the inspector updates the
			// field live, instead of only when a brush is moved.
			int fieldHash = HashCode.Combine( Hash( brushes ), FieldResolution, SparseField );
			if ( fieldHash != _lastFieldHash || !_fieldGpu.IsValid )
			{
				if ( _fieldGpu.Evaluate( brushes, _curLocalMins, _curLocalMaxs, FieldResolution, SparseField ) )
					_lastFieldHash = fieldHash;
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
				}
				_so.Attributes.Set( "SdfSparse", SparseField && _fieldGpu.Atlas.IsValid() ? 1 : 0 );

				fieldReady = true;
			}
		}
		_so.Attributes.SetCombo( "D_FIELD_TEX", fieldReady ? 1 : 0 );

		if ( DebugLiveField && _fieldGpu is { IsValid: true } )
			DrawLiveFieldDebug( tx );

		// FieldCacheOnly: suppress the analytic march entirely. When the field path is active but nothing's
		// ready yet (first build in flight), hide the surface this frame rather than revealing the brush loop —
		// applied as the last word on visibility in OnUpdate/UpdateDistanceSwitch.
		_fieldOnlyHidden = FieldCacheOnly && UseFieldCache && _curCount > 0 && _curRadius > 0.01f && !fieldReady;

		if ( DebugLiveField )
		{
			bool valid = _fieldGpu is { IsValid: true };
			var fsz = valid ? (_fieldGpu.Maxs - _fieldGpu.Mins) : Vector3.Zero;
			var psz = _curMaxs - _curMins;
			bool ren = _so.IsValid() && _so.RenderingEnabled;
			string st = $"[SDF live] suppress={SuppressFieldCache} valid={valid} ready={fieldReady} hidden={_fieldOnlyHidden} render={ren} field={fsz} proxy={psz}";
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
	int PackBrushes( List<SdfBrush> brushes, Transform tx )
	{
		var data = new float[MaxBrushes * TexelsPerBrush * 4];
		var spline = new float[MaxSplinePoints * 4]; // shared control-point pool (xyz world pos, w radius)

		// tx = WorldTransform: this pack is WORLD-space (the proxy march runs in world), unlike the field
		// baker's local-space pack (Transform.Zero).
		int written = SdfBrushPacker.Pack( brushes, tx, data, spline, MaxBrushes, TexelsPerBrush, MaxSplinePoints, _textAtlas );

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

	static int Hash( List<SdfBrush> brushes )
	{
		var h = new HashCode();
		h.Add( brushes.Count );
		foreach ( var b in brushes )
		{
			h.Add( (int)b.Shape );
			h.Add( (int)b.CrossSection ); // extruded profile swap must repack + re-bake the field
			h.Add( b.Text ); h.Add( b.Font ); // text brushes re-bake their glyph field
			h.Add( (int)b.Operation );
			h.Add( b.Enabled );
			h.Add( b.Position );
			h.Add( b.Size );
			h.Add( b.Rotation );
			h.Add( b.Blend );
			h.Add( b.Rounding );
			h.Add( b.Slice );
			h.Add( b.Color );
			h.Add( b.Metallic );
			h.Add( b.Roughness );
			h.Add( b.MirrorX );
			h.Add( b.MirrorY );
			h.Add( b.MirrorZ );
			if ( b.Points is { } pts )
			{
				h.Add( pts.Count );
				foreach ( var pt in pts )
					h.Add( pt );
				h.Add( b.Curvature );
				h.Add( b.SplineClosed );
			}
		}
		return h.ToHashCode();
	}
}
