using System;
using System.Collections.Generic;
using System.Linq;
using Mimiclay.Sim;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// DEBUG RIG for the shared simulation world (<see cref="SimWorld"/>): a group of loose boxes and one walking "pawn" box
/// per connection, all simulated there — host-authoritative, clients predicting and rolling back. It was the standalone
/// prototype of that service (2026-11-04, measured in the networked-physics-authority memory); it now only REGISTERS
/// bodies, drives the pawns and draws them. Nothing else in the game touches it.
///
/// Use (host, in play): <c>mimi_rb_spawn</c> (in front of the camera), <c>mimi_rb_clear</c>. Move your pawn with
/// I/J/K/L (camera-relative), or <c>mimi_rb_script 1</c> for a scripted walk. Lag/stats are the service's:
/// <c>mimi_sim_fakelag</c>, <c>mimi_sim_report</c>.
/// </summary>
public sealed class RollbackRig : Component
{
	const int BoxCount = 12;
	const float PawnHalf = 16f, BoxHalf = 12f, PawnMass = 60f, BoxMass = 15f;
	const float WalkSpeed = 220f;
	public const byte DriverId = 1;

	[ConVar( "mimi_rb_script", Help = "Rollback rig: 1 = this machine's pawn walks a scripted pattern instead of I/J/K/L." )]
	public static int ScriptedInput { get; set; } = 0;

	public static RollbackRig Current { get; private set; }

	readonly Dictionary<Guid, GameObject> _visuals = new();
	readonly Dictionary<Guid, Guid> _pawnKeys = new(); // host: connection → pawn key
	readonly HashSet<Guid> _mine = new(); // host: every key this rig registered (only these are removed with it)
	Model _boxModel;
	SimWorld _sim;

	static Guid LocalId => Connection.Local?.Id ?? Guid.Empty;

	// Keys every machine derives the same: the rig's id mixed with a salt (a box index, a connection id).
	Guid KeyFor( Guid salt, int index )
	{
		var a = GameObject.Id.ToByteArray();
		var b = salt.ToByteArray();
		for ( int i = 0; i < 16; i++ ) a[i] ^= b[i];
		a[0] ^= (byte)index; a[1] ^= (byte)(index >> 8); a[15] ^= 0xA5;
		return new Guid( a );
	}

	protected override void OnStart()
	{
		Current = this;
		_boxModel = Model.Load( "models/dev/box.vmdl" );
		_sim = Scene.GetSystem<SimWorld>();
		_sim.RegisterDriver( DriverId, new WalkDriver() );
		_sim.LocalInput = ReadLocalWish;
		_sim.BodyAdded += OnBodyAdded;
		_sim.BodyRemoved += OnBodyRemoved;
		foreach ( var b in _sim.Bodies )
			OnBodyAdded( b );

		if ( _sim.IsAuthority )
		{
			for ( int i = 0; i < BoxCount; i++ )
			{
				int col = i / 3, level = i % 3;
				// 1u clear of the floor: the map floor is a zero-thickness triangle mesh, and a box spawned 0.25u above it
				// started inside its contact skin and fell straight through (the whole stack with it).
				var at = WorldPosition + new Vector3( 120 + (col % 2) * 26f, (col / 2) * 26f - 13f + (level % 2) * 3f, BoxHalf + 1f + level * (BoxHalf * 2 + 1f) );
				_mine.Add( KeyFor( Guid.Empty, i ) );
				_sim.Register( new SimBodySpec
				{
					Key = KeyFor( Guid.Empty, i ), Shape = SimShapeKind.Box, HalfExtents = new Vector3( BoxHalf ), Mass = BoxMass,
					Pose = new Transform( at, Rotation.Identity ),
				} );
			}
		}
	}

	protected override void OnDestroy()
	{
		if ( Current == this )
			Current = null;
		if ( _sim is not null )
		{
			_sim.BodyAdded -= OnBodyAdded;
			_sim.BodyRemoved -= OnBodyRemoved;
			if ( _sim.LocalInput == ReadLocalWish )
				_sim.LocalInput = null;
			if ( _sim.IsAuthority )
				foreach ( var key in _mine )
					_sim.Unregister( key );
		}
		foreach ( var v in _visuals.Values )
			v.Destroy();
		_visuals.Clear();
	}

	// Host: one pawn per connection.
	void SyncPawns()
	{
		var owners = Networking.IsActive ? Connection.All.Where( c => c.IsActive ).Select( c => c.Id ).ToList() : new List<Guid> { LocalId };
		foreach ( var gone in _pawnKeys.Keys.Where( o => !owners.Contains( o ) ).ToList() )
		{
			_sim.Unregister( _pawnKeys[gone] );
			_pawnKeys.Remove( gone );
		}
		for ( int i = 0; i < owners.Count; i++ )
		{
			if ( _pawnKeys.ContainsKey( owners[i] ) )
				continue;
			var key = KeyFor( owners[i], 1000 );
			_pawnKeys[owners[i]] = key;
			_mine.Add( key );
			_sim.Register( new SimBodySpec
			{
				Key = key, Owner = owners[i], Driver = DriverId, Shape = SimShapeKind.Box, HalfExtents = new Vector3( PawnHalf ),
				Mass = PawnMass, Flags = SimBodyFlags.Upright,
				Pose = new Transform( WorldPosition + new Vector3( -150, (_pawnKeys.Count - 1) * 60f - 30f, PawnHalf + 0.5f ), Rotation.Identity ),
			} );
		}
	}

