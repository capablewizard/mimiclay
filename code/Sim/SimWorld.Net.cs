using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mimiclay.Sim;

// Messages. Static RPCs (this is a GameObjectSystem); each lands on the receiving machine's SimWorld.Current.
public sealed partial class SimWorld
{
	// ── Fake lag ────────────────────────────────────────────────────────────────────────────────────────────

	readonly List<(double due, Action run)> _inbox = new();

	void Receive( Action run )
	{
		if ( FakeLagMs <= 0 )
			run();
		else
			_inbox.Add( (RealTime.NowDouble + FakeLagMs / 1000.0, run) );
	}

	void RunInbox()
	{
		if ( _inbox.Count == 0 )
			return;
		double now = RealTime.NowDouble;
		var due = _inbox.Where( m => m.due <= now ).ToList();
		_inbox.RemoveAll( m => m.due <= now );
		foreach ( var m in due )
			m.run();
	}

	static bool FromHost => Rpc.Caller is null || Rpc.Caller.IsHost;

	// ── Adds / removes / rest (reliable) ────────────────────────────────────────────────────────────────────

	static byte[] WriteAdds( IEnumerable<SimBody> bodies )
	{
		var list = bodies.ToList();
		using var ms = new MemoryStream();
		using var w = new BinaryWriter( ms );
		w.Write( (ushort)list.Count );
		foreach ( var b in list )
		{
			var s = b.Spec;
			var st = b.Capture();
			w.Write( b.Id );
			w.Write( s.Key.ToByteArray() );
			w.Write( s.Owner.ToByteArray() );
			w.Write( s.Driver );
			w.Write( (byte)s.Shape );
			w.Write( (byte)s.Flags );
			SimQuant.WriteFull( w, s.HalfExtents );
			w.Write( s.Source.ToByteArray() );
			w.Write( s.Mass );
			SimQuant.WriteFull( w, st.P ); SimQuant.WriteFull( w, st.R ); SimQuant.WriteFull( w, st.V ); SimQuant.WriteFull( w, st.W );
			w.Write( st.Sleeping );
		}
		w.Flush();
		return ms.ToArray();
	}

	[Rpc.Broadcast]
	static void BroadcastAdds( byte[] data )
	{
		if ( !FromHost || Networking.IsHost )
			return;
		SimStats.ReliableBytes += data.Length;
		var sim = Current;
		sim?.Receive( () => sim.ReadAdds( data ) );
	}

	void ReadAdds( byte[] data )
	{
		EnsureWorld();
		using var r = new BinaryReader( new MemoryStream( data ) );
		int n = r.ReadUInt16();
		for ( int i = 0; i < n; i++ )
		{
			ushort id = r.ReadUInt16();
			var spec = new SimBodySpec
			{
				Key = new Guid( r.ReadBytes( 16 ) ),
				Owner = new Guid( r.ReadBytes( 16 ) ),
				Driver = r.ReadByte(),
				Shape = (SimShapeKind)r.ReadByte(),
				Flags = (SimBodyFlags)r.ReadByte(),
				HalfExtents = SimQuant.ReadFull( r ),
				Source = new Guid( r.ReadBytes( 16 ) ),
				Mass = r.ReadSingle(),
			};
			var st = new SimBodyState { P = SimQuant.ReadFull( r ), R = SimQuant.ReadFullRot( r ), V = SimQuant.ReadFull( r ), W = SimQuant.ReadFull( r ), Sleeping = r.ReadBoolean() };
			spec.Pose = new Transform( st.P, st.R );

			if ( _byId.TryGetValue( id, out var clash ) && clash.Key != spec.Key )
				Remove( clash );
			// The same key under a NEW id: a re-registration whose remove hasn't landed yet (or was lost) — replace it.
			if ( _byKey.TryGetValue( spec.Key, out var body ) && body.Id != id )
			{
				Remove( body );
				body = null;
			}
			if ( body is null )
				body = Create( spec, id );
			body.Restore( st );
			if ( st.Sleeping )
				body.Rest = st;
			body.Prev = body.Cur = body.Phys.Transform;
		}
	}

