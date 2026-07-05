using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Gives an <see cref="SdfSculpture"/> physics. Add this component alongside a sculpture to make it solid;
/// leave it off (e.g. on player-model parts) and the sculpture is a pure visual. Mirrors how s&amp;box splits
/// <c>ModelRenderer</c> (visual) from <c>ModelCollider</c> (physics): the sculpture owns shapes + properties,
/// this owns collision. It reads the sibling sculpture's brushes, builds a cheap primitive collider via
/// <see cref="SdfCollisionBuilder"/>, and drives a sibling <see cref="ModelCollider"/> with the result.
///
/// Rebuilds itself whenever the shape is COMMITTED — it subscribes to <see cref="SdfSculpture.Committed"/>
/// (gizmo release / discrete edits / a networked disguise swap), never mid-drag — so gameplay code never has
/// to poke it. Runtime only (no ExecuteInEditor): physics is a play concern, and not building shapes while
/// authoring keeps the editor clean.
/// </summary>
[Title( "SDF Collider" )]
[Category( "SDF" )]
[Icon( "view_in_ar" )]
public sealed class SdfCollider : Component
{
	/// <summary>Spacing (sculpture-local units) between ground-probe points sampled across each brush's underside.
	/// Smaller = denser coverage but more traces; each brush gets at least a 2×2 spread. See
	/// <see cref="SdfCollisionBuilder.ComputeFootPoints"/> and <see cref="FootPoints"/>.</summary>
	[Property, Range( 4f, 32f )] public float FootProbeSpacing { get; set; } = 12f;

	// Sculpture-local footprint snapshot (underside contact points), refreshed with the collider. Used by the
	// player controller for multi-point ground checks. Decoupled from the collider build (see Rebuild) so a
	// problem computing it can never stop the sculpture from being solid.
	List<Vector3> _footPoints = new();

	/// <summary>Sculpture-local underside contact points captured when the collider was last built (empty until
	/// the first build). Transform by <see cref="GameObject"/>'s world transform to probe the ground.</summary>
	public IReadOnlyList<Vector3> FootPoints => _footPoints;

	SdfSculpture _sculpture;

	protected override void OnEnabled()
	{
		_sculpture = GameObject.Components.Get<SdfSculpture>();
		if ( _sculpture.IsValid() )
			_sculpture.Committed += Rebuild;

		Rebuild();
	}

	protected override void OnDisabled()
	{
		if ( _sculpture.IsValid() )
			_sculpture.Committed -= Rebuild;
		_sculpture = null;

		// Going away = no longer solid: tear down the collider we drive and drop the footprint snapshot.
		var collider = GameObject.Components.Get<ModelCollider>();
		if ( collider.IsValid() )
			collider.Enabled = false;
		_footPoints.Clear();
	}

	/// <summary>Rebuild the primitive collider from the sibling sculpture's additive brushes and assign it to a
	/// sibling <see cref="ModelCollider"/>. Cheap and synchronous (no field sampling). Public so the disguise
	/// can force a build regardless of clone/OnEnabled timing; normally driven by <see cref="SdfSculpture.Committed"/>.</summary>
	public void Rebuild()
	{
		// Re-resolve in case Rebuild is called before OnEnabled has bound the sibling (e.g. a forced build right
		// after the component is created).
		_sculpture ??= GameObject.Components.Get<SdfSculpture>();
		var brushes = _sculpture.IsValid() ? _sculpture.Brushes : null;

		var model = SdfCollisionBuilder.Build( brushes );

		var collider = GameObject.Components.GetOrCreate<ModelCollider>();
		collider.Model = model;
		collider.Enabled = model is not null;

		// Footprint snapshot for ground probes — computed AFTER the collider is fully set up, and guarded, so a
		// problem here can NEVER stop the sculpture from being solid (the collider build must not depend on it).
		try
		{
			_footPoints = brushes is not null
				? SdfCollisionBuilder.ComputeFootPoints( brushes, FootProbeSpacing )
				: new();
		}
		catch
		{
			_footPoints = new();
		}
	}
}
