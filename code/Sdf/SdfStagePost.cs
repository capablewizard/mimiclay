namespace Mimiclay;

/// <summary>
/// Reproduces the rig prefab's post-process stack for an <see cref="SdfStage"/>.
///
/// Why this exists: every s&amp;box post-process effect is a Component that requires a
/// <c>CameraComponent</c>, and the stage renders through a raw <c>SceneCamera</c> (it has to — a
/// CameraComponent hard-asserts that its scene is the ACTIVE scene while the game is playing, so an
/// offscreen one is impossible at runtime). So the effects on the rig's camera never run. This walks the
/// same chain by hand instead: grab the frame and blit the engine's OWN post-process shaders, in the same
/// order the effect components would have used.
///
/// Using the engine shaders unmodified rather than one combined pass is deliberate — they disagree about
/// sRGB (tonemapping reads linear, sharpen and colour read sRGB), and chaining separate blits reproduces
/// that for free. All three preserve alpha, which the transparent thumbnail background depends on.
///
/// NOT reproducible: ambient occlusion (publishing the AO texture needs an internal pipeline slot) and
/// auto-exposure (needs an internal tonemap system pointer). Exposure is therefore fixed at 1.0, which
/// matches a Tonemapping component with AutoExposureEnabled off.
/// </summary>
/// <summary>
/// The scene object that runs an <see cref="SdfStagePost"/> chain.
///
/// A subclass rather than a <c>RenderOverride</c> lambda on purpose: hot reload has to remap delegates into the
/// freshly compiled assembly, and a compiler-generated lambda closure has no stable identity to match against —
/// it gets swapped for an error stub, so the pass throws "Unable to find matching substitution for a lambda
/// method" on the first render after ANY code change. A virtual override remaps by signature and survives.
/// </summary>
sealed class SdfStagePostObject : SceneCustomObject
{
	readonly SdfStagePost _post;

	public SdfStagePostObject( SceneWorld world, SdfStagePost post ) : base( world )
	{
		_post = post;
	}

	public override void RenderSceneObject() => _post?.Render();
}

sealed class SdfStagePost
{
	// Tonemapping. Mode 0 = the component's absent or set to None; matches D_TONEMAPPING in the shader.
	public int TonemapMode;
	public int ExposureMethod;

	// Sharpen. Strength <= 0 = skip, mirroring the component's own early-out.
	public float SharpenStrength;
	public float SharpenTexelSize = 1f;

	// Colour adjustments.
	public bool ColorEnabled;
	public float Blend = 1f;
	public float Saturation = 1f;
	public float HueRotate;
	public float Brightness = 1f;
	public float Contrast = 1f;

	/// <summary>
	/// The project's ink colour, and the only definition of it in code — the UI theme's <c>$paper-ink</c>
	/// (#5e4e38), the warm brown used for text and icons on paper. C# can't read SCSS, so these two are the
	/// one unavoidable duplicate: change either and change both.
	/// <para>
	/// NOT <c>$paper-edge</c> (#2c2418), which is what card and button borders use. That one is a dark line on
	/// a CREAM field, where it reads as ink; an icon outline floats over the 3D scene with no paper behind it,
	/// so the same value reads as near-black. Same colour, different context — this is the lighter of the two.
	/// </para>
	/// <para>
	/// Deliberately not a property on the rig prefab: it's a brand colour, and one authored copy per prefab
	/// would guarantee drift. Per-icon overrides still exist for genuine variation, like team colours.
	/// </para>
	/// </summary>
	public static readonly Color DefaultInk = new( 0.369f, 0.306f, 0.220f );

	// Ink outline. Width <= 0 = skip.
	public Color OutlineColor = DefaultInk;
	public float OutlineWidth;

	/// <summary>Flatten the subject to a featureless block of grey — the "who's that pokemon" silhouette. The
	/// ink outline still draws on top in its authored colour, so a silhouette icon wears the same brown ring as
	/// every portrait. Runs through the engine's own colour shader (contrast 0 collapses every covered pixel to
	/// 0.5, brightness rescales to <see cref="SilhouetteGrey"/>), so no shader of ours is involved and alpha —
	/// the shape — is untouched.</summary>
	public bool Silhouette;

	/// <summary>The silhouette's grey level, 0 (black) to 1 (white).</summary>
	public float SilhouetteGrey = 0.5f;

	/// <summary>Whether anything would actually draw — the stage skips creating the pass object if not.</summary>
	public bool Any => TonemapMode > 0 || SharpenStrength > 0f || ColorEnabled || OutlineWidth > 0f || Silhouette;

	/// <summary>
	/// Kill switch for the whole chain: `mimiclay_thumb_post false`. This pass grabs the frame buffer and blits
	/// over it, so if it misbehaves in a given render path the thumbnail goes black or transparent rather than
	/// merely looking wrong — turning it off is the fastest way to tell that apart from a stage problem.
	/// Checked per render, so it takes effect immediately on a live thumbnail.
	/// </summary>
	[ConVar( "mimiclay_thumb_post" )]
	public static bool Enabled { get; set; } = true;

	static Material _tonemapMaterial;
	static Material _sharpenMaterial;
	static Material _colorMaterial;
	static Material _outlineMaterial;

	// Our own bag rather than Graphics.Attributes, so a stage render can't leak state into whatever else is
	// drawing this frame.
	readonly RenderAttributes _attributes = new();

