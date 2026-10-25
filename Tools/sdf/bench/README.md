# SDF benchmark

A short, repeatable benchmark for the SDF renderer. It runs in the editor on the 256-prop development scene
(`Assets/scenes/sdf_development.scene`), writes one small JSON file per run to `runs/`, and compares two runs.
Code: `Editor/SdfBench.cs` (runner and comparison), `code/Debug/SdfBenchProbe.cs` (scene readouts).

## How it measures

Every timed sample is paired with a **control** sample: same camera and lights, with every SDF prop hidden
(`mimiclay_sdf_hide_all`). The SDF cost is:

> **sdf ms** = median GPU time per frame with the props − median GPU time without them

This removes the editor, sky, floor and lights from the result, so a 0.3 ms shader win shows up as 0.3 ms.
GPU time is the engine's graphics-queue busy time for each frame. It is read from the GPU profiler's raw per-frame
timestamps, not its smoothed display values. The same per-pass data gives the breakdown columns
(prepass / forward / shadows / other), also as with-props minus control. `other` can be slightly negative: with
the props hidden, the background behind them draws instead.

The on/control pairs are repeated (3× by default), and each result is the median of those repeats. Within a sample, per-frame **medians** are used (headline and every pass). The `slow` column is the share of frames over 1.3× the median. `range` is the
min–max of the per-repeat sdf ms. It shows how noisy that case is.

Each sample is 2 s (`"seconds"` changes it). A step that only flips a ConVar or the control's hide flag settles for
0.4 s; one that moves the camera or changes lights, sun or LOD policy settles for 1.5 s and waits for fields and
meshes to finish. At ~200+ fps a 2 s sample is 400+ GPU frames. Validated 2026-10-24: a no-op A/B arm reads `same`
in every case (±2.2%). Sub-0.25 ms effects can hide in 2 s noise — use `"seconds": 5` to confirm a small one.

Each case also records:
- **cpu**: main-thread ms per frame spent in all SDF renderers' `OnUpdate` (band logic, refresh, visibility).
- **MiB**: counted SDF texture memory and unique fields at the end of the sample (residency).
- **a reference image**: rendered after timing, from the last repeat. The frame is rendered twice, and the two
  renders are compared to give the case's **image noise**. On this machine two renders of the same frame are
  bit-identical, so any image difference between runs is a real rendering change.

While running, the benchmark turns off VSync and frame caps (except the cap below), pauses editor scene views, enables the GPU profiler and
disables the fly camera. All of these are restored afterwards. It never saves the scene.

**Spot/point-light cases run capped at 60 fps.** Shadow-map passes are memory-latency-bound: dependent 3D-texture
reads into the fields. Nsight (2026-10-24) showed that in a frame that starts after the GPU idled, the spot shadow pass
hits L2 only ~25% of the time (the forward pass after it: 66–90%), and it costs ~4× as much (≈9 vs ≈2.4 ms for 256
props × 4 spots). Uncapped, the editor flips between the two regimes sample to sample, which made spot results
unrepeatable. Capped, every frame is in the cold regime, which is repeatable and is also what a player with V-sync or a
frame cap sees. The sun case stays uncapped: its cascades went bimodal under a cap but are stable uncapped. Each case
records its `Cap` in the run file. Outside GPU load (video playback, Steam recording) did not cause the slow regime;
it only changed how often it appeared uncapped.

## Suites

**`shader`**: the raw cost of the SDF shader. Every prop is at full quality, raymarched, and casts a marched shadow.
About 1.5 minutes; each extra A/B arm adds about 1 minute.

| case | what it stresses |
| --- | --- |
| `dense` | ~90% of the screen is clay, four overlapping layers: march + shade per pixel |
| `close` | big props filling the screen: pixel pressure, steps per ray |
| `complex` | one 42-brush prop (fruit bowl) close up: brush evaluation / material cost |
| `dense-4spots` | `dense` + four shadowed spotlights, all 256 props as marched casters: shadow-view marching |
| `dense-sun` | `dense` + the sun casting (4 ortho cascades), uncapped |
| `dense-4spots-far` | the four spots pushed 500 u back (radius grown to match): large shadow-map texels, like a ceiling light across a room. Catches texel-scaled shadow settings that only misbehave far from the light |

