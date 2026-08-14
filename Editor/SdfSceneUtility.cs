using System.Collections.Generic;
using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// Editor-only bridge for <see cref="SculptSceneLibrary"/> saves — the multi-prop twin of
/// <see cref="SdfPrefabUtility"/>. A scene save (a co-op creative session's placed props, written at runtime by
/// <c>mimi_scene_save</c>) becomes project assets here: every unique sculpt in the save exports to a prefab
/// under <c>prefabs/saved/scenes/&lt;scene&gt;/</c>, and "add to scene" replays the save's placements into the
/// open editor scene — each placement an instance of its shape's prefab at the exact world transform it was
/// built at, grouped under one parent GameObject. Driven by the Scenes tab of <see cref="SavedSculptsWindow"/>.
/// </summary>
public static class SdfSceneUtility
{
	static string[] OutputDirFor( string sceneName )
		=> new[] { "prefabs", "saved", "scenes", SdfPrefabUtility.SanitizeName( sceneName ) };

	/// <summary>Export every unique sculpt in a scene save to a prefab (the Prefab button). Re-running
	/// refreshes the prefabs in place — placements in an already-populated editor scene keep pointing at them.</summary>
	public static bool ExportPrefabs( string sceneName )
	{
		var save = SculptSceneLibrary.Load( sceneName );
		if ( save is null )
		{
			Log.Warning( $"[SDF Scene] no scene save '{sceneName}'." );
			return false;
		}

		var prefabs = ExportAssets( sceneName, save );
		Log.Info( $"[SDF Scene] exported {prefabs.Count} prefab(s) for '{sceneName}' -> {string.Join( '/', OutputDirFor( sceneName ) )}/" );
		return prefabs.Count > 0;
	}

	/// <summary>Replay a scene save into the open editor scene (the Scene button): make sure its prefabs exist
	/// (same export as <see cref="ExportPrefabs"/>), then instantiate one per placement at the saved world
	/// transform, all under a parent GameObject named after the save — one undo step.</summary>
	public static bool AddToScene( string sceneName )
	{
		var save = SculptSceneLibrary.Load( sceneName );
		if ( save is null )
		{
			Log.Warning( $"[SDF Scene] no scene save '{sceneName}'." );
			return false;
		}

		var session = SceneEditorSession.Active;
		if ( session is null )
		{
			Log.Warning( "[Mimiclay] open a scene first, then add a saved scene to it." );
			return false;
		}

		var prefabs = ExportAssets( sceneName, save );
		if ( prefabs.Count == 0 )
		{
			Log.Warning( $"[SDF Scene] couldn't prepare any prefabs for '{sceneName}'." );
			return false;
		}

		int placed = 0;

		using var scope = SceneEditorSession.Scope();
		using ( session.UndoScope( $"Add Sculpt Scene '{sceneName}'" ).WithGameObjectCreations().Push() )
		{
			// One parent at the origin — placements are world transforms, so parenting doesn't move anything,
			// and the whole build can be selected/shifted/deleted as a unit.
			var parent = new GameObject( true, sceneName );

			foreach ( var p in save.Props )
			{
				if ( !prefabs.TryGetValue( p.Sculpt, out var prefab ) )
					continue; // its sculpt file was missing/corrupt — already warned in ExportAssets

				var go = SceneUtility.GetPrefabScene( prefab )?.Clone();
				if ( !go.IsValid() )
					continue;

				go.SetParent( parent );
				go.LocalPosition = p.Position;
				go.LocalRotation = p.Rotation;
				go.LocalScale = p.Scale;
				placed++;
			}

			session.Selection.Set( parent );
		}

		Log.Info( $"[SDF Scene] placed {placed}/{save.Props.Count} prop(s) from '{sceneName}' into the open scene." );
		return placed > 0;
	}

	// Export every sculpt the save references (unique stems) into the save's own prefab folder; the map is
	// what AddToScene instantiates from. Missing/corrupt sculpt files are warned about and skipped — the rest
	// of the scene still comes through.
	static Dictionary<string, PrefabFile> ExportAssets( string sceneName, SculptSceneLibrary.SceneSave save )
	{
		var dir = OutputDirFor( sceneName );
		var prefabs = new Dictionary<string, PrefabFile>();

		foreach ( var stem in save.Props.Select( p => p.Sculpt ).Distinct() )
		{
			var entry = SculptSceneLibrary.LoadSculpt( sceneName, stem );
			if ( entry is null )
			{
				Log.Warning( $"[SDF Scene] '{sceneName}' references sculpt '{stem}' but its file is missing or unreadable — skipping it." );
				continue;
			}

			var prefab = SdfPrefabUtility.ExportAsset( stem, entry.Brushes, entry.Resolution, entry.FlipFaces, dir )
				?.LoadResource<PrefabFile>();
			if ( prefab is null )
			{
				Log.Warning( $"[SDF Scene] exporting '{sceneName}/{stem}' to a prefab failed." );
				continue;
			}

			prefabs[stem] = prefab;
		}

		return prefabs;
	}

	// Editor console twin of the window's Prefab button: `mimi_scene_export <name>`.
	[ConCmd( "mimi_scene_export" )]
	public static void ExportCmd( string sceneName )
		=> Log.Info( ExportPrefabs( sceneName ) ? $"Exported scene '{sceneName}' to prefabs." : $"Export of scene '{sceneName}' failed." );
}
