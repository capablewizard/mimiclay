using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// The lobby scene's furniture + bootstrap: pawn prefabs, spawn points, the host's inspector-tunable round
/// defaults, the self-host fallback for direct Play, and the local debug keys. On the host it NetworkSpawns the
/// <see cref="LobbyManager"/> — the actual networked lobby state — exactly the way <see cref="RoundManagerSpawner"/>
/// spawns <see cref="RoundManager"/>, and for the same reason: a scene-placed component's <c>[Sync]</c> CHANGES
/// don't replicate here, but a NetworkSpawn'd object's do (incl. to late-joiners via the spawn snapshot).
///
/// The lobby loop: players spawn as a hunter to edit their face — or switch to a prop to practise a disguise —
/// nominate themselves to hunt, and the host configures the round (map + phase times) and starts it. Starting
/// writes the choices into session data and changes scene into the chosen map, where <see cref="RoundManager"/>
/// takes over; after the round the manager changes scene back here and the loop repeats.
/// </summary>
[Title( "Lobby Controller" )]
[Category( "Mimiclay" )]
[Icon( "groups" )]
public sealed class LobbyController : Component
{
	public static LobbyController Current { get; private set; }

	/// <summary>The lobby scene, loaded on hosting a Prop Hunt session and returned to after each round.</summary>
	public const string LobbyScene = "scenes/lobby.scene";

	/// <summary>Lobby cap when we self-host (pressing Play directly on this scene).</summary>
	public const int MaxLobbyPlayers = 8;

	[Property, Group( "Prefabs" )] public GameObject HunterPrefab { get; set; }
	[Property, Group( "Prefabs" )] public GameObject PropPrefab { get; set; }

	/// <summary>Ordered lobby spawn spots: slot i spawns at point i, wrapping with a ring offset once the points run
	/// out. Everyone spawning on ONE point at the same instant overlaps hulls, and the physics solver shoves the
	/// pile apart — sometimes through the floor — so give the lobby at least as many points as players.</summary>
	[Property] public List<GameObject> SpawnPoints { get; set; } = new();

	/// <summary>Legacy single spawn spot, used only when <see cref="SpawnPoints"/> is empty (falls back to this
	/// GameObject). Kept so existing scenes still work; simultaneous spawns ring around it rather than stack.</summary>
	[Property] public GameObject SpawnPoint { get; set; }

	// ── Round defaults (tune in the inspector) ──────────────────────────────────────────────────────────────
	// What a fresh lobby starts with, before the host adjusts them in the setup screen. Exposed here so they can be
	// tuned per lobby scene without touching code — they seed LobbyManager.Settings on the host.
	[Property, Group( "Round Defaults" )] public RoundMode DefaultRoundMode { get; set; } = RoundMode.Infection;
	[Property, Group( "Round Defaults" )] public float DefaultHideSeconds { get; set; } = 45f;
	[Property, Group( "Round Defaults" )] public float DefaultHuntSeconds { get; set; } = 180f;
	[Property, Group( "Round Defaults" )] public float DefaultRevealSeconds { get; set; } = 6f;
	[Property, Group( "Round Defaults" )] public float DefaultConsolidationSeconds { get; set; } = 12f;
	[Property, Group( "Round Defaults" )] public float DefaultStartCountdownSeconds { get; set; } = 4f;
	[Property, Group( "Round Defaults" )] public float DefaultTauntSeconds { get; set; } = 15f;
	[Property, Group( "Round Defaults" )] public int DefaultHunterCount { get; set; } = 1;

	/// <summary>Seconds between hitting Start and the scene change, so everyone sees the launch coming.</summary>
	[Property, Group( "Round Defaults" )] public float LaunchCountdownSeconds { get; set; } = 10f;

	/// <summary>The starting settings built from the inspector defaults above. Read by LobbyManager on the host.</summary>
	public RoundSettings DefaultSettings => new()
	{
		Mode = DefaultRoundMode,
		StartCountdownSeconds = DefaultStartCountdownSeconds,
		HideSeconds = DefaultHideSeconds,
		HuntSeconds = DefaultHuntSeconds,
		RevealSeconds = DefaultRevealSeconds,
		ConsolidationSeconds = DefaultConsolidationSeconds,
		TauntSeconds = DefaultTauntSeconds,
		HunterCount = DefaultHunterCount,
		MapIdent = MapCatalog.RandomIdent,
	};

