using Sandbox.Movement;

namespace Mimiclay;

/// <summary>
/// The hunter's move mode while it walks in the SIM world (HunterController.Sim, mimi_sim_hunter): the sim moves the body —
/// host-authoritative, its owner predicting it — and every machine draws the pawn from there. The stock
/// <see cref="PlayerController"/> stays for everything that isn't moving: look, the eye, the ground it reads off the drawn
/// pose (IsOnGround, for footsteps and the landing event), the colliders bullets hit. This mode is that controller moving
/// NOTHING: chosen over the walk and the climb (top score) for as long as the hunter is a sim body, adding no velocity and
/// never stepping or regrounding (those are the walk mode's). The controller's own input pass is off meanwhile
/// (HunterController keeps UseInputControls false), so its jump and duck never touch the body either.
/// </summary>
[Title( "MoveMode - Hunter Sim" ), Group( "Movement" ), Icon( "hub" )]
public sealed class HunterSimMode : MoveMode
{
	HunterController _hunter;
	HunterController Hunter => _hunter.IsValid() ? _hunter : (_hunter = Components.Get<HunterController>());

	public override bool AllowGrounding => true;
	public override bool AllowFalling => true;

	public override int Score( PlayerController controller ) => Hunter.IsValid() && Hunter.SimWalking ? 100000 : -100000;

	public override void AddVelocity() { }
	public override void PrePhysicsStep() { }
	public override void PostPhysicsStep() { }

	public override bool IsStandableSurface( in SceneTraceResult result )
		=> Vector3.GetAngle( Vector3.Up, result.Normal ) <= (Components.Get<MoveModeWalk>() is { } walk && walk.IsValid() ? walk.GroundAngle : 45f);
}
