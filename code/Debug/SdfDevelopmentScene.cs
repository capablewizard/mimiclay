using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>Persistent 256-prop stress fixture. Also the workload for the timed benchmark (Editor/SdfBench.cs).</summary>
[Title( "SDF Development Controls" ), Category( "SDF/Debug" ), Icon( "science" )]
public sealed class SdfDevelopmentScene : Component
{
	[Property] public CameraComponent Camera { get; set; }
	[Property] public List<GameObject> Props { get; set; } = new();
	[Property] public List<SpotLight> Lights { get; set; } = new();
	[Property] public Vector3 GalleryPosition { get; set; }
	[Property] public Rotation GalleryRotation { get; set; } = Rotation.Identity;

	/// <summary>-1: dense reference; -2: overview; -3: close crowd. 0–255: individual prop.</summary>
	[Property, Range( -3, 255 )] public int View { get; set; } = -1;
	/// <summary>Camera distance in geometry bounding-sphere radii. 1 is the pixel-pressure close-up.</summary>
	[Property, Range( 1, 4 )] public float CloseDistance { get; set; } = 1.3f;
	[Property, Range( 0, 8 )] public int LightCount { get; set; } = 4;
	/// <summary>Enable local light shadow maps.</summary>
	[Property] public bool Shadows { get; set; } = true;
	/// <summary>In Play, force all 256 props to cast raymarched shadows. Restores the original global cap on exit.</summary>
	[Property] public bool ForceSdfShadows { get; set; } = true;
	readonly Dictionary<SdfRaymarchRenderer, (bool Shadows, float Radii)> _shadowSettings = new();
	float? _savedShadowCap;

	int _lastView = int.MinValue, _lastLights = -1;
	float _lastDistance = -1;
	bool _lastShadows;

	protected override void OnStart() => Apply( true );
	protected override void OnUpdate() => Apply();
	protected override void OnDisabled() => RestoreShadows();
	protected override void OnDestroy() => RestoreShadows();

	void RestoreShadows()
	{
		if ( _savedShadowCap is null ) return;
		SdfRaymarchRenderer.ShadowRadiiCap = _savedShadowCap.Value;
		_savedShadowCap = null;
		foreach ( var (renderer, settings) in _shadowSettings )
			if ( renderer.IsValid() ) { renderer.SdfShadows = settings.Shadows; renderer.SdfShadowRadii = settings.Radii; }
		_shadowSettings.Clear();
	}

	public void Apply( bool force = false )
	{
		if ( !Camera.IsValid() ) return;
		if ( !Scene.IsEditor )
		{
			if ( ForceSdfShadows && _savedShadowCap is null )
			{
				_savedShadowCap = SdfRaymarchRenderer.ShadowRadiiCap;
				foreach ( var r in Scene.GetAllComponents<SdfRaymarchRenderer>() )
				{
					_shadowSettings[r] = (r.SdfShadows, r.SdfShadowRadii);
					r.SdfShadows = true; r.SdfShadowRadii = 0;
				}
				SdfRaymarchRenderer.ShadowRadiiCap = 0;
			}
			else if ( !ForceSdfShadows ) RestoreShadows();
		}
		View = Math.Clamp( View, -3, Props.Count - 1 );
		LightCount = Math.Clamp( LightCount, 0, Lights.Count );
		CloseDistance = Math.Clamp( CloseDistance, 1, 4 );
		if ( force || View != _lastView || CloseDistance != _lastDistance )
		{
			var fly = Camera.Components.Get<FlyCamera>();
			bool flying = fly.IsValid() && fly.Enabled;
			if ( flying ) fly.Enabled = false;
			if ( View < 0 )
			{
				Camera.WorldPosition = GalleryPosition + GalleryRotation.Forward * (View == -2 ? -320 : View == -3 ? 120 : 0)
					+ (View == -3 ? Vector3.Up * 100 : Vector3.Zero);
				Camera.WorldRotation = GalleryRotation;
			}
			else if ( Props[View].IsValid() )
			{
				var b = SdfBenchProbe.GeometryBounds( Props[View] );
				var radius = MathF.Max( b.Size.Length * .5f, 2 );
				Camera.WorldPosition = b.Center + new Vector3( -1, -.35f, .25f ).Normal * radius * CloseDistance;
				Camera.WorldRotation = Rotation.LookAt( b.Center - Camera.WorldPosition );
			}
			// FlyCamera caches its angles in OnEnabled; resync after a fixed-view jump.
			if ( flying ) fly.Enabled = true;
			_lastView = View; _lastDistance = CloseDistance;
		}
		if ( force || LightCount != _lastLights || Shadows != _lastShadows )
		{
			for ( int i = 0; i < Lights.Count; i++ )
			{
				if ( !Lights[i].IsValid() ) continue;
				Lights[i].Shadows = Shadows;
				Lights[i].Enabled = i < LightCount;
			}
			_lastLights = LightCount; _lastShadows = Shadows;
		}
		// The engine's static shadow cache needs static lights; the spots follow the ConVar so the bench can A/B it
		// (its spot cases toggle mimiclay_sdf_static_shadows). Play only: never dirties the saved scene.
		if ( !Scene.IsEditor )
			foreach ( var light in Lights )
				if ( light.IsValid() && light.GameObject.IsStatic != SdfStaticShadows.Enabled )
					light.GameObject.IsStatic = SdfStaticShadows.Enabled;
	}

