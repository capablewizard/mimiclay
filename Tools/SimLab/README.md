# SimLab — networked-sim collision lab

Repeatable tests for how props collide **as each player sees it**: two player props (A, B) and a hunter (H) on three
debug clients, the editor as host. Every screen acts its part of a scenario and records the drawn poses frame by frame;
the host's own line of the same run is the truth everything is compared with.

## Pieces

| File | What it is |
|---|---|
| `Assets/scenes/Tests/SimLab.scene` | The lab: a flat floor (top at z = 0) with one **station** per scenario. Generated — don't edit by hand. |
| `Assets/materials/simlab/` | The lab's floor (gray grid, ~50u cells) and box (warm tint) materials. |
| `Tools/SimLab/make_scene.pl` | Generates the scene. Stations, markers and static props are a table at the top. `perl Tools/SimLab/make_scene.pl` |
| `code/Debug/SimLab.cs` | The game side: `Setup` (host puts the cast on the markers), `HunterPrepare`, `Run` (act + record + analyse). Scenario timings live here. |
| `code/Debug/SimLab.PropClaims.cs` | `LabMakeLoose`: turns a station's clay football into a loose prop, as a creative shot does. |
| `Tools/SimLab/lab.sh` | The harness (Git Bash): drives the editor and the three clients through `sbox-cli run_csharp`. |
| `Editor/SimLabMenu.cs` | The editor menu, *Mimiclay ▸ SimLab*: Run Full Test, Open Results Folder, Close Test Clients. |
| `Tools/SimLab/lab_sum.awk` | Summarises a run log against the host. |
| `Tools/SimLab/out/` | Logs and scratch (git-ignored). |

## Running it

**From the editor** (*Mimiclay ▸ SimLab*; each runs the harness in its own console window):

| Menu item | What it does |
|---|---|
| Run Full Test | Every scenario once (about 6 minutes), then stops. Summary: `Tools/SimLab/out/summary_latest.txt`. |
| Run Continuously | Every scenario over and over until *Stop Continuous Run*; the summary is a running one over every cycle. |
| Stop Continuous Run | Ends the continuous run after the current scenario and closes the clients. |
| Launch Observer | A fourth client, in the creative free cam (the ghost), that flies over each scenario's station as it starts. Start it any time, even mid-run. It isn't an actor and nothing it sees is recorded; fly freely with WASD, B leaves the free cam. |
| Open Results Folder | `Tools/SimLab/out/` in Explorer. |
| Close Test Clients | Closes the three test clients and the observer (after an aborted run). |

Every lab machine shows what it is doing in yellow text at the top left: the scenario, its role (A, B, H, host or
observer), the current action (walking to a marker, holding, swinging, throwing, recording) and the run's timer.
Under it, in cyan, the test conditions: the fake lag the harness set, the MEASURED sim round trip, how far the client
predicts ahead and how far behind other players' bodies are drawn (the host shows its tick rate). Trust the measured
round trip: in one editor session `net_fakelag` silently stopped applying, and `lab.sh lag` now warns when it doesn't rise.

You can also join a client of your own and fly over with **B** (creative free cam). Stations are on a grid 900u apart in x
and 1200u in y; the centre of the floor is the spawn:

| y \ x | −1350 | −450 | 450 | 1350 |
|---|---|---|---|---|
| −1800 | loose | static | push | headon |
| −600 | held | swing | throw | jump |
| 600 | lying | wall | ledge | pushwall |
| 1800 | twopush | throwplayer | climb | |
| 2700 | corner | inner | hcorner | |

**From Git Bash:**

```bash
Tools/SimLab/lab.sh full -n 2   # all of it in one go: open, cast, no lag, every scenario twice, summary, close
Tools/SimLab/lab.sh loop        # every scenario over and over until out/STOP exists (the menu's Stop writes it)
Tools/SimLab/lab.sh watch       # launch the observer client
Tools/SimLab/lab.sh open        # editor: stop play, open scenes/tests/simlab.scene, play (this is the HOST)
Tools/SimLab/lab.sh cast        # launch the 3 test clients on the current build; A and B become player props
Tools/SimLab/lab.sh lag 90      # optional: fake lag on the clients (0 ≈ 115 ms sim round trip, 40 ≈ 210, 90 ≈ 310)
Tools/SimLab/lab.sh run -n 3 push headon     # or: run -n 2 all
Tools/SimLab/lab.sh sum         # summary of the latest log
Tools/SimLab/lab.sh run -d ledge             # -d: frame dump around the pair's deepest moment, per screen
Tools/SimLab/lab.sh close
```

Clients don't hotload: after any code change run `cast` again (the editor hotloads by itself). Each run is ~16 s.
Avoid editing lab code while a run is going: the editor's hotload interrupts the host's part for a while (the continuous
run skips a scenario that fails and stops after three failures in a row).
`Tools/SimLab/capture.ps1 -ProcessId <pid> -Out <file.png>` grabs any client window (PrintWindow, works behind other windows).
`SBOX_DIR` overrides the engine folder (default `D:/SteamLibrary/steamapps/common/sbox`).

## Scenarios

