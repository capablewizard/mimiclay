using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mimiclay;

/// <summary>
/// Runtime component that owns a list of SDF brushes and rebuilds itself into a mesh.
/// This is the shared core: the editor authoring tools and the in-game player UI will
/// both drive this same component.
/// </summary>
[Title( "SDF Sculpture" )]
[Category( "SDF" )]
[Icon( "blur_on" )]
public sealed class SdfSculpture : Component, Component.ExecuteInEditor, Component.IHasBounds
{
	// Seed a default sphere as the initial value so a freshly added component always
	// shows something and has a brush to click — edit-mode doesn't run OnStart/OnUpdate.
	[Property]
	public List<SdfBrush> Brushes { get; set; } = new()
	{
		new SdfBrush { Shape = SdfShape.Sphere, Size = 24f },
	};

	[Property, Range( 8, 96 )] public int Resolution { get; set; } = 32;

	[Property] public Material Material { get; set; }

	/// <summary>Optional pre-baked mesh. When set and still matching the current brushes, the sculpture
	/// loads this instead of meshing the field — so a shipped level pays no SDF cost at load. Editing the
	/// brushes makes it stale (hash mismatch) and live meshing resumes until you re-bake. See
	/// <see cref="BakeToAsset"/>.</summary>
	[Property] public SdfBakedMesh BakedMesh { get; set; }

	/// <summary>Flip triangle winding if the surface renders inside-out.</summary>
	[Property] public bool FlipFaces { get; set; }

	/// <summary>Editor-only bake hook, wired up by the SDF tool — writing a .sdfmesh asset needs the
	/// editor assembly, which game code can't reference. Null at runtime.</summary>
	public static Func<SdfSculpture, bool> BakeHandler;

	/// <summary>Editor-only export hook, wired up by the SDF tool — writing a .prefab asset needs the editor
	/// assembly. Turns this sculpture into a reusable prefab you can drop into any scene. Null at runtime (a
	/// shipped client is sandboxed out of the project's asset folder — that's what the local save library is
	/// for). See <see cref="ExportToPrefab"/>.</summary>
	public static Func<SdfSculpture, bool> ExportPrefabHandler;

	/// <summary>Raised at the end of <see cref="Rebuild"/> — i.e. whenever the shape is COMMITTED (a discrete
	/// edit or a gizmo release), never mid-drag (dragging goes through <see cref="RebuildShadowProxy"/>). The
	/// networked hider listens to this to push its authored brushes to the other clients; the SDF core itself
	/// stays networking-agnostic.</summary>
	public event Action Committed;

	/// <summary>Raised on each LIVE-PREVIEW change — the cheap drag path (<see cref="RebuildShadowProxy"/>),
	/// fired every time a handle moves the shape. The networked hider streams these to proxies so a disguise
	/// updates live as it's dragged, then reconciles to the exact shape on <see cref="Committed"/> at release.</summary>
	public event Action Previewed;

	/// <summary>Serialize a brush list to a portable string (per-shape transforms, sizes, materials and all),
	/// so the authored disguise can be sent across the wire. Pairs with <see cref="DeserializeBrushes"/>.</summary>
	public static string SerializeBrushes( List<SdfBrush> brushes ) => Json.Serialize( brushes );

	/// <summary>Rebuild a brush list from <see cref="SerializeBrushes"/> output. Returns null on malformed
	/// input (so a bad payload can never tear down the receiver) rather than throwing.</summary>
	public static List<SdfBrush> DeserializeBrushes( string data )
	{
		if ( string.IsNullOrEmpty( data ) )
			return null;

		try { return Json.Deserialize<List<SdfBrush>>( data ); }
		catch { return null; }
	}

	/// <summary>Bake the current mesh to a reusable .sdfmesh asset and reference it (editor only).</summary>
	[Button( "Bake To Asset" )]
	public void BakeToAsset()
	{
		if ( BakeHandler is null )
		{
			Log.Warning( "Baking needs the editor — select this sculpture so the SDF tool is active, then bake." );
			return;
		}

		BakeHandler( this );
	}

	/// <summary>Export the current sculpture as a reusable <c>.prefab</c> asset (editor only) so it can be
	/// dragged into any scene. Stays live/editable — it doesn't bake the mesh (use <see cref="BakeToAsset"/>
	/// separately for that).</summary>
	[Button( "Export To Prefab" )]
	public void ExportToPrefab()
	{
		if ( ExportPrefabHandler is null )
		{
			Log.Warning( "Exporting needs the editor — select this sculpture so the SDF tool is active, then export." );
			return;
		}

		ExportPrefabHandler( this );
	}

