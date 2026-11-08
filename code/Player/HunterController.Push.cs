namespace Mimiclay;

// A hunter walking into a player's prop shoves it (the prop is a physics body on its wearer's machine); walking into a
// still decoy now pushes it too, so body-checking props tells a hunter nothing (see RoundManager.NoteDecoyPush). A pawn
// the sim world owns (a loose or walking prop — HiderController.Sim) is keyframed in the scene, so the push is reported
// to the sim the same way (HiderController.NoteSimPush). Only the machine driving this hunter reports — its own body's
// contacts. While the hunter walks in the sim (mimi_sim_hunter) none of this runs: the sim pushes what it walks into itself
// (HunterController.Sim, SimPushAhead).
public sealed partial class HunterController : Component.ICollisionListener
{
	void Component.ICollisionListener.OnCollisionStart( Collision collision ) => NotePush( collision );
	void Component.ICollisionListener.OnCollisionUpdate( Collision collision ) => NotePush( collision );

	// The controller's WISH, not its velocity: pressed against a decoy, the velocity is already zero.
	void NotePush( in Collision collision )
	{
		if ( !Owned || !_controller.IsValid() || SimWalking )
			return;
		RoundManager.NoteDecoyPush( collision, _controller.WishVelocity );
		HiderController.NoteSimPush( collision, _controller.WishVelocity );
	}
}
