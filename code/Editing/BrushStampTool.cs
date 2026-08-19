using System;

namespace Mimiclay;

/// <summary>
/// Add-mode stamp tool: a pending "ghost" brush that rides the cursor over the sculpture, previewing its
/// add or carve LIVE in the real surface — the ghost is a real brush in <see cref="SdfSculpture.Brushes"/>,
/// so the raymarcher/field/blend all show exactly what a commit will produce. The operation comes from the
/// HUD's Add/Carve toggle (the A key flips it); a clean left TAP commits the stamp (<see
/// cref="AltNav.LmbTapped"/> — a left DRAG is the camera orbit, same click-vs-drag window as selection), and
/// a fresh ghost (inheriting the committed material/params) spawns immediately so stamping flows: place,
/// click, place, click. R + mouse movement scrubs scale (the same hold-key pattern as the rest of the edit
/// row); a right-click TAP steps back out of placing (<see cref="AltNav.RmbTapped"/> — a right DRAG is the
/// camera zoom).
///
/// Placement is physgun-style — ONE depth mechanism, no surface glue: the ghost's depth lives on a
/// WORLD-space anchor point (cursor steering slides it in the camera-parallel plane through that point, so
/// camera zoom/orbit never move the ghost), and the SCROLL WHEEL pushes/pulls that point along the view
/// ray (or the held constraint axis). Nothing ever re-seats the depth behind your back, so placement is
/// fully predictable; Shift held grid-snaps the position (sculpture-local, origin-centred) for the
/// precision cases.
///
/// The ghost is PENDING state: it must never leak into a commit it didn't ask for. The owning session
/// suppresses commit paths while a ghost is alive and calls <see cref="Cancel"/> on every exit
/// (tool switch, session end, disable) to strip it from the list.
/// </summary>
public sealed class BrushStampTool
{
	/// <summary>Shape the next ghost takes (and the live ghost is mutated to). Set via <see cref="SetShape"/>.</summary>
	public SdfShape Shape { get; private set; } = SdfShape.Sphere;

	/// <summary>Stamp operation — Add, Subtract (carve) or Cutout, driven by the HUD's cycle. The live ghost syncs
	/// to it on the next update, so toggling flips the preview in place.</summary>
	public SdfOperation Operation { get; set; } = SdfOperation.Add;

	/// <summary>The pending ghost brush — physically inside the target's brush list — or null (cap reached /
	/// not yet updated).</summary>
	public SdfBrush Stamp => _stamp;

	SdfBrush _stamp;

	// Carried appearance: each new ghost copies the last committed stamp (material, blend, rounding, size,
	// rotation), so consecutive stamps match without re-tweaking. Null until the first commit.
	SdfBrush _template;

	// The ghost's depth anchor: a sculpture-local POINT (its last position), not a camera distance — so
	// zooming/orbiting the camera never drags the ghost's world position along with it. Cursor steering
	// intersects the ray with the camera-parallel plane through this point; the scroll wheel pushes/pulls
	// the point along the view ray (or the held constraint axis).
	Vector3 _anchor;
	bool _seeded;

	// Camera-motion / gesture-end cursor resync. RULE: moving the camera must NEVER move the stamp, and no
	// frozen-cursor gesture (camera move, param scrub, depth drag) may end with a snap — so while any of
	// them runs, steering is suspended, and when the last one settles the OS cursor is WARPED onto the
	// ghost's screen position before steering resumes. Two-stage (arm → warp) so the HUD's mouse-capture
	// release — whose component-update order vs. this tool is arbitrary — gets a frame to let go first.
	Vector3 _lastCamPos;
	Rotation _lastCamRot;
	bool _camSeen;
	bool _resyncCursor; // a frozen gesture ran — warp the cursor onto the ghost before steering again
	bool _resyncArmed;  // intermediate stage: capture had a frame to release; warp now

	// Mouse.Position writes land ASYNCHRONOUSLY (OS/input service) — occasionally the next frame still
	// reads the pre-warp position, and steering from it pops the shape for a frame before the warp lands.
	// So after issuing a warp, steering stays suspended until the read-back position actually matches the
	// target (or a short timeout passes — the user moving the mouse immediately is legitimate).
	Vector2? _warpPending;
	int _warpFrames;

	// Camera-glide cursor pin state. A commit changes the sculpt bounds, which starts the designed camera
	// eases (pivot recentre glide, origin recentre) — seconds of slow motion during which the camera lock
	// would pin the cursor with per-frame warps, eating the user's input ("cursor stuck after stamping").
	// So: the pin holds only while the user is idle; their first real movement (echo-detected) BREAKS it —
	// they own the cursor, the ghost stays world-locked, and on settle steering resumes toward wherever
	// they put the cursor (user-initiated cursor movement is always allowed to move the ghost).
	bool _camLockBroken;
	Vector2? _camLockPx;

	/// <summary>Scroll depth speed as a fraction of the ghost's largest Size dimension per notch — big
	/// shapes travel further per notch, small ones move finely. Half of the default 16-sphere = 8 units
	/// per notch (the acceleration + camera-distance factors multiply on top).</summary>
	public float DepthStepFrac { get; set; } = 0.5f;

	/// <summary>Clamp on the per-notch depth step (world units), so extreme shapes stay controllable.</summary>
	public float DepthStepMin { get; set; } = 0.5f;
	public float DepthStepMax { get; set; } = 32f;

	/// <summary>The per-notch scroll depth step for a brush — a fraction of its largest dimension,
	/// clamped. Shared by the ghost's placement and gizmo mode's push/pull on the selection.</summary>
	public float DepthStepFor( SdfBrush b ) =>
		DepthStepFor( MathF.Max( b.Size.x, MathF.Max( b.Size.y, b.Size.z ) ) );

	/// <summary>As <see cref="DepthStepFor(SdfBrush)"/>, from a raw size reference — a spline has no
	/// meaningful Size, so its callers pass the fattest control-point radius instead.</summary>
	public float DepthStepFor( float sizeRef ) =>
		Math.Clamp( sizeRef * DepthStepFrac, DepthStepMin, DepthStepMax );

	/// <summary>Ceiling on the scroll-acceleration multiplier (a fast spin ramps toward this).</summary>
	public float DepthAccelMax { get; set; } = 8f;

