using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mimiclay;

/// <summary>
/// One-shot asset converter for the Cone → tapered Cylinder merge. The runtime already upgrades legacy cones as
/// they load (<see cref="SdfBrush.UpgradeLegacy()"/>), so nothing breaks without this — but the files on disk keep
/// saying "Cone" until something rewrites them. This walks every .prefab / .scene under Assets and rewrites each
/// legacy cone IN PLACE, touching only the fields that change:
/// <list type="bullet">
/// <item>brush-list form (an <see cref="SdfSculpture"/>'s Brushes array, prefab-instance patches included):
/// Shape, Position, Size, Taper, Slice;</item>
/// <item>object form (a GameObject carrying an <see cref="SdfBrushComponent"/>): the component's Shape/Taper/Slice
/// and the GameObject's Position/Scale, with children re-parented so their world transforms hold.</item>
/// </list>
/// Same math as the runtime (<see cref="SdfBrush.LegacyConeToCylinder"/>), so the clay doesn't move. Idempotent:
/// a converted file has no cones left. The editor recompiles the touched assets when it notices the writes.
/// </summary>
public static class LegacyConeConverter
{
	[Menu( "Editor", "Mimiclay/Convert Legacy Cones/Dry Run (report only)", "change_history" )]
	public static void DryRunMenu() => Run( write: false );

	[Menu( "Editor", "Mimiclay/Convert Legacy Cones/Convert All Assets", "change_history" )]
	public static void ConvertMenu() => Run( write: true );

	/// <summary>Convert (or just count, when <paramref name="write"/> is false) every legacy cone under Assets.
	/// Returns a one-line summary; per-file lines go to the log.</summary>
	public static string Run( bool write )
	{
		var root = Project.Current.GetAssetsPath();
		int files = 0, cones = 0, failed = 0;

		// Never rewrite a scene/prefab that's OPEN in the editor: its in-memory copy (possibly with unsaved work)
		// would overwrite the conversion on its next save, or clash with it. Open ones convert in memory on load
		// anyway; save them, close them, and re-run to convert their files too.
		var open = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var s in SceneEditorSession.All )
			if ( s?.Scene?.Source?.ResourcePath is { Length: > 0 } rp )
				open.Add( rp.Replace( '\\', '/' ) );
		var skipped = new List<string>();

		foreach ( var path in Directory.EnumerateFiles( root, "*.*", SearchOption.AllDirectories ) )
		{
			var ext = Path.GetExtension( path ).ToLowerInvariant();
			if ( ext is not (".prefab" or ".scene") )
				continue;

			string text;
			try { text = File.ReadAllText( path ); }
			catch ( Exception e ) { Log.Warning( $"[ConeConvert] can't read {path}: {e.Message}" ); failed++; continue; }

			if ( !text.Contains( "\"Cone\"" ) ) // cheap prefilter — the shape is always serialised as a string
				continue;

			try
			{
				var node = JsonNode.Parse( text );
				int n = ConvertNode( node );
				if ( n == 0 )
					continue;

				var rel = Path.GetRelativePath( root, path );
				if ( open.Contains( rel.Replace( '\\', '/' ) ) )
				{
					skipped.Add( rel );
					Log.Warning( $"[ConeConvert] skipped {rel} ({n} cone(s)) — it's open in the editor. Save + close it, then re-run." );
					continue;
				}

				files++;
				cones += n;
				Log.Info( $"[ConeConvert] {(write ? "converted" : "would convert")} {n} cone(s) in {rel}" );

				if ( write )
					File.WriteAllText( path, Format( node, text ) );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[ConeConvert] failed on {path}: {e.Message}" );
				failed++;
			}
		}

