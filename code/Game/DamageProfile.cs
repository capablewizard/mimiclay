namespace Mimiclay;

/// <summary>
/// GAMEPLAY policy for how the detector gun's carves behave on the sculpture this sits beside — kept OUT of
/// <see cref="SdfSculpture"/> deliberately: the SDF layer is mode-agnostic tooling (a pictionary mode has no
/// concept of healing), while this component is a prop-hunt rule. Compose it next to the sculpture, the same
/// way SdfCollider/SdfNetworkSync attach behaviour (hunter.prefab's Head carries one so faces become
/// recognisable again).
///
/// ABSENT = the default: craters scar permanently. That default is also a game-integrity rule — player
/// disguises must behave identically to decoys (both scar), or a healing crater would be an inverted tell
/// exposing the player. Heads healing leaks nothing: hunters are public. The SANDBOX modes (creative and
/// the lobby) override the default wholesale — every carve heals there (see HunterController.TryCarve),
/// on this component's timing when present and on <see cref="DefaultHealDelay"/>/<see cref="DefaultHealDuration"/>
/// otherwise: neither has a hunt to keep honest, and shots must never permanently deface what people are
/// building. Scarring is strictly a prop-hunt ROUND rule.
///
/// The shooter reads this off the target at carve time, rolls the timing ONCE, and ships concrete values
/// with the carve — so every machine agrees on each crater's schedule (see HunterController.TryCarve; the
/// animation itself is the mode-agnostic SdfShrinkSystem).
///
/// Presentation lives elsewhere: give the prop a <see cref="ClayBoil"/> set to WhileDamaged and it churns
/// stop-motion style from impact until the heal settles (see ClayBoil.Activation) — purely local look,
/// no gameplay effect, which is why it isn't a property here.
/// </summary>
[Title( "Damage Profile" )]
[Category( "Mimiclay" )]
[Icon( "healing" )]
public sealed class DamageProfile : Component
{
	/// <summary>The timing a creative-forced heal uses on clay with no profile of its own — also the
	/// authored properties' initial values, so the two can never drift apart.</summary>
	internal static readonly Vector2 DefaultHealDelay = new( 1.5f, 3.5f );

	/// <inheritdoc cref="DefaultHealDelay"/>
	internal static readonly Vector2 DefaultHealDuration = new( 0.8f, 2.2f );

	/// <summary>Craters on this sculpture heal (shrink away and free their brush slots).</summary>
	[Property] public bool Heals { get; set; } = true;

	/// <summary>Grace period range (seconds, min..max) before a crater starts healing — rolled per crater.</summary>
	[Property] public Vector2 HealDelay { get; set; } = DefaultHealDelay;

	/// <summary>Heal (ease-to-nothing) duration range, seconds min..max — rolled per crater.</summary>
	[Property] public Vector2 HealDuration { get; set; } = DefaultHealDuration;
}
