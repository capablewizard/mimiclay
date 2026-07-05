# Populates prefabs/saved/football.prefab with a truncated-icosahedron panel layout:
# 12 pentagons + 20 hexagons sitting tangent on the white sphere body (33 brushes total,
# under the 64 cap). Classic two-tone: BLACK pentagons, WHITE hexagons.
#
# Each panel is an Extruded brush (cross-section in local XY, extruded along local Z).
# Panel local +Z is aligned to the sphere outward normal at its face centre; the in-plane
# spin aligns an EDGE MIDPOINT to the nearest neighbour so seams line up (SdfBrush.cs:
# hexagon vertices at k*60 first on +X -> edge mids at 30+k*60; pentagon vertices at
# 90+k*72 -> edge mids at 126+k*72). Sizes/thickness/rounding copied from the hexagon the
# user already placed so the look matches. The sphere brush + all components are preserved;
# only the Brushes array is rewritten.

$ErrorActionPreference = 'Stop'
$Prefab = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/saved/football.prefab'
$inv = [Globalization.CultureInfo]::InvariantCulture
function F([double]$v) { $v.ToString('0.#########', $inv) }

# ---- panel look (from the example hexagon) --------------------------------
$Rsurf   = 22.7266445           # sphere Size.x (radius)
$placeR  = 21.0365              # face-centre distance along normal (example: 21.0364952)
$thick   = 2.35364056           # Size.z (half-depth)
$rounding = 1.8355044
$blend   = 0.00016903604
$hexApothem = 0.86602540        # apothem / circumradius for a regular hexagon
$pentApothem = 0.80901699       # ...and pentagon
$colPent = '0.09,0.09,0.1,1'    # black pentagons
$colHex  = '0.9,0.92,0.94,1'    # white hexagons (a touch brighter than the 0.85 ball)

$phi = (1 + [math]::Sqrt(5)) / 2
$D2R = [math]::PI / 180.0

function Norm([double]$x, [double]$y, [double]$z) {
    $l = [math]::Sqrt($x*$x + $y*$y + $z*$z)
    $a = $x/$l; $b = $y/$l; $c = $z/$l
    [double[]]@($a, $b, $c)
}

# 12 pentagon centres = icosahedron vertices.
$pentDirs = New-Object System.Collections.ArrayList
foreach ($s1 in 1, -1) { foreach ($s2 in 1, -1) {
    [void]$pentDirs.Add((Norm 0.0 ($s1 * 1.0) ($s2 * $phi)))
    [void]$pentDirs.Add((Norm ($s1 * 1.0) ($s2 * $phi) 0.0))
    [void]$pentDirs.Add((Norm ($s1 * $phi) 0.0 ($s2 * 1.0)))
} }

# 20 hexagon centres = icosahedron FACE centres: the normalised sum of each triple of
# mutually-adjacent vertices (adjacent when the angle between them is the icosa edge angle,
# cos = 1/sqrt5). NOTE: do NOT use raw dodecahedron vertex coords here -- the standard
# (0,+/-1/phi,+/-phi) set is the dual of a DIFFERENTLY-oriented icosahedron, which lands
# some hexagon centres ~10.8 deg from a pentagon centre and makes the panels overlap badly.
$hexDirs = New-Object System.Collections.ArrayList
$edgeCos = 1.0 / [math]::Sqrt(5.0)
for ($i = 0; $i -lt 12; $i++) { for ($j = $i + 1; $j -lt 12; $j++) { for ($k = $j + 1; $k -lt 12; $k++) {
    $dij = $pentDirs[$i][0]*$pentDirs[$j][0] + $pentDirs[$i][1]*$pentDirs[$j][1] + $pentDirs[$i][2]*$pentDirs[$j][2]
    $dik = $pentDirs[$i][0]*$pentDirs[$k][0] + $pentDirs[$i][1]*$pentDirs[$k][1] + $pentDirs[$i][2]*$pentDirs[$k][2]
    $djk = $pentDirs[$j][0]*$pentDirs[$k][0] + $pentDirs[$j][1]*$pentDirs[$k][1] + $pentDirs[$j][2]*$pentDirs[$k][2]
    if (([math]::Abs($dij - $edgeCos) -lt 0.01) -and ([math]::Abs($dik - $edgeCos) -lt 0.01) -and ([math]::Abs($djk - $edgeCos) -lt 0.01)) {
        $cx = $pentDirs[$i][0] + $pentDirs[$j][0] + $pentDirs[$k][0]
        $cy = $pentDirs[$i][1] + $pentDirs[$j][1] + $pentDirs[$k][1]
        $cz = $pentDirs[$i][2] + $pentDirs[$j][2] + $pentDirs[$k][2]
        [void]$hexDirs.Add((Norm $cx $cy $cz))
    }
} } }

