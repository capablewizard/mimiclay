using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// In-game sculpt editor for a single <see cref="SdfSculpture"/>. Toggling it active swaps the view to
/// the orbit camera and frees the mouse cursor; toggling it off restores normal play. While active it
/// drives brush authoring — for now a minimal add/remove stub so edit mode is testable; the transform
/// gizmo + UI panel land next.
///
/// Isolated by design: depends only on <see cref="SdfSculpture"/> + <see cref="OrbitCameraController"/>,
/// never on players/rounds. A future creative mode reuses it as-is, just with a different host enabling it.
/// </summary>
/// <summary>Which tool EDIT MODE (the whole session, toggled by the pawn's Edit action) is driving:
/// SCULPT MODE = the stamp ghost (add shapes by clicking), GIZMO MODE = select-and-edit with the
/// transform gizmo. Terminology: "edit mode" always means the session; sculpt/gizmo are its tools.</summary>
public enum SculptTool
{
	Sculpt,
	Gizmo,
}

[Title( "Sculpt Edit Session" )]
[Category( "Mimiclay" )]
[Icon( "construction" )]
public sealed class SculptEditSession : Component
{
	/// <summary>The sculpture being edited.</summary>
	[Property] public SdfSculpture Target { get; set; }

	/// <summary>Camera enabled while editing. Disabled (released back to normal play) otherwise.</summary>
	[Property] public OrbitCameraController OrbitCamera { get; set; }

	/// <summary>Enter edit mode on start, with no external toggler. The in-game sessions are driven by the
	/// hider/hunter controllers (leave this off); the main-menu sculpt toy has no pawn, so it sets this to
	/// self-activate.</summary>
	[Property] public bool StartActive { get; set; }

	/// <summary>Optional persistence slot (a <see cref="SculptLibrary"/> name — <see cref="SculptLibrary.HeadSlot"/>
	/// for the hunter face + menu head). When set, the target's brushes auto-load from the slot when the session
	/// starts, and while editing every commit saves them back (see <see cref="HookPersistSlot"/> for why per-commit
	/// beats save-on-exit). That's what makes an appearance persist across scenes and game sessions: every fresh
	/// pawn re-loads it on spawn. Owner-only on a networked pawn — proxies get the shape through
	/// <see cref="SdfNetworkSync"/>, never from their own disk. Loads apply BRUSHES only: the target keeps its
	/// authored Resolution/FlipFaces, so a slot written by a different host (menu head vs. hunter prefab) can't
	/// override tuned build settings. Null/empty = no persistence (the hider's disguise session).</summary>
	[Property] public string PersistSlot { get; set; }

	/// <summary>While editing, pull the main camera's depth of field in so the focus sits just behind the
	/// edited object (it stays sharp, the background blurs). Restored on exit.</summary>
	[Property, Group( "Depth of Field" )] public bool EditDepthOfField { get; set; } = true;

	/// <summary>How far behind the object's centre the focus plane sits (bigger = object sharper / more
	/// background blur).</summary>
	[Property, Group( "Depth of Field" )] public float DofFocusOffset { get; set; } = 32f;

	/// <summary>Blur strength applied while editing.</summary>
	[Property, Group( "Depth of Field" )] public float DofBlurSize { get; set; } = 7f;

	/// <summary>Depth band that stays sharp around the focus plane while editing.</summary>
	[Property, Group( "Depth of Field" )] public float DofFocusRange { get; set; } = 50f;

	/// <summary>When the focus distance changes faster than this (units/sec, i.e. zooming) the focus snaps
	/// instead of lerping, so it doesn't trail the camera. Orbiting changes it slowly, so that still eases.</summary>
	[Property, Group( "Depth of Field" )] public float DofZoomSnapSpeed { get; set; } = 10f;

	/// <summary>Render the in-world transform gizmo on the selected brush. On by default; turn it off for a
	/// view-only sculpture (the palette/sliders still anchor to the selection's projected position, so a
	/// colours-only HUD works with the gizmo hidden).</summary>
	[Property, Group( "Gizmo" )] public bool ShowGizmo { get; set; } = true;

	/// <summary>How fast the gizmo fades in/out as the selection changes (per second, exponential). Matches the
	/// HUD palette/slider fade so they appear/disappear together.</summary>
	[Property, Group( "Gizmo" )] public float GizmoFadeSpeed { get; set; } = 14f;

	/// <summary>How fast the hover ghost fades in/out (per second, exponential). Snappier than the gizmo — it's
	/// quick feedback that a shape is under the cursor.</summary>
	[Property, Group( "Gizmo" )] public float HoverFadeSpeed { get; set; } = 25f;

	/// <summary>Draw a wireframe outline of every brush while editing (like the editor scene view); fades in
	/// on enter and out on exit.</summary>
	[Property, Group( "Wireframes" )] public bool ShowWireframes { get; set; } = true;

	/// <summary>How fast the wireframes fade in/out (per second, exponential).</summary>
	[Property, Group( "Wireframes" )] public float WireframeFadeSpeed { get; set; } = 10f;

	/// <summary>Push the wireframe toward the camera (clip-space) so it shows through the blended/rounded SDF
	/// shell it sits just inside, while the far back half stays occluded. Bigger = more shows through.</summary>
	[Property, Group( "Wireframes" ), Range( 0f, 0.02f )] public float WireframeDepthBias { get; set; } = 0.002f;

	/// <summary>DEBUG: render the cheap shadow-proxy mesh as the visible surface (hides the raymarcher and the
	/// full surface-nets mesh) so you can inspect the proxy geometry used for drag-time shadows.</summary>
	[Property, Group( "Debug" )] public bool ShowShadowProxy { get; set; }

	/// <summary>The session currently editing (or null). The edit HUD binds to this, so it works the same
	/// whether a prop-hunt hider or a creative-mode host drives the session.</summary>
	public static SculptEditSession Current { get; private set; }

	public bool IsEditing { get; private set; }

	/// <summary>Tool edit mode opens on, every time it's entered (never wherever the last session left
	/// off). Gizmo is the neutral home; sculpt mode is entered explicitly via Add Shape / tiles / keys.</summary>
	[Property] public SculptTool StartTool { get; set; } = SculptTool.Gizmo;

	/// <summary>The active tool: SCULPT mode = stamp ghost, GIZMO mode = select + gizmo. Set by the HUD
	/// (Add Shape button, shape tiles), number keys, Esc (cancel sculpt) and the auto-switch on stamping.</summary>
	public SculptTool Tool { get; private set; } = SculptTool.Gizmo;

	readonly BrushStampTool _stampTool = new();
	bool _opKeyWas; // manual edge detect for the A add/carve toggle
	bool _delWas;   // …and for the Delete key
	bool _undoWas;  // …and for Ctrl+Z
	bool _redoWas;  // …and for Ctrl+Shift+Z / Ctrl+Y

	// Undo history for this session — one step per COMMIT, seeded on activate and dropped on exit.
	// See SculptUndo for why it stores authored-prefix states rather than deltas or whole brush lists.
	readonly SculptUndo _undo = new();

	/// <summary>Snap is a HOLD: Shift held = stamp placement snaps to the origin-centred grid and the
	/// E-rotate scrub snaps to the 45° grid.</summary>
	public static bool SnapHeld =>
		Sandbox.UI.InputFocus.Current is null && Input.Keyboard.Down( "shift" );


	/// <summary>The active stamp shape (what the next/current ghost is). The HUD toolbar highlights it.</summary>
	public SdfShape ActiveShape => _stampTool.Shape;

	/// <summary>The pending stamp ghost brush while the Add tool is live (null otherwise). It IS in
	/// Target.Brushes — commit paths must not bake it (see <see cref="NotifyChanged"/>'s guard). Membership
	/// is re-verified so a wholesale brush-list swap (Load) can't leave a stale ghost blocking commits.</summary>
	public SdfBrush StampBrush
	{
		get
		{
			if ( !IsEditing || Tool != SculptTool.Sculpt )
				return null;

			var s = _stampTool.Stamp;
			return s is not null && Target.IsValid() && Target.Brushes?.Contains( s ) == true ? s : null;
		}
	}

	/// <summary>The brush the HUD's palette/sliders/picker edit: the stamp ghost in Add mode, the selected
	/// brush in Edit mode. Editing the ghost pre-styles the next stamp.</summary>
	public SdfBrush ActiveBrush => Tool == SculptTool.Sculpt && IsEditing ? _stampTool.Stamp : SelectedBrush;

	/// <summary>True while a hold-key param scrub (A/S/D/E) is running — the HUD captures the mouse and
	/// draws the frozen-cursor dot at <see cref="BrushScrub.Anchor"/>.</summary>
	public bool IsScrubbing => BrushScrub.Active != ScrubKind.None;

	/// <summary>The pending stamp's projected screen position (real screen px), for landing the cursor on
	/// the shape when a frozen-cursor gesture releases. False with no stamp / camera / behind-camera.</summary>
	public bool TryGetStampScreenPos( out Vector2 px )
	{
		px = default;
		var b = StampBrush;
		var cam = Scene?.Camera;
		if ( b is null || cam is null || !Target.IsValid() )
			return false;

		// A spline's shape lives in its points, so use the one under the cursor, not the brush transform.
		var local = b.Shape == SdfShape.Spline && b.Points is { Count: > 0 } pts
			? new Vector3( pts[^1].x, pts[^1].y, pts[^1].z )
			: b.Position;

		var world = Target.WorldTransform.PointToWorld( local );
		if ( Vector3.Dot( world - cam.WorldPosition, cam.WorldRotation.Forward ) <= 0f )
			return false;

		px = cam.PointToScreenPixels( world );
		return true;
	}

