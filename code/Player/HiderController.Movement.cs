using System;

namespace Mimiclay;

// Prop locomotion: a KEYFRAMED body moved by sweeping one convex shape — the move hull (see PropMoveHull).
//
// The pawn's Rigidbody has motion disabled in Character mode; the simulating machine (owner, or the host for
// bots and released scenery) works out the next pose here and hands it to PhysicsBody.Move, whose controller
// sweep carries the real SDF collider there with a proper velocity — hunters and loose props ride it and are
// pushed by it, but nothing ever pushes IT. That is the whole drift fix: no solver contact can move a prop,
// so a prop with no input parks and stays exactly where it is.
//
// One tick: read the wish → turn (pose-tested) → integrate our own velocity → collide-and-slide the hull,
// stepping up ledges up to StepHeight → snap down onto walkable ground, or fall → hand the pose to physics.
// A grounded prop with no input, no speed and no turn PARKS after a few ticks: no sweeps at all until input,
// a new shape, its ground moving, a launch or a teleport wakes it.
public sealed partial class HiderController
{
	/// <summary>Tallest ledge the prop walks up without jumping (the bedroom blockout's step is 18).</summary>
	[Property, Group( "Movement" )] public float StepHeight { get; set; } = 18f;

	/// <summary>Steepest surface (degrees from flat) the prop can stand on. Steeper surfaces are walls: the prop
	/// slides down them.</summary>
	[Property, Group( "Movement" ), Range( 0f, 89f )] public float WalkAngle { get; set; } = 50f;

	/// <summary>Below this speed (u/s), a grounded prop with no input stops dead.</summary>
	[Property, Group( "Movement" )] public float StopSpeed { get; set; } = 6f;

	/// <summary>Draw the move hull (green grounded, yellow airborne, blue parked) and the ground normal.</summary>
	[Property, Group( "Debug" )] public bool DebugMovement { get; set; }

	/// <summary>Our own velocity (u/s) — the body is keyframed, so this is the only velocity there is.</summary>
	public Vector3 Velocity => _velocity;

	/// <summary>True while parked: grounded at rest, costing nothing per tick.</summary>
	public bool IsParked => _parked;

	// Gap kept between the hull and anything it stops against, so the next sweep never starts in contact
	// (a sweep that starts touching a convex body can pass straight through it).
	const float Skin = 0.25f;
	const int SlideIterations = 4;
	const int ParkAfterTicks = 6;
	const float MaxUnstick = 32f;

	// Tags the move hull looks straight through: fellow LIVE prop bodies (Collision.config ignores the pair),
	// the hunter's trace-only head trigger, triggers and water, and the charades stage fence (an invisible
	// crowd ring props ignore). Hunters' movement capsules ("movecollider") are NOT here: they block props.
	static readonly string[] MoveIgnoreTags =
	{
		PropBodyTag, "headcollider", "trigger", "water", CharadesStageFence.WallTag,
	};

	readonly PropMoveHull _hull = new();
	int _hullVersion = -1;
	Vector3 _velocity;
	Vector3 _groundNormal = Vector3.Up;
	GameObject _groundObject;
	Transform _groundLast;       // the ground object's world transform last tick (platform carry)
	bool _parked;
	int _stillTicks;
	bool _launched;              // an external launch landed this tick (jump pad)
	Vector3 _lastPose;           // where this tick left us — a different position next tick is a teleport

	float WalkCos => MathF.Cos( WalkAngle.DegreeToRadian() );
	bool Walkable( Vector3 normal ) => normal.z >= WalkCos;

	SceneTrace MoveTrace => Scene.Trace.IgnoreGameObjectHierarchy( GameObject ).WithoutTags( MoveIgnoreTags );

	SceneTraceResult HullSweep( Vector3 from, Rotation rot, Vector3 to )
		=> _hull.Sweep( MoveTrace, new Transform( from, rot ), new Transform( to, rot ) );

	bool PoseBlocked( Vector3 pos, Rotation rot )
		=> _hull.Overlaps( Scene, MoveTrace, GameObject, MoveIgnoreTags, new Transform( pos, rot ) );

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

		EndClimb(); // a launch tears it off the wall
		EndPress();

