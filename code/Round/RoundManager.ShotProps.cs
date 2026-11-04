using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mimiclay;

// Shot decoys: the central pellet (the one that catches props) knocks the decoy it strikes LOOSE — a real rigidbody
// tumble from the shot's impulse — and, with "Repair props" off, a decoy the shots have whittled below
// ShotRemoveFraction of its clay is removed outright. Players never get here: a caught player pops, and scatter pellets don't mark props at all (see
// HunterController.TryCarve), so this only ever touches scenery.
//
// Scene props are Snapshot objects, not network objects, so each machine running its own physics would leave every
// decoy somewhere different. Instead the Rigidbody is added on demand, LOCAL to each machine (NotNetworked, so a
// joiner's snapshot never ships a simulating body): the HOST simulates it and streams the pose until it sleeps;
// clients carry a kinematic body that follows the stream (kinematic rather than none, so a prop standing on a
// moving decoy gets proper contacts). A late joiner gets the live pose in the scene snapshot and picks the stream up
// from there. Props that were never shot carry no body at all.
public sealed partial class RoundManager
{
	/// <summary>Velocity change (u/s) the central pellet gives the decoy it hits — mass-independent, so a teapot and a
	/// cherry both visibly jump. Applied at the hit point, so off-centre shots spin it.</summary>
	internal const float ShotKnockSpeed = 180f;

	/// <summary>A decoy left with less than this fraction of its authored clay is removed.</summary>
	internal const float ShotRemoveFraction = 0.3f;

	const float ShotPoseInterval = 0.05f; // host → clients while tumbling
	const float ShotFollowRate = 25f;     // clients ease toward the streamed pose (1/s)

	readonly HashSet<GameObject> _shotLoose = new();               // host: simulating, streamed while awake
	readonly HashSet<GameObject> _shotResting = new();             // host: loose and asleep, rest pose already sent
	readonly Dictionary<GameObject, Transform> _shotTargets = new(); // clients: last streamed pose
	readonly Dictionary<GameObject, float> _shotAuthoredVolume = new(); // host: intact clay volume, once per decoy
	RealTimeSince _sinceShotPose;

	/// <summary>Every machine, from HunterController.BroadcastCarve once a central pellet's crater is in; acts on the
	/// host only.</summary>
	internal void OnPropShot( SdfSculpture sculpt, Vector3 hit, Vector3 dir )
	{
		if ( !IsHostAuthority || !IsShotDecoy( sculpt ) )
			return;

		var go = sculpt.GameObject;
		// Removal + island cleanup, off the main thread — lands a frame or two later. "Repair props" off only: with it
		// on the craters heal, so nothing is ever shot away for good (no removal, no crumbs, no splits).
		if ( !Settings.RepairProps )
			QueueShotScan( sculpt );

		// In the sim world (RoundManager.SimDecoys): the knock happens there.
		if ( KnockSimDecoy( go, hit, dir ) )
			return;

		// Lent to a client (held, or settling — RoundManager.Grab): that machine simulates it, so the knock goes there.
		if ( IsLentDecoy( go ) )
		{
			float lentMass = MathF.Max( go.Components.Get<SdfCollider>()?.ClayMass ?? 1f, 1f );
			ApplyLentImpulse( go, hit, dir.Normal * ShotKnockSpeed * lentMass );
			return;
		}

		var body = ShotBody( go, simulate: true );
		if ( !body.IsValid() )
			return;

		if ( _pushedDecoys.Remove( go ) )
			body.Locking = default; // was only pushed (sliding upright): a shot lets it tumble
		float mass = go.Components.Get<SdfCollider>()?.ClayMass ?? 0f;
		if ( mass > 0f )
			body.MassOverride = mass;
		body.Sleeping = false;
		float m = mass > 0f ? mass : body.PhysicsBody?.Mass ?? 1f;
		body.ApplyImpulseAt( hit, dir.Normal * ShotKnockSpeed * MathF.Max( m, 1f ) );
		_shotLoose.Add( go );
		_shotResting.Remove( go );
	}

