using System;
using Mimiclay.Sim;
using Sandbox.Movement;

namespace Mimiclay;

// Hunters in the sim, phase H3 (HUNTER-SIM-PLAN.md): the hunter CLIMB in the sim walk — a mode of the hunter driver, as the
// prop's climb is of the prop walk (HiderController.SimClimb). HunterClimb (the engine MoveMode) ported to run from
// (input, SimCtrl) only, its tuning still read off that component (ClimbSpeed, GrabReach, ...). The rules are the same:
//  • walls only, never ceilings or anything that moves (a loose prop, another hunter, a disguised player);
//  • grabbing is deliberately hard (the walk must never get stickier): Space HELD, or pressed within GrabBuffer, steering into
//    the wall, airborne MinAirTime, not falling fast, and not about to clear the wall's top — Space on the ground is always
//    a plain jump;
//  • Space up, Ctrl down, A/D sideways, W/S along the wall the way you look (ClimbRules, lookAlong), W into it mounts the top;
//    Shift, S pulling away, standable ground underfoot or losing the surface let go;
//  • inner corners turn onto the next wall (bumps are stepped over), outer corners wrap round the wall's end with the travel
//    carried while the same keys stay held, a shallow overhang is stepped out under, the top is mantled (a carry, no
//    teleport), and Space held on from a mantle grabs a wall behind the lip.
//
// ORDER: HunterClimb read the surface AFTER each physics step and moved on the next. Here the whole climb runs BEFORE the
// step, the reading first: nothing moves the body between one tick's step and the next's drive, so reading it then gives
// the same answers, and no reading has to be carried between ticks (only the gap to a far wall — SimCtrl.ClimbGap).
//
// State in SimCtrl (the prop climb's fields, meaning the same here): Climbing, ClimbN (the smoothed normal), ClimbP (the last
// contact; the mantle's start while mantling), ClimbGap, LostTicks (without a surface), GateTicks (an off-normal reading
// held), CornerCool, StepOverTicks, CornerLeft + CornerMoveX/Y/CornerVertical, CarryDir/CarryFrom + CarryMoveX/Y,
// SpaceBuffer, LetGoCool, MantleTicks/MantleDir/MantleZ, ClimbOn (Space held on from a mantle; MantleDir its way);
// StillTicks = ticks pulling away while climbing (ticks airborne while walking — the grab's MinAirTime); PushLevel = ticks
// held back climbing up (StepOutUnder); ClimbYaw/ClimbVertical/Wish = the last keys, for a remote copy's guess.
public sealed partial class HunterController
{
	HunterClimb _climbCfg;
	HunterClimb ClimbCfg => _climbCfg.IsValid() ? _climbCfg : (_climbCfg = Components.Get<HunterClimb>());

	/// <summary>Does this hunter's SIM body cling to a wall right now (any machine — the owner's prediction, the host, a copy)?</summary>
	public bool SimClimbing => SimSelf is { } s && s.Ctrl.Climbing;

	/// <summary>Clinging to a wall, whichever walk this hunter is on (the sim's, or the engine's HunterClimb — its synced flag).</summary>
	public bool Clinging => SimWalking ? SimClimbing : ClimbCfg.IsValid() && ClimbCfg.ClingingNet;

	/// <summary>The surface normal of the sim climb (SimLab).</summary>
	public Vector3 SimClimbNormal => SimSelf is { } s ? s.Ctrl.ClimbN : Vector3.Zero;

	static byte ClimbTicks( float seconds ) => (byte)Math.Clamp( (int)MathF.Round( seconds * SimWorld.T ), 1, 255 );

	const float ClimbBackOff = 2f, ClimbReach = 6f, ClimbNormalGate = 0.82f, ClimbSenseInset = 2f;
	const float ClimbWrapReach = 18f, ClimbSetBackReach = 24f, ClimbOnReach = 64f;
	const float ClimbRestGap = 2f;
	const float ClimbGateHold = 0.15f, ClimbCornerCooldown = 0.2f, ClimbStepOverTime = 0.12f, ClimbLetGoCooldown = 0.3f, ClimbMantleTime = 0.4f;

