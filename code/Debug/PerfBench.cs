using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Console-driven frame-time benchmark, for A/B-ing render features on a stress scene (perftest).
/// <c>mimi_bench &lt;seconds&gt; &lt;label&gt;</c> samples every frame's delta and logs count / avg /
/// p50 / p95 / p99 / max plus average FPS under the label, so runs can be compared in the console.
/// <c>mimi_bench_stats</c> counts what the scene is actually paying for (renderers, brushes, boils).
/// <c>mimi_bench_set &lt;feature&gt; &lt;0|1&gt;</c> flips a per-component feature across the whole
/// scene for bisecting — complements the global ConVars (mimiclay_sdf_field_cache etc), which cover
/// the rest. Debug-only tooling: statics survive Stop→Play (see SessionResetSystem notes) but every
/// run re-arms explicitly, so stale state can't corrupt a sample.
/// </summary>
public sealed class PerfBenchSystem : GameObjectSystem
{
	public PerfBenchSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 1000, Tick, "PerfBench" );
	}

	static bool _running;
	static float _endAt;
	static string _label = "";
	static readonly List<float> _samples = new();

	[ConCmd( "mimi_bench" )]
	public static void Run( float seconds, string label )
	{
		_samples.Clear();
		_label = label;
		_endAt = Time.Now + seconds;
		_running = true;
		Log.Info( $"[bench] sampling {seconds:0.#}s as '{label}'..." );
	}

	void Tick()
	{
		if ( !_running || Scene.IsEditor )
			return;

		_samples.Add( Time.Delta * 1000f );

		if ( Time.Now < _endAt )
			return;

		_running = false;
		Report();
	}

	static void Report()
	{
		if ( _samples.Count == 0 ) { Log.Warning( "[bench] no samples (editor not playing?)" ); return; }

		var s = _samples.OrderBy( x => x ).ToList();
		float Pct( float p ) => s[Math.Clamp( (int)(p * (s.Count - 1)), 0, s.Count - 1 )];
		var avg = s.Average();

		Log.Info( $"[bench] {_label}: n={s.Count} avg={avg:0.00}ms p50={Pct( 0.5f ):0.00} p95={Pct( 0.95f ):0.00} p99={Pct( 0.99f ):0.00} max={s[^1]:0.00} | {1000f / avg:0.0} fps" );
	}

	[ConCmd( "mimi_bench_stats" )]
	public static void Stats()
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		var renderers = scene.GetAllComponents<SdfRaymarchRenderer>().ToList();
		var sculptures = scene.GetAllComponents<SdfSculpture>().ToList();
		var boils = scene.GetAllComponents<ClayBoil>().ToList();
		var colliders = scene.GetAllComponents<SdfCollider>().ToList();

		int brushes = sculptures.Sum( x => x.Brushes?.Count ?? 0 );
		int boiling = boils.Count( b => b.Activation == BoilActivation.Always );
		int sdfShadows = renderers.Count( r => r.SdfShadows );

		Log.Info( $"[bench-stats] renderers={renderers.Count} (sdfShadows={sdfShadows}) sculptures={sculptures.Count} brushes={brushes} boils={boils.Count} (always={boiling}) colliders={colliders.Count}" );
		Log.Info( $"[bench-stats] convars: field_cache={SdfRaymarchRenderer.FieldCacheEnabled} sparse={SdfRaymarchRenderer.SparseFieldEnabled} atlas8={SdfFieldGpu.Atlas8BitEnabled}" );
	}

	// Scene.GetAllComponents skips disabled components (the round-outline lesson), so a feature toggled OFF
	// would be unfindable to toggle back ON - enumerate every object (disabled included) instead.
	static IEnumerable<T> AllIncludingDisabled<T>() where T : Component =>
		Game.ActiveScene.GetAllObjects( false ).SelectMany( go => go.Components.GetAll<T>( FindMode.EverythingInSelf ) );

	[ConCmd( "mimi_bench_set" )]
	public static void Set( string feature, bool on )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		int n = 0;
		switch ( feature )
		{
			case "sdfshadows":
				foreach ( var r in AllIncludingDisabled<SdfRaymarchRenderer>() ) { r.SdfShadows = on; n++; }
				break;
			case "boil":
				foreach ( var b in AllIncludingDisabled<ClayBoil>() ) { b.Activation = on ? BoilActivation.Always : BoilActivation.Never; n++; }
				break;
			case "collider":
				foreach ( var c in AllIncludingDisabled<SdfCollider>() ) { c.Enabled = on; n++; }
				break;
			case "renderer":
				foreach ( var r in AllIncludingDisabled<SdfRaymarchRenderer>() ) { r.Enabled = on; n++; }
				break;
			case "lod":
				foreach ( var r in AllIncludingDisabled<SdfRaymarchRenderer>() ) { r.DistanceSwitching = on; n++; }
				break;
			case "shadowlod":
				foreach ( var r in AllIncludingDisabled<SdfRaymarchRenderer>() ) { r.SdfShadowRadii = on ? 10f : 0f; n++; }
				break;
			case "overdraw":
				foreach ( var r in AllIncludingDisabled<SdfRaymarchRenderer>() ) { r.OverdrawOptimization = on; n++; }
				break;
			default:
				Log.Warning( $"[bench] unknown feature '{feature}' (sdfshadows|boil|collider|renderer|lod|shadowlod|overdraw)" );
				return;
		}

		Log.Info( $"[bench] {feature}={on} on {n} components" );
	}

	// Clones swept up by tag on the next wall build, so repeated calls don't accumulate.
	const string WallTag = "benchwall";

	// Scale load PAST the present-rate ceiling: clone the named prop into a YZ wall (SdfPerfGrid's
	// SpreadWall arrangement, but console-invokable mid-play). 0 clears.
	[ConCmd( "mimi_bench_wall" )]
	public static void Wall( int count )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		foreach ( var old in scene.GetAllObjects( false ).Where( go => go.Tags.Has( WallTag ) ).ToList() )
			old.Destroy();

		// Prefer a spawned prop over incidental SDF renderers (the detector gun is one!).
		var source = scene.GetAllComponents<SdfRaymarchRenderer>().Select( r => r.GameObject )
			.FirstOrDefault( go => go.Name.Contains( "Random Prop" ) )
			?? scene.GetAllComponents<SdfRaymarchRenderer>().FirstOrDefault()?.GameObject;
		if ( source is null ) { Log.Warning( "[bench] no SdfRaymarchRenderer in scene to clone" ); return; }

		int side = (int)MathF.Ceiling( MathF.Sqrt( count ) );
		for ( int i = 0; i < count; i++ )
		{
			int y = i % side, z = i / side;
			var clone = source.Clone( source.WorldPosition + new Vector3( 0, (y - side / 2) * 48f, z * 48f + 24f ) );
			clone.Name = $"benchwall_{i}";
			clone.Tags.Add( WallTag );
		}

		Log.Info( $"[bench] wall: {count} clones of '{source.Name}'" );
	}
}
