using System;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// An editor-only camera for shooting a map's banner (the lobby map card's <see cref="MapResource.Preview"/>)
/// straight from the level. Drop it in a map, frame it like any camera (select it for the live preview), press
/// "Capture Banner": it renders a PNG into <see cref="BannerFolder"/> (UI/Banners/Maps/&lt;scene&gt;_banner.png) and
/// points every <see cref="MapResource"/> for that scene at it. The folder matters: a png is a loose file, and the
/// publisher only ships loose files under the project's "Resources" globs (UI/*) — a banner anywhere else is
/// missing from the built game.
///
/// The look is BORROWED, not authored: the main camera prefab's post-process components are copied onto a hidden,
/// unsaved child, so the banner always matches what players see (see [[main-camera-prefab]]) and re-capturing after
/// a postfx tweak picks the tweak up. The GameObject is flagged EditorOnly, so it never exists in play — it can't
/// fight the shared camera ([[shared-camera-rule]]).
///
/// Writing the asset needs the editor assembly, so the capture itself lives in MapBannerCapture and is wired in via
/// <see cref="CaptureHandler"/> (same pattern as <see cref="FbxBlockoutImporter.BakeHandler"/>).
/// </summary>
[Title( "Map Banner Camera" ), Category( "Mimiclay" ), Icon( "photo_camera" )]
public sealed class MapBannerCamera : Component, Component.ExecuteInEditor
{
	/// <summary>The map card's image aspect. Wider than every card (2.0–2.25:1) on purpose: the cards fit the
	/// image to their HEIGHT and crop the sides, so a wider shot covers them all. Matches home_banner (1024×424).</summary>
	public const float MapCardAspect = 2.4f;

	/// <summary>Mount-relative folder every map banner is written to (lowercase, no leading slash). Under UI/ so the
	/// publisher's "Resources": "UI/*" glob ships it; the game's key art sits beside it in UI/Banners/Games.</summary>
	public const string BannerFolder = "ui/banners/maps";

	/// <summary>The narrowest card the banner is cropped into (the setup screen's featured card, ~1.98:1). Anything
	/// outside this, centred, can be cut off — the editor's banner preview draws it as the safe frame.</summary>
	public const float CardSafeAspect = 1.95f;

	/// <summary>Bottom fraction of the card the map name sits in (featured card: 32px caps above a 16px margin on a
	/// ~300px card) — shaded in the editor's banner preview so the subject stays clear of it.</summary>
	public const float CardTitleBand = 0.24f;

	const string ProxyName = "Post FX (borrowed from main camera)";

	[RequireComponent] public CameraComponent Camera { get; set; }

	/// <summary>Output width in pixels; height follows the aspect.</summary>
	[Property, Group( "Output" ), Range( 256, 4096 )] public int Width { get; set; } = 1536;

	/// <summary>Use <see cref="Aspect"/> instead of the map card's <see cref="MapCardAspect"/>.</summary>
	[Property, Group( "Output" )] public bool OverrideAspect { get; set; }

	/// <summary>Width ÷ height when <see cref="OverrideAspect"/> is on.</summary>
	[Property, Group( "Output" ), ShowIf( nameof( OverrideAspect ), true ), Range( 0.5f, 4f )]
	public float Aspect { get; set; } = MapCardAspect;

	/// <summary>Render this many times larger and downsample — cleaner edges than the raw render.</summary>
	[Property, Group( "Output" ), Range( 1, 4 )] public int Supersample { get; set; } = 2;

	/// <summary>Also set <see cref="MapResource.Preview"/> on every map asset that loads this scene.</summary>
	[Property, Group( "Output" )] public bool AssignToMap { get; set; } = true;

	/// <summary>Where the post-processing comes from. Empty = the scene's main camera prefab instance.</summary>
	[Property, Group( "Post Processing" )] public GameObject PostFxSource { get; set; }

	[Property, ReadOnly, Group( "Status" )] public string LastCapture { get; set; } = "";

