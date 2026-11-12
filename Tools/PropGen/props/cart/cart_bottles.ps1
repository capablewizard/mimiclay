# Squeeze bottle (v2 = user-tuned 2026-11-12). Base at z=0. Oval pillowy body, short flat collar, tapered nozzle.
# Colour variants (mustard #CA9834, white #EBD6AD body) are scene overrides on this prefab, not separate prefabs.
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

$cap = '0.92,0.84,0.68,1'
Write-SdfProp "$OutDir/bottle_ketchup.prefab" 'bottle_ketchup' @(
    (B @{ Shape = 'Cylinder'; Position = '0,0,25'; Size = '12.837,11,25'; Rounding = 5.5; Blend = 6; Color = '0.712,0.315,0.281,1' })
    # collar: squat band, barely blended
    (B @{ Shape = 'Cylinder'; Position = '0,0,53.915'; Size = '7.48,7.48,3.333'; Rounding = 1.4; Blend = 0.4; Color = $cap })
    # nozzle (cone)
    (B @{ Shape = 'Cylinder'; Position = '0,0,59.357'; Size = '5,5,6.5'; Taper = 0.65; Rounding = 1.5; Blend = 0; Color = $cap })
) '0,0,25'
