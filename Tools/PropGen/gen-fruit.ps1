# Editable SDF fruit, using the finished mug's complete renderer/collision stack.
# Run from any directory. Stable GUIDs allow regeneration without reference churn.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out = Join-Path $root 'Assets/Prefabs/Props/Food'
[IO.Directory]::CreateDirectory($out) | Out-Null
$template = [IO.File]::ReadAllText((Join-Path $root 'Assets/Prefabs/Props/Kitchen/mug.prefab'))
$inv = [Globalization.CultureInfo]::InvariantCulture
function V($a) { return (($a | ForEach-Object { ([double]$_).ToString('0.######', $inv) }) -join ',') }
function Q($axis, [double]$degrees) {
    $t = $degrees * [Math]::PI / 360
    return V @(($axis[0]*[Math]::Sin($t)), ($axis[1]*[Math]::Sin($t)), ($axis[2]*[Math]::Sin($t)), ([Math]::Cos($t)))
}
function B($pos, $size, $col, $blend=0, $op='Add', $rot='0,0,0,1') {
    return [ordered]@{ Shape='Sphere'; Operation=$op; CrossSection='Triangle'; Text='clay'; Font='Super Joyful'; Enabled=$true; Position=(V $pos); Rotation=$rot; Size=(V $size); Points=$null; Curvature=1; SplineClosed=$false; SplinePerPointRadius=$false; Slice=0; Blend=$blend; Rounding=0.75; Color=$col; Metallic=0; Roughness=0.5; MirrorX=$false; MirrorY=$false; MirrorZ=$false }
}
function S($points, $col, $blend=0.5) {
    $b=B @(0,0,0) @(1,1,1) $col $blend
    $b.Shape='Spline'; $b.Points=@($points | ForEach-Object { V $_ }); $b.SplinePerPointRadius=$true
    return $b
}
$stem='0.29,0.17,0.075,1'; $leaf='0.29,0.43,0.105,1'
function Leaf($pos, $angle=0) { return B $pos @(6,2.8,1.5) $leaf 0.65 'Add' (Q @(0,0,1) $angle) }
function SaveFruit($name, $brushes) {
    $md5=[Security.Cryptography.MD5]::Create()
    try {
        $json=[regex]::Replace($template, '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', {
            param($m)
            ([Guid]::new($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("fruit/${name}/$($m.Value)")))).ToString()
        })
    } finally { $md5.Dispose() }
    # Windows PowerShell 5 treats __type as serializer metadata and drops it.
    $p=$json.Replace('"__type"', '"componentTypePreserved"') | ConvertFrom-Json
    $p.RootObject.Name=$name
    $p.RootObject.Components[0].Brushes=@($brushes)
    $p.RootObject.Components[0].BakedMesh=$null
    $dest=Join-Path $out "$name.prefab"
    $serialized=($p | ConvertTo-Json -Depth 50).Replace('"componentTypePreserved"', '"__type"')
    [IO.File]::WriteAllText($dest, $serialized, [Text.UTF8Encoding]::new($false))
    Write-Output "$name : $($brushes.Count) brushes"
}

