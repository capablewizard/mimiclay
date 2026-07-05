using System;
using Mimiclay;

namespace Editor;

/// <summary>
/// Custom Unbound-style transform gizmo for a single <see cref="SdfBrush"/>:
/// per-axis move lines, scale dots at the line ends, rotation rings, and plane-move squares.
/// Drawn chunky in world space. Drag math is done by hand so the visual style is fully ours.
///
/// NOTE: assumes the sculpture GameObject has unit scale (rotation/position are handled
/// properly via the transform; only the scale handles assume scale == 1).
/// </summary>
public class BrushTransformGizmo
{
	static readonly Color[] AxisColors = { Color.Red, Color.Green, Color.Blue };
	static readonly Vector3[] LocalAxis = { new( 1, 0, 0 ), new( 0, 1, 0 ), new( 0, 0, 1 ) };
	static readonly Color ScreenMoveColor = new( 0.75f, 0.75f, 0.75f ); // light grey centre handle

	// The three planes, identified by their normal axis: plane 0 = YZ (normal X), etc.
	static readonly int[] PlaneU = { 1, 2, 0 };
	static readonly int[] PlaneV = { 2, 0, 1 };

	GizmoSettings _style;  // scene gizmo settings, resolved each Update
	string _active;        // name of the handle currently being dragged
	bool _hovered;         // any handle hovered this frame
	float _dragAlpha = 1f; // opacity multiplier applied to every handle while a drag is in progress
	Vector3 _grabWorldPos; // brush world position at drag start
	Rotation _grabWorldRot;// brush world rotation at drag start
	float _grabT;          // axis parameter at drag start
	float _grabHalf;       // axis half-extent at drag start
	float _grabAngle;      // ring angle on the previous drag frame (for incremental unwrapping)
	float _accumAngle;     // total accumulated rotation since drag start (radians, unwrapped)
	Vector3 _grabAxis;     // world rotation axis frozen at drag start (so it can't drift with the brush)
	Vector3 _grabPoint;    // plane hit point at drag start
	Vector3 _grabSize;     // brush size at drag start (for uniform scale)

	/// <summary>True while the cursor is over a handle or a drag is in progress — used so the
	/// tool gives the gizmo click priority over brush selection.</summary>
	public bool IsBusy => _hovered || _active is not null;

	/// <summary>True while a handle is actively being dragged. The tool uses this (with
	/// <see cref="GizmoSettings.DragOpacity"/>) to fade the brush outlines to match.</summary>
	public bool IsDragging => _active is not null;

	/// <summary>Draw + handle the gizmo. Returns true if the brush changed this frame.</summary>
	public bool Update( Transform tx, SdfBrush brush, GizmoSettings style, Action onDragStart )
	{
		_style = style;
		_hovered = false;
		_dragAlpha = _active is not null ? _style.DragOpacity : 1f;

		// Push every handle's hitbox forward in depth (negative bias = toward camera; the brush
		// click-blocker uses +1 to sit behind) so handles win selection over the shape.
		Gizmo.Hitbox.DepthBias = -_style.GizmoDepthBias;

		// A spline isn't a single transform — it's a chain of control points. Instead of the full move/
		// rotate/scale gizmo, each point gets just two dots: a move dot (drag in the view plane) and a
		// radius dot sitting on its rim (drag in/out). No whole-spline transform handles.
		if ( brush.Shape == SdfShape.Spline )
		{
			bool splChanged = SplineHandles( tx, brush, onDragStart );
			if ( !Gizmo.IsLeftMouseDown )
				_active = null;
			return splChanged;
		}

		var center = tx.PointToWorld( brush.Position );
		var rot = tx.Rotation * brush.Rotation;

		// All extents are screen-based: specified in pixels, converted to world at the gizmo's distance
		// so the whole thing stays a constant size on screen regardless of brush size or zoom.
		float gs = _style.GizmoScale;
		float ringR = W( center, _style.RotationRadius * gs );
		float tIn = W( center, (_style.TranslationMin + _style.TranslationOffset) * gs );
		float tOut = W( center, (_style.TranslationMax + _style.TranslationOffset) * gs );
		float dotReach = W( center, (_style.TranslationMax + _style.TranslationOffset + _style.ScaleDotOffset) * gs );
		float dotR = W( center, _style.DotRadius * gs );
		bool changed = false;

		for ( int i = 0; i < 3; i++ )
		{
			var axis = (rot * LocalAxis[i]).Normal;
			var col = AxisColors[i];

			if ( _style.ShowTranslation )
				changed |= MoveAxis( tx, brush, i, center, axis, tIn, tOut, col, onDragStart );
			if ( _style.ShowScale )
				changed |= ScaleAxis( tx, brush, i, center, axis, dotReach, dotR, col, onDragStart );
			if ( _style.ShowRotation )
				changed |= RotateRing( tx, brush, i, center, axis, ringR, col, onDragStart );
			if ( _style.ShowPlaneSquares )
				changed |= PlaneMove( tx, brush, i, center, rot, ringR, tOut, col, onDragStart );
		}

		// Screen-axis ring sits outside the per-axis rings and rotates around the view direction.
		if ( _style.ShowScreenRotation )
			changed |= ScreenRotate( tx, brush, center, ringR, onDragStart );

		// Centre free-move handle, sized off the translation reach so it sits in the move-line gap.
		if ( _style.ShowScreenMove )
			changed |= ScreenMove( tx, brush, center, tOut, onDragStart );

		// Uniform-scale circle pinned to the screen top-right.
		if ( _style.ShowUniformScale )
			changed |= UniformScale( brush, center, onDragStart );

		// Blend / rounding sliders below the gizmo.
		if ( _style.ShowSliders )
			changed |= Sliders( brush, center, ringR, gs, onDragStart );

		if ( !Gizmo.IsLeftMouseDown )
			_active = null;

		return changed;
	}

