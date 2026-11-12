using System;
using Sandbox;

namespace Mimiclay;

/// <summary>Something that drives a taunt pie — the <see cref="TauntPie"/> panel finds it in its ancestors and draws
/// its <see cref="TauntPicker"/>. A possessed prop (HiderController) and an eliminated prop's free cam
/// (SpectatorController).</summary>
public interface ITauntPieOwner
{
	TauntPicker TauntPicker { get; }

	/// <summary>The taunt this owner's source (its body, or its ghost) is playing on this machine right now, or -1 —
	/// the pie wobbles that button's emoji for as long as it lasts, open or closed in between.</summary>
	int PlayingTaunt { get; }

	/// <summary>Bumped every time this owner's source starts a taunt — the pie pops that button on each change.</summary>
	int TauntSerial { get; }
}

/// <summary>
/// The T key for taunts, shared by everything a prop player can be: TAP T for the whistle, HOLD T for the taunt pie.
/// While the pie is up the mouse steers a virtual cursor round it (the real one stays captured); releasing T on a
/// slice plays it, releasing in the empty middle plays nothing. Left-clicking a slice plays it and keeps the pie up;
/// right-clicking closes it without playing anything. Owners call
/// <see cref="Update"/> once a frame from component update (where Input reads reliably) and play what it returns;
/// while <see cref="Open"/> they must not turn the camera with the mouse.
/// </summary>
public sealed class TauntPicker
{
	/// <summary>How long T must be held before the pie opens; a shorter press is a tap (the whistle).</summary>
	public const float HoldToOpen = 0.2f;

	/// <summary>The virtual cursor: how far it can travel from the centre, and the empty middle it must leave to pick a
	/// slice — px at default mouse sensitivity (BrushScrub.CapturedDelta).</summary>
	public const float Reach = 140f;
	public const float Deadzone = 55f;

	/// <summary>Scale on the mouse motion that steers the cursor — below 1 so it's easy to settle in the empty middle
	/// (no taunt) instead of skating out to a button.</summary>
	public const float Sensitivity = 0.6f;

	bool _holding; // T went down and hasn't come back up yet
	bool _clicked; // a slice was clicked during this hold
	RealTimeSince _held;

	/// <summary>This frame's pick came from clicking a slice (spam is allowed: owners skip their Hunt cooldown for it).</summary>
	public bool LastWasClick { get; private set; }

	/// <summary>The pie is up — mouse-look is suspended while it is.</summary>
	public bool Open { get; private set; }

	/// <summary>Time since the pie last opened — the panel's spring-in clock, stamped here so it's right on the very
	/// first frame the pie draws.</summary>
	public RealTimeSince SinceOpened { get; private set; }

	/// <summary>Time since the pie last closed (only meaningful once <see cref="HasClosed"/>) — the panel's spring-out
	/// clock.</summary>
	public RealTimeSince SinceClosed { get; private set; }

	/// <summary>The pie has closed at least once (so <see cref="SinceClosed"/> is a real time).</summary>
	public bool HasClosed { get; private set; }

	/// <summary>The slice that was under the cursor when the pie last closed — it stays lit while the pie springs out.</summary>
	public int ClosedSelection { get; private set; } = -1;

	/// <summary>The virtual cursor, screen-oriented (+y down), within <see cref="Reach"/>.</summary>
	public Vector2 Cursor { get; private set; }

	/// <summary>The slice under the cursor (an index into <see cref="Taunts.All"/>), or -1 in the empty middle.</summary>
	public int Selection { get; private set; } = -1;

	/// <summary>Run one frame. <paramref name="blocked"/> = a cursor overlay owns the input (drops any hold);
	/// <paramref name="canOpen"/> = the pie may open now (a tap still whistles when it can't). Returns the taunt to
	/// play this frame, or -1.</summary>
	public int Update( bool blocked, bool canOpen )
	{
		LastWasClick = false;

		if ( blocked )
		{
			_holding = false;
			Close();
			return -1;
		}

		if ( Input.Pressed( "Taunt" ) )
		{
			_holding = true;
			_clicked = false;
			_held = 0f;
		}

		if ( !_holding )
			return -1;

		if ( Input.Down( "Taunt" ) )
		{
			if ( !Open && _held > HoldToOpen && canOpen )
			{
				Open = true;
				SinceOpened = 0f;
				Cursor = Vector2.Zero;
				Selection = -1;
			}

			if ( !Open )
				return -1;

			var cursor = Cursor + BrushScrub.CapturedDelta * Sensitivity;
			if ( cursor.Length > Reach )
				cursor = cursor.Normal * Reach;
			Cursor = cursor;

			// Screen-space angle: 0 = up, clockwise (screen +y is down).
			Selection = cursor.Length > Deadzone ? Taunts.SliceAt( MathF.Atan2( cursor.x, -cursor.y ) ) : -1;

			// LMB on a slice: play it now and keep the pie up, so it can be clicked again and again (spam). Once anything
			// has been clicked, letting go of T just closes the pie — the release doesn't add one more. The click is
			// consumed so it doesn't also orbit / grab behind the pie. A click in the empty middle is ignored.
			// RMB: cancel — close the pie, and end the hold so letting go of T afterwards plays nothing. Consumed like the
			// click, so it doesn't also grab or dolly behind the pie.
			if ( Input.Pressed( "Attack2" ) )
			{
				Input.Clear( "Attack2" );
				_holding = false;
				Close();
				return -1;
			}

			if ( Selection >= 0 && Input.Pressed( "Attack1" ) )
			{
				Input.Clear( "Attack1" );
				_clicked = true;
				LastWasClick = true;
				return Selection;
			}
			return -1;
		}

		// Released: a tap whistles, the pie plays whatever slice the cursor is on (nothing from the middle, nothing
		// after clicks).
		_holding = false;
		int pick = !Open ? Taunts.Whistle : _clicked ? -1 : Selection;
		Close();
		return pick;
	}

	public void Close()
	{
		if ( Open )
		{
			HasClosed = true;
			SinceClosed = 0f;
			ClosedSelection = Selection;
		}
		Open = false;
		Selection = -1;
	}

	/// <summary>The pie HUD for <paramref name="owner"/>: a runtime screen panel parented to it, so it goes with it.
	/// Pass the previous result back in; it's only made once.</summary>
	public static GameObject EnsureHud( GameObject owner, GameObject existing )
	{
		if ( existing.IsValid() )
			return existing;

		var go = new GameObject( false, "Taunt HUD" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.SetParent( owner, false );
		var screen = go.Components.Create<ScreenPanel>();
		screen.ZIndex = 60; // over the crosshair HUD
		go.Components.Create<TauntPie>();
		go.Enabled = true;
		return go;
	}
}
