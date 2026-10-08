using System;
using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// Radius handle for a selected spline control point (<see cref="SdfSplinePoint"/>): a camera-facing ring you
/// drag in or out. Position comes from the native gizmo like any object.
/// </summary>
public class SdfSplinePointTool : EditorTool<SdfSplinePoint>
{
	IDisposable _undo;

	public override void OnUpdate()
	{
		AllowGameObjectSelection = true;

		if ( !Gizmo.Pressed.Any )
		{
			_undo?.Dispose();
			_undo = null;
		}

		var point = GetSelectedComponent<SdfSplinePoint>();
		if ( !point.IsValid() || !SdfBrushComponent.GizmosVisible )
			return;

		var color = BrushWireframes.OpColor( point.Spline?.Operation ?? SdfOperation.Add );

		using ( Gizmo.Scope( "splinepoint", new Transform( point.WorldPosition, Rotation.Identity, 1f ) ) )
		{
			if ( Gizmo.Control.Sphere( "radius", point.Radius, out float r, color ) )
			{
				_undo ??= SceneEditorSession.Active.UndoScope( "Edit Spline Radius" )
					.WithComponentChanges( point )
					.Push();
				point.Radius = Math.Clamp( r, 0.5f, SdfBrush.MaxSplineRadius );
			}

			Gizmo.Draw.Color = Color.White.WithAlpha( 0.8f );
			Gizmo.Draw.ScreenText( $"Radius {point.Radius:0.0}", Gizmo.Transform.Position, new Vector2( 14f, -8f ), size: 11f );
		}
	}
}

/// <summary>
/// Editor-side wrappers for the sculpture's object-form conversions: the same conversions the component runs
/// itself, inside an undo scope that captures the sculpture's whole subtree so Ctrl+Z restores it either way.
/// </summary>
public static class SdfObjectEditing
{
	[EditorEvent.Frame]
	static void WireHandlers()
	{
		SdfSculpture.ExplodeHandler = Explode;
		SdfSculpture.CollapseHandler = Collapse;

		// Where the transform gizmo is this frame: the selected objects' positions. Near them on screen the brush
		// picks use plain scores so the gizmo handles win; elsewhere they out-score every other object.
		var pivots = SdfBrushComponent.GizmoPivots;
		pivots.Clear();
		var selection = SceneEditorSession.Active?.Selection;
		if ( selection is not null )
			foreach ( var go in selection.OfType<GameObject>() )
				if ( go.IsValid() )
					pivots.Add( go.WorldPosition );
	}

	public static bool Explode( SdfSculpture sculpt )
	{
		using var scope = Scope( sculpt, "Convert To Brush Objects" );
		sculpt.ExplodeToObjects();
		return true;
	}

	public static bool Collapse( SdfSculpture sculpt )
	{
		using var scope = Scope( sculpt, "Collapse To Brush List" );
		sculpt.CollapseToList();
		return true;
	}

	// Console entry points (the inspector buttons do the same): act on every selected sculpture.
	[ConCmd( "mimi_sdf_explode" )]
	public static void ExplodeSelectedCmd() => ForSelected( "explode", Explode );

	[ConCmd( "mimi_sdf_collapse" )]
	public static void CollapseSelectedCmd() => ForSelected( "collapse", Collapse );

	static void ForSelected( string verb, Func<SdfSculpture, bool> action )
	{
		var session = SceneEditorSession.Active;
		int n = 0;
		foreach ( var go in session?.Selection.OfType<GameObject>().ToArray() ?? Array.Empty<GameObject>() )
		{
			var sculpt = go.Components.GetInAncestorsOrSelf<SdfSculpture>( true );
			if ( sculpt.IsValid() && action( sculpt ) )
				n++;
		}
		Log.Info( n > 0 ? $"[SDF] {verb}: {n} sculpture(s)." : $"[SDF] {verb}: select a sculpture (or one of its brushes) first." );
	}

	static IDisposable Scope( SdfSculpture sculpt, string name )
	{
		var session = SceneEditorSession.Resolve( sculpt ) ?? SceneEditorSession.Active;
		return session?.UndoScope( name )
			.WithGameObjectChanges( sculpt.GameObject, GameObjectUndoFlags.All )
			.WithComponentChanges( sculpt )
			.Push();
	}

	// ── Adding (the sculpt panel's Add row and Add Point button) ──────────────────────────────────────────

	/// <summary>Where an "add shape" goes: the first object-form sculpture reachable from the selection, plus the
	/// selected brush (or the spline a selected point belongs to) the new brush is placed after, if any.</summary>
	public static (SdfSculpture Sculpt, SdfBrushComponent After) AddShapeTarget()
	{
		var selection = SceneEditorSession.Active?.Selection;
		if ( selection is null )
			return (null, null);

		foreach ( var go in selection.OfType<GameObject>() )
		{
			if ( !go.IsValid() )
				continue;

			var sculpt = go.Components.GetInAncestorsOrSelf<SdfSculpture>( true );
			if ( !sculpt.IsValid() || !sculpt.BrushObjects )
				continue;

			var brush = go.Components.Get<SdfBrushComponent>( true ) ?? go.Components.Get<SdfSplinePoint>( true )?.Spline;
			return (sculpt, brush);
		}

		return (null, null);
	}

