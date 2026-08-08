// ScenePanel.Camera / .World are marked obsolete in favour of "use a real Scene", but a real Scene is exactly
// what we can't use: CameraComponent.OnAwake hard-asserts that its scene IS the active scene while the game is
// playing, so a detached Scene can never hold a camera at runtime. The SceneCamera path is the only sanctioned
// way for game code to render a hand-built SceneWorld — SceneCamera.RenderToTexture itself is internal, and
// Graphics.RenderToTexture is a stub that returns false.
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// A UI panel showing one SDF sculpt, rendered live to a texture off an <see cref="SdfStage"/> — the prop on
/// transparency, lit by the stage's own rig, with nothing from the map in shot.
///
/// Renders on demand rather than every frame: the brushes are hashed each tick and the scene is only re-rendered
/// when the sculpt actually changes (or the panel resizes), so a static thumbnail costs one draw for its whole
/// lifetime. Keep the panel's identity stable across razor rebuilds (`@key`) — a new panel means a new
/// SceneWorld.
/// </summary>
public sealed class SdfThumbnail : ScenePanel
{
	/// <summary>
	/// The live sculpture to portray. Preferred over <see cref="Brushes"/>, because it lets us re-read only on
	/// COMMIT: a sculpture mutates its brush list continuously while someone drags a handle (that's the
	/// Previewed path), and polling the list can't tell that apart from a finished edit. Watching Committed
	/// means the portrait updates a step at a time as they sculpt, rather than animating along with them.
	/// </summary>
	public SdfSculpture Source
	{
		get => _source;
		set
		{
			if ( _source == value )
				return;

			Unhook();
			_source = value;
			Hook();

			// A brand new source hasn't committed anything yet, so take its current state now or the thumbnail
			// would sit empty until the player next edited their face.
			_syncPending = true;
		}
	}

	/// <summary>A static brush list to show when there's no <see cref="Source"/> — saved sculpts, the debug
	/// fallback sphere. Polled by content hash, since nothing can be mid-edit.</summary>
	public List<SdfBrush> Brushes { get; set; }

	// All three are OVERRIDES. Left null (the default) the rig prefab's camera and margin win, which is the
	// point of the rig — tune thumbnail_stage.prefab once and every thumbnail in the game follows.

	/// <summary>Camera orientation override.</summary>
	public Angles? Pose { get; set; }

	/// <summary>Horizontal field-of-view override. Low is flatter and more icon-like; high is more dramatic.</summary>
	public float? Fov { get; set; }

	/// <summary>Padding override around the prop's bounding sphere. 1 = touching the frame edge.</summary>
	public float? Margin { get; set; }

	/// <summary>Ink outline overrides. Null keeps what the rig prefab authored.</summary>
	public Color? OutlineColor { get; set; }

	/// <inheritdoc cref="OutlineColor"/>
	public float? OutlineWidth { get; set; }

	/// <summary>Re-render every frame rather than only when the sculpt changes. Costs a full scene render per
	/// frame, so it's for debugging — but it means a console toggle shows up immediately instead of waiting for
	/// something to invalidate the thumbnail.</summary>
	public bool Live { get; set; }

	/// <summary>
	/// Hold the current picture: while true, commits (and static-brush changes) stop reaching the stage, and
	/// the panel catches up in one re-read the moment it's cleared. This is how a roster icon sits still while
	/// its player sculpts — their per-commit edits would otherwise replay on everyone's HUD stroke by stroke —
	/// and updates exactly once, on edit exit. The FIRST render always goes through, frozen or not, so a
	/// panel built mid-edit (a late joiner's HUD) shows the current shape rather than nothing.
	/// </summary>
	public bool Frozen { get; set; }

	/// <summary>True once a sculpt has actually been staged — the caller's cue to stop showing its fallback
	/// icon. False while Brushes is empty or the stage hasn't come up yet.</summary>
	public bool HasSubject { get; private set; }

