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

/// <summary>What the world-space cursor should show, on the one grab-language rule: OPEN hand over
/// anything grabbable, CLOSED hand while anything is grabbed. None = whatever the UI/default says.</summary>
public enum GrabCursor
{
	None,
	Open,
	Closed,
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

	/// <summary>While editing, pull the main camera's depth of field onto the edited object (the whole
	/// sculpt stays sharp, the background blurs). The sharp band is sized from the sculpt's bounds, so it
	/// grows with the sculpt. On exit the camera eases back to its authored gameplay baseline by itself.
	/// All of these tune live while editing — they're re-read every frame.</summary>
	[Property, Group( "Depth of Field" )] public bool EditDepthOfField { get; set; } = true;

	/// <summary>Blur strength applied while editing.</summary>
	[Property, Group( "Depth of Field" )] public float DofBlurSize { get; set; } = 7f;

	/// <summary>Sharp margin (world units) in front of the sculpt's near surface — how much space toward
	/// the camera stays in focus before the blur starts.</summary>
	[Property, Group( "Depth of Field" )] public float DofFrontPadding { get; set; } = 16f;

	/// <summary>Sharp margin (world units) behind the sculpt's far surface — bigger keeps more of the
	/// backdrop in focus before the background blur ramps in.</summary>
	[Property, Group( "Depth of Field" )] public float DofBackPadding { get; set; } = 48f;

	/// <summary>How much of the sculpt's bounds radius counts toward the sharp band. 1 = the whole sculpt
	/// stays in focus no matter how big it gets; 0 = ignore size (only the pads around its centre are sharp).</summary>
	[Property, Group( "Depth of Field" )] public float DofSizeScale { get; set; } = 1f;

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
	bool _addKeyWas; // …and for the Space add-shape toggle
	bool _delWas;   // …and for the Delete key
	bool _undoWas;  // …and for Ctrl+Z
	bool _redoWas;  // …and for Ctrl+Shift+Z / Ctrl+Y

	// Undo history for this session — one step per COMMIT, seeded on activate and dropped on exit.
	// See SculptUndo for why it stores authored-prefix states rather than deltas or whole brush lists.
	readonly SculptUndo _undo = new();

	/// <summary>Snap is a HOLD, and Shift is THE snap modifier everywhere: stamp placement, the gizmo's
	/// move handles and spline point drags all snap to the origin-centred <see cref="GridStep"/> position
	/// grid, and every rotate control (E-scrub, rotation rings, trackball) snaps to the
	/// <see cref="SnapDeg"/> angle grid.</summary>
	public static bool SnapHeld =>
		Sandbox.UI.InputFocus.Current is null && Input.Keyboard.Down( "shift" );

	/// <summary>The angle grid (degrees) every rotate control snaps to while Shift is held — half-steps
	/// between the 45s: 0 / 22.5 / 45 / 67.5 / 90…</summary>
	public const float SnapDeg = 22.5f;

	/// <summary>Position grid cell size (sculpture-local units) for shift-snapped placement and moves. The
	/// grid is centred on the sculpture origin, so 0,0,0 — the symmetry planes — is always on-grid.</summary>
	public const float GridStep = 8f;

	/// <summary>Quantize a sculpture-local position onto the <see cref="GridStep"/> grid.</summary>
	public static Vector3 SnapPosition( Vector3 p ) => new(
		MathF.Round( p.x / GridStep ) * GridStep,
		MathF.Round( p.y / GridStep ) * GridStep,
		MathF.Round( p.z / GridStep ) * GridStep );

	/// <summary>Quantize an orientation onto the <see cref="SnapDeg"/> angle grid: its local Euler angles
	/// each rounded to the step. Lands exactly on the clean axis-aligned orientations (0/45/90…) in the
	/// sculpture's frame.</summary>
	public static Rotation SnapRotation( Rotation r )
	{
		var a = r.Angles();
		return Rotation.From(
			MathF.Round( a.pitch / SnapDeg ) * SnapDeg,
			MathF.Round( a.yaw / SnapDeg ) * SnapDeg,
			MathF.Round( a.roll / SnapDeg ) * SnapDeg );
	}

	/// <summary>The shared accelerated scroll-depth step (size-scaled, scroll-accelerated,
	/// camera-distance-scaled — see <see cref="BrushStampTool.AcceleratedDepthStep"/>). Exposed so the
	/// gizmo's spline point drags push/pull with exactly the same feel as ghost placement and the
	/// selection push.</summary>
	public float DepthStep( float sizeRef, float camDist ) =>
		_stampTool.AcceleratedDepthStep( sizeRef, camDist );


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

	// Brush under the cursor via the 3D scene pick ONLY (never the layer-row hover fallback that feeds
	// _hoverBrush) — the grab cursor must not go open-hand while the mouse is over a HUD row.
	int _worldHover = -1;

	/// <summary>The grab cursor the world should show this frame — ONE rule for the whole edit system:
	/// open hand over anything grabbable (a gizmo handle, a spline point/radius dot, a shape you can
	/// select), closed hand while anything is grabbed (a handle drag, the stamp ghost riding the cursor).
	/// None while a captured gesture hides the cursor (the drawn dot owns it), while the add-point cursor
	/// owns a spline-line hover, or when there's just nothing to grab. The HUD paints it via its
	/// full-screen cursor zone (the same trick as the add-point cursor).</summary>
	public GrabCursor WorldCursor
	{
		get
		{
			if ( !IsEditing || PauseMenu.IsOpen )
				return GrabCursor.None;
			if ( AltNav.Dragging || IsScrubbing )
				return GrabCursor.None; // cursor hidden by the mouse capture — the drawn dot stands in

			// Sculpt mode: the stamp ghost rides the cursor — it IS a grabbed thing, the whole time.
			if ( Tool == SculptTool.Sculpt )
				return StampBrush is not null ? GrabCursor.Closed : GrabCursor.None;

			if ( _gizmo.IsDragging || _gizmo.IsUiDragging )
				return GrabCursor.Closed;
			if ( _gizmo.HoveringSplineLine )
				return GrabCursor.None; // the add-point cursor panel owns this hover
			if ( _gizmo.IsHoveringGrabbable )
				return GrabCursor.Open;
			if ( _worldHover >= 0 )
				return GrabCursor.Open; // a shape you can pick up (select) is under the cursor

			return GrabCursor.None;
		}
	}

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

		var brush = IsMultiSelection ? _multiProxy : SelectedBrush; // multi: the circle sits on the pivot
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

	/// <summary>Index of the PRIMARY selected brush, or -1 for no selection (the default). With nothing
	/// selected the gizmo, palette and sliders all hide; click a shape to select it. Ctrl/shift clicks grow
	/// the selection beyond one brush (see <see cref="Selection"/>); assigning here — which every legacy
	/// path does (add, stamp commit, undo restore, load, remove…) — collapses it back to just this brush,
	/// so single-selection behaviour is exactly what it always was.</summary>
	public int Selected
	{
		get => _selected;
		private set
		{
			_selected = value >= 0 ? value : -1;
			_selection.Clear();
			if ( _selected >= 0 )
				_selection.Add( _selected );
			_anchor = _selected;
		}
	}

	int _selected = -1;
	readonly List<int> _selection = new(); // every selected index (primary included), ascending
	int _anchor = -1;                      // shift-range pivot: the row last plainly/ctrl-clicked

	/// <summary>True when a brush is selected (the gizmo/HUD are shown).</summary>
	public bool HasSelection => Selected >= 0;

	/// <summary>The PRIMARY selected brush — what the property panels display — or null when nothing is
	/// selected. Group edits act on <see cref="SelectedBrushes"/> / <see cref="EditTargets"/> instead.</summary>
	public SdfBrush SelectedBrush
	{
		get
		{
			var b = Target.IsValid() ? Target.Brushes : null;
			return b is { Count: > 0 } && Selected >= 0 && Selected < b.Count ? b[Selected] : null;
		}
	}

	/// <summary>Every selected brush index (the primary included), ascending. One entry on a plain click;
	/// ctrl/shift grow it, s&amp;box-hierarchy style — see <see cref="Select(int,bool,bool)"/>.</summary>
	public IReadOnlyList<int> Selection => _selection;

