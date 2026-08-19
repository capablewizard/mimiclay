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
/// fight the gizmo): every geometric change to the active brush is tested against the world — a scratch
/// <see cref="PhysicsBody"/> carrying the brush's own collision shapes (see
/// <see cref="SdfCollisionBuilder.BuildSweepShapes"/>), swept from its last clear state to the candidate by
/// <c>Scene.Trace.Sweep</c> (the sweep catches a fast flick tunnelling a thin wall in one frame) — and a
/// blocked change is simply reverted to the last clear state: a sticky stop at the surface. Additive
/// brushes only, by construction — BuildSweepShapes yields nothing for carves/cutouts, which can only
/// remove your own clay and are harmless (and satisfying) to wave through walls.
///
/// One instance per <see cref="SculptEditSession"/>, fed from the session's preview/commit funnel — the one
/// choke every continuous edit path (gizmo drags, scrubs, stamp placement, HUD sliders, wheel push) runs
/// through. Discrete paths that bypass the funnel for the WATCHED brush (undo/redo splices, an eye-toggle on
/// an unselected row) are covered by the commit-time backstop instead: <see cref="EmbeddedInWorld"/>, which
/// <see cref="SdfCollider.Rebuild"/> consults before swapping in a new collider.
///
/// Invariants:
///  • Owner-local only, like every validity gate — sessions only run on the editing machine, and the
///    backstop gates on !IsProxy. Proxies always build what they received.
///  • Never traps: a brush that STARTS blocked (loaded embedded, revealed by an eye-toggle, the pawn
///    settled) moves freely until it first tests clear — only then does clamping engage.
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

	// Where scratch bodies physically live. Sweeps place their shapes virtually at the from/to transforms, so
	// the resident shapes just need to be somewhere no gameplay trace or contact will ever find them.
	static readonly Vector3 ParkingSpot = new( 0f, 0f, -200000f );

	SdfBrush _watched;    // the brush the snapshot belongs to — identity, not index (undo splices new instances)
	SdfBrush _lastClear;  // Copy() of the last state that tested clear; null = none yet / started blocked
	int _lastHash;        // geometry hash at the last test, so an unchanged brush costs nothing per frame
	PhysicsBody _scratch;
	PhysicsWorld _scratchWorld;

	/// <summary>Run the clamp for this frame's state of the active brush. Call AFTER the tools have mutated
	/// it and BEFORE the surface rebuild, so a reverted state is what gets rendered, recorded and streamed.
	/// <paramref name="sweepFromLast"/> = also sweep the travel from the last clear position (gizmo/scrub
	/// drags — catches tunnelling); off for the stamp ghost, whose cursor-jumps across the scene are
	/// teleports, not travel.</summary>
	public void Apply( SdfSculpture target, SdfBrush brush, bool sweepFromLast )
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

		_lastHash = hash;

		if ( !Blocked( target, brush, sweepFromLast ? _lastClear : null ) )
		{
			_lastClear = brush.Copy();
			return;
		}

		if ( _lastClear is null )
			return; // started blocked — free until it first tests clear (never trap a brush)

		RestorePose( brush, _lastClear );
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
	/// undo/redo, layer-row toggles on unselected brushes, conversions, loads.</summary>
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
			foreach ( var b in brushes )
			{
				if ( !SdfCollisionBuilder.BuildSweepShapes( b, body, BackstopInset ) )
					continue;
				if ( SweepHits( scene, body, sculpture, Vector3.Zero ) )
					return true;
			}
			return false;
		}
		finally
		{
			body.Remove();
		}
	}

	// ── Internals ────────────────────────────────────────────────────────────────────────────────────

	bool Blocked( SdfSculpture target, SdfBrush brush, SdfBrush from )
	{
		var scene = target.Scene;
		if ( !scene.IsValid() )
			return false;

		EnsureScratch( scene.PhysicsWorld );
		if ( !SdfCollisionBuilder.BuildSweepShapes( brush, _scratch, ClampInset ) )
			return false; // yields no collision (detail brush) — can't embed anything, free to move

		return SweepHits( scene, _scratch, target, LocalDelta( brush, from ) );
	}

	// Sweep `shapes` (sculpture-local) from the sculpture's world transform offset by `localDelta` to the
	// transform itself. Zero delta = a pure overlap test (start-solid). The filter is the ground-probe one:
	// ignore our own pawn hierarchy (the disguise IS the shape being edited) and everything that isn't
	// really scenery — fellow prop bodies (our physics ignores them too) and the hunter's trace-only
	// trigger colliders. Released props, decoys and the map itself all block, exactly like they block feet.
	internal static bool SweepHits( Scene scene, PhysicsBody shapes, SdfSculpture target, Vector3 localDelta )
	{
		var to = target.WorldTransform;
		var from = to;
		if ( !localDelta.IsNearZeroLength )
			from = new Transform( to.Position + (to.PointToWorld( localDelta ) - to.Position), to.Rotation, to.Scale );

		var tr = scene.Trace
			.Sweep( shapes, from, to )
			.IgnoreGameObjectHierarchy( target.GameObject.Root )
			.WithoutTags( HiderController.PropBodyTag, "movecollider", "headcollider", "trigger", "water" )
			.Run();

		return tr.StartedSolid || tr.Hit;
	}

	// The travel since the last clear state, in sculpture-local space — a representative point is enough,
	// the sweep exists to catch whole-brush tunnelling, not grazes. Splines move per-point; use the first
	// point when the layout matches, else treat it as a structural change (no travel).
	static Vector3 LocalDelta( SdfBrush cur, SdfBrush prev )
	{
		if ( prev is null )
			return Vector3.Zero;

		if ( cur.Shape == SdfShape.Spline )
		{
			if ( cur.Points is { Count: > 0 } cp && prev.Points is { Count: > 0 } pp && cp.Count == pp.Count )
				return new Vector3( pp[0].x - cp[0].x, pp[0].y - cp[0].y, pp[0].z - cp[0].z );
			return Vector3.Zero;
		}

		return prev.Position - cur.Position;
	}

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
