using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Undo/redo history for one <see cref="SculptEditSession"/>. A plain STATE stack — the baseline shape plus one
/// entry per commit, with a cursor. Undo steps the cursor back and hands the session that state to apply; redo
/// steps forward. Recording whole states after the fact (rather than deltas) is what makes redo fall out for
/// free and keeps every entry independently applicable, however the shape got there.
///
/// Three decisions carry most of the weight:
///
/// • ONE ENTRY PER COMMIT. The session already splits continuous gestures from discrete ones
///   (<see cref="SculptEditSession.PreviewChanged"/> vs <see cref="SculptEditSession.NotifyChanged"/>), so the
///   commit funnel is exactly the right undo boundary: a whole slider drag or gizmo drag is one step, not two
///   hundred. Nothing here needs per-call-site begin/end transaction plumbing.
///
/// • ONLY THE AUTHORED PREFIX is stored (<see cref="SdfSculpture.AuthoredBrushCount"/>). Damage craters are
///   gameplay state that lands and heals outside the editor — carves append them, <see cref="SdfShrinkSystem"/>
///   removes them, and <see cref="SdfNetworkSync"/> can replace the list wholesale. Snapshotting everything
///   would make undo resurrect healed craters and erase ones that landed mid-edit, so the session splices a
///   restored prefix onto the LIVE damage tail instead.
///
/// • ENTRIES ARE DEDUPED on <see cref="SdfSculpture.ContentHash"/>. The commit path is deliberately redundant —
///   a debounce backstop, the exit commit, a cancelled stamp all re-commit an unchanged shape — so without the
///   hash guard Ctrl+Z would burn steps that change nothing. One hash per commit removes the whole class of bug.
///
/// Snapshots are deep copies (<see cref="SdfBrush.Copy"/>) and are NEVER mutated after being pushed, so an entry
/// can be applied any number of times (undo → redo → undo) and always yields the same shape. That immutability
/// is why the session copies again on the way out.
///
/// Scope is deliberately local: only edits made on this machine, through this session, are recorded. Damage,
/// shrink healing and remotely-authored shapes all reach the sculpture without passing through the commit
/// funnel, so they can never become undo steps — you can't undo another player's edit, which is correct.
/// </summary>
sealed class SculptUndo
{
	/// <summary>One recorded shape: the authored brushes plus the build settings and selection that went with
	/// them. Handed to the session to apply — treat it as immutable.</summary>
	public sealed class State
	{
		/// <summary>Deep copies of the authored brushes, in stack order. Never the damage tail.</summary>
		public List<SdfBrush> Authored { get; init; }

		public int Resolution { get; init; }
		public bool FlipFaces { get; init; }

		/// <summary>Selected index within <see cref="Authored"/>, or -1 for no selection. Re-clamped on apply,
		/// since the damage tail (and so the authored count) can have moved underneath it.</summary>
		public int Selected { get; init; }

		/// <summary>Content hash of <see cref="Authored"/> + the build settings — the dedup key.</summary>
		public int Hash { get; init; }
	}

	/// <summary>How many steps back the history holds. The brush cap is <see cref="SdfBrushPacker.MaxBrushes"/>
	/// (128), so even a pathological sculpt at full depth is a few thousand small objects — nothing worth
	/// optimising until it shows up in a profile.</summary>
	public const int MaxEntries = 64;

	readonly List<State> _stack = new();
	int _cursor = -1;

	/// <summary>True while the session is applying an undo/redo. The record site checks it so a rebuild
	/// triggered by an apply can't push the state it just restored straight back onto the stack.</summary>
	public bool IsApplying { get; set; }

	/// <summary>A step exists behind the cursor. False at the baseline — you can't undo past the shape the
	/// session opened on.</summary>
	public bool CanUndo => _cursor > 0;

	/// <summary>An undone step exists ahead of the cursor.</summary>
	public bool CanRedo => _cursor >= 0 && _cursor < _stack.Count - 1;

	/// <summary>Drop the whole history (session exit / teardown), so a later session on a different sculpture
	/// can never inherit and apply this one's states.</summary>
	public void Clear()
	{
		_stack.Clear();
		_cursor = -1;
	}

	/// <summary>Start a fresh history whose first entry is the target's CURRENT shape. Called when a session
	/// activates, so there's always a baseline to undo back to and the first Ctrl+Z after one edit returns to
	/// the shape the session opened on.</summary>
	public void Reset( SdfSculpture target, int selected )
	{
		Clear();
		Record( target, selected );
	}

	/// <summary>Record the target's current shape as a new step, unless it's identical to the one on top.
	/// Driven from the session's single commit funnel, so every discrete edit and every ended gesture lands
	/// here exactly once.</summary>
	public void Record( SdfSculpture target, int selected )
	{
		if ( IsApplying || !target.IsValid() )
			return;

		var brushes = target.Brushes;
		if ( brushes is null )
			return;

		int authored = target.AuthoredBrushCount;
		int hash = SdfSculpture.ContentHashPrefix( brushes, authored, target.Resolution, target.FlipFaces );

		// Same shape as the step on top → nothing happened worth undoing. This is the guard that absorbs the
		// commit path's deliberate redundancy (see the class summary); without it Ctrl+Z would appear to do
		// nothing for several presses in a row.
		if ( _cursor >= 0 && _stack[_cursor].Hash == hash )
			return;

		// A fresh edit made after undoing discards the redo tail — the standard branch-and-forget model.
		if ( _cursor < _stack.Count - 1 )
			_stack.RemoveRange( _cursor + 1, _stack.Count - _cursor - 1 );

		var snapshot = new List<SdfBrush>( authored );
		for ( int i = 0; i < authored; i++ )
			snapshot.Add( brushes[i].Copy() );

		_stack.Add( new State
		{
			Authored = snapshot,
			Resolution = target.Resolution,
			FlipFaces = target.FlipFaces,
			Selected = selected < authored ? selected : -1,
			Hash = hash,
		} );

		// Past the cap the oldest step falls off the bottom: the history stays bounded and undo simply stops
		// short of the very beginning rather than growing without limit through a long session.
		if ( _stack.Count > MaxEntries )
			_stack.RemoveAt( 0 );

		_cursor = _stack.Count - 1;
	}

	/// <summary>Step the cursor back one entry and hand out the state to apply. False (nothing touched) when
	/// already at the baseline.</summary>
	public bool Undo( out State state )
	{
		state = null;
		if ( !CanUndo )
			return false;

		state = _stack[--_cursor];
		return true;
	}

	/// <summary>Step the cursor forward one entry and hand out the state to apply. False when already at the
	/// newest state.</summary>
	public bool Redo( out State state )
	{
		state = null;
		if ( !CanRedo )
			return false;

		state = _stack[++_cursor];
		return true;
	}
}
