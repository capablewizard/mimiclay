using System;

namespace Mimiclay;

// Prop locomotion — shared pieces: sizes and speeds, what counts as ground, the move hull (see PropMoveHull) and its
// sweeps, input, turning, launches. The walking itself is in HiderController.Walk (a floating physics body); climbing in
// HiderController.Climb. (Until 2026-10-30 props walked as a KEYFRAMED body swept by hand — the "swept mover" — with a
// press mode for aligning to walls and a buried mode for escaping overlaps; it didn't get on with other rigid bodies and
// was replaced. See MOVEMENT-REWORK-PLAN.md.)
public sealed partial class HiderController
{
	/// <summary>Tallest ledge the prop walks up without jumping (the bedroom blockout's step is 18) — the floating collision
	/// shape's clearance (see HiderController.Walk), capped per prop by StepHeightFraction.</summary>
	[Property, Group( "Movement" )] public float StepHeight { get; set; } = 18f;

	/// <summary>A prop steps up at most this fraction of its OWN height (user, 2026-10-29: a small prop was popping onto
	/// things far taller than itself). StepHeight is the cap for big props.</summary>
	[Property, Group( "Movement" ), Range( 0f, 1f )] public float StepHeightFraction { get; set; } = 0.5f;

	/// <summary>Props smaller than this (u — the cube root of the move hull's width × depth × height) move slower (user,
	/// 2026-10-29: "small props zoom around way too fast"). Speed scales with the square root of size / this, so it falls
	/// off gently; bigger props keep full speed.</summary>
	[Property, Group( "Movement" )] public float SpeedReferenceSize { get; set; } = 64f;

	/// <summary>The slowest a tiny prop gets, as a fraction of full speed.</summary>
	[Property, Group( "Movement" ), Range( 0f, 1f )] public float MinSpeedScale { get; set; } = 0.35f;

	// Walk, crouch and climb speeds are scaled by this (see SpeedReferenceSize).
	float SizeSpeedScale
	{
		get
		{
			if ( !_hull.Ready || SpeedReferenceSize <= 0f )
				return 1f;
			var s = _hull.LocalBounds.Size;
			float size = MathF.Cbrt( MathF.Max( s.x * s.y * s.z, 0f ) );
			return Math.Clamp( MathF.Sqrt( size / SpeedReferenceSize ), MinSpeedScale, 1f );
		}
	}

	// This prop's step: `max` (StepHeight), but never more than StepHeightFraction of its height.
	float ScaledStep( float max ) => _hull.Ready ? MathF.Min( max, _hull.LocalBounds.Size.z * StepHeightFraction ) : max;

	/// <summary>Steepest surface (degrees from flat) the prop can stand on. Steeper surfaces are walls: the prop
	/// slides down them.</summary>
	[Property, Group( "Movement" ), Range( 0f, 89f )] public float WalkAngle { get; set; } = 50f;

	/// <summary>Clay props (pots, pans, released props) act like FLOOR, not walls: any of their surfaces that faces up at all
	/// can be stood on, and you never stick to them — unless they're bigger than you (see <see cref="StickToBiggerProps"/>).
	/// Off: they collide like any other surface.</summary>
	[Property, Group( "Movement" )] public bool PropsAsFloor { get; set; } = true;

	/// <summary>Clay props BIGGER than you are surfaces like any other — you can climb them and stick to them; smaller ones
	/// stay floor (user, 2026-10-30). Size = cube root of the bounding volume.</summary>
	[Property, Group( "Movement" )] public bool StickToBiggerProps { get; set; } = true;

	/// <summary>How much bigger than you a clay prop must be to stick to (1 = any prop bigger than you).</summary>
	[Property, Group( "Movement" ), Range( 0.5f, 4f )] public float StickPropSizeRatio { get; set; } = 1f;

	static float SizeOf( Vector3 size ) => MathF.Cbrt( MathF.Max( size.x * size.y * size.z, 0f ) );

	// A floor-like prop surface counts as ground if it faces up at all past this (≈76° from flat).
	const float PropFloorMinZ = 0.25f;

	/// <summary>Draw the move hull (green grounded, yellow airborne, blue parked) and the ground normal.</summary>
	[Property, Group( "Debug" )] public bool DebugMovement { get; set; }

	/// <summary>The prop's velocity (u/s) as last set by its movement.</summary>
	public Vector3 Velocity => _velocity;

	/// <summary>True while parked: grounded at rest, left to physics (and its sleep) until something moves it.</summary>
	public bool IsParked => _parked;

