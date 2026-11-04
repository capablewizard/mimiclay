namespace Mimiclay;

// A hunter walking into a player's prop shoves it (the prop is a physics body on its wearer's machine); walking into a
// still decoy now pushes it too, so body-checking props tells a hunter nothing (see RoundManager.NoteDecoyPush). Only
// the machine driving this hunter reports — its own body's contacts.
public sealed partial class HunterController : Component.ICollisionListener
{
	void Component.ICollisionListener.OnCollisionStart( Collision collision ) => NotePush( collision );
	void Component.ICollisionListener.OnCollisionUpdate( Collision collision ) => NotePush( collision );

	// The controller's WISH, not its velocity: pressed against a decoy, the velocity is already zero.
	void NotePush( in Collision collision )
	{
		if ( Owned && _controller.IsValid() )
			RoundManager.NoteDecoyPush( collision, _controller.WishVelocity );
	}
}
