using System;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Diagnostics for the hunter-pawn placement cost found in the 2026-10-05 playtest (frames fine until anyone
/// moved: every head placement rebuilt the head's hit collider as a shape on the pawn's body — see
/// <c>HunterController.EnsureHeadBody</c>). Console commands, all runtime-only:
///
///   mimi_dbg_pawnperf 1|0   — once a second, log ComposePawn time per pawn + frame time + where the head's
///                             shapes live (own head body vs the pawn root body).
///   mimi_dbg_wander 1|0     — EVERY machine's owned hunter spins its aim and walks a circle, so remote
///                             hunters move without anyone at the keyboard (broadcast).
///   mimi_dbg_headcol 1|0    — force OUR OWN head collider on (normally proxies only) so a single instance
///                             pays the same cost a proxy would.
///   mimi_dbg_headbody 1|0   — enable/disable the Head's own Rigidbody on every hunter pawn here: 0 puts the
///                             collider back on the pawn root's body (the OLD binding) for an A/B.
///   mimi_dbg_carve [radius] — fake a shot crater on our own face (append a Damage brush + Rebuild) and report
///                             whether the collider rebuilt and how long the commit took.
/// </summary>
public sealed class PawnPerfProbe
{
	public static bool Enabled { get; private set; }
	public static bool Wander { get; private set; }
	public static bool ForceOwnHeadCollider { get; private set; }

	public const float WanderYawSpeed = 70f;   // deg/s
	public const float WanderSpeed = 140f;     // units/s

	static long _ticks, _maxTicks;
	static int _samples, _frames;
	static float _lastFrameTime = -1f, _frameSum, _nextLog;

	[ConCmd( "mimi_dbg_pawnperf" )]
	public static void SetEnabled( int on )
	{
		Enabled = on != 0;
		_ticks = _maxTicks = 0; _samples = _frames = 0; _frameSum = 0f; _lastFrameTime = -1f;
		_nextLog = RealTime.Now + 1f;
		Log.Info( $"[pawnperf] stats {(Enabled ? "ON" : "OFF")}" );
	}

	[ConCmd( "mimi_dbg_wander" )]
	public static void WanderCmd( int on ) => BroadcastWander( on != 0 );

	[Rpc.Broadcast]
	static void BroadcastWander( bool on )
	{
		Wander = on;
		Log.Info( $"[pawnperf] wander {(on ? "ON" : "OFF")} on this machine" );
	}

	[ConCmd( "mimi_dbg_headcol" )]
	public static void HeadColCmd( int on )
	{
		ForceOwnHeadCollider = on != 0;
		Log.Info( $"[pawnperf] own head collider forced {(ForceOwnHeadCollider ? "ON" : "OFF (proxies only)")}" );
	}

	[ConCmd( "mimi_dbg_headbody" )]
	public static void HeadBodyCmd( int on )
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		int n = 0;
		foreach ( var h in scene.GetAllComponents<HunterController>() )
		{
			h.SetHeadBodyEnabled( on != 0 );
			n++;
		}

		Log.Info( $"[pawnperf] head Rigidbody {(on != 0 ? "ENABLED (new binding)" : "DISABLED (old binding: shapes on the pawn root body)")} on {n} hunter(s)" );
	}

	[ConCmd( "mimi_dbg_headpos" )]
	public static void HeadPosCmd()
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		foreach ( var h in scene.GetAllComponents<HunterController>() )
		{
			var (dist, deg) = h.HeadBodyTrackingError;
			Log.Info( $"[pawnperf] {(h.Owned ? "own" : h.IsProxy ? "proxy" : "bot")} {h.GameObject.Name}: head body is {dist:0.00} units / {deg:0.0}° from the head object (shapes head={h.HeadBodyShapeCount} root={h.RootBodyShapeCount})" );
		}
	}

	[ConCmd( "mimi_dbg_carve" )]
	public static void CarveCmd( float radius = 6f )
	{
		var scene = Game.ActiveScene;
		var hunter = scene?.GetAllComponents<HunterController>().FirstOrDefault( h => h.Owned );
		var face = hunter?.Face;
		if ( !face.IsValid() || face.Brushes is null )
		{
			Log.Warning( "[pawnperf] no owned hunter with a face here" );
			return;
		}

		var collider = face.GameObject.Components.Get<SdfCollider>( includeDisabled: true );
		int buildsBefore = SdfCollider.BuildCount;

		// Mirrors HunterController.BroadcastCarve's brush: a damage-tail subtract that heals.
		var first = face.Brushes.FirstOrDefault( b => !b.Damage );
		face.Brushes.Add( new SdfBrush
		{
			Shape = SdfShape.Sphere,
			Operation = SdfOperation.Subtract,
			Position = first is not null ? first.Position + Vector3.Forward * (first.Size.x * 0.5f) : Vector3.Zero,
			Size = radius,
			Damage = true,
			Shrinks = true,
			ShrinkDelay = 1f,
			ShrinkDuration = 1f,
		} );

		var sw = System.Diagnostics.Stopwatch.StartNew();
		face.Rebuild();
		sw.Stop();

		int builds = SdfCollider.BuildCount - buildsBefore;
		Log.Info( $"[pawnperf] fake crater: brushes={face.Brushes.Count} (authored {face.AuthoredBrushCount}) commit took {sw.Elapsed.TotalMilliseconds:0.00}ms | collider builds triggered: {builds} (collider {(collider.IsValid() ? (collider.Enabled ? "enabled" : "disabled") : "none")})" );
	}

	// ── Spike breakdown ──────────────────────────────────────────────────────────────────────────────
	// A ComposePawn that takes longer than SpikeThresholdMs logs where the time went: pivot / head / gun /
	// renderer refresh, with the slowest renderer's own section timings (NoteRefresh + SetRefreshDetail).
	public static float SpikeThresholdMs = 4f;
	static float _nextSpikeLog;
	static string _slowestRefresh = "";
	static long _slowestRefreshTicks;
	static string _refreshDetail = "";

	/// <summary>SdfRaymarchRenderer.Refresh reports its section timings here (only while Enabled).</summary>
	public static void SetRefreshDetail( string detail ) => _refreshDetail = detail;

	public static void NoteRefresh( SdfRaymarchRenderer r, long ticks )
	{
		if ( ticks <= _slowestRefreshTicks ) return;
		_slowestRefreshTicks = ticks;
		_slowestRefresh = $"{r.GameObject.Name} {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00}ms [{_refreshDetail}]";
	}

	// (GC counters — CollectionCount / GetAllocatedBytes / GetTotalPauseDuration — are outside the s&box
	// whitelist, so a stall's nature has to be inferred from WHERE it lands: a 7ms "pack" of two brushes is
	// not CPU work, it's a pause or a resource-creation sync paying for allocations.)
	public static void RecordBreakdown( HunterController pawn, long total, long pivot, long eyes, long gun, long refresh )
	{
		double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		if ( total * ms >= SpikeThresholdMs && RealTime.Now >= _nextSpikeLog )
		{
			_nextSpikeLog = RealTime.Now + 0.33f;
			Log.Info( $"[pawnperf] SPIKE {total * ms:0.00}ms on {(pawn.Owned ? "own" : "proxy")} {pawn.GameObject.Name}: pivot={pivot * ms:0.00} head={eyes * ms:0.00} gun={gun * ms:0.00} refresh={refresh * ms:0.00} | slowest renderer: {_slowestRefresh} | frame {Time.Delta * 1000f:0.0}ms" );
		}

		_slowestRefreshTicks = 0;
		_slowestRefresh = "";
	}

	/// <summary>Called by HunterController.ComposePawn with the Stopwatch ticks it spent, every pawn, every frame.</summary>
	public static void Record( HunterController pawn, long ticks )
	{
		if ( !Enabled ) return;

		_ticks += ticks;
		_samples++;
		if ( ticks > _maxTicks ) _maxTicks = ticks;

		if ( Time.Now != _lastFrameTime )
		{
			_lastFrameTime = Time.Now;
			_frames++;
			_frameSum += Time.Delta;
		}

		if ( RealTime.Now < _nextLog )
			return;
		_nextLog = RealTime.Now + 1f;

		double msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		double avg = _samples > 0 ? _ticks * msPerTick / _samples : 0;
		double perFrame = _frames > 0 ? _ticks * msPerTick / _frames : 0;
		double frameMs = _frames > 0 ? _frameSum * 1000f / _frames : 0;
		float pawnsPerFrame = _frames > 0 ? (float)_samples / _frames : 0;

		// Where the shapes are: a summary over every hunter here.
		var scene = Game.ActiveScene;
		string shapes = "";
		if ( scene is not null )
		{
			foreach ( var h in scene.GetAllComponents<HunterController>() )
				shapes += $" [{(h.Owned ? "own" : h.IsProxy ? "proxy" : "bot")} head={h.HeadBodyShapeCount} root={h.RootBodyShapeCount}]";
		}

		Log.Info( $"[pawnperf] pawns/frame={pawnsPerFrame:0.0} compose avg={avg:0.000}ms max={_maxTicks * msPerTick:0.00}ms total/frame={perFrame:0.000}ms | frame {frameMs:0.00}ms ({(frameMs > 0 ? 1000 / frameMs : 0):0} fps) | shapes:{shapes}" );

		_ticks = _maxTicks = 0; _samples = _frames = 0; _frameSum = 0f;
	}
}
