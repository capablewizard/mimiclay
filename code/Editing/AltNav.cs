namespace Mimiclay;

/// <summary>
/// Shared create-mode navigation input: the single source of truth for "is the user navigating the view and
/// with which gesture." One consumer advances it each frame via <see cref="Tick"/> (the active
/// <see cref="OrbitCameraController"/> in edit mode, or the <see cref="SculptViewController"/> in the menu); both
/// the camera/object controllers and the edit HUD read the resulting state. Centralised here so there's exactly
/// one place reading the input, owning the cursor dot, and exposing the frozen-drag anchor.
///
/// Mapping — claimed on the press edge, then latched until that button releases:
/// <list type="bullet">
/// <item><b>LMB drag</b> = orbit, <b>MMB drag</b> = pan, <b>RMB drag</b> = zoom (dolly).</item>
/// </list>
/// A bare press only claims its gesture when it lands FREE: not over the HUD, and not on anything the edit
/// layer would grab with that button — a gizmo handle or the spline line (see <see
/// cref="SculptEditSession.LmbPressIsEdit"/> / <see cref="SculptEditSession.RmbPressIsEdit"/>, asked LIVE on
/// the press edge so the answer can't go stale against component update order). A short zoom claim comes
/// back as <see cref="RmbTapped"/> — the right-click step-back gesture (deselect / drop the stamp).
///
/// A free LMB press doesn't commit immediately: it enters a short PENDING window and becomes the orbit only
/// after <see cref="TapTravelPx"/> of travel — released inside that window it was a CLICK, surfaced as
/// <see cref="LmbTapped"/>, which is what the edit session selects/deselects on (and what the stamp tool
/// places on — in add mode a click stamps, a drag orbits). So a click-drag orbits from
/// anywhere — including on top of a shape (a huge invisible carve volume must not wall off the camera) — while
/// a clean click on that same shape still picks it. Alt + the same buttons ALWAYS navigates immediately — the
/// Maya muscle-memory combo and the escape hatch while a tool owns the bare button. (The class keeps its name
/// from the original alt-driven scheme; alt is now just the force-navigate modifier.)
///
/// Static because only one local view navigates at a time (same pattern as <see cref="SculptEditSession.Current"/>).
/// </summary>
public static class AltNav
{
	public enum Gesture { None, Orbit, Dolly, Pan }

	/// <summary>A navigation input is active this frame (alt held, or a nav drag running). The HUD reads this
	/// to raise its inert shield so the cursor drift can't grab UI; the menu reads it to go inert the same way.</summary>
	public static bool Held { get; private set; }

	/// <summary>A nav-button drag is in progress. The HUD reads this to capture the mouse (clean relative
	/// delta) and draw the dot at <see cref="Anchor"/>.</summary>
	public static bool Dragging { get; private set; }

	/// <summary>Which gesture is active this frame (valid while <see cref="Dragging"/>).</summary>
	public static Gesture Current { get; private set; }

	/// <summary>This frame's drag delta (warp-free relative movement once the HUD's capture kicks in).</summary>
	public static Vector2 Delta { get; private set; }

	/// <summary>Screen-pixel position the current drag began at — where the cursor is frozen / the dot is drawn.</summary>
	public static Vector2 Anchor { get; private set; }

	/// <summary>One-frame signal: a bare LMB press released inside the pending window — a CLICK, not a drag.
	/// The edit session runs selection on this (select the hovered shape / deselect on empty space), so a
	/// press that grew into an orbit drag can never also pick. Not emitted by alt-forced orbits.</summary>
	public static bool LmbTapped { get; private set; }

	/// <summary>One-frame signal: a zoom (Dolly) claim released with (almost) no travel — a right-click TAP,
	/// not a drag. The edit layer's "step back" gesture lives on this: deselect the shape / drop the pending
	/// stamp / finish a spline chain. (Scale scrubbing moved to the R key, so the tap no longer rides the
	/// scale scrub's travel check — it rides the zoom claim's.)</summary>
	public static bool RmbTapped { get; private set; }

