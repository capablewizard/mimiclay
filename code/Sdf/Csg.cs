using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Constructive Solid Geometry via BSP trees — a C# port of Evan Wallace's csg.js. Operates on a soup of
/// convex polygons (each with a plane) and supports Union / Subtract / Intersect by clipping polygon sets
/// against each other's BSP trees. Robust enough for simple convex primitives (boxes, spheres, cylinders,
/// cones, prisms), which is all <see cref="SdfCsgMesher"/> feeds it.
/// </summary>
public sealed class Csg
{
	public List<CsgPolygon> Polygons;

	public Csg() { Polygons = new(); }

	public static Csg FromPolygons( List<CsgPolygon> polygons ) => new() { Polygons = polygons };

	public Csg Clone() => FromPolygons( Polygons.Select( p => p.Clone() ).ToList() );

	public Csg Union( Csg other )
	{
		var a = new CsgNode( Clone().Polygons );
		var b = new CsgNode( other.Clone().Polygons );
		a.ClipTo( b );
		b.ClipTo( a );
		b.Invert();
		b.ClipTo( a );
		b.Invert();
		a.Build( b.AllPolygons() );
		return FromPolygons( a.AllPolygons() );
	}

	public Csg Subtract( Csg other )
	{
		var a = new CsgNode( Clone().Polygons );
		var b = new CsgNode( other.Clone().Polygons );
		a.Invert();
		a.ClipTo( b );
		b.ClipTo( a );
		b.Invert();
		b.ClipTo( a );
		b.Invert();
		a.Build( b.AllPolygons() );
		a.Invert();
		return FromPolygons( a.AllPolygons() );
	}

	public Csg Intersect( Csg other )
	{
		var a = new CsgNode( Clone().Polygons );
		var b = new CsgNode( other.Clone().Polygons );
		a.Invert();
		b.ClipTo( a );
		b.Invert();
		a.ClipTo( b );
		b.ClipTo( a );
		a.Build( b.AllPolygons() );
		a.Invert();
		return FromPolygons( a.AllPolygons() );
	}
}

public sealed class CsgVertex
{
	public Vector3 Pos;
	public Vector3 Normal;

	public CsgVertex( Vector3 pos, Vector3 normal ) { Pos = pos; Normal = normal; }
	public CsgVertex Clone() => new( Pos, Normal );
	public void Flip() => Normal = -Normal;
	public CsgVertex Interpolate( CsgVertex o, float t ) =>
		new( Vector3.Lerp( Pos, o.Pos, t ), Vector3.Lerp( Normal, o.Normal, t ) );
}

public struct CsgPlane
{
	public const float Epsilon = 1e-5f;

	public Vector3 Normal;
	public float W;

	public CsgPlane( Vector3 normal, float w ) { Normal = normal; W = w; }

	public static CsgPlane FromPoints( Vector3 a, Vector3 b, Vector3 c )
	{
		var n = Vector3.Cross( b - a, c - a );
		float len = n.Length;
		if ( len < 1e-8f )
			return new CsgPlane( Vector3.Zero, 0f ); // degenerate (zero-area) — flagged invalid

		n /= len;
		return new CsgPlane( n, Vector3.Dot( n, a ) );
	}

	public CsgPlane Clone() => new( Normal, W );
	public void Flip() { Normal = -Normal; W = -W; }

	// csg.js polygon classification constants.
	const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

	public void SplitPolygon( CsgPolygon polygon, List<CsgPolygon> coplanarFront, List<CsgPolygon> coplanarBack,
		List<CsgPolygon> front, List<CsgPolygon> back )
	{
		int polygonType = 0;
		var types = new int[polygon.Vertices.Count];
		for ( int i = 0; i < polygon.Vertices.Count; i++ )
		{
			float t = Vector3.Dot( Normal, polygon.Vertices[i].Pos ) - W;
			int type = (t < -Epsilon) ? Back : (t > Epsilon) ? Front : Coplanar;
			polygonType |= type;
			types[i] = type;
		}

		switch ( polygonType )
		{
			case Coplanar:
				(Vector3.Dot( Normal, polygon.Plane.Normal ) > 0 ? coplanarFront : coplanarBack).Add( polygon );
				break;
			case Front:
				front.Add( polygon );
				break;
			case Back:
				back.Add( polygon );
				break;
			default: // Spanning — split the polygon along the plane.
				var f = new List<CsgVertex>();
				var b = new List<CsgVertex>();
				for ( int i = 0; i < polygon.Vertices.Count; i++ )
				{
					int j = (i + 1) % polygon.Vertices.Count;
					int ti = types[i], tj = types[j];
					var vi = polygon.Vertices[i];
					var vj = polygon.Vertices[j];
					if ( ti != Back ) f.Add( vi );
					if ( ti != Front ) b.Add( ti != Back ? vi.Clone() : vi );
					if ( (ti | tj) == Spanning )
					{
						float t = (W - Vector3.Dot( Normal, vi.Pos )) / Vector3.Dot( Normal, vj.Pos - vi.Pos );
						var v = vi.Interpolate( vj, t );
						f.Add( v );
						b.Add( v.Clone() );
					}
				}
				if ( f.Count >= 3 ) front.Add( new CsgPolygon( f ) );
				if ( b.Count >= 3 ) back.Add( new CsgPolygon( b ) );
				break;
		}
	}
}

