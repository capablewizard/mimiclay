using System.Collections.Generic;

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
/// <item>A HUNTER pawn's outline shows from Hunt onward, and only on machines playing a PROP. Props tracking
/// the threat through walls is the prop's information edge — the glow is for THEM. Fellow hunters don't need
/// it (they can see each other in the open), and on the owner's machine it would paint the gun/hands (which,
/// unlike the RenderHidden first-person body, stay visible).</item>
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
		else if ( CreativeManager.Current.IsValid() )
		{
			// Creative has its own rules (no phases, and the hover glow) — and it NEEDS gating just as much:
			// with no system writing Hidden, every prop in the map would wear its authored through-wall glow
			// on every machine.
			ApplyCreative();
			return;
		}
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

	// ── Creative ───────────────────────────────────────────────────────────────────────────────────────────────
	// The hover-glow look, driven through the runtime OVERRIDE slots (never the authored [Property] colours —
	// snapshot-carries-live-state) and restored to null when the crosshair moves off, the SdfOutlineFlash way.
	// The authored prop outline is invisible in the open (alpha-0 colour, through-wall blue only), so without
	// the override a hovered prop in plain sight would show nothing.
	static readonly Color HoverColor = new( 1f, 0.65f, 0.15f );
	const float HoverWidth = 5f;

	// Outlines we hover-tinted, so moving off restores them exactly (null = authored look, no bookkeeping).
	readonly List<SdfHighlightOutline> _hoverDriven = new();

	// Creative visibility: your OWN live prop keeps the owner-only locator glow (same rule as the lobby), the
	// hunter's SculptBounds warning outline keeps outranking everything, and the one claimable sculpture under
	// the local hunter's crosshair — a released prop OR scene clay — glows as "you can take this". Everything
	// else — other players' pawns, the rest of the released props and decoys — shows nothing.
	void ApplyCreative()
	{
		var hover = CreativeManager.LocalHoverSculpture;

		// Restore anything we tinted that's no longer the hover target (or died with its prop). Matched by the
		// SCULPTURE sharing the outline's GameObject — outlines live beside their clay (the disguise child, a
		// decoy's root) — so pawn and scene props resolve the same way.
		for ( var i = _hoverDriven.Count - 1; i >= 0; i-- )
		{
			var o = _hoverDriven[i];
			if ( o.IsValid() && hover.IsValid()
				&& o.Components.Get<SdfSculpture>( FindMode.EverythingInSelf ) == hover )
				continue;

			if ( o.IsValid() )
			{
				o.ColorOverride = null;
				o.ObscuredColorOverride = null;
				o.InsideColorOverride = null;
				o.InsideObscuredColorOverride = null;
				o.WidthOverride = null;
			}
			_hoverDriven.RemoveAt( i );
		}

		foreach ( var outline in Scene.GetAllComponents<SdfHighlightOutline>() )
		{
			var hovered = false;

			var hunter = outline.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors );
			if ( hunter.IsValid() )
			{
				// Same as the round rules: the head-scoped invalid-face warning is owner-only information and
				// the only hunter outline creative ever shows (there is no hunt, so no hunt glow).
				var bounds = outline.Components.Get<SculptBounds>( FindMode.EverythingInSelf );
				outline.Hidden = !(bounds.IsValid() && bounds.LocallyEditable && !bounds.IsSculptValid);
			}
			else
			{
				var sculpture = outline.Components.Get<SdfSculpture>( FindMode.EverythingInSelf );
				hovered = sculpture.IsValid() && sculpture == hover;

				var hider = outline.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
				var own = hider.IsValid() && !hider.IsProxy && !hider.Released
					&& !RoundManager.IsBotPawn( hider.GameObject );
				outline.Hidden = !(hovered || own);
			}

			if ( hovered && !_hoverDriven.Contains( outline ) )
			{
				outline.ColorOverride = HoverColor;
				outline.ObscuredColorOverride = HoverColor.WithAlpha( 0.35f );
				outline.InsideColorOverride = HoverColor.WithAlpha( 0.08f );
				outline.InsideObscuredColorOverride = HoverColor.WithAlpha( 0.08f );
				outline.WidthOverride = HoverWidth;
				_hoverDriven.Add( outline );
			}
		}
	}

	static bool ShouldShow( SdfHighlightOutline outline, RoundPhase phase )
	{
		// Hunter pawn: everyone sees its outlines once the Hunt is on — during Hide "no player can see
		// another player", and that must hold even if pawns ever become visible to proxies before Hunt.
		// Normally that's just the pawn-root glow: the head-scoped WARNING outline is WarningOnly —
		// enabled by SculptBounds solely while the owner's face is invalid (disabled, its renderers fold
		// back into the root group), so it only reaches this gate in that state. Pre-Hunt, it is also the
		// ONLY hunter outline allowed out — the one sharing its GameObject with the bounds component, for
		// a locally-authored invalid face (owner-only information; SculptBounds drives its warn colours).
		// Never the root glow: that would paint the whole body pre-Hunt.
		var hunter = outline.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors );
		if ( hunter.IsValid() )
		{
			// The head-scoped SculptBounds WARNING outline (co-located with the bounds component) is
			// owner-only information and OUTRANKS the phase rules: it shows exactly while the local
			// player's face is invalid, whatever the phase — and never for anyone else. It's only ever
			// enabled while the warning is live (WarningOnly), so this branch is rarely even reached.
			var bounds = outline.Components.Get<SculptBounds>( FindMode.EverythingInSelf );
			if ( bounds.IsValid() )
				return bounds.LocallyEditable && !bounds.IsSculptValid;

			// The hunt glow — threat information FOR THE PROPS, so only machines NOT playing a hunter get
			// it: fellow hunters can already see each other in the open and don't need the tell (LocalRole
			// reads the owned pawn, so a caught prop stops seeing it the moment they convert). The proxy/bot
			// check still excludes the owner's own pawn beneath that — while the first-person body self-hides
			// (RenderHidden, which the highlight follows), the gun/hands don't — and covers machines with no
			// pawn at all (LocalRole Unassigned), where a host-owned bot hunter isn't a proxy yet must glow.
			return phase >= RoundPhase.Hunt
				&& RoundManager.LocalRole != PlayerRole.Hunter
				&& (hunter.IsProxy || RoundManager.IsBotPawn( hunter.GameObject ));
		}

		// Prop pawn (outline sits inside the cloned Disguise child): owner-only — except the Reveal show-off,
		// where the only prop pawns left standing are the survivors. A test bot's prop is host-owned and so isn't
		// a proxy on the host, which would otherwise hand that machine a through-wall glow on every bot — the
		// exact tell this whole system exists to deny. (The owner-only rule already keeps the invalid-sculpt
		// warning visible to its owner — no extra case needed here.)
		var hider = outline.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
			return (!hider.IsProxy && !RoundManager.IsBotPawn( hider.GameObject )) || phase == RoundPhase.Reveal;

		// No pawn above it: a scene decoy.
		return false;
	}
}
