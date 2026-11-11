using System;
using System.Collections.Generic;
using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// Runs real physics on the selected objects in the EDITOR scene, so props can be dropped / tossed and left where
/// they land, which is how you scatter set dressing. Drives the engine's own editor physics stepper (the one its
/// "Physics Simulation" tool uses: <c>Scene.EnableEditorPhysics</c> + <c>EnableEditorRigidBody</c>). Only the
/// registered bodies turn dynamic; everything else in the world stays static.
///
/// Clay props have no collision in the editor, because <see cref="SdfCollider"/> is runtime only, and they have
/// no Rigidbody. So for the length of a run every simulated object gets a temporary hidden, unsaved Rigidbody, and
/// every clay prop gets a temporary ModelCollider built by <see cref="SdfCollisionBuilder"/>, the same shape it
/// collides with in play. The selected props get dynamic colliders; with <see cref="Settings.ClayCollides"/> on,
/// the unselected props in the scene get static ones, so the selection can land on a clay table. All of it is torn
/// down when the run ends.
///
/// Selecting a plain parent (a folder of props) simulates each prop under it separately. "Stop" keeps the result as
/// ONE undo step (Ctrl+Z puts everything back); "Cancel" puts everything back immediately.
///
/// UI: <see cref="PhysicsScatterPanel"/>, a viewport overlay stacked on the Collision panel, shown only while the
/// selection holds something this can simulate.
/// </summary>
public static class PhysicsScatter
{
	/// <summary>The dock's knobs, persisted in editor cookies.</summary>
	public sealed class Settings
	{
		const string Prefix = "Mimiclay.PhysicsScatter.";

		/// <summary>Random sideways speed each object is launched with (units/s, random direction, 50–100% of this).
		/// 0 = just drop.</summary>
		[Range( 0f, 1000f ), Step( 10f )]
		public float Scatter
		{
			get => EditorCookie.Get( Prefix + "Scatter", 0f );
			set => EditorCookie.Set( Prefix + "Scatter", value );
		}

		/// <summary>Upward launch speed (units/s), so objects hop before they land.</summary>
		[Range( 0f, 1000f ), Step( 10f )]
		public float Lift
		{
			get => EditorCookie.Get( Prefix + "Lift", 0f );
			set => EditorCookie.Set( Prefix + "Lift", value );
		}

		/// <summary>Random tumble each object is launched with (degrees/s, around a random axis).</summary>
		[Range( 0f, 1440f ), Step( 30f )]
		public float Spin
		{
			get => EditorCookie.Get( Prefix + "Spin", 0f );
			set => EditorCookie.Set( Prefix + "Spin", value );
		}

		/// <summary>Stop by itself once everything has come to rest.</summary>
		[Title( "Stop when settled" )]
		public bool StopWhenSettled
		{
			get => EditorCookie.Get( Prefix + "StopWhenSettled", true );
			set => EditorCookie.Set( Prefix + "StopWhenSettled", value );
		}

		/// <summary>Longest a run may go before it stops by itself (seconds). 0 = until you press Stop.</summary>
		[Title( "Time limit" ), Range( 0f, 60f ), Step( 1f )]
		public float MaxSeconds
		{
			get => EditorCookie.Get( Prefix + "MaxSeconds", 15f );
			set => EditorCookie.Set( Prefix + "MaxSeconds", value );
		}

		/// <summary>Give the UNSELECTED clay props in the scene temporary static collision, so the selection can
		/// land on them. Off = only the world's real colliders (map meshes, model colliders) are solid.</summary>
		[Title( "Land on clay props" )]
		public bool ClayCollides
		{
			get => EditorCookie.Get( Prefix + "ClayCollides", true );
			set => EditorCookie.Set( Prefix + "ClayCollides", value );
		}
	}

	public static Settings Options { get; } = new();

	public static bool IsSimulating => _scene is not null;
	public static int BodyCount => _bodies.Count;
	public static float Elapsed => IsSimulating ? (float)_since : 0f;

	/// <summary>One line about the last run (or why it couldn't start), for the dock.</summary>
	public static string Status { get; private set; } = "";

