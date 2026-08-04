using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mimiclay;

/// <summary>
/// Networks an <see cref="SdfSculpture"/>'s shape. Put it on a NETWORKED GameObject (a pawn) and point
/// <see cref="Target"/> at the sculpture to sync — a hider's disguise, a hunter's face, a future creative-mode
/// prop, anything. The owner publishes the brushes; every proxy applies them. It knows nothing about hunters or
/// props: its whole job is "serialise this sculpture's brushes and keep remotes in step."
///
/// The shape rides the wire two ways, mirroring how an authored shape actually changes:
/// <list type="bullet">
/// <item><b>Commit</b> (gizmo release / discrete edit): a durable <c>[Sync]</c> snapshot. Late-joiners get it in
/// the spawn snapshot, and it reconciles any dropped live frame to the exact final shape.</item>
/// <item><b>Live drag</b>: throttled unreliable RPC frames so proxies follow the handle, smoothed by per-frame
/// interpolation. After the first full frame of a drag, frames carry only the brushes that CHANGED since the
/// last tick (see <see cref="StreamDelta"/>) — a drag moves one brush, not forty. The full remesh waits for
/// the commit, and proxies march analytically (no field bakes) until it lands.</item>
/// </list>
///
/// <b>Hosting:</b> <c>[Sync]</c>/<c>[Rpc]</c> only work when this component's GameObject is networked, so it
/// belongs in the pawn PREFAB (present on every machine), not created at runtime. <see cref="Target"/> is local
/// config (not synced) — each machine binds it to its own local sculpture, set any time; binding is lazy.
/// </summary>
[Title( "SDF Network Sync" )]
[Category( "SDF" )]
[Icon( "sync" )]
public sealed class SdfNetworkSync : Component
{
	/// <summary>The sculpture whose brushes are networked. May be set at runtime (e.g. a runtime-cloned disguise);
	/// binding happens lazily, so it's fine for the host controller to assign this after this component starts.</summary>
	[Property] public SdfSculpture Target { get; set; }

	/// <summary>Smooth the streamed live-drag shape on proxies by blending each ~20 Hz sample to its successor
	/// instead of snapping. Owner-local authoring is unaffected. Off = snap on every sample.</summary>
	[Property] public bool Interpolate { get; set; } = true;

	/// <summary>Field-cache resolution multiplier applied on PROXIES while a remote live-drag streams in — the
	/// interpolated shape re-bakes its field every frame, and bake cost scales with the CUBE of resolution, so
	/// 0.5 makes each of those bakes 8× cheaper (a whole prep phase of remote sculptors ≈ one local edit).
	/// Restored to full resolution on commit, so the settled shape is always crisp; the cost of lowering this
	/// is softness (small brushes melt at coarse voxels) on shapes that are actively being dragged by someone
	/// else. 1 = bake remote drags at full authored resolution.</summary>
	[Property, Range( 0.1f, 1f )] public float LiveDragFieldScale { get; set; } = 0.5f;

	/// <summary>The other live-drag bake lever: minimum seconds between field re-bakes on a remotely-dragged
	/// proxy. The interpolated shape changes every frame, but re-baking faster than samples arrive (~20 Hz)
	/// buys nothing a viewer can keep — this caps the bake cadence at the stream rate instead of the frame
	/// rate. The visible trade: the marched surface steps sample-to-sample rather than gliding between them
	/// (the smoothed brush data still feeds everything non-baked). 0 = re-bake every frame the shape changes.
	/// Cleared along with the resolution scale on commit.</summary>
	[Property, Range( 0f, 0.25f )] public float LiveDragBakeInterval { get; set; } = 0.05f;

	/// <summary>The authored shape: the brush list serialised to a string (see <see cref="SdfSculpture.SerializeBrushes"/>).
	/// <c>[Sync]</c> owner-write so it rebroadcasts on every commit AND ships in the spawn snapshot (late-joiners see
	/// the current shape); <c>[Change]</c> applies it on proxies.</summary>
	[Sync, Change( nameof( OnDataChanged ) )] public string Data { get; set; }

	/// <summary>Monotonic shape version, bumped on every send (live + commit) so a proxy drops a stale update. It
	/// matters because live frames stream UNRELIABLY while the commit rides the reliable <c>[Sync]</c> channel.</summary>
	[Sync] public int Seq { get; set; }

