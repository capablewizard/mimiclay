using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// ── The grab (creative's gravity-gun pickup) ─────────────────────────────────────────────────────────────────
// Holding RMB on claimable clay carries it: the prop floats in front of the holder, E+mouse rotates it, R+mouse
// scales it, the wheel sets the distance; releasing RMB drops it and LMB FREEZES it. A dropped prop becomes LOOSE —
// a free rigidbody (engine gravity, all rotation unlocked) instead of the upright character body released props
// normally are — so it tumbles, stacks and rolls. That's the point: a way to throw clay at the world and watch
// what the collider actually does. A frozen prop is left exactly where it was with motion off (FrozenProps) —
// a fixed obstacle to drop things onto; grabbing it again unfreezes it.
//
// HOST-SIMULATED, like all released scenery. The prop never changes owner: the holder computes where they want
// it (root position + rotation) and streams that to the host every frame (UpdateGrab, unreliable), and the
// host's HiderController chases it with velocities — so the held prop still collides with the world on its way,
// and keeps its momentum the moment it's let go (a swing becomes a throw). Holding one costs the host nothing it
// isn't already doing for scenery; a client holder sees their own hold one round-trip late.
//
// SCALE is applied to the SHAPE, never the transform: the raymarcher folds samples through position + rotation
// only, so a scaled transform would grow the collider and leave the clay its old size. The host rescales the
// brushes with the group rule (SdfScaling.ScaleUniform about the clay's centre — every length, so the prop stays
// identical apart from its size), from a snapshot taken when the scaling started (so shrinking and growing back
// round-trips exactly). Live frames preview (RebuildShadowProxy → the sync's stream); once the factor holds still
// for a moment, or the prop is dropped, it commits (Rebuild → collider, durable publish).
//
// Scene clay is converted on the first grab exactly as an in-place sculpt converts it (host-owned released
// scenery), so everything after that only ever deals with prop pawns.
public sealed partial class PropClaims
{
	const string GrabOption = "claims.grab";

	/// <summary>May props be grabbed right now (claims open AND the host mode allows the grab)?</summary>
	public bool GrabOpen => ClaimsOpen && (Host?.GrabAllowed ?? false);

	/// <summary>Who is holding what: connection id → pawn GameObject id. One prop per player.</summary>
	[Sync] public NetDictionary<Guid, Guid> Held { get; private set; } = new();

	/// <summary>Props that were dropped from a grab and now simulate as free rigidbodies, by pawn GameObject id.
	/// Cleared when someone possesses the prop (it stands back up — see <see cref="HiderController.SettleUpright"/>).</summary>
	[Sync] public NetDictionary<Guid, bool> LooseProps { get; private set; } = new();

	/// <summary>Is anyone holding this prop?</summary>
	public static bool IsHeld( HiderController hider )
	{
		if ( !hider.IsValid() || !Current.IsValid() )
			return false;
		var id = hider.GameObject.Id;
		foreach ( var (_, pawn) in Current.Held )
		{
			if ( pawn == id )
				return true;
		}
		return false;
	}

	/// <summary>Props FROZEN from a grab (LMB while holding): motion off where they were let go — solid, but no
	/// physics at all. Grabbing one again unfreezes it; possessing one clears it.</summary>
	[Sync] public NetDictionary<Guid, bool> FrozenProps { get; private set; } = new();

	/// <summary>Is this prop loose — a free rigidbody since it was last dropped?</summary>
	public static bool IsLoose( HiderController hider )
		=> hider.IsValid() && Current.IsValid() && Current.LooseProps.ContainsKey( hider.GameObject.Id );

	/// <summary>Is this prop frozen in place (see <see cref="FrozenProps"/>)?</summary>
	public static bool IsFrozen( HiderController hider )
		=> hider.IsValid() && Current.IsValid() && Current.FrozenProps.ContainsKey( hider.GameObject.Id );

	/// <summary>The prop this machine's player holds (registry answer — the host may have refused or ended it).</summary>
	public static GameObject LocalHeld
		=> Current.IsValid() && Connection.Local is { } me && Current.Held.TryGetValue( me.Id, out var pawn )
			? Current.Scene.Directory.FindByGuid( pawn )
			: null;

