using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Keeps the ACTIVE brush (the selection, or the stamp ghost) out of the map's solid geometry while it's
/// being edited — the brush gets its own collision against the world, separate from the pawn's physics, so
/// a wall stops the BRUSH without ever pushing the PROP (the pivot-pin hard rule: the shape never moves on
/// its own).
///
/// <b>Why.</b> Mid-drag the disguise's collider is stale by design (it only rebuilds on commit), so clay
/// could be waved straight through a wall — and the COMMIT then embedded the pawn's dynamic rigidbody in
/// the world, whose depenetration ejected the prop out the far side of thin walls and floors. That commit
/// shove was the clip-through exploit. Stop the clay ever entering the wall and there's nothing to shove.
///
/// <b>How.</b> Not a real physics collider (a kinematic body pushes through anything; a dynamic one would
/// fight the gizmo). Two cooperating mechanisms per geometric change, both running the brush's own
/// collision shapes (<see cref="SdfCollisionBuilder.BuildSweepShapes"/>) on a scratch <see cref="PhysicsBody"/>:
///
///  1. SWEEP (anti-tunnel): a continuous translation is swept from its last clear state via
///     <c>Scene.Trace.Sweep</c> and collide-and-slides — advance to the hit, deflect the remainder along
///     the surface — so a drag pressed into the floor glides instead of sticking. This is what stops a
///     fast flick (or a scroll-wheel depth step) crossing a thin wall in one frame.
///  2. RESOLVE (the guarantee): the resulting state is actively DEPENETRATED with the native minimum-
///     translation query (<see cref="PhysicsBody.ComputePenetration"/>) against every nearby world body —
///     whatever residual overlap any path produced, the brush is pushed back out. Capped at
///     <see cref="MaxResolve"/>: past that the nearest way out may be the WRONG side of a thin wall, so a
///     deep embed reverts to the last clear state instead of resolving.
///
/// Resolve is deliberately the load-bearing layer. The first version trusted zero-length sweeps to report
/// StartedSolid for pure overlap tests, and they don't reliably — a sweep that begins in contact can pass
/// straight through, which is exactly how the stamp ghost (the one path with no travel sweep) could be
/// pulled beneath the floor. ComputePenetration/CheckOverlap are true overlap queries with no start-state
/// sensitivity, so every ACCEPTED state is one they proved (or made) clear — penetration can no longer
/// ratchet in through trace tolerances, and blocked shape changes (scale into the floor) get pushed out
/// instead of sticking.
///
/// BUT the native pair only sees CONVEX shapes. Against a triangle-MESH body — every MeshComponent block,
/// every FBX-imported ModelCollider, i.e. most real map geometry — CheckOverlap and ComputePenetration both
/// return nothing at all (verified empirically with mimi_clamp_probe; this had the whole clamp silently dead
/// on any mesh-built map, the Kitchen first). Mesh bodies therefore take a parallel contact path built from
/// the two primitives the probe PROVED against meshes — <see cref="PhysicsBody.FindClosestPoint"/> and
/// zero-length SHAPE traces (reliable for start-solid, unlike the zero-length body sweeps above; the
/// engine's own CharacterController leans on them) — driven by point samples of the scratch geometry that
/// <see cref="SdfCollisionBuilder.BuildSweepShapes"/> emits alongside the shapes. See
/// <see cref="MeshSampleContacts"/>. Convex bodies keep the native path untouched: each query runs where
/// it's proven.
///
/// Additive brushes only, by construction — BuildSweepShapes yields nothing for carves/cutouts, which can
/// only remove your own clay and are harmless (and satisfying) to wave through walls.
///
/// One instance per <see cref="SculptEditSession"/>, fed from the session's preview/commit funnel — the one
/// choke every continuous edit path (gizmo drags, scrubs, stamp placement, HUD sliders, wheel push) runs
/// through. Discrete paths that bypass the funnel (undo/redo splices, an eye-toggle on an unselected row)
/// are covered by the commit-time backstop instead: <see cref="EmbeddedInWorld"/>, which
/// <see cref="SdfCollider.Rebuild"/> consults before swapping in a new collider.
///
/// Invariants:
///  • Owner-local only, like every validity gate — sessions only run on the editing machine, and the
///    backstop gates on !IsProxy. Proxies always build what they received.
///  • Never traps: a brush that STARTS blocked (loaded embedded, revealed by an eye-toggle) is resolved if
///    it can be, and moves freely until it first comes up clear otherwise.
///  • Contact is not penetration: resting contact (solver slop included) never triggers corrections or
///    refusals — solids via the <see cref="RestTolerance"/> deadband on full-size shapes, splines via the
///    graced tube inset — while what actually RESTS on a surface is the real geometry, so a commit never
///    builds an embedded collider (which physics would answer by lifting the whole prop on release).
/// </summary>
public sealed class BrushWorldClamp
{
	/// <summary>Base tolerance unit of the live clamp. Solids no longer test at this inset — their gestures
	/// run FULL-SIZE shapes with <see cref="RestTolerance"/> as the contact deadband, so what rests on the
	/// floor is the REAL surface and commits never build an embedded collider (the release pop). It
	/// survives as the base of <see cref="SplineTubeInset"/>'s curve-dip grace.</summary>
	public const float ClampInset = 1f;

	/// <summary>Extra inset the live clamp grants a SPLINE's tube spheres on top of <see cref="ClampInset"/>
	/// — the curve-dip allowance. A control point resting on the floor consumes its whole own-sphere inset
	/// (its real sphere sits ~ClampInset deep), so at equal insets the interpolated spheres between two
	/// resting points had ZERO room below the endpoint line — and the Catmull-Rom tangent after a descent
	/// always bows the next span down a little, which made drawing along the floor impossibly strict. The
	/// grace lets the curve bow this much below the resting line; deeper dips still deflect/lift/restrict.
	/// Applied to EVERY clamp-side spline tube test (gestures, verify, uniform moves) — a graced-in state
	/// must never be vetoed or whole-shifted by a stricter re-test later. Sized to stay under
	/// <see cref="BackstopInset"/>, so the commit backstop still accepts everything the clamp allows.</summary>
	const float SplineTubeInset = ClampInset + 2.5f;

	/// <summary>Shape inset for the commit-time backstop — more tolerant than the live clamp (the spline
	/// tube's graced inset included) so the two can never disagree the wrong way round (everything the
	/// clamp allows, the backstop accepts) and so contact noise from a settling body can't freeze collider
	/// rebuilds.</summary>
	public const float BackstopInset = 4f;

	// The live clamp's inset for a whole-brush test: splines get the tube grace; solids test FULL SIZE.
	// Full size matters for where gestures come to REST: shapes swept/resolved at an inset rest with the
	// REAL primitive that far inside the floor, and the commit then builds the real collider embedded —
	// which physics answers by lifting the whole prop on release (the release pop). Full-size gestures
	// rest a skin's breadth ABOVE the surface instead; RestTolerance below keeps resting contact from
	// being endlessly re-corrected.
	static float InsetFor( SdfBrush b ) => b.Shape == SdfShape.Spline ? SplineTubeInset : 0f;

	/// <summary>Penetration depth (of FULL-SIZE shapes) the resolve treats as resting contact, not error —
	/// no correction below it. Without this, solver slop on a settled body (≤~0.5u of contact penetration
	/// is normal) would re-trigger a lift on every later edit of a floor-resting brush, ratcheting it
	/// upward in local space while ground-snap lowered the body to match. Also the worst embed a commit
	/// can now carry: within solver slop, so releases no longer pop the prop.</summary>
	const float RestTolerance = 0.5f;

