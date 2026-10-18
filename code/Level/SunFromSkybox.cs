using System;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Aims this GameObject's directional light at the brightest spot of a <see cref="SkyBox2D"/>'s texture, so the
/// sun light always agrees with the sun painted in the sky. Put it on the light, parented under the skybox (or point
/// <see cref="Skybox"/> at one). It re-scans when the sky material's texture changes and re-aims whenever the
/// skybox object rotates, in the editor and in play.
///
/// The sky material compiles its equirect source into a CUBEMAP (six 512² faces), which is what
/// <see cref="SkyBox2D.SkyTexture"/> hands back. Engine shaders sample it with the raw world direction
/// (envmap_filtering.hlsl, vr_environment_map.fxc: no swizzle), so the standard D3D/Vulkan face layout maps each
/// texel straight to a skybox-local direction. The scan reads every face back as floats at a small mip, takes each
/// texel within <see cref="HotspotFraction"/> of the peak luminance, and sums their unit directions weighted by
/// luminance; the mean vector is the sun. A plain 2D equirect texture is handled too, with the sky shader's
/// convention measured in the editor (yaw = -u × 360°, elevation = 90° − v × 180°, v from the top row).
/// </summary>
[Title( "Sun From Skybox" ), Category( "Mimiclay" ), Icon( "wb_sunny" )]
public sealed class SunFromSkybox : Component, Component.ExecuteInEditor
{
	/// <summary>The skybox to read. Empty = the nearest <see cref="SkyBox2D"/> up the hierarchy.</summary>
	[Property] public SkyBox2D Skybox { get; set; }

	/// <summary>Keep the light aimed as the sky texture or skybox rotation changes. Off = only "Align Now".</summary>
	[Property] public bool AutoAlign { get; set; } = true;

	/// <summary>Texels at least this fraction of the peak luminance count as the sun. Lower pulls in more halo.</summary>
	[Property, Range( 0.1f, 1f )] public float HotspotFraction { get; set; } = 0.5f;

	/// <summary>Where the scan found the sun, skybox-local: heading (yaw, degrees) and height above the horizon.</summary>
	[Property, ReadOnly, Group( "Result" )] public float SunYaw { get; private set; }
	[Property, ReadOnly, Group( "Result" )] public float SunElevation { get; private set; }
	[Property, ReadOnly, Group( "Result" )] public float PeakLuminance { get; private set; }
	[Property, ReadOnly, Group( "Result" )] public string ScannedTexture { get; private set; }

	Vector3 _sunDirLocal = Vector3.Up;   // towards the sun, skybox-local
	string _scannedKey;                 // texture identity the cached direction belongs to
	Rotation _appliedSkyRotation;
	bool _applied;
	float _hotspotUsed;

	SkyBox2D ResolveSkybox() => Skybox.IsValid() ? Skybox : Components.GetInAncestors<SkyBox2D>( true );

	protected override void OnEnabled()
	{
		_scannedKey = null;
		_applied = false;
	}

	protected override void OnUpdate()
	{
		if ( !AutoAlign ) return;
		Tick( force: false );
	}

	[Button( "Align Now", "wb_sunny" )]
	public void AlignNow()
	{
		_scannedKey = null;
		Tick( force: true );
	}

	void Tick( bool force )
	{
		var sky = ResolveSkybox();
		if ( !sky.IsValid() ) return;

		var tex = sky.SkyTexture;
		if ( tex is null || tex.Width <= 0 || tex.Height <= 0 ) return;

		var key = $"{tex.ResourceId}:{tex.Width}x{tex.Height}x{tex.Depth}:{sky.SkyMaterial?.ResourcePath}";
		if ( key != _scannedKey || !_hotspotUsed.AlmostEqual( HotspotFraction ) )
		{
			if ( !Scan( tex ) ) return;
			_scannedKey = key;
			_hotspotUsed = HotspotFraction;
			_applied = false;
		}

		var skyRot = sky.WorldRotation;
		if ( _applied && !force && skyRot == _appliedSkyRotation ) return;

		var sunDirWorld = (skyRot * _sunDirLocal).Normal;
		WorldRotation = Rotation.LookAt( -sunDirWorld, MathF.Abs( sunDirWorld.z ) > 0.999f ? Vector3.Forward : Vector3.Up );
		_appliedSkyRotation = skyRot;
		_applied = true;
	}

