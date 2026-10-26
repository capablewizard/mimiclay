using System;

namespace Mimiclay;

/// <summary>
/// A shared set of render-quality settings for <see cref="SdfRaymarchRenderer"/>: march quality, the distance
/// (LOD) chain, the overdraw switches, and how densely the prop's distance field is sampled.
///
/// Why it exists: s&amp;box serializes every [Property] into each prefab and scene, so a better code default never
/// reached the props already authored — by 2026-10 the library carried three generations of quality values (most
/// props had adaptive quality and the mesh hand-off switched off, the two biggest measured wins). The renderer now
/// stores none of these. It reads them from a profile, and only a per-prop override (a nullable property in the
/// renderer's "Quality Overrides" group) can diverge, so a divergence is rare and visible.
///
/// Shipped profiles (Assets/sdf/quality): <see cref="Standard"/> (every ordinary prop, and the default when a
/// renderer names none), <see cref="Player"/> (pawn heads and bodies), <see cref="Viewmodel"/> (the first-person
/// gun, also picked automatically for any non-Normal <see cref="SdfRaymarchRenderer.ViewLayer"/>) and
/// <see cref="Editing"/>, applied automatically while the LOCAL player sculpts the prop.
///
/// Field density (see <see cref="ContentVoxels(float, float, out bool)"/>): the voxel budget counts the CONTENT's
/// longest axis only — the field's padding is added on top at the same voxel size, so it no longer eats a large
/// prop's budget.
///
/// The property defaults are Standard's values, chosen by measurement on 2026-10-25 (SDF-OPTIMISATION-PLAN.md,
/// "Quality profiles"): the prop generators' 50 steps / 0.18 epsilon (13–27% cheaper than 96 / 0.05 in the `ship`
/// suite, ~1 px of silhouette difference) and 15-radii mesh hand-off (25–85% cheaper than 60 radii from the `lod`
/// suite's overview range out), and a 0.625" voxel, which with the 256 dense limit matches the old 128-capped cost
/// in the uncapped `shader` suite.
/// </summary>
[AssetType( Name = "SDF Quality Profile", Extension = "sdfq", Category = "SDF" )]
public sealed class SdfQualityProfile : GameResource
{
	// ── March ─────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Sphere-trace step budget at full quality.</summary>
	[Property, Group( "March" ), Range( 16, 256 )] public int MaxSteps { get; set; } = 50;

	/// <summary>Hit threshold (inches) at full quality.</summary>
	[Property, Group( "March" ), Range( 0.005f, 1f )] public float Epsilon { get; set; } = 0.18f;

	// ── Adaptive quality ──────────────────────────────────────────────────────────────────────────────

	/// <summary>Ramp the step count and epsilon down with distance (a pure-SDF LOD): full quality inside
	/// <see cref="FullQualityRadii"/>, the floor (<see cref="MinSteps"/> / <see cref="FarEpsilon"/>) at
	/// <see cref="MinQualityRadii"/>.</summary>
	[Property, Group( "Adaptive Quality" )] public bool AdaptiveQuality { get; set; } = true;

	/// <summary>Step-count floor for far props. Practical minimum ~2–4 with a large <see cref="FarEpsilon"/>.</summary>
	[Property, Group( "Adaptive Quality" ), Range( 1, 128 )] public int MinSteps { get; set; } = 12;

	/// <summary>Hit threshold (inches) at the floor. Bigger = cheaper and softer.</summary>
	[Property, Group( "Adaptive Quality" ), Range( 0.01f, 16f )] public float FarEpsilon { get; set; } = 1f;

	// ── LOD / Distance — one ordered chain, in BOUNDING RADII (camera distance ÷ the prop's radius) ───────
	// FullQuality ≤ MeshHandoff (= MinQualityRadii) ≤ Lod1 ≤ Lod2 ≤ Cull. The renderer resolves the chain
	// monotonically (each link at least the one before it), and the drift check flags an out-of-order one.

	/// <summary>Inside this the march runs at full quality.</summary>
	[Property, Group( "LOD / Distance" ), Title( "Full Quality (radii)" )] public float FullQualityRadii { get; set; } = 1f;

	/// <summary>The SDF→mesh hand-off, and where adaptive quality bottoms out (one distance, by design).</summary>
	[Property, Group( "LOD / Distance" ), Title( "Mesh Handoff (radii)" )] public float MinQualityRadii { get; set; } = 15f;

	/// <summary>Drive SDF↔mesh↔cull and the mesh LOD from camera distance. Off = always raymarched.</summary>
	[Property, Group( "LOD / Distance" )] public bool DistanceSwitching { get; set; } = true;

	[Property, Group( "LOD / Distance" ), Title( "Mesh LOD 1 (radii)" )] public float MeshLod1Radii { get; set; } = 20f;
	[Property, Group( "LOD / Distance" ), Title( "Mesh LOD 2 (radii)" )] public float MeshLod2Radii { get; set; } = 30f;
	[Property, Group( "LOD / Distance" ), Title( "Cull (radii)" )] public float CullRadii { get; set; } = 200f;

