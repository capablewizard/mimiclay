using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Network;

namespace Mimiclay;

/// <summary>
/// Creative mode: a group of hunters populating the map with props, sandbox style. Spawned into the chosen map
/// by <see cref="RoundManagerSpawner"/> (the mode key the lobby stamped decides between this and
/// <see cref="RoundManager"/>). No phases, no timers, no win state — the host's pause-menu "Return to Lobby" is
/// the way out.
///
/// The loop: everyone spawns as a HUNTER. P swaps you to a fresh prop pawn to sculpt; P again returns you to a
/// hunter and the prop is RELEASED — it persists in the world as scenery (<see cref="HiderController.ReleaseControl"/>,
/// host keeps simulating it). Aiming at a released prop as a hunter outlines it with an "E to Edit" prompt; E
/// POSSESSES it — your hunter despawns and you take the prop over, landing in edit mode.
///
/// <b>Claims.</b> The whole hover/claim/convert flow lives in <see cref="PropClaims"/>, the shared service
/// spawned beside this manager (this manager is its <see cref="IPropClaimHost"/>): the same machinery the lobby
/// hosts for its editable props. This class keeps only what is creative's own — the roster, the pawn spawning,
/// and the P swap.
///
/// <b>Networking.</b> This is the LOBBY's pawn model, not the round's: a NetworkSpawn'd singleton (a scene-placed
/// component's [Sync] changes don't replicate) where the HOST spawns every pawn and hands each to its owner —
/// because possession is an ownership CLAIM, and claims must be arbitrated in one place
/// (<see cref="PropClaims.RequestPossess"/>).
/// </summary>
[Title( "Creative Manager" )]
[Category( "Mimiclay" )]
[Icon( "brush" )]
public sealed class CreativeManager : Component, IRoundContext, IPropClaimHost
{
	/// <summary>The live creative manager (null everywhere but a creative map). HUD + input read this to know
	/// creative rules apply.</summary>
	public static CreativeManager Current { get; private set; }

	// The claim service sharing this GameObject (spawned beside us by RoundManagerSpawner, so they replicate
	// and die together).
	PropClaims Claims => Components.Get<PropClaims>();

	// ── Networked state (host writes, everyone reads — incl. late-joiners via the spawn snapshot) ─────────────
	/// <summary>Per-player state for the roster row at the top of the HUD. Role mirrors what each player is
	/// currently being (hunter or prop) so the pips read right; nominations/scores don't exist here.</summary>
	[Sync] public NetDictionary<Guid, PlayerInfo> Players { get; private set; } = new();

	/// <summary>Session-wide sculpt size-limit bypass — the map-making tool. Host writes (via
	/// <see cref="RequestBoundsBypass"/>), every machine mirrors it into
	/// <see cref="SculptBounds.SessionBypass"/> per frame; late joiners get the current state off the spawn
	/// snapshot. Session-wide is the point: it opens everyone's receive gate
	/// (<see cref="SculptBounds.ValidateIncoming"/>), so oversized builds actually replicate instead of
	/// being rejected on every other machine like the local-only bypass.</summary>
	[Sync] public bool BoundsBypass { get; private set; }

	// ── Host-only pawn bookkeeping ─────────────────────────────────────────────────────────────────────────────
	// The pawn each player currently IS. Released props leave this map — they belong to nobody.
	readonly Dictionary<Guid, GameObject> _pawns = new();
	readonly HashSet<Guid> _known = new();

	// What each player's last HUNTER pawn's face looked like, snapshotted as that pawn is destroyed and put on
	// their next one — the host can't read a client's saved head off their disk, so without this every P/E swap
	// would flash the prefab-default head at everyone until the client's own dress published back. Faces only:
	// there is no disguise memory here, because a swapped-away prop isn't destroyed — it PERSISTS in the world.
	readonly Dictionary<Guid, List<SdfBrush>> _faces = new();

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	// ── IRoundContext ──────────────────────────────────────────────────────────────────────────────────────────
	// Lobby phase, deliberately: it's the phase with no round furniture, and it's what makes RoundHud lay out
	// the all-players roster row (the "player list from lobby mode"). Never a timer — creative doesn't end.
	RoundPhase IRoundContext.Phase => RoundPhase.Lobby;
	float IRoundContext.TimeRemaining => 0f;
	bool IRoundContext.HasTimer => false;

