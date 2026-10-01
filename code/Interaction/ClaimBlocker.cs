namespace Mimiclay;

/// <summary>
/// The per-prop lock: clay on this object (or under it) can never be claimed/possessed, whatever mode hosts
/// <see cref="PropClaims"/>. For set pieces that are clay but must stay put — the charades stage plinth. Other
/// interactions on the object (an <see cref="IInteractable"/> beside it) are unaffected.
/// </summary>
[Title( "Claim Blocker" )]
[Category( "Mimiclay" )]
[Icon( "block" )]
public sealed class ClaimBlocker : Component
{
}