	/// <summary>Dead-band around every threshold (fraction of it), so a prop on a boundary doesn't flicker.</summary>
	[Property, Group( "LOD / Distance" ), Range( 0f, 0.4f )] public float LodHysteresis { get; set; } = 0.06f;

	/// <summary>Beyond this a settled prop casts its shadow with the mesh instead of marching it (0 = never).
	/// Capped globally by mimiclay_sdf_shadow_radii_cap; at most the mesh hand-off.</summary>
	[Property, Group( "LOD / Distance" ), Title( "Shadow Handoff (radii)" ), Range( 0f, 64f )] public float SdfShadowRadii { get; set; } = 3f;

	// ── Overdraw ──────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Skip the back-face march and keep early-Z (conservative depth). Auto-off with the camera
	/// within 2 radii of the prop.</summary>
	[Property, Group( "Overdraw" )] public bool OverdrawOptimization { get; set; } = true;

	/// <summary>Per-brush AABB early-out on the analytic (no-field) march.</summary>
	[Property, Group( "Overdraw" )] public bool BrushCulling { get; set; } = true;

	/// <summary>Clamp the forward march against the prepass depth (hidden props stop marching).</summary>
	[Property, Group( "Overdraw" )] public bool DepthOcclusionCull { get; set; } = true;

	// ── Field density ─────────────────────────────────────────────────────────────────────────────────

	/// <summary>Target WORLD voxel size (inches). Scaled globally by mimiclay_sdf_voxel_scale.</summary>
	[Property, Group( "Field Density" ), Range( 0.05f, 4f )] public float VoxelSize { get; set; } = 0.625f;

	/// <summary>Floor on the content voxel count (longest axis), so a small prop is never starved.</summary>
	[Property, Group( "Field Density" ), Range( 8, 512 )] public int MinVoxels { get; set; } = 32;

	/// <summary>Content voxels past this count half (see <see cref="ContentVoxels(float, float, out bool)"/>).</summary>
	[Property, Group( "Field Density" ), Range( 8, 512 )] public int KneeVoxels { get; set; } = 160;

	/// <summary>Hard ceiling on the content voxel count (after the taper).</summary>
	[Property, Group( "Field Density" ), Range( 8, 512 )] public int MaxVoxels { get; set; } = 256;

	// ── Mesh LODs (the surface-nets meshes the prop hands off to; LOD1 / LOD2 mesh at ½ and ¼ of this) ──────
	// Same shape of rule as the field density: cells along the brushes' longest axis at a WORLD cell size,
	// half-rate past the knee, clamped (the mesher's own ceiling is 96). Replaced SdfSculpture's per-prefab
	// Resolution (a count, so a big prop meshed at 5" cells and lost its detail at the hand-off).

	/// <summary>Target world cell size (inches) for mesh LOD0.</summary>
	[Property, Group( "Mesh LODs" ), Title( "Cell Size (in)" ), Range( 0.1f, 8f )] public float MeshCellSize { get; set; } = 1.5f;

	/// <summary>Floor on LOD0's cell count, so small props keep their shape.</summary>
	[Property, Group( "Mesh LODs" ), Title( "Min Cells" ), Range( 8, 96 )] public int MeshMinCells { get; set; } = 32;

	/// <summary>Cells past this count half.</summary>
	[Property, Group( "Mesh LODs" ), Title( "Knee Cells" ), Range( 8, 96 )] public int MeshKneeCells { get; set; } = 64;

	/// <summary>Ceiling on LOD0's cell count (the mesher caps at 96).</summary>
	[Property, Group( "Mesh LODs" ), Title( "Max Cells" ), Range( 8, 96 )] public int MeshMaxCells { get; set; } = 96;

	/// <summary>LOD0's cell count for a prop whose brushes span <paramref name="longest"/> world inches.</summary>
	public int MeshResolution( float longest )
	{
		int max = Math.Clamp( MeshMaxCells, 8, 96 );
		int min = Math.Clamp( MeshMinCells, 8, max );
		int knee = Math.Clamp( MeshKneeCells, min, max );
		return (int)MathF.Round( ContentVoxels( longest, MathF.Max( MeshCellSize, 0.05f ), min, knee, max, out _ ) );
	}

	// ── Debug (every prop on this profile; a renderer's own DebugLod still works for one prop) ──────────────
	// Saved into the asset like any other value, so switch them off before committing a profile
	// (Tools/sdf/validate-quality-drift.csx flags a shipped profile with either on).

	/// <summary>Show where each prop sits in the distance chain: while raymarched, the in-shader quality heatmap
	/// (red = adaptive-quality floor → green = full quality); once handed off, a flat tint per mesh band (cyan LOD0,
	/// blue LOD1, magenta LOD2).</summary>
	[Property, Group( "Debug" ), Title( "Quality / Hand-off Colours" )] public bool DebugLod { get; set; }

