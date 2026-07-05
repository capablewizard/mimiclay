using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Authoring helper that fills a target <see cref="SdfSculpture"/> with a tennis ball: a sphere
/// body plus one closed spline seam following the classic "baseball curve" — the single closed
/// curve that lies exactly on the sphere and splits its surface into two congruent pieces.
/// Add it next to a sculpture, tweak the sliders live, then "Apply &amp; Remove Builder" to bake
/// the brushes and delete the builder. It OWNS the sculpture's brush list while attached.
///
/// The seam is the exact spherical parametrisation
///   x = a·cos t + b·cos 3t,  y = a·sin t − b·sin 3t,  z = 2·sqrt(ab)·sin 2t,   a + b = 1,
/// which satisfies x²+y²+z² = 1 for all t (so every control point sits on the sphere). The
/// shape parameter <see cref="SeamShape"/> = a: lower values push the seam deeper toward the
/// poles (curvier), higher values flatten it toward a wavy equator.
/// </summary>
[Title( "Tennis Ball Builder" )]
[Category( "SDF" )]
[Icon( "sports_tennis" )]
public sealed class TennisBallBuilder : Component, Component.ExecuteInEditor
{
	/// <summary>The sculpture to fill. Defaults to one on this GameObject.</summary>
	[Property] public SdfSculpture Sculpture { get; set; }

	[Property, Range( 8f, 120f ), Group( "Ball" )] public float Diameter { get; set; } = 22.83f;
	[Property, Group( "Ball" )] public Color BallColor { get; set; } = new( 0.849f, 0.97f, 0.22f );

	/// <summary>Baseball-curve shape parameter (a, with a+b=1). Lower = deeper, curvier seam that
	/// reaches higher latitudes; higher = shallower, flatter seam. ~0.78 is the classic look.</summary>
	[Property, Range( 0.6f, 0.92f ), Group( "Seam" )] public float SeamShape { get; set; } = 0.78f;

	/// <summary>Seam tube radius (world units) — the width of the groove/ridge.</summary>
	[Property, Range( 0.3f, 5f ), Group( "Seam" )] public float SeamRadius { get; set; } = 1.4f;

	/// <summary>Control points around the loop. The spline curves through them (Catmull-Rom when
	/// Curvature &gt; 0), so ~24 already reads as a smooth seam; raise it for a crisper curve.</summary>
	[Property, Range( 8, 96 ), Group( "Seam" )] public int SeamPoints { get; set; } = 24;

	[Property, Range( 0f, 1f ), Group( "Seam" )] public float SeamCurvature { get; set; } = 1f;

	[Property, Range( 0f, 3f ), Group( "Seam" )] public float SeamBlend { get; set; } = 0.2f;

	/// <summary>True = carve the seam as a groove (Subtract, as the rough prefab had it); false =
	/// a raised ridge (Add) that shows <see cref="SeamColor"/> — the iconic pale tennis-ball line.</summary>
	[Property, Group( "Seam" )] public bool Recessed { get; set; } = true;

	[Property, Group( "Seam" )] public Color SeamColor { get; set; } = Color.White;

	int _lastHash;

	protected override void OnEnabled()
	{
		Sculpture ??= GameObject.Components.Get<SdfSculpture>();
		Regenerate();
	}

	// ExecuteInEditor ticks this in the scene view; regenerate only when a parameter changed
	// (cheap hash compare otherwise) so the sliders give live feedback.
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
			Log.Warning( "Tennis Ball Builder: no SdfSculpture assigned (or on this object)." );
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
		h.Add( Diameter ); h.Add( BallColor );
		h.Add( SeamShape ); h.Add( SeamRadius ); h.Add( SeamPoints );
		h.Add( SeamCurvature ); h.Add( SeamBlend ); h.Add( Recessed ); h.Add( SeamColor );
		h.Add( Sculpture );
		return h.ToHashCode();
	}

	List<SdfBrush> BuildBrushes()
	{
		float R = Diameter * 0.5f;

		var ball = new SdfBrush
		{
			Shape = SdfShape.Sphere,
			Size = new Vector3( R ),
			Blend = 6f,
			Rounding = 0.75f,
			Color = BallColor,
			Roughness = 0.5f,
		};

		var seam = new SdfBrush
		{
			Shape = SdfShape.Spline,
			Operation = Recessed ? SdfOperation.Subtract : SdfOperation.Add,
			Points = SeamCurvePoints( R ),
			SplineClosed = true,
			Curvature = SeamCurvature,
			Size = new Vector3( R ),
			Blend = SeamBlend,
			Rounding = 0.75f,
			Color = SeamColor,
			Roughness = 1f,
		};

		return new List<SdfBrush> { ball, seam };
	}

	// The baseball curve, sampled at SeamPoints control points and projected onto the sphere of
	// radius R. The last point is omitted (SplineClosed joins it back to the first).
	List<Vector4> SeamCurvePoints( float R )
	{
		float a = SeamShape;
		float b = 1f - a;
		float c = 2f * MathF.Sqrt( MathF.Max( a * b, 0f ) );

		int n = Math.Clamp( SeamPoints, 4, 200 );
		var pts = new List<Vector4>( n );
		for ( int i = 0; i < n; i++ )
		{
			float t = (i / (float)n) * (2f * MathF.PI);
			float x = a * MathF.Cos( t ) + b * MathF.Cos( 3f * t );
			float y = a * MathF.Sin( t ) - b * MathF.Sin( 3f * t );
			float z = c * MathF.Sin( 2f * t );
			// Unit by construction; normalise to shed float drift, then scale onto the surface.
			var p = new Vector3( x, y, z ).Normal * R;
			pts.Add( new Vector4( p.x, p.y, p.z, SeamRadius ) );
		}
		return pts;
	}
}
