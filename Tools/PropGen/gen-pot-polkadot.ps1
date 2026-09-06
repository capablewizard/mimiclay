# Rebuilds prefabs/props/kitchen/pot_polkedot.prefab = pot.prefab's brushes + a ring of polka dots.
#
# Each dot is a CYLINDER brush with Operation = Cutout, its local Z axis pointed RADIALLY
# outward at the pot wall. The cutout op subtracts a thin shell around the brush BOUNDARY
# (Blend = groove size) and repaints the clay INSIDE the brush, so the cylinder's side wall
# scores a circular groove on the pot and the disc it encloses comes out in the dot colour:
# the "cut out and set back in place" polka dot. See the Cutout notes in SdfOperation.
#
# Geometry (all local to the sculpture, matching the body brush = Cylinder r 32.2 on Z):
#   * dot centre sits ON the wall radius, so the NEAR cap is ~2u out in the air (no groove
#     there) and the FAR cap is ~2u inside the 4u-thick wall (its shell is buried, invisible,
#     and never reaches the hollow interior at r 28.19).
#   * rows are placed on the straight part of the barrel only (local z -19 .. +3.7: below is
#     the base round-off, above is the rim subtract), and staggered by half a step.
#
# Idempotent: the body brushes are re-read from pot.prefab every run, so re-running never
# stacks dots. Tune the knobs below and re-run.

$ErrorActionPreference = 'Stop'
$Src = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/Props/Kitchen/pot.prefab'
$Dst = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/Props/Kitchen/pot_polkedot.prefab'
$inv = [Globalization.CultureInfo]::InvariantCulture
function F([double]$v) { $v.ToString('0.#########', $inv) }

# ---- knobs ----------------------------------------------------------------
$wallR    = 32.2015152      # pot body outer radius (body brush Size.x)
$dotR     = 4.8             # dot radius (cylinder Size.x)
$depth    = 2.0             # cylinder half-height along the radial axis (Size.z)
$blend    = 1.6237113       # groove size (0 would be a flat painted dot, no score line)
$rounding = 0.0
$perRow   = 8               # dots per row
$rowZ     = @(-4.5, -16.5)  # row heights in sculpture-local Z
$stagger  = $true           # offset every other row by half a step
$colDot   = '0.62791,0.5235,0.43807,1'   # warm greige

# ---- dot brushes ----------------------------------------------------------
$D2R  = [math]::PI / 180.0
$half = 1.0 / [math]::Sqrt(2.0)             # cos45 = sin45, the Ry(90) that aims local Z at +X
$br   = [math]::Sqrt($dotR * $dotR + $depth * $depth) + $blend * 0.25   # SdfBrush.BoundingRadius

$dots = New-Object System.Collections.ArrayList
for ($row = 0; $row -lt $rowZ.Count; $row++) {
    $z = [double]$rowZ[$row]
    $step = 360.0 / $perRow
    $off = 0.0
    if ($stagger -and ($row % 2 -eq 1)) { $off = $step * 0.5 }
    for ($i = 0; $i -lt $perRow; $i++) {
        $deg = $off + $i * $step
        $th = $deg * $D2R
        $px = $wallR * [math]::Cos($th)
        $py = $wallR * [math]::Sin($th)

        # q = Rz(theta) * Ry(90): aims the cylinder's local +Z along the outward radial (cos,sin,0).
        $hs = [math]::Sin($th * 0.5); $hc = [math]::Cos($th * 0.5)
        $qx = -$half * $hs
        $qy =  $half * $hc
        $qz =  $half * $hs
        $qw =  $half * $hc

        $obj = @"
          {
            "Shape": "Cylinder",
            "Operation": "Cutout",
            "CrossSection": "Triangle",
            "Text": "clay",
            "Font": "Super Joyful",
            "Enabled": true,
            "Position": "$(F $px),$(F $py),$(F $z)",
            "Rotation": "$(F $qx),$(F $qy),$(F $qz),$(F $qw)",
            "Size": "$(F $dotR),$(F $dotR),$(F $depth)",
            "Points": [],
            "Curvature": 1,
            "SplineClosed": false,
            "SplinePerPointRadius": false,
            "Slice": 0,
            "SlicePlaneN": 1,
            "LocalCentre": "0,0,0",
            "Blend": $(F $blend),
            "Rounding": $(F $rounding),
            "Color": "$colDot",
            "Metallic": 0,
            "Roughness": 0.5,
            "MirrorX": false,
            "MirrorY": false,
            "EffectiveMirrorX": false,
            "EffectiveMirrorY": false,
            "EffectiveMirrorZ": false,
            "MirrorZ": false,
            "Damage": false,
            "Shrinks": false,
            "ShrinkDelay": 2,
            "ShrinkDuration": 1.5,
            "IsSplineLoop": false,
            "BoundingRadius": $(F $br)
          }
"@
        [void]$dots.Add($obj)
    }
}

# ---- splice ---------------------------------------------------------------
# The closing bracket is matched by INDENT ("\n        ],"), not a lazy "\],": the brushes
# themselves contain "Points": [], which a lazy match would stop on.
$rx = [regex]'(?s)"Brushes": \[\r?\n(.*?)\r?\n        \],'

$srcRaw = [IO.File]::ReadAllText($Src)
$m = $rx.Matches($srcRaw)
if ($m.Count -ne 1) { throw "pot.prefab: expected 1 Brushes array, found $($m.Count)" }
$bodyBrushes = $m[0].Groups[1].Value
$bodyCount = ([regex]'"Shape":').Matches($bodyBrushes).Count

$items = @($bodyBrushes) + $dots.ToArray()
$newBrushes = "`"Brushes`": [`r`n" + ($items -join ",`r`n") + "`r`n        ],"

$dstRaw = [IO.File]::ReadAllText($Dst)
$md = $rx.Matches($dstRaw)
if ($md.Count -ne 1) { throw "pot_polkedot.prefab: expected 1 Brushes array, found $($md.Count)" }
$out = $rx.Replace($dstRaw, { param($mm) $newBrushes }, 1)
# The here-strings above emit LF; the prefabs on disk are CRLF -- normalise so the file stays uniform.
$out = ($out -replace "`r`n", "`n") -replace "`n", "`r`n"
[IO.File]::WriteAllText($Dst, $out, (New-Object Text.UTF8Encoding($false)))

Write-Host ("Wrote {0}: {1} body + {2} dots = {3} brushes" -f $Dst, $bodyCount, $dots.Count, ($bodyCount + $dots.Count))