	// --- spline (per-control-point handles) ---

	static readonly List<Vector4> _splineCurve = new(); // reused buffer for the curve polyline

	bool SplineHandles( Transform tx, SdfBrush brush, Action onDragStart )
	{
		var pts = brush.Points;
		if ( pts is not { Count: > 0 } )
			return false;

		bool changed = false;
		float gs = _style.GizmoScale;

		// Centre line so the path reads as one strand even before you hover a dot (follows the curve). Styled
		// from the shared GizmoSettings spline params, same as the in-game gizmo. Scoped so the pen state
		// (IgnoreDepth/LineThickness/colour) doesn't leak into the editor's other gizmos — Update() runs in the
		// root gizmo context, so an unscoped thickness here makes ALL later wireframes (camera, bounds…) thick.
		using ( Gizmo.Scope( "splineLine" ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = _style.SplineThickness( false );
			SetColor( _style.SplineLineColor.WithAlpha( _style.SplineOpacity( false ) ) );
			brush.BuildSplinePolyline( _splineCurve );
			for ( int i = 0; i < _splineCurve.Count - 1; i++ )
				Gizmo.Draw.Line( tx.PointToWorld( new Vector3( _splineCurve[i].x, _splineCurve[i].y, _splineCurve[i].z ) ),
					tx.PointToWorld( new Vector3( _splineCurve[i + 1].x, _splineCurve[i + 1].y, _splineCurve[i + 1].z ) ) );

			// In-between dots along the line, evenly spaced per span at the configured frequency.
			int mid = _style.SplineMidDots;
			if ( mid > 0 )
			{
				int np = pts.Count;
				for ( int i = 0; i < np - 1; i++ )
				{
					var p0 = pts[i > 0 ? i - 1 : 0];
					var p1 = pts[i];
					var p2 = pts[i + 1];
					var p3 = pts[i < np - 2 ? i + 2 : np - 1];
					for ( int j = 1; j <= mid; j++ )
					{
						var cp = SdfBrush.CatmullRomPoint( p0, p1, p2, p3, j / (float)(mid + 1), brush.Curvature );
						var w = tx.PointToWorld( new Vector3( cp.x, cp.y, cp.z ) );
						Gizmo.Draw.SolidSphere( w, W( w, _style.SplineMidDotRadius * gs ) );
					}
				}
			}
		}

		for ( int i = 0; i < pts.Count; i++ )
		{
			changed |= SplinePointMove( tx, brush, i, gs, onDragStart );
			changed |= SplinePointRadius( tx, brush, i, gs, onDragStart );
		}

		changed |= SplineCloseToggle( tx, brush, gs, onDragStart );

		// Curvature + blend sliders below the spline (it has no transform ring to hang the normal sliders off).
		if ( _style.ShowSliders )
			changed |= SplineSliders( tx, brush, gs, onDragStart );

		// Click anywhere along the line (between control points) to insert a new control point there.
		changed |= SplineAddPoint( tx, brush, gs, onDragStart );

		return changed;
	}

	static readonly Color SplineCloseColor = new( 0.4f, 0.9f, 0.5f );   // green: click to close the loop
	static readonly Color SplineBreakColor = new( 0.9f, 0.3f, 0.3f ); // muted red: click to open the loop
	static readonly Color SplineAddColor = new( 0.45f, 0.75f, 1f );     // light blue: click to add a point here

	// Click anywhere along the spline body (between control points) to insert a new control point at that spot,
	// at the curve's interpolated radius. Mirrors the in-game gizmo's line-hover insert. Skipped while a dot/
	// handle/slider is under the cursor or a drag is running, so it never steals those interactions.
	bool SplineAddPoint( Transform tx, SdfBrush brush, float gs, Action onDragStart )
	{
		var pts = brush.Points;
		if ( pts is not { Count: >= 2 } || _hovered || _active is not null )
			return false;

		// Densely sample the curve (fixed rate, not curvature-dependent) and find the nearest point on it to the
		// cursor ray — so you can insert anywhere, including along a straight span.
		const int Samples = 16;
		float best = float.MaxValue;
		Vector4 insert = default;
		int insertIdx = -1;
		int np = pts.Count;
		for ( int i = 0; i < np - 1; i++ )
		{
			var p0 = pts[i > 0 ? i - 1 : 0];
			var p1 = pts[i];
			var p2 = pts[i + 1];
			var p3 = pts[i < np - 2 ? i + 2 : np - 1];
			var prevW = tx.PointToWorld( new Vector3( p1.x, p1.y, p1.z ) );
			for ( int s = 1; s <= Samples; s++ )
			{
				var cur = SdfBrush.CatmullRomPoint( p0, p1, p2, p3, s / (float)Samples, brush.Curvature );
				var curW = tx.PointToWorld( new Vector3( cur.x, cur.y, cur.z ) );
				float d = RaySegDist( Gizmo.CurrentRay, prevW, curW, out _ );
				// On the CENTRE LINE only (no tube-body deadzone) — the line's own thickness + standard grab pad,
				// exactly like the move-axis line handles.
				if ( d <= LineHitRadius( curW, _style.SplineThickness( false ) ) && d < best )
				{
					best = d;
					insert = cur;
					insertIdx = i + 1;
				}
				prevW = curW;
			}
		}

		if ( insertIdx < 0 )
			return false;

		var w = tx.PointToWorld( new Vector3( insert.x, insert.y, insert.z ) );

		using ( Gizmo.Scope( "splineAdd" ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			float rayT = MathF.Max( 0f, Vector3.Dot( w - Gizmo.CurrentRay.Position, Gizmo.CurrentRay.Forward ) );
			Gizmo.Hitbox.TrySetHovered( rayT );

			if ( !Gizmo.IsHovered )
				return false;

			_hovered = true; // make the tool give us click priority (don't re-pick / deselect on this click)

			// Ghost dot + a white "+" at the candidate position so it's clear a point will land there.
			float dotR = W( w, _style.SplinePointRadius * gs );
			SetColor( SplineAddColor );
			Gizmo.Draw.SolidSphere( w, dotR );

			float arm = dotR * 0.9f;
			Gizmo.Draw.LineThickness = _style.SplineThickness( true );
			SetColor( Color.White );
			Gizmo.Draw.Line( w - Gizmo.Camera.Rotation.Right * arm, w + Gizmo.Camera.Rotation.Right * arm );
			Gizmo.Draw.Line( w - Gizmo.Camera.Rotation.Up * arm, w + Gizmo.Camera.Rotation.Up * arm );

			if ( Gizmo.WasLeftMousePressed )
			{
				onDragStart?.Invoke();
				brush.Points.Insert( Math.Clamp( insertIdx, 0, brush.Points.Count ), insert );
				return true;
			}
		}

		return false;
	}

	// Curvature + blend sliders for a spline, anchored below its control-point centroid (a spline has no single
	// transform / screen-ring to hang the normal Sliders() off). Same look + drag math as the standard sliders.
	bool SplineSliders( Transform tx, SdfBrush brush, float gs, Action onDragStart )
	{
		var pts = brush.Points;
		if ( pts is not { Count: > 0 } )
			return false;

		Vector3 sum = default;
		for ( int i = 0; i < pts.Count; i++ )
			sum += new Vector3( pts[i].x, pts[i].y, pts[i].z );
		var center = tx.PointToWorld( sum / pts.Count );

		ScreenBasis( out _, out var right, out var up );
		right = -right; // run the track left → right (Cross(n, Up) points screen-left)

		float halfW = W( center, _style.RotationRadius * gs * _style.ScreenRingScale ); // match the normal slider span
		float buffer = W( center, _style.SliderBuffer * gs );
		float th = _style.SliderThickness * _style.Thickness;
		float y0 = W( center, _style.SliderOffset * gs );
		float y1 = W( center, (_style.SliderOffset + _style.SliderSpacing) * gs );

		bool changed = false;
		changed |= Slider( center, right, up, halfW, y0, buffer, th, "sliderBlend", brush.Blend, 0f, _style.SliderBlendMax, v => brush.Blend = v, onDragStart );
		changed |= Slider( center, right, up, halfW, y1, buffer, th, "sliderCurv", brush.Curvature, 0f, 1f, v => brush.Curvature = v, onDragStart );
		return changed;
	}

	// "Close loop" toggle: a handle at the midpoint of the gap between the last and first control points. While
	// OPEN it sits on a faint ghost of the would-be closing segment (so it's obvious what it does); clicking
	// closes the loop. While CLOSED that segment is already part of the centre line; clicking opens it again.
	// Needs >= 3 points to be a real ring.
	bool SplineCloseToggle( Transform tx, SdfBrush brush, float gs, Action onDragStart )
	{
		var pts = brush.Points;
		if ( pts is not { Count: >= 3 } )
			return false;

		var wLast = tx.PointToWorld( new Vector3( pts[^1].x, pts[^1].y, pts[^1].z ) );
		var wFirst = tx.PointToWorld( new Vector3( pts[0].x, pts[0].y, pts[0].z ) );
		var mid = (wLast + wFirst) * 0.5f;

		using ( Gizmo.Scope( "splineClose" ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || _active == "splineClose";

			// Idle it's just a small quiet marker; ONLY on hover does the ghost closing segment appear (a
			// preview of the join) and the dot grow — so an open spline you aren't closing isn't cluttered.
			if ( !brush.SplineClosed && on )
			{
				Gizmo.Draw.LineThickness = _style.SplineThickness( false );
				SetColor( SplineCloseColor.WithAlpha( 0.85f ) );
				Gizmo.Draw.Line( wLast, wFirst );
			}

			float fullR = W( mid, _style.SplinePointRadius * gs );
			Gizmo.Hitbox.Sphere( new Sphere( mid, fullR + GrabPad( mid ) ) ); // generous grab even when drawn small
			var col = brush.SplineClosed ? SplineBreakColor : SplineCloseColor;
			SetColor( on ? Color.Lerp( col, Color.White, 0.4f ) : col.WithAlpha( 0.55f ) );
			Gizmo.Draw.SolidSphere( mid, on ? fullR * _style.HotScale : fullR * 0.5f );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				onDragStart?.Invoke(); // capture an undo scope for the toggle
				brush.SplineClosed = !brush.SplineClosed;
				return true;
			}
		}

		return false;
	}

	// Move dot at the control point — drags it across the screen plane (like the centre screen-move handle).
	bool SplinePointMove( Transform tx, SdfBrush brush, int i, float gs, Action onDragStart )
	{
		string name = $"ptMove{i}";
		bool hot = _active == name;
		var pt = brush.Points[i];
		var c = tx.PointToWorld( new Vector3( pt.x, pt.y, pt.z ) );
		ScreenBasis( out var n, out _, out _ );
		float dotR = W( c, _style.SplinePointRadius * gs );

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			Gizmo.Hitbox.Sphere( new Sphere( c, dotR + GrabPad( c ) ) );
			SetColor( on ? Color.White : _style.SplineLineColor );
			Gizmo.Draw.SolidSphere( c, on ? dotR * _style.HotScale : dotR );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered && new Plane( c, n ).TryTrace( Gizmo.CurrentRay, out var hit0, true ) )
			{
				_active = name;
				_grabPoint = hit0;
				_grabWorldPos = c;
				onDragStart?.Invoke();
			}

			if ( hot && Gizmo.IsLeftMouseDown && new Plane( _grabWorldPos, n ).TryTrace( Gizmo.CurrentRay, out var hit, true ) )
			{
				var local = tx.PointToLocal( _grabWorldPos + (hit - _grabPoint) );
				brush.Points[i] = new Vector4( local.x, local.y, local.z, brush.Points[i].w ); // keep radius
				return true;
			}
		}

		return false;
	}

