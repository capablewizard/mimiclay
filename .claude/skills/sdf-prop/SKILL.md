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
  Accent parts (bands, lids, knobs) in a contrasting colour. Metallic 0 unless it's real metal
  (gold trim: `0.8,0.686,0.349` metallic 1).
- **Bright colours: keep hue, cut saturation/value.** My bright picks come out too saturated EVERY time. For any
  colour with HSV saturation > 0.5, keep **S ≤ ~0.65 (reds/blues) and V ≤ ~0.85**, roughly S × V ≤ 0.6. Reds
  take the biggest cut: ketchup S 0.81→0.61, can red V 0.80→0.60. Yellows go toward ochre/mustard
  (hue −5°, V 0.95→0.8). Plastic "white" is warm cream (`0.89,0.86,0.79`), not a cool blue-white. Lights stay saturated (wall_lamp).
- **Roughness 0.5.** Leave it at the kit default. Don't vary it per part (my 0.15/0.35/0.4 picks all got reset).
  Exceptions only: real metal (can lid 0.37, metallic 1), painted metal (can body metallic 0.57 / rough 0.6),
  bread/fabric 0.6.
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
   **Judge symmetry with pitch 0 and the camera centred on the prop:** a pitched, off-centre shot skews vertical
   edges, so one side's taper vanishes and the other doubles. Check the brush dump before "fixing" it.
   **The user may switch scene tabs while you work:** batch `open_scene` with the creates and screenshots in ONE
   `call_tools`, check `get_game_object(...).Scene` after a create, and switch them back to the tab they were on.
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

- **2026-11-12 flower pot leaves: the user deleted all but one leaf and reshaped it, then asked for copies.** Their leaf
  is the opposite of my profile: FAT at the soil (r 5.8), tapering to a POINT (5.4 → 4.1 → 2.2) over 4 points, rising
  ~50 deg and sweeping ~15 deg sideways. Lighter, fresher green `0.486,0.637,0.362` (leaves + stalks) and lighter soil
  `0.479,0.332,0.238`. Generating more from one hand-made part: keep theirs exact, then copy it in cylindrical terms
  (radius, azimuth relative to its base, height), at new azimuths with length x0.8-1 and the sideways sweep mirrored
  on alternate copies (`Leaf` in flower_pot.ps1).
- **2026-11-12 flower pot: petals cupped 12 deg, blend 0, pushed out to 10; petal count 4 or 5 per flower (seeded).**
  The user retuned the daisy centre: a SMALLER, FLATTER dome (r 6.5 → 5.55, height 6 → 3.88) RAISED 2.1 along the head
  normal so it sits proud of the petals as a button instead of sinking among them. Lesson: flower centres are small
  raised buttons, and petals read best as separate cupped lobes (no blend).
- **2026-11-12 planter tray: "very close, minor tweaks."** Darker, less brown grey (`0.335,0.322,0.308`; inside
  `0.209,0.203,0.198`). Softer V gaps: triangle-wedge blend 2 → 5.3 on the dividers and long walls (end walls left at 2).
  Lip overhang 4 → ~2.6 with rounding 2.2 (a thin crisp ledge, not a slab). The rim recess was dropped (fussy
  detail). My stepped two-box hollow became ONE bigger box per cell (half-width 15, floor ~19): the user accepts
  thinner walls in exchange for big, square openings. Body lifted 2.4. Lesson: on a first pass, read the reference's
  OPENINGS as large as it shows them, and keep cut blends soft enough that they read as moulded plastic.
- **2026-11-12 wheelbarrow: "the shape of the frame is totally different to the concept."** I'd made generic
  straight rails sloping up to the grips with separate legs. The concept's frame is ONE continuous tube per side:
  axle → back → drops into a U-shaped leg touching the ground mid-tray → up under the tray → rises steeply behind
  the back wall → turns FLAT at rim height into the handle/grip. **Trace the reference's actual silhouette line
  (especially tubes and frames) instead of building the generic version of the object.** Check a pure side view.

