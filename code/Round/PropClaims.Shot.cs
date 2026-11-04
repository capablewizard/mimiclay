using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Mimiclay;

// ── Shot props in creative ─────────────────────────────────────────────────────────────────────────────────
// Prop hunt's shot decoys (RoundManager.ShotProps) for creative: the central pellet knocks the prop it hits loose,
// a prop shot below RoundManager.ShotRemoveFraction of its clay crumbles away, and with "Repair props" off
// (CreativeSettings.RepairProps) loose crumbs are cut off and a detached part big enough breaks off as its own prop.
//
// Same rules and thresholds, different plumbing. Prop hunt leaves its decoys as scene objects (a round's decoys must
// stay what they look like) and streams their poses by hand. Creative already has a way to make scene clay a real
// physics object — the CONVERSION a grab does (ConvertSceneProp → host-owned released scenery, LooseProps = a free
// rigidbody) — so a shot just does that: the prop becomes released scenery the moment it's hit, and from there the
// engine's network sync moves it, the host's shape publish carries every cut, and a broken-off piece is a new pawn
// of the same kind. Possessed props and hunters are never knocked, removed or split; a frozen prop stays put (only
// its shape takes the damage), and a prop someone's holding (lent to them) is left alone until it's back.
public sealed partial class PropClaims
{
	static bool CreativeShots => CreativeManager.Current.IsValid();
	static bool CreativeRepair => CreativeManager.Current.IsValid() && CreativeManager.Current.Settings.RepairProps;

	/// <summary>Creative with "Repair props" off: craters are permanent, so props' colliders take them too.</summary>
	internal static bool ShotCarvesColliders => CreativeShots && !CreativeRepair;

