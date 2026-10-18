using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Mimiclay;
using Sandbox;

namespace Editor;

/// <summary>
/// The s&amp;box → Blender half of the level bridge (the other direction is <see cref="FbxBlockoutImporter"/>).
/// Dumps the active scene's static geometry — mesh blocks (MeshComponent) and model renderers — to
/// <c>Tools/blender/bridge/scene.obj</c> plus a <c>scene.json</c> manifest. Coordinates are written in the
/// FBX importer's frame (the "Blender" root's local space, s&amp;box units), so the Blender add-on
/// (<c>Tools/blender/sbox_bridge.py</c>) only has to divide by the unit factor to land everything exactly
/// where the Blender scene's own objects sit.
///
/// Unit factor: the importer treats FBX units as cm → inches, times ImportScale; the user's Blender exports
/// carry UnitScaleFactor 1, so 1 Blender unit = ImportScale / 2.54 s&amp;box units. The manifest records that
/// and the add-on lets the user override it.
///
/// By default the pieces the importer itself created are skipped (they came FROM Blender) and so is SDF clay
/// (in-game props, not level geometry).
/// </summary>
public static class BlenderBridge
{
	public static string BridgeDir => Path.Combine( Project.Current.GetRootPath(), "Tools", "blender", "bridge" );

	const string ObjName = "scene.obj";
	const string ManifestName = "scene.json";

	[Menu( "Editor", "Mimiclay/Blender/Send Scene Geometry", "north_east" )]
	public static void SendSceneMenu() => Log.Info( "[BlenderBridge] " + Send() );

	[Menu( "Editor", "Mimiclay/Blender/Send Selection", "north_east" )]
	public static void SendSelectionMenu() => Log.Info( "[BlenderBridge] " + Send( selectionOnly: true ) );

	/// <summary>
	/// Export the active scene's static geometry for Blender (Tools/blender/bridge/scene.obj + scene.json).
	/// The Blender add-on picks it up on its next pull (or automatically when auto-pull is on).
	/// </summary>
	/// <param name="selectionOnly">Only export the selected game objects (and their children).</param>
	/// <param name="includeFbxPieces">Also export the children an FbxBlockoutImporter built — normally skipped, they came from Blender.</param>
	/// <param name="includeClay">Also export SDF sculptures (the clay props) as their current meshes.</param>
	/// <param name="includeDisabled">Also export disabled objects.</param>
	/// <param name="tag">Only export objects carrying this tag (e.g. 'world'). Empty for all.</param>
	[Mcp.McpTool( "blender_send_scene" )]
	public static string Send( bool selectionOnly = false, bool includeFbxPieces = false, bool includeClay = false, bool includeDisabled = false, string tag = "" )
	{
		try
		{
			return Export( selectionOnly, includeFbxPieces, includeClay, includeDisabled, tag ?? "" );
		}
		catch ( Exception e )
		{
			Log.Warning( e, "[BlenderBridge] export failed" );
			return $"Export failed: {e.Message}";
		}
	}

	sealed class Piece
	{
		public string Name;
		public string Path;
		public string Kind;
		public List<string> Tags;
		public Vector3[] Verts;      // frame-local, s&box units
		public int[] Tris;           // 0-based, into Verts
	}

