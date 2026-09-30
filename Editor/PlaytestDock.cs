using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Mimiclay;
using Sandbox;
using Sandbox.Network;
using Client = Editor.PlaytestLauncher.Client;
using Tone = Editor.PlaytestLauncher.Tone;

namespace Editor;

/// <summary>
/// Dockable front-end for <see cref="PlaytestLauncher"/>:
///  - pick a window layout (1–6 slots per monitor) and which monitors it repeats on;
///  - the slot map shows every slot with its client's live status — click an empty slot to add a client there,
///    drag a client onto another slot to move/swap, right-click for disconnect / kill / focus / dismiss;
///  - the client list shows the same status in words, and selecting a client shows its engine log below.
/// Changing layout or monitors while clients run reflows them into the new slots (nothing spawned or killed).
/// </summary>
[Dock( "Editor", Title, "connected_tv" )]
public class PlaytestDock : Widget
{
	public const string Title = "Playtest";

	// Not readonly: a hotload that changes these fields' types leaves them null on the live dock, and Tick
	// rebuilds the UI rather than throwing every frame until the tab is reopened.
	Label _summary;
	Layout _monitorRow;
	SlotMap _map;
	ClientList _list;
	Label _logTitle;
	TextEdit _log;

	string _monitorSignature;
	RealTimeSince _sinceRefresh;
	NetworkSettings _netSettings; // created in Build — a hotload doesn't run field initializers on a live dock

	/// <summary>Host-side network knobs — the same backing as the engine's viewport network menu
	/// (ViewportTools.Network.cs), so changing either shows in both.</summary>
	sealed class NetworkSettings
	{
		/// <summary>Who can join the session the editor hosts. The editor overrides ANY lobby it creates with
		/// this (our self-hosting lobby scenes included), but only at creation — changes apply from the next Play.</summary>
		public LobbyPrivacy LobbyPrivacy
		{
			get => EditorUtility.Network.HostPrivacy;
			set => EditorUtility.Network.HostPrivacy = value;
		}

		/// <summary>Extra latency in ms on the host's connections. Live. Added on both send and receive,
		/// so the round trip grows by about twice this.</summary>
		[Range( 0f, 500f ), Step( 25f )]
		public int SimulateLag
		{
			get => ConsoleSystem.GetValueInt( "net_fakelag" );
			set => ConsoleSystem.SetValue( "net_fakelag", value.ToString() );
		}

		/// <summary>Percentage of packets the host drops. Live. Unreliable messages only — reliable ones
		/// (RPCs, snapshots) are never dropped.</summary>
		[Range( 0f, 100f ), Step( 0.5f )]
		public float SimulatePacketLoss
		{
			get => ConsoleSystem.GetValueFloat( "net_fakepacketloss" );
			set => ConsoleSystem.SetValue( "net_fakepacketloss", value.ToString() );
		}
	}
	Dictionary<Client, PlaytestLauncher.ClientView> _views = new();

	internal Client Selected { get; private set; }
	PlaytestClientLog _shownLog;
	int _shownVersion = -1;
	int _shownCount;
	string _shownFirst;

	public PlaytestDock( Widget parent ) : base( parent )
	{
		Build();
	}

