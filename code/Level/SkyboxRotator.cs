using Sandbox;

namespace Mimiclay;

/// <summary>
/// Slowly spins this GameObject about an axis to fake time of day. Put it on the <see cref="SkyBox2D"/> object:
/// the sky draws with that object's world transform, so the painted sky turns, and a <see cref="SunFromSkybox"/>
/// light under it re-aims every frame to follow the painted sun. Baked lighting (DDGI, envmap bakes) doesn't follow.
///
/// The angle is a pure function of time, so it never drifts. The rotation it started from is put back when the
/// spin stops (component disabled, or editor preview switched off), so the saved scene stays un-rotated.
/// </summary>
[Title( "Skybox Rotator" ), Category( "Mimiclay" ), Icon( "360" )]
public sealed class SkyboxRotator : Component, Component.ExecuteInEditor
{
	/// <summary>Axis to spin about, in the parent's space. Up = the sun circles the horizon; tilt it to make the sun rise and set.</summary>
	[Property] public Vector3 Axis { get; set; } = Vector3.Up;

	/// <summary>Real seconds for one full turn. Negative spins the other way.</summary>
	[Property] public float SecondsPerTurn { get; set; } = 600f;

	/// <summary>Extra angle added on top of the starting rotation, degrees. Scrub this to pick a time of day.</summary>
	[Property, Range( 0f, 360f )] public float Offset { get; set; }

	/// <summary>Also spin in the editor (outside play).</summary>
	[Property] public bool PreviewInEditor { get; set; }

	Rotation _base;
	bool _spinning;
	float _startTime;

	bool ShouldSpin => !Scene.IsEditor || PreviewInEditor;

	protected override void OnDisabled() => Stop();

	protected override void OnUpdate()
	{
		if ( !ShouldSpin || Axis.IsNearlyZero() )
		{
			Stop();
			return;
		}

		if ( !_spinning )
		{
			_base = LocalRotation;
			_startTime = Time.Now;
			_spinning = true;
		}

		float turn = SecondsPerTurn.AlmostEqual( 0f ) ? 0f : (Time.Now - _startTime) / SecondsPerTurn;
		LocalRotation = Rotation.FromAxis( Axis.Normal, Offset + turn * 360f ) * _base;
	}

	void Stop()
	{
		if ( !_spinning ) return;
		_spinning = false;
		LocalRotation = _base;
	}
}
