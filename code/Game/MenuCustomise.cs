using System;

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
	SculptWorkshop _workshop;    // the Workshop column's save/load/browse flow (shared with creative mode)
	EditHud _hud;

	// The weapon: a visuals-only gun.prefab clone beside the head, edited by its OWN session (the HUD's
	// Head/Weapon dock switches — the in-game hunter's two-session setup). Edited at the in-hand display scale
	// (HunterGun.WorldGunScale), because gun.prefab's SculptBounds limits are tuned for that brush scale; saved
	// to SculptLibrary.GunSlot inverse-scaled back to canonical, exactly like HunterGun.OnEditCommitted.
	SdfSculpture _gunSculpt;
	SculptEditSession _gunSession;
	float _gunScale = 0.5f;
	bool _editingGun;            // the gun session owns the stage (gates the commit → slot save, like _editingWorld)

	bool _frameQueued;           // frame-on-the-head still pending (waits for the session to self-activate)
	Vector3 _cameraReturnPos;    // the menu camera's pose before customise — the orbit rig moves AND rotates
	Rotation _cameraReturnRot;   // the camera, so leaving the page has to put both back
	float _cameraReturnFov;      // …and sets the edit FOV, which MainCamera would otherwise keep
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

			// The Workshop column — head flavor, save/load/browse in SculptWorkshop (creative mode wires
			// the same class prop-flavored). Closures read the LIVE fields, and Alive = IsOpen abandons
			// any in-flight async op once the page is left.
			_workshop = SculptWorkshop.ForHeads( () => Hud, () => _session, () => IsOpen );
			Hud.WorkshopSave = _workshop.Save;
			Hud.WorkshopLoad = _workshop.Load;
			Hud.WorkshopClose = _workshop.Close;
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
			// SNAP to the edit FOV in the same frame. The rig asserts GameSettings.EditFov (the player's
			// preferred FOV) every tick and MainCamera eases toward it — from the menu's authored 60° that ease
			// read as a zoom right after the cut.
			_cameraReturnFov = cam.FieldOfView;
			MainCamera.SetFov( GameSettings.EditFov, lerp: false );

			// The head's remembered view if there is one (the last visit), else the front framing. Distance is
			// in the rig's reference-FOV units; the camera sits at the REAL boom (OrbitCameraController.Reach),
			// converted here for the edit FOV just snapped to, so the rig's seed lands on this exact view.
			var (pivot, distance, rot) = s_headView is { } v ? (v.Pivot, v.Distance, v.Angles.ToRotation()) : HeadFraming();
			float tanRef = MathF.Tan( GameSettings.ReferenceFov.DegreeToRadian() * 0.5f );
			float tanLive = MathF.Tan( Math.Clamp( GameSettings.EditFov, 5f, 170f ).DegreeToRadian() * 0.5f );
			cam.WorldPosition = pivot - rot.Forward * (distance * tanRef / tanLive);
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

		// Keep where the current part's camera was, for the next visit (and the next switch back to it).
		RememberView();

		// Orbit rig off BEFORE the deferred destroy — a destroy-pending component could still tick this frame
		// and stamp its view back over the camera restore below. Its OnDisabled also resets the shared AltNav.
		if ( _orbit.IsValid() )
			_orbit.Enabled = false;

		// Leaving mid-weapon-edit: end the gun session HERE, while the commit → GunSlot save can still run
		// (it needs the live sculpt + the _editingGun gate) — the deferred destroy below would be too late.
		if ( _editingGun && _gunSession.IsValid() )
			_gunSession.SetActive( false );
		_editingGun = false;

		// The session's own teardown commits any pending edit on the way out, which also saves the head slot.
		if ( _model.IsValid() )
			_model.Destroy();
		_model = null;
		_session = null;
		_gunSession = null;
		_gunSculpt = null;
		_orbit = null;
		_face = null;
		_bodySculpts = null;
		_workshop = null;

		// Put the camera back exactly as the menu had it — the orbit rig moved and rotated it AND set its FOV,
		// and the home page should come back framed as if we never left. The FOV needs restoring too: MainCamera
		// has no FOV baseline to drift back to (only DoF), so the rig's EditFov target would otherwise stick.
		// Snapped, not eased, to match the position/rotation cut.
		if ( _hasCameraReturn && Scene.Camera.IsValid() )
		{
			Scene.Camera.WorldPosition = _cameraReturnPos;
			Scene.Camera.WorldRotation = _cameraReturnRot;
			MainCamera.SetFov( _cameraReturnFov, lerp: false );
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
	// gameplay stripped off, plus a floating gun beside the head (the Shoulder arm goes with the rest).
	GameObject SpawnModel()
	{
		var clone = HunterPrefab.Clone( new CloneConfig( WorldTransform, startEnabled: false, name: "Customise Hunter" ) );
		if ( !clone.IsValid() )
			return null;

		// The arm + gun's tuning, read off the prefab's HunterGun / HunterController before they're stripped
		// below — the menu poses the arm with the SAME numbers the game does (see PoseArm).
		var hunterGun = clone.Components.Get<HunterGun>( true );
		var hunterCtl = clone.Components.Get<HunterController>( true );
		var gunPrefab = hunterGun.IsValid() ? hunterGun.GunPrefab : null;
		_gunScale = hunterGun.IsValid() && hunterGun.WorldGunScale > 0f ? hunterGun.WorldGunScale : 0.5f;
		var shoulderOffset = hunterGun.IsValid() ? hunterGun.ShoulderOffset : new Vector3( 0f, -9f, -13f );
		var handOffset = hunterGun.IsValid() ? hunterGun.HandOffset : Vector3.Zero;
		var gunRotation = hunterGun.IsValid() ? hunterGun.RotationOffset : new Angles( 0f, -90f, 0f );
		var neckDrop = hunterCtl.IsValid() ? hunterCtl.NeckDrop : 16f;

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

		// Everything that isn't the art: movement colliders, pawn HUD, run dust. The Shoulder arm stays — its Hand
		// holds the gun, like the pawn's (the GunWorld/GunView clones are runtime-only, never in the prefab).
		foreach ( var child in clone.Children.ToArray() )
		{
			if ( child.Name is "Visuals" or "Shoulder" )
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

		var hand = PoseArm( clone, shoulderOffset, neckDrop );
		SpawnGun( hand.IsValid() ? hand : clone, gunPrefab, handOffset, gunRotation );

		// The Head/Weapon dock (EditHud, bottom-centre while nothing's selected) — the in-game hunter's two
		// buttons, each session marking its own part. No gun → no dock, the head edit stands alone.
		if ( _gunSession.IsValid() )
		{
			_session.EditParts = new (string, bool, Action)[]
			{
				("Head", true, () => SetEditPart( gun: false )),
				("Weapon", false, () => SetEditPart( gun: true )),
			};
			_gunSession.EditParts = new (string, bool, Action)[]
			{
				("Head", false, () => SetEditPart( gun: false )),
				("Weapon", true, () => SetEditPart( gun: true )),
			};
		}

		clone.Enabled = true;
		return clone;
	}

	// The arm, posed exactly as HunterGun.Place poses it for a hunter standing still and looking level along the
	// model's facing: the Shoulder pivot at eye + yaw·ShoulderOffset, rotated to the aim, its Hand child where the
	// prefab authored it. The eye is NeckDrop above the head object's origin (the head is parked at the neck —
	// HunterController.UpdateVisuals). Returns the Hand (the gun's mount), or null if the prefab has no arm.
	GameObject PoseArm( GameObject model, Vector3 shoulderOffset, float neckDrop )
	{
		var shoulder = model.Children.FirstOrDefault( c => c.Name == "Shoulder" );
		if ( !shoulder.IsValid() || !_face.IsValid() )
			return null;

		// Aim along the HEAD's facing, level — in game the head and the shoulder take the same aim
		// (HunterController.UpdateVisuals / HunterGun.Place), so this is what makes the arm point straight out the
		// way the face looks. Not the model root's yaw: hunter.prefab's root carries its own baked rotation.
		var aim = new Angles( 0f, _face.GameObject.WorldRotation.Angles().yaw, 0f );
		var eye = _face.GameObject.WorldPosition + Vector3.Up * neckDrop;
		shoulder.WorldPosition = eye + Rotation.FromYaw( aim.yaw ) * shoulderOffset;
		shoulder.WorldRotation = aim.ToRotation();

		return shoulder.Children.FirstOrDefault( c => c.Name == "Hand" );
	}

	// The weapon in the hand: gun.prefab cloned and reduced to visuals (HunterGun's own strip), mounted on the
	// Hand at HunterGun's HandOffset / RotationOffset (the GunWorld mount), wearing the saved GunSlot (or the
	// prefab's stock gun) scaled down to the in-hand display scale, with its own dormant edit session.
	void SpawnGun( GameObject mount, PrefabFile prefab, Vector3 handOffset, Angles gunRotation )
	{
		_gunSculpt = null;
		_gunSession = null;
		_editingGun = false;

		if ( prefab is null )
			return;

		var go = SceneUtility.GetPrefabScene( prefab )?.Clone();
		if ( !go.IsValid() )
			return;

		go.Name = "Customise Gun";
		go.Flags |= GameObjectFlags.NotSaved;
		go.SetParent( mount, false ); // GunWorld's mount: under the Hand, at HunterGun's offsets
		HunterGun.StripNonVisuals( go );

		var sculpt = go.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndDescendants );
		if ( !sculpt.IsValid() || sculpt.Brushes is not { Count: > 0 } )
		{
			go.Destroy();
			return;
		}

		// Canonical (prefab-scale) brushes: the player's saved gun, else the stock one the clone came with —
		// then down to display scale for editing (see the fields).
		var entry = SculptLibrary.Load( SculptLibrary.GunSlot );
		var canonical = entry?.Brushes is { Count: > 0 } ? entry.Brushes : sculpt.Brushes;
		sculpt.Brushes = canonical.Select( b => HunterGun.ScaledCopy( b, _gunScale ) ).ToList();

		go.LocalPosition = handOffset;
		go.LocalRotation = gunRotation.ToRotation();
		go.Tags.Add( HunterGun.CloneTag ); // tagged like the pawn's GunWorld — it's the gun, not body clay

		sculpt.Committed += OnGunCommitted;

		var session = sculpt.Components.Create<SculptEditSession>();
		session.Target = sculpt;
		session.OrbitCamera = _orbit; // NO PersistSlot: it would save display-scale brushes (see OnGunCommitted)

		_gunSculpt = sculpt;
		_gunSession = session;
	}

	// Every gun commit while weapon-editing: inverse-scale back to canonical and save the GunSlot — the same
	// funnel as HunterGun.OnEditCommitted, so the next round's hunter spawns holding exactly this gun. An
	// invalid shape (SculptBounds) is work-in-progress and never saved, mirroring the session's persist gate.
	void OnGunCommitted()
	{
		if ( !_editingGun || !_gunSculpt.IsValid() || _gunSculpt.Brushes is not { Count: > 0 } )
			return;

		var bounds = _gunSculpt.GameObject.Components.Get<SculptBounds>();
		if ( bounds.IsValid() && !bounds.EvaluateNow() )
			return;

		float inv = 1f / _gunScale;
		SculptLibrary.Save( new SculptLibrary.Entry
		{
			Name = SculptLibrary.GunSlot,
			Resolution = _gunSculpt.Resolution,
			FlipFaces = _gunSculpt.FlipFaces,
			Brushes = _gunSculpt.Brushes.Select( b => HunterGun.ScaledCopy( b, inv ) ).ToList(),
		} );
	}

	// The dock lands here: hand the stage (orbit camera + HUD) from one session to the other — each side commits
	// on its way out (SetActive( false ) is the same funnel any exit runs). The Workshop column is HEADS only, so
	// it steps aside while the gun is up.
	void SetEditPart( bool gun )
	{
		if ( !_session.IsValid() || !_gunSession.IsValid() )
			return;

		if ( gun )
		{
			if ( !_session.IsEditing || _gunSession.IsEditing )
				return;

			RememberView(); // the head's view, while it still owns the stage
			_session.SetActive( false );
			_editingGun = true;
			_gunSession.SetActive( true );
			ShowWorkshop( false );
			FrameGun();
		}
		else
		{
			if ( !_gunSession.IsEditing || _session.IsEditing )
				return;

			RememberView(); // the gun's view, while it still owns the stage
			_gunSession.SetActive( false );
			_editingGun = false;
			_session.SetActive( true );
			ShowWorkshop( true );
			FrameHead();
		}
	}

	void ShowWorkshop( bool show )
	{
		if ( !Hud.IsValid() || _workshop is null )
			return;

		Hud.WorkshopSave = show ? _workshop.Save : null;
		Hud.WorkshopLoad = show ? _workshop.Load : null;
		Hud.WorkshopClose = show ? _workshop.Close : null;
		if ( !show )
			Hud.WorkshopBrowserOpen = false;
	}

	// Frame the gun from the same front-on angle as the head, at its own fit distance.
	void FrameGun()
	{
		if ( !_orbit.IsValid() || !_gunSculpt.IsValid() )
			return;

		if ( s_gunView is { } saved )
		{
			ApplyView( saved );
			return;
		}

		if ( !Sdf.TryGetBounds( _gunSculpt.Brushes, out var bounds, SculptEditSession.PendingStamp( _gunSculpt ) ) )
			return;

		float radius = bounds.Size.Length * 0.5f * _gunSculpt.WorldScale.x;
		var (_, _, rot) = HeadFraming();
		_orbit.Pivot = _gunSculpt.WorldTransform.PointToWorld( bounds.Center );
		_orbit.Distance = radius > 0.01f ? GameSettings.EditFitDistance( radius, FramingMargin ) : 40f;
		_orbit.Angles = rot.Angles();
	}

	// ── Remembered views ─────────────────────────────────────────────────────────────────────────────────
	// The orbit rig's last view per part (pivot, distance, angles), so switching Head ↔ Weapon — or leaving
	// Customise and coming back — returns to exactly where you left each one. Plain world-space values: the menu
	// model always rebuilds on this same GameObject's transform. Static so a page exit/re-entry keeps them.
	static (Vector3 Pivot, float Distance, Angles Angles)? s_headView;
	static (Vector3 Pivot, float Distance, Angles Angles)? s_gunView;

	(Vector3 Pivot, float Distance, Angles Angles) CaptureView() => (_orbit.Pivot, _orbit.Distance, _orbit.Angles);

	void ApplyView( (Vector3 Pivot, float Distance, Angles Angles) v )
	{
		_orbit.Pivot = v.Pivot;
		_orbit.Distance = v.Distance;
		_orbit.Angles = v.Angles;
	}

	// Stash the live view into whichever part currently owns the stage.
	void RememberView()
	{
		if ( !_orbit.IsValid() || !_orbit.Enabled )
			return;

		if ( _editingGun )
			s_gunView = CaptureView();
		else
			s_headView = CaptureView();
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

		if ( s_headView is { } saved )
		{
			ApplyView( saved );
			return;
		}

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
	// at (GameSettings.EditFitDistance) — the same math as HunterController.FramingDistance.
	float FramingDistance()
	{
		const float fallback = 60f;

		if ( !_face.IsValid() || !Sdf.TryGetBounds( _face.Brushes, out var bounds, SculptEditSession.PendingStamp( _face ) ) )
			return fallback;

		float radius = bounds.Size.Length * 0.5f * _face.WorldScale.x;
		if ( radius <= 0.01f )
			return fallback;

		return GameSettings.EditFitDistance( radius, FramingMargin );
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
