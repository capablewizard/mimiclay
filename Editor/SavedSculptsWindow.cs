using System.IO;
using Editor;
using Mimiclay;

namespace Editor;

/// <summary>
/// A "Mimiclay" menu on the main editor menu bar + a small window that lists every saved <c>.sculpt</c>
/// (from <see cref="SculptLibrary"/>, i.e. the in-game saves) and lets you turn each one into a scene asset:
/// export to a <c>.prefab</c>, drop it straight into the open scene, delete it, or open the folder in Explorer.
///
/// Editor-only (writing project assets / driving the scene needs the tools assembly). The saves themselves are
/// written at runtime by the game to <see cref="Sandbox.FileSystem"/>.Data — this is the dev-side bridge that promotes
/// them into the project. See [[sculpt-save-and-prefab-export]].
/// </summary>
public class SavedSculptsWindow : BaseWindow
{
	Layout _listLayout;

	/// <summary>Render resolution for each row's thumbnail — the icon size the whole system targets. Kept above
	/// the display size so the downsample in <see cref="PixmapView"/> has headroom on a scaled display.</summary>
	const int ThumbnailSize = 256;

	/// <summary>How big those rows actually draw. Smaller than the render, deliberately: judging lighting wants
	/// detail, but a full 256px row would only fit three sculpts on screen.</summary>
	const int ThumbnailDisplaySize = 128;

	[Menu( "Editor", "Mimiclay/Saved Sculpts…", "view_in_ar" )]
	public static void OpenWindow()
	{
		var window = new SavedSculptsWindow();
		window.Show();
	}

	[Menu( "Editor", "Mimiclay/Open Sculpts Folder", "folder_open" )]
	public static void OpenFolder()
	{
		var dir = Sandbox.FileSystem.Data.GetFullPath( SculptLibrary.Folder );
		Directory.CreateDirectory( dir ); // the folder only exists once something's been saved — make sure it's there
		EditorUtility.OpenFolder( dir );
	}

	public SavedSculptsWindow()
	{
		WindowTitle = "Saved Sculpts";
		SetWindowIcon( "view_in_ar" );
		Size = new Vector2( 560, 560 );

		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 8;

		var header = Layout.Add( Layout.Row() );
		header.Spacing = 4;
		header.Add( new Label( "Saved sculptures" ), 1 );
		header.Add( new Button( "Open Folder", "folder_open" ) { ToolTip = "Open the sculpts folder in Explorer", Clicked = OpenFolder } );
		header.Add( new Button( "Refresh", "refresh" ) { Clicked = Rebuild } );

		var scroll = new ScrollArea( this );
		scroll.Canvas = new Widget( scroll );
		scroll.Canvas.Layout = Layout.Column();
		scroll.Canvas.Layout.Spacing = 4;
		_listLayout = scroll.Canvas.Layout;
		Layout.Add( scroll, 1 );

		Rebuild();
	}

	void Rebuild()
	{
		_listLayout.Clear( true );

		var names = SculptLibrary.List();

		// The head slot is filtered out of List() (leading underscore = reserved), but it's the one sculpt every
		// player has — pin it on top so there's always something here to look at.
		var hasHead = SculptLibrary.Exists( SculptLibrary.HeadSlot );
		if ( hasHead )
			AddRow( SculptLibrary.HeadSlot, "(your head)", canDelete: false );

		if ( names.Count == 0 && !hasHead )
		{
			_listLayout.Add( new Label.Body( "No saved sculptures yet.\nSave one in-game with  mimi_sculpt_save <name>." ) );
			_listLayout.AddStretchCell();
			return;
		}

		foreach ( var name in names )
			AddRow( name, name, canDelete: true );

		_listLayout.AddStretchCell();
	}

	void AddRow( string name, string label, bool canDelete )
	{
		var n = name; // capture per row for the button lambdas

		var row = _listLayout.Add( Layout.Row() );
		row.Spacing = 4;

		// Rendered through SdfStage — the same offscreen stage the in-game HUD thumbnails use, so this is the
		// ground truth for tuning thumbnail_stage.prefab: edit the rig, save, hit Refresh here.
		// Rendered at the full icon resolution and shown smaller, so it's supersampled rather than aliased.
		row.Add( new PixmapView { FixedSize = ThumbnailDisplaySize, Pixmap = SdfThumbnailRender.RenderSaved( n, ThumbnailSize ) } );

		row.Add( new Label( label ), 1 );
		row.Add( new Button( "Prefab", "deployed_code" ) { ToolTip = "Export to a .prefab asset", Clicked = () => ExportPrefab( n ) } );
		row.Add( new Button( "Scene", "add_box" ) { ToolTip = "Add to the current scene", Clicked = () => AddToScene( n ) } );

		if ( canDelete )
			row.Add( new Button( "Delete", "delete" ) { ToolTip = "Delete this save", Clicked = () => Delete( n ) } );
	}

	static void ExportPrefab( string name )
	{
		SdfPrefabUtility.ExportFromSave( name ); // logs the result + path itself
	}

	void AddToScene( string name )
	{
		var session = SceneEditorSession.Active;
		if ( session is null )
		{
			Log.Warning( "[Mimiclay] open a scene first, then add a sculpt to it." );
			return;
		}

		// Ensure a prefab exists (and reflects the latest save), then instantiate it into the open scene —
		// the same path the engine uses for a prefab drag-drop, so it lands with undo + fresh GUIDs.
		var asset = SdfPrefabUtility.ExportAssetFromSave( name );
		var prefab = asset?.LoadResource<PrefabFile>();
		if ( prefab is null )
		{
			Log.Warning( $"[Mimiclay] couldn't prepare a prefab for '{name}'." );
			return;
		}

		using var scope = SceneEditorSession.Scope();
		using ( session.UndoScope( $"Add Sculpt '{name}'" ).WithGameObjectCreations().Push() )
		{
			var go = SceneUtility.GetPrefabScene( prefab )?.Clone();
			if ( go.IsValid() )
			{
				go.Name = name;
				session.Selection.Set( go );
			}
		}
	}

	void Delete( string name )
	{
		if ( SculptLibrary.Delete( name ) )
		{
			Log.Info( $"[Mimiclay] deleted saved sculpture '{name}'." );
			Rebuild();
		}
	}
}
