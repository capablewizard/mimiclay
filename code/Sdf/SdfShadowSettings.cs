namespace Mimiclay;

/// <summary>
/// Global quality/cost settings for how SDF props march their own SHADOWS (sun cascades and spot/point-light faces).
/// One place for every prop — these were per-prop properties, which let props drift apart. Change a default here, or
/// try a value live with the ConVar (e.g. <c>mimiclay_sdf_shadow_texel_eps_local 3</c>), then A/B it:
/// <c>sdf_bench_run {"suite":"shader","ab":"mimiclay_sdf_shadow_texel_eps_local=3"}</c> (Tools/sdf/bench/README.md).
///
/// Why shadows can be cheaper than the camera: a shadow map stores one depth per texel, and the shadow pass is
/// memory-latency-bound (each march step is a field read that mostly misses cache — Nsight, 2026-10-24), so every
/// step saved is a slow read avoided, and precision finer than a texel is never stored anyway.
///
/// Sweep that chose these defaults (2026-10-24, 256-prop shader suite, float16 fields, RTX 4090; shadow-pass ms vs
/// the old settings — texel eps 0.5, step scale 1 — and the share of pixels changing by more than 10/255):
/// <code>
///   setting                    spot shadows   spot image   sun shadows   sun image
///   texel eps 1                -19%           0.05%        -9%           0.06%
///   texel eps 1.5              -32%           0.12%        -15%          0.25%
///   texel eps 2                -43%           0.18%        -20%          0.44%
///   texel eps 3                -56%           0.27%        -27%          0.88%
///   step scale 2               -10%           0.00%        -6%           0.01%
///   32 steps + near miss 1     0%             0.00%        -12%          0.34% (max 169: false hits)
///   eps 2 + step scale 2       -47%           0.16%        -26%          0.46%
/// </code>
/// Rule used: biggest saving that keeps visible change under ~0.3% of pixels with no structural differences in the
/// amplified diffs. The sun's changes are self-shadowing on surfaces (its texels are bigger in world terms), so it
/// gets a smaller epsilon than spot/point lights. Re-run the sweep before raising these.
///
/// The epsilon only lets the march stop sooner; the spot/point caster BIAS deliberately ignores it (sdf_raymarch
/// RayMarchHit, s_marchEps). An earlier cut scaled the bias with it, which pushed far-spot casters under the floor
/// (a 683 u kitchen spot erased a dinner plate's shadow). The benchmark's dense-4spots-far case guards against that.
/// </summary>
public static class SdfShadowSettings
{
	/// <summary>Sun-cascade hit epsilon, in shadow-map texels (0 = full camera precision). See the class table.</summary>
	[ConVar( "mimiclay_sdf_shadow_texel_eps_sun" )]
	public static float SunTexelEpsilon { get; set; } = 1.5f;

	/// <summary>Spot/point-light shadow hit epsilon, in shadow-map texels (0 = full camera precision).</summary>
	[ConVar( "mimiclay_sdf_shadow_texel_eps_local" )]
	public static float LocalTexelEpsilon { get; set; } = 2f;

	/// <summary>Shadow-view step length multiplier, up to a full sphere-trace step. Props with baked displacement march
	/// at ~half steps (the lumps steepen the field); in a shadow map a missed lump is invisible. Measured free: 2.</summary>
	[ConVar( "mimiclay_sdf_shadow_step_scale" )]
	public static float StepScale { get; set; } = 2f;
}
