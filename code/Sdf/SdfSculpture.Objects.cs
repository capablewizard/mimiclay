using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The OBJECT form of a sculpture: brushes as child GameObjects (<see cref="SdfBrushComponent"/>) instead of
/// rows in <see cref="Brushes"/>. An editor-only authoring form — the native editor gives GameObjects everything
/// precise work needs (gizmo + snapping, multi-select, copy/paste transform, duplicate, hierarchy reorder,
/// pivots, undo) and none of it reaches a list entry.
/// <para>ONE direction each way: the editor GATHERS the children into <see cref="Brushes"/> whenever they
/// change (so every consumer keeps reading the list, and the saved prefab carries a matching snapshot); at
/// runtime <see cref="FlattenObjects"/> gathers once in OnAwake and destroys the children, so the wire, the
/// .sculpt saves and the in-game editor see a plain list — the runtime model is untouched.</para>
/// </summary>
public sealed partial class SdfSculpture
{
	/// <summary>True = the brushes are child objects (see <see cref="SdfBrushComponent"/>) and <see cref="Brushes"/>
	/// is a snapshot gathered from them. Flipped by the convert buttons, never by hand. Always false at runtime.</summary>
	[Property, Group( "Brush Objects" ), ReadOnly] public bool BrushObjects { get; set; }

	/// <summary>Editor-side wrappers (undo scopes) for the two conversions; null at runtime, where the raw
	/// conversions run instead.</summary>
	public static Func<SdfSculpture, bool> ExplodeHandler;
	public static Func<SdfSculpture, bool> CollapseHandler;

	/// <summary>Turn the brush list into child brush objects (one GameObject per authored brush, spline points
	/// as grandchildren). The list stays as a snapshot.</summary>
	[Button( "Convert To Brush Objects" ), Group( "Brush Objects" )]
	public void ExplodeButton()
	{
		if ( BrushObjects ) { Log.Info( "SdfSculpture: already brush objects." ); return; }
		if ( ExplodeHandler is not null ) ExplodeHandler( this ); else ExplodeToObjects();
	}

	/// <summary>Gather the brush objects back into the list and delete them (pivots included).</summary>
	[Button( "Collapse To Brush List" ), Group( "Brush Objects" )]
	public void CollapseButton()
	{
		if ( !BrushObjects ) { Log.Info( "SdfSculpture: already a brush list." ); return; }
		if ( CollapseHandler is not null ) CollapseHandler( this ); else CollapseToList();
	}

	/// <summary>The brush components under this sculpture in BRUSH ORDER: depth-first, sibling order. A nested
	/// <see cref="SdfSculpture"/> owns its own subtree and is skipped; an object with a brush component is a leaf
	/// (its children are spline points, never brushes).</summary>
	public IEnumerable<SdfBrushComponent> BrushComponents()
	{
		foreach ( var child in GameObject.Children.ToArray() )
			foreach ( var b in Walk( child ) )
				yield return b;
	}

	static IEnumerable<SdfBrushComponent> Walk( GameObject go )
	{
		if ( !go.IsValid() || go.Components.Get<SdfSculpture>( true ) is not null )
			yield break;

		var brush = go.Components.Get<SdfBrushComponent>( true );
		if ( brush is not null )
		{
			yield return brush;
			yield break;
		}

		foreach ( var child in go.Children.ToArray() )
			foreach ( var b in Walk( child ) )
				yield return b;
	}

	/// <summary>A fresh brush list built from the child objects (sculpture space). Re-mints the id of any
	/// object that duplicates another's (Ctrl+D copies the hidden id along with everything else) so two brushes
	/// never share one. Stops at the brush cap like <see cref="AddBrush"/>.</summary>
	public List<SdfBrush> GatherFromObjects()
	{
		var world = WorldTransform;
		var list = new List<SdfBrush>();
		var seen = new HashSet<Guid>();

		foreach ( var c in BrushComponents() )
		{
			if ( !seen.Add( c.BrushId ) )
			{
				c.BrushId = Guid.NewGuid();
				seen.Add( c.BrushId );
			}

			if ( list.Count >= SdfBrushPacker.MaxBrushes )
			{
				Log.Warning( $"SdfSculpture: brush cap reached ({SdfBrushPacker.MaxBrushes}) — brush objects past it are ignored." );
				break;
			}

			list.Add( c.ToBrush( world ) );
		}

		return list;
	}

