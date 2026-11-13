# Clay watering can — v2 (2026-11-12) matched to the user's concept sheet (teal, squat, open top).
# Sheet: ~150 tall (to the handle top) x ~220 long; built at ~0.55 scale. Base at z=0, spout toward +X.
#   body   = upright cylinder tapering toward the top (wider base), soft rounded rims, OPEN top: a deep tapered hollow;
#   handle = one thick spline "?" — rises from the front of the rim, arches over the opening, runs down the BACK
#            wall and sinks into it at mid height;
#   spout  = thick near-parallel tube from low on the front, raked up ~43 deg, welded with a big blend;
#   rose   = a big flat disc square to the spout with 5 holes (4 round + centre).
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/watering_can.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/watering_can.prefab" }

$teal   = '0.29,0.6,0.56,1'
$inside = Hollow $teal
function Tl([hashtable]$h) { $h.Color = $teal; B $h }

$R = 33; $H = 60; $Taper = 0.18                  # body: base radius R, top R*(1-Taper), z 0..H

# spout axis: from inside the body low on the front, up to the rose
$s0 = @(14, 0, 18); $s1 = @(64, 0, 64); $spR = 6.5
$dx = $s1[0] - $s0[0]; $dz = $s1[2] - $s0[2]; $L = [math]::Sqrt($dx * $dx + $dz * $dz); $ux = $dx / $L; $uz = $dz / $L
$QSpout = Qaxis 0 1 0 ([math]::Atan2($ux, $uz) * 180 / [math]::PI)   # rotate +Z onto the spout axis
$roseR = 14; $roseH = 3.6
function OnAxis([double]$t, [double]$side, [double]$up) {
    # point t along the axis from s1, offset in the rose plane: side = +Y, up = the in-plane direction (-uz, 0, ux)
    V3 ($s1[0] + $ux * $t - $uz * $up) $side ($s1[2] + $uz * $t + $ux * $up)
}

$brushes = @(
    # body
    (Tl @{ Shape = 'Cylinder'; Position = (V3 0 0 ($H / 2)); Size = (V3 $R $R ($H / 2)); Taper = $Taper; Rounding = 7; Blend = 0 })
    # spout (big weld into the body) + rose
    (Tl @{ Shape = 'Cylinder'; Position = (V3 (($s0[0] + $s1[0]) / 2) 0 (($s0[2] + $s1[2]) / 2)); Rotation = $QSpout
           Size = (V3 $spR $spR ($L / 2)); Taper = 0.08; Rounding = 3; Blend = 5 })
    (Tl @{ Shape = 'Cylinder'; Position = (OnAxis ($roseH - 1) 0 0); Rotation = $QSpout; Size = (V3 $roseR $roseR $roseH)
           Rounding = 3; Blend = 2.5 })
)
# rose holes: centre + 4 on a diamond
$holes = @(@(0, 0), @(7.2, 0), @(-7.2, 0), @(0, 7.2), @(0, -7.2))
foreach ($h in $holes) {
    $brushes += , (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = (OnAxis (2 * $roseH - 1) $h[0] $h[1]); Rotation = $QSpout
                        Size = '2.4,2.4,2.6'; Rounding = 0.8; Blend = 0.6; Color = $inside })
}
$brushes += @(
    # open top: deep hollow following the body taper (wall ~5, floor at z 26)
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = (V3 0 0 48); Size = '25.5,25.5,22'; Taper = 0.13
          Rounding = 5; Blend = 1.2; Color = $inside })
    # handle: front of the rim -> over the opening -> down the back wall, sunk in at mid height
    (Tl @{ Shape = 'Spline'; Size = '1,1,1'; Blend = 2.5; Curvature = 1
           Points = @((P4 24.5 0 57 5.2), (P4 20 0 73 5.2), (P4 6 0 84 5.2), (P4 -13 0 84 5.2), (P4 -29 0 73 5.2),
                      (P4 -37 0 55 5.2), (P4 -35 0 40 5.2), (P4 -30 0 31 5.2)) })
)

Write-SdfProp $Out 'watering_can' $brushes '10,0,40'
