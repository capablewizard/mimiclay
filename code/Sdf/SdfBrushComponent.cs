using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// One SDF brush as a GameObject: the EDITOR authoring form of an <see cref="SdfBrush"/>. The object's local
/// transform IS the brush transform — Position/Rotation from the transform, <see cref="SdfBrush.Size"/> from
/// LocalScale — so the native transform gizmo, snapping, copy/paste transform, Ctrl+D, hierarchy reorder,
/// multi-select and undo all work on brushes with no custom tooling. Everything else (shape, operation, blend,
/// material…) lives here as flat inspector properties.
/// <para>A sculpture with <see cref="SdfSculpture.BrushObjects"/> set gathers its brushes from these components
/// (depth-first, sibling order = brush order) every time something changes in the editor, and FLATTENS them
/// back into its brush list at runtime (<see cref="SdfSculpture.FlattenObjects"/>) so the wire, the .sculpt
/// saves and the in-game editor never see an object — the runtime model is unchanged.</para>
/// <para>Objects with no components may sit between the sculpture and its brushes as PIVOTS (rotate a mouth
/// corner as a unit). Pivots must keep unit scale: an SDF brush can't shear.</para>
/// </summary>
[Title( "SDF Brush" )]
[Category( "SDF" )]
[Icon( "category" )]
// SelectionBase: a click on a brush selects THE BRUSH, even inside a prefab instance. Without it the engine
// routes the first click to the outermost prefab root and only the next one to the child, so clicks on a
// converted prefab would alternate between the sculpture and the shape.
[SelectionBase]
public sealed class SdfBrushComponent : Component, Component.ExecuteInEditor
{
	/// <summary>Editor master switch for brush outlines + hitboxes (the viewport eye toggle). Off = the
	/// brushes draw nothing and register no hitboxes, so the scene behaves as if they weren't there.</summary>
	public static bool GizmosVisible = true;

	/// <summary>Editor: draw the outlines ON TOP of the clay (no depth test). Off = depth-tested wires that hide
	/// behind the surface. Both stay clickable; this is purely what you can see to aim at.</summary>
	public static bool WiresOverSdf = true;

	/// <summary>Editor: fill the hovered brush with a translucent ghost of its shape.</summary>
	public static bool HoverGhosts = true;

	/// <summary>Editor: world positions of the selected objects — where the native transform gizmo sits — refreshed
	/// every editor frame by the editor assembly. Near them on screen our picks fall back to normal scoring so the
	/// gizmo handles keep winning; everywhere else they out-score any other object (see <see cref="SdfBrushGizmos.StrongScale"/>).</summary>
	[SkipHotload] public static readonly List<Vector3> GizmoPivots = new();



	// The non-transform half of the brush. Position/Rotation/Size on this instance are never read — the
	// object's transform supplies them at gather time (ToBrush).
	readonly SdfBrush _brush = new();

	/// <summary>The backing brush (shape/material/flags). Transform fields on it are meaningless — use
	/// <see cref="ToBrush"/> for a complete brush in sculpture space.</summary>
	public SdfBrush Brush => _brush;

	/// <summary>Stable brush identity (see <see cref="SdfBrush.Id"/>). Hidden; a duplicate object re-mints
	/// its own at the next gather so two brushes never share one.</summary>
	[Property, Hide] public Guid BrushId { get => _brush.Id; set => _brush.Id = value; }

	[Property] public SdfShape Shape { get => _brush.Shape; set => _brush.Shape = value; }
	[Property] public SdfOperation Operation { get => _brush.Operation; set => _brush.Operation = value; }

	[Property, ShowIf( nameof( Shape ), SdfShape.Extruded )]
	public SdfCrossSection CrossSection { get => _brush.CrossSection; set => _brush.CrossSection = value; }

	[Property, Group( "Text" ), ShowIf( nameof( Shape ), SdfShape.Text )]
	public string Text { get => _brush.Text; set => _brush.Text = value; }

	[Property, Group( "Text" ), ShowIf( nameof( Shape ), SdfShape.Text )]
	public string Font { get => _brush.Font; set => _brush.Font = value; }

	[Property, Group( "Group" ), ShowIf( nameof( Shape ), SdfShape.Group )]
	public string GroupKind { get => _brush.GroupKind; set => _brush.GroupKind = value; }

	/// <summary>Group-member flag: wear the placed group's colour (see <see cref="SdfBrush.LinkColor"/>).</summary>
	[Property, Group( "Group" )] public bool LinkColor { get => _brush.LinkColor; set => _brush.LinkColor = value; }

	/// <summary>Group-member flag: take the placed group's blend (see <see cref="SdfBrush.LinkBlend"/>).</summary>
	[Property, Group( "Group" )] public bool LinkBlend { get => _brush.LinkBlend; set => _brush.LinkBlend = value; }

	[Property, Range( 0f, SdfBrush.MaxBlend ), Step( 0.1f )] public float Blend { get => _brush.Blend; set => _brush.Blend = value; }
	// The slider covers the range roundness is actually dialled in (fractions of a unit up to a few units); the
	// field is unclamped so a bigger value can still be typed for a huge box.
	[Property, Range( SdfBrush.MinRounding, 8f, clamped: false ), Step( 0.05f )]
	public float Rounding { get => _brush.Rounding; set => _brush.Rounding = MathF.Max( value, SdfBrush.MinRounding ); }
	[Property, Range( 0f, SdfBrush.MaxSlice )] public float Slice { get => _brush.Slice; set => _brush.Slice = value; }

