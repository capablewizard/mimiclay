using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mimiclay;
using Sandbox;

namespace Editor;

/// <summary>
/// The editor half of <see cref="MapBannerCamera"/>: renders the banner, writes <c>&lt;scene&gt;_banner.png</c> into
/// <see cref="MapBannerCamera.BannerFolder"/> (Assets/UI/Banners/Maps — the one loose-file folder the publisher
/// ships), and assigns it to the scene's map assets (the png directly — see Finish for why not a .vtex).
///
/// Capture is deferred a few editor frames. The engine's PostProcessSystem only builds an editor camera's effect
/// layers while that camera is SELECTED (and only at the end of a scene tick), and the freshly copied effects plus
/// the SDF distance bands need a tick to land — so we select the banner, wait, then render.
/// </summary>
public static class MapBannerCapture
{
	const int SettleFrames = 4;
	const int MaxRenderDim = 8192;

	static MapBannerCamera _pending;
	static int _framesLeft;

	[EditorEvent.Frame]
	static void Tick()
	{
		// idempotent, cheap — keeps the hook alive across hotloads
		MapBannerCamera.CaptureHandler = Begin;

		if ( _pending is null )
			return;

		if ( !_pending.IsValid() )
		{
			Clear();
			return;
		}

		MapBannerCamera.CaptureViewPos = _pending.WorldPosition;
		if ( --_framesLeft > 0 )
			return;

		var banner = _pending;
		Clear();
		Finish( banner );
	}

	/// <summary>
	/// Press "Capture Banner" on a MapBannerCamera in any open scene tab. The render lands a few editor frames later
	/// (the camera gets selected first, so its post-processing builds), so read_console for the "[MapBanner]" result.
	/// </summary>
	/// <param name="id">The MapBannerCamera's game object id. Empty uses the first one in the active scene.</param>
	[Mcp.McpTool( "capture_map_banner" )]
	public static string CaptureFromMcp( string id = "" )
	{
		// by id: any open scene tab, not just the active one
		var banner = string.IsNullOrWhiteSpace( id )
			? SceneEditorSession.Active?.Scene?.GetAllComponents<MapBannerCamera>().FirstOrDefault()
			: SceneEditorSession.All
				.Select( s => (s.Scene?.Directory.FindByGuid( Guid.Parse( id ) ) as GameObject)?.GetComponent<MapBannerCamera>() )
				.FirstOrDefault( b => b.IsValid() );

		if ( !banner.IsValid() )
			return "No MapBannerCamera found.";

		return Begin( banner ) ? $"Capturing '{banner.GameObject.Name}' — check read_console for [MapBanner]." : "Capture didn't start — see read_console.";
	}

	static void Clear()
	{
		_pending = null;
		MapBannerCamera.CaptureViewPos = null;
	}

	static bool Begin( MapBannerCamera banner )
	{
		if ( _pending is not null )
		{
			Log.Warning( "[MapBanner] A capture is already in progress." );
			return false;
		}

		if ( banner?.Scene?.Editor is not { } editor )
		{
			Log.Warning( "[MapBanner] Capture only works on a scene open in the editor (not in play mode)." );
			return false;
		}

		banner.SyncPostFx();
		editor.Selection.Set( banner.GameObject );

		_pending = banner;
		_framesLeft = SettleFrames;
		MapBannerCamera.CaptureViewPos = banner.WorldPosition;
		return true;
	}

