using System;
using Mimiclay.Sim;

namespace Mimiclay;

// The sim climbs' shared steps — props (HiderController.SimClimb) and hunters (HunterController.SimClimb) keep their climb in
// the same SimCtrl fields, meaning the same things, and these are the steps that read and write them the same way: the
// keys, steering (with the corner carry-over and never-back-into-the-face-we-left), reading the surface's normal, turning
// an inner or outer corner, the pull back toward an edge. What differs stays with each: the body (a clay hull vs a hunter's
// box and capsule) and so every sweep, what may be climbed (props take ceilings and any static; hunters walls only, nothing
// that moves), parking and the mouse turn (props), pulling away, set-back ledges and the rest gap (hunters), and mantling.
// The key rules themselves are ClimbRules (shared with the old scene climbs too).
internal static class SimClimbShared
{
	public static byte Ticks( float seconds ) => (byte)Math.Clamp( (int)MathF.Round( seconds * SimWorld.T ), 1, 255 );

	public const float NormalGate = 0.82f;     // a reading this close to the current normal is the same surface: blended in
	public const float FacingGate = 0.3f;      // a different one still facing us this much takes over once it has held
	public const float CornerMinSpeed = 5f;    // u/s along the surface before the corner look-ahead runs
	public const float CornerCooldown = 0.2f;  // no second corner straight after one — never ping-pong between two faces
	public const float StepOverTime = 0.12f;   // lifting off the surface while passing a bump

	static sbyte Key( float v ) => (sbyte)MathF.Round( v * 127f );
	static Vector3 Keys( sbyte x, sbyte y ) => new( x / 127f, y / 127f, 0f );

	/// <summary>Every tick, any state: the Space press buffer, the let-go and corner cooldowns, Space held on from a mantle.</summary>
	public static void Clock( ref SimCtrl c, in SimInput input, byte grabBufferTicks )
	{
		if ( input.Has( SimInput.Jump ) )
			c.SpaceBuffer = grabBufferTicks;
		else if ( c.SpaceBuffer > 0 )
			c.SpaceBuffer--;
		if ( c.LetGoCool > 0 ) c.LetGoCool--;
		if ( c.CornerCool > 0 ) c.CornerCool--;
		if ( c.ClimbOn && !input.Has( SimInput.JumpHeld ) )
			c.ClimbOn = false;
	}

	/// <summary>Grabbed a surface: the climb's state from fresh.</summary>
	public static void Grab( ref SimCtrl c, Vector3 normal, Vector3 point )
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

	/// <summary>Let go: no re-grab for the cooldown unless Space is pressed afresh (the buffer is cleared), corners forgotten.</summary>
	public static void LetGo( ref SimCtrl c, byte coolTicks )
	{
		c.Climbing = false;
		c.SpaceBuffer = 0;
		c.LetGoCool = coolTicks;
		c.CornerLeft = c.CeilingOut = c.CarryDir = c.CarryFrom = Vector3.Zero;
	}

	/// <summary>What a remote copy repeats (its driver's Guess): the keys, the view, Space/Ctrl.</summary>
	public static void NoteKeys( ref SimCtrl c, Vector3 move, float viewYaw, float vertical )
	{
		c.Wish = new Vector2( MathF.Round( move.x * 127f ), MathF.Round( move.y * 127f ) );
		c.ClimbYaw = viewYaw;
		c.ClimbVertical = (sbyte)vertical;
	}

	/// <summary>The way along the surface this tick: WASD by the view (ClimbRules, W/S along a wall the way you look) — or,
	/// just round a corner with the same keys held, on the way the corner turned them — plus Space/Ctrl up and down it, with
	/// any motion back into the face a corner just took us off dropped while the keys that turned it stay held.
	/// <paramref name="wasd"/>: the WASD part alone; <paramref name="carrying"/>: it's the carried way, not the look's.</summary>
	public static Vector3 Steer( ref SimCtrl c, float viewYaw, Vector3 move, float vertical, Vector3 n, float speed, out Vector3 wasd, out bool carrying )
	{
		wasd = ClimbRules.Along( viewYaw, move, n, speed, lookAlong: true );
		carrying = false;
		if ( c.CarryDir != Vector3.Zero )
		{
			var carryMove = Keys( c.CarryMoveX, c.CarryMoveY );
			if ( !move.IsNearZeroLength && !carryMove.IsNearZeroLength && Vector3.Dot( move.Normal, carryMove.Normal ) > 0.95f && Vector3.Dot( n, c.CarryFrom ) <= 0.97f )
			{
				var flat = c.CarryDir - n * Vector3.Dot( c.CarryDir, n );
				if ( !flat.IsNearZeroLength )
				{
					wasd = flat.Normal * speed * MathF.Min( move.Length, 1f );
					carrying = true;
				}
			}
			else
				c.CarryDir = c.CarryFrom = Vector3.Zero; // new keys (or back on a face like the one we left): the look has W/S again
		}

		var climbUp = ClimbRules.Vertical( vertical, n, c.CeilingOut, speed );
		if ( c.CornerLeft != Vector3.Zero )
		{
			if ( vertical != c.CornerVertical || (move - Keys( c.CornerMoveX, c.CornerMoveY )).Length > 0.05f )
				c.CornerLeft = Vector3.Zero; // new keys: a fresh press back toward it is meant
			else
			{
				wasd = ClimbRules.NotBack( wasd, c.CornerLeft );
				climbUp = ClimbRules.NotBack( climbUp, c.CornerLeft );
			}
		}
		return wasd + climbUp;
	}

