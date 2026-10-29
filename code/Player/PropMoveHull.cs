using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// The convex shape a prop pawn MOVES with: the convex hull of its clay's emitted collider
/// (<see cref="SdfCollider.FramePoints"/>), in pawn-root space, on a scratch <see cref="PhysicsBody"/> parked
/// far below the world and swept with <c>Scene.Trace.Sweep</c>. One convex shape, however irregular the clay:
///
///  • it sits flush — the hull's lowest point IS the clay's lowest point, so nothing hovers or sinks;
///  • the clay never pokes through a wall the hull was stopped by — the clay lies inside its hull;
///  • there are no internal edges between overlapping shapes for a sweep to snag on, and one shape is the
///    cheapest thing to sweep.
///
/// The accepted trade: a prop's OWN movement ignores concavities (an arch can't straddle a post). Everyone else
/// still collides with the real compound collider, which rides the pawn's keyframed body.
///
/// Rebuilt when the collider rebuilds (<see cref="SdfCollider.BuildSerial"/>) or the disguise moves relative to
/// the root (the origin recentre on commit, a possessed tumble). Pure local derivation — never networked.
/// </summary>
public sealed class PropMoveHull : IDisposable
{
	// The engine silently drops hulls past ~127 edges (see SdfCollisionBuilder.HullEdgeLimit); a triangulated
	// 40-vertex hull has 114. Thinning keeps a hull's vertices, so the thinned hull sits up to a few units INSIDE
	// the clay on round parts — but the vertices that set where it rests (the underside) are seeded first.
	const int MaxVertices = 40;

	// Hull vertices within this of the lowest one are the resting face — kept through thinning so a flat
	// underside stays exactly flat.
	const float RestFaceTolerance = 0.5f;

	static readonly Vector3 ParkingSpot = new( 0f, 0f, -210000f );

	PhysicsBody _body;
	PhysicsWorld _world;
	int _builtSerial = -1;
	Transform _builtLocal;
	readonly List<Vector3> _verts = new();
	readonly List<(Vector3 a, Vector3 b, Vector3 c)> _tris = new();

	/// <summary>Hull vertices, pawn-root space.</summary>
	public IReadOnlyList<Vector3> Vertices => _verts;

	/// <summary>Pawn-root-space bounds of the hull.</summary>
	public BBox LocalBounds { get; private set; }

	/// <summary>True when there's a hull to sweep.</summary>
	public bool Ready => _body is not null && _body.IsValid() && _verts.Count >= 4;

	/// <summary>Bumped whenever the hull changes — movers un-park on it.</summary>
	public int Version { get; private set; }

	/// <summary>Re-derive from <paramref name="collider"/> if its build or its placement under the pawn root
	/// changed. The disguise is a direct child of the root, so its local transform IS disguise→root.</summary>
	public void Refresh( SdfCollider collider, PhysicsWorld world )
	{
		if ( !collider.IsValid() || world is null )
			return;

		var local = collider.GameObject.LocalTransform;
		if ( Ready && _world == world && _builtSerial == collider.BuildSerial && local.AlmostEqual( _builtLocal, 0.001f ) )
			return;

		_builtSerial = collider.BuildSerial;
		_builtLocal = local;

		var points = new List<Vector3>( collider.FramePoints.Count );
		foreach ( var p in collider.FramePoints )
			points.Add( local.PointToWorld( p ) );

		if ( !TryHull( points, _verts, _tris ) )
		{
			_verts.Clear();
			_tris.Clear();
			Version++;
			return;
		}

		var bounds = BBox.FromPoints( _verts );
		LocalBounds = bounds;

		if ( _body is null || !_body.IsValid() || _world != world )
		{
			_body?.Remove();
			_body = new PhysicsBody( world ) { BodyType = PhysicsBodyType.Static, Position = ParkingSpot };
			_world = world;
		}

		_body.ClearShapes();
		_body.AddHullShape( Vector3.Zero, Rotation.Identity, _verts );
		Version++;
	}

	/// <summary>Sweep the hull from <paramref name="from"/> to <paramref name="to"/> (pawn-root transforms) through
	/// the world <paramref name="trace"/> sees. Rotation may change during the sweep.</summary>
	public SceneTraceResult Sweep( SceneTrace trace, in Transform from, in Transform to )
		=> trace.Sweep( _body, from, to ).Run();