	// Scroll acceleration: deliberate, spaced notches stay at the fine base step; notches arriving within
	// AccelWindow of each other multiply the step up (toward DepthAccelMax), so a fast spin covers real
	// distance. This is how OS scrolling / CAD wheel zoom square fine control with long travel.
	float _accel = 1f;
	RealTimeSince _sinceWheel = 999f;
	const float AccelWindow = 0.15f;
	const float AccelGrowth = 1.4f;

	/// <summary>Camera distance (world units) at which the depth step is exactly the base step; nearer
	/// scales it down (fine work while zoomed in), farther scales it up (coarse moves from afar).</summary>
	public float DepthDistanceRef { get; set; } = 200f;

	/// <summary>Clamp on the camera-distance scale factor.</summary>
	public float DepthDistanceScaleMin { get; set; } = 0.25f;
	public float DepthDistanceScaleMax { get; set; } = 4f;

	/// <summary>The accelerated, camera-distance-scaled depth step — call once per frame that actually
	/// consumed a wheel event, passing the camera→brush distance. Shared state, so ghost placement and
	/// gizmo push/pull behave identically: zoomed in = fine, zoomed out = coarse, spinning = faster.</summary>
	public float AcceleratedDepthStepFor( SdfBrush b, float camDist ) =>
		AcceleratedDepthStep( MathF.Max( b.Size.x, MathF.Max( b.Size.y, b.Size.z ) ), camDist );

	/// <summary>As above, from a raw size reference (a spline passes its fattest point radius).</summary>
	public float AcceleratedDepthStep( float sizeRef, float camDist )
	{
		_accel = _sinceWheel < AccelWindow ? MathF.Min( DepthAccelMax, _accel * AccelGrowth ) : 1f;
		_sinceWheel = 0f;

		float distScale = Math.Clamp( camDist / MathF.Max( 1f, DepthDistanceRef ), DepthDistanceScaleMin, DepthDistanceScaleMax );
		return DepthStepFor( sizeRef ) * _accel * distScale;
	}

	// ── Spline chain placement ───────────────────────────────────────────────────────────────────────
	// A spline isn't one stamp, it's a CHAIN: each click drops a control point (a sphere at the cursor,
	// sized by the RMB scale scrub) and the ghost previews the tube through the points placed so far plus
	// the one under the cursor. A right-click TAP or Enter finishes it — that's the frame the brush is
	// really committed. Steering the cursor near the first point snaps to it and closes the loop.

	readonly List<Vector4> _chain = new(); // control points committed so far (sculpture-local xyz + radius w)
	bool _chainLoop;                       // the preview point is snapped onto point 0 → finishing closes the ring
	bool _enterWas;                        // manual edge detect for the Enter finish key

	/// <summary>True while a spline chain is being placed (the ghost is a partial curve, not a stamp).</summary>
	public bool IsChaining => Shape == SdfShape.Spline && _stamp is not null;

	/// <summary>Control points placed so far in the current chain (0 when not chaining).</summary>
	public int ChainCount => _chain.Count;

	/// <summary>True while the cursor is snapped onto the chain's first point — finishing here closes the loop.</summary>
	public bool ChainWillLoop => _chainLoop;

	Vector3 _chainPreview; // where the chain's live point sits (== point 0 while loop-snapped)

	/// <summary>The ghost's sculpture-local position for camera/cursor purposes: a spline lives in its
	/// points, so use the live point under the cursor rather than the (unused) brush Position.</summary>
	public Vector3 StampLocalPos =>
		_stamp is null ? Vector3.Zero
		: _stamp.Shape == SdfShape.Spline ? _chainPreview
		: _stamp.Position;

	// How close (as a multiple of the first point's radius) the cursor must come to point 0 to snap + loop.
	const float LoopSnapScale = 1.1f;

	// Rebuild the ghost's point list = committed chain + the live preview point at `pos`, snapping onto the
	// first point (and arming the loop) when the cursor comes close enough. Returns true if anything moved.
	bool UpdateChainGhost( SdfSculpture target, Vector3 pos )
	{
		float radius = Math.Clamp( _stamp.Size.x, 1f, SdfBrush.MaxSplineRadius );

		// Clamp the live point against the world AT THE SOURCE, swept from last frame's preview so it
		// glides along the floor like a dragged point. This can't be left to the session's clamp: a chain
		// click copies the live point into _chain BEFORE the session sees this frame's mutation, so a raw
		// under-floor cursor position would poison the committed chain — which the clamp can never reach,
		// and every later ghost rebuild then reverts against it (the stuck-chain bug).
		var chainFrom = _stamp.Points is { Count: > 0 } ? _chainPreview : pos;
		pos = BrushWorldClamp.SlidePointSphere( target.Scene, target, chainFrom, pos, radius );

		// Loop snap: needs 3+ committed points to be a ring at all.
		_chainLoop = false;
		if ( _chain.Count >= 3 )
		{
			var first = new Vector3( _chain[0].x, _chain[0].y, _chain[0].z );
			if ( (pos - first).Length <= MathF.Max( 4f, _chain[0].w * LoopSnapScale ) )
			{
				pos = first;
				radius = _chain[0].w;
				_chainLoop = true;
			}
		}

		var pts = _stamp.Points ??= new List<Vector4>();
		var preview = new Vector4( pos.x, pos.y, pos.z, radius );
		_chainPreview = pos;

		// Snapped onto point 0: show the ring CLOSED and drop the preview entirely — appending it would
		// duplicate point 0 and leave a zero-length span in the loop.
		int want = _chainLoop ? _chain.Count : _chain.Count + 1;
		bool same = pts.Count == want && _stamp.SplineClosed == _chainLoop
			&& (_chainLoop || pts[^1] == preview);
		if ( same )
			return false;

		pts.Clear();
		pts.AddRange( _chain );
		if ( !_chainLoop )
		{
			pts.Add( preview );

			// Tube-aware pass: the CURVE into the live point can dip into the floor even when the point
			// itself is clear (Catmull-Rom overshoot) — restrict the live point's move (from last frame's
			// accepted preview) so the tube stays clear, and the value a click commits into _chain is
			// legal INCLUDING its curvature. Without this the dip rides into the committed chain, where no
			// later gesture can reach it, and the whole-ghost resolve then fights this per-frame rebuild.
			// Track the restricted value as the preview so the next frame's gesture starts from it.
			SculptEditSession.Current?.ClampGhostChainPoint( _stamp, pts.Count - 1, chainFrom );
			var accepted = pts[^1];
			_chainPreview = new Vector3( accepted.x, accepted.y, accepted.z );
		}
		_stamp.SplineClosed = _chainLoop;
		return true;
	}

