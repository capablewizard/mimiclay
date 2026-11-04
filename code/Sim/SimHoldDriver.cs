namespace Mimiclay.Sim;

/// <summary>
/// Phase 3 of the rollback-physics integration: a prop in someone's hands is a sim body driven by its HOLDER's input —
/// the root pose they want it at (HunterController.Grab computes it exactly as before: the aim, the sweep, the slide and
/// press), carried in every tick's <see cref="SimInput"/> under <see cref="SimInput.Hold"/>. The holder predicts the hold,
/// the host simulates it with the same code, everyone else shows it from the host's poses (SimWorld.RemotePose): no
/// lend, no ownership change, no hand-back. The chase is HeldBodyDriver's velocity chase (its contact mode) — a joint
/// can't be rolled back. Held, a body has no gravity (its spec) and no friction (SimWorld.BuildShapes).
///
/// Let go — the holder's inputs stop carrying a target — it flies on under gravity at once, here and on the host, until
/// the host registers it as a loose body with that motion (HiderController.UpdateSimRegistration): a drop and a throw
/// need no round trip either.
/// </summary>
public sealed class SimHoldDriver : ISimDriver
{
	public const byte Id = 3;

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
			pb.Velocity = ((input.HoldPos - pb.Position) * Pull).ClampLength( MaxSpeed );
			pb.AngularVelocity = HeldBodyDriver.TurnToward( pb.Rotation, input.HoldRot ) * Turn;
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
	public SimInput Guess( SimBody body, in SimBodyState latest ) => new() { Buttons = SimInput.Hold, HoldPos = latest.P + latest.V / Pull, HoldRot = latest.R };
}
