using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Spawns one shared pause-menu HUD (ScreenPanel + <see cref="PauseMenu"/>) per gameplay scene, mirroring how the
/// edit HUD is spawned programmatically (see SculptEditSession.EnsureHud) rather than depending on a scene-placed
/// GameObject. A GameObjectSystem is created automatically for every scene, so this needs no wiring.
///
/// Skipped in the front-end menu scene (which has its own <see cref="MainMenu"/>) — that scene navigates with the
/// footer Back/Quit, it doesn't want an in-game pause overlay.
/// </summary>
public sealed class PauseMenuSystem : GameObjectSystem
{
	// The HUD we spawned. We key off "is it still alive" rather than a one-shot flag — Game.ChangeScene (the
	// host/join handoff) can reuse the Scene and keep this system instance, so a permanent flag set in the menu
	// scene would stick and we'd never spawn once gameplay loads. Re-evaluating each frame is robust to that.
	PauseMenu _hud;

	public PauseMenuSystem( Scene scene ) : base( scene )
	{
		// StartUpdate runs in the scene tick (so PauseMenu.OnUpdate can consume Escape before the menu DLL's
		// LateTick). Re-checked each frame; cheap once the HUD exists (early-out on the IsValid check).
		Listen( Stage.StartUpdate, 0, EnsureHud, "EnsurePauseHud" );

		// FinishUpdate runs AFTER every component OnUpdate, so forcing the cursor here wins over any gameplay
		// controller that hid/locked it earlier in the frame — the menu always has a usable cursor while paused.
		Listen( Stage.FinishUpdate, 0, ForcePausedCursor, "PauseCursor" );
	}

	static void ForcePausedCursor()
	{
		// Same cursor force for any full-screen modal that needs clicking — the pause menu and the host's round
		// setup. Running at FinishUpdate, this wins over a gameplay controller that hid the cursor earlier in the
		// frame, which also stops look capture (the controllers only read look while the cursor is hidden).
		if ( !PauseMenu.IsOpen && !RoundSetup.IsOpen )
			return;

		Mouse.Visibility = MouseVisibility.Visible;
		Mouse.CursorType = default; // plain arrow (clear any "dot-large" the orbit camera left set)
	}

	void EnsureHud()
	{
		if ( Scene is null )
			return;

		// Already spawned and alive — the common case; cheap early-out.
		if ( _hud.IsValid() )
			return;

		// Front-end menu scene → no pause overlay (it navigates with the footer Back/Quit). Re-checked each frame,
		// NOT permanently, so the same system surviving a scene change into gameplay will still spawn there.
		if ( Scene.GetAllComponents<MainMenu>().Any() )
			return;

		// Someone else already made one (e.g. a future scene-placed HUD) — adopt it.
		_hud = Scene.GetAllComponents<PauseMenu>().FirstOrDefault();
		if ( _hud.IsValid() )
			return;

		var go = new GameObject( true, "Pause HUD" );
		// Above the edit HUD's ScreenPanel (default ZIndex 100) so the blur + buttons cover everything, and the
		// backdrop captures input ahead of it while paused.
		var screen = go.Components.Create<ScreenPanel>();
		screen.ZIndex = 1000;
		_hud = go.Components.Create<PauseMenu>();
	}
}
