# Small cart props (blockout): teal tray, two drink cans, two lidded food tubs, two mugs, tea towel. Base at z=0.
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

$metal = '0.75,0.76,0.78,1'

# --- tray: rounded slab, hollowed (body colour) ---
$teal = '0.3,0.6,0.6,1'
Write-SdfProp "$OutDir/tray_teal.prefab" 'tray_teal' @(
    (B @{ Shape = 'Box'; Position = '0,0,5'; Size = '38,27,5'; Rounding = 4.5; Blend = 6; Color = $teal; Roughness = 0.4 })
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,11'; Size = '33,22,6.5'; Rounding = 3; Blend = 0.8; Color = $teal; Roughness = 0.4 })
) '0,0,5'

# --- cans: body, painted label band (Colour op), metal lid with recess, base ring ---
function Can([string]$name, [string]$body, [string]$label) {
    $brushes = @(
        (B @{ Shape = 'Cylinder'; Position = '0,0,17'; Size = '11,11,17'; Rounding = 3; Blend = 6; Color = $body; Roughness = 0.4 })
        (B @{ Shape = 'Cylinder'; Operation = 'Colour'; Position = '0,0,17'; Size = '12,12,7'; Blend = 1; Color = $label; Roughness = 0.4 })
        (B @{ Shape = 'Cylinder'; Position = '0,0,34'; Size = '10,10,1.6'; Rounding = 1.5; Blend = 0; Color = $metal; Metallic = 0.6; Roughness = 0.35 })
        (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,36.2'; Size = '7.8,7.8,1.6'; Rounding = 1; Blend = 0.5; Color = $metal; Metallic = 0.6; Roughness = 0.35 })
        (B @{ Shape = 'Cylinder'; Position = '0,0,1.2'; Size = '9.6,9.6,1.2'; Rounding = 1.2; Blend = 0; Color = $metal; Metallic = 0.6; Roughness = 0.35 })
    )
    Write-SdfProp "$OutDir/$name.prefab" $name $brushes '0,0,17'
}
Can 'can_teal' '0.5,0.72,0.75,1' '0.72,0.85,0.87,1'
Can 'can_red'  '0.8,0.22,0.18,1'  '0.9,0.85,0.8,1'

# --- food tubs: pale plastic body with the contents painted low (reads see-through), coloured lid w/ inset + clips ---
function Tub([string]$name, [string]$lid) {
    $plastic = '0.86,0.9,0.92,1'
    $brushes = @(
        (B @{ Shape = 'Box'; Position = '0,0,15'; Size = '32,23,15'; Rounding = 5; Blend = 6; Color = $plastic; Roughness = 0.15 })
        (B @{ Shape = 'Box'; Operation = 'Colour'; Position = '0,0,9'; Size = '34,25,9'; Blend = 4; Color = '0.88,0.78,0.6,1'; Roughness = 0.3 })
        (B @{ Shape = 'Box'; Position = '0,0,31'; Size = '33.5,24.5,3.4'; Rounding = 3; Blend = 0; Color = $lid; Roughness = 0.4 })
        (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,35.4'; Size = '27,18,2'; Rounding = 2; Blend = 0.8; Color = $lid; Roughness = 0.4 })
        (B @{ Shape = 'Box'; Position = '0,25,27'; Size = '6,1.6,4.5'; MirrorY = $true; Rounding = 1.5; Blend = 0; Color = (Shade $lid 1.15); Roughness = 0.4 })
    )
    Write-SdfProp "$OutDir/$name.prefab" $name $brushes '0,0,17'
}
Tub 'tub_blue'   '0.25,0.45,0.72,1'
Tub 'tub_orange' '0.93,0.55,0.2,1'

# --- mugs: cylinder body, hollow (body colour), spline handle on +X ---
function Mug([string]$name, [string]$col) {
    $brushes = @(
        (B @{ Shape = 'Cylinder'; Position = '0,0,15'; Size = '11,11,15'; Rounding = 3; Blend = 6; Color = $col; Roughness = 0.35 })
        (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,33'; Size = '8,8,15'; Rounding = 3; Blend = 0.6; Color = $col; Roughness = 0.35 })
        (B @{ Shape = 'Spline'; Points = @('9.5,0,25,2.6', '16,0,24.5,2.6', '19,0,17.5,2.6', '16,0,10,2.6', '9.5,0,9,2.6'); Size = '1,1,1'
              Blend = 0; Color = $col; Roughness = 0.35 })
    )
    Write-SdfProp "$OutDir/$name.prefab" $name $brushes '0,0,15'
}
Mug 'mug_teal'   '0.35,0.66,0.62,1'
Mug 'mug_yellow' '0.96,0.8,0.25,1'

# --- tea towel: folded over a bar along X (pivot = the fold, panels hang down), painted stripes ---
$towel = '0.94,0.92,0.86,1'; $stripe = '0.25,0.58,0.56,1'
Write-SdfProp "$OutDir/tea_towel.prefab" 'tea_towel' @(
    (B @{ Shape = 'Cylinder'; Position = '0,0,0'; Rotation = $QAlongX; Size = '8.5,8.5,20'; Rounding = 3; Blend = 6; Color = $towel; Roughness = 0.8 })
    (B @{ Shape = 'Box'; Position = '0,6,-26'; Rotation = (Qaxis 1 0 0 -4); Size = '20,2.6,27'; Rounding = 2.5; Blend = 2; Color = $towel; Roughness = 0.8 })
    (B @{ Shape = 'Box'; Position = '0,-6,-15'; Rotation = (Qaxis 1 0 0 3); Size = '20,2.6,16'; Rounding = 2.5; Blend = 2; Color = $towel; Roughness = 0.8 })
    (B @{ Shape = 'Box'; Operation = 'Colour'; Position = '9,0,-22'; Size = '3.5,14,40'; MirrorX = $true; Blend = 0.5; Color = $stripe; Roughness = 0.8 })
) '0,0,-22'
