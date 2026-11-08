using System;

namespace Mimiclay;

/// <summary>
/// THE free-fly camera — every "detach the view and fly around" in the game is one of these:
/// <list type="bullet">
/// <item>an ELIMINATED Teams prop's bench (spawned by <see cref="RoundManager"/>; their pawn is gone),</item>
/// <item>everyone while the map waits for all players to load (<see cref="LoadGate"/>), and joiners who arrive
/// too late in the round for a role (also RoundManager),</item>
/// <item>a living prop's F scouting cam (<see cref="HiderController"/>) and anyone's B spectate in creative
/// (<see cref="HunterController"/> / HiderController) — their pawn stays put as <see cref="LeftBehind"/>.</item>
/// </list>
/// Local only: the camera itself never touches the network. With <see cref="WantsGhost"/> it spawns a
/// <see cref="SpectatorGhost"/> (the player's head, drawn as a ghost) that follows it — when, whether it's networked and
/// which machines draw it are all <see cref="GhostRules"/> (the round's "Show ghosts" option, teams, phase).
///
/// Drives the one shared camera directly (<see cref="MainCamera.Set"/>, transform only, per the shared-camera
/// rule). It takes off from wherever the camera was, so every hand-off into it is one continuous view, and eases
/// the FOV to the player's spectate FOV at its own pace (<see cref="FovLerpSpeed"/>).
/// </summary>
[Title( "Spectator Controller" )]
[Category( "Mimiclay" )]
[Icon( "visibility" )]
public sealed class SpectatorController : Component
{
	/// <summary>The ghost every free cam spawns when <see cref="WantsGhost"/> is set.</summary>
	public const string GhostPrefabPath = "prefabs/ghost.prefab";

	/// <summary>The free cam this machine is flying, if any — at most one at a time (a newer one replaces it).</summary>
	public static SpectatorController Local { get; private set; }

	/// <summary>Is this machine flying a free cam right now?</summary>
	public static bool Flying => Local.IsValid();

	/// <summary>The raw key that toggles a living player's free cam here, or null where there isn't one: F for a
	/// prop in a prop-hunt round (hunters have none there), B for anyone in creative — F is creative's interact key.
	/// Raw keyboard keys rather than input actions: actions can only be added in the editor's Project Settings.</summary>
	public static string ToggleKey
		=> RoundManager.Current.IsValid() ? "F"
		: CreativeManager.Current.IsValid() ? "B"
		: null;

	/// <summary>Was <see cref="ToggleKey"/> pressed this frame?</summary>
	public static bool TogglePressed => ToggleKey is { } key && Input.Keyboard.Pressed( key );

	/// <summary>Base fly speed, world units/sec.</summary>
	[Property] public float FlySpeed { get; set; } = 350f;

	/// <summary>Speed multiplier while "run" is held.</summary>
	[Property] public float FastMultiplier { get; set; } = 3f;

	/// <summary>Collision radius — solid, gliding along whatever it hits rather than stopping dead (or phasing
	/// out of the map). Small enough to thread doorways, big enough that the view doesn't poke through thin walls.</summary>
	[Property] public float CollisionRadius { get; set; } = 8f;

	/// <summary>Momentum ease time (s): keys steer a wish velocity and the actual velocity eases toward it
	/// (accelerate in, glide to a stop). 0 = instant, no glide.</summary>
	[Property, Range( 0f, 1f )] public float Smoothing { get; set; } = 0.3f;

	/// <summary>How fast the FOV eases toward <see cref="GameSettings.SpectateFov"/> on entry (exponential, per
	/// second — lower = slower/smoother). Our own blend, independent of MainCamera's shared FOV lerp.</summary>
	[Property] public float FovLerpSpeed { get; set; } = 3f;

	/// <summary>The pawn this free cam was launched from, if it's still standing in the level (a scouting prop, a
	/// creative player). Ignored by the collision traces, so the camera can take off from inside it.</summary>
	public GameObject LeftBehind { get; set; }

	/// <summary>Give this free cam a ghost of the player's head (made and networked as <see cref="GhostRules"/> allow).</summary>
	public bool WantsGhost { get; set; }

	/// <summary>The live ghost, if one was spawned.</summary>
	public GameObject Ghost => _ghost;

	/// <summary>Is the view far enough out (third person) that we see our own ghost?</summary>
	public bool ShowsOwnGhost => Boom.Blend > 0.15f;

	/// <summary>Is this machine's free cam alt-orbiting (camera swung off the aim)? The crosshair hides then, as
	/// it does for the hunter's own alt-orbit.</summary>
	public static bool LocalAltOrbiting => Local.IsValid() && Local.Boom.AltOrbiting;

	// The view is the hunter's third-person camera (ShoulderBoom, shared with HunterController): first person on the
	// flying point, third person on the over-the-shoulder boom behind it — eased between, with the hunter's alt-orbit.
	// Lazy, so a hotload that lands mid-flight (new fields reset, OnStart long gone) rebuilds it instead of throwing.
	ShoulderBoom _boom;
	ShoulderBoom Boom => _boom ??= CreateBoom();

