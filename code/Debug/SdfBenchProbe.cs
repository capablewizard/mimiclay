using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>Read-only diagnostics for the SDF development scene and the editor benchmark (Editor/SdfBench.cs).
/// Never runs in normal gameplay.</summary>
public static class SdfBenchProbe
{
	public static BBox GeometryBounds( GameObject go )
	{
		bool first = true;
		BBox result = default;
		foreach ( var s in go.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( !Sdf.TryGetBounds( s.Brushes, out var b ) ) continue;
			for ( int i = 0; i < 8; i++ )
			{
				var local = new Vector3( (i & 1) == 0 ? b.Mins.x : b.Maxs.x, (i & 2) == 0 ? b.Mins.y : b.Maxs.y, (i & 4) == 0 ? b.Mins.z : b.Maxs.z );
				var point = s.WorldTransform.PointToWorld( local );
				result = first ? new BBox( point, point ) : result.AddPoint( point );
				first = false;
			}
		}
		return result;
	}

	public static object Snapshot( Scene scene, bool perRenderer = true )
	{
		var rs = scene.GetAllComponents<SdfRaymarchRenderer>().Where( r => r.Active ).ToArray();
		var fields = rs.Select( r => r.DebugField ).Where( f => f is not null ).Distinct().ToArray();
		return new
		{
			Renderers = rs.Length,
			Pending = rs.Count( r => r.FieldPending ),
			Ready = rs.Count( r => r.DebugFieldReady ),
			UniqueFields = fields.Length,
			FieldMiB = Mib( fields.Sum( f => f.EstimatedBytes ) ),
			LocalMiB = Mib( rs.Sum( r => r.LocalResourceBytes ) ),
			CountedMiB = Mib( fields.Sum( f => f.EstimatedBytes ) + rs.Sum( r => r.LocalResourceBytes ) ),
			GlobalCacheMiB = Mib( SdfFieldCache.ResidentBytes ),
			BuildsInFlight = SdfSculpture.BuildsInFlight,
			Bands = Bands( scene ),
			Fields = perRenderer ? rs.Select( r => new { Name = r.GameObject.Name, Bytes = r.FieldVramBytes,
				Dimensions = r.DebugFieldDims.ToString(), Tier = r.DebugFieldTier.ToString(),
				Sparse = r.DebugFieldSparse, Ready = r.DebugFieldReady, Band = r.DebugSwitchState } ).ToArray() : null
		};
	}

	static readonly string[] BandNames = { "sdf", "mesh0", "mesh1", "mesh2", "culled" };

	/// <summary>How many renderers sit in each distance band, plus how many march their own shadow.
	/// A renderer with DistanceSwitching off is always in the sdf band.</summary>
	public static Dictionary<string, int> Bands( Scene scene )
	{
		var result = new Dictionary<string, int>();
		foreach ( var r in scene.GetAllComponents<SdfRaymarchRenderer>() )
		{
			if ( !r.Active ) continue;
			var band = r.DistanceSwitching ? BandNames[System.Math.Clamp( r.DebugSwitchState, 0, 4 )] : "sdf";
			result[band] = result.GetValueOrDefault( band ) + 1;
			if ( r.DebugMarchedShadows ) result["marchedShadows"] = result.GetValueOrDefault( "marchedShadows" ) + 1;
			// A mesh band with no built mesh draws nothing — a LOD "saving" that is really a missing prop. Include
			// disabled renderers: the raymarch switches its mesh off whenever it isn't needed, which is not "missing".
			if ( r.DistanceSwitching && r.Components.Get<ModelRenderer>( FindMode.EverythingInSelf )?.Model is null )
				result["noMesh"] = result.GetValueOrDefault( "noMesh" ) + 1;
		}
		return result;
	}

	/// <summary>Main-thread ms spent in every SDF renderer's OnUpdate since the last call (CPU side of LOD work).</summary>
	public static double TakeRendererUpdateMs() => SdfRaymarchRenderer.TakeUpdateMs();

	/// <summary>Counted SDF texture payload and unique field count — cheap enough to take per benchmark case.</summary>
	public static (double CountedMiB, int UniqueFields) Residency( Scene scene )
	{
		var rs = scene.GetAllComponents<SdfRaymarchRenderer>().Where( r => r.Active ).ToArray();
		var fields = rs.Select( r => r.DebugField ).Where( f => f is not null ).Distinct().ToArray();
		return (Mib( fields.Sum( f => f.EstimatedBytes ) + rs.Sum( r => r.LocalResourceBytes ) ), fields.Length);
	}

	public static bool Ready( Scene scene ) => SdfSculpture.BuildsInFlight == 0
		&& !scene.GetAllComponents<SdfRaymarchRenderer>().Any( r => r.Active && r.FieldPending );

	static double Mib( long bytes ) => System.Math.Round( bytes / 1048576.0, 1 );
}
