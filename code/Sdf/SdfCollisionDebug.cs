using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The collision debug view: draws every <see cref="SdfCollider"/>'s ACTUAL physics shapes over the clay — the
/// engine's own hull edges and spheres from the built <see cref="Model.Physics"/>, not a re-derivation from the
/// brushes — coloured by where each shape came from (exact hull / exact sphere / carve voxel box, or per source
/// brush), with optional foot probes, per-sculpture stats and labels on brushes that got no collider at all.
///
/// Driven from <see cref="SdfCollider"/>'s DrawGizmos. In PLAY it shows the live collider the component built.
/// In the EDITOR the component never runs (physics is a play concern), so this builds a private preview with
/// the same builder + brush selection + probe frame — identical to what play will build for the shape on
/// screen — cached on the shape's content hash and throttled while it changes (a gizmo drag).
///
/// The switches are statics so both assemblies see them: the editor's Collision panel owns their persistence
/// (see <c>CollisionPanel</c>); <c>mimi_collision_view</c> toggles the master in game.
/// </summary>
public static class SdfCollisionDebug
{
	/// <summary>Master switch — off = nothing drawn, nothing built.</summary>
	public static bool Enabled;

	/// <summary>Only the selected sculptures (or the sculptures whose brushes are selected).</summary>
	public static bool SelectedOnly;

	/// <summary>Draw the shapes on top of the clay instead of depth-testing them.</summary>
	public static bool ThroughClay = true;

	/// <summary>Colour each shape by its source brush instead of by its kind.</summary>
	public static bool ColourByBrush;

	/// <summary>Draw the ground-probe foot points the player controller traces from.</summary>
	public static bool FootProbes;

	/// <summary>Per-sculpture shape counts next to each sculpture.</summary>
	public static bool Stats = true;

	/// <summary>Label additive brushes that produced no collision shape (detail-gated, or carved away).</summary>
	public static bool FlagMissing = true;

	public static readonly Color HullColor = new( 0.37f, 0.85f, 0.55f );
	public static readonly Color SphereColor = new( 0.31f, 0.76f, 0.97f );
	public static readonly Color VoxelColor = new( 1f, 0.72f, 0.30f );
	public static readonly Color MissingColor = new( 1f, 0.36f, 0.36f );
	public static readonly Color ProbeColor = new( 1f, 0.95f, 0.35f );

	// Editor previews rebuild at most this often while the shape keeps changing (a drag rebuilds per frame
	// otherwise, and a carved build samples the field per voxel).
	const float EditorRebuildInterval = 0.15f;

	/// <summary>Everything one draw needs, derived once per built model.</summary>
	public sealed class Snapshot
	{
		internal int Key;
		internal Model Model;          // reference identity: a runtime rebuild swaps it
		internal RealTimeSince Built;

		internal readonly List<(Line[] Lines, SdfCollisionBuilder.ShapeKind? Kind, int Brush)> Hulls = new();
		internal readonly List<(Sphere Sphere, int Brush)> Spheres = new();
		internal Vector3[] Feet = Array.Empty<Vector3>();
		internal readonly List<(Vector3 Position, string Text)> Missing = new();
		internal int VoxelBoxes, DroppedHulls;
	}

	[ConCmd( "mimi_collision_view" )]
	public static void ViewCmd( bool on ) => Enabled = on;