	float ClimbSpeedNow => ClimbCfg.IsValid() ? ClimbCfg.ClimbSpeed : 150f;

	static bool ClimbCeiling( Vector3 n ) => n.z < -0.5f;

	// Something a hunter may cling to: not a body that moves (loose props, other hunters, players' props — the sim's dynamic
	// bodies) and never part of a pawn (a disguised player however it's simulated, another hunter's head).
	static bool SimClimbable( SimWorld sim, in PhysicsTraceResult tr )
	{
		if ( !tr.Hit || tr.Body is not { } body || !body.IsValid() || body.BodyType == PhysicsBodyType.Dynamic )
			return false;
		var go = sim.SourceOf( body );
		if ( ClimbRules.IsHunter( go ) )
			return false;
		for ( var g = go; g.IsValid(); g = g.Parent )
			if ( g.Components.Get<HiderController>().IsValid() )
				return false;
		return true;
	}

	// The hunter's whole sim body swept from `from` to `to` (translation only — it never rotates).
	PhysicsTraceResult ClimbSweep( PhysicsTraceBuilder trace, PhysicsBody pb, Vector3 from, Vector3 to )
		=> trace.Sweep( pb, new Transform( from, Rotation.Identity ), new Transform( to, Rotation.Identity ) ).Run();

	// The wall-sensing sweep (HunterClimb.SenseSweep): the body's two shapes — the feet box and the capsule — each trimmed
	// SenseInset off its top and bottom (touching an overhang above or the floor below read as "sunk into the wall"); the
	// nearer hit wins.
	PhysicsTraceResult ClimbSense( PhysicsTraceBuilder trace, bool ducked, Vector3 from, Vector3 to )
	{
		float h = SimHeight( ducked ), half = h * 0.5f, r = SimRadius;
		float capTop = h - r, capBottom = MathF.Max( capTop - (half - r), r + 1f );
		bool capsule = capBottom < capTop;
		float feetTop = capsule ? half : h;
		var box = new BBox( new Vector3( -r, -r, MathF.Max( FeetClearance, ClimbSenseInset ) ), new Vector3( r, r, feetTop ) );
		var tr = trace.Box( box, from, to ).Run();
		if ( !capsule || tr.StartedSolid )
			return tr;
		float top = MathF.Max( capTop - ClimbSenseInset, capBottom );
		var cap = trace.Capsule( new Capsule( Vector3.Up * capBottom, Vector3.Up * top, r ), from, to ).Run();
		if ( cap.StartedSolid || !tr.Hit )
			return cap;
		return cap.Hit && cap.Distance < tr.Distance ? cap : tr;
	}

	// Every tick, any mode (SimHunterStep, first thing): the Space press buffer, the cooldowns, Space held on from a mantle.
	void SimClimbClock( ref SimCtrl c, in SimInput input )
	{
		if ( input.Has( SimInput.Jump ) )
			c.SpaceBuffer = ClimbTicks( ClimbCfg.IsValid() ? ClimbCfg.GrabBuffer : 0.15f );
		else if ( c.SpaceBuffer > 0 )
			c.SpaceBuffer--;
		if ( c.LetGoCool > 0 ) c.LetGoCool--;
		if ( c.CornerCool > 0 ) c.CornerCool--;
		if ( c.StepOverTicks > 0 && c.Climbing ) c.StepOverTicks--;
		if ( c.ClimbOn && !input.Has( SimInput.JumpHeld ) )
			c.ClimbOn = false;
	}

	// The engine's OnModeBegin: stop dead INTO the surface, keep what ran along it (catching a wall at speed doesn't stop dead).
	void SimHunterStartClimb( ref SimCtrl c, PhysicsBody pb, Vector3 normal, Vector3 point, float gap = 0f )
	{
		c.Climbing = true;
		c.ClimbN = normal.Normal;
		c.ClimbP = point;
		c.ClimbGap = MathF.Max( gap, 0f );
		c.LostTicks = c.StepOverTicks = c.GateTicks = c.CornerCool = 0;
		c.CornerLeft = c.CarryDir = c.CarryFrom = c.CeilingOut = Vector3.Zero;
		c.MantleTicks = 0;
		c.ClimbOn = false;
		c.Grounded = false;
		c.StillTicks = 0;
		c.PushLevel = 0;
		var v = pb.Velocity;
		pb.Velocity = (v - c.ClimbN * Vector3.Dot( v, c.ClimbN )).ClampLength( ClimbSpeedNow );
	}

