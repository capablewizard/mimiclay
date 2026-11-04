using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// THROWAWAY PROTOTYPE (2026-11-04) — can client-side prediction + rollback work for prop physics in s&amp;box?
///
/// Headless: two private <see cref="PhysicsWorld"/>s (transient worlds are the only ones we may step by hand) stand in
/// for the HOST (authoritative) and one CLIENT (predicting). Network latency is a delay queue counted in ticks. The
/// client runs ahead of the host by one-way latency + 1 tick, so its inputs reach the host just in time; the host sends
/// a state snapshot every few ticks; on each snapshot the client restores every body to it and replays its own stored
/// inputs up to the present.
///
/// Bodies: 0 = the client's prop (driven by its input), 1 = a REMOTE player's prop (input known only to the host — the
/// client guesses it from the last snapshot's velocity), 2.. = a stack of loose boxes both of them shove.
///
/// It reports the misprediction at each snapshot tick, the CORRECTION it causes at the present (the pop a player would
/// see before any smoothing), and the replay cost. Also a determinism pass (same world twice) and a restore-and-replay
/// pass (rollback with perfect information), which isolate what the engine itself contributes to corrections.
///
/// Run: <c>mimi_rollback_lab</c> (all scenarios) or <see cref="Run"/> from run_csharp.
/// </summary>
public static class RollbackLab
{
	const float Dt = 1f / 50f; // project TickRate 50

	public sealed class Options
	{
		public int RttMs = 100;
		public int SnapshotEvery = 2;   // host ticks between snapshots (2 = 25 Hz; project UpdateRate is 30)
		public int Ticks = 600;
		public bool Remote = true;      // a second player, unknown to the client, contests the stack
		public int Boxes = 3;
		public int Seed = 1;
		public float BoxMass = 15f;
		public float PlayerMass = 60f;
		public bool LatestSnapshotOnly = true;
	}

	struct BodyState
	{
		public Vector3 P; public Rotation R; public Vector3 V; public Vector3 W; public bool S;
	}

	// ── A lab world ─────────────────────────────────────────────────────────────────────────────────────────

	sealed class Lab : IDisposable
	{
		public readonly PhysicsWorld World;
		public readonly List<PhysicsBody> Bodies = new();

		public Lab( Options o )
		{
			World = new PhysicsWorld();
			var scene = Game.ActiveScene?.PhysicsWorld;
			World.Gravity = scene is not null && scene.IsValid() ? scene.Gravity : new Vector3( 0, 0, -800 );
			if ( scene is not null && scene.IsValid() )
				World.SubSteps = scene.SubSteps;

			// Floor: a static box on the world body, top at z = 0.
			World.Body.AddBoxShape( new Vector3( 0, 0, -10 ), Rotation.Identity, new Vector3( 4000, 4000, 10 ) );

			Bodies.Add( Player( new Vector3( -120, 0, 16.5f ), o.PlayerMass ) );
			Bodies.Add( Player( new Vector3( 420, 20, 16.5f ), o.PlayerMass ) );
			// Stacks of three, in a grid of columns from x = 150 (the scripted walks head there).
			for ( int i = 0; i < o.Boxes; i++ )
			{
				int col = i / 3, level = i % 3;
				var at = new Vector3( 150 + (col % 3) * 26f, (col / 3) * 26f - (col / 9) * 10f + (level % 2) * 3f, 12.25f + level * 24.5f );
				Bodies.Add( Box( at, o.BoxMass ) );
			}

			if ( !o.Remote )
				Bodies[1].Position = new Vector3( 0, 3000, 16.5f ); // parked far away
		}

		PhysicsBody Player( Vector3 at, float mass )
		{
			var b = new PhysicsBody( World ) { BodyType = PhysicsBodyType.Dynamic };
			b.AddBoxShape( Vector3.Zero, Rotation.Identity, new Vector3( 16, 16, 16 ) );
			b.Mass = mass;
			b.Locking = new PhysicsLock { Pitch = true, Roll = true };
			b.Position = at;
			return b;
		}

		PhysicsBody Box( Vector3 at, float mass )
		{
			var b = new PhysicsBody( World ) { BodyType = PhysicsBodyType.Dynamic };
			b.AddBoxShape( Vector3.Zero, Rotation.Identity, new Vector3( 12, 12, 12 ) );
			b.Mass = mass;
			b.Position = at;
			return b;
		}

		public BodyState[] Capture()
		{
			var s = new BodyState[Bodies.Count];
			for ( int i = 0; i < s.Length; i++ )
			{
				var b = Bodies[i];
				s[i] = new BodyState { P = b.Position, R = b.Rotation, V = b.Velocity, W = b.AngularVelocity, S = b.Sleeping };
			}
			return s;
		}

