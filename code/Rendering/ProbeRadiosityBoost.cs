using System;
using System.Text.Json.Nodes;

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
/// because this component holds the real reference and restores/reboosts on the next load. That null depends on the
/// runtime atlas having NO name (see <see cref="CreateAtlas"/>): a named runtime texture now serializes as its name.
/// </summary>
[Title( "Probe Radiosity Boost" )]
[Category( "Mimiclay/Rendering" )]
[Icon( "flare" )]
public sealed class ProbeRadiosityBoost : Component, Component.ExecuteInEditor
{
	/// <summary>The DDGI volume to boost. Left empty, it grabs one on the same GameObject.</summary>
	[Property] public IndirectLightVolume Volume { get; set; }

	/// <summary>
	/// The envmap whose lighting Level &lt; 1 blends toward — what the scene's ambient looks like with no DDGI
	/// volume. Empty = the SkyBox2D's sky lighting. Assign an EnvmapProbe (Baked or Custom Texture mode — realtime
	/// probes keep their texture private) to blend toward that instead. With neither, Level &lt; 1 blends to black.
	/// </summary>
	[Property] public EnvmapProbe FallbackEnvmap { get; set; }

	/// <summary>
	/// Overall GI level. 0 = the scene's lighting WITHOUT a DDGI volume (the envmap ambient — see
	/// <see cref="FallbackEnvmap"/>), 1 = the bake exactly as baked, above 1 = the radiosity boost —
	/// each probe gathers (Level - 1) of its neighbours' light per iteration. ~1.3-1.6 reads as extra bounce
	/// fill, 2+ starts to glow, and with many iterations it compounds toward blow-out — that's the "fake it"
	/// end of the dial, back off if the scene washes out.
	/// </summary>
	[Property, Range( 0f, 2.5f )] public float Level { get; set; } = 1f;

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

	/// <summary>
	/// Colour the boosted bounce is pushed toward — an orange warms it, a blue cools it. Hue only: the tint is
	/// normalised to unit luminance, so it shifts colour without changing brightness. Applied every iteration,
	/// so like a coloured surface it compounds with bounce count — more Iterations = a stronger cast.
	/// Does nothing at Level ≤ 1.
	/// </summary>
	[Property] public Color BounceTint { get; set; } = new Color( 1f, 0.8f, 0.6f );

	/// <summary>How far the bounce is pushed toward <see cref="BounceTint"/>. 0 = untinted.</summary>
	[Property, Range( 0f, 1f )] public float BounceTintAmount { get; set; } = 0f;

	/// <summary>
	/// Contrast of the final GI, pivoting on the volume's average brightness (log-average, so hot spots near
	/// lights don't skew it). 1 = unchanged, above 1 = brighter brights and darker darks, below 1 = flatter,
	/// more even ambient. Hue is preserved. Applied before the ambient floor, so the floor still holds.
	/// </summary>
	[Property, Range( 0.25f, 2.5f )] public float Contrast { get; set; } = 1f;

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

	public override int ComponentVersion => 1;

	/// <summary>v1: Gain (bounce strength, 0 = raw bake) became Level (1 = raw bake), so Level = 1 + Gain.</summary>
	[JsonUpgrader( typeof( ProbeRadiosityBoost ), 1 )]
	static void Upgrader_v1( JsonObject json )
	{
		if ( json["Gain"] is JsonValue gain && gain.TryGetValue<float>( out var g ) )
			json["Level"] = 1f + g;

		json.Remove( "Gain" );
	}

	Texture _pingA, _pingB, _output, _statsTex;
	int _appliedHash;

	static ComputeShader _seedCs;
	static ComputeShader _boostCs;
	static ComputeShader _lumCs;
	static ComputeShader _gradeCs;
	static Texture _blankCube; // bound when there's no fallback envmap, never sampled

