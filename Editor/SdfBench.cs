using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mimiclay;
using Sandbox;
using Sandbox.Diagnostics;
using Sandbox.Engine;
using Sandbox.Engine.Settings;

namespace Editor;

/// <summary>
/// Lean, repeatable SDF benchmark on the 256-prop development scene (scenes/sdf_development.scene).
/// See Tools/sdf/bench/README.md.
///
/// Every timed sample is paired with a CONTROL sample — same camera, same lights, every SDF prop hidden
/// (<see cref="SdfRaymarchRenderer.HideAll"/>) — so the headline number is the GPU time the props themselves
/// cost, with the editor, sky, floor and light setup subtracted out. Two suites:
/// <list type="bullet">
/// <item><b>shader</b>: everything forced to full quality and raymarched — the raw cost of the SDF shader.</item>
/// <item><b>lod</b>: the same scene from near to distant under three policies (full / adaptive march quality /
/// adaptive + mesh hand-off + shadow LOD), plus a dolly that flies through every band transition.</item>
/// </list>
/// One small JSON per run lands in Tools/sdf/bench/runs; <see cref="Compare"/> diffs two of them.
/// </summary>
public static class SdfBench
{
	const int Version = 1;
	const string DevScene = "scenes/sdf_development.scene";
	const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

	/// <summary>Full = every prop marches at full quality with marched shadows. Adaptive = the shader's own
	/// quality LOD (AdaptiveQuality). Shadow = adaptive + the authored shadow LOD (far props cast with their mesh).
	/// Mesh = adaptive + distance switching to meshes, shadows still forced marched in the SDF band.
	/// Lod = everything on: adaptive + shadow LOD + mesh hand-off. Each LOD system's saving is its row vs Adaptive.</summary>
	enum Lod { Full, Adaptive, Shadow, Mesh, Lod }

	static bool ShadowLod( Lod l ) => l is Lod.Shadow or Lod.Lod;
	static bool MeshHandoff( Lod l ) => l is Lod.Mesh or Lod.Lod;

	/// <summary>One timed configuration. View is a dev-scene view (-1 dense, -2 overview, -3 close crowd, 0–255 one
	/// prop); Back &gt; 0 instead puts the camera that far behind the gallery. A dolly flies from Back to the close crowd.</summary>
	sealed record Case( string Name, int View, float Back, int Lights, Lod Lod, bool Dolly = false, bool Sun = false, float SpotPushBack = 0 )
	{
		public string ControlKey => $"{View}/{Back}/{Lights}/{(Sun ? "sun" : "")}/{SpotPushBack}";

		/// <summary>Frame cap while this case runs. Shadow-map passes are memory-latency-bound and run ~4× slower in
		/// frames that start after the GPU idled (cold caches — Nsight, 2026-10-24: L2 hit ~25% vs ~85% warm). Uncapped,
		/// the editor mixes the two regimes sample to sample; capped at 60 every frame is cold, which is repeatable AND
		/// what a V-synced/capped player sees. Only LOCAL-light cases are capped: the sun's cascades went bimodal under a
		/// cap (5.1–6.6 ms) but are stable uncapped, as are cases without shadow-casting lights.</summary>
		public int Cap => Lights > 0 ? 60 : 0;
	}

	static Case[] ShaderSuite() => new Case[]
	{
		new( "dense", -1, 0, 0, Lod.Full ),        // ~90% of the screen is clay, four overlapping layers
		new( "close", -3, 0, 0, Lod.Full ),        // pixel pressure: big props filling the screen
		new( "complex", 63, 0, 0, Lod.Full ),      // one 42-brush prop (fruit bowl) close up: brush evaluation cost
		new( "dense-4spots", -1, 0, 4, Lod.Full ), // + four shadowed spots, every prop a marched caster
		new( "dense-sun", -1, 0, 0, Lod.Full, Sun: true ), // the sun casts: 4 ortho cascades, every prop a marched caster
		// The same four spots 500 u further back (radius grown to match): big shadow-map texels, like a ceiling light
		// across a room. Catches texel-scaled shadow settings that only misbehave far from the light (2026-10-24: a
		// texel-scaled caster bias erased a dinner plate's shadow under a 683 u spot; the near-spot case never showed it).
		new( "dense-4spots-far", -1, 0, 4, Lod.Full, SpotPushBack: 500 ),
	};

	static Case[] LodSuite()
	{
		var list = new List<Case>();
		foreach ( var (name, view, back) in new (string, int, float)[] { ("close", -3, 0), ("dense", -1, 0), ("overview", -2, 0), ("far", -1, 1200), ("distant", -1, 3000) } )
			foreach ( var lod in new[] { Lod.Full, Lod.Adaptive, Lod.Shadow, Lod.Mesh, Lod.Lod } )
				list.Add( new( $"{name}-{lod.ToString().ToLowerInvariant()}", view, back, 4, lod ) );
		list.Add( new( "dolly-full", -1, 3000, 4, Lod.Full, true ) );
		list.Add( new( "dolly-lod", -1, 3000, 4, Lod.Lod, true ) );
		return list.ToArray();
	}

	// ---------------------------------------------------------------- run state

	/// <summary>Arm 0 = the case as-is (A). Arm n ≥ 1 = the same case with the run's n-th <c>ab</c> override set
	/// applied, interleaved right after A against the same control. One arm is named [B], several [B1], [B2]…</summary>
	sealed record Step( Case Case, bool Control, int Rep, int Arm = 0 )
	{
		public string Key => Control ? Case.ControlKey : Case.Name + ArmSuffix( Arm );
	}

	static string ArmSuffix( int arm ) => arm == 0 ? "" : _arms.Count == 1 ? " [B]" : $" [B{arm}]";

	sealed class Sample
	{
		public readonly List<float> Gpu = new(), Frame = new();
		public readonly Dictionary<string, double> PassSum = new();
		public readonly Dictionary<string, int> PassFrames = new(); // GPU frames in which each leaf path appeared
		public Dictionary<string, int> Bands;
		public double UpdateMs, CountedMiB;
		public int UniqueFields;
		public readonly Dictionary<string, List<float>> PassPerFrame = new(); // each leaf path's ms per GPU frame (0 when absent)
		// MEDIANS, not means. Measured 2026-10-24: whenever the GPU idles between frames (a frame cap, or a CPU-bound
		// editor frame) the first passes of the next frame — the shadow maps — run ~4× slower for IDENTICAL work
		// (spot shadows 2.4 vs ~9 ms, bimodal per frame). Means mixed the two regimes and made the spot case
		// unmeasurable; the median tracks the GPU work, and SlowFrames reports how much of the sample was affected.
		public double GpuMid => Percentile( Gpu, .5 );
		public double PassMid( string path ) => PassPerFrame.TryGetValue( path, out var l ) ? Percentile( l, .5 ) : 0;
		public double SlowFrames { get { double m = GpuMid; return Gpu.Count == 0 ? 0 : Gpu.Count( g => g > 1.3 * m ) / (double)Gpu.Count; } }
	}

	static string _phase = "idle", _suite, _label, _output, _error;
	static double _phaseAt, _readyAt, _lastTick;
	static int _repeats, _stepIndex;
	static float _seconds;
	static bool _startedPlay;
	static Case[] _cases;
	static List<Step> _steps;
	static Scene _scene;
	static SdfDevelopmentScene _dev;
	static Lod? _appliedLod;
	static Sample _sample;
	static ulong _lastGpuFrame;
	static object _memory;
	static string _sourceHash, _imagesDir;
	static readonly List<string> _warnings = new();
	static readonly Dictionary<(string, int), Sample> On = new(), Control = new();

