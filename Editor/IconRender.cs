using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mimiclay;
using Sandbox;

namespace Editor;

/// <summary>
/// Everything one baked icon needs to be rendered again exactly as it was. Lives in the SOURCE PREFAB's
/// <c>.meta</c> (under <see cref="MetaKey"/>), not on a component: the meta file is editor-only, travels with the
/// prefab through git, and never touches what the prefab does at runtime. The PNG's own meta points back at the
/// prefab (<see cref="SourceKey"/>), so either end can reopen the window.
/// </summary>
public sealed class IconRenderSettings
{
	public const string MetaKey = "mimiclay_icon";
	public const string SourceKey = "mimiclay_icon_source";

	/// <summary>Where the PNG goes, relative to Assets. Keep it under UI/ — the publisher only ships loose files
	/// matching the sbproj's "UI/*" Resources glob, so a PNG anywhere else works in the editor and is missing in
	/// the built game.</summary>
	[Property, Group( "Output" )] public string Output { get; set; } = "";

	/// <summary>Final image size in pixels (square).</summary>
	[Property, Group( "Output" ), Range( 32, 1024 )] public int Size { get; set; } = 256;

	/// <summary>Render at this multiple of Size, then downsample — smooths the silhouette and the ink.</summary>
	[Property, Group( "Output" ), Range( 1, 4 )] public int Supersample { get; set; } = 2;

	/// <summary>Camera orientation. Drag in the preview (left mouse) to orbit.</summary>
	[Property, Group( "Camera" )] public Angles CameraAngles { get; set; }

	[Property, Group( "Camera" ), Range( 5f, 90f )] public float FieldOfView { get; set; } = 32f;

	/// <summary>Multiplies the rig's fitted distance. Below 1 crops in. Right-drag or wheel in the preview.</summary>
	[Property, Group( "Camera" ), Range( 0.3f, 3f )] public float Distance { get; set; } = 1f;

	/// <summary>Slides the subject across the frame, in half-frames. Middle-drag in the preview.</summary>
	[Property, Group( "Camera" )] public Vector2 Pan { get; set; }

	/// <summary>Turns the whole rig relative to the camera — yaw walks the key light round, pitch raises it.
	/// Shift + left-drag in the preview.</summary>
	[Property, Group( "Lighting" )] public Angles LightAngles { get; set; }

	[Property, Group( "Lighting" ), Range( 0f, 4f )] public float LightBrightness { get; set; } = 1f;

	[Property, Group( "Lighting" ), Range( 0f, 4f )] public float AmbientBrightness { get; set; } = 1f;

	/// <summary>Ink outline thickness in pixels at <see cref="Size"/>. 0 = none.</summary>
	[Property, Group( "Look" ), Range( 0f, 32f )] public float OutlinePixels { get; set; } = 3f;

	/// <summary>Flat grey "who's that" silhouette, outline on top.</summary>
	[Property, Group( "Look" )] public bool Silhouette { get; set; }

	/// <summary>Fresh settings for a prefab: the rig's camera and outline, output named after the prefab.</summary>
	public static IconRenderSettings Defaults( Asset prefab, SdfStage stage )
	{
		var s = new IconRenderSettings
		{
			Output = $"UI/Icons/{Path.GetFileNameWithoutExtension( prefab.Path ).ToLowerInvariant()}.png",
		};

		if ( stage is not null )
		{
			s.CameraAngles = stage.RigPose;
			s.FieldOfView = stage.RigFov;
			s.OutlinePixels = stage.RigOutlineFraction * s.Size;
		}

		return s;
	}

	public static IconRenderSettings Load( Asset prefab )
	{
		try { return prefab?.MetaData.Get<IconRenderSettings>( MetaKey ); }
		catch ( Exception e )
		{
			Log.Warning( $"[Icon] Couldn't read saved icon settings from {prefab?.Path}: {e.Message}" );
			return null;
		}
	}

	public void Save( Asset prefab ) => prefab?.MetaData.Set( MetaKey, this );

	public IconRenderSettings Clone() => (IconRenderSettings)MemberwiseClone();
}

/// <summary>
/// A private photo studio for one prefab: the prefab cloned into an editor scene of its own (so nothing shows
/// in the user's hierarchy or undo stack), lit by the thumbnail rig via a lighting-only <see cref="SdfStage"/>.
/// Same rig, same post chain, same framing maths as every in-game portrait — the difference is the subject can
/// be anything the prefab holds (models, several sculptures), and the result is written to disk.
///
/// Only ExecuteInEditor components wake in an editor scene, so gameplay components on the prefab stay inert;
/// the renderers (model and SDF) build their scene objects in the scene's world, which is the world we light.
/// </summary>
sealed class IconStudio : IDisposable
{
	Scene _scene;
	SdfStage _stage;
	SceneCamera _camera;
	GameObject _subject;