	static string Export( bool selectionOnly, bool includeFbxPieces, bool includeClay, bool includeDisabled, string tag )
	{
		var session = SceneEditorSession.Active;
		var scene = session?.Scene;
		if ( scene is null )
			return "No active scene.";

		// Frame = the FBX importer root, so Blender coordinates line up with the user's own objects.
		var importer = scene.GetAllComponents<FbxBlockoutImporter>().FirstOrDefault( c => c.Active )
			?? scene.GetAllComponents<FbxBlockoutImporter>().FirstOrDefault();
		var frame = importer.IsValid() ? importer.WorldTransform : global::Transform.Zero;
		var unitsPerBu = importer.IsValid() ? importer.ImportScale / 2.54f : 1f;

		IEnumerable<GameObject> roots = selectionOnly
			? session.Selection.OfType<GameObject>()
			: scene.Children;

		var seen = new HashSet<GameObject>();
		var pieces = new List<Piece>();
		var skipped = new List<string>();

		foreach ( var root in roots )
		{
			if ( !root.IsValid() ) continue;
			foreach ( var go in root.GetAllObjects( !includeDisabled ).Prepend( root ) )
			{
				if ( !go.IsValid() || !seen.Add( go ) ) continue;
				if ( !includeDisabled && !go.Active ) continue;
				if ( !string.IsNullOrWhiteSpace( tag ) && !go.Tags.Has( tag ) ) continue;
				if ( !includeFbxPieces && go.GetComponentInParent<FbxBlockoutImporter>( true ) is not null ) continue;
				if ( !includeClay && go.GetComponentInParent<SdfSculpture>( true ) is not null ) continue;

				Model model = null;
				string kind = null;
				if ( go.GetComponent<MeshComponent>( includeDisabled ) is { } mc && mc.Model is not null )
				{
					model = mc.Model; kind = "mesh";
				}
				else if ( go.GetComponent<ModelRenderer>( includeDisabled ) is { } mr && mr.Model is not null )
				{
					model = mr.Model; kind = "model";
					if ( model.IsError || model.ResourcePath.Contains( "sbox_procedural_model", StringComparison.OrdinalIgnoreCase ) )
					{
						skipped.Add( $"{go.Name} (placeholder model)" );
						continue;
					}
				}
				if ( model is null ) continue;

				var piece = Build( go, model, frame, kind, out var why );
				if ( piece is null ) { skipped.Add( $"{go.Name} ({why})" ); continue; }
				pieces.Add( piece );
			}
		}

		if ( pieces.Count == 0 )
			return "Nothing to export — no mesh blocks or model renderers matched.";

		Directory.CreateDirectory( BridgeDir );
		WriteObj( Path.Combine( BridgeDir, ObjName ), pieces );
		WriteManifest( Path.Combine( BridgeDir, ManifestName ), scene, pieces, importer, frame, unitsPerBu );

		var tris = pieces.Sum( p => p.Tris.Length / 3 );
		var msg = $"Sent {pieces.Count} objects ({tris} tris) to {BridgeDir} — frame '{(importer.IsValid() ? importer.GameObject.Name : "world")}', {unitsPerBu:0.####} units per Blender unit.";
		if ( skipped.Count > 0 )
			msg += $" Skipped {skipped.Count}: {string.Join( ", ", skipped.Take( 8 ) )}{(skipped.Count > 8 ? ", …" : "")}";
		return msg;
	}

	static Piece Build( GameObject go, Model model, global::Transform frame, string kind, out string why )
	{
		why = null;
		Vertex[] verts;
		uint[] indices;
		try
		{
			verts = model.GetVertices();
			indices = model.GetIndices();
		}
		catch ( Exception e )
		{
			why = "vertex read failed: " + e.Message;
			return null;
		}
		if ( verts is null || verts.Length == 0 || indices is null || indices.Length < 3 )
		{
			why = "no geometry";
			return null;
		}

		// Only the first LOD — GetIndices returns every LOD back to back.
		var lod0 = model.GetIndexCountForLod( 0 );
		var count = lod0 > 0 && lod0 <= indices.Length ? lod0 : indices.Length;
		count -= count % 3;

		var world = go.WorldTransform;
		var flip = (world.Scale.x < 0) ^ (world.Scale.y < 0) ^ (world.Scale.z < 0);

		var outVerts = new Vector3[verts.Length];
		for ( int i = 0; i < verts.Length; i++ )
			outVerts[i] = frame.PointToLocal( world.PointToWorld( verts[i].Position ) );

		var tris = new List<int>( count );
		for ( int i = 0; i + 2 < count; i += 3 )
		{
			int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
			if ( a >= verts.Length || b >= verts.Length || c >= verts.Length ) continue; // multi-drawcall base offsets we can't see
			if ( flip ) { tris.Add( a ); tris.Add( c ); tris.Add( b ); }
			else { tris.Add( a ); tris.Add( b ); tris.Add( c ); }
		}
		if ( tris.Count == 0 ) { why = "indices out of range"; return null; }

		return new Piece
		{
			Name = go.Name,
			Path = HierarchyPath( go ),
			Kind = kind,
			Tags = go.Tags.TryGetAll().ToList(),
			Verts = outVerts,
			Tris = tris.ToArray(),
		};
	}

