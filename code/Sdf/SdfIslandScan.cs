using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Finds the disconnected pieces ("islands") of a sculpture's clay — what's left when shot craters cut all the way
/// through. Samples the folded field on a coarse grid over the shape's bounds, groups the solid cells into
/// 26-connected components, and reports the biggest as the MAIN body and every other one as an island with a
/// cutting box that provably misses the main body (see <see cref="Island.Safe"/>).
///
/// PURE: no engine state, no scene access — it only reads the brush list it's handed. Built to run on a worker
/// thread (RoundManager.ShotProps): the caller snapshots (copies + flattens) the brushes on the main thread first.
/// Deterministic for a given brush list, though only the host runs it; machines receive its RESULT as brushes.
/// </summary>
public static class SdfIslandScan
{
	/// <summary>Grid budget: total cells ≈ this, cells roughly cubic, each axis capped at <see cref="MaxPerAxis"/>.</summary>
	public const int TargetCells = 8192;
	public const int MaxPerAxis = 40;

	public readonly struct Island
	{
		/// <summary>Sculpture-local cutting box: the island's cells grown by one cell every side.</summary>
		public readonly Vector3 Center, HalfExtents;
		/// <summary>Sampled clay volume (sculpture units³).</summary>
		public readonly float Volume;
		/// <summary>No main-body cell lies inside <see cref="Center"/>/<see cref="HalfExtents"/>' box — cutting it
		/// can't bite the body. False when the pieces interleave (an L hooked around the body, a thin neck the grid
		/// split): such an island is left alone.</summary>
		public readonly bool Safe;

		public Island( Vector3 center, Vector3 half, float volume, bool safe )
		{
			Center = center; HalfExtents = half; Volume = volume; Safe = safe;
		}
	}

	public sealed class Result
	{
		/// <summary>The sculpture-local bounds the grid covered (the whole shape).</summary>
		public BBox Bounds;
		public float MainVolume;
		public float TotalVolume;
		public readonly List<Island> Islands = new();
	}

