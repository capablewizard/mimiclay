using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Saves a creative session's placed props as a SCENE — the group-build workflow: friends join a map with
/// spawn-props off, populate it together in creative mode, then anyone runs <c>mimi_scene_save &lt;name&gt;</c>
/// (repeatedly, if they want checkpoints — same name overwrites). A save is a folder under
/// <c>{data}/scenes/&lt;name&gt;/</c> holding one <c>.sculpt</c> file per UNIQUE shape (the same
/// <see cref="SculptLibrary.Entry"/> format the sculpt library uses, so all the existing tooling reads them)
/// plus a <c>scene.json</c> that places them: each placement references a sculpt file by stem with the world
/// position/rotation/scale it stood at. Identical clones — a claimed prop released in ten places — dedup to one
/// sculpt file and ten placements.
///
/// This is the runtime half. The editor half (<c>SavedSculptsWindow</c>'s Scenes tab / <c>SdfSceneUtility</c>)
/// promotes a save into project assets: prefabs for every sculpt, optionally instantiated into the open scene
/// at their saved transforms — turning a co-op creative session into a real map. See
/// [[sculpt-save-and-prefab-export]] for the one-sculpt version of this bridge.
/// </summary>
public static class SculptSceneLibrary
{
	/// <summary>Folder (under the game's local data dir) where scene saves live — one subfolder per save.</summary>
	public const string Folder = "scenes";

	/// <summary>The placements file inside each save folder.</summary>
	public const string SceneFileName = "scene.json";

	const string SculptExtension = ".sculpt";

	const int CurrentVersion = 1;

	/// <summary>One placed prop: which sculpt file (stem, no extension — always inside this save's own folder)
	/// and the world transform its sculpture stood at. The transform is the SCULPTURE's, not the pawn root's:
	/// that's exactly the transform to hand a prefab whose SdfSculpture sits on the root object.</summary>
	public sealed class Placement
	{
		public string Sculpt { get; set; }
		public Vector3 Position { get; set; }
		public Rotation Rotation { get; set; }
		public Vector3 Scale { get; set; } = Vector3.One;
	}

	/// <summary>The scene.json record: where this was built and every placement.</summary>
	public sealed class SceneSave
	{
		/// <summary>Format version, so an old save can be migrated rather than silently mis-read.</summary>
		public int Version { get; set; } = CurrentVersion;

		/// <summary>Display name as typed (the folder name is a sanitized form of this).</summary>
		public string Name { get; set; }

		/// <summary>The map scene this was built on — informational, so the editor knows which scene to open
		/// before replaying the placements into it.</summary>
		public string Map { get; set; }

		public List<Placement> Props { get; set; } = new();
	}

