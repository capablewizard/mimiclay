# Tutorial NPC — Plan

A clay character stands in the lobby. Editing him launches a guided version of the sculpt
editor: UI sections stay hidden until the step that teaches them, the current control is
highlighted, and each step advances when the player actually performs the action.

---

## 1. UX flow

1. **Discovery.** The NPC stands on/near the cutting mat in `lobby.scene`. Until the local
   player has completed the tutorial, he pulses a soft outline (`SdfOutlineFlash`) so new
   players notice him. Hovering shows the existing possess-toast style prompt
   (`HunterCrosshair` `paper-card possess-toast`), but with tutorial copy: **"E — Learn to sculpt"**.
2. **Enter.** Pressing E does *not* go through `PropClaims.RequestPossess`. Instead a
   local-only edit session opens on the NPC in place (see §3). The pawn freezes exactly as
   it does for face editing; an orbit rig frames the NPC (pivot pinned to his bounds centre
   — the shape never moves, per the camera-pivot rule).
3. **Guided steps.** A centred instruction card (inside the `$hud-centre-safe` corridor)
   names one action at a time. Only the UI needed so far is visible; the piece being taught
   is highlighted. The step completes when the player performs the action, with a small
   "nice!" beat between steps.
4. **Exit.** Finish, Skip, or Q/Esc at any time. The NPC reverts to his canonical sculpt,
   all EditHud flags/gates are restored, and completion is written to disk so the pulse
   stops. Re-editing him later replays the tutorial (maybe with a "free play instead?"
   choice once completed).

## 2. Why local-only, not the claim path

"Editable like other props" should mean the same *affordance* (hover + E prompt), not the
same *mechanism*. Claiming would possess the NPC as your disguise, is host-authoritative,
and means two players fight over one tutorial dummy. Instead:

- The NPC is **excluded from claims** — tag `tutorial`, checked in
  `PropClaims.IsClaimable` (near the existing scenery check, `PropClaims.cs:116/137`).
- The session is **fully local**: no `SdfNetworkSync` on the NPC, `PersistSlot = ""`.
  Proxies simply never see your tutorial edits — each player can run the tutorial
  simultaneously on their own machine. This mirrors the menu sculpt toy exactly
  (`MenuCustomise.cs:535-540` creates a session in code with a target + orbit).
- Optionally mirror `NetEditing` onto the pawn while in the tutorial so the roster shows
  the 🎨 badge; not required for v1.

## 3. New components

### `TutorialNpc : Component` (scene-placed, on the NPC object)
- NPC object = standard saved-prop composition (`SdfSculpture`, `SdfRaymarchRenderer`,
  `SdfCollider`, `SculptBounds`, `ClayBoil`) + this component + tag `tutorial`.
- Holds the **canonical sculpt** (authored brushes in the prefab; keep a
  `SculptLibrary.Entry`-style snapshot at session start for reset — reset goes through
  `SculptEditSession.Load(entry)` so it stays inside the commit/undo funnel, never a bare
  `Target.Rebuild()`).
- Owns the hover prompt + pulse outline (reuse the `RoundOutlineSystem.ApplyClaims` recipe:
  create `SdfHighlightOutline` on demand, drive only the `*Override` slots, destroy or
  null-overrides on stop; sweep in a `Dispose`/teardown so nothing bakes into the scene).
- On E: builds the orbit rig, creates `SculptEditSession` **on the NPC's GameObject**
  (`NotSaved`), sets `Target`/`OrbitCamera`, calls `SetActive(true)`, then spawns the
  `TutorialDirector`.

### `TutorialDirector : Component` (created on entry, `NotSaved`)
- Owns the ordered step list and a small state machine: `Intro → Step[i] → Praise → … → Done`.
- Per-frame `Evaluate()`: checks the current step's completion predicate against
  session/target state (polling matches the codebase style; no engine events needed for
  most steps — see §5).
- Drives three outputs each frame:
  - **EditHud gate** — which sections are visible/locked/highlighted (§4).
  - **World highlight** — outline overrides + `SdfOutlineFlash` on the NPC (or a specific
    region via the gizmo/selection) when a step says "click the nose".
  - **Card content** — step title, body, key glyphs, progress dots.
- `static TutorialDirector Active` for cheap checks from EditHud/controllers. **Must** be
  cleared by teardown *and* by the `SessionResetSystem` pattern (statics survive
  Stop→Play).
- Teardown (`OnDisabled`, and forced-exit paths): restore NPC sculpt, restore every EditHud
  flag, clear gate statics, destroy rig + session. Teardown must be idempotent and run on
  *every* exit path — the `MenuCustomise.cs:71` caveat (interrupted deactivate baked flags
  into the scene) is the exact failure to avoid.

