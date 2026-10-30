# Prop movement — plan (v2, from scratch)

Status: Phase 1 (floor controller), Phase 2 (physics press) and Phase 3 (physics climbing) BUILT; on branch `prop_movement_rework`,
2026-10-29. Phase 3 (climbing) not started.
Supersedes v1 (body-sweep mover) and the sphere-proxy discussion.

## Progress

**Phase 1 — done.** `PropMoveHull` (code/Player/PropMoveHull.cs) and the mover partial
(code/Player/HiderController.Movement.cs). Foot probes deleted from `SdfCollider`, `SdfCollisionBuilder`, the
collision debug view and the editor panel (its toggle is now "Move hull"). Verified in the editor on the creative
kitchen, single player:

- Default sphere and the train both rest on the floor a 0.25u skin above it, and park with zero drift.
- Walking at 110 u/s holds a constant height and stops cleanly against a wall, with no jitter or tunnelling.
- Jump apex 59u, lands back at exactly the starting height and parks.
- A 12u box is stepped over; a 30u box blocks.
- Turning while pressed against a wall stops at the wall and never leaves the hull overlapping it.

**Engine finding that changes the plan:** `Scene.Trace.Sweep( body, from, to )` IGNORES `to.Rotation` (the
engine only sets the start rotation). Sweeps are translation-only. Every rotation (turning now; flush alignment
and edge wrapping later) is validated with `PropMoveHull.Overlaps` instead: a start-solid sweep (reliable on
mesh worlds) plus native `CheckOverlap` against nearby convex bodies. Hinges in phases 2–3 must step the
rotation in small increments and pose-test each step rather than sweep it.

**Phase 1 behaviour notes / not yet verified:**
- Edit mode still settles the prop under gravity (the mover runs without input). Parking while attached to a
  wall is a phase-3 change.
- Jump pads go through `HiderController.Launch`. NOT verified: whether a keyframed prop body still fires the
  pad's trigger.
- A prop is blocked by loose dynamic props rather than pushing them. Hunters' capsules block props.
- Multiplayer (client owner, proxies, host-simulated scenery) not yet tested with two instances.

**Phase 2 — physics press (replaces a rejected hinge version).** `HiderController.Press.cs`. A first version
animated scripted "hinges" (crouch-tip, a wall-flush spin, settle-on-landing, a Z upright key) driven by flat
sides of the move hull. The user rejected the feel and the hull's accuracy for alignment; it was removed
2026-10-29.

Now: pushing into a wall hard enough hands the prop to PHYSICS. The root rigidbody goes dynamic
(`PhysicsMode.Pressing`): input drives its horizontal velocity, engine gravity holds it down, friction is off,
pitch and roll are locked, and `PressAngularDamping` (10) plus a `PressMaxTurnRate` cap (40°/s) keep the turn gentle. The REAL SDF collider's contact with
the wall turns it flush, the same way a grabbed prop lays flush when it is pressed into a surface (the grab's
velocity chase while touching). Once the push force drops away, or on a jump, edit mode or a control lock, the body goes back to keyframed, is eased off the wall if the move hull overlaps it, and the mover
resumes and parks as before. Mouse turning pauses while pressing.

