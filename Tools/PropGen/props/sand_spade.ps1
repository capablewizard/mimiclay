# Kids' sand spade — carved scoop (rounded block, flat-cut base, sloped top cut, open-front hollow),
# tilted shaft + ring grip (shared: _sand_toy_handle.ps1).
# v2 (2026-11-11): the user's rebuild. Carve-don't-stack: one big rounded block shaped by flat Subtract planes;
# Blend 0 between parts; the hollow uses the BODY colour (no darker tint on an open scoop).
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/sand_spade.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
. "$PSScriptRoot/_sand_toy_handle.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/sand_spade.prefab" }

$blue = RGB255 96 150 206
$scoopX = 23.67

$brushes = @(
    # scoop block: big, very rounded (rounding ~0.4 of the half-width)
    (B @{ Shape = 'Box'; Position = (V3 $scoopX 0 7.33); Size = '15.682,16.802,12.768'
          Rounding = 6.05; Blend = 6; Color = $blue; Roughness = 0.4 })
    # flat base: plane cut at z~2.7, soft edge (blend 2.1)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 $scoopX 0 -5.383); Size = '18.797,20.346,8.074'
          Rounding = 0; Blend = 2.1; Color = $blue; Roughness = 0.4 })
    # sloped top: same slab tilted ~10.9 deg so the walls drop toward the front lip (z ~13 back -> ~7 front)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 $scoopX 0 18.187); Rotation = (Qaxis 0 1 0 10.88); Size = '18.797,20.346,8.074'
          Rounding = 0; Blend = 0.6; Color = $blue; Roughness = 0.4 })
    # hollow: open past the front, ~3.6 side/back walls, ~2.3 floor, crisp (blend 0)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '29.689,0,22.771'; Size = '18.04,13.226,17.744'
          Rounding = 3.75; Blend = 0; Color = $blue; Roughness = 0.4 })
)
$brushes += SandToyHandle $blue

Write-SdfProp $Out 'sand_spade' $brushes '0,0,4'
