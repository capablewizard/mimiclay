using System;

namespace Mimiclay.Sim;

// Phase 0 of the rollback-physics integration (see the networked-physics-authority memory): the shared simulation
// service. Nothing in the game registers bodies yet — RollbackRig (code/Debug) and the prop mirror probe are its only
// users — so it changes no behaviour. See SimWorld for the design.

/// <summary>What a body's collision comes from.</summary>
public enum SimShapeKind : byte
{
	/// <summary>An axis-aligned box of <see cref="SimBodySpec.HalfExtents"/> (debug rigs).</summary>
	Box = 0,
	/// <summary>Copied from the shapes of the scene object <see cref="SimBodySpec.Source"/> — re-copied whenever that object's
	/// collider rebuilds (an SdfCollider's BuildSerial changes), on every machine.</summary>
	FromGameObject = 1,
}

[Flags]
public enum SimBodyFlags : byte
{
	None = 0,
	/// <summary>Only turns about the vertical (pitch + roll locked) — a walking prop.</summary>
	Upright = 1,
	/// <summary>No gravity.</summary>
	NoGravity = 2,
	/// <summary>Starts asleep (a prop that's been sitting still — waking costs nothing until something touches it).</summary>
	StartAsleep = 4,
	/// <summary>The scene object it copies (<see cref="SimShapeKind.FromGameObject"/>) is SHOWN from the sim: every machine
	/// sets that object's transform from <see cref="SimWorld.VisualPose"/> each frame (the host also stops the engine
	/// syncing its transform). Its scene collider follows, so traces and the scene's own bodies still meet it.</summary>
	Present = 8,
	/// <summary>Held in place (keyframed — it blocks, but nothing moves it and nothing wakes it) until the host releases
	/// it: a prop-hunt decoy waits for a shot or a push. Without this every decoy was a sleeping dynamic body, and the
	/// keyframed cabinet doors it touched woke the lot — 35 of 70 fell off walls and doors (magnets, knobs, handles).
	/// The host reports pushes against anchored bodies (<see cref="SimWorld.AnchoredPushed"/>).</summary>
	Anchored = 16,
	/// <summary>With <see cref="Upright"/>: doesn't turn about the vertical either — slides without rotating (a pushed
	/// decoy, as the old push code locked it; measured 43° of spin from a push without it).</summary>
	NoTurn = 32,
}

/// <summary>Everything every machine needs to create a body the same way. Sent reliably by the host when the body is
/// registered (and to late joiners).</summary>
public struct SimBodySpec
{
	/// <summary>Network-wide identity: a scene object's id, or any id the registrant derives the same on every machine.</summary>
	public Guid Key;
	/// <summary>The connection whose input drives it (<see cref="Guid.Empty"/>: nobody — a loose body).</summary>
	public Guid Owner;
	/// <summary>Which registered <see cref="ISimDriver"/> moves it (0 = none, a plain rigid body).</summary>
	public byte Driver;
	public SimShapeKind Shape;
	public SimBodyFlags Flags;
	public Vector3 HalfExtents;
	/// <summary>The scene object whose collider it copies (<see cref="SimShapeKind.FromGameObject"/>).</summary>
	public Guid Source;
	/// <summary>kg; 0 = the engine's own from the shapes.</summary>
	public float Mass;
	public Transform Pose;
}

/// <summary>One tick of one player's input. Fixed size; extended as controllers move into the simulation.</summary>
public struct SimInput
{
	/// <summary>Horizontal movement the player wants (u/s, world space).</summary>
	public Vector2 Wish;
	public byte Buttons;

	public static readonly SimInput None = default;
}

/// <summary>The state rollback saves and restores per body.</summary>
public struct SimBodyState
{
	public Vector3 P;
	public Rotation R;
	public Vector3 V;
	public Vector3 W;
	public bool Sleeping;
}

/// <summary>Moves a body from its owner's input, the same way on the host and in every client replay. Registered on
/// <see cref="SimWorld"/> under an id that every machine agrees on.</summary>
public interface ISimDriver
{
	/// <summary>Before the step: apply <paramref name="input"/> to <paramref name="body"/>.</summary>
	void Drive( SimBody body, in SimInput input, int tick );

	/// <summary>Client side: the input to assume for someone ELSE's body we can't see the input of, given its newest
	/// authoritative state.</summary>
	SimInput Guess( SimBody body, in SimBodyState latest );
}

/// <summary>A body in the simulation world.</summary>
public sealed class SimBody
{
	public SimBodySpec Spec;
	/// <summary>Compact id the host assigned (snapshots address bodies by it).</summary>
	public ushort Id;
	public PhysicsBody Phys;
	public ISimDriver Driver;

	/// <summary>The FromGameObject collider version its shapes were copied at.</summary>
	internal int ShapeVersion = -1;
	internal bool ShapesReady;

	// Presentation: the poses at the last two ticks and the fading correction (see SimWorld.VisualPose).
	internal Transform Prev, Cur;
	internal Vector3 PosOffset;
	internal Rotation RotOffset = Rotation.Identity;

	// Client: the guessed input for someone else's body, and the newest rest pose the host sent.
	internal SimInput RemoteGuess;
	internal SimBodyState? Rest;

	// Host: ticks since it fell asleep (snapshots keep sending a body for a while after, until the reliable rest lands).
	internal int AsleepTicks = int.MaxValue;
	internal bool WasSleeping;

	// Host: motion to give it once its shapes exist (a split piece registered before its collider built).
	internal (Vector3 v, Vector3 w)? PendingMotion;

	// Presentation: the pose last written to the scene object (only rewritten when it changes).
	internal Transform? Presented;

	public Guid Key => Spec.Key;
	public bool IsLocalOwned => Spec.Owner != Guid.Empty && Spec.Owner == (Connection.Local?.Id ?? Guid.Empty);

	public SimBodyState Capture()
	{
		if ( Phys is null || !Phys.IsValid() )
			return default;
		return new SimBodyState { P = Phys.Position, R = Phys.Rotation, V = Phys.Velocity, W = Phys.AngularVelocity, Sleeping = Phys.Sleeping };
	}

	public void Restore( in SimBodyState s )
	{
		if ( Phys is null || !Phys.IsValid() )
			return;
		Phys.Transform = new Transform( s.P, s.R );
		Phys.Velocity = s.V;
		Phys.AngularVelocity = s.W;
		Phys.Sleeping = s.Sleeping;
	}
}
