using System;

namespace Mimiclay;

// Climbing: the press carried on (see HiderController.Press). The body is DYNAMIC and moved by the real SDF
// collider's contacts, with engine gravity swapped for a small pull into the surface it's stuck to. The prop
// stays UPRIGHT throughout — pitch and roll are locked, so physics only ever turns it about the vertical, the same
// as the press. On a wall it hangs upright against it; on a ceiling it hangs upright beneath it.
//
// Controls (Meccha Chameleon's scheme): Space while pressing a wall — or held while jumping into a wall or a
// ceiling — grabs it. Climbing: Space up the surface, Ctrl down it, A/D sideways (camera's left), W/S in depth (forward along
// a ceiling; into a wall, which mounts the top), the mouse turns it, Shift lets go.
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

	// ClimbSpeed for THIS prop: small props climb slower too (see SizeSpeedScale).
	float ScaledClimbSpeed => ClimbSpeed * SizeSpeedScale;

	/// <summary>How hard the prop is pulled into the surface it's stuck to (u/s into it).</summary>
	[Property, Group( "Climbing" )] public float ClimbStickSpeed { get; set; } = 40f;

	/// <summary>Speed the prop is pulled round an outer edge toward the last point it touched (u/s).</summary>
	[Property, Group( "Climbing" )] public float ClimbEdgePullSpeed { get; set; } = 140f;

	/// <summary>How far past the surface the sensing trace looks (u).</summary>
	[Property, Group( "Climbing" )] public float ClimbReach { get; set; } = 8f;

	/// <summary>Extra reach (u) to the wall BEHIND a clay prop on it — fridge magnets, door knobs. Grabs and the climb look
	/// through props (see PropsAsFloor) to the surface they sit on; resting on one, you can be this much further off it.</summary>
	[Property, Group( "Climbing" )] public float PropClingReach { get; set; } = 24f;

	/// <summary>Climbing up off the top of a wall: how far (u) over the lip the prop looks for ground to step onto.</summary>
	[Property, Group( "Climbing" )] public float ClimbMantleReach { get; set; } = 16f;

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

	// Corner carry (see StepClimb): the travel direction turned over the last corner, and the keys held then.
	Vector3? _carryDir;
	Vector3 _carryMove;
	RealTimeSince _sinceCornerSwitch = 999f;
	Vector3 _carryFrom;

	// The surface the last corner took us OFF, and the keys held then. While they're still held, motion back into that
	// surface is dropped (see StepClimb) — otherwise Space (up the wall) and W (toward the wall, on the ceiling) passed
	// the prop back and forth between wall and ceiling every corner cooldown, never settling on either.
	Vector3? _cornerLeft;
	Vector3 _cornerMove;
	float _cornerVertical;

	// Stepping over a bump on the surface (see the corner look-ahead): seconds left of lifting off it.
	float _climbStepOver;
	const float ClimbStepOverTime = 0.12f;

	// cos(35°): a sensed surface angle further than this from the current one is ignored (see the surface sensing).
	const float SurfaceNormalGate = 0.82f;

	// ...but a gated reading that persists this long (seconds) is a real surface, not a passing bump edge.
	const float SurfaceGateHold = 0.2f;
	const float FacingGate = 0.3f;
	float _surfaceGated;
	const float CornerSwitchCooldown = 0.2f; // no second corner straight after one — never ping-pong between two
	const float CornerMinSpeed = 5f; // u/s along the surface before the corner look-ahead runs

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
		_climbStepOver = 0f;
		_surfaceGated = 0f;
		_letGoQueued = false; // a Shift tap from before the grab must not drop us at once
		_cornerLeft = null;
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

	// Possessed while stuck to a wall or ceiling (user, 2026-10-29): a fridge magnet, a door knob, a light switch or a
	// hanging lamp is placed in the map against its surface with nothing under it, so the moment it became a body it
	// fell off. The first controlled tick checks: no floor within a step below, but a wall or ceiling within ClingReach
	// — start clinging to it, as if climbed there (held still until you move; Shift lets go). Set on creation and on
	// ResumeControl; cleared after one check.
	bool _clingCheckPending = true;
	const float ClingReach = 4f;

	// Hull sweep along `dir` starting a little BEHIND the pose (as the climb's sensing does), so a prop placed touching
	// or slightly sunk into its surface still finds it. Returns the hit and the gap from the pose itself (negative = sunk in).
	(SceneTraceResult Tr, float Gap) ProbeToward( Vector3 pos, Rotation rot, Vector3 dir, float reach )
	{
		SceneTraceResult tr = default;
		for ( float back = 1f; back <= 8f; back *= 2f )
		{
			tr = HullSweep( pos - dir * back, rot, pos + dir * reach );
			if ( !tr.StartedSolid )
				return (tr, tr.Fraction * (back + reach) - back);
		}
		return (default, 0f);
	}

	bool TryClingOnPossess( Vector3 pos, Rotation rot )
	{
		_clingCheckPending = false;
		if ( !AllowClimbing || !_hull.Ready )
			return false;

		// Something to stand on: an ordinary start.
		var (down, _) = ProbeToward( pos, rot, Vector3.Down, ScaledStep( StepHeight ) + Skin * 2f );
		if ( down.Hit && WalkableHit( down ) )
			return false;

		// The nearest wall or ceiling: world axes and the prop's own (a magnet on an angled face), and straight up.
		Span<Vector3> dirs = stackalloc Vector3[]
		{
			Vector3.Forward, Vector3.Backward, Vector3.Left, Vector3.Right,
			rot.Forward.WithZ( 0f ).Normal, -rot.Forward.WithZ( 0f ).Normal, rot.Left.WithZ( 0f ).Normal, -rot.Left.WithZ( 0f ).Normal,
			Vector3.Up,
		};
		SceneTraceResult best = default;
		float bestGap = float.MaxValue;
		foreach ( var dir in dirs )
		{
			if ( dir.IsNearZeroLength )
				continue;
			var (tr, gap) = ProbeToward( pos, rot, dir, ClingReach );
			if ( !tr.Hit || Walkable( tr.Normal ) || IsFloorProp( tr ) || gap >= bestGap )
				continue;
			best = tr;
			bestGap = gap;
		}
		if ( !best.Hit )
			return false;

		// Start from rest: the body's first step as a pawn can carry a huge velocity (computed from wherever it was before).
		_velocity = Vector3.Zero;
		if ( Body.PhysicsBody is { } b && b.IsValid() )
		{
			b.Velocity = Vector3.Zero;
			b.AngularVelocity = Vector3.Zero;
		}
		StartClimb( best.Normal, best.HitPosition );
		return _climbing;
	}

	// Climbing UP off the top of a wall (it ended under us — nothing left to hold): if there's ground just over the lip,
	// step onto it and hand back to the mover. Without this the outer-edge pull dragged the prop back toward its last
	// contact, BELOW the lip, so it bobbed at the top of the tablecloth, still climbing, and never got onto the table.
	bool TryMantle( Vector3 pos, Vector3 n )
	{
		var into = (-n).WithZ( 0f );
		if ( into.IsNearZeroLength || !_hull.Ready )
			return false;
		into = into.Normal;
		var rot = WorldRotation;

		// Over the lip (as far as nothing blocks), then down onto something we can stand on.
		var from = pos + Vector3.Up * Skin * 2f;
		var fwd = HullSweep( from, rot, from + into * ClimbMantleReach );
		if ( fwd.StartedSolid )
			return false;

		// A wall carrying on just behind the lip means this is only a small LEDGE on the way up (a shelf's lip, a washing
		// machine door's rim), not a top: keep climbing. Aim the outer-edge pull at that wall instead of the last contact
		// below the lip, so the prop steps in onto it. (Clay props over the lip don't count — they're floor, see PropsAsFloor.)
		if ( fwd.Hit && !Walkable( fwd.Normal ) && !IsFloorProp( fwd ) )
		{
			_climbPoint = fwd.HitPosition;
			_sinceSurface = 0f; // the wall is right there — don't let go while we step in onto it
			return false;
		}
		var over = fwd.Hit ? fwd.EndPosition - into * Skin : from + into * ClimbMantleReach;
		if ( (over - from).WithZ( 0f ).Length < 2f )
			return false;
		var down = HullSweep( over, rot, over - Vector3.Up * (StepHeight + ClimbMantleReach) );
		if ( down.StartedSolid || !down.Hit || !WalkableHit( down ) )
			return false;

		EndClimb();
		WorldPosition = down.EndPosition + Vector3.Up * Skin;
		_velocity = into * MathF.Min( ClimbSpeed, WalkSpeed ) * 0.5f; // a little carry onto the top, then the mover's own control
		IsGrounded = true;
		_groundNormal = down.Normal;
		Unpark();
		return true;
	}

	// After letting go: how long only a fresh Space press (not a held one, not the old jump) may grab again.
	const float LetGoCooldown = 0.4f;
	RealTimeSince _sinceLetGo = 999f;

	// Shift, latched per frame by OnUpdate and consumed by the climb.
	bool _letGoQueued;

	// Mover, airborne: remember the wall or ceiling the hull just slid along, so Space can grab it.
	void NoteAirContact( SteepHit steep )
	{
		_sinceAirWall += Time.Delta;
		if ( !steep.Hit || steep.FloorProp ) // floor-like props are never grabbed (see PropsAsFloor)
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
			var tr = GrabSweep( pos, rot, dir, ClimbGrabReach );
			if ( tr.Hit && !tr.StartedSolid && !Walkable( tr.Normal ) && tr.Normal.z > -0.7f && !IsFloorProp( tr )
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
			var tr = GrabSweep( pos, rot, Vector3.Up, ClimbGrabReach );
			if ( tr.Hit && !tr.StartedSolid && tr.Normal.z <= -0.7f && !IsFloorProp( tr ) )
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

	// WASD while climbing (user, 2026-10-29): A/D go SIDEWAYS along the surface, by the camera's left; W/S go in DEPTH —
	// the camera's level forward, kept only as far as the surface faces up or down: all of it on a ceiling (W = forward
	// along it), none on an upright wall, where W only pushes INTO the wall (ClimbInto — that's what mounts a ledge at the
	// top). Up and down a wall are Space and Ctrl. Pitch is ignored, so neither wobbles with the look or a bumpy face.
	Vector3 ClimbAlong( Vector3 move, Vector3 n )
	{
		if ( move.IsNearZeroLength )
			return Vector3.Zero;

		var view = Rotation.FromYaw( _orbit.IsValid() ? _orbit.Angles.yaw : MoveYaw );
		Vector3 Flat( Vector3 v ) => v - n * Vector3.Dot( v, n );

		var depth = Flat( view.Forward ) * MathF.Abs( n.z );
		var side = Flat( view.Left );
		side = side.Length >= 0.3f ? side.Normal : side / 0.3f; // looking along the wall: A/D say little, so slow rather than snap

		return (depth * move.x + side * move.y).ClampLength( 1f ) * ScaledClimbSpeed;
	}

	// How hard W/S push INTO the surface (0..1, S negative): the level look's share pointing at it. Mounts a ledge.
	float ClimbInto( Vector3 move, Vector3 n )
	{
		var forward = Rotation.FromYaw( _orbit.IsValid() ? _orbit.Angles.yaw : MoveYaw ).Forward;
		return move.x * Vector3.Dot( forward, -n );
	}

	SceneTraceResult ClimbTrace( Vector3 from, Vector3 to, bool throughProps = false )
		=> (throughProps ? PropFreeTrace : MoveTrace).Body( Body.PhysicsBody, new Transform( from, WorldRotation ), to ).Run();

	// Grabs and the climb look THROUGH clay props (see PropsAsFloor) to the wall they sit on: a fridge covered in magnets
	// is still a fridge. Only the corner look-ahead sees them, to step over them like bumps.
	SceneTrace PropFreeTrace => PropsAsFloor ? MoveTrace.WithoutTags( SdfCollider.ClayTag ) : MoveTrace;

	// A grab sweep that stopped on a clay prop: sweep again through props, to the wall behind it.
	SceneTraceResult GrabSweep( Vector3 from, Rotation rot, Vector3 dir, float reach )
	{
		var tr = HullSweep( from, rot, from + dir * reach );
		if ( tr.Hit && !tr.StartedSolid && IsFloorProp( tr ) )
			tr = _hull.Sweep( PropFreeTrace, new Transform( from, rot ), new Transform( from + dir * (reach + PropClingReach), rot ) );
		return tr;
	}

	// In place of the mover while climbing. `controlled` false = edit mode or a control lock: just hold on.
	void StepClimb( bool controlled )
	{
		var pb = Body.PhysicsBody;
		if ( pb is null || !pb.IsValid() )
			return;

		// Space is "up the surface" while climbing (held, below) — a press must never queue a jump for after we let go.
		_jumpQueued = false;

		// Shift lets go — latched per frame (see OnUpdate) so a quick tap can't fall between fixed ticks.
		bool letGo = _letGoQueued;
		_letGoQueued = false;
		if ( controlled && letGo )
		{
			EndClimb();
			return;
		}

		var perfWish = controlled ? PerfScenario.DriveProp( GameObject, ScaledClimbSpeed ) : null;
		var move = controlled ? Input.AnalogMove.ClampLength( 1f ) : Vector3.Zero;

		// Space climbs UP the surface and Ctrl DOWN it (user, 2026-10-29). A/D go sideways and W/S in depth (ClimbAlong).
		// The debug ConVar fakes holding Space or Ctrl.
		float vertical = controlled ? (Input.Down( "jump" ) ? 1f : 0f) - (Input.Down( "duck" ) ? 1f : 0f) : 0f;
		if ( controlled && DebugClimbVertical != 0f )
			vertical = Math.Clamp( DebugClimbVertical, -1f, 1f );
		float turn = controlled ? ReadTurn() : 0f; // the mouse turns a climbing prop too (see ApplySurfaceTurn)
		bool input = !move.IsNearZeroLength || perfWish is { IsNearZeroLength: false } || vertical != 0f || MathF.Abs( turn ) > 0.01f;

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
			tr = ClimbTrace( pos + n * back, pos - n * ClimbReach, throughProps: true );
			if ( !tr.StartedSolid )
				break;
			embedded = true;
		}
		// Not on the wall itself — resting on a prop that sits on it (a fridge magnet, a door knob)? Reach past it.
		if ( !tr.Hit && !tr.StartedSolid && PropsAsFloor )
		{
			var onProp = ClimbTrace( pos + n * ClimbBackOff, pos - n * ClimbReach );
			if ( onProp.Hit && !onProp.StartedSolid && IsFloorProp( onProp ) )
				tr = ClimbTrace( pos + n * ClimbBackOff, pos - n * (ClimbReach + PropClingReach), throughProps: true );
		}
		if ( tr.StartedSolid )
			tr = default; // buried even from 16u out — don't trust it, but still push out (embedded)
		bool surface = tr.Hit || embedded;
		if ( tr.Hit )
		{
			// Smooth a bumpy clay or tile face — but a reading far off the surface we're on is usually a bump's edge or
			// side (a fridge letter) that we're climbing PAST, so its angle is ignored. Unless we're not moving and it has
			// lasted SurfaceGateHold: then it's a surface we're really resting on (a train that slid onto the top of a
			// pot kept pressing diagonally into it on a stale normal, bouncing between "on it" and "lost it"). Adopted,
			// it follows the usual rules — ground we can stand on ends the climb below.
			// (Taking walkable readings straight away instead ended climbs on a passing letter's sloped top.)
			bool closeToCurrent = Vector3.Dot( tr.Normal, _climbNormal ) > SurfaceNormalGate;
			// Reading the ground ON TOP of the wall (the body grazes the table as it rises past the tablecloth's lip): step onto
			// it now. Waiting for the gate below kept a moving prop climbing, unable to get onto the table. A mantle fails
			// harmlessly while the wall is still in the way (the floor at the foot of a climb).
			if ( !closeToCurrent && WalkableHit( tr ) && MathF.Abs( _climbNormal.z ) < 0.7f && TryMantle( pos, _climbNormal ) )
				return;
			bool moving = perfWish is { IsNearZeroLength: false } || !move.IsNearZeroLength || vertical != 0f;
			_surfaceGated = closeToCurrent || moving ? 0f : _surfaceGated + Time.Delta;
			if ( closeToCurrent )
				_climbNormal = Vector3.Lerp( _climbNormal, tr.Normal, 0.5f ).Normal;
			// Only a surface that FACES us along the stick (dot > FacingGate) counts: a floor the prop grazes while
			// starting a climb from the ground is square to the wall, not under the stick.
			else if ( Vector3.Dot( tr.Normal, _climbNormal ) > FacingGate && _surfaceGated > SurfaceGateHold )
			{
				_climbNormal = tr.Normal;
				_surfaceGated = 0f;
			}
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

		// Along the surface: WASD by the camera's full view (see ClimbAlong), plus the test harness's push along the surface's
		// own "up".
		var wasd = perfWish is { } pw ? (pw - n * Vector3.Dot( pw, n )).ClampLength( ScaledClimbSpeed ) : ClimbAlong( move, n );
		float into = controlled ? ClimbInto( move, n ) : 0f; // W at the wall: "get onto the top" when it runs out

		// Corner carry: still holding the same keys since crossing onto this surface — keep going the way we were
		// going, rotated over the corner, instead of re-reading the camera. Without it W up a wall maps, on the
		// ceiling, to "toward the wall", which crossed straight back — the two surfaces swapped every tick (the
		// jitter that stopped a train sticking to the ceiling). Changing or releasing the keys hands W/S back to
		// the camera.
		//
		// Back on a surface facing the way the one we left did (we went round a bump-like corner and came back): the
		// carry has done its job — W/S follow the camera again.
		if ( _carryDir is not null && Vector3.Dot( n, _carryFrom ) > 0.97f )
			_carryDir = null;
		if ( _carryDir is { } carry && !move.IsNearZeroLength && Vector3.Dot( move.Normal, _carryMove.Normal ) > 0.95f )
		{
			var flat = carry - n * Vector3.Dot( carry, n );
			if ( !flat.IsNearZeroLength )
				wasd = flat.Normal * ScaledClimbSpeed * MathF.Min( move.Length, 1f );
		}
		else
		{
			_carryDir = null;
		}

		var along = wasd;
		var up = Vector3.Up - n * Vector3.Dot( Vector3.Up, n );
		if ( up.Length > 0.2f )
			along += up.Normal * ScaledClimbSpeed * vertical;

		// Just round a corner, same keys still held: don't head back into the surface we left (see _cornerLeft).
		if ( _cornerLeft is { } left )
		{
			if ( vertical != _cornerVertical || (move - _cornerMove).Length > 0.05f )
				_cornerLeft = null; // new keys: a fresh press back toward it is meant
			else if ( Vector3.Dot( along, -left ) > 0f )
				along -= -left * Vector3.Dot( along, -left );
		}

		// Inner corner: moving into a different surface — it takes over (a floor ends the climb next tick). The look-
		// ahead starts backed off BOTH the surface we're on and the way we're going: the body is pressed into the
		// surface (and may already be pressed against the next one — a train shoved up into the ceiling), and a
		// trace that starts touching something reads start-solid and is thrown away, so the ceiling never took over.
		// Only real movement looks for corners: a wish straight INTO the surface flattens to a few float-noise u/s whose
		// direction is meaningless — it pointed the look-ahead at the floor and ended climbs started on the ground.
		if ( along.Length > CornerMinSpeed && _sinceCornerSwitch > CornerSwitchCooldown )
		{
			var dir = along.Normal;
			var from = pos + n * ClimbBackOff - dir * ClimbBackOff;
			float reach = ClimbBackOff + along.Length * Time.Delta + 3f;
			var ahead = ClimbTrace( from, from + dir * reach );
			bool bump = false;
			if ( ahead.Hit && !ahead.StartedSolid && Vector3.Dot( ahead.Normal, n ) < 0.7f )
			{
				// A real corner, or a BUMP on this surface (another fridge letter)? Look again from a step's height
				// off the surface: a ceiling or the next wall is still there, a bump is cleared. Bumps are climbed
				// OVER (see _climbStepOver below), like steps on the floor — switching onto their rounded edges is
				// what turned "W up the fridge" into up-and-left.
				var lifted = from + n * StepHeight;
				var over = ClimbTrace( lifted, lifted + dir * reach );
				bump = !over.Hit && !over.StartedSolid;
				// Never turn the corner ONTO a clay prop (see PropsAsFloor): climb over it like a bump instead.
				bump |= IsFloorProp( ahead );
			}
			if ( bump )
			{
				_climbStepOver = ClimbStepOverTime;
			}
			else if ( ahead.Hit && !ahead.StartedSolid && Vector3.Dot( ahead.Normal, n ) < 0.7f )
			{
				// Turn the motion over the corner edge with the surface: what went into the new surface now goes
				// away from the old one — up a wall becomes along the ceiling, away from the wall.
				var corner = Rotation.FromToRotation( n, ahead.Normal );
				if ( !move.IsNearZeroLength && !wasd.IsNearZeroLength )
				{
					_carryDir = corner * wasd.Normal;
					_carryMove = move;
					_carryFrom = n; // the surface we left — back on one facing this way, the carry is dropped
				}
				along = corner * along;

				_cornerLeft = n;
				_cornerMove = move;
				_cornerVertical = vertical;
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
				pb.Velocity = Vector3.Zero; // held means held: a keyframed body still drifts at whatever velocity it last had
				pb.AngularVelocity = Vector3.Zero; // (a just-possessed magnet rose 80u up the fridge at 846 u/s)
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
		else if ( _climbStepOver > 0f )
		{
			// Stepping over a bump: lift off the surface while we pass it instead of pressing in; the stick pulls us
			// back down once it's behind us.
			velocity = along + n * ClimbSpeed;
		}
		else if ( surface )
		{
			velocity = along - n * ClimbStickSpeed;
		}
		else if ( (along.z > 1f || into > 0.3f) && MathF.Abs( n.z ) < 0.7f && TryMantle( pos, n ) )
		{
			return; // climbed off the top of a wall onto the ground above it
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
		_climbStepOver = MathF.Max( 0f, _climbStepOver - Time.Delta );
		ApplySurfaceTurn( turn );
	}
}