	// A click during a chain: drop the preview point into the chain. Landing on the loop snap finishes the
	// spline instead (the ring is closed by definition).
	bool AddChainPoint()
	{
		if ( _stamp?.Points is not { Count: > 0 } pts )
			return false;

		if ( _chainLoop )
			return true; // closing click — the caller finishes; the ring is already the full chain

		_chain.Add( pts[^1] );
		return false;
	}

	// Finish the chain: a spline needs 2+ points, so a shorter chain is abandoned (the ghost is stripped by
	// the caller). Returns true when a real spline was committed.
	bool FinishChain()
	{
		if ( _stamp is null )
			return false;

		var pts = _stamp.Points;
		if ( _chain.Count < 2 || pts is null )
			return false;

		// Drop the trailing preview point (it's not a placed point) unless we're closing the ring, where it
		// coincides with point 0 and is redundant either way.
		pts.Clear();
		pts.AddRange( _chain );
		_stamp.SplineClosed = _chainLoop && _chain.Count >= 3;
		_stamp.SplinePerPointRadius = true; // vestigial flag — radii are always per-point now (see SdfBrush)

		LastCommitted = _stamp;
		_template = _stamp.Copy();
		_stamp = null;
		_chain.Clear();
		_chainLoop = false;
		return true;
	}

	/// <summary>Set the active stamp shape. A live ghost is remoulded in place (shape + that shape's spawn
	/// size/rotation, keeping material/blend), so the toolbar feels instant.</summary>
	public void SetShape( SdfShape shape )
	{
		bool wasSpline = Shape == SdfShape.Spline;
		Shape = shape;

		// Switching away from (or re-picking) a spline abandons any half-placed chain.
		if ( wasSpline || shape == SdfShape.Spline )
		{
			_chain.Clear();
			_chainLoop = false;
		}

		if ( _stamp is null )
			return;

		// Carry the SCALE across the swap — resize a sphere then pick box and you get a box that size, not
		// a fresh default. Orientation still resets, so the flat shapes (text/extruded) come up facing you.
		var from = _stamp.Shape;
		_stamp.Shape = shape;
		_stamp.Size = ResizeForShape( _stamp.Size, from, shape );
		_stamp.Rotation = SdfSculpture.SpawnRotation( shape );
		_stamp.Rounding = Math.Clamp( _stamp.Rounding, SdfBrush.MinRounding, _stamp.MaxRounding() ); // caps differ per shape
		_stamp.SplineClosed = false;
		// A spline ghost carries its live points (chain + preview, rebuilt each frame); anything else has none.
		_stamp.Points = shape == SdfShape.Spline ? new List<Vector4>() : null;
	}

	// Carry a brush's scale from one shape to another. Every solid reads Size as half-extents, so it
	// transfers straight across; text is the exception both ways — its plaque is aspect-locked to the glyph
	// slot (anything else distorts the letters) and those proportions make a nonsense solid coming back.
	static Vector3 ResizeForShape( Vector3 size, SdfShape from, SdfShape to )
	{
		if ( to == from )
			return size;

		if ( to == SdfShape.Text )
		{
			float yv = MathF.Max( size.y, 1f );
			return new Vector3( yv * (SdfTextData.Width / (float)SdfTextData.Height), yv, MathF.Min( size.z, 4f ) );
		}

		return from == SdfShape.Text ? new Vector3( MathF.Max( size.y, 1f ) ) : size;
	}

	/// <summary>Strip the pending ghost from the target's brush list (tool switch / session exit). Returns
	/// true if a ghost was actually removed — the caller should rebuild to drop it from the surface.</summary>
	public bool Cancel( SdfSculpture target )
	{
		var b = _stamp;
		_stamp = null;
		_camSeen = false; // stale camera baseline must not read as "camera moved" (and warp) on re-entry
		_resyncCursor = _resyncArmed = false;
		_warpPending = null;
		_camLockBroken = false;
		_camLockPx = null;
		_chain.Clear();   // a half-placed spline chain dies with the ghost
		_chainLoop = false;
		return b is not null && target.IsValid() && (target.Brushes?.Remove( b ) ?? false);
	}

