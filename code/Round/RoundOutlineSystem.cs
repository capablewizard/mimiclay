namespace Mimiclay;

/// <summary>
/// Round-time visibility rules for every <see cref="SdfHighlightOutline"/> in the scene (the through-wall glow),
/// so the glow can never give a hider away. Without this, both pawn prefabs ship their outline simply ON, which
/// let hunters see every prop's silhouette through walls — the exact information the game is about denying them.
///
/// Applied per MACHINE, each frame, while a <see cref="RoundManager"/> OR <see cref="LobbyManager"/> is live —
/// the lobby clones pawns from the same prefabs, so it follows the same rules as the Lobby phase (menu / debug
/// scenes keep their authored outline state untouched):
/// <list type="bullet">
/// <item>A HUNTER pawn's outline shows for everyone from Hunt onward. Hunters aren't hiding, and props tracking the threat
/// through walls is the prop's information edge (the hunter's own first-person body already self-hides via
/// RenderHidden, which the highlight follows).</item>
/// <item>A PROP pawn's outline shows only on the machine that owns it — a private "where am I" locator when the
/// player's own disguise is occluded. Everyone else (hunters AND fellow props, who convert to hunters when
/// found) sees nothing.</item>
/// <item>A scene DECOY's outline shows for nobody. The saved prop prefabs carry an outline from the disguise
/// template they were exported from; if decoys glowed while player props didn't, the one silhouette WITHOUT a
/// glow would be the player — an inverted tell.</item>
/// <item>Reveal: surviving prop pawns show for everyone, and their pawn's <see cref="SdfOutlineFlash"/> is
/// enabled so the outline PULSES — the full reveal "show them off" flash.</item>
/// </list>
///
/// Gates <see cref="SdfHighlightOutline.Hidden"/>, NOT Enabled: Scene.GetAllComponents only enumerates enabled
/// components, so an outline this system disabled could never be found again to re-show at the Reveal.
/// A GameObjectSystem, like <see cref="PauseMenuSystem"/>: exists in every scene with no wiring.
/// </summary>
public sealed class RoundOutlineSystem : GameObjectSystem
{
	public RoundOutlineSystem( Scene scene ) : base( scene )
	{
		// StartUpdate, before component OnUpdates: the highlight redraws from its member renderers' OnUpdate,
		// so a verdict written here lands the same frame — no one-frame glow when a pawn spawns or converts.
		Listen( Stage.StartUpdate, 0, Apply, "RoundOutlineGate" );
	}

	void Apply()
	{
		// A live round drives the phase. The lobby has no RoundManager, but its pawns are cloned from
		// the SAME prefabs (which now ship their outline enabled for the Hunt), so it takes the rules
		// too, as the Lobby phase — otherwise every lobby hunter would wear the red through-wall glow.
		// Menu / debug scenes (neither manager present) keep their authored outline state untouched.
		RoundPhase phase;
		if ( RoundManager.Current.IsValid() )
			phase = RoundManager.Current.Phase;
		else if ( LobbyManager.Current.IsValid() )
			phase = RoundPhase.Lobby;
		else
			return;

		foreach ( var outline in Scene.GetAllComponents<SdfHighlightOutline>() )
			outline.Hidden = !ShouldShow( outline, phase );

		// Reveal pulse: any prop pawn still standing at the Reveal IS a survivor (caught props were
		// swapped to hunter pawns), so its authored-off SdfOutlineFlash runs for exactly that phase.
		// Asserted per machine every frame like the visibility above — Get, not GetAllComponents,
		// because the flash is DISABLED most of the time and GetAllComponents skips disabled.
		foreach ( var hider in Scene.GetAllComponents<HiderController>() )
		{
			var flash = hider.Components.Get<SdfOutlineFlash>( FindMode.EverythingInSelf );
			if ( flash.IsValid() )
				flash.Enabled = phase == RoundPhase.Reveal;
		}
	}

	static bool ShouldShow( SdfHighlightOutline outline, RoundPhase phase )
	{
		// Hunter pawn (outline sits on the pawn root): everyone sees it, but only once the Hunt is on —
		// during Hide "no player can see another player", and that must hold even if pawns ever become
		// visible to proxies before Hunt.
		if ( outline.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return phase >= RoundPhase.Hunt;

		// Prop pawn (outline sits inside the cloned Disguise child): owner-only — except the Reveal show-off,
		// where the only prop pawns left standing are the survivors.
		var hider = outline.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
			return !hider.IsProxy || phase == RoundPhase.Reveal;

		// No pawn above it: a scene decoy.
		return false;
	}
}
