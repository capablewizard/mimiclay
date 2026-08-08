using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>Region primitive the max-size constraint is expressed as.</summary>
public enum SculptBoundsShape
{
	Sphere,
	Box,
}

/// <summary>How the max-size region is positioned.</summary>
public enum SculptBoundsAnchor
{
	/// <summary>The region floats, centred on the sculpt's own bounds centre — what's constrained is the
	/// TOTAL EXTENT, not where the shape sits (the prop disguise: its pivot recentres on the shape anyway).
	/// There's never "one brush at fault", so invalid feedback highlights the whole sculpt.</summary>
	FollowShape,

	/// <summary>The region is fixed in sculpture-local space (the hunter head: nothing below the neck).
	/// Brushes are tested individually, so feedback can point at the exact brushes poking out.</summary>
	Fixed,
}

/// <summary>
/// Size limits for a player-editable <see cref="SdfSculpture"/> — the gameplay guard against sculpting
/// yourself huge, tiny, or (for the head) somewhere the shape shouldn't reach. Sits next to the sculpture it
/// constrains (the disguise clone root / the hunter's Head object) and is pure LOCAL config + verdict:
/// nothing here is networked or enforced by blocking tools. Enforcement is at the seams —
/// <see cref="SdfNetworkSync"/> refuses to publish (and refuses to APPLY, the anti-cheat side) an invalid
/// shape, and <see cref="SculptEditSession.PersistSlot"/> saves skip it — so the owner freely edits and SEES
/// their invalid work-in-progress while everyone else keeps the last valid shape.
///
/// Two independent checks, cached separately because their costs differ wildly:
/// <list type="bullet">
/// <item><b>Too big</b> — cheap AABB-vs-region math (blend-padded, every mirror copy), safe to re-run live
/// mid-drag. <see cref="SculptBoundsAnchor"/> picks whether the region is fixed or floats with the shape.</item>
/// <item><b>Too small</b> — the sculpt's actual clay VOLUME, sampled from the carved field at commit time
/// (never mid-drag). The voxel COLLIDER deliberately isn't the measure: it gates out detail brushes and keeps
/// grazed shapes convex, so it over-reports exactly the "big prop, 99% subtracted" case this check exists
/// for. Damage craters are excluded — being shot can't make you invalid.</item>
/// </list>
///
/// Both checks carry hysteresis so the verdict (and the feedback riding it) can't flicker while the player
/// slides along the limit. Owner feedback lives here too: a pulsing warning outline (via the
/// <see cref="SdfHighlightOutline"/> runtime override slots, the same snapshot-safe channel
/// <see cref="SdfOutlineFlash"/> uses), the region shell drawn in-world while too big, and the per-brush
/// blame list the edit wireframes tint. The persistent "Sculpt too large/small" toast is
/// <see cref="EditHud"/>'s, reading the verdict off this component.
/// </summary>
[Title( "Sculpt Bounds" )]
[Category( "Mimiclay" )]
[Icon( "select_all" )]
public sealed class SculptBounds : Component
{
	/// <summary>The sculpture being constrained. Leave null to bind to the sibling
	/// <see cref="SdfSculpture"/> on this GameObject (the normal wiring).</summary>
	[Property] public SdfSculpture Sculpture { get; set; }

	/// <summary>Region primitive for the max-size limit.</summary>
	[Property, Group( "Max Size" )] public SculptBoundsShape Shape { get; set; } = SculptBoundsShape.Sphere;

	/// <summary>Fixed region (per-brush blame) or floating with the shape (total-extent limit). See the
	/// enum values — this drives both the test and the highlight behaviour.</summary>
	[Property, Group( "Max Size" )] public SculptBoundsAnchor Anchor { get; set; } = SculptBoundsAnchor.FollowShape;

	/// <summary>Region centre in sculpture-local space. Fixed anchor only — a floating region centres
	/// itself on the sculpt's bounds centre (the same centre the orbit camera pins to).</summary>
	[Property, Group( "Max Size" )] public Vector3 Center { get; set; }

	/// <summary>Sphere region radius (sculpture units).</summary>
	[Property, Group( "Max Size" )] public float Radius { get; set; } = 64f;

	/// <summary>Box region half-extents (sculpture units).</summary>
	[Property, Group( "Max Size" )] public Vector3 Extents { get; set; } = new( 48f );

	/// <summary>Minimum total clay volume (sculpture units³) the carved shape must keep. 0 = no minimum.
	/// Tune from "a thin shell of the smallest acceptable shape", not a solid ball of it — a big hollow
	/// sculpt has surprisingly little volume but is still perfectly visible.</summary>
	[Property, Group( "Min Size" )] public float MinVolume { get; set; } = 6000f;

	/// <summary>Warning tint for all invalid feedback (outline pulse, region shell, offending wireframes).</summary>
	[Property, Group( "Feedback" )] public Color WarnColor { get; set; } = new( 1f, 0.62f, 0.1f );

	/// <summary>Outline width (screen px) while the invalid pulse drives it — authored disguise outlines are
	/// often fill-only, and the warning needs to read as a line.</summary>
	[Property, Group( "Feedback" ), Range( 0f, 8f )] public float WarnWidth { get; set; } = 3f;

	/// <summary>Invalid-pulse speed (pulses per second).</summary>
	[Property, Group( "Feedback" ), Range( 0.1f, 3f )] public float PulseFrequency { get; set; } = 0.8f;

	/// <summary>How fast the region shell fades in/out (per second, exponential) as too-big flips.</summary>
	[Property, Group( "Feedback" )] public float ShellFadeSpeed { get; set; } = 10f;

	/// <summary>DEBUG: draw the support points the max check measures — a cross per point, plus a circle
	/// where the point carries a pad radius (a sphere's radius, a rounded box's rounding, a spline's tube).
	/// This renders the EXACT set the verdict is computed from (see <see cref="CollectSupportPoints"/>),
	/// so it can never disagree with the check — unlike the HUD's Bounds silhouette, which is a coarse
	/// bounding sphere and always shows padding around non-spherical shapes.</summary>
	[Property, Group( "Feedback" )] public bool DrawSupportPoints { get; set; }

	// Hysteresis: trip when a brush/the sculpt pokes more than EnterSlack units out of the region, recover
	// once it's back within ExitSlack — so a shape dragged along the wall can't strobe the verdict. The
	// volume check recovers a few percent above the minimum for the same reason. Receive-side validation
	// (ValidateIncoming) accepts anything the owner could consider valid, i.e. the LENIENT thresholds.
	const float MaxEnterSlack = 1f;
	const float MaxExitSlack = 0.1f;
	const float VolumeExitFactor = 1.06f;

	// Volume sampling: ~4-unit cells over the sculpt's bounds, capped per axis. Commit-time only, with a
	// dual early-out (reached the target / can no longer reach it), so the common valid case touches a
	// small fraction of the grid and a genuinely tiny shape has a tiny grid to begin with. The slow case —
	// a big sculpt genuinely near the minimum — is rare and still bounded by the cap (36³ cells).
	const float VolumeCellTarget = 4f;
	const int VolumeMaxCellsPerAxis = 36;

