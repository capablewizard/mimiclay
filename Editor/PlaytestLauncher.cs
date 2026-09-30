using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mimiclay;
using Sandbox;

namespace Editor;

/// <summary>
/// One-click multiplayer playtesting: enters play mode on the current scene (the lobby + map scenes self-host a
/// session on start) and launches extra game instances that join through the editor's local TCP socket. The
/// spawn recipe (<c>sbox.exe -joinlocal +instanceid N</c>, windowed flags, user args) is copied from the
/// engine's own viewport "Join via new instance" option, so these clients behave exactly like that flow — they
/// retry the local connect for a while, which covers the seconds the instance spends booting.
///
/// A <see cref="TileLayout"/> (1–6 windows, see <see cref="Layouts"/>) repeated on every selected monitor defines
/// numbered SLOTS; each client lives in a slot (or none) and its window is moved + sized into it. While running,
/// clients can be added into empty slots, moved/swapped between slots, disconnected (graceful leave) or killed,
/// and switching layout reflows the existing clients rather than spawning/killing. Per client the launcher
/// tracks process state, "not responding", the host-side connection (matched by the fake SteamId local instances
/// get: <see cref="BaseFakeSteamId"/> + instance id), the client's own load stage
/// (<see cref="ClientStatusReporter"/>) and its engine log (<see cref="PlaytestClientLog"/>). UI: <see cref="PlaytestDock"/>.
///
/// Rules, learned the hard way:
///  - Everything cross-process runs OFF the editor main thread and moves use SWP_ASYNCWINDOWPOS. A plain
///    SetWindowPos is synchronous across processes — it blocks until the target window's thread pumps messages,
///    and a booting client stalls its pump for seconds at a time. With the watcher on the editor thread that
///    froze the HOST, which stalled the other client's join, which collapsed the whole session.
///  - Tiles are cut from each monitor's actual work area (GetMonitorInfo), never from coordinates guessed off
///    another monitor, so differing resolutions/vertical offsets can't push windows off-screen.
///  - Windows 10+ frames carry an invisible resize border; the tile is matched against the VISIBLE frame
///    (DWMWA_EXTENDED_FRAME_BOUNDS) so neighbouring clients butt up without gaps or overlap.
///  - Clients start ONE AT A TIME: each start waits until that client's log file has appeared and been claimed,
///    since that's the only moment a log file can be tied to a process (see <see cref="PlaytestClientLog"/>).
/// </summary>
public static class PlaytestLauncher
{
	/// <summary>A window arrangement for one monitor: tiles in 0–1 space over its work area.</summary>
	public sealed record TileLayout( string Name, Rect[] Cells );

	public static readonly TileLayout[] Layouts =
	{
		new( "Single", new[] { new Rect( 0, 0, 1, 1 ) } ),
		new( "Split", new[] { new Rect( 0, 0, 0.5f, 1 ), new Rect( 0.5f, 0, 0.5f, 1 ) } ),
		new( "Main + side", new[] { new Rect( 0, 0, 2 / 3f, 1 ), new Rect( 2 / 3f, 0, 1 / 3f, 1 ) } ),
		new( "Main + 2", new[] { new Rect( 0, 0, 0.5f, 1 ), new Rect( 0.5f, 0, 0.5f, 0.5f ), new Rect( 0.5f, 0.5f, 0.5f, 0.5f ) } ),
		new( "3 columns", new[] { new Rect( 0, 0, 1 / 3f, 1 ), new Rect( 1 / 3f, 0, 1 / 3f, 1 ), new Rect( 2 / 3f, 0, 1 / 3f, 1 ) } ),
		new( "2 × 2", Grid( 2, 2 ) ),
		new( "Main + 4", new[]
		{
			new Rect( 0, 0, 0.5f, 1 ),
			new Rect( 0.5f, 0, 0.25f, 0.5f ), new Rect( 0.75f, 0, 0.25f, 0.5f ),
			new Rect( 0.5f, 0.5f, 0.25f, 0.5f ), new Rect( 0.75f, 0.5f, 0.25f, 0.5f ),
		} ),
		new( "3 × 2", Grid( 3, 2 ) ),
	};