	[Property, Range( 0f, SdfBrush.MaxGap ), ShowIf( nameof( Operation ), SdfOperation.Cutout )]
	public float Gap { get => _brush.Gap; set => _brush.Gap = value; }

	[Property] public Color Color { get => _brush.Color; set => _brush.Color = value; }
	[Property, Range( 0f, 1f )] public float Metallic { get => _brush.Metallic; set => _brush.Metallic = value; }
	[Property, Range( 0f, 1f )] public float Roughness { get => _brush.Roughness; set => _brush.Roughness = value; }

	[Property, Group( "Symmetry" )] public bool MirrorX { get => _brush.MirrorX; set => _brush.MirrorX = value; }
	[Property, Group( "Symmetry" )] public bool MirrorY { get => _brush.MirrorY; set => _brush.MirrorY = value; }
	[Property, Group( "Symmetry" )] public bool MirrorZ { get => _brush.MirrorZ; set => _brush.MirrorZ = value; }

	/// <summary>Spline only. Control points are this object's CHILDREN carrying <see cref="SdfSplinePoint"/>,
	/// in sibling order; the brush object's own transform is just a frame to move them together.</summary>
	[Property, Group( "Spline" ), Range( 0f, 1f ), ShowIf( nameof( Shape ), SdfShape.Spline )]
	public float Curvature { get => _brush.Curvature; set => _brush.Curvature = value; }

	[Property, Group( "Spline" ), ShowIf( nameof( Shape ), SdfShape.Spline )]
	public bool SplineClosed { get => _brush.SplineClosed; set => _brush.SplineClosed = value; }

	[Property, Group( "Shrink" )] public bool Shrinks { get => _brush.Shrinks; set => _brush.Shrinks = value; }
	[Property, Group( "Shrink" ), ShowIf( nameof( Shrinks ), true )] public float ShrinkDelay { get => _brush.ShrinkDelay; set => _brush.ShrinkDelay = value; }
	[Property, Group( "Shrink" ), ShowIf( nameof( Shrinks ), true )] public float ShrinkDuration { get => _brush.ShrinkDuration; set => _brush.ShrinkDuration = value; }

	/// <summary>Take every non-transform property from <paramref name="src"/> (identity included — the caller
	/// decides whether this object IS that brush or a duplicate of it).</summary>
	public void CopyFrom( SdfBrush src )
	{
		var c = src.Copy();
		BrushId = c.Id;
		Shape = c.Shape; Operation = c.Operation; CrossSection = c.CrossSection;
		Text = c.Text; Font = c.Font;
		GroupKind = c.GroupKind; LinkColor = c.LinkColor; LinkBlend = c.LinkBlend;
		Blend = c.Blend; Rounding = c.Rounding; Slice = c.Slice; Gap = c.Gap;
		Color = c.Color; Metallic = c.Metallic; Roughness = c.Roughness;
		MirrorX = c.MirrorX; MirrorY = c.MirrorY; MirrorZ = c.MirrorZ;
		Curvature = c.Curvature; SplineClosed = c.SplineClosed;
		Shrinks = c.Shrinks; ShrinkDelay = c.ShrinkDelay; ShrinkDuration = c.ShrinkDuration;
	}

	/// <summary>The sculpture this brush belongs to: the nearest <see cref="SdfSculpture"/> up the hierarchy.</summary>
	public SdfSculpture Owner => Components.GetInAncestors<SdfSculpture>( true );

	/// <summary>This brush as a complete <see cref="SdfBrush"/> in the space of a sculpture whose world
	/// transform is <paramref name="sculptWorld"/>: transform from the object, the rest from the properties.
	/// A disabled object (or component) gathers as a disabled brush — the hierarchy checkbox is the eye toggle.</summary>
	public SdfBrush ToBrush( in Transform sculptWorld )
	{
		var local = sculptWorld.ToLocal( WorldTransform );
		var b = _brush.Copy();
		b.Enabled = Active;
		b.Position = local.Position;
		b.Rotation = local.Rotation;
		b.Size = local.Scale;
		b.Damage = false;
		b.Points = b.Shape == SdfShape.Spline ? GatherPoints( sculptWorld ) : null;
		return b;
	}

	/// <summary>Spline control points from the child <see cref="SdfSplinePoint"/> objects, in sculpture space
	/// (xyz) with each point's radius (w). Sibling order is point order.</summary>
	public List<Vector4> GatherPoints( in Transform sculptWorld )
	{
		var pts = new List<Vector4>();
		foreach ( var child in GameObject.Children )
		{
			if ( !child.IsValid() || !child.Enabled )
				continue;
			var p = child.Components.Get<SdfSplinePoint>();
			if ( p is null )
				continue;
			var lp = sculptWorld.PointToLocal( child.WorldPosition );
			pts.Add( new Vector4( lp.x, lp.y, lp.z, p.Radius ) );
		}
		return pts;
	}