	/// <summary>A reading of the surface under us: close to the current normal it's blended in; a different one still facing
	/// us takes over once it has held for <paramref name="holdTicks"/> with no input (one passing reading never flips us).</summary>
	public static void ReadSurface( ref SimCtrl c, Vector3 normal, Vector3 point, bool anyInput, byte holdTicks )
	{
		bool close = Vector3.Dot( normal, c.ClimbN ) > NormalGate;
		c.GateTicks = close || anyInput ? (byte)0 : (byte)Math.Min( c.GateTicks + 1, 255 );
		if ( close )
			c.ClimbN = Vector3.Lerp( c.ClimbN, normal, 0.5f ).Normal;
		else if ( Vector3.Dot( normal, c.ClimbN ) > FacingGate && c.GateTicks > holdTicks )
		{
			c.ClimbN = normal;
			c.GateTicks = 0;
		}
		c.ClimbP = point;
		c.LostTicks = 0;
	}

	/// <summary>The inner-corner look-ahead: from just off the surface and a little back, how far ahead to sweep along the travel.</summary>
	public static void CornerProbe( Vector3 pos, Vector3 n, Vector3 dir, float speed, float backOff, out Vector3 from, out float reach )
	{
		from = pos + n * backOff - dir * backOff;
		reach = backOff + speed * SimWorld.Dt + 3f;
	}

	/// <summary>Onto the face ahead round an inner corner: the face we left and the keys held are remembered (motion back into
	/// it is dropped while they stay held — <see cref="Steer"/>), then a cooldown.</summary>
	public static void TakeInnerCorner( ref SimCtrl c, Vector3 normal, Vector3 point, Vector3 move, float vertical )
	{
		c.CornerLeft = c.ClimbN;
		c.CornerMoveX = Key( move.x );
		c.CornerMoveY = Key( move.y );
		c.CornerVertical = (sbyte)vertical;
		c.ClimbN = normal;
		c.ClimbP = point;
		c.LostTicks = 0;
		c.CornerCool = Ticks( CornerCooldown );
	}

	/// <summary>Is this face, found sweeping back round the end of a wall (normal <paramref name="n"/>, travelling
	/// <paramref name="d"/>), the next face of an outer corner — upright-ish, facing the way we went, turning away from ours?</summary>
	public static bool IsWrapFace( in PhysicsTraceResult tr, Vector3 n, Vector3 d )
		=> tr.Hit && !tr.StartedSolid && MathF.Abs( tr.Normal.z ) <= 0.7f && Vector3.Dot( tr.Normal, d ) >= 0.5f && Vector3.Dot( tr.Normal, n ) <= 0.7f;

	/// <summary>Round an outer corner onto <paramref name="normal"/>: the travel <paramref name="way"/> turns over the edge with
	/// the surface, carried while the same keys stay held (the way on IS back past the old face's plane, so no CornerLeft).</summary>
	public static void TakeOuterCorner( ref SimCtrl c, Vector3 normal, Vector3 point, Vector3 way, Vector3 move )
	{
		var n = c.ClimbN;
		if ( !move.IsNearZeroLength && !way.IsNearZeroLength )
			SetCarry( ref c, Rotation.FromToRotation( n, normal ) * way.Normal, move, n );
		c.CornerLeft = c.CeilingOut = Vector3.Zero;
		c.ClimbN = normal;
		c.ClimbP = point;
		c.ClimbGap = 0f;
		c.LostTicks = 0;
		c.CornerCool = Ticks( CornerCooldown );
	}

	/// <summary>Carry <paramref name="dir"/> on while these keys stay held, leaving the face <paramref name="from"/>.</summary>
	public static void SetCarry( ref SimCtrl c, Vector3 dir, Vector3 move, Vector3 from )
	{
		c.CarryDir = dir;
		c.CarryMoveX = Key( move.x );
		c.CarryMoveY = Key( move.y );
		c.CarryFrom = from;
	}

	/// <summary>Off the surface (past an edge): the pull back toward the last contact, never into the old face faster than the stick.</summary>
	public static Vector3 EdgePull( Vector3 lastContact, Vector3 from, Vector3 n, float pullSpeed, float stick )
	{
		var pull = (lastContact - from).Normal * pullSpeed;
		float inward = Vector3.Dot( pull, -n );
		if ( inward > stick )
			pull -= -n * (inward - stick);
		return pull;
	}
}
