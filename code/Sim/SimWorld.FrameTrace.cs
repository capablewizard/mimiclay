using System.Collections.Generic;
using System.Text;

namespace Mimiclay.Sim;

// ── Frame trace (debug): every frame, for the bodies named, what is DRAWN and why ───────────────────────────────────
// Rows (space-separated "k=v" fields after a one-letter kind):
//   F  a frame on this machine: the drawn pose, the sim copy's pose, the fade offset, the drawn source, the copy's speed
//   N  a NudgeRemote restore of a traced body (dp = how far the restore moved the copy; the fold hides it)
//   R  a rollback replay's correction of a traced body (len = the visual jump folded into the fade)
//   S  a drawing-source switch (host poses ↔ our copy)
//   H  the HOST's own per-tick truth for the body (on the host only)
// Enable with TraceStart(keys) on the machine to watch (an observer client: what it sees of two others; the host: the
// truth to compare against by tick), then TraceDump(path). Analyse offline: jerks in F rows' drawn speed, attributed to
// the N/R/S event just before them; the drawn pose at tick T against the host's H row for T.
public sealed partial class SimWorld
{
	public static readonly HashSet<System.Guid> TraceKeys = new();
	public static readonly List<string> FrameTrace = new();
	public static int FrameTraceMax = 40000;

	public static void TraceStart( params System.Guid[] keys )
	{
		TraceKeys.Clear();
		foreach ( var k in keys ) TraceKeys.Add( k );
		FrameTrace.Clear();
	}

	public static void TraceStop() => TraceKeys.Clear();

	/// <summary>Writes the rows to a file in the game's data folder (FileSystem.Data — plain file IO is whitelisted out) and
	/// returns a one-line summary with the full path.</summary>
	public static string TraceDump( string name )
	{
		var sb = new StringBuilder();
		foreach ( var r in FrameTrace ) sb.Append( r ).Append( '\n' );
		FileSystem.Data.WriteAllText( name, sb.ToString() );
		return $"{FrameTrace.Count} rows → {FileSystem.Data.GetFullPath( name )}";
	}

	static bool Traced( SimBody b ) => TraceKeys.Count > 0 && TraceKeys.Contains( b.Spec.Source );
	static string Short( SimBody b ) => b.Spec.Source.ToString().Substring( 0, 4 );
	static string V( Vector3 v ) => $"{v.x:0.0},{v.y:0.0},{v.z:0.0}";

	static void TraceRow( string row )
	{
		if ( FrameTrace.Count < FrameTraceMax )
			FrameTrace.Add( row );
	}

	void TraceFrame()
	{
		if ( TraceKeys.Count == 0 )
			return;
		foreach ( var b in _bodies )
		{
			if ( !Traced( b ) || !b.Phys.IsValid() )
				continue;
			var drawn = VisualPose( b );
			var auth = b.Authority.Count > 0 ? b.Authority[^1] : default;
			TraceRow( $"F t={Time.NowDouble:0.0000} tick={Tick} a={_alpha:0.00} key={Short( b )} drawn={V( drawn.Position )} sim={V( b.Phys.Position )} off={b.PosOffset.Length:0.00} src={(b.ShownFromAuth ? "host" : "copy")} v={b.Phys.Velocity.Length:0} authN={b.Authority.Count} authTick={auth.tick} authP={V( auth.p )} drv={b.Spec.Driver} touched={(RecentlyTouched( b ) ? 1 : 0)}" );
		}
	}

	void TraceHostTick()
	{
		if ( TraceKeys.Count == 0 || !IsAuthority )
			return;
		foreach ( var b in _bodies )
			if ( Traced( b ) && b.Phys.IsValid() )
				TraceRow( $"H t={Time.NowDouble:0.0000} tick={Tick} key={Short( b )} pos={V( b.Phys.Position )} v={b.Phys.Velocity.Length:0} wish={b.Ctrl.Wish.Length:0} drv={b.Spec.Driver} grounded={(b.Ctrl.Grounded ? 1 : 0)}" );
	}

	void TraceNudge( SimBody b, int s, Vector3 before, Vector3 after, Vector3 hostV ) { if ( Traced( b ) ) TraceRow( $"N t={Time.NowDouble:0.0000} tick={Tick} s={s} key={Short( b )} dp={(before - after).Length:0.00} before={V( before )} after={V( after )} hostV={hostV.Length:0}" ); }
	void TraceReplay( SimBody b, int s, float len, Vector3 predictedAtS, Vector3 hostAtS ) { if ( Traced( b ) ) TraceRow( $"R t={Time.NowDouble:0.0000} tick={Tick} s={s} key={Short( b )} len={len:0.00} predictedAtS={V( predictedAtS )} hostAtS={V( hostAtS )}" ); }
	void TraceSwitch( SimBody b, bool toHost, float dp ) { if ( Traced( b ) ) TraceRow( $"S t={Time.NowDouble:0.0000} tick={Tick} key={Short( b )} to={(toHost ? "host" : "copy")} dp={dp:0.00}" ); }
}

