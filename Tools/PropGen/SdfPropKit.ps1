# SdfPropKit — shared helpers for hand-styled SDF prop recipes (Tools/PropGen/props/*.ps1).
# Dot-source it:   . "$PSScriptRoot/../SdfPropKit.ps1"
#
# The prefab boilerplate (SdfSculpture + ModelRenderer + SdfRaymarchRenderer + SdfCollider + SculptBounds +
# DamageProfile + ClayBoil) is spliced from a TEMPLATE prefab that matches the hand-made DevPropHunt props
# (Garden/gardenpot.prefab), so components stay identical to the reference set. Only the Brushes block,
# name, root rotation and every guid/Id are replaced.
#
# Shape conventions (Z up, 1 unit = 1 inch, sculpture-local):
#   Cylinder : radius = Size.x (Size.y ignored), HALF-height = Size.z, axis local Z, CENTRED.
#              Taper 0..1 shrinks the +Z end (top radius = Size.x*(1-Taper)). Flip it (Qaxis 0 1 0 180)
#              to get a shape that WIDENS upward (pots, buckets).
#   Box      : Size = half-extents.   Sphere: Size = ellipsoid radii.
#   Spline   : Points = "x,y,z,radius" strings in sculpture-local space (Position/Rotation unused).
#   Blend    : smooth-union radius against everything ABOVE it in the list (first brush: irrelevant).
#   Subtract : its Color TINTS the cut walls -> use a darker shade of the body for hollow interiors.

$ErrorActionPreference = 'Stop'
$script:KitInv = [Globalization.CultureInfo]::InvariantCulture
$script:KitRoot = (Resolve-Path "$PSScriptRoot").Path
$script:ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$script:TemplatePrefab = "$script:ProjectRoot/Assets/Prefabs/Props/Garden/gardenpot.prefab"

function F([double]$v) { $v.ToString('0.#####', $script:KitInv) }
function V3([double]$x, [double]$y, [double]$z) { "$(F $x),$(F $y),$(F $z)" }
function P4([double]$x, [double]$y, [double]$z, [double]$r) { "$(F $x),$(F $y),$(F $z),$(F $r)" }

# Colour from 0..255 sRGB ints (what you read off a reference image) -> "r,g,b,1".
function RGB255([int]$r, [int]$g, [int]$b) { "$(F ($r/255.0)),$(F ($g/255.0)),$(F ($b/255.0)),1" }
# Scale a colour string's rgb by k (k<1 darker; used for Subtract interiors / shadow bands).
function Shade([string]$c, [double]$k) {
    $p = $c.Split(',')
    "$(F ([math]::Min(1.0,[double]$p[0]*$k))),$(F ([math]::Min(1.0,[double]$p[1]*$k))),$(F ([math]::Min(1.0,[double]$p[2]*$k))),1"
}

# Quaternion "x,y,z,w" from axis + degrees.
function Qaxis([double]$ax, [double]$ay, [double]$az, [double]$deg) {
    $r = [math]::Sqrt($ax * $ax + $ay * $ay + $az * $az)
    if ($r -lt 1e-9) { return '0,0,0,1' }
    $ax /= $r; $ay /= $r; $az /= $r
    $h = [math]::PI * $deg / 360.0; $s = [math]::Sin($h); $c = [math]::Cos($h)
    "$(F ($ax*$s)),$(F ($ay*$s)),$(F ($az*$s)),$(F $c)"
}
# a*b (apply b first, then a).
function Qmul([string]$a, [string]$b) {
    $p = $a.Split(','); $q = $b.Split(',')
    $ax = [double]$p[0]; $ay = [double]$p[1]; $az = [double]$p[2]; $aw = [double]$p[3]
    $bx = [double]$q[0]; $by = [double]$q[1]; $bz = [double]$q[2]; $bw = [double]$q[3]
    $x = $aw * $bx + $ax * $bw + $ay * $bz - $az * $by
    $y = $aw * $by - $ax * $bz + $ay * $bw + $az * $bx
    $z = $aw * $bz + $ax * $by - $ay * $bx + $az * $bw
    $w = $aw * $bw - $ax * $bx - $ay * $by - $az * $bz
    "$(F $x),$(F $y),$(F $z),$(F $w)"
}
$script:QFlip = '0,1,0,0'           # 180 deg about Y: tapered cylinders widen UPWARD
$script:QAlongX = (Qaxis 0 1 0 90)  # lay a Z-axis shape along X
$script:QAlongY = (Qaxis 1 0 0 90)  # lay a Z-axis shape along Y

# Brush factory: overrides on top of the full field set the editor writes. Numeric fields MUST be numbers.
function B([hashtable]$o) {
    $d = [ordered]@{
        Id = ''; Shape = 'Sphere'; Operation = 'Add'; CrossSection = 'Triangle'; Text = 'clay'; Font = 'Super Joyful'
        GroupKind = $null; LinkColor = $false; LinkBlend = $false; Enabled = $true
        Position = '0,0,0'; Rotation = '0,0,0,1'; Size = '12,12,12'; Points = $null
        Curvature = 1; SplineClosed = $false; SplinePerPointRadius = $false
        Slice = 0; SlicePlaneN = 0; Blend = 1; Gap = 0; Rounding = 0.75
        Color = '1,1,1,1'; Metallic = 0; Roughness = 0.5
        MirrorX = $false; MirrorY = $false; MirrorZ = $false
        Damage = $false; Shrinks = $false; ShrinkDelay = 2; ShrinkDuration = 1.5
    }
    foreach ($k in $o.Keys) { $d[$k] = $o[$k] }
    if ($d.Shape -eq 'Spline') {
        $d.SplinePerPointRadius = $true
        $d.Points = @($d.Points)
    }
    $d.Id = [guid]::NewGuid().ToString()
    $d
}

