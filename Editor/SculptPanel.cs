using System;
using System.Collections.Generic;
using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// The sculpt panel: a small always-present window pinned to the BOTTOM-RIGHT of every scene viewport,
/// whatever is selected. It holds the two form conversions (convert the selected list-form sculpture to brush
/// objects / collapse the selected brush objects back to a list), an Add row (operation picker + one button per
/// shape, placed after the selected brush), Add Point for splines, and the three display toggles for brush
/// objects (shapes, wires over the clay, hover ghosts). Minimise with the header button; hide with the close
/// button and bring it back from Editor → Mimiclay → Sculpt Panel. Everything persists via editor cookies.
/// <para>Same Qt arrangement as <see cref="FpsOverlay"/>: the viewport is a native surface that paints over any
/// managed sibling, so this is a frameless top-level tool window re-parked on the viewport every frame.</para>
/// </summary>
public static class SculptPanel
{
	const string EnabledCookie = "Mimiclay.SculptPanel.Enabled";
	const string MinimisedCookie = "Mimiclay.SculptPanel.Minimised";
	const string ShapesCookie = "Mimiclay.SculptPanel.Shapes";
	const string WiresCookie = "Mimiclay.SculptPanel.WiresOverSdf";
	const string GhostsCookie = "Mimiclay.SculptPanel.Ghosts";
	const string OperationCookie = "Mimiclay.SculptPanel.Operation";

	/// <summary>Checkable menu entry — a [Menu] on a static bool property renders as a toggle.</summary>
	[Menu( "Editor", "Mimiclay/Sculpt Panel", "category" )]
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

	public static bool Minimised
	{
		get => _minimised ??= EditorCookie.Get( MinimisedCookie, false );
		set { _minimised = value; EditorCookie.Set( MinimisedCookie, value ); }
	}
	static bool? _minimised;

	/// <summary>The operation the Add row's shape buttons use. Persisted.</summary>
	public static SdfOperation Operation
	{
		get => _operation ??= (SdfOperation)EditorCookie.Get( OperationCookie, (int)SdfOperation.Add );
		set { _operation = value; EditorCookie.Set( OperationCookie, (int)value ); }
	}
	static SdfOperation? _operation;

	// The three display toggles live on SdfBrushComponent (game assembly, read by DrawGizmos); the panel owns
	// their persistence. Loaded from cookies once per editor session (statics survive hotloads).
	public static bool ShowShapes
	{
		get => SdfBrushComponent.GizmosVisible;
		set { SdfBrushComponent.GizmosVisible = value; EditorCookie.Set( ShapesCookie, value ); }
	}

	public static bool WiresOverSdf
	{
		get => SdfBrushComponent.WiresOverSdf;
		set { SdfBrushComponent.WiresOverSdf = value; EditorCookie.Set( WiresCookie, value ); }
	}

	public static bool HoverGhosts
	{
		get => SdfBrushComponent.HoverGhosts;
		set { SdfBrushComponent.HoverGhosts = value; EditorCookie.Set( GhostsCookie, value ); }
	}

	static bool _togglesLoaded;

	static readonly Dictionary<SceneViewportWidget, SculptPanelWidget> _widgets = new();

	/// <summary>What the current selection allows, recomputed every editor frame.</summary>
	internal readonly record struct SelectionState( bool Convert, bool Collapse, bool AddShape, bool AddPoint, bool Relax );

	[EditorEvent.Frame]
	public static void Tick()
	{
		if ( !_togglesLoaded )
		{
			_togglesLoaded = true;
			SdfBrushComponent.GizmosVisible = EditorCookie.Get( ShapesCookie, true );
			SdfBrushComponent.WiresOverSdf = EditorCookie.Get( WiresCookie, true );
			SdfBrushComponent.HoverGhosts = EditorCookie.Get( GhostsCookie, true );
		}

		if ( !Enabled )
		{
			DestroyAll();
			return;
		}

		Sync( ReadSelection() );
	}

