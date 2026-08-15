using System;

namespace Mimiclay;

/// <summary>
/// In-game transform gizmo for a single <see cref="SdfBrush"/> — a faithful runtime port of the editor's
/// BrushTransformGizmo. Same handles (per-axis move/scale, rotation rings, screen-rotate, screen-move,
/// uniform-scale, plane squares), same drag math, same <see cref="GizmoSettings"/>
/// styling. Blend/rounding are edited via the 2D ClaySliders in the HUD, not in-world. The editor version
/// rides the editor-only <c>Gizmo</c> static API (not available in-game), so
/// this one is self-contained:
///
/// <list type="bullet">
/// <item>Hit-tested by hand against the cursor ray (<see cref="CameraComponent.ScreenPixelToRay"/>).</item>
/// <item>Rendered as ONE solid vertex-coloured mesh (camera-facing ribbons for lines/rings, discs for dots,
/// filled rounded quads for the squares) via a reused <see cref="SceneObject"/> + the gizmo.shader (unlit,
/// drawn on top). One draw call; the mesh only re-uploads when something visually changed.</item>
/// </list>
/// </summary>
public sealed class RuntimeBrushGizmo
{
	static readonly Color[] AxisColors = { Color.Red, Color.Green, Color.Blue };
	static readonly Vector3[] LocalAxis = { new( 1, 0, 0 ), new( 0, 1, 0 ), new( 0, 0, 1 ) };
	static readonly Color ScreenMoveColor = new( 0.75f, 0.75f, 0.75f );
	static readonly int[] PlaneU = { 1, 2, 0 };
	static readonly int[] PlaneV = { 2, 0, 1 };

	// Per-frame context.
	GizmoSettings _style;
	CameraComponent _cam;
	Transform _tx;      // sculpture world transform this frame — folded into the draw hash so rotating the
	                    // OBJECT (static camera) still re-uploads the mesh (the camera moving would too).
	float _masterAlpha = 1f; // whole-gizmo opacity (fades the gizmo in/out with the selection); multiplies every Tint.
	Vector3 _camPos;
	Vector3 _sRight, _sUp; // camera screen basis (for camera-facing handles)
	Ray _ray;
	float _wpp;
	float _screenScale; // viewport height / GizmoSettings.ReferenceHeight — keeps the gizmo a constant fraction of the screen
	bool _interactive;  // false while the orbit camera owns the mouse (alt) — draw, but don't interact
	bool _pressed;      // Attack1 edge this frame (interactive only)
	bool _down;         // Attack1 held this frame (interactive only)
	bool _pressed2;     // Attack2 edge this frame (interactive only) — deletes a hovered spline point

	// Hover resolution + drag state.
	string _hover;
	float _hoverDepth;
	int _hoverPriority;     // higher tier wins outright; ties broken by nearest depth
	string _active;
	float _grabT, _grabHalf, _grabAngle, _accumAngle, _grabTwist;
	Vector3 _grabWorldPos, _grabWorldRotAxis, _grabPoint, _grabSize;
	Rotation _grabWorldRot;

	// Render resources (reused frame to frame).
	readonly List<Vertex> _verts = new();
	readonly List<int> _indices = new();
	Vector3 _bbMin, _bbMax;
	SceneObject _so;
	Material _material;
	int _lastDrawHash;

	public bool IsBusy => _hover is not null || _active is not null;

	/// <summary>True from the moment a right-click deleted a spline control point until that button is
	/// released. The session's right-click-to-deselect must skip such a click — the gizmo already used it,
	/// and the delete clears the hover, so hover state alone can't tell you afterwards.</summary>
	public bool RightClickConsumed { get; private set; }

	/// <summary>True while a handle is actively being dragged.</summary>
	public bool IsDragging => _active is not null;

	/// <summary>True while the cursor is over a GRABBABLE handle (move/scale/rotate/trackball, spline
	/// point/radius dots) with no drag running. The spline LINE is excluded — that hover is the add-point
	/// affordance with its own cursor. Drives the open-hand grab cursor.</summary>
	public bool IsHoveringGrabbable => _active is null && _hover is not null && _hover != "splineLine";

	public bool Update( Transform tx, SdfBrush brush, Scene scene, GizmoSettings style, bool allowInteract = true, float alpha = 1f )
	{
		_cam = scene?.Camera;
		if ( _cam is null || style is null )
			return false;

		_tx = tx;
		_style = style;
		_masterAlpha = alpha;
		_camPos = _cam.WorldPosition;
		_ray = _cam.ScreenPixelToRay( Mouse.Position );
		_wpp = WorldPerPixel( _cam );
		_screenScale = style.ReferenceHeight > 1f ? Screen.Height / style.ReferenceHeight : 1f;
		ScreenBasis( out _, out _sRight, out _sUp );

		// Still draws, but won't hover/grab when the cursor is over the UI, the orbit camera owns the mouse, or the
		// pause menu is open.
		_interactive = allowInteract && !Input.Down( "Walk" ) && !AltNav.Dragging && !PauseMenu.IsOpen;
		// A drag born from the HUD's add-point panel (insert-and-place) never sees Attack1 — the UI swallows
		// the whole click gesture — so _uiDrag stands in for the held button until the panel's mouseup ends it.
		_pressed = _interactive && Input.Pressed( "Attack1" );
		_down = (_interactive && Input.Down( "Attack1" )) || _uiDrag;
		_pressed2 = _interactive && Input.Pressed( "Attack2" );
		// Clear on the NEXT press, not on release: the deselect this guards fires on the RELEASE frame
		// (that's when a tap is known to be a tap), so clearing there would unlatch a frame too early and
		// the point-delete click would deselect after all. Each new right-click starts fresh.
		if ( Input.Pressed( "Attack2" ) )
			RightClickConsumed = false;

		// Release the drag on a real button-up (even while alt is held). A spline endpoint released on the
		// loop snap merges into a closed ring — done here, before _active is cleared, so the dragged index
		// is still known.
		bool merged = false;
		if ( _active is not null && !Input.Down( "Attack1" ) && !_uiDrag )
		{
			merged = TryCloseSplineLoop( brush );
			_active = null;
		}

		// A spline is a chain of control points, not a single transform — each point gets a move dot and a
		// radius dot instead of the full gizmo. Self-contained hover/draw/drag pass.
		if ( brush.Shape == SdfShape.Spline )
			return SplineUpdate( tx, brush, scene ) | merged;

		var center = tx.PointToWorld( brush.Position );
		var rot = tx.Rotation * brush.Rotation;
		float gs = style.GizmoScale;
		float ringR = W( center, style.RotationRadius * gs );
		float tIn = W( center, (style.TranslationMin + style.TranslationOffset) * gs );
		float tOut = W( center, (style.TranslationMax + style.TranslationOffset) * gs );
		float dotReach = W( center, (style.TranslationMax + style.TranslationOffset + style.ScaleDotOffset) * gs );
		float dotR = W( center, style.DotRadius * gs );

		var axes = new Vector3[3];
		for ( int i = 0; i < 3; i++ )
			axes[i] = (rot * LocalAxis[i]).Normal;

		// PASS 1 — nearest hovered handle (skipped while the camera owns the mouse).
		_hover = null;
		_hoverDepth = float.MaxValue;
		_hoverPriority = int.MinValue;
		if ( _interactive )
			ResolveHover( center, axes, ringR, tIn, tOut, dotReach, dotR );

		// PASS 2 — emit geometry + run the active drag. The trackball disc goes FIRST: the overlay shader
		// has no depth test, so mesh order is draw order — everything else paints over it.
		BeginMesh();
		bool changed = false;

		if ( _style.ShowRotation ) changed |= Trackball( tx, brush, center, ringR );

		for ( int i = 0; i < 3; i++ )
		{
			if ( _style.ShowTranslation ) changed |= MoveAxis( tx, brush, i, center, axes[i], tIn, tOut );
			if ( _style.ShowScale ) changed |= ScaleAxis( brush, i, center, axes[i], dotReach, dotR );
			if ( _style.ShowRotation ) changed |= RotateRing( tx, brush, i, center, axes[i], ringR );
			if ( _style.ShowPlaneSquares ) changed |= PlaneMove( tx, brush, i, center, axes, ringR, tOut );
		}

		if ( _style.ShowScreenRotation ) changed |= ScreenRotate( tx, brush, center, ringR );
		if ( _style.ShowScreenMove ) changed |= ScreenMove( tx, brush, center, tOut );
		if ( _style.ShowUniformScale ) changed |= UniformScale( brush, center, gs );

		UploadMesh( scene, DrawHash( brush ) );
		return changed;
	}

