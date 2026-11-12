# Kids' broom — long capsule wooden handle into a rounded head bar, a row of chunky pill bristles splayed slightly
# outward at the tips. Stands upright (handle +Z, head width along X); lean it in the scene. Scale matches sand_dustpan.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/broom.ps1 [-Out <path>]
param([string]$Out)
. "$PSScriptRoot/../SdfPropKit.ps1"
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/broom.prefab" }

$wood    = '0.7,0.5,0.34,1'
$head    = '0.85,0.64,0.32,1'
$bristle = '0.89,0.72,0.42,1'

$brushes = @(
    # handle: capsule (huge rounding) sitting in the head
    (B @{ Shape = 'Cylinder'; Position = '0,0,170'; Size = '7.5,7.5,125'; Rounding = 100; Blend = 0; Color = $wood; Roughness = 0.6 })
    # socket collar where the handle enters the head
    (B @{ Shape = 'Cylinder'; Position = '0,0,52'; Size = '10.5,10.5,5'; Rounding = 3.5; Blend = 0; Color = $head; Roughness = 0.45 })
    # head bar: rounded block
    (B @{ Shape = 'Box'; Position = '0,0,41'; Size = '38,11.5,9.5'; Rounding = 9; Blend = 0; Color = $head; Roughness = 0.45 })
)
# bristles: 8 pills, tips splayed outward
$n = 8; $sp = 8.6
for ($i = 0; $i -lt $n; $i++) {
    $x = ($i - ($n - 1) / 2) * $sp
    $tip = $x * 0.06                               # tips drift outward
    $brushes += (B @{ Shape = 'Box'; Position = (V3 ($x + $tip / 2) 0 18); Rotation = (Qaxis 0 1 0 (-$x * 0.12))
                      Size = '4.5,9,18'; Rounding = 4.4; Blend = 0; Color = $bristle; Roughness = 0.6 })
}

Write-SdfProp $Out 'broom' $brushes '0,0,100'
