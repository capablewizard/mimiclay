using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The lobby's tutorial character: scene-placed clay (the standard saved-prop composition) that teaches the
/// sculpt editor. He wears the claim affordance — the same crosshair hover and "E" paper toast as claimable
/// props — but his E never goes near <see cref="PropClaims"/>: it opens a LOCAL, in-place
/// <see cref="SculptEditSession"/> on him (the MenuCustomise pattern — session + orbit rig created in code,
/// PersistSlot empty), so every player can run the tutorial simultaneously on their own machine and nobody
/// fights over him. No <see cref="SdfNetworkSync"/> is ever attached, so nothing edited into him syncs; his
/// canonical shape is restored on every exit path (session exit, pawn loss, lobby launch, play stop).
///
/// Division of labour: <see cref="PropClaims.IsClaimable"/> excludes him (this component is the marker);
/// <see cref="HunterController.UpdateClaimHover"/> publishes the hover and routes E here;
/// <see cref="RoundOutlineSystem"/> arbitrates outline VISIBILITY through <see cref="OutlineVisible"/>, while
/// this component is the single writer of the outline's LOOK (the hover glow).
///
/// Snapshot caveat, accepted for now: he's a scene object, so a late joiner receives the HOST's live brush
/// state — if the host is mid-tutorial at that instant the joiner sees the host's edits until the host exits
/// (the restore never syncs; nothing anyone else does ever shows).
/// </summary>
[Title( "Tutorial NPC" )]
[Category( "Mimiclay" )]
[Icon( "school" )]
public sealed class TutorialNpc : Component
{
	/// <summary>The clay he IS. Left null in the prefab and resolved off this GameObject.</summary>
	[Property] public SdfSculpture Sculpture { get; set; }

	/// <summary>Breathing room when the edit camera frames him on entry — same meaning as the hunter's
	/// EditFramingMargin: 1 = he exactly fills the frame.</summary>
	[Property, Range( 1f, 3f )] public float FramingMargin { get; set; } = 1.5f;

	/// <summary>Pitch the edit camera opens at (degrees; positive looks slightly down at him).</summary>
	[Property] public float EntryPitch { get; set; } = 10f;

	// The hover look: the theme's craft green ($craft-green, "additive / positive") — deliberately NOT the
	// claims hover amber, so "learn here" and "take this" never read as the same offer. Same width so the
	// two prompts still feel like one family of affordance.
	static readonly Color GlowColor = new( 0.275f, 0.635f, 0.243f );
	const float HoverWidth = 5f;

	/// <summary>The tutorial running on THIS machine, or null — one at a time, its session owns the screen.</summary>
	public static TutorialNpc Running { get; private set; }

	/// <summary>The tutorial character under the local hunter's crosshair, freshness-gated exactly like
	/// <see cref="PropClaims.LocalHoverSculpture"/> — the publisher can vanish mid-hover, so staleness is
	/// told by age, never by relying on someone clearing it.</summary>
	public static TutorialNpc LocalHover
		=> _localHover.IsValid() && _hoverAge < 0.1f ? _localHover : null;
	static TutorialNpc _localHover;
	static RealTimeSince _hoverAge;

	/// <summary>Stamp this frame's hover (null = the crosshair is elsewhere).</summary>
	public static void SetLocalHover( TutorialNpc npc )
	{
		_localHover = npc;
		_hoverAge = 0f;
	}

	/// <summary>The tutorial character a traced sculpture belongs to, or null — the hover classifier.</summary>
	public static TutorialNpc Of( SdfSculpture sculpture )
		=> sculpture.IsValid()
			? sculpture.Components.Get<TutorialNpc>( FindMode.EverythingInSelfAndAncestors )
			: null;

	/// <summary>Should his outline render right now? Read by <see cref="RoundOutlineSystem"/>'s claims branch
	/// (which asserts Hidden on every outline in the scene each frame): the hover glow while he's idle,
	/// nothing while his guided session runs (the editor's own selection takes over).</summary>
	public bool OutlineVisible => Running != this && Hovered;

	bool Hovered => LocalHover == this;

	// Live instances, for the play-end sweep — component teardown alone isn't trusted at editor Stop (the
	// MenuCustomise flag-bake lesson), and in-editor play mutates the OPEN scene, so an unrestored shape or a
	// surviving runtime outline would bake into lobby.scene on the next save.
	static readonly HashSet<TutorialNpc> _live = new();

	// The guided session's runtime rig (NotSaved child), alive only while the tutorial runs.
	GameObject _rig;
	SculptEditSession _session;
	OrbitCameraController _orbit;
	HunterController _hunter;

	// His canonical shape, snapshotted at entry and restored on every exit.
	List<SdfBrush> _restore;

	// Outline we drive; created on demand (the RoundOutlineSystem recipe — never a second beside an authored
	// one: two live groups read each other's surfaces as occluders), destroyed on teardown if we made it.
	SdfHighlightOutline _outline;
	bool _outlineMade;