- **2026-11-12 big bucket** ("not a bad attempt"): the user removed the lugs, flattened the rim band (rounding 0.55),
  widened the hollow to a thin wall, raised the handle pivot to the rim, used a thinner tube, and made the handle a
  shiny light metal (`0.69,0.71,0.74`, Metallic 1, Roughness 0.6). Then they asked for:
  - **"Handle rotated so it aligns with the object":** a hanging handle should REST on the wall, not float. Find the
    swing numerically: sample the arc, check radial distance against the tapered body radius minus the tube radius,
    and take the angle where the clearance is ~0.5 (it was 139°; my 130° left a 3-unit gap).
    PowerShell trap: variable names are case-insensitive (`$D` and `$d` are ONE variable). Colour-helper function names must not shadow built-in aliases (`Ac` = Add-Content, `Gc`, `Gi`, `Sc`, `Ni`…): name them `Acc`, `Gn`, `Mt`. `[math]::Max(-1, x)` with int literals picks the INT overload and rounds x: write `-1.0`/`1.0`.
  - **Metal bolts where the handle pivots:** a flat round head (Cylinder along Y, MirrorY) on the rim band, plus a
    small domed nut, both in the handle metal, with the handle end sitting in the nut.
  - Editing a live instance: instances don't refresh from the prefab file, so load the new `Brushes` JSON from the
    prefab with `Json.Deserialize<System.Collections.Generic.List<Mimiclay.SdfBrush>>`, assign `sc.Brushes`, then `sc.Rebuild()`.

- **2026-11-12 garden spade/fork: "quite poor — didn't take anything from the existing models."** My upright
  flat-plate tools + D-grip were thrown out; the user rebuilt `GardenSpade.prefab` as **the sand spade mapped up**:
  - **Reuse the set's own construction at the new scale, don't invent a new one.** Full-size tool head =
    sand-toy head scaled (2.025 along the length, 1.716 in width/depth, rounding + blend ×1.716), tilted −12.9°
    about Y so the tip rises, same carve-a-rounded-block approach (hollow, top cut, base cut at ~+4° vs the head).
  - **Lies along X like the toys** (handle toward −X, shaft axis z=0), not standing upright.
  - **Handle:** a sliced, tapered ellipsoid as the metal neck (Sphere, Taper 1, Slice 0.417), a wood capsule shaft
    (`0.71,0.52,0.28`), a green grip sleeve (`0.15,0.34,0.24`) and a flat closed-spline D loop in the XY plane.
  - **Metal:** warm grey `0.656,0.607,0.543`, Metallic 0.85, Roughness 0.55.
  - Size: the whole tool is ~245 long. My 340-tall versions were too big.
  - **Fork rework (user):** dropped the head tilt and the mapped top cut and dish. The head is a FLAT slab (rounding 16)
    sliced top and bottom by ONE `MirrorZ` Subtract. The tine wedges carry their own Blend 2.2 / Rounding 3.7 instead of a
    Smooth modifier, and a rounded centre slot runs in from the tip. Mapping is a starting point; simplify after.
  - (v1) `garden_fork.ps1` built the fork by running the sand fork's brushes through the same map (`MapPos`/`MapTilt`)
    and reusing GardenSpade's handle verbatim. **Use that map for any other toy-to-full-size pair.**

- **2026-11-12 flask** (recipe `cart/cart_flask.ps1` ported, base re-zeroed to z=0):
  - **Red too saturated, again.** Body red became `0.74,0.37,0.34` (a dusty brick red). Saturated reds are the most
    common miss; start reds at ~0.55 saturation.
  - **~1.5x bigger** (body r 23, height 62; whole flask ~90 tall). I still under-size props; start bigger.
  - **Metal = cool light grey `0.64,0.65,0.68`, Metallic ~0.78, Roughness ~0.41.**
  - **Stepped silhouette:** flat base disc slightly WIDER than the body (rounding 0.2), cap band NARROWER than the body,
    tall dome (half 11, rounding 10.4) on top. Each step changes the radius, so the silhouette reads.
  - **Thick handle:** tube radius 4.5 (was 3). The thin groove under the cap was dropped as fussy.

