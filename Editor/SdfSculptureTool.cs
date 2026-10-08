using Mimiclay;

namespace Editor;

/// <summary>
/// Scene-viewport tool for a selected <see cref="SdfSculpture"/>. The editor's ONE authoring form is brush
/// objects (<see cref="SdfSculpture.BrushObjects"/>): the brush objects draw their own outlines and hitboxes
/// (<see cref="SdfBrushComponent"/>), the native transform gizmo moves them, the sculpture's own editor tick
/// gathers and rebuilds, and the always-present <see cref="SculptPanel"/> holds the conversions and display
/// toggles. So this tool has no handles or UI of its own — it only wires the component's editor-only buttons
/// and keeps the raymarch view fresh while a sculpture is selected.
/// </summary>
public class SdfSculptureTool : EditorTool<SdfSculpture>
{
	public override void OnUpdate()
	{
		AllowGameObjectSelection = true;

		// Wire the component's editor-only buttons to our editor-side utilities (the component can't reference
		// the editor assembly itself). Idempotent; cheap.
		SdfSculpture.BakeHandler = SdfBakeUtility.Bake;
		SdfSculpture.ExportPrefabHandler = SdfPrefabUtility.Export;

		var sculpt = GetSelectedComponent<SdfSculpture>();
		if ( !sculpt.IsValid() )
			return;

		sculpt.GameObject.Components.Get<SdfRaymarchRenderer>()?.Refresh();
	}
}
