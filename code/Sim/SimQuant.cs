using System;
using System.IO;

namespace Mimiclay.Sim;

/// <summary>
/// Snapshot compression. An awake body costs 27 bytes (id 2, position 9, rotation 4, velocity 6, spin 6) instead of 53
/// raw. Every error stays well under SimWorld.SkipEpsilon (0.05u), so a quantised snapshot that matches the prediction
/// still reads as matching:
/// <list type="bullet">
/// <item>position: 24 bits per axis at 1/32 u (±262144 u; error ≤ 1/64 u)</item>
/// <item>rotation: smallest-three, 2-bit index + 3 × 10 bits (error ≈ 0.08°)</item>
/// <item>velocity: 16 bits per axis at 1/8 u/s (±4096 u/s)</item>
/// <item>spin: 16 bits per axis at 1/100 rad/s (±327 rad/s)</item>
/// </list>
/// Reliable messages (adds, rest poses) use full floats.
/// </summary>
public static class SimQuant
{
	const float PosScale = 32f;
	const int PosMax = (1 << 23) - 1;
	const float VelScale = 8f;
	const float SpinScale = 100f;

	public static void WritePos( BinaryWriter w, Vector3 p )
	{
		Write24( w, p.x ); Write24( w, p.y ); Write24( w, p.z );
	}

	public static Vector3 ReadPos( BinaryReader r ) => new( Read24( r ), Read24( r ), Read24( r ) );

	static void Write24( BinaryWriter w, float v )
	{
		int q = Math.Clamp( (int)MathF.Round( v * PosScale ), -PosMax, PosMax );
		uint u = (uint)q & 0xFFFFFF;
		w.Write( (byte)u ); w.Write( (byte)(u >> 8) ); w.Write( (byte)(u >> 16) );
	}

	static float Read24( BinaryReader r )
	{
		uint u = r.ReadByte() | ((uint)r.ReadByte() << 8) | ((uint)r.ReadByte() << 16);
		int q = (int)(u << 8) >> 8; // sign-extend
		return q / PosScale;
	}

	public static void WriteVel( BinaryWriter w, Vector3 v, float scale )
	{
		w.Write( Q16( v.x * scale ) ); w.Write( Q16( v.y * scale ) ); w.Write( Q16( v.z * scale ) );
	}

	public static Vector3 ReadVel( BinaryReader r, float scale ) => new( r.ReadInt16() / scale, r.ReadInt16() / scale, r.ReadInt16() / scale );

	static short Q16( float v ) => (short)Math.Clamp( (int)MathF.Round( v ), short.MinValue, short.MaxValue );

	public static void WriteLinear( BinaryWriter w, Vector3 v ) => WriteVel( w, v, VelScale );
	public static Vector3 ReadLinear( BinaryReader r ) => ReadVel( r, VelScale );
	public static void WriteSpin( BinaryWriter w, Vector3 v ) => WriteVel( w, v, SpinScale );
	public static Vector3 ReadSpin( BinaryReader r ) => ReadVel( r, SpinScale );

	// Smallest three: drop the largest component (recoverable from unit length), store the other three in [-1/√2, 1/√2].
	const float QMax = 0.70710678f;
	const int QBits = 10;
	const int QRange = (1 << QBits) - 1;

	public static void WriteRot( BinaryWriter w, Rotation q )
	{
		Span<float> c = stackalloc float[4] { q.x, q.y, q.z, q.w };
		int big = 0;
		for ( int i = 1; i < 4; i++ )
			if ( MathF.Abs( c[i] ) > MathF.Abs( c[big] ) ) big = i;
		float sign = c[big] < 0f ? -1f : 1f;

		uint packed = (uint)big << 30;
		int shift = 20;
		for ( int i = 0; i < 4; i++ )
		{
			if ( i == big ) continue;
			float v = Math.Clamp( c[i] * sign, -QMax, QMax );
			uint qv = (uint)MathF.Round( (v + QMax) / (2f * QMax) * QRange );
			packed |= qv << shift;
			shift -= QBits;
		}
		w.Write( packed );
	}

