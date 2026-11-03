using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// A free-cam player's ghost: their head, drawn by <see cref="SdfGhost"/> instead of as clay, floating where their
/// camera is. Lives on the root of prefabs/ghost.prefab, spawned and owned by the player's
/// <see cref="SpectatorController"/>. The owner moves it (<see cref="Follow"/>) and the networked transform carries
/// it to everyone else; the head shape rides the prefab's <see cref="SdfNetworkSync"/>.
///
/// Who draws it is decided per machine, every frame (<see cref="Visibility"/>): the owner always (from its own third
/// person), everyone else per <see cref="GhostRules"/> — plus a spawn delay where it could give a body away (a living
/// prop's ghost appears where its body is, so it gets <see cref="SpawnHideSeconds"/> to get clear), and a fade out
/// on every machine when the owner leaves the free cam (<see cref="Dismiss"/>). Faded through
/// <see cref="SdfGhost.Opacity"/> / <see cref="SdfGhost.Hidden"/>, never by disabling anything.
/// </summary>
[Title( "Spectator Ghost" )]
[Category( "Mimiclay" )]
[Icon( "blur_on" )]
public sealed class SpectatorGhost : Component
{
	/// <summary>Head size relative to the player's real head. Applied to the brushes with the group-scaling rules
	/// (<see cref="SdfScaling.ScaleUniform"/>), so blends and rounding stay in proportion, and the result is what
	/// the network sync ships. Set on the prefab: the owner applies it to its own ghost (and re-applies if it changes
	/// there). The prefab root's transform scale multiplies in too — the SDF shaders ignore object scale, so it's
	/// folded into the brushes and the transform kept at 1.</summary>
	[Property, Range( 0.25f, 3f )] public float Scale { get; set; } = 0.75f;

	/// <summary>How fast the head turns to follow the look (per second, exponential) — the lag the hunter's head
	/// and gun have in third person (<see cref="HunterController.AimEaseRate"/>). 0 = locked to the look.</summary>
	[Property, Range( 0f, 30f )] public float TurnEaseRate { get; set; } = 10f;

	/// <summary>How long a living prop's ghost stays invisible to those it's hidden from after it appears
	/// (<see cref="GhostRules.SpawnDelayed"/>) — time to get away from the body it spawned beside.</summary>
	[Property, Group( "Visibility" ), Range( 0f, 15f )] public float SpawnHideSeconds { get; set; } = 3f;

	/// <summary>The fade in every ghost gets as it appears, on every machine, when there's no spawn delay (seconds).</summary>
	[Property, Group( "Visibility" ), Range( 0f, 5f )] public float AppearFadeSeconds { get; set; } = 1f;

	/// <summary>The fade in once that delay is up (seconds).</summary>
	[Property, Group( "Visibility" ), Range( 0f, 5f )] public float SpawnFadeSeconds { get; set; } = 1f;

	/// <summary>The fade out when its owner leaves the free cam (seconds) — the ghost doesn't pop where they left.</summary>
	[Property, Group( "Visibility" ), Range( 0f, 3f )] public float ExitFadeSeconds { get; set; } = 0.25f;

	/// <summary>How long the glow takes to come up once the ghost shows (seconds). Going out, it follows the ghost.</summary>
	[Property, Group( "Visibility" ), Range( 0f, 10f )] public float LightFadeInSeconds { get; set; } = 3f;

	/// <summary>The owner has left the free cam: every machine fades the ghost out, then the owner destroys it.</summary>
	[Sync] public bool Leaving { get; private set; }

	/// <summary>The glow's full intensity, as authored on the prefab — captured by the owner before it fades anything,
	/// and synced so a proxy never mistakes a mid-fade spawn snapshot for the real value.</summary>
	[Sync] public float LightPeak { get; private set; } = -1f;

