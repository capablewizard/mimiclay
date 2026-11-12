---
name: sdf-prop
description: Build simple stylised SDF clay props (prefabs of SdfSculpture brushes) from a reference image or description, in the house style of the DevPropHunt props. Use when asked to make/model/sculpt a new prop, or to learn from the user's edits to one.
---

# SDF prop building

Props are a short ordered list of SDF brushes on an `SdfSculpture`, written as a `.prefab`. We author
them as **recipe scripts** (`Tools/PropGen/props/<name>.ps1`) that dot-source the shared kit
`Tools/PropGen/SdfPropKit.ps1` and emit the prefab. The user then reviews in the editor and often
hand-tweaks the prefab. **Their tweaks are the most valuable signal in this skill**: always diff them
back (see "Learn from the user's edits") and record the lesson in the Style log below.

## House style (measured from the DevPropHunt props)

Reference set, all hand-made: `Assets/Prefabs/Props/Garden/{gardenpot,gardenpot_small,pumpkin,bowlingpin,WaterButtLid}.prefab`,
`Assets/Prefabs/Props/Kitchen/{mug,pot,pot_pitcher,teapot,sugar}.prefab`. Dump any of them with
`powershell -ExecutionPolicy Bypass -File Tools/PropGen/brushdump.ps1 -Files <prefab>...`.
NOT style references: `Assets/Prefabs/CastleCourtyard/*` and the other `gen-*.ps1` output (older generated sets).

- **Few, big brushes.** 3–11 brushes per prop. One primitive per readable form; no fussy detail.
- **Chunky + soft.** Big `Rounding` on the main body (pot: 15 on a 32 radius; gardenpot 3.5; pitcher 9).
  Main bodies use `Blend` ~6; welded secondary forms 1–4; crisp separate parts (rims, bands) 0–1.
- **Thick walls.** Hollows leave ≥ ~3 units of wall. Thin walls glow neon through the renderer's
  Transmission (saturated albedo leaks through) — the bucket's inside went royal blue at 2-unit walls.
- **Hollows = a Subtract** of the same shape slightly smaller, raised to leave a floor, `Blend` ~0.8,
  `Rounding` ~2–3 so the inside floor is soft. Its colour tints the cut walls: use `Hollow $body`
  (darker AND desaturated). Plain darker blues go neon; terracotta tolerates plain darkening.
- **Rims / lips** are a separate short cylinder (half-height 1–2.5), slightly wider than the body,
  `Rounding` = half-height for a rolled bead, low blend (≤1.2) so it reads as its own form.
- **Tapers via flipped cylinders.** Pots/buckets: `Rotation = $QFlip` + `Taper` 0.2–0.3 so they widen upward.
- **Handles = one Spline** (variable radius allowed), radius 1.5–3, low blend where it touches.
- **Colour:** flat, toy-like, one colour per part; sample the reference then keep it slightly muted.
  Accent parts (bands, lids, knobs) in a contrasting colour. Roughness 0.4–0.55, Metallic 0 unless
  it's real metal (gold trim: `0.8,0.686,0.349` metallic 1).
- **Scale:** inches, and BIG — props are hideable-in, not life-size. Mug ~30, sand bucket ~48, garden pot ~52,
  pitcher ~60, spade ~92 long, dustpan ~104 long (pan 75 × 66). My first guesses have run 1.5–2x too small every
  time; size against the user-tuned props in `prop_review.scene` and go bigger than feels right.
  Put the base near z=0.
- **Thickness scales with size:** walls/floors ~0.2 of the half-width (dustpan walls 7–8), never thin sheet.
- **Blunt edges:** lips and fronts end in thick flat faces (a near-vertical trim cut), not knife edges.
- **Colours a notch muted** from the reference image.
- **Secondary parts are crisp:** handles/rims/shafts that read as separate pieces get Blend 0 (≤1.4 at most);
  only the main body mass gets the big soft blends. Too much blend = "blobby", the user's most common fix.
- **Carve, don't stack:** shape a form by cutting a big rounded primitive with flat (sometimes tilted) Subtract
  slabs, rather than assembling many small adds. Tilted cuts give cheap visual interest.
- **My default is too simple.** The user has added detail to every prop. Before handing over, do a "one more
  level" pass: taper anything parallel-sided (wedge cuts), inset flat faces (shallow dish), add a functional
  detail the real object has (gusset, rib, hang slot), and only if hard cuts look harsh, soften that group
  with a Smooth modifier.
- **Accents stay small.** Accent-colour details (lugs, knobs) are understated; the silhouette carries the prop.

## Brush cheat sheet (sculpture-local, Z up)