	/// <summary>Save this sculpture to the local <c>.sculpt</c> library (<see cref="SculptLibrary"/>) under the
	/// GameObject's name, so it can be loaded and kept editing IN-GAME (via <c>mimi_sculpt_load &lt;name&gt;</c> or
	/// the edit HUD). Writes to <c>FileSystem.Data</c>, not the project assets — so unlike <see cref="ExportToPrefab"/>
	/// it works at runtime too, and it's the inverse of the prefab export: scene → editable in-game shape.</summary>
	[Button( "Save To .sculpt" )]
	public void SaveToLibrary()
	{
		if ( SculptLibrary.Save( GameObject.Name, this ) )
			Log.Info( $"Saved sculpture \"{GameObject.Name}\" to \"{SculptLibrary.FullPath( GameObject.Name )}\"." );
		else
			Log.Warning( $"Failed to save sculpture \"{GameObject.Name}\" — nothing to save?" );
	}

	/// <summary>
	/// Live-rebuild the mesh as brushes change in the editor. Turn off to benchmark the
	/// raymarch renderer without paying the meshing cost (use the Rebuild Mesh button to
	/// regenerate manually).
	/// </summary>
	[Property] public bool AutoRebuild { get; set; } = true;

	// Bumped on every rebuild request; an async build whose seq is stale by the time it finishes is
	// dropped (a newer edit superseded it) so we never clobber the current mesh with an old one.
	int _buildSeq;

	// Shared Model cache, keyed by a content hash (brushes + resolution + flip + material). A level full
	// of repeated props — or a perf grid of clones — meshes each UNIQUE shape once; identical sculptures
	// share one Model. The value is the in-flight Task, so concurrent identical requests join one build.
	// Bounded (FIFO eviction) so an editing session that churns through configs can't leak Models; a live
	// ModelRenderer keeps its own Model alive regardless, so eviction only drops the cache's reference.
	static readonly Dictionary<int, Task<Model>> _modelCache = new();
	static readonly Queue<int> _cacheOrder = new();
	const int ModelCacheCap = 256;

	protected override void OnEnabled()
	{
		Rebuild();
	}

	// A sculpture with no SdfRaymarchRenderer sibling relies entirely on its ModelRenderer for both pixels
	// AND editor click-selection, which normally works fine — but WITH a raymarch renderer present, that
	// ModelRenderer is deliberately disabled by default (see SdfRaymarchRenderer.ApplyMeshMode), leaving
	// nothing pickable at all. An explicit hitbox here means this GameObject stays clickable either way,
	// regardless of which renderer sibling is actually doing the drawing.
	protected override void DrawGizmos()
	{
		if ( Brushes is { Count: > 0 } && Sdf.TryGetBounds( Brushes, out var bounds ) )
			Gizmo.Hitbox.BBox( bounds );
	}

	// GameObject.GetBounds() — which the editor's generic prefab-preview/asset-thumbnail camera framing
	// (PreviewPrefab.LoadPrefabContent, engine-side) calls to size itself — ONLY ever looks at components
	// implementing Component.IHasBounds. It has nothing to do with ModelRenderer, SceneObject.Bounds, Enabled,
	// or RenderType at all (confirmed straight from GameObject.cs's own GetBounds() implementation). Without
	// this, GetBounds() found nothing here and fell back to a ZERO-SIZE box at WorldPosition — which is what
	// actually produced the "camera crammed inside the model" close-up thumbnails, regardless of anything
	// about the renderer's readiness/visibility (all the earlier SdfRaymarchRenderer/timing fixes were solving
	// real but ultimately unrelated problems). Sdf.TryGetBounds needs no mesh, no renderer, no async wait at
	// all — just the brush list — so this is always correct the instant the component exists.
	BBox Component.IHasBounds.LocalBounds =>
		Brushes is { Count: > 0 } && Sdf.TryGetBounds( Brushes, out var bounds2 ) ? bounds2 : BBox.FromPositionAndSize( Vector3.Zero );

	/// <summary>Rebuild the mesh and force any raymarch renderer on this object to repack.</summary>
	[Button( "Refresh" )]
	public void Refresh()
	{
		Rebuild();
		GameObject.Components.Get<SdfRaymarchRenderer>()?.ForceRefresh();
	}