	void Build()
	{
		_monitorSignature = null;
		_shownLog = null;

		if ( Layout is null )
			Layout = Layout.Column();
		else
			Layout.Clear( true );

		// Everything lives in a scroll area: the map, list and tiles all have fixed heights, so a dock shorter
		// than their sum would otherwise have Qt overlap them. Now it scrolls instead.
		var scroller = new ScrollArea( this );
		scroller.HorizontalScrollbarMode = ScrollbarMode.Off;
		scroller.Canvas = new Widget( scroller );
		scroller.Canvas.VerticalSizeMode = SizeMode.CanGrow;
		scroller.Canvas.HorizontalSizeMode = SizeMode.Flexible;
		Layout.Add( scroller, 1 );

		var col = scroller.Canvas.Layout = Layout.Column();
		col.Margin = 8;
		col.Spacing = 6;

		col.Add( new Label( "Layout (per monitor)" ) );

		// Two rows of four tiles.
		for ( int row = 0; row * 4 < PlaytestLauncher.Layouts.Length; row++ )
		{
			var r = col.AddRow();
			r.Spacing = 6;
			for ( int i = row * 4; i < Math.Min( row * 4 + 4, PlaytestLauncher.Layouts.Length ); i++ )
				r.Add( new LayoutTile( i ), 1 );
		}

		col.AddSpacingCell( 4 );

		var monitorHeader = col.AddRow();
		monitorHeader.Spacing = 4;
		monitorHeader.Add( new Label( "Monitors" ) );
		monitorHeader.AddStretchCell();
		_monitorRow = monitorHeader.AddRow();
		_monitorRow.Spacing = 4;

		_map = new SlotMap( this );
		col.Add( _map );

		_summary = new Label( "" ) { WordWrap = true };
		col.Add( _summary );

		var buttons = col.AddRow();
		buttons.Spacing = 6;
		buttons.Add( new Button.Primary( "Launch", "play_arrow" ) { ToolTip = "Enter play mode and launch a client into every empty slot", Clicked = PlaytestLauncher.Launch }, 1 );
		buttons.Add( new Button( "Re-tile", "grid_view" ) { ToolTip = "Snap running clients back into their slots", Clicked = PlaytestLauncher.Retile } );
		buttons.Add( new Button.Danger( "Close all", "close" ) { ToolTip = "Kill every client this launcher started", Clicked = PlaytestLauncher.CloseAll } );

		col.AddSpacingCell( 4 );
		col.Add( new Label( "Network" ) );
		{
			// Same three settings (and backing) as the engine's viewport network menu, so the two stay in sync.
			_netSettings ??= new NetworkSettings();
			var sheet = new ControlSheet();
			var so = _netSettings.GetSerialized();
			sheet.AddRow( so.GetProperty( nameof( NetworkSettings.LobbyPrivacy ) ) );
			sheet.AddRow( so.GetProperty( nameof( NetworkSettings.SimulateLag ) ) );
			sheet.AddRow( so.GetProperty( nameof( NetworkSettings.SimulatePacketLoss ) ) );
			col.Add( new Widget( this ) { Layout = sheet } );
		}

		col.AddSpacingCell( 4 );
		_list = new ClientList( this );
		col.Add( _list );

		var logHeader = col.AddRow();
		logHeader.Spacing = 4;
		_logTitle = new Label( "" );
		logHeader.Add( _logTitle, 1 );
		logHeader.Add( new Button( "Open file", "description" ) { ToolTip = "Open the selected client's log file", Clicked = OpenLogFile } );

		// Stretches to fill a tall dock; never shrinks below a readable height (the scroll area takes over).
		_log = new TextEdit( this ) { Editable = false, TextSelectable = true, MinimumHeight = 200 };
		_log.PlaceholderText = "Select a client to see its log.";
		col.Add( _log, 1 );

		RebuildMonitorButtons();
		Refresh();
	}

	internal PlaytestLauncher.ClientView ViewOf( Client c ) => _views.TryGetValue( c, out var v ) ? v : PlaytestLauncher.Describe( c );

	internal void Select( Client client )
	{
		Selected = client;
		RefreshLog();
		_map.Update();
		_list.Update();
	}

	internal void OpenClientMenu( Client c )
	{
		var menu = new Menu( this );
		menu.AddOption( $"Client #{c.InstanceId}" ).Enabled = false;
		menu.AddSeparator();

		if ( c.Exited )
		{
			int slot = c.Slot;
			if ( slot >= 0 )
				menu.AddOption( "Relaunch here", "replay", () => { PlaytestLauncher.Dismiss( c ); PlaytestLauncher.AddAt( slot ); } );
			menu.AddOption( "Dismiss", "delete", () => PlaytestLauncher.Dismiss( c ) );
		}
		else
		{
			menu.AddOption( "Focus window", "open_in_new", () => PlaytestLauncher.Focus( c ) );
			var leave = menu.AddOption( "Disconnect (leave to menu)", "logout", () => PlaytestLauncher.Disconnect( c ) );
			leave.Enabled = PlaytestLauncher.ConnectionOf( c ) is not null;
			menu.AddOption( "Kill process", "dangerous", () => PlaytestLauncher.Kill( c ) );
		}

		menu.AddSeparator();
		menu.AddOption( "Show log", "description", () => Select( c ) );
		menu.OpenAtCursor();
	}

