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

	/// <summary>Client: how many ticks ahead of the host's newest snapshot we're predicting (0 on the host).</summary>
	public int LeadTicks => IsAuthority || _lastSnap < 0 ? 0 : Math.Max( Tick - _lastSnap, 0 );
	int _slack = TargetSlack;
	int _clockDebt;
	float _slackAvg = TargetSlack; // smoothed slack readings (ClockRate)
	float _leadAvg; // smoothed lead (our tick − the newest snapshot's) — what RemotePose measures its delay from

	// The round trip to the host, in ticks, measured by the sim itself — NOT Connection.Ping: under the engine's net_fakelag
	// (applied to send AND receive: 300 = a 600 ms round trip) Ping reads 0, and every wait that was sized from it (the
	// first lead, how long to wait after a clock correction before judging the next) collapsed to nothing — the clock
	// corrected itself every 0.1 s on readings 0.6 s stale, overshooting ±30 ticks each time, so every input landed early
	// or late ("every tiny movement freaks out" past ~150 ms of fake lag). First a probe (SimPing/SimPong, before the sync),
	// then each snapshot: lead − slack = (host→us) + (us→host) in ticks.
	float _rttTicks = -1f;
	RealTimeSince _sincePing = 999f;
	bool _pingSent;

	/// <summary>Client: the measured round trip to the host (ms), or the engine's ping while unmeasured.</summary>
	public float RttMs => _rttTicks >= 0f ? _rttTicks * Dt * 1000f : (Connection.Host?.Ping ?? 0f) + 2f * FakeLagMs;
	int RttTicks => (int)MathF.Ceiling( RttMs / (Dt * 1000f) );
	float _alpha; // this frame's place between ticks (VisualPose)
	RealTimeSince _sinceClockAdjust;

	/// <summary>Client: body states after each predicted tick, indexed by body id (see <see cref="Snapshot"/>).</summary>
	readonly Dictionary<int, SimBodyState?[]> _history = new();
	readonly Dictionary<int, SimInput> _myInputs = new();

	// Host: each client's inputs by tick, the last one applied, and the newest tick each has sent.
	readonly Dictionary<Guid, Dictionary<int, SimInput>> _hostInputs = new();
	readonly Dictionary<Guid, SimInput> _hostLastInput = new();
	readonly Dictionary<Guid, int> _hostNewestInput = new();

	/// <summary>
	/// Where on the frame clock (Time.NowDouble) the tick being run falls — before this frame's time when the accumulator
	/// had some left over. Input sampled per tick (a mouse turn) reads its value AT this time, not at the frame's: ticks
	/// and frames don't line up (50 Hz vs 60 fps), and reading the frame's whole delta gave turns of 1.5°, 3°, 1.5°…
	/// for a steady mouse.
	/// </summary>
	public double TickTime { get; private set; }

	void Update()
	{
		if ( _world is null || Scene.IsEditor )
			return;

		// The sim keeps its OWN 50 Hz clock rather than the scene's fixed update: a joining client's scene ran at 16 Hz
		// when the rig first started (FixedDelta 0.0625), and every machine must tick at exactly the same rate.
		// A client keeps its lead by running the clock a few percent fast or slow (ClockRate) — skipping or doubling
		// whole ticks to do it was a visible hitch each time (51 of them in 8 s on a live session).
		float rate = IsAuthority ? 1f : ClockRate();
		_clock += Time.Delta * rate;
		int n = 0;
		while ( _clock >= Dt )
		{
			_clock -= Dt;
			TickTime = Time.NowDouble - _clock / rate;
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

		// Where this frame falls between the last tick and the next: what's left in the accumulator. (Time since the last
		// tick was wrong — a tick landing reset it to 0 and the pose fell back to the previous tick's, the one the frame
		// before had already shown: a stalled frame, then a catch-up jump, many times a second.)
		_alpha = Math.Clamp( (float)(_clock / Dt), 0f, 1f );

		UpdatePresentationSource();
		FadeCorrections();
		PresentBodies();
		Draw();
	}

	// Client: how fast the sim clock runs this frame. Slack (how many ticks of input wait at the host) above target = we
	// run too far ahead = slow down a little; below = speed up. Proportional, from a smoothed slack (each reading is a
	// round trip old and jittery), within ±6%: about one tick of lead gained or lost per second at the limit.
	float ClockRate()
	{
		if ( !_synced )
			return 1f;
		float error = _slackAvg - TargetSlack;
		return 1f + Math.Clamp( -error * 0.03f, -0.06f, 0.06f );
	}

	// Drive every body that has a driver (its owner's input; on a client, a guess for other people's), then step.
	void Step( int tick, SimInput mine, Func<SimBody, SimInput> others )
	{
		// Client: our hunter's pushes, at the tick they were made — predictions and replays alike (PredictPush) — and every
		// change of drive at its tick (a grab, a drop: LogDrive), so a replay across one switches where the original did.
		if ( !IsAuthority )
			foreach ( var b in _bodies )
			{
				if ( b.PredictedPush is { } push && push.tick == tick )
					PushBody( b, push.velocity );
				foreach ( var d in b.DriveLog )
					if ( d.tick == tick )
						ApplyRedrive( b, d.driver, d.owner, d.flags, d.shape );
			}

		foreach ( var b in _bodies )
		{
			b.Driver = DriverFor( b.Spec.Driver ); // from the current code, every tick (a hotload may have replaced it)
			if ( b.Driver is null || b.Spec.Owner == Guid.Empty || !b.ShapesReady )
				continue;
			SimInput input;
			if ( b.Spec.Owner == LocalId )
				input = b.Spec.Driver == SimHoldDriver.Id || IsLocallyControlled( b ) ? mine : SimInput.None; // our body or a prop we carry; a host bot gets none
			else
				input = others( b );
			if ( TraceKey != Guid.Empty )
				TraceStep( tick, b, input, IsAuthority ? "H" : "C" ); // state at the START of the tick + the input it gets
			b.Driver.Drive( b, input, tick );
		}
		DriveFollowers();
		long t0 = Stopwatch.GetTimestamp();
		_world.Step( Dt );
		DetectAnchoredPushes(); // every machine, every step (replays too): releases are predicted
		NoteLocalContacts();
		TraceSecond( tick );
		SimStats.StepMs.Add( (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency) );
		SimStats.Steps++;
	}

	// Client, after a step: a loose body our own walker or carried prop is against (bounds a hair apart) counts as touched
	// (ShownFromAuthority) — walking into it or shoving it with what we hold draws it from our prediction from then on.
	void NoteLocalContacts()
	{
		if ( IsAuthority )
			return;
		foreach ( var mine in _bodies )
		{
			if ( !mine.IsLocalOwned || !mine.ShapesReady || !mine.Phys.IsValid() )
				continue;
			var reach = mine.Phys.GetBounds().Grow( 2f );
			foreach ( var b in _bodies )
			{
				if ( b.Driver is not null || b.Spec.Owner != Guid.Empty || b.LocalTouched || !b.ShapesReady || b.Phys.Sleeping )
					continue;
				if ( reach.Overlaps( b.Phys.GetBounds() ) )
					b.LocalTouched = true;
			}
		}
	}

	// ── Debug trace: one body's per-tick input + state, host vs client (set TraceKey from a script) ─────────
	public static Guid TraceKey { get; set; }
	public static readonly Dictionary<int, string> TraceLog = new();

	public static Guid TraceKey2 { get; set; }
	public static readonly Dictionary<int, string> TraceLog2 = new();

	// A second, undriven body (what the first one pushes): its state after each step, first prediction kept on clients.
	void TraceSecond( int tick )
	{
		if ( TraceKey2 == Guid.Empty || !_byKey.TryGetValue( TraceKey2, out var b ) )
			return;
		if ( !IsAuthority && TraceLog2.ContainsKey( tick ) )
			return;
		TraceLog2[tick] = $"p({b.Phys.Position.x:0.0},{b.Phys.Position.y:0.0},{b.Phys.Position.z:0.0}) v({b.Phys.Velocity.x:0},{b.Phys.Velocity.y:0}) sleep {b.Phys.Sleeping} motion {b.Phys.MotionEnabled} shapes {b.Phys.ShapeCount}";
	}

	void TraceStep( int tick, SimBody b, in SimInput input, string phase )
	{
		if ( b.Key != TraceKey )
			return;
		var c = b.Ctrl;
		if ( !IsAuthority && TraceLog.ContainsKey( tick ) )
			return; // client: keep the FIRST prediction of each tick (the one made a full lead ahead)
		TraceLog[tick] = $"{phase} in({input.Wish.x:0},{input.Wish.y:0} b{input.Buttons} t{input.Turn:0.00}) yaw {b.Phys.Rotation.Angles().yaw:0.00} p({b.Phys.Position.x:0.0},{b.Phys.Position.y:0.0}) v({b.Phys.Velocity.x:0},{b.Phys.Velocity.y:0}) push {c.PushLevel} cmd({c.Commanded.x:0},{c.Commanded.y:0}) g {c.Grounded}";
		if ( TraceLog.Count > 4000 )
			TraceLog.Clear();
	}

	bool IsLocallyControlled( SimBody b )
	{
		if ( LocalControlled != Guid.Empty )
			return b.Key == LocalControlled;
		// No body named: the first driven body we own (the debug rig's single pawn).
		foreach ( var o in _bodies )
			if ( o.Driver is not null && o.Spec.Owner == LocalId )
				return o == b;
		return false;
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
			bool asleep = AtRest( b );
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
	bool InSnapshot( SimBody b ) => b.ShapesReady && (b.Spec.Flags & SimBodyFlags.Anchored) == 0 && (!AtRest( b ) || b.AsleepTicks < RestResendTicks);

	// At rest = asleep and nothing about to move it. A driven body the physics put to sleep but its driver hasn't parked is
	// NOT at rest: turning in place leaves its velocity at zero, so the solver sleeps it mid-turn — and a rest broadcast
	// from there snapped the turning client back to that pose ~12 ticks later (measured: 11 rests in 6 s of turning,
	// each a 20°+ snap back, then the next snapshot rolled it forward again).
	static bool AtRest( SimBody b ) => b.Phys.Sleeping && (b.Driver is null || b.Ctrl.Parked);

	// ── Client ──────────────────────────────────────────────────────────────────────────────────────────────

	void ClientTicks()
	{
		if ( !_synced )
			return;

		// Keep TargetSlack ticks of input queued at the host. Off by more than the deadband: take the WHOLE error as a
		// debt, paid a tick at a time (an extra tick to get ahead, a skipped one to fall back), then wait a round trip
		// before judging again — the slack we read is that old.
		// Only for a BIG error (a hitch, a lag change): small drift is the clock rate's job (ClockRate).
		float window = RttMs / 1000f + 0.1f;
		if ( _clockDebt == 0 && _sinceClockAdjust > window && MathF.Abs( _slack - TargetSlack ) > 8 )
		{
			_clockDebt = _slack - TargetSlack;
			if ( _clockDebt > 0 ) SimStats.ClockSlower++; else SimStats.ClockFaster++;
		}

		// A release we predicted (PushBody) that the host never confirmed — its own push didn't happen (it saw the pusher
		// elsewhere): anchor it again where it was registered. Anchored bodies never move, so that pose is still theirs.
		int confirmWindow = RttTicks + 2 * TargetSlack + 25;
		foreach ( var b in _bodies )
		{
			// A predicted push older than any replay reaches back: done with (the host's own has long since shown, or not).
			if ( b.PredictedPush is { } push && Tick - push.tick > confirmWindow )
				b.PredictedPush = null;
			// A grab we predicted (PredictHold) that the host never confirmed: back to what it was.
			if ( b.PredictedDrive is { } drive && Tick - drive.tick > confirmWindow )
				RevertPredictedHold( b.Key );
			if ( b.PredictedRelease < 0 || Tick - b.PredictedRelease < confirmWindow )
				continue;
			b.PredictedRelease = -1;
			if ( (b.Spec.Flags & SimBodyFlags.Anchored) != 0 )
				continue;
			var before = VisualPose( b );
			b.Restore( new SimBodyState { P = b.Spec.Pose.Position, R = b.Spec.Pose.Rotation, Anchored = true, AnchorKnown = true } );
			b.Prev = b.Cur = b.Phys.Transform;
			b.PosOffset += before.Position - VisualPose( b ).Position;
			SimStats.Reanchors++;
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

	// A change of drive in effect from a PAST tick (the host's, a lead ago by the time it lands): the states captured since
	// still say the old drive, and a replay starting from one of them put it back — a prop went loose, flipped back to
	// "held" two ticks later with nobody holding it, and slid off under the hold driver's gravity on a frictionless body
	// (traced live on a client). The log only re-applies a change at its own tick, so the history is patched instead.
	void PatchHistoryDrive( SimBody b, int fromTick, byte driver, Guid owner, SimBodyFlags flags, SimShapeKind shape )
	{
		for ( int t = fromTick; t <= Tick; t++ )
		{
			if ( !_history.TryGetValue( t, out var states ) || b.Id >= states.Length || states[b.Id] is not { } s )
				continue;
			s.Driver = driver;
			s.Owner = owner;
			s.Flags = flags;
			s.Shape = shape;
			s.DriveKnown = true;
			s.Anchored = (flags & SimBodyFlags.Anchored) != 0;
			states[b.Id] = s;
		}
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
			// Not before the probe's answer (or a second without one): the first lead must be the real round trip, or the
			// clock starts tens of ticks out and spends the first second doubling and skipping ticks to get there.
			if ( _rttTicks < 0f && _pingSent && _sincePing < 1f )
				return;
			int lead = RttTicks + TargetSlack + 1;
			for ( int i = 0; i < lead; i++ )
				PredictTick();
			_leadAvg = lead;
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
		_leadAvg += (Tick - s - _leadAvg) * 0.05f;
		// Our lead over this snapshot minus the slack it reports for us = the round trip, in ticks (both legs).
		float rtt = MathF.Max( Tick - s - _slack, 0f );
		_rttTicks = _rttTicks < 0f ? rtt : _rttTicks + (rtt - _rttTicks) * 0.05f;
		SimStats.Rtt.Add( rtt * Dt * 1000f );

		// How wrong were we at tick s? Bodies in the snapshot against their state; the rest should be asleep at rest.
		// Someone else's driven body only counts past RemoteSkipEpsilon (its guess is always a little wrong; see there).
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
					e = MathF.Max( g.Value.P.Distance( a.P ), g.Value.R.Distance( a.R ) * RotErrorArm ); // a wrong facing is wrong too
				else if ( b.Rest is { } rest )
					e = g.Value.Sleeping ? 0f : g.Value.P.Distance( rest.P ); // asleep at the host
				else
					continue;
				if ( b.IsRemoteDriven && e <= RemoteSkipEpsilon )
					e = 0f;
				err = MathF.Max( err, e );
				if ( b.IsLocalOwned )
					SimStats.MispredictMine.Add( e );
			}
			SimStats.Mispredict.Add( err );
		}

		if ( err <= SkipEpsilon )
		{
			SimStats.Skipped++;
			NudgeRemote( s, auth );
			return;
		}
		Rollback( s, auth );
	}

	// A skipped snapshot still says where everyone ELSE is. Their bodies aren't replayed for small errors (a replay
	// re-steps OUR body from a restored state — see SkipEpsilon), so left alone they'd drift on their guessed input: put
	// each one at the host's state carried forward to our tick by its velocity. Only its sim body — what we collide with
	// — moves; it is drawn from the host's poses regardless (RemotePose).
	void NudgeRemote( int s, Dictionary<ushort, SimBodyState> auth )
	{
		foreach ( var b in _bodies )
		{
			if ( !b.IsRemoteDriven || !auth.TryGetValue( b.Id, out var a ) )
				continue;
			var st = a;
			st.P += a.V * (Dt * (Tick - s));
			if ( b.Phys.Position.Distance( st.P ) <= SkipEpsilon )
				continue;
			b.Restore( st );
			b.Prev = b.Cur = b.Phys.Transform;
			SimStats.RemoteNudges++;
		}
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

		_history.TryGetValue( Tick, out var oldNow );
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
			if ( len > 5f && SimStats.BigCorrections.Count < 30 )
			{
				var at = auth.TryGetValue( b.Id, out var a ) ? a : (SimBodyState?)null;
				var guessAt = predicted is not null && b.Id < predicted.Length ? predicted[b.Id] : null;
				SimStats.BigCorrections.Add( $"t{Tick} s{s} id{b.Id}{(b.IsLocalOwned ? " MINE" : "")}: {len:0.0}u; at s predicted {guessAt?.P:0.0} v {guessAt?.V:0} g {guessAt?.Ctrl.Grounded} / host {at?.P:0.0} v {at?.V:0} g {at?.Ctrl.Grounded} jumpT {at?.Ctrl.SinceJumpTicks}; now was {(oldNow is not null && b.Id < oldNow.Length ? oldNow[b.Id]?.P : null):0.0} yaw {(oldNow is not null && b.Id < oldNow.Length ? oldNow[b.Id]?.R.Angles().yaw : null):0.0} replayed {b.Phys.Position:0.0} yaw {b.Phys.Rotation.Angles().yaw:0.0}; visual before {before[i].Position:0.0} after {after.Position:0.0} alpha {_alpha:0.00}; prev {b.Prev.Position:0.0} cur {b.Cur.Position:0.0} off {b.PosOffset:0.0} rotoff {b.RotOffset.Angles().yaw:0.0}" );
			}
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
