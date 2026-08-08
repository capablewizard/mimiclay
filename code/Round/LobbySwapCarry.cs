namespace Mimiclay;

/// <summary>
/// The OWNER-side half of lobby role-swap continuity. The host restores what it can see — body shapes and the
/// prop's facing ride <c>LobbyManager._swapMemory</c> into the spawn itself — but the CAMERA is per-machine
/// state the host never has: its pitch, and the prop orbit's zoom, exist only on the machine that owns them.
/// So the P press stashes them here, and the same machine's replacement pawn consumes them as it starts.
///
/// Two different lifetimes, deliberately:
/// <list type="bullet">
/// <item>The camera angles are CONTINUITY — consumed exactly once (<see cref="TakeCamera"/>), by whichever
/// pawn spawns next, so the view direction survives the swap (looking at a player as a prop → the hunter
/// comes up looking at them). Never reused: a stale view re-applied to some later spawn would yank the
/// camera.</item>
/// <item><see cref="PropZoom"/> is PER-ROLE MEMORY — written when leaving the prop, applied on EVERY return
/// to it (the hunter has no zoom to overwrite it with), so your framing comes back with your prop.</item>
/// </list>
/// </summary>
internal static class LobbySwapCarry
{
	/// <summary>Camera orientation at the moment of the swap press. Take with <see cref="TakeCamera"/>.</summary>
	static Angles? _cameraAngles;

	/// <summary>Orbit distance the player last had as a prop. Read directly; overwritten on each swap away.</summary>
	public static float? PropZoom;

	/// <summary>Called at the P press, before the swap request goes out. <paramref name="ownProp"/> is the
	/// caller's hider pawn if they're currently a prop (its zoom is about to be destroyed with it) — null
	/// when swapping away from hunter, which has no zoom worth keeping.</summary>
	public static void Capture( Scene scene, HiderController ownProp )
	{
		var cam = scene?.Camera;
		if ( cam.IsValid() )
			_cameraAngles = cam.WorldRotation.Angles();

		if ( ownProp.IsValid() )
			PropZoom = ownProp.OrbitDistance;
	}

	/// <summary>The carried view, at most once — the first pawn to start eats it.</summary>
	public static Angles? TakeCamera()
	{
		var angles = _cameraAngles;
		_cameraAngles = null;
		return angles;
	}
}