	/// <summary>Debug dump for the mimi_dbg_radiosity editor command (Editor/RadiosityBoostDebug.cs).</summary>
	public string DebugDescribe()
	{
		var v = Volume;
		var counts = v.IsValid() ? v.ProbeCounts : default;
		return $"scene '{Scene?.Name}' enabled {Enabled} active {Active} | Level {Level} It {Iterations} Wall {WallBlocking} Sat {BounceSaturation} Tint {BounceTint}x{BounceTintAmount} Contrast {Contrast} Ambient {AmbientColor}x{AmbientLift}\n" +
			$"    counts {counts} -> expect atlas {counts.x * 8}x{counts.y * 8}x{counts.z} | base {Dims( BaseIrradiance )} '{BaseIrradiance?.ResourcePath}'\n" +
			$"    volume irr {Dims( v?.IrradianceTexture )} '{v?.IrradianceTexture?.ResourcePath}' isOurs {(_output.IsValid() && v?.IrradianceTexture == _output)} | dist {Dims( v?.DistanceTexture )} reloc {Dims( v?.RelocationTexture )}\n" +
			$"    fallback {Dims( FallbackTexture( out var fbTint ) )} mips {FallbackTexture( out _ )?.Mips} tint {fbTint} '{FallbackTexture( out _ )?.ResourcePath}'\n" +
			$"    envmap probes: {string.Join( ", ", Scene.GetAll<EnvmapProbe>().Select( p => $"{p.GameObject.Name} mode {p.Mode}" ) )} | skies: {string.Join( ", ", Scene.GetAll<SkyBox2D>().Select( s => $"{s.GameObject.Name} indirect {s.SkyIndirectLighting} tint {s.Tint}" ) )}";

		static string Dims( Texture t ) => t.IsValid() ? $"{t.Width}x{t.Height}x{t.Depth} {t.ImageFormat}" : "null";
	}

	protected override void OnUpdate()
	{
		var volume = ResolveVolume();
		if ( volume is null )
			return;

		var current = volume.IrradianceTexture;
		var isOurs = _output.IsValid() && current == _output;

		// A pathed output is an atlas from before the unnamed-atlas fix (hotload keeps fields) — force a reapply so
		// EnsurePingPong retires it before it can serialize into the scene again.
		if ( isOurs && !string.IsNullOrEmpty( _output.ResourcePath ) )
			_appliedHash = 0;

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
			HashCode.Combine( BaseIrradiance, Level, Iterations, WallBlocking, volume ),
			HashCode.Combine( BounceSaturation, BounceTint, BounceTintAmount, Contrast, AmbientColor, AmbientLift, FallbackTexture( out var fbTint ), fbTint ),
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
		_statsTex?.Dispose(); _statsTex = null;
		_output = null;
		_appliedHash = 0;
	}

