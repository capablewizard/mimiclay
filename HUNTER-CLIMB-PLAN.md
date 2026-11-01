# Hunter climbing — plan (2026-11-01)

Goal: hunters climb walls and ceilings the way props do (`HiderController.Climb.cs`), but the hunter's
walk/jump/sprint must stay exactly as snappy as today. When "feels like the props" and "feels responsive"
disagree, responsive wins. Also: hunters walk up steeper slopes.

Branch: `hunter_climbing`.

## Where we start

- Hunters use the engine `PlayerController` + `MoveModeWalk` (`hunter.prefab`): capsule (radius 16, height 70)
  plus a box feet collider, always Run 320 (no sprint key since 2026-11-01: `RunByDefault`, empty
  `AltMoveButton`; crouch 70 is the slow move), Jump 300, AccelerationTime 0 (instant),
  GroundAngle **45**, StepUp/StepDown 18. `HunterController` already overrides ducking
  (`UpdateDucking` in `OnFixedUpdate`, see the timed-duck note).
- The engine picks the active `MoveMode` every physics tick by `Score()` (highest wins,
  `PlayerController.ChooseBestMoveMode`). `MoveModeLadder` is the template for a climb: gravity off,
  `Controller.IsClimbing = true` (so `IsAirborne` is false), its own `UpdateMove`.
- Props: climbing is a physics mode on a dynamic body: gravity off, velocity set each tick to
  "along the surface − pull into it", surface sensed by a short backed-off sweep, inner corners switch
  surface, outer edges pull toward the last contact, mantle = physics hand-off over the lip, idle = keyframed hold.

## Approach: a `MoveModeClimb` component on the hunter

A new `code/Player/HunterClimb.cs` (`MoveModeClimb : MoveMode`, added to `hunter.prefab` next to
`MoveModeWalk`). Nothing in the walk changes while it isn't climbing. When it isn't climbing, it scores
-100 and does nothing except a cheap grab check while airborne.

Why a separate mode rather than sharing the prop code: the prop climb is ~800 lines, and most of it handles
irregular hulls (looking through floor props, bump step-over, hull-vs-clay sensing, embedded recovery,
size scaling). A capsule needs none of that. The *rules* stay the same (controls, corner switch, edge pull,
mantle hand-off), and only the small pure helpers get shared: `ClimbAlong`, `ClimbInto`, corner rotation
and `_cornerLeft` filtering. They move into a static `ClimbRules` class that both controllers call. The
tuned prop climb is NOT refactored beyond that extraction (props behave identically; verify with the
radio test from the movement plan).

### Per tick (owner only; proxies follow the synced transform)

| Hook | Does |
|---|---|
| `OnFixedUpdate` | Not climbing + airborne: grab check (below). Climbing: read inputs, latch let-go. |
| `Score` | `Priority` (10, above ladder/swim) while `_climbing`, else -100. |
| `OnModeBegin` | `Controller.IsClimbing = true`, kill velocity into the wall, keep velocity along it (no dead stop on contact). |
| `UpdateRigidBody` | Gravity off, **LinearDamping 0** (the ladder's 20 is exactly the "sticky" feel we're avoiding; we set velocity directly). |
| `UpdateMove` | Returns the wish along the surface (`ClimbRules.ClimbAlong` + Space/Ctrl up/down). No smoothing, matching AccelerationTime 0. |
| `AddVelocity` | Overridden: `Body.Velocity = along − n·StickSpeed` (on surface) or edge pull (off it). Direct set, not the engine's AddClamped accel. |
| `PostPhysicsStep` | Sense surface (capsule sweep `Controller.TraceBody` from 2u off the wall toward it), inner-corner look-ahead, mantle check, lose-surface timer. |
| `OnModeEnd` | `IsClimbing = false`; velocity kept (clamped to RunSpeed) so letting go or mantling carries momentum. |

Capsule sensing is far simpler than the prop's: the capsule is upright and convex, so one sweep along `-n`
gives a clean normal. There's no bump/through-prop logic in v1.

Idle on a wall: velocity 0 and gravity off is enough to hold perfectly still. A capsule doesn't drift, so the
prop's keyframed ClimbHold isn't needed.

### Anti-sticky rules (the important part)

These are where the hunter deliberately differs from props:

1. **Space on the ground is ALWAYS an instant jump.** No ground grab, no tap/hold detection, no delay. You
   climb by jumping at a wall with Space still held.
2. **Grab needs Space held at contact (or pressed within ~0.15s)**, steering into the wall
   (dot > 0.5), within ~6u. The prop's "this airborne stretch began with my jump" intent is NOT used:
   a tapped jump into a wall must never stick, because hunters jump at walls constantly while chasing.
