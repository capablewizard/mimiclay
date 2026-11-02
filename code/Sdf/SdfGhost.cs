using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Draws every <see cref="SdfRaymarchRenderer"/> on this GameObject and its descendants as ONE translucent, glowing
/// ghost (shaders/sdf_ghost.shader) instead of clay — meant for eliminated players in the spectator cam. The members'
/// own surfaces (and shadows) are switched off via <see cref="SdfRaymarchRenderer.Ghosted"/> while this is enabled;
/// their baked fields are re-marched as a union, the same field-only scheme as <see cref="SdfHighlightOutline"/>, so
/// the ghost needs <see cref="SdfRaymarchRenderer.UseFieldCache"/> and shows nothing until the first bake lands.
/// At most <see cref="MaxTargets"/> members (shader slot count).
/// </summary>
[Title( "SDF Ghost" )]
[Category( "SDF" )]
[Icon( "blur_on" )]
public sealed class SdfGhost : Component, Component.ExecuteInEditor
{
	/// <summary>Shader field-slot count — must match sdf_field_union.hlsl.</summary>
	public const int MaxTargets = SdfHighlightOutline.MaxTargets;

	/// <summary>Body colour; alpha scales the whole ghost's opacity.</summary>
	[Property, Group( "Colour" )] public Color BodyColor { get; set; } = new( 0.27695f, 0.80465f, 0.5408f, 1f );

	/// <summary>Colour of the fresnel rim and the halo.</summary>
	[Property, Group( "Colour" )] public Color RimColor { get; set; } = new( 0.6093f, 1f, 0.76558f, 1f );

	/// <summary>Emissive multiplier — above 1 feeds bloom.</summary>
	[Property, Group( "Colour" ), Range( 0f, 8f )] public float Brightness { get; set; } = 1.5f;

	/// <summary>Rim falloff: higher = thinner, sharper edge glow.</summary>
	[Property, Group( "Rim" ), Range( 0.5f, 8f )] public float RimPower { get; set; } = 1.8f;

	[Property, Group( "Rim" ), Range( 0f, 2f )] public float RimStrength { get; set; } = 1.5f;

	/// <summary>Opacity floor where the surface faces the camera.</summary>
	[Property, Group( "Body" ), Range( 0f, 1f )] public float BaseOpacity { get; set; } = 0.1f;

	/// <summary>Opacity per world unit of thickness the ray passes through — thick parts read denser.</summary>
	[Property, Group( "Body" ), Range( 0f, 0.3f )] public float Density { get; set; } = 0.02f;

	/// <summary>How far (world units) the aura glows past the silhouette. The proxy box grows to fit it.</summary>
	[Property, Group( "Halo" ), Range( 0f, 24f )] public float HaloRadius { get; set; } = 4f;

	[Property, Group( "Halo" ), Range( 0f, 1f )] public float HaloStrength { get; set; } = 0.4f;

	/// <summary>Smoke noise frequency per world unit — smaller = bigger, softer blobs.</summary>
	[Property, Group( "Smoke" ), Range( 0.005f, 0.3f )] public float SmokeScale { get; set; } = 0.1f;

	/// <summary>Upward drift of the smoke through the body, world units per second.</summary>
	[Property, Group( "Smoke" ), Range( 0f, 40f )] public float SmokeSpeed { get; set; } = 14f;

	/// <summary>How strongly the smoke modulates the ghost's opacity (0 = smooth).</summary>
	[Property, Group( "Smoke" ), Range( 0f, 1f )] public float SmokeStrength { get; set; } = 0.75f;

	/// <summary>Fraction of the group's height that fades out at the bottom (0 = no fade).</summary>
	[Property, Group( "Shape" ), Range( 0f, 1f )] public float FadeHeight { get; set; } = 0.65f;

	/// <summary>Opacity where a wall is in front of the ghost (0 = walls hide it).</summary>
	[Property, Group( "Shape" ), Range( 0f, 1f )] public float ObscuredOpacity { get; set; }

	/// <summary>Runtime hide gate: true = draw nothing, while the members stay ghosted (no clay either). Set per
	/// machine by whoever owns the visibility rules (<see cref="SpectatorGhost"/>), the same contract as
	/// <see cref="SdfHighlightOutline.Hidden"/>: never hide a ghost by disabling the component. Not serialized.</summary>
	public bool Hidden { get; set; }

	SceneObject _so;
	Material _material;
	readonly List<SdfRaymarchRenderer> _targets = new();
	readonly List<SdfRaymarchRenderer> _ready = new();
	int _lastBuildHash;
	Vector3 _builtMins, _builtMaxs;
	RealTimeUntil _nextScan;

	const float ScanInterval = 0.25f;

	protected override void OnEnabled()
	{
		_lastBuildHash = 0;
		ScanTargets();
	}

	protected override void OnDisabled()
	{
		foreach ( var r in _targets )
		{
			if ( r.IsValid() )
				r.Ghosted = false;
		}
		_targets.Clear();
		_so?.Delete();
		_so = null;
	}

	protected override void OnDestroy()
	{
		_so?.Delete();
		_so = null;
	}

	void ScanTargets()
	{
		_nextScan = ScanInterval;

		foreach ( var r in _targets )
		{
			if ( r.IsValid() )
				r.Ghosted = false;
		}
		_targets.Clear();

		foreach ( var r in Components.GetAll<SdfRaymarchRenderer>( FindMode.EnabledInSelfAndDescendants ) )
		{
			if ( _targets.Count == MaxTargets )
			{
				Log.Warning( $"[SdfGhost] {GameObject.Name}: more than {MaxTargets} SDF renderers — extras keep their clay look." );
				break;
			}
			r.Ghosted = true;
			_targets.Add( r );
		}
	}

