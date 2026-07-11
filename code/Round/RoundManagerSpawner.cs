using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Drop this in each map scene. On the HOST it creates the real, networked <see cref="RoundManager"/> and steps
/// aside; clients just receive that manager over the wire.
///
/// Why a spawner instead of placing RoundManager directly: a scene-placed component's <c>[Sync]</c> CHANGES don't
/// replicate to clients here, but a <see cref="GameObject.NetworkSpawn()"/>'d object (NetworkMode.Object) syncs
/// properly — incl. to late-joiners via the spawn snapshot. You can't NetworkSpawn a scene object (it'd duplicate
/// on clients), so the manager has to be created in code. This mirrors how MiniMotors spawns its networked
/// singletons (LobbyFlow etc.). This spawner holds the per-map config the manager needs.
/// </summary>
[Title( "Round Manager Spawner" )]
[Category( "Mimiclay" )]
[Icon( "sports_esports" )]
public sealed class RoundManagerSpawner : Component
{
	/// <summary>The scene's spawner. Scene-placed, so it (and its prefab refs) exists on EVERY machine — unlike the
	/// NetworkSpawn'd RoundManager, whose [Property] prefabs only exist on the host. Every machine now spawns its OWN
	/// pawn, so it reads the prefabs from here. Set while enabled.</summary>
	public static RoundManagerSpawner Current { get; private set; }

	[Property, Group( "Prefabs" )] public GameObject HunterPrefab { get; set; }
	[Property, Group( "Prefabs" )] public GameObject PropPrefab { get; set; }

	/// <summary>One-shot smoke burst played where a prop is found, covering the swap to a hunter pawn (the
	/// "substitution" poof). Cloned locally on every machine by <see cref="RoundManager.PlayCaughtPuff"/>.</summary>
	[Property, Group( "Prefabs" )] public GameObject CaughtPuffPrefab { get; set; }

	[Property, Group( "Scoring" )] public int FindReward { get; set; } = 50;
	[Property, Group( "Scoring" )] public float PropPointsPerSecond { get; set; } = 1f;

	/// <summary>DEBUG: spawn everyone as a prop with an endless Hide phase (no hunters, no progression) — see
	/// <see cref="RoundManager.DebugSoloHide"/>. Tick for solo disguise testing; leave off for real play.</summary>
	[Property, Group( "Debug" )] public bool DebugSoloHide { get; set; }

	protected override void OnEnabled() => Current = this;
	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
	}

	// Seconds we tolerate "no session" before assuming it's a genuine direct Play (and self-host). A client FOLLOWING
	// the host's scene change is briefly !IsActive during the swap — self-hosting over that window makes the client
	// spawn its OWN local manager (which then never syncs). The grace lets a following client reconnect first.
	const float SelfHostGrace = 1f;

	bool _done;
	RealTimeSince _sinceStart;

	protected override void OnStart()
	{
		_sinceStart = 0;
	}

	protected override void OnUpdate()
	{
		if ( _done )
			return;

		// No session at all? Either direct Play on this map scene, or a client mid-scene-swap. Wait out the grace
		// before self-hosting, so a following client (which reconnects within a frame or two) is never self-hosted.
		if ( !Networking.IsActive )
		{
			if ( _sinceStart < SelfHostGrace )
				return;

			Networking.CreateLobby( new LobbyConfig { MaxPlayers = 8 } );
		}

		// Only the host creates the networked manager; a real client just receives it over the wire and is done.
		if ( !Networking.IsHost )
		{
			_done = true;
			return;
		}

		if ( !RoundManager.Current.IsValid() )
		{
			var go = new GameObject( true, "Round Manager" );
			var rm = go.Components.Create<RoundManager>();
			// Only host-side scoring config is copied onto the (host-only) manager; the pawn prefabs are read live off
			// this scene-placed spawner by every machine (RoundManager.PrefabFor) — clients' manager copies wouldn't
			// carry [Property] refs.
			rm.FindReward = FindReward;
			rm.PropPointsPerSecond = PropPointsPerSecond;
			rm.DebugSoloHide = DebugSoloHide;

			go.NetworkSpawn(); // host owns it; replicates to every client (and late-joiners) with working [Sync]
		}

		_done = true;
	}
}
