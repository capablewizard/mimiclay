using System;
using System.Linq;
using Mimiclay.Sim;
using Sandbox.Movement;

namespace Mimiclay;

// Hunters in the sim world, phase H1 (HUNTER-SIM-PLAN.md): the hunter WALKS in the shared simulation — host-authoritative,
// its owner predicting it from its own input and rolling back when a snapshot disagrees, every other machine drawing it from
// the sim — like a player's prop (HiderController.Sim). Behind mimi_sim_hunter (default OFF) until H5.
//
// The walk is the RETAIL engine PlayerController's (decompiled 2026-11-07; the public source is an older, different
// controller), ported to run from (input, SimCtrl) alone so the host and every replay produce the same motion:
//  • a dynamic body, all rotation locked, mass BodyMass: a feet box (BodyRadius square) under a capsule (radius BodyRadius/2),
//    neither with any friction — the velocity is the code's every tick;
//  • ground: Friction 6 (prefab), StopSpeed 100, GroundAcceleration 10, the velocity projected onto the ground's slope;
//    air: AirAcceleration 10 capped at 30, AirFriction; gravity BY HAND, half before the step and half after (the engine's
//    StartGravity / FinishGravity — and a jump tick's extra half, exactly as the engine does it);
//  • jump: JumpSpeed (replaced, not added, while ducked); no ground while rising faster than 140 u/s;
//  • step up (StepUpHeight) before the step: sweep along the move, lift, sweep again, find the ground, raise by the rise only
//    (the physics then carries the move); after the step, stick to the ground (StepDownHeight, Reground) and categorise it;
//  • duck: at once (the hunter never used the engine's timed duck — engine-timed-duck memory); mid-air it tucks the feet up
//    (the root rises by the height difference), standing needs the room.
// Left out for now (see the plan): the engine's static-move slide along static hulls, contact friction on dynamic ground,
// coyote time and the jump boost (both off on the hunter prefab), climbing (H3), teleports and jump pads (H4).
//
// The scene pawn becomes presentation (SimBodyFlags.Present — its transform set from the sim every frame; the host stops the
// engine syncing it). The stock controller stays for look, the eye, the ground it reads off the drawn pose and the colliders
// bullets hit, but moves nothing: HunterSimMode wins the move-mode vote and its input pass is kept off.
public sealed partial class HunterController : ISimShapeSource
{
	// Replicated: only the host's value decides (it registers the bodies); every machine's readout shows the real setting.
	[ConVar( "mimi_sim_hunter", Flags = ConVarFlags.Replicated, Help = "Hunters walk in the shared sim world (host-authoritative, owner-predicted); 0 = the engine's PlayerController walk. Host only." )]
	public static bool SimHunter { get; set; } = false;

	public const byte SimHunterDriverId = 5;

	/// <summary>The hunter walk the sim resolves by id (SimWorld.DriverFor).</summary>
	internal static readonly ISimDriver SimDriver = new SimHunterDriver();

	SimWorld HunterSim => Scene.GetSystem<SimWorld>();

	/// <summary>This hunter's body in the sim world, if it walks there (any machine — the host decides).</summary>
	public SimBody SimSelf => HunterSim is { Active: true } sim && sim.Find( GameObject.Id ) is { } b && b.Spec.Driver == SimHunterDriverId ? b : null;

	/// <summary>Does this hunter walk in the sim world right now?</summary>
	public bool SimWalking => SimSelf is not null;

	// ── Engine numbers (the prefab's PlayerController + MoveModeWalk; the walk mode's own defaults where the prefab has none) ──
	const float StopSpeed = 100f, GroundAccel = 10f, AirAccel = 10f, AirSpeedCap = 30f, MaxVelocity = 3500f;
	const float NonJumpVelocity = 140f; // rising faster than this (off the ground's plane) isn't on the ground
	const float Skin = 0.095f;          // the engine's skin: steps land this far above what they find
	// The feet box starts this far above the root (the engine lifts it ~1u while standing on static ground, so the box never
	// drags on the floor's seams). Fixed here — the body's shapes only change with the duck.
	const float FeetClearance = 1f;

	MoveModeWalk _walkMode;
	MoveModeWalk WalkMode => _walkMode.IsValid() ? _walkMode : (_walkMode = Components.Get<MoveModeWalk>());
	float WalkFriction => WalkMode.IsValid() ? WalkMode.Friction : 4f;
	float GroundAngle => WalkMode.IsValid() ? WalkMode.GroundAngle : 45f;
	float StepUp => WalkMode.IsValid() ? WalkMode.StepUpHeight : 18f;
	float StepDown => WalkMode.IsValid() ? WalkMode.StepDownHeight : 18f;

	float SimRadius => _controller.BodyRadius * 0.5f;
	float SimHeight( bool ducked ) => ducked ? _controller.DuckedHeight : _controller.BodyHeight;

