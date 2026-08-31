using System;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Lives in the SYSTEM scene, so every map gets it for free. On the HOST it creates the real, networked manager
/// for whichever GAME the session launched — <see cref="RoundManager"/> for prop hunt, <see cref="CreativeManager"/>
/// for creative, decided by the mode key the lobby stamped into session data — and steps aside; clients just
/// receive that manager over the wire.
///
/// This component is the shared MACHINERY only: prefabs, the lobby scene, scoring. Everything per-map or
/// debug-only (which game a direct play runs, test bots, rule overrides, creative hover range) moved to the
/// scene-placed <see cref="MapModeCard"/>, read here on start — otherwise a debug toggle flipped for one map
/// would flip for all of them. A scene that instead contains an enabled <see cref="DebugGameMode"/> (the
/// throwaway pawn harness) runs THAT: this spawner sees it and does nothing at all.
///
/// Why a spawner instead of placing RoundManager directly: a scene-placed component's <c>[Sync]</c> CHANGES don't
/// replicate to clients here, but a <see cref="GameObject.NetworkSpawn()"/>'d object (NetworkMode.Object) syncs
/// properly — incl. to late-joiners via the spawn snapshot. You can't NetworkSpawn a scene object (it'd duplicate
/// on clients), so the manager has to be created in code. This mirrors how MiniMotors spawns its networked
/// singletons (LobbyFlow etc.).
/// </summary>
[Title( "Round Manager Spawner" )]
[Category( "Mimiclay" )]
[Icon( "sports_esports" )]
public sealed class RoundManagerSpawner : Component
{
	/// <summary>The scene's spawner. Scene-placed (via the system scene), so it (and its prefab refs) exists on
	/// EVERY machine — unlike the NetworkSpawn'd RoundManager, whose [Property] prefabs only exist on the host.
	/// Every machine now spawns its OWN pawn, so it reads the prefabs from here. Set while enabled.</summary>
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

