using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Re-asserts a networked manager's full [Sync] state to every client — the heal for the engine dropping
/// reliable table deltas at a machine that is mid-scene-load.
///
/// <b>The engine bug.</b> A <c>NetDictionary</c> / <c>NetList</c> replicates as a CHANGE LIST (Add / Remove /
/// Replace per key, <c>INetworkReliable</c>), broadcast once to every connection at <c>ChannelState.Snapshot</c>
/// or later. A client that has been handed its snapshot but whose <c>Game.ActiveScene</c> is still null (the
/// snapshot stream is still downloading / about to apply) DROPS any such message — the receiver bails when the
/// scene or the object isn't there — while the sender has already cleared the change list. Unlike plain [Sync]
/// values, which the delta-snapshot path re-asserts by hash every tick, a dropped container change is
/// <b>never resent</b>: a key whose Add was in that message simply doesn't exist on that machine until the host
/// happens to write a DIFFERENT value to the same key (an equal-value write is skipped before it ever becomes
/// a change). Live symptom: the slowest-loading machine has a roster missing rows — its own (so it can never
/// spawn a pawn) and anyone else's that wasn't rewritten since — and a correct guess (a score write to its own
/// row) "magically" gives it a body.
///
/// <b>The heal.</b> <see cref="GameObject.NetworkAccessor.Refresh"/> ships the object's FULL table state
/// (<c>WriteAll</c>: every container key) to every client, and a receiver applies it as a full replace. The host
/// calls it (<see cref="OnPlayerActive"/>) the moment a connection finishes loading — the engine's
/// <c>INetworkListener.OnActive</c>, which fires after the client's scene is valid, so nothing sent from here on
/// can be dropped — and on request (<see cref="Refresh"/>) when a client's own watchdog (<see cref="RowWatch"/>)
/// notices its roster is incomplete. Rate-limited per object; the managers are tiny, so a refresh is cheap.
/// </summary>
public static class NetStateResync
{
	const float MinGapSeconds = 1.5f;

	static readonly Dictionary<Guid, RealTimeSince> _lastRefresh = new();

	/// <summary>Host-side <c>INetworkListener.OnActive</c> hook: a connection finished loading the scene (or
	/// joined) — re-send this manager's full state so anything it missed while loading is replaced.</summary>
	public static void OnPlayerActive( Component manager, Connection c )
	{
		if ( !Networking.IsActive || !Networking.IsHost || c is null || c == Connection.Local )
			return;

		// Never rate-limited: several clients finishing a load within a second of each other is the NORMAL
		// case (a scene change), and the last one in is exactly the machine that missed the most.
		Refresh( manager, $"{c.DisplayName} finished loading", force: true );
	}

	/// <summary>Host-only: re-send <paramref name="manager"/>'s object (its [Sync] values and every container
	/// key) to all clients. No-op on clients, offline, for an unnetworked object, or — unless
	/// <paramref name="force"/> — within the rate limit (client requests are limited; load-completions aren't).</summary>
	public static bool Refresh( Component manager, string why, bool force = false )
	{
		if ( !Networking.IsActive || !Networking.IsHost || !manager.IsValid() )
			return false;

		var go = manager.GameObject;
		if ( !go.IsValid() || !go.Network.Active )
			return false;

		if ( !force && _lastRefresh.TryGetValue( go.Id, out var since ) && since < MinGapSeconds )
			return false;
		_lastRefresh[go.Id] = 0f;

		Log.Info( $"NetStateResync: re-sending {go.Name} state to every client — {why}." );
		go.Network.Refresh();
		return true;
	}

	/// <summary>
	/// Client-side watchdog for "my copy of the roster is incomplete". Feed it <c>complete</c> every frame; it
	/// says <c>true</c> when it's time to ask the host for a re-send: the state has been incomplete for a grace
	/// period (a fresh snapshot can legitimately be a beat behind), with a backoff between asks and a cap on
	/// how many asks (so a client whose incompleteness is LEGITIMATE — a prop-hunt late joiner with no role —
	/// costs the host a bounded number of refreshes).
	/// </summary>
	public sealed class RowWatch
	{
		const float GraceSeconds = 4f;
		const float BackoffSeconds = 8f;

		RealTimeSince _incompleteFor = 0f;
		RealTimeUntil _backoff = 0f;
		int _attempts;

		/// <param name="complete">Whether this machine's copy currently looks whole.</param>
		/// <param name="maxAttempts">How many re-send requests to make before giving up (until it is next
		/// complete, which resets the count).</param>
		public bool Tick( bool complete, int maxAttempts = int.MaxValue )
		{
			if ( complete )
			{
				_incompleteFor = 0f;
				_attempts = 0;
				return false;
			}

			if ( _incompleteFor < GraceSeconds || _backoff > 0f || _attempts >= maxAttempts )
				return false;

			_backoff = BackoffSeconds;
			_attempts++;
			return true;
		}
	}
}
