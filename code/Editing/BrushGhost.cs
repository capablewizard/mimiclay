using System;

namespace Mimiclay;

/// <summary>
/// Translucent solid "ghost" overlay for the hovered <see cref="SdfBrush"/> — the runtime equivalent of
/// the editor tool's hover highlight, minus the ever-present wireframe (which reads as clutter in play).
/// Meshes the brush's primitive shape AND every mirror-symmetry copy, and renders them on top via the
/// gizmo shader (unlit, alpha-blended, after post-processing). Reuses one <see cref="SceneObject"/>;
/// only rebuilds when the hovered brush changes.
/// </summary>
public sealed class BrushGhost
{
	const float Pad = 1.04f; // sit a touch outside the surface so it doesn't z-fight the meshed sculpture

	readonly List<Vertex> _verts = new();
	readonly List<int> _indices = new();
	Vector3 _bbMin, _bbMax;
	SceneObject _so;
	Material _material;
	int _lastHash;

	// Build context: the brush, its owning sculpture transform, and the current mirror sign (±1 per axis,
	// in sculpture-local space). V() maps a shape-local vertex through these to world space.
	SdfBrush _brush;
	Transform _sculptTx;
	Vector3 _sign = Vector3.One;
	bool _spline;            // current brush is a spline — V() places verts relative to _splineCenter
	Vector3 _splineCenter;   // the control point a spline sphere is being built around (sculpture-local)
	readonly List<Vector4> _splinePath = new(); // reused buffer for the tube centre path (xyz + radius)

	public void Show( SdfBrush brush, Transform sculptTx, Scene scene, Color color )
	{
		if ( brush is null || scene is null )
		{
			Hide();
			return;
		}

		// World-space mesh — depends only on the brush + its sculpture transform, not the camera. Skip the
		// rebuild when nothing visible changed (mirror flags included, so toggling symmetry refreshes).
		int mirror = (brush.MirrorX ? 1 : 0) | (brush.MirrorY ? 2 : 0) | (brush.MirrorZ ? 4 : 0);
		int hash = HashCode.Combine( (int)brush.Shape, brush.Size, brush.Position, brush.Rotation, color,
			sculptTx.Position, sculptTx.Rotation, mirror );
		hash = HashCode.Combine( hash, (int)brush.CrossSection, brush.Slice ); // profile swap / slice drag rebuilds the ghost
		hash = HashCode.Combine( hash, brush.Text, brush.Font );
		hash = HashCode.Combine( hash, brush.TextData is not null ); // ink-rect ghost: rebuild when the bake lands
		if ( brush.Points is { } pts ) // spline: rebuild when the swept curve changes
		{
			hash = HashCode.Combine( hash, pts.Count, brush.Curvature, brush.SplineClosed );
			foreach ( var pt in pts )
				hash = HashCode.Combine( hash, pt );
		}
		if ( hash == _lastHash && _so.IsValid() )
			return;
		_lastHash = hash;

		Build( brush, sculptTx, color );
		Upload( scene );
	}

	public void Hide()
	{
		_so?.Delete();
		_so = null;
		_lastHash = 0;
	}

	void Build( SdfBrush brush, Transform sculptTx, Color col )
	{
		_verts.Clear();
		_indices.Clear();
		_bbMin = new Vector3( float.MaxValue );
		_bbMax = new Vector3( float.MinValue );

		_brush = brush;
		_sculptTx = sculptTx;
		_spline = brush.Shape == SdfShape.Spline;

		var s = brush.Size * Pad;

		// Mesh the primitive once per mirror sign combination: the identity (1,1,1) base shape plus a
		// reflection across each enabled sculpture-origin plane. The gizmo shader is two-sided, so a
		// reflected (winding-flipped) copy renders fine.
		int nx = brush.MirrorX ? 1 : 0, ny = brush.MirrorY ? 1 : 0, nz = brush.MirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
		for ( int sy = 0; sy <= ny; sy++ )
		for ( int sz = 0; sz <= nz; sz++ )
		{
			_sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
			switch ( brush.Shape )
			{
				case SdfShape.Box: Box( s, col ); break;
				case SdfShape.Text: Box( brush.TextInkExtents() * Pad, col ); break; // the ink rect, matching the wireframe
				case SdfShape.Cylinder: Cylinder( s.x, s.z, col ); break;
				case SdfShape.Cone: Cone( s.x, s.z, brush.Size.z, brush.SlicePlaneN, col ); break;
				case SdfShape.Extruded: Extrusion( brush.CrossSection, s, col ); break;
				case SdfShape.Spline: SplineTube( brush, col ); break; // one continuous tube surface
				default: Ellipsoid( s, brush.SlicePlaneN, col ); break; // Sphere / ellipsoid
			}
		}
	}

