using System;
using Sandbox.Movement;

namespace Mimiclay;

/// <summary>
/// Hunters climb walls the way props do (see HiderController.Climb and HUNTER-CLIMB-PLAN.md) — but never ceilings
/// (user, 2026-11-01: a hunter only clings to surfaces it can face; climbing up into an overhang just stops there). An engine
/// <see cref="MoveMode"/> next to the walk, chosen by <see cref="Score"/> only while clinging. The rules and keys are
/// the prop's (ClimbRules) — Space up, Ctrl down, A/D sideways, W into the wall (mounts the top), Shift lets go — on
/// a much simpler shape: the hunter is an upright capsule, so one sweep of its own body reads the surface cleanly.
///
/// The hunter's walk must not get any stickier for this, so grabbing is deliberately harder than for props:
///  • Space on the ground is always an instant jump (the engine's) — no ground grab, no tap/hold detection.
///  • A wall is grabbed only with Space HELD (or pressed within GrabBuffer) while steering into it, airborne for
///    MinAirTime, not falling fast, and not about to clear its top — a tapped jump at a wall never sticks.
///  • Letting go is instant: Shift, S pulling away, touching standable ground, or losing the surface.
/// </summary>
[Icon( "landscape" ), Group( "Movement" ), Title( "MoveMode - Hunter Climb" )]
public sealed class HunterClimb : MoveMode
{
	/// <summary>Move-mode priority while clinging (above the walk, ladders and swimming).</summary>
	[Property] public int Priority { get; set; } = 10;

	/// <summary>Hunters can grab and climb walls.</summary>
	[Property] public bool AllowClimbing { get; set; } = true;

	/// <summary>Speed along a surface (u/s). Slower than walking reads as sticky.</summary>
	[Property] public float ClimbSpeed { get; set; } = 150f;

	/// <summary>Most speed (u/s) the hunter is pulled back onto the surface when it drifts off it.</summary>
	[Property] public float StickSpeed { get; set; } = 60f;

	/// <summary>Speed the hunter is pulled round an outer edge toward the last point it touched (u/s).</summary>
	[Property] public float EdgePullSpeed { get; set; } = 140f;

	/// <summary>How far (u) a wall can be from the body and still be grabbed.</summary>
	[Property] public float GrabReach { get; set; } = 6f;

	/// <summary>Seconds a Space press stays "I want to grab" while waiting to reach a wall (Space held always counts).</summary>
	[Property, Range( 0f, 0.5f )] public float GrabBuffer { get; set; } = 0.15f;

	/// <summary>No grab this soon (s) after leaving the ground — a jump that starts beside a wall isn't a grab.</summary>
	[Property, Range( 0f, 0.5f )] public float MinAirTime { get; set; } = 0.1f;

	/// <summary>No grab while falling faster than this (u/s).</summary>
	[Property] public float MaxGrabFallSpeed { get; set; } = 400f;

	/// <summary>Seconds without any surface before the hunter lets go and falls.</summary>
	[Property, Range( 0.05f, 1f )] public float LoseTime { get; set; } = 0.25f;

	/// <summary>Seconds of S held (pulling away from the wall) before letting go.</summary>
	[Property, Range( 0f, 0.5f )] public float PullAwayTime { get; set; } = 0.1f;

	/// <summary>Speed (u/s) the hunter pushes off the wall when letting go by pulling away.</summary>
	[Property] public float PushOffSpeed { get; set; } = 100f;

	/// <summary>How far (u) over the lip the mantle looks for ground to stand on.</summary>
	[Property] public float MantleReach { get; set; } = 8f;

	[ConVar( "mimi_dbg_hunter_climb", Help = "Debug: draw the hunter climb's sensed surface and log grabs, corners and let-gos." )]
	public static bool DebugClimb { get; set; }

	/// <summary>Test harness: fake Space (1) or Ctrl (−1) held, for grabbing and climbing. Scripts can't hold keys.</summary>
	[ConVar( "mimi_dbg_hunter_climb_vertical", Help = "Debug: hunters grab and climb as if Space (1) or Ctrl (-1) were held." )]
	public static float DebugVertical { get; set; }

	/// <summary>True while the hunter clings to a wall (including the mantle carry over a lip). Owner only — other
	/// machines read <see cref="ClingingNet"/>.</summary>
	public bool IsClinging => _climbing;

	/// <summary>Test harness (SimLab): the move keys while climbing (x forward, y left), in place of the real ones.</summary>
	public static Vector3? DebugMove { get; set; }

