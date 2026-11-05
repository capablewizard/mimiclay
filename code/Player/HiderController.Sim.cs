using System;
using System.Collections.Generic;
using System.Linq;
using Mimiclay.Sim;

namespace Mimiclay;

// Phase 2a of the rollback-physics integration (networked-physics-authority memory): a player's prop WALKS in the shared
// simulation world (SimWorld) — host-authoritative, its owner predicting it from its own input and rolling back on a
// snapshot that disagrees, everyone else showing it from the sim. The walk is the same floating-body walk as
// HiderController.Walk, ported to run on the sim body from (input, controller state) alone, so the host and every
// client replay produce the same motion:
//
//  • INPUT (SimInput): the world-space wish, jump and duck, and the mouse turn (degrees this tick) — read by the owner
//    once per sim tick (ReadSimInput), sent to the host with every tick's.
//  • STATE (SimCtrl, on the SimBody): grounded, parked, jump / push-back timers in TICKS (wall-clock timers can't replay),
//    the last commanded velocity. Rolled back with the body; snapshots carry it.
//  • SHAPE: the move hull with its bottom cut off (the floating shape — PropMoveHull.FloatingVertices), built on every
//    machine from its own copy of the pawn (ISimShapeSource). The ground probe is the hull's foot slab swept through
//    the SIM world (a second PropMoveHull whose scratch bodies live there).
//
// The scene pawn becomes presentation: keyframed (Character mode), its transform set from the sim every frame
// (SimBodyFlags.Present — the host also stops the engine syncing it). Gated by mimi_sim_walk (default OFF).
//
// NOT in 2a (falls back to nothing while sim-walking): climbing, mantling, the possession cling, the charades leash's
// pull-in, released scenery, bodies being held (they leave the sim for the old lend path, like decoys).
public sealed partial class HiderController : ISimShapeSource
{
	// Replicated: the host's value is everyone's (only the host's matters — it registers the bodies), so every machine's
	// readout (DrawSimWalkHud) shows the real setting.
	// ON by default since 2026-11-05: the sim walk, hold and loose paths are the tested ones (see the networked-physics-authority
	// notes); off = the old scene-physics walk and the lend-based grab, kept for A/B. Climbing is unavailable while on.
	[ConVar( "mimi_sim_walk", Flags = ConVarFlags.Replicated, Help = "Players' props, holds and loose props live in the shared sim world (host-authoritative, owner-predicted); 0 = the old scene-physics walk and lend-based grab. Climbing is unavailable while on. Host only." )]
	public static bool SimWalk { get; set; } = true;

	[ConVar( "mimi_sim_hud", Help = "Show the sim-walk readout (top left) while you control a prop." )]
	public static bool SimHud { get; set; } = true;

	// Owner, per frame: one line saying what mimi_sim_walk is and whether YOUR prop walks in the sim (and if not, why).
	void DrawSimWalkHud()
	{
		if ( !SimHud || IsProxy || RoundManager.IsBotPawn( GameObject ) || PropClaims.IsReleased( this ) )
			return;
		if ( Networking.IsActive && Network.Owner != Connection.Local )
			return;

		string setting = SimWalk ? "ON" : "off";
		string state;
		Color color;
		if ( SimWalking )
		{
			var sim = SimSystem;
			string lead = sim is not null && !sim.IsAuthority ? $" · predicting {sim.LeadTicks} ticks ahead" : "";
			state = $"your prop: SIM-WALKING (no climbing){lead}";
			color = Color.Green;
		}
		else
		{
			state = $"your prop: old walk{(SimWalk ? $" — {SimWalkBlocker() ?? "not registered yet"}" : "")}";
			color = SimWalk ? Color.Yellow : Color.Gray;
		}
		DebugOverlay.ScreenText( new Vector2( 20, 120 ), $"Sim walk: {setting}  ·  {state}", size: 14, flags: TextFlag.LeftTop, color: color );
	}

	public const byte SimWalkDriverId = 2;

	// The push level (SimStep): the share of last tick's command the contacts took off that starts counting as a push,
	// the share that counts as pushing fully, and how long the level takes to fall from full to none. The scene walk's
	// switch fired at 20% and held 0.25 s — the blend brackets that.
	const float PushOnset = 0.05f;
	const float PushFull = 0.3f;
	const float PushRelease = 0.25f;
	const float SlideFriction = 0.8f; // lying down, driven on the ground: the floor friction the frictionless slide stands in for (the clay surface's)

	// The move hull again, its scratch bodies (foot slab) in the SIM world — sweeps must run in the world they hit.
	readonly PropMoveHull _simHull = new();

	SimWorld SimSystem => Scene.GetSystem<SimWorld>();

	/// <summary>This pawn's body in the sim world, if it walks there.</summary>
	// The host decides (UpdateSimRegistration) — every machine just asks the sim, whatever its own mimi_sim_walk says.
	internal SimBody SimSelf => SimSystem is { Active: true } sim && sim.Find( GameObject.Id ) is { } b && b.Spec.Driver == SimWalkDriverId ? b : null;

	/// <summary>Does this pawn walk in the sim world right now (on any machine)?</summary>
	internal bool SimWalking => SimSelf is not null;

	// ── Shape (every machine) ───────────────────────────────────────────────────────────────────────────────

	void RefreshSceneHull()
	{
		if ( _collider.IsValid() )
			_hull.Refresh( _collider, Scene.PhysicsWorld );
	}

	int ISimShapeSource.SimShapeVersion
	{
		get
		{
			RefreshSceneHull();
			// The ghosting tag rides the shape too (it decides who it passes through), so a tag flip rebuilds it.
			bool ghost = _body.IsValid() && _body.GameObject.Tags.Has( PropBodyTag );
			return _hull.Version * 2 + (ghost ? 1 : 0);
		}
	}

	bool ISimShapeSource.BuildSimShapes( PhysicsBody body )
	{
		RefreshSceneHull();
		if ( !_hull.Ready )
			return false;
		var shape = body.AddHullShape( Vector3.Zero, Rotation.Identity, _hull.FloatingVertices( ScaledStep( StepHeight ) ) );
		if ( shape is null )
			return false;
		shape.Friction = 0f; // slides and turns against walls (see UseHullCollision)
		if ( _body.IsValid() )
			shape.Tags.SetFrom( _body.GameObject.Tags ); // the disguise's collision rules — propbody ghosting included
		return true;
	}

	// ── Registration (host) ─────────────────────────────────────────────────────────────────────────────────

