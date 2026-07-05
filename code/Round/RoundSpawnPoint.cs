using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// A marker you drop in a map scene to say "pawns can appear here". <see cref="HunterStart"/> distinguishes the
/// hunters' return point (where they're sent at the start of the hunt) from the scattered hide spots props are
/// seeded at. <see cref="RoundManager"/> gathers these at round start; if a scene has none it falls back to the
/// manager's own GameObject, so an unprepared scene still spawns everyone (just stacked).
/// </summary>
[Title( "Round Spawn Point" )]
[Category( "Mimiclay" )]
[Icon( "place" )]
public sealed class RoundSpawnPoint : Component
{
	/// <summary>True = a hunter spawn/return point. False = a prop hide spot.</summary>
	[Property] public bool HunterStart { get; set; }

	/// <summary>All spawn points of a given kind in the active scene. Enabled-only, so a designer can toggle spots.</summary>
	public static List<RoundSpawnPoint> AllOfKind( Scene scene, bool hunterStart )
		=> scene.GetAllComponents<RoundSpawnPoint>()
			.Where( s => s.IsValid() && s.HunterStart == hunterStart )
			.ToList();
}