	/// <summary>Is this brush index part of the selection (primary or not)? Drives the layer-row highlight.</summary>
	public bool IsSelected( int index ) => index >= 0 && _selection.Contains( index );

	/// <summary>More than one brush is selected — the gizmo drives the whole group through the pivot proxy
	/// and property edits fan out to every member.</summary>
	public bool IsMultiSelection => _selection.Count > 1;

	/// <summary>The selected brushes themselves, in stack order (out-of-range entries skipped).</summary>
	public IEnumerable<SdfBrush> SelectedBrushes
	{
		get
		{
			var b = Target.IsValid() ? Target.Brushes : null;
			if ( b is null )
				yield break;

			foreach ( var i in _selection )
			{
				if ( i >= 0 && i < b.Count )
					yield return b[i];
			}
		}
	}

	/// <summary>The brushes a HUD property edit (palette click, colour wheel, blend/round sliders, symmetry
	/// chips…) applies to: the stamp ghost in sculpt mode, otherwise EVERY selected brush. Displayed values
	/// still come from <see cref="ActiveBrush"/> (the primary), like any multi-object inspector.</summary>
	public IEnumerable<SdfBrush> EditTargets
	{
		get
		{
			if ( Tool == SculptTool.Sculpt && IsEditing )
			{
				if ( StampBrush is { } ghost )
					yield return ghost;
				yield break;
			}

			foreach ( var b in SelectedBrushes )
				yield return b;
		}
	}

	/// <summary>Ctrl is held (sampled once per frame in component context, where Input reads reliably —
	/// UI event handlers read this instead of Input directly). The multi-select toggle modifier.</summary>
	public static bool CtrlHeld { get; private set; }

	/// <summary>Shift is held — the range-select modifier on layer rows (everywhere else it stays the snap
	/// hold, exactly as before; the two never collide because selection is a click and snap is a drag).</summary>
	public static bool ShiftHeld { get; private set; }

	readonly RuntimeBrushGizmo _gizmo = new();
	float _gizmoAlpha;        // current gizmo opacity, eased toward 1 (selected) / 0 (not) for the fade
	SdfBrush _gizmoBrush;     // last selected brush, kept so the gizmo can keep drawing while it fades out
	bool _splineInsertArmed;  // spline add-point is armed only after the cursor sits on the line with Attack1 released,
	                          // so the click that SELECTS a spline can't also insert (one click select, one to add)

	// ── Multi-selection pivot proxy ──────────────────────────────────────────────────────────────────
	// With 2+ brushes selected the gizmo (and the W/R/E/S/D scrubs) never touch a real brush: they drive
	// this synthetic one — pivot at the selection's average centre, OBJECT-SPACE axes (its rotation resets
	// to identity whenever no gesture is running) — and the per-frame delta fans out to every selected
	// brush: positions orbit the pivot, rotations compose, a UNIFORM scale scales sizes/positions/spline
	// radii together, and a single-axis scale stretches the ARRANGEMENT along that object axis (a rotated
	// brush's own size can't scale along an arbitrary world axis, so per-brush sizes sit that one out).
	// Never a member of Target.Brushes, so no commit path can ever bake it.
	readonly SdfBrush _multiProxy = new() { Shape = SdfShape.Box, Size = new Vector3( 50f ) };
	Vector3 _proxyPrePos;
	Rotation _proxyPreRot = Rotation.Identity;
	Vector3 _proxyPreSize = new( 50f );
	float _proxyPreBlend, _proxyPreRound, _proxyPreSlice;

	// Re-seat the proxy on the selection while nothing is mid-gesture (a drag/scrub owns it then): pivot =
	// average centre of every VISIBLE shape — mirror copies included, so a mirrored pair contributes its
	// on-plane midpoint (a face's eye pair pulls the pivot between the eyes, not onto the authored eye) —
	// axes = the sculpture's own (identity), scale baseline reset.
	void RefreshMultiProxy()
	{
		if ( _gizmo.IsDragging || _gizmo.IsUiDragging || IsScrubbing )
			return;

		_multiProxy.Position = SelectionPivot();
		_multiProxy.Rotation = Rotation.Identity; // object-space axes, always — the pivot never keeps a tilt
		_multiProxy.Size = new Vector3( 50f );    // arbitrary baseline — the scale handles apply the RATIO

		// Seed the property scrubs (S/D/F) from the primary, so a hold starts at its current value.
		if ( SelectedBrush is { } p )
		{
			_multiProxy.Blend = p.Blend;
			_multiProxy.Rounding = p.Rounding;
			_multiProxy.Slice = p.Slice;
		}
	}

	// The selection's pivot in sculpture space: the average centre of every VISIBLE shape, mirror copies
	// included — so a mirrored pair contributes its on-plane midpoint and a face's eye pair pulls the pivot
	// between the eyes rather than onto the authored one.
	Vector3 SelectionPivot()
	{
		Vector3 sum = Vector3.Zero;
		int n = 0;
		Span<Vector3> mirrorCentres = stackalloc Vector3[8];
		foreach ( var b in SelectedBrushes )
		{
			if ( b.Shape == SdfShape.Spline && b.Points is { Count: > 0 } )
			{
				// MirrorCentres reads the (inert) transform — reflect the curve's own centre instead.
				var c = SplineCentre( b );
				int nx = b.EffectiveMirrorX ? 1 : 0, ny = b.EffectiveMirrorY ? 1 : 0, nz = b.EffectiveMirrorZ ? 1 : 0;
				for ( int sx = 0; sx <= nx; sx++ )
				for ( int sy = 0; sy <= ny; sy++ )
				for ( int sz = 0; sz <= nz; sz++ )
				{
					sum += new Vector3( sx == 1 ? -c.x : c.x, sy == 1 ? -c.y : c.y, sz == 1 ? -c.z : c.z );
					n++;
				}
			}
			else
			{
				int count = b.MirrorCentres( mirrorCentres );
				for ( int i = 0; i < count; i++ )
				{
					sum += mirrorCentres[i];
					n++;
				}
			}
		}

		return n > 0 ? sum / n : Vector3.Zero;
	}

	// Snapshot the proxy just before the gizmo/scrubs run, so ApplyMultiDelta can diff one frame's change.
	void SnapshotMultiProxy()
	{
		_proxyPrePos = _multiProxy.Position;
		_proxyPreRot = _multiProxy.Rotation;
		_proxyPreSize = _multiProxy.Size;
		_proxyPreBlend = _multiProxy.Blend;
		_proxyPreRound = _multiProxy.Rounding;
		_proxyPreSlice = _multiProxy.Slice;
	}

