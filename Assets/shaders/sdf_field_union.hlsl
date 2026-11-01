//=========================================================================================================================
// Union of up to 4 baked SDF field slots — the field-only march input shared by sdf_highlight.shader and sdf_ghost.shader.
// Each slot is one SdfRaymarchRenderer's baked field (dense volume + optional sparse brick atlas + its model fold), bound
// per slot by SdfRaymarchRenderer.ApplyHighlightAttributes. Include inside the PS block, after common/pixel.hlsl.
//=========================================================================================================================
	// ── Shared march state (set by the driving component — SdfHighlightOutline / SdfGhost: union bracket + march budget) ──
	int    g_nFieldCount    < Attribute( "FieldCount" ); Default( 1 ); >; // occupied slots (1..4)
	float3 g_vBoundsMin     < Attribute( "BoundsMin" ); >;                // union world AABB bracket
	float3 g_vBoundsMax     < Attribute( "BoundsMax" ); >;
	int    g_nMaxSteps      < Attribute( "MaxSteps" ); Default( 96 ); >;
	float  g_flEpsilon      < Attribute( "Epsilon" ); Default( 0.05 ); >;

	SamplerState g_sField < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); AddressW( CLAMP ); >;

	// ── Field slots, one per grouped SdfRaymarchRenderer (mirrored by ApplyHighlightAttributes).
	// MUST match SdfHighlightOutline.MaxTargets. Same layout per slot: dense volume + optional
	// sparse brick atlas + the prop's model fold (the field is baked in the prop's local frame). ──
	#define SDF_BLOCK    8.0
	#define SDF_TILESIZE 9.0
	#define SDF_TILESX   16u
	#define SDF_TILESY   16u

	Texture3D g_tField0 < Attribute( "FieldTex0" ); >;
	float3 g_vFieldMin0   < Attribute( "FieldMin0" ); >;
	float3 g_vFieldMax0   < Attribute( "FieldMax0" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims0  < Attribute( "FieldDims0" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims0< Attribute( "SurfaceDims0" ); >;
	float3 g_vModelOrigin0< Attribute( "ModelOrigin0" ); >;
	float4 g_vModelRot0   < Attribute( "ModelRotation0" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas0       < Attribute( "Atlas0" ); >;
	Texture2D g_tIndirection0 < Attribute( "IndirectionTex0" ); >;
	float3 g_vBrickDims0 < Attribute( "BrickDims0" ); >;
	float3 g_vAtlasDims0 < Attribute( "AtlasDims0" ); >;
	int    g_nSdfSparse0 < Attribute( "SdfSparse0" ); Default( 0 ); >;
	float  g_flAtlasEncode0 < Attribute( "AtlasEncode0" ); Default( 0.0 ); >; // 8-bit tiles: encode band (world units), 0 = raw R32F

	Texture3D g_tField1 < Attribute( "FieldTex1" ); >;
	float3 g_vFieldMin1   < Attribute( "FieldMin1" ); >;
	float3 g_vFieldMax1   < Attribute( "FieldMax1" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims1  < Attribute( "FieldDims1" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims1< Attribute( "SurfaceDims1" ); >;
	float3 g_vModelOrigin1< Attribute( "ModelOrigin1" ); >;
	float4 g_vModelRot1   < Attribute( "ModelRotation1" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas1       < Attribute( "Atlas1" ); >;
	Texture2D g_tIndirection1 < Attribute( "IndirectionTex1" ); >;
	float3 g_vBrickDims1 < Attribute( "BrickDims1" ); >;
	float3 g_vAtlasDims1 < Attribute( "AtlasDims1" ); >;
	int    g_nSdfSparse1 < Attribute( "SdfSparse1" ); Default( 0 ); >;
	float  g_flAtlasEncode1 < Attribute( "AtlasEncode1" ); Default( 0.0 ); >;

	Texture3D g_tField2 < Attribute( "FieldTex2" ); >;
	float3 g_vFieldMin2   < Attribute( "FieldMin2" ); >;
	float3 g_vFieldMax2   < Attribute( "FieldMax2" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims2  < Attribute( "FieldDims2" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims2< Attribute( "SurfaceDims2" ); >;
	float3 g_vModelOrigin2< Attribute( "ModelOrigin2" ); >;
	float4 g_vModelRot2   < Attribute( "ModelRotation2" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas2       < Attribute( "Atlas2" ); >;
	Texture2D g_tIndirection2 < Attribute( "IndirectionTex2" ); >;
	float3 g_vBrickDims2 < Attribute( "BrickDims2" ); >;
	float3 g_vAtlasDims2 < Attribute( "AtlasDims2" ); >;
	int    g_nSdfSparse2 < Attribute( "SdfSparse2" ); Default( 0 ); >;
	float  g_flAtlasEncode2 < Attribute( "AtlasEncode2" ); Default( 0.0 ); >;

	Texture3D g_tField3 < Attribute( "FieldTex3" ); >;
	float3 g_vFieldMin3   < Attribute( "FieldMin3" ); >;
	float3 g_vFieldMax3   < Attribute( "FieldMax3" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vFieldDims3  < Attribute( "FieldDims3" ); Default3( 2.0, 2.0, 2.0 ); >;
	float3 g_vSurfaceDims3< Attribute( "SurfaceDims3" ); >;
	float3 g_vModelOrigin3< Attribute( "ModelOrigin3" ); >;
	float4 g_vModelRot3   < Attribute( "ModelRotation3" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	Texture3D g_tAtlas3       < Attribute( "Atlas3" ); >;
	Texture2D g_tIndirection3 < Attribute( "IndirectionTex3" ); >;
	float3 g_vBrickDims3 < Attribute( "BrickDims3" ); >;
	float3 g_vAtlasDims3 < Attribute( "AtlasDims3" ); >;
	int    g_nSdfSparse3 < Attribute( "SdfSparse3" ); Default( 0 ); >;
	float  g_flAtlasEncode3 < Attribute( "AtlasEncode3" ); Default( 0.0 ); >;

	// Displacement gradient bound, PER SLOT. The lumps (and their claymation boil) are baked INTO
	// each member's field by SdfFieldGpu, so the outline tracks the lumpy silhouette by construction —
	// what each member still owes the march is the step-safety factor: a displaced field's slope is
	// inflated by up to L = worst-tick-amp · freq · 4, and the union march must understep by the
	// steepest member's 1/(1+L). 0 = that member's field is a true distance field (Displace off),
	// which is also what an unoccupied slot gets.
	float g_flDispGradL0 < Attribute( "DispGradL0" ); Default( 0.0 ); >;
	float g_flDispGradL1 < Attribute( "DispGradL1" ); Default( 0.0 ); >;
	float g_flDispGradL2 < Attribute( "DispGradL2" ); Default( 0.0 ); >;
	float g_flDispGradL3 < Attribute( "DispGradL3" ); Default( 0.0 ); >;

	// Rotate vector v by quaternion q (same as sdf_raymarch).
	float3 qrot( float4 q, float3 v )
	{
		return v + 2.0 * cross( q.xyz, cross( q.xyz, v ) + q.w * v );
	}

	// (The per-sample displacement noise that lived here is gone: the lumps — claymation boil
	// included — are baked into each member's field by SdfFieldGpu, so sampling the field IS
	// sampling the displaced surface. Only the per-slot gradient bound above survives.)

	// Sample ONE slot's baked field at world point wp: fold into that prop's local frame, fetch the
	// dense volume (or the sparse brick atlas near the surface), then REPAIR the sample outside the
	// slot's grid. The repair is what makes the union valid: the shared bracket spans every member's
	// region, and outside its own grid a slot's CLAMP sampler returns the tiny boundary value — raw,
	// that false "always near" reading smears outline across the whole box and stalls the march.
	// dist² ≥ distToRegion² + clampedSample² is the same smooth lower bound sdText uses for its quad
	// (the legs can't oppose: the nearest surface is inward of the clamp point, wp is outward of it).
	float SampleSlot(
		Texture3D field, Texture3D atlas, Texture2D indirection, int sparse,
		float3 fmin, float3 fmax, float3 fdims, float3 sdims, float3 bdims, float3 adims, float aenc,
		float3 origin, float4 rot, float3 wp )
	{
		float3 lp = qrot( float4( -rot.xyz, rot.w ), wp - origin );

		// Dense fetch (the sparse path's low-res guide is the same texture).
		float3 ext = max( fmax - fmin, 1e-4 );
		float3 t   = (lp - fmin) / ext;
		float3 uvw = (t * (fdims - 1.0) + 0.5) / fdims;
		float d = field.SampleLevel( g_sField, uvw, 0 ).r;

		if ( sparse != 0 )
		{
			// Near the surface, swap the guide for the high-res brick atlas (see sdf_raymarch).
			float3 scell = ext / max( sdims - 1.0, 1.0 );
			float  nearBand = 2.0 * SDF_BLOCK * max( scell.x, max( scell.y, scell.z ) );
			if ( d <= nearBand )
			{
				float3 fv    = (lp - fmin) / scell;
				int3   bd    = (int3)( bdims + 0.5 );
				int3   brick = (int3)floor( fv / SDF_BLOCK );
				if ( !( any( brick < 0 ) || any( brick >= bd ) ) )
				{
					float ind = indirection.Load( int3( brick.x, brick.y + bd.y * brick.z, 0 ) ).r;
					float maxTiles = (adims.x / SDF_TILESIZE) * (adims.y / SDF_TILESIZE) * (adims.z / SDF_TILESIZE);
					if ( ind >= 0.0 && ind < maxTiles )
					{
						float3 vit = fv - (float3)brick * SDF_BLOCK;
						uint   tile = (uint)( ind + 0.5 );
						int3   tc  = int3( tile % SDF_TILESX, ( tile / SDF_TILESX ) % SDF_TILESY, tile / ( SDF_TILESX * SDF_TILESY ) );
						float3 av  = (float3)tc * SDF_TILESIZE + vit;
						float  at  = atlas.SampleLevel( g_sField, ( av + 0.5 ) / adims, 0 ).r;
						if ( aenc > 0.0 )
						{
							// 8-bit unorm tiles: decode ±band, then blend saturated magnitudes back to the
							// guide value (d, still the dense fetch here) — see sdf_raymarch's SampleFieldSparse.
							at = ( at - 0.5 ) * ( 2.0 * aenc );
							at = lerp( at, d, smoothstep( 0.7 * aenc, 0.95 * aenc, abs( at ) ) );
						}
						d = at;
					}
				}
			}
		}

		// Out-of-region repair (see above). Inside the grid dq = 0 and the sample passes through
		// untouched (including negative, inside-the-prop values).
		float3 q  = max( max( fmin - lp, lp - fmax ), 0.0 );
		float  dq = length( q );
		if ( dq > 0.0 )
		{
			float dz = max( d, 0.0 );
			d = sqrt( dq * dq + dz * dz );
		}
		return d;
	}

	// Union distance over the occupied slots — min() of per-slot distances, the combined group surface.
	float SdfDistAll( float3 p )
	{
		float d = SampleSlot( g_tField0, g_tAtlas0, g_tIndirection0, g_nSdfSparse0,
			g_vFieldMin0, g_vFieldMax0, g_vFieldDims0, g_vSurfaceDims0, g_vBrickDims0, g_vAtlasDims0, g_flAtlasEncode0,
			g_vModelOrigin0, g_vModelRot0, p );

		if ( g_nFieldCount > 1 )
			d = min( d, SampleSlot( g_tField1, g_tAtlas1, g_tIndirection1, g_nSdfSparse1,
				g_vFieldMin1, g_vFieldMax1, g_vFieldDims1, g_vSurfaceDims1, g_vBrickDims1, g_vAtlasDims1, g_flAtlasEncode1,
				g_vModelOrigin1, g_vModelRot1, p ) );

		if ( g_nFieldCount > 2 )
			d = min( d, SampleSlot( g_tField2, g_tAtlas2, g_tIndirection2, g_nSdfSparse2,
				g_vFieldMin2, g_vFieldMax2, g_vFieldDims2, g_vSurfaceDims2, g_vBrickDims2, g_vAtlasDims2, g_flAtlasEncode2,
				g_vModelOrigin2, g_vModelRot2, p ) );

		if ( g_nFieldCount > 3 )
			d = min( d, SampleSlot( g_tField3, g_tAtlas3, g_tIndirection3, g_nSdfSparse3,
				g_vFieldMin3, g_vFieldMax3, g_vFieldDims3, g_vSurfaceDims3, g_vBrickDims3, g_vAtlasDims3, g_flAtlasEncode3,
				g_vModelOrigin3, g_vModelRot3, p ) );

		return d;
	}
