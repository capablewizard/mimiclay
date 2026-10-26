using Sandbox.Rendering;

namespace Mimiclay;

/// <summary>
/// FXAA on the camera. s&amp;box ships no image-space AA, only MSAA, and MSAA can't see edges that aren't triangle
/// edges: shading sparkle (normal maps, speculars) and the raymarch's inner self-overlap edges. The raymarch's
/// outer silhouettes get their own coverage AA (mimiclay_sdf_edge_aa); this catches the rest.
///
/// Runs first in AfterPostProcess — after tonemapping (its thresholds want display values) and before Sharpen
/// (order 1), which would otherwise exaggerate the stair-steps before FXAA sees them. UI draws later (AfterUI),
/// so text and HUD are never blurred.
/// </summary>
[Title( "FXAA" )]
[Category( "Post Processing" )]
[Icon( "deblur" )]
public sealed class Fxaa : BasePostProcess<Fxaa>
{
	/// <summary>Local contrast an edge needs, relative to its brightest tap. Lower catches more (and softens more).</summary>
	[Property, Range( 0.063f, 0.333f )] public float EdgeThreshold { get; set; } = 0.125f;

	/// <summary>Absolute contrast floor, so noise in dark areas is left alone.</summary>
	[Property, Range( 0f, 0.1f )] public float EdgeThresholdMin { get; set; } = 0.0312f;

	/// <summary>How much single-pixel detail gets blended: 0 = edges only (sharpest), 1 = softest.</summary>
	[Property, Range( 0f, 1f )] public float Subpixel { get; set; } = 0.75f;

	/// <summary>Global kill switch, for A/B: `mimiclay_fxaa 0`.</summary>
	[ConVar( "mimiclay_fxaa" )]
	public static bool Enabled { get; set; } = true;

	static readonly Material Shader = Material.FromShader( "shaders/pp_fxaa.shader" );

	public override void Render()
	{
		if ( !Enabled )
			return;

		Attributes.Set( "EdgeThreshold", GetWeighted( x => x.EdgeThreshold, 0.125f ) );
		Attributes.Set( "EdgeThresholdMin", GetWeighted( x => x.EdgeThresholdMin, 0.0312f ) );
		Attributes.Set( "Subpixel", GetWeighted( x => x.Subpixel, 0.75f ) );

		Blit( BlitMode.WithBackbuffer( Shader, Stage.AfterPostProcess, 0 ), "FXAA" );
	}
}
