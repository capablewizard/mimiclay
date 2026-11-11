using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// A damped spring on one value — the HUD's one "bouncy" motion. Underdamped, so it overshoots and settles;
/// continuous through a target change mid-flight (it carries its velocity), which a CSS transition curve can't do —
/// and s&amp;box CSS has no overshoot easing anyway.
/// </summary>
public struct UiSpring
{
	public float Value;
	public float Velocity;

	public UiSpring( float value )
	{
		Value = value;
		Velocity = 0f;
	}

	/// <param name="frequency">Natural frequency, Hz — higher is snappier.</param>
	/// <param name="damping">Damping ratio — 1 = no overshoot, lower = bouncier.</param>
	public void Step( float target, float dt, float frequency, float damping )
	{
		var w = MathF.Tau * frequency;
		// Sub-step so a long frame (a hitch, a slow client) can't blow the integration up.
		var steps = Math.Clamp( (int)MathF.Ceiling( dt / 0.008f ), 1, 8 );
		var h = MathF.Min( dt, 0.1f ) / steps;
		for ( var i = 0; i < steps; i++ )
		{
			var accel = w * w * (target - Value) - 2f * damping * w * Velocity;
			Velocity += accel * h;
			Value += Velocity * h;
		}
	}

	public bool SettledAt( float target ) => MathF.Abs( Value - target ) < 0.002f && MathF.Abs( Velocity ) < 0.01f;
}

/// <summary>
/// Pops the items of a flex row in and out, and slides their neighbours to make / close the room — the shared motion
/// for every HUD row whose buttons come and go (the hotkey bar, the edit HUD's slider strip and shape dock).
///
/// The row's markup keeps an item MOUNTED while it leaves (a razor-deleted panel is parked at the row's end, so it
/// can't animate in place): an item is any child with the class <c>pop</c>, and it's on its way out while it also
/// has <c>gone</c>. Each item has ONE presence spring (0 = gone, 1 = here; bouncy on the way in, a touch firmer on the
/// way out) and everything is driven off it, so nothing can drift apart:
///   • the POP — the item's scale (or its first child's, with <c>scaleChild</c>: the hotkey bar, whose slots carry the
///     clay wobble in their own transform);
///   • its ROOM — while in motion every item holds a fixed width (a fixed item its natural size; a <c>grow</c> item —
///     one that shares the row's free space at rest — the share it will settle at, or had) and negative side margins
///     pull its footprint in to width x presence. So the room and the visible size always match. At rest the
///     stylesheet owns the size again. The row's spacing is the items' side padding (inside their boxes), never a
///     row `gap`: a gap stays behind a collapsed item and snaps shut.
/// (They used to be two springs — the scale hit 0 well before the room closed, so a card vanished and THEN its gap
/// shut, which read as a snap.) With <c>rise</c> the item doesn't take or give room at all: it pops UP from below
/// that many px (the shape dock's tiles, which all come and go together), and shrinks in place on the way out.
///
/// A brand-new item starts gone (stylesheets hold it out of the flow for its first layout with <c>.pop:intro</c>), so
/// mounting is an entrance too. Fully gone = display: none — out of the layout and, crucially, the hit test.
///
/// Every field is lazily created: initializers never run on a hotloaded live instance.
/// </summary>
public sealed class PopFlex
{
	sealed class Item
	{
		public UiSpring Presence;
		public bool Want;
		public float Natural; // the item's width at rest, padding included (style px) — measured whenever it's settled in
		public float LeaveOffset; // rise mode: where it was when it started leaving — it shrinks there, never sinks
		public float Room; // the footprint factor actually applied: one-way per transition (see Apply)
		public bool Live; // seen this frame
	}

	/// <summary>Debug: log every item's spring for this many row-steps (shared across rows) — <c>mimi_pop_trace</c>.</summary>
	public static int TraceFrames;

	[ConCmd( "mimi_pop_trace" )]
	static void Trace( int frames = 60 ) => TraceFrames = frames;

	/// <summary>Every item sat at rest (fully in or fully gone) as of the last Step — the row's layout is its real one,
	/// not a mid-pop one, so it's safe for other rows to measure it.</summary>
	public bool IsSettled { get; private set; } = true;

	Dictionary<Panel, Item> _items;
	List<Panel> _dead;
	RealTimeSince _sinceStep;
	bool _stepped;