	// ── Editor gizmo: outline, hover ghost, mirror copies and a hitbox so a click selects THIS object ──────
	//
	// Drawn in the brush's own frame (the engine scopes DrawGizmos to the object's world transform), with the
	// object's scale cancelled so everything is in Size units, exactly like the list-form tool draws. The
	// hitbox is what makes brush objects natively clickable / box-selectable: the engine turns a click on a
	// component hitbox into a selection of its GameObject.

	protected override void DrawGizmos()
	{
		if ( !GizmosVisible )
			return;

		var owner = Owner;
		var sculptWorld = owner.IsValid() ? owner.WorldTransform : global::Transform.Zero;
		var brush = ToBrush( sculptWorld );

		// Not inside a sculpture (dropped above the root in the hierarchy, say): nothing renders it, so say so loudly
		// instead of silently vanishing — a red outline at the object, plus a label.
		if ( !owner.IsValid() )
		{
			var saved = Gizmo.Transform;
			Gizmo.Transform = new Transform( WorldPosition, WorldRotation, 1f );
			Gizmo.Draw.Color = Color.Red.WithAlpha( 0.9f );
			SdfBrushGizmos.DrawBrushOutline( brush, 2f, ignoreDepth: true );
			Gizmo.Draw.ScreenText( "Brush is not inside an SDF Sculpture — drop it under one", WorldPosition, new Vector2( 12f, -10f ), size: 12f );
			Gizmo.Transform = saved;
			return;
		}

		bool selected = Gizmo.IsSelected;
		var opColor = BrushWireframes.OpColor( brush.Operation );

		// CLICK TARGETS. The wire outline is the primary target (a fat invisible copy of it registers line hits,
		// biased to beat any volume); the brush's loose volume is registered ONLY when the sculpture's SDF pick
		// says this brush owns the visible surface under the cursor — so a carver's box floating in front of the
		// clay never steals clicks meant for what is actually on screen.
		bool surfaceOwner = owner.IsValid() && owner.EditorHoverBrushId == BrushId;
		float occluder = owner.IsValid() ? owner.EditorOccluderDistance : float.MaxValue;

		// NO gizmo scopes in here — not even the blank one. A hitbox selects this object only when it is
		// registered at the exact path the engine tests for a click (the component's scope), and EVERY
		// Gizmo.Scope call appends to that path, the no-argument one included ("…/Scope2"). So the transform is
		// assigned directly and restored by hand on the way out.
		var savedTransform = Gizmo.Transform;
		try
		{
			if ( brush.Shape == SdfShape.Spline )
			{
				// The guide curve, in sculpture space. The curve is a click target for the spline object (the
				// sculpture's screen-space pick decides), and so is the tube's clay when the spline owns it.
				Gizmo.Transform = sculptWorld;
				if ( owner.IsValid() && owner.EditorPickTarget == GameObject )
					SdfBrushGizmos.RegisterPick( owner.EditorPickDistance, occluder, owner.EditorNearGizmo );
				if ( surfaceOwner )
					SdfBrushGizmos.RegisterSurfaceHit( owner.EditorSurfaceDistance, owner.EditorNearGizmo );

				bool splineHovered = Gizmo.IsHovered && !selected;
				Gizmo.Draw.IgnoreDepth = WiresOverSdf;
				Gizmo.Draw.LineThickness = selected ? 3f : (splineHovered ? 2.5f : 2f);
				Gizmo.Draw.Color = opColor.WithAlpha( selected ? 1f : (splineHovered ? 0.7f : 0.3f) );
				SdfBrushGizmos.DrawSplineCurve( brush );
				return;
			}

			// The brush's frame WITHOUT its scale (Size units, like the list-form tool draws), in world space.
			var baseWorld = sculptWorld.ToWorld( SdfBrushGizmos.BrushCopyTransform( brush, 0, 0, 0 ) );
			Gizmo.Transform = baseWorld;

			// Click targets: the ONE object the sculpture's screen-space pick chose registers its wire; the brush that
			// owns the visible surface registers its volume just above it. Nothing else competes.
			bool picked = owner.IsValid() && owner.EditorPickTarget == GameObject;
			if ( picked )
				SdfBrushGizmos.RegisterPick( owner.EditorPickDistance, occluder, owner.EditorNearGizmo );
			if ( surfaceOwner )
				SdfBrushGizmos.RegisterSurfaceHit( owner.EditorSurfaceDistance, owner.EditorNearGizmo );
			bool hovered = Gizmo.IsHovered && !selected;

			float alpha = selected ? 1f : (hovered ? 0.7f : 0.3f);
			float thickness = selected ? 3f : 2f;

			// The base shape plus every mirror copy (reflected across the SCULPTURE origin, one baked world
			// transform each — see SdfBrushGizmos.BrushCopyTransform).
			for ( int sx = 0; sx <= (brush.MirrorX ? 1 : 0); sx++ )
			for ( int sy = 0; sy <= (brush.MirrorY ? 1 : 0); sy++ )
			for ( int sz = 0; sz <= (brush.MirrorZ ? 1 : 0); sz++ )
			{
				bool isBase = sx == 0 && sy == 0 && sz == 0;
				Gizmo.Transform = isBase ? baseWorld : sculptWorld.ToWorld( SdfBrushGizmos.BrushCopyTransform( brush, sx, sy, sz ) );

				// Drawn OVER the clay: the wire is the click target, so it must always be visible to aim at.
				Gizmo.Draw.Color = opColor.WithAlpha( alpha * (isBase ? 1f : 0.6f) );
				SdfBrushGizmos.DrawBrushOutline( brush, thickness, ignoreDepth: WiresOverSdf );

				if ( HoverGhosts && hovered && isBase )
				{
					Gizmo.Draw.IgnoreDepth = true;
					Gizmo.Draw.Color = opColor.WithAlpha( 0.15f );
					SdfBrushGizmos.DrawBrushSolid( brush );
					Gizmo.Draw.IgnoreDepth = false;
				}
			}
		}
		finally
		{
			Gizmo.Transform = savedTransform;
		}
	}

}