public sealed class CsgPolygon
{
	public List<CsgVertex> Vertices;
	public CsgPlane Plane;

	public CsgPolygon( List<CsgVertex> vertices )
	{
		Vertices = vertices;
		Plane = CsgPlane.FromPoints( vertices[0].Pos, vertices[1].Pos, vertices[2].Pos );
	}

	public CsgPolygon Clone() => new( Vertices.Select( v => v.Clone() ).ToList() );

	public void Flip()
	{
		Vertices.Reverse();
		foreach ( var v in Vertices )
			v.Flip();
		Plane.Flip();
	}
}

public sealed class CsgNode
{
	CsgPlane _plane;
	bool _hasPlane;
	CsgNode _front, _back;
	readonly List<CsgPolygon> _polygons = new();

	public CsgNode() { }
	public CsgNode( List<CsgPolygon> polygons ) { if ( polygons is { Count: > 0 } ) Build( polygons ); }

	// Backstop against pathological infinite splitting (degenerate input): cap the total work Build does.
	// Convex solids make LINEAR-depth BSPs (a sphere ≈ its triangle count deep), so depth itself must NOT be
	// capped (that corrupts clipping → holes) — instead every traversal below is ITERATIVE (explicit stacks),
	// so depth can't overflow the call stack, and this budget bounds runaway without a crash.
	const int BuildBudget = 500_000;

	// All four operations are iterative so a deep (but legitimate) tree can't StackOverflow.

	public void Invert()
	{
		var stack = new Stack<CsgNode>();
		stack.Push( this );
		while ( stack.Count > 0 )
		{
			var node = stack.Pop();
			foreach ( var p in node._polygons )
				p.Flip();
			if ( node._hasPlane )
				node._plane.Flip();
			(node._front, node._back) = (node._back, node._front);
			if ( node._front != null ) stack.Push( node._front );
			if ( node._back != null ) stack.Push( node._back );
		}
	}

	List<CsgPolygon> ClipPolygons( List<CsgPolygon> polygons )
	{
		var output = new List<CsgPolygon>();
		var work = new Stack<(CsgNode node, List<CsgPolygon> polys)>();
		work.Push( (this, polygons) );
		while ( work.Count > 0 )
		{
			var (node, polys) = work.Pop();
			if ( !node._hasPlane )
			{
				output.AddRange( polys );
				continue;
			}

			var front = new List<CsgPolygon>();
			var back = new List<CsgPolygon>();
			foreach ( var p in polys )
				node._plane.SplitPolygon( p, front, back, front, back );

			if ( node._front != null ) work.Push( (node._front, front) );
			else output.AddRange( front ); // outside the solid — survives

			if ( node._back != null ) work.Push( (node._back, back) );
			// else: inside the solid — discarded
		}
		return output;
	}

	public void ClipTo( CsgNode bsp )
	{
		var stack = new Stack<CsgNode>();
		stack.Push( this );
		while ( stack.Count > 0 )
		{
			var node = stack.Pop();
			var clipped = bsp.ClipPolygons( node._polygons );
			node._polygons.Clear();
			node._polygons.AddRange( clipped );
			if ( node._front != null ) stack.Push( node._front );
			if ( node._back != null ) stack.Push( node._back );
		}
	}

	public List<CsgPolygon> AllPolygons()
	{
		var result = new List<CsgPolygon>();
		var stack = new Stack<CsgNode>();
		stack.Push( this );
		while ( stack.Count > 0 )
		{
			var node = stack.Pop();
			result.AddRange( node._polygons );
			if ( node._front != null ) stack.Push( node._front );
			if ( node._back != null ) stack.Push( node._back );
		}
		return result;
	}

	public void Build( List<CsgPolygon> polygons )
	{
		int budget = BuildBudget;
		var work = new Stack<(CsgNode node, List<CsgPolygon> polys)>();
		work.Push( (this, polygons) );
		while ( work.Count > 0 )
		{
			if ( --budget <= 0 )
				return; // runaway — stop (tree may be incomplete, but no OOM/hang/crash)

			var (node, polys) = work.Pop();
			if ( polys.Count == 0 )
				continue;

			if ( !node._hasPlane )
			{
				node._plane = polys[0].Plane.Clone();
				node._hasPlane = true;
			}

			var front = new List<CsgPolygon>();
			var back = new List<CsgPolygon>();
			foreach ( var p in polys )
				node._plane.SplitPolygon( p, node._polygons, node._polygons, front, back );

			if ( front.Count > 0 ) { node._front ??= new CsgNode(); work.Push( (node._front, front) ); }
			if ( back.Count > 0 ) { node._back ??= new CsgNode(); work.Push( (node._back, back) ); }
		}
	}
}