	[Rpc.Broadcast]
	static void BroadcastRemove( ushort id )
	{
		if ( !FromHost || Networking.IsHost )
			return;
		var sim = Current;
		sim?.Receive( () =>
		{
			if ( sim._byId.TryGetValue( id, out var b ) )
				sim.Remove( b );
		} );
	}

	[Rpc.Broadcast]
	static void BroadcastRest( int tick, ushort id, Vector3 position, Rotation rotation )
	{
		if ( !FromHost || Networking.IsHost )
			return;
		var sim = Current;
		sim?.Receive( () => sim.OnRest( tick, id, new SimBodyState { P = position, R = rotation, Sleeping = true } ) );
	}

	// The host says this body is asleep here. Remembered (rollbacks put bodies a snapshot leaves out at their rest pose);
	// if ours is also still and somewhere else, it moves there now, the jump fading like any correction.
	void OnRest( int tick, ushort id, SimBodyState rest )
	{
		if ( !_byId.TryGetValue( id, out var b ) )
			return;
		b.Rest = rest;
		NoteAuthority( b, tick, rest );
		// The rest is the host's state at ITS tick, and our input may have moved the body since (turned it, walked it
		// off): never snap the body we drive to it — snapshots correct that, through a replay of our inputs. And if what
		// we predicted for that tick is already there, there's nothing to correct.
		if ( b.Driver is not null && IsLocallyControlled( b ) )
			return;
		if ( _history.TryGetValue( tick, out var then ) && b.Id < then.Length && then[b.Id] is { } was && was.P.Distance( rest.P ) <= SkipEpsilon )
			return;
		var cur = b.Capture();
		if ( cur.P.Distance( rest.P ) <= SkipEpsilon || (!cur.Sleeping && cur.V.Length > 1f) )
			return;
		var before = VisualPose( b );
		b.Restore( rest );
		b.Prev = b.Cur = b.Phys.Transform;
		var after = VisualPose( b );
		b.PosOffset += before.Position - after.Position;
		b.RotOffset = before.Rotation * after.Rotation.Inverse * b.RotOffset;
		SimStats.RestSnaps++;
	}

	// ── Late joiners ────────────────────────────────────────────────────────────────────────────────────────

	readonly HashSet<Guid> _welcomed = new();

	// Host, per tick: anyone new gets every body (and its state) reliably.
	void HostWelcomeNewConnections()
	{
		if ( !Networking.IsActive )
			return;
		foreach ( var c in Connection.All )
		{
			if ( !c.IsActive || c.Id == LocalId || !_welcomed.Add( c.Id ) )
				continue;
			SendEverythingTo( c );
		}
	}

	void SendEverythingTo( Connection c )
	{
		if ( _bodies.Count == 0 )
			return;
		var data = WriteAdds( _bodies );
		using ( Rpc.FilterInclude( c ) )
			BroadcastAdds( data );
		SimStats.FullStatesSent++;
	}

	RealTimeSince _sinceAskedForAll = 999f;

	// ── Round-trip probe (client → host → client), before the first sync — see _rttTicks ───────────────────

	void SendSimPing()
	{
		if ( IsAuthority || !Networking.IsActive )
			return;
		_pingSent = true;
		_sincePing = 0f;
		SimPing( RealTime.NowDouble );
	}

	[Rpc.Host]
	static void SimPing( double sentAt )
	{
		var caller = Rpc.Caller;
		if ( caller is null )
			return;
		using ( Rpc.FilterInclude( caller ) )
			SimPong( sentAt );
	}

	[Rpc.Broadcast]
	static void SimPong( double sentAt )
	{
		if ( !FromHost || Networking.IsHost )
			return;
		var sim = Current;
		if ( sim is null || sim._rttTicks >= 0f )
			return;
		sim._rttTicks = (float)((RealTime.NowDouble - sentAt) / Dt);
	}

