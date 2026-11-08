using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Mimiclay.Sim;

/// <summary>What this machine's SimWorld has been doing since the last reset — <c>mimi_sim_report</c> prints and resets.</summary>
public static class SimStats
{
	public sealed class Series
	{
		readonly List<float> _v = new();
		public void Add( float x ) => _v.Add( x );
		public void Clear() => _v.Clear();
		public int Count => _v.Count;
		public string Text( string unit )
		{
			if ( _v.Count == 0 ) return "n/a";
			var s = _v.OrderBy( x => x ).ToList();
			float p( float q ) => s[Math.Clamp( (int)(q * (s.Count - 1)), 0, s.Count - 1 )];
			return $"mean {s.Average():0.00}{unit} p50 {p( 0.5f ):0.00} p95 {p( 0.95f ):0.00} max {s[^1]:0.00} (n {s.Count})";
		}
	}

	public static int Steps, Snapshots, SnapshotsStale, Skipped, Replays, Resyncs, ClockFaster, ClockSlower, DroppedTicks;
	public static int HostInputOnTime, HostInputMissing, RestSnaps, Reshapes, FullStatesSent, FullStatesAsked, Releases, Reanchors, RemoteNudges, PushesPredicted, HoldsPredicted, HoldsReverted, SourceSwitches;
	public static long SnapshotBytes, ReliableBytes, InputBytes;
	public static readonly Series Mispredict = new(), MispredictMine = new();
	public static readonly Series CorrectionMine = new(), CorrectionOthers = new(), CorrectionLoose = new();
	public static readonly Series ReplayMs = new(), ReplayTicks = new(), Lead = new(), Slack = new(), Rtt = new(), SnapshotBodies = new(), StepMs = new();
	public static readonly List<string> BigCorrections = new();
	static RealTimeSince _since;

	public static void Reset()
	{
		Steps = Snapshots = SnapshotsStale = Skipped = Replays = Resyncs = ClockFaster = ClockSlower = DroppedTicks = 0;
		HostInputOnTime = HostInputMissing = RestSnaps = Reshapes = FullStatesSent = FullStatesAsked = Releases = Reanchors = RemoteNudges = PushesPredicted = HoldsPredicted = HoldsReverted = SourceSwitches = 0;
		SnapshotBytes = ReliableBytes = InputBytes = 0;
		foreach ( var s in new[] { Mispredict, MispredictMine, CorrectionMine, CorrectionOthers, CorrectionLoose, ReplayMs, ReplayTicks, Lead, Slack, Rtt, SnapshotBodies, StepMs } )
			s.Clear();
		_since = 0f;
		BigCorrections.Clear();
	}

	public static string Report()
	{
		var sim = SimWorld.Current;
		float secs = MathF.Max( (float)_since, 0.001f );
		var sb = new StringBuilder();
		string role = sim is null || !sim.Active ? "inactive" : sim.IsAuthority ? "HOST" : "CLIENT";
		int awake = sim?.Bodies.Count( b => b.Phys.IsValid() && !b.Phys.Sleeping ) ?? 0;
		sb.AppendLine( $"sim ({role}) over {secs:0.0}s: {sim?.Bodies.Count ?? 0} bodies ({awake} awake), tick {sim?.Tick}, fakelag {SimWorld.FakeLagMs} ms, engine ping {Connection.Host?.Ping ?? 0f:0} ms, measured rtt {sim?.RttMs ?? 0f:0} ms ({Rtt.Text( "ms" )})" );
		sb.AppendLine( $"  steps {Steps} ({Steps / secs:0}/s), dropped ticks {DroppedTicks}, reshapes {Reshapes}, releases {Releases}, re-anchors {Reanchors}, pushes predicted {PushesPredicted}, holds predicted {HoldsPredicted} (reverted {HoldsReverted}), source switches {SourceSwitches}; step {StepMs.Text( "ms" )}" );
		sb.AppendLine( $"  bandwidth: snapshots {SnapshotBytes / secs / 1024f:0.0} KB/s, reliable {ReliableBytes / 1024f:0.0} KB total, inputs {InputBytes / secs / 1024f:0.0} KB/s" );
		if ( sim is not null && sim.IsAuthority )
		{
			sb.AppendLine( $"  snapshot bodies {SnapshotBodies.Text( "" )}" );
			sb.Append( $"  inputs on time {HostInputOnTime}, missing {HostInputMissing}; full states sent {FullStatesSent}" );
			return sb.ToString();
		}
		sb.AppendLine( $"  snapshots {Snapshots} (stale {SnapshotsStale}), replays {Replays}, skipped {Skipped}, resyncs {Resyncs}, rest snaps {RestSnaps}, remote nudges {RemoteNudges}, clock +{ClockFaster}/-{ClockSlower}, asked for all {FullStatesAsked}" );
		sb.AppendLine( $"  lead ticks    {Lead.Text( "" )}" );
		sb.AppendLine( $"  slack ticks   {Slack.Text( "" )}" );
		sb.AppendLine( $"  replay        {ReplayMs.Text( "ms" )}; ticks {ReplayTicks.Text( "" )}" );
		sb.AppendLine( $"  mispredict    all {Mispredict.Text( "u" )}" );
		sb.AppendLine( $"                mine {MispredictMine.Text( "u" )}" );
		sb.AppendLine( $"  correction    mine {CorrectionMine.Text( "u" )}" );
		sb.AppendLine( $"                others' {CorrectionOthers.Text( "u" )}" );
		sb.Append( $"                loose {CorrectionLoose.Text( "u" )}" );
		foreach ( var c in BigCorrections )
			sb.Append( "\n    big: " + c );
		return sb.ToString();
	}

	[ConCmd( "mimi_sim_report" )]
	public static void ReportCmd()
	{
		Log.Info( "\n" + Report() );
		Reset();
	}
}
