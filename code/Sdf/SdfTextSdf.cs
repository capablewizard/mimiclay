using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// A baked 2D signed distance field of a rendered string — the Text brush's cross-section profile, the
/// way the analytic 2D SDFs are the extruded shapes'. Distances are in TEXEL units (negative inside a
/// glyph), row 0 = the TOP of the rendered bitmap. PURE DATA (no engine references), so worker threads
/// can sample it freely: the mesher's brush snapshots share the immutable instance by reference.
/// </summary>
public sealed class SdfTextData
{
	/// <summary>Fixed slot resolution — every string is rendered scaled-to-fit this grid, and the GPU atlas
	/// stacks slots of exactly this size. Must match TEXT_SLOT_W/H in the shaders.</summary>
	public const int Width = 256;
	public const int Height = 128;

	/// <summary>Signed distances, Width×Height row-major, texel units, row 0 = bitmap top.</summary>
	public float[] Grid;

	/// <summary>Rendered text width / height — for sizing the brush quad to the string's natural shape.</summary>
	public float ContentAspect = 2f;

	/// <summary>The rendered string's height as a fraction of the slot height (letterboxing leaves margins).
	/// The HUD uses it to keep the LETTERS a constant world height as the string changes: quad world height
	/// × this = letter height.</summary>
	public float ContentHeightFrac = 0.5f;

	/// <summary>The rendered string's width as a fraction of the slot width — the ink rect's other axis, for
	/// tight bounds (<see cref="SdfBrush.TextInkExtents"/>). Stored directly rather than derived from
	/// <see cref="ContentAspect"/>, whose display clamp (12) undershoots on long strings.</summary>
	public float ContentWidthFrac = 0.5f;

	/// <summary>Bilinear sample at normalized (u, v), v = 0 at the bitmap TOP (matching the shaders' flip).
	/// Texel units.</summary>
	public float Sample( float u, float v )
	{
		float x = Math.Clamp( u, 0f, 1f ) * (Width - 1);
		float y = Math.Clamp( v, 0f, 1f ) * (Height - 1);
		int x0 = (int)x, y0 = (int)y;
		int x1 = Math.Min( x0 + 1, Width - 1 ), y1 = Math.Min( y0 + 1, Height - 1 );
		float fx = x - x0, fy = y - y0;

		float a = Grid[y0 * Width + x0], b = Grid[y0 * Width + x1];
		float c = Grid[y1 * Width + x0], d = Grid[y1 * Width + x1];
		return (a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy;
	}
}

/// <summary>
/// Bakes and caches <see cref="SdfTextData"/> per (text, font): Skia-rasterize the string via
/// <see cref="Bitmap.DrawText"/> scaled to fit the slot, threshold to a mask, then an exact Euclidean
/// distance transform (Felzenszwalb two-pass, O(n)) gives the signed field. MAIN THREAD ONLY (Bitmap is
/// engine-side) — the packer and rebuild paths warm the cache before any worker touches a brush snapshot.
/// </summary>
public static class SdfTextSdf
{
	/// <summary>Atlas capacity: text brushes beyond this share the last slot. Must match TEXT_SLOTS in the shaders.</summary>
	public const int MaxSlots = 8;

	/// <summary>Empty border around the rendered string (texels) — room for the outward distance band, and
	/// keeps bilinear samples at slot edges away from the neighbouring slot's rows.</summary>
	const int Pad = 12;

	static readonly Dictionary<(string, string), SdfTextData> _cache = new();

	/// <summary>The baked field for a string+font (cached). Null/empty text gives a valid all-empty field.</summary>
	public static SdfTextData Get( string text, string font )
	{
		// Clay text is ALL CAPS by design (the chunky display fonts read best that way) — normalised here so
		// the typed string keeps its case in the brush/serialisation but bakes (and caches) uppercased.
		text = (text ?? "").ToUpperInvariant();
		font = string.IsNullOrWhiteSpace( font ) ? "Super Joyful" : font; // the theme's display font

		if ( _cache.TryGetValue( (text, font), out var cached ) )
			return cached;

		var data = Bake( text, font );
		_cache[(text, font)] = data;
		return data;
	}

	/// <summary>Supersample factor: the string is rasterized and distance-transformed at SS× the slot
	/// resolution, then the DISTANCES are downsampled. Thresholding a raster bakes its stair-stepped edge
	/// into the field — sampled at 1× that staircase is a whole texel tall and reads as jagged letters;
	/// at 4× the edge positions are accurate to ¼ texel and bilinear reconstruction renders them smooth.</summary>
	const int SS = 4;

