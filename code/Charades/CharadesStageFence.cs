using System;

namespace Mimiclay;

/// <summary>
/// The stage's invisible fence: a hollow ring of box-collider walls built at runtime around this object.
/// Hollow on purpose — a single solid blocker (the first cut was a big capsule) can't do the job, because the
/// mimic's prop pawn SPAWNS inside the stage: inside a solid collider it's in permanent penetration and the
/// solver shoves it out (or through the floor). A ring of walls is symmetric by construction: the crowd
/// outside can't push in, the prop inside can't walk out, and the interior is genuinely empty space to stand
/// in. <see cref="CharadesManager"/> adds an owner-side leash on top for anything physics lets slip.
///
/// Walls are generated in OnStart (play only) as NotSaved children, so the prefab/scene never accumulates
/// them and the ring re-tunes from these properties alone.
/// </summary>
[Title( "Charades Stage Fence" )]
[Category( "Mimiclay" )]
[Icon( "fence" )]
public sealed class CharadesStageFence : Component
{
	/// <summary>Tag stamped on every fence wall so traces that must see through the INVISIBLE ring can
	/// exclude it — both camera booms skip it (a camera pulled in by a wall that isn't there reads as a
	/// glitch, not an obstruction). Pawn movement still collides; that's the fence's whole job.</summary>
	public const string WallTag = "stagefence";

	/// <summary>Ring radius — wall centres sit on this circle. Keep <see cref="CharadesStage.StageRadius"/>
	/// (the manager's leash) a touch inside it.</summary>
	[Property] public float Radius { get; set; } = 68f;

	/// <summary>How many wall segments make the ring. Each is widened to overlap its neighbours, so the ring
	/// is gap-free at any count ≥ 3.</summary>
	[Property, Range( 3, 24 )] public int Segments { get; set; } = 12;

	/// <summary>Wall bottom, local Z — a little below the floor so nothing slides under.</summary>
	[Property] public float Bottom { get; set; } = -40f;

	/// <summary>Wall top, local Z — comfortably above the jump apex (56) so nobody hops the fence
	/// in either direction.</summary>
	[Property] public float Top { get; set; } = 150f;

	/// <summary>Wall thickness (radially).</summary>
	[Property] public float Thickness { get; set; } = 8f;

	protected override void OnStart()
	{
		if ( Scene.IsEditor )
			return; // play-time geometry only — never bake wall objects into an asset

		for ( var i = 0; i < WallCount; i++ )
		{
			WallLayout( i, out var localPos, out var localRot, out var size );

			var wall = new GameObject( true, $"Fence Wall {i}" );
			wall.Flags |= GameObjectFlags.NotSaved;
			wall.SetParent( GameObject, false );
			wall.Tags.Add( WallTag );
			wall.LocalPosition = localPos;
			wall.LocalRotation = localRot;

			var box = wall.Components.Create<BoxCollider>();
			box.Center = Vector3.Zero;
			box.Scale = size;
			box.Static = true;
		}
	}

	// One source of truth for where each wall goes — OnStart builds the colliders from it and DrawGizmos
	// previews from it, so what the editor shows IS what play mode collides with.
	int WallCount => Math.Max( 3, Segments );

	void WallLayout( int index, out Vector3 localPos, out Rotation localRot, out Vector3 size )
	{
		var segments = WallCount;
		var height = MathF.Max( 8f, Top - Bottom );
		var centreZ = Bottom + height * 0.5f;

		// Chord for one segment, padded 25% so neighbouring boxes overlap instead of leaving seam gaps.
		var step = MathF.Tau / segments;
		var width = 2f * Radius * MathF.Sin( step * 0.5f ) * 1.25f;

		var yaw = step * index;
		localPos = new Vector3( MathF.Cos( yaw ) * Radius, MathF.Sin( yaw ) * Radius, centreZ );
		localRot = Rotation.FromYaw( yaw.RadianToDegree() );
		size = new Vector3( Thickness, width, height );
	}

	// Editor preview: the exact wall boxes the runtime will build, faint until the fence is selected —
	// there's nothing else to see (the walls are invisible runtime objects), so without this the ring is
	// unplaceable by eye.
	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		var bright = Gizmo.IsSelected || Gizmo.IsHovered;
		Gizmo.Draw.Color = Color.Cyan.WithAlpha( bright ? 0.85f : 0.25f );

		// Gizmos draw in this component's LOCAL space already; each wall gets its own scoped transform on
		// top so a plain axis-aligned box outline lands rotated into place.
		for ( var i = 0; i < WallCount; i++ )
		{
			WallLayout( i, out var localPos, out var localRot, out var size );

			using ( Gizmo.Scope( $"fence-wall-{i}", new Transform( localPos, localRot ) ) )
				Gizmo.Draw.LineBBox( BBox.FromPositionAndSize( Vector3.Zero, size ) );
		}
	}
}
