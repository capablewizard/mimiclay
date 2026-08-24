using System;
using System.Threading.Tasks;

namespace Mimiclay;

/// <summary>
/// Adds a bounce count to <see cref="IndirectLightVolume"/> baking, Marmoset-radiosity style.
///
/// How the engine bake works: every probe renders the REAL scene from its position into a cubemap, and while
/// a bake is running the volume's live textures are the in-progress ones — so each capture already sees the
/// irradiance integrated from previously-rendered probes. That's where the engine's (hardcoded, private)
/// "two pass" bounce comes from, and it's why simply re-baking doesn't accumulate: each bake starts from
/// freshly-zeroed textures.
///
/// The trick here: <see cref="IndirectLightVolume.BakeProbes"/> assigns the new empty irradiance texture to
/// the volume's public <c>IrradianceTexture</c> property synchronously, before the first probe renders (its
/// first await is ahead of the first capture). So we kick off the bake, immediately GPU-copy the PREVIOUS
/// bake's irradiance into the new texture, then let it run — every capture now starts from last pass's
/// lighting, making each full re-bake one more radiosity iteration. Bounces = N means N sequential engine
/// bakes, each seeded with the last. Converges geometrically, so 3-4 passes is usually visually done.
///
/// Editor-only (the engine baker needs the editor progress UI, and the scene should be saved so the result
/// textures persist). Works alongside the volume's normal Bake button — that just gives you pass 1 back.
/// </summary>
[Title( "Probe Bounce Baker" )]
[Category( "Mimiclay/Rendering" )]
[Icon( "flare" )]
public sealed class ProbeBounceBaker : Component, Component.ExecuteInEditor
{
	/// <summary>The DDGI volume to bake. Left empty, it grabs one on the same GameObject.</summary>
	[Property] public IndirectLightVolume Volume { get; set; }

	/// <summary>
	/// Number of seeded bake passes. 1 = identical to the engine's stock bake. Each extra pass adds one more
	/// light bounce (on top of the bake's own internal near-geometry double-render).
	/// </summary>
	[Property, Range( 1, 8 )] public int Bounces { get; set; } = 3;

	/// <summary>
	/// Radiosity gain — the fake-it dial. Multiplies the previous pass's irradiance as it seeds the next one,
	/// so bounce light gets amplified and the boost COMPOUNDS with the pass count (older bounces get gain²,
	/// gain³...). 1 = physically converged (which plateaus fast — bounce N carries only the albedo fraction of
	/// bounce N-1, so real scenes look "done" by pass 2-3). Push toward 2+ with several bounces for a glowy,
	/// over-lit radiosity look; too high with many passes runs away, back it off if the bake blooms out.
	/// Only affects bounce light — the direct capture is re-rendered every pass and stays clean.
	/// </summary>
	[Property, Range( 0.5f, 4f )] public float BounceGain { get; set; } = 1.0f;

	[Button( "Bake With Bounces", "lightbulb" )]
	public async Task BakeWithBounces()
	{
		var volume = Volume.IsValid() ? Volume : Components.Get<IndirectLightVolume>();
		if ( !volume.IsValid() )
		{
			Log.Warning( "ProbeBounceBaker: no IndirectLightVolume found" );
			return;
		}

		for ( int pass = 0; pass < Bounces; pass++ )
		{
			// Keep a reference to the completed previous pass before BakeProbes swaps in fresh textures.
			var previousIrradiance = volume.IrradianceTexture;

			// Runs synchronously up to (but not past) the first probe capture, so the volume already holds
			// the new empty irradiance texture when this returns — and nothing has rendered into it yet.
			var bake = volume.BakeProbes();

			if ( pass > 0 )
			{
				SeedIrradiance( previousIrradiance, volume.IrradianceTexture, BounceGain );
				LogSeedStats( previousIrradiance, volume.IrradianceTexture );
			}

			await bake;

			// A cancelled/failed bake nulls the volume's textures — nothing to seed the next pass with.
			if ( !volume.IrradianceTexture.IsValid() )
			{
				Log.Warning( $"ProbeBounceBaker: bake pass {pass + 1}/{Bounces} was cancelled or failed, stopping" );
				return;
			}

			Log.Info( $"ProbeBounceBaker: bounce pass {pass + 1}/{Bounces} complete" );
		}
	}

	static ComputeShader _seedCopyCs;

	/// <summary>
	/// Copies the previous pass's irradiance into the in-progress bake texture, so probe captures start from
	/// last pass's lighting instead of black. The saved previous pass is BC6H on disk (the engine's auto vtex
	/// format for HDR), so a raw CopyTexture can't work — instead a compute shader Loads the compressed volume
	/// (the GPU decodes BC6H in hardware) and stores into the RGBA16F bake target. Skipped when grids mismatch.
	/// </summary>
	static void SeedIrradiance( Texture previous, Texture current, float gain )
	{
		if ( !previous.IsValid() || !current.IsValid() )
			return;

		if ( previous.Width != current.Width || previous.Height != current.Height || previous.Depth != current.Depth )
		{
			Log.Warning( "ProbeBounceBaker: probe grid changed since last pass, seeding skipped" );
			return;
		}

		try
		{
			// Raw copy only works format-matched and can't apply gain — otherwise the shader converts+scales.
			if ( previous.ImageFormat == current.ImageFormat && gain == 1.0f )
			{
				Graphics.CopyTexture( previous, current );
				return;
			}

			_seedCopyCs ??= new ComputeShader( "ddgi_seed_copy_cs" );
			_seedCopyCs.Attributes.Set( "SeedSource", previous );
			_seedCopyCs.Attributes.Set( "SeedDest", current );
			_seedCopyCs.Attributes.Set( "SeedDims", new Vector3( current.Width, current.Height, current.Depth ) );
			_seedCopyCs.Attributes.Set( "SeedGain", gain );
			_seedCopyCs.Dispatch( current.Width, current.Height, current.Depth );
		}
		catch ( Exception e )
		{
			Log.Warning( $"ProbeBounceBaker: couldn't seed from previous pass ({e.Message}), this pass bakes fresh" );
		}
	}

	/// <summary>
	/// Diagnostic: reads the bake target back after seeding and logs pixel stats, so a silently-failed seed
	/// (shader didn't compile, binding didn't stick) shows up as "seeded mean ~0" instead of a mystery.
	/// </summary>
	static void LogSeedStats( Texture previous, Texture current )
	{
		try
		{
			Graphics.FlushGPU();

			var w = current.Width;
			var h = current.Height;
			var d = current.Depth;
			var pixels = new Half[w * h * d * 4];
			current.GetPixels3D( (0, 0, 0, w, h, d), 0, pixels.AsSpan(), ImageFormat.RGBA16161616F );

			float sum = 0f, max = 0f;
			int count = 0;
			for ( int i = 0; i < pixels.Length; i += 4 )
			{
				var v = MathF.Max( (float)pixels[i], MathF.Max( (float)pixels[i + 1], (float)pixels[i + 2] ) );
				sum += v;
				max = MathF.Max( max, v );
				count++;
			}

			Log.Info( $"ProbeBounceBaker: seed check — prev format {previous.ImageFormat}, target {w}x{h}x{d}, mean {sum / Math.Max( count, 1 ):F4}, max {max:F3}" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"ProbeBounceBaker: seed readback failed ({e.Message})" );
		}
	}
}
