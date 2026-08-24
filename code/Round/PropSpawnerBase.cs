using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Shared machinery behind <see cref="RandomPropSpawner"/> (spawns exactly one prop at this exact spot) and
/// <see cref="RandomVolumeSpawner"/> (spawns any number of props scattered anywhere inside a volume) — picking
/// prefabs, ground-aligning them (subtract-aware, pivot-agnostic), avoiding overlap with each other AND every
/// other spawner, replicating the picks, and a live editor preview. A subtype only varies HOW MANY picks to
/// roll (<see cref="RollSlotCount"/>, base = always exactly 1) and WHERE within its own local space each one
/// can land (<see cref="RollLocalOffset"/>, base = always <see cref="Vector3.Zero"/>).
///
/// <b>Networking.</b> The host is the only machine that ever rolls a pick or builds a prop (see
/// <see cref="IsHostAuthority"/>) — clients don't independently decide anything, and don't need to: each
/// built prop is handed to <see cref="GameObject.NetworkSpawn()"/>, so the engine itself ships it to every
/// connected client, AND to late joiners via the spawn snapshot, exactly like every other host-built object
/// in this project (see <c>LobbyController</c>/<c>PropClaims</c>). This is simpler and more robust than
/// syncing the DECISION (indices/yaws/offsets) and having every machine independently rebuild from it — that
/// was tried first and doesn't actually work here, because this component is scene-placed, and a scene-placed
/// component's own <c>[Sync]</c> changes don't replicate (only a NetworkSpawn'd object's do — see the comments
/// on <c>RoundManager</c>/<c>LobbyManager</c>/<c>PropClaims</c>); syncing the RESULT instead sidesteps that
/// entirely. Outside a live networked round (editor preview, or a non-networked local session) there's only
/// one machine anyway, so the same code path just runs locally without ever calling NetworkSpawn.
/// </summary>
public abstract class PropSpawnerBase : Component, Component.ExecuteInEditor
{
	[Property] public List<GameObject> Prefabs { get; set; } = new();

	/// <summary>Chance of a given slot spawning nothing at all instead of picking a prefab — lets picks
	/// sometimes come up empty for variety, rather than every slot always being filled.</summary>
	[Property, Range( 0f, 1f )] public float NoneChance { get; set; } = 0f;

	/// <summary>Spin each placed prop to its own random yaw instead of always facing this spawner's own
	/// authored rotation — decorative clutter reads as hand-placed junk when it's all facing the same way.</summary>
	[Property] public bool RandomizeRotation { get; set; } = true;

	protected bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	const int None = -1;       // a decided slot that rolled "spawn nothing"
	const int Undecided = -1;  // ChosenSlotCount hasn't been rolled at all yet (distinct meaning, same sentinel
	                            // value is fine since they're never compared against each other)

	// Purely local bookkeeping now — only the host (or the one machine in a non-networked session) ever reads
	// or writes these; nothing here needs to be [Sync] (see class doc).
	int ChosenSlotCount = Undecided;
	readonly Dictionary<int, int> ChosenIndices = new();
	readonly Dictionary<int, float> ChosenYaws = new();
	readonly Dictionary<int, Vector3> ChosenOffsets = new();

	// Retry list: a prop that was built but couldn't NetworkSpawn yet (GameObject.NetworkSpawn() can return
	// false — e.g. Connection.Local.CanSpawnObjects isn't granted the instant a freshly-loaded map scene
	// starts ticking, a real observed race, not just theoretical) — see OnUpdate(). Kept SEPARATELY from
	// _spawned's own lifetime: the object stays right where BuildAndAlign put it and is simply re-offered to
	// NetworkSpawn() every subsequent frame until it succeeds, rather than silently staying host-only forever
	// (which is exactly the "only spawns for the host" bug this whole mechanism replaced).
	readonly List<GameObject> _pendingNetworkSpawn = new();

	readonly List<GameObject> _spawned = new();

	// Cached once per placed prop (its bounds never change after AlignToGround settles it) so
	// OverlapsAnotherSpawner doesn't re-run a full SurfaceNetsMesher rebuild for the SAME already-placed prop
	// on every single candidate check some OTHER spawner happens to try — that was O(candidates × every
	// other spawner's every placed prop), all real mesh-building work, and by far the biggest remaining CPU
	// cost once the GPU-texture trial churn (see BuildAndAlign's trialOnly doc) was fixed.
	readonly Dictionary<GameObject, BBox> _spawnedBounds = new();

	// On the host, this component's very first OnUpdate can tick BEFORE Networking.IsActive flips true on
	// the host's own machine (the scene starts ticking before the network session finishes activating) — so
	// a Decide()+Apply() that happens to land on that exact frame builds everything with Networking.IsActive
	// still false, meaning Apply() never calls NetworkSpawn() at all (props built host-only forever, since
	// ChosenSlotCount is no longer Undecided afterward so OnUpdate never revisits that branch). Tracked here
	// so the false->true transition can retroactively NetworkSpawn what was already (correctly) built,
	// instead of re-rolling anything.
	bool _lastNetworkingActive;