### `TutorialCard.razor` (child panel inside EditHud, or sibling on the same ScreenPanel)
- `paper-card` styling from `_theme.scss`, `WobbleText` title, body text with `.key`
  glyph spans (same markup vocabulary as the existing `.hint-strip`), progress dots,
  **Skip** button.
- Centred in the `$hud-centre-safe` corridor so it never overlaps EditHud's edge panels.
- Skip must not delete-its-own-panel mid-press (panel-deletion latches `HasHovered` +
  press statics) — set a "close requested" flag and let the director tear down next frame.

## 4. EditHud gating + highlighting

The six `[Property] Show*` flags are too coarse (a step may want the palette visible but
the picker locked). Add a static gate the HUD consults, leaving normal behaviour untouched
when no tutorial is active:

```csharp
public static class EditHudGate
{
    public static bool Active;                       // false ⇒ HUD behaves as today
    public static Func<string, SectionState> Query;  // "tools","layers","palette","picker",
                                                     // "sliders","shapedock","undo","symmetry",
                                                     // "workshop","addchip","hintstrip"
}
public enum SectionState { Hidden, Locked, Normal, Highlight }
```

- Each gated section in `EditHud.razor` resolves its state: `Hidden` skips render,
  `Locked` renders with class `tutorial-locked` (dimmed, clicks ignored via an early-out in
  the handler — **not** a pointer-events zone, which swallows Attack1), `Highlight` adds
  `tutorial-highlight` (pulsing ring/glow, theme accent `$craft-orange-hi`).
- Fold the gate's state (a version int bumped on every change) into `BuildHash` next to
  the existing `Show*` fold (`EditHud.razor:1512`) — missed hashes fail silently.
- **Input gating**: session hotkeys (undo, delete, Slot1-7, Space, scrub keys, wireframes)
  early-out when `EditHudGate.Active` and the current step hasn't unlocked them — one check
  at the top of the hotkey block in `SculptEditSession.OnUpdate` (`:1346-1400`) keyed by
  the same section names ("undo", "shapedock", "scrub:S", …). Otherwise a player can Ctrl+Z
  or Delete their way out of sync with the script.

## 5. Step detection — how the director knows you did it

No engine change needed for most steps; the director snapshots state at step start and
polls for the delta:

| Signal | Source |
|---|---|
| camera orbited / zoomed / panned | `AltNav.Current` + `AltNav.Dragging` — count completed gestures per kind |
| selected a shape | `Session.Selected >= 0` (and which brush index) |
| moved / scaled / rotated | selected brush transform vs snapshot after a commit; or `BrushScrub.Active` = Move/Scale/Rotate observed then ended |
| recoloured | `SelectedBrush.Color` vs snapshot |
| blend / round scrub | `BrushScrub.Active` = Blend/Round observed + value delta |
| added a shape | `Target.Brushes.Count` increased (stamp ghost excluded via `PendingStamp`) |
| carve toggled | brush op vs snapshot |
| symmetry | brush mirror flags vs snapshot |
| undo / redo used | `SculptUndo` depth via `CanUndo/CanRedo` transitions |
| deleted | brush count decreased |

Use `SdfSculpture.Committed` as the "gesture finished" tick so steps advance on commit,
not mid-drag. If polling ever proves ambiguous, the fallback is a
`Session.Edited(EditKind)` event raised from the ~20 named mutators — but start without it.

## 6. Step curriculum (v1)

Core path (~3-4 min). Each step: card copy + unlocked sections + highlight + predicate.

1. **Look around** — everything hidden except the card. Orbit (LMB drag), zoom (RMB),
   pan (MMB). Done after one of each gesture.
2. **Pick a piece** — "Click his nose." World highlight on the NPC; done when
   `Selected >= 0`. Gizmo + hint-strip unlock.
3. **Push it around** — move via gizmo/W, then scale (R) and rotate (E) scrubs.
   Three sub-checks on one card.
4. **Paint it** — unlock palette (picker still locked). Apply a swatch to the selection.
   Then unlock the picker: change metal/rough or a custom colour (optional sub-step).
5. **Add a shape** — unlock Add chip + shape dock. Place a stamp; done on brush count +1.
   Teach Shift-snap in the card copy.
6. **Soften it** — S scrub (blend) on the new shape.
7. **Oops** — unlock Undo/Redo; undo once, redo once.
8. **Make it yours** — everything unlocks (layers, symmetry, carve, delete). Free-play
   beat: "give him a hat, whatever you like." Advance via a Continue button.
9. **Done** — praise card, NPC reverts (or keeps your masterpiece until you walk away —
   decide in playtest), completion saved.