	/// <summary>Kick off a rebuild. Fire-and-forget: the meshing runs on a worker thread and the Model is
	/// swapped in when it's ready, so this never stalls the caller (level load, player sculpting, clones).
	/// Fires <see cref="Committed"/> at the end — this is the "committed shape" path (release / discrete edits),
	/// which an <see cref="SdfCollider"/> (if present) listens to so its physics rebuilds, never mid-drag.</summary>
	[Button( "Rebuild Mesh" )]
	public void Rebuild()
	{
		_ = RebuildAsync();
		Committed?.Invoke();
	}

	bool _proxyBuilding;   // a proxy build is in flight (single-flight: skip new requests until it's done)
	bool _proxyVisible;    // latest "render it visibly" request (applied when the in-flight build completes)
	static Material _proxyWireMat;

	// The last drag-proxy mesh, built at the LOD1 resolution, so the full rebuild on release can reuse it as
	// LOD1 instead of recomputing it — the drag work becomes part of the final LOD chain.
	SurfaceNetsMesher.MeshData _proxyData;
	int _proxyContentHash;
	bool _proxyDataValid;

	/// <summary>Drag-time shadow: mesh the combined SDF at LOW resolution ASYNCHRONOUSLY (surface-nets on a
	/// worker thread, Model upload on main) and assign it to the ModelRenderer, instead of the full-res
	/// remesh. This is the real boolean — a mostly-subtracted shape casts the right (small) shadow — just
	/// coarse and cheap. Call while dragging a handle, then <see cref="Rebuild"/> on release for the accurate
	/// LOD mesh. The raymarcher remains the live surface. <paramref name="visible"/> = true renders it
	/// normally (a debug view of the proxy itself). Single-flight: one build at a time, latest wins.</summary>
	public void RebuildShadowProxy( bool visible = false )
	{
		// Fire BEFORE the single-flight guard: the brushes have changed for this preview frame regardless of
		// whether a new proxy build actually starts, and the listener (live disguise streaming) cares about the
		// change, not the mesh build.
		Previewed?.Invoke();

		_proxyVisible = visible;
		if ( _proxyBuilding )
			return; // a build is already running; it'll pick up the latest brushes when the next call lands

		_ = RebuildShadowProxyAsync();
	}

	async Task RebuildShadowProxyAsync()
	{
		_proxyBuilding = true;
		try
		{
			var renderer = GameObject.Components.GetOrCreate<ModelRenderer>();
			if ( Brushes is not { Count: > 0 } )
			{
				renderer.Model = null;
				return;
			}

			SdfTextSdf.EnsureBaked( Brushes ); // before the hash below, same as RebuildAsync
			var snapshot = Snapshot( Brushes ); // copy on the main thread before hopping
			var baseMat = Material ?? Material.Load( "materials/dev/reflectivity_50.vmat" );
			int res1 = Math.Max( 4, Resolution / 2 ); // the drag shadow IS the LOD1 mesh (reused on release)
			bool flip = FlipFaces;
			int seq = ++_buildSeq; // also supersedes any in-flight full surface-nets rebuild

			await GameTask.WorkerThread();
			// Mesh the COMBINED SDF at the LOD1 resolution — this IS the boolean (subtraction included),
			// robust and cheap. Cached below so the full rebuild on release reuses it as LOD1 (no recompute).
			var data = SurfaceNetsMesher.ComputeData( snapshot, res1, flip );
			await GameTask.MainThread();

			// Drop the result if a newer build (proxy or full remesh) started, or we went away meanwhile.
			if ( seq != _buildSeq || !this.IsValid() || !renderer.IsValid() || data.IsEmpty )
				return;

			// Cache for reuse as LOD1 by the next full rebuild.
			_proxyData = data;
			_proxyContentHash = ContentHash( snapshot, res1, flip );
			_proxyDataValid = true;

			renderer.Model = new ModelBuilder().AddMesh( SurfaceNetsMesher.Upload( data, baseMat ) ).Create();
			StampTexSeed( renderer );

			if ( _proxyVisible )
			{
				renderer.Enabled = true;
				renderer.RenderType = ModelRenderer.ShadowRenderType.On;
				// Wireframe-fill override so the debug view shows the proxy's triangulation.
				renderer.MaterialOverride = _proxyWireMat ??= Material.FromShader( "shaders/wireframe.shader" );
			}
		}
		catch { /* never crash the game on a proxy build */ }
		finally { _proxyBuilding = false; }
	}