	/// <summary>Is this machine's player holding (or asking to hold) a prop? E and R belong to the grab then.</summary>
	public static bool LocalGrabActive
		=> LocalGrabPending || (Current.IsValid() && Connection.Local is { } me && Current.Held.ContainsKey( me.Id ));

	/// <summary>Set by the hunter the instant it asks for a grab, until the host's row lands (or it times out).</summary>
	internal static bool LocalGrabPending => _grabPendingSince < 1f;
	static RealTimeSince _grabPendingSince = float.MaxValue;
	internal static void MarkGrabPending( bool pending ) => _grabPendingSince = pending ? 0f : float.MaxValue;

	// Host-only: the latest pose each holder asked for, by pawn id. Seeded with the prop's own transform at the
	// grab so it hangs still until the first stream lands.
	readonly Dictionary<Guid, Transform> _grabTargets = new();

	// Host-side per-caller rate gate on RequestGrab (see GrabFor).
	const float GrabCooldown = 0.1f;
	readonly Dictionary<Guid, RealTimeUntil> _grabGate = new();

	/// <summary>Host: the pose a held prop should chase, or false when nobody holds it.</summary>
	internal bool TryGetGrabTarget( HiderController hider, out Transform target )
	{
		target = default;
		return hider.IsValid() && IsHeld( hider ) && _grabTargets.TryGetValue( hider.GameObject.Id, out target );
	}

	/// <summary>Caller picks up the clay under their crosshair. Arbitrated like <see cref="RequestEdit"/>: a
	/// released pawn prop is taken as-is; scene clay is CONVERTED first (host-owned released scenery). Refused
	/// for anything a claim couldn't take — someone's body, a prop being sculpted, one already held.</summary>
	[Rpc.Host]
	public void RequestGrab( GameObject target ) => GrabFor( Rpc.Caller ?? Connection.Local, target );

	internal void GrabFor( Connection c, GameObject target )
	{
		var host = Host;
		if ( c is null || !target.IsValid() || host is null || !host.ClaimsAllowed || !host.GrabAllowed )
			return;

		var pawn = host.ClaimantPawn( c );
		if ( !pawn.IsValid() || !pawn.Components.Get<HunterController>().IsValid() )
			return;

		// Its own, shorter anti-spam gate: hold-to-carry makes quick re-presses normal (drop, grab again), and
		// sharing possession's 0.3s refused them outright.
		if ( _grabGate.TryGetValue( c.Id, out var gate ) && gate > 0f )
			return;
		_grabGate[c.Id] = GrabCooldown;

		if ( pawn.WorldPosition.Distance( target.WorldPosition ) > PossessRange )
			return;

		HiderController prop;
		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
		{
			if ( !IsClaimable( hider.DisguiseSculpture ) )
				return; // someone's body, being sculpted, or already held
			prop = hider;
		}
		else
		{
			var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
			if ( !IsClaimable( sculpture ) || !_claimedScene.Add( sculpture.GameObject.Id ) )
				return;

			var converted = ConvertSceneProp( sculpture, null, scenery: true );
			if ( !converted.IsValid() )
			{
				_claimedScene.Remove( sculpture.GameObject.Id );
				return;
			}

			prop = converted.Components.Get<HiderController>();
			prop.ReleaseControl();
			ReleasedProps[prop.GameObject.Id] = true;
		}

		DropFor( c ); // one prop per player
		FrozenProps.Remove( prop.GameObject.Id ); // picking a frozen prop up unfreezes it
		Held[c.Id] = prop.GameObject.Id;
		_grabTargets[prop.GameObject.Id] = prop.WorldTransform;
		PlaySwapPop( PopSpot( prop.GameObject ) );
	}

	/// <summary>Holder → host, every frame while holding: the root position + rotation the prop should chase,
	/// and the shape scale relative to its size when this hold began. Unreliable — the next frame's pose replaces
	/// a lost one. Ignored from anyone not holding it.</summary>
	[Rpc.Host( NetFlags.UnreliableNoDelay )]
	public void UpdateGrab( GameObject prop, Vector3 position, Rotation rotation, float scale )
	{
		var c = Rpc.Caller ?? Connection.Local;
		if ( c is null || !prop.IsValid() || !Held.TryGetValue( c.Id, out var id ) || id != prop.Id )
			return;

		_grabTargets[id] = new Transform( position, rotation );
		ApplyGrabScale( prop, Math.Clamp( scale, MinGrabScale, MaxGrabScale ) );
	}