	// ── IPropClaimHost (the claim flow itself lives in PropClaims, beside us) ──────────────────────────────────
	bool IPropClaimHost.ClaimsAllowed => true; // creative never closes claims — it has no countdowns to guard

	GameObject IPropClaimHost.PropPrefab
		=> RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.PropPrefab : null;

	GameObject IPropClaimHost.ClaimantPawn( Connection c ) => _pawns.GetValueOrDefault( c.Id );

	void IPropClaimHost.OnClaimGranted( Connection c, GameObject hunterPawn, HiderController prop )
	{
		RememberFace( c.Id, hunterPawn ); // the hunter pawn dies right after this returns
		if ( Players.TryGetValue( c.Id, out var row ) )
		{
			row.Role = PlayerRole.Prop;
			Players[c.Id] = row;
		}
		_pawns[c.Id] = prop.GameObject;
	}

	protected override void OnEnabled()
	{
		Current = this;
		RoundContext.Active = this;
	}

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
		if ( ReferenceEquals( RoundContext.Active, this ) ) RoundContext.Active = null;
		SculptBounds.SetSessionBypass( false ); // the mirror dies with its source — limits back on outside creative
		ClearWorkshopUi();
	}

	protected override void OnStart()
	{
		// EVERY machine (clients run this when the spawned manager replicates — after their scene is up): read
		// the host's creative rules off session data. Lobby data reaches all members, and what it drives here
		// is machine-local work, so nothing needs to ride [Sync].
		var settings = CreativeSettings.ReadFromLobby();

		// "Spawn Props" off = blank canvas: delete the map's pre-placed clay. A LOCAL, deterministic sweep on
		// each machine rather than host broadcasts — scene objects come identical from the scene file, so every
		// machine (late joiners included) resolves the same set, with zero network traffic. Player-made and
		// converted props all live under pawn controllers, so they can never match; running at start-only is
		// therefore also correct for a joiner arriving mid-session.
		if ( !settings.SpawnProps )
		{
			foreach ( var sculpture in Scene.GetAllComponents<SdfSculpture>().ToList() )
			{
				if ( PropClaims.IsScenery( sculpture ) )
					sculpture.GameObject.Destroy();
			}
		}
	}

	protected override void OnUpdate()
	{
		HandleInput();
		UpdateWorkshopUi();

		// Every machine: mirror the networked bypass into the static the checks read. Per-frame assert
		// rather than a change hook, so a late joiner's snapshot value lands without any event plumbing.
		SculptBounds.SetSessionBypass( BoundsBypass );

		if ( !IsHostAuthority )
			return;

		ReconcileConnections();
	}

	// ── Workshop column on the edit HUD (local UI, every machine) ─────────────────────────────────────────────
	// Creative mounts the customiser's Save/Load To Workshop buttons on the in-game EditHud whenever the local
	// player is editing: prop-flavored on a prop pawn, head-flavored on a hunter's face edit (recognised by the
	// head persist slot) — so a face saved here lands in the same workshop library the menu customiser browses,
	// and props keep a library of their own. The wiring is rebuilt per SESSION: a P/E swap tears the old session
	// down, and any async op it left in flight is abandoned by its alive check the moment IsEditing drops.

	EditHud _hud;
	SculptWorkshop _workshop;
	SculptEditSession _workshopSession; // the session the current wiring was built for

	// The scene's EditHud, found once (it's scene-placed in the map scenes; null-safe if a map lacks one).
	EditHud Hud => _hud.IsValid() ? _hud : (_hud = Scene.GetAllComponents<EditHud>().FirstOrDefault());

	void UpdateWorkshopUi()
	{
		var session = SculptEditSession.Current;
		if ( session.IsValid() && session.IsEditing )
		{
			if ( session == _workshopSession || !Hud.IsValid() )
				return;

			ClearWorkshopUi(); // a different session was wired — drop its browser state before rebinding

			var s = session; // captured per wiring, so a later Current can't be answered for
			Func<bool> alive = () => s.IsValid() && s.IsEditing;
			_workshopSession = s;
			_workshop = s.PersistSlot == SculptLibrary.HeadSlot
				? SculptWorkshop.ForHeads( () => Hud, () => s, alive )
				: SculptWorkshop.ForProps( () => Hud, () => s, alive );

			Hud.WorkshopSave = _workshop.Save;
			Hud.WorkshopLoad = _workshop.Load;
			Hud.WorkshopClose = _workshop.Close;
			return;
		}

		if ( _workshopSession is not null )
			ClearWorkshopUi();
	}

	void ClearWorkshopUi()
	{
		_workshopSession = null;
		_workshop = null;

		// _hud directly, not the finder: on teardown there's nothing to search the scene for.
		if ( !_hud.IsValid() )
			return;

		_hud.WorkshopSave = null;
		_hud.WorkshopLoad = null;
		_hud.WorkshopClose = null;
		_hud.WorkshopBrowserOpen = false;
		_hud.WorkshopItems = null;
		_hud.WorkshopStatus = null;
	}

	// A P press parked on the edit session's revert confirmation (see TrySwap). Local UI state, resolved when
	// the dialog closes: confirmed (the session exited, reverted) completes the swap; "Keep Editing" drops it.
	bool _swapAwaitingConfirm;

	// Every machine: the P swap, same raw-key shape as LobbyController.HandleDebugInput (the request is
	// [Rpc.Host], so a client's press routes to the host). The camera carry is owner-side state the host never
	// sees — captured at the press, consumed by our replacement pawn as it starts.
	void HandleInput()
	{
		if ( PauseMenu.IsOpen )
			return;

		// A swap waiting on the revert dialog: complete or drop it the frame the player answers. Further P
		// presses are swallowed while it's up (the dialog's scrim owns the HUD's input; this is the raw-key
		// equivalent).
		if ( _swapAwaitingConfirm )
		{
			var session = SculptEditSession.Current;
			if ( !session.IsValid() )
			{
				_swapAwaitingConfirm = false; // forced teardown took the session — nothing left to complete
			}
			else if ( !session.ExitConfirmPending )
			{
				_swapAwaitingConfirm = false;
				if ( !session.IsEditing )
					Swap(); // confirmed: reverted + exited — finish what the P press started
				// still editing = "Keep Editing" — the swap is dropped with it
			}
			return;
		}

		if ( Input.Keyboard.Pressed( "P" ) )
			TrySwap();
	}

	// The P press. Exiting your pawn is also exiting its edit session, so an ACTIVE session goes through the
	// same exit gate the Q toggle uses (SculptEditSession.RequestExit): a too-big/too-small sculpt raises the
	// revert confirmation instead of the swap silently releasing an invalid shape into the world, and the swap
	// waits on the player's answer. A valid session just exits cleanly first — persist included, better than
	// the force-teardown the release would otherwise run. The hunter's face edit gets the same treatment.
	void TrySwap()
	{
		var session = SculptEditSession.Current;
		if ( session.IsValid() && session.IsEditing )
		{
			session.RequestExit();
			if ( session.ExitConfirmPending )
			{
				_swapAwaitingConfirm = true;
				return;
			}
		}

		Swap();
	}

	void Swap()
	{
		LobbySwapCarry.Capture( Scene, OwnProp() );
		RequestSwap( Scene.Camera.IsValid() ? Scene.Camera.WorldRotation.Angles().yaw : 0f );
	}

	// Our own hider pawn if we're currently a prop — RosterIdOf, not IsProxy, for symmetry with the lobby's
	// version (and so a future host-held pawn can never answer for the host's own).
	HiderController OwnProp()
	{
		if ( Connection.Local?.Id is not { } id )
			return null;

		foreach ( var p in Scene.GetAllComponents<HiderController>() )
			if ( p.IsValid() && !PropClaims.IsReleased( p ) && RoundManager.RosterIdOf( p.GameObject ) == id )
				return p;

		return null;
	}

	// ── Host: roster + join/leave ──────────────────────────────────────────────────────────────────────────────
	// Auto-spawn a hunter for each connection; drop leavers' rows. A leaver's PROP is released into the world —
	// their work survives them (its spawn options ClearOwner instead of the prefab's destroy-on-orphan, and this
	// sweep finishes the job by marking it dormant + Released) — while a leaver's hunter just goes away.
	void ReconcileConnections()
	{
		_known.RemoveWhere( id => !Connection.All.Any( c => c.Id == id ) );
		foreach ( var c in Connection.All )
		{
			if ( _known.Add( c.Id ) )
			{
				Players[c.Id] = new PlayerInfo
				{
					Connection = c.Id,
					Name = c.DisplayName,
					Role = PlayerRole.Hunter,
					Alive = true,
					SpawnIndex = NextFreeSpawnIndex(),
				};
				SpawnHunter( c, HunterSpawnSpot( Players[c.Id].SpawnIndex ) );
			}
		}

		foreach ( var id in Players.Keys.ToList() )
		{
			if ( Connection.All.Any( c => c.Id == id ) )
				continue;

			Players.Remove( id );
			_faces.Remove( id );
			if ( _pawns.Remove( id, out var pawn ) && pawn.IsValid() )
			{
				var hider = pawn.Components.Get<HiderController>();
				if ( hider.IsValid() )
					Claims.Release( hider ); // the prop stays — creative worlds keep what leavers built
				else
					pawn.Destroy();
			}
		}
	}

	// Lowest roster slot no current row holds, so joiners fill gaps instead of marching down the spawn ring.
	int NextFreeSpawnIndex()
	{
		var used = Players.Values.Select( p => p.SpawnIndex ).ToHashSet();
		var i = 0;
		while ( used.Contains( i ) ) i++;
		return i;
	}

	// ── Client → host requests ─────────────────────────────────────────────────────────────────────────────────
	/// <summary>Caller toggles hunter ↔ prop. Hunter → a fresh default prop where they stand (sculpt away).
	/// Prop → back to a hunter — and unlike the lobby's practice bodies, the prop is NOT destroyed: it's released
	/// into the world (that's the whole game — populating the map). <paramref name="viewYaw"/> is the caller's
	/// camera yaw at the press, so the new hunter spawns aiming along the view they had.</summary>
	[Rpc.Host]
	public void RequestSwap( float viewYaw )
	{
		var c = Rpc.Caller;
		if ( c is null || !Players.TryGetValue( c.Id, out var row ) )
			return;

		var pawn = _pawns.GetValueOrDefault( c.Id );
		var hider = pawn.IsValid() ? pawn.Components.Get<HiderController>() : null;

		if ( hider.IsValid() )
		{
			// Prop → hunter. Release the prop first (it persists), then spawn the hunter CLEAR of its hull —
			// a pawn spawned inside the released disguise's collider gets solver-shoved, sometimes through the
			// floor (the lobby destroys the old pawn for exactly this reason; we can't, so we step aside).
			var at = Claims.HunterSpotClearOf( hider, viewYaw );
			_pawns.Remove( c.Id );
			Claims.Release( hider );

			row.Role = PlayerRole.Hunter;
			Players[c.Id] = row;
			SpawnHunter( c, at );
		}
		else
		{
			// Hunter → prop, in place. The face is remembered off the departing pawn so the next hunter spawn
			// doesn't flash the default head.
			var at = pawn.IsValid() ? pawn.WorldTransform : HunterSpawnSpot( row.SpawnIndex );
			if ( _pawns.Remove( c.Id, out var old ) && old.IsValid() )
			{
				RememberFace( c.Id, old );
				old.Destroy();
			}

			row.Role = PlayerRole.Prop;
			Players[c.Id] = row;
			SpawnProp( c, at );
		}
	}

	/// <summary>Set the session-wide size-limit bypass (see <see cref="BoundsBypass"/>). Explicit state, not
	/// a toggle, so two players flipping at once converge instead of double-flipping. Any player may call it —
	/// creative is collaborative map-building, and the toggle is loudly logged on every machine by the mirror
	/// (<see cref="SculptBounds.SetSessionBypass"/>).</summary>
	[Rpc.Host]
	public void RequestBoundsBypass( bool on )
	{
		if ( BoundsBypass == on )
			return;

		BoundsBypass = on;
		Log.Info( $"CreativeManager: {Rpc.Caller?.DisplayName ?? "host"} turned the session size-limit bypass {(on ? "ON" : "OFF")}." );
	}

	// ── Spawning (host-only; prefabs + spawn points read off the scene) ────────────────────────────────────────
	void SpawnHunter( Connection owner, Transform at )
	{
		var prefab = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.HunterPrefab : null;
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"CreativeManager: no hunter prefab on the spawner — can't spawn for {owner.DisplayName}." );
			return;
		}

		// A hunter spawns facing its owner's view (the engine controller seeds EyeAngles from spawn rotation).
		// Dress before enable, same as everywhere: an enabled clone already has the default face's build in
		// flight before a post-clone dress could swap the brushes — that build landing first is the flash.
		var dressLocalHead = owner.Id == Connection.Local?.Id;
		var dressCachedFace = !dressLocalHead && _faces.TryGetValue( owner.Id, out var face ) && face is { Count: > 0 };
		var dress = dressLocalHead || dressCachedFace;

		var pawn = prefab.Clone( new CloneConfig( at, startEnabled: !dress, name: $"Creative Hunter {owner.DisplayName}" ) );
		if ( !pawn.IsValid() )
			return;

		if ( dress )
		{
			if ( dressLocalHead )
				HunterController.WearSavedHead( pawn );
			else
				HunterController.WearFace( pawn, _faces[owner.Id] );
			pawn.Enabled = true;
		}

		pawn.NetworkSpawn( owner );
		_pawns[owner.Id] = pawn;
	}

	void SpawnProp( Connection owner, Transform at )
	{
		var prefab = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.PropPrefab : null;
		if ( !prefab.IsValid() )
		{
			Log.Warning( $"CreativeManager: no prop prefab on the spawner — can't spawn for {owner.DisplayName}." );
			return;
		}

		var pawn = prefab.Clone( new CloneConfig( at, startEnabled: true, name: $"Creative Prop {owner.DisplayName}" ) );
		if ( !pawn.IsValid() )
			return;

		// ClearOwner, not the prefab's authored Destroy: creative props must OUTLIVE their player — a leaver's
		// prop would otherwise be engine-destroyed at the disconnect, before the reconcile sweep can release it
		// into the world. ClearOwner (not Host) so the ownerless frame before the sweep runs can't read as the
		// HOST's own pawn anywhere (RosterIdOf answers null for unowned, host-id for host-owned).
		pawn.NetworkSpawn( new NetworkSpawnOptions
		{
			Owner = owner,
			OrphanedMode = NetworkOrphaned.ClearOwner,
		} );
		_pawns[owner.Id] = pawn;
	}

	// The initial hunter spawn spot for a roster slot: the map's hunter start points, ringed once they run out
	// (same de-stack rule as RoundManager.PickSpot). Lifted +64 so the pawn drops onto the floor.
	Transform HunterSpawnSpot( int index )
	{
		var spots = RoundSpawnPoint.AllOfKind( Scene, hunterStart: true );
		var origin = spots.Count > 0 ? spots[index % spots.Count].GameObject : GameObject;
		var stack = spots.Count > 0 ? index / spots.Count : index;
		return new Transform(
			origin.WorldPosition + RoundSpawnPoint.StackOffset( stack ) + Vector3.Up * 64f,
			Rotation.FromYaw( origin.WorldRotation.Yaw() ) );
	}

	// Snapshot the face off a departing hunter pawn (see _faces). Brush-by-brush copies, so nothing that later
	// mutates the about-to-die live list can reach the cache.
	void RememberFace( Guid id, GameObject pawn )
	{
		var face = HunterController.ResolveFaceOf( pawn );
		if ( face.IsValid() && face.Brushes is { Count: > 0 } )
			_faces[id] = face.Brushes.Select( b => b.Copy() ).ToList();
	}
}
