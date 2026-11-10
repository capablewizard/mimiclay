using System.Collections.Generic;
using Sandbox.Physics;

namespace Mimiclay.Sim;

/// <summary>
/// What a sim walker carries never collides with it (the scene filters the two — PropClaims.FilterAgainst): a filter joint
/// between the walker's sim body and each body the sim holds for the same player, kept in step every tick — host ticks,
/// predictions and replays alike — so a replay across a grab or a drop filters exactly where the original did. One per
/// walker that can hold things: the hunter's walk and, in creative, a prop's.
/// </summary>
public sealed class SimHoldFilters
{
	readonly Dictionary<PhysicsBody, PhysicsJoint> _filters = new();
	PhysicsBody _for;

	/// <summary>Call from the walker's step, every tick, with its own sim body.</summary>
	public void Sync( SimWorld sim, SimBody me )
	{
		if ( me?.Phys is not { } mine || !mine.IsValid() )
			return;
		if ( _for != mine )
		{
			Clear();
			_for = mine;
		}
		List<PhysicsBody> keep = null;
		foreach ( var b in sim.Bodies )
		{
			if ( b == me || b.Spec.Driver != SimHoldDriver.Id || b.Spec.Owner != me.Spec.Owner || b.Phys is not { } p || !p.IsValid() )
				continue;
			(keep ??= new()).Add( p );
			if ( !_filters.ContainsKey( p ) )
				_filters[p] = PhysicsJoint.CreateFilter( mine, p );
		}
		if ( _filters.Count == (keep?.Count ?? 0) )
			return;
		foreach ( var p in _filters.Keys.ToList() )
		{
			if ( keep is not null && keep.Contains( p ) )
				continue;
			if ( p.IsValid() )
				_filters[p]?.Remove();
			_filters.Remove( p );
		}
	}

	/// <summary>Remove every filter (the walker left the sim, or its body was rebuilt).</summary>
	public void Clear()
	{
		foreach ( var j in _filters.Values )
			if ( j.IsValid() )
				j.Remove();
		_filters.Clear();
		_for = null;
	}
}