	/// <summary>Also draw each prop's band label: the band, the bounding-radii ratio that picked it, the live
	/// steps/epsilon, who casts the shadow, and the field it samples. Busy with many props on screen.</summary>
	[Property, Group( "Debug" ), Title( "Band Labels" )] public bool DebugLodLabels { get; set; }

	/// <summary>The content voxel count for a prop whose longest content axis spans <paramref name="content"/>
	/// at the target <paramref name="cell"/> size (both local units). Up to <see cref="KneeVoxels"/> the count is
	/// exact (content ÷ cell); past it every further voxel counts half, so a prop twice the knee's size gets 1.5×
	/// the knee rather than 2×. Then clamped to [<see cref="MinVoxels"/>, <see cref="MaxVoxels"/>].
	/// <paramref name="exact"/> = the target cell is used as-is (inside the knee and the clamps): the voxel size is
	/// then bounds-independent, so a drag that grows the bounds adds voxels instead of rescaling the field.</summary>
	public float ContentVoxels( float content, float cell, out bool exact )
	{
		int max = Math.Max( MaxVoxels, 8 );
		int min = Math.Clamp( MinVoxels, 1, max );
		int knee = Math.Clamp( KneeVoxels, min, max );
		return ContentVoxels( content, cell, min, knee, max, out exact );
	}

	/// <summary>The density rule itself (see the instance overload), for explicit knobs.</summary>
	public static float ContentVoxels( float content, float cell, int min, int knee, int max, out bool exact )
	{
		float n = content / MathF.Max( cell, 1e-4f );
		float tapered = n <= knee ? n : knee + (n - knee) * 0.5f;
		float clamped = Math.Clamp( tapered, min, max );
		exact = clamped == n;
		return clamped;
	}

	// ── The shipped profiles ──────────────────────────────────────────────────────────────────────────

	public const string StandardPath = "sdf/quality/standard.sdfq";
	public const string PlayerPath = "sdf/quality/player.sdfq";
	public const string ViewmodelPath = "sdf/quality/viewmodel.sdfq";
	public const string EditingPath = "sdf/quality/editing.sdfq";

	static SdfQualityProfile _standard, _player, _viewmodel, _editing;
	static SdfQualityProfile _standardFallback, _playerFallback, _viewmodelFallback, _editingFallback;

	/// <summary>The project default: every renderer that names no profile.</summary>
	public static SdfQualityProfile Standard => Load( ref _standard, ref _standardFallback, StandardPath, () => new() );

	/// <summary>Pawn heads and bodies: worn on screen at close range.</summary>
	public static SdfQualityProfile Player => Load( ref _player, ref _playerFallback, PlayerPath,
		() => Fine( new() { VoxelSize = 0.3f, MinVoxels = 48, KneeVoxels = 192, MaxVoxels = 256, MeshCellSize = 1f } ) );

	/// <summary>The first-person gun: ~20 units from the camera, never far, never handed to a mesh.</summary>
	public static SdfQualityProfile Viewmodel => Load( ref _viewmodel, ref _viewmodelFallback, ViewmodelPath,
		() => Fine( new() { VoxelSize = 0.15f, MinVoxels = 64, KneeVoxels = 256, MaxVoxels = 384, DistanceSwitching = false, AdaptiveQuality = false, OverdrawOptimization = false } ) );

	/// <summary>Applied automatically while the LOCAL edit session is sculpting the prop.</summary>
	public static SdfQualityProfile Editing => Load( ref _editing, ref _editingFallback, EditingPath,
		() => Fine( new() { VoxelSize = 0.2f, MinVoxels = 64, KneeVoxels = 256, MaxVoxels = 384 } ) );

	// The close-up profiles march at the finer pre-profile code defaults (the property defaults above are Standard's).
	static SdfQualityProfile Fine( SdfQualityProfile p )
	{
		p.MaxSteps = 96; p.Epsilon = 0.05f; p.MinSteps = 28; p.FarEpsilon = 0.4f;
		p.FullQualityRadii = 6f; p.MinQualityRadii = 60f; p.MeshLod1Radii = 90f; p.MeshLod2Radii = 140f; p.CullRadii = 220f;
		p.SdfShadowRadii = 10f;
		return p;
	}

	// Loaded by path on first use (GetAll only returns resources something already loaded). The code fallback
	// only covers a missing asset (a stripped package, a broken compile) and the load is retried while it's in
	// use. The assets are the source of truth, so tune THEM; keep the fallbacks roughly in step so a missing
	// asset degrades gracefully, not wildly.
	static SdfQualityProfile Load( ref SdfQualityProfile slot, ref SdfQualityProfile fallbackSlot, string path, Func<SdfQualityProfile> fallback )
	{
		if ( slot is not null )
			return slot;

		slot = ResourceLibrary.Get<SdfQualityProfile>( path );
		if ( slot is not null )
			return slot;

		if ( fallbackSlot is null )
		{
			Log.Warning( $"[sdf] quality profile '{path}' not found — using the built-in fallback" );
			fallbackSlot = fallback();
		}
		return fallbackSlot;
	}
}
