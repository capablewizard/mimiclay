using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// An offscreen studio for one SDF sculpt: a private <see cref="SceneWorld"/> holding a single prop plus its
/// own lighting rig, framed by a camera the caller supplies. This is the render-to-texture foundation — give
/// it brushes, point a camera at it, and you get the prop on transparent black with nothing else in shot.
///
/// <see cref="SdfThumbnail"/> is the UI front end (a ScenePanel whose camera renders this world), and the
/// same stage is what a sculpt library / gallery grid would drive for its thumbnails.
///
/// The prop's GameObject lives in the REAL scene, because that's the only place a Component ticks — but its
/// scene object is created in <see cref="World"/> via <see cref="SdfRaymarchRenderer.TargetWorld"/>. That's
/// the whole isolation story: the game camera renders a different world, so the staged prop can sit at the
/// origin without ever appearing in the map, and no map light, fog or envmap reaches it.
/// </summary>
public sealed class SdfStage : IDisposable
{
	/// <summary>The private world to point a camera at. Never null while the stage is alive.</summary>
	public SceneWorld World { get; private set; }

	/// <summary>Local-space bounds of the current brushes — what <see cref="Frame"/> fits the camera to.</summary>
	public BBox Bounds { get; private set; } = BBox.FromPositionAndSize( Vector3.Zero, 16f );

	/// <summary>False once the host scene has gone (a scene change tears our GameObject out from under us).
	/// The owner should drop the stage and build a fresh one.</summary>
	public bool IsValid => World.IsValid() && _host.IsValid() && _renderer.IsValid();

	GameObject _host;
	SdfSculpture _sculpt;
	SdfRaymarchRenderer _renderer;

	// Lights whose PLACEMENT scales with the subject (point/spot). Directional lights are direction-only, so
	// they're created once and never touched again.
	sealed class PlacedLight
	{
		public SceneLight Light;
		public Vector3 Position;    // as authored, relative to a ReferenceRadius-sized subject at the origin
		public Rotation Rotation;   // as authored — matters for spots, harmless for points
		public float Radius;
	}

	// A directional light plus its authored rotation, kept so Frame can re-derive the swung direction every
	// call (mutating the live rotation in place would compound across frames).
	sealed class PlacedDirectional
	{
		public SceneDirectionalLight Light;
		public Rotation Rotation;
	}

	readonly List<SceneLight> _lights = new();
	readonly List<PlacedLight> _placed = new();
	SceneCubemap _env;

	// The rig's post-process stack, walked by hand (see SdfStagePost) because effect components can't attach
	// to a raw SceneCamera. Lives in the private world as an overlay object, so BOTH the editor preview and
	// the in-game thumbnail get the identical chain.
	readonly SdfStagePost _post = new();
	SdfStagePostObject _postObject;
	SceneSkyBox _sky;

	// Rig values, read from the prefab (or the fallback defaults below).
	Angles _pose = new( 12f, 180f, 0f );
	float _fov = 32f;
	float _margin = 1.2f;
	float _referenceRadius = 32f;
	Color _ambient = Color.White * 0.35f;

	// Subject look, also from the rig. These have to match the real prop's renderer or the thumbnail won't
	// look like the thing it's a thumbnail OF — displacement in particular changes the silhouette.
	Material _surfaceMaterial;
	bool _displace = true;
	bool _transmission = true;
	bool _sdfShadows = true;
	float _sdfShadowBias;
	float _shadowDistance;
	int _maxSteps = 160;
	float _epsilon = 0.02f;

	// Directional lights need their cascade range re-fitted per subject (see Frame) — cascades are sized from
	// the camera, and the default range is built for a whole map, not a prop on a turntable. Also carries the
	// authored rotation so the sun swings with a pose override like the placed lights do.
	readonly List<PlacedDirectional> _directionals = new();

	int _brushHash;
	bool _disposed;