	/// <summary>The most world-space correction the endpoint resolve may apply in one frame. Small on
	/// purpose: the minimum-translation direction is only trustworthy while penetration is shallow — deeper
	/// than this and "nearest way out" could be the far side of a thin wall, which is the exploit this whole
	/// system exists to close. Anything needing more reverts to the last clear state instead.</summary>
	const float MaxResolve = 8f;

	/// <summary>Resolve passes per frame — floor + wall corners need a second pass; a third catches the
	/// change the second introduced. Still overlapping after these = unresolvable, revert.</summary>
	const int ResolveIterations = 3;

	/// <summary>Longest translation (sculpture units) the sweep phase treats as TRAVEL. Beyond it the move
	/// is a teleport — the stamp cursor jumping across the scene — where sweeping the path would wrongly
	/// block hops over unrelated geometry; the endpoint resolve still guarantees where it LANDS is clear.
	/// Continuous gestures (drags, scrubs, scroll depth steps) sit far below this.</summary>
	const float SweepMaxTravel = 128f;

	// Rest gap kept off a surface after a slide or a resolve, on top of ClampInset, so the next frame
	// starts genuinely clear instead of oscillating between "touching" and "corrected".
	const float SlideSkin = 0.1f;

	/// <summary>Shape inset for a RETRY of a travel sweep whose full-size start read solid. Resting states
	/// legitimately carry up to <see cref="RestTolerance"/> of real contact (the deadband accepts it — the
	/// release-pop fix), and a full-size sweep from such a state is start-solid, killing the slide — on
	/// convex worlds the documented unreliability of start-solid sweeps hid this (they false-negative, so
	/// the sweep ran anyway), but MESH worlds report start-solid reliably, which turned every screen-plane
	/// drag along a mesh floor into resolve-vs-revert: the gizmo square "sticking" instead of gliding.
	/// Inset past the deadband, an accepted resting state can never read solid, so the slide survives; the
	/// endpoint resolve (full-size + deadband) still owns where things finally rest, so nothing sinks and
	/// commits still can't pop.</summary>
	const float SweepStartInset = RestTolerance + 2f * SlideSkin;

	// Primary hit + one deflection. A corner's second plane just stops the leftover motion — iterating
	// further buys nothing a next frame's drag doesn't.
	const int SlideIterations = 2;

	// Where scratch bodies physically live. Sweeps and overlap queries place their shapes virtually at the
	// supplied transforms, so the resident shapes just need to be somewhere no gameplay trace, contact or
	// query will ever find them.
	static readonly Vector3 ParkingSpot = new( 0f, 0f, -200000f );

	SdfBrush _watched;    // the brush the snapshot belongs to — identity, not index (undo splices new instances)
	SdfBrush _lastClear;  // Copy() of the last state that tested clear; null = none yet / started blocked
	int _lastHash;        // geometry hash of the watched brush at the last verdict, so idle frames cost nothing
	PhysicsBody _scratch;
	PhysicsWorld _scratchWorld;

	// Point-sample mirror of whatever geometry is currently baked into _scratch (sculpture-local; w = sphere
	// radius, hull corners/centroids at 0) — the contact representation for MESH world bodies, which the
	// native overlap/MTV queries can't see. Filled by the same BuildSweepShapes call that fills _scratch, so
	// the two can never drift apart.
	readonly List<Vector4> _samples = new();

	/// <summary>Run the clamp for this frame's state of the active brush. Call AFTER the tools have mutated
	/// it and BEFORE the surface rebuild, so the corrected state is what gets rendered, recorded and
	/// streamed.</summary>
	public void Apply( SdfSculpture target, SdfBrush brush )
	{
		if ( !target.IsValid() || brush is null )
		{
			_watched = null;
			_lastClear = null;
			return;
		}

		// Only clamp shapes that will actually become SOLID physics. The hunter's face (trigger collider,
		// bullet detection only) and the menu head (no collider at all) sculpt free — their clay can't shove
		// anything, so pressing it into scenery is cosmetic and allowed.
		if ( !ClampsApply( target ) )
		{
			_watched = null;
			_lastClear = null;
			return;
		}

		if ( !ReferenceEquals( brush, _watched ) )
		{
			_watched = brush;
			_lastClear = null;
			_lastHash = 0;
		}

		// A disabled or subtractive brush is exempt — and DROPS its snapshot, so a Subtract→Add conversion
		// (or an eye re-enable) re-baselines instead of teleporting the brush back to wherever it sat when
		// the stale snapshot was taken.
		if ( !brush.Enabled || brush.Operation != SdfOperation.Add )
		{
			_lastClear = null;
			_lastHash = 0;
			return;
		}

		int hash = GeometryHash( brush );
		if ( hash == _lastHash )
			return; // nothing changed since the last verdict

		var scene = target.Scene;
		if ( !scene.IsValid() )
		{
			_lastHash = hash;
			return;
		}

		EnsureScratch( scene.PhysicsWorld ); // both phases bake shapes onto it

		// 1) Anti-tunnel: a continuous translation sweeps from the last clear state and deflects along
		//    whatever it hits, so travel can never cross a wall and drags glide along surfaces. A
		//    single-spline-point gesture comes back fully constrained against its own curve (pointGesture)
		//    — for those the endpoint may only be VERIFIED, never shifted: no gesture on one point is ever
		//    allowed to move the others.
		var prev = _lastClear;
		bool pointGesture = prev is not null && SweepPhase( scene, target, brush, prev );

		// 2) The guarantee: actively depenetrate whatever state the tools (and the slide) produced. This is
		//    the layer with no start-state sensitivity — an accepted state is always a proved-clear state.
		if ( ResolveEndpoint( scene, target, brush, allowShift: !pointGesture ) )
		{
			_lastClear = brush.Copy();
			_lastHash = GeometryHash( brush );
			return;
		}

		// Unresolvable (deep embed — the nearest way out may be the wrong side of a thin wall).
		if ( prev is null )
		{
			_lastHash = GeometryHash( brush );
			return; // started blocked and can't be fixed — free until it first comes up clear (never trap)
		}

		RestorePose( brush, prev );
		_lastHash = GeometryHash( brush );
	}

	// ── Group clamp (multi-selection) ────────────────────────────────────────────────────────────────