	// Host, every frame, for every pawn (proxies included — a client's pawn is a proxy here): put it in the sim while it
	// should walk there, take it out when it shouldn't.
	void UpdateSimRegistration()
	{
		if ( !Networking.IsHost && Networking.IsActive )
			return;
		var sim = SimSystem;
		if ( sim is null )
			return;

		var existing = sim.Find( GameObject.Id );
		var owner = Network.Owner?.Id ?? Connection.Local?.Id ?? Guid.Empty;
		// Three kinds, in precedence: HELD (in a hunter's hands — the hold driver, owned by the holder; see SimHoldDriver),
		// LOOSE (a rigid body: dropped scenery, or a player's or bot's body let go of until it stands up), a WALKER.
		var holder = PropClaims.HolderOf( this );
		bool held = holder is { } && SimHoldApplies( this ) && _body.IsValid() && _collider.IsValid();
		if ( held )
		{
			_sinceSimHoldRowGone = 0f; // (how long since the Held row went: the loose redrive waits on the release tick, briefly)
		}
		// A player's or bot's body (not released scenery, which the Loose registry governs) let go of is DOWN (SimDownDriver):
		// the walk overturned once it has settled (the move keys slide it, the mouse turns it), righted with SPACE — the first
		// jump is the flip onto its feet; a bot rights itself once it has lain still. Up again (SimDownDriver.Done), it's a walker.
		if ( !held && existing is { Spec.Driver: SimHoldDriver.Id } && !PropClaims.IsReleased( this ) )
			_simDown = true;
		if ( _simDown && existing is { Spec.Driver: SimDownDriver.Id } d && SimDownDriver.Done( d ) )
			_simDown = false;
		if ( held || PropClaims.IsReleased( this ) ) // released (unpossessed): a physics item from here, not a bot that rights itself
			_simDown = false;
		bool down = !held && SimWalk && _simDown && Network.Active && _body.IsValid() && _collider.IsValid();
		bool loose = !held && !down && SimWalk && SimLooseEligible();
		bool walk = !held && !down && !loose && SimWalk && SimWalkEligible();

		byte driver = held ? SimHoldDriver.Id : down ? SimDownDriver.Id : walk ? SimWalkDriverId : (byte)0;
		var drvOwner = held ? holder.Value : down || walk ? owner : Guid.Empty;
		var flags = held ? SimBodyFlags.Present | SimBodyFlags.NoGravity
			: down ? SimBodyFlags.Present | (SimInert ? SimBodyFlags.Inert : 0)
			: walk ? SimBodyFlags.Present | SimBodyFlags.Upright | SimBodyFlags.NoTurn | SimBodyFlags.NoGravity | (SimInert ? SimBodyFlags.Inert : 0)
			: SimBodyFlags.Present;
		var shape = walk ? SimShapeKind.FromSource : SimShapeKind.FromGameObject;

		// Anything about the registration that changed (who drives it, what it collides as) changes IN PLACE (SimWorld.Redrive):
		// the same body, its motion carrying on everywhere — a throw keeps flying, a stand-up keeps its speed — nothing is
		// re-created or restored from an older state.
		if ( existing is not null )
		{
			if ( !held && !down && !walk && !loose )
			{
				sim.Unregister( GameObject.Id );
				return;
			}
			bool same = existing.Spec.Driver == driver && existing.Spec.Owner == drvOwner && (existing.Spec.Flags & ~SimBodyFlags.StartAsleep) == flags
				&& existing.Spec.Shape == shape && (!walk || IsUpright( existing.Phys.Rotation )); // a tilted walker is re-made upright
			if ( same )
				return;
			if ( existing.Spec.Driver == SimHoldDriver.Id && !held )
			{
				// Let go of: from the holder's RELEASE TICK — the first of its inputs with no target (HoldReleased), which the
				// holder's own prediction switched at too — not from the drop message, which lands a lead earlier while its
				// last targets are still on the way (a 71u correction on every throw). Capped: lost inputs, a vanished holder.
				bool waitForRelease = existing.Ctrl.Holding && !existing.HoldReleased;
				if ( waitForRelease && _sinceSimHoldRowGone < 1f )
					return;
			}
			sim.Redrive( GameObject.Id, driver, drvOwner, flags, shape );
			return;
		}

		if ( held || down )
		{
			// Its real clay collider, free to turn: held (no gravity — the hold carries it; the holder's input drives it) or
			// down (gravity on; the wearer's input moves it, overturned, and rights it — SimStepDown).
			sim.Register( new SimBodySpec
			{
				Key = GameObject.Id, Source = GameObject.Id, Shape = SimShapeKind.FromGameObject, Driver = driver, Owner = drvOwner,
				Flags = flags, Mass = _collider.IsValid() ? _collider.ClayMass : 0f, Pose = new Transform( WorldPosition, WorldRotation ),
			} );
		}
		else if ( walk )
		{
			// UPRIGHT — yaw only, the root SettleUpright leaves (it folds any tilt into the disguise, which the hull is built
			// from). Registered at a tilted root (a loose prop lying on its side, possessed), the walker kept that tilt for
			// good (pitch/roll are locked) while the owner's SettleUpright ALSO counter-rotated the disguise: the clay turned
			// twice, the move hull lay on its side — the prop sank into the floor and couldn't jump (user report; measured
			// root roll -148° and disguise roll -148°).
			sim.Register( new SimBodySpec
			{
				Key = GameObject.Id, Source = GameObject.Id, Shape = SimShapeKind.FromSource, Driver = SimWalkDriverId, Owner = owner,
				Flags = SimBodyFlags.Present | SimBodyFlags.Upright | SimBodyFlags.NoTurn | SimBodyFlags.NoGravity
					| (SimInert ? SimBodyFlags.Inert : 0),
				Mass = _collider.IsValid() ? _collider.ClayMass : 0f,
				Pose = new Transform( WorldPosition, Rotation.FromYaw( WorldRotation.Angles().yaw ) ),
			} );
		}
		else if ( loose )
		{
			// A loose prop (dropped from a grab): a plain rigid body in the sim — its real clay collider, free to tumble,
			// engine gravity — carrying on with whatever motion the scene body had.
			var rb = Body;
			bool asleep = !rb.IsValid() || rb.Sleeping;
			sim.Register( new SimBodySpec
			{
				Key = GameObject.Id, Source = GameObject.Id, Shape = SimShapeKind.FromGameObject, Owner = Guid.Empty,
				Flags = SimBodyFlags.Present | (asleep ? SimBodyFlags.StartAsleep : 0),
				Mass = _collider.IsValid() ? _collider.ClayMass : 0f, Pose = new Transform( WorldPosition, WorldRotation ),
			} );
			if ( !asleep && rb.MotionEnabled )
				sim.SetMotion( GameObject.Id, rb.Velocity, rb.AngularVelocity );
		}
	}

	// Host: a player's or bot's body let go of lies DOWN (SimDownDriver — its wearer's own input rolls it and rights it with
	// Space) until the driver reports it upright (see UpdateSimRegistration); then it's a walker again.
	bool _simDown;

