using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// One switch for every <see cref="PropColorRandomizer"/> in the scene: roll them all to a new look, or
/// put them all back. Drop it anywhere (a scene-settings object is the natural home) and use the buttons.
///
/// Each prop still owns its own palette and its own authored snapshot — this only presses their buttons,
/// so Revert All is exactly as exact as a per-prop Revert, and a prop with an empty palette (or one whose
/// brush list changed since Detect) is skipped and named in the log rather than silently mangled.
///
/// <see cref="UseSeed"/> makes a roll reproducible: the same seed over the same scene produces the same
/// looks, which is what the networked prop-randomisation system will want (host picks a seed, every
/// machine rolls it). Props are visited in a stable order — sorted by scene path — so the seed
/// means the same thing on every machine regardless of object iteration order.
/// </summary>
[Title( "Prop Color Randomizer (Scene)" )]
[Category( "SDF" )]
[Icon( "shuffle" )]
public sealed class ScenePropColorRandomizer : Component, Component.ExecuteInEditor
{
	/// <summary>Limit the sweep to this object and its descendants. Leave empty to drive the whole
	/// scene.</summary>
	[Property] public GameObject Root { get; set; }

	/// <summary>Include randomizers on disabled objects/components. On by default: a prop toggled off in
	/// the editor is still part of the map's look, and <c>Scene.GetAllComponents</c> would skip it.</summary>
	[Property] public bool IncludeDisabled { get; set; } = true;

	/// <summary>Roll from <see cref="Seed"/> instead of the game's RNG, so the same seed always produces
	/// the same scene-wide look.</summary>
	[Property, Group( "Seed" )] public bool UseSeed { get; set; }

	/// <inheritdoc cref="UseSeed"/>
	[Property, Group( "Seed" ), ShowIf( nameof( UseSeed ), true )] public int Seed { get; set; } = 1;

	/// <summary>Roll every prop in scope. Props with no palette, or whose brushes changed since their last
	/// Detect, are skipped and listed in the console.</summary>
	[Button( "Randomise All" )]
	public void RandomiseAll()
	{
		var props = Targets();
		int done = RandomiseAll( props, UseSeed ? new Random( Seed ) : Game.Random );
		Log.Info( $"Prop Color Randomizer (Scene): randomised {done}/{props.Count} prop(s)."
			+ (UseSeed ? $" seed {Seed}" : "") );
	}

	/// <summary>Put every prop in scope back to its authored colours and materials.</summary>
	[Button( "Revert All" )]
	public void RevertAll()
	{
		int total = 0, done = 0;
		foreach ( var prop in Targets() )
		{
			total++;
			if ( prop.Revert() )
				done++;
		}
		Log.Info( $"Prop Color Randomizer (Scene): reverted {done}/{total} prop(s)." );
	}

	/// <summary>Advance to the next seed and roll it — the "give me another one" button when
	/// <see cref="UseSeed"/> is on, so you can walk through looks and note the one you liked.</summary>
	[Button( "Next Seed" ), ShowIf( nameof( UseSeed ), true )]
	public void NextSeed()
	{
		Seed++;
		RandomiseAll();
	}

	/// <summary>Re-run Detect Groups on every prop in scope, after a tolerance change or a batch of sculpt
	/// edits. Each prop keeps the palettes it can rescue (see <see cref="PropColorRandomizer.DetectGroups"/>),
	/// and reverts to its authored look first, so the fresh snapshot is never a randomised one.</summary>
	[Button( "Detect All Groups" ), Group( "Maintenance" )]
	public void DetectAll()
	{
		int n = 0;
		foreach ( var prop in Targets() )
		{
			prop.DetectGroups();
			n++;
		}
		Log.Info( $"Prop Color Randomizer (Scene): re-detected {n} prop(s)." );
	}

	/// <summary>Roll a given set of props with a caller-supplied RNG and return how many actually rolled —
	/// the entry point for the prop-randomisation system, which can hand every machine the same seeded
	/// <see cref="Random"/>. Order matters to the seed, so pass the sequence <see cref="Targets"/> built
	/// (or an equally stable one).</summary>
	public static int RandomiseAll( IEnumerable<PropColorRandomizer> props, Random rng )
	{
		int done = 0;
		foreach ( var prop in props )
			if ( prop.IsValid() && prop.Randomise( rng ) )
				done++;
		return done;
	}

	/// <summary>Every randomizer in scope, in a stable, machine-independent order (by scene path) so a
	/// seeded roll lands the same way everywhere.</summary>
	public List<PropColorRandomizer> Targets() => Find( Scene, Root, IncludeDisabled );

	/// <inheritdoc cref="Targets"/>
	public static List<PropColorRandomizer> Find( Scene scene, GameObject root = null, bool includeDisabled = true )
	{
		if ( !scene.IsValid() )
			return new List<PropColorRandomizer>();

		var mode = includeDisabled
			? FindMode.EverythingInSelfAndDescendants
			: FindMode.EnabledInSelfAndDescendants;

		var found = root.IsValid()
			? root.Components.GetAll<PropColorRandomizer>( mode )
			// Scene.GetAllComponents skips disabled ones, so sweep the object list ourselves (the same
			// lesson the round-outline system learned) and take self-only per object.
			: scene.GetAllObjects( !includeDisabled )
				.SelectMany( go => go.Components.GetAll<PropColorRandomizer>(
					includeDisabled ? FindMode.EverythingInSelf : FindMode.EnabledInSelf ) );

		return found.OrderBy( ScenePath, StringComparer.Ordinal ).ToList();
	}

	// Sibling names can repeat, so the path alone isn't unique — the sibling index disambiguates and keeps
	// the order deterministic across machines.
	static string ScenePath( PropColorRandomizer prop )
	{
		var parts = new List<string>();
		for ( var go = prop.GameObject; go.IsValid(); go = go.Parent )
			parts.Add( $"{go.Parent?.Children.IndexOf( go ) ?? 0:D5}/{go.Name}" );
		parts.Reverse();
		return string.Join( "/", parts );
	}

	[ConCmd( "mimi_props_randomise" )]
	public static void RandomiseAllCmd() => RunCmd( Game.Random, null );

	/// <summary>Seeded twin of <c>mimi_props_randomise</c> — its own command rather than an optional
	/// argument, since console commands here bind their parameters positionally.</summary>
	[ConCmd( "mimi_props_randomise_seed" )]
	public static void RandomiseAllSeededCmd( int seed ) => RunCmd( new Random( seed ), seed );

	static void RunCmd( Random rng, int? seed )
	{
		var scene = Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		var props = Find( scene );
		int done = RandomiseAll( props, rng );
		Log.Info( $"[props] randomised {done}/{props.Count} prop(s)." + (seed is { } s ? $" seed {s}" : "") );
	}

	[ConCmd( "mimi_props_revert" )]
	public static void RevertAllCmd()
	{
		var scene = Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		int total = 0, done = 0;
		foreach ( var prop in Find( scene ) )
		{
			total++;
			if ( prop.Revert() )
				done++;
		}
		Log.Info( $"[props] reverted {done}/{total} prop(s)." );
	}
}
