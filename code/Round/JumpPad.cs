using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Drop one anywhere in a map: anything that steps into its trigger volume gets launched straight up. Works
/// for both pawn kinds in this game, which have completely separate movement (see <see cref="HunterController"/>
/// / <see cref="HiderController"/> doc comments) — a hunter's stock <see cref="PlayerController"/> and a
/// hider/prop's manually-driven <see cref="Rigidbody"/> — by just setting whichever one's <c>Body.Velocity</c>
/// is actually simulating.
///
/// <b>Networking.</b> No trigger-volume component existed anywhere in this codebase before this one, so there
/// was no template to copy for "who's standing on me" — this uses s&box's own <see cref="Component.ITriggerListener"/>.
/// Only <see cref="Component.IsProxy"/> pawns' OWNER machine actually simulates their body (a proxy is just
/// driven off the networked transform — see the "Proxy" doc comments on both controllers), so every machine's
/// own trigger fires independently but only the owner's velocity write does anything real; everyone else's
/// write would be silently overwritten by the next networked transform update anyway. The launch itself
/// therefore needs no RPC — it rides the pawn's existing position/velocity network sync, same as a normal jump.
/// The boing IS broadcast (<see cref="BroadcastBoing"/>), same guard convention as <see cref="RoundDoor.SetOpen"/>,
/// so everyone hears it once regardless of who owns the pawn that triggered it.
/// </summary>
[Title( "Jump Pad" )]
[Category( "Mimiclay" )]
[Icon( "bolt" )]
public sealed class JumpPad : Component, Component.ITriggerListener
{
	/// <summary>Vertical launch speed (units/s) — set as the pawn's Z velocity outright, same as a normal jump,
	/// not added on top (so repeated triggers don't stack into orbit).</summary>
	[Property, Range( 100f, 2500f )]
	public float LaunchSpeed { get; set; } = 900f;

	/// <summary>Optional horizontal shove, along the pad's own forward direction — 0 for a pure vertical pad.</summary>
	[Property, Range( 0f, 1000f )]
	public float ForwardSpeed { get; set; } = 0f;

	/// <summary>Per-pawn re-trigger debounce, so standing in a pad that's slightly bigger than the launch arc
	/// doesn't re-launch every physics tick on the way up.</summary>
	[Property, Range( 0.1f, 2f )]
	public float Cooldown { get; set; } = 0.75f;

	[Property] public SoundEvent BoingSound { get; set; }

	// Per-pawn debounce — keyed by the pawn's root GameObject, not the collider (a pawn can have several).
	readonly Dictionary<GameObject, TimeSince> _lastLaunch = new();

	protected override void OnStart()
	{
		// Convenience for a fresh drop: most placements just want a trigger box matching whatever model sits
		// here. If the author already added their own trigger collider (any shape), leave it alone.
		if ( Components.Get<Collider>() is null )
		{
			var box = Components.Create<BoxCollider>();

			// Bottom face sits BELOW ground level (not flush with it) on purpose: a standing/walking pawn's
			// capsule bottom rests exactly AT ground level, so a trigger box starting exactly there only ever
			// TOUCHES it rather than genuinely overlapping — which many physics engines don't count as an
			// "enter" (resting contact isn't penetration). That's why this only fired for a jump: falling
			// into it has real downward velocity that visibly penetrates before the ground-snap stops it. Going
			// 16 units underground guarantees real overlap the instant a pawn's footprint steps onto the pad,
			// standing still or not.
			box.Center = new Vector3( 0f, 0f, 8f );
			box.Scale = new Vector3( 64f, 64f, 48f ); // spans z -16..32 (Scale is full corner-to-corner size)
		}

		foreach ( var col in Components.GetAll<Collider>() )
			col.IsTrigger = true;
	}

	void Component.ITriggerListener.OnTriggerEnter( Collider other )
		=> TryLaunchFrom( other.GameObject );

	// Component.ITriggerListener also has a separate GameObject-keyed overload family (OnTriggerEnter(GameObject)),
	// which some trigger pairings surface INSTEAD of the Collider one depending on how the other body's colliders
	// are set up — implementing only one meant pads worked for some pawns/players but silently did nothing for
	// others. Handling both (TryLaunchFrom is idempotent per pawn thanks to the cooldown dictionary, so no harm
	// if the engine happens to call both for the same overlap) makes this reliable for every pawn kind.
	void Component.ITriggerListener.OnTriggerEnter( GameObject other )
		=> TryLaunchFrom( other );

