using System;

namespace Mimiclay;

/// <summary>
/// Cheap "boolean union" shadow mesh for an SDF sculpture: every ENABLED ADDITIVE brush's primitive solid
/// (plus its mirror copies) meshed in sculpture-LOCAL space and merged into one low-poly Model. Overlapping
/// solids cast the union shadow, so it approximates the meshed SDF's shadow WITHOUT the surface-nets remesh
/// — used as the ShadowsOnly mesh while dragging (the full remesh runs on release for accurate LODs).
///
/// Subtractive brushes are ignored: carving the shadow would need real CSG, and the proxy being a touch
/// larger than the carved result is fine for a transient drag-time shadow.
/// </summary>
public sealed class SdfShadowProxy
{
	// Low tessellation — it's only a shadow caster, so it can be coarse.
	const int Lat = 8, Lon = 12, Ring = 12;

	readonly List<Vertex> _verts = new();
	readonly List<int> _indices = new();
	Vector3 _mn, _mx;

	// Build context.
	SdfBrush _brush;
	Vector3 _sign = Vector3.One;
	bool _spline;            // current brush is a spline — V() places verts relative to _splineCenter
	Vector3 _splineCenter;   // the control point a spline sphere is being built around (sculpture-local)

	public Model Build( List<SdfBrush> brushes, Material material )
	{
		if ( brushes is null )
			return null;

		_verts.Clear();
		_indices.Clear();
		_mn = new Vector3( float.MaxValue );
		_mx = new Vector3( float.MinValue );

		foreach ( var b in brushes )
		{
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue;

			_brush = b;
			_spline = b.Shape == SdfShape.Spline;
			int nx = b.MirrorX ? 1 : 0, ny = b.MirrorY ? 1 : 0, nz = b.MirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= nx; sx++ )
			for ( int sy = 0; sy <= ny; sy++ )
			for ( int sz = 0; sz <= nz; sz++ )
			{
				_sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				Shape( b );
			}
		}

		if ( _verts.Count < 3 )
			return null;

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( _verts.Count, _verts );
		mesh.CreateIndexBuffer( _indices.Count, _indices );
		mesh.Bounds = new BBox( _mn, _mx );