	/// <summary>DEV: ignore every sculpt size limit on THIS machine — for authoring the game's own props
	/// in-game without the gameplay restrictions getting in the way. Toggled by <c>mimi_bounds_bypass</c>.
	/// Per-machine and local-only: while set, this machine's own checks all pass AND it accepts any
	/// incoming shape, but OTHER machines still re-validate what it broadcasts unless they bypass too.
	/// Cleared when play stops (<see cref="SessionResetSystem"/> — statics survive the editor's
	/// Stop→Play), so a forgotten toggle can't quietly leak into the next playtest.</summary>
	public static bool BypassAll { get; private set; }

	[ConCmd( "mimi_bounds_bypass" )]
	public static void ToggleBypass()
	{
		BypassAll = !BypassAll;
		Log.Info( BypassAll
			? "SculptBounds: size limits BYPASSED on this machine (mimi_bounds_bypass again to re-enforce)."
			: "SculptBounds: size limits enforced again." );
	}

	internal static void ResetBypass() => BypassAll = false;

	/// <summary>The sculpt currently exceeds the max region (with hysteresis).</summary>
	public bool TooBig { get; private set; }

	/// <summary>The carved sculpt was under <see cref="MinVolume"/> at its last commit (with hysteresis).</summary>
	public bool TooSmall { get; private set; }

	/// <summary>The verdict the network/persist seams gate on.</summary>
	public bool IsSculptValid => !TooBig && !TooSmall;

	/// <summary>Fixed anchor only: indices of the brushes poking out of the region while
	/// <see cref="TooBig"/> — the edit wireframes tint these. Empty for a floating region (there's no
	/// single culprit when the constraint is total extent — moving ANY brush moves the region).</summary>
	public IReadOnlyList<int> OffendingBrushes => _offending;

	/// <summary>The authored brush list (serialized) from the last VALID commit — what
	/// <see cref="SculptEditSession"/> reverts to when the player leaves edit mode with an incompatible
	/// shape, so nobody ever walks around wearing a shape the other machines aren't showing. Null until a
	/// valid shape has committed (a fresh spawn straight into invalid has nothing to fall back to).</summary>
	public string LastValidAuthored { get; private set; }

	readonly List<int> _offending = new();
	readonly List<SdfBrush> _shapeScratch = new();   // authored, ghost-free prefix for volume sampling
	readonly List<Vector3> _ptsScratch = new();      // per-brush hull cloud (support-point generation)
	readonly List<Vector4> _sweepScratch = new();    // spline swept spheres (support-point generation)
	readonly List<Vector4> _supportScratch = new();  // one brush's support spheres (max check)
	readonly List<Vector4> _supportDraw = new();     // debug draw: points the check counted as inside
	readonly List<Vector4> _supportDrawBad = new();  // debug draw: verified violators (drawn red)
	int _maxHash;    // last brush/config state the max check ran against
	int _volHash;    // …and the volume check (separate: volume must never re-run per frame)
	const float LiveVolumeInterval = 0.15f; // mid-drag volume re-sample cadence (see OnUpdate)

	SdfSculpture _sibling;
	SdfSculpture _hooked; // the sculpture whose Committed we're subscribed to
	RealTimeSince _sinceLiveVolume;
	SculptBoundsShell _shell;
	SculptSupportPoints _supportPoints;
	float _shellAlpha;
	bool _drivingOutlines;

	SdfSculpture Target => Sculpture.IsValid() ? Sculpture
		: (_sibling.IsValid() ? _sibling : _sibling = Components.Get<SdfSculpture>());

	/// <summary>Is this sculpt one the LOCAL player authors — a locally-owned player pawn's disguise/face,
	/// or the active edit session's target (the menu sculpt toy)? Everything else — remote pawns, host-owned
	/// bot pawns, scene DECOYS (exported disguise templates carry this component too) — gets no live
	/// checking, no warning feedback and no toast.</summary>
	public bool LocallyEditable
	{
		get
		{
			if ( IsProxy )
				return false;

			var root = GameObject.Root;
			bool pawn = root.Components.Get<HiderController>( FindMode.EverythingInSelf ).IsValid()
				|| root.Components.Get<HunterController>( FindMode.EverythingInSelf ).IsValid();
			if ( pawn )
				return !RoundManager.IsBotPawn( root );

			// No pawn above it: a decoy — unless it's what the active session is editing (menu toy /
			// future creative mode).
			return SculptEditSession.Current is { } s && s.Target == Target && s.Target.IsValid();
		}
	}

	// ── Evaluation entry points ─────────────────────────────────────────────────────────────────────────

	/// <summary>Force both checks up to date for the CURRENT brushes and return the verdict. Idempotent and
	/// hash-cached, so the seams (network commit gate, persist-slot save) can call it without caring whether
	/// this component's own hooks ran first — evaluation is pulled, never trusted to event order.</summary>
	public bool EvaluateNow()
	{
		RefreshMax();
		RefreshVolume();
		return IsSculptValid;
	}

	/// <summary>May the CURRENT live (mid-drag) shape be streamed to proxies? Max is re-checked live (it's
	/// cheap); too-small keeps the last COMMIT's verdict — volume can't be sampled at stream rate, and the
	/// commit funnel reverts proxies if a drag ends somewhere invalid.</summary>
	public bool StreamOk()
	{
		RefreshMax();
		return IsSculptValid;
	}

	/// <summary>Receive-side check for an INCOMING brush list (the anti-cheat seam — the owner's editor gate
	/// can be skipped by a modified client, this can't). Stateless: no hysteresis, no feedback, lenient
	/// thresholds so a shape any honest owner considers valid is never rejected. Damage-tail brushes are
	/// exempt, matching the owner-side checks. <paramref name="withVolume"/> only for durable commits —
	/// live stream frames get the cheap max check alone.</summary>
	public bool ValidateIncoming( List<SdfBrush> brushes, bool withVolume )
	{
		if ( BypassAll )
			return true; // dev bypass (see BypassAll) — this machine accepts anything

		if ( brushes is null )
			return false;

		int authored = AuthoredCount( brushes );
		if ( MaxProtrusion( brushes, authored, null, null, 0f ) > MaxEnterSlack )
			return false;

		if ( withVolume && MinVolume > 0f )
		{
			_shapeScratch.Clear();
			for ( int i = 0; i < authored; i++ )
				_shapeScratch.Add( brushes[i] );
			if ( !VolumeAtLeast( _shapeScratch, MinVolume ) )
				return false;
		}

		return true;
	}

	// ── Max check (cheap — safe per frame) ──────────────────────────────────────────────────────────────

	void RefreshMax()
	{
		if ( BypassAll )
		{
			TooBig = false;
			_offending.Clear();
			_maxHash = 0; // re-evaluate for real the moment the bypass lifts
			return;
		}

		var t = Target;
		if ( !t.IsValid() || t.Brushes is not { Count: > 0 } )
		{
			TooBig = false;
			_offending.Clear();
			_maxHash = 0;
			return;
		}

		int authored = t.AuthoredBrushCount;
		int hash = HashCode.Combine( ConfigHash(), SdfSculpture.ContentHashPrefix( t.Brushes, authored, 0, false ) );
		if ( hash == _maxHash )
			return; // same brushes + config ⇒ same verdict (hysteresis is deterministic on unchanged input)
		_maxHash = hash;

		// The stamp ghost IS judged, deliberately: an oversized stamp should warn while it's being SIZED,
		// not the moment after it lands (placing it stays allowed — the warning is feedback, never a
		// block). It only stays out of RecordLastValid, so a hover preview can never become the shape the
		// exit-revert falls back to.

		// Per-brush blame only exists for a FIXED region — in FollowShape mode moving ANY brush moves the
		// region, so there's no single culprit and the whole-sculpt outline carries the warning instead.
		_offending.Clear();
		var blame = Anchor == SculptBoundsAnchor.Fixed ? _offending : null;
		float worst = MaxProtrusion( t.Brushes, authored, null, blame, MaxExitSlack );
		TooBig = worst > (TooBig ? MaxExitSlack : MaxEnterSlack);
		if ( !TooBig )
			_offending.Clear(); // blame only renders while actually invalid
	}

