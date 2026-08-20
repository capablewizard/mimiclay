using System;

namespace Mimiclay;

/// <summary>
/// Wireframe overlay for every brush in a sculpture — the runtime equivalent of the editor's scene-view
/// brush wireframes. Each primitive edge is meshed as a thin world-space tube (so it reads from any angle),
/// rendered on top via the gizmo shader with a vertex-colour alpha the caller fades. Includes mirror copies.
/// Reuses one <see cref="SceneObject"/>; only rebuilds when the brushes / colour / thickness change.
/// </summary>
public sealed class BrushWireframes
{
	const int Seg = 20;              // segments per ring
	const int SplineRails = 4;       // longitudinal lines running the length of a spline tube
	const int SplineRingsPerSpan = 2; // cross-section rings per control-point span on a curved spline
	const int CapArcSeg = 5;         // segments per meridian over a rounded end cap
	const float Pad = 1.04f;         // match BrushGhost: sit a touch outside the surface (no z-fight)
	static readonly float Tau = MathF.PI * 2f;

	readonly List<Vertex> _verts = new();
	readonly List<int> _indices = new();
	Vector3 _bbMin, _bbMax;
	SceneObject _so;
	Material _material;
	int _lastHash;

	// Build context.
	SdfBrush _brush;
	Transform _sculptTx;
	Vector3 _sign = Vector3.One;
	Color _col;
	readonly List<Vector4> _curve = new();   // reused buffer for the spline centre-line polyline
	readonly List<Vector4> _frames = new();  // per-sample tube frame: centre (xyz) + radius (w)
	readonly List<Vector3> _splineT = new(); // …its tangent, and the parallel-transported cross-section axes
	readonly List<Vector3> _splineU = new();
	readonly List<Vector3> _splineV = new();
	Vector3 _camPos;
	float _wpp;          // world units per screen pixel (at unit distance)
	float _thicknessPx;  // line thickness in screen pixels (matches editor OutlineThickness)
	float _depthBias;

	/// <summary>
	/// Draw the wireframes matching the editor: per-brush colour (cyan additive / red subtractive) with the
	/// editor's per-state opacity (selected 1, hovered 0.7, else 0.3) times <paramref name="masterAlpha"/>
	/// (the fade × drag-opacity), and a screen-constant <paramref name="thicknessPx"/> (the editor's
	/// OutlineThickness). Camera-dependent now (screen-constant width), so it rebuilds as the view moves.
	/// <paramref name="warn"/> (optional) tints those brush indices <paramref name="warnColor"/> at high
	/// opacity — the SculptBounds "these brushes poke out of the region" blame.
	/// <paramref name="selectedMulti"/> (optional) marks EXTRA indices as selected (full opacity) — the
	/// multi-selection; <paramref name="selected"/> alone still covers the single-selection case.
	/// </summary>
	public void Draw( List<SdfBrush> brushes, Transform sculptTx, Scene scene, CameraComponent cam,
		int selected, int hovered, float masterAlpha, float thicknessPx, float depthBias,
		IReadOnlyList<int> warn = null, Color warnColor = default, IReadOnlyList<int> selectedMulti = null )
	{
		if ( brushes is null || brushes.Count == 0 || scene is null || cam is null )
		{
			Hide();
			return;
		}

		int warnHash = 0;
		if ( warn is { Count: > 0 } )
		{
			var whc = new HashCode();
			foreach ( var i in warn )
				whc.Add( i );
			whc.Add( warnColor );
			warnHash = whc.ToHashCode();
		}

		int selHash = 0;
		if ( selectedMulti is { Count: > 0 } )
		{
			var shc = new HashCode();
			foreach ( var i in selectedMulti )
				shc.Add( i );
			selHash = shc.ToHashCode();
		}

		var camPos = cam.WorldPosition;
		int hash = HashCode.Combine(
			HashCode.Combine( BrushesHash( brushes ), sculptTx.Position, sculptTx.Rotation ),
			HashCode.Combine( camPos, selected, hovered, masterAlpha, thicknessPx, depthBias ),
			warnHash, selHash );
		if ( hash == _lastHash && _so.IsValid() )
			return;
		_lastHash = hash;

		_depthBias = depthBias;
		Build( brushes, sculptTx, cam, selected, hovered, masterAlpha, thicknessPx, warn, warnColor, selectedMulti );
		Upload( scene );
	}

	public void Hide()
	{
		_so?.Delete();
		_so = null;
		_lastHash = 0;
	}