	/// <summary>True while the cursor is snapped onto a spline chain's first point — the next click closes
	/// the loop and finishes the curve (drives the HUD's LMB hint).</summary>
	public bool ChainWillLoop => IsEditing && Tool == SculptTool.Sculpt && _stampTool.ChainWillLoop;

	/// <summary>True while a dragged spline endpoint is snapped onto the other end in gizmo mode —
	/// releasing merges them into a closed ring.</summary>
	public bool SplineWillLoop => IsEditing && Tool == SculptTool.Gizmo && _gizmo.SplineWillLoop;

	/// <summary>The material the user last applied to anything — palette click, colour wheel, metal/rough
	/// scrub, paste, or the last committed stamp. New shapes are born wearing it, so picking gold and then
	/// adding a shape gives a gold shape. Static: one "current colour" for the session, like a paint
	/// program's active swatch. Null until the first pick, where shapes keep their own defaults.</summary>
	public static (Color Color, float Metallic, float Roughness)? LastMaterial { get; private set; }

	/// <summary>Record a brush's material as the current one (call after any user-driven material edit).</summary>
	public static void RememberMaterial( SdfBrush b )
	{
		if ( b is not null )
			LastMaterial = (b.Color, b.Metallic, b.Roughness);
	}

	/// <summary>The stamp tool's Add/Carve toggle state (the HUD chips bind to this).</summary>
	public SdfOperation StampOperation => _stampTool.Operation;

	/// <summary>Set the stamp operation (the HUD's Add/Carve toggle). The live ghost syncs — and previews —
	/// on the stamp tool's next update.</summary>
	public void SetStampOperation( SdfOperation op ) => _stampTool.Operation = op;

	/// <summary>The pending stamp ghost riding <paramref name="sculpt"/> (or null). Bounds consumers that
	/// drive the CAMERA or the PAWN — pivot pin, origin recentre, feet, DoF focus — must exclude it, or the
	/// view/body chases the cursor while placing. Render/mesh bounds keep including it (the preview surface
	/// needs the coverage).</summary>
	public static SdfBrush PendingStamp( SdfSculpture sculpt ) =>
		Current is { } s && s.Target == sculpt ? s.StampBrush : null;

	/// <summary>Switch tools. Entering Add clears the selection (no gizmo there); leaving Add strips the
	/// pending ghost and commits the real shape.</summary>
	public void SetTool( SculptTool tool )
	{
		if ( tool == Tool )
			return;

		Tool = tool;

		if ( tool == SculptTool.Sculpt )
		{
			Deselect();
			_gizmo.Hide();
			_gizmoAlpha = 0f;
			_gizmoBrush = null;
		}
		else
		{
			CancelStamp();
		}
	}

	/// <summary>Set the active stamp shape (HUD toolbar / number keys). The live ghost remoulds in place.</summary>
	public void SetShape( SdfShape shape ) => _stampTool.SetShape( shape );

	// Strip the pending ghost from the brush list. If one was actually removed the surface still shows it,
	// so run the full commit (NotifyChanged's ghost guard passes — StampBrush is null once cancelled).
	void CancelStamp()
	{
		_stampWire.Hide();
		if ( _stampTool.Cancel( Target ) )
			NotifyChanged();
	}

	/// <summary>True while a gizmo handle is being dragged (so the HUD can fade out of the way).</summary>
	public bool IsManipulating => _gizmo.IsDragging;

	/// <summary>True while the cursor is over the selected spline's line — the HUD swaps to the add-point
	/// cursor, and a click (handled in the gizmo) inserts a control point there.</summary>
	public bool HoveringSplineLine => IsEditing && _gizmo.HoveringSplineLine;

	/// <summary>True while the drag born from an add-point click is running. Keeps the HUD's cursor panel
	/// mounted so its mouseup can end the drag — the UI swallows Attack1 for the whole gesture, so the
	/// gizmo never sees the release itself.</summary>
	public bool SplinePointUiDragging => IsEditing && _gizmo.IsUiDragging;

	/// <summary>End the insert-and-place drag (the add-point panel's mouseup).</summary>
	public void EndSplinePointUiDrag() => _gizmo.EndUiDrag();

	/// <summary>The selected brush's transform gizmo as a screen-space circle (REAL screen px): centred on the
	/// brush origin with the farthest handle's reach as the radius. The gizmo draws at constant screen size
	/// (reference px × viewport scale), so the radius is pure 2D maths. The HUD unions this with the shape's
	/// bounding circle so the palette/sliders can't sit on the handles. False with no selection, the gizmo
	/// hidden, or a spline selected (its per-point dots hug the curve, which the bounds already cover).</summary>
	public bool TryGetGizmoScreenCircle( out Vector2 centrePx, out float radiusPx )
	{
		centrePx = default;
		radiusPx = 0f;

		var brush = SelectedBrush;
		var cam = Scene?.Camera;
		if ( !IsEditing || !ShowGizmo || brush is null || cam is null || !Target.IsValid() )
			return false;
		if ( brush.Shape == SdfShape.Spline )
			return false;

		var worldCentre = Target.WorldTransform.PointToWorld( brush.Position );
		if ( Vector3.Dot( worldCentre - cam.WorldPosition, cam.WorldRotation.Forward ) <= 0f )
			return false; // behind the camera

		var st = Style;
		float reach = MathF.Max( st.RotationRadius,
			st.TranslationMax + st.TranslationOffset + st.ScaleDotOffset + st.DotRadius ) * st.GizmoScale;
		float screenScale = st.ReferenceHeight > 1f ? Screen.Height / st.ReferenceHeight : 1f;

		centrePx = cam.PointToScreenPixels( worldCentre );
		radiusPx = reach * screenScale;
		return true;
	}

	/// <summary>Index of the brush the gizmo acts on, or -1 for no selection (the default). With nothing
	/// selected the gizmo, palette and sliders all hide; click a shape to select it.</summary>
	public int Selected { get; private set; } = -1;

	/// <summary>True when a brush is selected (the gizmo/HUD are shown).</summary>
	public bool HasSelection => Selected >= 0;

	/// <summary>The brush the gizmo/properties panel act on, or null when nothing is selected.</summary>
	public SdfBrush SelectedBrush
	{
		get
		{
			var b = Target.IsValid() ? Target.Brushes : null;
			return b is { Count: > 0 } && Selected >= 0 && Selected < b.Count ? b[Selected] : null;
		}
	}

	readonly RuntimeBrushGizmo _gizmo = new();
	float _gizmoAlpha;        // current gizmo opacity, eased toward 1 (selected) / 0 (not) for the fade
	SdfBrush _gizmoBrush;     // last selected brush, kept so the gizmo can keep drawing while it fades out
	bool _splineInsertArmed;  // spline add-point is armed only after the cursor sits on the line with Attack1 released,
	                          // so the click that SELECTS a spline can't also insert (one click select, one to add)
	// Two hover ghosts so hovering straight from one shape to another cross-fades: the incoming brush fades in on
	// _ghost while the one you left fades out on _ghostOut.
	readonly BrushGhost _ghost = new();        // incoming (currently hovered) brush, fading toward 1
	readonly BrushGhost _ghostOut = new();     // outgoing (just-left) brush, fading toward 0
	SdfBrush _ghostBrush;
	SdfBrush _ghostOutBrush;
	float _ghostAlpha;
	float _ghostOutAlpha;
	readonly BrushWireframes _wireframes = new();
	// Carve-stamp outline: a subtract ghost hovering in empty space is invisible on the live surface, so it
	// gets its own single-brush wireframe (red, editor-style). Additive stamps draw nothing extra.
	readonly BrushWireframes _stampWire = new();
	readonly List<SdfBrush> _stampWireList = new();
	float _wireAlpha;
	bool _wireframesOn = false; // off by default; toggled by Tab, but only while edit mode is active
	int _hoverBrush = -1;      // brush under the cursor while editing (for the wireframe hover highlight)

	/// <summary>Brush index the HUD's layer list is hovering, or -1. Drives the same hover highlight as a
	/// 3D scene pick, so hovering a row lights up its shape. The scene pick wins when the cursor is actually
	/// over a brush in the viewport; this is the fallback.</summary>
	public static int UiHoverBrush { get; set; } = -1;
	bool _wasManipulating;     // tracks drag end → triggers the full remesh on release
	bool _proxyDebugActive;    // ShowShadowProxy is currently overriding the renderers

	SdfRaymarchRenderer Raymarcher =>
		Target.IsValid() ? Target.GameObject.Components.Get<SdfRaymarchRenderer>() : null;

	// Scene-wide gizmo styling (same component the editor tool reads). Re-resolved until a real scene
	// component is found (so it isn't permanently stuck on the fallback if the GizmoController loads late),
	// then cached on it so live inspector edits flow straight through.
	GizmoSettings _style;
	static GizmoSettings _fallbackStyle;
	GizmoSettings Style => _style.IsValid()
		? _style
		: ((_style = Scene.GetAllComponents<GizmoSettings>().FirstOrDefault()) ?? (_fallbackStyle ??= new GizmoSettings()));

	// Self-activate for hosts with no toggler (the menu sculpt toy). Runs after OnEnabled, so the scene, the
	// target's renderers and the main camera are all ready for SetActive's HUD spawn + DoF setup.
	protected override void OnStart()
	{
		Tool = StartTool;
		LoadPersistSlot(); // before self-activate, so an always-on session opens already showing the restored shape

		if ( StartActive )
			SetActive( true );
	}

	public void Toggle() => SetActive( !IsEditing );