	protected override void OnUpdate()
	{
		if ( _nextScan <= 0f )
			ScanTargets();
	}

	// After every OnUpdate (members included), so the mirrored field bindings and transforms are same-frame.
	protected override void OnPreRender()
	{
		_ready.Clear();
		foreach ( var r in _targets )
		{
			if ( r.IsValid() && r.Enabled && r.HighlightReady )
				_ready.Add( r );
		}

		if ( _ready.Count == 0 || Hidden )
		{
			if ( _so.IsValid() )
				_so.RenderingEnabled = false;
			return;
		}

		_material ??= Material.FromShader( "shaders/sdf_ghost.shader" );

		// One local box around every member's proxy, padded for the halo (see SdfHighlightOutline.SyncFromRenderer).
		var h = new HashCode();
		h.Add( _ready.Count );
		h.Add( HaloRadius );
		foreach ( var r in _ready )
		{
			h.Add( r.Id );
			h.Add( r.ProxyVersion );
		}
		int hash = h.ToHashCode();

		var myTx = WorldTransform;
		var lmins = new Vector3( float.MaxValue );
		var lmaxs = new Vector3( float.MinValue );
		foreach ( var r in _ready )
		{
			var rtx = r.WorldTransform;
			var rmins = r.ProxyLocalMins;
			var rmaxs = r.ProxyLocalMaxs;
			for ( int c = 0; c < 8; c++ )
			{
				var corner = new Vector3(
					(c & 1) != 0 ? rmaxs.x : rmins.x,
					(c & 2) != 0 ? rmaxs.y : rmins.y,
					(c & 4) != 0 ? rmaxs.z : rmins.z );
				var local = myTx.PointToLocal( rtx.PointToWorld( corner ) );
				lmins = Vector3.Min( lmins, local );
				lmaxs = Vector3.Max( lmaxs, local );
			}
		}
		float pad = HaloRadius * 2f;
		lmins -= pad;
		lmaxs += pad;

		bool fits = lmins.x >= _builtMins.x && lmins.y >= _builtMins.y && lmins.z >= _builtMins.z
			&& lmaxs.x <= _builtMaxs.x && lmaxs.y <= _builtMaxs.y && lmaxs.z <= _builtMaxs.z;

		if ( !_so.IsValid() || _lastBuildHash != hash || !fits )
		{
			const float slack = 4f;
			_builtMins = lmins - slack;
			_builtMaxs = lmaxs + slack;

			var model = SdfRaymarchRenderer.BuildHighlightProxyBox( _builtMins, _builtMaxs, _material );
			if ( _so.IsValid() )
			{
				_so.Model = model;
			}
			else
			{
				_so = new SceneObject( Scene.SceneWorld, model );
				_so.Batchable = false;
				_so.Flags.CastShadows = false;
			}
			_lastBuildHash = hash;
		}

		_so.Transform = myTx;
		var wmin = new Vector3( float.MaxValue );
		var wmax = new Vector3( float.MinValue );
		for ( int c = 0; c < 8; c++ )
		{
			var corner = myTx.PointToWorld( new Vector3(
				(c & 1) != 0 ? _builtMaxs.x : _builtMins.x,
				(c & 2) != 0 ? _builtMaxs.y : _builtMins.y,
				(c & 4) != 0 ? _builtMaxs.z : _builtMins.z ) );
			wmin = Vector3.Min( wmin, corner );
			wmax = Vector3.Max( wmax, corner );
		}
		_so.Bounds = new BBox( wmin, wmax );

		int maxSteps = 0;
		float epsilon = float.MaxValue;
		foreach ( var r in _ready )
		{
			maxSteps = Math.Max( maxSteps, r.MaxSteps );
			epsilon = MathF.Min( epsilon, r.Epsilon );
		}

		var a = _so.Attributes;
		a.Set( "FieldCount", _ready.Count );
		a.Set( "BoundsMin", wmin );
		a.Set( "BoundsMax", wmax );
		a.Set( "MaxSteps", maxSteps );
		a.Set( "Epsilon", epsilon );

		a.Set( "GhostBodyColor", BodyColor );
		a.Set( "GhostRimColor", RimColor );
		a.Set( "GhostBrightness", Brightness );
		a.Set( "GhostRimPower", RimPower );
		a.Set( "GhostRimStrength", RimStrength );
		a.Set( "GhostBaseOpacity", BaseOpacity );
		a.Set( "GhostDensity", Density );
		a.Set( "GhostHaloRadius", HaloRadius );
		a.Set( "GhostHaloStrength", HaloStrength );
		a.Set( "GhostSmokeScale", SmokeScale );
		a.Set( "GhostSmokeSpeed", SmokeSpeed );
		a.Set( "GhostSmokeStrength", SmokeStrength );
		a.Set( "GhostOrigin", myTx.Position );
		a.Set( "GhostRotation", new Vector4( myTx.Rotation.x, myTx.Rotation.y, myTx.Rotation.z, myTx.Rotation.w ) );
		a.Set( "GhostFadeHeight", FadeHeight );
		a.Set( "GhostObscuredOpacity", ObscuredOpacity );

		for ( int slot = 0; slot < _ready.Count; slot++ )
			_ready[slot].ApplyHighlightAttributes( _so, slot );
		for ( int slot = _ready.Count; slot < MaxTargets; slot++ )
			a.Set( $"DispGradL{slot}", 0f );

		_so.RenderingEnabled = true;
	}
}
