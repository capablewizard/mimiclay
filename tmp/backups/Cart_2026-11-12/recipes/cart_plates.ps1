# Plate stack (blockout): five pill-edged plates, slightly uneven, top plate dished. Base at z=0.
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

$cream = '0.92,0.88,0.8,1'
$radii = @(27, 26.6, 27.3, 26.8, 27)
$offs  = @(0, 0.8, -0.6, 0.5, -0.3)
$brushes = @()
for ($i = 0; $i -lt 5; $i++) {
    $blend = if ($i -eq 0) { 6 } else { 0 }
    $brushes += (B @{ Shape = 'Cylinder'; Position = (V3 $offs[$i] 0 (2.6 + 4.6 * $i)); Size = (V3 $radii[$i] $radii[$i] 2.6)
                      Rounding = 2.6; Blend = $blend; Color = $cream; Roughness = 0.35 })
}
# shallow dish in the top plate
$brushes += (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = (V3 $offs[4] 0 25.4); Size = '19,19,2.6'
                  Rounding = 2; Blend = 1; Color = $cream; Roughness = 0.35 })

Write-SdfProp "$OutDir/plate_stack.prefab" 'plate_stack' $brushes '0,0,12'