	// Radius dot sitting on the point's rim (screen-right) — drag it toward/away from the centre to resize
	// that point. New radius = the dragged distance from the centre in the screen plane (min 1).
	bool SplinePointRadius( Transform tx, SdfBrush brush, int i, float gs, Action onDragStart )
	{
		string name = $"ptRad{i}";
		bool hot = _active == name;
		var pt = brush.Points[i];
		var c = tx.PointToWorld( new Vector3( pt.x, pt.y, pt.z ) );

		ScreenBasis( out var n, out var right, out _ );
		var handlePos = c + right * pt.w; // unit scale: local radius == world radius
		float dotR = W( handlePos, _style.SplinePointRadius * _style.SplineRadiusDotScale * gs );

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			Gizmo.Hitbox.Sphere( new Sphere( handlePos, dotR + GrabPad( handlePos ) ) );
			SetColor( on ? Color.Lerp( _style.SplineRadiusColor, Color.White, 0.4f ) : _style.SplineRadiusColor );
			Gizmo.Draw.SolidSphere( handlePos, on ? dotR * _style.HotScale : dotR );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered && new Plane( c, n ).TryTrace( Gizmo.CurrentRay, out _, true ) )
			{
				_active = name;
				_grabWorldPos = c;
				onDragStart?.Invoke();
			}