- **2026-11-12 snack cart set** (15 blockouts → 6 kept; the rest deleted as unused, backup in `tmp/backups/Cart_2026-11-12/`):
  - **Colours too saturated when bright** (the user's explicit note) and **roughness over-varied — 0.5 is the default.**
    Both folded into House style above.
  - **Variants = scene overrides, not prefabs.** Mustard/white bottles, a blue can and an orange-lidded tub are colour
    overrides on the ketchup/can/tub prefab instances. Build one well-tuned prefab per form; don't pre-make colour variants.
  - **Bowls: cut, don't scoop.** The sphere-Subtract hollow became: tapered body → flat cylinder Subtract slicing the
    top → rim band added back → tapered (flipped) cylinder Subtract for a straight sloped inner wall (Blend 3–3.6).
    Bun bowl's interior is darker (Subtract colour `0.567,0.425,0.14`). Fruit bowl lost its fruit (empty bowl).
  - **Piles look tossed:** every bun got a small random tilt (5–60°) and the pile spread; a big soft filler sphere
    (Blend 8) under it fills the gaps down to the floor.
  - **Ovals:** bottle body 12.8 × 11 (cylinder Size.y is ignored by the SDF, so the oval came from editor scale; it
    reads round in-game). Collar squatter (half 4.5→3.3), nozzle sunk closer.
  - **Tray deeper with a low lip:** slab doubled in height, then a second wider flat Subtract lowers the walls.
  - **Tub rebuilt:** opaque cream body (the see-through Colour trick went), flared collar + clips, flat cut, then a
    chunky lid with an inset and tabs. Everything ~1.3× bigger (body half 42 × 30 × 20).
  - **Can:** lid smaller and sunk; base dished out by a big-Rounding Subtract instead of a base ring; label band Blend 0 (crisp).
  - **Blend-seam gotcha:** a smooth Subtract fillets against faces BURIED in the clay (e.g. the body's cut top under
    an added rim), leaving a ridge on the cut wall. Give the covering Add a small Blend (~3) so the seam is smooth.

- **2026-11-12 wall_lamp** ("small changes"):
  - **Light sources are saturated:** the globe went from my pale `0.98,0.79,0.45` to pure amber `1,0.58,0`. "Mute
    colours" is for materials, not for things that glow.
  - **Capsule = Cylinder with huge Rounding** (100 clamps to the radius): the globe became an elongated capsule
    reaching up inside the shade instead of a sphere hanging below it.
  - **Wall mounts are round roses** (a short Cylinder disc on the wall), not pill plates.
  - **Neutral greys:** slate-blue `0.21,0.25,0.33` → neutral `0.22,0.24,0.27`.
  - **Hollows in body colour — again.** Only the bucket kept a darker interior; default to the body colour.
  - **Fussy bits dropped again:** the screw head went. (It also rendered black — `Shade` had an int-overload bug, fixed.)
  - Rim band fully rounded (Rounding ≥ its half-height); finial cap squatter; shade pulled closer to the wall.

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
| Wall lamp | `Tools/PropGen/props/wall_lamp.ps1` | `Assets/Prefabs/Props/Garden/wall_lamp.prefab` | v2 = user-tuned, recipe ported + verified (wall = plane x=0) |
| Snack cart set (6 prefabs: bottle_ketchup, bowl_buns, bowl_fruit, can_red, tray_teal, tub_blue) | `Tools/PropGen/props/cart/*.ps1` (`-OutDir`) | `Assets/Prefabs/Props/Cart/` | v2 = user-tuned, recipes ported + verified; placed in DevPropHunt (variants are scene overrides) |
| Vacuum flask | `Tools/PropGen/props/cart/cart_flask.ps1` | `Assets/Prefabs/Props/Cart/flask.prefab` | v2 = user-tuned, recipe ported (re-zeroed: base at z=0, centred) |
| Kids' broom | `Tools/PropGen/props/broom.ps1` | `Assets/Prefabs/Props/Garden/broom.prefab` | v1 = mine; user editing live in DevPropHunt (bristle fan, handle, hang hole) — port when done |
| Garden spade (full size) | user-built | `Assets/Prefabs/Props/Garden/GardenSpade.prefab` | the user's rebuild = the reference for full-size tools. My `garden_spade.prefab` (from `garden_tools.ps1`) was rejected |
| Garden digging fork (full size) | `Tools/PropGen/props/garden_fork.ps1` | `Assets/Prefabs/Props/Garden/garden_fork.prefab` | v2 = user-tuned, recipe ported + verified |
| Big bucket (now blue) | `Tools/PropGen/props/big_bucket.ps1` | `Assets/Prefabs/Props/Garden/big_bucket.prefab` | v3 = user-tuned (blue, bigger bolt heads, nut INSIDE the wall, handle span 49.4); recipe = prefab |
| Wheelbarrow (empty) | `Tools/PropGen/props/wheelbarrow.ps1` | `Assets/Prefabs/Props/Garden/wheelbarrow.prefab` | v1 = mine (tray carved from tilted planes + union-of-slabs hollow; lawnmowerWheel ×1.6; lawnmower-handle tubes); v1.1 frame redrawn to the concept (one tube per side with U leg, flat handles) |
| Seed-tray planter (3×2 + 2×2, stackable) | `Tools/PropGen/props/planter_tray.ps1` (`-Cols -Rows`) | `Assets/Prefabs/Props/Garden/planter_tray.prefab`, `planter_tray_2x2.prefab` | v2 = user-tuned 3×2, recipe ported (brush-for-brush); 2×2 generated from it |
| Clay watering can | `Tools/PropGen/props/watering_can.ps1` | `Assets/Prefabs/Props/Garden/watering_can.prefab` | v2 = rebuilt to the user's concept sheet (teal, squat open top, "?" handle into the back wall, rose with 5 holes; 0.55 of the sheet's 150 x 220); field-checked, NOT yet seen (editor was in play mode). v1 (generic plastic can) replaced |
| Flower pots (gardenpot + clay daisies) | `Tools/PropGen/props/flower_pot.ps1 -Variant default/sunny/daisies/blue` | `Assets/Prefabs/Props/Garden/flower_pot.prefab` (default = user-approved, KEEP; recipe reproduces it byte-for-byte), `flower_pot_sunny`, `flower_pot_daisies`, `flower_pot_blue` | default v2 = user-tuned; 3 variants matched to the user's reference shots (flower count/colours, leaf count, pot colour); all placed in DevPropHunt (unsaved) |