	// Worst protrusion (units past the region surface; ≤0 = fully inside) of the additive shape.
	// FollowShape centres the region on the whole-sculpt union AABB's centre (the same TryGetBounds the
	// orbit camera pivots on, so the shell/verdict agree with where the player's view centres); Fixed uses
	// the authored Center. Two-stage test, per brush, per mirror copy:
	//  1. The shape's SUPPORT POINTS (see CollectSupportPoints) — never a bounding box/sphere, whose
	//     padding flags shapes whose visible surface is nowhere near the wall. Cheap; runs on every point.
	//  2. Any point past the wall is VERIFIED against the CARVED field (TryCarvedReach): the point may
	//     belong to a mostly-subtracted brush and the clay it marks may simply not exist any more. Only
	//     the violating points pay for field marching, so the common all-inside case costs no field work.
	// `offending` (Fixed mode) records brushes whose VERIFIED overflow exceeds `offendAt`.
	float MaxProtrusion( List<SdfBrush> brushes, int authoredCount, SdfBrush exclude, List<int> offending, float offendAt )
	{
		var regionCentre = Center;
		if ( Anchor == SculptBoundsAnchor.FollowShape )
		{
			if ( !Sdf.TryGetBounds( brushes, out var bb, exclude ) )
				return 0f;
			regionCentre = bb.Center;
		}

		bool fieldBuilt = false;
		float worst = 0f;
		for ( int i = 0; i < Math.Min( authoredCount, brushes.Count ); i++ )
		{
			var b = brushes[i];
			if ( b == exclude || !b.Enabled || b.Operation != SdfOperation.Add )
				continue; // a subtract outside the region removes nothing that matters — never constrained

			_supportScratch.Clear();
			CollectSupportPoints( b, _supportScratch, Sdf.BlendInert( brushes, b ) );

			float bw = 0f;
			foreach ( var sp in _supportScratch )
			{
				float o = PointOverflow( new Vector3( sp.x, sp.y, sp.z ), sp.w, regionCentre );
				if ( o > MaxExitSlack )
				{
					// Past the wall by the naive test — check whether that clay actually EXISTS (the
					// carved field is the truth; verify at the tightest threshold any caller compares to).
					if ( !fieldBuilt )
					{
						BuildFieldScratch( brushes, authoredCount, exclude );
						fieldBuilt = true;
					}
					o = TryCarvedReach( sp, regionCentre, out _, out float verified ) ? verified : 0f;
				}
				bw = MathF.Max( bw, o );
			}

			if ( offending is not null && bw > offendAt )
				offending.Add( i );
			worst = MathF.Max( worst, bw );
		}
		return worst;
	}

	// The carved field the verification marches: the authored prefix (damage exempt) minus the pending
	// stamp ghost. Disabled brushes are skipped by Sdf.Sample itself.
	void BuildFieldScratch( List<SdfBrush> brushes, int authoredCount, SdfBrush exclude )
	{
		_shapeScratch.Clear();
		for ( int i = 0; i < Math.Min( authoredCount, brushes.Count ); i++ )
		{
			if ( brushes[i] != exclude )
				_shapeScratch.Add( brushes[i] );
		}
	}

	// Verify one violating support point against the carved field (_shapeScratch — call BuildFieldScratch
	// first): sphere-march from just outside the padded point toward the region centre and stop at the
	// first SOLID clay on that ray. That hit is the shape's real reach there — a fully carved-away point
	// finds no clay and returns false (contributes nothing). Solid uncarved shapes converge right back to
	// their own surface (a couple of steps), so the verified result also corrects the sharp-primitive
	// cloud's rounding overshoot for free. Slightly PERMISSIVE for carved shapes whose true extreme lies
	// off-ray — the forgiving direction.
	bool TryCarvedReach( Vector4 sp, Vector3 regionCentre, out Vector3 hit, out float overflow )
	{
		hit = default;
		overflow = 0f;

		var p = new Vector3( sp.x, sp.y, sp.z );
		var dir = regionCentre - p;
		float len = dir.Length;
		if ( len < 0.01f )
			dir = Vector3.Up; // support point ON the centre — direction arbitrary, the first sample decides
		else
			dir /= len;

		var q = p - dir * sp.w; // start just outside the padded reach
		float travelled = 0f;
		float tmax = len + sp.w + 1f;
		for ( int s = 0; s < 64 && travelled <= tmax; s++ )
		{
			float d = Sdf.Sample( _shapeScratch, q );
			if ( d < 0.25f )
			{
				hit = q;
				overflow = PointOverflow( q, 0f, regionCentre );
				return true;
			}
			float step = MathF.Max( d, 0.5f );
			q += dir * step;
			travelled += step;
		}
		return false; // no clay anywhere along the ray — the support point marks carved-away space
	}