$allDirs = New-Object System.Collections.ArrayList
foreach ($d in $pentDirs) { [void]$allDirs.Add($d) }
foreach ($d in $hexDirs)  { [void]$allDirs.Add($d) }
Write-Host ("Pentagons: {0}  Hexagons: {1}" -f $pentDirs.Count, $hexDirs.Count)

function Dot($a, $b) { $a[0]*$b[0] + $a[1]*$b[1] + $a[2]*$b[2] }
function Cross($a, $b) {
    $cx = $a[1]*$b[2] - $a[2]*$b[1]
    $cy = $a[2]*$b[0] - $a[0]*$b[2]
    $cz = $a[0]*$b[1] - $a[1]*$b[0]
    [double[]]@($cx, $cy, $cz)
}

# Nearest-neighbour analysis: mean angle to the N closest centres (5 pent / 6 hex) drives
# panel size, and the single nearest gives the seam-alignment direction.
function Neighbours($n, $count) {
    $angs = New-Object System.Collections.ArrayList
    foreach ($m in $allDirs) {
        $d = [math]::Max(-1.0, [math]::Min(1.0, (Dot $n $m)))
        if ($d -lt 0.99999) { [void]$angs.Add(@{ a = [math]::Acos($d); v = $m }) }
    }
    $sorted = $angs | Sort-Object { $_.a }
    $sorted[0..($count-1)]
}

# Rotation matrix [Xw|Yw|n] -> quaternion "x,y,z,w".
function MatToQuat($Xw, $Yw, $n) {
    $m00=$Xw[0]; $m10=$Xw[1]; $m20=$Xw[2]
    $m01=$Yw[0]; $m11=$Yw[1]; $m21=$Yw[2]
    $m02=$n[0];  $m12=$n[1];  $m22=$n[2]
    $tr = $m00 + $m11 + $m22
    if ($tr -gt 0) {
        $S = [math]::Sqrt($tr + 1.0) * 2; $w = 0.25 * $S
        $x = ($m21 - $m12) / $S; $y = ($m02 - $m20) / $S; $z = ($m10 - $m01) / $S
    } elseif (($m00 -gt $m11) -and ($m00 -gt $m22)) {
        $S = [math]::Sqrt(1.0 + $m00 - $m11 - $m22) * 2
        $w = ($m21 - $m12) / $S; $x = 0.25 * $S; $y = ($m01 + $m10) / $S; $z = ($m02 + $m20) / $S
    } elseif ($m11 -gt $m22) {
        $S = [math]::Sqrt(1.0 + $m11 - $m00 - $m22) * 2
        $w = ($m02 - $m20) / $S; $x = ($m01 + $m10) / $S; $y = 0.25 * $S; $z = ($m12 + $m21) / $S
    } else {
        $S = [math]::Sqrt(1.0 + $m22 - $m00 - $m11) * 2
        $w = ($m10 - $m01) / $S; $x = ($m02 + $m20) / $S; $y = ($m12 + $m21) / $S; $z = 0.25 * $S
    }
    "$(F $x),$(F $y),$(F $z),$(F $w)"
}

