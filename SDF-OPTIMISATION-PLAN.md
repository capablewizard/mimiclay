# SDF optimisation plan

Primary goal: make the SDF shader cheaper at the same visual quality (8× MSAA, same silhouettes, paint
edges, boil, lighting and shadows). Second: check what the LOD systems save on top of that.

Measure everything with the benchmark in [Tools/sdf/bench](Tools/sdf/bench/README.md). Use the `shader` suite for
shader work and the `lod` suite for LOD and residency work. Record a baseline run before each change and compare
afterwards. A change counts as a win only when the comparison says `FASTER`.

## Done — allocation and lifetime cleanup (October 2026)

- Text atlases are allocated only when a prop has an enabled Text brush. A text-free prop binds a shared white
  fallback. The mesher's shared atlas stays allocated.
- Superseded field, occupancy, atlas, indirection and brush/spline input textures are now disposed when replaced.
  Sparse resources are released when a field goes back to dense, and stale sparse bindings are cleared.
- The field cache sweeps expired entries every frame (`SdfFieldCacheSystem`), not only when a new field is acquired.
- `mimi_perf_stats` and `SdfBenchProbe` count shared fields once and split field / input / text bytes.

On the kitchen map (66 renderers, 41 unique fields), counted SDF texture memory went from 300 to 222 MiB. Rendering
cost is unchanged.

## Shader candidates (`shader` suite)

Ordered by expected payoff. Each one is a separate change with its own A/B result.

1. **Shading taps after the hit.** The forward pass now spends more on shading than on marching: triplanar ×9,
   normal 6 taps, curvature 5, transmission ~8. Try sharing taps between normal and curvature, cheaper triplanar
   (skip faint axes), and dropping transmission taps when they are faint. `complex` and `dense` show it.
2. **Material evaluation after the hit.** After every hit, the shader evaluates the whole brush list again to find
   paint and material (`SdfSurfaceLocal`). Prune it to the brushes that can affect that point: a conservative
   per-brick list, or bounds tests that keep brush order, smooth blends, cutouts and paint exact. A
   nearest-brush shortcut is not equivalent. `complex` shows it.
3. **Marched shadow views. DONE (2026-10-24).** The shadow pass is memory-latency-bound (Nsight: ~25% L2 hit in the
   spot shadow pass after GPU idle), so fewer and smaller field reads are what count. Shipped: shadow views skip
   normals; texel-epsilon floor on every shadow view; float16 fields; global shadow march settings in
   `code/Sdf/SdfShadowSettings.cs` (sun epsilon 1.5 texels, spot/point 2, step scale 2, no step cap). The sweep table
   is in that file's doc comment. Spot shadow pass ~7.8 → ~1.25 ms on `dense-4spots` across all of these.
4. **Empty-space skipping / tighter proxies.** Rays start at the object box and march through empty space first.
   Surface-brick proxy boxes (draw boxes around occupied bricks, start rays near the surface) are the larger
   version of this. Prototype it on its own and keep the current renderer as the reference. `dense` and `close`
   show it.
5. **R16F dense fields. DONE (2026-10-24), default on; `mimiclay_sdf_field_half 0` is the float32 fallback.**
   256-prop suite: field memory 1271 → 675 MiB, frame-capped spot shadows −35% (the shadow pass is memory-latency
   bound), images within 0.3% of pixels (silhouette hit/miss flips). The bake clamps to ±60000, so the 1e9 empty
   sentinel can't become +Inf.

## LOD and residency candidates (`lod` suite)

- **Release fields that no view needs.** A prop in a mesh band still keeps its field. Derive field demand from the
  same resolved state as visibility, including shadows, outlines, editing and viewmodels. Use hysteresis and a
  bounded bake queue.
- **Transition cost.** `dolly-lod` shows hitches and the frame tail when props cross band boundaries.
- **Paint-only edits.** Separate geometry invalidation from material invalidation so repainting doesn't re-bake the
  field.

## Round 2 tracker (from 2026-10-25)

Baseline: [20261025-111940-shader-baseline-2026-10-25](Tools/sdf/bench/runs/20261025-111940-shader-baseline-2026-10-25.json)
(8× MSAA, `55543a3`). sdf ms: dense 3.78 (prepass 1.06 / forward 2.14), close 3.37, complex 1.68, dense-4spots 4.74
(shadows 1.03), dense-sun 5.66 (shadows 1.30), dense-4spots-far 4.76. LOD-suite context: distant tiny props cost
1.8–2.1 ms in the prepass.

