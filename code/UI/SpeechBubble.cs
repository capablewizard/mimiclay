namespace Mimiclay;

/// <summary>
/// A world-anchored speech bubble: put this on any GameObject and the shared overlay
/// (<see cref="SpeechBubbleHud"/>, spawned on demand) draws a paper bubble at this object's screen
/// position, tail pointing down at it. Pure presentation — no networking, no game knowledge — so anything
/// can wear one: the tutorial character's invite today, NPC barks or player emotes later.
///
/// Position the ANCHOR OBJECT where the tail should point (typically just above a character's head);
/// the bubble hangs above that point and follows it every frame.
/// </summary>
[Title( "Speech Bubble" )]
[Category( "Mimiclay UI" )]
[Icon( "chat_bubble" )]
public sealed class SpeechBubble : Component
{
	/// <summary>What it says. Empty = nothing drawn.</summary>
	[Property] public string Text { get; set; } = "";

	/// <summary>Where the CARD sits relative to the anchor, in WORLD units along the camera's right axis
	/// (+ = screen-right of the speaker). World, not pixels, on purpose: the offset goes through the same
	/// projection as the anchor, so it shrinks with distance like the character does — a fixed pixel shove
	/// reads detached the moment you step back.</summary>
	[Property] public float OffsetRight { get; set; } = 24f;

	/// <summary>Same, along the camera's up axis (+ = above the anchor).</summary>
	[Property] public float OffsetUp { get; set; } = 6f;

	/// <summary>Camera-to-anchor distance beyond which the bubble is culled (it eases out over the last
	/// stretch rather than popping). 0 = visible at any distance.</summary>
	[Property] public float MaxDistance { get; set; } = 0f;

	/// <summary>Typewriter reveal speed, characters per second — the text types out (with a per-letter
	/// blip) each time the bubble appears or its text changes. 0 = no typing, the full text just shows.</summary>
	[Property] public float TypeSpeed { get; set; } = 28f;

	/// <summary>How many character-ticks of <see cref="TypeSpeed"/> a word gap costs — the typewriter's
	/// silent breath between words (only real characters blip). 1 = spaces type as fast as letters;
	/// 3 ≈ a 100ms beat at the default speed. 0 = word gaps are free.</summary>
	[Property] public int SpaceTicks { get; set; } = 3;

	/// <summary>Loudness of the per-letter typing blip. 0 = types silently — a bubble that shouldn't be
	/// heard (a distant bark, an ambient sign) turns the voice off here rather than in the overlay.</summary>
	[Property, Range( 0f, 1f )] public float TypeVolume { get; set; } = 0.4f;

	/// <summary>Per-blip loudness wobble, as a ± fraction of <see cref="TypeVolume"/> (0.15 = ±15%). Rolled
	/// fresh for every blip alongside the pitch: a fixed pair reads as one sample retriggering, the jitter
	/// makes it a voice. 0 = every letter exactly as loud as the last.</summary>
	[Property, Range( 0f, 1f )] public float TypeVolumeJitter { get; set; } = 0.15f;

	/// <summary>Per-machine runtime hide — the <see cref="SdfHighlightOutline.Hidden"/> pattern. Drive THIS
	/// from code, never <c>Enabled</c>: Enabled serializes, so an in-editor play session flipping it would
	/// bake the flip into the scene on the next save.</summary>
	public bool Hidden { get; set; }

	/// <summary>Per-machine runtime replacement for <see cref="Text"/> — same reasoning as
	/// <see cref="Hidden"/>: code that wants this bubble to say something else for a while (the tutorial
	/// speaking through the character) writes HERE, never the serialized Text, which an in-editor play
	/// session would bake into the scene. Null = the authored text; empty = say nothing (the bubble pops
	/// out). Changing it retypes, exactly like a Text change.</summary>
	public string TextOverride { get; set; }

	/// <summary>What the bubble is actually saying this frame.</summary>
	public string EffectiveText => TextOverride ?? Text;

	/// <summary>The typewriter has fully revealed the current text (written by <see cref="SpeechBubbleHud"/>
	/// each shown frame). Sequencing hooks read this — e.g. the tutorial holds its instruction card until
	/// the character finishes talking.</summary>
	public bool FullyTyped { get; internal set; }

	// The overlay lives with the system, not the scene: first enabled bubble in a scene brings it up.
	protected override void OnEnabled() => SpeechBubbleHud.Ensure( Scene );
}
