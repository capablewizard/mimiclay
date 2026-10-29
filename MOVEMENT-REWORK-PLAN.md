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
| Space | Jump | Start climbing | Hold: move up the surface |
| Ctrl | Crouch (slow) | — | Hold: move down the surface |
| WASD | Walk | Push / slide along the wall | Move along the surface, camera-relative |
| Shift | Run | Run | Let go (fall) |

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
