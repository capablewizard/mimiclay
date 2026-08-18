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

	/// <summary>Call this to open/close the door. Applies immediately on the calling machine, then replicates
	/// to every other machine via <see cref="Rpc.Broadcast"/>. The broadcast body is guarded by
	/// <see cref="Component.IsProxy"/> so the caller — which already applied the change directly — doesn't
	/// double-apply it again when its own broadcast round-trips back (that guard is the same pattern used by
	/// <c>SdfNetworkSync.Stream</c>; without it the caller's door would rotate twice, e.g. 90° becoming 180°).</summary>
	public void SetOpen( bool open )
	{
		Initialize();
		Apply( open );
		BroadcastOpen( open );
	}

	[Rpc.Broadcast]
	void BroadcastOpen( bool open )
	{
		if ( IsProxy )
			Apply( open );
	}

	void Apply( bool open )
	{
		IsOpen = open;

		GameObject.LocalRotation = open
			? Rotation.FromYaw( OpenAngle ) * _closedRotation
			: _closedRotation;
	}
}
