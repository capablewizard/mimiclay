namespace Mimiclay;

/// <summary>
/// Blinks every <see cref="SdfHighlightOutline"/> under this GameObject with a flash look — the Reveal
/// "show them off" beacon on surviving prop pawns. Authored DISABLED on the pawn prefab;
/// <see cref="RoundOutlineSystem"/> enables it per machine while the Reveal is on and disables it after
/// (the same per-frame assertion it uses for outline visibility).
///
/// While enabled the flash look REPLACES the authored look outright: the flash colours during the ON
/// half of each cycle, fully transparent during the OFF half — a plain on/off blink. It never blends
/// with the outline's authored [Property] colours (an early version did, and the prop read as
/// flashing red→teal as each trough fell back to the disguise's owner-locator glow).
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

	/// <summary>Blinks per second (one ON + one OFF per blink).</summary>
	[Property, Range( 0.1f, 5f )] public float Frequency { get; set; } = 0.75f;

	float _enabledAt;

	protected override void OnEnabled() => _enabledAt = Time.Now;

	protected override void OnUpdate()
	{
		// Square wave, ON first so the flash lands the instant the phase flips.
		bool on = ((Time.Now - _enabledAt) * Frequency) % 1f < 0.5f;

		foreach ( var o in Components.GetAll<SdfHighlightOutline>( FindMode.EnabledInSelfAndDescendants ) )
		{
			o.ColorOverride = on ? FlashColor : Color.Transparent;
			o.ObscuredColorOverride = on ? FlashObscuredColor : Color.Transparent;
			o.InsideColorOverride = on ? FlashInsideColor : Color.Transparent;
			o.InsideObscuredColorOverride = on ? FlashInsideObscuredColor : Color.Transparent;
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