	/// <summary>Scan <paramref name="flat"/> (already flattened — no groups — and owned by the caller's thread)
	/// over <paramref name="bounds"/> (sculpture-local).</summary>
	public static Result Scan( List<SdfBrush> flat, BBox bounds )
	{
		var result = new Result { Bounds = bounds };
		if ( !Grid( bounds, out int nx, out int ny, out int nz, out var cell ) )
			return result;

		float cellVol = cell.x * cell.y * cell.z;
		int count = nx * ny * nz;
		var label = new int[count]; // 0 = empty, -1 = solid unlabelled, >0 = component id

		for ( int z = 0; z < nz; z++ )
		for ( int y = 0; y < ny; y++ )
		for ( int x = 0; x < nx; x++ )
		{
			var p = bounds.Mins + new Vector3( (x + 0.5f) * cell.x, (y + 0.5f) * cell.y, (z + 0.5f) * cell.z );
			if ( Sdf.Sample( flat, p ) <= 0f )
				label[x + nx * (y + ny * z)] = -1;
		}

		// Flood fill, 26-connected: diagonal neighbours join, so a piece only counts as an island when it's
		// genuinely clear of the body by a whole cell — the conservative side of a coarse grid.
		var sizes = new List<int> { 0 };
		var mins = new List<(int, int, int)> { default };
		var maxs = new List<(int, int, int)> { default };
		var stack = new Stack<int>();
		for ( int i = 0; i < count; i++ )
		{
			if ( label[i] != -1 )
				continue;

			int id = sizes.Count;
			int size = 0;
			(int x, int y, int z) lo = (int.MaxValue, int.MaxValue, int.MaxValue), hi = (-1, -1, -1);
			label[i] = id;
			stack.Push( i );
			while ( stack.Count > 0 )
			{
				int c = stack.Pop();
				size++;
				int cx = c % nx, cy = (c / nx) % ny, cz = c / (nx * ny);
				lo = (Math.Min( lo.x, cx ), Math.Min( lo.y, cy ), Math.Min( lo.z, cz ));
				hi = (Math.Max( hi.x, cx ), Math.Max( hi.y, cy ), Math.Max( hi.z, cz ));

				for ( int dz = -1; dz <= 1; dz++ )
				for ( int dy = -1; dy <= 1; dy++ )
				for ( int dx = -1; dx <= 1; dx++ )
				{
					int ax = cx + dx, ay = cy + dy, az = cz + dz;
					if ( ax < 0 || ay < 0 || az < 0 || ax >= nx || ay >= ny || az >= nz )
						continue;
					int n = ax + nx * (ay + ny * az);
					if ( label[n] != -1 )
						continue;
					label[n] = id;
					stack.Push( n );
				}
			}
			sizes.Add( size );
			mins.Add( lo );
			maxs.Add( hi );
		}

		int main = 0;
		for ( int id = 1; id < sizes.Count; id++ )
		{
			result.TotalVolume += sizes[id] * cellVol;
			if ( main == 0 || sizes[id] > sizes[main] )
				main = id;
		}
		if ( main == 0 )
			return result;
		result.MainVolume = sizes[main] * cellVol;

		for ( int id = 1; id < sizes.Count; id++ )
		{
			if ( id == main )
				continue;

			// Grow by one cell: the clay reaches up to a cell past the solid cell centres the grid saw.
			var (lx, ly, lz) = mins[id];
			var (hx, hy, hz) = maxs[id];
			lx--; ly--; lz--; hx++; hy++; hz++;

			bool safe = true;
			for ( int z = Math.Max( lz, 0 ); safe && z <= Math.Min( hz, nz - 1 ); z++ )
			for ( int y = Math.Max( ly, 0 ); safe && y <= Math.Min( hy, ny - 1 ); y++ )
			for ( int x = Math.Max( lx, 0 ); x <= Math.Min( hx, nx - 1 ); x++ )
			{
				if ( label[x + nx * (y + ny * z)] == main )
				{
					safe = false;
					break;
				}
			}

			var boxMin = bounds.Mins + new Vector3( lx * cell.x, ly * cell.y, lz * cell.z );
			var boxMax = bounds.Mins + new Vector3( (hx + 1) * cell.x, (hy + 1) * cell.y, (hz + 1) * cell.z );
			result.Islands.Add( new Island( (boxMin + boxMax) * 0.5f, (boxMax - boxMin) * 0.5f, sizes[id] * cellVol, safe ) );
		}

		return result;
	}

	/// <summary>Sampled solid volume of <paramref name="flat"/> over <paramref name="bounds"/>, on the same grid budget.</summary>
	public static float Volume( List<SdfBrush> flat, BBox bounds )
	{
		if ( !Grid( bounds, out int nx, out int ny, out int nz, out var cell ) )
			return 0f;

		int solid = 0;
		for ( int z = 0; z < nz; z++ )
		for ( int y = 0; y < ny; y++ )
		for ( int x = 0; x < nx; x++ )
			if ( Sdf.Sample( flat, bounds.Mins + new Vector3( (x + 0.5f) * cell.x, (y + 0.5f) * cell.y, (z + 0.5f) * cell.z ) ) <= 0f )
				solid++;
		return solid * cell.x * cell.y * cell.z;
	}

	static bool Grid( BBox bounds, out int nx, out int ny, out int nz, out Vector3 cell )
	{
		var size = bounds.Size;
		nx = ny = nz = 0;
		cell = default;
		if ( size.x <= 0f || size.y <= 0f || size.z <= 0f )
			return false;

		float scale = MathF.Cbrt( TargetCells / (size.x * size.y * size.z) );
		nx = Math.Clamp( (int)MathF.Round( size.x * scale ), 2, MaxPerAxis );
		ny = Math.Clamp( (int)MathF.Round( size.y * scale ), 2, MaxPerAxis );
		nz = Math.Clamp( (int)MathF.Round( size.z * scale ), 2, MaxPerAxis );
		cell = new Vector3( size.x / nx, size.y / ny, size.z / nz );
		return true;
	}
}
