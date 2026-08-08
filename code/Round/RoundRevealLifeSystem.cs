namespace Mimiclay;

/// <summary>
/// The Reveal "they're ALIVE" beat: from <see cref="RoundPhase.Reveal"/> onward, every surviving prop
/// pawn's clay comes to life — its <see cref="ClayBoil"/> flips from Never to Always (the animator's
/// hands are on it now, and a revealed prop can't go back to playing dead) and its
/// <see cref="DamageProfile"/> starts healing, so the scars it collected during the Hunt melt away and
/// the harmless-fun shots of the Consolidation gallery can't permanently ruin the disguise being voted
/// on. Before the Reveal both flags sit in their authored dead state (boil Never, heals off) — a
/// disguise must behave identically to a decoy, and a boiling or self-repairing prop would be the tell.
///
/// Applied per MACHINE, each frame, like <see cref="RoundOutlineSystem"/> — the flags are local look /
/// carve-time policy, never networked, and continuous assertion both directions means a network
/// snapshot that ships another machine's live "alive" state can never leak it into the wrong phase
/// (see the RunPFX lesson). Only HIDER pawns are touched: scene decoys stay dead at the Reveal — that
/// stillness is the reveal — and hunter pawns own their authored always-boil + healing face.
///
/// Scar healing is retroactive by conversion: carves shipped while Heals was false are flagged
/// <see cref="SdfBrush.Damage"/> but not Shrinks, so flipping the profile alone would only affect
/// future carves. On an alive pawn any such scar brush is converted to a shrinking one, with timing
/// staggered DETERMINISTICALLY from the brush index (golden-ratio hash of the profile's ranges) —
/// every machine holds the same broadcast-built brush list, so every machine computes the same
/// schedule without shipping anything. Conversion is idempotent (Damage &amp;&amp; !Shrinks only
/// matches once) and per-frame, so carves that somehow arrive mid-Reveal heal too.
/// </summary>
public sealed class RoundRevealLifeSystem : GameObjectSystem
{
	public RoundRevealLifeSystem( Scene scene ) : base( scene )
	{
		// StartUpdate, same slot as the outline gate: the verdict lands before the renderer pushes
		// boil attributes, so the flip shows the frame the phase does.
		Listen( Stage.StartUpdate, 0, Apply, "RoundRevealLife" );
	}

	void Apply()
	{
		// Play mode only — an editor scene must never have pawn/prefab state asserted into it.
		if ( !Game.IsPlaying )
			return;

		// Same phase source as RoundOutlineSystem: a live round drives it; the lobby (same pawn
		// prefabs) takes the dead state as the Lobby phase; menu / debug scenes are left untouched.
		RoundPhase phase;
		if ( RoundManager.Current.IsValid() )
			phase = RoundManager.Current.Phase;
		else if ( LobbyManager.Current.IsValid() )
			phase = RoundPhase.Lobby;
		else
			return;

		// Reveal AND Consolidation: once shown alive, the prop stays alive through the gallery.
		bool alive = phase >= RoundPhase.Reveal;

		foreach ( var hider in Scene.GetAllComponents<HiderController>() )
		{
			var boil = hider.Components.Get<ClayBoil>( FindMode.EverythingInSelfAndDescendants );
			if ( boil.IsValid() )
				boil.Activation = alive ? BoilActivation.Always : BoilActivation.Never;

			var profile = hider.Components.Get<DamageProfile>( FindMode.EverythingInSelfAndDescendants );
			if ( !profile.IsValid() )
				continue;
			profile.Heals = alive;

			if ( alive )
				HealScars( hider.DisguiseSculpture, profile );
		}
	}

	/// <summary>Convert every still-permanent scar crater on the disguise into a healing one.</summary>
	static void HealScars( SdfSculpture sculpt, DamageProfile profile )
	{
		if ( !sculpt.IsValid() || sculpt.Brushes is not { Count: > 0 } brushes )
			return;

		for ( int i = 0; i < brushes.Count; i++ )
		{
			var b = brushes[i];
			if ( !b.Damage || b.Shrinks )
				continue;

			// Index-hashed stagger through the profile's authored ranges — the reveal reads best as
			// wounds closing one after another, not one synchronized wipe. Deterministic so every
			// machine agrees (see class doc); the golden-ratio multiplier decorrelates neighbours.
			float f = ( i * 0.6180339887f ) % 1f;
			b.Shrinks = true;
			b.ShrinkAge = 0f;
			b.ShrinkDelay = profile.HealDelay.x + ( profile.HealDelay.y - profile.HealDelay.x ) * f;
			b.ShrinkDuration = profile.HealDuration.x + ( profile.HealDuration.y - profile.HealDuration.x ) * ( ( f * 2.6180339887f ) % 1f );
		}
	}
}