	const float StreamInterval = 0.05f; // ~20 Hz while dragging
	RealTimeSince _sinceStream;
	int _sendSeq;     // owner: bumped per send (live + commit)
	int _appliedSeq;  // proxy: highest seq applied so far

	// Owner: the per-brush JSON of the last streamed shape, diffed each stream tick. Mid-drag only the grabbed
	// brush (plus a symmetry partner) actually changes, so the wire carries those brushes instead of the whole
	// list — a full 40-brush disguise at 20 Hz was hundreds of KB/s upstream, fanned out through the host to
	// every client, and prep phase has every hider dragging at once. Rebuilt on commit / count change.
	List<string> _streamCache;

	SdfSculpture _bound; // the Target we've currently subscribed to (lazy (re)bind in OnUpdate)

	SdfRaymarchRenderer _raymarcher; // proxy: resolved lazily to gate field baking during remote drags

	protected override void OnDestroy() => Unbind();

	bool _seededOnNetwork; // owner: have we published the shape we authored while still local? (deferred-network pawns)

	protected override void OnUpdate()
	{
		EnsureBound();

		// Owner sends through the Committed/Previewed callbacks; only proxies do per-frame work here.
		if ( !IsProxy )
		{
			// Deferred-network pawns (a prop sculpts its disguise LOCALLY through prep, then the pawn goes on the wire
			// at the hunt) author their shape before they're networked, so the Committed gate below swallowed it. The
			// moment we're networked, seed the durable [Sync] snapshot from the current brushes so proxies + the spawn
			// snapshot pick it up.
			if ( !_seededOnNetwork && GameObject.Network.Active && Target.IsValid() )
			{
				_seededOnNetwork = true;
				OnCommitted();
			}
			return;
		}

		// Reconcile the durable shape each frame. This is what makes a mid-session JOIN show the current shape: the
		// [Sync] snapshot can land AFTER we first bind, the engine doesn't fire OnNetworkSpawn on receivers, and the
		// [Change] callback isn't guaranteed for the initial replicated value. So we poll: if the committed version
		// is ahead of what we've applied, apply it. Cheap (an int compare), self-disabling once caught up; live
		// frames carry an equal-or-greater seq, so this never fights them.
		if ( Seq > _appliedSeq && !string.IsNullOrEmpty( Data ) )
			ApplyData();

		UpdateInterpolation();
	}

	// ── Target binding ──────────────────────────────────────────────────────────────────────────────────
	// Subscribe to the current Target's edit events, re-subscribing if Target changes. Lazy so the host can set
	// Target whenever (the disguise is a runtime clone). On first bind we apply whatever shape already arrived.
	void EnsureBound()
	{
		if ( _bound == Target )
			return;

		Unbind();
		_bound = Target;

		if ( _bound.IsValid() )
		{
			_bound.Committed += OnCommitted;
			_bound.Previewed += OnPreviewed;
			ApplyData(); // a pawn that bound already-sculpted (late-joiner / respawn) shows the right shape at once
		}
	}

	void Unbind()
	{
		if ( _bound.IsValid() )
		{
			_bound.Committed -= OnCommitted;
			_bound.Previewed -= OnPreviewed;
		}
		_bound = null;
	}

	// ── Owner -> everyone ─────────────────────────────────────────────────────────────────────────────────
	// Durable snapshot on commit (release / discrete edit). Gated to the owner (only it authors) and to networked
	// play. Proxies reach here too when they apply an incoming shape — its Rebuild fires Committed — but the
	// IsProxy gate stops them echoing it back.
	void OnCommitted()
	{
		// Gate on THIS object being networked, not just on a session existing: a pawn can be live-but-not-yet-networked
		// (infection prep spawns it locally and only publishes at the hunt), and authoring/streaming on a non-networked
		// object would fire RPCs with no network identity. Network.Active flips true exactly when it's published.
		if ( IsProxy || !GameObject.Network.Active || !Target.IsValid() )
			return;

		Seq = ++_sendSeq;
		Data = Pack( SdfSculpture.SerializeBrushes( Target.Brushes ) );
		_streamCache = null; // the commit is the new baseline; the next drag re-seeds the diff
	}

