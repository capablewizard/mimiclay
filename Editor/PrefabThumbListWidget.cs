using Sandbox.UI; // Margin, Align - see ClutterEntriesGridWidget, which imports it for the same two types

namespace Editor;

/// <summary>
/// Thumbnail grid for a prop spawner's <c>Prefabs</c> list, replacing the stock list-of-rows control.
/// The default editor for a <c>List&lt;GameObject&gt;</c> gives one row per entry, each a
/// <c>GameObjectControlWidget</c> that paints a flat blue icon and the prefab's NAME and nothing else — so a
/// spawner holding thirty props reads as thirty near-identical lines of text you have to click through one at
/// a time to find out what's actually in there. Every entry is a prefab with a rendered asset thumbnail
/// already sitting in the editor's cache; this just shows them.
///
/// Opted in per-property with <c>[Editor( "prefab-thumbs" )]</c> (see <c>Mimiclay.PropSpawnerBase.Prefabs</c>)
/// rather than by registering against <c>typeof( GameObject )</c> globally: that would hijack the inspector
/// for EVERY GameObject reference in the project (the lobby's spawn points, the debug mode's stage hooks, and
/// so on — none of which are prefabs with thumbnails) and would collide with the engine's own
/// GameObjectControlWidget registration for the same type.
///
/// The list format on disk is untouched — entries stay <c>{"_type":"gameobject","prefab":"path"}</c>, so all
/// ~1700 existing entries across the maps keep working and nothing needed migrating.
/// </summary>
[CustomEditor( typeof( List<GameObject> ), NamedEditor = "prefab-thumbs" )]
public sealed class PrefabThumbListWidget : ControlWidget
{
	public override bool SupportsMultiEdit => false;

	// Taller than one inspector row, so the sheet has to give us the full width and skip the inline label.
	public override bool IsWideMode => true;

	readonly PrefabGrid _grid;

	public PrefabThumbListWidget( SerializedProperty property ) : base( property )
	{
		Layout = Layout.Column();
		Layout.Spacing = 0;
		VerticalSizeMode = SizeMode.CanGrow;

		_grid = new PrefabGrid( this, property );
		_grid.VerticalSizeMode = SizeMode.CanGrow;
		Layout.Add( _grid, 1 );

		var buttons = Layout.AddRow();
		buttons.Spacing = 4;
		buttons.Margin = new Margin( 8, 4, 8, 8 );
		buttons.AddStretchCell();

		var add = new Button( "Add Prefabs", "add" );
		add.Clicked = () => _grid.PickPrefabs();
		buttons.Add( add );
	}

	// ── The grid ────────────────────────────────────────────────────────────────────────────────────────
	// Items are boxed INDICES, not the GameObjects themselves. A spawner list is allowed to hold the same
	// prefab more than once (repeating an entry is how you weight it — BuildCandidateOrder counts uses per
	// INDEX, not per prefab), and ListView identifies items by object identity, so entry-as-item would make
	// two copies of the same prefab indistinguishable: removing the second would silently remove the first.
	sealed class PrefabGrid : ListView
	{
		readonly SerializedProperty _property;

		public PrefabGrid( Widget parent, SerializedProperty property ) : base( parent )
		{
			_property = property;

			ItemSpacing = 4;
			Margin = 8;
			MinimumHeight = 140;
			VerticalSizeMode = SizeMode.CanGrow;
			ItemSize = new Vector2( 84, 84 + 22 ); // thumbnail square + one line for the name
			ItemAlign = Align.FlexStart;
			ItemContextMenu = ShowItemContext;
			AcceptDrops = true;

			Rebuild();
		}

		List<GameObject> Entries => _property.GetValue<List<GameObject>>();

		public void Rebuild()
		{
			int count = Entries?.Count ?? 0;
			SetItems( Enumerable.Range( 0, count ).Cast<object>() );
		}

		void Commit( List<GameObject> entries )
		{
			_property.SetValue( entries );
			_property.Parent?.NoteChanged( _property );
			Rebuild();
		}

		// The prefab at a list index, plus the asset backing it. A scene-object reference (something dragged
		// in from the hierarchy rather than the asset browser) is a valid GameObject with no Source, so it has
		// no thumbnail — it still gets a tile, just an iconned one.
		bool TryResolve( object item, out GameObject prefab, out Asset asset )
		{
			prefab = null;
			asset = null;

			var entries = Entries;
			if ( item is not int index || entries is null || index < 0 || index >= entries.Count )
				return false;

			prefab = entries[index];

			if ( prefab is PrefabScene scene && scene.Source is not null )
				asset = AssetSystem.FindByPath( scene.Source.ResourcePath );

			return true;
		}

		// ── Adding ──────────────────────────────────────────────────────────────────────────────────────
		public void PickPrefabs()
		{
			var picker = AssetPicker.Create( this, AssetType.Find( "prefab", false ),
				new AssetPicker.PickerOptions { EnableMultiselect = true } );

			picker.Window.Title = "Add Spawner Prefabs";
			picker.OnAssetPicked = assets => AddAssets( assets );
			picker.Show();
		}

