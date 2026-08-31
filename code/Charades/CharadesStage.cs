using System.Linq;

namespace Mimiclay;

/// <summary>
/// The charades stage — sits on the stage prefab's root, so a charades MAP is just "a map with this prefab
/// placed in it". Scene-placed, so it (and its prefab/marker refs) exists on every machine; the NetworkSpawn'd
/// <see cref="CharadesManager"/> reads everything stage-shaped through <see cref="Current"/>, the same live-read
/// arrangement as <see cref="RoundManagerSpawner"/>'s pawn prefabs.
///
/// The prefab itself is a raised clay plinth: its colliders are what physically keeps the guessing crowd from
/// walking through the mimic's spot (they can't jump up it either — it's taller than the jump apex). The mimic
/// doesn't need a door: the manager TELEPORTS their pawn onto <see cref="MimicSpot"/> when their turn starts,
/// so nobody ever has to path onto the stage.
/// </summary>
[Title( "Charades Stage" )]
[Category( "Mimiclay" )]
[Icon( "theater_comedy" )]
public sealed class CharadesStage : Component
{
	/// <summary>The scene's stage (null on maps without one — which no charades map should be).</summary>
	public static CharadesStage Current { get; private set; }

	/// <summary>UNUSED since the mimic became a prop pawn sculpting its own disguise — kept wired for a
	/// possible future canvas-style variant (a fixed easel the sculptor doesn't wear).</summary>
	[Property, Group( "Prefabs" )] public GameObject CanvasPrefab { get; set; }

	/// <summary>UNUSED (see <see cref="CanvasPrefab"/>) — where a fixed canvas would appear.</summary>
	[Property, Group( "Markers" )] public GameObject CanvasSpot { get; set; }

	/// <summary>Where the mimic's PROP pawn spawns for their turn — a marker on the stage floor.</summary>
	[Property, Group( "Markers" )] public GameObject MimicSpot { get; set; }

	/// <summary>How far from the stage centre the mimic's prop may wander — the manager's owner-side leash,
	/// backstopping the fence colliders (the prop must stay ON stage exactly as hard as the crowd stays off).
	/// Keep it a touch inside the fence ring's radius.</summary>
	[Property, Group( "Markers" )] public float StageRadius { get; set; } = 60f;

	protected override void OnEnabled() => Current = this;

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
	}

	/// <summary>Where the canvas spawns: the marker, or the stage root when unwired.</summary>
	public Transform CanvasTransform
		=> CanvasSpot.IsValid() ? CanvasSpot.WorldTransform : WorldTransform;

	/// <summary>Where the mimic's pawn stands: the marker, or just above the stage root when unwired.</summary>
	public Transform MimicTransform
		=> MimicSpot.IsValid() ? MimicSpot.WorldTransform : WorldTransform.WithPosition( WorldPosition + Vector3.Up * 16f );

	/// <summary>The scene's stage, looked up fresh (for callers running before OnEnabled ordering settles).</summary>
	public static CharadesStage FindIn( Scene scene )
		=> Current.IsValid() ? Current : scene?.GetAllComponents<CharadesStage>().FirstOrDefault();
}