	// One brush's SUPPORT SPHERES (xyz = sculpture-local centre, w = radius pad), every mirror copy — the
	// EXACT set the max check measures, and the exact set <see cref="DrawSupportPoints"/> renders, so the
	// debug view can never disagree with the verdict. The shape's actual surface, not a bounding volume:
	// the coarse per-shape hull cloud the collision builder tessellates (slices clipped, star notches
	// kept), each point padded by the smooth-union bulge. Bounding volumes are deliberately NOT used: an
	// AABB or circumradius reads 20-70% past the visible surface for flat, rotated, sliced or off-centre
	// shapes and flagged sculpts that were nowhere near the wall. Exact for spheres and (rounded) boxes;
	// a few % PERMISSIVE on curved rims (the tessellation's points sit on the surface, the arcs between
	// them a hair outside), which errs the forgiving way. `blendInert` (see Sdf.BlendInert) drops the
	// blend pad for the first additive brush, whose blend folds against empty space and bulges nothing.
	void CollectSupportPoints( SdfBrush b, List<Vector4> dst, bool blendInert )
	{
		float pad = blendInert ? 0f : b.Blend * 0.25f; // smooth-union bulge ≈ k/4 — same as SdfBrush.AabbExtents
		Span<Vector3> signs = stackalloc Vector3[8];
		int ns = MirrorSigns( b, signs );

		// Spline: its Position/Size mean nothing — the swept spheres along the drawn curve ARE its surface.
		if ( b.Shape == SdfShape.Spline )
		{
			b.BuildSplineSweep( _sweepScratch, 1f );
			for ( int s = 0; s < ns; s++ )
			{
				foreach ( var pt in _sweepScratch )
				{
					var c = new Vector3( pt.x, pt.y, pt.z ) * signs[s];
					dst.Add( new Vector4( c.x, c.y, c.z, pt.w + pad ) );
				}
			}
			return;
		}

		// Uniform unsliced sphere: exact — centre plus radius. (Sliced falls through to the clipped cloud.)
		if ( b.Shape == SdfShape.Sphere && IsUniform( b.Size ) && b.Slice <= 0f )
		{
			for ( int s = 0; s < ns; s++ )
			{
				var c = b.Position * signs[s];
				dst.Add( new Vector4( c.x, c.y, c.z, b.Size.x + pad ) );
			}
			return;
		}

		// Rounded box: rounding is a Minkowski inset-then-dilate (inset box ⊕ sphere r), so the INSET
		// corners with the rounding as extra pad are its exact support — the sharp corners would charge
		// up to ~0.73·rounding of clay that isn't there.
		if ( b.Shape == SdfShape.Box )
		{
			float r = Math.Clamp( b.Rounding, 0f, MathF.Min( b.Size.x, MathF.Min( b.Size.y, b.Size.z ) ) );
			var e = b.Size - new Vector3( r );
			for ( int s = 0; s < ns; s++ )
			{
				for ( int c = 0; c < 8; c++ )
				{
					var lp = new Vector3(
						(c & 1) != 0 ? e.x : -e.x,
						(c & 2) != 0 ? e.y : -e.y,
						(c & 4) != 0 ? e.z : -e.z );
					var p = (b.Position + b.Rotation * lp) * signs[s];
					dst.Add( new Vector4( p.x, p.y, p.z, r + pad ) );
				}
			}
			return;
		}

		// Everything else: the collision builder's per-shape hull cloud (sharp primitive — rounding on
		// these shapes only insets further, so this stays a mild over-estimate, never an under-estimate).
		SdfCollisionBuilder.LocalPoints( b, _ptsScratch );
		for ( int s = 0; s < ns; s++ )
		{
			foreach ( var lp in _ptsScratch )
			{
				var p = (b.Position + b.Rotation * lp) * signs[s];
				dst.Add( new Vector4( p.x, p.y, p.z, pad ) );
			}
		}
	}

	// Worst overflow of one support sphere (point + pad) past the region centred at `centre` (≤0 = inside).
	float PointOverflow( Vector3 p, float pad, Vector3 centre )
	{
		if ( Shape == SculptBoundsShape.Sphere )
			return (p - centre).Length + pad - Radius;

		var rlo = centre - Extents;
		var rhi = centre + Extents;
		float o = MathF.Max( rlo.x - p.x, p.x - rhi.x );
		o = MathF.Max( o, MathF.Max( rlo.y - p.y, p.y - rhi.y ) );
		o = MathF.Max( o, MathF.Max( rlo.z - p.z, p.z - rhi.z ) );
		return o + pad;
	}

	// The brush's mirror reflection signs (identity + each enabled mirror-plane combination). Every copy is
	// judged because an asymmetric region (the head box) can be violated by the REFLECTION of a brush that
	// itself sits legally.
	static int MirrorSigns( SdfBrush b, Span<Vector3> dst )
	{
		int n = 0;
		int nx = b.EffectiveMirrorX ? 1 : 0, ny = b.EffectiveMirrorY ? 1 : 0, nz = b.EffectiveMirrorZ ? 1 : 0;
		for ( int sx = 0; sx <= nx; sx++ )
		for ( int sy = 0; sy <= ny; sy++ )
		for ( int sz = 0; sz <= nz; sz++ )
			dst[n++] = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
		return n;
	}

	static bool IsUniform( Vector3 s )
	{
		float m = MathF.Max( s.x, MathF.Max( s.y, s.z ) );
		float n = MathF.Min( s.x, MathF.Min( s.y, s.z ) );
		return m - n <= 1e-3f * MathF.Max( 1f, m );
	}

	// ── Min check (volume — commit-time only) ───────────────────────────────────────────────────────────

	void RefreshVolume()
	{
		if ( BypassAll )
		{
			TooSmall = false;
			_volHash = 0; // re-evaluate for real the moment the bypass lifts
			return;
		}

		if ( MinVolume <= 0f )
		{
			TooSmall = false;
			return;
		}

		var t = Target;
		if ( !t.IsValid() || t.Brushes is null )
		{
			TooSmall = false;
			return;
		}

		// The measured shape: the authored prefix (damage craters exempt — being shot can't invalidate
		// you), INCLUDING any pending stamp ghost — sizing a stamp should move the verdict live, exactly
		// as landing it would. Subtracts stay IN — carving away clay is the whole point.
		int authored = t.AuthoredBrushCount;
		_shapeScratch.Clear();
		for ( int i = 0; i < Math.Min( authored, t.Brushes.Count ); i++ )
			_shapeScratch.Add( t.Brushes[i] );

		int hash = HashCode.Combine( ConfigHash(),
			SdfSculpture.ContentHashPrefix( _shapeScratch, _shapeScratch.Count, 0, false ) );
		if ( hash == _volHash )
			return; // unchanged authored shape — a shot landing (damage tail) re-fires Committed but lands here
		_volHash = hash;

		float target = TooSmall ? MinVolume * VolumeExitFactor : MinVolume;
		TooSmall = !VolumeAtLeast( _shapeScratch, target );
	}

	// Does the carved field hold at least `target` units³ of clay? Cell centres of a coarse grid over the
	// shape's bounds, counted solid where the folded field is negative — the honest boolean, so "big prop,
	// 99% subtracted" measures what's actually left. Early-outs both ways (see the constants).
	bool VolumeAtLeast( List<SdfBrush> shape, float target )
	{
		if ( shape.Count == 0 || !Sdf.TryGetBounds( shape, out var bb ) )
			return false;

		var span = bb.Maxs - bb.Mins;
		double bbVol = (double)span.x * span.y * span.z;
		if ( bbVol < target )
			return false; // the solid can't exceed its own bounds

		int nx = Math.Clamp( (int)MathF.Ceiling( span.x / VolumeCellTarget ), 2, VolumeMaxCellsPerAxis );
		int ny = Math.Clamp( (int)MathF.Ceiling( span.y / VolumeCellTarget ), 2, VolumeMaxCellsPerAxis );
		int nz = Math.Clamp( (int)MathF.Ceiling( span.z / VolumeCellTarget ), 2, VolumeMaxCellsPerAxis );
		var cell = new Vector3( span.x / nx, span.y / ny, span.z / nz );
		double cellVol = (double)cell.x * cell.y * cell.z;

		int needed = (int)Math.Ceiling( target / cellVol );
		int solid = 0;
		int remaining = nx * ny * nz;

		for ( int iz = 0; iz < nz; iz++ )
		for ( int iy = 0; iy < ny; iy++ )
		for ( int ix = 0; ix < nx; ix++ )
		{
			if ( solid + remaining < needed )
				return false; // even all-solid from here can't reach the target
			remaining--;

			var p = new Vector3(
				bb.Mins.x + (ix + 0.5f) * cell.x,
				bb.Mins.y + (iy + 0.5f) * cell.y,
				bb.Mins.z + (iz + 0.5f) * cell.z );
			if ( Sdf.Sample( shape, p ) >= 0f )
				continue;

			if ( ++solid >= needed )
				return true;
		}

		return solid >= needed;
	}

