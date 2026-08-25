using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor;
using Mimiclay;

namespace Editor;

/// <summary>
/// A "Mimiclay" menu on the main editor menu bar + a small tabbed window over the game's runtime saves:
///
/// <b>Sculpts</b> — every saved <c>.sculpt</c> (from <see cref="SculptLibrary"/>, i.e. the in-game saves), each
/// exportable to a <c>.prefab</c>, droppable straight into the open scene, or deletable.
///
/// <b>Scenes</b> — every scene save (from <see cref="SculptSceneLibrary"/>, a co-op creative session's placed
/// props saved with <c>mimi_scene_save</c>), with the same three moves writ large: Prefab exports all of a
/// save's sculpts to <c>prefabs/saved/scenes/&lt;name&gt;/</c>, Scene also replays the placements into the open
/// scene at their saved transforms, Delete removes the save.
///
/// Both tabs share a header: a search box (matches save names as you type) and a sort pick — newest/oldest by
/// the file's write time, or name A–Z/Z–A — remembered per tab in an editor cookie.
///
/// Editor-only (writing project assets / driving the scene needs the tools assembly). The saves themselves are
/// written at runtime by the game to <see cref="Sandbox.FileSystem"/>.Data — this is the dev-side bridge that promotes
/// them into the project. See [[sculpt-save-and-prefab-export]].
/// </summary>
public class SavedSculptsWindow : BaseWindow
{
	/// <summary>Render resolution for each row's thumbnail — the icon size the whole system targets. Kept above
	/// the display size so the downsample in <see cref="PixmapView"/> has headroom on a scaled display.</summary>
	internal const int ThumbnailSize = 256;

	/// <summary>How big those rows actually draw. Smaller than the render, deliberately: judging lighting wants
	/// detail, but a full 256px row would only fit three sculpts on screen.</summary>
	internal const int ThumbnailDisplaySize = 128;

	// The one open window (null/invalid when closed). A second "open" focuses it instead of stacking another —
	// duplicate windows would each render their own thumbnail set and drift out of sync on delete/refresh.
	// IsValid-guarded on every read: editor statics survive Stop→Play and hotload (see
	// [[editor-static-persistence]]), so this can hold a destroyed widget.
	static SavedSculptsWindow _instance;

	[Menu( "Editor", "Mimiclay/Saved Sculpts…", "view_in_ar" )]
	public static void OpenWindow()
	{
		if ( _instance.IsValid() )
		{
			_instance.Show(); // un-minimize if needed
			_instance.Focus();
			return;
		}

		_instance = new SavedSculptsWindow();
		_instance.Show();
	}

	[Menu( "Editor", "Mimiclay/Open Sculpts Folder", "folder_open" )]
	public static void OpenFolder()
		=> OpenDataFolder( SculptLibrary.Folder );

	/// <summary>Open a folder under the game's data dir in Explorer, creating it first (it only exists once
	/// something's been saved there).</summary>
	internal static void OpenDataFolder( string relative )
	{
		// With both data roots live (the editor writes to one, a standalone build to the other — see
		// SculptDataRoots) there is no single right folder to open, so show the parent that holds both.
		var roots = SculptDataRoots.All;
		if ( roots.Count > 1 )
		{
			EditorUtility.OpenFolder( Path.GetDirectoryName( roots[0] ) );
			return;
		}

		var dir = Sandbox.FileSystem.Data.GetFullPath( relative );
		Directory.CreateDirectory( dir );
		EditorUtility.OpenFolder( dir );
	}

	public SavedSculptsWindow()
	{
		WindowTitle = "Saved Sculpts";
		SetWindowIcon( "view_in_ar" );
		Size = new Vector2( 560, 560 );

		Layout = Layout.Column();

		var tabs = new TabWidget( this );
		tabs.AddPage( "Sculpts", "view_in_ar", new SavedSculptsPage() );
		tabs.AddPage( "Scenes", "map", new SavedScenesPage() );
		tabs.StateCookie = "mimiclay.savedsculpts";
		Layout.Add( tabs, 1 );
	}
}

/// <summary>How a tab's list is ordered. Both halves of each pair are offered outright rather than a mode plus
/// a direction toggle — one control, and the list can't end up in a state the control doesn't name.</summary>
enum SavedSort
{
	NameAsc,
	NameDesc,
	Newest,
	Oldest,
}

/// <summary>Shared skeleton for the window's two tabs: a header row (title + search + sort + Open Folder +
/// Refresh) over a scrolling list, and the thumbnail-label-buttons row shape both fill it with.</summary>
abstract class SavedListPage : Widget
{
	protected Layout ListLayout { get; private set; }

