# Bowls with contents (blockout): big blue bowl heaped with fruit, yellow bowl of buns. Base at z=0.
# Bowl = bucket construction: flipped tapered cylinder + rolled rim band + hollow (body colour); contents Blend 0.
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

function BowlBrushes([string]$col, [double]$R, [double]$H) {
    @(
        (B @{ Shape = 'Cylinder'; Position = (V3 0 0 ($H / 2)); Rotation = $QFlip; Size = (V3 $R $R ($H / 2)); Taper = 0.3
              Rounding = $H * 0.4; Blend = 6; Color = $col; Roughness = 0.4 })
        (B @{ Shape = 'Cylinder'; Position = (V3 0 0 ($H - 2)); Size = (V3 ($R + 1.5) ($R + 1.5) 3); Rounding = 3; Blend = 0; Color = $col; Roughness = 0.4 })
        (B @{ Shape = 'Sphere'; Operation = 'Subtract'; Position = (V3 0 0 ($H + 1)); Size = (V3 ($R - 5) ($R - 5) ($H - 4)); Blend = 0.8; Color = $col; Roughness = 0.4 })
    )
}

# --- fruit bowl ---
$green = '0.52,0.74,0.25,1'; $red = '0.82,0.24,0.18,1'; $orange = '0.93,0.55,0.18,1'
$b = @(BowlBrushes '0.27,0.47,0.7,1' 36 26)
$fruit = @(
    @(-14, -10, 22, $green), @(6, -16, 22, $red), @(18, 2, 22, $green), @(-2, 14, 22, $orange),
    @(-18, 10, 22, $red), @(4, 0, 24, $green), @(-6, -4, 33, $red), @(10, 8, 32, $orange), @(-10, 12, 31, $green)
)
foreach ($f in $fruit) {
    $b += (B @{ Shape = 'Sphere'; Position = (V3 $f[0] $f[1] $f[2]); Size = '9.5,9.5,9'; Blend = 0; Color = $f[3]; Roughness = 0.35 })
}
Write-SdfProp "$OutDir/bowl_fruit.prefab" 'bowl_fruit' $b '0,0,18'

# --- bun bowl ---
$bun = '0.9,0.72,0.42,1'
$b = @(BowlBrushes '0.95,0.77,0.22,1' 30 20)
$buns = @(@(-12, -8, 17), @(8, -12, 17), @(14, 6, 17), @(-4, 13, 17), @(-15, 8, 17), @(2, 0, 19), @(-5, -3, 26), @(8, 4, 25))
foreach ($p in $buns) {
    $b += (B @{ Shape = 'Sphere'; Position = (V3 $p[0] $p[1] $p[2]); Size = '8.5,8,6.5'; Blend = 0; Color = $bun; Roughness = 0.6 })
}
Write-SdfProp "$OutDir/bowl_buns.prefab" 'bowl_buns' $b '0,0,14'
