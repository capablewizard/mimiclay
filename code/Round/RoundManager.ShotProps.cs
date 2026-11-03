using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mimiclay;

// Shot decoys: the central pellet (the one that catches props) knocks the decoy it strikes LOOSE — a real rigidbody
// tumble from the shot's impulse — and a decoy the shots have whittled below ShotRemoveFraction of its clay is
// removed outright. Players never get here: a caught player pops, and scatter pellets don't mark props at all (see
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
	const float ShotKnockSpeed = 180f;

	/// <summary>A decoy left with less than this fraction of its authored clay is removed.</summary>
	const float ShotRemoveFraction = 0.3f;

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
		QueueShotScan( sculpt ); // removal + island cleanup, off the main thread — lands a frame or two later

		var body = ShotBody( go, simulate: true );
		if ( !body.IsValid() )
			return;

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
	static bool IsShotDecoy( SdfSculpture sculpt )
		=> PropClaims.IsScenery( sculpt )
		&& sculpt.Components.Get<SdfCollider>() is { BuildAsTrigger: false };

	// ── Shot scan: removal + island cleanup (host, worker thread) ─────────────────────────────────────────────
	// Each central-pellet hit queues one SdfIslandScan of the decoy. Coalesced per decoy: a hit landing while a scan
	// is running just marks it dirty, and the running scan goes round again on the newest shape — sustained fire costs
	// one or two scans, not one per shot. The brushes are copied + flattened on the main thread; the sampling and
	// flood fill (a few ms) run on a worker; only the verdict comes back to the main thread.
	//
	// Verdicts, measured against the decoy's INTACT clay (its authored brushes, sampled once on the first scan):
	//  - the main body under ShotRemoveFraction → the whole decoy is removed;
	//  - "Repair props" off: an island big enough to be a PART (ShotSplitFraction of the intact clay and at least
	//    ShotSplitMinVolume) BREAKS OFF as its own decoy (see SplitShotProp), one per scan; anything smaller is a
	//    crumb and is cut away with a subtract box (shape AND collider: the collider takes damage brushes then).
	//    With repair on, craters heal and the pieces join up again, so nothing is cut or split.

	/// <summary>Islands at least this fraction of the intact clay break off as their own object (repair off); smaller
	/// ones are cut away. Low on purpose: handles are small next to their prop — measured 2026-11-03, a mug's handle is
	/// 1.9% of its clay, a teapot spout 5%, a frying pan handle 8%, a saucepan handle 18% (and a shot through the root
	/// leaves the piece smaller still).</summary>
	const float ShotSplitFraction = 0.015f;

	/// <summary>…and at least this much clay (units³, ~a 7-unit cube), so crumbs off a big prop are still just cut.</summary>
	const float ShotSplitMinVolume = 300f;

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

	static List<SdfBrush> Snapshot( List<SdfBrush> brushes )
	{
		var copy = new List<SdfBrush>( brushes.Count );
		foreach ( var b in brushes )
			copy.Add( b.Copy() );
		return copy;
	}

	// Host, main thread.
	void ApplyShotScan( GameObject go, SdfIslandScan.Result scan, float intact )
	{
		if ( intact <= 0f )
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

		if ( Settings.RepairProps )
			return;

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
	const float ShotSplitSpeed = 60f;

	/// <summary>Slack (units) the keep-slabs extend past the shape's bounds, so blends at the edge are covered.</summary>
	const float ShotSplitPad = 4f;

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
		var lo = bounds.Mins - ShotSplitPad;
		var hi = bounds.Maxs + ShotSplitPad;
		var kLo = keepCentre - keepHalf;
		var kHi = keepCentre + keepHalf;
		AddSlab( chunk, new Vector3( lo.x, lo.y, lo.z ), new Vector3( kLo.x, hi.y, hi.z ) );   // -x
		AddSlab( chunk, new Vector3( kHi.x, lo.y, lo.z ), new Vector3( hi.x, hi.y, hi.z ) );   // +x
		AddSlab( chunk, new Vector3( kLo.x, lo.y, lo.z ), new Vector3( kHi.x, kLo.y, hi.z ) ); // -y
		AddSlab( chunk, new Vector3( kLo.x, kHi.y, lo.z ), new Vector3( kHi.x, hi.y, hi.z ) ); // +y
		AddSlab( chunk, new Vector3( kLo.x, kLo.y, lo.z ), new Vector3( kHi.x, kHi.y, kLo.z ) ); // -z
		AddSlab( chunk, new Vector3( kLo.x, kLo.y, kHi.z ), new Vector3( kHi.x, kHi.y, hi.z ) ); // +z

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

	static void AddSlab( List<SdfBrush> brushes, Vector3 min, Vector3 max )
	{
		var size = max - min;
		if ( size.x <= 0.01f || size.y <= 0.01f || size.z <= 0.01f )
			return; // the keep box already reaches this side of the bounds
		brushes.Add( SharpBox( (min + max) * 0.5f, size * 0.5f, damage: false ) );
	}

	static SdfBrush SharpBox( Vector3 centre, Vector3 half, bool damage ) => new()
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
		}

		body.MotionEnabled = simulate;
		body.Gravity = simulate;
		body.EnhancedCcd = simulate; // a shot decoy can be fast and small
		return body;
	}

	// Every machine, per frame (RoundManager.OnUpdate).
	void TickShotProps()
	{
		if ( IsHostAuthority )
		{
			if ( _shotLoose.Count == 0 || _sinceShotPose < ShotPoseInterval )
				return;
			_sinceShotPose = 0f;

			// Loose decoys stay tracked after they settle: a hider shoving one (or another shot) wakes it, and the
			// stream picks up again. Asleep costs one reliable "rest" pose, then nothing.
			_shotLoose.RemoveWhere( go => !go.IsValid() );
			_shotResting.RemoveWhere( go => !go.IsValid() );
			foreach ( var go in _shotLoose )
			{
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
			return;
		}

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
			if ( go == _predictedDecoy )
				continue; // in our hands: our own prediction moves it (RoundManager.Grab); this stream resumes on the drop
			go.WorldPosition = Vector3.Lerp( go.WorldPosition, target.Position, t );
			go.WorldRotation = Rotation.Slerp( go.WorldRotation, target.Rotation, t );
		}
		gone?.ForEach( go => _shotTargets.Remove( go ) );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastShotPose( GameObject go, Vector3 position, Rotation rotation ) => FollowShotPose( go, position, rotation );

	[Rpc.Broadcast]
	void BroadcastShotRest( GameObject go, Vector3 position, Rotation rotation ) => FollowShotPose( go, position, rotation );

	void FollowShotPose( GameObject go, Vector3 position, Rotation rotation )
	{
		if ( IsHostAuthority || (Rpc.Caller is not null && !Rpc.Caller.IsHost) || !go.IsValid() )
			return;

		if ( go != _predictedDecoy ) // our prediction keeps its simulating body (RoundManager.Grab)
			ShotBody( go, simulate: false );
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
		_shotAuthoredVolume.Remove( go );
		go.Destroy();
	}
}
