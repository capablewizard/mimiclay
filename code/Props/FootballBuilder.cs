using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Authoring helper that fills a target <see cref="SdfSculpture"/> with a soccer-ball
/// (truncated-icosahedron) brush layout: a sphere body plus 12 pentagon + 20 hexagon panels.
/// Add it next to a sculpture, tweak the sliders and watch it rebuild live, then hit
/// "Apply &amp; Remove Builder" — the generated brushes stay on the sculpture and the builder
/// deletes itself. It OWNS the sculpture's brush list while attached (every change replaces it),
/// so add it to a fresh/empty sculpture.
///
/// Geometry notes (learned the hard way authoring this):
///  - Pentagon centres = icosahedron vertices; hexagon centres = icosahedron FACE centres
///    (normalised sum of each triple of mutually-adjacent vertices). Do NOT use dodecahedron
///    vertex coords for the hexagons — that set is the dual of a differently-oriented icosahedron
///    and drops some hexagons ~10.8 deg from a pentagon, so the panels overlap.
///  - Pentagons and hexagons sit at slightly DIFFERENT face-plane radii (r_p/r_h ~= 1.027) and
///    have different circumradii. Placing every panel at one radius makes the pent-hex seams
///    tighter than the hex-hex seams; using the true per-type radii lets a single PanelScale
///    close every seam uniformly (1.0 = edge-to-edge, lower = uniform hairline seam).
/// </summary>
[Title( "Football Builder" )]
[Category( "SDF" )]
[Icon( "sports_soccer" )]
public sealed class FootballBuilder : Component, Component.ExecuteInEditor
{
	/// <summary>The sculpture to fill. Defaults to one on this GameObject.</summary>
	[Property] public SdfSculpture Sculpture { get; set; }

	[Property, Range( 10f, 160f ), Group( "Ball" )] public float Diameter { get; set; } = 45.4f;

	/// <summary>Panel size relative to exact edge-to-edge tiling. 1 = panels touch (no seam);
	/// lower opens a uniform seam that shows the ball colour through; above 1 they overlap.</summary>
	[Property, Range( 0.6f, 1.05f ), Group( "Panels" )] public float PanelScale { get; set; } = 0.97f;

	/// <summary>How far each panel's outer face rises above the ball surface (world units). Uniform
	/// across pentagons and hexagons regardless of diameter.</summary>
	[Property, Range( 0f, 4f ), Group( "Panels" )] public float PanelHeight { get; set; } = 0.7f;

	[Property, Range( 0f, 6f ), Group( "Panels" )] public float PanelRounding { get; set; } = 1.8f;

	/// <summary>Smooth-blend of the panels into the ball. 0 = crisp edges (classic look), higher
	/// melts the panels into the surface for a softer clay feel.</summary>
	[Property, Range( 0f, 3f ), Group( "Panels" )] public float PanelBlend { get; set; } = 0.1f;

	[Property, Group( "Colours" )] public Color BallColor { get; set; } = new( 0.85f, 0.88f, 0.9f );
	[Property, Group( "Colours" )] public Color PentagonColor { get; set; } = new( 0.09f, 0.09f, 0.1f );
	[Property, Group( "Colours" )] public Color HexagonColor { get; set; } = new( 0.9f, 0.92f, 0.94f );

	int _lastHash;

	protected override void OnEnabled()
	{
		Sculpture ??= GameObject.Components.Get<SdfSculpture>();
		Regenerate();
	}

	// ExecuteInEditor ticks this in the scene view; regenerate only when a parameter actually
	// changed (a cheap hash compare otherwise), which gives live slider feedback.
	protected override void OnUpdate()
	{
		if ( ParamHash() != _lastHash )
			Regenerate();
	}