	/// <summary>The brushes as every consumer should see them: gathered live from the objects in object form,
	/// the list itself otherwise. Use this (not <see cref="Brushes"/>) when reading a sculpture that may not
	/// have had its editor tick yet — a prefab scene, say.</summary>
	public List<SdfBrush> EffectiveBrushes() => BrushObjects ? GatherFromObjects() : Brushes;

	int _objectsHash;
	bool _objectsSettling;

	/// <summary>EDITOR, object form: the id of the brush that owns the visible surface under the cursor this
	/// frame (<see cref="Sdf.PickBrush"/> with the surface preferred), or Guid.Empty. Computed in the sculpture's
	/// DrawGizmos — which the engine runs BEFORE the children's — so each <see cref="SdfBrushComponent"/> can
	/// tell whether it is the one that should register a volume hitbox. Never serialized.</summary>
	public Guid EditorHoverBrushId;

	/// <summary>EDITOR, object form: distance along the cursor ray to whatever is in front this frame — the nearest
	/// render mesh in the scene OR this sculpture's own marched surface, whichever is nearer — or MaxValue. Wire
	/// click targets cap their score at it, so a wire always beats the clay it is drawn over without ever
	/// out-scoring a transform-gizmo handle (see <see cref="SdfBrushGizmos"/>).</summary>
	public float EditorOccluderDistance = float.MaxValue;

	/// <summary>EDITOR, object form: distance along the cursor ray to this sculpture's visible surface, or
	/// MaxValue. The brush that owns that surface registers its VOLUME click target at (just under) it.</summary>
	public float EditorSurfaceDistance = float.MaxValue;

	/// <summary>EDITOR, object form: the brush object or spline point whose WIRE is under the cursor in SCREEN space
	/// this frame (within <see cref="PickPixels"/>), or null. Decided here, once per sculpture, so exactly one of
	/// our objects enters the engine's hover race per sculpture — a pixel tolerance means the same thing at every
	/// zoom, and there is nothing to tie.</summary>
	public GameObject EditorPickTarget;

	/// <summary>Camera distance of the picked wire's closest point (what the pick is scored at).</summary>
	public float EditorPickDistance = float.MaxValue;

	/// <summary>Screen-space pick tolerance, in pixels, from the cursor to a wire.</summary>
	public const float PickPixels = 20f;

	/// <summary>EDITOR, object form: the cursor is within <see cref="GizmoScreenRadiusPx"/> of a selected object on
	/// screen this frame — where the native transform gizmo lives — so our picks use their plain scores there and
	/// let the gizmo's handles win (see <see cref="SdfBrushGizmos.StrongScale"/>).</summary>
	public bool EditorNearGizmo;

	/// <summary>Screen radius (px) around a selected object's pivot that counts as "on the transform gizmo".</summary>
	public const float GizmoScreenRadiusPx = 140f;

