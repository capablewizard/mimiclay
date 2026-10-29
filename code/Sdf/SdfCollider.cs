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
/// Shot craters are NOT part of that shape: the build reads the authored brushes only, so cosmetic damage
/// never changes physics and never costs a rebuild (<see cref="CarveCollision"/> is the future opt-in).
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
	/// <summary>Build the collider as a TRIGGER instead of a solid: it generates no physical contacts (never
	/// blocks or pushes anything), but the gun's rays still hit it because they opt in with HitTriggers().
	/// The hunter head's mode — its collider exists purely for bullet hit detection. Default off: disguises,
	/// decoys and world props stay exactly as solid as they look.</summary>
	[Property] public bool BuildAsTrigger { get; set; }

	/// <summary>Let shot craters (<see cref="SdfBrush.Damage"/> brushes) carve the COLLIDER too. Off everywhere
	/// today: shots are cosmetic, the collider is built from the authored brushes alone, and a crater landing or
	/// healing never rebuilds physics (see <see cref="Rebuild"/>). The switch exists so a mode can one day make
	/// shots physically open a prop up — the carve-aware builder already knows how.</summary>
	[Property] public bool CarveCollision { get; set; }

	// The brushes the collider is built from: the authored prefix, or everything when craters are opted in.
	// The damage tail is contiguous at the END (SdfSculpture.AuthoredBrushCount), so this is a cheap range copy.
	List<SdfBrush> BuildBrushes( List<SdfBrush> all )
	{
		if ( all is null || CarveCollision )
			return all;

		int authored = _sculpture.IsValid() ? _sculpture.AuthoredBrushCount : all.Count;
		return authored == all.Count ? all : all.GetRange( 0, authored );
	}

	// Key of the build the standing collider came from (authored-brush hash + probe frame) — see Rebuild's skip.
	int? _builtKey;

	/// <summary>How many collider builds have actually run on this machine (skips excluded). Diagnostics.</summary>
	public static int BuildCount { get; private set; }

	/// <summary>Tag stamped onto every SDF-collider GameObject (runtime only — this component never runs in
	/// the editor, so it's never saved into assets). Lets traces treat clay props differently from world
	/// geometry — the camera boom's world-only mode filters on it.</summary>
	public const string ClayTag = "clay";

	/// <summary>The physics surface every SDF collider is built with: default physics (density, friction, bounce
	/// — prop masses and handling are unchanged) with the physics splat (splat_physics.sound — separate from the
	/// gun's SplatSound) as its impact sounds. Played through
	/// <see cref="Surface.PlayCollisionSound"/> by props that are simulating freely (see HiderController's
	/// impact sounds).</summary>
	public const string ClaySurfacePath = "surfaces/clay.surface";

	public static Surface ClaySurface => ResourceLibrary.Get<Surface>( ClaySurfacePath );

	/// <summary>Bumped on every collider build that actually ran — consumers that derive data from the built
	/// shape (the prop's move hull) compare it to know when to re-derive.</summary>
	public int BuildSerial { get; private set; }

	/// <summary>The clay's solid volume (sculpture units³) for the shape last built — see
	/// <see cref="SdfCollisionBuilder.ClayVolume"/>. Sampled on the first read after a build (and shared by every
	/// sculpture of the same shape), so scenery that never weighs anything never pays for it. 0 until it builds.</summary>
	public float ClayVolume
	{
		get
		{
			if ( _clayVolume is null )
			{
				try
				{
					_clayVolume = _volumeBrushes is not null ? SdfCollisionBuilder.ClayVolume( _volumeBrushes ) : 0f;
				}
				catch
				{
					_clayVolume = 0f; // mass is a nicety; never let it throw into the controller
				}
			}
			return _clayVolume.Value;
		}
	}

	float? _clayVolume;
	List<SdfBrush> _volumeBrushes; // the brush list of the last build, for the lazy volume

	/// <summary>The mass (kg) the clay should have: its true volume at the clay surface's density. The engine's
	/// automatic mass sums the collider's SHAPES instead, and those overlap (buried capsule roots, voxel boxes
	/// under hulls, mirror copies meeting at the plane), so a prop would weigh more than its clay. 0 until it
	/// builds, and for a trigger build (nothing to weigh).</summary>
	public float ClayMass => BuildAsTrigger ? 0f : ClayVolume * MetresPerUnitCubed * ClaySurface.Density;

	// Sculpture units are inches; Surface.Density is kg/m³.
	const float MetresPerUnitCubed = 0.0254f * 0.0254f * 0.0254f;

	// Sculpture-local points outlining every shape the last build EMITTED — hull vertices, voxel-box corners,
	// points on each collision sphere — captured by SdfCollisionBuilder.Build itself, so they carry its carve
	// decisions exactly (a hollowed bin's frame stops at its real base, not the uncarved cylinder's).
	readonly List<Vector3> _framePoints = new();

	/// <summary>Sculpture-local points outlining the ACTUAL emitted collider (empty until the first build) —
	/// project them to frame the visible clay on screen (the creative-mode "E to Edit" prompt). Anything
	/// derived from the brush list instead cannot know what a subtract carved away.</summary>
	public IReadOnlyList<Vector3> FramePoints => _framePoints;

	// Kind + source brush of every shape the last build emitted, in emission order (the debug view's colouring).
	readonly List<SdfCollisionBuilder.EmittedShape> _emitted = new();

	/// <summary>The collision model this component last built (null until it builds) and the kind + source brush of
	/// each shape in it — what <see cref="SdfCollisionDebug"/> draws at runtime.</summary>
	public Model BuiltModel => _builtModel;
	public IReadOnlyList<SdfCollisionBuilder.EmittedShape> EmittedShapes => _emitted;

	// The collision debug view's derived draw data (and, in the editor, its private preview build).
	internal SdfCollisionDebug.Snapshot DebugSnapshot;

	SdfSculpture _sculpture;
	SculptBounds _bounds;
	Model _builtModel; // the model THIS component last assigned to the sibling ModelCollider (null until it builds)

	/// <summary>May an invalid/embedded commit leave the standing collider in place instead of building? Only
	/// while a LOCAL edit session is actively targeting this sculpture AND the standing model is one this
	/// component built. The freeze exists for editing: the standing collider is then the session's "last good
	/// shape" — the one proxies render and the exit-revert restores — so holding it is correct. Outside a
	/// session every shape change is AUTHORITATIVE and must build: spawn dress (<see cref="HiderController.WearDisguise"/>,
	/// <c>HunterController.WearFace</c>), lobby restores, persist-slot loads, proxy applies. Those replace the
	/// brushes wholesale on a clone whose collider ALREADY stands — the disguise/face template's default brush
	/// was built the moment the template cloned, before the real brushes arrived — so a standing collider is
	/// no evidence of a last good shape there. Gating on "a collider exists" froze a scene mop that overflowed
	/// the template's bounds (or leaned into a wall) onto the template SPHERE: one tiny collider, pawn through
	/// the floor. The model-identity check additionally ignores a Model a prefab deserialized into the
	/// ModelCollider (the procedural placeholder path), which nothing ever built.</summary>
	bool CanFreezeOn( ModelCollider standing )
	{
		if ( _builtModel is null || !ReferenceEquals( standing.Model, _builtModel ) )
			return false;

		return SculptEditSession.Current is { IsEditing: true } s && s.Target == _sculpture;
	}

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
		_builtModel = null; // the next enable's build is a first build again — never a freeze candidate
		_builtKey = null;   // ...and never a skip candidate
		_framePoints.Clear();
		_emitted.Clear();
	}

	/// <summary>The brushes this collider builds from — the authored prefix, or everything when
	/// <see cref="CarveCollision"/> opts the craters in. Null without a sibling sculpture.</summary>
	public List<SdfBrush> CollisionBrushes()
	{
		_sculpture ??= GameObject.Components.Get<SdfSculpture>();
		return BuildBrushes( _sculpture.IsValid() ? _sculpture.Brushes : null );
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
		var all = _sculpture.IsValid() ? _sculpture.Brushes : null;

		// Shot craters are COSMETIC: the collider is built from the AUTHORED brushes only (the prefix before the
		// damage tail — see SdfSculpture.AuthoredBrushCount), unless CarveCollision opts the crater subtracts in.
		// The authored prefix is hashed so a commit that only touched the damage tail — a pellet landing, every
		// heal step, the heal's final rebuild — is a no-op here: the standing collider IS the collider for this
		// shape. That matters more than it sounds: with craters in the brush list, each of those commits ran the
		// carve-aware builder (BuildCarved: a 14³ field grid per bitten copy, each sample evaluating the whole
		// brush list), which on a many-brush head was a visible stall on EVERY machine per shot (the carve is
		// broadcast), and the voxel boxes it swapped in multiplied the hull count the body then carried.
		//
		// The skip only trusts a standing collider THIS component built (model identity, same test the freeze
		// uses) — a prefab-deserialized placeholder or a disabled/absent collider always builds. Keyed on the
		// authored hash: the collider is pure sculpture-local geometry, so tilt and position never matter.
		var standing = GameObject.Components.Get<ModelCollider>();
		var brushes = BuildBrushes( all );
		int key = HashCode.Combine(
			SdfSculpture.ContentHashPrefix( brushes, brushes?.Count ?? 0, 0, false ),
			BuildAsTrigger, SdfCollisionBuilder.CodeVersion );

		if ( key == _builtKey && _builtModel is not null
			&& standing.IsValid() && standing.Enabled && ReferenceEquals( standing.Model, _builtModel ) )
			return;

		BuildCount++;

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
		bool mayFreeze = !IsProxy && standing.IsValid() && CanFreezeOn( standing );
		if ( mayFreeze && _bounds.IsValid() && !_bounds.EvaluateNow() )
			return;

		// NEVER swap in a collider whose clay is EMBEDDED in the world (deeper than the backstop tolerance).
		// An embedded collider is what the physics solver depenetrates by shoving the whole prop — with deep
		// penetration, straight through thin walls and floors (the clip-through exploit). The live drag paths
		// can't get here (BrushWorldClamp stops the active brush at the surface), so this catches the
		// discrete bypasses: undo/redo splices, layer-row toggles, conversions, loads. Same freeze semantics
		// and the same owner-side/standing-collider gating as the bounds check above — the standing collider
		// keeps the prop solid on its last good shape, and the next clean commit rebuilds as normal. Trigger
		// builds (the hunter head) are exempt: they generate no contacts, so embedding them shoves nothing.
		if ( mayFreeze && !BuildAsTrigger && BrushWorldClamp.EmbeddedInWorld( _sculpture ) )
			return;

		// The frame points ride the same build (see FramePoints) — the prop's move hull is derived from them.
		var model = SdfCollisionBuilder.Build( brushes, null, _framePoints, _emitted );

		var collider = standing.IsValid() ? standing : GameObject.Components.GetOrCreate<ModelCollider>();
		collider.Model = model;
		collider.Surface = ClaySurface;
		collider.IsTrigger = BuildAsTrigger;
		collider.Enabled = model is not null;
		_builtModel = model;
		_builtKey = key;
		BuildSerial++;

		_volumeBrushes = brushes;
		_clayVolume = null; // re-sampled on the next ClayVolume read
	}

	// Editor + runtime collision view (the viewport's Collision panel) — see SdfCollisionDebug.
	protected override void DrawGizmos() => SdfCollisionDebug.Draw( this );

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