	/// <summary>Is this pawn lying down (or righting itself) in the sim world right now (any machine)?</summary>
	internal bool SimDown => SimSystem is { Active: true } sim && sim.Find( GameObject.Id ) is { } b && b.Spec.Driver == SimDownDriver.Id;

	/// <summary>The launch speed of this prop's jump (HiderController.Walk): the flip's hop (SimDownDriver).</summary>
	internal float SimHopSpeed => JumpPower * MathF.Sqrt( SizeSpeedScale );

	/// <summary>Is this pawn carried BY THE SIM when grabbed (the hold driver, SimHoldDriver) rather than lent to its holder?
	/// Every pawn while the sim walk is on — released scenery, players' bodies, bots — except one whose shape is mid-edit on
	/// its wearer's machine (the lend never took those either).</summary>
	internal static bool SimHoldApplies( HiderController h )
		=> SimWalk && h.IsValid() && h.Network.Active && !h.NetEditing;

	RealTimeSince _sinceSimHoldRowGone = 999f;

	/// <summary>Is this pawn in a hunter's hands in the sim world right now (any machine)?</summary>
	internal bool SimHeld => SimSystem is { Active: true } sim && sim.Find( GameObject.Id ) is { } b && b.Spec.Driver == SimHoldDriver.Id;

	// Every machine, every tick: while this pawn is held in the sim, its holder's hunter stays out of the sim's followers,
	// so the held body never collides with the hand carrying it (the scene filters the two — PropClaims.FilterAgainst).
	GameObject _simHoldExcluded;

	void UpdateSimHoldFollower( SimWorld sim )
	{
		GameObject hunter = null;
		if ( SimHeld && PropClaims.HolderOf( this ) is { } holder )
			hunter = Scene.GetAllComponents<HunterController>().FirstOrDefault( x => x.Network.Owner?.Id == holder )?.GameObject;
		if ( hunter == _simHoldExcluded )
			return;
		if ( _simHoldExcluded.IsValid() )
			sim.ExcludeFollower( _simHoldExcluded, false );
		_simHoldExcluded = hunter;
		if ( hunter.IsValid() )
			sim.ExcludeFollower( hunter, true );
	}

	// Every machine, the moment this pawn's WALKER body appears in the sim (SimWorld.BodyAdded — before anything presents
	// it): fold the root's tilt into the disguise, as SettleUpright does, so the clay stays exactly where it is while the
	// root takes the walker's upright pose. Doing it here, on every machine at the same point, keeps the move hull (built
	// from the disguise's local transform) the same everywhere from the first tick. Left to the owner's SettleUpright and
	// its broadcast, the host and other clients built the hull from the un-folded disguise for a while: a tilted prop's clay
	// "stood up" inside the sim, overlapping the floor and its neighbours, which were shoved out hard.
	void OnSimBodyAdded( SimBody b )
	{
		if ( b.Key != GameObject.Id || b.Spec.Driver != SimWalkDriverId || !_body.IsValid() )
			return;
		var disguise = _body.GameObject;
		var keep = disguise.WorldTransform;
		var pose = b.Spec.Pose;
		if ( WorldRotation.Distance( pose.Rotation ) < 0.01f )
			return;
		WorldTransform = new Transform( WorldPosition, pose.Rotation, WorldScale );
		disguise.WorldTransform = keep;
		Transform.ClearInterpolation();
		disguise.Transform.ClearInterpolation();
	}

	static bool IsUpright( Rotation r )
	{
		var a = r.Angles();
		return MathF.Abs( a.pitch ) < 1f && MathF.Abs( a.roll ) < 1f;
	}

	// Released scenery that's LOOSE (dropped from a grab, a rigid body) — the sim's own rigid body (see above). Not
	// frozen (it never moves: a static obstacle, which its scene body already is) or being sculpted in place.
	bool SimLooseEligible()
		=> Network.Active && _body.IsValid() && _collider.IsValid()
			&& PropClaims.IsReleased( this ) && PropClaims.IsLoose( this ) && !PropClaims.IsFrozen( this ) && !BeingEdited
			&& !PropClaims.IsLent( GameObject ) && !PropClaims.IsHeldObject( GameObject );

	/// <summary>Is this pawn a plain rigid body in the sim world right now (loose scenery)?</summary>
	internal bool SimLoose => SimSystem is { Active: true } sim && sim.Find( GameObject.Id ) is { } b && b.Spec.Driver == 0;

	// A player's or bot's body, on the network, nobody else's hands on it. Bots count even though they read as Scenery
	// (dormant — no controller): they walk with no input and park, and leaving them as scene bodies made them FOLLOWERS —
	// moved on the host the moment a sim prop walked into them, seen late on clients, so the two disagreed about the
	// obstacle (measured: 39u corrections bumping a bot in the kitchen).
	//
	// Only REPLICATED state: the host decides for everyone's pawns, and `Scenery` reads the local _dormant flag, which only
	// the OWNER's machine clears when it takes control — so the host's copy of a prop a client re-possessed in creative
	// still read dormant, and never qualified. Released (synced) is what says "nobody's body"; an owner says whose.
	//
	// RELEASED scenery too, while it's in walking mode (it walks with no input and parks, like a bot) — not loose (a
	// tumbling rigid body), frozen or being sculpted in place: those are other physics. Left out, a released prop was a
	// FOLLOWER, and walking into one threw the walker around (user: "walking into other props ejects me"; measured 84
	// corrections in 5 s, up to 47u).
	bool SimWalkEligible() => SimWalkBlocker() is null;

	// Is this released scenery in its walking mode (see SimWalkEligible)?
	bool ReleasedWalking => PropClaims.IsReleased( this ) && !PropClaims.IsLoose( this ) && !PropClaims.IsFrozen( this ) && !BeingEdited;

	// Nobody's input reaches it: a bot, or released scenery.
	bool SimInert => RoundManager.IsBotPawn( GameObject ) || PropClaims.IsReleased( this );

	// Why this pawn isn't sim-walking (null = it qualifies) — for mimi_sim_status, and the rule itself.
	string SimWalkBlocker()
	{
		if ( !Network.Active ) return "not networked yet (prop hunt keeps hiding props off the network until the Hunt)";
		if ( !_body.IsValid() || !_collider.IsValid() ) return "no disguise collider yet";
		if ( PropClaims.IsReleased( this ) && !ReleasedWalking ) return "released scenery that's loose, frozen or being sculpted (other physics)";
		if ( !PropClaims.IsReleased( this ) && Network.Owner is null && !RoundManager.IsBotPawn( GameObject ) ) return "nobody owns it";
		if ( PropClaims.IsLent( GameObject ) ) return "lent to a holder for a grab";
		if ( PropClaims.IsHeldObject( GameObject ) ) return "being held";
		return null;
	}

