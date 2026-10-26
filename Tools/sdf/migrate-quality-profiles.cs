// Migration to SdfQualityProfile (2026-10-25/26). Run from the repo root (re-runnable: a clean file is left alone):
//   dotnet run Tools/sdf/migrate-quality-profiles.cs -- [--write] [--report <path>] [--skip a.scene,b.scene]
//       [--res-overrides Tools/sdf/mesh-resolution-overrides.json]
// Without --write it only reports. With --write it rewrites every .prefab/.scene that holds an SdfRaymarchRenderer:
//  - strips the profile-governed quality keys and the dead density keys from each renderer component, so it inherits
//    from its profile (Standard unless assigned below);
//  - strips scene/prefab instance PATCHES of those keys (PropertyOverrides entries targeting a renderer);
//  - assigns Profile by rule (pawn heads/bodies/hands -> Player; everything else inherits Standard);
//  - strips SdfSculpture.Resolution (mesh LOD0 cells now come from the profile's Mesh LODs rule) and its instance
//    patches, keeping it as ResolutionOverride where --res-overrides lists the component ("path|componentGuid" ->
//    authored count; computed in the editor as the sculptures whose rule result was coarser than the authored count).
// The drift check that keeps it clean afterwards is Tools/sdf/validate-quality-drift.csx.
// Formatting is preserved (indent, CRLF/LF, trailing newline, number text); run `git diff --stat` afterwards — a
// migrated file should only LOSE lines, plus one "Profile" line per assigned renderer.
// The report lists genuinely unusual values (not one of the known generator/code-default sets) for review.
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

bool write = args.Contains( "--write" );
string reportPath = args.SkipWhile( a => a != "--report" ).Skip( 1 ).FirstOrDefault() ?? "Tools/sdf/quality-migration-report.md";
// --skip a.scene,b.scene: leave these (paths relative to Assets) untouched — e.g. scenes open in the editor with unsaved work.
var skip = (args.SkipWhile( a => a != "--skip" ).Skip( 1 ).FirstOrDefault() ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries )
	.Select( s => s.Replace( '\\', '/' ).ToLowerInvariant() ).ToHashSet();
var skipped = new List<string>();
string assets = "Assets";
if ( !Directory.Exists( assets ) ) { Console.Error.WriteLine( "run from the repo root" ); return 1; }

// The keys the renderer no longer serializes: profile-governed (now "...Override" nullables) + the dead density keys.
string[] governed = { "MaxSteps", "Epsilon", "AdaptiveQuality", "MinSteps", "FarEpsilon", "FullQualityRadii", "MinQualityRadii",
	"DistanceSwitching", "MeshLod1Radii", "MeshLod2Radii", "CullRadii", "LodHysteresis", "SdfShadowRadii",
	"OverdrawOptimization", "BrushCulling", "DepthOcclusionCull" };
string[] dead = { "FieldResolution", "SparseField", "FieldDensity" };
var removed = governed.Concat( dead ).ToHashSet();

// Known value sets: the code defaults and the two prop-generator generations. Anything else is "unusual".
var known = new Dictionary<string, string[]>
{
	["MaxSteps"] = new[] { "96", "50" }, ["Epsilon"] = new[] { "0.05", "0.18" },
	["AdaptiveQuality"] = new[] { "true", "false" }, ["DistanceSwitching"] = new[] { "true", "false" },
	["MinSteps"] = new[] { "28", "6", "12" }, ["FarEpsilon"] = new[] { "0.4", "0.5", "1" },
	["FullQualityRadii"] = new[] { "6", "2", "1" }, ["MinQualityRadii"] = new[] { "60", "12", "15" },
	["MeshLod1Radii"] = new[] { "90", "30", "20" }, ["MeshLod2Radii"] = new[] { "140", "40", "30" },
	["CullRadii"] = new[] { "220", "80", "200" }, ["LodHysteresis"] = new[] { "0.06" },
	["SdfShadowRadii"] = new[] { "10", "3" }, ["OverdrawOptimization"] = new[] { "true", "false" },
	["BrushCulling"] = new[] { "true" }, ["DepthOcclusionCull"] = new[] { "true" },
	["FieldResolution"] = new[] { "256", "195", "64" }, ["SparseField"] = new[] { "true", "false" },
	["FieldDensity"] = new[] { "\"World\"" },
};

const string PlayerProfile = "sdf/quality/player.sdfq";
// Pawn prefabs: every renderer on them is a pawn head/body/hand.
string[] pawnPrefabs = { "Prefabs/hunter.prefab", "Prefabs/Charades Pawn Taylorr.prefab" };

var files = Directory.EnumerateFiles( assets, "*.*", SearchOption.AllDirectories )
	.Where( p => p.EndsWith( ".prefab" ) || p.EndsWith( ".scene" ) )
	.Select( p => p.Replace( '\\', '/' ) ).OrderBy( p => p ).ToList();