	static Rect[] Grid( int cols, int rows )
	{
		var cells = new Rect[cols * rows];
		for ( int y = 0; y < rows; y++ )
			for ( int x = 0; x < cols; x++ )
				cells[y * cols + x] = new Rect( x / (float)cols, y / (float)rows, 1f / cols, 1f / rows );
		return cells;
	}

	/// <summary>Local instances (<c>-joinlocal</c>) get this + their instance id as a fake SteamId, which is what the
	/// host sees on their connection. Mirrors the engine's internal <c>Steam.BaseFakeSteamId</c>.</summary>
	public const ulong BaseFakeSteamId = 90071996842377216;

	const string LayoutCookie = "mimiclay.playtest.layout";
	const string MonitorsCookie = "mimiclay.playtest.monitors";

	/// <summary>Index into <see cref="Layouts"/>, remembered per editor.</summary>
	public static int LayoutIndex
	{
		get => Math.Clamp( EditorCookie.Get( LayoutCookie, 1 ), 0, Layouts.Length - 1 );
		set => EditorCookie.Set( LayoutCookie, Math.Clamp( value, 0, Layouts.Length - 1 ) );
	}

	public static TileLayout Layout => Layouts[LayoutIndex];

	/// <summary>Device names (<c>\\.\DISPLAY2</c>…) of the monitors clients launch onto. Until the user has picked,
	/// defaults to every monitor the editor isn't on — the "secondary monitors" case.</summary>
	public static HashSet<string> SelectedMonitors
	{
		get
		{
			var saved = EditorCookie.Get<string>( MonitorsCookie, null );
			if ( saved is null )
				return GetMonitors().Where( m => !m.HasEditor ).Select( m => m.Device ).ToHashSet();

			return saved.Split( '|', StringSplitOptions.RemoveEmptyEntries ).ToHashSet();
		}
		set => EditorCookie.Set( MonitorsCookie, string.Join( '|', value ) );
	}

	public static void ToggleMonitor( string device )
	{
		var set = SelectedMonitors;
		if ( !set.Remove( device ) )
			set.Add( device );
		SelectedMonitors = set;
	}

	[Menu( "Editor", "Mimiclay/Playtest Launcher…", "connected_tv" )]
	public static void OpenDock()
	{
		EditorWindow.DockManager.SetDockState( PlaytestDock.Title, true );
		EditorWindow.DockManager.RaiseDock( PlaytestDock.Title );
	}

	[Menu( "Editor", "Mimiclay/Playtest (Last Layout)", "play_arrow" )]
	public static void LaunchLast() => Launch();

	// ---------------------------------------------------------------------------------------------------------
	// Monitors + slots

	public sealed class MonitorDesc
	{
		public string Device;
		public RECT Bounds;
		public RECT Work;
		public bool IsPrimary;
		public bool HasEditor;

		/// <summary>1-based, numbered left→right (then top→bottom) — matches how they sit on the desk, not the
		/// Windows display numbers, which are arbitrary.</summary>
		public int Number;
	}

	/// <summary>Every attached monitor, in desk order. Cheap enough to call per paint.</summary>
	public static List<MonitorDesc> GetMonitors()
	{
		var list = new List<MonitorDesc>();
		var editorMonitor = EditorMonitor();

		MonitorEnumProc proc = ( IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data ) =>
		{
			var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
			if ( GetMonitorInfoW( hMonitor, ref info ) )
			{
				list.Add( new MonitorDesc
				{
					Device = info.szDevice,
					Bounds = info.rcMonitor,
					Work = info.rcWork,
					IsPrimary = (info.dwFlags & 1) != 0,
					HasEditor = hMonitor == editorMonitor,
				} );
			}
			return true;
		};

		EnumDisplayMonitors( IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero );
		GC.KeepAlive( proc );

		list.Sort( ( a, b ) => a.Bounds.Left != b.Bounds.Left ? a.Bounds.Left.CompareTo( b.Bounds.Left ) : a.Bounds.Top.CompareTo( b.Bounds.Top ) );
		for ( int i = 0; i < list.Count; i++ )
			list[i].Number = i + 1;

		return list;
	}

	static IntPtr _editorHwnd;