	// ConVar overrides: Set applies to the whole run; Ab is applied only to the interleaved B steps.
	static Dictionary<string, string> _set = new(), _abBase = new();
	static List<Dictionary<string, string>> _arms = new();
	static readonly Dictionary<string, string> _convarRestore = new();

	static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
	static string Root => Path.GetDirectoryName( Project.Current.GetAssetsPath().TrimEnd( '/', '\\' ) );
	static string RunsDir => Path.Combine( Root, "Tools/sdf/bench/runs" );

	// Restored on completion, cancellation or failure.
	static RenderSettings _settings;
	static (int Max, int Inactive, int Menu, bool VSync, bool Profiler) _oldSettings;
	static readonly List<(SceneEditorSession Session, bool Update)> _sessions = new();
	static (int View, float Distance, int Lights, bool Shadows, bool Force, Transform Camera, bool Fly) _oldDev;
	static readonly Dictionary<SdfRaymarchRenderer, (bool Adaptive, bool Switching)> _oldRenderers = new();
	static DirectionalLight _sun;
	static readonly List<(SpotLight Light, Vector3 Position, float Radius)> _oldSpots = new();

	/// <summary>Move every dev-scene spot back along its own axis by pushBack units (radius grown to match), or restore
	/// them with 0. Larger distance = larger shadow-map texels on the props.</summary>
	static void PlaceSpots( float pushBack )
	{
		foreach ( var (l, pos, radius) in _oldSpots )
		{
			if ( !l.IsValid() ) continue;
			l.WorldPosition = pos - l.WorldRotation.Forward * pushBack;
			l.Radius = radius + pushBack;
		}
	}
	static bool _oldSunShadows;

	// ---------------------------------------------------------------- entry points

	[Menu( "Editor", "Mimiclay/SDF Bench/Run Shader Suite", "speed" )]
	public static void RunShaderMenu() => Run( "shader" );

	[Menu( "Editor", "Mimiclay/SDF Bench/Run LOD Suite", "speed" )]
	public static void RunLodMenu() => Run( "lod" );

	[Menu( "Editor", "Mimiclay/SDF Bench/Quick Shader Check (not comparable)", "bolt" )]
	public static void RunQuickMenu() => Run( "shader", repeats: 1, seconds: 3 );

	[Menu( "Editor", "Mimiclay/SDF Bench/Compare Latest Two", "compare_arrows" )]
	public static void CompareMenu() => Compare();

	[Menu( "Editor", "Mimiclay/SDF Bench/Stop", "stop" )]
	public static void StopMenu() => Stop();

	[Menu( "Editor", "Mimiclay/SDF Bench/Open Results Folder", "folder" )]
	public static void OpenFolderMenu() { Directory.CreateDirectory( RunsDir ); EditorUtility.OpenFolder( RunsDir ); }

	/// <summary>Start a run. suite: shader | lod. Reuses a running play session of the development scene (keeps the
	/// fields resident across hotloads); otherwise opens the scene and plays it, and stops it again afterwards.
	/// set: ConVar overrides for the whole run, "name=value,name=value". ab: ConVar overrides for an interleaved
	/// A/B — every case is measured as-is (A) and then with these overrides (B), back to back, in the same session.</summary>
	[Mcp.McpTool( "sdf_bench_run" )]
	public static object Run( string suite = "shader", string label = null, int repeats = 3, float seconds = 2, string set = null, string ab = null )
	{
		if ( Busy ) throw new InvalidOperationException( "A benchmark is already running (Mimiclay → SDF Bench → Stop)." );
		_cases = suite switch { "shader" => ShaderSuite(), "lod" => LodSuite(), _ => throw new ArgumentException( "suite must be shader or lod" ) };
		BindGpuProfiler(); // fail before touching editor state if the engine API moved
		_set = ParseOverrides( set );
		// "a=1;a=2,b=0" → two arms. Each arm is measured against the same A and control.
		_arms = (ab ?? "").Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
			.Select( ParseOverrides ).Where( o => o.Count > 0 ).ToList();

		_warnings.Clear();
		_sourceHash = SourceHash();
		if ( StaleShaderWarning() is string stale ) _warnings.Add( stale );

		_suite = suite;
		_repeats = Math.Clamp( repeats, 1, 10 );
		_seconds = Math.Clamp( seconds, 2, 60 );
		var (commit, dirty) = GitState();
		_label = Sanitise( string.IsNullOrWhiteSpace( label ) ? commit + (dirty > 0 ? "-dirty" : "") : label );
		var stem = $"{DateTime.Now:yyyyMMdd-HHmmss}-{suite}-{_label}";
		_output = Path.Combine( RunsDir, stem + ".json" );
		_imagesDir = Path.Combine( RunsDir, "images", stem );
		_error = null;
		On.Clear(); Control.Clear(); _imageNoise.Clear();
		_steps = new List<Step>();
		for ( int rep = 0; rep < _repeats; rep++ )
		{
			var measured = new HashSet<string>();
			foreach ( var c in _cases )
			{
				_steps.Add( new Step( c, false, rep ) );
				for ( int arm = 1; arm <= _arms.Count; arm++ ) _steps.Add( new Step( c, false, rep, arm ) );
				// One control per camera/light setup per repeat, taken right after its first case.
				if ( !c.Dolly && measured.Add( c.ControlKey ) )
					_steps.Add( new Step( c, true, rep ) );
			}
		}

		if ( Game.IsPlaying )
		{
			if ( FindDev( Game.ActiveScene ) is null )
				throw new InvalidOperationException( "Stop play, or play scenes/sdf_development.scene, before benchmarking." );
			_startedPlay = false;
			Phase( "loading" );
		}
		else
		{
			_startedPlay = true;
			EditorScene.OpenScene( SceneFile.Load( DevScene ) );
			Phase( "opening" );
		}

		Log.Info( $"[sdf-bench] {suite}: {_cases.Length} cases × {_repeats} × {_seconds:0.#}s, ~{EstimateMinutes():0.#} min after the scene settles. Don't touch the editor while it runs." );
		return Status();
	}

	[Mcp.McpTool( "sdf_bench_status" )]
	public static object Status() => new
	{
		Phase = _phase, Suite = _suite, Step = _stepIndex, Steps = _steps?.Count ?? 0,
		Current = _steps is not null && _stepIndex < _steps.Count ? $"{_steps[_stepIndex].Key}{(_steps[_stepIndex].Control ? " (control)" : "")} r{_steps[_stepIndex].Rep + 1}" : null,
		Output = _output, Error = _error
	};

	[Mcp.McpTool( "sdf_bench_stop" )]
	public static object Stop()
	{
		if ( Busy ) { _error = "Cancelled"; Cleanup(); Phase( "cancelled" ); Log.Warning( "[sdf-bench] cancelled" ); }
		return Status();
	}

	static bool Busy => _phase is "opening" or "loading" or "settle" or "sample";

	static double EstimateMinutes() => _steps.Select( ( s, i ) => _seconds + SettleSeconds( i ) ).Sum() / 60.0;