	// SimLab: the surface's normal, and a grab of the wall the hunter faces from where it stands (no jump needed).
	internal Vector3 LabNormal => _n;
	internal bool LabGrab()
	{
		var dir = Rotation.FromYaw( Controller.EyeAngles.yaw ).Forward;
		var pos = WorldPosition + Vector3.Up * 4f;
		var tr = Sweep( pos - dir * BackOff, pos + dir * 64f );
		if ( !tr.Hit || tr.StartedSolid || Walkable( tr.Normal ) || Ceiling( tr.Normal ) || !Climbable( tr ) )
			return false;
		StartClimb( tr.Normal, tr.HitPosition, "lab", gap: MathF.Max( tr.Distance - BackOff, 0f ) );
		return true;
	}

	/// <summary>IsClinging as every machine sees it (the owner writes it each tick): the climb scrape sound runs on all.</summary>
	[Sync] public bool ClingingNet { get; private set; }

	public override bool AllowGrounding => false;
	public override bool AllowFalling => false;

	MoveModeWalk _walk;
	MoveModeWalk Walk => _walk.IsValid() ? _walk : (_walk = Components.Get<MoveModeWalk>());
	float GroundAngle => Walk.IsValid() ? Walk.GroundAngle : 45f;
	float StepHeight => Walk.IsValid() ? Walk.StepUpHeight : 18f;

	bool _climbing;
	Vector3 _n;             // smoothed normal of the surface we cling to (points away from it)
	Vector3 _point;         // last contact point — what an outer edge pulls toward
	float _gap;             // body's distance off the surface (negative = sunk in)
	bool _surface;          // touching (or within reach of) the surface this tick
	bool _embedded;         // sunk in further than the sensing back-off
	float _sinceSurface;
	float _gated;           // seconds a reading far off the current normal has persisted
	Vector3 _lastPos;

	// This tick's input (UpdateMove), used by AddVelocity and PostPhysicsStep.
	Vector3 _along;
	float _into;
	float _away;
	float _vertical;
	bool _input;
	float _pullAway;

	// Corners: the surface the last corner took us off (and the keys held then), and a cooldown so two surfaces can't trade
	// places every tick. See HiderController.StepClimb for the history.
	Vector3? _cornerLeft;
	Vector3 _cornerMove;
	float _cornerVertical;
	RealTimeSince _sinceCorner = 999f;

	// Outer corners (new, user 2026-11-07): round the end of a wall onto the face beyond it (TryWrapOuterCorner), the travel
	// turned over the edge and CARRIED while the same keys stay held — on the new face the look no longer runs along it (you
	// were looking along the old one, which is now straight away from the new one: W read as "pull away" and let go).
	Vector3? _carryDir;
	Vector3 _carryMove;
	Vector3 _carryFrom;
	const float WrapReach = 18f;
	float _stepOver;
	float _blockedUp; // seconds climbing up but held back by something overhead (see StepOutUnder)

	// Mantle carry: up to the top's height, then in over it, then the walk takes over.
	bool _mantling;
	Vector3 _mantleDir;
	float _mantleZ;
	Vector3 _mantleFrom;
	RealTimeSince _sinceMantle;

	// Grab intent.
	RealTimeSince _sinceJumpPress = 999f;
	RealTimeSince _sinceLetGo = 999f;
	bool _letGoQueued;
	bool _pushOff;

	// Space held on from a mantle (as for props, HiderController._climbOnFromMantle): over a ledge into a taller wall
	// behind it, the climb carries on up without letting go of Space — on the ledge a held Space is no jump (the engine
	// jumps on a press), so without this the grab never came. Cleared when Space is let go.
	bool _climbOnFromMantle;
	Vector3 _climbOnDir;
	const float ClimbOnReach = 64f;     // how far behind the lip that wall can be (flatgrass facade ledges: ~40u)

	// A ledge too narrow to stand on (the wall above set back a little): when the wall we're on runs out going up, look
	// this much further in for a wall facing the same way and carry on up it.
	const float SetBackReach = 24f;

	const float BackOff = 2f;          // sensing sweeps start this far off the surface, so they never start touching it
	const float Reach = 6f;            // ...and look this far past it
	const float NormalGate = 0.82f;    // cos(35°): a reading further off the current normal is a bump edge, not the surface
	const float GateHold = 0.15f;      // ...unless it persists this long while we're still
	const float CornerCooldown = 0.2f;
	const float StepOverTime = 0.12f;
	const float LetGoCooldown = 0.3f;

