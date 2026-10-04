using System;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Session intent + the one way OUT of a session. Hosting, finding and joining games is the engine's job now:
/// the s&amp;box game page collects server name, privacy and slot count before launch (they arrive through
/// <c>LaunchArguments</c>, which <c>Networking.CreateLobby</c> applies over whatever config we pass), the
/// escape menu's multiplayer tab lists and joins sessions, and Steam invites / Join Game connect straight in.
/// The game boots into the lobby scene (the .sbproj StartupScene), where <see cref="LobbyController"/> self-hosts.
///
/// What remains here: the "we've deliberately been in a session" flag the scene bootstraps read, and
/// <see cref="LeaveSession"/>, which drops the current session and lands the player back in their OWN lobby.
/// The lobby data keys stay the browser-facing contract; keep them in sync with <see cref="Keys"/>.
/// </summary>
public static class MenuNetworking
{
	/// <summary>Lobby data keys (the networked contract). Short strings — Steam lobby data is limited.</summary>
	public static class Keys
	{
		public const string Mode = "mode";       // GameModeKind name
	}

	/// <summary>True once this process has deliberately been in a session (hosted or joined). Gameplay-scene
	/// bootstraps (<see cref="RoundManagerSpawner"/>, <see cref="LobbyController"/>) read this to tell a genuine
	/// direct Play (never in a session — safe to self-host) from a client that's briefly !IsActive while following
	/// the host's scene change (must NOT self-host — that would fork it into a private parallel session it can
	/// never leave except by leaving the game). Intent, not timing: the old grace-window approach forked any client
	/// whose reconnect outlasted the window. Reset by <see cref="NoteSessionEnded"/> — <see cref="LeaveSession"/>'s
	/// deliberate leave, and <see cref="SessionResetSystem"/> when play itself stops.</summary>
	public static bool EverInSession { get; private set; }

	/// <summary>Record that this process deliberately entered a session — see <see cref="EverInSession"/>. Called
	/// by the scene bootstraps when they legitimately self-host.</summary>
	public static void NoteSessionStarted() => EverInSession = true;

	/// <summary>Forget the session intent — the session is over, so a later lobby load is safe to self-host
	/// again. Called by <see cref="LeaveSession"/> and by <see cref="SessionResetSystem"/> at play teardown.</summary>
	public static void NoteSessionEnded() => EverInSession = false;

	/// <summary>Last user-facing session-loss message (e.g. "Lost connection to the host."), or null. Set by
	/// <see cref="NotifyDisconnected"/> right before the leave, so the lobby we land in can explain why.</summary>
	public static string LastError { get; private set; }

	/// <summary>Seconds since <see cref="LastError"/> was set.</summary>
	public static float ErrorAge => _errorSince;
	static RealTimeSince _errorSince;

	/// <summary>Clear the current error.</summary>
	public static void ClearError() => LastError = null;

	/// <summary>Surface a session-loss message. <see cref="DeadSessionWatchdog"/> calls this right before
	/// <see cref="LeaveSession"/>, so the player lands in their lobby with an explanation, not a silent kick.</summary>
	public static void NotifyDisconnected( string message )
	{
		LastError = message;
		_errorSince = 0;
		Log.Warning( $"MenuNetworking: {message}" );
	}

	/// <summary>Leave the current session and land in your OWN lobby: drop the lobby, then load the lobby scene
	/// locally (no networked ChangeScene — this client is leaving). <see cref="LobbyController"/> sees no session
	/// and no session intent, and self-hosts a fresh one — with the engine's pre-launch choices (name, privacy,
	/// slots) still applied, since LaunchArguments live for the whole game run. Used by the pause menu's "Leave
	/// Game", the dead-session watchdog, and the host's "please leave" request.</summary>
	public static void LeaveSession()
	{
		if ( Networking.IsActive )
			Networking.Disconnect();

		// A deliberate leave: the lobby we load next starts from a clean slate and may self-host.
		NoteSessionEnded();

		var options = new SceneLoadOptions();
		if ( !options.SetScene( LobbyController.LobbyScene ) )
		{
			Log.Warning( $"MenuNetworking: couldn't resolve the lobby scene '{LobbyController.LobbyScene}'." );
			return;
		}

		// With no active lobby (we just disconnected) ChangeScene loads locally.
		Game.ChangeScene( options );
	}
}

/// <summary>
/// Clears <see cref="MenuNetworking.EverInSession"/> when the play session itself ends. Statics survive the
/// editor's Stop→Play (and hotloads) — only <see cref="MenuNetworking.LeaveSession"/> or an editor restart cleared
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
		{
			MenuNetworking.NoteSessionEnded();
			LobbySplash.ResetRun(); // the next lobby load is a fresh launch again
			SculptBounds.ResetBypass(); // the dev size-limit bypass never outlives the play session
			TutorialNpc.SweepPlayEnd(); // restore the tutorial character's shape + drop runtime outlines, however teardown fell out
			SculptSceneLibrary.NotePlayEnded(); // next play session autosaves into its OWN folder
			Interactions.Reset(); // registered sources + the local hover are statics
			PawnSwapKeys.Reset(); // a swap parked on the revert dialog
		}

		base.Dispose();
	}
}
