using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mimiclay;

namespace Editor;

/// <summary>
/// Editor-only export of an <see cref="SdfSculpture"/> to a reusable <c>.prefab</c> asset, so a shape sculpted
/// in-game (or a saved <c>.sculpt</c> from <see cref="SculptLibrary"/>) can be dropped into any scene from the
/// asset browser. Writing project assets needs the editor assembly, so this lives here and is wired to the
/// component via <see cref="SdfSculpture.ExportPrefabHandler"/> by the tool — the same pattern as the
/// <c>.sdfmesh</c> <see cref="SdfBakeUtility"/> bake.
///
/// The prefab is built by cloning the shipped <c>disguise.prefab</c> as a TEMPLATE (so the exported prop keeps
/// the tuned raymarch renderer, shadow model, collision and material) and swapping in the sculpture's brushes —
/// then stripping the disguise's runtime-only children (the pause HUD) and the components that only make sense
/// on a PAWN (see <see cref="StrippedTypes"/>), and re-GUIDing so it's a standalone asset, not a copy that
/// aliases the template.
/// </summary>
public static class SdfPrefabUtility
{
	// Under the project's Assets root. Case-insensitive on Windows, so this resolves the existing "Prefabs/".
	const string TemplateRelPath = "prefabs/disguise.prefab";
	static readonly string[] OutputRelDir = { "prefabs", "saved" };

	/// <summary>Components carried by the disguise template that must NOT survive onto an exported scene prop.
	/// <c>ModelCollider</c> because the template's is a snapshot of a play session (it serialises a stale
	/// <c>sbox_procedural_model.vmdl</c> reference that has nothing to do with this shape) — the surviving
	/// <see cref="SdfCollider"/> builds a correct one for the exported brushes at runtime, so the prop is still
	/// solid. <c>SdfHighlightOutline</c> because the outline is a gameplay affordance owned by
	/// <c>RoundOutlineSystem</c>, not something a piece of scenery should carry.</summary>
	static readonly string[] StrippedTypes =
	{
		"Sandbox.ModelCollider",
		"Mimiclay.SdfHighlightOutline",
	};

	/// <summary>Export a live sculpture (route A: sculpted in an editor play session). Names the prefab after
	/// the sculpture's GameObject, matching the bake tool.</summary>
	public static bool Export( SdfSculpture sculpt )
	{
		if ( sculpt?.Brushes is not { Count: > 0 } )
		{
			Log.Warning( "[SDF Export] nothing to export." );
			return false;
		}

		return Export( sculpt.GameObject.Name, sculpt.Brushes, sculpt.Resolution, sculpt.FlipFaces );
	}

	/// <summary>Export a shape saved to the local library (route B: a player saved a <c>.sculpt</c> on any
	/// build; turn it into a curated scene asset here on the dev machine).</summary>
	public static bool ExportFromSave( string saveName )
	{
		var entry = SculptLibrary.Load( saveName );
		if ( entry is null )
		{
			Log.Warning( $"[SDF Export] no saved sculpture '{saveName}'." );
			return false;
		}

		return Export( entry.Name ?? saveName, entry.Brushes, entry.Resolution, entry.FlipFaces );
	}

	/// <summary>Export a saved sculpture to a prefab and hand back the registered <see cref="Asset"/> (for
	/// instantiating it straight into a scene). Null if the save is missing or the write failed.</summary>
	public static Asset ExportAssetFromSave( string saveName )
	{
		var entry = SculptLibrary.Load( saveName );
		if ( entry is null )
		{
			Log.Warning( $"[SDF Export] no saved sculpture '{saveName}'." );
			return null;
		}

		if ( !Export( entry.Name ?? saveName, entry.Brushes, entry.Resolution, entry.FlipFaces ) )
			return null;

		return AssetSystem.FindByPath( $"prefabs/saved/{SanitizeName( entry.Name ?? saveName )}.prefab" );
	}

