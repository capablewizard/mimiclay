namespace Mimiclay;

/// <summary>
/// The one shared piece of "this pawn has an editable sculpture" wiring. Both pawn controllers (hider disguise,
/// hunter face) stand up the same pair — a <see cref="SculptEditSession"/> for authoring and the prefab's
/// <see cref="SdfNetworkSync"/> for replication — and the invariant that matters is that BOTH point at the SAME
/// sculpture: a future pawn that wired the session but forgot the sync would sculpt a shape nobody else sees.
/// Everything else about the two pawns (orbit rig configuration, movement freezing, camera ownership) is
/// genuinely different and stays in the controllers.
/// </summary>
public static class SculptablePawn
{
	/// <summary>Create the edit session on <paramref name="pawn"/> and bind it AND the pawn's network sync to
	/// <paramref name="target"/>. Pass <paramref name="orbit"/> when the session should own the camera while
	/// editing (the hunter's face edit); the hider leaves it null and drives its own always-on rig.</summary>
	public static SculptEditSession AttachEditing( Component pawn, SdfSculpture target, OrbitCameraController orbit = null )
	{
		var session = pawn.Components.Create<SculptEditSession>();
		session.Target = target;
		session.OrbitCamera = orbit;

		var sync = pawn.Components.GetOrCreate<SdfNetworkSync>();
		sync.Target = target;

		return session;
	}
}