	// The released scenery pawn this clay is the disguise of — nobody wearing it — or null.
	static HiderController FreeSceneryOf( SdfSculpture sculpt )
	{
		var hider = sculpt.IsValid() ? sculpt.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ) : null;
		return hider.IsValid() && IsReleased( hider ) && hider.DisguiseSculpture == sculpt ? hider : null;
	}

	/// <summary>Every machine, from HunterController.BroadcastCarve right before a crater lands (creative's half of
	/// RoundManager.PrepareShotCarve): with repair off, a scene prop's or released prop's collider takes craters.
	/// A prop converted later carries the flag in its spawn snapshot (SpawnPropPawn).</summary>
	internal static void PrepareCreativeShotCarve( SdfSculpture sculpt )
	{
		if ( !ShotCarvesColliders || (!RoundManager.IsShotDecoy( sculpt ) && !FreeSceneryOf( sculpt ).IsValid()) )
			return;
		if ( sculpt.Components.Get<SdfCollider>() is { BuildAsTrigger: false } collider )
			collider.CarveCollision = true;
	}

	// Free to knock, cut, split or remove: released, and nobody holding, borrowing or sculpting it.
	bool ShotFree( HiderController hider )
	{
		if ( !hider.IsValid() || !IsReleased( hider ) || IsLent( hider.GameObject ) )
			return false;
		var id = hider.GameObject.Id;
		return !Held.Values.Contains( id ) && !Editing.Values.Contains( id );
	}

	/// <summary>Host, from HunterController.BroadcastCarve once a central pellet's crater is in (creative only).</summary>
	internal void OnCreativePropShot( SdfSculpture sculpt, Vector3 hit, Vector3 dir )
	{
		if ( !CreativeShots || (Networking.IsActive && !Networking.IsHost) || !sculpt.IsValid() )
			return;

		HiderController hider;
		if ( RoundManager.IsShotDecoy( sculpt ) )
		{
			// Still a scene object: make it a real prop, exactly as a grab would (the crater just landed is in its
			// brushes, so the pawn wears it).
			if ( !IsClaimable( sculpt ) || !_claimedScene.Add( sculpt.GameObject.Id ) )
				return;
			var pawn = ConvertSceneProp( sculpt, null, scenery: true );
			if ( !pawn.IsValid() )
			{
				_claimedScene.Remove( sculpt.GameObject.Id );
				return;
			}
			hider = pawn.Components.Get<HiderController>();
			hider.ReleaseControl();
			ReleasedProps[pawn.Id] = true;
		}
		else
		{
			hider = FreeSceneryOf( sculpt );
		}

		if ( !ShotFree( hider ) )
			return;

		var id = hider.GameObject.Id;
		if ( !FrozenProps.ContainsKey( id ) )
		{
			LooseProps[id] = true;
			_shotKicks[id] = new ShotKick { Point = hit, Impulse = dir.Normal * RoundManager.ShotKnockSpeed };
		}
		QueueCreativeShotScan( hider );
	}

	// ── Kicks: applied once the prop is a free rigidbody (a fresh conversion takes a tick or two) ──────────────
	struct ShotKick
	{
		public Vector3 Point;     // world hit point (impulse is applied there, so off-centre shots spin it)
		public Vector3 Impulse;   // per unit mass
		public Vector3? Velocity; // a broken-off piece: set outright instead
		public Vector3 Spin;
		public RealTimeSince Since;
	}

	readonly Dictionary<Guid, ShotKick> _shotKicks = new();
	const float ShotKickTimeout = 1f;

	// Host, per frame.
	void TickShotKicks()
	{
		if ( _shotKicks.Count == 0 )
			return;

		foreach ( var (id, kick) in _shotKicks.ToList() )
		{
			var hider = Scene.Directory.FindByGuid( id )?.Components.Get<HiderController>();
			if ( !hider.IsValid() || kick.Since > ShotKickTimeout )
			{
				_shotKicks.Remove( id );
				continue;
			}

			var body = hider.PhysicsBody;
			if ( !hider.InLoosePhysics || !body.IsValid() || body.PhysicsBody is not { } physics )
				continue;

			_shotKicks.Remove( id );
			body.Sleeping = false;
			if ( kick.Velocity is { } velocity )
			{
				body.Velocity = velocity;
				body.AngularVelocity = kick.Spin;
			}
			if ( kick.Impulse != Vector3.Zero )
				body.ApplyImpulseAt( kick.Point, kick.Impulse * MathF.Max( physics.Mass, 1f ) );
		}
	}

	// ── Scan: removal + crumbs + breaking off (host; the sampling runs on a worker) ─────────────────────────────
	// Coalesced per prop like the round's: a hit during a scan marks it dirty and the scan goes round again.
	readonly HashSet<Guid> _shotScanBusy = new();
	readonly HashSet<Guid> _shotScanDirty = new();
	readonly Dictionary<Guid, float> _shotIntact = new(); // intact clay volume, sampled once per prop

	void QueueCreativeShotScan( HiderController hider )
	{
		var id = hider.GameObject.Id;
		if ( !_shotScanBusy.Add( id ) )
		{
			_shotScanDirty.Add( id );
			return;
		}
		_ = RunCreativeShotScan( hider );
	}

	async Task RunCreativeShotScan( HiderController hider )
	{
		var id = hider.GameObject.Id;
		try
		{
			do
			{
				_shotScanDirty.Remove( id );
				var sculpt = hider.IsValid() ? hider.DisguiseSculpture : null;
				if ( !sculpt.IsValid() || sculpt.Brushes is not { Count: > 0 } brushes )
					return;

				// Main thread: snapshot (Flatten resolves groups through prefab lookups — never on the worker).
				var all = RoundManager.Snapshot( SdfGroups.Flatten( brushes ) );
				if ( !Sdf.TryGetBounds( all, out var bounds ) )
					return;

				List<SdfBrush> intact = null;
				BBox intactBounds = default;
				if ( !_shotIntact.ContainsKey( id ) )
				{
					intact = RoundManager.Snapshot( SdfGroups.Flatten( brushes.GetRange( 0, Math.Min( sculpt.AuthoredBrushCount, brushes.Count ) ) ) );
					Sdf.TryGetBounds( intact, out intactBounds );
				}

				await GameTask.WorkerThread();
				var scan = SdfIslandScan.Scan( all, bounds );
				float intactVolume = intact is not null ? SdfIslandScan.Volume( intact, intactBounds ) : 0f;
				await GameTask.MainThread();

				if ( !this.IsValid() || !hider.IsValid() || !sculpt.IsValid() )
					return;
				if ( intact is not null )
					_shotIntact[id] = intactVolume;

				await ApplyCreativeShotScan( hider, sculpt, scan, _shotIntact.GetValueOrDefault( id ) );
			}
			while ( _shotScanDirty.Contains( id ) && hider.IsValid() );
		}
		finally
		{
			_shotScanBusy.Remove( id );
			_shotScanDirty.Remove( id );
		}
	}

	async Task ApplyCreativeShotScan( HiderController hider, SdfSculpture sculpt, SdfIslandScan.Result scan, float intact )
	{
		if ( intact <= 0f || !ShotFree( hider ) )
			return; // picked up (or sculpted) since the shot: the next hit after it's back rescans

		var go = hider.GameObject;
		if ( scan.MainVolume < intact * RoundManager.ShotRemoveFraction )
		{
			BroadcastShotCrumble( sculpt.WorldTransform.PointToWorld( (scan.Bounds.Mins + scan.Bounds.Maxs) * 0.5f ) );
			_shotIntact.Remove( go.Id );
			_shotKicks.Remove( go.Id );
			LooseProps.Remove( go.Id );
			FrozenProps.Remove( go.Id );
			go.Destroy(); // host-owned network object: gone everywhere (the Disguise with it)
			return;
		}

		if ( CreativeRepair )
			return; // craters heal and the pieces join up again: nothing to cut or split

		var cuts = new List<SdfBrush>();
		SdfIslandScan.Island? split = null;
		foreach ( var island in scan.Islands )
		{
			if ( !island.Safe )
				continue;
			if ( island.Volume >= intact * RoundManager.ShotSplitFraction && island.Volume >= RoundManager.ShotSplitMinVolume )
			{
				if ( split is null || island.Volume > split.Value.Volume )
					split = island; // the biggest breaks off now; the rest wait for the next scan
				continue;
			}
			cuts.Add( RoundManager.SharpBox( island.Center, island.HalfExtents, damage: true ) );
		}

		if ( split is { } chunk && sculpt.Brushes.Count + cuts.Count + 7 <= SdfBrushPacker.MaxBrushes )
		{
			await SplitCreativeProp( hider, sculpt, chunk, new BBox( scan.Bounds.Mins, scan.Bounds.Maxs ), cuts );
			return;
		}

		if ( cuts.Count == 0 )
			return;
		foreach ( var cut in cuts )
		{
			if ( sculpt.Brushes.Count >= SdfBrushPacker.MaxBrushes )
				break;
			sculpt.Brushes.Add( cut );
		}
		sculpt.Rebuild(); // host-owned: the commit publishes the new shape to everyone (SdfNetworkSync)
	}

	// The original keeps everything but the chunk (one subtract box, plus this scan's crumb cuts); the chunk becomes a
	// new released prop wearing the same brushes with six subtract slabs around the chunk's box (all made AUTHORED —
	// the distant LOD mesh skips damage, so a damage-flagged copy would show the whole original from afar). Both
	// colliders are built on a worker first, straight into the collider cache, so the swap lands in one frame as cache
	// hits — two overlapping colliders on the host would fling the pieces apart.
	async Task SplitCreativeProp( HiderController hider, SdfSculpture sculpt, SdfIslandScan.Island island, BBox bounds, List<SdfBrush> cuts )
	{
		var keepCentre = island.Center;
		var keepHalf = island.HalfExtents;
		var cut = RoundManager.SharpBox( keepCentre, keepHalf, damage: true );

		var chunk = new List<SdfBrush>( sculpt.Brushes.Count + 6 );
		foreach ( var b in sculpt.Brushes )
		{
			var c = b.Copy();
			c.Damage = false;
			c.Shrinks = false;
			chunk.Add( c );
		}
		RoundManager.AddKeepSlabs( chunk, bounds, keepCentre, keepHalf );

		var original = new List<SdfBrush>( sculpt.Brushes );
		original.AddRange( cuts );
		original.Add( cut );
		await SdfCollisionBuilder.BuildAsync( original );
		await SdfCollisionBuilder.BuildAsync( chunk );

		if ( !this.IsValid() || !hider.IsValid() || !sculpt.IsValid() || !ShotFree( hider ) )
			return;

		// One frame: cut the original, spawn the piece exactly where it already stands.
		sculpt.Brushes.AddRange( cuts );
		sculpt.Brushes.Add( cut );
		sculpt.Rebuild();

		var root = hider.GameObject;
		var piece = SpawnPropPawn( chunk, root.WorldTransform, root.WorldTransform.ToLocal( sculpt.WorldTransform ),
			$"{root.Name} (piece)", owner: null, scenery: true );
		var pieceHider = piece.IsValid() ? piece.Components.Get<HiderController>() : null;
		if ( !pieceHider.IsValid() )
			return;
		pieceHider.ReleaseControl();
		ReleasedProps[piece.Id] = true;
		LooseProps[piece.Id] = true;

		// The piece flies on from where the original was going, the two pushed apart along the split.
		var from = hider.PhysicsBody;
		bool moving = from.IsValid() && from.MotionEnabled;
		var apart = sculpt.WorldTransform.NormalToWorld( (keepCentre - bounds.Center).Normal );
		_shotKicks[piece.Id] = new ShotKick
		{
			Velocity = (moving ? from.Velocity : Vector3.Zero) + apart * RoundManager.ShotSplitSpeed,
			Spin = moving ? from.AngularVelocity : Vector3.Zero,
		};
		if ( moving )
			from.Velocity -= apart * RoundManager.ShotSplitSpeed * 0.5f;
	}

	/// <summary>Host → everyone: a shot-to-bits prop crumbles — the heavy splat where its clay was.</summary>
	[Rpc.Broadcast]
	void BroadcastShotCrumble( Vector3 at )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;
		SdfCollider.ClaySurface?.PlayCollisionSound( at, 1000f );
	}
}
