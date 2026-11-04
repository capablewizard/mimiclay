using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay.Sim;

// Followers + presentation (Phase 1).
//
// FOLLOWERS: every MOVING scene body that isn't itself simulated here — players' props, hunters, held props, doors,
// anything with a Rigidbody — gets a copy in the sim that chases the scene body's pose each tick, so it pushes and
// blocks sim bodies the way it does in the scene. Local to each machine: never networked, never rolled back (in a
// replay it simply keeps chasing where the scene body is now). Two kinds:
//  • a PUPPET (anything with a Rigidbody: pawns, hunters, loose props) — a DYNAMIC body of the same mass, steered by
//    velocity to the target, so its push is mass-aware: a light prop can't bulldoze a heavy decoy. Keyframed followers
//    shove with infinite force, which would push a decoy harder than the same walk pushes a player's prop in the scene.
//  • KEYFRAMED (no Rigidbody: doors, moving scenery) — moved to the target with velocity, like the engine moves a proxy.
//
// PRESENTATION: bodies flagged Present drive their scene object's transform from VisualPose, every machine, every frame
// (only when it changed). Their scene collider follows: a keyframed Rigidbody if the object has one (its physics body is
// placed directly — on a client the object is a proxy, and a proxy's Rigidbody never follows its own transform), else the
// static collider moves with the object.
public sealed partial class SimWorld
{
	const int FollowerRefreshTicks = 10;
	const float FarBelow = -50000f; // the move-hull scratch bodies park down here (PropMoveHull) — never follow those
	const float PuppetMaxSpeed = 4000f;

	sealed class Follower
	{
		public PhysicsBody Source;
		public PhysicsBody Copy;
		public bool Puppet;
		public int ShapeCount;
		public int Version;
		public string TagKey;
	}

	readonly Dictionary<PhysicsBody, Follower> _followers = new();

	[ConVar( "mimi_sim_followers", Help = "Sim: copy every moving scene body into the sim as a follower (Phase 1)." )]
	public static bool UseFollowers { get; set; } = true;

	// OFF by default (2026-11-04): a puppet is a dynamic body steered back to its scene pose every tick at up to
	// PuppetMaxSpeed — walk into one and it shoves back with huge, solver-dependent force, and followers aren't rolled back,
	// so the host and every client replay got a different shove: "walking into other props ejects me" (84 corrections in
	// 5 s, up to 47u). Keyframed followers block (infinitely heavy) but do the same thing in every replay.
	[ConVar( "mimi_sim_puppets", Help = "Sim: followers of Rigidbody objects are mass-matched dynamic puppets (1) instead of keyframed (0)." )]
	public static bool UsePuppets { get; set; } = false;

	public int FollowerCount => _followers.Count;

	// Scene objects kept out of the followers on this machine: the hunter carrying a sim-held prop (HiderController.Sim) —
	// the held body must never collide with the hand holding it (the scene filters those two with PropClaims.FilterAgainst).
	readonly HashSet<Guid> _excludedFollowers = new();

	/// <summary>Leave this object's scene bodies out of the followers (or let them back in on the next refresh).</summary>
	public void ExcludeFollower( GameObject go, bool exclude )
	{
		if ( !go.IsValid() )
			return;
		if ( exclude ? !_excludedFollowers.Add( go.Id ) : !_excludedFollowers.Remove( go.Id ) )
			return;
		if ( !exclude )
			return;
		foreach ( var (src, f) in _followers.ToList() )
		{
			if ( src.IsValid() && src.GameObject.IsValid() && src.GameObject.Id == go.Id )
			{
				f.Copy.Remove();
				_followers.Remove( src );
			}
		}
	}