	protected override void OnUpdate()
	{
		// ExecuteInEditor makes this tick in the editor too (not just play mode) — that's what powers the
		// live preview below; it does NOT mean a real game's decision-making runs any differently, since
		// Scene.IsEditor is only ever true outside an actual running session.
		if ( Scene.IsEditor )
		{
			UpdateEditorPreview();
			return;
		}

		// Only the host (or the sole machine in a non-networked session) ever decides or builds anything —
		// a client just receives the host's already-built, NetworkSpawn'd props automatically (see Apply()).
		// There's nothing to poll for here on a client: it isn't tracking any synced decision state at all.
		if ( !IsHostAuthority )
			return;

		if ( ChosenSlotCount == Undecided )
		{
			if ( _decideSlot < 0 )
				StartDecide();
			StepDecide();
		}
		else if ( Networking.IsActive && !_lastNetworkingActive )
		{
			// Networking just flipped on AFTER we already built everything locally (see _lastNetworkingActive's
			// doc) — the props exist and are correct, they just never actually went out over the wire. Spawn
			// the SAME already-built objects now rather than re-rolling anything.
			NetworkSpawnAlreadyBuilt();
		}

		_lastNetworkingActive = Networking.IsActive;

		if ( _pendingNetworkSpawn.Count > 0 )
			RetryPendingNetworkSpawns();
	}

	// See _lastNetworkingActive's doc.
	void NetworkSpawnAlreadyBuilt()
	{
		foreach ( var go in _spawned )
		{
			if ( !go.IsValid() )
				continue;

			if ( !go.NetworkSpawn() )
				_pendingNetworkSpawn.Add( go );
		}
	}

	// See _pendingNetworkSpawn's doc — keeps trying until every prop this spawner built has actually gone
	// out over the wire, instead of a single attempt that can silently leave it host-only forever.
	void RetryPendingNetworkSpawns()
	{
		for ( int i = _pendingNetworkSpawn.Count - 1; i >= 0; i-- )
		{
			var go = _pendingNetworkSpawn[i];
			if ( !go.IsValid() )
			{
				_pendingNetworkSpawn.RemoveAt( i );
				continue;
			}

			if ( go.NetworkSpawn() )
				_pendingNetworkSpawn.RemoveAt( i );
		}
	}

	/// <summary>How many prefabs to try to place this decide. Base (a single fixed point) always rolls
	/// exactly one; override to scatter several (see <see cref="RandomVolumeSpawner"/>).</summary>
	protected virtual int RollSlotCount() => 1;

	/// <summary>Rolls a LOCAL-space offset for where one slot's pick should land within this spawner. Base
	/// implementation (a single fixed point) always returns <see cref="Vector3.Zero"/> — override to roll
	/// somewhere inside whatever region a subtype defines (see <see cref="RandomVolumeSpawner"/>).</summary>
	protected virtual Vector3 RollLocalOffset() => Vector3.Zero;

	// The actual world point a slot's pick landed on/near (before ground-alignment shifts it vertically) —
	// WorldPosition plus that slot's offset rotated into world space.
	Vector3 SpawnPointFor( Vector3 localOffset ) => WorldPosition + WorldRotation * localOffset;

	int _previewConfigHash;
	Vector3 _previewAppliedPosition;
	Rotation _previewAppliedRotation;
	bool _previewSuppressed; // true right after an explicit ClearPreview(), until the config actually changes
	                          // or PreviewPick() is called again — stops the live preview from immediately
	                          // refilling a spot that was just deliberately cleared.

	/// <summary>Set by a subtype's own gizmo handling (e.g. <see cref="RandomVolumeSpawner"/>'s
	/// <c>Gizmo.Pressed.This</c> right after <c>Gizmo.Control.BoundingBox</c>) while the user is actively
	/// mid-drag on some control that also happens to feed <see cref="ComputeConfigHash"/> — resizing the
	/// region changes <c>Bounds</c> every single frame of the drag, and without this the live preview would
	/// re-roll and rebuild every prop on every one of those frames (visibly janky, and expensive with a high
	/// <c>Count</c>). While true, <see cref="UpdateEditorPreview"/> just leaves whatever's already placed
	/// alone; the moment the drag ends the very next tick sees the settled config as "changed" (one clean
	/// final re-roll) exactly like any other edit.</summary>
	protected bool SuppressPreviewWhileDragging { get; set; }

