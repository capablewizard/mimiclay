# SDF quality-profile migration report

Generated 2026-10-26 01:00 by Tools/sdf/migrate-quality-profiles.cs (written).

- files changed: 316
- reformatted to s&box's own layout (were PowerShell ConvertTo-Json output, same JSON): Prefabs/BlockSet/blockset_arrangement.prefab, Prefabs/CastleCourtyard/well.prefab, Prefabs/Props/Food/apple.prefab, Prefabs/Props/Food/banana.prefab, Prefabs/Props/Food/cherries.prefab, Prefabs/Props/Food/grapes.prefab, Prefabs/Props/Food/lemon.prefab, Prefabs/Props/Food/orange.prefab, Prefabs/Props/Food/pear.prefab, Prefabs/Props/Toys/toy_train_wagon_green.prefab, Prefabs/Props/Toys/toy_train_wagon_red.prefab, Prefabs/Props/Toys/toy_train.prefab
- renderer components: 441 (keys stripped: 7570)
- instance patches stripped: 771
- renderers assigned the Player profile: 6 (pawn prefabs: Prefabs/hunter.prefab, Prefabs/Charades Pawn Taylorr.prefab); every other renderer inherits Standard
- previously OFF, now inherited ON from the profile: AdaptiveQuality 185, DistanceSwitching 183, OverdrawOptimization 237

## Unusual authored values (not a code default or a generator set) — override candidates

| file | object | values |
|---|---|---|
| Prefabs/CastleCourtyard/well.prefab | Stonework | Epsilon=0.15, FarEpsilon=3.5, MinQualityRadii=16, FieldResolution=96 |
| Prefabs/CastleCourtyard/well.prefab | Bricks | Epsilon=0.15, FarEpsilon=3.5, MinQualityRadii=16, FieldResolution=128 |
| Prefabs/CastleCourtyard/well.prefab | Frame | Epsilon=0.15, FarEpsilon=3.5, MinQualityRadii=16, FieldResolution=96 |
| Prefabs/CastleCourtyard/well.prefab | Roof | Epsilon=0.15, FarEpsilon=3.5, MinQualityRadii=16, FieldResolution=128 |
| Prefabs/Charades Pawn Taylorr.prefab | Hand | FieldResolution=32 |
| Prefabs/Charades Pawn Taylorr.prefab | Body | FieldResolution=32 |
| Prefabs/hunter.prefab | Hand | FieldResolution=32 |
| Prefabs/hunter.prefab | Body | FieldResolution=32 |
| scenes/minimal.scene | Snowman | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (1) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (2) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (3) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (4) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (5) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (6) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (7) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (8) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (17) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (16) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (15) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (14) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (13) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (12) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (11) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (10) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (9) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (26) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (25) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (24) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (23) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (22) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (21) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (20) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (19) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (18) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (27) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | Snowman (28) | MaxSteps=64, Epsilon=0.15 |
| scenes/minimal.scene | CitizenSDF | MaxSteps=64, Epsilon=0.15 |
| scenes/PerfTest.scene | Snowman (28) | Epsilon=0.15, FarEpsilon=3.5881817, MinQualityRadii=16 |
| scenes/PerfTestDebug.scene | Snowman (28) | Epsilon=0.15, FarEpsilon=3.5881817, MinQualityRadii=16, FieldResolution=48 |
| scenes/PerfTestDebug.scene | snowman | FieldResolution=160 |
| scenes/PerfTestDebug.scene | Wall | FieldResolution=48 |
| scenes/PerfTestDebug.scene | giraffe | FieldResolution=80 |
| scenes/PerfTestDebug.scene | dinosaur | FieldResolution=160 |
| scenes/PerfTestDebug.scene | MetalTest | FieldResolution=48 |
| scenes/PerfTestDebug.scene | MetalTest (5) | FieldResolution=48 |
| scenes/PerfTestDebug.scene | MetalTest (2) | FieldResolution=48 |
| scenes/PerfTestDebug.scene | MetalTest (3) | FieldResolution=48 |
| scenes/PerfTestDebug.scene | MetalTest (1) | FieldResolution=48 |
| scenes/PerfTestDebug.scene | MetalTest (4) | FieldResolution=48 |
| scenes/PerfTestDebug.scene | Head | FieldResolution=70 |
| scenes/PerfTestDebug.scene | Body | FieldResolution=48 |
| scenes/PerfTestDebug.scene | Head | FieldResolution=160 |

