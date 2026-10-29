using System;

namespace Mimiclay;

// Press: pushing into a wall hands the prop to PHYSICS for as long as the push lasts, so the real SDF collider's
// contacts turn it flush against the wall — the same thing that lays a grabbed prop flush when it's pressed into
// a surface (see DriveHeld: while touching, the hold drops its joint and writes velocities, and the contact
// solver does the aligning). The move hull is never involved: it's convex and thinned, too rough to align with.
//
// While pressing the body is DYNAMIC: our input drives its velocity (vertical left to engine gravity), friction
// is off so the contact slides and turns instead of gripping, pitch and roll are locked so it only turns about
// the vertical, and angular damping keeps that turn gentle. Mouse turning pauses — the wall owns the facing.
//
// FORCE decides both ends. The push force is how hard the wall is pushing back: the clay's mass times the part of
// the commanded velocity the wall cancels, per second. Before the press that's read off the mover's sweep (the
// wish into the wall it hit); during it, off the real physics step (what we commanded into the wall minus what the
// body actually did). A head-on push builds force fast, a glancing slide along a wall barely any. The force is
// smoothed over PressForceSmoothing and compared PER KILOGRAM, because the player's input sets a velocity, so the
// force is proportional to mass — clay runs from ~20 kg (a knob) to ~4500 kg (a pot), and one raw threshold would
// leave knobs never pressing and pots pressing on every graze. Enter above PressEnterForce, leave below
// PressExitForce (lower, so it can't flicker at the threshold) — or at once on a jump, edit mode or a control lock.
// Back on the mover the body is keyframed again, so a prop that isn't being pushed still parks and never drifts.
public sealed partial class HiderController
{
	/// <summary>Pushing into a wall lets physics turn the prop flush against it.</summary>
	[Property, Group( "Alignment" )] public bool PressAlign { get; set; } = true;

	/// <summary>Push force per kilogram of clay (u/s²) that hands the prop to physics. A head-on walk into a wall
	/// is ~5500; the default needs a push at least ~25° into the wall at walking speed.</summary>
	[Property, Group( "Alignment" )] public float PressEnterForce { get; set; } = 2500f;

	/// <summary>Push force per kilogram (u/s²) below which the prop goes back to the mover.</summary>
	[Property, Group( "Alignment" )] public float PressExitForce { get; set; } = 800f;

	/// <summary>Seconds the push force is averaged over (exponential).</summary>
	[Property, Group( "Alignment" ), Range( 0.01f, 0.5f )] public float PressForceSmoothing { get; set; } = 0.08f;

	/// <summary>Angular damping while pressing — higher turns flush more gently.</summary>
	[Property, Group( "Alignment" ), Range( 0f, 30f )] public float PressAngularDamping { get; set; } = 6f;

	/// <summary>True while physics is aligning the prop against a wall.</summary>
	public bool IsPressing => _pressing;

	/// <summary>The smoothed push force against the wall, in newton-ish units (kg·u/s²) — debug readout.</summary>
	public float PushForce => _pushPerKg * ClayKg;

	bool _pressing;
	float _pushPerKg;        // smoothed push force per kilogram (u/s²)
	Vector3 _pushNormal;
	Vector3 _pressCommand;   // the horizontal velocity we wrote last press tick
	float? _savedAngularDamping;

	float ClayKg => Body.PhysicsBody is { } pb && pb.IsValid() && pb.Mass > 0f ? pb.Mass
		: _collider.IsValid() ? MathF.Max( _collider.ClayMass, 1f ) : 1f;

	// Fold this tick's push (velocity the wall cancelled, u/s) into the smoothed force per kilogram.
	void AddPush( float cancelledSpeed )
	{
		float dt = MathF.Max( Time.Delta, 0.001f );
		float perKg = MathF.Max( cancelledSpeed, 0f ) / dt;
		_pushPerKg += (perKg - _pushPerKg) * (1f - MathF.Exp( -dt / PressForceSmoothing ));
	}

	// Grounded mover, every tick: how much of the wish the wall cancelled this tick. Enough force → physics.
	void NotePush( Vector3 wish, SteepHit steep )
	{
		if ( !PressAlign )
			return;

		var flatNormal = steep.Normal.WithZ( 0f );
		float cancelled = 0f;
		if ( steep.Hit && !flatNormal.IsNearZeroLength )
		{
			// A different wall: the force against the old one doesn't carry over.
			if ( Vector3.Dot( _pushNormal, steep.Normal ) < 0.9f )
				_pushPerKg = 0f;
			_pushNormal = steep.Normal;
			cancelled = Vector3.Dot( wish.WithZ( 0f ), -flatNormal.Normal );
		}
		AddPush( cancelled );

		if ( _pushPerKg >= PressEnterForce )
		{
			_pressing = true;
			_pressCommand = wish.WithZ( 0f );
		}
	}

	// In place of the mover while pressing.
	void StepPress()
	{
		SetPhysicsMode( PhysicsMode.Pressing );
		ApplyHeldFriction();

		// What the wall did to last tick's command: the part we asked for into the wall that the body didn't do.
		var into = (-_pushNormal).WithZ( 0f );
		into = into.IsNearZeroLength ? Vector3.Zero : into.Normal;
		float asked = Vector3.Dot( _pressCommand, into );
		float did = Vector3.Dot( Body.Velocity.WithZ( 0f ), into );
		AddPush( asked - did );

		if ( _pushPerKg < PressExitForce || _jumpQueued )
		{
			EndPress();
			return;
		}

		// Our input drives it; gravity keeps it on the floor. The wall stops the push and turns it flush.
		_pressCommand = ApplyLeash( ReadWish().WithZ( 0f ), pullIn: true );
		Body.Velocity = _pressCommand.WithZ( Body.Velocity.z );
	}

	void EndPress()
	{
		if ( !_pressing )
			return;

		_pressing = false;
		_pushPerKg = 0f; // a fresh press has to build its own force
		SetPhysicsMode( PhysicsMode.Character );

		// The contact solver leaves a sliver of penetration; ease the move hull off the wall so the mover's
		// first sweep starts clear (the hull may also stick out past the real collider where the clay is concave).
		_hull.Refresh( _collider, Scene.PhysicsWorld );
		if ( _hull.Ready && PoseBlocked( WorldPosition, WorldRotation ) )
		{
			var n = _pushNormal.WithZ( 0f );
			n = n.IsNearZeroLength ? Vector3.Up : n.Normal;
			for ( float d = 0.25f; d <= 4f; d *= 2f )
			{
				if ( PoseBlocked( WorldPosition + n * d, WorldRotation ) )
					continue;
				WorldPosition += n * d;
				break;
			}
		}

		IsGrounded = true; // it was standing on the floor; the mover's snap confirms it (or starts a fall)
	}

	// SetPhysicsMode's press-specific bits: damping on the way in, back on the way out.
	void ApplyPressDamping( bool pressing )
	{
		var pb = Body.PhysicsBody;
		if ( pb is null || !pb.IsValid() )
			return;

		if ( pressing )
		{
			_savedAngularDamping ??= pb.AngularDamping;
			pb.AngularDamping = PressAngularDamping;
		}
		else if ( _savedAngularDamping is { } saved )
		{
			pb.AngularDamping = saved;
			_savedAngularDamping = null;
		}
	}
}
