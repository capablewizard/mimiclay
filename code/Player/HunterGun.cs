using System;

namespace Mimiclay;

/// <summary>
/// The hunter's detector gun — DISPLAY ONLY for now (the actual hitscan lives in
/// <see cref="HunterController.Shoot"/>). Two local clones of <see cref="GunPrefab"/> are spawned on every
/// machine (owner AND proxies — same pattern as the Eyes/footstep proxies use: purely cosmetic, driven by the
/// networked eye transform, so nothing here needs its own network spawn):
///
/// <list type="bullet">
/// <item>WORLD model ("GunWorld"): what everyone else sees — held on an ARM: a <see cref="Shoulder"/> pivot is
/// pinned to the body (an eye-relative offset swung by yaw only, so it stays on the pawn's right side and rides
/// crouches) and rotated by the full aim, and the gun hangs off its <see cref="Hand"/> child. Pitching the view
/// arcs the gun around the shoulder like an arm swinging, instead of the gun rigidly tracking the head. On the
/// owning machine it goes <see cref="SdfRaymarchRenderer.RenderHidden"/> in first person (shadows-only),
/// exactly like the rest of the pawn's own body.</item>
/// <item>VIEW model ("GunView"): what the owner sees — hung off the camera eye at a classic bottom-right
/// viewmodel offset, enabled only on the owning machine outside edit mode. Its SDF shadows are turned OFF
/// (a gun shadow floating at head height would read as an object in the world).</item>
/// </list>
///
/// Placement runs from <see cref="HunterController"/>'s single smoothed per-frame eye via <see cref="Place"/> —
/// never from a cached EyePosition (see the fixed-tick jitter rule) and never self-driven from OnUpdate, so the
/// gun can't lag or fight the camera by update order.
///
/// The clones are cosmetic: colliders and the through-wall outline are stripped (a gun outline under a hunter
/// pawn would glow for everyone via RoundOutlineSystem). The raymarch renderer assumes unit GameObject scale
/// (brush data is baked in world space), so the scales are applied by scaling BRUSH data, not the GameObject —
/// and each model's brushes are re-derived from a pristine prefab-scale source list whenever its scale property
/// changes, so <see cref="WorldGunScale"/> / <see cref="ViewGunScale"/> are live-tweakable while playing.
/// Editable guns later can build on the same clones.
/// </summary>
[Title( "Hunter Gun" )]
[Category( "Mimiclay" )]
[Icon( "colorize" )]
public sealed class HunterGun : Component
{
	/// <summary>Tag stamped on both gun clones so <see cref="HunterController"/>'s body-hide sweep skips them —
	/// the world gun must not be re-shown in edit mode by that sweep's rules, and the view gun must never be
	/// forced shadows-only (it manages its own visibility here).</summary>
	public const string CloneTag = "hunter_gun";

	/// <summary>The gun prefab both models are cloned from (SdfSculpture + raymarch renderer authored there).</summary>
	[Property] public string GunPrefab { get; set; } = "prefabs/saved/gun.prefab";

	/// <summary>World-model scale, applied to its brush data (the sculpt was authored at edit size, ~80 units —
	/// 2 metres; 0.5 is rifle size against the 72-unit pawn). Live: changing it rebuilds the gun on the spot.</summary>
	[Property, Range( 0.1f, 1f ), Group( "Scale" )] public float WorldGunScale { get; set; } = 0.5f;

	/// <summary>Viewmodel scale, independent of the world model's — a viewmodel usually reads better a touch
	/// smaller than the "real" gun. Live-tweakable like <see cref="WorldGunScale"/>.</summary>
	[Property, Range( 0.1f, 1f ), Group( "Scale" )] public float ViewGunScale { get; set; } = 0.5f;

	/// <summary>Field-cache resolution for the world model (voxels along the padded longest axis — overrides
	/// what the gun prefab authored). Matters because scaling the gun down does NOT shrink the field's fixed
	/// 16-unit BlendPad, so a small gun keeps fewer of these voxels for itself; raise this to claw detail back.
	/// Live-tweakable (the renderer re-bakes on change).</summary>
	[Property, Range( 8, 512 ), Group( "Scale" )] public int WorldFieldResolution { get; set; } = 256;