		public void Restore( BodyState[] s )
		{
			for ( int i = 0; i < s.Length; i++ )
			{
				var b = Bodies[i];
				b.Transform = new Transform( s[i].P, s[i].R );
				b.Velocity = s[i].V;
				b.AngularVelocity = s[i].W;
				b.Sleeping = s[i].S;
			}
		}

		public void Tick( Vector3 localWish, Vector3 remoteWish )
		{
			Drive( Bodies[0], localWish );
			Drive( Bodies[1], remoteWish );
			World.Step( Dt );
		}

		public void Dispose()
		{
			if ( World.IsValid() )
				World.Delete();
		}
	}

	// The walk, cut down: chase the wish horizontally with a capped acceleration (a force, as HiderController.Walk).
	static void Drive( PhysicsBody b, Vector3 wish )
	{
		var v = b.Velocity;
		var flat = v.WithZ( 0f );
		if ( wish.IsNearZeroLength && flat.Length < 0.5f )
			return; // idle: let it sleep
		if ( b.Sleeping )
			b.Sleeping = false;
		var dv = ((wish - flat) * (1f - MathF.Exp( -12f * Dt ))).ClampLength( 3000f * Dt );
		b.Velocity = (flat + dv).WithZ( v.z );
	}

	// ── Input scripts (pure functions of the tick, so any tick can be replayed) ───────────────────────────────

	static uint Hash( int seed, int a, int b )
	{
		uint h = (uint)seed * 0x9E3779B1u ^ (uint)a * 0x85EBCA77u ^ (uint)b * 0xC2B2AE3Du;
		h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
		return h;
	}

	static Vector3 Segment( int seed, int who, int seg, float speed )
	{
		uint h = Hash( seed, who, seg );
		if ( h % 5 == 0 )
			return Vector3.Zero; // stand still a while
		float yaw = (h >> 8) % 360;
		// Mostly toward the stack (x = 150) so contacts keep happening.
		return Rotation.FromYaw( yaw ).Forward * speed;
	}

	static Vector3 LocalWish( Options o, int t, BodyState[] at = null )
	{
		if ( t < 100 ) return new Vector3( 220, 0, 0 );      // walk into the stack
		if ( t < 140 ) return Vector3.Zero;
		return Segment( o.Seed, 0, t / 40, 220 );
	}

	static Vector3 RemoteWish( Options o, int t )
	{
		if ( !o.Remote || t < 60 ) return Vector3.Zero;
		if ( t < 150 ) return new Vector3( -200, 0, 0 );     // into the stack from the other side
		return Segment( o.Seed, 1, t / 25, 200 );
	}

	// ── Metrics ─────────────────────────────────────────────────────────────────────────────────────────────

	sealed class Series
	{
		readonly List<float> _v = new();
		public void Add( float x ) => _v.Add( x );
		public int Count => _v.Count;
		public string Text( string unit )
		{
			if ( _v.Count == 0 ) return "n/a";
			var s = _v.OrderBy( x => x ).ToList();
			float p( float q ) => s[Math.Clamp( (int)(q * (s.Count - 1)), 0, s.Count - 1 )];
			return $"mean {s.Average():0.00}{unit} p95 {p( 0.95f ):0.00} max {s[^1]:0.00}  (>1: {s.Count( x => x > 1f )}, >5: {s.Count( x => x > 5f )}, n {s.Count})";
		}
	}

	static float PosErr( BodyState a, BodyState b ) => a.P.Distance( b.P );
	static float RotErr( BodyState a, BodyState b ) => a.R.Distance( b.R );

	// ── Scenarios ───────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Same world twice, same inputs: does the engine step identically?</summary>
	static string Determinism( Options o )
	{
		using var a = new Lab( o );
		using var b = new Lab( o );
		float maxP = 0, maxR = 0;
		int firstDiverge = -1;
		for ( int t = 0; t < o.Ticks; t++ )
		{
			a.Tick( LocalWish( o, t ), RemoteWish( o, t ) );
			b.Tick( LocalWish( o, t ), RemoteWish( o, t ) );
			var sa = a.Capture(); var sb = b.Capture();
			for ( int i = 0; i < sa.Length; i++ )
			{
				float e = PosErr( sa[i], sb[i] );
				if ( e > 0f && firstDiverge < 0 ) firstDiverge = t;
				maxP = MathF.Max( maxP, e );
				maxR = MathF.Max( maxR, RotErr( sa[i], sb[i] ) );
			}
		}
		return $"determinism (two identical worlds, {o.Ticks} ticks): max pos diff {maxP:0.0000}u, max rot diff {maxR:0.000}°, first divergence tick {firstDiverge}";
	}

