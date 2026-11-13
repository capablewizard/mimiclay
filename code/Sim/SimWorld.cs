using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Mimiclay.Sim;

/// <summary>
/// The shared simulation world — host-authoritative physics with client prediction + rollback (Phase 0 of the
/// integration in the networked-physics-authority memory; proven first by RollbackLab and RollbackRig).
///
/// <para><b>Model.</b> Every machine keeps one private <see cref="PhysicsWorld"/> (the only kind that may be stepped and
/// rewound by hand), holding a copy of the scene's static collision plus every REGISTERED body. The host's copy is the
/// truth: each tick it drives every body that has a driver with its owner's input for that tick, steps, and every
/// <see cref="SnapshotEvery"/> ticks broadcasts the awake bodies. A client runs AHEAD of the host (far enough that its
/// inputs arrive in time), predicts with its own input and a guess for everyone else's, and when a snapshot disagrees
/// with what it predicted for that tick, restores the snapshot and replays its stored inputs to the present.
/// The scene shows the result: <see cref="VisualPose"/> interpolates between ticks and fades each correction out.</para>
///
/// <para><b>Lifetime.</b> Nothing exists until the host registers a body (or a client hears of one), so a scene that
/// never uses it pays nothing. Bodies are keyed by a network-wide <see cref="Guid"/> (normally the scene object's id)
/// and addressed in snapshots by a compact id the host assigns.</para>
///
/// <para><b>Messages.</b> Reliable: body adds (with full state, also how late joiners catch up — a client that sees an
/// id it doesn't know asks for everything), removes, rest poses. Unreliable: snapshots (awake bodies + those asleep for
/// under <see cref="RestResendTicks"/>, quantised — see <see cref="SimQuant"/>) and client inputs (each message repeats
/// the last <see cref="InputRedundancy"/> ticks).</para>
///
/// Split across SimWorld.cs (registry, statics, presentation), SimWorld.Tick.cs (host + client ticks, rollback) and
/// SimWorld.Net.cs (messages).
/// </summary>
public sealed partial class SimWorld : GameObjectSystem
{
	// The sim's own clock (see RunTicks), independent of the project tick rate. Every tick count below is DERIVED from
	// seconds so the rate can change: 50 → 100 Hz on 2026-11-05 (Rocket League: smaller steps = smaller penetrations and
	// corrections; at 50 Hz a walking prop moved 6.4u per tick, the size of the walker-vs-walker slop and the landing overshoot).
	public const int TickRate = 100;
	public const float Dt = 1f / TickRate;
	/// <summary>Seconds → ticks at <see cref="TickRate"/> (a constant expression for the tick-count constants).</summary>
	public const float T = TickRate;
	public const int SnapshotEvery = TickRate / 50; // 50 Hz (was 25: every snapshot interval of staleness is ~6u of another player's prop at walk speed, folded into its drawn pose when their input changes)
	const int InputRedundancy = (int)(0.16f * T + 0.5f);
	const int HistoryTicks = (int)(5f * T);
	const int RestResendTicks = (int)(0.5f * T);   // keep sending a newly asleep body this long (its reliable rest may still be in flight)
	const int StaticRefreshTicks = (int)(1f * T);  // re-check the scene's static collision once a second
	const int ShapeRefreshTicks = (int)(0.2f * T); // re-check FromGameObject sources for collider rebuilds
	const int MaxTicksPerFrame = (int)(0.1f * T);
	const int TargetSlack = (int)(0.04f * T + 0.5f);

	[ConVar( "mimi_sim_fakelag", Help = "Sim: one-way delay (ms) added to every sim message this machine receives (set on both machines)." )]
	public static int FakeLagMs { get; set; } = 0;

	// 0.5u (was 0.05): a snapshot's quantisation alone reads as ~0.05u (position 1/64u per axis, rotation 0.08° × the 20u
	// arm, velocity 1/16 u/s), so the old threshold replayed every OTHER snapshot on a prop walking a straight line on a
	// flat floor (measured 30 replays / 65 snapshots, every error 0.05–0.06u). A replay from the quantised restore is not
	// the trajectory we had: at a step edge the foot sweep's answer flipped and the replay climbed 24u while the host
	// didn't (corrections of 9–26u, the clay shown 20u under the floor / 12u above it while they faded — "jittery",
	// "floating", "through the floor"). Skipping keeps our own prediction, which matched the host within quantisation.
	[ConVar( "mimi_sim_skip_eps", Help = "Sim: replay only when a snapshot disagrees with the prediction by more than this (u). Keep it well above the ~0.05u a snapshot's quantisation reads as." )]
	public static float SkipEpsilon { get; set; } = 0.5f;

	// Someone else's driven body (another player's prop) is a GUESS here, wrong by 5–25u whenever its player turns or slides
	// along something; replaying the world for that re-steps OUR body from a restored state (see SkipEpsilon). So their
	// errors only count past this, and a skipped snapshot still puts them where the host has them (NudgeRemote) — which is
	// better than any replay of a guess, so this is high enough that only a teleport-sized error forces one. They are shown
	// from the host's poses regardless (RemotePose); a loose body one of them pushes still counts at SkipEpsilon.
	[ConVar( "mimi_sim_skip_eps_remote", Help = "Sim: other players' props only force a replay when the guess of them is off by more than this (u)." )]
	public static float RemoteSkipEpsilon { get; set; } = 100f;

	// 6 ticks (120 ms; the engine interpolates its proxies 100 ms back): at 3 the display kept catching the newest snapshot
	// and holding — host ticks come in bursts with its frames, so snapshots land a tick or two apart from even — which
	// showed as a stutter (a frame at 149 u/s then one at 294 around a steady 224).
	[ConVar( "mimi_sim_remote_delay", Help = "Sim: other players' props are shown this many host ticks behind the newest snapshot, interpolated between the host's poses." )]
	public static int RemoteDelayTicks { get; set; } = (int)(0.12f * T + 0.5f);

	// The walk's gravity (HiderController.Gravity's default), for carrying another player's airborne prop forward (NudgeRemote).
	const float WalkGravity = 800f;

	// A rotation error counts as the distance it moves a point this far out (u per degree: 20u arm). Quantized snapshot
	// rotations are ~0.08° off (0.03u), under SkipEpsilon; a wrong facing used to go unnoticed entirely.
	const float RotErrorArm = 20f * MathF.PI / 180f;

	[ConVar( "mimi_sim_smooth", Help = "Sim: how fast corrections fade from the visuals (1/s). 0 = show the raw pop." )]
	public static float SmoothRate { get; set; } = 12f;

	// Other players' props are drawn from our prediction and nudged toward each snapshot with the nudge folded into this fade
	// (NudgeRemote); at the walk's own 12/s a 25u nudge still overlapped a prop it was against by 15u for ~100 ms (measured) —
	// faster here, still a slide (a 25u nudge at 30/s crosses in ~70 ms, under walking speed).
	[ConVar( "mimi_sim_smooth_remote", Help = "Sim: how fast other players' props slide onto each snapshot's nudge (1/s)." )]
	public static float RemoteSmoothRate { get; set; } = 30f;

