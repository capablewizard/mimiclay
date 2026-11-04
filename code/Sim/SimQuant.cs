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
	public static void WriteCtrl( BinaryWriter w, in SimCtrl c )
	{
		w.Write( (byte)((c.Grounded ? 1 : 0) | (c.Parked ? 2 : 0) | (c.AirFromJump ? 4 : 0)) );
		w.Write( c.StillTicks );
		w.Write( c.SinceJumpTicks );
		w.Write( c.PushLevel );
		WriteLinear( w, c.Commanded );
	}

	public static SimCtrl ReadCtrl( BinaryReader r )
	{
		byte f = r.ReadByte();
		return new SimCtrl
		{
			Grounded = (f & 1) != 0, Parked = (f & 2) != 0, AirFromJump = (f & 4) != 0,
			StillTicks = r.ReadByte(), SinceJumpTicks = r.ReadUInt16(), PushLevel = r.ReadUInt16(),
			Commanded = ReadLinear( r ),
		};
	}

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
		w.Flush();
		ms.Position = 0;
		using var r = new BinaryReader( ms );
		return new SimBodyState { P = ReadPos( r ), R = ReadRot( r ), V = ReadLinear( r ), W = ReadSpin( r ), Sleeping = s.Sleeping };
	}
}
