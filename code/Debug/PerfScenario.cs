using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Diagnostics;

namespace Mimiclay;

/// <summary>
/// Benchmark scenario driver for a map played directly from the editor (DevPropHunt → creative). Builds a
/// repeatable crowd of SDF props on the map floor, swaps the whole crowd between the raymarched and meshed
/// paths, parks or walks the local pawn through fixed viewpoints, and reports what the scene is paying for.
/// Pairs with <see cref="PerfBenchSystem"/> (mimi_bench samples frame times; mimi_bench_set flips features).
///
///   mimi_perf_spawn [count] [seed] [filter] [area]   scatter props from Assets/Prefabs/Props on the floor (tag perfprop)
///   mimi_perf_clear                            remove them
///   mimi_perf_mode sdf|mesh                    raymarch every perf prop, or hide the raymarch and show the mesh
///   mimi_perf_view overview|center|close|walk  teleport the local pawn to a fixed view (walk = start the loop)
///   mimi_perf_walk 0|1                         drive the local pawn (hunter or prop) around a waypoint loop
///   mimi_perf_role hunter|prop                 swap the local player's pawn (creative only)
///   mimi_perf_stats                            field VRAM, ready/pending counts, caster split, mesh builds
///   mimi_perf_set [Property] [value]        set any SdfRaymarchRenderer property on every perf prop (bisecting)
///   mimi_perf_field [res]                      pin Authored FieldResolution on every perf prop; 0 = World density
///   mimi_perf_sample [seconds] [label]         frame-time + CPU-system sample (prints [perf-sample] lines)
/// </summary>
public sealed class PerfScenarioSystem : GameObjectSystem
{
	public PerfScenarioSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 1001, Tick, "PerfScenario" );
	}

	void Tick()
	{
		if ( Scene.IsEditor )
			return;
		PerfScenario.Tick( Scene );
	}
}

public static class PerfScenario
{
	public const string Tag = "perfprop";

	static BBox _floor;
	static bool _floorValid;
	static BBox _crowd;      // AABB of the spawned prop positions: the views + walk route frame THIS, not the whole floor
	static bool _crowdValid;
	static float _spawnAt = -1f;
	static int _spawned;
	static bool _settled = true;

	// ── Spawning ────────────────────────────────────────────────────────────────────────────────────────────

	static bool FindFloor( Scene scene )
	{
		var floors = scene.GetAllObjects( true ).Where( go => go.Name.StartsWith( "Floor_" ) ).ToList();
		if ( floors.Count == 0 )
			floors = scene.GetAllObjects( true ).Where( go => go.Tags.Has( "world" ) ).ToList();
		if ( floors.Count == 0 )
			return false;

		bool first = true;
		foreach ( var f in floors )
		{
			var b = f.GetBounds();
			if ( b.Size.Length < 1f ) continue;
			_floor = first ? b : _floor.AddBBox( b );
			first = false;
		}
		_floorValid = !first;
		return _floorValid;
	}

	static List<PrefabFile> PropPrefabs( string filter )
	{
		var all = ResourceLibrary.GetAll<PrefabFile>()
			.Where( p => p.ResourcePath.StartsWith( "prefabs/props/", StringComparison.OrdinalIgnoreCase ) )
			.OrderBy( p => p.ResourcePath )
			.ToList();
		if ( filter == "-" || filter == "*" ) filter = ""; // console placeholder so a later positional arg (area) can be given
		if ( !string.IsNullOrEmpty( filter ) )
			all = all.Where( p => p.ResourcePath.Contains( filter, StringComparison.OrdinalIgnoreCase ) ).ToList();
		return all;
	}

	[ConCmd( "mimi_perf_spawn" )]
	public static void Spawn( int count, int seed = 1, string filter = "", float area = 1f )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		Clear();

		if ( !FindFloor( scene ) )
		{
			Log.Warning( "[perf] no floor objects found" );
			return;
		}

		var prefabs = PropPrefabs( filter );
		if ( prefabs.Count == 0 )
		{
			Log.Warning( "[perf] no prop prefabs under prefabs/props/" );
			return;
		}