	/// <summary>Snapshot the scene's placed props and write them as a named save. Overwrites a same-named save
	/// wholesale (the folder is replaced, so a shrunken re-save can't leave stale sculpt files behind).
	/// Returns false (and logs why) if there's nothing to save or the write fails.</summary>
	public static bool Save( string name, Scene scene )
	{
		if ( string.IsNullOrWhiteSpace( name ) || scene is null )
			return false;

		var (save, sculpts) = Capture( name, scene );
		if ( save.Props.Count == 0 )
		{
			Log.Warning( "SculptSceneLibrary: nothing to save — no released props in the scene (press P to release the one you're wearing)." );
			return false;
		}

		var dir = DirFor( name );
		try
		{
			if ( FileSystem.Data.DirectoryExists( dir ) )
				FileSystem.Data.DeleteDirectory( dir, true );
			FileSystem.Data.CreateDirectory( dir );

			foreach ( var (stem, entry) in sculpts )
				FileSystem.Data.WriteAllText( $"{dir}/{stem}{SculptExtension}", Json.Serialize( entry ) );

			FileSystem.Data.WriteAllText( $"{dir}/{SceneFileName}", Json.Serialize( save ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptSceneLibrary: failed to save '{name}' — {e.Message}" );
			return false;
		}

		Log.Info( $"SculptSceneLibrary: saved scene \"{name}\" — {save.Props.Count} prop(s), {sculpts.Count} unique shape(s) — to \"{FullPath( name )}\"." );
		return true;
	}

	// What goes in a save: every RELEASED prop pawn's disguise. Runs identically on any machine — released
	// props are networked objects every client has — so whoever types the command gets the whole group's work.
	// Worn props are deliberately skipped wherever claims run (a worn prop is a player standing somewhere, not placed
	// furniture — and skipping them also keeps a mid-edit shape out of the save); outside creative there's no
	// release concept, so every prop pawn counts (debug-mode convenience). Scene-placed clay is NOT captured:
	// it's already part of the map asset, so saving it would duplicate it on re-import.
	static (SceneSave Save, List<(string Stem, SculptLibrary.Entry Entry)> Sculpts) Capture( string name, Scene scene )
	{
		var save = new SceneSave
		{
			Name = name,
			Map = scene.Source?.ResourcePath ?? scene.Name,
		};

		var sculpts = new List<(string Stem, SculptLibrary.Entry Entry)>();
		var stems = new Dictionary<string, string>(); // shape identity → sculpt file stem (the dedup)

		// A claim service running (creative OR the lobby) is what makes "released" mean anything — see
		// [[prop-claims-service]]. Without one there's no release concept, so every prop pawn counts.
		var claims = PropClaims.Current.IsValid();
		int worn = 0;

		foreach ( var hider in scene.GetAllComponents<HiderController>() )
		{
			if ( !hider.IsValid() )
				continue;

			var sculpture = hider.DisguiseSculpture;
			if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } )
				continue;

			if ( claims && !PropClaims.IsReleased( hider ) )
			{
				worn++;
				continue;
			}

			// Never bake a mid-edit stamp ghost into a save — it's a REAL brush in the list while the Add tool
			// is up (see [[stamp-tool-add-mode]]). Only reachable on the non-creative path (released props have
			// no session), but cheap to guard everywhere.
			var pending = SculptEditSession.PendingStamp( sculpture );
			var brushes = pending is null ? sculpture.Brushes : sculpture.Brushes.Where( b => b != pending ).ToList();
			if ( brushes.Count is 0 or > SdfBrushPacker.MaxBrushes )
				continue;

			// Dedup by the exact serialized shape: released clones of one prop share a single sculpt file.
			var key = $"{sculpture.Resolution}|{sculpture.FlipFaces}|{Json.Serialize( brushes )}";
			if ( !stems.TryGetValue( key, out var stem ) )
			{
				stem = $"prop_{stems.Count + 1:00}";
				stems[key] = stem;
				sculpts.Add( (stem, new SculptLibrary.Entry
				{
					Name = stem,
					Resolution = sculpture.Resolution,
					FlipFaces = sculpture.FlipFaces,
					Brushes = brushes,
				}) );
			}

			var t = sculpture.WorldTransform;
			save.Props.Add( new Placement
			{
				Sculpt = stem,
				Position = t.Position,
				Rotation = t.Rotation,
				Scale = t.Scale,
			} );
		}

		if ( worn > 0 )
			Log.Info( $"SculptSceneLibrary: skipped {worn} prop(s) still being worn — press P to release them into the world, then save again." );

		return (save, sculpts);
	}

	/// <summary>Read a save's scene.json, or null if it's missing or corrupt (logs, never throws).</summary>
	public static SceneSave Load( string name )
	{
		var path = $"{DirFor( name )}/{SceneFileName}";
		if ( !FileSystem.Data.FileExists( path ) )
			return null;

		string json;
		try
		{
			json = FileSystem.Data.ReadAllText( path );
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptSceneLibrary: failed to load scene '{name}' — {e.Message}" );
			return null;
		}

		var save = SceneFromJson( json, name );
		if ( save is null )
			return null;

		save.Name ??= name;
		return save;
	}

	/// <summary>Parse a serialized <see cref="SceneSave"/> with the validation <see cref="Load"/> applies — null
	/// (never a throw) on corrupt or empty data. Shared with the editor's cross-root reader, which finds the same
	/// files under a data root FileSystem.Data does not point at. <paramref name="context"/> is only for the
	/// warning log (which file was bad).</summary>
	public static SceneSave SceneFromJson( string json, string context )
	{
		try
		{
			var save = Json.Deserialize<SceneSave>( json );
			return save?.Props is { Count: > 0 } ? save : null;
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptSceneLibrary: failed to load scene '{context}' — {e.Message}" );
			return null;
		}
	}