## Hollowing a flared tub (inner faces from Subtract boxes)

A box only has parallel face pairs, and Subtracts union, so one box can't follow a tub whose walls lean different
ways. Use ONE Subtract box per inner wall; each box touches its own inner plane and stays inside every other one.
`Tools/PropGen/hollow_tub.cs` (run_csharp, `hollow_mode.txt` = plan|apply in the exchange dir) does it on a live
instance:
- **Measure the walls from the FIELD, never from the cut boxes.** The user's cuts carry Blend 32 plus a Smooth
  modifier, so the real surface sits 10–20 inside the cut faces, and the floor was the body box's own bottom, not
  the tilted floor cut. Ray-march from the tub centre to the surface (`Mimiclay.Sdf.Sample(list, p)`), least-squares
  fit a plane per wall, and put the inner plane at the max hit + wall thickness.
- **Boxes:** a tall floor box, plus front/back boxes and LAYERED side boxes (sides that flare too leave solid
  wedges in the top corners otherwise). Grow each box, then shrink one edge per pass until sampled corners
  satisfy all other inner planes; fix the sideways extent before raising the bottom. Try depths and keep the
  biggest. Check coverage by sampling the cavity (aim for ≥97%).
- **Check holes after applying:** fire rays from the centre below the rim; none may escape, and report the thinnest
  wall. Rounding 20 on the boxes keeps the inner corners (where the outer blend curves in most) from breaking
  through. Wall 7 for a ~280-long barrow.