	int ConfigHash() => HashCode.Combine( (int)Shape, (int)Anchor, Center, Radius, Extents, MinVolume );

	static int AuthoredCount( List<SdfBrush> brushes )
	{
		for ( int i = 0; i < brushes.Count; i++ )
		{
			if ( brushes[i].Damage )
				return i;
		}
		return brushes.Count;
	}

	/// <summary>Editor tuning aid: measure and log the current sculpt's clay volume (full count, no
	/// early-out) AND its worst max-region protrusion, so <see cref="MinVolume"/> and the region can be
	/// set from real shapes — and so a surprising verdict can be traced to actual numbers.</summary>
	[Button( "Log Clay Volume" )]
	public void LogVolume()
	{
		var t = Target;
		if ( !t.IsValid() || t.Brushes is null )
			return;

		int authored = t.AuthoredBrushCount;
		_shapeScratch.Clear();
		for ( int i = 0; i < authored; i++ )
			_shapeScratch.Add( t.Brushes[i] );

		float protrusion = MaxProtrusion( t.Brushes, authored, null, null, 0f );
		Log.Info( $"SculptBounds: max-region protrusion {protrusion:+0.0;-0.0} " +
			$"(> {MaxEnterSlack} = too big; {Shape}/{Anchor})." );

		if ( !Sdf.TryGetBounds( _shapeScratch, out var bb ) )
		{
			Log.Info( "SculptBounds: no additive brushes — volume 0." );
			return;
		}

		var span = bb.Maxs - bb.Mins;
		int nx = Math.Clamp( (int)MathF.Ceiling( span.x / VolumeCellTarget ), 2, VolumeMaxCellsPerAxis );
		int ny = Math.Clamp( (int)MathF.Ceiling( span.y / VolumeCellTarget ), 2, VolumeMaxCellsPerAxis );
		int nz = Math.Clamp( (int)MathF.Ceiling( span.z / VolumeCellTarget ), 2, VolumeMaxCellsPerAxis );
		var cell = new Vector3( span.x / nx, span.y / ny, span.z / nz );
		double cellVol = (double)cell.x * cell.y * cell.z;

		int solid = 0;
		for ( int iz = 0; iz < nz; iz++ )
		for ( int iy = 0; iy < ny; iy++ )
		for ( int ix = 0; ix < nx; ix++ )
		{
			var p = new Vector3(
				bb.Mins.x + (ix + 0.5f) * cell.x,
				bb.Mins.y + (iy + 0.5f) * cell.y,
				bb.Mins.z + (iz + 0.5f) * cell.z );
			if ( Sdf.Sample( _shapeScratch, p ) < 0f )
				solid++;
		}

		Log.Info( $"SculptBounds: clay volume ≈ {solid * cellVol:0} units³ " +
			$"({nx}×{ny}×{nz} cells of {cell.x:0.0}×{cell.y:0.0}×{cell.z:0.0}; MinVolume = {MinVolume:0})." );
	}

	// ── Commit hook (volume refresh cadence) ────────────────────────────────────────────────────────────

	protected override void OnEnabled() => EnsureHooked(); // don't miss the spawn-load commit (OnUpdate hooks late)

	void EnsureHooked()
	{
		var t = Target;
		if ( t == _hooked )
			return;

		if ( _hooked is not null )
			_hooked.Committed -= OnSculptCommitted;
		_hooked = t;
		if ( _hooked.IsValid() )
			_hooked.Committed += OnSculptCommitted;
	}

	protected override void OnDestroy()
	{
		if ( _hooked is not null )
			_hooked.Committed -= OnSculptCommitted; // plain null check: unsubscribe even mid-teardown
		_hooked = null;
		_shell?.Hide();
		_supportPoints?.Hide();
	}

	protected override void OnDisabled()
	{
		_shell?.Hide();
		_supportPoints?.Hide();
	}

	void OnSculptCommitted()
	{
		// Owner-side verdict cadence only. Proxies applying a remote commit also fire Committed (their
		// validation is SdfNetworkSync's receive gate), and decoys rebuild once at scene load — neither
		// should pay for a volume sample. The network/persist seams don't rely on this hook at all: they
		// PULL the verdict through EvaluateNow.
		if ( !LocallyEditable )
			return;
		RefreshMax(); // hash-cached — just makes sure the verdict below isn't a frame stale
		RefreshVolume();
		if ( IsSculptValid )
			RecordLastValid();
	}

	// Snapshot the authored shape (ghost-free, damage exempt) as the exit-revert fallback. Valid commits
	// only — see LastValidAuthored.
	void RecordLastValid()
	{
		var t = Target;
		if ( !t.IsValid() || t.Brushes is null )
			return;

		var ghost = SculptEditSession.PendingStamp( t );
		int authored = t.AuthoredBrushCount;
		_shapeScratch.Clear();
		for ( int i = 0; i < Math.Min( authored, t.Brushes.Count ); i++ )
		{
			if ( t.Brushes[i] != ghost )
				_shapeScratch.Add( t.Brushes[i] );
		}

		if ( _shapeScratch.Count > 0 )
			LastValidAuthored = SdfSculpture.SerializeBrushes( _shapeScratch );
	}

	// ── Owner feedback ──────────────────────────────────────────────────────────────────────────────────

	protected override void OnUpdate()
	{
		EnsureHooked();

		// Feedback (and live max re-checking) is for the machine whose player authors this sculpt —
		// see LocallyEditable for what that excludes (proxies, bots, decoys).
		bool owner = LocallyEditable;
		if ( owner )
		{
			RefreshMax();

			// Live min-size: volume normally refreshes on COMMIT, but a scale-down drag should warn the
			// moment the clay dips under the minimum, not at release — so re-sample on a small throttle
			// too. The content-hash guard inside makes the idle case free; mid-drag this samples the
			// carved field a few times a second, each pass early-outing at the threshold.
			if ( _sinceLiveVolume > LiveVolumeInterval )
			{
				_sinceLiveVolume = 0f;
				RefreshVolume();
			}
		}

		bool invalid = owner && !IsSculptValid;
		DriveOutlines( invalid );
		DriveShell( owner && TooBig );
		DriveSupportDebug();
	}

