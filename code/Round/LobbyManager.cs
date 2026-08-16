using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// The lobby's networked state + host logic, NetworkSpawn'd by <see cref="LobbyController"/> exactly the way
/// <see cref="RoundManagerSpawner"/> spawns <see cref="RoundManager"/> — and for the same reason: a scene-placed
/// component's <c>[Sync]</c> CHANGES don't replicate here, but a NetworkSpawn'd object's do, including to
/// late-joiners via the spawn snapshot. That's what makes client nomination toggles and the launch countdown
/// actually visible on every machine — the old scene-placed <c>[Sync]</c> silently never updated on clients (a
/// client could nominate but never UN-nominate, because it flipped its own stale row), and the countdown rode a
/// fire-and-forget broadcast that anyone joining mid-countdown simply missed.
///
/// Owns: the roster (<see cref="Players"/>), the round config (<see cref="Settings"/>), the launch countdown,
/// the client→host requests, and the host-side pawn spawning. Scene furniture (prefabs, spawn points, inspector
/// defaults) stays on the scene-placed <see cref="LobbyController"/>, which exists on every machine.
///
/// Lobby scene props are EDITABLE, creative-style: this manager hosts a <see cref="PropClaims"/> service
/// (spawned beside it by LobbyController) and implements <see cref="IPropClaimHost"/> for it — aim at clay,
/// "E to Edit", possess, sculpt. A claimed prop is map furniture, so a role swap RELEASES it back into the
/// world (see <see cref="RequestSwapRole"/>) instead of destroying it like a practice body.
/// </summary>
[Title( "Lobby Manager" )]
[Category( "Mimiclay" )]
[Icon( "groups" )]
public sealed class LobbyManager : Component, IRoundContext, IPropClaimHost
{
	/// <summary>The live lobby manager (null in map scenes / the menu, and on a client until the host's spawn
	/// replicates). The lobby UI + LobbyController's input forwarding read this.</summary>
	public static LobbyManager Current { get; private set; }

	// ── Networked state (host writes, everyone reads — incl. late-joiners via the spawn snapshot) ─────────────
	/// <summary>Per-player lobby state: which role they're editing as + whether they've nominated to hunt.</summary>
	[Sync] public NetDictionary<Guid, PlayerInfo> Players { get; private set; } = new();

	/// <summary>Which GAME this session is set up to play — the top level of the mode taxonomy (prop hunt /
	/// creative), chosen in the setup dialog. Synced so every client's lobby UI shows what's coming.
	/// Per-game rules live beside it (<see cref="Settings"/> for prop hunt).</summary>
	[Sync] public GameModeKind SelectedGame { get; private set; } = GameModeKind.PropHunt;

	/// <summary>The round rules the host is configuring — genuinely synced now, so every client's lobby UI shows
	/// the same setup. Seeded from LobbyController's inspector defaults on the host in OnStart.</summary>
	[Sync] public RoundSettings Settings { get; set; } = RoundSettings.Default;

	/// <summary>The creative-mode rules the host is configuring (only meaningful while <see cref="SelectedGame"/>
	/// is Creative; carried into the map by its courier at launch — see <see cref="CreativeSettings"/>).</summary>
	[Sync] public CreativeSettings CreativeCfg { get; set; } = CreativeSettings.Default;

	/// <summary>True from Start being hit until the scene change. [Sync]'d, so a client joining mid-countdown
	/// sees the launch coming instead of an idle "waiting for host".</summary>
	[Sync] public bool Launching { get; private set; }

	/// <summary>When the launch countdown ends. <c>TimeUntil</c> is clock-skew-corrected per client, same as
	/// <see cref="RoundManager.PhaseEndsAt"/>, so it reads correctly everywhere.</summary>
	[Sync] public TimeUntil LaunchEndsAt { get; private set; }

	// ── Host-only pawn bookkeeping (the lobby host-spawns pawns and hands each to its owner) ──────────────────
	readonly Dictionary<Guid, GameObject> _pawns = new();
	readonly HashSet<Guid> _known = new();
	readonly HashSet<Guid> _botLooksPending = new();   // bots still waiting on a face to dress (see TryDressBot)