	// In: overshoots ~15% and settles in ~0.45s. Out: a firmer, quicker fall (an underdamped exit just re-swells).
	const float InHz = 2.4f, InDamping = 0.55f;
	const float OutHz = 2.6f, OutDamping = 0.8f;

	/// <param name="row">The row whose <c>pop</c> children to drive.</param>
	/// <param name="pad">Unused (kept for callers): the spacing is the stylesheet's side padding, inside each item's box.</param>
	/// <param name="scaleChild">Scale each item's first child instead of the item.</param>
	/// <param name="rise">Pop up vertically from this many px below, taking no room (0 = the room mode).</param>
	public void Step( Panel row, float pad, bool scaleChild = false, float rise = 0f )
	{
		_items ??= new();
		_dead ??= new();
		if ( row is not { IsValid: true } )
			return;

		var dt = _stepped ? Math.Clamp( (float)_sinceStep, 0f, 0.1f ) : 0f;
		_sinceStep = 0f;
		_stepped = true;

		foreach ( var it in _items.Values )
			it.Live = false;

		// Pass 1: wants, and the width a `grow` item will SETTLE at — the row's width less the fixed items that will be
		// in it, shared by the grow items that will be in it (flex-basis 0, equal padding: equal border-box shares).
		var fixedSum = 0f;
		var growCount = 0;
		var unmeasured = false;
		foreach ( var child in row.Children )
		{
			if ( !child.HasClass( "pop" ) || child.IsDeleting )
				continue;
			if ( !_items.TryGetValue( child, out var it ) )
				_items[child] = it = new Item(); // new: starts gone, on its way to wherever it should be
			var want = !child.HasClass( "gone" );
			if ( it.Want && !want )
				it.LeaveOffset = rise * (1f - Math.Clamp( it.Presence.Value, 0f, 1f ));
			it.Want = want;
			it.Live = true;
			if ( !want )
				continue;
			if ( child.HasClass( "grow" ) )
				growCount++;
			else if ( it.Natural > 0f )
				fixedSum += it.Natural;
			else if ( rise <= 0f )
				unmeasured = true;
		}
		var growShare = growCount > 0 ? MathF.Max( 0f, (row.Box.Rect.Width * row.ScaleFromScreen - fixedSum) / growCount ) : 0f;
		// A fixed item arriving for the first time has no width yet (it's measured out of the flow on its first layout),
		// so the grow share above is wrong for that one frame — the sliders came in at 373px and snapped to 320. Hold
		// the row's arrivals for that frame; nobody sees a one-frame-later start.
		var holdArrivals = unmeasured && growCount > 0;

		var index = 0;
		foreach ( var child in row.Children )
		{
			if ( !child.HasClass( "pop" ) || child.IsDeleting || !_items.TryGetValue( child, out var it ) )
				continue;

			var target = it.Want ? 1f : 0f;
			if ( it.Want )
				it.Presence.Step( 1f, holdArrivals ? 0f : dt, InHz, InDamping );
			else
				it.Presence.Step( 0f, dt, OutHz, OutDamping );
			if ( it.Presence.Value < 0f ) { it.Presence.Value = 0f; it.Presence.Velocity = 0f; }

			Apply( child, it, scaleChild, rise, target, growShare );

			if ( TraceFrames > 0 )
				Log.Info( $"[pop] {string.Concat( row.Classes.Take( 12 ) )} #{index} want={it.Want} p={it.Presence.Value:0.00} w={child.Box.Rect.Width * child.ScaleFromScreen:0} h={child.Box.Rect.Height * child.ScaleFromScreen:0} rowH={row.Box.Rect.Height * row.ScaleFromScreen:0}" );
			index++;
		}
		if ( TraceFrames > 0 && _items.Count > 0 )
			TraceFrames--;

		var settled = true;
		foreach ( var it in _items.Values )
			if ( it.Live && !(it.Want ? it.Presence.SettledAt( 1f ) : it.Presence.Value <= 0.002f) )
				settled = false;
		IsSettled = settled;

		// Forget panels that have gone.
		_dead.Clear();
		foreach ( var (panel, it) in _items )
			if ( !it.Live || !panel.IsValid() )
				_dead.Add( panel );
		foreach ( var p in _dead )
			_items.Remove( p );
	}