- Modifiers (`Smooth`) only floor brushes EARLIER in the list, so hollow boxes added after them keep their own blend.

## Fitting to a reference mesh the user drops in

The user may import a blockout mesh (an `FbxImportedPiece` under `Blender_Garden` etc.) as the proportions reference:
- Read its vertices with `ModelRenderer.Model.GetVertices()` in `run_csharp`, transform them to world, then into a frame
  aligned with the prop (yaw from the object, Y-up FBX already handled by its roll). Slice along the long axis and
  print z/|y| ranges per slice to get the wheel centre/radius, rim footprint, floor height, wall lean and tube path.
  Ignore contents baked into the mesh (the wheelbarrow had a sack and pots in it).
- Put the recipe in that frame (origin = mesh origin, ground z=0), so the prefab drops onto the mesh with one yaw.
- Compare from the same camera on both. To compare without the reference, disable its renderer, and re-enable it with
  `Components.Get<ModelRenderer>(true)` (plain `Get` skips disabled components, which is how I left it hidden once).
- `editor_camera_screenshot` can lag even after 2 throwaway shots when the scene is heavy; take 3, and
  sanity-check size with `ModelRenderer.Bounds` rather than trusting a single frame.
- Don't move the user's instance onto the reference without asking: they put it back where it was.

## Reusable parts from the hand-made props (reuse these before inventing)

- **Clay daisy (flower_pot.ps1 `Flower`):** thick spline stalk (r 3.4) from the soil to just behind the head; 4 flat
  Cylinder petals (per unit flower size: r 7.6, spread 9.6 so a ~2.4 GAP rings the centre (user), half-height 2.32,
  Rounding 3, lifted 0.6 along n, Blend 0 = separate lobes with creases) -
  from the centre, each CUPPED 12 deg (head q * Qaxis(-sin a, cos a, 0, -12): outer edge lifts toward n) IN THE HEAD
  PLANE; centre = Sphere Slice 0.5 (r 5.55, h 3.88, flat face raised 2.1 along n) with its dome along n (`q * Qaxis 1 0 0 180`). Build the
  head frame as `ZTo(n) * spin` and rotate petal offsets by it. Heads must FACE THE VIEWER (n mostly toward front,
  ~35-45 deg up); sideways-facing heads read as sticks. Leaves = the user's leaf (fat base 5.8 tapering to a 2.2 point,
  4 points, sideways sweep) copied round the pot. (My old version was carrot-top splines, thin at the soil (2.4), fat in the
  middle (6), rounded tip (3.4), rising steeply between the stalks (flat splayed leaves look like sausages).)
  **Leaves veer away from heads (user idea, `AvoidHeads`):** heads = discs (centre, n, radius PetalOff+PetalR); while a
  leaf point is within its radius + 3 of a disc, push the leaf away from the head centre seen from above, plus 0.6 of
  that downward (pure sideways = flat jut that looks like escaping; nearest-point push = droop, not a veer). ONLY THE TIP
  is tested and moved (weights 0/0/.25/1): the user preferred the leaves' placement kept and just the tips curling off.
  If the tip would need a bend > 6, the leaf gets SHORTER instead (5% steps, min 0.65 of its length) - a severe hook
  read badly (user).
  Hand-placed leaves (user redraws one) go in verbatim and skip the auto-veer. SOIL goes LAST with Blend ~2.9 (user) so
  it smooths into every leaf/stalk base. Flowers carry a `size` (1.2 / 1.0 / 0.8) scaling petal radius + spread + dome,
  not petal thickness: three same-size heads read as copies (user).
  **Heads must never clip each other (user):** sample each head disc (centre + rings at R/2, R), test every sample's
  gap to the other discs >= petal half-thickness + 3.5 (covers the cup); push clipping pairs apart along their centre
  line, smaller head moving more, BEFORE building stalks/leaves (they follow). For a tight bunch (daisies) the user
  preferred HEIGHT-ONLY separation (higher head up, lower down, floor z 50) to spreading them apart. Heads 33 across need ~33
  between centres unless staggered in depth.