	bool _rebuildPending;

	// A hotload swaps the dock's CODE but keeps this live widget, and the layout is only built in Build() — so
	// without this, new/changed sections never show until the tab (or editor) is reopened. Flag it and rebuild
	// on the next frame tick rather than tearing widgets down mid-hotload.
	[EditorEvent.Hotload]
	void OnHotload() => _rebuildPending = true;

	[EditorEvent.Frame]
	public void Tick()
	{
		if ( !Visible )
			return;

		if ( _rebuildPending || _map is null || _list is null || _log is null )
		{
			_rebuildPending = false;
			Build();
		}

		if ( _sinceRefresh < 0.25f )
			return;

		_sinceRefresh = 0;
		PlaytestLauncher.CheckPlanChanged();
		Refresh();
	}

	void Refresh()
	{
		ClientStatusBoard.Prune();

		var monitors = PlaytestLauncher.GetMonitors();
		var signature = string.Join( '|', monitors.Select( m => $"{m.Device}{m.HasEditor}" ) ) + string.Join( '|', PlaytestLauncher.SelectedMonitors );
		if ( signature != _monitorSignature )
			RebuildMonitorButtons();

		_views = PlaytestLauncher.Clients.ToDictionary( c => c, PlaytestLauncher.Describe );

		if ( Selected is not null && !PlaytestLauncher.Clients.Contains( Selected ) )
			Select( null );

		var selected = PlaytestLauncher.SelectedMonitors;
		int onScreens = monitors.Count( m => selected.Contains( m.Device ) );
		int slots = PlaytestLauncher.Layout.Cells.Length * onScreens;
		int running = PlaytestLauncher.Clients.Count( c => !c.Exited );
		int ready = PlaytestLauncher.Clients.Count( c => ViewOf( c ).Tone == Tone.Good );
		int queued = PlaytestLauncher.QueuedCount;

		var sb = new StringBuilder();
		sb.Append( onScreens == 0 ? "No monitor selected — windows won't be placed" : $"{slots} slot{(slots == 1 ? "" : "s")} · {PlaytestLauncher.Layout.Name}{(onScreens > 1 ? $" × {onScreens} monitors" : "")}" );
		if ( running > 0 ) sb.Append( $" · {running} running, {ready} ready" );
		if ( queued > 0 ) sb.Append( $" · {queued} queued" );
		_summary.Text = sb.ToString();

		foreach ( var tile in Children.OfType<LayoutTile>() )
			tile.Update();

		_map.Update();
		_list.Refresh();
		RefreshLog();
	}

	void RebuildMonitorButtons()
	{
		var monitors = PlaytestLauncher.GetMonitors();
		var selected = PlaytestLauncher.SelectedMonitors;
		_monitorSignature = string.Join( '|', monitors.Select( m => $"{m.Device}{m.HasEditor}" ) ) + string.Join( '|', selected );

		_monitorRow.Clear( true );
		foreach ( var m in monitors )
		{
			var device = m.Device;
			var button = new Button( m.HasEditor ? $"{m.Number} (editor)" : m.Number.ToString() )
			{
				IsToggle = true,
				IsChecked = selected.Contains( device ),
				ToolTip = $"{m.Bounds.Width}×{m.Bounds.Height} — click to {(selected.Contains( device ) ? "stop using" : "use")} this monitor",
			};
			button.Clicked = () =>
			{
				PlaytestLauncher.ToggleMonitor( device );
				RebuildMonitorButtons();
				Refresh();
			};
			_monitorRow.Add( button );
		}
	}

	// ---------------------------------------------------------------------------------------------------------
	// Log view

