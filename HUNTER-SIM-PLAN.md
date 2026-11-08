# Hunters in the sim: the full sim walk

Decided 2026-11-07: hunters become real bodies in the shared sim world (code/Sim), the same as players' props.
The host is authoritative. The owner predicts from its own input and is corrected when the host disagrees.
Everyone else draws the hunter from the sim, the way they draw other players' props.

Until now a hunter moves with the engine's `PlayerController` on its owner's machine. Everyone else gets its
transform through the engine's sync. In the sim it is only a keyframed follower, and its pushes are reported to the
host as intents (`NoteSimPush`, `NoteDecoyPush`, `PredictPush`).

## What the hunter runs today (retail engine, decompiled 2026-11-07)

`PlayerController` is a dynamic Rigidbody with all rotation locked, mass 500. It has two colliders:
- a feet box, BodyRadius square
- an upper capsule, radius BodyRadius/2

The velocity is set by code every physics step (Source-style). Neither the feet nor the capsule get friction from
the floor. Gravity is applied by hand, half before the step and half after (`StartGravity`/`FinishGravity`).

- **Ground:**
  - Friction 6 (prefab), StopSpeed 100, GroundAcceleration 10, max speed RunSpeed 280.
  - Ducked speed 70. Velocity is projected onto the ground plane.
- **Air:** AirAcceleration 10, AirSpeedCap 30, AirFriction 0.1.
- **Jump:**
  - JumpSpeed 300, with coyote time.
  - Ground is ignored while rising faster than 140 u/s.
  - Bunny hop allowed, jump boost when running.
- **Steps:**
  - StepUp 18 before the step (forward / up / across / down sweeps, teleport).
  - StepDown 18 after it (`Reground`).
  - GroundAngle 55.
- **Duck:**
  - Height 96 → 74. The hunter calls `UpdateDucking` itself, so posture changes at once.
  - Ducking in the air keeps the head still and tucks the feet up.
  - Unducking needs headroom.
- **Extras:**
  - Static-move sliding along static hulls.
  - Ground velocity from moving ground.
  - Landing event (fall distance) and footsteps.
- **Climbing:** `HunterClimb` is a MoveMode chosen while clinging. It climbs walls only, turns outer corners and mantles.

## Status

**H1 built 2026-11-07 (uncommitted), behind `mimi_sim_hunter` (default off).** The code is in:
- `code/Player/HunterController.Sim.cs`
- `code/Player/HunterSimMode.cs`
- small hooks in `HunterController.cs` / `.Grab.cs`, `SimWorld` and `SimLab.cs`

Measured in SimLab, client hunter, with the same scripted moves on the sim walk and the engine walk:

| | engine | sim |
|---|---|---|
| 1 s run | 254u | 261u |
| ticks to 270 u/s | 16 | 15 |
| stop | 39u in 35 ticks | 38u in 35 ticks |
| jump apex (gravity 850) | 48.8 | 50.3 |
| landed after | 68 ticks | 69 ticks |
| 45° wall slide | 193–198 u/s | 198 u/s |
| owner camera jitter | 0.08u | 0.15u |

- **Steps:** up a 16u step at full speed; a 22u step blocks (StepUp 18).
- **Duck:** walk at 71 u/s; standing back up works.
- **Prediction:** the client's own hunter mispredicts by 0.03u at most, with 0 replays through runs, jumps, ducks and steps.
- **Onlookers:** draw it smoothly except the jump take-off (one or two frames at ~850 u/s: the jump is known a lead late, as with props).
- **Grabs:** grab, swing, throw and throwplayer work with the sim hunter. The hold target rides the same input as the walk. A filter joint keeps the held body from colliding with its holder.
- **Default path:** the lab run with the flag off is unchanged, and engine climbing still works (`hcorner` 10 faces).

**Findings that shaped the port:**
- **The engine keeps its velocity into walls.** Its body reads 280 u/s into a wall it stands against, which is why it slides at the wish's share and is slow to turn away. The sim's contacts take that velocity off, and the accelerate then ran the slide up to 328 u/s.
  - Fix: `AlongWalls` projects the wish off a static wall on the ground. The slide speed matches the engine and turning away stays instant.