	/// <summary>Every machine, from HunterController.BroadcastCarve right before a crater lands. With "Repair props"
	/// off, craters are permanent, so a decoy's COLLIDER takes them too (SdfCollider.CarveCollision): a shot-up prop
	/// physically loses bulk, tumbles as what's left of it and weighs less (ClayMass follows the build). With repair on
	/// craters heal, so they stay cosmetic. Every machine flips it from the same synced setting, so colliders agree.
	/// PERF (unmeasured): each crater now rebuilds the decoy's collider on EVERY machine, uncached (every crater makes
	/// a new shape), and a deeply bitten prop can fall back to voxel boxes — more shapes on a moving body. Check with
	/// a sustained-fire test before shipping; see the repair-props memory.</summary>
	internal void PrepareShotCarve( SdfSculpture sculpt )
	{
		if ( Settings.RepairProps || !IsShotDecoy( sculpt ) )
			return;

		var collider = sculpt.Components.Get<SdfCollider>();
		if ( collider.IsValid() )
			collider.CarveCollision = true;
	}

	// Scenery clay with a solid collider: not a pawn (players pop, hunter faces heal), not saved-out runtime clay.
	internal static bool IsShotDecoy( SdfSculpture sculpt )
		=> PropClaims.IsScenery( sculpt )
		&& sculpt.Components.Get<SdfCollider>() is { BuildAsTrigger: false };

	// ── Shot scan: removal + island cleanup (host, worker thread) ─────────────────────────────────────────────
	// Each central-pellet hit queues one SdfIslandScan of the decoy. Coalesced per decoy: a hit landing while a scan
	// is running just marks it dirty, and the running scan goes round again on the newest shape — sustained fire costs
	// one or two scans, not one per shot. The brushes are copied + flattened on the main thread; the sampling and
	// flood fill (a few ms) run on a worker; only the verdict comes back to the main thread.
	//
	// Only with "Repair props" off — with it on, craters heal and the clay grows back, so nothing is removed, cut or
	// split (and no scan runs). Verdicts, measured against the decoy's INTACT clay (its authored brushes, sampled once
	// on the first scan):
	//  - the main body under ShotRemoveFraction → the whole decoy is removed;
	//  - an island big enough to be a PART (ShotSplitFraction of the intact clay and at least ShotSplitMinVolume)
	//    BREAKS OFF as its own decoy (see SplitShotProp), one per scan; anything smaller is a crumb and is cut away
	//    with a subtract box (shape AND collider: the collider takes damage brushes then).

	/// <summary>Islands at least this fraction of the intact clay break off as their own object (repair off); smaller
	/// ones are cut away. Low on purpose: handles are small next to their prop — measured 2026-11-03, a mug's handle is
	/// 1.9% of its clay, a teapot spout 5%, a frying pan handle 8%, a saucepan handle 18% (and a shot through the root
	/// leaves the piece smaller still).</summary>
	internal const float ShotSplitFraction = 0.015f;

	/// <summary>…and at least this much clay (units³, ~a 7-unit cube), so crumbs off a big prop are still just cut.</summary>
	internal const float ShotSplitMinVolume = 300f;

	readonly HashSet<GameObject> _scanBusy = new();
	readonly HashSet<GameObject> _scanDirty = new();

	void QueueShotScan( SdfSculpture sculpt )
	{
		var go = sculpt.GameObject;
		if ( !_scanBusy.Add( go ) )
		{
			_scanDirty.Add( go );
			return;
		}
		_ = RunShotScan( sculpt );
	}

