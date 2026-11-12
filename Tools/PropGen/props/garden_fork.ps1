# Full-size garden (digging) fork — v2 = the user's rework (2026-11-12) of my sand-fork-mapped v1:
#   head = a FLAT untilted rounded slab (rounding 16), sliced top+bottom by one MirrorZ cut; tines from the sand
#   fork's long triangle wedges (MirrorY) carrying their own blend/rounding (Smooth modifier dropped), plus a
#   rounded slot up the middle from the tip; top cut + dish dropped; shorter neck.
#   Handle = GardenSpade.prefab's (sliced tapered-ellipsoid neck, wood capsule, green sleeve, flat green D loop).
# Lies along X, handle toward -X, shaft axis at z=0.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/garden_fork.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/garden_fork.prefab" }

$metal = @{ Color = '0.65581,0.60691,0.54295,1'; Roughness = 0.55; Metallic = 0.85 }
$wood  = '0.71163,0.52373,0.27803,1'
$green = '0.15003,0.33953,0.24306,1'
function Mt([hashtable]$h) { foreach ($k in $metal.Keys) { $h[$k] = $metal[$k] }; B $h }
$QAlongNegX = '0,-0.707106411,0,0.70710659'   # Cylinder axis along X (shaft, sleeve)

$brushes = @(
    # head slab
    (Mt @{ Shape = 'Box'; Position = '56.187,0,-0.634'; Size = '34.86,29.167,15.612'; Rounding = 16; Blend = 10.296 })
    # flat top + bottom in one cut (MirrorZ)
    (Mt @{ Shape = 'Box'; Operation = 'Subtract'; Position = '66.468,0,-16.779'; Size = '46.575,40.217,13.855'; MirrorZ = $true; Rounding = 0; Blend = 1.5 })
    # tine wedges: long thin triangles, apex toward the handle (MirrorY)
    (Mt @{ Shape = 'Extruded'; CrossSection = 'Triangle'; Operation = 'Subtract'; Position = '14.736,18.732,-5.015'
           Rotation = '-0.0789515,-0.0802833,0.6967102,0.7084614'; Size = '5.60922,95.71077,34.97369'; MirrorY = $true; Rounding = 3.7; Blend = 2.2 })
    # centre slot from the tip
    (Mt @{ Shape = 'Box'; Operation = 'Subtract'; Position = '93.034,0,6.459'; Rotation = '-0.0796201,-0.0796202,0.7026101,0.7026101'
           Size = '5.58409,52.47332,34.102'; Rounding = 8; Blend = 2.2 })
    # handle (GardenSpade's; neck shortened)
    (Mt @{ Shape = 'Sphere'; Position = '5.5675,0,0'; Rotation = '-0.707106531,0,0.70710665,0'; Size = '7.40971,7.78056,34.05006'
           Taper = 1; Slice = 0.41730767; SlicePlaneN = 0.16538465; Rounding = 0.9; Blend = 0.2 })
    (B @{ Shape = 'Cylinder'; Position = '-38.49339,0,0'; Rotation = $QAlongNegX; Size = '5.74374,6.61647,40.31084'
          Rounding = 5.834875; Blend = 0.3; Color = $wood; Roughness = 0.4 })
    (B @{ Shape = 'Cylinder'; Position = '-84.91016,0,0'; Rotation = $QAlongNegX; Size = '7.26679,6.61647,10.53431'
          Rounding = 5.45; Blend = 0.3; Color = $green; Roughness = 0.4 })
    (B @{ Shape = 'Spline'; Size = '1,1,1'; SplineClosed = $true; Blend = 0; Color = $green; Roughness = 0.4
          Points = @('-90.92662,0,0,4.44071', '-101.77616,14.6601,0,4.44071', '-117.27531,16.91553,0,4.44071', '-121.92514,9.02161,0,4.44071',
                     '-121.92506,-9.02161,0,4.44071', '-117.27535,-16.91553,0,4.44071', '-101.77615,-14.6601,0,4.44071') })
)

Write-SdfProp $Out 'garden_fork' $brushes '0,0,0'
