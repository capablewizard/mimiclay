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
///
/// TWO HARD RULES, both learned the hard way — the HUD got baked into ten scenes and prefabs, so every pawn, prop,
/// gun and thumbnail rig cloned its own pause menu:
///   1. Never spawn in an editor scene. Stage.StartUpdate is signalled from Scene.SharedTick, which EditorTick
///      calls too — so without the <see cref="Scene.IsEditor"/> guard, merely OPENING a scene or prefab in the
///      editor drops a "Pause HUD" object into it, and the next save writes it to disk.
///   2. Flag the object <see cref="GameObjectFlags.NotSaved"/>. Belt to rule 1's braces: a runtime-only object
///      should never be serialisable into an asset, whatever ticks it.
/// </summary>
public sealed class PauseMenuSystem : GameObjectSystem
{
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

		// Editing, not playing — see rule 1 on the class. Nothing we create here would ever be wanted, and
		// anything we DID create could be saved into whatever asset is open.
		if ( Scene.IsEditor )
			return;

		// Already spawned and alive — the common case. PauseMenu keeps this static itself (and enforces that it's
		// the only one), so the check is O(1): no per-frame scene scan, and it's re-evaluated rather than latched,
		// which matters because Game.ChangeScene reuses the Scene and keeps this system instance.
		if ( PauseMenu.Current.IsValid() )
			return;

		// Front-end menu scene → no pause overlay (it navigates with the footer Back/Quit). Re-checked each frame,
		// NOT permanently, so the same system surviving a scene change into gameplay will still spawn there.
		if ( Scene.GetAllComponents<MainMenu>().Any() )
			return;

		var go = new GameObject( true, "Pause HUD" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked; // rule 2 — runtime only, ours only
		// Above the edit HUD's ScreenPanel (default ZIndex 100) so the blur + buttons cover everything, and the
		// backdrop captures input ahead of it while paused.
		var screen = go.Components.Create<ScreenPanel>();
		screen.ZIndex = 1000;
		go.Components.Create<PauseMenu>();
	}
}