	static bool IndexIn( IReadOnlyList<int> list, int i )
	{
		if ( list is null )
			return false;
		for ( int k = 0; k < list.Count; k++ )
		{
			if ( list[k] == i )
				return true;
		}
		return false;
	}

	static int BrushesHash( List<SdfBrush> brushes )
	{
		var hc = new HashCode();
		foreach ( var b in brushes )
		{
			hc.Add( (int)b.Shape );
			hc.Add( (int)b.CrossSection ); // extruded profile swap rebuilds the wire
			hc.Add( b.Text ); hc.Add( b.Font );
			hc.Add( b.TextData is not null ); // ink-rect wire: rebuild when the bake lands (quad → ink box)
			hc.Add( (int)b.Operation ); // colour depends on the op (add/carve/cutout)
			hc.Add( b.Position );
			hc.Add( b.Rotation );
			hc.Add( b.Size );
			hc.Add( b.Slice ); // slice moves the cut edge in the wire
			hc.Add( b.EffectiveMirrorX ); hc.Add( b.EffectiveMirrorY ); hc.Add( b.EffectiveMirrorZ );
			if ( b.Points is { } pts )
			{
				hc.Add( pts.Count );
				foreach ( var pt in pts )
					hc.Add( pt );
				hc.Add( b.Curvature );
				hc.Add( b.SplineClosed );
			}
		}
		return hc.ToHashCode();
	}

