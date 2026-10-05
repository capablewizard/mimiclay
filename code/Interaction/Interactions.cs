using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>Which key an interaction answers to. One option per slot is offered at a time (highest
/// <see cref="InteractOption.Priority"/> wins), and the toast draws one card per slot, in this order.</summary>
public enum InteractSlot
{
	/// <summary>E ("Use") — the main verb: Edit/possess, talk to the tutorial character, press a button.</summary>
	Primary,

	/// <summary>F ("SecondaryUse") — a second verb beside the main one: the radio's on/off.</summary>
	Secondary,

	/// <summary>RMB ("Attack2") — reserved for picking props up. Nothing offers it yet.</summary>
	Pickup,

	/// <summary>LMB ("Attack1") — sculpt the hovered clay in place, first person (the edit lease; see
	/// <see cref="PropClaims"/>). The trigger's own press: when a provider offers this slot and it fires, the
	/// hunter swallows the shot.</summary>
	Sculpt,
}

/// <summary>One thing the player can do to what's under the crosshair. <see cref="Id"/> is handed back to the
/// provider's <see cref="IInteractable.Interact"/>, so it only has to be unique per provider.</summary>
public readonly record struct InteractOption( string Id, string Label, InteractSlot Slot, int Priority = 0 );

/// <summary>What the local hunter is aiming at, as handed to every provider. <see cref="Target"/> is the traced
/// object (a pawn prop resolves to its pawn ROOT — the rigidbody owns the disguise's shapes); <see cref="Sculpture"/>
/// is the clay that owns it, if any (a pawn prop's Disguise child), and is what the toast frames.</summary>
public readonly struct InteractContext
{
	public HunterController Hunter { get; init; }
	public GameObject Target { get; init; }
	public SdfSculpture Sculpture { get; init; }
	public Vector3 HitPosition { get; init; }
}

/// <summary>
/// Something that offers interactions. Two ways in:
/// <list type="bullet">
/// <item>As a COMPONENT on the object (or any ancestor of the traced object / its clay): the radio's on/off,
/// the tutorial character, future buttons. Offered only for that object.</item>
/// <item>As a registered SOURCE (<see cref="Interactions.RegisterSource"/>): a service that adds options to things
/// it doesn't own. <see cref="PropClaims"/> is one — whether props are editable is the MODE's decision (maps carry
/// no flags), so "Edit" can't live on the props themselves.</item>
/// </list>
/// Both are asked every frame while hovered, so <see cref="GetInteractions"/> must be cheap and side-effect free.
/// <see cref="Interact"/> runs on the INTERACTOR's machine only — the provider owns its own networking (a host RPC
/// where arbitration matters, a broadcast for a simple toggle).
/// </summary>
public interface IInteractable
{
	void GetInteractions( in InteractContext ctx, List<InteractOption> options );
	void Interact( in InteractContext ctx, string optionId );
}

/// <summary>An offered option plus who to call back when it's chosen.</summary>
public readonly record struct InteractChoice( IInteractable Provider, InteractOption Option );

/// <summary>
/// The interaction registry and the local hover. <see cref="HunterController"/> traces, calls
/// <see cref="Collect"/>, publishes the result with <see cref="PublishLocal"/> and dispatches key presses;
/// <see cref="HunterCrosshair"/> draws the toast row; <see cref="RoundOutlineSystem"/> glows
/// <see cref="LocalHoverSculpture"/>.
/// </summary>
public static class Interactions
{
	/// <summary>How far a hunter can reach to hover (and so interact with / claim) clay, measured from the eye to the
	/// point the crosshair ray lands on — NOT to the prop's origin, so a big prop is reachable by its near face. The
	/// gun's own ray is map-length (4096u); without this bound every distant prop across the room outlines and offers
	/// "E to Edit", which reads as noise. ONE value for every scene, tuned in the kitchen lobby — it used to be a
	/// per-scene knob (LobbyController / MapModeCard) and the maps just drifted apart for no reason.</summary>
	public const float Reach = 100f;

	static readonly List<IInteractable> _sources = new();

	/// <summary>Add a service-level provider (call from OnEnabled; pair with <see cref="UnregisterSource"/>).</summary>
	public static void RegisterSource( IInteractable source )
	{
		if ( source is not null && !_sources.Contains( source ) )
			_sources.Add( source );
	}

	public static void UnregisterSource( IInteractable source ) => _sources.Remove( source );

	/// <summary>The input action each slot listens to.</summary>
	public static string ActionFor( InteractSlot slot ) => slot switch
	{
		InteractSlot.Secondary => "SecondaryUse",
		InteractSlot.Pickup => "Attack2",
		InteractSlot.Sculpt => "Attack1",
		_ => "Use",
	};

	/// <summary>What's painted on the key the slot is bound to — follows rebinding. The mouse-button slots
	/// read as LMB/RMB rather than the engine's origin names.</summary>
	public static string KeyLabel( InteractSlot slot )
	{
		if ( slot == InteractSlot.Sculpt )
			return "LMB";
		if ( slot == InteractSlot.Pickup )
			return "RMB";

		var key = Input.GetButtonOrigin( ActionFor( slot ) );
		if ( string.IsNullOrEmpty( key ) )
			return slot switch { InteractSlot.Secondary => "F", _ => "E" };
		return key.ToUpperInvariant();
	}

	// Scratch for Collect (main thread only).
	static readonly List<InteractOption> _scratch = new();
	static readonly HashSet<IInteractable> _seen = new();

	/// <summary>Every option on offer for <paramref name="ctx"/>: registered sources first, then components on the
	/// clay and the traced object and their ancestors. One per slot (highest priority, first wins ties), in slot
	/// order.</summary>
	public static void Collect( in InteractContext ctx, List<InteractChoice> into )
	{
		into.Clear();
		_seen.Clear();

		foreach ( var source in _sources.ToArray() )
		{
			if ( source is Component c && !c.IsValid() )
			{
				_sources.Remove( source );
				continue;
			}
			Ask( source, ctx, into );
		}

		const FindMode up = FindMode.Enabled | FindMode.InSelf | FindMode.InAncestors;
		if ( ctx.Sculpture.IsValid() )
		{
			foreach ( var c in ctx.Sculpture.Components.GetAll<IInteractable>( up ) )
				Ask( c, ctx, into );
		}
		if ( ctx.Target.IsValid() )
		{
			foreach ( var c in ctx.Target.Components.GetAll<IInteractable>( up ) )
				Ask( c, ctx, into );
		}

		// One per slot.
		for ( var i = into.Count - 1; i >= 0; i-- )
		{
			for ( var j = 0; j < into.Count; j++ )
			{
				if ( j == i || into[j].Option.Slot != into[i].Option.Slot )
					continue;
				var other = into[j].Option.Priority;
				var mine = into[i].Option.Priority;
				if ( other > mine || (other == mine && j < i) )
				{
					into.RemoveAt( i );
					break;
				}
			}
		}

		into.Sort( ( a, b ) => a.Option.Slot.CompareTo( b.Option.Slot ) );
	}

	static void Ask( IInteractable provider, in InteractContext ctx, List<InteractChoice> into )
	{
		if ( !_seen.Add( provider ) )
			return;

		_scratch.Clear();
		provider.GetInteractions( ctx, _scratch );
		foreach ( var option in _scratch )
			into.Add( new InteractChoice( provider, option ) );
	}

	// ── The local hover (this machine's hunter) ──────────────────────────────────────────────────────────────
	// Freshness-stamped, the old PropClaims.LocalHover idiom: the publisher can vanish mid-hover (its pawn is
	// destroyed by a granted possession, or stops updating in edit mode) and component update order is a
	// HashSet — staleness is told by age, never by relying on someone clearing it.

	static InteractContext _hover;
	static bool _hasHover;
	static readonly List<InteractChoice> _hoverChoices = new();
	static RealTimeSince _hoverAge;

	/// <summary>Stamp this frame's hover. Null (or no choices) = aiming at nothing interactive.</summary>
	public static void PublishLocal( InteractContext? ctx, List<InteractChoice> choices )
	{
		_hoverAge = 0f;
		_hoverChoices.Clear();
		_hasHover = ctx.HasValue && choices is { Count: > 0 };
		if ( !_hasHover )
		{
			_hover = default;
			return;
		}

		_hover = ctx.Value;
		_hoverChoices.AddRange( choices );
	}

	static bool Fresh => _hasHover && _hoverAge < 0.1f;

	/// <summary>The clay under the local crosshair that's offering something, or null — the outline/boil gate.</summary>
	public static SdfSculpture LocalHoverSculpture => Fresh && _hover.Sculpture.IsValid() ? _hover.Sculpture : null;

	/// <summary>Play is ending — drop the statics (they survive editor Stop→Play).</summary>
	internal static void Reset()
	{
		_sources.Clear();
		_hasHover = false;
		_hover = default;
		_hoverChoices.Clear();
	}
}