	static readonly Regex ErrorPattern = new( @"\b(error|exception|failed|fail)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled );
	static readonly Regex WarnPattern = new( @"\b(warn|warning)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled );

	void RefreshLog()
	{
		var c = Selected;
		_logTitle.Text = c is null ? "Log" : $"Log — client #{c.InstanceId}" + (c.LogUnavailable ? " (log file not found)" : c.Log is null ? " (waiting for log file…)" : "");

		var log = c?.Log;
		if ( log != _shownLog )
		{
			_shownLog = log;
			_shownVersion = -1;
			_shownCount = 0;
			_shownFirst = null;
			_log.Clear();
		}

		if ( log is null || log.Version == _shownVersion )
			return;

		_shownVersion = log.Version;
		var lines = log.Snapshot();
		bool follow = _log.VerticalScrollbar.SliderPosition >= _log.VerticalScrollbar.Maximum - 4;

		// The buffer trims its head past its cap — if the first line moved, redraw from scratch.
		if ( lines.Length == 0 || lines[0] != _shownFirst || lines.Length < _shownCount )
		{
			_log.Clear();
			_shownCount = 0;
			_shownFirst = lines.Length > 0 ? lines[0] : null;
		}

		if ( lines.Length > _shownCount )
		{
			var html = new StringBuilder();
			for ( int i = _shownCount; i < lines.Length; i++ )
			{
				if ( i > _shownCount ) html.Append( "<br>" );
				var text = WebUtility.HtmlEncode( lines[i] );
				var colour = ErrorPattern.IsMatch( lines[i] ) ? "#ff7070" : WarnPattern.IsMatch( lines[i] ) ? "#e5c07b" : null;
				html.Append( colour is null ? text : $"<span style=\"color:{colour}\">{text}</span>" );
			}

			_log.AppendHtml( html.ToString() );
			_shownCount = lines.Length;
		}

		if ( follow )
			_log.ScrollToBottom();
	}

	void OpenLogFile()
	{
		var path = Selected?.Log?.Path;
		if ( string.IsNullOrEmpty( path ) )
			return;

		try
		{
			System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo( path ) { UseShellExecute = true } );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Playtest: couldn't open {path} — {e.Message}" );
		}
	}

	internal static Color ToneColor( Tone tone ) => tone switch
	{
		Tone.Good => Theme.Green,
		Tone.Bad => Theme.Red,
		Tone.Busy => Theme.Yellow,
		_ => Theme.Text.WithAlpha( 0.5f ),
	};

	// ---------------------------------------------------------------------------------------------------------

	/// <summary>One clickable layout preset, drawn as its tiles.</summary>
	sealed class LayoutTile : Widget
	{
		readonly int _index;

		public LayoutTile( int index ) : base( null )
		{
			_index = index;
			FixedHeight = 46;
			MinimumWidth = 52;
			Cursor = CursorShape.Finger;
			ToolTip = $"{PlaytestLauncher.Layouts[index].Name} — {PlaytestLauncher.Layouts[index].Cells.Length} per monitor";
		}

		protected override void OnMousePress( MouseEvent e )
		{
			if ( !e.LeftMouseButton )
				return;

			PlaytestLauncher.LayoutIndex = _index;
			Parent?.Update();
		}

		protected override void OnPaint()
		{
			bool selected = PlaytestLauncher.LayoutIndex == _index;
			bool hover = Paint.HasMouseOver;

			Paint.Antialiasing = true;

			Paint.ClearPen();
			Paint.SetBrush( selected ? Theme.Primary.WithAlpha( 0.18f ) : Theme.ControlBackground.Lighten( hover ? 0.2f : 0f ) );
			Paint.DrawRect( LocalRect, 4 );

			var inner = LocalRect.Shrink( 5 );
			var line = selected ? Theme.Primary : Theme.Text.WithAlpha( hover ? 0.7f : 0.45f );

			foreach ( var c in PlaytestLauncher.Layouts[_index].Cells )
			{
				var r = new Rect( inner.Left + c.Left * inner.Width, inner.Top + c.Top * inner.Height, c.Width * inner.Width, c.Height * inner.Height ).Shrink( 1.5f );
				Paint.SetPen( line, 1.5f );
				Paint.SetBrush( line.WithAlpha( selected ? 0.25f : 0.1f ) );
				Paint.DrawRect( r, 2 );
			}
		}
	}

	/// <summary>The desk to scale: every monitor in its real arrangement; selected ones are divided into the
	/// layout's slots, each showing its client. Click empty = add, drag = move/swap, right-click = actions.</summary>
	sealed class SlotMap : Widget
	{
		readonly PlaytestDock _dock;
		readonly List<(Rect Rect, int Slot)> _slots = new();

		Vector2 _pressAt;
		int _pressSlot = -1;
		bool _dragging;

		public SlotMap( PlaytestDock dock ) : base( null )
		{
			_dock = dock;
			FixedHeight = 190;
			MouseTracking = true;
		}

		// Height follows width (the desk's own aspect), so a narrow dock gets a short map instead of a tall
		// strip of empty space — and a wide one doesn't blow up past a sensible size. Depends only on width,
		// so setting it from OnResize can't feed back into itself.
		protected override void OnResize()
		{
			base.OnResize();

			var monitors = PlaytestLauncher.GetMonitors();
			if ( monitors.Count == 0 || Width <= 0 )
				return;

			float deskWidth = monitors.Max( m => m.Bounds.Right ) - monitors.Min( m => m.Bounds.Left );
			float deskHeight = monitors.Max( m => m.Bounds.Bottom ) - monitors.Min( m => m.Bounds.Top );
			float height = Math.Clamp( (Width - 8) * deskHeight / deskWidth + 8, 90f, 260f );

			if ( MathF.Abs( FixedHeight - height ) > 1 )
				FixedHeight = height;
		}

		int SlotAt( Vector2 local )
		{
			foreach ( var (rect, slot) in _slots )
				if ( rect.IsInside( local ) )
					return slot;
			return -1;
		}

		protected override void OnMousePress( MouseEvent e )
		{
			int slot = SlotAt( e.LocalPosition );
			var client = PlaytestLauncher.ClientInSlot( slot );

			if ( e.RightMouseButton )
			{
				if ( client is not null )
					_dock.OpenClientMenu( client );
				return;
			}

			if ( !e.LeftMouseButton )
				return;

			_pressAt = e.LocalPosition;
			_pressSlot = slot;
			_dragging = false;
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			if ( _pressSlot >= 0 && !_dragging && PlaytestLauncher.ClientInSlot( _pressSlot ) is not null
				&& (e.LocalPosition - _pressAt).Length > 5 )
				_dragging = true;

			int hover = SlotAt( e.LocalPosition );
			var client = PlaytestLauncher.ClientInSlot( hover );
			ToolTip = hover < 0 ? ""
				: client is null ? (PlaytestLauncher.IsQueued( hover ) ? $"Slot {hover + 1}: queued" : $"Slot {hover + 1}: click to add a client")
				: $"#{client.InstanceId} — {_dock.ViewOf( client ).Title}\n{_dock.ViewOf( client ).Detail}\nDrag to move · right-click for actions";
			Cursor = _dragging ? CursorShape.ClosedHand : hover >= 0 ? CursorShape.Finger : CursorShape.Arrow;

			Update();
		}

		protected override void OnMouseReleased( MouseEvent e )
		{
			if ( !e.LeftMouseButton || _pressSlot < 0 )
				return;

			int slot = SlotAt( e.LocalPosition );
			var client = PlaytestLauncher.ClientInSlot( _pressSlot );

			if ( _dragging )
			{
				if ( client is not null && slot >= 0 && slot != _pressSlot )
					PlaytestLauncher.MoveTo( client, slot );
			}
			else if ( slot == _pressSlot )
			{
				if ( client is not null )
					_dock.Select( client );
				else
					PlaytestLauncher.AddAt( slot );
			}

			_pressSlot = -1;
			_dragging = false;
			Cursor = CursorShape.Arrow;
			Update();
		}

		protected override void OnMouseLeave() => Update();

		protected override void OnPaint()
		{
			_slots.Clear();

			var monitors = PlaytestLauncher.GetMonitors();
			if ( monitors.Count == 0 )
				return;

			var selected = PlaytestLauncher.SelectedMonitors;
			var layout = PlaytestLauncher.Layout;

			// Fit the union of all monitors into the widget, keeping proportions.
			int minX = monitors.Min( m => m.Bounds.Left ), minY = monitors.Min( m => m.Bounds.Top );
			int maxX = monitors.Max( m => m.Bounds.Right ), maxY = monitors.Max( m => m.Bounds.Bottom );
			var area = LocalRect.Shrink( 4 );
			float scale = MathF.Min( area.Width / (maxX - minX), area.Height / (maxY - minY) );
			float offX = area.Left + (area.Width - (maxX - minX) * scale) * 0.5f;
			float offY = area.Top + (area.Height - (maxY - minY) * scale) * 0.5f;

			Rect ToLocal( PlaytestLauncher.RECT r ) =>
				new( offX + (r.Left - minX) * scale, offY + (r.Top - minY) * scale, r.Width * scale, r.Height * scale );

			Paint.Antialiasing = true;
			var mouse = FromScreen( Application.CursorPosition );
			int hoverSlot = IsUnderMouse ? SlotAt( mouse ) : -1;
			int slot = 0;

			foreach ( var m in monitors )
			{
				var rect = ToLocal( m.Bounds ).Shrink( 2 );
				bool on = selected.Contains( m.Device );

				Paint.SetPen( on ? Theme.Primary.WithAlpha( 0.8f ) : Theme.Text.WithAlpha( 0.25f ), 1f );
				Paint.SetBrush( on ? Theme.Primary.WithAlpha( 0.06f ) : Theme.ControlBackground );
				Paint.DrawRect( rect, 3 );

				if ( !on )
				{
					Paint.SetPen( Theme.Text.WithAlpha( 0.4f ) );
					Paint.SetDefaultFont( 10, 700 );
					Paint.DrawText( rect, m.Number.ToString(), TextFlag.Center );
					Paint.SetDefaultFont( 7 );
					Paint.DrawText( rect.Shrink( 4 ), m.HasEditor ? "Editor" : "off", TextFlag.CenterHorizontally | TextFlag.Bottom );
					continue;
				}

				var work = ToLocal( m.Work ).Shrink( 3 );
				foreach ( var c in layout.Cells )
				{
					var cell = new Rect( work.Left + c.Left * work.Width, work.Top + c.Top * work.Height, c.Width * work.Width, c.Height * work.Height ).Shrink( 1.5f );
					_slots.Add( (cell, slot) );
					PaintSlot( cell, slot, hoverSlot );
					slot++;
				}
			}
		}

		void PaintSlot( Rect cell, int slot, int hoverSlot )
		{
			var client = PlaytestLauncher.ClientInSlot( slot );
			bool hover = slot == hoverSlot;
			bool dropTarget = _dragging && hover && slot != _pressSlot;
			bool dragSource = _dragging && slot == _pressSlot;

			if ( client is null )
			{
				bool queued = PlaytestLauncher.IsQueued( slot );
				Paint.SetPen( dropTarget ? Theme.Primary : Theme.Text.WithAlpha( hover ? 0.5f : 0.2f ), dropTarget ? 2f : 1f, PenStyle.Dash );
				Paint.SetBrush( hover ? Theme.Primary.WithAlpha( 0.1f ) : Color.Transparent );
				Paint.DrawRect( cell, 2 );

				Paint.SetPen( Theme.Text.WithAlpha( hover ? 0.8f : 0.35f ) );
				Paint.SetDefaultFont( queued ? 7 : 11 );
				Paint.DrawText( cell, queued ? "queued…" : "+", TextFlag.Center );
				return;
			}

			var view = _dock.ViewOf( client );
			var tone = ToneColor( view.Tone );
			bool isSelected = _dock.Selected == client;

			Paint.SetPen( dropTarget || isSelected ? Theme.Primary : tone.WithAlpha( dragSource ? 0.4f : 0.9f ), dropTarget || isSelected ? 2f : 1f );
			Paint.SetBrush( tone.WithAlpha( dragSource ? 0.06f : hover ? 0.28f : 0.18f ) );
			Paint.DrawRect( cell, 2 );

			var text = cell.Shrink( 4, 2 );
			Paint.SetPen( Theme.Text );
			Paint.SetDefaultFont( 8, 700 );
			Paint.DrawText( text, $"#{client.InstanceId}", TextFlag.LeftTop );

			if ( cell.Height > 26 )
			{
				Paint.SetDefaultFont( 7 );
				Paint.SetPen( Theme.Text.WithAlpha( 0.85f ) );
				Paint.DrawText( text, view.Title, TextFlag.LeftBottom );
			}
		}
	}

	/// <summary>Every tracked client as a row: status dot, id, slot, stage, detail. Click to select (shows its
	/// log), right-click for actions.</summary>
	sealed class ClientList : Widget
	{
		const float RowHeight = 20;
		readonly PlaytestDock _dock;
		List<Client> _rows = new();

		public ClientList( PlaytestDock dock ) : base( null )
		{
			_dock = dock;
			MouseTracking = true;
			Refresh();
		}

		public void Refresh()
		{
			_rows = PlaytestLauncher.Clients
				.OrderBy( c => c.Slot < 0 ? int.MaxValue : c.Slot )
				.ThenBy( c => c.InstanceId )
				.ToList();

			FixedHeight = Math.Max( 1, _rows.Count ) * RowHeight + 4;
			Update();
		}

		Client RowAt( Vector2 local )
		{
			int i = (int)((local.y - 2) / RowHeight);
			return i >= 0 && i < _rows.Count ? _rows[i] : null;
		}

		protected override void OnMousePress( MouseEvent e )
		{
			var c = RowAt( e.LocalPosition );
			if ( c is null )
				return;

			if ( e.RightMouseButton )
				_dock.OpenClientMenu( c );
			else if ( e.LeftMouseButton )
				_dock.Select( c );
		}

		protected override void OnDoubleClick( MouseEvent e )
		{
			if ( RowAt( e.LocalPosition ) is Client c )
				PlaytestLauncher.Focus( c );
		}

		protected override void OnMouseMove( MouseEvent e ) => Update();

		protected override void OnMouseLeave() => Update();

		protected override void OnPaint()
		{
			Paint.Antialiasing = true;

			if ( _rows.Count == 0 )
			{
				Paint.SetPen( Theme.Text.WithAlpha( 0.4f ) );
				Paint.SetDefaultFont( 8 );
				Paint.DrawText( LocalRect.Shrink( 4, 0 ), "No clients — Launch, or click an empty slot.", TextFlag.LeftCenter );
				return;
			}

			var mouse = FromScreen( Application.CursorPosition );
			var hovered = IsUnderMouse ? RowAt( mouse ) : null;

			for ( int i = 0; i < _rows.Count; i++ )
			{
				var c = _rows[i];
				var view = _dock.ViewOf( c );
				var row = new Rect( 0, 2 + i * RowHeight, Width, RowHeight );

				if ( _dock.Selected == c || hovered == c )
				{
					Paint.ClearPen();
					Paint.SetBrush( Theme.Primary.WithAlpha( _dock.Selected == c ? 0.2f : 0.08f ) );
					Paint.DrawRect( row, 3 );
				}

				Paint.ClearPen();
				Paint.SetBrush( ToneColor( view.Tone ) );
				Paint.DrawCircle( new Vector2( row.Left + 10, row.Top + row.Height * 0.5f ), new Vector2( 7 ) );

				Paint.SetPen( Theme.Text );
				Paint.SetDefaultFont( 8, 700 );
				Paint.DrawText( new Rect( row.Left + 20, row.Top, 34, row.Height ), $"#{c.InstanceId}", TextFlag.LeftCenter );

				Paint.SetDefaultFont( 8 );
				Paint.SetPen( Theme.Text.WithAlpha( 0.55f ) );
				Paint.DrawText( new Rect( row.Left + 54, row.Top, 44, row.Height ), c.Slot >= 0 ? $"slot {c.Slot + 1}" : "untiled", TextFlag.LeftCenter );

				Paint.SetPen( Theme.Text );
				Paint.DrawText( new Rect( row.Left + 100, row.Top, 110, row.Height ), view.Title, TextFlag.LeftCenter );

				Paint.SetPen( Theme.Text.WithAlpha( 0.6f ) );
				Paint.DrawText( new Rect( row.Left + 212, row.Top, Math.Max( 0, row.Width - 216 ), row.Height ), view.Detail, TextFlag.LeftCenter | TextFlag.SingleLine );
			}
		}
	}
}
