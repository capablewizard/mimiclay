# Outdoor wall lamp — dark grey dome shade (carved ellipsoid: flat base cut + thick hollow) with a fully-rounded
# rim band, capsule finial, glowing amber capsule globe tucked up into the shade, round wall rose + stubby arm.
# Mounting: the wall is the plane x=0, the lamp sticks out along +X.
# v2 (2026-11-12): the user's tune — round wall rose (not a pill plate), neutral grey, hollow in body colour,
# rounder rim, shorter finial, globe = saturated amber capsule, screw dropped, shade pulled ~4.7 toward the wall.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/wall_lamp.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/wall_lamp.prefab" }

$grey = '0.22319,0.23717,0.26512,1'   # neutral dark grey
$glow = '1,0.58039,0,1'               # light sources stay saturated

$sx = 37.339; $sz = 40                # shade centre
$cutZ = 34

$brushes = @(
    # wall rose: round disc flat on the wall
    (B @{ Shape = 'Cylinder'; Position = '0.123,0,40.309'; Rotation = $QAlongX; Size = '19.483,19.483,4.629'; Rounding = 5; Blend = 6; Color = $grey; Roughness = 0.5 })
    # arm
    (B @{ Shape = 'Cylinder'; Position = '8.339,0,45'; Rotation = $QAlongX; Size = '8,8,7'; Rounding = 3; Blend = 0; Color = $grey; Roughness = 0.5 })
    # shade dome
    (B @{ Shape = 'Sphere'; Position = (V3 $sx 0 $sz); Size = '30,30,24'; Blend = 0; Color = $grey; Roughness = 0.5 })
    # flat underside cut
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 $sx 0 24); Size = '34,34,10'; Rounding = 0; Blend = 1.5; Color = $grey; Roughness = 0.5 })
    # rim band, fully rounded
    (B @{ Shape = 'Cylinder'; Position = (V3 $sx 0 36.5); Size = '33.34,33.34,3.5'; Rounding = 5.6; Blend = 0; Color = $grey; Roughness = 0.5 })
    # hollow underside (body colour)
    (B @{ Shape = 'Sphere'; Operation = 'Subtract'; Position = (V3 $sx 0 $cutZ); Size = '24.5,24.5,18'; Blend = 0.8; Color = $grey; Roughness = 0.5 })
    # finial: neck + squat capsule cap
    (B @{ Shape = 'Cylinder'; Position = (V3 $sx 0 64); Size = '6,6,4'; Rounding = 1.5; Blend = 0; Color = $grey; Roughness = 0.5 })
    (B @{ Shape = 'Cylinder'; Position = (V3 $sx 0 72.271); Size = '10,10,5.805'; Rounding = 7; Blend = 0; Color = $grey; Roughness = 0.5 })
    # globe: capsule (cylinder, rounding clamps to the radius) reaching up inside the shade
    (B @{ Shape = 'Cylinder'; Position = (V3 $sx 0 29.223); Size = '21,21,25.616'; Rounding = 100; Blend = 0; Color = $glow; Roughness = 0.5 })
)

Write-SdfProp $Out 'wall_lamp' $brushes '35,0,38'