	async Task RunShotScan( SdfSculpture sculpt )
	{
		var go = sculpt.GameObject;
		try
		{
			do
			{
				_scanDirty.Remove( go );
				if ( !sculpt.IsValid() || sculpt.Brushes is not { Count: > 0 } brushes )
					return;

				// Main thread: snapshot. Flatten resolves groups (prefab lookups) here, never on the worker.
				var all = Snapshot( SdfGroups.Flatten( brushes ) );
				if ( !Sdf.TryGetBounds( all, out var bounds ) )
					return;

				List<SdfBrush> intact = null;
				BBox intactBounds = default;
				if ( !_shotAuthoredVolume.ContainsKey( go ) )
				{
					intact = Snapshot( SdfGroups.Flatten( brushes.GetRange( 0, Math.Min( sculpt.AuthoredBrushCount, brushes.Count ) ) ) );
					Sdf.TryGetBounds( intact, out intactBounds );
				}

				await GameTask.WorkerThread();
				var scan = SdfIslandScan.Scan( all, bounds );
				float intactVolume = intact is not null ? SdfIslandScan.Volume( intact, intactBounds ) : 0f;
				await GameTask.MainThread();

				if ( !this.IsValid() || !go.IsValid() )
					return;
				if ( intact is not null )
					_shotAuthoredVolume[go] = intactVolume;

				ApplyShotScan( go, scan, _shotAuthoredVolume.GetValueOrDefault( go ) );
			}
			while ( _scanDirty.Contains( go ) && go.IsValid() );
		}
		finally
		{
			_scanBusy.Remove( go );
			_scanDirty.Remove( go );
		}
	}

	internal static List<SdfBrush> Snapshot( List<SdfBrush> brushes )
	{
		var copy = new List<SdfBrush>( brushes.Count );
		foreach ( var b in brushes )
			copy.Add( b.Copy() );
		return copy;
	}

	// Host, main thread.
	void ApplyShotScan( GameObject go, SdfIslandScan.Result scan, float intact )
	{
		if ( intact <= 0f || Settings.RepairProps ) // repair turned on mid-scan: nothing goes for good
			return;

		if ( scan.MainVolume < intact * ShotRemoveFraction )
		{
			_shotLoose.Remove( go );
			if ( Networking.IsActive )
				BroadcastShotRemove( go );
			else
				RemoveShotProp( go );
			return;
		}

		List<Vector3> centres = null, halves = null;
		SdfIslandScan.Island? split = null;
		foreach ( var island in scan.Islands )
		{
			if ( !island.Safe )
				continue;
			if ( island.Volume >= intact * ShotSplitFraction && island.Volume >= ShotSplitMinVolume )
			{
				if ( split is null || island.Volume > split.Value.Volume )
					split = island; // the biggest breaks off now; the rest wait for the next scan
				continue;
			}
			(centres ??= new()).Add( island.Center );
			(halves ??= new()).Add( island.HalfExtents );
		}

		if ( centres is not null )
		{
			if ( Networking.IsActive )
				BroadcastIslandCut( go, centres.ToArray(), halves.ToArray() );
			else
				CutIslands( go, centres.ToArray(), halves.ToArray() );
		}

		if ( split is { } chunk )
		{
			var id = Guid.NewGuid();
			if ( Networking.IsActive )
				BroadcastShotSplit( go, id, chunk.Center, chunk.HalfExtents, scan.Bounds.Mins, scan.Bounds.Maxs );
			else
				_ = SplitShotProp( go, id, chunk.Center, chunk.HalfExtents, new BBox( scan.Bounds.Mins, scan.Bounds.Maxs ) );
		}
	}

	// ── Breaking off a chunk ────────────────────────────────────────────────────────────────────────────────
	// The host picks the chunk (an island's box that provably misses the main body) and a guid; EVERY machine then
	// makes the same two shapes from its own copy of the decoy:
	//  - the original loses the chunk: one subtract box on the damage tail (like an island cut);
	//  - the chunk is a CLONE of the decoy, under the host's guid (so every later RPC — carves, poses — resolves it
	//    on every machine, and late joiners get it in the snapshot), whose brushes keep only the chunk box: six
	//    subtract slabs cover everything outside it. Its brushes are all made AUTHORED (Damage off): the distant LOD
	//    mesh skips damage, so a damage-flagged clone would show the WHOLE original prop from afar.
	// Staggered: both new colliders are built on a worker FIRST (straight into the collider cache), and only then —
	// one main-thread frame — is the cut applied and the clone enabled, so both colliders resolve as cache hits and
	// never overlap a stale shape (overlapping colliders on the host would fling the pieces apart). The clone's mesh
	// and field bake are the sculpture's own async build.

	/// <summary>Speed (u/s) the two halves of a split get pushed apart, on top of the original's motion.</summary>
	internal const float ShotSplitSpeed = 60f;