# Panel size + placement uses the TRUE truncated-icosahedron geometry so every seam lines up
# with a single gap. Pentagons and hexagons sit at slightly DIFFERENT face-plane radii
# (r_p/r_h ~= 1.027) and have different circumradii -- forcing them onto one radius is what
# made the pent<->hex seams tighter than the hex<->hex seams. Canonical edge-2 metrics below,
# then scale (k) so the hexagon plane sits at the known-good depth and the solid's 60 vertices
# inscribe on the ball surface. At GAP=1 panels tile edge-to-edge (touching); GAP<1 opens a
# uniform seam on every edge.
$edge   = 2.0
$RpentF = $edge / (2 * [math]::Sin(36 * $D2R))            # pentagon circumradius
$RhexF  = $edge                                           # hexagon circumradius (= edge)
$Rsolid = ($edge / 4.0) * [math]::Sqrt(58 + 18 * [math]::Sqrt(5))
$rpF    = [math]::Sqrt($Rsolid*$Rsolid - $RpentF*$RpentF) # centre -> pentagon face plane
$rhF    = [math]::Sqrt($Rsolid*$Rsolid - $RhexF*$RhexF)   # centre -> hexagon face plane
$kScale = $placeR / $rhF                                  # anchor hexagon plane to $placeR
$placePent = $kScale * $rpF
$placeHex  = $kScale * $rhF                               # == $placeR
$crPent = $kScale * $RpentF                               # circumradius before the seam gap
$crHex  = $kScale * $RhexF
$GAP = 0.98            # 1.0 = panels touch edge-to-edge; lower opens a uniform hairline seam
Write-Host ("pent: plane {0} cr {1} | hex: plane {2} cr {3} | vertices at {4}" -f `
    (F $placePent), (F ($crPent*$GAP)), (F $placeHex), (F ($crHex*$GAP)), (F ($kScale*$Rsolid)))

$panels = New-Object System.Collections.ArrayList
function EmitPanel($n, $isPent) {
    $nb = Neighbours $n 6                                 # nearest gives the seam-align direction
    $placeThis = if ($isPent) { $placePent } else { $placeHex }
    $cr = if ($isPent) { $crPent * $GAP } else { $crHex * $GAP }

    # in-plane frame: align an edge midpoint to the nearest neighbour
    $near = $nb[0].v
    $dn = Dot $near $n
    $e1 = Norm ($near[0] - $dn*$n[0]) ($near[1] - $dn*$n[1]) ($near[2] - $dn*$n[2])
    $e2 = Cross $n $e1              # right-handed: e1 x e2 = n
    $thetaE = if ($isPent) { 126.0 * $D2R } else { 30.0 * $D2R }
    $ct = [math]::Cos($thetaE); $st = [math]::Sin($thetaE)
    $Xw = @( ($ct*$e1[0] - $st*$e2[0]), ($ct*$e1[1] - $st*$e2[1]), ($ct*$e1[2] - $st*$e2[2]) )
    $Yw = @( ($st*$e1[0] + $ct*$e2[0]), ($st*$e1[1] + $ct*$e2[1]), ($st*$e1[2] + $ct*$e2[2]) )
    $quat = MatToQuat $Xw $Yw $n

    $pos = "$(F ($n[0]*$placeThis)),$(F ($n[1]*$placeThis)),$(F ($n[2]*$placeThis))"
    $br = [math]::Sqrt($cr*$cr + $thick*$thick)
    $xs = if ($isPent) { 'Pentagon' } else { 'Hexagon' }
    $col = if ($isPent) { $colPent } else { $colHex }

    $obj = @"
          {
            "Shape": "Extruded",
            "Operation": "Add",
            "CrossSection": "$xs",
            "Text": "clay",
            "Font": "Super Joyful",
            "Enabled": true,
            "Position": "$pos",
            "Rotation": "$quat",
            "Size": "$(F $cr),$(F $cr),$thick",
            "Points": null,
            "Curvature": 1,
            "SplineClosed": false,
            "Slice": 0,
            "SlicePlaneN": 1,
            "LocalCentre": "0,0,0",
            "Blend": $blend,
            "Rounding": $rounding,
            "Color": "$col",
            "Metallic": 0,
            "Roughness": 0.5,
            "MirrorX": false,
            "MirrorY": false,
            "MirrorZ": false,
            "IsSplineLoop": false,
            "BoundingRadius": $(F $br)
          }
"@
    [void]$panels.Add($obj)
}

foreach ($n in $pentDirs) { EmitPanel $n $true }
foreach ($n in $hexDirs)  { EmitPanel $n $false }

# The sphere body brush, preserved verbatim (first brush of the existing prefab).
$sphere = @"
          {
            "Shape": "Sphere",
            "Operation": "Add",
            "CrossSection": "Triangle",
            "Text": "clay",
            "Font": "Super Joyful",
            "Enabled": true,
            "Position": "0,0,0",
            "Rotation": "0,0,0,1",
            "Size": "22.7266445,22.7266445,22.7266445",
            "Points": null,
            "Curvature": 1,
            "SplineClosed": false,
            "Slice": 0,
            "SlicePlaneN": 1,
            "LocalCentre": "0,0,0",
            "Blend": 6,
            "Rounding": 0.75,
            "Color": "0.85,0.88,0.9,1",
            "Metallic": 0,
            "Roughness": 0.5,
            "MirrorX": false,
            "MirrorY": false,
            "MirrorZ": false,
            "IsSplineLoop": false,
            "BoundingRadius": 24.226645
          }
"@

$items = @($sphere) + $panels.ToArray()
$arrayBody = ($items -join ",`r`n")
$newBrushes = "`"Brushes`": [`r`n$arrayBody`r`n        ],"

# Splice: replace the whole "Brushes": [ ... ], block, leaving everything else intact.
$raw = [IO.File]::ReadAllText($Prefab)
$rx = [regex]'(?s)"Brushes":\s*\[.*?\],'
if ($rx.Matches($raw).Count -ne 1) { throw "Expected exactly one Brushes array, found $($rx.Matches($raw).Count)" }
# MatchEvaluator replacement so no $-group substitution touches the JSON text.
$out = $rx.Replace($raw, { param($m) $newBrushes }, 1)
[IO.File]::WriteAllText($Prefab, $out, (New-Object Text.UTF8Encoding($false)))

Write-Host ("Wrote {0}: 1 sphere + {1} panels = {2} brushes" -f $Prefab, $panels.Count, ($panels.Count + 1))
