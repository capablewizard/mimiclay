using System;

namespace Mimiclay;

/// <summary>
/// Animates SHRINKING brushes (<see cref="SdfBrush.Shrinks"/> — shot craters healing, or any authored shape
/// flagged to vanish): after a grace period the brush eases down to nothing and is REMOVED from its
/// sculpture, freeing the slot. This is what stops sustained carve fire from ever exhausting a sculpture's
/// brush cap — damage is transient by construction.
///
/// Runs on EVERY machine independently, from each machine's own receive time — no networking. Machines drift
/// by network jitter mid-animation (cosmetic), but all converge on the same final state: brush gone. On a
/// synced disguise the owner's final <c>Rebuild → Committed</c> republish makes the removal durable for late
/// joiners too.
///
/// Animation cost is kept off the field-bake path with the same trick remote drags use: while sizes are
/// animating, the sculpture's field cache is SUPPRESSED (analytic march reads the live brush data — the
/// renderer re-hashes it per frame, so no explicit per-frame rebuild call and no Previewed events, meaning
/// nothing streams). The single full <c>Rebuild</c> happens once, when the last shrinking brush is removed:
/// mesh, field and collider all heal together. A <see cref="GameObjectSystem"/> like RoundOutlineSystem —
/// exists in every scene, no wiring.
/// </summary>
public sealed class SdfShrinkSystem : GameObjectSystem
{
	public SdfShrinkSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Tick, "SdfShrink" );
	}

	void Tick()
	{
		// Play mode only — the editor must never eat authored brushes off a scene being worked on.
		if ( !Game.IsPlaying )
			return;

		foreach ( var sculpt in Scene.GetAllComponents<SdfSculpture>() )
			TickSculpture( sculpt );
	}

	void TickSculpture( SdfSculpture sculpt )
	{
		var brushes = sculpt.Brushes;
		if ( brushes is not { Count: > 0 } )
			return;

		bool animating = false;
		bool removed = false;

		for ( int i = brushes.Count - 1; i >= 0; i-- )
		{
			var b = brushes[i];
			if ( !b.Shrinks )
				continue;

			// Per-brush timing (see SdfBrush.ShrinkDelay/ShrinkDuration — carves randomise these on the
			// shooter's machine so all machines agree per crater).
			b.ShrinkAge += Time.Delta;
			float t = (b.ShrinkAge - MathF.Max( b.ShrinkDelay, 0f )) / MathF.Max( b.ShrinkDuration, 0.05f );

			if ( t <= 0f )
				continue; // grace period — nothing visual yet

			if ( t >= 1f )
			{
				brushes.RemoveAt( i );
				removed = true;
				continue;
			}

			// Organic heal, two tricks on top of the raw timeline:
			//  - smoothstep the ease, so the shrink starts imperceptibly and LANDS gently (a linear ramp
			//    hits zero at full speed — the mechanical look);
			//  - decay the BLEND far slower than the size (k^0.35), so as the bite shrinks it gets
			//    relatively softer — the crisp crater relaxes into a shallow dimple that melts into the
			//    surface like smoothed-over clay, instead of a hard-edged sphere popping to nothing.
			b.ShrinkState ??= (b.Size, b.Blend, b.Rounding);
			var s0 = b.ShrinkState.Value;
			float ease = t * t * (3f - 2f * t);
			float k = 1f - ease;
			b.Size = s0.Size * k;
			b.Blend = s0.Blend * MathF.Pow( k, 0.35f );
			b.Rounding = MathF.Max( 0.05f, s0.Rounding * k );
			animating = true;
		}

		if ( animating )
		{
			// Sizes are moving: the baked field is stale, march analytically off the live brush data.
			SetSuppressed( sculpt, true );
		}
		else if ( removed )
		{
			// The LAST shrinking brush just vanished: one full rebuild (mesh, field, collider — the crater
			// heals physically) and back to the cached-field path. Owner republish rides Committed.
			SetSuppressed( sculpt, false );
			sculpt.Rebuild();

			// If an edit session is mid-edit on this sculpture, keep its selection index in range (tail
			// removals never shift authored indices, so this only fires on a genuinely stale selection).
			var session = SculptEditSession.Current;
			if ( session.IsValid() && session.Target == sculpt && session.Selected >= brushes.Count )
				session.Deselect();
		}
		// removed && animating: the list already shrank, the analytic march shows it instantly; the full
		// rebuild waits until the remaining animations finish (they all end in removal).
	}

	static void SetSuppressed( SdfSculpture sculpt, bool on )
	{
		var rm = sculpt.GameObject.Components.Get<SdfRaymarchRenderer>();
		if ( rm.IsValid() && rm.SuppressFieldCache != on )
			rm.SuppressFieldCache = on;
	}
}