	// Project every brush wire (mirror copies included) and every spline point ring to the screen and keep the one
	// closest to the cursor, nearest-in-depth among near-equal pixel distances.
	void UpdateEditorPick( in Transform world )
	{
		EditorPickTarget = null;
		EditorPickDistance = float.MaxValue;
		EditorNearGizmo = false;

		if ( Gizmo.Camera is not { } cam )
			return;

		var ray = Gizmo.CurrentRay;
		if ( ray.Forward.LengthSquared < 0.5f )
			return; // no cursor in the viewport this frame

		if ( !cam.ToScreen( ray.Project( 100f ), out var cursor ) )
			return;

		foreach ( var pivot in SdfBrushComponent.GizmoPivots )
		{
			if ( Vector3.Dot( pivot - cam.Position, cam.Rotation.Forward ) > 0f && cam.ToScreen( pivot, out var ps ) && ps.Distance( cursor ) < GizmoScreenRadiusPx )
			{
				EditorNearGizmo = true;
				break;
			}
		}

		var camPos = cam.Position;
		var camFwd = cam.Rotation.Forward;
		float bestScore = float.MaxValue;
		GameObject bestGo = null;
		float bestDepth = float.MaxValue;

		void Test( in Transform tx, Vector3 a, Vector3 b, GameObject go )
		{
			var wa = tx.PointToWorld( a );
			var wb = tx.PointToWorld( b );
			if ( Vector3.Dot( wa - camPos, camFwd ) <= 0f || Vector3.Dot( wb - camPos, camFwd ) <= 0f )
				return; // behind the camera: the projection is meaningless
			if ( !cam.ToScreen( wa, out var sa ) || !cam.ToScreen( wb, out var sb ) )
				return;

			var ab = sb - sa;
			float len2 = ab.LengthSquared;
			float t = len2 > 1e-6f ? Math.Clamp( Vector2.Dot( cursor - sa, ab ) / len2, 0f, 1f ) : 0f;
			float px = (sa + ab * t - cursor).Length;
			if ( px > PickPixels )
				return;

			float depth = camPos.Distance( wa + (wb - wa) * t );
			float score = px + depth * 0.002f; // pixels first; depth breaks near-ties nearest-first
			if ( score < bestScore )
			{
				bestScore = score;
				bestGo = go;
				bestDepth = depth;
			}
		}

		foreach ( var c in BrushComponents() )
		{
			if ( !c.IsValid() || !c.GameObject.Active )
				continue;

			var b = c.ToBrush( world );
			if ( b.Shape == SdfShape.Spline )
			{
				// The curve picks the spline object; a point ring picks that point (whichever is closer on screen).
				var splineGo = c.GameObject;
				var splineTx = world;
				SdfBrushGizmos.SplineCurveSegments( b, ( s, e ) => Test( splineTx, s, e, splineGo ) );
				foreach ( var child in c.GameObject.Children )
				{
					var p = child.Components.Get<SdfSplinePoint>( true );
					if ( p is null || !child.Active )
						continue;
					var ptx = new Transform( child.WorldPosition, world.Rotation, 1f );
					var go = child;
					SdfBrushGizmos.PointSegments( p.Radius, ( s, e ) => Test( ptx, s, e, go ) );
				}
				continue;
			}

			var brushGo = c.GameObject;
			for ( int sx = 0; sx <= (b.MirrorX ? 1 : 0); sx++ )
			for ( int sy = 0; sy <= (b.MirrorY ? 1 : 0); sy++ )
			for ( int sz = 0; sz <= (b.MirrorZ ? 1 : 0); sz++ )
			{
				var tx = world.ToWorld( SdfBrushGizmos.BrushCopyTransform( b, sx, sy, sz ) );
				SdfBrushGizmos.OutlineSegments( b, ( s, e ) => Test( tx, s, e, brushGo ) );
			}
		}

		EditorPickTarget = bestGo;
		EditorPickDistance = bestDepth;
	}

	// Sphere-trace the cursor ray through the real field so a click lands on the brush you can SEE, never on
	// a carver's box floating in front of it. Gated on the bounds so idle sculptures cost a box test.
	void UpdateEditorHover( in BBox localBounds )
	{
		EditorHoverBrushId = Guid.Empty;
		EditorOccluderDistance = float.MaxValue;
		EditorSurfaceDistance = float.MaxValue;
		if ( !SdfBrushComponent.GizmosVisible )
			return;

		var ray = Gizmo.CurrentRay;
		var tx = WorldTransform;
		UpdateEditorPick( tx );

		var occluder = Scene.Trace.Ray( ray, 100000f ).UseRenderMeshes( true ).UsePhysicsWorld( false ).WithoutTags( "hidden" ).Run();
		if ( occluder.Hit )
			EditorOccluderDistance = occluder.Distance;
		var invRot = tx.Rotation.Inverse;
		var o = invRot * (ray.Position - tx.Position);
		var d = (invRot * ray.Forward).Normal;

		if ( !localBounds.Trace( new Ray( o, d ), 1e6f, out _ ) )
			return;

		int idx = Sdf.PickBrush( Brushes, o, d, preferSurface: true, out float surface );
		if ( idx >= 0 && idx < Brushes.Count )
			EditorHoverBrushId = Brushes[idx].Id;

		EditorSurfaceDistance = surface;
		EditorOccluderDistance = MathF.Min( EditorOccluderDistance, surface );
	}