	/// <param name="scene">The scene to host the (invisible) prop GameObject in — normally the active one.</param>
	/// <param name="rig">Lighting/camera rig prefab. Null loads <see cref="SdfStageRig.DefaultPrefabPath"/>;
	/// if that's missing too, a built-in three-point rig stands in.</param>
	public SdfStage( Scene scene, PrefabFile rig = null )
	{
		ArgumentNullException.ThrowIfNull( scene );

		World = new SceneWorld();

		BuildLighting( rig ?? LoadDefaultRig() );

		// The host starts DISABLED so neither component's OnEnabled runs until everything is configured —
		// SdfRaymarchRenderer.OnEnabled calls Refresh() straight away, and before TargetWorld is set that would
		// build the prop's scene object in the GAME world: one frame of a giant head sitting in the map. The
		// components themselves are created enabled, so flipping the host on below wakes them in creation order
		// (sculpt first, which is what Refresh's Components.Get<SdfSculpture> needs).
		// Scoped to the host scene: we're built from a UI tick, which isn't guaranteed to be running under the
		// scene we're creating into, and GameObject/Component creation reads the pushed scene in places.
		using ( scene.Push() )
		{
			_host = scene.CreateObject( false );
			_host.Name = "SDF Stage";
			_host.Flags = GameObjectFlags.NotSaved | GameObjectFlags.Hidden | GameObjectFlags.NotNetworked;
			_host.WorldTransform = global::Transform.Zero;

			_sculpt = _host.Components.Create<SdfSculpture>();
			// The meshed path is dead weight here (we only ever raymarch), but SdfSculpture rebuilds a
			// surface-nets mesh on enable regardless — keep that build trivial.
			_sculpt.Resolution = 8;
			_sculpt.AutoRebuild = false;

			_renderer = _host.Components.Create<SdfRaymarchRenderer>();
			_renderer.TargetWorld = World;
			_renderer.Material = _surfaceMaterial ?? Material.Load( "materials/plasticine.vmat" );
			_renderer.MeshMode = SdfMeshMode.Hidden;
			_renderer.SdfShadows = _sdfShadows;
			_renderer.ShadowBias = _sdfShadowBias;
			// AdaptiveQuality is off below, so these are the constant quality for every pixel — including the
			// depth the shadow pass writes, which is where march error turns into acne.
			_renderer.MaxSteps = _maxSteps;
			_renderer.Epsilon = _epsilon;
			// Both LOD systems key off the GAME camera's distance to the prop, which has nothing to do with how
			// our camera frames it — leave them on and the thumbnail LODs out (or culls) based on where the
			// player happens to be standing.
			_renderer.DistanceSwitching = false;
			_renderer.AdaptiveQuality = false;
			// Analytic march, no baked field: a thumbnail is small and renders once, so the compute dispatch
			// would cost more than it saves — and skipping it means the very first frame is already correct
			// rather than blank while a bake lands.
			_renderer.UseFieldCache = false;
			_renderer.FieldCacheOnly = false;
			// These two ride the analytic path fine and BOTH change the look materially — displacement bends the
			// silhouette, transmission adds the back-scatter glow. Off by default here was the single biggest
			// reason a thumbnail stopped resembling the prop it depicts.
			_renderer.Displace = _displace;
			_renderer.Transmission = _transmission;

			_host.Enabled = true;
		}
	}

	static PrefabFile LoadDefaultRig()
	{
		try { return ResourceLibrary.Get<PrefabFile>( SdfStageRig.DefaultPrefabPath ); }
		catch { return null; }
	}

	static Texture LoadTexture( string path )
	{
		// Not fatal if it's missing — we just lose the image-based fill and lean on the analytic lights.
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		try { return Texture.Load( path ); }
		catch { return null; }
	}