	static SdfTextData Bake( string text, string font )
	{
		const int W = SdfTextData.Width, H = SdfTextData.Height;
		const int HW = W * SS, HH = H * SS;
		var data = new SdfTextData { Grid = new float[W * H] };

		// Rasterize white-on-transparent at the supersampled resolution, in TWO passes. DrawText places by
		// font LINE METRICS, not by what's actually inked (all-caps reserves empty descender space; bouncy
		// display fonts overflow their em boxes) — so pass 1 finds where the ink really landed, and pass 2
		// re-draws offset (and shrunk, if the ink overflowed the padded area) so the ink sits CENTRED in the
		// grid with its margins intact. Correcting at raster time matters: correcting only at sample time
		// shifts the window off the grid's data and the border clamp smears the letters nearest that edge.
		bool[] mask = null;
		int minX = 0, minY = 0, maxX = -1, maxY = -1;
		if ( !string.IsNullOrWhiteSpace( text ) )
		{
			var scope = new TextRendering.Scope( text, Color.White, 64f, font );
			var measured = scope.Measure();
			if ( measured.x > 0.5f && measured.y > 0.5f )
			{
				// Measure only drives the initial fit; ink bounds take over from here.
				float fit = MathF.Min( (W - 2 * Pad) / measured.x, (H - 2 * Pad) / measured.y );
				float fontSize = 64f * fit * SS;

				(mask, minX, minY, maxX, maxY) = Raster( scope, fontSize, 0f, 0f, HW, HH );
				if ( maxX >= 0 )
				{
					// Shrink if the ink overflowed the padded area, and offset so the ink centre lands on the
					// grid centre. Ink position scales ~linearly with font size about the rect centre, so the
					// pass-1 offset is pre-scaled by the same factor.
					float inkW = maxX - minX + 1, inkH = maxY - minY + 1;
					float s2 = MathF.Min( 1f, MathF.Min( (W - 2 * Pad) * SS / inkW, (H - 2 * Pad) * SS / inkH ) );
					float offX = ((minX + maxX + 1) * 0.5f - HW * 0.5f) * s2;
					float offY = ((minY + maxY + 1) * 0.5f - HH * 0.5f) * s2;
					(mask, minX, minY, maxX, maxY) = Raster( scope, fontSize * s2, -offX, -offY, HW, HH );
				}
			}
		}

		if ( mask is null || maxX < 0 )
		{
			// Empty / unmeasurable / all-whitespace text: a valid all-outside field (big positive everywhere)
			// so the brush simply contributes nothing instead of breaking the eval.
			Array.Fill( data.Grid, 1e4f );
			return data;
		}

		data.ContentHeightFrac = Math.Clamp( (maxY - minY + 1) / (float)HH, 0.05f, 1f );
		data.ContentWidthFrac = Math.Clamp( (maxX - minX + 1) / (float)HW, 0.05f, 1f );
		data.ContentAspect = Math.Clamp( (maxX - minX + 1) / (float)(maxY - minY + 1), 0.1f, 12f );

		// Signed distance = dist-to-glyph − dist-to-background (each an exact EDT over the mask):
		// positive outside the letters, negative inside. Computed at the supersampled resolution, then
		// point-downsampled from each slot texel's centre and rescaled into slot-texel units. Any RESIDUAL
		// ink-centre error after the pass-2 recentre (a texel or two of layout nonlinearity) is folded into
		// the sample shift — tiny, so it can never push the window off the grid's data.
		var dOut = Edt( mask, HW, HH, inside: true );  // distance to the nearest glyph texel
		var dIn = Edt( mask, HW, HH, inside: false );  // distance to the nearest background texel
		int shiftX = (minX + maxX + 1) / 2 - HW / 2;   // residual ink centre − grid centre, high-res texels
		int shiftY = (minY + maxY + 1) / 2 - HH / 2;
		for ( int y = 0; y < H; y++ )
			for ( int x = 0; x < W; x++ )
			{
				int hx = Math.Clamp( x * SS + SS / 2 + shiftX, 0, HW - 1 );
				int hy = Math.Clamp( y * SS + SS / 2 + shiftY, 0, HH - 1 );
				int hi = hy * HW + hx;
				data.Grid[y * W + x] = (MathF.Sqrt( dOut[hi] ) - MathF.Sqrt( dIn[hi] )) / SS;
			}

		return data;
	}

