using System;

namespace Mimiclay;

// Climbing: the body is DYNAMIC (as when walking, see HiderController.Walk) and collides as its move hull, with gravity
// swapped for a small pull into the surface it's stuck to. The prop stays UPRIGHT throughout — pitch and roll are locked,
// so physics only ever turns it about the vertical. On a wall it hangs upright against it; on a ceiling it hangs upright beneath it.
//
// Controls (Meccha Chameleon's scheme): Space held (or just pressed) while jumping into a wall or a ceiling grabs it;
// possessing a prop stuck to one starts it clinging (TryClingOnPossess). Climbing: Space up the surface, Ctrl down it, A/D sideways (camera's left), W/S in depth (forward along
// a ceiling; into a wall, which mounts the top), the mouse turns it, Shift lets go.
//
// Each tick the surface is SENSED with a short trace of the real rigidbody (translation only — all the engine's
// body traces can do) from a pose backed a little off the surface, toward it. That gives the surface's normal and
// the contact point:
//  • inner corner — a trace along the way we're moving hits a different surface: it takes over (a floor ends the
//    climb and walking stands on it);
//  • outer edge — the surface is gone: the pull aims at the LAST contact point instead, so the prop swings round the
//    edge like a magnet until it finds the next face (or lets go after ClimbLoseTime).
//
// No input for a few ticks (or edit mode / a control lock) HOLDS it: the body goes keyframed exactly where it is,
// so a prop hiding on a wall never slides and costs nothing — and can be sculpted in mid-air. Any input lets go of
// the hold. Letting go of the climb hands the body back to walking, which falls and parks as normal.
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

	/// <summary>Step onto ground over the top of a wall when the climb reaches it (TryMantle). Off: the climb lets go at the
	/// top and the walk's ground spring has to pick it up (tried 2026-10-30 — it needs the mantle).</summary>
	[Property, Group( "Climbing" )] public bool ClimbMantle { get; set; } = true;

	/// <summary>The mantle's forward reach over the lip, as a fraction of the prop's narrowest width — just far enough for
	/// the top to catch it; the walk carries it the rest of the way.</summary>
	[Property, Group( "Climbing" ), Range( 0.05f, 1f )] public float ClimbMantleFraction { get; set; } = 0.25f;

	/// <summary>The most (u) the mantle moves any prop over the lip.</summary>
	[Property, Group( "Climbing" )] public float ClimbMantleReach { get; set; } = 6f;

	// The mantle's reach for THIS prop (user, 2026-10-30: a tiny prop reaching a pot's lip was placed a fixed 16u — several
	// of its own lengths — forward; then "reduce it a lot to prevent it feeling jittery").
	float MantleReach => _hull.Ready
		? Math.Clamp( MathF.Min( _hull.LocalBounds.Size.x, _hull.LocalBounds.Size.y ) * ClimbMantleFraction, 1f, ClimbMantleReach )
		: ClimbMantleReach;

	/// <summary>Seconds without any surface before a climbing prop lets go and falls.</summary>
	[Property, Group( "Climbing" ), Range( 0.05f, 2f )] public float ClimbLoseTime { get; set; } = 0.35f;

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

	// On a ceiling we climbed onto from a wall: the way AWAY from that wall (its normal, flat). Space keeps going this way
	// there — out along the underside — since a ceiling has no "up the surface"; Ctrl comes back.
	Vector3 _ceilingOut;
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
		_ceilingOut = Vector3.Zero;
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

	// Climbing UP to the top of a wall: once the top is within a step of our base, let go and WALK onto it. Checked every tick
	// we're climbing up or pushing into the wall: from a step up (where the walk's floating shape starts), is the wall gone
	// a little way in, with ground under it no higher than a step? Then the climb ends with a push over the lip, and the
	// walk does the rest the way it climbs any step — its floating shape clears the lip, its ground spring lifts us onto
	// the top. (Before 2026-10-30 this TELEPORTED the prop onto the top: a long teleport looked like a jump for a small
	// prop, a short one left it balanced on the edge, "stuck to the top of the mantle".) Without a mantle the outer-edge
	// pull drags the prop back toward its last contact, below the lip, and it bobs there.
	//
	// `lost` = the wall has already gone from under us: then a wall a step up behind the lip means a small LEDGE on the
	// way up (a shelf's lip, a washing machine door's rim), and the edge pull is aimed at that wall so we step in onto it.
	bool TryMantle( Vector3 pos, Vector3 n, bool lost )
	{
		if ( !ClimbMantle )
			return false;
		var into = (-n).WithZ( 0f );
		if ( into.IsNearZeroLength || !_hull.Ready )
			return false;
		into = into.Normal;
		var rot = WorldRotation;
		float lift = ScaledStep( StepHeight );
		float reach = MantleReach;

		// A step up, a little way in: open (over the top), or still wall?
		var from = pos + Vector3.Up * lift;
		var fwd = HullSweep( from, rot, from + into * reach );
		if ( fwd.StartedSolid )
			return false;
		if ( fwd.Hit && !Walkable( fwd.Normal ) && !IsFloorProp( fwd ) )
		{
			if ( lost )
			{
				_climbPoint = fwd.HitPosition;
				_sinceSurface = 0f; // the wall is right there — don't let go while we step in onto it
			}
			return false;
		}

		// The top under that raised pose: ground we can stand on, no more than a step above our base.
		var over = fwd.Hit ? fwd.EndPosition - into * Skin : from + into * reach;
		var down = HullSweep( over, rot, over - Vector3.Up * (lift + reach) );
		if ( down.StartedSolid || !down.Hit || !WalkableHit( down ) )
			return false;
		if ( down.EndPosition.z - pos.z > lift )
			return false; // still more than a step to go — keep climbing

		// Hand over: let go and start the walk's guided carry over the lip (see StepWalk — no gravity, in toward the top and
		// up to its height until the ground spring has it). Just letting go dropped the prop: it was still beside the wall's
		// face, nothing under it, so it sank below the lip before it got over — then W + Space re-grabbed the wall, again
		// and again: "stuck to the top".
		EndClimb();
		StartMantleCarry( into, down.EndPosition.z + Skin, 0.4f );
		return true;
	}

	// After letting go: how long only a fresh Space press (not a held one, not the old jump) may grab again.
	const float LetGoCooldown = 0.4f;
	RealTimeSince _sinceLetGo = 999f;

	// Shift, latched per frame by OnUpdate and consumed by the climb.
	bool _letGoQueued;

	// Mover, airborne, controlled: grab a wall or ceiling within reach if the player means to.
	//
	// Intent is any of: this airborne stretch began with OUR jump, Space is held, or Space was pressed within
	// ClimbGrabBuffer. (Requiring Space to be held at the instant of contact is what made jump-to-wall so
	// unreliable: a jump is a tap, released long before the wall.)
	//
	// The wall is FOUND, not remembered: a short hull sweep toward where we're steering, out to ClimbGrabReach. A
	// wall is only grabbed if we're steering into it, so jumping alongside a wall never sticks by accident. With
	// no steering, Space sweeps the way the camera faces (jump straight up at a wall you're looking at). A ceiling needs
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

		// Walls: sweep the hull the way we're steering (with Space and no steering, the way the camera faces).
		var steer = wish.WithZ( 0f );
		if ( steer.IsNearZeroLength && spaceIntent )
			steer = Rotation.FromYaw( MoveYaw ).Forward;
		if ( !steer.IsNearZeroLength )
		{
			var dir = steer.Normal;
			var tr = GrabSweep( pos, rot, dir, ClimbGrabReach );
			if ( tr.Hit && !tr.StartedSolid && !Walkable( tr.Normal ) && tr.Normal.z > -0.7f && !IsFloorProp( tr )
				&& Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) > 0.3f )
			{
				// Only the jump itself (Space long released): don't snatch the side of a ledge we're about to clear
				// — jumping onto a counter would otherwise stick to its front. The wall must carry on a step above.
				float lip = ScaledStep( StepHeight );
				bool clearingLedge = !spaceIntent && !HullSweep( pos + Vector3.Up * lip, rot,
					pos + Vector3.Up * lip + dir * ClimbGrabReach ).Hit;
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

	// Grabs and the climb look THROUGH floor-like clay props (see PropsAsFloor — props smaller than us) to the surface they
	// sit on: a fridge covered in magnets is still a fridge. Each floor prop hit is ignored and the trace re-run (a few
	// deep); a prop bigger than us is a surface and stops it. Only the corner look-ahead sees small props, to step over them.
	[ConVar( "mimi_dbg_climb_corners", Help = "Debug: log the climb's corner/bump decisions (and the shallow-overhang rays)." )]
	public static bool DebugClimbCorners { get; set; }

	// Climbing along a CEILING (the underside of a table top, a worktop overhang) and out past its outer edge: mantle up onto
	// its TOP. Look for it just back over the edge and above us — the hull swept down from high above — and if there's ground
	// we can stand on no more than ~1.5 body heights up, hand over to the walk's guided carry (up past the edge, then in over
	// the top). (Wrapping onto the edge's front face and climbing it relied on the outer-edge pull to lift us beside it; it
	// dragged the prop into the corner instead.)
	bool TryMantleFromUnder( Vector3 pos, Vector3 n, Vector3 along )
	{
		if ( !ClimbMantle || n.z > -0.5f || !_hull.Ready )
			return false;
		var d = along.WithZ( 0f );
		d = d.Length > 5f ? d.Normal : _ceilingOut;
		if ( d.IsNearZeroLength )
			return false;
		var rot = WorldRotation;
		float height = _hull.LocalBounds.Size.z;
		float reach = MantleReach;
		float maxRise = height * 1.5f + ScaledStep( StepHeight );

		var above = pos + Vector3.Up * (maxRise + 2f) - d * (reach + 2f); // back over the edge, high
		var down = HullSweep( above, rot, above - Vector3.Up * (maxRise + 2f) );
		if ( down.StartedSolid || !down.Hit || !WalkableHit( down ) )
			return false;
		float rise = down.EndPosition.z - pos.z;
		if ( rise < height * 0.5f || rise > maxRise )
			return false; // not a top over our head (or too far up)

		EndClimb();
		StartMantleCarry( -d, down.EndPosition.z + Skin, 0.8f );
		if ( DebugClimbCorners )
			Log.Info( $"[climb corner] mantle from under a ceiling: up {rise:0} onto {down.GameObject?.Name}" );
		return true;
	}

	// Is the downward-facing surface we just met above us on a wall only a shallow LIP — no deeper than a step? Thin rays
	// straight up from just under it, a step (+1u) out from the wall, across the prop's width: all clear = shallow.
	bool ShallowOverhang( in SceneTraceResult ahead, Vector3 n )
	{
		float step = ScaledStep( StepHeight );
		var side = Vector3.Cross( n, Vector3.Up );
		side = side.IsNearZeroLength ? Vector3.Zero : side.Normal;
		float half = _hull.Ready ? MathF.Min( _hull.LocalBounds.Size.x, _hull.LocalBounds.Size.y ) * 0.4f : 0f;
		var trace = MoveTrace;
		foreach ( float s in stackalloc float[] { 0f, -half, half } )
		{
			var o = ahead.HitPosition + n * (step + 1f) + side * s - Vector3.Up;
			var r = trace.Ray( o, o + Vector3.Up * (step + 6f) ).Run();
			if ( r.Hit )
			{
				if ( DebugClimbCorners )
					Log.Info( $"[climb corner] overhang ray {s:0} hit {r.GameObject?.Name} {r.Distance:0.0}u up (start-solid {r.StartedSolid}) from {o:0}, underside hit at {ahead.HitPosition:0} on {ahead.GameObject?.Name}" );
				return false;
			}
		}
		return true;
	}

	SceneTraceResult ClimbTrace( Vector3 from, Vector3 to, bool throughProps = false )
	{
		var trace = MoveTrace;
		var tx = new Transform( from, WorldRotation );
		var tr = trace.Body( Body.PhysicsBody, tx, to ).Run();
		for ( int i = 0; throughProps && i < 4 && tr.Hit && IsFloorProp( tr ); i++ )
		{
			trace = trace.IgnoreGameObjectHierarchy( tr.Body?.GameObject ?? tr.GameObject );
			tr = trace.Body( Body.PhysicsBody, tx, to ).Run();
		}
		return tr;
	}

	// A grab sweep that stopped on a floor-like prop: sweep on past it (and the next), to the surface behind.
	SceneTraceResult GrabSweep( Vector3 from, Rotation rot, Vector3 dir, float reach )
	{
		var tr = HullSweep( from, rot, from + dir * reach );
		var trace = MoveTrace;
		for ( int i = 0; i < 4 && tr.Hit && IsFloorProp( tr ); i++ )
		{
			trace = trace.IgnoreGameObjectHierarchy( tr.Body?.GameObject ?? tr.GameObject );
			tr = _hull.Sweep( trace, new Transform( from, rot ), new Transform( from + dir * (reach + PropClingReach), rot ) );
		}
		return tr;
	}

	// Climbing: has the body been genuinely inside something for BuriedHandBackTime? Then it lets go and walks, and the
	// physics solver pushes it out. Checked only every few ticks — it's a pose test.
	const float BuriedHandBackTime = 0.3f;
	float _dynamicBuriedTime;
	int _dynamicBuriedCheck;

	bool TooLongBuried( Vector3 pos )
	{
		if ( ++_dynamicBuriedCheck % 3 != 0 )
			return false;

		_hull.Refresh( _collider, Scene.PhysicsWorld );
		if ( _hull.Ready && PoseBlocked( pos, WorldRotation ) )
			_dynamicBuriedTime += Time.Delta * 3f;
		else
			_dynamicBuriedTime = 0f;

		if ( _dynamicBuriedTime < BuriedHandBackTime )
			return false;
		_dynamicBuriedTime = 0f;
		return true;
	}

	// In place of walking while climbing. `controlled` false = edit mode or a control lock: just hold on.
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
		// let go and walk; the physics solver pushes the body out.
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
			if ( !closeToCurrent && WalkableHit( tr ) && MathF.Abs( _climbNormal.z ) < 0.7f && TryMantle( pos, _climbNormal, lost: false ) )
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

		// Climbed (or slid) onto something you can stand on: walking takes it from here.
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

		var up = Vector3.Up - n * Vector3.Dot( Vector3.Up, n );
		var climbUp = Vector3.Zero; // Space / Ctrl
		if ( up.Length > 0.2f )
			climbUp = up.Normal * ScaledClimbSpeed * vertical;
		else if ( !_ceilingOut.IsNearZeroLength )
			climbUp = _ceilingOut * ScaledClimbSpeed * vertical; // under a ceiling: Space carries on out from the wall below

		// Just round a corner, same keys still held: don't head back into the surface we left (see _cornerLeft). Dropped from
		// WASD and from Space/Ctrl SEPARATELY: on a ceiling reached up a wall, W (toward that wall) and Space (out from it)
		// cancelled exactly, so dropping "back into the wall" from their sum dropped nothing and W + Space hung there.
		if ( _cornerLeft is { } left )
		{
			if ( vertical != _cornerVertical || (move - _cornerMove).Length > 0.05f )
				_cornerLeft = null; // new keys: a fresh press back toward it is meant
			else
			{
				Vector3 NotBack( Vector3 v ) => Vector3.Dot( v, -left ) > 0f ? v + left * Vector3.Dot( v, -left ) : v;
				wasd = NotBack( wasd );
				climbUp = NotBack( climbUp );
			}
		}
		var along = wasd + climbUp;

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
				var lifted = from + n * ScaledStep( StepHeight ); // a bump is anything this prop could step over
				var over = ClimbTrace( lifted, lifted + dir * reach );
				bump = !over.Hit && !over.StartedSolid;
				// Never turn the corner ONTO a clay prop (see PropsAsFloor): climb over it like a bump instead.
				bump |= IsFloorProp( ahead );
				// Climbing UP a wall into the underside of a SHALLOW overhang (a worktop's lip over its cabinet, ~3u): step out
				// over it like a bump, don't turn onto it as a ceiling. In a nook the lifted body trace above starts touching
				// the cabinet side and reads "blocked", so the lip won as a ceiling and the climb was passed between wall, side
				// and underside for ever; rays (see ShallowOverhang) can't be blocked that way.
				bump |= MathF.Abs( n.z ) < 0.5f && dir.z > 0.5f && ahead.Normal.z < -0.3f && ShallowOverhang( ahead, n );
			}
			if ( DebugClimbCorners && ahead.Hit && Vector3.Dot( ahead.Normal, n ) < 0.7f )
				Log.Info( $"[climb corner] on n={n:0.00} moving {dir:0.00}: ahead {ahead.GameObject?.Name} n={ahead.Normal:0.00} start-solid {ahead.StartedSolid} → {(bump ? "BUMP" : "corner")}" );
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
				if ( MathF.Abs( n.z ) < 0.5f && ahead.Normal.z < -0.5f )
					_ceilingOut = n.WithZ( 0f ).Normal; // up a wall onto a ceiling: Space now heads out along it
				else if ( ahead.Normal.z > -0.5f )
					_ceilingOut = Vector3.Zero;
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

		// Climbing up (or pushing into the wall) and the top is within a step: walk onto it (see TryMantle).
		if ( (along.z > 1f || into > 0.3f) && MathF.Abs( n.z ) < 0.7f && TryMantle( pos, n, lost: !surface ) )
			return;

		// Off the outer edge of a ceiling while heading out along it: mantle up onto its top (see TryMantleFromUnder).
		if ( !surface && n.z < -0.5f && TryMantleFromUnder( pos, n, along ) )
			return;

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