	void OnBodyAdded( SimBody b )
	{
		if ( b.Spec.Shape != SimShapeKind.Box || _visuals.ContainsKey( b.Key ) )
			return;
		bool pawn = b.Spec.Owner != Guid.Empty;
		var go = new GameObject( true, pawn ? "rb pawn" : "rb box" );
		go.NetworkMode = NetworkMode.Never;
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		var mr = go.Components.Create<ModelRenderer>();
		mr.Model = _boxModel;
		var size = _boxModel?.Bounds.Size ?? new Vector3( 50 );
		go.WorldScale = b.Spec.HalfExtents * 2f / size;
		mr.Tint = !pawn ? new Color( 0.85f, 0.7f, 0.45f ) : b.IsLocalOwned ? new Color( 0.3f, 0.9f, 0.4f ) : new Color( 0.4f, 0.55f, 1f );
		_visuals[b.Key] = go;
	}

	void OnBodyRemoved( SimBody b )
	{
		if ( _visuals.Remove( b.Key, out var go ) )
			go.Destroy();
	}

	protected override void OnUpdate()
	{
		if ( _sim is null )
			return;
		if ( _sim.IsAuthority )
			SyncPawns();
		foreach ( var b in _sim.Bodies )
		{
			if ( !_visuals.TryGetValue( b.Key, out var go ) )
				continue;
			var t = _sim.VisualPose( b );
			go.WorldPosition = t.Position;
			go.WorldRotation = t.Rotation;
		}
	}

	SimInput ReadLocalWish( int tick )
	{
		var pawn = _sim.Bodies.FirstOrDefault( b => b.IsLocalOwned && b.Spec.Driver == DriverId );
		if ( ScriptedInput != 0 )
		{
			// 40-tick segments in hashed directions, pulled back toward the boxes when it wanders off.
			if ( pawn is not null )
			{
				var toCentre = (WorldPosition + new Vector3( 130, 0, 0 ) - pawn.Phys.Position).WithZ( 0f );
				if ( toCentre.Length > 260f )
					return new SimInput { Wish = (Vector2)(toCentre.Normal * WalkSpeed) };
			}
			uint h = (uint)(tick / 40) * 0x9E3779B1u ^ (uint)LocalId.GetHashCode() * 0x85EBCA77u;
			h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
			if ( h % 5 == 0 )
				return SimInput.None;
			return new SimInput { Wish = (Vector2)(Rotation.FromYaw( (h >> 8) % 360 ).Forward * WalkSpeed) };
		}

		float f = (Input.Keyboard.Down( "i" ) ? 1f : 0f) - (Input.Keyboard.Down( "k" ) ? 1f : 0f);
		float r = (Input.Keyboard.Down( "l" ) ? 1f : 0f) - (Input.Keyboard.Down( "j" ) ? 1f : 0f);
		if ( f == 0f && r == 0f )
			return SimInput.None;
		var yaw = Rotation.FromYaw( Scene.Camera.IsValid() ? Scene.Camera.WorldRotation.Yaw() : 0f );
		return new SimInput { Wish = (Vector2)((yaw.Forward * f + yaw.Right * r).Normal * WalkSpeed) };
	}

	/// <summary>The walk, cut down: chase the wish horizontally with a capped acceleration. Other people's pawns are
	/// guessed at their last known velocity (measured better than their newest input — see the memory).</summary>
	sealed class WalkDriver : ISimDriver
	{
		public void Drive( SimBody body, in SimInput input, int tick )
		{
			var b = body.Phys;
			var wish = new Vector3( input.Wish.x, input.Wish.y, 0f );
			var v = b.Velocity;
			var flat = v.WithZ( 0f );
			if ( wish.IsNearZeroLength && flat.Length < 0.5f )
				return;
			if ( b.Sleeping )
				b.Sleeping = false;
			var dv = ((wish - flat) * (1f - MathF.Exp( -12f * SimWorld.Dt ))).ClampLength( 3000f * SimWorld.Dt );
			b.Velocity = (flat + dv).WithZ( v.z );
		}

		public SimInput Guess( SimBody body, in SimBodyState latest ) => new() { Wish = new Vector2( latest.V.x, latest.V.y ) };
	}

	// ── Commands ────────────────────────────────────────────────────────────────────────────────────────────

	[ConCmd( "mimi_rb_spawn" )]
	public static void SpawnCmd()
	{
		var scene = Game.ActiveScene;
		if ( scene is null || (Networking.IsActive && !Networking.IsHost) )
		{
			Log.Info( "mimi_rb_spawn: host only, in play" );
			return;
		}
		ClearCmd();
		var cam = scene.Camera;
		var from = cam.IsValid() ? cam.WorldPosition + cam.WorldRotation.Forward.WithZ( 0f ).Normal * 250f : Vector3.Zero;
		var tr = scene.Trace.Ray( from + Vector3.Up * 50f, from - Vector3.Up * 2000f ).Run();
		var at = tr.Hit ? tr.HitPosition : from;
		var go = new GameObject( true, "RollbackRig" );
		go.WorldPosition = at;
		go.Components.Create<RollbackRig>();
		if ( Networking.IsActive )
			go.NetworkSpawn();
		Log.Info( $"mimi_rb_spawn: rig at {at}" );
	}

	[ConCmd( "mimi_rb_clear" )]
	public static void ClearCmd()
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
			return;
		foreach ( var go in scene.GetAllObjects( true ).Where( g => g.Name == "RollbackRig" ).ToList() )
			go.Destroy();
	}
}
