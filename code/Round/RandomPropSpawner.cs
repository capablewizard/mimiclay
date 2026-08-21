namespace Mimiclay;

/// <summary>
/// Decorative level dressing: drop one anywhere in a map and it picks ONE prefab from <see cref="PropSpawnerBase.Prefabs"/>
/// (or spawns nothing at all, per <see cref="PropSpawnerBase.NoneChance"/>) once per session, at this exact
/// spot. All the actual behaviour (networking, ground-alignment, collision avoidance, live editor preview)
/// lives in <see cref="PropSpawnerBase"/> — this is just that base with a fixed spawn point (no offset roll)
/// and its own icon/label. See <see cref="RandomVolumeSpawner"/> for the "spawn anywhere in a region" sibling.
/// </summary>
[Title( "Random Prop Spawner" )]
[Category( "Mimiclay" )]
[Icon( "casino" )]
[EditorHandle( "materials/gizmo/randompropspawner.png" )]
public sealed class RandomPropSpawner : PropSpawnerBase
{
	protected override string Label => "Random Prop";
}
