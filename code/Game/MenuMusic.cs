using Sandbox;

namespace Mimiclay;

/// <summary>
/// Owns the menu's looping background music. Drop this on a GameObject in menu.scene and assign <see cref="Track"/>.
///
/// This is the "single owner" pattern in its smallest form: music must never be fire-and-forget, or two tracks
/// stack on top of each other. We hold exactly one <see cref="SoundHandle"/>, so only one track can ever play —
/// it starts when the component enables, stops (with a short fade) when it disables, and loops itself.
///
/// When gameplay music arrives, lift this into a scene-persistent MusicPlayer service with a Play( track ) method
/// that fades the current handle out before swapping — same idea, one slot, no overlap.
/// </summary>
public sealed class MenuMusic : Component
{
	/// <summary>The track to loop. Should be a 2D (UI) SoundEvent — see Assets/sounds/music/menu_music.sound.</summary>
	[Property] public SoundEvent Track { get; set; }

	/// <summary>Music volume (0–1). Editable live so an Options "Music" slider can drive it later.</summary>
	[Property, Range( 0, 1 )] public float Volume { get; set; } = 0.4f;

	/// <summary>Seconds to fade the track out when the menu closes, so it doesn't cut off abruptly.</summary>
	[Property] public float FadeOut { get; set; } = 0.5f;

	SoundHandle _handle;

	protected override void OnEnabled() => Restart();

	protected override void OnDisabled()
	{
		_handle?.Stop( FadeOut );
		_handle = null;
	}

	protected override void OnUpdate()
	{
		// Keep volume live so a slider takes effect immediately.
		if ( _handle.IsValid() )
			_handle.Volume = Volume;

		// SoundHandles don't loop on their own, so restart the track once it finishes.
		// (For a gapless loop, also tick "Loop" on the .wav's import settings in the editor.)
		if ( !_handle.IsValid() || _handle.Finished )
			Restart();
	}

	void Restart()
	{
		if ( Track is null )
			return;

		_handle?.Stop();
		_handle = Sound.Play( Track );

		if ( _handle.IsValid() )
			_handle.Volume = Volume;
	}
}