// Pass 1: component guid -> type over every prefab and scene (instance patches name the PREFAB's component ids).
var typeOf = new Dictionary<string, string>();
var nameOf = new Dictionary<string, string>();
foreach ( var f in files )
{
	var txt = File.ReadAllText( f );
	if ( !txt.Contains( "__type" ) ) continue;
	foreach ( var (go, c) in Components( JsonNode.Parse( txt ), null ) )
		if ( (string)c["__guid"] is string g ) { typeOf[g] = (string)c["__type"]; nameOf[g] = go; }
}

var report = new StringBuilder();
var unusual = new List<string>();
var patchNotes = new List<string>();
int filesChanged = 0, renderers = 0, keysRemoved = 0, patchesRemoved = 0, playerAssigned = 0;
var turnedOn = new Dictionary<string, int>();
var displaced = new List<string>();
var sculptRes = new Dictionary<string, int>();
var sculptOdd = new List<string>();
int overridesKept = 0;
string resOverridePath = args.SkipWhile( a => a != "--res-overrides" ).Skip( 1 ).FirstOrDefault();
var resOverrides = resOverridePath is not null && File.Exists( resOverridePath )
	? JsonNode.Parse( File.ReadAllText( resOverridePath ) )!.AsObject().ToDictionary( kv => kv.Key, kv => (int)kv.Value! ) : new Dictionary<string, int>();

var opts = new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var reformatted = new List<string>();

foreach ( var f in files )
{
	var txt = File.ReadAllText( f );
	if ( !txt.Contains( "SdfRaymarchRenderer" ) && !txt.Contains( "SdfSculpture" ) && !removed.Any( k => txt.Contains( $"\"Property\": \"{k}\"" ) ) ) continue;
	string rel = f.Substring( assets.Length + 1 );
	if ( skip.Contains( rel.ToLowerInvariant() ) ) { skipped.Add( rel ); continue; }
	var root = JsonNode.Parse( txt );
	bool changed = false;
	bool pawn = pawnPrefabs.Contains( rel );

	foreach ( var (go, c) in Components( root, null ).ToList() )
	{
		// SdfSculpture.Resolution (mesh LOD0 cell count) moved onto the profile's Mesh LODs rule (2026-10-26).
		if ( (string)c["__type"] == "Mimiclay.SdfSculpture" )
		{
			if ( c["Resolution"] is JsonNode rv )
			{
				string s = rv.ToJsonString();
				sculptRes[s] = sculptRes.GetValueOrDefault( s ) + 1;
				// Keep an authored count as an override where the profile rule would mesh it COARSER (thin parts and
				// holes the generator gave extra cells). The list is computed in the editor from each sculpture's
				// brush bounds (Tools/sdf/mesh-resolution-overrides.json: "path|componentGuid" -> authored).
				bool keep = resOverrides.TryGetValue( $"{rel}|{(string)c["__guid"]}", out int pinned );
				if ( s != "32" ) sculptOdd.Add( $"| {rel} | {go} | {s} | {(keep ? "kept as override" : "profile rule")} |" );
				c.Remove( "Resolution" );
				if ( keep && c["ResolutionOverride"] is null )
				{
					InsertAfter( (JsonObject)c, "ResolutionOverride", JsonValue.Create( pinned ) );
					overridesKept++;
				}
				keysRemoved++; changed = true;
			}
			continue;
		}
		if ( (string)c["__type"] != "Mimiclay.SdfRaymarchRenderer" ) continue;
		renderers++;
		var odd = new List<string>();
		foreach ( var k in removed )
		{
			if ( c[k] is not JsonNode v ) continue;
			string s = v.ToJsonString();
			if ( known.TryGetValue( k, out var ok ) && !ok.Contains( s ) ) odd.Add( $"{k}={s}" );
			if ( (k == "AdaptiveQuality" || k == "DistanceSwitching" || k == "OverdrawOptimization") && s == "false" )
				turnedOn[k] = turnedOn.GetValueOrDefault( k ) + 1;
			c.Remove( k );
			keysRemoved++; changed = true;
		}
		if ( c["Displace"]?.ToJsonString() == "true" ) displaced.Add( $"{rel} :: {go}" );
		if ( odd.Count > 0 ) unusual.Add( $"| {rel} | {go} | {string.Join( ", ", odd )} |" );

		if ( pawn && c["Profile"] is null )
		{
			// Alphabetical position, like s&box writes it.
			InsertAfter( (JsonObject)c, "Profile", JsonValue.Create( PlayerProfile ) );
			playerAssigned++; changed = true;
		}
	}

	foreach ( var (owner, list) in PatchLists( root ) )
	{
		for ( int i = list.Count - 1; i >= 0; i-- )
		{
			var e = list[i] as JsonObject;
			string prop = (string)e?["Property"];
			string id = (string)e?["Target"]?["IdValue"];
			string type = id is not null && typeOf.TryGetValue( id, out var t ) ? t : null;
			// "Resolution" is a common name: only strip patches that resolve to a sculpture.
			if ( prop == "Resolution" && type == "Mimiclay.SdfSculpture" )
			{
				patchNotes.Add( $"| {rel} | SdfSculpture.Resolution={e["Value"]?.ToJsonString()} |" );
				list.RemoveAt( i );
				patchesRemoved++; changed = true;
				continue;
			}
			if ( prop is null || !removed.Contains( prop ) ) continue;
			// Only strip patches that target a renderer (SdfStageRig also has MaxSteps/Epsilon). Unknown targets are
			// reported, and stripped only for keys no other component in the project has.
			bool unambiguous = prop is not ("MaxSteps" or "Epsilon");
			if ( type == "Mimiclay.SdfRaymarchRenderer" || (type is null && unambiguous) )
			{
				patchNotes.Add( $"| {rel} | {prop}={e["Value"]?.ToJsonString()}{(type is null ? " (target unresolved)" : "")} |" );
				list.RemoveAt( i );
				patchesRemoved++; changed = true;
			}
			else if ( type is null )
				patchNotes.Add( $"| {rel} | {prop}={e["Value"]?.ToJsonString()} — KEPT (target unresolved, ambiguous key) |" );
		}
	}

	if ( !changed ) continue;
	filesChanged++;
	if ( write )
	{
		string nl = txt.Contains( "\r\n" ) ? "\r\n" : "\n";
		string regenerated = root.ToJsonString( opts );
		// Keep the ORIGINAL text of every surviving line (s&box files disagree on escaping: some write ', some a
		// raw apostrophe), so a migrated file only loses lines. Files in another layout (the PowerShell-written prop
		// generator output) don't merge and get the regenerated text, which is the layout s&box itself saves.
		var merged = MergeKeepingOriginal( txt.Replace( "\r\n", "\n" ).Split( '\n' ), regenerated.Replace( "\r\n", "\n" ).Split( '\n' ) );
		if ( merged is null ) reformatted.Add( rel );
		string outText = string.Join( nl, merged ?? regenerated.Replace( "\r\n", "\n" ).Split( '\n' ) );
		if ( txt.EndsWith( "\n" ) && !outText.EndsWith( "\n" ) ) outText += nl;
		File.WriteAllText( f, outText, new UTF8Encoding( txt.Length > 0 && txt[0] == '﻿' ) );
	}
}