		var rng = new Random( seed );
		// area < 1 packs the crowd into a centred fraction of the floor (the shelf-full-of-props case).
		area = Math.Clamp( area, 0.05f, 1f );
		var mins = _floor.Center - _floor.Extents * new Vector3( area, area, 1f );
		var maxs = _floor.Center + _floor.Extents * new Vector3( area, area, 1f );
		float w = maxs.x - mins.x, h = maxs.y - mins.y;
		// Grid cells sized so count fits with some slack; jitter inside each cell; trace down for ground.
		float cell = MathF.Sqrt( w * h / MathF.Max( count * 1.4f, 1f ) );
		int nx = Math.Max( 1, (int)(w / cell) ), ny = Math.Max( 1, (int)(h / cell) );
		var cells = new List<(int, int)>();
		for ( int i = 0; i < nx; i++ ) for ( int j = 0; j < ny; j++ ) cells.Add( (i, j) );
		for ( int i = cells.Count - 1; i > 0; i-- ) { int k = rng.Next( i + 1 ); (cells[i], cells[k]) = (cells[k], cells[i]); }

		int placed = 0, tries = 0, pi = 0;
		_crowdValid = false;
		foreach ( var (ci, cj) in cells )
		{
			if ( placed >= count ) break;
			tries++;
			float x = mins.x + (ci + 0.2f + 0.6f * (float)rng.NextDouble()) * cell;
			float y = mins.y + (cj + 0.2f + 0.6f * (float)rng.NextDouble()) * cell;
			var start = new Vector3( x, y, maxs.z + 400f );
			var end = new Vector3( x, y, mins.z - 50f );
			var tr = scene.Trace.Ray( start, end ).WithoutTags( Tag, "player", "clay" ).Run();
			if ( !tr.Hit || tr.Normal.z < 0.7f ) continue;

			var prefab = prefabs[pi++ % prefabs.Count];
			var ps = SceneUtility.GetPrefabScene( prefab );
			if ( ps is null ) continue;
			var tx = new Transform( tr.HitPosition + Vector3.Up * 0.5f, Rotation.FromYaw( (float)rng.NextDouble() * 360f ), 1f );
			var go = ps.Clone( new CloneConfig( tx, startEnabled: true, name: $"perf_{placed}_{prefab.ResourceName}" ) );
			if ( !go.IsValid() ) continue;
			go.Tags.Add( Tag );
			foreach ( var c in go.Children ) c.Tags.Add( Tag );
			// Crowd bounds from the positions placed THIS call: Destroy() is deferred, so enumerating by tag here
			// would still see the previous crowd.
			_crowd = _crowdValid ? _crowd.AddPoint( tx.Position ) : BBox.FromPositionAndSize( tx.Position, 1f );
			_crowdValid = true;
			placed++;
		}

