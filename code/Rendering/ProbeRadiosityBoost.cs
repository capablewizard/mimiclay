using System;

namespace Mimiclay;

/// <summary>
/// Marmoset-style radiosity controls for an <see cref="IndirectLightVolume"/>, applied AFTER baking — no rebakes.
///
/// Why this exists: the engine's DDGI bake gives roughly one bounce and hardcodes it. Re-running the bake seeded
/// with the previous result (ProbeBounceBaker) provably gets the seed into the bake textures, but the probe
/// captures never pick it up, so extra bounces can't come from re-rendering. This component fakes them instead:
/// a compute shader (ddgi_radiosity_boost_cs) diffuses light between neighbouring probes directly on the
/// irradiance atlas — each iteration is one fake bounce, blocked at walls via the baked distance moments.
/// The atlas is tiny, so this is effectively instant: drag the sliders and watch the GI change live.
///
/// It works on a COPY: the saved bake (BaseIrradiance) is never modified, the boosted result is a runtime
/// texture swapped into the volume. The component re-adopts whatever disk texture the volume holds (fresh bake,
/// scene load) and re-applies automatically; disabling it restores the clean bake. One caveat: while active, the
/// volume's IrradianceTexture property points at a runtime texture, so a scene save stores it as null — harmless,
/// because this component holds the real reference and restores/reboosts on the next load.
/// </summary>
[Title( "Probe Radiosity Boost" )]
[Category( "Mimiclay/Rendering" )]
[Icon( "flare" )]
public sealed class ProbeRadiosityBoost : Component, Component.ExecuteInEditor
{
	/// <summary>The DDGI volume to boost. Left empty, it grabs one on the same GameObject.</summary>
	[Property] public IndirectLightVolume Volume { get; set; }

	/// <summary>
	/// How much neighbour light each probe gathers per iteration. This is the radiosity crank: ~0.3-0.6 reads
	/// as extra bounce fill, 1+ starts to glow. Values ≥ 1 with many iterations compound toward blow-out —
	/// that's the "fake it" end of the dial, back off if the scene washes out.
	/// </summary>
	[Property, Range( 0f, 1.5f )] public float Gain { get; set; } = 0.5f;

	/// <summary>Diffusion iterations ≈ fake bounce count. Light travels one probe further per iteration.</summary>
	[Property, Range( 1, 8 )] public int Iterations { get; set; } = 3;

	/// <summary>How strongly the baked distance data blocks light at geometry. 1 = walls block bounce light,
	/// 0 = light diffuses straight through everything (maximum glow, maximum leaks).</summary>
	[Property, Range( 0f, 1f )] public float WallBlocking { get; set; } = 1f;

	/// <summary>
	/// Colour saturation of the diffused bounce. 1 = physically coloured (a yellow floor tints everything it
	/// lights, and the tint COMPOUNDS every iteration — that's the yellow-takeover). 0 = the bounce carries
	/// the same energy but white. Pull this down to boost brightness without boosting colour cast.
	/// </summary>
	[Property, Range( 0f, 1f )] public float BounceSaturation { get; set; } = 0.5f;

	/// <summary>Colour of the flat ambient floor added to the volume. Usually white.</summary>
	[Property, Category( "Ambient Floor" )] public Color AmbientColor { get; set; } = Color.White;

	/// <summary>
	/// Ambient floor level. Probe texels darker than this get lifted toward it, brighter ones are untouched —
	/// so it raises the dark corners without washing out lit areas. 0 = off. This is the "add flat white
	/// ambient light to the whole scene" dial, no lights or rebakes involved.
	/// </summary>
	[Property, Category( "Ambient Floor" ), Range( 0f, 2f )] public float AmbientLift { get; set; } = 0f;

	/// <summary>The clean saved bake the boost is computed from. Adopted automatically from the volume whenever
	/// it holds a disk texture; serialized so the boost can restore/reapply across scene loads.</summary>
	[Property, Hide] public Texture BaseIrradiance { get; set; }

	Texture _pingA, _pingB, _output;
	int _appliedHash;

	static ComputeShader _seedCs;
	static ComputeShader _boostCs;

	protected override void OnUpdate()
	{
		var volume = ResolveVolume();
		if ( volume is null )
			return;

		var current = volume.IrradianceTexture;
		var isOurs = _output.IsValid() && current == _output;

		if ( !isOurs )
		{
			if ( current.IsValid() && !string.IsNullOrEmpty( current.ResourcePath ) )
			{
				// Volume holds a disk texture (scene load / fresh bake) — adopt it as the new base.
				BaseIrradiance = current;
			}
			else if ( current.IsValid() )
			{
				// Pathless texture that isn't ours = an engine bake is mid-flight; don't fight it.
				return;
			}
			else if ( !BaseIrradiance.IsValid() )
			{
				// Nothing on the volume and nothing stored — no bake exists yet.
				return;
			}
			// else: the volume LOST its texture. This is the play-mode/save case — entering play clones the
			// live scene, where the volume held our runtime texture, which serializes as null. We hold the
			// disk reference, so fall through and reapply from BaseIrradiance.
		}

		var hash = HashCode.Combine(
			HashCode.Combine( BaseIrradiance, Gain, Iterations, WallBlocking, volume ),
			HashCode.Combine( BounceSaturation, AmbientColor, AmbientLift ),
			volume.DistanceTexture, volume.RelocationTexture );
		if ( hash == _appliedHash && isOurs )
			return;

		_appliedHash = hash;
		Apply( volume );
	}

