using System;
using System.IO;
using System.Runtime.CompilerServices;
using Mimiclay;

namespace Editor;

/// <summary>Asset writes and native save dialogs for the playmode sculpt HUD.</summary>
public static class SculptPrefabSave
{
	sealed class Destination
	{
		public string Path;
	}

	// Save As changes this sculpt's destination without replacing the object being edited in playmode.
	static readonly ConditionalWeakTable<SdfSculpture, Destination> Destinations = new();

	[EditorEvent.Frame]
	static void WireHandler() => SdfSculpture.SavePrefabHandler = Save;

	static string Save( SdfSculpture sculpt, bool saveAs )
	{
		if ( !Game.IsEditor || !sculpt.IsValid() || sculpt.Brushes is not { Count: > 0 } )
			return "Nothing to save.";

		try
		{
			Destinations.TryGetValue( sculpt, out var destination );
			var prefabRoot = sculpt.GameObject;
			while ( prefabRoot.Parent.IsValid() && prefabRoot.Parent.IsPrefabInstance )
				prefabRoot = prefabRoot.Parent;
			var source = prefabRoot.IsPrefabInstance ? AssetSystem.FindByPath( prefabRoot.PrefabInstanceSource ) : null;

			if ( !saveAs && destination is null && source is not null && File.Exists( source.AbsolutePath )
				&& !EditorUtility.Prefabs.IsComponentAddedToInstance( sculpt )
				&& SceneEditorSession.Resolve( sculpt ) is not null )
			{
				// Apply just the sculpt component: this retains the source's palettes, renderer settings,
				// other components and object identities, including for nested prefab instances.
				EditorUtility.Prefabs.ApplyComponentInstanceChangesToPrefab( sculpt );
				return $"Saved {source.Name}.";
			}

			string path = destination?.Path;
			bool updateExisting = !saveAs && path is not null && File.Exists( path );
			if ( saveAs || path is null || !File.Exists( path ) )
			{
				var dialog = new FileDialog( null );
				dialog.Title = "Save Sculpture As Prefab";
				dialog.Directory = path is not null ? Path.GetDirectoryName( path ) : Project.Current.GetAssetsPath();
				dialog.DefaultSuffix = ".prefab";
				dialog.SelectFile( path is not null ? Path.GetFileName( path ) : SdfPrefabUtility.SanitizeName( sculpt.GameObject.Name ) );
				dialog.SetFindFile();
				dialog.SetModeSave();
				dialog.SetNameFilter( "Prefab File (*.prefab)" );
				if ( !dialog.Execute() ) return "Save cancelled.";
				path = dialog.SelectedFile;
			}

			path = Path.GetFullPath( path );
			var assets = Path.GetFullPath( Project.Current.GetAssetsPath() ).TrimEnd( '\\', '/' ) + Path.DirectorySeparatorChar;
			if ( !path.StartsWith( assets, StringComparison.OrdinalIgnoreCase )
				|| !string.Equals( Path.GetExtension( path ), ".prefab", StringComparison.OrdinalIgnoreCase ) )
				return "Choose a .prefab file inside this project's Assets folder.";

			// Selecting the original asset in Save As still updates that asset in place.
			if ( source is not null && string.Equals( path, Path.GetFullPath( source.AbsolutePath ), StringComparison.OrdinalIgnoreCase )
				&& !EditorUtility.Prefabs.IsComponentAddedToInstance( sculpt )
				&& SceneEditorSession.Resolve( sculpt ) is not null )
			{
				EditorUtility.Prefabs.ApplyComponentInstanceChangesToPrefab( sculpt );
				Destinations.Remove( sculpt );
				return $"Saved {source.Name}.";
			}

			updateExisting |= destination is not null && File.Exists( path )
				&& string.Equals( path, destination.Path, StringComparison.OrdinalIgnoreCase );

			var colors = sculpt.GameObject.Components.Get<PropColorRandomizer>();
			if ( !SdfPrefabUtility.Export( Path.GetFileNameWithoutExtension( path ), sculpt.Brushes,
				sculpt.Resolution, sculpt.FlipFaces, outputPath: path, colors: colors, updateExisting: updateExisting ) )
				return "Prefab save failed. See the editor log.";

			Destinations.GetValue( sculpt, _ => new Destination() ).Path = path;
			return $"Saved {Path.GetFileName( path )}.";
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Sculpt Prefab Save] {e}" );
			return "Prefab save failed. See the editor log.";
		}
	}
}
