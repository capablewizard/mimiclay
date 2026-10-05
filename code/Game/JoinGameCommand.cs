using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// <c>mimi_join</c>: the platform game page's "Join Game" button, from the console. Mirrors the engine's
/// <c>MenuUtility.TryJoinLobby</c> — query this game's lobbies, most populated first, skip full ones, try each
/// until one connects. Differences: it runs in-game (so it skips our OWN self-hosted lobby), and it can't open
/// the engine's internal matchmaking scope, so a lobby that exists but refuses us closes the game to the menu
/// with a "Disconnected" notice instead of quietly moving on to the next one.
///
/// Joining drops the session we're hosting first (<c>TryConnectSteamId</c> disconnects) — anyone in it is kicked.
/// </summary>
public static class JoinGameCommand
{
	/// <summary>True while a join is mid-connect. We're session-less but <see cref="MenuNetworking.EverInSession"/>
	/// is still set from self-hosting, which <see cref="DeadSessionWatchdog"/> would read as a dead session and
	/// "rescue" us back into a fresh self-hosted lobby, wrecking the join.</summary>
	public static bool InProgress { get; private set; }

	[ConCmd( "mimi_join" )]
	public static async void Join()
	{
		if ( InProgress )
		{
			Log.Info( "[join] Already searching." );
			return;
		}

		InProgress = true;
		try
		{
			var me = (ulong)(Connection.Local?.SteamId ?? default);

			Log.Info( $"[join] Searching for {Game.Ident} games.." );
			var lobbies = await Networking.QueryLobbies();
			var candidates = lobbies
				.Where( l => !l.IsFull && l.OwnerId != me )
				.OrderByDescending( l => l.Members )
				.ToList();
			Log.Info( $"[join] ..found {lobbies.Count} lobbies, {candidates.Count} joinable" );

			foreach ( var lobby in candidates )
			{
				Log.Info( $"[join] Attempting {lobby.LobbyId} '{lobby.Name}' ({lobby.Members}/{lobby.MaxMembers})" );
				if ( await Networking.TryConnectSteamId( lobby.LobbyId ) )
					return;
			}

			Log.Info( "[join] Couldn't join any available games." );
		}
		finally
		{
			InProgress = false;
		}
	}
}
