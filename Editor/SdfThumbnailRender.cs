using System.Collections.Generic;
using Editor;
using Mimiclay;
using Sandbox;

namespace Editor;

/// <summary>
/// Renders an SDF sculpt to a <see cref="Pixmap"/> for editor UI — the tools-side twin of the in-game
/// <see cref="SdfThumbnail"/> panel. Both drive the same <see cref="SdfStage"/>, so what you see here is what
/// the game draws.
///
/// The editor gets the easier deal of the two: <c>SceneCamera.RenderToPixmap</c> is public in Sandbox.Tools, so
/// there's no ScenePanel and no CameraComponent involved — just a camera pointed at the stage's private world.
/// That also makes this the thing to build library / gallery thumbnail BAKING on: a Pixmap can go straight to
/// PNG with <c>SavePng</c>.
/// </summary>
public static class SdfThumbnailRender
{
	/// <summary>
	/// Render <paramref name="brushes"/> to a square pixmap on transparency. Returns null if there's nothing to
	/// draw or the render failed. Pose and FOV default to the rig prefab's camera — pass them only to override.
	/// </summary>
	/// <remarks>
	/// Builds and tears down a whole rig per call rather than caching one in a static. That's a few milliseconds
	/// we don't strictly need to spend, but editor statics survive Stop→Play and hotload (see
	/// [[editor-static-persistence]]), and a stale cached Scene/SceneWorld is a much worse problem than a slow
	/// thumbnail. Bake to PNG if you need these fast.
	/// </remarks>
	public static Pixmap Render( List<SdfBrush> brushes, int size = 256, Angles? pose = null, float? fov = null )
	{
		if ( brushes is not { Count: > 0 } )
			return null;

		if ( size < 2 )
			return null;

		Scene scene = null;
		SdfStage stage = null;
		SceneCamera camera = null;

		try
		{
			// An editor scene, not the one the user has open: the stage's host GameObject would otherwise show up
			// in their scene tree and undo stack. Nothing ticks it — SdfStage.SetBrushes drives the renderer's
			// Refresh() synchronously, so the prop is fully built by the time we render.
			scene = Scene.CreateEditorScene();
			stage = new SdfStage( scene );
			camera = new SceneCamera( "SDF Thumbnail" );

			stage.SetBrushes( brushes );
			stage.Frame( camera, pose, fov );

			var pixmap = new Pixmap( size, size );
			return camera.RenderToPixmap( pixmap ) ? pixmap : null;
		}
		finally
		{
			stage?.Dispose();
			camera?.Dispose();
			scene?.Destroy();
		}
	}

	/// <summary>Render a saved <c>.sculpt</c> by name (including the reserved <c>_head</c> slot).</summary>
	public static Pixmap RenderSaved( string name, int size = 256, Angles? pose = null, float? fov = null )
		=> Render( SculptLibrary.Load( name )?.Brushes, size, pose, fov );
}

/// <summary>
/// A widget that draws a <see cref="Pixmap"/> on the editor's control background, downscaled smoothly.
///
/// The smoothing is the whole point of this class. <c>Paint.Draw</c> hands the pixmap to Qt's drawPixmap
/// without setting the smooth-transform hint, so anything it has to SCALE comes out nearest-neighboured —
/// a 256px thumbnail in a 96px row looks like a mosaic. <c>Pixmap.Resize</c> does a proper smooth
/// downsample, so we resize once to the exact device size and then draw 1:1.
/// </summary>
public class PixmapView : Widget
{
	Pixmap _source;
	Pixmap _scaled;
	Vector2 _scaledFor;

	public Pixmap Pixmap
	{
		get => _source;
		set
		{
			_source = value;
			_scaled = null;
			Update();
		}
	}

	public PixmapView( Widget parent = null ) : base( parent ) { }

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect, 4 );

		if ( _source is null )
			return;

		// Device pixels, not logical ones — on a scaled display a logical-size resize would be upscaled
		// again on the way to the screen and land right back where we started.
		var target = LocalRect.Size * DpiScale;
		if ( target.x < 1f || target.y < 1f )
			return;

		if ( _scaled is null || _scaledFor != target )
		{
			_scaled = _source.Resize( target );
			_scaledFor = target;
		}

		Paint.Draw( LocalRect, _scaled ?? _source );
	}
}
