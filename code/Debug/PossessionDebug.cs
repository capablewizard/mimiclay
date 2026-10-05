using System.Collections.Generic;
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

	/// <summary>Sculpt the claimable clay nearest the LOCAL hunter in place (the first-person edit lease — the
	/// same as aiming at it and pressing F). Works solo on a creative map; a non-creative mode refuses leases.</summary>
	[ConCmd( "mimi_dbg_lease" )]
	static void Lease()
	{
		var claims = PropClaims.Current;
		var me = Connection.Local?.Id;
		var hunter = Game.ActiveScene.GetAllComponents<HunterController>()
			.FirstOrDefault( h => !h.IsProxy && !h.Bot && RoundManager.RosterIdOf( h.GameObject ) == me );
		if ( !claims.IsValid() || !hunter.IsValid() )
		{
			Log.Warning( "mimi_dbg_lease: needs a claim service and a local hunter pawn." );
			return;
		}

		var target = Game.ActiveScene.GetAllComponents<SdfSculpture>()
			.Where( PropClaims.IsSculptable )
			.OrderBy( s => s.WorldPosition.Distance( hunter.WorldPosition ) )
			.FirstOrDefault();
		if ( !target.IsValid() )
		{
			Log.Warning( "mimi_dbg_lease: nothing claimable." );
			return;
		}

		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		var root = hider.IsValid() ? hider.GameObject : target.GameObject;
		Log.Info( $"mimi_dbg_lease: sculpting '{root.Name}' in place ({target.WorldPosition.Distance( hunter.WorldPosition ):0}u away)" );
		hunter.RequestSculptEdit( root );
	}

	/// <summary>Host test of the shared-edit op path without a second keyboard: as the FIRST CLIENT, join the
	/// nearest editable prop (the one the host is editing, if any) and apply one op — the first authored brush
	/// nudged +8 on z — exactly as if that client's session had committed it. Exercises the editing registry,
	/// the lock check, the bounds gate, the host apply and the publish to every machine.</summary>
	[ConCmd( "mimi_dbg_remoteop" )]
	static void RemoteOp()
	{
		var claims = PropClaims.Current;
		var client = FirstClient;
		if ( !claims.IsValid() || client is null )
		{
			Log.Warning( "mimi_dbg_remoteop: needs a claim service and a connected client." );
			return;
		}

		// The prop: whatever the host is editing, else the nearest claimable pawn prop to the client's hunter.
		HiderController prop = null;
		if ( SculptEditSession.Current is { IsEditing: true, Shared: true } s && s.Target.IsValid() )
			prop = s.Target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( !prop.IsValid() )
		{
			var hunter = Game.ActiveScene.GetAllComponents<HunterController>()
				.FirstOrDefault( h => h.Network.Owner?.Id == client.Id );
			prop = Game.ActiveScene.GetAllComponents<HiderController>()
				.Where( h => PropClaims.IsReleased( h ) )
				.OrderBy( h => hunter.IsValid() ? h.WorldPosition.Distance( hunter.WorldPosition ) : 0f )
				.FirstOrDefault();
		}
		if ( !prop.IsValid() || prop.DisguiseSculpture is not { } sculpt || sculpt.Brushes is not { Count: > 0 } brushes )
		{
			Log.Warning( "mimi_dbg_remoteop: no released prop to edit." );
			return;
		}

		claims.EditFor( client, prop.GameObject, ignoreReach: true ); // a test op from across the room is fine
		if ( !PropClaims.IsEditedBy( prop, client.Id ) )
		{
			Log.Warning( $"mimi_dbg_remoteop: {client.DisplayName} couldn't join '{prop.GameObject.Name}' (not a hunter in reach?)." );
			return;
		}

		var nudged = brushes[0].Copy();
		nudged.Position += Vector3.Up * 8f;
		var packed = SdfNetworkSync.Pack( SdfSculpture.SerializeBrushes( new List<SdfBrush> { nudged } ) );
		Log.Info( $"mimi_dbg_remoteop: {client.DisplayName} nudges brush {nudged.Id} of '{prop.GameObject.Name}' to {nudged.Position}" );
		claims.DebugApplyOps( client, prop.GameObject, packed, commit: true );
		Log.Info( $"mimi_dbg_remoteop: brush now at {sculpt.Brushes?.FirstOrDefault( b => b.Id == nudged.Id )?.Position} (host copy — a new list, so re-read), edited={PropClaims.IsBeingEdited( prop )} locks={claims.BrushLocks.Count}" );
	}

	/// <summary>The first client stops editing (the counterpart to <c>mimi_dbg_remoteop</c>'s join).</summary>
	[ConCmd( "mimi_dbg_remoteend" )]
	static void RemoteEnd()
	{
		var claims = PropClaims.Current;
		var client = FirstClient;
		if ( !claims.IsValid() || client is null )
			return;
		claims.EndEditFor( client, pop: true );
		Log.Info( $"mimi_dbg_remoteend: {client.DisplayName} stopped editing (rows={claims.Editing.Count}, locks={claims.BrushLocks.Count})" );
	}

	/// <summary>Leave whatever sculpt session this machine is in (forced, no dialog) — the lease's exit path
	/// without a Q press, for editor-driven tests.</summary>
	[ConCmd( "mimi_dbg_unlease" )]
	static void Unlease()
	{
		var session = SculptEditSession.Current;
		if ( !session.IsValid() || !session.IsEditing )
		{
			Log.Warning( "mimi_dbg_unlease: no edit session is running." );
			return;
		}

		Log.Info( $"mimi_dbg_unlease: closing the session on '{session.Target?.GameObject.Name}' (first person: {session.FirstPerson})" );
		session.SetActive( false );
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
			Log.Info( $"[prop] {hider.GameObject.Name} released={released} possessed={possessed} edited={PropClaims.IsBeingEdited( hider )} bornScenery={hider.BornScenery} pos={hider.WorldPosition} " +
				$"vel={(body.IsValid() ? body.Velocity : default)} motion={(body.IsValid() && body.MotionEnabled)} sleeping={(body.IsValid() && body.Sleeping)} " +
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