	public SdfStage Stage => _stage;

	public bool IsValid => _scene.IsValid() && _stage is { IsValid: true } && _subject.IsValid();

	public IconStudio( PrefabFile prefab )
	{
		_scene = Scene.CreateEditorScene();

		using ( _scene.Push() )
			_subject = GameObject.Clone( prefab, global::Transform.Zero, null, true, "Icon Subject" );

		_stage = new SdfStage( _scene.SceneWorld );

		foreach ( var r in _subject.GetComponentsInChildren<SdfRaymarchRenderer>( true, true ) )
			_stage.ConfigureRenderer( r );

		_stage.SetSubjectBounds( MeasureBounds( _subject ) );
		_camera = new SceneCamera( "Icon Render" );
	}

	/// <summary>
	/// The subject's world box. Sculptures from their brushes (exact, and independent of whether a proxy mesh has
	/// been built yet); models from their renderers — but not the ModelRenderer riding on a sculpture's own
	/// object, which is that sculpture's collision/proxy mesh and can carry a stale model.
	/// </summary>
	static BBox MeasureBounds( GameObject root )
	{
		BBox? box = null;

		void Add( BBox b ) => box = box is { } existing ? existing.AddBBox( b ) : b;

		foreach ( var s in root.GetComponentsInChildren<SdfSculpture>( true, true ) )
		{
			if ( s.Brushes is { Count: > 0 } brushes && Sdf.TryGetBounds( brushes, out var local ) )
				Add( local.Transform( s.WorldTransform ) );
		}

		foreach ( var m in root.GetComponentsInChildren<ModelRenderer>( true, true ) )
		{
			if ( m.Model is null || m.GameObject.Components.Get<SdfSculpture>().IsValid() )
				continue;

			Add( m.Bounds );
		}

		return box ?? BBox.FromPositionAndSize( Vector3.Zero, 16f );
	}

	/// <summary>Render the subject square at <paramref name="pixels"/>. Null if the studio's gone or the render
	/// failed. The outline is quoted in pixels at <see cref="IconRenderSettings.Size"/> and converted to the
	/// image fraction the shader works in, so a supersampled or preview-sized render matches the final one.</summary>
	public Pixmap Render( IconRenderSettings s, int pixels )
	{
		if ( !IsValid || pixels < 2 )
			return null;

		_stage.LightOffset = s.LightAngles;
		_stage.LightScale = s.LightBrightness;
		_stage.AmbientScale = s.AmbientBrightness;
		_stage.ApplyOutlineOverride( null, s.OutlinePixels / MathF.Max( s.Size, 1 ) );
		_stage.ApplySilhouette( s.Silhouette );
		_stage.Frame( _camera, s.CameraAngles, s.FieldOfView, _stage.RigMargin * s.Distance, s.Pan );

		var pixmap = new Pixmap( pixels, pixels );
		return _camera.RenderToPixmap( pixmap ) ? pixmap : null;
	}

	public void Dispose()
	{
		// Stage first: it removes only its own lights/sky/post from the scene's world, which must still exist.
		_stage?.Dispose();
		_stage = null;

		_camera?.Dispose();
		_camera = null;

		_scene?.Destroy();
		_scene = null;
		_subject = null;
	}
}

/// <summary>
/// Right-click a prefab → "Render Icon…": a window with a live preview of the prefab under the thumbnail rig,
/// the camera and lighting dials beside it, and a button that bakes a PNG for the game to use. Everything you
/// set is written back to the prefab's .meta on every bake (and on close), so the next session opens exactly
/// where this one left off. Right-clicking a baked PNG reopens its prefab's window.
///
/// Preview mouse: left-drag orbits, Shift + left-drag swings the lights, middle-drag pans, right-drag / wheel
/// zooms — the project's usual orbit scheme (see [[altnav-and-sculpt-view-controller]]).
/// </summary>
public sealed class IconRenderWindow : BaseWindow
{
	const int PreviewSize = 512;