	// ── primitives (built in the brush's local frame, axis = Z, then mapped to world via V) ───────────

	// Spline ghost: ONE continuous tube surface swept along the curve, so it reads as a single translucent
	// shape (low, uniform opacity) instead of many overlapping spheres whose opacity accumulates. Rings of
	// vertices around the curve are joined into a tube wall + flat end caps. Built in sculpture space; V()
	// (spline mode, _splineCenter = 0) applies the mirror sign + the sculpture transform.
	void SplineTube( SdfBrush brush, Color col )
	{
		brush.BuildSplinePolyline( _splinePath ); // centre path (xyz) + per-point radius (w); curve-aware
		var path = _splinePath;
		int count = path.Count;
		if ( count == 0 )
			return;

		_splineCenter = Vector3.Zero; // V() treats each passed vertex as a full sculpture-space position

		if ( count == 1 )
		{
			_splineCenter = new Vector3( path[0].x, path[0].y, path[0].z );
			Ellipsoid( new Vector3( path[0].w * Pad ), 1f, col ); // never sliced
			return;
		}

		const int M = 10; // segments around the tube
		var rings = new int[count * M];
		Vector3 prevU = Vector3.Zero;
		bool haveFrame = false;
		// End frames captured so the rounded caps share the tube's first/last ring exactly.
		Vector3 firstU = default, firstV = default, firstTan = default;
		Vector3 lastU = default, lastV = default, lastTan = default;

		for ( int i = 0; i < count; i++ )
		{
			var c = new Vector3( path[i].x, path[i].y, path[i].z );
			float r = path[i].w * Pad;

			// Tangent from the neighbours (central where possible).
			var a = i > 0 ? new Vector3( path[i - 1].x, path[i - 1].y, path[i - 1].z ) : c;
			var b = i < count - 1 ? new Vector3( path[i + 1].x, path[i + 1].y, path[i + 1].z ) : c;
			var tan = b - a;
			tan = tan.LengthSquared < 1e-8f ? Vector3.Forward : tan.Normal;

			// Rotation-minimising-ish frame: carry the previous u and re-orthonormalise it against the new
			// tangent, so the ring doesn't twist between samples (which would crease the tube).
			Vector3 u, v;
			if ( !haveFrame )
			{
				PerpBasis( tan, out u, out v );
				haveFrame = true;
			}
			else
			{
				u = prevU - tan * Vector3.Dot( prevU, tan );
				if ( u.LengthSquared < 1e-6f )
					PerpBasis( tan, out u, out v );
				else
				{
					u = u.Normal;
					v = Vector3.Cross( tan, u ).Normal;
				}
			}
			prevU = u;
			if ( i == 0 ) { firstU = u; firstV = v; firstTan = tan; }
			if ( i == count - 1 ) { lastU = u; lastV = v; lastTan = tan; }

			for ( int m = 0; m < M; m++ )
			{
				float ang = m / (float)M * MathF.PI * 2f;
				var rv = c + (u * MathF.Cos( ang ) + v * MathF.Sin( ang )) * r;
				rings[i * M + m] = V( rv, col );
			}
		}

		// Tube wall.
		for ( int i = 1; i < count; i++ )
			for ( int m = 0; m < M; m++ )
			{
				int n = (m + 1) % M;
				Quad( rings[(i - 1) * M + m], rings[(i - 1) * M + n], rings[i * M + n], rings[i * M + m] );
			}

		// Rounded (hemispherical) end caps of the end-point radius, so the ghost's ends line up with the
		// rounded-cone tube's spherical caps instead of ending in a flat disc. Each shares the tube's end ring
		// as its equator (φ=0), then domes out by the radius along the outward tangent.
		var startEquator = new int[M];
		var endEquator = new int[M];
		int last = (count - 1) * M;
		for ( int m = 0; m < M; m++ )
		{
			startEquator[m] = rings[m];
			endEquator[m] = rings[last + m];
		}

		HemiCap( new Vector3( path[0].x, path[0].y, path[0].z ), path[0].w * Pad, firstU, firstV, -firstTan, startEquator, M, col );
		HemiCap( new Vector3( path[count - 1].x, path[count - 1].y, path[count - 1].z ), path[count - 1].w * Pad, lastU, lastV, lastTan, endEquator, M, col );
	}