	/// <summary>Draw <paramref name="collider"/>'s collision. Called inside its gizmo scope (sculpture-local space,
	/// scale included — exactly the space the collider's shapes live in).</summary>
	public static void Draw( SdfCollider collider )
	{
		if ( !Enabled || !collider.IsValid() || SdfThumbnail.IsThumbnailScene( collider.Scene ) )
			return;
		if ( SelectedOnly && !IsSelected( collider.GameObject ) )
			return;

		var snap = collider.Scene.IsEditor ? EditorSnapshot( collider ) : RuntimeSnapshot( collider );
		if ( snap is null )
			return;

		Gizmo.Draw.IgnoreDepth = ThroughClay;
		Gizmo.Draw.LineThickness = 1.5f;

		foreach ( var (lines, kind, brush) in snap.Hulls )
		{
			Gizmo.Draw.Color = ShapeColor( kind, brush ).WithAlpha( 0.9f );
			Gizmo.Draw.Lines( lines );
		}

		foreach ( var (sphere, brush) in snap.Spheres )
		{
			Gizmo.Draw.Color = ShapeColor( SdfCollisionBuilder.ShapeKind.Sphere, brush ).WithAlpha( 0.9f );
			Gizmo.Draw.LineSphere( sphere, 6 );
		}

		if ( FootProbes && snap.Feet.Length > 0 )
		{
			Gizmo.Draw.Color = ProbeColor;
			Gizmo.Draw.LineThickness = 2f;
			foreach ( var p in snap.Feet )
			{
				Gizmo.Draw.SolidSphere( p, 0.8f, 6, 6 );
				Gizmo.Draw.Line( p, p + Vector3.Down * 4f );
			}
		}

		Gizmo.Draw.IgnoreDepth = false;

		var world = collider.WorldTransform;
		if ( Stats )
		{
			Gizmo.Draw.Color = Color.White.WithAlpha( 0.9f );
			int exactHulls = snap.Hulls.Count - snap.VoxelBoxes;
			var text = snap.Model is null
				? $"{collider.GameObject.Name}: no collider"
				: $"{collider.GameObject.Name}: {exactHulls} hull · {snap.Spheres.Count} sphere · {snap.VoxelBoxes} voxel box";
			if ( snap.DroppedHulls > 0 )
				text += $" · {snap.DroppedHulls} hull REJECTED by physics";
			Gizmo.Draw.ScreenText( text, world.Position, new Vector2( 12f, -24f ), size: 12f );
		}

		if ( FlagMissing )
		{
			Gizmo.Draw.Color = MissingColor;
			foreach ( var (pos, text) in snap.Missing )
				Gizmo.Draw.ScreenText( text, world.PointToWorld( pos ), new Vector2( 8f, -6f ), size: 11f );
		}
	}

	static Color ShapeColor( SdfCollisionBuilder.ShapeKind? kind, int brush )
	{
		if ( ColourByBrush && brush >= 0 )
			return new ColorHsv( (brush * 137.508f) % 360f, 0.65f, 1f ).ToColor(); // golden-angle hues

		return kind switch
		{
			SdfCollisionBuilder.ShapeKind.Sphere => SphereColor,
			SdfCollisionBuilder.ShapeKind.VoxelBox => VoxelColor,
			SdfCollisionBuilder.ShapeKind.Hull => HullColor,
			_ => Color.White, // the engine's shape list didn't line up with what was emitted — still the real shape
		};
	}

	/// <summary>Editor: the selected GameObjects, refreshed every editor frame by the editor assembly (the gizmo
	/// selection isn't reachable from here).</summary>
	[SkipHotload] public static readonly List<GameObject> Selection = new();

	// The sculpture itself, or anything under it (a brush object, a spline point), is selected.
	static bool IsSelected( GameObject go )
	{
		foreach ( var sel in Selection )
			for ( var g = sel; g.IsValid(); g = g.Parent )
				if ( g == go )
					return true;
		return false;
	}

	// PLAY: the component's own build — what physics is actually simulating.
	static Snapshot RuntimeSnapshot( SdfCollider collider )
	{
		var model = collider.BuiltModel;
		var snap = collider.DebugSnapshot;
		if ( snap is not null && snap.Model == model && snap.Model is not null )
			return snap;

		snap = Derive( model, collider.EmittedShapes, collider.FootPoints, collider.CollisionBrushes() );
		collider.DebugSnapshot = snap;
		return snap;
	}