	/// <summary>Core writer: clone the template prefab, swap in the given shape, write it under
	/// <c>prefabs/saved/&lt;name&gt;.prefab</c> and register it so the editor imports it.</summary>
	public static bool Export( string name, List<SdfBrush> brushes, int resolution, bool flip )
	{
		if ( brushes is not { Count: > 0 } )
			return false;

		var assets = Project.Current.GetAssetsPath();
		var templatePath = Path.Combine( assets, "prefabs", "disguise.prefab" );
		if ( !File.Exists( templatePath ) )
		{
			Log.Warning( $"[SDF Export] template prefab missing at {templatePath}" );
			return false;
		}

		JsonNode root;
		try
		{
			root = JsonNode.Parse( File.ReadAllText( templatePath ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[SDF Export] couldn't parse template prefab — {e.Message}" );
			return false;
		}

		var rootObject = root?["RootObject"]?.AsObject();
		var components = rootObject?["Components"]?.AsArray();
		if ( rootObject is null || components is null )
		{
			Log.Warning( "[SDF Export] template prefab has no RootObject/Components." );
			return false;
		}

		// Find the SdfSculpture component in the template and replace its shape with ours. Serialize the brushes
		// exactly as the runtime/networking does, then re-parse so it drops into the prefab tree as real nodes.
		var sculptNode = components
			.FirstOrDefault( c => c is JsonObject && (string)c["__type"] == "Mimiclay.SdfSculpture" )
			?.AsObject();
		if ( sculptNode is null )
		{
			Log.Warning( "[SDF Export] template prefab has no SdfSculpture component." );
			return false;
		}

		sculptNode["Brushes"] = JsonNode.Parse( Json.Serialize( brushes ) );
		sculptNode["Resolution"] = resolution;
		sculptNode["FlipFaces"] = flip;
		sculptNode["BakedMesh"] = null; // stay live/editable; bake is a separate opt-in step

		// A clean scene prop: name it after the export and drop the disguise's runtime-only children (pause HUD).
		var safe = SanitizeName( name );
		rootObject["Name"] = safe;
		rootObject["Children"] = new JsonArray();

		// ...and drop the components a scene prop shouldn't inherit from the disguise template.
		foreach ( var node in components
			.Where( c => c is JsonObject && StrippedTypes.Contains( (string)c["__type"] ) )
			.ToList() )
			components.Remove( node );

		// Fresh GUIDs so this prefab is a standalone asset, not one that aliases the template's object identities.
		RemapGuids( root );

		var dir = Path.Combine( assets, Path.Combine( OutputRelDir ) );
		Directory.CreateDirectory( dir );
		var absPath = Path.Combine( dir, safe + ".prefab" );

		try
		{
			File.WriteAllText( absPath, root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[SDF Export] writing the prefab failed — {e.Message}" );
			return false;
		}

		AssetSystem.RegisterFile( absPath ); // pull it into the asset browser immediately
		Log.Info( $"[SDF Export] exported '{name}' -> {absPath}" );
		return true;
	}

	// Replace every object identity (__guid) in the prefab with a fresh one, keeping any internal reference that
	// points at a remapped guid consistent (walk once to build old->new, once to apply — to values in ANY field,
	// not just __guid, so cross-references survive).
	static void RemapGuids( JsonNode node )
	{
		var map = new Dictionary<string, string>();
		Collect( node, map );
		Apply( node, map );

		static void Collect( JsonNode n, Dictionary<string, string> map )
		{
			switch ( n )
			{
				case JsonObject o:
					foreach ( var kv in o )
					{
						if ( kv.Key == "__guid" && kv.Value is JsonValue jv && jv.TryGetValue<string>( out var g )
							&& !string.IsNullOrEmpty( g ) && !map.ContainsKey( g ) )
							map[g] = Guid.NewGuid().ToString();

						Collect( kv.Value, map );
					}
					break;

				case JsonArray a:
					foreach ( var item in a )
						Collect( item, map );
					break;
			}
		}

		static void Apply( JsonNode n, Dictionary<string, string> map )
		{
			switch ( n )
			{
				case JsonObject o:
					foreach ( var key in o.Select( kv => kv.Key ).ToList() )
					{
						if ( o[key] is JsonValue v && v.TryGetValue<string>( out var s ) && map.TryGetValue( s, out var nu ) )
							o[key] = nu;
						else
							Apply( o[key], map );
					}
					break;

				case JsonArray a:
					for ( int i = 0; i < a.Count; i++ )
					{
						if ( a[i] is JsonValue v && v.TryGetValue<string>( out var s ) && map.TryGetValue( s, out var nu ) )
							a[i] = nu;
						else
							Apply( a[i], map );
					}
					break;
			}
		}
	}

	static string SanitizeName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return "sdf_prop";

		foreach ( var c in Path.GetInvalidFileNameChars() )
			name = name.Replace( c, '_' );

		return name.Replace( ' ', '_' ).ToLowerInvariant();
	}

	// Editor console command for route B: `mimi_sculpt_export <saveName>` turns a local .sculpt save into a prefab.
	[ConCmd( "mimi_sculpt_export" )]
	public static void ExportCmd( string saveName )
		=> Log.Info( ExportFromSave( saveName ) ? $"Exported '{saveName}' to a prefab." : $"Export of '{saveName}' failed." );

	// ── Prefab → .sculpt (the reverse direction, from the asset browser) ─────────────────────────────────

	/// <summary>Right-click a prefab in the asset browser → "Save as .sculpt". The inverse of
	/// <see cref="Export(SdfSculpture)"/>: parses the prefab's JSON directly (no instantiation, works even if
	/// it hasn't compiled yet), finds its first <see cref="SdfSculpture"/> — root or child — and writes the
	/// shape to the local <see cref="SculptLibrary"/> under the prefab's file name. Produces the same file as
	/// the in-game <c>mimi_sculpt_from_prefab</c> command.</summary>
	[Event( "asset.contextmenu", Priority = 50 )]
	public static void OnPrefabAssetContext( AssetContextMenu e )
	{
		if ( e.SelectedList.Count == 0 || !e.SelectedList.All( x => x.Asset?.AssetType?.FileExtension == "prefab" ) )
			return;

		e.Menu.AddOption( "Save as .sculpt", "gesture", action: () =>
		{
			foreach ( var entry in e.SelectedList )
				SaveSculptFromPrefab( entry.Asset );
		} );
	}

	static void SaveSculptFromPrefab( Asset asset )
	{
		JsonNode root;
		try
		{
			root = JsonNode.Parse( File.ReadAllText( asset.AbsolutePath ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[SDF Import] couldn't read \"{asset.Path}\" — {e.Message}" );
			return;
		}

		var sculptNode = FindSculptureNode( root?["RootObject"] );
		if ( sculptNode is null )
		{
			Log.Warning( $"[SDF Import] \"{asset.Path}\" has no SdfSculpture component." );
			return;
		}

		List<SdfBrush> brushes;
		try
		{
			// Round-trip through the same serializer the runtime uses, so the brush JSON is read identically
			// to a networked or .sculpt-saved brush list.
			brushes = Json.Deserialize<List<SdfBrush>>( sculptNode["Brushes"]?.ToJsonString() ?? "null" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[SDF Import] couldn't parse brushes in \"{asset.Path}\" — {e.Message}" );
			return;
		}

		if ( brushes is not { Count: > 0 } )
		{
			Log.Warning( $"[SDF Import] \"{asset.Path}\" has an empty brush list — nothing to save." );
			return;
		}

		// Refuse rather than write a file SculptLibrary.Load() will reject as over-cap.
		if ( brushes.Count > SdfBrushPacker.MaxBrushes )
		{
			Log.Warning( $"[SDF Import] \"{asset.Path}\" has {brushes.Count} brushes (cap {SdfBrushPacker.MaxBrushes}) — too many to save." );
			return;
		}

		var entry = new SculptLibrary.Entry
		{
			Name = asset.Name,
			Resolution = sculptNode["Resolution"]?.GetValue<int>() ?? 32,
			FlipFaces = sculptNode["FlipFaces"]?.GetValue<bool>() ?? false,
			Brushes = brushes,
		};

		if ( SculptLibrary.Save( entry ) )
			Log.Info( $"[SDF Import] saved \"{asset.Path}\" -> \"{SculptLibrary.FullPath( entry.Name )}\"." );
		else
			Log.Warning( $"[SDF Import] failed to save \"{asset.Path}\" as a sculpt." );
	}

	// First SdfSculpture component on this GameObject node or (depth-first) any of its children.
	static JsonObject FindSculptureNode( JsonNode gameObject )
	{
		if ( gameObject is not JsonObject go )
			return null;

		var found = go["Components"]?.AsArray()
			.FirstOrDefault( c => c is JsonObject && (string)c["__type"] == "Mimiclay.SdfSculpture" )
			?.AsObject();
		if ( found is not null )
			return found;

		if ( go["Children"] is JsonArray children )
			foreach ( var child in children )
				if ( FindSculptureNode( child ) is { } inChild )
					return inChild;

		return null;
	}
}
