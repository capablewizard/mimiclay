using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Saves and loads player-sculpted shapes to local on-disk JSON (<see cref="FileSystem.Data"/>), so a
/// disguise survives across sessions WITHOUT going through the editor's prefab/.sdfmesh asset pipeline (that
/// pipeline is authoring-time only — a running build can't write new mounted assets). A saved sculpture is
/// just its brush list plus the couple of build settings needed to reproduce it exactly. That's the same data
/// <see cref="SdfNetworkSync"/> already ships over the wire, so this is persistence, not a new format.
///
/// Files live under <c>{data}/sculpts/&lt;name&gt;.sculpt</c> (JSON). Per-machine: saves don't follow the
/// player to another PC — see the [[ in-game editor ]] save plan if cloud sync is wanted later.
/// </summary>
public static class SculptLibrary
{
	/// <summary>Folder (under the game's local data dir) where sculptures live.</summary>
	public const string Folder = "sculpts";

	/// <summary>Reserved slot the player's persistent head appearance lives under: auto-loaded onto the hunter's
	/// face (and the menu sculpt head) at spawn, saved back on edit exit — see <see cref="SculptEditSession.PersistSlot"/>.
	/// The leading underscore marks it reserved: names starting with '_' are hidden from <see cref="List"/>, so
	/// slot files never show up beside the player's named saves in a save-browser UI.</summary>
	public const string HeadSlot = "_head";

	// JSON under the hood; a custom extension lets FindFile filter cleanly and keeps these out of the way of
	// any other data files.
	const string Extension = ".sculpt";

	const int CurrentVersion = 1;

	/// <summary>On-disk shape record. Plain auto-properties so s&amp;box's <see cref="Json"/> round-trips it the
	/// same way it serializes the live brush list for networking.</summary>
	public sealed class Entry
	{
		/// <summary>Format version, so an old save can be migrated rather than silently mis-read.</summary>
		public int Version { get; set; } = CurrentVersion;

		/// <summary>Display name as the player typed it (the on-disk file name is a sanitized form of this).</summary>
		public string Name { get; set; }

		/// <summary>Mesh resolution the sculpture was authored at.</summary>
		public int Resolution { get; set; } = 32;

		/// <summary>Triangle-winding flip, carried so the reloaded shape faces the same way.</summary>
		public bool FlipFaces { get; set; }

		/// <summary>The shape itself — every brush, with all its transforms, sizes, materials and symmetry.</summary>
		public List<SdfBrush> Brushes { get; set; }
	}

	/// <summary>Snapshot a sculpture into a fresh <see cref="Entry"/> and write it under <paramref name="name"/>.
	/// Returns false (and logs) if there's nothing valid to save or the write fails.</summary>
	public static bool Save( string name, SdfSculpture sculpture )
	{
		if ( !sculpture.IsValid() )
			return false;

		return Save( new Entry
		{
			Name = name,
			Resolution = sculpture.Resolution,
			FlipFaces = sculpture.FlipFaces,
			Brushes = sculpture.Brushes,
		} );
	}

	/// <summary>Write a prepared <see cref="Entry"/> to disk. Returns false (and logs) on bad input or IO error.</summary>
	public static bool Save( Entry entry )
	{
		if ( entry?.Brushes is not { Count: > 0 } || string.IsNullOrWhiteSpace( entry.Name ) )
			return false;

		entry.Version = CurrentVersion;

		try
		{
			FileSystem.Data.CreateDirectory( Folder );
			FileSystem.Data.WriteAllText( PathFor( entry.Name ), Json.Serialize( entry ) );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptLibrary: failed to save '{entry.Name}' — {e.Message}" );
			return false;
		}
	}