	[Button( "Rebuild Now" )]
	public void Regenerate()
	{
		Sculpture ??= GameObject.Components.Get<SdfSculpture>();
		if ( !Sculpture.IsValid() )
		{
			Log.Warning( "Football Builder: no SdfSculpture assigned (or on this object)." );
			return;
		}

		Sculpture.Brushes = BuildBrushes();
		Sculpture.Rebuild();               // updates the shadow mesh / collider; the raymarcher self-refreshes
		_lastHash = ParamHash();
	}

	/// <summary>Bake the current layout into the sculpture and delete this builder — the brushes stay.</summary>
	[Button( "Apply & Remove Builder" )]
	public void ApplyAndRemove()
	{
		Regenerate();
		Destroy();
	}

	int ParamHash()
	{
		var h = new HashCode();
		h.Add( Diameter ); h.Add( PanelScale ); h.Add( PanelHeight );
		h.Add( PanelRounding ); h.Add( PanelBlend );
		h.Add( BallColor ); h.Add( PentagonColor ); h.Add( HexagonColor );
		h.Add( Sculpture );
		return h.ToHashCode();
	}

	List<SdfBrush> BuildBrushes()
	{
		float R = Diameter * 0.5f;
		var brushes = new List<SdfBrush>( 33 );

		// Ball body.
		brushes.Add( new SdfBrush
		{
			Shape = SdfShape.Sphere,
			Size = new Vector3( R ),
			Blend = 6f,
			Rounding = 0.75f,
			Color = BallColor,
			Roughness = 0.5f,
		} );

		var pent = PentagonCentres();
		var hex = HexagonCentres( pent );
		var all = new List<Vector3>( pent );
		all.AddRange( hex );

		// True truncated-icosahedron proportions (canonical edge = 2), scaled so the solid's
		// vertices inscribe on the ball surface (k = R / circumradius). Pentagon and hexagon
		// faces then sit at their correct, slightly different plane radii so all seams align.
		const float edge = 2f;
		float rPentC = edge / (2f * MathF.Sin( 36f * MathF.PI / 180f ) ); // pentagon circumradius
		float rHexC = edge;                                                // hexagon circumradius (= edge)
		float rSolid = (edge / 4f) * MathF.Sqrt( 58f + 18f * MathF.Sqrt( 5f ) );
		float planePent = MathF.Sqrt( rSolid * rSolid - rPentC * rPentC ); // centre -> pentagon plane
		float planeHex = MathF.Sqrt( rSolid * rSolid - rHexC * rHexC );    // centre -> hexagon plane
		float k = R / rSolid;

		foreach ( var n in pent )
			brushes.Add( Panel( n, all, isPent: true, k * planePent, k * rPentC * PanelScale, R, SdfCrossSection.Pentagon, PentagonColor ) );
		foreach ( var n in hex )
			brushes.Add( Panel( n, all, isPent: false, k * planeHex, k * rHexC * PanelScale, R, SdfCrossSection.Hexagon, HexagonColor ) );

		return brushes;
	}

	SdfBrush Panel( Vector3 n, List<Vector3> all, bool isPent, float place, float cr, float R, SdfCrossSection xs, Color col )
	{
		// In-plane frame: align a polygon EDGE MIDPOINT to the nearest neighbouring panel so seams
		// line up (SdfBrush: hexagon vertices at k*60 from +X -> edge mids at 30 deg; pentagon
		// vertices at 90+k*72 -> edge mids at 126 deg). The 5-/6-fold symmetry aligns the rest.
		var near = NearestOther( n, all );
		var e1 = (near - Vector3.Dot( near, n ) * n).Normal;
		var e2 = Vector3.Cross( n, e1 ).Normal;              // right-handed: e1 x e2 = n
		float thetaE = (isPent ? 126f : 30f) * MathF.PI / 180f;
		float ct = MathF.Cos( thetaE ), st = MathF.Sin( thetaE );
		var xw = ct * e1 - st * e2;
		var yw = st * e1 + ct * e2;
		var rot = BasisToRotation( xw, yw, n );

		// Extrude from below the surface up to R + PanelHeight, so every panel is proud by the same
		// amount whatever its plane depth or the ball diameter.
		float halfDepth = MathF.Max( R + PanelHeight - place, 0.3f );

		return new SdfBrush
		{
			Shape = SdfShape.Extruded,
			CrossSection = xs,
			Position = n * place,
			Rotation = rot,
			Size = new Vector3( cr, cr, halfDepth ),
			Blend = PanelBlend,
			Rounding = PanelRounding,
			Color = col,
			Roughness = 0.5f,
		};
	}