	// Fan one frame's proxy delta out to the whole selection: rigid orbit about the pivot, optional scale,
	// and absolute property sync for the scrubs (what a shared slider drag does too). Shift-snap already
	// happened on the proxy, so the group snaps as one — relative offsets survive, like the editors do it.
	void ApplyMultiDelta()
	{
		var pivot = _proxyPrePos;
		var dPos = _multiProxy.Position - _proxyPrePos;
		var dq = _multiProxy.Rotation * _proxyPreRot.Inverse;
		var f = new Vector3(
			_multiProxy.Size.x / MathF.Max( _proxyPreSize.x, 1e-3f ),
			_multiProxy.Size.y / MathF.Max( _proxyPreSize.y, 1e-3f ),
			_multiProxy.Size.z / MathF.Max( _proxyPreSize.z, 1e-3f ) );

		bool moved = dPos != Vector3.Zero;
		bool rotated = dq != Rotation.Identity;
		bool scaled = f != Vector3.One;
		bool uniform = scaled && MathF.Abs( f.x - f.y ) < 1e-4f && MathF.Abs( f.y - f.z ) < 1e-4f;

		if ( moved || rotated || scaled )
		{
			foreach ( var b in SelectedBrushes )
			{
				// SCALING a mirrored brush measures its mirrored axes from the brush's own mirror planes,
				// not the group pivot: the visible pair's centroid sits ON the plane, so this is the only
				// pivot that scales the pair's spread proportionally — the group pivot would creep pairs
				// inward/outward whenever it sits off-plane (the shrinking-eyes bug). ROTATION keeps the
				// group pivot untouched: the authored half must stay rigid with the rest of the selection
				// (its derived copies mirror whatever it does — and about the plane's NORMAL axis that
				// reflection provably moves the pair as one, any pivot). The gizmo runs one handle at a
				// time, so scale and rotate never land in the same frame.
				// Raw toggles, not Effective*: a brush scaling across the deadzone must not flip pivots mid-drag.
				var bp = pivot;
				if ( scaled && !rotated )
				{
					if ( b.MirrorX ) bp.x = 0f;
					if ( b.MirrorY ) bp.y = 0f;
					if ( b.MirrorZ ) bp.z = 0f;
				}

				if ( b.Shape == SdfShape.Spline && b.Points is { Count: > 0 } pts )
				{
					// A spline's transform is inert — carry its control points through the same orbit.
					for ( int i = 0; i < pts.Count; i++ )
					{
						var rel = new Vector3( pts[i].x, pts[i].y, pts[i].z ) - bp;
						if ( rotated ) rel = dq * rel;
						if ( scaled ) rel *= f;
						var np = bp + rel + dPos;
						float r = uniform
							? Math.Clamp( pts[i].w * f.x, 1f, SdfBrush.MaxSplineRadius )
							: pts[i].w;
						pts[i] = new Vector4( np.x, np.y, np.z, r );
					}
				}
				else
				{
					var rel = b.Position - bp;
					if ( rotated ) rel = dq * rel;
					if ( scaled ) rel *= f;
					b.Position = bp + rel + dPos;
					if ( rotated )
						b.Rotation = dq * b.Rotation;
					if ( uniform )
					{
						float floor = b.Shape == SdfShape.Text ? 0.6f : 1f; // the gizmo's own scale floor
						b.Size = Vector3.Max( b.Size * f.x, new Vector3( floor ) );
						b.Rounding = Math.Clamp( b.Rounding * f.x, SdfBrush.MinRounding, b.MaxRounding() );
					}
				}
			}
		}

		// Property scrubs ran on the proxy — sync the ABSOLUTE value across, clamped per brush.
		if ( _multiProxy.Blend != _proxyPreBlend )
		{
			foreach ( var b in SelectedBrushes )
				b.Blend = Math.Clamp( _multiProxy.Blend, 0f, SdfBrush.MaxBlend );
		}
		if ( _multiProxy.Rounding != _proxyPreRound )
		{
			foreach ( var b in SelectedBrushes )
				b.Rounding = Math.Clamp( _multiProxy.Rounding, SdfBrush.MinRounding, b.MaxRounding() );
		}
		if ( _multiProxy.Slice != _proxyPreSlice )
		{
			foreach ( var b in SelectedBrushes )
			{
				if ( b.Shape is SdfShape.Sphere or SdfShape.Cone )
					b.Slice = Math.Clamp( _multiProxy.Slice, 0f, SdfBrush.MaxSlice );
			}
		}
	}

	// Shift one brush by a sculpture-local offset (a spline's geometry lives in its points).
	static void OffsetBrush( SdfBrush b, Vector3 offset )
	{
		if ( b.Shape == SdfShape.Spline && b.Points is { } opts )
		{
			for ( int i = 0; i < opts.Count; i++ )
				opts[i] = new Vector4( opts[i].x + offset.x, opts[i].y + offset.y, opts[i].z + offset.z, opts[i].w );
		}
		else
		{
			b.Position += offset;
		}
	}
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
	readonly List<int> _stampWireSel = new(); // which _stampWireList entries draw at selected opacity
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

	/// <summary>The player-facing toggle (the pawn's Edit action). Entering is unconditional; LEAVING
	/// routes through <see cref="RequestExit"/> so an incompatible sculpt raises the revert confirmation
	/// instead of exiting silently. Forced teardowns (round retiring the pawn, ownership loss) call
	/// <see cref="SetActive"/> directly — those can't wait on a dialog, so they revert without one.</summary>
	public void Toggle()
	{
		if ( IsEditing )
			RequestExit();
		else
			SetActive( true );
	}

	// ── Exit confirmation (SculptBounds revert) ──────────────────────────────────────────────────────
	// Leaving edit mode must never leave the owner wearing a shape the other machines aren't showing —
	// the network layer only ever broadcast VALID commits, so an invalid exit would be a permanent local/
	// remote mismatch. The player-driven exit therefore confirms first ("this will revert"), and every
	// deactivation — confirmed or forced — restores the bounds' last valid shape on its way out.

	/// <summary>A player-driven exit is waiting on the revert confirmation (the HUD shows the dialog).</summary>
	public bool ExitConfirmPending { get; private set; }

	/// <summary>Which violation the pending confirmation is about (drives the dialog's message).</summary>
	public bool ExitConfirmTooBig { get; private set; }

	/// <summary>Leave edit mode, confirming first when the sculpt is invalid and a revert would actually
	/// change it. Invalid with nothing to revert TO (never had a valid commit) exits directly — there's no
	/// mismatch a dialog could warn about, since proxies were never shown anything.</summary>
	public void RequestExit()
	{
		if ( !IsEditing )
			return;

		var bounds = Bounds;
		if ( bounds.IsValid() && !bounds.EvaluateNow() && bounds.LastValidAuthored is not null )
		{
			ExitConfirmPending = true;
			ExitConfirmTooBig = bounds.TooBig;
			return;
		}

		SetActive( false );
	}

	/// <summary>The dialog's "revert and leave": the actual revert runs inside <see cref="SetActive"/>.</summary>
	public void ConfirmExit()
	{
		ExitConfirmPending = false;
		SetActive( false );
	}

	/// <summary>The dialog's "keep editing".</summary>
	public void CancelExit() => ExitConfirmPending = false;

	SculptBounds Bounds => Target.IsValid() ? Target.GameObject.Components.Get<SculptBounds>() : null;

