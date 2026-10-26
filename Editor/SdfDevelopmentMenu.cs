using Sandbox;

namespace Editor;

public static class SdfDevelopmentMenu
{
	[Menu( "Editor", "Mimiclay/SDF Development Scene", "science" )]
	public static void Open()
	{
		if ( Game.IsPlaying )
		{
			Log.Warning( "Stop the current play session before opening the SDF development scene." );
			return;
		}
		EditorScene.OpenScene( SceneFile.Load( "scenes/sdf_development.scene" ) );
	}
}
