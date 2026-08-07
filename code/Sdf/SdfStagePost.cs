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

	/// <summary>Whether anything would actually draw — the stage skips creating the pass object if not.</summary>
	public bool Any => TonemapMode > 0 || SharpenStrength > 0f || ColorEnabled;

	static Material _tonemapMaterial;
	static Material _sharpenMaterial;
	static Material _colorMaterial;

	// Our own bag rather than Graphics.Attributes, so a stage render can't leak state into whatever else is
	// drawing this frame.
	readonly RenderAttributes _attributes = new();

	/// <summary>Run the chain. Called from the stage's overlay scene object, inside its render scope.</summary>
	public void Render()
	{
		// Order matches the components' render stages: Tonemapping (6500), then AfterPostProcess with
		// Sharpen at order 1 and ColorAdjustments at 3000.
		if ( TonemapMode > 0 )
		{
			_tonemapMaterial ??= Material.FromShader( "shaders/tonemapping/tonemapping.shader" );
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
			_sharpenMaterial ??= Material.FromShader( "shaders/postprocess/pp_sharpen.shader" );
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
			_colorMaterial ??= Material.FromShader( "shaders/postprocess/pp_color.shader" );
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
	}

	/// <summary>Mirror the post-process components authored on the rig prefab's camera.</summary>
	public void ReadFrom( Scene rigRoot )
	{
		if ( !rigRoot.IsValid() )
			return;

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
