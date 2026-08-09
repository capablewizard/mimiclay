using System.Linq;

namespace Mimiclay;

/// <summary>
/// Marks a networked map prop as grabbable by hunters (see <see cref="PropGrabber"/>) and keeps its physics
/// honest: mass is recomputed from the sculpted clay volume (<see cref="PropMass"/> — the same formula player
/// disguises use, so weight can never be a player-vs-decoy tell) on start and on every commit, which is also
/// how a shot-carved prop gets lighter. Normally added in code by <see cref="MapPropPhysics"/> when it
/// converts the scene props at map start, alongside the sibling <see cref="Rigidbody"/> on the prop's root.
/// Authoring it on a scene prop in the editor is fine too — it's inert there (no Rigidbody to weigh), and the
/// converter clones it through rather than treating it as a conversion marker (clones are recognised by
/// Network.Active instead).
///
/// Also guarantees the root carries an enabled <see cref="SdfHighlightOutline"/> so the hunter's hover/held
/// highlight has something to drive (the saved/ prefabs inherited one from the disguise template, the
/// CastleCourtyard ones didn't). Authored hidden: <see cref="RoundOutlineSystem"/> keeps decoy outlines off,
/// and <see cref="PropGrabber"/> un-hides it locally while hovering/holding.
/// </summary>
[Title( "Grabbable Prop" )]
[Category( "Mimiclay" )]
[Icon( "pan_tool" )]
public sealed class GrabbableProp : Component
{
	SdfSculpture[] _sculptures;
	Rigidbody _body;

	protected override void OnStart()
	{
		_body = Components.Get<Rigidbody>();

		_sculptures = Components.GetAll<SdfSculpture>( FindMode.EverythingInSelfAndDescendants ).ToArray();
		foreach ( var s in _sculptures )
			if ( s.IsValid() )
				s.Committed += RecomputeMass;
		RecomputeMass();

		// Hover/held highlight target. Only if the tree has none: a second live outline group would read the
		// first as an occluder (the SdfHighlightOutline two-group lesson).
		if ( !Components.Get<SdfHighlightOutline>( FindMode.EverythingInSelfAndDescendants ).IsValid() )
		{
			var outline = Components.Create<SdfHighlightOutline>();
			outline.Placement = SdfOutlinePlacement.Inside; // immune to the proxy-padding width clamp
			outline.Hidden = true;
		}
	}

	protected override void OnDestroy()
	{
		if ( _sculptures is null )
			return;
		foreach ( var s in _sculptures )
			if ( s.IsValid() )
				s.Committed -= RecomputeMass;
	}

	// Sums the parts (a multi-sculpture prop like the well is one compound body on the root) and clamps the
	// total. Runs on every machine; only the simulating owner's body reads it, but asserting everywhere is
	// free and survives ownership changes.
	void RecomputeMass()
	{
		if ( !_body.IsValid() )
			return;

		float mass = 0f;
		foreach ( var s in _sculptures )
			mass += PropMass.MassOf( s );

		_body.MassOverride = PropMass.Clamp( mass );
	}
}
