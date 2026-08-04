using System;

namespace Mimiclay;

/// <summary>
/// Pulses every <see cref="SdfHighlightOutline"/> under this GameObject between its authored look and
/// a flash look — the Reveal "show them off" beacon on surviving prop pawns. Authored DISABLED on the
/// pawn prefab; <see cref="RoundOutlineSystem"/> enables it per machine while the Reveal is on and
/// disables it after (the same per-frame assertion it uses for outline visibility).
///
/// While enabled it writes the outlines' runtime OVERRIDE slots each frame
/// (<see cref="SdfHighlightOutline.ColorOverride"/> et al.) — never their authored [Property] values,
/// which a network spawn/refresh would bake into proxies (snapshot-carries-live-state). Disabling
/// clears the overrides: an exact restore with no save/restore bookkeeping. The pulse blends FROM the
/// authored look TO the flash look, starting at the authored end, so toggling on/off can never pop.
/// </summary>
[Title( "SDF Outline Flash" )]
[Category( "SDF" )]
[Icon( "flare" )]
public sealed class SdfOutlineFlash : Component
{
	/// <summary>Outline colour at the pulse peak, where the silhouette is directly visible.</summary>
	[Property] public Color FlashColor { get; set; } = new Color( 1f, 0.85f, 0.3f );

	/// <summary>Outline colour at the pulse peak where the silhouette is behind something (through-wall glow).</summary>
	[Property] public Color FlashObscuredColor { get; set; } = new Color( 1f, 0.85f, 0.3f, 0.8f );

	/// <summary>Fill over the visible surface at the pulse peak. Transparent = outline only.</summary>
	[Property] public Color FlashInsideColor { get; set; } = Color.Transparent;

	/// <summary>Through-wall fill at the pulse peak.</summary>
	[Property] public Color FlashInsideObscuredColor { get; set; } = new Color( 1f, 0.85f, 0.3f, 0.35f );

	/// <summary>Outline width (screen px) at the pulse peak — authored width is often 0 (fill-only), so
	/// this is what makes the flash read as a line.</summary>
	[Property, Range( 0f, 16f )] public float FlashWidth { get; set; } = 5f;

	/// <summary>Pulses per second.</summary>
	[Property, Range( 0.1f, 5f )] public float Frequency { get; set; } = 1.25f;

	/// <summary>Pulse floor, 0..1: how far back toward the authored look each trough falls.
	/// 0 = fully back to authored; raise it to keep the flash look partially held between peaks.</summary>
	[Property, Range( 0f, 1f )] public float MinBlend { get; set; } = 0.15f;

	float _enabledAt;

	protected override void OnEnabled() => _enabledAt = Time.Now;

	protected override void OnUpdate()
	{
		// Raised-cosine pulse starting at 0 (the authored look) the moment we're enabled.
		float wave = (1f - MathF.Cos( (Time.Now - _enabledAt) * Frequency * MathF.Tau )) * 0.5f;
		float t = MinBlend + (1f - MinBlend) * wave;

		foreach ( var o in Components.GetAll<SdfHighlightOutline>( FindMode.EnabledInSelfAndDescendants ) )
		{
			o.ColorOverride = Color.Lerp( o.Color, FlashColor, t );
			o.ObscuredColorOverride = Color.Lerp( o.ObscuredColor, FlashObscuredColor, t );
			o.InsideColorOverride = Color.Lerp( o.InsideColor, FlashInsideColor, t );
			o.InsideObscuredColorOverride = Color.Lerp( o.InsideObscuredColor, FlashInsideObscuredColor, t );
			o.WidthOverride = MathX.Lerp( o.Width, FlashWidth, t );
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
