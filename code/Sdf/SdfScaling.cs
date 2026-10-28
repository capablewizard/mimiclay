using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// The two scaling rules, in one place:
/// <list type="bullet">
/// <item><b>Scaling ONE shape</b> changes its size and nothing else — rounding, blend and gap keep their values
/// (the R scrub and the gizmo's scale handles; nothing here is involved).</item>
/// <item><b>Scaling a GROUP</b> (a multi-selection, a parent of brush objects, a whole carried prop) must leave it
/// mathematically identical apart from its size: every LENGTH a brush holds scales by the same factor —
/// position about the pivot, size, spline points and radii, and the feature lengths rounding, blend and gap. A
/// blend or rounding left at its old value is exactly what made scaled groups look different.</item>
/// </list>
/// Ratios (slice, curvature) and colours aren't lengths and never change. No tool maxima are applied — the
/// sliders' caps (MaxBlend, MaxGap, MaxSplineRadius) are input ranges, not limits of the field, and clamping
/// them would break the "identical" promise; only rounding stays inside the shape's own legal range, which
/// scales with the size anyway. Same geometric-mean convention as <see cref="SdfGroups"/>' member expansion.
/// </summary>
public static class SdfScaling
{
	/// <summary>The uniform factor a (possibly non-uniform) scale applies to feature lengths: the geometric mean
	/// of its axes — what <see cref="SdfGroups"/> uses for a group's members.</summary>
	public static float MeanScale( Vector3 scale )
	{
		float mean = MathF.Cbrt( MathF.Abs( scale.x * scale.y * scale.z ) );
		return mean > 1e-4f ? mean : 1f;
	}

	/// <summary>The pivot a group scale must use to stay identical: <paramref name="desired"/>, but ON the
	/// symmetry plane for every axis any member mirrors. Mirror copies reflect across the sculpture's fixed
	/// planes (x/y/z = 0), so scaling about a point off a plane moves each copy differently from its original —
	/// the plane itself is the only pivot that maps the pair onto a scaled pair. Applying that one pivot to EVERY
	/// member (not per brush) keeps the mirrored and unmirrored members in the same proportion to each other.
	/// Raw toggles, not Effective*: a brush near the plane flipping its deadzone mid-drag must not move the pivot.</summary>
	public static Vector3 SymmetricPivot( IEnumerable<SdfBrush> members, Vector3 desired )
	{
		bool x = false, y = false, z = false;
		foreach ( var b in members )
		{
			x |= b.MirrorX;
			y |= b.MirrorY;
			z |= b.MirrorZ;
		}
		return new Vector3( x ? 0f : desired.x, y ? 0f : desired.y, z ? 0f : desired.z );
	}

	/// <summary>Group scaling of one member: every length scales by <paramref name="factor"/> about
	/// <paramref name="pivot"/> (sculpture-local — from <see cref="SymmetricPivot"/> whenever mirrored brushes are
	/// involved). A spline's geometry is its points, so they move and their radii scale; anything else moves its
	/// position and scales its size.</summary>
	public static void ScaleUniform( SdfBrush b, Vector3 pivot, float factor )
	{
		if ( b.Shape == SdfShape.Spline && b.Points is { Count: > 0 } pts )
		{
			for ( int i = 0; i < pts.Count; i++ )
			{
				var p = pivot + (new Vector3( pts[i].x, pts[i].y, pts[i].z ) - pivot) * factor;
				pts[i] = new Vector4( p.x, p.y, p.z, MathF.Max( pts[i].w * factor, 0.05f ) );
			}
		}
		else
		{
			b.Position = pivot + (b.Position - pivot) * factor;
			b.Size *= factor;
		}

		ScaleFeatures( b, factor );
	}

	/// <summary>Scale only the FEATURE lengths — rounding, blend, gap — by <paramref name="factor"/>. For a brush
	/// whose geometry has already been scaled some other way (a brush object's size comes from its GameObject
	/// scale, see <see cref="SdfBrushComponent.ToBrush"/>).</summary>
	public static void ScaleFeatures( SdfBrush b, float factor )
	{
		if ( MathF.Abs( factor - 1f ) < 1e-5f )
			return;

		b.Blend = MathF.Max( b.Blend * factor, 0f );
		b.Gap = MathF.Max( b.Gap * factor, 0f );
		b.Rounding = Math.Clamp( b.Rounding * factor, SdfBrush.MinRounding, b.MaxRounding() );
	}
}