	// His authored ClayBoil, churned while hovered — same "you can take this" juice as claimable clay. Safe as
	// single writer: RoundOutlineSystem never touches an authored boil that isn't the CLAIMS hover, and he's
	// excluded from claims entirely. The authored activation is captured once and restored on teardown.
	ClayBoil _boil;
	BoilActivation _boilRest;
	bool _boilCaptured;

	protected override void OnAwake()
	{
		Sculpture ??= Components.Get<SdfSculpture>();
	}

	protected override void OnEnabled() => _live.Add( this );

	protected override void OnDisabled()
	{
		if ( Running == this )
		{
			if ( _session.IsValid() && _session.IsEditing )
				_session.SetActive( false );
			EndTutorial();
		}

		TeardownPresentation();
		RestoreCanonical();

		if ( _localHover == this )
			_localHover = null;
		_live.Remove( this );
	}

	/// <summary>Play is ending (editor Stop / app close) — restore every live character and drop everything
	/// runtime-made, however teardown order fell out. Called from <see cref="SessionResetSystem"/>.</summary>
	internal static void SweepPlayEnd()
	{
		foreach ( var npc in _live.ToArray() )
		{
			if ( !npc.IsValid() )
				continue;

			npc.TeardownPresentation();
			npc.RestoreCanonical();
		}

		_live.Clear();
		_localHover = null;
		Running = null;

		TutorialDirector.SweepPlayEnd(); // and the step machine's HUD-flag restore + statics
	}

	/// <summary>The E press: open the guided edit session on this character, locally, in place. The initiating
	/// pawn freezes through <see cref="HunterController.ExternalSession"/> (folded into its EditMode) and gets
	/// the screen back the moment the session ends.</summary>
	public void BeginTutorial( HunterController hunter )
	{
		if ( Running.IsValid() || !Sculpture.IsValid() || !hunter.IsValid() )
			return;
		if ( RoundManager.ControlsLocked )
			return;

		// Canonical shape, restored on every exit — snapshotted at entry so a re-run after a scene edit
		// restores what the scene actually authored.
		_restore = Sculpture.Brushes?.Select( b => b.Copy() ).ToList();

		_rig = new GameObject( true, "Tutorial Rig" );
		_rig.Flags |= GameObjectFlags.NotSaved; // runtime-only: never let this end up serialised into the scene
		_rig.SetParent( GameObject, false );

		_orbit = _rig.Components.Create<OrbitCameraController>();
		_orbit.Enabled = false;  // only live while editing; the session toggles it
		_orbit.MinDistance = 8f; // let the player get right up close

		// The guided session: local-only by construction — PersistSlot stays empty (nothing writes to disk)
		// and no SdfNetworkSync exists on him (nothing goes on the wire). SetActive is called directly, the
		// hunter's ToggleEdit ordering: the orbit camera enables synchronously inside it, so the framing
		// write below lands after the rig's enable-seed and sticks.
		_session = _rig.Components.Create<SculptEditSession>();
		_session.Target = Sculpture;
		_session.OrbitCamera = _orbit;

		_hunter = hunter;
		hunter.ExternalSession = _session;

		_session.SetActive( true );
		FrameNpc();

		// The step machine, on the same rig — it can never outlive the session, and its OnStart (next tick)
		// finds the session already editing.
		var director = _rig.Components.Create<TutorialDirector>();
		director.Session = _session;

		Running = this;
	}

	protected override void OnUpdate()
	{
		if ( Running == this )
			UpdateRunning();

		UpdatePresentation();
	}

	// Watch the running session: forced teardowns first (the pawn died or swapped out from under it, or the
	// lobby locked controls for the launch countdown — neither can wait on the exit dialog), then notice any
	// end — Q/dialog exit included — and clean up.
	void UpdateRunning()
	{
		if ( _session.IsValid() && _session.IsEditing
			&& (!_hunter.IsValid() || RoundManager.ControlsLocked) )
			_session.SetActive( false );

		if ( !_session.IsValid() || !_session.IsEditing )
			EndTutorial();
	}

	void EndTutorial()
	{
		if ( Running == this )
			Running = null;

		if ( _hunter.IsValid() && _hunter.ExternalSession == _session )
			_hunter.ExternalSession = null;
		_hunter = null;

		// The session already deactivated (that's what got us here), so the deferred destroy's OnDisabled
		// finds nothing pending and can't re-commit over the restore below.
		if ( _rig.IsValid() )
			_rig.Destroy();
		_rig = null;
		_session = null;
		_orbit = null;

		RestoreCanonical();
	}