	public static Rotation ReadRot( BinaryReader r )
	{
		uint packed = r.ReadUInt32();
		int big = (int)(packed >> 30);
		Span<float> c = stackalloc float[4];
		int shift = 20;
		float sum = 0f;
		for ( int i = 0; i < 4; i++ )
		{
			if ( i == big ) continue;
			uint qv = (packed >> shift) & QRange;
			float v = qv / (float)QRange * (2f * QMax) - QMax;
			c[i] = v;
			sum += v * v;
			shift -= QBits;
		}
		c[big] = MathF.Sqrt( MathF.Max( 0f, 1f - sum ) );
		return new Rotation( c[0], c[1], c[2], c[3] ).Normal;
	}

	// A driven body's controller state: flags byte, still ticks, two tick counters, the commanded velocity (12 bytes).
	// (+2 bytes: the grab buffer and let-go cooldown; a climbing body adds its climb block, ~30–60 bytes; a mantle 9.)
	public static void WriteCtrl( BinaryWriter w, in SimCtrl c )
	{
		w.Write( (byte)((c.Grounded ? 1 : 0) | (c.Parked ? 2 : 0) | (c.AirFromJump ? 4 : 0) | (c.Holding ? 8 : 0) | (c.Driving ? 16 : 0)
			| (c.Climbing ? 32 : 0) | (c.MantleTicks > 0 ? 64 : 0) | (c.ClimbOn ? 128 : 0)) );
		w.Write( c.StillTicks );
		w.Write( c.SinceJumpTicks );
		w.Write( c.PushLevel );
		WriteLinear( w, c.Commanded );
		w.Write( (short)Math.Clamp( c.Wish.x * 8f, short.MinValue, short.MaxValue ) ); // the real last wish, 1/8 u/s
		w.Write( (short)Math.Clamp( c.Wish.y * 8f, short.MinValue, short.MaxValue ) );
		w.Write( c.SpaceBuffer );
		w.Write( c.LetGoCool );
		w.Write( (byte)(c.Ducked ? 1 : 0) ); // the second flags byte (hunters)
		w.Write( c.PadCool );
		if ( c.Climbing )
		{
			WriteUnit( w, c.ClimbN );
			WritePos( w, c.ClimbP );
			w.Write( c.LostTicks ); w.Write( c.StepOverTicks ); w.Write( c.GateTicks ); w.Write( c.CornerCool );
			bool left = c.CornerLeft != Vector3.Zero, ceil = c.CeilingOut != Vector3.Zero, carry = c.CarryDir != Vector3.Zero;
			w.Write( (byte)((left ? 1 : 0) | (ceil ? 2 : 0) | (carry ? 4 : 0)) );
			w.Write( (short)MathF.Round( QuantYawOf( c.ClimbYaw ) * 100f ) );
			w.Write( c.ClimbVertical );
			w.Write( (short)MathF.Round( Math.Clamp( c.ClimbGap, -1000f, 1000f ) * 16f ) ); // (hunters: the gap to the surface, 1/16 u)
			if ( left )
			{
				WriteUnit( w, c.CornerLeft );
				w.Write( c.CornerMoveX ); w.Write( c.CornerMoveY ); w.Write( c.CornerVertical );
			}
			if ( ceil )
				WriteUnit( w, c.CeilingOut );
			if ( carry )
			{
				WriteUnit( w, c.CarryDir ); WriteUnit( w, c.CarryFrom );
				w.Write( c.CarryMoveX ); w.Write( c.CarryMoveY );
			}
		}
		if ( c.MantleTicks > 0 )
		{
			w.Write( c.MantleTicks );
			w.Write( (short)MathF.Round( Math.Clamp( c.MantleDir.x, -1f, 1f ) * 32767f ) );
			w.Write( (short)MathF.Round( Math.Clamp( c.MantleDir.y, -1f, 1f ) * 32767f ) );
			w.Write( c.MantleZ );
		}
	}

