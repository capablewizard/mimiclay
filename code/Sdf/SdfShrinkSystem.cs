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
/// Animation cost: mutating sizes here changes the renderer's brush hash, so the field cache re-dispatches
/// itself every frame the shrink runs (on a prop with an active <see cref="ClayBoil"/> the pose is instead
/// held between boil ticks, so those mutations — and their dispatches — collapse onto the boil's own re-bake
/// frames) — the same per-change GPU eval a local edit drag pays, for one prop at
/// a time (no Previewed events fire, so nothing streams). Suppressing the cache instead (the old approach,
/// borrowed from remote drags) regressed the march to the analytic per-brush path — O(all brushes) per pixel
/// on a dense sculpted head right in the shooter's face, the game's worst perf drop. The final full
/// <c>Rebuild</c> still happens once, when the last shrinking brush is removed: mesh and collider heal
/// together (the field is already current). A <see cref="GameObjectSystem"/> like RoundOutlineSystem —
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

		// Boil-locked heal: while the prop's ClayBoil is ACTIVE (Always mode, or WhileDamaged — which
		// these very shrink brushes turn on), each heal pose is HELD and only re-evaluated on the
		// frame the boil tick rolls (same clock as the renderer's bake tick, floor(Time.Now × Fps)):
		// remodelled clay moves in poses, and the healing crater is part of what's being remodelled,
		// so it must not glide smoothly across a surface that only moves in ticks. The timeline and
		// ease are untouched — ShrinkAge still accumulates every frame — so total heal time is
		// identical; the pose is just sampled at BoilFps. Free perf bonus: brush mutations land on
		// the same frames as the boil's field re-bake, so the heal stops paying its own per-frame
		// dispatch. No boil (or Fps 0 / disabled) = the original smooth per-frame heal.
		// TickAt keeps the hold boundaries on the same grid as the field bake — including an impact
		// burst's spliced-in jolt pose, which counts as a tick roll of its own (the heal jumps the
		// frame the shot lands, then holds with everything else). LockHeal false opts the heal out:
		// smooth per-frame healing even while the surface boils.
		var boil = sculpt.GameObject.Components.Get<ClayBoil>(); // self-only + enabled-only
		bool hold = boil is { Fps: > 0f, Boiling: true, LockHeal: true }
			&& boil.TickAt( Time.Now ) == boil.TickAt( Time.Now - Time.Delta );

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

			if ( hold )
			{
				// Between boil ticks: keep the last-applied pose, including a finished brush — the
				// removal is a visual change too, so it waits for the next tick to land on a pose.
				animating = true;
				continue;
			}

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

		if ( !animating && removed )
		{
			// The LAST shrinking brush just vanished: one full rebuild (mesh + collider — the crater heals
			// physically; the field already tracked every animation frame). Owner republish rides Committed.
			sculpt.Rebuild();

			// If an edit session is mid-edit on this sculpture, keep its selection index in range (tail
			// removals never shift authored indices, so this only fires on a genuinely stale selection).
			var session = SculptEditSession.Current;
			if ( session.IsValid() && session.Target == sculpt && session.Selected >= brushes.Count )
				session.Deselect();
		}
		// removed && animating: the list already shrank, the re-dispatched field shows it instantly; the full
		// rebuild waits until the remaining animations finish (they all end in removal).
	}
}
