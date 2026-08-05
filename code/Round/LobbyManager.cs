using System;
using System.Collections.Generic;
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

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	// Most hunters we'll allow — always one fewer than the lobby size, so there's at least one prop. (Solo in the
	// editor it floors at 1, since one person can't be both.)
	int MaxHunters => Math.Max( 1, Connection.All.Count - 1 );

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

		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Connection.All.Any( c => c.Id == id ) )
				continue;
			Players.Remove( id );
			if ( _pawns.Remove( id, out var pawn ) )
				Retire( pawn );
		}
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
			Log.Warning( $"LobbyManager: no {role} prefab assigned — can't spawn for {connection.DisplayName}." );
			return null;
		}

		var at = lc.SpotAt( Players.TryGetValue( connection.Id, out var row ) ? row.SpawnIndex : 0 );

		// A role swap respawns IN PLACE — where the old pawn stands, not back at the spawn ring. Same buried-origin
		// guard as RoundManager.EnsureOwnPawn: a sculpted prop's origin can sit under the floor, so ground the new
		// pawn on the shape's feet (traced down onto whatever it stood on) rather than the raw origin.
		if ( _pawns.TryGetValue( connection.Id, out var previous ) && previous.IsValid() )
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

		var pawn = prefab.Clone( at, name: $"Lobby Pawn ({role}) {connection.DisplayName}" );
		if ( !pawn.IsValid() )
			return null;

		// The new pawn takes over the old one's exact spot, so the old pawn goes away entirely. Releasing it as
		// scenery (the Retire path, kept for disconnects) would leave the fresh pawn spawning inside the released
		// disguise's collider — the solver shoves overlapping hulls apart, sometimes through the floor.
		if ( _pawns.Remove( connection.Id, out var old ) && old.IsValid() )
			old.Destroy();

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
