using System;

namespace Mimiclay;

/// <summary>
/// Add-mode stamp tool: a pending "ghost" brush that rides the cursor over the sculpture, previewing its
/// add (or, while right-click is held, its carve) LIVE in the real surface — the ghost is a real brush in
/// <see cref="SdfSculpture.Brushes"/>, so the raymarcher/field/blend all show exactly what a commit will
/// produce. Left-release commits an add, right-release commits a subtract, and a fresh ghost (inheriting
/// the committed material/params) spawns immediately so stamping flows: place, click, place, click.
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

	/// <summary>Stamp operation — Add or Subtract (carve), driven by the HUD's toggle. The live ghost syncs
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
	bool _holding;         // a stamp click is mid-gesture (commits on release)

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

	// Single-axis cursor-lock echo detection. The per-frame warp is floored to whole pixels by the engine,
	// so steering from the warped position lands the ghost slightly elsewhere → new warp → new error — a
	// systematic sub-pixel feedback that drives the ghost along the axis on its own (worse the more the
	// axis is foreshortened on screen). Cure: remember the last TWO warp targets (writes land async) and
	// treat a cursor read sitting exactly on one of them as our own echo — hold the ghost still; only a
	// read that differs (real user movement, ≥1px) steers.
	Vector2? _axisLock, _axisLockPrev;

	// Camera-glide cursor pin state. A commit changes the sculpt bounds, which starts the designed camera
	// eases (pivot recentre glide, origin recentre) — seconds of slow motion during which the camera lock
	// would pin the cursor with per-frame warps, eating the user's input ("cursor stuck after stamping").
	// So: the pin holds only while the user is idle; their first real movement (echo-detected) BREAKS it —
	// they own the cursor, the ghost stays world-locked, and on settle steering resumes toward wherever
	// they put the cursor (user-initiated cursor movement is always allowed to move the ghost).
	bool _camLockBroken;
	Vector2? _camLockPx;

	/// <summary>World units one scroll-wheel notch pushes/pulls the ghost (wheel-up = away).</summary>
	public float DepthStep { get; set; } = 8f;

	/// <summary>Grid cell size for shift-snapped placement (sculpture-local units). The grid is centred on
	/// the sculpture origin, so 0,0,0 — the symmetry planes — is always on-grid.</summary>
	public float GridStep { get; set; } = 8f;

	/// <summary>Set the active stamp shape. A live ghost is remoulded in place (shape + that shape's spawn
	/// size/rotation, keeping material/blend), so the toolbar feels instant.</summary>
	public void SetShape( SdfShape shape )
	{
		Shape = shape;
		if ( _stamp is null )
			return;

		_stamp.Shape = shape;
		_stamp.Size = SpawnSize( shape );
		_stamp.Rotation = SdfSculpture.SpawnRotation( shape );
		_stamp.Points = null; // stamping never carries spline points (splines aren't stampable)
	}

	/// <summary>Strip the pending ghost from the target's brush list (tool switch / session exit). Returns
	/// true if a ghost was actually removed — the caller should rebuild to drop it from the surface.</summary>
	public bool Cancel( SdfSculpture target )
	{
		var b = _stamp;
		_stamp = null;
		_holding = false;
		_camSeen = false; // stale camera baseline must not read as "camera moved" (and warp) on re-entry
		_resyncCursor = _resyncArmed = false;
		_warpPending = null;
		_axisLock = _axisLockPrev = null;
		_camLockBroken = false;
		_camLockPx = null;
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
			_holding = false;
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

		// Hold param scrubs (A blend / S round / RMB scale / MMB rotate) — placement freezes while one runs
		// so the mouse motion drives the parameter, not the position. Ghost scrubs never commit (the stamp
		// itself is the commit), so the end signal is ignored.
		changed |= BrushScrub.Update( _stamp, tx, cam, interactive, out _ );

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

					var world = tx.PointToWorld( _stamp.Position );
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
			// Click stamps with the toggled operation: press keeps placing (you can slide it around while
			// held), release commits. The op itself comes from the HUD's Add/Carve toggle, not the button.
			if ( Input.Pressed( "Attack1" ) )
				_holding = true;

			if ( _holding && !Input.Down( "Attack1" ) )
			{
				Commit();
				committed = true;
			}
		}
		else
		{
			// The gesture drifted into UI/alt-nav — drop it without stamping.
			_holding = false;
		}

		return changed;
	}

	// The release turns the ghost INTO the sculpt: it just stays in the list. Remember it as the template so
	// the next ghost matches, and let Update spawn that next ghost on the following frame.
	void Commit()
	{
		_template = _stamp.Copy();
		_stamp = null;
		_holding = false;
	}

	bool UpdatePlacement( SdfSculpture target, CameraComponent cam, Transform tx )
	{
		var ray = cam.ScreenPixelToRay( Mouse.Position );

		// Cursor ray + camera forward into sculpture-local space (assumes unit scale, like every pick here).
		var invRot = tx.Rotation.Inverse;
		var o = invRot * (ray.Position - tx.Position);
		var d = (invRot * ray.Forward).Normal;
		var fwd = (invRot * cam.WorldRotation.Forward).Normal;

		// Seed the anchor once: where the ray passes closest to the sculpture origin, so the first ghost
		// appears in the middle of the working area rather than at some arbitrary depth.
		if ( !_seeded )
		{
			_anchor = o + d * MathF.Max( 8f, Vector3.Dot( -o, d ) );
			_seeded = true;
		}

		Vector3 pos;
		int mask = SculptEditSession.EffectiveAxisMask;
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
				var world = tx.PointToWorld( _stamp.Position );
				if ( Vector3.Dot( world - cam.WorldPosition, cam.WorldRotation.Forward ) > 0f )
				{
					var px = cam.PointToScreenPixels( world );
					Mouse.Position = px;
					_warpPending = px; // verify the write actually landed before steering resumes
					_warpFrames = 0;
					_anchor = _stamp.Position; // re-anchor on the applied (possibly snapped) position
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

			// Scroll wheel = push/pull: move the anchor along the view ray (or the single held constraint
			// axis) — wheel-up pushes away. The steered position recomputes from the moved anchor below,
			// staying under the cursor by construction (free/plane) or via the axis warp (single axis).
			float wheel = Input.MouseWheel.y;
			bool wheelMoved = wheel != 0f;
			if ( wheelMoved )
				_anchor += (SculptEditSession.TrySingleAxis( mask, out var wheelAxis ) ? wheelAxis : d) * wheel * DepthStep;

			if ( SculptEditSession.TrySingleAxis( mask, out var e ) )
			{
				// Single-axis steering: the ghost slides only along the sculpture-local axis line through
				// the anchor — the position on the line closest to the cursor ray. The cursor itself is
				// then WARPED onto that (unsnapped) point, so it visibly rides the axis and releasing the
				// constraint can't snap the ghost. Steering only runs on REAL cursor movement: a read
				// sitting exactly on one of our recent warp targets is our own echo (see _axisLock) and
				// must hold still, or the pixel-floored warps feed back into self-propelled drift.
				var mp = Mouse.Position;
				bool echo = (_axisLock is { } l0 && (mp - l0).LengthSquared < 0.25f)
					|| (_axisLockPrev is { } l1 && (mp - l1).LengthSquared < 0.25f);

				// Echo (or a wheel-depth step, which already moved the anchor) → don't steer from the
				// cursor; real movement → slide along the axis to the closest point to the new ray.
				pos = (echo || wheelMoved) ? _anchor : _anchor + e * ClosestAxisT( _anchor, e, o, d );

				// Re-warp whenever the ghost may have left the cursor (steered, or wheel-moved).
				if ( !echo || wheelMoved )
				{
					var world = tx.PointToWorld( pos );
					if ( Vector3.Dot( world - cam.WorldPosition, cam.WorldRotation.Forward ) > 0f )
					{
						// Mirror the engine setter's floor+clamp exactly, so the echo check sees the same
						// integer pixel a later read returns.
						var px = cam.PointToScreenPixels( world );
						px.x = Math.Clamp( MathF.Floor( px.x ), 0f, Screen.Width - 1 );
						px.y = Math.Clamp( MathF.Floor( px.y ), 0f, Screen.Height - 1 );
						Mouse.Position = px;
						_axisLockPrev = _axisLock;
						_axisLock = px;
					}
				}
			}
			else if ( mask != 0 )
			{
				_axisLock = _axisLockPrev = null;
				// Two-axis (plane) steering: intersect the cursor ray with the plane spanned by the two
				// held axes through the anchor. The result sits ON the cursor ray, so the cursor needs no
				// warp — releasing the constraint resumes seamlessly by construction.
				int i0 = (mask & 1) != 0 ? 0 : 1;
				int i1 = (mask & 4) != 0 ? 2 : 1;
				var n = Vector3.Cross( SculptEditSession.AxisVector( i0 ), SculptEditSession.AxisVector( i1 ) ).Normal;
				float denom = Vector3.Dot( d, n );
				pos = MathF.Abs( denom ) > 1e-4f
					? o + d * (Vector3.Dot( _anchor - o, n ) / denom)
					: _anchor; // plane edge-on to the view: hold position
			}
			else
			{
				_axisLock = _axisLockPrev = null;

				// Steer by intersecting the cursor ray with the camera-parallel plane through the anchor.
				// The depth lives on the WORLD-space anchor, not at a camera distance — so zooming or
				// orbiting the camera never drags the ghost along; only steering moves it.
				float denom = Vector3.Dot( d, fwd );
				float t = denom > 1e-4f ? Vector3.Dot( _anchor - o, fwd ) / denom : -1f;
				pos = t >= 8f ? o + d * t : _anchor; // degenerate / camera zoomed past the plane: hold position
			}
			_anchor = pos;
		}

		// Shift held = rough grid snap, in sculpture-local space with the grid centred on the origin — so
		// the symmetry planes (0,0,0) are always a snappable cell. The anchor stays UNSNAPPED so steering
		// keeps its precision; only the applied position quantizes.
		if ( SculptEditSession.SnapHeld )
			pos = new Vector3(
				MathF.Round( pos.x / GridStep ) * GridStep,
				MathF.Round( pos.y / GridStep ) * GridStep,
				MathF.Round( pos.z / GridStep ) * GridStep );

		if ( (pos - _stamp.Position).LengthSquared < 0.0001f )
			return false;

		_stamp.Position = pos;
		return true;
	}

	SdfBrush NewStamp()
	{
		// Copy the last committed stamp so consecutive stamps match; first ghost of a session gets defaults.
		var b = _template is not null ? _template.Copy() : new SdfBrush
		{
			Size = SpawnSize( Shape ),
			Blend = 6f,
		};

		b.Shape = Shape;
		if ( _template is null || _template.Shape != Shape )
		{
			b.Size = SpawnSize( Shape );
			b.Rotation = SdfSculpture.SpawnRotation( Shape );
		}
		b.Operation = Operation;
		b.Points = null;
		b.Damage = false;
		return b;
	}

	// Closest point on the axis line (origin + axis·t) to a ray, returned as t — the gizmo's move-axis math.
	static float ClosestAxisT( Vector3 origin, Vector3 axis, Vector3 ro, Vector3 rd )
	{
		var w0 = origin - ro;
		float b = Vector3.Dot( axis, rd );
		float dd = Vector3.Dot( axis, w0 );
		float ee = Vector3.Dot( rd, w0 );
		float denom = 1f - b * b;
		if ( MathF.Abs( denom ) < 1e-5f )
			return -dd;
		return (b * ee - dd) / denom;
	}

	// Same per-shape spawn sizes AddBrush uses, so a stamped shape matches a stack-added one.
	static Vector3 SpawnSize( SdfShape shape ) => shape switch
	{
		SdfShape.Sphere => new Vector3( 16f ),
		SdfShape.Text => new Vector3( 24f, 12f, 4f ),
		_ => new Vector3( 12f ),
	};
}