	/// <summary>The cubemap + tint the no-DDGI ambient comes from, or null if there's no usable envmap.</summary>
	Texture FallbackTexture( out Color tint )
	{
		// Explicit only: an assigned probe wins, empty means the skybox. (Auto-picking the scene's active probe
		// matched the engine's priority but surprised in use — clearing the field kept the probe, not the sky.)
		var probe = FallbackEnvmap.IsValid() ? FallbackEnvmap : null;

		tint = probe?.TintColor ?? Color.White;
		var tex = probe?.Mode switch
		{
			EnvmapProbe.EnvmapProbeMode.Baked => probe.BakedTexture,
			EnvmapProbe.EnvmapProbeMode.CustomTexture => probe.Texture,
			_ => null,
		};
		if ( tex.IsValid() )
			return tex;

		// No probe assigned (or not usable): the sky. SkyBox2D with SkyIndirectLighting registers its sky cubemap as a scene-wide,
		// lowest-priority envmap tinted by Tint — the "default skybox lighting" you get with no DDGI and no probes.
		var sky = Scene.GetAll<SkyBox2D>().FirstOrDefault( s => s.Active && s.SkyIndirectLighting );
		tint = sky?.Tint ?? Color.White;
		tex = sky?.SkyTexture;
		return tex.IsValid() ? tex : null;
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

		// Size the atlas from the probe grid, NOT baseTex.Width/Height/Depth. Re-baking at a different density
		// rewrites the same .vtex path, and the reloaded Texture object can keep the OLD bake's managed dims while
		// the GPU holds the new data (seen in CharadesZoo_art2: object said 80x80x7, file + GPU were 120x128x10).
		// The engine's integrator writes and samples by ProbeCounts, so that's the source of truth — trusting the
		// stale dims copied one corner of the atlas and the engine stretched it over the whole volume.
		var counts = volume.ProbeCounts;
		int w = counts.x * 8, h = counts.y * 8, d = counts.z;
		if ( baseTex.Width != w || baseTex.Height != h || baseTex.Depth != d )
			Log.Warning( $"ProbeRadiosityBoost: irradiance texture reports {baseTex.Width}x{baseTex.Height}x{baseTex.Depth} but the probe grid needs {w}x{h}x{d} — stale dims after a density change, using the grid. Restart the editor if the bake itself looks wrong." );
		EnsurePingPong( w, h, d );

		// Seed ping A with the clean bake (also converts BC6H -> RGBA16F via HW decode)
		_seedCs ??= new ComputeShader( "ddgi_seed_copy_cs" );
		_seedCs.Attributes.Set( "SeedSource", baseTex );
		_seedCs.Attributes.Set( "SeedDest", _pingA );
		_seedCs.Attributes.Set( "SeedDims", new Vector3( w, h, d ) );
		_seedCs.Attributes.Set( "SeedGain", 1.0f );
		_seedCs.Dispatch( w, h, d );

		var size = volume.Bounds.Size;
		var spacing = new Vector3(
			counts.x > 1 ? size.x / (counts.x - 1) : 0f,
			counts.y > 1 ? size.y / (counts.y - 1) : 0f,
			counts.z > 1 ? size.z / (counts.z - 1) : 0f );

		var hasRelocation = volume.RelocationTexture.IsValid();
		var hasDistance = volume.DistanceTexture.IsValid();

		_boostCs ??= new ComputeShader( "ddgi_radiosity_boost_cs" );

		// Level ≤ 1 blends the bake toward the no-DDGI envmap ambient (black if there's no envmap); above 1 the
		// excess is the bounce gain. With no gain the
		// neighbour gather contributes nothing, so one pass is enough.
		var baseScale = MathF.Min( Level, 1f );
		var fallbackTex = FallbackTexture( out var fallbackTint );
		var gain = MathF.Max( Level - 1f, 0f );
		var iterations = gain > 0f ? Iterations : 1;

		// Tint normalised to unit luminance so it's hue-only, then blended in by amount
		var tint = new Vector3( BounceTint.r, BounceTint.g, BounceTint.b );
		var tintLum = Vector3.Dot( tint, new Vector3( 0.2126f, 0.7152f, 0.0722f ) );
		tint = tintLum > 0.0001f ? tint / tintLum : Vector3.One;
		tint = Vector3.Lerp( Vector3.One, tint, BounceTintAmount );

		var prev = _pingA;
		var next = _pingB;

		for ( int i = 0; i < iterations; i++ )
		{
			_boostCs.Attributes.Set( "BoostBase", baseTex );
			_boostCs.Attributes.Set( "BoostPrev", prev );
			_boostCs.Attributes.Set( "BoostOut", next );
			_boostCs.Attributes.Set( "BoostDistance", hasDistance ? volume.DistanceTexture : baseTex );
			_boostCs.Attributes.Set( "BoostRelocation", hasRelocation ? volume.RelocationTexture : baseTex );
			_boostCs.Attributes.Set( "BoostCounts", new Vector3( counts.x, counts.y, counts.z ) );
			_boostCs.Attributes.Set( "BoostSpacing", spacing );
			_boostCs.Attributes.Set( "BoostBaseScale", baseScale );
			_boostCs.Attributes.Set( "BoostUseFallback", fallbackTex is null ? 0 : 1 );
			_boostCs.Attributes.Set( "BoostFallbackEnv", fallbackTex ?? (_blankCube ??= Texture.CreateCube( 1, 1 ).WithName( "RadiosityBoostBlankCube" ).Finish()) );
			_boostCs.Attributes.Set( "BoostFallbackMip", fallbackTex is null ? 0f : fallbackTex.Mips - 1 );
			_boostCs.Attributes.Set( "BoostFallbackTint", new Vector3( fallbackTint.r, fallbackTint.g, fallbackTint.b ) );			_boostCs.Attributes.Set( "BoostGain", gain );
			_boostCs.Attributes.Set( "BoostWall", WallBlocking );
			_boostCs.Attributes.Set( "BoostBounceSat", BounceSaturation );

			_boostCs.Attributes.Set( "BoostTint", tint );
			_boostCs.Attributes.Set( "BoostUseRelocation", hasRelocation ? 1 : 0 );
			_boostCs.Attributes.Set( "BoostUseDistance", hasDistance ? 1 : 0 );
			_boostCs.Dispatch( w, h, d );

			(prev, next) = (next, prev);
		}

		// Grade once on the final result. Contrast pivots on the volume's log-average luminance, measured on the
		// GPU so it tracks whatever the bake + boost produced. The ambient floor goes after contrast so contrast
		// can't crush the corners it lifts — and running it once (not per iteration) stops it compounding through
		// the gain feedback; a uniform floor gains nothing from diffusion anyway.
		_statsTex ??= Texture.Create( 1, 1, ImageFormat.RGBA32323232F ).WithName( "RadiosityBoostStats" ).WithUAVBinding().Finish();

		_lumCs ??= new ComputeShader( "ddgi_lum_average_cs" );
		_lumCs.Attributes.Set( "LumIn", prev );
		_lumCs.Attributes.Set( "LumRelocation", hasRelocation ? volume.RelocationTexture : baseTex );
		_lumCs.Attributes.Set( "LumStats", _statsTex );
		_lumCs.Attributes.Set( "LumDims", new Vector3( w, h, d ) );
		_lumCs.Attributes.Set( "LumUseRelocation", hasRelocation ? 1 : 0 );
		_lumCs.Dispatch( 8, 8, 1 ); // exactly one group — the shader strides the whole atlas itself

		_gradeCs ??= new ComputeShader( "ddgi_grade_cs" );
		_gradeCs.Attributes.Set( "GradeIn", prev );
		_gradeCs.Attributes.Set( "GradeOut", next );
		_gradeCs.Attributes.Set( "GradeStats", _statsTex );
		_gradeCs.Attributes.Set( "GradeDims", new Vector3( w, h, d ) );
		_gradeCs.Attributes.Set( "GradeContrast", Contrast );
		_gradeCs.Attributes.Set( "GradeAmbientFloor", new Vector3( AmbientColor.r, AmbientColor.g, AmbientColor.b ) * AmbientLift );
		_gradeCs.Dispatch( w, h, d );

		_output = next;
		volume.IrradianceTexture = _output;
		RefreshVolume( volume );
	}

