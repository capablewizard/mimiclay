using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// A texture that fills its box and crops the overflow, like CSS <c>object-fit: cover</c>. The engine's
/// <see cref="Image"/> panel is meant to do this, but its cover mode fitted a wide map shot to WIDTH and left a gap
/// under it (seen on the setup dialog's map cards, engine 26.09.30). This draws the texture as the panel's
/// background image instead, so the stylesheet's <c>background-size: cover</c> / <c>background-position</c> apply.
/// Usage: <c>&lt;CoverImage class="map-img" Texture=@tex /&gt;</c>.
/// </summary>
public sealed class CoverImage : Panel
{
	/// <summary>The texture to show (null = nothing).</summary>
	public Texture Texture { get; set; }

	public override void Tick()
	{
		base.Tick();

		if ( Style.BackgroundImage != Texture )
			Style.BackgroundImage = Texture;
	}
}