	/// <summary>A step that only toggles a ConVar or the control's hide flag leaves the scene as it was, so it
	/// needs just enough time for GPU timestamps (which lag a few frames) to catch up. Moving the camera,
	/// changing lights, the sun or the LOD policy needs shadow maps, bands and fields to settle first.</summary>
	static bool HeavyChange( int index ) => index == 0 || !SameScene( _steps[index].Case, _steps[index - 1].Case );
	static bool SameScene( Case a, Case b ) => a.View == b.View && a.Back == b.Back && a.Lights == b.Lights && a.Lod == b.Lod && a.Sun == b.Sun && a.SpotPushBack == b.SpotPushBack && !a.Dolly && !b.Dolly;
	static double SettleSeconds( int index ) => HeavyChange( index ) ? 1.5 : 0.4;

	// ---------------------------------------------------------------- the frame loop

	[EditorEvent.Frame]
	public static void Tick()
	{
		if ( !Busy ) return;
		try { TickInner(); }
		catch ( Exception e ) { Fail( e ); }
	}

	static void TickInner()
	{
		if ( _phase == "opening" )
		{
			var session = SceneEditorSession.Active;
			if ( session?.Scene is { } s && FindDev( s ) is not null )
			{
				EditorScene.Play( session );
				Phase( "loading" );
			}
			else if ( Now - _phaseAt > 60 ) throw new TimeoutException( $"Couldn't open {DevScene}." );
			return;
		}

		if ( _phase == "loading" )
		{
			var dev = Game.IsPlaying ? FindDev( Game.ActiveScene ) : null;
			if ( dev is null || !SdfBenchProbe.Ready( dev.Scene ) ) _readyAt = Now;
			if ( Now - _phaseAt > 300 ) throw new TimeoutException( "Fields/meshes did not settle within 5 minutes." );
			if ( dev is not null && Now - _readyAt >= 3 && Now - _phaseAt >= 5 )
				Begin( dev );
			return;
		}

		if ( !Game.IsPlaying || Game.ActiveScene != _scene || !_dev.IsValid() )
			throw new InvalidOperationException( "The play session stopped or changed during the benchmark." );
		KeepGpuProfilerAlive();

		var step = _steps[_stepIndex];
		if ( _phase == "settle" )
		{
			bool heavy = HeavyChange( _stepIndex );
			if ( heavy && !SdfBenchProbe.Ready( _scene ) ) _readyAt = Now;
			if ( Now - _phaseAt > 120 ) throw new TimeoutException( $"{step.Case.Name}: fields/meshes did not settle." );
			bool settled = !heavy || Now - _readyAt >= 0.5;
			if ( settled && Now - _phaseAt >= SettleSeconds( _stepIndex ) )
			{
				_sample = new Sample();
				_lastGpuFrame = _gpuFrameId();
				_lastTick = Now;
				SdfBenchProbe.TakeRendererUpdateMs(); // discard the settle period
				Phase( "sample" );
			}
			return;
		}

		// sampling
		var now = Now;
		_sample.Frame.Add( (float)((now - _lastTick) * 1000) );
		_lastTick = now;
		var gpuFrame = _gpuFrameId();
		if ( gpuFrame != 0 && gpuFrame != _lastGpuFrame )
		{
			_lastGpuFrame = gpuFrame;
			ReadGpuFrame( _sample );
		}

		float t = (float)((now - _phaseAt) / _seconds);
		if ( step.Case.Dolly )
			PlaceDolly( Math.Clamp( t, 0, 1 ) );
		if ( t < 1 ) return;

		if ( _sample.Gpu.Count < 20 )
			throw new InvalidOperationException( $"{step.Case.Name}: only {_sample.Gpu.Count} GPU frames — is the GPU profiler running?" );
		_sample.UpdateMs = SdfBenchProbe.TakeRendererUpdateMs() / Math.Max( _sample.Frame.Count, 1 );
		if ( !step.Control )
		{
			_sample.Bands = SdfBenchProbe.Bands( _scene );
			(_sample.CountedMiB, _sample.UniqueFields) = SdfBenchProbe.Residency( _scene );
			// One reference image per case, from the last repeat, after timing (never inside a sample).
			if ( step.Rep == _repeats - 1 && !step.Case.Dolly ) Capture( step.Key );
		}
		(step.Control ? Control : On)[(step.Key, step.Rep)] = _sample;
		StartStep( _stepIndex + 1 );
	}

	static void Begin( SdfDevelopmentScene dev )
	{
		_dev = dev;
		_scene = dev.Scene;
		_memory = SdfBenchProbe.Snapshot( _scene, perRenderer: false );

		_settings = (RenderSettings)typeof( RenderSettings ).GetField( "Instance", All ).GetValue( null );
		_oldSettings = (_settings.MaxFrameRate, _settings.MaxFrameRateInactive, _settings.MaxFrameRateMenu, _settings.VSync, GpuProfilerStats.Enabled);
		_settings.MaxFrameRate = 0; _settings.MaxFrameRateInactive = 0; _settings.MaxFrameRateMenu = 0; _settings.VSync = false;
		_settings.Apply();
		GpuProfilerStats.Enabled = true;

		// Editor scene views would otherwise keep rendering their own scenes into the GPU timings.
		_sessions.Clear();
		foreach ( var s in SceneEditorSession.All ) { _sessions.Add( (s, s.ShouldUpdate) ); s.ShouldUpdate = false; }

		var fly = dev.Camera.Components.Get<FlyCamera>();
		_oldDev = (dev.View, dev.CloseDistance, dev.LightCount, dev.Shadows, dev.ForceSdfShadows, dev.Camera.WorldTransform, fly.IsValid() && fly.Enabled);
		if ( fly.IsValid() ) fly.Enabled = false; // no stray input moves the camera mid-sample
		_oldRenderers.Clear();
		foreach ( var r in _scene.GetAllComponents<SdfRaymarchRenderer>() ) _oldRenderers[r] = (r.AdaptiveQuality, r.DistanceSwitching);
		_appliedLod = null;
		_sun = _scene.GetAllComponents<DirectionalLight>().FirstOrDefault();
		_oldSunShadows = _sun.IsValid() && _sun.Shadows;
		_oldSpots.Clear();
		foreach ( var l in _dev.Lights ) if ( l.IsValid() ) _oldSpots.Add( (l, l.WorldPosition, l.Radius) );

		_convarRestore.Clear();
		ApplyConvars( _set );
		_abBase = _arms.SelectMany( a => a.Keys ).Distinct().ToDictionary( k => k, k => Sandbox.ConsoleSystem.GetValue( k ) );

		StartStep( 0 );
	}

	static void StartStep( int index )
	{
		_stepIndex = index;
		if ( index >= _steps.Count ) { Finish(); return; }

		var step = _steps[index];
		var c = step.Case;
		SdfRaymarchRenderer.HideAll = step.Control;
		if ( _arms.Count > 0 )
		{
			ApplyConvars( _abBase );
			if ( step.Arm > 0 ) ApplyConvars( _arms[step.Arm - 1] );
		}

		if ( _appliedLod != c.Lod )
		{
			foreach ( var r in _scene.GetAllComponents<SdfRaymarchRenderer>() )
			{
				r.AdaptiveQuality = c.Lod != Lod.Full;
				r.DistanceSwitching = MeshHandoff( c.Lod );
			}
			_appliedLod = c.Lod;
		}

		if ( _sun.IsValid() ) _sun.Shadows = c.Sun;
		PlaceSpots( c.SpotPushBack );
		if ( _settings is not null && _settings.MaxFrameRate != c.Cap )
		{
			_settings.MaxFrameRate = c.Cap; _settings.MaxFrameRateInactive = c.Cap;
			_settings.Apply();
		}
		_dev.ForceSdfShadows = !ShadowLod( c.Lod );
		_dev.LightCount = c.Lights;
		_dev.Shadows = true;
		_dev.CloseDistance = 1.3f;
		_dev.View = c.View;
		_dev.Apply( true );
		if ( c.Dolly ) PlaceDolly( 0 );
		else if ( c.Back > 0 ) PlaceBack( c.Back );

		_readyAt = Now;
		Phase( "settle" );
	}

