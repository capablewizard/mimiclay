using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Editor;

/// <summary>
/// Tails one playtest client's engine log. Every sbox.exe logs to the same <c>logs/sbox.log</c>, but NLog's
/// ArchiveOldFileOnStartup means each new instance renames the previous one's live file to an archive name
/// (<c>sbox-2026-09-30.3.log</c>…) and starts a fresh <c>sbox.log</c> — while the older process keeps writing into
/// its renamed file through its open handle. So each process has its own file, it just doesn't keep its name.
///
/// The fix is to follow the file by NTFS file ID (stable across renames): <see cref="PlaytestLauncher"/> starts
/// clients one at a time and, after each start, claims whichever <c>sbox.log</c> appears with an ID nobody owns
/// yet (<see cref="ClaimNew"/>). Creation times are useless for this — Windows' file-name tunneling hands a new
/// <c>sbox.log</c> the creation time of the one just renamed away.
///
/// Thread-safety: <see cref="Pump"/> runs on the thread pool (the launcher's poller); the editor thread reads
/// <see cref="Snapshot"/> / <see cref="Version"/>. The line buffer is locked.
/// </summary>
public sealed class PlaytestClientLog
{
	const int MaxLines = 5000;

	public static string LogsFolder
	{
		get
		{
			// Same resolution as the engine's own logger (Sandbox.System/Logging): the FACEPUNCH_ENGINE user
			// variable if set, else the install dir — which is our working directory when we spawn clients.
			var root = Environment.GetEnvironmentVariable( "FACEPUNCH_ENGINE", EnvironmentVariableTarget.User );
			return System.IO.Path.Combine( string.IsNullOrEmpty( root ) ? Environment.CurrentDirectory : root, "logs" );
		}
	}

	readonly FileKey _key;
	readonly List<string> _lines = new();
	readonly StringBuilder _partial = new();
	string _path;
	long _offset;
	int _version;

	/// <summary>Where the file currently lives (it moves), or null once it can't be found.</summary>
	public string Path => _path;

	/// <summary>Bumped whenever lines are added, so the UI can tell cheaply whether to refresh.</summary>
	public int Version => System.Threading.Volatile.Read( ref _version );

	PlaytestClientLog( FileKey key, string path )
	{
		_key = key;
		_path = path;
	}

	/// <summary>The ID of the current <c>sbox.log</c>, if any — taken just before a spawn so the old file can't
	/// be mistaken for the new client's.</summary>
	public static FileKey? CurrentLogKey() => TryKey( System.IO.Path.Combine( LogsFolder, "sbox.log" ) );

	/// <summary>Blocking (call on the thread pool): wait for a fresh <c>sbox.log</c> whose ID is neither
	/// <paramref name="before"/> nor already <paramref name="claimed"/>. Null on timeout.</summary>
	public static PlaytestClientLog ClaimNew( FileKey? before, ICollection<FileKey> claimed, TimeSpan timeout )
	{
		var path = System.IO.Path.Combine( LogsFolder, "sbox.log" );
		var until = DateTime.UtcNow + timeout;

		while ( DateTime.UtcNow < until )
		{
			if ( TryKey( path ) is FileKey key && key != before && !claimed.Contains( key ) )
				return new PlaytestClientLog( key, path );

			System.Threading.Thread.Sleep( 150 );
		}

		return null;
	}

	public FileKey Key => _key;

	/// <summary>Read whatever's been appended since last time. Thread pool only.</summary>
	public void Pump()
	{
		try
		{
			if ( _path is null || TryKey( _path ) != _key )
				_path = Locate();

			if ( _path is null )
				return;

			using var fs = new FileStream( _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
			if ( fs.Length < _offset )
				_offset = 0; // truncated — start over
			if ( fs.Length == _offset )
				return;

			fs.Seek( _offset, SeekOrigin.Begin );
			var buffer = new byte[Math.Min( fs.Length - _offset, 1 << 20 )];
			int read = fs.Read( buffer, 0, buffer.Length );
			_offset += read;

			_partial.Append( Encoding.UTF8.GetString( buffer, 0, read ) );
			var text = _partial.ToString();
			int lastBreak = text.LastIndexOf( '\n' );
			if ( lastBreak < 0 )
				return;

			_partial.Clear();
			_partial.Append( text, lastBreak + 1, text.Length - lastBreak - 1 );

			var fresh = text.Substring( 0, lastBreak ).Split( '\n' );
			lock ( _lines )
			{
				foreach ( var raw in fresh )
					_lines.Add( Tidy( raw ) );

				if ( _lines.Count > MaxLines )
					_lines.RemoveRange( 0, _lines.Count - MaxLines );
			}

			System.Threading.Interlocked.Increment( ref _version );
		}
		catch
		{
			// Mid-rename / briefly locked — try again next pump.
		}
	}

	public string[] Snapshot()
	{
		lock ( _lines )
			return _lines.ToArray();
	}

	// "2026/09/30 11:49:25.5689\t[Generic] Connecting..\t" → "11:49:25.568 [Generic] Connecting.."
	static string Tidy( string raw )
	{
		var line = raw.TrimEnd( '\r', '\t', ' ' );
		int tab = line.IndexOf( '\t' );
		if ( tab == 24 && line.Length > 24 && line[4] == '/' )
			line = line.Substring( 11, 12 ) + " " + line.Substring( 25 ).Replace( '\t', ' ' );
		return line;
	}

	string Locate()
	{
		try
		{
			foreach ( var file in Directory.EnumerateFiles( LogsFolder, "sbox*.log" ) )
			{
				if ( TryKey( file ) == _key )
					return file;
			}
		}
		catch
		{
			// Folder briefly unreadable.
		}

		return null;
	}

	static FileKey? TryKey( string path )
	{
		try
		{
			using var fs = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
			if ( !GetFileInformationByHandle( fs.SafeFileHandle, out var info ) )
				return null;

			return new FileKey( info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow );
		}
		catch
		{
			return null;
		}
	}

	public readonly record struct FileKey( uint Volume, ulong Index );

	[StructLayout( LayoutKind.Sequential )]
	struct BY_HANDLE_FILE_INFORMATION
	{
		public uint FileAttributes;
		// Three FILETIMEs as DWORD pairs — a long here would 8-byte-align and shift every later field.
		public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
		public uint VolumeSerialNumber;
		public uint FileSizeHigh, FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh, FileIndexLow;
	}

	[DllImport( "kernel32.dll", SetLastError = true )]
	static extern bool GetFileInformationByHandle( SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info );
}
