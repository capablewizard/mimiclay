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
/// on the host rather than frozen on its last good stage. Tiny traffic, so it's left on in real sessions too —
/// and real sessions need it: <see cref="LoadGate"/> reads the <see cref="ClientStatus.Settled"/> flag to hold a
/// game's start until everyone has loaded.
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
	RealTimeSince _sinceBusy;
	int _lastHash;

	public ClientStatusReporter( Scene scene ) : base( scene )
	{
		// Start the settle clock at zero. A default RealTimeSince counts from time 0, so it reads as long since
		// elapsed: a map with nothing to bake (charades) reported Settled on its very first sample, skipping the
		// hold that covers props not having queued their builds yet.
		_sinceBusy = 0;

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

		int pending = PendingSdfWork( Scene );
		bool hasPawn = Scene.GetAllComponents<HunterController>().Any( c => !c.IsProxy )
			|| Scene.GetAllComponents<HiderController>().Any( c => !c.IsProxy );

		var stage = Classify( pending, hasPawn );
		var phase = RoundContext.Active?.Phase.ToString() ?? "";
		var scene = Scene.Name ?? "";

		// Settled = the map is loaded, the game manager is here, and no SDF work has been outstanding for a
		// while. The pawn is deliberately NOT part of it: LoadGate waits on this BEFORE the round deals roles,
		// so no pawn can exist yet. The hold is there because the pending counters read 0 for a moment after
		// the load, before the props have queued their first builds.
		if ( stage is ClientStage.LoadingScene or ClientStage.WaitingForGame || pending > 0 )
			_sinceBusy = 0;
		bool settled = _sinceBusy >= LoadGate.SettleSeconds;
		var manager = ManagerId();

		int hash = HashCode.Combine( stage, phase, scene, pending, hasPawn, settled, manager );
		if ( hash == _lastHash && _sinceSent < HeartbeatSeconds )
			return;

		_lastHash = hash;
		_sinceSent = 0;
		Report( (int)stage, scene, phase, pending, hasPawn, settled, manager );
	}

	// Which game manager this machine has, as its networked GameObject id: the same on every machine and new
	// on every visit to a map, so the host's LoadGate can tell a report from THIS map apart from a leftover
	// lobby one. (Scene names can't do that: the host's reads "system", a client's the default "Scene".)
	static Guid ManagerId()
	{
		if ( RoundManager.Current.IsValid() ) return RoundManager.Current.GameObject.Id;
		if ( CharadesManager.Current.IsValid() ) return CharadesManager.Current.GameObject.Id;
		if ( CreativeManager.Current.IsValid() ) return CreativeManager.Current.GameObject.Id;
		if ( LobbyManager.Current.IsValid() ) return LobbyManager.Current.GameObject.Id;
		return Guid.Empty;
	}

	/// <summary>SDF work still outstanding in <paramref name="scene"/> on this machine: mesh builds queued or
	/// running, plus raymarched shapes whose first field bake hasn't landed.</summary>
	public static int PendingSdfWork( Scene scene )
		=> SdfSculpture.BuildsInFlight + scene.GetAllComponents<SdfRaymarchRenderer>().Count( r => r.FieldPending );

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
	static void Report( int stage, string scene, string phase, int pendingProps, bool hasPawn, bool settled, Guid manager )
	{
		var caller = Rpc.Caller;
		if ( caller is null )
			return;

		ClientStatusBoard.Store( caller.Id, new ClientStatus( (ClientStage)stage, scene, phase, pendingProps, hasPawn, settled, manager, RealTime.Now ) );
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

/// <summary>One client's latest self-report. <see cref="At"/> is host <see cref="RealTime.Now"/> on arrival.
/// <see cref="Settled"/>: loaded, manager present, and no SDF work pending for <see cref="LoadGate.SettleSeconds"/>.
/// <see cref="Manager"/>: the networked GameObject id of the game manager the client has (empty if none).</summary>
public readonly record struct ClientStatus( ClientStage Stage, string Scene, string Phase, int PendingProps, bool HasPawn, bool Settled, Guid Manager, double At );

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
