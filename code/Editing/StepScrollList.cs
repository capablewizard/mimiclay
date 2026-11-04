using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// Drives a plain <c>overflow-y: scroll</c> panel (the edit HUD's shape list) for the two things the engine gets
/// wrong for it — owned and ticked by the HUD, because a custom Panel class can't hold the rows (child markup inside
/// a C# or .razor panel tag failed the editor's razor compile: "'StepScrollList' does not contain a definition for 'div'").
/// <list type="bullet">
/// <item>The WHEEL steps one row per notch, eased. The engine adds velocity per notch and compounds it while the list
/// is still moving (<c>×(1 + speed/100)</c>), so a quick spin ran away down a long list. Each frame the velocity a notch
/// left is read (20 per notch, see Panel.TryScroll), turned into whole rows and zeroed.</item>
/// <item>The EDGE FADES (more list above / below) come from cached mask textures. Writing a fresh
/// <c>linear-gradient(...)</c> as the scroll moved made the engine build and upload a new GPU texture every frame —
/// scrolling quickly lagged badly. Each fade level is drawn once, at the list's current height, and reused.</item>
/// </list>
/// Each fade eases in over the first <see cref="FadeSize"/> of scrolling (and out into the end), so neither pops.
/// The panel needs <c>mask-mode: luminance; mask-size: 100% 100%; mask-repeat: clamp</c>.
/// </summary>
public sealed class StepScrollList
{
	/// <summary>How tall (style px) each edge's fade is, and how far you scroll for it to come fully in.</summary>
	public const float FadeSize = 90f;

	const float WheelEase = 18f;   // 1/s — how quickly a wheel step glides to its row
	const float NotchVelocity = 20f; // what one notch adds to ScrollVelocity (Panel.TryScroll, from rest)
	const int FadeLevels = 12;     // fade strength steps per edge (textures cached per pair)

	float _wheelTarget;
	bool _wheeling;
	float _lastGlide = float.NaN;

	// Fade textures for the current list height, keyed by (top level, bottom level). Rebuilt when the height changes.
	readonly Dictionary<int, Texture> _fades = new();
	int _fadeHeight = -1;
	int _fadeKey = -1;
	Panel _fadeOn;

	static bool Scrollable( Panel list ) => list.IsValid() && list.HasScrollY && list.ScrollSize.y > 0.5f;

	/// <summary>Per frame, from the HUD's update.</summary>
	/// <param name="maskOn">The panel that carries the fade mask — a padding-free wrapper the size of the list (a masked
	/// panel draws through a layer clipped at its CONTENT box, so masking the padded list itself cut off the rows' hover
	/// grow and shadow). Null = the list.</param>
	public void Update( Panel list, Panel maskOn = null )
	{
		if ( !list.IsValid() )
			return;
		if ( !maskOn.IsValid() )
			maskOn = list;

		// Scrollable: the whole list takes the pointer (`.scrollable` in the sheet), gaps between rows included — a wheel
		// in a gap scrolls the list instead of falling through to the editor and pushing the selected shape away. A
		// short list stays click-through, so the empty column under it still reaches the world.
		bool scrollable = Scrollable( list );
		list.SetClass( "scrollable", scrollable );

		// No touch-style drag scrolling: a press-and-drag in a gap between rows would grab the list and fling it on
		// release (a big velocity UpdateWheel would then read as a long wheel spin). Rows drag to reorder; the thumb
		// and the wheel scroll.
		list.CanDragScroll = false;

		UpdateWheel( list, scrollable );
		UpdateFades( list, maskOn, scrollable );
	}

	/// <summary>Stop a wheel glide — another control (the thumb, a drag's edge scroll) is moving the list.</summary>
	public void StopWheel() => _wheeling = false;

	/// <summary>Glide the list (the wheel's easing) the least distance that puts <paramref name="row"/> fully on screen,
	/// clear of the edge fades — a shape picked in the viewport scrolls its row into view. No-op when it's already clear.</summary>
	public void Reveal( Panel list, Panel row )
	{
		if ( !Scrollable( list ) || !row.IsValid() )
			return;

		var inner = list.Box.RectInner;
		var r = row.Box.Rect;
		float clear = FadeSize * list.ScaleToScreen; // keep it out of the fade band
		float now = _wheeling ? _wheelTarget : list.ScrollOffset.y;
		float target = now;
		if ( r.Top < inner.Top + clear )
			target = now - (inner.Top + clear - r.Top);
		else if ( r.Bottom > inner.Bottom - clear )
			target = now + (r.Bottom - (inner.Bottom - clear));
		target = Math.Clamp( target, 0f, list.ScrollSize.y );
		if ( MathF.Abs( target - list.ScrollOffset.y ) < 0.5f )
			return;

		_wheelTarget = target;
		_wheeling = true;
		_lastGlide = list.ScrollOffset.y; // the glide starts from here (not an outside scroll)
	}

