#ifndef CLAY_SHADING_HLSL
#define CLAY_SHADING_HLSL

//
// ShadingModelStandard::Shade with engine decals made optional.
//
// The engine has no decal opt-out: decals aren't a pass, they're folded into the material inside Shade
// (Decals::Apply → Cluster::Query), and Decal.ExclusionBitMask is never read by the shader. So the clay
// shaders call this instead, which mirrors Shade( PixelInput, Material ) from common/shadingmodel.hlsl line
// for line apart from gating the Decals::Apply call. Keep it in step with the engine's copy when that changes.
//
// Two switches, both must allow it: g_bReceiveDecals is per MATERIAL (the hunter's and viewmodel's vmats turn
// it off), g_nSdfNoDecals is per OBJECT (SdfRaymarchRenderer.ReceiveDecals, for one renderer on a shared vmat).
//
bool  g_bReceiveDecals < Default( 1 ); UiGroup( "Surface,10/97" ); >;
int   g_nSdfNoDecals   < Attribute( "SdfNoDecals" ); Default( 0 ); >;

// Per-OBJECT sun exposure from IndoorVolume (SdfRaymarchRenderer pushes it onto the raymarch scene object
// AND its sibling mesh): 1 = outdoors, fading to 0 inside a volume. At exactly 0 the sun's direct lighting
// is skipped below (cascade PCF + screen-space shadow fetch + cascade blend), as is its transmission in
// sdf_raymarch and its shadow-cascade casting in both VSs (they mirror this declaration).
float g_flSdfSunExposure < Attribute( "SdfSunExposure" ); Default( 1.0 ); >;

// ComputeDirectLighting( o, f, nClusterIndex, vTransmissiveStart ) from vr_lighting.fxc, line for line, apart
// from the per-object gate on the directional light. Keep it in step with the engine's copy when that changes.
void ClayDirectLighting( inout LightingTerms_t o, const FinalCombinerInput_t f )
{
	o.vDiffuse 		= float3( 0.0, 0.0, 0.0 );
	o.vSpecular 	= float3( 0.0, 0.0, 0.0 );
	o.vTransmissive = float3( 0.0, 0.0, 0.0 );

	const float3 vReceiverNormalWs = ComputeShadowReceiverNormal( f.vPositionWithOffsetWs.xyz );

	// Indoors (exposure 0) the roof already shadows the sun to zero, so this only skips work. The one
	// visible difference: a prop inside a volume but standing in a window's sunbeam gets no direct sun.
	[branch]
	if ( g_flSdfSunExposure > 0.0 && !LightmappedLight::UsesLightmaps() && !ProbeLight::UsesProbes() )
		ComputeDirectionalLight( f, vReceiverNormalWs, o.vDiffuse.rgb, o.vSpecular.rgb, o.vTransmissive.rgb );

	if ( DirectionalLightDebug > 0 && g_DirectionalLightCascadeCount > 0 )
	{
		float3 debugColor = DirectionalLightShadow::GetDebugColor( f.vPositionWs.xyz );
		o.vDiffuse.rgb += debugColor;
	}

	ClusterRange lightRange = Cluster::Query( ClusterItemType_Light, Cluster::Index( f.vPositionSs ) );

	if ( !WaveActiveAnyTrue( lightRange.Count > 0 ) )
		return;

	uint features = Cluster::WaveFeatures( lightRange ) & ( LightModulators::Active() ? ClusterFeature_LightModulators : 0 );

	ClusterForEach( lightRange )
		ComputeDirectLightingForLight( f, vReceiverNormalWs, DynamicLightConstantByIndex( lightRange.Item ), o.vDiffuse.rgb, o.vSpecular.rgb, o.vTransmissive.rgb, false, features != 0 );
}

float4 ClayShade( PixelInput i, Material m )
{
	m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
	m.WorldPosition = i.vPositionWithOffsetWs + g_vHighPrecisionLightingOffsetWs.xyz;
	m.ScreenPosition = i.vPositionSs;

	// Want it right before the lighting
	[branch] if ( g_bReceiveDecals && g_nSdfNoDecals == 0 )
		Decals::Apply( m.WorldPosition, m );

	// Do our magic alpha to coverage adjustment
	AdjustAlphaToCoverage( m );

	LightingTerms_t lightingTerms = InitLightingTerms();
	CombinerInput combinerInput = ShadingModelStandard::MaterialToCombinerInput( m );

	// Calculate lighting
	{
		ClayDirectLighting( lightingTerms, combinerInput );
		CalculateIndirectLighting( lightingTerms, combinerInput );
	}

	// Composite lighting terms, apply adjustments
	float4 color;
	{
		float3 vDiffuseAO = CalculateDiffuseAmbientOcclusion( combinerInput, lightingTerms );
		lightingTerms.vIndirectDiffuse.rgb *= vDiffuseAO.rgb;
		lightingTerms.vDiffuse.rgb *= lerp( float3( 1.0, 1.0, 1.0 ), vDiffuseAO.rgb, combinerInput.flAmbientOcclusionDirectDiffuse );

		float3 vSpecularAO = CalculateSpecularAmbientOcclusion( combinerInput, lightingTerms );
		lightingTerms.vIndirectSpecular.rgb *= vSpecularAO.rgb;
		lightingTerms.vSpecular.rgb *= lerp( float3( 1.0, 1.0, 1.0 ), vSpecularAO.rgb, combinerInput.flAmbientOcclusionDirectSpecular );

		float3 vDiffuse = ( ( lightingTerms.vDiffuse.rgb + lightingTerms.vIndirectDiffuse.rgb ) * combinerInput.vDiffuseColor.rgb ) + combinerInput.vEmissive.rgb;
		float3 vSpecular = lightingTerms.vSpecular.rgb + lightingTerms.vIndirectSpecular.rgb;

		color = float4( vDiffuse + vSpecular, m.Opacity );
	}

	if ( DepthNormals::WantsDepthNormals() )
		return DepthNormals::Output( m.Normal, m.Roughness, color.a );

	if ( g_bWireframeMode )
		return g_vWireframeColor;

	if ( ToolsVis::WantsToolsVis() )
		return ShadingModelStandard::DoToolsVis( color, m, lightingTerms );

	// Composite atmospherics after lighting
	return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, color );
}

#endif
