using System;
using Mimiclay;

namespace Editor;

/// <summary>
/// Scene-viewport authoring tool for <see cref="SdfSculpture"/>. Activates automatically
/// when a sculpture is selected. Click a brush to select it, then use the transform gizmo
/// to move / rotate / scale it. Add / remove brushes from the component inspector.
/// </summary>
public class SdfSculptureTool : EditorTool<SdfSculpture>
{
	const float GizmoPad = 1.04f; // outlines/solids drawn a touch bigger than the surface

	int _selected;
	int _lastHash;
	IDisposable _undo;
	readonly BrushTransformGizmo _gizmo = new();
	static readonly List<Vector4> _splineCurve = new(); // reused buffer for the spline outline polyline
	static readonly List<Vector4> _splineSweep = new(); // reused buffer for the spline hover/hitbox spheres

	const string GizmoVisibleCookie = "mimiclay.sculptGizmoVisible";

	/// <summary>Master switch for the sculpt gizmo (outlines, transform handles, brush picking).
	/// Off = the tool draws nothing and never captures clicks, so the native s&amp;box transform
	/// gizmo is fully usable. Toggled by the viewport overlay button; persists via editor cookie.</summary>
	internal static bool GizmoVisible
	{
		get => EditorCookie.Get( GizmoVisibleCookie, true );
		set => EditorCookie.Set( GizmoVisibleCookie, value );
	}

	public override void OnEnabled()
	{
		AddOverlay( new SculptGizmoToggle(), TextFlag.RightBottom, 10 );
	}

	public override void OnUpdate()
	{
		// Wire the component's editor-only buttons to our editor-side utilities (the component can't reference
		// the editor assembly itself). Idempotent; cheap.
		SdfSculpture.BakeHandler = SdfBakeUtility.Bake;
		SdfSculpture.ExportPrefabHandler = SdfPrefabUtility.Export;

		var sculpt = GetSelectedComponent<SdfSculpture>();
		if ( sculpt?.Brushes is not { Count: > 0 } )
			return;

		if ( GizmoVisible )
			DrawSculptGizmo( sculpt );
		else
			AllowGameObjectSelection = true; // nothing of ours on screen — never swallow scene clicks

		// Rebuild whenever the brushes changed — inspector edits must still apply while the gizmo is hidden.
		int hash = BrushHash( sculpt.Brushes );
		if ( hash != _lastHash )
		{
			_lastHash = hash;
			if ( sculpt.AutoRebuild )
				sculpt.Rebuild();
		}

		// Keep the experimental raymarch view (if present) in sync while editing.
		sculpt.GameObject.Components.Get<SdfRaymarchRenderer>()?.Refresh();

		if ( Gizmo.WasLeftMouseReleased )
		{
			_undo?.Dispose();
			_undo = null;
		}
	}

