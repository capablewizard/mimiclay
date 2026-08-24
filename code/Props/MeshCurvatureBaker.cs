using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Bakes smooth per-vertex curvature into a <see cref="MeshComponent"/>'s paint channel so
/// sdf_mesh.shader can shade crevices/ridges without the blocky screen-space estimate (enable
/// "Curve From Vertex Paint" on the material). The value lives on the topological VERTEX and is
/// written to every face-corner around it, so it interpolates smoothly and — unlike anything
/// derived from normals — stays continuous across hard/split edges.
///
/// Curvature = how far the one-ring neighbours sit below the vertex tangent plane (discrete mean
/// curvature, convex positive), scaled by <see cref="FeatureScale"/> into a signed [-1,1], then
/// diffused <see cref="SmoothIterations"/> times over the vertex graph to widen the falloff —
/// that diffusion is what buys the soft raymarcher-like look bevel geometry can't express alone.
///
/// Encoding: paint alpha 128 = flat, 255 = full ridge, 1 = full crevice. 0 is reserved as
/// "no data" (the shader reads it as flat), so bakes clamp to [1,255]. RGB is left untouched.
/// </summary>
[Title( "Mesh Curvature Baker" )]
[Category( "SDF" )]
[Icon( "rounded_corner" )]
public sealed class MeshCurvatureBaker : Component, Component.ExecuteInEditor
{
	/// <summary>The mesh to bake. Defaults to one on this GameObject.</summary>
	[Property] public MeshComponent Target { get; set; }

	/// <summary>Feature size in world units: curvature of radius ~this maps to full strength.
	/// Bigger = softer response that only strong edges reach; matches the raymarcher's CurveRadius
	/// meaning (which then acts as a plain gain on top in the material).</summary>
	[Property, Range( 0.25f, 16f )] public float FeatureScale { get; set; } = 1.5f;

	/// <summary>Diffusion passes over the vertex graph. Each pass bleeds the value one ring further,
	/// so this is the "width" of the worn-edge falloff in mesh-edge hops. 0 = raw one-ring estimate.</summary>
	[Property, Range( 0, 16 )] public int SmoothIterations { get; set; } = 4;

	/// <summary>Extra gain applied before encoding, after smoothing (diffusion flattens peaks —
	/// this buys them back).</summary>
	[Property, Range( 0.25f, 8f )] public float Strength { get; set; } = 2f;

	[Button( "Bake Curvature" )]
	public void Bake()
	{
		var target = Target ?? GetComponent<MeshComponent>();
		var mesh = target?.Mesh;
		if ( mesh is null )
		{
			Log.Warning( $"{GameObject.Name}: no MeshComponent mesh to bake curvature into" );
			return;
		}

		var verts = mesh.VertexHandles.ToList();
		var index = new Dictionary<global::HalfEdgeMesh.VertexHandle, int>( verts.Count );
		for ( int i = 0; i < verts.Count; i++ )
			index[verts[i]] = i;

		// --- One-ring neighbours + vertex normals (face-average) from the face fans. ---
		var neighbours = new List<int>[verts.Count];
		var curv = new float[verts.Count];

		for ( int i = 0; i < verts.Count; i++ )
		{
			var p = mesh.GetVertexPosition( verts[i] );
			var normal = Vector3.Zero;
			var ring = new HashSet<int>();

			if ( mesh.GetFacesConnectedToVertex( verts[i], out var faces ) )
			{
				foreach ( var hFace in faces )
				{
					mesh.ComputeFaceNormal( hFace, out var fn );
					normal += fn;
					foreach ( var hv in mesh.GetFaceVertices( hFace ) )
						if ( index.TryGetValue( hv, out var ni ) && ni != i )
							ring.Add( ni );
				}
			}

			neighbours[i] = ring.ToList();
			if ( ring.Count == 0 || normal.LengthSquared < 1e-12f )
				continue;
			normal = normal.Normal;

			// Discrete mean curvature: neighbours below the tangent plane = convex (positive).
			// dot(d, n)/|d|² sums to a 1/units quantity; ×FeatureScale makes it the unitless
			// "how curved at this feature size" the shader's response curve expects.
			float c = 0f;
			foreach ( var ni in ring )
			{
				var d = mesh.GetVertexPosition( verts[ni] ) - p;
				var lenSq = d.LengthSquared;
				if ( lenSq > 1e-8f )
					c -= Vector3.Dot( d, normal ) / lenSq;
			}
			curv[i] = c / ring.Count * FeatureScale;
		}

		// --- Diffuse over the vertex graph: each pass half-lerps toward the ring average. This is
		// what turns the hard per-strip signal into a wide soft falloff across the flats. ---
		for ( int pass = 0; pass < SmoothIterations; pass++ )
		{
			var next = new float[verts.Count];
			for ( int i = 0; i < verts.Count; i++ )
			{
				if ( neighbours[i].Count == 0 ) { next[i] = curv[i]; continue; }
				float avg = 0f;
				foreach ( var ni in neighbours[i] )
					avg += curv[ni];
				next[i] = MathX.Lerp( curv[i], avg / neighbours[i].Count, 0.5f );
			}
			curv = next;
		}

		// --- Encode into the paint channel's alpha on every corner around each vertex (RGB kept). ---
		for ( int i = 0; i < verts.Count; i++ )
		{
			var signed = Math.Clamp( curv[i] * Strength, -1f, 1f );
			var alpha = (byte)Math.Clamp( (int)MathF.Round( 128f + signed * 127f ), 1, 255 );

			if ( !mesh.GetFaceVerticesConnectedToVertex( verts[i], out var corners ) )
				continue;
			foreach ( var hCorner in corners )
			{
				var c = mesh.GetVertexColor( hCorner );
				mesh.SetVertexColor( hCorner, new Color32( c.r, c.g, c.b, alpha ) );
			}
		}

		target.RebuildMesh();
		Log.Info( $"{GameObject.Name}: baked curvature for {verts.Count} vertices ({SmoothIterations} smoothing passes)" );
	}

	[Button( "Clear Bake" )]
	public void Clear()
	{
		var target = Target ?? GetComponent<MeshComponent>();
		var mesh = target?.Mesh;
		if ( mesh is null )
			return;

		foreach ( var hEdge in mesh.HalfEdgeHandles )
		{
			var c = mesh.GetVertexColor( hEdge );
			mesh.SetVertexColor( hEdge, new Color32( c.r, c.g, c.b, 0 ) ); // 0 = no data
		}
		target.RebuildMesh();
	}
}
