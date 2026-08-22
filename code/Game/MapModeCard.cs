namespace Mimiclay;

/// <summary>
/// Per-SCENE game setup. The system scene owns the shared machinery — <see cref="RoundManagerSpawner"/>, HUDs,
/// the camera — so anything authored there applies to every map at once. This card is the per-map half: drop one
/// in a map scene (it's optional) and the spawner reads it on start, letting THIS scene carry its own debug/test
/// setup and per-map tuning without touching the system scene. No card = the defaults authored below.
///
/// Precedence: a real lobby launch always wins. The mode key the lobby stamps into session data decides which
/// game runs, and the rules ride in as session data too — this card's game choice and rule override are only
/// consulted when the map is direct-played from the editor (no lobby). That makes a card left enabled in a
/// shipping map inert in real play; only <see cref="CreativeHoverRange"/> and <see cref="DisableSceneCameras"/>
/// (genuine per-map config, not debug) apply unconditionally.
///
/// To run the throwaway <see cref="DebugGameMode"/> harness instead, don't use this card — place (or enable) a
/// DebugGameMode object in the scene; the spawner sees it and steps aside entirely.
/// </summary>
[Title( "Map Mode Card" )]
[Category( "Mimiclay" )]
[Icon( "tune" )]
public sealed class MapModeCard : Component
{
	/// <summary>What <see cref="CreativeHoverRange"/> is when a scene has no card.</summary>
	public const float DefaultCreativeHoverRange = 300f;

	/// <summary>Creative only: how close a hunter must be for clay to outline and offer "E to Edit" — measured
	/// from the eye to the surface the crosshair lands on. Widen it for open maps where props sit far apart; see
	/// <see cref="PropClaims.HoverRange"/>, which this authors. Applies in real play too (per-map tuning, not
	/// debug).</summary>
	[Property, Group( "Creative" ), Range( 64f, 4096f )] public float CreativeHoverRange { get; set; } = DefaultCreativeHoverRange;

	/// <summary>Turn off any camera authored into the map scene when the game starts — including a full
	/// <see cref="MainCamera"/>+post-processing rig kept there for lighting work in the editor. The system
	/// scene's camera (the one holding <see cref="MainCamera.Current"/>) is the only rig left live, so the
	/// map's tuning camera can never render in play or fight for Current. Applies in real play too (per-map
	/// config, not debug).</summary>
	[Property, Group( "Scene" )] public bool DisableSceneCameras { get; set; } = true;

	/// <summary>Which game runs when this map scene is direct-played from the editor — no menu, no lobby. Only
	/// consulted then: the mode key in session data always wins when a lobby launched us. Covers every current and
	/// future <see cref="GameModeKind"/>.</summary>
	[Property, Group( "Direct Play" )] public GameModeKind DirectPlayGame { get; set; } = GameModeKind.PropHunt;

	/// <summary>DEBUG: spawn everyone as a prop with an endless Hide phase (no hunters, no progression) — see
	/// <see cref="RoundManager.DebugSoloHide"/>. Tick for solo disguise testing; leave off for real play.</summary>
	[Property, Group( "Debug" )] public bool DebugSoloHide { get; set; }

	// ── Test bots ─────────────────────────────────────────────────────────────────────────────────────────
	// Everything needed to play a full round on your own: seat some bots, say how many of the seats hunt, pick
	// your own side, and set the clock. All of it is copied onto the RoundManager by the spawner (it's never
	// scene-placed, so it has no inspector of its own) and read once at round start — editing these mid-round
	// does nothing.
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
	public RoundSettings AuthoredRules => new()
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

	protected override void OnStart()
	{
		if ( !DisableSceneCameras )
			return;

		// Every machine runs this on its own scene copy — the stray camera exists locally everywhere, and
		// disabling a component is per-machine render state, nothing networked.
		//
		// Which rig is the real one: the engine additively loads the system scene AFTER the map scene
		// (Scene.LoadSave → AddSystemScene), so the system camera is the last MainCamera to enable and
		// holds MainCamera.Current by the time OnStart runs. Everything else — a map's full
		// MainCamera+postfx tuning rig included — is an authoring aid: both its components go off so it
		// neither renders nor takes over Current later. If nothing holds Current (a scene played without
		// the system camera), the rigs are left alone rather than killing the only view.
		var keep = MainCamera.Current;

		foreach ( var rig in Scene.GetAllComponents<MainCamera>().ToArray() )
		{
			if ( !keep.IsValid() || rig == keep )
				continue;

			rig.Enabled = false;
			if ( rig.Camera.IsValid() )
				rig.Camera.Enabled = false;

			Log.Info( $"MapModeCard: disabled map camera rig '{rig.GameObject.Name}' — the system scene's camera is the game camera." );
		}

		// Bare cameras with no MainCamera on their object are authoring aids by definition. Only the
		// CameraComponent is disabled (not the GameObject) so a rig that also parents lights or probes
		// keeps working.
		foreach ( var cam in Scene.GetAllComponents<CameraComponent>().ToArray() )
		{
			if ( cam.GameObject.Components.Get<MainCamera>( FindMode.EverythingInSelf ).IsValid() )
				continue;

			cam.Enabled = false;
			Log.Info( $"MapModeCard: disabled scene camera '{cam.GameObject.Name}' — the shared gameplay camera is the only one that renders in play." );
		}
	}
}
