# Bowls (v2 = user-tuned 2026-11-12). Base at z=0.
# Bowl construction: flipped tapered cylinder body, a flat Subtract cylinder slicing the top off, a rim band added
# back, then a tapered (flipped) cylinder Subtract for the inside — a straight sloped wall, not a sphere scoop.
# Note: the inner cut's Blend fillets against the body's buried flat top under the rim (a small inward ridge on
# the inner wall). Rim Blend ~3 smooths that seam if it shows.
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

# --- fruit bowl (now an empty bowl; the fruit went) ---
$blue = '0.27,0.47,0.7,1'
Write-SdfProp "$OutDir/bowl_fruit.prefab" 'bowl_fruit' @(
    (B @{ Shape = 'Cylinder'; Position = '0,0,16.635'; Rotation = $QFlip; Size = '36,36,16.791'; Taper = 0.3; Rounding = 10.4; Blend = 6; Color = $blue })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,43.168'; Rotation = $QFlip; Size = '50.876,36,16.791'; Rounding = 10.4; Blend = 0; Color = $blue })
    (B @{ Shape = 'Cylinder'; Position = '0,0,24'; Size = '37.5,37.5,3'; Rounding = 3; Blend = 0; Color = $blue })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,20.249'; Rotation = $QFlip; Size = '32.231,28.898,13.479'; Taper = 0.3; Rounding = 5.05; Blend = 3.3; Color = $blue })
) '0,0,18'

# --- bun bowl: same construction; darker interior; a soft filler mound under a loose pile of tilted buns ---
$ochre = '0.842,0.625,0.192,1'; $bun = '0.735,0.581,0.325,1'
$b = @(
    (B @{ Shape = 'Cylinder'; Position = '0,0,12.819'; Rotation = $QFlip; Size = '30,30,12.43'; Taper = 0.3; Rounding = 8; Blend = 6; Color = $ochre })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,24.087'; Size = '35.921,31.5,4.881'; Rounding = 3; Blend = 0; Color = $ochre })
    (B @{ Shape = 'Cylinder'; Position = '0,0,18'; Size = '29.343,31.5,3'; Rounding = 3; Blend = 0; Color = $ochre })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '0,0,17.163'; Rotation = $QFlip; Size = '28.119,30,12.43'; Taper = 0.3; Rounding = 8; Blend = 3.6; Color = '0.567,0.425,0.14,1' })
)
# buns: position, rotation (quaternion) — each one tilted a little so the pile reads as tossed in
$buns = @(
    @('-12,-8,17', '0.123,0.077,0.01,0.989'), @('8,-12.836,13.989', '0.208,0,0,0.978'), @('14,6,17', '0,0.098,0,0.995'),
    @('-4,15.469,16.08', '-0.177,0,0,0.984'), @('-15,5.357,17', '-0.047,-0.14,-0.058,0.987'), @('0.624,0.274,20.357', '0,0,0,1'),
    @('18.218,-4.874,20.99', '0.159,0.102,0.496,0.848'), @('-5,-15.289,18.593', '0.148,0,0,0.989'), @('5.512,17.242,20.237', '-0.097,-0.008,-0.084,0.992')
)
foreach ($p in $buns) {
    $b += (B @{ Shape = 'Sphere'; Position = $p[0]; Rotation = $p[1]; Size = '8.5,8,6.5'; Blend = 0; Color = $bun; Roughness = 0.6 })
}
# filler mound: big soft blend fuses the pile's underside so there are no gaps down to the bowl floor
$b += (B @{ Shape = 'Sphere'; Position = '0,0,8.439'; Size = '20,20,8'; Blend = 8.2; Color = '0.698,0.532,0.256,1'; Roughness = 0.6 })
Write-SdfProp "$OutDir/bowl_buns.prefab" 'bowl_buns' $b '0,0,14'
