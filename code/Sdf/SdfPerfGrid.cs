using System;
using System.Collections.Generic;

namespace Mimiclay;

public enum PerfArrangement
{
	/// <summary>CountX × CountY × CountZ box.</summary>
	Grid,
	/// <summary>Count clones stacked along +X behind the original — same screen footprint, so the
	/// front ones occlude the rest. Config A of the occlusion A/B test.</summary>
	OcclusionStack,
	/// <summary>Count clones spread across the YZ plane (all the same depth) — nothing occludes
	/// anything. Config B of the occlusion A/B test.</summary>
	SpreadWall,
}

/// <summary>
/// Performance-test helper. Clones the GameObject this is attached to so you can stress many SDF
/// renderers at once. <see cref="Arrangement"/> picks the layout; OcclusionStack vs SpreadWall are
/// the A/B pair for checking whether early-Z occlusion is firing: aim the camera down +X at the
/// origin, Generate each with the same Count, and compare FPS (enable ShowFps). If the stack is
/// much cheaper than the wall, hidden objects are being culled before they march.
/// </summary>
[Title( "SDF Perf Grid" )]
[Category( "SDF/Debug" )]
[Icon( "grid_on" )]
public sealed class SdfPerfGrid : Component, Component.ExecuteInEditor
{
	[Property] public PerfArrangement Arrangement { get; set; } = PerfArrangement.Grid;

	// Grid mode:
	[Property, Range( 1, 32 )] public int CountX { get; set; } = 4;
	[Property, Range( 1, 32 )] public int CountY { get; set; } = 4;
	[Property, Range( 1, 32 )] public int CountZ { get; set; } = 1;

	/// <summary>Clone count for OcclusionStack / SpreadWall.</summary>
	[Property, Range( 1, 500 )] public int Count { get; set; } = 100;

	/// <summary>Gap between cells (world units). Stack uses .x (depth); wall uses .y/.z.</summary>
	[Property] public Vector3 Spacing { get; set; } = 64f;

	/// <summary>Build the grid automatically when the game starts.</summary>
	[Property] public bool SpawnOnStart { get; set; }

	/// <summary>Log smoothed FPS / frame time twice a second, for A/B comparison.</summary>
	[Property] public bool ShowFps { get; set; }

	// Tag stamped on every clone so Clear can sweep up orphans from a previous session.
	const string CloneTag = "sdfperfclone";

	readonly List<GameObject> _clones = new();

	float _fpsTimer;
	int _fpsFrames;

	public int Total => Arrangement == PerfArrangement.Grid ? CountX * CountY * CountZ : Count;

	protected override void OnStart()
	{
		if ( SpawnOnStart )
			Generate();
	}

	protected override void OnUpdate()
	{
		if ( !ShowFps )
			return;

		_fpsTimer += Time.Delta;
		_fpsFrames++;
		if ( _fpsTimer >= 0.5f )
		{
			float fps = _fpsFrames / _fpsTimer;
			float ms = 1000f * _fpsTimer / _fpsFrames;
			Log.Info( $"[SdfPerfGrid] {fps:0.0} fps ({ms:0.00} ms) — {Arrangement}, {_clones.Count} clones" );
			_fpsTimer = 0f;
			_fpsFrames = 0;
		}
	}

	[Button( "Generate" )]
	public void Generate()
	{
		Clear();
		var origin = WorldPosition;

		switch ( Arrangement )
		{
			case PerfArrangement.OcclusionStack:
				// Stacked along +X behind the original; same screen footprint -> front occludes back.
				for ( int i = 1; i < Count; i++ )
					SpawnClone( origin + new Vector3( i * Spacing.x, 0, 0 ), $"stack_{i}" );
				break;

			case PerfArrangement.SpreadWall:
				// Flat grid on the YZ plane (all the same depth) -> everything visible.
				int n = (int)MathF.Ceiling( MathF.Sqrt( Count ) );
				for ( int i = 1; i < Count; i++ )
				{
					int iy = i % n, iz = i / n;
					SpawnClone( origin + new Vector3( 0, (iy - n / 2) * Spacing.y, (iz - n / 2) * Spacing.z ), $"wall_{i}" );
				}
				break;

			default: // Grid
				for ( int ix = 0; ix < CountX; ix++ )
				for ( int iy = 0; iy < CountY; iy++ )
				for ( int iz = 0; iz < CountZ; iz++ )
				{
					if ( ix == 0 && iy == 0 && iz == 0 )
						continue; // original already lives here
					SpawnClone( origin + new Vector3( ix * Spacing.x, iy * Spacing.y, iz * Spacing.z ), $"{ix}_{iy}_{iz}" );
				}
				break;
		}

		Log.Info( $"[SdfPerfGrid] generated {_clones.Count} clones ({Arrangement})" );
	}

	void SpawnClone( Vector3 worldPos, string suffix )
	{
		var clone = GameObject.Clone();
		clone.Name = $"{GameObject.Name}_clone_{suffix}";
		clone.Tags.Add( CloneTag );

		if ( GameObject.Parent.IsValid() )
			clone.Parent = GameObject.Parent;

		clone.WorldPosition = worldPos;

		// Each clone's SdfSculpture remeshes on enable, but the shared Model cache (keyed by brush content)
		// dedupes identical clones to ONE async build — so generating N copies meshes once, off-thread.
		clone.Components.Get<SdfPerfGrid>( true )?.Destroy();

		_clones.Add( clone );
	}

	[Button( "Clear" )]
	public void Clear()
	{
		foreach ( var c in _clones )
			DestroyClone( c );
		_clones.Clear();

		// Sweep any clones orphaned by a reload (the tracked list doesn't survive one).
		foreach ( var o in Scene.GetAllObjects( false ) )
		{
			if ( o != GameObject && o.Tags.Has( CloneTag ) )
				DestroyClone( o );
		}
	}

	// Destroy a clone, first releasing the raymarch renderer's raw SceneObject explicitly. That
	// scene object lives in SceneWorld, not the GameObject graph, so GameObject.Destroy() alone
	// won't free it — and its OnDisabled/OnDestroy cleanup isn't guaranteed to fire during bulk
	// teardown, which leaves the geometry rendering with no GameObject behind it.
	static void DestroyClone( GameObject clone )
	{
		if ( !clone.IsValid() )
			return;

		clone.Components.Get<SdfRaymarchRenderer>( true )?.ReleaseSceneObject();
		clone.Destroy();
	}
}