	static IntPtr EditorMonitor()
	{
		// Our own top-level window rather than EditorWindow.ScreenGeometry: that's Qt logical pixels, which
		// stop matching Win32's physical ones the moment display scaling isn't 100%.
		if ( _editorHwnd == IntPtr.Zero || !IsWindow( _editorHwnd ) )
		{
			using var self = Process.GetCurrentProcess();
			_editorHwnd = self.MainWindowHandle;
		}

		return _editorHwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow( _editorHwnd, 2 /* NEAREST */ );
	}

	/// <summary>The screen rect of every slot for the current layout + monitor pick. Slot N = index N: monitor by
	/// monitor in desk order, then the layout's cells in order. Empty when no selected monitor is attached.</summary>
	public static List<RECT> PlannedTiles()
	{
		var selected = SelectedMonitors;
		var tiles = new List<RECT>();

		foreach ( var m in GetMonitors().Where( m => selected.Contains( m.Device ) ) )
		{
			var w = m.Work;
			float width = w.Right - w.Left, height = w.Bottom - w.Top;

			foreach ( var c in Layout.Cells )
			{
				// Round each edge (not position + size) so adjacent tiles share exact pixel edges.
				tiles.Add( new RECT
				{
					Left = w.Left + (int)MathF.Round( c.Left * width ),
					Top = w.Top + (int)MathF.Round( c.Top * height ),
					Right = w.Left + (int)MathF.Round( (c.Left + c.Width) * width ),
					Bottom = w.Top + (int)MathF.Round( (c.Top + c.Height) * height ),
				} );
			}
		}

		return tiles;
	}

	// ---------------------------------------------------------------------------------------------------------
	// Clients

	public sealed class Client
	{
		public int InstanceId { get; init; }
		public Process Process { get; init; }
		public DateTime StartedUtc { get; init; }

		/// <summary>Slot index into <see cref="PlannedTiles"/>, or -1 when untiled. Editor thread.</summary>
		public int Slot { get; internal set; } = -1;

		/// <summary>We asked it to leave (graceful disconnect) — it should now be sitting at the menu.</summary>
		public bool LeaveRequested { get; internal set; }

		/// <summary>Seconds from launch to first reaching <see cref="ClientStage.Ready"/>; null until then.</summary>
		public double? ReadyAfter { get; internal set; }

		volatile PlaytestClientLog _log;
		public PlaytestClientLog Log { get => _log; internal set => _log = value; }

		/// <summary>Couldn't tie a log file to this client (it didn't appear in time).</summary>
		public bool LogUnavailable { get; internal set; }

		// Written by the poller (thread pool), read by the UI.
		volatile bool _exited, _hung;
		volatile int _exitCode;
		public bool Exited => _exited;
		public int ExitCode => _exitCode;
		public bool Hung => _hung;
		internal volatile IntPtr Hwnd;

		// Editor → poller.
		internal volatile TileBox Target;
		internal volatile bool PlaceRequested;

		// Set by the editor thread the first time the client reports Ready (UTC ticks, 0 = not yet). The poller
		// keeps the window pinned to its tile until shortly after this — see Poll.
		long _readyAtTicks;
		internal DateTime? ReadyAtUtc => Volatile.Read( ref _readyAtTicks ) is long t and > 0 ? new DateTime( t, DateTimeKind.Utc ) : null;
		internal void MarkReady() => Interlocked.CompareExchange( ref _readyAtTicks, DateTime.UtcNow.Ticks, 0 );

		// Poller-only placement bookkeeping.
		internal IntPtr PlacedHwnd;
		internal DateTime HoldUntilUtc; // an explicit move/re-tile pins the window at least this long
		internal DateTime PlacedAtUtc;  // last posted move — give it time to land before judging the result
		internal RECT Wanted;
		internal int Corrections;

		internal void MarkExited( int code ) { _exitCode = code; _exited = true; }
		internal void SetHung( bool hung ) => _hung = hung;
	}

	internal sealed class TileBox
	{
		public readonly RECT Rect;
		public TileBox( RECT rect ) => Rect = rect;
	}

	// Editor-thread only. Statics survive Stop→Play (see [[editor-static-persistence]]) — deliberately, so the
	// dock keeps managing clients launched before a play session restarted.
	static readonly List<Client> _clients = new();

