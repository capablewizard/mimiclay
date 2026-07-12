using System;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// The menu's one door to s&amp;box networking. Every front-end action that touches lobbies/sessions goes through
/// here — hosting, quick play, listing public lobbies for the server browser, joining one, and the host's scene
/// change into gameplay — so the Razor pages stay declarative and there's a single place to evolve as the real
/// game mode lands.
///
/// Host settings (mode, max players, privacy, name) are written into the lobby's networked key/value data on
/// create, so the server browser can show what each session is running and a future RoundManager can read which
/// mode to start. The data keys are the contract; keep them in sync with <see cref="Keys"/>.
/// </summary>
public static class MenuNetworking
{
	/// <summary>Lobby data keys (the menu's networked contract). Short strings — Steam lobby data is limited.</summary>
	public static class Keys
	{
		public const string Mode = "mode";       // GameModeKind name
		public const string Name = "name";       // host-chosen session name
	}

	/// <summary>Default cap offered in the Host screen, bounded by what the package allows.</summary>
	public const int DefaultMaxPlayers = 8;
	public const int MinMaxPlayers = 2;
	public const int MaxMaxPlayers = 16;

	/// <summary>True while a connect/host/query is in flight, so the UI can show a spinner and gate buttons.</summary>
	public static bool Busy { get; private set; }

	/// <summary>True once this process has deliberately been in a session (hosted or joined). Gameplay-scene
	/// bootstraps (<see cref="RoundManagerSpawner"/>, <see cref="LobbyController"/>) read this to tell a genuine
	/// direct Play (never in a session — safe to self-host) from a client that's briefly !IsActive while following
	/// the host's scene change (must NOT self-host — that would fork it into a private parallel session it can
	/// never leave except via the menu). Intent, not timing: the old grace-window approach forked any client whose
	/// reconnect outlasted the window. Reset by <see cref="NoteSessionEnded"/> — <see cref="ExitToMenu"/>'s
	/// deliberate leave, and <see cref="SessionResetSystem"/> when play itself stops.</summary>
	public static bool EverInSession { get; private set; }

	/// <summary>Record that this process deliberately entered a session — see <see cref="EverInSession"/>. Called
	/// on host/join here, and by the scene bootstraps when they legitimately self-host a direct Play.</summary>
	public static void NoteSessionStarted() => EverInSession = true;

	/// <summary>Forget the session intent — the session is over, so a later direct Play is safe to self-host
	/// again. Called by <see cref="ExitToMenu"/> and by <see cref="SessionResetSystem"/> at play teardown.</summary>
	public static void NoteSessionEnded() => EverInSession = false;

	/// <summary>Last user-facing error from a failed action (e.g. "No games found"), or null. The UI surfaces it.</summary>
	public static string LastError { get; private set; }

	/// <summary>How long an error stays before it's auto-cleared. The UI fades it out across this window, then
	/// it's removed (so it doesn't linger invisibly or reappear stale on a later visit). See MainMenuNav.Tick.</summary>
	public const float ErrorLifetime = 4.2f;

	/// <summary>Seconds since <see cref="LastError"/> was last set — drives the auto-clear.</summary>
	public static float ErrorAge => _errorSince;
	static RealTimeSince _errorSince;

	/// <summary>Bumped each time an error is (re)set, so the UI can re-key the message and replay its fade even
	/// when the same text comes back.</summary>
	public static int ErrorStamp { get; private set; }

	// Set a user-facing error and (re)start its lifetime/fade.
	static void SetError( string message )
	{
		LastError = message;
		_errorSince = 0;
		ErrorStamp++;
	}

	/// <summary>Clear the current error immediately (the UI's fade timer calls this once it expires).</summary>
	public static void ClearError() => LastError = null;

	/// <summary>Host a new lobby for the chosen mode/settings, then (as host) load that mode's gameplay scene. The
	/// settings are written into lobby data first so they're live the instant clients see the session.</summary>
	public static void Host( GameModeKind mode, int maxPlayers, LobbyPrivacy privacy, string sessionName )
	{
		if ( Busy )
			return;

		LastError = null;

		// Already in a session (e.g. relaunched from an in-game "back to menu" that didn't disconnect)? Leave first.
		if ( Networking.IsActive )
			Networking.Disconnect();

		var info = GameModes.Get( mode );
		var config = new LobbyConfig
		{
			MaxPlayers = Math.Clamp( maxPlayers, MinMaxPlayers, MaxMaxPlayers ),
			Privacy = privacy,
			Hidden = privacy != LobbyPrivacy.Public,
			Name = string.IsNullOrWhiteSpace( sessionName ) ? $"{info.Label} Game" : sessionName.Trim(),
		};

		Networking.CreateLobby( config );
		NoteSessionStarted();

		// Stamp the session so the browser + round manager know what's running. CreateLobby makes us host
		// synchronously, so SetData is safe right after.
		Networking.SetData( Keys.Mode, mode.ToString() );
		Networking.SetData( Keys.Name, config.Name );

		LoadGameplayScene( info );
	}

