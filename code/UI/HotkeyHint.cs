using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// One card on a <see cref="HotkeyBar"/>: the glyph on its key-cap (<see cref="Key"/> — also the card's identity, so it
/// must be unique within a set), the action it does, and what PRESSES it, for the punch + click: input
/// <see cref="Actions"/> (any of them), and/or a raw keyboard <see cref="RawKey"/> for binds that bypass the action table
/// (the lobby's N / G, the free-cam keys, the edit mode's scrub keys). Neither = the card never punches.
/// </summary>
/// <param name="SwapGate">The key only means this card while <see cref="PawnSwapKeys"/> lets it through (R/E are the
/// grab's Scale/Rotate while holding a prop, the edit row's scrubs mid-edit…) — so it doesn't punch for a press that
/// did something else.</param>
/// <param name="Hot">Highlighted (orange) — a state the player should act on now ("Release: close loop").</param>
/// <param name="Hold">Clickable bars: the raw key the card HOLDS while it's pressed (VirtualInput) — press and drag the
/// "R Scale" card to scale, exactly as holding R and moving the mouse.</param>
/// <param name="Nav">Clickable bars: the camera gesture the card drives while pressed (AltNav.Forced) — drag "Orbit".</param>
/// <param name="Wheel">Clickable bars: dragging the card up / down scrolls (VirtualInput.WheelY) — "Depth".</param>
/// <param name="Click">Clickable bars: run once when the card is pressed — Tab wireframe, Q done.</param>
public readonly record struct HotkeyHint( string Key, string Label, string[] Actions = null, string RawKey = null,
	bool SwapGate = false, bool Hot = false,
	string Hold = null, AltNav.Gesture Nav = AltNav.Gesture.None, bool Wheel = false, System.Action Click = null );

/// <summary>Trigger arrays for <see cref="HotkeyHint.Actions"/>, cached: a bar walks its hints every frame.</summary>
public static class HotkeyActs
{
	public static readonly string[] Move = { "Forward", "Backward", "Left", "Right" };
	public static readonly string[] Slots1To7 = { "Slot1", "Slot2", "Slot3", "Slot4", "Slot5", "Slot6", "Slot7" };

	static readonly Dictionary<string, string[]> _cache = new();

	/// <summary>A one-action trigger array, cached per action.</summary>
	public static string[] Act( string action )
	{
		if ( !_cache.TryGetValue( action, out var arr ) )
			_cache[action] = arr = new[] { action };
		return arr;
	}
}
