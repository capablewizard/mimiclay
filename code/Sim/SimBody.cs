using System;
using System.Collections.Generic;

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
	/// <summary>Built by a component on <see cref="SimBodySpec.Source"/> implementing <see cref="ISimShapeSource"/> — a
	/// prop pawn's floating move hull (HiderController.Sim). Every machine derives it from its own copy of the pawn.</summary>
	FromSource = 2,
}

/// <summary>A component that builds its object's sim shapes itself (<see cref="SimShapeKind.FromSource"/>).</summary>
public interface ISimShapeSource
{
	/// <summary>The shape's version: when it changes, the sim rebuilds the shapes.</summary>
	int SimShapeVersion { get; }

	/// <summary>Add the shapes to <paramref name="body"/> (body space = the source object's space). False if not ready yet.</summary>
	bool BuildSimShapes( PhysicsBody body );
}

/// <summary>A driven body's controller state — saved and restored with its physics state on every rollback, and sent in
/// snapshots. Fixed fields, so it packs small and stays a struct (history keeps one per body per tick).
/// (Phase 2a: the prop walk's — HiderController.Sim.)</summary>
public struct SimCtrl
{
	public bool Grounded, Parked, AirFromJump;
	public byte StillTicks;
	public ushort SinceJumpTicks;
	/// <summary>How hard the walker is pushing against something, 0..<see cref="PushScale"/> (HiderController.Sim — a
	/// continuous level, not an on/off switch, so replays don't flip between two accelerations).</summary>
	public ushort PushLevel;
	public const ushort PushScale = 1000;
	/// <summary>The velocity the controller set last tick (what contacts took off it is the push-back).</summary>
	public Vector3 Commanded;

	public const ushort Long = 9999;
	public static readonly SimCtrl Initial = new() { SinceJumpTicks = Long };
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
	/// <summary>Driven, but nobody's input ever reaches it (a host's bot): clients guess NO input for it, not "keeps going
	/// at its velocity" — that had a bot a player was pushing walk off on its own in every replay (12–41u corrections).</summary>
	Inert = 64,
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
	/// <summary>Degrees the player turned their prop about the vertical this tick (mouse).</summary>
	public float Turn;

	public const byte Jump = 1, Duck = 2;
	public bool Has( byte button ) => (Buttons & button) != 0;

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
	/// <summary>A driven body's controller state (only meaningful when <see cref="HasCtrl"/>).</summary>
	public SimCtrl Ctrl;
	public bool HasCtrl;
	/// <summary>Whether it was ANCHORED (see <see cref="SimBodyFlags.Anchored"/>) — a client predicts releases, so the flag rolls
	/// back with the body (only meaningful when <see cref="AnchorKnown"/>).</summary>
	public bool Anchored;
	public bool AnchorKnown;
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

	// Client: the host's recent poses of a body someone ELSE drives (snapshots and rests, by host tick). It is SHOWN from
	// these, interpolated a few ticks behind the newest — not from our guess of it (see SimWorld.RemotePose).
	internal readonly List<(int tick, Vector3 p, Rotation r)> Authority = new();

	// Client: the tick this machine released it by a predicted push, until the host confirms (-1 = none pending).
	internal int PredictedRelease = -1;

	// Client: a push this machine's hunter made on it (reported to the host), applied at that tick in every replay too
	// (see SimWorld.PredictPush).
	internal (int tick, Vector3 velocity)? PredictedPush;

	/// <summary>A driven body's controller state (see <see cref="SimCtrl"/>) — rolled back with the body.</summary>
	public SimCtrl Ctrl = SimCtrl.Initial;

	/// <summary>What the driver is TRYING to move it at this tick (a walk's command) — a blocked body's velocity is 0, so pushes
	/// against anchored bodies read this (SimWorld.DetectAnchoredPushes).</summary>
	public Vector3 Intent;

	/// <summary>Whatever the registrant keeps with it (the walk driver: its HiderController).</summary>
	public object User;

	// Host: ticks since it fell asleep (snapshots keep sending a body for a while after, until the reliable rest lands).
	internal int AsleepTicks = int.MaxValue;
	internal bool WasSleeping;

	// Host: motion to give it once its shapes exist (a split piece registered before its collider built).
	internal (Vector3 v, Vector3 w)? PendingMotion;

	// Presentation: the pose last written to the scene object (only rewritten when it changes).
	internal Transform? Presented;

	public Guid Key => Spec.Key;
	public bool IsLocalOwned => Spec.Owner != Guid.Empty && Spec.Owner == (Connection.Local?.Id ?? Guid.Empty);

	/// <summary>Driven by someone else's input (another player's prop; the host's bots and released walkers, on a client):
	/// a body this machine can only guess at.</summary>
	public bool IsRemoteDriven => Driver is not null && Spec.Owner != Guid.Empty && !IsLocalOwned;

	public SimBodyState Capture()
	{
		if ( Phys is null || !Phys.IsValid() )
			return default;
		return new SimBodyState { P = Phys.Position, R = Phys.Rotation, V = Phys.Velocity, W = Phys.AngularVelocity, Sleeping = Phys.Sleeping, Ctrl = Ctrl, HasCtrl = Driver is not null,
			Anchored = (Spec.Flags & SimBodyFlags.Anchored) != 0, AnchorKnown = true };
	}

	public void Restore( in SimBodyState s )
	{
		if ( Phys is null || !Phys.IsValid() )
			return;
		// Anchored first: switching motion off clears the velocity, so it has to come before the velocity is set.
		if ( s.AnchorKnown && s.Anchored != ((Spec.Flags & SimBodyFlags.Anchored) != 0) )
		{
			Spec.Flags = s.Anchored ? Spec.Flags | SimBodyFlags.Anchored : Spec.Flags & ~SimBodyFlags.Anchored;
			if ( !s.Anchored )
				Spec.Flags |= (Spec.Flags & SimBodyFlags.Upright) != 0 ? SimBodyFlags.NoTurn : 0; // released by a push
			Phys.MotionEnabled = ShapesReady && !s.Anchored;
			Phys.Locking = (Spec.Flags & SimBodyFlags.Upright) == 0 ? default
				: new PhysicsLock { Pitch = true, Roll = true, Yaw = (Spec.Flags & SimBodyFlags.NoTurn) != 0 };
		}
		Phys.Transform = new Transform( s.P, s.R );
		Phys.Velocity = s.V;
		Phys.AngularVelocity = s.W;
		Phys.Sleeping = s.Sleeping;
		if ( s.HasCtrl )
			Ctrl = s.Ctrl;
	}
}