	/// <summary>Editor-only live preview: rolls a pick automatically the moment the config actually changes
	/// (instead of needing a manual "Preview Random Pick" click just to see an edit take effect), and re-aligns
	/// the SAME already-chosen picks as you drag the spawner around (moving it shouldn't re-roll what's shown —
	/// that would make it impossible to just reposition a spot without losing the picks you were looking at). A
	/// manual re-roll (the button below) still works on top of this for cycling through picks without touching
	/// any settings.</summary>
	void UpdateEditorPreview()
	{
		if ( SuppressPreviewWhileDragging )
			return; // mid-drag — see that property's doc; the settled value is picked up once it's released

		int hash = ComputeConfigHash();
		bool configChanged = hash != _previewConfigHash;
		_previewConfigHash = hash;

		if ( configChanged )
			_previewSuppressed = false; // a real settings edit always un-suppresses and rolls fresh

		if ( _previewSuppressed )
			return;

		if ( ChosenSlotCount == Undecided || configChanged )
		{
			Decide();
			Apply();
			_previewAppliedPosition = WorldPosition;
			_previewAppliedRotation = WorldRotation;
			return;
		}

		// Nothing decided has changed, but the spawner itself might have moved/rotated in the viewport since
		// the last apply — re-place the SAME picks rather than leaving a stale preview sitting in the old spot.
		if ( _spawned.Count > 0 && (WorldPosition != _previewAppliedPosition || WorldRotation != _previewAppliedRotation) )
		{
			Apply();
			_previewAppliedPosition = WorldPosition;
			_previewAppliedRotation = WorldRotation;
		}
	}

	/// <summary>Extra content that should also trigger a fresh preview roll when it changes — a volume
	/// spawner folds its bounds/count into this too. Base contribution is just Prefabs/NoneChance/
	/// RandomizeRotation.</summary>
	protected virtual int ComputeConfigHash()
	{
		var hash = new HashCode();
		hash.Add( NoneChance );
		hash.Add( RandomizeRotation );

		if ( Prefabs is not null )
			foreach ( var prefab in Prefabs )
				hash.Add( prefab?.Id ?? default );

		return hash.ToHashCode();
	}

	// Host-only: rolls RollSlotCount() slots, each independently picking a prefab — candidates are tried in
	// LEAST-used-first order (see useCount below), not a flat random cycle, so a multi-slot spawner (chiefly
	// RandomVolumeSpawner) maximises how many DIFFERENT prefabs actually show up instead of the same one or
	// two dominating by chance — and skips any candidate whose real placed bounds would overlap something
	// ALREADY placed — either another already-decided slot in THIS SAME pass (so a volume spawner's own
	// scattered props don't overlap each other) or another spawner's already-resolved prop entirely (so two
	// spawner regions placed too close together don't intersect either). Builds a real trial clone per
	// candidate (BuildAndAlign — the exact same path Apply() uses) purely to measure it accurately
	// (subtract-aware, ground-aligned) then throws it away; Apply() below builds the ones that actually
	// stick, once ChosenSlotCount settles. A slot with every option colliding, an empty Prefabs list, or a
	// NoneChance roll settles on "None" for just that slot rather than forcing a guaranteed overlap.
	void Decide()
	{
		int count = Math.Max( 0, RollSlotCount() );

		ChosenIndices.Clear();
		ChosenYaws.Clear();
		ChosenOffsets.Clear();

		var placedThisPass = new List<BBox>();

		// How many times each prefab has already been picked THIS decide — candidates are offered
		// least-used-first (ties broken randomly), so with e.g. 5 prefabs and 10 slots every prefab shows up
		// twice before any shows up a third time, instead of a flat random draw clumping by chance.
		var useCount = Prefabs is { Count: > 0 } ? new int[Prefabs.Count] : Array.Empty<int>();

		for ( int slot = 0; slot < count; slot++ )
			DecideSlot( slot, placedThisPass, useCount );

		ChosenSlotCount = count;
	}

