using System.Linq;
using Mimiclay;

namespace Editor;

/// <summary>
/// Editor-side resolver for the scene's <see cref="GizmoSettings"/>. The gizmo and sculpt tool
/// read all sizing / thickness from the returned component, so dropping a <see cref="GizmoSettings"/>
/// on any GameObject lets you tune the whole gizmo live from the inspector. When the scene has
/// none, a cached default instance is used so the gizmo keeps its out-of-the-box look.
/// </summary>
public static class GizmoStyle
{
	static GizmoSettings _fallback;

	public static GizmoSettings Resolve( Scene scene )
	{
		var found = scene?.GetAllComponents<GizmoSettings>().FirstOrDefault();
		if ( found is not null )
			return found;

		return _fallback ??= new GizmoSettings();
	}
}