	/// <summary>Quick Play: find the best public lobby for this game and join it. Falls back to an error the UI shows
	/// (the caller can then offer "Host instead"). Async because querying + connecting hits Steam.</summary>
	public static async Task QuickPlay()
	{
		if ( Busy )
			return;

		Busy = true;
		LastError = null;
		try
		{
			if ( Networking.IsActive )
				Networking.Disconnect();

			// JoinBestLobby queries public lobbies for our game ident and connects to the fullest joinable one.
			var joined = await Networking.JoinBestLobby( Game.Ident );
			if ( joined )
				NoteSessionStarted();
			else
				SetError( "No open games found. Try hosting one!" );
		}
		catch ( Exception e )
		{
			SetError( "Couldn't reach matchmaking." );
			Log.Warning( $"QuickPlay failed: {e.Message}" );
		}
		finally
		{
			Busy = false;
		}
	}

	/// <summary>Fetch the public lobbies for this game for the server browser. Returns an empty list on failure
	/// (with <see cref="LastError"/> set) rather than throwing, so the UI can just bind to the result.</summary>
	public static async Task<List<LobbyInformation>> QueryGames( CancellationToken ct = default )
	{
		LastError = null;
		try
		{
			var lobbies = await Networking.QueryLobbies( Game.Ident, ct );
			// Hide full / explicitly-hidden sessions from the browser; show the rest newest-fullest first.
			return lobbies
				.Where( l => !l.IsHidden && !l.IsFull )
				.OrderByDescending( l => l.Members )
				.ToList();
		}
		catch ( OperationCanceledException )
		{
			return new List<LobbyInformation>();
		}
		catch ( Exception e )
		{
			SetError( "Couldn't load the server list." );
			Log.Warning( $"QueryGames failed: {e.Message}" );
			return new List<LobbyInformation>();
		}
	}

	/// <summary>Join a specific lobby from the server browser.</summary>
	public static async Task Join( LobbyInformation lobby )
	{
		if ( Busy )
			return;

		Busy = true;
		LastError = null;
		try
		{
			if ( Networking.IsActive )
				Networking.Disconnect();

			var ok = await Networking.TryConnectSteamId( lobby.LobbyId );
			if ( ok )
				NoteSessionStarted();
			else
				SetError( "Couldn't join that game." );
		}
		catch ( Exception e )
		{
			SetError( "Couldn't join that game." );
			Log.Warning( $"Join failed: {e.Message}" );
		}
		finally
		{
			Busy = false;
		}
	}

	/// <summary>Read the mode a queried lobby is running (for the browser row). Falls back to PropHunt.</summary>
	public static GameModeKind ModeOf( LobbyInformation lobby )
		=> Enum.TryParse<GameModeKind>( lobby.Get( Keys.Mode ), out var m ) ? m : GameModeKind.PropHunt;

	/// <summary>Leave the current session and return to the front-end menu scene. The reverse of the host/join
	/// handoff: drop the lobby, then load menu.scene locally (no networked ChangeScene — this client is leaving).
	/// Used by the in-game pause menu's "Main Menu".</summary>
	public static void ExitToMenu()
	{
		if ( Networking.IsActive )
			Networking.Disconnect();

		// A deliberate leave: a later direct Play (or fresh menu flow) starts from a clean slate.
		NoteSessionEnded();

		var options = new SceneLoadOptions();
		if ( !options.SetScene( MenuScene ) )
		{
			Log.Warning( $"MenuNetworking: couldn't resolve the menu scene '{MenuScene}'." );
			return;
		}

		// Same call the host handoff uses; with no active lobby (we just disconnected) it loads locally.
		Game.ChangeScene( options );
	}

	/// <summary>The front-end menu scene (the .sbproj StartupScene), loaded when leaving a session.</summary>
	const string MenuScene = "scenes/menu.scene";

	// Host-only scene change into the mode's gameplay scene. Mirrors MiniMotors' LobbyFlow.Launch.
	static void LoadGameplayScene( GameModeInfo info )
	{
		if ( !Networking.IsHost )
			return;

		var options = new SceneLoadOptions();
		if ( !options.SetScene( info.Scene ) )
		{
			Log.Warning( $"MenuNetworking: couldn't resolve scene '{info.Scene}' for {info.Label}." );
			return;
		}

		// ChangeScene loads the scene on the host and broadcasts the load to every client.
		Game.ChangeScene( options );
	}
}

/// <summary>
/// Clears <see cref="MenuNetworking.EverInSession"/> when the play session itself ends. Statics survive the
/// editor's Stop→Play (and hotloads) — only <see cref="MenuNetworking.ExitToMenu"/> or an editor restart cleared
/// the flag before — so a direct Play after ANY earlier session in the same editor run found EverInSession still
/// true, refused to self-host, and the lobby came up dead (no session, no setup HUD, G did nothing).
///
/// Why this hook is the right one: GameObjectSystems persist across in-session scene changes (Scene.Load keeps
/// them; only Scene.Destroy shuts them down), and Game.IsClosing is true during that destroy exactly when play is
/// stopping (editor Stop / app close) — never during Game.ChangeScene. So Dispose+IsClosing fires once, precisely
/// at "the play session is over", which is the moment the session intent stops being true.
/// </summary>
public sealed class SessionResetSystem : GameObjectSystem
{
	public SessionResetSystem( Scene scene ) : base( scene ) { }

	public override void Dispose()
	{
		if ( Game.IsClosing )
			MenuNetworking.NoteSessionEnded();

		base.Dispose();
	}
}