		_velocity = velocity;
		IsGrounded = false;
		_launched = true;
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
		_pushPerKg = 0f;
		_pressing = false; // OnFixedUpdate puts the body back to keyframed on its next Character tick
		_climbing = false;
	}

	/// <summary>One fixed tick of prop locomotion. <paramref name="controlled"/> = read input; otherwise the prop
	/// still falls, settles and parks (edit mode, round locks, released scenery, bots).</summary>
	void StepMover( bool controlled )
	{
		_hull.Refresh( _collider, Scene.PhysicsWorld );
		if ( !_hull.Ready )
		{
			IsGrounded = false;
			return; // no shape to move yet — hold still rather than fall through the world
		}

		var pos = WorldPosition;
		var rot = WorldRotation; // whatever the last press left it at (see HiderController.Press)

		// Woken by anything that changed under us: a new shape (it may now overlap or float), a teleport.
		if ( _hull.Version != _hullVersion )
		{
			_hullVersion = _hull.Version;
			Unpark();
			if ( PoseBlocked( pos, rot ) )
				TryUnstick( ref pos, rot );
		}
		if ( (pos - _lastPose).LengthSquared > 0.01f )
			Unpark();

		CarryWithGround( ref pos, ref rot );

		var wish = controlled ? ReadWish() : Vector3.Zero;
		wish = ApplyLeash( wish, pullIn: false );
		bool jump = controlled && _jumpQueued && IsGrounded;
		_jumpQueued = false;
		float turn = controlled ? ReadTurn() : 0f;

		if ( !controlled )
			_turnSeeded = false; // re-seed from the live facing when control resumes (no snap)

		if ( _parked && wish.IsNearZeroLength && !jump && !_launched && MathF.Abs( turn ) < 0.001f )
		{
			Commit( pos, rot );
			return;
		}
		_parked = false;

		if ( MathF.Abs( turn ) > 0.001f )
			rot = Turn( ref pos, rot, turn );

		UpdateVelocity( wish, jump );

		var move = _velocity * Time.Delta;
		if ( IsGrounded )
			MoveGrounded( ref pos, rot, move, controlled ? wish : Vector3.Zero );
		else
			MoveAirborne( ref pos, rot, move );

		// Holding Space against a wall or ceiling in the air: grab it (see HiderController.Climb).
		if ( controlled && !IsGrounded && TryAirGrab( pos, rot, wish ) )
		{
			_launched = false;
			Commit( pos, rot );
			return;
		}

		_launched = false;

		// Park once there's nothing left to do: grounded, no input, no speed.
		bool still = IsGrounded && wish.IsNearZeroLength && _velocity.IsNearZeroLength && MathF.Abs( turn ) < 0.001f;
		_stillTicks = still ? _stillTicks + 1 : 0;
		if ( _stillTicks >= ParkAfterTicks )
			_parked = true;

		Commit( pos, rot );
	}

	// Hand the pose to physics. The controller sweep moves the keyframed body there over this step (so it has a
	// real velocity for whatever rides or touches it); the engine writes it back to the GameObject after the step.
	void Commit( Vector3 pos, Rotation rot )
	{
		_lastPose = pos;
		var tx = new Transform( pos, rot );
		if ( Body.PhysicsBody is { } pb && pb.IsValid() )
			pb.Move( tx, Time.Delta );
		else
			WorldTransform = tx;
	}

	Vector3 ReadWish()
	{
		float speed = IsCrouching ? CrouchSpeed : WalkSpeed; // no sprint for props — crouch is the fine control

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

	void UpdateVelocity( Vector3 wish, bool jump )
	{
		float dt = Time.Delta;
		if ( IsGrounded )
		{
			// Chase the wish along the ground plane, so an incline is walked up rather than driven into.
			float t = 1f - MathF.Exp( -GroundControl * dt );
			var target = ProjectOntoSlope( wish, _groundNormal );
			_velocity = Vector3.Lerp( _velocity, target, t );

			if ( wish.IsNearZeroLength && _velocity.Length < StopSpeed )
				_velocity = Vector3.Zero;

			if ( jump )
			{
				_velocity = _velocity.WithZ( JumpPower );
				_airFromJump = true; // a jump at a wall grabs it (see HiderController.Climb)
				IsGrounded = false;
			}
		}
		else
		{
			// Airborne: weaker horizontal control keeps a jump's momentum; our own gravity, falling faster than
			// it rose (FallGravityMult) so the descent is snappy, not floaty.
			float t = 1f - MathF.Exp( -AirControl * dt );
			var horiz = Vector3.Lerp( _velocity.WithZ( 0f ), wish.WithZ( 0f ), t );
			float g = Gravity * (_velocity.z < 0f ? FallGravityMult : 1f);
			_velocity = horiz.WithZ( _velocity.z - g * dt );
		}

		// Leash on the result too: a launch or a jump's momentum can carry the body outward with no input.
		_velocity = ApplyLeash( _velocity, pullIn: true );
	}

	void MoveGrounded( ref Vector3 pos, Rotation rot, Vector3 move, Vector3 wish )
	{
		_sinceAirWall = 999f; // an air grab only counts walls touched since leaving the ground
		_airFromJump = false;
		var start = pos;
		var startVel = _velocity;

		var walked = start;
		Slide( ref walked, rot, move, out var steep );
		bool stepped = false;

		// Blocked by something steep: try the step — up, across, down — and keep it if it got further.
		if ( steep.Hit && StepHeight > 0f && TryStep( start, rot, move.WithZ( 0f ), startVel, out var up, out var stepVel ) )
		{
			float walkedFlat = (walked - start).WithZ( 0f ).LengthSquared;
			float steppedFlat = (up - start).WithZ( 0f ).LengthSquared;
			if ( steppedFlat > walkedFlat + 0.01f )
			{
				walked = up;
				_velocity = stepVel;
				stepped = true;
			}
		}

		// Held against a wall: once the push has lasted, physics turns it flush (see HiderController.Press).
		NotePush( wish, stepped ? default : steep );

		pos = walked;

		// Stay on the ground: snap down a step's height (down stairs and ramps), or start falling.
		if ( !SnapToGround( ref pos, rot, StepHeight + Skin ) )
		{
			IsGrounded = false;
			_groundObject = null;
		}
	}

	void MoveAirborne( ref Vector3 pos, Rotation rot, Vector3 move )
	{
		_pushPerKg = 0f;
		Slide( ref pos, rot, move, out var steep );
		NoteAirContact( steep );

		// Landing: only while not rising, and only on something walkable.
		if ( _velocity.z <= 0f && SnapToGround( ref pos, rot, Skin * 2f + 0.5f ) )
			_velocity = ProjectOntoSlope( _velocity.WithZ( 0f ), _groundNormal );
	}

	// Up by StepHeight (or as far as the ceiling allows), across by the flat move, down onto walkable ground.
	bool TryStep( Vector3 start, Rotation rot, Vector3 flatMove, Vector3 startVel, out Vector3 pos, out Vector3 vel )
	{
		pos = start;
		vel = startVel;
		var saved = _velocity;
		_velocity = startVel;

		var up = HullSweep( start, rot, start + Vector3.Up * StepHeight );
		if ( up.StartedSolid )
		{
			_velocity = saved;
			return false;
		}
		var raised = up.Hit ? up.EndPosition - Vector3.Up * Skin : start + Vector3.Up * StepHeight;
		float lift = raised.z - start.z;
		if ( lift < 1f )
		{
			_velocity = saved;
			return false;
		}

		var across = raised;
		Slide( ref across, rot, flatMove, out _ );

		var down = HullSweep( across, rot, across - Vector3.Up * (lift + Skin * 2f) );
		if ( down.StartedSolid || !down.Hit || !Walkable( down.Normal ) )
		{
			_velocity = saved;
			return false;
		}

		pos = down.EndPosition + Vector3.Up * Skin;
		vel = _velocity;
		_velocity = saved;
		return true;
	}

	// Sweep down by `distance`; walkable hit = grounded there (a skin above it), with its normal + object.
	bool SnapToGround( ref Vector3 pos, Rotation rot, float distance )
	{
		var tr = HullSweep( pos, rot, pos - Vector3.Up * distance );
		if ( tr.StartedSolid )
		{
			if ( TryUnstick( ref pos, rot ) )
				tr = HullSweep( pos, rot, pos - Vector3.Up * distance );
			if ( tr.StartedSolid )
				return IsGrounded; // still stuck — keep whatever we had rather than flicker
		}

		if ( !tr.Hit || !Walkable( tr.Normal ) )
			return false;

		pos = tr.EndPosition + Vector3.Up * Skin;
		if ( !IsGrounded && _velocity.z < 0f )
			_velocity = _velocity.WithZ( 0f );
		IsGrounded = true;
		_groundNormal = tr.Normal;
		SetGround( tr );
		return true;
	}

	/// <summary>The steepest blocking surface a slide met — walls first, else a ceiling (never a floor).</summary>
	struct SteepHit
	{
		public bool Hit;
		public Vector3 Normal;
		public Vector3 Point;
	}

	// Collide-and-slide: advance to each hit (a skin off the surface), then continue along the surface with
	// what's left, clipping our velocity so we stop pushing into it. Creases between two surfaces slide along
	// their shared line; a third blocks.
	void Slide( ref Vector3 pos, Rotation rot, Vector3 move, out SteepHit steep )
	{
		steep = default;
		Span<Vector3> planes = stackalloc Vector3[SlideIterations];
		int count = 0;
		bool unstuck = false;

		for ( int i = 0; i < SlideIterations && move.LengthSquared > 1e-6f; i++ )
		{
			var tr = HullSweep( pos, rot, pos + move );
			if ( tr.StartedSolid )
			{
				if ( unstuck || !TryUnstick( ref pos, rot ) )
					return;
				unstuck = true;
				continue;
			}

			if ( !tr.Hit )
			{
				pos += move;
				return;
			}

			pos = tr.EndPosition + tr.Normal * Skin;
			var remaining = move * (1f - tr.Fraction);
			var n = tr.Normal;

			if ( !Walkable( n ) && (!steep.Hit || MathF.Abs( n.z ) < MathF.Abs( steep.Normal.z )) )
				steep = new SteepHit { Hit = true, Normal = n, Point = tr.HitPosition };

			float into = Vector3.Dot( _velocity, n );
			if ( into < 0f )
				_velocity -= n * into;

			planes[count++] = n;
			move = ClipToPlanes( remaining, planes[..count] );
		}
	}

	static Vector3 ClipToPlanes( Vector3 v, ReadOnlySpan<Vector3> planes )
	{
		var last = planes[^1];
		v -= last * MathF.Min( Vector3.Dot( v, last ), 0f );

		for ( int i = 0; i < planes.Length - 1; i++ )
		{
			if ( Vector3.Dot( v, planes[i] ) >= -0.001f )
				continue;

			// Into an earlier plane too: slide along the crease the two share, or stop if there isn't one.
			var crease = Vector3.Cross( planes[i], last );
			if ( crease.LengthSquared < 1e-6f )
				return Vector3.Zero;
			crease = crease.Normal;
			return crease * Vector3.Dot( v, crease );
		}
		return v;
	}

	// Something put the hull inside solid (a commit that grew the shape, a mover pushing into us): find the
	// nearest clear pose — up first, then sideways, then down — out to MaxUnstick. Gravity and the snap
	// settle it back onto whatever it was resting on.
	bool TryUnstick( ref Vector3 pos, Rotation rot )
	{
		if ( !PoseBlocked( pos, rot ) )
		{
			pos += Vector3.Up * Skin; // only touching: lift off the contact, the next sweep will start clear
			return true;
		}

		Span<Vector3> dirs = stackalloc Vector3[] { Vector3.Up, Vector3.Forward, Vector3.Backward, Vector3.Left, Vector3.Right, Vector3.Down };
		for ( float d = 1f; d <= MaxUnstick; d *= 2f )
		{
			foreach ( var dir in dirs )
			{
				var c = pos + rot * dir * d;
				if ( dir == Vector3.Up || dir == Vector3.Down )
					c = pos + dir * d;
				if ( PoseBlocked( c, rot ) )
					continue;

				pos = c;
				Unpark();
				return true;
			}
		}
		return false;
	}

	// ── Ground carry ─────────────────────────────────────────────────────────────────────────────────
	// Standing on something that moves (a held prop, a loose one, a door): follow it. Static world never moves,
	// so for most props this is one transform compare per tick, parked or not.

	void SetGround( in SceneTraceResult tr )
	{
		var go = tr.Body is { } b && b.IsValid() && b.BodyType != PhysicsBodyType.Static ? tr.GameObject : null;
		if ( go != _groundObject )
		{
			_groundObject = go;
			if ( go.IsValid() )
				_groundLast = go.WorldTransform;
		}
	}

	void CarryWithGround( ref Vector3 pos, ref Rotation rot )
	{
		if ( _groundObject is null )
			return;

		if ( !_groundObject.IsValid() )
		{
			_groundObject = null;
			IsGrounded = false; // what held us up is gone — fall
			Unpark();
			return;
		}

		var now = _groundObject.WorldTransform;
		if ( now.AlmostEqual( _groundLast, 0.001f ) )
			return;

		// Where our feet sit on the ground object, carried to where it is now. Swept, so a platform can't
		// carry us into a wall.
		var local = _groundLast.PointToLocal( pos );
		var target = now.PointToWorld( local );
		float yaw = MathX.DeltaDegrees( _groundLast.Rotation.Angles().yaw, now.Rotation.Angles().yaw );
		_groundLast = now;
		Unpark();

		Slide( ref pos, rot, target - pos, out _ );
		if ( MathF.Abs( yaw ) > 0.001f )
			rot = Turn( ref pos, rot, yaw );
	}

	// Reproject a flat, world-space wish onto the ground plane so movement runs ALONG a slope instead of driving
	// into it. Speed is preserved. Flat ground is a no-op.
	static Vector3 ProjectOntoSlope( Vector3 wish, Vector3 normal )
	{
		float speed = wish.Length;
		if ( speed < 0.01f )
			return wish;

		var projected = wish - normal * Vector3.Dot( wish, normal );
		return projected.IsNearZeroLength ? Vector3.Zero : projected.Normal * speed;
	}

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