	// Immutable copy for the poller thread, republished whenever _clients changes.
	static volatile Client[] _snapshot = Array.Empty<Client>();

	// Slots waiting for their turn in the one-at-a-time spawn queue (so the UI can show them as "queued").
	static readonly List<int> _queued = new();
	static int _queuedUntiled;
	static bool _spawning;

	public static IReadOnlyList<Client> Clients => _clients;

	public static bool IsQueued( int slot ) => _queued.Contains( slot );

	public static int QueuedCount => _queued.Count + _queuedUntiled;

	public static Client ClientInSlot( int slot ) => slot < 0 ? null : _clients.FirstOrDefault( c => c.Slot == slot );

	static void Publish() => _snapshot = _clients.ToArray();

	/// <summary>Fill every empty slot of the current plan (or, with no monitor selected, launch the layout's
	/// count untiled).</summary>
	public static void Launch()
	{
		var tiles = PlannedTiles();

		if ( tiles.Count == 0 )
		{
			Log.Info( "Playtest: no selected monitor is attached — leaving client windows where they spawn." );
			_queuedUntiled += Layout.Cells.Length;
		}
		else
		{
			for ( int slot = 0; slot < tiles.Count; slot++ )
			{
				if ( ClientInSlot( slot ) is null && !_queued.Contains( slot ) )
					_queued.Add( slot );
			}
		}

		StartSpawning();
	}

	/// <summary>Launch one client into a specific (empty) slot.</summary>
	public static void AddAt( int slot )
	{
		if ( ClientInSlot( slot ) is not null || _queued.Contains( slot ) )
			return;

		_queued.Add( slot );
		StartSpawning();
	}

	static void StartSpawning()
	{
		// Enter play mode first (same as the Play button) so the session + local socket exist before the
		// joiners come knocking. Session creation isn't instant, so the "did we end up hosting?" sanity check
		// runs on a delay instead of right here (checking immediately always cried wolf).
		if ( !Game.IsPlaying )
		{
			EditorScene.Play();
			_ = WarnIfNoSessionSoon();
		}

		if ( !_spawning )
			_ = SpawnQueue();
	}

	// One at a time: start a client, wait (off-thread) for its log file to appear and claim it, then the next.
	// The awaits resume on the editor thread, so _clients/_queued stay editor-thread-only.
	static async Task SpawnQueue()
	{
		_spawning = true;
		try
		{
			while ( _queued.Count > 0 || _queuedUntiled > 0 )
			{
				int slot = -1;
				if ( _queued.Count > 0 )
				{
					slot = _queued[0];
					_queued.RemoveAt( 0 );

					// Filled or planned away while it waited.
					if ( slot >= PlannedTiles().Count || ClientInSlot( slot ) is not null )
						continue;
				}
				else
				{
					_queuedUntiled--;
				}

				var before = PlaytestClientLog.CurrentLogKey();
				var client = Spawn( slot );
				if ( client is null )
					continue;

				var claimed = _clients.Where( c => c.Log is not null ).Select( c => c.Log.Key ).ToList();
				var log = await Task.Run( () => PlaytestClientLog.ClaimNew( before, claimed, TimeSpan.FromSeconds( 10 ) ) );

				client.Log = log;
				client.LogUnavailable = log is null;
				if ( log is null )
					Log.Warning( $"Playtest: couldn't find client #{client.InstanceId}'s log file — its log view will be empty." );
			}
		}
		finally
		{
			_spawning = false;
		}
	}

