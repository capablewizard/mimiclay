using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// A looping positional track that every player hears at the SAME moment, and that never restarts when the
/// object carrying it is swapped for a copy (possessing the lobby radio converts the scene prop into a pawn —
/// see <see cref="PropClaims"/>' CarryExtras). Use in place of a Sound Point for music.
/// </summary>
/// <remarks>
/// <para><b>Shared position.</b> The song position is a pure function of the scene clock: <c>Time.Now</c> is
/// host-synced on clients (the snapshot seeds it, the heartbeat keeps it converged), so
/// <c>Time.Now - StartedAt</c> is the same on every machine to within a ping. A fresh handle seeks there; a running
/// one is nudged back if it drifts past <see cref="DriftTolerance"/>.</para>
/// <para><b>Shared track.</b> A multi-file sound event is a playlist, and WHICH file plays is a function of the
/// clock too: the timeline is back-to-back cycles of every file, each cycle's order a shuffle seeded by
/// <see cref="Seed"/>, the cycle number and the channel (Random modes) or the list order (Forward/Backward).
/// Never <c>Sound.Play( SoundEvent )</c> — it rolls <c>Random.Shared</c> per machine, which is how clients ended
/// up on different songs. The only networked state is the run (<see cref="Playing"/>, <see cref="Seed"/>,
/// <see cref="StartedAt"/>), re-rolled at each start so every start is a new random track from its top.</para>
/// <para><b>Handoff.</b> Per machine, one handle per <see cref="Channel"/>, held by whichever instance enabled
/// last. A copy that enables while the original is still alive (host: same frame) or within
/// <see cref="ParkSeconds"/> of its death (clients: the destroy RPC and the pawn spawn arrive in either order)
/// adopts the playing handle — the audio never stops. An unadopted handle fades out once the park expires.</para>
/// <para>The import must loop (the .mp3/.wav meta "loop": true) — the handle loops itself; the restart in
/// OnUpdate is only a fallback. MP3 encoder padding can leave a tiny gap at the loop point: use WAV/OGG for a
/// truly gapless loop.</para>
/// </remarks>
[Title( "Synced Music" )]
[Category( "Audio" )]
[Icon( "radio" )]
public sealed class SyncedMusic : Component
{
	[Property] public SoundEvent SoundEvent { get; set; }

	[Property, Range( 0, 1 )] public float Volume { get; set; } = 1f;

	/// <summary>Instances sharing a channel share ONE handle (the handoff key). Empty = the sound event's path,
	/// which is right unless two radios in one scene play the same song.</summary>
	[Property] public string Channel { get; set; }

	/// <summary>How far (seconds) the playing handle may wander from the shared clock before it's re-seeked.
	/// Loose on purpose: a client's clock estimate jitters by a few ms, and a seek is an audible skip.</summary>
	[Property] public float DriftTolerance { get; set; } = 0.25f;

	/// <summary>Heard everywhere at full volume (background music) instead of from this object's position:
	/// no distance falloff, occlusion, reverb or air absorption, whatever the sound event says.</summary>
	[Property, Group( "Playback" )] public bool Global { get; set; }

	/// <summary>Mixer to play through instead of the sound event's own. The radio's event routes to the "radio"
	/// mixer (its RadioFilterProcessor is the tinny filter); background music picks "Music" here to play the same
	/// event clean. Empty = the event's mixer.</summary>
	[Property, Group( "Playback" )] public Sandbox.Audio.MixerHandle MixerOverride { get; set; }

	/// <summary>Seconds after this component enables before the music may start (on top of the load settle).
	/// The shared clock still decides the song position, so a delay never breaks sync.</summary>
	[Property, Group( "Playback" )] public float StartDelay { get; set; }

	/// <summary>Seconds to fade up from silence the first time this instance starts its music. 0 = straight in.
	/// Not reapplied on track changes or when a copy adopts an already-playing handle.</summary>
	[Property, Group( "Playback" )] public float FadeIn { get; set; }

	/// <summary>Switched on? Off fades the handle out but keeps the channel; back on starts a new run (fresh
	/// <see cref="Seed"/>: a new random track from the top). Changed on every machine at once by
	/// <see cref="SetPlaying"/>; late joiners get it from the live snapshot (scene objects ship the host's state,
	/// a converted pawn its owner's), and CarryExtras copies it across a possession.</summary>
	[Property] public bool Playing { get; set; } = true;