	// DEBUG: render the support-sphere set the max check measures, AFTER the same carved-field
	// verification the verdict applies — a point whose clay was subtracted away disappears, and a genuine
	// violator is drawn at its marched reach (where solid clay actually starts on its ray). What's on
	// screen is therefore exactly what the check counted, never the raw uncarved cloud. Driven by the
	// inspector toggle alone — no owner gate, so a shape can be inspected wherever the component is on.
	void DriveSupportDebug()
	{
		var t = Target;
		if ( !DrawSupportPoints || !t.IsValid() || t.Brushes is null || Scene?.Camera is null )
		{
			_supportPoints?.Hide();
			return;
		}

		var brushes = t.Brushes;
		int authored = t.AuthoredBrushCount;

		// Ghost included throughout, matching the check — its support points draw (and judge) live while
		// a stamp is being sized.
		var regionCentre = Center;
		if ( Anchor == SculptBoundsAnchor.FollowShape && Sdf.TryGetBounds( brushes, out var bb ) )
			regionCentre = bb.Center;

		bool fieldBuilt = false;
		_supportDraw.Clear();
		_supportDrawBad.Clear();
		for ( int i = 0; i < Math.Min( authored, brushes.Count ); i++ )
		{
			var b = brushes[i];
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue;

			_supportScratch.Clear();
			CollectSupportPoints( b, _supportScratch, Sdf.BlendInert( brushes, b ) );
			foreach ( var sp in _supportScratch )
			{
				if ( PointOverflow( new Vector3( sp.x, sp.y, sp.z ), sp.w, regionCentre ) <= MaxExitSlack )
				{
					_supportDraw.Add( sp );
					continue;
				}

				if ( !fieldBuilt )
				{
					BuildFieldScratch( brushes, authored, null );
					fieldBuilt = true;
				}
				// Verified violators (real clay past the wall) go RED; a hit the carve pulled back inside
				// is fine and stays the normal colour. No clay on the ray → the check counted nothing →
				// draw nothing.
				if ( TryCarvedReach( sp, regionCentre, out var hit, out float overflow ) )
					(overflow > MaxExitSlack ? _supportDrawBad : _supportDraw)
						.Add( new Vector4( hit.x, hit.y, hit.z, 0f ) );
			}
		}

		_supportPoints ??= new SculptSupportPoints();
		_supportPoints.Draw( Scene, Scene.Camera, t.WorldTransform,
			_supportDraw, WarnColor, _supportDrawBad, InvalidPointColor );
	}

	// Verified violating support points in the debug view — clay the check counted PAST the wall.
	static readonly Color InvalidPointColor = new( 0.92f, 0.18f, 0.12f );

	// Pulse the sculpt's highlight outline(s) in the warning colour while invalid — through the runtime
	// OVERRIDE slots, never the authored [Property] colours (snapshot-carries-live-state: authored values
	// ride the spawn snapshot onto proxies; overrides don't). Scoped to the CONSTRAINED SCULPTURE's own
	// subtree, never the pawn root — the hunter's root outline covers its body/hands/gun for the hunt
	// glow, and the warning must only paint the sculpt the bounds actually judge (the head-scoped outline
	// there; the disguise's own outline for props). Visibility (Hidden) is RoundOutlineSystem's call in
	// round/lobby scenes — it shows an invalid owner's warning outline itself; only in manager-less
	// scenes (menu sculpt toy) do we assert it shown here.
	void DriveOutlines( bool on )
	{
		// The Reveal flash drives the same override slots; it only runs while RoundOutlineSystem enables it,
		// and a survivor's shape is valid by construction — but never fight it for the slots regardless.
		if ( on && GameObject.Root.Components.Get<SdfOutlineFlash>( FindMode.EnabledInSelfAndDescendants ).IsValid() )
			on = false;

		var scope = Target.IsValid() ? Target.GameObject : GameObject;

		// WarningOnly outlines (the hunter's head-scoped one) live exactly as long as the warning: enabled
		// while it shows, disabled otherwise so the pawn's MAIN outline group re-absorbs their renderers —
		// two live groups read each other's surfaces as occluders (gun glowing through the head). Asserted
		// every frame on EVERY machine: the spawn snapshot can ship a live 'enabled' to a late joiner, and
		// proxies must put it back. (Toggling Enabled is safe here despite the round system's Hidden-only
		// rule — SculptBounds finds these with an Everything scan, so a disabled one is never lost.)
		foreach ( var o in scope.Components.GetAll<SdfHighlightOutline>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( o.WarningOnly && o.Enabled != on )
				o.Enabled = on;
		}

		if ( !on )
		{
			if ( !_drivingOutlines )
				return;
			_drivingOutlines = false;
			foreach ( var o in scope.Components.GetAll<SdfHighlightOutline>( FindMode.EverythingInSelfAndDescendants ) )
			{
				o.ColorOverride = null;
				o.ObscuredColorOverride = null;
				o.InsideColorOverride = null;
				o.InsideObscuredColorOverride = null;
				o.WidthOverride = null;
			}
			return;
		}

		_drivingOutlines = true;
		float wave = (1f + MathF.Cos( Time.Now * PulseFrequency * MathF.Tau )) * 0.5f;
		float a = 0.35f + 0.65f * wave; // breathes, never disappears — it's a standing warning, not a flash
		bool assertShown = !RoundManager.Current.IsValid() && !LobbyManager.Current.IsValid();

		foreach ( var o in scope.Components.GetAll<SdfHighlightOutline>( FindMode.EnabledInSelfAndDescendants ) )
		{
			o.ColorOverride = WarnColor.WithAlpha( WarnColor.a * a );
			o.ObscuredColorOverride = WarnColor.WithAlpha( 0.5f * a );
			o.InsideColorOverride = WarnColor.WithAlpha( 0.22f * a );
			o.InsideObscuredColorOverride = WarnColor.WithAlpha( 0.22f * a );
			o.WidthOverride = WarnWidth;
			if ( assertShown )
				o.Hidden = false;
		}
	}

	// The region drawn in-world while the sculpt is too big — where the wall is, so "get back inside it"
	// is visible rather than told. Faded in/out so it only exists while relevant.
	void DriveShell( bool on )
	{
		_shellAlpha = MathX.Lerp( _shellAlpha, on ? 1f : 0f, 1f - MathF.Exp( -ShellFadeSpeed * Time.Delta ) );

		var t = Target;
		if ( _shellAlpha <= 0.01f || !t.IsValid() || Scene?.Camera is null )
		{
			_shell?.Hide();
			return;
		}

		// Floating region: centred on the same bounds centre the max check judges against — ghost
		// INCLUDED, so while an oversized stamp is being sized the shell shows the wall exactly where the
		// verdict measures it. (The orbit camera's pivot stays ghost-excluded — that's a view rule, and
		// the shell only exists while too-big anyway.)
		var centre = Center;
		if ( Anchor == SculptBoundsAnchor.FollowShape )
			centre = Sdf.TryGetBounds( t.Brushes, out var bb ) ? bb.Center : Vector3.Zero;

		float wave = (1f + MathF.Cos( Time.Now * PulseFrequency * MathF.Tau )) * 0.5f;
		float alpha = _shellAlpha * (0.55f + 0.45f * wave);

		_shell ??= new SculptBoundsShell();
		_shell.Draw( Scene, Scene.Camera, t.WorldTransform, Shape, centre, Radius, Extents,
			WarnColor.WithAlpha( WarnColor.a * alpha ), thicknessPx: 2.5f );
	}

	// ── Editor authoring gizmo ──────────────────────────────────────────────────────────────────────────

	protected override void DrawGizmos()
	{
		if ( !Gizmo.IsSelected && !Gizmo.IsHovered )
			return;

		Gizmo.Draw.Color = WarnColor.WithAlpha( Gizmo.IsSelected ? 0.9f : 0.4f );

		// Fixed: the authored region. FollowShape: previewed around the sculpture's CURRENT bounds centre
		// so the author sees the allowance against the seed shape.
		var centre = Center;
		if ( Anchor == SculptBoundsAnchor.FollowShape )
		{
			var t = Target;
			if ( t.IsValid() && t.Brushes is not null && Sdf.TryGetBounds( t.Brushes, out var bb ) )
				centre = bb.Center;
		}

		if ( Shape == SculptBoundsShape.Box )
			Gizmo.Draw.LineBBox( new BBox( centre - Extents, centre + Extents ) );
		else
			Gizmo.Draw.LineSphere( new Sphere( centre, Radius ) );
	}
}

