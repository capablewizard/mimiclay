using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Mimiclay.Sim;

// The tick: the host steps the truth; a client predicts ahead and rolls back on a snapshot that disagrees.
public sealed partial class SimWorld
{
	double _clock;

	// Client
	bool _synced;
	int _lastSnap = -1;
	int _slack = TargetSlack;
	int _clockDebt;
	RealTimeSince _sinceClockAdjust;

	/// <summary>Client: body states after each predicted tick, indexed by body id (see <see cref="Snapshot"/>).</summary>
	readonly Dictionary<int, SimBodyState?[]> _history = new();
	readonly Dictionary<int, SimInput> _myInputs = new();

	// Host: each client's inputs by tick, the last one applied, and the newest tick each has sent.
	readonly Dictionary<Guid, Dictionary<int, SimInput>> _hostInputs = new();
	readonly Dictionary<Guid, SimInput> _hostLastInput = new();
	readonly Dictionary<Guid, int> _hostNewestInput = new();

	void Update()
	{
		if ( _world is null || Scene.IsEditor )
			return;

		// The sim keeps its OWN 50 Hz clock rather than the scene's fixed update: a joining client's scene ran at 16 Hz
		// when the rig first started (FixedDelta 0.0625), and every machine must tick at exactly the same rate.
		_clock += Time.Delta;
		int n = 0;
		while ( _clock >= Dt )
		{
			_clock -= Dt;
			if ( ++n > MaxTicksPerFrame )
			{
				_clock = 0;
				SimStats.DroppedTicks++;
				break;
			}
			RunInbox();
			if ( IsAuthority )
				HostTick();
			else
				ClientTicks();

			if ( Tick % StaticRefreshTicks == 0 )
				RefreshStatics();
			if ( Tick % FollowerRefreshTicks == 0 )
				RefreshFollowers();
			if ( Tick % ShapeRefreshTicks == 0 )
				RefreshShapes();
		}

		FadeCorrections();
		PresentBodies();
		Draw();
	}

	// Drive every body that has a driver (its owner's input; on a client, a guess for other people's), then step.
	void Step( int tick, SimInput mine, Func<SimBody, SimInput> others )
	{
		foreach ( var b in _bodies )
		{
			if ( b.Driver is null || b.Spec.Owner == Guid.Empty || !b.ShapesReady )
				continue;
			b.Driver.Drive( b, b.Spec.Owner == LocalId ? mine : others( b ), tick );
		}
		DriveFollowers();
		long t0 = Stopwatch.GetTimestamp();
		_world.Step( Dt );
		SimStats.StepMs.Add( (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency) );
		SimStats.Steps++;
	}

	// ── Host ────────────────────────────────────────────────────────────────────────────────────────────────

	void HostTick()
	{
		Tick++;
		var mine = LocalInput?.Invoke( Tick ) ?? SimInput.None;
		Step( Tick, mine, b => HostInput( b.Spec.Owner, Tick ) );

		// Rest: a body that just fell asleep tells everyone where, reliably (snapshots stop carrying it soon).
		foreach ( var b in _bodies )
		{
			bool asleep = b.Phys.Sleeping;
			if ( asleep && !b.WasSleeping )
			{
				b.AsleepTicks = 0;
				if ( Networking.IsActive )
					BroadcastRest( Tick, b.Id, b.Phys.Position, b.Phys.Rotation );
			}
			else if ( asleep )
				b.AsleepTicks = b.AsleepTicks == int.MaxValue ? int.MaxValue : b.AsleepTicks + 1;
			else
				b.AsleepTicks = int.MaxValue;
			b.WasSleeping = asleep;
		}

		AdvanceVisuals();
		DetectAnchoredPushes();
		HostWelcomeNewConnections();

		if ( Networking.IsActive && Tick % SnapshotEvery == 0 )
			SendSnapshot();
	}