	sealed class Body
	{
		public GameObject GameObject;
		public Rigidbody Rigidbody;
		public Transform Start;
	}

	static readonly List<Body> _bodies = new();
	static readonly List<Component> _temporary = new();
	static Scene _scene;
	static SceneEditorSession _session;
	static RealTimeSince _since;
	static RealTimeSince _calmFor;

	// ── Picking what to simulate ─────────────────────────────────────────────────────────────────────

	/// <summary>The objects a run would simulate right now: each selected object that can be a body, or, for a
	/// selected plain parent, every such object beneath it. A selection inside another selected object is covered
	/// by its ancestor.</summary>
	public static List<GameObject> Candidates()
	{
		var result = new List<GameObject>();
		var session = SceneEditorSession.Active;
		if ( session?.Scene is not { IsEditor: true } )
			return result;

		var selected = session.Selection.OfType<GameObject>().Where( go => go.IsValid() && go.Active ).ToHashSet();
		foreach ( var go in selected )
		{
			if ( HasAncestorIn( go, selected ) )
				continue;

			Collect( go, result );
		}

		return result;
	}

	static bool HasAncestorIn( GameObject go, HashSet<GameObject> set )
	{
		for ( var p = go.Parent; p.IsValid() && p is not Scene; p = p.Parent )
			if ( set.Contains( p ) )
				return true;
		return false;
	}

	static void Collect( GameObject go, List<GameObject> into )
	{
		if ( !go.Active )
			return;

		if ( CanBeBody( go ) )
		{
			into.Add( go );
			return;
		}

		foreach ( var child in go.Children )
			Collect( child, into );
	}

	/// <summary>Is this a PROP that can be a body: clay, a Rigidbody, or a model with physics? A bare collider
	/// (a floor mesh, a blockout box) doesn't count, so selecting world geometry never brings the panel up.</summary>
	static bool CanBeBody( GameObject go )
	{
		var c = go.Components;
		return c.Get<Rigidbody>( FindMode.EverythingInSelf ) is not null
			|| c.GetAll<SdfCollider>( FindMode.EverythingInSelf ).Any( x => x.Enabled )
			|| c.Get<ModelRenderer>( FindMode.EnabledInSelf ) is { Model.Physics: not null };
	}

	// ── Run ──────────────────────────────────────────────────────────────────────────────────────────

	public static void Start()
	{
		if ( IsSimulating )
			return;

		var session = SceneEditorSession.Active;
		if ( session?.Scene is not { IsEditor: true } scene )
		{
			Status = "Open a scene in the editor first (this doesn't run in play mode).";
			return;
		}

		var candidates = Candidates();
		if ( candidates.Count == 0 )
		{
			Status = "Nothing selected can be simulated.";
			return;
		}

		_scene = scene;
		_session = session;
		_bodies.Clear();
		_temporary.Clear();

		var simulated = candidates.ToHashSet();
		int skipped = 0;

		foreach ( var go in candidates )
		{
			if ( !MakeBody( go ) )
				skipped++;
		}

		int envClay = Options.ClayCollides ? AddEnvironmentClay( scene, simulated ) : 0;

		if ( _bodies.Count == 0 )
		{
			Teardown();
			Status = "Nothing selected has collision to simulate with.";
			return;
		}

		scene.EnableEditorPhysics( true );

		var rng = new Random();
		foreach ( var body in _bodies )
		{
			scene.EnableEditorRigidBody( body.Rigidbody, true );
			Launch( body.Rigidbody, rng );
		}

		_since = 0;
		_calmFor = 0;

		Status = $"Simulating {_bodies.Count} object{(_bodies.Count == 1 ? "" : "s")}"
			+ (skipped > 0 ? $" ({skipped} skipped: no collision or a disabled Rigidbody)" : "")
			+ (envClay > 0 ? $", landing on {envClay} clay prop{(envClay == 1 ? "" : "s")}" : "") + ".";
	}

