using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Every joined client tells the host how far through loading it is, so the editor's playtest launcher can show
/// per-client progress past the engine's own "connected" point (see Editor/PlaytestDock.cs). The stages are the
/// ones our joins actually stall in: the scene load itself, waiting for the host's networked game manager to
/// arrive, the local pawn, and SDF props still baking/meshing.
///
/// Client side only reports; the host keeps the latest report per connection in <see cref="ClientStatusBoard"/>.
/// Sends on change plus a slow heartbeat, so a client that goes quiet (frozen, mid-load, dropped) shows as stale
/// on the host rather than frozen on its last good stage. Tiny traffic, so it's left on in real sessions too.
///
/// A GameObjectSystem like <see cref="DeadSessionWatchdog"/>: present in every scene with no wiring. Only the
/// active game scene reports — editor scenes and the private render scenes inside thumbnail panels get a copy
/// of the system too (see [[gameobjectsystem-ticks-in-editor]]).
/// </summary>
public sealed class ClientStatusReporter : GameObjectSystem
{
	const float SampleSeconds = 0.5f;
	const float HeartbeatSeconds = 2f;

	RealTimeSince _sinceSample;
	RealTimeSince _sinceSent;
	int _lastHash;

	public ClientStatusReporter( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 20, Tick, "ClientStatusReporter" );
	}

	void Tick()
	{
		if ( Scene is null || Scene.IsEditor || !Networking.IsActive || Networking.IsHost )
			return;

		// Every Scene gets its own copy of each GameObjectSystem — including the private render scenes inside
		// SdfThumbnail panels (roster icons). Those have no pawn, so without this they'd report "spawning pawn"
		// on their own heartbeat and the host would see the stage flip-flop against the real scene's "ready".
		// Neither obvious test works: ScenePanel ticks its scene inside RenderScene.Push(), so ActiveScene always
		// equals the ticking scene; and WantsSystemScene is false on the lobby scenes too, which silenced the lobby.
		if ( SdfThumbnail.IsThumbnailScene( Scene ) )
			return;

		if ( _sinceSample < SampleSeconds )
			return;
		_sinceSample = 0;

		int pending = SdfSculpture.BuildsInFlight + Scene.GetAllComponents<SdfRaymarchRenderer>().Count( r => r.FieldPending );
		bool hasPawn = Scene.GetAllComponents<HunterController>().Any( c => !c.IsProxy )
			|| Scene.GetAllComponents<HiderController>().Any( c => !c.IsProxy );

		var stage = Classify( pending, hasPawn );
		var phase = RoundContext.Active?.Phase.ToString() ?? "";
		var scene = Scene.Name ?? "";

		int hash = HashCode.Combine( stage, phase, scene, pending, hasPawn );
		if ( hash == _lastHash && _sinceSent < HeartbeatSeconds )
			return;

		_lastHash = hash;
		_sinceSent = 0;
		Report( (int)stage, scene, phase, pending, hasPawn );
	}

	ClientStage Classify( int pending, bool hasPawn )
	{
		if ( Scene.IsLoading || LoadingScreen.IsVisible )
			return ClientStage.LoadingScene;

		bool manager = LobbyManager.Current.IsValid() || RoundManager.Current.IsValid()
			|| CharadesManager.Current.IsValid() || CreativeManager.Current.IsValid();
		if ( !manager )
			return ClientStage.WaitingForGame;

		if ( !hasPawn )
			return ClientStage.WaitingForPawn;

		return pending > 0 ? ClientStage.BuildingProps : ClientStage.Ready;
	}

	[Rpc.Host]
	static void Report( int stage, string scene, string phase, int pendingProps, bool hasPawn )
	{
		var caller = Rpc.Caller;
		if ( caller is null )
			return;

		ClientStatusBoard.Store( caller.Id, new ClientStatus( (ClientStage)stage, scene, phase, pendingProps, hasPawn, RealTime.Now ) );
	}

	/// <summary>Host → one client: leave the way a player does (pause menu → Main Menu). Call inside
	/// <c>Rpc.FilterInclude( connection )</c> so only that client runs it.</summary>
	[Rpc.Broadcast]
	public static void RequestLeave()
	{
		if ( Networking.IsHost )
			return;

		Log.Info( "ClientStatusReporter: the host asked this client to leave — exiting to the menu." );
		MenuNetworking.ExitToMenu();
	}
}

public enum ClientStage
{
	LoadingScene,
	WaitingForGame,
	WaitingForPawn,
	BuildingProps,
	Ready,
}

/// <summary>One client's latest self-report. <see cref="At"/> is host <see cref="RealTime.Now"/> on arrival.</summary>
public readonly record struct ClientStatus( ClientStage Stage, string Scene, string Phase, int PendingProps, bool HasPawn, double At );

/// <summary>Host-side store of <see cref="ClientStatusReporter"/> reports, keyed by <see cref="Connection.Id"/>.
/// Main-thread only (RPCs are dispatched there, and the editor reads it from its frame tick).</summary>
public static class ClientStatusBoard
{
	static readonly Dictionary<Guid, ClientStatus> _reports = new();

	internal static void Store( Guid connection, ClientStatus status ) => _reports[connection] = status;

	public static bool TryGet( Guid connection, out ClientStatus status ) => _reports.TryGetValue( connection, out status );

	/// <summary>Drop reports for connections that are no longer on the server.</summary>
	public static void Prune()
	{
		if ( _reports.Count == 0 )
			return;

		var live = Connection.All.Select( c => c.Id ).ToHashSet();
		foreach ( var id in _reports.Keys.Where( k => !live.Contains( k ) ).ToList() )
			_reports.Remove( id );
	}
}
