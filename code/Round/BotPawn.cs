using System;

namespace Mimiclay;

/// <summary>
/// Marks a pawn the HOST spawned to stand in for a bot roster row (see <see cref="RoundManager"/>'s test bots).
/// Every bot pawn is host-owned, so <c>Network.Owner</c> answers "the host" for all of them AND for the host's own
/// pawn — useless for telling them apart. This component carries the row the body actually belongs to, and
/// <see cref="RoundManager.RosterIdOf"/> is what anything asking "whose pawn is this?" should go through.
/// [Sync]'d (and set before the pawn goes on the wire) so clients resolve it exactly the way the host does.
/// </summary>
[Title( "Bot Pawn" )]
[Category( "Mimiclay" )]
[Icon( "smart_toy" )]
public sealed class BotPawn : Component
{
	/// <summary>The <see cref="RoundManager.Players"/> row this body stands in for.</summary>
	[Sync] public Guid RosterId { get; set; }
}
