# Wheelbarrow (empty) — v2 (2026-11-12): fitted to the user's reference mesh "garden wheelbarrow 3d model"
# (Blender_Garden in DevPropHunt). Measured in the barrow's own frame: X toward the wheel, origin = the mesh origin
# (ground z=0). Place the prefab at the mesh's position with yaw = mesh-frame yaw - 90 (-21.5 in DevPropHunt).
#   tray  = big rounded tub (rounding 30) sliced flat at the rim (top rounding hidden), front/back walls flared
#           14/17 deg by soft (Blend 10) Subtract planes, upright sides,
#           flat lip at z~136, thin-walled hollow = central box + slabs on the flared walls (body colour inside);
#   wheel = lawnmowerWheel construction scaled to the reference (r 40, wide tyre, yellow Cutout hub recess both sides);
#   frame = ONE spline per side (MirrorY, Curvature 1): axle -> under the tray front -> U leg to the ground ->
#           up behind the back corners -> flat handle at rim height; fat red grips.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/wheelbarrow.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/wheelbarrow.prefab" }

$teal  = '0.27,0.55,0.56,1'
$tyre  = '0.279,0.279,0.279,1'
$hub   = '0.813,0.677,0.382,1'
$frame = '0.42,0.43,0.45,1'
$grip  = '0.78,0.3,0.25,1'
function Tl([hashtable]$h) { $h.Color = $teal; $h.Roughness = 0.5; B $h }
function Fr([hashtable]$h) { $h.Color = $frame; $h.Roughness = 0.5; B $h }

$brushes = @(
    # --- tray: a big rounded tub (reference: near-upright walls, large bottom radius), floor z 40 ---
    # body runs ABOVE the rim and is sliced flat at z 136, so the top rounding never pulls the walls in under the lip
    (Tl @{ Shape = 'Box'; Position = '-1,0,100'; Size = '66,50,60'; Rounding = 30; Blend = 0 })
    (Tl @{ Shape = 'Box'; Operation = 'Subtract'; Position = '0,0,186'; Size = '100,80,50'; Rounding = 0; Blend = 0 })
    # flare: front wall leans forward 14 deg (plane (45,40)-(68,132)), back wall leans back 17 deg ((-40,40)-(-68,132));
    # big blend keeps the bottom edges round like the reference bowl
    (Tl @{ Shape = 'Box'; Operation = 'Subtract'; Position = '95.3,0,76.3'; Rotation = (Qaxis 0 1 0 14); Size = '40,70,80'; Rounding = 0; Blend = 10 })
    (Tl @{ Shape = 'Box'; Operation = 'Subtract'; Position = '-92.3,0,74.4'; Rotation = (Qaxis 0 1 0 -16.9); Size = '40,70,80'; Rounding = 0; Blend = 10 })
    # flat lip (rim footprint x -70..68, |y| 55)
    (Tl @{ Shape = 'Box'; Position = '-1,0,136'; Size = '70,55,5'; Rounding = 5; Blend = 1 })
    # hollow (~5 walls, ~8 on the flared front/back so the top of the back wall can't break through; floor z 46): central box + slabs hugging the leaning front/back walls
    (Tl @{ Shape = 'Box'; Operation = 'Subtract'; Position = '2.5,0,106'; Size = '38.5,45,60'; Rounding = 18; Blend = 1 })
    (Tl @{ Shape = 'Box'; Operation = 'Subtract'; Position = '30.9,0,100.1'; Rotation = (Qaxis 0 1 0 14); Size = '20,45,50'; Rounding = 10; Blend = 1 })
    (Tl @{ Shape = 'Box'; Operation = 'Subtract'; Position = '-28.6,0,100.6'; Rotation = (Qaxis 0 1 0 -16.9); Size = '20,45,50'; Rounding = 10; Blend = 1 })

    # --- wheel ---
    (B @{ Shape = 'Cylinder'; Position = '102,0,40'; Rotation = $QAlongY; Size = '40,40,18'; Rounding = 6; Blend = 0; Color = $tyre; Roughness = 0.6 })
    (B @{ Shape = 'Cylinder'; Operation = 'Cutout'; Position = '102,18,40'; Rotation = $QAlongY; Size = '24,24,12'; MirrorY = $true
          Rounding = 5; Blend = 7; Color = $hub; Roughness = 0.5 })
    (Fr @{ Shape = 'Cylinder'; Position = '102,0,40'; Rotation = $QAlongY; Size = '5,5,32'; Rounding = 2.5; Blend = 0 })

    # --- frame: one tube per side ---
    (Fr @{ Shape = 'Spline'; Size = '1,1,1'; MirrorY = $true; Blend = 1.3; Curvature = 1
           Points = @('102,30,40,6', '72,33,40,6', '42,34,30,6', '24,35,11,6', '6,35,5,6', '-12,35,9,6', '-28,35,28,6',
                      '-42,37,42,6', '-54,42,64,6.5', '-65,49,96,7', '-73,55,117,7.5', '-84,57,127,8', '-98,57,129,8') })
    # grips
    (B @{ Shape = 'Spline'; Size = '1,1,1'; MirrorY = $true; Blend = 0; Color = $grip; Roughness = 0.5
          Points = @('-94,57,129,10', '-136,58,129,10') })
)

Write-SdfProp $Out 'wheelbarrow' $brushes '0,0,70'
