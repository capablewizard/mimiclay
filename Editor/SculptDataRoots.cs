using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// Every on-disk data root the game's saves can land in, as seen from the editor.
///
/// <see cref="Sandbox.FileSystem"/>.Data resolves to ONE folder per running ident, and this project has two: the
/// editor (and editor play sessions) write to <c>…/data/&lt;org&gt;/&lt;game&gt;#local</c>, while a standalone
/// build — the playtest launcher, a joined client, anything run outside the editor — writes to
/// <c>…/data/&lt;org&gt;/&lt;game&gt;</c>. Same game, same save format, two folders: a scene built in a playtest
/// session is invisible to a tool that only asks FileSystem.Data. So the editor-side tools go through here and
/// read BOTH roots straight off disk (System.IO is fine in the tools assembly — the sandbox is a runtime rule).
///
/// Saves stay identified by (root, name), never by name alone: the same name can exist in both roots with
/// different content, and quietly showing one of them would hide the other's work. See
/// [[sculpt-save-and-prefab-export]] and [[scene-save-library]].
/// </summary>
internal static class SculptDataRoots
{
	const string LocalSuffix = "#local";

	const string SculptExtension = ".sculpt";

	/// <summary>The root FileSystem.Data points at for whoever is asking (the editor, normally).</summary>
	public static string Primary
		=> Path.GetDirectoryName( Sandbox.FileSystem.Data.GetFullPath( SculptLibrary.Folder ).TrimEnd( '/', '\\' ) );

	/// <summary>Both roots, primary first, filtered to the ones that actually exist. Derived by adding/removing
	/// the <c>#local</c> suffix rather than rebuilding the path from idents, so it follows whatever
	/// FileSystem.Data resolves to today.</summary>
	public static IReadOnlyList<string> All
	{
		get
		{
			var primary = Primary;
			if ( string.IsNullOrEmpty( primary ) )
				return Array.Empty<string>();

			var sibling = primary.EndsWith( LocalSuffix, StringComparison.OrdinalIgnoreCase )
				? primary[..^LocalSuffix.Length]
				: primary + LocalSuffix;

			return new[] { primary, sibling }
				.Where( Directory.Exists )
				.ToList();
		}
	}

	/// <summary>Short human tag for a root — the folder leaf ("mimiclay#local" / "mimiclay"). Shown on rows and
	/// in logs so a duplicated name is never ambiguous; deliberately the literal folder rather than a guess at
	/// what it means.</summary>
	public static string Label( string root ) => Path.GetFileName( root?.TrimEnd( '/', '\\' ) ?? "" );

	/// <summary>One saved <c>.sculpt</c>, in a specific root.</summary>
	public readonly record struct SculptRef( string Root, string Name )
	{
		public string Path => System.IO.Path.Combine( Root, SculptLibrary.Folder, Name + SculptExtension );
		public string Label => SculptDataRoots.Label( Root );

		/// <inheritdoc cref="SculptDataRoots.Modified"/>
		public DateTime Modified => SculptDataRoots.Modified( Path );
	}

	/// <summary>One scene save (a folder of <c>.sculpt</c> files + <c>scene.json</c>), in a specific root.</summary>
	public readonly record struct SceneRef( string Root, string Name )
	{
		public string Dir => System.IO.Path.Combine( Root, SculptSceneLibrary.Folder, Name );
		public string Path => System.IO.Path.Combine( Dir, SculptSceneLibrary.SceneFileName );
		public string Label => SculptDataRoots.Label( Root );

		/// <inheritdoc cref="SculptDataRoots.Modified"/>
		/// <remarks>The save's <c>scene.json</c>, not the folder: a folder's write time also moves when an
		/// unrelated file lands in it, while scene.json is rewritten by — and only by — a save.</remarks>
		public DateTime Modified => SculptDataRoots.Modified( Path );
	}

