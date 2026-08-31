using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Mimiclay;

/// <summary>
/// A shipped-build copy of an <see cref="FbxBlockoutImporter"/>'s geometry. The raw .fbx never makes
/// it into a package (only compiled assets ship), and the importer's runtime-built models serialize
/// as a useless <c>sbox_procedural_model</c> placeholder — so standalone builds load THIS instead:
/// every piece's vertices, materials and collision packed into <see cref="Data"/>, with the resolved
/// <see cref="Materials"/> referenced as real assets so the packager pulls them in too.
/// Baked in the editor via the importer's "Bake Shippable Geometry" button (same pattern as
/// <see cref="SdfBakedMesh"/>).
/// </summary>
[AssetType( Name = "FBX Baked Blockout", Extension = "fbxbake", Category = "Level" )]
public sealed class FbxBakedBlockout : GameResource
{
	/// <summary>The .fbx this was baked from (relative to Assets) — informational.</summary>
	public string SourceFbx { get; set; } = "";

	/// <summary>The importer's ImportScale at bake time — a different scale means a stale bake.</summary>
	public float ImportScale { get; set; } = 1f;

	/// <summary>FNV-1a of the source .fbx bytes, so the importer can warn when the file has
	/// changed since the bake (see <see cref="HashBytes"/>).</summary>
	public ulong SourceHash { get; set; }

	/// <summary>Every material the pieces resolve to, referenced by index from <see cref="Data"/>.
	/// Real asset references — this is what gets them traced into the package.</summary>
	public List<Material> Materials { get; set; } = new();

	/// <summary>Deflate-packed piece geometry (see <see cref="Create"/>/<see cref="UnpackPieces"/>). base64 in the JSON.</summary>
	public byte[] Data { get; set; }

	const int FormatVersion = 1;

	/// <summary>Pack a parsed piece list into a bake. <paramref name="resolveMaterial"/> maps an FBX
	/// material name to the project material (the importer's remap table) — resolved here, at bake
	/// time, so shipped builds don't depend on the remap rows at all.</summary>
	public static FbxBakedBlockout Create(
		string sourceFbx, float importScale, ulong sourceHash,
		List<FbxPiece> pieces, Func<string, Material> resolveMaterial )
	{
		var baked = new FbxBakedBlockout
		{
			SourceFbx = sourceFbx,
			ImportScale = importScale,
			SourceHash = sourceHash,
		};

		var indexOf = new Dictionary<Material, int>();

		int MaterialIndex( string fbxName )
		{
			var mat = resolveMaterial?.Invoke( fbxName );
			if ( mat is null )
				return -1;
			if ( indexOf.TryGetValue( mat, out var i ) )
				return i;
			i = baked.Materials.Count;
			baked.Materials.Add( mat );
			indexOf[mat] = i;
			return i;
		}

		using var ms = new MemoryStream();
		using ( var deflate = new DeflateStream( ms, CompressionLevel.Fastest, leaveOpen: true ) )
		using ( var w = new BinaryWriter( deflate ) )
		{
			w.Write( FormatVersion );
			w.Write( pieces.Count );

			foreach ( var piece in pieces )
			{
				w.Write( piece.Name ?? "" );
				w.Write( piece.Position.x ); w.Write( piece.Position.y ); w.Write( piece.Position.z );
				w.Write( piece.Rotation.x ); w.Write( piece.Rotation.y ); w.Write( piece.Rotation.z ); w.Write( piece.Rotation.w );

				w.Write( piece.SubMeshes.Count );
				foreach ( var sub in piece.SubMeshes )
				{
					w.Write( MaterialIndex( sub.MaterialName ) );
					w.Write( sub.MaterialName ?? "" );

					w.Write( sub.Vertices.Count );
					foreach ( var v in sub.Vertices )
					{
						w.Write( v.Position.x ); w.Write( v.Position.y ); w.Write( v.Position.z );
						w.Write( v.Normal.x ); w.Write( v.Normal.y ); w.Write( v.Normal.z );
						w.Write( v.Tangent.x ); w.Write( v.Tangent.y ); w.Write( v.Tangent.z ); w.Write( v.Tangent.w );
						w.Write( v.TexCoord0.x ); w.Write( v.TexCoord0.y );
					}

					w.Write( sub.Bounds.Mins.x ); w.Write( sub.Bounds.Mins.y ); w.Write( sub.Bounds.Mins.z );
					w.Write( sub.Bounds.Maxs.x ); w.Write( sub.Bounds.Maxs.y ); w.Write( sub.Bounds.Maxs.z );
				}

				w.Write( piece.CollisionVertices.Count );
				foreach ( var v in piece.CollisionVertices )
				{
					w.Write( v.x ); w.Write( v.y ); w.Write( v.z );
				}

				w.Write( piece.CollisionIndices.Count );
				foreach ( var idx in piece.CollisionIndices )
					w.Write( idx );
			}
		}

		baked.Data = ms.ToArray();
		return baked;
	}