	// EDITOR: a private preview build, same inputs Rebuild uses (brush selection, probe spacing, probe frame).
	static Snapshot EditorSnapshot( SdfCollider collider )
	{
		var brushes = collider.CollisionBrushes();
		var probeFrame = SdfCollider.ProbeFrame( collider.WorldRotation );
		int key = HashCode.Combine(
			SdfSculpture.ContentHashPrefix( brushes, brushes?.Count ?? 0, 0, false ),
			probeFrame, collider.FootProbeSpacing );

		var snap = collider.DebugSnapshot;
		if ( snap is not null && (snap.Key == key || snap.Built < EditorRebuildInterval) )
			return snap;

		var emitted = new List<SdfCollisionBuilder.EmittedShape>();
		var carved = new List<SdfCollisionBuilder.CarvedCopy>();
		var model = SdfCollisionBuilder.Build( brushes, carved, null, emitted );

		List<Vector3> feet = null;
		try
		{
			if ( brushes is not null )
				feet = SdfCollisionBuilder.ComputeFootPoints( brushes, collider.FootProbeSpacing, carved, probeFrame );
		}
		catch
		{
			// Probes are a side view here, same as at runtime — never let them stop the shapes drawing.
		}

		snap = Derive( model, emitted, feet, brushes );
		snap.Key = key;
		collider.DebugSnapshot = snap;
		return snap;
	}

	static Snapshot Derive( Model model, IReadOnlyList<SdfCollisionBuilder.EmittedShape> emitted,
		IReadOnlyList<Vector3> feet, List<SdfBrush> brushes )
	{
		var snap = new Snapshot { Model = model, Built = 0f };
		snap.Feet = feet?.ToArray() ?? Array.Empty<Vector3>();

		var hullTags = emitted.Where( e => e.Kind != SdfCollisionBuilder.ShapeKind.Sphere ).ToList();
		var sphereTags = emitted.Where( e => e.Kind == SdfCollisionBuilder.ShapeKind.Sphere ).ToList();

		var physics = model?.Physics;
		if ( physics is not null )
		{
			var hulls = new List<(Transform, PhysicsGroupDescription.BodyPart.HullPart)>();
			var spheres = new List<(Transform, PhysicsGroupDescription.BodyPart.SpherePart)>();
			foreach ( var part in physics.Parts )
			{
				foreach ( var h in part.Hulls ) hulls.Add( (part.Transform, h) );
				foreach ( var s in part.Spheres ) spheres.Add( (part.Transform, s) );
			}

			// Emission order is the engine's order, so tags map 1:1 — unless physics rejected a degenerate hull,
			// in which case the kinds can't be trusted and everything draws neutral (with the count reported).
			bool hullsAlign = hulls.Count == hullTags.Count;
			bool spheresAlign = spheres.Count == sphereTags.Count;
			snap.DroppedHulls = Math.Max( 0, hullTags.Count - hulls.Count );

			for ( int i = 0; i < hulls.Count; i++ )
			{
				var (tx, h) = hulls[i];
				var lines = h.GetLines().Select( l => new Line( tx.PointToWorld( l.Start ), tx.PointToWorld( l.End ) ) ).ToArray();
				var tag = hullsAlign ? hullTags[i] : default;
				snap.Hulls.Add( (lines, hullsAlign ? tag.Kind : null, hullsAlign ? tag.BrushIndex : -1) );
				if ( hullsAlign && tag.Kind == SdfCollisionBuilder.ShapeKind.VoxelBox )
					snap.VoxelBoxes++;
			}

			for ( int i = 0; i < spheres.Count; i++ )
			{
				var (tx, s) = spheres[i];
				var sphere = new Sphere( tx.PointToWorld( s.Sphere.Center ), s.Sphere.Radius * tx.UniformScale );
				snap.Spheres.Add( (sphere, spheresAlign ? sphereTags[i].BrushIndex : -1) );
			}
		}

		// Additive brushes that emitted nothing: say why at the brush. Indices are into the flattened list,
		// the same one Build walked.
		if ( brushes is { Count: > 0 } )
		{
			var flat = SdfGroups.Flatten( brushes );
			var gated = SdfCollisionBuilder.DetailGated( flat );
			var hit = new HashSet<int>( emitted.Select( e => e.BrushIndex ) );
			for ( int i = 0; i < flat.Count; i++ )
			{
				var b = flat[i];
				if ( !b.Enabled || b.Operation != SdfOperation.Add || hit.Contains( i ) )
					continue;

				var why = gated[i] ? "detail (< 10u on every axis)" : "carved away";
				var at = b.Shape == SdfShape.Spline && b.Points is { Count: > 0 } ? new Vector3( b.Points[0].x, b.Points[0].y, b.Points[0].z ) : b.Position;
				snap.Missing.Add( (at, $"#{i} {b.Shape}: no collision — {why}") );
			}
		}

		return snap;
	}
}
