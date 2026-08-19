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
		_workshop = null;

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
