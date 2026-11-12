# Kids' toy dustpan — the spade's carved block made wide and flat: flat base, steep sloped top cut so the side
# walls run from a tall back wall down into the open front lip, an under-chamfer thinning the lip, a Smooth
# modifier over the carved pan, then a short round-ended handle with a triangular gusset into the back wall.
# Lies on its floor, pan toward +X, handle toward -X (same frame as the spade/fork).
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/sand_dustpan.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
. "$PSScriptRoot/_sand_toy_handle.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/sand_dustpan.prefab" }

$col  = RGB255 108 152 210   # sampled from the reference
$slab = 8.074

$panX = 25; $panL = 17; $panW = 20    # pan block centre x, half-length (x 8..42), half-width
$baseZ = 2.69                         # flat base (spade value)
$wall  = 3.4                          # side/back wall thickness
$floorZ = 5                           # hollow floor height (floor ~2.3 thick)

# side-wall slope: wall top ~16 at the back (x=8) falling to ~4 at the front lip (x=42)
$slope = 19.4; $slopeMidZ = 10
$slopeC = $slopeMidZ + $slab / [math]::Cos([math]::PI * $slope / 180)

# lip under-chamfer: plane through (36, baseZ) rising 12 deg toward the front; slab centre = face point - n*slab
$ch = -12; $chR = [math]::PI * $ch / 180
$chPx = 46; $chPz = $baseZ + ($chPx - 36) * [math]::Tan(-$chR)
$chCx = $chPx - $slab * [math]::Sin($chR); $chCz = $chPz - $slab * [math]::Cos($chR)

# handle (shared shaft, fully rounded, shorter, no ring) + gusset following the shaft tilt
$shaftTilt = 8.852
$gussetX = -2
$gussetZ = 13.785 - ($gussetX + 9.65) * [math]::Tan([math]::PI * $shaftTilt / 180)
$gussetRot = Qmul (Qaxis 0 1 0 $shaftTilt) (Qaxis 0 0 1 90)

$brushes = @(
    # pan block
    (B @{ Shape = 'Box'; Position = (V3 $panX 0 7.33); Size = (V3 $panL $panW 12.768)
          Rounding = 6.05; Blend = 6; Color = $col; Roughness = 0.4 })
    # flat base
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 $panX 0 ($baseZ - $slab)); Size = (V3 ($panL + 3) ($panW + 3.5) $slab)
          Rounding = 0; Blend = 2.1; Color = $col; Roughness = 0.4 })
    # pan hollow: open past the front, body colour (open scoop), crisp
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 (8 + $wall + 20) 0 ($floorZ + 17.744)); Size = (V3 20 ($panW - $wall) 17.744)
          Rounding = 3.75; Blend = 0; Color = $col; Roughness = 0.4 })
    # sloped side walls
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 $panX 0 $slopeC); Rotation = (Qaxis 0 1 0 $slope); Size = (V3 ($panL + 6) ($panW + 3.5) $slab)
          Rounding = 0; Blend = 0.9; Color = $col; Roughness = 0.4 })
    # soften the carved pan (everything above)
    (B @{ Shape = 'Smooth'; Operation = 'Colour'; Position = (V3 $panX 0 10); Size = '6,6,6'; Blend = 2.5; Color = $col; Roughness = 0.4 })
    # thin the front lip from underneath (crisp, below the modifier)
    (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 $chCx 0 $chCz); Rotation = (Qaxis 0 1 0 $ch); Size = (V3 13 ($panW + 3.5) $slab)
          Rounding = 0; Blend = 0.6; Color = $col; Roughness = 0.4 })
    # gusset: flat triangle, base along the back wall, apex running up the handle
    (B @{ Shape = 'Extruded'; CrossSection = 'Triangle'; Position = (V3 $gussetX 0 $gussetZ); Rotation = $gussetRot
          Size = '11,9,3.3'; Rounding = 1.6; Blend = 1; Color = $col; Roughness = 0.4 })
)
$brushes += SandToyHandle $col -ShaftRounding 8 -ShaftHalfLength 17 -ShaftX -8 -NoRing   # end stops inside the back wall (x~9)

Write-SdfProp $Out 'sand_dustpan' $brushes '0,0,4'
