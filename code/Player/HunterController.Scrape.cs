using System;

namespace Mimiclay;

// Clay scrape while a hunter creeps or climbs (user, 2026-11-01): the clay surface's ScrapeRough — the sound a prop
// sliding along something makes (HiderController.Impacts) — played quietly under crouch-walking and wall climbing, the
// two moves where footsteps don't carry it (a creeping step is near-silent, a climb has none). Runs on every machine,
// like the footsteps: speed is OBSERVED from the (interpolated, proxy-safe) transform, crouch is the synced IsDucking,
// climbing the synced HunterClimb.ClingingNet. One looping voice, restarted if the clip ends, faded in and out.
public sealed partial class HunterController
{
	/// <summary>Volume of the clay scrape under crouch-walking and climbing, at full speed (0 = off).</summary>
	[Property, Group( "Footsteps" ), Range( 0f, 1f )] public float ScrapeVolume { get; set; } = 0.25f;

	const float ScrapeMinSpeed = 10f;     // observed u/s below which you're still, not scraping
	const float ScrapeFadeRate = 6f;      // volume per second in and out

	SoundHandle _scrapeHandle;
	float _scrapeBaseVolume;
	float _scrapeLevel;                   // 0..1, eased toward the target
	Vector3 _scrapeFrom;
	bool _scrapeSeeded;
	HunterClimb _climb;

	// Unstick squelch (user, 2026-11-10): Shift peeling a clinging hunter off the wall plays a wet splat. Detected by the
	// owner on the same gate the HUD's "Unstick" hint and both climbs read (Clinging + Run pressed, keys allowed), then
	// broadcast so everyone hears it — or played locally only while the pawn is off the network (Hide's concealment;
	// see DetectJumpEvents). One-shot: the Squelch.sound / squeeze clips are long loops, the Splat set are short hits.
	/// <summary>One-shot played when Shift unsticks the hunter from a wall (null = sounds/game/splats/splat.sound).</summary>
	[Property, Group( "Footsteps" )] public SoundEvent UnstickSound { get; set; }
	[Property, Group( "Footsteps" ), Range( 0f, 1f )] public float UnstickVolume { get; set; } = 0.6f;

	const string UnstickSoundPath = "sounds/game/splats/splat.sound";

	void UpdateUnstickSquelch()
	{
		if ( !Owned || !_simKeysAllowed || !Clinging || Concealed || !Input.Pressed( "run" ) )
			return;
		if ( GameObject.Network.Active )
			BroadcastUnstickSquelch();
		else
			PlayUnstickSquelch();
	}

	[Rpc.Broadcast] void BroadcastUnstickSquelch() => PlayUnstickSquelch();

	void PlayUnstickSquelch()
	{
		var sound = UnstickSound ?? ResourceLibrary.Get<SoundEvent>( UnstickSoundPath );
		if ( sound is null || !_controller.IsValid() )
			return;
		// The middle of the body, where it was stuck to the wall (as the climb scrape).
		var handle = Sound.Play( sound, _controller.WorldPosition + Vector3.Up * _controller.CurrentHeight * 0.5f );
		if ( handle is { IsValid: true } )
		{
			handle.TargetMixer = _controller.FootstepMixer.GetOrDefault();
			handle.Volume *= UnstickVolume;
		}
	}

	void UpdateScrape()
	{
		if ( !_controller.IsValid() )
			return;
		UpdateUnstickSquelch();
		_climb ??= Components.Get<HunterClimb>();

		var pos = _controller.WorldPosition;
		float speed = _scrapeSeeded && Time.Delta > 0f ? (pos - _scrapeFrom).Length / Time.Delta : 0f;
		_scrapeFrom = pos;
		_scrapeSeeded = true;

		bool climbing = Clinging; // (the engine climb's synced flag, or the sim climb's — HunterController.SimClimb)
		bool creeping = _controller.IsDucking && _controller.IsOnGround;

		// How fast relative to the move's own top speed: a full-speed creep or climb is the full (quiet) scrape.
		float top = climbing ? MathF.Max( _climb.ClimbSpeed, 1f ) : MathF.Max( _controller.DuckedSpeed, 1f );
		float target = (climbing || creeping) && !Concealed && speed > ScrapeMinSpeed
			? Math.Clamp( speed / top, 0f, 1f ) * ScrapeVolume
			: 0f;

		_scrapeLevel = _scrapeLevel.Approach( target, ScrapeFadeRate * Time.Delta );
		if ( _scrapeLevel <= 0f )
		{
			_scrapeHandle?.Stop( 0.1f );
			_scrapeHandle = null;
			return;
		}

		if ( (_scrapeHandle is null || !_scrapeHandle.IsValid || _scrapeHandle.Finished) && target > 0f
			&& SdfCollider.ClaySurface?.SoundCollection.ScrapeRough is { } sound )
		{
			_scrapeHandle = Sound.Play( sound, pos );
			if ( _scrapeHandle is { IsValid: true } )
			{
				_scrapeHandle.TargetMixer = _controller.FootstepMixer.GetOrDefault();
				_scrapeBaseVolume = _scrapeHandle.Volume;
			}
		}

		if ( _scrapeHandle is { IsValid: true } )
		{
			// Crouching: at the feet. Climbing: the middle of the body, where it rubs the wall.
			_scrapeHandle.Position = climbing ? pos + Vector3.Up * _controller.CurrentHeight * 0.5f : pos;
			_scrapeHandle.Volume = _scrapeBaseVolume * _scrapeLevel;
		}
	}
}