	// Live + throttled: fired on every drag-preview change; streams the in-progress brushes so proxies follow the
	// drag. Same owner/networked gates; proxies also fire Previewed (their cheap rebuild) but IsProxy stops re-streaming.
	void OnPreviewed()
	{
		if ( IsProxy || !GameObject.Network.Active || !Target.IsValid() )
			return;

		if ( _sinceStream < StreamInterval )
			return;

		var brushes = Target.Brushes;
		if ( brushes is not { Count: > 0 } )
			return;

		_sinceStream = 0f;

		// No baseline yet (first tick of a drag, or the count changed) → send the full list once and seed the diff.
		if ( _streamCache is null || _streamCache.Count != brushes.Count )
		{
			RefreshStreamCache( brushes );
			Stream( ++_sendSeq, Pack( SdfSculpture.SerializeBrushes( brushes ) ) );
			return;
		}

		// Diff against the last stream, brush by brush.
		List<int> changed = null;
		for ( int i = 0; i < brushes.Count; i++ )
		{
			var json = Json.Serialize( brushes[i] );
			if ( json == _streamCache[i] )
				continue;
			_streamCache[i] = json;
			(changed ??= new List<int>()).Add( i );
		}

		if ( changed is null )
			return; // nothing actually moved since the last tick — send nothing at all

		// Lots changed at once (a load, a palette applied across brushes) → the full list is cheaper than a delta.
		if ( changed.Count > Math.Max( 1, brushes.Count / 3 ) )
		{
			Stream( ++_sendSeq, Pack( SdfSculpture.SerializeBrushes( brushes ) ) );
			return;
		}

		var subset = new List<SdfBrush>( changed.Count );
		foreach ( var i in changed )
			subset.Add( brushes[i] );
		StreamDelta( ++_sendSeq, brushes.Count, string.Join( ',', changed ), Pack( SdfSculpture.SerializeBrushes( subset ) ) );
	}

	void RefreshStreamCache( List<SdfBrush> brushes )
	{
		_streamCache = new List<string>( brushes.Count );
		foreach ( var b in brushes )
			_streamCache.Add( Json.Serialize( b ) );
	}

	// Live drag frames: unreliable + dropped-if-delayed (only the latest matters) and never grouped with the
	// reliable channel, so a flurry can't stall gameplay RPCs. OwnerOnly so only the authoring client drives it.
	[Rpc.Broadcast( NetFlags.UnreliableNoDelay | NetFlags.OwnerOnly )]
	void Stream( int seq, string data )
	{
		if ( IsProxy )
			ApplyBrushes( seq, data, full: false );
	}

	// A live drag frame carrying ONLY the brushes that changed since the last stream tick (indices are a CSV into
	// the brush list, whose count rides along as a consistency check). Same channel + flags as Stream. Frames are
	// self-contained against the proxy's CURRENT list, so a dropped delta only costs smoothness — the next delta
	// (or the reliable commit) lands the same final values.
	[Rpc.Broadcast( NetFlags.UnreliableNoDelay | NetFlags.OwnerOnly )]
	void StreamDelta( int seq, int brushCount, string indices, string data )
	{
		if ( !IsProxy || !Target.IsValid() || seq <= _appliedSeq )
			return;

		// Structure diverged (we missed an add/remove, or haven't received the first full shape yet) — don't
		// guess; the reliable commit reconciles us.
		var current = Target.Brushes;
		if ( current is null || current.Count != brushCount )
			return;

		var subset = SdfSculpture.DeserializeBrushes( Unpack( data ) );
		if ( subset is null )
			return;

		var parts = indices.Split( ',', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != subset.Count )
			return;

		// The full interpolation target: the shape as we currently show it, with the changed slots replaced.
		var target = Snapshot( current );
		for ( int k = 0; k < parts.Length; k++ )
		{
			if ( !int.TryParse( parts[k], out var i ) || i < 0 || i >= target.Count )
				return; // malformed → ignore the frame, keep the standing shape
			target[i] = subset[k];
		}

		_appliedSeq = seq;
		SetInterpolationTarget( target );
		Target.RebuildShadowProxy();
		SetLiveDragField( true );
	}