public enum ScrubKind
{
	None,
	Blend,
	Round,
	Scale,
	Rotate,
}

/// <summary>
/// Hold parameter scrubbing, Blender-modal-style: hold A (blend), S (round), RIGHT CLICK (scale) or the
/// MIDDLE MOUSE BUTTON (rotate) and move the mouse. The held axis constraint (<see cref="SculptEditSession.AxisConstraint"/>,
/// X/C/Z keys) shapes scale and rotate: constrained scale drives the held Size components, constrained rotate spins
/// about that sculpture-local axis; Shift held snaps the rotate to the SnapDeg grid. Shared by BOTH tools — the stamp
/// ghost in add mode and the selected brush in edit mode — so the whole scheme is learned once. One scrub
/// runs at a time, game-wide (static), matching the one-cursor reality; the HUD reads
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
	static bool _aWas, _sWas, _dWas, _eWas;

	// The rotate scrub's continuous (unsnapped) orientation, sculpture-local. The mouse always drives THIS;
	// shift only changes what gets applied to the brush (the grid-snapped version of it). _grabRot is the
	// orientation at gesture start — a mid-gesture constraint change restarts the accumulation from it.
	static Rotation _rawRot = Rotation.Identity;
	static Rotation _grabRot = Rotation.Identity;
	static int _rotMask; // the constraint mask the current rotate accumulation was built under

	const float BlendPerPx = 0.06f;   // MaxBlend (15) across ~250 px
	const float RoundPerPx = 0.15f;
	const float ScaleHalfDoublePx = 240f; // px of mouse travel that doubles the size
	const float DegPerPx = 0.4f;
	const float SnapDeg = 22.5f; // half-steps between the 45s — 0 / 22.5 / 45 / 67.5 / 90…

	/// <summary>Run one frame. Returns true when the brush changed; <paramref name="ended"/> fires on the
	/// frame a scrub's key is released (edit mode commits there). Passing a null brush or allow=false ends
	/// any running scrub without changes.</summary>
	public static bool Update( SdfBrush b, Transform sculptTx, CameraComponent cam, bool allow, out bool ended )
	{
		ended = false;

		// A focused text field (the Text brush's entry) owns the keyboard — typing must never scrub.
		bool typing = Sandbox.UI.InputFocus.Current is not null;
		bool a = !typing && Input.Keyboard.Down( "a" );
		bool s = !typing && Input.Keyboard.Down( "s" );
		// Scale rides RIGHT CLICK, rotate the MIDDLE MOUSE BUTTON (both without alt — alt+RMB/MMB are the
		// camera dolly and pan).
		bool d = Input.Down( "Attack2" ) && !Input.Down( "Walk" );
		bool e = Input.Down( "CameraPan" ) && !Input.Down( "Walk" );
		bool aP = a && !_aWas, sP = s && !_sWas, dP = d && !_dWas, eP = e && !_eWas;
		_aWas = a; _sWas = s; _dWas = d; _eWas = e;

		if ( b is null || cam is null || (!allow && Active == ScrubKind.None) )
		{
			// Nothing to scrub — and if one was somehow running (brush deselected under it), stop it.
			if ( Active != ScrubKind.None ) { Active = ScrubKind.None; ended = true; }
			return false;
		}

		// Start on the key's press edge (one scrub at a time; the first claim wins).
		if ( Active == ScrubKind.None && allow )
		{
			if ( aP ) Begin( ScrubKind.Blend, b );
			else if ( sP ) Begin( ScrubKind.Round, b );
			else if ( dP ) Begin( ScrubKind.Scale, b );
			else if ( eP ) Begin( ScrubKind.Rotate, b );
		}

		if ( Active == ScrubKind.None )
			return false;

		// End when the owning key lifts.
		bool stillHeld = Active switch
		{
			ScrubKind.Blend => a,
			ScrubKind.Round => s,
			ScrubKind.Scale => d,
			ScrubKind.Rotate => e,
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
				b.Rounding = Math.Clamp( b.Rounding + delta.x * RoundPerPx, 0.75f, b.MaxRounding() );
				return true;

			case ScrubKind.Scale:
			{
				// The held axis constraint picks the components: free = uniform, else only the held axes
				// scale (one = stretch along it, two = grow the plane's dimensions together). The
				// constraint (and the guide line) are SCULPTURE axes, but Size lives in the brush's
				// ROTATED local frame — so each held sculpture axis is mapped into the brush's frame and
				// the dominant local component it lands on is what scales. That keeps "hold Z = grows the
				// way the blue line points" true for rotated brushes (and the flat shapes, whose spawn
				// rotation remaps the axes entirely).
				int mask = SculptEditSession.EffectiveAxisMask;
				int localMask = 0;
				if ( mask == 0 )
				{
					localMask = 7; // free = uniform
				}
				else
				{
					var inv = b.Rotation.Inverse;
					for ( int i = 0; i < 3; i++ )
					{
						if ( (mask & (1 << i)) == 0 )
							continue;

						var ls = inv * SculptEditSession.AxisVector( i );
						float ax = MathF.Abs( ls.x ), ay = MathF.Abs( ls.y ), az = MathF.Abs( ls.z );
						localMask |= ax >= ay && ax >= az ? 1 : ay >= az ? 2 : 4;
					}
				}

				float factor = MathF.Pow( 2f, delta.x / ScaleHalfDoublePx );
				float floor = b.Shape == SdfShape.Text ? 0.6f : 1f; // same floor the gizmo's scale handles use
				var size = b.Size;
				if ( (localMask & 1) != 0 ) size = size.WithX( MathF.Max( floor, size.x * factor ) );
				if ( (localMask & 2) != 0 ) size = size.WithY( MathF.Max( floor, size.y * factor ) );
				if ( (localMask & 4) != 0 ) size = size.WithZ( MathF.Max( floor, size.z * factor ) );
				b.Size = size;
				return true;
			}

			case ScrubKind.Rotate:
			{
				// Unconstrained: mouse X = yaw about the camera's up, mouse Y = pitch about its right —
				// gmod-familiar. With a single-axis constraint held, mouse X spins about that
				// SCULPTURE-LOCAL axis only. Pressing/releasing a constraint MID-GESTURE restarts the
				// accumulation from the orientation the gesture BEGAN with — you asked for "the initial
				// rotation, spun about this axis", not "wherever the free spin had wandered to, plus more".
				// The motion accumulates on the RAW orientation; Shift held applies it snapped ABSOLUTELY
				// to the 45° grid in the sculpture's frame (local Euler rounded) — a brush at 8° lands on
				// the grid steps (0/22.5/45…), never 8°+step. Releasing Shift returns to the raw value.
				int mask = SculptEditSession.EffectiveAxisMask;
				if ( mask != _rotMask )
				{
					_rotMask = mask;
					_rawRot = _grabRot; // constraint changed mid-gesture: restart from the grab orientation
				}

				var worldRaw = sculptTx.Rotation * _rawRot;
				if ( SculptEditSession.TrySingleAxis( mask, out var rotAxis ) )
				{
					var axis = (sculptTx.Rotation * rotAxis).Normal;
					worldRaw = Rotation.FromAxis( axis, -delta.x * DegPerPx ) * worldRaw;
				}
				else
				{
					worldRaw = Rotation.FromAxis( cam.WorldRotation.Up, -delta.x * DegPerPx )
						* Rotation.FromAxis( cam.WorldRotation.Right, -delta.y * DegPerPx )
						* worldRaw;
				}
				_rawRot = sculptTx.Rotation.Inverse * worldRaw;

				var applied = SculptEditSession.SnapHeld ? SnapToGrid( _rawRot, SnapDeg ) : _rawRot;
				if ( applied == b.Rotation )
					return false; // still in the same grid cell — no rebuild churn

				b.Rotation = applied;
				return true;
			}
		}

		return false;
	}

	// Quantize an orientation onto the angle grid: its local Euler angles each rounded to the step. Lands
	// exactly on the clean axis-aligned orientations (0/45/90…) in the sculpture's frame.
	static Rotation SnapToGrid( Rotation r, float step )
	{
		var a = r.Angles();
		return Rotation.From(
			MathF.Round( a.pitch / step ) * step,
			MathF.Round( a.yaw / step ) * step,
			MathF.Round( a.roll / step ) * step );
	}

	static void Begin( ScrubKind kind, SdfBrush b )
	{
		Active = kind;
		Anchor = Mouse.Position;
		_rawRot = b.Rotation; // rotate scrub: continuous motion accumulates from the brush's current orientation
		_grabRot = b.Rotation;
		_rotMask = SculptEditSession.EffectiveAxisMask;
	}
}
