namespace Mimiclay;

/// <summary>
/// The host-tunable creative-mode rules, and their courier across the scene change — the same two-lives shape
/// as <see cref="RoundSettings"/>: a <c>[Sync]</c> field on <see cref="LobbyManager"/> while the host configures
/// it in the lobby, then flattened into session lobby data at launch (the scene change destroys the lobby and
/// everything on it) and read back by <see cref="CreativeManager"/> in the map — on EVERY machine, since lobby
/// data replicates to all members and the settings drive machine-local work (the scene-prop sweep).
/// </summary>
public struct CreativeSettings
{
	/// <summary>Keep the map's pre-placed props (decoys, prop-builder clay). Off = every machine deletes all
	/// scene-placed sculptures at start, leaving a blank canvas the players populate themselves.</summary>
	public bool SpawnProps;

	public const bool DefaultSpawnProps = true;

	public static CreativeSettings Default => new()
	{
		SpawnProps = DefaultSpawnProps,
	};

	// Short keys, namespaced like RoundSettings' "r.*" so nothing collides.
	static class Keys
	{
		public const string Props = "c.props";
	}

	/// <summary>Host-only: flatten into session data right before the launch's <see cref="Game.ChangeScene"/>.</summary>
	public readonly void WriteToLobby()
	{
		Networking.SetData( Keys.Props, SpawnProps ? "1" : "0" );
	}

	/// <summary>Read back in the map scene (any machine). Missing/blank keys fall back to <see cref="Default"/>,
	/// so a direct-played map still gets usable values.</summary>
	public static CreativeSettings ReadFromLobby()
	{
		var d = Default;

		var props = Networking.GetData( Keys.Props );
		if ( props == "0" )
			d.SpawnProps = false;
		else if ( props == "1" )
			d.SpawnProps = true;

		return d;
	}

	/// <summary>Blank every key a creative launch writes — same reason as <see cref="RoundSettings.ClearLobbyData"/>:
	/// session data is an engine static that survives the editor's Stop→Play, so a self-hosted direct Play must
	/// clear it or read a stale launch's choices (here: silently deleting a map's props). Called beside the
	/// RoundSettings clear at both self-host bootstraps.</summary>
	public static void ClearLobbyData()
	{
		Networking.SetData( Keys.Props, "" );
	}
}
