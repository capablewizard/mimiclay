using System;
using System.Threading.Tasks;
using Sandbox.Modals;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// Drives the main menu's Customise screen. While active, the sculpt toy is swapped out for the player's
/// hunter model — art only, cloned fresh from hunter.prefab and stripped of all gameplay — standing on this
/// GameObject's transform, permanently in face-edit mode. MainMenuNav's route watcher activates/deactivates
/// this as /customise is entered/left (the NavigationHost keeps left pages alive and merely hides them, so a
/// page lifecycle hook can't be the teardown trigger — see <see cref="CustomisePage"/>).
///
/// Cloning the real prefab (rather than authoring a copy of the art into the scene) is deliberate: the menu
/// model can never drift from what the hunter actually looks like in game, and the persist-slot head it wears
/// is the same <see cref="SculptLibrary.HeadSlot"/> the pawn spawns with. The clone starts DISABLED and is
/// dressed in the saved head before it's switched on — the same no-default-face-flash ordering the round
/// spawner uses (see RoundManager).
///
/// The camera is the in-game face-edit rig, not the toy's rotate-the-object controller: an
/// <see cref="OrbitCameraController"/> created on the clone (so it dies with it), wired into the session and
/// framed on the head from the front — the same pivot/fit-distance/pitch math as HunterController.FrameFace.
/// It ticks the shared AltNav while enabled, and the toy's own view controller is off for the whole visit, so
/// the one-ticker invariant holds.
/// </summary>
[Title( "Menu Customise" )]
[Category( "Mimiclay" )]
[Icon( "face_retouching_natural" )]
public sealed class MenuCustomise : Component
{
	/// <summary>hunter.prefab — the source of truth for the model's art.</summary>
	[Property] public GameObject HunterPrefab { get; set; }

	/// <summary>The menu sculpt toy (the big head), hidden while the customise model is up.</summary>
	[Property] public GameObject SculptToy { get; set; }

	/// <summary>Breathing room when the camera frames the head on entry — same meaning as the hunter's
	/// EditFramingMargin: 1 = head exactly fills the frame, 1.4 ≈ 40% margin.</summary>
	[Property, Range( 1f, 3f )] public float FramingMargin { get; set; } = 1.4f;

	/// <summary>Pitch the edit camera opens at (degrees; positive looks slightly down at the face) — same
	/// meaning as the hunter's EditCameraPitch.</summary>
	[Property] public float CameraPitch { get; set; } = 10f;

	/// <summary>Live instance for the nav's route watcher to drive (scene-placed, one per menu scene).</summary>
	public static MenuCustomise Instance { get; private set; }

	/// <summary>The customise model is up and its session owns the stage. (Not "Active" — that name is
	/// taken by Component.Active, and shadowing it would be a trap.)</summary>
	public bool IsOpen { get; private set; }

	GameObject _model;
	SculptEditSession _session;
	OrbitCameraController _orbit;
	SdfSculpture _face;
	SdfSculpture[] _bodySculpts; // everything sculpted on the model EXCEPT the face — mirrors the face's clay
	EditHud _hud;
	bool _frameQueued;           // frame-on-the-head still pending (waits for the session to self-activate)
	Vector3 _cameraReturnPos;    // the menu camera's pose before customise — the orbit rig moves AND rotates
	Rotation _cameraReturnRot;   // the camera, so leaving the page has to put both back
	bool _hasCameraReturn;

	// The scene's EditHud, found once (it's scene-placed in menu.scene, and EnsureHud never duplicates it).
	EditHud Hud => _hud.IsValid() ? _hud : (_hud = Scene.GetAllComponents<EditHud>().FirstOrDefault());

	protected override void OnAwake() => Instance = this;

	// Assert the menu's trimmed HUD from code rather than trusting the scene file: the flag flips in
	// Activate mutate the LIVE component, and in-editor play runs the open scene in place — so a Stop
	// mid-customise (Deactivate never runs) followed by any editor save bakes ShowLayers/ShowTools=true
	// into menu.scene, and the menu then boots wearing the full editor over the landing page (which is
	// exactly how this shipped broken once). These two flags are customise-owned in this scene; the
	// palette/picker/slider flags stay scene-authored tuning.
	protected override void OnStart() => ApplyHudTrim();