	static void Finish( MapBannerCamera banner )
	{
		var scenePath = banner.Scene.Source?.ResourcePath;
		var sceneAsset = string.IsNullOrEmpty( scenePath ) ? null : AssetSystem.FindByPath( scenePath );
		if ( sceneAsset is null )
		{
			Log.Warning( "[MapBanner] Save the scene first — the banner is named after the .scene file." );
			return;
		}

		var camera = banner.Camera;
		if ( !camera.IsValid() )
			return;

		var size = banner.OutputSize;
		var ss = Math.Clamp( banner.Supersample, 1, 4 );
		while ( ss > 1 && Math.Max( size.x, size.y ) * ss > MaxRenderDim )
			ss--;

		Bitmap bitmap = null;
		try
		{
			var prevSize = camera.CustomSize;
			camera.CustomSize = new Vector2( size.x * ss, size.y * ss );
			try
			{
				bitmap = new Bitmap( size.x * ss, size.y * ss );
				camera.RenderToBitmap( bitmap, false );
			}
			finally
			{
				camera.CustomSize = prevSize;
			}

			if ( ss > 1 )
			{
				var small = bitmap.Resize( size.x, size.y, true );
				bitmap.Dispose();
				bitmap = small;
			}

			// Banners live under Assets/UI/Banners/Maps, NOT next to the .scene. A png is a loose file (nothing
			// compiles it), and the publisher only ships loose files matching the project's "Resources" globs —
			// "UI/*" here. Anything written under scenes/ shows up in the editor and is silently missing from the
			// built game.
			var stem = Path.GetFileNameWithoutExtension( sceneAsset.AbsolutePath ).ToLowerInvariant() + "_banner";
			var pngRel = $"{MapBannerCamera.BannerFolder}/{stem}.png";
			var pngAbs = Path.Combine( AssetsRootOf( sceneAsset, scenePath ), MapBannerCamera.BannerFolder.Replace( '/', Path.DirectorySeparatorChar ), stem + ".png" );
			Directory.CreateDirectory( Path.GetDirectoryName( pngAbs ) );

			// The map references the .png itself, NOT a .vtex: a vtex built from a "Sequences" source (the shape the
			// old home_thumb.vtex had) pads the image into a power-of-two sheet — 1536x640 inside 2048x1024 — so the
			// card drew the padding and the shot looked zoomed/offset. A png loads at its real size.
			File.WriteAllBytes( pngAbs, bitmap.ToPng() );
			var pngAsset = AssetSystem.RegisterFile( pngAbs ) ?? AssetSystem.FindByPath( pngRel );

			RefreshLoadedTexture( pngRel, bitmap );
			pngAsset?.RebuildThumbnail(); // the asset browser's cache was re-rendered from the stale texture

			var assigned = banner.AssignToMap ? AssignToMaps( scenePath, pngRel ) : 0;

			banner.LastCapture = $"{pngRel} ({size.x}x{size.y}) {DateTime.Now:HH:mm:ss}";
			Log.Info( $"[MapBanner] Saved {pngRel} ({size.x}x{size.y}, {ss}x supersampled)"
				+ (banner.AssignToMap ? $" — assigned to {assigned} map asset(s)." : ".") );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[MapBanner] Capture failed: {e.Message}" );
		}
		finally
		{
			bitmap?.Dispose();
		}
	}

	/// <summary>
	/// The Assets folder the scene lives in: the asset's absolute path minus its mount-relative path, so a scene in a
	/// library project writes its banner into THAT project's UI/Banners rather than the current one's.
	/// </summary>
	static string AssetsRootOf( Asset sceneAsset, string scenePath )
	{
		var abs = sceneAsset.AbsolutePath.Replace( '\\', '/' );
		var rel = scenePath.Replace( '\\', '/' ).TrimStart( '/' );
		if ( abs.EndsWith( rel, StringComparison.OrdinalIgnoreCase ) )
			return abs[..^rel.Length].TrimEnd( '/' );

		return Path.GetDirectoryName( sceneAsset.AbsolutePath ); // shouldn't happen; keeps the capture from failing
	}

	static int AssignToMaps( string scenePath, string imagePath )
	{
		var maps = ResourceLibrary.GetAll<MapResource>()
			.Where( m => string.Equals( m.Scene?.ResourcePath, scenePath, StringComparison.OrdinalIgnoreCase ) )
			.ToList();

		if ( maps.Count == 0 )
		{
			Log.Warning( $"[MapBanner] No map asset (.phmap) loads {scenePath} — nothing to assign the banner to." );
			return 0;
		}

		var texture = Texture.Load( imagePath );
		var count = 0;
		foreach ( var map in maps )
		{
			map.Preview = texture;
			if ( AssetSystem.FindByPath( map.ResourcePath ) is { } asset && asset.SaveToDisk( map ) )
				count++;
		}

		return count;
	}

	/// <summary>
	/// Push the new pixels into the texture the editor already has loaded for <paramref name="path"/>. Texture.Load
	/// caches by path, and the file watcher that would reload it belongs to the game instance — in edit mode it
	/// didn't fire, so a re-capture left the old banner in memory (the map card, Texture.Load here, AND the asset
	/// browser thumbnail all kept showing the first capture).
	/// </summary>
	static void RefreshLoadedTexture( string path, Bitmap bitmap )
	{
		var texture = Texture.Load( path, false );
		if ( texture is null || !texture.IsValid )
			return;

		// same size: the public in-place update
		if ( texture.Width == bitmap.Width && texture.Height == bitmap.Height )
		{
			texture.Update( bitmap );
			return;
		}

		// new size (aspect or width changed): only the engine-internal reload can resize it in place, keeping every
		// existing reference (map resources, the card's CoverImage) pointed at the fresh pixels
		var reload = typeof( Texture ).GetMethod( "TryReload", BindingFlags.Instance | BindingFlags.NonPublic );
		if ( reload is null )
		{
			Log.Warning( "[MapBanner] The banner changed size and the editor's loaded copy couldn't be refreshed — restart the editor to see it." );
			return;
		}

		reload.Invoke( texture, new object[] { FileSystem.Mounted, path } );
	}
}
