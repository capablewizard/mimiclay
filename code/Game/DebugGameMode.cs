using System;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>Which pawn a connection is controlling.</summary>
public enum DebugRole { Hunter, Prop }

/// <summary>
/// Throwaway test harness for blocking out the controllers — host-authoritative so it doubles as our first
/// networking pass. Drop this on a GameObject in the scene, point <see cref="SpawnPoint"/> at where pawns appear,
/// assign the hunter prefab, then hit play.
///
/// <b>Networking</b> (roughly mirrors MiniMotors): with no menu yet, the debug scene hosts its own lobby — the
/// first machine in becomes host (pressing play in the editor makes a one-player host session). The host owns all
/// spawning: it builds one pawn per <see cref="Connection"/> and hands ownership to that connection with
/// <see cref="GameObject.NetworkSpawn(Connection)"/>, so the owning client reads input + drives its camera while
/// every other machine renders it as a synced proxy. Each player presses O/P to respawn THEIR OWN pawn as a
/// hunter/prop; the keypress is routed to the host via <see cref="RequestRole"/> so spawning stays authoritative.
///
/// <b><see cref="EnableNetworking"/> = false</b>: no lobby, no NetworkSpawn — just one local pawn that O/P respawns.
///
/// Replace with a real RoundManager later.
/// </summary>
[Title( "Debug Game Mode" )]
[Category( "Mimiclay" )]
[Icon( "bug_report" )]
public sealed class DebugGameMode : Component
{
	/// <summary>Max lobby connections.</summary>
	public const int MaxPlayers = 8;

	/// <summary>On = host a lobby + network-spawn a pawn per connection (the real path). Off = no networking:
	/// spawn one LOCAL pawn and respawn it with O/P.</summary>
	[Property] public bool EnableNetworking { get; set; } = true;

	/// <summary>Where pawns spawn. Falls back to this component's GameObject when unset.</summary>
	[Property] public GameObject SpawnPoint { get; set; }

	/// <summary>Auto-spawn a pawn on join (networked) / on start (local), so nobody sits in an empty void.</summary>
	[Property] public bool SpawnOnJoin { get; set; } = true;

	/// <summary>The role spawned by default: true = prop (hider), false = hunter.</summary>
	[Property] public bool DefaultToProp { get; set; }

	/// <summary>The pawn prefabs we clone — assign hunter.prefab / prop.prefab here, and edit those assets to tune
	/// the controllers' inspector values.</summary>
	[Property, Group( "Prefabs" )] public GameObject HunterPrefab { get; set; }
	[Property, Group( "Prefabs" )] public GameObject PropPrefab { get; set; }

	// Host-side: the pawn each connection is actively controlling, keyed by Connection.Id.
	readonly Dictionary<Guid, GameObject> _pawns = new();

	// Host-side: connections we've already auto-spawned for, so the join loop only fires once each (and a rejoin
	// re-triggers it). Also marked when a connection explicitly requests a role, so we don't then auto-spawn over it.
	readonly HashSet<Guid> _known = new();

	// Non-networked path: the single local pawn.
	GameObject _localPawn;

	DebugRole DefaultRole => DefaultToProp ? DebugRole.Prop : DebugRole.Hunter;

	protected override void OnStart()
	{
		if ( !EnableNetworking )
		{
			// Local-only: no lobby, just drop a pawn in.
			if ( SpawnOnJoin )
				SpawnLocal( DefaultRole );
			return;
		}

		// No menu yet, so host the lobby right here. Guarded so we never double-host (and so a scene that's already
		// in a session — e.g. a future menu flow — just joins the existing one).
		if ( !Networking.IsActive )
			Networking.CreateLobby( new LobbyConfig { MaxPlayers = MaxPlayers } );
	}