	// Convert: a list-form sculpture with brushes is selected. Collapse: an object-form sculpture, or any of its
	// brush objects / spline points, is selected. Add shape: an object-form sculpture is reachable from the
	// selection. Add point: one spline point, or two neighbouring ones, are selected.
	static SelectionState ReadSelection()
	{
		var selection = SceneEditorSession.Active?.Selection;
		if ( selection is null )
			return default;

		bool convert = false, collapse = false;
		foreach ( var go in selection.OfType<GameObject>() )
		{
			if ( !go.IsValid() )
				continue;

			var sculpt = go.Components.GetInAncestorsOrSelf<SdfSculpture>( true );
			if ( !sculpt.IsValid() )
				continue;

			if ( sculpt.BrushObjects )
				collapse = true;
			else if ( sculpt.Brushes is { Count: > 0 } )
				convert = true;
		}

		bool addShape = SdfObjectEditing.AddShapeTarget().Sculpt.IsValid();
		bool addPoint = SdfObjectEditing.AddPointTargets().A.IsValid();
		bool relax = SdfObjectEditing.RelaxTargets().Count > 0;
		return new SelectionState( convert, collapse, addShape, addPoint, relax );
	}

	/// <summary>One panel per viewport of the active scene view, parked in its bottom-right corner.</summary>
	static void Sync( SelectionState state )
	{
		var viewports = SceneViewWidget.Current?._viewports;

		// Drop panels whose viewport died or belongs to a scene tab that is no longer current (top-level
		// windows don't follow their tab being hidden). Rebuilt when the tab comes back.
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
				widget = new SculptPanelWidget( viewport );
				_widgets[viewport] = widget;
			}

			widget.SetSelectionState( state );
			widget.Follow();
		}
	}

	static void DestroyAll()
	{
		foreach ( var widget in _widgets.Values )
			widget?.Destroy();

		_widgets.Clear();
	}

	// The panel widgets survive a hotload with whatever layout they were built with; rebuild them so a code
	// change to the panel shows up without toggling it.
	[EditorEvent.Hotload]
	static void OnHotload() => RebuildAll();

	/// <summary>Rebuild every panel's contents (after minimise / restore).</summary>
	internal static void RebuildAll()
	{
		foreach ( var widget in _widgets.Values )
			widget?.Rebuild();
	}

	[ConCmd( "mimi_sculptpanel" )]
	public static void EnabledCmd( bool on ) => Enabled = on;
}

internal class SculptPanelWidget : Widget
{
	static readonly Vector2 Inset = new( 8, 8 );
	const float PanelWidth = 230f;

	// The Add row's shapes, in dock order, with a Material icon each.
	static readonly (SdfShape Shape, string Icon, string Tip)[] Shapes =
	{
		(SdfShape.Sphere, "circle", "Sphere"),
		(SdfShape.Box, "crop_square", "Box"),
		(SdfShape.Cylinder, "panorama_vertical", "Cylinder"),
		(SdfShape.Cone, "change_history", "Cone"),
		(SdfShape.Extruded, "pentagon", "Extruded profile"),
		(SdfShape.Text, "title", "Text"),
		(SdfShape.Spline, "gesture", "Spline"),
	};

	static readonly (SdfOperation Op, string Icon, string Tip)[] Operations =
	{
		(SdfOperation.Add, "add", "Add"),
		(SdfOperation.Subtract, "remove", "Subtract"),
		(SdfOperation.Cutout, "content_cut", "Cutout"),
		(SdfOperation.Colour, "palette", "Colour"),
	};

	readonly SceneViewportWidget _viewport;

	Button _convert;
	Button _collapse;
	Button _addPoint;
	Button _relax;
	readonly List<IconButton> _shapeButtons = new();
	SculptPanel.SelectionState _state;

	public SculptPanelWidget( SceneViewportWidget viewport ) : base( viewport )
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
		// Reuse the layout on a rebuild: assigning a fresh one to a widget that has one makes Qt warn.
		if ( Layout is null )
			Layout = Layout.Column();
		else
			Layout.Clear( true );
		_shapeButtons.Clear();
		_convert = _collapse = _addPoint = _relax = null;

		Layout.Margin = 6;
		Layout.Spacing = 4;

		bool minimised = SculptPanel.Minimised;

		// Header: title, minimise / restore, close.
		var header = Layout.AddRow();
		header.Spacing = 4;
		header.Add( new Label( "Sculpt" ) { Alignment = TextFlag.LeftCenter } );
		header.AddStretchCell();
		header.Add( new IconButton( minimised ? "expand_less" : "expand_more", () =>
		{
			SculptPanel.Minimised = !SculptPanel.Minimised;
			SculptPanel.RebuildAll();
		} )
		{ ToolTip = minimised ? "Restore" : "Minimise", Background = Color.Transparent } );
		header.Add( new IconButton( "close", () => SculptPanel.Enabled = false )
		{ ToolTip = "Hide (Editor → Mimiclay → Sculpt Panel brings it back)", Background = Color.Transparent } );