report.AppendLine( "# SDF quality-profile migration report" );
report.AppendLine();
report.AppendLine( $"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by Tools/sdf/migrate-quality-profiles.cs ({(write ? "written" : "dry run")})." );
report.AppendLine();
report.AppendLine( $"- files changed: {filesChanged}" );
if ( skipped.Count > 0 ) report.AppendLine( $"- SKIPPED (re-run on them later): {string.Join( ", ", skipped )}" );
if ( reformatted.Count > 0 ) report.AppendLine( $"- reformatted to s&box's own layout (were PowerShell ConvertTo-Json output, same JSON): {string.Join( ", ", reformatted )}" );
report.AppendLine( $"- renderer components: {renderers} (keys stripped: {keysRemoved})" );
report.AppendLine( $"- instance patches stripped: {patchesRemoved}" );
report.AppendLine( $"- renderers assigned the Player profile: {playerAssigned} (pawn prefabs: {string.Join( ", ", pawnPrefabs )}); every other renderer inherits Standard" );
report.AppendLine( $"- previously OFF, now inherited ON from the profile: {string.Join( ", ", turnedOn.Select( p => $"{p.Key} {p.Value}" ) )}" );
report.AppendLine();
report.AppendLine( "## Unusual authored values (not a code default or a generator set) — override candidates" );
report.AppendLine();
report.AppendLine( "| file | object | values |" );
report.AppendLine( "|---|---|---|" );
foreach ( var u in unusual ) report.AppendLine( u );
report.AppendLine();
report.AppendLine( "## Instance patches stripped (someone changed these on an instance — deliberate?)" );
report.AppendLine();
report.AppendLine( "| file | value | instances |" );
report.AppendLine( "|---|---|---|" );
foreach ( var g in patchNotes.GroupBy( p => p ).OrderBy( g => g.Key ) ) report.AppendLine( $"{g.Key} {g.Count()} |" );
report.AppendLine();
if ( sculptRes.Count > 0 )
{
	report.AppendLine( "## SdfSculpture.Resolution stripped (mesh LOD0 cells now come from the profile's Mesh LODs rule)" );
	report.AppendLine();
	report.AppendLine( string.Join( ", ", sculptRes.OrderByDescending( p => p.Value ).Select( p => $"{p.Value}× {p.Key}" ) ) );
	report.AppendLine();
	report.AppendLine( $"Kept as ResolutionOverride (rule would be coarser): {overridesKept}" );
	report.AppendLine();
	report.AppendLine( "| file | object | authored resolution (not 32) | now |" );
	report.AppendLine( "|---|---|---|---|" );
	foreach ( var s in sculptOdd ) report.AppendLine( s );
	report.AppendLine();
}
report.AppendLine( "## Displaced props (now hand off to an undisplaced mesh at distance)" );
report.AppendLine();
foreach ( var d in displaced ) report.AppendLine( $"- {d}" );
File.WriteAllText( reportPath, report.ToString() );
Console.WriteLine( $"{(write ? "wrote" : "dry run:")} files={filesChanged} renderers={renderers} keys={keysRemoved} patches={patchesRemoved} player={playerAssigned} unusual={unusual.Count} patchNotes={patchNotes.Count} -> {reportPath}" );
return 0;

