using System.Collections.Generic;
using System.Linq;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// Drop-in click diagnostics. Two layers:
///  1) GAME INPUT — logs every mouse button press/release seen by the game (<see cref="Input"/>). If a click
///     is consumed by a UI panel, the game never sees it, so NOTHING prints here — that absence is the tell.
///  2) UI EVENTS — attaches listeners to every <see cref="PanelComponent"/>'s root panel, so it logs UI
///     mousedown/click events EVEN when they're swallowed from game input, naming which panel + element got
///     the click. This is what identifies the panel responsible for a "phantom" click.
///
/// Add it to any GameObject while debugging; remove when done. Self-contained.
/// </summary>
[Title( "Click Debugger" )]
[Category( "Mimiclay/Debug" )]
[Icon( "ads_click" )]
public sealed class ClickDebugger : Component
{
	// Mouse buttons as input actions (the same names the rest of the game uses).
	static readonly string[] Actions = { "Attack1", "Attack2", "CameraPan" };
	static readonly string[] Names = { "LEFT", "RIGHT", "MIDDLE" };
	readonly bool[] _down = new bool[3];

	// Panel roots we've already attached UI listeners to (roots are stable per PanelComponent).
	readonly HashSet<Panel> _hooked = new();

	// Cursor-activity heartbeat: detect when the mouse "wakes up" after being still (looks like a refocus).
	Vector2 _lastPos;
	float _lastMove;

	protected override void OnUpdate()
	{
		HookUiPanels();

		// Layer 0: if a click produced NOTHING (no GAME + no UI line), the window likely lost focus and the
		// OS ate the click. This logs when the cursor starts moving after a still gap — a refocus looks like
		// a long still gap right before the missed click.
		if ( Mouse.Position != _lastPos )
		{
			float still = Time.Now - _lastMove;
			if ( still > 0.4f )
				Log.Info( $"[ClickDbg] cursor woke after {still:0.0}s still  pos={Mouse.Position}" );

			_lastPos = Mouse.Position;
			_lastMove = Time.Now;
		}

		// Layer 1: what the GAME sees. No line here for a UI-consumed click.
		for ( int i = 0; i < Actions.Length; i++ )
		{
			bool now = Input.Down( Actions[i] );
			if ( now == _down[i] )
				continue;

			_down[i] = now;
			Log.Info( $"[ClickDbg] GAME {Names[i]} {(now ? "DOWN" : "UP  ")}  pos={Mouse.Position}  alt={Input.Down( "Walk" )}  over={HoveredElement()}" );
		}
	}

	// Layer 2: hook UI events on every panel-component root once, so we see clicks the game input never gets.
	void HookUiPanels()
	{
		foreach ( var pc in Scene.GetAllComponents<PanelComponent>() )
		{
			var root = pc.Panel;
			if ( root is null || !_hooked.Add( root ) )
				continue;

			// Method groups, NOT capturing lambdas — s&box can't substitute closures passed to
			// AddEventListener ("Unable to find matching substitution for a lambda method"). HoveredElement()
			// already names the component + element, so the handler needs no captured state.
			root.AddEventListener( "onmousedown", OnUiMouseDown );
			root.AddEventListener( "onclick", OnUiClick );
			Log.Info( $"[ClickDbg] hooked UI events on {pc.GetType().Name} (total panel components: {Scene.GetAllComponents<PanelComponent>().Count()})" );
		}
	}

	void OnUiMouseDown() => Log.Info( $"[ClickDbg] UI   mousedown on {HoveredElement()}" );
	void OnUiClick() => Log.Info( $"[ClickDbg] UI   CLICK    on {HoveredElement()}" );

	// The deepest hovered UI element across every PanelComponent in the scene (or <none> if over the world).
	string HoveredElement()
	{
		foreach ( var pc in Scene.GetAllComponents<PanelComponent>() )
		{
			var root = pc.Panel;
			if ( root is null || !root.HasHovered )
				continue;

			var deep = Deepest( root );
			return $"{pc.GetType().Name} → <{deep?.ElementName} class='{deep?.Classes}'>";
		}

		return "<none>";
	}

	static Panel Deepest( Panel p )
	{
		foreach ( var c in p.Children )
		{
			if ( c.HasHovered )
				return Deepest( c );
		}

		return p;
	}
}