	/// <summary>Give one candidate a dynamic body: its own Rigidbody when it has one, else a temporary one, plus
	/// temporary colliders for any clay in it (and its model's physics when it has nothing else).</summary>
	static bool MakeBody( GameObject go )
	{
		var rb = go.Components.Get<Rigidbody>( FindMode.EverythingInSelf );
		bool ownRigidbody = rb is not null;
		if ( ownRigidbody && !rb.Enabled )
			return false; // switching it on would change saved state; leave it alone

		int tempBefore = _temporary.Count;

		// Rigidbody FIRST, so the colliders below attach to its body instead of making static bodies of their own.
		if ( !ownRigidbody )
		{
			rb = go.Components.Create<Rigidbody>( false );
			rb.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved;
			rb.Enabled = true;
			_temporary.Add( rb );
		}

		foreach ( var sdf in go.Components.GetAll<SdfCollider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( sdf.Enabled && sdf.GameObject.Active )
				AddClayCollider( sdf );
		}

		if ( go.Components.Get<Collider>( FindMode.EnabledInSelfAndDescendants ) is null
			&& go.Components.Get<ModelRenderer>( FindMode.EnabledInSelf ) is { Model.Physics: not null } renderer )
		{
			var mc = go.Components.Create<ModelCollider>( false );
			mc.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved;
			mc.Model = renderer.Model;
			mc.Enabled = true;
			_temporary.Add( mc );
		}

		if ( go.Components.Get<Collider>( FindMode.EnabledInSelfAndDescendants ) is null )
		{
			// Nothing to collide with: undo what this object got and skip it.
			for ( int i = _temporary.Count - 1; i >= tempBefore; i-- )
			{
				_temporary[i].Destroy();
				_temporary.RemoveAt( i );
			}
			return false;
		}

		_bodies.Add( new Body { GameObject = go, Rigidbody = rb, Start = go.WorldTransform } );
		return true;
	}

	/// <summary>A temporary ModelCollider with the shape this clay prop collides with in play. Skips triggers and
	/// objects that already carry a collider of their own.</summary>
	static bool AddClayCollider( SdfCollider sdf )
	{
		if ( sdf.BuildAsTrigger || sdf.GameObject.Components.Get<ModelCollider>( FindMode.EverythingInSelf ) is not null )
			return false;

		var brushes = sdf.CollisionBrushes();
		if ( brushes is null || brushes.Count == 0 )
			return false;

		Model model;
		try
		{
			model = SdfCollisionBuilder.Build( brushes, maxShapes: sdf.MaxShapes > 0 ? sdf.MaxShapes : SdfCollisionBuilder.DefaultMaxShapes );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Physics Scatter] Couldn't build collision for '{sdf.GameObject.Name}': {e.Message}" );
			return false;
		}

		if ( model is null )
			return false;