	/// <summary>The header's search text, matched case-insensitively against save names. Empty = show all.</summary>
	protected string Filter { get; private set; } = "";

	/// <summary>The header's sort pick. Defaults to newest-first — the save you just made in a playtest is the
	/// one you came here to export.</summary>
	protected SavedSort Sort { get; private set; } = SavedSort.Newest;

	protected SavedListPage( string title, string folder, string cookie ) : base( null )
	{
		Layout = Layout.Column();
		Layout.Spacing = 8;

		var header = Layout.Add( Layout.Row() );
		header.Spacing = 4;
		header.Add( new Label( title ) );

		// Filters the rows already in hand — the saves are read off disk on Rebuild either way, and there are
		// tens of them at most, so this never needs to be cleverer than a re-list.
		var search = header.Add( new LineEdit( this ) { PlaceholderText = "⌕  Search", MinimumWidth = 160 }, 1 );
		search.TextChanged += text =>
		{
			Filter = text ?? "";
			Rebuild();
		};

		var sort = header.Add( new ComboBox( this ) { MinimumWidth = 130, ToolTip = "Order the list" } );
		sort.AddItem( "Newest first", "schedule", () => SetSort( SavedSort.Newest ) );
		sort.AddItem( "Oldest first", "schedule", () => SetSort( SavedSort.Oldest ) );
		sort.AddItem( "Name (A–Z)", "sort_by_alpha", () => SetSort( SavedSort.NameAsc ) );
		sort.AddItem( "Name (Z–A)", "sort_by_alpha", () => SetSort( SavedSort.NameDesc ) );

		// Set while ListLayout is still null, deliberately: restoring the cookie fires the restored item's
		// action, and SetSort's guard then skips its Rebuild — so the ctor's own Rebuild below is the only one,
		// instead of rendering every row's thumbnail twice on open. (Per tab — the two lists are browsed for
		// different reasons, so they remember their own order.)
		sort.StateCookie = cookie;

		header.Add( new Button( "Open Folder", "folder_open" ) { ToolTip = "Open this folder in Explorer", Clicked = () => SavedSculptsWindow.OpenDataFolder( folder ) } );
		header.Add( new Button( "Refresh", "refresh" ) { ToolTip = "Re-read the saves from disk", Clicked = HardRefresh } );

		var scroll = new ScrollArea( this );
		scroll.Canvas = new Widget( scroll );
		scroll.Canvas.Layout = Layout.Column();
		scroll.Canvas.Layout.Spacing = 4;
		ListLayout = scroll.Canvas.Layout;
		Layout.Add( scroll, 1 );

		Rebuild();
	}

	void SetSort( SavedSort sort )
	{
		if ( Sort == sort )
			return;

		Sort = sort;

		// The cookie restore fires this during construction, before ListLayout exists — the ctor Rebuilds
		// right after, so skipping it here costs nothing.
		if ( ListLayout is not null )
			Rebuild();
	}

	protected void Rebuild()
	{
		ListLayout.Clear( true );
		Fill();
		ListLayout.AddStretchCell();
	}

	// Rendering one row's thumbnail spins up a whole offscreen rig (see SdfThumbnailRender) — a few ms each,
	// and the search box rebuilds the list on every keystroke. So they're kept, keyed by the save's write time:
	// an edited save re-renders itself, a re-filter costs nothing.
	readonly Dictionary<string, Pixmap> _thumbnails = new();

	/// <summary>This save's thumbnail, rendered once per (file, write time). The brushes are fetched only on a
	/// miss, so a cached row doesn't re-read or re-parse its JSON either.</summary>
	protected Pixmap Thumbnail( string path, DateTime modified, Func<List<SdfBrush>> brushes )
	{
		var key = $"{path}|{modified.Ticks}";
		if ( _thumbnails.TryGetValue( key, out var cached ) )
			return cached;

		return _thumbnails[key] = SdfThumbnailRender.Render( brushes(), SavedSculptsWindow.ThumbnailSize );
	}

	/// <summary>The Refresh button: drop the thumbnails too, so "re-read from disk" is literally true even for a
	/// save whose timestamp somehow didn't move.</summary>
	void HardRefresh()
	{
		_thumbnails.Clear();
		Rebuild();
	}

	protected abstract void Fill();

	/// <summary>Does a save's name survive the search box? Matched on the raw save name, so the reserved head
	/// slot ("_head") is still findable by typing "head".</summary>
	protected bool Matches( string name )
		=> string.IsNullOrWhiteSpace( Filter )
		|| (name?.Contains( Filter.Trim(), StringComparison.OrdinalIgnoreCase ) ?? false);