	[Button( "Dense 256 / Reset Camera" )]
	public void Gallery() { View = -1; Apply( true ); }
	[Button( "Overview" )]
	public void Overview() { View = -2; Apply( true ); }
	[Button( "Close Crowd / Pixel Pressure" )]
	public void Extreme() { View = -3; Apply( true ); }
	[Button( "Complex Close-up" )]
	public void Complex() { View = Math.Min( 63, Props.Count - 1 ); Apply( true ); }
	[Button( "Next Prop" )]
	public void Next() { View = Props.Count == 0 ? -1 : Math.Max( 0, View + 1 ) % Props.Count; Apply( true ); }
	[Button( "Cycle Lights: 0 / 1 / 4 / 8" )]
	public void CycleLights() { LightCount = LightCount == 0 ? 1 : LightCount == 1 ? 4 : LightCount == 4 ? 8 : 0; Apply(); }

	static SdfDevelopmentScene Current() => Game.ActiveScene?.GetAllComponents<SdfDevelopmentScene>().FirstOrDefault();

	[ConCmd( "sdf_dev_view" )]
	public static void SetView( int index = -1, float distance = 1.3f )
	{
		var dev = Current();
		if ( dev is null ) { Log.Warning( "Play scenes/sdf_development.scene first." ); return; }
		dev.View = index; dev.CloseDistance = distance; dev.Apply( true );
		Log.Info( $"[sdf-dev] view={dev.View}, close distance={dev.CloseDistance:0.0} radii" );
	}

	[ConCmd( "sdf_dev_lights" )]
	public static void SetLights( int count = 1, bool shadows = true )
	{
		var dev = Current();
		if ( dev is null ) { Log.Warning( "Play scenes/sdf_development.scene first." ); return; }
		dev.LightCount = count; dev.Shadows = shadows; dev.Apply();
		Log.Info( $"[sdf-dev] {dev.LightCount} local spots, shadows={dev.Shadows}, forced SDF casters={dev.ForceSdfShadows}" );
	}

	[ConCmd( "sdf_dev_shadow_policy" )]
	public static void SetShadowPolicy( bool force = true )
	{
		var dev = Current();
		if ( dev is null ) { Log.Warning( "Play scenes/sdf_development.scene first." ); return; }
		dev.ForceSdfShadows = force; dev.Apply();
	}

	[ConCmd( "sdf_dev_stats" )]
	public static void Stats()
	{
		var dev = Current();
		if ( dev is null ) { Log.Warning( "Play scenes/sdf_development.scene first." ); return; }
		Log.Info( Json.Serialize( SdfBenchProbe.Snapshot( dev.Scene ) ) );
	}
}
