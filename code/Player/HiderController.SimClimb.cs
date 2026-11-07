using System;
using Mimiclay.Sim;

namespace Mimiclay;

// Climbing in the SIM walk (HiderController.Sim): a mode of the walk driver, not a driver of its own — SimStep hands the tick
// to SimStepClimb while SimCtrl.Climbing. The old scene walk's climb (HiderController.Climb) ported to run from (input,
// body.Ctrl) only: every number it keeps between ticks lives in SimCtrl, so the host, the owner's prediction and every
// replay step the same climb. Times are ticks here (SimWorld.T a second). The controls are the old climb's: Space held
// (or just pressed) jumping into a wall or ceiling grabs it; Space up the surface, Ctrl down, A/D sideways and W/S in
// depth by the camera (ClimbRules), the mouse turns, Shift lets go.
//
// NEW (user, 2026-11-07: "we weren't able to climb around a corner — outer corner, inner corners were fine"): going sideways
// along a wall past its end, the climber WRAPS round onto the next face (SimWrapOuterCorner). The old climb's outer edge
// pull aimed at the last contact on the face it was leaving, so it hung at the edge and let go after ClimbLoseTime.
//
// Not ported (v1): the possession cling, looking through small clay props to the wall behind, the buried hand-back (the
// solver pushes it out), the leash's pull-in.
public sealed partial class HiderController
{
	/// <summary>Does this pawn's SIM body climb right now (any machine — the owner's prediction, the host, a copy)?</summary>
	internal bool SimClimbing => SimSelf is { } s && s.Ctrl.Climbing;

	static byte Ticks( float seconds ) => (byte)Math.Clamp( (int)MathF.Round( seconds * SimWorld.T ), 1, 255 );

	float SimMantleSpeed => MathF.Max( ScaledClimbSpeed, WalkSpeed * SizeSpeedScale * 0.5f );

	PhysicsTraceResult SimSweep( PhysicsTraceBuilder trace, Vector3 from, Rotation rot, Vector3 to )
		=> _simHull.Sweep( trace, new Transform( from, rot ), new Transform( to, rot ) );

	bool SimFloorProp( SimWorld sim, in PhysicsTraceResult tr ) => IsFloorProp( tr.Hit, sim.SourceOf( tr.Body ), tr.Body );

	bool SimWalkableHit( SimWorld sim, in PhysicsTraceResult tr )
		=> Walkable( tr.Normal ) || (tr.Normal.z > PropFloorMinZ && SimFloorProp( sim, tr ));

	// Every tick, any state (SimStep, first thing): the press buffer, the let-go cooldown, the mantle carry, Space held on from it.
	static void SimClimbClock( ref SimCtrl c, in SimInput input, byte grabBufferTicks )
	{
		if ( input.Has( SimInput.Jump ) )
			c.SpaceBuffer = grabBufferTicks;
		else if ( c.SpaceBuffer > 0 )
			c.SpaceBuffer--;
		if ( c.LetGoCool > 0 ) c.LetGoCool--;
		if ( c.MantleTicks > 0 ) c.MantleTicks--;
		if ( c.CornerCool > 0 ) c.CornerCool--;
		if ( c.ClimbOn && !input.Has( SimInput.JumpHeld ) )
			c.ClimbOn = false;
	}

	static void SimStartClimb( ref SimCtrl c, Vector3 normal, Vector3 point )
	{
		c.Climbing = true;
		c.ClimbN = normal.Normal;
		c.ClimbP = point;
		c.LostTicks = c.StepOverTicks = c.GateTicks = c.CornerCool = 0;
		c.CornerLeft = c.CeilingOut = c.CarryDir = c.CarryFrom = Vector3.Zero;
		c.Grounded = false;
		c.Parked = false;
		c.StillTicks = 0;
		c.MantleTicks = 0;
		c.ClimbOn = false;
	}