	/// <summary>Add a brush object of this shape/operation to the selected sculpture and select it.</summary>
	public static void AddShape( SdfShape shape, SdfOperation operation )
	{
		var (sculpt, after) = AddShapeTarget();
		if ( !sculpt.IsValid() )
		{
			Log.Info( "[SDF] add: select a brush-object sculpture (or one of its brushes) first." );
			return;
		}

		var session = SceneEditorSession.Resolve( sculpt ) ?? SceneEditorSession.Active;
		GameObject go;
		using ( session?.UndoScope( $"Add {shape}" ).WithGameObjectChanges( sculpt.GameObject, GameObjectUndoFlags.All ).Push() )
			go = sculpt.AddBrushObject( shape, operation, after );

		if ( go.IsValid() )
			session?.Selection.Set( go );
	}

	/// <summary>What "add point" would act on: one selected point (insert after it), or two ADJACENT selected
	/// points (insert between them). (null, null) when the selection fits neither.</summary>
	public static (SdfSplinePoint A, SdfSplinePoint B) AddPointTargets()
	{
		var selection = SceneEditorSession.Active?.Selection;
		if ( selection is null )
			return (null, null);

		var points = selection.OfType<GameObject>()
			.Where( g => g.IsValid() )
			.Select( g => g.Components.Get<SdfSplinePoint>( true ) )
			.Where( p => p.IsValid() )
			.Distinct()
			.ToList();

		if ( points.Count == 1 )
			return (points[0], null);
		if ( points.Count == 2 && SdfSplinePoint.AreAdjacent( points[0], points[1], out var first, out var second ) )
			return (first, second);
		return (null, null);
	}

	[ConCmd( "mimi_sdf_add" )]
	public static void AddShapeCmd( string shape, string operation = "Add" )
	{
		if ( !Enum.TryParse<SdfShape>( shape, true, out var s ) || !Enum.TryParse<SdfOperation>( operation, true, out var o ) )
		{
			Log.Warning( $"[SDF] add: unknown shape/operation '{shape}' '{operation}'." );
			return;
		}
		AddShape( s, o );
	}

	[ConCmd( "mimi_sdf_addpoint" )]
	public static void AddPointCmd() => AddPoint();

	/// <summary>The splines a "relax" acts on: every selected spline brush object, plus the spline of every selected point.</summary>
	public static List<GameObject> RelaxTargets()
	{
		var result = new List<GameObject>();
		var selection = SceneEditorSession.Active?.Selection;
		if ( selection is null )
			return result;

		foreach ( var go in selection.OfType<GameObject>() )
		{
			if ( !go.IsValid() )
				continue;

			GameObject spline = null;
			if ( go.Components.Get<SdfBrushComponent>( true ) is { Shape: SdfShape.Spline } )
				spline = go;
			else if ( go.Components.Get<SdfSplinePoint>( true ) is { } point && point.Spline.IsValid() )
				spline = point.Spline.GameObject;

			if ( spline.IsValid() && !result.Contains( spline ) )
				result.Add( spline );
		}

		return result;
	}

	/// <summary>Relax every spline the selection touches (see <see cref="SdfSplinePoint.Relax"/>), one undo step.</summary>
	public static void Relax()
	{
		var targets = RelaxTargets();
		if ( targets.Count == 0 )
		{
			Log.Info( "[SDF] relax: select a spline (or some of its points) first." );
			return;
		}

		var session = SceneEditorSession.Resolve( targets[0] ) ?? SceneEditorSession.Active;
		using ( session?.UndoScope( "Relax Spline" ).WithGameObjectChanges( targets, GameObjectUndoFlags.All ).Push() )
		{
			foreach ( var spline in targets )
				SdfSplinePoint.Relax( spline );
		}
	}

	[ConCmd( "mimi_sdf_relax" )]
	public static void RelaxCmd() => Relax();

	/// <summary>Insert a spline point for the current selection (see <see cref="AddPointTargets"/>) and select it.</summary>
	public static void AddPoint()
	{
		var (a, b) = AddPointTargets();
		if ( !a.IsValid() )
		{
			Log.Info( "[SDF] add point: select one spline point, or two neighbouring ones, first." );
			return;
		}

		var sculpt = a.Spline?.Owner;
		var root = sculpt.IsValid() ? sculpt.GameObject : a.GameObject.Parent;
		var session = SceneEditorSession.Resolve( a ) ?? SceneEditorSession.Active;

		GameObject go;
		using ( session?.UndoScope( "Add Spline Point" ).WithGameObjectChanges( root, GameObjectUndoFlags.All ).Push() )
			go = b.IsValid() ? SdfSplinePoint.InsertBetween( a, b ) : SdfSplinePoint.InsertAfter( a );

		if ( go.IsValid() )
			session?.Selection.Set( go );
	}
}