	/// <summary>Shuffle seed for the playlist, rolled fresh every time the music starts (session start, or switched
	/// on) so each start is a new random track. 0 = not rolled yet: the host rolls it, clients wait for it. Ships
	/// like <see cref="Playing"/> (broadcast + live snapshot + CarryExtras).</summary>
	[Property, Hide] public int Seed { get; set; }

	/// <summary>Shared-clock time (<c>Time.NowDouble</c>) the current run started: the playlist timeline is measured
	/// from here, so a fresh start begins at the top of its first track.</summary>
	[Property, Hide] public double StartedAt { get; set; }

	/// <summary>Anyone → everyone: switch the music on or off. Switching on rolls a new seed here, on the caller,
	/// so every machine applies the same one. A plain broadcast — a toggle needs no host arbitration (two players
	/// racing just end on whichever landed last, the same on every machine since the host relays in order).</summary>
	public void SetPlaying( bool on )
	{
		var (seed, at) = on ? RollStart() : (Seed, StartedAt);
		BroadcastPlaying( on, seed, at );
	}

	[Rpc.Broadcast]
	void BroadcastPlaying( bool on, int seed, double startedAt ) => ApplyPlaying( on, seed, startedAt );

	/// <summary>Set the state locally. Only call this from inside a broadcast that already carries the same
	/// arguments to every machine (e.g. <see cref="MusicToggleInteraction"/>'s), with values from <see cref="RollStart"/>.</summary>
	public void ApplyPlaying( bool on, int seed, double startedAt )
	{
		Playing = on;
		Seed = seed;
		StartedAt = startedAt;
	}

	/// <summary>A fresh seed + start time, rolled once by whoever starts the music and then broadcast.</summary>
	public static (int seed, double startedAt) RollStart() => (Random.Shared.Int( 1, int.MaxValue - 1 ), Time.NowDouble);

	const float ParkSeconds = 1f;
	const float DriftCheckInterval = 2f;

	sealed class Entry
	{
		public SoundHandle Handle;
		public SoundFile File; // the playlist file Handle is playing
		public int Seed;       // the run it was started for — a new run (re-roll) restarts it
		public double StartedAt;
		public SyncedMusic Holder;
	}

	// Per machine, by channel. Statics survive editor Stop→Play, so every read validates handle + holder.
	static readonly Dictionary<string, Entry> _channels = new();

	string Key => string.IsNullOrEmpty( Channel ) ? SoundEvent?.ResourcePath ?? "" : Channel;

	// Consecutive frames shorter than CalmFrameDelta needed before a fresh start (a hitching or loading frame resets the count; 10fps still qualifies).
	const int CalmFrames = 10;
	const float CalmFrameDelta = 0.1f;

	TimeUntil _nextDriftCheck;
	int _calmFrames;
	TimeSince _sinceEnabled;
	RealTimeSince? _fadingSince; // null until this instance first starts a handle

	// The fade-in multiplier on Volume: 0→1 over FadeIn from this instance's first start.
	float FadeScale => FadeIn <= 0f || _fadingSince is not { } t ? 1f : Math.Clamp( t / FadeIn, 0f, 1f );

	protected override void OnEnabled()
	{
		if ( Scene.IsEditor || SoundEvent is null )
			return;

		// Session start: the host rolls this run's track. A copy (CarryExtras) or a client that already has the
		// host's values from the snapshot keeps them; a client still at 0 waits (OnUpdate) for the broadcast.
		if ( Seed == 0 && Networking.IsHost )
		{
			var (seed, at) = RollStart();
			Seed = seed;
			StartedAt = at;
			if ( Networking.IsActive )
				BroadcastPlaying( Playing, seed, at );
		}

		var key = Key;
		if ( _channels.TryGetValue( key, out var e ) && e.Handle.IsValid() && e.Handle.IsPlaying )
		{
			e.Holder = this; // adopt — the previous holder (if still alive) stops driving it
			_fadingSince = null; // already audible: no fade
			return;
		}

		// Claim the channel but DON'T start yet — OnUpdate starts it once the scene has settled (see CalmFrames).
		_channels[key] = new Entry { Holder = this };
		_calmFrames = 0;
		_sinceEnabled = 0;
	}

	protected override void OnDisabled()
	{
		if ( !_channels.TryGetValue( Key, out var e ) || e.Holder != this )
			return; // never held it, or a copy already adopted it

		e.Holder = null;
		_ = ReapIfUnadopted( Key, e );
	}

