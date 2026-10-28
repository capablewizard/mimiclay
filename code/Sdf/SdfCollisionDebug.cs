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
/// Two draw paths over one <see cref="Snapshot"/>:
///  • EDITOR scene — gizmos, from <see cref="SdfCollider"/>'s DrawGizmos. The component never runs there (physics
///    is a play concern), so this builds a private preview with the same builder + brush selection + probe frame
///    — identical to what play will build for the shape on screen — cached on the shape's content hash and
///    throttled while it changes (a gizmo drag).
///  • PLAY — the game camera never draws gizmos, so <see cref="SdfCollisionDebugSystem"/> renders the LIVE
///    collider the component built as one line-list scene object per collider, plus overlay text.
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
		internal readonly List<(Capsule Capsule, int Brush)> Capsules = new();
		internal Vector3[] Feet = Array.Empty<Vector3>();
		internal readonly List<(Vector3 Position, string Text)> Missing = new();
		internal int VoxelBoxes, DroppedHulls;

		// Play path: every shape as sculpture-local line-list vertices, built once per option set that shapes them.
		Vertex[] _lines;
		(bool ByBrush, bool Probes) _linesMode;

		internal Vertex[] LineVertices()
		{
			if ( _lines is not null && _linesMode == (ColourByBrush, FootProbes) )
				return _lines;

			var verts = new List<Vertex>();
			void Seg( Vector3 a, Vector3 b, Color c )
			{
				verts.Add( new Vertex( a, c ) );
				verts.Add( new Vertex( b, c ) );
			}

			foreach ( var (lines, kind, brush) in Hulls )
			{
				var c = ShapeColor( kind, brush );
				foreach ( var l in lines )
					Seg( l.Start, l.End, c );
			}

			// Three great circles per sphere — reads as a sphere from any angle.
			const int n = 16;
			foreach ( var (sphere, brush) in Spheres )
			{
				var c = ShapeColor( SdfCollisionBuilder.ShapeKind.Sphere, brush );
				for ( int axis = 0; axis < 3; axis++ )
				for ( int i = 0; i < n; i++ )
				{
					float a0 = MathF.Tau * i / n, a1 = MathF.Tau * (i + 1) / n;
					Seg( sphere.Center + Circle( axis, a0 ) * sphere.Radius, sphere.Center + Circle( axis, a1 ) * sphere.Radius, c );
				}
			}

			// Capsules: both end spheres' great circles plus four lines down the sides.
			foreach ( var (cap, brush) in Capsules )
			{
				var c = ShapeColor( SdfCollisionBuilder.ShapeKind.Capsule, brush );
				foreach ( var centre in new[] { cap.CenterA, cap.CenterB } )
				for ( int axis = 0; axis < 3; axis++ )
				for ( int i = 0; i < n; i++ )
				{
					float a0 = MathF.Tau * i / n, a1 = MathF.Tau * (i + 1) / n;
					Seg( centre + Circle( axis, a0 ) * cap.Radius, centre + Circle( axis, a1 ) * cap.Radius, c );
				}

				var dir = (cap.CenterB - cap.CenterA).Normal;
				var side = Vector3.Cross( dir, MathF.Abs( dir.z ) < 0.9f ? Vector3.Up : Vector3.Forward ).Normal;
				var up = Vector3.Cross( side, dir );
				foreach ( var off in new[] { side, -side, up, -up } )
					Seg( cap.CenterA + off * cap.Radius, cap.CenterB + off * cap.Radius, c );
			}

			if ( FootProbes )
			{
				foreach ( var p in Feet )
				{
					Seg( p - Vector3.Forward, p + Vector3.Forward, ProbeColor );
					Seg( p - Vector3.Left, p + Vector3.Left, ProbeColor );
					Seg( p, p + Vector3.Down * 4f, ProbeColor );
				}
			}

			_lines = verts.ToArray();
			_linesMode = (ColourByBrush, FootProbes);
			return _lines;
		}

		// Point at angle a on the unit circle normal to the given axis (0 = x, 1 = y, 2 = z).
		static Vector3 Circle( int axis, float a )
		{
			float s = MathF.Sin( a ), c = MathF.Cos( a );
			return axis switch { 0 => new Vector3( 0f, c, s ), 1 => new Vector3( c, 0f, s ), _ => new Vector3( c, s, 0f ) };
		}
	}

	[ConCmd( "mimi_collision_view" )]
	public static void ViewCmd( bool on ) => Enabled = on;

	/// <summary>Should this collider draw at all right now (master switch, thumbnail scenes, the selection filter)?</summary>
	internal static bool ShouldDraw( SdfCollider collider )
	{
		if ( !Enabled || !collider.IsValid() || SdfThumbnail.IsThumbnailScene( collider.Scene ) )
			return false;
		return !SelectedOnly || IsSelected( collider );
	}

	/// <summary>The sculpture's shape-count line for the stats label.</summary>
	internal static string StatsText( SdfCollider collider, Snapshot snap )
	{
		if ( snap.Model is null )
			return $"{collider.GameObject.Name}: no collider";

		int exactHulls = snap.Hulls.Count - snap.VoxelBoxes;
		var text = $"{collider.GameObject.Name}: {exactHulls} hull · {snap.Spheres.Count} sphere · {snap.Capsules.Count} capsule · {snap.VoxelBoxes} voxel box";
		if ( snap.DroppedHulls > 0 )
			text += $" · {snap.DroppedHulls} hull REJECTED by physics";
		return text;
	}

	/// <summary>Draw <paramref name="collider"/>'s collision as gizmos — EDITOR scene only (play draws through
	/// <see cref="SdfCollisionDebugSystem"/>, whose scene objects the scene view sees too). Called inside the
	/// collider's gizmo scope (sculpture-local space, scale included — the space its shapes live in).</summary>
	public static void Draw( SdfCollider collider )
	{
		if ( !ShouldDraw( collider ) || !collider.Scene.IsEditor )
			return;

		var snap = EditorSnapshot( collider );
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

		foreach ( var (capsule, brush) in snap.Capsules )
		{
			Gizmo.Draw.Color = ShapeColor( SdfCollisionBuilder.ShapeKind.Capsule, brush ).WithAlpha( 0.9f );
			Gizmo.Draw.LineCapsule( capsule, 6 );
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
			Gizmo.Draw.ScreenText( StatsText( collider, snap ), world.Position, new Vector2( 12f, -24f ), size: 12f );
		}

		if ( FlagMissing )
		{
			Gizmo.Draw.Color = MissingColor;
			foreach ( var (pos, text) in snap.Missing )
				Gizmo.Draw.ScreenText( text, world.PointToWorld( pos ), new Vector2( 8f, -6f ), size: 11f );
		}
	}

	internal static Color ShapeColor( SdfCollisionBuilder.ShapeKind? kind, int brush )
	{
		if ( ColourByBrush && brush >= 0 )
			return new ColorHsv( (brush * 137.508f) % 360f, 0.65f, 1f ).ToColor(); // golden-angle hues

		return kind switch
		{
			SdfCollisionBuilder.ShapeKind.Sphere or SdfCollisionBuilder.ShapeKind.Capsule => SphereColor,
			SdfCollisionBuilder.ShapeKind.VoxelBox => VoxelColor,
			SdfCollisionBuilder.ShapeKind.Hull => HullColor,
			_ => Color.White, // the engine's shape list didn't line up with what was emitted — still the real shape
		};
	}

	/// <summary>Editor: the selected GameObjects, refreshed every editor frame by the editor assembly (the gizmo
	/// selection isn't reachable from here).</summary>
	[SkipHotload] public static readonly List<GameObject> Selection = new();

	// The sculpture itself, or anything under it (a brush object, a spline point), is selected. In play the
	// editor's selection is of EDITOR-scene objects, so there "selected" also means the sculpture this machine
	// is sculpting right now (the local edit session's target).
	static bool IsSelected( SdfCollider collider )
	{
		var go = collider.GameObject;
		if ( !collider.Scene.IsEditor && SculptEditSession.Current is { IsEditing: true } s && s.Target.IsValid()
			&& s.Target.GameObject == go )
			return true;

		foreach ( var sel in Selection )
			for ( var g = sel; g.IsValid(); g = g.Parent )
				if ( g == go )
					return true;
		return false;
	}

	/// <summary>PLAY: the component's own build — what physics is actually simulating. Re-derived only when the
	/// component swaps its model.</summary>
	internal static Snapshot RuntimeSnapshot( SdfCollider collider )
	{
		var model = collider.BuiltModel;
		var snap = collider.DebugSnapshot;
		if ( snap is not null && snap.Model == model )
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
			probeFrame, collider.FootProbeSpacing, SdfCollisionBuilder.CodeVersion );

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

		var hullTags = emitted.Where( e => e.Kind is SdfCollisionBuilder.ShapeKind.Hull or SdfCollisionBuilder.ShapeKind.VoxelBox ).ToList();
		var sphereTags = emitted.Where( e => e.Kind == SdfCollisionBuilder.ShapeKind.Sphere ).ToList();
		var capsuleTags = emitted.Where( e => e.Kind == SdfCollisionBuilder.ShapeKind.Capsule ).ToList();

		var physics = model?.Physics;
		if ( physics is not null )
		{
			var hulls = new List<(Transform, PhysicsGroupDescription.BodyPart.HullPart)>();
			var spheres = new List<(Transform, PhysicsGroupDescription.BodyPart.SpherePart)>();
			var capsules = new List<(Transform, PhysicsGroupDescription.BodyPart.CapsulePart)>();
			foreach ( var part in physics.Parts )
			{
				foreach ( var h in part.Hulls ) hulls.Add( (part.Transform, h) );
				foreach ( var s in part.Spheres ) spheres.Add( (part.Transform, s) );
				foreach ( var c in part.Capsules ) capsules.Add( (part.Transform, c) );
			}

			bool capsulesAlign = capsules.Count == capsuleTags.Count;
			for ( int i = 0; i < capsules.Count; i++ )
			{
				var (tx, c) = capsules[i];
				var capsule = new Capsule( tx.PointToWorld( c.Capsule.CenterA ), tx.PointToWorld( c.Capsule.CenterB ), c.Capsule.Radius * tx.UniformScale );
				snap.Capsules.Add( (capsule, capsulesAlign ? capsuleTags[i].BrushIndex : -1) );
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
