# Big yellow bucket — the sand bucket's construction at ~1.6x (base at z=0): flipped tapered body, flat rim band,
# thin-walled hollow, shiny metal bail handle pivoting on a bolt each side of the rim band, hanging forward and
# down so it RESTS on the front wall (swing found numerically: ~139 deg touches with ~0.5 clearance).
# v2 (2026-11-12): the user's tune — lugs dropped, rim band flat (rounding 0.55), hollow much wider (thin wall),
# handle pivot raised to the rim, thinner tube, light metal (Metallic 1). Then: handle swung to rest on the body,
# metal bolts added where it pivots.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/big_bucket.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/big_bucket.prefab" }

$yellow = '0.47111,0.59779,0.85116,1'   # (name kept) user recoloured it blue
$inside = '0.25231,0.3372,0.50698,1'
$metal  = @{ Color = '0.69227,0.71303,0.74419,1'; Metallic = 1; Roughness = 0.6 }
function Mt([hashtable]$h) { foreach ($k in $metal.Keys) { $h[$k] = $metal[$k] }; B $h }

$R = 41.2; $halfH = 36.3; $Taper = 0.24          # body: top radius R, bottom R*(1-Taper), z 0..72.6
$rimR = 44.8; $rimH = 5.5; $rimZ = 71.9
$pivotZ = 71.96; $W = 49.425; $Reach = 58; $swing = 139; $T = 2.4157   # handle: half-span, reach, deg from up toward -X
$boltY = 46.435

$s = [math]::Sin([math]::PI * $swing / 180); $c = [math]::Cos([math]::PI * $swing / 180)
$handlePts = ArcPoints @(0, 0, $pivotZ) @(0, 1, 0) @((-$s), 0, $c) $W $Reach 0 180 12 $T

$brushes = @(
    (B @{ Shape = 'Cylinder'; Position = (V3 0 0 $halfH); Rotation = $QFlip; Size = (V3 $R $R $halfH)
          Taper = $Taper; Rounding = 6.25; Blend = 0; Color = $yellow; Roughness = 0.5 })
    (B @{ Shape = 'Cylinder'; Position = (V3 0 0 $rimZ); Size = (V3 $rimR $rimR $rimH)
          Rounding = 0.55; Blend = 1.5; Color = $yellow; Roughness = 0.5 })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,46'; Rotation = $QFlip; Size = (V3 39.992 39.992 $halfH)
          Taper = 0.3907; Rounding = 5.5; Blend = 2.5; Color = $inside; Roughness = 0.5 })
    # bolts (MirrorY): flat round head on the rim band outside, nut on the INSIDE of the wall (user); handle end outside the head
    (Mt @{ Shape = 'Cylinder'; Position = (V3 0 $boltY $pivotZ); Rotation = $QAlongY; Size = '5.32256,5.32256,1.83211'; MirrorY = $true
           Rounding = 1.3; Blend = 0.4 })
    (Mt @{ Shape = 'Sphere'; Position = (V3 0 36.5316 $pivotZ); Size = '4.03083,2.32548,4.03083'; MirrorY = $true; Rounding = 0.75; Blend = 0 })
    (Mt @{ Shape = 'Spline'; Points = $handlePts; Size = '1,1,1'; Blend = 0; Rounding = 1.4 })
)

Write-SdfProp $Out 'big_bucket' $brushes '0,0,38'
