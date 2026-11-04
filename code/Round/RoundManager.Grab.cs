using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// "Grab props" (RoundSettings.GrabProps): the round hosts a PropClaims (RoundManagerSpawner puts one on the manager's
// object) for the INSPECT grab alone — claims stay closed, so nobody possesses or sculpts anything; hunters can only
// pick props up during the Hunt, carry and turn them, and put them down. See PropClaims.Grab for the rules, and
// IPropClaimHost.GrabSandbox for what's switched off.
//
// Held DECOYS are lent to their holder's machine and held with the same HeldBodyDriver as players' bodies (see "Held
// decoys" below); players' bodies are held by their own controller (HiderController), lent to the holding hunter's
// machine (mimi_grab_borrow, default on) or the creative way on their wearer's. Either way: same hold, same machine.
public sealed partial class RoundManager : IPropClaimHost
{
	bool IPropClaimHost.ClaimsAllowed => false;
	bool IPropClaimHost.GrabAllowed => Settings.GrabProps && Phase == RoundPhase.Hunt && !LobbyReturn.Active;
	bool IPropClaimHost.GrabPlayersAllowed => Settings.GrabProps;
	bool IPropClaimHost.GrabSandbox => false;
	bool IPropClaimHost.GrabBorrowsBodies => GrabBorrowBodies;

	/// <summary>Held props (players' bodies; decoys; creative's released scenery): 1 (default) = LENT to the hunter holding them — the hunter's machine simulates the
	/// hold, no lag at any ping, the same as a held decoy (see PropClaims.BorrowedBodies); 0 = the creative way, the
	/// wearer simulates and the host relays the hold to them (one extra round-trip for the hunter). For A/B-ing.</summary>
	[ConVar( "mimi_grab_borrow", Help = "Prop hunt grab: 1 = held players' bodies are lent to the holding hunter, 0 = wearer simulates (creative way)." )]
	public static bool GrabBorrowBodies { get; set; } = true;

	GameObject IPropClaimHost.PropPrefab
		=> RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.PropPrefab : null;

	// The holder's pawn, from the roster's host-stamped PawnId.
	GameObject IPropClaimHost.ClaimantPawn( Connection c )
		=> Players.TryGetValue( c.Id, out var row ) && row.PawnId != Guid.Empty ? Scene.Directory.FindByGuid( row.PawnId ) : null;

	void IPropClaimHost.OnClaimGranted( Connection c, GameObject hunterPawn, HiderController prop ) { } // claims never open

	// ── Held decoys: lent to their holder ───────────────────────────────────────────────────────────────────
	// A held decoy is handled exactly like a held player's body (PropClaims.BorrowedBodies): its HOLDER's machine
	// simulates it, with the same hold (HeldBodyDriver — joint in free air, velocity chase in contact, no friction), from
	// the target its own hunter computes that frame (PropClaims.SetLocalGrabTarget). No round-trip for the hunter at any
	// ping, and nothing a held decoy does in the hand differs from a held player.
	//
	// A decoy isn't a network object, so the lend is a registry (LentDecoys: decoy → the connection simulating it) and the
	// simulator streams the pose itself (BroadcastShotPose — everyone else, the host included, follows it). After the drop
	// the holder keeps simulating it — a throw still flies — until it's at rest, then reports the exact resting pose
	// (FinishLentDecoy); the host takes it back from that pose, so nobody sees a correction. The host holds its own held
	// decoys directly (it's their simulator anyway). Safety net: a simulator that leaves or never reports back loses it
	// after LentDecoyTimeout, from the host's own view.

	/// <summary>Decoys whose physics a CLIENT is simulating (it's holding one, or it's settling after the drop): decoy
	/// GameObject id → that connection. Host-written.</summary>
	[Sync] public NetDictionary<Guid, Guid> LentDecoys { get; private set; } = new();

	const float LentDecoyTimeout = 6f;
	const float LentRestSpeed = 4f;   // u/s under which a dropped decoy counts as still…
	const float LentRestTime = 0.25f; // …for this long, before it's handed back

	readonly HashSet<GameObject> _heldDecoys = new();                       // host: decoys it holds and simulates itself
	readonly Dictionary<GameObject, HeldBodyDriver> _decoyHolds = new();      // any machine: the hold per decoy it drives
	readonly Dictionary<Guid, RealTimeSince> _lentLetGo = new();              // host: when each lent decoy's hold ended

	// Simulator side (a client): the decoy we were lent, how long it's been still since the drop, whether we've reported
	// it, and the filter that keeps it off our own pawn.
	GameObject _simDecoy;
	RealTimeSince _simStill;
	bool _simReported;
	List<PhysicsFilter> _simFilters;

	/// <summary>Is this decoy's physics being simulated by THIS machine as its lent simulator (a client)?</summary>
	bool SimulatingLentHere( GameObject go ) => go.IsValid() && go == _simDecoy;

	/// <summary>Is this decoy lent to someone (a client simulates it)?</summary>
	bool IsLentDecoy( GameObject go ) => go.IsValid() && LentDecoys.ContainsKey( go.Id );