	// One slot's worth of Decide()'s work — pulled out so the runtime path (see StartDecide/StepDecide) can
	// spread this across multiple frames instead of paying for every slot of every spawner in the level on
	// the exact frame the map loads (see those methods' doc for why that used to freeze the game). Each
	// candidate builds a real trial clone (mesh + a physics overlap test) purely to measure/validate it, which
	// is NOT cheap — Decide() (the editor-preview/PreviewPick path, where there's only ever one spawner being
	// edited at a time) still calls this once per slot in a tight loop, unchanged.
	void DecideSlot( int slot, List<BBox> placedThisPass, int[] useCount )
	{
		float yaw = RandomizeRotation ? (float)(Random.Shared.NextDouble() * 360.0) : 0f;
		var offset = RollLocalOffset();
		int chosenIndex = None;

		if ( Prefabs is { Count: > 0 } && Random.Shared.NextDouble() >= NoneChance )
		{
			// Least-used-first with a random tie-break (see the old LINQ version's reasoning) — done with
			// two reusable scratch arrays and a single Array.Sort instead of two chained OrderBy's plus a
			// ToList per slot, which allocated (and re-boxed the whole prefab index range) on every single
			// slot of every spawner.
			var order = BuildCandidateOrder( useCount );

			foreach ( var index in order )
			{
				var prefab = Prefabs[index];
				if ( !prefab.IsValid() )
					continue;

				// No trial clone: the candidate's bounds are derived from this prefab's ONE cached
				// measurement (see GetCandidateBounds) instead of cloning + re-meshing it per candidate.
				if ( GetCandidateBounds( prefab, offset, yaw ) is not { } b )
					continue;

				if ( OverlapsAnySpawnedBounds( b, placedThisPass ) || OverlapsAnotherSpawner( b ) || OverlapsWorld( b ) )
					continue;

				chosenIndex = index;
				useCount[index]++;
				placedThisPass.Add( b );
				break;
			}
		}

		ChosenYaws[slot] = yaw;
		ChosenOffsets[slot] = offset;
		ChosenIndices[slot] = chosenIndex;
	}

	// Scratch buffers for BuildCandidateOrder — reused across every slot (and every decide) of this spawner
	// rather than allocating a fresh ordering per slot.
	int[] _orderScratch;
	long[] _orderKeys;

	// Least-used-first, ties broken randomly. Packs "times used" in the high bits and a random tie-break in
	// the low bits of a single long key so one Array.Sort does the whole ordering.
	int[] BuildCandidateOrder( int[] useCount )
	{
		int n = Prefabs.Count;
		if ( _orderScratch is null || _orderScratch.Length != n )
		{
			_orderScratch = new int[n];
			_orderKeys = new long[n];
		}

		for ( int i = 0; i < n; i++ )
		{
			_orderScratch[i] = i;
			int used = i < useCount.Length ? useCount[i] : 0;
			_orderKeys[i] = ((long)used << 32) | (uint)Random.Shared.Next();
		}

		Array.Sort( _orderKeys, _orderScratch );
		return _orderScratch;
	}

	// A prefab's LOCAL-space bounds, measured exactly once ever (per prefab) instead of once per candidate
	// per slot per spawner. Measuring used to mean cloning the prefab into the scene and running
	// SurfaceNetsMesher over its SDF brushes — by far the dominant cost of a decide, and completely
	// redundant work since a prefab's own geometry never changes between candidates. Static because the
	// measurement is a property of the PREFAB, not of whichever spawner happens to be asking.
	static readonly Dictionary<Guid, BBox?> s_prefabLocalBounds = new();

	// The world bounds a candidate WOULD occupy if placed at this slot's offset/yaw, computed analytically
	// from the cached local bounds — the clone+align round-trip only happens for picks that actually stick
	// (Apply). Ground-alignment is reproduced here the same way AlignToGround does it (trace down from the
	// spawn point, shift so the bounds' underside sits on the hit), so what's tested is what gets built.
	BBox? GetCandidateBounds( GameObject prefab, Vector3 localOffset, float yaw )
	{
		if ( GetPrefabLocalBounds( prefab ) is not { } local )
			return null;

		var point = SpawnPointFor( localOffset );
		var rotation = RandomizeRotation ? WorldRotation * Rotation.FromYaw( yaw ) : WorldRotation;
		var tx = new Transform( point, rotation, WorldScale );

		var world = local.Transform( tx );

		var tr = Scene.Trace.Ray( point + Vector3.Up * 4f, point + Vector3.Down * 512f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		if ( !tr.Hit )
			return world;

		var shift = Vector3.Up * (tr.EndPosition.z - world.Mins.z);
		return new BBox( world.Mins + shift, world.Maxs + shift );
	}

	// Measures a prefab once by cloning it disabled, off to the side, with only the two component types
	// ComputeWorldBounds actually reads left enabled (see BuildAndAlign's trialOnly doc for why enabling
	// anything else here is actively harmful), then caches the result forever.
	BBox? GetPrefabLocalBounds( GameObject prefab )
	{
		if ( s_prefabLocalBounds.TryGetValue( prefab.Id, out var cached ) )
			return cached;

		var probe = prefab.Clone( new CloneConfig( new Transform( Vector3.Zero, Rotation.Identity, 1f ),
			startEnabled: false, name: $"Bounds Probe ({prefab.Name})" ) );

		BBox? result = null;
		if ( probe.IsValid() )
		{
			probe.Flags |= GameObjectFlags.NotSaved;

			foreach ( var c in probe.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ) )
				if ( c is not (ModelRenderer or SdfSculpture) )
					c.Enabled = false;

			probe.Enabled = true; // only actually activates the two component types left enabled above

			result = ComputeWorldBounds( probe ); // probe sits at the origin unrotated/unscaled, so its
												   // "world" bounds ARE the prefab's local bounds
			probe.Destroy();
		}

		s_prefabLocalBounds[prefab.Id] = result;
		return result;
	}

