using System;

namespace Mimiclay;

/// <summary>Payload estimates for the uncompressed, single-mip textures owned by the SDF renderer.
/// Excludes driver alignment, staging copies and engine-owned fallback textures.</summary>
internal static class SdfGpuMemory
{
	internal static long TextureBytes( Texture texture )
	{
		if ( !texture.IsValid() ) return 0;
		int bytesPerPixel = texture.ImageFormat switch
		{
			ImageFormat.I8 => 1,
			ImageFormat.RGBA8888 => 4,
			ImageFormat.R16F => 2,
			ImageFormat.R32F => 4,
			ImageFormat.RGBA32323232F => 16,
			_ => 4 // diagnostics only: never throw from a stats readout
		};
		return (long)texture.Width * Math.Max( texture.Height, 1 ) * Math.Max( texture.Depth, 1 ) * bytesPerPixel;
	}
}
