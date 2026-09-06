# Clay toy train

Built against the supplied blue-engine / red-and-green-wagon concept.

- Complete prop: `Assets/Prefabs/Final/Toys/toy_train.prefab`.
- Individual cars: `toy_train_engine`, `toy_train_wagon_red`, `toy_train_wagon_green` in the same folder.
- Scene: `Assets/scenes/toy_train_review.scene`; its main camera preserves the concept comparison angle.
- `concept-view.png` and `reverse-view.png` are actual scene camera renders, not asset icons.

All cars retain the finished mug's SDF renderer, clay material, collision and damage component stack. The complete train groups three editable sculptures. Approximate overall size is 185 long, 44 wide, 68 tall, in game units, with the wheel bottoms at Z=0. This is a static prop; no wheel animation or driving controller is included.

Regenerate from any directory with `powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/gen-toy-train.ps1`. This replaces the four train prefabs; save hand-edited variations under another name. GUIDs and output are deterministic. All four prefabs compiled successfully and the train was visually inspected from both sides in its scene. Physics interaction has not been playtested.