	/// <summary>
	/// Mirror the rig prefab's lights into our private world. The prefab is read, never instantiated into the
	/// live scene — a cloned Light component would create its scene light in the SCENE's world and light the
	/// whole map. So the components are pure data here and we build the raw equivalents ourselves.
	/// </summary>
	void BuildLighting( PrefabFile rig )
	{
		var root = rig is not null ? SceneUtility.GetPrefabScene( rig ) : null;

		if ( !root.IsValid() )
		{
			BuildFallbackLighting();
			return;
		}

		if ( root.GetAllComponents<SdfStageRig>().FirstOrDefault() is { } settings )
		{
			_ambient = settings.AmbientColor;
			_margin = settings.Margin;
			_referenceRadius = MathF.Max( settings.ReferenceRadius, 0.001f );

			_surfaceMaterial = settings.SurfaceMaterial;
			_displace = settings.Displace;
			_transmission = settings.Transmission;
			_sdfShadows = settings.SdfShadows;
			_sdfShadowBias = settings.SdfShadowBias;
			_shadowDistance = settings.ShadowDistance;
			_maxSteps = settings.MaxSteps;
			_epsilon = settings.Epsilon;
		}

		// A SkyBox2D in the rig is the preferred source of sky + image-based lighting, because then the prefab
		// view and the thumbnail are lit by the same thing. Mirroring it means both halves of what the component
		// builds: the sky itself, and the cubemap that actually supplies the indirect term.
		if ( root.GetAllComponents<SkyBox2D>().FirstOrDefault() is { SkyMaterial: not null } sky )
		{
			_sky = new SceneSkyBox( World, sky.SkyMaterial ) { SkyTint = sky.Tint };

			if ( sky.SkyIndirectLighting && sky.SkyTexture is not null )
			{
				_env = new SceneCubemap( World, sky.SkyTexture, BBox.FromPositionAndSize( Vector3.Zero, 32768f ) )
				{
					TintColor = sky.Tint,
					Priority = -5, // lowest priority, same as the component's own global probe
				};
			}
		}
		else if ( root.GetAllComponents<SdfStageRig>().FirstOrDefault() is { } fallbackSettings )
		{
			ApplyEnvmap( LoadTexture( fallbackSettings.EnvmapTexture ) );
		}

		// Camera pose + FOV. Only the ROTATION is used — the distance is fitted to each subject's bounds, so
		// the authored camera position is just there to aim it in the prefab view.
		if ( root.GetAllComponents<CameraComponent>().FirstOrDefault() is { } cam )
		{
			_pose = cam.WorldRotation.Angles();
			_fov = cam.FieldOfView;
		}

		var any = false;

		foreach ( var l in root.GetAllComponents<DirectionalLight>() )
		{
			// No ContactShadows here: the DirectionalLight component sets it on its scene light, but that
			// property doesn't exist on SceneDirectionalLight in this engine build. Screen-space contact
			// shadows are therefore the one shadow setting the rig can't carry across.
			var so = new SceneDirectionalLight( World, l.WorldRotation, l.LightColor )
			{
				ShadowCascadeCount = l.ShadowCascadeCount,
				ShadowCascadeSplitRatio = l.ShadowCascadeSplitRatio,
			};

			ApplyCommon( so, l );
			_lights.Add( so );
			_directionals.Add( new PlacedDirectional { Light = so, Rotation = l.WorldRotation } );
			any = true;
		}

		foreach ( var l in root.GetAllComponents<PointLight>() )
		{
			var so = new ScenePointLight( World, l.WorldPosition, l.Radius, l.LightColor );
			ApplyCommon( so, l );
			_lights.Add( so );
			_placed.Add( new PlacedLight { Light = so, Position = l.WorldPosition, Rotation = l.WorldRotation, Radius = l.Radius } );
			any = true;
		}

		foreach ( var l in root.GetAllComponents<SpotLight>() )
		{
			var so = new SceneSpotLight( World, l.WorldPosition, l.LightColor )
			{
				Rotation = l.WorldRotation,
				Radius = l.Radius,
				ConeInner = l.ConeInner,
				ConeOuter = l.ConeOuter,
			};

			ApplyCommon( so, l );
			_lights.Add( so );
			_placed.Add( new PlacedLight { Light = so, Position = l.WorldPosition, Rotation = l.WorldRotation, Radius = l.Radius } );
			any = true;
		}

		// An AmbientLight component, if present, wins over the rig's AmbientColor — it's the more obvious dial
		// to reach for once you're already arranging lights.
		if ( root.GetAllComponents<AmbientLight>().FirstOrDefault() is { } amb )
			_ambient = amb.Color;

		World.AmbientLightColor = _ambient;

		// A rig with no lights at all would render pure black, which reads as "the system is broken" rather
		// than "you deleted the lights" — fall back rather than ship a black thumbnail.
		if ( !any )
			BuildFallbackLighting();

		_post.ReadFrom( root );
		BuildPostPass();
	}

