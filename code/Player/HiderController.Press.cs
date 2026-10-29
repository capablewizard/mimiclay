using System;
using System.Collections.Generic;

namespace Mimiclay;

// Press: pushing into a wall hands the prop to PHYSICS for as long as the push lasts, so the real SDF collider's
// contacts turn it flush against the wall — the same thing that lays a grabbed prop flush when it's pressed into
// a surface (see DriveHeld: while touching, the hold drops its joint and writes velocities, and the contact
// solver does the aligning). The move hull is never involved: it's convex and thinned, too rough to align with.
//
// While pressing the body is DYNAMIC: our input drives its velocity (vertical left to engine gravity), friction
// is off so the contact slides and turns instead of gripping, pitch and roll are locked so it only turns about
// the vertical. The MOUSE turns it (see ApplySurfaceTurn): the wall only blocks a turn into it, and pulls it toward
// flush only as much as PressAlignStrength allows (default none).
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

	/// <summary>Most of the walk speed (u/s) that drives INTO the wall while pressing. Enough to keep the contact
	/// loaded so the wall turns the prop flush; more just pushes the body through thin walls.</summary>
	[Property, Group( "Alignment" )] public float PressPushSpeed { get; set; } = 60f;

	/// <summary>Angular damping while pressing — higher turns flush more gently.</summary>
	[Property, Group( "Alignment" ), Range( 0f, 30f )] public float PressAngularDamping { get; set; } = 10f;

	/// <summary>Fastest the wall may turn the prop while pressing (degrees per second). The contacts still decide
	/// which way and how far it turns; this only caps how fast — every tick's push into the wall is a big contact
	/// impulse, so without a cap the turn lands almost instantly whatever the damping.</summary>
	[Property, Group( "Alignment" ), Range( 5f, 360f )] public float PressMaxTurnRate { get; set; } = 40f;

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
		UseHullCollision( true );

		if ( TooLongBuried( WorldPosition ) )
		{
			EndPress(); // inside something, not against it — the mover's buried escape takes over
			return;
		}

		// What the wall did to last tick's command: the part we asked for into the wall that the body didn't do.
		var into = (-_pushNormal).WithZ( 0f );
		into = into.IsNearZeroLength ? Vector3.Zero : into.Normal;
		float asked = Vector3.Dot( _pressCommand, into );
		float did = Vector3.Dot( Body.Velocity.WithZ( 0f ), into );
		AddPush( asked - did );

		// Space while pressing a wall: climb it (see HiderController.Climb) instead of jumping.
		if ( _jumpQueued )
		{
			_jumpQueued = false;
			_pressing = false;
			_pushPerKg = 0f;
			StartClimb( _pushNormal, WorldPosition );
			return;
		}

		// Falling (pushed off a ledge along the wall): a press is a floor thing — hand back to the mover, which falls.
		if ( _pushPerKg < PressExitForce || Body.Velocity.z < -PressFallSpeed )
		{
			EndPress();
			return;
		}

		// Our input drives it; gravity keeps it on the floor. The wall stops the push and turns it flush. Only
		// PressPushSpeed of it goes INTO the wall: we rewrite the velocity every tick, so the solver's push-back never
		// accumulates, and at the full walk speed (6u a tick) that drove props straight through thin mesh walls.
		// Sliding along the wall keeps the full speed.
		//
		// "The wall" is whatever the hull would ACTUALLY hit along the command this tick — not the surface the press
		// started on. Capping only the remembered one let the push into a different surface (a train pressed onto a
		// stove knob, then steered into the stove front) run at full speed and bury the prop. Two passes cover a corner.
		var command = ApplyLeash( ReadWish().WithZ( 0f ), pullIn: true );
		CapPushInto( ref command, into );
		for ( int pass = 0; pass < 2 && !command.IsNearZeroLength; pass++ )
		{
			var dir = command.Normal;
			var from = WorldPosition - dir * ClimbBackOff; // backed off: a sweep that starts touching reads start-solid
			var hit = HullSweep( from, WorldRotation, WorldPosition + dir * (command.Length * Time.Delta + 4f) );
			if ( !hit.Hit || hit.StartedSolid || Walkable( hit.Normal ) )
				break;

			var wall = (-hit.Normal).WithZ( 0f );
			if ( wall.IsNearZeroLength )
				break;
			_pushNormal = hit.Normal; // the force, the exit and the hand-back all follow the real wall
			if ( !CapPushInto( ref command, wall.Normal ) )
				break;
		}
		_pressCommand = command;
		Body.Velocity = command.WithZ( Body.Velocity.z );
		ApplySurfaceTurn( ReadTurn() ); // the mouse turns it; the wall can only block (or, at PressAlignStrength, nudge)
	}

	void EndPress()
	{
		if ( !_pressing )
			return;

		_pressing = false;
		_pushPerKg = 0f; // a fresh press has to build its own force
		SetPhysicsMode( PhysicsMode.Character );
		EaseHullOff( _pushNormal );
		IsGrounded = true; // it was standing on the floor; the mover's snap confirms it (or starts a fall)
	}

	// Limit the part of `command` going along `into` to PressPushSpeed. True if it had to.
	// Falling faster than this (u/s) ends a press.
	const float PressFallSpeed = 60f;

	bool CapPushInto( ref Vector3 command, Vector3 into )
	{
		if ( into.IsNearZeroLength )
			return false;
		float intoWall = Vector3.Dot( command, into );
		if ( intoWall <= PressPushSpeed )
			return false;
		command -= into * (intoWall - PressPushSpeed);
		return true;
	}

	// Handing a dynamic body back to the mover: the contact solver leaves a sliver of penetration, and the move hull
	// may stick out past the real collider where the clay is concave — ease the hull off along the surface's normal
	// so the mover's first sweep starts clear.
	void EaseHullOff( Vector3 normal )
	{
		_unstickHint = normal; // if the mover still finds itself inside, it backs out this way (see TryUnstick)
		_sinceUnstickHint = 0f;

		// Clear means the FULL hull starts clear (the mover's sweeps need that), not just "no real overlap" — the
		// inset test calls a hull a hair into the wall merely touching, which left the mover unable to sweep away.
		_hull.Refresh( _collider, Scene.PhysicsWorld );
		var trace = MoveTrace;
		bool Clear( Vector3 p ) => !_hull.StartsSolid( trace, new Transform( p, WorldRotation ) ) && !PoseBlocked( p, WorldRotation );
		if ( !_hull.Ready || Clear( WorldPosition ) )
			return;

		var n = normal.IsNearZeroLength ? Vector3.Up : normal.Normal;
		for ( float d = 0.25f; d <= 8f; d *= 2f )
		{
			if ( !Clear( WorldPosition + n * d ) )
				continue;
			WorldPosition += n * d;
			return;
		}
	}

	/// <summary>How much of the turn a wall or ceiling puts on the prop by itself is kept, 0..1, while pressing or
	/// climbing. 0 = none: the prop only turns when you turn it (the mouse), and contacts merely BLOCK a turn into
	/// them — line it up against a surface by turning into it. Higher = surfaces also pull it toward flush.</summary>
	[Property, Group( "Alignment" ), Range( 0f, 1f )] public float PressAlignStrength { get; set; } = 0f;

	float _lastSetSpin;
	bool _spinSeeded;

	// Pressing/climbing, every dynamic tick: the MOUSE turns the prop about the vertical (pitch and roll are locked),
	// at whatever rate it's turning. The physics step can still stop that turn against a surface. Whatever spin the
	// contacts added on their own last step is kept only at PressAlignStrength (capped at PressMaxTurnRate) — the
	// compliance-turning idea: mouse input leads, surfaces can only block it or (optionally) nudge it flush.
	// `turnDegrees` is this tick's mouse turn (ReadTurn), 0 when not controlled.
	void ApplySurfaceTurn( float turnDegrees )
	{
		// No alignment: LOCK the yaw too, so contacts can't turn the body at all — zeroing the spin each tick isn't
		// enough, the contact impulses land INSIDE the physics step (a train pressed in skewed swung 25°→91° that
		// way). The mouse then turns it directly, pose-tested against the move hull (Turn), so it still can't be
		// turned into a wall.
		bool lockYaw = PressAlignStrength <= 0.001f;
		if ( Body.Locking.Yaw != lockYaw || !Body.Locking.Pitch || !Body.Locking.Roll )
			Body.Locking = new PhysicsLock { Pitch = true, Roll = true, Yaw = lockYaw };

		if ( lockYaw )
		{
			Body.AngularVelocity = Vector3.Zero;
			if ( MathF.Abs( turnDegrees ) > 0.001f && _hull.Ready )
			{
				var pos = WorldPosition;
				var rot = Turn( ref pos, WorldRotation, turnDegrees );
				if ( !rot.AlmostEqual( WorldRotation ) )
					WorldTransform = new Transform( pos, rot, WorldScale );
			}
			return;
		}

		float dt = MathF.Max( Time.Delta, 0.001f );
		float mouseRate = turnDegrees.DegreeToRadian() / dt;
		float spin = Body.AngularVelocity.z;
		float fromContacts = _spinSeeded ? spin - _lastSetSpin : 0f;
		_spinSeeded = true;

		float maxAlign = PressMaxTurnRate.DegreeToRadian();
		float aligned = Math.Clamp( fromContacts * PressAlignStrength, -maxAlign, maxAlign );
		_lastSetSpin = mouseRate + aligned;
		Body.AngularVelocity = Body.AngularVelocity.WithZ( _lastSetSpin );
	}

	/// <summary>While pressing or climbing, collide with the convex MOVE HULL instead of the clay collider. The two
	/// are identical against flat walls and ceilings (a flat surface only ever touches a shape's convex envelope),
	/// but the clay can nestle into bumps its hull covers — stove knobs under a train's chassis, a shelf end above
	/// its boiler — and handing that back to the mover left the hull embedded: "buried". Off = the clay collider,
	/// for A/B-ing the feel.</summary>
	[Property, Group( "Alignment" )] public bool AlignWithMoveHull { get; set; } = true;

	readonly List<PhysicsShape> _clayShapesOff = new();
	PhysicsShape _taggedHullShape;

	// Pressing/climbing on: the move hull becomes the body's solid shape and the clay's shapes stop making contacts
	// (traces still hit them — bullets, hover prompts). Re-asserted every dynamic tick: a commit rebuilds the clay
	// collider with solid shapes, and a re-made body drops ours. Off: put everything back.
	void UseHullCollision( bool on )
	{
		var pb = Body.IsValid() ? Body.PhysicsBody : null;
		if ( !on || !AlignWithMoveHull || pb is null || !pb.IsValid() )
		{
			_hull.Detach();
			foreach ( var shape in _clayShapesOff )
				if ( shape.IsValid() )
					shape.EnableSolidCollisions = true;
			_clayShapesOff.Clear();
			return;
		}

		_hull.Refresh( _collider, Scene.PhysicsWorld );
		var hullShape = _hull.AttachTo( pb );
		if ( hullShape is null )
			return; // no hull yet — keep colliding with the clay
		hullShape.Friction = 0f; // slides and turns against the wall like a held prop

		// Collision tags: a shape added straight to a body has NONE (a Collider copies its object's tags onto its
		// shapes) and an untagged shape went straight through clay props — a train pressed into the released radio
		// ended up inside it. Wear the disguise's tags, so the hull follows exactly the clay collider's rules
		// (solid to scenery clay, passing through fellow live props).
		if ( _body.IsValid() && _taggedHullShape != hullShape )
		{
			hullShape.Tags.SetFrom( _body.GameObject.Tags );
			_taggedHullShape = hullShape;
		}

		foreach ( var shape in pb.Shapes )
		{
			if ( shape == hullShape || shape.IsTrigger || !shape.EnableSolidCollisions )
				continue;
			shape.EnableSolidCollisions = false;
			_clayShapesOff.Add( shape );
		}
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
