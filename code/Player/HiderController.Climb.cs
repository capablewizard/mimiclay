using System;

namespace Mimiclay;

// Climbing: the press carried on (see HiderController.Press). The body is DYNAMIC and moved by the real SDF
// collider's contacts, with engine gravity swapped for a small pull into the surface it's stuck to. The prop
// stays UPRIGHT throughout — pitch and roll are locked, so physics only ever turns it about the vertical, the same
// as the press. On a wall it hangs upright against it; on a ceiling it hangs upright beneath it.
//
// Controls (Meccha Chameleon's scheme): Space while pressing a wall — or held while jumping into a wall or a
// ceiling — grabs it. WASD moves along the surface (camera-relative), Space / Ctrl move up / down it, Shift lets go.
//
// Each tick the surface is SENSED with a short trace of the real rigidbody (translation only — all the engine's
// body traces can do) from a pose backed a little off the surface, toward it. That gives the surface's normal and
// the contact point:
//  • inner corner — a trace along the way we're moving hits a different surface: it takes over (a floor ends the
//    climb and the mover stands on it);
//  • outer edge — the surface is gone: the pull aims at the LAST contact point instead, so the prop swings round the
//    edge like a magnet until it finds the next face (or lets go after ClimbLoseTime).
//
// No input for a few ticks (or edit mode / a control lock) HOLDS it: the body goes keyframed exactly where it is,
// so a prop hiding on a wall never slides and costs nothing — and can be sculpted in mid-air. Any input lets go of
// the hold. Letting go of the climb hands the body back to the mover, which falls and parks as normal.
public sealed partial class HiderController
{
	/// <summary>Props can grab and climb walls and ceilings.</summary>
	[Property, Group( "Climbing" )] public bool AllowClimbing { get; set; } = true;

	/// <summary>Speed along a surface while climbing (u/s).</summary>
	[Property, Group( "Climbing" )] public float ClimbSpeed { get; set; } = 80f;

	/// <summary>How hard the prop is pulled into the surface it's stuck to (u/s into it).</summary>
	[Property, Group( "Climbing" )] public float ClimbStickSpeed { get; set; } = 40f;

	/// <summary>Speed the prop is pulled round an outer edge toward the last point it touched (u/s).</summary>
	[Property, Group( "Climbing" )] public float ClimbEdgePullSpeed { get; set; } = 140f;

	/// <summary>How far past the surface the sensing trace looks (u).</summary>
	[Property, Group( "Climbing" )] public float ClimbReach { get; set; } = 8f;

	/// <summary>Seconds without any surface before a climbing prop lets go and falls.</summary>
	[Property, Group( "Climbing" ), Range( 0.05f, 2f )] public float ClimbLoseTime { get; set; } = 0.35f;

	/// <summary>Seconds after touching a wall or ceiling in the air that it still counts as "the wall I'm at"
	/// when you aren't steering toward anything.</summary>
	[Property, Group( "Climbing" ), Range( 0f, 1f )] public float ClimbGrabWindow { get; set; } = 0.3f;

	/// <summary>How far (u) a wall or ceiling can be from the prop in the air and still be grabbed.</summary>
	[Property, Group( "Climbing" )] public float ClimbGrabReach { get; set; } = 10f;

	/// <summary>Seconds a Space press in the air stays "I want to grab" while waiting to reach a wall.</summary>
	[Property, Group( "Climbing" ), Range( 0f, 1f )] public float ClimbGrabBuffer { get; set; } = 0.35f;

	/// <summary>True while the prop is stuck to a wall or ceiling.</summary>
	public bool IsClimbing => _climbing;

	/// <summary>Test harness: fake the climb's up/down input (+1 = Space held, −1 = Ctrl held). Scripts can't hold keys.</summary>
	[ConVar( "mimi_dbg_climb_vertical", Help = "Debug: climbing props move as if Space (1) or Ctrl (-1) were held." )]
	public static float DebugClimbVertical { get; set; }

	bool _climbing;
	Vector3 _climbNormal;      // smoothed normal of the surface we're stuck to (points away from it)
	Vector3 _climbPoint;       // last point the surface was touched — what an outer edge pulls toward
	float _sinceSurface;
	int _climbStill;

