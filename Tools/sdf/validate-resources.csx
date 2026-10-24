// sbox-cli --call run_csharp with {"path":"<project>/Tools/sdf/validate-resources.csx"}.
// Run in an editor with SDF props open. Temporarily toggles/restores two renderers and one brush list;
// does not save scenes. Includes GPU readback stalls and a 4.2-second cache expiry check.
// Run through sbox MCP run_csharp with path. Uses temporary GPU resources and releases them in finally.
var passed=new System.Collections.Generic.List<string>();
void Check(bool ok,string name){ if(!ok)throw new Exception(name); passed.Add(name); }
var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
object Get(object o,string name)=>o.GetType().GetField(name,flags).GetValue(o);
var data=new float[128*7*4]; var spline=new float[256*4];
var list=new System.Collections.Generic.List<Mimiclay.SdfBrush>();
using var atlas=new Mimiclay.SdfTextAtlas();
int Pack()=>Mimiclay.SdfBrushPacker.Pack(list,Transform.Zero,data,spline,128,7,256,atlas);
void VerifySlots(){
 var pixels=new float[256*128*8]; atlas.Texture.GetPixels3D((0,0,0,256,128*8,1),0,pixels.AsSpan(),ImageFormat.R32F);
 var enabled=list.Where(b=>b.Enabled&&b.Shape==Mimiclay.SdfShape.Text).ToArray();
 for(int i=0;i<Math.Min(enabled.Length,8);i++){
 var expected=enabled[i==7?enabled.Length-1:i].TextData.Grid;
 if(!expected.AsSpan().SequenceEqual(pixels.AsSpan(i*256*128,256*128)))throw new Exception("Text pixels differ at slot "+i);
 }
}
Check(atlas.Texture.IsValid()&&atlas.EstimatedBytes==0,"Unpopulated atlas has a valid shared binding with zero owned bytes");
list.Add(new Mimiclay.SdfBrush {Shape=Mimiclay.SdfShape.Sphere,Size=8}); Pack();
Check(atlas.EstimatedBytes==0,"Non-text pack allocates no text atlas");
list.Clear(); for(int i=0;i<9;i++)list.Add(new Mimiclay.SdfBrush {Shape=Mimiclay.SdfShape.Text,Text="TEST "+i,Size=new Vector3(16,8,2)});
Pack(); VerifySlots(); Check(atlas.EstimatedBytes==1048576,"Eight-slot text atlas retains exact uploaded float values and overflow behavior");
list.Reverse(); list[0].Text="EDITED"; Pack(); VerifySlots(); passed.Add("Reordering and editing text updates the correct GPU slots");
foreach(var b in list)b.Enabled=false; var oldAtlas=atlas.Texture; Pack();
Check(atlas.EstimatedBytes==0&&!oldAtlas.IsValid()&&Texture.White.IsValid(),"Disabling all text releases the atlas without disposing the shared fallback");
list[0].Enabled=true; Pack(); VerifySlots(); passed.Add("Re-enabling text reuploads cached glyphs");
list.Clear(); Pack(); Check(atlas.EstimatedBytes==0,"Removing every brush releases the atlas");
var f=new Mimiclay.SdfFieldGpu();
try {
 list.Add(new Mimiclay.SdfBrush {Shape=Mimiclay.SdfShape.Sphere,Size=8});
 Check(f.Evaluate(list,new Vector3(-8),new Vector3(8),24,false),"Dense compute bake succeeds with fallback text binding");
 Check(f.TextAtlasBytes==0&&f.InputTextureBytes==18432,"Dense accounting includes persistent inputs and no phantom text");
 var oldVolume=f.Texture;
 Check(f.Evaluate(list,new Vector3(-8),new Vector3(8),32,true),"Sparse compute bake succeeds");
 Check(!oldVolume.IsValid()&&f.Atlas.IsValid()&&f.IndirectionTex.IsValid(),"Dense-to-sparse replacement releases the old volume");
 var oldSparse=new[]{f.Texture,f.Occupancy,f.Atlas,f.IndirectionTex};
 Check(f.Evaluate(list,new Vector3(-8),new Vector3(8),24,false),"Sparse-to-dense rebake succeeds");
 Check(oldSparse.All(t=>!t.IsValid())&&!f.Atlas.IsValid()&&!f.Occupancy.IsValid()&&f.ReadBrickCount()==-1,"Sparse resources released on return to dense");
 list[0].Shape=Mimiclay.SdfShape.Text; list[0].Text="GPU";
 Check(f.Evaluate(list,new Vector3(-8),new Vector3(8),24,false)&&f.TextAtlasBytes==1048576,"Text field owns and accounts for its atlas");
 list[0].Shape=Mimiclay.SdfShape.Sphere;
 Check(f.Evaluate(list,new Vector3(-8),new Vector3(8),24,false)&&f.TextAtlasBytes==0,"Text-to-primitive field edit releases atlas");
 f.Dispose(); f.Dispose();
 Check(f.EstimatedBytes==0&&!f.IsValid,"Field disposal is complete and repeatable");
 var bindings=typeof(RenderAttributes).GetField("usedTextures",flags);
 foreach(var name in new[]{"_cs","_classifyCs","_allocCs","_fillCs"}){
 var cs=Get(f,name) as ComputeShader;
 if(cs==null)continue;
 var d=bindings.GetValue(cs.Attributes) as System.Collections.IDictionary;
 Check(d==null||d.Count==0,"Disposal clears retained texture handles: "+name);
 }
}finally{f.Dispose();}
// Check real lifecycle hooks without creating or saving scene objects. Restore component state on failure.
var renderers=Editor.SceneEditorSession.All.SelectMany(s=>s.Scene.GetAllObjects(true)).SelectMany(g=>g.Components.GetAll<Mimiclay.SdfRaymarchRenderer>(FindMode.EverythingInSelf)).Where(r=>r.Enabled).ToArray();
foreach(var r in renderers.Where(r=>((Mimiclay.SdfTextAtlas)Get(r,"_textAtlas")).EstimatedBytes>0).Take(1).Concat(renderers.Where(r=>((Mimiclay.SdfTextAtlas)Get(r,"_textAtlas")).EstimatedBytes==0).Take(1))){
 var text=(Mimiclay.SdfTextAtlas)Get(r,"_textAtlas"); var oldData=(Texture)Get(r,"_dataTex"); var oldField=(Mimiclay.SdfFieldGpu)Get(r,"_fieldGpu");
 if(oldData==null||oldField==null)continue;
 using var sceneScope=r.Scene.Push();
 try{
 r.Enabled=false;
 Check(!oldData.IsValid()&&text.EstimatedBytes==0&&Get(r,"_fieldGpu")==null,"Disabling renderer releases local resources: "+r.GameObject.Name);
 r.Enabled=true; r.ForceRefresh();
 Check(((Texture)Get(r,"_dataTex")).IsValid()&&Get(r,"_fieldGpu")==oldField,"Re-enabling renderer restores inputs and reuses grace field: "+r.GameObject.Name);
 }finally{r.Enabled=true;r.ForceRefresh();}
}
var editable=renderers.First(r=>Get(r,"_fieldGpu") is Mimiclay.SdfFieldGpu);
var sculpture=editable.GameObject.Components.Get<Mimiclay.SdfSculpture>();
var originalBrushes=sculpture.Brushes;
using(var editScope=editable.Scene.Push())try{
 sculpture.Brushes=new System.Collections.Generic.List<Mimiclay.SdfBrush>();
 editable.ForceRefresh();
 Check(Get(editable,"_dataTex")==null&&Get(editable,"_fieldGpu")==null,"Removing the final brush releases renderer resources");
 sculpture.Brushes=new System.Collections.Generic.List<Mimiclay.SdfBrush>{new Mimiclay.SdfBrush{Enabled=false}};
 editable.ForceRefresh();editable.ForceRefresh();
 Check(Get(editable,"_dataTex")==null,"An all-disabled brush list does not repeatedly allocate inputs");
}finally{sculpture.Brushes=originalBrushes;editable.ForceRefresh();}
Check(((Texture)Get(editable,"_dataTex")).IsValid()&&Get(editable,"_fieldGpu") is Mimiclay.SdfFieldGpu,"Restoring brushes recreates renderer resources");
// No call to Sweep after Release: the scene maintenance hook must expire this otherwise-idle entry.
var cacheMap=(System.Collections.IDictionary)typeof(Mimiclay.SdfFieldCache).GetField("_byKey",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).GetValue(null);
int key=Guid.NewGuid().GetHashCode(); while(cacheMap.Contains(key))key++;
var validationScene = Scene.CreateEditorScene();
Check(validationScene.GetSystem<Mimiclay.SdfFieldCacheSystem>() != null, "Fresh scenes install cache maintenance");
var cached=Mimiclay.SdfFieldCache.Acquire(key,null,out var bake);
try{
 Check(bake&&cached.Evaluate(list,new Vector3(-8),new Vector3(8),16,false),"Temporary cache entry baked");
 Mimiclay.SdfFieldCache.Release(cached);
 await Task.Delay(4200,cancellationToken);
 validationScene.EditorTick(RealTime.Now, 0.016f);
 Check(!cacheMap.Contains(key)&&!cached.IsValid,"Unowned cache entry expires without another acquisition");
}finally{Mimiclay.SdfFieldCache.Discard(cached);validationScene.Destroy();}
System.IO.File.WriteAllText(System.IO.Path.Combine(exchangeDirectory,"resource-validation.json"),Json.Serialize(passed));
return new {Passed=passed.Count,Checks=passed};
