using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// The mode-side half of the prop-claim contract: implemented by the game-mode manager that HOSTS a
/// <see cref="PropClaims"/> service, on the SAME GameObject (so one NetworkSpawn ships both). The service owns
/// everything mode-agnostic — classification, hover, arbitration, conversion, the released registry — and calls
/// back through this for the things only the mode knows: its pawn bookkeeping, its roster, and its policy.
/// </summary>
public interface IPropClaimHost
{
	/// <summary>Are claims currently open? Policy, read on every machine (the hover prompt) and re-checked on
	/// the host at the claim itself. Creative never closes; the lobby closes during the launch countdown.</summary>
	bool ClaimsAllowed { get; }

	/// <summary>May a hunter SCULPT a released prop in place (the first-person edit lease — see
	/// <see cref="PropClaims.RequestLease"/>) without possessing it? Creative's collaborative building says
	/// yes; the lobby and charades keep the one-body-per-player possession flow only. Default off, so a host
	/// opts in explicitly.</summary>
	bool LeasesAllowed => false;

	/// <summary>The prop-pawn prefab a claimed SCENE prop converts into (read live off scene furniture — a
	/// NetworkSpawn'd manager's [Property] refs only exist on the host).</summary>
	GameObject PropPrefab { get; }

	/// <summary>The caller's current pawn, from the host's bookkeeping. Null denies the claim; the service
	/// itself requires it to be a hunter (only hunters have the crosshair that claims).</summary>
	GameObject ClaimantPawn( Connection c );

	/// <summary>A claim was granted: update roster + pawn bookkeeping (the claimant is a prop now, driving
	/// <paramref name="prop"/>). Called BEFORE <paramref name="hunterPawn"/> is destroyed, so the host can
	/// still read state off it (the face snapshot).</summary>
	void OnClaimGranted( Connection c, GameObject hunterPawn, HiderController prop );
}

/// <summary>
/// The prop-claim service: what lets a hunter aim at clay in the world and press E to take it over for editing.
/// Extracted from CreativeManager so any mode can host it — creative and the lobby today — by spawning one
/// beside its manager (which implements <see cref="IPropClaimHost"/>) on the networked singleton. A mode that
/// doesn't want editable props simply doesn't spawn one: presence IS the policy, and maps carry no flags.
///
/// Owns, mode-agnostically:
/// <list type="bullet">
/// <item>CLASSIFICATION — <see cref="IsClaimable"/>/<see cref="IsScenery"/>: any sculpture with brushes that
/// isn't someone's body. The <see cref="SdfSculpture"/> component is the marker; there is no prop tag to keep
/// in sync across prefabs and maps.</item>
/// <item>THE "EDIT" OPTION — registered as an <see cref="Interactions"/> source, so every claimable sculpture
/// under a hunter's crosshair offers E Edit beside whatever the object itself offers (the radio's on/off).</item>
/// <item>ARBITRATION — <see cref="RequestPossess"/>, THE claim point: requests arrive serially on the host,
/// with the <see cref="ReleasedProps"/> registry remove (pawn props) and <see cref="_claimedScene"/> (scene
/// props) as idempotency guards, so two players pressing E on the same prop in the same instant get exactly
/// one winner — the loser keeps their hunter.</item>
/// <item>CONVERSION — <see cref="ConvertSceneProp"/>: a scene-placed prop can't be taken over directly (it was
/// never networked, and you can't NetworkSpawn a scene object without duplicating it on clients), so a
/// prop-pawn clone is dressed in its brushes at its exact spot and the original is destroyed everywhere.</item>
/// <item>RELEASE — <see cref="Release"/>: hand a pawn prop off into the world as claimable scenery, registered
/// in this component's [Sync] registry (the same provably-replicating mechanism the rosters use — a flag on
/// the pawn itself would have to be written across the release's ownership edge, which proved unreliable).</item>
/// </list>
/// </summary>
[Title( "Prop Claims" )]
[Category( "Mimiclay" )]
[Icon( "pan_tool" )]
public sealed class PropClaims : Component, IInteractable
{
	/// <summary>The live claim service (null wherever props aren't editable — round maps, the menu). The
	/// hunter's hover detection and the outline system read this to know claim rules apply. Named Current like
	/// every other singleton here — NOT Active, which would shadow Component.Active (the enabled state).</summary>
	public static PropClaims Current { get; private set; }

	/// <summary>How far a hunter can reach to hover (and so claim) clay, measured from the eye to the point the
	/// crosshair ray lands on — NOT to the prop's origin, so a big prop is reachable by its near face. The gun's
	/// own ray is map-length (4096u); without this bound every distant prop across the room outlines and offers
	/// "E to Edit", which reads as noise and lets you claim things you can't see properly. Authored by whoever
	/// spawns the service (RoundManagerSpawner for creative maps, LobbyController for the lobby), BEFORE the
	/// NetworkSpawn so the snapshot ships it to every client's hover.</summary>
	[Property, Range( 64f, 4096f )]
	public float HoverRange { get; set; } = 100f;

	/// <summary>The reach the host validates a claim against, in origin-to-origin terms. Slack over
	/// <see cref="HoverRange"/> on two counts: the client measured to a SURFACE, and the origin of a large prop
	/// can sit well behind it; plus the usual latency margin the shot validation uses.</summary>
	float PossessRange => HoverRange * 1.25f + 256f;

	// The mode manager beside us. Cached — same GameObject, same lifetime by construction.
	IPropClaimHost _host;
	IPropClaimHost Host => _host ??= Components.Get<IPropClaimHost>();