3. **No grab while clearing a ledge** (the prop's `clearingLedge` test: if the wall stops within a step
   above, you're jumping onto it, not up it).
4. **No grab in the first ~0.1s after leaving the ground** and none while falling faster than ~400 u/s
   (a long fall into a wall shouldn't catch).
5. **Leaving is instant and has several exits.** Any one of these lets go on the same tick:
   - Shift-tap (same as props; Shift is no longer sprint, see decision 1),
   - pulling away: S/back into open air for ~0.1s,
   - Ctrl at the foot of the wall touching the floor = stand up,
   - Space released for ~0.25s while *not* moving (optional, see decision 2).
6. **Walls the capsule can stand on are never climbed.** The climb's "walkable" threshold reads
   `MoveModeWalk.GroundAngle`. One number, so there's no band of slopes that are both standable and
   climbable, and none that are neither (the prop climb uses its own `WalkAngle` 50°).
7. **Hider pawns aren't climbable** (a hunter can't cling to a disguised player and ride it). Clay scenery is.
8. **Speed:** hunter climb ~150 u/s (props 80 × size scale). A climb that feels slower than walking reads
   as sticky.

### Mantle (top of a wall)

Same rule as `TryMantle`: climbing up or holding W, and from a step up the wall is gone with standable
ground under it within `StepUpHeight` → end climb and do a **0.2s guided carry**: velocity up to the lip
plus forward, gravity off, then the walk's `StickToGround` takes over. No teleport. Because it's a capsule
the lip test is one `TraceBody` forward + one down. Holding Space through the mantle into a taller wall
re-grabs (the prop's `_climbOnFromMantle` rule).

Ceilings: same as props (capsule stays upright under it; Space continues out from the wall below;
`TryMantleFromUnder` onto the top past the edge). The camera never rolls.

### Gun and camera

Climbing is first-person-only state, so no camera work is needed (no roll, eye height unchanged). Fire
while climbing: allowed by default (decision 3). The hunter's jump/land spring (`DetectJumpEvents`) must
ignore climb start/end (it watches `IsAirborne` transitions, which `IsClimbing` flips) so that
grabbing a wall doesn't play a landing dip or squash. Footsteps: silent while climbing, or a quieter
"scrabble" step later.

### Networking

Owner simulates, as with the walk. The only new visible state is whether the hunter is climbing, for
the clay body's pose and footsteps on other machines. `[Sync] bool IsClimbingSynced` on HunterController
(not the engine's `IsClimbing`, which proxies don't run). Per the scene-sync memory this is a pawn, not scene
state, so `[Sync]` reaches clients.

## Steeper slopes

- `MoveModeWalk.GroundAngle` 45 → **55** in `hunter.prefab` (props walk 50°; going a little past them
  is fine for the chaser). This is one property and needs no code.
- Side effect to check: at 55° the steep sides of round clay props become standable. Usually that's
  welcome, but test on the kitchen's rounded objects.
- **Speed up steep slopes:** the engine wish is horizontal (`MoveModeWalk.AddVelocity` zeroes Z) and
  the capsule's contact deflects it, so speed up a slope drops to roughly cos(slope) (55° ≈ 57%).
  If that feels sluggish, add a tiny `MoveModeWalk` subclass that projects the wish onto the ground
  plane when grounded (speed kept, not just direction). Measure first, and only if needed.
- Standing still on 55° already holds: the engine turns gravity off and brakes when grounded and idle.

## Phases

1. **Slope**: GroundAngle 55, feel it. (Tiny, can ship alone.)
2. **Extract `ClimbRules`** from `HiderController.Climb.cs` (pure functions only), then re-run the
   prop climb checks to confirm nothing changed.
3. **`MoveModeClimb` core**: grab rules 1–4, wall movement, idle hold, exits, walkable = GroundAngle.
   Debug ConVar `mimi_dbg_hunter_climb` draws the sensed normal and contact point.
4. **Corners + edges + mantle**: inner-corner switch, outer-edge pull, mantle carry, ceiling,
   mantle-from-under.
5. **Hunter polish**: jump spring/footsteps ignore climb, `[Sync]` climbing flag, body pose.
6. **MP test** with a joined client (Release compiler for joined clients).

Acceptance (all with a stopwatch or trace log, not just by feel): Space on the ground → `IsAirborne`
on the next tick, the same as today. A tap-jump into a wall never grabs. Let-go to falling is ≤ 1 tick. Jump → hold →
grab ≤ 1 tick after contact. Zero drift on a wall over 10s. Mantle onto the kitchen counter in one motion.

## Decisions for you

1. **Let-go key — DECIDED 2026-11-01:** Shift lets go, as for props. The hunter's sprint was removed to
   free it: hunters always move at run speed and crouch for control. S-away and Ctrl-at-the-floor
   exits stay. A wall-jump (Space tap after releasing) is a possible later addition.
2. **Auto-drop when idle?** Props hang forever. For hunters, should a wall/ceiling hold be limited
   (stamina, e.g. 4s then slide) so they can't camp a ceiling? The movement plan suggested stamina.
   Recommendation: no stamina in v1, add it if playtests show camping.
3. **Shooting while climbing**: allowed (recommended), or holstered?
4. **Slope angle**: 55° (recommended) or 60°?

## Progress

**2026-11-01: BUILT (phases 1–4 and most of 5), scripted tests only. Not felt with real input yet.**
- Hunter `RunSpeed` 280. No sprint: `RunByDefault` on, empty `AltMoveButton` (set in the prefab and in `HunterController.OnAwake`).
- `GroundAngle` 55.
- `code/Player/ClimbRules.cs` holds the shared pure helpers (Along, Into, Vertical, NotBack). `HiderController.Climb.cs`
  calls them; prop behaviour is unchanged by construction but hasn't been re-run.
- `code/Player/HunterClimb.cs` (`MoveMode`, on `hunter.prefab`, also GetOrCreate'd in OnAwake). It has grab rules 1–4
  plus a "will clear the ledge at the top of this jump" test, wall/ceiling movement, gap-closing (never pressing),
  idle hold, Shift / S-away / ground exits, inner corners + bump step-over, the outer-edge pull, and the mantle carry
  (rise, then in, then the walk).
- HunterController: DetectJumpEvents treats clinging as airborne (no land/launch on grab/let-go), and eye step
  smoothing is off while climbing.
- Debug: `mimi_dbg_hunter_climb` (logs + surface gizmo), `mimi_dbg_hunter_climb_vertical` (fake Space/Ctrl for scripts).
- Verified by script in kitchen_creative: tap-jump at a wall doesn't grab; Space-held jump grabs at ~150ms; climbs
  at 150 u/s flush (8.2u = feet box half-width); mantled onto a 120u counter; 0.000u drift over 2s holding;
  Ctrl climbs down and hands to the walk, grounded.
- Not done: `TryMantleFromUnder` (onto the top past a ceiling's edge), the `[Sync]` climbing flag (no consumer yet),
  climb footsteps. Untested: Shift/S-away let-go, corners/ceilings/outer edges, multiplayer.
- 2026-11-01 (user): looking ALONG a wall, W/S move along it the way you look (`ClimbRules` `lookAlong`, hunters only).
  The look's along-wall share fades in between ~44° and ~70° off the wall's normal, so roughly facing the wall,
  W is still "into" (mantle) and S still pulls away. Looking along, S walks backwards along the wall instead of letting go.
  Movement stays level (pitch ignored). Props are unchanged.
- 2026-11-01 (user): S walks backwards along the wall the same way, and lets go only when W/S head mostly AWAY from it
  (level look × key against the wall's flat normal > 0.5, i.e. within ~60° of straight off it, for PullAwayTime).
  So: S facing the wall lets go, W with your back to it lets go, S looking along it walks backwards.
- 2026-11-01 (user): NO ceilings for hunters. No ceiling grab; a corner onto a downward face (normal.z < -0.5) never
  switches (climbing up into an overhang stops under it, still on the wall); a sensed normal that drifts that far ends
  the climb. The wall is now sensed with a capsule the body's radius but 2u shorter at each end (`SenseSweep`): with the
  whole body, touching the overhang read as start-solid = "embedded", and the push-out shoved the hunter off the wall.
  Scripted test on a temporary box rig: jump into a ceiling with Space held → no cling; climb up into an overhang →
  stays on the wall, 0.00u drift over 1s. Kitchen wall-climb regression: same as before (mantled onto a 158u cabinet).
- 2026-11-01 (user, flatgrass facade): holding Space over a ledge now carries on up the wall behind it.
  - After a mantle with Space still held, `TryClimbOn` grabs a wall ahead (steering, else the way the mantle went) within
    64u, from the ground or in the air, rising past a step. Cleared when Space is released.
  - The gap to a wall grabbed from further off (or a set-back wall) closes at climbing speed, and sensing reaches that far.
  - Narrow ledges: when the wall runs out, sensing looks 24u further in for a wall facing the same way (`SetBackReach`).
  - The mantle needs the top to be ≥1u above the feet (standing on a ledge, it kept "mantling" onto where it stood).
  - Verified on the real flatgrass facade by script, Space held only: base → recessed wall → mantle onto the band at
    +195 → straight on up the deep wall behind (44u back) → mantle onto the roof at +375, in ~2.8s.
- 2026-11-01 (user): hunter collider sized to the clay model: BodyRadius 30 (capsule r 15 ≈ body r 14; feet box 30²),
  BodyHeight 96 (head top ≈ 96), EyeDistanceFromTop 34 (eye still 62, so camera + Visuals pivot unchanged), DuckedHeight 74
  (same 22u crouch). Was 16 / 70 / 8 / 48: a 16u-wide column with the head poking 26u out the top.
  - ENGINE (retail 26.11): capsule radius = BodyRadius/2 (open-source says ·√2/2), feet box = BodyRadius square,
    BodyHeight/2 − 1 tall. `SenseSweep` now reads the live BodyCollider + FeetCollider instead of assuming.
  - Shallow overhangs (clear a step out from the wall) are stepped out under when climbing up stalls (`StepOutUnder`);
    the wider feet box caught under flatgrass's 14u facade band. Real ceilings still stop the climb.
  - Re-verified by script: flatgrass facade climbs through; deep-ceiling rig holds flush (15.0u = capsule r).