**Force, not time, decides both ends.** Push force = clay mass × the commanded velocity the wall cancels, per second
(read off the mover's sweep before the press, off the real physics step during it), smoothed over
`PressForceSmoothing` (0.08s). Compared PER KILOGRAM because input sets a velocity, so force scales with mass
(clay spans ~20 kg knobs to ~4500 kg pots): enter above `PressEnterForce` (2500 u/s², ~25° into a wall at walk
speed; head-on is ~5500), leave below `PressExitForce` (800). Tested on the teapot: a head-on push entered the
press at once and held it; released, it parked with no overlap. The teapot only turned ~3° before settling,
because its round body pushes back through its centre (no torque) — a boxy prop is needed to see alignment.

NOT verified in play yet (the user was mid-session in the editor): the feel and speed of the turn, whether the
damping value is right, and whether the hand-back pops the prop off the wall.

Kept from the hinge version: the root rotation is no longer forced upright each tick, turning spins about the
world vertical through the hull centre, and the camera pivot pin uses the bounds centre's height.

## Goals

1. Move like a normal player controller: walk, run, jump, steps, ramps, fall off ledges.
2. Climb vertical surfaces and get round overhangs (wall → ceiling, over a table lip, under a shelf).
3. Push into a surface to lie flush against it, with whichever side of the clay faces it.
4. Do all of it for arbitrarily irregular shapes, deterministically, with no drift, and networked.

## Core idea: the move hull

Every prop gets one derived shape, the **move hull**: the convex hull of its clay, rebuilt on each commit.

- Built from `SdfCollider.FramePoints` (points outlining every emitted collider shape) with the existing
  `ConvexHull.TryBuild`, thinned to ≤ 40 vertices (resting face and axis extremes seeded first) so it stays
  under the engine's hull edge limit. Sphere and capsule shapes contribute 14 sampled points each.
- It lives on a **scratch `PhysicsBody`** parked off-world, the same pattern `BrushWorldClamp` already uses,
  and is swept with `Scene.Trace.Sweep( body, fromTx, toTx )` for translation. Sweeps cannot rotate (see
  Progress), so rotations are pose-tested with `PropMoveHull.Overlaps`.
- It is used for two things: **moving the prop** (all collision for its own locomotion) and **choosing
  which side lies flush** (its faces).

Why a hull and not a sphere or the real compound collider:

| Proxy | Sits flush on the floor | Clay never clips walls | Irregular shape stable | Cost |
| --- | --- | --- | --- | --- |
| Sphere | no (tall shapes float, or clay pokes out) | no | yes | 1 shape |
| Real compound collider | yes | yes | no (internal edges snag, many shapes) | N shapes |
| **Convex move hull** | **yes** (hull bottom is clay bottom) | **yes** (clay is inside hull) | **yes** (one smooth convex shape) | **1 shape** |

The accepted trade: the prop's *own* movement ignores concavities. An arch cannot straddle a post and a
U-shape cannot wrap round a pole. Everyone else still collides with the real compound collider, so hunters,
bullets and loose props see the clay exactly as it looks.

## Body model

- The pawn root `Rigidbody` is **keyframed** in Character mode (motion disabled, we write the transform). The
  real SDF collider rides it. Nothing pushes the prop, so a parked prop cannot creep, and hunters and other
  props stand on it like a moving platform.
- **Loose, Held and Frozen** modes (creative grab) stay dynamic exactly as today.
- No probes, no foot points, no self-integrated gravity on a dynamic body, no compliance turning.

## State

```
Surface  Normal       // the surface we are attached to; world Up on the floor; none when airborne
Contact  Point        // last hull contact point (hinge for tipping and wrapping)
bool     Sticking     // climb mode: gravity replaced by stick toward Normal
GameObject Ground     // what we are on, for platform carry
```

"Grounded" is `attached && Normal.z > cos(WalkAngle)`. A wall or ceiling is just another normal.

## Per-tick step (simulating machine only)

1. **Wish.** Camera-relative WASD, projected onto the plane of `Normal`. When the camera looks almost straight
   at the surface (wall in front of you), the forward axis falls back to the camera's up, so W means "up
   the wall".
2. **Slide.** Collide-and-slide the hull along the wish, at most three sweeps, skin 0.5u.
3. **Step.** Only on the floor and only when slide hit something steeper than `WalkAngle`: lift by
   `StepHeight` (18u) along `Normal`, sweep forward, sweep back down. Ramps under `WalkAngle` need nothing:
   the slide already follows them.
4. **Snap.** Sweep along `−Normal` by `SnapDistance` (≈ step height on the floor, 4u on walls). Hit = attached,
   take its normal, record the contact point. Miss = edge (see Climbing) or airborne.
5. **Airborne.** Gravity on world down (rise 800, fall ×1.9 as today), weak air control, land on the first
   hull contact.
6. **Turn.** Mouse yaw rotates about `Normal`, validated by a rotating sweep. Blocked = bisect to the largest
   free angle.
7. **Park.** Attached, no input, slow for a few ticks: stop sweeping entirely. Input, a commit, or the ground
   object moving un-parks it. A parked prop costs zero.

Moving cost: about 2 to 5 single-hull sweeps per tick. That is cheaper than today's probes on every shape.

## Flush alignment

Done as the physics **press** (see Progress): pushing into a wall hard enough makes the body dynamic, and the real
collider's contact turns it flush, capped at `PressMaxTurnRate`. The scripted hinge design that used to be here
was built, rejected and removed.

## Climbing (physics, v3 — BUILT 2026-10-29, `HiderController.Climb.cs`)

Climbing is the press carried on: the same dynamic body and real-collider contacts, with gravity swapped for a
pull toward the surface the prop is touching. Nothing is scripted to rotate it; contacts lay it flat on whatever
it's stuck to, exactly as the press turns it flush now.

**Controls (Meccha Chameleon's scheme, no new key):**

| Input | On the floor | While pressing a wall | While climbing |
| --- | --- | --- | --- |
| Space | Jump | Start climbing | Up the surface (held) |
| Ctrl | Crouch (slow) | — | Down the surface (held) |
| Shift | — | — | Let go |
| A/D | Walk | Slide along the wall | Sideways along the surface (camera's left) |
| W/S | Walk | Push into the wall | Depth: forward/back along a ceiling; into a wall (mounts the top) |


**State:** a new `PhysicsMode.Climbing` (dynamic, engine gravity OFF), plus the current surface normal and the
last contact point, both read from the real collider (see step 1).

**Per tick while climbing:**

1. **Contact.** Collision events on the body (enable update events for this mode) give the touching surface's
   normal and point; smooth the normal over a few ticks so a bumpy clay or tile surface doesn't jitter it.
   Between events, a short `Scene.Trace.Body` of the real rigidbody along −normal confirms the surface is still
   there (translation only, which is all the engine's body traces can do).
2. **Stick.** The velocity we write always carries a small component into the surface (`StickSpeed`, ~40 u/s),
   so the contact stays loaded. Pitch and roll stay LOCKED (upright only); the contact only turns it about the
   vertical, capped by `PressMaxTurnRate`.
3. **Move.** WASD by the camera's FULL view (pitch included) flattened onto the surface: W goes where you look (up a
   wall when looking up it, down when looking well down it, forward along a ceiling); the camera's up blends in as
   its forward turns square to the surface so head-on means up, and `ClimbLevelPitch` (15°) counts the orbit camera's
   resting look-down as level; Space/Ctrl along the surface's "up" (world up
   projected onto the plane; on a ceiling, the camera's forward projected instead).
4. **Inner corner** (wall → ceiling, floor → wall): moving into a second surface puts a second contact on the
   body; its normal takes over and the contacts roll the prop onto it. No special case.
5. **Outer edge** (over a table lip, round under a shelf): the contact disappears as the prop moves past the edge.
   The pull then aims at the LAST CONTACT POINT instead of along the old normal, so the prop swings round the
   edge like a magnet and the new face's contact lays it flat. If no surface is regained within a short reach
   (~1 body length), it lets go and falls.
6. **Park on the wall.** No input for a few ticks → the body goes KEYFRAMED where it is (no gravity, no drift),
   still "climbing". Any input makes it dynamic again. A prop hiding on a wall costs nothing and cannot slide.
7. **Edit while climbing.** Entering edit mode parks it on the wall the same way, so you can sculpt in the air
   (the coat-hook case). Leaving edit mode resumes climbing; if the new shape no longer touches the surface, it
   gets the outer-edge reach to find it again, then falls.
8. **Letting go.** Shift, losing the surface, a jump pad, or a control lock: back to the mover, which falls and
   parks as normal (keeping whatever orientation the wall left — no auto-upright).

**Air grab (reworked after "jumping into a wall mostly doesn't stick"):** the first version needed Space HELD at the
instant of contact, but a jump is a tap released long before the wall. Now intent = this airborne stretch began
with our own jump, OR Space held, OR Space pressed within `ClimbGrabBuffer` (0.35s); and the wall is FOUND by a
hull sweep toward where you're steering, out to `ClimbGrabReach` (10u). Walls need you steering into them (no
accidental sticks beside a wall); a jump-only grab skips a ledge you're about to clear (the wall must continue a
step above, so jumping onto a counter still lands on it). Ceilings need Space OR our own jump, while not yet falling fast (a full jump takes ~0.37s to peak, longer than the
press buffer). Wall→ceiling corners: the look-ahead trace starts backed off both the current surface and the
direction of travel — started from the pressed-in pose it read start-solid and was discarded, so a train climbing
up a wall never transferred to the ceiling. Then a W-up-the-wall ping-ponged: on the ceiling the camera mapped W to
"toward the wall", which crossed straight back, every tick (jitter). Fix: CORNER CARRY — on a corner switch the
motion is rotated over the edge (`FromToRotation(oldN, newN)`: up the wall → along the ceiling away from it) and that
direction is kept while the same keys stay held; changing/releasing them hands W/S back to the camera. Plus a 0.2s
cooldown between corner switches. Not
steering: the wall last slid along in the air counts for `ClimbGrabWindow` (0.3s).

**Anti-tunnelling (after "it's quite easy to get pushed through a wall"):** (1) the press drives at most
`PressPushSpeed` (60 u/s) INTO the wall — at the 320 walk speed it rewrote ~6u/tick into thin mesh walls; sliding along
keeps full speed. (2) Climb sensing backs off up to 16u when the body is sunk deeper than the 2u back-off; found
only from further out = EMBEDDED → push out along the surface normal (and never hold still while embedded). Before,
start-solid read as "surface lost", which fired the edge pull INTO the wall — a runaway. (3) The edge pull never drives
into the old surface faster than the stick speed. (4) EnhancedCcd on for Pressing/Climbing. (5) `TryUnstick` first backs
out along `_unstickHint` (the last wall/pressed/climbed surface normal), then up, then only ≤4u sideways — the old
search tried 32u sideways hops that could land beyond a thin wall. (6) A held climb re-checks the surface when it
is moved or its shape changes. Verified on the radio: 3s full-speed press never passed the wall surface; shoved 8u
into the wall while holding on, it came back out on its own side within 0.1s and held with zero drift.

**Fired through walls near other props (follow-up):** `PropMoveHull.Overlaps` used the full hull with the native
`CheckOverlap`, which reports a hull resting a skin away from a CONVEX body (another prop) as overlapping. Every false
overlap ran the unstick search, which teleported up to 32u along a possibly stale wall normal or up. Now: overlap
tests use an INSET copy of the hull (1u smaller; the brush clamp's "contact is not penetration" rule), unstick hops
are capped (8u back out, 16u up, 4u sideways), and the back-out direction only counts for 1s after it was set.
Couldn't reproduce the fling on demand; `mimi_dbg_move_jumps 1` logs any tick that moves much further than the
prop's speed allows, with its mode — turn it on if it happens again.
That inset broke unsticking (follow-up): a body a hair into a wall reads "only touching" to the inset test, but every
FULL-hull sweep from there starts solid, so the old "lift a skin" left props hovering against walls, unable to move
away. Now "only touching" nudges out in small steps (0.25–2u; the way we came, then up, then sideways) to the first
spot `PropMoveHull.StartsSolid` (full hull) says is clear, and the press/climb hand-back eases off by the same test.
Verified on the radio: let go of a climb and walked 293u away in 1s; pressed then walked 328u away in 1s.

**Buried escape (after a train got impaled on the end of a 16u wall shelf, `Cube.017`, a keyframed mesh):** the capped
unstick hops can't clear a deep overlap, and the airborne mover had piled gravity up to -52 800 u/s. Now
(`HiderController.Buried.cs`): genuinely inside something (inset overlap) and unstick fails → BURIED: no collision, no
gravity, the player steers out (WASD by full camera view, Space up, Ctrl down, `BuriedEscapeSpeed` 120) until clear —
no automatic long teleports, so it can't fling. Press and climb hand back to the mover after 0.3s buried. Falls are
capped at `MaxFallSpeed` (3000). Becoming buried logs `[buried]` once with what it's inside and the last 12 ticks'
positions and modes — the way in is still unknown (suspect: the climb's edge pull dragging a tall prop round a thin
shelf edge).

**Getting stuck — root causes found from the `[buried]` logs (train vs stove, wall shelf):**
1. The press capped only the push into the surface it STARTED on; steering into a different one (stove front vs a
   knob or the radio) ran at full walk speed. Now every press tick sweeps the hull along the command and caps the push
   into whatever it would actually hit (two passes for corners), and `_pushNormal` follows that real surface.
2. The clay collider (concave) nestled where its convex hull overlaps things, so the hand-back found the hull
   buried. Now press and climb COLLIDE WITH THE MOVE HULL (`AlignWithMoveHull`, on): the hull is attached to the
   pawn body as its solid shape, the clay shapes stop making contacts (traces still hit them). Against flat walls
   and ceilings this aligns identically (a flat surface only touches the convex envelope).
3. A shape added straight to a body has NO collision tags, and the untagged hull passed through clay props (the
   released radio, the copper pot). It now copies the disguise's tags.
4. A press now ends once the prop is falling (it used to keep driving it off a counter edge).
Measured: 20 approaches into the stove at 5–35° — 0 burials (was ~1 in 10). One "launched up high" case was a jump pad.
`[buried]` logs now name the overlapping bodies.

**Mouse rotation while pressing/climbing (user: "too little control over rotation", "alignment too strong"):** the mouse
now turns the prop on walls and ceilings too. `PressAlignStrength` (0..1, default 0) is how much of the turn the
surface causes by itself. At 0 the body's YAW is LOCKED while pressing/climbing — zeroing the spin each tick wasn't
enough, contact impulses land inside the physics step (a train pressed in at 25° swung to 91°) — and the mouse turns it
directly through the pose-tested `Turn`, so a surface still blocks a turn into it. Above 0 the yaw is free and the
contact's own spin is blended in (capped at `PressMaxTurnRate`). Verified: under a shelf the train turned 90° and
−45° exactly; pressed skewed with no mouse it stayed at 25°; turning into the wall stopped when its far end met it.
Mouse turning stays continuous across walking/pressing/climbing (only the grab's object modes re-seed it).

**Climb controls, current (user, 2026-10-29):** WASD by the camera's full view (up/down a wall included); Ctrl slows
it by CrouchSpeed/WalkSpeed like crouch on the floor; the mouse turns it; SHIFT LETS GO; SPACE HOPS up the surface
(`ClimbHopSpeed` 300 fading at `ClimbHopDecay` 6 ≈ 50u, still stuck; on a ceiling it hops the way the camera looks; a
hop carries round a corner like the climb does). Ctrl no longer moves down. Space still grabs: while pressing a wall, or held/just pressed

**Tried and reverted (user, 2026-10-29: "movement feels a lot worse"):** making props less sticky over pots and pans by
(1) Space while pressing = jump, (2) hold-Space-to-grab only (no grab from the jump itself), (3) no pressing against
other clay props. Don't retry that combination as-is.

**Props as floor (EXPERIMENT, user 2026-10-29: "props still feel really sticky … perhaps they just act like a floor"):**
`PropsAsFloor` (on), `PropStepHeight` 40. ANY clay prop (SdfSculpture or released prop pawn; user: "don't stick to any props" —
the 64u height cap was removed the same day): any surface facing up past `PropFloorMinZ` 0.25 (≈76°) counts as ground (`WalkableHit`, used by
slide, snap, landing and the step-down); its sides step up to 40u; it is never pressed against or air-grabbed. Non-clay
furniture behaves as before. A climb's corner look-ahead never turns onto a clay prop: it steps over it like a bump.
**Climb controls, FINAL for now (user, 2026-10-29: "space = move up, ctrl = move down, a/d = side to side, w/s =
forward/backward but in depth mainly to make mounting easier and moving on the ceiling work, shift = unstick"):**
`vertical` = Space − Ctrl along U (world up flattened onto the surface). `ClimbAlong`: A/D = camera-left flattened onto the
surface (scaled down, not snapped, when looking along the wall); W/S = the camera's LEVEL forward flattened onto the surface
× |n.z| — all of it on a ceiling, none on an upright wall. Pitch no longer matters (`ClimbLevelPitch` removed). `ClimbInto`
= W's level share pointing into the surface; > 0.3 also triggers `TryMantle` when the wall runs out, so W at the lip
mounts the top. The Space push-off (`ClimbLaunchSpeed`) and Ctrl-slow were removed again. The three entries below are
the steps that led here.
Wall→ceiling ping-pong (user: "jump onto a wall and climb up to the ceiling in one movement, I can't latch on"): with Space
(up the wall) and W (on the ceiling: forward = toward the wall) both held, every corner cooldown passed the prop between wall
and ceiling (reproduced by script: normal flipped every 0.2s for 3s). Fix `_cornerLeft`: each corner switch remembers the
surface left and the keys held (move + vertical); while those keys are unchanged, the part of `along` heading back into
that surface is dropped. New keys clear it, so a fresh W still goes back to the wall. Re-test: latched to the ceiling and
held for 3s.

Tried and REVERTED (user: "feels a bit awkward"): limiting the corner carry to just rounding the corner (body depth + 4u),
so W off a ceiling stopped partway down the wall. The carry runs for as long as the same keys are held again.

**Space / Ctrl while climbing (user, 2026-10-29: "disable space/crouch for up/down … spacebar = launch off the wall, ctrl
= slow movement"):** Space launches off the surface via `Launch` — `_climbNormal` × `ClimbLaunchSpeed` (250), straight off the
surface with NO lift (user: "the push off shouldn't add an uplift at all"); the let-go cooldown stops a held Space re-grabbing at once. Ctrl
scales the climb by CrouchSpeed/WalkSpeed again. Up/down a wall is W/S only (`mimi_dbg_climb_vertical` still fakes it).

**W/S on walls (user, 2026-10-29: "holding W on the drawers wobbles left/right a lot more than Space", then "W should
feel like pressing Space AND W … climb up and move forward over the ledge"):** `ClimbAlong` keeps the old look mapping
(camera forward flattened onto the surface, camera up blended in head-on), clamped to 1, and on walls ADDS U·move.x — U =
world up flattened onto the surface, exactly Space's direction. So W = Space + old W, S = Ctrl + old S; up to 2× ClimbSpeed,
as holding both used to be. The steady U dilutes the flattened look's sideways wobble on bumpy faces. Looking down with W
now roughly cancels (as Space+W did): S is the way down. (A first try that REPLACED the look with a surface-frame mapping —
head-on = up, down at ~45° below level — was superseded the same day.) Space/Ctrl kept for now.

**Cling on possess (user, 2026-10-29: "if I possess a prop which is artificially stuck to a prop, it immediately falls"):**
`TryClingOnPossess` runs on the first mover tick of a non-scenery body (`_clingCheckPending`: set at creation and in
`ResumeControl`, cleared after one check). No walkable ground within a scaled step below, but a wall or ceiling within
`ClingReach` 4u (probes on world axes, the prop's own axes, and up; each starts up to 8u BEHIND the pose so a prop touching
or sunk into its surface still finds it; clay surfaces excluded) → `StartClimb` on the nearest. Found while testing: a
just-possessed body can carry a huge body velocity from its first keyframed `Move` (846 u/s), and a keyframed ClimbHold body
keeps drifting at it (a magnet rose 80u up the fridge, lost it and fell) — so the cling and every entry into ClimbHold now
zero the body's linear and angular velocity. Verified by script: FridgeAlphabet (3) and (5) and light_switch possessed →
ClimbHold, 0u drift over 2s.

**Speed scales with the prop (user, 2026-10-29: "small props zoom around way too fast"):** `SizeSpeedScale` =
clamp(√(size / `SpeedReferenceSize` 64), `MinSpeedScale` 0.35, 1), size = cube root of the move hull's x·y·z. Multiplies
WalkSpeed/CrouchSpeed (`ReadWish`) and ClimbSpeed (`ScaledClimbSpeed`: WASD, Space/Ctrl, carry, test driver). Size 16 →
0.5, 32 → 0.71, 64+ → 1 (the toy train, ~75, is full speed). Jump, stick and edge-pull speeds are not scaled. Untested in play.

**Step height scales with the prop (user, 2026-10-29: "a small prop is teleported to the top of a really tall prop … max
step up around 50% of the height of the prop"):** `ScaledStep(max)` = min(max, move-hull height × `StepHeightFraction` 0.5).
Used for the step-up (cap StepHeight 18, or PropStepHeight 40 against clay props) and the grounded snap-down. The climb's
bump lift, ledge check and mantle drop still use the flat StepHeight. Untested in play.

**Mantle (user, 2026-10-29: "climb to the top of the tablecloth and can't really move, still registering as stuck"):**
`TryMantle` — sweep the hull over the lip (toward the wall, `ClimbMantleReach` 16), then down onto ground (`WalkableHit`); if
it lands, end the climb there, grounded, with a little carry onto the top. Tried (a) when the wall is lost while climbing up
(the outer-edge pull used to drag the prop back BELOW the lip, so it bobbed there), and (b) as soon as sensing reads a
walkable top past a steep wall (the body grazing the table top was gated as a "bump edge" while moving, so the climb never
ended). Fails harmlessly while the wall is still in front (the floor at a climb's foot). Untested in play.
Small ledges (user: "trips up on very small ledges — the shelves, the washing machine door"): the mantle only steps up when the
space over the lip is OPEN. If the sweep over the lip hits a steep non-prop wall within `ClimbMantleReach`, it's a ledge on
the way up: no mantle, the outer-edge pull is aimed at that wall (`_climbPoint`) and the let-go timer is reset, so the prop
steps in onto it and keeps climbing. A shelf deeper than 16u still counts as a top.

Surfaces CARRYING props (user: "the fridge with the magnets, doors with doorknobs feel hard to climb"): grabs and the climb's
surface sensing look THROUGH clay (`PropFreeTrace` = MoveTrace without the clay tag) to the wall behind. `GrabSweep` re-sweeps
through props when the plain sweep stopped on one, out to `ClimbGrabReach` + `PropClingReach` (24); sensing that only finds a
prop under the body reaches `ClimbReach` + `PropClingReach` to the wall. The corner look-ahead still SEES props, so magnets
stay bumps to step over. A bigger hull was rejected: it would hit the props sooner and sit further off the wall. Not tested
yet (the user was in play mode); 24u is a guess at magnet/knob depth. Two fixes rode along: walking up a slope steeper than
`WalkAngle` lifts no faster than a `WalkAngle` ramp, and walking off the ground (not jumping) drops any upward speed.
Controls unchanged (Space while pressing still climbs, a jump still grabs walls). Scripted check on open floor as the toy
train: crossed fryingpan, castironpan, pot_copper, mug, dinnerplate, dinnerbowl, egg_cup with no press or grab; with it
off the 44u egg cup pressed and stalled. Not hand-tested; not tested on the hob or as a small prop. Test lanes: the
kitchen floor near (-150,-575) has a JUMP PAD (900 u/s) — a "launch" there is the pad, not the mover.

**Bumps vs corners (user: "climbing the fridge as a letter, crossing another letter turns W into up/left"):** the
corner look-ahead treated any surface >45° off as an inner corner, so a neighbouring letter's rounded edge became "the
new surface" and the corner carry rotated W onto it — and kept it rotated while W stayed held. Now: (1) a hit is
re-checked from a pose lifted `StepHeight` (18u) off the surface; if that clears it, it's a BUMP — no switch; the prop
lifts off the surface for `ClimbStepOverTime` (0.12s) and the stick pulls it back after. (2) The carry is dropped once
the surface faces the way the one it came from did. (3) A sensed normal more than 35° off the current one (a bump's
side or edge) no longer tilts the surface; only the corner logic changes surfaces. Verified on the fridge: ~200u
straight up through two letter columns, step-over fired at each letter, normal stayed (0,−1,0), sideways drift ≤1.3u.
while touching a wall or ceiling mid-air. The let-go press can't re-grab (it predates the let-go, see the cooldown).

**Climb jitter on a pot (follow-up):** a train came to rest on top of a pot while still "climbing" on a stale slanted
normal; every sensed reading (the pot top, ~60° away) was ignored by the 35° gate, and the climb bounced between
"on it" and "lost it" forever. Two causes: (1) a wish straight INTO the surface flattens to float noise whose direction
is meaningless, and the corner look-ahead steered by it — into the floor, the pot — so the climb now needs
`CornerMinSpeed` (5 u/s) along the surface before looking for corners (this also ended climbs started on the
ground); (2) the gate now ADOPTS an off-angle reading that faces back along the stick (dot > 0.3) and has persisted
0.2s while the prop isn't moving — a surface it's resting on, not a bump it's passing. (Adopting walkable readings at
once was tried and ended climbs on a passing letter's sloped top.) Verified: the pot repro holds still within 0.1s;
the fridge climb is unchanged (187u through letters, ≤1.5u drift).

**Decided (user, 2026-10-29):** props stay UPRIGHT — physics may only turn them about the vertical, never tilt
them — so pitch and roll stay locked on walls and ceilings too, and there is nothing to restore after letting go.
Space grabs both while pressing a wall and when held while touching a wall or ceiling mid-air.

**Verified on the radio (single player):** press turned it flush (167° → 180°); Space grabbed the wall; holding
Space climbed straight up at ~80 u/s, upright; climbing into an object overhead transferred onto its underside
(inner corner); with no input it held still (0.000u over 1.5s, keyframed); Ctrl climbed down and let go onto a
ledge, then it landed and parked; launched at the wall with Space held, it grabbed mid-air. NOT yet verified: the
outer-edge pull (over a lip, round under a shelf), edit mode while stuck, Shift to let go (scripts can't press
keys), and multiplayer. `mimi_dbg_climb_vertical 1/-1` fakes holding Space/Ctrl for scripted tests.

## Hunters

Hunters keep the stock `PlayerController`. Add a `MoveModeClimb` component (the engine's `MoveMode`,
selected by `Score()`; the ladder and swim modes are the precedent) that runs the same *Stick* rules on
the upright capsule: wall movement, ceiling hang, outer-edge wrap. No capsule rotation, so there is no camera
roll. Gate it with climb stamina so hunters cannot camp a ceiling. The surface rules live in one static
`SurfaceRules` helper that takes a sweep delegate, so props and hunters share them.

## Networking

- Ownership is unchanged: owner simulates its pawn, host simulates bots, scenery and loose or held props,
  proxies are moved by the synced transform. A keyframed body syncs the same way the dynamic one did.
- Root **rotation** is now gameplay state. It is already in the synced transform, and the engine interpolates
  it on proxies. Hinges take ~0.15s, so they interpolate smoothly.
- The move hull is derived locally from the shape every machine already has. It never goes on the wire.
- A hunter riding a proxy prop on a client may see zero body velocity because the proxy is network-driven.
  If riding feels sticky, feed ground velocity from the proxy's per-tick transform delta.
- Released scenery: one snap and settle, then park. No simulate-until-asleep loop.

## Known traps (from past work)

- A sweep that starts in contact can pass straight through. Every sweep backs off a skin along the last normal
  first, with the `SweepStartInset` retry `BrushWorldClamp` uses.
- `CheckOverlap` and `ComputePenetration` are blind to mesh worlds (every FBX map). Never use them for
  world tests.
- Physics silently drops hulls over 255 half-edges. Thin the move hull before adding it.

## What gets deleted

- `SdfCollisionBuilder.ComputeFootPoints` and its helpers (~500 lines), `SdfCollider.FootPoints`,
  `FootProbeSpacing`, `ProbeFrame`.
- In `HiderController`: ground probes and their debug draw, `RefreshProbesOnTilt`, `SleepWhenSettled`,
  compliance turning, `GroundSnapSpeed`, `GroundRestGap`, `StopSpeed`, `SnagCompliance`, `EditGravity`.

## Phases

0. **Spike (half a day).** Build the move hull on a scratch body, keyframe the root, floor-only slide, step,
   snap and park behind a ConVar. Verify sweeps on the FBX kitchen, that the train does not move when
   parked, that a hunter can ride a moving prop, and cost against today's probes on the crowd benchmark.
1. **Floor controller.** Full walk, jump, step, ramp, fall, turn, park. Released scenery uses settle-and-park.
   Delete the probe system. Exit: no drift on any prefab in `Assets/prefabs/Props`, no stuck case on kitchen
   counters and steps.
2. **Flush alignment.** Physics press (done).
3. **Climbing.** Physics climb mode: stick, move on surfaces, inner corners, outer-edge magnet, park on the wall, edit while stuck, let go.
4. **Hunter climbing.** `MoveModeClimb`, stamina, sounds.
5. **Multiplayer pass.** Two tiled clients: climb and park while watched, hunter rides a proxy prop, possess a
   wall-attached released prop, grab one in creative, mid-load join during a climb.

## Decisions for you

1. **Stick input.** Hold a key (recommended: predictable, a normal controller otherwise) or toggle.
2. **Concavities.** Accept that a prop's own movement uses its convex hull (recommended for v1), or invest in a
   compound move shape later.
3. **Props solid to props.** Recommended yes now that nothing fights the solver. Needs a change to the
   `propbody` pair in `Collision.config` and spawners that sweep-place.
4. **Hunter climb gating.** Stamina (recommended), unlimited, or a pickup.

## Physics-walk PROTOTYPE (2026-10-30)

User: "our prop movement really doesn't work well with other rigid bodies … lots of props are rigid bodies and we'll need
to navigate around and over them". Toggle `mimi_prop_physics_walk 1` (default off; scenery keeps the swept mover).
Code: `code/Player/HiderController.PhysicsWalk.cs`, `PropMoveHull.AttachTo(body, floorClearance)` / `SweepFoot`,
`PhysicsMode.Walking`. Test bed: `mimi_dbg_loose_props` / `mimi_dbg_loose_props_clear` (`code/Debug/LoosePropsTest.cs`;
boxes, balls, planks, a stack, masses by the clay rule so they compare honestly — the toy train is ~9000).

Design — a "floating" physics controller:
- One DYNAMIC body while walking (pitch/roll locked; the mouse turns it as when pressing). Collision shape = the move hull
  with its lowest `ScaledStep` cut off (`floorClearance`), so steps and small debris pass UNDER it.
- Ground = the cut-off FOOT slab (`SweepFoot`) swept down from the floating shape's base to `RideSnap` (6u) below the
  hull's base. A ride spring (`RideSpring` 18/s, `RideMaxLift` 400) sets vertical velocity so the visible base sits on the
  ground → flush, and a step under the footprint just lifts us. Landing needs the base within 1u; staying grounded allows
  RideSnap below. Start-solid while grounded = hold height (the whole-hull probe once started solid on a box stack, read
  "no ground", and the train sank 18u into the floor until the floating shape rested on it).
- Walking = velocity change toward the wish at the mover's GroundControl response, capped at `WalkAccel` (3000) — a
  force, not a teleport. While contacts eat the command (pushing something), only `WalkPushAccel` (500; sticky 0.25s), so
  a body heavier than us blocks us. Air: the mover's AirControl, own gravity (FallGravityMult), MaxFallSpeed.
- Standing on a dynamic body: we ride its velocity only if it's ≥ half our mass (or keyframed); we press our weight into
  it (capped at 4× its own) and our feet's push scaled by the mass ratio. (Inheriting a light box's velocity while
  pushing it back was a feedback loop: a 3 kg box flung 680u.)
- Launch (jump pads) sets the body's velocity. Climbing still grabs from the air (TryAirGrab) and hands back on let-go;
  the possession cling runs first. NOT ported: pressing into walls, parking/hold-still lock, ground carry of rotation.

Verified by script (toy train on the kitchen floor, lane (-50,-1000) +x — pads at (302,-971) and (-14,-608)):
flush at rest (base -543.97 vs floor -544), 0→320 u/s smoothly, 0 drift when stopped, jump apex ~60; small boxes (≤22u)
walked over, balls shouldered aside, nothing flung; a 4-box stack of 20u boxes toppled, train stays grounded (≤5.5u up);
plank stood on (base +4.4); a box 1.6× our mass head-on: moves 30u on impact, then blocks us; jump-grab a wall → ceiling →
let go → lands walking; toggling off returns to the swept mover cleanly.
Open: feel (springy steps? WalkAccel/WalkPushAccel/RideSpring tuning), hold-still on slopes, pressing, networking proxies,
props smaller than a step (clearance = half height), hidden-in-plain-sight parking.

## Physics walk is THE walk; swept mover, press and buried mode removed (2026-10-30)

User: "this is actually very nice … I don't think we really need pressing as we can rotate with our mouse now. Can we make
this the default and remove the keyframed pass. Can we add a toggle to stick to other props too? … size based — stick to
props that are bigger than you but not smaller".

- `HiderController.Walk.cs` (was PhysicsWalk.cs, toggle removed) is the only walk. Dispatch: held / loose / frozen →
  object modes; climbing → StepClimb; scenery being sculpted in place → `Character` = KEYFRAMED PIN; everything else →
  `StepWalk(controlled: !Scenery && ControlActive)` — players, bots, released scenery, bodies in edit mode.
- PARKING added to the walk: grounded on still ground, no input, at rest, at ride height for `ParkAfterTicks` → stop
  driving the body (velocity zeroed) and let physics sleep it. Woken by input, a new hull (`_parkedHullVersion`), or the
  body being moved (|v| > 2 or spin) — so parked props can still be pushed.
- REMOVED: the swept mover (StepMover, Slide, TryStep, SnapToGround, TryUnstick, CarryWithGround, Commit, …), pressing
  (HiderController.Press.cs; its surface-turn / hull-collision / ease-off parts live on in HiderController.Surface.cs with
  their old "Press…" property names), buried mode (HiderController.Buried.cs; `TooLongBuried` moved to Climb — a climb
  stuck in geometry lets go and the solver pushes it out), `PhysicsMode.Pressing`, `PropStepHeight`, `StopSpeed`,
  `ClimbGrabWindow` + `NoteAirContact` (air-wall memory from the slide). With Space and no steering, the air grab now
  sweeps the way the camera faces. Climbs start only from the air grab and the possession cling.
- STICK TO BIGGER PROPS: `StickToBiggerProps` (on) + `StickPropSizeRatio` (1). A clay prop whose size (cube root of its
  body-bounds volume) exceeds ours × ratio is NOT floor-like: normal walk-angle rules, climbable, grabbable, cling-able.
  Smaller ones stay floor. The climb's look-through now re-runs the trace ignoring each floor-like prop it hits (up to
  4 deep, `IgnoreGameObjectHierarchy`) instead of ignoring the clay tag wholesale, so a bigger prop stops it.

Verified by script (the user's gold knob, 64 kg, 17u, speed ×0.49, step 8.3): walk 157 u/s flush; parks + sleeps with 0
drift; jump ~59; toaster (65u) with StickToBiggerProps on → grabbed, climbed its side to the top (+50), walked across and
off; off → not grabbed. Loose props: blocked by a 14 500 box and by 262 kg stack boxes (4× its mass), stands on a plank, no
launches or sinking. Released train scenery: Walking, parked, asleep. NOT verified: multiplayer, a prop pinned while
sculpted then released, bots. Test-harness gotcha: teleporting a keyframed scene prop back (WorldTransform) sweeps through
whatever is in the way and shoves it — move test pawns clear first.

Jump scales with size too (user, 2026-10-30): launch speed = JumpPower × √SizeSpeedScale, so the jump HEIGHT scales by
the same factor as walking speed (the 17u gold knob, ×0.49: ~29u instead of ~59u; props ≥ SpeedReferenceSize unchanged).
Not tested in play (the user was editing the knob).

Climb distances scale with size (user, 2026-10-30: "a very small prop climbing over the pots — as soon as I reach the lip
I get teleported forward"): the mantle's forward reach = `MantleReach` = 60% of the prop's narrowest width, clamped to
2..`ClimbMantleReach` (16 = the cap for big props); its drop onto the top = ScaledStep + reach; the bump lift (corner
look-ahead's "is it a bump?") and the air grab's "clearing a ledge" check use ScaledStep instead of the flat StepHeight;
the mantle's carry onto the top uses the scaled climb/walk speed. Not tested in play.

Mantle switched OFF for now (`ClimbMantle`, default false; user, 2026-10-30: "temporarily disable the mantle … see what
it's like with physics now"). TryMantle returns at once, so the small-ledge retarget is off too; at the top the climb
loses the wall (edge pull, then let go after ClimbLoseTime) and the walk's ground spring takes over.

Mantle back ON (user: "we definitely need the mantle reach — but reduced a lot to prevent it feeling jittery"):
`ClimbMantle` true; reach = narrowest width × `ClimbMantleFraction` (0.25), clamped 1..`ClimbMantleReach` (now 6, was 16).
Just far enough for the top to catch the prop; the walk carries it on. (Train: 6u; a 10u prop: 2.5u.) Side effect: the
small-ledge check only looks this far behind a lip, so narrower shelves count as tops sooner.

Mantle = PHYSICS HAND-OFF, no teleport (user: "now we're getting stuck to the top of the mantle … smooth as well as
reliable"). The short teleport left props balanced on the edge — and refused outright when it could move < 2u (small
props with the new short reach): stuck. Now `TryMantle(pos, n, lost)` runs EVERY climbing tick that's going up or pushing
in: from a step up (ScaledStep — where the walk's floating shape starts) and MantleReach in, is the wall gone, with
walkable ground under that pose no more than a step above our base? Then EndClimb and carry on over the lip at climbing
pace (`_velocity`, which the walk takes when it switches the body over); the walk's floating shape clears the lip and its
ground spring lifts us onto the top, as for any step. `lost` (the wall already gone) keeps the small-ledge retarget.
Old triggers: the "walkable top sensed" one stays (lost: false); the outer-edge one is folded into the per-tick check.
Not tested in play (the user was playing). Test site: kitchen floor (-250,-1250) facing +y — a counter front 73u away,
top +158 (Cube.013).

Mantle GUIDED CARRY (user: "if I hold W + Space we get stuck to the top"): just letting go dropped the prop — it was still
beside the wall's face with nothing under it, sank below the lip before it got over, slid down, and W + Space re-grabbed
the wall after the let-go cooldown: a loop. Now TryMantle starts a carry in the walk (`_mantleDir/_mantleTopZ/_mantleSpeed`,
`MantleCarryTime` 0.4s): airborne → no gravity, move in at the carry speed and rise toward the top's height at RideSpring;
grounded → keep adding the carry until it's fully on; no air grab while carrying. Verified on an immovable 120u test block
(train, Space held): climb → carry from base +97 → grounded on top, 5–10u per 25ms, no teleport.
Two findings: (1) hot reloads keep a live prop's property VALUES — the user's train still had ClimbMantle=false / reach 16
from the temporary disable, so their recent climbs had no mantle at all (set to the defaults in their session). (2) Kitchen
worktops OVERHANG their cabinets by ~3u at about +145, and in nooks (cabinet side + wall + overhang) the climb passes
between wall, side and the overhang's underside and never reaches the top (both counter sites near x −250 stall the train at
base +74). Not fixed yet.

Overhangs and ceilings (2026-10-30, "fix overhangs"). CORRECTION: the counter-front stalls blamed on "worktops overhanging
by ~3u" were test-site error — both sites (x −250) are in a recess UNDER Cube.013, a 16u slab at +142..+158 spanning
y −930..−1350; the train was climbing into a real ceiling. Open counters ((−50,−1200) facing −x, (−200,−600) facing −y,
top +158) climb straight on in ~1.3s. What the bad sites exposed, now fixed:
- Space on a CEILING reached from a wall moves OUT along it (`_ceilingOut` = that wall's flat normal); Ctrl comes back.
- `_cornerLeft`'s "don't go back into the surface you left" is applied to WASD and Space/Ctrl SEPARATELY: on such a
  ceiling W (toward the wall) and Space (out) cancelled exactly, so filtering their sum dropped nothing — W + Space hung.
- `ShallowOverhang` (thin rays up, a step out from the underside hit, across the prop's width): a downward face met
  climbing up a wall whose edge is within a step is a BUMP (step out over it), not a ceiling corner. A 40u overhang
  over a cabinet: the prop steps/slides out and climbs the front face, mantles at the top (1.9s).
- `TryMantleFromUnder`: out past a ceiling's outer edge (surface lost, heading out), sweep the hull down from high above
  just back over the edge; walkable top 0.5–1.5 body heights (+ step) up → EndClimb + `StartMantleCarry(back in, top,
  0.8s)`. (A first try wrapped onto the edge's front face via the outer-edge pull — it dragged the prop into the corner.)
  Test: 100u overhang over a 140u cabinet → climb, ceiling, out, mantle → on the +158 top in 1.8s.
- Walk ground probe: the foot slab is inset 1.5u sideways (`PropMoveHull.FootInset`), and if it still starts solid a ray
  from the hull centre stands in — landing pressed against a wall read "no ground" and the train SANK 11u into the floor.
- `StartMantleCarry(dir, topZ, time)` shared by both mantles; `mimi_dbg_climb_corners` logs corner/bump/overhang decisions.
