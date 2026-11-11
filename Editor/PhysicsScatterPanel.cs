using System.Collections.Generic;
using System.Linq;

namespace Editor;

/// <summary>
/// The Physics Scatter panel: a small window in every scene viewport, stacked on top of the
/// <see cref="CollisionPanel"/> (or the <see cref="SculptPanel"/> when that's hidden), the same frameless
/// arrangement. Shown only while the selection holds something <see cref="PhysicsScatter"/> can simulate
/// (clay props, Rigidbodies, models with physics) or a run is going. Hide it for good from
/// Editor → Mimiclay → Physics Scatter Panel.
/// </summary>
public static class PhysicsScatterPanel
{
	const string EnabledCookie = "Mimiclay.PhysicsScatterPanel.Enabled";
	const string OptionsCookie = "Mimiclay.PhysicsScatterPanel.ShowOptions";

	[Menu( "Editor", "Mimiclay/Physics Scatter Panel", "scatter_plot" )]
	public static bool Enabled
	{
		get => _enabled ??= EditorCookie.Get( EnabledCookie, true );
		set
		{
			_enabled = value;
			EditorCookie.Set( EnabledCookie, value );
			if ( !value ) DestroyAll();
		}
	}
	static bool? _enabled;

	/// <summary>Launch settings shown (on) or folded away behind the header's tune button (off).</summary>
	public static bool ShowOptions
	{
		get => _showOptions ??= EditorCookie.Get( OptionsCookie, true );
		set
		{
			_showOptions = value;
			EditorCookie.Set( OptionsCookie, value );
			RebuildAll();
		}
	}
	static bool? _showOptions;

	/// <summary>How many objects a run would simulate right now (refreshed a few times a second).</summary>
	public static int CandidateCount { get; private set; }

	static readonly Dictionary<SceneViewportWidget, PhysicsScatterPanelWidget> _widgets = new();
	static RealTimeSince _sinceCount;

	[EditorEvent.Frame]
	public static void Tick()
	{
		if ( !Enabled )
		{
			DestroyAll();
			return;
		}

		if ( _sinceCount > 0.1f )
		{
			_sinceCount = 0;
			CandidateCount = PhysicsScatter.IsSimulating ? 0 : PhysicsScatter.Candidates().Count;
		}

		if ( !PhysicsScatter.IsSimulating && CandidateCount == 0 )
		{
			DestroyAll();
			return;
		}

		Sync();
	}

	/// <summary>One panel per viewport of the active scene view.</summary>
	static void Sync()
	{
		var viewports = SceneViewWidget.Current?._viewports;

		foreach ( var (viewport, widget) in _widgets.ToArray() )
		{
			if ( viewport.IsValid() && widget.IsValid() && viewports is not null && viewports.Values.Contains( viewport ) )
				continue;

			widget?.Destroy();
			_widgets.Remove( viewport );
		}

		if ( viewports is null )
			return;

		foreach ( var viewport in viewports.Values )
		{
			if ( !viewport.IsValid() )
				continue;

			if ( !_widgets.TryGetValue( viewport, out var widget ) )
			{
				widget = new PhysicsScatterPanelWidget( viewport );
				_widgets[viewport] = widget;
			}

			widget.Refresh();
			widget.Follow();
		}
	}

	static void DestroyAll()
	{
		foreach ( var widget in _widgets.Values )
			widget?.Destroy();

		_widgets.Clear();
	}

	[EditorEvent.Hotload]
	static void OnHotload() => RebuildAll();

	internal static void RebuildAll()
	{
		foreach ( var widget in _widgets.Values )
			widget?.Rebuild();
	}
}

internal class PhysicsScatterPanelWidget : Widget
{
	static readonly Vector2 Inset = new( 8, 8 );
	const float Gap = 4f;
	const float PanelWidth = 230f;

	readonly SceneViewportWidget _viewport;

	// Not readonly: a hotload keeps this live widget but drops what Rebuild made; Refresh rebuilds when they're gone.
	IconButton _icon;
	Button _run;
	Button _cancel;
	Label _status;
	string _shownText;
	bool _shownSimulating;

