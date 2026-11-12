# Squeeze bottles (blockout): ketchup (red, beige cap), mustard (all yellow), white bottle. Base at z=0.
# Pillowy body (big rounding = shoulders), collar cap, tapered nozzle. Blend 0 between parts.
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

function Bottle([string]$name, [string]$body, [string]$cap, [string]$nozzle, [double]$R, [double]$H, [double]$nozzleR) {
    $top = $H
    $brushes = @(
        (B @{ Shape = 'Cylinder'; Position = (V3 0 0 ($H / 2)); Size = (V3 $R $R ($H / 2)); Rounding = $R * 0.5; Blend = 6; Color = $body; Roughness = 0.35 })
        # collar
        (B @{ Shape = 'Cylinder'; Position = (V3 0 0 ($top + 2)); Size = (V3 ($R * 0.68) ($R * 0.68) 4.5); Rounding = 2; Blend = 0; Color = $cap; Roughness = 0.45 })
        # nozzle (cone)
        (B @{ Shape = 'Cylinder'; Position = (V3 0 0 ($top + 12)); Size = (V3 $nozzleR $nozzleR 6.5); Taper = 0.65; Rounding = 1.5; Blend = 0; Color = $nozzle; Roughness = 0.45 })
    )
    Write-SdfProp "$OutDir/$name.prefab" $name $brushes (V3 0 0 ($H / 2))
}

Bottle 'bottle_ketchup' '0.78,0.2,0.15,1'  '0.92,0.84,0.68,1' '0.92,0.84,0.68,1' 11 50 5
Bottle 'bottle_mustard' '0.95,0.76,0.22,1' '0.97,0.82,0.3,1'  '0.97,0.82,0.3,1'  11 44 4.5
Bottle 'bottle_white'   '0.92,0.91,0.87,1' '0.95,0.94,0.9,1'  '0.95,0.94,0.9,1'  10 40 5.5