	protected override void OnUpdate()
	{
		if ( !_channels.TryGetValue( Key, out var e ) || e.Holder != this )
			return;

		// Counted whether or not we're playing, so switching on after the load starts at once.
		_calmFrames = Time.Delta < CalmFrameDelta ? _calmFrames + 1 : 0;

		if ( !Playing || Seed == 0 )
		{
			if ( e.Handle.IsValid() )
				e.Handle.Stop( 0.5f );
			e.Handle = null;
			return;
		}

		// New run (switched back on before the fade finished, or a late seed arrived), or a track change (the clock
		// crossed into the next playlist entry): swap files. A looping import keeps the old file going until this
		// frame, so a track seam is at most a frame of its restart.
		var newRun = e.Seed != Seed || e.StartedAt != StartedAt;
		if ( e.Handle.IsValid() && !e.Handle.Finished && (newRun || Locate( out var due, out _ ) && due != e.File) )
		{
			e.Handle.Stop( 0.1f );
			e.Handle = null;
		}

		if ( !e.Handle.IsValid() || e.Handle.Finished )
		{
			// Wait out the load. Starting during it (OnEnabled runs inside Scene.Load) meant the first frame
			// after was a ~2s stall: the scene clock jumped 2s while the mixer — fed from the main thread —
			// barely advanced, so the first drift check found 1.35s of error and seeked forward. That jump was
			// the hiccup heard on load. A run of normal frames means the clock and the mixer move together.
			if ( _calmFrames >= CalmFrames && _sinceEnabled >= StartDelay )
			{
				_fadingSince ??= 0f;
				e.Handle = StartHandle( out e.File );
				e.Seed = Seed;
				e.StartedAt = StartedAt;
			}
			return;
		}

		e.Handle.Position = WorldPosition;
		e.Handle.Volume = Volume * FadeScale;

		if ( _nextDriftCheck <= 0f )
		{
			_nextDriftCheck = DriftCheckInterval;
			if ( Locate( out var file, out var offset ) && file == e.File )
			{
				// Circular difference, so a single-file loop seam (9.9s vs 0.1s) reads as 0.2s, not 9.8s.
				var length = file.Duration;
				var diff = MathF.Abs( e.Handle.Time - offset );
				diff = MathF.Min( diff, length - diff );
				if ( diff > DriftTolerance )
					e.Handle.Time = offset;
			}
		}
	}

	// Durations of every playlist file, or null until ALL are resident. Duration THROWS on an unloaded SoundFile
	// (its native VSound_t is null) — and at scene load it usually isn't loaded yet — so kick preloads and report
	// "not yet". Cached once complete; durations never change.
	float[] _durations;
	SoundEvent _durationsFor;

	float[] Durations
	{
		get
		{
			var files = SoundEvent?.Sounds;
			if ( files is null || files.Count == 0 )
				return null;

			if ( _durations is not null && _durationsFor == SoundEvent && _durations.Length == files.Count )
				return _durations;

			var ready = true;
			foreach ( var f in files )
			{
				if ( f is null )
					return null;
				if ( !f.IsLoaded )
				{
					f.Preload();
					ready &= f.IsLoaded;
				}
			}
			if ( !ready )
				return null;

			var d = files.Select( f => f.Duration ).ToArray();
			if ( d.Any( x => x <= 0f ) )
				return null;

			_durationsFor = SoundEvent;
			return _durations = d;
		}
	}

	int[] _order; // CycleOrder for _orderCycle — rebuilt once per playlist cycle, not per frame
	long _orderCycle;
	int _orderSeed;

	/// <summary>Which playlist file the shared clock says is playing right now, and how far into it. False until
	/// every file has loaded. Identical on every machine: the only inputs are the clock, the channel and the list.</summary>
	bool Locate( out SoundFile file, out float offset )
	{
		file = null;
		offset = 0f;

		var d = Durations;
		if ( d is null )
			return false;

		var files = SoundEvent.Sounds;
		double cycleLength = 0;
		foreach ( var x in d )
			cycleLength += x;

		// Clamped: a client's clock estimate can trail the starter's by a few ms right after a start.
		var now = Math.Max( 0.0, Time.NowDouble - StartedAt );
		var cycle = (long)Math.Floor( now / cycleLength );
		var t = now - cycle * cycleLength;

		if ( _order is null || _orderCycle != cycle || _orderSeed != Seed || _order.Length != files.Count )
		{
			_order = CycleOrder( cycle, files.Count );
			_orderCycle = cycle;
			_orderSeed = Seed;
		}

		var order = _order;
		foreach ( var i in order )
		{
			if ( t < d[i] )
			{
				file = files[i];
				offset = (float)t;
				return true;
			}
			t -= d[i];
		}

		// Float rounding at the very end of the cycle.
		var last = order[^1];
		file = files[last];
		offset = MathF.Max( 0f, d[last] - 0.001f );
		return true;
	}

