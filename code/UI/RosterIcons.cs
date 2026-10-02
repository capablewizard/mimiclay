using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// What a roster icon should portray for one player: their sculpture (hunter face, or a prop's whole
/// disguise), the icon camera pose that goes with it (props carry their edit-exit angle; faces keep the
/// rig's default, so Pose stays null), and whether they're mid-edit right now — which badges the icon and
/// freezes the render (see SdfIcon.Frozen).
/// </summary>
public readonly record struct PipIcon( SdfSculpture Sculpture, Angles? Pose, bool Editing, List<SdfBrush> Fallback = null )
{
	/// <summary>Something to draw: the live subject, or the last shape we saw them as (see Fallback).</summary>
	public bool HasPicture => Sculpture.IsValid() || Fallback is not null;
}

/// <summary>
/// Resolves roster ids to the pawn sculpture their icon should show, with a per-player memory of the last
/// subject seen. One per HUD (the cache is that HUD's), shared logic so the round HUD's pips and the charades
/// scoreboard portray players identically.
/// </summary>
public sealed class RosterIcons
{
	// Last subject we managed to resolve per player. Losing the pawn for even ONE frame used to swap the razor
	// branch from portrait to generic glyph, which deletes the SdfThumbnail panel — and with it a whole
	// SceneWorld, to be rebuilt from scratch the moment the pawn came back. That churn is what a thumbnail
	// "flashing up for a second" looks like. A pawn blinking out is never a reason to forget whose icon it
	// was, so we don't.
	readonly Dictionary<Guid, (SdfSculpture Sculpture, Angles? Pose, List<SdfBrush> Brushes)> _cache = new();

	/// <summary>How many players have a remembered subject (debug readout / BuildHash).</summary>
	public int CachedCount => _cache.Count;

	/// <summary>
	/// The icon subject for a roster row, or a null sculpture if this machine has never had a pawn for them.
	/// Handing over the sculpture rather than its brushes is what lets the thumbnail update on commit instead
	/// of following a live drag — see SdfThumbnail.Source. Whichever pawn type is live wins the cache row, so
	/// a lobby role swap retargets the pip to the new body on its first resolvable frame.
	/// <para>
	/// <paramref name="facesOnly"/>: never portray a prop body — the player's HEAD is who they are (charades,
	/// where the mimic borrows a prop for one turn). While they wear the prop their hunter is gone, so the row
	/// falls back to the last face this machine saw them with.
	/// </para>
	/// </summary>
	public PipIcon Of( Scene scene, Guid connection, bool facesOnly = false )
	{
		var editing = false;

		foreach ( var h in scene.GetAllComponents<HunterController>() )
		{
			if ( !h.IsValid() || !h.Face.IsValid() )
				continue;

			// RosterIdOf handles the awkward cases: a pawn that hasn't gone on the wire yet has no Network.Owner
			// (if it isn't a proxy, it's ours), and every bot pawn is host-owned, so an owner match alone would
			// pin all of their faces — and the host's own — onto the host's row.
			var owner = RoundManager.RosterIdOf( h.GameObject );
			if ( owner != connection )
				continue;

			editing = h.NetEditing;
			if ( h.Face.Brushes is { Count: > 0 } )
				_cache[connection] = (h.Face, null, h.Face.Brushes);

			break;
		}

		// Prop players portray the disguise itself, from the angle its player last signed it off at. A prop the
		// round is concealing from us never resolves — the cache can't leak what this machine was never shown —
		// though in practice a concealed prop's pawn isn't even on this machine's network until the Reveal.
		// Released props (creative/lobby scenery) belong to NOBODY and must never portray anyone — skipped before the
		// owner resolve so no resolver quirk (an unowned pawn resolves null today, but a host-orphaned one
		// answers the host) can pin scenery onto a player's row.
		if ( !facesOnly )
		{
			foreach ( var p in scene.GetAllComponents<HiderController>() )
			{
				if ( !p.IsValid() || !p.DisguiseSculpture.IsValid() || p.Concealed || PropClaims.IsReleased( p ) )
					continue;

				var owner = RoundManager.RosterIdOf( p.GameObject );
				if ( owner != connection )
					continue;

				editing = p.NetEditing;
				if ( p.DisguiseSculpture.Brushes is { Count: > 0 } )
					_cache[connection] = (p.DisguiseSculpture, p.IconPoseSet ? p.IconPose : null, p.DisguiseSculpture.Brushes);

				break;
			}
		}

		var (sculpture, pose, brushes) = _cache.GetValueOrDefault( connection );

		// The cached body died (a swap or possession destroys it a beat before the new one resolves here): keep
		// drawing the last shape we saw them as, rather than dropping to the generic glyph for those frames. The
		// list is the dead sculpture's own — nothing edits a destroyed body, so holding the reference is enough.
		return sculpture.IsValid()
			? new PipIcon( sculpture, pose, editing )
			: new PipIcon( null, pose, editing, brushes is { Count: > 0 } ? brushes : null );
	}

	/// <summary>
	/// The SET of icon subjects this machine can currently resolve — an order-independent hash of their object
	/// ids, folded into BuildHash so a pawn spawning (or its sculpt arriving) triggers a re-render. Identities,
	/// deliberately NOT a count: a lobby role swap destroys one pawn and spawns another, so a count reads 1→1
	/// and the HUD can skip the rebuild entirely — the pip keeps requesting the destroyed sculpture and sits on
	/// the old body's last render. Ids see old-out/new-in and force the rebuild that retargets the pip. XOR
	/// because GetAllComponents' enumeration order reshuffles on despawn/respawn (it's a HashSet underneath),
	/// and an order-sensitive hash would fire spurious rebuilds every time the slots got reused.
	/// </summary>
	public static int Signal( Scene scene )
	{
		var n = 0;
		foreach ( var h in scene.GetAllComponents<HunterController>() )
			if ( h.IsValid() && h.Face.IsValid() && h.Face.Brushes is { Count: > 0 } )
				n ^= h.Face.GameObject.Id.GetHashCode();
		foreach ( var p in scene.GetAllComponents<HiderController>() )
			if ( p.IsValid() && p.DisguiseSculpture.IsValid() && p.DisguiseSculpture.Brushes is { Count: > 0 } )
				n ^= p.DisguiseSculpture.GameObject.Id.GetHashCode();
		return n;
	}
}
