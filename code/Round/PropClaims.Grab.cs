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
// IN THE SIM (mimi_sim_walk, the default): every held prop — scenery, players' bodies, bots, prop hunt's decoys — is
// carried by the shared sim's hold driver (SimHoldDriver), fed by the holder's own target as sim input, so the holder
// predicts the hold with no lag and the host's sim is everyone's truth. The rest of this file is the arbitration (who
// may hold what), the registries, scaling, and the fallback below for anything the sim doesn't carry.
//
// FALLBACK (the sim off, or a body mid-edit on its wearer's machine): HOST-SIMULATED, like all released scenery. The
// prop never changes owner: the holder computes where they want it (root position + rotation) and streams that to the
// host every frame (UpdateGrab, unreliable), and the host's HiderController chases it with velocities — so the held
// prop still collides with the world on its way, and keeps its momentum the moment it's let go (a swing becomes a
// throw). A client holder sees their own hold one round-trip late.
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
//
// PLAYERS' PROPS (GrabPlayersAllowed) can be grabbed too. In the fallback those are NOT host-simulated: the body
// stays its wearer's, simulated on THEIR machine, and the host only relays the holder's pose to the wearer
// (RelayGrabTarget → HiderController.ReceiveGrabTarget); the wearer's controller chases it exactly like held scenery.
// Space breaks free (EscapeGrab). No scale (the shape is the wearer's, published from their machine) and no freeze; a
// drop leaves the body tumbling on its owner's machine until they move or jump (see HiderController.Grabbed).
//
// THE INSPECT GRAB (IPropClaimHost.GrabSandbox false — prop hunt's "Grab props"): carry and turn, nothing else. It
// must not tell a decoy from a player. Scene decoys are held AS THEY ARE — no conversion into a released pawn (round
// code reads every HiderController as a player): the Held row names the scene object itself, and RoundManager.Grab
// holds it (in the sim, or host-driven and streamed like a shot decoy in the fallback).
public sealed partial class PropClaims
{
	/// <summary>Is this a prop someone is WEARING right now — a live player's body, not released scenery or a bot?
	/// Not gated on BornScenery: that [Sync] flag is only cleared on the wearer's own machine (ResumeControl), so a
	/// once-shared prop someone later possessed still reads born-scenery everywhere else. The released registry
	/// is the scenery answer.</summary>
	public static bool IsPlayerBody( HiderController hider )
		=> hider.IsValid() && !IsReleased( hider ) && hider.Network.Owner is not null
			&& !RoundManager.IsBotPawn( hider.GameObject );

