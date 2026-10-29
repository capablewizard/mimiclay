using System;

namespace Mimiclay;

// Impact sounds: a prop knocking into things SPLATS — the clay surface's impact sound (SdfCollider.ClaySurface),
// through the engine's own volume curve (Surface.PlayCollisionSound: louder with speed, hard vs soft by speed).
//
// Only while the prop is simulating as an OBJECT — held, loose or frozen (see PhysicsMode). A prop that is
// someone's BODY stays silent, as it always has (Body's DisableCollisionSounds — a hiding player must not
// clatter); so does released scenery settling as a character.
//
// Not the engine's collision-sound path: that only broadcasts when the OTHER object is networked, so a prop
// hitting the map — the usual case — would splat on the host alone. Instead the host, which simulates every
// released prop, hears the hit here and broadcasts it, so every machine plays it.
public sealed partial class HiderController : Component.ICollisionListener
{
	// Below this contact speed (units/s) a touch is a rest, not an impact — the engine's own floor.
	const float ImpactMinSpeed = 100f;

	// One splat per prop per this long: a tumble's bounces are distinct hits, a frame's worth of contacts isn't.
	const float ImpactCooldown = 0.08f;

	RealTimeSince _sinceImpact = 999f;

	bool ImpactSoundsLive => _physicsMode is PhysicsMode.Held or PhysicsMode.Loose or PhysicsMode.Frozen;

	// A held prop touched something this recently (see DriveHeld: touching, the hold switches to the velocity chase).
	RealTimeSince _sinceHeldContact = 999f;

	void Component.ICollisionListener.OnCollisionUpdate( Collision collision )
	{
		if ( !IsProxy && _physicsMode == PhysicsMode.Held )
			_sinceHeldContact = 0f;
		NoteScrape( collision );
	}

	void Component.ICollisionListener.OnCollisionStart( Collision collision )
	{
		if ( !IsProxy && _physicsMode == PhysicsMode.Held )
			_sinceHeldContact = 0f;
		NoteScrape( collision );

		// The simulating machine only (the host, for every released prop) — it alone sees the real contacts.
		if ( IsProxy || !ImpactSoundsLive )
			return;

		float speed = MathF.Abs( collision.Contact.NormalSpeed );
		if ( speed < ImpactMinSpeed || _sinceImpact < ImpactCooldown )
			return;

		_sinceImpact = 0f;
		if ( Networking.IsActive && GameObject.Network.Active )
			BroadcastImpact( collision.Contact.Point, speed );
		else
			PlayImpact( collision.Contact.Point, speed );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastImpact( Vector3 position, float speed ) => PlayImpact( position, speed );

	static void PlayImpact( Vector3 position, float speed )
		=> SdfCollider.ClaySurface?.PlayCollisionSound( position, speed );

	// ── Scrape: clay sliding along something squelches (the clay surface's ScrapeRough sound) ───────────────
	// Not the engine's scrape: that scales with friction force (a held prop has none), skips bodies with
	// collision sounds off (ours), and plays on the host alone. Instead the host keeps the fastest slide it
	// saw (relative velocity along the contact surface) and every ScrapeInterval broadcasts a volume for it;
	// every machine plays one voice per prop from that — started when there's something to hear, restarted
	// if the clip ends mid-scrape, faded out when the slide stops or the updates do.

	const float ScrapeMinSpeed = 25f;     // slide speed (u/s) below which it's resting, not scraping
	const float ScrapeFullSpeed = 350f;   // full volume from here
	const float ScrapeInterval = 0.1f;    // host → everyone while scraping, plus one zero when it stops
	const float ScrapeFadeRate = 10f;     // volume per second (the engine's scrape fade)

	float _scrapeSlide;                   // host: fastest slide this interval
	RealTimeSince _sinceScrapeSent;
	float _scrapeSent;
	float _scrapeTarget, _scrapeVolume;   // every machine: the voice
	RealTimeSince _sinceScrapeHeard = 999f;
	SoundHandle _scrapeHandle;
	float _scrapeBaseVolume;

	void NoteScrape( in Collision collision )
	{
		if ( IsProxy || !ImpactSoundsLive || !collision.Self.Body.IsValid() )
			return;

		// How fast the contact SLIPS: the centre's speed along the surface, minus what the spin accounts for
		// (spin rate about the surface's axes × centre-to-contact distance). Rolling cleanly → ~0, sliding
		// without turning → the full speed, a skidding roll → about the skid. Not the velocity AT the contact
		// point: the engine reports one representative point per pair, rarely the exact touching spot on a lumpy
		// shape, and the rest of a rolling body moves at up to twice its speed — so rolling read as scraping.
		var self = collision.Self.Body;
		var point = collision.Contact.Point;
		var normal = collision.Contact.Normal;
		var other = collision.Other.Body;
		var centre = self.MassCenter;
		var relative = self.Velocity - (other.IsValid() ? other.GetVelocityAtPoint( point ) : Vector3.Zero);
		var spin = self.AngularVelocity;
		float along = (relative - normal * Vector3.Dot( relative, normal )).Length;
		float rolled = (spin - normal * Vector3.Dot( spin, normal )).Length * centre.Distance( point );
		_scrapeSlide = MathF.Max( _scrapeSlide, along - rolled );
	}

	// Every machine, per frame.
	void UpdateScrape()
	{
		if ( !IsProxy && _sinceScrapeSent >= ScrapeInterval )
		{
			float volume = ImpactSoundsLive ? MathX.LerpInverse( _scrapeSlide, ScrapeMinSpeed, ScrapeFullSpeed ) : 0f;
			if ( volume > 0f || _scrapeSent > 0f )
			{
				if ( Networking.IsActive && GameObject.Network.Active )
					BroadcastScrape( volume );
				else
					HearScrape( volume );
			}
			_scrapeSent = volume;
			_scrapeSlide = 0f;
			_sinceScrapeSent = 0f;
		}

		if ( _sinceScrapeHeard > ScrapeInterval * 3f )
			_scrapeTarget = 0f; // updates stopped (lost, or the prop went away on the host)

		if ( _scrapeHandle is null && _scrapeTarget <= 0f )
			return;

		_scrapeVolume = _scrapeVolume.Approach( _scrapeTarget, ScrapeFadeRate * Time.Delta );
		if ( _scrapeTarget <= 0f && _scrapeVolume <= 0f )
		{
			_scrapeHandle?.Stop( 0.1f );
			_scrapeHandle = null;
			return;
		}

		if ( (_scrapeHandle is null || !_scrapeHandle.IsValid || _scrapeHandle.Finished) && _scrapeTarget > 0f
			&& SdfCollider.ClaySurface?.SoundCollection.ScrapeRough is { } sound )
		{
			_scrapeHandle = Sound.Play( sound, WorldPosition );
			_scrapeBaseVolume = _scrapeHandle?.Volume ?? 0f;
		}

		if ( _scrapeHandle is { IsValid: true } )
		{
			_scrapeHandle.Position = PropClaims.PopSpot( GameObject ); // the clay's centre, following the prop
			_scrapeHandle.Volume = _scrapeBaseVolume * _scrapeVolume;
		}
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastScrape( float volume ) => HearScrape( volume );

	void HearScrape( float volume )
	{
		_scrapeTarget = volume;
		_sinceScrapeHeard = 0f;
	}
}