	/// <summary>The engine's BodyBox: BodyRadius square, from the feet up to the (scaled) height.</summary>
	BBox SimBox( bool ducked, float scale = 1f, float heightScale = 1f )
	{
		float r = SimRadius * scale;
		return new BBox( new Vector3( -r, -r, 0f ), new Vector3( r, r, SimHeight( ducked ) * heightScale ) );
	}

	bool Standable( Vector3 normal ) => Vector3.GetAngle( Vector3.Up, normal ) <= GroundAngle;

	// ── Registration (host) ─────────────────────────────────────────────────────────────────────────────────

	// Host, every frame, for every hunter: in the sim while mimi_sim_hunter is on and it's on the network with an owner; out otherwise.
	void UpdateHunterSimRegistration()
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;
		var sim = HunterSim;
		if ( sim is null || !_controller.IsValid() )
			return;
		var existing = sim.Find( GameObject.Id );
		var owner = Network.Owner?.Id ?? Connection.Local?.Id ?? Guid.Empty;
		bool want = SimHunter && Network.Active && owner != Guid.Empty;
		if ( !want )
		{
			if ( existing is { Spec.Driver: SimHunterDriverId } )
				sim.Unregister( GameObject.Id );
			return;
		}
		var flags = SimBodyFlags.Present | SimBodyFlags.Upright | SimBodyFlags.NoTurn | SimBodyFlags.NoGravity | (Bot ? SimBodyFlags.Inert : 0);
		if ( existing is not null )
		{
			if ( existing.Spec.Driver == SimHunterDriverId && (existing.Spec.Owner != owner || existing.Spec.Flags != flags) )
				sim.Redrive( GameObject.Id, SimHunterDriverId, owner, flags );
			return;
		}
		sim.Register( new SimBodySpec
		{
			Key = GameObject.Id, Source = GameObject.Id, Shape = SimShapeKind.FromSource, Driver = SimHunterDriverId, Owner = owner,
			Flags = flags, Mass = _controller.BodyMass, Pose = new Transform( WorldPosition, Rotation.Identity ),
		} );
	}

	// ── Shapes (every machine) ──────────────────────────────────────────────────────────────────────────────

	// Which posture this machine's copy of the body is built for (the duck rebuilds it — ReshapeSim). Constant version: the
	// sim never rebuilds it on its own.
	bool _simShapeDucked;
	int ISimShapeSource.SimShapeVersion => 0;

	bool ISimShapeSource.BuildSimShapes( PhysicsBody body )
	{
		_simShapeDucked = false;
		return AddSimShapes( body, false );
	}

	// The engine's colliders for a posture (PlayerController.UpdateBody): the capsule over the upper half, its bottom tip at
	// least a unit up; the feet box (BodyRadius square) from the clearance to where the capsule starts.
	bool AddSimShapes( PhysicsBody body, bool ducked )
	{
		if ( !_controller.IsValid() )
			return false;
		float h = SimHeight( ducked );
		float half = h * 0.5f;
		float r = SimRadius;
		float capTop = h - r;
		float capBottom = MathF.Max( capTop - (half - r), r + 1f );
		bool capsule = capBottom < capTop;
		float feetTop = capsule ? half : h;
		float boxH = MathF.Max( feetTop - FeetClearance, 1f );
		var tags = GameObject.Tags.TryGetAll().ToArray();
		var box = body.AddBoxShape( new Vector3( 0f, 0f, FeetClearance + boxH * 0.5f ), Rotation.Identity, new Vector3( r, r, boxH * 0.5f ) );
		if ( box is null )
			return false;
		box.Friction = 0f;
		foreach ( var t in tags )
			box.Tags.Add( t );
		if ( capsule )
		{
			var cap = body.AddCapsuleShape( new Vector3( 0f, 0f, capBottom ), new Vector3( 0f, 0f, capTop ), r );
			if ( cap is not null )
			{
				cap.Friction = 0f;
				foreach ( var t in tags )
					cap.Tags.Add( t );
			}
		}
		return true;
	}

	// The body for a posture, rebuilt only when it changes (a duck, a stand, a replay crossing either).
	void ReshapeSim( SimBody b, bool ducked )
	{
		if ( _simShapeDucked == ducked || b.Phys is not { } p || !p.IsValid() )
			return;
		_simShapeDucked = ducked;
		p.ClearShapes();
		AddSimShapes( p, ducked );
		foreach ( var s in p.Shapes )
			s.Tags.Add( SimWorld.SelfTag( b ) );
		if ( b.Spec.Mass > 0f )
			p.Mass = b.Spec.Mass;
	}

	// ── Input (the owner) ───────────────────────────────────────────────────────────────────────────────────

	// Latched per frame (OnUpdate), taken by the next tick that reads it.
	bool _simJumpQueued;

	/// <summary>Test harness (SimLab, scripts): the move keys (x forward, y left, at the view's yaw), a Space press (taken by the
	/// next tick) and Ctrl held, in place of the real keys — on the machine whose hunter walks in the sim.</summary>
	public static Vector2? DebugSimMove { get; set; }
	public static bool DebugSimJump { get; set; }
	public static bool DebugSimDuck { get; set; }
	public static bool DebugSimTrace { get; set; }
	public static string SimDebugGround { get; set; }
	// What the keys may do this frame (the same gates the engine's input pass had), and a scripted wish replacing them (wander,
	// the benchmark route, the debug chase — they write the controller's WishVelocity).
	bool _simKeysAllowed;
	Vector3? _simWishOverride;
	bool _wasSimWalking;
	Func<int, SimInput> _simInputFn;

	// Owner, every frame: point the sim's input at this hunter while it walks there (or carries a prop the sim holds), and keep
	// the engine controller's own input pass off meanwhile.
	void UpdateHunterSimControl()
	{
		var sim = HunterSim;
		if ( sim is null )
			return;
		bool walking = SimWalking;
		// Out of the sim again: presentation made the controller's Rigidbody keyframed (SimWorld.PresentBodies) and nothing
		// turns it back — the engine walk then moved it by velocity THROUGH everything (a hunter walking through a football,
		// found 2026-11-08 switching mimi_sim_hunter off mid-session).
		if ( walking )
			_wasSimWalking = true;
		else if ( _wasSimWalking )
		{
			_wasSimWalking = false;
			if ( _controller.IsValid() && _controller.Body.IsValid() )
			{
				_controller.Body.MotionEnabled = true;
				_controller.Body.Velocity = Vector3.Zero;
			}
		}
		if ( walking && Owned && _controller.IsValid() )
		{
			if ( Input.Pressed( "jump" ) && _simKeysAllowed )
				_simJumpQueued = true;
		}
		else
			_simJumpQueued = false;
		HookSimInput( sim );
	}

	// The sim's local input is this hunter's while it walks there or carries a sim-held prop (SetSimHold); handed back otherwise.
	void HookSimInput( SimWorld sim )
	{
		if ( sim is null )
			return;
		_simInputFn ??= ReadHunterSimInput;
		bool walking = Owned && SimWalking;
		bool mine = walking || _simHolding;
		if ( mine )
		{
			sim.LocalInput = _simInputFn;
			if ( walking )
				sim.LocalControlled = GameObject.Id;
		}
		else if ( sim.LocalInput == _simInputFn )
			sim.LocalInput = null;
		if ( !walking && sim.LocalControlled == GameObject.Id )
			sim.LocalControlled = Guid.Empty;
	}

	// One sim tick's input: the walk's (move keys at the view's yaw, Space pressed, Ctrl held) and, while carrying a prop the
	// sim holds, the hold's target (SimHoldDriver). Called once per predicted tick.
	SimInput ReadHunterSimInput( int tick )
	{
		var input = new SimInput();
		if ( _simHolding )
		{
			input.Buttons |= SimInput.Hold;
			input.HoldPos = _simHoldTarget.Position;
			input.HoldRot = _simHoldTarget.Rotation;
		}
		if ( !SimWalking || !_controller.IsValid() )
			return input;
		input.ViewYaw = SimInput.QuantYaw( _controller.EyeAngles.yaw );
		if ( _simJumpQueued )
			input.Buttons |= SimInput.Jump;
		_simJumpQueued = false;
		if ( DebugSimJump )
		{
			input.Buttons |= SimInput.Jump;
			DebugSimJump = false;
		}
		if ( DebugSimDuck )
			input.Buttons |= SimInput.Duck;
		if ( DebugSimMove is { } dm )
		{
			input.Move = new Vector2( SimInput.QuantMove( dm.x ), SimInput.QuantMove( dm.y ) );
			return input;
		}
		if ( _simWishOverride is { } w )
		{
			input.Wish = new Vector2( w.x, w.y );
			return input;
		}
		if ( !_simKeysAllowed )
			return input;
		if ( Input.Down( "duck" ) )
			input.Buttons |= SimInput.Duck;
		var move = new Vector2( Input.AnalogMove.x, Input.AnalogMove.y );
		move = move.Length > 1f ? move.Normal : move;
		input.Move = new Vector2( SimInput.QuantMove( move.x ), SimInput.QuantMove( move.y ) );
		return input;
	}

	// ── The walk, in the sim (every machine: host ticks, client predictions, replays) ───────────────────────

	sealed class SimHunterDriver : ISimDriver, ISimPostStep
	{
		static HunterController Of( SimBody body )
		{
			if ( body.User is HunterController h && h.IsValid() )
				return h;
			var go = Game.ActiveScene?.Directory.FindByGuid( body.Spec.Source );
			h = go.IsValid() ? go.Components.Get<HunterController>() : null;
			body.User = h;
			return h.IsValid() ? h : null;
		}

		public void Drive( SimBody body, in SimInput input, int tick ) => Of( body )?.SimHunterStep( body, input, tick );
		public void PostStep( SimBody body, int tick ) => Of( body )?.SimHunterPostStep( body, tick );

		// Someone else's hunter: their last wish repeated (SimCtrl.Wish), crouched if they were.
		public SimInput Guess( SimBody body, in SimBodyState latest ) => new()
		{
			Wish = latest.HasCtrl ? latest.Ctrl.Wish : new Vector2( latest.V.x, latest.V.y ),
			Buttons = latest.HasCtrl && latest.Ctrl.Ducked ? SimInput.Duck : (byte)0,
		};
	}

	PhysicsTraceBuilder SimHunterTrace( SimWorld sim, SimBody body )
		=> sim.Trace.WithoutTags( SimWorld.SelfTag( body ) ).WithCollisionRules( GameObject.Tags.TryGetAll() );

	// The engine's ground probe (CategorizeGround / FindGroundSupport): the body's box at half height swept from `above` over the
	// feet to `below` under them, losing girth while it starts inside something or lands on something too steep.
	static PhysicsTraceResult GroundProbe( HunterController h, PhysicsTraceBuilder trace, Vector3 pos, bool ducked, float above, float below )
	{
		for ( float s = 1f; s > 0.65f; s -= 0.1f )
		{
			var tr = trace.Box( h.SimBox( ducked, s, 0.5f ), pos + Vector3.Up * above, pos - Vector3.Up * below ).Run();
			if ( tr.StartedSolid || (tr.Hit && !h.Standable( tr.Normal )) )
				continue;
			return tr;
		}
		return default;
	}

	// The ground's own velocity where we stand (a moving platform, a prop being carried), and its friction.
	static Vector3 GroundVelocityOf( in PhysicsTraceResult tr, Vector3 at )
		=> tr.Hit && tr.Body is { } gb && gb.IsValid() && gb.BodyType != PhysicsBodyType.Static ? gb.GetVelocityAtPoint( at ) : Vector3.Zero;

	static float GroundFrictionOf( in PhysicsTraceResult tr )
	{
		float f = tr.Shape is { } s && s.IsValid() ? s.Friction : -1f;
		return f >= 0f ? f : tr.Surface?.Friction ?? 1f;
	}

	static float SurfaceFriction( float friction ) => MathF.Min( friction * 1.25f, 1f );

	static Vector3 ApplyFriction( Vector3 v, float friction, float surface, float dt )
	{
		float len = v.Length;
		if ( len < 0.1f )
			return v;
		float drop = (len < StopSpeed ? StopSpeed : len) * friction * surface * dt;
		float to = MathF.Max( len - drop, 0f );
		return to != len ? v * (to / len) : v;
	}

	static Vector3 Accelerate( Vector3 v, Vector3 dir, float wishSpeed, float accel, float surface, float dt )
	{
		float add = wishSpeed - v.Dot( dir );
		if ( add <= 0f )
			return v;
		return v + dir * MathF.Min( accel * dt * wishSpeed * surface, add );
	}

	static Vector3 AirAccelerate( Vector3 v, Vector3 dir, float wishSpeed, float accel, float cap, float surface, float dt )
	{
		float add = MathF.Min( wishSpeed, cap ) - v.Dot( dir );
		if ( add <= 0f )
			return v;
		return v + dir * MathF.Min( accel * wishSpeed * dt * surface, add );
	}

	static Vector3 CheckVelocity( Vector3 v )
		=> new( float.IsNaN( v.x ) ? 0f : Math.Clamp( v.x, -MaxVelocity, MaxVelocity ), float.IsNaN( v.y ) ? 0f : Math.Clamp( v.y, -MaxVelocity, MaxVelocity ),
			float.IsNaN( v.z ) ? 0f : Math.Clamp( v.z, -MaxVelocity, MaxVelocity ) );

	float SimGravity( SimWorld sim ) => -sim.World.Gravity.z;

	// The tick's wish (u/s, horizontal): the move keys at the view's yaw and the posture's speed, or a wish sent as-is (a
	// scripted hunter, or a remote copy repeating the last one).
	Vector3 SimWish( in SimInput input, bool ducked )
	{
		if ( input.Move != Vector2.Zero )
		{
			float speed = ducked ? _controller.DuckedSpeed : _controller.RunSpeed;
			return Rotation.FromYaw( input.ViewYaw ) * new Vector3( input.Move.x, input.Move.y, 0f ) * speed;
		}
		var w = new Vector3( input.Wish.x, input.Wish.y, 0f );
		if ( ducked && w.Length > _controller.DuckedSpeed )
			w = w.Normal * _controller.DuckedSpeed;
		return w;
	}

	// Before the step (the engine's FixedUpdate input pass, BeginPhysicsStep, AddVelocity and the walk's PrePhysicsStep, in order).
	PhysicsBody _simCcdOff; // (the body CCD was last turned off on)

	// What this hunter carries never collides with it (the scene filters the two — PropClaims.FilterAgainst): a filter joint
	// between our sim body and each body the sim holds for our player, kept in step every tick — host ticks, predictions and
	// replays alike — so a replay across a grab or a drop filters exactly where the original did.
	readonly System.Collections.Generic.Dictionary<PhysicsBody, Sandbox.Physics.PhysicsJoint> _holdFilters = new();
	PhysicsBody _holdFiltersFor;

	void SyncHoldFilters( SimWorld sim, SimBody me )
	{
		if ( _holdFiltersFor != me.Phys )
		{
			foreach ( var j in _holdFilters.Values )
				j?.Remove();
			_holdFilters.Clear();
			_holdFiltersFor = me.Phys;
		}
		System.Collections.Generic.List<PhysicsBody> keep = null;
		foreach ( var b in sim.Bodies )
		{
			if ( b == me || b.Spec.Driver != SimHoldDriver.Id || b.Spec.Owner != me.Spec.Owner || b.Phys is not { } p || !p.IsValid() )
				continue;
			(keep ??= new()).Add( p );
			if ( !_holdFilters.ContainsKey( p ) )
				_holdFilters[p] = Sandbox.Physics.PhysicsJoint.CreateFilter( me.Phys, p );
		}
		if ( _holdFilters.Count == (keep?.Count ?? 0) )
			return;
		foreach ( var p in _holdFilters.Keys.ToList() )
		{
			if ( keep is not null && keep.Contains( p ) )
				continue;
			if ( p.IsValid() )
				_holdFilters[p]?.Remove();
			_holdFilters.Remove( p );
		}
	}

	void SimHunterStep( SimBody body, in SimInput input, int tick )
	{
		var sim = HunterSim;
		var pb = body.Phys;
		if ( sim is null || pb is null || !pb.IsValid() || !_controller.IsValid() )
			return;
		ref var c = ref body.Ctrl;
		const float dt = SimWorld.Dt;
		if ( c.SinceJumpTicks < SimCtrl.Long ) c.SinceJumpTicks++;
		ReshapeSim( body, c.Ducked ); // (a rollback restores the posture with the state — the shapes follow it here)
		if ( pb.Sleeping )
			pb.Sleeping = false;
		if ( _simCcdOff != pb )
		{
			pb.EnhancedCcd = false; // (the engine's body has it off — PlayerController.UpdateBody)
			_simCcdOff = pb;
		}

		var pos = pb.Position;
		var v = pb.Velocity;
		float g = SimGravity( sim );

		// Fell out of the world: back to the last safe spot, at rest.
		if ( FallRespawn.Fell( pos.z, _fallAnchor.Position.z ) )
		{
			pb.Transform = new Transform( _fallAnchor.Position, Rotation.Identity );
			pb.Velocity = 0f;
			c = SimCtrl.Initial;
			body.Intent = 0f;
			return;
		}

		var trace = SimHunterTrace( sim, body );
		SyncHoldFilters( sim, body );

		// Duck: at once. Crouching mid-air tucks the feet up (the root rises by the difference, the head holds still); standing
		// needs the room for the full body — mid-air it lowers the feet back down instead.
		bool wantDuck = input.Has( SimInput.Duck );
		if ( wantDuck != c.Ducked )
		{
			float delta = _controller.BodyHeight - _controller.DuckedHeight;
			if ( wantDuck )
			{
				c.Ducked = true;
				if ( !c.Grounded )
					pos += Vector3.Up * delta;
			}
			else
			{
				var to = c.Grounded ? pos : pos - Vector3.Up * delta;
				var box = SimBox( false );
				if ( c.Grounded )
					box = new BBox( box.Mins + Vector3.Up * 1f, box.Maxs ); // (standing: not the floor under the feet)
				var tr = trace.Box( box, pos, to + Vector3.Up * 0.01f ).Run();
				if ( !tr.StartedSolid && !tr.Hit )
				{
					c.Ducked = false;
					pos = to;
				}
			}
			ReshapeSim( body, c.Ducked );
		}

		// The ground under us, as the last tick's categorise left it (re-found for its slope, velocity and friction).
		var ground = c.Grounded ? GroundProbe( this, trace, pos, c.Ducked, 4f, 2f ) : default;
		bool grounded = c.Grounded && ground.Hit;
		var normal = grounded ? ground.Normal : Vector3.Up;
		var groundVel = grounded ? GroundVelocityOf( ground, pos ) : Vector3.Zero;
		float surface = grounded ? SurfaceFriction( GroundFrictionOf( ground ) ) : (v.z > 0f && v.z <= NonJumpVelocity ? 0.25f : 1f);
		// On a body that moves (a loose prop, someone's prop): the engine's contact-friction ground — real gravity, the contacts
		// holding us up, so the prop carries our weight (it tips a ball, sinks a stack) — instead of the hover over static ground.
		bool dynGround = grounded && ground.Body is { } dgb && dgb.BodyType == PhysicsBodyType.Dynamic;
		if ( DebugSimTrace && body.IsLocalOwned )
			SimDebugGround = $"t{tick} gr {grounded} hit {ground.Hit} body {(ground.Body is { } xb ? $"{xb.BodyType} {sim.BodyOf( xb )?.Key}" : "-")} gv {groundVel:0} v {v:0} pos {pos:0.0}";
		if ( dynGround && sim.BodyOf( ground.Body ) is { } under && under != body )
		{
			var gb = ground.Body;
			gb.Sleeping = false;
			gb.ApplyImpulseAt( ground.HitPosition, -Vector3.Up * MathF.Min( pb.Mass, gb.Mass * 4f ) * g * dt );
			sim.NotePushContact( body, under );
		}

		// BeginPhysicsStep: the first half of gravity off the ground (or on a jump), and the jump.
		bool jump = input.Has( SimInput.Jump ) && grounded && _controller.JumpSpeed > 0f;
		if ( !grounded || jump )
			v.z -= g * 0.5f * dt;
		else if ( dynGround )
			v.z -= g * dt; // (the whole tick's: the engine turns the body's own gravity on here)
		if ( jump )
		{
			float vz = c.Ducked ? _controller.JumpSpeed : v.z + _controller.JumpSpeed;
			v.z = vz - g * dt * 0.5f; // (the engine finishes the jump tick's gravity at once — and again after the step, airborne)
			grounded = false;
			c.Grounded = false;
			c.SinceJumpTicks = 0;
			normal = Vector3.Up;
		}
		v = CheckVelocity( v );

		// AddVelocity: friction + acceleration on the ground (in the ground's plane), air strafe off it.
		var wish = SimWish( input, c.Ducked );
		var drive = grounded ? AlongWalls( trace, pos, wish, c.Ducked ) : wish;
		float wishSpeed = drive.Length;
		var dir = wishSpeed > 0f ? drive / wishSpeed : Vector3.Zero;
		if ( !grounded )
			v = AirAccelerate( v, dir, wishSpeed, AirAccel, AirSpeedCap, surface, dt );
		else
		{
			var flat = (v - groundVel).WithZ( 0f );
			if ( dynGround && wishSpeed <= 0f )
				flat = Vector3.Zero; // (standing still on a moving body: GRIP — the engine's feet get its brakes friction, 1 + 100 × the
									 // ground's, against a body that moves, and ride it; ours have none, and the code's friction alone let a
									 // walking prop slide out from under a hunter standing on it — measured 2026-11-08)
			else
			{
				flat = ApplyFriction( flat, MathF.Max( WalkFriction * _controller.BrakePower, 0f ), surface, dt );
				flat = Accelerate( flat, dir, wishSpeed, GroundAccel, surface, dt );
			}
			if ( flat.Length < 1f )
				flat = Vector3.Zero;
			if ( dynGround )
				v = (flat + groundVel).WithZ( v.z ); // (on a moving body: the vertical is gravity and the contacts')
			else
			{
				if ( normal.z > 0f )
					flat -= Vector3.Up * (flat.Dot( normal ) / normal.z); // along the slope
				v = flat + groundVel;
			}
		}

		// The walk's PrePhysicsStep: air friction, then a step up onto what the move runs into.
		if ( !grounded )
			v /= 1f + MathF.Max( _controller.AirFriction, 0f ) * dt;
		else if ( StepUp > 0f )
			SimTryStep( trace, ref pos, ref v, groundVel, c.Ducked );

		// Walking into a prop pushes it — prop hunt's push (SimWorld.PushBody), now inside the sim: run the same on the host,
		// in our prediction and in every replay, so no report, no round trip.
		SimPushAhead( sim, body, trace, pos, ref v, wish, c.Ducked );

		pb.Position = pos;
		pb.Velocity = v;
		pb.AngularVelocity = 0f;
		c.Grounded = grounded;
		c.Wish = new Vector2( wish.x, wish.y ); // what a remote copy repeats (SimCtrl.Wish)
		c.Driving = wishSpeed > 0f;
		c.AirFromJump = c.AirFromJump && !grounded || jump;
		body.Intent = wishSpeed > 0f ? wish : Vector3.Zero; // (what a push into an anchored decoy reads — SimWorld.DetectAnchoredPushes)
	}

	// After the step (the walk's PostPhysicsStep, CategorizeGround and FinishPhysicsStep, in order).
	void SimHunterPostStep( SimBody body, int tick )
	{
		var sim = HunterSim;
		var pb = body.Phys;
		if ( sim is null || pb is null || !pb.IsValid() || !_controller.IsValid() )
			return;
		ref var c = ref body.Ctrl;
		const float dt = SimWorld.Dt;
		var trace = SimHunterTrace( sim, body );
		var pos = pb.Position;
		var v = pb.Velocity;
		bool wasGrounded = c.Grounded;
		bool jumped = c.SinceJumpTicks == 0;

		// Reground: on the ground before the step, back onto it after (down a step, or the slope we walked down) — not onto
		// dynamic ground (a loose prop: the contacts hold us there) and not after a jump.
		if ( wasGrounded && !jumped && StepDown > 0f )
		{
			var tr = GroundProbe( this, trace, pos, c.Ducked, 2f, StepDown );
			if ( tr.Hit && !tr.StartedSolid && tr.Body is { } gb && gb.BodyType != PhysicsBodyType.Dynamic )
			{
				var landing = tr.EndPosition;
				float rise = landing.z - pos.z;
				if ( rise <= 1.19f && rise >= -(StepDown + Skin) )
				{
					if ( MathF.Abs( rise ) > Skin )
						pos = landing;
					var gv = GroundVelocityOf( tr, pos );
					float into = (v - gv).Dot( tr.Normal );
					if ( into < 0f && tr.Normal.z > 0f )
						v -= Vector3.Up * (into / tr.Normal.z);
				}
			}
		}

		// Categorise: on the ground unless rising off it; the probe from 4 over the feet to 2 under them.
		var ground = default( PhysicsTraceResult );
		bool rising = v.z > NonJumpVelocity;
		if ( !rising || (wasGrounded && !jumped) )
			ground = GroundProbe( this, trace, pos, c.Ducked, 4f, 2f );
		if ( rising && ground.Hit && v.Dot( ground.Normal ) > NonJumpVelocity )
			ground = default;
		bool grounded = ground.Hit;
		var groundVel = grounded ? GroundVelocityOf( ground, pos ) : Vector3.Zero;

		// FinishPhysicsStep: the second half of gravity in the air; on the ground, the ground's own vertical speed.
		if ( !grounded )
			v.z -= SimGravity( sim ) * dt * 0.5f;
		else if ( ground.Body is not { BodyType: PhysicsBodyType.Dynamic } )
			v.z = groundVel.z; // (on a moving body the contacts have the vertical — see dynGround)
		v = CheckVelocity( v );

		pb.Position = pos;
		pb.Velocity = v;
		c.Grounded = grounded;
		c.Commanded = v;
	}

	// PUSHES (H2). What the hunter walks into gets prop hunt's push: PushTransfer of the hunter's WISH, capped (SimWorld.PushBody —
	// an anchored decoy is released by it too, so body-checking props tells a hunter nothing), and the hunter goes no faster
	// into it than that, so it walks behind what it pushes at the push's speed — as it did when props were obstacles on its
	// screen and the push a report to the host. Its velocity is set every tick, so left to the contacts it bulldozed anything,
	// however heavy (a 1878 kg prop at 276 u/s, measured 2026-11-07). The rules NoteSimPush used: touching (the body's box
	// swept a hair along the move), not something we stand on or are under, and only the part of the wish heading into it.
	// Not other hunters (the contacts shove two hunters apart) or a body in someone's hands (the hold drives it).
	const float SimPushMinSpeed = 20f;

	void SimPushAhead( SimWorld sim, SimBody me, PhysicsTraceBuilder trace, Vector3 pos, ref Vector3 v, Vector3 wish, bool ducked )
	{
		var flatWish = wish.WithZ( 0f );
		if ( flatWish.Length < SimPushMinSpeed )
			return;
		var n = flatWish.Normal;
		float reach = MathF.Max( v.WithZ( 0f ).Dot( n ), 0f ) * SimWorld.Dt + 2f;
		var box = SimBox( ducked );
		var mine = new BBox( pos + box.Mins, pos + box.Maxs );
		foreach ( var tr in trace.Box( box, pos, pos + n * reach ).RunAll() )
		{
			if ( !tr.Hit || tr.Body is not { } hit || sim.BodyOf( hit ) is not { } b || b == me )
				continue;
			if ( hit.BodyType != PhysicsBodyType.Dynamic && (b.Spec.Flags & SimBodyFlags.Anchored) == 0 )
				continue; // (moving bodies, and a waiting decoy — anchored, keyframed until pushed)
			if ( b.Spec.Driver == SimHunterDriverId || b.Spec.Driver == SimHoldDriver.Id || !b.ShapesReady )
				continue;
			var clay = hit.GetBounds();
			if ( mine.Mins.z >= clay.Maxs.z - 2f || mine.Maxs.z <= clay.Mins.z + 2f )
				continue; // a floor or a ceiling to us
			var toward = (clay.Center - mine.Center).WithZ( 0f );
			if ( toward.LengthSquared > 0.01f && flatWish.Dot( toward.Normal ) < SimPushMinSpeed )
				continue;
			sim.PushBody( b, flatWish );
			// No faster into it than it now goes: the hunter follows the push instead of driving through it.
			float into = v.Dot( n ), its = b.Phys.Velocity.Dot( n );
			if ( into > its )
				v -= n * (into - its);
			sim.NotePushContact( me, b ); // (drawn from the prediction, kept apart in the drawing — see it)
		}
	}

	// Pressed against a wall on the ground: the wish loses the part heading into it, so the hunter slides along at the wish's
	// share along the wall (the engine's speed: 198 u/s at 45° — measured 2026-11-07). The engine gets there by KEEPING the
	// velocity into the wall (its body reads 280 u/s into a wall it stands against, so its accelerate never pushes the slide
	// past the wish); here the contacts take that velocity off, and without this the accelerate kept adding along the wall
	// up to 328 u/s. Unlike the engine, nothing is left heading into the wall, so turning away from one is instant (the
	// engine had to brake 280 u/s of it first). Static walls only — a prop in the way is the contacts' business (pushing it).
	Vector3 AlongWalls( PhysicsTraceBuilder trace, Vector3 pos, Vector3 wish, bool ducked )
	{
		if ( wish.IsNearZeroLength )
			return wish;
		var box = SimBox( ducked );
		var lifted = pos + Vector3.Up * (StepUp + 1f); // (above anything the step-up takes in its stride: only real walls)
		box = new BBox( box.Mins, box.Maxs - Vector3.Up * (StepUp + 1f) );
		for ( int i = 0; i < 2 && !wish.IsNearZeroLength; i++ )
		{
			var tr = trace.Box( box, lifted, lifted + wish.Normal * 1.5f ).Run();
			if ( !tr.Hit || tr.StartedSolid || tr.Body is not { } wb || wb.BodyType != PhysicsBodyType.Static )
				break;
			var n = tr.Normal.WithZ( 0f );
			if ( n.Length < 0.7f )
				break; // a slope or a lip, not a wall
			n = n.Normal;
			float into = wish.Dot( n );
			if ( into >= 0f )
				break;
			wish -= n * into;
		}
		return wish;
	}

	// Lift onto a reachable step (the engine's TryStep, its main path): sweep the body along this tick's move; if something
	// unwalkable is in the way, sweep up a step, along again, then down onto what's there — and raise the body by that rise
	// only, so the physics still carries the move itself.
	void SimTryStep( PhysicsTraceBuilder trace, ref Vector3 pos, ref Vector3 v, Vector3 groundVel, bool ducked )
	{
		var move = (v - groundVel).WithZ( 0f ) * SimWorld.Dt;
		float len = move.Length;
		if ( len <= 0.0001f )
			return;
		var dir = move / len;
		var reach = move + dir * 0.5f;
		var box = SimBox( ducked );
		var ahead = trace.Box( box, pos, pos + reach ).Run();
		if ( !ahead.Hit || ahead.StartedSolid || Standable( ahead.Normal ) )
			return;
		float maxRise = StepUp + Skin;
		var lift = trace.Box( box, pos, pos + Vector3.Up * (StepUp + 0.5f + Skin) ).Run();
		if ( lift.StartedSolid )
			return;
		float up = lift.Hit ? MathF.Max( 0f, lift.Distance - 0.05f ) : StepUp + 0.5f + Skin;
		if ( up <= Skin )
			return;
		var raised = pos + Vector3.Up * up;
		var across = trace.Box( box, raised, raised + reach ).Run();
		if ( across.StartedSolid )
			return;
		float clear = across.Distance - (across.Hit ? 0.5f : 0f);
		if ( clear <= ahead.Distance + MathF.Min( Skin, len * 0.1f ) )
			return;
		var over = raised + dir * clear;
		var down = trace.Box( box, over, over - Vector3.Up * (up + Skin) ).Run();
		if ( !down.Hit || down.StartedSolid || !Standable( down.Normal ) )
			return;
		float rise = down.EndPosition.z - pos.z;
		if ( rise <= Skin || rise > maxRise )
			return;
		var to = pos + Vector3.Up * rise;
		if ( trace.Box( box, to, to + Vector3.Up * 0.01f ).Run().StartedSolid )
			return;
		pos = to;
		v = (v - groundVel).WithZ( 0f ) + groundVel;
	}

	// ── Teleports ───────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Move this hunter's sim body (a spawn, a respawn, a lab placement): the host places it at once; any other
	/// machine asks the host, and its own copy follows from the next snapshot. The root rotation stays upright — the view's
	/// facing is the controller's EyeAngles, not the body's.</summary>
	internal void PlaceInSim( Vector3 position )
	{
		_fallAnchor = new Transform( position, _fallAnchor.Rotation );
		if ( !Networking.IsActive || Networking.IsHost )
			HunterSim?.Place( GameObject.Id, position, Rotation.Identity );
		else
			RequestSimPlace( position );
	}

	[Rpc.Host]
	void RequestSimPlace( Vector3 position )
	{
		if ( Rpc.Caller is { } caller && caller.Id != (Network.Owner?.Id ?? Guid.Empty) && !caller.IsHost )
			return; // only the hunter's own player (or the host) moves it
		_fallAnchor = new Transform( position, _fallAnchor.Rotation );
		HunterSim?.Place( GameObject.Id, position, Rotation.Identity );
	}

	// ── What game code reads (the engine controller's, or the sim's while the hunter walks there) ───────────

	/// <summary>The hunter's velocity: the sim body's while it walks there (the stock controller's body is only presentation
	/// then, placed each frame, so its velocity reads zero).</summary>
	public Vector3 MoveVelocity => SimSelf is { } b && b.Phys.IsValid() ? b.Phys.Velocity : _controller.IsValid() ? _controller.Velocity : Vector3.Zero;

	// Every machine, every frame while sim-walking: the posture the sim has (the owner's prediction; others follow the synced
	// IsDucking) onto the stock controller, which sizes the colliders bullets hit and the eye from it.
	void SyncSimPosture()
	{
		if ( !Owned || SimSelf is not { } b || !_controller.IsValid() )
			return;
		if ( _controller.IsDucking != b.Ctrl.Ducked )
			_controller.IsDucking = b.Ctrl.Ducked;
	}
}
