using System;

namespace Mimiclay;

/// <summary>
/// The slice of "what phase are we in and how long is left" that the HUD (and any phase-agnostic UI) needs,
/// without caring whether the lobby or a live round is running. Both <see cref="LobbyController"/> and
/// <see cref="RoundManager"/> implement it and publish themselves to <see cref="RoundContext.Active"/>, so a single
/// HUD binds to one source across the whole loop and across scene changes.
/// </summary>
public interface IRoundContext
{
	/// <summary>The phase currently showing to players.</summary>
	RoundPhase Phase { get; }

	/// <summary>Seconds left on the current phase's timer, or 0 when the phase has no countdown.</summary>
	float TimeRemaining { get; }

	/// <summary>True when <see cref="TimeRemaining"/> is meaningful (the phase is timed).</summary>
	bool HasTimer { get; }
}

/// <summary>
/// Process-wide pointer to whichever <see cref="IRoundContext"/> is live in the current scene. The active manager
/// sets this in <c>OnEnabled</c> and clears it in <c>OnDisabled</c>, so it follows scene changes for free — the
/// lobby's <see cref="LobbyController"/> tears down as its scene unloads and the map's <see cref="RoundManager"/>
/// installs itself as the new scene starts. UI reads <see cref="Active"/> and tolerates it being null mid-handoff.
/// </summary>
public static class RoundContext
{
	public static IRoundContext Active { get; set; }
}

/// <summary>
/// Broadcast to every component implementing it whenever the round phase changes, so gameplay/UI can react to a
/// transition (play a sting, flip visibility, frame the consolidation gallery) without polling. Fired host-side
/// inside <see cref="RoundManager"/> right after the networked <c>Phase</c> flips; because <c>Phase</c> is
/// <c>[Sync]</c>, proxies also raise it locally when they observe the change, so listeners on every machine see it.
///
/// Post with <c>IRoundPhaseChanged.Post( x =&gt; x.OnRoundPhaseChanged( from, to ) )</c>.
/// </summary>
public interface IRoundPhaseChanged : ISceneEvent<IRoundPhaseChanged>
{
	void OnRoundPhaseChanged( RoundPhase from, RoundPhase to );
}