		return new ModelBuilder().AddMesh( mesh ).Create();
	}

	void Shape( SdfBrush b )
	{
		var s = b.Size;
		switch ( b.Shape )
		{
			case SdfShape.Box:
			case SdfShape.Text: Box( s ); break; // text casts its quad as the drag-time shadow
			case SdfShape.Cylinder: Cylinder( s.x, s.z ); break;
			case SdfShape.Cone: Cone( s.x, s.z, s.z, b.SlicePlaneN ); break; // zoff = h: base-pivot
			case SdfShape.Extruded: Extrusion( b.CrossSection, s ); break;
			case SdfShape.Spline: SplineSpheres( b ); break; // a coarse sphere per control point
			default: Ellipsoid( s, b.SlicePlaneN ); break; // Sphere / ellipsoid
		}
	}

	// Spline shadow: a sphere at each control point (radius = the point's w). V() is in spline mode so each
	// sphere is centred on its control point in sculpture space (mirror sign still applied).
	void SplineSpheres( SdfBrush b )
	{
		if ( b.Points is not { } pts )
			return;

		foreach ( var pt in pts )
		{
			_splineCenter = new Vector3( pt.x, pt.y, pt.z );
			Ellipsoid( new Vector3( pt.w ), 1f ); // never sliced
		}
	}

	// ── primitives (brush-local frame, axis = Z; same shapes as BrushGhost, coarser) ─────────────────

	// Optionally sliced flat at z = r.z·sliceN — see BrushGhost.Ellipsoid (same construction, coarser).
	void Ellipsoid( Vector3 r, float sliceN )
	{
		int stride = Lon + 1;
		int b0 = _verts.Count;

		bool cut = sliceN < 0.999f;
		float th0 = cut ? MathF.Acos( Math.Clamp( sliceN, -0.999f, 1f ) ) : 0f;

		for ( int y = 0; y <= Lat; y++ )
		{
			float th = th0 + y / (float)Lat * (MathF.PI - th0);
			float st = MathF.Sin( th ), ct = MathF.Cos( th );
			for ( int x = 0; x <= Lon; x++ )
			{
				float ph = x / (float)Lon * MathF.PI * 2f;
				V( new Vector3( st * MathF.Cos( ph ) * r.x, st * MathF.Sin( ph ) * r.y, ct * r.z ) );
			}
		}

		for ( int y = 0; y < Lat; y++ )
		for ( int x = 0; x < Lon; x++ )
		{
			int i0 = b0 + y * stride + x, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
			Quad( i0, i2, i3, i1 );
		}

		if ( cut )
		{
			int centre = V( new Vector3( 0f, 0f, r.z * sliceN ) );
			for ( int x = 0; x < Lon; x++ )
				Tri( centre, b0 + x, b0 + x + 1 );
		}
	}

	void Box( Vector3 b )
	{
		var c = new int[8];
		for ( int i = 0; i < 8; i++ )
			c[i] = V( new Vector3( (i & 1) != 0 ? b.x : -b.x, (i & 2) != 0 ? b.y : -b.y, (i & 4) != 0 ? b.z : -b.z ) );

		int[] f = { 0,1,3, 0,3,2, 4,6,7, 4,7,5, 0,4,5, 0,5,1, 2,3,7, 2,7,6, 0,2,6, 0,6,4, 1,5,7, 1,7,3 };
		for ( int k = 0; k < f.Length; k += 3 )
			Tri( c[f[k]], c[f[k + 1]], c[f[k + 2]] );
	}

	void Cylinder( float rad, float h )
	{
		int top = V( new Vector3( 0, 0, h ) );
		int bot = V( new Vector3( 0, 0, -h ) );

		var rt = new int[Ring];
		var rb = new int[Ring];
		for ( int k = 0; k < Ring; k++ )
		{
			float a = k / (float)Ring * MathF.PI * 2f;
			float cx = MathF.Cos( a ) * rad, cy = MathF.Sin( a ) * rad;
			rt[k] = V( new Vector3( cx, cy, h ) );
			rb[k] = V( new Vector3( cx, cy, -h ) );
		}

		for ( int k = 0; k < Ring; k++ )
		{
			int n = (k + 1) % Ring;
			Quad( rb[k], rb[n], rt[n], rt[k] );
			Tri( top, rt[n], rt[k] );
			Tri( bot, rb[k], rb[n] );
		}
	}

	// Base-pivot cone (shifted up by zoff), optionally sliced into a frustum at z = h·sliceN — see
	// BrushGhost.Cone (same construction, coarser).
	void Cone( float R, float h, float zoff, float sliceN )
	{
		bool cut = sliceN < 0.999f;
		float zc = (cut ? h * sliceN : h) + zoff;
		float rc = cut ? R * (1f - sliceN) * 0.5f : 0f;

		int bot = V( new Vector3( 0, 0, zoff - h ) );
		var rb = new int[Ring];
		var rt = cut ? new int[Ring] : null;
		for ( int k = 0; k < Ring; k++ )
		{
			float a = k / (float)Ring * MathF.PI * 2f;
			float ca = MathF.Cos( a ), sa = MathF.Sin( a );
			rb[k] = V( new Vector3( ca * R, sa * R, zoff - h ) );
			if ( cut )
				rt[k] = V( new Vector3( ca * rc, sa * rc, zc ) );
		}

		if ( cut )
		{
			int top = V( new Vector3( 0, 0, zc ) );
			for ( int k = 0; k < Ring; k++ )
			{
				int n = (k + 1) % Ring;
				Quad( rb[k], rb[n], rt[n], rt[k] );
				Tri( top, rt[n], rt[k] );
				Tri( bot, rb[n], rb[k] );
			}
			return;
		}

		int apex = V( new Vector3( 0, 0, zoff + h ) );
		for ( int k = 0; k < Ring; k++ )
		{
			int n = (k + 1) % Ring;
			Tri( rb[k], rb[n], apex );
			Tri( bot, rb[n], rb[k] );
		}
	}

	// Extruded cross-section (triangle/star/hexagon) swept along Z — centroid-fan caps + side wall, matching
	// BrushGhost.Extrusion (the fan is valid for the star: it's star-shaped about its centre).
	readonly List<Vector2> _outline = new();
	void Extrusion( SdfCrossSection xs, Vector3 s )
	{
		SdfBrush.CrossSectionOutline( xs, s, _outline );
		int n = _outline.Count;
		if ( n < 3 )
			return;

		var top = new int[n];
		var bot = new int[n];
		for ( int i = 0; i < n; i++ )
		{
			var o = _outline[i];
			top[i] = V( new Vector3( o.x, o.y, s.z ) );
			bot[i] = V( new Vector3( o.x, o.y, -s.z ) );
		}
		int ct = V( new Vector3( 0, 0, s.z ) );
		int cb = V( new Vector3( 0, 0, -s.z ) );

		for ( int i = 0; i < n; i++ )
		{
			int j = (i + 1) % n;
			Tri( ct, top[i], top[j] );
			Tri( cb, bot[j], bot[i] );
			Quad( bot[i], bot[j], top[j], top[i] );
		}
	}

	// ── mesh infra ───────────────────────────────────────────────────────────────────────────────────

	// Brush-local vertex → sculpture-local (reflected for the mirror copy). No sculpture transform — the
	// ModelRenderer this is assigned to applies the GameObject transform.
	int V( Vector3 local )
	{
		var p = (_spline ? _splineCenter + local : _brush.Position + _brush.Rotation * local) * _sign;
		_mn = Vector3.Min( _mn, p );
		_mx = Vector3.Max( _mx, p );
		_verts.Add( new Vertex( p, Vector3.Up, Vector3.Forward, Vector4.Zero ) );
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
}
