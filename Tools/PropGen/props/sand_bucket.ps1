# Kids' sand bucket — blue tapered pail, rim band, yellow swing handle anchored at the rim.
# v2 (2026-11-11): numbers ported from the user's hand-tuned prefab (scaled up ~1.5x, rounder body,
# flatter/taller rim band, softer hollow lip, handle pivots at the rim and stands further off the body).
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/sand_bucket.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/sand_bucket.prefab" }

$blue   = RGB255 72 138 192
$yellow = RGB255 245 196 50
$inside = "0.22715,0.35803,0.46512,1"   # user-tuned; Hollow $blue (0.68, 0.3) gives ~0.234,0.357,0.458

$R      = 25.72   # body radius at the top
$halfH  = 22.7    # body half-height
$bodyZ  = 12.92   # body centre (base ~ -9.8)
$Taper  = 0.2     # bottom radius = R*(1-Taper)

$rimR   = 27.69   # rim band radius (~2 proud of the body)
$rimH   = 4.0     # rim band half-height
$rimZ   = 34.63   # rim centre: band top sits ~3 above the body top

$pivotZ  = 33.76  # handle pivot height (at the rim)
$handleW = 30.09  # handle half-span along Y (~0.17R outside the body)
$handleD = 34.35  # reach along the swing (~1.14 x span)
$swing   = 129    # degrees from straight-up toward -X
$handleT = 2.17   # tube radius

$s = [math]::Sin([math]::PI * $swing / 180); $c = [math]::Cos([math]::PI * $swing / 180)
$handlePts = ArcPoints @(-0.76, 0, $pivotZ) @(0, 1, 0) @((-$s), 0, $c) $handleW $handleD 0 180 12 $handleT

$brushes = @(
    # body: flipped tapered cylinder so it widens upward; rounding ~0.26R
    (B @{ Shape = 'Cylinder'; Position = (V3 0 0 $bodyZ); Rotation = $QFlip; Size = (V3 $R $R $halfH)
          Taper = $Taper; Rounding = 6.75; Blend = 6.9; Color = $blue; Roughness = 0.4 })
    # rim band: flat face, soft edges (rounding ~0.3 x half-height, not a full bead)
    (B @{ Shape = 'Cylinder'; Position = (V3 0 0 $rimZ); Size = (V3 $rimR $rimR $rimH)
          Rounding = 1.27; Blend = 1.38; Color = $blue; Roughness = 0.4 })
    # lug pair (MirrorY): small, sits mostly inside the wall
    (B @{ Shape = 'Sphere'; Position = (V3 0 18.95 33.33); Size = '3.99,2.35,3.99'; MirrorY = $true
          Blend = 0; Rounding = 0.86; Color = $yellow; Roughness = 0.4 })
    # hollow: ~4.2 wall, ~6 floor, softer lip (blend 2)
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = (V3 0 0 18.98); Rotation = $QFlip; Size = (V3 21.49 21.49 $halfH)
          Taper = 0.22; Rounding = 3.46; Blend = 2.07; Color = $inside; Roughness = 0.5 })
    # handle: crisp (blend 0)
    (B @{ Shape = 'Spline'; Points = $handlePts; Size = '1,1,1'; Blend = 0; Rounding = 0.86; Color = $yellow; Roughness = 0.4 })
)

Write-SdfProp $Out 'sand_bucket' $brushes '0,0,16'
