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

		GameObject.LocalRotation = open
			? Rotation.FromYaw( OpenAngle ) * _closedRotation
			: _closedRotation;
	}
}
