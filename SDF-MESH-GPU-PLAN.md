# SDF mesh: sample the GPU field instead of re-evaluating brushes

**Step 1 landed 2026-08-26.** The CPU mesher no longer evaluates brushes. Step 2 (surface nets in compute) is
still deliberately deferred; see the end.

## The problem it solved

`SurfaceNetsMesher` built the LOD/shadow mesh by calling `Sdf.Sample` / `Sdf.SampleSurface` / `Sdf.Gradient` —
the **CPU analytic brush evaluator**, a second independent implementation of the field the GPU already knows how
to compute. The two drifted: Text brushes meshed as a plain box for months because the CPU path falls back to a
box when the glyph field isn't baked, while the GPU path never did. Every new brush property had to be added to
both, which is what the add-a-property checklist exists for.

## What it looks like now

Sampling and meshing are separate steps:

- **`SurfaceNetsMesher.SampleGrids( brushes, res0, res1, res2 )`** — MAIN THREAD. Produces one `MeshGrid` per
  resolution via `SdfMeshGridGpu`, which dispatches `shaders/sdf_mesh_grid_cs` over the mesher's own grid.
- **`SurfaceNetsMesher.ComputeData( in MeshGrid, flip )`** — WORKER THREAD. Pure array work: vertex placement,
  normals, attributes, quad stitching. Knows nothing about brushes.

`SdfSculpture.BuildModelAsync` stages it main → worker → main. `SdfBakeUtility` does the same for `.sdfmesh`.

**One RGBA32F texel per grid point**: `.r` distance, `.g` metalness, `.b` roughness, `.a` sRGB colour packed as
`r + g·256 + b·65536` (under 2²⁴, so float32 holds it exactly). Packing the colour keeps it to one dispatch and
one readback. The CPU unpacks the eight corners *before* interpolating — lerping packed values is nonsense.

Normals now come from a central difference of the **sampled grid** rather than a fresh analytic gradient. That
removed six brush-list walks per vertex, and it is arguably more correct: it's the surface the mesh actually has.

`sdf_raymarch`'s `SdfShade` moved into `sdf_eval.hlsl` as `SdfSurfaceLocal`, so the march and the mesh bake
colour from one definition. `SdfShade` is now just the world→local fold.

## Two things that were not obvious

**The 3D readback is one engine call per Z slice.** `Texture.GetPixels3D` loops `GetPixels` over depth, so
reading a 33-deep volume is ~33 GPU round trips. That alone was 1161ms of a 1640ms chain benchmark. The grid is
therefore a **2D texture with the Z slices stacked down Y** (`y = point.y + Dims.y · point.z`) — the same trick
`SdfFieldGpu` uses for its indirection texture — and one `GetPixels` reads the lot. 1161ms → 97ms.
Use the `dstSize` overload, not `dstRect`/`dstStride`: the latter sizes its bounds check from a format table with
no RGBA32F entry and throws `NotImplementedException`.

**Exact-zero samples have no side.** A flat face landing on a grid plane samples to zero, and which way it rounds
is float noise — it differed between CPU and GPU, and would differ between two runs of either. `BreakSurfaceTies`
pushes every tie outside by `cell·5e-3`, which is both deterministic and the better answer (the edge crossing then
lands on the face plane instead of a whole cell out). The band has to clear the noise floor, not just exact zeros:
`1e-3` still left glyph-edge samples flipping, because the GPU reads the text field through a hardware bilinear
sampler (fixed-point sub-texel weights) while the CPU fallback interpolates in floats. `5e-3` of a cell is roughly
5× the worst measured disagreement and still far too small to move a vertex visibly.

Why it matters more than it sounds: vertices are emitted in cell order, so **one flipped sample renumbers every
vertex after it**. An index-for-index comparison then reports a one-sample difference as a catastrophic one, which
is exactly how this looked before it was tracked down. `mimi_mesh_verify` now prints how many vertices differ and
the first index, so a renumbered tail is distinguishable from a real divergence at a glance.