	/// <summary>Is the sim walk on, and which props walk there (and why not)? Run it on the HOST — it decides.</summary>
	[ConCmd( "mimi_sim_status" )]
	public static void SimStatusCmd()
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
			return;
		var sim = scene.GetSystem<SimWorld>();
		var sb = new System.Text.StringBuilder();
		bool host = !Networking.IsActive || Networking.IsHost;
		sb.AppendLine( $"mimi_sim_walk {(SimWalk ? "ON" : "off")} on this machine ({(host ? "HOST — this one decides" : "client — only the host's setting matters")}); sim world {(sim is { Active: true } ? $"active, {sim.Bodies.Count} bodies" : "not started")}" );
		foreach ( var h in scene.GetAllComponents<HiderController>() )
		{
			var who = h.Network.Owner?.DisplayName ?? "nobody";
			string state = h.SimWalking ? "SIM-WALKING (climbing unavailable)" : h.SimLoose ? "SIM RIGID BODY (loose)" : $"old walk — {h.SimWalkBlocker() ?? (SimWalk || !host ? "should qualify — not registered yet?" : "mimi_sim_walk is off")}";
			sb.AppendLine( $"  {h.GameObject.Name} [{who}{(h.IsProxy ? "" : ", simulated here")}]: {state}" );
		}
		Log.Info( "\n" + sb );
	}

	// ── Input (the owner) ───────────────────────────────────────────────────────────────────────────────────

	// Owner, every frame: point the sim's input at this pawn while it sim-walks.
	void UpdateSimControl()
	{
		var sim = SimSystem;
		if ( sim is null )
			return;
		UpdateSimHoldFollower( sim );
		bool mine = !IsProxy && !RoundManager.IsBotPawn( GameObject ) && !Scenery && (SimWalking || SimDown); // walking, or lying down (rolling / the flip)
		if ( mine )
		{
			sim.LocalInput = ReadSimInput;
			sim.LocalControlled = GameObject.Id;
		}
		else if ( sim.LocalControlled == GameObject.Id )
		{
			sim.LocalInput = null;
			sim.LocalControlled = Guid.Empty;
		}
	}

	// One sim tick's input. Called once per predicted tick (possibly twice in a frame when the clock catches up — the
	// turn is a delta since the last call, and a queued jump is taken once).
	SimInput ReadSimInput( int tick )
	{
		if ( !ControlActive )
		{
			_jumpQueued = false;
			ReadTurn(); // keep the turn baseline current, so control coming back doesn't turn by everything missed
			_turnSampled = false;
			return SimInput.None;
		}
		IsCrouching = Input.Down( "duck" );
		var wish = ApplyLeash( ReadWish(), pullIn: false );
		byte buttons = 0;
		if ( _jumpQueued )
			buttons |= SimInput.Jump;
		if ( IsCrouching )
			buttons |= SimInput.Duck;
		_jumpQueued = false;
		return new SimInput { Wish = new Vector2( wish.x, wish.y ), Buttons = buttons, Turn = ReadSimTurn() };
	}

	[ConVar( "mimi_sim_feet_lever", ConVarFlags.Replicated, Help = "Sim walk: where our weight/feet push lands on a prop we stand on: 1 = under the foot (tips it), 0 = its centre of mass." )]
	public static float SimFeetLever { get; set; } = 1f;

	[ConVar( "mimi_sim_feet_push", ConVarFlags.Replicated, Help = "Sim walk: scale on the sideways push our feet give a prop we stand on (1 = scene walk)." )]
	public static float SimFeetPush { get; set; } = 1f;

	// The facing, unwrapped, at the last two frames that ran a tick; and how much of it the ticks have taken so far.
	bool _turnSampled;
	double _turnTimeA, _turnTimeB;
	float _turnYawA, _turnYawB, _turnTaken;

	// This tick's turn: the facing AT the tick's time (SimWorld.TickTime), interpolated between frame samples, minus what
	// earlier ticks took. A steady mouse gives a steady turn per tick — reading the frame's whole delta instead gave
	// 1.5°, 3°, 1.5°… (two frames' worth landing in one tick, 50 Hz against 60 fps), a visible stutter in every turn.
	float ReadSimTurn()
	{
		double now = Time.NowDouble;
		float delta = ReadTurn(); // facing turned since the last call (0 for a second tick in the same frame)
		if ( !_turnSampled )
		{
			_turnSampled = true;
			_turnTimeA = _turnTimeB = now;
			_turnYawA = _turnYawB = _turnTaken = 0f;
			return 0f;
		}
		if ( now != _turnTimeB )
		{
			_turnTimeA = _turnTimeB; _turnYawA = _turnYawB;
			_turnTimeB = now; _turnYawB += delta;
		}
		else
			_turnYawB += delta;

		double t = SimSystem?.TickTime ?? now;
		float f = _turnTimeB > _turnTimeA ? (float)Math.Clamp( (t - _turnTimeA) / (_turnTimeB - _turnTimeA), 0.0, 1.0 ) : 1f;
		float yaw = MathX.Lerp( _turnYawA, _turnYawB, f );
		float turn = yaw - _turnTaken;
		_turnTaken = yaw;
		// Keep the numbers small (they only matter relative to each other).
		if ( MathF.Abs( _turnTaken ) > 3600f )
		{
			_turnYawA -= _turnTaken; _turnYawB -= _turnTaken; _turnTaken = 0f;
		}
		return turn;
	}

	// ── The walk, in the sim (every machine: host ticks, client predictions, replays) ───────────────────────

	sealed class SimWalkDriver : ISimDriver
	{
		public void Drive( SimBody body, in SimInput input, int tick )
		{
			if ( body.User is not HiderController h || !h.IsValid() )
			{
				var go = Game.ActiveScene?.Directory.FindByGuid( body.Spec.Source );
				h = go.IsValid() ? go.Components.Get<HiderController>() : null;
				body.User = h;
			}
			if ( h.IsValid() )
				h.SimStep( body, input );
		}

		// Someone else's prop: assume they keep going the way they're going (measured better than their last input).
		public SimInput Guess( SimBody body, in SimBodyState latest ) => new() { Wish = new Vector2( latest.V.x, latest.V.y ) };
	}

	/// <summary>The walk driver the sim resolves by id (SimWorld.DriverFor) — current code, whatever was registered before.</summary>
	internal static readonly ISimDriver WalkDriver = new SimWalkDriver();

	internal static void RegisterSimDriver( SimWorld sim )
	{
		sim.RegisterDriver( SimWalkDriverId, new SimWalkDriver() );
		sim.RegisterDriver( SimHoldDriver.Id, new SimHoldDriver() ); // the grab's hold (phase 3) — every machine runs it
		sim.RegisterDriver( SimDownDriver.Id, new SimDownDriver() ); // lying down: the walk overturned, righted by Space
	}

