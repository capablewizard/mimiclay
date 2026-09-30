using System;

namespace Mimiclay;

/// <summary>
/// Game-wide tunables destined for a user-facing settings menu. Single source of truth: read these at the
/// point of use, every frame, rather than copying them — that way a future settings screen just writes the
/// fields and the change takes effect live. When the menu lands, persist them as JSON via FileSystem.Data
/// (same shape as the sculpt saves).
///
/// FOV: every value here is HORIZONTAL degrees (every scene camera is FovAxis Horizontal, and that's the axis
/// the engine's own default_fov preference is expressed in). Every camera runs at the player's s&box
/// graphics-menu FOV (<see cref="Preferences.FieldOfView"/>, read-only to games); the per-mode names stay so a
/// mode can diverge again without touching its call sites. Orbit-rig framing (edit, prop play cam, customiser,
/// tutorial) is authored at <see cref="ReferenceFov"/> and dolly-zoomed to the live FOV by
/// OrbitCameraController.Reach, so it's equally tight at any preference. The gun's viewmodel runs its own FOV
/// (HunterGun.ViewmodelFov) and is already independent of all of these.
/// </summary>
public static class GameSettings
{
	/// <summary>The player's s&box FOV preference, clamped to the engine's own convar range.</summary>
	public static float PreferredFov => Math.Clamp( Preferences.FieldOfView, 40f, 120f );

	/// <summary>Hunter camera, first AND third person.</summary>
	public static float HunterFov => PreferredFov;

	/// <summary>Look-around views: the caught player's spectator cam and the hider's free cam.</summary>
	public static float SpectateFov => PreferredFov;

	/// <summary>The prop's third-person play camera.</summary>
	public static float PropPlayFov => PreferredFov;

	/// <summary>Everything the orbit rig drives while sculpting — head/gun edit, creative, the menu
	/// customiser, the tutorial.</summary>
	public static float EditFov => PreferredFov;

	/// <summary>The FOV every orbit-rig distance (framing, zoom limits, prop camera distance, saved views) was
	/// tuned at. The rig scales its real boom by tan(ref/2)/tan(live/2), so those numbers keep meaning the
	/// same on-screen framing at any FOV.</summary>
	public const float ReferenceFov = 60f;

	/// <summary>Hunter camera in third person (over-the-shoulder boom) instead of first person. Toggled in
	/// game with the "View" action; static so the choice survives pawn respawns (prop→hunter conversion
	/// spawns a fresh pawn) and scene changes.</summary>
	public static bool HunterThirdPerson { get; set; }

	/// <summary>
	/// Orbit-rig distance (reference-FOV units, see <see cref="ReferenceFov"/>) that frames a sphere of
	/// <paramref name="radius"/> with <paramref name="margin"/> breathing room: r·margin / sin(ref/2). The
	/// margins in the prefabs/scenes were tuned against exactly this, so it's kept as-is — the rig's dolly
	/// factor carries it to whatever FOV is live.
	/// </summary>
	public static float EditFitDistance( float radius, float margin )
		=> radius * margin / MathF.Sin( ReferenceFov.DegreeToRadian() * 0.5f );
}