	/// <summary>Is the whole flow currently open? The hover/prompt gate on every machine — policy comes from
	/// the host mode (its component replicates beside this one, and its policy inputs are [Sync], so clients
	/// answer correctly too). The host re-checks at the claim itself; this just keeps the UI honest.</summary>
	public bool ClaimsOpen => Host?.ClaimsAllowed ?? false;

	/// <summary>May props be sculpted in place right now (claims open AND the host mode allows leases)? The
	/// hunter's roaming scan reads this every frame.</summary>
	public bool LeasesOpen => ClaimsOpen && (Host?.LeasesAllowed ?? false);

	// ── The "Edit" interaction (PropClaims is a registered Interactions source) ────────────────────────────

	const string EditOption = "claims.edit";
	const string SculptOption = "claims.sculpt";

	/// <summary>Set the instant THIS machine asks to possess something, cleared by the next possession (or by
	/// timing out). Charades reads it so its pawn-kind poll doesn't respawn a hunter in the gap between the host
	/// destroying ours and the possession reaching us.</summary>
	public static bool LocalClaimPending => _claimPendingSince < 3f;
	static RealTimeSince _claimPendingSince = float.MaxValue;

	/// <summary>The pending local claim landed (or was refused) — see <see cref="LocalClaimPending"/>.</summary>
	public static void ClearLocalClaimPending() => _claimPendingSince = float.MaxValue;

	void IInteractable.GetInteractions( in InteractContext ctx, List<InteractOption> options )
	{
		if ( !ClaimsOpen )
			return;

		// E: possess — only clay nobody is sculpting right now.
		if ( IsClaimable( ctx.Sculpture ) )
			options.Add( new InteractOption( EditOption, "Edit", InteractSlot.Primary ) );

		// LMB: sculpt it in place, first person, without becoming it — alongside whoever is already on it
		// (see RequestEdit). Opens with nothing selected; shapes are picked inside the session. Clicking
		// away exits.
		if ( (Host?.LeasesAllowed ?? false) && IsSculptable( ctx.Sculpture ) )
			options.Add( new InteractOption( SculptOption, "Sculpt", InteractSlot.Sculpt ) );
	}

	void IInteractable.Interact( in InteractContext ctx, string optionId )
	{
		if ( !ctx.Sculpture.IsValid() )
			return;

		if ( optionId == SculptOption )
		{
			// Same root rule as the claim below: the pawn root for a pawn prop (the Disguise child is a
			// per-machine object whose id doesn't resolve on the host), the sculpture's own object for scene
			// clay. The hunter owns the rest of the flow — it opens the session once the lease lands.
			var leaseHider = ctx.Sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
			ctx.Hunter?.RequestSculptEdit( leaseHider.IsValid() ? leaseHider.GameObject : ctx.Sculpture.GameObject );
			return;
		}

		if ( optionId != EditOption )
			return;

		// Carry the view into the prop, same as a lobby swap: yaw+pitch stashed owner-side here, consumed by
		// ResumeControl on the possessed pawn — whatever you were looking at, you still are.
		LobbySwapCarry.Capture( Scene, null );

		// Claim by the pawn ROOT when the hover is a pawn prop, not the sculpture: the Disguise child is created
		// per-machine in OnStart (it's not in the spawn snapshot), so its id only resolves locally — a client
		// sending it would no-op on the host. The root is the networked object; scene clay has no root and its
		// own scene-file id resolves everywhere. Same rule as the shot's ReportPropHit.
		var claimHider = ctx.Sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		_claimPendingSince = 0f;
		RequestPossess( claimHider.IsValid() ? claimHider.GameObject : ctx.Sculpture.GameObject );
	}

	/// <summary>What a claim can take: any clay in the map that isn't currently BEING someone.
	/// A pawn prop only once its player let it go (<see cref="IsReleased"/>); a hunter's face never;
	/// everything else with brushes — scene decoys, prop-builder balls, blockset pieces — always
	/// (claiming one CONVERTS it into a prop pawn, see <see cref="RequestPossess"/>).</summary>
	public static bool IsClaimable( SdfSculpture sculpture ) => IsTakeable( sculpture, joinEdited: false );

	/// <summary>What an in-place SCULPT can open: the same clay a claim can take, PLUS a prop other people are
	/// already sculpting — joining them is the point of shared editing. (Possession stays refused there: you
	/// can't wear a prop while others are reshaping it.)</summary>
	public static bool IsSculptable( SdfSculpture sculpture ) => IsTakeable( sculpture, joinEdited: true );

