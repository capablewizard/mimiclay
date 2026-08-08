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
/// </summary>
[Title( "Lobby Manager" )]
[Category( "Mimiclay" )]
[Icon( "groups" )]
public sealed class LobbyManager : Component, IRoundContext
{
	/// <summary>The live lobby manager (null in map scenes / the menu, and on a client until the host's spawn
	/// replicates). The lobby UI + LobbyController's input forwarding read this.</summary>
	public static LobbyManager Current { get; private set; }

	// ── Networked state (host writes, everyone reads — incl. late-joiners via the spawn snapshot) ─────────────
	/// <summary>Per-player lobby state: which role they're editing as + whether they've nominated to hunt.</summary>
	[Sync] public NetDictionary<Guid, PlayerInfo> Players { get; private set; } = new();

	/// <summary>The round rules the host is configuring — genuinely synced now, so every client's lobby UI shows
	/// the same setup. Seeded from LobbyController's inspector defaults on the host in OnStart.</summary>
	[Sync] public RoundSettings Settings { get; set; } = RoundSettings.Default;

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

	// The face each player's LAST hunter pawn wore, snapshotted host-side as that pawn is destroyed (a role
	// swap). The host can't read a client's saved head off their disk, so without this a client's fresh hunter
	// pawn ships wearing the prefab default and everyone watches it flash until the client's own dress
	// publishes back. Dressing the new pawn from this cache instead means the spawn snapshot already carries
	// the face everyone was just looking at; the owner's on-arrival dress reconciles any drift (and is a
	// content-identical no-op in the normal case).
	readonly Dictionary<Guid, List<SdfBrush>> _lastFaces = new();

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
			_lastFaces.Remove( id ); // a leaver's cached face has nobody to come back for it
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
	/// <summary>Caller swaps to the opposite lobby role — hunter ↔ prop — respawning where they stand.</summary>
	[Rpc.Host]
	public void RequestSwapRole()
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
		SpawnPawn( c, row.Role );
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

		// Don't begin a countdown we can't finish — there must be a map to launch into.
		if ( MapCatalog.Resolve( Settings.MapIdent ) is null )
		{
			Log.Warning( "LobbyManager: can't start — no Prop Hunt Map assets exist. Create one and pick it." );
			return;
		}

		LaunchEndsAt = LobbyController.Current.IsValid() ? LobbyController.Current.LaunchCountdownSeconds : 10f;
		Launching = true;
	}

	// ── Host-side config (the host owns this object, so it can set the synced fields directly) ────────────────
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

	// ── Launch ─────────────────────────────────────────────────────────────────────────────────────────────────
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

	GameObject SpawnPawn( Connection connection, PlayerRole role )
		=> SpawnPawnFor( connection.Id, connection.DisplayName, role, connection );

	// The one spawn path, for players and bots alike. A null <paramref name="owner"/> means a bot: the pawn stays
	// host-owned and is prepared so nobody drives it (RoundBots.Prepare), instead of being handed to a connection.
	GameObject SpawnPawnFor( Guid id, string name, PlayerRole role, Connection owner )
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

		var at = lc.SpotAt( Players.TryGetValue( id, out var row ) ? row.SpawnIndex : 0 );

		// A role swap respawns IN PLACE — where the old pawn stands, not back at the spawn ring. Same buried-origin
		// guard as RoundManager.EnsureOwnPawn: a sculpted prop's origin can sit under the floor, so ground the new
		// pawn on the shape's feet (traced down onto whatever it stood on) rather than the raw origin.
		if ( _pawns.TryGetValue( id, out var previous ) && previous.IsValid() )
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

		// A fresh hunter pawn wears its player's face BEFORE anything builds. Cloned DISABLED for that:
		// SdfSculpture.OnEnabled fires Rebuild, so an enabled clone already has the default face's build in
		// flight before a post-clone dress could swap the brushes — dressing first means the first build ever
		// started is the real head, and the NetworkSpawn below snapshots it for everyone.
		//
		// Where the face comes from depends on whose pawn it is: the host's own comes off this machine's disk
		// (the saved head slot); a CLIENT's comes from the last-worn-face cache (the host can't read their
		// disk, but it was just rendering their previous hunter pawn — see _lastFaces). The client's own
		// on-arrival dress reconciles any drift. A first-ever spawn has no cache entry and dresses owner-side
		// on arrival, same as before.
		var dressLocalHead = role == PlayerRole.Hunter && owner is not null && owner.Id == Connection.Local?.Id;
		var dressCachedFace = role == PlayerRole.Hunter && !dressLocalHead && owner is not null && _lastFaces.ContainsKey( id );

		var pawn = prefab.Clone( new CloneConfig( at, startEnabled: !(dressLocalHead || dressCachedFace), name: $"Lobby Pawn ({role}) {name}" ) );
		if ( !pawn.IsValid() )
			return null;

		if ( dressLocalHead || dressCachedFace )
		{
			if ( dressLocalHead )
				HunterController.WearSavedHead( pawn );
			else
				HunterController.WearFace( pawn, _lastFaces[id] );
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
			RememberFace( id, old );
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

	// Snapshot the face a departing HUNTER pawn wore into _lastFaces (see it for why). Hunter pawns only —
	// ResolveFaceOf on a prop would happily hand back the disguise sculpture, which is not a face. Copied
	// brush-by-brush so nothing that later mutates the (about-to-die) live list can reach the cache.
	void RememberFace( Guid id, GameObject pawn )
	{
		if ( !pawn.Components.Get<HunterController>( includeDisabled: true ).IsValid() )
			return;

		var face = HunterController.ResolveFaceOf( pawn );
		if ( !face.IsValid() || face.Brushes is not { Count: > 0 } )
			return;

		_lastFaces[id] = face.Brushes.Select( b => b.Copy() ).ToList();
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