	/// <summary>Search + sort in one pass, over whichever save type the tab lists. Name comparisons are
	/// ordinal-ignore-case (the same comparer the listers use), dates are the file's write time.</summary>
	protected List<T> Arrange<T>( IEnumerable<T> saves, Func<T, string> name, Func<T, DateTime> modified )
	{
		var list = saves.Where( s => Matches( name( s ) ) ).ToList();

		list.Sort( Sort switch
		{
			SavedSort.NameAsc => ( Comparison<T> )(( a, b ) => string.Compare( name( a ), name( b ), StringComparison.OrdinalIgnoreCase )),
			SavedSort.NameDesc => ( a, b ) => string.Compare( name( b ), name( a ), StringComparison.OrdinalIgnoreCase ),
			SavedSort.Oldest => ( a, b ) => modified( a ).CompareTo( modified( b ) ),
			_ => ( a, b ) => modified( b ).CompareTo( modified( a ) ),
		} );

		return list;
	}

	/// <summary>The line every row ends with: which data root the save lives in, and when it was last written —
	/// so a date sort reads as an order instead of a shuffle.</summary>
	protected static string Stamp( string root, DateTime modified )
		=> modified > DateTime.MinValue ? $"{root}  ·  {modified:d MMM yyyy  HH:mm}" : root;

	/// <summary>What an empty list should say: "nothing saved yet" and "nothing matched" are different problems,
	/// and only one of them is fixed by going and saving something.</summary>
	protected string EmptyText( string nothingSaved )
		=> string.IsNullOrWhiteSpace( Filter ) ? nothingSaved : $"Nothing here matches “{Filter.Trim()}”.";

	protected Layout AddRow( Pixmap thumbnail, string label )
	{
		var row = ListLayout.Add( Layout.Row() );
		row.Spacing = 4;

		// Rendered through SdfStage — the same offscreen stage the in-game HUD thumbnails use, so this is the
		// ground truth for tuning thumbnail_stage.prefab: edit the rig, save, hit Refresh here.
		// Rendered at the full icon resolution and shown smaller, so it's supersampled rather than aliased.
		row.Add( new PixmapView { FixedSize = SavedSculptsWindow.ThumbnailDisplaySize, Pixmap = thumbnail } );

		row.Add( new Label( label ), 1 );
		return row;
	}
}


/// <summary>The Sculpts tab: one row per saved <c>.sculpt</c> in either data root, plus the pinned head slot.</summary>
class SavedSculptsPage : SavedListPage
{
	public SavedSculptsPage() : base( "Saved sculptures", SculptLibrary.Folder, "mimiclay.savedsculpts.sort.sculpts" ) { }

	protected override void Fill()
	{
		var saves = SculptDataRoots.ListSculpts();

		// The head slot is hidden from the named library (leading underscore = reserved), but it's the one sculpt
		// every player has — pin it on top so there's always something here to look at. One row per root: the
		// editor's head and the standalone build's head are genuinely different files. It stays pinned under any
		// sort (it isn't part of the named library), but the search box does filter it out.
		var heads = saves.Where( s => s.Name == SculptLibrary.HeadSlot && Matches( s.Name ) ).ToList();
		foreach ( var head in heads )
			AddSculptRow( head, "(your head)", canDelete: false );

		var named = Arrange( saves.Where( s => !s.Name.StartsWith( '_' ) ), s => s.Name, s => s.Modified );
		if ( named.Count == 0 && heads.Count == 0 )
		{
			ListLayout.Add( new Label.Body( EmptyText( "No saved sculptures yet.\nSave one in-game with  mimi_sculpt_save <name>." ) ) );
			return;
		}

		foreach ( var save in named )
			AddSculptRow( save, save.Name, canDelete: true );
	}

	void AddSculptRow( SculptDataRoots.SculptRef save, string label, bool canDelete )
	{
		var s = save; // capture per row for the button lambdas

		// Read lazily, per action, rather than once per row: the search box rebuilds this list on every
		// keystroke, and parsing every save's JSON to draw a row nobody clicked is the one cost this window
		// can't cache away. A click then also acts on what's on disk NOW, not on what was there when the
		// window was built.
		var row = AddRow( Thumbnail( s.Path, s.Modified, () => SculptDataRoots.Read( s )?.Brushes ), $"{label}\n{Stamp( s.Label, s.Modified )}" );
		row.Add( new Button( "Prefab", "deployed_code" ) { ToolTip = "Export to a .prefab asset", Clicked = () => ExportPrefab( s, SculptDataRoots.Read( s ) ) } );
		row.Add( new Button( "Scene", "add_box" ) { ToolTip = "Add to the current scene", Clicked = () => AddToScene( s, SculptDataRoots.Read( s ) ) } );

		if ( canDelete )
			row.Add( new Button( "Delete", "delete" ) { ToolTip = "Delete this save", Clicked = () => Delete( s ) } );
	}