	protected override void OnDestroy()
	{
		if ( Instance == this )
			Instance = null;
	}

	public void Activate()
	{
		if ( IsOpen || !HunterPrefab.IsValid() )
			return;

		// Toy off FIRST — disabling it tears its always-on session down cleanly (dropping
		// SculptEditSession.Current), so the stage is clear before the model's session claims it.
		if ( SculptToy.IsValid() )
			SculptToy.Enabled = false;

		_model = SpawnModel();
		if ( !_model.IsValid() )
		{
			RestoreToy();
			return;
		}

		// Full editor HUD for the customiser (the toy runs it trimmed — no layer stack or tools), plus the
		// Back button under the layer stack. Deactivate re-trims.
		if ( Hud.IsValid() )
		{
			Hud.ShowLayers = true;
			Hud.ShowTools = true;
			Hud.BackAction = () => MainMenuNav.Instance?.GoBackOrHome();
			Hud.WorkshopSave = SaveToWorkshop;
			Hud.WorkshopLoad = LoadFromWorkshop;
			Hud.WorkshopClose = CloseWorkshopBrowser;
		}

		var cam = Scene.Camera;
		if ( cam.IsValid() )
		{
			_cameraReturnPos = cam.WorldPosition;
			_cameraReturnRot = cam.WorldRotation;
			_hasCameraReturn = true;

			// Park the camera on the head framing THIS frame. The session (and the orbit rig it enables)
			// only comes up on the clone's first update, and component order isn't guaranteed — so for a
			// few frames the camera would otherwise hold the menu's whole-body view and then visibly snap
			// in. With the camera already on the framed pose, the rig's enable-seed reproduces this exact
			// view (angles from the camera, distance along it from the session's FocusHint), so entry is
			// seamless — the queued FrameHead below just trues up pivot/distance on the rig itself.
			var (pivot, distance, rot) = HeadFraming();
			cam.WorldPosition = pivot - rot.Forward * distance;
			cam.WorldRotation = rot;
		}

		// The rig's own framing still can't run yet: the session self-activates in ITS OnStart (a frame
		// away), and the orbit rig it enables seeds itself from the current view — writing the rig before
		// that would just be overwritten. OnUpdate applies it the moment the session reports editing.
		_frameQueued = true;

		IsOpen = true;
	}

	public void Deactivate()
	{
		if ( !IsOpen )
			return;

		IsOpen = false;
		_frameQueued = false;

		ApplyHudTrim();

		// Orbit rig off BEFORE the deferred destroy — a destroy-pending component could still tick this frame
		// and stamp its view back over the camera restore below. Its OnDisabled also resets the shared AltNav.
		if ( _orbit.IsValid() )
			_orbit.Enabled = false;

		// The session's own teardown commits any pending edit on the way out, which also saves the head slot.
		if ( _model.IsValid() )
			_model.Destroy();
		_model = null;
		_session = null;
		_orbit = null;
		_face = null;
		_bodySculpts = null;

		// Put the camera back exactly as the menu had it — the orbit rig moved and rotated it, and the home
		// page should come back framed as if we never left. (FOV needs no restore: the rig asserts it through
		// MainCamera, which eases back to its authored baseline once nothing asserts.)
		if ( _hasCameraReturn && Scene.Camera.IsValid() )
		{
			Scene.Camera.WorldPosition = _cameraReturnPos;
			Scene.Camera.WorldRotation = _cameraReturnRot;
		}
		_hasCameraReturn = false;

		RestoreToy();
	}

	protected override void OnUpdate()
	{
		if ( !IsOpen )
			return;

		// One-shot: frame the head once the session is up (it enables the orbit rig, whose seed we override —
		// the same run-after-the-session-enables ordering HunterController.FrameFace documents).
		if ( _frameQueued && _session.IsValid() && _session.IsEditing )
		{
			_frameQueued = false;
			FrameHead();
		}

		MatchBodyMaterialToFace();
	}

