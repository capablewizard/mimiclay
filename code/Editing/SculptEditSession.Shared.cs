using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The SHARED half of the edit session: editing a HOST-owned sculpt alongside other players (the in-place
/// sculpting of released creative props — see <see cref="PropClaims.Editing"/>). The session edits its local
/// copy exactly as always; this partial adds the three things that make several such sessions agree:
/// <list type="bullet">
/// <item><b>Publishing.</b> Every commit / preview the funnel runs is diffed BY BRUSH ID against what was last
/// sent, and the difference goes to the host as a brush op (<see cref="PropClaims.SubmitBrushes"/> reliable on
/// commit, <see cref="PropClaims.StreamBrushes"/> unreliable at 20 Hz mid-gesture). The host applies it and
/// its sync publishes to everyone. Nothing is published while an incoming shape is being applied
/// (<see cref="SdfNetworkSync.Applying"/>) — that's the host's shape landing, not an edit.</item>
/// <item><b>Locks.</b> The selection IS the lock set: selecting asks the host for those brushes' locks,
/// deselecting gives them back, and a brush another editor holds is dropped from the selection (and can't be
/// hovered). Optimistic — the local pick happens at once; the registry corrects it a round-trip later.</item>
/// <item><b>Merging.</b> When the host's shape lands, <see cref="MergeIncoming"/> keeps the LOCAL instances of
/// the brushes this session owns right now (locked, or changed within the last moment and not yet echoed,
/// or the pending stamp ghost) and takes everything else from the host, so a drag in progress is never
/// yanked by someone else's commit, and the gizmo's brush reference survives. The selection is re-seated by
/// id afterwards (<see cref="AfterExternalApply"/>).</item>
/// </list>
/// Undo is scoped the same way: a step restores only the brushes THIS session has touched, by id.
/// </summary>
public sealed partial class SculptEditSession
{
	/// <summary>Edit a HOST-owned sculpt alongside other players: publish edits as brush ops, lock what's
	/// selected, merge incoming shapes by id. Set by the host pawn for in-place sculpting; the possession and
	/// orbit sessions (one owner, one author) leave it off.</summary>
	[Property] public bool Shared { get; set; }

	/// <summary>The shared session editing <paramref name="sculpture"/> on this machine, if any — the sync asks
	/// before landing an incoming shape on it.</summary>
	public static SculptEditSession SharedSessionOn( SdfSculpture sculpture )
		=> Current is { } c && c.IsValid() && c.IsEditing && c.Shared && c.Target == sculpture ? c : null;

	// What was last SENT, per authored brush id — the commit diff baseline, and the preview (stream) baseline,
	// kept apart: a preview that updated one shared cache would hide its brushes from the commit diff.
	readonly Dictionary<Guid, string> _sharedCommitCache = new();
	readonly Dictionary<Guid, string> _sharedStreamCache = new();
	readonly List<Guid> _sharedOrder = new();        // authored order as last committed
	readonly HashSet<Guid> _sharedTouched = new();   // every id this session has ever changed — the undo scope
	readonly Dictionary<Guid, RealTimeSince> _sharedPending = new();        // local ops not yet echoed by the host
	readonly Dictionary<Guid, RealTimeSince> _sharedPendingRemoved = new(); // local removals not yet echoed
	readonly HashSet<Guid> _sharedLocks = new();     // locks requested / held for the selection
	RealTimeSince _sinceSharedStream;
	SdfSculpture _sharedBound;

	// Selection captured by id across an external apply (MergeIncoming → AfterExternalApply).
	readonly List<Guid> _sharedSelIds = new();
	Guid? _sharedPrimaryId;

	const float SharedStreamInterval = 0.05f; // 20 Hz, like SdfNetworkSync's own stream
	const float SharedPendingHold = 1.5f;     // how long an unechoed local change out-ranks the host's copy

	// The networked object the host resolves the clay from: the pawn root (scene clay never gets here — a
	// shared edit converts it to a pawn first).
	GameObject SharedRoot
	{
		get
		{
			if ( !Target.IsValid() )
				return null;
			var hider = Target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
			return hider.IsValid() ? hider.GameObject : Target.GameObject;
		}
	}

	void BeginShared()
	{
		EndShared();
		if ( !Target.IsValid() )
			return;

		_sharedBound = Target;
		_sharedBound.Committed += OnSharedCommitted;
		_sharedBound.Previewed += OnSharedPreviewed;
		SeedSharedCaches();
	}

	void EndShared()
	{
		if ( _sharedBound.IsValid() )
		{
			_sharedBound.Committed -= OnSharedCommitted;
			_sharedBound.Previewed -= OnSharedPreviewed;
		}
		_sharedBound = null;

		if ( _sharedLocks.Count > 0 && PropClaims.Current is { } claims )
			claims.UnlockBrushes( Csv( _sharedLocks ) );
		_sharedLocks.Clear();
		_sharedCommitCache.Clear();
		_sharedStreamCache.Clear();
		_sharedOrder.Clear();
		_sharedPending.Clear();
		_sharedPendingRemoved.Clear();
		_sharedKnown.Clear();
		_sharedSelIds.Clear();
		_sharedPrimaryId = null;
	}

	// The brushes this session publishes: the whole authored prefix, pending stamp ghost INCLUDED — it's
	// collaborative, so the shape you're about to stamp shows up live on everyone's screen (streamed as it
	// rides the crosshair, committed when it lands, removed when you cancel). It's locked while held so
	// nobody else can grab it mid-placement.
	IEnumerable<SdfBrush> SharedAuthored()
	{
		var b = Target.Brushes;
		if ( b is null )
			yield break;
		int n = Target.AuthoredBrushCount;
		for ( int i = 0; i < n && i < b.Count; i++ )
			yield return b[i];
	}

	// Every brush id the host has been told about (by commit OR stream) — the removal baseline. A ghost
	// only ever streams, so a commit-cache-only baseline would never notice it being cancelled.
	readonly HashSet<Guid> _sharedKnown = new();

	void SeedSharedCaches()
	{
		_sharedCommitCache.Clear();
		_sharedStreamCache.Clear();
		_sharedOrder.Clear();
		_sharedKnown.Clear();
		foreach ( var b in SharedAuthored() )
		{
			var json = Json.Serialize( b );
			_sharedCommitCache[b.Id] = json;
			_sharedStreamCache[b.Id] = json;
			_sharedOrder.Add( b.Id );
			_sharedKnown.Add( b.Id );
		}
	}

	// A commit landed on the sculpture: diff against the last commit, send what changed, what went, and the
	// order if it moved. Skipped while the sync is applying the host's shape (that's not our edit).
	void OnSharedCommitted()
	{
		if ( SdfNetworkSync.Applying || !_sharedBound.IsValid() || !Target.IsValid() || PropClaims.Current is not { } claims )
			return;
		var root = SharedRoot;
		if ( !root.IsValid() )
			return;

		var changed = new List<SdfBrush>();
		var order = new List<Guid>();
		var seen = new HashSet<Guid>();
		foreach ( var b in SharedAuthored() )
		{
			seen.Add( b.Id );
			order.Add( b.Id );
			var json = Json.Serialize( b );
			if ( !_sharedCommitCache.TryGetValue( b.Id, out var last ) || last != json )
			{
				changed.Add( b );
				_sharedCommitCache[b.Id] = json;
				_sharedStreamCache[b.Id] = json;
				_sharedKnown.Add( b.Id );
				Touch( b.Id );
			}
		}

		var removed = _sharedKnown.Where( id => !seen.Contains( id ) ).ToList();
		foreach ( var id in removed )
		{
			_sharedKnown.Remove( id );
			_sharedCommitCache.Remove( id );
			_sharedStreamCache.Remove( id );
			_sharedPending.Remove( id );
			_sharedPendingRemoved[id] = 0f;
			_sharedTouched.Add( id );
		}

		string orderCsv = order.SequenceEqual( _sharedOrder ) ? null : Csv( order );
		_sharedOrder.Clear();
		_sharedOrder.AddRange( order );

		if ( changed.Count == 0 && removed.Count == 0 && orderCsv is null )
			return;

		claims.SubmitBrushes( root,
			changed.Count > 0 ? SdfNetworkSync.Pack( SdfSculpture.SerializeBrushes( changed ) ) : null,
			removed.Count > 0 ? Csv( removed ) : null,
			orderCsv );
	}

	// A preview landed (a handle / scrub / carry / slider mid-gesture): stream the brushes that moved since
	// the last stream tick, throttled. Removals and order wait for the commit.
	void OnSharedPreviewed()
	{
		if ( SdfNetworkSync.Applying || !_sharedBound.IsValid() || !Target.IsValid() || PropClaims.Current is not { } claims )
			return;
		if ( _sinceSharedStream < SharedStreamInterval )
			return;
		var root = SharedRoot;
		if ( !root.IsValid() )
			return;

		List<SdfBrush> changed = null;
		foreach ( var b in SharedAuthored() )
		{
			var json = Json.Serialize( b );
			if ( _sharedStreamCache.TryGetValue( b.Id, out var last ) && last == json )
				continue;
			_sharedStreamCache[b.Id] = json;
			_sharedKnown.Add( b.Id );
			Touch( b.Id );
			(changed ??= new()).Add( b );
		}

		if ( changed is null )
			return;

		_sinceSharedStream = 0f;
		claims.StreamBrushes( root, SdfNetworkSync.Pack( SdfSculpture.SerializeBrushes( changed ) ) );
	}

	void Touch( Guid id )
	{
		_sharedTouched.Add( id );
		_sharedPending[id] = 0f;
	}

	// Is this brush OURS right now — locked by us, changed by us within the hold and not yet echoed, or the
	// pending stamp ghost? Ours keep their local instance when the host's shape lands.
	bool IsMineShared( Guid id )
	{
		if ( _sharedLocks.Contains( id ) )
			return true;
		if ( _sharedPending.TryGetValue( id, out var since ) && since < SharedPendingHold )
			return true;
		return StampBrush is { } ghost && ghost.Id == id;
	}

	/// <summary>Fold the host's shape into ours: the host's list, with our own brushes (see
	/// <c>IsMineShared</c>) kept as the LOCAL instances we're mid-edit on, our not-yet-echoed removals left
	/// out, and our not-yet-echoed additions (and the stamp ghost) kept in at the authored end. Called by the
	/// sync BEFORE it assigns the list; <see cref="AfterExternalApply"/> follows the assignment.</summary>
	/// <summary>Remember the selection by id — the indices are about to mean something else (a list swap
	/// follows; <see cref="AfterExternalApply"/> re-seats them). <see cref="MergeIncoming"/> calls this itself;
	/// the host's op path calls it directly before swapping its own copy.</summary>
	internal void CaptureSelectionIds()
	{
		_sharedSelIds.Clear();
		foreach ( var sb in SelectedBrushes )
			_sharedSelIds.Add( sb.Id );
		_sharedPrimaryId = SelectedBrush?.Id;
	}

	internal List<SdfBrush> MergeIncoming( List<SdfBrush> incoming )
	{
		var local = Target.IsValid() ? Target.Brushes : null;
		local ??= new List<SdfBrush>();

		CaptureSelectionIds();

		// Expire the holds: an echo that never came (a rejected op) must not pin stale local state forever.
		foreach ( var id in _sharedPending.Where( kv => kv.Value >= SharedPendingHold ).Select( kv => kv.Key ).ToList() )
			_sharedPending.Remove( id );
		foreach ( var id in _sharedPendingRemoved.Where( kv => kv.Value >= SharedPendingHold ).Select( kv => kv.Key ).ToList() )
			_sharedPendingRemoved.Remove( id );

		var byId = new Dictionary<Guid, SdfBrush>( local.Count );
		foreach ( var lb in local )
			byId[lb.Id] = lb;

		var result = new List<SdfBrush>( incoming.Count + 2 );
		var seen = new HashSet<Guid>();
		foreach ( var nb in incoming )
		{
			if ( _sharedPendingRemoved.ContainsKey( nb.Id ) )
				continue; // we deleted it; the host just hasn't said so yet
			seen.Add( nb.Id );
			result.Add( IsMineShared( nb.Id ) && byId.TryGetValue( nb.Id, out var lb ) ? lb : nb );
		}

		// Ours that the host hasn't got yet (a fresh stamp / duplicate in flight, the ghost): keep them, just
		// below the damage tail.
		int authoredLocal = Target.IsValid() ? Target.AuthoredBrushCount : 0;
		for ( int i = 0; i < authoredLocal && i < local.Count; i++ )
		{
			var lb = local[i];
			if ( !seen.Contains( lb.Id ) && IsMineShared( lb.Id ) )
				result.Insert( PropClaims.AuthoredCount( result ), lb );
		}

		return result;
	}

	/// <summary>The merged list is in place: re-seat the selection by id and drop every cached brush reference
	/// that no longer sits in the list (the gizmo's fade-out brush, the hover ghosts).</summary>
	internal void AfterExternalApply()
	{
		var b = Target.IsValid() ? Target.Brushes : null;

		_selection.Clear();
		if ( b is not null )
		{
			for ( int i = 0; i < b.Count; i++ )
			{
				if ( _sharedSelIds.Contains( b[i].Id ) )
					_selection.Add( i );
			}
		}
		_selected = -1;
		if ( b is not null && _sharedPrimaryId is { } pid )
			_selected = b.FindIndex( x => x.Id == pid );
		if ( _selected < 0 && _selection.Count > 0 )
			_selected = _selection[^1];
		_anchor = _selected;
		PruneSelection();

		if ( _gizmoBrush is not null && (b is null || !b.Contains( _gizmoBrush )) )
		{
			_gizmo.Hide();
			_gizmoBrush = null;
			_gizmoAlpha = 0f;
		}
		HideGhosts();
		_hoverBrush = -1;
		_worldHover = -1;
		_splineInsertArmed = false;
	}

	// Per frame: the selection is the lock set. Ask for what's newly selected, give back what isn't selected
	// any more, and drop any selected brush the registry says someone else holds.
	void UpdateSharedLocks()
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( b is null )
			return;

		// Enforce first, so a brush we lost never makes it into the request below.
		bool dropped = false;
		for ( int k = _selection.Count - 1; k >= 0; k-- )
		{
			int idx = _selection[k];
			if ( idx < 0 || idx >= b.Count )
				continue;
			var id = b[idx].Id;
			if ( !PropClaims.BrushLockedByOther( id ) )
				continue;
			_selection.RemoveAt( k );
			_sharedLocks.Remove( id );
			dropped = true;
		}
		if ( dropped )
		{
			if ( !_selection.Contains( _selected ) )
				_selected = _selection.Count > 0 ? _selection[^1] : -1;
			_anchor = _selected;
			MarkSelectionBaseline();
		}

		var want = new HashSet<Guid>();
		foreach ( var sb in SelectedBrushes )
			want.Add( sb.Id );
		if ( StampBrush is { } ghost )
			want.Add( ghost.Id ); // the pending stamp streams to everyone — hold it so nobody grabs it mid-placement

		List<Guid> take = null, give = null;
		foreach ( var id in want )
		{
			if ( !_sharedLocks.Contains( id ) )
				(take ??= new()).Add( id );
		}
		foreach ( var id in _sharedLocks )
		{
			if ( !want.Contains( id ) )
				(give ??= new()).Add( id );
		}

		if ( take is null && give is null )
			return;

		var claims = PropClaims.Current;
		if ( take is not null )
		{
			foreach ( var id in take )
				_sharedLocks.Add( id );
			if ( claims.IsValid() )
				claims.LockBrushes( Csv( take ) );
		}
		if ( give is not null )
		{
			foreach ( var id in give )
				_sharedLocks.Remove( id );
			if ( claims.IsValid() )
				claims.UnlockBrushes( Csv( give ) );
		}
	}

	// Undo/redo on a shared sculpt: restore only the brushes this session has touched, by id — set them to
	// the recorded state (re-adding ones since removed), remove touched ones the state doesn't have — and
	// leave everyone else's work alone. A touched brush another editor now holds is skipped. The resulting
	// commit publishes like any other edit. Selection is simply cleared (the recorded indices describe a
	// list that no longer exists); build settings aren't touched (they're the prop's, not ours).
	void ApplyUndoStateShared( SculptUndo.State state )
	{
		var live = Target.Brushes;
		if ( live is null )
			return;

		var stateById = new Dictionary<Guid, SdfBrush>( state.Authored.Count );
		foreach ( var sb in state.Authored )
			stateById[sb.Id] = sb;

		var work = new List<SdfBrush>( live );
		bool any = false;
		foreach ( var id in _sharedTouched )
		{
			if ( PropClaims.BrushLockedByOther( id ) )
				continue;

			int idx = work.FindIndex( x => x.Id == id );
			if ( stateById.TryGetValue( id, out var sb ) )
			{
				var copy = sb.Copy(); // the stack's copies stay pristine
				if ( idx >= 0 )
				{
					if ( Json.Serialize( work[idx] ) == Json.Serialize( copy ) )
						continue;
					work[idx] = copy;
				}
				else
				{
					work.Insert( PropClaims.AuthoredCount( work ), copy );
				}
				any = true;
			}
			else if ( idx >= 0 && !work[idx].Damage )
			{
				work.RemoveAt( idx );
				any = true;
			}
		}

		if ( !any )
			return;

		_undo.IsApplying = true;
		try
		{
			Target.Brushes = work;

			Selected = -1;
			MarkSelectionBaseline();
			_gizmo.Hide();
			_gizmoBrush = null;
			_gizmoAlpha = 0f;
			_splineInsertArmed = false;
			HideGhosts();
			_stampWire.Hide();
			_stampWireList.Clear();
			_hoverBrush = -1;
			_worldHover = -1;

			_pendingCommit = false;
			Target.Rebuild(); // → Committed → OnSharedCommitted publishes the restored brushes
		}
		finally
		{
			_undo.IsApplying = false;
		}
	}

	static string Csv( IEnumerable<Guid> ids ) => string.Join( ',', ids );
}