	/// <summary>Find the sun in the sky texture and cache its skybox-local direction.</summary>
	bool Scan( Texture tex )
	{
		// A ≤512-wide read is plenty to locate a sun disc and keeps the readback small.
		int mip = 0;
		int w = tex.Width, h = tex.Height;
		while ( w > 512 && mip + 1 < tex.Mips ) { mip++; w = Math.Max( 1, w >> 1 ); h = Math.Max( 1, h >> 1 ); }

		bool isCube = tex.Depth == 6 && tex.Width == tex.Height;
		int slices = isCube ? 6 : 1;
		var faces = new Vector4[slices][];

		try
		{
			for ( int s = 0; s < slices; s++ )
			{
				faces[s] = new Vector4[w * h];
				tex.GetPixels<Vector4>( (0, 0, w, h), s, mip, faces[s], ImageFormat.RGBA32323232F );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[SunFromSkybox] Couldn't read '{tex.ResourcePath}' as float pixels ({e.Message})" );
			return false;
		}

		float peak = 0f;
		foreach ( var face in faces )
			foreach ( var p in face )
				peak = MathF.Max( peak, Luma( p ) );

		if ( peak <= 0f )
		{
			Log.Warning( $"[SunFromSkybox] '{tex.ResourcePath}' is black, nothing to aim at" );
			return false;
		}

		// Luminance-weighted mean of the hotspot texels' unit directions. Seam- and face-boundary-proof.
		float threshold = peak * HotspotFraction;
		Vector3 sum = Vector3.Zero;
		for ( int s = 0; s < slices; s++ )
		{
			var face = faces[s];
			for ( int y = 0; y < h; y++ )
			{
				for ( int x = 0; x < w; x++ )
				{
					float l = Luma( face[y * w + x] );
					if ( l < threshold ) continue;

					float u = (x + 0.5f) / w;
					float v = (y + 0.5f) / h;
					sum += l * (isCube ? CubeTexelDirection( s, u, v ) : EquirectDirection( u, v ));
				}
			}
		}

		if ( sum.LengthSquared < 1e-12f ) return false;
		_sunDirLocal = sum.Normal;

		SunYaw = MathF.Atan2( _sunDirLocal.y, _sunDirLocal.x ).RadianToDegree();
		SunElevation = MathF.Asin( Math.Clamp( _sunDirLocal.z, -1f, 1f ) ).RadianToDegree();
		PeakLuminance = peak;
		var name = string.IsNullOrEmpty( tex.ResourcePath ) ? (string.IsNullOrEmpty( tex.ResourceName ) ? "(generated)" : tex.ResourceName) : tex.ResourcePath;
		ScannedTexture = $"{name} ({(isCube ? "cube" : "2D")}, mip {mip}, {w}x{h})";
		return true;
	}

	/// <summary>Standard cubemap face layout (D3D/Vulkan), texel (u,v) with v from the top row → sampling direction.</summary>
	static Vector3 CubeTexelDirection( int face, float u, float v )
	{
		float sc = u * 2f - 1f;
		float tc = v * 2f - 1f;
		return face switch
		{
			0 => new Vector3( 1f, -tc, -sc ),   // +X
			1 => new Vector3( -1f, -tc, sc ),   // -X
			2 => new Vector3( sc, 1f, tc ),     // +Y
			3 => new Vector3( sc, -1f, -tc ),   // -Y
			4 => new Vector3( sc, -tc, 1f ),    // +Z
			_ => new Vector3( -sc, -tc, -1f ),  // -Z
		};
	}

	/// <summary>Sky shader equirect convention (verified in-editor): yaw = -u·360, elevation = 90 − v·180 (v from top).</summary>
	static Vector3 EquirectDirection( float u, float v )
	{
		float yaw = (-u * 360f).DegreeToRadian();
		float el = (90f - v * 180f).DegreeToRadian();
		float ce = MathF.Cos( el );
		return new Vector3( ce * MathF.Cos( yaw ), ce * MathF.Sin( yaw ), MathF.Sin( el ) );
	}

	static float Luma( in Vector4 c ) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
}