/// <summary>
/// A spline brush's control point, as a child object of an <see cref="SdfBrushComponent"/> whose shape is
/// <see cref="SdfShape.Spline"/>. Position from the object's transform (move it with the native gizmo, Ctrl+D
/// to add a point after it, Delete to remove); the tube radius at this point is <see cref="Radius"/>.
/// </summary>
[Title( "SDF Spline Point" )]
[Category( "SDF" )]
[Icon( "radio_button_checked" )]
[SelectionBase] // same reason as SdfBrushComponent
public sealed class SdfSplinePoint : Component, Component.ExecuteInEditor
{
	[Property, Range( 0.5f, SdfBrush.MaxSplineRadius )] public float Radius { get; set; } = 10f;

	/// <summary>The spline brush this point belongs to (its parent object's brush component).</summary>
	public SdfBrushComponent Spline => GameObject.Parent?.Components.Get<SdfBrushComponent>();

	// ── Editing helpers (used by the editor's sculpt panel) ───────────────────────────────────────────────

	/// <summary>Every control point object under a spline brush object, in sibling (= point) order.</summary>
	public static List<SdfSplinePoint> PointsOf( GameObject spline )
	{
		var list = new List<SdfSplinePoint>();
		if ( !spline.IsValid() )
			return list;
		foreach ( var child in spline.Children )
		{
			var p = child.Components.Get<SdfSplinePoint>( true );
			if ( p is not null )
				list.Add( p );
		}
		return list;
	}

	/// <summary>True if <paramref name="a"/> and <paramref name="b"/> are consecutive points of the same spline
	/// (or its closing pair when the spline is a loop), ordered as <paramref name="first"/> → <paramref name="second"/>.</summary>
	public static bool AreAdjacent( SdfSplinePoint a, SdfSplinePoint b, out SdfSplinePoint first, out SdfSplinePoint second )
	{
		first = second = null;
		if ( !a.IsValid() || !b.IsValid() || a == b || a.GameObject.Parent != b.GameObject.Parent )
			return false;

		var points = PointsOf( a.GameObject.Parent );
		int ia = points.IndexOf( a ), ib = points.IndexOf( b );
		if ( ia < 0 || ib < 0 )
			return false;

		if ( Math.Abs( ia - ib ) == 1 )
		{
			first = ia < ib ? a : b;
			second = ia < ib ? b : a;
			return true;
		}

		// Closing segment of a loop: last → first.
		bool closed = a.Spline?.SplineClosed == true;
		if ( closed && points.Count >= 3 && Math.Min( ia, ib ) == 0 && Math.Max( ia, ib ) == points.Count - 1 )
		{
			first = ia > ib ? a : b;  // the LAST point comes first; the new point appends after it
			second = ia > ib ? b : a;
			return true;
		}

		return false;
	}

	/// <summary>Insert a point between two adjacent points: midpoint, averaged radius, placed right after
	/// <paramref name="a"/> in sibling order. Null if they aren't adjacent.</summary>
	public static GameObject InsertBetween( SdfSplinePoint a, SdfSplinePoint b )
	{
		if ( !AreAdjacent( a, b, out var first, out var second ) )
			return null;

		var parent = first.GameObject.Parent;
		var go = new GameObject( parent, true, "Point" );
		go.WorldPosition = (first.WorldPosition + second.WorldPosition) * 0.5f;
		go.Components.Create<SdfSplinePoint>().Radius = (first.Radius + second.Radius) * 0.5f;
		first.GameObject.AddSibling( go, before: false );
		Renumber( parent );
		return go;
	}

	/// <summary>Insert a point after <paramref name="a"/>: between it and the next point when there is one,
	/// otherwise extending the spline past the end along its last segment.</summary>
	public static GameObject InsertAfter( SdfSplinePoint a )
	{
		if ( !a.IsValid() )
			return null;

		var parent = a.GameObject.Parent;
		var points = PointsOf( parent );
		int i = points.IndexOf( a );
		if ( i < 0 )
			return null;

		if ( i + 1 < points.Count )
			return InsertBetween( a, points[i + 1] );

		var step = i > 0 ? a.WorldPosition - points[i - 1].WorldPosition : Vector3.Zero;
		if ( step.Length < 1f )
			step = Vector3.Up * 16f;

		var go = new GameObject( parent, true, "Point" );
		go.WorldPosition = a.WorldPosition + step;
		go.Components.Create<SdfSplinePoint>().Radius = a.Radius;
		a.GameObject.AddSibling( go, before: false );
		Renumber( parent );
		return go;
	}