	void RefreshFollowers()
	{
		if ( _world is null )
			return;
		if ( !UseFollowers )
		{
			ClearFollowers();
			return;
		}

		var seen = new HashSet<PhysicsBody>();
		foreach ( var b in Scene.PhysicsWorld.Bodies.ToList() )
		{
			if ( !b.IsValid() || b.BodyType == PhysicsBodyType.Static || b.ShapeCount == 0 || !b.Enabled )
				continue;
			var go = b.GameObject;
			if ( !go.IsValid() || _excludedStatics.Contains( go.Id ) || _excludedFollowers.Contains( go.Id ) || b.Position.z < FarBelow )
				continue;
			if ( !b.Shapes.Any( s => !s.IsTrigger ) )
				continue;
			seen.Add( b );

			int version = go.Components.Get<SdfCollider>() is { } sdf && sdf.IsValid() ? sdf.BuildSerial : 0;
			string tags = TagKeyOf( b );
			if ( _followers.TryGetValue( b, out var f ) )
			{
				bool wantPuppet = UsePuppets && go.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndAncestors ).IsValid();
				if ( f.ShapeCount == b.ShapeCount && f.Version == version && f.TagKey == tags && f.Puppet == wantPuppet )
					continue;
				f.Copy.Remove();
				_followers.Remove( b );
			}

			bool puppet = UsePuppets && go.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndAncestors ).IsValid();
			var copy = new PhysicsBody( _world ) { BodyType = puppet ? PhysicsBodyType.Dynamic : PhysicsBodyType.Keyframed };
			copy.Transform = b.Transform;
			CopyShapes( b, copy, solidOnly: true );
			if ( puppet )
			{
				copy.GravityEnabled = false;
				copy.AutoSleep = false;
				float mass = b.Mass;
				copy.Mass = float.IsFinite( mass ) && mass > 0.01f ? mass : 50f;
			}
			_followers[b] = new Follower { Source = b, Copy = copy, Puppet = puppet, ShapeCount = b.ShapeCount, Version = version, TagKey = tags };
			_sourceOf[copy] = go;
		}

		foreach ( var gone in _followers.Keys.Where( k => !seen.Contains( k ) ).ToList() )
		{
			_followers[gone].Copy.Remove();
			_followers.Remove( gone );
		}
	}

	static string TagKeyOf( PhysicsBody b )
	{
		var first = b.Shapes.FirstOrDefault();
		return first?.Tags is { } t ? string.Join( ",", t.TryGetAll() ) : "";
	}

	void ClearFollowers()
	{
		foreach ( var f in _followers.Values )
			if ( f.Copy.IsValid() )
				f.Copy.Remove();
		_followers.Clear();
	}

	// Before every step (host ticks, client predictions AND replays): each follower heads for its scene body's pose now.
	void DriveFollowers()
	{
		if ( _followers.Count == 0 )
			return;
		foreach ( var f in _followers.Values )
		{
			if ( !f.Source.IsValid() || !f.Copy.IsValid() )
				continue;
			var target = f.Source.Transform;
			if ( !f.Puppet )
			{
				// Most keyframed scene bodies never move (blockout pieces with their collider's Static off): leave those be
				// — moving a keyframed body every tick, even onto its own pose, kept waking whatever leaned on it.
				var at = f.Copy.Transform;
				if ( at.Position.AlmostEqual( target.Position, 0.001f ) && at.Rotation.Distance( target.Rotation ) < 0.01f )
				{
					if ( !f.Copy.Velocity.IsNearZeroLength )
					{
						f.Copy.Velocity = 0f;
						f.Copy.AngularVelocity = 0f;
					}
					continue;
				}
				f.Copy.Move( target, Dt );
				continue;
			}
			var to = target.Position - f.Copy.Position;
			if ( to.Length > 200f )
			{
				f.Copy.Transform = target; // a teleport (respawn, possession): don't drag through the world
				f.Copy.Velocity = 0f;
				continue;
			}
			f.Copy.Velocity = (to / Dt).ClampLength( PuppetMaxSpeed );
			f.Copy.AngularVelocity = HeldBodyDriver.TurnToward( f.Copy.Rotation, target.Rotation ) / Dt;
			if ( f.Copy.Sleeping )
				f.Copy.Sleeping = false;
		}
	}

	// ── Pushes against anchored bodies (host) ───────────────────────────────────────────────────────────────

	/// <summary>Sideways speed (u/s) a body must press into an anchored one with to push it — resting against one isn't.</summary>
	const float PushMinSpeed = 20f;

	/// <summary>Fraction of the pusher's speed a pushed body is given, and the cap (prop hunt's push numbers).</summary>
	public const float PushTransfer = 0.6f;
	public const float PushMaxSpeed = 400f;

	/// <summary>A push on a body at this horizontal velocity: an anchored one is released (an upright one also stops
	/// turning — it slides, as a pushed player's prop does) and given PushTransfer of the push; a moving one is kept at
	/// least that fast along it. Run identically by the host's ticks, client predictions and replays (the anchored flag
	/// rolls back with the body); the host also tells everyone about a release, and a client re-anchors a release the host
	/// never confirms (see ClientTicks).</summary>
	public void PushBody( SimBody b, Vector3 velocity )
	{
		if ( b is null || !b.ShapesReady )
			return;
		if ( (b.Spec.Flags & SimBodyFlags.Anchored) != 0 )
		{
			var flags = (b.Spec.Flags & ~SimBodyFlags.Anchored) | ((b.Spec.Flags & SimBodyFlags.Upright) != 0 ? SimBodyFlags.NoTurn : 0);
			ApplyFlags( b, flags );
			if ( IsAuthority )
			{
				if ( Networking.IsActive )
					BroadcastFlags( b.Id, (byte)flags );
			}
			else
				b.PredictedRelease = Tick;
			SimStats.Releases++;
		}
		var want = velocity.WithZ( 0f ).ClampLength( PushMaxSpeed ) * PushTransfer;
		if ( want.IsNearZeroLength )
			return;
		var v = b.Phys.Velocity;
		float along = Vector3.Dot( v, want.Normal );
		if ( along < want.Length )
		{
			b.Phys.Sleeping = false;
			b.Phys.Velocity = v + want.Normal * (want.Length - along);
		}
	}

	/// <summary>Host: <see cref="PushBody"/> by key (the scene-intent push path — RoundManager.NoteDecoyPush).</summary>
	public bool PushBody( Guid key, Vector3 velocity )
	{
		if ( !IsAuthority || !_byKey.TryGetValue( key, out var b ) )
			return false;
		PushBody( b, velocity );
		return true;
	}

	/// <summary>Client: a push THIS machine's hunter is reporting to the host (HiderController.NoteSimPush,
	/// RoundManager.NoteDecoyPush) — predicted here at the next tick, and again at that tick in every replay, so the body
	/// moves at once instead of a round trip later when the host's push shows in a snapshot (user: "feels delayed"). A
	/// hunter is only a follower in the sim, so unlike a sim body's push nothing in a replay would re-make it. The host's own
	/// push lands a couple of ticks apart from ours at most; a push the host refuses unwinds through the normal replay once
	/// the record expires (ClientTicks).</summary>
	public void PredictPush( Guid key, Vector3 velocity )
	{
		if ( IsAuthority || !_byKey.TryGetValue( key, out var b ) )
			return;
		b.PredictedPush = (Tick + 1, velocity); // the next tick stepped (this frame's are done when a collision callback runs)
		b.LocalTouched = true;
		SimStats.PushesPredicted++;
	}

	// The rules prop hunt's push RPC used (RoundManager.NoteDecoyPush), now run on the sim's own bodies: bounds overlap
	// (touching, 1u slack); standing on it or under it is a floor/ceiling, not a push; only the part of the motion heading
	// INTO it counts. After every step, on every machine (a client predicts the release; see PushBody).
	void DetectAnchoredPushes()
	{
		List<SimBody> anchored = null;
		foreach ( var b in _bodies )
			if ( (b.Spec.Flags & SimBodyFlags.Anchored) != 0 && b.ShapesReady )
				(anchored ??= new()).Add( b );
		if ( anchored is null )
			return;

		void Test( PhysicsBody mover, Vector3 velocity )
		{
			var v = velocity.WithZ( 0f );
			if ( v.Length < PushMinSpeed )
				return;
			var mine = mover.GetBounds().Grow( 1f );
			foreach ( var a in anchored )
			{
				if ( (a.Spec.Flags & SimBodyFlags.Anchored) == 0 )
					continue; // released earlier this tick
				var clay = a.Phys.GetBounds();
				if ( !mine.Overlaps( clay ) )
					continue;
				if ( mine.Mins.z >= clay.Maxs.z - 2f || mine.Maxs.z <= clay.Mins.z + 2f )
					continue;
				var toward = (clay.Center - mine.Center).WithZ( 0f );
				if ( toward.LengthSquared > 0.01f && Vector3.Dot( v, toward.Normal ) < PushMinSpeed )
					continue;
				PushBody( a, v );
			}
		}

		// Not followers: a pawn pressing into a decoy is BLOCKED by the decoy's scene collider, so its velocity (and its
		// follower's) is ~0 at the moment of the push — the walk's INTENT is what tells, and the scene side already reports
		// that (RoundManager.NoteDecoyPush). Here: sim bodies (a knocked decoy sliding or falling into an anchored one).
		foreach ( var b in _bodies )
			if ( b.ShapesReady && (b.Spec.Flags & SimBodyFlags.Anchored) == 0 && !b.Phys.Sleeping )
				Test( b.Phys, b.Intent.IsNearZeroLength ? b.Phys.Velocity : b.Intent ); // a walking pawn: what it is TRYING to do
	}

	// ── Presentation ────────────────────────────────────────────────────────────────────────────────────────

	void PresentBodies()
	{
		foreach ( var b in _bodies )
		{
			if ( (b.Spec.Flags & SimBodyFlags.Present) == 0 || !b.ShapesReady )
				continue;
			var go = Scene.Directory.FindByGuid( b.Spec.Source );
			if ( !go.IsValid() )
				continue;

			var pose = VisualPose( b );
			if ( b.Presented is { } last && last.Position.AlmostEqual( pose.Position, 0.001f ) && last.Rotation.Distance( pose.Rotation ) < 0.01f )
				continue;
			b.Presented = pose;

			go.WorldPosition = pose.Position;
			go.WorldRotation = pose.Rotation;
			var rb = go.Components.Get<Rigidbody>();
			if ( rb.IsValid() && rb.PhysicsBody is { } pb && pb.IsValid() )
			{
				if ( rb.MotionEnabled )
					rb.MotionEnabled = false;
				pb.Transform = new Transform( pose.Position, pose.Rotation, pb.Transform.Scale );
			}
		}
	}

	// ── Host controls ───────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Host: change a body's flags (upright lock, gravity) — every machine follows.</summary>
	public void SetFlags( Guid key, SimBodyFlags flags )
	{
		if ( !IsAuthority || !_byKey.TryGetValue( key, out var b ) || b.Spec.Flags == flags )
			return;
		ApplyFlags( b, flags );
		if ( Networking.IsActive )
			BroadcastFlags( b.Id, (byte)flags );
	}

	void ApplyFlags( SimBody b, SimBodyFlags flags )
	{
		b.Spec.Flags = flags;
		b.Phys.Locking = LockFor( flags );
		b.Phys.GravityEnabled = (flags & SimBodyFlags.NoGravity) == 0;
		bool motion = b.ShapesReady && (flags & SimBodyFlags.Anchored) == 0;
		if ( b.Phys.MotionEnabled != motion )
		{
			b.Phys.MotionEnabled = motion;
			if ( motion )
				b.Phys.Sleeping = false; // released
		}
	}

	[Rpc.Broadcast]
	static void BroadcastFlags( ushort id, byte flags )
	{
		if ( !FromHost || Networking.IsHost )
			return;
		var sim = Current;
		sim?.Receive( () =>
		{
			if ( sim._byId.TryGetValue( id, out var b ) )
			{
				sim.ApplyFlags( b, (SimBodyFlags)flags );
				if ( ((SimBodyFlags)flags & SimBodyFlags.Anchored) == 0 )
					b.PredictedRelease = -1; // the host released it too
			}
		} );
	}

	// ── Changing a body's driver in place ───────────────────────────────────────────────────────────────────
	// A grab or a drop changes WHO moves a body (the hold driver and its holder, or nobody), not what it is: the same clay
	// shapes, the same id, its motion carrying on. Re-registering it instead (remove + add, a new id) restarted it on
	// every client from the add's state — captured BEFORE the thrown velocity was set, and a lead old by the time it landed
	// — so a dropped prop stopped dead at the host's older pose and was then yanked onto its real path (measured on a live
	// client: first correction 90–445u on every drop).

	/// <summary>Host: change a body's driver, owner and flags in place (every machine follows). The body keeps its shapes,
	/// id and motion; its controller state starts afresh.</summary>
	public void Redrive( Guid key, byte driver, Guid owner, SimBodyFlags flags )
	{
		if ( !IsAuthority || !_byKey.TryGetValue( key, out var b ) )
			return;
		if ( b.Spec.Driver == driver && b.Spec.Owner == owner && b.Spec.Flags == flags )
			return;
		ApplyRedrive( b, driver, owner, flags );
		if ( Networking.IsActive )
			BroadcastRedrive( b.Id, Tick + 1, driver, owner, (byte)flags ); // in effect from the next tick stepped
	}

	// Client: a change of drive in effect from this tick — applied now when that tick is the next one or already past, and
	// remembered so a replay crossing it switches there (Step); the body's history before it keeps the old drive (Restore).
	void LogDrive( SimBody b, int tick, byte driver, Guid owner, SimBodyFlags flags )
	{
		var log = b.DriveLog;
		log.Add( (tick, driver, owner, flags) );
		if ( log.Count > 64 )
			log.RemoveRange( 0, log.Count - 64 );
		if ( tick <= Tick + 1 )
			ApplyRedrive( b, driver, owner, flags );
		if ( tick <= Tick )
			PatchHistoryDrive( b, tick, driver, owner, flags );
	}

	internal void ApplyRedrive( SimBody b, byte driver, Guid owner, SimBodyFlags flags )
	{
		if ( b.Spec.Driver == driver && b.Spec.Owner == owner && b.Spec.Flags == flags )
			return;
		bool wasHold = b.Spec.Driver == SimHoldDriver.Id, isHold = driver == SimHoldDriver.Id;
		b.Spec.Driver = driver;
		b.Spec.Owner = owner;
		_drivers.TryGetValue( driver, out b.Driver );
		b.Ctrl = SimCtrl.Initial;
		b.Intent = Vector3.Zero;
		b.RemoteGuess = SimInput.None;
		b.Rest = null;
		b.HoldReleased = false;
		b.LocalTouched = isHold && owner == LocalId; // our own hold (and the throw out of it) is ours to predict; anything else starts untouched
		ApplyFlags( b, flags );
		if ( wasHold != isHold && b.ShapesReady && b.Phys.IsValid() )
		{
			// Friction follows the driver (a held body has none): the shapes are re-copied, the state kept.
			var state = b.Capture();
			BuildShapes( b );
			b.Restore( state );
		}
		if ( b.Phys.IsValid() )
			b.Phys.Sleeping = false;
	}

	[Rpc.Broadcast]
	static void BroadcastRedrive( ushort id, int tick, byte driver, Guid owner, byte flags )
	{
		if ( !FromHost || Networking.IsHost )
			return;
		var sim = Current;
		sim?.Receive( () =>
		{
			if ( !sim._byId.TryGetValue( id, out var b ) )
				return;
			bool same = b.Spec.Driver == driver && b.Spec.Owner == owner && b.Spec.Flags == (SimBodyFlags)flags;
			bool predicted = b.PredictedDrive is not null;
			b.PredictedDrive = null;
			if ( same && predicted )
				return; // the host confirmed what we did ourselves (PredictHold / PredictRelease), logged at our own tick
			sim.LogDrive( b, tick, driver, owner, (SimBodyFlags)flags ); // at the HOST's tick: a replay across it switches there
		} );
	}

	/// <summary>Client: this machine asked to grab a body the sim has as a LOOSE one — make it ours to hold NOW (the hold
	/// driver, our input), as the host will when the request lands (<see cref="Redrive"/>), so the grab costs no round trip.
	/// Flagged until the host's redrive confirms it; an unanswered request reverts (ClientTicks). False if it can't be
	/// predicted (not here, not loose, no shapes yet).</summary>
	public bool PredictHold( Guid key )
	{
		if ( IsAuthority || !_byKey.TryGetValue( key, out var b ) || b.Spec.Driver != 0 || !b.ShapesReady )
			return false;
		b.PredictedDrive = (Tick, b.Spec.Driver, b.Spec.Owner, b.Spec.Flags);
		LogDrive( b, Tick + 1, SimHoldDriver.Id, LocalId, SimBodyFlags.Present | SimBodyFlags.NoGravity ); // from the next tick, where our first target goes
		SimStats.HoldsPredicted++;
		return true;
	}

	/// <summary>Client: this machine let go of a body it holds — make it loose NOW, at the tick its inputs stop carrying a
	/// target, as the host will at that same tick (the hold driver's release, HoldReleased). The host's redrive confirms it.</summary>
	public void PredictRelease( Guid key )
	{
		if ( IsAuthority || !_byKey.TryGetValue( key, out var b ) || b.Spec.Driver != SimHoldDriver.Id || !b.IsLocalOwned )
			return;
		b.PredictedDrive = (Tick, b.Spec.Driver, b.Spec.Owner, b.Spec.Flags);
		LogDrive( b, Tick + 1, 0, Guid.Empty, SimBodyFlags.Present ); // from the next tick, the first with no target
		b.LocalTouched = true; // our throw: predicted here until it rests
		SimStats.HoldsPredicted++;
	}

	/// <summary>Client: undo a <see cref="PredictHold"/> the host never confirmed (or that this machine gave up on).</summary>
	public void RevertPredictedHold( Guid key )
	{
		if ( !_byKey.TryGetValue( key, out var b ) || b.PredictedDrive is not { } was )
			return;
		b.PredictedDrive = null;
		LogDrive( b, Tick + 1, was.driver, was.owner, was.flags );
		SimStats.HoldsReverted++;
	}

	static PhysicsLock LockFor( SimBodyFlags flags )
		=> (flags & SimBodyFlags.Upright) == 0 ? default
			: new PhysicsLock { Pitch = true, Roll = true, Yaw = (flags & SimBodyFlags.NoTurn) != 0 };

	/// <summary>Host: knock a body (an impulse at a world point), waking it.</summary>
	public void Kick( Guid key, Vector3 point, Vector3 impulse )
	{
		if ( !IsAuthority || !_byKey.TryGetValue( key, out var b ) || !b.ShapesReady )
			return;
		b.Phys.Sleeping = false;
		b.Phys.ApplyImpulseAt( point, impulse );
	}

	/// <summary>Host: set a body's motion (now, or once its shapes exist).</summary>
	public void SetMotion( Guid key, Vector3 velocity, Vector3 angular )
	{
		if ( !IsAuthority || !_byKey.TryGetValue( key, out var b ) )
			return;
		if ( !b.ShapesReady )
		{
			b.PendingMotion = (velocity, angular);
			return;
		}
		b.Phys.Sleeping = false;
		b.Phys.Velocity = velocity;
		b.Phys.AngularVelocity = angular;
	}
}