	// The mover's last wall or ceiling contact in the air, for a jump-and-grab.
	Vector3 _airWallNormal, _airWallPoint;
	float _sinceAirWall = 999f;

	// Grab intent in the air: this airborne stretch began with our own jump, or Space was pressed just now.
	bool _airFromJump;
	RealTimeSince _sinceJumpPress = 999f;

	const float ClimbBackOff = 2f; // the sensing trace starts this far off the surface, so it never starts touching it

	void StartClimb( Vector3 normal, Vector3 point )
	{
		if ( !AllowClimbing || normal.IsNearZeroLength )
			return;

		_climbing = true;
		_climbNormal = normal.Normal;
		_climbPoint = point;
		_sinceSurface = 0f;
		_climbStill = 0;
		_jumpQueued = false;
		IsGrounded = false;
	}

	void EndClimb()
	{
		if ( !_climbing )
			return;

		_climbing = false;
		SetPhysicsMode( PhysicsMode.Character );
		EaseHullOff( _climbNormal );
	}

	// Mover, airborne: remember the wall or ceiling the hull just slid along, so Space can grab it.
	void NoteAirContact( SteepHit steep )
	{
		_sinceAirWall += Time.Delta;
		if ( !steep.Hit )
			return;
		_airWallNormal = steep.Normal;
		_airWallPoint = steep.Point;
		_sinceAirWall = 0f;
	}

	// Mover, airborne, controlled: grab a wall or ceiling within reach if the player means to.
	//
	// Intent is any of: this airborne stretch began with OUR jump, Space is held, or Space was pressed within
	// ClimbGrabBuffer. (Requiring Space to be held at the instant of contact is what made jump-to-wall so
	// unreliable: a jump is a tap, released long before the wall.)
	//
	// The wall is FOUND, not remembered: a short hull sweep toward where we're steering, out to ClimbGrabReach. A
	// wall is only grabbed if we're steering into it, so jumping alongside a wall never sticks by accident. With
	// no steering, the wall or ceiling we last slid along in the air counts for ClimbGrabWindow. A ceiling needs
	// Space (held or just pressed) and the prop not already falling away from it.
	bool TryAirGrab( Vector3 pos, Rotation rot, Vector3 wish )
	{
		if ( !AllowClimbing )
			return false;

		bool spaceIntent = Input.Down( "jump" ) || _sinceJumpPress < ClimbGrabBuffer || DebugClimbVertical > 0f;
		if ( !spaceIntent && !_airFromJump )
			return false;

		// Walls: sweep the hull the way we're steering.
		var steer = wish.WithZ( 0f );
		if ( !steer.IsNearZeroLength )
		{
			var dir = steer.Normal;
			var tr = HullSweep( pos, rot, pos + dir * ClimbGrabReach );
			if ( tr.Hit && !tr.StartedSolid && !Walkable( tr.Normal ) && tr.Normal.z > -0.7f
				&& Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) > 0.3f )
			{
				// Only the jump itself (Space long released): don't snatch the side of a ledge we're about to clear
				// — jumping onto a counter would otherwise stick to its front. The wall must carry on a step above.
				bool clearingLedge = !spaceIntent && !HullSweep( pos + Vector3.Up * StepHeight, rot,
					pos + Vector3.Up * StepHeight + dir * ClimbGrabReach ).Hit;
				if ( !clearingLedge )
				{
					StartClimb( tr.Normal, tr.HitPosition );
					return _climbing;
				}
			}
		}

		// Ceilings: Space and still rising (or near the top of the arc) into one.
		if ( spaceIntent && _velocity.z > -100f )
		{
			var tr = HullSweep( pos, rot, pos + Vector3.Up * ClimbGrabReach );
			if ( tr.Hit && !tr.StartedSolid && tr.Normal.z <= -0.7f )
			{
				StartClimb( tr.Normal, tr.HitPosition );
				return _climbing;
			}
		}

		// Not steering: the wall or ceiling we just slid along in the air, if Space says so.
		if ( spaceIntent && steer.IsNearZeroLength && _sinceAirWall <= ClimbGrabWindow )
		{
			StartClimb( _airWallNormal, _airWallPoint );
			return _climbing;
		}

