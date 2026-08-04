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
	readonly List<Vector4> _curve = new(); // reused buffer for the spline centre-line polyline
	Vector3 _camPos;
	float _wpp;          // world units per screen pixel (at unit distance)
	float _thicknessPx;  // line thickness in screen pixels (matches editor OutlineThickness)
	float _depthBias;

	/// <summary>
	/// Draw the wireframes matching the editor: per-brush colour (cyan additive / red subtractive) with the
	/// editor's per-state opacity (selected 1, hovered 0.7, else 0.3) times <paramref name="masterAlpha"/>
	/// (the fade × drag-opacity), and a screen-constant <paramref name="thicknessPx"/> (the editor's
	/// OutlineThickness). Camera-dependent now (screen-constant width), so it rebuilds as the view moves.
	/// </summary>
	public void Draw( List<SdfBrush> brushes, Transform sculptTx, Scene scene, CameraComponent cam,
		int selected, int hovered, float masterAlpha, float thicknessPx, float depthBias )
	{
		if ( brushes is null || brushes.Count == 0 || scene is null || cam is null )
		{
			Hide();
			return;
		}

		var camPos = cam.WorldPosition;
		int hash = HashCode.Combine(
			HashCode.Combine( BrushesHash( brushes ), sculptTx.Position, sculptTx.Rotation ),
			HashCode.Combine( camPos, selected, hovered, masterAlpha, thicknessPx, depthBias ) );
		if ( hash == _lastHash && _so.IsValid() )
			return;
		_lastHash = hash;

		_depthBias = depthBias;
		Build( brushes, sculptTx, cam, selected, hovered, masterAlpha, thicknessPx );
		Upload( scene );
	}

	public void Hide()
	{
		_so?.Delete();
		_so = null;
		_lastHash = 0;
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
			hc.Add( (int)b.Operation ); // colour depends on add/subtract
			hc.Add( b.Position );
			hc.Add( b.Rotation );
			hc.Add( b.Size );
			hc.Add( b.Slice ); // slice moves the cut edge in the wire
			hc.Add( b.MirrorX ); hc.Add( b.MirrorY ); hc.Add( b.MirrorZ );
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

	void Build( List<SdfBrush> brushes, Transform sculptTx, CameraComponent cam, int selected, int hovered, float masterAlpha, float thicknessPx )
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
			// and drag-opacity passed in).
			float stateAlpha = i == selected ? 1f : (i == hovered ? 0.7f : 0.3f);
			var baseCol = b.Operation == SdfOperation.Subtract ? Color.Red : Color.Cyan;
			_col = baseCol.WithAlpha( stateAlpha * masterAlpha );

			// One copy per mirror-sign combination (identity + reflection across each enabled plane).
			int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
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

	// Spline wireframe: the centre polyline plus three axis circles at each control point. Points are in
	// sculpture space (not the brush frame), so these use WorldPt/EdgeWorld directly instead of ToWorld.
	void SplineWire( SdfBrush b )
	{
		var pts = b.Points;
		if ( pts is not { Count: > 0 } )
			return;

		// Centre line follows the drawn curve (tessellated when curved); rings stay on the control points.
		b.BuildSplinePolyline( _curve );
		for ( int i = 0; i < _curve.Count - 1; i++ )
			EdgeWorld( WorldPt( new Vector3( _curve[i].x, _curve[i].y, _curve[i].z ) ),
				WorldPt( new Vector3( _curve[i + 1].x, _curve[i + 1].y, _curve[i + 1].z ) ) );

		var ux = new Vector3( 1, 0, 0 );
		var uy = new Vector3( 0, 1, 0 );
		var uz = new Vector3( 0, 0, 1 );
		foreach ( var pt in pts )
		{
			var c = new Vector3( pt.x, pt.y, pt.z );
			float r = pt.w * Pad;
			RingWorld( c, r, ux, uy );
			RingWorld( c, r, ux, uz );
			RingWorld( c, r, uy, uz );
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