		if ( !minimised )
		{
			_convert = Layout.Add( new Button( "Convert To Brush Objects", "auto_fix_high" )
			{
				Clicked = SdfObjectEditing.ExplodeSelectedCmd,
				ToolTip = "Turn the selected list-form sculpture's brushes into child brush objects",
			} );
			_collapse = Layout.Add( new Button( "Collapse To Brush List", "list" )
			{
				Clicked = SdfObjectEditing.CollapseSelectedCmd,
				ToolTip = "Gather the selected sculpture's brush objects back into its brush list",
			} );

			Layout.AddSpacingCell( 2 );

			// Add row: operation picker, then one icon button per shape. The new brush lands right after the
			// selected brush (or above the last one) and becomes the selection, ready to move.
			var addRow = Layout.AddRow();
			addRow.Spacing = 2;
			var op = new ComboBox { ToolTip = "Operation for the shapes added with the buttons" };
			foreach ( var (value, icon, tip) in Operations )
				op.AddItem( tip, icon, () => SculptPanel.Operation = value, selected: value == SculptPanel.Operation );
			op.FixedWidth = 92f;
			addRow.Add( op );
			foreach ( var (shape, icon, tip) in Shapes )
			{
				var button = new IconButton( icon, () => SdfObjectEditing.AddShape( shape, SculptPanel.Operation ) )
				{
					ToolTip = $"Add {tip}",
					FixedWidth = 22f,
					FixedHeight = 22f,
					IconSize = 14f,
				};
				_shapeButtons.Add( addRow.Add( button ) );
			}

			_addPoint = Layout.Add( new Button( "Add Spline Point", "add_location_alt" )
			{
				Clicked = SdfObjectEditing.AddPoint,
				ToolTip = "Select one point to add after it, or two neighbouring points to add between them",
			} );
			_relax = Layout.Add( new Button( "Relax Spline", "waves" )
			{
				Clicked = SdfObjectEditing.Relax,
				ToolTip = "Smooth the selected spline: each point moves halfway toward the midpoint of its neighbours (ends stay). Click again for more.",
			} );

			Layout.AddSpacingCell( 2 );

			AddToggle( "Show shapes", SculptPanel.ShowShapes, v => SculptPanel.ShowShapes = v,
				"Draw brush outlines and make them clickable" );
			AddToggle( "Wires over clay", SculptPanel.WiresOverSdf, v => SculptPanel.WiresOverSdf = v,
				"Draw the outlines on top of the clay instead of depth-testing them" );
			AddToggle( "Hover ghosts", SculptPanel.HoverGhosts, v => SculptPanel.HoverGhosts = v,
				"Fill the hovered brush with a translucent ghost of its shape" );
		}

		ApplySelectionState();
		FixedWidth = PanelWidth;
		AdjustSize();
	}

	void AddToggle( string title, bool value, Action<bool> set, string tooltip )
	{
		var cb = new Checkbox( title ) { Value = value, ToolTip = tooltip };
		cb.Toggled = () => set( cb.Value );
		Layout.Add( cb );
	}

	public void SetSelectionState( SculptPanel.SelectionState state )
	{
		if ( _state == state )
			return;

		_state = state;
		ApplySelectionState();
	}

	void ApplySelectionState()
	{
		if ( _convert.IsValid() ) _convert.Enabled = _state.Convert;
		if ( _collapse.IsValid() ) _collapse.Enabled = _state.Collapse;
		if ( _addPoint.IsValid() ) _addPoint.Enabled = _state.AddPoint;
		if ( _relax.IsValid() ) _relax.Enabled = _state.Relax;
		foreach ( var b in _shapeButtons )
			if ( b.IsValid() ) b.Enabled = _state.AddShape;
	}

	/// <summary>Track the viewport's screen rect (docking, resizing, moving the editor window all shift it).</summary>
	public void Follow()
	{
		bool visible = _viewport.Visible;
		if ( Visible != visible )
			Visible = visible;

		if ( !visible )
			return;

		var target = _viewport.ScreenPosition + _viewport.Size - Size - Inset;
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