	/// <summary>Play order of the playlist for one cycle of the timeline.</summary>
	int[] CycleOrder( long cycle, int count )
	{
		var order = Enumerable.Range( 0, count ).ToArray();
		switch ( SoundEvent.SelectionMode )
		{
			case SoundEvent.SoundSelectionMode.Forward:
				return order;
			case SoundEvent.SoundSelectionMode.Backward:
				Array.Reverse( order );
				return order;
		}

		Shuffle( order, cycle );

		// Don't play the same song twice in a row across a cycle boundary.
		if ( count > 1 )
		{
			var prev = Enumerable.Range( 0, count ).ToArray();
			Shuffle( prev, cycle - 1 );
			if ( order[0] == prev[^1] )
				(order[0], order[1]) = (order[1], order[0]);
		}
		return order;
	}

	// Fisher-Yates on a seeded SplitMix64 stream. Hand-rolled rather than System.Random / string.GetHashCode (the
	// latter is randomised per process) so every machine derives the same order.
	void Shuffle( int[] order, long cycle )
	{
		var state = StableHash( Key ) ^ ((ulong)(uint)Seed << 32) ^ ((ulong)cycle * 0x9E3779B97F4A7C15UL);
		for ( var i = order.Length - 1; i > 0; i-- )
		{
			var j = (int)(SplitMix( ref state ) % (ulong)(i + 1));
			(order[i], order[j]) = (order[j], order[i]);
		}
	}

	static ulong SplitMix( ref ulong state )
	{
		var z = state += 0x9E3779B97F4A7C15UL;
		z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
		z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
		return z ^ (z >> 31);
	}

	static ulong StableHash( string s )
	{
		var h = 14695981039346656037UL; // FNV-1a
		foreach ( var c in s.ToLowerInvariant() )
			h = (h ^ c) * 1099511628211UL;
		return h;
	}

	// Null until every file has loaded: starting before we know the lengths would play the wrong spot and then
	// jump at the first drift check — an audible skip. OnUpdate retries every frame meanwhile.
	SoundHandle StartHandle( out SoundFile file )
	{
		if ( !Locate( out file, out var offset ) )
			return null;

		// PlayFile, not Play( SoundEvent ): that rolls its own per-machine random file. So copy the event's
		// settings across by hand (mirrors the engine's Sound.Play). Pitch stays 1 — a random pitch would drift.
		var ev = SoundEvent;
		var h = Sound.PlayFile( file, Volume * FadeScale );
		if ( !h.IsValid() )
			return null;

		h.Position = WorldPosition;
		h.Distance = ev.Distance;
		h.Falloff = ev.Falloff;
		h.TargetMixer = string.IsNullOrEmpty( MixerOverride.Name ) ? ev.DefaultMixer.Get() : MixerOverride.Get();
		if ( Global )
		{
			h.ListenLocal = true;
			h.DistanceAttenuation = false;
			h.AirAbsorption = false;
			h.OcclusionEnabled = false;
			h.ReverbEnabled = false;
		}
		else if ( ev.UI )
		{
			h.ListenLocal = true;
			h.DistanceAttenuation = false;
			h.AirAbsorption = false;
			h.OcclusionEnabled = false;
			h.ReverbEnabled = false;
			h.TargetMixer ??= Sandbox.Audio.Mixer.FindMixerByName( "UI" );
		}
		else
		{
			h.DistanceAttenuation = ev.DistanceAttenuation;
			h.AirAbsorption = ev.AirAbsorption;
			h.OcclusionEnabled = ev.OcclusionEnabled;
			h.ReverbEnabled = ev.ReverbEnabled;
		}

		h.Time = offset;

		_nextDriftCheck = DriftCheckInterval;
		return h;
	}

	static async System.Threading.Tasks.Task ReapIfUnadopted( string key, Entry e )
	{
		await GameTask.DelayRealtimeSeconds( ParkSeconds );

		if ( e.Holder.IsValid() )
			return; // a copy picked it up

		e.Handle?.Stop( 0.5f );
		if ( _channels.TryGetValue( key, out var cur ) && cur == e )
			_channels.Remove( key );
	}
}