	// Letting go must not re-grab on the next tick (see EndClimb): the jump's intent is cleared, and for the cooldown only a
	// FRESH Space press grabs again.
	void SimEndClimb( ref SimCtrl c )
	{
		c.Climbing = false;
		c.AirFromJump = false;
		c.SpaceBuffer = 0;
		c.LetGoCool = Ticks( LetGoCooldown );
		c.Parked = false;
		c.StillTicks = 0;
		c.CornerLeft = c.CeilingOut = c.CarryDir = c.CarryFrom = Vector3.Zero;
	}

	void SimStartMantleCarry( ref SimCtrl c, in SimInput input, PhysicsBody pb, Vector3 dir, float topZ, float seconds )
	{
		c.MantleTicks = Ticks( seconds );
		c.MantleDir = new Vector2( dir.x, dir.y );
		c.MantleZ = topZ;
		c.ClimbOn = input.Has( SimInput.JumpHeld );
		pb.Velocity = dir * SimMantleSpeed;
	}

	// In the air (SimStep): grab a wall or ceiling within reach if the player means to — TryAirGrab's rules.
	bool SimTryAirGrab( SimWorld sim, PhysicsTraceBuilder trace, ref SimCtrl c, in SimInput input, Vector3 pos, Rotation rot, Vector3 wish, Vector3 v )
	{
		if ( !AllowClimbing )
			return false;
		bool spaceIntent = input.Has( SimInput.JumpHeld ) || c.SpaceBuffer > 0;
		if ( c.LetGoCool > 0 )
			spaceIntent = c.SpaceBuffer > 0; // just let go: only a press made since (SimEndClimb cleared the buffer)
		if ( !spaceIntent && !c.AirFromJump )
			return false;

		var steer = wish.WithZ( 0f );
		if ( steer.IsNearZeroLength && spaceIntent )
			steer = Rotation.FromYaw( input.ViewYaw ).Forward;
		if ( !steer.IsNearZeroLength )
		{
			var dir = steer.Normal;
			var tr = SimSweep( trace, pos, rot, pos + dir * ClimbGrabReach );
			if ( tr.Hit && !tr.StartedSolid && !Walkable( tr.Normal ) && tr.Normal.z > -0.7f && !SimFloorProp( sim, tr )
				&& Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) > 0.3f )
			{
				// Only the jump itself (Space long released): don't snatch the side of a ledge we're about to clear.
				float lip = ScaledStep( StepHeight );
				bool clearingLedge = !spaceIntent && !SimSweep( trace, pos + Vector3.Up * lip, rot, pos + Vector3.Up * lip + dir * ClimbGrabReach ).Hit;
				if ( !clearingLedge )
				{
					SimStartClimb( ref c, tr.Normal, tr.HitPosition );
					return true;
				}
			}
		}