	// Restore the bounds' last valid authored shape, spliced onto the LIVE damage tail (the same rule as
	// ApplyUndoState), and push it through the commit funnel — the revert IS an edit as far as the remesh,
	// collider, network publish and persist slot are concerned. Runs with IsEditing already false, so
	// NotifyChanged records no undo step (the history is being dropped anyway).
	void RevertIfInvalid()
	{
		var bounds = Bounds;
		if ( !bounds.IsValid() || !Target.IsValid() || bounds.EvaluateNow() )
			return;

		var restored = SdfSculpture.DeserializeBrushes( bounds.LastValidAuthored );
		if ( restored is null )
			return; // never had a valid shape — nothing was ever shown to revert to

		Deselect(); // the restored list may be shorter than the selection index

		var live = Target.Brushes;
		int liveAuthored = Target.AuthoredBrushCount;
		var spliced = new List<SdfBrush>( restored.Count + (live.Count - liveAuthored) );
		spliced.AddRange( restored );
		for ( int i = liveAuthored; i < live.Count; i++ )
			spliced.Add( live[i] );

		Target.Brushes = spliced;
		NotifyChanged();
	}

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
			ExitConfirmPending = false; // whatever exit path ran, no dialog survives it
			CancelStamp(); // strip the pending stamp ghost FIRST, so no exit commit can bake it
			RevertIfInvalid(); // never walk away wearing a shape the other machines aren't showing
			if ( _pendingCommit )
				CommitChanged(); // never leave a previewed-but-uncommitted edit behind on exit (saves the slot too)
			UnhookPersistSlot();
			_undo.Clear(); // history is per-session — the next one re-baselines on whatever it opens on
			_worldClamp.Dispose(); // scratch physics body + watch state die with the session
			_gizmo.Hide();
			HideGhosts();
			// No DoF restore needed: we just stop asserting, and MainCamera eases back to its baseline.
			if ( _proxyDebugActive )
				ExitProxyDebug();
		}

		// Optional: when this session owns a camera (creative mode), hand it over while editing. The hider
		// leaves OrbitCamera null and keeps its own always-on orbit camera instead.
		if ( OrbitCamera.IsValid() )
		{
			if ( active )
				OrbitCamera.FocusHint = FocusTarget().Point;
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
		_worldClamp.Dispose();
		_gizmo.Hide();
		HideGhosts();
		_wireframes.Hide();
		_wireAlpha = 0f;
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

	/// <summary>Convert every selected brush to a different primitive IN PLACE — position, size, material,
	/// blend and symmetry all carry over; only the shape swaps (shape is a brush property, like colour).
	/// Converting TO text bakes the glyph field and adopts the glyph slot's fixed 2:1 aspect from the
	/// current height (anything else stretches the letters). One commit for the whole group.</summary>
	public void ConvertSelectedShape( SdfShape shape )
	{
		bool changed = false;
		foreach ( var sel in SelectedBrushes )
			changed |= ConvertBrushShape( sel, shape );
		if ( changed )
			NotifyChanged();
	}

	// One brush's share of the shape conversion (no commit — the wrapper above runs one for the group).
	bool ConvertBrushShape( SdfBrush b, SdfShape shape )
	{
		if ( b is null || b.Shape == shape )
			return false;

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
			return true;
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
		return true;
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

	/// <summary>Modifier-aware selection — the s&amp;box hierarchy scheme, shared by layer-row clicks and
	/// world picks: plain = just this brush; ctrl = toggle it in/out of the group; shift = select the whole
	/// range between the anchor (the last plain/ctrl click) and this row, keeping the anchor put so the next
	/// shift-click re-pivots off the same row. Ctrl/shift on an out-of-range index leave the selection alone
	/// (a plain one clears it, matching the empty-space click).</summary>
	public void Select( int index, bool ctrl, bool shift )
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		int authored = Target.IsValid() ? Target.AuthoredBrushCount : 0;

		if ( b is not { Count: > 0 } || index < 0 || index >= authored )
		{
			if ( !ctrl && !shift )
				Selected = -1;
			return;
		}

		if ( shift )
		{
			int anchor = Math.Clamp( _anchor >= 0 ? _anchor : index, 0, authored - 1 );
			int lo = Math.Min( anchor, index ), hi = Math.Max( anchor, index );
			_selection.Clear();
			for ( int i = lo; i <= hi; i++ )
				_selection.Add( i );
			_selected = index;
			_anchor = anchor;
			return;
		}

		if ( ctrl )
		{
			int at = _selection.BinarySearch( index );
			if ( at >= 0 )
			{
				// Toggle OFF. The primary hops to the nearest remaining member (or nothing is left).
				_selection.RemoveAt( at );
				if ( _selection.Count == 0 )
				{
					Selected = -1;
					return;
				}
				if ( _selected == index )
					_selected = _selection[Math.Min( at, _selection.Count - 1 )];
				_anchor = _selected;
			}
			else
			{
				_selection.Insert( ~at, index );
				_selected = index;
				_anchor = index;
			}
			return;
		}

		Selected = index; // collapses the group to this row + re-seats the anchor
	}

	// Keep every selected index inside the authored prefix (undo/load/shrink can shorten the list under
	// it), and the primary a member of the set. The multi-select equivalent of the old bounds check.
	void PruneSelection()
	{
		int authored = Target.IsValid() ? Target.AuthoredBrushCount : 0;
		for ( int k = _selection.Count - 1; k >= 0; k-- ) // manual: this runs per frame, no closure alloc
		{
			if ( _selection[k] < 0 || _selection[k] >= authored )
				_selection.RemoveAt( k );
		}
		if ( _selection.Count == 0 )
		{
			_selected = -1;
			_anchor = -1;
			return;
		}

		if ( !_selection.Contains( _selected ) )
			_selected = _selection[^1];
		if ( _anchor >= authored )
			_anchor = _selected;
	}

	// Re-point the selection at the same brushes after an index shuffle (the layer-list reorder).
	void RemapSelection( List<SdfBrush> selectedRefs, SdfBrush primary, List<SdfBrush> list )
	{
		_selection.Clear();
		foreach ( var r in selectedRefs )
		{
			int ni = list.IndexOf( r );
			if ( ni >= 0 && !_selection.Contains( ni ) )
				_selection.Add( ni );
		}
		_selection.Sort();

		int np = primary is not null ? list.IndexOf( primary ) : -1;
		_selected = np >= 0 ? np : (_selection.Count > 0 ? _selection[^1] : -1);
		if ( _selected >= 0 && !_selection.Contains( _selected ) )
		{
			_selection.Add( _selected );
			_selection.Sort();
		}
		_anchor = _selected;
	}

	/// <summary>Clear the selection (gizmo/palette/sliders hide).</summary>
	public void Deselect() => Selected = -1;

	/// <summary>Remove every selected brush (keeps at least one AUTHORED brush — damage craters don't count
	/// toward that minimum — and never the last SOLID one, see <see cref="RefuseIfLastSolid"/>) and rebuild.
	/// Leaves nothing selected.</summary>
	public void RemoveSelected()
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( Selected < 0 || b is not { Count: > 1 } )
			return;

		var doomed = new List<SdfBrush>( SelectedBrushes );
		if ( doomed.Count == 0 )
			return;

		if ( Target.AuthoredBrushCount - doomed.Count < 1 )
		{
			SinceNoSolidRefusal = 0f; // same "must have at least 1 shape" story as the solids guard
			return;
		}

		if ( RefuseIfLosingSolids( doomed ) )
			return;

		b.RemoveAll( doomed.Contains );
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

	/// <summary>Set to 0 whenever a layer action is refused because it would leave the sculpt with no solid
	/// clay (see <see cref="RefuseIfLastSolid"/>) — <see cref="EditHud"/> shows its transient "must have at
	/// least 1 shape" toast off this.</summary>
	public RealTimeSince SinceNoSolidRefusal { get; private set; } = 999f;

	// Would losing `b` (deleting it, hiding it, or flipping it to carve) leave the sculpt with no enabled
	// additive brush? No solid clay means no mesh AND no collider — a prop pawn would fall straight through
	// the floor — so the layer actions REFUSE the edit up front (returning true and firing the toast)
	// rather than ever allowing the state. Self-filtering: a brush that isn't itself solid can't be the
	// last solid, so callers guard with a bare call, no pre-checks. The pending stamp ghost never counts
	// as the remaining solid — it's cancelled on every exit path.
	bool RefuseIfLastSolid( SdfBrush b )
	{
		if ( b is null || !b.Enabled || b.Operation != SdfOperation.Add )
			return false; // b isn't solid itself — losing it can't empty the sculpt

		var list = Target.IsValid() ? Target.Brushes : null;
		if ( list is null )
			return false;

		var ghost = PendingStamp( Target );
		int authored = Target.AuthoredBrushCount;
		for ( int i = 0; i < Math.Min( authored, list.Count ); i++ )
		{
			var o = list[i];
			if ( o != b && o != ghost && o.Enabled && o.Operation == SdfOperation.Add )
				return false; // another solid brush remains — the edit is safe
		}

		SinceNoSolidRefusal = 0f;
		return true;
	}

	// The GROUP form of RefuseIfLastSolid: would losing ALL of `losing` at once (deleting, hiding or
	// carving the whole selection) leave no enabled additive brush? Same toast, same self-filtering — a
	// group with no solid in it can't empty the sculpt, so it always passes.
	bool RefuseIfLosingSolids( IReadOnlyList<SdfBrush> losing )
	{
		bool losesSolid = false;
		foreach ( var l in losing )
			losesSolid |= l is { Enabled: true, Operation: SdfOperation.Add };
		if ( !losesSolid )
			return false;

		var list = Target.IsValid() ? Target.Brushes : null;
		if ( list is null )
			return false;

		var ghost = PendingStamp( Target );
		int authored = Target.AuthoredBrushCount;
		for ( int i = 0; i < Math.Min( authored, list.Count ); i++ )
		{
			var o = list[i];
			if ( o != ghost && o.Enabled && o.Operation == SdfOperation.Add && !losing.Contains( o ) )
				return false; // a solid outside the group survives — the edit is safe
		}

		SinceNoSolidRefusal = 0f;
		return true;
	}

	/// <summary>Toggle a brush's visibility (the eye button) and rebuild. On a multi-selected row the eye
	/// drives the WHOLE selection to one state (the clicked row's flip). Refused (with the toast) when
	/// hiding would leave no solid shape.</summary>
	public void ToggleEnabled( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		if ( IsMultiSelection && IsSelected( index ) )
		{
			bool on = !b.Enabled;
			var group = new List<SdfBrush>( SelectedBrushes );
			if ( !on && RefuseIfLosingSolids( group ) )
				return;

			foreach ( var s in group )
				s.Enabled = on;
			NotifyChanged();
			return;
		}

		if ( RefuseIfLastSolid( b ) )
			return;

		b.Enabled = !b.Enabled;
		NotifyChanged();
	}

	/// <summary>Cycle a brush's operation (the layer-row op button): Add → Carve → Cutout → Add. On a
	/// multi-selected row the whole selection lands on ONE op — the clicked row's next. Refused (with the
	/// toast) when leaving Add would strip the last solid shape — Carve and Cutout both add no geometry,
	/// so neither can be the sculpt's remaining solid.</summary>
	public void ToggleOperation( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		if ( IsMultiSelection && IsSelected( index ) )
		{
			var op = NextOperation( b.Operation );
			var group = new List<SdfBrush>( SelectedBrushes );
			if ( op != SdfOperation.Add && RefuseIfLosingSolids( group ) )
				return;

			foreach ( var s in group )
				s.Operation = op;
			NotifyChanged();
			return;
		}

		if ( RefuseIfLastSolid( b ) )
			return;

		b.Operation = NextOperation( b.Operation );
		NotifyChanged();
	}

	/// <summary>The op cycle every add/carve toggle steps through (A key, op chip, layer-row button):
	/// Add → Subtract (carve) → Cutout → back to Add.</summary>
	public static SdfOperation NextOperation( SdfOperation op ) => op switch
	{
		SdfOperation.Add => SdfOperation.Subtract,
		SdfOperation.Subtract => SdfOperation.Cutout,
		_ => SdfOperation.Add,
	};

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
	/// restores the combo it had when last toggled off (left/right X for a brush with no history). On a
	/// multi-selected row the clicked row picks the DIRECTION and the whole selection follows it.
	/// Per-axis control still lives in the Symmetry section.</summary>
	public void ToggleSymmetry( int index )
	{
		if ( BrushAt( index ) is not { } b )
			return;

		bool turnOn = !(b.MirrorX || b.MirrorY || b.MirrorZ);
		bool changed;
		if ( IsMultiSelection && IsSelected( index ) )
		{
			changed = false;
			foreach ( var s in SelectedBrushes )
				changed |= ApplySymmetryToggle( s, turnOn );
		}
		else
		{
			changed = ApplySymmetryToggle( b, turnOn );
		}

		if ( changed )
			NotifyChanged();
	}

	// One brush's share of the symmetry button: off stores the combo for the trip back; on restores it (X
	// for a brush with no history). Already in the target state = untouched, so a mixed group converges
	// without scrambling anyone's remembered axes.
	bool ApplySymmetryToggle( SdfBrush b, bool on )
	{
		bool anyOn = b.MirrorX || b.MirrorY || b.MirrorZ;
		if ( on == anyOn )
			return false;

		if ( !on )
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
		return true;
	}

	/// <summary>Delete a specific brush (the bin button), keeping at least one — and never the last SOLID
	/// one (see <see cref="RefuseIfLastSolid"/>) — and rebuild. The bin on a row that's part of a
	/// multi-selection deletes the whole selection (hierarchy-style).</summary>
	public void Remove( int index )
	{
		if ( IsMultiSelection && IsSelected( index ) )
		{
			RemoveSelected();
			return;
		}

		var b = Target.IsValid() ? Target.Brushes : null;
		if ( b is not { Count: > 1 } || index < 0 || index >= b.Count || RefuseIfLastSolid( b[index] ) )
			return;

		b.RemoveAt( index );

		// Keep the same brushes selected (indices above the removal shift down one); drop the removed row.
		_selection.Remove( index );
		for ( int k = 0; k < _selection.Count; k++ )
		{
			if ( _selection[k] > index )
				_selection[k]--;
		}
		if ( _selected == index ) _selected = -1;
		else if ( _selected > index ) _selected--;
		if ( _selected < 0 && _selection.Count > 0 ) _selected = _selection[^1];
		if ( _selected < 0 ) _selection.Clear();
		_anchor = _selected;

		NotifyChanged();
	}

	/// <summary>Duplicate the selection: an independent copy of every selected brush, inserted as a block
	/// just above the topmost selected row — each copy exactly in place — then select the copies and
	/// rebuild. The clones carry every property (shape, transform, size, material and symmetry), so they
	/// sit on top of the originals until you drag them off. Refused past the brush cap.</summary>
	public void DuplicateSelected()
	{
		var b = Target.IsValid() ? Target.Brushes : null;
		if ( Selected < 0 || b is not { Count: > 0 } )
			return;

		var src = new List<int>();
		foreach ( var i in _selection )
		{
			if ( i >= 0 && i < b.Count )
				src.Add( i );
		}
		if ( src.Count == 0 || b.Count + src.Count > SdfBrushPacker.MaxBrushes )
			return;

		int insert = src[^1] + 1; // just above the topmost selected — still below the damage tail
		for ( int k = 0; k < src.Count; k++ )
			b.Insert( insert + k, b[src[k]].Copy() );

		// The copies become the selection (their block), the last one the primary — the single-brush case
		// lands exactly where it always did (copy right above, selected).
		_selection.Clear();
		for ( int k = 0; k < src.Count; k++ )
			_selection.Add( insert + k );
		_selected = _selection[^1];
		_anchor = _selected;
		NotifyChanged();
	}

	/// <summary>Reset every selected brush's rotation to its shape's spawn orientation (the "Rotate" tool
	/// button) and rebuild — identity for most shapes, face-forward for the flat-profile ones.</summary>
	public void ResetRotationSelected()
	{
		bool changed = false;
		foreach ( var b in SelectedBrushes )
		{
			b.Rotation = SdfSculpture.SpawnRotation( b.Shape );
			changed = true;
		}

		if ( changed )
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

		// Remember the selected brushes so we can keep THEM selected after the indices shuffle.
		var selectedBrush = (Selected >= 0 && Selected < b.Count) ? b[Selected] : null;
		var selectedRefs = new List<SdfBrush>( _selection.Count );
		foreach ( var i in _selection )
		{
			if ( i >= 0 && i < b.Count )
				selectedRefs.Add( b[i] );
		}

		var item = b[from];
		b.RemoveAt( from );
		b.Insert( to, item );

		// Re-point the selection at the same brushes (their indices may have shifted).
		RemapSelection( selectedRefs, selectedBrush, b );

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

		// BEFORE the undo record: a blocked edit reverts here, so the clamped state is what becomes the
		// step, the mesh, the network snapshot and the persist save.
		ClampActiveBrush();

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

	// ── World clamp ──────────────────────────────────────────────────────────────────────────────────
	// The active brush's own collision against the map: an additive brush can't be dragged/grown through
	// walls or floors, and because it's OUR test (not real physics) the contact never pushes the prop. See
	// BrushWorldClamp for the whole story. Fed from the preview/commit funnel + the two direct shadow-proxy
	// sites, so every continuous edit path passes through it; discrete bypasses are caught by the
	// commit-time backstop in SdfCollider.Rebuild.
	readonly BrushWorldClamp _worldClamp = new();

	// Clamp this frame's state of the active brush (stamp ghost in Add mode, selection otherwise) BEFORE any
	// rebuild renders/records/streams it. Travel-vs-teleport (the stamp cursor jumping across the scene must
	// not sweep against geometry along the way) is decided inside by the move's distance.
	//
	// A MULTI-selection gesture clamps as a GROUP instead — one shared correction for every member, so the
	// arrangement keeps its formation (see BrushWorldClamp.ApplyGroup). It must not simply be skipped: clay
	// pushed into the world makes SdfCollider.Rebuild refuse the embedded collider, which silently freezes
	// the prop's physics on its last good shape. Each path drops the other's watch state on the way past,
	// so switching between them always re-baselines rather than restoring a stale pose.
	void ClampActiveBrush()
	{
		if ( Tool == SculptTool.Gizmo && IsMultiSelection )
		{
			_clampGroup.Clear();
			foreach ( var b in SelectedBrushes )
				_clampGroup.Add( b );

			// The clamp translates the group rigidly (or reverts it) — carry the gizmo along, or a
			// correction would leave the pivot floating off the shapes it drives.
			var pivotBefore = SelectionPivot();
			_worldClamp.Apply( Target, null );
			_worldClamp.ApplyGroup( Target, _clampGroup );
			_multiProxy.Position += SelectionPivot() - pivotBefore;
			return;
		}

		_worldClamp.ApplyGroup( Target, null );
		_worldClamp.Apply( Target, ActiveBrush );
	}

	// Reusable buffer for the group clamp's member list (no per-frame allocation during a drag).
	readonly List<SdfBrush> _clampGroup = new();

	/// <summary>Chain-placement hook: tube-aware clamp of the spline ghost's live point (see
	/// <see cref="BrushWorldClamp.ClampTubePoint"/>) — its move from <paramref name="fromLocal"/> (last
	/// frame's accepted preview) is restricted so the whole curve stays clear. The stamp tool calls this
	/// while rebuilding the ghost, so the point its chain commits on a click is legal including the
	/// curve's dip into the world.</summary>
	internal void ClampGhostChainPoint( SdfBrush ghost, int idx, Vector3 fromLocal ) =>
		_worldClamp.ClampTubePoint( Target, ghost, idx, fromLocal );

	/// <summary>A HUD control is mid-gesture (slider drag, colour-wheel scrub, text typing): update only the
	/// LIVE surface — the raymarcher reads the brushes directly each frame, and the shadow proxy keeps shadows
	/// plus the 20 Hz network preview stream moving — and defer the expensive commit until the gesture ends.
	/// This is exactly the preview/commit split gizmo handle drags already have; without it, every mouse-move
	/// on a slider was a full commit (remesh + collider + reliable network send) on every machine.</summary>
	public void PreviewChanged()
	{
		if ( !Target.IsValid() )
			return;

		ClampActiveBrush(); // before the proxy build, so the reverted state is what previews + streams

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
			_worldHover = -1;

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
		return Load( SculptLibrary.Load( name ) );
	}

	/// <summary>The same replace-commit-undo funnel for an entry that didn't come off the local disk — the
	/// workshop download path. Validates like <see cref="SculptLibrary.Load"/> does (a workshop file is just
	/// as player-editable as a save on disk): empty or over the brush cap = refused, shape untouched.</summary>
	public bool Load( SculptLibrary.Entry entry )
	{
		if ( !Target.IsValid() )
			return false;

		if ( entry?.Brushes is not { Count: > 0 } || entry.Brushes.Count > SdfBrushPacker.MaxBrushes )
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

		// Already wearing it — the pawn was dressed before this session even existed (see
		// HunterController.WearSavedHead, which runs at clone/OnStart so the prefab default never renders).
		// Rebuilding again here would just re-bake and re-publish the identical shape.
		if ( SdfSculpture.ContentHash( Target.Brushes, Target.Resolution, Target.FlipFaces )
			== SdfSculpture.ContentHash( entry.Brushes, Target.Resolution, Target.FlipFaces ) )
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

		// Only VALID shapes persist (see SculptBounds): an invalid work-in-progress on disk would spawn the
		// next session invalid with no last-valid shape for proxies to fall back to. Skipping the write means
		// worst case a disconnect loses edits the player was being told to fix anyway.
		var bounds = Target.GameObject.Components.Get<SculptBounds>();
		if ( bounds.IsValid() && !bounds.EvaluateNow() )
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
		// Sample the selection modifiers here (component context — Input reads reliably) for the HUD's
		// layer-row clicks, which run inside UI event dispatch where it doesn't.
		CtrlHeld = Sandbox.UI.InputFocus.Current is null && Input.Keyboard.Down( "ctrl" );
		ShiftHeld = SnapHeld;

		UpdateWireframes(); // runs even when not editing so it can fade OUT after exit

		if ( !IsEditing || !Target.IsValid() )
			return;

		UpdateDofFocus(); // keep focus just behind the object as the camera orbits / blur is tuned

		var brushes = Target.Brushes;

		// (Q is NOT handled here: it's the pawn's Edit action — enter/exit EDIT MODE as a whole. Sculpt
		// mode is entered via the Add Shape button / Space / shape tiles / number keys, and left via
		// Space/Esc/stamping.)

		// One shared gate for every discrete editing key below: never while typing in a text field, never
		// while the pause menu is open. (The scrubs, gizmo and stamp tool each carry their own equivalent
		// gate — this covers the one-shot keys, which used to check only the typing half.)
		bool keysLive = Sandbox.UI.InputFocus.Current is null && !PauseMenu.IsOpen;

		// A flips add/carve — the head of the A/S/D/F "edit keys" row, matching the strip's chip order
		// (op · blend · round · wildcard). Stamp's op in sculpt mode, the SELECTED brush's in gizmo mode.
		// Tutorial-gated with the op chip's section (EditHudGate) — the keyboard can never do what the
		// hidden/locked chip can't; same rule for every gated key below.
		bool opKey = keysLive && EditHudGate.Interactive( HudSection.Sliders ) && Input.Keyboard.Down( "a" );
		if ( opKey && !_opKeyWas )
		{
			if ( Tool == SculptTool.Sculpt )
				SetStampOperation( NextOperation( StampOperation ) );
			else if ( Selected >= 0 )
				ToggleOperation( Selected );
		}
		_opKeyWas = opKey;

		// Space mirrors the Tools panel's Add Shape button: pick up a stamp, or — while one is held —
		// put it down and return to plain editing ("Done"). During spline chain placement the stamp tool
		// owns Space instead (it finishes the chain, like Enter) — toggling the tool here would silently
		// drop the half-built chain. Held off during a drag or scrub so the tool can't switch out from
		// under a live gesture. Manual edge detection like the keys below.
		bool addKey = keysLive && EditHudGate.Interactive( HudSection.AddChip ) && Input.Keyboard.Down( "space" )
			&& !IsScrubbing && !_gizmo.IsDragging
			&& !(Tool == SculptTool.Sculpt && ActiveShape == SdfShape.Spline);
		if ( addKey && !_addKeyWas )
			SetTool( Tool == SculptTool.Sculpt ? SculptTool.Gizmo : SculptTool.Sculpt );
		_addKeyWas = addKey;

		// Number keys follow the shape row left-to-right, so the tile order IS the hotkey order.
		if ( keysLive && EditHudGate.Interactive( HudSection.ShapeDock ) )
		{
			if ( Input.Pressed( "Slot1" ) ) HotkeyShape( SdfShape.Sphere );
			if ( Input.Pressed( "Slot2" ) ) HotkeyShape( SdfShape.Box );
			if ( Input.Pressed( "Slot3" ) ) HotkeyShape( SdfShape.Cylinder );
			if ( Input.Pressed( "Slot4" ) ) HotkeyShape( SdfShape.Cone );

			// The advanced shapes follow the dock: the tutorial's first shape lesson trims their tiles,
			// so their hotkeys stand down with them (key and dock must always agree).
			if ( !EditHudGate.BasicShapesOnly )
			{
				if ( Input.Pressed( "Slot5" ) ) HotkeyShape( SdfShape.Extruded );
				if ( Input.Pressed( "Slot6" ) ) HotkeyShape( SdfShape.Spline );
				if ( Input.Pressed( "Slot7" ) ) HotkeyShape( SdfShape.Text );
			}
		}

		// Delete removes the selected brush (gizmo mode only — in sculpt mode the pending ghost isn't a
		// real shape yet, and Esc/right-click is how you drop that). Manual edge detection on Down, like
		// the other keys here: Keyboard.Pressed doesn't fire reliably in this context. Both spellings are
		// asked for since an unknown key name just resolves to invalid (false), never throws.
		bool del = keysLive && EditHudGate.Interactive( HudSection.EditChips )
			&& (Input.Keyboard.Down( "delete" ) || Input.Keyboard.Down( "del" ));
		if ( del && !_delWas && Tool == SculptTool.Gizmo )
			RemoveSelected();
		_delWas = del;
		if ( keysLive && EditHudGate.Interactive( HudSection.EditChips ) && Input.Pressed( "Drop" ) ) RemoveLast();

		// Undo / redo: Ctrl+Z back, Ctrl+Shift+Z or Ctrl+Y forward. Manual edge detection like the keys above
		// (Keyboard.Pressed doesn't fire reliably in this context), and held off whenever a gesture owns the
		// mouse — stepping the shape out from under a live drag or scrub would leave it editing a brush that
		// no longer exists. The InputFocus guard leaves Ctrl+Z to the text field while typing.
		bool undoMod = Sandbox.UI.InputFocus.Current is null && Input.Keyboard.Down( "ctrl" )
			&& !PauseMenu.IsOpen && !IsScrubbing && !_gizmo.IsDragging
			&& EditHudGate.Interactive( HudSection.UndoRedo );
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
		PruneSelection();

		var tx = Target.WorldTransform;

		// Don't let world clicks/picks/gizmo-grabs fall through the HUD (palette or sliders) when over it.
		bool overUi = EditHud.PointerOverUi;

		// ── Sculpt mode: the stamp ghost owns the frame (no selection / gizmo / hover-pick). ─────────────
		if ( Tool == SculptTool.Sculpt )
		{
			SculptToolUpdate( tx, interactive: !overUi && !Input.Down( "Walk" ) && !AltNav.Dragging && !PauseMenu.IsOpen );
			return;
		}

		// The gizmo fades in/out with the selection (matching the HUD palette/sliders). While fading out after a
		// deselect it keeps drawing the LAST brush at falling opacity, then tears down once invisible; it only
		// hovers/grabs while actually selected. ShowGizmo off (view-only) just fades it out and leaves it gone.
		// With 2+ selected the gizmo's subject is the pivot PROXY, not a real brush (see the proxy fields).
		bool multi = IsMultiSelection;
		if ( multi )
		{
			RefreshMultiProxy();
			_gizmoBrush = _multiProxy;
		}
		else if ( Selected >= 0 )
			_gizmoBrush = brushes[Selected]; // remember so the fade-out has something to draw

		float gizmoTarget = (ShowGizmo && Selected >= 0) ? 1f : 0f;
		_gizmoAlpha = MathX.Lerp( _gizmoAlpha, gizmoTarget, 1f - MathF.Exp( -GizmoFadeSpeed * Time.Delta ) );

		bool changed = false;
		if ( multi )
			SnapshotMultiProxy(); // before the gizmo AND the scrubs — ApplyMultiDelta diffs against this

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

		// Hold-key param scrubs on the selection — the same A/S/D/F + W/R/E scheme the stamp tool uses, so
		// both modes share one muscle memory (W move is edit-only — the stamp ghost already rides the
		// cursor). Continuous changes preview; the key release runs the full commit.
		// Gated on IsDragging, NOT IsBusy: merely HOVERING a gizmo handle (which covers most of the shape)
		// must not eat the scrub keys — only an actual handle drag owns the mouse.
		changed |= BrushScrub.Update( multi ? _multiProxy : SelectedBrush, tx, Scene.Camera,
			allow: !overUi && !Input.Down( "Walk" ) && !AltNav.Dragging && !PauseMenu.IsOpen && !_gizmo.IsDragging,
			out bool scrubEnded,
			blendLocked: !multi && Sdf.BlendInert( Target.Brushes, SelectedBrush ),
			allowMove: true );

		// Multi-selection: the gizmo/scrubs moved the PROXY — fan its delta out to every selected brush
		// before anything previews, commits or streams this frame's state.
		if ( multi && changed )
			ApplyMultiDelta();

		if ( scrubEnded )
			CommitChanged();

		// A right-click TAP (a zoom claim that released without travel) clears the selection — the same
		// "step back" gesture that leaves sculpt mode. RightClickConsumed guards the update-order race: if
		// the gizmo deleted a spline control point with this click before AltNav saw the press, the claim
		// query's gizmo-hover state may have been a frame stale — that click was the delete, never a tap.
		if ( AltNav.RmbTapped && !_gizmo.RightClickConsumed )
			Deselect();

		// Scroll = push/pull the SELECTED brush along the view direction — the same size-scaled depth
		// control the stamp ghost has (wheel-up = away). Each notch previews; the debounce backstop below
		// runs the one full commit once the scrolling goes quiet.
		// (Skipped while a gizmo handle is being dragged — the drag owns the wheel there; a spline point
		// drag consumes it to push that single point along the view ray.)
		float pushWheel = Input.MouseWheel.y;
		if ( pushWheel != 0f && !overUi && !Input.Down( "Walk" ) && !AltNav.Dragging && !PauseMenu.IsOpen
			&& BrushScrub.Active == ScrubKind.None && !_gizmo.IsDragging
			&& SelectedBrush is { } pushB && Scene.Camera is { } pushCam )
		{
			// A spline's transform is inert (its geometry lives in the control points), so pushing Position
			// would do nothing visible — shift every point instead, measured from the curve's centre.
			// A multi-selection pushes the whole group by one shared offset, measured from the pivot.
			bool multiPush = IsMultiSelection;
			bool spline = !multiPush && pushB.Shape == SdfShape.Spline && pushB.Points is { Count: > 0 };
			var local = multiPush ? _multiProxy.Position : (spline ? SplineCentre( pushB ) : pushB.Position);
			var world = tx.PointToWorld( local );
			var toCam = world - pushCam.WorldPosition;
			var dir = (tx.Rotation.Inverse * toCam).Normal;
			var offset = dir * pushWheel * _stampTool.AcceleratedDepthStep( DepthSizeRef( pushB ), toCam.Length );

			if ( multiPush )
			{
				_multiProxy.Position += offset; // the pivot rides along, so repeat notches stay anchored
				foreach ( var gb in SelectedBrushes )
					OffsetBrush( gb, offset );
			}
			else if ( spline )
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

		// Hover: the brush under the cursor (skipped while camera-navving, over a gizmo handle, over the UI, or
		// paused — the pause overlay must not highlight shapes behind it; the tutorial gates it with selection,
		// so pre-selection stages don't tease a hover ghost the click won't honour).
		int hover = (!overUi && !Input.Down( "Walk" ) && !AltNav.Dragging && !PauseMenu.IsOpen && !_gizmo.IsBusy && !IsScrubbing
			&& EditHudGate.Interactive( HudSection.WorldSelect ))
			? PickBrush( tx ) : -1;
		_worldHover = hover; // scene pick only — recorded BEFORE the layer-row fallback (see the field)

		// Fall back to the brush the HUD's layer list is hovering, so mousing a row highlights its shape.
		// The scene pick wins when valid; this only fills in when the cursor isn't over a 3D brush.
		if ( hover < 0 && UiHoverBrush >= 0 && UiHoverBrush < brushes.Count )
			hover = UiHoverBrush;

		_hoverBrush = hover; // for the wireframe's hover highlight

		// Ghost the hovered brush (unless it's already selected — that shows the gizmo/wires instead). Hovering straight
		// from one shape to another cross-fades: the brush you left slides to the outgoing slot and fades out while
		// the new one fades in. Both keep drawing while they fade, then tear down once invisible.
		int ghostHover = (hover >= 0 && !IsSelected( hover )) ? hover : -1;
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

		// A selected CARVE or CUTOUT brush has no surface of its own (a hole / a scored recolour) — draw
		// its wireframe so you can see what you're moving. Covers every such brush in the selection.
		_stampWireList.Clear();
		_stampWireSel.Clear();
		foreach ( var sel in SelectedBrushes )
		{
			if ( sel.Operation is SdfOperation.Subtract or SdfOperation.Cutout )
			{
				_stampWireSel.Add( _stampWireList.Count );
				_stampWireList.Add( sel );
			}
		}
		if ( _stampWireList.Count > 0 )
		{
			_stampWire.Draw( _stampWireList, tx, Scene, Scene.Camera,
				selected: 0, hovered: -1, masterAlpha: 0.9f, Style.OutlineThickness, WireframeDepthBias,
				selectedMulti: _stampWireSel );
		}
		else
		{
			_stampWire.Hide();
		}

		// Selection decides on the RELEASE, not the press: a clean LMB tap (AltNav's click-vs-drag window
		// never crossed the travel threshold) selects the hovered shape, or clears the selection on empty
		// space. A press that grew into a drag became the ORBIT instead — so you can start an orbit anywhere,
		// including on top of a shape, and a clean click on that same shape still picks it. The tap can only
		// exist if the press already landed clear of the HUD/gizmo/scrub; the gates here re-check the ones
		// that can shift inside the window (and alt, whose own click has never meant select).
		if ( AltNav.LmbTapped && !overUi && !Input.Down( "Walk" ) && !PauseMenu.IsOpen
			&& !_gizmo.IsBusy && !IsScrubbing
			&& EditHudGate.Interactive( HudSection.WorldSelect ) ) // tutorial: no picking before the selection stage
		{
			if ( CtrlHeld || ShiftHeld )
				Select( hover, ctrl: true, shift: false ); // additive toggle (a scene has no row order to range over)
			else
				Selected = hover; // hover is -1 on empty space → deselect
		}

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
		// (Single-selection only — magnetizing members of a group drag would pop them out of formation.)
		if ( changed && !multi )
			SelectedBrush?.SnapToMirrorPlanes();

		if ( changed )
		{
			// A gizmo/scrub drag mutates the brush directly (it doesn't go through PreviewChanged) — clamp
			// here, before the proxy build, so a blocked drag reverts before anything renders or streams it.
			ClampActiveBrush();

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

	// Faint translucent overlay tint — cyan additive, red subtractive, blue cutout (matches the wires).
	static Color HoverColor( SdfBrush b ) => (b.Operation switch
	{
		SdfOperation.Subtract => Color.Red,
		SdfOperation.Cutout => Color.Blue,
		_ => Color.Cyan,
	}).WithAlpha( 0.2f );

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
		_worldHover = -1;
		_ghostOut.Hide();
		_ghostOutBrush = null;
		_ghostOutAlpha = 0f;

		_ghost.Hide();
		_ghostBrush = null;
		_ghostAlpha = 0f;

		bool changed = _stampTool.Update( Target, Scene, interactive, out bool committed );

		// No overlay for an additive stamp — the live surface IS the preview. Carve and cutout stamps are
		// the exception: hovering empty space they change nothing, so they'd be invisible — give them the
		// editor wireframe (red carve / blue cutout, from BrushWireframes' op colour).
		var stamp = StampBrush;
		if ( stamp is not null && stamp.Operation != SdfOperation.Add )
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
			// The commit frame's placement mutated the brush AFTER the last preview clamp ran, and by now
			// it isn't the active stamp any more (Stamp is null), so NotifyChanged's clamp can't see it —
			// clamp the placed brush explicitly or what LANDS can differ from what the preview showed
			// (the stamp-under-the-floor exploit). Same instance the clamp was watching as the ghost, so
			// its last-clear snapshot (and the revert fallback) carry straight across.
			_worldClamp.Apply( Target, _stampTool.LastCommitted );

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
			ClampActiveBrush(); // the stamp ghost mutates directly too — never let it rest inside a wall
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

	// ── AltNav claim queries ─────────────────────────────────────────────────────────────────────────
	// AltNav asks these on a bare mouse press edge: true = "this press is an EDIT interaction here", so the
	// camera gesture (LMB orbit / RMB zoom) must stand down. Answered from live state on the press edge so
	// the verdict can't disagree with what this session does with the same press, whichever component
	// happens to update first.

	/// <summary>Would a bare left-press at the current cursor start an edit DRAG gesture of its own (gizmo
	/// handle grab, spline add-point)? False = the press enters AltNav's click-vs-drag window: a drag becomes
	/// the orbit, a clean click comes back as <see cref="AltNav.LmbTapped"/> — which runs selection in gizmo
	/// mode and PLACES THE STAMP in add mode (the stamp rides the tap, not the press, so orbiting works the
	/// same in both tools). Deliberately NOT a hover/pick test — a press on top of a shape must still be able
	/// to grow into an orbit (a room-sized invisible carve volume must not wall off the camera).</summary>
	internal bool LmbPressIsEdit()
	{
		return IsScrubbing || _gizmo.IsBusy || HoveringSplineLine;
	}

	/// <summary>Would a bare right-press start an edit interaction? Only a press on the gizmo itself (a spline
	/// point delete) or a running scrub owns RMB now — scale scrubbing moved to the R key, so right-DRAG is
	/// the camera zoom everywhere (selection, stamp, or nothing), and a right-click TAP comes back as
	/// <see cref="AltNav.RmbTapped"/>: the step-back gesture (deselect / drop the pending stamp).</summary>
	internal bool RmbPressIsEdit()
	{
		return IsScrubbing || _gizmo.IsBusy;
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

	/// <summary>Is the Tab wireframe overlay switched on? The preference alone — what actually renders also
	/// needs <see cref="IsEditing"/> and <see cref="ShowWireframes"/>. Read by the tutorial's "press Tab"
	/// checklist row.</summary>
	public bool WireframesOn => _wireframesOn;

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

		// SculptBounds blame: while the sculpt is too big for a FIXED region, the offending brushes' wires
		// wear the warning colour so the player sees exactly what to pull back in (a floating region has no
		// single culprit — its list stays empty and the whole-sculpt outline carries the warning instead).
		var bounds = Target.GameObject.Components.Get<SculptBounds>();
		var warn = bounds.IsValid() && bounds.TooBig ? bounds.OffendingBrushes : null;

		_wireframes.Draw( Target.Brushes, Target.WorldTransform, Scene, Scene.Camera,
			Selected, hover, master, st.OutlineThickness, WireframeDepthBias,
			warn, bounds.IsValid() ? bounds.WarnColor : default,
			IsMultiSelection ? _selection : null );
	}

	// ── Depth of field (main camera) ─────────────────────────────────────────────────────────────────
	// Pull focus onto the edited object by asserting DoF targets every frame while editing; MainCamera does
	// the rack/easing. Asserting claims the DoF, so on exit we just stop — the camera's authored gameplay
	// baseline re-takes over and eases back in on its own (no save/restore handshake).

	float _lastFocusDist;

	void ApplyEditDof()
	{
		if ( !EditDepthOfField || !MainCamera.Current.IsValid() )
			return;

		// Seed so the first frame reads as "not moving" → the rack-in eases rather than snapping.
		_lastFocusDist = Vector3.DistanceBetween( MainCamera.Position, FocusTarget().Point );

		UpdateDofFocus(); // eases in toward the object
	}

	void UpdateDofFocus()
	{
		if ( !EditDepthOfField || !MainCamera.Current.IsValid() )
			return;

		var (point, radius) = FocusTarget();
		float dist = Vector3.DistanceBetween( MainCamera.Position, point );

		// Snap the focal plane while zooming (distance changing fast) so it doesn't trail the camera; ease
		// otherwise (enter/exit rack, orbiting). Blur/range always ease (and pick up live tweaks).
		float speed = Time.Delta > 0f ? MathF.Abs( dist - _lastFocusDist ) / Time.Delta : 0f;
		_lastFocusDist = dist;

		// The sharp band spans the sculpt (its radius scaled by DofSizeScale) plus the front/back pads;
		// the focal plane is the band's centre. Solved from the band edges, so the whole sculpt stays in
		// focus at any size and the pads keep their meaning as it grows.
		float extent = radius * DofSizeScale;
		float focal = dist + (DofBackPadding - DofFrontPadding) * 0.5f;
		float range = 2f * extent + DofFrontPadding + DofBackPadding;

		MainCamera.Dof.SetFocal( focal, lerp: speed <= DofZoomSnapSpeed );
		MainCamera.Dof.SetBlur( DofBlurSize, lerp: true );
		MainCamera.Dof.FocusRange = range;
	}

	(Vector3 Point, float Radius) FocusTarget()
	{
		// Exclude the pending stamp ghost — the DoF focus must not chase the cursor while placing.
		if ( Target.IsValid() && Sdf.TryGetBounds( Target.Brushes, out var bounds, StampBrush ) )
		{
			var scale = Target.WorldTransform.Scale;
			float maxScale = MathF.Max( MathF.Abs( scale.x ), MathF.Max( MathF.Abs( scale.y ), MathF.Abs( scale.z ) ) );
			return (Target.WorldTransform.PointToWorld( bounds.Center ), bounds.Size.Length * 0.5f * maxScale);
		}

		return (Target.IsValid() ? Target.WorldPosition : WorldPosition, 0f);
	}
}