		var mc = sdf.GameObject.Components.Create<ModelCollider>( false );
		mc.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved;
		mc.Model = model;
		mc.Surface = SdfCollider.ClaySurface;
		mc.Enabled = true;
		_temporary.Add( mc );
		return true;
	}

	/// <summary>Static collision for every clay prop that isn't being simulated (no Rigidbody = static in the editor).</summary>
	static int AddEnvironmentClay( Scene scene, HashSet<GameObject> simulated )
	{
		int count = 0;
		foreach ( var sdf in scene.Components.GetAll<SdfCollider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( !sdf.Enabled || !sdf.GameObject.Active || IsUnder( sdf.GameObject, simulated ) )
				continue;

			if ( AddClayCollider( sdf ) )
				count++;
		}
		return count;
	}

	static bool IsUnder( GameObject go, HashSet<GameObject> set )
	{
		for ( var p = go; p.IsValid() && p is not Scene; p = p.Parent )
			if ( set.Contains( p ) )
				return true;
		return false;
	}

	static void Launch( Rigidbody rb, Random rng )
	{
		var velocity = Vector3.Zero;

		if ( Options.Scatter > 0f )
		{
			float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
			float speed = Options.Scatter * (0.5f + 0.5f * (float)rng.NextDouble());
			velocity += new Vector3( MathF.Cos( angle ), MathF.Sin( angle ), 0f ) * speed;
		}

		velocity += Vector3.Up * Options.Lift;

		if ( velocity != Vector3.Zero )
			rb.Velocity = velocity;

		if ( Options.Spin > 0f )
		{
			var axis = new Vector3( (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f );
			if ( axis.Length < 0.01f )
				axis = Vector3.Up;
			float rate = Options.Spin * (0.5f + 0.5f * (float)rng.NextDouble());
			rb.AngularVelocity = axis.Normal * MathX.DegreeToRadian( rate );
		}
	}

	[EditorEvent.Frame]
	static void Tick()
	{
		if ( !IsSimulating )
			return;

		// The scene closed, or play started: the objects are gone or not ours to keep.
		if ( !_scene.IsValid() || SceneEditorSession.Active?.Scene != _scene )
		{
			Cancel();
			Status = "Stopped: the scene changed.";
			return;
		}

		_bodies.RemoveAll( b => !b.GameObject.IsValid() || !b.Rigidbody.IsValid() );
		if ( _bodies.Count == 0 )
		{
			Cancel();
			Status = "Stopped: the simulated objects were deleted.";
			return;
		}

		if ( Options.MaxSeconds > 0f && _since > Options.MaxSeconds )
		{
			Stop( $"Stopped at the {Options.MaxSeconds:0} s time limit" );
			return;
		}

		if ( !Options.StopWhenSettled || _since < 0.3f )
			return;

		bool calm = _bodies.All( b =>
		{
			var body = b.Rigidbody.PhysicsBody;
			return !body.IsValid() || body.Sleeping
				|| (body.Velocity.Length < 2f && body.AngularVelocity.Length < 0.2f);
		} );

		if ( !calm )
			_calmFor = 0;
		else if ( _calmFor > 0.4f )
			Stop( $"Settled after {Elapsed:0.0} s" );
	}

	/// <summary>End the run and keep where everything landed, as one undo step.</summary>
	public static void Stop() => Stop( null );

	static void Stop( string reason )
	{
		if ( !IsSimulating )
			return;

		float elapsed = Elapsed;
		var bodies = _bodies.ToList();
		var finals = bodies.Select( b => b.GameObject.WorldTransform ).ToList();
		var session = _session;

		Teardown();

		// Put everything back first, then make the move inside the undo scope, so the undo step holds the
		// original transforms rather than where the physics left them.
		foreach ( var b in bodies )
			b.GameObject.WorldTransform = b.Start;

		using ( session.UndoScope( "Physics Scatter" ).WithGameObjectChanges( bodies.Select( b => b.GameObject ), GameObjectUndoFlags.Properties ).Push() )
		{
			for ( int i = 0; i < bodies.Count; i++ )
				bodies[i].GameObject.WorldTransform = finals[i];
		}

		Status = $"{reason ?? $"Kept the result after {elapsed:0.0} s"}. Ctrl+Z puts everything back.";
	}

	/// <summary>End the run and put everything back where it started.</summary>
	public static void Cancel()
	{
		if ( !IsSimulating )
			return;

		var bodies = _bodies.ToList();
		Teardown();

		foreach ( var b in bodies )
		{
			if ( b.GameObject.IsValid() )
				b.GameObject.WorldTransform = b.Start;
		}

		Status = "Cancelled: everything is back where it started.";
	}

	/// <summary>Make every body static again and remove everything this run added.</summary>
	static void Teardown()
	{
		if ( _scene.IsValid() )
		{
			foreach ( var b in _bodies )
			{
				if ( b.Rigidbody.IsValid() )
				{
					b.Rigidbody.Velocity = Vector3.Zero;
					b.Rigidbody.AngularVelocity = Vector3.Zero;
					_scene.EnableEditorRigidBody( b.Rigidbody, false );
				}
			}

			_scene.EnableEditorPhysics( false );
		}

		foreach ( var c in _temporary )
		{
			if ( c.IsValid() )
				c.Destroy();
		}

		_temporary.Clear();
		_bodies.Clear();
		_scene = null;
		_session = null;
	}
}
