using System;
using System.IO;
using System.Runtime.CompilerServices;
using Mimiclay;

namespace Editor;

/// <summary>Asset writes and native save dialogs for the playmode sculpt HUD's Prefab column (Save / Save As).
///
/// Where "Save" goes, in priority order:
/// <list type="number">
/// <item>A destination chosen earlier this session via Save As (per sculpture).</item>
/// <item>A POSSESSED prop: the pawn's disguise came from a scene-placed prefab instance, so Save writes back
/// over that prefab (<see cref="HiderController.DisguiseSource"/>), keeping its other components intact.</item>
/// <item>A scene-placed prefab instance edited in place (the tutorial NPC and friends): apply just the sculpt
/// component to its own prefab.</item>
/// <item>Otherwise Save As — a sphere sculpted from scratch never silently overwrites anything, and in
/// particular never the disguise template prefab the pawn happens to be an instance of.</item>
/// </list>
/// "Save As" opens the file dialog in the folder of whatever Save would have written, with that file selected.</summary>
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
			var assets = Path.GetFullPath( Project.Current.GetAssetsPath() ).TrimEnd( '\\', '/' ) + Path.DirectorySeparatorChar;

			Destinations.TryGetValue( sculpt, out var destination );
			var destinationPath = destination?.Path is { } d && File.Exists( d ) ? d : null;

			// A pawn's disguise is a clone of the disguise template prefab — NEVER a save target in itself. The
			// only prefab a pawn may write to is the one its clay was possessed from.
			var hider = sculpt.Components.GetInAncestorsOrSelf<HiderController>( true );
			bool isPawn = hider.IsValid();
			var possessedPath = isPawn ? ResolveAssetPath( hider.DisguiseSource, assets ) : null;

			// A scene-placed prefab instance edited in place: its own prefab is the natural target.
			Asset source = null;
			if ( !isPawn && sculpt.GameObject.IsPrefabInstance )
			{
				var prefabRoot = sculpt.GameObject;
				while ( prefabRoot.Parent.IsValid() && prefabRoot.Parent.IsPrefabInstance )
					prefabRoot = prefabRoot.Parent;
				source = AssetSystem.FindByPath( prefabRoot.PrefabInstanceSource );
				if ( source is not null && !File.Exists( source.AbsolutePath ) )
					source = null;
			}
			bool canApplyToSource = source is not null
				&& !EditorUtility.Prefabs.IsComponentAddedToInstance( sculpt )
				&& SceneEditorSession.Resolve( sculpt ) is not null;

			var colors = sculpt.GameObject.Components.Get<PropColorRandomizer>();

			if ( !saveAs )
			{
				var target = destinationPath ?? possessedPath;
				if ( target is not null )
					return WriteOver( sculpt, target, colors, remember: true );

				if ( canApplyToSource )
				{
					// Apply just the sculpt component: this retains the source's palettes, renderer settings,
					// other components and object identities, including for nested prefab instances.
					EditorUtility.Prefabs.ApplyComponentInstanceChangesToPrefab( sculpt );
					return $"Saved {source.Name}.";
				}
			}

			// Save As — or a plain Save with nowhere sensible to go. Open where Save would have written.
			var suggested = destinationPath ?? possessedPath ?? source?.AbsolutePath;
			var dialog = new FileDialog( null );
			dialog.Title = "Save Sculpture As Prefab";
			dialog.Directory = suggested is not null ? Path.GetDirectoryName( suggested ) : Project.Current.GetAssetsPath();
			dialog.DefaultSuffix = ".prefab";
			dialog.SelectFile( suggested is not null ? Path.GetFileName( suggested ) : SdfPrefabUtility.SanitizeName( sculpt.GameObject.Name ) );
			dialog.SetFindFile();
			dialog.SetModeSave();
			dialog.SetNameFilter( "Prefab File (*.prefab)" );
			if ( !dialog.Execute() ) return "Save cancelled.";

			var path = Path.GetFullPath( dialog.SelectedFile );
			if ( !path.StartsWith( assets, StringComparison.OrdinalIgnoreCase )
				|| !string.Equals( Path.GetExtension( path ), ".prefab", StringComparison.OrdinalIgnoreCase ) )
				return "Choose a .prefab file inside this project's Assets folder.";

			// Picking the in-place instance's own asset in Save As still applies to it in place.
			if ( canApplyToSource && string.Equals( path, Path.GetFullPath( source.AbsolutePath ), StringComparison.OrdinalIgnoreCase ) )
			{
				EditorUtility.Prefabs.ApplyComponentInstanceChangesToPrefab( sculpt );
				Destinations.Remove( sculpt );
				return $"Saved {source.Name}.";
			}

			return WriteOver( sculpt, path, colors, remember: true );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Sculpt Prefab Save] {e}" );
			return "Prefab save failed. See the editor log.";
		}
	}

	/// <summary>Write the sculpture to <paramref name="path"/>. An existing prefab is used as its own template
	/// (brushes swapped in, every other component and identity kept); a new path is stamped from the disguise
	/// template. Remembers the path so the next plain Save goes straight there.</summary>
	static string WriteOver( SdfSculpture sculpt, string path, PropColorRandomizer colors, bool remember )
	{
		bool updateExisting = File.Exists( path );
		if ( !SdfPrefabUtility.Export( Path.GetFileNameWithoutExtension( path ), sculpt.Brushes,
			sculpt.Resolution, sculpt.FlipFaces, outputPath: path, colors: colors, updateExisting: updateExisting ) )
			return "Prefab save failed. See the editor log.";

		if ( remember )
			Destinations.GetValue( sculpt, _ => new Destination() ).Path = path;

		return $"Saved {Path.GetFileName( path )}.";
	}

	/// <summary>Assets-relative prefab path (as <c>GameObject.PrefabInstanceSource</c> reports it) → absolute
	/// path, or null if it's empty, escapes the assets folder or the file is gone.</summary>
	static string ResolveAssetPath( string relative, string assetsRoot )
	{
		if ( string.IsNullOrWhiteSpace( relative ) )
			return null;

		var full = Path.GetFullPath( Path.Combine( assetsRoot, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );
		if ( !full.StartsWith( assetsRoot, StringComparison.OrdinalIgnoreCase ) || !File.Exists( full ) )
			return null;

		return full;
	}
}