**`lod`**: how much the LOD systems save, and what they cost. Five distances (`close`, `dense`, `overview`, `far` =
1200u back, `distant` = 3000u back), each under five policies, with four shadowed spots. About 5–6 minutes.

| policy | meaning | read it against |
| --- | --- | --- |
| `full` | everything marches at full quality, every prop a marched shadow caster (the shader-suite setting) | — |
| `adaptive` | + `AdaptiveQuality`: the shader's own distance-based march quality | `full` |
| `shadow` | `adaptive` + the authored shadow LOD: props beyond `SdfShadowRadii` cast with their mesh | `adaptive` |
| `mesh` | `adaptive` + `DistanceSwitching`: far props draw as meshes; in-band props still march their shadows | `adaptive` |
| `lod` | everything on: `adaptive` + shadow LOD + mesh hand-off (what the game ships) | `shadow` |

The `full` and `adaptive` rows force all 256 props to march their shadows from four spotlights. The game never
ships that: its default shadow cap is 2 radii. So treat `shadow` as the realistic baseline, and `lod` vs `shadow` as
what the mesh hand-off is worth in practice.

`dolly-full` / `dolly-lod` fly from 3000u back into the close crowd, so every band transition happens. They have no
control. Compare their total GPU ms, frame p99 and hitches (>33 ms frames) instead.
The `bands` column shows how many props were in each band (`sdf`, `mesh0–2`, `culled`), plus `marchedShadows`.
`noMesh` counts props in mesh mode that have no built mesh. Those draw nothing, so treat a "saving" from them as a bug.

## Running

The editor menu is **Mimiclay → SDF Bench → Run Shader Suite / Run LOD Suite / Compare Latest Two**.

Through MCP (`sbox-cli --call …`):

```text
sdf_bench_run      {"suite":"shader","label":"baseline"}    # label defaults to the git commit (+ "-dirty")
sdf_bench_status
sdf_bench_compare  {}                                        # newest vs the previous run of the same suite
sdf_bench_compare  {"baseline":"baseline","candidate":"r16f"}   # any part of the file names
sdf_bench_stop
```

If the development scene is already playing, the run reuses it and restores its view, lights and renderer settings
afterwards. This skips the ~1 minute field build, so it suits a hotload loop. Otherwise it opens the scene, plays
it, and stops it when finished.

`Quick Shader Check` (1 × 3 s) is a smoke test. It is marked `Quick` in the file. A single repeat has no spread to
judge noise by, so comparisons label its cases `1 rep` instead of giving a verdict.

## Two ways to A/B

**Behind a ConVar: in-run A/B (preferred).** Put the optimisation behind a runtime toggle (a ConVar that drives a
shader uniform, as `mimiclay_sdf_group_cull` does). Then:

```text
sdf_bench_run {"suite":"shader","label":"xyz","ab":"mimiclay_sdf_my_toggle=1"}
```

Several arms in one run, separated by `;`, share the same A and control. That is how to price several stages at
once, for example the attribution run:

```text
sdf_bench_run {"suite":"shader","ab":"mimiclay_sdf_debug_skip=1;mimiclay_sdf_debug_skip=2;mimiclay_sdf_debug_skip=16"}
```

Every case is measured as-is (A) and then with the overrides applied (B), back to back in the same session, against
the same control. There's no hotload or shader recompile between A and B, and no drift between runs. The run ends with
an A/B table and an amplified diff image per case (`runs/images/<run>/diff-ab/`). This is the most precise
comparison the benchmark can make. Avoid adding shader combos for experiments; the raymarch compile is already slow.

**Across a code change: two runs, back to back.** Runs taken in one sitting agree to about ±2%. Runs an hour
apart have drifted 5–10% with no code change at all, so a stored baseline can't anchor a small claim. The committed
baseline is for orientation; for a verdict, measure the baseline and the candidate together. `compare` warns when
two runs are more than 20 minutes apart.
1. Run the suite on the current code: `label: "before-xyz"`.
2. Make the change and let it hotload/compile. Shader compiles take minutes, so wait for them to finish. The run
   warns if a compiled `sdf_*.shader_c` is older than its `.shader` or any local `#include`. The editor doesn't
   always recompile after an `.hlsl`-only edit; touch the `.shader` if it warns.
