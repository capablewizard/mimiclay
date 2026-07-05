using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// The list of maps the host can pick in setup, sourced from the <see cref="MapResource"/> assets in the project
/// (plus the special "Random" option). Mirrors how MiniMotors reads its maps from <see cref="ResourceLibrary"/>:
/// create/edit maps as assets, not in code. A map is referenced everywhere by its <c>ResourcePath</c>, which is
/// what gets stored in the round settings + lobby data.
/// </summary>
public static class MapCatalog
{
	/// <summary>Sentinel ident meaning "host doesn't care — roll one at launch".</summary>
	public const string RandomIdent = "random";

	/// <summary>Every selectable (non-hidden) map asset, ordered by title for a stable list.</summary>
	public static IReadOnlyList<MapResource> All
		=> ResourceLibrary.GetAll<MapResource>()
			.Where( m => m is not null && !m.Hidden )
			.OrderBy( m => m.Title )
			.ToList();

	/// <summary>Look up a map by its <c>ResourcePath</c>. <see cref="RandomIdent"/> / unknown / empty return false.</summary>
	public static bool TryGet( string ident, out MapResource map )
	{
		map = string.IsNullOrEmpty( ident ) || ident == RandomIdent
			? null
			: ResourceLibrary.Get<MapResource>( ident );
		return map is not null;
	}

	/// <summary>Resolve the host's choice to a concrete map. <see cref="RandomIdent"/> (or anything unrecognised)
	/// picks a random available map. Returns null when no map assets exist yet.</summary>
	public static MapResource Resolve( string ident )
	{
		if ( TryGet( ident, out var map ) )
			return map;

		var all = All;
		return all.Count > 0 ? all[Random.Shared.Next( all.Count )] : null;
	}
}
