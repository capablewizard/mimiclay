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

	/// <summary>The circle the mimic's WHOLE prop stays inside — body and clay. The manager sets it as the
	/// soft leash on the prop's <see cref="HiderController"/>: movement keeps the collider's footprint inside
	/// it, and <see cref="BrushWorldClamp"/> stops additive brushes being sculpted past it, so a wide sculpt
	/// has less room to walk and nothing ever overhangs. The fence ring is crowd-only and never touches the
	/// prop; keep this a little inside the fence's inner face. Drawn as a cyan circle in the editor.</summary>
	[Property, Group( "Markers" )] public float StageRadius { get; set; } = 90f;

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

	// Editor preview of StageRadius at the mimic spot's height, so the leash circle can be tuned by eye
	// against the plinth edge and the fence ring (the fence draws its own walls).
	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		var bright = Gizmo.IsSelected || Gizmo.IsHovered;
		Gizmo.Draw.Color = Color.Cyan.WithAlpha( bright ? 0.9f : 0.3f );
		float z = MimicSpot.IsValid() ? WorldTransform.PointToLocal( MimicSpot.WorldPosition ).z : 0f;
		Gizmo.Draw.LineCircle( Vector3.Up * z, Vector3.Up, StageRadius, sections: 48 );
	}
}
