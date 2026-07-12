using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Runs the lobby (the hub players return to between rounds), in its own <c>lobby.scene</c>. Here players spawn as
/// a hunter to edit their face — or switch to a prop to practise a disguise — nominate themselves to hunt, and the
/// host configures the round (map + phase times) and starts it. Starting writes the choices into session data and
/// changes scene into the chosen map, where <see cref="RoundManager"/> takes over; after the round the manager
/// changes scene back here and the loop repeats.
///
/// <b>Networking.</b> Like <see cref="RoundManager"/>: one host-owned singleton, host-authoritative spawning, the
/// shared config + nominations replicated via <c>[Sync]</c>. The host owns the config controls; clients see them
/// read-only. Client→host messages: <see cref="RequestEditRole"/>, <see cref="SetNominated"/>,
/// <see cref="RequestStart"/>.
/// </summary>
[Title( "Lobby Controller" )]
[Category( "Mimiclay" )]
[Icon( "groups" )]
public sealed class LobbyController : Component, IRoundContext
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
	// tuned per lobby scene without touching code — they seed Settings on the host in OnStart.
	[Property, Group( "Round Defaults" )] public RoundMode DefaultRoundMode { get; set; } = RoundMode.Infection;
	[Property, Group( "Round Defaults" )] public float DefaultHideSeconds { get; set; } = 45f;
	[Property, Group( "Round Defaults" )] public float DefaultHuntSeconds { get; set; } = 180f;
	[Property, Group( "Round Defaults" )] public float DefaultRevealSeconds { get; set; } = 6f;
	[Property, Group( "Round Defaults" )] public float DefaultConsolidationSeconds { get; set; } = 12f;
	[Property, Group( "Round Defaults" )] public float DefaultStartCountdownSeconds { get; set; } = 4f;
	[Property, Group( "Round Defaults" )] public int DefaultHunterCount { get; set; } = 1;

	/// <summary>Seconds between hitting Start and the scene change, so everyone sees the launch coming.</summary>
	[Property, Group( "Round Defaults" )] public float LaunchCountdownSeconds { get; set; } = 10f;

	// ── Networked state ───────────────────────────────────────────────────────────────────────────────────
	/// <summary>Per-player lobby state: which role they're editing as + whether they've nominated to hunt.</summary>
	[Sync] public NetDictionary<Guid, PlayerInfo> Players { get; private set; } = new();

	/// <summary>The round rules the host is configuring (synced so every client's lobby UI shows the same setup).
	/// Seeded from the inspector defaults on the host in OnStart.</summary>
	[Sync] public RoundSettings Settings { get; set; } = RoundSettings.Default;

	// The starting settings built from the inspector defaults above.
	RoundSettings DefaultSettings => new()
	{
		Mode = DefaultRoundMode,
		StartCountdownSeconds = DefaultStartCountdownSeconds,
		HideSeconds = DefaultHideSeconds,
		HuntSeconds = DefaultHuntSeconds,
		RevealSeconds = DefaultRevealSeconds,
		ConsolidationSeconds = DefaultConsolidationSeconds,
		HunterCount = DefaultHunterCount,
		MapIdent = MapCatalog.RandomIdent,
	};

	/// <summary>True once Start has been hit and the launch countdown is running. Set on EVERY machine by the
	/// <see cref="BeginCountdown"/> broadcast (not [Sync]), so the whole lobby counts down together.</summary>
	public bool Launching { get; private set; }

	/// <summary>When <see cref="Launching"/>, the local moment the countdown ends. Each machine runs its own (set
	/// from the broadcast) so it ticks on that client's clock; only the host's reaching zero changes the scene.</summary>
	public TimeUntil LaunchAt { get; private set; }

	/// <summary>The game mode the launch was started with — set on every machine by <see cref="BeginCountdown"/> so
	/// clients (whose <see cref="Settings"/> isn't synced) can show the right mode on the launch card.</summary>
	public RoundMode LaunchMode { get; private set; }

	readonly Dictionary<Guid, GameObject> _pawns = new();
	readonly HashSet<Guid> _known = new();

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	// Most hunters we'll allow — always one fewer than the lobby size, so there's at least one prop. (Solo in the
	// editor it floors at 1, since one person can't be both.)
	int MaxHunters => Math.Max( 1, Connection.All.Count - 1 );

	// ── IRoundContext ────────────────────────────────────────────────────────────────────────────────────────
	RoundPhase IRoundContext.Phase => RoundPhase.Lobby;
	float IRoundContext.TimeRemaining => Launching ? MathF.Max( 0f, LaunchAt ) : 0f;
	bool IRoundContext.HasTimer => Launching;

	protected override void OnEnabled()
	{
		Current = this;
		RoundContext.Active = this;
	}

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
		if ( ReferenceEquals( RoundContext.Active, this ) ) RoundContext.Active = null;
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

		// The host owns the round setup: seed the synced Settings from the inspector defaults (clients receive them
		// via [Sync]) and spawn the setup panel (programmatically, like the pause/edit HUDs — clients don't get one).
		// Checked against the LIVE session (not IsHostAuthority, which reads true on a still-reconnecting client and
		// would hand a mere client the host's setup panel).
		if ( Networking.IsActive && Networking.IsHost )
		{
			Settings = DefaultSettings;
			EnsureSetupHud();
		}
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

	protected override void OnUpdate()
	{
		// Read local input on EVERY machine (a client's keypress routes to the host via the RPCs), before the
		// host-only gate below.
		HandleDebugInput();

		if ( !IsHostAuthority )
			return;

		// Auto-spawn a hunter pawn for each connection so nobody sits in an empty lobby; forget gone connections so
		// a rejoin re-spawns them.
		_known.RemoveWhere( id => !Connection.All.Any( c => c.Id == id ) );
		foreach ( var c in Connection.All )
		{
			if ( _known.Add( c.Id ) )
			{
				Players[c.Id] = NewRow( c, PlayerRole.Hunter, NextFreeSpawnIndex() );
				SpawnPawn( c, PlayerRole.Hunter );
			}
		}

		// Drop leavers.
		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Connection.All.Any( c => c.Id == id ) )
				continue;
			Players.Remove( id );
			if ( _pawns.Remove( id, out var pawn ) )
				Retire( pawn );
		}

		// Keep the hunter count valid as the lobby grows/shrinks — never more than MaxHunters, so a prop always remains.
		if ( Settings.HunterCount > MaxHunters )
		{
			var s = Settings; s.HunterCount = MaxHunters; Settings = s;
		}

		// Fire the launch once the countdown elapses.
		if ( Launching && LaunchAt <= 0f )
			Launch();
	}

	// Temporary keyboard driving for the lobby until the full UI lands. Raw keys so they need no input-config
	// actions. Runs on any machine; the requests below are [Rpc.Host], so a client's press routes to the host. The
	// host's G (open the setup panel) is handled by RoundSetup itself. TODO: replace O/P/N with lobby UI controls.
	//   O = edit as Hunter   P = edit as Prop   N = toggle hunt nomination
	void HandleDebugInput()
	{
		if ( !Networking.IsActive || RoundSetup.IsOpen )
			return;

		if ( Input.Keyboard.Pressed( "O" ) ) RequestEditRole( PlayerRole.Hunter );
		if ( Input.Keyboard.Pressed( "P" ) ) RequestEditRole( PlayerRole.Prop );
		if ( Input.Keyboard.Pressed( "N" ) ) ToggleNominate();
	}

	// Flip the local player's nomination, reading the current value off the synced roster.
	void ToggleNominate()
	{
		var id = Connection.Local?.Id;
		var current = id is not null && Players.TryGetValue( id.Value, out var row ) && row.Nominated;
		SetNominated( !current );
	}

	// ── Client → host requests ───────────────────────────────────────────────────────────────────────────────
	/// <summary>Caller wants to edit as a hunter (face) or prop (disguise). Re-spawns their lobby pawn in that role.</summary>
	[Rpc.Host]
	public void RequestEditRole( PlayerRole role )
	{
		var c = Rpc.Caller;
		if ( c is null || Launching )
			return;
		if ( role is not PlayerRole.Hunter and not PlayerRole.Prop )
			return;

		var row = Players.TryGetValue( c.Id, out var existing ) ? existing : NewRow( c, role, NextFreeSpawnIndex() );
		row.Role = role;
		Players[c.Id] = row;
		_known.Add( c.Id );
		SpawnPawn( c, role );
	}

	/// <summary>Caller nominates (or un-nominates) themselves to be a hunter next round.</summary>
	[Rpc.Host]
	public void SetNominated( bool nominated )
	{
		var c = Rpc.Caller;
		if ( c is null || !Players.TryGetValue( c.Id, out var row ) )
			return;
		row.Nominated = nominated;
		Players[c.Id] = row;
	}

	/// <summary>Host-only: begin the launch. Broadcasts the countdown to everyone so all players see it tick down,
	/// not just the host. Ignored from non-host callers and while already launching.</summary>
	[Rpc.Host]
	public void RequestStart()
	{
		if ( Launching )
			return;
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return; // only the host starts

		// Don't begin a countdown we can't finish — there must be a map to launch into.
		if ( MapCatalog.Resolve( Settings.MapIdent ) is null )
		{
			Log.Warning( "LobbyController: can't start — no Prop Hunt Map assets exist. Create one and pick it." );
			return;
		}

		BeginCountdown( Settings.Mode );
	}

	// Runs on EVERY machine (host + all clients): start the launch countdown locally, so the whole lobby sees the
	// same ticking clock. Each machine's LaunchAt runs on its own clock; the host's hitting zero drives Launch().
	// The selected mode rides along so clients show the right game mode on the launch card (their Settings isn't
	// synced — see the scene-sync note).
	[Rpc.Broadcast]
	void BeginCountdown( RoundMode mode )
	{
		LaunchMode = mode;
		Launching = true;
		LaunchAt = LaunchCountdownSeconds;
	}

	// ── Host-side config (the host owns the component, so it can set the synced fields directly) ──────────────
	public void SetRoundMode( RoundMode mode )
	{
		if ( !Networking.IsHost && Networking.IsActive ) return;
		var s = Settings; s.Mode = mode; Settings = s;
	}

	public void SetMap( string mapIdent )
	{
		if ( !Networking.IsHost && Networking.IsActive ) return;
		var s = Settings; s.MapIdent = mapIdent; Settings = s;
	}

	public void SetHunterCount( int count )
	{
		if ( !Networking.IsHost && Networking.IsActive ) return;
		var s = Settings; s.HunterCount = Math.Clamp( count, 1, MaxHunters ); Settings = s;
	}

	public void SetHideSeconds( float seconds )
	{
		if ( !Networking.IsHost && Networking.IsActive ) return;
		var s = Settings; s.HideSeconds = MathF.Max( 1f, seconds ); Settings = s;
	}

	public void SetHuntSeconds( float seconds )
	{
		if ( !Networking.IsHost && Networking.IsActive ) return;
		var s = Settings; s.HuntSeconds = MathF.Max( 1f, seconds ); Settings = s;
	}

	public void SetRevealSeconds( float seconds )
	{
		if ( !Networking.IsHost && Networking.IsActive ) return;
		var s = Settings; s.RevealSeconds = MathF.Max( 1f, seconds ); Settings = s;
	}

	// ── Launch ───────────────────────────────────────────────────────────────────────────────────────────────
	// Host-only. Resolve the map, stamp the round settings + nominated hunters into session data (so they survive
	// the scene change), then change scene into the map where RoundManager reads them back.
	void Launch()
	{
		Launching = false;
		if ( !Networking.IsHost && Networking.IsActive )
			return;

		var map = MapCatalog.Resolve( Settings.MapIdent );
		if ( map is null || map.Scene is null )
		{
			Log.Warning( "LobbyController: nothing to launch — no Prop Hunt Map asset (with a Scene) to load." );
			return;
		}

		// Carry the RESOLVED map's path (Random already rolled into a real one) so every client agrees on the scene.
		Settings.WriteToLobby( map.ResourcePath );
		Networking.SetData( RoundManager.HunterIdsKey, NominatedHunterIds() );

		var options = new SceneLoadOptions();
		if ( !options.SetScene( map.Scene ) )
		{
			Log.Warning( $"LobbyController: couldn't load the scene for map '{map.Title}'." );
			return;
		}

		Game.ChangeScene( options );
	}

	// The nominated connections, comma-joined for lobby data. Empty if nobody nominated — RoundManager then rolls
	// hunters at random (honouring Settings.HunterCount).
	string NominatedHunterIds()
		=> string.Join( ',', Players.Values.Where( p => p.Nominated ).Select( p => p.Connection ) );

	// ── Spawning (mirrors RoundManager's, against the lobby's spawn point list) ─────────────────────────────
	static PlayerInfo NewRow( Connection c, PlayerRole role, int spawnIndex ) => new()
	{
		Connection = c.Id,
		Name = c.DisplayName,
		Role = role,
		Alive = true,
		Found = false,
		Nominated = false,
		Score = 0,
		SpawnIndex = spawnIndex,
	};

	// Lowest slot no current row holds — leavers free their spot, joiners fill the gap, so the lobby stays packed
	// around the first points rather than marching ever further down the wrap.
	int NextFreeSpawnIndex()
	{
		var used = Players.Values.Select( p => p.SpawnIndex ).ToHashSet();
		var i = 0;
		while ( used.Contains( i ) ) i++;
		return i;
	}

	// The spot for a slot: point slot%N (SpawnPoints, else the legacy single SpawnPoint, else this GameObject),
	// ringed outward via StackOffset once the points run out — same de-stack rule as RoundManager.PickSpot, so
	// simultaneous spawns never overlap hulls no matter how few points the scene has.
	Transform SpotAt( int slot )
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

	GameObject SpawnPawn( Connection connection, PlayerRole role )
	{
		var prefab = role == PlayerRole.Hunter ? HunterPrefab : PropPrefab;
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"LobbyController: no {role} prefab assigned — can't spawn for {connection.DisplayName}." );
			return null;
		}

		var at = SpotAt( Players.TryGetValue( connection.Id, out var row ) ? row.SpawnIndex : 0 );

		var pawn = prefab.Clone( at, name: $"Lobby Pawn ({role}) {connection.DisplayName}" );
		if ( !pawn.IsValid() )
			return null;

		if ( _pawns.Remove( connection.Id, out var old ) )
			Retire( old );

		pawn.NetworkSpawn( connection );
		_pawns[connection.Id] = pawn;
		return pawn;
	}

	static void Retire( GameObject pawn )
	{
		if ( !pawn.IsValid() )
			return;

		var hider = pawn.Components.Get<HiderController>();
		if ( !hider.IsValid() )
		{
			pawn.Destroy();
			return;
		}

		hider.ReleaseControl();
		if ( Networking.IsActive )
			pawn.Network.DropOwnership();
	}
}
