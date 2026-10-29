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

	/// <summary>Camera pitch (degrees, looking down) treated as "level" when W/S steer up and down a surface — the
	/// orbit camera rests looking a little down at the prop. Facing a wall, W climbs until the camera looks about
	/// 20° further down than this, then goes down instead.</summary>
	[Property, Group( "Climbing" ), Range( 0f, 45f )] public float ClimbLevelPitch { get; set; } = 15f;

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

	// Corner carry (see StepClimb): the travel direction turned over the last corner, and the keys held then.
	Vector3? _carryDir;
	Vector3 _carryMove;
	RealTimeSince _sinceCornerSwitch = 999f;
	const float CornerSwitchCooldown = 0.2f; // no second corner straight after one — never ping-pong between two

	// Where the climb went still, and on which shape — a hold re-checks the surface if either changes.
	Vector3 _climbHoldPos;
	int _climbHoldHull;

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
		_letGoQueued = false;
		_carryDir = null;
		IsGrounded = false;
	}

	void EndClimb()
	{
		if ( !_climbing )
			return;

		_climbing = false;
		SetPhysicsMode( PhysicsMode.Character );
		EaseHullOff( _climbNormal );

		// Letting go must not re-grab on the next tick: we're in the air right beside (or under) the surface we left,
		// still flagged as airborne from the jump that got us up there, maybe with Space still held from climbing.
		// Clear the intent, and for LetGoCooldown only a FRESH Space press grabs again.
		_airFromJump = false;
		_sinceAirWall = 999f;
		_sinceLetGo = 0f;
	}

	// After letting go: how long only a fresh Space press (not a held one, not the old jump) may grab again.
	const float LetGoCooldown = 0.4f;
	RealTimeSince _sinceLetGo = 999f;

	// Shift, latched per frame (a tap can fall between fixed ticks) and consumed by the climb.
	bool _letGoQueued;

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

		// Just let go: only a Space press made AFTER letting go counts (see EndClimb).
		if ( _sinceLetGo < LetGoCooldown )
			spaceIntent = _sinceJumpPress < _sinceLetGo;

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

		// Ceilings: still rising (or near the top of the arc) into one, on our own jump or with Space. (Space alone
		// wasn't enough: a full jump takes ~0.37s to peak, longer than the press buffer, so a tap-jump into a
		// ceiling near the top of the arc never grabbed it.)
		if ( (spaceIntent || _airFromJump) && _velocity.z > -100f )
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

	// WASD while climbing, by the camera's FULL view (pitch included), flattened onto the surface: W goes where you
	// look — up a wall when you look up it, down when you look down it, forward along a ceiling — and A/D go to the
	// camera's sides. The third-person camera normally looks a little DOWN at the prop, so facing a wall its forward
	// alone would flatten to "down the wall"; the camera's up blends in as its forward turns square to the surface
	// (weight (1 − |flattened forward|)²), so looking at a wall roughly head-on still means W = up.
	Vector3 ClimbAlong( Vector3 move, Vector3 n )
	{
		if ( move.IsNearZeroLength )
			return Vector3.Zero;

		// The orbit camera's resting look-down counts as level, so W's flip from "up" to "down" sits well past it.
		var view = _orbit.IsValid()
			? (_orbit.Angles with { pitch = _orbit.Angles.pitch - ClimbLevelPitch }).ToRotation()
			: Rotation.FromYaw( MoveYaw );
		Vector3 Flat( Vector3 v ) => v - n * Vector3.Dot( v, n );

		var f = Flat( view.Forward );
		float squareOn = 1f - MathF.Min( f.Length, 1f );
		var forward = f + Flat( view.Up ) * squareOn * squareOn;
		var left = Flat( view.Left );

		var dir = (forward.IsNearZeroLength ? Vector3.Zero : forward.Normal) * move.x
			+ (left.IsNearZeroLength ? Vector3.Zero : left.Normal) * move.y;
		return dir.ClampLength( 1f ) * ClimbSpeed;
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

		bool letGo = _letGoQueued;
		_letGoQueued = false;
		if ( controlled && (letGo || Input.Pressed( "run" )) )
		{
			EndClimb();
			return;
		}

		var perfWish = controlled ? PerfScenario.DriveProp( GameObject, ClimbSpeed ) : null;
		var move = controlled ? Input.AnalogMove.ClampLength( 1f ) : Vector3.Zero;
		float vertical = controlled ? (Input.Down( "jump" ) ? 1f : 0f) - (Input.Down( "duck" ) ? 1f : 0f) : 0f;
		if ( controlled && DebugClimbVertical != 0f )
			vertical = Math.Clamp( DebugClimbVertical, -1f, 1f );
		bool input = !move.IsNearZeroLength || perfWish is { IsNearZeroLength: false } || vertical != 0f;

		// Holding still on the surface: keyframed, nothing to do until there's input again — unless something moved us
		// (a teleport) or changed our shape (sculpting while stuck): then re-check, we may now be sunk into the surface.
		_hull.Refresh( _collider, Scene.PhysicsWorld );
		if ( _physicsMode == PhysicsMode.ClimbHold && !input
			&& WorldPosition.AlmostEqual( _climbHoldPos, 0.01f ) && _hull.Version == _climbHoldHull )
			return;

		var pos = WorldPosition;
		var n = _climbNormal;

		// Genuinely inside something (not just sunk into the surface we're on — that pushes out below) for a while:
		// let go, and the mover's buried escape takes over (see HiderController.Buried).
		if ( TooLongBuried( pos ) )
		{
			EndClimb();
			return;
		}

		// Sense the surface: from a pose backed off it, trace the real body back toward it.
		// If the body has been shoved INTO the surface deeper than the back-off, the trace starts inside it and reads
		// start-solid. That used to count as "surface lost", which fired the edge pull — straight at the last contact
		// point, i.e. into the wall, harder — a runaway that carried props through walls. Back off further instead,
		// and if we only find the surface from further out, we're embedded: push OUT this tick.
		SceneTraceResult tr = default;
		bool embedded = false;
		for ( float back = ClimbBackOff; back <= ClimbBackOff * 8f; back *= 2f )
		{
			tr = ClimbTrace( pos + n * back, pos - n * ClimbReach );
			if ( !tr.StartedSolid )
				break;
			embedded = true;
		}
		if ( tr.StartedSolid )
			tr = default; // buried even from 16u out — don't trust it, but still push out (embedded)
		bool surface = tr.Hit || embedded;
		if ( tr.Hit )
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

		// Along the surface: WASD by the camera's full view (see ClimbAlong), plus Space/Ctrl along the surface's
		// own "up".
		var wasd = perfWish is { } pw ? (pw - n * Vector3.Dot( pw, n )).ClampLength( ClimbSpeed ) : ClimbAlong( move, n );

		// Corner carry: still holding the same keys since crossing onto this surface — keep going the way we were
		// going, rotated over the corner, instead of re-reading the camera. Without it W up a wall maps, on the
		// ceiling, to "toward the wall", which crossed straight back — the two surfaces swapped every tick (the
		// jitter that stopped a train sticking to the ceiling). Changing or releasing the keys hands W/S back to
		// the camera.
		if ( _carryDir is { } carry && !move.IsNearZeroLength && Vector3.Dot( move.Normal, _carryMove.Normal ) > 0.95f )
		{
			var flat = carry - n * Vector3.Dot( carry, n );
			if ( !flat.IsNearZeroLength )
				wasd = flat.Normal * ClimbSpeed * MathF.Min( move.Length, 1f );
		}
		else
		{
			_carryDir = null;
		}

		var along = wasd;
		var up = Vector3.Up - n * Vector3.Dot( Vector3.Up, n );
		if ( up.Length > 0.2f )
			along += up.Normal * ClimbSpeed * vertical;

		// Inner corner: moving into a different surface — it takes over (a floor ends the climb next tick). The look-
		// ahead starts backed off BOTH the surface we're on and the way we're going: the body is pressed into the
		// surface (and may already be pressed against the next one — a train shoved up into the ceiling), and a
		// trace that starts touching something reads start-solid and is thrown away, so the ceiling never took over.
		if ( !along.IsNearZeroLength && _sinceCornerSwitch > CornerSwitchCooldown )
		{
			var dir = along.Normal;
			var from = pos + n * ClimbBackOff - dir * ClimbBackOff;
			var ahead = ClimbTrace( from, from + dir * (ClimbBackOff + along.Length * Time.Delta + 3f) );
			if ( ahead.Hit && !ahead.StartedSolid && Vector3.Dot( ahead.Normal, n ) < 0.7f )
			{
				// Turn the motion over the corner edge with the surface: what went into the new surface now goes
				// away from the old one — up a wall becomes along the ceiling, away from the wall.
				var corner = Rotation.FromToRotation( n, ahead.Normal );
				if ( !move.IsNearZeroLength && !wasd.IsNearZeroLength )
				{
					_carryDir = corner * wasd.Normal;
					_carryMove = move;
				}
				along = corner * along;

				_climbNormal = ahead.Normal;
				_climbPoint = ahead.HitPosition;
				_sinceSurface = 0f;
				_sinceCornerSwitch = 0f;
				n = _climbNormal;
			}
		}

		// Hold still when there's no input (or no control) and we're on the surface.
		if ( !input && surface && !embedded ) // never freeze in place while sunk into the surface
		{
			_climbStill++;
			if ( !controlled || _climbStill >= ParkAfterTicks )
			{
				SetPhysicsMode( PhysicsMode.ClimbHold );
				_climbHoldPos = WorldPosition;
				_climbHoldHull = _hull.Version;
				return;
			}
		}
		else
		{
			_climbStill = 0;
		}

		SetPhysicsMode( PhysicsMode.Climbing );
		ApplyHeldFriction();
		UseHullCollision( true );

		// On a surface: press into it. Off one (an outer edge): pull toward where we last touched it, so the prop
		// swings round the edge onto the next face.
		Vector3 velocity;
		if ( embedded )
		{
			// Sunk into the surface: back out of it (along it still works) rather than pressing deeper.
			velocity = along + n * ClimbStickSpeed * 2f;
		}
		else if ( surface )
		{
			velocity = along - n * ClimbStickSpeed;
		}
		else
		{
			// Round an outer edge — but never INTO the old surface faster than the ordinary stick: past the edge the
			// pull wraps us round it, short of the edge it must not ram us through the wall.
			var pull = (_climbPoint - pb.MassCenter).Normal * ClimbEdgePullSpeed;
			float inward = Vector3.Dot( pull, -n );
			if ( inward > ClimbStickSpeed )
				pull -= -n * (inward - ClimbStickSpeed);
			velocity = along * 0.5f + pull;
		}

		Body.Velocity = ApplyLeash( velocity, pullIn: true );
		CapTurnRate();
	}
}