	SdfSculpture _head;
	RealTimeSince _sinceAppeared;  // per machine: since this ghost appeared HERE (the spawn delay counts from this)
	RealTimeSince _sinceLeaving;   // per machine: since we first saw Leaving
	bool _sawLeaving;
	float _visibility;             // 0..1, this frame
	Rotation? _turn;             // owner: the eased facing (null until the first Follow, which snaps)
	SdfGhost _look;
	List<SdfBrush> _base;        // owner: the head before scaling
	float _appliedScale = float.NaN;
	float? _transformScale;      // owner: the authored root scale, captured once, then reset to 1
	Vector3 _headCentre;         // owner: the clay's centre in the head's frame, after scaling
	PointLight _light;           // the "Light" child's glow (lazy — hotload-safe)
	float _lightFade;            // 0..1, eased toward visible here

	float EffectiveScale => Scale * (_transformScale ?? 1f);

	SdfSculpture Head => _head.IsValid() ? _head : (_head = HunterController.ResolveFaceOf( GameObject ));

	/// <summary>The ghost's head sculpture (nameplates anchor above it).</summary>
	public SdfSculpture HeadSculpture => Head;

	/// <summary>How visible this ghost is on this machine this frame, 0..1 (see the class notes).</summary>
	public float Visibility => _visibility;

	/// <summary>Is this ghost drawn on this machine at all?</summary>
	public bool Shown => _visibility > 0f;

	/// <summary>The side of the player who owns this ghost (<see cref="GhostRules"/>).</summary>
	public GhostTeam OwnerTeam => GhostRules.TeamOf( Network.Owner?.Id );

	// Our own ghost, being orbited by our third-person free cam.
	bool OwnedAndOrbited => SpectatorController.Local is { } cam && cam.Ghost == GameObject && cam.ShowsOwnGhost;

	/// <summary>Owner: the free cam is over — fade out everywhere, then go (<see cref="ExitFadeSeconds"/>).</summary>
	public void Dismiss()
	{
		if ( IsProxy || Leaving )
			return;

		Leaving = true;
		_sawLeaving = true;
		_sinceLeaving = 0f;
	}

	protected override void OnStart()
	{
		_sinceAppeared = 0f;

		// Every machine binds its own copy of the head to the sync (Target is local config, never synced).
		var sync = Components.Get<SdfNetworkSync>();
		if ( sync.IsValid() && Head.IsValid() )
			sync.Target = Head;

		UpdateLight(); // dark from the first frame — the glow fades up from here
	}

	/// <summary>Owner: (re)apply <see cref="Scale"/> to the head, from its unscaled shape. Called once the head is
	/// dressed, and again whenever Scale changes.</summary>
	public void ApplyScale()
	{
		var head = Head;
		if ( !head.IsValid() || head.Brushes is not { Count: > 0 } )
			return;

		if ( _transformScale is null )
		{
			_transformScale = SdfScaling.MeanScale( WorldScale );
			WorldScale = Vector3.One;
		}

		_base ??= head.Brushes.Select( b => b.Copy() ).ToList();
		var scaled = _base.Select( b => b.Copy() ).ToList();
		var factor = EffectiveScale;

		if ( Sdf.TryGetBounds( scaled, out var bounds ) )
		{
			var pivot = SdfScaling.SymmetricPivot( scaled, bounds.Center );
			if ( MathF.Abs( factor - 1f ) > 1e-4f )
			{
				foreach ( var b in scaled )
					SdfScaling.ScaleUniform( b, pivot, factor );
			}
			_headCentre = pivot + (bounds.Center - pivot) * factor;
		}

		head.Brushes = scaled;
		_appliedScale = Scale;

		// Rebuild raises Committed, which the network sync publishes. A disabled pre-spawn clone skips it:
		// enabling the ghost starts the first build (and the sync seeds itself once networked).
		if ( head.Active )
			head.Rebuild();
	}

