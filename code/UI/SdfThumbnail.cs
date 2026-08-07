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
	/// <summary>The sculpt to show. Read-only to us — the stage deep-copies before staging anything.</summary>
	public List<SdfBrush> Brushes { get; set; }

	// All three are OVERRIDES. Left null (the default) the rig prefab's camera and margin win, which is the
	// point of the rig — tune thumbnail_stage.prefab once and every thumbnail in the game follows.

	/// <summary>Camera orientation override.</summary>
	public Angles? Pose { get; set; }

	/// <summary>Horizontal field-of-view override. Low is flatter and more icon-like; high is more dramatic.</summary>
	public float? Fov { get; set; }

	/// <summary>Padding override around the prop's bounding sphere. 1 = touching the frame edge.</summary>
	public float? Margin { get; set; }

	/// <summary>True once a sculpt has actually been staged — the caller's cue to stop showing its fallback
	/// icon. False while Brushes is empty or the stage hasn't come up yet.</summary>
	public bool HasSubject { get; private set; }

	SdfStage _stage;
	Angles? _framedPose;
	float? _framedFov, _framedMargin;
	bool _framed;

	public SdfThumbnail()
	{
		RenderOnce = true;
	}

	public override void Tick()
	{
		if ( Brushes is not { Count: > 0 } )
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
		}

		// Both of these are hash/no-op guarded, so this is cheap on the frames where nothing moved.
		var changed = _stage.SetBrushes( Brushes );

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

	void DisposeStage()
	{
		_stage?.Dispose();
		_stage = null;
	}
}