## Instance patches stripped (someone changed these on an instance — deliberate?)

| file | value | instances |
|---|---|---|
| Prefabs/Charades Pawn Taylorr.prefab | FieldResolution=128 | 1 |
| Prefabs/Charades Pawn Taylorr.prefab | FieldResolution=256 | 1 |
| Prefabs/Charades Pawn Taylorr.prefab | OverdrawOptimization=false | 1 |
| scenes/Heads.scene | Epsilon=0.25 | 1 |
| scenes/Heads.scene | FarEpsilon=1.8 | 1 |
| scenes/Heads.scene | FieldResolution=135 | 1 |
| scenes/Heads.scene | MaxSteps=32 | 1 |
| scenes/Heads.scene | MinQualityRadii=20 | 1 |
| scenes/Heads.scene | MinSteps=5 | 1 |
| scenes/kitchenlobby.scene | AdaptiveQuality=false | 2 |
| scenes/kitchenlobby.scene | CullRadii=220 | 3 |
| scenes/kitchenlobby.scene | FieldResolution=256 | 1 |
| scenes/kitchenlobby.scene | FullQualityRadii=6 | 3 |
| scenes/kitchenlobby.scene | MeshLod1Radii=90 | 3 |
| scenes/kitchenlobby.scene | MeshLod2Radii=140 | 3 |
| scenes/kitchenlobby.scene | MinQualityRadii=60 | 3 |
| scenes/kitchenlobby.scene | SdfShadowRadii=0.3106796 | 1 |
| scenes/kitchenlobby.scene | SdfShadowRadii=10 | 3 |
| scenes/Maps/Bedroom.scene | AdaptiveQuality=false | 39 |
| scenes/Maps/Bedroom.scene | DistanceSwitching=false | 39 |
| scenes/Maps/CharadesZoo_art2.scene | FieldDensity="World" (target unresolved) | 14 |
| scenes/Maps/CharadesZoo_art2.scene | FieldDensity="World" | 67 |
| scenes/Maps/Claybusters.scene | CullRadii=220 | 76 |
| scenes/Maps/Claybusters.scene | FullQualityRadii=6 | 76 |
| scenes/Maps/Claybusters.scene | MeshLod1Radii=90 | 76 |
| scenes/Maps/Claybusters.scene | MeshLod2Radii=140 | 76 |
| scenes/Maps/Claybusters.scene | MinQualityRadii=60 | 76 |
| scenes/Maps/Claybusters.scene | SdfShadowRadii=10 | 62 |
| scenes/Maps/kitchen_creative.scene | AdaptiveQuality=false | 2 |
| scenes/Maps/kitchen_creative.scene | CullRadii=220 | 2 |
| scenes/Maps/kitchen_creative.scene | FieldDensity="World" | 54 |
| scenes/Maps/kitchen_creative.scene | FieldResolution=256 | 1 |
| scenes/Maps/kitchen_creative.scene | FullQualityRadii=6 | 2 |
| scenes/Maps/kitchen_creative.scene | MeshLod1Radii=90 | 2 |
| scenes/Maps/kitchen_creative.scene | MeshLod2Radii=140 | 2 |
| scenes/Maps/kitchen_creative.scene | MinQualityRadii=60 | 2 |
| scenes/Maps/kitchen_creative.scene | SdfShadowRadii=0.3106796 | 1 |
| scenes/Maps/kitchen_creative.scene | SdfShadowRadii=10 | 2 |
| scenes/Maps/Kitchen.scene | CullRadii=220 | 3 |
| scenes/Maps/Kitchen.scene | FullQualityRadii=6 | 3 |
| scenes/Maps/Kitchen.scene | MeshLod1Radii=90 | 3 |
| scenes/Maps/Kitchen.scene | MeshLod2Radii=140 | 3 |
| scenes/Maps/Kitchen.scene | MinQualityRadii=60 | 3 |
| scenes/Maps/Kitchen.scene | SdfShadowRadii=0.3106796 | 1 |
| scenes/Maps/Kitchen.scene | SdfShadowRadii=10 | 3 |
| scenes/Maps/Kitchen2.scene | DepthOcclusionCull=false | 1 |
| scenes/Maps/Medieval.scene | FieldResolution=195 | 1 |
| scenes/Maps/PropZoo.scene | CullRadii=220 | 1 |
| scenes/Maps/PropZoo.scene | FullQualityRadii=6 | 1 |
| scenes/Maps/PropZoo.scene | MeshLod1Radii=90 | 1 |
| scenes/Maps/PropZoo.scene | MeshLod2Radii=140 | 1 |
| scenes/Maps/PropZoo.scene | MinQualityRadii=60 | 1 |
| scenes/Maps/PropZoo.scene | SdfShadowRadii=10 | 1 |
| scenes/menu.scene | CullRadii=220 | 1 |
| scenes/menu.scene | FullQualityRadii=6 | 1 |
| scenes/menu.scene | MeshLod1Radii=90 | 1 |
| scenes/menu.scene | MeshLod2Radii=140 | 1 |
| scenes/menu.scene | MinQualityRadii=60 | 1 |
| scenes/menu.scene | SdfShadowRadii=10 | 1 |
| scenes/minimal.scene | Epsilon=0.15 — KEPT (target unresolved, ambiguous key) | 1 |
| scenes/minimal.scene | MaxSteps=64 — KEPT (target unresolved, ambiguous key) | 1 |
| scenes/PerfTest.scene | CullRadii=80 | 5 |
| scenes/PerfTest.scene | Epsilon=0.18 | 5 |
| scenes/PerfTest.scene | FarEpsilon=0.5 | 5 |
| scenes/PerfTest.scene | MeshLod1Radii=30 | 5 |
| scenes/PerfTest.scene | MeshLod2Radii=40 | 5 |
| scenes/PerfTest.scene | MinQualityRadii=12 | 5 |
| scenes/PerfTest.scene | MinSteps=12 | 1 |
| scenes/PerfTest.scene | SdfSculpture.Resolution=64 | 5 |