$red='0.68,0.095,0.065,1'
SaveFruit 'apple' @(
    (B @(0,0,13.5) @(14,13,13.5) $red)
    (B @(-4,0.7,18) @(10.3,11.7,9.7) $red 4)
    (B @(4.2,-0.4,18.2) @(10.5,11.4,9.4) $red 4)
    (B @(0,0,28.8) @(4.2,4.2,3.8) $red 1.3 'Subtract')
    (S @(@(0,0,24,1.5),@(0.3,0,28.5,1.3),@(1.5,0.5,32,1.1)) $stem 0.6)
    (Leaf @(5,0.5,29.2) 18)
)
$pear='0.64,0.66,0.16,1'
SaveFruit 'pear' @(
    (B @(0,0,12) @(13,12,12) $pear)
    (B @(-0.8,0,20) @(10,9.8,12) $pear 6)
    (B @(-2,0.4,28) @(6.6,6.7,8.7) $pear 5)
    (B @(-2,0.4,37.8) @(2.7,2.7,2.6) $pear 0.8 'Subtract')
    (S @(@(-2,0.4,34,1.4),@(-2.8,0.5,38,1.3),@(-1,0.7,41,1)) $stem)
    (Leaf @(2.7,0.8,37.7) -15)
)
$orange='0.91,0.37,0.075,1'
SaveFruit 'orange' @(
    (B @(0,0,13.2) @(14,13.6,13.2) $orange)
    (B @(0.7,-0.5,26.7) @(3.1,3,1.7) $orange 0.7 'Subtract')
    (B @(0.7,-0.5,25.1) @(2,2,0.7) '0.38,0.38,0.1,1' 0.3)
    (S @(@(0.7,-0.5,25.1,0.9),@(1,-0.3,27.1,0.7)) $stem 0.2)
    (Leaf @(5,0,26.4) 20)
)
$yellow='0.92,0.73,0.115,1'
SaveFruit 'lemon' @(
    (B @(0,0,10) @(15,10.3,10) $yellow)
    (B @(-13,0,10.3) @(5.2,4.8,4.5) $yellow 3)
    (B @(13.2,0.3,10) @(4.8,4,4) $yellow 3)
    (B @(-17.6,0,10.3) @(0.7,1.5,1.5) '0.52,0.42,0.12,1' 0.3)
)
SaveFruit 'banana' @(
    (S @(@(-20,0,18,2.1),@(-16,0,10,4.6),@(-7,0,5.8,5.6),@(4,0.3,6.7,5.8),@(14,0.6,12.3,4.5),@(19,0.7,21,2.4)) $yellow 0)
    (S @(@(18.5,0.7,20,2.2),@(20,0.7,24,1.8),@(19.8,0.8,26,1.6)) '0.44,0.43,0.13,1' 0.7)
    (B @(19.8,0.8,26) @(1.65,1.65,0.7) $stem 0.2)
    (B @(-20.3,0,18.7) @(1.9,1.8,1.4) $stem 0.35)
)
$grape='0.32,0.16,0.40,1'; $grape2='0.37,0.19,0.44,1'
$grapes=@()
foreach($p in @(@(0,0,5.5),@(-4.5,0,13),@(4.8,0.4,13.7),@(0,6,17.4),@(-8,-0.5,22),@(0,-4,22),@(8,0,22.8),@(-4.5,5,26),@(4.7,5,27))) {
    $c=$grape; if($grapes.Count % 3 -eq 1){$c=$grape2}
    $grapes+=B $p @(5.5,5.3,5.6) $c 0.45
}
$grapes+=S @(@(0,2,27,1.6),@(0,2,33,1.5),@(3,2,37,1.3),@(7,2.5,38,1.1)) $stem 0.5
$grapes+=Leaf @(-4,2,34) -20
SaveFruit 'grapes' $grapes

$berry='0.74,0.115,0.08,1'
$strawberry=@(
    (B @(0,0,12) @(10,9,10) $berry)
    (B @(0,0,6) @(5.6,5.4,6) $berry 5)
    (B @(0,0,16.6) @(10.6,9.4,6) $berry 4)
)
foreach($deg in @(0,72,144,216,288)) {
    $t=$deg*[Math]::PI/180
    $strawberry+=B @((3.4*[Math]::Cos($t)),(3.4*[Math]::Sin($t)),22.7) @(5,2.1,1.6) $leaf 0.45 'Add' (Q @(0,0,1) $deg)
}
$strawberry+=S @(@(0,0,21,1.3),@(0.5,0,25.5,1)) $leaf 0.5
# Sparse, embedded seed shapes: large enough to survive the field cache.
foreach($ring in @(@(9,9.5,8.6,0),@(14.3,10.9,9.8,30),@(18.2,10.5,9.4,0))) {
    foreach($deg in @(0,60,120,180,240,300)) {
        $t=($deg+$ring[3])*[Math]::PI/180
        $strawberry+=B @(($ring[1]*[Math]::Cos($t)),($ring[2]*[Math]::Sin($t)),$ring[0]) @(1.05,1.05,1.4) '0.92,0.68,0.28,1' 0.15
    }
}
SaveFruit 'strawberry' $strawberry
$cherry='0.48,0.055,0.07,1'
SaveFruit 'cherries' @(
    (B @(-7,0,8) @(8,7.7,8) $cherry)
    (B @(7,1,8.2) @(8,7.5,8.2) $cherry 0.5)
    (B @(-7,0,16.5) @(2.8,2.8,2) $cherry 0.6 'Subtract')
    (B @(7,1,16.9) @(2.8,2.8,2) $cherry 0.6 'Subtract')
    (S @(@(-7,0,14.8,1.45),@(-5,0,23,1.4),@(0,1,31,1.45)) $stem 0.4)
    (S @(@(7,1,15.2,1.45),@(5.5,1,25,1.4),@(0,1,31,1.45),@(0.5,1,33,1.3)) $stem 0.4)
    (Leaf @(5,1,31) 10)
)
