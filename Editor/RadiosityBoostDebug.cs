using Mimiclay;

namespace Editor;

/// <summary>
/// Console commands for poking ProbeRadiosityBoost in the ACTIVE editor scene tab. Editor-side because game code
/// can't see editor scenes (Scene.All skips them, Game.ActiveScene is empty), and MCP id lookups are ambiguous when
/// two open scenes share GUIDs (CharadesZoo_art / _art2).
/// </summary>
static class RadiosityBoostDebug
{
	static IEnumerable<ProbeRadiosityBoost> All()
	{
		var scene = SceneEditorSession.Active?.Scene;
		if ( scene is null )
			return [];

		return scene.GetAllObjects( false )
			.SelectMany( go => go.Components.GetAll<ProbeRadiosityBoost>( FindMode.EverythingInSelf ) );
	}

	[ConCmd( "mimi_dbg_radiosity" )]
	static void Dump()
	{
		var all = All().ToList();
		Log.Info( $"[RadiosityBoost] editor scene '{SceneEditorSession.Active?.Scene?.Name}': {all.Count} instance(s)" );
		foreach ( var b in all )
			Log.Info( $"[RadiosityBoost] {b.DebugDescribe()}" );

		// play mode runs its own copy of the scene — dump that too, it can diverge from the editor tab
		var game = Game.ActiveScene;
		if ( game is null || game == SceneEditorSession.Active?.Scene )
			return;

		var playing = game.GetAllObjects( false )
			.SelectMany( go => go.Components.GetAll<ProbeRadiosityBoost>( FindMode.EverythingInSelf ) )
			.ToList();
		Log.Info( $"[RadiosityBoost] game scene '{game.Name}': {playing.Count} instance(s)" );
		foreach ( var b in playing )
			Log.Info( $"[RadiosityBoost] {b.DebugDescribe()}" );
	}

	/// <summary>Same as Editor → Scene → Bake Indirect Light Volumes, but scriptable from the console/MCP.</summary>
	[ConCmd( "mimi_dbg_bake_ddgi" )]
	static void BakeDdgi()
	{
		_ = IndirectLightVolume.BakeAll();
	}

	[ConCmd( "mimi_dbg_radiosity_enable" )]
	static void Enable( bool enabled )
	{
		foreach ( var b in All() )
			b.Enabled = enabled;
	}

	[ConCmd( "mimi_dbg_radiosity_level" )]
	static void Level( float level )
	{
		foreach ( var b in All() )
			b.Level = level;
	}
}