	public static SimCtrl ReadCtrl( BinaryReader r )
	{
		byte f = r.ReadByte();
		var c = new SimCtrl
		{
			Grounded = (f & 1) != 0, Parked = (f & 2) != 0, AirFromJump = (f & 4) != 0, Holding = (f & 8) != 0, Driving = (f & 16) != 0,
			Climbing = (f & 32) != 0, ClimbOn = (f & 128) != 0,
			StillTicks = r.ReadByte(), SinceJumpTicks = r.ReadUInt16(), PushLevel = r.ReadUInt16(),
			Commanded = ReadLinear( r ),
			Wish = new Vector2( r.ReadInt16() / 8f, r.ReadInt16() / 8f ),
			SpaceBuffer = r.ReadByte(), LetGoCool = r.ReadByte(),
		};
		byte f2 = r.ReadByte();
		c.Ducked = (f2 & 1) != 0;
		c.PadCool = r.ReadByte();
		if ( c.Climbing )
		{
			c.ClimbN = ReadUnit( r );
			c.ClimbP = ReadPos( r );
			c.LostTicks = r.ReadByte(); c.StepOverTicks = r.ReadByte(); c.GateTicks = r.ReadByte(); c.CornerCool = r.ReadByte();
			byte sub = r.ReadByte();
			c.ClimbYaw = r.ReadInt16() / 100f;
			c.ClimbVertical = r.ReadSByte();
			c.ClimbGap = r.ReadInt16() / 16f;
			if ( (sub & 1) != 0 )
			{
				c.CornerLeft = ReadUnit( r );
				c.CornerMoveX = r.ReadSByte(); c.CornerMoveY = r.ReadSByte(); c.CornerVertical = r.ReadSByte();
			}
			if ( (sub & 2) != 0 )
				c.CeilingOut = ReadUnit( r );
			if ( (sub & 4) != 0 )
			{
				c.CarryDir = ReadUnit( r ); c.CarryFrom = ReadUnit( r );
				c.CarryMoveX = r.ReadSByte(); c.CarryMoveY = r.ReadSByte();
			}
		}
		if ( (f & 64) != 0 )
		{
			c.MantleTicks = r.ReadByte();
			c.MantleDir = new Vector2( r.ReadInt16() / 32767f, r.ReadInt16() / 32767f );
			c.MantleZ = r.ReadSingle();
		}
		return c;
	}

	static float QuantYawOf( float yaw ) => SimInput.QuantYaw( yaw );

	// A unit-ish vector (a normal, a direction): three shorts, ~0.003° steps. Not renormalised on read — the host continues
	// from the same decoded numbers (RoundTrip), so both sides agree to the bit.
	static void WriteUnit( BinaryWriter w, Vector3 v )
	{
		w.Write( (short)MathF.Round( Math.Clamp( v.x, -1f, 1f ) * 32767f ) );
		w.Write( (short)MathF.Round( Math.Clamp( v.y, -1f, 1f ) * 32767f ) );
		w.Write( (short)MathF.Round( Math.Clamp( v.z, -1f, 1f ) * 32767f ) );
	}

	static Vector3 ReadUnit( BinaryReader r ) => new( r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f );

	// Full precision, for reliable messages.
	public static void WriteFull( BinaryWriter w, Vector3 v ) { w.Write( v.x ); w.Write( v.y ); w.Write( v.z ); }
	public static Vector3 ReadFull( BinaryReader r ) => new( r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );
	public static void WriteFull( BinaryWriter w, Rotation q ) { w.Write( q.x ); w.Write( q.y ); w.Write( q.z ); w.Write( q.w ); }
	public static Rotation ReadFullRot( BinaryReader r ) => new( r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() );

	/// <summary>The state as a client will decode it — the host simulates from this too after each snapshot, so both
	/// sides start the next ticks from identical numbers.</summary>
	public static SimBodyState RoundTrip( in SimBodyState s )
	{
		using var ms = new MemoryStream( 32 );
		using var w = new BinaryWriter( ms );
		WritePos( w, s.P ); WriteRot( w, s.R ); WriteLinear( w, s.V ); WriteSpin( w, s.W );
		if ( s.HasCtrl )
			WriteCtrl( w, s.Ctrl ); // the controller state too: a climb's normal and points decode coarser than the host keeps them
		w.Flush();
		ms.Position = 0;
		using var r = new BinaryReader( ms );
		var o = new SimBodyState { P = ReadPos( r ), R = ReadRot( r ), V = ReadLinear( r ), W = ReadSpin( r ), Sleeping = s.Sleeping };
		if ( s.HasCtrl )
		{
			o.Ctrl = ReadCtrl( r );
			o.HasCtrl = true;
		}
		return o;
	}
}
