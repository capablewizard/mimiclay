namespace Mimiclay;

/// <summary>
/// A door that begins a round either fully closed or fully open.
/// Its authored local rotation is the closed position.
/// </summary>
[Title( "Round Door" )]
[Category( "Mimiclay" )]
[Icon( "door_front" )]
public sealed class RoundDoor : Component
{
	/// <summary>Yaw added to the authored rotation when open.</summary>
	[Property, Range( -180f, 180f )]
	public float OpenAngle { get; set; } = 90f;

	/// <summary>Current open/closed state, kept in sync by <see cref="SetOpen"/>. Doors start closed.</summary>
	public bool IsOpen { get; private set; }

	Rotation _closedRotation;
	bool _initialized;

	protected override void OnStart()
	{
		Initialize();
	}

	void Initialize()
	{
		if ( _initialized )
			return;

		_closedRotation = GameObject.LocalRotation;
		_initialized = true;
	}

	public void SetOpen( bool open )
	{
		Initialize();

		IsOpen = open;

		GameObject.LocalRotation = open
			? Rotation.FromYaw( OpenAngle ) * _closedRotation
			: _closedRotation;
	}
}