	/// <summary>Rollback with PERFECT information: one world runs straight; the other, every few ticks, restores its own
	/// state from R ticks ago and replays the same inputs. Any difference is what restore loses (contact warm-start,
	/// sleep islands…) — the floor under every real correction.</summary>
	static string RestoreReplay( Options o, int back, int every )
	{
		using var a = new Lab( o );
		using var b = new Lab( o );
		var hist = new Dictionary<int, BodyState[]>();
		var err = new Series();
		int firstBad = -1;
		string firstBadText = "no divergence";
		for ( int t = 0; t < o.Ticks; t++ )
		{
			a.Tick( LocalWish( o, t ), RemoteWish( o, t ) );
			b.Tick( LocalWish( o, t ), RemoteWish( o, t ) );
			hist[t] = a.Capture(); // the STRAIGHT run's history: each rollback starts from the truth, like a snapshot

			if ( t >= back && t % every == 0 )
			{
				b.Restore( hist[t - back] );
				for ( int r = t - back + 1; r <= t; r++ )
					b.Tick( LocalWish( o, r ), RemoteWish( o, r ) );
			}
			else
				b.Restore( hist[t] ); // only rollback ticks are measured against a fresh restore

			var sa = a.Capture(); var sb = b.Capture();
			float worst = 0;
			int worstBody = -1;
			for ( int i = 0; i < sa.Length; i++ )
			{
				float e = PosErr( sa[i], sb[i] );
				if ( e > worst ) { worst = e; worstBody = i; }
			}
			if ( !(t >= back && t % every == 0) )
				continue;
			if ( worst > 0.01f && firstBad < 0 )
			{
				firstBad = t;
				firstBadText = $"first divergence tick {t}, body {worstBody}: straight {sa[worstBody].P} / replayed {sb[worstBody].P}";
			}
			err.Add( worst );
		}
		return $"restore+replay (perfect info, back {back} ticks every {every}): divergence from straight run {err.Text( "u" )}\n  {firstBadText}";
	}