	/// <summary>Run one frame of the stamp tool. Returns true when the brush list visibly changed (ghost
	/// moved/param-scrubbed/spawned) so the caller can refresh the live preview. <paramref name="committed"/>
	/// is set when a stamp was just committed — the caller runs the full rebuild/commit.
	/// <paramref name="interactive"/> false (cursor over UI, alt-nav, pause) freezes placement and gestures
	/// but keeps the ghost visible.</summary>
	public bool Update( SdfSculpture target, Scene scene, bool interactive, out bool committed )
	{
		committed = false;
		var cam = scene?.Camera;
		if ( cam is null || !target.IsValid() )
			return false;

		var brushes = target.Brushes ??= new();
		bool changed = false;

		// Ensure a ghost exists (respecting the brush cap — at the cap you simply can't stamp until you
		// delete something; the packer would silently drop an over-cap brush anyway).
		if ( _stamp is null || !brushes.Contains( _stamp ) )
		{
			if ( brushes.Count >= SdfBrushPacker.MaxBrushes )
			{
				_stamp = null;
				return false;
			}

			_stamp = NewStamp();
			brushes.Insert( target.AuthoredBrushCount, _stamp );
			changed = true;
		}

		// The HUD's Add/Carve toggle drives the ghost's live operation (the carve previews in place).
		if ( _stamp.Operation != Operation )
		{
			_stamp.Operation = Operation;
			changed = true;
		}

		var tx = target.WorldTransform;

		// Any camera motion (alt orbit/pan, wheel zoom, follow-rig settle) suspends steering — the stamp
		// must stay locked in place while the view moves, then the cursor re-syncs onto it (see fields).
		bool camMoved = _camSeen &&
			((cam.WorldPosition - _lastCamPos).LengthSquared > 1e-4f
			|| (cam.WorldRotation.Forward - _lastCamRot.Forward).LengthSquared > 1e-8f
			|| (cam.WorldRotation.Up - _lastCamRot.Up).LengthSquared > 1e-8f);
		_lastCamPos = cam.WorldPosition;
		_lastCamRot = cam.WorldRotation;
		_camSeen = true;

		// Hold param scrubs (S blend / D round / F wildcard / E rotate / R scale) — placement freezes while one runs
		// so the mouse motion drives the parameter, not the position. Ghost scrubs never commit (the stamp
		// itself is the commit), so the end signal is ignored. The ghost is already in the brush list, so
		// BlendInert locks its blend scrub exactly when it would land as the first additive brush.
		changed |= BrushScrub.Update( _stamp, tx, cam, interactive, out _,
			blendLocked: Sdf.BlendInert( target.Brushes, _stamp ) );

		// The scale scrub drives Size, but a spline's thickness lives in its point radii — mirror it onto
		// the preview point live, so the sphere under the cursor visibly resizes as you drag.
		if ( Shape == SdfShape.Spline && BrushScrub.Active == ScrubKind.Scale
			&& _stamp.Points is { Count: > 0 } livePts )
		{
			float r = Math.Clamp( _stamp.Size.x, 1f, SdfBrush.MaxSplineRadius );
			var lp = livePts[^1];
			if ( MathF.Abs( lp.w - r ) > 0.0001f )
			{
				livePts[^1] = new Vector4( lp.x, lp.y, lp.z, r );
				changed = true;
			}
		}

		if ( camMoved || BrushScrub.Active != ScrubKind.None )
		{
			if ( BrushScrub.Active != ScrubKind.None || AltNav.Dragging )
			{
				// Captured gestures (scrubs, alt-nav): the cursor is hidden — it gets placed back on the
				// shape at capture-release (EditHud), so just suspend steering and arm the resync.
				_resyncCursor = true;
				_resyncArmed = false;
				_camLockBroken = false;
				_camLockPx = null;
			}
			else if ( _camLockBroken )
			{
				// The user grabbed the cursor mid-glide — they own it. The ghost stays world-locked for
				// the rest of the motion; on settle, steering resumes toward wherever they put the cursor.
			}
			else
			{
				// Visible-cursor camera motion: LOCK the cursor to the shape while the user is idle, so it
				// glides with the ghost and nothing snaps on settle. The moment a read differs from our
				// last pin target (real input — post-commit recentre glides last seconds, and eating mouse
				// input that whole time reads as "cursor stuck"), break the pin for this glide.
				var mp = Mouse.Position;
				if ( _camLockPx is { } cp && (mp - cp).LengthSquared >= 0.25f )
				{
					_camLockBroken = true;
					_resyncCursor = false; // their movement is intentional — no warp-back at settle
					_resyncArmed = false;
					_camLockPx = null;
				}
				else
				{
					_resyncCursor = true;
					_resyncArmed = false;

					var world = tx.PointToWorld( StampLocalPos );
					if ( Vector3.Dot( world - cam.WorldPosition, cam.WorldRotation.Forward ) > 0f )
					{
						// Mirror the engine setter's floor+clamp so the break check sees the same pixel.
						var px = cam.PointToScreenPixels( world );
						px.x = Math.Clamp( MathF.Floor( px.x ), 0f, Screen.Width - 1 );
						px.y = Math.Clamp( MathF.Floor( px.y ), 0f, Screen.Height - 1 );
						Mouse.Position = px;
						_camLockPx = px;
					}
				}
			}
		}
		else
		{
			_camLockBroken = false;
			_camLockPx = null;
			if ( interactive )
				changed |= UpdatePlacement( target, cam, tx );
		}

		if ( interactive )
		{
			// A clean left TAP stamps with the toggled operation — the same click-vs-drag window selection
			// uses (see AltNav.LmbTapped): a press that travels becomes the camera ORBIT instead, so you can
			// orbit from anywhere while placing, and only a genuine click commits. The op itself comes from
			// the HUD's Add/Carve toggle, not the button.
			if ( AltNav.LmbTapped )
			{
				if ( Shape == SdfShape.Spline )
				{
					// Chain click: drop a control point — or finish, if it landed on the loop snap.
					if ( AddChainPoint() )
						committed = FinishChain();
					changed = true;
				}
				else
				{
					Commit();
					committed = true;
				}
			}

			// A right-click TAP (a zoom claim that released without travel — see AltNav.RmbTapped; a right
			// DRAG is the camera zoom now) steps back out of placing: it finishes a spline chain, or drops
			// the pending stamp and returns to gizmo mode. Enter and Space finish a chain too (the session's
			// Space toggle stands down while a spline stamp is active — see its addKey gate) — manual edge
			// detection on Down, since Keyboard.Pressed doesn't fire reliably here (an unknown key name
			// resolves to invalid/false, never throws).
			bool enter = Sandbox.UI.InputFocus.Current is null
				&& (Input.Keyboard.Down( "enter" ) || Input.Keyboard.Down( "return" )
					|| Input.Keyboard.Down( "space" ));
			bool enterTap = enter && !_enterWas;
			_enterWas = enter;

			if ( !committed && (AltNav.RmbTapped || enterTap) )
			{
				if ( Shape == SdfShape.Spline )
				{
					committed = FinishChain();
					changed = true;
					if ( !committed )
						Abandon = true; // too short to be a curve — the caller strips the ghost
				}
				else if ( AltNav.RmbTapped )
				{
					Abandon = true; // plain right-click = done placing
				}
			}
		}

		return changed;
	}

	/// <summary>Set when a spline chain was finished with too few points to be a curve — the session strips
	/// the ghost and drops back to gizmo mode instead of committing anything. Cleared by the reader.</summary>
	public bool Abandon { get; set; }

	/// <summary>The brush the most recent commit produced — the session selects it for place-then-tweak.</summary>
	public SdfBrush LastCommitted { get; private set; }

	// The release turns the ghost INTO the sculpt: it just stays in the list. Remember it as the template so
	// the next ghost matches, and let Update spawn that next ghost on the following frame.
	void Commit()
	{
		LastCommitted = _stamp;
		_template = _stamp.Copy();
		_stamp = null;
	}

