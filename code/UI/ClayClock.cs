using System;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// The claymation heartbeat for code-driven UI wobble: the SAME global grid the 3D clay boils on —
/// <c>floor(Time.Now × Fps)</c>, the normal branch of <see cref="ClayBoil.TickAt"/> — so a UI element
/// posing from here snaps at the exact instants the clay does (a CSS animation can only match the RATE;
/// it free-runs from mount, and the engine rejects the negative-delay trick that would re-phase it).
///
/// <see cref="Pose"/> serves the shared six-pose jitter table (the action-jitter keyframes, the same
/// family every CSS wobble uses), advanced one pose per tick; the seed offsets WHICH pose an element
/// shows, never WHEN it changes — variety without breaking lockstep. Consumers apply it to
/// <c>Style.Transform</c> only when <see cref="Tick"/> changes.
///
/// Divergence accepted by design: a ClayBoil running a non-default Fps, or mid impact-burst, leaves this
/// grid — impacts are local events, and the UI shouldn't flinch with them.
/// </summary>
public static class ClayClock
{
	/// <summary>Poses per second — MUST match the clay's authored heartbeat (ClayBoil.Fps default, 4).
	/// The CSS side encodes the same rate as $wobble-duration (6 poses / 1.5s) — change one, change all.</summary>
	public const float Fps = 4f;

	/// <summary>The current global pose tick.</summary>
	public static int Tick => (int)MathF.Floor( Time.Now * Fps );

	public readonly record struct JitterPose( float Rotation, Vector2 Travel, float Scale );

	// The action-jitter table verbatim (rotation deg, travel px at card scale, scale) — six held poses,
	// same order the keyframes play them.
	static readonly JitterPose[] _poses =
	{
		new( -2.0f, new Vector2( 0f, 0f ), 1f ),
		new( 1.5f, new Vector2( 1f, -2f ), 1f ),
		new( -1.0f, new Vector2( -2f, 1f ), 1f ),
		new( 2.5f, new Vector2( 2f, 1f ), 1.03f ),
		new( -2.5f, new Vector2( -1f, -1f ), 1f ),
		new( 1.0f, new Vector2( 1f, 2f ), 1f ),
	};

	/// <summary>The pose for the CURRENT tick. Seed rotates the table so neighbouring elements hold
	/// different poses while still snapping together.</summary>
	public static JitterPose Pose( int seed = 0 )
	{
		int idx = (Tick + seed) % _poses.Length;
		if ( idx < 0 )
			idx += _poses.Length;
		return _poses[idx];
	}

	/// <summary>The current pose as a ready-to-assign <c>Style.Transform</c>, scaled per element size —
	/// the shared builder every lock-stepped panel uses (bubble letters, hint-icon caps). The scales trim
	/// the card-sized table down: rotation reads at any size, travel wants sub-pixel on small elements.</summary>
	public static PanelTransform BuildPose( int seed, float rotationScale = 1f, float travelScale = 1f, float scalePulse = 1f )
	{
		var pose = Pose( seed );
		float s = 1f + (pose.Scale - 1f) * scalePulse;

		var t = new PanelTransform();
		t.AddRotation( 0f, 0f, pose.Rotation * rotationScale );
		t.AddTranslateX( Length.Pixels( pose.Travel.x * travelScale ) );
		t.AddTranslateY( Length.Pixels( pose.Travel.y * travelScale ) );
		t.AddScale( new Vector3( s, s, 1f ) );
		return t;
	}
}

/// <summary>
/// Poses every child of a row from <see cref="ClayClock"/> — card-scale jitter, each child seeded off its index so
/// neighbours hold different poses while snapping together. Call every frame; it only writes on a new tick, or
/// immediately when the row remounts / changes length. Used by the key-card rows (crosshair hover prompts, the
/// round HUD's controls bar): pose the WRAPPER slots, leaving each card's own transform free.
/// </summary>
public sealed class ClayRowPoser
{
	Panel _panel;
	int _count;
	int _lastTick = int.MinValue;

	/// <param name="row">The row whose children to pose.</param>
	/// <param name="strength">Wobble amount as a fraction of full card-scale (scales rotation, travel and the scale
	/// pulse) — the hover prompts run full, the controls bar a quiet fraction. An argument rather than stored state,
	/// so it can't go stale across a hotload (which keeps a live instance's old field values).</param>
	public void Update( Panel row, float strength = 1f )
	{
		if ( row is not { IsValid: true } )
		{
			_panel = null;
			return;
		}

		var count = row.ChildrenCount;
		if ( row != _panel || count != _count )
		{
			_panel = row;
			_count = count;
			_lastTick = int.MinValue;
		}

		int tick = ClayClock.Tick;
		if ( tick == _lastTick )
			return;
		_lastTick = tick;

		var i = 0;
		foreach ( var child in row.Children )
			child.Style.Transform = ClayClock.BuildPose( i++ * 2, strength, strength, strength );
	}
}
