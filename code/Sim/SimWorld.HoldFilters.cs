using System;
using System.Collections.Generic;
using Sandbox.Physics;

namespace Mimiclay.Sim;

// ── A held body never collides with its holder ───────────────────────────────────────────────────────────────
// The scene fallback hold has always done this (PropClaims.IgnoreHolder: "up close a held prop otherwise gets pinned between
// a wall and the holder's own body, and the two shove each other back and forth"); the sim never did. With hunters as real
// sim bodies, a prop grabbed while standing against it started INSIDE the holder's body: the contact correction slid it
// ~10u at the grab before the hold had even begun (measured on devprophunt's stool, 2026-11-12: 7u sideways with a 1 u/s
// velocity — a positional correction, not the chase), knocking what sat on it flying.
//
// Every step, before the drive: each body the hold driver carries gets a no-collision joint (PhysicsJoint.Collisions =
// false, slack enough never to constrain anything) with every other body its holder controls (same Spec.Owner — their
// walker, a lying body). Derived from the drive alone, which rolls back with the body (DriveLog), so every machine and every
// replay has the same pairs at the same tick.
public sealed partial class SimWorld
{
	sealed class HoldFilter
	{
		public PhysicsJoint Joint;
		public PhysicsBody A, B;
	}

	readonly Dictionary<(ushort Held, ushort Holder), HoldFilter> _holdFilters = new();
	readonly HashSet<(ushort, ushort)> _holdWanted = new();
	readonly List<SimBody> _heldScratch = new();
	readonly List<(ushort, ushort)> _holdDrop = new();

	// Far beyond anything two bodies in one world can be apart: the joint never pulls.
	const float HoldFilterSlack = 1_000_000f;

	void SyncHoldFilters()
	{
		_heldScratch.Clear();
		foreach ( var b in _bodies )
			if ( b.Spec.Driver == SimHoldDriver.Id && b.Spec.Owner != Guid.Empty && b.ShapesReady )
				_heldScratch.Add( b );

		_holdWanted.Clear();
		foreach ( var held in _heldScratch )
			foreach ( var holder in _bodies )
				if ( holder != held && holder.Spec.Owner == held.Spec.Owner && holder.Spec.Driver != SimHoldDriver.Id && holder.ShapesReady )
					_holdWanted.Add( (held.Id, holder.Id) );

		if ( _holdWanted.Count == 0 && _holdFilters.Count == 0 )
			return;

		// Gone, or no longer wanted, or either body's physics object replaced: drop it.
		_holdDrop.Clear();
		foreach ( var (key, f) in _holdFilters )
		{
			bool live = _holdWanted.Contains( key ) && _byId.TryGetValue( key.Held, out var h ) && _byId.TryGetValue( key.Holder, out var o )
				&& h.Phys == f.A && o.Phys == f.B && f.A.IsValid() && f.B.IsValid();
			if ( !live )
				_holdDrop.Add( key );
		}
		foreach ( var key in _holdDrop )
		{
			RemoveHoldFilter( _holdFilters[key] );
			_holdFilters.Remove( key );
		}

		foreach ( var key in _holdWanted )
		{
			if ( _holdFilters.ContainsKey( key ) || !_byId.TryGetValue( key.Item1, out var held ) || !_byId.TryGetValue( key.Item2, out var holder ) )
				continue;
			var joint = PhysicsJoint.CreateLength( new PhysicsPoint( held.Phys ), new PhysicsPoint( holder.Phys ), HoldFilterSlack );
			if ( joint is null )
				continue;
			joint.Collisions = false;
			_holdFilters[key] = new HoldFilter { Joint = joint, A = held.Phys, B = holder.Phys };
		}
	}

	static void RemoveHoldFilter( HoldFilter f )
	{
		try
		{
			f.Joint?.Remove();
		}
		catch ( Exception )
		{
			// (a body removed under it took the joint with it)
		}
	}

	void ClearHoldFilters()
	{
		foreach ( var (_, f) in _holdFilters )
			RemoveHoldFilter( f );
		_holdFilters.Clear();
	}
}