## Results (kitchen scene, 145 unique shapes, full 3-LOD chain at res 32)

| | CPU evaluator | GPU grid |
|---|---|---|
| total | 5302 ms | **539 ms** (9.8×) |
| per shape | 36.6 ms | **3.7 ms** |
| worst single shape | 486 ms (`generator`) | **8.3 ms** (`paintcan`) (59×) |

The worst-case number is the one that matters: meshing cost no longer scales with brush count, so the pathological
props that used to hitch a scene load don't any more. What remains is ~0.7ms of GPU sync per prop plus the CPU
stitch, which is now the larger half.

Correctness: `mimi_mesh_verify` reports **no structural differences** across every shape in the scene at res 16
and res 32 — geometry typically within a few hundredths of a percent of a cell, most shapes bit-identical.

## Tools (Editor/SdfMeshBench.cs)

- `mimi_mesh_chain [res] [runs]` — the three-LOD chain as the sculpture builds it. **The number that matters.**
- `mimi_mesh_bench [res] [runs]` — per-LOD phase split. Over-counts the GPU sync threefold; use `chain` for totals.
- `mimi_mesh_verify [res]` — mesh every shape both ways and compare. Run this after any brush-evaluator change.
  Reports `STRUCTURAL` (a difference spread across the mesh = a brush property one path is missing — the thing to
  care about) versus `minor` (a few boundary samples rounding differently, which is expected).
- `mimi_mesh_probe <name> [res]` — where two grids disagree: sign flips, worst delta, and the brushes near it.
- `mimi_mesh_rebuild` — kick real rebuilds through `RebuildAsync`; the others call the mesher directly.
- ConVars: `mimiclay_mesh_gpu` (kill switch, also clears the give-up latch), `mimiclay_mesh_gpu_cull`.

## Known, accepted differences

- **Colour at smooth-union seams.** The GPU blends brush colours in LINEAR space (as the raymarch does); the CPU
  mirror blends in gamma space. Seam texels shift by up to ~40/255 on a few props. The GPU is the one that now
  agrees with the marched surface, so this is a fix, not a regression.
- **Cutout recolour** is sampled on the grid rather than exactly per vertex, so its 1-unit colour edge aliases at
  cell sizes above 1 unit. Only affects meshed LODs, which are what you see at distance.

## Who consumes the mesh (unchanged, all still working)

| consumer | needs CPU vertex data? |
|---|---|
| far-LOD render + shadows (`SdfSculpture` → `ModelRenderer`) | no, GPU only |
| `SdfBakedMesh` Pack/Unpack (`.sdfmesh` bake) | **yes** — editor only |
| `PropSpawnerBase` placement bounds | **yes** |
| physics | **no** — `SdfCollisionBuilder` emits per-brush hulls/spheres, never touches the mesher or the field |

The CPU evaluator is still there as a fallback (`SampleGridCpu`) for a worker thread, a context without compute,
or after a GPU failure — it writes the identical grid layout, so the meshing never knows which one ran.

## Step 2 (still deferred) — surface nets in compute

Feasible: `Graphics.Draw<T>( GpuBuffer<T> vertexBuffer, GpuBuffer indexBuffer, Material, startIndex, indexCount )`
draws GPU-resident geometry. Blockers to weigh first:

- `indexCount` is a CPU int → needs a 4-byte counter readback (~1 frame) or a conservative max.
- Would draw from a `SceneCustomObject.RenderOverride`, so we own per-view + shadow-view rendering and bounds, and
  lose engine model LOD — the `LodOverride` path in `SdfRaymarchRenderer.ApplyVisibility` would need replacing.
- Doesn't let us delete the CPU mesher anyway: the bake and the spawner still need vertices.

Step 1's numbers say the remaining cost is split roughly 25% GPU sync / 75% CPU stitching, at ~3.7ms per prop.
Step 2 would attack the stitching. Not worth it until something actually hitches.