	void Build( List<SdfBrush> brushes, Transform sculptTx, CameraComponent cam, int selected, int hovered,
		float masterAlpha, float thicknessPx, IReadOnlyList<int> warn, Color warnColor,
		IReadOnlyList<int> selectedMulti )
	{
		_verts.Clear();
		_indices.Clear();
		_bbMin = new Vector3( float.MaxValue );
		_bbMax = new Vector3( float.MinValue );
		_sculptTx = sculptTx;
		_camPos = cam.WorldPosition;
		_wpp = WorldPerPixel( cam );
		_thicknessPx = MathF.Max( 0.1f, thicknessPx );

		for ( int i = 0; i < brushes.Count; i++ )
		{
			var b = brushes[i];
			if ( b.Damage )
				continue; // shot craters aren't part of the authored sculpt — no wireframe

			_brush = b;

			// Editor styling: cyan additive / red subtractive; opacity by selection state (× the master fade
			// and drag-opacity passed in). An out-of-bounds brush (SculptBounds blame, fixed regions only)
			// wears the warn colour near-full regardless of selection — the culprit has to pop.
			float stateAlpha = (i == selected || IndexIn( selectedMulti, i )) ? 1f : (i == hovered ? 0.7f : 0.3f);
			var baseCol = b.Operation switch
			{
				SdfOperation.Subtract => Color.Red,
				SdfOperation.Cutout => Color.Blue,
				_ => Color.Cyan,
			};
			if ( IndexIn( warn, i ) )
			{
				baseCol = warnColor;
				stateAlpha = MathF.Max( stateAlpha, 0.9f );
			}
			_col = baseCol.WithAlpha( stateAlpha * masterAlpha );

			// One copy per mirror-sign combination (identity + reflection across each enabled plane).
			int nx = b.EffectiveMirrorX ? 1 : 0, ny = b.EffectiveMirrorY ? 1 : 0, nz = b.EffectiveMirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= nx; sx++ )
			for ( int sy = 0; sy <= ny; sy++ )
			for ( int sz = 0; sz <= nz; sz++ )
			{
				_sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				Shape( b );
			}
		}
	}

	void Shape( SdfBrush b )
	{
		var s = b.Size * Pad; // same 1.04 pad as BrushGhost so the wireframe and solid ghost line up
		switch ( b.Shape )
		{
			case SdfShape.Box: BoxWire( s ); break;
			case SdfShape.Text: BoxWire( b.TextInkExtents() * Pad ); break; // the ink rect, matching bounds/collider
			case SdfShape.Cylinder: CylinderWire( s.x, s.z ); break;
			case SdfShape.Cone: ConeWire( s.x, s.z, b.Size.z, b.SlicePlaneN ); break; // zoff raw: base-pivot
			case SdfShape.Extruded: ExtrusionWire( b.CrossSection, s ); break;
			case SdfShape.Spline: SplineWire( b ); break; // centre line + radius rings per control point
			default: SphereWire( s, b.SlicePlaneN ); break; // Sphere / ellipsoid
		}
	}

	// Spline wireframe: the tube's SURFACE, drawn Blender-style — SplineRails longitudinal lines running
	// along the sweep plus cross-section rings at regular subdivision, with a hemispherical dome closing each
	// open end. Points are in sculpture space (not the brush frame), so these use WorldPt/EdgeWorld directly
	// instead of ToWorld.
	void SplineWire( SdfBrush b )
	{
		var pts = b.Points;
		if ( pts is not { Count: > 0 } )
			return;

		// A lone control point is just a sphere — no sweep to run rails along, so keep the axis rings.
		if ( pts.Count == 1 )
		{
			var c0 = new Vector3( pts[0].x, pts[0].y, pts[0].z );
			float r0 = pts[0].w * Pad;
			RingWorld( c0, r0, new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ) );
			RingWorld( c0, r0, new Vector3( 1, 0, 0 ), new Vector3( 0, 0, 1 ) );
			RingWorld( c0, r0, new Vector3( 0, 1, 0 ), new Vector3( 0, 0, 1 ) );
			return;
		}

		b.BuildSplinePolyline( _curve );
		bool closed = b.IsSplineLoop;
		if ( !BuildSplineFrames( closed ) )
			return;

		int m = _frames.Count;
		int spans = closed ? m : m - 1; // closed: the wrap-around span (last sample → first) too

		// Longitudinal rails: one continuous line per frame angle, following the tube all the way along.
		for ( int j = 0; j < SplineRails; j++ )
		{
			float a = j / (float)SplineRails * Tau;
			var prev = SplineSurfacePt( 0, a );
			for ( int i = 1; i <= spans; i++ )
			{
				var cur = SplineSurfacePt( i % m, a );
				EdgeWorld( prev, cur );
				prev = cur;
			}
		}

		// Cross-section rings. Straight splines only have a sample per control point, so ring every one;
		// curved ones are tessellated SplineTessellation-per-span, so stride down to SplineRingsPerSpan.
		int stride = b.Curvature <= 1e-4f
			? 1
			: Math.Max( 1, SdfBrush.SplineTessellation / SplineRingsPerSpan );
		for ( int i = 0; i < m; i += stride )
			SplineRing( i );

		// Open ends are rounded in the field (the tube is a chain of rounded cones), so cap them with a dome
		// rather than letting the rails stop dead on a flat disc.
		if ( !closed )
		{
			if ( (m - 1) % stride != 0 )
				SplineRing( m - 1 ); // the far seam ring, when the stride doesn't land on it
			SplineCap( 0, -_splineT[0] );
			SplineCap( m - 1, _splineT[m - 1] );
		}
	}

	// Hemispherical end cap: each rail continues over the dome as a meridian converging on the pole. No
	// latitude rings — the seam ring already reads as the tube's last cross-section.
	void SplineCap( int i, Vector3 outward )
	{
		var f = _frames[i];
		float r = f.w * Pad;
		var c = CurvePos( f );
		var u = _splineU[i];
		var v = _splineV[i];

		// `a` around the tube (0 = the first rail, so the meridians line up with them), `phi` up to the pole.
		Vector3 Dome( float a, float phi ) => WorldPt( c
			+ (u * MathF.Cos( a ) + v * MathF.Sin( a )) * (r * MathF.Cos( phi ))
			+ outward * (r * MathF.Sin( phi )) );

		for ( int j = 0; j < SplineRails; j++ )
		{
			float a = j / (float)SplineRails * Tau;
			var prev = Dome( a, 0f );
			for ( int k = 1; k <= CapArcSeg; k++ )
			{
				var cur = Dome( a, k / (float)CapArcSeg * (MathF.PI * 0.5f) );
				EdgeWorld( prev, cur );
				prev = cur;
			}
		}
	}

	// Parallel-transported frames along the polyline in _curve — one orthonormal (u,v) pair per sample, so
	// the rails sweep the tube without twisting. Returns false if the curve degenerates to a single point.
	bool BuildSplineFrames( bool closed )
	{
		_frames.Clear();
		_splineT.Clear();
		_splineU.Clear();
		_splineV.Clear();

		int m = _curve.Count;
		// A closed polyline ends back on its first point — drop the duplicate so the wrap span isn't zero-length.
		if ( closed && m > 1 && (CurvePos( _curve[m - 1] ) - CurvePos( _curve[0] )).Length < 0.001f )
			m--;
		if ( m < 2 )
			return false;

		for ( int i = 0; i < m; i++ )
			_frames.Add( _curve[i] );

		// Tangents: central difference, wrapping when closed and one-sided at open ends.
		for ( int i = 0; i < m; i++ )
		{
			int prev = i > 0 ? i - 1 : (closed ? m - 1 : 0);
			int next = i < m - 1 ? i + 1 : (closed ? 0 : m - 1);
			var t = CurvePos( _frames[next] ) - CurvePos( _frames[prev] );
			_splineT.Add( t.Length < 1e-5f
				? (i > 0 ? _splineT[i - 1] : Vector3.Forward) // coincident samples: carry the last tangent
				: t.Normal );
		}

		var u0 = Vector3.Cross( _splineT[0], MathF.Abs( _splineT[0].z ) < 0.9f ? Vector3.Up : Vector3.Forward ).Normal;
		_splineU.Add( u0 );
		_splineV.Add( Vector3.Cross( _splineT[0], u0 ).Normal );
		for ( int i = 1; i < m; i++ )
		{
			var u = Transport( _splineU[i - 1], _splineT[i - 1], _splineT[i] );
			_splineU.Add( u );
			_splineV.Add( Vector3.Cross( _splineT[i], u ).Normal );
		}

		if ( closed )
		{
			// Transport once more across the seam and measure how far the frame drifted (the transport's
			// holonomy), then unwind it evenly around the loop so each rail rejoins itself instead of stepping.
			var back = Transport( _splineU[m - 1], _splineT[m - 1], _splineT[0] );
			float drift = MathF.Atan2( Vector3.Dot( Vector3.Cross( _splineU[0], back ), _splineT[0] ),
				Vector3.Dot( _splineU[0], back ) );
			for ( int i = 1; i < m; i++ )
			{
				float a = -drift * i / m;
				var u = _splineU[i] * MathF.Cos( a ) + _splineV[i] * MathF.Sin( a );
				_splineU[i] = u;
				_splineV[i] = Vector3.Cross( _splineT[i], u ).Normal;
			}
		}

		return true;
	}

	// Rotate `u` by the minimal rotation taking tangent t0 onto t1, then re-orthogonalise (stops the frame
	// drifting off the tangent plane over a long curve).
	static Vector3 Transport( Vector3 u, Vector3 t0, Vector3 t1 )
	{
		var axis = Vector3.Cross( t0, t1 );
		float s = axis.Length;
		if ( s > 1e-6f )
		{
			float deg = MathF.Atan2( s, Vector3.Dot( t0, t1 ) ) * (180f / MathF.PI);
			u = Rotation.FromAxis( axis / s, deg ) * u;
		}
		u -= t1 * Vector3.Dot( u, t1 );
		return u.Length > 1e-5f
			? u.Normal
			: Vector3.Cross( t1, MathF.Abs( t1.z ) < 0.9f ? Vector3.Up : Vector3.Forward ).Normal; // 180° flip
	}

	static Vector3 CurvePos( Vector4 v ) => new( v.x, v.y, v.z );

	// Point on the tube surface at sample `i`, `a` radians around its frame → world.
	Vector3 SplineSurfacePt( int i, float a )
	{
		var f = _frames[i];
		float r = f.w * Pad;
		return WorldPt( CurvePos( f ) + _splineU[i] * (r * MathF.Cos( a )) + _splineV[i] * (r * MathF.Sin( a )) );
	}

	// The cross-section circle at sample `i` (also the flat end cap on an open spline's first/last sample).
	void SplineRing( int i )
	{
		var prev = SplineSurfacePt( i, 0f );
		for ( int k = 1; k <= Seg; k++ )
		{
			var cur = SplineSurfacePt( i, k / (float)Seg * Tau );
			EdgeWorld( prev, cur );
			prev = cur;
		}
	}

	// A circle of radius r centred at the sculpture-local point `centerLocal`, in the u/v plane.
	void RingWorld( Vector3 centerLocal, float r, Vector3 u, Vector3 v )
	{
		var prev = WorldPt( centerLocal + u * r );
		for ( int k = 1; k <= Seg; k++ )
		{
			float a = k / (float)Seg * Tau;
			var cur = WorldPt( centerLocal + u * (r * MathF.Cos( a )) + v * (r * MathF.Sin( a )) );
			EdgeWorld( prev, cur );
			prev = cur;
		}
	}

	// Sculpture-local point (reflected for the mirror copy) → world.
	Vector3 WorldPt( Vector3 sculptLocal ) => _sculptTx.PointToWorld( sculptLocal * _sign );

	// ── per-shape edge lists (brush-local frame, axis = Z) ───────────────────────────────────────────

	void BoxWire( Vector3 b )
	{
		Vector3 C( int i ) => new( (i & 1) != 0 ? b.x : -b.x, (i & 2) != 0 ? b.y : -b.y, (i & 4) != 0 ? b.z : -b.z );

		int[,] e =
		{
			{ 0, 1 }, { 1, 3 }, { 3, 2 }, { 2, 0 }, // -z face
			{ 4, 5 }, { 5, 7 }, { 7, 6 }, { 6, 4 }, // +z face
			{ 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }, // verticals
		};

		for ( int k = 0; k < e.GetLength( 0 ); k++ )
			Edge( C( e[k, 0] ), C( e[k, 1] ) );
	}

	// Ellipsoid wire, optionally sliced at z = r.z·sliceN: the vertical great rings clamp their z to the cut
	// plane (their top arc becomes the flat chord), a ring is drawn on the cut circle itself, and the equator
	// only survives while it's below the cut.
	void SphereWire( Vector3 r, float sliceN )
	{
		bool cut = sliceN < 0.999f;
		float zc = r.z * sliceN;

		if ( !cut || sliceN > 0f )
			Ring( a => new Vector3( MathF.Cos( a ) * r.x, MathF.Sin( a ) * r.y, 0 ) ); // XY equator
		if ( cut )
		{
			float s = MathF.Sqrt( MathF.Max( 1f - sliceN * sliceN, 0f ) ); // cut-circle radius factor
			Ring( a => new Vector3( MathF.Cos( a ) * r.x * s, MathF.Sin( a ) * r.y * s, zc ) ); // the cut edge
		}
		Ring( a => new Vector3( MathF.Cos( a ) * r.x, 0, MathF.Min( MathF.Sin( a ) * r.z, cut ? zc : r.z ) ) ); // XZ
		Ring( a => new Vector3( 0, MathF.Cos( a ) * r.y, MathF.Min( MathF.Sin( a ) * r.z, cut ? zc : r.z ) ) ); // YZ
	}

	void CylinderWire( float rad, float h )
	{
		Ring( a => new Vector3( MathF.Cos( a ) * rad, MathF.Sin( a ) * rad, h ) );
		Ring( a => new Vector3( MathF.Cos( a ) * rad, MathF.Sin( a ) * rad, -h ) );

		for ( int k = 0; k < 4; k++ )
		{
			float a = k / 4f * Tau;
			float cx = MathF.Cos( a ) * rad, cy = MathF.Sin( a ) * rad;
			Edge( new Vector3( cx, cy, -h ), new Vector3( cx, cy, h ) );
		}
	}

	// Base-pivot cone wire (shifted up by zoff, the RAW half-height), optionally sliced into a frustum at
	// z = h·sliceN: the side edges stop at the cut plane and a top ring marks the flat cut.
	void ConeWire( float R, float h, float zoff, float sliceN )
	{
		Ring( a => new Vector3( MathF.Cos( a ) * R, MathF.Sin( a ) * R, zoff - h ) );

		bool cut = sliceN < 0.999f;
		float zc = (cut ? h * sliceN : h) + zoff;
		float rc = cut ? R * (1f - sliceN) * 0.5f : 0f;

		if ( cut )
			Ring( a => new Vector3( MathF.Cos( a ) * rc, MathF.Sin( a ) * rc, zc ) ); // the cut edge

		for ( int k = 0; k < 4; k++ )
		{
			float a = k / 4f * Tau;
			float ca = MathF.Cos( a ), sa = MathF.Sin( a );
			Edge( new Vector3( ca * R, sa * R, zoff - h ), new Vector3( ca * rc, sa * rc, zc ) );
		}
	}

	// Extruded cross-section (triangle/star/hexagon) swept along Z — both cap outlines plus a vertical at
	// every profile vertex. Matches ExtrudedDistance / BrushGhost.
	readonly List<Vector2> _outline = new();
	void ExtrusionWire( SdfCrossSection xs, Vector3 s )
	{
		SdfBrush.CrossSectionOutline( xs, s, _outline );
		int n = _outline.Count;
		if ( n < 3 )
			return;

		for ( int i = 0; i < n; i++ )
		{
			int j = (i + 1) % n;
			var a = _outline[i];
			var b = _outline[j];
			Edge( new Vector3( a.x, a.y, s.z ), new Vector3( b.x, b.y, s.z ) );   // top cap edge
			Edge( new Vector3( a.x, a.y, -s.z ), new Vector3( b.x, b.y, -s.z ) ); // bottom cap edge
			Edge( new Vector3( a.x, a.y, -s.z ), new Vector3( a.x, a.y, s.z ) );  // vertical
		}
	}

	// Closed loop of `Seg` edges around a parametric ring (angle → brush-local point).
	void Ring( Func<float, Vector3> pointAt )
	{
		var prev = pointAt( 0f );
		for ( int k = 1; k <= Seg; k++ )
		{
			var cur = pointAt( k / (float)Seg * Tau );
			Edge( prev, cur );
			prev = cur;
		}
	}

	// ── mesh infra ───────────────────────────────────────────────────────────────────────────────────

	// A brush-local edge → a thin world-space tube (4-sided), so it stays visible from any view angle.
	void Edge( Vector3 la, Vector3 lb ) => EdgeWorld( ToWorld( la ), ToWorld( lb ) );

	// Same, but from two world-space endpoints (used by the spline, whose points are already in world).
	void EdgeWorld( Vector3 a, Vector3 b )
	{
		var d = b - a;
		float len = d.Length;
		if ( len < 0.001f )
			return;
		d /= len;

		// Screen-constant half-width (so it reads at _thicknessPx pixels regardless of distance, like the editor).
		float halfW = W( (a + b) * 0.5f, _thicknessPx * 0.5f );

		var up = MathF.Abs( d.z ) < 0.9f ? Vector3.Up : Vector3.Forward;
		var right = Vector3.Cross( d, up ).Normal * halfW;
		up = Vector3.Cross( right, d ).Normal * halfW;

		int a0 = Add( a - right - up ), a1 = Add( a + right - up ), a2 = Add( a + right + up ), a3 = Add( a - right + up );
		int b0 = Add( b - right - up ), b1 = Add( b + right - up ), b2 = Add( b + right + up ), b3 = Add( b - right + up );

		Quad( a0, a1, b1, b0 );
		Quad( a1, a2, b2, b1 );
		Quad( a2, a3, b3, b2 );
		Quad( a3, a0, b0, b3 );
	}

	// World size of `px` screen pixels at world point `at` (same formula as RuntimeBrushGizmo).
	float W( Vector3 at, float px ) => px * (_camPos - at).Length * _wpp;

	static float WorldPerPixel( CameraComponent cam )
	{
		var c = Screen.Size * 0.5f;
		var r0 = cam.ScreenPixelToRay( c );
		var r1 = cam.ScreenPixelToRay( c + new Vector2( 0f, 1f ) );
		return (r1.Forward - r0.Forward).Length;
	}

	// Brush-local → sculpture space (reflected for the mirror copy) → world.
	Vector3 ToWorld( Vector3 local )
	{
		var v = _brush.Position + _brush.Rotation * local;
		v *= _sign;
		return _sculptTx.PointToWorld( v );
	}

	int Add( Vector3 p )
	{
		_bbMin = Vector3.Min( _bbMin, p );
		_bbMax = Vector3.Max( _bbMax, p );
		_verts.Add( new Vertex( p, Vector3.Up, Vector3.Forward, Vector4.Zero ) { Color = _col } );
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

	void Upload( Scene scene )
	{
		if ( _verts.Count < 3 )
		{
			Hide();
			return;
		}

		// Depth-TESTED shader (unlike the on-top gizmo) so the SDF surface occludes the wireframe's back half,
		// like the editor scene view. Default render layer (NOT OverlayWithoutDepth) so it tests scene depth.
		_material ??= Material.FromShader( "shaders/gizmo_depth.shader" );

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
			_so.Transform = Transform.Zero;
			_so.Flags.CastShadows = false;
			_so.Batchable = false;
			// Depth-tested overlay (renders after the scene so the SDF's depth is present to occlude us),
			// vs the gizmos' OverlayWithoutDepth (always on top).
			_so.RenderLayer = SceneRenderLayer.OverlayWithDepth;
		}

		_so.Attributes.Set( "DepthBias", _depthBias ); // push toward camera so it shows through the SDF shell
		_so.Bounds = new BBox( _bbMin, _bbMax );
		_so.RenderingEnabled = true;
	}
}
