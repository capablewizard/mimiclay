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

	void Component.ICollisionListener.OnCollisionStart( Collision collision )
	{
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
}
