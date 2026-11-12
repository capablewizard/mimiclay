# Snack cart (blockout) — two-shelf wooden trolley on four casters, round end rails (towel bar).
# Scale: sized off a 30-tall mug (whole cart set). Length along X (~285), depth Y (~115), rails at z~172.
# Top shelf surface z=150, bottom shelf surface z=58 (other cart props sit on these).
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/cart/cart_trolley.ps1 [-OutDir <dir>]
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

$wood  = '0.62,0.36,0.22,1'
$tyre  = '0.22,0.22,0.24,1'
$fork  = '0.55,0.56,0.58,1'

$px = 133; $py = 48            # post centres (MirrorX + MirrorY)

$brushes = @(
    # top shelf slab with a deep apron (z 132..150)
    (B @{ Shape = 'Box'; Position = '0,0,141'; Size = '142,57,9'; Rounding = 4; Blend = 6; Color = $wood; Roughness = 0.6 })
    # shallow tray inset on the top shelf (flat faces get an inset)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,152'; Size = '120,47,4'; Rounding = 3; Blend = 0.8; Color = $wood; Roughness = 0.6 })
    # bottom shelf + raised front/back boards
    (B @{ Shape = 'Box'; Position = '0,0,52'; Size = '142,57,6'; Rounding = 3; Blend = 0; Color = $wood; Roughness = 0.6 })
    (B @{ Shape = 'Box'; Position = (V3 0 54 64); Size = '142,4,6'; MirrorY = $true; Rounding = 3; Blend = 0; Color = $wood; Roughness = 0.6 })
    # four posts (one brush, mirrored)
    (B @{ Shape = 'Box'; Position = (V3 $px $py 103); Size = '9,9,77'; MirrorX = $true; MirrorY = $true; Rounding = 4; Blend = 0; Color = $wood; Roughness = 0.6 })
    # end rails along Y, overhanging the posts (front overhang = towel bar)
    (B @{ Shape = 'Box'; Position = (V3 $px 0 172); Size = '6.5,78,6.5'; MirrorX = $true; Rounding = 8; Blend = 0; Color = $wood; Roughness = 0.6 })
    # caster forks + wheels
    (B @{ Shape = 'Box'; Position = (V3 $px $py 22); Size = '6,8,6'; MirrorX = $true; MirrorY = $true; Rounding = 2.5; Blend = 0; Color = $fork; Roughness = 0.45 })
    (B @{ Shape = 'Cylinder'; Position = (V3 $px $py 14); Rotation = $QAlongY; Size = '14,14,5.5'; MirrorX = $true; MirrorY = $true; Rounding = 4; Blend = 0; Color = $tyre; Roughness = 0.7 })
)

Write-SdfProp "$OutDir/cart_trolley.prefab" 'cart_trolley' $brushes '0,0,90'