	// ── Runtime incremental decide (spread across frames) ───────────────────────────────────────────────
	// Freeze-at-map-start fix: a spawner with a high Count, or just many spawners all deciding on the exact
	// same frame the map finishes loading, used to pay for EVERY slot's EVERY candidate — each one a real
	// trial clone (mesh build) plus a physics overlap test against every other spawner's already-placed
	// props AND the world — all synchronously in one frame. Nothing about that work is per-frame-cheap, so
	// doing a whole spawner's worth (up to 64 slots for a volume) in a single OnUpdate tick, times however
	// many spawners a level has, is exactly what a load-time hitch looks like. This spreads it across ticks
	// with a TIME budget (not a fixed slot count) — a slot that finds a candidate on its first try is cheap,
	// one that has to reject most of Prefabs isn't, so a fixed slot count either wastes frames on the cheap
	// case or still spikes on the expensive one. Plain per-OnUpdate-tick state (async/Task.Yield was tried
	// here and dropped — same result, more moving parts).
	int _decideSlot = -1; // -1 = not currently mid-decide; otherwise the next slot index to process
	int _decideCount;
	List<BBox> _decidePlaced;
	int[] _decideUseCount;
	const double PerTickBudgetMs = 4.0;

	// A per-SPAWNER budget alone isn't enough: at map load, potentially every spawner in the level has
	// ChosenSlotCount == Undecided on the SAME frame, and each one independently getting its own
	// PerTickBudgetMs allowance means the TOTAL cost that frame is (number of spawners currently deciding) ×
	// PerTickBudgetMs — unbounded again, just like before any of this existed. This is a budget SHARED across
	// every PropSpawnerBase instance: whichever spawners happen to tick first in a given frame spend from it,
	// and once it's gone for that "frame" every other spawner's StepDecide bails immediately (0 slots done,
	// tries again next tick) — so no matter how many spawners are simultaneously deciding, the aggregate cost
	// is capped at roughly GlobalBudgetMs per frame, and a level with many spawners just takes proportionally
	// more frames to finish (spread wider), not more time in any single one.
	static RealTimeSince s_sinceGlobalReset = 999f;
	static double s_globalUsedMs;
	const double GlobalResetIntervalMs = 8; // ~once per frame at a common refresh rate, without needing an
	                                          // actual engine frame-index API
	const double GlobalBudgetMs = 6.0; // total, shared across every spawner, per "frame" window above. Tuned
										// up from 2ms once a slot stopped meaning "clone + re-mesh a prefab per
										// candidate" (see GetCandidateBounds) — the work left in a slot is a
										// couple of traces and some box tests, so a wider window finishes the
										// level in a handful of frames without ever coming near a hitch.

	// True if there's still some of this frame's shared budget left — checked before doing each slot's work
	// so an EXHAUSTED spawner further down the tick order does zero slots this tick rather than still paying
	// for at least one.
	static bool GlobalBudgetAvailable()
	{
		if ( s_sinceGlobalReset * 1000.0 >= GlobalResetIntervalMs )
		{
			s_sinceGlobalReset = 0;
			s_globalUsedMs = 0;
		}

		return s_globalUsedMs < GlobalBudgetMs;
	}

	void StartDecide()
	{
		_decideCount = Math.Max( 0, RollSlotCount() );
		ChosenIndices.Clear();
		ChosenYaws.Clear();
		ChosenOffsets.Clear();
		_decidePlaced = new List<BBox>();
		_decideUseCount = Prefabs is { Count: > 0 } ? new int[Prefabs.Count] : Array.Empty<int>();
		_decideSlot = _decideCount > 0 ? 0 : _decideCount; // 0 slots: fall straight through to finish below
	}

	// Called once per OnUpdate tick while a decide is in progress — keeps doing slots until every slot is
	// done, this spawner's OWN tick budget is spent, or the budget SHARED across every spawner (see
	// GlobalBudgetAvailable's doc) is spent, whichever comes first.
	void StepDecide()
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();

		while ( _decideSlot < _decideCount )
		{
			if ( !GlobalBudgetAvailable() )
				return; // another spawner already spent this frame's shared allowance — our turn next tick

			DecideSlot( _decideSlot, _decidePlaced, _decideUseCount );
			_decideSlot++;

			double elapsed = sw.Elapsed.TotalMilliseconds;
			s_globalUsedMs += elapsed;
			sw.Restart();

			if ( elapsed >= PerTickBudgetMs || s_globalUsedMs >= GlobalBudgetMs )
				return; // spent our own or the shared budget this tick — pick up the rest next OnUpdate
		}