	// Everything that affects the rendered gizmo: camera view, full brush transform (so a rotate/scale drag
	// re-uploads, not just a move), and the hover/drag highlight state.
	int DrawHash( SdfBrush b )
	{
		int h = HashCode.Combine( _camPos, _cam.WorldRotation, b.Position, b.Rotation, b.Size, Screen.Size );
		h = HashCode.Combine( h, _tx.Position, _tx.Rotation, _tx.Scale );
		if ( b.Points is { } pts )
		{
			foreach ( var pt in pts )
				h = HashCode.Combine( h, pt );
			h = HashCode.Combine( h, b.Curvature, b.SplineClosed );
		}
		return HashCode.Combine( h, _hover, _active, _style.VisualHash(), (int)(_masterAlpha * 100) );
	}

	/// <summary>Tear down the rendered mesh (call when leaving edit mode / disabling the session).</summary>
	public void Hide()
	{
		_so?.Delete();
		_so = null;
		_lastDrawHash = 0;
		_hover = null;   // a hidden gizmo isn't busy — clear so IsBusy can't read stale-true and block world picks
		_active = null;
		_splineLoopSnap = false; // a drag torn down mid-snap must not merge when some later drag releases
	}

	// ── hover pass ──────────────────────────────────────────────────────────────────────────────────

	// The screen-relative handles (screen-move, screen-rotate, uniform-scale) overlap the per-axis handles at
	// the gizmo centre. Give them a higher hover tier so they win the click even when an axis line/ring passes
	// nearer the camera underneath them. The trackball disc sits BELOW everything — it's the fallback when
	// no real handle is hovered.
	const int PriorityScreen = 1;
	const int PriorityTrackball = -1;

	void ResolveHover( Vector3 center, Vector3[] axes, float ringR, float tIn, float tOut, float dotReach, float dotR )
	{
		for ( int i = 0; i < 3; i++ )
		{
			if ( _style.ShowTranslation )
			{
				float d = RaySegDist( _ray, center + axes[i] * tIn, center + axes[i] * tOut, out float depth );
				Hover( $"move{i}", d, depth, LineHitRadius( center, _style.MoveThickness( false ) ) );
			}

			if ( _style.ShowScale )
			{
				float d = RayPointDist( _ray, center + axes[i] * dotReach, out float depth );
				Hover( $"scale{i}", d, depth, dotR + GrabPad( center ) );
			}

			if ( _style.ShowRotation )
				HoverRing( $"rot{i}", center, axes[i], ringR, (_camPos - center).Normal, _style.OccludeRingBackHalf );

			if ( _style.ShowPlaneSquares )
			{
				var sq = center + (axes[PlaneU[i]] + axes[PlaneV[i]]) * _style.PlaneOffset( ringR );
				float d = RayPointDist( _ray, sq, out float depth );
				Hover( $"plane{i}", d, depth, tOut * _style.PlaneScale + GrabPad( center ) );
			}
		}

		if ( _style.ShowRotation )
		{
			// Blender's inner trackball: the whole disc inside the rotation rings free-rotates in all three
			// axes. Exact radius, NO GrabPad, and priority -1 — every real handle outranks it, so it only
			// claims the click when nothing else is under the cursor.
			float dBall = RayPointDist( _ray, center, out float ballDepth );
			Hover( "trackball", dBall, ballDepth, ringR, PriorityTrackball );
		}

		if ( _style.ShowScreenRotation )
			HoverRing( "rotScreen", center, ScreenAxis( center ), ringR * _style.ScreenRingScale, default, false, PriorityScreen );

		if ( _style.ShowScreenMove )
		{
			float d = RayPointDist( _ray, center, out float depth );
			Hover( "screenMove", d, depth, tOut * _style.ScreenMoveScale + GrabPad( center ), PriorityScreen );
		}

		if ( _style.ShowUniformScale )
		{
			var pos = UniformPos( center, _style.GizmoScale, out float rad );
			float d = RayPointDist( _ray, pos, out float depth );
			Hover( "uscale", d, depth, rad + GrabPad( center ), PriorityScreen );
		}
	}

	void HoverRing( string name, Vector3 c, Vector3 axis, float r, Vector3 viewAxis, bool cull, int priority = 0 )
	{
		PerpBasis( axis, out var u, out var v );
		float radius = LineHitRadius( c, _style.RingThickness( false ) );
		int segs = _style.RingSegments;

		var prev = c + u * r;
		bool prevVis = !cull || Vector3.Dot( prev - c, viewAxis ) >= 0f;
		for ( int s = 1; s <= segs; s++ )
		{
			float a = (s / (float)segs) * MathF.PI * 2f;
			var p = c + (u * MathF.Cos( a ) + v * MathF.Sin( a )) * r;
			bool vis = !cull || Vector3.Dot( p - c, viewAxis ) >= 0f;
			if ( prevVis && vis )
				Hover( name, RaySegDist( _ray, prev, p, out float depth ), depth, radius, priority );
			prev = p; prevVis = vis;
		}
	}