	// The whole interactive sculpt gizmo: transform handles, brush outlines, hover ghosts and click
	// picking. Skipped entirely while GizmoVisible is off.
	void DrawSculptGizmo( SdfSculpture sculpt )
	{
		_selected = Math.Clamp( _selected, 0, sculpt.Brushes.Count - 1 );
		var tx = sculpt.WorldTransform;
		var style = GizmoStyle.Resolve( sculpt.Scene );

		// Transform gizmo first. It's the ONLY thing using gizmo hitboxes now, so it always
		// wins hover; brush picking is a manual raycast that only runs when the gizmo is idle.
		_gizmo.Update( tx, sculpt.Brushes[_selected], style, () => BeginUndo( sculpt ) );

		int hoverIndex = _gizmo.IsBusy ? -1 : PickBrush( sculpt, tx );

		// Suppress scene-object selection while over a brush or the gizmo, so the click selects
		// our brush instead of falling through and deselecting the GameObject. Over empty space
		// it stays on, so clicking away still deselects normally.
		AllowGameObjectSelection = !_gizmo.IsBusy && hoverIndex < 0;

		if ( hoverIndex >= 0 && Gizmo.WasLeftMousePressed )
			_selected = hoverIndex;

		// While dragging a handle the gizmo fades to DragOpacity; fade the brush outlines to match so
		// the whole thing dims together for a clearer view of the result.
		float dragAlpha = _gizmo.IsDragging ? style.DragOpacity : 1f;

		using ( Gizmo.Scope( "SdfSculpt", tx ) )
		{
			for ( int i = 0; i < sculpt.Brushes.Count; i++ )
			{
				var brush = sculpt.Brushes[i];
				bool isSel = i == _selected;
				bool hovered = i == hoverIndex && !isSel;

				// A spline lives in sculpture space (its points carry their own positions), not the brush's
				// local frame, so draw it here in the SdfSculpt scope and skip the brush-scope path below.
				// Its per-point move/radius dots come from the transform gizmo (selected brush only).
				if ( brush.Shape == SdfShape.Spline )
				{
					DrawSplineOutline( brush, isSel, hovered, dragAlpha );
					continue;
				}

				// Draw the brush and every mirror copy. A mirror copy is the brush REFLECTED across the
				// sculpture origin — see BrushCopyTransform for why that's baked into ONE transform rather
				// than a reflecting parent scope. The identity copy (sx=sy=sz=0) is the base shape. Picking
				// maps a clicked mirror copy back to this brush via the SDF, so only the base needs a hitbox;
				// an extra hitbox on a copy harmlessly keeps the sculpture selected on click too.
				for ( int sx = 0; sx <= (brush.MirrorX ? 1 : 0); sx++ )
				for ( int sy = 0; sy <= (brush.MirrorY ? 1 : 0); sy++ )
				for ( int sz = 0; sz <= (brush.MirrorZ ? 1 : 0); sz++ )
				using ( Gizmo.Scope( $"brush{i}_{sx}{sy}{sz}", BrushCopyTransform( brush, sx, sy, sz ) ) )
				{
					// Hitbox on the hovered brush so a click on the shape is consumed and the sculpture
					// stays selected (a click that hits nothing deselects it, regardless of
					// AllowGameObjectSelection). DepthBias pushes it to the back so the transform handles
					// — which carry no bias — always win hover over it; reset after so the bias can't
					// leak onto the handles next frame.
					if ( hovered )
					{
						Gizmo.Hitbox.DepthBias = 1f;
						BrushHitbox( brush );
						Gizmo.Hitbox.DepthBias = 0f;
					}

					float alpha = isSel ? 1f : (hovered ? 0.7f : 0.3f);
					Gizmo.Draw.Color = Mimiclay.BrushWireframes.OpColor( brush.Operation ).WithAlpha( alpha * dragAlpha );

					DrawBrushOutline( brush, style );

					// Hovered (unselected) brushes get a faint solid ghost overlaid on everything.
					if ( hovered )
					{
						Gizmo.Draw.IgnoreDepth = true;
						Gizmo.Draw.Color = Mimiclay.BrushWireframes.OpColor( brush.Operation ).WithAlpha( 0.15f * dragAlpha );
						DrawBrushSolid( brush );
					}
				}
			}
		}
	}

	void BeginUndo( SdfSculpture sculpt )
	{
		_undo ??= SceneEditorSession.Active.UndoScope( "Edit SDF Brush" )
			.WithComponentChanges( sculpt )
			.Push();
	}

	// Transform that draws a brush's mirror copy: the brush REFLECTED across the sculpture origin for the
	// per-axis sign (sx/sy/sz: 1 = mirror that axis; all 0 = the un-mirrored brush). A reflection can't be a
	// parent scope — s&box collapses nested scopes into ONE Transform, so a -1-scale parent would apply the
	// flip inside the child's rotated frame and shear it. Baking it into a single transform avoids that:
	// reflect the position, conjugate the rotation by the same reflection (so the copy is oriented like the
	// reflected SDF), and use the sign as scale so the geometry itself flips. Drawn directly (no nested
	// rotated scope under it), so there's no shear.
	static Transform BrushCopyTransform( SdfBrush brush, int sx, int sy, int sz )
	{
		var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
		return new Transform( brush.Position * sign, MirrorRotation( brush.Rotation, sign ), sign );
	}

	// Conjugate a rotation by an axis-plane reflection diag(sign): keep w, and scale each vector component
	// by the product of the OTHER two signs. This is M·R·M — the orientation of the reflected brush.
	static Rotation MirrorRotation( Rotation r, Vector3 sign )
	{
		return new Rotation(
			r.x * sign.y * sign.z,
			r.y * sign.x * sign.z,
			r.z * sign.x * sign.y,
			r.w );
	}

