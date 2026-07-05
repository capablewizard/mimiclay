using System.Linq;

namespace Mimiclay;

/// <summary>
/// Overrides the direction the shadow is cast from on the <c>shadow_catcher</c> surface (the menu backdrop
/// ground). The caught shadow is the engine's DIRECTIONAL shadow map, so its direction is whatever the scene's
/// directional light faces — there's no way to fake a cast-shadow direction in the shader without a light
/// pointing that way. This component takes over a directional light's rotation so you can art-direct the
/// shadow ("imaginary sun from over there") independently of where it would naturally sit.
///
/// Because the shadow catcher (with D_POINT_LIGHTS off) responds ONLY to the directional light, you can treat
/// that light as a pure shadow-direction dial and light the actual prop with point/spot lights — re-aiming
/// this for a nicer shadow then won't change how the prop itself is lit. The catcher's darkness is driven by
/// shadow VISIBILITY (1 - vis), independent of the light's brightness/colour, so the sun can be as dim as you
/// like and the shadow still reads at full strength (tune it on the material instead: ShadowStrength etc.).
///
/// Runs in the editor too, so the shadow updates live as you drag <see cref="SunAngles"/>. While enabled it
/// continuously forces the light's rotation, so set the angle here rather than rotating the light gizmo.
/// </summary>
[Title( "Shadow Sun (Direction Override)" )]
[Category( "Mimiclay/Rendering" )]
[Icon( "wb_twilight" )]
public sealed class ShadowSun : Component, Component.ExecuteInEditor
{
	/// <summary>The directional light to drive. Left empty, it grabs the first directional light in the scene.</summary>
	[Property] public DirectionalLight TargetLight { get; set; }

	/// <summary>
	/// Direction the imaginary sun shines FROM, as pitch/yaw. Higher pitch = sun higher in the sky = shorter
	/// shadow; lower pitch = raking light = long stretched shadow. Yaw spins it around the prop.
	/// </summary>
	[Property] public Angles SunAngles { get; set; } = new Angles( 45f, 35f, 0f );

	/// <summary>Force the driven light to cast shadows (otherwise the catcher has nothing to catch).</summary>
	[Property] public bool EnsureShadowsEnabled { get; set; } = true;

	protected override void OnEnabled() => Apply();
	protected override void OnUpdate() => Apply();

	void Apply()
	{
		var light = TargetLight.IsValid()
			? TargetLight
			: Scene?.GetAllComponents<DirectionalLight>().FirstOrDefault();

		if ( !light.IsValid() )
			return;

		// A directional light shines along its forward vector, so its rotation IS the sun direction.
		light.WorldRotation = SunAngles.ToRotation();

		if ( EnsureShadowsEnabled && !light.Shadows )
			light.Shadows = true;
	}
}