	// The meshed path samples the same triplanar maps as the raymarch, offset per instance by the
	// object's seed (SeedTexOffset in the shaders). SdfRaymarchRenderer re-stamps every frame while it's
	// active; stamping on every Model assignment covers mesh-only rendering and the SceneObject being
	// recreated by the model swap.
	static void StampTexSeed( ModelRenderer renderer )
	{
		if ( renderer.IsValid() && renderer.SceneObject.IsValid() )
			renderer.SceneObject.Attributes.Set( "BoilSeed", SdfRaymarchRenderer.BoilSeedFor( renderer.GameObject ) );
	}

	public async Task RebuildAsync()
	{
		var brushes = Brushes;
		var renderer = GameObject.Components.GetOrCreate<ModelRenderer>();

		if ( brushes is not { Count: > 0 } )
		{
			renderer.Model = null;
			return;
		}

		// Warm the text fields BEFORE hashing, not just before meshing: the hash decides whether we mesh at
		// all (model cache / .sdfmesh match), and an unbaked text brush hashes the same as a baked one bar
		// the readiness bit — so hashing first would keep serving a box that an earlier unbaked build cached.
		SdfTextSdf.EnsureBaked( brushes );

		var baseMat = Material ?? Material.Load( "materials/dev/reflectivity_50.vmat" );
		int res = Resolution;
		bool flip = FlipFaces;
		int content = ContentHash( brushes, res, flip );

		// A matching pre-baked asset short-circuits all meshing: just upload its geometry. A stale bake
		// (brushes edited since) fails the hash and falls through to live meshing until it's re-baked.
		if ( BakedMesh is not null && BakedMesh.SourceHash == content )
		{
			var baked = BakedMesh.BuildModel( baseMat );
			if ( baked is not null )
			{
				renderer.Model = baked;
				StampTexSeed( renderer );
				return;
			}
		}

		int key = HashCode.Combine( content, baseMat );
		int seq = ++_buildSeq;

		// Reuse the drag proxy's mesh as LOD1 if it was built for this exact shape (so the drag work isn't
		// thrown away — it becomes LOD1 of the final chain, and we skip recomputing it here).
		int res1 = Math.Max( 4, res / 2 );
		var reuseLod1 = _proxyData;
		bool haveLod1 = _proxyDataValid && !_proxyData.IsEmpty && _proxyContentHash == ContentHash( brushes, res1, flip );

		// Snapshot the brushes (inside the factory, so it only runs on a cache MISS, on the main thread
		// before any thread hop) so a concurrent edit can't race the worker-thread build.
		var task = GetOrBuildModel( key, () => BuildModelAsync( Snapshot( brushes ), baseMat, res, flip, reuseLod1, haveLod1 ) );

		Model model;
		try { model = await task; }
		catch { return; }

		// Drop the result if a newer rebuild superseded us, or we/the renderer went away during the build.
		if ( seq != _buildSeq || !this.IsValid() || !renderer.IsValid() )
			return;

		renderer.Model = model;
		StampTexSeed( renderer );
	}

	// One build in flight at a time, machine-wide. A scene load kicks EVERY sculpture's rebuild in the same
	// frame; ungated, all their main-thread uploads land together in one enormous frozen frame — and a
	// multi-second freeze on any machine during a networked scene change collapses the editor-test TCP link
	// and drops every client (see the scene-change post-mortems). Serialized, each upload lands on its own
	// frame: props stream in over a second or two, but the game keeps pumping and the session survives.
	static readonly System.Threading.SemaphoreSlim BuildGate = new( 1 );

