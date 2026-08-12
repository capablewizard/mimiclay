using System;

namespace Mimiclay;

/// <summary>
/// The GAMES the lobby's host can set the session up to play. This is the top level of a two-level taxonomy:
/// a game (this enum) owns which gameplay scene the lobby launches and which manager runs there; a game may
/// then have its own modes WITHIN it — prop hunt's <see cref="RoundMode"/> (Infection / Teams) is the first —
/// picked in the same setup dialog. The menu no longer chooses any of this: hosting just creates a session
/// and drops everyone into the lobby, where <see cref="LobbyManager.SelectedGame"/> carries the live choice.
///
/// <see cref="PropHunt"/> is the headline game (hunters vs. sculpted hiders). <see cref="Creative"/> is the
/// no-round sandbox. <see cref="Pictionary"/> is sculpt-and-guess.
/// </summary>
public enum GameModeKind
{
	PropHunt,
	Creative,
	Pictionary,
}

/// <summary>Display metadata for a <see cref="GameModeKind"/> — label, one-line blurb, icon, and the gameplay
/// scene the LOBBY launches when the host starts this game. Empty <see cref="Scene"/> means the game doesn't
/// launch a fixed scene: prop hunt resolves its map through <see cref="MapCatalog"/> instead. Centralised here
/// so the setup dialog, the server browser and the launch path all agree from one place.</summary>
public readonly record struct GameModeInfo( GameModeKind Kind, string Label, string Blurb, string Scene, string Icon );

/// <summary>The catalogue of playable games, in the order the setup dialog cycles them.</summary>
public static class GameModes
{
	public static readonly GameModeInfo[] All =
	{
		new( GameModeKind.PropHunt, "Prop Hunt", "Create a disguise to blend in and evade the hunters.",
			"", "visibility" ), // scene comes from the map picker (MapCatalog), not the catalogue
		new( GameModeKind.Pictionary, "Pictionary", "One player sculpts a secret word — everyone else guesses.",
			"scenes/pictionary.scene", "draw" ),
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