	/// <summary>Read a saved sculpture by name, or null if it's missing or corrupt (a bad file never throws —
	/// it logs and returns null, so a damaged save can't tear down the caller).</summary>
	public static Entry Load( string name )
	{
		var path = PathFor( name );
		if ( !FileSystem.Data.FileExists( path ) )
			return null;

		try
		{
			var entry = Json.Deserialize<Entry>( FileSystem.Data.ReadAllText( path ) );
			if ( entry?.Brushes is not { Count: > 0 } )
				return null;

			// Saves are player-editable files — cap the brush count like SdfNetworkSync does on the wire, so a
			// hand-grown list can't blow past what the packer/raymarcher handle. Treated as corrupt, not truncated.
			if ( entry.Brushes.Count > SdfBrushPacker.MaxBrushes )
			{
				Log.Warning( $"SculptLibrary: '{name}' has {entry.Brushes.Count} brushes (cap {SdfBrushPacker.MaxBrushes}) — ignoring it." );
				return null;
			}

			entry.Name ??= name;
			return entry;
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptLibrary: failed to load '{name}' — {e.Message}" );
			return null;
		}
	}

	/// <summary>True if a save with this name exists on disk.</summary>
	public static bool Exists( string name ) => FileSystem.Data.FileExists( PathFor( name ) );

	/// <summary>Delete a saved sculpture. Returns false if it wasn't there or the delete failed.</summary>
	public static bool Delete( string name )
	{
		var path = PathFor( name );
		if ( !FileSystem.Data.FileExists( path ) )
			return false;

		try
		{
			FileSystem.Data.DeleteFile( path );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptLibrary: failed to delete '{name}' — {e.Message}" );
			return false;
		}
	}

	/// <summary>All saved sculpture names (the on-disk file stems), sorted. Empty if none have been saved.</summary>
	public static IReadOnlyList<string> List()
	{
		if ( !FileSystem.Data.DirectoryExists( Folder ) )
			return Array.Empty<string>();

		return FileSystem.Data
			.FindFile( Folder, $"*{Extension}" )
			.Select( StripExtension )
			.Where( n => !n.StartsWith( '_' ) ) // reserved slots (HeadSlot) aren't part of the named library
			.OrderBy( n => n, StringComparer.OrdinalIgnoreCase )
			.ToList();
	}

	static string PathFor( string name ) => $"{Folder}/{Sanitize( name )}{Extension}";

	/// <summary>The absolute on-disk path a save with this name resolves to — <see cref="FileSystem.Data"/>
	/// (the game's persistent data dir) plus the sculpts folder and sanitized file name. Handy for logging
	/// "saved to &lt;path&gt;" so the file's easy to find.</summary>
	public static string FullPath( string name ) => FileSystem.Data.GetFullPath( PathFor( name ) );

	static string StripExtension( string file )
	{
		// FindFile yields bare names here, but drop any leading folder defensively before trimming the ext.
		int slash = file.LastIndexOfAny( new[] { '/', '\\' } );
		if ( slash >= 0 )
			file = file[(slash + 1)..];

		return file.EndsWith( Extension, StringComparison.OrdinalIgnoreCase )
			? file[..^Extension.Length]
			: file;
	}

	// Map a free-text name to a safe file stem: keep letters/digits/space/-/_; fold anything else to '_'. Two
	// names that sanitize the same share a file (last write wins) — fine for a first-cut local library; the
	// original text is preserved inside the JSON's Name field for display.
	static string Sanitize( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return "unnamed";

		var sb = new StringBuilder( name.Length );
		foreach ( var c in name.Trim() )
			sb.Append( char.IsLetterOrDigit( c ) || c is '-' or '_' or ' ' ? c : '_' );

		var s = sb.ToString().Trim();
		return string.IsNullOrEmpty( s ) ? "unnamed" : s;
	}

	// ── Dev console commands (test the round-trip without UI) ─────────────────────────────────────────────
	// Drive the sculpture being edited right now (SculptEditSession.Current). The styled HUD buttons can call
	// the same SculptEditSession.SaveAs/Load once the in-game save UI is designed.

	[ConCmd( "mimi_sculpt_save" )]
	public static void SaveCmd( string name )
	{
		var s = SculptEditSession.Current;
		if ( s is null )
		{
			Log.Info( "mimi_sculpt_save: no active sculpt session (enter edit mode first)." );
			return;
		}

		if ( s.SaveAs( name ) )
			Log.Info( $"Saved sculpture \"{name}\" to \"{FullPath( name )}\"." );
		else
			Log.Info( $"Failed to save \"{name}\"." );
	}