| Shape | Size meaning | Notes |
|---|---|---|
| Cylinder | x = radius (y ignored), z = HALF-height | centred; `Taper` 0..1 shrinks +Z end; `Rounding` rounds both rims |
| Box | half-extents | `Rounding` up to the smallest half-extent = pill/soap shape (mug body) |
| Sphere | ellipsoid radii | `Slice`/`SlicePlaneN` cut a flat (sphere only) |
| Extruded | x = circumradius, z = half-depth | `CrossSection` Triangle/…, used as Cutout grooves (pumpkin) |
| Spline | — | `Points` = `"x,y,z,radius"` sculpture-local; Position/Rotation unused |
| Extruded Triangle | x = half base width, y = half height (apex +y, base −y), z = half-depth | `Qaxis 0 0 1 90` points the apex to −X; long thin ones = wedge cuts |
| Smooth (modifier) | gizmo only | amount = `Blend`; floors blend+rounding of every brush ABOVE it; emit `Operation='Colour'` |

Ops: `Add`, `Subtract` (tints cut walls with its colour), `Cutout` (thin groove/slot), `Colour` (paint only).
`Blend` is smooth-union against everything ABOVE it in the list, so **order matters**: anything that
must be trimmed by a hollow (lugs, knobs that could poke inside) goes BEFORE the Subtract; parts that
must stay whole (handles outside) go after.

## Workflow

1. **Read the reference** (image/description). List the readable forms (body, rim, hollow, handle…),
   estimate proportions as ratios of the body, sample 1–3 colours (`RGB255 r g b`).
2. **Pick a reference prop** from the house set with similar construction and dump its brushes.
3. **Write the recipe** `Tools/PropGen/props/<name>.ps1` (copy `sand_bucket.ps1`). Keep named
   proportions at the top so tweaks are one number. Kit helpers: `B`, `V3`, `P4`, `RGB255`, `Shade`,
   `Hollow`, `Qaxis`, `Qmul`, `$QFlip`/`$QAlongX`/`$QAlongY`, `ArcPoints` (elliptical arc → spline points),
   `Write-SdfProp <out> <name> <brushes> <boundsCentre>`. Ids are deterministic per recipe name.
4. **Generate:** `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/<name>.ps1`
   (the Bash tool's direct `.ps1` call is blocked by execution policy — always pass `-ExecutionPolicy Bypass`).
   Output goes under `Assets/Prefabs/Props/<Set>/`.
5. **Review in the editor** (sbox MCP): `open_scene scenes/prop_review.scene` (sun + sand floor + reference
   props). The scene instance does NOT refresh when the prefab file changes — `delete_game_object` the old
   instance and `create_game_object` with the prefab again after every regenerate. Then
   `set_editor_camera` + `editor_camera_screenshot`; take **two throwaway 64×64 shots first** (screenshots
   lag a frame or two). Check at least: 3/4 front view, top-down into any hollow, side view.
   Good cameras for a ~30-tall prop at the origin: `-75,-25,42 / 18,18,0` (3/4), `-30,-10,80 / 62,18,0` (top),
   `8,-62,38 / 18,90,0` (side). Editor yaw 90 looks toward +Y.
   `asset_thumbnail` is unreliable for brand-new prefabs (ERROR model, then a garbled coarse mesh).
   Don't use `render_prefab_icon` for review — it writes `UI/Icons/<name>.png` and settings into the prefab `.meta`.
6. **Hand over:** tell the user the prefab path, the recipe path, and what you were unsure of.
   Save `prop_review.scene` with the new prop in it (`save_scene`) so they can open it.

## Learn from the user's edits

When the user says they've changed a prop:
1. Copy the edited prefab to the scratchpad, `brushdump.ps1` it, and compare with the recipe (recipes take
   `-Out <path>` so you can regenerate to scratch without touching their prefab). Normalise by any overall
   scale change first — a uniform rescale hides the real proportional tweaks. List every change: added/removed brushes, sizes,
   blends, rounding, colours, order.
2. Explain back what each change does to the look and *why* it's better (proportion, chunkiness,
   silhouette, colour harmony…), and generalise it into a rule.