	// The menu's resting HUD state: colour tools only, no layer stack / tools column, no Back button.
	void ApplyHudTrim()
	{
		if ( !Hud.IsValid() )
			return;

		Hud.ShowLayers = false;
		Hud.ShowTools = false;
		Hud.BackAction = null;
		Hud.WorkshopSave = null;
		Hud.WorkshopLoad = null;
		Hud.WorkshopClose = null;
		Hud.WorkshopBrowserOpen = false;
		Hud.WorkshopStatus = null;
		Hud.WorkshopItems = null;
	}

	// Save To Workshop: pack the current head into a Storage entry (the same JSON a local .sculpt save
	// carries) and hand it to the Steam Workshop publish overlay — user-confirmed every time, by Steam's
	// design (the silent UgcPublisher is engine-internal on purpose). Every save is a FRESH entry and so a
	// NEW workshop item: the workshop is a library of heads, and the earlier update-in-place behaviour (one
	// reused entry carrying a _workshopId) meant each save — or a save after a load — silently overwrote an
	// existing item. An explicit "update this item" flow can come later; overwriting must never be implicit.

	bool _thumbCapturing;

	async void SaveToWorkshop()
	{
		if ( _thumbCapturing || !_face.IsValid() || _face.Brushes is not { Count: > 0 } )
			return;

		// Render the thumbnail first — it spans a few frames, and the modal should open showing it.
		_thumbCapturing = true;
		Bitmap thumb;
		try
		{
			thumb = await CaptureHeadBitmap();
		}
		finally
		{
			_thumbCapturing = false;
		}

		// The capture awaited across frames — bail if the page was left (model torn down) meanwhile.
		if ( !IsOpen || !_face.IsValid() || _face.Brushes is not { Count: > 0 } )
			return;

		var entry = Storage.CreateEntry( "head" );
		entry.Files.WriteAllText( "head.sculpt", Json.Serialize( new SculptLibrary.Entry
		{
			Name = "Head",
			Resolution = _face.Resolution,
			FlipFaces = _face.FlipFaces,
			Brushes = _face.Brushes,
		} ) );

		// Stored ON the entry (not just passed to the modal): Publish reads the entry's _thumb.png into the
		// options itself, and the saved file doubles as the local gallery icon for the future Library page.
		if ( thumb is not null )
			entry.SetThumbnail( thumb );

		entry.Publish( new WorkshopPublishOptions
		{
			Title = "My Mimiclay Head",
			Description = "A head sculpted in Mimiclay.",
			Visibility = Storage.Visibility.Private, // preset only — the modal's visibility selector is left on
		} );
	}

	// Render the current head through the roster-icon pipeline (SdfThumbnail → SdfStage: same rig prefab,
	// same ink outline, prop on transparency) and read the pixels back as a workshop-ready Bitmap. A
	// ScenePanel is the only sanctioned runtime render-to-texture route (see SdfThumbnail's header), so the
	// capture rig IS a panel — parked invisible on the EditHud, ticked by the UI for a few frames while it
	// stages and renders, then read back and deleted.
	async Task<Bitmap> CaptureHeadBitmap()
	{
		var host = Hud.IsValid() ? Hud.Panel : null;
		if ( host is null )
			return null;

		var thumb = new SdfThumbnail
		{
			Parent = host,
			Brushes = _face.Brushes.Where( b => !b.Damage ).Select( b => b.Copy() ).ToList(),
		};

		// Invisible but laid out: the panel needs a real rect to size its render target. Opacity only hides
		// the on-screen draw — the offscreen render still happens. (Panels don't take pointer events unless
		// styled to, so this can't block the HUD while it exists.)
		thumb.Style.Position = PositionMode.Absolute;
		thumb.Style.Left = 0;
		thumb.Style.Top = 0;
		thumb.Style.Width = 512;
		thumb.Style.Height = 512;
		thumb.Style.Opacity = 0;

		try
		{
			// A few UI ticks: layout a rect, stage the brushes, render. HasSubject + a live RenderTexture is
			// the "picture landed" signal; the deadline covers a stage that can't come up without hanging.
			for ( int i = 0; i < 30; i++ )
			{
				await Task.Frame();
				if ( thumb.HasSubject && thumb.RenderTexture is not null )
					break;
			}

			await Task.Frame(); // one more so the render queued by the final stage/frame change has landed

			var tex = thumb.RenderTexture;
			if ( tex is null )
				return null;

			// Keep the stage's transparency: the head floats on alpha with its ink outline, so the library
			// tiles show no backdrop square. (Steam's docs suggest opaque previews but alpha PNGs are
			// accepted fine — the workshop site just draws its own ground behind them.) Then square it to
			// the 512×512 the workshop asks for.
			var src = tex.GetPixels();
			var pixels = new Color[src.Length];

			for ( int i = 0; i < src.Length; i++ )
				pixels[i] = src[i].ToColor();

			var bitmap = new Bitmap( tex.Width, tex.Height );
			bitmap.SetPixels( pixels );

			return tex.Width == 512 && tex.Height == 512 ? bitmap : bitmap.Resize( 512, 512 );
		}
		finally
		{
			// NOT Delete(): this finally runs in an await continuation, which the engine can resume from
			// inside the capture panel's own internal-scene tick — a synchronous delete there destroys that
			// scene mid-tick and the resumed tick NREs in Nav_Update. DeleteSoon defers to the panel's own
			// next tick, where its scene is guaranteed idle.
			thumb.DeleteSoon();
		}
	}