		return false;
	}

	SceneTraceResult ClimbTrace( Vector3 from, Vector3 to )
		=> MoveTrace.Body( Body.PhysicsBody, new Transform( from, WorldRotation ), to ).Run();

	// In place of the mover while climbing. `controlled` false = edit mode or a control lock: just hold on.
	void StepClimb( bool controlled )
	{
		var pb = Body.PhysicsBody;
		if ( pb is null || !pb.IsValid() )
			return;

		_jumpQueued = false; // Space means "up" here, never a jump once we let go

		if ( controlled && Input.Pressed( "run" ) )
		{
			EndClimb();
			return;
		}

		var wish = controlled ? ReadWish().WithZ( 0f ) : Vector3.Zero;
		float vertical = controlled ? (Input.Down( "jump" ) ? 1f : 0f) - (Input.Down( "duck" ) ? 1f : 0f) : 0f;
		if ( controlled && DebugClimbVertical != 0f )
			vertical = Math.Clamp( DebugClimbVertical, -1f, 1f );
		bool input = !wish.IsNearZeroLength || vertical != 0f;

		// Holding still on the surface: keyframed, nothing to do until there's input again.
		if ( _physicsMode == PhysicsMode.ClimbHold && !input )
			return;

		var pos = WorldPosition;
		var n = _climbNormal;

		// Sense the surface: from a pose backed off it, trace the real body back toward it.
		var tr = ClimbTrace( pos + n * ClimbBackOff, pos - n * ClimbReach );
		bool surface = tr.Hit && !tr.StartedSolid;
		if ( surface )
		{
			_climbNormal = Vector3.Lerp( _climbNormal, tr.Normal, 0.5f ).Normal; // smooth a bumpy clay or tile face
			_climbPoint = tr.HitPosition;
			_sinceSurface = 0f;
		}
		else
		{
			_sinceSurface += Time.Delta;
			if ( _sinceSurface > ClimbLoseTime )
			{
				EndClimb(); // nothing to hold on to any more — fall
				return;
			}
		}

		// Climbed (or slid) onto something you can stand on: the mover takes it from here.
		if ( Walkable( _climbNormal ) )
		{
			EndClimb();
			return;
		}

		n = _climbNormal;

		// Along the surface: the flat wish projected onto it (on a wall that's sideways only — up and down are
		// Space and Ctrl), plus up/down along the surface's own "up".
		var along = wish - n * Vector3.Dot( wish, n );
		along = along.ClampLength( ClimbSpeed );
		var up = Vector3.Up - n * Vector3.Dot( Vector3.Up, n );
		if ( up.Length > 0.2f )
			along += up.Normal * ClimbSpeed * vertical;

		// Inner corner: moving into a different surface — it takes over (a floor ends the climb next tick).
		if ( !along.IsNearZeroLength )
		{
			var ahead = ClimbTrace( pos, pos + along.Normal * (along.Length * Time.Delta + 3f) );
			if ( ahead.Hit && !ahead.StartedSolid && Vector3.Dot( ahead.Normal, n ) < 0.7f )
			{
				_climbNormal = ahead.Normal;
				_climbPoint = ahead.HitPosition;
				_sinceSurface = 0f;
				n = _climbNormal;
				along -= n * MathF.Min( Vector3.Dot( along, n ), 0f );
			}
		}

		// Hold still when there's no input (or no control) and we're on the surface.
		if ( !input && surface )
		{
			_climbStill++;
			if ( !controlled || _climbStill >= ParkAfterTicks )
			{
				SetPhysicsMode( PhysicsMode.ClimbHold );
				return;
			}
		}
		else
		{
			_climbStill = 0;
		}

		SetPhysicsMode( PhysicsMode.Climbing );
		ApplyHeldFriction();

		// On a surface: press into it. Off one (an outer edge): pull toward where we last touched it, so the prop
		// swings round the edge onto the next face.
		var velocity = surface
			? along - n * ClimbStickSpeed
			: along * 0.5f + (_climbPoint - pb.MassCenter).Normal * ClimbEdgePullSpeed;

		Body.Velocity = ApplyLeash( velocity, pullIn: true );
		CapTurnRate();
	}
}
