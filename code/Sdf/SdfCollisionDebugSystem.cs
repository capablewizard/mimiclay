using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// The PLAY half of the collision debug view (<see cref="SdfCollisionDebug"/>): the game camera never draws
/// component gizmos, so while the view is on this keeps ONE line-list <see cref="SceneDynamicObject"/> per
/// <see cref="SdfCollider"/> — its live collider's hull edges, sphere circles and foot probes, sculpture-local,
/// riding the collider's world transform — and writes the stats / no-collision labels as one-frame overlay text.
/// The vertex buffer is only rewritten when the collider swaps its model or a line-shaping option changes, so a
/// scene full of voxel boxes costs a transform write per collider per frame. Everything is torn down the moment
/// the view goes off. Never runs in editor or thumbnail scenes (the gizmo path owns the editor scene).
/// </summary>
public sealed class SdfCollisionDebugSystem : GameObjectSystem
{
	sealed class Entry
	{
		public SceneDynamicObject Lines;
		public Vertex[] Built; // the vertex array currently in Lines (reference identity = up to date)
		public bool Seen;
	}

	readonly Dictionary<SdfCollider, Entry> _entries = new();
	Material _material;

	public SdfCollisionDebugSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, Tick, "SdfCollisionDebug" );
	}

	void Tick()
	{
		if ( Scene is null || Scene.IsEditor || SdfThumbnail.IsThumbnailScene( Scene ) )
			return;

		if ( !SdfCollisionDebug.Enabled )
		{
			Clear();
			return;
		}

		foreach ( var entry in _entries.Values )
			entry.Seen = false;

		var cam = Scene.Camera;
		foreach ( var collider in Scene.GetAllComponents<SdfCollider>() )
		{
			if ( !collider.Active || !SdfCollisionDebug.ShouldDraw( collider ) )
				continue;

			var snap = SdfCollisionDebug.RuntimeSnapshot( collider );
			if ( snap is null )
				continue;

			if ( !_entries.TryGetValue( collider, out var entry ) )
				_entries[collider] = entry = new Entry();
			entry.Seen = true;

			var verts = snap.LineVertices();
			if ( !ReferenceEquals( verts, entry.Built ) || !entry.Lines.IsValid() )
				Upload( entry, verts );

			entry.Lines.Transform = collider.WorldTransform;
			entry.Lines.RenderLayer = SdfCollisionDebug.ThroughClay
				? SceneRenderLayer.OverlayWithoutDepth
				: SceneRenderLayer.OverlayWithDepth;

			if ( cam.IsValid() )
				Labels( collider, snap, cam );
		}

		// Colliders that went away, were disabled or got filtered out this frame.
		List<SdfCollider> stale = null;
		foreach ( var (collider, entry) in _entries )
		{
			if ( entry.Seen && collider.IsValid() )
				continue;
			entry.Lines?.Delete();
			(stale ??= new()).Add( collider );
		}
		if ( stale is not null )
			foreach ( var c in stale )
				_entries.Remove( c );
	}

	void Upload( Entry entry, Vertex[] verts )
	{
		if ( !entry.Lines.IsValid() )
		{
			_material ??= Material.Load( "materials/gizmo/line.vmat" );
			entry.Lines = new SceneDynamicObject( Scene.SceneWorld ) { Material = _material };
			entry.Lines.Flags.CastShadows = false;
		}

		entry.Lines.Clear();
		entry.Lines.Init( Graphics.PrimitiveType.Lines );
		if ( verts.Length > 0 )
			entry.Lines.AddVertex( new System.Span<Vertex>( verts ) );
		entry.Built = verts;
	}

	void Labels( SdfCollider collider, SdfCollisionDebug.Snapshot snap, CameraComponent cam )
	{
		var world = collider.WorldTransform;

		if ( SdfCollisionDebug.Stats && OnScreen( cam, world.Position, out var px ) )
			Scene.DebugOverlay.ScreenText( px + new Vector2( 12f, -24f ), SdfCollisionDebug.StatsText( collider, snap ),
				12f, TextFlag.LeftTop, Color.White.WithAlpha( 0.9f ) );

		if ( !SdfCollisionDebug.FlagMissing )
			return;

		foreach ( var (pos, text) in snap.Missing )
		{
			if ( OnScreen( cam, world.PointToWorld( pos ), out var mpx ) )
				Scene.DebugOverlay.ScreenText( mpx + new Vector2( 8f, -6f ), text, 11f, TextFlag.LeftTop, SdfCollisionDebug.MissingColor );
		}
	}

	static bool OnScreen( CameraComponent cam, Vector3 world, out Vector2 px )
	{
		px = default;
		if ( Vector3.Dot( world - cam.WorldPosition, cam.WorldRotation.Forward ) <= 0f )
			return false; // behind the camera — the projection would mirror it onto the screen

		px = cam.PointToScreenPixels( world );
		return true;
	}

	void Clear()
	{
		foreach ( var entry in _entries.Values )
			entry.Lines?.Delete();
		_entries.Clear();
	}

	public override void Dispose()
	{
		Clear();
		base.Dispose();
	}
}