		// Every slot done — settle it (this is what OnUpdate's Undecided check reads) and build for real.
		ChosenSlotCount = _decideCount;
		_decideSlot = -1;
		_decidePlaced = null;
		_decideUseCount = null;
		Apply();
	}


	static bool OverlapsAnySpawnedBounds( BBox candidate, List<BBox> bounds )
	{
		foreach ( var b in bounds )
			if ( candidate.Overlaps( b ) )
				return true;

		return false;
	}

	// Real solid-geometry check (walls, floors, furniture, terrain — anything with a collider already in the
	// map) — the two checks above only ever compare against OTHER random-spawner picks, so without this nothing
	// stopped a roll from landing a candidate half-embedded in a wall or clipped through the floor. A
	// zero-length Trace.Box (from == to) is a pure overlap test at that position — not an actual sweep — using
	// the candidate's REAL (subtract-aware) world bounds as the shape, centred on itself. Ignores this
	// spawner's hierarchy (a marker sitting inside/behind level geometry shouldn't block its own region) and
	// triggers (non-solid by definition, so overlapping one is never actually a placement problem).
	bool OverlapsWorld( BBox candidate )
	{
		var half = (candidate.Maxs - candidate.Mins) * 0.25f;
		var localBox = new BBox( -half, half );

		var tr = Scene.Trace.Box( localBox, candidate.Center, candidate.Center )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "trigger" )
			.Run();

		return tr.Hit;
	}

	// Every bounds every spawner has actually placed, in one flat list. This used to walk
	// Scene.GetAllComponents<PropSpawnerBase>() (a full scene-wide component query, allocating and scanning
	// every spawner in the level) and then that spawner's dictionary — PER CANDIDATE, per slot, per spawner.
	// The set of placed bounds only changes when some spawner applies or clears, so it's maintained there
	// (see Apply/ClearPreview) and merely READ here.
	static readonly List<(PropSpawnerBase Owner, BBox Bounds)> s_allPlacedBounds = new();

	// Only ever checks against OTHER spawners (of either kind) that have already resolved and built their own
	// real props — order-dependent (a spawner deciding earlier in the same session can't know about one that
	// hasn't decided yet), which is a fine trade-off for a "declutter obvious overlaps" nicety, not a hard
	// guarantee.
	bool OverlapsAnotherSpawner( BBox candidate )
	{
		for ( int i = 0; i < s_allPlacedBounds.Count; i++ )
		{
			var entry = s_allPlacedBounds[i];
			if ( entry.Owner == this )
				continue;

			if ( candidate.Overlaps( entry.Bounds ) )
				return true;
		}

		return false;
	}

	// Drops this spawner's contribution to the shared list above — called whenever its placements are torn
	// down, so stale bounds can never keep blocking other spawners' rolls.
	void ClearGlobalPlacedBounds()
	{
		for ( int i = s_allPlacedBounds.Count - 1; i >= 0; i-- )
			if ( s_allPlacedBounds[i].Owner == this )
				s_allPlacedBounds.RemoveAt( i );
	}

		// Builds whatever ChosenIndices/ChosenYaws/ChosenOffsets currently hold. Only ever called on the host (or
	// the sole machine in a non-networked session) — see OnUpdate(). Each real placement is NetworkSpawn'd
	// (when actually networked) so the engine ships it to every client, including late joiners via the spawn
	// snapshot — see the class doc for why that's the mechanism instead of syncing the decision itself.
	void Apply()
	{
		foreach ( var go in _spawned )
			go?.Destroy();
		_spawned.Clear();
		_spawnedBounds.Clear();
		ClearGlobalPlacedBounds();
		_pendingNetworkSpawn.Clear();

		int count = Math.Max( 0, ChosenSlotCount );
		for ( int slot = 0; slot < count; slot++ )
		{
			if ( !ChosenIndices.TryGetValue( slot, out var index ) )
				continue;

			// "None", or the list shrank since this slot's index was picked (e.g. edited mid-session) —
			// leave this one slot empty rather than aborting every other slot too.
			if ( index < 0 || index >= (Prefabs?.Count ?? 0) )
				continue;

			var prefab = Prefabs[index];
			if ( !prefab.IsValid() )
				continue;

			ChosenYaws.TryGetValue( slot, out var yaw );
			ChosenOffsets.TryGetValue( slot, out var offset );

			var (go, bounds) = BuildAndAlign( prefab, offset, yaw );
			if ( !go.IsValid() )
				continue;

			// host-owned; ships to every client (and late-joiners) automatically. Can transiently fail (e.g.
			// Connection.Local.CanSpawnObjects not granted yet the instant a freshly-loaded map starts
			// ticking) — queued for a retry every frame until it actually goes out, rather than silently
			// staying host-only forever (see _pendingNetworkSpawn's doc, and RetryPendingNetworkSpawns()).
			if ( Networking.IsActive && !go.NetworkSpawn() )
				_pendingNetworkSpawn.Add( go );

			_spawned.Add( go );
			if ( bounds is { } b )
			{
				_spawnedBounds[go] = b;
				s_allPlacedBounds.Add( (this, b) );
			}
		}
	}

	// Clones a prefab at the given slot's spawn point (yawed per that slot's own roll, if RandomizeRotation
	// is on), ground-aligns it, and hands back its real (subtract-aware) world bounds alongside it. Only ever
	// called for picks that actually STICK — collision trials no longer clone anything at all, they measure
	// analytically from a per-prefab cached measurement instead (see GetCandidateBounds), which is what made
	// deciding cheap: a trial clone used to build every component live, including SdfRaymarchRenderer, which
	// allocates REAL GPU textures the moment it enables, just to throw the whole thing away a few lines
	// later. Across many candidates × many slots × many spawners that was real GPU memory allocated and
	// discarded far faster than a deferred Destroy()/GC pass could reclaim it, on top of a full
	// SurfaceNetsMesher rebuild per candidate for geometry that never changes between candidates.
	(GameObject, BBox?) BuildAndAlign( GameObject prefab, Vector3 localOffset, float yaw )
	{
		var point = SpawnPointFor( localOffset );
		var tx = new Transform( point, WorldRotation, WorldScale );
		if ( RandomizeRotation )
			tx = tx.WithRotation( tx.Rotation * Rotation.FromYaw( yaw ) );

		var go = prefab.Clone( new CloneConfig( tx, startEnabled: true,
			name: $"Random Prop ({prefab.Name})" ) );

		go.SetParent( GameObject, true );
		go.Flags |= GameObjectFlags.NotSaved; // never let a preview/runtime pick bake into the scene file

		return (go, AlignToGround( go, point ));
	}

	// Prefabs in a list like this can have all sorts of pivot conventions (centred, base, off to one side) —
	// spawning them all at this exact transform meant some floated and some clipped into the floor depending
	// on which one got picked. Trace straight down from the candidate's actual spawn point for the real floor,
	// then shift the WHOLE clone (not just move its pivot) so its real render bounds sit flush with that
	// surface regardless of pivot. Returns the FINAL (post-shift) world bounds, or null if there was nothing
	// measurable to align at all.
	BBox? AlignToGround( GameObject go, Vector3 point )
	{
		var bounds = ComputeWorldBounds( go );
		if ( bounds is not { } b )
			return null;

		var tr = Scene.Trace.Ray( point + Vector3.Up * 4f, point + Vector3.Down * 512f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		if ( !tr.Hit )
			return b; // no floor found — leave it wherever it was cloned, still measurable as-is

		var shift = Vector3.Up * (tr.EndPosition.z - b.Mins.z);
		go.WorldPosition += shift;
		return new BBox( b.Mins + shift, b.Maxs + shift );
	}

	// Real (subtract-aware) world-space bounds of everything renderable under `go` — used both to ground-align
	// a freshly-placed prop and to test overlap against every other already-placed one.
	BBox? ComputeWorldBounds( GameObject go )
	{
		Vector3 mn = default, mx = default;
		bool any = false;

		void Include( Vector3 p )
		{
			mn = any ? Vector3.Min( mn, p ) : p;
			mx = any ? Vector3.Max( mx, p ) : p;
			any = true;
		}

		foreach ( var renderer in go.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( !renderer.IsValid() || !renderer.SceneObject.IsValid() )
				continue;

			Include( renderer.SceneObject.Bounds.Mins );
			Include( renderer.SceneObject.Bounds.Maxs );
		}

		// SdfRaymarchRenderer keeps its SceneObject private (no public world-bounds accessor), so go via the
		// SdfSculpture's brushes instead — but NOT Sdf.TryGetBounds: that only unions ADD brushes (see its own
		// doc comment), so a Subtract that carves into the bottom (a notch, a hollowed underside, etc.) left
		// the additive-only box reaching lower than the surface actually visible once the subtraction applies.
		// That's exactly what floated props with any bottom-facing subtract brush — the alignment math shifted
		// them up to match a floor that was never really there. Meshing at a cheap resolution and reading the
		// REAL vertex positions accounts for every subtract exactly the way the rendered surface does.
		foreach ( var sculpture in go.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } )
				continue;

			var data = SurfaceNetsMesher.ComputeData( sculpture.Brushes, 16, sculpture.FlipFaces );
			if ( data.IsEmpty )
				continue;

			var tx = sculpture.WorldTransform;
			foreach ( var vertex in data.Vertices )
				Include( tx.PointToWorld( vertex.Position ) );
		}

		return any ? new BBox( mn, mx ) : null;
	}

	/// <summary>Editor convenience: re-rolls and applies fresh picks immediately, without needing to enter
	/// play mode — lets a level designer see what a spot might spawn (and cycle through a few rolls to check
	/// they all look right) right in the editor viewport. Re-rolls the SAME way <see cref="Decide"/> normally
	/// would (including NoneChance), it's just triggered manually instead of waiting for OnUpdate.
	/// In an actual multiplayer session this only ever does anything useful for the host — a client pressing
	/// it would just be clobbering its own local state, which OnUpdate will immediately stomp back to whatever
	/// the host's [Sync] state already says, so it's a no-op (logged) rather than a silent desync.</summary>
	[Button( "Preview Random Pick" )]
	public void PreviewPick()
	{
		if ( Networking.IsActive && !IsHostAuthority )
		{
			Log.Warning( $"{GetType().Name}.PreviewPick: only the host's pick actually sticks in a live session." );
			return;
		}

		ChosenSlotCount = Undecided;
		if ( Scene.IsEditor )
			s_prefabLocalBounds.Clear(); // a designer may have edited a prefab's geometry since it was
										  // measured — a manual re-roll is the natural place to re-measure
		Decide();
		Apply();
		_previewConfigHash = ComputeConfigHash();
		_previewAppliedPosition = WorldPosition;
		_previewAppliedRotation = WorldRotation;
		_previewSuppressed = false;
	}

	/// <summary>Editor convenience: removes whatever's currently previewed and resets back to undecided,
	/// WITHOUT rolling new picks. Same NotSaved reasoning as the clones themselves (see <see cref="Apply"/>)
	/// applies to <see cref="ChosenSlotCount"/> too — leaving it reset means saving the scene right after
	/// clearing can never accidentally bake in "whatever I was last previewing" as the real decision for every
	/// future session; a fresh game still rolls its own picks from scratch either way. Suppresses the live
	/// editor preview (see <see cref="UpdateEditorPreview"/>) so it doesn't just refill this on the very next
	/// tick.</summary>
	[Button( "Clear Preview" )]
	public void ClearPreview()
	{
		foreach ( var go in _spawned )
			go?.Destroy();
		_spawned.Clear();
		_spawnedBounds.Clear();
		ClearGlobalPlacedBounds();

		ChosenSlotCount = Undecided;
		_pendingNetworkSpawn.Clear();
		_previewSuppressed = true;
	}

	// The shared placed-bounds list outlives any single component (it's static), so a spawner that goes away
	// — scene change, deleted in the editor, etc. — has to take its own entries with it.
	protected override void OnDestroy()
	{
		base.OnDestroy();
		ClearGlobalPlacedBounds();
	}

	// [EditorHandle] (on the concrete subtype) supplies an always-visible, clickable billboard icon at this
	// GameObject's position — so a spawner with an empty (or not-yet-populated) Prefabs list is at least
	// findable/selectable without any of this. This just adds the EXTRA detail once you've already found it:
	// a marker per current slot's spawn point (colour-coded red if misconfigured) plus a text readout, gated
	// to hover/select instead of drawing for every placed spawner, since the handle icon already covers
	// "something is here". DrawRegionGizmo is the hook a volume spawner uses to also draw its own extents.
	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		DrawRegionGizmo();

		if ( !Gizmo.IsSelected && !Gizmo.IsHovered )
			return;

		Gizmo.Transform = new Transform( 0 );

		bool empty = Prefabs is not { Count: > 0 };
		Gizmo.Draw.Color = (empty ? Color.Red : Color.Cyan).WithAlpha( 0.9f );

		if ( ChosenSlotCount > 0 )
		{
			for ( int slot = 0; slot < ChosenSlotCount; slot++ )
			{
				if ( ChosenOffsets.TryGetValue( slot, out var offset ) )
					DrawSpawnMarker( SpawnPointFor( offset ) );
			}
		}
		else
		{
			DrawSpawnMarker( WorldPosition );
		}

		var label = empty ? $"{Label}\n(no prefabs set)" : $"{Label}\n({Prefabs.Count} option{(Prefabs.Count == 1 ? "" : "s")})";
		Gizmo.Draw.Text( label, new Transform( WorldPosition + Vector3.Up * 32f ), size: 14 );
	}

	static void DrawSpawnMarker( Vector3 point )
	{
		const float half = 16f;
		var extents = new Vector3( half, half, half );
		Gizmo.Draw.LineBBox( new BBox( point - extents, point + extents ) );
		Gizmo.Draw.LineSphere( new Sphere( point, 4f ) );
	}

	/// <summary>Short name used in the gizmo label — override per subtype.</summary>
	protected virtual string Label => "Random Prop";

	/// <summary>Hook for a subtype to draw its own extra editor-only gizmos (e.g. a volume's extents box).
	/// Runs unconditionally (not gated to hover/select) so the region itself is always findable — same
	/// reasoning as the EditorHandle icon.</summary>
	protected virtual void DrawRegionGizmo() { }
}