	static void PlaceBack( float back )
	{
		_dev.Camera.WorldPosition = _dev.GalleryPosition - _dev.GalleryRotation.Forward * back;
		_dev.Camera.WorldRotation = _dev.GalleryRotation;
	}

	/// <summary>Distant (Back) → close crowd, eased so the camera spends real time in every band.</summary>
	static void PlaceDolly( float t )
	{
		var from = _dev.GalleryPosition - _dev.GalleryRotation.Forward * _steps[_stepIndex].Case.Back;
		var to = _dev.GalleryPosition + _dev.GalleryRotation.Forward * 120 + Vector3.Up * 100; // = the close-crowd view
		_dev.Camera.WorldPosition = Vector3.Lerp( from, to, 1 - (1 - t) * (1 - t) );
		_dev.Camera.WorldRotation = _dev.GalleryRotation;
	}

	static void Finish()
	{
		var (commit, dirty) = GitState();
		if ( SourceHash() != _sourceHash ) _warnings.Add( "SDF code or shader sources changed during the run — results mix two builds." );
		if ( Screen.Size.x != 1920 || Screen.Size.y != 1080 ) _warnings.Add( $"Game view is {Screen.Size.x:0}x{Screen.Size.y:0}, not the reference 1920x1080." );
		var run = new RunFile
		{
			Version = Version, Suite = _suite, Label = _label, Commit = commit, DirtyFiles = dirty, Utc = DateTime.UtcNow,
			Repeats = _repeats, Seconds = _seconds, Quick = _repeats < 3,
			SourceHash = _sourceHash, Set = _set, Ab = _arms.FirstOrDefault() ?? new(), Arms = _arms, Warnings = _warnings.ToList(),
			Env = EnvSnapshot(), Memory = _memory,
			Cases = _cases.SelectMany( c => Enumerable.Range( 0, _arms.Count + 1 ).Select( arm => Summarise( c, arm ) ) ).ToList()
		};
		Directory.CreateDirectory( RunsDir );
		File.WriteAllText( _output, JsonSerializer.Serialize( run, JsonOptions ) );
		Cleanup();
		Phase( "done" );
		Log.Info( Table( run ) + (_arms.Count > 0 ? "\n" + AbTable( run ) : "") + $"\nSaved {Path.GetRelativePath( Root, _output )}" );
	}

	/// <summary>Renders the case twice: the first is saved as the reference, the second only measures how much two
	/// renders of the SAME frame disagree (the noise floor any A/B image difference has to beat).</summary>
	static void Capture( string key )
	{
		try
		{
			Directory.CreateDirectory( _imagesDir );
			using var image = new Bitmap( (int)Screen.Size.x, (int)Screen.Size.y );
			using var repeat = new Bitmap( (int)Screen.Size.x, (int)Screen.Size.y );
			_dev.Camera.RenderToBitmap( image, false );
			_dev.Camera.RenderToBitmap( repeat, false );
			File.WriteAllBytes( Path.Combine( _imagesDir, ImageName( key ) ), image.ToPng() );
			_imageNoise[key] = DiffStats( image, repeat, null );
		}
		catch ( Exception e ) { _warnings.Add( $"Image capture failed for {key}: {e.Message}" ); }
	}

	static readonly Dictionary<string, string> _imageNoise = new();

	static string ImageName( string key ) => Sanitise( key.Replace( " [", "-" ).Replace( "]", "" ) ) + ".png";

	static Dictionary<string, string> ParseOverrides( string text )
	{
		var result = new Dictionary<string, string>();
		if ( string.IsNullOrWhiteSpace( text ) ) return result;
		foreach ( var part in text.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var kv = part.Split( '=', 2, StringSplitOptions.TrimEntries );
			if ( kv.Length != 2 ) throw new ArgumentException( $"Override '{part}' must be name=value." );
			if ( Sandbox.ConsoleSystem.GetValue( kv[0] ) is null ) throw new ArgumentException( $"Unknown ConVar '{kv[0]}'." );
			result[kv[0]] = kv[1];
		}
		return result;
	}

	static void ApplyConvars( Dictionary<string, string> values )
	{
		foreach ( var (name, value) in values )
		{
			_convarRestore.TryAdd( name, Sandbox.ConsoleSystem.GetValue( name ) );
			if ( Sandbox.ConsoleSystem.GetValue( name ) != value ) Sandbox.ConsoleSystem.SetValue( name, value );
		}
	}

	static void Fail( Exception e )
	{
		_error = e.Message;
		Log.Error( $"[sdf-bench] failed: {e}" );
		Cleanup();
		Phase( "failed" );
	}

	static void Cleanup()
	{
		SdfRaymarchRenderer.HideAll = false;
		foreach ( var (name, value) in _convarRestore ) Sandbox.ConsoleSystem.SetValue( name, value );
		_convarRestore.Clear();
		if ( _settings is not null )
		{
			(_settings.MaxFrameRate, _settings.MaxFrameRateInactive, _settings.MaxFrameRateMenu, _settings.VSync, _) = _oldSettings;
			_settings.Apply();
			GpuProfilerStats.Enabled = _oldSettings.Profiler;
			_settings = null;
		}
		foreach ( var (session, update) in _sessions ) if ( session is not null ) session.ShouldUpdate = update;
		_sessions.Clear();

		if ( _startedPlay )
		{
			if ( Game.IsPlaying ) EditorScene.Stop(); // a fresh play scene: stopping discards every change
		}
		else if ( _dev.IsValid() && _scene is not null )
		{
			foreach ( var (r, old) in _oldRenderers ) if ( r.IsValid() ) { r.AdaptiveQuality = old.Adaptive; r.DistanceSwitching = old.Switching; }
			if ( _sun.IsValid() ) _sun.Shadows = _oldSunShadows;
			PlaceSpots( 0 );
			_dev.View = _oldDev.View; _dev.CloseDistance = _oldDev.Distance; _dev.LightCount = _oldDev.Lights;
			_dev.Shadows = _oldDev.Shadows; _dev.ForceSdfShadows = _oldDev.Force;
			_dev.Apply( true );
			_dev.Camera.WorldTransform = _oldDev.Camera;
			var fly = _dev.Camera.Components.Get<FlyCamera>();
			if ( fly.IsValid() ) fly.Enabled = _oldDev.Fly;
		}
		_oldRenderers.Clear();
		_dev = null; _scene = null;
	}

	static void Phase( string phase ) { _phase = phase; _phaseAt = Now; }

	static SdfDevelopmentScene FindDev( Scene scene ) => scene?.GetAllComponents<SdfDevelopmentScene>().FirstOrDefault();

	// ---------------------------------------------------------------- GPU profiler (engine internals, read-only)

	// GpuProfilerStats only exposes SMOOTHED durations publicly; the raw per-frame rows are internal. Bound once per
	// run so an engine change fails loudly at start rather than producing silent zeros.
	static Func<ulong> _gpuFrameId;
	static Func<float> _gpuBusy;
	static Func<int> _rowCount;
	static MethodInfo _getRow;
	static FieldInfo _rowName, _rowParent, _rowMeasured, _rowDuration;
	static readonly object[] _rowArg = new object[1];