	// Change-hash of the whole object tree: every brush's content hash in sculpture space plus its identity
	// and the flags HashInto leaves out. Cheap enough per editor frame at the brush cap.
	int HashObjects()
	{
		var world = WorldTransform;
		unchecked
		{
			int h = (int)2166136261;
			void Mix( int x ) { h = (h ^ x) * 16777619; }

			int n = 0;
			foreach ( var c in BrushComponents() )
			{
				n++;
				var b = c.ToBrush( world );
				b.HashInto( ref h );
				Mix( b.Id.GetHashCode() );
				Mix( b.Shrinks ? 1 : 0 );
				Mix( b.LinkColor ? 1 : 0 );
				Mix( b.LinkBlend ? 1 : 0 );
			}
			Mix( n );
			return h;
		}
	}

	// Editor tick for the object form: gather on change, cheap drag proxy while things keep moving, full
	// LOD mesh the frame they settle. The raymarch renderer hashes Brushes itself, so it follows the gather.
	protected override void OnUpdate()
	{
		if ( !BrushObjects || !Scene.IsEditor )
			return;

		int h = HashObjects();
		if ( h != _objectsHash )
		{
			_objectsHash = h;
			Brushes = GatherFromObjects();
			_objectsSettling = true;
			if ( AutoRebuild )
				RebuildShadowProxy();
		}
		else if ( _objectsSettling )
		{
			_objectsSettling = false;
			if ( AutoRebuild )
				Rebuild();
		}
	}

	// Runtime: the object form never survives OnAwake — flatten before OnEnabled's Rebuild and before any
	// network snapshot can ship children.
	protected override void OnAwake()
	{
		if ( BrushObjects && !Scene.IsEditor )
			FlattenObjects();
	}

	// Belt and braces: if the tree wasn't complete at OnAwake, it is by the first update.
	protected override void OnStart()
	{
		if ( BrushObjects && !Scene.IsEditor )
			FlattenObjects();
	}

	/// <summary>Gather the brush objects into <see cref="Brushes"/>, leave object form, and destroy the objects
	/// (and any pivot that held nothing but brushes). The runtime path; also what the collapse button does.</summary>
	public void FlattenObjects()
	{
		// Never trade a real snapshot for nothing: if the objects aren't there (yet), the serialized list is
		// the gathered shape as of the last editor tick and stays authoritative.
		var gathered = GatherFromObjects();
		if ( gathered.Count > 0 || Brushes is not { Count: > 0 } )
			Brushes = gathered;
		BrushObjects = false;
		_objectsHash = 0;
		_objectsSettling = false;

		var removable = new List<GameObject>();
		foreach ( var child in GameObject.Children.ToArray() )
			CollectBrushTree( child, removable );
		// In the editor the removal must be visible NOW: the undo scope around a collapse snapshots the "after"
		// state the moment it closes, and a deferred Destroy would leave the objects in that snapshot (and
		// then delete the ones an undo brought back at the end of the frame). Runtime keeps the deferred path.
		bool immediate = Scene.IsEditor;
		foreach ( var go in removable )
		{
			if ( !go.IsValid() )
				continue;
			if ( immediate )
				go.DestroyImmediate();
			else
				go.Destroy();
		}
	}

	// Every object that exists only to hold brushes: brush objects (their spline points go with them) and
	// component-less pivots whose whole subtree is removable. Returns whether `go` itself is removable.
	static bool CollectBrushTree( GameObject go, List<GameObject> removable )
	{
		if ( !go.IsValid() || go.Components.Get<SdfSculpture>( true ) is not null )
			return false;

		if ( go.Components.Get<SdfBrushComponent>( true ) is not null )
		{
			removable.Add( go );
			return true;
		}

		bool all = go.Components.Count == 0 && go.Children.Count > 0;
		foreach ( var child in go.Children.ToArray() )
			all &= CollectBrushTree( child, removable );

		if ( all )
			removable.Add( go );
		return all;
	}

	/// <summary>Create one child brush object per authored brush and switch to object form. The list stays
	/// as the snapshot the next editor tick re-gathers.</summary>
	public void ExplodeToObjects()
	{
		if ( BrushObjects )
			return;

		var brushes = Brushes ?? new();
		int count = AuthoredBrushCount;
		for ( int i = 0; i < count; i++ )
			CreateBrushObject( brushes[i], GameObject );

		BrushObjects = true;
		_objectsHash = 0;
	}