	public override int Score( PlayerController controller ) => _climbing ? Priority : -100;

	public override void UpdateRigidBody( Rigidbody body )
	{
		// Velocity is set outright every tick (AddVelocity), so no damping: the ladder's LinearDamping 20 is exactly the
		// treacle feel to avoid.
		body.Gravity = false;
		body.LinearDamping = 0f;
		body.AngularDamping = 1f;
	}

	public override void OnModeBegin()
	{
		Controller.IsClimbing = true;
		// Stop dead INTO the surface, keep what ran along it, so catching a wall at speed doesn't stop dead.
		var v = Controller.Body.Velocity;
		Controller.Body.Velocity = (v - _n * Vector3.Dot( v, _n )).ClampLength( ClimbSpeed );
	}

	public override void OnModeEnd( MoveMode next )
	{
		Controller.IsClimbing = false;
		_climbing = false;
		_mantling = false;
		var v = Controller.Body.Velocity.ClampLength( Controller.RunSpeed );
		if ( _pushOff )
			v += _n.WithZ( 0f ) * PushOffSpeed;
		_pushOff = false;
		Controller.Body.Velocity = v;
	}

	protected override void OnDisabled()
	{
		if ( _climbing )
			EndClimb( "disabled" );
	}

	public override bool IsStandableSurface( in SceneTraceResult result ) => false;

	bool Walkable( Vector3 normal ) => Vector3.GetAngle( Vector3.Up, normal ) <= GroundAngle;

	// Something the hunter may cling to: not a body that moves under physics (loose props, other hunters — they'd just be
	// shoved away) and never a pawn: no riding a disguised player, however it's being simulated right now, and no
	// climbing another hunter (whose clay head carries its own kinematic body, so the Dynamic test alone let it through).
	static bool Climbable( in SceneTraceResult tr )
	{
		if ( !tr.Hit || tr.Body is not { } body || !body.IsValid() || body.BodyType == PhysicsBodyType.Dynamic )
			return false;
		if ( ClimbRules.IsHunter( tr.GameObject ) )
			return false;
		return !ClimbRules.IsUnclimbablePawn( tr.GameObject ); // (a frozen prop climbs like scenery)
	}

	// The hunter's real rigidbody (capsule + feet box) swept from `from` to `to`, translation only — it never rotates.
	SceneTraceResult Sweep( Vector3 from, Vector3 to )
	{
		var pb = Controller.Body.PhysicsBody;
		return Scene.Trace.Body( pb, new Transform( from, pb.Rotation ), to )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithCollisionRules( Controller.Tags )
			.Run();
	}

	// The wall-sensing sweep: the body's own two shapes — the controller's capsule (upper body) and feet box (lower) — read
	// live off its colliders (the retail engine sizes them differently from the open-source one: capsule radius is
	// BodyRadius/2, not ·√2/2), each trimmed SenseInset off the body's top and bottom; the nearer hit wins. With the whole
	// body, touching an overhang above or the floor below read as start-solid — "sunk into the wall" — and the push-out
	// shoved the hunter off the wall it was climbing.
	const float SenseInset = 2f;

	SceneTraceResult SenseSweep( Vector3 from, Vector3 to )
	{
		SceneTrace Rules( SceneTrace t ) => t.IgnoreGameObjectHierarchy( GameObject ).WithCollisionRules( Controller.Tags );

		var feet = Controller.FeetCollider;
		var half = feet.Scale * 0.5f;
		var box = new BBox( feet.Center - half, feet.Center + half );
		box = new BBox( box.Mins.WithZ( MathF.Max( box.Mins.z, SenseInset ) ), box.Maxs );
		var tr = Rules( Scene.Trace.Box( box, from, to ) ).Run();

		var body = Controller.BodyCollider;
		if ( !body.IsValid() || !body.Enabled || tr.StartedSolid )
			return tr;
		float top = MathF.Max( body.Start.z - SenseInset, body.End.z );
		var cap = Rules( Scene.Trace.Capsule( new Capsule( body.End, body.Start.WithZ( top ), body.Radius ), from, to ) ).Run();
		if ( cap.StartedSolid || !tr.Hit )
			return cap;
		return cap.Hit && cap.Distance < tr.Distance ? cap : tr;
	}

	// ── Grabbing (owner, while the walk is the mode) ─────────────────────────────────────────────────────────────────

