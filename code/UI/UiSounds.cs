using System;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Interface sounds played as raw files: flat 2D blips at the listener (ListenLocal, no distance / air / occlusion /
/// reverb) — pure feedback, nothing in the world. CSS `sound-in` covers hover/press; this is for code-driven moments.
/// </summary>
public static class UiSounds
{
	/// <summary>The "pop" a silhouette hint makes when it flashes into a roster pip — also the setup menu's
	/// game / map change.</summary>
	public const string Silhouette = "sounds/kenney/ui/maximize_006.wav";

	/// <summary>The game's success sting (a prop found in prop hunt — there it plays IN the world, at the prop).
	/// A sound EVENT, so play it with <see cref="PlayEvent"/>.</summary>
	public const string Success = "sounds/game/success.sound";

	/// <summary>The typewriter tick: the menu hover sound pitched up — speech bubbles typing out a chat line, the
	/// charades word box typing a solved answer. Play at most one per frame; per-character at typing speed is
	/// machine-gun fire.</summary>
	const string TypeSound = "ui.button.over";

	/// <summary>One typewriter tick, at <paramref name="volume"/> ± <paramref name="jitter"/> (a fraction) and a
	/// rolled pitch.</summary>
	public static void TypeBlip( float volume = 0.4f, float jitter = 0.15f )
	{
		if ( volume <= 0f )
			return; // silent — don't spend a sound channel on it

		var snd = Sound.Play( TypeSound );
		if ( snd is null )
			return;

		jitter = Math.Clamp( jitter, 0f, 1f );
		snd.Volume = volume * Game.Random.Float( 1f - jitter, 1f + jitter );
		snd.Pitch = Game.Random.Float( 1.15f, 1.35f ); // pitched up out of "button hover", into "type tick"
	}

	public static void Play( string path ) => Flatten( Sound.PlayFile( SoundFile.Load( path ) ) );

	/// <summary>Play a .sound event (rather than a raw file) as flat UI feedback.</summary>
	public static void PlayEvent( string path ) => Flatten( Sound.Play( path ) );

	static void Flatten( SoundHandle handle )
	{
		if ( !handle.IsValid() )
			return;

		handle.ListenLocal = true;
		handle.DistanceAttenuation = false;
		handle.AirAbsorption = false;
		handle.OcclusionEnabled = false;
		handle.ReverbEnabled = false;
	}
}