	public PhysicsScatterPanelWidget( SceneViewportWidget viewport ) : base( viewport )
	{
		_viewport = viewport;

		TranslucentBackground = true;
		NoSystemBackground = true;
		ShowWithoutActivating = true;
		WindowFlags = WindowFlags.FramelessWindowHint | WindowFlags.Tool;

		Rebuild();
		Show();
	}

	public void Rebuild()
	{
		if ( Layout is null )
			Layout = Layout.Column();
		else
			Layout.Clear( true );

		Layout.Margin = 6;
		Layout.Spacing = 4;

		bool simulating = PhysicsScatter.IsSimulating;
		_shownSimulating = simulating;

		// Header: icon (lit while running), title, fold the options, close.
		var header = Layout.AddRow();
		header.Spacing = 4;
		_icon = header.Add( new IconButton( "scatter_plot" )
		{
			ToolTip = "Physics Scatter: drop or toss the selected props with real physics, then keep where they land",
			Background = simulating ? Theme.Primary.WithAlpha( 0.6f ) : Color.Transparent,
		} );
		header.Add( new Label( "Physics Scatter" ) { Alignment = TextFlag.LeftCenter } );
		header.AddStretchCell();
		header.Add( new IconButton( "tune", () => PhysicsScatterPanel.ShowOptions = !PhysicsScatterPanel.ShowOptions )
		{
			ToolTip = PhysicsScatterPanel.ShowOptions ? "Hide the launch settings" : "Show the launch settings",
			Background = PhysicsScatterPanel.ShowOptions ? Theme.Primary.WithAlpha( 0.35f ) : Color.Transparent,
		} );
		header.Add( new IconButton( "close", () => PhysicsScatterPanel.Enabled = false )
		{ ToolTip = "Hide (Editor → Mimiclay → Physics Scatter Panel brings it back)", Background = Color.Transparent } );

		if ( PhysicsScatterPanel.ShowOptions && !simulating )
		{
			var sheet = new ControlSheet();
			sheet.AddObject( PhysicsScatter.Options.GetSerialized() );
			Layout.Add( sheet );
		}

		var row = Layout.AddRow();
		row.Spacing = 4;
		_run = row.Add( new Button.Primary( "Simulate", simulating ? "stop" : "play_arrow" ) { Clicked = OnRun } );
		if ( simulating )
		{
			_cancel = row.Add( new Button( "Cancel", "undo" )
			{
				Clicked = PhysicsScatter.Cancel,
				ToolTip = "Stop and put everything back where it started",
			} );
		}

		_status = Layout.Add( new Label( "" ) { WordWrap = true } );
		_shownText = null;

		FixedWidth = PanelWidth;
		Refresh();
	}

	static void OnRun()
	{
		if ( PhysicsScatter.IsSimulating )
			PhysicsScatter.Stop();
		else
			PhysicsScatter.Start();
	}

	/// <summary>Update the button and status in place; rebuild when a run starts or ends (the layout differs).</summary>
	public void Refresh()
	{
		bool simulating = PhysicsScatter.IsSimulating;
		if ( simulating != _shownSimulating || !_run.IsValid() || !_status.IsValid() )
		{
			Rebuild();
			return;
		}

		int count = PhysicsScatterPanel.CandidateCount;
		_run.Text = simulating
			? $"Stop and keep ({PhysicsScatter.Elapsed:0.0} s)"
			: $"Simulate {count} object{(count == 1 ? "" : "s")}";

		var text = PhysicsScatter.Status;
		if ( text != _shownText )
		{
			_shownText = text;
			_status.Text = text;
			_status.Visible = !string.IsNullOrEmpty( text );
			AdjustSize();
		}
	}

	/// <summary>Sit on top of this viewport's collision panel, else its sculpt panel, else the corner.</summary>
	public void Follow()
	{
		bool visible = _viewport.Visible;
		if ( Visible != visible )
			Visible = visible;

		if ( !visible )
			return;

		var target = _viewport.ScreenPosition + _viewport.Size - Size - Inset;

		Widget below = CollisionPanel.WidgetFor( _viewport );
		if ( below is null || !below.Visible )
			below = SculptPanel.WidgetFor( _viewport );
		if ( below is not null && below.Visible )
			target.y = below.Position.y - Size.y - Gap;

		if ( Position != target )
			Position = target;
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.WindowBackground.WithAlpha( 0.9f ) );
		Paint.DrawRect( LocalRect, Theme.ControlRadius );
	}
}