	// A hemisphere dome on the end of the tube: latitude rings from the existing equator ring out to a pole,
	// centred at `center`, radius `r`, in the ring's (u,v) frame, bulging along `outward`. Reusing the equator
	// ring makes the dome seam-match the tube wall exactly.
	void HemiCap( Vector3 center, float r, Vector3 u, Vector3 v, Vector3 outward, int[] equator, int m, Color col )
	{
		const int L = 4; // latitude steps from equator to pole
		outward = outward.LengthSquared < 1e-8f ? Vector3.Forward : outward.Normal;

		int[] prev = equator;
		for ( int l = 1; l <= L; l++ )
		{
			float phi = (l / (float)L) * (MathF.PI * 0.5f);
			float rr = r * MathF.Cos( phi );
			var cc = center + outward * (r * MathF.Sin( phi ));

			if ( l == L )
			{
				int pole = V( cc, col );
				for ( int k = 0; k < m; k++ )
					Tri( prev[k], prev[(k + 1) % m], pole );
				break;
			}

			var cur = new int[m];
			for ( int k = 0; k < m; k++ )
			{
				float ang = k / (float)m * MathF.PI * 2f;
				cur[k] = V( cc + (u * MathF.Cos( ang ) + v * MathF.Sin( ang )) * rr, col );
			}
			for ( int k = 0; k < m; k++ )
			{
				int n = (k + 1) % m;
				Quad( prev[k], prev[n], cur[n], cur[k] );
			}
			prev = cur;
		}
	}

	static void PerpBasis( Vector3 axis, out Vector3 u, out Vector3 v )
	{
		u = Vector3.Cross( axis, Vector3.Up );
		if ( u.LengthSquared < 0.001f )
			u = Vector3.Cross( axis, Vector3.Forward );
		u = u.Normal;
		v = Vector3.Cross( axis, u ).Normal;
	}

	// Ellipsoid, optionally sliced flat at z = r.z·sliceN (sliceN ≥ ~1 = untouched, from SdfBrush.SlicePlaneN):
	// latitude rings only run from the cut plane down, and a fan disc caps the flat top. The first ring sits
	// exactly ON the cut circle, so the disc seam-matches the dome.
	void Ellipsoid( Vector3 r, float sliceN, Color col )
	{
		const int lat = 10, lon = 16;
		int stride = lon + 1;
		int b0 = _verts.Count; // this copy's first vertex — the grid indices below are relative to it

		bool cut = sliceN < 0.999f;
		float th0 = cut ? MathF.Acos( Math.Clamp( sliceN, -0.999f, 1f ) ) : 0f; // top latitude of the surviving dome

		for ( int y = 0; y <= lat; y++ )
		{
			float th = th0 + y / (float)lat * (MathF.PI - th0);
			float st = MathF.Sin( th ), ct = MathF.Cos( th );
			for ( int x = 0; x <= lon; x++ )
			{
				float ph = x / (float)lon * MathF.PI * 2f;
				V( new Vector3( st * MathF.Cos( ph ) * r.x, st * MathF.Sin( ph ) * r.y, ct * r.z ), col );
			}
		}

		for ( int y = 0; y < lat; y++ )
		for ( int x = 0; x < lon; x++ )
		{
			int i0 = b0 + y * stride + x, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
			Quad( i0, i2, i3, i1 );
		}

		if ( cut )
		{
			// Flat cap: fan from the plane's centre to the top ring (which lies exactly on the cut circle).
			int centre = V( new Vector3( 0f, 0f, r.z * sliceN ), col );
			for ( int x = 0; x < lon; x++ )
				Tri( centre, b0 + x, b0 + x + 1 );
		}
	}

	void Box( Vector3 b, Color col )
	{
		var c = new int[8];
		for ( int i = 0; i < 8; i++ )
			c[i] = V( new Vector3( (i & 1) != 0 ? b.x : -b.x, (i & 2) != 0 ? b.y : -b.y, (i & 4) != 0 ? b.z : -b.z ), col );

		int[] f = { 0,1,3, 0,3,2, 4,6,7, 4,7,5, 0,4,5, 0,5,1, 2,3,7, 2,7,6, 0,2,6, 0,6,4, 1,5,7, 1,7,3 };
		for ( int k = 0; k < f.Length; k += 3 )
			Tri( c[f[k]], c[f[k + 1]], c[f[k + 2]] );
	}

	void Cylinder( float rad, float h, Color col )
	{
		const int N = 20;
		int top = V( new Vector3( 0, 0, h ), col );
		int bot = V( new Vector3( 0, 0, -h ), col );

		var rt = new int[N];
		var rb = new int[N];
		for ( int k = 0; k < N; k++ )
		{
			float a = k / (float)N * MathF.PI * 2f;
			float cx = MathF.Cos( a ) * rad, cy = MathF.Sin( a ) * rad;
			rt[k] = V( new Vector3( cx, cy, h ), col );
			rb[k] = V( new Vector3( cx, cy, -h ), col );
		}

		for ( int k = 0; k < N; k++ )
		{
			int n = (k + 1) % N;
			Quad( rb[k], rb[n], rt[n], rt[k] ); // side
			Tri( top, rt[n], rt[k] );           // top cap
			Tri( bot, rb[k], rb[n] );           // bottom cap
		}
	}