	SimInput HostInput( Guid owner, int tick )
	{
		if ( _hostInputs.TryGetValue( owner, out var q ) )
		{
			if ( q.Remove( tick, out var w ) )
			{
				_hostLastInput[owner] = w;
				SimStats.HostInputOnTime++;
			}
			else
				SimStats.HostInputMissing++;
			if ( q.Count > 0 )
				foreach ( var old in q.Keys.Where( k => k < tick ).ToList() )
					q.Remove( old );
		}
		return _hostLastInput.TryGetValue( owner, out var last ) ? last : SimInput.None;
	}

	// Bodies a snapshot carries: awake, or asleep so recently its reliable rest may not have landed yet.
	bool InSnapshot( SimBody b ) => b.ShapesReady && (b.Spec.Flags & SimBodyFlags.Anchored) == 0 && (!b.Phys.Sleeping || b.AsleepTicks < RestResendTicks);

	// ── Client ──────────────────────────────────────────────────────────────────────────────────────────────

	void ClientTicks()
	{
		if ( !_synced )
			return;

		// Keep TargetSlack ticks of input queued at the host. Off by more than the deadband: take the WHOLE error as a
		// debt, paid a tick at a time (an extra tick to get ahead, a skipped one to fall back), then wait a round trip
		// before judging again — the slack we read is that old.
		float window = ((Connection.Host?.Ping ?? 0f) + 2f * FakeLagMs) / 1000f + 0.1f;
		if ( _clockDebt == 0 && _sinceClockAdjust > window && (_slack < TargetSlack - 1 || _slack > TargetSlack + 2) )
		{
			_clockDebt = _slack - TargetSlack;
			if ( _clockDebt > 0 ) SimStats.ClockSlower++; else SimStats.ClockFaster++;
		}

		int steps = 1;
		if ( _clockDebt > 0 ) { steps = 0; _clockDebt--; }
		else if ( _clockDebt < 0 ) { steps = 2; _clockDebt++; }
		if ( steps != 1 )
			_sinceClockAdjust = 0f;
		for ( int i = 0; i < steps; i++ )
			PredictTick();
	}

	void PredictTick()
	{
		Tick++;
		var mine = LocalInput?.Invoke( Tick ) ?? SimInput.None;
		_myInputs[Tick] = mine;
		_myInputs.Remove( Tick - HistoryTicks );
		_history.Remove( Tick - HistoryTicks );
		// Always, even with nothing to drive: the host answers every client's inputs with its slack, and that is what keeps
		// the client's clock just far enough ahead (a client with no input of its own sat 21 ticks = 420 ms ahead, so every
		// replay was long and every unpredicted host event a big correction).
		SendInputs();

		Step( Tick, mine, b => b.RemoteGuess );
		_history[Tick] = CaptureAll();
		AdvanceVisuals();
	}

	SimBodyState?[] CaptureAll()
	{
		var s = new SimBodyState?[_maxId + 1];
		foreach ( var b in _bodies )
			s[b.Id] = b.Capture();
		return s;
	}

