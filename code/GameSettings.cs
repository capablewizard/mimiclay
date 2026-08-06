namespace Mimiclay;

/// <summary>
/// Game-wide tunables destined for a user-facing settings menu. Single source of truth: read these at the
/// point of use, every frame, rather than copying them — that way a future settings screen just writes the
/// fields and the change takes effect live. When the menu lands, persist them as JSON via FileSystem.Data
/// (same shape as the sculpt saves).
/// </summary>
public static class GameSettings
{
	/// <summary>First-person field of view for the hunter, vertical degrees.</summary>
	public static float HunterFov { get; set; } = 90f;

	/// <summary>Hunter camera in third person (over-the-shoulder boom) instead of first person. Toggled in
	/// game with the "View" action; static so the choice survives pawn respawns (prop→hunter conversion
	/// spawns a fresh pawn) and scene changes.</summary>
	public static bool HunterThirdPerson { get; set; }

	/// <summary>Field of view for everything the orbit rig drives — sculpt/edit mode and the prop's
	/// third-person play camera — vertical degrees.</summary>
	public static float OrbitFov { get; set; } = 60f;
}