	// Cone (base radius R at −h, apex at +h, then shifted up by zoff — the RAW half-height — because the
	// cone pivots at its BASE), optionally sliced into a frustum at z = h·sliceN: the apex is replaced by a
	// top ring at the cone's radius there, plus a flat cap fan (matches SliceRound's cut plane).
	void Cone( float R, float h, float zoff, float sliceN, Color col )
	{
		const int N = 20;
		bool cut = sliceN < 0.999f;
		float zc = (cut ? h * sliceN : h) + zoff;
		float rc = cut ? R * (1f - sliceN) * 0.5f : 0f; // cone radius at the cut plane

		int bot = V( new Vector3( 0, 0, zoff - h ), col );
		var rb = new int[N];
		var rt = cut ? new int[N] : null;
		for ( int k = 0; k < N; k++ )
		{
			float a = k / (float)N * MathF.PI * 2f;
			float ca = MathF.Cos( a ), sa = MathF.Sin( a );
			rb[k] = V( new Vector3( ca * R, sa * R, zoff - h ), col );
			if ( cut )
				rt[k] = V( new Vector3( ca * rc, sa * rc, zc ), col );
		}

		if ( cut )
		{
			int top = V( new Vector3( 0, 0, zc ), col );
			for ( int k = 0; k < N; k++ )
			{
				int n = (k + 1) % N;
				Quad( rb[k], rb[n], rt[n], rt[k] ); // side wall
				Tri( top, rt[n], rt[k] );           // flat cut cap
				Tri( bot, rb[n], rb[k] );           // base cap
			}
			return;
		}

		int apex = V( new Vector3( 0, 0, zoff + h ), col );
		for ( int k = 0; k < N; k++ )
		{
			int n = (k + 1) % N;
			Tri( rb[k], rb[n], apex ); // side
			Tri( bot, rb[n], rb[k] );  // base cap
		}
	}

	// Extruded cross-section (triangle/star/hexagon) swept along Z — matches ExtrudedDistance. Caps are
	// triangle fans from the centroid, valid for the star too (it's star-shaped about its centre); the gizmo
	// shader is two-sided, so cap winding doesn't matter.
	readonly List<Vector2> _outline = new();
	void Extrusion( SdfCrossSection xs, Vector3 s, Color col )
	{
		SdfBrush.CrossSectionOutline( xs, s, _outline );
		int n = _outline.Count;
		if ( n < 3 )
			return;

		float hz = s.z;
		var top = new int[n];
		var bot = new int[n];
		for ( int i = 0; i < n; i++ )
		{
			var o = _outline[i];
			top[i] = V( new Vector3( o.x, o.y, hz ), col );
			bot[i] = V( new Vector3( o.x, o.y, -hz ), col );
		}
		int ct = V( new Vector3( 0, 0, hz ), col );
		int cb = V( new Vector3( 0, 0, -hz ), col );

		for ( int i = 0; i < n; i++ )
		{
			int j = (i + 1) % n;
			Tri( ct, top[i], top[j] );          // top cap fan
			Tri( cb, bot[j], bot[i] );          // bottom cap fan
			Quad( bot[i], bot[j], top[j], top[i] ); // side wall
		}
	}

	// ── mesh infra ───────────────────────────────────────────────────────────────────────────────────

	// Map a shape-local vertex to world: brush transform into sculpture space, reflect for the mirror copy
	// (componentwise sign across the enabled sculpture-origin planes), then the sculpture transform to world.
	int V( Vector3 local, Color col )
	{
		// A spline vertex is offset from the current control point (already in sculpture space); every other
		// shape is built in the brush's local frame and placed by Position/Rotation.
		var vSculpt = _spline ? _splineCenter + local : _brush.Position + _brush.Rotation * local;
		vSculpt *= _sign;
		var p = _sculptTx.PointToWorld( vSculpt );
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

	void Upload( Scene scene )
	{
		if ( _verts.Count < 3 )
		{
			Hide();
			return;
		}

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
			_so.Transform = Transform.Zero;
			_so.Flags.CastShadows = false;
			_so.Batchable = false;
			_so.RenderLayer = SceneRenderLayer.OverlayWithoutDepth; // on top, after post (no DOF), like the gizmo
		}

		_so.Bounds = new BBox( _bbMin, _bbMax );
		_so.RenderingEnabled = true;
	}
}
