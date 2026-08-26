using System;
using System.Collections.Generic;
using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// Times the CPU surface-nets mesher, split by phase — the baseline for moving its field sampling onto the
/// GPU. Lives in the editor assembly because the scene being measured is usually the one open in the editor,
/// and <c>Game.ActiveScene</c> is null there; it falls back to the running game's scene in play mode.
/// </summary>
public static class SdfMeshBench
{
	static Scene ActiveScene => SceneEditorSession.Active?.Scene ?? Game.ActiveScene;

	/// <summary><c>mimi_mesh_bench [resolution] [runs]</c> — resolution 0 uses each sculpture's own.
	/// Reports per-phase milliseconds so it's clear whether the O(res³ × brushes) grid pass actually
	/// dominates, or whether the per-vertex pass (6 gradient samples + 1 surface sample each) does.
	/// Runs synchronously on the calling thread: this WILL hitch. It's a measurement, not a feature.</summary>
	[ConCmd( "mimi_mesh_bench" )]
	public static void Run( int resolution = 0, int runs = 3 )
	{
		var scene = ActiveScene;
		if ( scene is null ) { Log.Warning( "[mesh-bench] no scene open" ); return; }

		// Bench UNIQUE shapes, not instances: the mesher is content-cached, so 767 perf-grid clones of one pot
		// would otherwise report the same measurement 767 times and drown the shapes that actually differ.
		// GetAllComponents skips disabled ones, hence the explicit walk (the round-outline lesson).
		var seen = new HashSet<string>();
		var targets = new List<SdfSculpture>();
		foreach ( var s in scene.GetAllObjects( false ).SelectMany( go => go.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelf ) ) )
		{
			if ( s.Brushes is not { Count: > 0 } ) continue;
			if ( !seen.Add( $"{s.GameObject.Name}|{s.Brushes.Count}" ) ) continue;
			targets.Add( s );
		}

		if ( targets.Count == 0 ) { Log.Warning( "[mesh-bench] no sculptures with brushes" ); return; }

		Log.Info( $"[mesh-bench] {targets.Count} unique shapes, {runs} run(s), res={(resolution > 0 ? resolution.ToString() : "per-sculpture")}" );

		double totSample = 0, totVertex = 0, totStitch = 0, totDispatch = 0, totReadback = 0;
		foreach ( var s in targets )
		{
			int res = resolution > 0 ? resolution : s.Resolution;
			SdfTextSdf.EnsureBaked( s.Brushes ); // text bakes are main-thread only; keep them out of the timings
			double sample = 0, vertex = 0, stitch = 0;

			for ( int r = 0; r < runs; r++ )
			{
				SurfaceNetsMesher.ComputeData( s.Brushes, res, s.FlipFaces );
				sample += SurfaceNetsMesher.LastSampleMs;
				vertex += SurfaceNetsMesher.LastVertexMs;
				stitch += SurfaceNetsMesher.LastStitchMs;
				if ( SurfaceNetsMesher.LastFromGpu )
				{
					totDispatch += SdfMeshGridGpu.LastDispatchMs / runs;
					totReadback += SdfMeshGridGpu.LastReadbackMs / runs;
				}
			}

			sample /= runs; vertex /= runs; stitch /= runs;
			totSample += sample; totVertex += vertex; totStitch += stitch;

			Log.Info( $"[mesh-bench] {s.GameObject.Name} res={res} brushes={SurfaceNetsMesher.LastBrushCount} " +
				$"grid={SurfaceNetsMesher.LastGridPoints} verts={SurfaceNetsMesher.LastVertexCount} " +
				$"{(SurfaceNetsMesher.LastFromGpu ? "gpu" : "CPU")} | " +
				$"sample={sample:0.00}ms vertex={vertex:0.00}ms stitch={stitch:0.00}ms total={sample + vertex + stitch:0.00}ms" );
		}