	protected override void OnFixedUpdate()
	{
		if ( !IsProxy )
			ClingingNet = _climbing;
		if ( IsProxy || !Controller.IsValid() || !Controller.Body.IsValid() || !Controller.UseInputControls )
			return;

		if ( Input.Pressed( "jump" ) )
			_sinceJumpPress = 0f;
		if ( _climbing && Input.Pressed( "run" ) )
			_letGoQueued = true; // latched: consumed by PostPhysicsStep
		if ( !SpaceHeld )
			_climbOnFromMantle = false;

		if ( !_climbing && AllowClimbing && Controller.Mode is MoveModeWalk && !(_climbOnFromMantle && TryClimbOn()) )
			TryGrab();
	}

	bool SpaceHeld => Input.Down( "jump" ) || DebugVertical > 0f;

	// On the ledge after a mantle, Space still held: grab the wall ahead (steering, else the way the mantle went) — on the
	// ground or in the air, and only a wall that rises past a step, so walking off over a kerb never grabs.
	bool TryClimbOn()
	{
		var steer = Controller.WishVelocity.WithZ( 0f );
		var dir = steer.IsNearZeroLength ? _climbOnDir : steer.Normal;
		if ( dir.IsNearZeroLength )
			return false;
		var from = WorldPosition + Vector3.Up * StepHeight;
		var tr = Sweep( from - dir * BackOff, from + dir * ClimbOnReach );
		if ( !tr.Hit || tr.StartedSolid || Walkable( tr.Normal ) || Ceiling( tr.Normal ) || !Climbable( tr )
			|| Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) < 0.5f )
			return false;
		StartClimb( tr.Normal, tr.HitPosition, "on from the mantle", gap: tr.Distance - BackOff );
		return true;
	}

	void TryGrab()
	{
		if ( !Controller.IsAirborne || Controller.TimeSinceGrounded < MinAirTime )
			return;
		var vel = Controller.Body.Velocity;
		if ( vel.z < -MaxGrabFallSpeed )
			return;

		bool intent = Input.Down( "jump" ) || _sinceJumpPress < GrabBuffer || DebugVertical > 0f;
		if ( _sinceLetGo < LetGoCooldown )
			intent = _sinceJumpPress < _sinceLetGo; // just let go: only a FRESH press grabs again
		if ( !intent )
			return;

		var pos = WorldPosition;
		var yaw = Controller.EyeAngles.yaw;

		// Walls: the way we're steering (no steering: the way we look).
		var steer = Controller.WishVelocity.WithZ( 0f );
		var dir = steer.IsNearZeroLength ? Rotation.FromYaw( yaw ).Forward : steer.Normal;
		var tr = Sweep( pos - dir * BackOff, pos + dir * GrabReach );
		if ( tr.Hit && !tr.StartedSolid && !Walkable( tr.Normal ) && !Ceiling( tr.Normal ) && Climbable( tr )
			&& Vector3.Dot( dir, -tr.Normal.WithZ( 0f ).Normal ) > 0.5f && !WillClear( pos, dir, vel ) )
			StartClimb( tr.Normal, tr.HitPosition, "wall" );
	}

	// Faces more down than sideways (an underside, a steep overhang): never clung to.
	static bool Ceiling( Vector3 normal ) => normal.z < -0.5f;

	// Jumping onto a ledge, not up a wall: at the top of this jump our feet will be over the wall's top (the body swept
	// forward from there meets nothing). Grabbing its front instead of landing on it is the stickiest thing this could do.
	bool WillClear( Vector3 pos, Vector3 dir, Vector3 vel )
	{
		float g = MathF.Max( MathF.Abs( Scene.PhysicsWorld.Gravity.z ), 1f );
		float rise = vel.z > 0f ? vel.z * vel.z / (2f * g) : 0f;
		var from = pos + Vector3.Up * (rise + 1f);
		return !Sweep( from, from + dir * (GrabReach + BackOff) ).Hit;
	}

	// `gap`: how far the body starts off the wall (a wall grabbed from further off is closed in on by AddVelocity, and the
	// sensing reaches that far to keep it).
	void StartClimb( Vector3 normal, Vector3 point, string why, float gap = 0f )
	{
		_climbing = true;
		_mantling = false;
		_climbOnFromMantle = false;
		_n = normal.Normal;
		_point = point;
		_gap = MathF.Max( gap, 0f );
		_surface = true;
		_embedded = false;
		_sinceSurface = 0f;
		_gated = 0f;
		_cornerLeft = null;
		_carryDir = null;
		_stepOver = 0f;
		_pullAway = 0f;
		_letGoQueued = false;
		_pushOff = false;
		_lastPos = WorldPosition;
		if ( DebugClimb )
			Log.Info( $"[hunter climb] grab {why} n={_n:0.00} on {GameObject.Name}" );
	}

	void EndClimb( string why, bool pushOff = false )
	{
		if ( !_climbing )
			return;
		_climbing = false;
		_mantling = false;
		_pushOff = pushOff;
		_sinceLetGo = 0f;
		if ( DebugClimb )
			Log.Info( $"[hunter climb] let go: {why}" );
	}

	// ── Climbing (owner; this is the active mode) ────────────────────────────────────────────────────────────────────

	// Called from the controller's input pass: read the keys into motion along the surface. No smoothing — the hunter's
	// AccelerationTime is 0, and climbing must answer the keys as quickly as walking does.
	public override Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		if ( !_climbing || _mantling )
			return Vector3.Zero;

		var move = (DebugMove ?? input).ClampLength( 1f );
		float yaw = eyes.Angles().yaw;
		float vertical = (Input.Down( "jump" ) ? 1f : 0f) - (Input.Down( "duck" ) ? 1f : 0f);
		if ( DebugVertical != 0f )
			vertical = Math.Clamp( DebugVertical, -1f, 1f );

		var wasd = ClimbRules.Along( yaw, move, _n, ClimbSpeed, lookAlong: true ); // look down the wall, W walks along it
		bool carrying = false;
		if ( _carryDir is { } carry )
		{
			if ( !move.IsNearZeroLength && Vector3.Dot( move.Normal, _carryMove.Normal ) > 0.95f && Vector3.Dot( _n, _carryFrom ) <= 0.97f )
			{
				var flat = carry - _n * Vector3.Dot( carry, _n );
				if ( !flat.IsNearZeroLength )
				{
					wasd = flat.Normal * ClimbSpeed * MathF.Min( move.Length, 1f );
					carrying = true;
				}
			}
			else
				_carryDir = null; // new keys (or back on a face like the one we left): the look has W/S again
		}
		var climbUp = ClimbRules.Vertical( vertical, _n, Vector3.Zero, ClimbSpeed );

		if ( _cornerLeft is { } left )
		{
			if ( vertical != _cornerVertical || (move - _cornerMove).Length > 0.05f )
				_cornerLeft = null; // new keys: a fresh press back toward it is meant
			else
			{
				wasd = ClimbRules.NotBack( wasd, left );
				climbUp = ClimbRules.NotBack( climbUp, left );
			}
		}

		_into = ClimbRules.Into( yaw, move, _n, lookAlong: true );
		// How much W/S head straight AWAY from the wall (the level look × the key, against the wall's flat normal): S facing
		// the wall, or W with your back to it. Looking along the wall they just walk along it (user, 2026-11-01).
		var flatN = _n.WithZ( 0f );
		_away = flatN.IsNearZeroLength ? 0f : move.x * Vector3.Dot( Rotation.FromYaw( yaw ).Forward, flatN.Normal );
		if ( carrying )
			_away = 0f; // (the carried keys go round the corner, not away from the new face)
		_vertical = vertical;
		_cornerMove = move;
		_along = wasd + climbUp;
		_input = !move.IsNearZeroLength || vertical != 0f;
		return _along;
	}

	public override void AddVelocity()
	{
		if ( !_climbing )
			return;

		var body = Controller.Body;
		bool controlled = Controller.UseInputControls;
		var along = controlled ? _along : Vector3.Zero; // locked / editing: just hang on

		if ( _mantling )
		{
			body.Velocity = MantleVelocity();
			return;
		}

		// Normal motion: close any gap to the surface (never pressing into it — resting exactly in contact is what holds
		// still with no drift and no jitter), back out if sunk in, lift off while stepping over a bump.
		float dt = MathF.Max( Time.Delta, 0.001f );
		Vector3 v;
		if ( _embedded )
			v = along + _n * StickSpeed;
		else if ( _stepOver > 0f )
			v = along + _n * ClimbSpeed * 0.5f;
		else if ( _surface )
			// A wall well off (a set-back wall, one grabbed from a ledge) closes at climbing speed, a drift at StickSpeed.
			v = along - _n * Math.Clamp( _gap / dt, 0f, _gap > 2f ? MathF.Max( StickSpeed, ClimbSpeed ) : StickSpeed );
		else
		{
			// Off an outer edge: pull toward the last contact so we swing round onto the next face — but never into the
			// old surface faster than the ordinary stick.
			var pull = (_point - (WorldPosition + Vector3.Up * Controller.CurrentHeight * 0.5f)).Normal * EdgePullSpeed;
			float inward = Vector3.Dot( pull, -_n );
			if ( inward > StickSpeed )
				pull -= -_n * (inward - StickSpeed);
			v = along * 0.5f + pull;
		}
		body.Velocity = v;
	}

	public override void PostPhysicsStep()
	{
		if ( !_climbing )
			return;

		var pos = WorldPosition;

		// Teleported (respawn, round reset): let go rather than pull toward a wall on the other side of the map.
		if ( pos.Distance( _lastPos ) > MathF.Max( Controller.RunSpeed, ClimbSpeed ) * Time.Delta * 2f + 16f )
		{
			EndClimb( "teleported" );
			return;
		}
		_lastPos = pos;

		// Locked or editing: the controller stops calling UpdateMove, so this tick's input is whatever it last was. Hang on.
		if ( !Controller.UseInputControls )
		{
			_along = Vector3.Zero;
			_into = 0f;
			_away = 0f;
			_vertical = 0f;
			_input = false;
		}

		if ( _mantling )
		{
			StepMantle( pos );
			return;
		}

		bool controlled = Controller.UseInputControls;
		if ( controlled && _letGoQueued )
		{
			_letGoQueued = false;
			EndClimb( "shift" );
			return;
		}

		// W/S backing us away from the wall — mostly away, within ~60° of straight off it: let go and push off. Backing
		// along the wall at a shallower angle just walks backwards along it.
		if ( controlled && _away > 0.5f && MathF.Abs( _n.z ) < 0.7f )
		{
			_pullAway += Time.Delta;
			if ( _pullAway >= PullAwayTime )
			{
				EndClimb( "pulled away", pushOff: true );
				return;
			}
		}
		else
			_pullAway = 0f;

		// Feet on ground we can stand on (climbed down to the floor, or onto a slope): walking takes it from here.
		var ground = Controller.TraceBody( pos + Vector3.Up * 2f, pos + Vector3.Down * 2f, 0.9f, 0.5f );
		if ( ground.Hit && !ground.StartedSolid && Walkable( ground.Normal ) && _vertical <= 0f && _along.z <= 1f )
		{
			EndClimb( "on the ground" );
			return;
		}

		SenseSurface( pos );
		if ( !_climbing )
			return;

		Corners( pos );
		StepOutUnder( pos );

		// Climbing up (or pushing into the wall) with the top within a step of our feet: mantle onto it.
		if ( (_along.z > 1f || _into > 0.3f) && MathF.Abs( _n.z ) < 0.7f && TryMantle( pos ) )
			return;

		_stepOver = MathF.Max( 0f, _stepOver - Time.Delta );

		if ( DebugClimb )
		{
			Scene.DebugOverlay.Line( _point, _point + _n * 24f, _surface ? Color.Green : Color.Red, 0f );
			Scene.DebugOverlay.Sphere( new Sphere( _point, 2f ), _surface ? Color.Green : Color.Red, 0f );
		}
	}

	// From a pose backed off the surface, sweep the body back toward it: the surface's normal, contact and our gap to it.
	// Started inside it (shoved in deeper than the back-off)? Back off further — that's "embedded", push out.
	void SenseSurface( Vector3 pos )
	{
		SceneTraceResult tr = default;
		float back = BackOff;
		_embedded = false;
		for ( ; back <= BackOff * 8f; back *= 2f )
		{
			tr = SenseSweep( pos + _n * back, pos - _n * (Reach + MathF.Max( _gap, 0f )) );
			if ( !tr.StartedSolid )
				break;
			_embedded = true;
		}
		if ( tr.StartedSolid )
			tr = default;

		// The wall ran out — a narrow ledge with the wall above set back? Look further in for one facing the same way.
		if ( !tr.Hit && !_embedded )
		{
			var setBack = SenseSweep( pos + _n * BackOff, pos - _n * (Reach + SetBackReach) );
			if ( setBack.Hit && !setBack.StartedSolid && Vector3.Dot( setBack.Normal, _n ) > NormalGate )
			{
				tr = setBack;
				back = BackOff;
			}
		}

		// Still nothing, going sideways: round an outer corner onto the face beyond the wall's end.
		if ( !tr.Hit && !_embedded && _sinceCorner >= CornerCooldown && MathF.Abs( _n.z ) < 0.7f && TryWrapOuterCorner( pos, out var face ) )
		{
			var turnOver = Rotation.FromToRotation( _n, face.Normal );
			var flatAlong = (_along - _n * Vector3.Dot( _along, _n )).WithZ( 0f );
			if ( !_cornerMove.IsNearZeroLength && !flatAlong.IsNearZeroLength )
			{
				_carryDir = turnOver * flatAlong.Normal;
				_carryMove = _cornerMove;
				_carryFrom = _n;
			}
			if ( DebugClimb )
				Log.Info( $"[hunter climb] outer corner {_n:0.00} → {face.Normal:0.00} on {face.GameObject?.Name}" );
			_cornerLeft = null;
			_n = face.Normal;
			_point = face.HitPosition;
			_gap = 0f;
			_surface = false; // swung round by the edge pull until the new face is under us
			_sinceSurface = 0f;
			_sinceCorner = 0f;
			return;
		}

		_surface = (tr.Hit && Climbable( tr )) || _embedded;
		if ( tr.Hit && Climbable( tr ) )
		{
			_gap = tr.Distance - back;
			bool close = Vector3.Dot( tr.Normal, _n ) > NormalGate;
			_gated = close || _input ? 0f : _gated + Time.Delta;
			if ( close )
				_n = Vector3.Lerp( _n, tr.Normal, 0.5f ).Normal;
			else if ( Vector3.Dot( tr.Normal, _n ) > 0.3f && _gated > GateHold )
			{
				_n = tr.Normal;
				_gated = 0f;
			}
			_point = tr.HitPosition;
			_sinceSurface = 0f;
		}
		else
		{
			_gap = 0f;
			_sinceSurface += Time.Delta;
			if ( _sinceSurface > LoseTime )
			{
				EndClimb( "lost the surface" );
				return;
			}
		}

		if ( Walkable( _n ) )
			EndClimb( "surface became ground" );
		else if ( Ceiling( _n ) )
			EndClimb( "surface became a ceiling" ); // e.g. wrapped round an outer edge onto an underside
	}

	// Going sideways along a wall (normal _n, the travel's level part d) that has ended: the face beyond its end that turns
	// away — facing roughly d. The body is placed a little past the end and half its width deeper than the old face (so
	// it overlaps the new face's depth however narrow — a thin panel's end too), then swept BACK along -d.
	bool TryWrapOuterCorner( Vector3 pos, out SceneTraceResult face )
	{
		face = default;
		var d = (_along - _n * Vector3.Dot( _along, _n )).WithZ( 0f );
		if ( d.Length < 5f )
			return false;
		d = d.Normal;
		float depth = Controller.BodyRadius * 0.5f + 2f; // (BodyRadius is the body's full width)
		var probe = pos + d * 2f - _n * depth;
		var tr = SenseSweep( probe, probe - d * (2f + WrapReach) );
		if ( !tr.Hit || tr.StartedSolid || !Climbable( tr ) || Walkable( tr.Normal ) || MathF.Abs( tr.Normal.z ) > 0.7f )
			return false;
		if ( Vector3.Dot( tr.Normal, d ) < 0.5f || Vector3.Dot( tr.Normal, _n ) > 0.7f )
			return false;
		face = tr;
		return true;
	}

	// Inner corners: moving into a different surface — it takes over (ground ends the climb), or it's a bump we step over.
	void Corners( Vector3 pos )
	{
		if ( _along.Length < 5f || _sinceCorner < CornerCooldown )
			return;

		var dir = _along.Normal;
		var from = pos + _n * BackOff - dir * BackOff;
		float reach = BackOff + _along.Length * Time.Delta + 3f;
		var ahead = Sweep( from, from + dir * reach );
		if ( !ahead.Hit || ahead.StartedSolid || Vector3.Dot( ahead.Normal, _n ) >= 0.7f )
			return;

		if ( Walkable( ahead.Normal ) )
		{
			if ( dir.z < 0f )
				EndClimb( "climbed down onto ground" );
			return;
		}

		// A bump (anything we could step over) — or something we can't cling to: lift over it rather than turn onto it.
		var lifted = from + _n * StepHeight;
		var over = Sweep( lifted, lifted + dir * reach );
		if ( (!over.Hit && !over.StartedSolid) || !Climbable( ahead ) )
		{
			_stepOver = StepOverTime;
			return;
		}

		// Up into an overhang or a ceiling: no turning onto it — the body just comes to rest against it.
		if ( Ceiling( ahead.Normal ) )
			return;

		if ( DebugClimb )
			Log.Info( $"[hunter climb] corner {_n:0.00} → {ahead.Normal:0.00} on {ahead.GameObject?.Name}" );

		_cornerLeft = _n;
		_cornerVertical = _vertical;
		_n = ahead.Normal;
		_point = ahead.HitPosition;
		_sinceSurface = 0f;
		_sinceCorner = 0f;
	}

	// Climbing up but stopped by a SHALLOW overhang (a band on a facade, a worktop's lip): step out over it, as props do.
	// The corner look-ahead can't see it — pressed up against the lip, its sweep starts touching and is thrown away — so
	// this goes by what the body actually did: asked to rise, didn't. Shallow = clear overhead a step out from the wall; a
	// real ceiling (deeper than that) still just stops the climb under it.
	void StepOutUnder( Vector3 pos )
	{
		if ( _along.z < 1f || _stepOver > 0f || Controller.Body.Velocity.z > _along.z * 0.25f )
		{
			_blockedUp = 0f;
			return;
		}
		_blockedUp += Time.Delta;
		if ( _blockedUp < 0.06f )
			return;
		_blockedUp = 0f;

		var lifted = pos + _n.WithZ( 0f ).Normal * StepHeight;
		var up = Sweep( lifted, lifted + Vector3.Up * 6f );
		if ( up.Hit || up.StartedSolid )
			return;
		_stepOver = StepOverTime;
		if ( DebugClimb )
			Log.Info( "[hunter climb] stepping out under a shallow overhang" );
	}

	// At the top of a wall: from a step up, a little way in, is the wall gone with ground under it no higher than a step
	// above our feet? Then carry over the lip (no teleport): rise to the top's height, then in over it.
	bool TryMantle( Vector3 pos )
	{
		var into = (-_n).WithZ( 0f );
		if ( into.IsNearZeroLength )
			return false;
		into = into.Normal;
		float lift = StepHeight;

		var from = pos + Vector3.Up * lift;
		var fwd = Sweep( from, from + into * MantleReach );
		if ( fwd.StartedSolid || (fwd.Hit && !Walkable( fwd.Normal )) )
			return false;

		var over = fwd.Hit ? fwd.EndPosition : from + into * MantleReach;
		var down = Sweep( over, over - Vector3.Up * (lift + MantleReach) );
		// The top must be ABOVE our feet: standing on a ledge (grabbed on from a mantle, the wall still a way ahead) the ledge
		// itself read as "a top within a step" and mantled us onto where we already stood, again and again.
		float rise = down.EndPosition.z - pos.z;
		if ( down.StartedSolid || !down.Hit || !Walkable( down.Normal ) || rise > lift || rise < 1f )
			return false;

		_mantling = true;
		_mantleDir = into;
		_mantleZ = down.EndPosition.z + 1f;
		_mantleFrom = pos;
		_sinceMantle = 0f;
		if ( DebugClimb )
			Log.Info( $"[hunter climb] mantle up {_mantleZ - pos.z:0.0} onto {down.GameObject?.Name}" );
		return true;
	}

	Vector3 MantleVelocity()
	{
		var pos = WorldPosition;
		float dt = MathF.Max( Time.Delta, 0.001f );
		float rise = Math.Clamp( (_mantleZ - pos.z) / dt, 0f, MathF.Max( ClimbSpeed, 300f ) );
		// Forward only once the feet are (nearly) up to the top — earlier, we'd be pushing the lip.
		float forward = pos.z >= _mantleZ - 2f ? MathF.Max( ClimbSpeed, Controller.WalkSpeed ) : 0f;
		return Vector3.Up * rise + _mantleDir * forward;
	}

	void StepMantle( Vector3 pos )
	{
		float travelled = Vector3.Dot( pos - _mantleFrom, _mantleDir );
		if ( (pos.z >= _mantleZ - 1f && travelled >= Controller.BodyRadius) || _sinceMantle > 0.4f ) // BodyRadius = full width: the whole body over the lip
		{
			var dir = _mantleDir;
			EndClimb( "mantled" );
			_climbOnFromMantle = SpaceHeld; // still holding Space: carry on up a wall behind the lip (TryClimbOn)
			_climbOnDir = dir;
		}
	}
}