	// The entry is passed in rather than looked up here: the caller has it in hand, and a name alone can't say
	// WHICH root's copy this row is.
	static Asset ExportPrefab( SculptDataRoots.SculptRef save, SculptLibrary.Entry entry )
	{
		if ( entry is null )
		{
			Log.Warning( $"[Mimiclay] '{save.Label}/{save.Name}' is missing or unreadable." );
			return null;
		}

		return SdfPrefabUtility.ExportAsset( entry.Name ?? save.Name, entry.Brushes, entry.Resolution, entry.FlipFaces );
	}

	void AddToScene( SculptDataRoots.SculptRef save, SculptLibrary.Entry entry )
	{
		var session = SceneEditorSession.Active;
		if ( session is null )
		{
			Log.Warning( "[Mimiclay] open a scene first, then add a sculpt to it." );
			return;
		}

		// Ensure a prefab exists (and reflects the latest save), then instantiate it into the open scene —
		// the same path the engine uses for a prefab drag-drop, so it lands with undo + fresh GUIDs.
		var prefab = ExportPrefab( save, entry )?.LoadResource<PrefabFile>();
		if ( prefab is null )
		{
			Log.Warning( $"[Mimiclay] couldn't prepare a prefab for '{save.Name}'." );
			return;
		}

		using var scope = SceneEditorSession.Scope();
		using ( session.UndoScope( $"Add Sculpt '{save.Name}'" ).WithGameObjectCreations().Push() )
		{
			var go = SceneUtility.GetPrefabScene( prefab )?.Clone();
			if ( go.IsValid() )
			{
				go.Name = save.Name;
				session.Selection.Set( go );
			}
		}
	}

	void Delete( SculptDataRoots.SculptRef save )
	{
		if ( SculptDataRoots.Delete( save ) )
		{
			Log.Info( $"[Mimiclay] deleted saved sculpture '{save.Label}/{save.Name}'." );
			Rebuild();
		}
	}
}

/// <summary>The Scenes tab: one row per scene save in either data root. Thumbnail is the save's first placed
/// shape — enough to tell saves apart without rendering a whole diorama.</summary>
class SavedScenesPage : SavedListPage
{
	public SavedScenesPage() : base( "Saved scenes", SculptSceneLibrary.Folder, "mimiclay.savedsculpts.sort.scenes" ) { }

	protected override void Fill()
	{
		var scenes = Arrange( SculptDataRoots.ListScenes(), s => s.Name, s => s.Modified );
		if ( scenes.Count == 0 )
		{
			ListLayout.Add( new Label.Body( EmptyText( "No saved scenes yet.\nBuild one in creative mode, then save it with  mimi_scene_save <name>." ) ) );
			return;
		}

		foreach ( var scene in scenes )
		{
			var s = scene; // capture per row for the button lambdas

			var save = SculptDataRoots.Read( s );
			if ( save is null )
			{
				AddRow( null, $"{s.Name}  (unreadable)\n{Stamp( s.Label, s.Modified )}" );
				continue;
			}

			var shapes = save.Props.Select( p => p.Sculpt ).Distinct().Count();
			var thumbnail = Thumbnail( s.Path, s.Modified,
				() => SculptDataRoots.ReadSculpt( s, save.Props[0].Sculpt )?.Brushes );

			var row = AddRow( thumbnail, $"{s.Name}\n{save.Props.Count} prop(s), {shapes} shape(s)\nmap: {save.Map}\n{Stamp( s.Label, s.Modified )}" );
			row.Add( new Button( "Prefab", "deployed_code" ) { ToolTip = "Export every sculpt in this save to .prefab assets", Clicked = () => SdfSceneUtility.ExportPrefabs( s ) } );
			row.Add( new Button( "Scene", "add_box" ) { ToolTip = "Export the prefabs and place them in the current scene at their saved positions", Clicked = () => SdfSceneUtility.AddToScene( s ) } );
			row.Add( new Button( "Delete", "delete" ) { ToolTip = "Delete this scene save", Clicked = () => Delete( s ) } );
		}
	}

	void Delete( SculptDataRoots.SceneRef scene )
	{
		if ( SculptDataRoots.Delete( scene ) )
		{
			Log.Info( $"[Mimiclay] deleted scene save '{scene.Label}/{scene.Name}'." );
			Rebuild();
		}
	}
}
