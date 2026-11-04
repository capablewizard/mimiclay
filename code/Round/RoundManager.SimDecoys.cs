using System;
using System.Linq;
using Mimiclay.Sim;

namespace Mimiclay;

// Phase 1 of the rollback-physics integration (networked-physics-authority memory): prop-hunt DECOYS live in the shared
// simulation world (SimWorld) from the moment the Hunt starts — host-authoritative, every machine predicting, each one's
// scene object shown from it (SimBodyFlags.Present). Players' props, hunters and everything else that moves are
// followers there (SimWorld.Followers), so walking into a decoy pushes it in the sim, mass for mass, on every machine
// the same way — no more push RPC (NoteDecoyPush) or host pose stream (ShotProps) for them.
//
//  • Registered asleep and UPRIGHT (a push slides it without toppling it, as a pushed player's prop walks). A shot
//    frees the rotation for good and knocks it in the sim.
//  • A grabbed decoy LEAVES the sim for the hold (the existing lend path, RoundManager.Grab — Phase 3 brings the hold
//    in) and comes back where it's put down, carrying its motion.
//  • A split-off piece joins with the motion it breaks off with; a decoy shot away leaves.
//
// mimi_sim_decoys 0 = the old behaviour (on-demand shot bodies, pose stream, push RPC), for A/B. Read once per decoy —
// switching mid-round affects only decoys registered after.
public sealed partial class RoundManager
{
	[ConVar( "mimi_sim_decoys", Help = "Prop hunt: decoys are simulated in the shared sim world from the Hunt on (1), or the old way (0)." )]
	public static bool SimDecoys { get; set; } = true;

	SimWorld Sim => Scene.GetSystem<SimWorld>();

	// Host: decoys went into the sim this round (so ones a grab borrowed go back in when put down).
	bool _simDecoysActive;

	/// <summary>Any machine: is this decoy currently simulated in the sim world?</summary>
	internal bool DecoyInSim( GameObject go )
		=> go.IsValid() && Sim is { Active: true } sim && sim.Find( go.Id ) is { } b && b.Spec.Shape == SimShapeKind.FromGameObject;

	// Host, as the Hunt starts: every decoy that isn't busy (held, lent) joins, asleep where it stands.
	void RegisterSimDecoys()
	{
		if ( !SimDecoys || !IsHostAuthority )
			return;
		_simDecoysActive = true;
		var sim = Sim;
		sim.AnchoredPushed -= OnAnchoredDecoyPushed;
		sim.AnchoredPushed += OnAnchoredDecoyPushed;
		int n = 0;
		foreach ( var sculpt in Scene.GetAllComponents<SdfSculpture>().ToList() )
		{
			if ( !IsShotDecoy( sculpt ) || IsLentDecoy( sculpt.GameObject ) || PropClaims.IsHeldObject( sculpt.GameObject ) )
				continue;
			// ANCHORED, as decoys always were until something moved them: a magnet on the fridge, a knob on a cabinet door
			// stays put until it's shot or pushed (without it, 35 of devprophunt's 70 fell off at the first disturbance).
			if ( RegisterSimDecoy( sculpt.GameObject, awake: false, anchored: true ) is not null )
				n++;
		}
		Log.Info( $"RoundManager: {n} decoys simulated in the sim world" );
	}

	// Host: a knocked sim decoy slid or fell into an anchored one — that's a push too.
	void OnAnchoredDecoyPushed( SimBody body, Vector3 velocity )
	{
		var go = Scene.Directory.FindByGuid( body.Key );
		if ( go.IsValid() )
			PushSimDecoy( go, velocity );
	}