	// Put the canonical shape back. A bare Rebuild is correct here — deliberately outside the commit funnel,
	// like SdfNetworkSync's remote applies: the session is gone, its undo history is already cleared, and a
	// restore must never read as an edit.
	void RestoreCanonical()
	{
		if ( _restore is null || !Sculpture.IsValid() )
			return;

		if ( SdfSculpture.ContentHash( Sculpture.Brushes, Sculpture.Resolution, Sculpture.FlipFaces )
			!= SdfSculpture.ContentHash( _restore, Sculpture.Resolution, Sculpture.FlipFaces ) )
		{
			Sculpture.Brushes = _restore.Select( b => b.Copy() ).ToList();
			Sculpture.Rebuild();
		}

		_restore = null;
	}

	// ── Presentation: the hover glow + the hover boil ──────────────────────────────────────────────────────

	void UpdatePresentation()
	{
		bool hovered = Hovered && Running != this;

		// Boil churn while hovered, authored activation otherwise — captured once so teardown can put the
		// mapper's dial back exactly.
		_boil = _boil.IsValid() ? _boil : Components.Get<ClayBoil>( includeDisabled: true );
		if ( _boil.IsValid() )
		{
			if ( !_boilCaptured )
			{
				_boilRest = _boil.Activation;
				_boilCaptured = true;
			}
			_boil.Activation = hovered ? BoilActivation.Always : _boilRest;
		}

		if ( !OutlineVisible )
		{
			// Keep the component (RoundOutlineSystem gates Hidden, and GetAllComponents skips disabled) —
			// this write only matters in scenes where no gating system runs.
			if ( _outline.IsValid() )
				_outline.Hidden = true;
			return;
		}

		EnsureOutline();
		if ( !_outline.IsValid() )
			return;

		// The claims hover look in the tutorial green — same alphas/width, different offer.
		_outline.Hidden = false;
		_outline.ColorOverride = GlowColor;
		_outline.ObscuredColorOverride = GlowColor.WithAlpha( 0.35f );
		_outline.InsideColorOverride = GlowColor.WithAlpha( 0.08f );
		_outline.InsideObscuredColorOverride = GlowColor.WithAlpha( 0.08f );
		_outline.WidthOverride = HoverWidth;
	}

	void EnsureOutline()
	{
		if ( _outline.IsValid() )
			return;

		// An authored outline (some saved-prop exports carry one) is reused, never doubled.
		_outline = Components.Get<SdfHighlightOutline>( FindMode.EverythingInSelfAndDescendants );
		if ( _outline.IsValid() )
			return;

		_outline = Components.Create<SdfHighlightOutline>();
		_outline.IgnoreDepthOfField = true; // the look comes from the overrides; this is the one flag they don't cover
		_outlineMade = true;
	}

	// Drop everything presentation-related the way we found it: a runtime-created outline is destroyed (a
	// survivor on a scene object bakes into the .scene on an in-editor save), an authored one gets its
	// overrides nulled; the authored boil dial goes back.
	void TeardownPresentation()
	{
		if ( _outline.IsValid() )
		{
			if ( _outlineMade )
			{
				_outline.Destroy();
			}
			else
			{
				_outline.ColorOverride = null;
				_outline.ObscuredColorOverride = null;
				_outline.InsideColorOverride = null;
				_outline.InsideObscuredColorOverride = null;
				_outline.WidthOverride = null;
			}
		}
		_outline = null;
		_outlineMade = false;

		if ( _boil.IsValid() && _boilCaptured )
			_boil.Activation = _boilRest;
		_boilCaptured = false;
	}

	// ── Entry framing: pivot on his sculpted centre, fit distance, approached from where the player stands ──

	void FrameNpc()
	{
		if ( !_orbit.IsValid() || !Sculpture.IsValid() )
			return;

		var pivot = CenterWorld();
		var from = Scene.Camera.IsValid() ? Scene.Camera.WorldPosition : pivot + Vector3.Backward * 100f;
		var toNpc = pivot - from;
		float yaw = toNpc.LengthSquared > 0.01f ? Rotation.LookAt( toNpc ).Angles().yaw : 0f;

		_orbit.Pivot = pivot;
		_orbit.Distance = FramingDistance();
		_orbit.Angles = new Angles( EntryPitch, yaw, 0f );
	}

	Vector3 CenterWorld()
		=> Sdf.TryGetBounds( Sculpture.Brushes, out var bounds )
			? Sculpture.WorldTransform.PointToWorld( bounds.Center )
			: Sculpture.WorldPosition;

	// Fit-sphere distance with FramingMargin breathing room, against the FOV edit mode settles at — the same
	// math as HunterController.FramingDistance.
	float FramingDistance()
	{
		const float fallback = 120f;

		if ( !Sculpture.IsValid() || !Sdf.TryGetBounds( Sculpture.Brushes, out var bounds ) )
			return fallback;

		float radius = bounds.Size.Length * 0.5f * Sculpture.WorldScale.x;
		if ( radius <= 0.01f )
			return fallback;

		float halfFov = GameSettings.OrbitFov.DegreeToRadian() * 0.5f;
		float sin = MathF.Sin( halfFov );
		if ( sin <= 0.001f )
			return fallback;

		return radius * FramingMargin / sin;
	}
}
