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
	public const float Dt = 1f / 50f;           // project TickRate 50 — the sim keeps its own clock (see RunTicks)
	public const int SnapshotEvery = 2;         // 25 Hz
	const int InputRedundancy = 8;
	const int HistoryTicks = 256;
	const int RestResendTicks = 25;             // keep sending a newly asleep body this long (its reliable rest may still be in flight)
	const int StaticRefreshTicks = 50;          // re-check the scene's static collision once a second
	const int ShapeRefreshTicks = 10;           // re-check FromGameObject sources for collider rebuilds
	const int MaxTicksPerFrame = 5;
	const int TargetSlack = 2;

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
	public static int RemoteDelayTicks { get; set; } = 6;

	// A rotation error counts as the distance it moves a point this far out (u per degree: 20u arm). Quantized snapshot
	// rotations are ~0.08° off (0.03u), under SkipEpsilon; a wrong facing used to go unnoticed entirely.
	const float RotErrorArm = 20f * MathF.PI / 180f;

	[ConVar( "mimi_sim_smooth", Help = "Sim: how fast corrections fade from the visuals (1/s). 0 = show the raw pop." )]
	public static float SmoothRate { get; set; } = 12f;

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

	public event Action<SimBody> BodyAdded;
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
		var body = new SimBody { Spec = spec, Id = id, World = this };
		_drivers.TryGetValue( spec.Driver, out body.Driver );

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
		if ( body.ShapesReady && body.Spec.Driver == SimHoldDriver.Id )
			foreach ( var s in p.Shapes )
				s.Friction = 0f; // a held body slides rather than grips (HeldBodyDriver.HeldFriction)
		// Nothing to collide with yet (a client still building the source's collider): hold it still until it has.
		p.MotionEnabled = body.ShapesReady && (body.Spec.Flags & SimBodyFlags.Anchored) == 0;
		// Copied props can be small and fast (a knocked knob): without CCD they tunnelled through the zero-thickness mesh floor.
		// Walkers too (the scene walk's body has it on): a small prop's hull falling at MaxFallSpeed moves 60u a tick, and
		// the foot sweep only looks a step and a snap below it.
		p.EnhancedCcd = body.Spec.Shape != SimShapeKind.Box;
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
	static int CopyShapes( PhysicsBody from, PhysicsBody to, bool solidOnly = true )
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

	void RefreshStatics()
	{
		if ( _world is null )
			return;
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
	bool ShownFromAuthority( SimBody b )
		=> !IsAuthority && b.Authority.Count > 0 && (b.IsRemoteDriven || (b.Driver is null && b.Spec.Owner == Guid.Empty && !b.LocalTouched));

	// Every frame: switch a body's drawn source when its situation changed, folding the difference between the two into
	// the fading correction so nothing jumps. A touched body that has come to rest goes back to the host's poses.
	void UpdatePresentationSource()
	{
		if ( IsAuthority )
			return;
		foreach ( var b in _bodies )
		{
			if ( b.LocalTouched && b.Phys.IsValid() && b.Phys.Sleeping && b.Driver is null )
				b.LocalTouched = false;
			bool auth = ShownFromAuthority( b );
			if ( auth == b.ShownFromAuth )
				continue;
			var before = PoseFrom( b, b.ShownFromAuth );
			var after = PoseFrom( b, auth );
			b.ShownFromAuth = auth;
			var dp = before.Position - after.Position;
			if ( dp.Length > 80f )
			{
				b.PosOffset = Vector3.Zero; // too far to slide: show it where it is
				b.RotOffset = Rotation.Identity;
				continue;
			}
			b.PosOffset += dp;
			b.RotOffset = before.Rotation * after.Rotation.Inverse * b.RotOffset;
			SimStats.SourceSwitches++;
		}
	}

	/// <summary>Client: something of ours is touching this body (see <see cref="ShownFromAuthority"/>).</summary>
	internal void NoteLocalTouch( SimBody b )
	{
		if ( b is not null && !IsAuthority )
			b.LocalTouched = true;
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
		if ( IsAuthority || b.IsLocalOwned )
			return;
		var list = b.Authority;
		if ( list.Count > 0 && tick <= list[^1].tick )
			return;
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
		float k = SmoothRate > 0f ? MathF.Exp( -SmoothRate * Time.Delta ) : 0f;
		foreach ( var b in _bodies )
		{
			if ( b.PosOffset.IsNearZeroLength && b.RotOffset == Rotation.Identity )
				continue;
			b.PosOffset *= k;
			b.RotOffset = Rotation.Slerp( Rotation.Identity, b.RotOffset, k );
			if ( b.PosOffset.Length < 0.001f )
				b.PosOffset = 0f;
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
