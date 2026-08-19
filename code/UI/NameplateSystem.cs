namespace Mimiclay;

/// <summary>
/// Spawns the one shared nameplate overlay (ScreenPanel + <see cref="PlayerNameplates"/>) per gameplay scene —
/// the same programmatic-HUD shape as <see cref="PauseMenuSystem"/>, and it exists for the same reason that
/// system does: nameplates are a PER-MACHINE concern (labels over other players' heads on MY screen), so hosting
/// them on the pawn prefabs meant N copies with a fragile "am I the one that draws?" gate — a gate every unowned
/// pawn slipped on the host (released props read !IsProxy there), waking a stacked overlay per released prop and
/// melting the host's framerate late in creative sessions. One machine-local overlay has nothing to wake.
///
/// Both of PauseMenuSystem's hard rules apply verbatim (the pause HUD got baked into ten assets learning them):
///   1. Never spawn in an editor scene — GameObjectSystems tick when a scene or prefab is merely OPEN in the
///      editor, and the next save would write our object to disk. <see cref="Scene.IsEditor"/> guards it.
///   2. Flag the object <see cref="GameObjectFlags.NotSaved"/> — a runtime-only object should never be
///      serialisable into an asset, whatever ticks it.
///
/// No front-end-menu skip, unlike the pause system: an overlay with no pawns to label draws nothing and reads no
/// input, so it's harmless there — and skipping would cost a per-frame scene scan forever instead of one idle panel.
/// </summary>
public sealed class NameplateSystem : GameObjectSystem
{
	public NameplateSystem( Scene scene ) : base( scene )
	{
		// Re-checked each frame; cheap once the HUD exists (early-out on the IsValid check). Re-evaluated rather
		// than latched because Game.ChangeScene reuses the Scene and keeps this system instance.
		Listen( Stage.StartUpdate, 0, EnsureHud, "EnsureNameplateHud" );
	}

	void EnsureHud()
	{
		if ( Scene is null )
			return;

		// Editing, not playing — see rule 1 on the class.
		if ( Scene.IsEditor )
			return;

		// Already spawned and alive — the common case. PlayerNameplates keeps this static itself (and enforces
		// that it's the only one), so the check is O(1): no per-frame scene scan.
		if ( PlayerNameplates.Current.IsValid() )
			return;

		var go = new GameObject( true, "Nameplate HUD" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked; // rule 2 — runtime only, ours only

		// Ambient gameplay UI: same layer the old per-pawn HUD used — under the EditHud (100) and every dialog.
		var screen = go.Components.Create<ScreenPanel>();
		screen.ZIndex = 50;

		go.Components.Create<PlayerNameplates>();
	}
}