	/// <summary>Editor-only capture hook — wired up by MapBannerCapture; null at runtime.</summary>
	public static Func<MapBannerCamera, bool> CaptureHandler;

	/// <summary>While a capture settles, the SDF distance bands measure from the banner camera instead of the
	/// scene view (read by SdfEditorViewPump), so props are shot at the LOD this camera would see.</summary>
	public static Vector3? CaptureViewPos;

	public float OutputAspect => OverrideAspect ? Math.Max( Aspect, 0.1f ) : MapCardAspect;

	public Vector2Int OutputSize => new( Width, Math.Max( 2, (int)MathF.Round( Width / OutputAspect ) ) );

	protected override void OnEnabled()
	{
		if ( !Scene.IsEditor )
			return;

		GameObject.Flags |= GameObjectFlags.EditorOnly;

		// a fresh CameraComponent defaults to main — never let this one take over Scene.Camera
		if ( Camera.IsValid() )
			Camera.IsMainCamera = false;

		SyncPostFx();
	}

	protected override void OnValidate()
	{
		if ( Camera.IsValid() )
			Camera.IsMainCamera = false;
	}

	[Button( "Capture Banner", "photo_camera" )]
	public void Capture()
	{
		if ( CaptureHandler is null )
		{
			Log.Warning( "[MapBanner] Capturing needs the editor." );
			return;
		}

		CaptureHandler( this );
	}

	/// <summary>Re-copy the post-processing from <see cref="PostFxSource"/> (capture does this anyway).</summary>
	[Button( "Refresh Post FX", "refresh" )]
	public void SyncPostFx()
	{
		using var _ = Scene.Push();

		// immediate, not Destroy(): for a frame the old and new copies would both feed the post-process system,
		// which keeps the FIRST effect of each type
		foreach ( var old in GameObject.Children.Where( x => x.Name == ProxyName ).ToList() )
			old.DestroyImmediate();

		var source = ResolvePostFxSource();
		if ( !source.IsValid() )
		{
			Log.Warning( $"[MapBanner] '{GameObject.Name}': no main camera in the scene to borrow post-processing from — set Post Fx Source." );
			return;
		}

		var proxy = new GameObject( GameObject, true, ProxyName );
		proxy.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.Hidden;

		foreach ( var fx in source.GetComponentsInChildren<BasePostProcess>() )
		{
			// property-by-property, not Serialize/Deserialize: deserializing outside the scene's callback batch
			// (an inspector button, an editor frame) throws, and the batch API is engine-internal
			var type = TypeLibrary.GetType( fx.GetType() );
			var copy = proxy.Components.Create( type, false );
			foreach ( var prop in type.Properties )
			{
				if ( prop.IsStatic || !prop.CanWrite || !prop.CanRead || !prop.HasAttribute<PropertyAttribute>() )
					continue;

				prop.SetValue( copy, prop.GetValue( fx ) );
			}

			copy.Enabled = true;
		}
	}

	/// <summary>Snap this camera to the main camera's position, rotation and FOV — a starting point for framing.</summary>
	[Button( "Copy Main Camera View", "center_focus_strong" )]
	public void CopyMainCameraView()
	{
		var source = ResolvePostFxSource()?.GetComponent<CameraComponent>();
		if ( !source.IsValid() || !Camera.IsValid() )
			return;

		WorldPosition = source.WorldPosition;
		WorldRotation = source.WorldRotation;
		Camera.FieldOfView = source.FieldOfView;
	}

	GameObject ResolvePostFxSource()
	{
		if ( PostFxSource.IsValid() )
			return PostFxSource;

		var main = Scene.GetAllComponents<MainCamera>().FirstOrDefault();
		if ( main.IsValid() )
			return main.GameObject;

		var cam = Scene.Camera;
		return cam.IsValid() && cam.GameObject != GameObject ? cam.GameObject : null;
	}
}