		double tot = Math.Max( totSample + totVertex + totStitch, 0.0001 );
		Log.Info( $"[mesh-bench] TOTAL sample={totSample:0.0}ms ({totSample / tot:P0}) vertex={totVertex:0.0}ms ({totVertex / tot:P0}) " +
			$"stitch={totStitch:0.0}ms ({totStitch / tot:P0}) => {tot:0.0}ms" );
		if ( totDispatch + totReadback > 0 )
			Log.Info( $"[mesh-bench] of which GPU: dispatch={totDispatch:0.0}ms readback(sync stall)={totReadback:0.0}ms" );
	}

	/// <summary><c>mimi_mesh_rebuild</c> — kick a real rebuild on every sculpture in the scene, through
	/// <c>SdfSculpture.RebuildAsync</c> and its cache, thread hops and all. The bench and verify commands call
	/// the mesher directly, so this is the one that exercises the actual build path — worth running after any
	/// change to how the grids are sampled or staged.</summary>
	[ConCmd( "mimi_mesh_rebuild" )]
	public static void Rebuild()
	{
		var scene = ActiveScene;
		if ( scene is null ) { Log.Warning( "[mesh-rebuild] no scene open" ); return; }

		int n = 0;
		foreach ( var s in scene.GetAllObjects( false ).SelectMany( go => go.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelf ) ) )
		{
			if ( s.Brushes is not { Count: > 0 } ) continue;
			s.Rebuild();
			n++;
		}

		Log.Info( $"[mesh-rebuild] kicked {n} sculpture rebuild(s) — they stream in behind the build gate" );
	}

	/// <summary><c>mimi_mesh_chain [resolution] [runs]</c> — time the full three-LOD chain exactly as
	/// <c>SdfSculpture.BuildModelAsync</c> builds it: one batched sample of res / res÷2 / res÷4, then three
	/// meshes. This is the number that matters for scene-load cost, where the per-LOD bench is not — the GPU
	/// sync is paid once for the chain, so measuring a single LOD three times over-counts it threefold.
	/// Pass runs &gt; 1 to see it warm; the first pass includes texture allocation.</summary>
	[ConCmd( "mimi_mesh_chain" )]
	public static void Chain( int resolution = 0, int runs = 3 )
	{
		var scene = ActiveScene;
		if ( scene is null ) { Log.Warning( "[mesh-chain] no scene open" ); return; }

		var seen = new HashSet<string>();
		var targets = new List<SdfSculpture>();
		foreach ( var s in scene.GetAllObjects( false ).SelectMany( go => go.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelf ) ) )
		{
			if ( s.Brushes is not { Count: > 0 } ) continue;
			if ( !seen.Add( $"{s.GameObject.Name}|{s.Brushes.Count}" ) ) continue;
			targets.Add( s );
		}
		if ( targets.Count == 0 ) { Log.Warning( "[mesh-chain] no sculptures with brushes" ); return; }

		double totSample = 0, totMesh = 0, totReadback = 0;
		double worst = 0; string worstName = "";

		foreach ( var s in targets )
		{
			int res = resolution > 0 ? resolution : s.Resolution;
			SdfTextSdf.EnsureBaked( s.Brushes );
			double sample = 0, mesh = 0, readback = 0;

			for ( int r = 0; r < runs; r++ )
			{
				var grids = SurfaceNetsMesher.SampleGrids( s.Brushes, res, Math.Max( 4, res / 2 ), Math.Max( 4, res / 4 ) );
				sample += SurfaceNetsMesher.LastSampleMs;
				readback += SdfMeshGridGpu.LastReadbackMs;

				long t = System.Diagnostics.Stopwatch.GetTimestamp();
				foreach ( var g in grids )
					SurfaceNetsMesher.ComputeData( g, s.FlipFaces );
				mesh += (System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			}

			sample /= runs; mesh /= runs; readback /= runs;
			totSample += sample; totMesh += mesh; totReadback += readback;
			if ( sample + mesh > worst ) { worst = sample + mesh; worstName = s.GameObject.Name; }
		}

		double tot = totSample + totMesh;
		Log.Info( $"[mesh-chain] {targets.Count} shapes x 3 LODs, {runs} run(s): sample={totSample:0.0}ms " +
			$"(readback stall {totReadback:0.0}ms) mesh={totMesh:0.0}ms => {tot:0.0}ms total, {tot / targets.Count:0.00}ms per shape" );
		Log.Info( $"[mesh-chain] worst single shape: {worstName} at {worst:0.00}ms" );
	}

	/// <summary><c>mimi_mesh_verify [resolution]</c> — mesh every unique shape BOTH ways and report how far the
	/// GPU-sampled result is from the CPU one. This is the check that the two evaluators actually agree, which
	/// is the entire point of moving the sampling: a divergence here is a brush property the compute path is
	/// missing. Vertex counts should match exactly (the sign pattern decides them) and positions should differ
	/// only by float noise; colour is compared separately because it goes through an 8-bit pack.</summary>
	[ConCmd( "mimi_mesh_verify" )]
	public static void Verify( int resolution = 0 )
	{
		var scene = ActiveScene;
		if ( scene is null ) { Log.Warning( "[mesh-verify] no scene open" ); return; }

		var seen = new HashSet<string>();
		int checkedShapes = 0, mismatched = 0;

		foreach ( var s in scene.GetAllObjects( false ).SelectMany( go => go.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelf ) ) )
		{
			if ( s.Brushes is not { Count: > 0 } ) continue;
			if ( !seen.Add( $"{s.GameObject.Name}|{s.Brushes.Count}" ) ) continue;

			int res = resolution > 0 ? resolution : s.Resolution;
			SdfTextSdf.EnsureBaked( s.Brushes );

			SdfMeshGridGpu.Enabled = true;
			var gpu = SurfaceNetsMesher.ComputeData( s.Brushes, res, s.FlipFaces );
			bool wasGpu = SurfaceNetsMesher.LastFromGpu;

			SdfMeshGridGpu.Enabled = false;
			var cpu = SurfaceNetsMesher.ComputeData( s.Brushes, res, s.FlipFaces );
			SdfMeshGridGpu.Enabled = true;

			checkedShapes++;
			if ( !wasGpu )
			{
				Log.Warning( $"[mesh-verify] {s.GameObject.Name}: GPU sampling unavailable, nothing to compare" );
				continue;
			}

			int ng = gpu.Vertices?.Length ?? 0, nc = cpu.Vertices?.Length ?? 0;
			if ( ng != nc )
			{
				// A count delta is a sign disagreement somewhere. A handful out of thousands is the two
				// implementations rounding a boundary sample differently — glyph edges especially, where the
				// GPU reads the text field through a hardware bilinear sampler (fixed-point sub-texel weights)
				// and the CPU fallback interpolates in floats. The GPU is the one that matches the raymarched
				// surface, so that residue is expected. A large delta means a brush property one side is
				// missing, which is the thing this command exists to catch.
				int delta = Math.Abs( ng - nc );
				bool structural = delta > Math.Max( 8, nc / 100 );
				if ( structural ) mismatched++;
				string msg = $"[mesh-verify] {(structural ? "STRUCTURAL" : "minor")} {s.GameObject.Name} res={res}: " +
					$"vertex count gpu={ng} cpu={nc} (delta {delta}, {(nc > 0 ? (float)delta / nc : 0f):P2})";
				if ( structural ) Log.Warning( msg ); else Log.Info( msg );
				continue;
			}
			if ( ng == 0 )
				continue;

			// Vertices come out in the same cell order from both paths, so they pair up index for index.
			// Colour is Color32, so the difference is in 0-255 steps.
			var bsz = gpu.Bounds.Size;
			float cellSize = MathF.Max( bsz.x, MathF.Max( bsz.y, bsz.z ) ) / Math.Clamp( res, 4, 96 );

			float maxPos = 0f, maxNrm = 0f;
			int maxCol = 0, nDiff = 0, firstDiff = -1;
			double sumCol = 0;
			for ( int i = 0; i < ng; i++ )
			{
				float dp = gpu.Vertices[i].Position.Distance( cpu.Vertices[i].Position );
				maxPos = MathF.Max( maxPos, dp );
				maxNrm = MathF.Max( maxNrm, gpu.Vertices[i].Normal.Distance( cpu.Vertices[i].Normal ) );
				if ( dp > cellSize * 0.03f )
				{
					nDiff++;
					if ( firstDiff < 0 ) firstDiff = i;
				}
				var a = gpu.Vertices[i].Color;
				var b = cpu.Vertices[i].Color;
				int dc = Math.Max( Math.Abs( a.r - b.r ), Math.Max( Math.Abs( a.g - b.g ), Math.Abs( a.b - b.b ) ) );
				maxCol = Math.Max( maxCol, dc );
				sumCol += dc;
			}

			// Geometry is the pass/fail, and the tolerance has to be relative to the CELL — that's the mesh's
			// own quantum, and a fixed distance means nothing on a shape twenty times the size of another.
			// A couple of percent of a cell is fp32 noise between two evaluators (the text field especially,
			// where the CPU does its own bilinear and the GPU uses a hardware sampler). Colour is only
			// REPORTED, because the two paths legitimately differ there — the GPU blends brush colours in
			// linear space (as the raymarch does) while the CPU mirror blends them in gamma space, so seam
			// texels shift by design. A large average, not just a large max, is what would be suspicious.
			// Vertices are compared index for index, which only holds while both paths visit the same cells. One
			// flipped sample renumbers everything after it, so "84 differing, first at index 1917" is a
			// RENUMBERING of the tail, not a divergence — that's what nDiff and firstDiff make legible. Only a
			// difference spread across a real share of the mesh means a brush property one side is missing.
			bool spreadOut = nDiff > Math.Max( 8, ng / 100 ) && firstDiff < ng / 2;
			if ( spreadOut ) mismatched++;
			string diff = nDiff > 0 ? $" DIFFERING={nDiff}/{ng} from index {firstDiff}" : "";
			Log.Info( $"[mesh-verify] {(spreadOut ? "STRUCTURAL" : nDiff > 0 ? "minor" : "ok   ")} {s.GameObject.Name} res={res} verts={ng} " +
				$"maxPos={maxPos:0.0000} ({maxPos / cellSize:P1} of a cell) maxNormal={maxNrm:0.0000} " +
				$"colour max={maxCol}/255 avg={sumCol / ng:0.0}{diff}" );
		}

		Log.Info( $"[mesh-verify] {checkedShapes} shape(s), {mismatched} with STRUCTURAL differences (a \"minor\" line is boundary-sample rounding between the two implementations, not a missing feature)" );
	}

	/// <summary><c>mimi_mesh_probe &lt;name&gt; [resolution]</c> — compare the two sampled grids point for point
	/// for one shape and say WHERE they disagree, rather than only that the meshes came out different. Reports
	/// the sign flips (the ones that change the mesh), the worst absolute difference and the local position it
	/// happened at, so a divergence can be traced to a brush instead of guessed at.</summary>
	[ConCmd( "mimi_mesh_probe" )]
	public static void Probe( string name, int resolution = 0, int match = 0 )
	{
		var scene = ActiveScene;
		if ( scene is null ) { Log.Warning( "[mesh-probe] no scene open" ); return; }

		// Index into the matches rather than taking the first: object names here differ only by a "(3)" suffix,
		// and the console splits on spaces, so naming one exactly isn't an option.
		var matches = scene.GetAllObjects( false )
			.SelectMany( go => go.Components.GetAll<SdfSculpture>( FindMode.EverythingInSelf ) )
			.Where( s => s.Brushes is { Count: > 0 } && s.GameObject.Name.Contains( name, StringComparison.OrdinalIgnoreCase ) )
			.ToList();
		if ( matches.Count == 0 ) { Log.Warning( $"[mesh-probe] no sculpture matching '{name}'" ); return; }
		if ( match < 0 || match >= matches.Count )
		{
			Log.Warning( $"[mesh-probe] '{name}' has {matches.Count} match(es): {string.Join( ", ", matches.Select( ( s, i ) => $"{i}={s.GameObject.Name}" ) )}" );
			return;
		}
		var target = matches[match];

		int res = resolution > 0 ? resolution : target.Resolution;
		SdfTextSdf.EnsureBaked( target.Brushes );

		SdfMeshGridGpu.Enabled = true;
		var gpu = SurfaceNetsMesher.SampleGrid( target.Brushes, res );
		SdfMeshGridGpu.Enabled = false;
		var cpu = SurfaceNetsMesher.SampleGrid( target.Brushes, res );
		SdfMeshGridGpu.Enabled = true;

		if ( gpu.IsEmpty || cpu.IsEmpty || gpu.Data.Length != cpu.Data.Length )
		{
			Log.Warning( "[mesh-probe] grids aren't comparable" );
			return;
		}

		int n = gpu.Gx * gpu.Gy * gpu.Gz;
		int signFlips = 0, worst = -1;
		float worstDelta = 0f;

		for ( int i = 0; i < n; i++ )
		{
			float a = gpu.Data[i * 4], b = cpu.Data[i * 4];
			if ( (a < 0f) != (b < 0f) )
				signFlips++;

			// Compare only where the value could matter: a difference out at the 1e9 empty-space sentinel is
			// noise, a difference near the surface is the mesh.
			if ( MathF.Abs( a ) > gpu.Cell * 8f && MathF.Abs( b ) > gpu.Cell * 8f )
				continue;
			float delta = MathF.Abs( a - b );
			if ( delta > worstDelta ) { worstDelta = delta; worst = i; }
		}

		string where = "n/a";
		if ( worst >= 0 )
		{
			int i0 = worst % gpu.Gx, j0 = (worst / gpu.Gx) % gpu.Gy, k0 = worst / (gpu.Gx * gpu.Gy);
			var p = gpu.Mins + new Vector3( i0 * gpu.Cell, j0 * gpu.Cell, k0 * gpu.Cell );
			where = $"{p} (gpu={gpu.Data[worst * 4]:0.0000} cpu={cpu.Data[worst * 4]:0.0000})";
		}

		Log.Info( $"[mesh-probe] {target.GameObject.Name} res={res} brushes={target.Brushes.Count} grid={gpu.Gx}x{gpu.Gy}x{gpu.Gz} cell={gpu.Cell:0.000}" );
		Log.Info( $"[mesh-probe] sign flips={signFlips}/{n}  worst near-surface delta={worstDelta:0.0000} at {where}" );

		// Which brush owns that point tells us which primitive to look at.
		if ( worst >= 0 )
		{
			int i0 = worst % gpu.Gx, j0 = (worst / gpu.Gx) % gpu.Gy, k0 = worst / (gpu.Gx * gpu.Gy);
			var p = gpu.Mins + new Vector3( i0 * gpu.Cell, j0 * gpu.Cell, k0 * gpu.Cell );
			for ( int b = 0; b < target.Brushes.Count; b++ )
			{
				var br = target.Brushes[b];
				if ( !br.Enabled ) continue;
				float bd = br.Distance( p );
				if ( MathF.Abs( bd ) < gpu.Cell * 3f )
					Log.Info( $"[mesh-probe]   near brush {b}: {br.Shape}/{br.Operation} blend={br.Blend:0.###} rounding={br.Rounding:0.###} slice={br.Slice:0.###} dist={bd:0.000}" );
			}
		}
	}
}