	// Load From Workshop: entirely silent API (only PUBLISHING is overlay-mediated), so the browser is our
	// own UI — result rows in the EditHud's workshop column. The query can only ever return the player's own
	// mimiclay heads: it's scoped to this app's workshop, filtered to the "head" tag our publisher stamps,
	// and restricted to Author = the local player (which is also what lets it see Private items).
	bool _workshopBusy;

	async void LoadFromWorkshop()
	{
		if ( !Hud.IsValid() )
			return;

		// Second click toggles the window closed (the window's own X routes here too, via WorkshopClose).
		if ( Hud.WorkshopBrowserOpen )
		{
			CloseWorkshopBrowser();
			return;
		}

		if ( _workshopBusy )
			return;

		_workshopBusy = true;
		Hud.WorkshopBrowserOpen = true;
		Hud.WorkshopItems = null;
		Hud.WorkshopStatus = "Searching…";

		try
		{
			var query = new Storage.Query
			{
				Author = Game.SteamId,
				TagsRequired = { "head" },
				SortOrder = Storage.SortOrder.RankedByPublicationDate,
			};

			var result = await query.Run();

			if ( !IsOpen || !Hud.IsValid() || !Hud.WorkshopBrowserOpen )
				return; // page left or window closed while searching

			var items = result?.Items?.Where( i => !i.Banned ).ToList();
			if ( items is not { Count: > 0 } )
			{
				Hud.WorkshopStatus = "No saved heads found";
				return;
			}

			Hud.WorkshopStatus = null;
			Hud.WorkshopItems = items
				.Select( i => (
					string.IsNullOrWhiteSpace( i.Title ) ? "Head" : i.Title,
					i.Preview,
					(Action)(() => ApplyWorkshopItem( i )) ) )
				.ToList();
		}
		catch ( Exception e )
		{
			Log.Warning( $"MenuCustomise: workshop query failed — {e.Message}" );
			if ( Hud.IsValid() )
				Hud.WorkshopStatus = "Workshop unavailable";
		}
		finally
		{
			_workshopBusy = false;
		}
	}

	void CloseWorkshopBrowser()
	{
		if ( !Hud.IsValid() )
			return;

		Hud.WorkshopBrowserOpen = false;
		Hud.WorkshopItems = null;
		Hud.WorkshopStatus = null;
	}

	// Download the picked item (silent) and wear it — through the session's Load funnel, so it's undoable,
	// rebuilds/commits, and the per-commit persist hook writes it straight into the local head slot.
	async void ApplyWorkshopItem( Storage.QueryItem item )
	{
		if ( _workshopBusy || !Hud.IsValid() )
			return;

		_workshopBusy = true;
		Hud.WorkshopStatus = "Downloading…"; // tiles stay up; the busy flag blanks double-clicks

		try
		{
			var installed = await item.Install();

			if ( !IsOpen || !Hud.IsValid() )
				return;

			var json = installed?.Files.FileExists( "head.sculpt" ) == true
				? installed.Files.ReadAllText( "head.sculpt" )
				: null;
			var entry = json is null ? null : Json.Deserialize<SculptLibrary.Entry>( json );

			if ( entry is null || !_session.IsValid() || !_session.Load( entry ) )
			{
				Hud.WorkshopStatus = "Couldn't load that head";
				return;
			}

			// Success — the head is on; close the window so the player sees it. (Deliberately NOT remembering
			// the item's id for the next save: saves always mint a new item — sculpting over a loaded head
			// and saving must never silently overwrite the original.)
			CloseWorkshopBrowser();
		}
		catch ( Exception e )
		{
			Log.Warning( $"MenuCustomise: workshop install failed — {e.Message}" );
			if ( Hud.IsValid() )
				Hud.WorkshopStatus = "Couldn't load that head";
		}
		finally
		{
			_workshopBusy = false;
		}
	}

