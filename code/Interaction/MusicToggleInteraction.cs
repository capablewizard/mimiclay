using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// "Turn off / Turn on" for a <see cref="SyncedMusic"/> — the lobby radio. Put it beside the music (same
/// GameObject as the clay, so possession's CarryExtras moves both onto the pawn and the possessed radio can
/// still be switched by everyone else). Anyone can toggle it; the state lives on the music itself
/// (<see cref="SyncedMusic.Playing"/>), so there's nothing here to network.
/// </summary>
[Title( "Music Toggle Interaction" )]
[Category( "Mimiclay" )]
[Icon( "power_settings_new" )]
public sealed class MusicToggleInteraction : Component, IInteractable
{
	[Property] public InteractSlot Slot { get; set; } = InteractSlot.Secondary;
	[Property] public string OffLabel { get; set; } = "Turn off";
	[Property] public string OnLabel { get; set; } = "Turn on";

	/// <summary>Played at the object on every machine when it's switched either way (the radio's button click).</summary>
	[Property] public SoundEvent ToggleSound { get; set; }

	const string ToggleOption = "music.toggle";

	// Always the music on THIS GameObject — never an authored reference: CarryExtras copies this component onto
	// the possessed pawn, and a copied reference would still point at the (dying) scene original.
	SyncedMusic _music;
	SyncedMusic Target => _music.IsValid() ? _music : _music = Components.Get<SyncedMusic>();

	void IInteractable.GetInteractions( in InteractContext ctx, List<InteractOption> options )
	{
		var music = Target;
		if ( music.IsValid() )
			options.Add( new InteractOption( ToggleOption, music.Playing ? OffLabel : OnLabel, Slot ) );
	}

	void IInteractable.Interact( in InteractContext ctx, string optionId )
	{
		var music = Target;
		if ( optionId == ToggleOption && music.IsValid() )
			ApplyToggle( !music.Playing );
	}

	/// <summary>Anyone → everyone: switch the music and click, in one message, so the click and the change land
	/// together on every machine. No host arbitration — racing toggles just end on whichever landed last.</summary>
	[Rpc.Broadcast]
	void ApplyToggle( bool on )
	{
		var music = Target;
		if ( music.IsValid() )
			music.Playing = on;

		if ( ToggleSound is not null )
			Sound.Play( ToggleSound, WorldPosition );
	}
}
