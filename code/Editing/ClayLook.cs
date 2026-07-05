using System;
using System.Globalization;

namespace Mimiclay;

/// <summary>
/// Shared "clay look" colour maths — the single place that turns a clay colour + metalness into the CSS a
/// swatch or layer-row icon uses. Both the floating palette (EditHud) and the layer-row icon (LayerRow)
/// build their fill from here, so a metallic material reads the same diagonal dark→light→dark sheen
/// everywhere. Tweak the look in one spot.
/// </summary>
public static class ClayLook
{
	// Metallic sheen defaults (the palette can override these with its own inspector tunables; the layer
	// row uses these as-is). Mirrors the values the palette swatches shipped with.
	public const float MetalThreshold = 0.5f;  // metalness above this gets the sheen
	public const float MetalDarken = 0.35f;     // how much darker the gradient's dark stops are
	public const float MetalSaturate = 0.4f;    // how much more saturated the dark stops are
	public const float MetalSheen = 0.3f;       // how much lighter the central sheen stop is
	public const float MetalAngle = 135f;       // diagonal angle of the sheen, in degrees

	public static string Hex( Color c ) => $"rgba({(int)(c.r * 255)}, {(int)(c.g * 255)}, {(int)(c.b * 255)}, 1)";

	public static Color Darken( Color c, float amt ) => new( c.r * (1f - amt), c.g * (1f - amt), c.b * (1f - amt), c.a );

	public static Color Lighten( Color c, float amt ) => new( c.r + (1f - c.r) * amt, c.g + (1f - c.g) * amt, c.b + (1f - c.b) * amt, c.a );

	public static Color Saturate( Color c, float amt )
	{
		float g = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f; // luma
		return new Color(
			Math.Clamp( g + (c.r - g) * (1f + amt), 0f, 1f ),
			Math.Clamp( g + (c.g - g) * (1f + amt), 0f, 1f ),
			Math.Clamp( g + (c.b - g) * (1f + amt), 0f, 1f ),
			c.a );
	}

	/// <summary>True if this metalness should render with the metallic sheen rather than a flat colour.</summary>
	public static bool IsMetallic( float metalness, float threshold = MetalThreshold ) => metalness > threshold;

	/// <summary>HSV → RGB (hue in degrees, any range; sat/val 0..1). Alpha comes out 1.</summary>
	public static Color HsvToRgb( float hue, float sat, float val )
	{
		hue = ((hue % 360f) + 360f) % 360f;
		float c = val * sat;
		float x = c * (1f - MathF.Abs( (hue / 60f) % 2f - 1f ));
		float m = val - c;
		var (r, g, b) = (hue / 60f) switch
		{
			< 1f => (c, x, 0f),
			< 2f => (x, c, 0f),
			< 3f => (0f, c, x),
			< 4f => (0f, x, c),
			< 5f => (x, 0f, c),
			_ => (c, 0f, x),
		};
		return new Color( r + m, g + m, b + m );
	}

	/// <summary>RGB → HSV (hue in degrees 0..360, sat/val 0..1). Hue/sat are 0 where they're undefined
	/// (greys / black) — callers that need continuity should keep their previous values there.</summary>
	public static (float Hue, float Sat, float Val) RgbToHsv( Color c )
	{
		float max = MathF.Max( c.r, MathF.Max( c.g, c.b ) );
		float min = MathF.Min( c.r, MathF.Min( c.g, c.b ) );
		float d = max - min;

		float h = 0f;
		if ( d > 1e-5f )
		{
			if ( max == c.r ) h = 60f * (((c.g - c.b) / d) % 6f);
			else if ( max == c.g ) h = 60f * ((c.b - c.r) / d + 2f);
			else h = 60f * ((c.r - c.g) / d + 4f);
			if ( h < 0f ) h += 360f;
		}

		float s = max <= 1e-5f ? 0f : d / max;
		return (h, s, max);
	}

	/// <summary>
	/// The diagonal dark→light→dark sheen as a CSS <c>linear-gradient(...)</c> value (no property name), built
	/// from a base colour. Use as a <c>background-image</c> or <c>mask</c>'d overlay.
	/// </summary>
	public static string MetalGradient( Color color,
		float darken = MetalDarken, float saturate = MetalSaturate, float sheen = MetalSheen, float angle = MetalAngle )
	{
		var dark = Hex( Saturate( Darken( color, darken ), saturate ) );
		var light = Hex( Lighten( color, sheen ) );
		return $"linear-gradient({(int)angle}deg, {dark}, {light}, {dark})";
	}
}