	// StepWalk, from (input, body.Ctrl) only — every number it keeps between ticks is in the SimCtrl, so a replay of the
	// same inputs from the same snapshot comes out the same. See HiderController.Walk for why each piece is there.
	void SimStep( SimBody body, in SimInput input )
	{
		var sim = SimSystem;
		var pb = body.Phys;
		if ( sim is null || pb is null || !pb.IsValid() || !_collider.IsValid() )
			return;
		_simHull.Refresh( _collider, sim.World );
		RefreshSceneHull();
		if ( !_simHull.Ready || !_hull.Ready )
			return;

		ref var c = ref body.Ctrl;
		const float dt = SimWorld.Dt;
		if ( c.SinceJumpTicks < SimCtrl.Long ) c.SinceJumpTicks++;
		// (PushLevel decays inside the ground branch; in the air nothing pushes back.)

		var pos = pb.Position;
		var rot = pb.Rotation;

		// Fell out of the world: back to the spawn spot, upright, at rest (as OnFixedUpdate does).
		if ( FallRespawn.Fell( pos.z, _fallAnchor.Position.z ) )
		{
			pb.Transform = new Transform( _fallAnchor.Position, Rotation.FromYaw( rot.Angles().yaw ) );
			pb.Velocity = 0f;
			c = SimCtrl.Initial;
			body.Intent = 0f;
			return;
		}

		float clearance = ScaledStep( StepHeight );
		var wish = new Vector3( input.Wish.x, input.Wish.y, 0f );
		bool jump = input.Has( SimInput.Jump );
		float turn = input.Turn;

		bool idle = wish.IsNearZeroLength && !jump && MathF.Abs( turn ) < 0.001f;
		if ( c.Parked )
		{
			if ( idle && pb.Velocity.LengthSquared < 4f )
			{
				body.Intent = 0f;
				return; // left to physics and its sleep
			}
			c.Parked = false;
			c.StillTicks = 0;
		}
		if ( pb.Sleeping )
			pb.Sleeping = false;

		var v = pb.Velocity;
		var trace = sim.Trace.WithoutTags( MoveIgnoreTags.Append( SimWorld.SelfTag( body ) ).ToArray() );

		// Ground: the hull's foot swept down from where the floating shape starts (HiderController.Walk).
		var from = pos + Vector3.Up * clearance;
		var tr = _simHull.SweepFoot( trace, new Transform( from, rot ), new Transform( from - Vector3.Up * (clearance + RideSnap), rot ), clearance );
		if ( tr.StartedSolid )
		{
			var centre = pos + rot * _hull.Centre;
			var rayFrom = centre.WithZ( pos.z + _hull.LocalBounds.Mins.z + clearance );
			var ray = trace.Ray( rayFrom, rayFrom - Vector3.Up * (clearance + RideSnap) ).Run();
			if ( !ray.StartedSolid )
				tr = ray;
		}
		var hitGo = tr.Hit ? sim.SourceOf( tr.Body ) : null;
		bool walkable = tr.Hit && (Walkable( tr.Normal ) || (tr.Normal.z > PropFloorMinZ && IsFloorProp( true, hitGo, tr.Body )));
		float gap = tr.Hit ? tr.Distance - clearance : float.MaxValue;
		bool pastJumpGrace = c.SinceJumpTicks * dt > WalkJumpGrace;
		bool ground = tr.Hit && !tr.StartedSolid && walkable && pastJumpGrace
			&& (c.Grounded ? gap <= RideSnap : gap <= 1f && v.z <= 50f);
		if ( tr.StartedSolid && c.Grounded && pastJumpGrace )
		{
			ground = true;
			gap = 0f;
		}

		var gb = ground && !tr.StartedSolid && tr.Body is { } b && b.IsValid() && b.BodyType != PhysicsBodyType.Static ? b : null;
		if ( gb is not null && body.IsLocalOwned )
			sim.NoteLocalTouch( sim.BodyOf( gb ) ); // standing on a loose prop: ours to predict (SimWorld.ShownFromAuthority)
		float myMass = MathF.Max( pb.Mass, 0.01f );
		float groundMassRatio = gb is null ? 0f : gb.BodyType == PhysicsBodyType.Dynamic ? gb.Mass / myMass : 1f;
		Vector3 groundVel = gb is not null && groundMassRatio >= 0.5f ? gb.GetVelocityAtPoint( tr.HitPosition ) : Vector3.Zero;

		if ( ground )
		{
			c.AirFromJump = false;
			c.Grounded = true;

			var flat = v.WithZ( 0f );
			var target = wish + groundVel.WithZ( 0f );
			var dv = (target - flat) * (1f - MathF.Exp( -GroundControl * dt ));
			// Pushing against something: how much of last tick's command the contacts took off us, as a LEVEL that rises at
			// once and falls over PushRelease — and the acceleration cap blends from WalkAccel to WalkPushAccel with it.
			// The scene walk switches between the two (took > 20% off → push accel for 0.25 s), and an on/off switch is poison
			// to prediction: a hair's difference in a contact between the host and a client replay flipped it, one side
			// accelerating 6× harder than the other (measured: predicted 280 u/s vs host 100 while pushing a loose prop).
			var cmd = c.Commanded.WithZ( 0f );
			float taken = cmd.Length > 5f ? Math.Clamp( Vector3.Dot( cmd - flat, cmd.Normal ) / cmd.Length, 0f, 1f ) : 0f;
			float raw = Math.Clamp( (taken - PushOnset) / (PushFull - PushOnset), 0f, 1f );
			float level = MathF.Max( raw, c.PushLevel / (float)SimCtrl.PushScale - dt / PushRelease );
			c.PushLevel = (ushort)MathF.Round( Math.Clamp( level, 0f, 1f ) * SimCtrl.PushScale );
			level = c.PushLevel / (float)SimCtrl.PushScale; // use exactly what's stored (and sent): replays agree to the bit
			dv = dv.ClampLength( MathX.Lerp( WalkAccel, WalkPushAccel, level ) * dt );
			flat += dv;

			float vz = groundVel.z + Math.Clamp( -gap * RideSpring, -RideMaxLift, RideMaxLift );
			if ( jump )
			{
				vz = groundVel.z + JumpPower * MathF.Sqrt( SizeSpeedScale );
				c.Grounded = false;
				c.SinceJumpTicks = 0;
				c.AirFromJump = true;
			}

			if ( gb is not null && gb.BodyType == PhysicsBodyType.Dynamic )
			{
				float weight = MathF.Min( myMass, gb.Mass * 4f ) * Gravity * dt;
				var impulse = -Vector3.Up * weight - dv.WithZ( 0f ) * myMass * MathF.Min( groundMassRatio, 1f ) * SimFeetPush;
				// Where it lands: SimFeetLever 1 = under our foot (the scene walk), 0 = through the prop's centre of mass.
				var com = gb.MassCenter;
				var at = Vector3.Lerp( com, tr.HitPosition, SimFeetLever );
				gb.ApplyImpulseAt( at, impulse );
			}

			v = flat.WithZ( vz );
		}
		else
		{
			c.Grounded = false;
			c.PushLevel = (ushort)Math.Max( 0, c.PushLevel - (int)(SimCtrl.PushScale * dt / PushRelease) ); // nothing pushes back in the air
			float t = 1f - MathF.Exp( -AirControl * dt );
			var flat = Vector3.Lerp( v.WithZ( 0f ), wish, t );
			float g = Gravity * (v.z < 0f ? FallGravityMult : 1f);
			v = flat.WithZ( MathF.Max( v.z - g * dt, -MaxFallSpeed ) );
		}

		pb.Velocity = v;
		pb.AngularVelocity = 0f;
		c.Commanded = v;
		body.Intent = wish.IsNearZeroLength ? Vector3.Zero : v.WithZ( 0f ).Length > wish.Length * 0.5f ? v.WithZ( 0f ) : wish;

		// Park once there's nothing left to do.
		bool still = c.Grounded && idle && (v - groundVel).Length < 1f && MathF.Abs( gap ) < 0.5f && groundVel.Length < 0.5f;
		c.StillTicks = still ? (byte)Math.Min( c.StillTicks + 1, 255 ) : (byte)0;
		if ( c.StillTicks >= ParkAfterTicks )
		{
			c.Parked = true;
			pb.Velocity = Vector3.Zero;
		}

		// The mouse turn: about the world vertical through the hull's centre, applied to the body directly (its yaw is
		// locked, so contacts can't turn it). Unlike the scene walk's Turn, not pose-tested against the world: the solver
		// pushes it back out of anything the turn swings it into. (2a: the pose test needs a sim overlap query.)
		if ( MathF.Abs( turn ) > 0.001f )
		{
			var centre = pos + rot * _hull.Centre;
			var centreXY = new Vector3( centre.x, centre.y, pos.z );
			var spin = Rotation.FromAxis( Vector3.Up, turn );
			pb.Transform = new Transform( centreXY + spin * (pos - centreXY), spin * rot );
		}
	}