	public void SetActive( bool active )
	{
		if ( active == IsEditing )
			return;

		IsEditing = active;

		// Distance-field cache: stays on at full resolution for the whole session (the per-change dispatch for
		// one locally-edited prop is the proven-cheap path). Reset the live-drag levers here so a fresh
		// activate, and any deactivate, always starts crisp and unthrottled — SdfNetworkSync lowers them on
		// PROXIES during remote drags, and an edit session taking over this prop must not inherit them.
		var rmField = Raymarcher;
		if ( rmField.IsValid() )
		{
			rmField.FieldResolutionScale = 1f;
			rmField.FieldRebakeInterval = 0f;
		}

		if ( active )
		{
			Current = this;
			SetTool( StartTool ); // edit mode always OPENS on its starting tool (gizmo, normally) — never
			                      // wherever a previous session happened to leave off (e.g. mid-placement)
			EnsureHud(); // the edit system brings its own HUD — no scene setup needed (and works in any game mode)
			ApplyEditDof();
			HookPersistSlot(); // save the slot on every commit while editing (see HookPersistSlot for why)
			_undo.Reset( Target, Selected ); // baseline = the shape the session opens on; undo stops there
		}
		else if ( Current == this )
		{
			Current = null;
		}

		// Tear down the rendered gizmo + ghost when leaving edit mode (works whether or not we own a camera).
		if ( !active )
		{
			CancelStamp(); // strip the pending stamp ghost FIRST, so no exit commit can bake it
			if ( _pendingCommit )
				CommitChanged(); // never leave a previewed-but-uncommitted edit behind on exit (saves the slot too)
			UnhookPersistSlot();
			_undo.Clear(); // history is per-session — the next one re-baselines on whatever it opens on
			_gizmo.Hide();
			HideGhosts();
			RestoreDof();
			if ( _proxyDebugActive )
				ExitProxyDebug();
		}

		// Optional: when this session owns a camera (creative mode), hand it over while editing. The hider
		// leaves OrbitCamera null and keeps its own always-on orbit camera instead.
		if ( OrbitCamera.IsValid() )
		{
			if ( active )
				OrbitCamera.FocusHint = FocusPoint();
			OrbitCamera.Enabled = active;
		}
	}

	// Covers both disabling the component and destroying the pawn (the gizmo's SceneObject lives in the
	// SceneWorld, so it must be released explicitly).
	protected override void OnDisabled()
	{
		CancelStamp(); // strip the pending stamp ghost FIRST, so no teardown commit can bake it
		if ( _pendingCommit )
			CommitChanged(); // NotifyChanged guards Target.IsValid, so this is safe mid-teardown (saves the slot too)
		UnhookPersistSlot();
		_undo.Clear();
		_gizmo.Hide();
		HideGhosts();
		_wireframes.Hide();
		_wireAlpha = 0f;
		RestoreDof();
		if ( _proxyDebugActive )
			ExitProxyDebug();

		// Don't leave the prop on a lowered/throttled field bake if the session is torn down mid-edit.
		var rmField = Raymarcher;
		if ( rmField.IsValid() )
		{
			rmField.FieldResolutionScale = 1f;
			rmField.FieldRebakeInterval = 0f;
		}

		if ( Current == this )
			Current = null;
	}

	// ── HUD-facing API (drives the same logic regardless of game mode) ───────────────────────────────

	/// <summary>Add an additive (or subtractive) brush of the given shape and select it. At the brush cap the
	/// add is refused (see <see cref="SdfSculpture.AddBrush"/>) and the selection stays put.</summary>
	public void Add( SdfShape shape, SdfOperation operation = SdfOperation.Add )
	{
		if ( !Target.IsValid() )
			return;

		if ( Target.AddBrush( shape, operation ) )
			Selected = Target.AuthoredBrushCount - 1; // the insert lands just below the damage tail, not at the raw end
	}

	/// <summary>Convert the selected brush to a different primitive IN PLACE — position, size, material,
	/// blend and symmetry all carry over; only the shape swaps (shape is a brush property, like colour).
	/// Splines are excluded both ways (their geometry lives in control points, not a transform). Converting
	/// TO text bakes the glyph field and adopts the glyph slot's fixed 2:1 aspect from the current height
	/// (anything else stretches the letters).</summary>
	public void ConvertSelectedShape( SdfShape shape )
	{
		if ( SelectedBrush is not { } b || b.Shape == shape )
			return;

		// Keep the shape's world-space VOLUME CENTRE fixed across the swap. LocalCentre is each shape's
		// centre offset from its pivot — zero for the centred shapes, +Size.z for the base-pivoted cone —
		// so removing the old offset and adding the new one makes cylinder↔cone occupy the SAME space
		// (the cone's base lands on the old bottom) instead of jumping by a half-height.
		var oldCentre = b.Rotation * b.LocalCentre;
		bool fromText = b.Shape == SdfShape.Text;

		// TO a spline: rebuild the solid as a 2-point tube filling the same box (see SplineFromSolid).
		if ( shape == SdfShape.Spline )
		{
			// Remember the TRUE original solid (before any normalisation below) for the trip back.
			_preSplineSolid[b] = (b.Shape, b.Size, b.Rotation);

			// Text first has to stop being text. Its Size is an aspect-locked plaque and its extents are
			// the ink rect — sweeping THOSE gives a rod whose axis, length and thickness all depend on the
			// string, which is why text→spline disagreed with text→cylinder→spline. Every other text
			// conversion normalises the plaque away first; do exactly the same here (same uniform
			// letter-height size, same remembered pre-text size if it had one) and then sweep the
			// normalised solid. The result is identical to going via a cylinder by hand.
			if ( b.Shape == SdfShape.Text )
			{
				b.Shape = SdfShape.Cylinder;
				b.Size = _preTextSize.TryGetValue( b, out var preText )
					? preText
					: new Vector3( MathF.Max( b.Size.y, 1f ) );
			}

			SplineFromSolid( b );
			NotifyChanged();
			return;
		}

		// FROM a spline: a curve has no transform to inherit. A brush that WAS a solid gets its exact Size
		// and Rotation back (the tube is deliberately thinner than the solid, so re-deriving them from its
		// bounds would shrink the shape a little more on every round trip); a chain-placed spline, with
		// nothing to restore, takes the curve's bounding box at the shape's spawn orientation. Either way
		// the position comes from the curve's CURRENT centre, so moving the spline moves the solid — and
		// for an untouched round trip that centre is exactly where the solid's was.
		if ( b.Shape == SdfShape.Spline )
		{
			b.LocalBounds( out var mn, out var mx, includeBlend: false );
			if ( _preSplineSolid.TryGetValue( b, out var mem ) )
			{
				b.Size = mem.Size;
				b.Rotation = mem.Rotation;
				// The remembered solid was TEXT: its Size is an aspect-locked plaque, nonsense for any
				// other solid — flag it so the leaving-text normalisation below runs, exactly as it would
				// converting straight out of text.
				fromText = mem.Shape == SdfShape.Text;
			}
			else
			{
				b.Rotation = SdfSculpture.SpawnRotation( shape );
				b.Size = Vector3.Max( (mx - mn) * 0.5f, new Vector3( 1f ) );
			}
			b.Position = (mn + mx) * 0.5f;
			b.SplineClosed = false;
		}

		b.Shape = shape;
		b.Points = null;

		if ( shape == SdfShape.Text )
		{
			// Remember the SOLID's scale so converting back restores it exactly; the text itself takes its
			// own aspect-locked plaque size (glyph slot is 2:1 — anything else distorts the letters), with
			// the letter height taken from the source's Y. Orientation is deliberately untouched.
			// (fromText here means we came back via a spline that was text — the size we're holding is
			// already a plaque, so recording it would overwrite the real solid size with a text one.)
			if ( !fromText )
				_preTextSize[b] = b.Size;
			b.TextData = SdfTextSdf.Get( b.Text, b.Font );
			float yv = MathF.Max( b.Size.y, 1f );
			b.Size = new Vector3( yv * (SdfTextData.Width / (float)SdfTextData.Height), yv, MathF.Min( b.Size.z, 4f ) );
		}
		else if ( fromText )
		{
			// Leaving text: restore the remembered pre-text scale (the round trip keeps the solid exactly
			// as it was); a brush BORN as text has nothing to restore, so it gets a clean uniform size at
			// the letters' height. Orientation is preserved in both directions.
			b.Size = _preTextSize.TryGetValue( b, out var remembered )
				? remembered
				: new Vector3( MathF.Max( b.Size.y, 1f ) );
		}

		b.Position += oldCentre - b.Rotation * b.LocalCentre;

		// Different shapes cap rounding differently (a star's erosion limit << a box's inscribed radius).
		b.Rounding = Math.Clamp( b.Rounding, SdfBrush.MinRounding, b.MaxRounding() );
		NotifyChanged();
	}