	Vector3 _pos;        // the flying point: the camera in first person, the boom's eye (the ghost's head) in third
	Angles _angles;      // the aim we fly along — frozen while an alt-orbit swings the camera
	Vector3 _velocity;
	float _fov;
	GameObject _ghost;

	/// <summary>Start flying: a fresh local free cam taking off from the live camera. Replaces any free cam already
	/// running on this machine.</summary>
	public static SpectatorController Begin( GameObject leftBehind, bool ghost )
	{
		if ( Local.IsValid() )
			Local.GameObject.Destroy();

		// NotSaved: the camera is runtime state — editor play mode must never bake it into the .scene on the next
		// save (the RoundOutlineSystem hover-component lesson).
		var go = new GameObject( true, "Spectator (local)" );
		go.Flags |= GameObjectFlags.NotSaved;

		var cam = go.Components.Create<SpectatorController>();
		cam.LeftBehind = leftBehind;
		cam.WantsGhost = ghost;
		return cam;
	}

	/// <summary>SimLab's observer: put the free cam at a point, looking along <paramref name="angles"/>, at rest.</summary>
	internal void LabPlace( Vector3 pos, Angles angles )
	{
		_pos = pos;
		_angles = angles with { roll = 0f };
		_velocity = Vector3.Zero;
	}

	/// <summary>Stop flying (no-op when this machine isn't). The caller takes the camera back on the same frame.</summary>
	public static void End()
	{
		if ( Local.IsValid() )
			Local.GameObject.Destroy();
		Local = null;
	}

	protected override void OnEnabled()
	{
		Local = this;
	}

	protected override void OnDisabled()
	{
		if ( Local == this )
			Local = null;
	}

	protected override void OnDestroy()
	{
		// The ghost fades out where we left it (every machine), then removes itself — it doesn't pop.
		if ( _ghost.IsValid() )
		{
			var ghost = _ghost.Components.Get<SpectatorGhost>();
			if ( ghost.IsValid() )
				ghost.Dismiss();
			else
				_ghost.Destroy();
		}
		_ghost = null;
	}

	protected override void OnStart()
	{
		// Take off from the live camera — the exact view the player had a moment ago. The view always starts in
		// first person (on that camera) and eases out to third if that's the preference, so the hand-off is seamless.
		_pos = MainCamera.Position;
		_angles = MainCamera.Angles with { roll = 0f };
		_fov = Scene.Camera.IsValid() ? Scene.Camera.FieldOfView : MainCamera.Fov;

		UpdateGhostLife();
	}

	// The ghost is always made — we always see our own — but goes on the network only once someone else could see it
	// (GhostRules.GhostShared — under "Show ghosts: None" not until the Reveal) and its owner's body is networked
	// (GhostRules.MayNetwork — an Infection prop's Hide-phase ghost stays local). Re-checked every frame, so either can
	// turn true mid-flight.
	void UpdateGhostLife()
	{
		if ( !WantsGhost )
			return;

		if ( !_ghost.IsValid() )
			SpawnGhost();

		if ( _ghost.IsValid() && !_ghost.Network.Active && GhostRules.GhostShared && GhostRules.MayNetwork( LeftBehind ) )
		{
			_ghost.NetworkSpawn( new NetworkSpawnOptions
			{
				Owner = Connection.Local,
				OrphanedMode = NetworkOrphaned.Destroy,
			} );
		}
	}

	// The boom, tuned like the hunter we left (or the hunter defaults — a prop or a spectator has no boom of its own).
	ShoulderBoom CreateBoom()
	{
		var boom = new ShoulderBoom();
		var hunter = LeftBehind.IsValid() ? LeftBehind.Components.Get<HunterController>() : null;
		hunter?.ApplyBoomTuning( boom );
		return boom;
	}

	protected override void OnUpdate()
	{
		UpdateGhostLife();

		// A cursor overlay owns the mouse — hold the view rather than navigate behind the menu (the same
		// gate every other camera driver uses).
		if ( PauseMenu.IsOpen || RoundSetup.IsOpen )
		{
			Apply();
			return;
		}

		Mouse.Visibility = MouseVisibility.Hidden;

		// First / third person, like the hunter's toggle (the "View" action).
		if ( Input.Pressed( "ToggleView" ) )
			GameSettings.SpectatorThirdPerson = !GameSettings.SpectatorThirdPerson;

		// Alt-orbit (third person): the mouse swings the camera around the ghost while the aim — and so the flight
		// direction — stays frozen, exactly the hunter's gesture. Otherwise the mouse looks.
		Boom.UpdateAltOrbit( GameSettings.SpectatorThirdPerson, _angles );
		if ( !Boom.AltOrbiting )
		{
			_angles += Input.AnalogLook;
			_angles = _angles with { pitch = Math.Clamp( _angles.pitch, -89f, 89f ), roll = 0f };
		}

		var move = Vector3.Zero;
		if ( Input.Down( "forward" ) ) move += Vector3.Forward;
		if ( Input.Down( "backward" ) ) move += Vector3.Backward;
		if ( Input.Down( "left" ) ) move += Vector3.Left;
		if ( Input.Down( "right" ) ) move += Vector3.Right;

		// Horizontal + pitch fly relative to the aim (look up, fly up); vertical (jump/duck) is world-space on
		// top — the standard noclip/spectator feel.
		var wish = Vector3.Zero;
		if ( move.LengthSquared > 0.001f )
			wish += _angles.ToRotation() * move.Normal;
		if ( Input.Down( "jump" ) ) wish += Vector3.Up;
		if ( Input.Down( "duck" ) ) wish += Vector3.Down;

		var wishVel = wish * FlySpeed * (Input.Down( "run" ) ? FastMultiplier : 1f);
		_velocity = Smoothing > 0.001f
			? Vector3.Lerp( _velocity, wishVel, 1f - MathF.Exp( -Time.Delta * 3f / Smoothing ) )
			: wishVel;

		var from = _pos;
		_pos = Slide( _pos, _velocity * Time.Delta );

		// Re-derive velocity from the distance actually covered, so a wall bleeds off the blocked component
		// (the slide keeps the tangential part) instead of momentum silently pressing into geometry.
		if ( Time.Delta > 0f )
			_velocity = (_pos - from) / Time.Delta;

		Apply();
	}

