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