	/// <summary>Cursor name applied while NOT nav-dragging (e.g. the spline add-point cursor) — null = the
	/// default arrow. Set by the HUD each frame. Routed through here because Tick is the single writer of
	/// <see cref="Mouse.CursorType"/> every frame, so setting it anywhere else just gets clobbered by Tick.</summary>
	public static string HoverCursor { get; set; }

	/// <summary>Console: <c>mimiclay_nav_debug 1</c> — log every bare nav press edge with the full verdict
	/// chain (which pointer-over term or session claim refused it), so "the camera won't move" is
	/// diagnosable in one press instead of by bisection.</summary>
	[ConVar( "mimiclay_nav_debug" )]
	public static bool NavDebug { get; set; }

	const float TapTravelPx = 6f; // matches BrushScrub's tap threshold — one "that was a click" feel

	static Gesture _active;
	static bool _pending; // bare LMB press seen; waiting for TapTravelPx to decide click (tap) vs drag (orbit)
	static Vector2 _anchor;
	static float _travel;
	static bool _lmbWas, _mmbWas, _rmbWas;

	/// <summary>Advance one frame: read the nav input and update the shared state + cursor dot. Call once per frame
	/// from the single active consumer. (Idempotent-ish if double-called — it just re-reads the same input — but the
	/// invariant is one consumer per scene: the edit-mode orbit camera, or the menu's sculpt view.)</summary>
	static RealTimeSince _dbgBeat;