	/// <summary>When a save was last written, as the filesystem sees it. <see cref="DateTime.MinValue"/> if the
	/// file is gone (a save deleted out from under an open window sorts to the bottom rather than throwing).
	/// This is the only date these saves have — the JSON carries no timestamp of its own.</summary>
	public static DateTime Modified( string path )
	{
		try
		{
			return File.Exists( path ) ? File.GetLastWriteTime( path ) : DateTime.MinValue;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Mimiclay] couldn't read the timestamp of '{path}' — {e.Message}" );
			return DateTime.MinValue;
		}
	}

	// ── Sculpts ───────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Every <c>.sculpt</c> across both roots, reserved slots (leading underscore) included — the
	/// caller decides whether to show them.</summary>
	public static List<SculptRef> ListSculpts()
	{
		var list = new List<SculptRef>();

		foreach ( var root in All )
		{
			var dir = Path.Combine( root, SculptLibrary.Folder );
			if ( !Directory.Exists( dir ) )
				continue;

			list.AddRange( Directory
				.EnumerateFiles( dir, "*" + SculptExtension )
				.Select( f => new SculptRef( root, Path.GetFileNameWithoutExtension( f ) ) )
				.OrderBy( r => r.Name, StringComparer.OrdinalIgnoreCase ) );
		}

		return list;
	}

	/// <summary>The first save with this name, primary root first — for the console commands, which take a bare
	/// name. Null if neither root has it.</summary>
	public static SculptRef? FindSculpt( string name )
	{
		foreach ( var root in All )
		{
			var candidate = new SculptRef( root, SculptLibrary.Sanitize( name ) );
			if ( File.Exists( candidate.Path ) )
				return candidate;
		}

		return null;
	}

	/// <summary>Read and validate a save, or null (logged, never thrown) if it's missing or corrupt.</summary>
	public static SculptLibrary.Entry Read( SculptRef sculpt )
	{
		var context = $"{sculpt.Label}/{sculpt.Name}";

		var json = ReadText( sculpt.Path, context );
		if ( json is null )
			return null;

		var entry = SculptLibrary.EntryFromJson( json, context );
		if ( entry is not null )
			entry.Name ??= sculpt.Name;

		return entry;
	}

	public static bool Delete( SculptRef sculpt ) => DeleteFile( sculpt.Path );

	// ── Scenes ────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Every scene save across both roots (folders holding a <c>scene.json</c>).</summary>
	public static List<SceneRef> ListScenes()
	{
		var list = new List<SceneRef>();

		foreach ( var root in All )
		{
			var dir = Path.Combine( root, SculptSceneLibrary.Folder );
			if ( !Directory.Exists( dir ) )
				continue;

			list.AddRange( Directory
				.EnumerateDirectories( dir )
				.Select( d => new SceneRef( root, Path.GetFileName( d ) ) )
				.Where( r => File.Exists( r.Path ) )
				.OrderBy( r => r.Name, StringComparer.OrdinalIgnoreCase ) );
		}

		return list;
	}

	/// <summary>The first scene save with this name, primary root first — for the console commands.</summary>
	public static SceneRef? FindScene( string name )
	{
		foreach ( var root in All )
		{
			var candidate = new SceneRef( root, SculptLibrary.Sanitize( name ) );
			if ( File.Exists( candidate.Path ) )
				return candidate;
		}

		return null;
	}

	/// <summary>Read a scene save's placements, or null (logged) if missing or corrupt.</summary>
	public static SculptSceneLibrary.SceneSave Read( SceneRef scene )
	{
		var context = $"{scene.Label}/{scene.Name}";

		var json = ReadText( scene.Path, context );
		if ( json is null )
			return null;

		var save = SculptSceneLibrary.SceneFromJson( json, context );
		if ( save is not null )
			save.Name ??= scene.Name;

		return save;
	}

	/// <summary>Read one of a scene save's shapes by stem. The stem is re-sanitized before it touches the path —
	/// scene.json is a player-editable file, so a doctored reference can't escape the save's folder.</summary>
	public static SculptLibrary.Entry ReadSculpt( SceneRef scene, string stem )
	{
		var context = $"{scene.Label}/{scene.Name}/{stem}";
		var json = ReadText( Path.Combine( scene.Dir, SculptLibrary.Sanitize( stem ) + SculptExtension ), context );

		return json is null ? null : SculptLibrary.EntryFromJson( json, context );
	}

	/// <summary>Delete a scene save — the whole folder, sculpt files included.</summary>
	public static bool Delete( SceneRef scene )
	{
		if ( !Directory.Exists( scene.Dir ) )
			return false;

		try
		{
			Directory.Delete( scene.Dir, true );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Mimiclay] failed to delete scene save '{scene.Label}/{scene.Name}' — {e.Message}" );
			return false;
		}
	}

	// ── Shared IO ─────────────────────────────────────────────────────────────────────────────────────────

	static string ReadText( string path, string context )
	{
		if ( !File.Exists( path ) )
			return null;

		try
		{
			return File.ReadAllText( path );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Mimiclay] failed to read '{context}' — {e.Message}" );
			return null;
		}
	}

	static bool DeleteFile( string path )
	{
		if ( !File.Exists( path ) )
			return false;

		try
		{
			File.Delete( path );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Mimiclay] failed to delete '{path}' — {e.Message}" );
			return false;
		}
	}
}