	/// <summary>Slack (units) the keep-slabs extend past the shape's bounds, so blends at the edge are covered.</summary>
	internal const float ShotSplitPad = 4f;

	[Rpc.Broadcast]
	void BroadcastShotSplit( GameObject go, Guid id, Vector3 keepCentre, Vector3 keepHalf, Vector3 boundsMin, Vector3 boundsMax )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;
		_ = SplitShotProp( go, id, keepCentre, keepHalf, new BBox( boundsMin, boundsMax ) );
	}

	async Task SplitShotProp( GameObject go, Guid id, Vector3 keepCentre, Vector3 keepHalf, BBox bounds )
	{
		var sculpt = go.IsValid() ? go.Components.Get<SdfSculpture>() : null;
		if ( !sculpt.IsValid() || sculpt.Brushes is null || sculpt.Brushes.Count + 6 > SdfBrushPacker.MaxBrushes )
			return;

		var cut = SharpBox( keepCentre, keepHalf, damage: true );

		// The chunk: every brush as authored, plus the six slabs around the keep box.
		var chunk = new List<SdfBrush>( sculpt.Brushes.Count + 6 );
		foreach ( var b in sculpt.Brushes )
		{
			var c = b.Copy();
			c.Damage = false;
			c.Shrinks = false;
			chunk.Add( c );
		}
		AddKeepSlabs( chunk, bounds, keepCentre, keepHalf );

		// Worker: both colliders into the cache. The original builds from its whole list (CarveCollision is on —
		// splits only happen with repair off); if another crater lands meanwhile its build just misses the cache
		// and goes the normal async way.
		var original = new List<SdfBrush>( sculpt.Brushes ) { cut };
		await SdfCollisionBuilder.BuildAsync( original );
		await SdfCollisionBuilder.BuildAsync( chunk );

		if ( !this.IsValid() || !go.IsValid() || !sculpt.IsValid() )
			return;

		// One frame: cut the original, spawn the chunk where it already stands.
		sculpt.Brushes.Add( cut );
		sculpt.Rebuild();

		// A plain (non-prefab) copy, re-id'd under the host's guid: clone disabled, cut the prefab link, serialize.
		var temp = go.Clone( new CloneConfig( go.WorldTransform, startEnabled: false ) );
		temp.BreakFromPrefab();
		var json = temp.Serialize();
		temp.Destroy();
		SceneUtility.MakeIdGuidsUnique( json, id );
		var piece = new GameObject( false );
		piece.Deserialize( json );
		piece.Name = $"{go.Name} (piece)";
		piece.WorldTransform = go.WorldTransform;
		// includeDisabled: the piece is still disabled, and a plain Get skips disabled components.
		piece.Components.Get<Rigidbody>( includeDisabled: true )?.Destroy(); // a fresh local body, made below / by the pose stream
		var pieceSculpt = piece.Components.Get<SdfSculpture>( includeDisabled: true );
		if ( pieceSculpt.IsValid() )
			pieceSculpt.Brushes = chunk;
		piece.Enabled = true;

		if ( !IsHostAuthority )
			return;

		// Host: the piece tumbles from where the original was going, both pushed apart along the split.
		if ( DecoyInSim( go ) )
		{
			// In the sim world: so is the piece (it gets its motion once its collider has built).
			var sim = Sim;
			var orig = sim.Find( go.Id );
			var away = go.WorldTransform.NormalToWorld( (keepCentre - bounds.Center).Normal );
			var v0 = orig.Phys.Velocity;
			var w0 = orig.Phys.AngularVelocity;
			if ( RegisterSimDecoy( piece, awake: true, upright: false ) is not null )
				sim.SetMotion( piece.Id, v0 + away * ShotSplitSpeed, w0 );
			sim.SetMotion( go.Id, v0 - away * ShotSplitSpeed * 0.5f, w0 );
			return;
		}

		var from = go.Components.Get<Rigidbody>();
		var body = ShotBody( piece, simulate: true );
		if ( !body.IsValid() )
			return;
		float mass = piece.Components.Get<SdfCollider>()?.ClayMass ?? 0f;
		if ( mass > 0f )
			body.MassOverride = mass;
		var apart = go.WorldTransform.NormalToWorld( (keepCentre - bounds.Center).Normal );
		body.Velocity = (from.IsValid() ? from.Velocity : Vector3.Zero) + apart * ShotSplitSpeed;
		body.AngularVelocity = from.IsValid() ? from.AngularVelocity : Vector3.Zero;
		if ( from.IsValid() )
			from.Velocity -= apart * ShotSplitSpeed * 0.5f;
		_shotLoose.Add( piece );
		_shotResting.Remove( piece );
	}

	/// <summary>Six subtract slabs that leave only the keep box of a shape with these bounds (a split's chunk). Each slab
	/// spans the WHOLE bounds on its other two axes, so neighbouring slabs overlap instead of meeting edge to edge: the
	/// collider clears a voxel only where ONE subtract reaches deep into it (SdfCollisionBuilder.BuildCarved takes the
	/// deepest carver, not their union), so edge-to-edge slabs left ~1-unit walls standing along every seam — a broken-
	/// off cone tip carried a cage of slivers the size of the whole cone. Every slab still stays outside the keep box
	/// on its own axis, so the overlap never bites the chunk.</summary>
	internal static void AddKeepSlabs( List<SdfBrush> brushes, BBox bounds, Vector3 keepCentre, Vector3 keepHalf )
	{
		var lo = bounds.Mins - ShotSplitPad;
		var hi = bounds.Maxs + ShotSplitPad;
		var kLo = keepCentre - keepHalf;
		var kHi = keepCentre + keepHalf;
		AddSlab( brushes, new Vector3( lo.x, lo.y, lo.z ), new Vector3( kLo.x, hi.y, hi.z ) ); // -x
		AddSlab( brushes, new Vector3( kHi.x, lo.y, lo.z ), new Vector3( hi.x, hi.y, hi.z ) ); // +x
		AddSlab( brushes, new Vector3( lo.x, lo.y, lo.z ), new Vector3( hi.x, kLo.y, hi.z ) ); // -y
		AddSlab( brushes, new Vector3( lo.x, kHi.y, lo.z ), new Vector3( hi.x, hi.y, hi.z ) ); // +y
		AddSlab( brushes, new Vector3( lo.x, lo.y, lo.z ), new Vector3( hi.x, hi.y, kLo.z ) ); // -z
		AddSlab( brushes, new Vector3( lo.x, lo.y, kHi.z ), new Vector3( hi.x, hi.y, hi.z ) ); // +z
	}

	internal static void AddSlab( List<SdfBrush> brushes, Vector3 min, Vector3 max )
	{
		var size = max - min;
		if ( size.x <= 0.01f || size.y <= 0.01f || size.z <= 0.01f )
			return; // the keep box already reaches this side of the bounds
		brushes.Add( SharpBox( (min + max) * 0.5f, size * 0.5f, damage: false ) );
	}

	internal static SdfBrush SharpBox( Vector3 centre, Vector3 half, bool damage ) => new()
	{
		Shape = SdfShape.Box,
		Operation = SdfOperation.Subtract,
		Position = centre,
		Size = half,
		Blend = 0f,
		Rounding = 0f,
		Damage = damage,
	};

	[Rpc.Broadcast]
	void BroadcastIslandCut( GameObject go, Vector3[] centres, Vector3[] halves )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;
		CutIslands( go, centres, halves );
	}

	// Every machine: one sharp subtract box per island, on the damage tail (permanent — repair is off), then one
	// rebuild. The collider takes it (CarveCollision is on for this decoy), so the stray hull goes with the clay.
	static void CutIslands( GameObject go, Vector3[] centres, Vector3[] halves )
	{
		var sculpt = go.IsValid() ? go.Components.Get<SdfSculpture>() : null;
		if ( !sculpt.IsValid() || sculpt.Brushes is null || centres is null || halves is null )
			return;

		int n = Math.Min( centres.Length, halves.Length );
		for ( int i = 0; i < n && sculpt.Brushes.Count < SdfBrushPacker.MaxBrushes; i++ )
		{
			sculpt.Brushes.Add( SharpBox( centres[i], halves[i], damage: true ) );
		}
		sculpt.Rebuild();
	}

	// The decoy's local Rigidbody, made on first need. simulate: the host's free body; otherwise kinematic.
	static Rigidbody ShotBody( GameObject go, bool simulate )
	{
		if ( !go.IsValid() )
			return null;

		var body = go.Components.Get<Rigidbody>();
		if ( !body.IsValid() )
		{
			body = go.Components.Create<Rigidbody>( startEnabled: false );
			body.Flags |= ComponentFlags.NotNetworked | ComponentFlags.NotSaved;
			body.RigidbodyFlags |= RigidbodyFlags.DisableCollisionSounds;
			body.Enabled = true;

			// Whatever this machine simulates of it (held, predicted, loose) can push the still decoys it runs into.
			var listener = go.Components.Create<DecoyPushListener>( startEnabled: false );
			listener.Flags |= ComponentFlags.NotNetworked | ComponentFlags.NotSaved | ComponentFlags.Hidden;
			listener.Enabled = true;
		}

		body.MotionEnabled = simulate;
		body.Gravity = simulate;
		body.EnhancedCcd = simulate; // a shot decoy can be fast and small
		return body;
	}

	// ── Pushable decoys ─────────────────────────────────────────────────────────────────────────────────────
	// A player's prop is a physics body: shove it (walk into it, push a held prop into it) and it slides. A still decoy
	// was a static collider that never budged — so pushing things around sorted players from decoys. Now whatever
	// SIMULATES a moving body — a prop's own machine, a hunter's, the machine running a held or loose decoy — reports
	// pressing sideways into a still decoy (NoteDecoyPush), and the host wakes that decoy as a physics body moving at
	// the push, from where its own physics (with every other body's kinematic proxy) keeps pushing it, streamed like
	// any loose decoy. It slides UPRIGHT — rotation locked, the way a pushed player's prop walks — so a shove doesn't
	// topple a decoy where it wouldn't topple a player; a shot (OnPropShot) frees it to tumble.

	/// <summary>Sideways speed (u/s) a body must press into a still decoy with to push it — resting against one isn't.</summary>
	const float PushMinSpeed = 20f;

	/// <summary>Fraction of the pusher's speed the decoy starts with (the host's physics carries the push on from there).</summary>
	const float PushTransfer = 0.6f;
	const float PushMaxSpeed = 400f;

	// Pusher side: last report per decoy, so a sustained contact doesn't send one per tick.
	static readonly Dictionary<Guid, RealTimeSince> _pushSent = new();

	// Host: decoys woken by a push (rotation-locked) rather than a shot.
	readonly HashSet<GameObject> _pushedDecoys = new();

	/// <summary>On the machine simulating a moving body (its collision callback): was that a push into a still decoy?
	/// Reports it to the host if so. Standing on or under a decoy isn't a push. <paramref name="intent"/> is how the
	/// pusher is TRYING to move (a walk's command, a controller's wish, a hold's chase) — never its actual velocity, which
	/// a static decoy in the way has already zeroed (measured: a blocked hunter read 0 u/s and pushed nothing).</summary>
	internal static void NoteDecoyPush( in Collision collision, Vector3 intent )
	{
		var rm = Current;
		if ( !rm.IsValid() || rm.Phase == RoundPhase.Lobby )
			return;
		var other = collision.Other.GameObject;
		var sculpt = other.IsValid() ? other.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors ) : null;
		if ( !sculpt.IsValid() || !IsShotDecoy( sculpt ) )
			return;

		// Where the two are, from BOUNDS — not the contact: sustained-contact callbacks carry no contact point or normal
		// (measured: every OnCollisionUpdate reported a zero normal, so normal-based tests rejected every push).
		var self = collision.Self.Body;
		if ( !self.IsValid() || !Sdf.TryGetBounds( sculpt.Brushes, out var b ) )
			return;
		var mine = self.GetBounds();
		var clay = WorldBounds( sculpt.WorldTransform, b );

		// On top of it or underneath it: a floor or a ceiling to us, not something we're pushing — standing on a decoy
		// mustn't slide it out from under you.
		if ( mine.Mins.z >= clay.Maxs.z - 2f || mine.Maxs.z <= clay.Mins.z + 2f )
			return;

		// Only the part of the intent heading INTO the decoy pushes it (sliding past along its side doesn't).
		var velocity = intent.WithZ( 0f );
		var toward = (clay.Center - mine.Center).WithZ( 0f );
		if ( velocity.Length < PushMinSpeed || (toward.LengthSquared > 0.01f && Vector3.Dot( velocity, toward.Normal ) < PushMinSpeed) )
			return;

		var id = sculpt.GameObject.Id;
		// A sim decoy (RoundManager.SimDecoys) is re-pushed while the contact lasts: the scene collider it shows blocks the
		// pusher, so the sim never sees the push itself — these reports are what keep it sliding (Phase 2 moves the walk in).
		if ( _pushSent.TryGetValue( id, out var since ) && since < (rm.DecoyInSim( sculpt.GameObject ) ? 0.1f : 0.3f) )
			return;
		_pushSent[id] = 0f;
		if ( rm.DecoyInSim( sculpt.GameObject ) )
			rm.Sim.PredictPush( id, velocity ); // on a client: the sim decoy moves now, not a round trip later (no-op on the host)
		rm.RequestDecoyPush( sculpt.GameObject, velocity );
	}

	// A sculpture-local box as a world AABB (all eight corners — the decoy may be turned).
	static BBox WorldBounds( Transform t, BBox local )
	{
		var box = BBox.FromPositionAndSize( t.PointToWorld( local.Center ), 0f );
		for ( int i = 0; i < 8; i++ )
		{
			var corner = new Vector3( (i & 1) == 0 ? local.Mins.x : local.Maxs.x, (i & 2) == 0 ? local.Mins.y : local.Maxs.y, (i & 4) == 0 ? local.Mins.z : local.Maxs.z );
			box = box.AddPoint( t.PointToWorld( corner ) );
		}
		return box;
	}

	/// <summary>Any machine → host: something pushed this still decoy at this velocity — wake it.</summary>
	[Rpc.Host]
	void RequestDecoyPush( GameObject go, Vector3 velocity )
	{
		var sculpt = go.IsValid() ? go.Components.Get<SdfSculpture>() : null;
		if ( PushSimDecoy( go, velocity ) )
			return; // in the sim world (RoundManager.SimDecoys)
		if ( !IsShotDecoy( sculpt ) || _shotLoose.Contains( go ) || IsLentDecoy( go ) )
			return; // already a physics body here (or on the client it's lent to): its own contacts push it from now on

		var body = ShotBody( go, simulate: true );
		if ( !body.IsValid() )
			return;
		body.Locking = new PhysicsLock { Pitch = true, Roll = true, Yaw = true };
		float mass = go.Components.Get<SdfCollider>()?.ClayMass ?? 0f;
		if ( mass > 0f )
			body.MassOverride = mass;
		body.Sleeping = false;
		body.Velocity = velocity.ClampLength( PushMaxSpeed ) * PushTransfer;
		_pushedDecoys.Add( go );
		_shotLoose.Add( go );
		_shotResting.Remove( go );
	}

	// Every machine, per frame (RoundManager.OnUpdate): stream what we simulate, follow what others simulate.
	void TickShotProps()
	{
		if ( _sinceShotPose >= ShotPoseInterval )
		{
			_sinceShotPose = 0f;
			if ( IsHostAuthority )
				StreamHostDecoys();
			else if ( _simDecoy.IsValid() && Networking.IsActive && !EngineSynced( _simDecoy ) )
				BroadcastShotPose( _simDecoy, _simDecoy.WorldPosition, _simDecoy.WorldRotation ); // lent snapshot decoy: we stream it
		}

		FollowStreamedDecoys();
	}

	// Host: the loose decoys it simulates. Lent ones are streamed by their simulator instead (RoundManager.Grab).
	void StreamHostDecoys()
	{
		if ( _shotLoose.Count == 0 )
			return;

		// Loose decoys stay tracked after they settle: a hider shoving one (or another shot) wakes it, and the stream
		// picks up again. Asleep costs one reliable "rest" pose, then nothing.
		_shotLoose.RemoveWhere( go => !go.IsValid() );
		_shotResting.RemoveWhere( go => !go.IsValid() );
		foreach ( var go in _shotLoose )
		{
			if ( IsLentDecoy( go ) || DecoyInSim( go ) )
				continue;
			var body = go.Components.Get<Rigidbody>();
			bool asleep = !body.IsValid() || body.Sleeping;
			if ( asleep && !_shotResting.Add( go ) )
				continue; // already told everyone where it rests
			if ( !asleep )
				_shotResting.Remove( go );

			if ( !Networking.IsActive )
				continue;
			if ( asleep )
				BroadcastShotRest( go, go.WorldPosition, go.WorldRotation ); // reliable: the pose it stays at
			else
				BroadcastShotPose( go, go.WorldPosition, go.WorldRotation );
		}
	}

	// Every machine: ease the decoys someone ELSE simulates toward their streamed pose (on the host, only lent ones).
	void FollowStreamedDecoys()
	{
		if ( _shotTargets.Count == 0 )
			return;

		float t = 1f - MathF.Exp( -ShotFollowRate * Time.Delta );
		List<GameObject> gone = null;
		foreach ( var (go, target) in _shotTargets )
		{
			if ( !go.IsValid() )
			{
				(gone ??= new()).Add( go );
				continue;
			}
			if ( SimulatingLentHere( go ) )
				continue; // lent to us: our own physics moves it (RoundManager.Grab)
			go.WorldPosition = Vector3.Lerp( go.WorldPosition, target.Position, t );
			go.WorldRotation = Rotation.Slerp( go.WorldRotation, target.Rotation, t );
		}
		gone?.ForEach( go => _shotTargets.Remove( go ) );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastShotPose( GameObject go, Vector3 position, Rotation rotation ) => FollowShotPose( go, position, rotation );

	[Rpc.Broadcast]
	void BroadcastShotRest( GameObject go, Vector3 position, Rotation rotation ) => FollowShotPose( go, position, rotation );

	// A pose from whoever simulates the decoy: the host, or the client it's lent to (RoundManager.Grab) — the host follows
	// those too. Never applied on the machine simulating it, and never from anyone else.
	void FollowShotPose( GameObject go, Vector3 position, Rotation rotation )
	{
		if ( !go.IsValid() || SimulatingLentHere( go ) || DecoyInSim( go ) )
			return;
		var caller = Rpc.Caller;
		bool fromSimulator = caller is null
			? !IsHostAuthority
			: LentDecoys.TryGetValue( go.Id, out var simulator ) ? simulator == caller.Id : caller.IsHost;
		if ( !fromSimulator || (IsHostAuthority && !IsLentDecoy( go )) )
			return; // the host simulates non-lent decoys itself

		// A kinematic body here either way (so things collide with it properly). The pose itself only for snapshot-only
		// decoys: a NETWORKED one is moved by the engine's own transform sync from its owner — easing it here too fought
		// that sync every frame.
		ShotBody( go, simulate: false );
		if ( !EngineSynced( go ) )
			_shotTargets[go] = new Transform( position, rotation );
	}

	[Rpc.Broadcast]
	void BroadcastShotRemove( GameObject go )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;
		RemoveShotProp( go );
	}

	// Every machine: the decoy crumbles — a heavy splat at its clay centre, then it's gone. Late joiners receive the
	// host's live snapshot, where it already is.
	void RemoveShotProp( GameObject go )
	{
		if ( !go.IsValid() )
			return;

		var sculpt = go.Components.Get<SdfSculpture>();
		var at = sculpt.IsValid() && Sdf.TryGetBounds( sculpt.Brushes, out var b )
			? sculpt.WorldTransform.PointToWorld( b.Center )
			: go.WorldPosition;
		SdfCollider.ClaySurface?.PlayCollisionSound( at, 1000f );

		_shotTargets.Remove( go );
		UnregisterSimDecoy( go );
		_shotAuthoredVolume.Remove( go );
		go.Destroy();
	}
}
