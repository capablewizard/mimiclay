using System;

namespace Mimiclay;

/// <summary>
/// Pulses every <see cref="SdfHighlightOutline"/> under this GameObject with a flash look — the Reveal
/// "show them off" beacon on surviving prop pawns. Authored DISABLED on the pawn prefab;
/// <see cref="RoundOutlineSystem"/> enables it per machine while the Reveal is on and disables it after
/// (the same per-frame assertion it uses for outline visibility).
///
/// While enabled the flash look REPLACES the authored look outright: a raised-cosine pulse of the
/// flash colours' OPACITY, from full at each peak down to <see cref="MinBlend"/> at each trough
/// (0 = fades fully out). It never blends with the outline's authored [Property] colours (an early
/// version did, and the prop read as flashing red→teal as each trough fell back to the disguise's
/// owner-locator glow — the pulse must always be flash-colour↔transparent).
///
/// It writes the outlines' runtime OVERRIDE slots each frame
/// (<see cref="SdfHighlightOutline.ColorOverride"/> et al.) — never their authored [Property] values,
/// which a network spawn/refresh would bake into proxies (snapshot-carries-live-state). Disabling
/// clears the overrides: an exact restore with no save/restore bookkeeping.
/// </summary>
[Title( "SDF Outline Flash" )]
[Category( "SDF" )]
[Icon( "flare" )]
public sealed class SdfOutlineFlash : Component
{
	/// <summary>Outline colour while the flash is ON, where the silhouette is directly visible.</summary>
	[Property] public Color FlashColor { get; set; } = new Color( 1f, 0f, 0f );

	/// <summary>Outline colour while ON where the silhouette is behind something (through-wall glow).</summary>
	[Property] public Color FlashObscuredColor { get; set; } = new Color( 1f, 0f, 0f, 0.5f );

	/// <summary>Fill over the visible surface while ON. Transparent = outline only.</summary>
	[Property] public Color FlashInsideColor { get; set; } = new Color( 1f, 0f, 0f, 0.5f );

	/// <summary>Through-wall fill while ON.</summary>
	[Property] public Color FlashInsideObscuredColor { get; set; } = new Color( 1f, 0f, 0f, 0.5f );

	/// <summary>Outline width (screen px) while ON — authored width is often 0 (fill-only), so this is
	/// what makes the flash read as a line. 0 = fill-only flash.</summary>
	[Property, Range( 0f, 16f )] public float FlashWidth { get; set; } = 0f;

	/// <summary>Pulses per second.</summary>
	[Property, Range( 0.1f, 5f )] public float Frequency { get; set; } = 0.75f;

	/// <summary>Pulse floor, 0..1: the fraction of the flash opacity kept at each trough.
	/// 0 = fades fully to transparent; raise it so the flash never quite disappears.</summary>
	[Property, Range( 0f, 1f )] public float MinBlend { get; set; } = 0.15f;

	float _enabledAt;

	protected override void OnEnabled() => _enabledAt = Time.Now;

	protected override void OnUpdate()
	{
		// Raised cosine starting AT the peak, so the flash lands the instant the phase flips, then
		// breathes down toward transparent and back. t scales the flash colours' ALPHA only — the
		// hue never moves, so the pulse reads as one colour fading, not a colour cycle.
		float wave = (1f + MathF.Cos( (Time.Now - _enabledAt) * Frequency * MathF.Tau )) * 0.5f;
		float t = MinBlend + (1f - MinBlend) * wave;

		foreach ( var o in Components.GetAll<SdfHighlightOutline>( FindMode.EnabledInSelfAndDescendants ) )
		{
			o.ColorOverride = FlashColor.WithAlpha( FlashColor.a * t );
			o.ObscuredColorOverride = FlashObscuredColor.WithAlpha( FlashObscuredColor.a * t );
			o.InsideColorOverride = FlashInsideColor.WithAlpha( FlashInsideColor.a * t );
			o.InsideObscuredColorOverride = FlashInsideObscuredColor.WithAlpha( FlashInsideObscuredColor.a * t );
			o.WidthOverride = FlashWidth;
		}
	}

	protected override void OnDisabled()
	{
		// EverythingIn…: also reach outlines that were disabled mid-flash, so no override sticks.
		foreach ( var o in Components.GetAll<SdfHighlightOutline>( FindMode.EverythingInSelfAndDescendants ) )
		{
			o.ColorOverride = null;
			o.ObscuredColorOverride = null;
			o.InsideColorOverride = null;
			o.InsideObscuredColorOverride = null;
			o.WidthOverride = null;
		}
	}
}