	// Bring the sculpt toy back and re-enter its always-on edit mode. OnStart only ever runs once, so the
	// StartActive self-activation can't re-fire on a re-enable — and the session's OnDisabled teardown leaves
	// IsEditing set, so the re-entry has to bounce SetActive through false to take the full activate path.
	void RestoreToy()
	{
		if ( !SculptToy.IsValid() )
			return;

		SculptToy.Enabled = true;

		var session = SculptToy.Components.Get<SculptEditSession>( FindMode.EverythingInSelfAndDescendants );
		if ( session.IsValid() && session.StartActive )
		{
			session.SetActive( false );
			session.SetActive( true );
		}
	}

	// Clone hunter.prefab and reduce it to art: the Visuals subtree (head + body sculptures) with the
	// gameplay stripped off. No hand/gun for now — the Shoulder arm goes with the rest.
	GameObject SpawnModel()
	{
		var clone = HunterPrefab.Clone( new CloneConfig( WorldTransform, startEnabled: false, name: "Customise Hunter" ) );
		if ( !clone.IsValid() )
			return null;

		clone.Flags |= GameObjectFlags.NotSaved; // runtime-only: never let this end up serialised into an asset
		clone.SetParent( GameObject, true );

		// Pin the clone to this GameObject's authored pose EXPLICITLY. The prefab root carries its own baked
		// rotation (180° yaw), and letting the clone config compose with it spawned the model facing away from
		// the camera — an explicit write makes the scene transform the single source of truth for the facing.
		clone.WorldPosition = WorldPosition;
		clone.WorldRotation = WorldRotation;

		// Gameplay components off the root. Each is disabled BEFORE the (deferred) Destroy so none of them can
		// run an OnEnabled when the clone is switched on below — a live PlayerController would grab the shared
		// camera, which the menu scene must own (see the shared-camera rule).
		Strip( clone.Components.Get<PlayerController>( true ) );
		Strip( clone.Components.Get<Sandbox.Movement.MoveModeWalk>( true ) );
		Strip( clone.Components.Get<HunterController>( true ) );
		Strip( clone.Components.Get<HunterGun>( true ) );
		Strip( clone.Components.Get<Rigidbody>( true ) );
		Strip( clone.Components.Get<SdfNetworkSync>( true ) );
		Strip( clone.Components.Get<SdfHighlightOutline>( true ) ); // root only — the Head keeps its WarningOnly one

		// Everything that isn't the art: hand/gun arm, movement colliders, pawn HUD, run dust.
		foreach ( var child in clone.Children.ToArray() )
		{
			if ( child.Name == "Visuals" )
				continue;

			child.Enabled = false;
			child.Destroy();
		}

		// Saved head on while still disabled, so the first build that ever starts is the real face.
		HunterController.WearSavedHead( clone );

		_face = HunterController.ResolveFaceOf( clone );
		if ( !_face.IsValid() )
		{
			clone.Destroy();
			return null;
		}

		// This GameObject marks where the HEAD sits, not the feet: the menu's light rig (the spotlight) is
		// aimed at the old sculpt toy's spot, so the face must land exactly there whatever head is loaded —
		// measure the worn face's bounds centre and hang the body beneath it.
		clone.WorldPosition += WorldPosition - FaceCenterWorld();

		// Everything sculpted on the model except the face — the body spheres — mirrors the face's clay,
		// exactly like the pawn (see MatchBodyMaterialToFace).
		_bodySculpts = clone.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelfAndDescendants )
			.Where( s => s != _face )
			.ToArray();