	static Client Spawn( int slot )
	{
		var tiles = PlannedTiles();
		bool place = slot >= 0 && slot < tiles.Count;

		// Tiling needs a window; otherwise honour the editor's own preference.
		bool windowed = place || EditorPreferences.WindowedLocalInstances;

		int id = NextInstanceId();

		var p = new Process();
		p.StartInfo.FileName = "sbox.exe";
		p.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
		p.StartInfo.UseShellExecute = false;

		p.StartInfo.ArgumentList.Add( "-joinlocal" );
		p.StartInfo.ArgumentList.Add( "+instanceid" );
		p.StartInfo.ArgumentList.Add( id.ToString() );

		if ( windowed )
		{
			p.StartInfo.ArgumentList.Add( "-sw" );
			p.StartInfo.ArgumentList.Add( "-720" );
		}

		AddUserArgs( p.StartInfo, EditorPreferences.NewInstanceCommandLineArgs );

		try
		{
			p.Start();
		}
		catch ( Exception e )
		{
			Log.Warning( $"Playtest: couldn't start a client — {e.Message}" );
			p.Dispose();
			return null;
		}

		var client = new Client { InstanceId = id, Process = p, StartedUtc = DateTime.UtcNow };
		if ( place )
			AssignSlot( client, slot, tiles );

		_clients.Add( client );
		Publish();
		EnsurePoller();

		Log.Info( $"Playtest: launched client #{id}{(place ? $" into slot {slot + 1}" : "")}." );
		return client;
	}

	// Highest id handed out this editor session (statics survive Stop→Play, which is what we want here).
	static int _lastInstanceId;

	// The id picks the client's fake SteamId, so it must NEVER be reused while the host might still hold the
	// previous owner's connection: a client killed by Close/Kill lingers on the host until its connection times
	// out, and a relaunch that reused ids 1..N would match the new client to that dead connection — inheriting
	// its old "Ready" report (which also ended the window pin early, so relaunched clients ended up untiled),
	// and giving the host two players with one identity. So ids only go up, and skip any id that still has a
	// connection. Also above every running sbox.exe count — the engine's own flow uses "process count + 1", so
	// this never collides with an instance spawned from its viewport menu either.
	static int NextInstanceId()
	{
		int running = Process.GetProcessesByName( "sbox" ).Length;
		int id = Math.Max( running, _lastInstanceId ) + 1;

		while ( _clients.Any( c => c.InstanceId == id ) || HasConnection( id ) )
			id++;

		_lastInstanceId = id;
		return id;
	}

	static bool HasConnection( int instanceId )
	{
		if ( !Networking.IsActive )
			return false;

		ulong steamId = BaseFakeSteamId + (ulong)instanceId;
		return Connection.All.Any( c => c.SteamId.ValueUnsigned == steamId );
	}

	static void AssignSlot( Client client, int slot, List<RECT> tiles )
	{
		client.Slot = slot;
		client.Target = slot >= 0 && slot < tiles.Count ? new TileBox( tiles[slot] ) : null;
		client.PlaceRequested = client.Target is not null;
	}

	/// <summary>Move a client into another slot, swapping with whoever is there.</summary>
	public static void MoveTo( Client client, int slot )
	{
		if ( client.Slot == slot )
			return;

		var tiles = PlannedTiles();
		if ( slot < 0 || slot >= tiles.Count )
			return;

		var other = ClientInSlot( slot );
		if ( other is not null )
			AssignSlot( other, client.Slot, tiles );

		_queued.Remove( slot );
		AssignSlot( client, slot, tiles );
	}

	static string _planSignature;

	/// <summary>Call regularly (the dock's tick): when the layout / monitor pick / monitor geometry changes,
	/// reflow the running clients into the new slots in their current order. Nothing is spawned or killed;
	/// clients past the new slot count stay running, untiled.</summary>
	public static void CheckPlanChanged()
	{
		var tiles = PlannedTiles();
		var signature = string.Join( ';', tiles.Select( t => $"{t.Left},{t.Top},{t.Right},{t.Bottom}" ) );
		if ( signature == _planSignature )
			return;

		bool first = _planSignature is null;
		_planSignature = signature;
		if ( first )
			return;

		_queued.RemoveAll( s => s >= tiles.Count );

		var ordered = _clients
			.OrderBy( c => c.Slot < 0 ? int.MaxValue : c.Slot )
			.ThenBy( c => c.InstanceId )
			.ToList();

		for ( int i = 0; i < ordered.Count; i++ )
			AssignSlot( ordered[i], i < tiles.Count ? i : -1, tiles );
	}

	/// <summary>Snap every running client back into its slot (after you've dragged windows about).</summary>
	public static void Retile()
	{
		var tiles = PlannedTiles();
		foreach ( var c in _clients )
			AssignSlot( c, c.Slot, tiles );
	}