	/// <summary>Read one of a save's sculpt files by stem (as referenced from a <see cref="Placement"/>), with
	/// the library's usual validation. The stem is re-sanitized before it touches the path — scene.json is a
	/// player-editable file, so a doctored reference can't escape the save's folder.</summary>
	public static SculptLibrary.Entry LoadSculpt( string name, string stem )
	{
		var path = $"{DirFor( name )}/{SculptLibrary.Sanitize( stem )}{SculptExtension}";
		if ( !FileSystem.Data.FileExists( path ) )
			return null;

		string json;
		try
		{
			json = FileSystem.Data.ReadAllText( path );
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptSceneLibrary: failed to read sculpt '{stem}' of scene '{name}' — {e.Message}" );
			return null;
		}

		return SculptLibrary.EntryFromJson( json, $"{name}/{stem}" );
	}

	/// <summary>True if a scene save with this name exists on disk.</summary>
	public static bool Exists( string name )
		=> FileSystem.Data.FileExists( $"{DirFor( name )}/{SceneFileName}" );

	/// <summary>Delete a scene save — the whole folder, sculpt files included.</summary>
	public static bool Delete( string name )
	{
		var dir = DirFor( name );
		if ( !FileSystem.Data.DirectoryExists( dir ) )
			return false;

		try
		{
			FileSystem.Data.DeleteDirectory( dir, true );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptSceneLibrary: failed to delete scene '{name}' — {e.Message}" );
			return false;
		}
	}

	/// <summary>All saved scene names (folder names holding a scene.json), sorted.</summary>
	public static IReadOnlyList<string> List()
	{
		if ( !FileSystem.Data.DirectoryExists( Folder ) )
			return Array.Empty<string>();

		return FileSystem.Data
			.FindDirectory( Folder )
			.Select( StripFolder )
			.Where( n => FileSystem.Data.FileExists( $"{Folder}/{n}/{SceneFileName}" ) )
			.OrderBy( n => n, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	/// <summary>The absolute on-disk folder a save with this name resolves to — for "saved to &lt;path&gt;" logs.</summary>
	public static string FullPath( string name ) => FileSystem.Data.GetFullPath( DirFor( name ) );

	static string DirFor( string name ) => $"{Folder}/{SculptLibrary.Sanitize( name )}";

	static string StripFolder( string dir )
	{
		// FindDirectory yields bare child names here, but drop any leading path defensively.
		int slash = dir.LastIndexOfAny( new[] { '/', '\\' } );
		return slash >= 0 ? dir[(slash + 1)..] : dir;
	}

	// ── Dev console commands (the whole runtime UI, for now) ──────────────────────────────────────────────

	[ConCmd( "mimi_scene_save" )]
	public static void SaveCmd( string name )
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
		{
			Log.Info( "mimi_scene_save: no active scene." );
			return;
		}

		if ( !Save( name, scene ) )
			Log.Info( $"Failed to save scene \"{name}\"." ); // Save logged the why
	}

	[ConCmd( "mimi_scene_list" )]
	public static void ListCmd()
	{
		var names = List();
		if ( names.Count == 0 )
		{
			Log.Info( "No saved scenes." );
			return;
		}

		Log.Info( $"Saved scenes ({names.Count}) in \"{FileSystem.Data.GetFullPath( Folder )}\":" );
		foreach ( var n in names )
		{
			var save = Load( n );
			Log.Info( save is null
				? $"  {n} (unreadable)"
				: $"  {n} — {save.Props.Count} prop(s), {save.Props.Select( p => p.Sculpt ).Distinct().Count()} shape(s), map {save.Map}" );
		}
	}

	[ConCmd( "mimi_scene_delete" )]
	public static void DeleteCmd( string name )
		=> Log.Info( Delete( name ) ? $"Deleted scene save '{name}'." : $"No scene save '{name}'." );
}
