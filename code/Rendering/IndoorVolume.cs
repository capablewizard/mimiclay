using System;
using System.Collections.Generic;
using Sandbox.Volumes;

namespace Mimiclay;

/// <summary>
/// Marks an interior: SDF props whose centre is inside get no SUN transmission (back-lit glow + sun
/// ambient fill). The shader skips the sun's whole transmission block for them (cascade sample + the
/// thickness march), and their ambient fill moves to the cluster lights as in a sun-less scene.
/// Fully indoors anywhere inside; fades back to full sun over <see cref="FadeDistance"/> inches OUTSIDE
/// the box, so props hugging a wall (or poking just through it) stay dark and a prop walking out of a
/// doorway doesn't pop. Outside any volume the sun's transmission is still shadow-masked per pixel, so windows,
/// doorways and fence shadows work without volumes — these are the cheap path for whole interiors, and
/// cover interiors past the sun's shadow distance where that per-pixel mask has nothing to read.
/// </summary>
[Title( "Indoor Volume" ), Category( "Rendering" ), Icon( "roofing" )]
public sealed class IndoorVolume : VolumeComponent, Component.ExecuteInEditor
{
	/// <summary>Inches outside the volume over which the sun's transmission fades back in.</summary>
	[Property, Range( 0f, 128f )] public float FadeDistance { get; set; } = 24f;

	/// <summary>
	/// Fit the box to the meshes/models on this object and its children (e.g. a sun shadow blocker),
	/// in this object's local space. Refits on enable, on property edits and from the Refit button —
	/// hand edits to the box are overwritten while this is on.
	/// </summary>
	[Property, Group( "Match Renderer" ), Title( "Match Renderer" )] public bool MatchRenderer { get; set; }

	/// <summary>Grows the fitted box on every side (inches).</summary>
	[Property, Group( "Match Renderer" ), ShowIf( nameof( MatchRenderer ), true )] public float Padding { get; set; } = 0f;

	/// <summary>Extra inches below the fitted box — for a roof-only blocker, reach down to the floor.</summary>
	[Property, Group( "Match Renderer" ), ShowIf( nameof( MatchRenderer ), true )] public float ExtendDown { get; set; } = 0f;

	protected override void OnValidate() { if ( MatchRenderer ) Refit(); }

	// Editor only: follow the blocker while it's being edited (mesh edits don't touch this component, so
	// OnValidate never fires for them). Throttled; in game the fit from OnEnabled stands (blockers are static).
	RealTimeSince _sinceFit;
	protected override void OnUpdate()
	{
		if ( MatchRenderer && !Game.IsPlaying && _sinceFit > 0.25f )
			Refit();
	}

	[Button( "Refit to Renderer", "fit_screen" ), Group( "Match Renderer" ), ShowIf( nameof( MatchRenderer ), true )]
	public void Refit()
	{
		_sinceFit = 0;
		BBox? fit = null;
		var self = WorldTransform;
		foreach ( var c in GameObject.Components.GetAll( FindMode.EverythingInSelfAndDescendants ) )
		{
			BBox local;
			Transform xf;
			if ( c is MeshComponent mc && mc.Mesh is not null ) { local = mc.Mesh.CalculateBounds(); xf = mc.WorldTransform; }
			else if ( c is ModelRenderer mr && mr.Model is not null ) { local = mr.Model.Bounds; xf = mr.WorldTransform; }
			else continue;

			// Corners into THIS object's local space (children may be offset/rotated).
			foreach ( var corner in local.Corners )
			{
				var p = self.PointToLocal( xf.PointToWorld( corner ) );
				fit = fit is { } b ? b.AddPoint( p ) : new BBox( p, p );
			}
		}

		if ( fit is not { } box )
		{
			Log.Warning( $"IndoorVolume on '{GameObject.Name}': Match Renderer found no MeshComponent or ModelRenderer." );
			return;
		}

		box = box.Grow( Padding );
		box.Mins.z -= MathF.Max( ExtendDown, 0f );

		var v = SceneVolume;
		if ( v.Type == SceneVolume.VolumeTypes.Box && v.Box.Mins.AlmostEqual( box.Mins, 0.01f ) && v.Box.Maxs.AlmostEqual( box.Maxs, 0.01f ) )
			return; // unchanged: don't dirty the scene every refit
		v.Type = SceneVolume.VolumeTypes.Box;
		v.Box = box;
		SceneVolume = v;
	}

	protected override void OnEnabled()
	{
		if ( MatchRenderer ) Refit();
	}

	/// <summary>1 = full sun transmission, 0 = fully indoors.</summary>
	// Asks the scene's component index directly. A static self-registry (OnEnabled/OnDisabled) was tried
	// first and came back EMPTY in the editor: a component that was already enabled when it gained
	// ExecuteInEditor (or across a hotload) never re-runs OnEnabled, so every prop read as outdoors.
	public static float SunExposure( Scene scene, Vector3 worldPosition )
	{
		if ( scene is null ) return 1f;
		float exposure = 1f;
		foreach ( var v in scene.GetAll<IndoorVolume>() )
		{
			if ( !v.Active ) continue;
			exposure = MathF.Min( exposure, v.ExposureAt( worldPosition ) );
			if ( exposure <= 0f ) return 0f;
		}
		return exposure;
	}

	/// <summary>0 inside, ramping to 1 at <see cref="FadeDistance"/> outside.</summary>
	float ExposureAt( Vector3 worldPosition )
	{
		var vol = SceneVolume;
		if ( IsInfinite ) return 0f;
		var local = WorldTransform.PointToLocal( worldPosition );

		// TRUE distance outside the shape. Not SceneVolume.GetEdgeDistance: for a box that's the distance
		// to the nearest face PLANE, which is tiny for any point lined up with a face, inside or out.
		float outside;
		switch ( vol.Type )
		{
			case SceneVolume.VolumeTypes.Box:
				var b = vol.Box;
				var d = Vector3.Max( Vector3.Max( b.Mins - local, local - b.Maxs ), Vector3.Zero );
				outside = d.Length;
				break;
			case SceneVolume.VolumeTypes.Sphere:
				outside = MathF.Max( local.Distance( vol.Sphere.Center ) - vol.Sphere.Radius, 0f );
				break;
			default: // capsule: inside/outside only
				outside = vol.Test( WorldTransform, worldPosition ) ? 0f : float.MaxValue;
				break;
		}

		if ( outside <= 0f ) return 0f;
		if ( FadeDistance <= 0f ) return 1f;
		return Math.Clamp( outside / FadeDistance, 0f, 1f );
	}
}