3. **Port the changes into the recipe** so regenerating reproduces their version (never regenerate
   over their edits without doing this first — the prefab is the source of truth once they've touched it).
4. Add a dated line to the Style log below, and fold anything general into "House style".

## Style log (user corrections, newest first)

- **2026-11-12 sand_dustpan** (rebuilt at ~2x):
  - **Scale again — bigger.** Pan 75 long × 66 wide (mine 34 × 40); ~104 long with the handle. Every prop so far
    has been scaled up by the user (1.5x, then ~2x). Start bigger.
  - **Pillowy block:** rounding ~0.48 × half-width (16 on 33). The thickness scales too: walls ~7–8, floor ~5.4.
    Chunky walls read as toy plastic, not sheet.
  - **Blunt front, not a knife edge:** my under-chamfer went; a near-vertical Subtract face (leaning ~3.6°, Blend 2)
    trims the front instead, leaving a thick flat lip. Toys have blunt, safe edges.
  - **No Smooth modifier here:** the big rounding on the block already gives softness; the cuts stay crisp
    (0–2). Use Smooth only when hard cuts need softening (fork tines), not by default.
  - **Functional detail = the "one more level":** a hang slot (rounded Box Subtract through the handle near its end)
    and the gusset placed AFTER the hollow so it also shows as a rib on the inner back wall.
  - **Handle thick and round:** half 6 × 6, Rounding 8; tilted ~13° (steeper than the spade's 8.85°); gusset steeper still (16.6°).
  - **Mute sampled colours:** reference blue `0.42,0.60,0.82` → user `0.39,0.50,0.64`. Pull saturation down
    off the image (the bucket kept its bright blue, so judge per prop, but err muted).

- **2026-11-12 sand_fork** ("general shapes ok, but too simple" — 2nd time running, so push detail further):
  - **Tapered tines:** my parallel box slots became two long `Extruded` Triangle wedge Subtracts (MirrorY), rotated
    90° about Z so the apex points back toward the handle. Gaps widen toward the tips → tines taper to points
    and run the full head length. Taper and angle read as "designed"; parallel-sided cuts read as placeholder.
  - **Smooth modifier** (`Shape='Smooth'`, amount in `Blend`, here 3.7) placed right after the hard cuts softens
    every brush ABOVE it in one go — use it instead of per-brush blends to round off a group of carved cuts.
    Brushes BELOW it (the later top cut, dish) keep their own crisp blends.
  - **Shallow dish on flat tops:** a rounded Box Subtract (Rounding ~4.4, tilted ~5°) sunk ~3.7 into the head,
    leaving a ~1.6 border. Flat faces get an inset; it's the cheapest "more interesting".
  - **Fully rounded shaft:** shaft Rounding 3.4 → 8 (clamps to a full round bar).
  - **Per-toy colour:** the fork went orange-red (`0.902,0.444,0.290`) — a set is one family, but each piece its own colour.
  - Sloped top cut blend 0.6 → 0.9.

- **2026-11-11 sand_spade** ("scoop far too simple; blending made it way too blobby"):
  - **Carve, don't stack.** The scoop became ONE big rounded block (Rounding ~0.4 × half-width) shaped by flat
    Subtract slabs: a flat base cut (Blend ~2 for a soft bottom edge), a TILTED top cut (~11°, Blend 0.6) so the
    walls step down toward the open front, then the hollow (Blend 0). Planar cuts on a soft block = a manufactured
    toy silhouette with clay-soft edges, and the slope adds interest for free.
  - **Blend 0 between separate parts.** Shaft, ring and scoop all Blend 0 — my Blend 2–6 joins melted them into blobs.
    Big blends only belong on the first/body brush; cuts get 0–2.
  - **Drop fussy bits:** the collar went. Fewer, bolder parts.
  - **Thicker:** shaft half 4.7 × 3.9, ring tube 3.66 (mine 3.6 × 3.0 / 2.8–3.1). Spade ~92 long (mine ~75).
  - **Pose:** the shaft tilts ~9° up toward the grip and the ring tilts with it — not dead flat.
  - **Open scoop uses the body colour** for the hollow (no darker tint); the tint is for deep hollows like the bucket.
  - Shared shaft + ring now live in `Tools/PropGen/props/_sand_toy_handle.ps1` (sand-toy set).

- **2026-11-11 sand_bucket** ("quite close, minor tweaks to balance it"):
  - **Scale up.** The user scaled the whole bucket ~1.5x (32 → ~48 tall, top radius 17 → 25.7). Garden/sandpit props
    should sit at garden-pot scale (gardenpot ~52 tall, r~29). When unsure, go bigger, not smaller.
  - **Rounder body:** Rounding 0.18R → 0.26R, Blend ~7.
  - **Rim = band, not bead:** taller (half-height ~0.155R) but much less rounded (Rounding ~0.3 × half-height) —
    a flat-faced band with soft edges, ~2 proud of the body, its top ~3 above the body top.
  - **Softer hollow lip:** Subtract Blend 0.8 → 2. Wall ~0.16R, floor ~0.23 × body height (proportions kept).
  - **Handle anchored at the rim**, standing off the body by ~0.17R, tube ~0.085R, Blend 0 (crisp, separate part).
    The swing/ellipse shape was kept.
  - **Accents de-emphasised:** the yellow lugs shrank and mostly sank into the wall (one sphere + MirrorY).
  - **Interior colour** slightly darker and bluer than my desaturated guess → `Hollow` default now (k 0.68, desat 0.3).
  - The user left a root rotation and a pivot ~20% up the body; neither looks like a rule.

## Prop log

| Prop | Recipe | Prefab | Status |
|---|---|---|---|
| Kids' sand bucket | `Tools/PropGen/props/sand_bucket.ps1` | `Assets/Prefabs/Props/Garden/sand_bucket.prefab` | v2 = user-tuned, recipe ported + verified |
| Kids' sand spade | `Tools/PropGen/props/sand_spade.ps1` | `Assets/Prefabs/Props/Garden/sand_spade.prefab` | v2 = user-tuned, recipe ported + verified |
| Kids' sand fork | `Tools/PropGen/props/sand_fork.ps1` | `Assets/Prefabs/Props/Garden/sand_fork.prefab` | v2 = user-tuned, recipe ported + verified |
| Kids' dustpan | `Tools/PropGen/props/sand_dustpan.ps1` | `Assets/Prefabs/Props/Garden/sand_dustpan.prefab` | v2 = user-tuned, recipe ported + verified |