	/// <summary>Rebuild the piece list the importer feeds to SyncChildren. Each submesh carries its
	/// RESOLVED <see cref="FbxSubMesh.Material"/> (from <see cref="Materials"/>). Null on missing
	/// data or a format-version mismatch — the caller falls back to a live import.</summary>
	public List<FbxPiece> UnpackPieces()
	{
		if ( Data is null || Data.Length == 0 )
			return null;

		try
		{
			using var ms = new MemoryStream( Data );
			using var inflate = new DeflateStream( ms, CompressionMode.Decompress );
			using var r = new BinaryReader( inflate );

			if ( r.ReadInt32() != FormatVersion )
				return null;

			var pieces = new List<FbxPiece>();
			var pieceCount = r.ReadInt32();

			for ( var p = 0; p < pieceCount; p++ )
			{
				var piece = new FbxPiece
				{
					Name = r.ReadString(),
					Position = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
					Rotation = new Rotation( r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
					Scale = Vector3.One,
				};

				var subCount = r.ReadInt32();
				for ( var s = 0; s < subCount; s++ )
				{
					var matIndex = r.ReadInt32();
					var sub = new FbxSubMesh
					{
						Material = matIndex >= 0 && matIndex < Materials.Count ? Materials[matIndex] : null,
						MaterialName = r.ReadString(),
					};

					var vcount = r.ReadInt32();
					sub.Vertices.Capacity = vcount;
					for ( var i = 0; i < vcount; i++ )
					{
						sub.Vertices.Add( new Vertex
						{
							Position = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
							Normal = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
							Tangent = new Vector4( r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
							TexCoord0 = new Vector4( r.ReadSingle(), r.ReadSingle(), 0, 0 ),
							Color = Color32.White,
						} );
					}

					var mins = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );
					var maxs = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );
					sub.Bounds = new BBox( mins, maxs );

					piece.SubMeshes.Add( sub );
				}

				var cvcount = r.ReadInt32();
				piece.CollisionVertices.Capacity = cvcount;
				for ( var i = 0; i < cvcount; i++ )
					piece.CollisionVertices.Add( new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ) );

				var cicount = r.ReadInt32();
				piece.CollisionIndices.Capacity = cicount;
				for ( var i = 0; i < cicount; i++ )
					piece.CollisionIndices.Add( r.ReadInt32() );

				pieces.Add( piece );
			}

			return pieces;
		}
		catch
		{
			return null; // corrupt/old bake — the importer falls back to a live import + warning
		}
	}

	/// <summary>FNV-1a over the source file bytes — the shared staleness check between the importer
	/// and the bake.</summary>
	public static ulong HashBytes( byte[] data )
	{
		var hash = 14695981039346656037UL;
		foreach ( var b in data )
			hash = (hash ^ b) * 1099511628211UL;
		return hash;
	}
}
