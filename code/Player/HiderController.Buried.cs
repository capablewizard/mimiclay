using System;
using System.Linq;

namespace Mimiclay;

// Buried: the prop is genuinely INSIDE something — deeper than the overlap inset, so not merely resting against it —
// and the unstick search's short hops can't get it out (a train impaled on the end of a shelf). Hopping further
// automatically is how props got fired through walls (a long teleport can land on the far side of a thin wall), so
// instead the player steers out: no collision and no gravity, WASD by the camera's full view (pitch included),
// Space up, Ctrl down, until the move hull is clear. Then normal movement resumes from rest.
//
// Becoming buried is logged once, with the last few ticks' positions and modes, so the way in can be found.
public sealed partial class HiderController
{
	/// <summary>Fastest the prop may fall (u/s).</summary>
	[Property, Group( "Movement" )] public float MaxFallSpeed { get; set; } = 3000f;

	/// <summary>Speed (u/s) the player steers a buried prop out of whatever it's stuck in.</summary>
	[Property, Group( "Movement" )] public float BuriedEscapeSpeed { get; set; } = 120f;

	/// <summary>True while the prop is stuck inside something and being steered out of it.</summary>
	public bool IsBuried => _buried;

	bool _buried;

	// The last few fixed ticks — recorded every tick by WatchMoveJumps, dumped when we become buried.
	readonly (Vector3 Pos, PhysicsMode Mode, bool Pressing, bool Climbing, bool Grounded)[] _history = new (Vector3, PhysicsMode, bool, bool, bool)[12];
	int _historyNext;

	// Dynamic modes (press, climb): has the body been genuinely inside something for BuriedHandBackTime? Then the
	// mode hands back to the mover, whose escape can get it out. Checked only every few ticks — it's a pose test.
	const float BuriedHandBackTime = 0.3f;
	float _dynamicBuriedTime;
	int _dynamicBuriedCheck;

	bool TooLongBuried( Vector3 pos )
	{
		if ( ++_dynamicBuriedCheck % 3 != 0 )
			return false;

		_hull.Refresh( _collider, Scene.PhysicsWorld );
		if ( _hull.Ready && PoseBlocked( pos, WorldRotation ) )
			_dynamicBuriedTime += Time.Delta * 3f;
		else
			_dynamicBuriedTime = 0f;

		if ( _dynamicBuriedTime < BuriedHandBackTime )
			return false;
		_dynamicBuriedTime = 0f;
		return true;
	}

	// Mover, buried: steer out by input, nothing else.
	void StepBuried( ref Vector3 pos, Rotation rot, bool controlled )
	{
		if ( !_buried )
		{
			_buried = true;
			LogBuried( pos, rot );
		}

		_velocity = Vector3.Zero;
		IsGrounded = false;
		_groundObject = null;
		if ( !controlled )
			return;

		var move = Input.AnalogMove.ClampLength( 1f );
		var view = _orbit.IsValid() ? _orbit.Angles.ToRotation() : Rotation.FromYaw( MoveYaw );
		var dir = view.Forward * move.x + view.Left * move.y;
		float vertical = (Input.Down( "jump" ) ? 1f : 0f) - (Input.Down( "duck" ) ? 1f : 0f);
		dir += Vector3.Up * vertical;
		if ( PerfScenario.DriveProp( GameObject, BuriedEscapeSpeed ) is Vector3 scripted && !scripted.IsNearZeroLength )
			dir = scripted.Normal; // the scripted route driver (tests) steers out too
		_jumpQueued = false;

		pos += dir.ClampLength( 1f ) * BuriedEscapeSpeed * Time.Delta;
	}

	void LogBuried( Vector3 pos, Rotation rot )
	{
		// What are we inside?
		var trace = MoveTrace;
		var inside = string.Join( ", ", Scene.PhysicsWorld.Bodies
			.Where( b => b.GameObject.IsValid() && b.GameObject.Root != GameObject
				&& _hull.LocalBounds.Transform( new Transform( pos, rot ) ).Overlaps( b.GetBounds() ) )
			.Select( b => $"{b.GameObject.Name} ({b.BodyType}{(b.Shapes.Any( s => s.IsMeshShape ) ? ", mesh" : "")})" )
			.Distinct().Take( 6 ) );

		var history = string.Join( "\n", Enumerable.Range( 0, _history.Length )
			.Select( i => _history[(_historyNext + i) % _history.Length] )
			.Where( e => e.Pos != default )
			.Select( e => $"    {e.Pos} {e.Mode} pressing={e.Pressing} climbing={e.Climbing} grounded={e.Grounded}" ) );

		var overlapping = _hull.OverlapReport( Scene, trace, GameObject, MoveIgnoreTags, new Transform( pos, rot ) );
		Log.Warning( $"[buried] {GameObject.Name} ({DisguiseSource}) is stuck inside geometry at {pos} — steer out with " +
			$"WASD/Space/Ctrl. Overlapping: {overlapping}. Near: {inside}. Last ticks (oldest first):\n{history}" );
	}
}