	// ── Lying DOWN (SimDownDriver): the walk, overturned ────────────────────────────────────────────────────
	// Let go of by a hunter it TUMBLES (the physics, the move keys adding to its velocity) until it is calm a moment; SETTLED, it's the walk in
	// whatever pose it came to rest in — the move keys slide it at the walk's speed and acceleration, the mouse turns it
	// about the vertical, its rotation held (locked, its spin zeroed, as the walker's is) — until SPACE: the hop and a
	// righting spin toward upright, the host making it a walker once SimDownDriver.Done (the first jump IS the flip onto
	// its feet). Engine gravity throughout (the spec): it falls, lands and weighs on things as a body; what the walk does
	// with its ride spring and hand gravity, the contacts do here. No step-up (its real collider lies on the floor).
	// SimCtrl: Grounded = settled, PushLevel = ticks lain still (tumbling) or settled (a bot), AirFromJump = rising,
	// StillTicks = ticks off the ground / the park counter / the rise ticks, Parked and Commanded as the walk's.
	internal void SimStepDown( SimBody body, in SimInput input )
	{
		var sim = SimSystem;
		var pb = body.Phys;
		if ( sim is null || pb is null || !pb.IsValid() || !_collider.IsValid() )
			return;
		ref var c = ref body.Ctrl;
		const float dt = SimWorld.Dt;
		bool inert = (body.Spec.Flags & SimBodyFlags.Inert) != 0;
		var pos = pb.Position;
		var rot = pb.Rotation;
		var wish = new Vector3( input.Wish.x, input.Wish.y, 0f );

		// Fell out of the world: back to the spawn spot, upright, at rest (as the walk does) — it settles there.
		if ( FallRespawn.Fell( pos.z, _fallAnchor.Position.z ) )
		{
			SimDownDriver.Settle( body, pb, false );
			pb.Transform = new Transform( _fallAnchor.Position, Rotation.FromYaw( rot.Angles().yaw ) );
			pb.Velocity = 0f;
			pb.AngularVelocity = 0f;
			c = SimCtrl.Initial;
			body.Intent = 0f;
			return;
		}

		// Rising (the flip): the righting spin, the walk's air control on the way up.
		if ( c.AirFromJump )
		{
			SimDownDriver.Settle( body, pb, false );
			if ( pb.Sleeping )
				pb.Sleeping = false;
			if ( c.StillTicks < 255 )
				c.StillTicks++;
			var v = pb.Velocity;
			pb.Velocity = Vector3.Lerp( v.WithZ( 0f ), wish, 1f - MathF.Exp( -AirControl * dt ) ).WithZ( v.z );
			SimDownDriver.Right( pb );
			body.Intent = 0f;
			return;
		}

		// Tumbling: the physics has it, the move keys ADDED to it (user, 2026-11-05: "move before we settle, keeping the
		// velocity but adding our own inputs") — on the ground, the velocity along the keys is brought to the walk's speed
		// (up at the walk's acceleration; down, and across the keys, at what floor friction would take off a sliding body —
		// the shapes are FRICTIONLESS while the keys are held, so this stands in for it: without it a thrown ball slid at
		// the throw's speed for as long as a key was held, "extremely quickly while rolling"); in the air only the push,
		// never braking. The push adds SLIDE only: frictionless, the spin stays what the throw gave it (a ball keeps rolling
		// at its own pace and slides along under the keys; a kettle slides on whatever it's lying on), the floor's friction
		// coupling the two again as soon as the keys are let go. SPACE on the ground is the flip from here too, the tumble's
		// spin dropped (not inherited — the righting spin starts clean). Calm for a moment — no spin, not falling, not
		// faster than the walk — it's settled where it lies, moving or not (a spinning ball never is).
		if ( !c.Grounded )
		{
			bool driving = !wish.IsNearZeroLength;
			bool flip = !inert && input.Has( SimInput.Jump );
			SimDownDriver.Settle( body, pb, false, frictionless: driving );
			body.Intent = wish;
			bool onGround = (driving || flip) && OnGround();
			if ( flip && onGround )
			{
				c.AirFromJump = true;
				c.StillTicks = 0;
				SimDownDriver.Settle( body, pb, false );
				if ( pb.Sleeping )
					pb.Sleeping = false;
				pb.AngularVelocity = 0f;
				pb.Velocity = pb.Velocity.WithZ( MathF.Max( pb.Velocity.z, SimHopSpeed ) );
				body.Intent = 0f;
				return;
			}
			if ( driving )
			{
				if ( pb.Sleeping )
					pb.Sleeping = false;
				var v = pb.Velocity;
				var dir = wish.Normal;
				float along = Vector3.Dot( v, dir );
				if ( onGround )
				{
					float slide = SlideFriction * Gravity * dt; // what the floor's friction would take off the slide this tick
					var across = (v.WithZ( 0f ) - dir * along).ClampLength( MathF.Max( 0f, (v.WithZ( 0f ) - dir * along).Length - slide ) );
					along = along > wish.Length ? MathF.Max( wish.Length, along - slide ) : MathF.Min( wish.Length, along + WalkAccel * dt );
					pb.Velocity = (dir * along + across).WithZ( v.z );
				}
				else if ( along < wish.Length )
					pb.Velocity += dir * MathF.Min( WalkAccel * dt, wish.Length - along );
			}
			float walkMax = WalkSpeed * SizeSpeedScale + 10f;
			bool calm = pb.AngularVelocity.Length < 0.5f && MathF.Abs( pb.Velocity.z ) < 20f && pb.Velocity.WithZ( 0f ).Length <= walkMax;
			c.PushLevel = calm ? (ushort)Math.Min( c.PushLevel + 1, ushort.MaxValue ) : (ushort)0;
			if ( c.PushLevel < SimDownDriver.SettleTicks )
				return;
			c.Grounded = true;
			c.Parked = false;
			c.StillTicks = 0;
			c.Commanded = 0f;
		}

		// On the ground? The whole hull IN THIS POSE, swept down from a step above where it is (the walk's foot sweep is the
		// upright hull's base). Started solid (pressed into the floor, or against something) counts as on it.
		bool OnGround()
		{
			_simHull.Refresh( _collider, sim.World );
			RefreshSceneHull();
			if ( !_simHull.Ready || !_hull.Ready )
				return false;
			float clearance = ScaledStep( StepHeight );
			var trace = sim.Trace.WithoutTags( MoveIgnoreTags.Append( SimWorld.SelfTag( body ) ).ToArray() );
			var from = pb.Position + Vector3.Up * clearance;
			var tr = _simHull.Sweep( trace, new Transform( from, pb.Rotation ), new Transform( from - Vector3.Up * (clearance + RideSnap), pb.Rotation ) );
			bool walkable = tr.Hit && (Walkable( tr.Normal ) || (tr.Normal.z > PropFloorMinZ && IsFloorProp( true, sim.SourceOf( tr.Body ), tr.Body )));
			return tr.StartedSolid || (walkable && tr.Distance - clearance <= RideSnap);
		}

		// Settled.
		SimDownDriver.Settle( body, pb, true );
		if ( inert )
		{
			// A bot: lies there, then rights itself where it lies (no hop).
			pb.AngularVelocity = 0f;
			body.Intent = 0f;
			c.PushLevel = (ushort)Math.Min( c.PushLevel + 1, ushort.MaxValue );
			if ( c.PushLevel >= SimDownDriver.InertRestTicks )
			{
				c.AirFromJump = true;
				c.StillTicks = 0;
				SimDownDriver.Settle( body, pb, false );
				if ( pb.Sleeping )
					pb.Sleeping = false;
			}
			return;
		}

		bool jump = input.Has( SimInput.Jump );
		float turn = input.Turn;
		bool idle = wish.IsNearZeroLength && !jump && MathF.Abs( turn ) < 0.001f;
		if ( c.Parked )
		{
			if ( idle && pb.Velocity.LengthSquared < 4f )
			{
				body.Intent = 0f;
				return; // left to physics and its sleep
			}
			c.Parked = false;
			c.StillTicks = 0;
		}
		if ( pb.Sleeping )
			pb.Sleeping = false;

		bool ground = OnGround();

		var vel = pb.Velocity;
		if ( !ground )
		{
			// Off the ground (slid off an edge, a steep slope): the walk's air control, gravity's fall. Off it for a while,
			// it's a body again — to land and tumble, not stay balanced on whatever corner it left in.
			c.StillTicks = (byte)Math.Min( c.StillTicks + 1, 255 );
			if ( c.StillTicks >= SimDownDriver.UnsettleTicks )
			{
				c.Grounded = false;
				c.PushLevel = 0;
				SimDownDriver.Settle( body, pb, false );
			}
			pb.Velocity = Vector3.Lerp( vel.WithZ( 0f ), wish, 1f - MathF.Exp( -AirControl * dt ) ).WithZ( vel.z );
			pb.AngularVelocity = 0f;
			c.Commanded = pb.Velocity;
			body.Intent = wish;
			return;
		}

		// On the ground: the walk's chase of the wish (GroundControl, capped at WalkAccel); the vertical is the contacts'.
		var flat = vel.WithZ( 0f );
		var dv = (wish - flat) * (1f - MathF.Exp( -GroundControl * dt ));
		flat += dv.ClampLength( WalkAccel * dt );
		float vz = vel.z;
		if ( jump )
		{
			// Up we go: the flip is a jump (the prop's own hop); upright, the host makes it a walker.
			c.AirFromJump = true;
			c.StillTicks = 0;
			SimDownDriver.Settle( body, pb, false );
			vz = MathF.Max( vz, SimHopSpeed );
		}
		pb.Velocity = flat.WithZ( vz );
		pb.AngularVelocity = 0f;
		c.Commanded = pb.Velocity;
		body.Intent = wish.IsNearZeroLength ? Vector3.Zero : flat.Length > wish.Length * 0.5f ? flat : wish;
		if ( jump )
			return;

		// Park once there's nothing left to do.
		bool still = idle && flat.Length < 1f;
		c.StillTicks = still ? (byte)Math.Min( c.StillTicks + 1, 255 ) : (byte)0;
		if ( c.StillTicks >= ParkAfterTicks )
		{
			c.Parked = true;
			pb.Velocity = Vector3.Zero;
		}

		// The mouse turn: about the world vertical through the hull's centre, applied to the body directly (as the walk's).
		if ( MathF.Abs( turn ) > 0.001f )
		{
			var centre = pos + rot * _hull.Centre;
			var centreXY = new Vector3( centre.x, centre.y, pos.z );
			var spin = Rotation.FromAxis( Vector3.Up, turn );
			pb.Transform = new Transform( centreXY + spin * (pos - centreXY), spin * rot );
		}
	}

