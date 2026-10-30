using System;
using System.Collections.Generic;

namespace Mimiclay;

// Against a surface — what a dynamic body needs while climbing (and walking, see HiderController.Movement): the move hull
// as its collision shape, the mouse turning it about the vertical with contacts only blocking the turn, and a nudge clear
// of the surface when a climb lets go. (Pressing into walls to align, which this file grew out of, was removed on
// 2026-10-30: with the mouse turning the prop, it wasn't needed.) The property names keep their old "Press" prefix so
// tuned values in scenes and prefabs survive.
public sealed partial class HiderController
{
	/// <summary>Angular damping while climbing — higher turns more gently.</summary>
	[Property, Group( "Alignment" ), Range( 0f, 30f )] public float PressAngularDamping { get; set; } = 10f;

	/// <summary>Fastest the wall may turn the prop against a surface (degrees per second). The contacts still decide
	/// which way and how far it turns; this only caps how fast — every tick's push into the wall is a big contact
	/// impulse, so without a cap the turn lands almost instantly whatever the damping.</summary>
	[Property, Group( "Alignment" ), Range( 5f, 360f )] public float PressMaxTurnRate { get; set; } = 40f;

	float? _savedAngularDamping;

	// Letting go of a climb: the contact solver leaves a sliver of penetration, and the move hull may stick out past the
	// real collider where the clay is concave — ease the hull off along the surface's normal so walking starts clear.
	void EaseHullOff( Vector3 normal )
	{
		// Clear means the FULL hull starts clear (the ground probe needs that), not just "no real overlap" — the
		// inset test calls a hull a hair into the wall merely touching, which left it unable to sweep away.
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

	/// <summary>How much of the turn a wall or ceiling puts on the prop by itself is kept, 0..1, while
	/// climbing or walking. 0 = none: the prop only turns when you turn it (the mouse), and contacts merely BLOCK a turn into
	/// them — line it up against a surface by turning into it. Higher = surfaces also pull it toward flush.</summary>
	[Property, Group( "Alignment" ), Range( 0f, 1f )] public float PressAlignStrength { get; set; } = 0f;

	float _lastSetSpin;
	bool _spinSeeded;

	// Walking/climbing, every dynamic tick: the MOUSE turns the prop about the vertical (pitch and roll are locked),
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

	/// <summary>While walking or climbing, collide with the convex MOVE HULL instead of the clay collider. The two
	/// are identical against flat walls and ceilings (a flat surface only ever touches a shape's convex envelope),
	/// but the clay can nestle into bumps its hull covers — stove knobs under a train's chassis, a shelf end above
	/// its boiler — and handing that back to the old swept mover left the hull embedded. Off = the clay collider,
	/// for A/B-ing the feel.</summary>
	[Property, Group( "Alignment" )] public bool AlignWithMoveHull { get; set; } = true;

	readonly List<PhysicsShape> _clayShapesOff = new();
	PhysicsShape _taggedHullShape;

	// Walking/climbing on: the move hull becomes the body's solid shape and the clay's shapes stop making contacts
	// (traces still hit them — bullets, hover prompts). Re-asserted every dynamic tick: a commit rebuilds the clay
	// collider with solid shapes, and a re-made body drops ours. Off: put everything back.
	void UseHullCollision( bool on, float floorClearance = 0f )
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
		var hullShape = _hull.AttachTo( pb, floorClearance );
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

	// SetPhysicsMode's climb-specific bits: damping on the way in, back on the way out.
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