	/// <summary>
	/// Override the rig's outline for this stage only. Null keeps whatever the rig authored. Safe to call after
	/// construction — <see cref="SdfStagePost.Render"/> reads these live, and the pass object is created here if
	/// the rig hadn't asked for one.
	/// </summary>
	public void ApplyOutlineOverride( Color? colour, float? width )
	{
		if ( colour is { } c )
			_post.OutlineColor = c;

		if ( width is { } w )
			_post.OutlineWidth = w;

		BuildPostPass();
	}

	/// <summary>
	/// The post-process chain, as an overlay object in the private world. OverlayWithoutDepth draws after the
	/// scene's own post block, which is where the effect components' blits would have landed. Idempotent — the
	/// outline override calls it again in case the rig alone hadn't asked for a pass.
	/// </summary>
	void BuildPostPass()
	{
		if ( _postObject.IsValid() || !_post.Any )
			return;

		// Main thread here (stage construction, or an override applied from a panel tick); RenderSceneObject is
		// NOT, and that's the only other place materials could get loaded.
		SdfStagePost.EnsureMaterials();

		_postObject = new SdfStagePostObject( World, _post );

		// SceneObject.RenderLayer's setter early-outs on a cached value the Default branch clears natively but
		// never writes back, so assign a different layer first to move the cache off Default. Same dance as
		// SdfRaymarchRenderer.ApplyViewLayer.
		_postObject.RenderLayer = SceneRenderLayer.OverlayWithDepth;
		_postObject.RenderLayer = SceneRenderLayer.OverlayWithoutDepth;
		_postObject.Flags.WantsFrameBufferCopy = true;
	}

	/// <summary>
	/// Carry across the shadow settings the Light component would have pushed onto its scene light. Mirroring
	/// only ShadowsEnabled silently threw these away — including the authored shadow bias, which is exactly
	/// the knob you reach for when shadows come out acned.
	/// </summary>
	static void ApplyCommon( SceneLight so, Light source )
	{
		so.ShadowsEnabled = source.Shadows;
		so.ShadowBias = source.ShadowBias;
		so.ShadowHardness = source.ShadowHardness;
	}

	/// <summary>Built-in three-point rig, used when the prefab is missing or empty. Kept deliberately close to
	/// what the shipped prefab authors, so a missing asset changes the look as little as possible.</summary>
	void BuildFallbackLighting()
	{
		ApplyEnvmap( LoadTexture( "textures/cubemaps/default2.vtex" ) );

		World.AmbientLightColor = _ambient;

		var sun = new SceneDirectionalLight( World, Rotation.From( 35, 150, 0 ), Color.White * 2.2f )
		{
			ShadowsEnabled = false, // one object on transparency — a cast shadow has nothing to land on
		};
		_lights.Add( sun );
		_directionals.Add( new PlacedDirectional { Light = sun, Rotation = Rotation.From( 35, 150, 0 ) } );

		// Key is front-above-LEFT of the camera, so fill goes front-right (into its shadow) and the rim sits
		// back-left to pick out the silhouette. Keep these in step with thumbnail_stage.prefab.
		AddFallbackPoint( new Vector3( 60, 55, 20 ), 220f, Color.White * 2.5f );
		AddFallbackPoint( new Vector3( -65, -50, 55 ), 260f, new Color( 1.4f, 1.5f, 1.8f ) );
	}

	void AddFallbackPoint( Vector3 position, float radius, Color colour )
	{
		var so = new ScenePointLight( World, position, radius, colour ) { ShadowsEnabled = false };
		_lights.Add( so );
		_placed.Add( new PlacedLight { Light = so, Position = position, Rotation = Rotation.Identity, Radius = radius } );
	}

	void ApplyEnvmap( Texture cube )
	{
		// A bare SceneWorld has no skybox and no envmap, so a PBR material has nothing to reflect and renders
		// far darker than the same prop does in game. This is the cheap fix, and it's what the engine's own
		// model-preview widgets do.
		if ( cube is null || _env is not null )
			return;

		_env = new SceneCubemap( World, cube, BBox.FromPositionAndSize( Vector3.Zero, 32768f ) );
	}