	/// <summary>One pass of Laplacian relaxation over a spline's points: every interior point moves <paramref name="amount"/>
	/// of the way toward the midpoint of its two neighbours (a closed loop relaxes all the way round; an open
	/// spline keeps its ends). Radii are left alone. Returns how many points moved.</summary>
	public static int Relax( GameObject spline, float amount = 0.5f )
	{
		var points = PointsOf( spline );
		int n = points.Count;
		if ( n < 3 )
			return 0;

		bool closed = spline.Components.Get<SdfBrushComponent>( true )?.SplineClosed == true;
		var current = new Vector3[n];
		for ( int i = 0; i < n; i++ )
			current[i] = points[i].WorldPosition;

		int moved = 0;
		for ( int i = 0; i < n; i++ )
		{
			if ( !closed && (i == 0 || i == n - 1) )
				continue;

			var prev = current[(i - 1 + n) % n];
			var next = current[(i + 1) % n];
			var target = (prev + next) * 0.5f;
			var p = Vector3.Lerp( current[i], target, amount );
			if ( p.Distance( current[i] ) < 1e-4f )
				continue;

			points[i].WorldPosition = p;
			moved++;
		}

		return moved;
	}

	static void Renumber( GameObject spline )
	{
		int n = 0;
		foreach ( var p in PointsOf( spline ) )
			p.GameObject.Name = $"Point {++n}";
	}

	protected override void DrawGizmos()
	{
		if ( !SdfBrushComponent.GizmosVisible )
			return;

		var spline = Spline;
		var color = BrushWireframes.OpColor( spline?.Operation ?? SdfOperation.Add );
		var ownerWorld = spline?.Owner.IsValid() == true ? spline.Owner.WorldTransform : global::Transform.Zero;

		// No gizmo scope (see SdfBrushComponent.DrawGizmos): the hitbox must sit at this object's exact path.
		// Rings in the SCULPTURE's axes at the point (the tube ignores the point's own rotation and scale).
		var savedTransform = Gizmo.Transform;
		try
		{
			Gizmo.Transform = new Transform( WorldPosition, ownerWorld.Rotation, 1f );

			// The point IS the click target, drawn like a sphere brush: three great circles in the sculpture's
			// axes, OVER the clay (the tube's blended surface bulges past the radius, so a depth-tested wire or a
			// plain sphere hitbox would lose to the rendered mesh). Wires register line hits like every brush;
			// the volume sphere gets the same forward bias so the clay can't out-hover it.
			var owner = spline?.Owner;
			float occluder = owner.IsValid() ? owner.EditorOccluderDistance : float.MaxValue;
			bool picked = owner.IsValid() && owner.EditorPickTarget == GameObject;
			if ( picked )
				SdfBrushGizmos.RegisterPick( owner.EditorPickDistance, occluder, owner.EditorNearGizmo );

			bool selected = Gizmo.IsSelected;
			bool hovered = Gizmo.IsHovered && !selected;

			Gizmo.Draw.IgnoreDepth = SdfBrushComponent.WiresOverSdf;
			Gizmo.Draw.LineThickness = selected ? 3f : 2f;
			Gizmo.Draw.Color = color.WithAlpha( selected ? 1f : (hovered ? 0.7f : 0.3f) );
			SdfBrushGizmos.DrawPointWires( Radius );

			if ( SdfBrushComponent.HoverGhosts && hovered )
			{
				Gizmo.Draw.IgnoreDepth = true;
				Gizmo.Draw.Color = color.WithAlpha( 0.15f );
				Gizmo.Draw.SolidSphere( Vector3.Zero, Radius * SdfBrushGizmos.Pad, 12, 16 );
			}
			Gizmo.Draw.IgnoreDepth = false;

		}
		finally
		{
			Gizmo.Transform = savedTransform;
		}
	}

}

/// <summary>
/// Shape-accurate gizmo drawing and click-target registration for a single <see cref="SdfBrush"/>, shared by the
/// brush objects' own gizmos and the spline points. Everything is drawn in the BRUSH's local frame (axis = Z)
/// in Size units unless stated otherwise.
/// <para>CLICK TARGETS: the sculpture picks ONE object per frame in SCREEN space (<see cref="SdfSculpture.EditorPickTarget"/>,
/// a pixel tolerance to any wire) and only that object registers a hit (<see cref="RegisterPick"/>), scored by hand:
/// its camera distance capped at the nearest clay along the cursor ray, times <see cref="WireScoreScale"/>. The engine
/// scores every hit as distance × bias and keeps the lowest across the whole view, so this beats the clay (× 1) and
/// the surface owner's volume (× <see cref="VolumeScoreScale"/>) but never a transform-gizmo handle (× 0.01).</para>
/// </summary>
public static class SdfBrushGizmos
{
	/// <summary>Outlines/solids are drawn a touch bigger than the surface so the render can't z-fight them.</summary>
	public const float Pad = 1.04f;

	/// <summary>A wire's score is (its capped distance) × this: just under the clay's own score at the same
	/// distance, so the wire wins the tie against the surface it sits on, and nothing more.</summary>
	public const float WireScoreScale = 0.95f;

