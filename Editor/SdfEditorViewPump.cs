using Sandbox;

namespace Editor;

/// <summary>
/// Pushes the scene view's camera position into <see cref="Mimiclay.SdfRaymarchRenderer.EditorViewPos"/>
/// every editor frame, so the renderer's camera-distance bands (shadow LOD + overdraw near-gate) track
/// the EDITOR camera unconditionally. Without this they only update from DrawGizmos, which ticks solely
/// while a scene view is drawing WITH gizmos enabled — a hidden tab, the gizmo toggle off, or an
/// offscreen/tool render all skip it, freezing the bands at whatever they last were (safe — the flags
/// default to full quality — but permanently unoptimized, and untestable headlessly). The State path is
/// the same one the engine's own tooling reads (and its MCP camera tool writes), so it's always current.
/// </summary>
public static class SdfEditorViewPump
{
	[EditorEvent.Frame]
	public static void Tick()
	{
		var viewport = SceneViewWidget.Current?.LastSelectedViewportWidget;
		Mimiclay.SdfRaymarchRenderer.EditorViewPos = viewport?.State.CameraPosition;
	}
}