	// What each player's last pawn OF EACH ROLE was like, snapshotted host-side as that pawn is destroyed in a
	// role swap and put back on the next pawn of that role — so swapping prop↔hunter is returning to a body
	// you parked, not rolling a fresh character.
	//
	//  • Face: the hunter head. The host can't read a client's saved head off their disk, so without this a
	//    client's fresh hunter pawn ships wearing the prefab default and everyone watches it flash until the
	//    client's own dress publishes back. The owner's on-arrival dress reconciles any drift (a
	//    content-identical no-op in the normal case).
	//  • Disguise: the prop they were sculpting. Deliberately saved on NO disk anywhere (see
	//    SculptEditSession.PersistSlot) — this cache is the only copy, and it's what makes a swap round-trip
	//    keep your work-in-progress.
	//  • PropYaw: the prop BODY's facing (the cone keeps pointing where it pointed). Only the prop's — a
	//    hunter's facing is its aim, which is view state, and the VIEW is continuity, not memory: the camera
	//    direction rides the swap request (yaw) and LobbySwapCarry (pitch + prop zoom, owner-side), so
	//    whatever you were looking at, you still are — while the prop's body ignores the view entirely.
	sealed class SwapMemory
	{
		public List<SdfBrush> Face;
		public List<SdfBrush> Disguise;
		public float? PropYaw;
	}

	readonly Dictionary<Guid, SwapMemory> _swapMemory = new();

	SwapMemory MemoryFor( Guid id )
		=> _swapMemory.TryGetValue( id, out var m ) ? m : (_swapMemory[id] = new SwapMemory());

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	// Most hunters we'll allow — always one fewer than the lobby size, so there's at least one prop. (Solo in the
	// editor it floors at 1, since one person can't be both.) Counted off the ROSTER, not Connection.All, so
	// seated test bots raise the ceiling — otherwise a solo host could never slide the hunter count past 1 and
	// the multi-hunter setup would be untestable without a second machine.
	int MaxHunters => Math.Max( 1, Players.Count - 1 );

	// ── IRoundContext (for the phase-agnostic HUD) ─────────────────────────────────────────────────────────────
	RoundPhase IRoundContext.Phase => RoundPhase.Lobby;
	float IRoundContext.TimeRemaining => Launching ? MathF.Max( 0f, LaunchEndsAt ) : 0f;
	bool IRoundContext.HasTimer => Launching;

	// ── IPropClaimHost (lobby prop editing — the claim flow lives in PropClaims, beside us) ────────────────────
	// Claims close during the launch countdown: the scene is about to die, and a mid-countdown pawn swap
	// would race the launch's roster snapshot.
	bool IPropClaimHost.ClaimsAllowed => !Launching;

	GameObject IPropClaimHost.PropPrefab
		=> LobbyController.Current.IsValid() ? LobbyController.Current.PropPrefab : null;

	GameObject IPropClaimHost.ClaimantPawn( Connection c ) => _pawns.GetValueOrDefault( c.Id );

	void IPropClaimHost.OnClaimGranted( Connection c, GameObject hunterPawn, HiderController prop )
	{
		RememberPawn( c.Id, hunterPawn ); // the face — the hunter pawn dies right after this returns
		if ( Players.TryGetValue( c.Id, out var row ) )
		{
			row.Role = PlayerRole.Prop;
			Players[c.Id] = row;
		}
		_pawns[c.Id] = prop.GameObject;
	}

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
		if ( !IsHostAuthority )
			return;

