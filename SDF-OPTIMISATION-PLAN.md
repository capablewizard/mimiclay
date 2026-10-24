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

## Rules

- Keep 8× MSAA, resolution, field density and the SDF→mesh hand-off distances fixed while measuring. Lowering any
  of them is a separate quality decision, not an optimisation.
- Avoid adding dynamic shader combos for experiments. The raymarch shader's compile time is already the
  bottleneck. Use runtime uniforms instead.
- After changing shading, look at the close-up views in the development scene (`sdf_dev_view <prop> 1`) as well as
  the numbers.
