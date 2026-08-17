using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// A marker you drop in a map scene to say "pawns can appear here". <see cref="HunterStart"/> distinguishes the
/// hunters' return point (where they're sent at the start of the hunt) from the scattered hide spots props are
/// seeded at. <see cref="RoundManager"/> gathers these at round start; if a scene has none it falls back to the
/// manager's own GameObject, so an unprepared scene still spawns everyone (just stacked).
/// </summary>
[Title( "Round Spawn Point" )]
[Category( "Mimiclay" )]
[Icon( "place" )]
public sealed class RoundSpawnPoint : Component
{
	/// <summary>True = a hunter spawn/return point. False = a prop hide spot.</summary>
	[Property] public bool HunterStart { get; set; }

	/// <summary>All spawn points of a given kind in the active scene. Enabled-only, so a designer can toggle spots.
	/// Ordered by GameObject id: every machine spawns its OWN pawn by indexing into this list, and scene enumeration
	/// order isn't a cross-machine guarantee — without a deterministic sort two players could resolve the same index
	/// to different markers (or, worse, different indexes to the same marker) and spawn inside each other.</summary>
	public static List<RoundSpawnPoint> AllOfKind( Scene scene, bool hunterStart )
		=> scene.GetAllComponents<RoundSpawnPoint>()
			.Where( s => s.IsValid() && s.HunterStart == hunterStart )
			.OrderBy( s => s.GameObject.Id )
			.ToList();

	/// <summary>Horizontal offset for the <paramref name="stack"/>th pawn assigned to the SAME spot (0 = takes the
	/// spot exactly). Two pawns spawning inside each other get shoved apart by the physics solver — sometimes through
	/// the floor — so when spots run out we ring the extras around the marker instead: 8 per ring, 48 units apart,
	/// each ring twisted half a step so pawns never share a position no matter how far the count wraps.</summary>
	public static Vector3 StackOffset( int stack )
	{
		if ( stack <= 0 )
			return Vector3.Zero;

		var ring = (stack - 1) / 8 + 1;
		var slot = (stack - 1) % 8;
		var yaw = slot * 45f + (ring - 1) * 22.5f;
		return Rotation.FromYaw( yaw ).Forward * (48f * ring);
	}
	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		var spawnpointModel = Model.Load( "models/editor/spawnpoint.vmdl" );
		Gizmo.Draw.Color = Color.Green;
		Gizmo.Hitbox.Model( spawnpointModel );
		Gizmo.Draw.Color = Gizmo.Draw.Color.WithAlpha( (Gizmo.IsHovered || Gizmo.IsSelected) ? 0.7f : 0.5f );
		var so = Gizmo.Draw.Model( spawnpointModel );
		if ( so is not null )
		{
			so.Flags.CastShadows = true;
		}
	}

}