	// Every open window, by prefab. Two jobs: a second "Render Icon…" on the same prefab focuses the existing
	// window instead of stacking another (each would hold its own staged copy and fight over the .meta), and a
	// static reference is what lets hotload find an open window and swap it onto the new assembly — an
	// unreferenced one keeps the old type and its frame event throws "Object type … does not match target
	// type" every frame after a code change.
	static readonly Dictionary<string, IconRenderWindow> _open = new();

	readonly Asset _prefabAsset;
	readonly PrefabFile _prefab;

	IconStudio _studio;
	IconRenderSettings _settings;
	IconPreview _preview;
	Widget _sheetHost;
	Label _status;
	bool _dirty = true;

	[Event( "asset.contextmenu", Priority = 60 )]
	public static void OnAssetContext( AssetContextMenu e )
	{
		if ( e.SelectedList.Count != 1 || e.SelectedList[0].Asset is not { } asset )
			return;

		if ( asset.AssetType?.FileExtension == "prefab" )
		{
			e.Menu.AddOption( "Render Icon…", "photo_camera", action: () => Open( asset ) );
			return;
		}

		// A baked icon: back to the prefab it came from, settings and all.
		var source = asset.MetaData.GetString( IconRenderSettings.SourceKey );
		if ( !string.IsNullOrEmpty( source ) && AssetSystem.FindByPath( source ) is { } prefab )
			e.Menu.AddOption( "Re-render Icon…", "photo_camera", action: () => Open( prefab ) );
	}

	/// <summary>Open the icon window for a prefab — what the asset browser's "Render Icon…" does.</summary>
	/// <param name="prefab">Prefab path relative to Assets, e.g. "prefabs/props/mug.prefab".</param>
	[Mcp.McpTool( "open_icon_window" )]
	public static string OpenFromMcp( string prefab )
	{
		if ( AssetSystem.FindByPath( prefab ) is not { } asset )
			return $"No prefab at '{prefab}'.";

		Open( asset );
		return $"Opened the icon window for {asset.Path}.";
	}

	public static void Open( Asset prefabAsset )
	{
		var prefab = ResourceLibrary.Get<PrefabFile>( prefabAsset.Path );
		if ( prefab is null )
		{
			Log.Warning( $"[Icon] Couldn't load {prefabAsset.Path} as a prefab." );
			return;
		}

		if ( _open.TryGetValue( prefabAsset.Path, out var existing ) && existing.IsValid() )
		{
			existing.Show();
			existing.Focus();
			return;
		}

		var window = new IconRenderWindow( prefabAsset, prefab );
		_open[prefabAsset.Path] = window;
		window.Show();
	}

	IconRenderWindow( Asset prefabAsset, PrefabFile prefab )
	{
		_prefabAsset = prefabAsset;
		_prefab = prefab;

		WindowTitle = $"Render Icon — {Path.GetFileNameWithoutExtension( prefabAsset.Path )}";
		SetWindowIcon( "photo_camera" );
		Size = new Vector2( 1000, 620 );
		DeleteOnClose = true;

		BuildStudio();
		_settings = IconRenderSettings.Load( _prefabAsset ) ?? IconRenderSettings.Defaults( _prefabAsset, _studio?.Stage );

		Layout = Layout.Row();
		Layout.Margin = 8;
		Layout.Spacing = 8;

		_preview = new IconPreview( this ) { MinimumSize = new Vector2( 360, 360 ) };
		Layout.Add( _preview, 1 );

		// Fixed width: the preview is the stretchy half, and without a hard width the settings got squeezed off
		// the window's edge.
		var sidePanel = new Widget( this ) { FixedWidth = 380, Layout = Layout.Column() };
		Layout.Add( sidePanel );
		var side = sidePanel.Layout;
		side.Spacing = 6;

		var scroll = new ScrollArea( this );
		scroll.Canvas = new Widget( scroll ) { Layout = Layout.Column() };
		_sheetHost = scroll.Canvas;
		side.Add( scroll, 1 );
		RebuildSheet();

		var help = new Label( "Drag: orbit · Shift+drag: lights · Middle: pan · Right / wheel: zoom" ) { WordWrap = true };
		side.Add( help );

		var row = side.AddRow();
		row.Spacing = 4;
		row.Add( new Button( "Rig defaults", "restart_alt" ) { ToolTip = "Reset camera, lights and look to the thumbnail rig (keeps the output path and size)", Clicked = ResetToRig } );
		row.Add( new Button( "Reload prefab", "refresh" ) { ToolTip = "Pick up changes saved to the prefab since this window opened", Clicked = Reload } );
		row.Add( new Button.Primary( "Render & Save", "photo_camera" ) { Clicked = Bake }, 1 );

		_status = new Label( LastBakeText() ) { WordWrap = true };
		side.Add( _status );
	}

