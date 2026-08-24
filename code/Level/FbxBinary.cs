using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mimiclay;

/// <summary>
/// One node of a parsed binary FBX file. Properties hold: long (Y/I/L), double (F/D), bool (C),
/// string (S), byte[] (R), double[] (f/d), int[] (i), long[] (l) or bool[] (b).
/// </summary>
public sealed class FbxNode
{
	public string Name;
	public List<object> Properties = new();
	public List<FbxNode> Children = new();

	public FbxNode Find( string name ) => Children.Find( c => c.Name == name );

	public IEnumerable<FbxNode> FindAll( string name )
	{
		foreach ( var c in Children )
			if ( c.Name == name )
				yield return c;
	}

	public long GetLong( int i ) => i < Properties.Count ? Properties[i] switch { long l => l, double d => (long)d, bool b => b ? 1 : 0, _ => 0 } : 0;
	public double GetDouble( int i ) => i < Properties.Count ? Properties[i] switch { double d => d, long l => l, _ => 0 } : 0;
	public string GetString( int i ) => i < Properties.Count ? Properties[i] as string ?? "" : "";
	public double[] GetDoubleArray( int i ) => i < Properties.Count ? Properties[i] as double[] : null;
	public int[] GetIntArray( int i ) => i < Properties.Count ? Properties[i] as int[] : null;

	/// <summary>Object name with the binary "Name\0\x01Class" suffix stripped.</summary>
	public string GetObjectName( int i )
	{
		var s = GetString( i );
		var cut = s.IndexOf( '\0' );
		return cut >= 0 ? s[..cut] : s;
	}
}

/// <summary>
/// Minimal binary-FBX reader (versions 7100–7700, covers every Blender export). Parses the raw
/// node/property tree only — semantic extraction lives in <see cref="FbxSceneReader"/>.
/// </summary>
public static class FbxBinary
{
	const string Magic = "Kaydara FBX Binary  ";

	public static bool LooksBinary( byte[] data )
		=> data != null && data.Length > 27 && Encoding.ASCII.GetString( data, 0, 20 ) == Magic;

	/// <summary>Parse a whole file. Returns a nameless root whose Children are the top-level nodes. Throws on malformed data.</summary>
	public static FbxNode Parse( byte[] data )
	{
		if ( !LooksBinary( data ) )
			throw new Exception( "Not a binary FBX file. Export from Blender with the default (binary) FBX format." );

		var version = BitConverter.ToUInt32( data, 23 );
		var wide = version >= 7500; // 64-bit node headers

		var root = new FbxNode { Name = "" };
		var pos = 27;
		while ( pos < data.Length )
		{
			var node = ReadNode( data, ref pos, wide );
			if ( node == null )
				break;
			root.Children.Add( node );
		}

		return root;
	}

	static FbxNode ReadNode( byte[] d, ref int pos, bool wide )
	{
		long endOffset, numProps;
		if ( wide )
		{
			endOffset = (long)BitConverter.ToUInt64( d, pos );
			numProps = (long)BitConverter.ToUInt64( d, pos + 8 );
			pos += 24; // end offset, prop count, prop list length
		}
		else
		{
			endOffset = BitConverter.ToUInt32( d, pos );
			numProps = BitConverter.ToUInt32( d, pos + 4 );
			pos += 12;
		}

		var nameLen = d[pos++];
		if ( endOffset == 0 )
			return null; // null record — terminates a nested list / the top level

		var node = new FbxNode { Name = Encoding.ASCII.GetString( d, pos, nameLen ) };
		pos += nameLen;

		for ( long i = 0; i < numProps; i++ )
			node.Properties.Add( ReadProperty( d, ref pos ) );

		while ( pos < endOffset )
		{
			var child = ReadNode( d, ref pos, wide );
			if ( child == null )
				break;
			node.Children.Add( child );
		}

		pos = (int)endOffset;
		return node;
	}

	static object ReadProperty( byte[] d, ref int pos )
	{
		var type = (char)d[pos++];
		switch ( type )
		{
			case 'Y': { var v = BitConverter.ToInt16( d, pos ); pos += 2; return (long)v; }
			case 'C': { var v = d[pos] != 0; pos += 1; return v; }
			case 'I': { var v = BitConverter.ToInt32( d, pos ); pos += 4; return (long)v; }
			case 'L': { var v = BitConverter.ToInt64( d, pos ); pos += 8; return v; }
			case 'F': { var v = BitConverter.ToSingle( d, pos ); pos += 4; return (double)v; }
			case 'D': { var v = BitConverter.ToDouble( d, pos ); pos += 8; return v; }

			case 'S':
			case 'R':
			{
				var len = BitConverter.ToInt32( d, pos );
				pos += 4;
				object result;
				if ( type == 'S' )
				{
					result = Encoding.UTF8.GetString( d, pos, len );
				}
				else
				{
					var raw = new byte[len];
					Array.Copy( d, pos, raw, 0, len );
					result = raw;
				}
				pos += len;
				return result;
			}

			case 'f':
			{
				var raw = ReadArrayPayload( d, ref pos, out var n, 4 );
				var a = new double[n];
				for ( var i = 0; i < n; i++ ) a[i] = BitConverter.ToSingle( raw, i * 4 );
				return a;
			}
			case 'd':
			{
				var raw = ReadArrayPayload( d, ref pos, out var n, 8 );
				var a = new double[n];
				for ( var i = 0; i < n; i++ ) a[i] = BitConverter.ToDouble( raw, i * 8 );
				return a;
			}
			case 'i':
			{
				var raw = ReadArrayPayload( d, ref pos, out var n, 4 );
				var a = new int[n];
				for ( var i = 0; i < n; i++ ) a[i] = BitConverter.ToInt32( raw, i * 4 );
				return a;
			}
			case 'l':
			{
				var raw = ReadArrayPayload( d, ref pos, out var n, 8 );
				var a = new long[n];
				for ( var i = 0; i < n; i++ ) a[i] = BitConverter.ToInt64( raw, i * 8 );
				return a;
			}
			case 'b':
			{
				var raw = ReadArrayPayload( d, ref pos, out var n, 1 );
				var a = new bool[n];
				for ( var i = 0; i < n; i++ ) a[i] = raw[i] != 0;
				return a;
			}

			default:
				throw new Exception( $"Unknown FBX property type '{type}' at offset {pos - 1}." );
		}
	}

	static byte[] ReadArrayPayload( byte[] d, ref int pos, out int count, int elemSize )
	{
		count = BitConverter.ToInt32( d, pos );
		var encoding = BitConverter.ToInt32( d, pos + 4 );
		var compressedLen = BitConverter.ToInt32( d, pos + 8 );
		pos += 12;

		var result = new byte[count * elemSize];

		if ( encoding == 0 )
		{
			Array.Copy( d, pos, result, 0, result.Length );
			pos += result.Length;
			return result;
		}

		// encoding 1 = zlib: 2-byte header, deflate body, adler32 tail (DeflateStream stops before the tail)
		using var src = new MemoryStream( d, pos + 2, compressedLen - 2 );
		using var inflate = new DeflateStream( src, CompressionMode.Decompress );
		var read = 0;
		while ( read < result.Length )
		{
			var r = inflate.Read( result, read, result.Length - read );
			if ( r <= 0 )
				throw new Exception( "Truncated compressed array in FBX file." );
			read += r;
		}

		pos += compressedLen;
		return result;
	}
}