	/// <summary>Can the grab pick up this sculpture as someone's BODY (see <see cref="IPropClaimHost.GrabPlayersAllowed"/>)?
	/// The inspect grab counts bots as bodies too — they're players of the round.</summary>
	public bool IsGrabbableBody( SdfSculpture sculpture )
	{
		if ( !(Host?.GrabPlayersAllowed ?? false) || !sculpture.IsValid() )
			return false;
		if ( sculpture.Components.Get<ClaimBlocker>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false;
		var hider = sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		bool body = IsPlayerBody( hider ) || (!Sandbox && hider.IsValid() && RoundManager.IsBotPawn( hider.GameObject ));
		return body && !IsHeld( hider );
	}

	/// <summary>Can the grab pick this sculpture up at all? Sandbox: what a claim could take, or a body. Inspect: a
	/// scene decoy as it is, or a body.</summary>
	public bool IsGrabbable( SdfSculpture sculpture )
		=> IsGrabbableBody( sculpture ) || (Sandbox ? IsClaimable( sculpture ) : IsInspectableDecoy( sculpture ));

	// The host mode's grab flavour (see IPropClaimHost.GrabSandbox).
	bool Sandbox => Host?.GrabSandbox ?? true;

	// Inspect grab: scene clay with a solid collider (a decoy), not authored off-limits, not already in someone's hands.
	bool IsInspectableDecoy( SdfSculpture sculpture )
		=> IsScenery( sculpture )
		&& sculpture.Components.Get<SdfCollider>() is { BuildAsTrigger: false }
		&& !sculpture.Components.Get<ClaimBlocker>( FindMode.EverythingInSelfAndAncestors ).IsValid()
		&& !IsHeldObject( sculpture.GameObject );

	/// <summary>Is anyone holding this object (a pawn root or, in the inspect grab, a scene decoy)?</summary>
	public static bool IsHeldObject( GameObject go )
	{
		if ( !go.IsValid() || !Current.IsValid() )
			return false;
		foreach ( var (_, held) in Current.Held )
		{
			if ( held == go.Id )
				return true;
		}
		return false;
	}

	/// <summary>The connection holding this prop, if anyone is.</summary>
	public static Guid? HolderOf( HiderController hider )
	{
		if ( !hider.IsValid() || !Current.IsValid() )
			return null;
		var id = hider.GameObject.Id;
		foreach ( var (holder, pawn) in Current.Held )
		{
			if ( pawn == id )
				return holder;
		}
		return null;
	}

	/// <summary>Runtime PhysicsFilters on <paramref name="prop"/> against every body in <paramref name="pawn"/>, so
	/// the two never collide (see IgnoreHolder). Machine-local: never saved, cloned or networked.</summary>
	internal static List<PhysicsFilter> FilterAgainst( GameObject prop, GameObject pawn )
	{
		var filters = new List<PhysicsFilter>();
		if ( !prop.IsValid() || !pawn.IsValid() )
			return filters;

		foreach ( var rb in pawn.Components.GetAll<Rigidbody>( FindMode.EverythingInSelfAndDescendants ) )
		{
			var filter = prop.Components.Create<PhysicsFilter>( startEnabled: false );
			filter.Flags |= ComponentFlags.NotSaved | ComponentFlags.NotNetworked | ComponentFlags.NotCloned | ComponentFlags.Hidden;
			filter.Body = rb.GameObject;
			filter.Enabled = true;
			filters.Add( filter );
		}
		return filters;
	}

	const string GrabOption = "claims.grab";

	/// <summary>May props be grabbed right now? The host mode's call alone — independent of claims (prop hunt grabs
	/// with claims closed).</summary>
	public bool GrabOpen => Host?.GrabAllowed ?? false;

	/// <summary>May a PROP grab right now (the mode lets props grab — <see cref="IPropClaimHost.PropsGrab"/> — and the grab is open)?</summary>
	public bool PropGrabOpen => GrabOpen && (Host?.PropsGrab ?? false);

	/// <summary>The grab's sandbox extras (R scale, LMB freeze) — off in prop hunt's inspect grab.</summary>
	public bool GrabExtras => Sandbox;

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

	// Host-only: per held prop, the PhysicsFilters that stop it colliding with its HOLDER. Up close a held prop
	// otherwise gets pinned between a wall and the holder's own body, and the two shove each other back and
	// forth. Only the holder: the prop still collides with every other player and everything else. One filter
	// per body in the holder's pawn (the movement body, the head's kinematic body). Runtime-only components on
	// the prop root: never saved, cloned or networked — collisions are the host's business alone.
	readonly Dictionary<Guid, List<PhysicsFilter>> _holderFilters = new();

	void IgnoreHolder( Connection c, GameObject prop )
	{
		ClearHolderFilters( prop.Id );

		var pawn = Host?.ClaimantPawn( c );
		if ( !pawn.IsValid() )
			return;

		_holderFilters[prop.Id] = FilterAgainst( prop, pawn );
	}

	void ClearHolderFilters( Guid pawnId )
	{
		if ( !_holderFilters.Remove( pawnId, out var filters ) )
			return;

		foreach ( var filter in filters )
		{
			if ( filter.IsValid() )
				filter.Destroy();
		}
	}

	/// <summary>For this long after a grab the wearer can't break free, even with Space (user, 2026-11-06) — enforced here
	/// (EscapeGrab) and mirrored on the wearer's machine (HiderController.EscapeGraceActive).</summary>
	public const float EscapeHoldGrace = 1f;
	readonly Dictionary<Guid, RealTimeSince> _heldSince = new(); // host: per held body, time since the grab

	// Host-side per-caller rate gate on RequestGrab (see GrabFor).
	const float GrabCooldown = 0.1f;
	readonly Dictionary<Guid, RealTimeUntil> _grabGate = new();

	/// <summary>Host: the pose a held prop should chase, or false when nobody holds it.</summary>
	internal bool TryGetGrabTarget( HiderController hider, out Transform target )
	{
		target = default;
		return hider.IsValid() && IsHeld( hider ) && _grabTargets.TryGetValue( hider.GameObject.Id, out target );
	}

	// Host-only: the holder's GRIP per held prop (root-local; see GrabHolder) — what the fallback hold turns it about.
	readonly Dictionary<Guid, Vector3> _grabGrips = new();

	/// <summary>Host: the point a held prop (or scene decoy) is held by, root-local — null before the holder's first stream
	/// (the hold then pulls by the mass centre).</summary>
	internal Vector3? GrabGripOf( GameObject go )
		=> go.IsValid() && IsHeldObject( go ) && _grabGrips.TryGetValue( go.Id, out var grip ) ? grip : null;

	/// <summary>Host: the pose a held scene decoy (inspect grab) should chase.</summary>
	internal bool TryGetGrabTarget( GameObject go, out Transform target )
	{
		target = default;
		return go.IsValid() && IsHeldObject( go ) && _grabTargets.TryGetValue( go.Id, out target );
	}

	/// <summary>Caller picks up the clay under their crosshair. Arbitrated like <see cref="RequestEdit"/>: a
	/// released pawn prop is taken as-is; scene clay is CONVERTED first (host-owned released scenery). Refused
	/// for anything a claim couldn't take — someone's body, a prop being sculpted, one already held.</summary>
	[Rpc.Host]
	public void RequestGrab( GameObject target ) => GrabFor( Rpc.Caller ?? Connection.Local, target );

	/// <summary>May this pawn hold a prop? A hunter always (where the mode grabs at all); a prop pawn — one a player is
	/// driving, not released scenery — only where the mode lets props grab (<see cref="IPropClaimHost.PropsGrab"/>).</summary>
	bool CanHold( GameObject pawn )
	{
		if ( !pawn.IsValid() )
			return false;
		if ( pawn.Components.Get<HunterController>().IsValid() )
			return true;
		var hider = pawn.Components.Get<HiderController>();
		return (Host?.PropsGrab ?? false) && hider.IsValid() && !IsReleased( hider );
	}

	internal void GrabFor( Connection c, GameObject target )
	{
		var host = Host;
		if ( c is null || !target.IsValid() || host is null || !host.GrabAllowed )
			return;

		var pawn = host.ClaimantPawn( c );
		if ( !CanHold( pawn ) )
			return;

		// Its own, shorter anti-spam gate: hold-to-carry makes quick re-presses normal (drop, grab again), and
		// sharing possession's 0.3s refused them outright.
		if ( _grabGate.TryGetValue( c.Id, out var gate ) && gate > 0f )
			return;
		_grabGate[c.Id] = GrabCooldown;

		if ( pawn.WorldPosition.Distance( target.WorldPosition ) > PossessRange )
			return;

		// The still props touching what's picked up wake with it (PropContacts) — found before any conversion moves it.
		// Scenery only: lifting someone's body doesn't shake the world loose.
		List<SdfSculpture> cluster = null;
		SdfSculpture decoy = null;

		GameObject prop;
		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
		{
			if ( hider.GameObject == pawn )
				return; // a prop can't pick itself up
			if ( !IsGrabbable( hider.DisguiseSculpture ) )
				return; // someone's body (where the mode says no), being sculpted, or already held
			prop = hider.GameObject;
			if ( IsReleased( hider ) )
				cluster = StillCluster( hider.DisguiseSculpture );
		}
		else if ( !Sandbox )
		{
			// Inspect grab: a scene decoy is held as it is — RoundManager.ShotProps moves it.
			var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
			if ( !IsInspectableDecoy( sculpture ) )
				return;
			prop = sculpture.GameObject;
			decoy = sculpture;
		}
		else
		{
			var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
			if ( !IsClaimable( sculpture ) || !_claimedScene.Add( sculpture.GameObject.Id ) )
				return;
			cluster = StillCluster( sculpture );

			var converted = ConvertSceneProp( sculpture, null, scenery: true );
			if ( !converted.IsValid() )
			{
				_claimedScene.Remove( sculpture.GameObject.Id );
				return;
			}

			var convertedHider = converted.Components.Get<HiderController>();
			convertedHider.ReleaseControl();
			ReleasedProps[converted.Id] = true;
			prop = converted;
		}

		DropFor( c ); // one prop per player
		FrozenProps.Remove( prop.Id ); // picking a frozen prop up unfreezes it
		Held[c.Id] = prop.Id;
		_heldSince[prop.Id] = 0f;
		_grabTargets[prop.Id] = prop.WorldTransform;
		IgnoreHolder( c, prop );
		PlaySwapPop( PopSpot( prop ) );

		if ( cluster is not null )
			WakeCluster( cluster );
		if ( decoy is not null )
			RoundManager.Current?.WakeTouchingDecoys( decoy );
	}

	/// <summary>Holder → host, every frame while holding: the root position + rotation the prop should chase,
	/// and the shape scale relative to its size when this hold began. Unreliable — the next frame's pose replaces
	/// a lost one. Ignored from anyone not holding it.</summary>
	[Rpc.Host( NetFlags.UnreliableNoDelay )]
	public void UpdateGrab( GameObject prop, Vector3 position, Rotation rotation, float scale, Vector3 grip )
	{
		var c = Rpc.Caller ?? Connection.Local;
		if ( c is null || !prop.IsValid() || !Held.TryGetValue( c.Id, out var id ) || id != prop.Id )
			return;

		_grabGrips[id] = grip.ClampLength( 2048f ); // the point it's held by (root-local) — HeldBodyDriver turns it about this

		// Keep the target within reach of where the HOST sees the holder (sandbox's physgun does the same): the
		// joint has no speed cap, so a stale or bogus pose far away would fling the prop across the map.
		var pawn = Host?.ClaimantPawn( c );
		if ( pawn.IsValid() )
			position = pawn.WorldPosition + (position - pawn.WorldPosition).ClampLength( GrabTargetReach );

		_grabTargets[id] = new Transform( position, rotation );

		// Someone's body: its wearer simulates it in the fallback — pass the pose on. Never scaled (the shape is theirs).
		var hider = prop.Components.Get<HiderController>();
		if ( IsPlayerBody( hider ) )
		{
			RelayGrabTarget( hider, position, rotation );
			return;
		}

		// Scale: the host owns scenery's shape, so its commits are the ones that publish.
		if ( Sandbox )
			ApplyGrabScale( prop, Math.Clamp( scale, MinGrabScale, MaxGrabScale ) );
	}

	// Host → the wearer of a held body: the pose to chase. Straight in when the host wears it itself.
	static void RelayGrabTarget( HiderController hider, Vector3 position, Rotation rotation )
	{
		var owner = hider.Network.Owner;
		if ( owner is null || owner.Id == Connection.Local?.Id )
		{
			hider.SetGrabTarget( new Transform( position, rotation ) );
			return;
		}

		using ( Rpc.FilterInclude( owner ) )
		{
			hider.ReceiveGrabTarget( position, rotation );
		}
	}

	/// <summary>The wearer of a held body jumped: break free of whoever is holding it (not within EscapeHoldGrace of the grab).</summary>
	[Rpc.Host]
	public void EscapeGrab( GameObject body, bool jump = false )
	{
		var c = Rpc.Caller ?? Connection.Local;
		var hider = body.IsValid() ? body.Components.Get<HiderController>() : null;
		if ( c is null || !IsPlayerBody( hider ) || hider.Network.Owner?.Id != c.Id )
			return; // only its wearer
		if ( _heldSince.TryGetValue( body.Id, out var heldFor ) && heldFor < EscapeHoldGrace )
			return; // too soon after the grab: the hold stands

		if ( HolderOf( hider ) is { } holder && Connection.All.FirstOrDefault( x => x.Id == holder ) is { } holderConn )
			DropFor( holderConn );

		// The wearer broke free: the sim lets go of it NOW (not at the holder's release tick, which is a holder round trip away —
		// the "big delay on our jump/detach", user 2026-11-06), and a Space escape is a jump out of the hands: the flip starts on
		// the first lying tick (the press itself was spent while the hold still drove the body). HiderController.UpdateSimRegistration.
		// A body the sim carries simply lies down, and its wearer's own input rolls it or flips it back up (SimDownDriver).
		hider.SimEscaped = true;
		hider.SimEscapeJump = jump;
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

	// Furthest a held prop's ROOT may be asked to go from its holder's pawn origin. A sanity bound, not the hold
	// range (that's the holder's 300u to the clay centre) — generous for a 5×-scaled prop whose root sits far
	// from its centre.
	const float GrabTargetReach = 1200f;

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
		_heldSince.Remove( id );
		_grabTargets.Remove( id );
		_grabGrips.Remove( id );
		EndGrabScale( id );
		ClearHolderFilters( id );
		var go = Scene.Directory.FindByGuid( id );
		if ( !go.IsValid() )
			return;

		PlaySwapPop( PopSpot( go ), DropPopPitch );

		var hider = go.Components.Get<HiderController>();

		// Someone's body: its wearer's machine takes it from here (it tumbles until they move — see
		// HiderController.Grabbed). The scenery registries never apply, and a body is never frozen.
		if ( IsPlayerBody( hider ) )
			return;

		// Inspect grab: a bot stands back up where it was put down; a scene decoy is left loose by its own system
		// (RoundManager.ShotProps notices it's no longer held). Neither ever freezes.
		if ( !Sandbox )
		{
			if ( hider.IsValid() && !HiderController.SimHoldApplies( hider ) )
				hider.SettleUpright(); // (a body the sim carries lies loose until it stands up — UpdateSimRegistration)
			return;
		}

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
	// pawn that may hold (CanHold), on a prop that still exists. Anything else drops it where it is.
	void SweepHolders()
	{
		SettleGrabScales();

		if ( Held.Count == 0 )
			return;

		// The mode closing the grab (prop hunt: the Hunt ended) drops every hold too.
		bool open = Host?.GrabAllowed ?? false;
		foreach ( var (holder, pawnId) in Held.ToList() )
		{
			var conn = Connection.All.FirstOrDefault( x => x.Id == holder );
			var claimant = conn is not null ? Host?.ClaimantPawn( conn ) : null;
			if ( open && CanHold( claimant ) && Scene.Directory.FindByGuid( pawnId ).IsValid() )
				continue;

			if ( conn is not null )
			{
				DropFor( conn ); // a still-connected holder: the normal drop
				continue;
			}

			// The holder left: drop it by hand.
			Held.Remove( holder );
			_grabTargets.Remove( pawnId );
			_grabGrips.Remove( pawnId );
			EndGrabScale( pawnId );
			ClearHolderFilters( pawnId );
			var go = Scene.Directory.FindByGuid( pawnId );
			if ( go.IsValid() )
			{
				var hider = go.Components.Get<HiderController>();
				if ( Sandbox && !IsPlayerBody( hider ) )
					LooseProps[pawnId] = true;
				else if ( !Sandbox && hider.IsValid() && !IsPlayerBody( hider ) && !hider.InSim )
					hider.SettleUpright(); // (not a sim body: the sim stands it up — see HandOver)
				PlaySwapPop( PopSpot( go ), DropPopPitch );
			}
		}
	}
}
