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
		Size = new Vector2( 440, 480 );

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
		if ( names.Count == 0 )
		{
			_listLayout.Add( new Label.Body( "No saved sculptures yet.\nSave one in-game with  mimi_sculpt_save <name>." ) );
			_listLayout.AddStretchCell();
			return;
		}

		foreach ( var name in names )
		{
			var n = name; // capture per row for the button lambdas

			var row = _listLayout.Add( Layout.Row() );
			row.Spacing = 4;
			row.Add( new Label( n ), 1 );
			row.Add( new Button( "Prefab", "deployed_code" ) { ToolTip = "Export to a .prefab asset", Clicked = () => ExportPrefab( n ) } );
			row.Add( new Button( "Scene", "add_box" ) { ToolTip = "Add to the current scene", Clicked = () => AddToScene( n ) } );
			row.Add( new Button( "Delete", "delete" ) { ToolTip = "Delete this save", Clicked = () => Delete( n ) } );
		}

		_listLayout.AddStretchCell();
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