	/// <summary>Is the hull at <paramref name="pose"/> overlapping anything solid? Translation sweeps can't
	/// answer this — the engine's sweep ignores the end rotation, and a sweep that STARTS in contact with a
	/// convex body can pass straight through it — so this splits by world body kind, the same split
	/// <see cref="BrushWorldClamp"/> settled on: a short sweep's start-solid flag (reliable against triangle
	/// meshes) plus the native <see cref="PhysicsBody.CheckOverlap(PhysicsBody, Transform)"/> against nearby
	/// convex bodies (reliable there, blind to meshes). Resting a skin away from a surface is NOT an overlap.</summary>
	public bool Overlaps( Scene scene, SceneTrace trace, GameObject ignoreRoot, string[] ignoreTags, in Transform pose )
	{
		if ( !Ready )
			return false;

		var nudge = pose.WithPosition( pose.Position + Vector3.Up * 0.05f );
		if ( Sweep( trace, pose, nudge ).StartedSolid )
			return true;

		var bounds = LocalBounds.Transform( pose ).Grow( 1f );
		foreach ( var body in scene.PhysicsWorld.Bodies )
		{
			if ( body == _body || !bounds.Overlaps( body.GetBounds() ) )
				continue;

			var go = body.GameObject;
			if ( go.IsValid() && (go.Root == ignoreRoot || go.Tags.HasAny( ignoreTags )) )
				continue;

			bool convex = false;
			foreach ( var shape in body.Shapes )
			{
				if ( !shape.IsTrigger && !shape.IsMeshShape )
				{
					convex = true;
					break;
				}
			}
			if ( convex && body.CheckOverlap( _body, pose ) )
				return true;
		}
		return false;
	}

	public void Dispose()
	{
		if ( _body is not null && _body.IsValid() )
			_body.Remove();
		_body = null;
		_world = null;
		_verts.Clear();
		_tris.Clear();
	}

	/// <summary>Draw the hull at <paramref name="root"/> (debug overlay, this frame only).</summary>
	public void Draw( Scene scene, in Transform root, Color color )
	{
		foreach ( var (a, b, c) in _tris )
		{
			var wa = root.PointToWorld( a );
			var wb = root.PointToWorld( b );
			var wc = root.PointToWorld( c );
			scene.DebugOverlay.Line( wa, wb, color, 0f );
			scene.DebugOverlay.Line( wb, wc, color, 0f );
			scene.DebugOverlay.Line( wc, wa, color, 0f );
		}
	}

	// Hull the points, thinning to MaxVertices when needed: the resting face (everything within
	// RestFaceTolerance of the lowest vertex) and the six axis extremes are seeded first, then farthest-point
	// sampling spreads the rest evenly.
	static bool TryHull( List<Vector3> points, List<Vector3> verts, List<(Vector3, Vector3, Vector3)> tris )
	{
		var planes = new List<(Vector3 n, float d)>( 64 );
		if ( !ConvexHull.TryBuild( points, planes, verts, tris ) )
			return false;
		if ( verts.Count <= MaxVertices )
			return true;

		var all = new List<Vector3>( verts );
		var picked = new List<Vector3>( MaxVertices );
		var taken = new bool[all.Count];

		void Take( int i )
		{
			if ( taken[i] || picked.Count >= MaxVertices )
				return;
			taken[i] = true;
			picked.Add( all[i] );
		}

		int lowest = 0;
		for ( int i = 1; i < all.Count; i++ )
			if ( all[i].z < all[lowest].z )
				lowest = i;
		float floor = all[lowest].z;

		// The resting face, capped at a third of the budget so a huge flat base can't starve the rest.
		Take( lowest );
		for ( int i = 0; i < all.Count && picked.Count < MaxVertices / 3; i++ )
			if ( all[i].z - floor <= RestFaceTolerance )
				Take( i );

		Span<Vector3> axes = stackalloc Vector3[] { Vector3.Forward, Vector3.Backward, Vector3.Left, Vector3.Right, Vector3.Up };
		foreach ( var axis in axes )
		{
			int best = 0;
			for ( int i = 1; i < all.Count; i++ )
				if ( Vector3.Dot( all[i], axis ) > Vector3.Dot( all[best], axis ) )
					best = i;
			Take( best );
		}

		// Farthest-point sampling from everything picked so far.
		var minD = new float[all.Count];
		for ( int i = 0; i < all.Count; i++ )
		{
			minD[i] = float.MaxValue;
			foreach ( var p in picked )
				minD[i] = MathF.Min( minD[i], (all[i] - p).LengthSquared );
		}

		while ( picked.Count < MaxVertices )
		{
			int next = -1;
			float best = -1f;
			for ( int i = 0; i < all.Count; i++ )
			{
				if ( taken[i] || minD[i] <= best )
					continue;
				best = minD[i];
				next = i;
			}
			if ( next < 0 )
				break;

			Take( next );
			for ( int i = 0; i < all.Count; i++ )
				minD[i] = MathF.Min( minD[i], (all[i] - all[next]).LengthSquared );
		}

		return ConvexHull.TryBuild( picked, planes, verts, tris );
	}
}
