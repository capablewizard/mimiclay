using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Drop this in each map scene. On the HOST it creates the real, networked <see cref="RoundManager"/> and steps
/// aside; clients just receive that manager over the wire.
///
/// Why a spawner instead of placing RoundManager directly: a scene-placed component's <c>[Sync]</c> CHANGES don't
/// replicate to clients here, but a <see cref="GameObject.NetworkSpawn()"/>'d object (NetworkMode.Object) syncs
/// properly — incl. to late-joiners via the spawn snapshot. You can't NetworkSpawn a scene object (it'd duplicate
/// on clients), so the manager has to be created in code. This mirrors how MiniMotors spawns its networked
/// singletons (LobbyFlow etc.). This spawner holds the per-map config the manager needs.
/// </summary>
[Title( "Round Manager Spawner" )]
[Category( "Mimiclay" )]
[Icon( "sports_esports" )]
public sealed class RoundManagerSpawner : Component
{
	/// <summary>The scene's spawner. Scene-placed, so it (and its prefab refs) exists on EVERY machine — unlike the
	/// NetworkSpawn'd RoundManager, whose [Property] prefabs only exist on the host. Every machine now spawns its OWN
	/// pawn, so it reads the prefabs from here. Set while enabled.</summary>
	public static RoundManagerSpawner Current { get; private set; }

	[Property, Group( "Prefabs" )] public GameObject HunterPrefab { get; set; }
	[Property, Group( "Prefabs" )] public GameObject PropPrefab { get; set; }

	/// <summary>One-shot smoke burst played where a prop is found, covering the swap to a hunter pawn (the
	/// "substitution" poof). Cloned locally on every machine by <see cref="RoundManager.PlayCaughtPuff"/>.</summary>
	[Property, Group( "Prefabs" )] public GameObject CaughtPuffPrefab { get; set; }

	/// <summary>The lobby scene the round returns to after consolidation. A real SceneFile REFERENCE, not a
	/// runtime path string: `SceneFile.Load("scenes/lobby.scene")` (a ResourceLibrary lookup by path) has proven
	/// unreliable mid-session — it resolved fine from the menu scene, then returned null from inside a map in the
	/// same session, leaving the round stuck spamming "couldn't resolve lobby scene". A reference deserializes
	/// with this scene and is dependency-tracked, the same way the map launch resolves its phmap's Scene (which
	/// has never failed).</summary>
	[Property, Group( "Scenes" )] public SceneFile LobbyScene { get; set; }

	[Property, Group( "Scoring" )] public int FindReward { get; set; } = 50;
	[Property, Group( "Scoring" )] public float PropPointsPerSecond { get; set; } = 1f;

	/// <summary>DEBUG: spawn everyone as a prop with an endless Hide phase (no hunters, no progression) — see
	/// <see cref="RoundManager.DebugSoloHide"/>. Tick for solo disguise testing; leave off for real play.</summary>
	[Property, Group( "Debug" )] public bool DebugSoloHide { get; set; }

	// ── Test bots ─────────────────────────────────────────────────────────────────────────────────────────
	// Everything needed to play a full round on your own: seat some bots, say how many of the seats hunt, pick
	// your own side, and set the clock. All of it is copied onto the RoundManager below (it's never scene-placed,
	// so it has no inspector of its own) and read once at round start — editing these mid-round does nothing.
	/// <summary>How many stand-in players to seat alongside the real connections. They hold roster slots, count
	/// toward the win checks, and (as props) get a real body you can hunt and shoot — they just never move.
	/// 0 = off, and nothing about the round changes.</summary>
	[Property, Group( "Test Bots" )] public int BotCount { get; set; }

	/// <summary>Give bot HUNTERS a body on the map as well. On by default: a bot hunter can't actually hunt, but
	/// its body is what a found bot prop CONVERTS INTO — turn this off and shooting a bot just pops it, leaving
	/// nothing behind to look at, and nothing for the HUD's hunter portraits to read a face off. Bot props always
	/// get a body.</summary>
	[Property, Group( "Test Bots" )] public bool BotHunterPawns { get; set; } = true;

	/// <summary>Dress bot props in random saved shapes from this machine's sculpt library, so the map fills with
	/// varied silhouettes instead of identical default blobs. Nothing saved yet = everyone keeps the default.</summary>
	[Property, Group( "Test Bots" )] public bool BotRandomDisguises { get; set; } = true;

	/// <summary>Which side YOU start on, so you can test either seat without re-rolling until the dice agree.
	/// Auto = normal play (lobby nominations, then chance).</summary>
	[Property, Group( "Test Bots" )] public PlayAsChoice PlayAs { get; set; } = PlayAsChoice.Auto;

	// ── Round rules override ──────────────────────────────────────────────────────────────────────────────
	// The rules normally ride in from the lobby as session data (RoundSettings.ReadFromLobby). Ticking the
	// override below replaces them wholesale, so a map opened straight from the editor — no menu, no lobby — can
	// run 20-second phases while you test the loop.
	/// <summary>Use the rules authored below instead of whatever the lobby sent. Leave OFF for real play, or the
	/// host's lobby choices are silently ignored on this map.</summary>
	[Property, Group( "Round Rules (Override)" )] public bool OverrideRules { get; set; }

