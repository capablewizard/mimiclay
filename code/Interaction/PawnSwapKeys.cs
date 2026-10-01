using System;

namespace Mimiclay;

/// <summary>
/// The shared rules for the body-swap keys, so the lobby, creative and charades all read them the same way:
/// <list type="bullet">
/// <item><b>R</b> ("Reload") — toggle between hunter and prop. While wearing a possessed prop it pops you out
/// as a hunter (leaving a borrowed prop always releases it back into the world).</item>
/// <item><b>E</b> ("Use") — pop out of a POSSESSED prop (<see cref="PropClaims.IsPossessed"/>). The same key
/// that took you in; on a hunter it's the interaction key instead (see <see cref="Interactions"/>).</item>
/// </list>
/// Both work mid-edit too, EXCEPT while the edit row would use them — R scales and E rotates the selected shape
/// or the stamp ghost (<see cref="SculptEditSession.ScrubKeysLive"/>). Nothing selected and not adding = free.
/// They also stand down while typing (charades guesses go through chat) or behind a menu.
///
/// Leaving mid-edit exits the session properly first (<see cref="Run"/>): a too-big/too-small sculpt raises the
/// revert confirmation and the swap waits on the answer, instead of the release force-reverting it silently.
/// </summary>
public static class PawnSwapKeys
{
	public const string SwapAction = "Reload";
	public const string LeaveAction = "Use";

	/// <summary>True when the swap keys must be ignored this frame.</summary>
	public static bool Blocked
	{
		get
		{
			var session = SculptEditSession.Current;
			if ( session.IsValid() && session.IsEditing && (session.ScrubKeysLive || session.ExitConfirmPending) )
				return true;

			return Sandbox.UI.InputFocus.Current is not null
				|| PauseMenu.IsOpen
				|| RoundSetup.IsOpen
				|| _afterExit is not null;
		}
	}

	/// <summary>R this frame: swap hunter ⇄ prop (or pop out of a possessed prop).</summary>
	public static bool SwapPressed => !Blocked && Input.Pressed( SwapAction );

	/// <summary>E this frame while <paramref name="own"/> is a possessed prop: pop out as a hunter.</summary>
	public static bool LeavePressed( HiderController own )
		=> own.IsValid() && PropClaims.IsPossessed( own ) && !Blocked && Input.Pressed( LeaveAction );

	// A swap parked on the edit session's revert confirmation — run once the player answers it.
	static Action _afterExit;

	/// <summary>Run <paramref name="swap"/> now — or, if a sculpt session is editing, after it exits through the
	/// same gate as Q (<see cref="SculptEditSession.RequestExit"/>). An invalid sculpt parks the swap on the
	/// revert dialog: "revert and leave" completes it, "keep editing" drops it. Pair with <see cref="Tick"/>.</summary>
	public static void Run( Action swap )
	{
		var session = SculptEditSession.Current;
		if ( session.IsValid() && session.IsEditing )
		{
			session.RequestExit();
			if ( session.ExitConfirmPending )
			{
				_afterExit = swap;
				return;
			}
		}

		swap();
	}

	/// <summary>Call once per frame from each mode's input handler, before reading the keys: completes or drops
	/// a swap parked on the revert dialog.</summary>
	public static void Tick()
	{
		if ( _afterExit is null )
			return;

		var session = SculptEditSession.Current;
		if ( session.IsValid() && session.ExitConfirmPending )
			return; // still waiting on the answer

		var swap = _afterExit;
		_afterExit = null;

		// Confirmed = reverted and exited → finish the swap. Still editing = "keep editing" → dropped. A
		// session that vanished (forced teardown) has nothing left to wait for, so it completes too.
		if ( !session.IsValid() || !session.IsEditing )
			swap();
	}

	/// <summary>Play is ending — drop a parked swap (statics survive editor Stop→Play).</summary>
	internal static void Reset() => _afterExit = null;
}
