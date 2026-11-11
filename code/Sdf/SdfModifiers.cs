namespace Mimiclay;

/// <summary>
/// Stack modifiers (<see cref="SdfShape.Smooth"/>): brushes with no geometry that reshape the brushes UNDERNEATH
/// them (earlier in the list — the layer stack draws the last brush on top). Resolved here on the CPU, never in a
/// shader: <see cref="Apply"/> walks the list top-down and stamps each brush's <see cref="SdfBrush.SmoothFloor"/>,
/// which <see cref="SdfBrush.EffectiveBlend"/>/<see cref="SdfBrush.EffectiveRounding"/> fold in. Every field
/// evaluator (the GPU packer, CPU sample/surface/bounds, collision) calls it at its entry, so the smoothed field
/// is identical everywhere and costs the march nothing.
///
/// Smoothing the field under a modifier = every join below it blends by at least the Smooth amount, and every
/// shape below it rounds its edges by at least that much (capped per shape). Modifiers stack by max. Colour
/// brushes are paint, not geometry, so their feather is left alone.
/// </summary>
public static class SdfModifiers
{
	/// <summary>Stamp the smoothing floors for this list. Cheap (one pass, no allocation) and idempotent, so
	/// evaluators call it unconditionally. Needs no hash of its own: the floors derive entirely from the
	/// modifiers' Blend / Enabled / list order, all of which the list-level content hashes already mix.</summary>
	public static void Apply( List<SdfBrush> brushes )
	{
		if ( brushes is null )
			return;

		float floor = 0f;
		for ( int i = brushes.Count - 1; i >= 0; i-- )
		{
			var b = brushes[i];
			if ( b is null )
				continue;

			if ( b.IsModifier )
			{
				if ( b.Enabled && b.Blend > floor )
					floor = b.Blend;
				b.SmoothFloor = 0f;
				continue;
			}

			float f = b.Operation == SdfOperation.Colour ? 0f : floor;
			b.SmoothFloor = f;

			// Group members are what the evaluators actually fold in — they take the group's floor. Written only
			// on change: a worker snapshot shares the member list with the live brush (see SdfBrush.Copy).
			if ( b.Shape == SdfShape.Group )
			{
				foreach ( var m in b.Members() )
				{
					float mf = m.Operation == SdfOperation.Colour ? 0f : f;
					if ( m.SmoothFloor != mf )
						m.SmoothFloor = mf;
				}
			}
		}
	}

	/// <summary>A fresh modifier brush of <paramref name="shape"/>. Paint-only op so every solid-only consumer
	/// skips it; sits at the sculpture origin (it has no transform of its own) so a bounds consumer that forgets
	/// to skip it can't drag the box anywhere odd.</summary>
	public static SdfBrush Create( SdfShape shape, float amount = 6f ) => new()
	{
		Shape = shape,
		Operation = SdfOperation.Colour,
		Position = Vector3.Zero,
		Rotation = Rotation.Identity,
		Size = new Vector3( 1f ),
		Blend = amount,
		Rounding = 0f,
	};
}
