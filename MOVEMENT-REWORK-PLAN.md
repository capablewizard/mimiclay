# Prop movement — plan (v2, from scratch)

Status: Phase 1 (floor controller) BUILT on branch `prop_movement_rework`, 2026-10-29. Phases 2+ not started.
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

## Flush alignment ("push into it")

On commit, cluster the hull's faces into **flat sides**: merge faces whose normals are within ~10°, sum their
areas, drop sides smaller than a minimum area. Store each side's local normal and area.

**Pushing to align.** While attached or touching a surface S and pushing into it (input toward S, or crouch
on the floor) with *Stick* held for `AlignHold` (~0.15s):

1. Pick the side whose current world normal is closest to `−S.normal`.
2. Target rotation = shortest arc taking that side's normal onto `−S.normal`, keeping the twist about S.
3. **Hinge** about the contact point toward the target with a rotating sweep, over ~0.15s. If the hinge is
   blocked part-way, stop there.

The prop tips over like a real object pushed onto its side, so it never teleports into a pose. Round clay has
no large flat sides, so it simply sticks without rotating. Concave clay rests on its hull, meaning a hook rests
on its tips, which is how it would physically rest.

**Settling on landing.** When a falling prop lands on a corner or edge, hinge it onto the side nearest world
down whose centroid projection is inside the side (a stable rest pose), so it never balances on a vertex.

**Upright.** One key returns to the authored orientation (the way the player sculpted it), validated by the
same rotating sweep, with a lift along `Normal` if it would overlap.

## Climbing

Requires *Stick* held (or toggled, see decisions). Without it the prop behaves like a normal player
controller: walls block, ledges drop you.

- **Inner corner** (floor to wall, wall to ceiling). Slide hits a surface steeper than the current one while the
  wish pushes into it: run the flush alignment against that surface, then attach to it.
- **Outer edge** (over a table lip, round to the underside of a shelf). Snap misses after a move: hinge the
  hull about the last contact point, rotating toward `−Normal` around the axis `Normal × moveDir`, as one
  rotating sweep of up to 120°. The first hit is the surface round the corner: align and attach. No hit means
  airborne.
- **Ceiling from the floor.** Jump with *Stick* held. The first surface the hull touches mid-air is attached
  and aligned.
- **Letting go.** Releasing *Stick* on a wall or ceiling drops you with gravity, keeping your orientation, then
  settles on landing. Jump pushes off along `Normal`.
- **Editing in place.** Entering edit mode parks the prop exactly where and however it hangs. On exit, snap
  against the last normal; a miss means it falls. `BrushWorldClamp` keeps the clay out of the world during the
  edit, so a commit cannot embed it, and the move hull rebuilds from the new shape.

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
2. **Flush alignment.** Flat sides, push-to-align hinge, settle on landing, Upright key.
3. **Climbing.** Inner corners, outer-edge wrap, ceilings, letting go, editing while attached.
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