	void TryLaunchFrom( GameObject other )
	{
		var root = other?.Root;
		if ( !root.IsValid() )
			return;

		// Hunter: stock PlayerController (its own Body is the real simulated Rigidbody — see HunterController's
		// `_controller.Body.Velocity` writes for the equivalent in-controller pattern this mirrors).
		var pc = root.Components.Get<PlayerController>();
		if ( pc.IsValid() )
		{
			Launch( pc, pc.Body, pc.IsProxy );
			return;
		}

		// Hider/prop: its body is keyframed and moved by its own mover, so the launch goes through the mover
		// (HiderController.Launch), not the rigidbody. Same IsProxy gate: a proxy is moved, not simulated.
		var hider = root.Components.Get<HiderController>();
		if ( hider.IsValid() && !hider.IsProxy && TakeCooldown( hider.GameObject ) )
		{
			hider.Launch( LaunchVelocity( hider.Velocity ) );
			BroadcastBoing();
		}
	}

	bool TakeCooldown( GameObject go )
	{
		if ( _lastLaunch.TryGetValue( go, out var since ) && since < Cooldown )
			return false;
		_lastLaunch[go] = 0;
		return true;
	}

	Vector3 LaunchVelocity( Vector3 current )
	{
		var vel = current.WithZ( LaunchSpeed );
		if ( ForwardSpeed > 0f )
			vel += WorldRotation.Forward * ForwardSpeed;
		return vel;
	}

	void Launch( PlayerController pc, Rigidbody body, bool isProxy )
	{
		if ( isProxy )
			return; // this machine doesn't simulate this pawn — the owner's own trigger fires there instead

		if ( !body.IsValid() || !TakeCooldown( pc.GameObject ) )
			return;

		// PlayerController re-snaps to the ground and ZEROES vertical velocity every physics tick while
		// IsOnGround is true (MoveModeWalk.PostPhysicsStep -> StickToGround -> PlayerController.Reground) — a
		// plain Body.Velocity write here would get cancelled out again before it ever visibly moved the hunter
		// (this is exactly what "just resets to 0" was). PreventGrounding is the engine's own public escape
		// hatch for this — its Jump(Vector3) helper uses the same call internally. Estimate enough airtime to
		// clear the ground trace: a touch more than the time to rise back to launch height under gravity.
		pc.PreventGrounding( AirborneEstimate( LaunchSpeed ) );
		body.Velocity = LaunchVelocity( body.Velocity );

		BroadcastBoing();
	}

	// Only the PlayerController needs the PreventGrounding escape hatch: HiderController.Launch un-grounds the
	// prop mover itself.
	static float AirborneEstimate( float launchSpeed )
	{
		const float gravity = 800f; // s&box's default PlayerController.Gravity magnitude
		return launchSpeed / gravity + 0.2f; // time to apex, plus a little slack
	}

	[Rpc.Broadcast]
	void BroadcastBoing()
	{
		if ( BoingSound is not null )
			Sound.Play( BoingSound, WorldPosition );
	}

	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		Gizmo.Draw.Color = Color.Cyan.WithAlpha( (Gizmo.IsSelected || Gizmo.IsHovered) ? 0.9f : 0.5f );
		Gizmo.Draw.LineBBox( new BBox( new Vector3( -32f, -32f, -16f ), new Vector3( 32f, 32f, 32f ) ) );

		// Launch direction preview: straight up, kicked toward Forward if this pad also shoves horizontally.
		// Plain lines only (Gizmo.Draw.Line/LineBBox — the only draw primitives already proven elsewhere in
		// this codebase, e.g. RoomController's gizmos) rather than an unverified arrow-drawing helper.
		var from = Vector3.Up * 32f;
		var dir = (Vector3.Up * LaunchSpeed + Vector3.Forward * ForwardSpeed).Normal;
		var to = from + dir * 48f;
		Gizmo.Draw.Line( from, to );

		// Small arrowhead: two short lines angled back from the tip.
		var back = -dir * 12f;
		var side = Vector3.Cross( dir, Vector3.Right ).Normal * 8f;
		if ( side.Length < 0.1f )
			side = Vector3.Cross( dir, Vector3.Forward ).Normal * 8f;
		Gizmo.Draw.Line( to, to + back + side );
		Gizmo.Draw.Line( to, to + back - side );
	}
}