	/// <summary>The host-side connection for a client, matched by its fake SteamId. Null until it has sent its
	/// user info (i.e. while it's still booting / fetching server info), and after it leaves.</summary>
	public static Connection ConnectionOf( Client client )
	{
		if ( !Networking.IsActive )
			return null;

		ulong steamId = BaseFakeSteamId + (ulong)client.InstanceId;
		return Connection.All.FirstOrDefault( c => c.SteamId.ValueUnsigned == steamId );
	}

	/// <summary>Graceful leave: the client exits to its menu the way a player does. The process keeps running.</summary>
	public static void Disconnect( Client client )
	{
		var connection = ConnectionOf( client );
		if ( connection is null )
		{
			Log.Warning( $"Playtest: client #{client.InstanceId} isn't connected — nothing to disconnect." );
			return;
		}

		using ( Rpc.FilterInclude( connection ) )
			ClientStatusReporter.RequestLeave();

		client.LeaveRequested = true;
	}

	/// <summary>Abrupt drop: terminate the process (the host sees a timeout, like a crash).</summary>
	public static void Kill( Client client )
	{
		Remove( client );

		var p = client.Process;
		_ = Task.Run( () =>
		{
			try { p.Kill(); }
			catch { /* already gone */ }
			p.Dispose();
		} );
	}

	/// <summary>Forget an exited client, freeing its slot.</summary>
	public static void Dismiss( Client client )
	{
		Remove( client );
		client.Process.Dispose();
	}

	public static void CloseAll()
	{
		_queued.Clear();
		_queuedUntiled = 0;

		foreach ( var c in _clients.ToList() )
		{
			if ( c.Exited )
				Dismiss( c );
			else
				Kill( c );
		}
	}

	static void Remove( Client client )
	{
		_clients.Remove( client );
		Publish();
	}

