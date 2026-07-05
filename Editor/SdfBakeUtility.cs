using System;
using System.IO;
using Mimiclay;

namespace Editor;

/// <summary>
/// Editor-only baking of an <see cref="SdfSculpture"/> to a reusable <c>.sdfmesh</c> asset, so shipped
/// levels load the geometry instead of re-meshing the SDF. Writing the asset needs the editor assembly,
/// so this lives here and is wired to the component via <see cref="SdfSculpture.BakeHandler"/> by the tool.
/// </summary>
public static class SdfBakeUtility
{
	public static bool Bake( SdfSculpture sculpt )
	{
		var brushes = sculpt?.Brushes;
		if ( brushes is not { Count: > 0 } )
		{
			Log.Warning( "[SDF Bake] nothing to bake." );
			return false;
		}

		int res = sculpt.Resolution;
		bool flip = sculpt.FlipFaces;

		// Same 3-LOD recipe the runtime build uses (full / half / quarter resolution).
		var lods = new[]
		{
			SurfaceNetsMesher.ComputeData( brushes, res, flip ),
			SurfaceNetsMesher.ComputeData( brushes, Math.Max( 4, res / 2 ), flip ),
			SurfaceNetsMesher.ComputeData( brushes, Math.Max( 4, res / 4 ), flip ),
		};

		if ( lods[0].IsEmpty )
		{
			Log.Warning( "[SDF Bake] the field is empty — nothing to bake." );
			return false;
		}

		var name = SanitizeName( sculpt.GameObject.Name );
		var dir = Path.Combine( Project.Current.GetAssetsPath(), "models", "sdf" );
		Directory.CreateDirectory( dir );
		var absPath = Path.Combine( dir, name + ".sdfmesh" );

		var asset = AssetSystem.CreateResource( "sdfmesh", absPath );
		if ( asset is null )
		{
			Log.Warning( $"[SDF Bake] couldn't create the asset at {absPath}" );
			return false;
		}

		var baked = new SdfBakedMesh
		{
			SourceHash = SdfSculpture.ContentHash( brushes, res, flip ),
			Data = SdfBakedMesh.Pack( lods ),
		};

		if ( !asset.SaveToDisk( baked ) )
		{
			Log.Warning( "[SDF Bake] saving the asset failed." );
			return false;
		}

		sculpt.BakedMesh = baked;
		sculpt.Rebuild(); // swap straight over to the baked model

		Log.Info( $"[SDF Bake] baked '{sculpt.GameObject.Name}' -> {absPath} ({baked.Data.Length / 1024} KB)" );
		return true;
	}

	static string SanitizeName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return "sdf_prop";

		foreach ( var c in Path.GetInvalidFileNameChars() )
			name = name.Replace( c, '_' );

		return name.Replace( ' ', '_' ).ToLowerInvariant();
	}
}
