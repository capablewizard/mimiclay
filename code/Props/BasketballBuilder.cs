using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Authoring helper that fills a target <see cref="SdfSculpture"/> with a basketball: an orange
/// sphere plus its seams as recessed black grooves. The seams are a horizontal equator, a vertical
/// equator (two perpendicular great-circle rings), and the tennis-ball "baseball curve" — the same
/// closed weaving curve as the tennis ball seam, exaggerated. Together they read as a basketball.
///
/// The grooves are Subtract splines whose colour tints the cut walls (see SdfBrush.SampleSurface —
/// "Subtraction tints the cut walls with the brush material"), so a black seam brush gives genuinely
/// recessed BLACK seams rather than revealing the orange interior.
///
/// Add it next to a sculpture, tweak the sliders live, then "Apply &amp; Remove Builder" to bake the
/// brushes and delete the builder. It OWNS the sculpture's brush list while attached.
/// </summary>
[Title( "Basketball Builder" )]
[Category( "SDF" )]
[Icon( "sports_basketball" )]
public sealed class BasketballBuilder : Component, Component.ExecuteInEditor
{
	/// <summary>The sculpture to fill. Defaults to one on this GameObject.</summary>
	[Property] public SdfSculpture Sculpture { get; set; }

	[Property, Range( 8f, 120f ), Group( "Ball" )] public float Diameter { get; set; } = 24f;
	[Property, Group( "Ball" )] public Color BallColor { get; set; } = new( 0.85f, 0.38f, 0.13f );

	/// <summary>Seam groove tube radius (world units) — the width/depth of the black channels.</summary>
	[Property, Range( 0.2f, 4f ), Group( "Seams" )] public float SeamRadius { get; set; } = 1.0f;

	/// <summary>Control points per great circle. The spline curves through them (Catmull-Rom when
	/// Curvature &gt; 0), so ~32 gives a clean circle.</summary>
	[Property, Range( 8, 96 ), Group( "Seams" )] public int SeamPoints { get; set; } = 32;

	[Property, Range( 0f, 1f ), Group( "Seams" )] public float SeamCurvature { get; set; } = 1f;

	/// <summary>Shape of the tennis-ball baseball curve (a, with a+b=1). LOWER = more exaggerated /
	/// deeper weave (reaches higher latitude); higher = shallower. The tennis ball uses ~0.78, so a
	/// basketball wants it a bit lower/more exaggerated.</summary>
	[Property, Range( 0.6f, 0.85f ), Group( "Seams" )] public float SeamShape { get; set; } = 0.70f;

	/// <summary>Rotation of the baseball curve about the vertical axis (degrees). 45° sits its
	/// crossings between the two equator rings instead of on top of where they meet.</summary>
	[Property, Range( 0f, 90f ), Group( "Seams" )] public float SeamTwist { get; set; } = 45f;

	/// <summary>Orients the whole seam pattern about the Y axis (degrees).</summary>
	[Property, Range( -180f, 180f ), Group( "Seams" )] public float PatternYRotation { get; set; } = 90f;

	/// <summary>Softness of the groove edge / colour transition. 0 = crisp black line, higher melts
	/// the black into the orange for a softer clay look.</summary>
	[Property, Range( 0f, 3f ), Group( "Seams" )] public float SeamBlend { get; set; } = 0.35f;

	/// <summary>True = recessed grooves (Subtract, the real basketball look); false = raised ridges (Add).</summary>
	[Property, Group( "Seams" )] public bool Recessed { get; set; } = true;