	static string HierarchyPath( GameObject go )
	{
		var parts = new List<string>();
		for ( var g = go; g.IsValid() && g != g.Scene; g = g.Parent )
			parts.Add( g.Name );
		parts.Reverse();
		return string.Join( "/", parts );
	}

	static void WriteObj( string path, List<Piece> pieces )
	{
		var sb = new StringBuilder( 1 << 20 );
		var inv = CultureInfo.InvariantCulture;
		sb.AppendLine( "# mimiclay BlenderBridge — s&box units, FBX-importer frame, Z up" );
		int baseIndex = 1;
		foreach ( var p in pieces )
		{
			sb.Append( "o " ).AppendLine( SafeName( p.Path ) );
			foreach ( var v in p.Verts )
				sb.Append( "v " ).Append( v.x.ToString( "0.####", inv ) ).Append( ' ' ).Append( v.y.ToString( "0.####", inv ) ).Append( ' ' ).Append( v.z.ToString( "0.####", inv ) ).Append( '\n' );
			for ( int i = 0; i < p.Tris.Length; i += 3 )
				sb.Append( "f " ).Append( baseIndex + p.Tris[i] ).Append( ' ' ).Append( baseIndex + p.Tris[i + 1] ).Append( ' ' ).Append( baseIndex + p.Tris[i + 2] ).Append( '\n' );
			baseIndex += p.Verts.Length;
		}
		File.WriteAllText( path, sb.ToString() );
	}

	static string SafeName( string s )
	{
		var sb = new StringBuilder( s.Length );
		foreach ( var ch in s )
			sb.Append( char.IsWhiteSpace( ch ) ? '_' : ch );
		return sb.ToString();
	}

	static void WriteManifest( string path, Scene scene, List<Piece> pieces, FbxBlockoutImporter importer, global::Transform frame, float unitsPerBu )
	{
		var spawns = scene.GetAllComponents<RoundSpawnPoint>()
			.Where( s => s.IsValid() && s.Active )
			.Select( s => new
			{
				name = s.GameObject.Name,
				hunter = s.HunterStart,
				position = Vec( frame.PointToLocal( s.WorldPosition ) ),
				yaw = (frame.Rotation.Inverse * s.WorldRotation).Yaw(),
			} ).ToList();

		var doc = new
		{
			generator = "mimiclay BlenderBridge",
			exported_at = DateTime.UtcNow.ToString( "o" ),
			scene = scene.Name,
			scene_path = scene.Source?.ResourcePath,
			units = "sbox (inches), Z up, same handedness as Blender",
			sbox_units_per_blender_unit = unitsPerBu,
			import_scale = importer.IsValid() ? importer.ImportScale : (float?)null,
			fbx_path = importer.IsValid() ? importer.FbxPath : null,
			frame = new
			{
				name = importer.IsValid() ? importer.GameObject.Name : "world",
				position = Vec( frame.Position ),
				rotation = new[] { frame.Rotation.x, frame.Rotation.y, frame.Rotation.z, frame.Rotation.w },
			},
			player = new { body_height = 70f, body_radius = 16f, jump_apex = 56f, step_height = 18f },
			spawns,
			objects = pieces.Select( p => new { name = p.Name, obj_name = SafeName( p.Path ), path = p.Path, kind = p.Kind, tags = p.Tags, verts = p.Verts.Length, tris = p.Tris.Length / 3 } ).ToList(),
		};

		File.WriteAllText( path, JsonSerializer.Serialize( doc, new JsonSerializerOptions { WriteIndented = true } ) );
	}

	static float[] Vec( Vector3 v ) => new[] { v.x, v.y, v.z };
}
