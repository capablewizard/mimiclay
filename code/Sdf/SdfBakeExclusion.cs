using Sandbox;

namespace Mimiclay;

/// <summary>
/// Keeps SDF renderers out of DDGI bakes. The engine's probe-capture camera (DDGIProbeUpdaterCubemapper) and its
/// probe-relocation traces skip scene objects carrying any of the volume's <see cref="IndirectLightVolume.RenderExcludeTags"/>;
/// <see cref="SdfRaymarchRenderer"/> tags its scene objects with <see cref="SdfRaymarchRenderer.BakeExcludeTag"/>, and this
/// makes sure every volume in the scene lists that tag — including ones added later or loaded from old scenes.
/// Runs in the editor too (GameObjectSystems tick there), which is where bakes happen; the tag then saves with the scene.
/// </summary>
public sealed class SdfBakeExclusion : GameObjectSystem
{
	RealTimeSince _sinceCheck;

	public SdfBakeExclusion( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Tick, "SdfBakeExclusion" );
	}

	void Tick()
	{
		if ( Scene is null || _sinceCheck < 1f )
			return;
		_sinceCheck = 0;

		foreach ( var volume in Scene.GetAllComponents<IndirectLightVolume>() )
		{
			var tags = volume.RenderExcludeTags;
			if ( tags is null )
				volume.RenderExcludeTags = tags = new TagSet();

			if ( !tags.Has( SdfRaymarchRenderer.BakeExcludeTag ) )
				tags.Add( SdfRaymarchRenderer.BakeExcludeTag );
		}
	}
}
