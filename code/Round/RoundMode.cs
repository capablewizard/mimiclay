namespace Mimiclay;

/// <summary>
/// How a single prop-hunt round plays out — a rules variant chosen per round, distinct from the session's
/// <see cref="GameModeKind"/> (Prop Hunt vs. Creative). The setup screen picks one of these before starting.
/// </summary>
public enum RoundMode
{
	/// <summary>Caught props respawn as hunters — the hunter pool grows until every prop is found.</summary>
	Infection,
	/// <summary>Fixed sides for the whole round — caught props are out, not converted to hunters.</summary>
	Teams,
}

/// <summary>Display metadata for a <see cref="RoundMode"/> — its picker label and a one-line description. Centralised
/// here (like <see cref="GameModes"/>) so the setup UI and any future round logic agree on names + blurbs.</summary>
public readonly record struct RoundModeInfo( RoundMode Kind, string Label, string Description );

/// <summary>The catalogue of selectable round modes. Add a mode here (enum value + entry) and it appears in setup.</summary>
public static class RoundModes
{
	public static readonly RoundModeInfo[] All =
	{
		new( RoundMode.Infection, "Infection",
			"Caught props respawn as hunters — the hunters grow until everyone's found." ),
		new( RoundMode.Teams, "Teams",
			"Fixed sides for the whole round — caught props are out, not turned into hunters." ),
	};

	public static RoundModeInfo Get( RoundMode kind )
	{
		foreach ( var m in All )
			if ( m.Kind == kind )
				return m;
		return All[0];
	}
}
