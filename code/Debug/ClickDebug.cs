using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// `mimi_dbg_clicks 1` — logs every screen-UI left click that played its :active sound but never fired onclick,
/// with the reason. The engine (PanelInput.MouseButtonState) only clicks when the panel hit on RELEASE is the
/// exact panel hit on PRESS and no drag started; :active (and its sound) switches on at press, so any of these
/// produce "sound but no click":
///   - drag: a WantsDrag ancestor (draggable row, any scrollable box) saw &gt;5px travel → click cancelled
///   - moved: the release hit a different panel — usually the :active transform (scale/translate) pulled the
///     hit box out from under the cursor, or the cursor slipped off a small target
///   - root: a different root panel (higher z) took the mouse on the release frame
///   - deleted: the pressed panel was rebuilt/deleted mid-press (Razor re-render)
///   - lost: no mouseup reached the UI at all (mouse state flipped UI→Game while held)
/// Listeners hook every PanelComponent's panel; events bubble, so each is deduped by instance.
/// </summary>
public sealed class ClickDebug : GameObjectSystem
{
	[ConVar( "mimi_dbg_clicks", Help = "Log UI presses that played the click sound but never fired onclick." )]
	public static bool Enabled { get; set; }

	readonly HashSet<Panel> _hooked = new();
	PanelEvent _lastEvent;

	Panel _pressed;
	Vector2 _pressPos;
	RealTimeSince _sincePress;
	bool _clicked, _dragged, _released, _waiting;
	Panel _dragTarget;
	Vector2 _releasePos;
	int _releaseFrames;

	public ClickDebug( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Tick, "ClickDebug" );
	}

	void Tick()
	{
		if ( !Enabled || Scene is null || Scene.IsEditor || SdfThumbnail.IsThumbnailScene( Scene ) )
			return;

		_hooked.RemoveWhere( p => !p.IsValid() );
		foreach ( var pc in Scene.GetAllComponents<PanelComponent>() )
		{
			var p = pc.Panel;
			if ( p is null || !_hooked.Add( p ) ) continue;
			p.AddEventListener( "onmousedown", OnDown );
			p.AddEventListener( "onmouseup", OnUp );
			p.AddEventListener( "onclick", OnClickEvt );
			p.AddEventListener( "ondragstart", OnDragStart );
		}

		// Verdict a frame after release — onclick is queued right behind onmouseup.
		if ( _released && ++_releaseFrames >= 2 )
		{
			_released = false;
			if ( !_clicked ) Diagnose();
		}

		if ( _waiting && !_released && _sincePress > 3f )
		{
			_waiting = false;
			Log.Warning( $"[clicks] LOST: pressed {Describe( _pressed )} {_sincePress.Relative:0.0}s ago, no mouseup ever reached the UI (mouse state left UI while held?)" );
		}
	}

	bool Fresh( PanelEvent e )
	{
		if ( ReferenceEquals( e, _lastEvent ) ) return false;
		_lastEvent = e;
		return true;
	}

	static bool IsLeft( PanelEvent e ) => e is MousePanelEvent m && m.Button == "mouseleft";

	void OnDown( PanelEvent e )
	{
		if ( !Fresh( e ) || !IsLeft( e ) ) return;
		_pressed = e.Target;
		_pressPos = Mouse.Position;
		_sincePress = 0;
		_clicked = _dragged = _released = false;
		_dragTarget = null;
		_waiting = true;
	}

	void OnUp( PanelEvent e )
	{
		if ( !Fresh( e ) || !IsLeft( e ) || !_waiting ) return;
		_waiting = false;
		_released = true;
		_releaseFrames = 0;
		_releasePos = Mouse.Position;
	}

	void OnClickEvt( PanelEvent e )
	{
		if ( !Fresh( e ) ) return;
		_clicked = true;
	}

	void OnDragStart( PanelEvent e )
	{
		if ( !Fresh( e ) ) return;
		_dragged = true;
		_dragTarget = e.Target;
	}

	void Diagnose()
	{
		var travel = (_releasePos - _pressPos).Length;
		var head = $"[clicks] MISSED {Describe( _pressed )} (held {_sincePress.Relative * 1000:0}ms, travel {travel:0.0}px)";

		if ( _dragged )
		{
			Log.Warning( $"{head} — DRAG: ancestor {Describe( _dragTarget )} is a drag target and >5px of travel started a drag, which cancels the click" );
			return;
		}

		if ( _pressed is not { IsValid: true, IsDeleting: false } )
		{
			Log.Warning( $"{head} — DELETED: the pressed panel was deleted/rebuilt during the press" );
			return;
		}

		// Re-run the engine's question: which root takes the mouse, and which panel in it is under the cursor.
		var roots = _hooked.Where( p => p.IsValid() ).Select( RootOf ).Distinct()
			.OrderByDescending( r => r.ComputedStyle?.ZIndex ?? 0 ).ToList();
		Panel winner = null, winnerRoot = null;
		foreach ( var r in roots )
		{
			winner = HitTest( r, _releasePos );
			if ( winner is not null ) { winnerRoot = r; break; }
		}

		var pressedRoot = RootOf( _pressed );
		if ( winnerRoot is not null && winnerRoot != pressedRoot )
		{
			Log.Warning( $"{head} — ROOT: release went to another root panel ({Describe( winner )}); only one root gets the mouse per frame" );
			return;
		}

		var untransformed = _pressed.IsInside( _releasePos );
		var why = untransformed
			? "cursor is inside its layout box but NOT its transformed hit box — the :active/:hover transform moved it"
			: "cursor ended outside the pressed panel";
		Log.Warning( $"{head} — MOVED: release hit {Describe( winner )}; {why}" );
	}

	static Panel RootOf( Panel p )
	{
		while ( p.Parent is not null ) p = p.Parent;
		return p;
	}

	/// <summary>Same walk as PanelInput.CheckHover (minus render-order ties): deepest pointer-events panel under pos.</summary>
	static Panel HitTest( Panel p, Vector2 pos )
	{
		if ( !p.IsVisible || p.ComputedStyle is null ) return null;

		pos = p.GetTransformPosition( pos );
		var inside = p.IsInside( pos );
		Panel found = inside && p.ComputedStyle.PointerEvents != PointerEvents.None ? p : null;

		if ( !inside && (p.ComputedStyle.Overflow ?? OverflowMode.Visible) != OverflowMode.Visible )
			return found;

		foreach ( var c in p.Children )
		{
			var h = HitTest( c, pos );
			if ( h is not null ) found = h;
		}
		return found;
	}

	static string Describe( Panel p )
	{
		if ( p is null ) return "<nothing>";
		var parts = new List<string>();
		for ( var q = p; q is not null && parts.Count < 4; q = q.Parent )
		{
			var cls = string.Join( ".", q.Class );
			parts.Add( string.IsNullOrEmpty( cls ) ? q.ElementName : $"{q.ElementName}.{cls}" );
		}
		parts.Reverse();
		return string.Join( " > ", parts );
	}
}
