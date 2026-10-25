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

**Shipping-settings costs** ([lod-baseline-2026-10-25](Tools/sdf/bench/runs/20261025-130127-lod-lod-baseline-2026-10-25.json),
`lod` policy = adaptive + shadow LOD + mesh hand-off, four spots): close 4.00 ms (prepass 1.00 / forward 2.45 / shadows
0.45), dense 4.37 (1.00 / 2.80 / 0.46), overview 3.54 (0.67 / 2.35 / 0.46), far 1.44, distant 0.21. Only 3–9 props march
their shadows, and the ~0.45 ms shadow cost barely changes with zero marched casters, so it's the mesh casters, not the
SDF march. The mesh hand-off already removes the distant-prop prepass cost (distant prepass 0.03 ms). **In game the
forward pass is ~60–65% of SDF time**, so forward items (material walk #3, shading taps, grazing steps #12) outrank the
shadow rows (#1, #5, shadow half of #2) and #6.

**Forward-pass attribution, shipping settings** ([forward-attribution](Tools/sdf/bench/runs/20261025-141557-ship-forward-attribution.json),
new `ship` suite = the lod suite's four `lod`-policy views, 8× MSAA, in-run `mimiclay_sdf_debug_skip` arms, 3 × 3 s).
Forward ms saved by switching each stage off (diagnostic only — every arm changes the image):

| stage (bit) | close | dense | complex | overview* |
|---|---|---|---|---|
| forward total (A) | 2.51 | 2.63 | 2.50 | ~2.9 |
| material walk (1) | 0.75 | 0.49 | 0.70 | 1.15 |
| transmission (8) | 0.26 | 0.31 | 0.27 | 0.58 |
| triplanar maps (2) | 0.14 | 0.16 | 0.15 | 0.42 |
| forward normals (32) | 0.08 | 0.12 | 0.08 | 0.20 |
| curvature (4) | 0.12 | 0.07 | 0.06 | ~0 |
| all five (47) | 1.32 | 1.13 | 1.67 | 1.28 |
| everything after the hit (64 = march only) | 1.85 | 1.78 | 2.07 | 1.87 |

\*overview's single arms are noisy (they sum past the all-five arm); read close/dense/complex.

Reading it: the forward MARCH is only 0.4–0.9 ms; **~70–80% of the forward pass is shading after the hit**. Of that, the
five SDF stages are 1.1–1.7 ms, and the remaining 0.4–0.6 ms (64 minus 47) is lighting + output (engine lights,
clustered lights/decals, envmap, `clay_shading`). The material walk is still the largest single stage (0.5–0.75 ms) —
#3/#3b's loop savings didn't touch it, so its cost is evaluating the 2–5 brushes that survive. Transmission
(0.26–0.31 ms) is second and has had no work yet.

**SDF vs mesh, shipping settings** ([mesh-vs-sdf](Tools/sdf/bench/runs/20261025-143119-ship-mesh-vs-sdf.json), `ship`
suite, in-run A/B with the new diagnostic `mimiclay_sdf_force_mesh=1`: every prop that would raymarch draws its mesh
LOD0 instead; 256/256 meshes present except the 083 rug):

| case | SDF sdf ms (prepass / forward / shadows) | all-mesh sdf ms (prepass / forward / shadows) | gap |
|---|---|---|---|
| close-lod | 3.98 (0.93 / 2.50 / 0.42) | 0.89 (0.08 / 0.33 / 0.46) | −3.09 (−78%) |
| dense-lod | 4.09 (0.94 / 2.66 / 0.44) | 1.33 (0.23 / 0.59 / 0.46) | −2.77 (−68%) |
| overview-lod | 3.65 (0.68 / 2.45 / 0.47) | 1.71 (0.28 / 0.93 / 0.46) | −1.94 (−53%) |
| complex-lod | 3.02 (0.46 / 1.93 / 0.46) | 0.73 (0.04 / 0.19 / 0.46) | −2.28 (−76%) |

The raymarched path costs **2.3–4.5× the mesh path**, a 1.9–3.1 ms gap at these views; shadows are identical (both are
mesh casters in shipping). This is the ceiling for any SDF-side optimisation. Images differ heavily (mesh LOD0 is
lower fidelity and the mesh shader's look isn't identical), so it's a cost comparison, not a like-for-like quality one.
Open question: the mesh's WHOLE forward pass (0.19–0.93 ms) is less than the SDF forward's post-hit remainder after the
five SDF stages (0.4–0.6 ms) — compare the two shading paths (`clay_shading`, SV_Depth/early-Z, cluster re-seat) for
SDF-only overhead.

**Occupancy, measured (2026-10-25, Nsight Systems GPU metrics, `shader` suite dense view, 8× MSAA).** Only Nsight
Systems is installed (no Nsight Graphics), but its `ad10x-gfxt` metric set samples pixel warps and allocated registers
per TPC, and it works without admin or an editor restart: `nsys profile --trace=none --sample=none --cpuctxsw=none
--gpu-metrics-devices=0 --gpu-metrics-set=ad10x-gfxt --gpu-metrics-frequency=20000 --duration=5 -o <out> cmd.exe /c
"ping -n 7 127.0.0.1 >nul"` while a bench case samples, then `nsys export --type sqlite` and read `GPU_METRICS`
(metric 32 Pixel Warps/cycle, 52 3D Register Allocation, 38 Unallocated Warps, 65 SM Issue Active, 86 L1 texture,
93 L1 hit) with the bundled `host-windows-x64/sqlite3.exe`. Registers per thread = metric 52 / (metric 32 × 32); values
are per TPC (2 SMs: 96 warp slots, 131072 registers). The frame's segments are obvious in the 50 µs timeline:

| segment | length | regs / thread | pixel warps / SM | register cap (warps / SM) | issue active | L1 hit |
|---|---|---|---|---|---|---|
| depth prepass | ~1.0 ms | ~50 | ~16 | ~40 (not the limit) | 35–45% | 96% |
| forward | ~2.6 ms | **~145–160** | **~5–10** | **~13 (27% of 48)** | 20–35% | 93% |

The forward raymarch pixel shader allocates ~150 registers per thread, so an SM can hold at most ~13 of its warps
(27% theoretical occupancy), and it averages 5–10. With the L1 hitting 93% and issue only 20–35% busy, the forward pass
is latency-bound for lack of warps to switch between. This is the "occupancy" the final pass inferred, now with numbers.
Register targets: ≤128 → 16 warps/SM (+23%), ≤96 → 21, ≤80 → 25. The prepass (~50 registers) is not register-limited;
its low warp count has another cause (small per-object draws, depth-output ordering — unmeasured).

This also gives a cheap way to score register-reduction attempts: build a variant, capture 5 s, read regs/thread in the
forward segment. Candidates: run the material walk BEFORE triplanar/normals so their results aren't live across it;
half-precision material maths; brush data read at a uniform index from a buffer (NVIDIA can hold uniform loads in
uniform registers); keep transmission's light loop un-unrolled.

**Register pressure (#21, 2026-10-25).** Each variant was a real build (compile-time define, or edit / measure /
revert), captured for 5 s during the `shader` suite's `dense r1` sample (`seconds 30`). The number is the **median
regs/thread** over the forward band (samples ≥ 60 regs/thread with > 0.5 pixel warps/TPC). The ratio counts every
3D register (vertex warps too) against pixel warps, so read it as a relative score: the mesh shader reads ~96 on the
same scale.

| build | regs / thread | forward | image |
|---|---|---|---|
| HEAD (baseline) | **149** | — | — |
| **reorder: `SdfShade` before `SdfNormal` / triplanar / normal map (KEPT)** | **122** | −0.14…−1.66 ms, every case of the final run vs both befores | identical |
| + brush texels only from `g_BrushBuf` (buffer-only compile, #3) | 122 | not benched (no register change) | — |
| + `min16float` material blends (`SdfSurface` + lerps) | — | editor shader compile never finished (12+ min; the other 5 shaders took seconds) | — |
| + load A / C after the cull (#10 again) | 123 | not benched | — |
| + one `OneBrushDist` call site in `BrushDist` (identity = first mirror iteration) | 122 | not benched | — |
| + slim `Material::Init( pos, ss )` through transmission, full `Init( i )` only before `ClayShade` | 122 | not benched | — |
| + `[branch]` on the walk, uniform / baked-material tests, transmission and its metal early-out | 122 | not benched | — |

Strip builds (compile-time `SDF_SKIP` mask; 1024 = `ClayShade` replaced by a flat output) show where the peak lives:

| stripped | HEAD order | reordered |
|---|---|---|
| nothing | 149 | 122 |
| walk + transmission (`ClayShade` only) | 117 | 117 |
| transmission (walk + `ClayShade`) | 149 | 122 |
| walk (transmission + `ClayShade`) | 145 | 145 |
| `ClayShade` (walk + transmission) | 148 | 122 |
| transmission + `ClayShade` (walk alone) | 121 | 122 |
| walk + `ClayShade` (transmission alone) | 117 | — |
| walk + transmission + `ClayShade` | ~72 | — |
| `mimiclay_sdf_force_mesh 1` (mesh shader + `ClayShade`) | ~96 | |

Reading it: at HEAD the walk ran with the normal, the three triplanar results and the rebased `i` fields all live, and
that pushed the peak to ~149. Reordered, the walk itself (~122) sets the peak, with `ClayShade` alone at ~117. Below
that, allocation **is not monotone in the code**: stripping the spline primitive (`SDF_SHAPE_STRIP` 1) took the
reordered build back UP to 147, and stripping spline + extruded + text + cone to 143 (extruded, text or cone alone:
122). Stripping the walk at compile time also gave 145. The driver's allocator seems to land in two states, ~122 or
~145, depending on what else is in the program. So shaving the walk's maths is not a reliable lever. Getting to ≤96
would mean making `ClayShade` cheaper in this shader (it's ~117 here vs ~96 in the mesh shader, and the live state
across it is small), which can't be done without SASS-level visibility. Transmission's light loop is already `[loop]`
and doesn't set the reordered peak. Any future change to the forward shading block should be re-scored with this
capture, since an unrelated edit can flip the allocation back to ~145.

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
| 2 | Conservative depth via FRONT faces (`SV_DepthGreaterEqual`/`LessEqual` per depth direction), fallback shader when camera/light is inside the box | prepass + shadows on hidden layers | 0.3–1.0 ms | tried, code reverted (note "Final pass" below; was ConVar `mimiclay_sdf_front_depth`) | [shader](Tools/sdf/bench/runs/20261025-172754-shader-overdraw-all-front-depth.json), [shader-2](Tools/sdf/bench/runs/20261025-173812-shader-overdraw-none-front-depth.json), [ship](Tools/sdf/bench/runs/20261025-171422-ship-overdraw-all-front-depth.json) | `same`: prepass −0.03…0 ms on every uncapped case, images identical; `SV_DepthLessEqual` under reversed-Z `GREATER_EQUAL` gives no measurable early rejection on the 4090 |
| 3 | Brush data in a constant / structured buffer read at a wave-uniform index (bake: groupshared preload) | material walk 0.7–1.0 ms | 0.2–0.5 ms | tried, ships OFF: structured buffer (a cbuffer can't hold persistent per-object data, note below), ConVar `mimiclay_sdf_brush_buffer` | [brush-buffer-groupcull](Tools/sdf/bench/runs/20261025-132210-shader-brush-buffer-groupcull.json) (+ [confirm](Tools/sdf/bench/runs/20261025-133138-shader-brush-buffer-groupcull-confirm.json), [noop](Tools/sdf/bench/runs/20261025-134424-shader-brush-buffer-groupcull-noop.json)) | `same`: forward −0.03…−0.06 ms alone, images identical |
| 3b | Order-preserving group cull: one record per 4 packed brushes (union AABB + widest per-op threshold), skip the run when its box is past the threshold | material walk iterations | 0.1–0.3 ms | tried, ships OFF: ConVar `mimiclay_sdf_group_cull` (1 = wave-uniform skip, 2 = per-lane jump) | same runs as #3 | `same` by the bench rule, but complex forward −0.11…−0.15 ms in every run; with #3 −0.13…−0.20 ms; images bit-identical (note below) |
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
| 18 | Mesh-shell proxy, phase 1: raster the prop's own inflated surface mesh instead of the padded box; camera rays start at its front faces; validity gate + box fallback | prepass ~0.7–0.9 ms, forward march 0.4–0.9 ms | 0.5–1.5 ms | tried, code reverted (note below; was ConVar `mimiclay_sdf_mesh_shell`) | [mesh-shell](Tools/sdf/bench/runs/20261025-145727-ship-mesh-shell.json), [lods](Tools/sdf/bench/runs/20261025-150818-ship-mesh-shell-lods.json), [dilated](Tools/sdf/bench/runs/20261025-151707-ship-mesh-shell-dilated.json), [near](Tools/sdf/bench/runs/20261025-152219-ship-mesh-shell-near.json), [shader](Tools/sdf/bench/runs/20261025-153924-shader-mesh-shell.json) | never `FASTER`: LOD0 shell `SLOWER` (dense +1.2–1.5 ms, forward), and drops thin features (holes); the hole-free dilated LOD2 shell is `same` (−0.15…+0.9 ms) |
| 19 | Shading-path comparison: what the forward "lighting + output" remainder (bit 64 − bit 47) is, and any SDF-only overhead in it | forward 0.4–0.6 ms remainder | ? | measured (note "Final pass" below) | [attribution](Tools/sdf/bench/runs/20261025-160846-ship-remainder-attribution.json), strip builds [47](Tools/sdf/bench/runs/20261025-161739-ship-occupancy-strip47.json) / [8](Tools/sdf/bench/runs/20261025-162303-ship-occupancy-strip8.json) / [keep walk](Tools/sdf/bench/runs/20261025-162719-ship-occupancy-keepwalk.json) / [keep transmission](Tools/sdf/bench/runs/20261025-163128-ship-occupancy-keeptrans.json) | the remainder is engine lighting (`ClayShade`, same code as the mesh shader, 0.36–0.6 ms); SDF-only overhead = occupancy, set independently by the material walk and transmission (~0.1–0.2 ms on the march); no cheap fix |
| 19b | Force `OverdrawOptimization` on every prop (PropGen templates author it off: 146 of 256 dev props, 380 of 660 in prefabs/scenes) | doubled box-face march | 0.3–1 ms (expected) | tried, code reverted (was ConVar `mimiclay_sdf_overdraw_all`) | same runs as #2 | `same`: one face vs both faces on all 256 props differs by ≤ 0.06 ms; images identical |
| 20 | Cheaper transmission at the same look: wave-uniform light loop (`ClusterForEach`, no per-light shadow sample), lazy inward tap | transmission 0.22–0.38 ms | 0.1–0.2 ms | tried, code reverted (were ConVars `mimiclay_sdf_trans_fast`, `_trans_lazy_tap`) | [trans-fast](Tools/sdf/bench/runs/20261025-163657-ship-trans-fast.json), [lazy-tap](Tools/sdf/bench/runs/20261025-164536-ship-trans-lazy-tap.json) | `same`: forward +0.01 / ±0.01 ms, images identical; the tap costs ~0 and the march 0.06–0.13 ms, so no stage is worth a look change |
| 21 | Register pressure of the forward PS: run `SdfShade` (the brush walk) BEFORE the normal / triplanar samples so their results aren't live across it; also tried half-precision material, buffer-only brush reads, late A/C loads, one `OneBrushDist` site, slim Material across transmission, `[branch]` | forward occupancy (~149 regs/thread → ≤13 warps/SM) | ≤128 regs (16 warps/SM) | DONE (uncommitted): reorder kept, everything else reverted (note "Register pressure" below) | before / after / before-2 / final: [shader](Tools/sdf/bench/runs/20261025-183140-shader-regs-before.json) / [reorder](Tools/sdf/bench/runs/20261025-183643-shader-regs-reorder.json) / [before-2](Tools/sdf/bench/runs/20261025-184105-shader-regs-before2.json) / [final](Tools/sdf/bench/runs/20261025-192629-shader-regs-reorder-final.json); [ship](Tools/sdf/bench/runs/20261025-183340-ship-regs-before.json) / [reorder](Tools/sdf/bench/runs/20261025-183843-ship-regs-reorder.json) / [before-2](Tools/sdf/bench/runs/20261025-184305-ship-regs-before2.json) / [final](Tools/sdf/bench/runs/20261025-192829-ship-regs-reorder-final.json) | **149 → 122 regs/thread** (16–17 warps/SM cap); forward −0.14…−1.66 ms in every case of the final run vs both befores (`FASTER`: shader dense, ship close-lod); images identical |
| — | Single-material shortcut (`mimiclay_sdf_uniform_material`) | material walk | | DONE `55543a3` | [uniform-material](Tools/sdf/bench/runs/20261025-104220-shader-uniform-material.json) | forward −0.02…−0.12 ms, images identical (2× MSAA run) |

**#10 result (2026-10-25, 8× MSAA, 4 s samples):** forward ms before / after / before-2: dense 1.96 / 1.96 / 1.97,
close 1.73 / 1.71 / 1.74, complex 1.32 / 1.33 / 1.26. The SPIR-V did change (+0.7 KB), so the compiler had not
already sunk the loads. The likely reason it doesn't pay: a brush's 7 texels are adjacent (`k * 7 + slot`, 112 B),
so a culled brush still costs about one cache line either way. Fewer brush iterations or wave-uniform reads (#3) is
the lever, not fewer texels per iteration. The `SdfDist` bake variant was not tried.

**#3 / #3b result (2026-10-25, in-run A/B, 8× MSAA, 4 s samples; 3 runs, 3–5 repeats).** Both are runtime uniforms
on the same shader. Forward Δ ms against the run's own A arm (uncapped cases; the spot cases' forward moved ±0.4 ms in
every arm alike, including the no-op, so they're not read here):

| arm | dense | close | complex | runs |
|---|---|---|---|---|
| #3 buffer alone | −0.05 | −0.06 | −0.03 | 1 |
| #3b mode 1 alone | −0.01 | −0.06 | −0.11 | 1 |
| #3b mode 2 alone | −0.01 / −0.01 | −0.07 / −0.07 | −0.14 / −0.15 | 2 |
| #3 + #3b mode 1 | −0.05 / −0.05 / +0.13 | −0.10 / −0.10 / −0.11 | −0.16 / −0.18 / −0.13 | 3 |
| #3 + #3b mode 2 | −0.05 / +0.15 | −0.12 / −0.13 | −0.20 / −0.17 | 2 |
| no-op (`group_cull=0`) | −0.01 | −0.01 | 0.00 | 1 |

Images were `identical` (or matched the case's own noise row) in every arm of every run, so the group cull is exact as
designed. **No arm reads `FASTER`.** The verdict uses whole-case sdf ms with a bar of 2× the A arm's spread, and these
runs had one slow rep per case in most arms. In the third run every arm of `dense` spanned 3.1–4.3 ms, which is
where dense's +0.13/+0.15 came from. The complex gain is real and repeats in every run: about 10–14% of its forward pass, where
the fruit bowl walks 42 brushes. Dense/close props have a median of 7 brushes (two groups), which leaves little to skip.
**Both ship OFF** under the FASTER rule. The best candidate to switch on is #3 + #3b mode 2: about −0.1 ms on close and
−0.2 ms on complex, with identical images. Turning it on needs a sign-off decision or a cleaner bench (a forward-pass verdict, or a
case with more brush-heavy props), not more shader work. **Decision (2026-10-25): both stay OFF, code left in place.**

Engine finding for #3: `RenderAttributes.SetData` (the cbuffer route, e.g. `DirectionalLightCB`) writes a per-frame
*dynamic* constant buffer through `Graphics.Context`, which exists only inside a render block. A SceneObject's
attributes, set from Update and kept across frames, can't hold one. So #3 uses the fallback: a `GpuBuffer`
(992 × 16 B, a copy of the brush texture uploaded with it in `PackBrushes`), read as `StructuredBuffer<float4>
g_BrushBuf` behind `#define SDF_BRUSH_BUFFER` (raymarch only; the five compute shaders keep the texture). The buffer is
allocated and uploaded even while the ConVar is off (~4 MiB for 256 props, one synchronous ~16 KB upload per repack).
Remove it if #3 is dropped for good. #3b: `SdfBrushPacker.PackGroups` appends 32 × 3 texels after the brush texels
(texture now 992 wide in the renderer; the field baker's texture is unchanged). `SdfSurfaceLocal` keeps one
`SdfSurfaceBrush` call site: mode 1 sets a skip-until index so k stays wave-uniform; mode 2 jumps `k += 3`. Not done:
the same group cull in `SdfDist` for the field bake (#8's rebake spike). It would reuse the add / subtract / cutout+gap
terms; colour brushes never reach the field.

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

**#18 mesh-shell proxy result (2026-10-25, in-run A/B, 8× MSAA, 3 × 4 s).** Built as agreed: the shell is the
sculpture's own surface-nets mesh, drawn by the raymarch `SceneObject` through a single-LOD Model that shares the
LOD's Mesh (no copy of its buffers) with the raymarch material as an override. The VS pushes it out along its
normals (`SdfShellInflate`); the PS marches FRONT faces from the fragment, keeps the box exit, and restarts from the
box entry when the first sample is inside the clay (debug skip bit 1024 paints shell / restart / box). The gate is in
`SdfRaymarchRenderer.UpdateMeshShell`. It needs the mesh's `ContentHash` to equal the current brushes' (any edit
→ box the same frame; damage brushes → box), plus not boiling, camera beyond 2 radii (`_overdrawSafe`), normal view
layer, no `TargetWorld`, not `RenderHidden`, no `FlipFaces`. Shadow views keep the box rule (back faces from the box
entry) on the shell geometry. Box ↔ shell is a model swap only on a state change. ConVar off: the shader's box path
renders bit-identical to before (old vs new A-arm images, 3 of 4 ship views; 2 pixels in overview).

| shell variant (`ship` suite unless noted) | close | dense | overview | complex | images |
|---|---|---|---|---|---|
| LOD0, δ = √3 cells + 1 voxel | +0.10 | **+1.46 SLOWER** | **+2.67 SLOWER** | +0.01 | 0.4–1% >10, but thin features missing |
| LOD1 / LOD2 meshes | +0.37 / −0.51 | +0.20 / −0.23 | +0.53 / −0.06 | +0.81 / +0.43 | **holes**: 2–12% >10 |
| dilated LOD1 / LOD2 (1 cell + VS 1 cell + voxel) | −0.06 / −0.02 | **+0.74 SLOWER** / **+0.23 SLOWER** | **+1.44 SLOWER** / +0.92 | −0.63 / −1.10 (A arm noisy) | ≤0.32% >10, hole-free |
| dilated LOD2, shell only within 4 / 8 radii | −0.33 / −0.97 (A noisy) | +0.62 / +0.74 | +0.06 / +0.25 | ≈0 | ≤0.09% >10 |
| `shader` suite: LOD0 | +0.19 | **+1.20 SLOWER** (spots/sun/far +1.3…+1.8) | — | −0.04 | ≤0.4% >10 |
| `shader` suite: dilated LOD2 | −0.15 | +0.03 (sun +0.33, far +0.50) | — | +0.06 | ≤0.1% >10 |

Read the pass buckets, not the whole-case ms (several A arms were noisy): **the prepass never improved by more than
0.05 ms on an ordinary view** (complex −0.26 once). The forward pass got slower wherever many props wear shells: LOD0
+0.8…+2.0 ms, dilated LOD2 +0.06…+0.34 ms. Shadows +0.2–0.35 ms on concave props in the sun case: shell back faces
march from the box entry, once per layer. Shell coverage was 245–255 of 256 props (the 083 rug has no mesh and
stays on the box; props within 2 radii stay on the box).

Why it loses: **quad overshading**. A shell tight enough to save steps is many small triangles. Every triangle edge
inside a silhouette splits 2×2 quads, so the pixels along it run the whole pixel shader (march + ~1.8 ms of post-hit
shading) once per triangle, with helper lanes. The box has almost no interior edges. Coarser meshes cut the edges,
but a coarse shell has to be fat, so the margin it removes is small. The heatmaps' 70–80% miss steps in the box
margin are cheap steps: rays that leave the box in 1–6 steps.

Correctness findings, kept for any later shell or proxy work:
- **Surface-nets meshes drop thin features at every LOD.** Knife blades, table legs, a thin cloth: a feature with no
  grid point inside it has no surface. Those pixels are never rasterised, so the restart can't catch them. The fix
  is the dilated shell: mesh the grid at iso-level +1 cell (`SurfaceNetsMesher.Dilated`, which forces the outer
  grid layer outside so the shell closes). Any feature then has a lattice point inside it. Close-ups (fruit bowl,
  knife rack, side table, chair, candelabra, revolver, jug; `sdf_dev_view <i> 3`): 0 restarts, ≤0.16% of pixels >10.
- **Deeper shell layers start inside nearer clay** (crevices, folds of the inflated shell). They restart, re-march
  to layer one's hit and paint the restart debug red without being a hole (0.8% of shell pixels in dense). The
  forward pass now drops a layer behind the prepass depth before the inside test (0.14% left: real first-layer
  tightness). The prepass can't, so it pays those re-marches.
- Edit test (jug, brush grown 10%): box in the same frame as the edit, shell back 20 ms later when the mesher's
  build for the new shape lands. Grown 25%, the camera fell inside 2 radii, so it correctly stayed on the box.
- The sandbox engine's `SceneObject.LodOverride` is internal, hence the single-LOD Models. The surface-nets
  `Vertex` normal reads fine raw (`vNormalOs.xyz`).

**Decision: ships OFF, code left in place.** Defaults are the hole-free variant (dilated LOD2, VS 1 cell); its meshes
are built only while `mimiclay_sdf_mesh_shell` and `_dilated` are on at mesh-build time, so it costs nothing while
off (their GPU memory when on was not measured). While off, the remaining cost is the per-LOD single Models
(objects only, no buffers) and the per-frame gate (bench `cpu` +0.05–0.1 ms only while the shell is on). Before phase 2 (conservative depth on the shell) is worth trying, the forward pass needs to stop paying for the
shell's triangles. One route: the shell draws only the depth prepass and the box draws forward + shadows, but the
prepass gain measured here is ≤0.05 ms. That makes the whole direction low value at these views.

Also landed in `55543a3`: cutout colour-edge cull fix (`sdf_eval.hlsl` `SdfSurfaceBrush`), and the shadow march
knobs now gate on `IsShadowView()` alone so ortho editor views march like cameras (cascades verified still detected).

**Final pass (2026-10-25): #19 shading-path comparison, #20 transmission, #2 conservative depth.** All in-run A/B, 8×
MSAA, 3 × 4 s. Every candidate read `same` and was reverted; the tree is back at `f839893` for shaders and code. The
diagnostic bits used here (1024 lighting off, 2048 lane-utilisation heatmap, 4096 transmission march off, 8192
transmission tap off) were reverted with them; the full experiment sources are in the side-session notes folder
(`final-pass-2026-10-25.md`). **Bench warning from this sitting:** the 60 fps-capped `ship` cases flipped between a
fast and a slow regime *between runs* (same code: close 1.77 vs 4.0 ms, prepass 0.36 vs 0.93), and arms within one
run moved ±0.5 ms on forward. Read only in-run differences, and prefer the uncapped `shader` cases (dense/close/complex
held to ±0.02 ms across arms).

*#19, what the remainder is.* Forward ms per arm, [attribution run](Tools/sdf/bench/runs/20261025-160846-ship-remainder-attribution.json):

| forward ms | close | dense | overview | complex |
|---|---|---|---|---|
| A (full) | 3.19 | 2.66 | 2.31 | 2.78 |
| bit 47 (five SDF stages off) | 1.76 | 2.03 | 1.26 | 0.82 |
| bit 47 + 1024 (`ClayShade` off too) | 1.19 | 0.90 | 0.86 | 0.46 |
| bit 64 (march only) | 1.12 | 0.86 | 0.83 | 0.43 |
| all-mesh (`force_mesh`), whole forward | 0.33 | 0.59 | 1.51 | 0.20 |

So the remainder (47 − 64) is **`ClayShade`, the engine lighting: 0.36–0.6 ms** (dense's 1.13 is a noisy A/B; the strip
build below reads 0.58). The SDF glue between hit and lighting (`BoilTexOffset`, the model fold, `Material::Init`, the
cluster re-seat) is 1071 − 64 = **0.03–0.07 ms**. Checked against `sdf_mesh.shader` by source: both call the same
`ClayShade` (decals, direct + indirect lighting, the MSAA-aware AO gather, fog). Nothing in the SDF path makes the PS run
per sample (no `SV_SampleIndex`, `sample` inputs or coverage output; `pixelinput.hlsl` has only a `centroid` lightmap UV),
and the forward depth clamp keeps hidden-surface shading to the 2 u slack. **Lane utilisation is not it:** heatmap 2048
(the wave's share of lanes reaching shading) reads ≥ 97% for 95–99% of shaded pixels in all four views (silhouettes
mean 83%).

**The SDF-only part is occupancy.** Runtime skip bits don't change the register allocation, so compile-time strip builds
(`SDF_SKIP` forced on for chosen bits) measured it. March-only forward (bit 64) as the probe, one run per build:

| build | close | dense | overview | complex |
|---|---|---|---|---|
| full | 1.12* | 0.86 | 0.83 | 0.43 |
| transmission compiled out | 0.66 | 0.85 | 0.83 | 0.43 |
| only the walk kept (2, 4, 8, 32 out) | 0.64 | 0.84 | 0.82 | 0.42 |
| only transmission kept (1, 2, 4, 32 out) | 0.64 | 0.83 | 0.81 | 0.42 |
| all five compiled out | **0.52** | **0.67** | **0.69** | **0.31** |

\*noisy (that run's close A arm spread 4.70–5.45 ms); the three partial builds agree on 0.64.

The material walk alone and transmission alone each set the same register peak; only removing both (with the three small
stages) drops it, and then the march runs 0.12–0.19 ms faster (lighting at the stripped build's occupancy: 0.34–0.58 ms,
close to the full build's). This is why the mesh's whole forward pass undercuts the SDF remainder: the same lighting runs
with more warps in flight there. Turning it into a saving needs both the walk (7 RGBA32F brush texels + the whole
`BrushDist` primitive switch live at once) and transmission's light loop under the next occupancy step, with no way to
read register counts on this machine (no Nsight Graphics). Not pursued.

*#19b, doubled box faces.* `Tools/PropGen/gen-*.ps1` write `OverdrawOptimization = $false`, so 146 of the 256 dev props
(380 of 660 renderers in prefabs/scenes) rasterise both box faces. Forcing it on everywhere, or off everywhere, changes
nothing measurable ([shader run](Tools/sdf/bench/runs/20261025-173812-shader-overdraw-none-front-depth.json), all
single-faced vs all both-faced: dense 2.36 → 2.32, close 1.65 → 1.64, complex 1.01 → 1.00 ms; prepass ≤ 0.03 ms; images
identical). Both faces do reach the shader (a camera inside the box still draws the prop with both on). Why the second
face is free is unexplained; the measurement is what counts. Related finding: **`SV_IsFrontFace` is inverted relative to
the box geometry here.** A debug paint of `SV_IsFrontFace` fragments at their raster depth lands behind the props (exit
faces), so the default path, which clips "front faces", actually marches the ENTRY faces. That is why the prop vanished
with the camera inside the box (2026-08-25) and why the `OverdrawNearRadii` gate fixes it. The comment at
`SdfPixelOutput` ("back faces put the hit in front of the raster depth") describes the opposite of what runs.

*#20, transmission.* [trans-fast](Tools/sdf/bench/runs/20261025-163657-ship-trans-fast.json) and
[lazy-tap](Tools/sdf/bench/runs/20261025-164536-ship-trans-lazy-tap.json), sdf ms (prepass / forward):

| arm | close | dense | overview | complex |
|---|---|---|---|---|
| no-op (`trans_fast=0`) | 3.79 (0.92 / 2.42) | 4.67 (0.94 / 3.20)* | 3.41 (0.65 / 2.43) | 2.96 (0.45 / 2.00) |
| wave-uniform light loop (`trans_fast=1`) | 4.33 (0.92 / 2.43) | 4.61 (0.94 / 2.75) | 3.64 (0.63 / 2.48) | 2.96 (0.45 / 2.01) |
| transmission off (bit 8) | 3.61 (0.92 / 2.20) | 3.78 (0.94 / 2.36) | 3.41 (0.66 / 2.23) | 2.73 (0.45 / 1.78) |
| thickness march off (bit 4096) | 3.73 (0.92 / 2.36) | 4.06 (0.94 / 2.64) | 3.49 (0.64 / 2.36) | 2.90 (0.45 / 1.94) |
| lazy inward tap, its own run | 3.82 (0.92 / 2.46) | 4.79 (1.14 / 3.29) | 3.59 (0.63 / 2.46) | 3.71 (0.45 / 2.04) |
| inward tap off (bit 8192), same run | 4.30 (0.92 / 2.45) | 4.78 (0.94 / 3.30) | 3.59 (0.62 / 2.38) | 3.73 (0.46 / 2.03) |

\*dense's no-op arm caught a slow rep; read it against A (forward 2.74). The lazy-tap run's own no-op: forward 2.47 / 3.27 / 2.46 / 2.04.

Transmission costs 0.22–0.38 ms of forward. The 6-step thickness march is only 0.06–0.13 ms of it (already gated on
back-lighting), and the inward tap ~0 (−0.08…+0.01 ms). The wave-uniform rewrite (`ClusterForEach` as in `vr_lighting`,
light constants once per wave, no `Shadows()` in `Light::Init`) was bit-identical and `same` (forward +0.01 ms): the
compiler already dropped the unused shadow sample. The lazy tap (taken only when a light's fill weight is non-zero)
was exact and `same`; with the sun on, every pixel's fill needs it anyway. The rest is the light loop itself plus
transmission's share of the register peak (#19), which no exact rewrite here reached. With no image change in any kept
arm, the close-up look check was not needed; the earlier back-lit band fixes are untouched.

*#2, front-face conservative depth.* Built as planned: a static combo `S_FRONT_DEPTH` (feature `F_FRONT_DEPTH`, excluded
with `F_TRANSLUCENT`) with `SV_DepthLessEqual`; every view is reversed-Z (`GREATER_EQUAL` test; the engine's spot, cube
and cascade shadow code all linearise reversed-Z), so a hit behind the entry face is a smaller value. Entry faces were
picked by the box's outward face normal against the view ray (the box mesh got 24 face vertices with real normals; the
old 8-vertex box measured the same), and the output clamped to the raster depth so the promise always held. The
renderer swapped a runtime twin (`Material.CreateCopy` + `SetFeature`, keeps the `alphatest` flag) in with
`SceneObject.SetMaterialOverride` while `_overdrawSafe` held, box proxies in the scene world only, and kept props on the
plain path while they marched shadows with a shadowed spot/point light inside their box. Verified live by a debug paint
inside the variant (247 of 256 props on it in the dense view).

| `shader` suite, sdf ms (prepass / forward) | dense | close | complex | dense-4spots | dense-sun | dense-4spots-far |
|---|---|---|---|---|---|---|
| no-op arm | 2.34 (0.72 / 1.28) | 1.64 (0.44 / 1.15) | 1.00 (0.22 / 0.73) | 3.38 (0.72 / 1.70) | 3.22 (0.68 / 1.37) | 3.95 (0.75 / 2.40) |
| front depth | 2.06 (0.70 / 1.25) | 1.64 (0.43 / 1.15) | 1.00 (0.22 / 0.73) | 3.70 (0.70 / 2.07) | 3.38 (0.68 / 1.37) | 3.71 (0.75 / 2.18) |

(From [shader-2](Tools/sdf/bench/runs/20261025-173812-shader-overdraw-none-front-depth.json); the first
[shader run](Tools/sdf/bench/runs/20261025-172754-shader-overdraw-all-front-depth.json) gave dense / close / complex
2.32 / 1.65 / 1.01 in every arm, prepass 0.69 / 0.43 / 0.22.) The prepass, where the gain was expected, moved
−0.03…0 ms; the spot cases' forward swings both ways in every arm, including no-ops. Images identical everywhere. So on
this GPU, `SV_DepthLessEqual` under a `GREATER_EQUAL` test bought no early rejection (the forum report's "one direction
doesn't early-cull"). Worth one re-check on the RTX 3060-class target before closing it for good; the variant would be
the code above.

Process notes: a hotload failed upgrading a new static `List<Vector3>` ("Destination array is too small") and left the
renderers half-upgraded (everything above the screen's centre line stopped drawing, still reading as "faster"); two
runs from that state were deleted. Restart play after any hotload error before benching.

Dropped after research: Brixelizer (already have an I8 atlas), hardware-RT bricks (not exposed), Dreams splats,
Harnack tracing, backface distance fields (bake too slow for boil rebakes), BC4 fields (too coarse at the surface),
general per-pixel brush-cell lists (tried 2026-10-24, nothing in crowds).

## Pre-merge trim (2026-10-25)

Removed every path that was OFF by default with no measured gain: prepass depth reuse, the baked-material prototype
(`sdf_material_cs`), the brush structured buffer, the static shadow cache (`SdfStaticShadows`), the shadow step cap /
near-miss knobs, and debug bits 16 / 256 / 512. All of it is recoverable from commit `a1507fd`. Kept: group cull (exact,
still default off), uniform material, register reorder, edge AA, FXAA. Verified back to back: forward registers 123.3 →
123.4 per thread (Nsight), images identical in all 10 cases, uncapped `shader` suite same or faster in every case
([before](Tools/sdf/bench/runs/20261025-211255-shader-trim-before.json) /
[after](Tools/sdf/bench/runs/20261025-212040-shader-trim-after.json)), main-thread SDF update 1.62 → 1.34 ms/frame
(no per-repack brush-buffer upload or static-shadow bookkeeping). The capped `ship` cases swung both ways between runs,
the known regime flip ([before](Tools/sdf/bench/runs/20261025-211643-ship-trim-before.json) /
[after](Tools/sdf/bench/runs/20261025-212406-ship-trim-after.json)).

## Rules

- Keep 8× MSAA, resolution, field density and the SDF→mesh hand-off distances fixed while measuring. Lowering any
  of them is a separate quality decision, not an optimisation.
- Avoid adding dynamic shader combos for experiments. The raymarch shader's compile time is already the
  bottleneck. Use runtime uniforms instead.
- After changing shading, look at the close-up views in the development scene (`sdf_dev_view <prop> 1`) as well as
  the numbers.