- **Enhanced CCD:** off for the hunter body, as on the engine's body. It wasn't the wall cause.
- **Pushing:** the hunter's velocity is set every tick, so it bulldozes heavy loose props at walking speed (pushes a 1878 kg prop at 276 u/s).
  - That's H2's first job: decide the push model (prop hunt's push numbers, or mass-aware).
- **Lab:** `SimLab.HunterPrepare` places its hunter through `Teleport` (`PlaceInSim`) when it's a sim body.

**Not in H1 yet:**
- climbing (H3)
- jump pads, landing events and sounds driven from the sim, footsteps checked (H4)
- the camera's slightly higher jitter
- the bulldozing above

**H2 built 2026-11-08 (uncommitted).**

- **Pushes:** `SimPushAhead`, in the hunter's walk, is prop hunt's push run inside the sim on the host, in predictions and in replays: no report RPC, no hand-made prediction.
  - Something the hunter walks into gets `SimWorld.PushBody`: 60% of the hunter's wish, capped at 400 u/s. A waiting (anchored) decoy is released by it.
  - The hunter goes no faster into it than the push it gave, so it follows at the push's speed instead of bulldozing.
  - The rules are the old `NoteSimPush` ones: touching, not a floor or ceiling, only the part of the wish heading into it. Other hunters and held bodies are excluded.
  - While sim-walking, the hunter's scene-side reports (`NoteSimPush` / `NoteDecoyPush` in `HunterController.Push.cs`) are off.
- **Drawing a push:** the pusher stops a hair short, so the shape-overlap passes never saw the pair. `SimWorld.NotePushContact` notes it instead:
  - our pushed body is drawn from our prediction;
  - two other players' pairs are replayed together;
  - the pair is kept apart in the drawing.
- **Standing on a moving body:** the engine's contact-friction ground. Real gravity, the prop carries the hunter's weight (an impulse at the feet), and an idle hunter GRIPS it, so it rides a walking prop.
- **Fix:** leaving the sim, the controller's Rigidbody is made dynamic again. Presentation had made it keyframed, and with the flag switched off the engine walk went through everything.

Measured in SimLab, all at fakelag 0:

| | result |
|---|---|
| hunter → loose football | hunter follows at 169 u/s (60% of 280); the ball rolls on 166u after |
| hunter → standing player B | B pushed 229–253u at 164 u/s on the host, 0 replays |
| hunter → anchored cone (stand-in decoy) | released on host and client, pushed 218u, hunter at 158 u/s |
| hunter standing on walking player B | rides it: height steady to 0.4u, velocity matched |
| full lab pass, sim hunters on | every pair within ~8u of the host; jump watch: only the known thrown-player landing frames; `hcorner` fails (no climb yet) |

Peak drawn speed of the pushed player, per screen:

| screen | old path | sim |
|---|---|---|
| B's own | 201 | 174 |
| H (pusher) | 220 | 173 |
| A (onlooker) | 162 | 241 |

Every screen's hunter-to-B gap matches the host's within 4u.

**H3 built 2026-11-08 (uncommitted).** `HunterController.SimClimb.cs`: `HunterClimb` (the engine MoveMode) ported into the sim walk as a mode of the hunter driver. Its tuning is still read off that component.
- **Same rules as the engine climb:**
  - walls only;
  - the hard grab: Space held or buffered, steering in, airborne for MinAirTime, not falling fast, not about to clear the top;
  - lookAlong keys;
  - Shift, pulling away, ground underfoot and a lost surface all let go;
  - inner corners, step-over, outer-corner wrap with the carried travel, step-out under shallow overhangs, mantle carry, and climbing on from a mantle with Space held.
- **Order:** the engine read the surface after each step. Here the whole climb runs before the step, reading first, on the same pose. State is in `SimCtrl` (the prop climb's fields plus `ClimbGap`). `SimInput.LabGrab` is the lab's standing grab, sent as input so the host grabs on the same tick. The engine controller's `IsClimbing` follows the sim on every machine, and the scrape sound reads `Clinging`.
- **Results:**
  - jump at a wall with Space held → grab 0.14 s later → climb → mantle onto the 110u top in 0.67 s, 0 replays;
  - `hcorner`: 9 faces round the pillar in 5 of 5 runs (the engine climb manages 10).

**Findings:**
- **A sim body's children were followers.** The hunter's clay head (its own kinematic Rigidbody) sat in front of the hunter's sim body as an obstacle, drawn where the body was drawn.
  - Fix: `SimWorld.ExcludedFromFollowers` now excludes a sim body's whole hierarchy.
- **Climbers caught on corner edges.** Resting flush on a face, as the engine does, the body caught the edge of the face it had just wrapped from as it crossed that face's plane, and stopped dead. This happened on the host and every copy, though a query sweep called the way clear. The engine's body has `ContactRecycling` off, which game code can't set.
  - Fix: `ClimbRestGap` 2u. That gets 7 faces in 6 of 6 runs, against 2–4 at 0 and 0.5u. The edge pull also never heads back toward the face just left.
- **Lab:** HunterClimb's debug move only applies while clinging, as in the engine. A failed grab had the hunter walk sideways for the whole run, shoving the parked props off the lab floor.
- **Seen in passing (not hunter work):** a released loose prop pushed off the floor edge falls forever. There's no fall respawn for loose sim props, and the jump watch fills with it until the lab reopens.

**Known cost (the full walk's):** a player's prop walking into a standing hunter shoves it (152u). The hunter's own screen learns of the walk late and is corrected: 58–83u, faded in, peak 304 u/s, no snaps. Look at it in H5's latency pass.

## Phases

### H1: the walk in the sim (behind `mimi_sim_hunter`, default off)
- `HunterController.Sim.cs`: the host registers each owned hunter as a sim body.
  - Driver 5, owner = the hunter's connection, flags Present | Upright | NoTurn | NoGravity.
  - Shapes FromSource: the feet box and the capsule, read from the live colliders. Standing and ducked shapes are
    both built; the duck state picks which collide.
- `SimHunterDriver`: the engine walk ported to run from (input, `SimCtrl`) alone:
  - ground categorise, friction and accelerate, air strafe, jump (gravity halves, coyote, no ground while rising)
  - step up, a post-step `Reground`, duck with headroom
  - fall respawn
- The sim gets an optional post-step hook per driver, for `Reground` and `FinishGravity`.
- **Input:** wish from keys × eye yaw × speed, Jump pressed, Duck held, view yaw.
  - Remote guess = their last wish repeated.
- **Presentation:** the hunter root is drawn from the sim on every machine.
  - Its own screen draws from its prediction, as a prop walker's does.
  - The camera and eye follow the drawn root.
- **The engine controller stays for look, eye, camera and footsteps, but stops moving the body:**
  - a no-op top-scoring move mode
  - the Rigidbody made keyframed
  - game code reads ground, velocity and duck through one facade instead of `_controller`.

### H2: contacts and pushes
- The hunter pushes props and walkers through real contacts in the sim, using prop hunt's push numbers as the guide.
- Anchored decoys release from the hunter body's intent (`DetectAnchoredPushes`).
- With the sim hunter on, the hunter's paths for `NoteSimPush`, `RequestSimPush`, `PredictPush` and `NoteDecoyPush` are
  removed.
- A held prop never collides with its holder: a collision filter in the sim, instead of `ExcludeFollower`.
- Standing on moving or loose props: ground velocity, and feet weight on dynamic ground.

### H3: climbing
- Port `HunterClimb` into the driver: walls only, Space-held grab rules (`ClimbRules`), outer corners, mantle.
- State goes in `SimCtrl` (reuse the prop climb's fields where they fit).

### H4: everything else that moves a hunter
- Spawns and teleports (round start, respawn, lobby), `FallRespawn`
- Jump pads, knockback
- Landing events and sounds, footsteps, scrape, `PropGround`
- The gun's movement spread
- Debug drivers: wander, `DebugChase`, the chase bot
- Spectator and possession transitions

### H5: test and switch on
- New SimLab scenarios:
  - hunter walk parity with the engine: top speed, time to speed, stop distance, jump apex 56, step 18, duck
  - a hunter pushing a standing prop, a loose ball and a walker
  - a hunter standing on a prop being carried
  - `hcorner` on the sim climb
- The jump watch already covers hunters.
- Latency feel at fakelag 0, 40 and 90; then default on, and delete the follower and push-report paths.