	// Worker-thread compute (the heavy O(res^3) field sampling) then a hop to the main thread for the
	// cheap GPU upload + model assembly. Builds all three LODs — reusing a precomputed LOD1 (the drag proxy)
	// when one is supplied, so that mesh is computed once and serves as both the drag shadow and final LOD1.
	static async Task<Model> BuildModelAsync( List<SdfBrush> brushes, Material material, int resolution, bool flip,
		SurfaceNetsMesher.MeshData reuseLod1, bool haveLod1 )
	{
		await BuildGate.WaitAsync();
		try
		{
			await GameTask.WorkerThread();

			var d0 = SurfaceNetsMesher.ComputeData( brushes, resolution, flip );
			var d1 = haveLod1 ? reuseLod1 : SurfaceNetsMesher.ComputeData( brushes, Math.Max( 4, resolution / 2 ), flip );
			var d2 = SurfaceNetsMesher.ComputeData( brushes, Math.Max( 4, resolution / 4 ), flip );

			await GameTask.MainThread();

			if ( d0.IsEmpty )
				return null;

			var builder = new ModelBuilder();
			builder.AddMesh( SurfaceNetsMesher.Upload( d0, material ), 0 );
			if ( !d1.IsEmpty ) builder.AddMesh( SurfaceNetsMesher.Upload( d1, material ), 1 );
			if ( !d2.IsEmpty ) builder.AddMesh( SurfaceNetsMesher.Upload( d2, material ), 2 );
			return builder.Create();
		}
		finally
		{
			BuildGate.Release();
		}
	}

	static Task<Model> GetOrBuildModel( int key, Func<Task<Model>> factory )
	{
		if ( _modelCache.TryGetValue( key, out var existing ) && !existing.IsFaulted )
			return existing;

		var task = factory();
		_modelCache[key] = task;
		_cacheOrder.Enqueue( key );

		while ( _cacheOrder.Count > ModelCacheCap )
			_modelCache.Remove( _cacheOrder.Dequeue() );

		return task;
	}

	static List<SdfBrush> Snapshot( List<SdfBrush> brushes )
	{
		// Warm the text fields FIRST, on this (main) thread: the copies carry TextData by reference, and a
		// text brush that reaches the worker without one meshes as a featureless box (SdfTextSdf.EnsureBaked).
		SdfTextSdf.EnsureBaked( brushes );

		var copy = new List<SdfBrush>( brushes.Count );
		foreach ( var b in brushes )
			copy.Add( b.Copy() );
		return copy;
	}

	/// <summary>Deterministic (FNV-1a) content hash of the geometry inputs — resolution + flip + the canonical
	/// per-brush hash (<see cref="SdfBrush.HashInto"/>, the ONE place brush properties are hashed). Stable across
	/// runs, so it can be baked into a .sdfmesh on disk and compared on a later load to tell whether the bake
	/// still matches the brushes. Excludes Material — geometry only (the material is applied when the mesh is
	/// uploaded, not baked into the data).</summary>
	public static int ContentHash( List<SdfBrush> brushes, int resolution, bool flip ) =>
		ContentHashPrefix( brushes, brushes.Count, resolution, flip );

	/// <summary>Content hash of the first <paramref name="count"/> brushes only — identical to what
	/// <see cref="ContentHash"/> produces for a list of exactly that length. Used by <see cref="SculptUndo"/>
	/// to hash the AUTHORED prefix while ignoring the damage tail, without copying the prefix out first.
	/// A separate name rather than an overload so the many <c>cref</c>s to ContentHash stay unambiguous.</summary>
	public static int ContentHashPrefix( List<SdfBrush> brushes, int count, int resolution, bool flip )
	{
		unchecked
		{
			int h = unchecked((int)2166136261);
			void Mix( int x ) { h = (h ^ x) * 16777619; }

			count = Math.Clamp( count, 0, brushes?.Count ?? 0 );

			Mix( resolution );
			Mix( flip ? 1 : 0 );
			Mix( count );

			for ( int i = 0; i < count; i++ )
				brushes[i].HashInto( ref h );

			return h;
		}
	}

	[Button( "Add Sphere" ), Group( "Add" )]
	public void AddSphere() => AddBrush( SdfShape.Sphere, SdfOperation.Add );

	[Button( "Add Box" ), Group( "Add" )]
	public void AddBox() => AddBrush( SdfShape.Box, SdfOperation.Add );

	[Button( "Add Cylinder" ), Group( "Add" )]
	public void AddCylinder() => AddBrush( SdfShape.Cylinder, SdfOperation.Add );

	[Button( "Add Cone" ), Group( "Add" )]
	public void AddCone() => AddBrush( SdfShape.Cone, SdfOperation.Add );

	[Button( "Subtract Sphere" ), Group( "Subtract" )]
	public void SubtractSphere() => AddBrush( SdfShape.Sphere, SdfOperation.Subtract );

	[Button( "Subtract Box" ), Group( "Subtract" )]
	public void SubtractBox() => AddBrush( SdfShape.Box, SdfOperation.Subtract );

	[Button( "Subtract Cylinder" ), Group( "Subtract" )]
	public void SubtractCylinder() => AddBrush( SdfShape.Cylinder, SdfOperation.Subtract );