	/// <summary>Owner: put the head's centre on the camera, turning (eased, see <see cref="TurnEaseRate"/>) to face
	/// where it looks. The centre stays put while it turns, so the lag reads as the head catching up, not drifting.</summary>
	public void Follow( Vector3 eye, Rotation view )
	{
		var turn = _turn is { } from && TurnEaseRate > 0f
			? Rotation.Slerp( from, view, 1f - MathF.Exp( -TurnEaseRate * MathF.Min( Time.Delta, 0.1f ) ) )
			: view;
		_turn = turn;

		WorldTransform = new Transform( eye - turn * _headCentre, turn );
	}

	protected override void OnUpdate()
	{
		if ( !IsProxy && Scale != _appliedScale )
			ApplyScale();

		_visibility = ComputeVisibility();

		_look ??= Components.Get<SdfGhost>();
		if ( _look.IsValid() )
		{
			_look.Hidden = _visibility <= 0f;
			_look.Opacity = _visibility;
		}

		UpdateLight();

		// The owner removes the ghost once everyone has had the exit fade.
		if ( !IsProxy && Leaving && _sinceLeaving > ExitFadeSeconds )
			GameObject.Destroy();
	}

	// 1 → 0 over the exit fade once the owner has left (counted from when this machine saw it), else 1.
	float ExitFade => Leaving ? 1f - Smooth( _sinceLeaving / MathF.Max( ExitFadeSeconds, 0.001f ) ) : 1f;

	float ComputeVisibility()
	{
		if ( Leaving && !_sawLeaving )
		{
			_sawLeaving = true;
			_sinceLeaving = 0f;
		}
		float exit = ExitFade;
		float appear = Smooth( _sinceAppeared / MathF.Max( AppearFadeSeconds, 0.001f ) );

		// Our own — always, whatever "Show ghosts" says — but only from our third person (first person sits inside it).
		if ( !IsProxy )
			return OwnedAndOrbited ? appear * exit : 0f;

		var owner = OwnerTeam;
		if ( !GhostRules.MaySee( owner ) )
			return 0f;

		// A living prop's ghost waits out the spawn delay for anyone who could learn where its body is from it (its own
		// fade in, after the delay, replaces the short appear fade).
		float spawn = GhostRules.SpawnDelayed( owner, Network.Owner?.Id )
			? Smooth( (_sinceAppeared - SpawnHideSeconds) / MathF.Max( SpawnFadeSeconds, 0.001f ) )
			: appear;

		return spawn * exit;
	}

	static float Smooth( float t )
	{
		t = t.Clamp( 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	// The glow: the prefab's "Light" child, kept on the head's centre. Lit on the owner's machine whenever the ghost is
	// out (even in first person, where the head itself isn't drawn), and elsewhere with the ghost's visibility — a light
	// is seen by everyone, so an unseen ghost's glow would give away where it is. Comes up over LightFadeInSeconds, goes
	// out with the ghost. Faded through the intensity, never .Enabled (it's part of the networked object); the authored
	// value rides LightPeak, since the owner's live intensity — what a spawn snapshot ships — may be mid-fade.
	void UpdateLight()
	{
		_light ??= GameObject.Children.FirstOrDefault( c => c.Name == "Light" )?.Components.Get<PointLight>();
		if ( !_light.IsValid() )
			return;

		// (<= 0, not < 0: a hotload leaves a new field at 0 on a live ghost.)
		if ( !IsProxy && LightPeak <= 0f )
			LightPeak = _light.Intensity;
		if ( LightPeak <= 0f )
			return;

		// The head's centre in our frame: from the brushes every machine has (the owner's scaled shape included).
		if ( Head.IsValid() && Sdf.TryGetBounds( Head.Brushes, out var bounds ) )
			_light.LocalPosition = bounds.Center;

		var target = IsProxy ? _visibility : ExitFade;
		_lightFade = target > _lightFade
			? _lightFade.Approach( target, Time.Delta / MathF.Max( LightFadeInSeconds, 0.001f ) )
			: target;
		_light.Intensity = LightPeak * Smooth( _lightFade );
	}
}