	/// <summary>Host: a push on a sim decoy (a walk or a hunter's intent, see NoteDecoyPush; or a sim body sliding into
	/// it). An anchored one is released — still upright, so it slides like a pushed player's prop — and given the push
	/// (PushTransfer of it, capped); a moving one is kept at least that fast along it. False if it isn't in the sim.</summary>
	bool PushSimDecoy( GameObject go, Vector3 velocity )
	{
		if ( !IsHostAuthority || !DecoyInSim( go ) )
			return false;
		var sim = Sim;
		var b = sim.Find( go.Id );
		if ( (b.Spec.Flags & SimBodyFlags.Anchored) != 0 )
			sim.SetFlags( go.Id, (b.Spec.Flags & ~SimBodyFlags.Anchored) | ((b.Spec.Flags & SimBodyFlags.Upright) != 0 ? SimBodyFlags.NoTurn : 0) ); // slides without turning, as the old push did
		var want = velocity.WithZ( 0f ).ClampLength( PushMaxSpeed ) * PushTransfer;
		var v = b.Phys.Velocity;
		float along = want.IsNearZeroLength ? 0f : Vector3.Dot( v, want.Normal );
		if ( along < want.Length )
			sim.SetMotion( go.Id, v + want.Normal * (want.Length - along), b.Phys.AngularVelocity );
		return true;
	}

	/// <summary>Host: put one decoy in the sim (no-op if it's there). Its legacy state — a local shot body, the pose
	/// stream — is dropped: the sim moves it from now on.</summary>
	internal SimBody RegisterSimDecoy( GameObject go, bool awake, bool upright = true, bool anchored = false )
	{
		if ( !SimDecoys || !IsHostAuthority || !go.IsValid() )
			return null;
		var collider = go.Components.Get<SdfCollider>();
		if ( !collider.IsValid() || collider.BuildAsTrigger )
			return null;
		if ( !go.WorldScale.AlmostEqual( Vector3.One, 0.001f ) )
		{
			Log.Warning( $"RoundManager: decoy {go.Name} is scaled ({go.WorldScale}) — the sim copies unscaled shapes, leaving it out" );
			return null;
		}

		var sim = Sim;
		if ( sim.Find( go.Id ) is { } existing )
			return existing;

		var legacy = go.Components.Get<Rigidbody>();
		Vector3 v = default, w = default;
		if ( legacy.IsValid() && legacy.MotionEnabled )
		{
			v = legacy.Velocity;
			w = legacy.AngularVelocity;
			legacy.MotionEnabled = false; // keyframed from here: the sim places it
		}
		_shotLoose.Remove( go );
		_shotResting.Remove( go );
		_shotTargets.Remove( go );
		_pushedDecoys.Remove( go );

		var flags = SimBodyFlags.Present | (upright ? SimBodyFlags.Upright : 0) | (awake ? 0 : SimBodyFlags.StartAsleep)
			| (anchored ? SimBodyFlags.Anchored : 0);
		var body = sim.Register( new SimBodySpec
		{
			Key = go.Id, Source = go.Id, Shape = SimShapeKind.FromGameObject, Flags = flags,
			Mass = collider.ClayMass, Pose = new Transform( go.WorldPosition, go.WorldRotation ),
		} );
		if ( awake && (v.Length > 0.01f || w.Length > 0.01f) )
			sim.SetMotion( go.Id, v, w );
		return body;
	}

	/// <summary>Host: take a decoy out of the sim (a grab takes over its motion). It stays exactly where it was.</summary>
	internal void UnregisterSimDecoy( GameObject go )
	{
		if ( !IsHostAuthority || !DecoyInSim( go ) )
			return;
		Sim.Unregister( go.Id );
	}

	/// <summary>Host: a shot on a sim decoy — free its rotation and knock it.</summary>
	bool KnockSimDecoy( GameObject go, Vector3 hit, Vector3 dir )
	{
		if ( !DecoyInSim( go ) )
			return false;
		var sim = Sim;
		var b = sim.Find( go.Id );
		sim.SetFlags( go.Id, b.Spec.Flags & ~(SimBodyFlags.Upright | SimBodyFlags.NoTurn | SimBodyFlags.Anchored) );
		float mass = MathF.Max( b.Phys.Mass, 1f );
		sim.Kick( go.Id, hit, dir.Normal * ShotKnockSpeed * mass );
		return true;
	}
}