		if ( (spaceIntent || c.AirFromJump) && v.z > -100f )
		{
			var tr = SimSweep( trace, pos, rot, pos + Vector3.Up * ClimbGrabReach );
			if ( tr.Hit && !tr.StartedSolid && tr.Normal.z <= -0.7f && !SimFloorProp( sim, tr ) )
			{
				SimStartClimb( ref c, tr.Normal, tr.HitPosition );
				return true;
			}
		}
		return false;
	}

	// Space held on from a mantle (TryGroundGrab): a wall the way we steer (or face), rising past a step, within reach.
	bool SimTryGroundGrab( SimWorld sim, PhysicsTraceBuilder trace, ref SimCtrl c, in SimInput input, Vector3 pos, Rotation rot, Vector3 wish )
	{
		if ( !AllowClimbing )
			return false;
		var steer = wish.WithZ( 0f );
		var dir = steer.IsNearZeroLength ? Rotation.FromYaw( input.ViewYaw ).Forward : steer.Normal;
		var from = pos + Vector3.Up * ScaledStep( StepHeight );
		var tr = SimSweep( trace, from, rot, from + dir * ClimbGrabReach );
		if ( !tr.Hit || tr.StartedSolid || Walkable( tr.Normal ) || MathF.Abs( tr.Normal.z ) > 0.7f || SimFloorProp( sim, tr )
			|| Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) < 0.3f )
			return false;
		SimStartClimb( ref c, tr.Normal, tr.HitPosition );
		return true;
	}

	// The top of a wall within a step of our base: hand over to the walk's guided carry over the lip (TryMantle).
	bool SimTryMantle( SimWorld sim, PhysicsTraceBuilder trace, ref SimCtrl c, in SimInput input, PhysicsBody pb, Vector3 pos, Rotation rot, Vector3 n, bool lost )
	{
		if ( !ClimbMantle || !_hull.Ready )
			return false;
		var into = (-n).WithZ( 0f );
		if ( into.IsNearZeroLength )
			return false;
		into = into.Normal;
		float lift = ScaledStep( StepHeight );
		float reach = MantleReach;

		var from = pos + Vector3.Up * lift;
		var fwd = SimSweep( trace, from, rot, from + into * reach );
		if ( fwd.StartedSolid )
			return false;
		if ( fwd.Hit && !Walkable( fwd.Normal ) && !SimFloorProp( sim, fwd ) )
		{
			if ( lost )
			{
				c.ClimbP = fwd.HitPosition; // a small ledge on the way up: step in onto the wall behind it
				c.LostTicks = 0;
			}
			return false;
		}

		var over = fwd.Hit ? fwd.EndPosition - into * Skin : from + into * reach;
		var down = SimSweep( trace, over, rot, over - Vector3.Up * (lift + reach) );
		if ( down.StartedSolid || !down.Hit || !SimWalkableHit( sim, down ) )
			return false;
		if ( down.EndPosition.z - pos.z > lift )
			return false; // still more than a step to go — keep climbing

		SimEndClimb( ref c );
		SimStartMantleCarry( ref c, input, pb, into, down.EndPosition.z + Skin, 0.4f );
		return true;
	}

	// Along a ceiling and out past its edge: mantle up onto its top (TryMantleFromUnder).
	bool SimTryMantleFromUnder( SimWorld sim, PhysicsTraceBuilder trace, ref SimCtrl c, in SimInput input, PhysicsBody pb, Vector3 pos, Rotation rot, Vector3 n, Vector3 along )
	{
		if ( !ClimbMantle || n.z > -0.5f || !_hull.Ready )
			return false;
		var d = along.WithZ( 0f );
		d = d.Length > 5f ? d.Normal : c.CeilingOut;
		if ( d.IsNearZeroLength )
			return false;
		float height = _hull.LocalBounds.Size.z;
		float reach = MantleReach;
		float maxRise = height * 1.5f + ScaledStep( StepHeight );

		var above = pos + Vector3.Up * (maxRise + 2f) - d * (reach + 2f);
		var down = SimSweep( trace, above, rot, above - Vector3.Up * (maxRise + 2f) );
		if ( down.StartedSolid || !down.Hit || !SimWalkableHit( sim, down ) )
			return false;
		float rise = down.EndPosition.z - pos.z;
		if ( rise < height * 0.5f || rise > maxRise )
			return false;

		SimEndClimb( ref c );
		SimStartMantleCarry( ref c, input, pb, -d, down.EndPosition.z + Skin, 0.8f );
		return true;
	}

	// A downward face met climbing up a wall: only a shallow lip (ShallowOverhang)? Thin rays up from under it, a step out.
	bool SimShallowOverhang( PhysicsTraceBuilder trace, in PhysicsTraceResult ahead, Vector3 n )
	{
		float step = ScaledStep( StepHeight );
		var side = Vector3.Cross( n, Vector3.Up );
		side = side.IsNearZeroLength ? Vector3.Zero : side.Normal;
		float half = _hull.Ready ? MathF.Min( _hull.LocalBounds.Size.x, _hull.LocalBounds.Size.y ) * 0.4f : 0f;
		foreach ( float s in stackalloc float[] { 0f, -half, half } )
		{
			var o = ahead.HitPosition + n * (step + 1f) + side * s - Vector3.Up;
			if ( trace.Ray( o, o + Vector3.Up * (step + 6f) ).Run().Hit )
				return false;
		}
		return true;
	}

	// How thick the hull is along `dir` (its extent, world units).
	float SimHullExtent( Rotation rot, Vector3 dir )
	{
		float lo = float.MaxValue, hi = float.MinValue;
		foreach ( var v in _simHull.Vertices )
		{
			float d = Vector3.Dot( rot * v, dir );
			lo = MathF.Min( lo, d );
			hi = MathF.Max( hi, d );
		}
		return hi > lo ? hi - lo : 0f;
	}

	/// <summary>Outer corners: how far (u) past the end of a wall the wrap looks back for the next face.</summary>
	[ConVar( "mimi_climb_wrap_reach", Help = "Climbing: how far (u) past a wall's end an outer-corner wrap looks back for the next face (0 = no wrapping)." )]
	public static float ClimbWrapReach { get; set; } = 18f;

	// OUTER CORNER (new). Going sideways along face A (normal n, travelling d) and A has ended under us: look for the face
	// that turns away round the end — B, facing roughly the way we were going. The hull is placed a little past the end and
	// half its thickness deeper than A (overlapping the depth of B, whatever B's width — a thin panel's end face too), then
	// swept BACK along -d. A face there that faces d is B: it becomes the surface, and the travel turns over the edge with
	// it (d → -n), carried while the same keys stay held, so the edge pull and the carry swing the climber round onto B.
	bool SimWrapOuterCorner( SimWorld sim, PhysicsTraceBuilder trace, Vector3 pos, Rotation rot, Vector3 n, Vector3 d, out PhysicsTraceResult face )
	{
		face = default;
		if ( ClimbWrapReach <= 0f || !_simHull.Ready )
			return false;
		float depth = SimHullExtent( rot, n ) * 0.5f + 2f;
		var probe = pos + d * 2f - n * depth;
		var tr = SimSweep( trace, probe, rot, probe - d * (2f + ClimbWrapReach) );
		if ( !tr.Hit || tr.StartedSolid || Walkable( tr.Normal ) || MathF.Abs( tr.Normal.z ) > 0.7f || SimFloorProp( sim, tr ) )
			return false;
		if ( Vector3.Dot( tr.Normal, d ) < 0.5f || Vector3.Dot( tr.Normal, n ) > 0.7f )
			return false;
		face = tr;
		return true;
	}

	[ConVar( "mimi_dbg_climb_sim", Help = "Debug: log the sim climb's grabs, corners, wraps, mantles and let-gos (host and owner, not replays)." )]
	public static bool DebugSimClimb { get; set; }

	void ClimbLog( SimBody body, string what )
	{
		if ( DebugSimClimb && SimSystem is { Replaying: false } )
			Log.Info( $"[sim climb {(SimSystem.IsAuthority ? "host" : "own")}] {GameObject.Name} t{SimSystem.Tick}: {what}" );
	}

	// One climbing tick (StepClimb, from input + Ctrl). Called by SimStep while c.Climbing.
	void SimStepClimb( SimBody body, SimWorld sim, ref SimCtrl c, in SimInput input, int tick )
	{
		var pb = body.Phys;
		const float dt = SimWorld.Dt;
		var pos = pb.Position;
		var rot = pb.Rotation;
		var trace = sim.Trace.WithoutTags( MoveIgnoreTags.Append( SimWorld.SelfTag( body ) ).ToArray() );
		c.Grounded = false;
		c.AirFromJump = false;

		if ( input.Has( SimInput.LetGo ) )
		{
			SimEndClimb( ref c );
			ClimbLog( body, "let go (Shift)" );
			body.Intent = 0f;
			return;
		}

		var move = new Vector3( input.Move.x, input.Move.y, 0f );
		float vertical = (input.Has( SimInput.JumpHeld ) ? 1f : 0f) - (input.Has( SimInput.Duck ) ? 1f : 0f);
		float turn = input.Turn;
		bool moving = !move.IsNearZeroLength || vertical != 0f;
		bool anyInput = moving || MathF.Abs( turn ) > 0.01f;
		// What a remote copy repeats (SimWalkDriver.Guess): the keys, the view, Space/Ctrl.
		c.Wish = new Vector2( MathF.Round( move.x * 127f ), MathF.Round( move.y * 127f ) );
		c.ClimbYaw = input.ViewYaw;
		c.ClimbVertical = (sbyte)vertical;
		c.Driving = moving;

		// Held still on the surface: nothing to do until there's input — or the surface has gone (checked every ParkCheckTicks).
		if ( c.Parked )
		{
			if ( !anyInput && pb.Velocity.LengthSquared < 4f )
			{
				if ( tick % ParkCheckTicks != 0 )
				{
					body.Intent = 0f;
					return;
				}
				var hold = SimSweep( trace, pos + c.ClimbN * ClimbBackOff, rot, pos - c.ClimbN * ClimbReach );
				if ( hold.Hit || hold.StartedSolid )
				{
					body.Intent = 0f;
					return;
				}
			}
			c.Parked = false;
			c.StillTicks = 0;
		}
		if ( pb.Sleeping )
			pb.Sleeping = false;

		// Sense the surface: from a pose backed off it, sweep the hull toward it — further back if that starts inside it
		// (shoved in deeper than the back-off: then push OUT this tick, never treat it as lost).
		var n = c.ClimbN;
		PhysicsTraceResult tr = default;
		bool embedded = false;
		for ( float back = ClimbBackOff; back <= ClimbBackOff * 8f; back *= 2f )
		{
			tr = SimSweep( trace, pos + n * back, rot, pos - n * ClimbReach );
			if ( !tr.StartedSolid )
				break;
			embedded = true;
		}
		if ( tr.StartedSolid )
			tr = default;
		bool surface = tr.Hit || embedded;
		if ( tr.Hit )
		{
			bool closeToCurrent = Vector3.Dot( tr.Normal, c.ClimbN ) > SurfaceNormalGate;
			if ( !closeToCurrent && SimWalkableHit( sim, tr ) && MathF.Abs( c.ClimbN.z ) < 0.7f && SimTryMantle( sim, trace, ref c, input, pb, pos, rot, c.ClimbN, lost: false ) )
			{
				ClimbLog( body, "mantle (grazed the top)" );
				return;
			}
			c.GateTicks = closeToCurrent || moving ? (byte)0 : (byte)Math.Min( c.GateTicks + 1, 255 );
			if ( closeToCurrent )
				c.ClimbN = Vector3.Lerp( c.ClimbN, tr.Normal, 0.5f ).Normal;
			else if ( Vector3.Dot( tr.Normal, c.ClimbN ) > FacingGate && c.GateTicks > Ticks( SurfaceGateHold ) )
			{
				c.ClimbN = tr.Normal;
				c.GateTicks = 0;
			}
			c.ClimbP = tr.HitPosition;
			c.LostTicks = 0;
		}
		else if ( !embedded )
		{
			if ( c.LostTicks < 255 )
				c.LostTicks++;
			if ( c.LostTicks > Ticks( ClimbLoseTime ) )
			{
				SimEndClimb( ref c );
				ClimbLog( body, "lost the surface — fall" );
				return;
			}
		}

		// Climbed (or slid) onto something to stand on: the walk takes it from here.
		if ( Walkable( c.ClimbN ) )
		{
			SimEndClimb( ref c );
			ClimbLog( body, "onto ground" );
			return;
		}
		n = c.ClimbN;

		// Along the surface: WASD by the view (ClimbRules), carried over the last corner while the same keys stay held.
		float speed = ScaledClimbSpeed;
		var wasd = ClimbRules.Along( input.ViewYaw, move, n, speed );
		float into = ClimbRules.Into( input.ViewYaw, move, n );
		if ( c.CarryDir != Vector3.Zero && Vector3.Dot( n, c.CarryFrom ) > 0.97f )
			c.CarryDir = Vector3.Zero; // back on a surface facing the one we left: the camera has W/S again
		var carryMove = new Vector3( c.CarryMoveX / 127f, c.CarryMoveY / 127f, 0f );
		if ( c.CarryDir != Vector3.Zero && !move.IsNearZeroLength && !carryMove.IsNearZeroLength && Vector3.Dot( move.Normal, carryMove.Normal ) > 0.95f )
		{
			var flat = c.CarryDir - n * Vector3.Dot( c.CarryDir, n );
			if ( !flat.IsNearZeroLength )
				wasd = flat.Normal * speed * MathF.Min( move.Length, 1f );
		}
		else
			c.CarryDir = Vector3.Zero;

		var climbUp = ClimbRules.Vertical( vertical, n, c.CeilingOut, speed );
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
		var along = wasd + climbUp;

		// Inner corner: a different surface ahead takes over (a bump is stepped over instead).
		if ( along.Length > CornerMinSpeed && c.CornerCool == 0 )
		{
			var dir = along.Normal;
			var from = pos + n * ClimbBackOff - dir * ClimbBackOff;
			float reach = ClimbBackOff + along.Length * dt + 3f;
			var ahead = SimSweep( trace, from, rot, from + dir * reach );
			bool corner = ahead.Hit && !ahead.StartedSolid && Vector3.Dot( ahead.Normal, n ) < 0.7f;
			bool bump = false;
			if ( corner )
			{
				var lifted = from + n * ScaledStep( StepHeight );
				var over = SimSweep( trace, lifted, rot, lifted + dir * reach );
				bump = !over.Hit && !over.StartedSolid;
				bump |= SimFloorProp( sim, ahead );
				bump |= MathF.Abs( n.z ) < 0.5f && dir.z > 0.5f && ahead.Normal.z < -0.3f && SimShallowOverhang( trace, ahead, n );
			}
			if ( bump )
				c.StepOverTicks = Ticks( ClimbStepOverTime );
			else if ( corner )
			{
				var turnOver = Rotation.FromToRotation( n, ahead.Normal );
				if ( !move.IsNearZeroLength && !wasd.IsNearZeroLength )
					SetCarry( ref c, turnOver * wasd.Normal, move, n );
				along = turnOver * along;
				c.CornerLeft = n;
				if ( MathF.Abs( n.z ) < 0.5f && ahead.Normal.z < -0.5f )
					c.CeilingOut = n.WithZ( 0f ).Normal; // up a wall onto a ceiling: Space now heads out along it
				else if ( ahead.Normal.z > -0.5f )
					c.CeilingOut = Vector3.Zero;
				c.CornerMoveX = (sbyte)MathF.Round( move.x * 127f );
				c.CornerMoveY = (sbyte)MathF.Round( move.y * 127f );
				c.CornerVertical = (sbyte)vertical;
				c.ClimbN = ahead.Normal;
				c.ClimbP = ahead.HitPosition;
				c.LostTicks = 0;
				c.CornerCool = Ticks( CornerSwitchCooldown );
				n = c.ClimbN;
				ClimbLog( body, $"inner corner → n {n:0.00}" );
			}
		}

		// Outer corner (new): the wall ran out while we went sideways along it — wrap round onto the face beyond its end.
		if ( !surface && c.CornerCool == 0 && MathF.Abs( n.z ) < 0.7f )
		{
			var d = (along - n * Vector3.Dot( along, n )).WithZ( 0f );
			if ( d.Length > CornerMinSpeed && SimWrapOuterCorner( sim, trace, pos, rot, n, d.Normal, out var face ) )
			{
				var turnOver = Rotation.FromToRotation( n, face.Normal );
				if ( !move.IsNearZeroLength && !wasd.IsNearZeroLength )
					SetCarry( ref c, turnOver * wasd.Normal, move, n );
				along = turnOver * along;
				c.CornerLeft = Vector3.Zero; // (round an outer corner the way on IS back past the old face's plane)
				c.CeilingOut = Vector3.Zero;
				c.ClimbN = face.Normal;
				c.ClimbP = face.HitPosition;
				c.LostTicks = 0;
				c.CornerCool = Ticks( CornerSwitchCooldown );
				n = c.ClimbN;
				ClimbLog( body, $"outer corner → n {n:0.00}" );
			}
		}

		// Hold still when there's no input and we're on the surface.
		if ( !anyInput && surface && !embedded )
		{
			c.StillTicks = (byte)Math.Min( c.StillTicks + 1, 255 );
			if ( c.StillTicks >= SimParkAfterTicks )
			{
				c.Parked = true;
				pb.Velocity = Vector3.Zero;
				pb.AngularVelocity = Vector3.Zero;
				c.Commanded = Vector3.Zero;
				body.Intent = 0f;
				return;
			}
		}
		else
			c.StillTicks = 0;

		// Climbing up (or pushing into the wall) with the top within a step: walk onto it.
		if ( (along.z > 1f || into > 0.3f) && MathF.Abs( n.z ) < 0.7f && SimTryMantle( sim, trace, ref c, input, pb, pos, rot, n, lost: !surface ) )
		{
			ClimbLog( body, "mantle" );
			return;
		}
		// Off the outer edge of a ceiling heading out along it: up onto its top.
		if ( !surface && n.z < -0.5f && SimTryMantleFromUnder( sim, trace, ref c, input, pb, pos, rot, n, along ) )
		{
			ClimbLog( body, "mantle from under" );
			return;
		}

		// On a surface: press into it. Off one (an edge): pull toward where we last touched it — never into the old surface
		// faster than the ordinary stick.
		Vector3 velocity;
		if ( embedded )
			velocity = along + n * ClimbStickSpeed * 2f;
		else if ( c.StepOverTicks > 0 )
			velocity = along + n * ClimbSpeed;
		else if ( surface )
			velocity = along - n * ClimbStickSpeed;
		else
		{
			var pull = (c.ClimbP - pb.MassCenter).Normal * ClimbEdgePullSpeed;
			float inward = Vector3.Dot( pull, -n );
			if ( inward > ClimbStickSpeed )
				pull -= -n * (inward - ClimbStickSpeed);
			velocity = along * 0.5f + pull;
		}
		if ( c.StepOverTicks > 0 )
			c.StepOverTicks--;

		pb.Velocity = velocity;
		pb.AngularVelocity = 0f;
		c.Commanded = velocity;
		body.Intent = along;
		SimTurn( pb, pos, rot, turn );
	}

	static void SetCarry( ref SimCtrl c, Vector3 dir, Vector3 move, Vector3 from )
	{
		c.CarryDir = dir;
		c.CarryMoveX = (sbyte)MathF.Round( move.x * 127f );
		c.CarryMoveY = (sbyte)MathF.Round( move.y * 127f );
		c.CarryFrom = from;
	}

	// The mouse turn: about the world vertical through the hull's centre, applied to the body directly (its yaw is locked).
	void SimTurn( PhysicsBody pb, Vector3 pos, Rotation rot, float turn )
	{
		if ( MathF.Abs( turn ) <= 0.001f )
			return;
		var centre = pos + rot * _hull.Centre;
		var centreXY = new Vector3( centre.x, centre.y, pos.z );
		var spin = Rotation.FromAxis( Vector3.Up, turn );
		pb.Transform = new Transform( centreXY + spin * (pos - centreXY), spin * rot );
	}
}