		// The in-game edit camera: an orbit rig on the clone (dies with it), handed to the session so edit
		// mode enables it — same wiring as the hunter pawn, same close-up MinDistance.
		_orbit = clone.Components.Create<OrbitCameraController>();
		_orbit.Enabled = false;
		_orbit.MinDistance = 8f;

		// The always-on edit session: self-activates on start, persists the head slot on every commit.
		var session = _face.Components.Create<SculptEditSession>();
		session.Target = _face;
		session.OrbitCamera = _orbit;
		session.StartActive = true;
		session.PersistSlot = SculptLibrary.HeadSlot;
		_session = session;

		clone.Enabled = true;
		return clone;
	}

	// The head framing — pivot on the face, fit distance, camera in front looking back — shared by the
	// instant camera park on entry and the rig write once the session is up.
	(Vector3 pivot, float distance, Rotation rot) HeadFraming()
	{
		float faceYaw = _model.IsValid() ? _model.WorldRotation.Angles().yaw : WorldRotation.Angles().yaw;
		var rot = new Angles( CameraPitch, faceYaw + 180f, 0f ).ToRotation(); // +180: stand in front, look back
		return (FaceCenterWorld(), FramingDistance(), rot);
	}

	// Park the orbit camera on the face, framed from the front — the first-entry branch of
	// HunterController.FrameFace with the pawn's eye yaw replaced by the model's authored facing.
	void FrameHead()
	{
		if ( !_orbit.IsValid() || !_face.IsValid() )
			return;

		var (pivot, distance, rot) = HeadFraming();
		_orbit.Pivot = pivot;
		_orbit.Distance = distance;
		_orbit.Angles = rot.Angles();
	}

	// World-space centre of the face's sculpted shape (its brush bounds), so the camera frames the head
	// itself rather than its pivot at the neck. Same fallbacks as HunterController.FaceCenterWorld.
	Vector3 FaceCenterWorld()
	{
		if ( _face.IsValid() && Sdf.TryGetBounds( _face.Brushes, out var bounds, SculptEditSession.PendingStamp( _face ) ) )
			return _face.WorldTransform.PointToWorld( bounds.Center );

		return _face.IsValid() ? _face.WorldPosition : WorldPosition;
	}

	// Fit-sphere distance for the head with FramingMargin breathing room, against the FOV edit mode settles
	// at (GameSettings.OrbitFov) — the same math as HunterController.FramingDistance.
	float FramingDistance()
	{
		const float fallback = 60f;

		if ( !_face.IsValid() || !Sdf.TryGetBounds( _face.Brushes, out var bounds, SculptEditSession.PendingStamp( _face ) ) )
			return fallback;

		float radius = bounds.Size.Length * 0.5f * _face.WorldScale.x;
		if ( radius <= 0.01f )
			return fallback;

		float halfFov = GameSettings.OrbitFov.DegreeToRadian() * 0.5f;
		float sin = MathF.Sin( halfFov );
		if ( sin <= 0.001f )
			return fallback;

		return radius * FramingMargin / sin;
	}

	// The body is always made of the same clay as the head — every frame, copy the face's first authored
	// brush's material (the bottom shape in the layer stack) onto every body brush, rebuilding only on a real
	// change. A straight copy of HunterController.MatchBodyMaterialToFace, which the stripped clone lost.
	void MatchBodyMaterialToFace()
	{
		if ( _bodySculpts is not { Length: > 0 } || !_face.IsValid() )
			return;

		var src = _face.Brushes?.FirstOrDefault( b => !b.Damage );
		if ( src is null )
			return;

		foreach ( var sculpt in _bodySculpts )
		{
			if ( !sculpt.IsValid() || sculpt.Brushes is null )
				continue;

			bool changed = false;
			foreach ( var b in sculpt.Brushes )
			{
				if ( b.Damage )
					continue;
				if ( b.Color == src.Color && b.Metallic == src.Metallic && b.Roughness == src.Roughness )
					continue;

				b.Color = src.Color;
				b.Metallic = src.Metallic;
				b.Roughness = src.Roughness;
				changed = true;
			}

			if ( changed )
				sculpt.Rebuild();
		}
	}

	static void Strip( Component c )
	{
		if ( !c.IsValid() )
			return;

		c.Enabled = false;
		c.Destroy();
	}
}
