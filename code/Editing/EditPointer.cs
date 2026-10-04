namespace Mimiclay;

/// <summary>
/// Where the edit tools' pick ray comes from. Every pick, hover, gizmo hit-test and stamp placement reads
/// <see cref="Position"/> instead of <see cref="Mouse.Position"/>, so one switch moves the whole edit system
/// between the ways it can be driven:
/// <list type="bullet">
/// <item><b>Free cursor</b> (the default — the orbit-camera sessions): the pointer is the OS cursor.</item>
/// <item><b>Locked</b> (a first-person session — see <see cref="SculptEditSession.FirstPerson"/>): the cursor is
/// hidden and locked for mouse-look, so the pointer is the screen centre — the crosshair. Aiming IS pointing.</item>
/// <item><b>Locked + <see cref="CursorFree"/></b> (alt held in that session): the host pawn freezes mouse-look
/// and shows the OS cursor, and the pointer is that cursor again — so the gizmo, picks and the HUD all work
/// exactly like the orbit sessions', against a still view. Alt is the "cursor key" there, not the camera
/// modifier (see <see cref="AltNav.NavModifierHeld"/>).</item>
/// </list>
/// Static because one local session drives the pointer at a time (the same shape as <see cref="AltNav"/>);
/// the session owning the screen sets Locked on activate and clears it on exit, its host pawn drives CursorFree.
/// </summary>
public static class EditPointer
{
	/// <summary>True while a first-person session owns the screen: the pointer is the crosshair, not the cursor.</summary>
	public static bool Locked { get; set; }

	/// <summary>Within a locked session: the host pawn has freed the OS cursor (alt held, view frozen), so the
	/// pointer follows the cursor instead of sitting at the centre.</summary>
	public static bool CursorFree { get; set; }

	/// <summary>The screen-pixel position every edit pick should trace from this frame.</summary>
	public static Vector2 Position => Locked && !CursorFree ? Screen.Size * 0.5f : Mouse.Position;
}
