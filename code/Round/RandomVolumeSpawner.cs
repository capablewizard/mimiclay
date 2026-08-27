using System;

namespace Mimiclay;

/// <summary>
/// The "spawn anywhere in a region" sibling of <see cref="RandomPropSpawner"/>: instead of always landing at
/// this exact spot, up to <see cref="Count"/> picks land at random points somewhere inside <see cref="Bounds"/>
/// — a local-space box (rotating/scaling this GameObject moves and reshapes the region with it, same as
/// <c>IndirectLightVolume</c>'s own <c>Bounds</c>). Everything else — prefab picking, ground-alignment,
/// collision avoidance (both between this volume's own scattered props AND against other spawners of either
/// kind), networking, live editor preview — is inherited unchanged from <see cref="PropSpawnerBase"/>; this
/// only overrides <see cref="RollSlotCount"/> (how many to scatter), <see cref="RollLocalOffset"/> (where
/// within the region each one can land) and the extra region gizmo.
/// </summary>
[Title( "Random Volume Spawner" )]
[Category( "Mimiclay" )]
[Icon( "casino" )]
[EditorHandle( "materials/gizmo/randomvolumespawner.png" )]
public sealed class RandomVolumeSpawner : PropSpawnerBase
{
	/// <summary>Local-space box a pick can land anywhere inside. Not necessarily centred on this GameObject —
	/// each face can be dragged independently in the editor (see <see cref="DrawRegionGizmo"/>), same as
	/// <c>IndirectLightVolume.Bounds</c>.</summary>
	[Property] public BBox Bounds { get; set; } = BBox.FromPositionAndSize( Vector3.Zero, new Vector3( 256f, 256f, 128f ) );

	/// <summary>How many props to scatter through the volume per decide. Each slot is picked/placed/collision-
	/// checked independently (see <see cref="PropSpawnerBase.Decide"/>), so some can still land empty per
	/// <see cref="PropSpawnerBase.NoneChance"/> even with a high count.</summary>
	[Property, Range( 1, 64 )] public int Count { get; set; } = 5;

	protected override string Label => "Random Volume Prop";

	protected override int RollSlotCount() => Count;

	protected override Vector3 RollLocalOffset()
	{
		var b = Bounds;
		float Axis( float min, float max ) => min >= max ? min : (float)(min + Random.Shared.NextDouble() * (max - min));
		return new Vector3( Axis( b.Mins.x, b.Maxs.x ), Axis( b.Mins.y, b.Maxs.y ), Axis( b.Mins.z, b.Maxs.z ) );
	}

	// Bounds/Count both need to be part of the "should the live preview re-roll" hash too — resizing the
	// region or changing how many props scatter through it are real config changes, same as editing
	// Prefabs/NoneChance/RandomizeRotation in the base class.
	protected override int ComputeConfigHash()
	{
		var hash = new HashCode();
		hash.Add( base.ComputeConfigHash() );
		hash.Add( Bounds.Mins );
		hash.Add( Bounds.Maxs );
		hash.Add( Count );
		return hash.ToHashCode();
	}

	// Same pattern as IndirectLightVolume.DrawGizmos: Gizmo.Control.BoundingBox gives real per-face drag
	// handles while selected — much easier to size a region by eye than typing numbers into the inspector.
	// Falls back to a plain (non-interactive) outline otherwise, so the region is still visible/findable
	// without already being selected — same reasoning as the always-on EditorHandle icon.
	protected override void DrawRegionGizmo()
	{
		if ( Gizmo.IsSelected )
		{
			var bounds = Bounds;
			Gizmo.Control.BoundingBox( "Bounds", bounds, out bounds, out bool pressed );
			Bounds = bounds;

			// Don't re-roll/rebuild every single frame of an active resize drag — Bounds feeds
			// ComputeConfigHash, so without this the live preview would tear down and re-scatter every prop
			// on every frame the box is being dragged. See SuppressPreviewWhileDragging's doc. BoundingBox
			// drives its per-face handles as internal sub-controls, so the plain Gizmo.Pressed.This (which
			// only reflects THIS exact control scope) never actually went true — the control's own
			// "outPressed" is the real signal for "one of my handles is currently held".
			SuppressPreviewWhileDragging = pressed;

			Gizmo.Draw.Color = Color.Yellow.WithAlpha( 0.6f );
			Gizmo.Draw.LineBBox( bounds );
		}
		else
		{
			SuppressPreviewWhileDragging = false;
			Gizmo.Draw.Color = Color.Yellow.WithAlpha( Gizmo.IsHovered ? 0.4f : 0.2f );
			Gizmo.Draw.LineBBox( Bounds );
		}

	}
}