	// ── Shape scaling (host) ─────────────────────────────────────────────────────────────────────────────
	// Per held prop: the authored brushes as they were when scaling started, the pivot (their bounds centre —
	// the point the holder floats at the hold point, so the prop grows about where it hangs), the factor
	// currently applied, and whether a preview is waiting to be committed.
	sealed class GrabScale
	{
		public List<SdfBrush> Base;
		public Vector3 Pivot;
		public float Applied = 1f;
		public bool Pending;
		public RealTimeSince SinceChange;
	}

	readonly Dictionary<Guid, GrabScale> _grabScales = new();

	// How long the factor must hold still before a preview commits (the collider and the durable publish follow
	// the commit, so committing every frame of a scrub would rebuild the collider per frame).
	const float GrabScaleSettle = 0.3f;

	void ApplyGrabScale( GameObject prop, float factor )
	{
		var sculpture = prop.Components.Get<HiderController>()?.DisguiseSculpture;
		if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } current )
			return;

		if ( !_grabScales.TryGetValue( prop.Id, out var st ) )
		{
			if ( MathF.Abs( factor - 1f ) < 1e-3f )
				return; // never scaled — nothing to snapshot

			// Pivot: the clay's centre (the point the holder floats at the hold point — PopSpot), moved onto the
			// symmetry plane of any mirrored axis, or the mirror copies wouldn't scale with their originals.
			var authored = current.GetRange( 0, sculpture.AuthoredBrushCount ).Select( b => b.Copy() ).ToList();
			if ( authored.Count == 0 || !Sdf.TryGetBounds( current, out var bounds ) )
				return;

			st = new GrabScale { Base = authored, Pivot = SdfScaling.SymmetricPivot( authored, bounds.Center ) };
			_grabScales[prop.Id] = st;
		}

		if ( MathF.Abs( factor - st.Applied ) < 1e-3f )
			return;

		var limits = sculpture.GameObject.Components.Get<SculptBounds>();
		if ( !GrabScaleStepAllowed( limits, st.Base, st.Pivot, st.Applied, factor ) )
			return;

		// Base × factor, then whatever damage tail is live now (craters heal on their own clock — never rescaled
		// from a stale snapshot).
		var work = ScaledCopy( st.Base, st.Pivot, factor );
		work.AddRange( current.Skip( sculpture.AuthoredBrushCount ) );

		sculpture.Brushes = work;
		sculpture.RebuildShadowProxy();
		st.Applied = factor;
		st.Pending = true;
		st.SinceChange = 0f;
	}

	/// <summary>May a grab scale go from <paramref name="from"/> to <paramref name="to"/> (factors of
	/// <paramref name="baseBrushes"/> about <paramref name="pivot"/>)? The size limits every shape on the wire
	/// answers to (<see cref="SculptBounds.ValidateIncoming"/>) stop a VALID prop at the limit. But a prop that's
	/// ALREADY outside them — a converted map knob under the minimum — isn't the player's problem outside edit
	/// mode: any step back toward the limits is allowed (growing a too-small prop, shrinking a too-big one), so
	/// it never sticks. Shared by the holder (to stop its factor) and the host (to apply it), so both agree.
	/// While the shape is still outside the limits the host keeps it local — the owner's network gate publishes
	/// only valid shapes — and everyone sees it the moment it fits.</summary>
	internal static bool GrabScaleStepAllowed( SculptBounds limits, List<SdfBrush> baseBrushes, Vector3 pivot, float from, float to )
	{
		if ( !limits.IsValid() || limits.ValidateIncoming( ScaledCopy( baseBrushes, pivot, to ), withVolume: true ) )
			return true;

		var now = ScaledCopy( baseBrushes, pivot, from );
		if ( !limits.ValidateIncoming( now, withVolume: false ) )
			return to < from; // too big already: shrinking helps
		if ( !limits.ValidateIncoming( now, withVolume: true ) )
			return to > from; // too small already: growing helps
		return false;         // fits now — this step would break it, so stop at the limit
	}

	/// <summary>Copies of <paramref name="brushes"/> scaled by the group rule (<see cref="SdfScaling.ScaleUniform"/>).</summary>
	internal static List<SdfBrush> ScaledCopy( List<SdfBrush> brushes, Vector3 pivot, float factor )
	{
		var work = new List<SdfBrush>( brushes.Count );
		foreach ( var b in brushes )
		{
			var scaled = b.Copy();
			SdfScaling.ScaleUniform( scaled, pivot, factor );
			work.Add( scaled );
		}
		return work;
	}

	// Host, per frame: commit any scale preview that has held still long enough.
	void SettleGrabScales()
	{
		foreach ( var (pawnId, st) in _grabScales )
		{
			if ( st.Pending && st.SinceChange > GrabScaleSettle )
				CommitGrabScale( pawnId, st );
		}
	}

	void CommitGrabScale( Guid pawnId, GrabScale st )
	{
		st.Pending = false;
		var sculpture = Scene.Directory.FindByGuid( pawnId )?.Components.Get<HiderController>()?.DisguiseSculpture;
		if ( sculpture.IsValid() )
			sculpture.Rebuild();
	}

	// The hold on this prop ended: land any pending scale and forget the snapshot (the next hold measures from
	// whatever size it ends up).
	void EndGrabScale( Guid pawnId )
	{
		if ( !_grabScales.Remove( pawnId, out var st ) )
			return;
		if ( st.Pending )
			CommitGrabScale( pawnId, st );
	}

	/// <summary>Holder lets go: RMB released (<paramref name="freeze"/> false — the prop goes LOOSE, keeping
	/// whatever motion the hold gave it, so a swing still carries) or LMB (true — FROZEN where it is).</summary>
	[Rpc.Host]
	public void DropGrab( bool freeze ) => DropFor( Rpc.Caller ?? Connection.Local, freeze );

	/// <summary>The grab's scale limits, relative to the prop's size when the hold began.</summary>
	public const float MinGrabScale = 0.2f, MaxGrabScale = 5f;

	/// <summary>Pitch of the pop every PUT-DOWN makes — a grab's drop (release, freeze, a swap to another prop, a
	/// sweep) and leaving a prop for a hunter (creative's and the lobby's swap): the same "thunk" as picking up,
	/// lower, read as letting go.</summary>
	public const float DropPopPitch = 0.7f;

	// Host-only: end a connection's hold. The prop stays where it is, released — loose, or frozen.
	void DropFor( Connection c, bool freeze = false )
	{
		if ( c is null || !Held.TryGetValue( c.Id, out var id ) )
			return;

		Held.Remove( c.Id );
		_grabTargets.Remove( id );
		EndGrabScale( id );
		var go = Scene.Directory.FindByGuid( id );
		if ( !go.IsValid() )
			return;

		PlaySwapPop( PopSpot( go ), DropPopPitch );

		if ( freeze )
		{
			LooseProps.Remove( id );
			FrozenProps[id] = true;
		}
		else
		{
			LooseProps[id] = true;
		}
	}

	// Host-only, per frame (beside SweepEditors): a hold must belong to a connected player whose pawn is still a
	// hunter, on a prop that still exists. Anything else drops it where it is.
	void SweepHolders()
	{
		SettleGrabScales();

		if ( Held.Count == 0 )
			return;

		foreach ( var (holder, pawnId) in Held.ToList() )
		{
			var conn = Connection.All.FirstOrDefault( x => x.Id == holder );
			var claimant = conn is not null ? Host?.ClaimantPawn( conn ) : null;
			bool holderIsHunter = claimant.IsValid() && claimant.Components.Get<HunterController>().IsValid();
			if ( holderIsHunter && Scene.Directory.FindByGuid( pawnId ).IsValid() )
				continue;

			Held.Remove( holder );
			_grabTargets.Remove( pawnId );
			EndGrabScale( pawnId );
			var go = Scene.Directory.FindByGuid( pawnId );
			if ( go.IsValid() )
			{
				LooseProps[pawnId] = true;
				PlaySwapPop( PopSpot( go ), DropPopPitch );
			}
		}
	}
}
