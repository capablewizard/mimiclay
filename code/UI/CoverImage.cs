using System;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// A texture that fills its box and crops the overflow, like CSS <c>object-fit: cover</c>, centred. Usage:
/// <c>&lt;CoverImage class="map-img" Texture=@tex /&gt;</c>.
///
/// The engine can't be trusted with this from the stylesheet: its Image cover mode fitted a wide map shot to WIDTH
/// and left a gap under it (engine 26.09.30), and <c>background-size: auto 100%</c> is worse — an <c>auto</c> first
/// value keeps the texture's NATIVE pixel size and ignores the second (ImageRect.Calculate), so a 1536px banner drew
/// full size in a 300px card. So we size the background ourselves, in percentages of the box: the percentage path
/// is applied as-is (pixel sizes would also get multiplied by the UI scale).
/// </summary>
public sealed class CoverImage : Panel
{
	/// <summary>The texture to show (null = nothing).</summary>
	public Texture Texture { get; set; }

	public CoverImage()
	{
		Style.BackgroundPositionX = Length.Parse( "center" );
		Style.BackgroundPositionY = Length.Parse( "center" ); // the engine's "center" unit, not 50% (which is NOT CSS-relative here)
		Style.BackgroundRepeat = BackgroundRepeat.NoRepeat;
	}

	public override void Tick()
	{
		base.Tick();

		if ( Style.BackgroundImage != Texture )
			Style.BackgroundImage = Texture;

		var box = Box.Rect;
		if ( Texture is null || Texture.Width <= 0 || Texture.Height <= 0 || box.Width <= 0 || box.Height <= 0 )
			return;

		// cover: the axis where the image is relatively shorter fills 100%, the other overflows
		var imageAspect = (float)Texture.Width / Texture.Height;
		var boxAspect = box.Width / box.Height;
		var w = imageAspect > boxAspect ? 100f * imageAspect / boxAspect : 100f;
		var h = imageAspect > boxAspect ? 100f : 100f * boxAspect / imageAspect;

		// only write on change — a style write dirties layout
		if ( Style.BackgroundSizeX?.Value is not { } curW || MathF.Abs( curW - w ) > 0.01f )
			Style.BackgroundSizeX = Length.Percent( w );
		if ( Style.BackgroundSizeY?.Value is not { } curH || MathF.Abs( curH - h ) > 0.01f )
			Style.BackgroundSizeY = Length.Percent( h );
	}
}