	/// <summary>Client: a snapshot of tick <paramref name="s"/> arrived (already decoded).</summary>
	void OnSnapshot( int s, Dictionary<ushort, SimBodyState> auth )
	{
		// First contact: take the host's state, then run ahead far enough that our inputs reach it in time.
		if ( !_synced )
		{
			ApplyAuthority( auth );
			foreach ( var b in _bodies ) b.Prev = b.Cur = b.Phys.Transform;
			Tick = s;
			_history[s] = CaptureAll();
			int lead = (int)MathF.Ceiling( ((Connection.Host?.Ping ?? 0f) + 2f * FakeLagMs) / (Dt * 1000f) ) + TargetSlack + 1;
			for ( int i = 0; i < lead; i++ )
				PredictTick();
			_synced = true;
			_sinceClockAdjust = 0f;
			Log.Info( $"SimWorld: synced at host tick {s}, running {lead} ticks ahead, {_bodies.Count} bodies" );
			return;
		}

		// Behind the host (a hitch): jump to it.
		if ( s > Tick )
		{
			ApplyAuthority( auth );
			Tick = s;
			_history[s] = CaptureAll();
			foreach ( var b in _bodies ) b.Prev = b.Cur = b.Phys.Transform;
			SimStats.Resyncs++;
			return;
		}

		SimStats.Lead.Add( Tick - s );
		SimStats.Slack.Add( _slack );

		// How wrong were we at tick s? Bodies in the snapshot against their state; the rest should be asleep at rest.
		float err = float.MaxValue;
		if ( _history.TryGetValue( s, out var guess ) )
		{
			err = 0f;
			foreach ( var b in _bodies )
			{
				var g = b.Id < guess.Length ? guess[b.Id] : null;
				if ( g is null )
					continue;
				float e;
				if ( auth.TryGetValue( b.Id, out var a ) )
					e = g.Value.P.Distance( a.P );
				else if ( b.Rest is { } rest )
					e = g.Value.Sleeping ? 0f : g.Value.P.Distance( rest.P ); // asleep at the host
				else
					continue;
				err = MathF.Max( err, e );
				if ( b.IsLocalOwned )
					SimStats.MispredictMine.Add( e );
			}
			SimStats.Mispredict.Add( err );
		}

		if ( err <= SkipEpsilon )
		{
			SimStats.Skipped++;
			return;
		}
		Rollback( s, auth );
	}

	// Host state for tick s onto the bodies: snapshot ones exactly; ones it left out are asleep at their rest pose.
	void ApplyAuthority( Dictionary<ushort, SimBodyState> auth )
	{
		foreach ( var b in _bodies )
		{
			if ( auth.TryGetValue( b.Id, out var a ) )
				b.Restore( a );
			else if ( b.Rest is { } rest )
				b.Restore( rest );
		}
	}

	void Rollback( int s, Dictionary<ushort, SimBodyState> auth )
	{
		var before = _bodies.Select( VisualPose ).ToList();

		var sw = Stopwatch.StartNew();
		// Start from what we predicted for s (bodies the host didn't mention, e.g. ones it hasn't heard about yet,
		// keep that), then overwrite with the host's state.
		if ( _history.TryGetValue( s, out var predicted ) )
			foreach ( var b in _bodies )
				if ( b.Id < predicted.Length && predicted[b.Id] is { } st )
					b.Restore( st );
		ApplyAuthority( auth );
		_history[s] = CaptureAll();

		Transform[] prevPose = null;
		for ( int t = s + 1; t <= Tick; t++ )
		{
			if ( t == Tick )
				prevPose = _bodies.Select( b => b.Phys.Transform ).ToArray();
			Step( t, _myInputs.TryGetValue( t, out var w ) ? w : SimInput.None, b => b.RemoteGuess );
			_history[t] = CaptureAll();
		}
		sw.Stop();
		SimStats.Replays++;
		SimStats.ReplayMs.Add( (float)sw.Elapsed.TotalMilliseconds );
		SimStats.ReplayTicks.Add( Tick - s );

		for ( int i = 0; i < _bodies.Count; i++ )
		{
			var b = _bodies[i];
			b.Prev = prevPose is not null ? prevPose[i] : b.Phys.Transform;
			b.Cur = b.Phys.Transform;

			// Fold the jump into the visual offset so it fades rather than pops.
			var after = VisualPose( b );
			var dp = before[i].Position - after.Position;
			float len = dp.Length;
			if ( len > 0.001f )
			{
				if ( b.IsLocalOwned ) SimStats.CorrectionMine.Add( len );
				else if ( b.Spec.Owner != Guid.Empty ) SimStats.CorrectionOthers.Add( len );
				else SimStats.CorrectionLoose.Add( len );
			}
			if ( SmoothRate <= 0f || len > 80f )
			{
				b.PosOffset = 0f;
				b.RotOffset = Rotation.Identity;
				continue;
			}
			// after already includes the old offset, so the new one is the old plus the jump.
			b.PosOffset += dp;
			b.RotOffset = before[i].Rotation * after.Rotation.Inverse * b.RotOffset;
		}
	}
}
