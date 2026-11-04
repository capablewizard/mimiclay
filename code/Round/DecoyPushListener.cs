namespace Mimiclay;

/// <summary>
/// Runtime-only, beside a decoy's local shot-prop Rigidbody (RoundManager.ShotBody adds it): while THIS machine is
/// simulating the decoy — the host for a loose or held one, a client hunter predicting the one in their hands — its
/// contacts can push the still decoys it runs into, exactly as a player's prop or a hunter does (see
/// RoundManager.NoteDecoyPush). A kinematic copy (just following the host's stream) reports nothing.
/// </summary>
public sealed class DecoyPushListener : Component, Component.ICollisionListener
{
	Rigidbody _body;

	/// <summary>Time since this decoy last touched anything — a held decoy's hold switches from its joint to the velocity
	/// chase while touching (HeldBodyDriver.ContactWindow), exactly as a held player's prop does.</summary>
	public RealTimeSince SinceContact { get; private set; } = 999f;

	void Component.ICollisionListener.OnCollisionStart( Collision collision ) => Note( collision );
	void Component.ICollisionListener.OnCollisionUpdate( Collision collision ) => Note( collision );

	void Note( in Collision collision )
	{
		SinceContact = 0f;
		_body ??= Components.Get<Rigidbody>();
		if ( _body.IsValid() && _body.MotionEnabled )
			RoundManager.NoteDecoyPush( collision, _body.Velocity ); // a held decoy's chase re-aims its velocity every tick
	}
}
