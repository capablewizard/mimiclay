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
/// Who draws it is decided per machine, every frame: the owner never does (their camera sits inside it), and
/// everyone else only when the round's <see cref="RoundSettings.Ghosts"/> includes their group (see
/// <see cref="LocalMaySee"/>). Hidden through <see cref="SdfGhost.Hidden"/>, never by disabling anything.
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

	/// <summary>The ghost's wake — the hunter's run puffs worn as ghost (materials/ghostpuff.vmat) — starts trailing
	/// at this speed (world units/s)…</summary>
	[Property, Group( "Wake" )] public float PuffOnSpeed { get; set; } = 250f;

	/// <summary>…and stops below this one (hysteresis, so hovering at the threshold doesn't flicker).</summary>
	[Property, Group( "Wake" )] public float PuffOffSpeed { get; set; } = 150f;

	SdfSculpture _head;
	Rotation? _turn;             // owner: the eased facing (null until the first Follow, which snaps)
	RunPuffs _puffs;             // every machine: the wake, run from the movement it sees (lazy — hotload-safe)
	GameObject _puffSource;      // the "RunPFX" child, kept on the head's centre
	SdfGhost _look;
	List<SdfBrush> _base;        // owner: the head before scaling
	float _appliedScale = float.NaN;
	float? _transformScale;      // owner: the authored root scale, captured once, then reset to 1
	Vector3 _headCentre;         // owner: the clay's centre in the head's frame, after scaling

	float EffectiveScale => Scale * (_transformScale ?? 1f);

	SdfSculpture Head => _head.IsValid() ? _head : (_head = HunterController.ResolveFaceOf( GameObject ));

	/// <summary>The ghost's head sculpture (nameplates anchor above it).</summary>
	public SdfSculpture HeadSculpture => Head;

	/// <summary>Is this ghost drawn on this machine? Someone else's per <see cref="LocalMaySee"/>; our own only while
	/// our free cam is out in third person (in first person the camera sits inside it).</summary>
	public bool Shown => IsProxy ? LocalMaySee() : OwnedAndOrbited;

	// Our own ghost, being orbited by our third-person free cam.
	bool OwnedAndOrbited => SpectatorController.Local is { } cam && cam.Ghost == GameObject && cam.ShowsOwnGhost;

	protected override void OnStart()
	{
		// Every machine binds its own copy of the head to the sync (Target is local config, never synced).
		var sync = Components.Get<SdfNetworkSync>();
		if ( sync.IsValid() && Head.IsValid() )
			sync.Target = Head;
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

		var shown = Shown;
		_look ??= Components.Get<SdfGhost>();
		if ( _look.IsValid() )
			_look.Hidden = !shown;

		UpdateWake( shown );
	}

	// The wake: the same puffs (and on/off logic, RunPuffs) as the hunter's run dust, emitted from the head's centre
	// while flying fast — climbing and diving included. Every machine runs it from the ghost it sees, so nothing is
	// networked, and it only emits where the ghost itself is drawn (Shown): never trailing an invisible ghost, never
	// behind our own first-person camera. Puffs already out finish and fade on their own.
	void UpdateWake( bool shown )
	{
		if ( _puffs is null )
		{
			_puffSource = GameObject.Children.FirstOrDefault( c => c.Name == "RunPFX" );
			_puffs = new RunPuffs( _puffSource );
		}

		if ( !_puffSource.IsValid() )
			return;

		// The head's centre in our frame (the Head sits on our origin): the brushes are what every machine has, the
		// owner's scaled shape included, so this needs nothing from the network.
		if ( Head.IsValid() && Sdf.TryGetBounds( Head.Brushes, out var bounds ) )
			_puffSource.LocalPosition = bounds.Center;

		_puffs.Update( _puffSource.WorldPosition, PuffOnSpeed, PuffOffSpeed, horizontal: false, allowed: shown );
	}

	/// <summary>May this machine's player see other players' ghosts right now? Their group(s) against the round's
	/// <see cref="RoundSettings.Ghosts"/>. Outside a prop-hunt round (creative, the lobby) everyone can.</summary>
	public static bool LocalMaySee()
	{
		var round = RoundManager.Current;
		if ( !round.IsValid() )
			return true;

		var me = GhostAudience.None;

		// Spectating: benched, waiting without a role, or flying a free cam right now (a scouting prop counts both
		// as a spectator and as a prop).
		if ( SpectatorController.Flying || RoundManager.LocalEliminated || RoundManager.LocalRole == PlayerRole.Unassigned )
			me |= GhostAudience.Spectators;

		if ( !RoundManager.LocalEliminated )
		{
			if ( RoundManager.LocalRole == PlayerRole.Prop ) me |= GhostAudience.Props;
			if ( RoundManager.LocalRole == PlayerRole.Hunter ) me |= GhostAudience.Hunters;
		}

		return (round.Settings.Ghosts & me) != 0;
	}
}