	/// <summary>The surface owner's VOLUME scores (surface distance) × this: under the clay's own hover (× 1) but
	/// above every wire (× <see cref="WireScoreScale"/> of a distance capped at that same surface), so wires beat
	/// volumes, volumes beat clay, and the engine's gizmo handles (× 0.01) beat all of them.</summary>
	public const float VolumeScoreScale = 0.98f; // between WireScoreScale and the clay's 1.0

	/// <summary>Register the brush's VOLUME as this object's click target: only the brush that owns the visible
	/// surface under the cursor calls this, scored at that surface (see <see cref="VolumeScoreScale"/>).</summary>
	public static void RegisterSurfaceHit( float surfaceDistance, bool nearGizmo )
	{
		if ( surfaceDistance >= float.MaxValue )
			return;

		float bias = Gizmo.Hitbox.DepthBias;
		Gizmo.Hitbox.DepthBias = 1f;
		Gizmo.Hitbox.TrySetHovered( surfaceDistance * VolumeScoreScale * (nearGizmo ? 1f : StrongScale) );
		Gizmo.Hitbox.DepthBias = bias;
	}

	// Per-frame scratch buffers. SkipHotload: a reload mid-use trips the engine's list migration (the "array is
	// too small" hotload errors) and there is nothing worth carrying across anyway.
	[SkipHotload] static readonly List<Vector2> _xsOutline = new();
	[SkipHotload] static readonly List<Vector4> _splineCurve = new();

	/// <summary>Register this object as the click target the sculpture's screen-space pick chose (see
	/// <see cref="SdfSculpture.EditorPickTarget"/>): scored at the wire's camera distance capped at the nearest clay,
	/// times <see cref="WireScoreScale"/>, so it beats the clay and the surface owner's volume but never a
	/// transform-gizmo handle (those sit at a hundredth of their distance).</summary>
	public static void RegisterPick( float distance, float occluderDistance, bool nearGizmo ) => SubmitScore( distance, occluderDistance, nearGizmo );

	/// <summary>Away from the transform gizmo on screen, every pick score is multiplied by this so it out-scores any
	/// OTHER object's click target (a list-form sculpture's bounds box, a mesh, whatever is nearer the camera) —
	/// what you see drawn over the clay is what you click. Near the gizmo the plain scores apply so its handles
	/// (× 0.01) keep winning. Our own picks are all scaled the same way, so their ordering is unchanged.</summary>
	public const float StrongScale = 0.05f;

	/// <summary>Tie-break weight on a wire's TRUE distance. Capping at the occluder makes every wire behind the clay
	/// score the same, and ties pick arbitrarily — this tiny term ranks them nearest-first again while staying far
	/// below the gap between <see cref="WireScoreScale"/> and <see cref="VolumeScoreScale"/> for any sane depth.</summary>
	public const float DepthTieBreak = 0.001f;

	static void SubmitScore( float distance, float occluderDistance, bool nearGizmo )
	{
		float bias = Gizmo.Hitbox.DepthBias;
		Gizmo.Hitbox.DepthBias = 1f;
		float score = MathF.Min( distance, occluderDistance ) * WireScoreScale + distance * DepthTieBreak;
		Gizmo.Hitbox.TrySetHovered( score * (nearGizmo ? 1f : StrongScale) );
		Gizmo.Hitbox.DepthBias = bias;
	}

	/// <summary>Per-axis reciprocal of a scale (zero-safe) — the scope transform that cancels an object's scale.</summary>
	public static Vector3 InverseScale( Vector3 scale ) => new(
		MathF.Abs( scale.x ) > 1e-6f ? 1f / scale.x : 1f,
		MathF.Abs( scale.y ) > 1e-6f ? 1f / scale.y : 1f,
		MathF.Abs( scale.z ) > 1e-6f ? 1f / scale.z : 1f );

	/// <summary>Transform of a brush's mirror copy in SCULPTURE space: the brush reflected across the origin
	/// per axis (sx/sy/sz: 1 = mirror that axis; all 0 = the brush itself). A reflection can't be a parent
	/// scope — nested scopes collapse into ONE Transform, so a -1-scale parent would flip inside the child's
	/// rotated frame and shear it — so reflect the position, conjugate the rotation by the same reflection and
	/// carry the sign as scale so the geometry itself flips.</summary>
	public static Transform BrushCopyTransform( SdfBrush brush, int sx, int sy, int sz )
	{
		var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
		return new Transform( brush.Position * sign, MirrorRotation( brush.Rotation, sign ), sign );
	}

	// Conjugate a rotation by an axis-plane reflection diag(sign): keep w, scale each vector component by
	// the product of the OTHER two signs (M·R·M — the orientation of the reflected brush).
	public static Rotation MirrorRotation( Rotation r, Vector3 sign )
		=> new( r.x * sign.y * sign.z, r.y * sign.x * sign.z, r.z * sign.x * sign.y, r.w );