	// --- pure geometry ------------------------------------------------------

	static List<Vector3> PentagonCentres()
	{
		float phi = (1f + MathF.Sqrt( 5f )) / 2f;
		var list = new List<Vector3>( 12 );
		foreach ( int s1 in new[] { 1, -1 } )
			foreach ( int s2 in new[] { 1, -1 } )
			{
				list.Add( new Vector3( 0f, s1, s2 * phi ).Normal );
				list.Add( new Vector3( s1, s2 * phi, 0f ).Normal );
				list.Add( new Vector3( s1 * phi, 0f, s2 ).Normal );
			}
		return list;
	}

	static List<Vector3> HexagonCentres( List<Vector3> pent )
	{
		// icosahedron face centres: triples of mutually-adjacent vertices (adjacent when the angle
		// between normalised vertices is the edge angle, cos = 1/sqrt5).
		float edgeCos = 1f / MathF.Sqrt( 5f );
		var list = new List<Vector3>( 20 );
		for ( int i = 0; i < 12; i++ )
			for ( int j = i + 1; j < 12; j++ )
				for ( int m = j + 1; m < 12; m++ )
				{
					if ( MathF.Abs( Vector3.Dot( pent[i], pent[j] ) - edgeCos ) < 0.01f
					  && MathF.Abs( Vector3.Dot( pent[i], pent[m] ) - edgeCos ) < 0.01f
					  && MathF.Abs( Vector3.Dot( pent[j], pent[m] ) - edgeCos ) < 0.01f )
						list.Add( (pent[i] + pent[j] + pent[m]).Normal );
				}
		return list;
	}

	static Vector3 NearestOther( Vector3 n, List<Vector3> all )
	{
		Vector3 best = default;
		float bestDot = -2f;
		foreach ( var m in all )
		{
			float d = Vector3.Dot( n, m );
			if ( d < 0.99999f && d > bestDot ) { bestDot = d; best = m; }
		}
		return best;
	}

	// Rotation mapping local axes X,Y,Z onto the world basis vectors (columns of the rotation
	// matrix) -> quaternion. Standard matrix-to-quaternion; the basis is orthonormal so it's unit.
	static Rotation BasisToRotation( Vector3 x, Vector3 y, Vector3 z )
	{
		float m00 = x.x, m10 = x.y, m20 = x.z;
		float m01 = y.x, m11 = y.y, m21 = y.z;
		float m02 = z.x, m12 = z.y, m22 = z.z;
		float tr = m00 + m11 + m22;
		float qx, qy, qz, qw;
		if ( tr > 0f )
		{
			float s = MathF.Sqrt( tr + 1f ) * 2f;
			qw = 0.25f * s; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s;
		}
		else if ( m00 > m11 && m00 > m22 )
		{
			float s = MathF.Sqrt( 1f + m00 - m11 - m22 ) * 2f;
			qw = (m21 - m12) / s; qx = 0.25f * s; qy = (m01 + m10) / s; qz = (m02 + m20) / s;
		}
		else if ( m11 > m22 )
		{
			float s = MathF.Sqrt( 1f + m11 - m00 - m22 ) * 2f;
			qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = 0.25f * s; qz = (m12 + m21) / s;
		}
		else
		{
			float s = MathF.Sqrt( 1f + m22 - m00 - m11 ) * 2f;
			qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = 0.25f * s;
		}
		return new Rotation( qx, qy, qz, qw );
	}
}
