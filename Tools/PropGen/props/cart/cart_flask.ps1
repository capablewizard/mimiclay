# Vacuum flask — v2 = the user's tune (2026-11-12): ~1.5x my v1, muted red, flat base disc a touch wider than the body,
# cap band NARROWER than the body, tall domed cap, thick squared loop handle on -X, groove dropped. Base at z=0.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/cart/cart_flask.ps1 [-OutDir <dir>]
param([string]$OutDir)
. "$PSScriptRoot/../../SdfPropKit.ps1"
if (-not $OutDir) { $OutDir = "$ProjectRoot/Assets/Prefabs/Props/Cart" }

$red   = '0.73953,0.36758,0.34053,1'
$metal = '0.63602,0.64794,0.68372,1'
$m = @{ Color = $metal; Metallic = 0.7834; Roughness = 0.415 }
function M([hashtable]$h) { foreach ($k in $m.Keys) { $h[$k] = $m[$k] }; B $h }

$t = 4.456   # handle tube radius
$brushes = @(
    (M @{ Shape = 'Cylinder'; Position = '0,0,4.456'; Size = '23.96,23.96,4.456'; Rounding = 0.2; Blend = 0 })
    (B @{ Shape = 'Cylinder'; Position = '0,0,37.877'; Size = '23.023,23.023,31.193'; Rounding = 5.94; Blend = 0; Color = $red; Roughness = 0.55 })
    (M @{ Shape = 'Cylinder'; Position = '0,0,72.724'; Size = '20.719,20.719,3.862'; Rounding = 1.55; Blend = 0 })
    (M @{ Shape = 'Cylinder'; Position = '0,0,78.777'; Size = '17.58,17.58,11.14'; Rounding = 10.4; Blend = 0 })
    (M @{ Shape = 'Spline'; Points = @("-19.31,0,59.415,$t", "-31.193,0,59.415,$t", "-37.134,0,51.473,$t", "-37.134,0,27.251,$t", "-31.193,0,19.31,$t", "-19.31,0,19.31,$t"); Size = '1,1,1'; Blend = 0 })
)

Write-SdfProp "$OutDir/flask.prefab" 'flask' $brushes '0,0,45'
