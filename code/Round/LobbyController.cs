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
	public const string LobbyScene = "scenes/kitchenlobby.scene";

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
	[Property, Group( "Round Defaults" )] public float DefaultHintSeconds { get; set; } = RoundSettings.DefaultHintSeconds;
	[Property, Group( "Round Defaults" )] public int DefaultHunterCount { get; set; } = 1;

	/// <summary>Seconds between hitting Start and the scene change, so everyone sees the launch coming.</summary>
	[Property, Group( "Round Defaults" )] public float LaunchCountdownSeconds { get; set; } = 10f;

	// ── Prop editing ──────────────────────────────────────────────────────────────────────────────────────
	/// <summary>Lobby scene props are editable exactly like creative's — aim, "E to Edit", possess. This is
	/// how close a hunter must be for clay to outline and offer the prompt, measured eye-to-surface; it
	/// authors <see cref="PropClaims.HoverRange"/> the way RoundManagerSpawner's CreativeHoverRange does for
	/// creative maps.</summary>
	[Property, Group( "Prop Editing" ), Range( 64f, 4096f )] public float PropHoverRange { get; set; } = 300f;

	// ── Test bots ─────────────────────────────────────────────────────────────────────────────────────────
	// Fake players so a solo editor session can see a full lobby: roster pips, names, a hunter count worth
	// sliding, nominations. They're the same bots the round uses (RoundBots), just seated by LobbyManager
	// instead — which reads these LIVE, so changing the count while playing adds or removes them on the spot.
	/// <summary>How many stand-in players to seat in the lobby. They spawn as hunters like everyone else, hold
	/// roster slots, and count toward the hunter-count limit. 0 = off.</summary>
	[Property, Group( "Test Bots" )] public int BotCount { get; set; }

	/// <summary>How many of those bots nominate themselves to hunt. Nominations carry into the round the same way
	/// a real player's do, so this is how you set up "these three are hunting" without three machines. Clamped to
	/// <see cref="BotCount"/>.</summary>
	[Property, Group( "Test Bots" )] public int BotNominations { get; set; }

	/// <summary>Give lobby bots random saved shapes from this machine's sculpt library instead of identical default
	/// heads — useful for telling roster pips apart at a glance. Off by default because saved sculpts are usually
	/// PROPS, so expect bots wearing a sofa for a head; it's for checking the UI, not for looking right.</summary>
	[Property, Group( "Test Bots" )] public bool BotRandomLooks { get; set; }

	/// <summary>Send this lobby's bot count into the map at launch, so the crowd you set up here is the crowd you
	/// play with. Off = the map's own <see cref="RoundManagerSpawner"/> decides instead (its bot count is the
	/// direct-Play path, for opening a map scene without going through a lobby).</summary>
	[Property, Group( "Test Bots" )] public bool BotsFollowIntoRound { get; set; } = true;

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
		HintSeconds = DefaultHintSeconds,
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

			// Session data survives the editor's Stop→Play (engine static), so an earlier launch's round keys
			// are still lying around. Here every key is rewritten at launch anyway, so this is hygiene — the
			// clear that MATTERS is RoundManagerSpawner's, where stale keys were read as real lobby intent.
			RoundSettings.ClearLobbyData();
			CreativeSettings.ClearLobbyData();
			CharadesSettings.ClearLobbyData();
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
			// Lobby props are editable via the same claim service creative uses; the manager is its
			// IPropClaimHost. Same GameObject so one NetworkSpawn ships both, and the reach is set BEFORE
			// the spawn so the snapshot ships the authored value to every client's hover.
			var claims = go.Components.Create<PropClaims>();
			claims.HoverRange = PropHoverRange;
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
		go.Flags |= GameObjectFlags.NotSaved; // runtime-only: never let this end up serialised into an asset
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

		// Mid-tutorial both keys stand down: P would swap the pawn out from under the guided session (a
		// forced exit mid-lesson), and a nomination toggle is invisible noise behind the card.
		if ( TutorialDirector.IsRunning )
			return;

		// The swap keeps the CAMERA continuous: whatever the view was pointing at, the new pawn's view points
		// at too. The yaw rides the request (the host aims the new HUNTER's spawn rotation with it — a prop's
		// body ignores it and keeps its remembered facing instead); the pitch and the prop-orbit zoom are
		// owner-only state the host never sees, so they ride LobbySwapCarry and our own replacement pawn
		// applies them as it starts.
		if ( Input.Keyboard.Pressed( "P" ) )
		{
			LobbySwapCarry.Capture( Scene, OwnProp() );
			lm.RequestSwapRole( Scene.Camera.IsValid() ? Scene.Camera.WorldRotation.Angles().yaw : 0f );
		}
		if ( Input.Keyboard.Pressed( "N" ) ) lm.ToggleNominate();
	}

	// Our own hider pawn, if we're currently a prop — RosterIdOf rather than a bare IsProxy so the host's bot
	// pawns can never answer for the host's own.
	HiderController OwnProp()
	{
		if ( Connection.Local?.Id is not { } id )
			return null;

		foreach ( var p in Scene.GetAllComponents<HiderController>() )
			if ( p.IsValid() && RoundManager.RosterIdOf( p.GameObject ) == id )
				return p;

		return null;
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