	void Apply()
	{
		// The boom places the camera: on the flying point in first person, over the shoulder in third. Its sweep
		// ignores our own ghost and the body we left (and, through the world-only rule, all clay).
		var pose = Boom.Place( Scene, _pos, _angles, GameSettings.SpectatorThirdPerson, _ghost, LeftBehind );
		MainCamera.Set( pose.Position, pose.Rotation );

		// Ease the FOV ourselves and hand MainCamera the eased value, so this transition's pace is tunable without
		// touching MainCamera's shared lerp (hunter zoom, orbit/hunter swap ride that one).
		_fov = MathX.Lerp( _fov, GameSettings.SpectateFov, 1f - MathF.Exp( -FovLerpSpeed * Time.Delta ) );
		MainCamera.SetFov( _fov, lerp: false );

		// The ghost faces the AIM, not the swung camera — an alt-orbit comes round to look it in the face.
		if ( _ghost.IsValid() )
			_ghost.Components.Get<SpectatorGhost>()?.Follow( _pos, _angles.ToRotation() );
	}

	// ── Ghost ──────────────────────────────────────────────────────────────────────────────────────────────────
	// Cloned DISABLED, dressed in this machine's saved head, then enabled — the same order as the hunter pawn
	// (RoundManager.SpawnOwnPawn), so the prefab's default head never builds and the spawn snapshot (when
	// UpdateGhostLife networks it) ships the real one. Owned by this connection and destroyed with it, and faded out
	// (SpectatorGhost.Dismiss) when this camera goes.
	void SpawnGhost()
	{
		var file = ResourceLibrary.Get<PrefabFile>( GhostPrefabPath );
		var prefab = file is null ? null : SceneUtility.GetPrefabScene( file );
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"SpectatorController: no ghost prefab at {GhostPrefabPath}." );
			return;
		}

		// The prefab root's scale rides the clone so SpectatorGhost can fold it into the head (see its Scale).
		_ghost = prefab.Clone( new CloneConfig( new Transform( _pos, _angles.ToRotation(), prefab.LocalScale ), startEnabled: false,
			name: $"Ghost {Connection.Local?.DisplayName}" ) );
		_ghost.Flags |= GameObjectFlags.NotSaved;

		HunterController.WearSavedHead( _ghost );
		_ghost.Components.Get<SpectatorGhost>( FindMode.EverythingInSelf )?.ApplyScale();

		// Local until UpdateGhostLife puts it on the network (offline, for good — still worth having: it's what our own
		// third person orbits).
		_ghost.Enabled = true;
	}

	// Multi-bump slide: keep re-deflecting off every surface met along the step so corners glide instead of
	// sticking, holding a small skin back from each hit so the next iteration's trace starts clear of the surface.
	const int SlideIterations = 4;
	const float SlideSkin = 0.25f;

	Vector3 Slide( Vector3 from, Vector3 delta )
	{
		var pos = from;
		var remaining = delta;

		for ( int i = 0; i < SlideIterations && remaining.LengthSquared > 0.0001f; i++ )
		{
			var to = pos + remaining;
			var trace = Scene.Trace.Ray( pos, to ).Radius( CollisionRadius );
			if ( LeftBehind.IsValid() )
				trace = trace.IgnoreGameObjectHierarchy( LeftBehind );
			var tr = trace.Run();

			if ( !tr.Hit )
			{
				pos = to;
				break;
			}

			// Advance to just short of the hit (the skin), then project whatever distance was LEFT onto the
			// hit plane for the next iteration to attempt.
			var stepLen = remaining.Length;
			var keepFrac = stepLen > SlideSkin ? MathF.Max( 0f, tr.Fraction - SlideSkin / stepLen ) : 0f;
			pos += remaining * keepFrac;

			var leftover = remaining * (1f - tr.Fraction);
			remaining = leftover - Vector3.Dot( leftover, tr.Normal ) * tr.Normal;
		}

		return pos;
	}
}
