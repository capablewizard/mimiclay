using System;
using System.Collections.Generic;
using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// The collision panel: a small window stacked directly ABOVE the <see cref="SculptPanel"/> in every scene
/// viewport. Its header button switches the collision debug view on (<see cref="SdfCollisionDebug"/> — every
/// <see cref="SdfCollider"/>'s real physics shapes drawn over the clay, previewed in the editor and live in play);
/// while on, it shows the view's options and a colour legend. Hide it from Editor → Mimiclay → Collision Panel.
/// Everything persists via editor cookies. Same frameless-tool-window arrangement as the sculpt panel.
/// </summary>
public static class CollisionPanel
{
	const string EnabledCookie = "Mimiclay.CollisionPanel.Enabled";
	const string ViewCookie = "Mimiclay.CollisionPanel.View";
	const string SelectedOnlyCookie = "Mimiclay.CollisionPanel.SelectedOnly";
	const string ThroughClayCookie = "Mimiclay.CollisionPanel.ThroughClay";
	const string ByBrushCookie = "Mimiclay.CollisionPanel.ByBrush";
	const string MoveHullCookie = "Mimiclay.CollisionPanel.MoveHull";
	const string StatsCookie = "Mimiclay.CollisionPanel.Stats";
	const string MissingCookie = "Mimiclay.CollisionPanel.Missing";

	[Menu( "Editor", "Mimiclay/Collision Panel", "view_in_ar" )]
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

	// The view's switches live on SdfCollisionDebug (game assembly, read by SdfCollider.DrawGizmos); the panel
	// owns their persistence. Loaded from cookies once per editor session.
	public static bool View
	{
		get => SdfCollisionDebug.Enabled;
		set { SdfCollisionDebug.Enabled = value; EditorCookie.Set( ViewCookie, value ); }
	}

	public static bool SelectedOnly
	{
		get => SdfCollisionDebug.SelectedOnly;
		set { SdfCollisionDebug.SelectedOnly = value; EditorCookie.Set( SelectedOnlyCookie, value ); }
	}

	public static bool ThroughClay
	{
		get => SdfCollisionDebug.ThroughClay;
		set { SdfCollisionDebug.ThroughClay = value; EditorCookie.Set( ThroughClayCookie, value ); }
	}

	public static bool ColourByBrush
	{
		get => SdfCollisionDebug.ColourByBrush;
		set { SdfCollisionDebug.ColourByBrush = value; EditorCookie.Set( ByBrushCookie, value ); }
	}

	public static bool MoveHull
	{
		get => SdfCollisionDebug.MoveHull;
		set { SdfCollisionDebug.MoveHull = value; EditorCookie.Set( MoveHullCookie, value ); }
	}

	public static bool Stats
	{
		get => SdfCollisionDebug.Stats;
		set { SdfCollisionDebug.Stats = value; EditorCookie.Set( StatsCookie, value ); }
	}

	public static bool FlagMissing
	{
		get => SdfCollisionDebug.FlagMissing;
		set { SdfCollisionDebug.FlagMissing = value; EditorCookie.Set( MissingCookie, value ); }
	}

	static bool _togglesLoaded;

	static readonly Dictionary<SceneViewportWidget, CollisionPanelWidget> _widgets = new();

	[EditorEvent.Frame]
	public static void Tick()
	{
		if ( !_togglesLoaded )
		{
			_togglesLoaded = true;
			SdfCollisionDebug.Enabled = EditorCookie.Get( ViewCookie, false );
			SdfCollisionDebug.SelectedOnly = EditorCookie.Get( SelectedOnlyCookie, false );
			SdfCollisionDebug.ThroughClay = EditorCookie.Get( ThroughClayCookie, true );
			SdfCollisionDebug.ColourByBrush = EditorCookie.Get( ByBrushCookie, false );
			SdfCollisionDebug.MoveHull = EditorCookie.Get( MoveHullCookie, false );
			SdfCollisionDebug.Stats = EditorCookie.Get( StatsCookie, true );
			SdfCollisionDebug.FlagMissing = EditorCookie.Get( MissingCookie, true );
		}

		// The view's "selected only" filter can't reach the gizmo selection from the game assembly — feed it.
		SdfCollisionDebug.Selection.Clear();
		if ( SdfCollisionDebug.Enabled && SceneEditorSession.Active?.Selection is { } selection )
			SdfCollisionDebug.Selection.AddRange( selection.OfType<GameObject>().Where( go => go.IsValid() ) );

		if ( !Enabled )
		{
			DestroyAll();
			return;
		}

		Sync();
	}

