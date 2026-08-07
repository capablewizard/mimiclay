namespace Mimiclay;

/// <summary>
/// The tunable half of an <see cref="SdfStage"/>: settings that aren't components, sitting on the root of the
/// thumbnail rig prefab. The rest of the rig IS components — put <c>DirectionalLight</c> / <c>PointLight</c> /
/// <c>SpotLight</c> / <c>AmbientLight</c> and one <c>CameraComponent</c> alongside this and the stage mirrors
/// them into its private world.
///
/// Editing loop: open <c>Assets/Prefabs/thumbnail_stage.prefab</c>, move the lights, save, then hit Refresh in
/// the editor's Saved Sculpts window to see the result. That window renders through the real stage, so it's the
/// ground truth — the prefab view only shows you the gizmos.
///
/// Positions are authored around a subject of <see cref="ReferenceRadius"/> sitting at the origin. The stage
/// scales light positions and radii by the actual subject's size, so a tennis ball and a wardrobe get the same
/// lighting rather than the same absolute light placement.
/// </summary>
[Title( "SDF Stage Rig" )]
[Category( "SDF" )]
[Icon( "wb_iridescent" )]
public sealed class SdfStageRig : Component, Component.ExecuteInEditor
{
	/// <summary>Where the stage looks for its rig when a caller doesn't name one.</summary>
	public const string DefaultPrefabPath = "prefabs/thumbnail_stage.prefab";

	/// <summary>Flat fill applied to the whole world, so faces pointing away from every light still read as
	/// clay rather than black. This is the single biggest dial for how "studio" vs "moody" thumbnails look.</summary>
	[Property] public Color AmbientColor { get; set; } = Color.White * 0.35f;

	/// <summary>Fallback cubemap for image-based lighting, used ONLY when the rig has no <c>SkyBox2D</c>. A bare
	/// SceneWorld has no sky, so without one or the other a PBR material has nothing to reflect and comes out far
	/// darker than in game. Prefer adding a SkyBox2D — then the prefab view and the thumbnail share a sky.</summary>
	[Property] public string EnvmapTexture { get; set; } = "textures/cubemaps/default2.vtex";

	/// <summary>Material the staged prop is raymarched with. Must match what the real prop uses in game or the
	/// thumbnail won't look like it.</summary>
	[Property, Group( "Subject" )] public Material SurfaceMaterial { get; set; } = Material.Load( "materials/plasticine.vmat" );

	/// <summary>Lumpy plasticine displacement. Bends the SILHOUETTE, not just the shading — leaving this off
	/// when the real prop has it on is one of the bigger reasons a thumbnail stops matching.</summary>
	[Property, Group( "Subject" )] public bool Displace { get; set; } = true;

	/// <summary>Subsurface back-scatter, so thin parts glow. Matches the disguise prefab's default.</summary>
	[Property, Group( "Subject" )] public bool Transmission { get; set; } = true;

	/// <summary>Let the raymarched surface cast its own shadow. Only does anything if a light in this rig has
	/// its own Shadows enabled.</summary>
	[Property, Group( "Subject" )] public bool SdfShadows { get; set; } = true;

	/// <summary>
	/// CASTER-side shadow bias, in world units, for the raymarched surface — separate from the receiver-side
	/// bias on each light. The SDF writes its own shadow-map depth by marching, so that depth carries the
	/// march's own error (roughly <see cref="Epsilon"/>) and the surface can shadow itself: acne.
	/// <para>
	/// Default 0, and leave it there unless you actually see acne. This offsets the shadow along the light
	/// ray, so any non-zero value detaches it from the object by that much — at thumbnail scale even a
	/// quarter of a unit reads as a visibly floating shadow. Fix acne with <see cref="Epsilon"/> and
	/// <see cref="ShadowDistance"/> first; reach for this last.
	/// </para>
	/// </summary>
	[Property, Group( "Subject" ), Range( 0f, 4f )] public float SdfShadowBias { get; set; }

	/// <summary>
	/// How far from the camera the shadow cascades have to reach, in world units. <b>0 = fit automatically to
	/// the subject</b>, which is almost always what you want here.
	/// <para>
	/// This is the big one for a stage this small. Cascade distance defaults to something sized for a whole
	/// map, so a 60-unit prop lands on a handful of shadow-map texels — blocky, and offset enough to look
	/// mis-biased. Packing the cascades around the subject instead raises the texel density by orders of
	/// magnitude. With it fitted, a Cascade Count of 1 on the light usually beats 4: there's nothing else in
	/// the scene to need the extra range, so one map at full resolution wins.
	/// </para>
	/// </summary>
	[Property, Group( "Subject" )] public float ShadowDistance { get; set; }

	/// <summary>Ray-march step ceiling. A thumbnail renders once, so it can afford far more than a prop
	/// being drawn every frame.</summary>
	[Property, Group( "Subject" ), Range( 16, 256 )] public int MaxSteps { get; set; } = 160;

	/// <summary>Surface hit threshold, in world units. This is the march's positional error, so it sets the
	/// floor on how much <see cref="SdfShadowBias"/> you need — tightening it is the real fix for shadow
	/// acne, and bias is the cheap one.</summary>
	[Property, Group( "Subject" ), Range( 0.005f, 1f )] public float Epsilon { get; set; } = 0.02f;

	/// <summary>Padding around the subject's bounding sphere. 1 = it touches the frame edge.</summary>
	[Property, Range( 1f, 2f )] public float Margin { get; set; } = 1.2f;

	/// <summary>The subject size this rig's light positions were authored against. See the class summary.</summary>
	[Property] public float ReferenceRadius { get; set; } = 32f;
}
