# Small cart props (v2 = user-tuned 2026-11-12): teal tray, drink can, lidded food tub. Base at z=0.
# Colour variants are scene overrides on these prefabs: a blue can (#2E6A9E, two Box Colour halves for the label)
# and an orange-lidded tub (#E7A739 lid / #C68617 inset).
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

# --- tray: deeper rounded slab, hollowed, then a wider flat cut lowers the walls to a shallow lip ---
$teal = '0.201,0.458,0.498,1'
Write-SdfProp "$OutDir/tray_teal.prefab" 'tray_teal' @(
    (B @{ Shape = 'Box'; Position = '0,0,7.804'; Size = '38,27,7.772'; Rounding = 4.5; Blend = 6; Color = $teal })
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,11'; Size = '34.577,23.083,6.5'; Rounding = 3; Blend = 0.8; Color = $teal })
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,18.694'; Size = '41.795,30.691,6.5'; Rounding = 3; Blend = 0.8; Color = $teal })
) '0,0,5'

# --- can: painted-metal body, crisp label band (Colour op), smaller recessed metal lid, base dished out ---
$metal = '0.75,0.76,0.78,1'
Write-SdfProp "$OutDir/can_red.prefab" 'can_red' @(
    (B @{ Shape = 'Cylinder'; Position = '0,0,17'; Size = '11,11,17'; Rounding = 3; Blend = 6; Color = '0.6,0.255,0.232,1'; Metallic = 0.573; Roughness = 0.597 })
    (B @{ Shape = 'Cylinder'; Operation = 'Colour'; Position = '0,0,17'; Size = '12,12,7'; Blend = 0; Color = '0.9,0.85,0.8,1'; Roughness = 0.4 })
    (B @{ Shape = 'Cylinder'; Position = '0,0,35.507'; Size = '8.274,8.274,1.324'; Rounding = 1.5; Blend = 0; Color = $metal; Metallic = 1; Roughness = 0.366 })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,36.752'; Size = '6.755,6.755,1.386'; Rounding = 1; Blend = 1.3; Color = '0.509,0.51,0.512,1'; Metallic = 1; Roughness = 0.366 })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,-0.765'; Size = '8.095,8.095,2.707'; Rounding = 8; Blend = 0.2; Color = $metal; Metallic = 1; Roughness = 0.366 })
) '0,0,17'

# --- food tub: opaque cream body + flared collar + clips, a flat cut, then the coloured lid with an inset + lid tabs ---
$cream = '0.893,0.856,0.793,1'; $lid = '0.349,0.493,0.688,1'
Write-SdfProp "$OutDir/tub_blue.prefab" 'tub_blue' @(
    (B @{ Shape = 'Box'; Position = '0,0,14.137'; Size = '42.469,30.524,19.907'; Rounding = 6.636; Blend = 7.963; Color = $cream })
    (B @{ Shape = 'Box'; Position = '0,0,35.371'; Size = '44.46,32.515,4.512'; Rounding = 3.981; Blend = 3.052; Color = $cream })
    (B @{ Shape = 'Box'; Position = '0,32.091,30.638'; Size = '7.963,3.08,5.7'; Rounding = 1.991; Blend = 1.593; MirrorY = $true; Color = $cream })
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,38.953'; Size = '56.417,37.148,4.512'; Rounding = 3.981; Blend = 0; Color = '0.86,0.9,0.92,1' })
    (B @{ Shape = 'Box'; Position = '0,0,40.444'; Size = '46.035,34.002,6.079'; Rounding = 3.981; Blend = 0; Color = $lid })
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,46.194'; Size = '37.873,24.909,3.782'; Rounding = 2.654; Blend = 1.062; Color = '0.235,0.376,0.567,1' })
    (B @{ Shape = 'Box'; Position = '0,33.278,35.821'; Size = '7.963,3.534,3.854'; Rounding = 1.991; Blend = 0.398; MirrorY = $true; Color = $lid })
) '0,0,17'
