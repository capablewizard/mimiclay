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
/// host-synced on clients (the snapshot seeds it, the heartbeat keeps it converged), so <c>Time.Now % length</c>
/// is the same on every machine to within a ping. A fresh handle seeks there; a running one is nudged back if
/// it drifts past <see cref="DriftTolerance"/>. Nothing is networked — joiners land in step for free.</para>
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

	const float ParkSeconds = 1f;
	const float DriftCheckInterval = 2f;

	sealed class Entry
	{
		public SoundHandle Handle;
		public SyncedMusic Holder;
	}

	// Per machine, by channel. Statics survive editor Stop→Play, so every read validates handle + holder.
	static readonly Dictionary<string, Entry> _channels = new();

	string Key => string.IsNullOrEmpty( Channel ) ? SoundEvent?.ResourcePath ?? "" : Channel;

	TimeUntil _nextDriftCheck;

	protected override void OnEnabled()
	{
		if ( Scene.IsEditor || SoundEvent is null )
			return;

		var key = Key;
		if ( _channels.TryGetValue( key, out var e ) && e.Handle.IsValid() && e.Handle.IsPlaying )
		{
			e.Holder = this; // adopt — the previous holder (if still alive) stops driving it
			return;
		}

		_channels[key] = new Entry { Handle = StartHandle(), Holder = this };
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

		if ( !e.Handle.IsValid() || e.Handle.Finished )
		{
			e.Handle = StartHandle();
			return;
		}

		e.Handle.Position = WorldPosition;
		e.Handle.Volume = Volume;

		if ( _nextDriftCheck <= 0f )
		{
			_nextDriftCheck = DriftCheckInterval;
			var length = Length;
			if ( length > 0f )
			{
				// Circular difference, so the loop seam (9.9s vs 0.1s) reads as 0.2s, not 9.8s.
				var diff = MathF.Abs( e.Handle.Time - SharedTime( length ) );
				diff = MathF.Min( diff, length - diff );
				if ( diff > DriftTolerance )
					e.Handle.Time = SharedTime( length );
			}
		}
	}

	float Length => SoundEvent?.Sounds?.FirstOrDefault()?.Duration ?? 0f;

	static float SharedTime( float length ) => (float)(Time.NowDouble % length);

	SoundHandle StartHandle()
	{
		var h = Sound.Play( SoundEvent, WorldPosition );
		if ( !h.IsValid() )
			return h;

		h.Volume = Volume;
		var length = Length;
		if ( length > 0f )
			h.Time = SharedTime( length );

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