	static bool IsTakeable( SdfSculpture sculpture, bool joinEdited )
	{
		if ( !sculpture.IsValid() || sculpture.Brushes is not { Count: > 0 } )
			return false;

		if ( sculpture.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false; // someone's face (or their gun's clay) — never claimable

		if ( sculpture.Components.Get<TutorialNpc>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false; // the tutorial character: his E opens the guided session locally (see TutorialNpc), never a claim

		if ( sculpture.Components.Get<ClaimBlocker>( FindMode.EverythingInSelfAndAncestors ).IsValid() )
			return false; // authored off-limits (the charades stage) — the per-prop lock

		var hider = sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
			return IsReleased( hider ) && (joinEdited || !IsBeingEdited( hider )); // a pawn's body: only once released into the world — and, for a claim, not while people are sculpting it in place

		return IsScenery( sculpture ); // scene-placed clay — claimable by conversion
	}

	/// <summary>Scene-placed clay: a sculpture that is nobody's pawn (no controller above it) and genuinely
	/// part of the scene — NotSaved excludes runtime-made rigs, most importantly SdfStage's thumbnail hosts,
	/// which live in the GAME scene while rendering to their own SceneWorld and would otherwise read as props.
	/// The component IS the marker (no prop tag to keep in sync across prefabs); this is the classification
	/// both the claim rule and creative's spawn-props sweep bottom out in, so "what the sweep deletes" and
	/// "what a hunter can take" can never drift apart.</summary>
	public static bool IsScenery( SdfSculpture sculpture )
		=> sculpture.IsValid()
		&& !sculpture.GameObject.Flags.HasFlag( GameObjectFlags.NotSaved )
		&& !sculpture.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid()
		&& !sculpture.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ).IsValid();

	/// <summary>The released props, by pawn GameObject id — the registry every machine's hover/claim reads.
	/// Lives HERE, on the host-owned service, rather than as a [Sync] flag on the pawn itself: a released pawn
	/// is UNOWNED (its release just dropped ownership), and a per-pawn flag written across that ownership edge
	/// proved unreliable on other machines — while this component's [Sync] state is the same provably-replicating
	/// mechanism the rosters use. Host adds on release, removes on claim (the remove is the claim's idempotency
	/// guard). Destroyed pawns' ids linger harmlessly — nothing resolves them again.</summary>
	[Sync] public NetDictionary<Guid, bool> ReleasedProps { get; private set; } = new();

	/// <summary>Is this pawn prop released scenery (claimable, driven by nobody)? Safe anywhere — false wherever
	/// no claim service runs.</summary>
	public static bool IsReleased( HiderController hider )
		=> hider.IsValid() && Current.IsValid() && Current.ReleasedProps.ContainsKey( hider.GameObject.Id );

	/// <summary>Props someone is currently WEARING through a claim, by pawn GameObject id — the other half of the
	/// lifecycle to <see cref="ReleasedProps"/> (host adds on every granted claim, removes on release). Synced for
	/// the same reason: the owner's machine must know "this body is borrowed" (E pops you out of it) and only the
	/// host saw the claim.</summary>
	[Sync] public NetDictionary<Guid, bool> PossessedProps { get; private set; } = new();

	/// <summary>Is this pawn prop being worn through a claim (as opposed to a body its player spawned as)? Safe
	/// anywhere — false wherever no claim service runs.</summary>
	public static bool IsPossessed( HiderController hider )
		=> hider.IsValid() && Current.IsValid() && Current.PossessedProps.ContainsKey( hider.GameObject.Id );

	// ── Shared editing (sculpt a released prop in place, first person, several players at once) ────────────
	// A released prop is HOST-owned scenery, and stays that way while it's sculpted: nobody takes it over.
	// Each EDITOR runs a first-person SculptEditSession on their own machine against the host's shape and
	// sends every change as a brush OP — named by brush id (SdfBrush.Id), never by index — to the host
	// (SubmitBrushes / StreamBrushes). The host applies it to its copy, and the pawn's SdfNetworkSync then
	// publishes the result to everyone exactly as it always has (the host is the owner of an unowned object).
	// Concurrency is settled with PER-BRUSH LOCKS: selecting a brush asks the host for its lock, only the
	// holder's ops may touch it, and a receiver keeps its OWN locked brushes' local state when a snapshot
	// lands (see SculptEditSession.MergeIncoming). Registries live here, [Sync] on the host-owned service,
	// for the same reason ReleasedProps does.

	/// <summary>Who is editing what: connection id → pawn GameObject id. One prop per player at a time.</summary>
	[Sync] public NetDictionary<Guid, Guid> Editing { get; private set; } = new();

	/// <summary>Brush locks: brush id → the connection holding it. Brush ids are globally unique (Guids), so
	/// no prop key is needed.</summary>
	[Sync] public NetDictionary<Guid, Guid> BrushLocks { get; private set; } = new();

	/// <summary>Is anyone sculpting this prop in place (any machine's answer)?</summary>
	public static bool IsBeingEdited( HiderController hider )
	{
		if ( !hider.IsValid() || !Current.IsValid() )
			return false;
		var id = hider.GameObject.Id;
		foreach ( var (_, pawn) in Current.Editing )
		{
			if ( pawn == id )
				return true;
		}
		return false;
	}

	/// <summary>Is connection <paramref name="connectionId"/> one of this prop's editors?</summary>
	public static bool IsEditedBy( HiderController hider, Guid? connectionId )
		=> connectionId is { } id && hider.IsValid() && Current.IsValid()
		&& Current.Editing.TryGetValue( id, out var pawn ) && pawn == hider.GameObject.Id;

	/// <summary>Who holds this brush's lock, or null when it's free.</summary>
	public static Guid? BrushLockHolder( Guid brushId )
		=> Current.IsValid() && Current.BrushLocks.TryGetValue( brushId, out var holder ) ? holder : null;

	/// <summary>Is this brush locked by someone OTHER than this machine's player?</summary>
	public static bool BrushLockedByOther( Guid brushId )
		=> BrushLockHolder( brushId ) is { } h && h != Connection.Local?.Id;

	/// <summary>Caller starts sculpting the clay under their crosshair in place — the LMB Sculpt card.
	/// Arbitrated like <see cref="RequestPossess"/>: a released pawn prop is joined as-is; scene clay is
	/// CONVERTED first (the same clone-and-dress as a claim, but host-owned) and lands straight into released
	/// scenery, so it persists when the sculptors are done. Same validation as a claim: a hunter, in reach,
	/// not spamming, mode allows it. A player already editing another prop leaves that one first.</summary>
	[Rpc.Host]
	public void RequestEdit( GameObject target ) => EditFor( Rpc.Caller ?? Connection.Local, target );

	internal void EditFor( Connection c, GameObject target, bool ignoreReach = false )
	{
		var host = Host;
		if ( c is null || !target.IsValid() || host is null || !host.ClaimsAllowed || !host.LeasesAllowed )
			return;

		var pawn = host.ClaimantPawn( c );
		if ( !pawn.IsValid() || !pawn.Components.Get<HunterController>().IsValid() )
			return;

		if ( _possessGate.TryGetValue( c.Id, out var gate ) && gate > 0f )
			return;
		_possessGate[c.Id] = PossessCooldown;

		if ( !ignoreReach && pawn.WorldPosition.Distance( target.WorldPosition ) > PossessRange )
			return;

		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
		{
			if ( !ReleasedProps.ContainsKey( hider.GameObject.Id ) )
				return; // someone's body

			EndEditFor( c );
			Editing[c.Id] = hider.GameObject.Id;
			PlaySwapPop( PopSpot( hider.GameObject ) ); // the same pop a possession makes — "someone's on this prop now"
			return;
		}

		var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
		if ( !IsClaimable( sculpture ) || !_claimedScene.Add( sculpture.GameObject.Id ) )
			return;

		// Host-owned from birth (owner null) and scenery from the first frame — nobody wears it (BornScenery
		// rides the spawn snapshot, so every copy knows before the registry rows land).
		var converted = ConvertSceneProp( sculpture, null, scenery: true );
		if ( !converted.IsValid() )
		{
			_claimedScene.Remove( sculpture.GameObject.Id );
			return;
		}

		var prop = converted.Components.Get<HiderController>();
		prop.ReleaseControl();
		ReleasedProps[prop.GameObject.Id] = true;
		EndEditFor( c );
		Editing[c.Id] = prop.GameObject.Id;
		PlaySwapPop( PopSpot( prop.GameObject ) );
	}

	/// <summary>Caller is done sculpting — their session exited. Their locks go with it.</summary>
	[Rpc.Host]
	public void EndEdit() => EndEditFor( Rpc.Caller ?? Connection.Local, pop: true );

	// Host-only: drop a connection's editing row and every lock it held. The pop plays on the holder's own
	// exit only — the sweep's forced ends ride events that already pop (the R swap) or shouldn't (a leaver).
	internal void EndEditFor( Connection c, bool pop = false )
	{
		if ( c is null || !Editing.TryGetValue( c.Id, out var pawnId ) )
			return;

		Editing.Remove( c.Id );
		ReleaseLocksOf( c.Id );
		if ( pop )
			PlaySwapPop( PopSpot( Scene.Directory.FindByGuid( pawnId ) ) );
	}

	void ReleaseLocksOf( Guid connectionId )
	{
		foreach ( var (brushId, holder) in BrushLocks.ToList() )
		{
			if ( holder == connectionId )
				BrushLocks.Remove( brushId );
		}
	}

	/// <summary>Caller selected these brushes (csv of ids): take their locks. Granted per brush when it is
	/// free or already theirs; a brush someone else holds stays theirs — the caller's session reads the
	/// registry and drops it from the selection (see SculptEditSession.EnforceBrushLocks).</summary>
	[Rpc.Host]
	public void LockBrushes( string ids )
	{
		var c = Rpc.Caller ?? Connection.Local;
		if ( c is null || !Editing.ContainsKey( c.Id ) )
			return;

		foreach ( var id in ParseIds( ids ) )
		{
			if ( !BrushLocks.TryGetValue( id, out var holder ) || holder == c.Id )
				BrushLocks[id] = c.Id;
		}
	}

	/// <summary>Caller deselected these brushes: release the locks it holds on them.</summary>
	[Rpc.Host]
	public void UnlockBrushes( string ids )
	{
		var c = Rpc.Caller ?? Connection.Local;
		if ( c is null )
			return;

		foreach ( var id in ParseIds( ids ) )
		{
			if ( BrushLocks.TryGetValue( id, out var holder ) && holder == c.Id )
				BrushLocks.Remove( id );
		}
	}

	static IEnumerable<Guid> ParseIds( string csv )
	{
		if ( string.IsNullOrEmpty( csv ) )
			yield break;
		foreach ( var part in csv.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( Guid.TryParse( part, out var id ) )
				yield return id;
		}
	}

	/// <summary>An editor's COMMIT: <paramref name="changed"/> is a packed brush list (new or updated brushes,
	/// matched by id), <paramref name="removed"/> a csv of brush ids to drop, <paramref name="order"/> a csv of
	/// every authored brush id in the editor's order (empty = unchanged). Applied to the host's copy of
	/// <paramref name="root"/>'s clay and committed, so the pawn's sync publishes it. Reliable.</summary>
	[Rpc.Host]
	public void SubmitBrushes( GameObject root, string changed, string removed, string order )
		=> ApplyOps( Rpc.Caller ?? Connection.Local, root, changed, removed, order, commit: true );

	/// <summary>An editor's LIVE frame mid-gesture: changed brushes only, previewed (shadow proxy + the sync's
	/// own 20 Hz stream), never committed. Unreliable — only the latest matters.</summary>
	[Rpc.Host( NetFlags.UnreliableNoDelay )]
	public void StreamBrushes( GameObject root, string changed )
		=> ApplyOps( Rpc.Caller ?? Connection.Local, root, changed, null, null, commit: false );

	// Host-only: the one place remote edits enter a shared sculpt. Every brush an op touches must be free or
	// held by the caller; the result must pass the brush cap and the bounds gate as a whole, or the op is
	// dropped (the editor's own copy diverges until the next snapshot corrects it — the same stance the
	// receive-side bounds gate has always taken). The HOST's own editor never reaches here: its session has
	// already applied the change to this very copy and the commit funnel publishes it.
	void ApplyOps( Connection c, GameObject root, string changedPacked, string removedCsv, string orderCsv, bool commit )
	{
		if ( c is null || c.Id == Connection.Local?.Id || !root.IsValid() )
			return;

		var hider = root.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( !IsEditedBy( hider, c.Id ) )
			return;

		var sculpture = hider.DisguiseSculpture;
		if ( !sculpture.IsValid() || sculpture.Brushes is not { } current )
			return;

		var changed = SdfSculpture.DeserializeBrushes( SdfNetworkSync.Unpack( changedPacked ) );
		var work = new List<SdfBrush>( current );
		bool any = false;

		if ( changed is not null )
		{
			foreach ( var nb in changed )
			{
				if ( nb is null || nb.Damage )
					continue;
				if ( BrushLocks.TryGetValue( nb.Id, out var holder ) && holder != c.Id )
					continue; // someone else's brush

				int idx = work.FindIndex( b => b.Id == nb.Id );
				if ( idx >= 0 )
					work[idx] = nb;
				else
					work.Insert( AuthoredCount( work ), nb ); // new: just below the damage tail (order lands below)
				any = true;
			}
		}

		foreach ( var id in ParseIds( removedCsv ) )
		{
			if ( BrushLocks.TryGetValue( id, out var holder ) && holder != c.Id )
				continue;
			int idx = work.FindIndex( b => b.Id == id );
			if ( idx >= 0 && !work[idx].Damage )
			{
				work.RemoveAt( idx );
				any = true;
			}
		}

		// Reorder the authored prefix to the editor's order — only when it names exactly the authored set
		// (a stale order from before someone else's add/remove is ignored; the next commit carries a fresh one).
		var order = ParseIds( orderCsv ).ToList();
		int authored = AuthoredCount( work );
		if ( order.Count == authored && authored > 0 )
		{
			var byId = new Dictionary<Guid, SdfBrush>( authored );
			for ( int i = 0; i < authored; i++ )
				byId[work[i].Id] = work[i];
			if ( order.All( byId.ContainsKey ) && order.Distinct().Count() == authored )
			{
				bool differs = false;
				for ( int i = 0; i < authored && !differs; i++ )
					differs = work[i].Id != order[i];
				if ( differs )
				{
					for ( int i = 0; i < authored; i++ )
						work[i] = byId[order[i]];
					any = true;
				}
			}
		}

		if ( !any || work.Count > SdfBrushPacker.MaxBrushes )
			return;

		var bounds = sculpture.GameObject.Components.Get<SculptBounds>();
		if ( bounds.IsValid() && !bounds.ValidateIncoming( work, withVolume: commit ) )
			return;

		// The host may be editing this very sculpt itself: its session must read this as a shape LANDING (not
		// its own edit — SdfNetworkSync.Applying), and re-seat its selection by id once the list is swapped.
		var local = SculptEditSession.SharedSessionOn( sculpture );
		local?.CaptureSelectionIds();
		SdfNetworkSync.Applying = true;
		try
		{
			sculpture.Brushes = work;
			local?.AfterExternalApply();
			if ( commit )
				sculpture.Rebuild();
			else
				sculpture.RebuildShadowProxy();
		}
		finally
		{
			SdfNetworkSync.Applying = false;
		}
	}

	/// <summary>Debug seam: apply <paramref name="changedPacked"/> as if <paramref name="c"/> had sent it (see
	/// PossessionDebug's <c>mimi_dbg_remoteop</c>). Host-only.</summary>
	internal void DebugApplyOps( Connection c, GameObject root, string changedPacked, bool commit )
		=> ApplyOps( c, root, changedPacked, null, null, commit );

	/// <summary>Brushes before the damage tail of an arbitrary list (the static twin of
	/// <see cref="SdfSculpture.AuthoredBrushCount"/>).</summary>
	internal static int AuthoredCount( List<SdfBrush> list )
	{
		int n = list.Count;
		while ( n > 0 && list[n - 1].Damage )
			n--;
		return n;
	}

	// Host-only, per frame: an editing row must belong to a connected player whose current pawn is a hunter
	// (a swap to a prop, a leave, a mode-driven respawn all end it) on a pawn that still exists; a lock must
	// belong to an editor. The holder's own EndEdit covers the normal exit; this is the net under everything
	// that can't ask.
	void SweepEditors()
	{
		if ( Editing.Count == 0 && BrushLocks.Count == 0 )
			return;

		foreach ( var (holder, pawnId) in Editing.ToList() )
		{
			var conn = Connection.All.FirstOrDefault( x => x.Id == holder );
			var claimant = conn is not null ? Host?.ClaimantPawn( conn ) : null;
			bool holderIsHunter = claimant.IsValid() && claimant.Components.Get<HunterController>().IsValid();
			if ( !holderIsHunter || !Scene.Directory.FindByGuid( pawnId ).IsValid() )
			{
				Editing.Remove( holder );
				ReleaseLocksOf( holder );
			}
		}

		foreach ( var (brushId, holder) in BrushLocks.ToList() )
		{
			if ( !Editing.ContainsKey( holder ) )
				BrushLocks.Remove( brushId );
		}
	}

	protected override void OnUpdate()
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;
		SweepEditors();
	}