## SdfSculpture.Resolution stripped (mesh LOD0 cells now come from the profile's Mesh LODs rule)

359× 32, 35× 48, 23× 24, 21× 64, 2× 40, 1× 8

Kept as ResolutionOverride (rule would be coarser): 43

| file | object | authored resolution (not 32) | now |
|---|---|---|---|
| Prefabs/BlockSet/block_bat.prefab | block_bat | 48 | profile rule |
| Prefabs/BlockSet/block_bridge.prefab | block_bridge | 48 | kept as override |
| Prefabs/BlockSet/block_cube.prefab | block_cube | 24 | profile rule |
| Prefabs/BlockSet/block_cylinder_slim.prefab | block_cylinder_slim | 24 | profile rule |
| Prefabs/BlockSet/block_cylinder.prefab | block_cylinder | 24 | profile rule |
| Prefabs/BlockSet/block_fence.prefab | block_fence | 64 | kept as override |
| Prefabs/BlockSet/block_holedcube.prefab | block_holedcube | 48 | kept as override |
| Prefabs/BlockSet/block_holedpanel.prefab | block_holedpanel | 48 | kept as override |
| Prefabs/BlockSet/block_ornament.prefab | block_ornament | 64 | kept as override |
| Prefabs/BlockSet/block_paddle.prefab | block_paddle | 48 | kept as override |
| Prefabs/BlockSet/block_panelpost.prefab | block_panelpost | 48 | kept as override |
| Prefabs/BlockSet/block_plank_square.prefab | block_plank_square | 24 | profile rule |
| Prefabs/BlockSet/block_road.prefab | block_road | 64 | profile rule |
| Prefabs/BlockSet/block_vault.prefab | block_vault | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | cube_49_575 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cube_50_972 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cube_51_93 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cube_52_147 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cube_54_367 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cube_47_829 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cube_44_406 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_42_852 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_43_40 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_45_248 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_46_710 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_55_910 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_56_261 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_57_683 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_slim_10_957 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | cylinder_slim_14_328 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | plank_square_59_925 | 24 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | panelpost_62_975 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | panelpost_63_697 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | panelpost_64_206 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | panelpost_65_167 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | panelpost_66_15 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | panelpost_67_642 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | bridge_48_253 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | bridge_53_270 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | bridge_60_781 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | fence_6_262 | 64 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | fence_9_12 | 64 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | fence_8_127 | 64 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | fence_1_741 | 64 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | fence_7_991 | 64 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | fence_2_677 | 64 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | holedpanel_13_109 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | holedpanel_19_263 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | holedpanel_17_799 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | vault_16_940 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | vault_18_11 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | vault_15_663 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | holedcube_12_635 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | holedcube_5_300 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | holedcube_3_92 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | holedcube_11_208 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | bat_38_426 | 48 | profile rule |
| Prefabs/BlockSet/blockset_arrangement.prefab | paddle_39_732 | 48 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | ornament_35_177 | 64 | kept as override |
| Prefabs/BlockSet/blockset_arrangement.prefab | road_4_730 | 64 | profile rule |
| Prefabs/saved/buildingblock_column_tall.prefab | buildingblock_column_tall | 40 | kept as override |
| Prefabs/saved/buildingblock_column.prefab | buildingblock_column | 24 | profile rule |
| Prefabs/saved/buildingblock_cube.prefab | buildingblock_cube | 24 | profile rule |
| Prefabs/saved/buildingblock_ramp.prefab | buildingblock_ramp | 40 | kept as override |
| Prefabs/TempProps/bed-frame.prefab | Bed Frame | 48 | profile rule |
| Prefabs/TempProps/blanket.prefab | Blanket | 48 | profile rule |
| Prefabs/TempProps/desk.prefab | Desk | 48 | profile rule |
| Prefabs/TempProps/giraffe.prefab | Giraffe Plush | 48 | profile rule |
| Prefabs/TempProps/guitar.prefab | Toy Guitar | 48 | profile rule |
| Prefabs/TempProps/rug.prefab | Rug | 48 | profile rule |
| Prefabs/TempProps/trex.prefab | Toy T-Rex | 48 | profile rule |
| scenes/minimal.scene | Object (1) | 8 | profile rule |
| scenes/PerfTestDebug.scene | snowman | 64 | profile rule |
| scenes/PerfTestDebug.scene | giraffe | 64 | profile rule |
| scenes/PerfTestDebug.scene | giraffe (1) | 64 | profile rule |
| scenes/PerfTestDebug.scene | dinosaur | 64 | kept as override |
| scenes/PerfTestDebug.scene | MetalTest | 64 | kept as override |
| scenes/PerfTestDebug.scene | MetalTest (5) | 64 | kept as override |
| scenes/PerfTestDebug.scene | MetalTest (2) | 64 | kept as override |
| scenes/PerfTestDebug.scene | MetalTest (3) | 64 | kept as override |
| scenes/PerfTestDebug.scene | MetalTest (1) | 64 | profile rule |
| scenes/PerfTestDebug.scene | MetalTest (4) | 64 | kept as override |

