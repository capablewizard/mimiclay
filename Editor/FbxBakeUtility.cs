using System;
using System.Collections.Generic;
using System.IO;
using Mimiclay;

namespace Editor;

/// <summary>
/// Editor-only baking of an <see cref="FbxBlockoutImporter"/>'s geometry into a shippable
/// <c>.fbxbake</c> asset. Raw .fbx files never get packaged and the importer's runtime-built models
/// serialize as a dead placeholder, so standalone builds show nothing — the bake is the packaged
/// copy the importer rebuilds from when the file is missing. Writing the asset needs the editor
/// assembly, so this lives here and is wired to the component's button via
/// <see cref="FbxBlockoutImporter.BakeHandler"/> (same pattern as <see cref="SdfBakeUtility"/>).
/// </summary>
public static class FbxBakeUtility
{
	const string OutputRelDir = "models/fbxbaked";

	[EditorEvent.Frame]
	static void WireHandler()
	{
		// idempotent, cheap — keeps the hook alive across hotloads without a tool having to be active
		FbxBlockoutImporter.BakeHandler = Bake;
	}

	public static bool Bake( FbxBlockoutImporter importer )
	{
		if ( importer is null )
			return false;

		var bytes = importer.ReadFileBytes( out var readError );
		if ( bytes is null )
		{
			Log.Warning( $"[FbxBake] {readError}" );
			return false;
		}

		List<FbxPiece> pieces;
		try
		{
			pieces = FbxSceneReader.Read( bytes, importer.ImportScale, out _ );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[FbxBake] Couldn't parse '{importer.FbxPath}': {e.Message}" );
			return false;
		}

		if ( pieces.Count == 0 )
		{
			Log.Warning( $"[FbxBake] '{importer.FbxPath}' has no mesh objects — nothing to bake." );
			return false;
		}

		var baked = FbxBakedBlockout.Create(
			importer.FbxPath, importer.ImportScale, FbxBakedBlockout.HashBytes( bytes ),
			pieces, importer.ResolveMaterial );

		// name by the full fbx path so two files that share a stem can't clobber each other
		var name = SanitizeName( importer.FbxPath );
		var relPath = $"{OutputRelDir}/{name}.fbxbake";
		var absDir = Path.Combine( Project.Current.GetAssetsPath(), "models", "fbxbaked" );
		Directory.CreateDirectory( absDir );
		var absPath = Path.Combine( absDir, name + ".fbxbake" );

		var asset = AssetSystem.FindByPath( relPath ) ?? AssetSystem.CreateResource( "fbxbake", absPath );
		if ( asset is null )
		{
			Log.Warning( $"[FbxBake] couldn't create the asset at {absPath}" );
			return false;
		}

		if ( !asset.SaveToDisk( baked ) )
		{
			Log.Warning( "[FbxBake] saving the asset failed." );
			return false;
		}

		importer.BakedGeometry = baked;

		var tris = 0;
		foreach ( var p in pieces )
			foreach ( var s in p.SubMeshes )
				tris += s.Vertices.Count / 3;

		Log.Info( $"[FbxBake] baked '{importer.FbxPath}' -> {relPath} ({pieces.Count} pieces, {tris} tris, {baked.Data.Length / 1024} KB packed). Save the scene to keep the reference." );
		return true;
	}

	static string SanitizeName( string fbxPath )
	{
		var name = (fbxPath ?? "").Replace( '\\', '/' ).Trim().TrimStart( '/' );
		if ( name.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) )
			name = name[..^4];
		if ( string.IsNullOrWhiteSpace( name ) )
			name = "blockout";

		var chars = name.ToLowerInvariant().ToCharArray();
		for ( var i = 0; i < chars.Length; i++ )
		{
			if ( !char.IsLetterOrDigit( chars[i] ) )
				chars[i] = '_';
		}

		return new string( chars );
	}
}
