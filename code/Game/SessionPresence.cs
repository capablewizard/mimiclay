using System;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Feeds the engine's multiplayer tab the one slot we control for "what is this session playing": the lobby's
/// <c>map</c> key. The host socket re-stamps that key EVERY tick from <c>Networking.MapName</c>, which the engine
/// derives from the active scene's name (a MapInstance's map name first, else <c>Scene.Name</c> — the scene file's
/// resource name, so "kitchenlobby" / "charadeszoo_art" out of the box). Our own lobby data (<c>mode</c>, <c>r.map</c>)
/// is carried but never displayed by the engine UI, so writing the key directly is pointless: the engine
/// overwrites it a tick later. The lever is the runtime scene name itself — Scene is a GameObject and Name is
/// settable — so this system keeps it set to a display string on the host:
///
///   lobby   → "Lobby · Prop Hunt"        (what the host has selected, live as they change it)
///   playing → "Prop Hunt · Kitchen"      (mode + map title from the launch courier keys)
///
/// Host only (clients' scene names go nowhere), never in the editor (the editor scene is the asset), never in
/// thumbnail render scenes (every ScenePanel runs its own system instances — see the GameObjectSystem notes).
/// </summary>
public sealed class SessionPresence : GameObjectSystem
{
	const string Separator = " · ";

	string _applied;

	public SessionPresence( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 20, Tick, "SessionPresence" );
	}

	void Tick()
	{
		if ( Scene is null || Scene.IsEditor || SdfThumbnail.IsThumbnailScene( Scene ) )
			return;

		if ( !Networking.IsActive || !Networking.IsHost )
			return;

		var name = Describe();
		if ( name is null || name == _applied )
			return;

		_applied = name;
		Scene.Name = name; // Networking.MapName follows on the engine's next host tick
	}

	/// <summary>The display string for this scene, or null when there's nothing better than the file name.</summary>
	string Describe()
	{
		// In the lobby: the game the host is currently set up for.
		if ( LobbyManager.Current.IsValid() )
			return "Lobby" + Separator + GameModes.Get( LobbyManager.Current.SelectedGame ).Label;

		// In a game scene: the mode the lobby launched, and the map it resolved to (both ride session data).
		string mode = Enum.TryParse<GameModeKind>( Networking.GetData( MenuNetworking.Keys.Mode ), out var kind )
			? GameModes.Get( kind ).Label
			: null;

		string map = MapCatalog.TryGet( RoundSettings.LaunchedMapIdent, out var picked )
			? picked.Title
			: MapCatalog.All.FirstOrDefault( m => m.Scene is not null && m.Scene.ResourcePath == Scene.Source?.ResourcePath )?.Title;

		if ( mode is null && map is null )
			return null;

		return mode is null ? map : map is null ? mode : mode + Separator + map;
	}
}