	/// <summary>Gather the objects into the list, destroy them and rebuild — the inverse of <see cref="ExplodeToObjects"/>.</summary>
	public void CollapseToList()
	{
		if ( !BrushObjects )
			return;

		FlattenObjects();
		Rebuild();
	}

	/// <summary>A brush object for <paramref name="b"/> under <paramref name="parent"/>. Brush fields are
	/// sculpture-local, so the object is placed through WORLD space (the parent may be a pivot with its own
	/// transform). A spline becomes a frame at its points' centroid with one <see cref="SdfSplinePoint"/> child
	/// per control point.</summary>
	internal GameObject CreateBrushObject( SdfBrush b, GameObject parent )
	{
		var world = WorldTransform;
		var go = new GameObject( parent, true, BrushObjectName( b ) );
		var comp = go.Components.Create<SdfBrushComponent>();
		comp.CopyFrom( b );

		if ( b.Shape == SdfShape.Spline && b.Points is { Count: > 0 } pts )
		{
			var centroid = Vector3.Zero;
			foreach ( var p in pts )
				centroid += new Vector3( p.x, p.y, p.z );
			centroid /= pts.Count;

			go.WorldTransform = world.ToWorld( new Transform( centroid, Rotation.Identity, 1f ) );
			for ( int i = 0; i < pts.Count; i++ )
			{
				var p = pts[i];
				var pgo = new GameObject( go, true, $"Point {i + 1}" );
				pgo.WorldPosition = world.PointToWorld( new Vector3( p.x, p.y, p.z ) );
				pgo.Components.Create<SdfSplinePoint>().Radius = p.w;
			}
		}
		else
		{
			go.WorldTransform = world.ToWorld( new Transform( b.Position, b.Rotation, b.Size ) );
		}

		go.Enabled = b.Enabled;
		return go;
	}

	/// <summary>Add a brush OBJECT (object form only) of the given shape and operation. Placed a step above
	/// <paramref name="after"/> — same parent, right after it in sibling (= brush) order — or stacked above the
	/// last brush when nothing is given. Returns the new object, or null (not object form / at the cap).</summary>
	public GameObject AddBrushObject( SdfShape shape, SdfOperation operation, SdfBrushComponent after = null )
	{
		if ( !BrushObjects )
			return null;

		if ( BrushComponents().Count() >= SdfBrushPacker.MaxBrushes )
		{
			Log.Warning( $"SdfSculpture: brush cap reached ({SdfBrushPacker.MaxBrushes}) — not adding another." );
			return null;
		}

		var world = WorldTransform;
		var parent = GameObject;
		Vector3 pos;

		bool next = after.IsValid() && after.Owner == this;
		if ( next )
		{
			pos = world.ToLocal( after.WorldTransform ).Position + new Vector3( 0f, 0f, 16f );
			parent = after.GameObject.Parent.IsValid() ? after.GameObject.Parent : GameObject;
		}
		else
		{
			var last = Brushes is { Count: > 0 } ? Brushes[^1] : null; // the gathered snapshot
			pos = last is not null ? new Vector3( 0f, 0f, last.Position.z + 16f ) : Vector3.Zero;
		}

		var brush = new SdfBrush
		{
			Shape = shape,
			Operation = operation,
			Position = pos,
			Rotation = SpawnRotation( shape ),
			Size = SpawnSize( shape ),
			Points = shape == SdfShape.Spline ? DefaultSplinePoints( pos ) : null,
		};

		var go = CreateBrushObject( brush, parent );
		if ( next )
			after.GameObject.AddSibling( go, before: false );

		_objectsHash = 0; // gather on the next editor tick
		return go;
	}

	/// <summary>The size a freshly added brush of this shape gets. Text is locked to the glyph slot's 2:1 aspect
	/// (a uniform slot→world mapping — anything else stretches the glyphs) and much shallower than the solids.</summary>
	public static Vector3 SpawnSize( SdfShape shape ) => shape switch
	{
		SdfShape.Sphere => new Vector3( 16f ),
		SdfShape.Text => new Vector3( 24f, 12f, 4f ),
		_ => new Vector3( 12f ),
	};


	static string BrushObjectName( SdfBrush b )
		=> b.Operation == SdfOperation.Add ? b.Shape.ToString() : $"{b.Operation} {b.Shape}";
}
