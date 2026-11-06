using System;
using System.Linq;

namespace Mimiclay.Sim;

/// <summary>
/// A player's (or bot's) body lying DOWN — let go of by a hunter, thrown, tumbled — with its wearer's own input: the WALK,
/// overturned (HiderController.SimStepDown). Thrown, it tumbles as a body, the move keys adding to its velocity (never
/// braking it), until it is calm a moment — no spin, not falling, no faster than the walk; settled, the
/// move keys slide it at the walk's speed and acceleration and the mouse turns it about the vertical, its rotation held
/// as the walker's is, in whatever pose it came to rest in; SPACE rights it: the prop's own hop and a righting spin toward
/// upright, the host making it a walker once it's within <see cref="UprightDeg"/> and nearly still (<see cref="Done"/>)
/// or after <see cref="MaxRiseTicks"/> — the first jump IS the flip onto its feet. A bot (<see cref="SimBodyFlags.Inert"/>,
/// no input) rights itself once it has lain still, without the hop. (User, 2026-11-05: "feel very similar to the normal
/// movement, just overturned until we jump".)
/// Input-driven, so the wearer predicts it exactly; everyone else sees it from the host's poses. Engine gravity (the spec).
/// </summary>
public sealed class SimDownDriver : ISimDriver
{
	public const byte Id = 4;
	public static readonly SimDownDriver Instance = new();

	/// <summary>Debug: every Space input the down driver consumed, and what became of it (taken as the flip, or lost — the
	/// body not on the ground that tick). Read by test scripts; on while <see cref="LogJumps"/>.</summary>
	public static readonly System.Collections.Generic.List<string> JumpLog = new();
	public static bool LogJumps { get; set; }

	internal const int SettleTicks = (int)(0.16f * SimWorld.T + 0.5f);      // calm this long (0.16 s: no spin, not falling, not faster than the walk) → settled: the overturned walk takes over
	internal const int UnsettleTicks = (int)(0.2f * SimWorld.T + 0.5f);   // settled but off the ground this long (0.2 s) (a fall) → tumbling again, to land as a body
	internal const int InertRestTicks = (int)(0.5f * SimWorld.T + 0.5f);  // a bot, settled this long (0.5 s), rights itself
	internal const int JumpBufferTicks = (int)(0.2f * SimWorld.T + 0.5f); // a Space press waits this long (0.2 s) for the body to be on the ground (HiderController.SimStepDown)
	const float Rate = 8f;                   // 1/s: righting spin = tilt × Rate…
	const float MaxSpin = 10f;               // …capped (rad/s)
	const float YawDamp = 6f;                // 1/s: the spin about the vertical dies out while rising
	public const float UprightDeg = 3f;
	public const float RestSpin = 1f;        // rad/s
	public const int MaxRiseTicks = (int)(1.2f * SimWorld.T + 0.5f);      // 1.2 s

	public void Drive( SimBody body, in SimInput input, int tick )
	{
		if ( body.User is not HiderController h || !h.IsValid() )
		{
			var go = Game.ActiveScene?.Directory.FindByGuid( body.Spec.Source );
			h = go.IsValid() ? go.Components.Get<HiderController>() : null;
			body.User = h;
		}
		if ( h.IsValid() )
			h.SimStepDown( body, input, tick );
	}

	/// <summary>Rising: spin toward upright, the tumble damped out (the host makes it a walker once <see cref="Done"/>).</summary>
	internal static void Right( PhysicsBody pb )
	{
		var up = pb.Rotation.Up;
		var axis = Vector3.Cross( up, Vector3.Up );
		float sin = axis.Length, cos = Vector3.Dot( up, Vector3.Up );
		float tilt = MathF.Atan2( sin, cos );
		var righting = sin > 1e-4f ? axis / sin * MathF.Min( tilt * Rate, MaxSpin ) : Vector3.Zero;
		float yawSpin = Vector3.Dot( pb.AngularVelocity, Vector3.Up ) * MathF.Exp( -YawDamp * SimWorld.Dt );
		pb.AngularVelocity = righting + Vector3.Up * yawSpin;
	}

	/// <summary>Settled, the body's rotation is held (as the walker's: pitch and roll locked, the mouse turning it) and its
	/// shapes have NO friction — the walk commands its velocity each tick, and floor friction only fought that (a kettle
	/// sliding on its base lost 40 u/s to it, and before the lock tipped over). Tumbling, it turns freely, and its friction is
	/// its own EXCEPT while the keys drive it (<paramref name="frictionless"/>): friction is the one thing that couples
	/// spin to slide (it spins a pushed ball up, and turns spin into speed), so without it our push only adds SLIDE — a ball
	/// keeps rolling at the pace the throw gave it and slides along under the keys (user, 2026-11-05); let go of the keys
	/// and the floor's friction reconciles the two as it always would. Frictionless, the shapes also wear
	/// <see cref="SlideSurface"/>: the clay surface's rolling resistance (what stops a rolling ball — the engine's own, in the
	/// surface asset) is a bare torque once friction isn't coupling the spin to the slide, and killed a ball's spin in 1.4 s
	/// (measured) — the very spin we're keeping. Rising, both are its own.</summary>
	internal static void Settle( SimBody body, PhysicsBody pb, bool settled, bool frictionless = false )
	{
		if ( pb.Locking.Pitch != settled )
			pb.Locking = settled ? new PhysicsLock { Pitch = true, Roll = true, Yaw = true } : default;
		if ( settled || frictionless )
		{
			if ( body.LowFriction && body.SavedFrictionVersion == body.ShapeVersion )
				return;
			var slide = SlideSurface;
			body.SavedFriction = pb.Shapes.Select( s => s.Friction ).ToArray();
			body.SavedSurface = pb.Shapes.Select( s => s.Surface ).ToArray();
			body.SavedFrictionVersion = body.ShapeVersion;
			foreach ( var s in pb.Shapes )
			{
				if ( slide is not null )
					s.Surface = slide;
				s.Friction = 0f;
			}
			body.LowFriction = true;
			return;
		}
		if ( !body.LowFriction )
			return;
		body.LowFriction = false;
		if ( body.SavedFriction is null || body.SavedFrictionVersion != body.ShapeVersion )
			return; // rebuilt since: the new shapes came with their own friction and surface
		int i = 0;
		foreach ( var s in pb.Shapes )
		{
			if ( i >= body.SavedFriction.Length )
				break;
			if ( body.SavedSurface is { } saved && i < saved.Length && saved[i] is not null )
				s.Surface = saved[i];
			s.Friction = body.SavedFriction[i++];
		}
	}

	// The clay surface without rolling resistance (Assets/surfaces/clay_slide.surface); null until the asset system has it.
	static Surface _slide;
	static Surface SlideSurface => _slide ??= Surface.FindByName( "clay_slide" );

	/// <summary>Rising, and upright and still enough to walk — or out of time.</summary>
	public static bool Done( SimBody b )
	{
		if ( b.Phys is null || !b.Phys.IsValid() )
			return true;
		if ( !b.Ctrl.AirFromJump )
			return false;
		var a = b.Phys.Rotation.Angles();
		bool upright = MathF.Abs( a.pitch ) < UprightDeg && MathF.Abs( a.roll ) < UprightDeg;
		return (upright && b.Phys.AngularVelocity.Length < RestSpin) || b.Ctrl.StillTicks >= MaxRiseTicks;
	}

	// Someone else's: it lies there (its sim copy here is nudged to the host's state each snapshot anyway).
	public SimInput Guess( SimBody body, in SimBodyState latest ) => SimInput.None;
}