	// Gap kept between the hull and a surface it's placed against (a mantle), so the next sweep never starts in contact
	// (a sweep that starts touching a convex body can pass straight through it).
	const float Skin = 0.25f;
	const int ParkAfterTicks = 6;

	// Tags the move hull looks straight through: fellow LIVE prop bodies (Collision.config ignores the pair),
	// the hunter's trace-only head trigger, triggers and water, and the charades stage fence (an invisible
	// crowd ring props ignore). Hunters' movement capsules ("movecollider") are NOT here: they block props.
	static readonly string[] MoveIgnoreTags =
	{
		PropBodyTag, "headcollider", "trigger", "water", CharadesStageFence.WallTag,
	};

	readonly PropMoveHull _hull = new();
	Vector3 _velocity;
	Vector3 _groundNormal = Vector3.Up;
	GameObject _groundObject;
	bool _parked;
	int _stillTicks;

	float WalkCos => MathF.Cos( WalkAngle.DegreeToRadian() );
	bool Walkable( Vector3 normal ) => normal.z >= WalkCos;

	// Walkable for THIS hit: ordinary ground, or any upward-facing surface of a floor-like clay prop.
	bool WalkableHit( in SceneTraceResult tr ) => Walkable( tr.Normal ) || (tr.Normal.z > PropFloorMinZ && IsFloorProp( tr ));

	// Any clay prop — a sculpture in the map or a released prop pawn — under PropsAsFloor (see there).
	bool IsFloorProp( in SceneTraceResult tr )
	{
		if ( !PropsAsFloor || !tr.Hit )
			return false;
		var go = tr.GameObject;
		bool clay = false;
		for ( var g = go; g.IsValid() && !clay; g = g.Parent )
			clay = g.Tags.Has( SdfCollider.ClayTag ) || g.Components.Get<SdfSculpture>().IsValid() || g.Components.Get<HiderController>().IsValid();
		if ( !clay )
			return false;

		// Bigger than us: a surface like any other (StickToBiggerProps).
		if ( StickToBiggerProps && _hull.Ready && tr.Body is { } b && b.IsValid()
			&& SizeOf( b.GetBounds().Size ) > SizeOf( _hull.LocalBounds.Size ) * StickPropSizeRatio )
			return false;
		return true;
	}

	SceneTrace MoveTrace => Scene.Trace.IgnoreGameObjectHierarchy( GameObject ).WithoutTags( MoveIgnoreTags );

	SceneTraceResult HullSweep( Vector3 from, Rotation rot, Vector3 to )
		=> _hull.Sweep( MoveTrace, new Transform( from, rot ), new Transform( to, rot ) );

	bool PoseBlocked( Vector3 pos, Rotation rot )
		=> _hull.Overlaps( Scene, MoveTrace, GameObject, MoveIgnoreTags, new Transform( pos, rot ) );

	/// <summary>Debug: log any fixed tick where this prop moved much further than its own speed allows — the
	/// "fired through a wall" hunt. Logs the mode before and after and the distance.</summary>
	[ConVar( "mimi_dbg_move_jumps", Help = "Debug: log prop ticks that move much further than their speed allows." )]
	public static bool DebugMoveJumps { get; set; }

	Vector3 _jumpWatchPos;
	PhysicsMode _jumpWatchMode;
	bool _jumpWatchSeeded;

	// Called at the top of every simulated fixed tick: how far did the LAST tick (and its physics step) carry us?
	void WatchMoveJumps()
	{
		if ( !DebugMoveJumps )
		{
			_jumpWatchSeeded = false;
			return;
		}

		var pos = WorldPosition;
		if ( _jumpWatchSeeded )
		{
			float moved = (pos - _jumpWatchPos).Length;
			float allowed = MathF.Max( WalkSpeed, MathF.Abs( _velocity.z ) + 50f ) * Time.Delta * 1.5f + StepHeight + 2f;
			if ( moved > allowed )
				Log.Warning( $"[move jump] {GameObject.Name}: moved {moved:0.0}u in one tick (allowed {allowed:0.0}) " +
					$"{_jumpWatchMode} -> {_physicsMode}, from {_jumpWatchPos} to {pos}, body vel {(Body.IsValid() ? Body.Velocity.Length : 0f):0}, " +
					$"climbing={_climbing} grounded={IsGrounded}" );
		}
		_jumpWatchPos = pos;
		_jumpWatchMode = _physicsMode;
		_jumpWatchSeeded = true;
	}