Deliberately *not* in v1: splines, workshop save/load, hollow/carve as a required step,
layer reordering. Candidates for an "advanced tips" replay.

## 7. Reset + persistence

- **NPC reset**: on any exit, `Session.Load(canonicalEntry)` then normal session teardown.
  Because the session is local and the NPC has no `SdfNetworkSync`, nothing leaks to
  proxies or disk (`PersistSlot = ""`; `SavePersistSlot` never hooked to a real slot).
- **Completion flag**: tiny JSON in `FileSystem.Data` (e.g. `tutorial.json`:
  `{ completedVersion: 1 }`). Gates the pulse + prompt copy ("Learn to sculpt" vs
  "Practice sculpting"). Versioned so a future curriculum change can re-invite.

## 8. Gotchas checklist (from prior incidents)

- [ ] Everything runtime-created is `GameObjectFlags.NotSaved` + guarded for
      `Scene.IsEditor` (Pause-HUD-baked-into-10-assets incident).
- [ ] All statics (`EditHudGate`, `TutorialDirector.Active`) cleared in the
      `SessionResetSystem` end-of-play hook (statics survive Stop→Play).
- [ ] EditHud flag changes restored on *every* exit path, including forced teardowns
      (`SetActive(false)` direct calls) and scene changes.
- [ ] Gate state folded into `EditHud.BuildHash`.
- [ ] No pointer-events cursor zones; locked sections early-out in handlers instead.
- [ ] Card's Skip button defers destruction one frame (panel-deletion latch).
- [ ] NPC reset routes through the session (`Load`) — never a bare `Target.Rebuild()`
      (silently not undoable / bypasses the commit funnel).
- [ ] NPC excluded from `PropClaims`, `RoundOutlineSystem` decoy logic, and round
      carry-over (lobby-only object).
- [ ] Orbit pivot pinned to NPC bounds centre; the NPC never moves.
- [ ] Tutorial works in the self-hosted single-player lobby (launched straight into
      `lobby.scene`) *and* in a joined lobby as a client.

## 9. Milestones

- **M1 — Seams** (no tutorial logic): NPC placed + claim exclusion + E-prompt + local
  in-place session opens/closes cleanly + canonical reset + pawn freeze. Verify in a
  2-player lobby that proxies never see edits.
  **STATUS 2026-08-17: implemented** — `code/Tutorial/TutorialNpc.cs` (marker + session +
  pulse/hover presentation + restore + play-end sweep), `HunterController.ExternalSession`
  folded into `EditMode`, claim exclusion in `PropClaims.IsClaimable`, tutorial branches in
  `RoundOutlineSystem`, prompt label in `HunterCrosshair`, `TutorialNpc` added to
  `tutorialtestguy.prefab`. Hunter pawns only (hider path deferred, see open question 1).
  Needs in-editor verification, esp. that the lobby.scene prefab instance (which carries a
  full component-replace patch) inherits the new prefab component.
- **M2 — Framework**: `TutorialDirector` + `TutorialCard` + step machine with just two
  steps (camera, select). Skip/exit/teardown hardened against §8.
  **STATUS 2026-08-17: implemented** — `TutorialDirector.cs` (poll-based step machine with
  latched per-hint checks, praise beats, Skip→free-play, Finish→dialog-aware exit) +
  `TutorialCard.razor(.scss)` (persistent top-centre paper card, never unmounts — avoids
  the panel-deletion latch; hover ORed into `EditHud.PointerOverUi` so clicks can't fall
  through). Went past the two-step skeleton: five steps live (camera / select /
  move-scale-rotate / paint / add-shape) then free-play. Coarse HUD trim via the existing
  `Show*` flags (captured + restored on every exit incl. play-end sweep); EditHud
  hint-strip hidden while guiding. Fine-grained `EditHudGate` (lock vs hide, highlight,
  input gating) remains M3.
- **M3 — Gating**: `EditHudGate` + section states + input gating + highlight styling.
- **M4 — Curriculum**: all nine steps, predicates tuned, completion persistence, pulse
  outline, praise beats, copy pass.
- **M5 — Polish** (playtest-driven): pacing, free-play beat, whether the NPC keeps your
  edits, advanced-tips replay.

## 10. Open questions

1. Should hunters *and* hiders be able to start the tutorial, or hunter-pawn only?
   (Hider is already a prop; the tutorial teaches the same editor, so probably both —
   but the pawn-freeze path differs per controller.)
2. On completion, does the NPC keep your edits until you leave the lobby (fun, social)
   or revert immediately (always pristine for the next player)? Local-only either way.
3. Does the tutorial auto-offer on first-ever lobby join (modal prompt), or stay purely
   opt-in via the pulsing NPC? Recommend opt-in + pulse; measure in playtest.