	/// <summary>One panel per viewport of the active scene view, parked on top of that viewport's sculpt panel.</summary>
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
				widget = new CollisionPanelWidget( viewport );
				_widgets[viewport] = widget;
			}

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

	[ConCmd( "mimi_collisionpanel" )]
	public static void EnabledCmd( bool on ) => Enabled = on;
}

internal class CollisionPanelWidget : Widget
{
	static readonly Vector2 Inset = new( 8, 8 );
	const float Gap = 4f;
	const float PanelWidth = 230f;

	readonly SceneViewportWidget _viewport;

	public CollisionPanelWidget( SceneViewportWidget viewport ) : base( viewport )
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

		bool on = CollisionPanel.View;

		// Header: the view's on/off button (lit while on), title, close.
		var header = Layout.AddRow();
		header.Spacing = 4;
		header.Add( new IconButton( "view_in_ar", () =>
		{
			CollisionPanel.View = !CollisionPanel.View;
			CollisionPanel.RebuildAll();
		} )
		{
			ToolTip = on ? "Hide collision shapes" : "Show collision shapes (the real physics shapes of every SDF Collider)",
			Background = on ? Theme.Primary.WithAlpha( 0.6f ) : Color.Transparent,
		} );
		header.Add( new Label( "Collision" ) { Alignment = TextFlag.LeftCenter } );
		header.AddStretchCell();
		header.Add( new IconButton( "close", () => CollisionPanel.Enabled = false )
		{ ToolTip = "Hide (Editor → Mimiclay → Collision Panel brings it back)", Background = Color.Transparent } );

		if ( on )
		{
			AddToggle( "Selected only", CollisionPanel.SelectedOnly, v => CollisionPanel.SelectedOnly = v,
				"Only the selected sculptures (selecting one of its brushes counts)" );
			AddToggle( "Through clay", CollisionPanel.ThroughClay, v => CollisionPanel.ThroughClay = v,
				"Draw the shapes on top of the clay instead of depth-testing them" );
			AddToggle( "Colour by brush", CollisionPanel.ColourByBrush, v => CollisionPanel.ColourByBrush = v,
				"One colour per source brush instead of per shape kind (the legend below)" );
			AddToggle( "Move hull", CollisionPanel.MoveHull, v => CollisionPanel.MoveHull = v,
				"Prop pawns: the convex hull their movement sweeps, plus the ground contact (play only)" );
			AddToggle( "Shape counts", CollisionPanel.Stats, v => CollisionPanel.Stats = v,
				"Hull / sphere / voxel-box counts next to each sculpture, and any hull physics rejected" );
			AddToggle( "Flag uncollided brushes", CollisionPanel.FlagMissing, v => CollisionPanel.FlagMissing = v,
				"Label additive brushes that produced no collision (too small, or carved away)" );

			var legend = Layout.AddRow();
			legend.Spacing = 8;
			legend.Add( Swatch( "Hull", SdfCollisionDebug.HullColor ) );
			legend.Add( Swatch( "Sphere", SdfCollisionDebug.SphereColor ) );
			legend.Add( Swatch( "Voxel box", SdfCollisionDebug.VoxelColor ) );
			legend.AddStretchCell();
		}

		FixedWidth = PanelWidth;
		AdjustSize();
	}

	void AddToggle( string title, bool value, Action<bool> set, string tooltip )
	{
		var cb = new Checkbox( title ) { Value = value, ToolTip = tooltip };
		cb.Toggled = () => set( cb.Value );
		Layout.Add( cb );
	}

	static Label Swatch( string text, Color color )
	{
		var label = new Label( $"■ {text}" );
		label.SetStyles( $"color: {color.Hex};" );
		return label;
	}

	/// <summary>Sit right on top of this viewport's sculpt panel (or in the corner when it's hidden).</summary>
	public void Follow()
	{
		bool visible = _viewport.Visible;
		if ( Visible != visible )
			Visible = visible;

		if ( !visible )
			return;

		var target = _viewport.ScreenPosition + _viewport.Size - Size - Inset;
		var sculpt = SculptPanel.WidgetFor( _viewport );
		if ( sculpt is not null && sculpt.Visible )
			target.y = sculpt.Position.y - Size.y - Gap;

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