	// ── The scene pawn while it sim-walks ───────────────────────────────────────────────────────────────────

	// The simulating machine's fixed tick (owner / host): nothing to simulate here any more — the scene body is keyframed
	// presentation (SimWorld moves it). The rest of the game still reads IsGrounded / Velocity, so mirror them.
	bool StepSimWalking()
	{
		// Loose in the sim (a rigid body there), or in a hunter's hands there: just presentation here, like a sim walker. Our
		// OWN body as an object: the camera follows the clay, and a move or jump key breaks free / stands it up.
		if ( SimLoose || SimHeld || SimDown )
		{
			SetPhysicsMode( PhysicsMode.Character );
			if ( !Scenery && !IsProxy && !RoundManager.IsBotPawn( GameObject ) )
				WatchSimObjectEscape();
			return true;
		}

		var self = SimSelf;
		if ( self is null )
			return false;
		if ( _grabbedObject )
			RecoverFromSimObject(); // back on our feet (the host stood us up): the mouse turns us from where we face now
		if ( _climbing )
			EndClimb();
		SetPhysicsMode( PhysicsMode.Character );
		IsGrounded = self.Ctrl.Grounded;
		_parked = self.Ctrl.Parked;
		_velocity = self.Phys.IsValid() ? self.Phys.Velocity : Vector3.Zero;
		return true;
	}

