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
///
/// Saves are addressed as a <see cref="SculptDataRoots.SceneRef"/> (root + name), because the game writes to a
/// different data root than the editor — see <see cref="SculptDataRoots"/>.
/// </summary>
public static class SdfSceneUtility
{
	static string[] OutputDirFor( string sceneName )
		=> new[] { "prefabs", "saved", "scenes", SdfPrefabUtility.SanitizeName( sceneName ) };

	/// <summary>Export every unique sculpt in a scene save to a prefab (the Prefab button). Re-running
	/// refreshes the prefabs in place — placements in an already-populated editor scene keep pointing at them.</summary>
	internal static bool ExportPrefabs( SculptDataRoots.SceneRef scene )
	{
		var save = SculptDataRoots.Read( scene );
		if ( save is null )
		{
			Log.Warning( $"[SDF Scene] no scene save '{scene.Label}/{scene.Name}'." );
			return false;
		}

		var prefabs = ExportAssets( scene, save );
		Log.Info( $"[SDF Scene] exported {prefabs.Count} prefab(s) for '{scene.Name}' -> {string.Join( '/', OutputDirFor( scene.Name ) )}/" );
		return prefabs.Count > 0;
	}

	/// <summary>Replay a scene save into the open editor scene (the Scene button): make sure its prefabs exist
	/// (same export as <see cref="ExportPrefabs"/>), then instantiate one per placement at the saved world
	/// transform, all under a parent GameObject named after the save — one undo step.</summary>
	internal static bool AddToScene( SculptDataRoots.SceneRef scene )
	{
		var save = SculptDataRoots.Read( scene );
		if ( save is null )
		{
			Log.Warning( $"[SDF Scene] no scene save '{scene.Label}/{scene.Name}'." );
			return false;
		}

		var session = SceneEditorSession.Active;
		if ( session is null )
		{
			Log.Warning( "[Mimiclay] open a scene first, then add a saved scene to it." );
			return false;
		}

		var prefabs = ExportAssets( scene, save );
		if ( prefabs.Count == 0 )
		{
			Log.Warning( $"[SDF Scene] couldn't prepare any prefabs for '{scene.Name}'." );
			return false;
		}

		int placed = 0;

		using var scope = SceneEditorSession.Scope();
		using ( session.UndoScope( $"Add Sculpt Scene '{scene.Name}'" ).WithGameObjectCreations().Push() )
		{
			// One parent at the origin — placements are world transforms, so parenting doesn't move anything,
			// and the whole build can be selected/shifted/deleted as a unit.
			var parent = new GameObject( true, scene.Name );

			foreach ( var p in save.Props )
			{
				if ( !prefabs.TryGetValue( p.Sculpt, out var prefab ) )
					continue; // its sculpt file was missing/corrupt — already warned in ExportAssets

				// The placement lives on a plain WRAPPER GameObject, never on the prefab instance inside it. An
				// instance's own transform is an override the engine keeps in a patch it recomputes from the prefab
				// FILE, and the editor re-deserializes every instance of a prefab whose file changed (ResourceLibrary
				// event → EditorScene.UpdatePrefabInstancesInScene → UpdateGameObjectFromPrefab). We rewrote these
				// very .prefab files moments ago and they compile asynchronously, so that reload always lands just
				// after we place — reverting each prop to the prefab's own origin and stacking the whole build at
				// 0,0,0. Refreshing the patch by hand is no defence either: RefreshPatch bails out while the prefab
				// is still an uncompiled promise, which is exactly when we're placing. A wrapper is nobody's prefab
				// instance, so nothing can revert it, and the instance underneath stays linked to its asset.
				var holder = new GameObject( true, p.Sculpt );
				holder.SetParent( parent );
				holder.LocalPosition = p.Position;
				holder.LocalRotation = p.Rotation;
				holder.LocalScale = p.Scale;

				var go = SceneUtility.GetPrefabScene( prefab )?.Clone();
				if ( !go.IsValid() )
				{
					holder.Destroy();
					continue;
				}

				// keepWorldPosition: false — leave the clone's local transform exactly as the prefab authored it
				// (identity for an exported prop), so the instance carries NO override to lose in the first place.
				go.SetParent( holder, false );
				placed++;
			}

			session.Selection.Set( parent );
		}

		Log.Info( $"[SDF Scene] placed {placed}/{save.Props.Count} prop(s) from '{scene.Name}' into the open scene." );
		return placed > 0;
	}

	/// <summary>Name-only entry point for the console commands: takes the first save with this name, primary
	/// data root first.</summary>
	public static bool ExportPrefabs( string sceneName )
	{
		var scene = SculptDataRoots.FindScene( sceneName );
		if ( scene is null )
		{
			Log.Warning( $"[SDF Scene] no scene save '{sceneName}' in any data root." );
			return false;
		}

		return ExportPrefabs( scene.Value );
	}

	/// <inheritdoc cref="ExportPrefabs(string)"/>
	public static bool AddToScene( string sceneName )
	{
		var scene = SculptDataRoots.FindScene( sceneName );
		if ( scene is null )
		{
			Log.Warning( $"[SDF Scene] no scene save '{sceneName}' in any data root." );
			return false;
		}

		return AddToScene( scene.Value );
	}

	// Export every sculpt the save references (unique stems) into the save's own prefab folder; the map is
	// what AddToScene instantiates from. Missing/corrupt sculpt files are warned about and skipped — the rest
	// of the scene still comes through. The folder is keyed on the save NAME alone, so two same-named saves in
	// different data roots share one set of prefabs (last export wins) — the row's root tag is what tells them
	// apart in the window.
	static Dictionary<string, PrefabFile> ExportAssets( SculptDataRoots.SceneRef scene, SculptSceneLibrary.SceneSave save )
	{
		var dir = OutputDirFor( scene.Name );
		var prefabs = new Dictionary<string, PrefabFile>();

		foreach ( var stem in save.Props.Select( p => p.Sculpt ).Distinct() )
		{
			var entry = SculptDataRoots.ReadSculpt( scene, stem );
			if ( entry is null )
			{
				Log.Warning( $"[SDF Scene] '{scene.Name}' references sculpt '{stem}' but its file is missing or unreadable — skipping it." );
				continue;
			}

			var prefab = SdfPrefabUtility.ExportAsset( stem, entry.Brushes, entry.Resolution, entry.FlipFaces, dir )
				?.LoadResource<PrefabFile>();
			if ( prefab is null )
			{
				Log.Warning( $"[SDF Scene] exporting '{scene.Name}/{stem}' to a prefab failed." );
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

	// Every scene save the editor can see, across both data roots: `mimi_scene_list_all`. The runtime's
	// mimi_scene_list only ever sees the root the GAME is writing to, which is the confusion this solves.
	[ConCmd( "mimi_scene_list_all" )]
	public static void ListAllCmd()
	{
		var scenes = SculptDataRoots.ListScenes();
		if ( scenes.Count == 0 )
		{
			Log.Info( $"No scene saves in any data root ({string.Join( ", ", SculptDataRoots.All )})." );
			return;
		}

		Log.Info( $"Scene saves ({scenes.Count}):" );
		foreach ( var scene in scenes )
		{
			var save = SculptDataRoots.Read( scene );
			Log.Info( save is null
				? $"  [{scene.Label}] {scene.Name} (unreadable)"
				: $"  [{scene.Label}] {scene.Name} — {save.Props.Count} prop(s), {save.Props.Select( p => p.Sculpt ).Distinct().Count()} shape(s), map {save.Map}" );
		}
	}
}
