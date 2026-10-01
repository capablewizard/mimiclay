using System.Linq;

namespace Mimiclay;

/// <summary>
/// Host-side console tooling for reproducing possession bugs with a local client instance you can't drive:
/// claim / release ON BEHALF of the first remote connection, and dump the state of every released prop.
/// Lobby only (release goes through the lobby's role swap).
/// </summary>
internal static class PossessionDebug
{
	static Connection FirstClient => Connection.All.FirstOrDefault( c => !c.IsHost );

	/// <summary>Claim the scenery prop nearest the first client's pawn, as that client.</summary>
	[ConCmd( "mimi_dbg_claim" )]
	static void Claim()
	{
		var claims = PropClaims.Current;
		var client = FirstClient;
		var lm = LobbyManager.Current;
		if ( !claims.IsValid() || client is null || !lm.IsValid() )
		{
			Log.Warning( "mimi_dbg_claim: needs a lobby, a claim service and a connected client." );
			return;
		}

		var hunter = Game.ActiveScene.GetAllComponents<HunterController>()
			.FirstOrDefault( h => h.Network.Owner?.Id == client.Id );
		if ( !hunter.IsValid() )
		{
			Log.Warning( "mimi_dbg_claim: the client has no hunter pawn." );
			return;
		}

		var target = Game.ActiveScene.GetAllComponents<SdfSculpture>()
			.Where( s => PropClaims.IsScenery( s ) && PropClaims.IsClaimable( s ) )
			.OrderBy( s => s.WorldPosition.Distance( hunter.WorldPosition ) )
			.FirstOrDefault();
		if ( !target.IsValid() )
		{
			Log.Warning( "mimi_dbg_claim: no claimable scenery." );
			return;
		}

		Log.Info( $"mimi_dbg_claim: {client.DisplayName} claims '{target.GameObject.Name}' at {target.WorldPosition} ({target.WorldPosition.Distance( hunter.WorldPosition ):0}u away)" );
		claims.PossessFor( client, target.GameObject );
	}

	/// <summary>The first client swaps back to a hunter (releasing a possessed prop).</summary>
	[ConCmd( "mimi_dbg_release" )]
	static void Release()
	{
		var client = FirstClient;
		var lm = LobbyManager.Current;
		if ( client is null || !lm.IsValid() )
			return;

		Log.Info( $"mimi_dbg_release: {client.DisplayName} swaps back" );
		lm.SwapRoleFor( client, 0f );
	}

	/// <summary>Dump every released / possessed prop pawn: where it is, how it moves, who drives it.</summary>
	[ConCmd( "mimi_dbg_props" )]
	static void Props()
	{
		foreach ( var hider in Game.ActiveScene.GetAllComponents<HiderController>() )
		{
			var released = PropClaims.IsReleased( hider );
			var possessed = PropClaims.IsPossessed( hider );
			if ( !released && !possessed )
				continue;

			var body = hider.Components.Get<Rigidbody>();
			var disguise = hider.DisguiseSculpture;
			var colliders = hider.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ).ToList();
			Log.Info( $"[prop] {hider.GameObject.Name} released={released} possessed={possessed} pos={hider.WorldPosition} " +
				$"vel={(body.IsValid() ? body.Velocity : default)} motion={(body.IsValid() && body.MotionEnabled)} " +
				$"proxy={hider.IsProxy} owner={hider.GameObject.Network.Owner?.DisplayName ?? "none"} " +
				$"brushes={disguise?.Brushes?.Count ?? -1} colliders={colliders.Count}({colliders.Count( c => c.Enabled )} on) " +
				$"tags=[{string.Join( ",", (disguise.IsValid() ? disguise.GameObject.Tags : hider.GameObject.Tags).TryGetAll() )}]" );

			foreach ( var child in hider.GameObject.Children )
			{
				var net = child.Network;
				Log.Info( $"    child '{child.Name}' mode={child.NetworkMode} active={net.Active} owner={net.Owner?.DisplayName ?? "none"} " +
					$"root={(net.RootGameObject == hider.GameObject ? "pawn" : net.RootGameObject?.Name ?? "null")} orphaned={net.NetworkOrphaned}" );
			}
		}
	}
}