3. Run again: `label: "xyz"`.
4. `sdf_bench_compare`. Each case's verdict is `FASTER`/`SLOWER` only if it moved by more than max(3%, twice
   the baseline's own repeat spread) **and** the two repeat ranges don't overlap. Capped spot cases drift about ±4%
   between arms, so their bar is higher. Otherwise it is `same`. The image column shows how many pixels changed by more than
   2 and 10 (of 255), with the noise floor next to it. Inspect the amplified diffs in
   `runs/images/<candidate>/diff-vs-<baseline>/`.
5. For a small or surprising result, run the baseline again (A/B/A). Effects under ~0.25 ms need longer samples
   (`"seconds": 5`).

`set` applies ConVar overrides to a whole run (`"set":"mimiclay_sdf_field_res_cap=96"`). They're recorded in the
file and shown in comparisons. Each run also stores a `SourceHash` over every `code/Sdf` and `Assets/shaders`
source. Equal hashes in a comparison print "sources identical", meaning any difference is noise or settings. A
hash that changes mid-run, from a hotload landing during the benchmark, produces a warning.

Compare only runs from the same machine, resolution, MSAA, render scale and shadow quality. The comparison prints a
⚠ line when any of these differ. Keep the game view at 1920×1080; the run warns otherwise. Resolution changes the
result more than most shader changes do.

## Run files

`runs/<date>-<suite>-<label>.json` (a few KB) records:
- the commit, the number of dirty code/shader files, `SourceHash`, the `Set`/`Ab` overrides and `Warnings`;
- the environment and SDF memory (`Memory`: unique fields, counted MiB);
- for each case: `SdfMs` + range, `GpuMs`, `ControlMs`, frame p50/p99, hitches, `RendererUpdateMs`, `CountedMiB`,
  `Buckets`, `Passes` (every leaf pass that moved by ≥0.05 ms), `Bands`, `Image` and `ImageNoise`.

Commit the runs you want to keep as reference points and delete the rest. Images go in `runs/images/` (~2 MB each),
which is gitignored. Keep the baseline's images locally, because comparisons diff against them.

## Not covered yet

The suites measure static props. These need their own cases before optimising them: boiling/displaced props (the
scene has no ClayBoil), live edits and field rebakes, the viewmodel and editing views, and first-load/compile time.
Everything so far was measured on an RTX 4090. The target hardware (RTX 3060 class) can rank the costs
differently, so run the suites there before trusting a trade-off between two passes.

## The development scene by hand

**Mimiclay → SDF Development Scene**, then Play. It contains 256 distinct props in four overlapping layers, with
props forced to full quality. Keep it playing while you hotload. The console commands:

| command | |
| --- | --- |
| `sdf_dev_view -1` / `-2` / `-3` | dense / overview / close crowd |
| `sdf_dev_view 63 1` | close-up of prop 63 (any 0–255) at 1 bounding radius |
| `sdf_dev_lights 0` … `8 [shadows]` | number of local spotlights |
| `sdf_dev_shadow_policy true/false` | force every prop to cast marched shadows / authored policy |
| `sdf_dev_stats` | field readiness, unique fields, counted MiB, bands |
| `mimiclay_sdf_hide_all 1` | hide every SDF prop (the benchmark's control) |

Hold right mouse to fly. A benchmark run started while this scene is playing uses it as-is, so the fields don't
need rebuilding.

## Shadow settings

How SDF props march their shadows is global, in `code/Sdf/SdfShadowSettings.cs`. Each value is a ConVar, so you can
try one live, or A/B it with several variants in one run:

```text
sdf_bench_run {"suite":"shader","ab":"mimiclay_sdf_shadow_texel_eps_local=3;mimiclay_sdf_shadow_texel_eps_sun=2"}
```

The doc comment in that file records the sweep that chose the current defaults, with shadow-pass time and visible
pixel change for each setting. Update it when you change a default.

## Changing the workload

The workload is the saved development scene plus the case tables at the top of `SdfBench.cs`. Changing either
invalidates comparisons with older runs. If you change them on purpose, bump `Version` in `SdfBench.cs`. Comparisons
then warn about the version mismatch.
