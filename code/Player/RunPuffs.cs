using System;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Puffs that trail something moving fast — the hunter's run dust and a free-cam ghost's wake, both a "RunPFX" particle
/// effect. Only the EMITTERS are toggled (the effect itself stays enabled), so live puffs finish their lifetime and
/// fade instead of popping. The owner feeds it a position every frame; it measures the speed itself (its own tracker,
/// so a running jump keeps puffing) and switches on at <c>onAt</c>, off below <c>offAt</c> — hysteresis, so a speed
/// hovering at the threshold doesn't flicker. Purely local: every machine runs it from the movement it sees.
/// </summary>
public sealed class RunPuffs
{
	readonly ParticleEmitter[] _emitters;
	Vector3 _from;
	bool _seeded;
	bool _on;

	/// <summary>The effect's model renderers (the hunter flips its own to shadows-only in first person).</summary>
	public ParticleModelRenderer[] Renderers { get; }

	/// <summary>Are the emitters running?</summary>
	public bool Emitting => _on;

	/// <param name="effect">The particle effect's GameObject; null/invalid makes this a no-op.</param>
	public RunPuffs( GameObject effect )
	{
		// EverythingInSelf so a re-cache still finds them after we've disabled them. They start enabled in the
		// prefabs, so force the initial off state — the owner spawns standing still.
		_emitters = effect.IsValid()
			? effect.Components.GetAll<ParticleEmitter>( FindMode.EverythingInSelfAndDescendants ).ToArray()
			: Array.Empty<ParticleEmitter>();
		Renderers = effect.IsValid()
			? effect.Components.GetAll<ParticleModelRenderer>( FindMode.EverythingInSelfAndDescendants ).ToArray()
			: Array.Empty<ParticleModelRenderer>();

		foreach ( var e in _emitters )
			e.Enabled = false;
	}

	/// <summary>Advance one frame at <paramref name="position"/>. <paramref name="horizontal"/> measures ground speed
	/// only (a walker — falling isn't running); a flyer passes false so climbing and diving puff too.
	/// <paramref name="allowed"/> false holds the emitters off whatever the speed.</summary>
	public void Update( Vector3 position, float onAt, float offAt, bool horizontal = true, bool allowed = true )
	{
		if ( _emitters.Length == 0 )
			return;

		if ( !_seeded )
		{
			_from = position;
			_seeded = true;
			return;
		}

		var moved = position - _from;
		if ( horizontal )
			moved = moved.WithZ( 0f );
		float speed = Time.Delta > 0f ? moved.Length / Time.Delta : 0f;
		_from = position;

		_on = allowed && (_on ? speed > offAt : speed >= onAt);

		foreach ( var e in _emitters )
		{
			if ( e.IsValid() )
				e.Enabled = _on;
		}
	}
}