	static Action<string, float> _collectFor;
	static double _keepCollectingAt;

	/// <summary>The engine only refreshes GPU timestamps while the dev tools are open or a tool asked for them
	/// (DevTools.CollectFor) — so keep asking, a few seconds at a time, for as long as the run lasts.</summary>
	static void KeepGpuProfilerAlive()
	{
		if ( Now < _keepCollectingAt ) return;
		_collectFor( "gpu", 3 );
		_keepCollectingAt = Now + 1;
	}

	static void BindGpuProfiler()
	{
		var devTools = typeof( PerformanceStats ).Assembly.GetType( "Sandbox.Diagnostics.DevTools", true );
		_collectFor = (Action<string, float>)devTools.GetMethod( "CollectFor", All ).CreateDelegate( typeof( Action<string, float> ) );
		_keepCollectingAt = 0;

		var t = typeof( GpuProfilerStats );
		_gpuFrameId = (Func<ulong>)t.GetProperty( "FrameId", All ).GetMethod.CreateDelegate( typeof( Func<ulong> ) );
		_gpuBusy = (Func<float>)t.GetProperty( "BusyMs", All ).GetMethod.CreateDelegate( typeof( Func<float> ) );
		_rowCount = (Func<int>)t.GetProperty( "RowCount", All ).GetMethod.CreateDelegate( typeof( Func<int> ) );
		_getRow = t.GetMethod( "GetRow", All );
		var row = _getRow?.ReturnType;
		_rowName = row?.GetField( "Name", All );
		_rowParent = row?.GetField( "Parent", All );
		_rowMeasured = row?.GetField( "Measured", All );
		_rowDuration = row?.GetField( "Duration", All );
		if ( _rowName is null || _rowParent is null || _rowMeasured is null || _rowDuration is null )
			throw new InvalidOperationException( "GpuProfilerStats internals changed — update SdfBench.BindGpuProfiler." );
	}

	/// <summary>Adds one GPU frame: the graphics-queue busy time, and every LEAF pass's duration under its full
	/// path (leaves only — parents nest their children, so summing everything would double count).</summary>
	static void ReadGpuFrame( Sample s )
	{
		s.Gpu.Add( _gpuBusy() );
		int n = _rowCount();
		var names = new string[n];
		var parents = new int[n];
		var measured = new bool[n];
		var durations = new float[n];
		var hasChild = new bool[n];
		for ( int i = 0; i < n; i++ )
		{
			_rowArg[0] = i;
			var row = _getRow.Invoke( null, _rowArg );
			names[i] = (string)_rowName.GetValue( row );
			parents[i] = (int)_rowParent.GetValue( row );
			measured[i] = (bool)_rowMeasured.GetValue( row );
			durations[i] = (float)_rowDuration.GetValue( row );
		}
		for ( int i = 0; i < n; i++ )
			if ( measured[i] && parents[i] >= 0 && parents[i] < n ) hasChild[parents[i]] = true;
		var seen = new HashSet<string>();
		var frameMs = new Dictionary<string, float>();
		for ( int i = 0; i < n; i++ )
		{
			if ( !measured[i] || hasChild[i] ) continue;
			var path = names[i];
			int p = parents[i], guard = 0;
			string root = names[i];
			while ( p >= 0 && p < n && guard++ < 32 ) { path = names[p] + "/" + path; root = names[p]; p = parents[p]; }
			if ( root == "Async compute" ) continue; // not on the graphics queue
			s.PassSum[path] = s.PassSum.GetValueOrDefault( path ) + durations[i];
			frameMs[path] = frameMs.GetValueOrDefault( path ) + durations[i];
			if ( seen.Add( path ) ) s.PassFrames[path] = s.PassFrames.GetValueOrDefault( path ) + 1;
		}
		// Per-frame series: a path first seen now is back-filled with zeros; one missing this frame gets a zero.
		foreach ( var (path, ms) in frameMs )
		{
			if ( !s.PassPerFrame.TryGetValue( path, out var list ) )
				s.PassPerFrame[path] = list = new List<float>( Enumerable.Repeat( 0f, s.Gpu.Count - 1 ) );
			list.Add( ms );
		}
		foreach ( var (path, list) in s.PassPerFrame )
			if ( !frameMs.ContainsKey( path ) ) list.Add( 0 );
	}

	static string Bucket( string path )
	{
		var p = path.ToLowerInvariant();
		if ( p.Contains( "shadow" ) || p.Contains( "cascade" ) || p.Contains( "csm" ) ) return "shadows";
		if ( p.Contains( "prepass" ) ) return "prepass";
		if ( p.Contains( "forward" ) || p.Contains( "opaque" ) ) return "forward";
		return "other";
	}

	// ---------------------------------------------------------------- results

	public sealed class RunFile
	{
		public int Version { get; set; }
		public string Suite { get; set; }
		public string Label { get; set; }
		public string Commit { get; set; }
		public int DirtyFiles { get; set; }
		public DateTime Utc { get; set; }
		public int Repeats { get; set; }
		public float Seconds { get; set; }
		public bool Quick { get; set; }
		/// <summary>Hash of every SDF C# and shader source. Equal hashes = the same code was measured.</summary>
		public string SourceHash { get; set; }
		public Dictionary<string, string> Set { get; set; }
		public Dictionary<string, string> Ab { get; set; }
		/// <summary>Every A/B arm's overrides, in order ([B] or [B1], [B2]…). Ab repeats the first for older readers.</summary>
		public List<Dictionary<string, string>> Arms { get; set; }
		public List<string> Warnings { get; set; }
		public Dictionary<string, string> Env { get; set; }
		public object Memory { get; set; }
		public List<CaseResult> Cases { get; set; }
	}

	public sealed class CaseResult
	{
		public string Name { get; set; }
		public string Lod { get; set; }
		/// <summary>GPU ms the SDF props cost: mean(on) − mean(control) per repeat, median over repeats. Null for a dolly.</summary>
		public double? SdfMs { get; set; }
		public double? SdfMin { get; set; }
		public double? SdfMax { get; set; }
		/// <summary>Mean graphics-queue busy ms with the props on / hidden (median over repeats).</summary>
		public double GpuMs { get; set; }
		public double? ControlMs { get; set; }
		public double FrameP50 { get; set; }
		public double FrameP99 { get; set; }
		public int Hitches33 { get; set; }
		/// <summary>Share of GPU frames over 1.3× the median — the "slow first passes after GPU idle" regime. Above ~10% the
		/// shadow passes especially are unstable; the medians still track the work.</summary>
		public double SlowFrames { get; set; }
		/// <summary>Frame cap the case ran at (0 = uncapped). Capped shadow cases measure the cold-cache regime.</summary>
		public int Cap { get; set; }
		/// <summary>Main-thread ms per frame across every SDF renderer's OnUpdate (bands, refresh, visibility).</summary>
		public double RendererUpdateMs { get; set; }
		/// <summary>Counted SDF texture payload and unique fields at the end of the sample (residency).</summary>
		public double CountedMiB { get; set; }
		public int UniqueFields { get; set; }
		/// <summary>Reference image file name in runs/images/&lt;run&gt;/, or null for a dolly.</summary>
		public string Image { get; set; }
		/// <summary>Two renders of the same frame compared — the floor an image difference has to beat.</summary>
		public string ImageNoise { get; set; }
		/// <summary>SDF cost split by pass group (mean per frame, on − control).</summary>
		public Dictionary<string, double> Buckets { get; set; }
		/// <summary>Leaf passes whose cost moved by ≥0.05 ms, on − control (or raw for a dolly).</summary>
		public Dictionary<string, double> Passes { get; set; }
		/// <summary>Share of GPU frames (with the props shown) in which each listed pass appeared — below 1 means the
		/// pass was skipped or its timings missing on some frames, so its mean understates the per-frame cost.</summary>
		public Dictionary<string, double> PassPresence { get; set; }
		public Dictionary<string, int> Bands { get; set; }
	}

