using System;
using System.Linq;
using Mimiclay.Sim;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// DEBUG: load-tests the shared simulation world (<see cref="SimWorld"/>) at real scale without changing gameplay.
/// <c>mimi_sim_mirror</c> (host) registers every still clay prop in the scene (scene clay / decoys — not pawns) as a
/// sim body copied from its own collider: they exist only in the sim (the scene's props stay exactly as they were —
/// nothing is driven from it), so this measures bandwidth, late joining, rest handling and step cost.
/// <c>mimi_sim_shake [n]</c> (host) knocks n random mirrored bodies into the air. <c>mimi_sim_draw 1</c> shows them.
/// <c>mimi_sim_unmirror</c> removes them.
/// </summary>
public static class SimMirrorProbe
{
	[ConCmd( "mimi_sim_mirror" )]
	public static void Mirror()
	{
		var scene = Game.ActiveScene;
		var sim = scene?.GetSystem<SimWorld>();
		if ( sim is null || !sim.IsAuthority )
		{
			Log.Info( "mimi_sim_mirror: host only, in play" );
			return;
		}

		int n = 0, skipped = 0;
		foreach ( var c in scene.GetAllComponents<SdfCollider>().ToList() )
		{
			if ( !c.IsValid() || c.BuildAsTrigger || c.BuiltModel is null || c.GameObject.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			{
				skipped++;
				continue;
			}
			sim.Register( new SimBodySpec
			{
				Key = c.GameObject.Id, Source = c.GameObject.Id, Shape = SimShapeKind.FromGameObject,
				Mass = c.ClayMass, Flags = SimBodyFlags.StartAsleep, Pose = c.WorldTransform.WithScale( 1f ),
			} );
			n++;
		}
		Log.Info( $"mimi_sim_mirror: registered {n} clay props ({skipped} skipped), sim now {sim.Bodies.Count} bodies" );
	}

	[ConCmd( "mimi_sim_unmirror" )]
	public static void Unmirror()
	{
		var sim = Game.ActiveScene?.GetSystem<SimWorld>();
		if ( sim is null || !sim.IsAuthority )
			return;
		foreach ( var b in sim.Bodies.Where( b => b.Spec.Shape == SimShapeKind.FromGameObject ).ToList() )
			sim.Unregister( b.Key );
	}

	[ConCmd( "mimi_sim_shake" )]
	public static void Shake( int count = 10 )
	{
		var sim = Game.ActiveScene?.GetSystem<SimWorld>();
		if ( sim is null || !sim.IsAuthority )
			return;
		var pool = sim.Bodies.Where( b => b.Spec.Shape == SimShapeKind.FromGameObject && b.ShapesReady ).ToList();
		var rng = new Random();
		foreach ( var b in pool.OrderBy( _ => rng.Next() ).Take( count ) )
		{
			b.Phys.Sleeping = false;
			b.Phys.Velocity = new Vector3( rng.Next( -150, 150 ), rng.Next( -150, 150 ), rng.Next( 250, 450 ) );
			b.Phys.AngularVelocity = new Vector3( rng.Next( -5, 5 ), rng.Next( -5, 5 ), rng.Next( -5, 5 ) );
		}
		Log.Info( $"mimi_sim_shake: knocked {Math.Min( count, pool.Count )} of {pool.Count}" );
	}
}
