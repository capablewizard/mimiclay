using System;

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

	/// <summary>Keep the sculpt centred over its parent: between operations (no gizmo drag, no alt-nav, no
	/// held click) the sculpture GAMEOBJECT glides within its parent until the shape's bounds centre sits on
	/// the parent's origin — X/Y only; the ground lift stays put and up/down framing stays the player's. The
	/// brush data never changes (no rebuilds, and symmetry planes ride along with the object) — the whole
	/// clay just drifts home: to the middle of the screen (the hider's camera follows the PARENT pawn, not
	/// the sculpture) and onto the pivot the prop rotates around in play mode. An unfinished glide keeps
	/// settling after edit mode ends, and proxies run it too — the goal derives purely from the synced
	/// brushes, so every machine converges to the same offset. Off by default: enable it only where the
	/// sculpture hangs under a root that owns the pivot (the hider's disguise) — a face sculpture must stay
	/// put on its head.</summary>
	[Property, Group( "Recenter" )] public bool RecenterSculpt { get; set; }

	/// <summary>How fast the glide closes on centre (per second, exponential). Kept gentle — the clay should
	/// drift home, not snap.</summary>
	[Property, Group( "Recenter" )] public float RecenterSpeed { get; set; } = 2f;

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

	/// <summary>True while a gizmo handle is being dragged (so the HUD can fade out of the way).</summary>
	public bool IsManipulating => _gizmo.IsDragging;

	/// <summary>True while the cursor is over the selected spline's line — the HUD swaps to the add-point
	/// cursor, and a click (handled in the gizmo) inserts a control point there.</summary>
	public bool HoveringSplineLine => IsEditing && _gizmo.HoveringSplineLine;

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
		if ( StartActive )
			SetActive( true );
	}

	public void Toggle() => SetActive( !IsEditing );

	public void SetActive( bool active )
	{
		if ( active == IsEditing )
			return;

		IsEditing = active;

		// Distance-field cache: NOT suppressed for the whole session. OnUpdate suppresses it only while a handle
		// is actively dragged (instant feedback, no per-edit bakes), and un-suppresses the moment editing settles
		// or you're just rotating/idle — so even an always-active session (the menu sculpt) caches when not being
		// edited. Reset to false here so a fresh activate, and any deactivate, starts from the cached state.
		var rmField = Raymarcher;
		if ( rmField.IsValid() )
			rmField.SuppressFieldCache = false;

		if ( active )
		{
			Current = this;
			EnsureHud(); // the edit system brings its own HUD — no scene setup needed (and works in any game mode)
			ApplyEditDof();
		}
		else if ( Current == this )
		{
			Current = null;
		}

		// Tear down the rendered gizmo + ghost when leaving edit mode (works whether or not we own a camera).
		if ( !active )
		{
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
		_gizmo.Hide();
		HideGhosts();
		_wireframes.Hide();
		_wireAlpha = 0f;
		RestoreDof();
		if ( _proxyDebugActive )
			ExitProxyDebug();

		// Don't leave the prop stuck on the per-brush path if the session is torn down mid-edit — let it cache.
		var rmField = Raymarcher;
		if ( rmField.IsValid() )
			rmField.SuppressFieldCache = false;

		if ( Current == this )
			Current = null;
	}

	// ── HUD-facing API (drives the same logic regardless of game mode) ───────────────────────────────

	/// <summary>Add an additive (or subtractive) brush of the given shape and select it.</summary>
	public void Add( SdfShape shape, SdfOperation operation = SdfOperation.Add )
	{
		if ( !Target.IsValid() )
			return;

		Target.AddBrush( shape, operation );
		Selected = Target.Brushes.Count - 1;
	}

	/// <summary>Select a brush by index, or pass a negative index to clear the selection.</summary>
	public void Select( int index )
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		Selected = ( b is { Count: > 0 } && index >= 0 ) ? Math.Clamp( index, 0, b.Count - 1 ) : -1;
	}

	/// <summary>Clear the selection (gizmo/palette/sliders hide).</summary>
	public void Deselect() => Selected = -1;

	/// <summary>Remove the selected brush (keeps at least one) and rebuild. Leaves nothing selected.</summary>
	public void RemoveSelected()
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( Selected < 0 || b is not { Count: > 1 } )
			return;

		b.RemoveAt( Math.Clamp( Selected, 0, b.Count - 1 ) );
		Selected = -1;
		Target.Rebuild();
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
		Target.Rebuild();
	}

	/// <summary>Flip a brush between additive and subtractive (the +/- button) and rebuild.</summary>
	public void ToggleOperation( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		b.Operation = b.Operation == SdfOperation.Add ? SdfOperation.Subtract : SdfOperation.Add;
		Target.Rebuild();
	}

	/// <summary>Toggle symmetry on a brush (the symmetry button): clears all axes if any are on, else turns
	/// on left/right (X) mirroring — the usual one. Per-axis control still lives in the Symmetry section.</summary>
	public void ToggleSymmetry( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		bool any = b.MirrorX || b.MirrorY || b.MirrorZ;
		b.MirrorX = b.MirrorY = b.MirrorZ = false;
		if ( !any )
			b.MirrorX = true;

		Target.Rebuild();
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

		Target.Rebuild();
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
		Target.Rebuild();
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

		Target.Rebuild();
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

	/// <summary>Rebuild after the HUD edits a brush property (colour/metalness/roughness/etc.).</summary>
	public void NotifyChanged()
	{
		if ( Target.IsValid() )
			Target.Rebuild();
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
		Target.Rebuild();
		return true;
	}

	// Spawn one shared edit HUD (ScreenPanel + EditHud) the first time anyone edits, so the HUD ships with
	// the edit system rather than depending on a scene-placed GameObject. EditHud binds to Current, so a
	// single instance serves whichever session is active. Lives at the scene root (survives respawns).
	void EnsureHud()
	{
		if ( Scene is null || Scene.GetAllComponents<EditHud>().Any() )
			return;

		var go = new GameObject( true, "Edit HUD" );
		go.Components.Create<ScreenPanel>();
		go.Components.Create<EditHud>();
	}

	protected override void OnUpdate()
	{
		UpdateWireframes();     // runs even when not editing so it can fade OUT after exit
		UpdateRecenterGlide();  // runs even when not editing so a glide cut short by exiting still settles

		if ( !IsEditing || !Target.IsValid() )
			return;

		UpdateDofFocus(); // keep focus just behind the object as the camera orbits / blur is tuned

		var brushes = Target.Brushes;

		// Add shapes (number keys) / remove the last (G). A new shape becomes the selection. UI panel later.
		if ( Input.Pressed( "Slot1" ) ) AddShape( Target.AddSphere );
		if ( Input.Pressed( "Slot2" ) ) AddShape( Target.AddBox );
		if ( Input.Pressed( "Slot3" ) ) AddShape( Target.AddCylinder );
		if ( Input.Pressed( "Slot4" ) ) AddShape( Target.AddCone );
		if ( Input.Pressed( "Drop" ) ) RemoveLast();

		if ( brushes is not { Count: > 0 } )
			return;

		// Keep the selection valid; -1 (nothing selected) is allowed and is the default — never force one.
		if ( Selected >= brushes.Count )
			Selected = -1;

		var tx = Target.WorldTransform;

		// Don't let world clicks/picks/gizmo-grabs fall through the HUD (palette or sliders) when over it.
		bool overUi = EditHud.PointerOverUi;

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
				allowInteract: Selected >= 0 && !overUi, alpha: _gizmoAlpha );
		}
		else
		{
			_gizmo.Hide();
			_gizmoAlpha = 0f;
			if ( Selected < 0 )
				_gizmoBrush = null;
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
		int hover = (!overUi && !Input.Down( "Walk" ) && !_gizmo.IsBusy) ? PickBrush( tx ) : -1;

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

		// A click selects the hovered shape, or clears the selection when it lands on empty space. Gated so it
		// only fires on a real world click: not over the HUD, not alt-orbiting, and not on a gizmo handle (a
		// handle grab reads hover<0, so without the IsBusy guard starting a drag would also deselect).
		if ( Input.Pressed( "Attack1" ) && !overUi && !Input.Down( "Walk" ) && !_gizmo.IsBusy )
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

		if ( changed )
		{
			// While dragging a handle, skip the heavy surface-nets remesh: the raymarcher shows the live
			// surface, so the meshed model only matters for SHADOWS — use a cheap union-of-primitives proxy.
			if ( IsManipulating )
				Target.RebuildShadowProxy();
			else
				Target.Rebuild();
		}

		// Drag just released → do the accurate full remesh once (nice LODs / exact shadows).
		if ( _wasManipulating && !IsManipulating )
			Target.Rebuild();
		_wasManipulating = IsManipulating;

		// Field cache: use the GPU field evaluator for the WHOLE active session. It's cheap (the renderer only
		// re-dispatches the compute eval when brushes actually change), so every edit updates instantly whether
		// it's a handle drag OR a slider (blend, rounding, curvature, colour) - no CPU bake, no settle hitch.
		// Un-suppressed on deactivate / disable (above), so a settled prop falls back to the shared cached bake.
		var rmCache = Raymarcher;
		if ( rmCache.IsValid() )
			rmCache.SuppressFieldCache = true;
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

	void AddShape( Action add )
	{
		add();
		Selected = Target.Brushes.Count - 1; // select the brush we just added
	}

	void RemoveLast()
	{
		var brushes = Target.Brushes;
		if ( brushes is not { Count: > 1 } ) // keep at least one brush so there's always something to edit
			return;

		brushes.RemoveAt( brushes.Count - 1 );
		Selected = brushes.Count - 1;
		Target.Rebuild();
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

		return Sdf.PickBrush( Target.Brushes, o, d );
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

	// ── Sculpt recentring ────────────────────────────────────────────────────────────────────────────
	// Editing can walk the shape away from the pivot the prop rotates around in play (its parent's origin),
	// so rotation swings the clay in an arc — and since the hider's camera follows the parent pawn, the clay
	// sits off-centre on screen too. Fix: glide the sculpture GAMEOBJECT within its parent until the shape's
	// bounds centre is over the parent origin. The brushes never change — no rebuilds, and the symmetry
	// planes travel with the object — the whole clay just drifts home, which reads on screen as the object
	// easing into the middle of the view. OnUpdate calls this BEFORE its IsEditing gate, so a glide cut short
	// by leaving edit mode keeps settling; proxies run it too and converge on the same synced-brush-derived
	// goal.

	void UpdateRecenterGlide()
	{
		if ( !RecenterSculpt || !Target.IsValid() )
			return;

		// While editing, anything in progress pauses the glide: a gizmo drag, alt-nav (even just holding
		// alt), any held click (HUD sliders included) — plus the release frame, so its full remesh isn't
		// competing with the start of a drift. (Proxies never edit, so they glide whenever off-centre.)
		if ( IsEditing && (IsManipulating || _wasManipulating || AltNav.Held || Input.Down( "Attack1" )) )
			return;

		if ( !Sdf.TryGetBounds( Target.Brushes, out var bounds ) )
			return;

		var go = Target.GameObject;

		// The local position that puts the bounds centre on the parent's origin in X/Y. Z — the ground lift —
		// is deliberate and stays exactly where it is, so the player keeps their up/down framing.
		var centred = -(go.LocalRotation * bounds.Center);
		var local = go.LocalPosition;
		var goal = new Vector3( centred.x, centred.y, local.z );

		var delta = goal - local;
		if ( delta.LengthSquared < 0.0001f )
			return; // settled

		// Exponential glide; the last quarter-unit snaps so it actually terminates.
		go.LocalPosition = delta.Length < 0.25f
			? goal
			: Vector3.Lerp( local, goal, 1f - MathF.Exp( -RecenterSpeed * Time.Delta ) );
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
		if ( Target.IsValid() && Sdf.TryGetBounds( Target.Brushes, out var bounds ) )
			return Target.WorldTransform.PointToWorld( bounds.Center );

		return Target.IsValid() ? Target.WorldPosition : WorldPosition;
	}
}