	/// <summary>Field-cache resolution for the viewmodel — it sits inches from the camera, so it earns the top
	/// end. 512 needs the sparse field (forced on when exceeded; dense caps at 256).</summary>
	[Property, Range( 8, 512 ), Group( "Scale" )] public int ViewFieldResolution { get; set; } = 512;

	/// <summary>The arm's pivot — the world gun rotates around THIS point with the aim. Its position is driven
	/// every frame (eye + yaw · <see cref="ShoulderOffset"/>, then the full aim rotation), so where it sits in
	/// the prefab is cosmetic; wire it so the <see cref="Hand"/> under it is authorable. Auto-resolved by child
	/// name ("Shoulder"), created if missing.</summary>
	[Property, Group( "Arm" )] public GameObject Shoulder { get; set; }

	/// <summary>The grip point, a child of <see cref="Shoulder"/> — the world gun is parented here, so its
	/// LOCAL position (how far in front of the shoulder the hand holds the gun) sets where the gun sits.
	/// Auto-resolved by child name ("Hand"), created if missing.</summary>
	[Property, Group( "Arm" )] public GameObject Hand { get; set; }

	/// <summary>Where the shoulder pivot sits relative to the EYE, in yaw space (x forward, y left, z up) — yaw
	/// only, never pitch, so looking down doesn't ride the shoulder up the body. Eye-relative so crouching
	/// carries the arm down automatically.</summary>
	[Property, Group( "Arm" )] public Vector3 ShoulderOffset { get; set; } = new( 0f, -9f, -13f );

	/// <summary>Extra offset of the gun from the <see cref="Hand"/> point, in hand space (x forward, y left,
	/// z up) — tune where the grip sits in the hand without moving the hand (and the arm arc) itself.</summary>
	[Property, Group( "Arm" )] public Vector3 HandOffset { get; set; } = Vector3.Zero;

	/// <summary>Viewmodel offset from the camera eye, in aim space (x forward, y left, z up), as authored at
	/// <see cref="ViewBaseFov"/>. At other FOVs the forward component is compensated (see Place) so the gun
	/// keeps the same screen position and apparent size instead of swimming with the FOV setting.</summary>
	[Property, Group( "Placement" )] public Vector3 ViewOffset { get; set; } = new( 15f, -8f, -12f );

	/// <summary>The camera FOV <see cref="ViewOffset"/> was tuned at. When the live camera FOV differs (user
	/// preference, zoom), the viewmodel's forward distance is scaled by tan(base/2)/tan(live/2) — that single
	/// scale keeps both the on-screen position AND the apparent size of the gun where you authored them.</summary>
	[Property, Group( "Placement" ), Range( 40f, 120f )] public float ViewBaseFov { get; set; } = 90f;

	/// <summary>Which render path the viewmodel draws through — live-tweakable debug switch while the anti-clip
	/// path is proven out. Normal = game pass (clips into walls, always visible). Viewmodel = native viewmodel
	/// layer (on top + altered depth — currently renders invisible, under investigation). OverlayNoDepth =
	/// after post, no depth at all (pure draw-over). OverlayFlag = the known-live ModelRenderer overlay path
	/// (after post, scene depth — likely still clips; diagnostic).</summary>
	[Property, Group( "Placement" )] public SdfViewLayer ViewLayerMode { get; set; } = SdfViewLayer.Normal;

	/// <summary>Mounting rotation of the gun on its parent (the hand for the world model, the aim for the view
	/// model). The sculpt's barrel runs along its local +y, so the default -90 yaw points it forward.</summary>
	[Property, Group( "Placement" )] public Angles RotationOffset { get; set; } = new( 0f, -90f, 0f );

	/// <summary>While this machine renders the first-person gun, switch the sun's screen-space CONTACT
	/// shadows off (restored the moment we're not first-person: edit mode, death, pawn teardown). The
	/// contact-shadow pass marches the depth buffer toward the light, and the viewmodel's depth-squashed
	/// near blob (which keeps screen-space AO neutral) reads as a solid occluder there — the gun fully
	/// self-shadows out of the directional light. Same depth chain feeds both effects, so they can't
	/// coexist; this trades a subtle world effect, per machine, for a lit gun. Cascade shadows unaffected.</summary>
	[Property, Group( "Placement" )] public bool DisableSunContactShadows { get; set; } = true;