	public static void Tick()
	{
		// Heartbeat (mimiclay_nav_debug 1): proves Tick is RUNNING and shows whether a held button even
		// reaches the input layer — a press that logs no edge AND reads Down=false here was swallowed by
		// a hovered pointer-events surface before the game input ever saw it.
		if ( NavDebug && _dbgBeat > 1f )
		{
			_dbgBeat = 0f;
			Log.Info( $"AltNav alive: pause={PauseMenu.IsOpen} active={_active} pending={_pending} " +
				$"lmbDown={Input.Down( "Attack1" )} mmbDown={Input.Down( "CameraPan" )} rmbDown={Input.Down( "Attack2" )} " +
				$"overUi={EditHud.PointerOverUi}" );
		}

		LmbTapped = false;
		RmbTapped = false;

		// Paused → the pause menu owns the cursor; just clear our state so nothing reads it as stuck.
		if ( PauseMenu.IsOpen )
		{
			Reset();
			return;
		}

		bool alt = Input.Down( "Walk" );
		bool lmb = Input.Down( "Attack1" );
		bool mmb = Input.Down( "CameraPan" );
		bool rmb = Input.Down( "Attack2" );

		if ( _active != Gesture.None )
		{
			// Latched: the gesture holds until ITS button releases — other buttons (or alt) can't retarget it
			// mid-drag, and claim state going stale under it can't cut it short.
			bool still = _active switch
			{
				Gesture.Orbit => lmb,
				Gesture.Pan => mmb,
				Gesture.Dolly => rmb,
				_ => false,
			};
			if ( !still )
			{
				// A zoom claim that never travelled was a right-click TAP — the edit layer's step-back gesture.
				RmbTapped = _active == Gesture.Dolly && _travel < TapTravelPx;
				_active = Gesture.None;
			}
		}
		else if ( _pending )
		{
			// Click-vs-drag limbo: the LMB is down on the world but hasn't travelled enough to mean orbit yet.
			// Nothing navigates in here (Dragging stays false, the cursor stays live), so a clean click costs
			// zero camera motion. Release inside the window = the click; crossing it = the orbit, anchored at
			// the original press point.
			if ( !lmb )
			{
				_pending = false;
				LmbTapped = true;
			}
			else
			{
				_travel += Mouse.Delta.Length;
				if ( _travel >= TapTravelPx )
				{
					_pending = false;
					_active = Gesture.Orbit;
				}
			}
		}
		else
		{
			// Claim on the press edge only. A bare button navigates when the press lands free (the session's
			// claim queries run against live state, so this can't disagree with what the edit layer does with
			// the same press); alt+button always navigates, immediately. The edge requirement means a press
			// refused here never turns into a drag later by the cursor merely drifting free.
			bool overUi = EditHud.PointerOverUi;
			var session = SculptEditSession.Current is { IsEditing: true } cur ? cur : null;

			// The full verdict chain for any press edge this frame — one line tells you exactly which gate
			// refused a camera claim (mimiclay_nav_debug 1).
			if ( NavDebug && ((lmb && !_lmbWas) || (mmb && !_mmbWas) || (rmb && !_rmbWas)) )
			{
				Log.Info(
					$"AltNav press: lmb={lmb && !_lmbWas} mmb={mmb && !_mmbWas} rmb={rmb && !_rmbWas} alt={alt} | " +
					$"overUi={overUi} (palette={EditHud.PointerOverPalette} sliders={EditHud.PointerOverSliders} " +
					$"picker={EditHud.PointerOverPicker} panels={EditHud.PointerOverPanels} " +
					$"sliderPress={ClaySlider.Pressed} wheelPress={ClayColorPicker.Pressed} " +
					$"exitDialog={session?.ExitConfirmPending ?? false}) | " +
					$"lmbEdit={session?.LmbPressIsEdit()} rmbEdit={session?.RmbPressIsEdit()} " +
					$"scrub={BrushScrub.Active} session={(session.IsValid() ? "editing" : "none")}" );
			}

			if ( lmb && !_lmbWas )
			{
				if ( alt )
					Begin( Gesture.Orbit );
				else if ( !overUi && session?.LmbPressIsEdit() != true )
				{
					// Free press — but it could still be a select-click, so wait out the tap window first.
					_pending = true;
					_anchor = Mouse.Position;
					_travel = 0f;
				}
			}
			else if ( rmb && !_rmbWas && (alt || (!overUi && session?.RmbPressIsEdit() != true)) )
				Begin( Gesture.Dolly );
			else if ( mmb && !_mmbWas && (alt || !overUi) )
				Begin( Gesture.Pan ); // MMB has no edit meaning — always free off the HUD
		}

		_lmbWas = lmb;
		_mmbWas = mmb;
		_rmbWas = rmb;

		bool dragging = _active != Gesture.None;
		Held = alt || dragging;
		Dragging = dragging;
		Current = _active;
		Anchor = _anchor;
		Delta = dragging ? Mouse.Delta : default;
		_travel += Delta.Length; // active claims track travel too (the RMB tap-vs-drag check reads it)

		// Keep the cursor visible (UI mouse-state, so the button release is still delivered) and show the dot while
		// dragging. The HUD's mouse-capture (keyed off Dragging) hides the real cursor and gives the warp-free delta;
		// we never use MouseVisibility.Hidden here — that would switch to game mouse-state and drop the release.
		Mouse.Visibility = MouseVisibility.Visible;
		Mouse.CursorType = dragging ? "dot-large" : HoverCursor; // HoverCursor null = default arrow
	}

	static void Begin( Gesture g )
	{
		_active = g;
		_anchor = Mouse.Position; // freeze the anchor on the press, while the cursor is still visible and its position real
		_travel = 0f;
	}

	/// <summary>Clear the state (a consumer torn down mid-drag), so the HUD doesn't keep drawing a stale dot /
	/// holding a capture.</summary>
	public static void Reset()
	{
		Held = false;
		Dragging = false;
		Current = Gesture.None;
		Delta = default;
		LmbTapped = false;
		RmbTapped = false;
		_active = Gesture.None;
		_pending = false;
		_travel = 0f;
		_lmbWas = _mmbWas = _rmbWas = false;
	}
}
