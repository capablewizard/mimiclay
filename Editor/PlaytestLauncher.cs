using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Sandbox;

namespace Editor;

/// <summary>
/// One-click multiplayer playtesting: enters play mode on the current scene (the lobby + map scenes self-host a
/// session on start) and launches extra game instances that join through the editor's local TCP socket. The
/// spawn recipe (<c>sbox.exe -joinlocal +instanceid N</c>, windowed flags, user args) is copied from the
/// engine's own viewport "Join via new instance" option, so these clients behave exactly like that flow — they
/// retry the local connect for a while, which covers the seconds the instance spends booting.
///
/// Client windows are parked on the monitor LEFT of the editor's so they don't land on top of it. Placement
/// rules, learned the hard way:
///  - Everything runs OFF the editor main thread and uses SWP_ASYNCWINDOWPOS. A plain SetWindowPos is
///    synchronous across processes — it blocks until the target window's thread pumps messages, and a booting
///    client stalls its pump for seconds at a time. With the watcher on the editor thread that froze the HOST,
///    which stalled the other client's join, which collapsed the whole session (host + both clients).
///  - Windows are placed inside the left monitor's actual work area (GetMonitorInfo), not at coordinates
///    guessed from the editor's monitor, so differing resolutions/vertical offsets can't push them off-screen.
///  - Skipped entirely when no monitor sits to the editor's left.
/// </summary>
public static class PlaytestLauncher
{
	[Menu( "Editor", "Mimiclay/Playtest + 2 Clients", "connected_tv" )]
	public static void PlayWithTwoClients() => Launch( 2 );

	[Menu( "Editor", "Mimiclay/Playtest + 1 Client", "connected_tv" )]
	public static void PlayWithOneClient() => Launch( 1 );

	// The -sw -720 windowed size the engine's new-instance flow uses; positioning assumes it.
	const int ClientWidth = 1280;
	const int CascadeStep = 120; // vertical offset per extra client, so stacked windows stay distinguishable

	static void Launch( int clients )
	{
		// Enter play mode first (same as the Play button) so the session + local socket exist before the
		// joiners come knocking. Session creation isn't instant, so the "did we end up hosting?" sanity check
		// runs on a delay instead of right here (checking immediately always cried wolf).
		if ( !Game.IsPlaying )
			EditorScene.Play();

		_ = WarnIfNoSessionSoon();

		bool windowed = EditorPreferences.WindowedLocalInstances;
		RECT leftWork = default;
		bool placeLeft = windowed && TryGetLeftMonitorWorkArea( EditorWindow.ScreenGeometry, out leftWork );
		if ( windowed && !placeLeft )
			Log.Info( "Playtest: no monitor to the left of the editor — leaving client windows where they spawn." );

		// Count the running instances ONCE and hand out sequential ids — the engine recounts per spawn, which
		// would give two clients launched back-to-back the same id (the first hasn't started yet).
		int existing = Process.GetProcessesByName( "sbox" ).Length;

		for ( int i = 0; i < clients; i++ )
		{
			var p = new Process();
			p.StartInfo.FileName = "sbox.exe";
			p.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
			p.StartInfo.UseShellExecute = false;

			p.StartInfo.ArgumentList.Add( "-joinlocal" );
			p.StartInfo.ArgumentList.Add( "+instanceid" );
			p.StartInfo.ArgumentList.Add( (existing + 1 + i).ToString() );

			if ( windowed )
			{
				p.StartInfo.ArgumentList.Add( "-sw" );
				p.StartInfo.ArgumentList.Add( "-720" );
			}

			AddUserArgs( p.StartInfo, EditorPreferences.NewInstanceCommandLineArgs );

			p.Start();

			if ( placeLeft )
			{
				// Right-aligned inside the LEFT monitor's own work area, cascading down per client.
				int x = leftWork.Right - ClientWidth - 16;
				int y = Math.Min( leftWork.Top + 48 + i * CascadeStep, leftWork.Bottom - 200 );
				_ = MoveWindowWhenReady( p, x, y );
			}
			else
			{
				p.Dispose();
			}
		}

		Log.Info( $"Playtest: launched {clients} client instance{(clients == 1 ? "" : "s")} joining the local session." );
	}

	static async Task WarnIfNoSessionSoon()
	{
		await Task.Delay( 8000 );
		if ( !Networking.IsActive )
			Log.Warning( "Playtest: still no session 8s after Play — this scene doesn't self-host, so host manually before the clients give up." );
	}

	static void AddUserArgs( ProcessStartInfo startInfo, string argumentString )
	{
		if ( string.IsNullOrWhiteSpace( argumentString ) )
			return;

		foreach ( var arg in argumentString.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			startInfo.ArgumentList.Add( arg );
	}

	// Watch the starting instance and move its window to (x, y) as it appears. The main window handle changes
	// as the instance boots (splash → game window), so keep watching for a while and re-place each new handle.
	// Runs entirely on the thread pool (ConfigureAwait(false)) and posts the move asynchronously — see the
	// class comment for why touching a booting process from the editor thread is catastrophic.
	static async Task MoveWindowWhenReady( Process p, int x, int y )
	{
		const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_ASYNCWINDOWPOS = 0x4000;

		try
		{
			var moved = IntPtr.Zero;
			for ( int tick = 0; tick < 90; tick++ )
			{
				await Task.Delay( 1000 ).ConfigureAwait( false );
				if ( p.HasExited )
					return;

				p.Refresh();
				var handle = p.MainWindowHandle;
				if ( handle != IntPtr.Zero && handle != moved )
				{
					SetWindowPos( handle, IntPtr.Zero, x, y, 0, 0,
						SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS );
					moved = handle;
				}
			}
		}
		catch
		{
			// The process died or went unqueryable mid-boot — nothing to place.
		}
		finally
		{
			p.Dispose();
		}
	}

	// Find the monitor left of the given screen rect and return its work area. Probes several heights along
	// the editor screen's left edge, since side-by-side monitors can sit at different vertical offsets.
	static bool TryGetLeftMonitorWorkArea( Rect editorScreen, out RECT workArea )
	{
		workArea = default;

		int probeX = (int)editorScreen.Left - 64;
		Span<int> probeYs = stackalloc int[]
		{
			(int)(editorScreen.Top + editorScreen.Height * 0.5f),
			(int)editorScreen.Top + 100,
			(int)(editorScreen.Top + editorScreen.Height) - 100,
		};

		foreach ( var probeY in probeYs )
		{
			// MONITOR_DEFAULTTONULL: zero when no monitor contains the point.
			var monitor = MonitorFromPoint( new POINT { X = probeX, Y = probeY }, 0 );
			if ( monitor == IntPtr.Zero )
				continue;

			var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
			if ( !GetMonitorInfoW( monitor, ref info ) )
				continue;

			workArea = info.rcWork;
			return true;
		}

		return false;
	}

	[StructLayout( LayoutKind.Sequential )]
	struct POINT { public int X, Y; }

	[StructLayout( LayoutKind.Sequential )]
	struct RECT { public int Left, Top, Right, Bottom; }

	[StructLayout( LayoutKind.Sequential )]
	struct MONITORINFO
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
	}

	[DllImport( "user32.dll" )]
	static extern bool SetWindowPos( IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags );

	[DllImport( "user32.dll" )]
	static extern IntPtr MonitorFromPoint( POINT pt, uint flags );

	[DllImport( "user32.dll" )]
	static extern bool GetMonitorInfoW( IntPtr hMonitor, ref MONITORINFO lpmi );
}