	// Rebuild a solid brush as a 2-point spline occupying the same space: a control point at each EXTREME
	// of the sweep axis (a cone's base centre and its apex) and a radius HALF what would fill the
	// cross-section — a deliberately thinner noodle, since a full-fat tube reads as "nothing happened" on
	// a cylinder. Points are SCULPTURE-space, so the brush's own transform is folded in here; afterwards
	// the transform is inert for the spline, which is why the caller remembers the solid's Size + Rotation
	// for the trip back. TEXT never reaches here as text — the caller normalises it to a plain solid
	// first, since plaque proportions make the sweep string-dependent.
	void SplineFromSolid( SdfBrush b )
	{
		var ext = b.AabbExtents( Rotation.Identity, includeBlend: false ); // local half-extents, per shape
		var centre = b.LocalCentre; // base-pivot cone sits above the origin

		// Which axis carries the sweep. The shapes SWEPT along their own local Z (cylinder, cone, extruded)
		// always run the tube down that axis — a squat cylinder's natural axis is still Z even when it
		// isn't the longest extent, and a point at each end cap is what reads as "the same shape". Text
		// runs along the string. Only box/sphere, which have no intrinsic axis, use the longest extent.
		// Text is local X too — ALWAYS, never by proportion vote: a short string's ink is narrow
		// (ContentWidthFrac floors at 0.05) but stays full letter height, so "longest extent" flips the rod
		// upright THROUGH the letters. The reading direction is the only sensible sweep.
		int axis = b.Shape switch
		{
			SdfShape.Cylinder or SdfShape.Cone or SdfShape.Extruded => 2,
			SdfShape.Text => 0,
			_ => ext.x >= ext.y && ext.x >= ext.z ? 0 : (ext.y >= ext.z ? 1 : 2),
		};

		float half = MathF.Max( axis == 0 ? ext.x : axis == 1 ? ext.y : ext.z, 0.5f );

		// Thickness comes from the two cross-section extents — except for text, whose Z is a shallow
		// plaque depth that would thin the rod to spaghetti; use its other IN-PLANE extent instead.
		float crossA = axis == 0 ? ext.y : ext.x;
		float crossB = b.Shape == SdfShape.Text ? crossA : (axis == 2 ? ext.y : ext.z);

		float radius = Math.Clamp( MathF.Min( crossA, crossB ) * 0.5f, 1f, SdfBrush.MaxSplineRadius );
		var dir = axis == 0 ? new Vector3( 1f, 0f, 0f ) : axis == 1 ? new Vector3( 0f, 1f, 0f ) : new Vector3( 0f, 0f, 1f );

		Vector4 ToSculpture( Vector3 local )
		{
			var s = b.Position + b.Rotation * local;
			return new Vector4( s.x, s.y, s.z, radius );
		}

		b.Shape = SdfShape.Spline;
		b.SplineClosed = false;
		b.SplinePerPointRadius = true;
		b.Points = new List<Vector4> { ToSculpture( centre - dir * half ), ToSculpture( centre + dir * half ) };
	}

