using System;
using Sandbox;
using Sandbox.Diagnostics;

namespace Editor;

/// <summary>
/// A frame-rate readout pinned to the TOP-LEFT of every scene viewport, in both scene and game view. The engine
/// already prints FPS in the viewport toolbar, but only in Game view mode and only tucked in with the resolution
/// controls — this is the always-there version, and it shows the numbers this project actually gets judged on:
/// the average, the WORST frame in the window (raymarch hitches don't move an average), and the GPU time.
///
/// Implementation notes, mostly Qt facts learned from the engine's own <see cref="SceneOverlayWidget"/>:
///  - It has to be a frameless TOOL WINDOW, not a plain child widget. The viewport's <c>SceneRenderingWidget</c>
///    is a native surface that paints over any managed sibling, so the engine's own overlay is a top-level
///    window positioned in screen coordinates. We do the same, and re-sync the position every editor frame.
///  - <see cref="Widget.TransparentForMouseEvents"/> is load-bearing: without it this box would eat clicks in
///    the corner of the viewport, which is exactly where the gizmo/tool buttons live.
///  - Statics survive Stop→Play and hotloads, so the widget map is pruned by IsValid() rather than trusting
///    any teardown event to fire.
/// </summary>
public static class FpsOverlay
{
	const string EnabledCookie = "Mimiclay.FpsOverlay.Enabled";

	/// <summary>Checkable menu entry — a [Menu] on a static bool property renders as a toggle, not a button.</summary>
	[Menu( "Editor", "Mimiclay/FPS Overlay", "speed" )]
	public static bool Enabled
	{
		get => _enabled ??= EditorCookie.Get( EnabledCookie, true );
		set
		{
			_enabled = value;
			EditorCookie.Set( EnabledCookie, value );
			if ( !value ) DestroyAll();
		}
	}
	static bool? _enabled;

	/// <summary>Averaged over <see cref="WindowSeconds"/>, not over the engine's fixed one-second block.</summary>
	public static float Fps { get; private set; }
	public static float CpuMs { get; private set; }
	/// <summary>Slowest single frame in the window — the number a hitch actually shows up in.</summary>
	public static float WorstMs { get; private set; }
	public static float GpuMs { get; private set; }

	const float WindowSeconds = 0.25f;

	static RealTimeSince _sinceFlush;
	static int _frames;
	static float _cpuSum;
	static float _cpuWorst;
	static float _gpuSum;

	static readonly Dictionary<SceneViewportWidget, FpsOverlayWidget> _widgets = new();

	[EditorEvent.Frame]
	public static void Tick()
	{
		bool flushed = Sample();

		if ( !Enabled )
		{
			DestroyAll();
			return;
		}

		Sync( flushed );
	}

	/// <summary>Accumulate this frame; returns true on the frames where the displayed numbers changed.</summary>
	static bool Sample()
	{
		float cpuMs = (float)PerformanceStats.FrameTime * 1000f;

		_frames++;
		_cpuSum += cpuMs;
		_cpuWorst = MathF.Max( _cpuWorst, cpuMs );
		_gpuSum += PerformanceStats.GpuFrametime;

		if ( _sinceFlush < WindowSeconds )
			return false;

		float elapsed = MathF.Max( (float)_sinceFlush, 0.0001f );
		Fps = _frames / elapsed;
		CpuMs = _cpuSum / _frames;
		WorstMs = _cpuWorst;
		GpuMs = _gpuSum / _frames;

		_sinceFlush = 0;
		_frames = 0;
		_cpuSum = 0;
		_cpuWorst = 0;
		_gpuSum = 0;
		return true;
	}

	/// <summary>Make sure every viewport of the active scene view has an overlay, parked in its top-left corner.</summary>
	static void Sync( bool repaint )
	{
		foreach ( var (viewport, widget) in _widgets.ToArray() )
		{
			if ( viewport.IsValid() && widget.IsValid() )
				continue;

			widget?.Destroy();
			_widgets.Remove( viewport );
		}

		var viewports = SceneViewWidget.Current?._viewports;
		if ( viewports is null )
			return;

		foreach ( var viewport in viewports.Values )
		{
			if ( !viewport.IsValid() )
				continue;

			if ( !_widgets.TryGetValue( viewport, out var widget ) )
			{
				widget = new FpsOverlayWidget( viewport );
				_widgets[viewport] = widget;
			}

			widget.Follow( repaint );
		}
	}

	static void DestroyAll()
	{
		foreach ( var widget in _widgets.Values )
			widget?.Destroy();

		_widgets.Clear();
	}
}

internal class FpsOverlayWidget : Widget
{
	// Inset from the viewport's own corner so the box doesn't sit flush against the border.
	static readonly Vector2 Inset = new( 8, 8 );

	readonly SceneViewportWidget _viewport;

	public FpsOverlayWidget( SceneViewportWidget viewport ) : base( viewport )
	{
		_viewport = viewport;

		TranslucentBackground = true;
		NoSystemBackground = true;
		TransparentForMouseEvents = true;
		ShowWithoutActivating = true;
		WindowFlags = WindowFlags.FramelessWindowHint | WindowFlags.Tool;

		Size = new Vector2( 152, 46 );
		Show();
	}

	/// <summary>Track the viewport's screen rect (docking, resizing, moving the editor window all shift it).</summary>
	public void Follow( bool repaint )
	{
		bool visible = _viewport.Visible;
		if ( Visible != visible )
			Visible = visible;

		if ( !visible )
			return;

		var target = _viewport.ScreenPosition + Inset;
		if ( Position != target )
			Position = target;

		if ( repaint )
			Update();
	}

	protected override void OnPaint()
	{
		var rect = LocalRect;

		Paint.ClearPen();
		Paint.SetBrush( Color.Black.WithAlpha( 0.5f ) );
		Paint.DrawRect( rect, Theme.ControlRadius );

		float fps = FpsOverlay.Fps;
		var colour = fps >= 60f ? Theme.Green : fps >= 30f ? Theme.Yellow : Theme.Red;

		Paint.SetFont( Theme.MonospaceFont, 12, 700 );
		Paint.SetPen( colour );
		Paint.DrawText( rect.Shrink( 8, 5, 8, 0 ), $"{fps:0} FPS", TextFlag.LeftTop );

		Paint.SetFont( Theme.MonospaceFont, 7, 400 );
		Paint.SetPen( Theme.TextLight.WithAlpha( 0.85f ) );
		Paint.DrawText( rect.Shrink( 8, 0, 8, 6 ),
			$"{FpsOverlay.CpuMs:0.0}ms  peak {FpsOverlay.WorstMs:0.0}  gpu {FpsOverlay.GpuMs:0.0}", TextFlag.LeftBottom );
	}
}

