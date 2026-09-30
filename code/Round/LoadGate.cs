using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Host-side "wait for everyone to load" check that a game manager runs before its first real phase, so the
/// round doesn't start while slower machines are still on the loading screen or baking props.
///
/// A connection counts as loaded once:
/// <list type="bullet">
/// <item>the engine says so (<see cref="Connection.IsActive"/>: the scene load + snapshot handshake is done), and</item>
/// <item>its latest <see cref="ClientStatusReporter"/> report is recent, names THIS game manager (its networked
/// GameObject id, so a leftover lobby report can't pass), and is <see cref="ClientStatus.Settled"/> (no SDF work
/// pending for <see cref="SettleSeconds"/>).</item>
/// </list>
/// The host checks itself the same way, locally. Once someone counts as loaded they stay counted, so a later rebuild
/// (a disguise arriving, an edit) never makes them "unload".
///
/// The gate opens when every LIVE connection has loaded. A player who crashes or quits drops out of
/// <see cref="Connection.All"/> and stops blocking straight away; one who is still connected but stuck (frozen,
/// heartbeat gone quiet) only holds things up until <see cref="TimeoutSeconds"/>, after which the game starts
/// without them and they join late like any mid-round joiner.
///
/// Expected players are simply whoever is connected. Connections survive <see cref="Game.ChangeScene"/>, so
/// everyone from the lobby is already in the list, and someone joining during the wait is waited for too.
/// Solo / offline, it opens on the first tick.
/// </summary>
public sealed class LoadGate
{
	/// <summary>How long a machine's SDF work must stay at zero before it counts as settled.</summary>
	public const float SettleSeconds = 1.5f;

	/// <summary>Longest the gate will hold for a straggler before starting without them.</summary>
	public const float TimeoutSeconds = 30f;

	// A client report older than this is ignored (the reporter heartbeats every 2s, so it has gone quiet).
	const float StaleSeconds = 5f;

	readonly Scene _scene;
	readonly Guid _managerId;
	readonly double _startedAt = RealTime.Now;
	readonly HashSet<Guid> _loaded = new();
	RealTimeSince _hostSinceBusy;

	const float DiagSeconds = 3f;
	RealTimeSince _sinceDiag;

	/// <param name="manager">The networked game manager's GameObject; clients must report having it.</param>
	public LoadGate( GameObject manager )
	{
		_scene = manager.Scene;
		_managerId = manager.Id;
		_hostSinceBusy = 0;
	}

	/// <summary>Latched true once everyone has loaded or the timeout hit.</summary>
	public bool IsOpen { get; private set; }

	/// <summary>Live connections counted as loaded, as of the last <see cref="Tick"/>.</summary>
	public int Loaded { get; private set; }

	/// <summary>Live connections, as of the last <see cref="Tick"/>.</summary>
	public int Expected { get; private set; }

	/// <summary>Seconds left before the gate gives up on stragglers.</summary>
	public float TimeLeft => MathF.Max( 0f, TimeoutSeconds - (float)(RealTime.Now - _startedAt) );

	/// <summary>Host-only, every frame until it returns true. True once the game may start.</summary>
	public bool Tick()
	{
		if ( IsOpen )
			return true;

		// Solo / offline, or nobody else here: nothing to wait for.
		if ( !Networking.IsActive || Connection.All.Count( c => c != Connection.Local ) == 0 )
			return Open( "no other players" );

		if ( _scene.IsLoading || ClientStatusReporter.PendingSdfWork( _scene ) > 0 )
			_hostSinceBusy = 0;

		var live = Connection.All.ToList();
		foreach ( var c in live )
		{
			if ( !_loaded.Contains( c.Id ) && IsLoaded( c ) )
				_loaded.Add( c.Id );
		}

		Expected = live.Count;
		Loaded = live.Count( c => _loaded.Contains( c.Id ) );

		if ( Loaded >= Expected )
			return Open( $"all {Expected} players loaded" );

		if ( _sinceDiag >= DiagSeconds )
		{
			_sinceDiag = 0;
			var waiting = live.Where( c => !_loaded.Contains( c.Id ) ).Select( c => $"{c.DisplayName}: {WhyNotLoaded( c )}" );
			Log.Info( $"LoadGate: waiting on {Expected - Loaded}/{Expected} — {string.Join( " | ", waiting )}" );
		}

		if ( TimeLeft <= 0f )
		{
			var missing = live.Where( c => !_loaded.Contains( c.Id ) ).Select( c => c.DisplayName );
			return Open( $"timed out after {TimeoutSeconds:0}s — starting without {string.Join( ", ", missing )}" );
		}

		return false;
	}

	bool IsLoaded( Connection c )
	{
		if ( c == Connection.Local )
			return _hostSinceBusy >= SettleSeconds;

		if ( !c.IsActive || !ClientStatusBoard.TryGet( c.Id, out var s ) )
			return false;

		// Must name THIS manager: the client's last lobby report (or one from an earlier visit to this same map)
		// can still be sitting in the board when the gate starts. Not a scene-name match: names differ per
		// machine (the host's reads "system", a client's the default "Scene"), which timed out every client.
		return s.Settled
			&& s.Manager == _managerId
			&& RealTime.Now - s.At < StaleSeconds;
	}

	// Diagnostic: every failing condition for one connection, so a gate that always times out says why.
	string WhyNotLoaded( Connection c )
	{
		if ( c == Connection.Local )
			return $"host busy (loading={_scene.IsLoading}, sdf pending={ClientStatusReporter.PendingSdfWork( _scene )}, quiet {(float)_hostSinceBusy:0.0}s)";

		var why = new List<string>();
		if ( !c.IsActive )
			why.Add( c.IsConnecting ? "engine: still connecting" : "engine: not active (loading scene)" );

		if ( !ClientStatusBoard.TryGet( c.Id, out var s ) )
		{
			why.Add( "no status report" );
			return string.Join( ", ", why );
		}

		why.Add( $"stage {s.Stage}, pending {s.PendingProps}" );
		if ( !s.Settled ) why.Add( "not settled" );
		if ( s.Manager != _managerId ) why.Add( s.Manager == Guid.Empty ? "no game manager yet" : "report is from another scene's manager" );
		if ( RealTime.Now - s.At >= StaleSeconds ) why.Add( $"report stale {RealTime.Now - s.At:0.0}s" );
		return string.Join( ", ", why );
	}

	bool Open( string why )
	{
		IsOpen = true;
		Log.Info( $"LoadGate: starting — {why} ({RealTime.Now - _startedAt:0.0}s)." );
		return true;
	}
}