	// Host-only: pawns minted by ConvertSceneProp, by pawn GameObject id. The lobby reads this to tell borrowed
	// map furniture (release it back into the world on a role swap) from a player's own practice body (destroy
	// it, remembering the disguise). Host-only is enough — every consumer runs inside a host RPC.
	readonly HashSet<Guid> _converted = new();

	/// <summary>Was this pawn converted from a scene prop (it's map furniture being worn, not a body a player
	/// built from scratch)? Host-side answer only.</summary>
	public bool IsConverted( GameObject pawn )
		=> pawn.IsValid() && _converted.Contains( pawn.Id );

	/// <summary>Host-only. The pawn-presence heal republished a converted pawn under a fresh object id — carry
	/// the "this is borrowed map furniture" mark across, so a later role swap still RELEASES it back into the
	/// world instead of destroying it like a practice body. No-op if the old id wasn't marked.</summary>
	internal void TransferConverted( Guid oldPawnId, GameObject fresh )
	{
		if ( _converted.Remove( oldPawnId ) && fresh.IsValid() )
			_converted.Add( fresh.Id );
		if ( PossessedProps.Remove( oldPawnId ) && fresh.IsValid() )
			PossessedProps[fresh.Id] = true;
	}

	// Host-side per-caller gate on RequestPossess, same shape as RoundManager's shot gate: the RPC is the trust
	// boundary, so re-enforce a sane rate at it rather than trusting the client's own key repeat.
	const float PossessCooldown = 0.3f;
	readonly Dictionary<Guid, RealTimeUntil> _possessGate = new();