	// [Change] callback for the durable snapshot, fired on every machine the synced value lands on. Only proxies
	// apply — the owner already has these exact brushes (it authored them).
	void OnDataChanged( string oldData, string newData )
	{
		if ( IsProxy )
			ApplyData();
	}

	// Apply the durable snapshot: a FULL rebuild (mesh all LODs + any collider/footprints via Committed) so a
	// remote sculpture is as solid and well-shadowed as it looks. This is the settled shape.
	void ApplyData() => ApplyBrushes( Seq, Data, full: true );

	// Deserialise brushes onto the local Target. Drops a stale/duplicate frame (seq not newer than applied) —
	// covers an out-of-order live packet landing after the commit. A malformed payload deserialises to null and is
	// ignored (and doesn't advance the seq), so the standing shape survives a bad frame.
	void ApplyBrushes( int seq, string data, bool full )
	{
		if ( !Target.IsValid() || seq <= _appliedSeq )
			return;

		var brushes = SdfSculpture.DeserializeBrushes( Unpack( data ) );
		if ( brushes is null )
			return;

		// Reject oversized payloads outright: authoring caps at MaxBrushes, so anything past it is a modified
		// client (raymarch cost is per-brush — a 500-brush payload taxes every machine that applies it).
		if ( brushes.Count > SdfBrushPacker.MaxBrushes )
			return;

		_appliedSeq = seq;

		if ( full )
		{
			StopInterpolation();
			CarryShrinkState( Target.Brushes, brushes );
			Target.Brushes = brushes;
			SetLiveDragField( false ); // settled shape → one full-resolution dispatch, back to the crisp field
			Target.Rebuild();
		}
		else
		{
			SetInterpolationTarget( brushes );
			Target.RebuildShadowProxy();
			SetLiveDragField( true );
		}
	}

	// While a remote drag streams in, drop the field bake to LiveDragFieldScale of authored resolution and cap
	// its cadence at LiveDragBakeInterval — together they turn "full volume × every frame × every sculpting
	// hider" into a small bake at stream rate, cheap enough for a whole prep phase at once. (This replaced
	// suppressing the field entirely and falling back to the analytic per-brush march, whose cost blows up when
	// a mid-drag prop is close/large on screen — and which dropped the highlight outline, since that marches
	// baked fields only.)
	void SetLiveDragField( bool live )
	{
		if ( !_raymarcher.IsValid() )
			_raymarcher = Target.IsValid() ? Target.GameObject.Components.Get<SdfRaymarchRenderer>() : null;
		if ( !_raymarcher.IsValid() )
			return;
		_raymarcher.FieldResolutionScale = live ? LiveDragFieldScale : 1f;
		_raymarcher.FieldRebakeInterval = live ? LiveDragBakeInterval : 0f;
	}

	// ── Live-drag interpolation (proxy only) ──────────────────────────────────────────────────────────────
	// Blends each streamed brush sample over the stream interval to the next, so a dragged shape morphs smoothly
	// instead of stepping at sample rate. Only the raymarched surface is driven per-frame (a cheap brush-data
	// repack); the shadow mesh + collision stay on the sample/commit path. Geometry interpolates, material/structure
	// snap — see SdfBrush.LerpFrom.
	List<SdfBrush> _lerpFrom, _lerpTo, _lerpResult;
	RealTimeSince _sinceSample;

	void SetInterpolationTarget( List<SdfBrush> target )
	{
		CarryShrinkState( Target.IsValid() ? Target.Brushes : null, target );

		// Snap when interpolation is off, or when the brush COUNT changes (a brush added/removed mid-drag has
		// nothing to blend against). Stops any in-flight blend so the next frame doesn't fight the snap.
		if ( !Interpolate || Target.Brushes is not { } current || current.Count != target.Count )
		{
			StopInterpolation();
			Target.Brushes = target;
			return;
		}

		// Ease from wherever we are NOW (current may itself be mid-blend) to the new sample — no snap on resync.
		_lerpFrom = Snapshot( current );
		_lerpTo = target;
		if ( _lerpResult is null || _lerpResult.Count != target.Count )
			_lerpResult = Snapshot( target ); // a mutable list of the right size; values overwritten each frame
		_sinceSample = 0f;
		Target.Brushes = _lerpResult;
	}

