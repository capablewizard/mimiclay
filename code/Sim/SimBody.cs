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
	/// <summary>The walker had a move wish last tick. What a remote copy GUESSES from: driving, it keeps commanding what it
	/// commanded; not driving, it wants to stand — however fast something is shoving it (guessing from the velocity drove a
	/// shoved standing prop along at the push speed, up to 52u ahead of the host, and the pusher's own prediction followed
	/// that copy right into the prop's drawn body: 49u of visual penetration measured, user 2026-11-05).</summary>
	public bool Driving;
	/// <summary>The walker's move wish last tick (u/s, world) — its real last INPUT, carried in snapshots so a remote copy repeats it
	/// (Rocket League's "run physics using previous player input"): guessing from the COMMANDED velocity instead under-pushed —
	/// against a contact the command is already what the contact left, not what the player is pressing.</summary>
	public Vector2 Wish;
	public byte StillTicks;
	public ushort SinceJumpTicks;
	/// <summary>How hard the walker is pushing against something, 0..<see cref="PushScale"/> (HiderController.Sim — a
	/// continuous level, not an on/off switch, so replays don't flip between two accelerations).</summary>
	public ushort PushLevel;
	public const ushort PushScale = 1000;
	/// <summary>The velocity the controller set last tick (what contacts took off it is the push-back).</summary>
	public Vector3 Commanded;
	/// <summary>A held body (SimHoldDriver) has had its holder's first target: from then on an input with no target is
	/// the holder letting go — before it, it's that input not having arrived yet (the host, half a round trip after the
	/// grab), and the body waits where it is.</summary>
	public bool Holding;

	// ── Climbing (HiderController.SimClimb) — a mode of the walk: every number the climb keeps between ticks ──
	/// <summary>Stuck to a wall or ceiling (the climb runs in place of the walk).</summary>
	public bool Climbing;
	/// <summary>The surface's smoothed normal (away from it), and the last point it was touched (an outer edge pulls toward it).</summary>
	public Vector3 ClimbN, ClimbP;
	/// <summary>Ticks without any surface; ticks left of stepping over a bump; ticks a gated reading has lasted; corner cooldown ticks left.</summary>
	public byte LostTicks, StepOverTicks, GateTicks, CornerCool;
	/// <summary>The surface the last inner corner took us off (zero = none) and the keys held then (Move ×127, vertical).</summary>
	public Vector3 CornerLeft;
	public sbyte CornerMoveX, CornerMoveY, CornerVertical;
	/// <summary>Under a ceiling reached up a wall: the way out from that wall (Space goes this way). Zero = none.</summary>
	public Vector3 CeilingOut;
	/// <summary>Corner carry: the travel direction turned over the last corner (zero = none), the surface we left, the keys held then.</summary>
	public Vector3 CarryDir, CarryFrom;
	public sbyte CarryMoveX, CarryMoveY;
	/// <summary>The climber's last view yaw and Space/Ctrl (what a remote copy repeats while climbing; its move keys ride in
	/// <see cref="Wish"/>, ×127).</summary>
	public float ClimbYaw;
	public sbyte ClimbVertical;
	/// <summary>Ticks left of the Space press buffer (an air grab wants it), and of the let-go cooldown (only a fresh press grabs).</summary>
	public byte SpaceBuffer, LetGoCool;
	/// <summary>A mantle's guided carry over the lip: ticks left, the way in, the root height on top; ClimbOn = Space held on
	/// from it (a wall ahead is grabbed straight away).</summary>
	public byte MantleTicks;
	public Vector2 MantleDir;
	public float MantleZ;
	public bool ClimbOn;

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
	/// <summary>With <see cref="Hold"/>: the ROOT pose this player wants the prop it's carrying at (<see cref="SimHoldDriver"/>).</summary>
	public Vector3 HoldPos;
	public Rotation HoldRot;

	/// <summary>The raw move keys (x forward, y left, -1..1, in steps of 1/127) — the climb maps them by the view.</summary>
	public Vector2 Move;
	/// <summary>The camera's yaw (degrees, steps of 0.01) — what the climb's keys and an air grab without steering go by.</summary>
	public float ViewYaw;

	/// <summary>Jump = Space PRESSED this tick; JumpHeld = held (the climb's "up", a grab's intent); Duck = Ctrl held; LetGo = Shift
	/// pressed (lets go of a climb).</summary>
	public const byte Jump = 1, Duck = 2, Hold = 4, JumpHeld = 8, LetGo = 16;

	/// <summary>The wire's precision, applied where the input is made, so the owner predicts from exactly what the host gets.</summary>
	public static float QuantMove( float v ) => MathF.Round( Math.Clamp( v, -1f, 1f ) * 127f ) / 127f;
	public static float QuantYaw( float yaw )
	{
		yaw = (yaw % 360f + 540f) % 360f - 180f;
		return MathF.Round( yaw * 100f ) / 100f;
	}
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
	/// <summary>Its DRIVE — who moves it (driver, owner) and its flags — which a client predicts too (a grab, a drop:
	/// SimWorld.PredictHold / PredictRelease), so it rolls back with the body (only meaningful when <see cref="DriveKnown"/>;
	/// history states carry it, snapshots don't).</summary>
	public byte Driver;
	public Guid Owner;
	public SimBodyFlags Flags;
	public SimShapeKind Shape;
	public bool DriveKnown;
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
	// The offset's own velocity: corrections fade as a critically damped spring (SimWorld.FadeCorrections), so a fold moves the
	// drawn pose by position only and its speed stays continuous - no one-frame lurch at the start of every fade.
	internal Vector3 OffVel;
	// Source-switch bookkeeping (SimWorld.UpdatePresentationSource): since when the other source has been wanted (-1 = not),
	// and until when the offset fades on the gentler switch spring.
	internal float WantSwitchSince = -1f;
	internal float SoftFadeUntil;
	// The drive last seen by UpdatePresentationSource, and when it changed: a switch that follows a change of drive is real, not
	// a flicker, and is taken at once.
	internal byte SeenDriver;
	internal Guid SeenOwner;
	internal float DriveChangedAt = -999f;
	/// <summary>Another player's walker is DRAWN through a critically damped tracker of its copy's pose (SimWorld.FadeCorrections):
	/// position and velocity stay continuous across the copy's nudges and restores, which the position-only fade could
	/// not do (a copy restored to a stopped host stops dead: 3000 u/s drawn-speed spikes in the trace; the tracker: none).</summary>
	internal Vector3 TrackPos, TrackVel;
	internal bool Tracking;
	internal Rotation RotOffset = Rotation.Identity;

	// Client: the guessed input for someone else's body, and the newest rest pose the host sent.
	internal SimInput RemoteGuess;
	internal SimBodyState? Rest;

	// Client: the host's recent poses of a body we don't drive (snapshots and rests, by host tick). Someone else's prop,
	// and a loose body nobody here is touching, are SHOWN from these, interpolated a few ticks behind the newest — not from
	// our prediction of them (see SimWorld.RemotePose / ShownFromAuthority).
	internal readonly List<(int tick, Vector3 p, Rotation r)> Authority = new();

	// Client: which source this body was drawn from last frame (the switch between the two is blended — see
	// SimWorld.UpdatePresentationSource), and whether something of OURS has touched it since it last came to rest (our
	// walker standing on or pushing it, our hunter's push, our hold or throw): a touched loose body is drawn from our
	// prediction, which answers us at once; an untouched one from the host's poses, which never rubber-band.
	internal bool ShownFromAuth;
	internal bool LocalTouched;
	// Client: WE let go of this body (PredictRelease) — our throw, drawn from our own prediction until it is calm and agrees
	// with the host's poses (SimWorld.UpdatePresentationSource), whoever drives it now. ThrownTick: the release tick.
	internal bool ThrownHere;
	internal int ThrownTick;
	// Client: the host's latest state of this body says its driver is steering it (SimCtrl.Driving) - SimWorld.NoteAuthority.
	internal bool HostDriving;
	// Client: drawn from the copy since ANOTHER player let go of it (SimWorld.ApplyRedrive, ReleasePredict) - its switch fades
	// on the quick ReleaseSpring so it has caught up before it lands. SoftSpring: the stiffness the soft window uses (0 = the
	// lead-scaled switch spring).
	internal bool RemoteRelease;
	internal float SoftSpring;
	/// <summary>Client: the last tick something of ours was against this OTHER player's walker (SimWorld.NoteLocalContacts).
	/// Recently touched, it is drawn from our prediction and not nudged to the host's poses (SimWorld.ShownFromAuthority):
	/// a prop we push moves under our push at once, instead of its drawn body trailing our copy of it by the round trip
	/// plus the remote display delay (42u measured — our own prop drawn 45u INTO it, user 2026-11-05).</summary>
	internal int TouchedTick = -1_000_000; // (NOT int.MinValue: "Tick - TouchedTick" overflowed negative and read as "just touched" for every untouched walker)

	// Client: the tick this machine released it by a predicted push, until the host confirms (-1 = none pending).
	internal int PredictedRelease = -1;

	// Client: a push this machine's hunter made on it (reported to the host), applied at that tick in every replay too
	// (see SimWorld.PredictPush).
	internal (int tick, Vector3 velocity)? PredictedPush;

	// Client: this machine changed the body's driver itself, ahead of the host (a grab predicted — SimWorld.PredictHold):
	// what it was before, to go back to if the host never confirms.
	internal (int tick, byte driver, Guid owner, SimBodyFlags flags, SimShapeKind shape)? PredictedDrive;

	// A held body whose holder's inputs have stopped carrying a target (SimHoldDriver saw the release tick): the host makes
	// it loose from here, not from when the drop message arrived — that was a lead earlier than the release the holder
	// predicted (measured: a 71u correction on every throw).
	internal bool HoldReleased;

	/// <summary>Lying down and DRIVEN by its wearer (SimDownDriver): its shapes' friction is lowered so a flat-based prop slides
	/// instead of tipping; what they had, to put back once it rests (per shape, for the shape build it was taken from).</summary>
	internal float[] SavedFriction;
	internal Surface[] SavedSurface;
	internal int SavedFrictionVersion = -1;
	internal bool LowFriction;

	// Client: every change of drive this body had, by the tick it took effect — re-applied at that tick in a replay, so a
	// replay reaching back across a drop doesn't re-simulate the swing as a loose body (measured: 121u corrections at every
	// throw with the switch applied "now" only). See SimWorld.LogDrive.
	internal readonly List<(int tick, byte driver, Guid owner, SimBodyFlags flags, SimShapeKind shape)> DriveLog = new();

	internal SimWorld World;

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
			Anchored = (Spec.Flags & SimBodyFlags.Anchored) != 0, AnchorKnown = true,
			Driver = Spec.Driver, Owner = Spec.Owner, Flags = Spec.Flags, Shape = Spec.Shape, DriveKnown = true };
	}

	public void Restore( in SimBodyState s )
	{
		if ( Phys is null || !Phys.IsValid() )
			return;
		// The drive first (a history state): who moved it then, with its flags — before the anchored flag and the velocity.
		if ( s.DriveKnown && World is not null && (s.Driver != Spec.Driver || s.Owner != Spec.Owner || s.Flags != Spec.Flags || s.Shape != Spec.Shape) )
			World.ApplyRedrive( this, s.Driver, s.Owner, s.Flags, s.Shape );
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