	[Property, Group( "Seams" )] public Color SeamColor { get; set; } = new( 0.06f, 0.06f, 0.07f );

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
			Log.Warning( "Basketball Builder: no SdfSculpture assigned (or on this object)." );
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
		h.Add( SeamRadius ); h.Add( SeamPoints ); h.Add( SeamCurvature ); h.Add( SeamShape ); h.Add( SeamTwist );
		h.Add( PatternYRotation ); h.Add( SeamBlend ); h.Add( Recessed ); h.Add( SeamColor );
		h.Add( Sculpture );
		return h.ToHashCode();
	}

	List<SdfBrush> BuildBrushes()
	{
		float R = Diameter * 0.5f;
		var brushes = new List<SdfBrush>( 10 )
		{
			new SdfBrush
			{
				Shape = SdfShape.Sphere,
				Size = new Vector3( R ),
				Blend = 6f,
				Rounding = 0.75f,
				Color = BallColor,
				Roughness = 0.5f,
			},
		};

		// Two perpendicular great-circle rings (a horizontal and a vertical "equator") + the exaggerated
		// tennis-ball baseball curve weaving through them.
		var ex = new Vector3( 1f, 0f, 0f );
		var ey = new Vector3( 0f, 1f, 0f );
		var ez = new Vector3( 0f, 0f, 1f );
		brushes.Add( GreatCircle( ex, ey, R ) ); // horizontal equator (z = 0)
		brushes.Add( GreatCircle( ex, ez, R ) ); // vertical equator   (y = 0)
		brushes.Add( BaseballCurve( R ) );       // exaggerated tennis-ball seam

		return brushes;
	}

	// A great-circle ring in the plane spanned by orthonormal u, v.
	SdfBrush GreatCircle( Vector3 u, Vector3 v, float R )
	{
		int n = Math.Clamp( SeamPoints * 2, 12, 300 );
		var pts = new List<Vector4>( n );
		for ( int i = 0; i < n; i++ )
		{
			float t = (i / (float)n) * (2f * MathF.PI);
			var p = R * (MathF.Cos( t ) * u + MathF.Sin( t ) * v);
			pts.Add( new Vector4( p.x, p.y, p.z, SeamRadius ) );
		}
		return SeamBrush( pts, R, closed: true );
	}

	// The tennis-ball "baseball curve": x = a·cos t + b·cos 3t, y = a·sin t − b·sin 3t,
	// z = 2·sqrt(ab)·sin 2t with a + b = 1, so it lies exactly on the sphere. SeamShape = a
	// (lower = more exaggerated weave).
	SdfBrush BaseballCurve( float R )
	{
		float a = SeamShape, b = 1f - a, c = 2f * MathF.Sqrt( MathF.Max( a * b, 0f ) );
		float tw = SeamTwist * (MathF.PI / 180f);
		float cw = MathF.Cos( tw ), sw = MathF.Sin( tw );
		var rotY = Rotation.FromAxis( new Vector3( 0f, 1f, 0f ), PatternYRotation ); // orients only this curve
		int n = Math.Clamp( SeamPoints * 2, 16, 300 );
		var pts = new List<Vector4>( n );
		for ( int i = 0; i < n; i++ )
		{
			float t = (i / (float)n) * (2f * MathF.PI);
			float x = a * MathF.Cos( t ) + b * MathF.Cos( 3f * t );
			float y = a * MathF.Sin( t ) - b * MathF.Sin( 3f * t );
			float z = c * MathF.Sin( 2f * t );
			var p = new Vector3( x * cw - y * sw, x * sw + y * cw, z ).Normal * R; // twist about vertical axis
			p = rotY * p;
			pts.Add( new Vector4( p.x, p.y, p.z, SeamRadius ) );
		}
		return SeamBrush( pts, R, closed: true );
	}

	SdfBrush SeamBrush( List<Vector4> pts, float R, bool closed ) => new()
	{
		Shape = SdfShape.Spline,
		Operation = Recessed ? SdfOperation.Subtract : SdfOperation.Add,
		Points = pts,
		SplineClosed = closed,
		Curvature = SeamCurvature,
		Size = new Vector3( R ),
		Blend = SeamBlend,
		Rounding = 0.75f,
		Color = SeamColor,
		Roughness = 1f,
	};
}