	/// <summary>
	/// Stage these brushes. Deep-copies them, so the staged prop can't be mutated mid-pack by whoever owns the
	/// live sculpt. Cheap to call every frame: it hashes the content and no-ops when nothing changed.
	/// </summary>
	/// <returns>True when the staged prop actually changed — the caller's cue to re-render.</returns>
	public bool SetBrushes( List<SdfBrush> brushes )
	{
		if ( !IsValid )
			return false;

		if ( brushes is not { Count: > 0 } )
			return Clear();

		// Portraits are of the AUTHORED sculpt only — never the damage craters a prop accumulates from being
		// shot. Damage brushes are appended after the authored ones (SdfSculpture.AuthoredBrushCount leans on
		// the same invariant), so the portrait is the prefix before the first one.
		//
		// Hashing the prefix rather than the whole list matters as much as the filtering: it means a gunshot
		// can't change the hash at all, so it doesn't merely render the same picture again — it costs no
		// re-render in the first place.
		var authored = AuthoredCount( brushes );
		if ( authored == 0 )
			return Clear();

		// Resolution/flip are fixed on the stage, so the brush list is the only content that can vary.
		var hash = SdfSculpture.ContentHashPrefix( brushes, authored, _sculpt.Resolution, false );
		if ( hash == _brushHash )
			return false;

		_brushHash = hash;

		var copy = new List<SdfBrush>( authored );
		for ( var i = 0; i < authored; i++ )
			copy.Add( brushes[i].Copy() );

		_sculpt.Brushes = copy;
		_renderer.ForceRefresh();

		Bounds = Sdf.TryGetBounds( copy, out var bb )
			? bb
			: BBox.FromPositionAndSize( Vector3.Zero, 16f );

		return true;
	}

	/// <summary>Number of authored brushes — everything before the first damage crater.</summary>
	static int AuthoredCount( List<SdfBrush> brushes )
	{
		for ( var i = 0; i < brushes.Count; i++ )
		{
			if ( brushes[i].Damage )
				return i;
		}

		return brushes.Count;
	}

	/// <summary>Empty the stage. Returns whether anything actually changed.</summary>
	bool Clear()
	{
		if ( _brushHash == 0 )
			return false;

		_brushHash = 0;
		_sculpt.Brushes = new List<SdfBrush>();
		_renderer.ForceRefresh();
		return true;
	}