	// Other players' WALKERS are drawn through a critically damped tracker of their copy instead (natural frequency, 1/s):
	// replaying a 14 s observer trace offline, 25/s took the pusher's drawn-velocity jerks over 400 u/s from 14 to 0 (9 of
	// them over 1000 — a copy restored to a stopped host stops dead; a position fade can't smooth a velocity step) for a
	// mean lag of 15u behind the copy (max 48u). 40/s left 9 jerks at 9u lag; 15/s none at 25u lag.
	[ConVar( "mimi_sim_remote_track", Help = "Sim: how stiffly other players' walking props are drawn toward their predicted copy (1/s; 0 = the plain fade)." )]
	public static float RemoteTrackRate { get; set; } = 25f;
	// A critically damped tracker trails a constant-velocity target by 2v/ω (22u at walk speed at 25/s): fine for a bystander,
	// but the prop PUSHING our own body would be drawn that far short of the contact — so one in real contact with us tracks stiffer.
	[ConVar( "mimi_sim_remote_track_touched", Help = "Sim: the tracker stiffness (1/s) for another player's walker in contact with our own body." )]
	public static float RemoteTrackRateTouched { get; set; } = 60f;
	[ConVar( "mimi_sim_remote_track_rise", Help = "Sim: how fast the drawn walker of another player follows its copy UPWARD (1/s) - a late-known jump." )]
	public static float RemoteTrackRateRise { get; set; } = 70f;

	// Off by default: A/B 2026-11-04 (lobby rig, 200 ms RTT) showed no gain — own pawn correction p95 1.4u on vs 0.55u
	// off, boxes ~equal — and re-placing every awake body each snapshot is one more thing that disturbs contacts.
	[ConVar( "mimi_sim_quantize_host", Help = "Sim: the host continues from the quantised state it sends, so clients restore exactly what it simulates." )]
	public static bool QuantizeHost { get; set; } = false;

	public static SimWorld Current => Game.ActiveScene?.GetSystem<SimWorld>();