## Displaced props (now hand off to an undisplaced mesh at distance)

- Prefabs/CastleCourtyard/well.prefab :: Stonework
- Prefabs/CastleCourtyard/well.prefab :: Bricks
- Prefabs/CastleCourtyard/well.prefab :: Frame
- Prefabs/CastleCourtyard/well.prefab :: Roof
- Prefabs/Charades Pawn Taylorr.prefab :: Hand
- Prefabs/Charades Pawn Taylorr.prefab :: Head
- Prefabs/Charades Pawn Taylorr.prefab :: Body
- Prefabs/charades_canvas.prefab :: charades_canvas
- Prefabs/hunter.prefab :: Hand
- Prefabs/hunter.prefab :: Head
- Prefabs/hunter.prefab :: Body
- scenes/Heads.scene :: BlueGuy
- scenes/Maps/Medieval.scene :: Head
- scenes/Maps/Medieval.scene :: Body
- scenes/PerfTestDebug.scene :: snowman
- scenes/PerfTestDebug.scene :: giraffe
- scenes/PerfTestDebug.scene :: giraffe (1)
- scenes/PerfTestDebug.scene :: dinosaur
- scenes/PerfTestDebug.scene :: MetalTest
- scenes/PerfTestDebug.scene :: MetalTest (5)
- scenes/PerfTestDebug.scene :: MetalTest (2)
- scenes/PerfTestDebug.scene :: MetalTest (3)
- scenes/PerfTestDebug.scene :: MetalTest (1)
- scenes/PerfTestDebug.scene :: MetalTest (4)
- scenes/PerfTestDebug.scene :: Head
- scenes/PerfTestDebug.scene :: Body
- scenes/PerfTestDebug.scene :: Head