	[ConCmd( "mimi_sculpt_load" )]
	public static void LoadCmd( string name )
	{
		var s = SculptEditSession.Current;
		if ( s is null )
		{
			Log.Info( "mimi_sculpt_load: no active sculpt session (enter edit mode first)." );
			return;
		}

		if ( s.Load( name ) )
			Log.Info( $"Loaded sculpture \"{name}\" from \"{FullPath( name )}\"." );
		else
			Log.Info( $"No saved sculpture \"{name}\"." );
	}

	/// <summary>Convert a prefab asset's sculpture into a <c>.sculpt</c> library save without touching the
	/// scene: reads the <see cref="SdfSculpture"/> straight off the prefab scene (the same trick
	/// <see cref="HunterGun"/> uses to read gun.prefab's brushes) and writes it under the given name — default
	/// is the prefab's file stem. Complements <see cref="SdfSculpture.SaveToLibrary"/>, which needs the
	/// sculpture placed in a scene; this works on the asset directly.</summary>
	[ConCmd( "mimi_sculpt_from_prefab" )]
	public static void FromPrefabCmd( string prefabPath, string name = null )
	{
		if ( string.IsNullOrWhiteSpace( prefabPath ) )
		{
			Log.Info( "Usage: mimi_sculpt_from_prefab <prefab path> [save name] — e.g. mimi_sculpt_from_prefab prefabs/bedroom/toycar" );
			return;
		}

		var path = prefabPath.Replace( '\\', '/' ).Trim().ToLowerInvariant();
		if ( !path.EndsWith( ".prefab", StringComparison.Ordinal ) )
			path += ".prefab";

		var file = ResourceLibrary.Get<PrefabFile>( path );
		if ( file is null )
		{
			Log.Info( $"mimi_sculpt_from_prefab: no prefab at \"{path}\" (paths are relative to the assets root, e.g. prefabs/bedroom/toycar.prefab)." );
			return;
		}

		var sculptures = SceneUtility.GetPrefabScene( file )?.GetAllComponents<SdfSculpture>().ToList();
		if ( sculptures is not { Count: > 0 } )
		{
			Log.Info( $"mimi_sculpt_from_prefab: \"{path}\" has no SdfSculpture component." );
			return;
		}

		if ( sculptures.Count > 1 )
			Log.Info( $"mimi_sculpt_from_prefab: \"{path}\" has {sculptures.Count} sculptures — converting the first (\"{sculptures[0].GameObject.Name}\")." );

		var sculpture = sculptures[0];
		if ( sculpture.Brushes is not { Count: > 0 } )
		{
			Log.Info( $"mimi_sculpt_from_prefab: \"{path}\" has an empty brush list — nothing to convert." );
			return;
		}

		// Refuse rather than write a file Load() will reject as over-cap (it treats > MaxBrushes as corrupt).
		if ( sculpture.Brushes.Count > SdfBrushPacker.MaxBrushes )
		{
			Log.Info( $"mimi_sculpt_from_prefab: \"{path}\" has {sculpture.Brushes.Count} brushes (cap {SdfBrushPacker.MaxBrushes}) — too many to convert." );
			return;
		}

		if ( string.IsNullOrWhiteSpace( name ) )
		{
			// Default save name = the prefab's file stem ("prefabs/bedroom/toycar.prefab" → "toycar").
			var stem = path[(path.LastIndexOf( '/' ) + 1)..];
			name = stem[..^".prefab".Length];
		}

		if ( Save( name, sculpture ) )
			Log.Info( $"Converted \"{path}\" → sculpture \"{name}\" at \"{FullPath( name )}\"." );
		else
			Log.Info( $"Failed to convert \"{path}\"." );
	}

	[ConCmd( "mimi_sculpt_list" )]
	public static void ListCmd()
	{
		var names = List();
		if ( names.Count == 0 )
		{
			Log.Info( "No saved sculptures." );
			return;
		}

		Log.Info( $"Saved sculptures ({names.Count}) in \"{FileSystem.Data.GetFullPath( Folder )}\":" );
		foreach ( var n in names )
			Log.Info( $"  {n}" );
	}

	[ConCmd( "mimi_sculpt_delete" )]
	public static void DeleteCmd( string name )
		=> Log.Info( Delete( name ) ? $"Deleted sculpture '{name}'." : $"No saved sculpture '{name}'." );
}
