using System;
using System.Collections.Generic;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Diagnostic for the editing-collision clamp going dead on specific maps (first seen: the FBX-imported
/// Kitchen). Aim the camera at a floor/wall and run <c>mimi_clamp_probe</c> in the console.
///
/// The clamp (<see cref="BrushWorldClamp"/>) stands on four engine primitives, and this logs a PASS/FAIL
/// verdict for each against the REAL surface under the crosshair, using the same scratch-shape setup the
/// clamp builds (sphere + box hull on a parked static body):
///
///   1. enumeration — would WorldBodies find this body at all (PhysicsWorld.Bodies + GetBounds + filters)?
///   2. CheckOverlap — the commit backstop (EmbeddedInWorld)
///   3. ComputePenetration — the live resolve, THE guarantee layer
///   4. traces — the sweep phase (travel sweep + the zero-length shape trace the engine's own
///      CharacterController trusts for start-solid)
///
/// Run it once on a map where the clamp works (DefaultMap's floor is a BoxCollider) and once where it
/// doesn't (Kitchen: every surface is a triangle-mesh shape) — whichever rows flip between the two runs
/// are the primitives that don't support that map's shape type, and that's exactly what the fallback
/// needs to replace.
/// </summary>
public static class ClampQueryProbe
{
	const float ProbeRadius = 8f;   // sphere probe: same order as a small brush
	const float ProbeEmbed = 4f;    // how deep the probe is buried past the surface — well over the clamp's
	                                // RestTolerance deadband (0.5), well under its MaxResolve revert cap (8)

	static readonly Vector3 ParkingSpot = new( 0f, 0f, -200000f ); // same trick as the clamp's scratch body

	[ConCmd( "mimi_clamp_probe" )]
	public static void Run()
	{
		var scene = Game.ActiveScene;
		if ( scene is null || scene.PhysicsWorld is not { } world )
		{
			Log.Info( "mimi_clamp_probe: no active scene / physics world." );
			return;
		}

		var cam = scene.Camera;
		if ( !cam.IsValid() )
		{
			Log.Info( "mimi_clamp_probe: no camera to aim with." );
			return;
		}

		// The surface under the crosshair, seen through the clamp's own world filter. Straight down as a
		// fallback so the command also works from a free/menu camera pointed at the sky.
		var eye = cam.WorldPosition;
		var tr = ClampFilter( scene ).Ray( eye, eye + cam.WorldRotation.Forward * 4096f ).Run();
		if ( !tr.Hit )
			tr = ClampFilter( scene ).Ray( eye, eye + Vector3.Down * 4096f ).Run();
		if ( !tr.Hit || tr.Body is not { } hitBody || !hitBody.IsValid() )
		{
			Log.Info( "mimi_clamp_probe: nothing solid under the crosshair (or below the camera)." );
			return;
		}

		var go = hitBody.GameObject;
		Log.Info( "── mimi_clamp_probe ──────────────────────────────────────────────" );
		Log.Info( $"surface: '{(go.IsValid() ? go.Name : "<no GameObject>")}' at {tr.HitPosition}, normal {tr.Normal}" );
		Log.Info( $"body: type={hitBody.BodyType}, shapes={Describe( hitBody )}, tags='{(go.IsValid() ? string.Join( ",", go.Tags.TryGetAll() ) : "")}'" );
		Log.Info( $"body.GetBounds(): {hitBody.GetBounds()}" );

		// Probe centre: buried ProbeEmbed past the surface along the hit normal — an unambiguous overlap
		// of every probe shape, at a depth the live clamp is expected to correct (not revert).
		var centre = tr.HitPosition + tr.Normal * (ProbeRadius - ProbeEmbed);

		// 1) Enumeration: would the clamp's WorldBodies loop even reach this body?
		var query = new BBox( centre - (ProbeRadius + 16f), centre + (ProbeRadius + 16f) );
		bool enumerated = false, boundsOverlap = false;
		foreach ( var b in world.Bodies )
		{
			if ( b != hitBody )
				continue;
			enumerated = true;
			boundsOverlap = query.Overlaps( b.GetBounds() );
			break;
		}
		Verdict( "enumeration: body in PhysicsWorld.Bodies", enumerated );
		Verdict( "enumeration: GetBounds overlaps query box", boundsOverlap );

		// 2 + 3) The overlap/MTV battery, once per scratch-shape kind the clamp actually builds.
		RunShapeBattery( scene, world, hitBody, centre, tr.Normal, sphere: true );
		RunShapeBattery( scene, world, hitBody, centre, tr.Normal, sphere: false );

		// 4b) FindClosestPoint — candidate primitive for a mesh-safe fallback resolve. Expected: a point
		// ~ProbeEmbed short of the probe centre (i.e. on the surface we just hit).
		var closest = hitBody.FindClosestPoint( centre );
		float closestDist = Vector3.DistanceBetween( closest, centre );
		Verdict( $"FindClosestPoint: dist {closestDist:0.##} (expect ≈{ProbeRadius - ProbeEmbed:0.##})",
			MathF.Abs( closestDist - (ProbeRadius - ProbeEmbed) ) < 1f );

		Log.Info( "──────────────────────────────────────────────────────────────────" );
		Log.Info( "Rows that PASS on DefaultMap but FAIL here are the primitives this map's shape type breaks." );
	}

	// The full query battery for one scratch-shape kind, on a fresh parked body — mirrors the clamp's
	// EnsureScratch/BuildSweepShapes setup exactly (static body at the parking spot, shapes local to it,
	// tested via the transform overloads).
	static void RunShapeBattery( Scene scene, PhysicsWorld world, PhysicsBody hitBody, Vector3 centre, Vector3 normal, bool sphere )
	{
		string kind = sphere ? "sphere" : "hull";
		var probe = new PhysicsBody( world ) { BodyType = PhysicsBodyType.Static, Position = ParkingSpot };
		try
		{
			if ( sphere )
			{
				probe.AddSphereShape( Vector3.Zero, ProbeRadius, rebuildMass: false );
			}
			else
			{
				// The 8 corners of a box the sphere probe would inscribe — the same AddHullShape route
				// BuildSweepShapes uses for solid brushes.
				var h = ProbeRadius;
				var corners = new List<Vector3>();
				for ( int x = -1; x <= 1; x += 2 )
				for ( int y = -1; y <= 1; y += 2 )
				for ( int z = -1; z <= 1; z += 2 )
					corners.Add( new Vector3( x * h, y * h, z * h ) );
				probe.AddHullShape( Vector3.Zero, Rotation.Identity, corners, rebuildMass: false );
			}

			var at = new Transform( centre );

			// The backstop's primitive, in the backstop's direction (world body queried, probe passed in).
			bool overlap = hitBody.CheckOverlap( probe, at );
			Verdict( $"{kind}: CheckOverlap (backstop)", overlap );

			// The live resolve's primitive, in the resolve's direction.
			bool pen = hitBody.ComputePenetration( probe, at, out var dir, out var dist );
			Verdict( $"{kind}: ComputePenetration (resolve) — dir {dir}, dist {dist:0.##}", pen );

			if ( pen )
			{
				// Contract check: dir·dist moves the WORLD body clear, so the probe moves the opposite way.
				var corrected = new Transform( centre - dir * (dist + 0.5f) );
				bool separated = !hitBody.CheckOverlap( probe, corrected );
				Verdict( $"{kind}: translating by the MTV separates", separated );
			}

			// The sweep phase's primitive: a real travel sweep from clear air into the surface, approaching
			// against the hit normal so it works on walls and ceilings, not just floors.
			var sweep = ClampFilter( scene ).Sweep( probe, new Transform( centre + normal * 64f ), at ).Run();
			Verdict( $"{kind}: travel Sweep hits on approach — hit={sweep.Hit}, frac {sweep.Fraction:0.##}", sweep.Hit );

			// The documented-unreliable one, for the record: zero-length body sweep as a pure overlap test.
			var zeroBody = ClampFilter( scene ).Sweep( probe, at, at ).Run();
			Log.Info( $"  (info) {kind}: zero-length body Sweep StartedSolid = {zeroBody.StartedSolid}" );

			if ( sphere )
			{
				// The engine CharacterController's own start-solid pattern — the likely fallback overlap test.
				var zeroShape = ClampFilter( scene ).Sphere( ProbeRadius, centre, centre ).Run();
				Verdict( "sphere: zero-length shape trace StartedSolid", zeroShape.StartedSolid );
			}
		}
		finally
		{
			if ( probe.IsValid() )
				probe.Remove();
		}
	}

	// Same world filter as BrushWorldClamp.Filtered, minus the target hierarchy (there's no edit session here).
	static SceneTrace ClampFilter( Scene scene ) => scene.Trace
		.WithoutTags( HiderController.PropBodyTag, "movecollider", "headcollider", "trigger", "water" );

	static string Describe( PhysicsBody body )
	{
		int total = 0, mesh = 0, trigger = 0;
		foreach ( var s in body.Shapes )
		{
			total++;
			if ( s.IsMeshShape ) mesh++;
			if ( s.IsTrigger ) trigger++;
		}
		return $"{total} ({mesh} mesh, {trigger} trigger)";
	}

	static void Verdict( string label, bool pass ) =>
		Log.Info( $"  [{(pass ? "PASS" : "FAIL")}] {label}" );
}