	protected override void OnDisabled()
	{
		var volume = ResolveVolume();
		if ( volume is not null && _output.IsValid() && volume.IrradianceTexture == _output )
		{
			volume.IrradianceTexture = BaseIrradiance;
			RefreshVolume( volume );
		}

		_pingA?.Dispose(); _pingA = null;
		_pingB?.Dispose(); _pingB = null;
		_output = null;
		_appliedHash = 0;
	}

	IndirectLightVolume ResolveVolume()
	{
		if ( !Volume.IsValid() )
			Volume = Components.Get<IndirectLightVolume>();

		return Volume.IsValid() && Volume.Active ? Volume : null;
	}

	void Apply( IndirectLightVolume volume )
	{
		var baseTex = BaseIrradiance;
		if ( !baseTex.IsValid() )
			return;

		int w = baseTex.Width, h = baseTex.Height, d = baseTex.Depth;
		EnsurePingPong( w, h, d );

		// Seed ping A with the clean bake (also converts BC6H -> RGBA16F via HW decode)
		_seedCs ??= new ComputeShader( "ddgi_seed_copy_cs" );
		_seedCs.Attributes.Set( "SeedSource", baseTex );
		_seedCs.Attributes.Set( "SeedDest", _pingA );
		_seedCs.Attributes.Set( "SeedDims", new Vector3( w, h, d ) );
		_seedCs.Attributes.Set( "SeedGain", 1.0f );
		_seedCs.Dispatch( w, h, d );

		var counts = volume.ProbeCounts;
		var size = volume.Bounds.Size;
		var spacing = new Vector3(
			counts.x > 1 ? size.x / (counts.x - 1) : 0f,
			counts.y > 1 ? size.y / (counts.y - 1) : 0f,
			counts.z > 1 ? size.z / (counts.z - 1) : 0f );

		var hasRelocation = volume.RelocationTexture.IsValid();
		var hasDistance = volume.DistanceTexture.IsValid();

		_boostCs ??= new ComputeShader( "ddgi_radiosity_boost_cs" );

		var prev = _pingA;
		var next = _pingB;

		for ( int i = 0; i < Iterations; i++ )
		{
			_boostCs.Attributes.Set( "BoostBase", baseTex );
			_boostCs.Attributes.Set( "BoostPrev", prev );
			_boostCs.Attributes.Set( "BoostOut", next );
			_boostCs.Attributes.Set( "BoostDistance", hasDistance ? volume.DistanceTexture : baseTex );
			_boostCs.Attributes.Set( "BoostRelocation", hasRelocation ? volume.RelocationTexture : baseTex );
			_boostCs.Attributes.Set( "BoostCounts", new Vector3( counts.x, counts.y, counts.z ) );
			_boostCs.Attributes.Set( "BoostSpacing", spacing );
			_boostCs.Attributes.Set( "BoostGain", Gain );
			_boostCs.Attributes.Set( "BoostWall", WallBlocking );
			_boostCs.Attributes.Set( "BoostBounceSat", BounceSaturation );

			// The floor is uniform, so diffusing it adds nothing — apply on the final iteration only, which
			// also stops it compounding through the gain feedback.
			var isLast = i == Iterations - 1;
			var floorColor = isLast ? new Vector3( AmbientColor.r, AmbientColor.g, AmbientColor.b ) * AmbientLift : Vector3.Zero;
			_boostCs.Attributes.Set( "BoostAmbientFloor", floorColor );
			_boostCs.Attributes.Set( "BoostUseRelocation", hasRelocation ? 1 : 0 );
			_boostCs.Attributes.Set( "BoostUseDistance", hasDistance ? 1 : 0 );
			_boostCs.Dispatch( w, h, d );

			(prev, next) = (next, prev);
		}

		_output = prev;
		volume.IrradianceTexture = _output;
		RefreshVolume( volume );
	}

	void EnsurePingPong( int w, int h, int d )
	{
		if ( _pingA.IsValid() && _pingA.Width == w && _pingA.Height == h && _pingA.Depth == d )
			return;

		_pingA?.Dispose();
		_pingB?.Dispose();
		_pingA = CreateAtlas( w, h, d, "RadiosityBoostA" );
		_pingB = CreateAtlas( w, h, d, "RadiosityBoostB" );
		_output = null;
	}

	static Texture CreateAtlas( int w, int h, int d, string name )
	{
		return Texture.CreateVolume( w, h, d, ImageFormat.RGBA16161616F )
			.WithName( name )
			.WithUAVBinding()
			.Finish();
	}

	/// <summary>The DDGI system only re-uploads texture indices when a volume marks itself dirty, and MarkDirty
	/// is internal — toggling the component is the public way to force it.</summary>
	static void RefreshVolume( IndirectLightVolume volume )
	{
		volume.Enabled = false;
		volume.Enabled = true;
	}
}
