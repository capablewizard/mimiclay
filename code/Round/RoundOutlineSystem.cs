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
///
/// Wherever a claim service (<see cref="PropClaims"/>) is live without a round — creative maps, and the lobby
/// with its editable props — this is the hover presentation gate instead: the claimable clay under the local
/// hunter's crosshair gets the outline glow AND the claymation boil (see <see cref="ApplyClaimBoils"/>),
/// everything else follows the Lobby-phase rules the claims branch subsumes.
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
		// A live claim service WITHOUT a round (creative maps; the lobby with prop editing) takes the
		// hover-presentation rules — which subsume the Lobby-phase rules, so the lobby doesn't also run
		// the phase path. It NEEDS gating just as much as a round: with no system writing Hidden, every
		// prop in the map would wear its authored through-wall glow on every machine. A live round always
		// wins (rounds never host claims; this is belt-and-braces).
		if ( !RoundManager.Current.IsValid() && PropClaims.Current.IsValid() )
		{
			ApplyClaims();
			return;
		}

		// A live round drives the phase. The lobby has no RoundManager, but its pawns are cloned from
		// the SAME prefabs (which now ship their outline enabled for the Hunt), so it takes the rules
		// too, as the Lobby phase — otherwise every lobby hunter would wear the red through-wall glow.
		// (Reached in the lobby only in the replication gap before its PropClaims arrives.)
		// Menu / debug scenes (no manager present) keep their authored outline state untouched.
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

	// ── Claims (creative maps + the lobby) ─────────────────────────────────────────────────────────────────────
	// The hover-glow look, driven through the runtime OVERRIDE slots (never the authored [Property] colours —
	// snapshot-carries-live-state) and restored to null when the crosshair moves off, the SdfOutlineFlash way.
	// The authored prop outline is invisible in the open (alpha-0 colour, through-wall blue only), so without
	// the override a hovered prop in plain sight would show nothing.
	static readonly Color HoverColor = new( 1f, 0.65f, 0.15f );
	const float HoverWidth = 5f;

	// Outlines we hover-tinted, so moving off restores them exactly (null = authored look, no bookkeeping).
	readonly List<SdfHighlightOutline> _hoverDriven = new();

	// Outlines we CREATED for a hover (see below) — these are destroyed on unhover, not restored.
	readonly HashSet<SdfHighlightOutline> _hoverSpawned = new();

	// Stopping play mid-hover must not leave runtime-created components behind: in-editor play mutates the
	// OPEN scene, so survivors bake into the .scene on the next save. Swept here because Dispose is the one
	// hook that runs however play ends. (The hover CHURN needs no sweep of its own any more: it's a freshness
	// stamp on the component — ClayBoil.StampHover — never a written dial, so there is nothing to put back.)
	public override void Dispose()
	{
		foreach ( var o in _hoverSpawned )
		{
			if ( o.IsValid() )
				o.Destroy();
		}
		_hoverSpawned.Clear();

		// Runtime-created boils are found by their component FLAG, never a remembered set: a hotload
		// rebuilds this system with empty memory, and only a scan survives that.
		foreach ( var boil in Scene.GetAllComponents<ClayBoil>() )
		{
			if ( boil.RuntimeManaged )
				boil.Destroy();
		}

		base.Dispose();
	}

	// Claims visibility: your OWN live prop keeps the owner-only locator glow (same rule as the lobby phase),
	// the hunter's SculptBounds warning outline keeps outranking everything, and the one claimable sculpture
	// under the local hunter's crosshair — a released prop OR scene clay — glows as "you can take this".
	// Everything else — other players' pawns, the rest of the released props and decoys — shows nothing.
	// These ARE the Lobby-phase rules plus the hover, which is what lets the lobby take this branch wholesale.
	void ApplyClaims()
	{
		var hover = PropClaims.LocalHoverSculpture;

		// Not every scene prop's prefab carries an outline — only some of the saved exports do (bear yes,
		// alarmclock no), and the prop-builder clay has none. Give the hovered sculpture one ON DEMAND, and
		// destroy it again on unhover (below) rather than leaving it: a runtime-created component on a scene
		// object would otherwise bake into the open scene on an in-editor save. One per subtree, never a
		// second beside an authored one — two live groups read each other's surfaces as occluders.
		if ( hover.IsValid()
			&& !hover.Components.Get<SdfHighlightOutline>( FindMode.EverythingInSelfAndDescendants ).IsValid() )
		{
			var made = hover.Components.Create<SdfHighlightOutline>();
			made.IgnoreDepthOfField = true; // look comes from the hover overrides; this is the one flag they don't cover
			_hoverSpawned.Add( made );
		}

		// Restore anything we tinted that's no longer the hover target (or died with its prop). Matched by the
		// SCULPTURE sharing the outline's GameObject — outlines live beside their clay (the disguise child, a
		// decoy's root) — so pawn and scene props resolve the same way. Outlines we created are destroyed
		// instead of restored (their authored look IS nothing).
		for ( var i = _hoverDriven.Count - 1; i >= 0; i-- )
		{
			var o = _hoverDriven[i];
			if ( o.IsValid() && hover.IsValid()
				&& o.Components.Get<SdfSculpture>( FindMode.EverythingInSelf ) == hover )
				continue;

			if ( o.IsValid() )
			{
				if ( _hoverSpawned.Remove( o ) )
				{
					o.Destroy();
				}
				else
				{
					o.ColorOverride = null;
					o.ObscuredColorOverride = null;
					o.InsideColorOverride = null;
					o.InsideObscuredColorOverride = null;
					o.WidthOverride = null;
				}
			}
			else
			{
				_hoverSpawned.Remove( o );
			}
			_hoverDriven.RemoveAt( i );
		}

		foreach ( var outline in Scene.GetAllComponents<SdfHighlightOutline>() )
		{
			var hovered = false;

			// The tutorial character: visibility is his call (tutorial hover only, hidden while his guided
			// session runs) and the LOOK is his own single-writer drive (see TutorialNpc) — never the claims
			// hover tint below, which his exclusion from IsClaimable keeps him out of anyway.
			var npc = outline.Components.Get<TutorialNpc>( FindMode.EverythingInSelfAndAncestors );
			if ( npc.IsValid() )
			{
				outline.Hidden = !npc.OutlineVisible;
				continue;
			}

			var hunter = outline.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors );
			if ( hunter.IsValid() )
			{
				// Same as the round rules: the head-scoped invalid-face warning is owner-only information and
				// the only hunter outline the claims branch ever shows (no hunt here, so no hunt glow).
				var bounds = outline.Components.Get<SculptBounds>( FindMode.EverythingInSelf );
				outline.Hidden = !(bounds.IsValid() && bounds.LocallyEditable && !bounds.IsSculptValid);
			}
			else
			{
				var sculpture = outline.Components.Get<SdfSculpture>( FindMode.EverythingInSelf );
				hovered = sculpture.IsValid() && sculpture == hover;

				var hider = outline.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
				var own = hider.IsValid() && !hider.IsProxy && !PropClaims.IsReleased( hider )
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

		ApplyClaimBoils( hover );
		ApplySceneBoils( hover );

		// The Reveal pulse never runs here (no phases reach Reveal) — asserted OFF the same both-ways way the
		// round branch asserts it, so a snapshot that shipped a mid-Reveal flash can't leave one pulsing.
		foreach ( var hider in Scene.GetAllComponents<HiderController>() )
		{
			var flash = hider.Components.Get<SdfOutlineFlash>( FindMode.EverythingInSelf );
			if ( flash.IsValid() )
				flash.Enabled = false;
		}
	}

	// ── Runtime boils on SCENE clay: hovered = "you can take this", healing = "the wound is being smoothed" ──
	// Most scene clay (decoys, prop-builder balls, blockset pieces) carries no ClayBoil at all, so one is
	// created on demand — for the hover churn, and because creative shots always heal (HunterController.TryCarve
	// forces it) and a crater easing out of perfectly still clay reads as a glitch. Created RuntimeManaged, its
	// Activation ASSERTED to WhileDamaged every frame (the hover churn is the freshness stamp, never the dial —
	// see ClayBoil.StampHover) and retired the moment it stops Boiling — !Boiling rather than "no shrinking
	// brush left", so the hover stamp's decay and the final removal's follow-through jolt (ClayBoil.ImpactTicks)
	// play out instead of being cut mid-shudder. Everything derives from durable state (the flag on the
	// component, the hover, the brushes) — no system memory is load-bearing, so a hotload rebuilding this
	// system can't orphan one mid-hover with Always latched in (the stuck-boiling-props bug this replaced).
	//
	// A mapper-AUTHORED boil (RuntimeManaged false, includeDisabled — disabled is their kill switch, not an
	// invitation to plant a second one beside it) keeps its Activation untouched here: its own dial decides its
	// damage response, and the hover churn reaches it through ApplyClaimBoils' stamp instead.
	void ApplySceneBoils( SdfSculpture hover )
	{
		foreach ( var sculpt in Scene.GetAllComponents<SdfSculpture>() )
		{
			// Scene clay only: pawns are asserted above (hunter faces keep their authored character boil),
			// and NotSaved excludes runtime rigs — most importantly SdfStage's thumbnail hosts, which live
			// in the game scene and can carry copies of mid-heal brush lists.
			if ( sculpt.GameObject.Flags.HasFlag( GameObjectFlags.NotSaved ) )
				continue;
			if ( sculpt.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ).IsValid()
				|| sculpt.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
				continue;

			bool hovered = sculpt == hover;

			var boil = sculpt.Components.Get<ClayBoil>( includeDisabled: true );
			if ( boil.IsValid() && !boil.RuntimeManaged )
				continue; // the mapper's look — ApplyClaimBoils' stamp handles its hover churn

			if ( !boil.IsValid() )
			{
				bool healing = sculpt.Brushes is { Count: > 0 } brushes && brushes.Exists( b => b.Shrinks );
				if ( !hovered && !healing )
					continue;

				boil = sculpt.Components.Create<ClayBoil>(); // defaults = the pawn tuning
				boil.RuntimeManaged = true;
			}

			boil.Activation = BoilActivation.WhileDamaged;
			if ( hovered )
				boil.StampHover();
			else if ( !boil.Boiling )
				boil.Destroy();
		}
	}

	// ── Authored boils: pawn assertion + the hovered scene prop's mapper-authored churn stamp ─────────────────
	// The stop-motion churn is the same "you can take this" feedback as the outline (runtime-created scene
	// boils live in ApplySceneBoils; this handles the components somebody AUTHORED). The disguise/hunter
	// prefabs' dials ARE ClayBoil's defaults (fps 4, jitter 0.4, amp 1), only Activation differs.
	void ApplyClaimBoils( SdfSculpture hover )
	{
		// PAWN props carry an authored boil on the disguise. Asserted continuously and in BOTH directions,
		// the RoundRevealLifeSystem way: a network snapshot ships live component state, so a prop that was
		// being hovered on the authority machine when a late-joiner connected would otherwise arrive boiling
		// and never stop (the RunPFX lesson). In CREATIVE, RevealLife is inert (no round/lobby manager) and
		// this owns every pawn's boil. In the LOBBY, RevealLife is live and owns the LIVE pawns' boils (the
		// Lobby-phase dead state); only RELEASED pawns — scenery, which RevealLife deliberately skips — take
		// the hover feedback here, so the two systems never write the same component. The rest state is
		// WhileDamaged, not Never: creative shots always heal (see HunterController.TryCarve), and a crater
		// silently easing out of perfectly still clay reads as a glitch — so a shot prop churns from impact
		// through the heal and settles the moment it's whole, exactly the component's own damage fiction. On
		// undamaged clay WhileDamaged is indistinguishable from Never, so the snapshot-leak reasoning above
		// still holds.
		foreach ( var hider in Scene.GetAllComponents<HiderController>() )
		{
			if ( LobbyManager.Current.IsValid() && !PropClaims.IsReleased( hider ) )
				continue; // a live lobby pawn — RoundRevealLifeSystem's boil, not ours

			var boil = hider.Components.Get<ClayBoil>( FindMode.EverythingInSelfAndDescendants );
			if ( !boil.IsValid() )
				continue;

			boil.Activation = BoilActivation.WhileDamaged;
			if ( hover.IsValid() && hider.DisguiseSculpture == hover )
				boil.StampHover();
		}

		// SCENE props with a mapper-AUTHORED boil: the hover churn is a freshness stamp on the component
		// (ClayBoil.StampHover), never a write to their Activation dial. The capture/restore this replaces
		// latched a transient Always in whenever this system lost its memory mid-hover (a hotload rebuild,
		// a poisoned Listen delegate) — and the next hover then re-captured the leaked Always as "authored",
		// making it permanent; that was the second stuck-boiling-props bug, and how leaked Activation
		// overrides got baked into home.scene. A stamp expires by itself: there is no restore step to miss.
		// Enabled-only Get, matching the old flip: a DISABLED authored boil is the mapper's kill switch and
		// gets no hover churn. Everything runtime-created gets its stamp in ApplySceneBoils.
		var sceneHover = hover.IsValid()
			&& !hover.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ).IsValid()
			? hover : null;

		if ( sceneHover.IsValid() )
		{
			var existing = sceneHover.Components.Get<ClayBoil>();
			if ( existing.IsValid() && !existing.RuntimeManaged )
				existing.StampHover();
		}
	}

	static bool ShouldShow( SdfHighlightOutline outline, RoundPhase phase )
	{
		// The tutorial character's affordance exists only under the claims branch — any phase-ruled context
		// (a live round, the lobby's pre-claims replication gap) hides him like any other scene glow.
		if ( outline.Components.Get<TutorialNpc>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false;

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
