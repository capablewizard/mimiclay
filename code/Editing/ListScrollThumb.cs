using System;
using Sandbox;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// A grab-able scroll thumb for a scrolling list: one small rounded square that rides the list's right edge, its
/// height in the track showing how far down the list is, and dragging it scrolls. Used instead of the engine's
/// <c>scrollbar</c> because that thumb's length is set inline from the content ratio — the HUD wants a fixed square.
/// Hidden (the <c>shown</c> class off) while the list fits. Place it as a sibling of the list inside a
/// <c>position: relative</c> wrapper; it positions itself against the list's own rect.
/// </summary>
public sealed class ListScrollThumb : Panel
{
	/// <summary>The scrolling panel this thumb drives (its <c>overflow-y</c> must be <c>scroll</c>).</summary>
	public Panel List { get; set; }

	bool _dragging;
	float _grabMouseY;   // the wrapper's mouse Y when the drag began (screen px)
	float _grabOffset;   // the list's scroll offset then (screen px)
	float _laidTop = float.NaN;

	public ListScrollThumb()
	{
		AddClass( "list-scroll-thumb" );
		AddClass( "paper-card" ); // the rows' own surface — fill, soft outline, float shadow
	}

	/// <summary>Is the thumb being dragged right now?</summary>
	public bool Dragging => _dragging;

	// Hover-out spring, like the layer rows': leaving the thumb adds .unhover-a / .unhover-b (alternating, so a quick
	// re-leave restarts the animation) for a moment, which springs it back down from the hover grow.
	bool _unhoverB;
	RealTimeSince _sinceUnhover = 999f;
	const float UnhoverWindow = 0.6f;

	protected override void OnMouseOut( MousePanelEvent e )
	{
		base.OnMouseOut( e );
		if ( _dragging )
			return; // the drag-capture overlay took the hover; the grab keeps it grown until release
		_unhoverB = !_unhoverB;
		_sinceUnhover = 0f;
	}

	public override void Tick()
	{
		base.Tick();
		FollowDrag();

		var list = List;
		bool show = list.IsValid() && list.HasScrollY && list.ScrollSize.y > 0.5f;
		SetClass( "shown", show );
		SetClass( "dragging", _dragging );
		bool springing = !_dragging && _sinceUnhover < UnhoverWindow;
		SetClass( "unhover-a", springing && !_unhoverB );
		SetClass( "unhover-b", springing && _unhoverB );
		if ( !show || Parent is null )
			return;

		// Everything in screen px (Box rects, scroll offsets), converted to style px only for the write.
		float progress = Math.Clamp( list.ScrollOffset.y / list.ScrollSize.y, 0f, 1f );
		var (from, to) = Travel( list );
		float top = from + (to - from) * progress - Parent.Box.Rect.Top;
		float styleTop = MathF.Round( top * ScaleFromScreen, 1 );
		if ( styleTop != _laidTop )
		{
			_laidTop = styleTop;
			Style.Top = Length.Pixels( styleTop );
		}
	}

	// The thumb's screen-space top at the two ends of the scroll: centred on the first row when scrolled to the top,
	// on the last row at the bottom — so it reads as pointing at the row on screen at that end. Rows sit inside the
	// list's padding (its inner rect); a row is measured off the list's first child.
	(float From, float To) Travel( Panel list )
	{
		var inner = list.Box.RectInner;
		float thumb = Box.Rect.Height;
		float row = list.ChildrenCount > 0 ? list.GetChild( 0 ).Box.Rect.Height : thumb;
		float inset = (row - thumb) * 0.5f;
		float from = inner.Top + inset;
		float to = MathF.Max( inner.Bottom - row + inset, from );
		return (from, to);
	}

	// A press, then the mouse followed by hand every Tick until the button comes up — NOT the engine's panel drag
	// (WantsDrag / OnDrag): in the editor a live panel drag was handed to the editor's own drag handling, which cost
	// 50–230 ms a frame (profiled 2026-11-04, "Editor" category) — a fast thumb drag crawled.
	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.MouseButton != MouseButtons.Left || !List.IsValid() || Parent is null )
			return;
		_dragging = true;
		_grabMouseY = Parent.MousePosition.y;
		_grabOffset = List.ScrollOffset.y;
		List.ScrollTo( List.ScrollOffset ); // stops any wheel momentum, like the engine's own thumb
		e.StopPropagation();
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		if ( e.MouseButton == MouseButtons.Left && _dragging )
		{
			_dragging = false;
			_unhoverB = !_unhoverB; // let go: spring back down from the held grow
			_sinceUnhover = 0f;
		}
	}

	// Per Tick while held: follow the mouse. The release always reaches us (PanelInput sends onmouseup to the panel that
	// was PRESSED, wherever the cursor is) — not Input.Down("Attack1"), which a hovered UI panel swallows.
	void FollowDrag()
	{
		var list = List;
		if ( !_dragging )
			return;
		if ( !list.IsValid() || Parent is null )
		{
			_dragging = false;
			return;
		}

		// Thumb travel ↔ scroll range: moving the thumb its whole travel scrolls the whole list.
		var (from, to) = Travel( list );
		float travel = to - from;
		if ( travel <= 0f )
			return;
		float offset = _grabOffset + (Parent.MousePosition.y - _grabMouseY) * list.ScrollSize.y / travel;
		list.ScrollTo( new Vector2( list.ScrollOffset.x, Math.Clamp( offset, 0f, list.ScrollSize.y ) ) );
	}
}
