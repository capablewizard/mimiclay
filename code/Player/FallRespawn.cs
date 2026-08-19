namespace Mimiclay;

/// <summary>
/// The out-of-world safety net: how far below its spawn placement a pawn must sink before its controller
/// snaps it back there (see the checks in <see cref="HunterController"/> / <see cref="HiderController"/>).
/// A stopgap for blockout-map floor holes and physics tunnelling — the depth is spawn-RELATIVE rather than
/// an absolute kill-Z so it needs no per-map setup and survives maps built at any height.
/// </summary>
public static class FallRespawn
{
	/// <summary>Units below the pawn's recorded spawn Z that count as "fell out of the world". Generous on
	/// purpose: nothing walkable sits anywhere near this far below a spawn point, so a trigger is always a
	/// genuine escape, never a deep-but-legal drop.</summary>
	public const float Depth = 1000f;

	public static bool Fell( float z, float anchorZ ) => z < anchorZ - Depth;
}