	// Seeded from RoundSettings' DEFAULT CONSTS, never from RoundSettings.Default itself: s&box bakes a
	// [Property]'s initializer into a generated attribute, and an attribute argument has to be a constant
	// expression — a property read doesn't compile there.
	[Property, Group( "Round Rules (Override)" )] public RoundMode Mode { get; set; } = RoundSettings.DefaultMode;

	/// <summary>How many of the seated players hunt (the rest are props). Clamped to leave at least one prop.</summary>
	[Property, Group( "Round Rules (Override)" )] public int HunterCount { get; set; } = RoundSettings.DefaultHunterCount;

	/// <summary>Frozen "get ready" countdown after the map loads.</summary>
	[Property, Group( "Round Rules (Override)" )] public float StartCountdownSeconds { get; set; } = RoundSettings.DefaultStartCountdownSeconds;

	/// <summary>Sculpting + hiding time before the hunt opens.</summary>
	[Property, Group( "Round Rules (Override)" )] public float HideSeconds { get; set; } = RoundSettings.DefaultHideSeconds;

	/// <summary>The hunt itself — the round's main clock.</summary>
	[Property, Group( "Round Rules (Override)" )] public float HuntSeconds { get; set; } = RoundSettings.DefaultHuntSeconds;

	/// <summary>How long the surviving props are flashed on the map.</summary>
	[Property, Group( "Round Rules (Override)" )] public float RevealSeconds { get; set; } = RoundSettings.DefaultRevealSeconds;

	/// <summary>The results/gallery beat before the round returns to the lobby.</summary>
	[Property, Group( "Round Rules (Override)" )] public float ConsolidationSeconds { get; set; } = RoundSettings.DefaultConsolidationSeconds;

	/// <summary>How often each surviving prop auto-taunts during the hunt.</summary>
	[Property, Group( "Round Rules (Override)" )] public float TauntSeconds { get; set; } = RoundSettings.DefaultTauntSeconds;

	// The authored rules as the manager wants them. MapIdent is left at the default: the map is already loaded by
	// the time anyone reads this — nothing downstream re-resolves it — so there's nothing here to choose.
	RoundSettings AuthoredRules => new()
	{
		Mode = Mode,
		StartCountdownSeconds = StartCountdownSeconds,
		HideSeconds = HideSeconds,
		HuntSeconds = HuntSeconds,
		RevealSeconds = RevealSeconds,
		ConsolidationSeconds = ConsolidationSeconds,
		TauntSeconds = TauntSeconds,
		HunterCount = HunterCount,
		MapIdent = RoundSettings.Default.MapIdent,
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

		// No session? Either a genuine direct Play on this map scene, or a client briefly !IsActive while following
		// the host's scene change. Intent (has this process ever been in a session?) tells them apart — timing can't:
		// the old grace window forked any client whose reconnect outlasted it into a private parallel session. A
		// following client now just waits here (staying not-_done) until the session comes back.
		if ( !Networking.IsActive )
		{
			if ( MenuNetworking.EverInSession )
				return;

			Networking.CreateLobby( new LobbyConfig { MaxPlayers = 8 } );
			MenuNetworking.NoteSessionStarted();

			// This session is brand new and OURS, so no lobby data can legitimately exist yet — but
			// Networking.SetData lands in an engine STATIC that survives the editor's Stop→Play, so a lobby
			// launch earlier in this editor run has left its keys behind. Without this clear the manager
			// reads that stale data instead of this spawner's config — most visibly the old lobby's bot
			// count ("0") overriding BotCount above, so the test bots silently never spawn.
			RoundSettings.ClearLobbyData();
		}

		// Only the host creates the networked manager; a real client just receives it over the wire and is done.
		if ( !Networking.IsHost )
		{
			_done = true;
			return;
		}

		if ( !RoundManager.Current.IsValid() )
		{
			var go = new GameObject( true, "Round Manager" );
			var rm = go.Components.Create<RoundManager>();
			// Only host-side scoring config is copied onto the (host-only) manager; the pawn prefabs are read live off
			// this scene-placed spawner by every machine (RoundManager.PrefabFor) — clients' manager copies wouldn't
			// carry [Property] refs.
			rm.FindReward = FindReward;
			rm.PropPointsPerSecond = PropPointsPerSecond;
			rm.DebugSoloHide = DebugSoloHide;

			// Test config — host-only by nature: the host seats the bots, owns their bodies, and decides the rules.
			rm.BotCount = BotCount;
			rm.BotHunterPawns = BotHunterPawns;
			rm.BotRandomDisguises = BotRandomDisguises;
			rm.PlayAs = PlayAs;
			rm.RulesOverride = OverrideRules ? AuthoredRules : null;

			go.NetworkSpawn(); // host owns it; replicates to every client (and late-joiners) with working [Sync]
		}

		_done = true;
	}
}