	/// <summary>
	/// Point a camera at the staged prop from <paramref name="pose"/>, pulled back far enough to fit
	/// <see cref="Bounds"/> in frame, and swing the lighting rig round to match so any angle stays lit.
	/// </summary>
	/// <param name="camera">The camera to place — typically a ScenePanel's.</param>
	/// <param name="pose">Camera orientation. Null uses the rig prefab's camera rotation.</param>
	/// <param name="fov">Horizontal field of view (SceneCamera's FOV axis). Null uses the rig's camera.</param>
	/// <param name="margin">Padding around the bounding sphere. Null uses the rig's Margin.</param>
	public void Frame( SceneCamera camera, Angles? pose = null, float? fov = null, float? margin = null )
	{
		if ( !IsValid || camera is null )
			return;

		var centre = Bounds.Center;
		// Bounding-SPHERE radius. Still what the lights scale against — that's about the prop's physical size,
		// which shouldn't change with the camera angle — but no longer what the camera fits to.
		var subjectRadius = MathF.Max( Bounds.Size.Length * 0.5f, 1f );

		var f = MathF.Max( fov ?? _fov, 1f );
		var rot = (pose ?? _pose).ToRotation();
		var m = MathF.Max( margin ?? _margin, 0.01f );

		// Fit the subject as a SPHERE whose radius is the largest half-extent of the bounds.
		//
		// Two wrong answers came before this one, both too conservative in the same direction. Fitting the
		// bounding sphere by its half-DIAGONAL treats a ball of radius r as radius r*sqrt(3) and fills barely
		// half the frame. Fitting the bounding BOX corner-by-corner is exactly right for a box, but a clay prop
		// doesn't occupy its corners — and it's the corners that set the distance, so a round head still lost
		// about a third of the frame to background.
		//
		// The largest half-extent is the honest radius for a rounded form that fills its box, which is what
		// these props are. A genuinely cubic subject viewed corner-on could graze the frame edge; that's what
		// Margin is for, and 1.2 leaves plenty.
		//
		// sin, not tan: a sphere of radius r is tangent to the frustum at distance r / sin(halfFov). Using tan
		// puts it slightly too close and clips the silhouette.
		var extent = MathF.Max( Bounds.Size.x, MathF.Max( Bounds.Size.y, Bounds.Size.z ) ) * 0.5f;
		var dist = MathF.Max( extent, 1f ) * m / MathF.Sin( f * MathF.PI / 360f );

		// Depth range of the actual box along the view, for the near/far planes.
		var nearest = float.MaxValue;
		var furthest = float.MinValue;

		for ( var i = 0; i < 8; i++ )
		{
			var corner = new Vector3(
				(i & 1) != 0 ? Bounds.Maxs.x : Bounds.Mins.x,
				(i & 2) != 0 ? Bounds.Maxs.y : Bounds.Mins.y,
				(i & 4) != 0 ? Bounds.Maxs.z : Bounds.Mins.z ) - centre;

			var forward = Vector3.Dot( corner, rot.Forward );
			nearest = MathF.Min( nearest, forward );
			furthest = MathF.Max( furthest, forward );
		}

		camera.World = World;
		camera.FieldOfView = f;
		camera.Position = centre - rot.Forward * dist;
		camera.Rotation = rot;
		// Bracket the box itself, with a subject-sized pad so displacement pushing the silhouette out past the
		// SDF bounds can't get clipped by the near/far planes.
		camera.ZNear = MathF.Max( 0.5f, dist + nearest - subjectRadius );
		camera.ZFar = dist + furthest + subjectRadius * 2f;
		camera.BackgroundColor = Color.Transparent;
		camera.AmbientLightColor = _ambient;

		// Re-place the local lights for this subject's size. The rig is authored around a ReferenceRadius-sized
		// prop at the origin, so scaling by the real radius means a tennis ball and a wardrobe get the same
		// LIGHTING rather than the same absolute light positions (which would leave one blown out and the other
		// unlit). Directional lights need none of this — they're direction-only.
		//
		// SWING the whole rig by the difference between the rig's authored camera pose and the requested one,
		// so key/fill/rim keep their authored relationship to the CAMERA rather than to the subject's axes — a
		// prop portrayed from behind (a pose override) is lit like a portrait, not like the dark side of the
		// moon. Identity when no pose override is in play, so the stock thumbnails render exactly as before.
		// Derived fresh from the AUTHORED values every call — swinging the live lights in place would compound.
		var scale = subjectRadius / _referenceRadius;
		var swing = rot * _pose.ToRotation().Inverse;

		foreach ( var p in _placed )
		{
			p.Light.Position = centre + swing * p.Position * scale;
			p.Light.Rotation = swing * p.Rotation;
			p.Light.Radius = p.Radius * scale;
		}

		// Pack the shadow cascades around the subject. They're fitted from the camera outward, and the default
		// range is sized for a whole map — which on a stage this small leaves the prop covering a handful of
		// shadow-map texels, blocky enough to read as a bias problem. Reaching just past the subject's far side
		// puts the entire map on the thing we're photographing.
		var shadowRange = _shadowDistance > 0f ? _shadowDistance : dist + subjectRadius * 2f;

		foreach ( var d in _directionals )
		{
			d.Light.Rotation = swing * d.Rotation;
			d.Light.SetCascadeDistanceScale( shadowRange );
		}
	}

	public void Dispose()
	{
		if ( _disposed )
			return;

		_disposed = true;

		// Order matters: the renderer's scene object lives in OUR world, and GameObject.Destroy is deferred, so
		// tearing the world down first would leave a live primitive pointing at freed native memory. Release the
		// scene object by hand, flush the world's deferred deletes, then drop the world and the host.
		if ( _renderer.IsValid() )
			_renderer.ReleaseSceneObject();

		foreach ( var l in _lights )
			l?.Delete();

		_lights.Clear();
		_placed.Clear();
		_directionals.Clear();

		_postObject?.Delete();
		_postObject = null;

		_sky?.Delete();
		_sky = null;

		_env?.Delete();

		if ( World.IsValid() )
		{
			World.DeletePendingObjects();
			World.Delete();
		}

		World = null;
		_env = null;

		if ( _host.IsValid() )
			_host.Destroy();

		_host = null;
		_sculpt = null;
		_renderer = null;
	}
}