	static CaseResult Summarise( Case c, int arm )
	{
		var key = new Step( c, false, 0, arm ).Key;
		var reps = Enumerable.Range( 0, _repeats ).Select( r => (On: On[(key, r)], Ctl: c.Dolly ? null : Control[(c.ControlKey, r)]) ).ToList();
		var sdf = c.Dolly ? null : reps.Select( r => r.On.GpuMid - r.Ctl.GpuMid ).ToList();

		var paths = reps.SelectMany( r => r.On.PassSum.Keys.Concat( r.Ctl?.PassSum.Keys ?? Enumerable.Empty<string>() ) ).Distinct();
		var passes = new Dictionary<string, double>();
		foreach ( var path in paths )
		{
			var d = Median( reps.Select( r => r.On.PassMid( path ) - (r.Ctl?.PassMid( path ) ?? 0) ) );
			if ( Math.Abs( d ) >= 0.05 ) passes[path] = Round( d );
		}
		var buckets = new[] { "prepass", "forward", "shadows", "other" }.ToDictionary( b => b, b => Round( Median( reps.Select( r =>
			r.On.PassSum.Keys.Concat( r.Ctl?.PassSum.Keys ?? Enumerable.Empty<string>() ).Distinct().Where( p => Bucket( p ) == b )
				.Sum( p => r.On.PassMid( p ) - (r.Ctl?.PassMid( p ) ?? 0) ) ) ) ) );

		var frames = reps.SelectMany( r => r.On.Frame ).ToList();
		return new CaseResult
		{
			Name = key, Lod = c.Lod.ToString().ToLowerInvariant(),
			SdfMs = sdf is null ? null : Round( Median( sdf ) ), SdfMin = sdf is null ? null : Round( sdf.Min() ), SdfMax = sdf is null ? null : Round( sdf.Max() ),
			GpuMs = Round( Median( reps.Select( r => r.On.GpuMid ) ) ),
			ControlMs = c.Dolly ? null : Round( Median( reps.Select( r => r.Ctl.GpuMid ) ) ),
			FrameP50 = Round( Median( reps.Select( r => Percentile( r.On.Frame, .5 ) ) ) ),
			FrameP99 = Round( Median( reps.Select( r => Percentile( r.On.Frame, .99 ) ) ) ),
			Hitches33 = frames.Count( f => f > 33.3f ),
			SlowFrames = Round( Median( reps.Select( r => r.On.SlowFrames ) ) ),
			Cap = c.Cap,
			RendererUpdateMs = Round( Median( reps.Select( r => r.On.UpdateMs ) ) ),
			CountedMiB = reps[^1].On.CountedMiB,
			UniqueFields = reps[^1].On.UniqueFields,
			Image = c.Dolly ? null : ImageName( key ),
			ImageNoise = _imageNoise.GetValueOrDefault( key ),
			Buckets = buckets,
			Passes = passes.OrderByDescending( p => Math.Abs( p.Value ) ).ToDictionary( p => p.Key, p => p.Value ),
			PassPresence = passes.Keys.ToDictionary( p => p, p => Round( Median( reps.Select( r => r.On.Gpu.Count == 0 ? 0 : r.On.PassFrames.GetValueOrDefault( p ) / (double)r.On.Gpu.Count ) ) ) ),
			Bands = reps[^1].On.Bands,
		};
	}

	static Dictionary<string, string> EnvSnapshot() => new()
	{
		["Gpu"] = SystemInfo.Gpu?.ToString(),
		["Resolution"] = $"{Screen.Size.x:0}x{Screen.Size.y:0}",
		["Msaa"] = _settings?.AntiAliasQuality.ToString(),
		["Upscaler"] = _settings?.UpscalerMode.ToString(),
		["RenderScale"] = _settings?.UpscalerRenderScale.ToString(),
		["ShadowQuality"] = _settings?.ShadowQuality.ToString(),
		["Renderer"] = typeof( GpuProfilerStats ).GetProperty( "Renderer", All )?.GetValue( null )?.ToString(),
		["Props"] = _scene?.GetAllComponents<SdfRaymarchRenderer>().Count().ToString(),
	};

	/// <summary>The values that must match for two runs to be comparable at all.</summary>
	static readonly string[] ComparableEnv = { "Gpu", "Resolution", "Msaa", "Upscaler", "RenderScale", "ShadowQuality", "Props" };

	static string Table( RunFile run )
	{
		var sb = new StringBuilder();
		sb.AppendLine( $"[sdf-bench] {run.Suite} '{run.Label}' {run.Commit}{(run.DirtyFiles > 0 ? $" (+{run.DirtyFiles} dirty)" : "")} {run.Env.GetValueOrDefault( "Resolution" )} {run.Repeats}×{run.Seconds:0.#}s{(run.Quick ? " QUICK" : "")}" );
		foreach ( var w in run.Warnings ?? new() ) sb.AppendLine( $"  ⚠ {w}" );
		if ( run.Set?.Count > 0 ) sb.AppendLine( $"  set: {Overrides( run.Set )}" );
		sb.AppendLine( $"{"case",-22} {"sdf ms",7} {"range",13}  {"prepass",7} {"forward",7} {"shadows",7} {"other",6}  {"gpu",6} {"ctl",6} {"p99",6} {"cpu",5} {"MiB",6} {"slow",5}  bands" );
		foreach ( var c in run.Cases )
		{
			string sdf = c.SdfMs is double v ? $"{v,7:0.00}" : $"{"—",7}";
			string range = c.SdfMin is double lo && c.SdfMax is double hi ? $"{lo:0.00}–{hi:0.00}" : "";
			string bands = c.Bands is null ? "" : string.Join( " ", c.Bands.Select( b => $"{b.Key}={b.Value}" ) );
			sb.AppendLine( $"{c.Name,-22} {sdf} {range,13}  {c.Buckets["prepass"],7:0.00} {c.Buckets["forward"],7:0.00} {c.Buckets["shadows"],7:0.00} {c.Buckets["other"],6:0.00}  {c.GpuMs,6:0.00} {c.ControlMs ?? 0,6:0.00} {c.FrameP99,6:0.0} {c.RendererUpdateMs,5:0.00} {c.CountedMiB,6:0} {c.SlowFrames,5:P0}  {bands}" );
		}
		return sb.ToString();
	}

	static string Overrides( Dictionary<string, string> o ) => string.Join( ", ", o.Select( kv => $"{kv.Key}={kv.Value}" ) );