	bool UpdatePlacement( SdfSculpture target, CameraComponent cam, Transform tx )
	{
		var ray = cam.ScreenPixelToRay( Mouse.Position );

		// Cursor ray + camera forward into sculpture-local space (assumes unit scale, like every pick here).
		var invRot = tx.Rotation.Inverse;
		var o = invRot * (ray.Position - tx.Position);
		var d = (invRot * ray.Forward).Normal;
		var fwd = (invRot * cam.WorldRotation.Forward).Normal;

		// Seed the anchor once: at the SCULPTURE's bounds centre (sans the ghost), so the first ghost
		// appears on the model — the old ray-closest-approach seed degenerated to "8 units from the
		// camera" whenever the cursor ray happened to point away from the origin. Ray fallback only when
		// there are no bounds to centre on.
		if ( !_seeded )
		{
			_anchor = Sdf.TryGetBounds( target.Brushes, out var bb, _stamp )
				? bb.Center
				: o + d * MathF.Max( 8f, Vector3.Dot( -o, d ) );
			_seeded = true;
		}

		Vector3 pos;
		{
			// A frozen-cursor gesture (camera move / scrub) just ended: warp the OS cursor exactly onto the
			// shape, so steering resumes FROM the ghost and nothing ever snaps. Staged across two frames so
			// the HUD's mouse-capture release (arbitrary update order) lands first.
			if ( _resyncCursor )
			{
				_resyncCursor = false;
				_resyncArmed = true;
				return false;
			}
			if ( _resyncArmed )
			{
				_resyncArmed = false;
				var world = tx.PointToWorld( StampLocalPos );
				if ( Vector3.Dot( world - cam.WorldPosition, cam.WorldRotation.Forward ) > 0f )
				{
					var px = cam.PointToScreenPixels( world );
					Mouse.Position = px;
					_warpPending = px; // verify the write actually landed before steering resumes
					_warpFrames = 0;
					_anchor = StampLocalPos; // re-anchor on the applied (possibly snapped) position
				}
				return false; // steer once the warp is confirmed
			}

			// Warp in flight: the OS cursor write hasn't been observed yet — steering now would use the
			// STALE position and pop the shape for a frame. Wait for it (or give up after a few frames:
			// past that, a mismatch means the user genuinely moved the mouse, which is normal steering).
			if ( _warpPending is { } wp )
			{
				if ( (Mouse.Position - wp).Length <= 2f || ++_warpFrames > 5 )
					_warpPending = null;
				else
					return false;
			}

			// Scroll wheel = push/pull: move the anchor along the view ray — wheel-up pushes away. The
			// per-notch step scales with the ghost's size (half its largest dimension, clamped), so a
			// pebble nudges finely and a boulder actually travels. The steered position recomputes from
			// the moved anchor below, staying under the cursor by construction.
			float wheel = Input.MouseWheel.y;
			if ( wheel != 0f )
			{
				float camDist = (tx.PointToWorld( _stamp.Position ) - cam.WorldPosition).Length;
				_anchor += d * wheel * AcceleratedDepthStepFor( _stamp, camDist );
			}

			// Steer by intersecting the cursor ray with the camera-parallel plane through the anchor.
			// The depth lives on the WORLD-space anchor, not at a camera distance — so zooming or
			// orbiting the camera never drags the ghost along; only steering moves it.
			float denom = Vector3.Dot( d, fwd );
			float t = denom > 1e-4f ? Vector3.Dot( _anchor - o, fwd ) / denom : -1f;
			pos = t >= 8f ? o + d * t : _anchor; // degenerate / camera zoomed past the plane: hold position

			// Depth MEMORY keeps the raw steered value: the anchor may sit behind geometry, but only as a
			// remembered depth — the PRESENTED position below is always capped to the near side, so the
			// stored depth comes back the moment the view ray clears the obstacle.
			_anchor = pos;

			// The PRESENTED position must never sit BEHIND world geometry the view passes through
			// (scrolled past the floor, or a stale anchor from an earlier ghost): sweep a sphere of the
			// ghost's rough size from the CAMERA to the steered position and stop at the first thing the
			// world clamp considers solid. Capping runs every placement frame, fresh ghosts included, so a
			// wrong-side anchor is harmless — the ghost is always shown (and clamped, and committed) on
			// the near side. StartedSolid (the camera itself inside geometry) skips the cap — the
			// session's world clamp still protects the brush itself.
			var scene = target.Scene;
			if ( scene.IsValid() )
			{
				float sweepR = _stamp.Shape == SdfShape.Spline
					? Math.Clamp( _stamp.Size.x, 1f, SdfBrush.MaxSplineRadius ) // the chain point's radius
					: Math.Clamp( MathF.Min( _stamp.Size.x, MathF.Min( _stamp.Size.y, _stamp.Size.z ) ), 2f, 64f );

				var trc = BrushWorldClamp.Filtered( scene, target )
					.Sphere( sweepR, cam.WorldPosition, tx.PointToWorld( pos ) )
					.Run();
				if ( trc.Hit && !trc.StartedSolid )
					pos = tx.PointToLocal( trc.EndPosition );
			}
		}

		// Shift held = rough grid snap (the shared sculpture-local, origin-centred grid every editing tool
		// uses — see SculptEditSession.SnapPosition). The anchor stays UNSNAPPED so steering keeps its
		// precision; only the applied position quantizes.
		if ( SculptEditSession.SnapHeld )
			pos = SculptEditSession.SnapPosition( pos );

		// A spline lives in its points, not its transform: steer the PREVIEW point (the sphere under the
		// cursor) and leave Position alone.
		if ( _stamp.Shape == SdfShape.Spline )
			return UpdateChainGhost( target, pos );

		var old = _stamp.Position;
		_stamp.Position = pos;
		_stamp.SnapToMirrorPlanes(); // mirrored ghost entering the deadzone magnetizes to the plane
		return (_stamp.Position - old).LengthSquared >= 0.0001f;
	}