/// <summary>
/// In-world wireframe of a <see cref="SculptBounds"/> region — a box's 12 edges or a sphere's three great
/// circles, meshed as thin screen-constant tubes exactly like <see cref="BrushWireframes"/> (same shader,
/// same overlay-with-depth layer), rebuilt only when the view or region changes.
/// </summary>
sealed class SculptBoundsShell
{
	const int Seg = 48; // segments per great circle
	static readonly float Tau = MathF.PI * 2f;

	readonly List<Vertex> _verts = new();
	readonly List<int> _indices = new();
	Vector3 _bbMin, _bbMax;
	SceneObject _so;
	Material _material;
	int _lastHash;

	Transform _tx;
	Vector3 _camPos;
	float _wpp;
	float _thicknessPx;
	Color _col;

	public void Draw( Scene scene, CameraComponent cam, Transform sculptTx, SculptBoundsShape shape,
		Vector3 centre, float radius, Vector3 extents, Color col, float thicknessPx )
	{
		if ( scene is null || cam is null )
		{
			Hide();
			return;
		}

		int hash = HashCode.Combine(
			HashCode.Combine( sculptTx.Position, sculptTx.Rotation, (int)shape, centre, radius, extents ),
			HashCode.Combine( cam.WorldPosition, col, thicknessPx ) );
		if ( hash == _lastHash && _so.IsValid() )
			return;
		_lastHash = hash;

		_verts.Clear();
		_indices.Clear();
		_bbMin = new Vector3( float.MaxValue );
		_bbMax = new Vector3( float.MinValue );
		_tx = sculptTx;
		_camPos = cam.WorldPosition;
		_wpp = WorldPerPixel( cam );
		_thicknessPx = MathF.Max( 0.1f, thicknessPx );
		_col = col;

		if ( shape == SculptBoundsShape.Box )
		{
			Span<Vector3> c = stackalloc Vector3[8];
			for ( int i = 0; i < 8; i++ )
				c[i] = centre + new Vector3(
					(i & 1) != 0 ? extents.x : -extents.x,
					(i & 2) != 0 ? extents.y : -extents.y,
					(i & 4) != 0 ? extents.z : -extents.z );

			// Bottom ring, top ring, verticals — indices differ by bit per axis.
			Edge( c[0], c[1] ); Edge( c[1], c[3] ); Edge( c[3], c[2] ); Edge( c[2], c[0] );
			Edge( c[4], c[5] ); Edge( c[5], c[7] ); Edge( c[7], c[6] ); Edge( c[6], c[4] );
			Edge( c[0], c[4] ); Edge( c[1], c[5] ); Edge( c[3], c[7] ); Edge( c[2], c[6] );
		}
		else
		{
			Circle( centre, radius, ( s, cs ) => new Vector3( cs, s, 0f ) );
			Circle( centre, radius, ( s, cs ) => new Vector3( cs, 0f, s ) );
			Circle( centre, radius, ( s, cs ) => new Vector3( 0f, cs, s ) );
		}

		Upload( scene );
	}

	public void Hide()
	{
		_so?.Delete();
		_so = null;
		_lastHash = 0;
	}

	void Circle( Vector3 centre, float radius, Func<float, float, Vector3> basis )
	{
		var prev = centre + basis( 0f, radius );
		for ( int k = 1; k <= Seg; k++ )
		{
			float a = k / (float)Seg * Tau;
			var cur = centre + basis( MathF.Sin( a ) * radius, MathF.Cos( a ) * radius );
			Edge( prev, cur );
			prev = cur;
		}
	}

	// Sculpture-local edge → thin world-space tube (same construction as BrushWireframes.EdgeWorld).
	void Edge( Vector3 la, Vector3 lb )
	{
		var a = _tx.PointToWorld( la );
		var b = _tx.PointToWorld( lb );
		var d = b - a;
		float len = d.Length;
		if ( len < 0.001f )
			return;
		d /= len;

		float halfW = _thicknessPx * 0.5f * (_camPos - (a + b) * 0.5f).Length * _wpp;
		var up = MathF.Abs( d.z ) < 0.9f ? Vector3.Up : Vector3.Forward;
		var right = Vector3.Cross( d, up ).Normal * halfW;
		up = Vector3.Cross( right, d ).Normal * halfW;

		int a0 = Add( a - right - up ), a1 = Add( a + right - up ), a2 = Add( a + right + up ), a3 = Add( a - right + up );
		int b0 = Add( b - right - up ), b1 = Add( b + right - up ), b2 = Add( b + right + up ), b3 = Add( b - right + up );

		Quad( a0, a1, b1, b0 );
		Quad( a1, a2, b2, b1 );
		Quad( a2, a3, b3, b2 );
		Quad( a3, a0, b0, b3 );
	}

	static float WorldPerPixel( CameraComponent cam )
	{
		var c = Screen.Size * 0.5f;
		var r0 = cam.ScreenPixelToRay( c );
		var r1 = cam.ScreenPixelToRay( c + new Vector2( 0f, 1f ) );
		return (r1.Forward - r0.Forward).Length;
	}

	int Add( Vector3 p )
	{
		_bbMin = Vector3.Min( _bbMin, p );
		_bbMax = Vector3.Max( _bbMax, p );
		_verts.Add( new Vertex( p, Vector3.Up, Vector3.Forward, Vector4.Zero ) { Color = _col } );
		return _verts.Count - 1;
	}

	void Quad( int a, int b, int c, int d )
	{
		_indices.Add( a ); _indices.Add( b ); _indices.Add( c );
		_indices.Add( a ); _indices.Add( c ); _indices.Add( d );
	}

	void Upload( Scene scene )
	{
		if ( _verts.Count < 3 )
		{
			Hide();
			return;
		}

		_material ??= Material.FromShader( "shaders/gizmo_depth.shader" );

		var mesh = new Mesh( _material );
		mesh.CreateVertexBuffer( _verts.Count, _verts );
		mesh.CreateIndexBuffer( _indices.Count, _indices );
		mesh.Bounds = new BBox( _bbMin, _bbMax );

		var model = new ModelBuilder().AddMesh( mesh ).Create();

		if ( _so.IsValid() )
		{
			_so.Model = model;
		}
		else
		{
			_so = new SceneObject( scene.SceneWorld, model );
			_so.Transform = Transform.Zero;
			_so.Flags.CastShadows = false;
			_so.Batchable = false;
			_so.RenderLayer = SceneRenderLayer.OverlayWithDepth; // depth-tested: the world occludes the far side
		}

		_so.Attributes.Set( "DepthBias", 0.001f ); // a hair toward the camera so it isn't eaten by the sculpt skin
		_so.Bounds = new BBox( _bbMin, _bbMax );
		_so.RenderingEnabled = true;
	}
}

