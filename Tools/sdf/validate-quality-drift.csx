// sbox-cli --call run_csharp with {"path":"<project>/Tools/sdf/validate-quality-drift.csx"}. Read-only: scans prefab and scene
// files on disk plus the loaded quality profiles. Writes quality-drift.json to the exchange directory; throws on stale keys.
var passed=new System.Collections.Generic.List<string>();
void Check(bool ok,string name){ if(!ok)throw new Exception(name); passed.Add(name); }
// ── Quality-profile drift check (2026-10-25). Renderers inherit march/LOD/overdraw/density settings from an
// SdfQualityProfile; per-prop values live only in the nullable "...Override" properties. This scans every prefab and
// scene ON DISK for (a) stale serialized quality keys — the pre-profile property names, which the renderer ignores, so
// a file carrying them was written by old code or a generator that still emits them (Tools/PropGen) — in renderer
// components and in prefab-instance patches; (b) overrides, listed for review (they should be rare and deliberate);
// (c) profiles and overrides whose distance chain is out of order. Also SdfSculpture.Resolution (mesh LOD0 cells, now
// the profile's Mesh LODs rule) and its ResolutionOverride. Stale keys fail the run; overrides are reported.
var staleKeys=new System.Collections.Generic.HashSet<string>{"MaxSteps","Epsilon","AdaptiveQuality","MinSteps","FarEpsilon","FullQualityRadii","MinQualityRadii","DistanceSwitching","MeshLod1Radii","MeshLod2Radii","CullRadii","LodHysteresis","SdfShadowRadii","OverdrawOptimization","BrushCulling","DepthOcclusionCull","FieldResolution","SparseField","FieldDensity"};
var stale=new System.Collections.Generic.List<string>(); var overrides=new System.Collections.Generic.List<string>(); var chain=new System.Collections.Generic.List<string>();
string assetsRoot=Project.Current.GetAssetsPath();
var rendererIds=new System.Collections.Generic.HashSet<string>(); var sculptIds=new System.Collections.Generic.HashSet<string>();
var docs=new System.Collections.Generic.List<(string Rel,System.Text.Json.Nodes.JsonNode Root)>();
foreach(var path in System.IO.Directory.EnumerateFiles(assetsRoot,"*.*",System.IO.SearchOption.AllDirectories).Where(p=>p.EndsWith(".prefab")||p.EndsWith(".scene"))){
 var txt=System.IO.File.ReadAllText(path); if(!txt.Contains("SdfRaymarchRenderer")&&!txt.Contains("SdfSculpture")&&!staleKeys.Any(k=>txt.Contains("\"Property\": \""+k+"\"")))continue;
 docs.Add((System.IO.Path.GetRelativePath(assetsRoot,path).Replace(System.IO.Path.DirectorySeparatorChar,'/'),System.Text.Json.Nodes.JsonNode.Parse(txt)));
}
System.Collections.Generic.IEnumerable<System.Text.Json.Nodes.JsonObject> Objs(System.Text.Json.Nodes.JsonNode n){
 if(n is System.Text.Json.Nodes.JsonObject o){yield return o;foreach(var kv in o)if(kv.Value!=null)foreach(var x in Objs(kv.Value))yield return x;}
 else if(n is System.Text.Json.Nodes.JsonArray a)foreach(var e in a)if(e!=null)foreach(var x in Objs(e))yield return x;
}
foreach(var (rel,root) in docs)foreach(var o in Objs(root)){ if((string)o["__type"]=="Mimiclay.SdfRaymarchRenderer"&&o["__guid"]!=null)rendererIds.Add((string)o["__guid"]); if((string)o["__type"]=="Mimiclay.SdfSculpture"&&o["__guid"]!=null)sculptIds.Add((string)o["__guid"]); }
float? F(System.Text.Json.Nodes.JsonObject o,string k)=>o[k] is System.Text.Json.Nodes.JsonValue v&&v.TryGetValue<float>(out var f)?f:null;
foreach(var (rel,root) in docs)foreach(var o in Objs(root)){
 if((string)o["__type"]=="Mimiclay.SdfSculpture"){ if(o["Resolution"]!=null)stale.Add($"{rel}: SdfSculpture.Resolution={o["Resolution"].ToJsonString()} (mesh resolution comes from the profile now)"); if(o["ResolutionOverride"] is System.Text.Json.Nodes.JsonValue)overrides.Add($"{rel}: SdfSculpture.ResolutionOverride={o["ResolutionOverride"].ToJsonString()}"); }
 if((string)o["__type"]=="Mimiclay.SdfRaymarchRenderer"){
  foreach(var kv in o){
   if(staleKeys.Contains(kv.Key))stale.Add($"{rel}: {kv.Key}={kv.Value?.ToJsonString()}");
   else if(kv.Key.EndsWith("Override")&&kv.Value!=null)overrides.Add($"{rel}: {kv.Key}={kv.Value.ToJsonString()}");
  }
 }
 if(o["Property"] is System.Text.Json.Nodes.JsonValue pv&&pv.TryGetValue<string>(out var prop)&&o["Target"]?["IdValue"] is System.Text.Json.Nodes.JsonValue tv){
  bool renderer=rendererIds.Contains((string)tv);
  if(prop=="Resolution"&&sculptIds.Contains((string)tv))stale.Add($"{rel}: instance patch SdfSculpture.Resolution={o["Value"]?.ToJsonString()}");
  if(staleKeys.Contains(prop)&&(renderer||prop is not ("MaxSteps" or "Epsilon")))stale.Add($"{rel}: instance patch {prop}={o["Value"]?.ToJsonString()}");
  else if(renderer&&prop.EndsWith("Override"))overrides.Add($"{rel}: instance patch {prop}={o["Value"]?.ToJsonString()}");
 }
}
foreach(var p in new[]{Mimiclay.SdfQualityProfile.Standard,Mimiclay.SdfQualityProfile.Player,Mimiclay.SdfQualityProfile.Viewmodel,Mimiclay.SdfQualityProfile.Editing}){
 var links=new[]{p.FullQualityRadii,p.MinQualityRadii,p.MeshLod1Radii,p.MeshLod2Radii,p.CullRadii};
 for(int i=1;i<links.Length;i++)if(links[i]<links[i-1])chain.Add($"profile {p.ResourcePath ?? "builtin"}: chain link {i} ({links[i]}) < link {i-1} ({links[i-1]})");
 if(p.SdfShadowRadii>p.MinQualityRadii)chain.Add($"profile {p.ResourcePath}: shadow handoff {p.SdfShadowRadii} > mesh handoff {p.MinQualityRadii}");
 if(!(p.MinVoxels<=p.KneeVoxels&&p.KneeVoxels<=p.MaxVoxels))chain.Add($"profile {p.ResourcePath}: density min {p.MinVoxels} / knee {p.KneeVoxels} / max {p.MaxVoxels} out of order");
 if(p.ResourcePath==null)chain.Add("a shipped profile failed to load (built-in fallback in use)");
 if(p.DebugLod||p.DebugLodLabels)chain.Add($"profile {p.ResourcePath}: Debug colours/labels left on");
}
System.IO.File.WriteAllText(System.IO.Path.Combine(exchangeDirectory,"quality-drift.json"),Json.Serialize(new{Stale=stale,Overrides=overrides,Chain=chain}));
Check(stale.Count==0,$"No stale serialized quality keys ({stale.Count}: {string.Join("; ",stale.Take(5))})");
Check(chain.Count==0,$"Shipped profiles load and are ordered ({string.Join("; ",chain)})");
passed.Add($"Quality overrides on disk: {overrides.Count} (listed in quality-drift.json)");
return new {Passed=passed.Count,Checks=passed};