	void BuildStudio()
	{
		_studio?.Dispose();
		_studio = null;

		try { _studio = new IconStudio( _prefab ); }
		catch ( Exception e ) { Log.Warning( $"[Icon] Couldn't stage {_prefabAsset.Path}: {e.Message}" ); }
	}

	void RebuildSheet()
	{
		_sheetHost.Layout.Clear( true );

		var so = _settings.GetSerialized();
		so.OnPropertyChanged += _ => _dirty = true;

		var sheet = new ControlSheet();
		sheet.AddObject( so );
		_sheetHost.Layout.Add( new Widget( _sheetHost ) { Layout = sheet } );
		_sheetHost.Layout.AddStretchCell();
	}

	string LastBakeText()
	{
		var path = Path.Combine( Project.Current.GetAssetsPath(), _settings.Output ?? "" );
		return File.Exists( path ) ? $"{_settings.Output} — last saved {File.GetLastWriteTime( path ):g}" : "Not rendered yet.";
	}

	[EditorEvent.Frame]
	public void Frame()
	{
		if ( !_dirty || _preview is null )
			return;

		_dirty = false;
		_preview.Pixmap = _studio?.Render( _settings, PreviewSize );
	}

	internal IconRenderSettings Settings => _settings;

	/// <summary>The preview changed a setting by dragging. The sheet's controls don't watch the object, so they
	/// get rebuilt once the drag ends rather than on every mouse move.</summary>
	internal void OnPreviewEdited( bool finished )
	{
		_dirty = true;
		if ( finished )
			RebuildSheet();
	}

	void ResetToRig()
	{
		var fresh = IconRenderSettings.Defaults( _prefabAsset, _studio?.Stage );
		fresh.Output = _settings.Output;
		fresh.Size = _settings.Size;
		fresh.Supersample = _settings.Supersample;
		fresh.OutlinePixels = (_studio?.Stage?.RigOutlineFraction ?? 0f) * fresh.Size;
		_settings = fresh;
		RebuildSheet();
		_dirty = true;
	}

	void Reload()
	{
		BuildStudio();
		_dirty = true;
	}

	void Bake()
	{
		var error = Bake( _prefabAsset, _studio, _settings );
		_status.Text = error ?? LastBakeText();
	}

	/// <summary>
	/// Bake a prefab's icon without the window — its saved settings, or the rig defaults if it has none. For
	/// re-baking after a rig change, and for tooling (MCP can't click the window's button).
	/// </summary>
	/// <param name="prefab">Prefab path relative to Assets, e.g. "prefabs/props/mug.prefab".</param>
	[Mcp.McpTool( "render_prefab_icon" )]
	public static string BakeFromMcp( string prefab )
	{
		var asset = AssetSystem.FindByPath( prefab );
		var file = asset is null ? null : ResourceLibrary.Get<PrefabFile>( asset.Path );
		if ( file is null )
			return $"No prefab at '{prefab}'.";

		using var studio = new IconStudio( file );
		var settings = IconRenderSettings.Load( asset ) ?? IconRenderSettings.Defaults( asset, studio.Stage );
		return Bake( asset, studio, settings ) ?? $"Saved {settings.Output}.";
	}

	/// <summary>Render, write the PNG, register it, and remember the settings. Returns an error, or null.</summary>
	static string Bake( Asset prefabAsset, IconStudio studio, IconRenderSettings s )
	{
		var rel = (s.Output ?? "").Replace( '\\', '/' ).TrimStart( '/' );
		if ( !rel.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) )
			return "Output must be a .png path relative to Assets.";

		var size = Math.Clamp( s.Size, 32, 1024 );
		var ss = Math.Clamp( s.Supersample, 1, 4 );

		var big = studio?.Render( s, size * ss );
		if ( big is null )
			return "Render failed — see the console.";

		var final = ss > 1 ? big.Resize( size, size ) : big;
		var png = final.GetPng();

		var abs = Path.Combine( Project.Current.GetAssetsPath(), rel.Replace( '/', Path.DirectorySeparatorChar ) );
		Directory.CreateDirectory( Path.GetDirectoryName( abs ) );
		File.WriteAllBytes( abs, png );

		var pngAsset = AssetSystem.RegisterFile( abs ) ?? AssetSystem.FindByPath( rel );
		pngAsset?.MetaData.Set( IconRenderSettings.SourceKey, prefabAsset.Path );