	void Hover( string name, float dist, float depth, float radius, int priority = 0 )
	{
		if ( dist > radius )
			return;
		// A higher-priority handle (screen-move/rotate, uniform-scale) claims the hover outright; within the
		// same tier the nearest-to-camera handle wins.
		if ( priority < _hoverPriority )
			return;
		if ( priority == _hoverPriority && depth >= _hoverDepth )
			return;

		_hover = name;
		_hoverDepth = depth;
		_hoverPriority = priority;
	}

	// ── spline (per-control-point handles) ───────────────────────────────────────────────────────────

	static readonly List<Vector4> _splineCurve = new(); // reused buffer for the curve polyline

	Vector4 _splineInsertPoint; // candidate control point under the cursor on the line (sculpture-local pos + radius)
	int _splineInsertIndex;     // list index it would insert at

	/// <summary>True while the cursor is over a selected spline's line (between control points) — the HUD shows
	/// the add-point cursor, and a click inserts a control point there at the curve's interpolated radius.</summary>
	public bool HoveringSplineLine => _hover == "splineLine";

	/// <summary>Insert the candidate control point computed while line-hovering (curve position + interpolated
	/// radius) into <paramref name="brush"/>. Driven by the HUD's add-point cursor panel on click — that
	/// pointer-events:all surface swallows the raw Attack1, so the gizmo can't insert on its own. No-op (false)
	/// unless the line is currently hovered.</summary>
	public bool TryInsertSplinePoint( SdfBrush brush )
	{
		if ( _hover != "splineLine" || brush?.Points is null )
			return false;

		int idx = Math.Clamp( _splineInsertIndex, 0, brush.Points.Count );
		brush.Points.Insert( idx, _splineInsertPoint );

		// The insert click is also the start of a move drag: grab the new point's handle right away, so
		// add-and-place is one gesture (SplinePointMove's active-drag path takes over; releasing without
		// moving just leaves the point where it was born). The UI swallows Attack1 for this whole gesture,
		// so _uiDrag keeps the grab alive — the HUD panel's mouseup calls EndUiDrag to finish it.
		// _tx/_cam/_ray are from the last Update — the same frame's state the hover that led here used.
		var c = _tx.PointToWorld( new Vector3( _splineInsertPoint.x, _splineInsertPoint.y, _splineInsertPoint.z ) );
		if ( _cam is not null && new Plane( c, -_cam.WorldRotation.Forward ).TryTrace( _ray, out var hit, true ) )
		{
			_active = $"ptMove{idx}";
			_hover = _active;
			_grabPoint = hit;
			_grabWorldPos = c;
			_uiDrag = true;
		}

		return true;
	}

	bool _uiDrag; // an insert-and-place drag is running with Attack1 swallowed by the UI (see TryInsertSplinePoint)

	/// <summary>True while an insert-and-place drag (born from the HUD's add-point panel) is running.</summary>
	public bool IsUiDragging => _uiDrag;

	/// <summary>End the insert-and-place drag — called from the HUD panel's mouseup, the only place the
	/// gesture's release is visible (the UI swallows the button from raw input for the whole click).</summary>
	public void EndUiDrag()
	{
		if ( !_uiDrag )
			return;
		_uiDrag = false;
		_active = null;
	}

