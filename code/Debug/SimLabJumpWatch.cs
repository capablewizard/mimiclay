using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// SimLab's JUMP WATCH (2026-11-07; user: "a ball going kind of crazy at the end … let's keep the jump recorders for
/// everything so we can catch it in the future"). On every machine taking part in a lab run, every frame, every prop pawn
/// (players' props, loose props, bots) and every hunter: a frame whose DRAWN motion is faster than <see cref="SpeedLimit"/>
/// or turns more than <see cref="TurnLimit"/> is recorded with what was going on. It runs the whole time the lab is up —
/// between scenarios too, and whatever the sim is set to — not just inside a scenario's recording window (the scenario's own
/// "dv / blips" metric only watches its pair, in its window).
///
/// The lab's own teleports (a scenario's Setup putting the cast on its markers) aren't news: lab.sh tells each machine the
/// phase (<see cref="SimLab.NotePhase"/>), and jumps during "setup" are counted apart. Each <see cref="SimLab.Run"/> line
/// reports the rest since the previous one (<see cref="TakeReport"/>) — including any that happened between scenarios.
/// Off until the first lab call on this machine (never in a normal session).
/// </summary>
public sealed class SimLabJumpWatch : GameObjectSystem
{
	/// <summary>Drawn speed (u/s) over which a frame counts as a jump — the lab's "blip" line.</summary>
	public const float SpeedLimit = 1500f;
	/// <summary>Turn in one frame (degrees) over which a frame counts as a jump (a body spinning wildly, or snapping round).</summary>
	public const float TurnLimit = 60f;
	/// <summary>How far under the lab floor (top at z = 0) a prop's drawn centre (a hunter's feet) may go before it counts (through the floor).</summary>
	public const float UnderLimit = 6f;

	/// <summary>On for this machine once the lab has talked to it (SimLab.NotePhase / Run / Setup).</summary>
	public static bool On;
	/// <summary>What the lab is doing on this machine right now: "setup &lt;scen&gt;", "run &lt;scen&gt;", "idle after &lt;scen&gt;".</summary>
	public static string Phase = "idle";
	static double _phaseSince;

	public readonly record struct Jump( double T, string Phase, string Who, float Speed, float Turn, float Under, Vector3 At, string State );
	static readonly List<Jump> _jumps = new();
	static int _setupJumps;
	const int MaxKept = 200;

	readonly Dictionary<Guid, (Vector3 P, Rotation R)> _last = new();

	public SimLabJumpWatch( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 1000, Tick, "SimLabJumpWatch" );
		if ( scene is null || scene.IsEditor )
			return;
		// A fresh game scene: forget the last one's (the editor keeps statics across play sessions — a reopened lab's first
		// run line reported the previous session's jumps). The lab switches it back on with its next call.
		_jumps.Clear();
		_setupJumps = 0;
		On = false;
	}

	public static void SetPhase( string phase )
	{
		Phase = phase;
		_phaseSince = Time.NowDouble;
		On = true;
	}

	void Tick()
	{
		if ( !On || Scene is null || Scene.IsEditor || Scene != Game.ActiveScene )
			return;
		float dt = Time.Delta;
		if ( dt <= 0f )
			return;

		var seen = new HashSet<Guid>();
		foreach ( var h in Scene.GetAllComponents<HiderController>() )
			Check( h.GameObject, h, dt, seen );
		foreach ( var h in Scene.GetAllComponents<HunterController>() )
			Check( h.GameObject, null, dt, seen );
		if ( _last.Count > seen.Count )
			foreach ( var k in _last.Keys.Where( k => !seen.Contains( k ) ).ToList() )
				_last.Remove( k );
	}

	void Check( GameObject go, HiderController h, float dt, HashSet<Guid> seen )
	{
		if ( !go.IsValid() )
			return;
		seen.Add( go.Id );
		// A prop's clay is the disguise child (it tumbles while the root stays upright) — watch what's drawn.
		var drawn = h?.DisguiseSculpture is { } clay && clay.IsValid() ? clay.GameObject : go;
		var p = drawn.WorldPosition;
		var r = drawn.WorldRotation;
		// Through the floor: a prop's CENTRE (its physics body's box centre — the collider follows what's drawn) or a hunter's
		// feet under the lab floor. Not the bottom: both GameObject.GetBounds and the body's box reach 7–40u below a football
		// resting on the floor (loose boxes round a rotated hull), while their centre is the ball's.
		var pb = h is not null && go.Components.Get<Rigidbody>() is { } rb && rb.IsValid() ? rb.PhysicsBody : null;
		float under = h is null ? -go.WorldPosition.z : pb is not null && pb.IsValid() ? -pb.GetBounds().Center.z : 0f;
		if ( _last.TryGetValue( go.Id, out var was ) )
		{
			float speed = p.Distance( was.P ) / dt;
			float turn = dt < 0.05f ? was.R.Distance( r ) : 0f; // (a long hitch legitimately turns a spinning body a lot)
			if ( speed > SpeedLimit || turn > TurnLimit || under > UnderLimit )
				Note( go, h, speed, turn, under, p );
		}
		_last[go.Id] = (p, r);
	}

	static void Note( GameObject go, HiderController h, float speed, float turn, float under, Vector3 at )
	{
		if ( Phase.StartsWith( "setup" ) )
		{
			_setupJumps++;
			return;
		}
		if ( _jumps.Count >= MaxKept )
			return;
		string state = "hunter";
		if ( h is not null )
		{
			state = h.SimClimbing ? "climbing" : h.SimHeld ? "sim-held" : h.SimDown ? "down" : h.SimLoose ? "sim-loose" : h.SimWalking ? "sim-walk"
				: PropClaims.IsHeld( h ) ? "held (scene)" : PropClaims.IsLoose( h ) ? "loose (scene)" : h.IsGrabbedObject ? "grabbed object" : "scene walk";
			state += h.IsProxy ? ", proxy" : "";
		}
		_jumps.Add( new Jump( Time.NowDouble - _phaseSince, Phase, Short( go.Name ), speed, turn, under, at, state ) );
	}

	static string Short( string name ) => name.Replace( "Creative Prop ", "" ).Replace( "Creative Hunter ", "hunter " ).Replace( "SimLab.", "" );

	/// <summary>The jumps since the last report, as " | jumps N (setup M)" plus up to five detail lines, then forgotten.</summary>
	public static string TakeReport()
	{
		var sb = new System.Text.StringBuilder();
		sb.Append( $" | jumps {_jumps.Count} (setup {_setupJumps})" );
		// Grouped: one line per body and phase (a crazy ball is many frames in a row), its worst frame and frame count.
		foreach ( var g in _jumps.GroupBy( j => (j.Who, j.Phase) ).Take( 5 ) )
		{
			var worst = g.MaxBy( j => j.Speed + j.Turn * 10f + MathF.Max( j.Under, 0f ) * 100f );
			sb.Append( $"\n    jump: {g.Key.Who} in '{g.Key.Phase}' {g.Count()} frames from {g.Min( j => j.T ):0.00}s, worst {worst.Speed:0} u/s {worst.Turn:0}°{(g.Max( j => j.Under ) > UnderLimit ? $" under the floor by up to {g.Max( j => j.Under ):0}u" : "")} at {worst.At.x:0},{worst.At.y:0},{worst.At.z:0} ({worst.State})" );
		}
		_jumps.Clear();
		_setupJumps = 0;
		return sb.ToString();
	}
}
