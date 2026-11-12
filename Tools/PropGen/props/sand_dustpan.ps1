# Kids' toy dustpan — a big, very rounded block carved into a pan: flat base cut, deep hollow (thick walls/floor),
# sloped top cut so the side walls fall toward the front, and a near-vertical front trim that leaves a blunt,
# chunky lip. Thick fully-rounded handle with a hang slot, triangular gusset that also forms a rib on the
# inner back wall. Lies on its floor, pan toward +X, handle toward -X.
# v2 (2026-11-12): the user's rebuild — ~2x scale, no Smooth modifier, blunt front instead of an under-chamfer.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/sand_dustpan.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/sand_dustpan.prefab" }

$col = '0.39109,0.49926,0.64186,1'   # user-muted from the reference's brighter blue

$handleTilt = 12.97                   # handle rises toward -X
$qHandle = Qaxis 0 1 0 $handleTilt

$brushes = @(
    # pan block: rounding ~0.48 of the half-width -> pillowy
    (B @{ Shape = 'Box'; Position = '41.596,0,9.275'; Size = '37.77,33.14,27.021'
          Rounding = 16; Blend = 6; Color = $col; Roughness = 0.4 })
    # flat base at z~2.4
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '39.962,0,-10.87'; Size = '41.868,36.865,13.282'
          Rounding = 0; Blend = 1.1; Color = $col; Roughness = 0.4 })
    # hollow: open past the front; back wall ~7.9, side walls ~7.2, floor ~5.4 (chunky)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '53.599,0,25.744'; Size = '41.868,25.96,17.956'
          Rounding = 5.6; Blend = 0; Color = $col; Roughness = 0.4 })
    # sloped top: wall top ~23 at the back falling to ~6 at the front (12.7 deg)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '44.267,0,26.859'; Rotation = (Qaxis 0 1 0 12.716); Size = '42.745,36.666,12.577'
          Rounding = 0; Blend = 0.9; Color = $col; Roughness = 0.4 })
    # front trim: near-vertical face at x~66 (leans 3.6 deg) -> blunt, chunky lip
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '81.36,0,9.21'; Rotation = (Qaxis 0 1 0 3.627); Size = '15.019,38.253,14.538'
          Rounding = 0; Blend = 2; Color = $col; Roughness = 0.4 })
    # handle: thick fully-rounded bar (r~6)
    (B @{ Shape = 'Box'; Position = '-14.154,0,20.537'; Rotation = $qHandle; Size = '24.588,6,6'
          Rounding = 8; Blend = 0; Color = $col; Roughness = 0.4 })
    # hang slot through the handle near its end
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '-27.384,0,24.894'; Rotation = $qHandle; Size = '4.898,3,9.778'
          Rounding = 8; Blend = 1.7; Color = $col; Roughness = 0.4 })
    # gusset: flat triangle, steeper than the handle (16.6 deg); added AFTER the hollow so its base pokes
    # through the back wall as a rib inside the pan
    (B @{ Shape = 'Extruded'; CrossSection = 'Triangle'; Position = '5.969,0,20.676'; Rotation = (Qmul (Qaxis 0 1 0 16.57) (Qaxis 0 0 1 90))
          Size = '11,9,3.3'; Rounding = 2.3; Blend = 0; Color = $col; Roughness = 0.4 })
)

Write-SdfProp $Out 'sand_dustpan' $brushes '0,0,4'