	SdfSculpture _source;
	bool _syncPending;
	bool _staged;    // first SetBrushes has landed — until then Frozen must not hold an empty stage
	bool _wasFrozen; // last tick's Frozen, to catch the thaw edge and re-read once

	SdfStage _stage;
	Angles? _framedPose;
	float? _framedFov, _framedMargin;
	bool _framed;
	bool _postEnabled = SdfStagePost.Enabled;
	Color? _appliedOutlineColor;
	float? _appliedOutlineWidth;
	bool _outlineApplied;

	public SdfThumbnail()
	{
		RenderOnce = true;
	}

	public override void Tick()
	{
		RenderOnce = !Live;

		var live = _source.IsValid();
		var brushes = live ? _source.Brushes : Brushes;

		if ( brushes is not { Count: > 0 } )
		{
			HasSubject = false;
			// Keep the stage alive through an empty frame — a hunter's pawn can flicker out of the scene between
			// phases, and rebuilding the SceneWorld each time would cost far more than holding it.
			base.Tick();
			return;
		}

		// A scene change destroys the host GameObject out from under the stage; rebuild rather than render into
		// a world whose prop is gone.
		if ( _stage is { IsValid: false } )
		{
			_stage.Dispose();
			_stage = null;
		}

		if ( _stage is null )
		{
			var scene = Game.ActiveScene;
			if ( !scene.IsValid() )
			{
				base.Tick();
				return;
			}

			_stage = new SdfStage( scene );
			_framed = false; // force a Frame() below
			_outlineApplied = false;
			_staged = false; // a fresh stage has no picture to hold — the next SetBrushes must go through
		}

		if ( !_outlineApplied || _appliedOutlineColor != OutlineColor || _appliedOutlineWidth != OutlineWidth )
		{
			_outlineApplied = true;
			_appliedOutlineColor = OutlineColor;
			_appliedOutlineWidth = OutlineWidth;
			_stage.ApplyOutlineOverride( OutlineColor, OutlineWidth );
			RenderNextFrame();
		}

		// With a live Source we only re-read on commit; a static list has nothing that can be mid-edit, so it's
		// polled by content hash (which is itself a no-op when nothing moved).
		//
		// Frozen holds the stage as-is (once it has a first picture at all) — _syncPending is left standing, so
		// everything committed while held collapses into the single re-read on the thaw edge below.
		if ( _wasFrozen && !Frozen )
			_syncPending = true;
		_wasFrozen = Frozen;

		var hold = Frozen && _staged;
		var changed = false;

		if ( !hold && (!live || _syncPending) )
		{
			_syncPending = false;
			changed = _stage.SetBrushes( brushes );
			_staged = true;
		}

		// Toggling mimiclay_thumb_post changes what a render produces but nothing about the sculpt, so without
		// this a render-on-change thumbnail would keep showing the old one and the switch would look broken.
		if ( _postEnabled != SdfStagePost.Enabled )
		{
			_postEnabled = SdfStagePost.Enabled;
			changed = true;
		}

		if ( changed || !_framed || _framedFov != Fov || _framedMargin != Margin || _framedPose != Pose )
		{
			_framed = true;
			_framedFov = Fov;
			_framedMargin = Margin;
			_framedPose = Pose;
			_stage.Frame( Camera, Pose, Fov, Margin );
			changed = true;
		}

		if ( changed )
			RenderNextFrame();

		HasSubject = true;

		base.Tick();
	}

	public override void Delete( bool immediate = false )
	{
		DisposeStage();
		base.Delete( immediate );
	}

	public override void OnDeleted()
	{
		DisposeStage();
		base.OnDeleted();
	}

	void Hook()
	{
		if ( _source.IsValid() )
			_source.Committed += OnSourceCommitted;
	}

	void Unhook()
	{
		if ( _source.IsValid() )
			_source.Committed -= OnSourceCommitted;
	}

	void OnSourceCommitted() => _syncPending = true;

	void DisposeStage()
	{
		Unhook();
		_source = null;

		_stage?.Dispose();
		_stage = null;
	}
}