	/// <summary>Client → host: I'm missing bodies (a snapshot named ids I don't know) — send me everything.</summary>
	[Rpc.Host]
	static void RequestEverything()
	{
		var sim = Current;
		var caller = Rpc.Caller;
		if ( sim is null || caller is null || !sim.IsAuthority )
			return;
		sim.SendEverythingTo( caller );
	}

	// ── Snapshots (unreliable) ──────────────────────────────────────────────────────────────────────────────

	const int SnapshotEntryBytes = 2 + 1 + 9 + 4 + 6 + 6;

	void SendSnapshot()
	{
		// Nothing registered: nothing to say (a client with no world asked for everything on every empty snapshot, once a
		// second, forever).
		if ( _bodies.Count == 0 )
			return;
		var included = _bodies.Where( InSnapshot ).ToList();
		using var ms = new MemoryStream( 64 + included.Count * SnapshotEntryBytes );
		using var w = new BinaryWriter( ms );
		w.Write( Tick );

		// Each client's slack: how many ticks ahead of us its newest input is (it steers its clock by it).
		var slacks = _hostNewestInput.Where( kv => Connection.All.Any( c => c.Id == kv.Key ) ).ToList();
		w.Write( (byte)Math.Min( slacks.Count, 255 ) );
		foreach ( var (id, newest) in slacks.Take( 255 ) )
		{
			w.Write( id.ToByteArray() );
			w.Write( (short)Math.Clamp( newest - Tick, short.MinValue, short.MaxValue ) );
		}

		w.Write( (ushort)included.Count );
		foreach ( var b in included )
		{
			var st = b.Capture();
			w.Write( b.Id );
			w.Write( (byte)((st.Sleeping ? 1 : 0) | (st.HasCtrl ? 2 : 0)) );
			SimQuant.WritePos( w, st.P ); SimQuant.WriteRot( w, st.R ); SimQuant.WriteLinear( w, st.V ); SimQuant.WriteSpin( w, st.W );
			if ( st.HasCtrl )
				SimQuant.WriteCtrl( w, st.Ctrl ); // a driven body: its controller state rolls back with it

			// Continue from exactly what the clients will decode, so a restore there lands on our numbers.
			if ( QuantizeHost && !st.Sleeping )
				b.Restore( SimQuant.RoundTrip( st ) );
		}
		w.Flush();

		var data = ms.ToArray();
		SimStats.SnapshotBytes += data.Length;
		SimStats.SnapshotBodies.Add( included.Count );
		BroadcastSnapshot( data );
	}

	[Rpc.Broadcast( NetFlags.UnreliableNoDelay )]
	static void BroadcastSnapshot( byte[] data )
	{
		if ( !FromHost || Networking.IsHost )
			return;
		var sim = Current;
		if ( sim is null )
			return;
		if ( sim._world is null )
		{
			// Snapshots but no bodies: the reliable adds went somewhere else — e.g. the host sent them while we were still
			// loading this scene (seen on a round → lobby change: the client sat idle forever). Ask again.
			sim.AskForEverything();
			return;
		}
		SimStats.SnapshotBytes += data.Length;
		sim.Receive( () => sim.ReadSnapshot( data ) );
	}