	/// <summary>Bring a client's window to the front.</summary>
	public static void Focus( Client client )
	{
		var hwnd = client.Hwnd;
		if ( hwnd == IntPtr.Zero )
			return;

		_ = Task.Run( () =>
		{
			ShowWindowAsync( hwnd, 9 /* SW_RESTORE */ );
			SetForegroundWindow( hwnd );
		} );
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

	// ---------------------------------------------------------------------------------------------------------
	// Status

	public enum Tone { Busy, Good, Bad, Idle }

	public readonly record struct ClientView( string Title, string Detail, Tone Tone );

	/// <summary>What to show for a client right now. Editor thread (reads the host's connection list and the
	/// client's self-report from <see cref="ClientStatusBoard"/>).</summary>
	public static ClientView Describe( Client c )
	{
		double age = (DateTime.UtcNow - c.StartedUtc).TotalSeconds;

		if ( c.Exited )
			return c.ExitCode == 0
				? new( "Exited", "", Tone.Idle )
				: new( "Crashed", $"exit code {c.ExitCode}", Tone.Bad );

		var connection = ConnectionOf( c );
		string hung = c.Hung ? "NOT RESPONDING · " : "";

		if ( connection is null )
		{
			if ( c.LeaveRequested )
				return new( "At menu", "left the session", Tone.Idle );

			if ( !Game.IsPlaying )
				return new( "Waiting for host", hung + "not in play mode", c.Hung ? Tone.Bad : Tone.Busy );

			return new( "Starting", $"{hung}booting / fetching server info · {age:0}s", c.Hung ? Tone.Bad : Tone.Busy );
		}

		if ( connection.IsConnecting )
			return new( "Joining", $"{hung}handshake · snapshot · {age:0}s", c.Hung ? Tone.Bad : Tone.Busy );

		if ( !ClientStatusBoard.TryGet( connection.Id, out var s ) )
			return new( "Connected", $"{hung}no status report yet · {connection.Ping:0}ms", c.Hung ? Tone.Bad : Tone.Busy );

		if ( s.Stage == ClientStage.Ready && c.ReadyAfter is null )
		{
			c.ReadyAfter = age;
			c.MarkReady();
		}

		double silent = RealTime.Now - s.At;
		string title = s.Stage switch
		{
			ClientStage.LoadingScene => "Loading scene",
			ClientStage.WaitingForGame => "Waiting for game",
			ClientStage.WaitingForPawn => "Spawning pawn",
			ClientStage.BuildingProps => $"Building {s.PendingProps} prop{(s.PendingProps == 1 ? "" : "s")}",
			_ => "Ready",
		};

		var detail = hung + s.Scene
			+ (string.IsNullOrEmpty( s.Phase ) ? "" : $" · {s.Phase}")
			+ $" · {connection.Ping:0}ms"
			+ (c.ReadyAfter is double ready ? $" · ready in {ready:0}s" : "")
			+ (silent > 4 ? $" · silent {silent:0}s" : "");

		var tone = c.Hung || silent > 10 ? Tone.Bad : s.Stage == ClientStage.Ready ? Tone.Good : Tone.Busy;
		return new( title, detail, tone );
	}

	// ---------------------------------------------------------------------------------------------------------
	// Poller: one thread-pool loop for every client — process state, window handle, hung check, placement, log.

	const int PollMs = 500;

	// How long a window stays pinned to its tile. Not a fixed window after the handle appears: a client takes
	// 45–50s to load the game and the engine can resize/move its own window anywhere in that (video settings
	// applying, DPI change on the target monitor), so the pin lasts until it first reports Ready + a grace, with
	// a hard cap in case it never gets there. After that it's left alone so dragging a client mid-test sticks.
	static readonly TimeSpan PinAfterReady = TimeSpan.FromSeconds( 5 );
	static readonly TimeSpan PinCap = TimeSpan.FromMinutes( 3 );
	static readonly TimeSpan PinAfterMove = TimeSpan.FromSeconds( 10 ); // Re-tile / drag-move / reflow
	const int LoggedCorrections = 3;

	static bool _pollerRunning;

	static void EnsurePoller()
	{
		if ( _pollerRunning )
			return;

		_pollerRunning = true;
		_ = Task.Run( PollLoop );
	}

	static async Task PollLoop()
	{
		int idle = 0;
		while ( true )
		{
			await Task.Delay( PollMs ).ConfigureAwait( false );

			var clients = _snapshot;
			if ( clients.Length == 0 )
			{
				if ( ++idle > 20 )
					break;
				continue;
			}

			idle = 0;
			foreach ( var c in clients )
				Poll( c );
		}

		_pollerRunning = false;
		if ( _snapshot.Length > 0 )
			EnsurePoller();
	}

	static void Poll( Client c )
	{
		try
		{
			if ( !c.Exited && c.Process.HasExited )
				c.MarkExited( c.Process.ExitCode );

			if ( !c.Exited )
			{
				c.Process.Refresh();
				var handle = c.Process.MainWindowHandle;
				c.Hwnd = handle;
				c.SetHung( handle != IntPtr.Zero && IsHungAppWindow( handle ) );

				if ( handle != IntPtr.Zero && c.Target is TileBox target )
				{
					var utcNow = DateTime.UtcNow;

					if ( handle != c.PlacedHwnd || c.PlaceRequested )
					{
						if ( c.PlaceRequested )
							c.HoldUntilUtc = utcNow + PinAfterMove;

						c.PlaceRequested = false;
						c.Wanted = Place( handle, target.Rect );
						c.PlacedHwnd = handle;
						c.PlacedAtUtc = utcNow;
					}
					else if ( IsPinned( c, utcNow ) && utcNow - c.PlacedAtUtc > TimeSpan.FromSeconds( 1.5 )
						&& !c.Hung && !VisibleFrameMatches( handle, target.Rect ) )
					{
						// The engine resized/moved its own window mid-boot — put it back.
						if ( c.Corrections++ < LoggedCorrections )
						{
							GetWindowRect( handle, out var now );
							Log.Info( $"Playtest: client #{c.InstanceId} moved/resized its own window " +
								$"{(utcNow - c.StartedUtc).TotalSeconds:0}s after launch (now {now.Width}×{now.Height} at {now.Left},{now.Top}) — putting it back." );
						}

						c.Wanted = Place( handle, target.Rect );
						c.PlacedAtUtc = utcNow;
					}
				}
			}
		}
		catch
		{
			// Process went unqueryable mid-poll — try again next tick.
		}

		c.Log?.Pump();
	}

	static bool IsPinned( Client c, DateTime utcNow )
	{
		if ( utcNow < c.HoldUntilUtc )
			return true;

		if ( utcNow - c.StartedUtc > PinCap )
			return false;

		return c.ReadyAtUtc is not DateTime ready || utcNow < ready + PinAfterReady;
	}

	// Compare the VISIBLE frame (not the outer rect) against the tile: the invisible border can change size when
	// a window crosses to a monitor with different scaling, which would make an outer-rect check misfire.
	static bool VisibleFrameMatches( IntPtr handle, RECT tile )
	{
		const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
		if ( DwmGetWindowAttribute( handle, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>() ) != 0 )
			return true; // can't tell — don't fight it

		return Math.Abs( visible.Left - tile.Left ) <= 1 && Math.Abs( visible.Top - tile.Top ) <= 1
			&& Math.Abs( visible.Right - tile.Right ) <= 1 && Math.Abs( visible.Bottom - tile.Bottom ) <= 1;
	}

	// Fit a window's VISIBLE frame to the tile and return the outer rect that took. Non-blocking: GetWindowRect
	// and the DWM query don't message the target, and the move itself is posted (SWP_ASYNCWINDOWPOS).
	static RECT Place( IntPtr handle, RECT tile )
	{
		const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_ASYNCWINDOWPOS = 0x4000;
		const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

		if ( IsZoomed( handle ) )
			ShowWindowAsync( handle, 9 /* SW_RESTORE */ );

		var outer = tile;
		if ( GetWindowRect( handle, out var window )
			&& DwmGetWindowAttribute( handle, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>() ) == 0 )
		{
			// Grow by the invisible border on each side so the visible frame lands exactly on the tile.
			outer.Left -= visible.Left - window.Left;
			outer.Top -= visible.Top - window.Top;
			outer.Right += window.Right - visible.Right;
			outer.Bottom += window.Bottom - visible.Bottom;
		}

		SetWindowPos( handle, IntPtr.Zero, outer.Left, outer.Top, outer.Right - outer.Left, outer.Bottom - outer.Top,
			SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS );

		return outer;
	}

	// ---------------------------------------------------------------------------------------------------------
	// Win32

	[StructLayout( LayoutKind.Sequential )]
	public struct RECT
	{
		public int Left, Top, Right, Bottom;
		public readonly int Width => Right - Left;
		public readonly int Height => Bottom - Top;
	}

	[StructLayout( LayoutKind.Sequential, CharSet = CharSet.Unicode )]
	struct MONITORINFOEX
	{
		public int cbSize;
		public RECT rcMonitor;
		public RECT rcWork;
		public uint dwFlags;
		[MarshalAs( UnmanagedType.ByValTStr, SizeConst = 32 )]
		public string szDevice;
	}

	delegate bool MonitorEnumProc( IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data );

	[DllImport( "user32.dll" )]
	static extern bool EnumDisplayMonitors( IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data );

	[DllImport( "user32.dll", CharSet = CharSet.Unicode )]
	static extern bool GetMonitorInfoW( IntPtr hMonitor, ref MONITORINFOEX lpmi );

	[DllImport( "user32.dll" )]
	static extern IntPtr MonitorFromWindow( IntPtr hWnd, uint flags );

	[DllImport( "user32.dll" )]
	static extern bool IsWindow( IntPtr hWnd );

	[DllImport( "user32.dll" )]
	static extern bool IsZoomed( IntPtr hWnd );

	[DllImport( "user32.dll" )]
	static extern bool IsHungAppWindow( IntPtr hWnd );

	[DllImport( "user32.dll" )]
	static extern bool ShowWindowAsync( IntPtr hWnd, int cmd );

	[DllImport( "user32.dll" )]
	static extern bool SetForegroundWindow( IntPtr hWnd );

	[DllImport( "user32.dll" )]
	static extern bool GetWindowRect( IntPtr hWnd, out RECT rect );

	[DllImport( "user32.dll" )]
	static extern bool SetWindowPos( IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags );

	[DllImport( "dwmapi.dll" )]
	static extern int DwmGetWindowAttribute( IntPtr hWnd, int attribute, out RECT value, int size );
}