		_spawned = placed;
		_spawnAt = RealTime.Now;
		if ( _crowdValid ) Log.Info( $"[perf] crowd bounds {_crowd.Mins:0}..{_crowd.Maxs:0}" );
		_settled = false;
		Log.Info( $"[perf] spawned {placed}/{count} props from {prefabs.Count} prefabs over floor {_floor.Mins:0}..{_floor.Maxs:0} (cell {cell:0}, {tries} cells tried)" );
	}

	[ConCmd( "mimi_perf_clear" )]
	public static void Clear()
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;
		int n = 0;
		foreach ( var go in scene.GetAllObjects( false ).Where( g => g.Tags.Has( Tag ) && g.Parent == scene ).ToList() )
		{
			foreach ( var r in go.Components.GetAll<SdfRaymarchRenderer>( FindMode.EverythingInSelfAndDescendants ) )
				r.ReleaseSceneObject();
			go.Destroy();
			n++;
		}
		_spawnAt = -1f;
		if ( n > 0 ) Log.Info( $"[perf] cleared {n} props" );
	}

	static IEnumerable<SdfRaymarchRenderer> PerfRenderers( Scene scene ) =>
		scene.GetAllObjects( false ).Where( g => g.Tags.Has( Tag ) )
			.SelectMany( g => g.Components.GetAll<SdfRaymarchRenderer>( FindMode.EverythingInSelf ) );

	// ── Render path ─────────────────────────────────────────────────────────────────────────────────────────

	[ConCmd( "mimi_perf_mode" )]
	public static void Mode( string mode )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;
		int n = 0;
		switch ( mode )
		{
			case "sdf":
				foreach ( var r in PerfRenderers( scene ) ) { r.Enabled = true; n++; }
				break;
			case "mesh":
				// Disabling the raymarch renderer hands the sibling mesh back as a normal visible caster (OnDisabled).
				foreach ( var r in PerfRenderers( scene ) ) { r.Enabled = false; n++; }
				break;
			default:
				Log.Warning( "[perf] mode sdf|mesh" );
				return;
		}
		Log.Info( $"[perf] mode={mode} on {n} renderers" );
	}

	[ConCmd( "mimi_perf_set" )]
	public static void SetProp( string property, string value )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;
		var type = TypeLibrary.GetType<SdfRaymarchRenderer>();
		var prop = type?.GetProperty( property );
		if ( prop is null ) { Log.Warning( $"[perf] SdfRaymarchRenderer has no property '{property}'" ); return; }
		object v;
		try
		{
			v = prop.PropertyType == typeof( bool ) ? (object)(value == "1" || value.Equals( "true", StringComparison.OrdinalIgnoreCase ))
				: prop.PropertyType == typeof( int ) ? int.Parse( value )
				: prop.PropertyType == typeof( float ) ? float.Parse( value )
				: prop.PropertyType.IsEnum ? Enum.Parse( prop.PropertyType, value, true )
				: value;
		}
		catch ( Exception e ) { Log.Warning( $"[perf] can't parse '{value}' as {prop.PropertyType.Name}: {e.Message}" ); return; }
		int n = 0;
		foreach ( var r in PerfRenderers( scene ) ) { prop.SetValue( r, v ); n++; }
		Log.Info( $"[perf] {property}={v} on {n} perf renderers" );
	}

	[ConCmd( "mimi_perf_field" )]
	public static void Field( int res )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;
		int n = 0;
		// 0 = back to the World density tier; anything else pins the legacy authored resolution.
		foreach ( var r in PerfRenderers( scene ) )
		{
			r.FieldDensity = res > 0 ? SdfFieldDensity.Authored : SdfFieldDensity.World;
			if ( res > 0 ) r.FieldResolution = res;
			n++;
		}
		Log.Info( $"[perf] {(res > 0 ? $"FieldResolution={res} (Authored)" : "FieldDensity=World")} on {n} renderers" );
	}

	// ── Stats ───────────────────────────────────────────────────────────────────────────────────────────────

	[ConCmd( "mimi_perf_stats" )]
	public static void Stats()
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		var all = scene.GetAllComponents<SdfRaymarchRenderer>().ToList();
		var perf = PerfRenderers( scene ).ToList();
		long bytes = 0, perfBytes = 0; int ready = 0, pending = 0, marched = 0, soOn = 0, meshOn = 0, sparse = 0;
		var states = new int[5];
		var resHist = new Dictionary<int, int>();
		foreach ( var r in all )
		{
			long b = r.FieldVramBytes;
			bytes += b;
			if ( r.DebugFieldReady ) ready++; else if ( r.FieldPending ) pending++;
			if ( r.DebugMarchedShadows ) marched++;
			if ( r.DrawnSceneObjectLive ) soOn++;
			if ( r.DebugFieldSparse ) sparse++;
			states[Math.Clamp( r.DebugSwitchState, 0, 4 )]++;
			// Longest surface-grid axis actually built, bucketed to 16s (the density tiers make this continuous).
			var dims = r.DebugFieldDims;
			int longest = (int)MathF.Max( dims.x, MathF.Max( dims.y, dims.z ) ) / 16 * 16;
			resHist[longest] = resHist.GetValueOrDefault( longest ) + 1;
			var mr = r.GameObject.Components.Get<ModelRenderer>( true );
			if ( mr.IsValid() && mr.Enabled ) meshOn++;
		}
		foreach ( var r in perf ) perfBytes += r.FieldVramBytes;
		var fields = all.Select( r => r.DebugField ).Where( f => f is not null ).Distinct().ToList();
		long localBytes = all.Sum( r => r.LocalResourceBytes );
		long uniqueFieldBytes = fields.Sum( f => f.EstimatedBytes );

		int brushes = scene.GetAllComponents<SdfSculpture>().Sum( s => s.Brushes?.Count ?? 0 );
		Log.Info( $"[perf-stats] renderers={all.Count} (perf={perf.Count}) brushes={brushes} fieldReady={ready} pending={pending} marchedShadow={marched} soOn={soOn} meshOn={meshOn} sparse={sparse} bands[sdf={states[0]} lod0={states[1]} lod1={states[2]} lod2={states[3]} cull={states[4]}]" );
		Log.Info( $"[perf-stats] fieldPayload(sum over renderers, shared fields counted per owner) total={bytes / 1048576.0:0.0}MiB perf={perfBytes / 1048576.0:0.0}MiB avg/prop={(all.Count > 0 ? bytes / 1048576.0 / all.Count : 0):0.00}MiB res={string.Join( ",", resHist.OrderBy( k => k.Key ).Select( k => $"{k.Key}x{k.Value}" ) )} meshBuildsInFlight={SdfSculpture.BuildsInFlight} colliderBuilds={SdfCollider.BuildCount}" );
		Log.Info( $"[perf-stats] {SdfFieldCache.DebugLine()}" );
		Log.Info( $"[perf-stats] scene GPU payload={((localBytes + uniqueFieldBytes) / 1048576.0):0.0}MiB uniqueFields={fields.Count}/{uniqueFieldBytes / 1048576.0:0.0}MiB rendererLocal={localBytes / 1048576.0:0.0}MiB (rendererText={all.Sum( r => r.TextAtlasBytes ) / 1048576.0:0.0}MiB fieldText={fields.Sum( f => f.TextAtlasBytes ) / 1048576.0:0.0}MiB); excludes mesh buffers, mesh-bake scratch, driver overhead and other scenes" );
		Log.Info( $"[perf-stats] convars: field_cache={SdfRaymarchRenderer.FieldCacheEnabled} sparse={SdfRaymarchRenderer.SparseFieldEnabled} atlas8={SdfFieldGpu.Atlas8BitEnabled} screen={Screen.Size}" );
		Log.Info( $"[perf-stats] density: voxel={SdfRaymarchRenderer.WorldVoxelSize} detail={SdfRaymarchRenderer.DetailVoxelSize} res=[{SdfRaymarchRenderer.DensityMinResolution},{SdfRaymarchRenderer.FieldResolutionCap}] detailMax={SdfRaymarchRenderer.DetailMaxResolution} denseMax={SdfRaymarchRenderer.DenseMaxResolution} tiers={string.Join( ",", all.GroupBy( r => r.DebugFieldTier ).Select( g => $"{g.Key}x{g.Count()}" ) )}" );
	}

	// ── Load test: synchronous spawn cost + time/hitches until every field and mesh has landed ──────────────

	static bool _loadTest;
	static float _loadMaxFrame;
	static int _loadFrames;
	static float _loadStart;
	static bool _loadSawBuilds;

	/// <summary>Spawn a crowd and report what loading it cost: the synchronous spawn-call time (clone + collider
	/// builds in one frame), the seconds until every field is baked and every mesh build has drained, and the
	/// worst frame in that window. Cold numbers need a fresh play session (the mesh model cache persists).</summary>
	[ConCmd( "mimi_perf_loadtest" )]
	public static void LoadTest( int count, int seed = 1, float area = 0.3f )
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		Spawn( count, seed, "-", area );
		sw.Stop();
		_loadTest = true;
		_loadMaxFrame = 0f;
		_loadFrames = 0;
		_loadStart = RealTime.Now;
		_loadSawBuilds = false;
		Log.Info( $"[perf-load] spawn call (clone + colliders, one frame): {sw.Elapsed.TotalMilliseconds:0.0}ms for {_spawned} props" );
	}

	static void LoadTestTick( Scene scene )
	{
		_loadFrames++;
		_loadMaxFrame = MathF.Max( _loadMaxFrame, Time.Delta * 1000f );
		if ( SdfSculpture.BuildsInFlight > 0 ) _loadSawBuilds = true;

		bool pending = false;
		foreach ( var r in PerfRenderers( scene ) )
			if ( r.FieldPending ) { pending = true; break; }

		// Give the mesh builds a couple of frames to start queueing before declaring the drain complete.
		if ( !pending && SdfSculpture.BuildsInFlight == 0 && _loadFrames > 3 )
		{
			_loadTest = false;
			Log.Info( $"[perf-load] settled in {RealTime.Now - _loadStart:0.00}s / {_loadFrames} frames, worst frame {_loadMaxFrame:0.0}ms, mesh builds seen={_loadSawBuilds}, {SdfFieldCache.DebugLine()}" );
			Stats();
		}
	}

	// ── Per-frame: settle timing + sampling ──────────────────────────────────────────────────────────────────

	internal static void Tick( Scene scene )
	{
		if ( _spawnAt >= 0f && !_settled )
		{
			bool anyPending = false;
			foreach ( var r in PerfRenderers( scene ) )
				if ( r.FieldPending ) { anyPending = true; break; }
			if ( !anyPending && SdfSculpture.BuildsInFlight == 0 && RealTime.Now - _spawnAt > 0.5f )
			{
				_settled = true;
				Log.Info( $"[perf] settled {_spawned} props in {RealTime.Now - _spawnAt:0.00}s (fields ready, mesh builds drained)" );
				Stats();
			}
		}

		if ( _sampling )
			SampleTick();

		if ( _loadTest )
			LoadTestTick( scene );
	}

	// ── Views + walking ─────────────────────────────────────────────────────────────────────────────────────

	public static bool DrivingHunter { get; private set; }

	static readonly List<Vector3> _route = new();
	static int _routeIdx;
	const float WalkSpeed = 160f;

	static HunterController LocalHunter( Scene scene ) => scene.GetAllComponents<HunterController>().FirstOrDefault( h => h.Owned );
	static HiderController LocalProp( Scene scene ) => scene.GetAllComponents<HiderController>().FirstOrDefault( h => !h.IsProxy );

	static void BuildRoute()
	{
		_route.Clear();
		if ( !_floorValid ) return;
		var frame = _crowdValid ? _crowd : _floor;
		var c = frame.Center.WithZ( _floor.Maxs.z );
		var e = frame.Extents * 0.6f;
		_route.Add( new Vector3( c.x - e.x, c.y - e.y, c.z ) );
		_route.Add( new Vector3( c.x + e.x, c.y - e.y, c.z ) );
		_route.Add( new Vector3( c.x, c.y, c.z ) );
		_route.Add( new Vector3( c.x + e.x, c.y + e.y, c.z ) );
		_route.Add( new Vector3( c.x - e.x, c.y + e.y, c.z ) );
		_route.Add( new Vector3( c.x, c.y, c.z ) );
		_routeIdx = 0;
	}

	[ConCmd( "mimi_perf_walk" )]
	public static void Walk( int on )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;
		if ( !_floorValid ) FindFloor( scene );
		BuildRoute();
		DrivingHunter = on != 0;
		Log.Info( $"[perf] walk {(on != 0 ? "ON" : "OFF")} ({_route.Count} waypoints)" );
	}

	[ConCmd( "mimi_perf_view" )]
	public static void View( string name )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;
		if ( !_floorValid && !FindFloor( scene ) ) { Log.Warning( "[perf] no floor" ); return; }

		DrivingHunter = false;

		var frame = _crowdValid ? _crowd : _floor;
		var c = frame.Center.WithZ( _floor.Maxs.z );
		var e = frame.Extents;
		Vector3 pos; Angles ang;
		switch ( name )
		{
			case "overview":
				pos = new Vector3( c.x - e.x * 1.1f, c.y - e.y * 1.1f, _floor.Maxs.z + 2f );
				ang = new Angles( 6f, Vector3.VectorAngle( (c - pos).WithZ( 0 ) ).yaw, 0 );
				break;
			case "center":
				pos = new Vector3( c.x, c.y, _floor.Maxs.z + 2f );
				ang = new Angles( 10f, 0f, 0f );
				break;
			case "close":
			{
				// Stand 50u from the perf prop nearest the centre, looking at it.
				var target = scene.GetAllObjects( false ).Where( g => g.Tags.Has( Tag ) && g.Parent == scene )
					.OrderBy( g => g.WorldPosition.Distance( c ) ).FirstOrDefault();
				if ( !target.IsValid() ) { Log.Warning( "[perf] no perf props" ); return; }
				var t = target.WorldPosition;
				var dir = (t - c).WithZ( 0 ).Normal;
				if ( dir.Length < 0.1f ) dir = Vector3.Forward;
				pos = t - dir * 50f + Vector3.Up * 1f;
				ang = new Angles( 20f, Vector3.VectorAngle( dir ).yaw, 0 );
				break;
			}
			case "walk":
				Walk( 1 );
				return;
			default:
			{
				var parts = name.Split( ',' );
				if ( parts.Length < 5 ) { Log.Warning( "[perf] view overview|center|close|walk|x,y,z,pitch,yaw" ); return; }
				pos = new Vector3( float.Parse( parts[0] ), float.Parse( parts[1] ), float.Parse( parts[2] ) );
				ang = new Angles( float.Parse( parts[3] ), float.Parse( parts[4] ), 0 );
				break;
			}
		}

		var hunter = LocalHunter( scene );
		if ( hunter.IsValid() )
		{
			hunter.Teleport( pos, Rotation.FromYaw( ang.yaw ) );
			hunter.PerfSetEye( ang );
			Log.Info( $"[perf] hunter view '{name}' at {pos:0} {ang}" );
			return;
		}
		var prop = LocalProp( scene );
		if ( prop.IsValid() )
		{
			prop.WorldPosition = pos;
			prop.PerfSetEye( ang );
			Log.Info( $"[perf] prop view '{name}' at {pos:0} {ang}" );
			return;
		}
		Log.Warning( "[perf] no local pawn" );
	}

	/// <summary>Called by HunterController in place of input while <see cref="DrivingHunter"/>.</summary>
	internal static void DriveHunter( PlayerController c, GameObject pawn )
	{
		if ( _route.Count == 0 ) { c.WishVelocity = Vector3.Zero; return; }
		var p = pawn.WorldPosition;
		var target = _route[_routeIdx];
		var to = (target - p).WithZ( 0 );
		if ( to.Length < 40f ) { _routeIdx = (_routeIdx + 1) % _route.Count; return; }
		float wantYaw = Vector3.VectorAngle( to ).yaw;
		var a = c.EyeAngles;
		float dy = MathX.DeltaDegrees( a.yaw, wantYaw );
		a.yaw += Math.Clamp( dy, -120f * Time.Delta, 120f * Time.Delta );
		a.pitch = 10f + 8f * MathF.Sin( RealTime.Now * 0.7f ); // slow look-around so shadows/fields get exercised
		c.EyeAngles = a;
		c.WishVelocity = Rotation.FromYaw( a.yaw ) * Vector3.Forward * WalkSpeed;
	}

	/// <summary>Called by HiderController each movement tick while walking; returns a world-space wish velocity.</summary>
	internal static Vector3? DriveProp( GameObject pawn, float speed )
	{
		if ( !DrivingHunter || _route.Count == 0 ) return null;
		var p = pawn.WorldPosition;
		var target = _route[_routeIdx];
		var to = (target - p).WithZ( 0 );
		if ( to.Length < 40f ) { _routeIdx = (_routeIdx + 1) % _route.Count; return Vector3.Zero; }
		return to.Normal * speed;
	}

	[ConCmd( "mimi_perf_role" )]
	public static void Role( string role )
	{
		var scene = Game.ActiveScene;
		bool isHunter = LocalHunter( scene ).IsValid();
		if ( !((role == "prop" && isHunter) || (role == "hunter" && !isHunter)) ) { Log.Info( $"[perf] already {role}" ); return; }

		// Direct play of a map runs creative (no lobby manager): the swap lives on CreativeManager there.
		var creative = scene.GetAllComponents<CreativeManager>().FirstOrDefault();
		if ( creative.IsValid() )
		{
			creative.RequestSwap( 0f );
			Log.Info( $"[perf] creative swap: local role to {role}" );
			return;
		}
		var lm = LobbyManager.Current;
		if ( !lm.IsValid() ) { Log.Warning( "[perf] no creative or lobby manager" ); return; }
		lm.SwapRoleFor( Connection.Local, 0f );
		Log.Info( $"[perf] lobby swap: local role to {role}" );
	}

	// ── Sampling: frame-time percentiles + CPU system averages ──────────────────────────────────────────────

	static bool _sampling;
	static float _sampleEnd;
	static string _sampleLabel = "";
	static readonly List<float> _frames = new();
	static readonly List<float> _gpuFrames = new();

	[ConCmd( "mimi_perf_sample" )]
	public static void Sample( float seconds, string label )
	{
		_frames.Clear();
		_gpuFrames.Clear();
		SdfRaymarchRenderer.TakeUpdateMs();
		_sampleLabel = label;
		_sampleEnd = RealTime.Now + seconds;
		_sampling = true;
		Log.Info( $"[perf] sampling {seconds:0.#}s as '{label}'" );
	}

	static void SampleTick()
	{
		_frames.Add( Time.Delta * 1000f );
		_gpuFrames.Add( PerformanceStats.GpuFrametime );
		if ( RealTime.Now < _sampleEnd ) return;
		_sampling = false;

		var s = _frames.OrderBy( x => x ).ToList();
		float Pct( float p ) => s[Math.Clamp( (int)(p * (s.Count - 1)), 0, s.Count - 1 )];
		float avg = s.Average();
		var g = _gpuFrames.Where( x => x > 0f ).ToList();
		float gavg = g.Count > 0 ? g.Average() : 0f;

		double refreshMs = SdfRaymarchRenderer.TakeUpdateMs() / Math.Max( s.Count, 1 );
		var t = PerformanceStats.Timings.GetMain().Select( x => $"{x.Name}={x.AverageMs( Math.Min( 256, s.Count ) ):0.00}" );
		Log.Info( $"[perf-sample] {_sampleLabel}: n={s.Count} avg={avg:0.00}ms p50={Pct( 0.5f ):0.00} p95={Pct( 0.95f ):0.00} p99={Pct( 0.99f ):0.00} max={s[^1]:0.00} fps={1000f / avg:0.0} | gpuFrame avg={gavg:0.00}ms | SdfRaymarchRenderer.OnUpdate total={refreshMs:0.00}ms/frame" );
		Log.Info( $"[perf-sample] {_sampleLabel} cpu: {string.Join( " ", t )} | gc0={PerformanceStats.LastSecond.Gc0} gc1={PerformanceStats.LastSecond.Gc1} gc2={PerformanceStats.LastSecond.Gc2} alloc/s={PerformanceStats.LastSecond.ByteAlloc / 1048576.0:0.0}MB" );
		// Caster state, so a sample self-describes which shadow path it measured (the shadow band has hysteresis).
		int marched = 0, soOn = 0, meshOn = 0, total = 0;
		foreach ( var r in Game.ActiveScene.GetAllComponents<SdfRaymarchRenderer>() )
		{
			total++;
			if ( r.DebugMarchedShadows ) marched++;
			if ( r.DrawnSceneObjectLive ) soOn++;
			var mr = r.GameObject.Components.Get<ModelRenderer>( true );
			if ( mr.IsValid() && mr.Enabled ) meshOn++;
		}
		var f = FrameStats.Current;
		Log.Info( $"[perf-sample] {_sampleLabel} casters: renderers={total} marchedShadow={marched} soOn={soOn} meshOn={meshOn}" );
		Log.Info( $"[perf-sample] {_sampleLabel} frame: draws={f.DrawCalls} objs={f.ObjectsRendered}/{f.ObjectsPreCull} tris={f.TrianglesRendered} shadowMaps={f.ShadowMaps} shadowedLights={f.ShadowedLightsInView} views={f.SceneViewsRendered} matChanges={f.MaterialChanges} shadowMat={f.ShadowMaterialChanges}/{f.ShadowMaterialChangesAlphaTested} unbatchable={f.UnbatchableMaterialDraws}" );
	}
}