	/// <summary>Click target for the brush's VOLUME: a loose AABB of the shape in its local frame (engine hitbox,
	/// default bias — only the brush that owns the visible surface registers this).</summary>
	public static void Hitbox( SdfBrush brush )
	{
		var ext = brush.Shape is SdfShape.Cylinder or SdfShape.Cone
			? new Vector3( brush.Size.x, brush.Size.x, brush.Size.z )
			: brush.Size;
		// The cone stands on its pivot (spans local z 0..2·Size.z); the rest are centred.
		var centre = brush.Shape == SdfShape.Cone ? new Vector3( 0f, 0f, brush.Size.z ) : Vector3.Zero;
		Gizmo.Hitbox.BBox( BBox.FromPositionAndSize( centre, ext * 2f ) );
	}

	// ── Outline geometry: one segment emitter per shape, used both to draw and to hit-test ────────────────

	/// <summary>Every wire segment of the brush's outline (local rotated frame; axis = Z), padded a touch
	/// beyond the surface.</summary>
	public static void OutlineSegments( SdfBrush brush, Action<Vector3, Vector3> emit )
	{
		const float pad = Pad;
		float r = brush.Size.x * pad;
		var bottom = new Vector3( 0, 0, -brush.Size.z * pad );
		var top = new Vector3( 0, 0, brush.Size.z * pad );
		var ax = new Vector3( 1, 0, 0 );
		var ay = new Vector3( 0, 1, 0 );
		var az = new Vector3( 0, 0, 1 );

		switch ( brush.Shape )
		{
			case SdfShape.Sphere:
			{
				// Three great ellipses on the LOCAL axis planes (per-axis radii). Literal axis vectors: the
				// engine's Vector3.Right/Up/Forward are -Y/+Z/+X and would permute the radii.
				var s = brush.Size * pad;
				Ellipse( s.x, s.y, ax, ay, emit );
				Ellipse( s.y, s.z, ay, az, emit );
				Ellipse( s.z, s.x, az, ax, emit );
				float sphereSlice = brush.SlicePlaneN;
				if ( sphereSlice < 0.999f )
				{
					float cs = MathF.Sqrt( MathF.Max( 1f - sphereSlice * sphereSlice, 0f ) );
					Ellipse( s.x * cs, s.y * cs, ax, ay, emit, center: az * (s.z * sphereSlice) );
				}
				break;
			}
			case SdfShape.Cylinder:
				Ellipse( r, r, ax, ay, emit, 24, bottom );
				Ellipse( r, r, ax, ay, emit, 24, top );
				for ( int i = 0; i < 4; i++ )
				{
					var side = (ax * MathF.Cos( i * MathF.PI * 0.5f ) + ay * MathF.Sin( i * MathF.PI * 0.5f )) * r;
					emit( bottom + side, top + side );
				}
				break;
			case SdfShape.Cone:
			{
				// Base-pivot (stands ON Position, growing up +Z); sliced = frustum.
				float coneSlice = brush.SlicePlaneN;
				float zoff = brush.Size.z;
				var baseC = new Vector3( 0, 0, zoff + bottom.z );
				var topC = new Vector3( 0, 0, zoff + (coneSlice < 0.999f ? top.z * coneSlice : top.z) );
				float rc = coneSlice < 0.999f ? r * (1f - coneSlice) * 0.5f : 0f;
				Ellipse( r, r, ax, ay, emit, 24, baseC );
				if ( rc > 0f )
					Ellipse( rc, rc, ax, ay, emit, 24, topC );
				for ( int i = 0; i < 4; i++ )
				{
					var dir = ax * MathF.Cos( i * MathF.PI * 0.5f ) + ay * MathF.Sin( i * MathF.PI * 0.5f );
					emit( baseC + dir * r, topC + dir * rc );
				}
				break;
			}
			case SdfShape.Extruded:
			{
				var bt = ExtrudedCorners( brush, pad, bottom.z );
				var tp = ExtrudedCorners( brush, pad, top.z );
				int n = bt.Length;
				for ( int i = 0; i < n; i++ )
				{
					int j = (i + 1) % n;
					emit( bt[i], bt[j] );
					emit( tp[i], tp[j] );
					emit( bt[i], tp[i] );
				}
				break;
			}
			default:
			{
				var e = brush.Size * pad;
				for ( int i = 0; i < 4; i++ )
				{
					float sx = (i & 1) == 0 ? -1f : 1f;
					float sy = (i & 2) == 0 ? -1f : 1f;
					var c = new Vector3( sx * e.x, sy * e.y, 0f );
					emit( c - az * e.z, c + az * e.z ); // vertical edge
				}
				for ( int z = -1; z <= 1; z += 2 )
				{
					var p0 = new Vector3( -e.x, -e.y, z * e.z );
					var p1 = new Vector3( e.x, -e.y, z * e.z );
					var p2 = new Vector3( e.x, e.y, z * e.z );
					var p3 = new Vector3( -e.x, e.y, z * e.z );
					emit( p0, p1 ); emit( p1, p2 ); emit( p2, p3 ); emit( p3, p0 );
				}
				break;
			}
		}
	}

	/// <summary>The spline's drawn curve (sculpture space), as segments. Points have their own wires.</summary>
	public static void SplineCurveSegments( SdfBrush brush, Action<Vector3, Vector3> emit )
	{
		if ( brush.Points is not { Count: > 0 } )
			return;

		brush.BuildSplinePolyline( _splineCurve );
		for ( int i = 0; i < _splineCurve.Count - 1; i++ )
			emit( new Vector3( _splineCurve[i].x, _splineCurve[i].y, _splineCurve[i].z ),
				new Vector3( _splineCurve[i + 1].x, _splineCurve[i + 1].y, _splineCurve[i + 1].z ) );
	}