	/// <summary>The multi-selection form of <see cref="Apply"/>: clamp a whole GROUP of brushes as ONE
	/// rigid body. A group gesture has to keep its formation — correcting members individually would shear
	/// the arrangement apart — so the resolve pools the solid members' penetration into ONE shared
	/// world-space correction applied to EVERY member (carves and disabled brushes included), and a frame
	/// it can't resolve reverts the WHOLE group to its last clear pose.
	///
	/// Not optional: without it a group drag pushes clay through the floor, and the commit backstop then
	/// refuses to swap in the embedded collider (see <see cref="EmbeddedInWorld"/>) — so the prop's physics
	/// silently freezes on its last good shape while the visible clay keeps moving. Pass a null/empty list
	/// to drop the watch (the group path's equivalent of <c>Apply(target, null)</c>).</summary>
	public void ApplyGroup( SdfSculpture target, IReadOnlyList<SdfBrush> brushes )
	{
		if ( !target.IsValid() || brushes is not { Count: > 0 } || !ClampsApply( target ) )
		{
			ClearGroupWatch();
			return;
		}

		// Only members that become SOLID physics generate contacts (same exemption the single-brush path
		// makes, and the same reason: they shove nothing) — but every member MOVES with the group. The
		// shared correction and the revert must cover disabled/carve members too, or a wall contact would
		// shift the additive clay while the carves stayed put, shearing the arrangement the group clamp
		// exists to keep rigid (the scale-next-to-a-wall misalignment bug).
		_groupAll.Clear();
		_groupAll.AddRange( brushes );
		_groupSolid.Clear();
		foreach ( var b in brushes )
		{
			if ( b is { Enabled: true, Operation: SdfOperation.Add } )
				_groupSolid.Add( b );
		}

		if ( _groupSolid.Count == 0 )
		{
			ClearGroupWatch();
			return; // nothing in the group can embed — carves are free to wave through walls, as ever
		}

		// Re-baseline whenever the MEMBERSHIP changes — identity-keyed, like the single-brush watch, so an
		// undo splicing in fresh instances (or a selection change) can't restore a stale pose.
		if ( !SameMembers( _groupAll, _groupWatched ) )
		{
			_groupWatched.Clear();
			_groupWatched.AddRange( _groupAll );
			_groupClear.Clear();
			_groupHash = 0;
		}

		int hash = GroupHash( _groupAll );
		if ( hash == _groupHash )
			return; // nothing changed since the last verdict — idle frames cost nothing

		var scene = target.Scene;
		if ( !scene.IsValid() )
		{
			_groupHash = hash;
			return;
		}

		EnsureScratch( scene.PhysicsWorld );

		// No sweep phase here (unlike Apply): the members of a group gesture each travel the same short
		// per-frame step, so the endpoint resolve below — which has no start-state sensitivity — is the
		// whole guarantee. Tunnelling would need one frame's step to exceed the thinnest wall.
		if ( ResolveGroup( scene, target ) )
		{
			SnapshotGroup();
			_groupHash = GroupHash( _groupAll );
			return;
		}

		// Unresolvable. Revert the whole group together (never a partial revert — that IS shearing).
		if ( _groupClear.Count != _groupAll.Count )
		{
			_groupHash = GroupHash( _groupAll );
			return; // started blocked with nothing clear to fall back to — free until it comes up clear
		}

		for ( int i = 0; i < _groupAll.Count; i++ )
			RestorePose( _groupAll[i], _groupClear[i] );
		_groupHash = GroupHash( _groupAll );
	}

	// The group's shared depenetration. Same contract as ResolveEndpoint (true = proved clear, possibly
	// after a correction; false = needed more than MaxResolve, so the caller reverts) — the difference is
	// that every solid member's correction accumulates into ONE offset applied to EVERY member (carves and
	// disabled included), so the arrangement translates rigidly instead of each brush finding its own way
	// out — or the contact-exempt members getting left behind.
	bool ResolveGroup( Scene scene, SdfSculpture target )
	{
		var tx = target.WorldTransform;
		var offset = Vector3.Zero; // accumulated world-space correction, shared by the whole group

		for ( int iter = 0; iter < ResolveIterations; iter++ )
		{
			var at = new Transform( tx.Position + offset, tx.Rotation, tx.Scale );

			bool any = false;
			var correction = Vector3.Zero;
			foreach ( var b in _groupSolid )
			{
				if ( !SdfCollisionBuilder.BuildSweepShapes( b, _scratch, InsetFor( b ), _samples ) )
					continue; // no collision shapes — nothing that can embed

				// Per-shape deadband, exactly as ResolveEndpoint splits it: solids run full-size shapes so
				// resting contact reads as a tiny penetration (RestTolerance absorbs it); splines run their
				// already-graced shapes, so theirs must stay zero.
				float deadband = b.Shape == SdfShape.Spline ? 0f : RestTolerance;
				var query = QueryBounds( _scratch, tx, offset );

				bool sawMesh = false;
				foreach ( var body in WorldBodies( scene, target, query, _scratch ) )
				{
					ShapeKinds( body, out bool convex, out bool mesh );
					sawMesh |= mesh;

					// Combine by DEFICIT, never by blind sum: several members resting on the SAME floor each
					// report a near-parallel penetration, and summing them lifts the group by the TOTAL — an
					// overshoot the next pass then accepts, leaving the group hovering above the surface.
					// Extend the pooled correction only by what this contact still needs beyond what it
					// already provides along its own direction: parallel duplicates add ~nothing, a corner's
					// orthogonal contact still gets its full push, and genuinely opposed contacts (squeezed
					// between floor and ceiling) still grow past MaxResolve into the revert. (The mesh path
					// merges its samples by the same deficit rule, into the same pooled correction.)
					if ( convex && body.ComputePenetration( _scratch, at, out var dir, out var dist )
						&& dist > deadband )
					{
						any = true;
						var outDir = -dir; // dir·dist moves the WORLD body clear — the brush moves the opposite way
						Deficit( ref correction, outDir, dist + SlideSkin );
					}

					if ( mesh && MeshSampleContacts( scene, target, body, tx, offset, _samples, deadband, ref correction ) )
						any = true;
				}

				if ( sawMesh && StraddleContacts( scene, target, tx, offset, _samples, deadband, ref correction ) )
					any = true;
			}

			if ( !any )
			{
				if ( !offset.IsNearZeroLength )
				{
					foreach ( var b in _groupAll )
						ApplyWorldOffset( b, tx, offset );
				}
				return true;
			}

			offset += correction;
			if ( offset.Length > MaxResolve )
				return false;
		}

		return false; // still overlapping after the passes — unresolvable this frame
	}

	// Group watch state — the multi-selection twin of _watched / _lastClear / _lastHash. _groupClear runs
	// index-parallel to _groupAll, which the membership re-baseline above keeps honest.
	readonly List<SdfBrush> _groupAll = new();     // this frame's full membership — everything that MOVES
	readonly List<SdfBrush> _groupSolid = new();   // the subset that generates contacts (enabled Adds)
	readonly List<SdfBrush> _groupWatched = new(); // the membership the snapshot belongs to (identity)
	readonly List<SdfBrush> _groupClear = new();   // Copy() of the last pose that tested clear, per member
	int _groupHash;

	void ClearGroupWatch()
	{
		_groupWatched.Clear();
		_groupClear.Clear();
		_groupHash = 0;
	}

	void SnapshotGroup()
	{
		_groupClear.Clear();
		foreach ( var b in _groupAll )
			_groupClear.Add( b.Copy() );
	}

	static bool SameMembers( List<SdfBrush> a, List<SdfBrush> b )
	{
		if ( a.Count != b.Count )
			return false;
		for ( int i = 0; i < a.Count; i++ )
		{
			if ( !ReferenceEquals( a[i], b[i] ) )
				return false;
		}
		return true;
	}

	static int GroupHash( List<SdfBrush> brushes )
	{
		int h = unchecked( (int)2166136261 );
		foreach ( var b in brushes )
			b.HashInto( ref h );
		return h;
	}

	/// <summary>Drop the watch state and the scratch physics body. Call on session teardown.</summary>
	public void Dispose()
	{
		if ( _scratch is not null && _scratch.IsValid() )
			_scratch.Remove();
		_scratch = null;
		_scratchWorld = null;
		_watched = null;
		_lastClear = null;
		_lastHash = 0;
		_samples.Clear();
		ClearGroupWatch();
		_groupAll.Clear();
		_groupSolid.Clear();
	}

