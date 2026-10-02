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

	public static void Play( string path )
	{
		var handle = Sound.PlayFile( SoundFile.Load( path ) );
		if ( !handle.IsValid() )
			return;

		handle.ListenLocal = true;
		handle.DistanceAttenuation = false;
		handle.AirAbsorption = false;
		handle.OcclusionEnabled = false;
		handle.ReverbEnabled = false;
	}
}