	/// <summary>Does the ENGINE sync this decoy's transform (a network object — scene props are, on the maps checked)?
	/// Then its owner's physics is everyone's truth, and our own pose stream (ShotProps) stays out of it.</summary>
	static bool EngineSynced( GameObject go ) => go.IsValid() && go.Network.Active;

	protected override void OnFixedUpdate()
	{
		if ( IsHostAuthority )
			DriveHeldDecoys();
		SimulateLentDecoy();
	}

	// One tick of the hold for a decoy this machine simulates.
	void DriveDecoyHold( GameObject go, Transform target )
	{
		var body = ShotBody( go, simulate: true );
		if ( !body.IsValid() )
			return;
		float mass = go.Components.Get<SdfCollider>()?.ClayMass ?? 0f;
		if ( mass > 0f && MathF.Abs( body.MassOverride - mass ) > 1e-3f )
			body.MassOverride = mass;
		body.Gravity = false;
		body.Locking = default; // held, it turns freely (a pushed decoy's upright lock doesn't apply in the hand)
		_pushedDecoys.Remove( go );

		if ( !_decoyHolds.TryGetValue( go, out var hold ) )
			_decoyHolds[go] = hold = new HeldBodyDriver();
		var contact = go.Components.Get<DecoyPushListener>();
		hold.Drive( body, target, touching: contact.IsValid() && contact.SinceContact < HeldBodyDriver.ContactWindow );
	}

	// The hold on this decoy is over here: joint gone, friction back.
	void EndDecoyHold( GameObject go )
	{
		if ( go is not null && _decoyHolds.Remove( go, out var hold ) )
			hold.Release();
	}

	// Host, fixed step: lend held decoys to their (client) holders, drive the ones it holds itself, and take back lent
	// decoys whose simulator left or went quiet.
	void DriveHeldDecoys()
	{
		var claims = PropClaims.Current;
		if ( claims.IsValid() && claims.Held.Count > 0 )
		{
			foreach ( var (holder, id) in claims.Held )
			{
				var go = Scene.Directory.FindByGuid( id );
				if ( !go.IsValid() || go.Components.Get<HiderController>().IsValid() )
					continue; // a pawn (its controller holds itself), or gone
				UnregisterSimDecoy( go ); // the hold takes over its motion (the lend path below) — it rejoins the sim when put down

				// A client's hand: lend it — its machine simulates it. Our copy just follows.
				if ( holder != Connection.Local?.Id )
				{
					if ( !LentDecoys.TryGetValue( id, out var to ) || to != holder )
					{
						LentDecoys[id] = holder;
						EndDecoyHold( go );
						_heldDecoys.Remove( go );
						_shotLoose.Remove( go );
						ShotBody( go, simulate: false );

						// A NETWORKED decoy (most are — scene props are network objects) is lent by ownership, like a
						// player's body: the engine then syncs its transform from the holder. Snapshot-only ones are
						// streamed by the holder instead (TickShotProps).
						if ( EngineSynced( go ) && Connection.All.FirstOrDefault( c => c.Id == holder ) is { } holderConn )
							foreach ( var net in PropClaims.NetworkTree( go ) )
								net.Network.AssignOwnership( holderConn );
					}
					_lentLetGo.Remove( id );
					continue;
				}

				// Our own hand.
				if ( !claims.TryGetGrabTarget( go, out var target ) )
					continue;
				LentDecoys.Remove( id );
				_heldDecoys.Add( go );
				_shotLoose.Add( go ); // streamed to clients like any loose decoy
				_shotResting.Remove( go );
				DriveDecoyHold( go, target );
			}
		}

		// Let go of (our own hand): a loose decoy again — gravity back, keeping whatever motion the hold gave it.
		_heldDecoys.RemoveWhere( go =>
		{
			if ( !go.IsValid() )
				return true;
			if ( PropClaims.IsHeldObject( go ) )
				return false;
			EndDecoyHold( go );
			var body = go.Components.Get<Rigidbody>();
			if ( body.IsValid() )
				body.Gravity = true;
			if ( _simDecoysActive )
				RegisterSimDecoy( go, awake: true, upright: false ); // back into the sim world, keeping the motion the hold gave it
			return true;
		} );

		// Lent decoys nobody holds any more are settling on their simulator; take them back if it left or went quiet.
		if ( LentDecoys.Count == 0 )
			return;
		foreach ( var (id, simulator) in LentDecoys.ToList() )
		{
			var go = Scene.Directory.FindByGuid( id );
			if ( !go.IsValid() )
			{
				LentDecoys.Remove( id );
				_lentLetGo.Remove( id );
				continue;
			}
			if ( PropClaims.IsHeldObject( go ) )
				continue;
			if ( !_lentLetGo.TryGetValue( id, out var since ) )
				_lentLetGo[id] = since = 0f;
			bool gone = Connection.All.All( c => c.Id != simulator );
			if ( gone || since > LentDecoyTimeout )
				TakeBackDecoy( go, go.WorldPosition, go.WorldRotation );
		}
	}