		void AddAssets( IEnumerable<Asset> assets )
		{
			var entries = Entries ?? new List<GameObject>();
			bool added = false;

			foreach ( var asset in assets )
			{
				if ( asset is null || !asset.TryLoadResource<PrefabFile>( out var file ) )
					continue;

				var scene = SceneUtility.GetPrefabScene( file );
				if ( !scene.IsValid() )
					continue;

				entries.Add( scene );
				added = true;
			}

			if ( added )
				Commit( entries );
		}

		// ── Drag and drop from the asset browser ────────────────────────────────────────────────────────
		// Acceptance has to happen in OnDragHover: a drag that never sets e.Action is refused outright and
		// OnDragDrop is never reached, so the drop silently does nothing.
		public override void OnDragHover( DragEvent e )
		{
			base.OnDragHover( e );

			if ( e.Data.Assets is null )
				return;

			foreach ( var drag in e.Data.Assets )
			{
				if ( !string.IsNullOrEmpty( drag.AssetPath ) && drag.AssetPath.EndsWith( ".prefab" ) )
				{
					e.Action = DropAction.Copy;
					return;
				}
			}
		}

		public override void OnDragDrop( DragEvent e )
		{
			base.OnDragDrop( e );

			if ( e.Data.Assets is not null )
				AddDroppedAssets( e.Data.Assets );
		}

		async void AddDroppedAssets( IEnumerable<DragAssetData> dragged )
		{
			var entries = Entries ?? new List<GameObject>();
			bool added = false;

			foreach ( var drag in dragged )
			{
				// Filter on the path BEFORE resolving: GetAssetAsync on a whole folder drop of mixed types is
				// a lot of work to do just to throw most of it away.
				if ( string.IsNullOrEmpty( drag.AssetPath ) || !drag.AssetPath.EndsWith( ".prefab" ) )
					continue;

				var asset = await drag.GetAssetAsync();
				if ( asset is null || !asset.TryLoadResource<PrefabFile>( out var file ) )
					continue;

				var scene = SceneUtility.GetPrefabScene( file );
				if ( !scene.IsValid() )
					continue;

				entries.Add( scene );
				added = true;
			}

			if ( added )
				Commit( entries );
		}

		// ── Per-item menu ───────────────────────────────────────────────────────────────────────────────
		void ShowItemContext( object item )
		{
			if ( !TryResolve( item, out _, out var asset ) || item is not int index )
				return;

			var menu = new Menu( this );

			if ( asset is not null )
			{
				menu.AddOption( "Find in Asset Browser", "search", () => LocalAssetBrowser.OpenTo( asset, true ) );
				menu.AddSeparator();
			}

			menu.AddOption( "Remove", "clear", () =>
			{
				var entries = Entries;
				if ( entries is null || index < 0 || index >= entries.Count )
					return;

				entries.RemoveAt( index );
				Commit( entries );
			} );

			menu.OpenAtCursor();
		}

		// ── Painting ────────────────────────────────────────────────────────────────────────────────────
		protected override void PaintItem( VirtualWidget item )
		{
			if ( !TryResolve( item.Object, out var prefab, out var asset ) )
				return;

			var thumb = item.Rect.Shrink( 0, 0, 0, 22 ); // reserve the bottom strip for the name

			if ( Paint.HasMouseOver )
			{
				Paint.ClearPen();
				Paint.SetBrush( Theme.Blue.WithAlpha( 0.2f ) );
				Paint.DrawRect( item.Rect, 4 );
			}

			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( thumb.Shrink( 2 ), 4 );

			// generateIfNotInCache: a prefab that's never been opened has no cached thumb yet, and an empty
			// grid of placeholder icons would defeat the entire point of this widget.
			var pixmap = asset?.GetAssetThumb( true );
			if ( pixmap is not null )
			{
				Paint.Draw( thumb.Shrink( 2 ), pixmap );
			}
			else
			{
				// No asset behind it (a scene reference) or the render failed — say so rather than
				// showing an empty box that reads as a broken entry.
				Paint.SetPen( Theme.Text.WithAlpha( 0.3f ) );
				Paint.DrawIcon( thumb.Shrink( 22 ), asset is null ? "panorama_wide_angle_select" : "category", 28 );
			}

			Paint.ClearBrush();
			Paint.SetPen( Theme.ControlBackground.Lighten( 0.1f ) );
			Paint.DrawRect( thumb.Shrink( 2 ), 4 );

			var label = asset?.Name ?? (prefab.IsValid() ? prefab.Name : "(missing)");
			var nameRect = new Rect( item.Rect.Left, thumb.Bottom + 1, item.Rect.Width, 20 );
			Paint.SetDefaultFont( 8 );
			Paint.SetPen( Theme.Text.WithAlpha( prefab.IsValid() ? 0.8f : 0.4f ) );
			Paint.DrawText( nameRect, label, TextFlag.CenterTop );
		}

		protected override void OnPaint()
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( LocalRect, 4 );

			if ( Entries is null or { Count: 0 } )
			{
				Paint.SetDefaultFont( 10 );
				Paint.SetPen( Theme.Text.WithAlpha( 0.4f ) );
				Paint.DrawText( LocalRect, "Drag prefabs here, or click Add Prefabs", TextFlag.Center );
			}

			base.OnPaint();
		}
	}
}
