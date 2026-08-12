using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Drop this in the pictionary scene. On the HOST it creates the real, networked
/// <see cref="PictionaryManager"/> and steps aside; clients just receive that manager over the wire. Same
/// spawner-not-scene-placed reasoning as <see cref="RoundManagerSpawner"/>: a scene component's [Sync]
/// CHANGES don't replicate, a NetworkSpawn'd object's do (late joiners included, via the spawn snapshot).
///
/// Also the scene's config rack: the pawn/canvas prefabs are read LIVE off this scene-placed component by
/// every machine (the NetworkSpawn'd manager's own [Property] refs wouldn't exist on clients), and the rules
/// are authored here and copied onto the manager at spawn.
/// </summary>
[Title( "Pictionary Spawner" )]
[Category( "Mimiclay" )]
[Icon( "draw" )]
public sealed class PictionarySpawner : Component
{
	/// <summary>The scene's spawner — scene-placed, so it (and its prefab refs) exists on every machine.</summary>
	public static PictionarySpawner Current { get; private set; }

	/// <summary>Everyone's pawn — the hunter, gun and all: shooting the stage is allowed (the canvas heals).</summary>
	[Property, Group( "Prefabs" )] public GameObject PawnPrefab { get; set; }

	/// <summary>The per-turn canvas the sculptor's machine spawns + owns on the stage.</summary>
	[Property, Group( "Prefabs" )] public GameObject CanvasPrefab { get; set; }

	/// <summary>Where the canvas appears — a marker on top of the stage.</summary>
	[Property, Group( "Stage" )] public GameObject CanvasSpot { get; set; }

	/// <summary>The lobby scene a lobby-launched game returns to after the podium. A real SceneFile REFERENCE,
	/// not a path string, for the same reason as <see cref="RoundManagerSpawner.LobbyScene"/>: string lookups
	/// have returned null mid-session; a reference deserializes with the scene and is dependency-tracked.</summary>
	[Property, Group( "Scenes" )] public SceneFile LobbyScene { get; set; }

	// ── Rules (authored per scene, copied onto the manager at spawn) ──────────────────────────────────────
	[Property, Group( "Rules" )] public float ChooseSeconds { get; set; } = PictionarySettings.DefaultChooseSeconds;
	[Property, Group( "Rules" )] public float SculptSeconds { get; set; } = PictionarySettings.DefaultSculptSeconds;
	[Property, Group( "Rules" )] public float RevealSeconds { get; set; } = PictionarySettings.DefaultRevealSeconds;
	[Property, Group( "Rules" )] public float PodiumSeconds { get; set; } = PictionarySettings.DefaultPodiumSeconds;

	/// <summary>Full cycles per game — everyone sculpts once per cycle.</summary>
	[Property, Group( "Rules" )] public int Rounds { get; set; } = PictionarySettings.DefaultRounds;

	/// <summary>Players needed before a game starts.</summary>
	[Property, Group( "Rules" )] public int MinPlayers { get; set; } = PictionarySettings.DefaultMinPlayers;

	/// <summary>DEBUG: let a game start alone (MinPlayers 1) so the whole loop — choose, sculpt, reveal —
	/// can be walked solo from the editor. Turns never end early (you have no guessers), only by timer.</summary>
	[Property, Group( "Debug" )] public bool DebugAllowSolo { get; set; }

	/// <summary>The authored rules as the manager wants them.</summary>
	public PictionarySettings BuildSettings() => new()
	{
		ChooseSeconds = ChooseSeconds,
		SculptSeconds = SculptSeconds,
		RevealSeconds = RevealSeconds,
		PodiumSeconds = PodiumSeconds,
		Rounds = Rounds,
		MinPlayers = DebugAllowSolo ? 1 : MinPlayers,
	};

	protected override void OnEnabled() => Current = this;

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
	}

	bool _done;

	protected override void OnUpdate()
	{
		if ( _done )
			return;

		// No session yet? A genuine direct Play (or a menu Host arriving before its lobby settles) self-hosts;
		// a client briefly !IsActive while following the host must NOT — same intent-not-timing rule as every
		// other scene bootstrap (see RoundManagerSpawner).
		if ( !Networking.IsActive )
		{
			if ( MenuNetworking.EverInSession )
				return;

			Networking.CreateLobby( new LobbyConfig { MaxPlayers = MenuNetworking.DefaultMaxPlayers } );
			MenuNetworking.NoteSessionStarted();

			// Session data survives the editor's Stop→Play, so a lobby-launched pictionary game earlier in
			// this editor run left its courier keys behind — including the came-from-lobby flag, which would
			// make THIS direct Play try to "return" to a lobby it never came from. Blank slate.
			PictionarySettings.ClearLobbyData();
		}

		if ( !Networking.IsHost )
		{
			_done = true;
			return;
		}

		if ( !PictionaryManager.Current.IsValid() )
		{
			var go = new GameObject( true, "Pictionary Manager" );
			go.Components.Create<PictionaryManager>();
			go.NetworkSpawn(); // host owns it; replicates (incl. late joiners) with working [Sync]
		}

		_done = true;
	}
}
