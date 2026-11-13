# Seed-tray planter (grey plastic, Cols x Rows cells) — stackable: cells taper inward toward the base.
# Construction: ONE rounded box for the whole cell block, carved into tapered cells by Extruded Triangle
# Subtracts (apex up at the top of the block, base below the floor): one centred on every cell divider (a V gap
# that opens toward the bottom) and one centred on each outer wall plane (half of it in air = the wall taper).
# A flat lip slab on top and one rounded box hollow per cell.
# v2 (2026-11-12) = the user's tune: darker grey, body lifted 2.4, soft (5.3) blend on the V gaps + long walls
# (end walls stay crisp 2), thinner lip overhang + rounding 2.2, rim recess dropped, ONE bigger hollow box per cell.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/planter_tray.ps1 [-Cols 3 -Rows 2] [-Out <path>]
#   default 3x2 -> planter_tray.prefab; any other grid -> planter_tray_<C>x<R>.prefab
param([string]$Out, [int]$Cols = 3, [int]$Rows = 2)
. "$PSScriptRoot/../SdfPropKit.ps1"
$Name = if ($Cols -eq 3 -and $Rows -eq 2) { 'planter_tray' } else { "planter_tray_${Cols}x${Rows}" }
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/$Name.prefab" }

$grey   = '0.33488,0.32164,0.3084,1'
$inside = '0.2093,0.20346,0.19762,1'
function G([hashtable]$h) { $h.Color = $grey; B $h }

$Pitch = 36                           # cell top = Pitch x Pitch
$H = 30                               # cell block height
$Lift = 2.41                          # body raised off z 0 (wedges stay put, so the cell feet sit a touch lower in the cut)
$Inset = 4                            # how far each cell wall leans in by the base
$hx = $Cols * $Pitch / 2; $hy = $Rows * $Pitch / 2

# wedge: apex at z=H, base 2 below z 0; half-base chosen so the cut is exactly $Inset wide at z=0
$wHH = ($H + 2) / 2; $wZ = $H - $wHH; $wHB = $Inset * (2 * $wHH) / $H
$QWedgeY = $QAlongY                   # triangle in XZ, extruded along Y (planes x = const)
$QWedgeX = '0.5,0.5,0.5,0.5'          # triangle in YZ, extruded along X (planes y = const)
function WedgeX([double]$x, [double]$blend) {
    G @{ Shape = 'Extruded'; CrossSection = 'Triangle'; Operation = 'Subtract'; Position = (V3 $x 0 $wZ); Rotation = $QWedgeY
         Size = (V3 $wHB $wHH ($hy + 10)); MirrorX = ($x -gt 0.01); Rounding = 0; Blend = $blend }
}
function WedgeY([double]$y, [double]$blend) {
    G @{ Shape = 'Extruded'; CrossSection = 'Triangle'; Operation = 'Subtract'; Position = (V3 0 $y $wZ); Rotation = $QWedgeX
         Size = (V3 $wHB $wHH ($hx + 10)); MirrorY = ($y -gt 0.01); Rounding = 0; Blend = $blend }
}
# grid lines / cell centres on the >= 0 side (the rest come from mirroring)
function HalfLines([int]$n, [double]$h) { $r = @(); for ($k = 1; $k -lt $n; $k++) { $v = -$h + $k * $Pitch; if ($v -ge -0.01) { $r += [math]::Abs($v) } }; , $r }
function HalfCentres([int]$n, [double]$h) { $r = @(); for ($k = 0; $k -lt $n; $k++) { $v = -$h + ($k + 0.5) * $Pitch; if ($v -ge -0.01) { $r += [math]::Abs($v) } }; , $r }

$GapBlend = 5.3; $EndBlend = 2                  # soft V gaps + long walls, crisp end walls (user)
$lipT = 3.5; $lipZ = $H + 1.5; $lipOutX = 2.73; $lipOutY = 2.44   # lip slab half-thickness, centre, overhang past the cells (user)
$cellHalf = 15; $cellHH = 12; $cellZ = 30.95    # one rounded hollow box per cell (floor ~19)

$brushes = @(
    # cell block
    (G @{ Shape = 'Box'; Position = (V3 0 0 ($H / 2 + $Lift)); Size = (V3 $hx $hy ($H / 2)); Rounding = 5; Blend = 0 })
)
# V gaps between cells + tapered outer walls
foreach ($x in (HalfLines $Cols $hx)) { $brushes += , (WedgeX $x $GapBlend) }
$brushes += , (WedgeX $hx $EndBlend)            # end walls
foreach ($y in (HalfLines $Rows $hy)) { $brushes += , (WedgeY $y $GapBlend) }
$brushes += , (WedgeY $hy $GapBlend)            # long walls
# lip
$brushes += , (G @{ Shape = 'Box'; Position = (V3 0 0 $lipZ); Size = (V3 ($hx + $lipOutX) ($hy + $lipOutY) $lipT); Rounding = 2.2; Blend = 1 })
# cell hollows (MirrorX/Y fill the grid)
foreach ($x in (HalfCentres $Cols $hx)) {
    foreach ($y in (HalfCentres $Rows $hy)) {
        $brushes += , (B @{ Shape = 'Box'; Operation = 'Subtract'; Position = (V3 $x $y $cellZ); Size = (V3 $cellHalf $cellHalf $cellHH)
                            MirrorX = ($x -gt 0.01); MirrorY = ($y -gt 0.01); Rounding = 6; Blend = 0.8; Color = $inside })
    }
}

Write-SdfProp $Out $Name $brushes (V3 0 0 ($H / 2 + 3))