Ranked by expected impact, not effort. Savings are estimates until a run says otherwise. Work one item at a time
(one editor, one GPU, and nearly every item touches `sdf_raymarch.shader` / `sdf_eval.hlsl`): gate it behind a
ConVar, bench with an in-run `ab` arm, commit, then fill in Run / Verdict here.

**Measure first** — cheap checks that re-rank the uncertain rows (measured 2026-10-25, 1920×1080, 8× MSAA, shader-suite
views at full quality, `mimiclay_sdf_uniform_material` on):
- [x] Slope bound `g_flDispGradL` on a displaced dev-scene prop (decides #7).
  Only 1 of the 256 props displaces (`146: charades_canvas`). All 256 carry `DispAmp 0.8`, but the other 255 have
  `Displace` off, and no prop has a `ClayBoil`. On that prop L = 0.8 × 0.073 × 4 = **0.234** and **B = 0.8** (a boiling
  prop at the default `AmpJitter 1`: L 0.350, B 1.2), so every step is 0.9 / (1 + L) = 0.73 d. `d − 2B` beats
  `d / (1 + L)` only when d > (1 + L) / (2 · DispFreq) = **8.5 u** (9.3 u boiling). The amp cancels out of that
  threshold. Beyond it the step grows +7% at d = 12, +13% at d = 20, and at most +23% (+35% boiling) far from the
  surface. The proxy pad is 8 u and face-on rays hit in 4 steps (below), so almost every step runs at d < 8.5.
  → **#7: ~0 in the bench** (one displaced prop). In game it applies only to `Displace`/boiling props and their few
  long steps: < 0.05 ms.
- [x] GPU time of one boil-tick rebake (decides #8).
  No dev-scene prop boils. Of the shipped prefabs, 6 boil `Always` (pawns, hunter, charades canvas) and 1
  `WhileDamaged`; the rest boil only while hovered or after being shot. A tick is 4 Hz and global, and
  `FieldRebakeInterval` is 0, so every boiling prop rebakes on the same frame. Method: re-run each prop's own
  `SdfFieldGpu.Evaluate` with its normal inputs (all dense fields, ~1.2 M voxels each), then time a GPU round trip:
  a 1-voxel readback, minus a readback alone. Results are medians of 3–7 reps, and very steady:

  | set | GPU per prop |
  |---|---|
  | 1–2-brush props (×8) | 0.18 ms |
  | 8 biggest (avg 34 brushes) | 0.39 ms |
  | fruit bowl (42 brushes, 130×130×100) | 0.74 ms |
  | all 256 at once | 39.7 ms total, 0.155 ms each |

  CPU submit is 0.034 ms per prop, and boil displacement adds ~6%. These dispatches run on the immediate context,
  outside the frame's GPU timestamps, so neither `GpuProfilerStats` nor the bench sees them: 4096 rebakes in 4 s moved
  `BusyMs` by 0.02 ms. Cost per voxel is ×2.4 at 34 brushes and ×3.4 at 42 compared with 1–2 brushes.
  → **#8: now sized.** A tick costs about 0.4–0.75 ms per boiling 30+-brush prop. Per-cell pruning could cut that by
  roughly 50–65%, but it saves little on props under ~8 brushes, where the fixed cost per voxel dominates. The spike
  scales with the number of boiling props. An A/B needs a rebake case (or this timing harness); the current bench is
  blind to it.
- [x] Brushes-evaluated heatmap, `mimiclay_sdf_debug_skip 256` (decides #3 / #10).
  Brushes that survive the cull, per shaded pixel (mean / p90): dense 2.3 / 5, close 1.8 / 3, complex view 4.0 / 7.
  Inside the fruit bowl it is **4.9 / 7 of 42** (max ~10). Silhouettes look the same as interiors (dense 2.4 / 5), and
  10–16% of pixels belong to uniform-material props, which walk nothing. With `BrushCulling` off, the same map shows
  how many brushes the loop walks. Each walked brush loads all 7 texels before its cull test: dense ≥ 8.2 mean (15% of
  pixels > 16), close ≥ 13 (62% > 16), fruit bowl 42. Scene: 211 non-uniform props, median 7 brushes, p90 20.
  → **~70–90% of walk iterations are culled but still pay 7 RGBA32F loads** (bowl: 37 of 42). **#10: raise to
  0.1–0.25 ms.** Deferring A and C (and E outside cutouts) cuts ~30–40% of the walk's texel traffic. It is the cheapest
  row to try. If it reads `same`, check whether the compiler already sinks those loads. **#3: keep 0.2–0.5 ms**; the walk
  is shaped the way #3 assumes, and #10 would take a slice of #3's saving.
- [x] Step-count heatmap, bits 64 / 128: where do the steps go — silhouettes, empty space, near-surface?
  Note: bit 128 alone paints only misses, and hits shade normally. Use 192 (64 + 128) to see both.
  Forward pass, visible hits (bit 64): dense mean 8.3 steps (p50 6 / p90 15), close 6.6 (5 / 12), complex 8.7
  (6 / 17). About half the hits sit at the 4-step floor; those are face-on surfaces. The high counts come from
  **grazing faces** (tops and undersides seen edge-on): in the dense view, the 28% of hit pixels at ≥ 10 steps carry 53%
  of hit steps. Silhouettes against the background are only 1–2% of hit pixels (mean 14–16 steps) and ~3% of hit steps.
  Misses: map 192 with `DepthOcclusionCull` off, so the forward pass marches the full bracket like the prepass. It shows
  the frontmost proxy layer. Proxy-margin misses cover 70–76% of the screen at a mean of 6–9 steps, which is
  **70–80% of that layer's steps**. Of those misses, 21–31% leave the box in 1 step. The **near-miss halo** that hugs
  each silhouette (misses at ≥ 10 steps, 15–34% of miss pixels) carries 39–66% of miss steps.
  → Steps go to **rays near the surface**: the near-miss halo and grazing hits. There are few long empty-space approaches.
  **#4: lower to 0.05–0.2 ms.** A wave-shared cone stops at the first nearby surface, which is exactly where these
  rays spend their steps. **#12: keep 0–0.2 ms**, but it now aims at the right rays: over-relaxation speeds up rays
  travelling along a surface. The miss share also strengthens #2, and an A/B of the existing
  `mimiclay_sdf_proxy_pad` (8 u) is a cheap side check.

Suggested re-ranking from these (table order left as is): #10 up to just after #3, and try it first because it is a
few lines. #12 above #4. #4 down to around #9. #7 to the bottom. #8 matters only when several large props boil; sized
above.

Screenshots, the pixel decoder (`heat.ps1`) and the rebake timing harness (`baketime.cs`, a `run_csharp` body) are in
the session's scratchpad. Copies of the two scripts are in the side-session notes folder.

| # | Optimisation | Targets | Expected | Status | Run | Verdict |
|---|---|---|---|---|---|---|
| 1 | Screen-traced SDF shadows instead of shadow-map marching (Wright, SIGGRAPH 2015) | shadow passes 1.0–1.3 ms per light type | 0.5–1 ms+, grows with lights | blocked: needs engine PR (see note below) | | partly feasible |
| 1b | Engine static shadow cache: flag SDF props `StaticObject` (+ static lights), invalidate by nudging the light on edit/boil/claim | spot/point shadow re-renders of unchanged props | most of the static props' 1.03 ms spot share | done, ConVar `mimiclay_sdf_static_shadows` ships OFF (note below) | [static-shadows](Tools/sdf/bench/runs/20261025-123737-shader-static-shadows.json) | bench win: spot shadows 1.01 → 0.04 ms (dense-4spots), 0.39 → 0.03 ms (far), images identical; in game ≈ 0 today |
| 2 | Conservative depth via FRONT faces (`SV_DepthGreaterEqual`/`LessEqual` per depth direction), fallback shader when camera/light is inside the box | prepass + shadows on hidden layers | 0.3–1.0 ms | todo | | |
| 3 | Brush data in a constant / structured buffer read at a wave-uniform index (bake: groupshared preload) | material walk 0.7–1.0 ms | 0.2–0.5 ms | todo | | |
| 4 | Cone march shared across a wave (bounding cone of the lanes' rays, uniform steps, then per-lane) | empty-space steps, all passes | 0.2–0.6 ms | todo | | |
| 5 | L2-resident shadow field (~32³ R8 per prop, shadow views only) | cold-L2 shadow pass | 0.3–0.6 ms; moot if #1 lands | todo | | |
| 6 | Helper-lane early exit (`IsHelperLane()`) in the camera prepass after `wpDx`/`wpDy` | distant/tiny props, silhouettes | 0.2–0.5 ms far views | todo | | |
| 7 | Displacement understep only near the surface: step `max(d/(1+L), d − 2B)` | every march step (all props `DispAmp 0.8`) | 0.1–0.4 ms | todo | | |
| 8 | Per-cell brush pruning for the field bake (Lipschitz Pruning, EG 2025) | rebake per boil tick | unknown; hitches | todo | | |
| 9 | Temporal warm start for the prepass (reprojected previous depth, windowed confirm) | prepass march | 0–0.3 ms | todo | | |
| 10 | Load brush texels A and C only after the cull test in `SdfSurfaceBrush` | material walk | 0.05–0.2 ms | tried, reverted | [after-defer-loads](Tools/sdf/bench/runs/20261025-115725-shader-after-defer-loads.json) (A/B/A: [before](Tools/sdf/bench/runs/20261025-115230-shader-before-defer-loads.json), [before-2](Tools/sdf/bench/runs/20261025-120708-shader-before-defer-loads-2.json)) | `same`: forward Δ ≤ 0.02 ms in every case, images identical (note below) |
| 11 | SDF visibility buffer | repeated march + shading | 0–0.3 ms | parked | | |
| 12 | Over-relaxed sphere tracing | march steps | 0–0.2 ms | todo | | |
| 13 | March in model space against the oriented proxy box | rotated props, per-step rotation | 0.05–0.15 ms | todo | | |
| 14 | Tight per-brick bounds in the sparse atlas (NVIDIA, EG 2023) | atlas traversal | ≤0.1 ms | todo | | |
| 15 | 4-tap tetrahedral normals in the prepass | prepass normals | 0.05–0.1 ms; look changes | todo | | |
| 16 | Pre-tessellated splines | walk on spline props | <0.05 ms | todo | | |
| 17 | Constant-roughness path | 3 triplanar samples | <0.05 ms | todo | | |
| — | Single-material shortcut (`mimiclay_sdf_uniform_material`) | material walk | | DONE `55543a3` | [uniform-material](Tools/sdf/bench/runs/20261025-104220-shader-uniform-material.json) | forward −0.02…−0.12 ms, images identical (2× MSAA run) |

**#10 result (2026-10-25, 8× MSAA, 4 s samples):** forward ms before / after / before-2: dense 1.96 / 1.96 / 1.97,
close 1.73 / 1.71 / 1.74, complex 1.32 / 1.33 / 1.26. The SPIR-V did change (+0.7 KB), so the compiler had not
already sunk the loads. The likely reason it doesn't pay: a brush's 7 texels are adjacent (`k * 7 + slot`, 112 B),
so a culled brush still costs about one cache line either way. Fewer brush iterations or wave-uniform reads (#3) is
the lever, not fewer texels per iteration. The `SdfDist` bake variant was not tried.

**#1 feasibility (2026-10-25, read-only engine study of the installed 10-23 DLL; `sbox-public-master` lags it):**
partly feasible, only with engine changes. The SUN already multiplies a full-res, MSAA-aware screen-space shadow mask
(`DirectionalLightShadow.hlsl` `SampleScreenSpaceShadow`, written by Bend contact shadows), and projects can run a pass
at `Stage.AfterDepthPrepass` after it, but the mask is `internal` (`SceneLight.GetShadowMask`), exists only with
`ContactShadows` on, and no public attribute or pipeline slot reaches engine lighting. Spot/point have a
`BinnedLight.ShadowMaskTextureIndex` field that no shader reads. Sun path ≈ a week plus an engine PR to expose the
mask and per-light cast control; local lights need native light-binner work. Cheaper today: the engine's static
shadow cache for spot/point (row 1b) and `r.shadows.updates` time slicing (reuses stale maps; moving casters lag).
SDF-receiver-only screen shadows save nothing on their own (props must still cast onto the world).

**#1b result (2026-10-25, in-run A/B, 8× MSAA, 3 × 4 s, spot cases capped at 60):** `code/Sdf/SdfStaticShadows.cs`
plus `SdfRaymarchRenderer.UpdateStaticShadow`. The dev-scene spots turn static with the ConVar (play only).

| case | sdf ms A → B | spot shadows A → B | prepass / forward Δ | image |
|---|---|---|---|---|
| dense-4spots | 5.10 → 4.35 (−0.75) | 1.01 → 0.04 | +0.11 / −0.11 | identical |
| dense-4spots-far | 4.85 → 4.69 (−0.16) | 0.39 → 0.03 | +0.01 / +0.29 | identical |
| dense / close / complex / dense-sun | within noise | — (sun has no static cache) | 0 | identical |

The bench calls both spot rows `same` only because the A arm's range is wide (4.84–5.38). The shadow column is not
ambiguous. In the far case the forward pass picks up +0.29 ms, most likely the cold-L2 cost that the shadow pass
used to pay at the start of each capped frame (see the bench's `Cap` note). Engine facts (installed 10-23 DLL): a
spot/point light on a static GameObject renders `StaticObject` casters once into a cache and draws only dynamic casters
on top each frame. The cache drops only when the light's SceneLight `TransformVersion` or shadow resolution changes.
Turning the light's static flag off keeps the old cache. Nothing watches the casters. Invalidation: a prop joins only
after its shadow inputs (shape/field/placement/casting/shadow knobs) are unchanged for 2 s
(`mimiclay_sdf_static_shadows_settle`). It never joins while boiling, in another world, or as a viewmodel. Any change,
hide, disable, destroy or band flip drops it in the same frame, before rendering. Promotions batch to one window per
0.5 s. At FinishUpdate every spot/point SceneLight that reaches a changed prop is nudged 0.01 u and back, which bumps
`TransformVersion` and leaves the light where it was. Checked by pixel diff against the ConVar-off render: move, shape
edit, boil on/off, disable, destroy and camera band flips all come back identical (a few isolated pixels while the prop
is dynamic). A negative control (move without invalidation) leaves a visible stale shadow, so the diff catches one.
Claim/convert destroys the scene prop and spawns a boiling pawn, so it is the destroy path; it was not run live.
Re-cache rate: 0 in a still scene. In the dev scene, one re-cache per light per edit/move burst (a demotion, then a
promotion 2 s later). In kitchenlobby with its spot forced static: 0.4 re-caches/s idle, from props flipping in and out
of the shadow-LOD band.

**Why it ships OFF:** (1) no shipped map has a static light (kitchen 2 spots, home 16, kitchenlobby 2, bedroom
sun-only; none static, none baked), so it does nothing in game. (2) Even with the lobby spot forced static, spot
shadows cost 0.08 ms either way: shadow LOD leaves ~5 marched casters near the camera, and those are boiling pawns, a
boiling orange and an animated train, which never cache. The other casters are sibling meshes, which this leaves
dynamic. (3) A baked/mixed light drops `StaticObject` casters from its shadow entirely, so the flag is unsafe on a map
that uses one. The in-game saving would need static map lights AND the sibling mesh casters to join the cache under the
same rules. Side finding: with shadow LOD on, the cached render differs from the uncached one by ~20 already-marginal
acne pixels. That goes away with `OverdrawOptimization` off, so the conservative-depth shadow path is sensitive to draw
order. This predates the cache.

**Parked (2026-10-25)** until a full game scene exists to bench against. The experiment to run then: mark that map's
spot lights static, make every prop's spot/point shadow marched and cached (no mesh shadows for local lights), and
compare average and worst-frame spot shadow ms with today's mesh-shadow LOD, standing still and walking. The sun has no
static cache, so it keeps mesh/LOD shadows either way. Costs to watch: each re-cache re-marches every prop in that
light (~0.25 ms per spot for 256 props), the engine drops a cache whenever the spot's shadow-map resolution steps as the
camera moves, cached lights use about twice the shadow-map memory, and no map can use baked lights.

Also landed in `55543a3`: cutout colour-edge cull fix (`sdf_eval.hlsl` `SdfSurfaceBrush`), and the shadow march
knobs now gate on `IsShadowView()` alone so ortho editor views march like cameras (cascades verified still detected).

Dropped after research: Brixelizer (already have an I8 atlas), hardware-RT bricks (not exposed), Dreams splats,
Harnack tracing, backface distance fields (bake too slow for boil rebakes), BC4 fields (too coarse at the surface),
general per-pixel brush-cell lists (tried 2026-10-24, nothing in crowds).

## Rules

- Keep 8× MSAA, resolution, field density and the SDF→mesh hand-off distances fixed while measuring. Lowering any
  of them is a separate quality decision, not an optimisation.
- Avoid adding dynamic shader combos for experiments. The raymarch shader's compile time is already the
  bottleneck. Use runtime uniforms instead.
- After changing shading, look at the close-up views in the development scene (`sdf_dev_view <prop> 1`) as well as
  the numbers.
