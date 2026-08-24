using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Piggybacks on an <see cref="IndirectLightVolume"/> (DDGI) and places a real point light at every probe
/// position, so the probe grid can act as an art-directable fill-light rig. Reconstructs the exact same grid
/// the engine uses (bounds + probe counts), and honours the baked relocation data when present — relocated
/// probes light from their pushed-out position, deactivated probes (inside geometry) get no light.
///
/// The lights are raw <see cref="ScenePointLight"/> scene objects, not GameObjects — nothing gets added to the
/// hierarchy or saved into the scene, and tearing down/rebuilding is cheap. Light settings (intensity, radius,
/// attenuation, shadows...) apply live to the existing lights; the grid itself rebuilds automatically when the
/// volume's bounds/density/transform change.
///
/// Watch <see cref="MaxLights"/>: a big volume at high density is tens of thousands of probes, which no
/// lightbinner will forgive. Shadows on many point lights are similarly brutal — use sparingly.
/// </summary>
[Title( "Probe Grid Lights" )]
[Category( "Mimiclay/Rendering" )]
[Icon( "lightbulb" )]
public sealed class ProbeGridLights : Component, Component.ExecuteInEditor
{
	/// <summary>The DDGI volume to mirror. Left empty, it grabs one on the same GameObject.</summary>
	[Property] public IndirectLightVolume Volume { get; set; }

	/// <summary>
	/// Fraction of probes that get a light (1 = every probe). Selection is a deterministic hash of the probe's
	/// grid position, so the same probes stay lit across rebuilds/sessions and the subset stays spatially even.
	/// Auto radius grows to compensate as this drops, keeping the volume covered with fewer lights.
	/// </summary>
	[Property, Range( 0f, 1f )] public float LightDensity { get; set; } = 1.0f;

	/// <summary>Brightness multiplier applied to <see cref="Tint"/> for every light.</summary>
	[Property, Range( 0f, 8f )] public float Intensity { get; set; } = 1.0f;

	/// <summary>Base colour of every light.</summary>
	[Property] public Color Tint { get; set; } = Color.White;

	/// <summary>Light radius in units. 0 = auto: the largest probe spacing, so neighbouring lights just overlap.</summary>
	[Property] public float Radius { get; set; } = 0f;

	/// <summary>Quadratic attenuation term (same scale as the PointLight component; 1 = engine default falloff).</summary>
	[Property, Range( 0f, 10f )] public float Attenuation { get; set; } = 1.0f;

	/// <summary>Whether the lights cast shadows. Expensive — this is per probe, think hard above a handful of lights.</summary>
	[Property, Category( "Shadows" )] public bool Shadows { get; set; } = false;

	/// <summary>Shadow map resolution per light. 0 lets the engine decide.</summary>
	[Property, Category( "Shadows" )] public int ShadowResolution { get; set; } = 0;

	/// <summary>Whether the lights contribute specular highlights (off = pure diffuse fill).</summary>
	[Property] public bool Specular { get; set; } = true;

	/// <summary>Skip probes the bake deactivated for being inside geometry (needs baked relocation data).</summary>
	[Property, Category( "Probe Data" )] public bool SkipInactiveProbes { get; set; } = true;

	/// <summary>Place lights at the relocated probe positions rather than the raw grid (needs baked relocation data).</summary>
	[Property, Category( "Probe Data" )] public bool UseRelocation { get; set; } = true;

	/// <summary>Hard cap on spawned lights. Probes beyond this are dropped (with a warning) instead of flooding the renderer.</summary>
	[Property, Category( "Probe Data" )] public int MaxLights { get; set; } = 256;

	readonly List<ScenePointLight> _lights = new();
	int _gridHash;
	int _settingsHash;

	protected override void OnEnabled()
	{
		Volume ??= Components.Get<IndirectLightVolume>();
		Rebuild();
	}

	protected override void OnDisabled()
	{
		Clear();
	}

	protected override void OnUpdate()
	{
		var volume = ResolveVolume();

		var gridHash = ComputeGridHash( volume );
		if ( gridHash != _gridHash )
		{
			Rebuild();
			return;
		}

		var settingsHash = ComputeSettingsHash();
		if ( settingsHash != _settingsHash )
		{
			_settingsHash = settingsHash;
			ApplySettings( volume );
		}
	}

	IndirectLightVolume ResolveVolume()
	{
		if ( !Volume.IsValid() )
			Volume = Components.Get<IndirectLightVolume>();

		return Volume.IsValid() && Volume.Active ? Volume : null;
	}

	int ComputeGridHash( IndirectLightVolume volume )
	{
		if ( volume is null )
			return 0;

		return HashCode.Combine(
			volume.Bounds,
			volume.ProbeCounts,
			volume.WorldTransform,
			volume.RelocationTexture,
			SkipInactiveProbes,
			UseRelocation,
			MaxLights,
			LightDensity );
	}

	int ComputeSettingsHash()
	{
		return HashCode.Combine( Intensity, Tint, Radius, Attenuation, Shadows, ShadowResolution, Specular );
	}