	bool SplineUpdate( Transform tx, SdfBrush brush, Scene scene )
	{
		var pts = brush.Points;
		float gs = _style.GizmoScale;
		var n = -_cam.WorldRotation.Forward; // screen plane normal for drags

		// PASS 1 — hover the move + radius dots (skipped while the camera owns the mouse / over UI).
		_hover = null;
		_hoverDepth = float.MaxValue;
		_hoverPriority = int.MinValue;
		if ( _interactive && pts is { Count: > 0 } )
		{
			for ( int i = 0; i < pts.Count; i++ )
			{
				var c = tx.PointToWorld( new Vector3( pts[i].x, pts[i].y, pts[i].z ) );
				Hover( $"ptMove{i}", RayPointDist( _ray, c, out float dm ), dm, W( c, _style.SplinePointRadius * gs ) + GrabPad( c ) );
				// Every point carries its own radius — the 3D ring is the ONLY size control now (the HUD's
				// uniform Size slider + per-point toggle are gone).
				HoverRing( $"ptRad{i}", c, (_camPos - c).Normal, pts[i].w, default, false );
			}
		}

		// Line hover (only when no dot is under the cursor): the nearest point ON the curve to the cursor ray.
		// Hovering it shows the add-point cursor and a click inserts a control point there at the curve's
		// interpolated radius. Densely sampled (fixed, not curvature-dependent) so you can insert anywhere,
		// including along a straight span.
		if ( _interactive && _hover is null && pts is { Count: >= 2 } )
		{
			const int HoverSamples = 16;
			float best = float.MaxValue;
			int np = pts.Count;
			for ( int i = 0; i < np - 1; i++ )
			{
				var p0 = pts[i > 0 ? i - 1 : 0];
				var p1 = pts[i];
				var p2 = pts[i + 1];
				var p3 = pts[i < np - 2 ? i + 2 : np - 1];
				var prevW = tx.PointToWorld( new Vector3( p1.x, p1.y, p1.z ) );
				for ( int sIdx = 1; sIdx <= HoverSamples; sIdx++ )
				{
					var cur = SdfBrush.CatmullRomPoint( p0, p1, p2, p3, sIdx / (float)HoverSamples, brush.Curvature );
					var curW = tx.PointToWorld( new Vector3( cur.x, cur.y, cur.z ) );
					float d = RaySegDist( _ray, prevW, curW, out _ );
					// Hit zone = a fraction of the TUBE radius (SplineLineHitScale) + a small grab margin, so it
					// can be tuned from "anywhere on the body" down to "near the centre line". (Unit scale:
					// cur.w local radius ≈ world radius.)
					if ( d <= cur.w * _style.SplineLineHitScale + GrabPad( curW ) && d < best )
					{
						best = d;
						_hover = "splineLine";
						_splineInsertPoint = cur;
						_splineInsertIndex = i + 1;
					}
					prevW = curW;
				}
			}
		}

		// PASS 2 — emit geometry + run the active drag.
		BeginMesh();
		bool changed = false;

		if ( pts is { Count: > 0 } )
		{
			// Centre line follows the curve (tessellated when curved); dots stay on the control points. The
			// line brightens while hovered (the add-point affordance).
			bool lineHot = _hover == "splineLine";
			var lineCol = Tint( _style.SplineLineColor.WithAlpha( _style.SplineOpacity( lineHot ) ), "transform" );
			brush.BuildSplinePolyline( _splineCurve );
			SplineRibbon( _splineCurve, tx, _style.SplineThickness( lineHot ), lineCol );

			// In-between dots along the line (interactive-line hint), evenly spaced per span at the set frequency.
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
						Disc( w, W( w, _style.SplineMidDotRadius * gs ), lineCol );
					}
				}
			}

			for ( int i = 0; i < pts.Count; i++ )
			{
				// A right-click inside SplinePointMove DELETES point i, shrinking the list under us — the
				// radius handle would then index a point that's gone (out of range on the last one) and
				// every later index has shifted. Bail out for this frame; the delete already cleared the
				// hover and flagged the mesh stale, so next frame redraws cleanly from the new list.
				int before = pts.Count;
				changed |= SplinePointMove( tx, brush, i, gs, n );
				if ( pts.Count != before )
					break;

				changed |= SplinePointRadius( tx, brush, i, gs, n );
			}

			// Close-loop and per-point-radius are HUD chips now (next to the sliders), not 3D handles.
			// The line-click insert is driven by the HUD's add-point cursor panel (TryInsertSplinePoint), not
			// here — that pointer-events:all panel sits under the cursor and swallows the raw Attack1 click.
		}

		UploadMesh( scene, DrawHash( brush ) );
		if ( _splineMeshStale ) // a mid-pass point delete drew stale geometry — force a re-upload next frame
		{
			_lastDrawHash = 0;
			_splineMeshStale = false;
		}
		return changed;
	}

	bool _splineMeshStale; // set when a point is deleted AFTER the curve ribbon was already emitted this frame

	// End-to-end loop snap while dragging a control point: the dragged endpoint is sitting on the other
	// endpoint, and releasing there merges them into a closed ring. Mirrors the stamp tool's chain snap.
	bool _splineLoopSnap;
	int _splineLoopIndex;
	const float LoopSnapScale = 1.1f; // multiple of the target point's radius that counts as "touching"

	/// <summary>True while a dragged spline endpoint is snapped onto the other end — releasing closes the
	/// ring. Lets the HUD say so.</summary>
	public bool SplineWillLoop => _splineLoopSnap;

	// Release landed on the snap: drop the now-duplicate dragged point and close the curve. Returns true
	// when the shape changed. Called before _active is cleared, so the index is still meaningful.
	bool TryCloseSplineLoop( SdfBrush brush )
	{
		// Only a POINT-MOVE drag can arm this; a radius drag releasing must never merge on a stale flag.
		if ( !_splineLoopSnap || _active is null || !_active.StartsWith( "ptMove" ) )
			return false;

		_splineLoopSnap = false;
		if ( brush?.Points is not { Count: >= 4 } pts || _splineLoopIndex < 0 || _splineLoopIndex >= pts.Count )
			return false;

		pts.RemoveAt( _splineLoopIndex );
		brush.SplineClosed = true;
		_hover = null; // the removed point's handle name must not linger as hovered
		return true;
	}

	// One continuous welded ribbon along the curve polyline — like DrawRing, consecutive samples SHARE their
	// two edge vertices, so the segments butt together gap-free with no end caps. Replaces drawing each span as
	// its own capsule (whose rounded caps left little dots where they overlapped). Width faces the camera.
	void SplineRibbon( List<Vector4> path, Transform tx, float thPx, Color col )
	{
		int n = path.Count;
		if ( n < 2 )
			return;

		int prevOut = -1, prevIn = -1;
		for ( int i = 0; i < n; i++ )
		{
			var ai = path[i > 0 ? i - 1 : i];
			var bi = path[i < n - 1 ? i + 1 : i];
			var p = tx.PointToWorld( new Vector3( path[i].x, path[i].y, path[i].z ) );
			var a = tx.PointToWorld( new Vector3( ai.x, ai.y, ai.z ) );
			var b = tx.PointToWorld( new Vector3( bi.x, bi.y, bi.z ) );

			var tangent = b - a;
			tangent = tangent.LengthSquared > 1e-8f ? tangent.Normal : Vector3.Forward;
			var toCam = (_camPos - p).Normal;
			var off = Vector3.Cross( tangent, toCam ).Normal * W( p, thPx * 0.5f );

			int outV = AddVert( p + off, col );
			int inV = AddVert( p - off, col );
			if ( i > 0 )
				Quad( prevOut, prevIn, inV, outV ); // shared-edge strip segment — gap-free by construction
			prevOut = outV;
			prevIn = inV;
		}
	}

	bool SplinePointMove( Transform tx, SdfBrush brush, int i, float gs, Vector3 n )
	{
		string name = $"ptMove{i}";
		bool hot = IsHot( name );
		var pt = brush.Points[i];
		var c = tx.PointToWorld( new Vector3( pt.x, pt.y, pt.z ) );
		float dotR = W( c, _style.SplinePointRadius * gs );
		Disc( c, hot ? dotR * _style.HotScale : dotR, Tint( hot ? Color.White : _style.SplineLineColor, name ) );

		// Right-click deletes this control point — but never below 2 (the tube needs a span). Dropping under
		// 3 can't stay a ring, so un-loop too. Hover is cleared so nothing reads the stale point name.
		if ( _pressed2 && _hover == name && brush.Points.Count > 2 )
		{
			RightClickConsumed = true; // this click was a point delete — it must not also deselect
			brush.Points.RemoveAt( i );
			if ( brush.Points.Count < 3 )
				brush.SplineClosed = false;
			_hover = null;
			// This frame's mesh was emitted from the PRE-delete points but gets stamped with the post-delete
			// hash — mark it stale so next frame re-uploads instead of matching the hash and keeping the old curve.
			_splineMeshStale = true;
			return true;
		}

		if ( _pressed && _hover == name && new Plane( c, n ).TryTrace( _ray, out var hit0, true ) )
		{
			_active = name;
			_grabPoint = hit0;
			_grabWorldPos = c;
		}

		if ( _active == name )
		{
			// Scroll while dragging pushes/pulls the point along the VIEW RAY. Both drag anchors shift by
			// the same amount along the view, which keeps the accumulated cursor delta intact and moves the
			// drag plane to the new depth — so the point stays under the cursor as it travels, exactly like
			// the stamp ghost's scroll-depth. The step IS the shared accelerated one (sized by the point's
			// radius, scroll-accelerated, camera-distance-scaled), so pushing a point feels identical to
			// pushing the ghost or the whole selection.
			float wheel = Input.MouseWheel.y;
			if ( wheel != 0f )
			{
				float camDist = (_grabWorldPos - _camPos).Length;
				float step = SculptEditSession.Current is { } session
					? session.DepthStep( brush.Points[i].w, camDist )
					: MathF.Max( 1f, brush.Points[i].w ) * 0.5f; // no session (shouldn't happen) — old fixed step
				var push = _cam.WorldRotation.Forward * (wheel * step);
				_grabWorldPos += push;
				_grabPoint += push;
			}

			if ( _down && new Plane( _grabWorldPos, n ).TryTrace( _ray, out var hit, true ) )
			{
				var local = tx.PointToLocal( _grabWorldPos + (hit - _grabPoint) );

				// Shift snaps the point onto the shared grid — a free move, so absolute, like the stamp.
				// Applied BEFORE the loop snap below, which overrides it: closing the ring always wins.
				if ( SculptEditSession.SnapHeld )
					local = SculptEditSession.SnapPosition( local );

				// Loop snap, the same gesture sculpt mode's chain has: drag one END of an open curve onto
				// the other and it sticks there. Releasing merges them into a closed ring (see
				// TryCloseSplineLoop). Needs 4+ points so 3 — a real ring — survive the merge.
				_splineLoopSnap = false;
				if ( !brush.SplineClosed && brush.Points.Count >= 4 && (i == 0 || i == brush.Points.Count - 1) )
				{
					var other = brush.Points[i == 0 ? ^1 : 0];
					var otherLocal = new Vector3( other.x, other.y, other.z );
					if ( (local - otherLocal).Length <= MathF.Max( 4f, other.w * LoopSnapScale ) )
					{
						local = otherLocal;
						_splineLoopSnap = true;
						_splineLoopIndex = i;
					}
				}

				brush.Points[i] = new Vector4( local.x, local.y, local.z, brush.Points[i].w ); // keep radius
				return true;
			}
		}

		return false;
	}

	bool SplinePointRadius( Transform tx, SdfBrush brush, int i, float gs, Vector3 n )
	{
		string name = $"ptRad{i}";
		bool hot = IsHot( name );
		var pt = brush.Points[i];
		var c = tx.PointToWorld( new Vector3( pt.x, pt.y, pt.z ) );

		// Camera-facing circle AT the point's radius — the circle IS the size readout. Highlighted like the
		// rotation rings (brighter + thicker while hot); grab and drag toward/away from the centre to scale.
		var toCam = (_camPos - c).Normal;
		DrawRing( c, toCam, pt.w, _style.RingThickness( hot ), Tint( Highlight( _style.SplineRadiusColor, hot ), name ), toCam, false );

		if ( _pressed && _hover == name && new Plane( c, n ).TryTrace( _ray, out _, true ) )
		{
			_active = name;
			_grabWorldPos = c;
		}

		if ( _active == name && _down && new Plane( _grabWorldPos, n ).TryTrace( _ray, out var hit, true ) )
		{
			float newR = MathF.Max( 1f, (hit - _grabWorldPos).Length );
			brush.Points[i] = new Vector4( pt.x, pt.y, pt.z, newR );
			return true;
		}

		return false;
	}

	// ── handles (draw + drag) ────────────────────────────────────────────────────────────────────────

	bool MoveAxis( Transform tx, SdfBrush brush, int i, Vector3 c, Vector3 axis, float inner, float outer )
	{
		string name = $"move{i}";
		bool hot = IsHot( name );
		Line( c + axis * inner, c + axis * outer, _style.MoveThickness( hot ), Tint( Highlight( AxisColors[i], hot ), name ) );

		if ( _pressed && _hover == name )
		{
			_active = name;
			_grabT = ClosestAxisT( c, axis, _ray );
			_grabWorldPos = c;
		}

		if ( _active == name && _down )
		{
			float t = ClosestAxisT( _grabWorldPos, axis, _ray );
			float move = t - _grabT;
			// Shift snaps the travel along the axis to grid-step increments. The axis is the BRUSH's local
			// axis (arbitrary once rotated), so the DISPLACEMENT quantizes — snapping the absolute position
			// would yank a rotated brush off its own constraint line.
			if ( SculptEditSession.SnapHeld )
				move = MathF.Round( move / SculptEditSession.GridStep ) * SculptEditSession.GridStep;
			brush.Position = tx.PointToLocal( _grabWorldPos + axis * move );
			return true;
		}

		return false;
	}

	// Smallest a brush dimension may be dragged to (the scale floor). Text goes 40% finer than the solid
	// shapes: its binding dimension is the shallow extrusion depth, so the default floor stops it well before
	// the letters get as small as small captions/labels want.
	static float ScaleFloor( SdfBrush brush ) => brush.Shape == SdfShape.Text ? 0.6f : 1f;

	bool ScaleAxis( SdfBrush brush, int i, Vector3 c, Vector3 axis, float reach, float dotR )
	{
		string name = $"scale{i}";
		bool hot = IsHot( name );
		Disc( c + axis * reach, hot ? dotR * _style.HotScale : dotR, Tint( Highlight( AxisColors[i], hot ), name ) );

		if ( _pressed && _hover == name )
		{
			_active = name;
			_grabT = ClosestAxisT( c, axis, _ray );
			_grabHalf = AxisHalf( brush, i );
		}

		if ( _active == name && _down )
		{
			float t = ClosestAxisT( c, axis, _ray );
			SetAxisSize( brush, i, MathF.Max( ScaleFloor( brush ), _grabHalf + (t - _grabT) ) );
			return true;
		}

		return false;
	}

	// Blender-style trackball: the disc inside the rotation rings. Invisible until hovered (then a faint
	// white fill), and dragging free-rotates about the SCREEN axes — mouse X spins about the camera's up,
	// mouse Y about its right — so the shape tumbles under the cursor in all three axes at once. Shift
	// held snaps to the shared angle grid, same as the E-rotate scrub and the rings: the motion
	// accumulates on the RAW orientation and only the applied value quantizes (absolutely — a brush at 8°
	// lands on the grid, never 8°+step).
	Vector2 _trackPx;   // last frame's cursor position during a trackball drag (the drag is delta-driven)
	Rotation _trackRaw; // continuous unsnapped orientation, sculpture-local — Shift applies its snapped form
	const float TrackballDegPerPx = 0.4f; // matches BrushScrub's DegPerPx — one free-rotate feel

	bool Trackball( Transform tx, SdfBrush brush, Vector3 c, float radius )
	{
		string name = "trackball";
		bool hot = IsHot( name );

		if ( hot )
			Disc( c, radius, Tint( Color.White.WithAlpha( _active == name ? 0.2f : 0.12f ), name ), 40 );

		if ( _pressed && _hover == name )
		{
			_active = name;
			_trackPx = Mouse.Position;
			_trackRaw = brush.Rotation;
		}

		if ( _active == name && _down )
		{
			var d = Mouse.Position - _trackPx;
			_trackPx = Mouse.Position;
			if ( d.LengthSquared < 0.0001f )
				return false;

			var worldRot = tx.Rotation * _trackRaw;
			worldRot = Rotation.FromAxis( _cam.WorldRotation.Up, d.x * TrackballDegPerPx )
				* Rotation.FromAxis( _cam.WorldRotation.Right, d.y * TrackballDegPerPx )
				* worldRot;
			_trackRaw = tx.Rotation.Inverse * worldRot;

			var applied = SculptEditSession.SnapHeld ? SculptEditSession.SnapRotation( _trackRaw ) : _trackRaw;
			if ( applied == brush.Rotation )
				return false; // still in the same grid cell — no rebuild churn

			brush.Rotation = applied;
			return true;
		}

		return false;
	}

	bool RotateRing( Transform tx, SdfBrush brush, int i, Vector3 c, Vector3 axis, float r )
	{
		string name = $"rot{i}";
		bool hot = IsHot( name );
		DrawRing( c, axis, r, _style.RingThickness( hot ), Tint( Highlight( AxisColors[i], hot ), name ),
			(_camPos - c).Normal, _style.OccludeRingBackHalf );
		return RingInteract( name, tx, brush, c, axis );
	}

	bool ScreenRotate( Transform tx, SdfBrush brush, Vector3 c, float ringR )
	{
		string name = "rotScreen";
		bool hot = IsHot( name );
		var axis = ScreenAxis( c );
		Color col = hot ? Color.White : Color.White.WithAlpha( 0.6f );
		DrawRing( c, axis, ringR * _style.ScreenRingScale, _style.RingThickness( hot ), Tint( col, name ), default, false );
		return RingInteract( name, tx, brush, c, axis );
	}

	bool RingInteract( string name, Transform tx, SdfBrush brush, Vector3 c, Vector3 axis )
	{
		if ( _pressed && _hover == name )
		{
			PerpBasis( axis, out var u, out var v );
			_active = name;
			_grabWorldRotAxis = axis;
			_grabAngle = RingAngle( c, axis, u, v, out _ );
			_accumAngle = 0f;
			_grabWorldRot = tx.Rotation * brush.Rotation;

			// The brush's existing twist about the drag axis, in sculpture-local space (swing-twist
			// decomposition) — lets Shift snap the brush's TOTAL angle to the shared angle grid, not the
			// drag delta.
			var la = (tx.Rotation.Inverse * axis).Normal;
			var lr = brush.Rotation;
			_grabTwist = 2f * MathF.Atan2( lr.x * la.x + lr.y * la.y + lr.z * la.z, lr.w );
		}

		if ( _active != name || !_down )
			return false;

		var ax = _grabWorldRotAxis;
		PerpBasis( ax, out var pu, out var pv );
		float ang = RingAngle( c, ax, pu, pv, out bool ok );
		if ( !ok )
			return false;

		float delta = ang - _grabAngle;
		if ( delta > MathF.PI ) delta -= MathF.PI * 2f;
		else if ( delta < -MathF.PI ) delta += MathF.PI * 2f;

		_accumAngle += delta;
		_grabAngle = ang;

		// Shift snaps the absolute angle (the shared snap hold, same grid as the E-rotate scrub): a brush
		// already at 2.1° lands on 22.5°, not 24.6°. Snap the total twist (pre-drag + dragged) to the grid,
		// then apply what's left after the pre-drag part.
		float applied = _accumAngle;
		if ( SculptEditSession.SnapHeld )
		{
			const float step = SculptEditSession.SnapDeg * (MathF.PI / 180f);
			applied = MathF.Round( (_grabTwist + _accumAngle) / step ) * step - _grabTwist;
		}

		brush.Rotation = tx.Rotation.Inverse * (Rotation.FromAxis( ax, applied.RadianToDegree() ) * _grabWorldRot);
		return true;
	}

	bool ScreenMove( Transform tx, SdfBrush brush, Vector3 c, float reach )
	{
		string name = "screenMove";
		bool hot = IsHot( name );
		float draw = reach * _style.ScreenMoveScale * (hot ? _style.HotScale : 1f);
		RoundedQuad( c, _sRight, _sUp, draw, draw * _style.ScreenMoveRounding,
			Tint( hot ? Color.Lerp( ScreenMoveColor, Color.White, 0.5f ) : ScreenMoveColor, name ) );

		var n = -_cam.WorldRotation.Forward;

		if ( _pressed && _hover == name && new Plane( c, n ).TryTrace( _ray, out var hit0, true ) )
		{
			_active = name;
			_grabPoint = hit0;
			_grabWorldPos = c;
		}

		if ( _active == name && _down && new Plane( _grabWorldPos, n ).TryTrace( _ray, out var hit, true ) )
		{
			// Screen-move is a free move (camera-parallel plane, like stamp steering), so Shift snaps the
			// ABSOLUTE sculpture-local position onto the shared grid — exactly what the stamp does.
			var pos = tx.PointToLocal( _grabWorldPos + (hit - _grabPoint) );
			if ( SculptEditSession.SnapHeld )
				pos = SculptEditSession.SnapPosition( pos );
			brush.Position = pos;
			return true;
		}

		return false;
	}

	bool UniformScale( SdfBrush brush, Vector3 c, float gs )
	{
		string name = "uscale";
		bool hot = IsHot( name );
		var pos = UniformPos( c, gs, out float rad );
		Disc( pos, hot ? rad * _style.HotScale : rad, Tint( hot ? Color.White : Color.White.WithAlpha( 0.85f ), name ) );

		ScreenBasis( out var n, out var right, out var up );
		var diag = (up - right).Normal;

		if ( _pressed && _hover == name && new Plane( c, n ).TryTrace( _ray, out var hit0, true ) )
		{
			_active = name;
			_grabPoint = hit0;
			_grabSize = brush.Size;
			_grabWorldPos = c;
		}

		if ( _active == name && _down && new Plane( _grabWorldPos, n ).TryTrace( _ray, out var hit, true ) )
		{
			float worldPerPixel = MathF.Max( (_camPos - _grabWorldPos).Length * _wpp, 1e-6f );
			float deltaPx = Vector3.Dot( hit - _grabPoint, diag ) / worldPerPixel;
			float factor = MathF.Pow( 2f, deltaPx / MathF.Max( 1f, _style.UniformScaleSensitivity ) );

			float minComp = MathF.Min( _grabSize.x, MathF.Min( _grabSize.y, _grabSize.z ) );
			factor = MathF.Max( factor, ScaleFloor( brush ) / MathF.Max( minComp, 0.0001f ) );
			brush.Size = _grabSize * factor;
			return true;
		}

		return false;
	}

	bool PlaneMove( Transform tx, SdfBrush brush, int i, Vector3 c, Vector3[] axes, float ringR, float sizeReach )
	{
		string name = $"plane{i}";
		bool hot = IsHot( name );
		var n = axes[i];
		var u = axes[PlaneU[i]];
		var v = axes[PlaneV[i]];
		var sq = c + (u + v) * _style.PlaneOffset( ringR );
		float draw = sizeReach * _style.PlaneScale * (hot ? _style.HotScale : 1f);

		RoundedQuad( sq, u, v, draw, draw * _style.PlaneRounding, Tint( Highlight( AxisColors[i], hot ).WithAlpha( 0.6f ), name ) );

		if ( _pressed && _hover == name && new Plane( c, n ).TryTrace( _ray, out var hit0, true ) )
		{
			_active = name;
			_grabPoint = hit0;
			_grabWorldPos = c;
		}

		if ( _active == name && _down && new Plane( _grabWorldPos, n ).TryTrace( _ray, out var hit, true ) )
		{
			var d = hit - _grabPoint;
			// Shift snaps the in-plane travel to grid-step increments along the plane's own axes — the
			// plane is brush-rotated, so (like the axis handles) the displacement quantizes, not the
			// absolute position.
			if ( SculptEditSession.SnapHeld )
			{
				float du = MathF.Round( Vector3.Dot( d, u ) / SculptEditSession.GridStep ) * SculptEditSession.GridStep;
				float dv = MathF.Round( Vector3.Dot( d, v ) / SculptEditSession.GridStep ) * SculptEditSession.GridStep;
				d = u * du + v * dv;
			}
			brush.Position = tx.PointToLocal( _grabWorldPos + d );
			return true;
		}

		return false;
	}

	// ── geometry helpers shared by both passes ───────────────────────────────────────────────────────

	Vector3 ScreenAxis( Vector3 c )
	{
		var axis = (_camPos - c).Normal;
		return axis.LengthSquared < 0.001f ? _cam.WorldRotation.Forward : axis;
	}

	Vector3 UniformPos( Vector3 c, float gs, out float rad )
	{
		var pos = c + (_sRight + _sUp) * W( c, _style.UniformScaleOffset * gs );
		rad = W( pos, _style.UniformScaleRadius * gs );
		return pos;
	}

	// ── mesh building (solid, vertex-coloured, camera-facing) ────────────────────────────────────────

	void BeginMesh()
	{
		_verts.Clear();
		_indices.Clear();
		_bbMin = new Vector3( float.MaxValue );
		_bbMax = new Vector3( float.MinValue );
	}

	int AddVert( Vector3 p, Color col )
	{
		_bbMin = Vector3.Min( _bbMin, p );
		_bbMax = Vector3.Max( _bbMax, p );
		_verts.Add( new Vertex( p, Vector3.Up, Vector3.Forward, Vector4.Zero ) { Color = col } );
		return _verts.Count - 1;
	}

	void Tri( int a, int b, int c )
	{
		_indices.Add( a ); _indices.Add( b ); _indices.Add( c );
	}

	void Quad( int a, int b, int c, int d )
	{
		Tri( a, b, c ); Tri( a, c, d );
	}

	// A flat camera-facing ribbon from a to b, half-widths haA/haB (world). The perpendicular faces the
	// camera, so the ribbon reads as a constant-width screen-space line like the editor's gizmo lines.
	void Ribbon( Vector3 a, Vector3 b, float haA, float haB, Color col )
	{
		var dir = b - a;
		if ( dir.LengthSquared < 1e-8f )
			return;
		var toCam = (_camPos - (a + b) * 0.5f).Normal;
		var perp = Vector3.Cross( dir.Normal, toCam ).Normal;
		Quad( AddVert( a + perp * haA, col ), AddVert( a - perp * haA, col ),
			AddVert( b - perp * haB, col ), AddVert( b + perp * haB, col ) );
	}

	// Straight thick line with rounded end caps (a capsule's silhouette, but flat/camera-facing).
	void Line( Vector3 a, Vector3 b, float thPx, Color col )
	{
		Ribbon( a, b, W( a, thPx * 0.5f ), W( b, thPx * 0.5f ), col );
		if ( _style.LineCapScale > 0f )
		{
			Disc( a, W( a, thPx * _style.LineCapScale * 0.5f ), col );
			Disc( b, W( b, thPx * _style.LineCapScale * 0.5f ), col );
		}
	}

	// A camera-facing filled disc (round dot).
	void Disc( Vector3 c, float radius, Color col, int segs = 20 )
	{
		if ( radius <= 1e-4f )
			return;
		int c0 = AddVert( c, col );
		int prev = AddVert( c + _sRight * radius, col );
		int first = prev;
		for ( int s = 1; s <= segs; s++ )
		{
			float a = (s / (float)segs) * MathF.PI * 2f;
			var p = c + (_sRight * MathF.Cos( a ) + _sUp * MathF.Sin( a )) * radius;
			int cur = s == segs ? first : AddVert( p, col );
			Tri( c0, prev, cur );
			prev = cur;
		}
	}

	// A filled rounded square in the given (right, up) basis — camera-facing for screen handles, in-plane
	// for plane squares. Port of the editor gizmo's SolidRoundedSquare (fan from the centre).
	void RoundedQuad( Vector3 c, Vector3 right, Vector3 up, float half, float cornerR, Color col, int cornerSegs = 4 )
	{
		if ( half <= 1e-4f )
			return;
		cornerR = MathF.Max( 0f, MathF.Min( cornerR, half ) );
		float a = half - cornerR;

		int c0 = AddVert( c, col );
		int first = -1, prev = -1;
		for ( int k = 0; k < 4; k++ )
		{
			float sx = (k == 0 || k == 3) ? 1f : -1f;
			float sy = (k == 0 || k == 1) ? 1f : -1f;
			for ( int s = 0; s <= cornerSegs; s++ )
			{
				float ang = (k * 90f + (s / (float)cornerSegs) * 90f) * (MathF.PI / 180f);
				float x = sx * a + cornerR * MathF.Cos( ang );
				float y = sy * a + cornerR * MathF.Sin( ang );
				int cur = AddVert( c + right * x + up * y, col );
				if ( prev >= 0 ) Tri( c0, prev, cur );
				if ( first < 0 ) first = cur;
				prev = cur;
			}
		}
		Tri( c0, prev, first );
	}

	// A camera-facing ring drawn as ONE continuous ribbon: each sample's two edge vertices are shared by the
	// segments on either side, so consecutive quads butt together with no gap on the outside of the curve
	// (the faint dashes you get from drawing each chord as an independent quad). The width runs perpendicular
	// to the circle's tangent at each sample, so the shared edges line up exactly.
	void DrawRing( Vector3 c, Vector3 axis, float r, float thPx, Color col, Vector3 viewAxis, bool cull )
	{
		PerpBasis( axis, out var u, out var v );
		int segs = _style.RingSegments;

		Vector3 prevP = default;
		int prevOut = -1, prevIn = -1;
		bool prevVis = false;
		for ( int s = 0; s <= segs; s++ )
		{
			float a = (s / (float)segs) * MathF.PI * 2f;
			float ca = MathF.Cos( a ), sa = MathF.Sin( a );
			var p = c + (u * ca + v * sa) * r;
			bool vis = !cull || Vector3.Dot( p - c, viewAxis ) >= 0f;

			// Offset from the tangent (not the chord), so this sample's two edge verts are shared cleanly
			// with both neighbouring segments — the join is a single welded edge, not two near-aligned ones.
			var tangent = (v * ca - u * sa).Normal;
			var toCam = (_camPos - p).Normal;
			var off = Vector3.Cross( tangent, toCam ).Normal * W( p, thPx * 0.5f );
			int outV = AddVert( p + off, col );
			int inV = AddVert( p - off, col );

			if ( s > 0 )
			{
				if ( prevVis && vis )
					Quad( prevOut, prevIn, inV, outV ); // shared-edge strip segment — gap-free by construction
				else if ( prevVis != vis && _style.LineCapScale > 0f )
				{
					// Round the open end where a back-culled arc starts/ends (cap the visible endpoint).
					var end = vis ? p : prevP;
					Disc( end, W( end, thPx * _style.LineCapScale * 0.5f ), col );
				}
			}

			prevP = p; prevOut = outV; prevIn = inV; prevVis = vis;
		}
	}

	void UploadMesh( Scene scene, int hash )
	{
		if ( _verts.Count < 3 )
		{
			Hide();
			return;
		}

		// Only re-upload when the picture actually changed (camera/brush/hover/drag), so static frames
		// don't churn GPU buffers.
		if ( hash == _lastDrawHash && _so.IsValid() )
			return;
		_lastDrawHash = hash;

		_material ??= Material.FromShader( "shaders/gizmo.shader" );

		var mesh = new Mesh( _material );
		mesh.CreateVertexBuffer( _verts.Count, _verts );
		mesh.CreateIndexBuffer( _indices.Count, _indices );
		mesh.Bounds = new BBox( _bbMin, _bbMax );

		var model = new ModelBuilder().AddMesh( mesh ).Create();

		if ( _so.IsValid() )
		{
			_so.Model = model;
		}
		else
		{
			_so = new SceneObject( scene.SceneWorld, model );
			_so.Transform = Transform.Zero; // vertices are already in world space
			_so.Flags.CastShadows = false;
			_so.Batchable = false;
			// Render in the overlay pass — AFTER post-processing — so DOF/bloom/tonemap don't touch the
			// gizmo. It draws on top like UI.
			_so.RenderLayer = SceneRenderLayer.OverlayWithoutDepth;
		}

		_so.Bounds = new BBox( _bbMin, _bbMax );
		_so.RenderingEnabled = true;
	}

	// ── sizing + math (ported from the editor gizmo) ─────────────────────────────────────────────────

	float W( Vector3 at, float px ) => px * _screenScale * (_camPos - at).Length * _wpp;
	float GrabPad( Vector3 at ) => W( at, _style.HitboxPadding + 5f );
	float LineHitRadius( Vector3 at, float thPx ) => W( at, thPx * 0.5f ) + GrabPad( at );

	static float WorldPerPixel( CameraComponent cam )
	{
		var c = Screen.Size * 0.5f;
		var r0 = cam.ScreenPixelToRay( c );
		var r1 = cam.ScreenPixelToRay( c + new Vector2( 0f, 1f ) );
		return (r1.Forward - r0.Forward).Length;
	}

	static float RaySegDist( Ray ray, Vector3 a, Vector3 b, out float depth )
	{
		var ab = b - a;
		float len = ab.Length;
		var dir = len > 1e-4f ? ab / len : Vector3.Forward;
		float t = Math.Clamp( ClosestAxisT( a, dir, ray ), 0f, len );
		var segPt = a + dir * t;
		depth = MathF.Max( 0f, Vector3.Dot( segPt - ray.Position, ray.Forward ) );
		return (segPt - (ray.Position + ray.Forward * depth)).Length;
	}

	static float RayPointDist( Ray ray, Vector3 p, out float depth )
	{
		depth = MathF.Max( 0f, Vector3.Dot( p - ray.Position, ray.Forward ) );
		return (p - (ray.Position + ray.Forward * depth)).Length;
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

	float RingAngle( Vector3 c, Vector3 normal, Vector3 u, Vector3 v, out bool ok )
	{
		if ( MathF.Abs( Vector3.Dot( _ray.Forward, normal ) ) < 0.09f )
		{
			ok = false;
			return 0f;
		}

		if ( !new Plane( c, normal ).TryTrace( _ray, out var hit, true ) )
		{
			ok = false;
			return 0f;
		}

		ok = true;
		var d = hit - c;
		return MathF.Atan2( Vector3.Dot( d, v ), Vector3.Dot( d, u ) );
	}

	void ScreenBasis( out Vector3 normal, out Vector3 right, out Vector3 up )
	{
		normal = -_cam.WorldRotation.Forward;
		right = Vector3.Cross( normal, Vector3.Up );
		if ( right.LengthSquared < 0.001f )
			right = Vector3.Cross( normal, Vector3.Forward );
		right = right.Normal;
		up = Vector3.Cross( right, normal ).Normal;
	}

	static void PerpBasis( Vector3 axis, out Vector3 u, out Vector3 v )
	{
		u = Vector3.Cross( axis, Vector3.Up );
		if ( u.LengthSquared < 0.001f )
			u = Vector3.Cross( axis, Vector3.Forward );
		u = u.Normal;
		v = Vector3.Cross( axis, u ).Normal;
	}

	// Handle colour through the gizmo's master fade. (The old per-family fade — dimming handle groups against
	// each other during a drag — died with the 3D blend/round sliders; every handle is "transform" now. The
	// name parameter stays so call sites read naturally and a future group fade has its hook back.)
	Color Tint( Color c, string name ) => c.WithAlpha( c.a * _masterAlpha );

	bool IsHot( string name ) => _active == name || (_hover == name && _active is null);

	static Color Highlight( Color c, bool on ) => on ? Color.Lerp( c, Color.White, 0.4f ) : c;
	static float AxisHalf( SdfBrush b, int i ) => i == 0 ? b.Size.x : i == 1 ? b.Size.y : b.Size.z;

	static void SetAxisSize( SdfBrush b, int i, float value )
	{
		var s = b.Size;
		b.Size = i == 0 ? new Vector3( value, s.y, s.z )
			: i == 1 ? new Vector3( s.x, value, s.z )
			: new Vector3( s.x, s.y, value );
	}
}
