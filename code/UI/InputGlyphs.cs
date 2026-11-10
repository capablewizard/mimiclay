namespace Mimiclay;

/// <summary>
/// Which glyph a control hint draws for a key label. Mouse buttons get the input-icon art (under Assets/UI — only
/// "UI/*" loose files ship); everything else is a key-cap chip with the label text. Shared by every .key-card
/// (the crosshair's hover prompts, the round HUD's controls bar) so a key reads the same everywhere.
/// </summary>
public static class InputGlyphs
{
	/// <summary>The mouse art for a key label ("LMB", "RMB"…), or null for a keyboard key.</summary>
	public static string MouseIcon( string key ) => key?.ToUpperInvariant() switch
	{
		"LMB" or "MOUSE1" => "UI/inputicons/mouse_left.png",
		"RMB" or "MOUSE2" => "UI/inputicons/mouse_right.png",
		"MMB" or "MOUSE3" => "UI/inputicons/mouse_scroll.png",
		"SCROLL" or "MWHEEL" => "UI/inputicons/mouse_scroll_vertical.png",
		"MOUSE" => "UI/inputicons/mouse_move.png",
		_ => null,
	};

	/// <summary>The key-cap text for an input action as bound ("SHIFT", "TAB", "R"), or <paramref name="fallback"/> when
	/// it isn't bound. Side prefixes are dropped — the engine names Shift "Left Shift", too long for a cap.</summary>
	public static string KeyFor( string action, string fallback )
	{
		var key = Sandbox.Input.GetButtonOrigin( action );
		if ( string.IsNullOrEmpty( key ) )
			return fallback;
		key = key.ToUpperInvariant();
		if ( key.StartsWith( "LEFT " ) ) key = key[5..];
		else if ( key.StartsWith( "RIGHT " ) ) key = key[6..];
		return key;
	}

	/// <summary>Inline style for a mouse glyph's background art.</summary>
	public static string IconStyle( string icon ) => $"background-image: url( /{icon} );";
}
