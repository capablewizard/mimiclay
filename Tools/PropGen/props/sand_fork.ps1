# Kids' sand fork — the spade's carved block: flat base cut, sloped top cut, tapered tines from two long
# triangular-prism wedge cuts (MirrorY), a Smooth modifier softening every cut above it, and a shallow dish
# recessed into the head. Shaft + ring shared with the spade (fork uses a fully-rounded shaft).
# v2 (2026-11-12): the user's rebuild of v1 ("shapes ok, too simple").
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/sand_fork.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
. "$PSScriptRoot/_sand_toy_handle.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/sand_fork.prefab" }

$col = '0.90233,0.44394,0.28958,1'   # orange-red: each toy in the set gets its own colour
$slab = 8.074
$QYaw90 = Qaxis 0 0 1 90             # lays an Extruded triangle's apex toward -X (the handle)

$brushes = @(
    # head block: the spade's soft rounded block, longer
    (B @{ Shape = 'Box'; Position = '28,0,7.33'; Size = '20,16.8,12.768'
          Rounding = 6.05; Blend = 6; Color = $col; Roughness = 0.4 })
    # flat base
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '28,0,-5.384'; Size = '23,20.3,8.074'
          Rounding = 0; Blend = 2.1; Color = $col; Roughness = 0.4 })
    # tine wedges: long thin triangles (base 11.3 wide past the tips, apex far back toward the handle) so the
    # gaps widen toward the tips and the tines taper to points
    (B @{ Shape = 'Extruded'; CrossSection = 'Triangle'; Operation = 'Subtract'; Position = '3.982,7.741,7'; Rotation = $QYaw90
          Size = '5.658,47.461,20'; MirrorY = $true; Rounding = 2.8; Blend = 0; Color = $col; Roughness = 0.4 })
    # Smooth modifier: raises blend/rounding of every brush ABOVE it to >= 3.7 (amount lives in Blend; gizmo pos/size unused)
    (B @{ Shape = 'Smooth'; Operation = 'Colour'; Position = '19.507,7.69,7'; Rotation = $QYaw90; Size = '5.658,39.075,20'
          MirrorY = $true; Rounding = 2.8; Blend = 3.7; Color = $col; Roughness = 0.4 })
    # sloped top: thins the head toward the tips (below the modifier: stays crisp-ish, blend 0.9)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '28,0,18.823'; Rotation = (Qaxis 0 1 0 8); Size = '23,20.3,8.074'
          Rounding = 0; Blend = 0.9; Color = $col; Roughness = 0.4 })
    # shallow dish recessed into the head top (~3.7 deep, ~1.6 border on the outer tines), tilted 5.2 deg
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = '36.64,0,13.832'; Rotation = (Qaxis 0 1 0 5.206); Size = '23,15.179,8.074'
          Rounding = 4.35; Blend = 0.9; Color = $col; Roughness = 0.4 })
)
$brushes += SandToyHandle $col -ShaftRounding 8 -ShaftHalfLength 21.907 -ShaftX -9.59

Write-SdfProp $Out 'sand_fork' $brushes '0,0,4'