	SdfBrush NewStamp()
	{
		// Copy the last committed stamp so consecutive stamps match; first ghost of a session gets defaults.
		var b = _template is not null ? _template.Copy() : new SdfBrush
		{
			Size = SpawnSize( Shape ),
			Blend = 2f, // a third of SdfBrush's own default — stamps want a much tighter seam
		};

		// The scale carries across shapes here too: picking a new shape between stamps re-creates the ghost
		// from the template, and losing the size there would undo the swap-keeps-scale rule above.
		b.Shape = Shape;
		if ( _template is null )
		{
			b.Size = SpawnSize( Shape );
			b.Rotation = SdfSculpture.SpawnRotation( Shape );
		}
		else
		{
			b.Size = ResizeForShape( _template.Size, _template.Shape, Shape );
			if ( _template.Shape != Shape )
				b.Rotation = SdfSculpture.SpawnRotation( Shape );
		}
		b.Operation = Operation;
		b.SplineClosed = false;
		b.Points = Shape == SdfShape.Spline ? new List<Vector4>() : null;
		b.Damage = false;

		// Wear the current material — whatever the user last picked, on any brush (see LastMaterial). The
		// template usually already matches; this covers colouring an EXISTING shape and then adding one.
		if ( SculptEditSession.LastMaterial is { } mat )
		{
			b.Color = mat.Color;
			b.Metallic = mat.Metallic;
			b.Roughness = mat.Roughness;
		}

		return b;
	}

	// Spawn size for the FIRST stamp of a session (after that the scale carries across shapes and stamps).
	// Half the old values — those matched AddBrush's stack-added defaults, which read as too chunky when
	// you're placing by hand.
	static Vector3 SpawnSize( SdfShape shape ) => shape switch
	{
		SdfShape.Sphere => new Vector3( 8f ),
		SdfShape.Text => new Vector3( 12f, 6f, 2f ),
		_ => new Vector3( 6f ),
	};
}

public enum ScrubKind
{
	None,
	Blend,
	Round,
	Wildcard, // the per-shape third slider: slice / profile / spline size
	Scale,
	Rotate,
	Move, // screen-space grab (W) — edit mode only; the stamp ghost already rides the cursor
}

/// <summary>
/// Hold parameter scrubbing, Blender-modal-style: hold S / D / F (the slider stack in order — blend, round,
/// then the per-shape wildcard: slice, profile, spline size; A ahead of them TAPS add/carve, so the whole
/// edit row sits on A-S-D-F), W (screen-space move), R (uniform scale) or E (free rotate) and move the
/// mouse. Shift held snaps the rotate to the shared <see cref="SculptEditSession.SnapDeg"/> grid and the
/// move to the <see cref="SculptEditSession.GridStep"/> grid. Shared by BOTH tools — the stamp ghost in add
/// mode and the selected brush in edit mode — so the whole scheme is learned once (W is the exception: the
/// caller enables it per-tool, and the stamp leaves it off — its ghost already rides the cursor). One
/// scrub runs at a time, game-wide (static), matching the one-cursor reality; the HUD reads
/// <see cref="Active"/>/<see cref="Anchor"/> to capture the mouse and draw the frozen-cursor dot.
/// </summary>
public static class BrushScrub
{
	/// <summary>The scrub currently running (None = idle).</summary>
	public static ScrubKind Active { get; private set; }

	/// <summary>Screen px where the scrub started — the HUD draws its frozen-cursor dot here while the
	/// mouse is captured.</summary>
	public static Vector2 Anchor { get; private set; }

	// Manual edge detection (Input.Keyboard exposes Down only).
	static bool _blendWas, _roundWas, _wildWas, _moveWas, _scaleWas, _rotWas;

	// The rotate scrub's continuous (unsnapped) orientation, sculpture-local. The mouse always drives THIS;
	// shift only changes what gets applied to the brush (the grid-snapped version of it).
	static Rotation _rawRot = Rotation.Identity;

	// Wildcard profile-cycling accumulator (Extruded: the profile steps once per ProfileStepPx of travel).
	static float _wildAccum;
	static readonly SdfCrossSection[] ProfileCycle =
	{
		SdfCrossSection.Triangle,
		SdfCrossSection.Star,
		SdfCrossSection.Pentagon,
		SdfCrossSection.Hexagon,
	};

	const float BlendPerPx = 0.06f;   // MaxBlend (15) across ~250 px
	const float RoundPerPx = 0.15f;
	const float CurvePerPx = 0.005f;  // spline curvature 0..1 across ~200 px
	const float SlicePerPx = 0.0035f; // MaxSlice (0.95) across ~270 px
	const float SplineSizePerPx = 0.08f;
	const float ProfileStepPx = 80f;
	const float ScaleHalfDoublePx = 240f; // px of mouse travel that doubles the size
	const float DegPerPx = 0.4f; // matches the gizmo trackball's TrackballDegPerPx — one free-rotate feel