// The regenerated file is the original minus some lines (plus an inserted "Profile" line and trailing-comma changes).
// Walk both and emit the ORIGINAL text of each surviving line, so escaping and number text stay byte-identical.
// Returns null when the files don't line up (another layout), or if the result isn't the same JSON.
static string[] MergeKeepingOriginal( string[] orig, string[] neu )
{
	static string Key( string line ) => System.Text.RegularExpressions.Regex.Replace( line.Trim().TrimEnd( ',' ), @"\\u([0-9A-Fa-f]{4})",
		m => ((char)Convert.ToInt32( m.Groups[1].Value, 16 )).ToString() );
	var origKeys = orig.Select( Key ).ToArray();
	var result = new List<string>( neu.Length );
	int i = 0;
	foreach ( var n in neu )
	{
		string nk = Key( n );
		int j = i;
		while ( j < orig.Length && origKeys[j] != nk ) j++;
		if ( j == orig.Length )
		{
			if ( n.TrimStart().StartsWith( "\"Profile\"" ) || n.TrimStart().StartsWith( "\"ResolutionOverride\"" ) ) { result.Add( n ); continue; }
			return null;
		}
		string o = orig[j];
		bool nc = n.TrimEnd().EndsWith( "," ), oc = o.TrimEnd().EndsWith( "," );
		if ( nc && !oc ) o = o.TrimEnd() + ",";
		else if ( !nc && oc ) o = o.TrimEnd().TrimEnd( ',' );
		result.Add( o );
		i = j + 1;
	}
	var a = JsonNode.Parse( string.Join( "\n", result ) );
	var b = JsonNode.Parse( string.Join( "\n", neu ) );
	return JsonNode.DeepEquals( a, b ) ? result.ToArray() : null;
}

// Every component object under a node, with its GameObject's name.
static IEnumerable<(string Go, JsonObject C)> Components( JsonNode node, string go )
{
	if ( node is JsonObject o )
	{
		string name = o["Name"] is JsonValue nv && nv.TryGetValue<string>( out var s ) ? s : go;
		if ( o["Components"] is JsonArray comps )
			foreach ( var c in comps.OfType<JsonObject>() ) yield return (name, c);
		foreach ( var kv in o )
			if ( kv.Key != "Components" && kv.Value is not null )
				foreach ( var x in Components( kv.Value, name ) ) yield return x;
	}
	else if ( node is JsonArray a )
		foreach ( var e in a ) if ( e is not null ) foreach ( var x in Components( e, go ) ) yield return x;
}

// Every PropertyOverrides list in prefab-instance patches, with the instance's prefab path.
static IEnumerable<(string Owner, JsonArray List)> PatchLists( JsonNode node )
{
	if ( node is JsonObject o )
	{
		if ( o["__PrefabInstancePatch"]?["PropertyOverrides"] is JsonArray l )
			yield return ((o["__Prefab"] as JsonValue)?.ToString() ?? (string)o["Name"] ?? "?", l);
		foreach ( var kv in o ) if ( kv.Value is not null ) foreach ( var x in PatchLists( kv.Value ) ) yield return x;
	}
	else if ( node is JsonArray a )
		foreach ( var e in a ) if ( e is not null ) foreach ( var x in PatchLists( e ) ) yield return x;
}

// JsonObject has no ordered insert, so rebuild it with (key, value) at its alphabetical position.
static void InsertAfter( JsonObject o, string key, JsonNode value )
{
	var items = o.ToList();
	o.Clear();
	bool placed = false;
	foreach ( var kv in items )
	{
		if ( !placed && !kv.Key.StartsWith( "__" ) && kv.Key != "Flags" && string.CompareOrdinal( kv.Key, key ) > 0 )
		{ o[key] = value; placed = true; }
		o[kv.Key] = kv.Value;
	}
	if ( !placed ) o[key] = value;
}