	void Clear()
	{
		foreach ( var light in _lights )
			light?.Delete();

		_lights.Clear();
		_gridHash = 0;
	}

	void Rebuild()
	{
		Clear();

		var volume = ResolveVolume();
		if ( volume is null || Scene?.SceneWorld is null )
			return;

		_gridHash = ComputeGridHash( volume );
		_settingsHash = ComputeSettingsHash();

		var counts = volume.ProbeCounts;
		var bounds = volume.Bounds;
		var spacing = ComputeSpacing( bounds, counts );
		var radius = EffectiveRadius( spacing );
		var color = Tint * Intensity;
		var probeData = ReadProbeData( volume, counts );
		var truncated = 0;

		for ( int z = 0; z < counts.z; z++ )
		for ( int y = 0; y < counts.y; y++ )
		for ( int x = 0; x < counts.x; x++ )
		{
			if ( ProbeNoise( x, y, z ) >= LightDensity )
				continue;

			var offset = Vector3.Zero;

			if ( probeData is not null )
			{
				var flatIndex = (x + y * counts.x + z * counts.x * counts.y) * 4;

				if ( SkipInactiveProbes && (float)probeData[flatIndex + 3] <= 0.5f )
					continue;

				if ( UseRelocation )
					offset = new Vector3( (float)probeData[flatIndex], (float)probeData[flatIndex + 1], (float)probeData[flatIndex + 2] );
			}

			if ( _lights.Count >= MaxLights )
			{
				truncated++;
				continue;
			}

			var localPosition = bounds.Mins + new Vector3( x, y, z ) * spacing;
			var worldPosition = volume.WorldTransform.PointToWorld( localPosition ) + offset;

			var light = new ScenePointLight( Scene.SceneWorld, worldPosition, radius, color )
			{
				QuadraticAttenuation = Attenuation,
				ShadowsEnabled = Shadows,
				ShadowTextureResolution = ShadowResolution,
				RenderSpecular = Specular,
			};

			_lights.Add( light );
		}

		if ( truncated > 0 )
			Log.Warning( $"ProbeGridLights: {truncated} probes over the MaxLights cap ({MaxLights}) were dropped" );
	}

	/// <summary>Applies light settings to the existing lights without touching the grid.</summary>
	void ApplySettings( IndirectLightVolume volume )
	{
		if ( volume is null )
			return;

		var radius = EffectiveRadius( ComputeSpacing( volume.Bounds, volume.ProbeCounts ) );
		var color = Tint * Intensity;

		foreach ( var light in _lights )
		{
			if ( light is null )
				continue;

			light.LightColor = color;
			light.Radius = radius;
			light.QuadraticAttenuation = Attenuation;
			light.ShadowsEnabled = Shadows;
			light.ShadowTextureResolution = ShadowResolution;
			light.RenderSpecular = Specular;
		}
	}

	float EffectiveRadius( Vector3 spacing )
	{
		if ( Radius > 0f )
			return Radius;

		var auto = MathF.Max( spacing.x, MathF.Max( spacing.y, spacing.z ) );
		if ( auto <= 0f )
			auto = 128f;

		// Fewer lights = larger effective spacing between them (grows with the cube root of 1/density),
		// so the auto radius scales up to keep the volume covered.
		if ( LightDensity > 0f && LightDensity < 1f )
			auto /= MathF.Cbrt( LightDensity );

		return auto;
	}

	/// <summary>
	/// Deterministic per-probe noise in [0,1) from the grid position. Deliberately not HashCode.Combine —
	/// that's seeded per process, which would reshuffle the kept subset every editor restart.
	/// </summary>
	static float ProbeNoise( int x, int y, int z )
	{
		var h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791);
		h ^= h >> 13;
		h *= 0x5bd1e995u;
		h ^= h >> 15;
		return (h & 0xFFFFFF) / 16777216f;
	}

	/// <summary>Same spacing maths as the engine's IndirectLightVolume.ComputeSpacing (which is internal).</summary>
	static Vector3 ComputeSpacing( BBox bounds, Vector3Int counts )
	{
		var size = bounds.Size;
		return new Vector3(
			counts.x > 1 ? size.x / (counts.x - 1) : 0f,
			counts.y > 1 ? size.y / (counts.y - 1) : 0f,
			counts.z > 1 ? size.z / (counts.z - 1) : 0f );
	}

	/// <summary>
	/// Reads the baked relocation texture (RGBA16F: XYZ = world offset, W = active). Null when there's no bake
	/// or its dimensions don't match the current probe grid — callers fall back to the raw grid positions.
	/// </summary>
	static Half[] ReadProbeData( IndirectLightVolume volume, Vector3Int counts )
	{
		var texture = volume.RelocationTexture;
		if ( !texture.IsValid() )
			return null;

		if ( texture.Width != counts.x || texture.Height != counts.y || texture.Depth != counts.z )
			return null;

		var data = new Half[counts.x * counts.y * counts.z * 4];
		texture.GetPixels3D( (0, 0, 0, counts.x, counts.y, counts.z), 0, data.AsSpan(), ImageFormat.RGBA16161616F );
		return data;
	}
}
