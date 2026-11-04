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
// sync moves it. Possessed props and hunters are never knocked; a frozen prop stays put, and a prop someone's holding
// (lent to them) or sculpting is left alone.
public sealed partial class PropClaims
{
	// The released scenery pawn this clay is the disguise of — nobody wearing it — or null.
	static HiderController FreeSceneryOf( SdfSculpture sculpt )
	{
		var hider = sculpt.IsValid() ? sculpt.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ) : null;
		return hider.IsValid() && IsReleased( hider ) && hider.DisguiseSculpture == sculpt ? hider : null;
	}

	// Free to knock: released, and nobody holding, borrowing or sculpting it.
	bool ShotFree( HiderController hider )
	{
		if ( !hider.IsValid() || !IsReleased( hider ) || IsLent( hider.GameObject ) )
			return false;
		var id = hider.GameObject.Id;
		return !Held.Values.Contains( id ) && !Editing.Values.Contains( id );
	}

	/// <summary>Host, from HunterController.BroadcastCarve once a central pellet's crater is in (creative only).</summary>
	internal void OnCreativePropShot( SdfSculpture sculpt, Vector3 hit, Vector3 dir )
	{
		if ( !CreativeManager.Current.IsValid() || (Networking.IsActive && !Networking.IsHost) || !sculpt.IsValid() )
			return;

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
		if ( FrozenProps.ContainsKey( id ) )
			return;
		LooseProps[id] = true;
		_shotKicks[id] = new ShotKick { Point = hit, Impulse = dir.Normal * RoundManager.ShotKnockSpeed, Since = 0f };
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

			var body = hider.PhysicsBody;
			if ( !hider.InLoosePhysics || !body.IsValid() || body.PhysicsBody is not { } physics )
				continue;

			_shotKicks.Remove( id );
			body.Sleeping = false;
			body.ApplyImpulseAt( kick.Point, kick.Impulse * MathF.Max( physics.Mass, 1f ) );
		}
	}
}