	void EnsurePingPong( int w, int h, int d )
	{
		// The ResourcePath check retires atlases that still carry a name (hotloaded from the pre-fix code, or an engine
		// that starts naming anonymous textures) — a pathed atlas would serialize into the scene again.
		if ( _pingA.IsValid() && _pingA.Width == w && _pingA.Height == h && _pingA.Depth == d && string.IsNullOrEmpty( _pingA.ResourcePath ) )
			return;

		_pingA?.Dispose();
		_pingB?.Dispose();
		_pingA = CreateAtlas( w, h, d );
		_pingB = CreateAtlas( w, h, d );
		_output = null;
	}

	/// <summary>
	/// Deliberately UNNAMED. Since the Oct-2026 engine update, Texture.Create registers any named runtime texture as a
	/// weak resource (RegisterWeakResourceId), which gives it a ResourcePath = the lowercased name. The boosted atlas
	/// sits in the volume's IrradianceTexture property, so a named one serialized into the scene as "radiosityboostb"
	/// and every load tried to find a file by that name (FixupResourceName: missing extension). Unnamed = null
	/// ResourcePath = serializes as null, which is what this component relies on (see class remarks).
	/// </summary>
	static Texture CreateAtlas( int w, int h, int d )
	{
		return Texture.CreateVolume( w, h, d, ImageFormat.RGBA16161616F )
			.WithUAVBinding()
			.Finish();
	}

	/// <summary>
	/// The DDGI system only re-uploads texture indices when a volume marks itself dirty, and MarkDirty is
	/// internal. Every DDGI property setter calls it on change, so nudge NormalBias and put it back.
	///
	/// Don't toggle volume.Enabled for this (the old approach): from OnDisabled during component removal the
	/// toggle never reached the volume, so the system kept pointing at our freed boost texture's bindless slot —
	/// the boosted look stuck after removal until a rebake or GameObject toggle forced a re-upload. It also
	/// re-ran LoadProbesFromRelocationTexture on every slider drag.
	/// </summary>
	static void RefreshVolume( IndirectLightVolume volume )
	{
		var bias = volume.NormalBias;
		volume.NormalBias = bias + 0.001f;
		volume.NormalBias = bias;
	}
}
