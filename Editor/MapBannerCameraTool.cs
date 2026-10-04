using System;
using Mimiclay;

namespace Editor;

/// <summary>
/// Viewport overlay for a selected <see cref="MapBannerCamera"/>: a live preview at the banner's exact aspect, with
/// the borrowed post-processing, and the map-card guides drawn over it.
///
/// Why not the engine's camera preview: that window is a fixed 16:9 SceneWidget (1280x720 × 0.4) with no aspect hook,
/// and it clears every world except the scene's, so gizmo lines can't reach it either. This renders through the
/// CameraComponent itself — the same RenderToBitmap path the capture uses — so it's what the PNG will be.
/// </summary>
public class MapBannerCameraTool : EditorTool<MapBannerCamera>
{
	BannerPreviewWindow _window;

	public override void OnEnabled()
	{
		_window = new BannerPreviewWindow();
		AddOverlay( _window, TextFlag.LeftBottom, 10 );
	}

	public override void OnUpdate()
	{
		_window?.SetTarget( GetSelectedComponent<MapBannerCamera>() );
	}
}

class BannerPreviewWindow : WidgetWindow
{
	const float PreviewWidth = 480;

	readonly BannerPreviewView _view;
	float _aspect;

	public BannerPreviewWindow()
	{
		Icon = "photo_camera";
		WindowTitle = "Banner Preview";
		Layout = Layout.Column();
		Layout.Margin = 4;

		_view = Layout.Add( new BannerPreviewView( this ) );
	}

	public void SetTarget( MapBannerCamera banner )
	{
		_view.Banner = banner;
		if ( !banner.IsValid() )
			return;

		var aspect = banner.OutputAspect;
		if ( MathF.Abs( aspect - _aspect ) < 0.0001f )
			return;

		_aspect = aspect;
		_view.FixedWidth = PreviewWidth;
		_view.FixedHeight = MathF.Round( PreviewWidth / aspect );

		var size = banner.OutputSize;
		WindowTitle = $"Banner Preview — {size.x}x{size.y}";
		AdjustSize();
	}
}

class BannerPreviewView : Widget
{
	public MapBannerCamera Banner { get; set; }

	/// <summary>The most recent preview frame, for <see cref="SavePreviewFromMcp"/>.</summary>
	static Pixmap _lastFrame;

	Pixmap _pixmap;

	/// <summary>
	/// Save what the Banner Preview overlay last rendered (the camera image, without the guide lines) to a PNG, to
	/// compare it against capture_map_banner's output. The overlay only renders while a MapBannerCamera is selected.
	/// </summary>
	/// <param name="path">Absolute path of the PNG to write.</param>
	[Mcp.McpTool( "save_map_banner_preview" )]
	public static string SavePreviewFromMcp( string path )
	{
		if ( _lastFrame is null )
			return "No preview frame yet — select a MapBannerCamera so the overlay renders.";

		return _lastFrame.SavePng( path ) ? $"Saved {_lastFrame.Width}x{_lastFrame.Height} to {path}" : "SavePng failed.";
	}

	public BannerPreviewView( Widget parent ) : base( parent )
	{
	}

	[EditorEvent.Frame]
	void Frame()
	{
		if ( !Visible || !Banner.IsValid() || !Banner.Camera.IsValid() )
			return;

		var realSize = Size * DpiScale;
		if ( realSize.x < 2 || realSize.y < 2 )
			return;

		if ( _pixmap is null || _pixmap.Size != realSize )
			_pixmap = new Pixmap( realSize );

		// same as the capture: size the camera to the target so the projection matches the PNG
		var camera = Banner.Camera;
		var prevSize = camera.CustomSize;
		camera.CustomSize = realSize;
		try
		{
			camera.RenderToPixmap( _pixmap );
			_lastFrame = _pixmap;
		}
		finally
		{
			camera.CustomSize = prevSize;
		}

		Update();
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		var r = LocalRect;
		if ( _pixmap is not null )
			Paint.Draw( r, _pixmap );

		DrawCardGuides( r );
	}

	/// <summary>The narrowest card crop (centred) as the safe frame, everything outside it dimmed, and the map-name
	/// band along its bottom.</summary>
	static void DrawCardGuides( Rect r )
	{
		var safeW = MathF.Min( r.Width, r.Height * MapBannerCamera.CardSafeAspect );
		var safe = new Rect( r.Left + (r.Width - safeW) * 0.5f, r.Top, safeW, r.Height );

		Paint.ClearPen();
		Paint.SetBrush( Color.Black.WithAlpha( 0.45f ) );
		if ( safe.Left > r.Left )
		{
			Paint.DrawRect( new Rect( r.Left, r.Top, safe.Left - r.Left, r.Height ) );
			Paint.DrawRect( new Rect( safe.Right, r.Top, r.Right - safe.Right, r.Height ) );
		}

		var bandH = safe.Height * MapBannerCamera.CardTitleBand;
		Paint.SetBrush( Color.Black.WithAlpha( 0.2f ) );
		Paint.DrawRect( new Rect( safe.Left, safe.Bottom - bandH, safe.Width, bandH ) );

		Paint.SetPen( Color.White.WithAlpha( 0.8f ), 1, PenStyle.Dash );
		Paint.DrawLine( new Vector2( safe.Left, r.Top ), new Vector2( safe.Left, r.Bottom ) );
		Paint.DrawLine( new Vector2( safe.Right, r.Top ), new Vector2( safe.Right, r.Bottom ) );

		Paint.SetPen( Color.White.WithAlpha( 0.5f ), 1, PenStyle.Dot );
		Paint.DrawLine( new Vector2( safe.Left, safe.Bottom - bandH ), new Vector2( safe.Right, safe.Bottom - bandH ) );
		Paint.DrawText( new Rect( safe.Left + 6, safe.Bottom - bandH, safe.Width - 12, bandH ), "MAP NAME", TextFlag.LeftCenter );
	}
}