| Scenario | What happens | Pair measured |
|---|---|---|
| `loose` | A walks into a loose football and pushes it | A–L |
| `static` | A presses into a traffic cone, then slides along it | A–cone |
| `push` | A pushes a standing B | A–B |
| `headon` | A and B walk into each other | A–B |
| `held` | A walks into a football H holds near the floor | A–L |
| `swing` | H swings a held football through a standing A | A–L |
| `throw` | H throws a football at a standing A | L–A (+ hit timeline) |
| `jump` | A jumps onto B | A–B (+ vertical gap) |
| `lying` | A walks into B lying on its side | A–B |
| `wall` | A walks diagonally into a wall and slides along it | A–wall |
| `ledge` | A jumps onto a 40u ledge and stops on top | A–ledge (+ vertical gap) |
| `pushwall` | A pushes a football into a wall and keeps pressing | A–L |
| `twopush` | A and B push one football from opposite sides | A–L |
| `throwplayer` | H grabs B and throws B at A | B–A (+ hit timeline) |
| `climb` | A jumps at a wall, climbs it (Space) and mantles onto the top | A–wall (+ climb) |
| `corner` | A grabs a pillar, climbs up, then sideways (D) round its OUTER corners | A–pillar (+ climb) |
| `inner` | A grabs a wall, climbs up, then sideways into an INNER corner and on along the other wall | A–wall (+ climb) |
| `hcorner` | H (a hunter) grabs a pillar, climbs up, then sideways round its outer corners | (+ climb, on H only) |

## Reading the summary

Per scenario and screen (A, B, H), each against the host's line of the same run:

- **depth+** — the deepest drawn box overlap of the pair, minus the host's. Positive = drawn deeper into each other than they really are.
- **centre+** — the host's closest centre distance minus the screen's. Positive = drawn closer; negative = drawn further apart.
- **vgap+** — landings only: how much lower the screen draws the lander on what it landed on.
- **dv / blips** — the worst frame-to-frame velocity jump of any watched body (u/s), and frames over 1500 u/s.
- **r-a / gap** — throws: the target's drawn reaction minus the projectile's drawn arrival (ms; negative = it reacts first), and the drawn box gap between them at that reaction (u; negative = still apart). The raw log also has the tick-stamped timeline per screen: release seen, copy hit, drawn reaction, drawn arrival.
- **climb** — climbing scenarios: per screen and run, who climbs, the share of frames it was climbing, how many faces it climbed and which (by the way each faces: -y, +x, ...), and the highest point reached. The host's line is the truth; a client that shows fewer faces drew its climb differently.

Walker hulls float ~19u above the feet, so absolute vertical gaps carry that offset — compare with the host, never read them alone.
A run whose host recording missed the motion (the editor ran the script late) is dropped as invalid.

## Adding a scenario

1. Add a station to the table in `make_scene.pl` (markers + any static boxes/cones; an `L` marker gets a football) and regenerate the scene.
2. In `SimLab.cs`: add it to `Scenarios`, its walk windows to `Walks`, its pair to `PairOf`, and any special acting to `Run`'s `Act` (and `HunterHolds` if H holds something).
3. Add it to `ALL` in `lab.sh`.

## Results (2026-11-07, two runs each)

At ~115 ms round trip every pair is within 5u of the host and there are no blips except two single frames in `throw`.
At ~310 ms: `push`, `static`, `wall`, `lying`, `held`, `jump`, `ledge` ≤ 1.5u; `headon` 0–10u (one frame); `pushwall`/`twopush` 6–9u; `swing` ~5u.

Known limits:
- **Remote throws at high ping**: the target predicts the hit at the right tick, but learns of the release a lead late. At ~115 ms the released prop now switches to the prediction straight away (`mimi_sim_release_predict`) and arrives within ~30u of the target when it reacts (it was ~80u). At ~310 ms the release arrives about when the prop lands, so it stays ~130–200u short.
- **Throws**: one or two frames around 1700 u/s on the target when it is knocked.
- **loose** at ~310 ms: onlookers draw the pushed football ~25u further ahead of A than the host has it (apart, not overlapping).

## Climbing results (2026-11-07)

At ~115, ~190 and ~290 ms round trip, every screen draws the climber within 2u of the host (`climb`, `corner`, `inner`):
- `climb`: grabs on the jump, climbs and mantles onto the top on every screen.
- `corner`: the host and the climber's own screen go a full lap round the pillar (-y, +x, +y, -x, -y) in 8 s.
- `inner`: turns the inner corner onto the second wall, then wraps round that wall's 30u end onto its back.
- `hcorner`: the hunter climbs 10 faces (more than two laps) in 9 s.

Onlookers' copies of a climber switch faces back and forth internally (9–19 "faces" in the report) but are drawn on the host's track.
At ~190 ms onlookers used to draw a mantle up to ~20–30u above the top for a moment. The cause was the snapshot nudge (SimWorld.NudgeRemote), which carried a climber straight up past the top at climb speed × the lead. Since the fix (SimWorld.ClimbCarry: height only, never past the surface; a mantle's carry stops at its MantleZ), every screen tops out at the host's 109: 3/3 runs at ~190 ms and 9/10 at ~290 ms, with one run at 124. Limiting the sideways carry as well caused 28000 u/s snaps in `inner` (the copy waited at the panel end while the host wrapped), so only the height is limited.