	public SimWorld( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 10, Update, "SimWorld" );
	}

	public override void Dispose()
	{
		DestroyWorld();
		base.Dispose();
	}

	PhysicsWorld _world;
	public PhysicsWorld World => _world;
	public bool Active => _world is not null;

	/// <summary>The last tick simulated here (the host's is the truth; a client's runs ahead of it).</summary>
	public int Tick { get; private set; } = -1;

	public bool IsAuthority => !Networking.IsActive || Networking.IsHost;
	static Guid LocalId => Connection.Local?.Id ?? Guid.Empty;

	readonly Dictionary<Guid, SimBody> _byKey = new();
	readonly Dictionary<ushort, SimBody> _byId = new();
	readonly List<SimBody> _bodies = new();
	ushort _nextId = 1;
	int _maxId;

	public IReadOnlyList<SimBody> Bodies => _bodies;
	public SimBody Find( Guid key ) => _byKey.TryGetValue( key, out var b ) ? b : null;

	/// <summary>A player WALKING — a prop's walk or a hunter's (HunterController.Sim): drawn from the prediction on other
	/// machines, carried forward with nudges, tracked, paired with what it touches — the same treatment either way.</summary>
	public static bool IsWalker( byte driver ) => driver == HiderController.SimWalkDriverId || driver == HunterController.SimHunterDriverId;

	public event Action<SimBody> BodyAdded;
	/// <summary>A body stopped being a walker (SimWorld.ApplyRedrive) — every machine, before its shapes are rebuilt.</summary>
	public event Action<SimBody> BodyLeftWalk;
	public event Action<SimBody> BodyRemoved;

	// Drivers by id — registered by whatever owns them (the same id on every machine).
	readonly Dictionary<byte, ISimDriver> _drivers = new();
	public void RegisterDriver( byte id, ISimDriver driver )
	{
		_drivers[id] = driver;
		foreach ( var b in _bodies )
			if ( b.Spec.Driver == id )
				b.Driver = driver;
	}

	// The game's own drivers, resolved from the CURRENT code every time: this system outlives a hotload, and a driver
	// registered once (from a pawn's OnStart) kept running the class it was before — the host sat on a deleted driver's
	// old instance for a whole test (a body that never rolled or rose while every client did). Anything else (a debug
	// rig's) comes from the registry.
	ISimDriver DriverFor( byte id ) => id switch
	{
		0 => null,
		HiderController.SimWalkDriverId => HiderController.WalkDriver,
		HunterController.SimHunterDriverId => HunterController.SimDriver,
		SimHoldDriver.Id => SimHoldDriver.Instance,
		SimDownDriver.Id => SimDownDriver.Instance,
		_ => _drivers.TryGetValue( id, out var d ) ? d : null,
	};

	/// <summary>This machine's input for a tick (the gameplay code that owns local control sets it).</summary>
	public Func<int, SimInput> LocalInput { get; set; }

	/// <summary>The body this machine's input drives (its player's prop). Other driven bodies this machine owns — the
	/// host's bots — get no input. Empty = the first driven body we own (the debug rig's one pawn).</summary>
	public Guid LocalControlled { get; set; }

	// Which scene object a sim PhysicsBody stands for (a static copy, a follower, a registered body) — a driver's trace
	// results carry only the body. Stale entries are harmless (a removed body never comes back from a trace) and pruned.
	readonly Dictionary<PhysicsBody, GameObject> _sourceOf = new();

	/// <summary>The scene object a sim body (from a sim trace) stands for, or null.</summary>
	public GameObject SourceOf( PhysicsBody body ) => body is not null && _sourceOf.TryGetValue( body, out var go ) ? go : null;

	/// <summary>Sim traces (the drivers' sweeps). In a replay this is the world being replayed — the same one.</summary>
	public PhysicsTraceBuilder Trace => _world.Trace;

	// ── Registry ────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Host: add a body to the simulation (broadcast to everyone). Re-registering a key updates nothing.</summary>
	public SimBody Register( SimBodySpec spec )
	{
		if ( !IsAuthority )
			throw new InvalidOperationException( "SimWorld.Register is host-only" );
		if ( _byKey.TryGetValue( spec.Key, out var existing ) )
			return existing;

		EnsureWorld();
		var body = Create( spec, _nextId++ );
		SetEngineTransformSync( body, false );
		if ( Networking.IsActive )
			BroadcastAdds( WriteAdds( new[] { body } ) );
		return body;
	}

	/// <summary>Host: remove a body (broadcast). A presented scene object is left exactly where the sim had it.</summary>
	public void Unregister( Guid key )
	{
		if ( !IsAuthority || !_byKey.TryGetValue( key, out var b ) )
			return;
		if ( (b.Spec.Flags & SimBodyFlags.Present) != 0 && Scene.Directory.FindByGuid( b.Spec.Source ) is { } go && go.IsValid() && b.Phys.IsValid() )
		{
			go.WorldPosition = b.Phys.Position;
			go.WorldRotation = b.Phys.Rotation;
			go.Transform.ClearInterpolation();
		}
		SetEngineTransformSync( b, true );
		Remove( b );
		if ( Networking.IsActive )
			BroadcastRemove( b.Id );
	}

	// Host: a presented NETWORK object is moved by the sim on every machine, so the engine must stop syncing its
	// transform from the owner (it would fight the sim on every client) — and start again once it leaves the sim.
	void SetEngineTransformSync( SimBody b, bool on )
	{
		if ( (b.Spec.Flags & SimBodyFlags.Present) == 0 || !Networking.IsActive )
			return;
		var go = Scene.Directory.FindByGuid( b.Spec.Source );
		if ( !go.IsValid() || !go.Network.Active )
			return;
		var flags = go.Network.Flags;
		go.Network.Flags = on ? flags & ~NetworkFlags.NoTransformSync : flags | NetworkFlags.NoTransformSync;
	}

	SimBody Create( in SimBodySpec spec, ushort id )
	{
		var body = new SimBody { Spec = spec, Id = id, World = this, Driver = DriverFor( spec.Driver ) };

		var p = new PhysicsBody( _world ) { BodyType = PhysicsBodyType.Dynamic };
		body.Phys = p;
		p.Transform = spec.Pose;
		p.Locking = LockFor( spec.Flags );
		p.GravityEnabled = (spec.Flags & SimBodyFlags.NoGravity) == 0;
		BuildShapes( body );
		if ( (spec.Flags & SimBodyFlags.StartAsleep) != 0 && body.ShapesReady )
			p.Sleeping = true;
		body.WasSleeping = p.Sleeping;
		body.Prev = body.Cur = p.Transform;

		_byKey[spec.Key] = body;
		if ( spec.Shape != SimShapeKind.Box && Scene.Directory.FindByGuid( spec.Source ) is { } srcGo )
			_sourceOf[p] = srcGo;
		_byId[id] = body;
		_bodies.Add( body );
		_maxId = Math.Max( _maxId, id );
		if ( spec.Shape != SimShapeKind.Box )
			ExcludeFromStatics( spec.Source );
		BodyAdded?.Invoke( body );
		return body;
	}

	void Remove( SimBody b )
	{
		_byKey.Remove( b.Key );
		_byId.Remove( b.Id );
		_bodies.Remove( b );
		if ( b.Phys is not null && b.Phys.IsValid() )
			b.Phys.Remove();
		if ( b.Spec.Shape != SimShapeKind.Box )
			_excludedStatics.Remove( b.Spec.Source );
		BodyRemoved?.Invoke( b );
	}

	void EnsureWorld()
	{
		if ( _world is not null )
			return;
		var scene = Scene.PhysicsWorld;
		_world = new PhysicsWorld { Gravity = scene.Gravity, SubSteps = scene.SubSteps };
		// The scene's collision rules (copied shapes keep their tags), so what passes through what matches — a ghosting
		// prop's tags, triggers, the move hull's own tags.
		if ( scene.CollisionRules is { } rules )
			_world.CollisionRules = rules;
		RefreshStatics();
		RefreshFollowers();
		SimStats.Reset();
		SendSimPing(); // client: measure the round trip before the first sync (see _rttTicks)
		Log.Info( $"SimWorld: world created ({(IsAuthority ? "host" : "client")}), {_staticCopies.Count} static bodies copied" );
	}

	void DestroyWorld()
	{
		ClearHoldFilters();
		foreach ( var b in _bodies.ToList() )
			Remove( b );
		_staticCopies.Clear();
		_excludedStatics.Clear();
		_followers.Clear();
		if ( _world is not null && _world.IsValid() )
			_world.Delete();
		_world = null;
		Tick = -1;
		_synced = false;
		_lastSnap = -1;
		_history.Clear();
		_myInputs.Clear();
	}

	/// <summary>Host: drop everything (every machine follows).</summary>
	public void ClearAll()
	{
		if ( !IsAuthority )
			return;
		foreach ( var b in _bodies.ToList() )
			Unregister( b.Key );
	}

	// ── Shapes ──────────────────────────────────────────────────────────────────────────────────────────────

	void BuildShapes( SimBody body )
	{
		var p = body.Phys;
		switch ( body.Spec.Shape )
		{
			case SimShapeKind.Box:
				p.AddBoxShape( Vector3.Zero, Rotation.Identity, body.Spec.HalfExtents );
				body.ShapesReady = true;
				break;

			case SimShapeKind.FromGameObject:
				var src = SourceBody( body.Spec.Source, out int version );
				p.ClearShapes();
				body.ShapesReady = src is not null && CopyShapes( src, p ) > 0;
				body.ShapeVersion = version;
				break;

			case SimShapeKind.FromSource:
				p.ClearShapes();
				var shapeSource = ShapeSourceOf( body.Spec.Source );
				body.ShapesReady = shapeSource is not null && shapeSource.BuildSimShapes( p ) && p.ShapeCount > 0;
				body.ShapeVersion = shapeSource?.SimShapeVersion ?? -1;
				if ( body.ShapesReady )
					foreach ( var s in p.Shapes )
						s.Tags.Add( SelfTag( body ) ); // its own sweeps skip it (see SelfTag)
				break;
		}

		if ( body.ShapesReady && body.Spec.Mass > 0f )
			p.Mass = body.Spec.Mass;
		if ( body.ShapesReady && body.Spec.Driver == SimHoldDriver.Id && !HeldBodyDriver.HeldFrictionKept )
			foreach ( var s in p.Shapes )
				s.Friction = 0f; // a held body slides rather than grips (HeldBodyDriver.HeldFriction)
		// Nothing to collide with yet (a client still building the source's collider): hold it still until it has.
		p.MotionEnabled = body.ShapesReady && (body.Spec.Flags & SimBodyFlags.Anchored) == 0;
		// Copied props can be small and fast (a knocked knob): without CCD they tunnelled through the zero-thickness mesh floor.
		// Walkers too (the scene walk's body has it on): a small prop's hull falling at MaxFallSpeed moves 60u a tick, and
		// the foot sweep only looks a step and a snap below it.
		p.EnhancedCcd = body.Spec.Shape != SimShapeKind.Box;
		HookContacts( body );
	}

	// Host: a registered body touching something in a step (start = a new contact, else a held one) goes to the prop it is
	// (HiderController.SimContact) — the sim's own collision events, for what a scene Rigidbody's collision listeners used
	// to hear (a held or loose prop's splats and scrapes: its scene body is keyframed presentation now, which never gets a
	// contact). The host only: it never replays, so every event is a real one, once; a client's replays would re-run old
	// contacts.
	void HookContacts( SimBody body )
	{
		if ( !IsAuthority || !body.ShapesReady || body.Spec.Shape == SimShapeKind.Box || body.Phys is not { } p )
			return;
		p.EnableTouch = true;
		p.EnableTouchPersists = true;
		p.OnIntersectionStart = c => HiderController.SimContact( body, c, true );
		p.OnIntersectionUpdate = c => HiderController.SimContact( body, c, false );
	}

	ISimShapeSource ShapeSourceOf( Guid source )
	{
		var go = Scene.Directory.FindByGuid( source );
		return go.IsValid() ? go.Components.GetAll<Component>().OfType<ISimShapeSource>().FirstOrDefault() : null;
	}

	/// <summary>A tag only this body's shapes carry: a driver's own sweeps leave it out (a sim trace can't ignore a body
	/// any other way).</summary>
	public static string SelfTag( SimBody b ) => $"simself{b.Id}";

	// The scene body a FromGameObject spec copies, and the collider version it's at (SdfCollider.BuildSerial; 0 otherwise).
	//
	// The version covers everything the copy depends on: the collider's build (on the object or a CHILD — a prop pawn's
	// collider is on its disguise), the shape count, and the collision TAGS. Tags matter: a released prop sheds the
	// `propbody` tag (live props ignore each other), and a client that copied it a moment too early kept the stale tag
	// forever — its sim walker then passed straight through the prop, and the host's snapshot dragged it back out:
	// "walking into props ejects me".
	PhysicsBody SourceBody( Guid source, out int version )
	{
		version = 0;
		var go = Scene.Directory.FindByGuid( source );
		if ( !go.IsValid() )
			return null;
		foreach ( var b in Scene.PhysicsWorld.Bodies )
		{
			if ( !b.IsValid() || b.ShapeCount == 0 || b.GameObject != go )
				continue;
			var sdf = go.Components.Get<SdfCollider>( FindMode.EverythingInSelfAndDescendants );
			// ...and where a CHILD collider sits under the object: an origin rebase or a tilt fold moves the disguise and its
			// shapes on the body without a rebuild, and a copy at the old offset collides where the clay no longer is.
			int place = 0;
			if ( sdf.IsValid() && sdf.GameObject != go )
			{
				var local = sdf.GameObject.LocalTransform;
				var ang = local.Rotation.Angles();
				place = HashCode.Combine( (int)MathF.Round( local.Position.x * 100f ), (int)MathF.Round( local.Position.y * 100f ), (int)MathF.Round( local.Position.z * 100f ),
					(int)MathF.Round( ang.pitch * 10f ), (int)MathF.Round( ang.yaw * 10f ), (int)MathF.Round( ang.roll * 10f ) );
			}
			version = HashCode.Combine( sdf.IsValid() ? sdf.BuildSerial : 0, b.ShapeCount, TagKeyOf( b ), place );
			return b;
		}
		return null;
	}

	// The one place shapes are copied between worlds. NOT PhysicsBody.AddCloneShape: in the retail engine it is an
	// obsolete STUB that adds nothing and returns null (found 2026-11-04 — every "clone" before that was empty).
	// Spheres and capsules are copied as themselves; hulls and meshes are rebuilt from Triangulate (body-local points —
	// verified: a box dropped on a rebuilt map floor rests within 0.2u of the real one). Tags + surface carried over.
	internal static int CopyShapes( PhysicsBody from, PhysicsBody to, bool solidOnly = true )
	{
		int n = 0;
		foreach ( var s in from.Shapes )
		{
			if ( solidOnly && s.IsTrigger )
				continue;
			PhysicsShape made = null;
			if ( s.IsSphereShape )
				made = to.AddSphereShape( s.Sphere, rebuildMass: false );
			else if ( s.IsCapsuleShape )
				made = to.AddCapsuleShape( s.Capsule.CenterA, s.Capsule.CenterB, s.Capsule.Radius, rebuildMass: false );
			else
			{
				s.Triangulate( out var pos, out var idx );
				if ( pos.Length < 3 )
					continue;
				if ( s.IsHullShape )
					made = to.AddHullShape( Vector3.Zero, Rotation.Identity, pos.AsSpan(), rebuildMass: false );
				else
				{
					var ints = new int[idx.Length];
					for ( int i = 0; i < idx.Length; i++ ) ints[i] = (int)idx[i];
					made = to.AddMeshShape( pos.AsSpan(), ints.AsSpan() );
				}
			}
			if ( made is null )
				continue;
			if ( s.Tags is { } tags )
				foreach ( var t in tags.TryGetAll() )
					made.Tags.Add( t );
			if ( s.Surface is not null )
				made.Surface = s.Surface;
			n++;
		}
		// Every body type: shapes were added with rebuildMass off, and a body left with dirty mass trips Box3D's solver
		// assert ("body->flags & b3_dirtyMass") on the next step — static and keyframed copies included.
		if ( n > 0 )
			to.RebuildMass();
		return n;
	}

	// Every ShapeRefreshTicks: FromGameObject bodies whose source collider rebuilt (a sculpt, a crater) get new shapes,
	// and ones still waiting for theirs try again. State is kept.
	void RefreshShapes()
	{
		// Host: a body whose scene object is gone (destroyed without unregistering — a disconnected player's pawn) leaves.
		if ( IsAuthority )
			foreach ( var gone in _bodies.Where( b => b.Spec.Shape != SimShapeKind.Box && !Scene.Directory.FindByGuid( b.Spec.Source ).IsValid() ).ToList() )
				Unregister( gone.Key );

		foreach ( var b in _bodies )
		{
			int version;
			if ( b.Spec.Shape == SimShapeKind.FromGameObject )
				SourceBody( b.Spec.Source, out version );
			else if ( b.Spec.Shape == SimShapeKind.FromSource )
				version = ShapeSourceOf( b.Spec.Source )?.SimShapeVersion ?? -1;
			else
				continue;
			if ( b.ShapesReady && version == b.ShapeVersion )
				continue;
			var state = b.Capture();
			BuildShapes( b );
			if ( b.ShapesReady )
			{
				b.Restore( state );
				if ( b.PendingMotion is { } m && IsAuthority )
				{
					b.Phys.Sleeping = false;
					b.Phys.Velocity = m.v;
					b.Phys.AngularVelocity = m.w;
				}
				b.PendingMotion = null;
			}
			SimStats.Reshapes++;
		}
	}

	// ── Statics ─────────────────────────────────────────────────────────────────────────────────────────────
	// The scene's static collision, copied in and kept up to date (a crater or a sculpt rebuilds a static prop's collider;
	// a converted prop leaves). Objects a registered body copies are left out — they move in the sim instead.

	readonly Dictionary<PhysicsBody, (PhysicsBody copy, int version)> _staticCopies = new();
	readonly HashSet<Guid> _excludedStatics = new();

	// The object now moves in the sim itself: no static copy of it, and no follower (a follower would collide with its
	// own sim body).
	void ExcludeFromStatics( Guid go )
	{
		_excludedStatics.Add( go );
		foreach ( var (src, f) in _followers.ToList() )
		{
			if ( src.IsValid() && src.GameObject.IsValid() && src.GameObject.Id == go )
			{
				f.Copy.Remove();
				_followers.Remove( src );
			}
		}
		foreach ( var (src, c) in _staticCopies.ToList() )
		{
			if ( src.IsValid() && src.GameObject.IsValid() && src.GameObject.Id == go )
			{
				c.copy.Remove();
				_staticCopies.Remove( src );
			}
		}
	}

	/// <summary>A static scene object is leaving (a converted prop's original, destroyed at the end of this frame): drop its
	/// copy NOW. Left to RefreshStatics (once a second) the copy outlived the original by up to a second, and the pawn that
	/// replaced it spawned inside it — a hollow garden pot's hulls interlocked with its own stale copy and the solver threw
	/// the possessed pot ~190u into the air (2026-11-13).</summary>
	public void ForgetStatic( GameObject go )
	{
		if ( go.IsValid() )
			ExcludeFromStatics( go.Id );
	}

	void RefreshStatics()
	{
		if ( _world is null )
			return;
		// Exclusions for objects that no longer exist (ForgetStatic's originals, once destroyed).
		_excludedStatics.RemoveWhere( id => !Scene.Directory.FindByGuid( id ).IsValid() );
		foreach ( var dead in _sourceOf.Keys.Where( k => !k.IsValid() ).ToList() )
			_sourceOf.Remove( dead );
		var seen = new HashSet<PhysicsBody>();
		foreach ( var b in Scene.PhysicsWorld.Bodies.ToList() )
		{
			if ( !b.IsValid() || b.BodyType != PhysicsBodyType.Static || b.ShapeCount == 0 )
				continue;
			var go = b.GameObject;
			if ( go.IsValid() && _excludedStatics.Contains( go.Id ) )
				continue;
			seen.Add( b );
			int version = go.IsValid() && go.Components.Get<SdfCollider>() is { } sdf && sdf.IsValid() ? sdf.BuildSerial : 0;
			if ( _staticCopies.TryGetValue( b, out var had ) )
			{
				if ( had.version == version && had.copy.Transform.Position.AlmostEqual( b.Transform.Position, 0.01f ) )
					continue;
				had.copy.Remove();
			}
			var copy = new PhysicsBody( _world ) { BodyType = PhysicsBodyType.Static };
			copy.Transform = b.Transform;
			CopyShapes( b, copy );
			_staticCopies[b] = (copy, version);
			_sourceOf[copy] = b.GameObject;
		}
		foreach ( var gone in _staticCopies.Keys.Where( k => !seen.Contains( k ) ).ToList() )
		{
			_staticCopies[gone].copy.Remove();
			_staticCopies.Remove( gone );
		}
	}

	// ── Presentation ────────────────────────────────────────────────────────────────────────────────────────

	RealTimeSince _sinceTick;

	/// <summary>Where to SHOW a body this frame: between its last two ticks, plus the fading correction. Someone else's
	/// driven body, on a client: between the host's own poses instead (<see cref="RemotePose"/>).</summary>
	public Transform VisualPose( SimBody b )
	{
		var raw = PoseFrom( b, b.ShownFromAuth );
		return new Transform( raw.Position + b.PosOffset, b.RotOffset * raw.Rotation );
	}

	// The body's pose from one of the two sources, before the fading correction.
	Transform PoseFrom( SimBody b, bool authority )
	{
		if ( authority && b.Authority.Count > 0 )
			return RemotePose( b );
		float a = _alpha;
		return new Transform( Vector3.Lerp( b.Prev.Position, b.Cur.Position, a ), Rotation.Slerp( b.Prev.Rotation, b.Cur.Rotation, a ) );
	}

	// Another player's prop (or a bot's) on a client is shown from the host's poses, a few ticks behind the newest snapshot
	// — the way the engine shows a network proxy — NOT from our prediction of it. Predicting it (keep going at its last
	// velocity) and correcting it every snapshot made it lurch whenever its player turned, stopped or slid along something
	// (measured on a straight walk past a prop: shown speed 658 u/s against a real 170, corrections p95 11u max 26u). Its
	// sim body still predicts and collides (and is kept near the truth by NudgeRemote); only what's drawn comes from here.
	//
	// So is a LOOSE body nothing of ours has touched since it last rested (LocalTouched): a prop the host throws is a
	// guess here until the host's snapshots correct it — and the moment it left the host's hands it used to switch from
	// these poses (a few ticks behind) to our prediction (a lead ahead): a jump of ~0.35 s of its flight, then the
	// corrections' fades (user: "rubber-banding when the host drops or throws"). Touch it — walk into it, stand on it, push
	// it with a carried prop, grab it — and it switches to our prediction, which answers at once; the switch is blended
	// (UpdatePresentationSource), and it goes back to the host's poses once it has come to rest, where both agree.
	// Another player's WALKER is drawn from our prediction of it (its copy here: the host's state carried forward on its guessed
	// input, nudged toward each snapshot with the nudge folded into the fade — NudgeRemote), never from the host's delayed
	// poses: our own body reacts to that copy, so drawing the walker anywhere else put the contact out of time (a pusher drawn
	// 100u behind the copy that was already moving us; user 2026-11-05) and switching sources at contact popped it 80–100u.
	// Their held and lying bodies (hold / down drivers) stay on the host's poses: those guesses are weak.
	// Our OWN body lying down is drawn from the host's poses while it TUMBLES (no input of ours shapes that but the additive
	// tumble push; the RISE is ours — started by our Space — and drawn from the copy: on the host's poses the hop showed a round
	// trip late, a hitch at every flip): a hunter's throw of us switched from the host-drawn hold to our copy at the release, and the copy
	// was a guessed swing 139u ahead — a one-frame pop at 8000 u/s (user, 2026-11-06). Settled, it is the walk and ours to
	// predict (the switch happens at rest, a small fold); the flip switches back at the hop (slow), and the walker comes
	// back to the copy when upright.
	//
	// A body WE threw (ThrownHere) stays on our prediction whoever drives it now: we drew it from the copy all through the
	// hold, and the host's poses are a lead plus the display delay behind — switching at the hand-over to its wearer jumped it
	// back along its own flight, 108–164u in one frame on every bot throw (user, 2026-11-06: "really severe popping when we
	// release a prop from the hunter's point of view"). Its wearer's late input (a push, the flip) reaches the copy through the
	// replays and fades in; it goes over to the host's poses once both agree (UpdatePresentationSource).
	bool ShownFromAuthority( SimBody b )
		=> !IsAuthority && !b.ThrownHere && b.Authority.Count > 0 && ((b.IsRemoteDriven && !IsWalker( b.Spec.Driver ) && !(b.Spec.Driver == SimDownDriver.Id && RecentlyTouched( b ))) || (b.Driver is null && b.Spec.Owner == Guid.Empty && !b.LocalTouched && !RecentlyTouched( b ))
			|| (b.IsLocalOwned && b.Spec.Driver == SimDownDriver.Id && !b.Ctrl.Grounded && !b.Ctrl.AirFromJump));

	// Another player's walker our body has been against within the last TouchShowTicks (SimBody.TouchedTick) — one we push, or one pushing us:
	// drawn from our prediction and left un-nudged, so a prop we push moves under our push now rather than a round trip plus
	// the display delay later; meanwhile its copy counts in full in the mispredict check (SkipEpsilon, not RemoteSkipEpsilon),
	// so replays keep it on the host's track. Left to the 100u remote guard it drifted on its guess and popped when the hold
	// ended (user 2026-11-05: "big desyncs" when two props that had just collided came near).
	const int TouchShowTicks = (int)(0.24f * T + 0.5f);
	// The window covers our LEAD too: the host states a nudge or replay starts from are a lead old, so a contact that ended on
	// our clock is still going on in them. Ending after TouchShowTicks alone (0.24 s, shorter than a 0.35 s lead), a pushed
	// walker went back to per-body nudges while the host state they carried forward still had the pusher pushing it - carried
	// on alone, it ended up 40u inside the pusher's copy and stayed there until a replay (measured 2026-11-06, col_run.sh push
	// at 310 ms: 45u extra depth on the pusher's screen in every run).
	bool RecentlyTouched( SimBody b ) => b.TouchedTick > -1_000_000 && Tick - b.TouchedTick <= TouchShowTicks + (int)MathF.Ceiling( _leadAvg );

	// Drawing-source switches (UpdatePresentationSource): how long the other source must be wanted, how far a switch may fold
	// before it is shown as a jump, and how long its offset fades on the switch spring (SwitchSpring, gentler than corrections').
	const float SwitchSettleTime = 0.08f;
	const float DriveChangeWindow = 0.3f;
	const float TouchSwitchMaxFold = 60f;
	[ConVar( "mimi_sim_release_predict", Help = "Sim: draw a prop another player lets go of from our prediction straight away (1), or from the host's delayed poses (0)." )]
	public static bool ReleasePredict { get; set; } = true;
	// A remote release's switch: the quick spring it fades on, and the largest fold it takes (past it - a high ping, where the
	// release arrives about when the prop lands anyway - it stays on the host's poses).
	const float ReleaseSpring = 25f; // (15 left 14-38u of the catch-up when the target was hit, 0.2 s after the release was known)
	const float ReleaseMaxFold = 180f;
	const float SwitchSnapDistance = 250f;
	const float SoftFadeTime = 0.6f;
	[ConVar( "mimi_sim_switch_spring", Help = "Sim: stiffness (1/s) of the fade after a drawing-source switch when mimi_sim_switch_catchup is 0. Lower = a slower, gentler catch-up." )]
	public static float SwitchSpring { get; set; } = 8f;

	// The two sources a switch folds between are this client's lead plus the display delay apart in time, so the fold is the
	// body's speed times that gap: it grows with ping. A fixed stiffness covers it in a fixed time, at an extra speed that grows
	// with ping too (a surge at 200 ms, nothing at 30 ms). Scaled by the gap instead, the catch-up's peak extra speed is a fixed
	// share of the body's own speed at any ping (a critically damped spring from rest peaks at fold x stiffness / e), and it
	// simply takes longer when the gap is longer (user, 2026-11-06: "should that value be related to the ping of the player?").
	[ConVar( "mimi_sim_switch_catchup", Help = "Sim: after a drawing-source switch, the most the catch-up adds to the body's own speed, as a share of it (the fade then scales with this client's lead). 0 = the fixed mimi_sim_switch_spring." )]
	public static float SwitchCatchup { get; set; } = 0.75f;
	const float SwitchSpringMin = 3f, SwitchSpringMax = 20f;

	/// <summary>Seconds between the copy and the host pose shown on this client (its lead plus the display delay).</summary>
	float DisplayGap => MathF.Max( (_leadAvg + RemoteDelayTicks) * Dt, 0.05f );

	/// <summary>Client, for debug HUDs (SimLabOverlay): the measured sim round trip (ms; -1 until measured), how far our
	/// prediction runs ahead of the newest snapshot (ms), and how far behind the host's poses are drawn (ms).</summary>
	public float RoundTripMs => _rttTicks < 0f ? -1f : _rttTicks * Dt * 1000f;
	public float LeadMs => _leadAvg * Dt * 1000f;
	public float DisplayGapMs => DisplayGap * 1000f;

	// A replay's correction grows with the lead as a switch's fold does (a mispredict is the body's unexpected speed times how far
	// ahead we run): past a fixed 80u, every correction at 300 ms was a snap. Shown as a jump only past CorrectionSnapSpeed x the
	// gap (80u at most pings, more at high ones); a big one fades on the switch spring rather than the corrections' stiff one.
	const float CorrectionSnapSpeed = 500f; // (300 snapped a 173u post-release correction at 200 ms: a real mispredict, but a jump; faded it is a catch-up)
	const float BigCorrection = 40f;
	float CorrectionSnapDistance() => Math.Clamp( CorrectionSnapSpeed * DisplayGap, 80f, SwitchSnapDistance );

	/// <summary>Fade this body's offset on the gentle switch spring for the next while (the whole fade at the current stiffness).</summary>
	void SoftenFade( SimBody b )
	{
		b.SoftSpring = 0f;
		b.SoftFadeUntil = RealTime.Now + MathF.Max( SoftFadeTime, 5f / SwitchStiffness() );
	}

	/// <summary>The switch spring's stiffness on this client now (1/s) - see <see cref="SwitchCatchup"/>.</summary>
	public float SwitchStiffness()
	{
		if ( SwitchCatchup <= 0f )
			return SwitchSpring;
		return Math.Clamp( SwitchCatchup * MathF.E / DisplayGap, SwitchSpringMin, SwitchSpringMax );
	}

	// Every frame: switch a body's drawn source when its situation changed, folding the difference between the two into
	// the fading correction so nothing jumps. A touched body that has come to rest goes back to the host's poses.
	void UpdatePresentationSource()
	{
		if ( IsAuthority )
			return;
		_replaying = false; // (never left set past a frame, should a replayed step have thrown)
		foreach ( var b in _bodies )
		{
			if ( b.LocalTouched && b.Phys.IsValid() && b.Phys.Sleeping && b.Driver is null )
				b.LocalTouched = false;
			if ( b.ThrownHere && ThrowSettledHere( b ) )
				b.ThrownHere = false;
			if ( b.SeenDriver != b.Spec.Driver || b.SeenOwner != b.Spec.Owner )
			{
				b.SeenDriver = b.Spec.Driver;
				b.SeenOwner = b.Spec.Owner;
				b.DriveChangedAt = RealTime.Now;
			}
			bool auth = ShownFromAuthority( b );
			if ( auth == b.ShownFromAuth )
			{
				b.WantSwitchSince = -1f;
				continue;
			}
			// A switch the situation only flickers into is not taken: the other source must be wanted for SwitchSettleTime first
			// (measured: a thrown wearer's copy predicted itself settled for one frame, a replay un-settled it, and the drawn source
			// went copy -> host within 16 ms - a 1634 u/s blip). Our own touch, push or throw switches to the copy at once.
			bool ours = !auth && (b.LocalTouched || b.ThrownHere || RecentlyTouched( b ));
			// A switch that follows a change of drive (a grab, a release, standing up) is real and taken at once - for the host's first
			// poses to arrive too (DriveChangeWindow). Held back by the settle check, a grabbed wearer drew its own copy being yanked
			// toward the hunter's hand for those 80 ms (97u in one frame at 310 ms round trip, measured).
			if ( RealTime.Now - b.DriveChangedAt < DriveChangeWindow )
				ours = true;
			if ( !ours && b.Authority.Count > 0 )
			{
				if ( b.WantSwitchSince < 0f )
					b.WantSwitchSince = RealTime.Now;
				if ( RealTime.Now - b.WantSwitchSince < SwitchSettleTime )
					continue;
			}
			b.WantSwitchSince = -1f;
			var before = PoseFrom( b, b.ShownFromAuth );
			var after = PoseFrom( b, auth );
			if ( b.RemoteRelease && !auth && (before.Position - after.Position).Length > ReleaseMaxFold )
			{
				b.RemoteRelease = b.ThrownHere = false; // (too far to catch up smoothly: stays on the host's poses)
				continue;
			}
			// A TOUCH switch (a loose prop or a lying body something drawn from prediction is against) is for things being pushed:
			// they start at rest, where both sources agree. A fast one - a thrown prop hitting a player - is a lead plus the display
			// delay apart on its own flight (285u at 310 ms): past the slide limit it snapped (17000 u/s, measured 2026-11-06,
			// col_run.sh throw). It stays on the host's poses through the hit and switches once it has slowed.
			if ( !auth && (before.Position - after.Position).Length > TouchSwitchMaxFold && !b.ThrownHere && RealTime.Now - b.DriveChangedAt >= DriveChangeWindow
				&& (b.Driver is null || (b.IsRemoteDriven && b.Spec.Driver == SimDownDriver.Id)) )
				continue;
			b.ShownFromAuth = auth;
			var dp = before.Position - after.Position;
			TraceSwitch( b, auth, dp.Length );
			// The two sources differ by the body's speed times the lead plus the display delay (100-200u for a body in flight is
			// not a teleport): fold it, and fade it on the gentler switch spring for a moment.
			if ( dp.Length > SwitchSnapDistance )
			{
				b.PosOffset = Vector3.Zero; // too far to slide: show it where it is
				b.OffVel = Vector3.Zero;
				b.RotOffset = Rotation.Identity;
				continue;
			}
			b.PosOffset += dp;
			if ( b.RemoteRelease && !auth )
			{
				b.RemoteRelease = false;
				b.SoftSpring = ReleaseSpring; // (quick: caught up before it lands)
				b.SoftFadeUntil = RealTime.Now + 5f / ReleaseSpring;
			}
			else
				SoftenFade( b ); // (the whole fade, however slow, on the switch spring)
			b.RotOffset = before.Rotation * after.Rotation.Inverse * b.RotOffset;
			SimStats.SourceSwitches++;
		}
	}

	// Our throw is over when the copy has calmed down and sits where the host's poses show it — the switch to them is then
	// a few units, not a jump back along the flight. A body still being slid along by its wearer never agrees (its host pose
	// is a lead behind): it stays on the copy until it stands up, is held, or ThrownMaxTicks passes.
	const int ThrownMinTicks = (int)(0.3f * T + 0.5f);
	const int ThrownMaxTicks = (int)(10f * T + 0.5f);
	bool ThrowSettledHere( SimBody b )
	{
		if ( !b.Phys.IsValid() || Tick - b.ThrownTick > ThrownMaxTicks )
			return true;
		// Its wearer has taken over (steering it while it tumbles): no longer ballistic, so no longer ours to predict - every change
		// of their keys reached our copy a lead late as one big correction (126u in a frame, measured: past the 80u snap). Over to
		// the host's poses, through the source switch's gentle fade.
		if ( b.HostDriving && b.Spec.Driver == SimDownDriver.Id )
			return true;
		if ( Tick - b.ThrownTick < ThrownMinTicks || b.Authority.Count == 0 || b.Phys.Velocity.Length > 40f )
			return false;
		return (PoseFrom( b, false ).Position - RemotePose( b ).Position).Length < 6f;
	}

	// ── Drawn contacts ──
	// Each body's correction offset fades on its own (FadeCorrections), blind to what it is against: two props in contact in
	// the copy (their physics apart by the contact distance) were DRAWN up to 20u inside each other while one carried a fading
	// correction toward the other - a head-on push at 310 ms: copies 39-48u apart (host 43u), drawn 22u (measured 2026-11-06,
	// col_hunt.sh headon). For a pair in contact now or within TouchShowTicks, an offset may not draw them closer than their
	// physics poses are: the part of each offset pointing into the other is taken out, at most ContactPushSpeed (no snap).
	const float ContactPushSpeed = 1200f; // (800 took 4 frames to undo a 57u fold after a head-on push resolved: a visible 29u overlap, Tools/SimLab headon)
	readonly Dictionary<(int, int), (SimBody p, SimBody q, int tick)> _contacts = new();

	void NoteContact( SimBody p, SimBody q )
	{
		var key = p.Id < q.Id ? (p.Id, q.Id) : (q.Id, p.Id);
		_contacts[key] = (p, q, Tick);
	}

	void AgeContacts()
	{
		if ( _contacts.Count == 0 )
			return;
		List<(int, int)> old = null;
		foreach ( var (k, c) in _contacts )
			if ( Tick - c.tick > TouchShowTicks || !_bodies.Contains( c.p ) || !_bodies.Contains( c.q ) )
				(old ??= new()).Add( k );
		if ( old is not null )
			foreach ( var k in old )
				_contacts.Remove( k );
	}

	void ConstrainContacts()
	{
		if ( IsAuthority || _contacts.Count == 0 )
			return;
		float budget = ContactPushSpeed * Time.Delta;
		foreach ( var (_, c) in _contacts )
		{
			var p = c.p; var q = c.q;
			if ( !p.Phys.IsValid() || !q.Phys.IsValid() )
				continue;
			var srcP = PoseFrom( p, p.ShownFromAuth ).Position;
			var srcQ = PoseFrom( q, q.ShownFromAuth ).Position;
			var d = srcQ - srcP;
			if ( d.Length < 1f )
				continue;
			var n = d.Normal;
			float deficit = Vector3.Dot( d, n ) - Vector3.Dot( srcQ + q.PosOffset - srcP - p.PosOffset, n );
			if ( deficit <= 0.25f )
				continue; // (offsets are not drawing them into each other)
			float inP = MathF.Max( 0f, Vector3.Dot( p.PosOffset, n ) );  // P drawn shifted toward Q
			float inQ = MathF.Max( 0f, -Vector3.Dot( q.PosOffset, n ) ); // Q drawn shifted toward P
			float total = inP + inQ;
			if ( total <= 0f )
				continue;
			float take = MathF.Min( MathF.Min( deficit, total ), budget ) / total;
			ShiftOffset( p, -n * (inP * take), n );
			ShiftOffset( q, n * (inQ * take), -n );
		}
	}

	// Move a body's drawn offset (and the tracker's position, for a tracked walker), dropping any offset speed into the partner.
	static void ShiftOffset( SimBody b, Vector3 delta, Vector3 towardPartner )
	{
		if ( delta.IsNearZeroLength )
			return;
		b.PosOffset += delta;
		float vin = Vector3.Dot( b.OffVel, towardPartner );
		if ( vin > 0f )
			b.OffVel -= towardPartner * vin;
		if ( b.Tracking )
		{
			b.TrackPos += delta;
			float tin = Vector3.Dot( b.TrackVel - (b.Phys.IsValid() ? b.Phys.Velocity : Vector3.Zero), towardPartner );
			if ( tin > 0f )
				b.TrackVel -= towardPartner * tin;
		}
	}

	/// <summary>Client: something of ours is touching this body (see <see cref="ShownFromAuthority"/>).</summary>
	internal void NoteLocalTouch( SimBody b )
	{
		if ( b is not null && !IsAuthority )
			b.LocalTouched = true;
	}

	/// <summary>Client: a walker (a hunter — HunterController.Sim) is pushing or standing on <paramref name="q"/> this tick. The
	/// pusher stops a hair short of what it pushes (it follows the push), so the shape-overlap passes (NoteLocalContacts) never
	/// see the pair: it is noted here instead — ours pushing: the pushed body drawn from our prediction; two others': both
	/// drawn from the prediction and replayed together (no per-body nudges carrying each forward alone); either way the pair
	/// kept apart in the drawing (ConstrainContacts).</summary>
	internal void NotePushContact( SimBody p, SimBody q )
	{
		if ( IsAuthority || p is null || q is null )
			return;
		NoteContact( p, q );
		if ( p.IsLocalOwned )
		{
			if ( q.Driver is null && q.Spec.Owner == Guid.Empty )
				q.LocalTouched = true;
			q.TouchedTick = Tick;
		}
		else if ( p.IsRemoteDriven )
			p.TouchedTick = q.TouchedTick = Tick;
	}

	/// <summary>The registered body a sim <see cref="PhysicsBody"/> belongs to, or null (a static copy, a follower).</summary>
	public SimBody BodyOf( PhysicsBody phys )
	{
		if ( phys is null )
			return null;
		foreach ( var b in _bodies )
			if ( b.Phys == phys )
				return b;
		return null;
	}

	Transform RemotePose( SimBody b )
	{
		var list = b.Authority;
		// The host tick to show: our tick runs ~_leadAvg ahead of the newest snapshot; stay RemoteDelayTicks behind that,
		// so the next snapshot has normally landed by the time we reach it (a tick or two of jitter in the lead absorbed).
		double at = Tick + _alpha - _leadAvg - RemoteDelayTicks;
		var last = list[^1];
		if ( list.Count == 1 || at >= last.tick )
			return new Transform( last.p, last.r ); // nothing newer (a lost snapshot, or it's at rest and left the snapshots): hold
		int i = list.Count - 2;
		while ( i > 0 && list[i].tick > at )
			i--;
		var a = list[i];
		var n = list[i + 1];
		float f = n.tick > a.tick ? (float)Math.Clamp( (at - a.tick) / (n.tick - a.tick), 0.0, 1.0 ) : 1f;
		return new Transform( Vector3.Lerp( a.p, n.p, f ), Rotation.Slerp( a.r, n.r, f ) );
	}

	// Client: remember the host's pose of a body we don't drive at a host tick (RemotePose shows from these).
	void NoteAuthority( SimBody b, int tick, in SimBodyState st )
	{
		if ( IsAuthority || (b.IsLocalOwned && b.Spec.Driver != SimDownDriver.Id) )
			return; // (our own body DOWN keeps the host's poses too: thrown or flipping, it is drawn from them — ShownFromAuthority)
		var list = b.Authority;
		if ( list.Count > 0 && tick <= list[^1].tick )
			return;
		b.HostDriving = st.HasCtrl && st.Ctrl.Driving;
		list.Add( (tick, st.P, st.R) );
		if ( list.Count > 32 )
			list.RemoveRange( 0, list.Count - 32 );
	}

	public bool TryGetVisualPose( Guid key, out Transform pose )
	{
		pose = default;
		if ( !_byKey.TryGetValue( key, out var b ) )
			return false;
		pose = VisualPose( b );
		return true;
	}

	void AdvanceVisuals()
	{
		foreach ( var b in _bodies )
		{
			b.Prev = b.Cur;
			b.Cur = b.Phys.Transform;
		}
		_sinceTick = 0f;
	}

	void FadeCorrections()
	{
		float kOwn = SmoothRate > 0f ? MathF.Exp( -SmoothRate * Time.Delta ) : 0f;
		float kRemote = RemoteSmoothRate > 0f ? MathF.Exp( -RemoteSmoothRate * Time.Delta ) : 0f;
		foreach ( var b in _bodies )
		{
			float k = b.IsRemoteDriven ? kRemote : kOwn;
			if ( RemoteTrackRate > 0f && !IsAuthority && b.IsRemoteDriven && IsWalker( b.Spec.Driver ) && !b.ShownFromAuth && b.Phys.IsValid() )
			{
				// The tracker: the drawn position is a spring-damper after the copy's raw pose; PosOffset carries the difference
				// so the rest of the pipeline draws raw + offset as before. (A jump over 120u is a teleport: start there.)
				var raw = PoseFrom( b, false ).Position;
				if ( !b.Tracking )
				{
					// Taking over: start from where it is DRAWN and how fast that is moving (the offset still fading in - a source
					// switch's fold, a correction - included). Starting at the raw pose dropped that offset in one frame: a blip every
					// time a body became a walker again (a thrown wearer standing up, on its thrower's screen).
					b.TrackPos = raw + b.PosOffset;
					b.TrackVel = b.Phys.Velocity + b.OffVel;
					b.Tracking = true;
				}
				else if ( (raw - b.TrackPos).Length > SwitchSnapDistance )
				{
					b.TrackPos = raw; // a teleport: start there
					b.TrackVel = Vector3.Zero;
				}
				else
				{
					float w = RealTime.Now < b.SoftFadeUntil ? MathF.Min( SwitchStiffness() * 1.5f, RemoteTrackRate ) : RecentlyTouched( b ) ? RemoteTrackRateTouched : RemoteTrackRate;
					int steps = (int)(Time.Delta / 0.004f) + 1;
					float h = Time.Delta / steps;
					for ( int i = 0; i < steps; i++ )
					{
						// Upward the spring is much stiffer: another player's jump reaches our copy late, as one replay that puts it
						// straight into mid-air (29u up in a frame, measured); trailing it at the walk's rate, the drawn walker crossed
						// onto a ledge still below the top - sunk 10-12u into it on onlookers' screens at 310 ms (Tools/SimLab ledge).
						var d = raw - b.TrackPos;
						float wz = d.z > 0f ? MathF.Max( w, RemoteTrackRateRise ) : w;
						var acc = new Vector3( d.x * (w * w) - b.TrackVel.x * (2f * w), d.y * (w * w) - b.TrackVel.y * (2f * w), d.z * (wz * wz) - b.TrackVel.z * (2f * wz) );
						b.TrackVel += acc * h;
						b.TrackPos += b.TrackVel * h;
					}
				}
				b.PosOffset = b.TrackPos - raw;
				b.OffVel = b.TrackVel - (b.Phys.IsValid() ? b.Phys.Velocity : Vector3.Zero); // (carried on if the tracker lets go of it)
				b.RotOffset = Rotation.Slerp( Rotation.Identity, b.RotOffset, k );
				continue;
			}
			b.Tracking = false;
			if ( b.PosOffset.IsNearZeroLength && b.OffVel.IsNearZeroLength && b.RotOffset == Rotation.Identity )
				continue;
			if ( k <= 0f )
			{
				b.PosOffset = 0f; // smoothing off: show the raw pop
				b.OffVel = 0f;
			}
			else
			{
				// A critically damped spring to zero (not an exponential): a fold adds to the offset but not to its velocity, so the
				// drawn speed is continuous and the catch-up eases in - an exponential fade started at its fastest, a one-frame
				// lurch of offset x rate on every correction and switch (user, 2026-11-06: blips at the switchover). Stiffness ~1.5x the
				// old rate settles in about the same time; the gentler SwitchSpring right after a source switch.
				float w = RealTime.Now < b.SoftFadeUntil ? (b.SoftSpring > 0f ? b.SoftSpring : SwitchStiffness()) : 1.5f * (b.IsRemoteDriven ? RemoteSmoothRate : SmoothRate);
				int steps = (int)(Time.Delta / 0.004f) + 1;
				float h = Time.Delta / steps;
				for ( int i = 0; i < steps; i++ )
				{
					var acc = -b.PosOffset * (w * w) - b.OffVel * (2f * w);
					b.OffVel += acc * h;
					b.PosOffset += b.OffVel * h;
				}
			}
			b.RotOffset = Rotation.Slerp( Rotation.Identity, b.RotOffset, k );
			if ( b.PosOffset.Length < 0.001f && b.OffVel.Length < 0.01f )
			{
				b.PosOffset = 0f;
				b.OffVel = 0f;
			}
		}
	}

	// ── Debug draw ──────────────────────────────────────────────────────────────────────────────────────────

	[ConVar( "mimi_sim_draw", Help = "Sim: draw every body's bounds at its visual pose (green asleep, yellow awake)." )]
	public static bool DrawBodies { get; set; }

	void Draw()
	{
		if ( !DrawBodies )
			return;
		var overlay = Scene.GetSystem<DebugOverlaySystem>();
		if ( overlay is null )
			return;
		foreach ( var b in _bodies )
		{
			if ( b.Phys is null || !b.Phys.IsValid() )
				continue;
			var bounds = b.Phys.GetBounds();
			var vis = VisualPose( b );
			var shifted = bounds.Translate( vis.Position - b.Phys.Position );
			overlay.Box( shifted, b.Phys.Sleeping ? Color.Green.WithAlpha( 0.6f ) : Color.Yellow, 0f, default, false );
		}
	}
}
