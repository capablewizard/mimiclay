using System;
using System.Collections.Generic;
using System.IO;

namespace Mimiclay;

/// <summary>
/// A pre-baked SDF prop mesh saved to disk, so a shipped level loads the geometry instead of
/// re-sampling the signed-distance field at load time. Stores all LODs — vertices (including the
/// per-brush vertex colour), indices and bounds — as a packed <see cref="Data"/> blob.
/// <see cref="BuildModel"/> uploads it to a Model on the main thread (cheap: no field sampling).
/// Bake one in the editor via SdfSculpture's "Bake To Asset" button.
/// </summary>
[AssetType( Name = "SDF Baked Mesh", Extension = "sdfmesh", Category = "SDF" )]
public sealed class SdfBakedMesh : GameResource
{
	/// <summary>Deterministic content hash (see <see cref="SdfSculpture.ContentHash"/>) of the brushes
	/// this was baked from, so a sculpture can detect a stale bake and fall back to live meshing.</summary>
	public int SourceHash { get; set; }

	/// <summary>Packed per-LOD geometry (see <see cref="Pack"/>/<see cref="Unpack"/>). base64 in the JSON.</summary>
	public byte[] Data { get; set; }

	// v2 adds per-vertex metalness/roughness (TexCoord0.xy). A v1 file fails Unpack -> the sculpture
	// falls back to live meshing and can be re-baked; nothing breaks.
	const int FormatVersion = 2;

	// One Model per material the bake is rendered with (usually just one). Runtime-only, not serialised.
	readonly Dictionary<Material, Model> _models = new();

	/// <summary>Realise the baked geometry into a Model. MAIN THREAD (GPU upload), but no field sampling.
	/// Cached per material. Null if the data is missing or from an incompatible format version.</summary>
	public Model BuildModel( Material material )
	{
		material ??= Material.Load( "materials/dev/reflectivity_50.vmat" );

		if ( _models.TryGetValue( material, out var cached ) && cached is not null )
			return cached;

		var lods = Unpack( Data );
		if ( lods is null || lods.Length == 0 || lods[0].IsEmpty )
			return null;

		var builder = new ModelBuilder();
		for ( int i = 0; i < lods.Length; i++ )
		{
			if ( lods[i].IsEmpty )
				continue;
			builder.AddMesh( SurfaceNetsMesher.Upload( lods[i], material ), i );
		}

		var model = builder.Create();
		_models[material] = model;
		return model;
	}

	/// <summary>Serialise the LOD mesh data to a compact byte blob.</summary>
	public static byte[] Pack( SurfaceNetsMesher.MeshData[] lods )
	{
		using var ms = new MemoryStream();
		using var w = new BinaryWriter( ms );

		w.Write( FormatVersion );
		w.Write( lods.Length );

		foreach ( var lod in lods )
		{
			if ( lod.IsEmpty )
			{
				w.Write( 0 ); // vertex count 0 = empty LOD
				continue;
			}

			w.Write( lod.Vertices.Length );
			foreach ( var v in lod.Vertices )
			{
				w.Write( v.Position.x ); w.Write( v.Position.y ); w.Write( v.Position.z );
				w.Write( v.Normal.x ); w.Write( v.Normal.y ); w.Write( v.Normal.z );
				w.Write( v.Tangent.x ); w.Write( v.Tangent.y ); w.Write( v.Tangent.z ); w.Write( v.Tangent.w );
				w.Write( v.Color.r ); w.Write( v.Color.g ); w.Write( v.Color.b ); w.Write( v.Color.a );
				w.Write( v.TexCoord0.x ); w.Write( v.TexCoord0.y ); // metalness, roughness
			}

			w.Write( lod.Indices.Length );
			foreach ( var idx in lod.Indices )
				w.Write( idx );

			w.Write( lod.Bounds.Mins.x ); w.Write( lod.Bounds.Mins.y ); w.Write( lod.Bounds.Mins.z );
			w.Write( lod.Bounds.Maxs.x ); w.Write( lod.Bounds.Maxs.y ); w.Write( lod.Bounds.Maxs.z );
		}

		return ms.ToArray();
	}

	/// <summary>Inverse of <see cref="Pack"/>. Null on missing data or a format-version mismatch.</summary>
	public static SurfaceNetsMesher.MeshData[] Unpack( byte[] bytes )
	{
		if ( bytes is null || bytes.Length == 0 )
			return null;

		using var ms = new MemoryStream( bytes );
		using var r = new BinaryReader( ms );

		if ( r.ReadInt32() != FormatVersion )
			return null;

		int lodCount = r.ReadInt32();
		var lods = new SurfaceNetsMesher.MeshData[lodCount];

		for ( int l = 0; l < lodCount; l++ )
		{
			int vcount = r.ReadInt32();
			if ( vcount == 0 )
			{
				lods[l] = default;
				continue;
			}

			var verts = new Vertex[vcount];
			for ( int i = 0; i < vcount; i++ )
			{
				verts[i] = new Vertex
				{
					Position = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
					Normal = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
					Tangent = new Vector4( r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() ),
					Color = new Color32( r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte() ),
					TexCoord0 = new Vector4( r.ReadSingle(), r.ReadSingle(), 0f, 0f ), // metalness, roughness
				};
			}

			int icount = r.ReadInt32();
			var indices = new int[icount];
			for ( int i = 0; i < icount; i++ )
				indices[i] = r.ReadInt32();

			var mins = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );
			var maxs = new Vector3( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );
			lods[l] = new SurfaceNetsMesher.MeshData( verts, indices, new BBox( mins, maxs ) );
		}

		return lods;
	}
}