	void UpdateWheel( Panel list, bool scrollable )
	{
		if ( !scrollable )
		{
			_wheeling = false;
			return;
		}

		// Something else moved it since our last glide step (the thumb, a drag's edge scroll): that wins.
		if ( _wheeling && !float.IsNaN( _lastGlide ) && MathF.Abs( list.ScrollOffset.y - _lastGlide ) > 1f )
			_wheeling = false;

		float v = list.ScrollVelocity.y;
		if ( MathF.Abs( v ) > 0.5f )
		{
			int notches = Math.Max( 1, (int)MathF.Round( MathF.Abs( v ) / NotchVelocity ) ) * Math.Sign( v );
			if ( !_wheeling )
				_wheelTarget = list.ScrollOffset.y;
			_wheelTarget = Math.Clamp( _wheelTarget + notches * RowStride( list ), 0f, list.ScrollSize.y );
			_wheeling = true;
			list.ScrollVelocity = Vector2.Zero; // none of the engine's own momentum on top
		}

		if ( !_wheeling )
		{
			_lastGlide = float.NaN;
			return;
		}

		float now = list.ScrollOffset.y;
		float next = MathX.Lerp( now, _wheelTarget, 1f - MathF.Exp( -WheelEase * Time.Delta ) );
		if ( MathF.Abs( _wheelTarget - next ) < 0.5f )
		{
			next = _wheelTarget;
			_wheeling = false;
		}
		list.ScrollTo( new Vector2( list.ScrollOffset.x, next ) );
		_lastGlide = next;
	}

	// One row's step (screen px): row-top to row-top, gap included.
	static float RowStride( Panel list )
	{
		if ( list.ChildrenCount >= 2 )
		{
			float d = list.GetChild( 1 ).Box.Rect.Top - list.GetChild( 0 ).Box.Rect.Top;
			if ( d > 1f )
				return d;
		}
		return 60f * list.ScaleToScreen;
	}

	void UpdateFades( Panel list, Panel maskOn, bool scrollable )
	{
		int h = (int)MathF.Round( maskOn.Box.Rect.Height );
		int key = -1;
		if ( scrollable && h > 2 )
		{
			float fade = FadeSize * list.ScaleToScreen;
			int top = (int)MathF.Round( Math.Clamp( list.ScrollOffset.y / fade, 0f, 1f ) * FadeLevels );
			int bottom = (int)MathF.Round( Math.Clamp( (list.ScrollSize.y - list.ScrollOffset.y) / fade, 0f, 1f ) * FadeLevels );
			if ( top > 0 || bottom > 0 )
				key = top * (FadeLevels + 1) + bottom;
		}

		if ( key == _fadeKey && h == _fadeHeight && maskOn == _fadeOn )
			return;
		_fadeOn = maskOn; // a fresh panel (edit mode reopened) needs it written again

		if ( h != _fadeHeight )
		{
			Clear();
			_fadeHeight = h;
		}
		_fadeKey = key;

		if ( key < 0 )
		{
			maskOn.Style.MaskImage = null;
			return;
		}

		if ( !_fades.TryGetValue( key, out var tex ) )
			_fades[key] = tex = BuildFade( list, h, key / (FadeLevels + 1) / (float)FadeLevels, key % (FadeLevels + 1) / (float)FadeLevels );
		maskOn.Style.MaskImage = tex;
	}

	// A 1×h luminance strip: white through the middle, darkening into each edge by that edge's strength (1 = black).
	static Texture BuildFade( Panel list, int h, float top, float bottom )
	{
		float fade = MathF.Min( FadeSize * list.ScaleToScreen, h * 0.45f );
		var data = new byte[h * 4];
		for ( int y = 0; y < h; y++ )
		{
			float v = 1f;
			if ( y < fade )
				v = MathX.Lerp( 1f - top, 1f, y / fade );
			if ( y > h - fade )
				v = MathF.Min( v, MathX.Lerp( 1f, 1f - bottom, (y - (h - fade)) / fade ) );
			byte c = (byte)Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );
			int i = y * 4;
			data[i] = c;
			data[i + 1] = c;
			data[i + 2] = c;
			data[i + 3] = 255;
		}

		return Texture.Create( 1, h ).WithName( "shape-list-fade" ).WithData( data ).Finish();
	}

	/// <summary>Drop the cached fade textures (the HUD going away, or a new list height).</summary>
	public void Clear()
	{
		foreach ( var t in _fades.Values )
			t?.Dispose();
		_fades.Clear();
		_fadeKey = -1;
	}
}
