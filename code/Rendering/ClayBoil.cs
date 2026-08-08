using System;

namespace Mimiclay;

/// <summary>
/// Claymation "boil" for ONE prop — the stop-motion wobble that stops its plasticine surface sitting
/// perfectly still. Add this alongside an <see cref="SdfRaymarchRenderer"/> on the same GameObject;
/// props without one don't boil at all.
///
/// Deliberately opt-in per prop rather than scene-wide: a whole room boiling at once reads as the
/// picture being unstable rather than as the props being handmade, so this is for the few things
/// meant to draw the eye.
///
/// The mechanism: each tick, the displacement noise (and optionally the surface maps) are sampled at
/// a slightly different offset, held for the whole tick, then jumped. It rides the existing
/// displacement, so it only does anything on a renderer with Displace on — <see cref="TextureJitter"/>
/// is the exception and works regardless, since it shifts the triplanar maps rather than the field.
///
/// Disabling the component turns the boil off (the lookup is enabled-only), so it doubles as a toggle.
/// </summary>
[Title( "Clay Boil" )]
[Category( "SDF" )]
[Icon( "blur_on" )]
public sealed class ClayBoil : Component
{
	/// <summary>Ticks per second — how often the surface re-forms. 12 would be "on twos" at a 24fps
	/// film rate; the default is deliberately slower than that, which reads as a hand-made budget
	/// stop-motion and leaves each pose on screen long enough to be seen as a pose. Past ~16 it stops
	/// reading as animation and starts reading as shimmer. 0 disables the boil entirely.
	///
	/// This is also the prop's field re-BAKE rate: each tick re-dispatches the GPU field eval with the
	/// new offsets (the lumps live in the baked field, not in the march), so the boil costs a few
	/// compute dispatches per second — the same work a brush edit already does — instead of per-pixel
	/// noise every frame.</summary>
	[Property, Range( 0f, 30f ), Group( "Boil" )] public float Fps { get; set; } = 4f;

	/// <summary>How far the noise jumps each tick, in noise CELLS (so it's independent of the
	/// material's DispFreq — retuning lump density won't retune the boil).
	///
	/// Sizing this by eye is a trap — work back from pixels. Surface movement per tick is roughly
	/// 2·DispAmp·(0.28·Jitter), so at the stock plasticine (amp 1.357) Jitter 0.1 buys ~0.08" of
	/// travel, which is about ONE pixel at normal viewing distance: mathematically present, visually
	/// nothing. ~0.35 gives 4–5px, which is where it starts to read as animation.</summary>
	[Property, Range( 0f, 1f ), Group( "Boil" )] public float Jitter { get; set; } = 0.4f;

	/// <summary>Per-tick lump-depth wobble, as a fraction of the material's DispAmp — the surface
	/// reading deeper/shallower frame to frame.
	///
	/// Reads STRONGER per unit than <see cref="Jitter"/>: it scales every point of the surface the
	/// same way, so the movement adds up coherently instead of partly cancelling the way a directional
	/// noise offset does. The default is the top of the range (+/-50% depth per tick), which lands
	/// well because the slow <see cref="Fps"/> gives each depth time to register as a pose.
	///
	/// Cost: the march's step-safety bound is sized from the WORST tick (amp × (1 + AmpJitter/2)),
	/// so at 1.0 this prop understeps at 1.5× the factor it would otherwise need. Since the lumps
	/// are baked into the field that is the whole cost — the noise itself is paid at bake, not per
	/// march step.</summary>
	[Property, Range( 0f, 1f ), Group( "Boil" )] public float AmpJitter { get; set; } = 1f;

	/// <summary>Per-tick shift of the triplanar surface maps, in texture REPEATS — the one that makes
	/// the fingerprints and fine imperfections change rather than the silhouette. Works even with the
	/// renderer's Displace off, since it shifts the maps rather than the field.
	///
	/// Two regimes. Well under 1 repeat the texture SLIDES: detail keeps its identity and crawls.
	/// Around/above 1 it lands on uncorrelated texels and genuinely RE-FORMS, which is what really
	/// happens between stop-motion frames — but full re-rolls fizz and give TAA nothing to track. The
	/// default sits FAR into the slide regime (a fiftieth of a repeat): in practice the fine detail
	/// wants only the faintest crawl. Treat 0.25+ as a stylistic choice, not a starting point.
	///
	/// Unlike the other dials this is free — applied once at the hit point during shading, not inside
	/// the march loop, so it costs no steps.</summary>
	[Property, Range( 0f, 2f ), Group( "Boil" )] public float TextureJitter { get; set; } = 0.02f;

	/// <summary>Write this prop's boil onto the raymarcher's scene-object attributes. These feed the
	/// live texture jitter and the analytic fallback; the displacement boil itself is baked into the
	/// distance field per tick (the renderer folds the tick into its field hash).</summary>
	public void Apply( RenderAttributes a )
	{
		a.Set( "BoilFps", MathF.Max( Fps, 0f ) );
		a.Set( "BoilJitter", MathF.Max( Jitter, 0f ) );
		a.Set( "BoilAmpJitter", MathF.Max( AmpJitter, 0f ) );
		a.Set( "BoilTexJitter", MathF.Max( TextureJitter, 0f ) );
	}

	/// <summary>Write "no boil" onto a scene object's attributes. Must be called for props WITHOUT a
	/// ClayBoil, not just skipped: attributes persist on the scene object, so a prop that had the
	/// component removed (or disabled) at runtime would otherwise keep boiling on the last values it
	/// was given. Zeroing Fps alone would do it — the shader short-circuits on it — but the rest are
	/// zeroed too so a stale value can't leak into the march's step-safety factor.</summary>
	public static void ApplyOff( RenderAttributes a )
	{
		a.Set( "BoilFps", 0f );
		a.Set( "BoilJitter", 0f );
		a.Set( "BoilAmpJitter", 0f );
		a.Set( "BoilTexJitter", 0f );
	}
}