	// Draw the scope at the given font size into a fresh mask, the rect offset by (ox, oy), and return the
	// thresholded coverage plus the ink's bounding box (maxX = -1 when nothing inked).
	static (bool[] Mask, int MinX, int MinY, int MaxX, int MaxY) Raster(
		TextRendering.Scope scope, float fontSize, float ox, float oy, int hw, int hh )
	{
		scope.FontSize = fontSize;
		using var bmp = new Bitmap( hw, hh, false );
		bmp.Clear( Color.Transparent );
		bmp.DrawText( scope, new Rect( ox, oy, hw, hh ), TextFlag.Center );

		var px = bmp.GetPixels();
		var mask = new bool[hw * hh];
		int minX = hw, minY = hh, maxX = -1, maxY = -1;
		for ( int y = 0; y < hh; y++ )
			for ( int x = 0; x < hw; x++ )
			{
				if ( px[y * hw + x].a <= 0.5f )
					continue;
				mask[y * hw + x] = true;
				if ( x < minX ) minX = x;
				if ( x > maxX ) maxX = x;
				if ( y < minY ) minY = y;
				if ( y > maxY ) maxY = y;
			}
		return (mask, minX, minY, maxX, maxY);
	}

	// Exact squared Euclidean distance transform (Felzenszwahl & Huttenlocher): distance to the nearest
	// texel where mask==inside. Rows first, then columns over the row result.
	static float[] Edt( bool[] mask, int w, int h, bool inside )
	{
		const float INF = 1e12f;
		var g = new float[w * h];

		// Rows: 1D squared distance to the nearest set texel along x.
		var f = new float[Math.Max( w, h )];
		var d = new float[Math.Max( w, h )];
		var v = new int[Math.Max( w, h )];
		var z = new float[Math.Max( w, h ) + 1];

		for ( int y = 0; y < h; y++ )
		{
			for ( int x = 0; x < w; x++ )
				f[x] = mask[y * w + x] == inside ? 0f : INF;
			Edt1D( f, d, v, z, w );
			for ( int x = 0; x < w; x++ )
				g[y * w + x] = d[x];
		}

		// Columns: 1D pass over the row results.
		for ( int x = 0; x < w; x++ )
		{
			for ( int y = 0; y < h; y++ )
				f[y] = g[y * w + x];
			Edt1D( f, d, v, z, h );
			for ( int y = 0; y < h; y++ )
				g[y * w + x] = d[y];
		}

		return g;
	}

	// 1D lower-envelope-of-parabolas squared distance transform.
	static void Edt1D( float[] f, float[] d, int[] v, float[] z, int n )
	{
		int k = 0;
		v[0] = 0;
		z[0] = float.MinValue;
		z[1] = float.MaxValue;

		for ( int q = 1; q < n; q++ )
		{
			float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
			while ( s <= z[k] )
			{
				k--;
				s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
			}
			k++;
			v[k] = q;
			z[k] = s;
			z[k + 1] = float.MaxValue;
		}

		k = 0;
		for ( int q = 0; q < n; q++ )
		{
			while ( z[k + 1] < q )
				k++;
			float dx = q - v[k];
			d[q] = dx * dx + f[v[k]];
		}
	}
}

/// <summary>
/// The GPU side of the text fields: one R32F texture stacking <see cref="SdfTextSdf.MaxSlots"/> slots
/// vertically, bound as "TextSdf" next to BrushData/SplineData. The packer assigns slots in brush order
/// (deterministic, so the renderer's and the field baker's independent atlases agree) and calls
/// <see cref="Set"/>, which only uploads a slot when its content actually changed.
/// </summary>
public sealed class SdfTextAtlas
{
	Texture _tex;
	readonly SdfTextData[] _slots = new SdfTextData[SdfTextSdf.MaxSlots];

	public Texture Texture
	{
		get
		{
			_tex ??= Texture.Create( SdfTextData.Width, SdfTextData.Height * SdfTextSdf.MaxSlots )
				.WithFormat( ImageFormat.R32F ).WithDynamicUsage().Finish();
			return _tex;
		}
	}

	public void Set( int slot, SdfTextData data )
	{
		if ( slot < 0 || slot >= SdfTextSdf.MaxSlots || data?.Grid is null || _slots[slot] == data )
			return;
		_slots[slot] = data;
		Texture.Update<float>( data.Grid, 0, slot * SdfTextData.Height, SdfTextData.Width, SdfTextData.Height );
	}
}