# Points of an elliptical arc for a Spline brush: centre c, unit axes u and v (in-plane), radii Ru along u and
# Rv along v, from angle a0 to a1 degrees in n steps, tube radius tr (scalar, or scriptblock { param($t) } t 0..1).
# A bucket handle = u along the pivot axis, v = the swing direction; Rv > Ru lets it hang clear of a tapered body.
function ArcPoints([double[]]$c, [double[]]$u, [double[]]$v, [double]$Ru, [double]$Rv, [double]$a0, [double]$a1, [int]$n, $tr) {
    $pts = @()
    for ($i = 0; $i -le $n; $i++) {
        $t = $i / [double]$n
        $a = [math]::PI * ($a0 + ($a1 - $a0) * $t) / 180.0
        $ca = [math]::Cos($a); $sa = [math]::Sin($a)
        $x = $c[0] + $Ru * $ca * $u[0] + $Rv * $sa * $v[0]
        $y = $c[1] + $Ru * $ca * $u[1] + $Rv * $sa * $v[1]
        $z = $c[2] + $Ru * $ca * $u[2] + $Rv * $sa * $v[2]
        $rr = if ($tr -is [scriptblock]) { [double](& $tr $t) } else { [double]$tr }
        $pts += (P4 $x $y $z $rr)
    }
    , $pts
}

# Write <OutPath>.prefab: template boilerplate + these brushes. $Bounds = SculptBounds centre "x,y,z".
# Deterministic guid from a string, so re-running a recipe keeps every id stable (scene instances + diffs stay sane).
function DetGuid([string]$key) {
    $md5 = [Security.Cryptography.MD5]::Create()
    $h = $md5.ComputeHash([Text.Encoding]::UTF8.GetBytes($key))
    (New-Object guid (,$h)).ToString()
}

function Write-SdfProp([string]$OutPath, [string]$Name, [object[]]$Brushes, [string]$BoundsCentre = '0,0,24') {
    $brushes = @($Brushes)
    for ($i = 0; $i -lt $brushes.Count; $i++) { $brushes[$i].Id = DetGuid "$Name/brush/$i" }
    $json = ConvertTo-Json -InputObject $brushes -Depth 8
    # indent to sit under "Brushes":
    $json = ($json -split "`r?`n" | ForEach-Object { '        ' + $_ }) -join "`n"
    $json = $json.TrimStart()

    $t = [IO.File]::ReadAllText($script:TemplatePrefab)
    $start = $t.IndexOf('"Brushes": [')
    $end = $t.IndexOf('"BrushObjects"', $start)
    if ($start -lt 0 -or $end -lt 0) { throw "template has no Brushes block" }
    $t = $t.Substring(0, $start) + '"Brushes": ' + $json + ",`n        " + $t.Substring($end)

    # fresh identity: every guid, name, upright root, bounds centre
    $script:GuidN = 0
    $t = [regex]::Replace($t, '"__guid": "[0-9a-f-]{36}"', { param($m) $script:GuidN++; '"__guid": "' + (DetGuid "$Name/obj/$script:GuidN") + '"' })
    $t = [regex]::Replace($t, '("RootObject": \{\s*"__guid": "[^"]*",\s*"__version": 2,\s*"Flags": 0,\s*"Name": )"[^"]*"', "`$1`"$Name`"")
    $t = [regex]::Replace($t, '("Name": "' + [regex]::Escape($Name) + '",\s*"Position": "[^"]*",\s*"Rotation": )"[^"]*"', '$1"0,0,0,1"')
    $t = [regex]::Replace($t, '("Anchor": "FollowShape",\s*"Center": )"[^"]*"', "`$1`"$BoundsCentre`"")

    $dir = Split-Path $OutPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [IO.File]::WriteAllText($OutPath, $t, (New-Object Text.UTF8Encoding($false)))
    Write-Host "wrote $OutPath ($($brushes.Count) brushes)"
}

# Interior / shadow-side colour: darken AND pull toward grey. Saturated interiors (esp. blues) come out
# neon through Transmission's albedo-deepen, so hollows want a muted shade of the body, not just a darker one.
function Hollow([string]$c, [double]$k = 0.68, [double]$desat = 0.3) {
    $p = $c.Split(','); $r = [double]$p[0]; $g = [double]$p[1]; $b = [double]$p[2]
    $l = 0.299 * $r + 0.587 * $g + 0.114 * $b
    $r = ($r + ($l - $r) * $desat) * $k; $g = ($g + ($l - $g) * $desat) * $k; $b = ($b + ($l - $b) * $desat) * $k
    "$(F $r),$(F $g),$(F $b),1"
}