	/// <summary>Run the chain. Called from the stage's overlay scene object, inside its render scope.</summary>
	/// <summary>
	/// Load every shader the chain can blit. MUST be called from the main thread: <see cref="Render"/> runs on
	/// the RENDER thread and <c>Material.Create</c> asserts main-thread, so loading lazily down there throws
	/// (and <c>SceneCustomObject</c> swallows it into the log, leaving the whole chain silently dead).
	///
	/// Loads all four regardless of which are currently switched on — they're process-wide statics, so it's a
	/// one-off cost, and tying it to the flags would just mean a config change could reintroduce the crash.
	/// </summary>
	public static void EnsureMaterials()
	{
		_tonemapMaterial ??= Material.FromShader( "shaders/tonemapping/tonemapping.shader" );
		_sharpenMaterial ??= Material.FromShader( "shaders/postprocess/pp_sharpen.shader" );
		_colorMaterial ??= Material.FromShader( "shaders/postprocess/pp_color.shader" );
		_outlineMaterial ??= Material.FromShader( "shaders/sdf_thumbnail_outline.shader" );
	}

	public void Render()
	{
		if ( !Enabled )
			return;

		// Order matches the components' render stages: Tonemapping (6500), then AfterPostProcess with
		// Sharpen at order 1 and ColorAdjustments at 3000.
		// Materials are loaded up-front by EnsureMaterials — never create one here, see its remarks.
		if ( TonemapMode > 0 )
		{
			if ( _tonemapMaterial is not null )
			{
				Graphics.GrabFrameTexture( "ColorBuffer", _attributes, Graphics.DownsampleMethod.None );
				_attributes.SetCombo( "D_TONEMAPPING", TonemapMode );
				_attributes.SetCombo( "D_EXPOSUREMETHOD", ExposureMethod );
				Graphics.Blit( _tonemapMaterial, _attributes );
			}
		}

		if ( SharpenStrength > 0f )
		{
			if ( _sharpenMaterial is not null )
			{
				Graphics.GrabFrameTexture( "ColorBuffer", _attributes, Graphics.DownsampleMethod.None );
				_attributes.Set( "strength", SharpenStrength );
				_attributes.Set( "size", SharpenTexelSize );
				Graphics.Blit( _sharpenMaterial, _attributes );
			}
		}

		if ( ColorEnabled )
		{
			if ( _colorMaterial is not null )
			{
				Graphics.GrabFrameTexture( "ColorBuffer", _attributes, Graphics.DownsampleMethod.None );
				_attributes.Set( "blend", Blend );
				_attributes.Set( "saturate", Saturation );
				_attributes.Set( "hue_rotate", HueRotate );
				_attributes.Set( "brightness", Brightness );
				_attributes.Set( "contrast", Contrast );
				Graphics.Blit( _colorMaterial, _attributes );
			}
		}

		// Silhouette flatten — after the rig's own grade (whose output it discards anyway) and BEFORE the
		// outline, so the ink still lands on top in its exact authored colour. pp_color applies contrast first
		// and multiplies brightness after, which is what makes the grey tunable: contrast 0 → flat 0.5,
		// brightness 2·grey → the requested level.
		if ( Silhouette )
		{
			if ( _colorMaterial is not null )
			{
				Graphics.GrabFrameTexture( "ColorBuffer", _attributes, Graphics.DownsampleMethod.None );
				_attributes.Set( "blend", 1f );
				_attributes.Set( "saturate", 0f );
				_attributes.Set( "hue_rotate", 0f );
				_attributes.Set( "brightness", SilhouetteGrey * 2f );
				_attributes.Set( "contrast", 0f );
				Graphics.Blit( _colorMaterial, _attributes );
			}
		}

		// LAST, deliberately — after tonemapping and colour grading, so the ink lands as the exact colour it was
		// authored as rather than whatever the grade would have turned it into.
		if ( OutlineWidth > 0f )
		{
			if ( _outlineMaterial is not null )
			{
				Graphics.GrabFrameTexture( "ColorBuffer", _attributes, Graphics.DownsampleMethod.None );
				_attributes.Set( "OutlineColor", new Vector3( OutlineColor.r, OutlineColor.g, OutlineColor.b ) );
				_attributes.Set( "OutlineWidth", OutlineWidth );
				Graphics.Blit( _outlineMaterial, _attributes );
			}
		}
	}

	/// <summary>Mirror the post-process components authored on the rig prefab's camera.</summary>
	public void ReadFrom( Scene rigRoot )
	{
		if ( !rigRoot.IsValid() )
			return;

		// The outline isn't an engine effect — it's ours, so its WIDTH is configured on the rig component rather
		// than by dropping a post-process component on the camera. The colour isn't here at all: see DefaultInk.
		if ( rigRoot.GetAllComponents<SdfStageRig>().FirstOrDefault() is { } rig )
			OutlineWidth = rig.OutlineFraction;

		if ( rigRoot.GetAllComponents<Tonemapping>().FirstOrDefault() is { } tonemap )
		{
			TonemapMode = (int)tonemap.Mode;
			ExposureMethod = (int)tonemap.ExposureMethod;
		}

		if ( rigRoot.GetAllComponents<Sharpen>().FirstOrDefault() is { } sharpen )
		{
			SharpenStrength = sharpen.Scale;
			SharpenTexelSize = sharpen.TexelSize;
		}

		if ( rigRoot.GetAllComponents<ColorAdjustments>().FirstOrDefault() is { } colour )
		{
			ColorEnabled = true;
			Blend = colour.Blend;
			Saturation = colour.Saturation;
			HueRotate = colour.HueRotate;
			Brightness = colour.Brightness;
			Contrast = colour.Contrast;
		}
	}
}
