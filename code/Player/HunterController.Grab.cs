using System.Collections.Generic;

namespace Mimiclay;

// The hunter's half of the grab: the holder logic itself is GrabHolder (shared with a prop holding things in creative) —
// this partial feeds it the hunter's eye and aim, and turns its hints into the crosshair's cards.
public sealed partial class HunterController
{
	/// <summary>Test harness: a grab keeps holding as if RMB were down (scripts can't hold buttons).</summary>
	[ConVar( "mimi_dbg_grab_hold", Help = "Debug: grabs hold as if RMB were held down." )]
	public static bool DebugGrabHold { get; set; }

	/// <summary>Testing: RMB toggles the grab — one press picks up, the next press drops — instead of hold-to-carry.
	/// Local preference (each player's own feel), saved between sessions.</summary>
	[ConVar( "mimi_grab_toggle", ConVarFlags.Saved, Help = "Grab toggles on RMB press (press again to drop) instead of holding RMB." )]
	public static bool GrabToggle { get; set; }

	GrabHolder _grabHolder;
	GrabHolder Grab => _grabHolder ??= new GrabHolder( this, () => HookSimInput( HunterSim ) );

	/// <summary>E or R held over a grabbed prop: the mouse is turning or scaling it, so the view must not move.</summary>
	internal bool GrabLookFrozen => _grabHolder?.LookFrozen ?? false;

	/// <summary>Ask the host to put <paramref name="target"/> in our hands (the RMB Grab card).</summary>
	internal void BeginGrab( GameObject target ) => Grab.Begin( target );

	/// <summary>Owner, per frame, before the interaction hover: run the hold. True = the grab owns this frame's
	/// input (no hover prompts, and the LMB press was a freeze, not a shot).</summary>
	bool UpdateGrab( Vector3 eye )
	{
		var grab = Grab;
		if ( !grab.Update( eye, _aimDir, _controller.EyeAngles.yaw, EditMode || LeaseEditing ) )
			return false;

		if ( grab.PressedSlot is { } pressed ) // E Rotate / R Scale / LMB Freeze: spring that card
		{
			LastFiredSlot = pressed;
			SinceFired = 0;
		}
		if ( grab.Grabbed is { } held && held.IsValid() )
			PublishGrabHover( held, grab );
		return true;
	}

	internal void LetGo( GameObject held, bool freeze = false ) => Grab.LetGo( held, freeze ); // (the chase bot lets go too — HunterChaseBot)

	// The held prop's verbs, as the crosshair's cards (and its outline, through the local hover).
	void PublishGrabHover( GameObject held, GrabHolder grab )
	{
		var hider = held.Components.Get<HiderController>();
		var ctx = new InteractContext
		{
			Hunter = this,
			Target = held,
			Sculpture = hider.IsValid() ? hider.DisguiseSculpture : null,
			HitPosition = grab.HintAt,
		};

		grab.FillHints( _hoverChoices );
		HoverContext = ctx;
		Interactions.PublishLocal( ctx, _hoverChoices );
	}
}