	// Shape-accurate outline (drawn in the brush's local rotated frame; axis = Z).
	// Padded a touch beyond the surface so the render doesn't obscure / z-fight it.
	static void DrawBrushOutline( SdfBrush brush, GizmoSettings style )
	{
		const float pad = GizmoPad;
		Gizmo.Draw.IgnoreDepth = false; // outlines are depth-tested (reset after a hover solid)
		Gizmo.Draw.LineThickness = style.OutlineThickness;

		float r = brush.Size.x * pad;
		var bottom = new Vector3( 0, 0, -brush.Size.z * pad );
		var top = new Vector3( 0, 0, brush.Size.z * pad );

		switch ( brush.Shape )
		{
			case SdfShape.Sphere:
			{
				// Three great ellipses on the LOCAL axis planes (per-axis radii) — sphere if uniform.
				// Use literal axis vectors: s&box's Vector3.Right/Up/Forward are -Y/+Z/+X, which would
				// permute the radii vs the shape's Size.x/y/z.
				var s = brush.Size * pad;
				var ax = new Vector3( 1, 0, 0 );
				var ay = new Vector3( 0, 1, 0 );
				var az = new Vector3( 0, 0, 1 );
				DrawEllipse( s.x, s.y, ax, ay );
				DrawEllipse( s.y, s.z, ay, az );
				DrawEllipse( s.z, s.x, az, ax );
				// Sliced: mark the cut circle (the great ellipses still trace the full sphere — good enough
				// for an editor outline; the runtime ghost/wireframes draw the true sliced silhouette).
				float sphereSlice = brush.SlicePlaneN;
				if ( sphereSlice < 0.999f )
				{
					float cs = MathF.Sqrt( MathF.Max( 1f - sphereSlice * sphereSlice, 0f ) );
					DrawEllipse( s.x * cs, s.y * cs, ax, ay, center: az * (s.z * sphereSlice) );
				}
				break;
			}
			case SdfShape.Cylinder:
				Gizmo.Draw.LineCylinder( bottom, top, r, r, 24 );
				break;
			case SdfShape.Cone:
			{
				// Base-pivot (the shape stands ON Position, growing up +Z); sliced = frustum, the outline's
				// top radius is the cone's radius at the cut plane.
				float coneSlice = brush.SlicePlaneN;
				float zoff = brush.Size.z;
				var baseC = new Vector3( 0, 0, zoff + bottom.z );
				var topC = new Vector3( 0, 0, zoff + (coneSlice < 0.999f ? top.z * coneSlice : top.z) );
				float rc = coneSlice < 0.999f ? r * (1f - coneSlice) * 0.5f : 0f;
				Gizmo.Draw.LineCylinder( baseC, topC, r, rc, 24 ); // top radius 0 = pointed cone
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
					Gizmo.Draw.Line( bt[i], bt[j] ); // bottom profile edge
					Gizmo.Draw.Line( tp[i], tp[j] ); // top profile edge
					Gizmo.Draw.Line( bt[i], tp[i] ); // vertical edge
				}
				break;
			}
			default:
				Gizmo.Draw.LineBBox( BBox.FromPositionAndSize( Vector3.Zero, brush.Size * 2f * pad ) );
				break;
		}
	}

	// Translucent solid fill of the shape (local rotated frame; axis = Z) — the hover ghost.
	static void DrawBrushSolid( SdfBrush brush )
	{
		const float pad = GizmoPad;
		float r = brush.Size.x * pad;
		var bottom = new Vector3( 0, 0, -brush.Size.z * pad );
		var top = new Vector3( 0, 0, brush.Size.z * pad );

		switch ( brush.Shape )
		{
			case SdfShape.Sphere:
				// No solid-ellipsoid primitive — scale a unit sphere by the per-axis radii so the ghost
				// reflects non-uniform scaling (matches the outline) instead of a mean-radius sphere.
				using ( Gizmo.Scope( "ellipsoid", new Transform( Vector3.Zero, Rotation.Identity, brush.Size * pad ) ) )
					Gizmo.Draw.SolidSphere( Vector3.Zero, 1f, 16, 24 );
				break;
			case SdfShape.Cylinder:
				Gizmo.Draw.SolidCylinder( bottom, top, r, 24 );
				break;
			case SdfShape.Cone:
			{
				// SolidCone is (base, axisVector, radius, sides) — axis spans the full height. Base-pivot:
				// shifted up so the base sits on Position. (Slice isn't shown in this editor hover fill —
				// the outline and runtime ghost draw the true frustum.)
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
				// Caps as fans from the centre (valid for the concave star too — it's star-shaped about its
				// centre); sides as quads. The ghost material is two-sided, so winding is cosmetic.
				for ( int i = 0; i < n; i++ )
				{
					int j = (i + 1) % n;
					Gizmo.Draw.SolidTriangle( new Triangle( cb, bt[j], bt[i] ) ); // bottom cap
					Gizmo.Draw.SolidTriangle( new Triangle( ct, tp[i], tp[j] ) ); // top cap
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

	// The extruded brush's cross-section corners at height z, in the brush's local frame — the shared
	// outline definition (SdfBrush.CrossSectionOutline), so the editor gizmo matches the rendered SDF.
	static readonly List<Vector2> _xsOutline = new();
	static Vector3[] ExtrudedCorners( SdfBrush brush, float pad, float z )
	{
		SdfBrush.CrossSectionOutline( brush.CrossSection, brush.Size * pad, _xsOutline );
		var pts = new Vector3[_xsOutline.Count];
		for ( int i = 0; i < pts.Length; i++ )
			pts[i] = new Vector3( _xsOutline[i].x, _xsOutline[i].y, z );
		return pts;
	}

	// Spline wireframe: the centre polyline plus three axis circles at each control point's radius. Drawn in
	// sculpture space (points are sculpture-local), so it's called from inside the SdfSculpt scope, NOT a
	// brush scope. Mirror copies aren't drawn here — the solid SDF still mirrors; this is just the path guide.
	static void DrawSplineOutline( SdfBrush brush, bool isSel, bool hovered, float dragAlpha )
	{
		var pts = brush.Points;
		if ( pts is not { Count: > 0 } )
			return;

		float alpha = isSel ? 1f : (hovered ? 0.7f : 0.3f);
		Gizmo.Draw.IgnoreDepth = false;
		Gizmo.Draw.LineThickness = 2f;
		Gizmo.Draw.Color = Mimiclay.BrushWireframes.OpColor( brush.Operation ).WithAlpha( alpha * dragAlpha );

		// Centre line follows the drawn curve (tessellated when Curvature > 0); rings stay on the control points.
		brush.BuildSplinePolyline( _splineCurve );
		for ( int i = 0; i < _splineCurve.Count - 1; i++ )
			Gizmo.Draw.Line( new Vector3( _splineCurve[i].x, _splineCurve[i].y, _splineCurve[i].z ),
				new Vector3( _splineCurve[i + 1].x, _splineCurve[i + 1].y, _splineCurve[i + 1].z ) );

		var ax = new Vector3( 1, 0, 0 );
		var ay = new Vector3( 0, 1, 0 );
		var az = new Vector3( 0, 0, 1 );
		foreach ( var p in pts )
		{
			var c = new Vector3( p.x, p.y, p.z );
			float r = p.w;
			DrawCircleAt( c, r, ax, ay );
			DrawCircleAt( c, r, ay, az );
			DrawCircleAt( c, r, az, ax );
		}

		// Spheres swept along the curve are the spline's selectable shape. Register them as click hitboxes
		// whenever the spline is hovered OR selected, so a click on the tube keeps the sculpture selected (a
		// click that hits nothing deselects it — the spline has no other body collider). The back depth-bias
		// lets the transform dots still win hover. When hovered (and not the active selection), also draw the
		// translucent solid ghost — the "nice hover shape" the other primitives get.
		if ( hovered || isSel )
		{
			brush.BuildSplineSweep( _splineSweep );

			Gizmo.Hitbox.DepthBias = 1f;
			foreach ( var s in _splineSweep )
				Gizmo.Hitbox.Sphere( new Sphere( new Vector3( s.x, s.y, s.z ), s.w ) );
			Gizmo.Hitbox.DepthBias = 0f;

			if ( hovered )
			{
				Gizmo.Draw.IgnoreDepth = true;
				Gizmo.Draw.Color = Mimiclay.BrushWireframes.OpColor( brush.Operation ).WithAlpha( 0.15f * dragAlpha );
				foreach ( var s in _splineSweep )
					Gizmo.Draw.SolidSphere( new Vector3( s.x, s.y, s.z ), s.w, 6, 8 );
				Gizmo.Draw.IgnoreDepth = false;
			}
		}
	}

	// A circle of radius r centred at `center` in the u/v plane (both unit, perpendicular).
	static void DrawCircleAt( Vector3 center, float r, Vector3 u, Vector3 v, int segments = 32 )
	{
		Vector3 prev = center + u * r;
		for ( int s = 1; s <= segments; s++ )
		{
			float a = (s / (float)segments) * MathF.PI * 2f;
			var p = center + u * (r * MathF.Cos( a )) + v * (r * MathF.Sin( a ));
			Gizmo.Draw.Line( prev, p );
			prev = p;
		}
	}

	// A smooth ellipse with radii ra,rb along the unit vectors u,v (brush-local), optionally offset from the
	// origin (used for the slice cut circle). ra==rb = circle.
	static void DrawEllipse( float ra, float rb, Vector3 u, Vector3 v, int segments = 48, Vector3 center = default )
	{
		Vector3 prev = center + u * ra;
		for ( int s = 1; s <= segments; s++ )
		{
			float a = (s / (float)segments) * MathF.PI * 2f;
			var p = center + u * (ra * MathF.Cos( a )) + v * (rb * MathF.Sin( a ));
			Gizmo.Draw.Line( prev, p );
			prev = p;
		}
	}

	// Click-blocker hitbox for the hovered brush — a loose AABB, registered with a back depth-bias so it
	// loses to the transform handles. Its only job is to consume the click so the sculpture stays selected.
	static void BrushHitbox( SdfBrush brush )
	{
		// AABB of the shape: box + ellipsoid use Size directly; cylinder/cone are radial in x/y.
		var ext = brush.Shape is SdfShape.Cylinder or SdfShape.Cone
			? new Vector3( brush.Size.x, brush.Size.x, brush.Size.z )
			: brush.Size; // box/prism/ellipsoid use Size directly
		Gizmo.Hitbox.BBox( BBox.FromPositionAndSize( Vector3.Zero, ext * 2f ) );
	}

	// Sphere-trace the real SDF along the cursor ray to find the visible surface, then return the brush
	// that owns that surface point (or -1 if it's the selected brush, or nothing was hit). Matches what
	// you actually see — only the brush under the cursor, never a far-off fallback. Assumes unit scale.
	int PickBrush( SdfSculpture sculpt, Transform tx )
	{
		var ray = Gizmo.CurrentRay;

		// Cursor ray into sculpture-local space, then the shared pick (also selects subtractive volumes that
		// have nothing behind them). If the owner is already selected, return -1 (don't re-pick it).
		var invRot = tx.Rotation.Inverse;
		var o = invRot * (ray.Position - tx.Position);
		var d = (invRot * ray.Forward).Normal;

		int best = Sdf.PickBrush( sculpt.Brushes, o, d );
		return best == _selected ? -1 : best;
	}

	static int BrushHash( List<SdfBrush> brushes )
	{
		var h = new HashCode();
		h.Add( brushes.Count );
		foreach ( var b in brushes )
		{
			h.Add( (int)b.Shape );
			h.Add( (int)b.CrossSection ); // extruded profile swap re-meshes
			h.Add( b.Text ); h.Add( b.Font ); // text brushes re-bake their glyph field
			h.Add( (int)b.Operation );
			h.Add( b.Position );
			h.Add( b.Size );
			h.Add( b.Rotation );
			h.Add( b.Blend );
			h.Add( b.Rounding );
			h.Add( b.Slice );
			h.Add( b.Color );
			h.Add( b.MirrorX );
			h.Add( b.MirrorY );
			h.Add( b.MirrorZ );
			if ( b.Points is { } pts )
			{
				h.Add( pts.Count );
				foreach ( var pt in pts )
					h.Add( pt );
				h.Add( b.Curvature );
				h.Add( b.SplineClosed );
			}
		}
		return h.ToHashCode();
	}
}

/// <summary>
/// Viewport top-right overlay: a single eye button toggling <see cref="SdfSculptureTool.GizmoVisible"/>,
/// so the sculpt gizmo can be hidden when it gets in the way of the native transform gizmo.
/// </summary>
class SculptGizmoToggle : WidgetWindow
{
	readonly IconButton _button;

	public SculptGizmoToggle()
	{
		ContentMargins = 0;
		Layout = Layout.Row();
		IsGrabbable = false;

		_button = Layout.Add( new IconButton( "", Toggle )
		{
			FixedHeight = HeaderHeight,
			FixedWidth = HeaderHeight,
			Background = Color.Transparent,
		} );

		Refresh();
	}

	void Toggle()
	{
		SdfSculptureTool.GizmoVisible = !SdfSculptureTool.GizmoVisible;
		Refresh();
	}

	void Refresh()
	{
		bool on = SdfSculptureTool.GizmoVisible;
		_button.Icon = on ? "visibility" : "visibility_off";
		_button.ToolTip = on
			? "Hide the sculpt gizmo (use the native transform gizmo unobstructed)"
			: "Show the sculpt gizmo";
		_button.Update();
	}
}