	/// <summary>Run one frame. Returns true when the brush changed; <paramref name="ended"/> fires on the
	/// frame a scrub's key is released (edit mode commits there). Passing a null brush or allow=false ends
	/// any running scrub without changes. <paramref name="blendLocked"/> parks the S/blend scrub (the first
	/// additive brush's blend is a field no-op — see <see cref="Sdf.BlendInert"/> — and the HUD greys its
	/// slider; the key hold must respect the same lock). <paramref name="allowMove"/> enables the W
	/// screen-space move — the edit session turns it on; the stamp leaves it off (its ghost already rides
	/// the cursor, so a move scrub there would just be a worse version of placement).</summary>
	public static bool Update( SdfBrush b, Transform sculptTx, CameraComponent cam, bool allow, out bool ended,
		bool blendLocked = false, bool allowMove = false )
	{
		ended = false;

		// A focused text field (the Text brush's entry) owns the keyboard — typing must never scrub.
		// S/D/F scrub the slider stack in order: blend, round (curve on a spline), then the per-shape
		// wildcard (slice / profile / spline size) — A ahead of them taps add/carve, so the whole edit row
		// sits on A-S-D-F. W moves (screen space), R scales, E rotates (gmod-style) — keyboard holds like
		// the rest of the row; the right mouse button belongs to the CAMERA now (zoom drag, step-back tap
		// via AltNav.RmbTapped).
		bool typing = Sandbox.UI.InputFocus.Current is not null;
		bool blendKey = !typing && Input.Keyboard.Down( "s" );
		bool roundKey = !typing && Input.Keyboard.Down( "d" );
		bool wildKey = !typing && Input.Keyboard.Down( "f" );
		bool moveKey = allowMove && !typing && Input.Keyboard.Down( "w" );
		bool scale = !typing && Input.Keyboard.Down( "r" );
		bool rot = !typing && Input.Keyboard.Down( "e" );
		bool blendP = blendKey && !_blendWas, roundP = roundKey && !_roundWas, wildP = wildKey && !_wildWas,
			moveP = moveKey && !_moveWas, scaleP = scale && !_scaleWas, rotP = rot && !_rotWas;
		_blendWas = blendKey; _roundWas = roundKey; _wildWas = wildKey; _moveWas = moveKey; _scaleWas = scale; _rotWas = rot;

		if ( b is null || cam is null || (!allow && Active == ScrubKind.None) )
		{
			// Nothing to scrub — and if one was somehow running (brush deselected under it), stop it.
			if ( Active != ScrubKind.None ) { Active = ScrubKind.None; ended = true; }
			return false;
		}

		// Start on the key's press edge (one scrub at a time; the first claim wins).
		if ( Active == ScrubKind.None && allow )
		{
			if ( blendP && !blendLocked ) Begin( ScrubKind.Blend, b );
			else if ( roundP ) Begin( ScrubKind.Round, b );
			else if ( wildP ) Begin( ScrubKind.Wildcard, b );
			else if ( moveP ) Begin( ScrubKind.Move, b );
			else if ( scaleP ) Begin( ScrubKind.Scale, b );
			else if ( rotP ) Begin( ScrubKind.Rotate, b );
		}

		if ( Active == ScrubKind.None )
			return false;

		// End when the owning key lifts.
		bool stillHeld = Active switch
		{
			ScrubKind.Blend => blendKey,
			ScrubKind.Round => roundKey,
			ScrubKind.Wildcard => wildKey,
			ScrubKind.Move => moveKey,
			ScrubKind.Scale => scale,
			ScrubKind.Rotate => rot,
			_ => false,
		};
		if ( !stillHeld )
		{
			Active = ScrubKind.None;
			ended = true;
			return false;
		}

		var delta = Mouse.Delta;
		if ( delta.LengthSquared < 0.0001f )
			return false;

		switch ( Active )
		{
			case ScrubKind.Blend:
				b.Blend = Math.Clamp( b.Blend + delta.x * BlendPerPx, 0f, SdfBrush.MaxBlend );
				return true;

			case ScrubKind.Round:
				// The second slider: rounding for the solids, curvature/smoothness for a spline — a curve
				// has no roundness, so smoothness takes that slot in BOTH modes.
				if ( b.Shape == SdfShape.Spline )
					b.Curvature = Math.Clamp( b.Curvature + delta.x * CurvePerPx, 0f, 1f );
				else
					b.Rounding = Math.Clamp( b.Rounding + delta.x * RoundPerPx, SdfBrush.MinRounding, b.MaxRounding() );
				return true;

			case ScrubKind.Wildcard:
				// The per-shape third slider: slice for the sliceable solids, the profile for the extruded
				// "Shape" brush (steps through them as you scrub), uniform tube size for a spline. Shapes
				// with no third parameter (box, cylinder, text) just do nothing.
				switch ( b.Shape )
				{
					case SdfShape.Sphere or SdfShape.Cone:
						b.Slice = Math.Clamp( b.Slice + delta.x * SlicePerPx, 0f, SdfBrush.MaxSlice );
						return true;

					case SdfShape.Extruded:
					{
						_wildAccum += delta.x;
						int steps = (int)(_wildAccum / ProfileStepPx);
						if ( steps == 0 )
							return false;
						_wildAccum -= steps * ProfileStepPx;

						int cur = Array.IndexOf( ProfileCycle, b.CrossSection );
						if ( cur < 0 )
							cur = 0;
						b.CrossSection = ProfileCycle[((cur + steps) % ProfileCycle.Length + ProfileCycle.Length) % ProfileCycle.Length];
						// Profiles have different rounding caps (star << hexagon) — keep the value honest.
						b.Rounding = Math.Clamp( b.Rounding, SdfBrush.MinRounding, b.MaxRounding() );
						return true;
					}

					case SdfShape.Spline:
					{
						// Smoothness lives on the Round slot for splines; the wildcard is the uniform tube
						// size — which only exists in gizmo mode (while PLACING, each point is sized as you
						// drop it, so a uniform slider would fight that).
						if ( SculptEditSession.Current?.Tool == SculptTool.Sculpt )
							return false;

						if ( b.Points is not { Count: > 0 } pts )
							return false;
						float r = Math.Clamp( pts[0].w + delta.x * SplineSizePerPx, 1f, SdfBrush.MaxSplineRadius );
						for ( int i = 0; i < pts.Count; i++ )
							pts[i] = new Vector4( pts[i].x, pts[i].y, pts[i].z, r );
						return true;
					}

					default:
						return false;
				}

			case ScrubKind.Scale:
			{
				float factor = MathF.Pow( 2f, delta.x / ScaleHalfDoublePx );

				// A selected spline's geometry lives in its points, so Size would be inert — scale the WHOLE
				// curve instead: positions about its bounds centre plus every radius, multiplicatively, so
				// the per-point radius ratios survive (the F/wildcard scrub is the "make them uniform"
				// control). While PLACING a chain the solid path below still runs — Size.x is what the stamp
				// tool mirrors onto the preview point, sizing each point as it's dropped.
				if ( b.Shape == SdfShape.Spline && SculptEditSession.Current?.Tool != SculptTool.Sculpt )
				{
					if ( b.Points is not { Count: > 0 } pts )
						return false;

					b.LocalBounds( out var mn, out var mx, includeBlend: false );
					var centre = (mn + mx) * 0.5f;
					for ( int i = 0; i < pts.Count; i++ )
					{
						var p = centre + (new Vector3( pts[i].x, pts[i].y, pts[i].z ) - centre) * factor;
						float r = Math.Clamp( pts[i].w * factor, 1f, SdfBrush.MaxSplineRadius );
						pts[i] = new Vector4( p.x, p.y, p.z, r );
					}
					return true;
				}

				float floor = b.Shape == SdfShape.Text ? 0.6f : 1f; // same floor the gizmo's scale handles use
				b.Size = new Vector3(
					MathF.Max( floor, b.Size.x * factor ),
					MathF.Max( floor, b.Size.y * factor ),
					MathF.Max( floor, b.Size.z * factor ) );
				return true;
			}

			case ScrubKind.Rotate:
			{
				// A spline's Rotation is inert — while PLACING a chain there's nothing to rotate (each point
				// is placed where it's placed), so the scrub just parks, like the wildcard does there.
				if ( b.Shape == SdfShape.Spline && SculptEditSession.Current?.Tool == SculptTool.Sculpt )
					return false;

				// Mouse X = yaw about the camera's up, mouse Y = pitch about its right — the SAME direction
				// convention as the gizmo's trackball (the shape follows the mouse), so the two free-rotate
				// controls feel like one. The motion accumulates on the RAW orientation; Shift held applies
				// it snapped ABSOLUTELY to the shared grid in the sculpture's frame (local Euler rounded) —
				// a brush at 8° lands on the grid steps (0/22.5/45…), never 8°+step. Releasing Shift
				// returns to the raw value.
				var worldRaw = sculptTx.Rotation * _rawRot;
				worldRaw = Rotation.FromAxis( cam.WorldRotation.Up, delta.x * DegPerPx )
					* Rotation.FromAxis( cam.WorldRotation.Right, delta.y * DegPerPx )
					* worldRaw;
				_rawRot = sculptTx.Rotation.Inverse * worldRaw;

				var applied = SculptEditSession.SnapHeld ? SculptEditSession.SnapRotation( _rawRot ) : _rawRot;

				// A selected spline has no transform to turn — rotate its POINT CLOUD about its bounds
				// centre instead. _rawRot accumulates the drag DELTA (Begin seeds identity + a point
				// snapshot), so Shift snaps that delta to the same 22.5° grid.
				if ( b.Shape == SdfShape.Spline )
				{
					if ( b.Points is not { Count: > 0 } pts || _splinePts0 is null || pts.Count != _splinePts0.Count )
						return false;
					if ( applied == _splineApplied )
						return false; // still in the same grid cell — no rebuild churn

					_splineApplied = applied;
					for ( int i = 0; i < pts.Count; i++ )
					{
						var p0 = new Vector3( _splinePts0[i].x, _splinePts0[i].y, _splinePts0[i].z );
						var p = _splineCentre0 + applied * (p0 - _splineCentre0);
						pts[i] = new Vector4( p.x, p.y, p.z, _splinePts0[i].w );
					}
					return true;
				}

				if ( applied == b.Rotation )
					return false; // still in the same grid cell — no rebuild churn

				b.Rotation = applied;
				return true;
			}

			case ScrubKind.Move:
			{
				// Screen-space grab: the shape tracks the captured cursor 1:1 — the mouse delta maps onto the
				// camera's right/up plane at the shape's own depth (world-per-pixel × distance, recomputed as
				// it moves so the 1:1 lock holds while it travels). The offset accumulates RAW from the grab
				// snapshot; Shift applies it snapped — a solid's ABSOLUTE position onto the shared grid (like
				// the gizmo's screen-move and the stamp), a spline's OFFSET onto grid steps instead (points
				// rounded independently would distort the curve). Releasing Shift returns to the raw drag.
				bool spline = b.Shape == SdfShape.Spline;
				if ( spline && (b.Points is not { Count: > 0 } || _splinePts0 is null || b.Points.Count != _splinePts0.Count) )
					return false;

				var centreLocal = spline ? _splineCentre0 + _moveAccum : b.Position;
				float dist = (sculptTx.PointToWorld( centreLocal ) - cam.WorldPosition).Length;
				var worldDelta = (cam.WorldRotation.Right * delta.x - cam.WorldRotation.Up * delta.y)
					* (dist * WorldPerPixel( cam ));
				_moveAccum += sculptTx.Rotation.Inverse * worldDelta; // sculpture-local (unit scale, like the picks)

				if ( spline )
				{
					var pts = b.Points;
					var off = SculptEditSession.SnapHeld ? SculptEditSession.SnapPosition( _moveAccum ) : _moveAccum;
					for ( int i = 0; i < pts.Count; i++ )
						pts[i] = new Vector4( _splinePts0[i].x + off.x, _splinePts0[i].y + off.y,
							_splinePts0[i].z + off.z, _splinePts0[i].w );
					return true;
				}

				var pos = _movePos0 + _moveAccum;
				if ( SculptEditSession.SnapHeld )
					pos = SculptEditSession.SnapPosition( pos );
				b.Position = pos;
				return true;
			}
		}

		return false;
	}