	/// <summary>Select a brush by index, or pass a negative index to clear the selection.</summary>
	public void Select( int index )
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		Selected = ( b is { Count: > 0 } && index >= 0 ) ? Math.Clamp( index, 0, b.Count - 1 ) : -1;
	}

	/// <summary>Clear the selection (gizmo/palette/sliders hide).</summary>
	public void Deselect() => Selected = -1;

	/// <summary>Remove the selected brush (keeps at least one AUTHORED brush — damage craters don't count
	/// toward that minimum) and rebuild. Leaves nothing selected.</summary>
	public void RemoveSelected()
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( Selected < 0 || b is not { Count: > 1 } || Target.AuthoredBrushCount <= 1 )
			return;

		b.RemoveAt( Math.Clamp( Selected, 0, b.Count - 1 ) );
		Selected = -1;
		NotifyChanged();
	}

	// ── Per-row layer actions (the icon buttons on each LayerRow) ────────────────────────────────────

	/// <summary>The brush at a given index, or null if out of range.</summary>
	SdfBrush BrushAt( int index )
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		return ( b is not null && index >= 0 && index < b.Count ) ? b[index] : null;
	}

	/// <summary>Toggle a brush's visibility (the eye button) and rebuild.</summary>
	public void ToggleEnabled( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		b.Enabled = !b.Enabled;
		NotifyChanged();
	}

	/// <summary>Flip a brush between additive and subtractive (the +/- button) and rebuild.</summary>
	public void ToggleOperation( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		b.Operation = b.Operation == SdfOperation.Add ? SdfOperation.Subtract : SdfOperation.Add;
		NotifyChanged();
	}

	// Each brush's axis combo from the last time the layer-row toggle turned its symmetry OFF, so turning
	// it back on restores that combo instead of defaulting to X. Keyed by brush reference (survives
	// reorders); entries for deleted brushes are just harmless orphans.
	readonly Dictionary<SdfBrush, (bool X, bool Y, bool Z)> _mirrorMemory = new();

	// Each brush's Size from the last time it was converted TO text, so converting back to a solid restores
	// its exact scale (the text plaque's aspect-locked Size would otherwise leak into the solid). Same
	// reference-keyed orphan-tolerant pattern as _mirrorMemory.
	readonly Dictionary<SdfBrush, Vector3> _preTextSize = new();

	// Each brush's Size + Rotation from the last time it was converted TO a spline, so converting back
	// restores the solid exactly. Deriving them from the tube instead would compound: the spline is
	// deliberately half the cross-section, so every round trip would halve the shape again.
	readonly Dictionary<SdfBrush, (SdfShape Shape, Vector3 Size, Rotation Rotation)> _preSplineSolid = new();

	/// <summary>Toggle symmetry on a brush (the symmetry button): clears all axes if any are on, else
	/// restores the combo it had when last toggled off (left/right X for a brush with no history).
	/// Per-axis control still lives in the Symmetry section.</summary>
	public void ToggleSymmetry( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		if ( b.MirrorX || b.MirrorY || b.MirrorZ )
		{
			_mirrorMemory[b] = (b.MirrorX, b.MirrorY, b.MirrorZ);
			b.MirrorX = b.MirrorY = b.MirrorZ = false;
		}
		else
		{
			var m = _mirrorMemory.TryGetValue( b, out var saved ) ? saved : (X: true, Y: false, Z: false);
			b.MirrorX = m.X;
			b.MirrorY = m.Y;
			b.MirrorZ = m.Z;
		}

		b.SnapToMirrorPlanes(); // symmetry turned on near the plane = "centre this" — magnetize now
		NotifyChanged();
	}

	/// <summary>Delete a specific brush (the bin button), keeping at least one, and rebuild.</summary>
	public void Remove( int index )
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( b is not { Count: > 1 } || index < 0 || index >= b.Count )
			return;

		b.RemoveAt( index );

		// Keep the same brush selected (its index may have shifted down); deselect if we removed the selected one.
		if ( Selected == index ) Selected = -1;
		else if ( Selected > index ) Selected--;

		NotifyChanged();
	}

	/// <summary>Duplicate the selected brush: insert an independent copy right above it in the stack — exactly
	/// in place — then select the copy and rebuild. The clone carries every property (shape, transform, size,
	/// material and symmetry), so it sits on top of the original until you drag it off.</summary>
	public void DuplicateSelected()
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( Selected < 0 || b is not { Count: > 0 } || Selected >= b.Count )
			return;

		int i = Selected;
		b.Insert( i + 1, b[i].Copy() );
		Selected = i + 1;
		NotifyChanged();
	}

	/// <summary>Reset the selected brush's rotation to its shape's spawn orientation (the "Rotate" tool
	/// button) and rebuild — identity for most shapes, face-forward for the flat-profile ones.</summary>
	public void ResetRotationSelected()
	{
		if ( BrushAt( Selected ) is not { } b )
			return;

		b.Rotation = SdfSculpture.SpawnRotation( b.Shape );
		NotifyChanged();
	}

	/// <summary>Reorder the layer list: move the brush at <paramref name="from"/> so it lands at
	/// <paramref name="to"/> and rebuild. Order is significant — later brushes draw on top of the field — so
	/// this drag-reorder actually changes the resulting shape. The selection is preserved on whatever brush
	/// it was on (its index follows the shuffle); reordering does NOT select the moved brush.</summary>
	public void MoveBrush( int from, int to )
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( b is not { Count: > 1 } || from < 0 || from >= b.Count )
			return;

		to = Math.Clamp( to, 0, b.Count - 1 );
		if ( from == to )
			return;

		// Remember the selected brush so we can keep IT selected after the indices shuffle.
		var selectedBrush = (Selected >= 0 && Selected < b.Count) ? b[Selected] : null;

		var item = b[from];
		b.RemoveAt( from );
		b.Insert( to, item );

		// Re-point the selection at the same brush it was on (its index may have shifted).
		int ni = selectedBrush is not null ? b.IndexOf( selectedBrush ) : -1;
		if ( ni >= 0 )
			Selected = ni;

		NotifyChanged();
	}

	/// <summary>Insert a control point where the cursor is hovering the selected spline's line. Called by the
	/// HUD's add-point cursor panel (its pointer-events:all surface swallows the world click, so the gizmo
	/// can't do it on its own).</summary>
	public void InsertHoveredSplinePoint()
	{
		if ( !_splineInsertArmed ) // not yet released-then-pressed since selecting → this click only selected
			return;

		if ( _gizmo.TryInsertSplinePoint( SelectedBrush ) )
			NotifyChanged();
	}

	/// <summary>Rebuild after the HUD edits a brush property — the full COMMIT: 3-LOD surface-nets remesh,
	/// physics collider rebuild, reliable network snapshot. For continuous gestures (slider drags, colour
	/// scrubs, typing) use <see cref="PreviewChanged"/> + <see cref="CommitChanged"/> instead; this is for
	/// discrete one-shot edits (palette click, cross-section swap, add/remove/reorder).</summary>
	public void NotifyChanged()
	{
		// A pending stamp ghost is sitting in the brush list — a full commit here would remesh, persist and
		// network it as if it were real. Downgrade to a live preview; the real commit happens with the stamp
		// itself (or right after the ghost is cancelled, when this guard passes).
		if ( StampBrush is not null )
		{
			PreviewChanged();
			return;
		}

		_pendingCommit = false; // a full commit covers anything previewed before it
		if ( !Target.IsValid() )
			return;

		// THE undo record site. Every authored edit — gizmo release, stamp commit, scrub release, debounced
		// HUD gesture, add/remove/duplicate/reorder/convert/toggle — funnels through here, and nothing else
		// does: damage carves, shrink healing and remote shapes all call Target.Rebuild() directly, so they
		// can't become undo steps. Recording in the SESSION rather than on SdfSculpture.Committed is what
		// buys those exclusions for free. Gated on IsEditing so the commit run during teardown (where the
		// stack is about to be dropped anyway) doesn't push a final step.
		if ( IsEditing )
			_undo.Record( Target, Selected );

		Target.Rebuild();
	}

	// A preview happened and no commit has followed yet. The debounce in OnUpdate backstops gestures with no
	// clean end event (typing in the text field) and any missed control release.
	bool _pendingCommit;
	RealTimeSince _sincePreview;
	const float CommitDebounce = 0.5f;

	/// <summary>A HUD control is mid-gesture (slider drag, colour-wheel scrub, text typing): update only the
	/// LIVE surface — the raymarcher reads the brushes directly each frame, and the shadow proxy keeps shadows
	/// plus the 20 Hz network preview stream moving — and defer the expensive commit until the gesture ends.
	/// This is exactly the preview/commit split gizmo handle drags already have; without it, every mouse-move
	/// on a slider was a full commit (remesh + collider + reliable network send) on every machine.</summary>
	public void PreviewChanged()
	{
		if ( !Target.IsValid() )
			return;

		Target.RebuildShadowProxy();
		_pendingCommit = true;
		_sincePreview = 0;
	}

	/// <summary>The HUD gesture ended → run the one full commit for everything previewed since. Safe to call
	/// redundantly: the sculpture's content-hash cache makes a no-change commit cheap.</summary>
	public void CommitChanged() => NotifyChanged();

	// ── Undo / redo ──────────────────────────────────────────────────────────────────────────────────
	// The whole system hangs off the commit funnel above: every authored edit ends in NotifyChanged, which
	// records one step. See SculptUndo for the model; the interesting work down here is APPLYING a step,
	// which has to splice around the damage tail and drop every cached brush reference.

	/// <summary>A step exists to go back to (drives the HUD button's enabled state).</summary>
	public bool CanUndo => IsEditing && _undo.CanUndo;

	/// <summary>An undone step exists to re-apply.</summary>
	public bool CanRedo => IsEditing && _undo.CanRedo;

	/// <summary>Step back one commit. A held stamp ghost is dropped first — it isn't a real shape yet, so
	/// "one step back" means the last committed shape, not the placement in progress. Any in-flight HUD
	/// gesture is committed first too, so its change becomes a step of its own rather than being skipped
	/// straight over (nudge a slider, hit Ctrl+Z, and the nudge is what comes off).</summary>
	public bool Undo() => Step( redo: false );

	/// <summary>Re-apply the step a matching <see cref="Undo"/> took off.</summary>
	public bool Redo() => Step( redo: true );

	bool Step( bool redo )
	{
		if ( !IsEditing || !Target.IsValid() )
			return false;

		if ( Tool == SculptTool.Sculpt )
			SetTool( SculptTool.Gizmo ); // strips the ghost; its commit is a no-op the hash dedup absorbs

		if ( _pendingCommit )
			CommitChanged(); // fold an unfinished gesture into its own step before stepping off it

		SculptUndo.State state;
		if ( !(redo ? _undo.Redo( out state ) : _undo.Undo( out state )) )
			return false;

		ApplyUndoState( state );
		return true;
	}

	/// <summary>Apply a recorded state: splice its authored brushes onto the LIVE damage tail, restore the
	/// build settings and selection, then run the one full commit. This deliberately goes through
	/// <see cref="SdfSculpture.Rebuild"/> like any other edit, so the remesh, collider, network push and
	/// persist-slot save all follow exactly as they would for a normal change — an undo IS an edit as far as
	/// the rest of the system is concerned.</summary>
	void ApplyUndoState( SculptUndo.State state )
	{
		if ( state is null || !Target.IsValid() )
			return;

		var live = Target.Brushes;
		int liveAuthored = Target.AuthoredBrushCount;

		// The stack's copies stay pristine so the same entry survives being applied again (undo → redo →
		// undo); the sculpture gets its own instances to mutate.
		var restored = new List<SdfBrush>( state.Authored.Count );
		foreach ( var b in state.Authored )
			restored.Add( b.Copy() );

		// Carry the damage tail across untouched. Craters that landed (or healed) since the snapshot are
		// gameplay state, not authoring — undoing a shape edit must neither resurrect nor erase them.
		var rebuilt = new List<SdfBrush>( restored );
		if ( live is not null )
		{
			for ( int i = liveAuthored; i < live.Count; i++ )
				rebuilt.Add( live[i] );
		}

		RemapBrushMemory( live, liveAuthored, restored ); // before the swap — it reads the OLD brushes

		_undo.IsApplying = true;
		try
		{
			Target.Brushes = rebuilt;
			Target.Resolution = state.Resolution;
			Target.FlipFaces = state.FlipFaces;

			// Every cached brush REFERENCE now dangles (the gizmo's fade-out brush, both hover ghosts, the
			// carve wireframe): they'd keep drawing — and stay draggable — as orphans outside the list. Drop
			// them all and let the next frame re-pick from the restored brushes.
			_gizmo.Hide();
			_gizmoBrush = null;
			_gizmoAlpha = 0f;
			_splineInsertArmed = false;
			HideGhosts();
			_stampWire.Hide();
			_stampWireList.Clear();
			_hoverBrush = -1;

			int authored = Target.AuthoredBrushCount;
			Selected = (state.Selected >= 0 && state.Selected < authored) ? state.Selected : -1;

			_pendingCommit = false;
			Target.Rebuild();
		}
		finally
		{
			_undo.IsApplying = false;
		}
	}

	// Move the reference-keyed per-brush memories onto the restored brushes, BY INDEX. _mirrorMemory,
	// _preTextSize and _preSplineSolid all key on the brush INSTANCE, and every authored brush was just
	// replaced by a fresh copy — so without this they all orphan and, say, a text↔solid round trip spanning an
	// undo forgets its pre-text size. Index is exact for the common case (undoing a property tweak leaves the
	// list the same length and order) and no worse than the wholesale orphaning it replaces when it isn't.
	void RemapBrushMemory( List<SdfBrush> oldBrushes, int oldAuthored, List<SdfBrush> restored )
	{
		if ( oldBrushes is null )
		{
			_mirrorMemory.Clear();
			_preTextSize.Clear();
			_preSplineSolid.Clear();
			return;
		}

		int n = Math.Min( Math.Min( oldAuthored, oldBrushes.Count ), restored.Count );

		static void Remap<T>( Dictionary<SdfBrush, T> map, List<SdfBrush> from, List<SdfBrush> to, int n )
		{
			if ( map.Count == 0 )
				return;

			// Collect first, then refill: rekeying in place would risk colliding with keys not yet visited.
			var carried = new List<(SdfBrush Key, T Value)>( map.Count );
			for ( int i = 0; i < n; i++ )
			{
				if ( map.TryGetValue( from[i], out var v ) )
					carried.Add( (to[i], v) );
			}

			map.Clear();
			foreach ( var (k, v) in carried )
				map[k] = v;
		}

		Remap( _mirrorMemory, oldBrushes, restored, n );
		Remap( _preTextSize, oldBrushes, restored, n );
		Remap( _preSplineSolid, oldBrushes, restored, n );
	}

	// ── Save / load (local on-disk library) ──────────────────────────────────────────────────────────
	// Persist the current sculpture so it survives across sessions, without the editor prefab pipeline. See
	// SculptLibrary for where the files live. These wrap it with the session's Target so the HUD (or a console
	// command) just calls SaveAs/Load by name.

	/// <summary>Save the current sculpture to the local library under <paramref name="name"/>. Returns false if
	/// there's nothing valid to save (see <see cref="SculptLibrary"/>).</summary>
	public bool SaveAs( string name ) => Target.IsValid() && SculptLibrary.Save( name, Target );

	/// <summary>Replace the current sculpture with a saved one and rebuild — clears the selection and commits the
	/// new shape (the <see cref="SdfSculpture.Committed"/> from <see cref="SdfSculpture.Rebuild"/> is what pushes
	/// it to the other clients on a networked hider). Returns false if the named save is missing or corrupt, in
	/// which case the current shape is left untouched.</summary>
	public bool Load( string name )
	{
		if ( !Target.IsValid() )
			return false;

		var entry = SculptLibrary.Load( name );
		if ( entry is null )
			return false;

		Target.Brushes = entry.Brushes;
		Target.Resolution = entry.Resolution;
		Target.FlipFaces = entry.FlipFaces;
		Selected = -1; // the loaded brushes are a different set — never keep a stale index into the old list

		// Loading a library shape replaces every brush reference, so the per-brush memories are all orphans.
		_mirrorMemory.Clear();
		_preTextSize.Clear();
		_preSplineSolid.Clear();

		// This path sets the brushes directly rather than going through the commit funnel, so record here
		// explicitly — loading over your work is exactly the kind of thing you want to be able to take back.
		if ( IsEditing )
			_undo.Record( Target, Selected );

		Target.Rebuild();
		return true;
	}

	// Persistence slot (see PersistSlot): restore the saved appearance onto the target at session start. Brushes
	// only — unlike Load(), the target's authored Resolution/FlipFaces stand, so the hunter prefab and the menu
	// head each keep their own tuned build settings whichever of them wrote the slot. No save yet (or a corrupt
	// one — SculptLibrary returns null) → silently keep the authored default shape.
	void LoadPersistSlot()
	{
		if ( string.IsNullOrWhiteSpace( PersistSlot ) || IsProxy || !Target.IsValid() )
			return;

		var entry = SculptLibrary.Load( PersistSlot );
		if ( entry is null )
			return;

		Target.Brushes = entry.Brushes;
		Selected = -1;
		Target.Rebuild(); // the Committed this fires is what publishes the restored shape on a networked pawn
	}

	// Write the current shape back to the slot. SculptLibrary refuses an empty brush list, so a shape deleted
	// down to nothing can't clobber the save — the last real head survives and reloads next spawn.
	void SavePersistSlot()
	{
		if ( string.IsNullOrWhiteSpace( PersistSlot ) || IsProxy || !Target.IsValid() )
			return;

		SculptLibrary.Save( PersistSlot, Target );
	}

	bool _persistHooked;

	// While the session is ACTIVE, every commit (gizmo release / discrete edit / debounced HUD gesture) also
	// rewrites the persist slot, so the on-disk appearance always matches the last committed shape. An exit-only
	// save isn't enough: menu-scene teardown can destroy the Target before this component's OnDisabled runs, and
	// an app quit can skip teardown entirely — both would eat the player's edits. Hooked only while editing, so
	// out-of-edit-mode rebuilds (a carve from being shot) are never baked into the saved appearance; the load
	// rebuild in OnStart runs before the first activate, so restoring a shape never pointlessly resaves it.
	void HookPersistSlot()
	{
		if ( _persistHooked || string.IsNullOrWhiteSpace( PersistSlot ) || !Target.IsValid() )
			return;

		Target.Committed += SavePersistSlot;
		_persistHooked = true;
	}

	void UnhookPersistSlot()
	{
		if ( !_persistHooked )
			return;

		if ( Target is not null )
			Target.Committed -= SavePersistSlot; // plain null check: unsubscribe even from a destroyed component
		_persistHooked = false;
	}

	// Spawn one shared edit HUD (ScreenPanel + EditHud) the first time anyone edits, so the HUD ships with
	// the edit system rather than depending on a scene-placed GameObject. EditHud binds to Current, so a
	// single instance serves whichever session is active. Lives at the scene root (survives respawns).
	void EnsureHud()
	{
		if ( Scene is null || Scene.GetAllComponents<EditHud>().Any() )
			return;

		var go = new GameObject( true, "Edit HUD" );
		go.Flags |= GameObjectFlags.NotSaved; // runtime-only: never let this end up serialised into an asset
		go.Components.Create<ScreenPanel>();
		go.Components.Create<EditHud>();
	}

	protected override void OnUpdate()
	{
		UpdateWireframes(); // runs even when not editing so it can fade OUT after exit

		if ( !IsEditing || !Target.IsValid() )
			return;

		UpdateDofFocus(); // keep focus just behind the object as the camera orbits / blur is tuned

		var brushes = Target.Brushes;

		// (Q is NOT handled here: it's the pawn's Edit action — enter/exit EDIT MODE as a whole. Sculpt
		// mode is entered via the Add Shape button / shape tiles / number keys, and left via Esc/stamping.)

		// A flips add/carve — the head of the A/S/D/F "edit keys" row, matching the strip's chip order
		// (op · blend · round · wildcard). Stamp's op in sculpt mode, the SELECTED brush's in gizmo mode.
		bool opKey = Sandbox.UI.InputFocus.Current is null && Input.Keyboard.Down( "a" );
		if ( opKey && !_opKeyWas )
		{
			if ( Tool == SculptTool.Sculpt )
				SetStampOperation( StampOperation == SdfOperation.Add ? SdfOperation.Subtract : SdfOperation.Add );
			else if ( Selected >= 0 )
				ToggleOperation( Selected );
		}
		_opKeyWas = opKey;

		// Number keys follow the shape row left-to-right, so the tile order IS the hotkey order.
		if ( Input.Pressed( "Slot1" ) ) HotkeyShape( SdfShape.Sphere );
		if ( Input.Pressed( "Slot2" ) ) HotkeyShape( SdfShape.Box );
		if ( Input.Pressed( "Slot3" ) ) HotkeyShape( SdfShape.Cylinder );
		if ( Input.Pressed( "Slot4" ) ) HotkeyShape( SdfShape.Cone );
		if ( Input.Pressed( "Slot5" ) ) HotkeyShape( SdfShape.Extruded );
		if ( Input.Pressed( "Slot6" ) ) HotkeyShape( SdfShape.Spline );
		if ( Input.Pressed( "Slot7" ) ) HotkeyShape( SdfShape.Text );

		// Delete removes the selected brush (gizmo mode only — in sculpt mode the pending ghost isn't a
		// real shape yet, and Esc/right-click is how you drop that). Manual edge detection on Down, like
		// the other keys here: Keyboard.Pressed doesn't fire reliably in this context. Both spellings are
		// asked for since an unknown key name just resolves to invalid (false), never throws.
		bool del = Sandbox.UI.InputFocus.Current is null
			&& (Input.Keyboard.Down( "delete" ) || Input.Keyboard.Down( "del" ));
		if ( del && !_delWas && Tool == SculptTool.Gizmo )
			RemoveSelected();
		_delWas = del;
		if ( Input.Pressed( "Drop" ) ) RemoveLast();

		// Undo / redo: Ctrl+Z back, Ctrl+Shift+Z or Ctrl+Y forward. Manual edge detection like the keys above
		// (Keyboard.Pressed doesn't fire reliably in this context), and held off whenever a gesture owns the
		// mouse — stepping the shape out from under a live drag or scrub would leave it editing a brush that
		// no longer exists. The InputFocus guard leaves Ctrl+Z to the text field while typing.
		bool undoMod = Sandbox.UI.InputFocus.Current is null && Input.Keyboard.Down( "ctrl" )
			&& !PauseMenu.IsOpen && !IsScrubbing && !_gizmo.IsDragging;
		bool undoShift = Input.Keyboard.Down( "shift" );
		bool zKey = undoMod && Input.Keyboard.Down( "z" );
		bool undoKey = zKey && !undoShift;
		bool redoKey = (zKey && undoShift) || (undoMod && Input.Keyboard.Down( "y" ));

		bool stepped = false;
		if ( undoKey && !_undoWas ) stepped = Undo();
		if ( redoKey && !_redoWas ) stepped |= Redo();
		_undoWas = undoKey;
		_redoWas = redoKey;

		// A step replaced the brush list wholesale, so the `brushes` local above — and every selection/hover
		// index derived from it below — now refers to the OLD list. Bail out for this frame; the next one
		// re-picks against the restored brushes with the gizmo and ghosts already torn down.
		if ( stepped )
			return;

		if ( brushes is not { Count: > 0 } )
			return;

		// Keep the selection valid; -1 (nothing selected) is allowed and is the default — never force one.
		if ( Selected >= brushes.Count )
			Selected = -1;

		var tx = Target.WorldTransform;

		// Don't let world clicks/picks/gizmo-grabs fall through the HUD (palette or sliders) when over it.
		bool overUi = EditHud.PointerOverUi;

		// ── Sculpt mode: the stamp ghost owns the frame (no selection / gizmo / hover-pick). ─────────────
		if ( Tool == SculptTool.Sculpt )
		{
			SculptToolUpdate( tx, interactive: !overUi && !Input.Down( "Walk" ) && !PauseMenu.IsOpen );
			return;
		}

		// The gizmo fades in/out with the selection (matching the HUD palette/sliders). While fading out after a
		// deselect it keeps drawing the LAST brush at falling opacity, then tears down once invisible; it only
		// hovers/grabs while actually selected. ShowGizmo off (view-only) just fades it out and leaves it gone.
		if ( Selected >= 0 )
			_gizmoBrush = brushes[Selected]; // remember so the fade-out has something to draw

		float gizmoTarget = (ShowGizmo && Selected >= 0) ? 1f : 0f;
		_gizmoAlpha = MathX.Lerp( _gizmoAlpha, gizmoTarget, 1f - MathF.Exp( -GizmoFadeSpeed * Time.Delta ) );

		bool changed = false;
		if ( ShowGizmo && _gizmoBrush is not null && (gizmoTarget > 0f || _gizmoAlpha > 0.01f) )
		{
			changed = _gizmo.Update( tx, _gizmoBrush, Scene, Style,
				allowInteract: Selected >= 0 && !overUi && !IsScrubbing, alpha: _gizmoAlpha );
		}
		else
		{
			_gizmo.Hide();
			_gizmoAlpha = 0f;
			if ( Selected < 0 )
				_gizmoBrush = null;
		}

		// Hold-key param scrubs on the selection — the same A/S/D/E scheme the stamp tool uses, so both
		// modes share one muscle memory. Continuous changes preview; the key release runs the full commit.
		// Gated on IsDragging, NOT IsBusy: merely HOVERING a gizmo handle (which covers most of the shape)
		// must not eat the scrub keys — only an actual handle drag owns the mouse.
		changed |= BrushScrub.Update( SelectedBrush, tx, Scene.Camera,
			allow: !overUi && !Input.Down( "Walk" ) && !PauseMenu.IsOpen && !_gizmo.IsDragging, out bool scrubEnded );
		if ( scrubEnded )
			CommitChanged();

		// A right-click TAP (not a scale drag) clears the selection — the same "step back" gesture that
		// leaves sculpt mode. Skipped when the gizmo used that click to delete a spline control point.
		if ( BrushScrub.ScaleTapped && !_gizmo.RightClickConsumed )
			Deselect();

		// Scroll = push/pull the SELECTED brush along the view direction — the same size-scaled depth
		// control the stamp ghost has (wheel-up = away). Each notch previews; the debounce backstop below
		// runs the one full commit once the scrolling goes quiet.
		// (Skipped while a gizmo handle is being dragged — the drag owns the wheel there; a spline point
		// drag consumes it to push that single point along the view ray.)
		float pushWheel = Input.MouseWheel.y;
		if ( pushWheel != 0f && !overUi && !Input.Down( "Walk" ) && !PauseMenu.IsOpen
			&& BrushScrub.Active == ScrubKind.None && !_gizmo.IsDragging
			&& SelectedBrush is { } pushB && Scene.Camera is { } pushCam )
		{
			// A spline's transform is inert (its geometry lives in the control points), so pushing Position
			// would do nothing visible — shift every point instead, measured from the curve's centre.
			bool spline = pushB.Shape == SdfShape.Spline && pushB.Points is { Count: > 0 };
			var local = spline ? SplineCentre( pushB ) : pushB.Position;
			var world = tx.PointToWorld( local );
			var toCam = world - pushCam.WorldPosition;
			var dir = (tx.Rotation.Inverse * toCam).Normal;
			var offset = dir * pushWheel * _stampTool.AcceleratedDepthStep( DepthSizeRef( pushB ), toCam.Length );

			if ( spline )
			{
				var pts = pushB.Points;
				for ( int i = 0; i < pts.Count; i++ )
					pts[i] = new Vector4( pts[i].x + offset.x, pts[i].y + offset.y, pts[i].z + offset.z, pts[i].w );
			}
			else
			{
				pushB.Position += offset;
				pushB.SnapToMirrorPlanes();
			}

			PreviewChanged();
		}

		// Arm the spline add-point only once the cursor is over the line with Attack1 RELEASED. The click that
		// SELECTS a spline holds the button down through the frame the line first becomes hoverable, so it can't
		// arm — you have to release and press again to add. (Disarmed whenever the cursor leaves the line.)
		if ( !HoveringSplineLine )
			_splineInsertArmed = false;
		else if ( !Input.Down( "Attack1" ) )
			_splineInsertArmed = true;

		// Hover: the brush under the cursor (skipped while orbiting, over a gizmo handle, or over the UI).
		// Ghost it if it isn't already the selected one; a click selects it.
		int hover = (!overUi && !Input.Down( "Walk" ) && !_gizmo.IsBusy && !IsScrubbing) ? PickBrush( tx ) : -1;

		// Fall back to the brush the HUD's layer list is hovering, so mousing a row highlights its shape.
		// The scene pick wins when valid; this only fills in when the cursor isn't over a 3D brush.
		if ( hover < 0 && UiHoverBrush >= 0 && UiHoverBrush < brushes.Count )
			hover = UiHoverBrush;

		_hoverBrush = hover; // for the wireframe's hover highlight

		// Ghost the hovered brush (unless it's the selected one — that shows the gizmo instead). Hovering straight
		// from one shape to another cross-fades: the brush you left slides to the outgoing slot and fades out while
		// the new one fades in. Both keep drawing while they fade, then tear down once invisible.
		int ghostHover = (hover >= 0 && hover != Selected) ? hover : -1;
		var hoverBrush = ghostHover >= 0 ? brushes[ghostHover] : null;

		if ( hoverBrush != _ghostBrush )
		{
			if ( hoverBrush is not null && hoverBrush == _ghostOutBrush )
			{
				// Flicked back onto the shape that was fading out — revive it as the incoming (don't double it up).
				_ghostAlpha = _ghostOutAlpha;
				_ghostOutBrush = null;
				_ghostOutAlpha = 0f;
			}
			else if ( _ghostBrush is not null )
			{
				// Retire the old incoming to the outgoing slot so the two cross-fade.
				_ghostOutBrush = _ghostBrush;
				_ghostOutAlpha = _ghostAlpha;
				_ghostAlpha = 0f;
			}
			else
			{
				_ghostAlpha = 0f;
			}
			_ghostBrush = hoverBrush;
		}

		float ghostFade = 1f - MathF.Exp( -HoverFadeSpeed * Time.Delta );
		float ghostTarget = _ghostBrush is not null ? 1f : 0f;
		_ghostAlpha = MathX.Lerp( _ghostAlpha, ghostTarget, ghostFade );
		if ( MathF.Abs( _ghostAlpha - ghostTarget ) < 0.01f )
			_ghostAlpha = ghostTarget; // snap when settled so a steady hover stops re-meshing (its hash keys on colour)
		_ghostOutAlpha = MathX.Lerp( _ghostOutAlpha, 0f, ghostFade );
		if ( _ghostOutAlpha < 0.01f )
			_ghostOutAlpha = 0f;

		DrawGhost( _ghost, _ghostBrush, _ghostAlpha, tx );
		DrawGhost( _ghostOut, _ghostOutBrush, _ghostOutAlpha, tx );
		if ( _ghostOutAlpha <= 0f )
			_ghostOutBrush = null;

		// A selected CARVE brush has no surface of its own (it's a hole) — draw its red wireframe so you
		// can see what you're moving. Same single-brush wire the carve stamp uses.
		if ( SelectedBrush is { Operation: SdfOperation.Subtract } carveSel )
		{
			_stampWireList.Clear();
			_stampWireList.Add( carveSel );
			_stampWire.Draw( _stampWireList, tx, Scene, Scene.Camera,
				selected: 0, hovered: -1, masterAlpha: 0.9f, Style.OutlineThickness, WireframeDepthBias );
		}
		else
		{
			_stampWire.Hide();
		}

		// A click selects the hovered shape, or clears the selection when it lands on empty space. Gated so it
		// only fires on a real world click: not over the HUD, not alt-orbiting, and not on a gizmo handle (a
		// handle grab reads hover<0, so without the IsBusy guard starting a drag would also deselect).
		if ( Input.Pressed( "Attack1" ) && !overUi && !Input.Down( "Walk" ) && !_gizmo.IsBusy && !IsScrubbing )
			Selected = hover; // hover is -1 on empty space → deselect

		// Debug: render the shadow-proxy mesh AS the visible surface (raymarch + full mesh hidden).
		if ( ShowShadowProxy )
		{
			EnterProxyDebug();
			if ( changed )
				Target.RebuildShadowProxy( visible: true );
			_wasManipulating = IsManipulating;
			return;
		}
		if ( _proxyDebugActive )
			ExitProxyDebug();

		// Magnetic centre snap: a mirrored brush moved (gizmo drag) or resized (scrub) into the symmetry
		// deadzone physically centres on the plane instead of just quietly losing its mirror copy.
		if ( changed )
			SelectedBrush?.SnapToMirrorPlanes();

		if ( changed )
		{
			// While dragging a handle (or running a hold-key scrub), skip the heavy surface-nets remesh: the
			// raymarcher shows the live surface, so the meshed model only matters for SHADOWS — use a cheap
			// union-of-primitives proxy. The scrub's key release commits via CommitChanged above.
			if ( IsManipulating || IsScrubbing )
				Target.RebuildShadowProxy();
			else
				CommitChanged(); // a DISCRETE gizmo change (spline point insert/delete) — full commit
		}

		// Drag just released → do the accurate full remesh once (nice LODs / exact shadows).
		// CommitChanged, not a bare Rebuild: a handle drag is a real edit and has to reach the commit funnel
		// or it never becomes an undo step (and any preview still pending underneath it stays pending).
		// Both calls here are safe to double up — the undo stack's content hash collapses them to one step.
		if ( _wasManipulating && !IsManipulating )
			CommitChanged();
		_wasManipulating = IsManipulating;

		// Backstop commit for HUD previews with no explicit end event (text typing) or a missed control
		// release: once the edits go quiet and nothing is still captured, run the deferred full commit.
		if ( _pendingCommit && _sincePreview > CommitDebounce
			&& !ClaySlider.Pressed && !ClayColorPicker.Pressed && !IsManipulating )
			CommitChanged();

		// Field cache: the GPU field evaluator stays on, at full resolution, for the WHOLE active session —
		// it's cheap locally (the renderer only re-dispatches the compute eval when brushes actually change,
		// and this is the one prop this machine is editing), so every edit updates instantly with no CPU bake
		// or settle hitch. FieldResolutionScale belongs to SdfNetworkSync, which lowers it on PROXIES during
		// remote drags (where per-frame interpolation re-dispatches every frame, per prop).
	}

	// Debug view: hide the raymarcher (its OnDisabled hands the sibling mesh back to visible) and put the
	// proxy on the ModelRenderer. Restored by ExitProxyDebug.
	void EnterProxyDebug()
	{
		if ( _proxyDebugActive )
			return;

		_proxyDebugActive = true;
		var rm = Raymarcher;
		if ( rm.IsValid() )
			rm.Enabled = false;

		Target.RebuildShadowProxy( visible: true );
	}

	void ExitProxyDebug()
	{
		_proxyDebugActive = false;
		var rm = Raymarcher;
		if ( rm.IsValid() )
			rm.Enabled = true; // raymarch resumes + sets the mesh back to ShadowsOnly

		if ( Target.IsValid() )
		{
			var mr = Target.GameObject.Components.Get<ModelRenderer>();
			if ( mr.IsValid() )
				mr.MaterialOverride = null; // drop the wireframe override

			Target.Rebuild(); // restore the full surface-nets mesh
		}
	}

	// Faint translucent overlay tint — cyan for additive brushes, red for subtractive (matches the editor).
	static Color HoverColor( SdfBrush b ) =>
		(b.Operation == SdfOperation.Subtract ? Color.Red : Color.Cyan).WithAlpha( 0.2f );

	// Draw one hover ghost at the given opacity (fades the base hover tint), or hide it when invisible.
	void DrawGhost( BrushGhost ghost, SdfBrush brush, float alpha, Transform tx )
	{
		if ( brush is not null && alpha > 0.01f )
		{
			var col = HoverColor( brush );
			ghost.Show( brush, tx, Scene, col.WithAlpha( col.a * alpha ) );
		}
		else
		{
			ghost.Hide();
		}
	}

	// Tear down both hover ghosts and clear their fade state (on edit-mode exit / disable).
	void HideGhosts()
	{
		_ghost.Hide();
		_ghostOut.Hide();
		_ghostBrush = _ghostOutBrush = null;
		_ghostAlpha = _ghostOutAlpha = 0f;
	}

	// The curve's centre in sculpture space (its control points already live there).
	static Vector3 SplineCentre( SdfBrush b )
	{
		b.LocalBounds( out var mn, out var mx, includeBlend: false );
		return (mn + mx) * 0.5f;
	}

	// What the scroll-depth step scales off: a solid's largest dimension, or a spline's fattest point.
	static float DepthSizeRef( SdfBrush b )
	{
		if ( b.Shape != SdfShape.Spline || b.Points is not { Count: > 0 } pts )
			return MathF.Max( b.Size.x, MathF.Max( b.Size.y, b.Size.z ) );

		float r = 0f;
		foreach ( var p in pts )
			r = MathF.Max( r, p.w );
		return r;
	}

	// Number-key shape hotkeys — identical semantics to clicking a shape tile: re-mould the held stamp,
	// CONVERT the selection in place, or (with nothing selected) pick up a stamp of that shape.
	void HotkeyShape( SdfShape shape )
	{
		if ( Tool == SculptTool.Sculpt )
		{
			SetShape( shape );
			return;
		}

		if ( SelectedBrush is not null )
		{
			ConvertSelectedShape( shape );
			return;
		}

		SetTool( SculptTool.Sculpt );
		SetShape( shape );
	}

	// ── Sculpt mode (stamp ghost) ────────────────────────────────────────────────────────────────────

	void SculptToolUpdate( Transform tx, bool interactive )
	{
		// No selection UI in add mode: gizmo down instantly, hover cross-fade state cleared — the incoming
		// hover ghost slot is reused as the stamp's translucent shell.
		_gizmo.Hide();
		_gizmoAlpha = 0f;
		_gizmoBrush = null;
		_hoverBrush = -1;
		_ghostOut.Hide();
		_ghostOutBrush = null;
		_ghostOutAlpha = 0f;

		_ghost.Hide();
		_ghostBrush = null;
		_ghostAlpha = 0f;

		bool changed = _stampTool.Update( Target, Scene, interactive, out bool committed );

		// No overlay for an additive stamp — the live surface IS the preview. A carve stamp is the exception:
		// hovering empty space it removes nothing, so it'd be invisible — give it the red editor wireframe.
		var stamp = StampBrush;
		if ( stamp is not null && stamp.Operation == SdfOperation.Subtract )
		{
			_stampWireList.Clear();
			_stampWireList.Add( stamp );
			_stampWire.Draw( _stampWireList, tx, Scene, Scene.Camera,
				selected: 0, hovered: -1, masterAlpha: 0.9f, Style.OutlineThickness, WireframeDepthBias );
		}
		else
		{
			_stampWire.Hide();
		}

		// A spline chain finished with too few points to be a curve — strip the ghost, no commit.
		if ( _stampTool.Abandon )
		{
			_stampTool.Abandon = false;
			SetTool( SculptTool.Gizmo );
			return;
		}

		if ( committed )
		{
			NotifyChanged(); // the stamp is real now (StampBrush is null until the next ghost spawns) → full commit

			// Place-then-tweak: a stamp drops you straight into the EDIT tool with the new brush selected,
			// gizmo up. Back to Add via Q, a shape tile, or a number key.
			var placed = _stampTool.LastCommitted;
			SetTool( SculptTool.Gizmo );
			int idx = placed is not null ? (Target.Brushes?.IndexOf( placed ) ?? -1) : -1;
			if ( idx >= 0 )
				Selected = idx;
		}
		else if ( changed )
		{
			Target.RebuildShadowProxy(); // live preview: the raymarcher reads the brushes; shadows + net stream follow
		}
	}

	void RemoveLast()
	{
		var brushes = Target.Brushes;
		if ( brushes is null )
			return;

		// Never remove the pending stamp ghost, and keep at least one real brush to edit.
		var ghost = StampBrush;
		if ( brushes.Count - (ghost is not null ? 1 : 0) <= 1 )
			return;

		for ( int i = brushes.Count - 1; i >= 0; i-- )
		{
			if ( brushes[i] == ghost )
				continue;
			brushes.RemoveAt( i );
			break;
		}

		Selected = ghost is null ? brushes.Count - 1 : -1;
		NotifyChanged();
	}

	// Sphere-trace the field along the cursor ray and return the brush owning the surface point (or -1).
	// Pure-Sdf, so it works identically at runtime and in the editor tool. Assumes unit scale.
	int PickBrush( Transform tx )
	{
		var cam = Scene?.Camera;
		if ( cam is null )
			return -1;

		var ray = cam.ScreenPixelToRay( Mouse.Position );

		// Cursor ray into sculpture-local space, then the shared pick (handles subtractive volumes too).
		var invRot = tx.Rotation.Inverse;
		var o = invRot * (ray.Position - tx.Position);
		var d = (invRot * ray.Forward).Normal;

		// Damage brushes (shot craters) aren't editable — a pick landing on one reads as empty space.
		int picked = Sdf.PickBrush( Target.Brushes, o, d );
		return picked >= 0 && Target.Brushes[picked].Damage ? -1 : picked;
	}

	// ── Brush wireframes ─────────────────────────────────────────────────────────────────────────────
	// Outline every brush (the scene-view wireframes), as its own Tab toggle, fading in/out.

	/// <summary>Toggle the brush wireframe overlay (bound to Tab). Only does anything while editing — the
	/// overlay is edit-mode-only, so the toggle is ignored (and stays off) when you're not in edit mode.</summary>
	public void ToggleWireframes()
	{
		if ( IsEditing )
			_wireframesOn = !_wireframesOn;
	}

	void UpdateWireframes()
	{
		// Can't draw without a sculpture — drop instantly.
		if ( !Target.IsValid() )
		{
			_wireAlpha = 0f;
			_wireframes.Hide();
			return;
		}

		// Ease toward on/off — only while editing (Tab toggles the preference, but it renders in edit mode
		// only, fading out when you exit). Keep drawing until the fade-out finishes.
		float target = (IsEditing && ShowWireframes && _wireframesOn) ? 1f : 0f;
		_wireAlpha = MathX.Lerp( _wireAlpha, target, 1f - MathF.Exp( -WireframeFadeSpeed * Time.Delta ) );

		if ( _wireAlpha <= 0.01f )
		{
			_wireframes.Hide();
			return;
		}

		// Match the editor exactly: colour (cyan/red) + per-state opacity live in BrushWireframes; thickness
		// and drag-opacity come from the SAME GizmoSettings the editor tool uses. Master alpha = fade × drag.
		var st = Style;
		float dragAlpha = IsManipulating ? st.DragOpacity : 1f;
		float master = _wireAlpha * dragAlpha;
		int hover = IsEditing ? _hoverBrush : -1;

		_wireframes.Draw( Target.Brushes, Target.WorldTransform, Scene, Scene.Camera,
			Selected, hover, master, st.OutlineThickness, WireframeDepthBias );
	}

	// ── Depth of field (main camera) ─────────────────────────────────────────────────────────────────
	// Pull focus onto the edited object on enter (tracked while editing), then ease back on exit. The actual
	// rack/easing lives in MainCamera — we just set its targets via MainCamera.Dof, so it lerps nicely.

	bool _dofSaved;
	float _savedFocal, _savedRange, _savedBlur;
	float _lastFocusDist;

	void ApplyEditDof()
	{
		if ( !EditDepthOfField || !MainCamera.Current.IsValid() )
			return;

		// Remember the current DoF so we can ease back to it on exit.
		_savedFocal = MainCamera.TargetFocalDistance;
		_savedRange = MainCamera.TargetFocusRange;
		_savedBlur = MainCamera.TargetBlurSize;
		_dofSaved = true;

		// Seed so the first frame reads as "not moving" → the rack-in eases rather than snapping.
		_lastFocusDist = Vector3.DistanceBetween( MainCamera.Position, FocusPoint() );

		UpdateDofFocus(); // eases in toward the object
	}

	void UpdateDofFocus()
	{
		if ( !EditDepthOfField || !MainCamera.Current.IsValid() )
			return;

		float dist = Vector3.DistanceBetween( MainCamera.Position, FocusPoint() );

		// Snap the focal plane while zooming (distance changing fast) so it doesn't trail the camera; ease
		// otherwise (enter/exit rack, orbiting). Blur/range always ease (and pick up live tweaks).
		float speed = Time.Delta > 0f ? MathF.Abs( dist - _lastFocusDist ) / Time.Delta : 0f;
		_lastFocusDist = dist;

		MainCamera.Dof.SetFocal( dist + DofFocusOffset, lerp: speed <= DofZoomSnapSpeed );
		MainCamera.Dof.SetBlur( DofBlurSize, lerp: true );
		MainCamera.Dof.FocusRange = DofFocusRange;
	}

	void RestoreDof()
	{
		if ( !_dofSaved )
			return;

		_dofSaved = false;
		MainCamera.Dof.Set( _savedFocal, _savedRange, _savedBlur, lerp: true ); // eases back to normal
	}

	Vector3 FocusPoint()
	{
		// Exclude the pending stamp ghost — the DoF focus must not chase the cursor while placing.
		if ( Target.IsValid() && Sdf.TryGetBounds( Target.Brushes, out var bounds, StampBrush ) )
			return Target.WorldTransform.PointToWorld( bounds.Center );

		return Target.IsValid() ? Target.WorldPosition : WorldPosition;
	}
}