- **Tapered cells / grid of tapered boxes (planter tray):** Box cannot taper, so carve ONE rounded block with
  `Extruded` Triangle Subtracts, apex up at the block top and base just below the floor. Put one CENTRED on each
  cell divider (a V gap that opens downward) and one centred on each outer wall plane (half of it in air = the wall
  lean). Half-base = inset × (2·halfHeight)/H, so the cut is exactly `inset` wide at z=0. Rotation `$QAlongY` cuts
  planes x = const; `0.5,0.5,0.5,0.5` (x→y, y→z, z→x) cuts planes y = const. Use MirrorX/Y for symmetric pairs.
  Hollow = ONE rounded box per cell (user's choice over my stepped 2-box taper), floor raised (~19) so the walls hold.
- **Bevelled corners on a curved spline (Curvature 1):** the curve is uniform Catmull-Rom (tangent = (next − prev)/2),
  so uneven point spacing overshoots. To keep a straight-segment look with slightly rounded corners, replace each
  corner P with FOUR points on its two legs: P + u_in·10, P + u_in·4, P + u_out·4, P + u_out·10 (u = unit vectors
  toward the neighbours; 4 = bevel size). The runs between corners stay exactly straight, because their points are
  collinear, and each corner becomes a short symmetric round. Legs must be longer than 20. With brush objects on, the
  points are `SdfSplinePoint` child objects; rebuild them in order (destroy the old ones, create new children).
  **The user preferred the lighter version: TWO points per corner** (P + u_in·b, P + u_out·b, b ≈ 9 on 25–45-long
  legs). The runs bow by only ~1 (hidden inside a radius-5 tube) and the corners cut ~4 in: rounder, fewer points.
  Score candidate b values first by sampling `SdfBrush.CatmullRomPoint` against the original polyline in `run_csharp`.

- **Wheel** = `lawnmowerWheel.prefab`: grey rounded tyre (Cylinder along Y, r 18.9, half 6.6, rounding 2.85) plus a
  yellow `Cutout` hub recess (r 11, half 5.7, offset 4.2 out, Blend 3.5, Rounding 2.4). Scale it as a unit.
- **Tube frame / handle** = `lawnmowerhandle.prefab`: one thick spline (radius ~5.5, Blend 1.3, dark grey `0.27`).
- **Metal bar handle** = `CartHandle.prefab`: capsule cylinder plus sphere end caps plus small box brackets (Metallic 1).
- **Full-size tool handle** = `GardenSpade.prefab`: sliced tapered-ellipsoid neck, wood capsule, green sleeve, D loop.
- **Tapered rectangular tub** (wheelbarrow tray): a rounded box, with one big Subtract slab per sloped wall (its face
  IS the plane: centre = a point on the plane + normal × half-thickness). The hollow is a union of Subtract boxes:
  a central box plus one slab per wall, offset inward by the wall thickness.

## Blocking out a whole scene/set

- Size everything off ONE anchor object (cart set: a 30-tall mug) and derive the rest from ratios in the
  reference, so the set is consistent. Big furniture follows (cart ~285 long, shelves at z 58 / 150).
- One recipe per object family; multi-prefab scripts take `-OutDir` and call `Write-SdfProp` per prefab.
  Variants (bottle colours, cans, mugs, tubs) = one function with colour/size params.
- Lay the set out under an empty parent in `prop_review.scene` with `create_game_object parent=…`, in one batch;
  record the surface heights other props sit on in the base prop's header comment.
- **Colour op tricks:** a `Colour` brush paints without geometry — label bands on cans, towel stripes, and
  "contents seen through clear plastic" (a soft Colour box over the lower half of an opaque tub).