	// Any machine, fixed step: simulate the decoy lent to us — held (from our own target), then settling after the drop
	// until it's at rest, when we report the pose and stop. Streams ride TickShotProps.
	void SimulateLentDecoy()
	{
		var me = Connection.Local;
		GameObject lent = null;
		if ( me is not null && !IsHostAuthority )
		{
			foreach ( var (id, simulator) in LentDecoys )
			{
				if ( simulator == me.Id )
				{
					lent = Scene.Directory.FindByGuid( id );
					break;
				}
			}
		}

		if ( lent != _simDecoy )
		{
			EndSimulation();
			if ( lent.IsValid() )
			{
				_shotTargets.Remove( lent ); // a stream pose from before the lend must not pull it back later
				_simDecoy = lent;
				_simReported = false;
				_simStill = 0f;
				var mine = Scene.GetAllComponents<HunterController>().FirstOrDefault( h => !h.IsProxy && !IsBotPawn( h.GameObject ) );
				_simFilters = PropClaims.FilterAgainst( lent, mine?.GameObject );
			}
		}
		if ( !_simDecoy.IsValid() )
			return;

		// A networked decoy is ours to simulate only once its ownership has landed (the registry and the ownership change
		// travel separately). Until then the engine is still moving our copy from the host's.
		if ( _simDecoy.IsProxy )
			return;

		var claims = PropClaims.Current;
		if ( claims.IsValid() && claims.TryGetGrabTarget( _simDecoy, out var target ) )
		{
			_simReported = false;
			_simStill = 0f;
			DriveDecoyHold( _simDecoy, target );
			return;
		}

		// Let go of: a free body under gravity until it's been still for a moment, then report where it rests (once).
		EndDecoyHold( _simDecoy );
		ClearSimFilters();
		var body = ShotBody( _simDecoy, simulate: true );
		if ( !body.IsValid() || _simReported )
			return;
		if ( body.Velocity.Length > LentRestSpeed || body.AngularVelocity.Length > 0.2f )
			_simStill = 0f;
		if ( _simStill < LentRestTime )
			return;
		_simReported = true;
		FinishLentDecoy( _simDecoy, _simDecoy.WorldPosition, _simDecoy.WorldRotation );
	}

	// The lend is over here (taken back, or gone): back to following the host's stream.
	void EndSimulation()
	{
		ClearSimFilters();
		if ( _simDecoy.IsValid() )
		{
			EndDecoyHold( _simDecoy );
			ShotBody( _simDecoy, simulate: false );
			_shotTargets[_simDecoy] = _simDecoy.WorldTransform; // hold where our simulation left it until the host's poses land
		}
		_simDecoy = null;
	}

	void ClearSimFilters()
	{
		if ( _simFilters is null )
			return;
		foreach ( var filter in _simFilters )
			if ( filter.IsValid() )
				filter.Destroy();
		_simFilters = null;
	}

	/// <summary>Simulator → host: the decoy it was lent is at rest here — take it back.</summary>
	[Rpc.Host]
	void FinishLentDecoy( GameObject go, Vector3 position, Rotation rotation )
	{
		var c = Rpc.Caller ?? Connection.Local;
		if ( c is null || !go.IsValid() || !LentDecoys.TryGetValue( go.Id, out var simulator ) || simulator != c.Id || PropClaims.IsHeldObject( go ) )
			return;
		TakeBackDecoy( go, position, rotation );
	}

	// Host: the lend ends — the decoy is ours to simulate again, placed exactly at this pose and at rest. The next
	// TickShotProps sends everyone that rest pose (reliable), the old simulator included.
	void TakeBackDecoy( GameObject go, Vector3 position, Rotation rotation )
	{
		LentDecoys.Remove( go.Id );
		_lentLetGo.Remove( go.Id );
		_shotTargets.Remove( go );
		if ( EngineSynced( go ) )
			foreach ( var net in PropClaims.NetworkTree( go ) )
				net.Network.AssignOwnership( Connection.Local ); // the host simulates it again
		go.WorldPosition = position;
		go.WorldRotation = rotation;
		go.Transform.ClearInterpolation();
		var body = ShotBody( go, simulate: true );
		if ( body.IsValid() )
		{
			if ( body.PhysicsBody is { } pb && pb.IsValid() )
			{
				pb.Position = position;
				pb.Rotation = rotation;
			}
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
		}
		_shotLoose.Add( go );
		_shotResting.Remove( go );
		if ( _simDecoysActive )
			RegisterSimDecoy( go, awake: false, upright: false ); // back into the sim world, at rest where it was put down
	}

	/// <summary>Host → the simulator of a lent decoy: something hit it (a shot) — apply this impulse there.</summary>
	[Rpc.Broadcast]
	void ApplyLentImpulse( GameObject go, Vector3 point, Vector3 impulse )
	{
		if ( (Rpc.Caller is not null && !Rpc.Caller.IsHost) || !SimulatingLentHere( go ) )
			return;
		var body = go.Components.Get<Rigidbody>();
		if ( body.IsValid() && body.MotionEnabled )
		{
			body.Sleeping = false;
			body.ApplyImpulseAt( point, impulse );
		}
	}
}