	// ── Backstop ─────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Commit-time backstop: is any of this sculpture's clay embedded in the world's solid geometry
	/// (deeper than <see cref="BackstopInset"/>)? <see cref="SdfCollider.Rebuild"/> consults this before
	/// swapping in a new collider — a collider built embedded is what the physics solver depenetrates by
	/// shoving the whole prop, potentially through the wall. Covers every path the live clamp can't see:
	/// undo/redo, layer-row toggles on unselected brushes, conversions, loads. Convex world bodies use the
	/// native overlap query (<see cref="PhysicsBody.CheckOverlap"/>) — never a zero-length sweep, which can
	/// miss a body it starts inside of; mesh bodies, which that query is blind to, use the sample path
	/// (<see cref="MeshSampleContacts"/> + <see cref="StraddleContacts"/>) on the same inset geometry.</summary>
	public static bool EmbeddedInWorld( SdfSculpture sculpture )
	{
		if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } brushes )
			return false;

		var scene = sculpture.Scene;
		if ( !scene.IsValid() || scene.PhysicsWorld is not { } world )
			return false;

		var body = new PhysicsBody( world ) { BodyType = PhysicsBodyType.Static, Position = ParkingSpot };
		var samples = new List<Vector4>( 64 );
		try
		{
			var tx = sculpture.WorldTransform;
			var discard = Vector3.Zero;
			foreach ( var b in brushes )
			{
				if ( !SdfCollisionBuilder.BuildSweepShapes( b, body, BackstopInset, samples ) )
					continue;

				var query = QueryBounds( body, tx, Vector3.Zero );
				bool sawMesh = false;
				foreach ( var wb in WorldBodies( scene, sculpture, query, body ) )
				{
					ShapeKinds( wb, out bool convex, out bool mesh );
					sawMesh |= mesh;
					if ( convex && wb.CheckOverlap( body, tx ) )
						return true;
					// Zero deadband: the BackstopInset already provides all the tolerance this test means.
					if ( mesh && MeshSampleContacts( scene, sculpture, wb, tx, Vector3.Zero, samples, 0f, ref discard ) )
						return true;
				}

				if ( sawMesh && StraddleContacts( scene, sculpture, tx, Vector3.Zero, samples, 0f, ref discard ) )
					return true;
			}
			return false;
		}
		finally
		{
			body.Remove();
		}
	}

	// ── Sweep phase (anti-tunnel + slide) ────────────────────────────────────────────────────────────

	static Vector3 P( Vector4 v ) => new( v.x, v.y, v.z );

	// Classify this frame's change; pure TRANSLATIONS under the travel cap get swept from the last clear
	// state and deflected along whatever they hit. Mutates the brush to the furthest legal spot — the
	// resolve phase then proves (or corrects) the result. Shape changes and teleports pass through
	// untouched: resolve alone handles them. Returns TRUE only for a single-spline-point gesture, which
	// comes back fully constrained against its own curve — the caller must then verify the endpoint
	// WITHOUT shifting anything (no gesture on one point may ever move the others).
	bool SweepPhase( Scene scene, SdfSculpture target, SdfBrush brush, SdfBrush prev )
	{
		if ( brush.Shape != SdfShape.Spline )
		{
			// Rigid move of a solid: everything but Position must match the last clear state.
			if ( !SameShapeIgnoringPosition( brush, prev ) )
				return false;

			var delta = brush.Position - prev.Position;
			if ( delta.IsNearZeroLength || delta.Length > SweepMaxTravel )
				return false;

			// FULL-SIZE sweep first, so the drag rests the real surface a skin above the floor — an inset
			// here let the real shape sink by the inset, and the commit's real collider then popped the
			// prop. A start-solid verdict (resting contact within the deadband — routine, not an error)
			// retries at SweepStartInset rather than abandoning the slide: without the retry, the raw
			// screen-plane write (which can sit far below the floor) goes to resolve-only and the frame
			// REVERTS, eating the drag's lateral motion — the square-handle "stuck to the floor" bug.
			if ( !SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, 0f ) )
				return false;
			var slid = SlideAnchor( scene, target, prev.Position, brush.Position );
			if ( slid is null && SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, SweepStartInset ) )
				slid = SlideAnchor( scene, target, prev.Position, brush.Position );
			if ( slid is { } pos )
				brush.Position = pos;
			return false;
		}

		// Splines: geometry lives in Points. Two sweepable cases — the WHOLE curve moved by one delta (the
		// wheel push), or exactly ONE control point moved (a gizmo dot drag). Radii/curvature/loop/mirror
		// changes are structural — resolve-only.
		if ( prev.Points is not { Count: > 0 } pp || brush.Points is not { } cp || cp.Count != pp.Count
			|| brush.Curvature != prev.Curvature || brush.SplineClosed != prev.SplineClosed
			|| brush.MirrorX != prev.MirrorX || brush.MirrorY != prev.MirrorY || brush.MirrorZ != prev.MirrorZ )
			return false;

		int moved = -1;        // the one point whose position and/or radius changed (-2 = several)
		bool anyRadius = false;
		bool uniform = true;   // all xyz deltas equal — the rigid whole-curve move (invalid with radius edits)
		var delta0 = P( cp[0] ) - P( pp[0] );
		for ( int i = 0; i < cp.Count; i++ )
		{
			bool wDiff = MathF.Abs( cp[i].w - pp[i].w ) > 1e-4f;
			anyRadius |= wDiff;

			var di = P( cp[i] ) - P( pp[i] );
			if ( (di - delta0).Length > 1e-3f )
				uniform = false;
			if ( wDiff || !di.IsNearZeroLength )
				moved = moved == -1 ? i : -2; // -2 = several points changed
		}

		if ( uniform && !anyRadius && !delta0.IsNearZeroLength && delta0.Length <= SweepMaxTravel )
		{
			// Whole-curve move: rigid, so the full-shape sweep applies. Anchor on point 0. FULL SIZE first
			// (rests the real tube on real contact — no release pop); a tube whose curve already dips
			// within the grace starts solid at full size, so retry with the graced shapes it was accepted
			// under rather than losing the slide entirely.
			Vector3? slid = null;
			if ( SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, 0f ) )
				slid = SlideAnchor( scene, target, P( pp[0] ), P( cp[0] ) );
			if ( slid is null && SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, SplineTubeInset ) )
				slid = SlideAnchor( scene, target, P( pp[0] ), P( cp[0] ) );
			if ( slid is not { } anchor )
				return false;

			var net = anchor - P( pp[0] );
			for ( int i = 0; i < cp.Count; i++ )
			{
				var q = P( pp[i] ) + net;
				cp[i] = new Vector4( q.x, q.y, q.z, pp[i].w );
			}
			return false;
		}

		if ( moved < 0 )
			return false; // nothing / several points changed — not a sweepable gesture (resolve handles it)

		// One control point changed — moved, resized, or both. Handle it as a fully-constrained POINT
		// gesture: first the point's own sphere slides along surfaces and lifts out of shallow overlap,
		// then the gesture slides against the WHOLE tube (deflect → lift → restrict, see
		// SlidePointInTube) — the curve is part of the moving shape, and only the edited point ever moves.
		var from3 = P( pp[moved] );
		var to3 = P( cp[moved] );
		if ( (to3 - from3).Length > SweepMaxTravel )
			return false;

		var fixed3 = SlidePointSphere( scene, target, from3, to3, cp[moved].w );
		cp[moved] = new Vector4( fixed3.x, fixed3.y, fixed3.z, cp[moved].w );

		SlidePointInTube( scene, target, brush, moved, from3, pp[moved].w );
		return true;
	}

	/// <summary>Tube-aware clamp for ONE spline point from outside the Apply flow — the stamp tool's chain
	/// placement calls this on the live point after rebuilding the ghost list, sliding the point's move
	/// from <paramref name="fromPos"/> against the whole curve so the value a click commits into its
	/// private chain is legal INCLUDING the curve's dip (see <see cref="SlidePointInTube"/>).</summary>
	internal void ClampTubePoint( SdfSculpture target, SdfBrush brush, int idx, Vector3 fromPos )
	{
		if ( !target.IsValid() || !ClampsApply( target ) )
			return;
		if ( brush?.Points is not { } pts || idx < 0 || idx >= pts.Count )
			return;

		var scene = target.Scene;
		if ( !scene.IsValid() )
			return;

		EnsureScratch( scene.PhysicsWorld );
		SlidePointInTube( scene, target, brush, idx, fromPos, pts[idx].w );
	}

	// Bisection resolution of the last-resort restriction: 5 halvings lands within ~3% of the exact
	// stopping fraction — per-frame gesture deltas are small, so that's sub-pixel.
	const int TubeBisectSteps = 5;

	// SLIDE one point's gesture against the WHOLE tube. The curve between control points (Catmull-Rom)
	// can contact the world while every control point is clear, and no correction may touch the other
	// points — so the gesture itself adapts, in order of preference:
	//   (A) DEFLECT: drop the motion component driving the tube into the contact (the drag glides along
	//       the surface instead of sticking on the curve's graze);
	//   (B) LIFT: move the edited point out along the tube's minimum-translation vector — a radius grown
	//       against the floor raises ITS point, and a horizontal drag over uneven contact rides up over
	//       it; converges geometrically (the curve moves by less than the point, Catmull-Rom basis < 1);
	//   (C) RESTRICT: bisect toward last frame's accepted state (t=0 — always a valid bracket).
	// Every state this lands on has itself been tube-tested, and the caller verifies WITHOUT shifting, so
	// the other points are immovable by construction.
	void SlidePointInTube( Scene scene, SdfSculpture target, SdfBrush brush, int idx, Vector3 fromPos, float fromW )
	{
		var pts = brush.Points;
		var cand = pts[idx];
		var candPos = new Vector3( cand.x, cand.y, cand.z );
		float candW = cand.w;

		// Set the point and test the full tube. On failure _scratch keeps the failed state's shapes, so a
		// follow-up MTV query reads exactly THAT contact.
		bool TestAt( Vector3 p, float w )
		{
			pts[idx] = new Vector4( p.x, p.y, p.z, w );
			return !SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, SplineTubeInset, _samples )
				|| ShapesClear( scene, target ); // no collision shapes at all = nothing can clip
		}

		if ( TestAt( candPos, candW ) )
			return; // the candidate's full tube is clear — the gesture goes through whole

		// (A) Deflect the positional part along the contact plane. Two planes — a corner's second face
		// just stops what's left.
		var pos = candPos;
		var delta = candPos - fromPos;
		for ( int i = 0; i < 2 && !delta.IsNearZeroLength; i++ )
		{
			var n = TubeContactDirection( scene, target );
			if ( n.IsNearZeroLength )
				break;
			n = n.Normal;

			var slid = delta - n * Vector3.Dot( delta, n );
			if ( (slid - delta).Length < 1e-3f )
				break; // the contact isn't opposing this motion — deflecting again changes nothing

			delta = slid;
			pos = fromPos + delta;
			if ( TestAt( pos, candW ) )
				return;
		}

		// (B) Lift the edited point out along the tube's MTV.
		for ( int i = 0; i < ResolveIterations; i++ )
		{
			var correction = TubeContactDirection( scene, target );
			if ( correction.IsNearZeroLength || correction.Length > MaxResolve )
				break; // clear (caught below) or too deep to trust the direction

			var tx = target.WorldTransform;
			pos += tx.PointToLocal( tx.Position + correction ); // world vec → sculpture-local vec
			if ( TestAt( pos, candW ) )
				return;
		}

		// (C) Restrict: largest fraction of (from → best attempt), radius included, that tests clear.
		float lo = 0f, hi = 1f; // lo = last frame's accepted state (proven clear), hi = blocked
		bool ClearAt( float t ) => TestAt( Vector3.Lerp( fromPos, pos, t ), MathX.Lerp( fromW, candW, t ) );
		for ( int i = 0; i < TubeBisectSteps; i++ )
		{
			float mid = (lo + hi) * 0.5f;
			if ( ClearAt( mid ) )
				lo = mid;
			else
				hi = mid;
		}
		ClearAt( lo ); // land on the biggest fraction that tested clear
	}

	// The tube's accumulated minimum-translation vector OUT of the world, at the state currently baked
	// into _scratch (world space; zero = no contact). Skin included so a resolved state isn't
	// kiss-touching the surface it just cleared.
	Vector3 TubeContactDirection( Scene scene, SdfSculpture target )
	{
		var tx = target.WorldTransform;
		var query = QueryBounds( _scratch, tx, Vector3.Zero );
		var correction = Vector3.Zero;
		foreach ( var body in WorldBodies( scene, target, query, _scratch ) )
		{
			ShapeKinds( body, out bool convex, out bool mesh );
			if ( convex && body.ComputePenetration( _scratch, tx, out var dir, out var dist ) )
				correction -= dir * (dist + SlideSkin);
			if ( mesh )
				MeshSampleContacts( scene, target, body, tx, Vector3.Zero, _samples, 0f, ref correction );
		}
		return correction;
	}

	// Pure overlap check of the scratch shapes at the sculpture's transform — no correction applied.
	// _samples always holds the sample mirror of whatever's baked into _scratch (every build site fills
	// both), so the mesh side tests the same geometry the native side does.
	bool ShapesClear( Scene scene, SdfSculpture target )
	{
		var tx = target.WorldTransform;
		var query = QueryBounds( _scratch, tx, Vector3.Zero );
		var discard = Vector3.Zero;
		foreach ( var body in WorldBodies( scene, target, query, _scratch ) )
		{
			ShapeKinds( body, out bool convex, out bool mesh );
			if ( convex && body.CheckOverlap( _scratch, tx ) )
				return false;
			if ( mesh && MeshSampleContacts( scene, target, body, tx, Vector3.Zero, _samples, 0f, ref discard ) )
				return false;
		}
		return true;
	}

	/// <summary>Slide-and-resolve ONE sphere (a spline control point) against the world, in sculpture-local
	/// space: sweep <paramref name="from"/>→<paramref name="to"/> deflecting along surfaces, then
	/// depenetrate the endpoint with the native MTV query (so a sphere GROWN into the floor lifts out of
	/// it). The shared primitive behind the clamp's single-point gestures and the stamp tool's chain
	/// placement — the chain list must never hold an illegal point, because a chain click copies the live
	/// point BEFORE the session clamp can correct that frame's mutation. Returns the furthest
	/// provably-legal position, falling back to <paramref name="from"/> when the endpoint can't be made
	/// clear; a no-op (returns <paramref name="to"/>) when the target doesn't build solid physics.</summary>
	public static Vector3 SlidePointSphere( Scene scene, SdfSculpture target, Vector3 from, Vector3 to, float radius )
	{
		if ( !scene.IsValid() || !ClampsApply( target ) )
			return to;

		// FULL radius: the sweep/resolve place the point where its REAL sphere rests a skin above the
		// surface — an inset here let the real sphere sink by the inset, popping the prop at commit.
		float r = MathF.Max( radius, 0.5f );
		var tx = target.WorldTransform;

		var pos = from;
		var remaining = to - from;
		float sweepR = r;
		bool retried = false;
		for ( int i = 0; i < SlideIterations && !remaining.IsNearZeroLength; i++ )
		{
			var tr = Filtered( scene, target )
				.Sphere( sweepR, tx.PointToWorld( pos ), tx.PointToWorld( pos + remaining ) )
				.Run();
			if ( tr.StartedSolid )
			{
				// Resting contact within the deadband reads start-solid (reliably so on mesh worlds) —
				// retry the slide at a radius inset past it (see SweepStartInset) rather than abandoning
				// the glide; the full-size resolve below still owns where the point finally rests.
				if ( retried || r - SweepStartInset < 0.5f )
					break; // genuinely blocked — the resolve below is the authority
				retried = true;
				sweepR = r - SweepStartInset;
				i--;
				continue;
			}
			if ( !tr.Hit )
			{
				pos += remaining;
				break;
			}
			Deflect( tx, ref pos, ref remaining, tr.Fraction, tr.Normal );
		}

		return ResolveSphere( scene, target, pos, r ) ?? from;
	}

	// Depenetrate a single sculpture-local sphere with the native MTV query. Null = can't be made clear
	// within MaxResolve (caller falls back). The probe body is only created once something's bounds
	// actually overlap the query — a point in open air (most frames) never touches physics at all.
	static Vector3? ResolveSphere( Scene scene, SdfSculpture target, Vector3 localPos, float r )
	{
		var tx = target.WorldTransform;
		if ( scene.PhysicsWorld is not { } world )
			return localPos;

		PhysicsBody probe = null;
		var candidates = new List<PhysicsBody>();
		var meshSample = new List<Vector4>( 1 ) { new( localPos.x, localPos.y, localPos.z, r ) };
		try
		{
			var offset = Vector3.Zero;
			for ( int i = 0; i < ResolveIterations; i++ )
			{
				var centre = tx.PointToWorld( localPos ) + offset;
				var query = new BBox( centre - (r + 16f), centre + (r + 16f) );

				// Materialize the candidates BEFORE creating the probe: constructing a PhysicsBody
				// registers it into the world's body set — the very collection WorldBodies enumerates —
				// and mutating it mid-enumeration throws.
				candidates.Clear();
				foreach ( var body in WorldBodies( scene, target, query, null ) )
					candidates.Add( body );

				if ( candidates.Count == 0 )
					return localPos + tx.PointToLocal( tx.Position + offset );

				if ( probe is null )
				{
					probe = new PhysicsBody( world ) { BodyType = PhysicsBodyType.Static, Position = ParkingSpot };
					probe.AddSphereShape( Vector3.Zero, r, rebuildMass: false );
				}

				bool any = false;
				var correction = Vector3.Zero;
				foreach ( var body in candidates )
				{
					ShapeKinds( body, out bool convex, out bool mesh );

					// Resting contact of the full-size sphere (≤ RestTolerance) is not an error — leave it be.
					if ( convex && body.ComputePenetration( probe, new Transform( centre ), out var dir, out var dist )
						&& dist > RestTolerance )
					{
						any = true;
						correction -= dir * (dist + SlideSkin);
					}

					if ( mesh && MeshSampleContacts( scene, target, body, tx, offset, meshSample, RestTolerance, ref correction ) )
						any = true;
				}

				if ( !any )
					return localPos + tx.PointToLocal( tx.Position + offset );

				offset += correction;
				if ( offset.Length > MaxResolve )
					return null;
			}
			return null;
		}
		finally
		{
			if ( probe is not null && probe.IsValid() )
				probe.Remove();
		}
	}

	// The clamp only exists for shapes that become SOLID physics — see Apply's gate.
	static bool ClampsApply( SdfSculpture target )
	{
		if ( !target.IsValid() )
			return false;

		var collider = target.GameObject.Components.Get<SdfCollider>();
		return collider.IsValid() && collider.Active && !collider.BuildAsTrigger;
	}

	// Sweep the scratch shapes (baked at the CANDIDATE state, anchored on `candAnchor`) from the last clear
	// anchor toward the candidate, deflecting along whatever they hit. Returns the furthest legal anchor,
	// or null when the sweep can't run meaningfully (start reads solid — resolve will handle it).
	Vector3? SlideAnchor( Scene scene, SdfSculpture target, Vector3 prevAnchor, Vector3 candAnchor )
	{
		var tx = target.WorldTransform;
		Transform At( Vector3 anchor ) =>
			new( tx.Position + (tx.PointToWorld( anchor ) - tx.PointToWorld( candAnchor )), tx.Rotation, tx.Scale );

		var pos = prevAnchor;
		var remaining = candAnchor - prevAnchor; // sculpture-local throughout
		for ( int i = 0; i < SlideIterations && !remaining.IsNearZeroLength; i++ )
		{
			var tr = Filtered( scene, target ).Sweep( _scratch, At( pos ), At( pos + remaining ) ).Run();
			if ( tr.StartedSolid )
				return null;
			if ( !tr.Hit )
			{
				pos += remaining;
				break;
			}
			Deflect( tx, ref pos, ref remaining, tr.Fraction, tr.Normal );
		}

		return pos;
	}

	// Shared slide step: advance to the hit (held a skin short so the next frame starts clear), then
	// project the leftover travel onto the hit plane. All in sculpture-local; the normal arrives in world.
	static void Deflect( in Transform tx, ref Vector3 pos, ref Vector3 remaining, float fraction, Vector3 worldNormal )
	{
		var stepWorld = tx.PointToWorld( pos + remaining ) - tx.PointToWorld( pos );
		float stepLen = stepWorld.Length;
		float keep = MathF.Max( fraction - (stepLen > 1e-4f ? SlideSkin / stepLen : 1f), 0f );
		pos += remaining * keep;

		var remWorld = stepWorld * (1f - fraction);
		var slideWorld = remWorld - worldNormal * Vector3.Dot( remWorld, worldNormal );
		remaining = tx.PointToLocal( tx.Position + slideWorld ); // world vec → sculpture-local vec
	}

	// The world as the clamp sees it — the ground-probe filter: ignore our own pawn hierarchy (the disguise
	// IS the shape being edited) and everything that isn't really scenery — fellow prop bodies (our physics
	// ignores them too) and the hunter's trace-only trigger colliders. Released props, decoys and the map
	// itself all block, exactly like they block feet. Internal so the stamp tool's anchor validation sees
	// the same world the clamp does.
	internal static SceneTrace Filtered( Scene scene, SdfSculpture target ) => scene.Trace
		.IgnoreGameObjectHierarchy( target.GameObject.Root )
		.WithoutTags( HiderController.PropBodyTag, "movecollider", "headcollider", "trigger", "water" );

	// ── Resolve phase (the guarantee) ────────────────────────────────────────────────────────────────

	// Depenetrate the brush's current state: query the native minimum-translation vector against every
	// nearby world body and shift the brush by the accumulated correction (world-space, applied back in
	// sculpture-local). True = the state is now proved clear (possibly after correction); false = it needed
	// more than MaxResolve, where the MTV direction stops being trustworthy — the caller reverts.
	// allowShift false = VERIFY ONLY, no correction ever applied: the single-spline-point path, where the
	// gesture was already restricted against the tube and any residual must become a revert — a whole-shape
	// shift would move points the player isn't touching.
	bool ResolveEndpoint( Scene scene, SdfSculpture target, SdfBrush brush, bool allowShift )
	{
		if ( !SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, InsetFor( brush ), _samples ) )
			return true; // no collision shapes — nothing can embed

		if ( !allowShift )
			return ShapesClear( scene, target );

		var tx = target.WorldTransform;
		var offset = Vector3.Zero; // accumulated world-space correction

		// Solids run FULL-SIZE shapes, so resting contact reads as tiny penetrations — the deadband keeps
		// those from being endlessly "fixed" (see RestTolerance). Splines run their GRACED shapes here, and
		// their deadband must stay zero: the point-gesture verify (ShapesClear, no deadband) tests the same
		// graced shapes, and a state this loop accepted must never read dirty there.
		float deadband = brush.Shape == SdfShape.Spline ? 0f : RestTolerance;

		for ( int i = 0; i < ResolveIterations; i++ )
		{
			var at = new Transform( tx.Position + offset, tx.Rotation, tx.Scale );
			var query = QueryBounds( _scratch, tx, offset );

			bool any = false;
			bool sawMesh = false;
			var correction = Vector3.Zero;
			foreach ( var body in WorldBodies( scene, target, query, _scratch ) )
			{
				ShapeKinds( body, out bool convex, out bool mesh );
				sawMesh |= mesh;

				// dir·dist moves the WORLD body clear (see the engine's ComputePenetration contract) —
				// the brush moves the opposite way, plus a skin so the settled state isn't kiss-touching.
				// Contacts at or under the deadband are resting, not an error — left be.
				if ( convex && body.ComputePenetration( _scratch, at, out var dir, out var dist ) && dist > deadband )
				{
					any = true;
					correction -= dir * (dist + SlideSkin);
				}

				if ( mesh && MeshSampleContacts( scene, target, body, tx, offset, _samples, deadband, ref correction ) )
					any = true;
			}

			// Only mesh worlds need the straddle rays — the native MTV already sees a thin CONVEX slab
			// inside a hull.
			if ( sawMesh && StraddleContacts( scene, target, tx, offset, _samples, deadband, ref correction ) )
				any = true;

			if ( !any )
			{
				if ( !offset.IsNearZeroLength )
					ApplyWorldOffset( brush, tx, offset );
				return true;
			}

			offset += correction;
			if ( offset.Length > MaxResolve )
				return false;
		}

		return false; // still overlapping after the passes — unresolvable this frame
	}

	// Conservative world-space query box around the scratch shapes at (sculpture transform + offset).
	// The parked body has identity rotation, so its bounds minus the parking spot ARE the sculpture-local
	// AABB; a bounding sphere of that stays valid under the sculpture's rotation.
	static BBox QueryBounds( PhysicsBody scratch, in Transform tx, Vector3 offset )
	{
		var b = scratch.GetBounds();
		var localMins = b.Mins - ParkingSpot;
		var localMaxs = b.Maxs - ParkingSpot;
		var localCentre = (localMins + localMaxs) * 0.5f;
		float radius = (localMaxs - localMins).Length * 0.5f + 16f;

		var worldCentre = tx.PointToWorld( localCentre ) + offset;
		return new BBox( worldCentre - radius, worldCentre + radius );
	}

	// Every world body the brush should collide with, bounds-filtered. Mirrors Filtered()'s trace rules,
	// applied by hand because the native overlap/penetration queries bypass trace filtering entirely
	// ("ignoring all collision rules" — including trigger-ness, hence the per-shape trigger check).
	static IEnumerable<PhysicsBody> WorldBodies( Scene scene, SdfSculpture target, BBox query, PhysicsBody exclude )
	{
		var root = target.GameObject.Root;
		foreach ( var body in scene.PhysicsWorld.Bodies )
		{
			if ( body == exclude )
				continue;
			if ( !query.Overlaps( body.GetBounds() ) )
				continue;

			var go = body.GameObject;
			if ( go.IsValid() )
			{
				if ( go.Root == root )
					continue; // our own pawn/disguise/gun — the shape being edited included

				var tags = go.Tags;
				if ( tags.Has( HiderController.PropBodyTag ) || tags.Has( "movecollider" )
					|| tags.Has( "headcollider" ) || tags.Has( "trigger" ) || tags.Has( "water" ) )
					continue;
			}

			// Trigger-only bodies never generate contacts — pass through, exactly like the sweeps do.
			bool solid = false;
			foreach ( var shape in body.Shapes )
			{
				if ( !shape.IsTrigger )
				{
					solid = true;
					break;
				}
			}
			if ( !solid )
				continue;

			yield return body;
		}
	}

	// ── Mesh-world contacts ──────────────────────────────────────────────────────────────────────────

	// Which query family a world body needs: convex shapes take the native CheckOverlap/ComputePenetration
	// path, mesh shapes the sample path below, and a mixed body takes both — the native queries silently
	// skip its mesh shapes, they don't fail on them.
	static void ShapeKinds( PhysicsBody body, out bool convex, out bool mesh )
	{
		convex = false;
		mesh = false;
		foreach ( var shape in body.Shapes )
		{
			if ( shape.IsMeshShape )
				mesh = true;
			else
				convex = true;
		}
	}

	/// <summary>Contact test + correction against ONE mesh world body — the stand-in for the native
	/// overlap/MTV pair, which returns nothing at all for triangle-mesh shapes (verified with
	/// mimi_clamp_probe). Each sample of the scratch geometry resolves like this:
	///
	///  • sample centre BURIED in solid (<see cref="ProbeSolid"/> reads start-solid): push toward the body's
	///    nearest surface point and out the far side — depth is the surface distance plus the radius.
	///  • centre outside but the surface nearer than the radius: push away from the surface point by the
	///    shortfall.
	///
	/// Contributions merge into <paramref name="correction"/> by DEFICIT along their own directions (see
	/// ResolveGroup's reasoning) — a row of samples resting on the same floor must never stack into a
	/// launch. Returns true if anything penetrated beyond <paramref name="deadband"/>, the same "any"
	/// contract as the native path — including the rare sample that sits dead on a surface inside solid,
	/// which contributes no direction (another sample, or the caller's revert, resolves it) but must still
	/// keep the state reading dirty. A burial deeper than <see cref="MaxResolve"/> simply drives the
	/// caller's accumulated offset over its cap, which reverts to the last clear state — same contract as
	/// the native path.</summary>
	static bool MeshSampleContacts( Scene scene, SdfSculpture target, PhysicsBody body, in Transform tx,
		Vector3 offset, List<Vector4> samples, float deadband, ref Vector3 correction )
	{
		if ( samples is not { Count: > 0 } )
			return false;

		var bounds = body.GetBounds();
		bool any = false;

		foreach ( var s in samples )
		{
			// Positions go through the transform (scale included); radii scale uniformly — sculptures are
			// only ever uniformly scaled, and the rest of the clamp already assumes the same. Hull centroid
			// markers (w = -1, see BuildSweepShapes) test here as plain points; their marker role belongs
			// to StraddleContacts.
			float r = MathF.Max( s.w, 0f ) * tx.Scale.x;
			var w = tx.PointToWorld( new Vector3( s.x, s.y, s.z ) ) + offset;

			// Only samples near THIS body can contact it — and the probe below is scene-wide, so without
			// this bound a sample buried in some other body would be "corrected" against this one's surface.
			float pad = r + deadband + 1f;
			if ( w.x < bounds.Mins.x - pad || w.y < bounds.Mins.y - pad || w.z < bounds.Mins.z - pad
				|| w.x > bounds.Maxs.x + pad || w.y > bounds.Maxs.y + pad || w.z > bounds.Maxs.z + pad )
				continue;

			var p = body.FindClosestPoint( w );
			var delta = w - p;
			float d = delta.Length;

			if ( d < 1e-3f )
			{
				// Dead on the surface — direction unknowable from here, and the penetration measure is just
				// the radius. A corner sample (r = 0) exactly on a surface is resting contact inside any
				// deadband — flagging it dirty would revert-freeze a grounded brush over a measure-zero
				// alignment. A sphere CENTRE on a surface is a half-buried sphere: dirty, directionless
				// (another sample, or the caller's revert, resolves it).
				any |= r > deadband && ProbeSolid( scene, target, w );
				continue;
			}

			if ( ProbeSolid( scene, target, w ) )
			{
				// Centre inside solid: out through the nearest face, plus the radius.
				float pen = d + r;
				if ( pen > deadband )
				{
					any = true;
					Deficit( ref correction, -delta / d, pen + SlideSkin );
				}
				continue;
			}

			float shortfall = r - d;
			if ( shortfall > deadband )
			{
				any = true;
				Deficit( ref correction, delta / d, shortfall + SlideSkin );
			}
		}

		return any;
	}

	/// <summary>The straddle belt — the one hole point sampling leaves open: a hull can cross a THIN mesh
	/// slab (a tabletop, an FBX floor piece) with every corner AND the centroid sitting in open air on
	/// either side, which no point query can see. So hull samples get one more test: a RAY from the hull's
	/// centroid marker (w = -1, see BuildSweepShapes) to each of its corners. Crossing a world surface on
	/// the way means a face passes INSIDE the hull — push out along that face's normal by how far the
	/// corner sits past it. Reachable only by non-swept changes (scale/rotate gestures, the stamp ghost's
	/// teleports) — travel is already blocked by the sweep phase. Scene-wide through the clamp's filter, so
	/// it covers convex bodies too; the native MTV reports those as well, and the deficit merge makes the
	/// duplicate free. Sphere samples carry no corners and need none — FindClosestPoint sees a slab poking
	/// into a sphere just fine.</summary>
	static bool StraddleContacts( Scene scene, SdfSculpture target, in Transform tx, Vector3 offset,
		List<Vector4> samples, float deadband, ref Vector3 correction )
	{
		bool any = false;
		Vector3 centroid = default;
		bool haveCentroid = false;

		foreach ( var s in samples )
		{
			if ( s.w < 0f )
			{
				centroid = tx.PointToWorld( new Vector3( s.x, s.y, s.z ) ) + offset;
				haveCentroid = true;
				continue;
			}
			if ( !haveCentroid || s.w > 0f )
				continue; // not a hull corner

			var corner = tx.PointToWorld( new Vector3( s.x, s.y, s.z ) ) + offset;
			var tr = Filtered( scene, target ).Ray( centroid, corner ).Run();
			if ( !tr.Hit )
				continue; // no face between centroid and corner (a centroid itself buried in solid is the
				          // probes' case — a ray from inside leaves through the backface unimpeded)

			// How far past the crossed face the corner sits. Resting contact (solids embed up to the
			// deadband by design) must not read as a straddle, or every later edit of a grounded brush
			// would ratchet it upward.
			float past = (1f - tr.Fraction) * Vector3.DistanceBetween( centroid, corner );
			if ( past <= deadband )
				continue;

			any = true;
			Deficit( ref correction, tr.Normal, past + SlideSkin );
		}

		return any;
	}

	// Is this world-space point inside SOLID scenery? Zero-length SHAPE traces report start-solid reliably —
	// the engine's own CharacterController leans on exactly this, and mimi_clamp_probe confirmed it against
	// mesh shapes — unlike zero-length BODY sweeps, which this file already caught lying once (see the
	// class doc). Scene-wide through the clamp's own filter, so "solid" means what "solid" means everywhere
	// else here.
	static bool ProbeSolid( Scene scene, SdfSculpture target, Vector3 point ) =>
		Filtered( scene, target ).Sphere( 0.05f, point, point ).Run().StartedSolid;

	// Extend `correction` only by what this contact still needs along its own direction — parallel
	// duplicates add ~nothing, orthogonal contacts get their full push (see ResolveGroup's reasoning).
	static void Deficit( ref Vector3 correction, Vector3 dir, float need )
	{
		float have = Vector3.Dot( correction, dir );
		if ( have < need )
			correction += dir * (need - have);
	}

	// Shift the brush by a world-space correction, expressed back in sculpture-local space. Splines carry
	// their geometry in Points, so every point shifts; everything else moves its Position.
	static void ApplyWorldOffset( SdfBrush brush, in Transform tx, Vector3 offsetWorld )
	{
		var local = tx.PointToLocal( tx.Position + offsetWorld ); // world vec → sculpture-local vec
		if ( brush.Shape == SdfShape.Spline )
		{
			if ( brush.Points is not { } pts )
				return;
			for ( int i = 0; i < pts.Count; i++ )
				pts[i] = new Vector4( pts[i].x + local.x, pts[i].y + local.y, pts[i].z + local.z, pts[i].w );
		}
		else
		{
			brush.Position += local;
		}
	}

	// ── Shared plumbing ──────────────────────────────────────────────────────────────────────────────

	void EnsureScratch( PhysicsWorld world )
	{
		if ( _scratch is not null && _scratch.IsValid() && _scratchWorld == world )
			return;

		if ( _scratch is not null && _scratch.IsValid() )
			_scratch.Remove();

		_scratch = new PhysicsBody( world ) { BodyType = PhysicsBodyType.Static, Position = ParkingSpot };
		_scratchWorld = world;
	}

	static int GeometryHash( SdfBrush b )
	{
		int h = unchecked( (int)2166136261 );
		b.HashInto( ref h );
		return h;
	}

	static bool SameShapeIgnoringPosition( SdfBrush a, SdfBrush b ) =>
		a.Shape == b.Shape && a.CrossSection == b.CrossSection
		&& a.Size == b.Size && a.Rotation == b.Rotation && a.Slice == b.Slice
		&& a.MirrorX == b.MirrorX && a.MirrorY == b.MirrorY && a.MirrorZ == b.MirrorZ
		&& a.Text == b.Text && a.Font == b.Font;

	// Restore the GEOMETRIC pose from a clear snapshot — everything that shapes the collider, nothing that
	// styles it (colour/material/blend/rounding stay wherever the gesture put them: the collider is the
	// sharp primitive, so they can never turn a clear state blocked).
	static void RestorePose( SdfBrush b, SdfBrush s )
	{
		b.Shape = s.Shape;
		b.CrossSection = s.CrossSection;
		b.Text = s.Text;
		b.Font = s.Font;
		b.TextData = s.TextData;
		b.Position = s.Position;
		b.Rotation = s.Rotation;
		b.Size = s.Size;
		b.Slice = s.Slice;
		b.MirrorX = s.MirrorX;
		b.MirrorY = s.MirrorY;
		b.MirrorZ = s.MirrorZ;
		b.Curvature = s.Curvature;
		b.SplineClosed = s.SplineClosed;

		// Restore spline points IN PLACE when the layout matches — the gizmo holds the live List through a
		// point drag, and swapping the instance out from under it would orphan the rest of the gesture.
		if ( s.Points is null )
			b.Points = null;
		else if ( b.Points is { } live && live.Count == s.Points.Count )
		{
			for ( int i = 0; i < live.Count; i++ )
				live[i] = s.Points[i];
		}
		else
			b.Points = new List<Vector4>( s.Points );
	}
}