		// Seed the synced config from the scene's inspector defaults. Fresh each lobby visit (results carry-back
		// is still the TODO on RoundManager.ReturnToLobby).
		var lc = LobbyController.Current;
		Settings = lc.IsValid() ? lc.DefaultSettings : RoundSettings.Default;
	}

	protected override void OnUpdate()
	{
		if ( !IsHostAuthority )
			return;

		ReconcileConnections();
		ReconcileBots(); // after the real players, so they take the front spawn slots

		// Keep the hunter count valid as the lobby grows/shrinks — never more than MaxHunters, so a prop always
		// remains.
		if ( Settings.HunterCount > MaxHunters )
		{
			var s = Settings; s.HunterCount = MaxHunters; Settings = s;
		}

		// Fire the launch once the countdown elapses.
		if ( Launching && LaunchEndsAt <= 0f )
			Launch();
	}

	// Host-only. Auto-spawn a hunter pawn for each connection so nobody sits in an empty lobby; forget gone
	// connections so a rejoin re-spawns them; drop leavers' rows + pawns.
	void ReconcileConnections()
	{
		_known.RemoveWhere( id => !Connection.All.Any( c => c.Id == id ) );
		foreach ( var c in Connection.All )
		{
			if ( _known.Add( c.Id ) )
			{
				Players[c.Id] = NewRow( c, PlayerRole.Hunter, NextFreeSpawnIndex() );
				SpawnPawn( c, PlayerRole.Hunter );
			}
		}

		// Bot rows are exempt: there was never a connection behind them to leave, and this sweep would otherwise
		// wipe every bot the frame after ReconcileBots seats it.
		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Players[id].Bot || Connection.All.Any( c => c.Id == id ) )
				continue;
			Players.Remove( id );
			_swapMemory.Remove( id ); // a leaver's remembered bodies have nobody to come back for them
			if ( _pawns.Remove( id, out var pawn ) )
				Retire( pawn );
		}
	}

	// ── Test bots ──────────────────────────────────────────────────────────────────────────────────────────────
	// Host-only. Keep the seated bots matching LobbyController's inspector count — read LIVE each frame rather than
	// latched at start, so turning the dial while playing adds or removes them on the spot. Bots spawn as hunters
	// like everyone else; the only thing that makes them different from a real row is that nobody is behind them,
	// so the host holds their pawns and PlayerInfo.Bot marks them for the sweeps that would otherwise drop them.
	void ReconcileBots()
	{
		var lc = LobbyController.Current;
		var want = Math.Max( 0, lc.IsValid() ? lc.BotCount : 0 );
		var nominations = Math.Clamp( lc.IsValid() ? lc.BotNominations : 0, 0, want );

		var wanted = new HashSet<Guid>();
		for ( var i = 0; i < want; i++ )
			wanted.Add( RoundBots.IdFor( i ) );

		// Bots we no longer want — the count was turned down, or off.
		foreach ( var id in Players.Keys.ToList() )
		{
			if ( !Players[id].Bot || wanted.Contains( id ) )
				continue;

			Players.Remove( id );
			if ( _pawns.Remove( id, out var gone ) && gone.IsValid() )
				gone.Destroy();
			_botLooksPending.Remove( id );
		}

		for ( var i = 0; i < want; i++ )
		{
			var id = RoundBots.IdFor( i );
			var nominated = i < nominations;

			if ( !Players.TryGetValue( id, out var row ) )
			{
				// SpawnIndex is read BEFORE the row goes in, or the row would count itself as occupying a slot.
				row = new PlayerInfo
				{
					Connection = id,
					Name = RoundBots.NameFor( i ),
					Role = PlayerRole.Hunter,
					Alive = true,
					Found = false,
					Nominated = nominated,
					Score = 0,
					Bot = true,
					SpawnIndex = NextFreeSpawnIndex(),
				};
				Players[id] = row;
				SpawnPawnFor( id, row.Name, PlayerRole.Hunter, null );

				if ( lc.IsValid() && lc.BotRandomLooks )
					_botLooksPending.Add( id ); // deferred: the hunter resolves its Face in OnStart

				continue;
			}

			// The nomination dial is live too — retuning it re-stamps the existing rows rather than needing a
			// respawn, so you can watch the lobby UI react.
			if ( row.Nominated != nominated )
			{
				row.Nominated = nominated;
				Players[id] = row;
			}

			if ( _botLooksPending.Contains( id ) )
				TryDressBot( id );
		}
	}

	// Host-only. Give a bot a random saved shape for a face. Retried each frame until the hunter's OnStart has
	// resolved its Face sculpture (an empty sculpt library settles it immediately on the prefab's default head).
	void TryDressBot( Guid id )
	{
		if ( !_pawns.TryGetValue( id, out var pawn ) || !pawn.IsValid() )
		{
			_botLooksPending.Remove( id );
			return;
		}

		var hunter = pawn.Components.Get<HunterController>();
		if ( RoundBots.TryWearRandomSculpt( hunter.IsValid() ? hunter.Face : null ) )
			_botLooksPending.Remove( id );
	}

	/// <summary>Flip the LOCAL player's nomination. Reads the current value off the synced roster — which now
	/// actually updates on clients, so pressing N genuinely toggles instead of re-sending "true" forever.</summary>
	public void ToggleNominate()
	{
		var id = Connection.Local?.Id;
		var current = id is not null && Players.TryGetValue( id.Value, out var row ) && row.Nominated;
		SetNominated( !current );
	}

	// ── Client → host requests ─────────────────────────────────────────────────────────────────────────────────
	/// <summary>Caller swaps to the opposite lobby role — hunter ↔ prop — respawning where they stand.
	/// <paramref name="viewYaw"/> is the caller's CAMERA yaw at the press — the view that must stay
	/// continuous across the swap. A new HUNTER spawns aiming along it; a new PROP's body ignores it (the
	/// body facing is remembered per role — see SwapMemory) and only its orbit camera picks the view up,
	/// owner-side via LobbySwapCarry (which also carries the pitch and the prop zoom the host can't see).</summary>
	[Rpc.Host]
	public void RequestSwapRole( float viewYaw )
	{
		var c = Rpc.Caller;
		if ( c is null || Launching )
			return;

		// Everyone auto-spawns as a hunter in ReconcileConnections, so a missing row (a press racing the first
		// reconcile) defaults to Hunter — the swap then lands them as a Prop, same as it would a frame later.
		var row = Players.TryGetValue( c.Id, out var existing ) ? existing : NewRow( c, PlayerRole.Hunter, NextFreeSpawnIndex() );
		row.Role = row.Role == PlayerRole.Prop ? PlayerRole.Hunter : PlayerRole.Prop;
		Players[c.Id] = row;
		_known.Add( c.Id );

		// Leaving a CLAIMED prop (a scene prop the claim service converted — see PropClaims.IsConverted): it's
		// map furniture, not a practice body, so it's RELEASED back into the world — claimable again — instead
		// of destroyed, and the fresh hunter spawns stepped CLEAR of its hull (the usual in-place respawn would
		// land inside the released disguise's collider and get solver-shoved, sometimes through the floor).
		// Deliberately no RememberPawn: swap memory holds YOUR practice disguise, not borrowed furniture.
		var claims = PropClaims.Current;
		if ( row.Role == PlayerRole.Hunter
			&& claims.IsValid()
			&& _pawns.TryGetValue( c.Id, out var worn ) && worn.IsValid()
			&& claims.IsConverted( worn ) )
		{
			var hider = worn.Components.Get<HiderController>();
			if ( hider.IsValid() )
			{
				var at = claims.HunterSpotClearOf( hider, viewYaw );
				_pawns.Remove( c.Id );
				claims.Release( hider );
				SpawnPawnFor( c.Id, c.DisplayName, PlayerRole.Hunter, c, viewYaw, at );
				return;
			}
		}

		SpawnPawn( c, row.Role, viewYaw );
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

	/// <summary>Host-only: begin the launch countdown. It's plain [Sync] state now (<see cref="Launching"/> +
	/// <see cref="LaunchEndsAt"/>), so every machine — including anyone who joins mid-countdown — sees the same
	/// ticking clock from the snapshot. Ignored from non-host callers and while already launching.</summary>
	[Rpc.Host]
	public void RequestStart()
	{
		if ( Launching )
			return;
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return; // only the host starts

		// Don't begin a countdown we can't finish — every map-playing game needs a resolvable map.
		if ( GameModes.Get( SelectedGame ).UsesMaps && MapCatalog.Resolve( Settings.MapIdent ) is null )
		{
			Log.Warning( "LobbyManager: can't start — no Prop Hunt Map assets exist. Create one and pick it." );
			return;
		}

		LaunchEndsAt = LobbyController.Current.IsValid() ? LobbyController.Current.LaunchCountdownSeconds : 10f;
		Launching = true;
	}

	// ── Host-side config (the host owns this object, so it can set the synced fields directly) ────────────────
	/// <summary>Pick which game the session plays. Re-stamps the browser-facing lobby data too, so the server
	/// list shows what a session is set up for the moment the host changes their mind — not just what it was
	/// when the session was created.</summary>
	public void SetGame( GameModeKind game )
	{
		if ( !IsHostAuthority ) return;
		SelectedGame = game;
		if ( Networking.IsActive )
			Networking.SetData( MenuNetworking.Keys.Mode, game.ToString() );
	}

	public void SetRoundMode( RoundMode mode )
	{
		if ( !IsHostAuthority ) return;
		var s = Settings; s.Mode = mode; Settings = s;
	}

	public void SetMap( string mapIdent )
	{
		if ( !IsHostAuthority ) return;
		var s = Settings; s.MapIdent = mapIdent; Settings = s;
	}

	public void SetHunterCount( int count )
	{
		if ( !IsHostAuthority ) return;
		var s = Settings; s.HunterCount = Math.Clamp( count, 1, MaxHunters ); Settings = s;
	}

	public void SetHideSeconds( float seconds )
	{
		if ( !IsHostAuthority ) return;
		var s = Settings; s.HideSeconds = MathF.Max( 1f, seconds ); Settings = s;
	}

	public void SetHuntSeconds( float seconds )
	{
		if ( !IsHostAuthority ) return;
		var s = Settings; s.HuntSeconds = MathF.Max( 1f, seconds ); Settings = s;
	}

	public void SetRevealSeconds( float seconds )
	{
		if ( !IsHostAuthority ) return;
		var s = Settings; s.RevealSeconds = MathF.Max( 1f, seconds ); Settings = s;
	}

	// Floor of 5s (not the 1s the phase timers use): the taunt repeats for every prop for the whole hunt, and
	// per-second whistling would be pure noise spam.
	public void SetTauntSeconds( float seconds )
	{
		if ( !IsHostAuthority ) return;
		var s = Settings; s.TauntSeconds = MathF.Max( 5f, seconds ); Settings = s;
	}

	// ── Creative config (same copy-mutate-write shape as the round setters above) ─────────────────────────────
	public void SetSpawnProps( bool on )
	{
		if ( !IsHostAuthority ) return;
		var s = CreativeCfg; s.SpawnProps = on; CreativeCfg = s;
	}

	// ── Launch ─────────────────────────────────────────────────────────────────────────────────────────────────
	// Host-only. The countdown elapsed: every game launches into the picked map. The mode key stamped below is
	// the courier that tells the map scene which game to run — RoundManagerSpawner reads it and spawns that
	// game's manager (RoundManager / CreativeManager). Everything rides session data because the scene change
	// destroys the lobby scene and every component on it, this one included.
	void Launch()
	{
		Launching = false;
		if ( !Networking.IsHost && Networking.IsActive )
			return;

		// Re-stamp the browser-facing mode at the moment it becomes true (SetGame keeps it live pre-launch).
		// This same key doubles as the map scene's game selector.
		Networking.SetData( MenuNetworking.Keys.Mode, SelectedGame.ToString() );

		// Creative's own rules ride their courier (CreativeManager reads them back on every machine).
		if ( SelectedGame == GameModeKind.Creative )
			CreativeCfg.WriteToLobby();

		LaunchIntoMap();
	}

	// Resolve the map, stamp the round settings + nominated hunters into session data, then change scene into
	// the map where the game's manager reads them back. Shared by every game: creative ignores the hunter ids
	// (it has no roles to assign) but rides the same courier.
	void LaunchIntoMap()
	{
		var map = MapCatalog.Resolve( Settings.MapIdent );
		if ( map is null || map.Scene is null )
		{
			Log.Warning( "LobbyManager: nothing to launch — no Prop Hunt Map asset (with a Scene) to load." );
			return;
		}

		// Carry the RESOLVED map's path (Random already rolled into a real one) so every client agrees on the scene.
		Settings.WriteToLobby( map.ResourcePath );
		Networking.SetData( RoundManager.HunterIdsKey, NominatedHunterIds() );

		// Hand the bot crowd over, so the lobby you set up is the round you play. Written EVERY launch, blank when
		// they're not to follow: session data outlives the scene change (that's the whole point of it), so a stale
		// count from a previous launch would otherwise keep seating bots after you turned them off.
		var follow = LobbyController.Current.IsValid() && LobbyController.Current.BotsFollowIntoRound;
		Networking.SetData( RoundManager.BotCountKey,
			follow ? Players.Values.Count( p => p.Bot ).ToString( CultureInfo.InvariantCulture ) : "" );

		var options = new SceneLoadOptions();
		if ( !options.SetScene( map.Scene ) )
		{
			Log.Warning( $"LobbyManager: couldn't load the scene for map '{map.Title}'." );
			return;
		}

		Game.ChangeScene( options );
	}

	// The nominated connections, comma-joined for lobby data. Empty if nobody nominated — RoundManager then rolls
	// hunters at random (honouring Settings.HunterCount).
	string NominatedHunterIds()
		=> string.Join( ',', Players.Values.Where( p => p.Nominated ).Select( p => p.Connection ) );

	// ── Spawning (host-authoritative; scene furniture read off the scene-placed LobbyController) ──────────────
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

	GameObject SpawnPawn( Connection connection, PlayerRole role, float? viewYaw = null )
		=> SpawnPawnFor( connection.Id, connection.DisplayName, role, connection, viewYaw );

	// The one spawn path, for players and bots alike. A null <paramref name="owner"/> means a bot: the pawn stays
	// host-owned and is prepared so nobody drives it (RoundBots.Prepare), instead of being handed to a connection.
	// A non-null <paramref name="viewYaw"/> spawns the pawn facing it (a role swap carrying the caller's camera).
	// A non-null <paramref name="atOverride"/> spawns there instead of the slot/in-place spot — the release path
	// uses it to step the fresh hunter clear of the prop it just let go of.
	GameObject SpawnPawnFor( Guid id, string name, PlayerRole role, Connection owner, float? viewYaw = null, Transform? atOverride = null )
	{
		var lc = LobbyController.Current;
		if ( !lc.IsValid() )
		{
			Log.Warning( "LobbyManager: no scene LobbyController — can't read pawn prefabs." );
			return null;
		}

		var prefab = role == PlayerRole.Hunter ? lc.HunterPrefab : lc.PropPrefab;
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"LobbyManager: no {role} prefab assigned — can't spawn for {name}." );
			return null;
		}

		var at = atOverride ?? lc.SpotAt( Players.TryGetValue( id, out var row ) ? row.SpawnIndex : 0 );

		// A role swap respawns IN PLACE — where the old pawn stands, not back at the spawn ring. Same buried-origin
		// guard as RoundManager.EnsureOwnPawn: a sculpted prop's origin can sit under the floor, so ground the new
		// pawn on the shape's feet (traced down onto whatever it stood on) rather than the raw origin.
		if ( atOverride is null && _pawns.TryGetValue( id, out var previous ) && previous.IsValid() )
		{
			at = previous.WorldTransform;
			var hider = previous.Components.Get<HiderController>();
			if ( hider.IsValid() && hider.TryGetShapeFeet( out var feet ) )
			{
				var tr = Scene.Trace.Ray( feet + Vector3.Up * 64f, feet - Vector3.Up * 8f )
					.IgnoreGameObjectHierarchy( previous ) // the old pawn's disguise collider is still live — the ray starts above it
					.Run();
				at = at.WithPosition( tr.Hit ? tr.HitPosition : feet );
			}
		}

		_swapMemory.TryGetValue( id, out var memory );

		// The spawn rotation means something different per role. A HUNTER spawns facing the caller's camera
		// yaw — its facing IS its view, and the view is continuous across a swap (the engine PlayerController
		// seeds EyeAngles from the spawn rotation, then resets the root to identity). A PROP spawns at its
		// REMEMBERED body facing — the cone keeps pointing where it pointed — and its camera picks the view
		// up separately, owner-side (LobbySwapCarry): HiderController seeds body AND orbit from the spawn
		// rotation, then the carry re-aims just the orbit. Position still comes from the in-place block above
		// (you swap where you STAND).
		var spawnYaw = role == PlayerRole.Prop ? (memory?.PropYaw ?? viewYaw) : viewYaw;
		if ( spawnYaw is { } yaw )
			at = at.WithRotation( Rotation.FromYaw( yaw ) );

		// A fresh pawn wears its player's remembered body BEFORE anything builds. Cloned DISABLED for that:
		// SdfSculpture.OnEnabled fires Rebuild, so an enabled clone already has the default's build in flight
		// before a post-clone dress could swap the brushes — dressing first means the first build ever started
		// is the right shape, and the NetworkSpawn below snapshots it for everyone.
		//
		// Hunters: the host's own face comes off this machine's disk (the saved head slot); a CLIENT's comes
		// from the swap memory (the host can't read their disk, but it was just rendering their previous
		// hunter pawn), and their own on-arrival dress reconciles any drift. Props: the remembered disguise is
		// the ONLY copy anywhere (no disk, by design), and everyone — owner included — receives it through
		// the spawn snapshot. A first-ever spawn has nothing remembered and spawns the prefab default.
		var dressLocalHead = role == PlayerRole.Hunter && owner is not null && owner.Id == Connection.Local?.Id;
		var dressCachedFace = role == PlayerRole.Hunter && !dressLocalHead && owner is not null && memory?.Face is not null;
		var dressDisguise = role == PlayerRole.Prop && owner is not null && memory?.Disguise is not null;
		var dress = dressLocalHead || dressCachedFace || dressDisguise;

		var pawn = prefab.Clone( new CloneConfig( at, startEnabled: !dress, name: $"Lobby Pawn ({role}) {name}" ) );
		if ( !pawn.IsValid() )
			return null;

		if ( dress )
		{
			if ( dressLocalHead )
				HunterController.WearSavedHead( pawn );
			else if ( dressCachedFace )
				HunterController.WearFace( pawn, memory.Face );

			if ( dressDisguise )
				HiderController.WearDisguise( pawn, memory.Disguise );

			pawn.Enabled = true;
		}

		// A bot's body: stamp its roster row, take the controls away, strip the per-player hardware. Before the
		// NetworkSpawn below, so BotPawn.RosterId ships in the spawn snapshot and clients resolve it too.
		if ( owner is null )
			RoundBots.Prepare( pawn, id );

		// The new pawn takes over the old one's exact spot, so the old pawn goes away entirely. Releasing it as
		// scenery (the Retire path, kept for disconnects) would leave the fresh pawn spawning inside the released
		// disguise's collider — the solver shoves overlapping hulls apart, sometimes through the floor.
		if ( _pawns.Remove( id, out var old ) && old.IsValid() )
		{
			RememberPawn( id, old );
			old.Destroy();
		}

		// Handed to its player, or kept by the host when there isn't one.
		if ( owner is not null )
			pawn.NetworkSpawn( owner );
		else
			pawn.NetworkSpawn();

		_pawns[id] = pawn;
		return pawn;
	}

	// Snapshot what a departing pawn WORE — face or disguise — into _swapMemory (see it for why). Role told
	// apart by controller: ResolveFaceOf on a prop would happily hand back the disguise sculpture, which is
	// not a face. Brushes are copied brush-by-brush so nothing that later mutates the (about-to-die) live
	// list can reach the cache.
	void RememberPawn( Guid id, GameObject pawn )
	{
		if ( pawn.Components.Get<HunterController>( includeDisabled: true ).IsValid() )
		{
			var face = HunterController.ResolveFaceOf( pawn );
			if ( face.IsValid() && face.Brushes is { Count: > 0 } )
				MemoryFor( id ).Face = face.Brushes.Select( b => b.Copy() ).ToList();
			return;
		}

		var hider = pawn.Components.Get<HiderController>( includeDisabled: true );
		if ( hider.IsValid() )
		{
			var m = MemoryFor( id );

			// The hider turns its physics root through the solver, so the root yaw IS the body's facing.
			m.PropYaw = pawn.WorldRotation.Angles().yaw;

			var disguise = hider.DisguiseSculpture;
			if ( disguise.IsValid() && disguise.Brushes is { Count: > 0 } )
				m.Disguise = disguise.Brushes.Select( b => b.Copy() ).ToList();
		}
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

		// With the claim service live, release properly: the same dormant + ownership drop as below, PLUS the
		// registry entry that makes a leaver's surviving prop claimable by everyone else.
		if ( PropClaims.Current.IsValid() )
		{
			PropClaims.Current.Release( hider );
			return;
		}

		hider.ReleaseControl();
		if ( Networking.IsActive )
			pawn.Network.DropOwnership();
	}
}
