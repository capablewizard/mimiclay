namespace Mimiclay;

/// <summary>
/// Shared Maya alt-nav input: the single source of truth for "is the user holding Alt and dragging a nav button,
/// and which one." One consumer advances it each frame via <see cref="Tick"/> (the active
/// <see cref="OrbitCameraController"/> in edit mode, or the <see cref="SculptViewController"/> in the menu); both
/// the camera/object controllers and the edit HUD read the resulting state. Centralised here so there's exactly
/// one place reading the input, owning the cursor dot, and exposing the frozen-drag anchor.
///
/// Static because only one local view navigates at a time (same pattern as <see cref="SculptEditSession.Current"/>).
/// </summary>
public static class AltNav
{
	public enum Gesture { None, Orbit, Dolly, Pan }

	/// <summary>Alt held this frame (nav intent), even before a button is pressed. The HUD reads this to raise its
	/// inert shield so the cursor drift can't grab UI; the menu reads it to go inert the same way.</summary>
	public static bool Held { get; private set; }

	/// <summary>An alt + nav-button drag is in progress. The HUD reads this to capture the mouse (clean relative
	/// delta) and draw the dot at <see cref="Anchor"/>.</summary>
	public static bool Dragging { get; private set; }

	/// <summary>Which gesture is active this frame (valid while <see cref="Dragging"/>).</summary>
	public static Gesture Current { get; private set; }

	/// <summary>This frame's drag delta (warp-free relative movement once the HUD's capture kicks in).</summary>
	public static Vector2 Delta { get; private set; }

	/// <summary>Screen-pixel position the current drag began at — where the cursor is frozen / the dot is drawn.</summary>
	public static Vector2 Anchor { get; private set; }

	/// <summary>Cursor name applied while NOT alt-dragging (e.g. the spline add-point cursor) — null = the
	/// default arrow. Set by the HUD each frame. Routed through here because Tick is the single writer of
	/// <see cref="Mouse.CursorType"/> every frame, so setting it anywhere else just gets clobbered by Tick.</summary>
	public static string HoverCursor { get; set; }

	static bool _was;
	static Vector2 _anchor;

	/// <summary>Advance one frame: read alt-nav input and update the shared state + cursor dot. Call once per frame
	/// from the single active consumer. (Idempotent-ish if double-called — it just re-reads the same input — but the
	/// invariant is one consumer per scene: the edit-mode orbit camera, or the menu's sculpt view.)</summary>
	public static void Tick()
	{
		// Paused → the pause menu owns the cursor; just clear our state so nothing reads it as stuck.
		if ( PauseMenu.IsOpen )
		{
			Reset();
			return;
		}

		bool alt = Input.Down( "Walk" );
		bool orbit = alt && Input.Down( "Attack1" );
		bool dolly = alt && Input.Down( "Attack2" );
		bool pan = alt && Input.Down( "CameraPan" );
		bool dragging = orbit || dolly || pan;

		// Freeze the anchor on the frame the drag begins, while the cursor is still visible and its position real.
		if ( dragging && !_was )
			_anchor = Mouse.Position;
		_was = dragging;

		Held = alt;
		Dragging = dragging;
		Anchor = _anchor;
		Current = orbit ? Gesture.Orbit : dolly ? Gesture.Dolly : pan ? Gesture.Pan : Gesture.None;
		Delta = dragging ? Mouse.Delta : default;

		// Keep the cursor visible (UI mouse-state, so the button release is still delivered) and show the dot while
		// dragging. The HUD's mouse-capture (keyed off Dragging) hides the real cursor and gives the warp-free delta;
		// we never use MouseVisibility.Hidden here — that would switch to game mouse-state and drop the release.
		Mouse.Visibility = MouseVisibility.Visible;
		Mouse.CursorType = dragging ? "dot-large" : HoverCursor; // HoverCursor null = default arrow
	}

	/// <summary>Clear the state (a consumer torn down mid-drag), so the HUD doesn't keep drawing a stale dot /
	/// holding a capture.</summary>
	public static void Reset()
	{
		Held = false;
		Dragging = false;
		Current = Gesture.None;
		Delta = default;
		_was = false;
	}
}
