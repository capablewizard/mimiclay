using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Undo/redo history for one <see cref="SculptEditSession"/>. A plain STATE stack — the baseline shape plus one
/// entry per commit, with a cursor. Undo steps the cursor back and hands the session that state to apply; redo
/// steps forward. Recording whole states after the fact (rather than deltas) is what makes redo fall out for
/// free and keeps every entry independently applicable, however the shape got there. Selection travels with
/// the steps too — each entry keeps the selection its command started from and ended on, so undo lands you on
/// the selection you had before the command and redo on the one it left you with.
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
///
/// The history SURVIVES leaving edit mode: exit seals the final shape as a step, and <see cref="Activate"/>
/// resumes the stack (redo tail included) when it finds that same shape on the same sculpture — so you can
/// step out, look your work over, come back and still Ctrl+Z. Anything else it finds means the shape changed
/// while no session was watching, and the local-scope rule above is exactly why that resumes NOTHING: applying
/// a stored state over an externally-changed shape would silently revert work that wasn't ours to undo.
/// </summary>
sealed class SculptUndo
{
	/// <summary>A selection frozen in time: every selected index (ascending), the primary, and the shift
	/// anchor — the session's full selection model, so a multi-selection survives the round trip intact.
	/// The list is a copy taken at capture; treat it as immutable like everything else on the stack.</summary>
	public sealed class SelectionState
	{
		public List<int> Indices { get; init; }
		public int Primary { get; init; }
		public int Anchor { get; init; }
	}

	/// <summary>One recorded shape: the authored brushes plus the build settings and the selections around
	/// the command that produced it. Handed to the session to apply — treat it as immutable.</summary>
	public sealed class State
	{
		/// <summary>Deep copies of the authored brushes, in stack order. Never the damage tail.</summary>
		public List<SdfBrush> Authored { get; init; }

		public int Resolution { get; init; }
		public bool FlipFaces { get; init; }

		/// <summary>The selection the command that produced this state STARTED from. Undoing this state
		/// restores it — so stepping a command off also steps off the selection change it caused (undoing a
		/// duplicate re-selects the originals; undoing an add re-selects what the add replaced).</summary>
		public SelectionState Before { get; init; }

		/// <summary>The selection the command ENDED on, as committed. Redoing onto this state restores it
		/// (redoing an add re-selects the added brush).</summary>
		public SelectionState After { get; init; }

		/// <summary>Content hash of <see cref="Authored"/> + the build settings — the dedup key.</summary>
		public int Hash { get; init; }
	}

	/// <summary>How many steps back the history holds. The brush cap is <see cref="SdfBrushPacker.MaxBrushes"/>
	/// (128), so even a pathological sculpt at full depth is a few thousand small objects — nothing worth
	/// optimising until it shows up in a profile.</summary>
	public const int MaxEntries = 64;

	readonly List<State> _stack = new();
	int _cursor = -1;

	/// <summary>The sculpture every stored state was recorded from. A history is only ever resumed — or even
	/// appended to — for this exact component; any other target self-heals to a fresh stack in
	/// <see cref="Record"/>, so states can never be applied across sculptures however alike their hashes.</summary>
	SdfSculpture _target;

	/// <summary>True while the session is applying an undo/redo. The record site checks it so a rebuild
	/// triggered by an apply can't push the state it just restored straight back onto the stack.</summary>
	public bool IsApplying { get; set; }

	/// <summary>A step exists behind the cursor. False at the baseline — you can't undo past the shape the
	/// session opened on.</summary>
	public bool CanUndo => _cursor > 0;

	/// <summary>An undone step exists ahead of the cursor.</summary>
	public bool CanRedo => _cursor >= 0 && _cursor < _stack.Count - 1;

	/// <summary>Drop the whole history. Session TEARDOWN only (component death) — leaving edit mode keeps the
	/// stack so the next <see cref="Activate"/> can resume it.</summary>
	public void Clear()
	{
		_stack.Clear();
		_cursor = -1;
		_target = null;
	}

	/// <summary>Start a fresh history whose first entry is the target's CURRENT shape, so there's always a
	/// baseline to undo back to and the first Ctrl+Z after one edit returns to the shape this history opened
	/// on. The baseline had no command, so its Before and After are both just the current selection.</summary>
	public void Reset( SdfSculpture target, SelectionState selection )
	{
		Clear();
		Record( target, selection, selection );
	}

	/// <summary>A session is entering edit mode: resume or re-baseline. The history resumes — cursor position
	/// and redo tail intact — when the target is the sculpture it was recorded from AND the current shape is
	/// the entry under the cursor; the exit path seals the final shape as a step, so any clean leave-and-return
	/// matches. A mismatch means the shape changed while no session was watching (a remote author, a wholesale
	/// replace) — the stored states no longer describe this shape's lineage, so start over from what's there
	/// now rather than offer undo steps that would revert work that wasn't made here.</summary>
	public void Activate( SdfSculpture target, SelectionState selection )
	{
		if ( _cursor >= 0 && target == _target && target.IsValid() && target.Brushes is { } brushes
			&& SdfSculpture.ContentHashPrefix( brushes, target.AuthoredBrushCount, target.Resolution, target.FlipFaces )
				== _stack[_cursor].Hash )
			return;

		Reset( target, selection );
	}

	/// <summary>Record the target's current shape as a new step, unless it's identical to the one on top.
	/// Driven from the session's single commit funnel, so every discrete edit and every ended gesture lands
	/// here exactly once. <paramref name="before"/> is the selection the command started from (the session's
	/// pre-command snapshot), <paramref name="after"/> the one it's committing with.</summary>
	public void Record( SdfSculpture target, SelectionState before, SelectionState after )
	{
		if ( IsApplying || !target.IsValid() )
			return;

		var brushes = target.Brushes;
		if ( brushes is null )
			return;

		// A different sculpture than the history was recorded from: its states must never sit on this stack
		// (an undo would apply one sculpture's shape to another). Self-healing, not an error — start over.
		if ( target != _target )
		{
			Clear();
			_target = target;
		}

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
			Before = before,
			After = after,
			Hash = hash,
		} );

		// Past the cap the oldest step falls off the bottom: the history stays bounded and undo simply stops
		// short of the very beginning rather than growing without limit through a long session.
		if ( _stack.Count > MaxEntries )
			_stack.RemoveAt( 0 );

		_cursor = _stack.Count - 1;
	}

	/// <summary>Step the cursor back one entry: hand out the previous state's SHAPE to apply, and the
	/// SELECTION the undone command started from (the stepped-OFF state's Before) — together they put
	/// everything back as it stood the moment before that command ran. False (nothing touched) when already
	/// at the baseline.</summary>
	public bool Undo( out State state, out SelectionState selection )
	{
		state = null;
		selection = null;
		if ( !CanUndo )
			return false;

		selection = _stack[_cursor].Before; // the command being undone — the selection it was run from
		state = _stack[--_cursor];
		return true;
	}

	/// <summary>Step the cursor forward one entry: hand out that state's shape and the selection its command
	/// ended on (its After), re-running the command's selection change along with its shape change. False
	/// when already at the newest state.</summary>
	public bool Redo( out State state, out SelectionState selection )
	{
		state = null;
		selection = null;
		if ( !CanRedo )
			return false;

		state = _stack[++_cursor];
		selection = state.After;
		return true;
	}
}