	protected override void OnUpdate()
	{
		if ( !EnableNetworking )
		{
			// Local respawn — no RPC, no NetworkSpawn.
			if ( Input.Keyboard.Pressed( "O" ) ) SpawnLocal( DebugRole.Hunter );
			if ( Input.Keyboard.Pressed( "P" ) ) SpawnLocal( DebugRole.Prop );
			return;
		}

		// Local input on ANY machine: ask the host to (re)spawn this client's pawn in the chosen role. Raw keys so
		// they don't need input-config actions: O = hunter, P = prop.
		if ( Input.Keyboard.Pressed( "O" ) ) RequestRole( DebugRole.Hunter );
		if ( Input.Keyboard.Pressed( "P" ) ) RequestRole( DebugRole.Prop );

		if ( !Networking.IsHost )
			return;

		// Forget gone connections so a rejoin re-triggers an auto-spawn.
		_known.RemoveWhere( id => !Connection.All.Any( c => c.Id == id ) );

		if ( SpawnOnJoin )
		{
			foreach ( var connection in Connection.All )
			{
				if ( _known.Add( connection.Id ) )
					SpawnFor( connection, DefaultRole );
			}
		}
	}

	// Client -> host: the caller wants its pawn (re)spawned in this role. Runs on the host, which owns spawning.
	[Rpc.Host]
	void RequestRole( DebugRole role )
	{
		var caller = Rpc.Caller;
		if ( caller is null )
			return;

		_known.Add( caller.Id ); // so the auto-spawn loop doesn't also drop a default pawn on them
		SpawnFor( caller, role );
	}

	// Host-only. (Re)spawn the connection's pawn in the given role, handing network ownership to that connection.
	void SpawnFor( Connection connection, DebugRole role )
	{
		var pawn = Clone( role, $" {connection.DisplayName}" );
		if ( !pawn.IsValid() )
			return;

		// Retire the connection's previous pawn only once the new one exists. A sculpted prop is LEFT standing as
		// scenery; a hunter is destroyed.
		if ( _pawns.Remove( connection.Id, out var old ) )
			Retire( old );

		// NetworkSpawn(connection) hands ownership to the player: that connection becomes the authority that reads
		// input + drives its camera; everyone else renders it as a synced proxy. Works the same on a from-scratch
		// pawn as on a prefab clone.
		pawn.NetworkSpawn( connection );
		_pawns[connection.Id] = pawn;
	}

	// Non-networked: (re)spawn the single local pawn.
	void SpawnLocal( DebugRole role )
	{
		var pawn = Clone( role, "" );
		if ( !pawn.IsValid() )
			return;

		Retire( _localPawn );
		_localPawn = pawn;
	}

	// Clone the role's prefab at the spawn point. Cloned AT the transform (not after) so the pawn's OnAwake already
	// sees its spawn position/rotation — the hider seeds its camera yaw from WorldRotation there.
	GameObject Clone( DebugRole role, string nameSuffix )
	{
		var prefab = role == DebugRole.Prop ? PropPrefab : HunterPrefab;
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"No {role} prefab assigned on DebugGameMode — can't spawn." );
			return null;
		}

		var origin = SpawnPoint ?? GameObject;
		var transform = new Transform(
			origin.WorldPosition + Vector3.Up * 64f,
			Rotation.FromYaw( origin.WorldRotation.Yaw() ) );

		return prefab.Clone( transform, name: $"Pawn ({role}){nameSuffix}" );
	}

	// A sculpted prop is LEFT standing in the level as scenery; a hunter is just destroyed. For a hider we hand the
	// prop OFF the player: ReleaseControl stops it taking input + driving the camera (marks it dormant), and — when
	// networked — the host DROPS the player's ownership so it becomes server-controlled. That drop is the crucial
	// bit: ReleaseControl only sets a local, non-networked flag on the HOST's copy, so without it the prop stays
	// owned by (and therefore still controlled on) the client that spawned it — a respawned player would drive BOTH
	// their new pawn and the old one. Once ownership is dropped, the prop becomes a proxy on that client (the
	// IsProxy gating silences its input/camera/physics) while the host simulates it settling in place.
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
