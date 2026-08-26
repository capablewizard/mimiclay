using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Gives an <see cref="SdfSculpture"/> physics. Add this component alongside a sculpture to make it solid;
/// leave it off (e.g. on player-model parts) and the sculpture is a pure visual. Mirrors how s&amp;box splits
/// <c>ModelRenderer</c> (visual) from <c>ModelCollider</c> (physics): the sculpture owns shapes + properties,
/// this owns collision. It reads the sibling sculpture's brushes, builds a cheap convex collider (carve-aware:
/// heavily subtracted brushes become hollowed voxel boxes, so an open bin really is open — see
/// <see cref="SdfCollisionBuilder"/>), and drives a sibling <see cref="ModelCollider"/> with the result.
///
/// Rebuilds itself whenever the shape is COMMITTED — it subscribes to <see cref="SdfSculpture.Committed"/>
/// (gizmo release / discrete edits / a networked disguise swap), never mid-drag — so gameplay code never has
/// to poke it. Runtime only (no ExecuteInEditor): physics is a play concern, and not building shapes while
/// authoring keeps the editor clean. Commits that <see cref="SculptBounds"/> judges INVALID are skipped, so
/// the collider always matches the shape other machines are rendering (see <see cref="Rebuild"/>).
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

	/// <summary>Build the collider as a TRIGGER instead of a solid: it generates no physical contacts (never
	/// blocks or pushes anything), but the gun's rays still hit it because they opt in with HitTriggers().
	/// The hunter head's mode — its collider exists purely for bullet hit detection. Default off: disguises,
	/// decoys and world props stay exactly as solid as they look.</summary>
	[Property] public bool BuildAsTrigger { get; set; }

	/// <summary>Tag stamped onto every SDF-collider GameObject (runtime only — this component never runs in
	/// the editor, so it's never saved into assets). Lets traces treat clay props differently from world
	/// geometry — the camera boom's world-only mode filters on it.</summary>
	public const string ClayTag = "clay";

	// Sculpture-local footprint snapshot (underside contact points), refreshed with the collider. Used by the
	// player controller for multi-point ground checks. Decoupled from the collider build (see Rebuild) so a
	// problem computing it can never stop the sculpture from being solid.
	List<Vector3> _footPoints = new();

	/// <summary>Sculpture-local underside contact points captured when the collider was last built (empty until
	/// the first build). Transform by <see cref="GameObject"/>'s world transform to probe the ground. The
	/// underside is WORLD-down at build time — a tilted sculpture's points sit on the face actually resting on
	/// the floor — and stays valid while the pawn yaws (yaw cancels out of the tilt).</summary>
	public IReadOnlyList<Vector3> FootPoints => _footPoints;

	// Sculpture-local points outlining every shape the last build EMITTED — hull vertices, voxel-box corners,
	// points on each collision sphere — captured by SdfCollisionBuilder.Build itself, so they carry its carve
	// decisions exactly (a hollowed bin's frame stops at its real base, not the uncarved cylinder's).
	readonly List<Vector3> _framePoints = new();

	/// <summary>Sculpture-local points outlining the ACTUAL emitted collider (empty until the first build) —
	/// project them to frame the visible clay on screen (the creative-mode "E to Edit" prompt). Anything
	/// derived from the brush list instead cannot know what a subtract carved away.</summary>
	public IReadOnlyList<Vector3> FramePoints => _framePoints;

	SdfSculpture _sculpture;
	SculptBounds _bounds;

	protected override void OnEnabled()
	{
		GameObject.Tags.Add( ClayTag );
		_sculpture = GameObject.Components.Get<SdfSculpture>();
		_bounds = GameObject.Components.Get<SculptBounds>();
		if ( _sculpture.IsValid() )
			_sculpture.Committed += Rebuild;

		Rebuild();
	}

	protected override void OnDisabled()
	{
		if ( _sculpture.IsValid() )
			_sculpture.Committed -= Rebuild;
		_sculpture = null;
		_bounds = null;

		// Going away = no longer solid: tear down the collider we drive and drop the footprint snapshot.
		var collider = GameObject.Components.Get<ModelCollider>();
		if ( collider.IsValid() )
			collider.Enabled = false;
		_footPoints.Clear();
		_framePoints.Clear();
	}

	/// <summary>Rebuild the primitive collider from the sibling sculpture's additive brushes and assign it to a
	/// sibling <see cref="ModelCollider"/>. Cheap and synchronous (no field sampling). Public so the disguise
	/// can force a build regardless of clone/OnEnabled timing; normally driven by <see cref="SdfSculpture.Committed"/>.
	/// A commit an owner-side <see cref="SculptBounds"/> rejects leaves the standing collider alone — see the gate.</summary>
	public void Rebuild()
	{
		// Re-resolve in case Rebuild is called before OnEnabled has bound the siblings (e.g. a forced build right
		// after the component is created).
		_sculpture ??= GameObject.Components.Get<SdfSculpture>();
		_bounds ??= GameObject.Components.Get<SculptBounds>();
		var brushes = _sculpture.IsValid() ? _sculpture.Brushes : null;

		// NEVER build physics for an INVALID shape. An invalid shape is deliberately never published (see
		// SdfNetworkSync's bounds gate), so proxies keep rendering the last valid one — but the collider is a
		// LOCAL rebuild that changes the pawn's footprint and the ground snap that rides it, and the resulting
		// root move goes out over the engine's always-on transform sync. Everyone else then watches the prop
		// slide and settle at a height that matches a shape they can't see. Same split-transport trap the origin
		// recenter gates on (HiderController.RecenterOriginOnShape), and the same predicate the sync gates on
		// (non-proxy + hash-cached EvaluateNow, so the three gates can never disagree): proxies always build,
		// because what they applied was already validated on receive.
		//
		// Freezing costs nothing to catch up on — the shape other machines see is the shape the standing
		// collider was built from, and the next VALID commit (a fix, or the exit revert) rebuilds and publishes
		// together. Only ever skips when a collider is already standing: the very first build must go through
		// whatever the sculpture starts as, or a shape that spawns invalid (or a decoy this machine's config
		// judges invalid) would be left with no collision at all.
		var standing = GameObject.Components.Get<ModelCollider>();
		if ( !IsProxy && _bounds.IsValid() && standing.IsValid() && standing.Model is not null && !_bounds.EvaluateNow() )
			return;

		// NEVER swap in a collider whose clay is EMBEDDED in the world (deeper than the backstop tolerance).
		// An embedded collider is what the physics solver depenetrates by shoving the whole prop — with deep
		// penetration, straight through thin walls and floors (the clip-through exploit). The live drag paths
		// can't get here (BrushWorldClamp stops the active brush at the surface), so this catches the
		// discrete bypasses: undo/redo splices, layer-row toggles, conversions, loads. Same freeze semantics
		// and the same owner-side/standing-collider gating as the bounds check above — the standing collider
		// keeps the prop solid on its last good shape, and the next clean commit rebuilds as normal. Trigger
		// builds (the hunter head) are exempt: they generate no contacts, so embedding them shoves nothing.
		if ( !IsProxy && !BuildAsTrigger && standing.IsValid() && standing.Model is not null
			&& BrushWorldClamp.EmbeddedInWorld( _sculpture ) )
			return;

		// The carved-copy record keeps the foot probes in lockstep with the collider: any copy Build swaps to
		// voxel boxes gets its probes placed ON those boxes (see ComputeFootPoints), not on the smooth field.
		// The frame points ride the same build (see FramePoints).
		var carved = new List<SdfCollisionBuilder.CarvedCopy>();
		var model = SdfCollisionBuilder.Build( brushes, carved, _framePoints );

		var collider = standing.IsValid() ? standing : GameObject.Components.GetOrCreate<ModelCollider>();
		collider.Model = model;
		collider.IsTrigger = BuildAsTrigger;
		collider.Enabled = model is not null;

		// Footprint snapshot for ground probes — computed AFTER the collider is fully set up, and guarded, so a
		// problem here can NEVER stop the sculpture from being solid (the collider build must not depend on it).
		//
		// The probes trace WORLD-down, but the snapshot is sculpture-local: a sculpture standing tilted (a scene
		// prop placed on its side — conversion moves that tilt onto the disguise child) has its real underside on
		// a local side face. Hand the builder the local→down-aligned frame so the probes land on the face resting
		// on the floor. Built from the CURRENT rotation, yet stable at runtime: the pawn only ever yaws, and yaw
		// cancels out of world-down expressed locally. Near-upright snaps to null (the common case, and float
		// noise in a live rotation must not perturb the standing behaviour).
		try
		{
			var downLocal = WorldRotation.Inverse * Vector3.Down;
			Rotation? probeFrame = null;
			if ( downLocal.z > -0.9999f )
				probeFrame = downLocal.z > 0.9999f
					? Rotation.FromAxis( Vector3.Forward, 180f ) // upside-down: FromToRotation's axis degenerates
					: Rotation.FromToRotation( downLocal, Vector3.Down );

			_footPoints = brushes is not null
				? SdfCollisionBuilder.ComputeFootPoints( brushes, FootProbeSpacing, carved, probeFrame )
				: new();
		}
		catch
		{
			_footPoints = new();
		}
	}

	// ── Diagnostics ──────────────────────────────────────────────────────────────────────────────────
	// Prints what the physics world ACTUALLY enforces for the hunter head: the live headcollider rules
	// (Ignore = the Collision.config rule is loaded; Collide = the editor is serving a stale cached
	// config — restart it) and every SdfCollider's effective tag set (what its shapes are stamped with).
	[ConCmd( "mimi_collision_debug" )]
	public static void CollisionDebugCmd()
	{
		var scene = Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		Log.Info( $"rule headcollider vs world: {scene.PhysicsWorld.GetCollisionRule( "headcollider", "world" )}" );
		Log.Info( $"rule headcollider vs solid: {scene.PhysicsWorld.GetCollisionRule( "headcollider", "solid" )}" );

		foreach ( var c in scene.GetAllComponents<SdfCollider>() )
		{
			var mc = c.GameObject.Components.Get<ModelCollider>();
			Log.Info( $"  {c.GameObject.Name}: tags=[{string.Join( ",", c.GameObject.Tags.TryGetAll() )}] " +
				$"collider={(mc.IsValid() && mc.Enabled ? "enabled" : "off")} " +
				$"trigger={(mc.IsValid() && mc.IsTrigger)}" );
		}

		// The native contact filter runs on PHYSICS SHAPE tags, not GameObject tags — dump what each pawn
		// rigidbody's shapes are actually stamped with, so a tag that never reached the shape shows up here.
		foreach ( var rb in scene.GetAllComponents<Rigidbody>() )
		{
			if ( !rb.PhysicsBody.IsValid() )
				continue;

			Log.Info( $"  body \"{rb.GameObject.Name}\" ({rb.PhysicsBody.BodyType}):" );
			foreach ( var shape in rb.PhysicsBody.Shapes )
				Log.Info( $"    shape tags=[{string.Join( ",", shape.Tags.TryGetAll() )}] trigger={shape.IsTrigger}" );
		}
	}
}
