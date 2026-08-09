using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Converts the map's scene-placed SDF props into host-owned NETWORKED physics props at round start, so every
/// prop in the world reacts when hunters grab and throw things (<see cref="PropGrabber"/>).
///
/// Why conversion is needed at all: scene-placed objects are NetworkMode.Snapshot — their transform changes
/// don't replicate, and a Rigidbody on one would simulate INDEPENDENTLY on every machine (see
/// <c>Rigidbody.ShouldSimulatePhysics</c>) and drift apart. And you can't NetworkSpawn a scene object (the
/// remote-create duplicates it on clients) — the same constraint that shaped <see cref="RoundManagerSpawner"/>.
/// So the host CLONES each prop (the clone's spawn snapshot carries the live brush JSON, so clients rebuild the
/// identical sculpture), gives the clone a root <see cref="Rigidbody"/> + <see cref="GrabbableProp"/>, and
/// NetworkSpawns it; the scene originals are disabled on every machine. Bodies start asleep, so an untouched
/// map costs nothing and nothing pops at load.
///
/// Runs once per scene, on every machine, gated to playable scenes (a <see cref="RoundManagerSpawner"/> or a
/// <see cref="LobbyController"/> is present) once the session is up. Clients normally receive the host's scene ALREADY converted (a scene change streams
/// the host's live scene, disabled originals and network clones included); the local disable pass is the
/// belt-and-braces for originals that slipped through enabled. A GameObjectSystem so no scene wiring is needed
/// — editor-guarded, since systems tick in the editor too.
///
/// Prop identification: every enabled <see cref="SdfCollider"/>'s ROOT object (map props are top-level scene
/// objects; multi-part props like the well are one root whose children carry the colliders — one compound body).
/// Pawns are excluded by their controllers, already-converted clones by the <see cref="GrabbableProp"/> marker.
/// TempProps scenery has no SdfCollider and stays static.
/// </summary>
public sealed class MapPropPhysics : GameObjectSystem
{
	bool _done;

	public MapPropPhysics( Scene scene ) : base( scene )
	{
		// Fixed update, before physics ever steps a prop: conversion swaps bodies in, so it must not race a
		// frame where the originals are collidable and the clones are too.
		Listen( Stage.StartFixedUpdate, -10, Convert, "MapPropPhysics" );
	}

	void Convert()
	{
		if ( _done || Scene.IsEditor )
			return;

		// Playable scenes only: map scenes (RoundManagerSpawner) and the lobby (LobbyController) — both spawn
		// hunter pawns that can grab, and the lobby's shelf props ALREADY carried patched-in Rigidbodies that
		// were silently simulating per-machine as Snapshot objects (drift); conversion is what makes them
		// actually shared. Menu / debug scenes keep their authored props untouched.
		if ( !RoundManagerSpawner.Current.IsValid() && !LobbyController.Current.IsValid() )
			return;

		// Wait for the session: on a direct editor Play the spawner/lobby self-hosts within a frame, and a
		// client mid-scene-change just isn't converted yet (its snapshot arrives converted anyway).
		if ( !Networking.IsActive )
			return;

		_done = true;

		// Collect distinct prop roots first — conversion mutates the scene, so never enumerate live.
		var roots = new List<GameObject>();
		var seen = new HashSet<GameObject>();
		foreach ( var collider in Scene.GetAllComponents<SdfCollider>() )
		{
			var root = collider.GameObject.Root;
			if ( !root.IsValid() || !seen.Add( root ) )
				continue;

			// Pawns own their colliders (disguise bodies, hunter heads) — never map props.
			if ( root.Components.Get<HiderController>( FindMode.EverythingInSelfAndDescendants ).IsValid() )
				continue;
			if ( root.Components.Get<HunterController>( FindMode.EverythingInSelfAndDescendants ).IsValid() )
				continue;

			// Already-converted clones (this machine's or ones that arrived over the wire) are real network
			// objects — Network.Active is only ever true for NetworkSpawn'd objects, never for scene-snapshot
			// ones, so it's the reliable discriminator. Deliberately NOT a GrabbableProp check: that component
			// shows up in the editor and gets hand-authored onto scene props (the lobby trophy/alarmclock),
			// and skipping on it silently un-converted exactly the props someone marked as grabbable.
			if ( root.Network is not null && root.Network.Active )
				continue;

			roots.Add( root );
		}

		foreach ( var original in roots )
		{
			if ( Networking.IsHost )
			{
				var clone = original.Clone( original.WorldTransform, null, true, original.Name );

				// Root body so every child collider aggregates into ONE compound prop. Adopt an authored one
				// first — a few scene props already carry a patched-in Rigidbody (chair3/footstool in the
				// bedroom) and a second body on the same root would be a mess. Otherwise created DISABLED so
				// StartAsleep is set before OnEnabled reads it (a Create-then-set would miss — the sleep is
				// applied in OnEnabled): asleep until touched means no settle-pop at load and no idle cost.
				// Impact damage off: thrown props knocking chunks off players is not this game's damage model
				// (the gun is).
				var body = clone.Components.Get<Rigidbody>( FindMode.EverythingInSelf );
				if ( !body.IsValid() )
					body = clone.Components.Create<Rigidbody>( false );
				body.StartAsleep = true;
				body.EnableImpactDamage = false;
				body.Enabled = true;

				// GetOrCreate: a scene prop can carry a hand-authored GrabbableProp (harmless on the original —
				// it's inert without a Rigidbody — but the clone must not end up with two).
				clone.Components.GetOrCreate<GrabbableProp>();

				// Re-bind the colliders now the body exists — the HiderController lesson: a collider that
				// enabled before its Rigidbody existed bound as a keyframe, and the dynamic body has no shape.
				// Rigidbody.OnEnabled broadcasts a re-bind, but the off/on toggle is the proven belt-and-braces.
				foreach ( var mc in clone.Components.GetAll<ModelCollider>( FindMode.EverythingInSelfAndDescendants ).ToArray() )
				{
					mc.Enabled = false;
					mc.Enabled = true;
				}

				// The rebind can wake the body (adding a collider syncs the body transform) — re-assert the
				// load-time sleep now the shapes are in place.
				body.Sleeping = true;

				clone.NetworkSpawn(); // host owns it; snapshot ships brushes + components to every client
			}

			original.Enabled = false;
		}

		if ( roots.Count > 0 )
			Log.Info( $"MapPropPhysics: converted {roots.Count} scene props to networked physics props" );
	}
}
