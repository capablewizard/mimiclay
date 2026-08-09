using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Sculpt-volume → physics mass, shared by every grabbable body in the game. The engine's automatic mass
/// (summing collision shapes) is wrong for us twice over: our compound colliders OVERLAP by construction —
/// a spline's swept spheres overlap at every step, so a rope coil would auto-mass like an anvil — and the
/// carve-aware voxel path changes the shape set without changing the clay. So mass comes from the honest
/// clay volume instead: the same coarse field sampling <see cref="SculptBounds"/> uses for the min-size
/// check (cell centres of a grid over the bounds, solid where the folded field is negative), times one
/// shared density.
///
/// ONE formula for everything — map props, player disguises, decoys — deliberately: if player props massed
/// differently from the identical map prop, a hunter could weigh-test for players, the exact tell the
/// disguise exists to deny.
/// </summary>
public static class PropMass
{
	/// <summary>Mass per sculpture unit³ of clay. Tuned so a hand prop (ball, mug ~10–20k units³) lands in
	/// single digits and lifts freely, while furniture (~200k+ units³) climbs high enough that the grab
	/// spring's fixed force budget (<see cref="PropGrabber.HoldForce"/>) can only drag it.</summary>
	public const float Density = 0.0004f;

	/// <summary>Floor so a near-empty sculpt still simulates sanely.</summary>
	public const float MinMass = 1f;

	/// <summary>Ceiling so architecture-scale sculpts can't produce absurd solver ratios.</summary>
	public const float MaxMass = 2000f;

	// Coarser than SculptBounds' validity grid (6 vs 4 unit cells): mass tolerates a few percent of noise,
	// and this runs per sculpture at conversion + every commit, so keep it cheap. No early-outs — unlike the
	// bounds check this wants the actual number, not a threshold verdict.
	const float CellTarget = 6f;
	const int MaxCellsPerAxis = 24;

	/// <summary>Clay volume (sculpture-local units³) of a brush list, by coarse field sampling. 0 for an
	/// empty/degenerate list.</summary>
	public static float MeasureVolume( List<SdfBrush> brushes )
	{
		if ( brushes is not { Count: > 0 } || !Sdf.TryGetBounds( brushes, out var bb ) )
			return 0f;

		var span = bb.Maxs - bb.Mins;
		int nx = Math.Clamp( (int)MathF.Ceiling( span.x / CellTarget ), 2, MaxCellsPerAxis );
		int ny = Math.Clamp( (int)MathF.Ceiling( span.y / CellTarget ), 2, MaxCellsPerAxis );
		int nz = Math.Clamp( (int)MathF.Ceiling( span.z / CellTarget ), 2, MaxCellsPerAxis );
		var cell = new Vector3( span.x / nx, span.y / ny, span.z / nz );
		double cellVol = (double)cell.x * cell.y * cell.z;

		int solid = 0;
		for ( int iz = 0; iz < nz; iz++ )
		for ( int iy = 0; iy < ny; iy++ )
		for ( int ix = 0; ix < nx; ix++ )
		{
			var p = new Vector3(
				bb.Mins.x + (ix + 0.5f) * cell.x,
				bb.Mins.y + (iy + 0.5f) * cell.y,
				bb.Mins.z + (iz + 0.5f) * cell.z );
			if ( Sdf.Sample( brushes, p ) < 0f )
				solid++;
		}

		return (float)(solid * cellVol);
	}

	/// <summary>Mass of one sculpture, world scale folded in (volume scales with the cube). Unclamped —
	/// multi-sculpture props sum parts first and clamp the total (see <see cref="Clamp"/>).</summary>
	public static float MassOf( SdfSculpture sculpture )
	{
		if ( !sculpture.IsValid() )
			return 0f;

		var s = sculpture.WorldScale;
		return MeasureVolume( sculpture.Brushes ) * MathF.Abs( s.x * s.y * s.z ) * Density;
	}

	/// <summary>The shared clamp every consumer must apply to a summed mass.</summary>
	public static float Clamp( float mass ) => Math.Clamp( mass, MinMass, MaxMass );
}