	/// <summary>In-run A/B: each case against each interleaved arm, same session, same control.</summary>
	static string AbTable( RunFile run )
	{
		var sb = new StringBuilder();
		var images = Path.Combine( RunsDir, "images", Path.GetFileNameWithoutExtension( _output ) );
		for ( int arm = 1; arm <= run.Arms.Count; arm++ )
		{
			var suffix = ArmSuffix( arm );
			sb.AppendLine( $"[sdf-bench] A/B within the run —{suffix} = {Overrides( run.Arms[arm - 1] )}" );
			sb.AppendLine( DiffHeader() );
			foreach ( var a in run.Cases.Where( c => !c.Name.EndsWith( "]" ) ) )
			{
				var b = run.Cases.First( c => c.Name == a.Name + suffix );
				sb.AppendLine( DiffRow( a.Name, a, b, ImageDiff( images, a.Image, images, b.Image, Path.Combine( images, "diff-ab" ) ) ) );
			}
		}
		return sb.ToString();
	}

	static string DiffHeader() => $"{"case",-22} {"before",7} {"after",7} {"Δ ms",7} {"Δ %",7}  {"verdict",-8}  Δprepass Δforward Δshadows Δother  image";

	/// <summary>FASTER/SLOWER only when the change is over 3% AND the repeat ranges don't overlap.</summary>
	static string DiffRow( string name, CaseResult ca, CaseResult cb, string image )
	{
		// Dollies have no control: compare their total GPU time and frame tail instead.
		double va = ca.SdfMs ?? ca.GpuMs, vb = cb.SdfMs ?? cb.GpuMs;
		double d = vb - va, pct = va > 0.01 ? d / va * 100 : 0;
		bool overlap = ca.SdfMin is double aLo && ca.SdfMax is double aHi && cb.SdfMin is double bLo && cb.SdfMax is double bHi
			&& aLo <= bHi && bLo <= aHi;
		// One repeat has no spread to judge noise by (A/B single runs wobble 4–8%), so it never gets a verdict.
		bool single = cb.SdfMs is not null && (ca.SdfMin == ca.SdfMax || cb.SdfMin == cb.SdfMax);
		// The bar scales with the baseline's own repeat spread: capped spot shadows drift ~±4% between arms, which a fixed
		// 3% rule called SLOWER for a no-op (2026-10-24). Tight cases keep the 3% floor.
		double spreadPct = ca.SdfMin is double lo && ca.SdfMax is double hi && va > 0.01 ? (hi - lo) / va * 100 : 0;
		double bar = Math.Max( 3, 2 * spreadPct );
		string verdict = single ? "1 rep" : Math.Abs( pct ) < bar || overlap ? "same" : d < 0 ? "FASTER" : "SLOWER";
		string Bd( string k ) => $"{cb.Buckets[k] - ca.Buckets[k],8:+0.00;-0.00;0.00}";
		if ( !string.IsNullOrEmpty( image ) && cb.ImageNoise is not null ) image += $"  (noise {cb.ImageNoise})";
		return $"{name,-22} {va,7:0.00} {vb,7:0.00} {d,7:+0.00;-0.00;0.00} {pct,6:+0.0;-0.0;0.0}%  {verdict,-8}  {Bd( "prepass" )} {Bd( "forward" )} {Bd( "shadows" )} {Bd( "other" )}  {image}"
			+ (cb.SdfMs is null ? $"  (gpu; p99 {ca.FrameP99:0.0}→{cb.FrameP99:0.0}, hitches {ca.Hitches33}→{cb.Hitches33})" : "")
			+ (Math.Abs( cb.CountedMiB - ca.CountedMiB ) >= 1 ? $"  MiB {ca.CountedMiB:0}→{cb.CountedMiB:0}" : "")
			+ (Math.Abs( cb.RendererUpdateMs - ca.RendererUpdateMs ) >= 0.05 ? $"  cpu {ca.RendererUpdateMs:0.00}→{cb.RendererUpdateMs:0.00}" : "");
	}

	/// <summary>Pixel diff of two reference images: the share of pixels whose largest channel moved by more than
	/// 2 and 10 (of 255), and the worst channel error. Writes an 8× amplified difference PNG into diffDir.
	/// "identical" means bit-for-bit. Returns a short summary, or "" when either image is missing.</summary>
	static string ImageDiff( string dirA, string fileA, string dirB, string fileB, string diffDir )
	{
		if ( fileA is null || fileB is null ) return "";
		var pa = Path.Combine( dirA, fileA ); var pb = Path.Combine( dirB, fileB );
		if ( !File.Exists( pa ) || !File.Exists( pb ) ) return "(no image)";
		try
		{
			using var a = Bitmap.CreateFromBytes( File.ReadAllBytes( pa ) );
			using var b = Bitmap.CreateFromBytes( File.ReadAllBytes( pb ) );
			return DiffStats( a, b, Path.Combine( diffDir, fileB ) );
		}
		catch ( Exception e ) { return $"(diff failed: {e.Message})"; }
	}

	static string DiffStats( Bitmap a, Bitmap b, string diffPath )
	{
		{
			if ( a.Width != b.Width || a.Height != b.Height ) return "(image size differs)";
			var aa = a.GetPixels32(); var bb = b.GetPixels32();
			var heat = new Color[aa.Length];
			int over2 = 0, over10 = 0, max = 0;
			for ( int i = 0; i < aa.Length; i++ )
			{
				int r = Math.Abs( aa[i].r - bb[i].r ), g = Math.Abs( aa[i].g - bb[i].g ), bl = Math.Abs( aa[i].b - bb[i].b );
				int peak = Math.Max( r, Math.Max( g, bl ) );
				max = Math.Max( max, peak );
				if ( peak > 2 ) over2++;
				if ( peak > 10 ) over10++;
				heat[i] = new Color( Math.Min( r * 8, 255 ) / 255f, Math.Min( g * 8, 255 ) / 255f, Math.Min( bl * 8, 255 ) / 255f, 1 );
			}
			if ( max == 0 ) return "identical";
			if ( diffPath is not null )
			{
				Directory.CreateDirectory( Path.GetDirectoryName( diffPath ) );
				using var diff = new Bitmap( a.Width, a.Height );
				diff.SetPixels( heat );
				File.WriteAllBytes( diffPath, diff.ToPng() );
			}
			return $">2: {over2 * 100.0 / aa.Length:0.00}%  >10: {over10 * 100.0 / aa.Length:0.00}%  max {max}";
		}
	}

	// ---------------------------------------------------------------- compare

