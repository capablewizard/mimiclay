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

	[ConVar( "mimi_sim_skip_eps", Help = "Sim: replay only when a snapshot disagrees with the prediction by more than this (u)." )]
	public static float SkipEpsilon { get; set; } = 0.05f;

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
		var body = new SimBody { Spec = spec, Id = id };
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
		_byId[id] = body;
		_bodies.Add( body );
		_maxId = Math.Max( _maxId, id );
		if ( spec.Shape == SimShapeKind.FromGameObject )
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
		if ( b.Spec.Shape == SimShapeKind.FromGameObject )
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
		}

		if ( body.ShapesReady && body.Spec.Mass > 0f )
			p.Mass = body.Spec.Mass;
		// Nothing to collide with yet (a client still building the source's collider): hold it still until it has.
		p.MotionEnabled = body.ShapesReady && (body.Spec.Flags & SimBodyFlags.Anchored) == 0;
		// Copied props can be small and fast (a knocked knob): without CCD they tunnelled through the zero-thickness mesh floor.
		p.EnhancedCcd = body.Spec.Shape == SimShapeKind.FromGameObject;
	}

	// The scene body a FromGameObject spec copies, and the collider version it's at (SdfCollider.BuildSerial; 0 otherwise).
	PhysicsBody SourceBody( Guid source, out int version )
	{
		version = 0;
		var go = Scene.Directory.FindByGuid( source );
		if ( !go.IsValid() )
			return null;
		var sdf = go.Components.Get<SdfCollider>();
		if ( sdf.IsValid() )
			version = sdf.BuildSerial;
		foreach ( var b in Scene.PhysicsWorld.Bodies )
			if ( b.IsValid() && b.ShapeCount > 0 && b.GameObject == go )
				return b;
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
		if ( n > 0 && to.BodyType == PhysicsBodyType.Dynamic )
			to.RebuildMass();
		return n;
	}

	// Every ShapeRefreshTicks: FromGameObject bodies whose source collider rebuilt (a sculpt, a crater) get new shapes,
	// and ones still waiting for theirs try again. State is kept.
	void RefreshShapes()
	{
		foreach ( var b in _bodies )
		{
			if ( b.Spec.Shape != SimShapeKind.FromGameObject )
				continue;
			var go = Scene.Directory.FindByGuid( b.Spec.Source );
			int version = go.IsValid() && go.Components.Get<SdfCollider>() is { } sdf && sdf.IsValid() ? sdf.BuildSerial : 0;
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
		}
		foreach ( var gone in _staticCopies.Keys.Where( k => !seen.Contains( k ) ).ToList() )
		{
			_staticCopies[gone].copy.Remove();
			_staticCopies.Remove( gone );
		}
	}

	// ── Presentation ────────────────────────────────────────────────────────────────────────────────────────

	RealTimeSince _sinceTick;

	/// <summary>Where to SHOW a body this frame: between its last two ticks, plus the fading correction.</summary>
	public Transform VisualPose( SimBody b )
	{
		float a = Math.Clamp( _sinceTick / Dt, 0f, 1f );
		var pos = Vector3.Lerp( b.Prev.Position, b.Cur.Position, a ) + b.PosOffset;
		var rot = b.RotOffset * Rotation.Slerp( b.Prev.Rotation, b.Cur.Rotation, a );
		return new Transform( pos, rot );
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
