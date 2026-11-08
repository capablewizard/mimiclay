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

**Default ON since 2026-11-08** (`mimi_sim_hunter`, after the user's hand-test: "looks solid"). `0` keeps the engine walk for A/B. The follower and push-report paths it needs stay until the old walk is retired.

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

**H4 built 2026-11-08 (uncommitted).**

Fixed:
- **Jump pads** were dead to anything the sim walks, hunters and walking props alike: the pad wrote a launch onto the scene body, which is only where the pawn is drawn.
  - Now both sim walks test their body against the pads' trigger volumes every tick (`JumpPad.SimPadAt`) and launch from inside the sim. The debounce is `SimCtrl.PadCool`, on the wire. The host broadcasts the boing.
  - The owner's prediction launches on the host's tick: hunter 886 u/s, apex 450u, 0 replays; prop A rose 511u, 0 replays.
  - The scene trigger skips pawns the sim walks.
- **Walking on a prop:** the sim walk scaled acceleration and braking by the ground's friction, and a prop's move hull has none, so a hunter on a prop could neither go nor stop.
  - Ground friction is now floored at the clay's 0.8, as the engine path's `GripSlipperyGround` does.
  - Result: 175 u/s after 8 ticks, stops within 0.6u, stays on top.
- **Landing volume:** the engine body's velocity reads zero in sim mode, so every landing played at its quietest. The landing splat now uses the sim body's fastest descent (`TakeSimFallSpeed`), and the engine thump gets its proper volume added.
- **Destroy cleanup:** a destroyed hunter (a swap to a prop, a respawn) now leaves the sim at once (`LeaveSimOnDestroy`): input handed back, hold filters removed, host unregisters.
  - Swap to a prop and back: the body leaves, the prop walks, the new hunter sim-walks and pads work.

Checked, no change needed:
- teleports (the round's return-to-spawn uses `Teleport` → `PlaceInSim`)
- spawns (registered at the current pose when the pawn goes on the network)
- the debug drivers (the wish override), the chase bot (hold input)
- jump and landing events and footsteps (ground transitions on the drawn pose; `MoveVelocity`)
- the gun bob (`MoveVelocity`)
- free cam, edit mode, round lock (input gated)
- there's no knockback in the game

Lab pass of the hunter scenarios, sim hunters on: all pass, `hcorner` 9 faces. The jump watch's only new lines are single 2000 u/s frames of A on B's screen, at A's start marker: the lab's setup teleport arriving after the run began.

Left as-is:
- Pads don't launch held, loose or lying sim bodies (the old scene launch didn't reach those in the sim either).
- Edit mode and the round lock stop a sim hunter by friction (about 0.3 s) rather than zeroing its velocity at once.

**H5 testing 2026-11-08 (uncommitted).** Five SimLab hunter scenarios, at the props' stations (`SimLab.Base`):
`hpush`, `hloose`, `hpushed`, `hride` and `hclimb`. The hunter is now recorded as a fourth body on every screen: from the
sim, or from its transform on the engine walk.

Extra drawn depth against the host (u), worst of each screen:

| | engine walk, no lag | sim, no lag | sim, fakelag 40 (~165 ms) | sim, fakelag 90 (~265 ms) |
|---|---|---|---|---|
| `hpush` | 5–11 | ≤0.3 | ≤0.0 (1 run dropped, see below) | ≤0.0 |
| `hloose` | 14–15 | ≤0.8 | ≤−0.1 | ≤−0.8 |
| `hpushed` | ≤0 | ≤0.1 | ≤0.3 | ≤0.1 |
| `hride` | 3.6 (vgap 8.7) | 0.1 | 0.1 | 0.1 |

- **`hclimb`:** tops out at 110 every run at every lag. The engine climb can't be scripted, since its grab needs the input pass the debug walk turns off.
- **`hcorner`:** 9 faces every run at 40 and 90.
- **`hpushed`:** this is the prop-walks-into-hunter case, the hunter's correction cost from H2. It's within 0.5u now at every lag.

**Open (not fixed):**
- **Holder's view at ~265 ms:** in `held`, with sim hunters on, the holder sees A pressing into the held ball a little worse. In 5 runs: one 390u single-frame snap and one 1585 u/s frame. Depth is 7.5u, against −1.0 with the flag off. Likely A's walk now shoves the hunter's real body, so its screen and its hold get corrected.
- **Onlookers at ~265 ms:** they draw a remote hunter stopping with one 1800 u/s frame. The guess keeps walking for the lead, then folds back. Props' walkers have the same; for props it was softened with BrakeAccel, but hunters keep the engine's instant stop.
- **Seen, not new:** at 40 and 90 a thrown ball is drawn up to 19u under the floor on onlookers' screens. That's the late-throw item in held-prop-prediction-todo.

**Follow-up the same day (user's picks).**

1. **Tried, then REVERTED: a heavy hunter** (20,000 kg in the sim, so props couldn't shove it).
   - In the lab, the host's hunter moved 1u in `hpushed`.
   - It didn't stop the holder's glitch in `held`: a 1650 u/s frame of A in 2 of 5 runs, about 0.3 s after A stops against the held ball. So the shove was never its main cause.
   - The user found it far too heavy by hand. Climbing onto props was too disruptive, so the hunter is back at BodyMass (500 kg).
   - Both open items stand: a prop walking into a hunter corrects the hunter's screen, and the holder's glitch.
2. **Floatier hunter walk, between the old hunter and props.** On the prefab's walk component (the engine walk and the sim walk both read it): Friction 6 → 3.5, GroundAcceleration 10 → 6.

   | | before | now | props |
   |---|---|---|---|
   | time to 270 u/s | 0.15 s | 0.24 s | ~0.4 s |
   | stop from 280 | 38u in 0.35 s | 64u in 0.59 s | ~102u in 0.64 s |

   Sim and engine walks match. Onlookers' stop overshoot at ~265 ms (`hloose`) is down from 1800–1970 to 1500–1600 u/s, about at the watch's threshold.

**Lab fixes:**
- The summary drops a hunter run whose host recording missed the hunter's motion. One `hpush` run at 40 read 116u against a host line where nothing moved.
- SimLab's hunter jump uses the controller's public `Jump`. The s&box whitelist rejects reflection in game code, and that compile failure silently left the editor and the clients on the old SimLab while `dotnet build` passed.

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