	// Latched once the manager exists (or we know we're a client receiving it) — same pattern as
	// RoundManagerSpawner._done.
	bool _managerDone;

	protected override void OnEnabled() => Current = this;

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
	}

	protected override void OnStart()
	{
		// Launched directly (you pressed Play on lobby.scene) there's no session yet, so Connection.All is empty and
		// nothing would spawn. Self-host a single-player lobby — same trick DebugGameMode used — so the loop is
		// testable straight from the scene. A client returning map→lobby behind the host is ALSO briefly !IsActive
		// while it reconnects; that must never self-host (it'd fork into a private parallel lobby), so gate on
		// session intent, not on timing.
		if ( !Networking.IsActive && !MenuNetworking.EverInSession )
		{
			Networking.CreateLobby( new LobbyConfig { MaxPlayers = MaxLobbyPlayers } );
			MenuNetworking.NoteSessionStarted();
		}

		// Spawn the host's setup panel (programmatically, like the pause/edit HUDs — clients don't get one).
		// Checked against the LIVE session (not "host authority", which reads true on a still-reconnecting client
		// and would hand a mere client the host's setup panel).
		if ( Networking.IsActive && Networking.IsHost )
			EnsureSetupHud();
	}

	protected override void OnUpdate()
	{
		EnsureManager();
		HandleDebugInput();
	}

	// On the host, create the networked LobbyManager; a client just receives it over the wire and is done. A
	// still-reconnecting client (no session yet) simply waits — EverInSession guarantees OnStart didn't self-host.
	void EnsureManager()
	{
		if ( _managerDone )
			return;

		if ( !Networking.IsActive )
			return;

		if ( !Networking.IsHost )
		{
			_managerDone = true;
			return;
		}

		if ( !LobbyManager.Current.IsValid() )
		{
			var go = new GameObject( true, "Lobby Manager" );
			go.Components.Create<LobbyManager>();
			go.NetworkSpawn(); // host owns it; replicates to every client (and late-joiners) with working [Sync]
		}

		_managerDone = true;
	}

	// Spawn the host's setup UI on its own ScreenPanel (ZIndex between the readout HUD's 100 and the pause menu's
	// 1000). Idempotent — adopt an existing one if the scene already has it.
	void EnsureSetupHud()
	{
		if ( Scene.GetAllComponents<RoundSetup>().Any() )
			return;

		var go = new GameObject( true, "Round Setup HUD" );
		var screen = go.Components.Create<Sandbox.ScreenPanel>();
		screen.ZIndex = 500;
		go.Components.Create<RoundSetup>();
	}

	// Temporary keyboard driving for the lobby until the full UI lands. Raw keys so they need no input-config
	// actions. Runs on any machine; the requests are [Rpc.Host] on the manager, so a client's press routes to the
	// host. The host's G (open the setup panel) is handled by RoundSetup itself. TODO: replace P/N with lobby UI
	// controls.
	//   P = swap role (hunter ↔ prop)   N = toggle hunt nomination
	void HandleDebugInput()
	{
		var lm = LobbyManager.Current;
		if ( !lm.IsValid() || !Networking.IsActive || RoundSetup.IsOpen )
			return;

		if ( Input.Keyboard.Pressed( "P" ) ) lm.RequestSwapRole();
		if ( Input.Keyboard.Pressed( "N" ) ) lm.ToggleNominate();
	}

	// The spot for a slot: point slot%N (SpawnPoints, else the legacy single SpawnPoint, else this GameObject),
	// ringed outward via StackOffset once the points run out — same de-stack rule as RoundManager.PickSpot, so
	// simultaneous spawns never overlap hulls no matter how few points the scene has.
	public Transform SpotAt( int slot )
	{
		var points = SpawnPoints?.Where( p => p.IsValid() ).ToList();
		if ( points is null || points.Count == 0 )
			points = new List<GameObject> { SpawnPoint.IsValid() ? SpawnPoint : GameObject };

		var origin = points[slot % points.Count];
		var stack = slot / points.Count;
		return new Transform(
			origin.WorldPosition + RoundSpawnPoint.StackOffset( stack ) + Vector3.Up * 64f,
			Rotation.FromYaw( origin.WorldRotation.Yaw() ) );
	}
}