	/// <summary>The real thing: host + predicting client over a fake link.</summary>
	static string Network( Options o )
	{
		int oneWay = Math.Max( 0, (int)MathF.Round( o.RttMs * 0.5f / (Dt * 1000f) ) );
		int lead = oneWay + 1;

		using var host = new Lab( o );
		using var client = new Lab( o );

		var hostInputs = new Dictionary<int, Vector3>();
		var inputsInFlight = new Queue<(int arrive, int tick, Vector3 wish)>();
		var snapsInFlight = new Queue<(int arrive, int tick, BodyState[] state)>();
		var predicted = new Dictionary<int, BodyState[]>(); // client: state after each tick
		var clientInputs = new Dictionary<int, Vector3>();
		Vector3 remoteGuess = Vector3.Zero;
		Vector3 lastHostInput = Vector3.Zero;

		var mispredictMe = new Series(); var mispredictRemote = new Series(); var mispredictBoxes = new Series();
		var popMe = new Series(); var popRemote = new Series(); var popBoxes = new Series();
		var replayMs = new Series(); var replayTicks = new Series();
		var sw = new Stopwatch();

		for ( int g = -lead; g < o.Ticks; g++ )
		{
			// HOST simulates tick g.
			if ( g >= 0 )
			{
				while ( inputsInFlight.Count > 0 && inputsInFlight.Peek().arrive <= g )
				{
					var m = inputsInFlight.Dequeue();
					hostInputs[m.tick] = m.wish;
				}
				if ( hostInputs.TryGetValue( g, out var w ) )
					lastHostInput = w;
				host.Tick( lastHostInput, RemoteWish( o, g ) );
				if ( g % o.SnapshotEvery == 0 )
					snapsInFlight.Enqueue( (g + oneWay, g, host.Capture()) );
			}

			// CLIENT simulates tick c = g + lead, after reconciling with whatever snapshot just arrived.
			int c = g + lead;
			(int arrive, int tick, BodyState[] state)? snap = null;
			while ( snapsInFlight.Count > 0 && snapsInFlight.Peek().arrive <= g )
			{
				var s = snapsInFlight.Dequeue();
				if ( !o.LatestSnapshotOnly ) Reconcile( s.tick, s.state );
				snap = s;
			}
			if ( o.LatestSnapshotOnly && snap is { } latest )
				Reconcile( latest.tick, latest.state );

			var wish = LocalWish( o, c );
			clientInputs[c] = wish;
			inputsInFlight.Enqueue( (g + oneWay, c, wish) );
			client.Tick( wish, remoteGuess );
			predicted[c] = client.Capture();

			void Reconcile( int s, BodyState[] auth )
			{
				if ( predicted.TryGetValue( s, out var guess ) )
				{
					mispredictMe.Add( PosErr( guess[0], auth[0] ) );
					if ( o.Remote ) mispredictRemote.Add( PosErr( guess[1], auth[1] ) );
					for ( int i = 2; i < auth.Length; i++ ) mispredictBoxes.Add( PosErr( guess[i], auth[i] ) );
				}

				var before = client.Capture(); // present (tick c-1) as predicted
				remoteGuess = auth[1].V.WithZ( 0f ); // dead-reckon the remote player at its last known velocity

				sw.Restart();
				client.Restore( auth );
				predicted[s] = auth;
				int n = 0;
				for ( int t = s + 1; t <= c - 1; t++ )
				{
					client.Tick( clientInputs.TryGetValue( t, out var iw ) ? iw : Vector3.Zero, remoteGuess );
					predicted[t] = client.Capture();
					n++;
				}
				sw.Stop();
				replayMs.Add( (float)sw.Elapsed.TotalMilliseconds );
				replayTicks.Add( n );

				var after = client.Capture();
				popMe.Add( PosErr( before[0], after[0] ) );
				if ( o.Remote ) popRemote.Add( PosErr( before[1], after[1] ) );
				for ( int i = 2; i < after.Length; i++ ) popBoxes.Add( PosErr( before[i], after[i] ) );
			}
		}

		var sb = new StringBuilder();
		sb.AppendLine( $"network: RTT {o.RttMs} ms (one-way {oneWay} ticks, client lead {lead}), snapshot every {o.SnapshotEvery} ticks, remote player {(o.Remote ? "ON" : "off")}, {o.Boxes} boxes" );
		sb.AppendLine( $"  replay cost   {replayMs.Text( "ms" )}" );
		sb.AppendLine( $"  replay ticks  {replayTicks.Text( "" )}" );
		sb.AppendLine( $"  mispredict @snapshot  me {mispredictMe.Text( "u" )}" );
		if ( o.Remote ) sb.AppendLine( $"                        remote {mispredictRemote.Text( "u" )}" );
		sb.AppendLine( $"                        boxes {mispredictBoxes.Text( "u" )}" );
		sb.AppendLine( $"  correction @present   me {popMe.Text( "u" )}" );
		if ( o.Remote ) sb.AppendLine( $"                        remote {popRemote.Text( "u" )}" );
		sb.Append( $"                        boxes {popBoxes.Text( "u" )}" );
		return sb.ToString();
	}