/// <summary>
/// DEBUG renderer for <see cref="SculptBounds.DrawSupportPoints"/>: each support sphere the max check
/// measures (xyz = sculpture-local centre, w = pad radius) drawn as a screen-constant cross, plus two
/// great circles where the pad radius is real (a sphere's radius, a rounded box's rounding, a spline's
/// tube) — so what the check "sees" is visible against the sculpt's surface. Same tube-mesh construction
/// and overlay layer as <see cref="SculptBoundsShell"/>.
/// </summary>
sealed class SculptSupportPoints
{
	const int Seg = 16;          // segments per pad circle
	const int MaxPoints = 4096;  // draw cap — a runaway spline sweep shouldn't build a monster mesh
	const float CrossPx = 5f;    // cross half-length, screen px
	const float ThicknessPx = 1.5f;
	static readonly float Tau = MathF.PI * 2f;

	readonly List<Vertex> _verts = new();
	readonly List<int> _indices = new();
	Vector3 _bbMin, _bbMax;
	SceneObject _so;
	Material _material;
	int _lastHash;

	Vector3 _camPos;
	float _wpp;
	Color _col;
	Transform _tx;

	public void Draw( Scene scene, CameraComponent cam, Transform sculptTx,
		List<Vector4> points, Color col, List<Vector4> invalidPoints = null, Color invalidCol = default )
	{
		int total = (points?.Count ?? 0) + (invalidPoints?.Count ?? 0);
		if ( scene is null || cam is null || total == 0 )
		{
			Hide();
			return;
		}

		var hc = new HashCode();
		hc.Add( sculptTx.Position );
		hc.Add( sculptTx.Rotation );
		hc.Add( cam.WorldPosition );
		hc.Add( col );
		hc.Add( invalidCol );
		if ( points is not null )
			foreach ( var p in points )
				hc.Add( p );
		if ( invalidPoints is not null )
			foreach ( var p in invalidPoints )
				hc.Add( p );
		int hash = hc.ToHashCode();
		if ( hash == _lastHash && _so.IsValid() )
			return;
		_lastHash = hash;

		_verts.Clear();
		_indices.Clear();
		_bbMin = new Vector3( float.MaxValue );
		_bbMax = new Vector3( float.MinValue );
		_camPos = cam.WorldPosition;
		_wpp = WorldPerPixel( cam );
		_tx = sculptTx;

		// Valid points in the base colour, verified violators in theirs — the shared MaxPoints budget is
		// spent on the violators FIRST (they're what's being debugged; never let a dense valid cloud
		// starve them out of the draw).
		int budget = MaxPoints;
		_col = invalidCol;
		budget -= AddPoints( invalidPoints, budget );
		_col = col;
		AddPoints( points, budget );

		Upload( scene );
	}

	// Draw up to `budget` of the list's support spheres in the current colour; returns how many it drew.
	int AddPoints( List<Vector4> points, int budget )
	{
		if ( points is null )
			return 0;

		int n = Math.Min( points.Count, budget );
		for ( int i = 0; i < n; i++ )
		{
			var sp = points[i];
			var c = _tx.PointToWorld( new Vector3( sp.x, sp.y, sp.z ) );

			// Screen-constant cross at the support centre — always visible, whatever the pad.
			float h = CrossPx * (_camPos - c).Length * _wpp;
			Edge( c - new Vector3( h, 0, 0 ), c + new Vector3( h, 0, 0 ) );
			Edge( c - new Vector3( 0, h, 0 ), c + new Vector3( 0, h, 0 ) );
			Edge( c - new Vector3( 0, 0, h ), c + new Vector3( 0, 0, h ) );

			// The pad radius as two world-space great circles (world-scale on purpose — the pad is real
			// clay reach, not a marker). Skipped when it's effectively zero (an unblended hull point).
			float r = sp.w * _tx.UniformScale;
			if ( r < 0.3f )
				continue;
			Circle( c, r, ( s, cs ) => new Vector3( cs, s, 0f ) );
			Circle( c, r, ( s, cs ) => new Vector3( cs, 0f, s ) );
		}
		return n;
	}

	public void Hide()
	{
		_so?.Delete();
		_so = null;
		_lastHash = 0;
	}

	void Circle( Vector3 centre, float radius, Func<float, float, Vector3> basis )
	{
		var prev = centre + basis( 0f, radius );
		for ( int k = 1; k <= Seg; k++ )
		{
			float a = k / (float)Seg * Tau;
			var cur = centre + basis( MathF.Sin( a ) * radius, MathF.Cos( a ) * radius );
			Edge( prev, cur );
			prev = cur;
		}
	}

	// World-space edge → thin screen-constant tube (same construction as SculptBoundsShell).
	void Edge( Vector3 a, Vector3 b )
	{
		var d = b - a;
		float len = d.Length;
		if ( len < 0.001f )
			return;
		d /= len;

		float halfW = ThicknessPx * 0.5f * (_camPos - (a + b) * 0.5f).Length * _wpp;
		var up = MathF.Abs( d.z ) < 0.9f ? Vector3.Up : Vector3.Forward;
		var right = Vector3.Cross( d, up ).Normal * halfW;
		up = Vector3.Cross( right, d ).Normal * halfW;

		int a0 = Add( a - right - up ), a1 = Add( a + right - up ), a2 = Add( a + right + up ), a3 = Add( a - right + up );
		int b0 = Add( b - right - up ), b1 = Add( b + right - up ), b2 = Add( b + right + up ), b3 = Add( b - right + up );

		Quad( a0, a1, b1, b0 );
		Quad( a1, a2, b2, b1 );
		Quad( a2, a3, b3, b2 );
		Quad( a3, a0, b0, b3 );
	}

	static float WorldPerPixel( CameraComponent cam )
	{
		var c = Screen.Size * 0.5f;
		var r0 = cam.ScreenPixelToRay( c );
		var r1 = cam.ScreenPixelToRay( c + new Vector2( 0f, 1f ) );
		return (r1.Forward - r0.Forward).Length;
	}

	int Add( Vector3 p )
	{
		_bbMin = Vector3.Min( _bbMin, p );
		_bbMax = Vector3.Max( _bbMax, p );
		_verts.Add( new Vertex( p, Vector3.Up, Vector3.Forward, Vector4.Zero ) { Color = _col } );
		return _verts.Count - 1;
	}

	void Quad( int a, int b, int c, int d )
	{
		_indices.Add( a ); _indices.Add( b ); _indices.Add( c );
		_indices.Add( a ); _indices.Add( c ); _indices.Add( d );
	}

	void Upload( Scene scene )
	{
		if ( _verts.Count < 3 )
		{
			Hide();
			return;
		}

		_material ??= Material.FromShader( "shaders/gizmo_depth.shader" );

		var mesh = new Mesh( _material );
		mesh.CreateVertexBuffer( _verts.Count, _verts );
		mesh.CreateIndexBuffer( _indices.Count, _indices );
		mesh.Bounds = new BBox( _bbMin, _bbMax );

		var model = new ModelBuilder().AddMesh( mesh ).Create();

		if ( _so.IsValid() )
		{
			_so.Model = model;
		}
		else
		{
			_so = new SceneObject( scene.SceneWorld, model );
			_so.Transform = Transform.Zero;
			_so.Flags.CastShadows = false;
			_so.Batchable = false;
			_so.RenderLayer = SceneRenderLayer.OverlayWithDepth;
		}

		_so.Attributes.Set( "DepthBias", 0.002f ); // through the sculpt skin, like the brush wireframes
		_so.Bounds = new BBox( _bbMin, _bbMax );
		_so.RenderingEnabled = true;
	}
}