	/// <summary>A spline point's wires: the three great circles a sphere brush gets, at the tube radius (padded).</summary>
	public static void PointSegments( float radius, Action<Vector3, Vector3> emit )
	{
		float r = radius * Pad;
		var ax = new Vector3( 1, 0, 0 );
		var ay = new Vector3( 0, 1, 0 );
		var az = new Vector3( 0, 0, 1 );
		Ellipse( r, r, ax, ay, emit );
		Ellipse( r, r, ay, az, emit );
		Ellipse( r, r, az, ax, emit );
	}

	/// <summary>An ellipse with radii ra,rb along the unit vectors u,v, optionally offset from the origin.</summary>
	static void Ellipse( float ra, float rb, Vector3 u, Vector3 v, Action<Vector3, Vector3> emit, int segments = 48, Vector3 center = default )
	{
		Vector3 prev = center + u * ra;
		for ( int s = 1; s <= segments; s++ )
		{
			float a = (s / (float)segments) * MathF.PI * 2f;
			var p = center + u * (ra * MathF.Cos( a )) + v * (rb * MathF.Sin( a ));
			emit( prev, p );
			prev = p;
		}
	}

	// The extruded brush's cross-section corners at height z, in the brush's local frame — the shared outline
	// definition (SdfBrush.CrossSectionOutline), so the gizmo matches the rendered SDF.
	static Vector3[] ExtrudedCorners( SdfBrush brush, float pad, float z )
	{
		SdfBrush.CrossSectionOutline( brush.CrossSection, brush.Size * pad, _xsOutline );
		var pts = new Vector3[_xsOutline.Count];
		for ( int i = 0; i < pts.Length; i++ )
			pts[i] = new Vector3( _xsOutline[i].x, _xsOutline[i].y, z );
		return pts;
	}

	// ── Drawing ───────────────────────────────────────────────────────────────────────────────────────────

	static readonly Action<Vector3, Vector3> DrawLine = ( a, b ) => Gizmo.Draw.Line( a, b );

	/// <summary>Shape-accurate wire outline (local rotated frame; axis = Z).</summary>
	public static void DrawBrushOutline( SdfBrush brush, float thickness, bool ignoreDepth = false )
	{
		Gizmo.Draw.IgnoreDepth = ignoreDepth;
		Gizmo.Draw.LineThickness = thickness;
		OutlineSegments( brush, DrawLine );
	}

	/// <summary>Only the spline's drawn curve (sculpture space), no point rings — the point objects draw themselves.</summary>
	public static void DrawSplineCurve( SdfBrush brush ) => SplineCurveSegments( brush, DrawLine );

	/// <summary>A spline point's wires (see <see cref="PointSegments"/>).</summary>
	public static void DrawPointWires( float radius ) => PointSegments( radius, DrawLine );

	/// <summary>Translucent solid fill of the shape (the hover ghost). Caller sets colour / depth mode.</summary>
	public static void DrawBrushSolid( SdfBrush brush )
	{
		const float pad = Pad;
		float r = brush.Size.x * pad;
		var bottom = new Vector3( 0, 0, -brush.Size.z * pad );
		var top = new Vector3( 0, 0, brush.Size.z * pad );

		switch ( brush.Shape )
		{
			case SdfShape.Sphere:
				using ( Gizmo.Scope( "ellipsoid", new Transform( Vector3.Zero, Rotation.Identity, brush.Size * pad ) ) )
					Gizmo.Draw.SolidSphere( Vector3.Zero, 1f, 16, 24 );
				break;
			case SdfShape.Cylinder:
				Gizmo.Draw.SolidCylinder( bottom, top, r, 24 );
				break;
			case SdfShape.Cone:
			{
				var baseC = bottom + new Vector3( 0, 0, brush.Size.z );
				Gizmo.Draw.SolidCone( baseC, top - bottom, r, 24 );
				break;
			}
			case SdfShape.Extruded:
			{
				var bt = ExtrudedCorners( brush, pad, bottom.z );
				var tp = ExtrudedCorners( brush, pad, top.z );
				int n = bt.Length;
				var cb = new Vector3( 0f, 0f, bottom.z );
				var ct = new Vector3( 0f, 0f, top.z );
				for ( int i = 0; i < n; i++ )
				{
					int j = (i + 1) % n;
					Gizmo.Draw.SolidTriangle( new Triangle( cb, bt[j], bt[i] ) );
					Gizmo.Draw.SolidTriangle( new Triangle( ct, tp[i], tp[j] ) );
					Gizmo.Draw.SolidTriangle( new Triangle( bt[i], bt[j], tp[j] ) );
					Gizmo.Draw.SolidTriangle( new Triangle( bt[i], tp[j], tp[i] ) );
				}
				break;
			}
			default:
				Gizmo.Draw.SolidBox( BBox.FromPositionAndSize( Vector3.Zero, brush.Size * 2f * pad ) );
				break;
		}
	}
}