	// The engine's EndClimb + OnModeEnd: no re-grab for the cooldown unless Space is pressed afresh; leave at no more than the
	// run speed, pushing off the wall if we let go by pulling away.
	void SimHunterEndClimb( ref SimCtrl c, PhysicsBody pb, bool pushOff = false )
	{
		var n = c.ClimbN;
		c.Climbing = false;
		c.MantleTicks = 0;
		c.SpaceBuffer = 0;
		c.LetGoCool = ClimbTicks( ClimbLetGoCooldown );
		c.CornerLeft = c.CarryDir = c.CarryFrom = Vector3.Zero;
		c.StillTicks = 255; // (long since the ground: a fresh press may grab again at once)
		c.PushLevel = 0;
		c.ClimbGap = 0f;
		var v = pb.Velocity.ClampLength( _controller.RunSpeed );
		if ( pushOff )
			v += n.WithZ( 0f ) * (ClimbCfg.IsValid() ? ClimbCfg.PushOffSpeed : 100f);
		pb.Velocity = v;
	}

	// ── Grabbing (from the walk) ────────────────────────────────────────────────────────────────────────────

	// In the air, Space held (or just pressed) steering into a wall: grab it (HunterClimb.TryGrab).
	bool SimHunterTryGrab( SimWorld sim, PhysicsTraceBuilder trace, PhysicsBody pb, ref SimCtrl c, in SimInput input, Vector3 pos, Vector3 v, Vector3 wish )
	{
		var cfg = ClimbCfg;
		if ( !cfg.IsValid() || !cfg.AllowClimbing || c.Grounded || c.StillTicks < ClimbTicks( cfg.MinAirTime ) || v.z < -cfg.MaxGrabFallSpeed )
			return false;
		bool intent = input.Has( SimInput.JumpHeld ) || c.SpaceBuffer > 0;
		if ( c.LetGoCool > 0 )
			intent = c.SpaceBuffer > 0; // just let go: only a press made since (the let-go cleared the buffer)
		if ( !intent )
			return false;
		var steer = wish.WithZ( 0f );
		var dir = steer.IsNearZeroLength ? Rotation.FromYaw( input.ViewYaw ).Forward : steer.Normal;
		var tr = ClimbSweep( trace, pb, pos - dir * ClimbBackOff, pos + dir * cfg.GrabReach );
		if ( !tr.Hit || tr.StartedSolid || Standable( tr.Normal ) || ClimbCeiling( tr.Normal ) || !SimClimbable( sim, tr )
			|| Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) <= 0.5f || SimWillClear( trace, pb, pos, dir, v, sim ) )
			return false;
		SimHunterStartClimb( ref c, pb, tr.Normal, tr.HitPosition );
		return true;
	}

	// Jumping onto a ledge, not up a wall: at the top of this jump the feet clear the wall's top.
	bool SimWillClear( PhysicsTraceBuilder trace, PhysicsBody pb, Vector3 pos, Vector3 dir, Vector3 v, SimWorld sim )
	{
		float g = MathF.Max( SimGravity( sim ), 1f );
		float rise = v.z > 0f ? v.z * v.z / (2f * g) : 0f;
		var from = pos + Vector3.Up * (rise + 1f);
		return !ClimbSweep( trace, pb, from, from + dir * (ClimbCfg.GrabReach + ClimbBackOff) ).Hit;
	}

	// On a ledge after a mantle, Space still held: the wall ahead (steering, else the way the mantle went) rising past a step.
	bool SimHunterTryClimbOn( SimWorld sim, PhysicsTraceBuilder trace, PhysicsBody pb, ref SimCtrl c, Vector3 pos, Vector3 wish )
	{
		var steer = wish.WithZ( 0f );
		var dir = steer.IsNearZeroLength ? new Vector3( c.MantleDir.x, c.MantleDir.y, 0f ) : steer.Normal;
		if ( dir.IsNearZeroLength )
			return false;
		var from = pos + Vector3.Up * StepUp;
		var tr = ClimbSweep( trace, pb, from - dir * ClimbBackOff, from + dir * ClimbOnReach );
		if ( !tr.Hit || tr.StartedSolid || Standable( tr.Normal ) || ClimbCeiling( tr.Normal ) || !SimClimbable( sim, tr )
			|| Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) < 0.5f )
			return false;
		SimHunterStartClimb( ref c, pb, tr.Normal, tr.HitPosition, gap: tr.Distance - ClimbBackOff );
		return true;
	}

	// SimLab: the wall the hunter faces from where it stands, grabbed without a jump (HunterClimb.LabGrab) — sent as an input
	// button so the host and every replay grab on the same tick.
	bool SimHunterLabGrab( SimWorld sim, PhysicsTraceBuilder trace, PhysicsBody pb, ref SimCtrl c, in SimInput input, Vector3 pos )
	{
		var dir = Rotation.FromYaw( input.ViewYaw ).Forward;
		var from = pos + Vector3.Up * 4f;
		var tr = ClimbSweep( trace, pb, from - dir * ClimbBackOff, from + dir * 64f );
		if ( !tr.Hit || tr.StartedSolid || Standable( tr.Normal ) || ClimbCeiling( tr.Normal ) || !SimClimbable( sim, tr ) )
			return false;
		SimHunterStartClimb( ref c, pb, tr.Normal, tr.HitPosition, gap: MathF.Max( tr.Distance - ClimbBackOff, 0f ) );
		return true;
	}

	// ── Climbing (a mode of the walk) ───────────────────────────────────────────────────────────────────────

	// One tick on the wall. False = let go this tick (the walk takes the tick from here).
	bool SimHunterClimbStep( SimWorld sim, SimBody body, PhysicsTraceBuilder trace, ref SimCtrl c, in SimInput input )
	{
		var cfg = ClimbCfg;
		var pb = body.Phys;
		var pos = pb.Position;
		const float dt = SimWorld.Dt;
		float climbSpeed = ClimbSpeedNow;

		// This tick's keys (HunterClimb.UpdateMove).
		var move = new Vector3( input.Move.x, input.Move.y, 0f ).ClampLength( 1f );
		float yaw = input.ViewYaw;
		float vertical = (input.Has( SimInput.JumpHeld ) ? 1f : 0f) - (input.Has( SimInput.Duck ) ? 1f : 0f);
		c.Wish = new Vector2( MathF.Round( move.x * 127f ), MathF.Round( move.y * 127f ) ); // (a remote copy repeats these — Guess)
		c.ClimbYaw = yaw;
		c.ClimbVertical = (sbyte)vertical;

		if ( c.MantleTicks > 0 )
			return SimHunterMantle( body, ref c, pos );

		if ( !cfg.IsValid() || !cfg.AllowClimbing || input.Has( SimInput.LetGo ) )
		{
			SimHunterEndClimb( ref c, pb );
			return false;
		}

		var n = c.ClimbN;
		var wasd = ClimbRules.Along( yaw, move, n, climbSpeed, lookAlong: true );
		bool carrying = false;
		if ( c.CarryDir != Vector3.Zero )
		{
			var carryMove = new Vector3( c.CarryMoveX / 127f, c.CarryMoveY / 127f, 0f );
			if ( !move.IsNearZeroLength && !carryMove.IsNearZeroLength && Vector3.Dot( move.Normal, carryMove.Normal ) > 0.95f && Vector3.Dot( n, c.CarryFrom ) <= 0.97f )
			{
				var flat = c.CarryDir - n * Vector3.Dot( c.CarryDir, n );
				if ( !flat.IsNearZeroLength )
				{
					wasd = flat.Normal * climbSpeed * MathF.Min( move.Length, 1f );
					carrying = true;
				}
			}
			else
				c.CarryDir = c.CarryFrom = Vector3.Zero; // new keys (or back on a face like the one we left): the look has W/S again
		}
		var climbUp = ClimbRules.Vertical( vertical, n, Vector3.Zero, climbSpeed );
		if ( c.CornerLeft != Vector3.Zero )
		{
			var cornerMove = new Vector3( c.CornerMoveX / 127f, c.CornerMoveY / 127f, 0f );
			if ( vertical != c.CornerVertical || (move - cornerMove).Length > 0.05f )
				c.CornerLeft = Vector3.Zero; // new keys: a fresh press back toward it is meant
			else
			{
				wasd = ClimbRules.NotBack( wasd, c.CornerLeft );
				climbUp = ClimbRules.NotBack( climbUp, c.CornerLeft );
			}
		}
		float into = ClimbRules.Into( yaw, move, n, lookAlong: true );
		var flatN = n.WithZ( 0f );
		float away = flatN.IsNearZeroLength || carrying ? 0f : move.x * Vector3.Dot( Rotation.FromYaw( yaw ).Forward, flatN.Normal );
		var along = wasd + climbUp;
		bool anyInput = !move.IsNearZeroLength || vertical != 0f;

		// W/S backing straight off the wall for PullAwayTime: let go and push off.
		if ( away > 0.5f && MathF.Abs( n.z ) < 0.7f )
		{
			c.StillTicks = (byte)Math.Min( c.StillTicks + 1, 255 );
			if ( c.StillTicks >= ClimbTicks( cfg.PullAwayTime ) )
			{
				SimHunterEndClimb( ref c, pb, pushOff: true );
				return false;
			}
		}
		else
			c.StillTicks = 0;

		// Feet on ground we can stand on (climbed down to the floor, or onto a slope): the walk takes it from here.
		var ground = trace.Box( SimBox( c.Ducked, 0.9f, 0.5f ), pos + Vector3.Up * 2f, pos - Vector3.Up * 2f ).Run();
		if ( ground.Hit && !ground.StartedSolid && Standable( ground.Normal ) && vertical <= 0f && along.z <= 1f )
		{
			SimHunterEndClimb( ref c, pb );
			return false;
		}

		// Read the surface (SenseSurface): from a pose backed off it, sweep the body back toward it. Started inside it
		// (shoved in deeper than the back-off)? Back off further — that's "embedded", push out.
		PhysicsTraceResult tr = default;
		float back = ClimbBackOff;
		bool embedded = false;
		for ( ; back <= ClimbBackOff * 8f; back *= 2f )
		{
			tr = ClimbSense( trace, c.Ducked, pos + n * back, pos - n * (ClimbReach + MathF.Max( c.ClimbGap, 0f )) );
			if ( !tr.StartedSolid )
				break;
			embedded = true;
		}
		if ( tr.StartedSolid )
			tr = default;
		// The wall ran out — a narrow ledge with the wall above set back? Look further in for one facing the same way.
		if ( !tr.Hit && !embedded )
		{
			var setBack = ClimbSense( trace, c.Ducked, pos + n * ClimbBackOff, pos - n * (ClimbReach + ClimbSetBackReach) );
			if ( setBack.Hit && !setBack.StartedSolid && Vector3.Dot( setBack.Normal, n ) > ClimbNormalGate )
			{
				tr = setBack;
				back = ClimbBackOff;
			}
		}

		bool surface;
		float gap;
		// Still nothing, going sideways: round an outer corner onto the face beyond the wall's end.
		if ( !tr.Hit && !embedded && c.CornerCool == 0 && MathF.Abs( n.z ) < 0.7f && SimWrapOuterCorner( sim, trace, c.Ducked, pos, n, along, out var face ) )
		{
			var turnOver = Rotation.FromToRotation( n, face.Normal );
			var flatAlong = (along - n * Vector3.Dot( along, n )).WithZ( 0f );
			if ( !move.IsNearZeroLength && !flatAlong.IsNearZeroLength )
			{
				c.CarryDir = turnOver * flatAlong.Normal;
				c.CarryMoveX = (sbyte)MathF.Round( move.x * 127f );
				c.CarryMoveY = (sbyte)MathF.Round( move.y * 127f );
				c.CarryFrom = n;
			}
			if ( DebugSimTrace )
				SimClimbLog.Add( $"{(sim.IsAuthority ? "H" : "C")} t{sim.Tick} WRAP {n:0.00} -> {face.Normal:0.00} move {move} flatAlong {flatAlong:0} carry {c.CarryDir:0.0}" );
			c.CornerLeft = Vector3.Zero;
			c.ClimbN = n = face.Normal;
			c.ClimbP = face.HitPosition;
			c.ClimbGap = 0f;
			c.LostTicks = 0;
			c.CornerCool = ClimbTicks( ClimbCornerCooldown );
			surface = false; // swung round by the edge pull until the new face is under us
			gap = 0f;
		}
		else
		{
			bool hitOk = tr.Hit && SimClimbable( sim, tr );
			surface = hitOk || embedded;
			if ( hitOk )
			{
				gap = tr.Distance - back;
				bool close = Vector3.Dot( tr.Normal, n ) > ClimbNormalGate;
				c.GateTicks = close || anyInput ? (byte)0 : (byte)Math.Min( c.GateTicks + 1, 255 );
				if ( close )
					n = Vector3.Lerp( n, tr.Normal, 0.5f ).Normal;
				else if ( Vector3.Dot( tr.Normal, n ) > 0.3f && c.GateTicks > ClimbTicks( ClimbGateHold ) )
				{
					n = tr.Normal;
					c.GateTicks = 0;
				}
				c.ClimbN = n;
				c.ClimbP = tr.HitPosition;
				c.LostTicks = 0;
			}
			else
			{
				gap = 0f;
				c.LostTicks = (byte)Math.Min( c.LostTicks + 1, 255 );
				if ( c.LostTicks > ClimbTicks( cfg.LoseTime ) )
				{
					SimHunterEndClimb( ref c, pb );
					return false;
				}
			}
			if ( Standable( n ) || ClimbCeiling( n ) )
			{
				SimHunterEndClimb( ref c, pb ); // the surface became ground, or (round an edge) an underside
				return false;
			}
		}
		c.ClimbGap = gap;

		// Inner corners: a different surface ahead takes over (ground ends the climb), or it's a bump we step over.
		if ( !SimClimbCorners( sim, trace, pb, ref c, pos, along, vertical, move, dt ) )
			return false;
		n = c.ClimbN;

		// Climbing up but held back by a SHALLOW overhang: step out over it (StepOutUnder).
		if ( along.z >= 1f && c.StepOverTicks == 0 && pb.Velocity.z <= along.z * 0.25f )
		{
			c.PushLevel = (ushort)Math.Min( c.PushLevel + 1, 1000 );
			if ( c.PushLevel >= ClimbTicks( 0.06f ) )
			{
				c.PushLevel = 0;
				var lifted = pos + n.WithZ( 0f ).Normal * StepUp;
				var upTr = ClimbSweep( trace, pb, lifted, lifted + Vector3.Up * 6f );
				if ( !upTr.Hit && !upTr.StartedSolid )
					c.StepOverTicks = ClimbTicks( ClimbStepOverTime );
			}
		}
		else
			c.PushLevel = 0;

		// Climbing up (or pushing into the wall) with the top within a step of our feet: mantle onto it.
		if ( (along.z > 1f || into > 0.3f) && MathF.Abs( n.z ) < 0.7f && SimTryMantle( trace, pb, ref c, pos ) )
			return SimHunterMantle( body, ref c, pos );

		// The motion (HunterClimb.AddVelocity): close any gap to the surface (never pressing into it — resting exactly in
		// contact holds still without drift or jitter), back out if sunk in, lift off while stepping over a bump; off an outer
		// edge, pull toward the last contact (never into the old face faster than the ordinary stick).
		float stick = cfg.StickSpeed;
		Vector3 v;
		if ( embedded )
			v = along + n * stick;
		else if ( c.StepOverTicks > 0 )
			v = along + n * climbSpeed * 0.5f;
		else if ( surface )
		{
			// Resting ClimbRestGap OFF the surface (the engine climb rests exactly in contact): within half a unit, the body caught
			// the edge of the face it had just wrapped round from as it crossed that face's plane and stopped dead — the contacts
			// took the whole travel along the new face, tick after tick, on the host and every copy alike, though a query sweep
			// called the way clear (the engine's body has ContactRecycling off, which game code can't set). 2u clears it: 7 faces
			// round the hcorner pillar in 6 of 6 runs, against 2–4 at 0 and 0.5u (SimLab, 2026-11-08).
			float off = gap - ClimbRestGap;
			v = along - n * (off >= 0f ? Math.Clamp( off / dt, 0f, off > 2f ? MathF.Max( stick, climbSpeed ) : stick ) : -MathF.Min( -off / dt, stick ));
		}
		else
		{
			var pull = (c.ClimbP - (pos + Vector3.Up * SimHeight( c.Ducked ) * 0.5f)).Normal * cfg.EdgePullSpeed;
			float inward = Vector3.Dot( pull, -n );
			if ( inward > stick )
				pull -= -n * (inward - stick);
			// Just round an outer corner: never back toward the face we left (pulled back to its plane, the body caught on its edge —
			// see ClimbRestGap).
			if ( c.CarryFrom != Vector3.Zero )
			{
				float toOld = Vector3.Dot( pull, -c.CarryFrom );
				if ( toOld > 0f )
					pull += c.CarryFrom * toOld;
			}
			v = along * 0.5f + pull;
		}
		c.Commanded = v; // (what the climb asked for; the contacts may take some off)
		if ( DebugSimTrace && SimClimbLog.Count < 4000 )
			SimClimbLog.Add( $"{(sim.IsAuthority ? "H" : "C")} t{sim.Tick} pos {pos:0.00} n {n:0.00} move {input.Move} yaw {input.ViewYaw:0} carry {c.CarryDir:0.0} cm {c.CarryMoveX},{c.CarryMoveY} left {c.CornerLeft:0.0} surf {surface} gap {gap:0.00} along {along:0} v {v:0} prevV {pb.Velocity:0.0}" );
		pb.Velocity = v;
		pb.AngularVelocity = 0f;
		body.Intent = Vector3.Zero;
		return true;
	}

	// Going sideways along a wall that has ended: the face beyond its end that turns away (TryWrapOuterCorner).
	bool SimWrapOuterCorner( SimWorld sim, PhysicsTraceBuilder trace, bool ducked, Vector3 pos, Vector3 n, Vector3 along, out PhysicsTraceResult face )
	{
		face = default;
		var d = (along - n * Vector3.Dot( along, n )).WithZ( 0f );
		if ( d.Length < 5f )
			return false;
		d = d.Normal;
		float depth = SimRadius + 2f;
		var probe = pos + d * 2f - n * depth;
		var tr = ClimbSense( trace, ducked, probe, probe - d * (2f + ClimbWrapReach) );
		if ( !tr.Hit || tr.StartedSolid || !SimClimbable( sim, tr ) || Standable( tr.Normal ) || MathF.Abs( tr.Normal.z ) > 0.7f )
			return false;
		if ( Vector3.Dot( tr.Normal, d ) < 0.5f || Vector3.Dot( tr.Normal, n ) > 0.7f )
			return false;
		face = tr;
		return true;
	}

	// Inner corners (HunterClimb.Corners). False = let go (climbed down onto ground).
	bool SimClimbCorners( SimWorld sim, PhysicsTraceBuilder trace, PhysicsBody pb, ref SimCtrl c, Vector3 pos, Vector3 along, float vertical, Vector3 move, float dt )
	{
		if ( along.Length < 5f || c.CornerCool > 0 )
			return true;
		var n = c.ClimbN;
		var dir = along.Normal;
		var from = pos + n * ClimbBackOff - dir * ClimbBackOff;
		float reach = ClimbBackOff + along.Length * dt + 3f;
		var ahead = ClimbSweep( trace, pb, from, from + dir * reach );
		if ( !ahead.Hit || ahead.StartedSolid || Vector3.Dot( ahead.Normal, n ) >= 0.7f )
			return true;
		if ( Standable( ahead.Normal ) )
		{
			if ( dir.z < 0f )
			{
				SimHunterEndClimb( ref c, pb );
				return false;
			}
			return true;
		}
		// A bump (anything we could step over) — or something we can't cling to: lift over it rather than turn onto it.
		var lifted = from + n * StepUp;
		var over = ClimbSweep( trace, pb, lifted, lifted + dir * reach );
		if ( (!over.Hit && !over.StartedSolid) || !SimClimbable( sim, ahead ) )
		{
			c.StepOverTicks = ClimbTicks( ClimbStepOverTime );
			return true;
		}
		if ( ClimbCeiling( ahead.Normal ) )
			return true; // up into an overhang: no turning onto it — the body just comes to rest against it
		c.CornerLeft = n;
		c.CornerMoveX = (sbyte)MathF.Round( move.x * 127f );
		c.CornerMoveY = (sbyte)MathF.Round( move.y * 127f );
		c.CornerVertical = (sbyte)vertical;
		c.ClimbN = ahead.Normal;
		c.ClimbP = ahead.HitPosition;
		c.LostTicks = 0;
		c.CornerCool = ClimbTicks( ClimbCornerCooldown );
		return true;
	}

	// At the top of a wall (HunterClimb.TryMantle): from a step up, a little way in, is the wall gone with ground under it
	// no higher than a step above our feet? Then carry over the lip — rise to the top's height, then in over it.
	bool SimTryMantle( PhysicsTraceBuilder trace, PhysicsBody pb, ref SimCtrl c, Vector3 pos )
	{
		var into = (-c.ClimbN).WithZ( 0f );
		if ( into.IsNearZeroLength )
			return false;
		into = into.Normal;
		float lift = StepUp, reach = ClimbCfg.IsValid() ? ClimbCfg.MantleReach : 8f;
		var from = pos + Vector3.Up * lift;
		var fwd = ClimbSweep( trace, pb, from, from + into * reach );
		if ( fwd.StartedSolid || (fwd.Hit && !Standable( fwd.Normal )) )
			return false;
		var over = fwd.Hit ? fwd.EndPosition : from + into * reach;
		var down = ClimbSweep( trace, pb, over, over - Vector3.Up * (lift + reach) );
		float rise = down.EndPosition.z - pos.z;
		if ( down.StartedSolid || !down.Hit || !Standable( down.Normal ) || rise > lift || rise < 1f )
			return false;
		c.MantleTicks = ClimbTicks( ClimbMantleTime );
		c.MantleDir = new Vector2( into.x, into.y );
		c.MantleZ = down.EndPosition.z + 1f;
		c.ClimbP = pos; // (the mantle's start: how far over the lip we've come)
		return true;
	}

	// The mantle carry: up to the top's height, then in over it; done once the whole body is over the lip (or after
	// MantleTime). False = done (the walk takes it from here; Space still held carries on up a wall behind — ClimbOn).
	bool SimHunterMantle( SimBody body, ref SimCtrl c, Vector3 pos )
	{
		var pb = body.Phys;
		var dir = new Vector3( c.MantleDir.x, c.MantleDir.y, 0f );
		float travelled = Vector3.Dot( pos - c.ClimbP, dir );
		c.MantleTicks--;
		if ( (pos.z >= c.MantleZ - 1f && travelled >= _controller.BodyRadius) || c.MantleTicks == 0 )
		{
			bool spaceHeld = c.ClimbVertical > 0;
			SimHunterEndClimb( ref c, pb );
			c.ClimbOn = spaceHeld;
			c.MantleDir = new Vector2( dir.x, dir.y ); // (the way the mantle went: ClimbOn's fallback direction)
			return false;
		}
		const float dt = SimWorld.Dt;
		float climbSpeed = ClimbSpeedNow;
		float rise = Math.Clamp( (c.MantleZ - pos.z) / dt, 0f, MathF.Max( climbSpeed, 300f ) );
		float forward = pos.z >= c.MantleZ - 2f ? MathF.Max( climbSpeed, _controller.WalkSpeed ) : 0f;
		pb.Velocity = Vector3.Up * rise + dir * forward;
		pb.AngularVelocity = 0f;
		body.Intent = Vector3.Zero;
		return true;
	}
}