	/// <summary>Launch the prop (jump pad). Simulating machine only — a proxy's copy is driven by the network.</summary>
	public void Launch( Vector3 velocity )
	{
		if ( IsProxy || !Body.IsValid() )
			return;

		if ( _physicsMode is PhysicsMode.Loose or PhysicsMode.Held )
		{
			Body.Velocity = velocity;
			return;
		}

		EndClimb(); // a launch tears it off the wall (the next tick walks again, at this velocity)

		_velocity = velocity;
		IsGrounded = false;
		_groundObject = null;
		_sinceWalkJump = 0f; // the ground spring mustn't catch us straight back
		if ( Body.MotionEnabled )
			Body.Velocity = velocity;
		Unpark();
	}

	void Unpark()
	{
		_parked = false;
		_stillTicks = 0;
	}

	// Called by mode switches and teleports: forget motion and ground, re-settle from scratch.
	void ResetMover()
	{
		_velocity = Vector3.Zero;
		_groundObject = null;
		IsGrounded = false;
		Unpark();
		_climbing = false;
		if ( Body.IsValid() && Body.MotionEnabled )
		{
			Body.Velocity = Vector3.Zero;
			Body.AngularVelocity = Vector3.Zero;
		}
	}

	Vector3 ReadWish()
	{
		float speed = (IsCrouching ? CrouchSpeed : WalkSpeed) * SizeSpeedScale; // no sprint for props — crouch is the fine control; small props are slower

		// CLAMP, don't normalize — exactly what the stock controller does: raw AnalogMove is per-axis, so two
		// keys give length √2; clamping caps it at 1 while a part-deflected stick can still ask for less.
		var move = Input.AnalogMove.ClampLength( 1f );
		var wish = Rotation.FromYaw( MoveYaw ) * move * speed;

		// Benchmark route driver (PerfScenario.mimi_perf_walk): a world-space wish replaces input.
		if ( PerfScenario.DriveProp( GameObject, speed ) is Vector3 perfWish )
			wish = perfWish;

		return wish;
	}

	// How far the mouse turned the facing this tick (degrees). Deltas only — there is no absolute facing target,
	// so a blocked turn simply doesn't happen and nothing tries to force it later.
	float ReadTurn()
	{
		if ( !_turnSeeded )
		{
			_prevFacingYaw = FacingYaw;
			_turnSeeded = true;
			return 0f;
		}

		float delta = MathX.DeltaDegrees( _prevFacingYaw, FacingYaw );
		_prevFacingYaw = FacingYaw;
		return delta;
	}

	// Turn about the world vertical through the hull's centre by up to `degrees`, as far as the pose stays clear.
	// World vertical, not the root's own up: a prop lying on its side still spins flat on the floor.
	Rotation Turn( ref Vector3 pos, Rotation rot, float degrees )
	{
		var start = pos;
		var centre = start + rot * _hull.Centre;
		var centreXY = new Vector3( centre.x, centre.y, start.z );

		(Vector3, Rotation) At( float deg )
		{
			var spin = Rotation.FromAxis( Vector3.Up, deg );
			return (centreXY + spin * (start - centreXY), spin * rot);
		}

		var (fullPos, fullRot) = At( degrees );
		if ( !PoseBlocked( fullPos, fullRot ) )
		{
			pos = fullPos;
			return fullRot;
		}

		// Blocked: bisect for the largest clear fraction. Small turns per tick, so a few halvings is plenty.
		float lo = 0f, hi = 1f;
		for ( int i = 0; i < 4; i++ )
		{
			float mid = (lo + hi) * 0.5f;
			var (p, r) = At( degrees * mid );
			if ( PoseBlocked( p, r ) )
				hi = mid;
			else
				lo = mid;
		}
		if ( lo <= 0f )
			return rot;

		var (okPos, okRot) = At( degrees * lo );
		pos = okPos;
		return okRot;
	}

	// What we're standing on, when it can move (a loose or held prop, a platform) — null on the static world.
	void SetGround( in SceneTraceResult tr )
		=> _groundObject = tr.Body is { } b && b.IsValid() && b.BodyType != PhysicsBodyType.Static ? tr.GameObject : null;

	void DrawMovement()
	{
		if ( !_hull.Ready )
			return;

		var color = _parked ? Color.Cyan : IsGrounded ? Color.Green : Color.Yellow;
		_hull.Draw( Scene, WorldTransform, color );
		if ( IsGrounded )
			Scene.DebugOverlay.Line( WorldPosition, WorldPosition + _groundNormal * 24f, Color.Magenta, 0f );
	}
}
