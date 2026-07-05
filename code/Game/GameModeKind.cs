using System;

namespace Mimiclay;

/// <summary>
/// The game modes the host can pick in the Host Game screen. Kept deliberately small for the block-out — the
/// real round managers land later; for now this is just the menu's selection + what gets stashed in lobby data
/// so a future <c>RoundManager</c> (and the server browser) can read which mode a session is running.
///
/// <see cref="PropHunt"/> is the headline mode (hunters vs. sculpted hiders). <see cref="Creative"/> is the
/// no-round sandbox that just drops everyone into the sculpt editor.
/// </summary>
public enum GameModeKind
{
	PropHunt,
	Creative,
}

/// <summary>Display metadata for a <see cref="GameModeKind"/> — its menu label, one-line blurb, and the gameplay
/// scene to load when a host starts it. Centralised here so the Host screen, the server browser and (later) the
/// round manager all agree on names and scenes from one place.</summary>
public readonly record struct GameModeInfo( GameModeKind Kind, string Label, string Blurb, string Scene, string Icon );

/// <summary>The catalogue of selectable modes. The scene paths point at what exists today (the debug perf scene)
/// so Host → Start is testable end-to-end now; swap them for the real mode scenes as those land.</summary>
public static class GameModes
{
	public static readonly GameModeInfo[] All =
	{
		new( GameModeKind.PropHunt, "Prop Hunt", "Create a disguise to blend in and evade the hunters.",
			LobbyController.LobbyScene, "visibility" ),
		new( GameModeKind.Creative, "Creative", "A quiet sandbox — let your creativity run wild",
			"scenes/perftestdebug.scene", "brush" ),
	};

	public static GameModeInfo Get( GameModeKind kind )
	{
		foreach ( var m in All )
			if ( m.Kind == kind )
				return m;
		return All[0];
	}
}
