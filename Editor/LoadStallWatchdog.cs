using System.Linq;
using Sandbox;

namespace Editor;

/// <summary>
/// Diagnostic for the map↔lobby transition stalls: while the playing scene is stuck in its loading state, log
/// WHAT it's waiting on every few seconds. Runs on the editor frame tick, so it keeps reporting even when the
/// game scene isn't updating (the exact state we need to observe — a client stuck on a black loading screen
/// means the HOST never finished loading and never sent the snapshot; see OnLoadSceneRequestSnapshotMsg's
/// wait-for-IsLoading loop in the engine). If the editor hard-freezes, nothing managed can log — but a soft
/// stall (a loading task that never completes) shows up here by name. Cheap and editor-only; delete once the
/// transition stall is solved.
/// </summary>
public static class LoadStallWatchdog
{
	static RealTimeSince _sinceLog;
	static RealTimeSince _stalledFor;
	static bool _wasLoading;

	[EditorEvent.Frame]
	public static void Tick()
	{
		if ( !Game.IsPlaying || Game.ActiveScene is null )
		{
			_wasLoading = false;
			return;
		}

		bool loading = Game.ActiveScene.IsLoading || LoadingScreen.IsVisible;
		if ( !loading )
		{
			if ( _wasLoading )
				Log.Info( $"[LoadWatch] scene finished loading after {_stalledFor.Relative:F1}s" );
			_wasLoading = false;
			return;
		}

		if ( !_wasLoading )
		{
			_wasLoading = true;
			_stalledFor = 0;
			_sinceLog = 0;
			return;
		}

		if ( _sinceLog < 5f )
			return;
		_sinceLog = 0;

		var tasks = string.Join( ", ",
			LoadingScreen.Tasks.Where( t => t is not null && !t.IsCompleted )
				.Select( t => string.IsNullOrEmpty( t.Title ) ? "(untitled)" : t.Title ) );

		Log.Warning( $"[LoadWatch] still loading after {_stalledFor.Relative:F0}s — " +
			$"Scene.IsLoading={Game.ActiveScene.IsLoading} LoadingScreen={LoadingScreen.IsVisible} " +
			$"IsConnecting={Networking.IsConnecting} connections={Connection.All.Count} pendingTasks=[{tasks}]" );
	}
}
