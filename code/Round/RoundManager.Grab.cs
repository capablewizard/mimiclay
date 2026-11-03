using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// "Grab props" (RoundSettings.GrabProps): the round hosts a PropClaims (RoundManagerSpawner puts one on the manager's
// object) for the INSPECT grab alone — claims stay closed, so nobody possesses or sculpts anything; hunters can only
// pick props up during the Hunt, carry and turn them, and put them down. See PropClaims.Grab for the rules, and
// IPropClaimHost.GrabSandbox for what's switched off.
//
// Held DECOYS are moved here, on the host: the decoy's local shot-prop body (see ShotProps) chases the holder's pose
// with velocities — the same pull and turn the hider's velocity hold uses — and is streamed to clients like any loose
// decoy — and a CLIENT holder predicts its own held decoy locally (PredictHeldDecoy). On the drop gravity comes back and
// it carries on as a loose decoy. Players' bodies are held by their own
// controller instead (HiderController): lent to the holding hunter's machine (mimi_grab_borrow, default on), or the
// creative way on their wearer's.
public sealed partial class RoundManager : IPropClaimHost
{
	bool IPropClaimHost.ClaimsAllowed => false;
	bool IPropClaimHost.GrabAllowed => Settings.GrabProps && Phase == RoundPhase.Hunt && !LobbyReturn.Active;
	bool IPropClaimHost.GrabPlayersAllowed => Settings.GrabProps;
	bool IPropClaimHost.GrabSandbox => false;
	bool IPropClaimHost.GrabBorrowsBodies => GrabBorrowBodies;

	/// <summary>Held players' bodies: 1 (default) = LENT to the hunter holding them — the hunter's machine simulates the
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

	// The hider's velocity hold, for a decoy (HiderController.DriveHeld's contact branch — no joint: a decoy has none).
	const float DecoyHoldPull = 18f;
	const float DecoyHoldTurn = 14f;
	const float DecoyHoldMaxSpeed = 1800f;

	readonly HashSet<GameObject> _heldDecoys = new();

	// Client holder: the decoy we're PREDICTING — the one in our hands, simulated here from our own aim (see
	// PredictHeldDecoy) — and the filter that keeps it off our own pawn.
	GameObject _predictedDecoy;
	List<PhysicsFilter> _predictedFilters;

	protected override void OnFixedUpdate()
	{
		if ( IsHostAuthority )
			DriveHeldDecoys();
		else
			PredictHeldDecoy();
	}

	// Client, fixed step: the decoy in OUR hands is simulated on our machine too, from the target our hunter computed
	// this frame (PropClaims.SetLocalGrabTarget) with the host's own pull and turn — so it answers the mouse with no
	// round-trip, exactly as a lent player's body does (PropClaims.BorrowedBodies). The host's stream for it is ignored
	// meanwhile (TickShotProps / FollowShotPose); the moment we let go our copy goes back to following that stream,
	// whose ease (ShotFollowRate) is the blend from our prediction onto the host's truth.
	void PredictHeldDecoy()
	{
		var claims = PropClaims.Current;
		var held = PropClaims.LocalHeld;
		bool decoy = held.IsValid() && !held.Components.Get<HiderController>().IsValid();
		Transform target = default;
		if ( !decoy || !claims.IsValid() || !claims.TryGetGrabTarget( held, out target ) )
			held = null;

		if ( _predictedDecoy != held )
		{
			EndPrediction();
			if ( held.IsValid() )
			{
				_predictedDecoy = held;
				var mine = Scene.GetAllComponents<HunterController>().FirstOrDefault( h => !h.IsProxy && !IsBotPawn( h.GameObject ) );
				_predictedFilters = PropClaims.FilterAgainst( held, mine?.GameObject );
			}
		}
		if ( !held.IsValid() )
			return;

		var body = ShotBody( held, simulate: true );
		if ( !body.IsValid() )
			return;
		float mass = held.Components.Get<SdfCollider>()?.ClayMass ?? 0f;
		if ( mass > 0f && MathF.Abs( body.MassOverride - mass ) > 1e-3f )
			body.MassOverride = mass;
		body.Gravity = false;
		body.Sleeping = false;
		ChaseHold( body, held, target );
	}

	// Our prediction ends (dropped, swapped, or gone): back to following the host's stream, from where we are.
	void EndPrediction()
	{
		if ( _predictedFilters is not null )
		{
			foreach ( var filter in _predictedFilters )
				if ( filter.IsValid() )
					filter.Destroy();
			_predictedFilters = null;
		}
		if ( _predictedDecoy.IsValid() )
			ShotBody( _predictedDecoy, simulate: false );
		_predictedDecoy = null;
	}

	// The hider's velocity hold, shared by the host's drive and the holder's prediction so the two agree.
	static void ChaseHold( Rigidbody body, GameObject go, Transform target )
	{
		var dt = MathF.Max( Time.Delta, 0.001f );
		body.Velocity = ((target.Position - go.WorldPosition) * MathF.Min( DecoyHoldPull, 1f / dt )).ClampLength( DecoyHoldMaxSpeed );
		body.AngularVelocity = TurnToward( go.WorldRotation, target.Rotation ) * MathF.Min( DecoyHoldTurn, 1f / dt );
	}

	void DriveHeldDecoys()
	{
		var claims = PropClaims.Current;
		if ( claims.IsValid() && claims.Held.Count > 0 )
		{
			foreach ( var (_, id) in claims.Held )
			{
				var go = Scene.Directory.FindByGuid( id );
				if ( !go.IsValid() || go.Components.Get<HiderController>().IsValid() || !claims.TryGetGrabTarget( go, out var target ) )
					continue; // a pawn (its controller holds itself), or gone

				var body = ShotBody( go, simulate: true );
				if ( !body.IsValid() )
					continue;
				if ( _heldDecoys.Add( go ) )
				{
					float mass = go.Components.Get<SdfCollider>()?.ClayMass ?? 0f;
					if ( mass > 0f )
						body.MassOverride = mass;
				}
				body.Gravity = false;
				body.Sleeping = false;
				_shotLoose.Add( go ); // streamed to clients like any loose decoy
				_shotResting.Remove( go );

				ChaseHold( body, go, target );
			}
		}

		// Let go of: a loose decoy again — gravity back, keeping whatever motion the hold gave it.
		_heldDecoys.RemoveWhere( go =>
		{
			if ( !go.IsValid() )
				return true;
			if ( PropClaims.IsHeldObject( go ) )
				return false;
			var body = go.Components.Get<Rigidbody>();
			if ( body.IsValid() )
				body.Gravity = true;
			return true;
		} );
	}

	// Shortest-arc axis × angle (radians) from one rotation to another — HiderController.ChaseHeldRotation's maths.
	static Vector3 TurnToward( Rotation from, Rotation to )
	{
		var q = to * from.Inverse;
		if ( q.w < 0f )
			q = new Rotation( -q.x, -q.y, -q.z, -q.w );
		var sinHalf = MathF.Sqrt( q.x * q.x + q.y * q.y + q.z * q.z );
		if ( sinHalf < 1e-5f )
			return Vector3.Zero;
		var angle = 2f * MathF.Atan2( sinHalf, q.w );
		return new Vector3( q.x, q.y, q.z ) / sinHalf * angle;
	}
}