	/// <summary>Diff two runs. Names may be partial (any unique substring of the file name). With no arguments, the
	/// newest run is compared against the newest earlier run of the same suite.</summary>
	[Mcp.McpTool( "sdf_bench_compare" )]
	public static string Compare( string baseline = null, string candidate = null )
	{
		var files = Directory.Exists( RunsDir ) ? Directory.GetFiles( RunsDir, "*.json" ).OrderBy( f => Path.GetFileName( f ) ).ToList() : new List<string>();
		string Find( string name ) => files.Where( f => Path.GetFileName( f ).Contains( name, StringComparison.OrdinalIgnoreCase ) ).LastOrDefault()
			?? throw new FileNotFoundException( $"No run matching '{name}' in {RunsDir}" );

		var bPath = candidate is null ? files.LastOrDefault() : Find( candidate );
		if ( bPath is null ) throw new FileNotFoundException( "No benchmark runs yet." );
		var b = Load( bPath );
		var aPath = baseline is not null ? Find( baseline )
			: files.Where( f => string.CompareOrdinal( Path.GetFileName( f ), Path.GetFileName( bPath ) ) < 0 && Load( f ).Suite == b.Suite ).LastOrDefault()
			?? throw new FileNotFoundException( $"No earlier {b.Suite} run to compare against." );
		var a = Load( aPath );

		var sb = new StringBuilder();
		sb.AppendLine( $"[sdf-bench] {b.Suite}: {Path.GetFileNameWithoutExtension( aPath )}  →  {Path.GetFileNameWithoutExtension( bPath )}" );
		if ( a.Version != b.Version || a.Suite != b.Suite ) sb.AppendLine( "  ⚠ different suite/version — not comparable" );
		foreach ( var key in ComparableEnv )
			if ( a.Env.GetValueOrDefault( key ) != b.Env.GetValueOrDefault( key ) )
				sb.AppendLine( $"  ⚠ {key} differs: {a.Env.GetValueOrDefault( key )} → {b.Env.GetValueOrDefault( key )}" );
		if ( a.Quick || b.Quick ) sb.AppendLine( "  ⚠ quick run involved — indicative only" );
		// Measured 2026-10-24: unchanged code read 5–10% faster in a session an hour later, while back-to-back runs
		// agreed to ±2%. A stale baseline can fake a win, so say so instead of trusting the 3% rule.
		var gap = Math.Abs( (b.Utc - a.Utc).TotalMinutes );
		if ( gap > 20 )
			sb.AppendLine( $"  ⚠ runs are {gap:0} min apart — between-session drift can reach 5–10%. Re-run the baseline right before the candidate (A/B/A), or use an in-run \"ab\"." );
		foreach ( var (run, tag) in new[] { (a, "before"), (b, "after") } )
			foreach ( var w in run.Warnings ?? new() ) sb.AppendLine( $"  ⚠ {tag}: {w}" );
		sb.AppendLine( a.SourceHash is not null && a.SourceHash == b.SourceHash
			? "  sources identical — any difference below is noise or settings"
			: "  sources differ" );
		if ( Overrides( a.Set ?? new() ) != Overrides( b.Set ?? new() ) )
			sb.AppendLine( $"  set: [{Overrides( a.Set ?? new() )}] → [{Overrides( b.Set ?? new() )}]" );

		string imagesA = Path.Combine( RunsDir, "images", Path.GetFileNameWithoutExtension( aPath ) );
		string imagesB = Path.Combine( RunsDir, "images", Path.GetFileNameWithoutExtension( bPath ) );
		string diffDir = Path.Combine( imagesB, "diff-vs-" + Path.GetFileNameWithoutExtension( aPath ) );
		sb.AppendLine( DiffHeader() );
		foreach ( var cb in b.Cases )
		{
			var ca = a.Cases.FirstOrDefault( c => c.Name == cb.Name );
			if ( ca is null ) { sb.AppendLine( $"{cb.Name,-22} (new case)" ); continue; }
			sb.AppendLine( DiffRow( cb.Name, ca, cb, ImageDiff( imagesA, ca.Image, imagesB, cb.Image, diffDir ) ) );
		}
		Log.Info( sb.ToString() );
		return sb.ToString();
	}

	static RunFile Load( string path ) => JsonSerializer.Deserialize<RunFile>( File.ReadAllText( path ), JsonOptions );

	// ---------------------------------------------------------------- helpers

	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	static double Round( double v ) => Math.Round( v, 3 );

	static double Median( IEnumerable<double> values )
	{
		var a = values.OrderBy( x => x ).ToArray();
		if ( a.Length == 0 ) return 0;
		return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) * .5;
	}

	static double Percentile( List<float> values, double q )
	{
		if ( values.Count == 0 ) return 0;
		var a = values.OrderBy( x => x ).ToArray();
		return a[Math.Clamp( (int)Math.Ceiling( q * a.Length ) - 1, 0, a.Length - 1 )];
	}

	static IEnumerable<string> SdfSources()
	{
		var code = Path.Combine( Root, "code/Sdf" );
		var shaders = Path.Combine( Root, "Assets/shaders" );
		return Directory.GetFiles( code, "*.cs", SearchOption.AllDirectories )
			.Concat( Directory.GetFiles( shaders, "*.shader", SearchOption.AllDirectories ) )
			.Concat( Directory.GetFiles( shaders, "*.hlsl", SearchOption.AllDirectories ) )
			.OrderBy( p => p, StringComparer.Ordinal );
	}

	/// <summary>One hash over every SDF C# and shader source (contents + relative path). Two runs with the same hash
	/// measured the same renderer; a hash that changes mid-run means a hotload landed during the benchmark.</summary>
	static string SourceHash()
	{
		using var sha = System.Security.Cryptography.SHA256.Create();
		foreach ( var file in SdfSources() )
		{
			var name = Encoding.UTF8.GetBytes( Path.GetRelativePath( Root, file ).Replace( '\\', '/' ) );
			sha.TransformBlock( name, 0, name.Length, null, 0 );
			var bytes = File.ReadAllBytes( file );
			sha.TransformBlock( bytes, 0, bytes.Length, null, 0 );
		}
		sha.TransformFinalBlock( Array.Empty<byte>(), 0, 0 );
		return Convert.ToHexString( sha.Hash )[..16];
	}

	/// <summary>An SDF shader whose compiled .shader_c is older than its source or a local #include it pulls in. The
	/// editor doesn't always recompile a shader when only an included .hlsl changed — then the benchmark measures
	/// the old shader. Touch the .shader (or wait for the compile) and run again.</summary>
	static string StaleShaderWarning()
	{
		var dir = Path.Combine( Root, "Assets/shaders" );
		var stale = new List<string>();
		foreach ( var shader in Directory.GetFiles( dir, "sdf_*.shader" ) )
		{
			var compiled = shader + "_c";
			if ( !File.Exists( compiled ) ) continue;
			var newest = File.GetLastWriteTimeUtc( shader );
			var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			var queue = new Queue<string>( new[] { shader } );
			while ( queue.Count > 0 )
			{
				var file = queue.Dequeue();
				foreach ( var line in File.ReadLines( file ) )
				{
					var m = System.Text.RegularExpressions.Regex.Match( line, "^\\s*#include\\s+\"([^\"]+)\"" );
					if ( !m.Success ) continue;
					var inc = Path.Combine( dir, m.Groups[1].Value );
					if ( !File.Exists( inc ) || !seen.Add( inc ) ) continue; // engine includes live elsewhere
					if ( File.GetLastWriteTimeUtc( inc ) > newest ) newest = File.GetLastWriteTimeUtc( inc );
					queue.Enqueue( inc );
				}
			}
			if ( File.GetLastWriteTimeUtc( compiled ) < newest ) stale.Add( Path.GetFileName( shader ) );
		}
		return stale.Count == 0 ? null : $"Compiled shader may be stale (source newer than .shader_c): {string.Join( ", ", stale )}";
	}

	static string Sanitise( string s ) => new( s.Select( ch => char.IsLetterOrDigit( ch ) || ch is '-' or '_' or '.' ? ch : '-' ).ToArray() );

	static (string Commit, int Dirty) GitState()
	{
		try
		{
			string Git( string args )
			{
				using var p = Process.Start( new ProcessStartInfo( "git", args ) { WorkingDirectory = Root, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } );
				var output = p.StandardOutput.ReadToEnd();
				p.WaitForExit( 5000 );
				return output.Trim();
			}
			var commit = Git( "rev-parse --short HEAD" );
			var dirty = Git( "status --porcelain -- code Editor Assets/shaders" ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Length;
			return (string.IsNullOrEmpty( commit ) ? "nogit" : commit, dirty);
		}
		catch { return ("nogit", 0); }
	}
}
