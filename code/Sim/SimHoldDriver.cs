namespace Mimiclay.Sim;

/// <summary>
/// Phase 3 of the rollback-physics integration: a prop in someone's hands is a sim body driven by its HOLDER's input —
/// the root pose they want it at (HunterController.Grab computes it exactly as before: the aim, the sweep, the slide and
/// press), carried in every tick's <see cref="SimInput"/> under <see cref="SimInput.Hold"/>. The holder predicts the hold,
/// the host simulates it with the same code, everyone else shows it from the host's poses (SimWorld.RemotePose): no
/// lend, no ownership change, no hand-back. The chase is HeldBodyDriver's velocity chase (<see cref="HeldBodyDriver.Chase"/>:
/// about the holder's GRIP, acceleration-limited) — a joint can't be rolled back. Held, a body has no gravity (its spec); it
/// keeps its friction unless mimi_hold_friction is off (SimWorld.BuildShapes).
///
/// Let go — the holder's inputs stop carrying a target — it flies on under gravity at once, here and on the host, until
/// the host registers it as a loose body with that motion (HiderController.UpdateSimRegistration): a drop and a throw
/// need no round trip either.
/// </summary>
public sealed class SimHoldDriver : ISimDriver
{
	public const byte Id = 3;
	public static readonly SimHoldDriver Instance = new();

	// HeldBodyDriver's chase: pull toward the target (1/s), turn rate, and the speed cap that keeps a fast swing from
	// tunnelling the body through a thin wall. 1/Dt = 50 > Pull, so the per-frame minimum there never applies here.
	public const float Pull = 18f;
	public const float Turn = 14f;
	public const float MaxSpeed = 1800f;

	public void Drive( SimBody body, in SimInput input, int tick )
	{
		var pb = body.Phys;
		if ( pb is null || !pb.IsValid() )
			return;
		if ( pb.Sleeping )
			pb.Sleeping = false;

		ref var c = ref body.Ctrl;
		if ( input.Has( SimInput.Hold ) )
		{
			c.Holding = true;
			c.Parked = false; // (held again: no longer frozen in place)
			c.Commanded = input.HoldPos - pb.Position; // (the gap to the holder's REAL target - what another machine's copy aims at: Guess)
			c.Grip = input.HoldGrip;
			// The grip (where the holder took it, root-local) chases its place on the target, the body turning about it, within
			// the acceleration limits — HeldBodyDriver.Chase, the same chase as the fallback hold.
			var (linear, angular) = HeldBodyDriver.Chase( pb.MassCenter, pb.Transform.PointToWorld( input.HoldGrip ),
				new Transform( input.HoldPos, input.HoldRot ).PointToWorld( input.HoldGrip ), pb.Rotation, input.HoldRot,
				pb.Velocity, pb.AngularVelocity, SimWorld.Dt );
			pb.Velocity = linear;
			pb.AngularVelocity = angular;
			body.Intent = pb.Velocity.WithZ( 0f ); // a carried prop shoved into an anchored decoy pushes it (DetectAnchoredPushes)
			return;
		}

		body.Intent = Vector3.Zero;
		// No target yet (the host, before the holder's first input lands — half a round trip after the grab): wait where it
		// is. Treating that as a release dropped the prop on the host while the holder lifted it: a 157u correction at
		// every grab start.
		if ( !c.Holding )
		{
			pb.Velocity = Vector3.Zero;
			pb.AngularVelocity = Vector3.Zero;
			return;
		}

		// Let go FROZEN (LMB — the release tick's input carries Freeze): parked exactly where it is, from that tick on, here and
		// on the host, until the host takes it out of the sim as frozen scenery (HiderController.UpdateSimRegistration waits for
		// this tick). Before, the holder's prediction let it go loose and it fell for the round trip until the freeze landed.
		if ( input.Has( SimInput.Freeze ) )
			c.Parked = true;
		if ( c.Parked )
		{
			body.HoldReleased = true;
			pb.Velocity = Vector3.Zero;
			pb.AngularVelocity = Vector3.Zero;
			return;
		}

		// Let go: gravity by hand (the spec has it off while held) until the host makes it a loose body — from THIS tick
		// (HoldReleased; HiderController.UpdateSimRegistration waits for it), the same tick the holder's own prediction
		// stops holding (SimWorld.PredictRelease).
		body.HoldReleased = true;
		pb.Velocity += pb.World.Gravity * SimWorld.Dt;
	}

	// Someone else's hold: assume it keeps moving as the host last had it — a target far enough ahead that the chase
	// reproduces that velocity (Pull × the gap). Parking it at the last pose instead left it hovering for the half round
	// trip after its holder let go while the real prop flew on. (Its sim copy here is nudged to the host's state every
	// snapshot anyway — SimWorld.NudgeRemote — and drawn from the host's poses.)
	//
	// The target is the holder's REAL one as of the snapshot (P + the gap the host stored, SimCtrl.Commanded) - not P + V / Pull,
	// which equals it only while the prop is freely chasing its target. Pressed on (a player walking into a held prop), the
	// prop's velocity is the contact's back-and-forth, and a copy aiming along it drifted the wrong way for a whole round
	// trip: the player pushing it was corrected every snapshot - their camera shook, so the held prop looked like it was
	// jittering (user, 2026-11-07, Tools/SimLab held: the pusher's worst-1% speed changes 3-4x the host's).
	// Only while the prop is held nearly still, though: in a swing or a throw the target keeps moving, and P + V / Pull carries
	// that motion on where the snapshot's target stops dead - aimed at it, a thrown player launched ~23u off on every other
	// machine (Tools/SimLab throwplayer). Slow = being held or pressed on; fast = carried along.
	const float SlowHold = 150f;
	public SimInput Guess( SimBody body, in SimBodyState latest ) => latest.HasCtrl && latest.Ctrl.Parked ? SimInput.None : new()
	{
		Buttons = SimInput.Hold,
		HoldPos = latest.HasCtrl && latest.Ctrl.Holding && latest.V.Length < SlowHold ? latest.P + latest.Ctrl.Commanded : latest.P + latest.V / Pull,
		HoldRot = latest.R,
		HoldGrip = latest.HasCtrl ? latest.Ctrl.Grip : Vector3.Zero,
	};
}