		// A scene with an enabled DebugGameMode runs the throwaway harness instead — it hosts its own lobby and
		// spawns its own pawns, so this spawner steps aside entirely. Presence = policy: enable/disable that
		// object per scene to flip debug mode without touching the system scene. (GetAllComponents skips disabled
		// components, which is exactly the off switch.)
		if ( Scene.GetAllComponents<DebugGameMode>().Any() )
		{
			_done = true;
			return;
		}

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
			// reads that stale data instead of the card's config — most visibly the old lobby's bot
			// count ("0") overriding the card's BotCount, so the test bots silently never spawn.
			RoundSettings.ClearLobbyData();
			CreativeSettings.ClearLobbyData();
			CharadesSettings.ClearLobbyData();
		}

		// Only the host creates the networked manager; a real client just receives it over the wire and is done.
		if ( !Networking.IsHost )
		{
			_done = true;
			return;
		}

		// The scene's optional per-map card. The system scene merges into the same runtime Scene, so this finds a
		// card placed in the map scene proper. No card = the defaults (real prop hunt, no bots, no overrides).
		var card = Scene.GetAllComponents<MapModeCard>().FirstOrDefault();

		// Which game did the lobby launch? The mode key rides session data across the scene change and always
		// wins; a blank key (direct play — ClearLobbyData ran) falls back to the card's choice, so debug setup
		// left in a scene can never hijack a real lobby launch.
		var game = Enum.TryParse<GameModeKind>( Networking.GetData( MenuNetworking.Keys.Mode ), out var launched )
			? launched
			: card.IsValid() ? card.DirectPlayGame : GameModeKind.PropHunt;

		switch ( game )
		{
			case GameModeKind.Creative:
				SpawnCreative( card );
				break;

			case GameModeKind.PropHunt:
				SpawnPropHunt( card );
				break;

			case GameModeKind.Charades:
				SpawnCharades( card );
				break;

			default:
				// A new GameModeKind was added but not wired up here — every game needs a case that creates its
				// manager (and, if it has per-map knobs, a group on MapModeCard).
				Log.Warning( $"No manager wired for game '{game}' — falling back to prop hunt." );
				SpawnPropHunt( card );
				break;
		}

		_done = true;
	}

	void SpawnCreative( MapModeCard card )
	{
		if ( CreativeManager.Current.IsValid() )
			return;

		var go = new GameObject( true, "Creative Manager" );
		go.Components.Create<CreativeManager>(); // pawn prefabs are read live off this spawner, like RoundManager's
		// The claim service rides the same GameObject — one NetworkSpawn ships both, and its [Sync]
		// registry + RPCs need the networked object. Reach set BEFORE the spawn: the snapshot ships
		// this component's live JSON, so the host's authored value is the reach every client's hover
		// uses — a client's own scene copy never applies.
		var claims = go.Components.Create<PropClaims>();
		claims.HoverRange = card.IsValid() ? card.CreativeHoverRange : MapModeCard.DefaultCreativeHoverRange;
		go.NetworkSpawn();
	}

	void SpawnCharades( MapModeCard card )
	{
		if ( CharadesManager.Current.IsValid() )
			return;

		var go = new GameObject( true, "Charades Manager" );
		var cm = go.Components.Create<CharadesManager>(); // pawn prefab read live off this spawner, like RoundManager's

		// Same lobby-vs-card precedence as prop hunt: a direct play takes the card's debug knobs; a real lobby
		// launch plays the lobby's rules only (the courier's bot count overrides the card's in OnStart).
		bool lobbyLaunch = !string.IsNullOrEmpty( Networking.GetData( MenuNetworking.Keys.Mode ) );
		if ( card.IsValid() )
		{
			cm.BotCount = card.BotCount;
			cm.BotRandomLooks = card.BotRandomDisguises;

			if ( lobbyLaunch )
			{
				if ( card.OverrideCharadesRules || card.CharadesSoloDebug )
					Log.Warning( "RoundManagerSpawner: ignoring this map card's charades override/solo debug — lobby launches play the lobby's rules." );
			}
			else if ( card.OverrideCharadesRules || card.CharadesSoloDebug )
			{
				// Solo debug without the full override still wants MinPlayers 1 — AuthoredCharadesRules folds
				// it in either way; the non-overridden fields are just the defaults then.
				cm.RulesOverride = card.OverrideCharadesRules
					? card.AuthoredCharadesRules
					: CharadesSettings.Default with { MinPlayers = 1 };
			}
		}

		go.NetworkSpawn(); // host owns it; replicates to every client (and late-joiners) with working [Sync]
	}

	void SpawnPropHunt( MapModeCard card )
	{
		if ( RoundManager.Current.IsValid() )
			return;

		var go = new GameObject( true, "Round Manager" );
		var rm = go.Components.Create<RoundManager>();
		// Only host-side scoring config is copied onto the (host-only) manager; the pawn prefabs are read live off
		// this scene-placed spawner by every machine (RoundManager.PrefabFor) — clients' manager copies wouldn't
		// carry [Property] refs.
		rm.FindReward = FindReward;
		rm.PropPointsPerSecond = PropPointsPerSecond;

		// Debug/test config comes off the map's card — host-only by nature: the host seats the bots, owns their
		// bodies, and decides the rules. No card = a clean real round.
		//
		// But only a DIRECT play (the map opened straight in the editor — the card's whole purpose) takes the
		// debug knobs. A lobby launch is a REAL round and the lobby's choices are the only truth: a knob left
		// ticked on a map must not silently distort it — a card's OverrideRules swapped a lobby's Teams round
		// back to Infection, and its PlayAs force-huntered the host, through several confusing playtests. The
		// game-mode key is stamped by every lobby launch and blanked by the direct-play path above, so its
		// presence IS "we came from a lobby". (Bots are safe either way: a lobby launch always writes
		// BotCountKey, which overrides the card's count in RoundManager.OnStart.)
		bool lobbyLaunch = !string.IsNullOrEmpty( Networking.GetData( MenuNetworking.Keys.Mode ) );
		if ( card.IsValid() )
		{
			rm.BotCount = card.BotCount;
			rm.BotHunterPawns = card.BotHunterPawns;
			rm.BotRandomDisguises = card.BotRandomDisguises;

			if ( lobbyLaunch )
			{
				if ( card.OverrideRules || card.PlayAs != PlayAsChoice.Auto || card.DebugSoloHide )
					Log.Warning( "RoundManagerSpawner: ignoring this map card's debug round config " +
						"(rules override / PlayAs / solo hide) — lobby launches play the lobby's rules." );
			}
			else
			{
				rm.DebugSoloHide = card.DebugSoloHide;
				rm.PlayAs = card.PlayAs;
				rm.RulesOverride = card.OverrideRules ? card.AuthoredRules : null;
			}
		}

		go.NetworkSpawn(); // host owns it; replicates to every client (and late-joiners) with working [Sync]
	}
}