			if ( hot && Gizmo.IsLeftMouseDown && new Plane( _grabWorldPos, n ).TryTrace( Gizmo.CurrentRay, out var hit, true ) )
			{
				float newR = MathF.Max( 1f, (hit - _grabWorldPos).Length );
				brush.Points[i] = new Vector4( pt.x, pt.y, pt.z, newR );
				return true;
			}
		}

		return false;
	}

	bool MoveAxis( Transform tx, SdfBrush brush, int i, Vector3 c, Vector3 axis, float inner, float outer, Color col, Action onDragStart )
	{
		string name = $"move{i}";
		bool hot = _active == name;

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			float th = _style.MoveThickness( on );

			// Line spans inner..outer along the axis, leaving the inner gap for the centre handle.
			var start = c + axis * inner;
			var tip = c + axis * outer;

			HitSegment( c, start, tip, th ); // manual click target — fully controllable, no engine slop

			Gizmo.Draw.LineThickness = th;
			SetColor( Highlight( col, on ) );
			Gizmo.Draw.Line( start, tip );

			// Round both ends so the line doesn't terminate in a hard square edge. Each end is sized
			// for its own distance so both stay the line's width on screen.
			Cap( CapRadius( th, start ), start );
			Cap( CapRadius( th, tip ), tip );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				_active = name;
				_grabT = ClosestAxisT( c, axis, Gizmo.CurrentRay );
				_grabWorldPos = c;
				onDragStart?.Invoke();
			}

			if ( hot && Gizmo.IsLeftMouseDown )
			{
				// Measure from the fixed grab origin, not the live (moving) centre — otherwise
				// the reference point shifts under the drag each frame and the move jitters.
				float t = ClosestAxisT( _grabWorldPos, axis, Gizmo.CurrentRay );
				brush.Position = tx.PointToLocal( _grabWorldPos + axis * (t - _grabT) );
				return true;
			}
		}

		return false;
	}

	bool ScaleAxis( Transform tx, SdfBrush brush, int i, Vector3 c, Vector3 axis, float reach, float dotR, Color col, Action onDragStart )
	{
		string name = $"scale{i}";
		bool hot = _active == name;
		var pos = c + axis * reach;

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			Gizmo.Hitbox.Sphere( new Sphere( pos, dotR + GrabPad( c ) ) );
			SetColor( Highlight( col, on ) );
			Gizmo.Draw.SolidSphere( pos, on ? dotR * _style.HotScale : dotR );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				_active = name;
				_grabT = ClosestAxisT( c, axis, Gizmo.CurrentRay );
				_grabHalf = AxisHalf( brush, i );
				onDragStart?.Invoke();
			}

			if ( hot && Gizmo.IsLeftMouseDown )
			{
				float t = ClosestAxisT( c, axis, Gizmo.CurrentRay );
				SetAxisSize( brush, i, MathF.Max( 1f, _grabHalf + (t - _grabT) ) );
				return true;
			}
		}

		return false;
	}

	bool RotateRing( Transform tx, SdfBrush brush, int i, Vector3 c, Vector3 axis, float R, Color col, Action onDragStart )
	{
		string name = $"rot{i}";
		bool hot = _active == name;

		// In-plane basis perpendicular to the axis.
		PerpBasis( axis, out var u, out var v );

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			float th = _style.RingThickness( on );

			// Cull the far half against the view sphere so only the camera-facing arc shows.
			var toCam = (Gizmo.Camera.Position - c).Normal;

			// Manual hitbox matching the visible (culled) arc.
			HitRing( c, u, v, R, _style.RingSegments, toCam, _style.OccludeRingBackHalf, th );

			Gizmo.Draw.LineThickness = th;
			SetColor( Highlight( col, on ) );
			DrawRing( c, u, v, R, _style.RingSegments, toCam, _style.OccludeRingBackHalf, CapRadius( th, c ) );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				_active = name;
				_grabAxis = axis;
				_grabAngle = RingAngle( c, axis, u, v, out _ );
				_accumAngle = 0f;
				_grabWorldRot = tx.Rotation * brush.Rotation;
				onDragStart?.Invoke();
			}

			if ( hot && Gizmo.IsLeftMouseDown )
				return ApplyRingDrag( tx, brush, c );
		}

		return false;
	}

	// A single white ring facing the camera. Dragging it spins the brush around the view axis —
	// the screen-space rotation handle you get in Unity / Maya / Blender, drawn just outside the
	// coloured per-axis rings.
	bool ScreenRotate( Transform tx, SdfBrush brush, Vector3 c, float R, Action onDragStart )
	{
		const string name = "rotScreen";
		bool hot = _active == name;

		// View axis = direction from the brush centre to the camera, so the ring always faces us.
		var axis = (Gizmo.Camera.Position - c).Normal;
		if ( axis.LengthSquared < 0.001f )
			axis = Gizmo.Camera.Rotation.Forward; // degenerate: camera sitting on the centre

		// In-plane basis perpendicular to the view axis.
		PerpBasis( axis, out var u, out var v );

		float ringR = R * _style.ScreenRingScale;

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			float th = _style.RingThickness( on );

			// Manual hitbox (full circle — the screen ring isn't culled).
			HitRing( c, u, v, ringR, _style.RingSegments, default, false, th );

			Gizmo.Draw.LineThickness = th;
			SetColor( on ? Color.White : Color.White.WithAlpha( 0.6f ) );
			DrawRing( c, u, v, ringR, _style.RingSegments );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				_active = name;
				_grabAxis = axis;
				_grabAngle = RingAngle( c, axis, u, v, out _ );
				_accumAngle = 0f;
				_grabWorldRot = tx.Rotation * brush.Rotation;
				onDragStart?.Invoke();
			}

			if ( hot && Gizmo.IsLeftMouseDown )
				return ApplyRingDrag( tx, brush, c );
		}

		return false;
	}

	// Light-grey rounded square at the centre, facing the camera. Dragging it slides the brush across
	// the view plane (a free-move handle), sized to sit inside the translation gap.
	bool ScreenMove( Transform tx, SdfBrush brush, Vector3 c, float reach, Action onDragStart )
	{
		const string name = "screenMove";
		bool hot = _active == name;

		// Screen-aligned plane: normal toward the camera, right/up parallel to the screen.
		ScreenBasis( out var n, out var right, out var up );

		float h = reach * _style.ScreenMoveScale;

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			float draw = h * (on ? _style.HotScale : 1f); // grow the visual, leave the hitbox put
			Gizmo.Hitbox.Sphere( new Sphere( c, h + GrabPad( c ) ) );
			SetColor( on ? Color.Lerp( ScreenMoveColor, Color.White, 0.5f ) : ScreenMoveColor );
			SolidRoundedSquare( c, right, up, draw, draw * _style.ScreenMoveRounding );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				var plane = new Plane( c, n );
				if ( plane.TryTrace( Gizmo.CurrentRay, out var hit, true ) )
				{
					_active = name;
					_grabPoint = hit;
					_grabWorldPos = c;
					onDragStart?.Invoke();
				}
			}

			if ( hot && Gizmo.IsLeftMouseDown )
			{
				var plane = new Plane( _grabWorldPos, n );
				if ( plane.TryTrace( Gizmo.CurrentRay, out var hit, true ) )
				{
					brush.Position = tx.PointToLocal( _grabWorldPos + (hit - _grabPoint) );
					return true;
				}
			}
		}

		return false;
	}

	// White circle pinned to the screen top-right. Dragging up/right scales the whole brush up
	// uniformly, down/left scales it down — measured along the up-right screen diagonal, exponential
	// so each UniformScaleSensitivity pixels doubles or halves the size.
	bool UniformScale( SdfBrush brush, Vector3 c, Action onDragStart )
	{
		const string name = "uscale";
		bool hot = _active == name;

		// Screen-aligned basis; the handle and its drag axis live in the screen plane.
		ScreenBasis( out var n, out var right, out var up );
		var diag = (up - right).Normal; // right/up scales up, left/down scales down

		float gs = _style.GizmoScale;
		var pos = c + (right + up) * W( c, _style.UniformScaleOffset * gs );
		float rad = W( pos, _style.UniformScaleRadius * gs );

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			float draw = on ? rad * _style.HotScale : rad;
			Gizmo.Hitbox.Sphere( new Sphere( pos, rad + GrabPad( c ) ) );
			SetColor( on ? Color.White : Color.White.WithAlpha( 0.85f ) );
			SolidRoundedSquare( pos, right, up, draw, draw ); // rc == half ⇒ a flat circle

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				var plane = new Plane( c, n );
				if ( plane.TryTrace( Gizmo.CurrentRay, out var hit, true ) )
				{
					_active = name;
					_grabPoint = hit;
					_grabSize = brush.Size;
					_grabWorldPos = c;
					onDragStart?.Invoke();
				}
			}

			if ( hot && Gizmo.IsLeftMouseDown )
			{
				var plane = new Plane( _grabWorldPos, n );
				if ( plane.TryTrace( Gizmo.CurrentRay, out var hit, true ) )
				{
					float wpp = (Gizmo.Camera.Position - _grabWorldPos).Length * PixelToWorld; // world per pixel
					float deltaPx = Vector3.Dot( hit - _grabPoint, diag ) / MathF.Max( wpp, 1e-6f );
					float factor = MathF.Pow( 2f, deltaPx / MathF.Max( 1f, _style.UniformScaleSensitivity ) );

					// Keep proportions and never let any component fall below 1.
					float minComp = MathF.Min( _grabSize.x, MathF.Min( _grabSize.y, _grabSize.z ) );
					factor = MathF.Max( factor, 1f / MathF.Max( minComp, 0.0001f ) );
					brush.Size = _grabSize * factor;
					return true;
				}
			}
		}

		return false;
	}

	// Two horizontal screen-space sliders below the gizmo, spanning the screen-ring's width: the top
	// drives the brush's blend, the bottom its rounding.
	bool Sliders( SdfBrush brush, Vector3 center, float ringR, float gs, Action onDragStart )
	{
		// Screen-aligned basis (same as the screen-space handles).
		ScreenBasis( out _, out var right, out var up );
		right = -right; // Cross(n, Up) points screen-left here — flip so the slider runs left → right

		float halfW = ringR * _style.ScreenRingScale; // span = screen-rotation-ring diameter
		float buffer = W( center, _style.SliderBuffer * gs );
		float th = _style.SliderThickness * _style.Thickness;
		float y0 = W( center, _style.SliderOffset * gs );
		float y1 = W( center, (_style.SliderOffset + _style.SliderSpacing) * gs );

		bool changed = false;
		changed |= Slider( center, right, up, halfW, y0, buffer, th, "sliderBlend", brush.Blend, 0f, _style.SliderBlendMax, v => brush.Blend = v, onDragStart );
		changed |= Slider( center, right, up, halfW, y1, buffer, th, "sliderRound", brush.Rounding, _style.SliderRoundingMin, brush.MaxRounding(), v => brush.Rounding = v, onDragStart );
		return changed;
	}

	bool Slider( Vector3 center, Vector3 right, Vector3 up, float halfW, float yOff, float buffer, float th,
		string name, float value, float minValue, float maxValue, Action<float> set, Action onDragStart )
	{
		bool hot = _active == name;
		var left = center - up * yOff - right * halfW;
		float fullW = 2f * halfW;
		var rightEnd = left + right * fullW;
		float denom = MathF.Max( 1e-4f, fullW - buffer );      // usable track length (after the dot buffer)
		float range = MathF.Max( 1e-4f, maxValue - minValue );
		float frac = Math.Clamp( (value - minValue) / range, 0f, 1f );
		var knob = left + right * (buffer + frac * denom);

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;

			HitSegment( center, left, rightEnd, th );

			// Faint full-width track, then the bright value fill (left → knob), both with rounded ends so
			// an empty slider still reads as a dot and the track doesn't terminate in a hard square edge.
			float cap = CapRadius( th, center );
			Gizmo.Draw.LineThickness = th;
			Gizmo.Draw.Color = Color.White.WithAlpha( 0.25f );
			Gizmo.Draw.Line( left, rightEnd );
			Cap( cap, left );
			Cap( cap, rightEnd );

			Gizmo.Draw.Color = on ? Color.White : Color.White.WithAlpha( 0.8f );
			Gizmo.Draw.Line( left, knob );
			Cap( cap, left );
			Cap( cap, knob );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				_active = name;
				onDragStart?.Invoke();
			}

			if ( hot && Gizmo.IsLeftMouseDown )
			{
				float t = ClosestAxisT( left, right, Gizmo.CurrentRay );
				set( minValue + Math.Clamp( (t - buffer) / denom, 0f, 1f ) * range );
				return true;
			}
		}

		return false;
	}

	bool PlaneMove( Transform tx, SdfBrush brush, int i, Vector3 c, Rotation rot, float R, float sizeReach, Color col, Action onDragStart )
	{
		string name = $"plane{i}";
		bool hot = _active == name;

		var n = (rot * LocalAxis[i]).Normal;
		var u = (rot * LocalAxis[PlaneU[i]]).Normal;
		var v = (rot * LocalAxis[PlaneV[i]]).Normal;

		// Position sits relative to the ring radius; size shares the centre handle's basis (translation reach).
		float off = _style.PlaneOffset( R );
		float half = sizeReach * _style.PlaneScale;
		var sq = c + (u + v) * off;

		using ( Gizmo.Scope( name ) )
		{
			Gizmo.Draw.IgnoreDepth = true;
			bool on = Gizmo.IsHovered || hot;
			float draw = half * (on ? _style.HotScale : 1f); // grow the visual, leave the hitbox put
			Gizmo.Hitbox.Sphere( new Sphere( sq, half + GrabPad( c ) ) );
			SetColor( Highlight( col, on ).WithAlpha( 0.6f ) );
			// Flat rounded square lying in the constraint plane (u/v), normal = the locked axis n.
			SolidRoundedSquare( sq, u, v, draw, draw * _style.PlaneRounding );

			if ( Gizmo.IsHovered )
				_hovered = true;

			if ( Gizmo.WasLeftMousePressed && Gizmo.IsHovered )
			{
				var plane = new Plane( c, n );
				if ( plane.TryTrace( Gizmo.CurrentRay, out var hit, true ) )
				{
					_active = name;
					_grabPoint = hit;
					_grabWorldPos = c;
					onDragStart?.Invoke();
				}
			}

			if ( hot && Gizmo.IsLeftMouseDown )
			{
				var plane = new Plane( _grabWorldPos, n );
				if ( plane.TryTrace( Gizmo.CurrentRay, out var hit, true ) )
				{
					brush.Position = tx.PointToLocal( _grabWorldPos + (hit - _grabPoint) );
					return true;
				}
			}
		}

		return false;
	}

	// --- helpers ---

	// When cullBack is set, a ring segment is drawn only if it lies on the camera-facing side of the
	// plane through the centre perpendicular to viewAxis (the near hemisphere of the imagined sphere).
	// capRadius > 0 rounds off the two open ends of the resulting arc with a small dot.
	static void DrawRing( Vector3 c, Vector3 u, Vector3 v, float r, int segments, Vector3 viewAxis = default, bool cullBack = false, float capRadius = 0f )
	{
		Vector3 prev = c + u * r;
		bool prevVis = !cullBack || Vector3.Dot( prev - c, viewAxis ) >= 0f;
		for ( int s = 1; s <= segments; s++ )
		{
			float a = (s / (float)segments) * MathF.PI * 2f;
			var p = c + (u * MathF.Cos( a ) + v * MathF.Sin( a )) * r;
			bool vis = !cullBack || Vector3.Dot( p - c, viewAxis ) >= 0f;

			if ( prevVis && vis )
				Gizmo.Draw.Line( prev, p );

			// Cap the vertex where the visible arc starts (p) or ends (prev).
			if ( capRadius > 0f && prevVis != vis )
				Cap( capRadius, vis ? p : prev );

			prev = p;
			prevVis = vis;
		}
	}

	// Filled, camera-facing rounded square centred at c in the right/up plane (half-size h, corner
	// radius rc). Triangle-fanned from the centre and drawn in both windings so it shows regardless of
	// which way the engine culls.
	static void SolidRoundedSquare( Vector3 c, Vector3 right, Vector3 up, float h, float rc, int cornerSegs = 4 )
	{
		rc = MathF.Max( 0f, MathF.Min( rc, h ) );
		float a = h - rc; // distance from centre to each corner's arc centre

		Vector3 first = default, prev = default;
		bool started = false;
		for ( int k = 0; k < 4; k++ )
		{
			float sx = (k == 0 || k == 3) ? 1f : -1f;
			float sy = (k == 0 || k == 1) ? 1f : -1f;
			for ( int s = 0; s <= cornerSegs; s++ )
			{
				float ang = (k * 90f + (s / (float)cornerSegs) * 90f) * (MathF.PI / 180f);
				float x = sx * a + rc * MathF.Cos( ang );
				float y = sy * a + rc * MathF.Sin( ang );
				var p = c + right * x + up * y;

				if ( !started ) { first = prev = p; started = true; continue; }
				Gizmo.Draw.SolidTriangle( new Triangle( c, prev, p ) );
				Gizmo.Draw.SolidTriangle( new Triangle( c, p, prev ) );
				prev = p;
			}
		}

		Gizmo.Draw.SolidTriangle( new Triangle( c, prev, first ) );
		Gizmo.Draw.SolidTriangle( new Triangle( c, first, prev ) );
	}

	// A round end cap — a small solid dot in the current draw colour. Skipped when radius <= 0.
	static void Cap( float radius, Vector3 p )
	{
		if ( radius > 0f )
			Gizmo.Draw.SolidSphere( p, radius );
	}

	// World units per on-screen pixel at unit distance, for a typical editor scene view. Multiplying by
	// the point's distance to the camera cancels perspective, so a size given in pixels stays a constant
	// size on screen. Tuned against a high-DPI viewport (~2000px tall); a very different FOV / viewport
	// shifts this, which GizmoScale / the per-feature sizes then absorb.
	const float PixelToWorld = 0.00058f;

	// World size for an on-screen size of `px` pixels at point `at` — the basis for all screen-based sizing.
	float W( Vector3 at, float px ) => px * (Gizmo.Camera.Position - at).Length * PixelToWorld;

	// Extra world-space grab margin shared by every handle: the user's HitboxPadding plus a fixed
	// baseline. We hit-test all handles ourselves (no engine LineScope slop), so this is the single
	// source of grab tolerance — tuning HitboxPadding now moves lines, rings and dots together.
	const float GrabBaseline = 5f;
	float GrabPad( Vector3 at ) => W( at, _style.HitboxPadding + GrabBaseline );

	// World grab radius for a line/ring of visible thickness `th` (px): half its width + the shared margin.
	float LineHitRadius( Vector3 at, float th ) => W( at, th * 0.5f ) + GrabPad( at );

	// Shortest distance between the cursor ray and segment [a,b], plus the depth (ray distance to the
	// closest point) for hover sorting. The basis for our manual line/ring hit-tests.
	static float RaySegDist( Ray ray, Vector3 a, Vector3 b, out float rayT )
	{
		var ab = b - a;
		float len = ab.Length;
		var dir = len > 1e-4f ? ab / len : Vector3.Forward;
		float t = Math.Clamp( ClosestAxisT( a, dir, ray ), 0f, len );
		var segPt = a + dir * t;
		rayT = MathF.Max( 0f, Vector3.Dot( segPt - ray.Position, ray.Forward ) );
		var rayPt = ray.Position + ray.Forward * rayT;
		return (segPt - rayPt).Length;
	}

	// Manual hit-test for a straight handle: hover this scope if the cursor ray passes within the grab
	// radius of the segment. Fully controllable — no engine slop.
	void HitSegment( Vector3 c, Vector3 a, Vector3 b, float th )
	{
		float dist = RaySegDist( Gizmo.CurrentRay, a, b, out float rayT );
		if ( dist <= LineHitRadius( c, th ) )
			Gizmo.Hitbox.TrySetHovered( rayT );
	}

	// Manual hit-test for a ring: walk the (optionally back-culled) segments and hover if the nearest
	// one is within the grab radius. Mirrors DrawRing's tessellation so the hitbox matches the drawn arc.
	void HitRing( Vector3 c, Vector3 u, Vector3 v, float r, int segments, Vector3 viewAxis, bool cullBack, float th )
	{
		float radius = LineHitRadius( c, th );
		var ray = Gizmo.CurrentRay;
		float best = float.MaxValue;

		Vector3 prev = c + u * r;
		bool prevVis = !cullBack || Vector3.Dot( prev - c, viewAxis ) >= 0f;
		for ( int s = 1; s <= segments; s++ )
		{
			float ang = (s / (float)segments) * MathF.PI * 2f;
			var p = c + (u * MathF.Cos( ang ) + v * MathF.Sin( ang )) * r;
			bool vis = !cullBack || Vector3.Dot( p - c, viewAxis ) >= 0f;

			if ( prevVis && vis )
			{
				float d = RaySegDist( ray, prev, p, out float rayT );
				if ( d <= radius && rayT < best )
					best = rayT;
			}

			prev = p;
			prevVis = vis;
		}

		if ( best < float.MaxValue )
			Gizmo.Hitbox.TrySetHovered( best );
	}

	// World radius for a cap whose on-screen diameter is LineCapScale × the line width (in pixels).
	float CapRadius( float thickness, Vector3 at ) =>
		_style.LineCapScale <= 0f ? 0f : W( at, thickness * _style.LineCapScale * 0.5f );

	// Set the draw colour, faded by the active drag opacity (1 when not dragging). Every handle routes
	// its colour through here so the whole gizmo dims together while you manipulate a brush.
	void SetColor( Color c ) => Gizmo.Draw.Color = c.WithAlpha( c.a * _dragAlpha );

	// Advance the active ring drag by the angle the cursor has swept since last frame. The delta is
	// measured per-frame and unwrapped into [-180°, 180°], then accumulated — so the rotation tracks
	// continuously past 180° / 360° instead of flipping when atan2 wraps. Unreliable (near edge-on)
	// frames are skipped so the projection blowing up doesn't jitter the brush.
	bool ApplyRingDrag( Transform tx, SdfBrush brush, Vector3 c )
	{
		// Measure against the axis frozen at grab — NOT the live brush axis, which would drift as the
		// brush turns and unconstrain the rotation.
		var axis = _grabAxis;
		PerpBasis( axis, out var u, out var v );

		float ang = RingAngle( c, axis, u, v, out bool ok );
		if ( !ok )
			return false;

		float delta = ang - _grabAngle;
		if ( delta > MathF.PI ) delta -= MathF.PI * 2f;
		else if ( delta < -MathF.PI ) delta += MathF.PI * 2f;

		_accumAngle += delta;
		_grabAngle = ang;

		var dq = Rotation.FromAxis( axis, _accumAngle.RadianToDegree() );
		brush.Rotation = tx.Rotation.Inverse * (dq * _grabWorldRot);
		return true;
	}

	// Screen-aligned basis taken from the camera's own orientation (not the object→camera vector), so the
	// screen-space handles stay aligned to the screen wherever the gizmo sits — not just dead centre.
	// normal points toward the camera; right/up are parallel to the screen plane. Same cross-product
	// formula as before (only the source of `normal` changed), so existing sign conventions still hold.
	static void ScreenBasis( out Vector3 normal, out Vector3 right, out Vector3 up )
	{
		normal = -Gizmo.Camera.Rotation.Forward;
		right = Vector3.Cross( normal, Vector3.Up );
		if ( right.LengthSquared < 0.001f )
			right = Vector3.Cross( normal, Vector3.Forward );
		right = right.Normal;
		up = Vector3.Cross( right, normal ).Normal;
	}

	// An in-plane basis (u, v) perpendicular to the given axis. Deterministic, so the same axis always
	// yields the same basis — which is what lets the drag freeze its reference frame at grab time.
	static void PerpBasis( Vector3 axis, out Vector3 u, out Vector3 v )
	{
		u = Vector3.Cross( axis, Vector3.Up );
		if ( u.LengthSquared < 0.001f )
			u = Vector3.Cross( axis, Vector3.Forward );
		u = u.Normal;
		v = Vector3.Cross( axis, u ).Normal;
	}

	static float RingAngle( Vector3 c, Vector3 normal, Vector3 u, Vector3 v, out bool ok )
	{
		var ray = Gizmo.CurrentRay;

		// Reject grazing rays: when the cursor ray is nearly parallel to the ring's plane the hit point
		// shoots off toward infinity and the angle is unusable. ~5° off the plane is the cutoff.
		if ( MathF.Abs( Vector3.Dot( ray.Forward, normal ) ) < 0.09f )
		{
			ok = false;
			return 0f;
		}

		var plane = new Plane( c, normal );
		if ( !plane.TryTrace( ray, out var hit, true ) )
		{
			ok = false;
			return 0f;
		}

		ok = true;
		var d = hit - c;
		return MathF.Atan2( Vector3.Dot( d, v ), Vector3.Dot( d, u ) );
	}

	static float ClosestAxisT( Vector3 o, Vector3 d, Ray ray )
	{
		var w0 = o - ray.Position;
		float b = Vector3.Dot( d, ray.Forward );
		float dd = Vector3.Dot( d, w0 );
		float ee = Vector3.Dot( ray.Forward, w0 );
		float denom = 1f - b * b;
		if ( MathF.Abs( denom ) < 1e-5f )
			return -dd;
		return (b * ee - dd) / denom;
	}

	static Color Highlight( Color c, bool on ) => on ? Color.Lerp( c, Color.White, 0.4f ) : c;

	// The brush's half-extent on an axis — the actual size driven while scale-dragging (not gizmo display).
	static float AxisHalf( SdfBrush b, int i ) => i == 0 ? b.Size.x : i == 1 ? b.Size.y : b.Size.z;

	static void SetAxisSize( SdfBrush b, int i, float value )
	{
		var s = b.Size;
		b.Size = i == 0 ? new Vector3( value, s.y, s.z )
			: i == 1 ? new Vector3( s.x, value, s.z )
			: new Vector3( s.x, s.y, value );
	}
}