		// Same stale-cache trap as the map banners: the editor keeps the first-loaded copy of a path.
		using ( var bitmap = Bitmap.CreateFromBytes( png ) )
			MapBannerCapture.RefreshLoadedTexture( rel, bitmap );
		pngAsset?.RebuildThumbnail();

		s.Save( prefabAsset );
		Log.Info( $"[Icon] Saved {rel} ({size}x{size}, {ss}x supersampled) from {prefabAsset.Path}." );
		return null;
	}

	protected override void OnClosed()
	{
		base.OnClosed();

		if ( _open.TryGetValue( _prefabAsset.Path, out var me ) && me == this )
			_open.Remove( _prefabAsset.Path );

		// Keep the tweaks even if nothing was baked — the point is to pick up where you left off.
		_settings?.Save( _prefabAsset );

		_studio?.Dispose();
		_studio = null;
	}
}

/// <summary>The window's preview: draws the latest render and turns mouse drags into camera / light edits.</summary>
sealed class IconPreview : PixmapView
{
	readonly IconRenderWindow _window;
	Vector2 _last;
	MouseButtons _drag;
	bool _shift;

	public IconPreview( IconRenderWindow window ) : base( window )
	{
		_window = window;
		Cursor = CursorShape.SizeAll;
	}

	// The icon is square; the widget is whatever shape the window makes it. Draw the render in the largest
	// centred square (PixmapView would stretch it to fill), on a checkerboard so the transparency reads.
	Pixmap _fitted;
	Pixmap _fittedFrom;
	float _fittedSize;

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect, 4 );

		var side = MathF.Min( Width, Height );
		var square = new Rect( (Width - side) * 0.5f, (Height - side) * 0.5f, side, side );

		const float cell = 16f;
		Paint.SetBrush( Theme.ControlBackground.Lighten( 0.15f ) );
		for ( var y = 0; y * cell < side; y++ )
		for ( var x = (y & 1); x * cell < side; x += 2 )
			Paint.DrawRect( new Rect( square.Left + x * cell, square.Top + y * cell, MathF.Min( cell, side - x * cell ), MathF.Min( cell, side - y * cell ) ) );

		if ( Pixmap is null )
			return;

		var device = side * DpiScale;
		if ( _fittedFrom != Pixmap || _fittedSize != device )
		{
			_fitted = Pixmap.Resize( new Vector2( device, device ) );
			_fittedFrom = Pixmap;
			_fittedSize = device;
		}

		Paint.Draw( square, _fitted ?? Pixmap );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		_drag = e.Button;
		_shift = e.HasShift;
		_last = e.LocalPosition;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		if ( _drag == MouseButtons.None )
			return;

		var d = e.LocalPosition - _last;
		_last = e.LocalPosition;

		var s = _window.Settings;
		const float degreesPerPixel = 0.4f;

		if ( _drag == MouseButtons.Left && _shift )
		{
			var a = s.LightAngles;
			s.LightAngles = new Angles( Math.Clamp( a.pitch + d.y * degreesPerPixel, -89f, 89f ), a.yaw - d.x * degreesPerPixel, 0f );
		}
		else if ( _drag == MouseButtons.Left )
		{
			var a = s.CameraAngles;
			s.CameraAngles = new Angles( Math.Clamp( a.pitch + d.y * degreesPerPixel, -89f, 89f ), a.yaw - d.x * degreesPerPixel, a.roll );
		}
		else if ( _drag == MouseButtons.Middle )
		{
			// Half-frames per pixel: the subject follows the cursor across the square preview.
			var half = MathF.Max( MathF.Min( Width, Height ) * 0.5f, 1f );
			s.Pan += new Vector2( d.x, -d.y ) / half;
		}
		else if ( _drag == MouseButtons.Right )
		{
			s.Distance = Math.Clamp( s.Distance * MathF.Exp( d.y * 0.005f ), 0.3f, 3f );
		}

		_window.OnPreviewEdited( false );
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( _drag == MouseButtons.None )
			return;

		_drag = MouseButtons.None;
		_window.OnPreviewEdited( true );
	}

	protected override void OnWheel( WheelEvent e )
	{
		var s = _window.Settings;
		s.Distance = Math.Clamp( s.Distance * MathF.Exp( -e.Delta * 0.001f ), 0.3f, 3f );
		_window.OnPreviewEdited( true );
		e.Accept();
	}
}
