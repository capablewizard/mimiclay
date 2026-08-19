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
///  • Contact is not penetration: shapes are tested inset by <see cref="ClampInset"/> so clay resting
///    flush on the floor — which ground-snap actively produces — always reads clear.
/// </summary>
public sealed class BrushWorldClamp
{
	/// <summary>Shape inset for the LIVE clamp — deep enough that flush floor contact reads clear, shallow
	/// enough that a brush can't visibly bury itself.</summary>
	public const float ClampInset = 1f;

	/// <summary>Shape inset for the commit-time backstop — more tolerant than the live clamp so the two can
	/// never disagree the wrong way round (everything the clamp allows, the backstop accepts) and so contact
	/// noise from a settling body can't freeze collider rebuilds.</summary>
	public const float BackstopInset = 4f;

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
		var collider = target.GameObject.Components.Get<SdfCollider>();
		if ( !collider.IsValid() || !collider.Active || collider.BuildAsTrigger )
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
		//    whatever it hits, so travel can never cross a wall and drags glide along surfaces.
		var prev = _lastClear;
		if ( prev is not null )
			SweepPhase( scene, target, brush, prev );

		// 2) The guarantee: actively depenetrate whatever state the tools (and the slide) produced. This is
		//    the layer with no start-state sensitivity — an accepted state is always a proved-clear state.
		if ( ResolveEndpoint( scene, target, brush ) )
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
	}

	// ── Backstop ─────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Commit-time backstop: is any of this sculpture's clay embedded in the world's solid geometry
	/// (deeper than <see cref="BackstopInset"/>)? <see cref="SdfCollider.Rebuild"/> consults this before
	/// swapping in a new collider — a collider built embedded is what the physics solver depenetrates by
	/// shoving the whole prop, potentially through the wall. Covers every path the live clamp can't see:
	/// undo/redo, layer-row toggles on unselected brushes, conversions, loads. Uses the native overlap query
	/// (<see cref="PhysicsBody.CheckOverlap"/>) — never a zero-length sweep, which can miss a body it
	/// starts inside of.</summary>
	public static bool EmbeddedInWorld( SdfSculpture sculpture )
	{
		if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } brushes )
			return false;

		var scene = sculpture.Scene;
		if ( !scene.IsValid() || scene.PhysicsWorld is not { } world )
			return false;

		var body = new PhysicsBody( world ) { BodyType = PhysicsBodyType.Static, Position = ParkingSpot };
		try
		{
			var tx = sculpture.WorldTransform;
			foreach ( var b in brushes )
			{
				if ( !SdfCollisionBuilder.BuildSweepShapes( b, body, BackstopInset ) )
					continue;

				var query = QueryBounds( body, tx, Vector3.Zero );
				foreach ( var wb in WorldBodies( scene, sculpture, query, body ) )
					if ( wb.CheckOverlap( body, tx ) )
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
	// untouched: resolve alone handles them.
	void SweepPhase( Scene scene, SdfSculpture target, SdfBrush brush, SdfBrush prev )
	{
		if ( brush.Shape != SdfShape.Spline )
		{
			// Rigid move of a solid: everything but Position must match the last clear state.
			if ( !SameShapeIgnoringPosition( brush, prev ) )
				return;

			var delta = brush.Position - prev.Position;
			if ( delta.IsNearZeroLength || delta.Length > SweepMaxTravel )
				return;

			if ( !SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, ClampInset ) )
				return;
			if ( SlideAnchor( scene, target, prev.Position, brush.Position ) is { } pos )
				brush.Position = pos;
			return;
		}

		// Splines: geometry lives in Points. Two sweepable cases — the WHOLE curve moved by one delta (the
		// wheel push), or exactly ONE control point moved (a gizmo dot drag). Radii/curvature/loop/mirror
		// changes are structural — resolve-only.
		if ( prev.Points is not { Count: > 0 } pp || brush.Points is not { } cp || cp.Count != pp.Count
			|| brush.Curvature != prev.Curvature || brush.SplineClosed != prev.SplineClosed
			|| brush.MirrorX != prev.MirrorX || brush.MirrorY != prev.MirrorY || brush.MirrorZ != prev.MirrorZ )
			return;

		int moved = -1;
		bool uniform = true;
		var delta0 = P( cp[0] ) - P( pp[0] );
		for ( int i = 0; i < cp.Count; i++ )
		{
			if ( MathF.Abs( cp[i].w - pp[i].w ) > 1e-4f )
				return; // a radius change rode along — not a translation

			var di = P( cp[i] ) - P( pp[i] );
			if ( (di - delta0).Length > 1e-3f )
				uniform = false;
			if ( !di.IsNearZeroLength )
				moved = moved == -1 ? i : -2; // -2 = more than one point moved
		}

		if ( uniform && !delta0.IsNearZeroLength && delta0.Length <= SweepMaxTravel )
		{
			// Whole-curve move: rigid, so the full-shape sweep applies. Anchor on point 0.
			if ( !SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, ClampInset ) )
				return;
			if ( SlideAnchor( scene, target, P( pp[0] ), P( cp[0] ) ) is not { } slid )
				return;

			var net = slid - P( pp[0] );
			for ( int i = 0; i < cp.Count; i++ )
			{
				var q = P( pp[i] ) + net;
				cp[i] = new Vector4( q.x, q.y, q.z, pp[i].w );
			}
			return;
		}

		if ( moved < 0 )
			return; // nothing / several points moved — not a sweepable gesture

		// One control point dragged: the tube deforms non-rigidly, so sweep just that point's sphere as a
		// guide (the rest of the tube didn't move) — resolve then judges the whole re-shaped tube.
		var from3 = P( pp[moved] );
		var to3 = P( cp[moved] );
		if ( (to3 - from3).Length > SweepMaxTravel )
			return;

		float r = MathF.Max( pp[moved].w - ClampInset, 0.5f );
		var tx = target.WorldTransform;

		var pos3 = from3;
		var remaining = to3 - from3;
		for ( int i = 0; i < SlideIterations && !remaining.IsNearZeroLength; i++ )
		{
			var tr = Filtered( scene, target )
				.Sphere( r, tx.PointToWorld( pos3 ), tx.PointToWorld( pos3 + remaining ) )
				.Run();
			if ( tr.StartedSolid )
				return; // degenerate start — leave the candidate for resolve to fix or veto
			if ( !tr.Hit )
			{
				pos3 += remaining;
				break;
			}
			Deflect( tx, ref pos3, ref remaining, tr.Fraction, tr.Normal );
		}

		cp[moved] = new Vector4( pos3.x, pos3.y, pos3.z, pp[moved].w );
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
	// itself all block, exactly like they block feet.
	static SceneTrace Filtered( Scene scene, SdfSculpture target ) => scene.Trace
		.IgnoreGameObjectHierarchy( target.GameObject.Root )
		.WithoutTags( HiderController.PropBodyTag, "movecollider", "headcollider", "trigger", "water" );

	// ── Resolve phase (the guarantee) ────────────────────────────────────────────────────────────────

	// Depenetrate the brush's current state: query the native minimum-translation vector against every
	// nearby world body and shift the brush by the accumulated correction (world-space, applied back in
	// sculpture-local). True = the state is now proved clear (possibly after correction); false = it needed
	// more than MaxResolve, where the MTV direction stops being trustworthy — the caller reverts.
	bool ResolveEndpoint( Scene scene, SdfSculpture target, SdfBrush brush )
	{
		if ( !SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, ClampInset ) )
			return true; // no collision shapes — nothing can embed

		var tx = target.WorldTransform;
		var offset = Vector3.Zero; // accumulated world-space correction
		for ( int i = 0; i < ResolveIterations; i++ )
		{
			var at = new Transform( tx.Position + offset, tx.Rotation, tx.Scale );
			var query = QueryBounds( _scratch, tx, offset );

			bool any = false;
			var correction = Vector3.Zero;
			foreach ( var body in WorldBodies( scene, target, query, _scratch ) )
			{
				if ( !body.ComputePenetration( _scratch, at, out var dir, out var dist ) )
					continue;
				any = true;
				// dir·dist moves the WORLD body clear (see the engine's ComputePenetration contract) —
				// the brush moves the opposite way, plus a skin so the settled state isn't kiss-touching.
				correction -= dir * (dist + SlideSkin);
			}

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
