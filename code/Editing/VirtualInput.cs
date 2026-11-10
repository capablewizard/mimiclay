using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Input the HUD can supply on the player's behalf — the hotkey bar's clickable cards (HotkeyBar): pressing the
/// "R Scale" card holds R, so the scale scrub runs exactly as if the key were held and the mouse dragged. The edit
/// layer reads raw keys through <see cref="KeyDown"/> (real key OR a card holding it) instead of
/// Input.Keyboard.Down; camera cards drive <see cref="AltNav.Forced"/>; the Depth card feeds <see cref="WheelY"/>.
///
/// Statics are lazy / auto-default: a hotload doesn't run a NEW static's initializer on an existing type.
/// </summary>
public static class VirtualInput
{
	static HashSet<string> _held;

	/// <summary>A card is holding at least one key — a scrub may start even though the pointer is over the HUD
	/// (the card is where the press landed).</summary>
	public static bool AnyHeld => _held is { Count: > 0 };

	/// <summary>Is <paramref name="key"/> down — the real key, or a card holding it?</summary>
	public static bool KeyDown( string key )
		=> Input.Keyboard.Down( key ) || (_held is not null && _held.Contains( key ));

	public static void SetHeld( string key, bool down )
	{
		_held ??= new();
		if ( down ) _held.Add( key ); else _held.Remove( key );
	}

	public static void ReleaseAll() => _held?.Clear();

	/// <summary>Scroll notches the Depth card produced this frame (drag up = wheel up = push away). Added to
	/// Input.MouseWheel.y by the wheel readers; the bar rewrites it every frame, so each value is read once.</summary>
	public static float WheelY { get; set; }
}
