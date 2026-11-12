using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// ── Shot props in creative ─────────────────────────────────────────────────────────────────────────────────
// The central pellet knocks the prop it hits loose, like prop hunt's shot decoys (RoundManager.ShotProps). That's all:
// creative always repairs (craters heal — props there are edited in place, and permanent damage fights the editor),
// and with repair on nothing is ever shot away for good — no removal, no crumbs, no parts breaking off.
//
// Same knock, different plumbing. Prop hunt leaves its decoys as scene objects (a round's decoys must stay what they
// look like) and streams their poses by hand. Creative already has a way to make scene clay a real physics object —
// the CONVERSION a grab does (ConvertSceneProp → host-owned released scenery, LooseProps = a free rigidbody) — so a
// shot just does that: the prop becomes released scenery the moment it's hit, and from there the engine's network
// sync moves it. Possessed props and hunters are never knocked; a frozen prop is unfrozen and knocked loose, and a prop
// someone's holding or sculpting is left alone.
public sealed partial class PropClaims
{
	// The released scenery pawn this clay is the disguise of — nobody wearing it — or null.
	static HiderController FreeSceneryOf( SdfSculpture sculpt )
	{
		var hider = sculpt.IsValid() ? sculpt.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ) : null;
		return hider.IsValid() && IsReleased( hider ) && hider.DisguiseSculpture == sculpt ? hider : null;
	}

	// Free to knock: released, and nobody holding or sculpting it.
	bool ShotFree( HiderController hider )
	{
		if ( !hider.IsValid() || !IsReleased( hider ) )
			return false;
		var id = hider.GameObject.Id;
		return !Held.Values.Contains( id ) && !Editing.Values.Contains( id );
	}

	/// <summary>Host, from HunterController.BroadcastCarve once a central pellet's crater is in (creative only).</summary>
	internal void OnCreativePropShot( SdfSculpture sculpt, Vector3 hit, Vector3 dir )
	{
		if ( !CreativeManager.Current.IsValid() || (Networking.IsActive && !Networking.IsHost) || !sculpt.IsValid() )
			return;

		// The still props touching it go too (PropContacts) — found now, while the hit one is still where it stood.
		var cluster = StillCluster( sculpt );

		HiderController hider;
		if ( RoundManager.IsShotDecoy( sculpt ) )
		{
			// Still a scene object: make it a real prop, exactly as a grab would (the crater just landed is in its
			// brushes, so the pawn wears it — and heals it).
			if ( !IsClaimable( sculpt ) || !_claimedScene.Add( sculpt.GameObject.Id ) )
				return;
			var pawn = ConvertSceneProp( sculpt, null, scenery: true );
			if ( !pawn.IsValid() )
			{
				_claimedScene.Remove( sculpt.GameObject.Id );
				return;
			}
			hider = pawn.Components.Get<HiderController>();
			hider.ReleaseControl();
			ReleasedProps[pawn.Id] = true;
		}
		else
		{
			hider = FreeSceneryOf( sculpt );
		}

		if ( !ShotFree( hider ) )
			return;

		var id = hider.GameObject.Id;
		FrozenProps.Remove( id ); // shooting a frozen prop unfreezes it, like grabbing one does
		LooseProps[id] = true;
		_shotKicks[id] = new ShotKick { Point = hit, Impulse = dir.Normal * RoundManager.ShotKnockSpeed, Since = 0f };
		WakeCluster( cluster );
	}

	// ── Touching props wake with it (see PropContacts) ─────────────────────────────────────────────────────────
	// Creative's still props: scene clay nobody has converted yet, and FROZEN pawns. Both become loose released scenery,
	// exactly as if each had been shot — minus the knock: a zero kick only wakes it (a frozen body would otherwise
	// register in the sim asleep and hang where it was), and gravity and its contacts do the rest.

	/// <summary>Host: the still props connected to <paramref name="sculpt"/> through contact (not including it).</summary>
	internal List<SdfSculpture> StillCluster( SdfSculpture sculpt )
		=> Sandbox ? PropContacts.Cluster( Scene, sculpt, IsStillProp ) : new();

	bool IsStillProp( SdfSculpture s )
	{
		var hider = s.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
		if ( hider.IsValid() )
			return hider.DisguiseSculpture == s && FrozenProps.ContainsKey( hider.GameObject.Id ) && ShotFree( hider );
		return IsClaimable( s ) && s.Components.Get<SdfCollider>() is { BuildAsTrigger: false } && !_claimedScene.Contains( s.GameObject.Id );
	}

	/// <summary>Host: make each of these loose (converting scene clay first), woken where it stands.</summary>
	internal void WakeCluster( List<SdfSculpture> cluster )
	{
		foreach ( var s in cluster )
		{
			if ( !s.IsValid() )
				continue;

			var hider = s.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors );
			if ( !hider.IsValid() )
			{
				if ( !IsClaimable( s ) || !_claimedScene.Add( s.GameObject.Id ) )
					continue;
				var pawn = ConvertSceneProp( s, null, scenery: true );
				if ( !pawn.IsValid() )
				{
					_claimedScene.Remove( s.GameObject.Id );
					continue;
				}
				hider = pawn.Components.Get<HiderController>();
				hider.ReleaseControl();
				ReleasedProps[pawn.Id] = true;
			}

			if ( !ShotFree( hider ) )
				continue;

			var id = hider.GameObject.Id;
			FrozenProps.Remove( id );
			LooseProps[id] = true;
			if ( !_shotKicks.ContainsKey( id ) )
				_shotKicks[id] = new ShotKick { Point = hider.WorldPosition, Impulse = Vector3.Zero, Since = 0f };
		}
	}

	// ── Kicks: applied once the prop is a free rigidbody (a fresh conversion takes a tick or two) ──────────────
	struct ShotKick
	{
		public Vector3 Point;   // world hit point (impulse is applied there, so off-centre shots spin it)
		public Vector3 Impulse; // per unit mass
		public RealTimeSince Since;
	}

	readonly Dictionary<Guid, ShotKick> _shotKicks = new();
	const float ShotKickTimeout = 1f;

	// Host, per frame.
	void TickShotKicks()
	{
		if ( _shotKicks.Count == 0 )
			return;

		foreach ( var (id, kick) in _shotKicks.ToList() )
		{
			var hider = Scene.Directory.FindByGuid( id )?.Components.Get<HiderController>();
			if ( !hider.IsValid() || kick.Since > ShotKickTimeout )
			{
				_shotKicks.Remove( id );
				continue;
			}

			// The sim walk on, a loose prop is the SIM's rigid body (HiderController.UpdateSimRegistration) and the scene body
			// is only presentation — the kick has to go to the sim body, or it's lost. Waited for: a prop shot while FROZEN is
			// registered a tick or two later, and from an asleep (frozen) scene body it registers ASLEEP — without this kick
			// waking it, it hung in the air wherever it was frozen (user: "fairly often it just doesn't" fall).
			if ( HiderController.SimWalk && hider.Network.Active )
			{
				var sim = Scene.GetSystem<Mimiclay.Sim.SimWorld>();
				if ( sim is not { Active: true } || sim.Find( id ) is not { Spec.Driver: 0, ShapesReady: true } simBody || !simBody.Phys.IsValid() )
					continue;

				_shotKicks.Remove( id );
				sim.Kick( id, kick.Point, kick.Impulse * MathF.Max( simBody.Phys.Mass, 1f ) );
				continue;
			}

			var body = hider.PhysicsBody;
			if ( !hider.InLoosePhysics || !body.IsValid() || body.PhysicsBody is not { } physics )
				continue;

			_shotKicks.Remove( id );
			body.Sleeping = false;
			body.ApplyImpulseAt( kick.Point, kick.Impulse * MathF.Max( physics.Mass, 1f ) );
		}
	}
}