	static void Apply( Panel child, Item it, bool scaleChild, float rise, float target, float growShare )
	{
		var p = it.Presence.Value;
		var settledOpen = target >= 1f && it.Presence.SettledAt( 1f );

		// Fully gone: out of the layout AND the hit test. A scale-0 panel still hit-tested — a hidden text row's
		// TextEntry (pointer-events: all on itself, so a `gone` wrapper's none didn't reach it) took every click in
		// edit mode and showed the text cursor. Remembered Natural carries over to the next opening.
		var closed = target <= 0f && p <= 0.002f;
		child.Style.Display = closed ? DisplayMode.None : null;
		if ( closed )
		{
			child.Style.Dirty();
			return;
		}

		var measured = child.Box.Rect.Width * child.ScaleFromScreen;
		var grow = child.HasClass( "grow" );
		if ( settledOpen && measured > 0f )
			it.Natural = measured;
		else if ( !grow && measured > 0f )
			it.Natural = MathF.Max( it.Natural, measured ); // fixed: its own size, whatever the room

		// The room never bounces — only the pop (scale) does. It's clamped to [0, 1] (a row near its width limit — the
		// edit hotkey bar — wrapped onto a second line while returning cards swelled past their natural width, lifting
		// every row stacked on it) AND it only moves one way per transition: up while arriving, down while leaving.
		// The spring dips back under 1 after its overshoot; following that dip, an arriving slider gave up ~10px just
		// before settling, its neighbour widened into it and everything slid over and back.
		var raw = Math.Clamp( p, 0f, 1f );
		it.Room = it.Want ? MathF.Max( it.Room, raw ) : MathF.Min( it.Room, raw );
		var room = it.Room;
		if ( rise > 0f )
		{
			// Vertical pop: the room is never touched.
		}
		else if ( settledOpen )
		{
			// At rest: the stylesheet owns the size again (a grow item's flex share, a fixed item's own width).
			child.Style.Position = null;
			child.Style.Width = null;
			child.Style.FlexBasis = null;
			child.Style.FlexGrow = null;
			child.Style.FlexShrink = null;
			child.Style.MarginLeft = null;
			child.Style.MarginRight = null;
		}
		else
		{
			// In motion, EVERY item holds a fixed width and negative side margins pull its footprint in (centred, so it
			// pops from its middle) — footprint = width x room, the same factor as its scale, so what you see and the
			// room it takes always match. Never by narrowing the box: content squeezed into a narrow box measures
			// squeezed (a hotkey card read ~8px while opening, then jumped to its real width at the end).
			// A grow item's width is the share it will settle at (arriving) or had at rest (leaving) — easing its flex
			// share instead shrank it twice over (share x scale), so it vanished while still holding empty room and its
			// neighbour kept sliding after it was gone. A max-width still caps it, so the margins follow the measured box.
			// A fixed item that has never been laid out has nothing to measure: held out of the flow (absolute) for
			// that one frame so it can't flash open.
			float width;
			if ( grow )
			{
				width = it.Want && growShare > 0f ? growShare : (it.Natural > 0f ? it.Natural : growShare);
				child.Style.Width = Length.Pixels( width );
				child.Style.FlexBasis = Length.Pixels( width ); // the stylesheet's flex-basis: 0 beats width in a flex row
				child.Style.FlexGrow = 0f;
				child.Style.FlexShrink = 0f;
				if ( measured > 0f )
					width = MathF.Min( width, measured );
			}
			else
			{
				width = it.Natural;
				child.Style.Position = width > 0f ? null : PositionMode.Absolute;
			}
			var pull = Length.Pixels( -width * (1f - room) * 0.5f );
			child.Style.MarginLeft = pull;
			child.Style.MarginRight = pull;
		}

		var scaled = scaleChild ? (child.ChildrenCount > 0 ? child.GetChild( 0 ) : null) : child;
		if ( scaled is not null )
		{
			if ( settledOpen )
			{
				scaled.Style.Transform = null;
			}
			else
			{
				var s = MathF.Max( p, 0.001f ); // never a singular transform
				var t = new PanelTransform();
				if ( rise > 0f )
				{
					// Arriving: rises from below as it grows. Leaving: shrinks where it is (it used to run the rise
					// backwards and sink into the row beneath).
					var y = it.Want ? rise * (1f - MathF.Min( p, 1f )) : it.LeaveOffset;
					t.AddTranslateY( Length.Pixels( y ) );
				}
				t.AddScale( new Vector3( s, s, 1f ) );
				scaled.Style.Transform = t;
			}
		}
		child.Style.Dirty();
	}
}