	void ReadSnapshot( byte[] data )
	{
		using var r = new BinaryReader( new MemoryStream( data ) );
		int s = r.ReadInt32();
		if ( s <= _lastSnap )
		{
			SimStats.SnapshotsStale++;
			return;
		}
		_lastSnap = s;
		SimStats.Snapshots++;

		int slacks = r.ReadByte();
		for ( int i = 0; i < slacks; i++ )
		{
			var id = new Guid( r.ReadBytes( 16 ) );
			short slack = r.ReadInt16();
			if ( id == LocalId )
			{
				_slack = slack;
				_slackAvg += (slack - _slackAvg) * 0.1f;
			}
		}

		int n = r.ReadUInt16();
		var auth = new Dictionary<ushort, SimBodyState>( n );
		bool unknown = false;
		for ( int i = 0; i < n; i++ )
		{
			ushort id = r.ReadUInt16();
			byte flags = r.ReadByte();
			var st = new SimBodyState { P = SimQuant.ReadPos( r ), R = SimQuant.ReadRot( r ), V = SimQuant.ReadLinear( r ), W = SimQuant.ReadSpin( r ), Sleeping = (flags & 1) != 0,
				Anchored = false, AnchorKnown = true }; // snapshots only carry bodies the host has free
			if ( (flags & 2) != 0 )
			{
				st.Ctrl = SimQuant.ReadCtrl( r );
				st.HasCtrl = true;
			}
			if ( !_byId.TryGetValue( id, out var b ) )
			{
				unknown = true;
				continue;
			}
			auth[id] = st;
			b.PredictedRelease = -1; // moving on the host: whatever we predicted about releasing it, the host agrees it's free
			if ( st.Sleeping )
				b.Rest = st;
			NoteAuthority( b, s, st );
			if ( b.Driver is not null && b.Spec.Owner != Guid.Empty && !b.IsLocalOwned )
				b.RemoteGuess = (b.Spec.Flags & SimBodyFlags.Inert) != 0 ? SimInput.None : b.Driver.Guess( b, st );
		}

		if ( unknown )
			AskForEverything();

		OnSnapshot( s, auth );
	}

	void AskForEverything()
	{
		if ( _sinceAskedForAll < 1f )
			return;
		_sinceAskedForAll = 0f;
		SimStats.FullStatesAsked++;
		RequestEverything();
	}

	// ── Inputs (client → host, unreliable) ──────────────────────────────────────────────────────────────────

	void SendInputs()
	{
		int first = Math.Max( Tick - InputRedundancy + 1, 0 );
		using var ms = new MemoryStream( 1 + InputRedundancy * 26 );
		using var w = new BinaryWriter( ms );
		w.Write( (byte)(Tick - first + 1) );
		for ( int t = first; t <= Tick; t++ )
		{
			var i = _myInputs.TryGetValue( t, out var x ) ? x : SimInput.None;
			w.Write( i.Wish.x ); w.Write( i.Wish.y ); w.Write( i.Buttons ); w.Write( i.Turn );
			if ( i.Has( SimInput.Hold ) )
			{
				SimQuant.WritePos( w, i.HoldPos ); // a carried prop's target (SimHoldDriver): 13 bytes, only while holding
				SimQuant.WriteRot( w, i.HoldRot );
			}
		}
		w.Flush();
		var data = ms.ToArray();
		SimStats.InputBytes += data.Length;
		SendInputsToHost( first, data );
	}

	[Rpc.Host( NetFlags.UnreliableNoDelay )]
	static void SendInputsToHost( int firstTick, byte[] data )
	{
		var sim = Current;
		var from = Rpc.Caller?.Id ?? Guid.Empty;
		if ( sim is null || sim._world is null || !sim.IsAuthority )
			return;
		sim.Receive( () => sim.ReadInputs( from, firstTick, data ) );
	}

	void ReadInputs( Guid from, int firstTick, byte[] data )
	{
		using var r = new BinaryReader( new MemoryStream( data ) );
		int n = r.ReadByte();
		if ( !_hostInputs.TryGetValue( from, out var q ) )
			_hostInputs[from] = q = new();
		for ( int i = 0; i < n; i++ )
		{
			var input = new SimInput { Wish = new Vector2( r.ReadSingle(), r.ReadSingle() ), Buttons = r.ReadByte(), Turn = r.ReadSingle() };
			if ( input.Has( SimInput.Hold ) )
			{
				input.HoldPos = SimQuant.ReadPos( r );
				input.HoldRot = SimQuant.ReadRot( r );
			}
			int t = firstTick + i;
			if ( t > Tick )
				q[t] = input; // older ticks are redundant repeats (or too late — counted as missing when needed)
		}
		int newest = firstTick + n - 1;
		if ( !_hostNewestInput.TryGetValue( from, out var had ) || newest > had )
			_hostNewestInput[from] = newest;
	}
}