	void UpdateInterpolation()
	{
		if ( _lerpTo is null || _lerpFrom is null || _lerpResult is null || !Target.IsValid() )
			return;

		// Defensive: a count mismatch means a snap happened out from under us — bail.
		if ( _lerpFrom.Count != _lerpResult.Count || _lerpTo.Count != _lerpResult.Count )
		{
			StopInterpolation();
			return;
		}

		float t = StreamInterval > 0f ? Math.Clamp( (float)_sinceSample / StreamInterval, 0f, 1f ) : 1f;
		for ( int i = 0; i < _lerpResult.Count; i++ )
			_lerpResult[i].LerpFrom( _lerpFrom[i], _lerpTo[i], t );

		// Reached the target: rest Brushes on it and stop (a settled shape then costs nothing per-frame — the
		// raymarcher's hash sees no change and skips its repack until the next sample lands).
		if ( t >= 1f )
		{
			_lerpFrom = null;
			_lerpTo = null;
		}
	}

	void StopInterpolation()
	{
		_lerpFrom = null;
		_lerpTo = null;
	}

	// Carry LOCAL shrink-animation clocks (SdfBrush.ShrinkAge/ShrinkState — runtime fields, deliberately not
	// serialised) across an incoming list replace, so a new commit (e.g. another shot landing) doesn't reset
	// every already-healing crater back into its grace period — heals continue instead of pausing. Brushes
	// are matched by POSITION (stable — only sizes animate); copying ShrinkState (the cached pre-shrink size)
	// with the age keeps the animation seamless regardless of the mid-animation size the owner serialised.
	static void CarryShrinkState( List<SdfBrush> from, List<SdfBrush> to )
	{
		if ( from is null || to is null )
			return;

		foreach ( var nb in to )
		{
			if ( !nb.Shrinks )
				continue;

			foreach ( var ob in from )
			{
				if ( !ob.Shrinks || ob.ShrinkAge <= 0f || ob.Damage != nb.Damage )
					continue;
				if ( ob.Position.Distance( nb.Position ) > 0.01f )
					continue;

				nb.ShrinkAge = ob.ShrinkAge;
				nb.ShrinkState = ob.ShrinkState;
				break;
			}
		}
	}

	static List<SdfBrush> Snapshot( List<SdfBrush> brushes )
	{
		var copy = new List<SdfBrush>( brushes.Count );
		foreach ( var b in brushes )
			copy.Add( b.Copy() );
		return copy;
	}

	// ── Wire encoding ─────────────────────────────────────────────────────────────────────────────────────
	// Brush JSON is ~500 bytes/brush, most of it field names repeated per brush — the ideal gzip case — so
	// every payload this component sends (the [Sync] commit snapshot, live Stream frames, StreamDelta subsets)
	// is gzip+base64'd: ~4-6× smaller even after base64's 4/3 inflation. This is what made raising the brush
	// cap to 128 affordable — the commit payload scales linearly with the cap. Wire-format only: .sculpt saves
	// and everything else keep using SerializeBrushes' plain JSON. Unpack auto-detects a plain-JSON payload
	// (it starts with '['), so a mixed-version peer or a legacy snapshot still applies.
	static string Pack( string json )
	{
		if ( string.IsNullOrEmpty( json ) )
			return json;

		var raw = Encoding.UTF8.GetBytes( json );
		using var ms = new MemoryStream();
		using ( var gz = new GZipStream( ms, CompressionLevel.Fastest, leaveOpen: true ) )
			gz.Write( raw, 0, raw.Length );
		return Convert.ToBase64String( ms.ToArray() );
	}

	static string Unpack( string data )
	{
		if ( string.IsNullOrEmpty( data ) )
			return data;

		if ( data[0] == '[' )
			return data; // plain JSON brush list (legacy / mixed-version peer)

		try
		{
			var bytes = Convert.FromBase64String( data );
			using var ms = new MemoryStream( bytes );
			using var gz = new GZipStream( ms, CompressionMode.Decompress );
			using var reader = new StreamReader( gz, Encoding.UTF8 );
			return reader.ReadToEnd();
		}
		catch
		{
			return null; // malformed payload → callers treat like any bad frame (ignore, keep standing shape)
		}
	}
}