	[Button( "Subtract Cone" ), Group( "Subtract" )]
	public void SubtractCone() => AddBrush( SdfShape.Cone, SdfOperation.Subtract );

	// Default spline layout: a short straight 3-point tube along X, centred on `center` (sculpture-local).
	static List<Vector4> DefaultSplinePoints( Vector3 center )
	{
		const float r = 10f, span = 16f;
		return new List<Vector4>
		{
			new Vector4( center.x - span, center.y, center.z, r ),
			new Vector4( center.x,        center.y, center.z, r ),
			new Vector4( center.x + span, center.y, center.z, r ),
		};
	}

	/// <summary>Add a brush of the given shape/operation, offset above the last one, and rebuild. Public so
	/// the in-game edit UI can drive it the same way the inspector buttons do.</summary>
	/// <summary>Add a brush of the given shape. Returns false (and adds nothing) at the brush cap — the GPU
	/// packer silently drops brushes past <see cref="SdfBrushPacker.MaxBrushes"/>, so past it the raymarched,
	/// meshed, collision and networked shapes would all quietly disagree. Refusing here keeps them in step.</summary>
	public bool AddBrush( SdfShape shape, SdfOperation operation = SdfOperation.Add )
	{
		Brushes ??= new();

		if ( Brushes.Count >= SdfBrushPacker.MaxBrushes )
		{
			Log.Warning( $"SdfSculpture: brush cap reached ({SdfBrushPacker.MaxBrushes}) — not adding another." );
			return false;
		}

		// New authored brushes insert BEFORE the damage tail (shot craters): craters stay the topmost brushes
		// so they keep carving everything authored, and the edit UI can treat damage as a contiguous tail
		// (rows/indices line up on the authored prefix).
		int insert = AuthoredBrushCount;

		// Stack each new brush above the previous one's height, but always centred on XY (so it never drifts
		// sideways from where earlier brushes were moved to).
		var pos = insert > 0 ? new Vector3( 0f, 0f, Brushes[insert - 1].Position.z + 16f ) : Vector3.Zero;

		// Text sizing: the quad is locked to the glyph slot's 2:1 aspect (a uniform slot→world mapping —
		// anything else stretches the glyphs) and much shallower than the solid shapes (plaque-like).
		var size = shape switch
		{
			SdfShape.Sphere => new Vector3( 16f ),
			SdfShape.Text => new Vector3( 24f, 12f, 4f ),
			_ => new Vector3( 12f ),
		};

		Brushes.Insert( insert, new SdfBrush
		{
			Shape = shape,
			Operation = operation,
			Position = pos,
			Rotation = SpawnRotation( shape ),
			Size = size,
			// A spline starts as a short 3-point tube centred on the stack position; each point is then
			// dragged/sized by its own dots. (xyz = sculpture-local position, w = radius.)
			Points = shape == SdfShape.Spline ? DefaultSplinePoints( pos ) : null,
		} );

		Rebuild();
		return true;
	}

	/// <summary>Brushes before the DAMAGE tail — the authored sculpt the edit UI shows and edits. Damage
	/// brushes (shot craters, <see cref="SdfBrush.Damage"/>) are kept contiguous at the END of the list:
	/// carves append there and <see cref="AddBrush"/> inserts before them, so index i &lt; this count is
	/// always an authored brush and UI row/index math needs no per-row filtering.</summary>
	public int AuthoredBrushCount
	{
		get
		{
			var b = Brushes;
			if ( b is null )
				return 0;

			for ( int i = 0; i < b.Count; i++ )
			{
				if ( b[i].Damage )
					return i;
			}

			return b.Count;
		}
	}

	/// <summary>The rotation a freshly added brush of this shape gets — also what the "Rotate" reset tool
	/// restores. The flat-profile shapes (text, extruded cross-sections) spawn FACING FORWARD instead of
	/// lying flat: a cyclic-permutation rotation mapping local X→world Y, local Y→world Z (profile "up" =
	/// up), and local Z (the extrusion normal) → world X (the sculpture's forward). Identity would leave
	/// the profile face-up on the ground.</summary>
	public static Rotation SpawnRotation( SdfShape shape )
		=> shape is SdfShape.Text or SdfShape.Extruded ? new Rotation( 0.5f, 0.5f, 0.5f, 0.5f ) : Rotation.Identity;
}
