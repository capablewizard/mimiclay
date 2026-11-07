using System;
using System.Collections.Generic;
using System.Linq;
using Mimiclay.Sim;

namespace Mimiclay;

// "Grab props" (RoundSettings.GrabProps): the round hosts a PropClaims (RoundManagerSpawner puts one on the manager's
// object) for the INSPECT grab alone — claims stay closed, so nobody possesses or sculpts anything; hunters can only
// pick props up during the Hunt, carry and turn them, and put them down. See PropClaims.Grab for the rules, and
// IPropClaimHost.GrabSandbox for what's switched off.
//
// Held DECOYS are carried by the shared sim's hold driver, exactly like players' bodies (SimHoldDriver, driven by the
// holder's own target as sim input). A decoy outside the sim (mimi_sim_decoys 0, or one the sim can't take) is held by
// the HOST with the same HeldBodyDriver a body's fallback hold uses, from the holder's streamed target, and streamed to
// everyone like a shot decoy.
public sealed partial class RoundManager : IPropClaimHost
{
	bool IPropClaimHost.ClaimsAllowed => false;
	bool IPropClaimHost.GrabAllowed => Settings.GrabProps && Phase == RoundPhase.Hunt && !LobbyReturn.Active;
	bool IPropClaimHost.GrabPlayersAllowed => Settings.GrabProps;
	bool IPropClaimHost.GrabSandbox => false;

	GameObject IPropClaimHost.PropPrefab
		=> RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.PropPrefab : null;

	// The holder's pawn, from the roster's host-stamped PawnId.
	GameObject IPropClaimHost.ClaimantPawn( Connection c )
		=> Players.TryGetValue( c.Id, out var row ) && row.PawnId != Guid.Empty ? Scene.Directory.FindByGuid( row.PawnId ) : null;

	void IPropClaimHost.OnClaimGranted( Connection c, GameObject hunterPawn, HiderController prop ) { } // claims never open

	// ── Held decoys ─────────────────────────────────────────────────────────────────────────────────────────

	readonly HashSet<GameObject> _heldDecoys = new();                       // host: decoys outside the sim it holds and simulates
	readonly Dictionary<GameObject, HeldBodyDriver> _decoyHolds = new();      // host: the hold per decoy it drives
	readonly Dictionary<Guid, RealTimeSince> _simDecoyLetGo = new();          // host: when each SIM-held decoy's Held row went

	/// <summary>Does the ENGINE sync this decoy's transform (a network object — scene props are, on the maps checked)?
	/// Then the host's physics is everyone's truth, and our own pose stream (ShotProps) stays out of it.</summary>
	static bool EngineSynced( GameObject go ) => go.IsValid() && go.Network.Active;

	protected override void OnFixedUpdate()
	{
		if ( IsHostAuthority )
			DriveHeldDecoys();
	}

	// One tick of the hold for a decoy the host simulates.
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

	// The hold on this decoy is over: joint gone, friction back.
	void EndDecoyHold( GameObject go )
	{
		if ( go is not null && _decoyHolds.Remove( go, out var hold ) )
			hold.Release();
	}

	// Host, fixed step: hand held sim decoys to the sim's hold, drive the rest itself, and let go of both once put down.
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
				if ( DecoyInSim( go ) )
				{
					// A sim decoy is held IN the sim: the hold driver, driven by its holder's input (SimHoldDriver, phase 3b) —
					// no pose stream. Free to turn in the hand; loose again, free to tumble, when put down (below).
					Sim.Redrive( id, SimHoldDriver.Id, holder, SimBodyFlags.Present | SimBodyFlags.NoGravity );
					continue;
				}

				// Outside the sim: the host holds it, toward the holder's target (UpdateGrab — the host's own, or a client's stream).
				if ( !claims.TryGetGrabTarget( go, out var target ) )
					continue;
				_heldDecoys.Add( go );
				_shotLoose.Add( go ); // streamed to clients like any loose decoy
				_shotResting.Remove( go );
				DriveDecoyHold( go, target );
			}
		}

		// Sim decoys nobody holds any more: loose again — from the holder's release tick (HoldReleased, as a pawn's drop:
		// HiderController.UpdateSimRegistration), free to tumble like a shot one.
		if ( Sim is { Active: true } sim )
		{
			foreach ( var b in sim.Bodies.ToList() )
			{
				if ( b.Spec.Driver != SimHoldDriver.Id || b.Spec.Shape != SimShapeKind.FromGameObject )
					continue;
				var go = Scene.Directory.FindByGuid( b.Spec.Source );
				if ( !go.IsValid() || go.Components.Get<HiderController>().IsValid() || PropClaims.IsHeldObject( go ) )
					continue;
				if ( !_simDecoyLetGo.TryGetValue( go.Id, out var since ) )
					_simDecoyLetGo[go.Id] = since = 0f;
				if ( b.Ctrl.Holding && !b.HoldReleased && since < 1f )
					continue;
				_simDecoyLetGo.Remove( go.Id );
				_pushedDecoys.Remove( go );
				sim.Redrive( go.Id, 0, Guid.Empty, SimBodyFlags.Present );
			}
		}

		// Host-held decoys let go of: loose again — gravity back, keeping whatever motion the hold gave it.
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
				RegisterSimDecoy( go, awake: true, upright: false ); // into the sim world, keeping the motion the hold gave it
			return true;
		} );
	}
}