		var summary = $"[ConeConvert] {(write ? "Converted" : "Dry run: would convert")} {cones} legacy cone(s) in {files} file(s)"
			+ (failed > 0 ? $", {failed} file(s) failed (see warnings)" : "")
			+ (skipped.Count > 0 ? $"; skipped {skipped.Count} open in the editor: {string.Join( ", ", skipped )}" : "") + ".";
		Log.Info( summary );
		return summary;
	}

	/// <summary>Convert one file at an arbitrary path (testing / one-offs). Returns the cone count; writes only when
	/// <paramref name="write"/> and there was something to convert.</summary>
	public static int ConvertFile( string path, bool write )
	{
		var text = File.ReadAllText( path );
		var node = JsonNode.Parse( text );
		int n = ConvertNode( node );
		if ( n > 0 && write )
			File.WriteAllText( path, Format( node, text ) );
		return n;
	}

	// Re-emit in the engine's own layout: 2-space indent, unescaped text, the source's line endings, and the
	// source's trailing-newline state — so the diff is just the converted fields.
	static string Format( JsonNode node, string original )
	{
		var json = node.ToJsonString( new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		} );
		// Files the engine wrote with escaped non-ASCII ("·") keep it escaped; files already holding raw
		// UTF-8 stay raw. (The relaxed encoder above emits raw.)
		if ( !original.Any( c => c > 127 ) )
		{
			var sb = new System.Text.StringBuilder( json.Length );
			foreach ( var c in json )
				if ( c > 127 ) sb.Append( "\\u" ).Append( ((int)c).ToString( "X4" ) );
				else sb.Append( c );
			json = sb.ToString();
		}
		json = json.Replace( "\r\n", "\n" );
		if ( original.Contains( "\r\n" ) )
			json = json.Replace( "\n", "\r\n" );
		if ( original.EndsWith( "\n" ) && !json.EndsWith( "\n" ) )
			json += original.EndsWith( "\r\n" ) ? "\r\n" : "\n";
		return json;
	}

	/// <summary>Convert every legacy cone in this JSON tree; returns how many.</summary>
	static int ConvertNode( JsonNode node )
	{
		int n = 0;
		switch ( node )
		{
			case JsonObject obj:
				if ( IsComponentConeHost( obj, out var comp ) )
				{
					ConvertObjectForm( obj, comp );
					n++;
				}
				else if ( IsBrushCone( obj ) )
				{
					ConvertBrushForm( obj );
					n++;
				}
				foreach ( var kv in obj.ToList() )
					if ( kv.Value is not null )
						n += ConvertNode( kv.Value );
				break;

			case JsonArray arr:
				foreach ( var item in arr )
					if ( item is not null )
						n += ConvertNode( item );
				break;
		}
		return n;
	}

	static bool IsCone( JsonObject o ) => o["Shape"] is JsonValue v && v.TryGetValue( out string s ) && s == "Cone";

	// Brush-list entry: a plain SdfBrush object (no component type tag) that carries its own transform.
	static bool IsBrushCone( JsonObject o ) => IsCone( o ) && o["__type"] is null && o["Size"] is not null;

	// A GameObject whose components include a legacy-cone SdfBrushComponent.
	static bool IsComponentConeHost( JsonObject go, out JsonObject comp )
	{
		comp = null;
		if ( go["Components"] is not JsonArray comps )
			return false;
		foreach ( var c in comps.OfType<JsonObject>() )
		{
			if ( c["__type"] is JsonValue t && t.TryGetValue( out string type ) && type.EndsWith( "SdfBrushComponent" ) && IsCone( c ) )
			{
				comp = c;
				return true;
			}
		}
		return false;
	}

	static void ConvertBrushForm( JsonObject b )
	{
		float slice = GetFloat( b, "Slice" );
		SdfBrush.LegacyConeToCylinder( slice, out float heightScale, out float centreUp, out float taper );

		var size = GetVec( b, "Size", Vector3.One * 24f );
		var pos = GetVec( b, "Position", Vector3.Zero );
		var rot = GetRot( b, "Rotation" );

		SetVec( b, "Position", pos, pos + rot * new Vector3( 0f, 0f, size.z * centreUp ) );
		SetVec( b, "Size", size, size.WithZ( size.z * heightScale ) );
		SetShapeFields( b, taper );
	}

	static void ConvertObjectForm( JsonObject go, JsonObject comp )
	{
		float slice = GetFloat( comp, "Slice" );
		SdfBrush.LegacyConeToCylinder( slice, out float heightScale, out float centreUp, out float taper );

		var oldT = new Transform( GetVec( go, "Position", Vector3.Zero ), GetRot( go, "Rotation" ), GetVec( go, "Scale", Vector3.One ) );
		var newT = oldT
			.WithPosition( oldT.PointToWorld( new Vector3( 0f, 0f, centreUp ) ) )
			.WithScale( oldT.Scale.WithZ( oldT.Scale.z * heightScale ) );

		SetVec( go, "Position", oldT.Position, newT.Position );
		SetVec( go, "Scale", oldT.Scale, newT.Scale );

		// Children are stored relative to this object — re-express them under the new transform so they stay put.
		if ( go["Children"] is JsonArray kids )
		{
			foreach ( var child in kids.OfType<JsonObject>() )
			{
				var local = new Transform( GetVec( child, "Position", Vector3.Zero ), GetRot( child, "Rotation" ), GetVec( child, "Scale", Vector3.One ) );
				var moved = newT.ToLocal( oldT.ToWorld( local ) );
				SetVec( child, "Position", local.Position, moved.Position );
				SetVec( child, "Scale", local.Scale, moved.Scale );
				child["Rotation"] = Fmt( moved.Rotation );
			}
		}

		SetShapeFields( comp, taper );
	}

	static void SetShapeFields( JsonObject o, float taper )
	{
		o["Shape"] = "Cylinder";
		o["Slice"] = 0;
		o["Taper"] = Round( taper );
		// Read-only values an old serializer once wrote alongside the brush — stale for the new shape, and ignored
		// on load; drop them rather than leave a misleading number behind.
		o.Remove( "LocalCentre" );
		o.Remove( "BoundingRadius" );
	}

	// ── tiny value helpers (the engine writes vectors as "x,y,z" strings, numbers as plain JSON numbers) ──

	static float GetFloat( JsonObject o, string key )
	{
		if ( o[key] is not JsonValue v ) return 0f;
		if ( v.TryGetValue( out double d ) ) return (float)d;
		if ( v.TryGetValue( out string s ) && float.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ) return f;
		return 0f;
	}

	static float[] GetParts( JsonObject o, string key )
	{
		if ( o[key] is not JsonValue v || !v.TryGetValue( out string s ) ) return null;
		var parts = s.Split( ',' );
		var r = new float[parts.Length];
		for ( int i = 0; i < parts.Length; i++ )
			if ( !float.TryParse( parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i] ) ) return null;
		return r;
	}

	static Vector3 GetVec( JsonObject o, string key, Vector3 fallback )
	{
		var p = GetParts( o, key );
		return p is { Length: 3 } ? new Vector3( p[0], p[1], p[2] ) : fallback;
	}

	static Rotation GetRot( JsonObject o, string key )
	{
		var p = GetParts( o, key );
		return p is { Length: 4 } ? new Rotation( p[0], p[1], p[2], p[3] ) : Rotation.Identity;
	}

	static double Round( float f ) => Math.Round( f, 5 );

	// The engine's own float text (shortest round-trip, e.g. "10.273201", "-3.7460095E-06").
	static string N( float f ) => f.ToString( "R", CultureInfo.InvariantCulture );
	static string Fmt( Rotation r ) => $"{N( r.x )},{N( r.y )},{N( r.z )},{N( r.w )}";

	/// <summary>Write a "x,y,z" vector, keeping each component's ORIGINAL text where its value didn't change —
	/// so a converted brush's untouched axes diff as nothing (no re-rounding of authored numbers).</summary>
	static void SetVec( JsonObject o, string key, Vector3 before, Vector3 after )
	{
		string[] orig = o[key] is JsonValue v && v.TryGetValue( out string s ) ? s.Split( ',' ) : null;
		string C( int i, float b, float a ) => orig is { Length: 3 } && a == b ? orig[i].Trim() : N( a );
		o[key] = $"{C( 0, before.x, after.x )},{C( 1, before.y, after.y )},{C( 2, before.z, after.z )}";
	}
}