	// Whole-spline rotate/move state: the points as they were when the scrub began, and the curve centre
	// they turn about. _rawRot holds the accumulated drag delta (from identity) instead of a brush
	// orientation.
	static List<Vector4> _splinePts0;
	static Vector3 _splineCentre0;
	static Rotation _splineApplied;

	// Move scrub state: position at grab + the accumulated raw (unsnapped) sculpture-local offset.
	static Vector3 _movePos0;
	static Vector3 _moveAccum;

	static void Begin( ScrubKind kind, SdfBrush b )
	{
		Active = kind;
		Anchor = Mouse.Position;
		_rawRot = b.Rotation; // rotate scrub: continuous motion accumulates from the brush's current orientation
		_wildAccum = 0f;
		_movePos0 = b.Position;
		_moveAccum = Vector3.Zero;

		_splinePts0 = null;
		if ( (kind == ScrubKind.Rotate || kind == ScrubKind.Move)
			&& b.Shape == SdfShape.Spline && b.Points is { Count: > 0 } pts )
		{
			// A spline rotates/moves its point cloud, not its (inert) transform: accumulate a DELTA from
			// identity/zero and re-derive every frame from this snapshot, so Shift-snapping stays clean.
			_rawRot = Rotation.Identity;
			_splineApplied = Rotation.Identity;
			_splinePts0 = new List<Vector4>( pts );
			b.LocalBounds( out var mn, out var mx, includeBlend: false );
			_splineCentre0 = (mn + mx) * 0.5f;
		}
	}

	// World size of one screen pixel at unit distance (same formula as RuntimeBrushGizmo / BrushWireframes).
	static float WorldPerPixel( CameraComponent cam )
	{
		var c = Screen.Size * 0.5f;
		var r0 = cam.ScreenPixelToRay( c );
		var r1 = cam.ScreenPixelToRay( c + new Vector2( 0f, 1f ) );
		return (r1.Forward - r0.Forward).Length;
	}
}