	// ⚠ INVALID (found 2026-11-04): PhysicsBody.AddCloneShape is a no-op stub in the retail engine, so every body this
	// cloned had NO shapes and the 0.14 ms/tick it reported measured empty bodies. Real numbers (SimWorld + SimMirrorProbe,
	// 66 lobby clay props / 626 real hulls, ~40 tumbling): 0.43 ms/step. Kept only for the history.
	/// <summary>What one tick of a REAL map costs in a private world: every body in the active scene's physics world
	/// cloned in (statics static, everything else dynamic — the clay props as if all loose), then stepped. Also the cost
	/// of capturing + restoring every dynamic body (what each rollback pays on top of the steps).</summary>
	public static string SceneCost( PhysicsWorld src = null )
	{
		src ??= Game.ActiveScene?.PhysicsWorld;
		if ( src is null || !src.IsValid() )
			return "scene cost: no active scene physics world";

		var world = new PhysicsWorld { Gravity = src.Gravity, SubSteps = src.SubSteps };
		try
		{
			int statics = 0, dynamics = 0, shapes = 0, failed = 0;
			var dyn = new List<PhysicsBody>();
			foreach ( var b in src.Bodies.ToList() )
			{
				if ( !b.IsValid() || b.ShapeCount == 0 )
					continue;
				// Clay props count as loose (they're static colliders until shot or pushed); the map stays static.
				bool clay = b.GameObject.IsValid() && b.GameObject.Components.Get<SdfCollider>( FindMode.EverythingInSelfAndAncestors ).IsValid();
				bool isStatic = b.BodyType == PhysicsBodyType.Static && !clay;
				var copy = new PhysicsBody( world ) { BodyType = isStatic ? PhysicsBodyType.Static : PhysicsBodyType.Dynamic };
				copy.Transform = b.Transform;
				foreach ( var s in b.Shapes )
				{
					try { copy.AddCloneShape( s ); shapes++; }
					catch { failed++; }
				}
				if ( isStatic ) statics++;
				else
				{
					dynamics++;
					copy.Mass = MathF.Max( b.Mass, 1f );
					dyn.Add( copy );
				}
			}

			var sw = Stopwatch.StartNew();
			for ( int t = 0; t < 25; t++ ) world.Step( Dt );   // first second: things fall and settle
			float settleMs = (float)sw.Elapsed.TotalMilliseconds / 25f;
			int awake = dyn.Count( d => !d.Sleeping );

			// Wake everything (worst case: the whole map tumbling) and time again.
			var startPos = dyn.Select( d => d.Position ).ToList();
			float awakeMs = 0f;
			int awakeAfter = 0;
			for ( int t = 0; t < 25; t++ )
			{
				foreach ( var d in dyn ) { d.Sleeping = false; d.Velocity += Vector3.Random * 40f; } // keep them all tumbling
				sw.Restart();
				world.Step( Dt );
				awakeMs += (float)sw.Elapsed.TotalMilliseconds / 25f;
			}
			awakeAfter = dyn.Count( d => !d.Sleeping );
			float moved = dyn.Select( ( d, i ) => d.Position.Distance( startPos[i] ) ).DefaultIfEmpty( 0f ).Average();

			sw.Restart();
			for ( int k = 0; k < 25; k++ )
			{
				var states = dyn.Select( d => (d.Transform, d.Velocity, d.AngularVelocity) ).ToList();
				for ( int i = 0; i < dyn.Count; i++ )
				{
					dyn[i].Transform = states[i].Transform;
					dyn[i].Velocity = states[i].Velocity;
					dyn[i].AngularVelocity = states[i].AngularVelocity;
				}
			}
			float restoreMs = (float)sw.Elapsed.TotalMilliseconds / 25f;

			return $"scene cost:{statics} static + {dynamics} dynamic bodies, {shapes} shapes ({failed} failed to clone)\n"
				+ $"  step: settling {settleMs:0.000} ms/tick ({awake} still awake after 0.5s), all awake {awakeMs:0.000} ms/tick ({awakeAfter} awake, moved {moved:0.0}u avg); capture+restore all {restoreMs:0.000} ms";
		}
		finally
		{
			world.Delete();
		}
	}

	// ── Entry points ────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Run one scenario: "det", "replay", "net", or "all" (default: the full sweep).</summary>
	public static string Run( string scenario = "all", Options o = null )
	{
		o ??= new Options();
		var sb = new StringBuilder();
		var total = Stopwatch.StartNew();
		try
		{
			if ( scenario is "all" or "sanity" )
			{
				using var lab = new Lab( o );
				var bb = lab.Bodies[2].GetBounds();
				for ( int t = 0; t < 50; t++ ) lab.World.Step( Dt );
				sb.AppendLine( $"sanity: box bounds size {bb.Size} (24 = half-extent API), box z after 1s {lab.Bodies[2].Position.z:0.00}, top box z {lab.Bodies[^1].Position.z:0.00}, asleep {lab.Bodies[2].Sleeping}" );
			}
			if ( scenario is "all" or "det" )
				sb.AppendLine( Determinism( o ) );
			if ( scenario is "all" or "replay" )
			{
				sb.AppendLine( RestoreReplay( o, back: 6, every: 2 ) );
				sb.AppendLine( RestoreReplay( o, back: 15, every: 2 ) );
			}
			if ( scenario is "net" )
				sb.AppendLine( Network( o ) );
			if ( scenario is "all" or "scene" )
				sb.AppendLine( SceneCost() );
			if ( scenario is "all" or "scale" )
			{
				foreach ( var n in new[] { 3, 12, 30 } )
					sb.AppendLine( Network( new Options { RttMs = 200, Remote = true, Boxes = n, Seed = o.Seed } ) );
			}
			if ( scenario is "all" )
			{
				foreach ( var rtt in new[] { 0, 50, 100, 200 } )
				{
					sb.AppendLine( Network( new Options { RttMs = rtt, Remote = false, Seed = o.Seed } ) );
					sb.AppendLine( Network( new Options { RttMs = rtt, Remote = true, Seed = o.Seed } ) );
				}
			}
		}
		catch ( Exception e )
		{
			sb.AppendLine( $"FAILED: {e}" );
		}
		sb.Append( $"total {total.Elapsed.TotalSeconds:0.00}s" );
		return sb.ToString();
	}

	[ConCmd( "mimi_rollback_lab" )]
	public static void Cmd( string scenario = "all" ) => Log.Info( "\n" + Run( scenario ) );
}