	// ── Our own body carried or lying loose in the sim (the wearer's machine) ───────────────────────────────
	// The body stays ours on the network; only its sim body is driven by the holder (or by nobody). We watch it from the
	// host's poses (SimWorld.RemotePose), keep the camera on the clay (UpdateGrabbedCamera), and a jump or a fresh movement
	// key breaks free of a hold (PropClaims.EscapeGrab — the host drops us); lying down, our own sim input moves and rights us (SimDownDriver).
	RealTimeSince _sinceSimEscape = 999f;

	void WatchSimObjectEscape()
	{
		if ( !_grabbedObject )
		{
			// Just grabbed (or dropped): a key already DOWN doesn't count — only a press made after this breaks free. The
			// baseline WatchMoveEscape compares against is only kept while we're an object, so a prop grabbed mid-walk read
			// the key it was walking with as a fresh press on its first held tick and escaped at once (user: "the first grab
			// fails after moving").
			_grabbedObject = true;
			_moveWasDown = !Input.AnalogMove.IsNearZeroLength;
			_moveEscapeQueued = false;
			_jumpQueued = false;
			return;
		}
		if ( !SimHeld )
		{
			// Down: nothing to ask for — our own sim input moves it (the walk, overturned) and rights it (Space), see SimDownDriver.
			_escapeSent = false;
			return;
		}
		// Held: a move key or Space breaks free (the host drops us — we're DOWN from there). A Space press is left queued,
		// so the first input once we're down carries the jump: the flip onto our feet.
		WatchMoveEscape();
		bool jumped = _jumpQueued, moved = _moveEscapeQueued;
		_moveEscapeQueued = false;
		if ( !jumped )
			_jumpQueued = false;
		if ( _escapeSent || (!jumped && !moved) || _sinceSimEscape < 0.3f )
			return;
		_sinceSimEscape = 0f;
		_escapeSent = true;
		PropClaims.Current?.EscapeGrab( GameObject );
	}

	// Back to a walker after being an object: face where the body points now, and re-seed the mouse turn from it (the walk
	// turns by the facing's CHANGE since its last tick — frozen from before the grab, that read as one big spin).
	void RecoverFromSimObject()
	{
		_grabbedObject = false;
		_grabbedTarget = null;
		_bodyYaw = WorldRotation.Angles().yaw;
		_turnSeeded = false;
	}

	// ── Pushes from the scene side: a hunter walking into a pawn the sim owns ───────────────────────────────
	// While this pawn is in the sim its scene collider is keyframed presentation, and a hunter is only a keyframed FOLLOWER
	// there: pressing into the pawn, the hunter stops at the surface in both worlds and the sim never sees a push (user,
	// 2026-11-04: a dropped prop couldn't be pushed — the engine's player controller used to shove it as a loose
	// Rigidbody). So the hunter's machine reports its INTENT (its velocity against a solid is already 0), exactly as it
	// does for a prop-hunt decoy (RoundManager.NoteDecoyPush), and the host gives the sim body the push
	// (SimWorld.PushBody: wake it, PushTransfer of the push, capped) — a loose prop slides or rolls, a walker is shoved
	// and its walk damps it out, as the old push did.

	// Pusher side: last report per pawn, so a sustained contact sends ten a second, not one per tick.
	static readonly Dictionary<Guid, RealTimeSince> _simPushSent = new();
	const float SimPushMinSpeed = 20f;

	/// <summary>On the machine driving a hunter (its collision callback): was that a push into a prop pawn the sim owns?
	/// Reports it to the host if so. Standing on it or under it isn't a push.</summary>
	internal static void NoteSimPush( in Collision collision, Vector3 intent )
	{
		var other = collision.Other.GameObject;
		var h = other.IsValid() ? other.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ) : null;
		if ( !h.IsValid() || h.SimSystem is not { Active: true } sim || sim.Find( h.GameObject.Id ) is not { } b || !b.ShapesReady )
			return;
		if ( PropClaims.IsLent( h.GameObject ) || PropClaims.IsHeldObject( h.GameObject ) )
			return;
		var self = collision.Self.Body;
		var pb = h.Body.IsValid() ? h.Body.PhysicsBody : null;
		if ( !self.IsValid() || pb is null || !pb.IsValid() )
			return;

		// Bounds, not the contact: sustained-contact callbacks carry no contact point or normal (see NoteDecoyPush).
		var mine = self.GetBounds();
		var clay = pb.GetBounds();
		if ( mine.Mins.z >= clay.Maxs.z - 2f || mine.Maxs.z <= clay.Mins.z + 2f )
			return; // a floor or a ceiling to us

		// Only the part of the intent heading INTO it pushes it.
		var velocity = intent.WithZ( 0f );
		var toward = (clay.Center - mine.Center).WithZ( 0f );
		if ( velocity.Length < SimPushMinSpeed || (toward.LengthSquared > 0.01f && Vector3.Dot( velocity, toward.Normal ) < SimPushMinSpeed) )
			return;

		var id = h.GameObject.Id;
		if ( _simPushSent.TryGetValue( id, out var since ) && since < 0.1f )
			return;
		_simPushSent[id] = 0f;
		sim.PredictPush( id, velocity ); // on a client: moves it now, not a round trip later (no-op on the host)
		h.RequestSimPush( velocity );
	}

	/// <summary>Any machine → host: a hunter is pushing this pawn at this velocity (see <see cref="NoteSimPush"/>).</summary>
	[Rpc.Host]
	void RequestSimPush( Vector3 velocity )
	{
		if ( SimSystem is { Active: true } sim )
			sim.PushBody( GameObject.Id, velocity );
	}
}