	// Scene props already converted (or mid-conversion) this session, by the ORIGINAL scene object's id. The
	// original's Destroy is deferred to end-of-frame, so without this two same-frame claims on one scene prop
	// would both pass the IsValid check and mint two clones.
	readonly HashSet<Guid> _claimedScene = new();

	protected override void OnEnabled()
	{
		Current = this;
		Interactions.RegisterSource( this );
	}

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
		Interactions.UnregisterSource( this );
	}

	/// <summary>Caller claims the clay under their crosshair — the E press. THE arbitration point: requests
	/// arrive serially on the host. A released PAWN prop is handed over directly, with the
	/// <see cref="ReleasedProps"/> registry remove doubling as the idempotency guard (it succeeds for the first
	/// claim only; every later claim is rejected). A SCENE prop is CONVERTED (see
	/// <see cref="ConvertSceneProp"/>), guarded by <see cref="_claimedScene"/>. Either way two players pressing
	/// E together get exactly one winner; the loser keeps their hunter. Validated like
	/// <see cref="RoundManager.ReportPropHit"/>: the caller must actually be a hunter here, within reach, and
	/// not spamming.</summary>
	[Rpc.Host]
	public void RequestPossess( GameObject target ) => PossessFor( Rpc.Caller, target );

	// Host-only: the claim itself, for connection c. Split from the RPC so debug tooling can claim on a client's
	// behalf (see PossessionDebug).
	internal void PossessFor( Connection c, GameObject target )
	{
		var host = Host;
		if ( c is null || !target.IsValid() || host is null || !host.ClaimsAllowed )
			return;

		// The claimant must currently be a hunter (their pawn is how we range-check, too).
		var pawn = host.ClaimantPawn( c );
		if ( !pawn.IsValid() || !pawn.Components.Get<HunterController>().IsValid() )
			return;

		if ( _possessGate.TryGetValue( c.Id, out var gate ) && gate > 0f )
			return;
		_possessGate[c.Id] = PossessCooldown;

		if ( pawn.WorldPosition.Distance( target.WorldPosition ) > PossessRange )
			return;

		// A released pawn prop: claim it as-is. The registry Remove is the idempotency guard — it succeeds for
		// exactly one caller, so the loser of a same-frame race returns here and keeps their hunter.
		var hider = target.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
		{
			if ( !ReleasedProps.Remove( hider.GameObject.Id ) )
				return; // already claimed (or still being worn)

			HandOver( c, pawn, hider, assignOwnership: true );
			return;
		}

		// Scene-placed clay: convert it into a pawn the claimant owns.
		var sculpture = target.Components.Get<SdfSculpture>( FindMode.EverythingInSelfAndAncestors );
		if ( !IsClaimable( sculpture ) || !_claimedScene.Add( sculpture.GameObject.Id ) )
			return; // not clay, someone's body, or a same-frame race already took it

		var converted = ConvertSceneProp( sculpture, c );
		if ( !converted.IsValid() )
		{
			_claimedScene.Remove( sculpture.GameObject.Id ); // conversion failed — the original is still there
			return;
		}

		HandOver( c, pawn, converted.Components.Get<HiderController>(), assignOwnership: false );
	}

	// The shared possession tail: swap the claimant's hunter for the prop. The host's bookkeeping callback runs
	// FIRST (it still needs the doomed hunter pawn — the face snapshot). assignOwnership is false for a
	// freshly-converted clone (it NetworkSpawned already owned by the claimant); true for a released pawn prop,
	// which is UNOWNED after its release's DropOwnership — and everything that maps a body to a player
	// (RosterIdOf → the roster pips, own-prop lookups) reads Network.Owner, so even a host self-claim assigns.
	void HandOver( Connection c, GameObject hunterPawn, HiderController prop, bool assignOwnership )
	{
		Host.OnClaimGranted( c, hunterPawn, prop );
		hunterPawn.Destroy();
		PossessedProps[prop.GameObject.Id] = true;
		PlaySwapPop( PopSpot( prop.GameObject ) );

		if ( assignOwnership && Networking.IsActive )
		{
			// The whole tree (see NetworkTree) — a root-only assign left the Disguise owned by whoever wore the
			// prop before, so THEIR machine kept authority over the clay the new claimant was sculpting.
			foreach ( var net in NetworkTree( prop.GameObject ) )
				net.Network.AssignOwnership( c );
		}

		// Tell the claimant (and only them) to resume control once the ownership change lands on their machine.
		// Their copy consumes it in OnUpdate — acting inside the RPC could race the ownership packet.
		using ( Rpc.FilterInclude( c ) )
		{
			prop.BeginPossession();
		}
	}

	/// <summary>Host-only: play the body-swap "pop" at <paramref name="at"/> on every machine — possessing a prop,
	/// popping out of one, the hunter ⇄ prop swap. Every swap is granted on the host, so this is called from
	/// the grant sites (HandOver here, the modes' swap/release RPCs), once per swap.</summary>
	public void PlaySwapPop( Vector3 at )
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;
		BroadcastSwapPop( at );
	}

	[Rpc.Broadcast]
	void BroadcastSwapPop( Vector3 at )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return;

		// Read off the mode's prop PREFAB (see HiderController.SwapPopSound) — every machine resolves it locally,
		// the same way the hover reads PropPrefab.
		var sound = Host?.PropPrefab?.Components.Get<HiderController>( FindMode.EverythingInSelf )?.SwapPopSound;
		if ( sound is not null )
			Sound.Play( sound, at );
	}

	// Where a pop for this body goes: the middle of its clay (a prop's root sits at its feet), else its origin.
	public static Vector3 PopSpot( GameObject pawn )
	{
		if ( !pawn.IsValid() )
			return Vector3.Zero;

		var hider = pawn.Components.Get<HiderController>();
		var clay = hider.IsValid() ? hider.DisguiseSculpture : null;
		if ( clay.IsValid() && Sdf.TryGetBounds( clay.Brushes, out var b ) )
			return clay.WorldTransform.PointToWorld( b.Center );

		return pawn.WorldPosition + Vector3.Up * 32f;
	}

	// Host-only. Turn a scene-placed sculpture into a live prop pawn: clone the prop prefab dressed in the
	// scene shape's brushes, positioned so the clay lands EXACTLY where it stood (the pawn root sits upright at
	// the shape's feet; any tilt/scale the scene object carried moves onto the disguise child — the shape must
	// never move on its own), then remove the original everywhere. Spawned owned by the claimant with ClearOwner
	// orphan mode, so the prop outlives a leaver and can be released back into the world.
	GameObject ConvertSceneProp( SdfSculpture sculpture, Connection owner, bool scenery = false )
	{
		var prefab = Host?.PropPrefab;
		if ( !prefab.IsValid() )
		{
			Log.Warning( "PropClaims: the host mode supplied no prop prefab — can't convert a scene prop." );
			return null;
		}

		var sceneT = sculpture.WorldTransform;
		var feet = Sdf.TryGetBounds( sculpture.Brushes, out var b )
			? sceneT.PointToWorld( new Vector3( b.Center.x, b.Center.y, b.Mins.z ) )
			: sceneT.Position;
		var rootT = new Transform( feet, Rotation.FromYaw( sceneT.Rotation.Yaw() ) );

		var pawn = prefab.Clone( new CloneConfig( rootT, startEnabled: false, name: owner is not null ? $"Claimed Prop {owner.DisplayName}" : "Shared Prop" ) );
		if ( !pawn.IsValid() )
			return null;

		// Dress while disabled (the first build ever started is the right shape), then override WearDisguise's
		// feet-at-origin lift with the exact composed placement — the lift assumes an upright unscaled shape,
		// and the scene original may be neither.
		HiderController.WearDisguise( pawn, sculpture.Brushes );

		// Remember which prefab the clay came from so the editor HUD's Prefab "Save" writes back over it (see
		// HiderController.DisguiseSource). NEAREST instance root, not outermost: a prop nested inside an
		// arrangement prefab still resolves to the prop's own file, not the arrangement's.
		var sourceRoot = sculpture.GameObject;
		while ( sourceRoot.IsValid() && !sourceRoot.IsPrefabInstanceRoot )
			sourceRoot = sourceRoot.Parent;
		var hider = pawn.Components.Get<HiderController>( includeDisabled: true );
		if ( hider.IsValid() )
		{
			hider.DisguiseSource = sourceRoot.IsValid() ? sourceRoot.PrefabInstanceSource : null;
			hider.BornScenery = scenery; // pre-spawn, so it ships in the snapshot — see HiderController.BornScenery
		}

		var disguise = pawn.Children.FirstOrDefault( ch => ch.Name == "Disguise" );
		if ( disguise.IsValid() )
		{
			var local = rootT.ToLocal( sceneT );
			disguise.LocalPosition = local.Position;
			disguise.LocalRotation = local.Rotation;
			disguise.LocalScale = local.Scale;
			CarryExtras( sculpture.GameObject, disguise );
		}

		// The clone replaces the original for everyone. Scene objects share ids from the scene file, so the
		// broadcast resolves the same object on every machine; joiners never see it — they receive the host's
		// live scene snapshot, where it's already gone.
		DestroySceneProp( sculpture.GameObject );

		pawn.Enabled = true;
		SetOrphanedModeTree( pawn, NetworkOrphaned.ClearOwner ); // the Disguise too — see NetworkTree
		pawn.NetworkSpawn( new NetworkSpawnOptions
		{
			Owner = owner,
			OrphanedMode = NetworkOrphaned.ClearOwner,
		} );
		_converted.Add( pawn.Id );
		return pawn;
	}

	// Components on a scene prop that aren't clay but belong to the OBJECT (the lobby radio's music and its
	// on/off), which would otherwise die with the original. Kept to an allowlist: the SDF stack is rebuilt by the
	// pawn prefab, and anything else on scenery (triggers, map logic) must not start riding a player around.
	// Interactables are carried wholesale — what a prop OFFERS is part of the object, so possessing it must not
	// strip its interactions from everyone else.
	internal static bool IsCarried( Component c ) => c is BaseSoundComponent or SyncedMusic or IInteractable;

	// Host-only, pawn still disabled. Copy each carried component off the original onto the disguise body — it
	// sits at exactly the original's transform, so a positional sound stays put, then follows the prop. Copied
	// through the serializer with the id stripped (the original still exists until end-of-frame; two components
	// can't share a guid). Added before NetworkSpawn, so it rides the spawn snapshot to every machine; the pawn
	// then persists through release and re-claim, so the component comes along for free.
	static void CarryExtras( GameObject from, GameObject to )
	{
		foreach ( var c in from.Components.GetAll( FindMode.EverythingInSelf ) )
		{
			if ( !IsCarried( c ) || c.Serialize() is not JsonObject json )
				continue;

			json.Remove( "__guid" );
			var copy = to.Components.Create( TypeLibrary.GetType( c.GetType() ), startEnabled: false );
			if ( copy is null )
				continue;

			copy.DeserializeImmediately( json );
			copy.Enabled = c.Enabled;
		}
	}

	/// <summary>A pawn's networked objects: the root plus every descendant that is its OWN network object. The
	/// Disguise child is one — disguise.prefab is NetworkMode.Object, so when the pawn is NetworkSpawn'd the engine
	/// spawns the dressed Disguise alongside it as a separate network object, with the same owner but its OWN
	/// orphan action (the prefab's Destroy). So any ownership/orphan rule meant for "the prop" has to be applied to
	/// all of them, or the clay quietly keeps the old rule: a release that only dropped the root left the Disguise
	/// owned by the leaver, and their disconnect destroyed it — the pawn root fell forever with no clay or collider
	/// (the fall respawn kept teleporting it back to its spawn spot, which is what late joiners saw).</summary>
	internal static IEnumerable<GameObject> NetworkTree( GameObject root )
	{
		if ( !root.IsValid() )
			yield break;

		yield return root;
		foreach ( var go in root.GetAllObjects( false ) )
		{
			if ( go != root && go.NetworkMode == NetworkMode.Object && go.Network.Active )
				yield return go;
		}
	}

	/// <summary>Before a NetworkSpawn: give every object that will spawn as its own network object (see
	/// <see cref="NetworkTree"/>) the same orphan action as the root. Pre-spawn only — the engine reads it at the
	/// spawn.</summary>
	internal static void SetOrphanedModeTree( GameObject root, NetworkOrphaned mode )
	{
		if ( !root.IsValid() )
			return;

		foreach ( var go in root.GetAllObjects( false ) )
		{
			if ( go != root && go.NetworkMode == NetworkMode.Object )
				go.Network.SetOrphanedMode( mode );
		}
	}

	/// <summary>Host→everyone: remove a scene prop that was just converted — each machine destroys its own copy
	/// of the scene object (a scene object can't be despawned through the network; it was never on it).</summary>
	[Rpc.Broadcast]
	void DestroySceneProp( GameObject go )
	{
		if ( Rpc.Caller is not null && !Rpc.Caller.IsHost )
			return; // only the host converts

		if ( go.IsValid() )
			go.Destroy();
	}

	/// <summary>Host-only: hand a prop off into the world as claimable scenery. Dormant on the host (which keeps
	/// simulating it — gravity and ground-snap still settle it), ownership dropped (the ex-owner's copy becomes
	/// a proxy; StopControl tears down their edit state), then registered released — in THIS component's [Sync]
	/// registry, whose replication doesn't depend on the pawn's just-changed ownership.</summary>
	public void Release( HiderController hider )
	{
		if ( !hider.IsValid() )
			return;

		hider.ReleaseControl();
		if ( Networking.IsActive && hider.GameObject.Network.Active )
		{
			// The WHOLE network tree, not just the root — see NetworkTree: a Disguise left owned by the leaver is
			// destroyed by the engine's orphan pass when they disconnect, taking the clay and its collider with it.
			foreach ( var net in NetworkTree( hider.GameObject ) )
				net.Network.DropOwnership();
		}
		PossessedProps.Remove( hider.GameObject.Id );
		ReleasedProps[hider.GameObject.Id] = true;
	}

	/// <summary>Where a hunter appears when its player releases a prop in place: stepped back from the prop
	/// along the caller's view (so the prop they just placed is right in front of them), clear of the disguise's
	/// hull by its own horizontal radius plus a body's worth of margin — a pawn spawned inside the released
	/// disguise's collider gets solver-shoved, sometimes through the floor — grounded on whatever the prop
	/// stands on.</summary>
	public Transform HunterSpotClearOf( HiderController hider, float viewYaw )
	{
		var basePos = hider.TryGetShapeFeet( out var feet ) ? feet : hider.WorldPosition;

		// Horizontal half-extent of the disguise, world-scaled — how far the hull reaches from the origin.
		var radius = 24f;
		var disguise = hider.DisguiseSculpture;
		if ( disguise.IsValid() && Sdf.TryGetBounds( disguise.Brushes, out var bounds ) )
			radius = MathF.Max( bounds.Size.x, bounds.Size.y ) * 0.5f * disguise.WorldScale.x;

		var back = Rotation.FromYaw( viewYaw ).Backward;
		var pos = basePos + back * (radius + 40f);

		// Ground the spot so a big prop on a slope doesn't leave the hunter floating; +64 lift for the drop.
		var tr = Scene.Trace.Ray( pos + Vector3.Up * 96f, pos - Vector3.Up * 128f )
			.IgnoreGameObjectHierarchy( hider.GameObject )
			.Run();
		if ( tr.Hit )
			pos = tr.HitPosition;

		return new Transform( pos + Vector3.Up * 64f, Rotation.FromYaw( viewYaw ) );
	}
}