	GameObject _world;
	GameObject _view;
	SdfRaymarchRenderer _worldSdf;
	SdfRaymarchRenderer _viewSdf;
	SdfSculpture _worldSculpt;
	SdfSculpture _viewSculpt;

	// The sun whose ContactShadows we override while first-person, and whether we currently hold an
	// override (only ever restore a value WE changed — if the scene authored them off, stay hands-off).
	DirectionalLight _sun;
	bool _sunOverridden;

	// The pristine prefab-scale brush list both models derive from, and the scale each model last applied
	// (0 = never — the first Place() always applies). Deriving from the SOURCE every time (instead of scaling
	// the live brushes in place) is what makes the scales freely re-tweakable: no compounding, no baseline
	// drift, and a snapshot-received clone (streamed at the owner's scale) self-heals to ours.
	List<SdfBrush> _source;
	float _worldApplied;
	float _viewApplied;

	protected override void OnStart()
	{
		// The arm hierarchy: pawn → Shoulder → Hand → GunWorld. Authored in hunter.prefab (so the hand offset
		// is tweakable in the editor); created here when dropped on a bare GameObject so it still works.
		Shoulder = Resolve( Shoulder, GameObject, "Shoulder", new Vector3( 0f, -9f, 50f ) );
		Hand = Resolve( Hand, Shoulder, "Hand", new Vector3( 26f, 0f, -2f ) );

		// By-name existence check first (like the hider's Disguise clone): a late joiner may receive the owner's
		// clones inside the scene snapshot, and spawning a second pair on top would double the gun.
		_world = EnsureClone( "GunWorld", Hand );
		_view = EnsureClone( "GunView", GameObject );

		_worldSdf = _world.IsValid() ? _world.Components.Get<SdfRaymarchRenderer>( FindMode.EverythingInSelfAndDescendants ) : null;
		_viewSdf = _view.IsValid() ? _view.Components.Get<SdfRaymarchRenderer>( FindMode.EverythingInSelfAndDescendants ) : null;
		_worldSculpt = _world.IsValid() ? _world.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndDescendants ) : null;
		_viewSculpt = _view.IsValid() ? _view.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndDescendants ) : null;

		_source = LoadSourceBrushes();

		// Viewmodel: no shadow of any kind — SdfShadows off stops the raymarch casting, MeshMode.Hidden stops
		// the legacy shadows-only sibling mesh taking over the job. Applied to snapshot-received clones too
		// (idempotent), since the owner's instance streams with these already set but a fresh one doesn't.
		// (ViewmodelLayer is asserted per frame in Place, from the live-tweakable UseViewmodelLayer.)
		if ( _viewSdf.IsValid() )
		{
			_viewSdf.SdfShadows = false;
			_viewSdf.MeshMode = SdfMeshMode.Hidden;
		}

		// Starts hidden everywhere; Place() enables it once this machine is known to be the owner. Ownership
		// resolves after OnStart (see HunterController), so this can't be decided here.
		if ( _view.IsValid() )
			_view.Enabled = false;
	}

	/// <summary>Drive the arm and viewmodel off the given eye — called by <see cref="HunterController"/> every
	/// frame on every machine, AFTER it has placed the camera, with the same smoothed eye.
	/// <paramref name="firstPerson"/> is true only on the owning machine outside edit mode: the viewmodel shows
	/// and the world model self-hides (shadows-only), mirroring the body treatment.</summary>
	public void Place( Vector3 eye, Angles aim, bool firstPerson )
	{
		// Scale-on-change, so the properties are live while playing. The world model on every machine; the
		// viewmodel only where it's shown (proxies never enable theirs — no point building it).
		ApplyScale( _worldSculpt, WorldGunScale, ref _worldApplied );
		if ( firstPerson )
			ApplyScale( _viewSculpt, ViewGunScale, ref _viewApplied );

		ApplySunContactShadows( firstPerson );

		var aimRot = aim.ToRotation();

		// The arm: pin the shoulder to the body (yaw-only swing so it stays at the pawn's side), rotate it with
		// the FULL aim. The hand — and the gun parented under it — arcs around this pivot like an arm would.
		if ( Shoulder.IsValid() )
		{
			Shoulder.WorldPosition = eye + Rotation.FromYaw( aim.yaw ) * ShoulderOffset;
			Shoulder.WorldRotation = aimRot;
		}

		// The world gun rides the Shoulder→Hand hierarchy; only its mount needs asserting (per frame so
		// HandOffset / RotationOffset stay live-tweakable in the editor).
		if ( _world.IsValid() )
		{
			_world.LocalPosition = HandOffset;
			_world.LocalRotation = RotationOffset.ToRotation();
		}

		if ( _worldSdf.IsValid() )
		{
			_worldSdf.RenderHidden = firstPerson;
			ApplyFieldResolution( _worldSdf, WorldFieldResolution );
		}

		if ( _view.IsValid() )
		{
			_view.Enabled = firstPerson;
			if ( firstPerson )
			{
				if ( _viewSdf.IsValid() )
				{
					ApplyFieldResolution( _viewSdf, ViewFieldResolution );
					_viewSdf.ViewLayer = ViewLayerMode;
				}

				// FOV compensation: scale the FORWARD distance only, by tan(base/2)/tan(live/2). Screen
				// position is lateral/(forward·tan(half)) and apparent size is modelSize/(forward·tan(half)) —
				// scaling forward alone cancels the tan out of both, so the gun stays visually put as the FOV
				// changes. (Perspective distortion at extreme FOVs remains — that's inherent to rendering in
				// the main projection.)
				var offset = ViewOffset.WithX( ViewOffset.x * FovCompensation() );

				_view.WorldPosition = eye + aimRot * offset;
				_view.WorldRotation = aimRot * RotationOffset.ToRotation();
			}
		}
	}

	// tan(base/2)/tan(live/2) — 1 when the live camera FOV matches ViewBaseFov. Reads the live FOV off the
	// shared camera each frame so preference changes and zoom effects are tracked automatically.
	float FovCompensation()
	{
		var cam = Scene.Camera;
		if ( !cam.IsValid() )
			return 1f;

		float baseTan = MathF.Tan( ViewBaseFov.DegreeToRadian() * 0.5f );
		float liveTan = MathF.Tan( cam.FieldOfView.DegreeToRadian() * 0.5f );
		return liveTan > 0.001f ? baseTan / liveTan : 1f;
	}

	// Assert a field resolution on a clone's renderer. Safe to call every frame: the renderer folds
	// FieldResolution into its field hash and only re-bakes when the value actually changes. Above 256 the
	// dense field would silently clamp (a full 512³ volume is ~537 MB), so the sparse path is forced on.
	static void ApplyFieldResolution( SdfRaymarchRenderer sdf, int resolution )
	{
		sdf.FieldResolution = resolution;
		if ( resolution > 256 )
			sdf.SparseField = true;
	}

	// Hold the sun's contact shadows off while the first-person gun is on screen (see DisableSunContactShadows),
	// give them back the moment it isn't. Purely local rendering state — scene-component property changes
	// don't replicate, so hiders/other machines keep their contact shadows untouched.
	void ApplySunContactShadows( bool firstPerson )
	{
		bool want = firstPerson && DisableSunContactShadows && ViewLayerMode != SdfViewLayer.Normal;

		if ( want && !_sunOverridden )
		{
			if ( !_sun.IsValid() )
				_sun = Scene.GetAllComponents<DirectionalLight>().FirstOrDefault( l => l.ContactShadows );

			if ( _sun.IsValid() )
			{
				_sun.ContactShadows = false;
				_sunOverridden = true;
			}
		}
		else if ( !want && _sunOverridden )
		{
			if ( _sun.IsValid() )
				_sun.ContactShadows = true;
			_sunOverridden = false;
		}
	}

	protected override void OnDisabled()
	{
		// Pawn teardown/disable while first-person — give the sun its contact shadows back.
		ApplySunContactShadows( false );
	}

	// Swap the sculpture's brushes for a freshly scaled derivation of the source list, only when the wanted
	// scale actually changed (float-equal compare: a slider tweak lands as one rebuild). Builds are async and
	// model-cached, and the sculpt editor rebuilds far more aggressively than a slider drag ever will.
	void ApplyScale( SdfSculpture sculpt, float scale, ref float applied )
	{
		if ( !sculpt.IsValid() || _source is null || applied == scale )
			return;

		applied = scale;
		sculpt.Brushes = _source.Select( b => ScaledCopy( b, scale ) ).ToList();
		sculpt.Rebuild();
	}

	// A copy of the brush with every length-like field scaled together, so the shape is a true uniform
	// miniature of the source. BoundingRadius/LocalCentre are computed properties — nothing to touch.
	static SdfBrush ScaledCopy( SdfBrush src, float s )
	{
		var b = src.Copy();
		b.Position *= s;
		b.Size *= s;
		b.Blend *= s;
		b.Rounding = MathF.Max( 0.05f, b.Rounding * s );

		if ( b.Points is not null )
		{
			for ( int i = 0; i < b.Points.Count; i++ )
			{
				var p = b.Points[i];
				b.Points[i] = new Vector4( p.x * s, p.y * s, p.z * s, p.w * s );
			}
		}

		return b;
	}

	// The pristine prefab-scale brushes, read straight from the prefab's loaded scene — no instantiation, and
	// it works even when BOTH clones pre-existed at some already-applied scale (the late-join snapshot case,
	// where the live brushes are not a usable baseline). Copies, so nothing here can mutate the prefab's data.
	List<SdfBrush> LoadSourceBrushes()
	{
		var prefab = ResourceLibrary.Get<PrefabFile>( GunPrefab );
		var sculpt = prefab is null
			? null
			: SceneUtility.GetPrefabScene( prefab )?.GetAllComponents<SdfSculpture>().FirstOrDefault();

		if ( sculpt is null || sculpt.Brushes is null )
		{
			// Fallback: a fresh clone still carries unscaled prefab brushes at this point (scaling only ever
			// happens in Place). Only a snapshot-received clone would be wrong here, and then we'd rather show
			// the owner's scale than nothing.
			var live = _worldSculpt.IsValid() ? _worldSculpt : _viewSculpt;
			if ( !live.IsValid() || live.Brushes is null )
				return null;

			Log.Warning( $"Gun prefab '{GunPrefab}' scene unreadable — sourcing brushes from the live clone." );
			return live.Brushes.Select( b => b.Copy() ).ToList();
		}

		return sculpt.Brushes.Select( b => b.Copy() ).ToList();
	}

	// Resolve an arm point: the wired property, else an existing child by name, else a freshly created one at
	// the given local offset (the bare-GameObject fallback — the prefab authors these).
	static GameObject Resolve( GameObject wired, GameObject parent, string name, Vector3 localPos )
	{
		if ( wired.IsValid() )
			return wired;

		var existing = parent.Children.FirstOrDefault( c => c.Name == name );
		if ( existing.IsValid() )
			return existing;

		var go = new GameObject( true, name );
		go.Parent = parent;
		go.LocalPosition = localPos;
		go.LocalRotation = Rotation.Identity;
		return go;
	}

	// Find an existing clone by name under the given parent, else spawn a fresh one there (unscaled — Place()
	// applies the wanted scale on the first frame, superseding the clone's own initial build).
	GameObject EnsureClone( string name, GameObject parent )
	{
		if ( !parent.IsValid() )
			return null;

		var existing = parent.Children.FirstOrDefault( c => c.Name == name );
		if ( existing.IsValid() )
			return existing;

		var prefab = ResourceLibrary.Get<PrefabFile>( GunPrefab );
		if ( prefab is null )
		{
			Log.Warning( $"Gun prefab '{GunPrefab}' not found — hunter gets no gun model." );
			return null;
		}

		var go = SceneUtility.GetPrefabScene( prefab )?.Clone();
		if ( !go.IsValid() )
			return null;

		go.Name = name;
		go.Parent = parent;
		go.Tags.Add( CloneTag );
		go.WorldPosition = WorldPosition; // parked on the pawn until the first Place() this same frame

		StripNonVisuals( go );

		return go;
	}

	// The prefab is a full sculpt-save export; as an attachment only the visual stack survives. Colliders would
	// bind into the pawn's physics, and the outline would glow through walls for everyone (RoundOutlineSystem's
	// hunter rule applies to every outline under the pawn).
	static void StripNonVisuals( GameObject go )
	{
		foreach ( var c in go.Components.GetAll<Component>( FindMode.EverythingInSelfAndDescendants ).ToArray() )
		{
			if ( c is SdfCollider or ModelCollider or SdfHighlightOutline )
				c.Destroy();
		}
	}
}
