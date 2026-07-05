# Generates Castle Courtyard SDF prop prefabs.
# Each prop is an ordered list of SdfBrush definitions; this script wraps them in the
# standard prefab boilerplate (SdfSculpture + ModelRenderer + SdfRaymarchRenderer + SdfCollider)
# with fresh GUIDs and writes <name>.prefab into Assets/Prefabs/CastleCourtyard.
#
# Conventions (match the hand-made bench/stool/turkey examples):
#   * Z is up. Props sit on the ground at z=0 and build upward. ~1 unit = 1cm (stool ~25 tall).
#   * Cylinder: radius = Size.x, half-height = Size.z, axis along Z.
#   * Cone:     base radius = Size.x at z=-Size.z, apex at z=+Size.z.
#   * Box:      Size = half-extents.   Sphere: Size = per-axis radii (ellipsoid).
#   * TriPrism: isosceles triangle in XY (apex +Size.y, base +/-Size.x), extruded +/-Size.z.
#   * Spline:   Points = list of "x,y,z,w" (w = tube radius), variable-radius tube.

$ErrorActionPreference = 'Stop'
$OutDir = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/CastleCourtyard'
$inv = [Globalization.CultureInfo]::InvariantCulture

function NG { [guid]::NewGuid().ToString() }
function F([double]$v) { $v.ToString('0.######', $inv) }

# Quaternion "x,y,z,w" from an axis + angle in degrees.
function Qaxis([double]$ax, [double]$ay, [double]$az, [double]$deg) {
    $r = [math]::Sqrt($ax * $ax + $ay * $ay + $az * $az)
    if ($r -lt 1e-9) { return '0,0,0,1' }
    $ax /= $r; $ay /= $r; $az /= $r
    $h = [math]::PI * $deg / 360.0; $s = [math]::Sin($h); $c = [math]::Cos($h)
    "$(F ($ax*$s)),$(F ($ay*$s)),$(F ($az*$s)),$(F $c)"
}
# Compose two quaternion strings (apply b then a -> a*b).
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

# Brush factory: takes a hashtable of overrides over sane defaults, returns an ordered dict.
function B([hashtable]$o) {
    $d = [ordered]@{
        Shape = 'Sphere'; Operation = 'Add'; Enabled = $true
        Position = '0,0,0'; Rotation = '0,0,0,1'; Size = '24,24,24'
        Points = @(); Curvature = 1; SplineClosed = $false
        Blend = 6; Rounding = 0.75; Color = '1,1,1,1'; Metallic = 0; Roughness = 1
        MirrorX = $false; MirrorY = $false; MirrorZ = $false
        IsSplineLoop = $false; BoundingRadius = 24
    }
    foreach ($k in $o.Keys) { $d[$k] = $o[$k] }
    # crude bounding radius for the (ignored-on-load) serialized field
    $sz = $d.Size.Split(','); $mx = 0.0
    foreach ($c in $sz) { $f = [math]::Abs([double]$c); if ($f -gt $mx) { $mx = $f } }
    $d.BoundingRadius = [double](F ($mx * 1.74 + $d.Blend * 0.25))
    $d
}

# ---- palette (r,g,b,a 0..1) -------------------------------------------------
$wood     = '0.6,0.35,0.22,1'
$woodDk   = '0.45,0.26,0.17,1'
$woodLt   = '0.74,0.5,0.3,1'
$plank    = '0.62,0.47,0.29,1'
$cream    = '0.95,0.9,0.75,1'
$tan      = '0.92,0.68,0.4,1'
$stone    = '0.55,0.54,0.5,1'
$stoneDk  = '0.42,0.42,0.4,1'
$terra    = '0.78,0.4,0.28,1'
$terraDk  = '0.62,0.3,0.2,1'
$red      = '0.78,0.18,0.18,1'
$royal    = '0.2,0.32,0.8,1'
$green    = '0.16,0.6,0.28,1'
$dgreen   = '0.1,0.4,0.2,1'
$orange   = '0.96,0.53,0.15,1'
$amber    = '0.96,0.7,0.13,1'
$wax      = '0.93,0.9,0.78,1'
$flame    = '0.97,0.6,0.13,1'
$breadTan = '0.8,0.56,0.3,1'

# metals: pass these spread into a brush with Metallic=1
$iron     = @{ Color = '0.32,0.32,0.34,1'; Metallic = 1; Roughness = 0.55 }
$steel    = @{ Color = '0.68,0.7,0.72,1'; Metallic = 1; Roughness = 0.45 }
$gold     = @{ Color = '0.83,0.69,0.32,1'; Metallic = 1; Roughness = 0.4 }

# merge helper: B with a metal preset + extra overrides
function BM([hashtable]$metal, [hashtable]$o) {
    $m = @{}; foreach ($k in $metal.Keys) { $m[$k] = $metal[$k] }
    foreach ($k in $o.Keys) { $m[$k] = $o[$k] }
    B $m
}

# =====================  PROP DEFINITIONS  ===================================
$props = @()

# ---- 1. Barrel ----
$props += @{ file = 'barrel'; name = 'Barrel'; res = 80; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,18'; Size='17,17,11'; Rounding=5; Blend=5; Color=$plank })
    (B @{ Shape='Cylinder'; Position='0,0,6';  Size='14.5,14.5,6'; Rounding=3; Blend=7; Color=$plank })
    (B @{ Shape='Cylinder'; Position='0,0,30'; Size='14.5,14.5,6'; Rounding=3; Blend=7; Color=$plank })
    (B @{ Shape='Cylinder'; Position='0,0,35'; Size='13,13,1.5'; Rounding=1; Blend=1; Color=$woodDk })
    (BM $iron @{ Shape='Cylinder'; Position='0,0,11'; Size='16.5,16.5,1.6'; Rounding=0.5; Blend=0.4 })
    (BM $iron @{ Shape='Cylinder'; Position='0,0,25'; Size='16.5,16.5,1.6'; Rounding=0.5; Blend=0.4 })
) }

# ---- 2. Crate ----
$props += @{ file = 'crate'; name = 'Crate'; res = 72; brushes = @(
    (B @{ Shape='Box'; Position='0,0,16'; Size='16,16,16'; Rounding=2; Blend=1; Color=$wood })
    # corner posts (one + mirror to all four verticals)
    (B @{ Shape='Box'; Position='14,14,16'; Size='2.5,2.5,16.5'; Rounding=0.9; Blend=0.2; Color=$woodDk; MirrorX=$true; MirrorY=$true })
    # top & bottom rails along X
    (B @{ Shape='Box'; Position='0,14,30'; Size='16.5,2.4,2.2'; Rounding=0.9; Blend=0.2; Color=$woodDk; MirrorY=$true; MirrorZ=$false })
    (B @{ Shape='Box'; Position='0,14,2';  Size='16.5,2.4,2.2'; Rounding=0.9; Blend=0.2; Color=$woodDk; MirrorY=$true })
    (B @{ Shape='Box'; Position='14,0,30'; Size='2.4,16.5,2.2'; Rounding=0.9; Blend=0.2; Color=$woodDk; MirrorX=$true })
    (B @{ Shape='Box'; Position='14,0,2';  Size='2.4,16.5,2.2'; Rounding=0.9; Blend=0.2; Color=$woodDk; MirrorX=$true })
) }

# ---- 3. Chest ----
$props += @{ file = 'chest'; name = 'Chest'; res = 88; brushes = @(
    (B @{ Shape='Box'; Position='0,0,11'; Size='22,14,11'; Rounding=2; Blend=1; Color=$wood })
    (B @{ Shape='Cylinder'; Position='0,0,22'; Rotation=(Qaxis 1 0 0 90); Size='14,14,22'; Rounding=3; Blend=1; Color=$woodLt })
    # iron bands over the lid
    (BM $iron @{ Shape='Box'; Position='-13,0,22'; Rotation=(Qaxis 1 0 0 90); Size='1.8,14.2,14.2'; Rounding=0.6; Blend=0.3; MirrorX=$true })
    (BM $iron @{ Shape='Box'; Position='0,0,11'; Size='22.3,1.6,11.3'; Rounding=0.6; Blend=0.3; MirrorX=$false })
    # corner straps on the box
    (BM $iron @{ Shape='Box'; Position='20,13,11'; Size='2.2,1.6,11.3'; Rounding=0.6; Blend=0.3; MirrorX=$true; MirrorY=$true })
    # lock plate
    (BM $iron @{ Shape='Box'; Position='0,-14,18'; Size='3,1.2,4'; Rounding=0.8; Blend=0.3 })
) }

# ---- 4. Sack ----
$props += @{ file = 'sack'; name = 'Sack'; res = 72; brushes = @(
    (B @{ Shape='Sphere'; Position='0,0,16'; Size='14,14,17'; Rounding=0.75; Blend=8; Color=$tan })
    (B @{ Shape='Sphere'; Position='0,0,4';  Size='15,15,5'; Blend=8; Color=$tan })
    (B @{ Shape='Cylinder'; Position='0,0,30'; Size='5,5,4'; Rounding=2; Blend=6; Color=$tan })
    # cinched neck
    (B @{ Shape='Cylinder'; Position='0,0,34'; Size='3,3,2'; Rounding=1.5; Blend=4; Color=$woodLt })
    # flared top
    (B @{ Shape='Cone'; Position='0,0,38'; Size='6,6,3'; Rounding=1.5; Blend=3; Color=$tan })
) }

# ---- 5. Pottery jug ----
$props += @{ file = 'jug'; name = 'Jug'; res = 80; brushes = @(
    (B @{ Shape='Sphere'; Position='0,0,15'; Size='13,13,15'; Blend=6; Color=$terra })
    (B @{ Shape='Cylinder'; Position='0,0,3'; Size='8,8,3'; Rounding=2; Blend=6; Color=$terra })
    (B @{ Shape='Cylinder'; Position='0,0,29'; Size='6,6,5'; Rounding=2; Blend=6; Color=$terra })
    (B @{ Shape='Cylinder'; Position='0,0,34'; Size='7,7,1.5'; Rounding=1; Blend=2; Color=$terraDk })
    # handle (spline arc, side)
    (B @{ Shape='Spline'; Blend=3; Color=$terra; Points=@('11,0,30,2.2','17,0,24,2','17,0,16,2','11,0,11,2.2') })
    # painted band
    (B @{ Shape='Cylinder'; Position='0,0,18'; Size='13.4,13.4,2.5'; Rounding=2; Blend=4; Color=$royal })
) }

# extra colours used below
$hay    = '0.85,0.72,0.32,1'
$twine  = '0.55,0.4,0.22,1'
$glass  = '0.95,0.78,0.35,1'
$soil   = '0.3,0.2,0.13,1'
$cheese = '0.95,0.78,0.32,1'
$paper  = '0.92,0.86,0.7,1'
$leaf   = '0.2,0.55,0.25,1'
$flip   = (Qaxis 1 0 0 180)   # turn a cone wide-end-up
$lieY   = (Qaxis 1 0 0 90)    # lay a Z-axis shape along Y
$lieX   = (Qaxis 0 1 0 90)    # lay a Z-axis shape along X

# ---- 6. Goblet ----
$props += @{ file = 'goblet'; name = 'Goblet'; res = 72; brushes = @(
    (BM $gold @{ Shape='Cone'; Rotation=$flip; Position='0,0,22'; Size='6,6,7'; Rounding=1; Blend=2 })
    (B  @{ Shape='Sphere'; Operation='Subtract'; Position='0,0,28'; Size='4.5,4.5,5'; Blend=1 })
    (BM $gold @{ Shape='Cylinder'; Position='0,0,13'; Size='1.6,1.6,7'; Blend=3 })
    (BM $gold @{ Shape='Sphere'; Position='0,0,16'; Size='2.4,2.4,2.4'; Blend=3 })
    (BM $gold @{ Shape='Cylinder'; Position='0,0,3'; Size='6,6,2'; Rounding=1.5; Blend=3 })
) }

# ---- 7. Tankard ----
$props += @{ file = 'tankard'; name = 'Tankard'; res = 64; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,11'; Size='8,8,11'; Rounding=2; Blend=1; Color=$woodLt })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Position='0,0,14'; Size='6,6,9'; Blend=1 })
    (B @{ Shape='Cylinder'; Position='0,0,1.5'; Size='8,8,1.5'; Rounding=1; Blend=1; Color=$woodDk })
    (B @{ Shape='Cylinder'; Position='0,0,21'; Size='8.2,8.2,1'; Rounding=0.6; Blend=1; Color=$woodDk })
    (B @{ Shape='Spline'; Blend=2; Color=$woodLt; Points=@('8,0,18,1.8','13,0,15,1.6','13,0,7,1.6','8,0,4,1.8') })
) }

# ---- 8. Cauldron ----
$props += @{ file = 'cauldron'; name = 'Cauldron'; res = 80; brushes = @(
    (B @{ Shape='Sphere'; Position='0,0,14'; Size='15,15,12'; Blend=4; Color='0.22,0.22,0.24,1' })
    (B @{ Shape='Sphere'; Operation='Subtract'; Position='0,0,20'; Size='11.5,11.5,8'; Blend=2 })
    (B @{ Shape='Cylinder'; Position='0,0,22'; Size='13,13,1.6'; Rounding=1; Blend=2; Color='0.22,0.22,0.24,1' })
    (B @{ Shape='Cone'; Rotation=$flip; Position='0,-11,4'; Size='2.5,2.5,5'; Blend=2; Color='0.18,0.18,0.2,1' })
    (B @{ Shape='Cone'; Rotation=$flip; Position='-9.5,5.5,4'; Size='2.5,2.5,5'; Blend=2; Color='0.18,0.18,0.2,1' })
    (B @{ Shape='Cone'; Rotation=$flip; Position='9.5,5.5,4'; Size='2.5,2.5,5'; Blend=2; Color='0.18,0.18,0.2,1' })
    (B @{ Shape='Spline'; Blend=1; Color='0.18,0.18,0.2,1'; Points=@('-13,0,18,1.3','0,0,30,1.3','13,0,18,1.3') })
) }

# ---- 9. Bowl ----
$props += @{ file = 'bowl'; name = 'Bowl'; res = 64; brushes = @(
    (B @{ Shape='Sphere'; Position='0,0,6'; Size='12,12,7'; Blend=2; Color=$terra })
    (B @{ Shape='Sphere'; Operation='Subtract'; Position='0,0,9'; Size='10,10,7'; Blend=2 })
    (B @{ Shape='Cylinder'; Position='0,0,1.5'; Size='5,5,1.5'; Rounding=1; Blend=2; Color=$terraDk })
) }

# ---- 10. Plate stack ----
$props += @{ file = 'plate-stack'; name = 'Plate Stack'; res = 56; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,1.2'; Size='11,11,1.1'; Rounding=0.9; Blend=0.5; Color=$cream })
    (B @{ Shape='Cylinder'; Position='0,0,3.5'; Size='11,11,1.1'; Rounding=0.9; Blend=0.5; Color=$royal })
    (B @{ Shape='Cylinder'; Position='0,0,5.8'; Size='11,11,1.1'; Rounding=0.9; Blend=0.5; Color=$cream })
    (B @{ Shape='Cylinder'; Position='1.5,1,8.2'; Rotation=(Qaxis 0 1 0 5); Size='11,11,1.1'; Rounding=0.9; Blend=0.5; Color=$terra })
) }

# ---- 11. Amphora ----
$props += @{ file = 'amphora'; name = 'Amphora'; res = 88; brushes = @(
    (B @{ Shape='Sphere'; Position='0,0,24'; Size='11,11,18'; Blend=5; Color=$terra })
    (B @{ Shape='Cone'; Rotation=$flip; Position='0,0,8'; Size='5,5,9'; Blend=5; Color=$terra })
    (B @{ Shape='Cylinder'; Position='0,0,42'; Size='4.5,4.5,7'; Blend=4; Color=$terra })
    (B @{ Shape='Cylinder'; Position='0,0,48'; Size='6,6,1.5'; Rounding=1; Blend=2; Color=$terraDk })
    (B @{ Shape='Spline'; Blend=3; Color=$terra; MirrorX=$true; Points=@('4,0,44,1.6','11,0,42,1.5','11,0,34,1.5','5,0,30,1.6') })
) }

# ---- 12. Bread loaf ----
$props += @{ file = 'bread-loaf'; name = 'Bread Loaf'; res = 64; brushes = @(
    (B @{ Shape='Sphere'; Position='0,0,7'; Size='16,9,7'; Blend=6; Color=$breadTan })
    (B @{ Shape='Sphere'; Position='0,0,9'; Size='13,7,5'; Blend=7; Color='0.84,0.6,0.34,1' })
    (B @{ Shape='Box'; Operation='Subtract'; Position='-6,0,13'; Rotation=(Qaxis 0 0 1 18); Size='1,9,2.5'; Blend=1 })
    (B @{ Shape='Box'; Operation='Subtract'; Position='0,0,14';  Rotation=(Qaxis 0 0 1 18); Size='1,9,2.5'; Blend=1 })
    (B @{ Shape='Box'; Operation='Subtract'; Position='6,0,13';  Rotation=(Qaxis 0 0 1 18); Size='1,9,2.5'; Blend=1 })
) }

# ---- 13. Carrot ----
$props += @{ file = 'carrot'; name = 'Carrot'; res = 56; brushes = @(
    (B @{ Shape='Cone'; Rotation=$flip; Position='0,0,11'; Size='3.5,3.5,11'; Rounding=1; Blend=2; Color=$orange })
    (B @{ Shape='Cone'; Position='0,0,26'; Size='1,1,6'; Blend=1; Color=$leaf })
    (B @{ Shape='Cone'; Position='2.5,0,25'; Rotation=(Qaxis 0 1 0 25); Size='0.9,0.9,5'; Blend=1; Color=$leaf; MirrorX=$true })
) }

# ---- 14. Apple ----
$props += @{ file = 'apple'; name = 'Apple'; res = 56; brushes = @(
    (B @{ Shape='Sphere'; Position='0,0,9'; Size='9,9,8.5'; Blend=2; Color=$red })
    (B @{ Shape='Sphere'; Operation='Subtract'; Position='0,0,18'; Size='2.5,2.5,2'; Blend=2 })
    (B @{ Shape='Sphere'; Operation='Subtract'; Position='0,0,0.5'; Size='2,2,1.5'; Blend=2 })
    (B @{ Shape='Cylinder'; Position='0,0,17'; Size='0.7,0.7,2.5'; Blend=1; Color=$woodDk })
    (B @{ Shape='Sphere'; Position='3,0,18'; Rotation=(Qaxis 0 1 0 30); Size='3,0.4,1.5'; Blend=1; Color=$leaf })
) }

# ---- 15. Cheese wheel ----
$props += @{ file = 'cheese-wheel'; name = 'Cheese Wheel'; res = 56; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,5'; Size='14,14,5'; Rounding=2; Blend=1; Color=$cheese })
    (B @{ Shape='TriangularPrism'; Operation='Subtract'; Position='0,0,5'; Size='8,12,6'; Blend=0.5 })
    (B @{ Shape='Sphere'; Operation='Subtract'; Position='-6,-7,7'; Size='1.5,1.5,1.5'; Blend=1 })
    (B @{ Shape='Sphere'; Operation='Subtract'; Position='-9,-2,6'; Size='1.2,1.2,1.2'; Blend=1 })
) }

# ---- 16. Table ----
$props += @{ file = 'table'; name = 'Table'; res = 88; brushes = @(
    (B @{ Shape='Box'; Position='0,0,28'; Size='40,24,2.5'; Rounding=1.5; Blend=1; Color=$plank })
    (B @{ Shape='Box'; Position='35,19,14'; Size='2.6,2.6,14'; Rounding=0.8; Blend=0.3; Color=$plank; MirrorX=$true; MirrorY=$true })
    (B @{ Shape='Box'; Position='0,19,24'; Size='36,2,2.5'; Rounding=0.7; Blend=0.3; Color=$plank; MirrorY=$true })
    (B @{ Shape='Box'; Position='35,0,24'; Size='2,21,2.5'; Rounding=0.7; Blend=0.3; Color=$plank; MirrorX=$true })
) }

# ---- 17. Ladder ----
$props += @{ file = 'ladder'; name = 'Ladder'; res = 88; brushes = @(
    (B @{ Shape='Box'; Position='0,9,40'; Size='1.8,1.8,40'; Rounding=0.8; Blend=0.3; Color=$woodLt; MirrorY=$true })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,8';  Size='1.3,1.3,9'; Blend=0.5; Color=$woodDk })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,22'; Size='1.3,1.3,9'; Blend=0.5; Color=$woodDk })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,36'; Size='1.3,1.3,9'; Blend=0.5; Color=$woodDk })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,50'; Size='1.3,1.3,9'; Blend=0.5; Color=$woodDk })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,64'; Size='1.3,1.3,9'; Blend=0.5; Color=$woodDk })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,72'; Size='1.3,1.3,9'; Blend=0.5; Color=$woodDk })
) }

# ---- 18. Signpost ----
$props += @{ file = 'signpost'; name = 'Signpost'; res = 80; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,28'; Size='2.5,2.5,28'; Rounding=1; Blend=1; Color=$woodDk })
    (B @{ Shape='Cone'; Position='0,0,57'; Size='3,3,3'; Blend=1; Color=$woodDk })
    (B @{ Shape='Box'; Position='6,0,50'; Size='13,1.4,4.5'; Rounding=1; Blend=1; Color=$plank })
    (B @{ Shape='Box'; Position='-6,0,42'; Size='12,1.4,4.5'; Rounding=1; Blend=1; Color=$plank })
) }

# ---- 19. Cart wheel ----
$props += @{ file = 'cart-wheel'; name = 'Cart Wheel'; res = 96; brushes = @(
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,18'; Size='18,18,2.5'; Rounding=1; Blend=0.5; Color=$woodLt })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Rotation=$lieY; Position='0,0,18'; Size='14,14,3'; Blend=0.5 })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,18'; Size='4,4,3'; Blend=1; Color=$woodDk })
    (B @{ Shape='Box'; Position='0,0,18'; Size='1.4,2.5,15'; Blend=0.5; Color=$woodLt })
    (B @{ Shape='Box'; Position='0,0,18'; Size='15,2.5,1.4'; Blend=0.5; Color=$woodLt })
    (B @{ Shape='Box'; Rotation=(Qaxis 0 1 0 45);  Position='0,0,18'; Size='1.4,2.5,15'; Blend=0.5; Color=$woodLt })
    (B @{ Shape='Box'; Rotation=(Qaxis 0 1 0 -45); Position='0,0,18'; Size='1.4,2.5,15'; Blend=0.5; Color=$woodLt })
) }

# ---- 20. Anvil ----
$props += @{ file = 'anvil'; name = 'Anvil'; res = 80; brushes = @(
    (BM $iron @{ Shape='Box'; Position='0,0,5'; Size='9,6,5'; Rounding=1; Blend=1 })
    (BM $iron @{ Shape='Box'; Position='0,0,11'; Size='5,4,3'; Blend=1 })
    (BM $iron @{ Shape='Box'; Position='0,0,17'; Size='15,7,4'; Rounding=1.5; Blend=1 })
    (BM $iron @{ Shape='Cone'; Rotation=$lieX; Position='16,0,17'; Size='4,4,9'; Blend=1 })
    (BM $iron @{ Shape='Box'; Position='-13,0,17'; Size='3,5,2'; Blend=1 })
) }

# ---- 21. Hay bale ----
$props += @{ file = 'hay-bale'; name = 'Hay Bale'; res = 72; brushes = @(
    (B @{ Shape='Box'; Position='0,0,12'; Size='22,13,12'; Rounding=5; Blend=2; Color=$hay })
    (B @{ Shape='Box'; Position='9,0,12'; Size='0.9,13.2,12.3'; Rounding=0.5; Blend=0.3; Color=$twine; MirrorX=$true })
) }

# ---- 22. Candle ----
$props += @{ file = 'candle'; name = 'Candle'; res = 56; brushes = @(
    (BM $iron @{ Shape='Cylinder'; Position='0,0,1.5'; Size='6,6,1.5'; Rounding=1; Blend=0.5 })
    (B @{ Shape='Cylinder'; Position='0,0,8'; Size='3,3,7'; Blend=1; Color=$wax })
    (B @{ Shape='Sphere'; Position='3,0,12'; Size='1,1,2'; Blend=3; Color=$wax })
    (B @{ Shape='Cylinder'; Position='0,0,15.5'; Size='0.35,0.35,1'; Color='0.15,0.12,0.1,1' })
    (B @{ Shape='Cone'; Position='0,0,18'; Size='1.2,1.2,2.5'; Blend=1; Color=$flame })
    (B @{ Shape='Sphere'; Position='0,0,17.5'; Size='0.6,0.6,1'; Blend=2; Color='0.97,0.85,0.4,1' })
) }

# ---- 23. Candelabra ----
$props += @{ file = 'candelabra'; name = 'Candelabra'; res = 80; brushes = @(
    (BM $gold @{ Shape='Cylinder'; Position='0,0,2'; Size='7,7,2'; Rounding=1; Blend=1 })
    (BM $gold @{ Shape='Cylinder'; Position='0,0,13'; Size='1.6,1.6,12'; Blend=1 })
    (BM $gold @{ Shape='Spline'; Blend=2; MirrorX=$true; Points=@('0,0,20,1.3','7,0,22,1.2','12,0,21,1.2','13,0,25,1.2') })
    (BM $gold @{ Shape='Cylinder'; Position='13,0,26'; Size='2.4,2.4,1.5'; Blend=1; MirrorX=$true })
    (BM $gold @{ Shape='Cylinder'; Position='0,0,26'; Size='2.4,2.4,1.5'; Blend=1 })
    (B @{ Shape='Cylinder'; Position='13,0,29'; Size='1.5,1.5,3'; Blend=1; Color=$wax; MirrorX=$true })
    (B @{ Shape='Cylinder'; Position='0,0,29'; Size='1.5,1.5,3'; Blend=1; Color=$wax })
    (B @{ Shape='Cone'; Position='13,0,33'; Size='1,1,2'; Blend=1; Color=$flame; MirrorX=$true })
    (B @{ Shape='Cone'; Position='0,0,33'; Size='1,1,2'; Blend=1; Color=$flame })
) }

# ---- 24. Book ----
$props += @{ file = 'book'; name = 'Book'; res = 56; brushes = @(
    (B @{ Shape='Box'; Position='0,0,2'; Size='11,15,2'; Rounding=1; Blend=0.5; Color=$royal })
    (B @{ Shape='Box'; Position='1,0,2'; Size='9.5,13.5,1.4'; Blend=0.3; Color=$cream })
    (B @{ Shape='Box'; Position='-10.5,0,2'; Size='1,15,2.2'; Rounding=0.8; Blend=0.5; Color=$royal })
) }

# ---- 25. Book stack ----
$props += @{ file = 'book-stack'; name = 'Book Stack'; res = 64; brushes = @(
    (B @{ Shape='Box'; Position='0,0,2'; Size='12,16,2'; Rounding=1; Blend=0.4; Color=$red })
    (B @{ Shape='Box'; Position='1,1,6'; Rotation=(Qaxis 0 0 1 8);  Size='11,15,2'; Rounding=1; Blend=0.4; Color=$dgreen })
    (B @{ Shape='Box'; Position='-1,-1,10'; Rotation=(Qaxis 0 0 1 -6); Size='11.5,15,2'; Rounding=1; Blend=0.4; Color=$royal })
) }

# ---- 26. Scroll ----
$props += @{ file = 'scroll'; name = 'Scroll'; res = 56; brushes = @(
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,0,4'; Size='3.5,3.5,11'; Rounding=1; Blend=1; Color=$paper })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,11,4'; Size='4,4,1.5'; Rounding=1; Blend=1; Color=$paper; MirrorY=$true })
    (B @{ Shape='Cylinder'; Rotation=$lieY; Position='0,14,4'; Size='0.8,0.8,2'; Blend=0.5; Color=$woodDk; MirrorY=$true })
) }

# ---- 27. Shield ----
$props += @{ file = 'shield'; name = 'Shield'; res = 80; brushes = @(
    (B @{ Shape='Box'; Position='0,0,24'; Size='12,2,9'; Rounding=4; Blend=2; Color=$royal })
    (B @{ Shape='Box'; Position='0,0,14'; Size='9,2,6'; Rounding=3; Blend=3; Color=$royal })
    (B @{ Shape='Box'; Rotation=(Qaxis 0 1 0 45); Position='0,0,8'; Size='5,2,5'; Rounding=1; Blend=3; Color=$royal })
    (BM $steel @{ Shape='Sphere'; Position='0,2,20'; Size='3,1.5,3'; Blend=1 })
    (BM $gold @{ Shape='Box'; Position='0,2,20'; Size='1.2,0.6,11'; Blend=0.5 })
    (BM $gold @{ Shape='Box'; Position='0,2,22'; Size='7,0.6,1.2'; Blend=0.5 })
) }

# ---- 28. Banner ----
$props += @{ file = 'banner'; name = 'Banner'; res = 80; brushes = @(
    (B @{ Shape='Cylinder'; Rotation=$lieX; Position='0,0,56'; Size='1.5,1.5,15'; Blend=0.5; Color=$woodDk })
    (BM $gold @{ Shape='Sphere'; Position='15,0,56'; Size='2,2,2'; Blend=0.5; MirrorX=$true })
    (B @{ Shape='Box'; Position='0,1,38'; Size='11,0.6,17'; Blend=1; Color=$red })
    (B @{ Shape='Box'; Operation='Subtract'; Position='0,1,20'; Rotation=(Qaxis 0 1 0 35); Size='4,2,8'; Blend=0.5; MirrorX=$true })
    (BM $gold @{ Shape='Box'; Rotation=(Qaxis 0 1 0 45); Position='0,1.4,40'; Size='5,0.4,5'; Blend=0.5 })
) }

# ---- 29. Lantern ----
$props += @{ file = 'lantern'; name = 'Lantern'; res = 72; brushes = @(
    (BM $iron @{ Shape='Cylinder'; Position='0,0,3'; Size='6,6,2'; Rounding=1; Blend=0.5 })
    (B @{ Shape='Cylinder'; Position='0,0,11'; Size='5,5,6'; Blend=1; Color=$glass; Roughness=0.3 })
    (BM $iron @{ Shape='Box'; Position='4.5,4.5,11'; Size='0.7,0.7,7'; Blend=0.3; MirrorX=$true; MirrorY=$true })
    (BM $iron @{ Shape='Cone'; Position='0,0,18'; Size='6,6,4'; Blend=0.5 })
    (BM $iron @{ Shape='Sphere'; Position='0,0,22'; Size='1.2,1.2,1.2'; Blend=0.5 })
    (BM $iron @{ Shape='Spline'; SplineClosed=$true; Blend=0.5; Points=@('2.5,0,24,0.5','0,0,28,0.5','-2.5,0,24,0.5') })
) }

# ---- 30. Flower pot ----
$props += @{ file = 'flower-pot'; name = 'Flower Pot'; res = 72; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,7'; Size='8,8,7'; Rounding=2; Blend=1; Color=$terra })
    (B @{ Shape='Cylinder'; Position='0,0,2'; Size='6,6,2'; Rounding=1; Blend=3; Color=$terra })
    (B @{ Shape='Cylinder'; Position='0,0,13'; Size='9,9,1.5'; Rounding=1; Blend=1; Color=$terraDk })
    (B @{ Shape='Cylinder'; Position='0,0,13.5'; Size='7,7,1'; Blend=0.5; Color=$soil })
    (B @{ Shape='Cylinder'; Position='0,0,18'; Size='0.7,0.7,5'; Blend=1; Color=$green })
    (B @{ Shape='Sphere'; Position='0,0,23'; Size='3,3,2.5'; Blend=2; Color=$red })
    (B @{ Shape='Cylinder'; Position='3,0,17'; Rotation=(Qaxis 0 1 0 20); Size='0.6,0.6,5'; Blend=1; Color=$green; MirrorX=$true })
    (B @{ Shape='Sphere'; Position='5,0,22'; Size='2.5,2.5,2'; Blend=2; Color=$amber; MirrorX=$true })
) }

# ---- 31. Broom ----
$props += @{ file = 'broom'; name = 'Broom'; res = 64; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,34'; Size='1.3,1.3,34'; Blend=0.5; Color=$woodLt })
    (B @{ Shape='Cone'; Position='0,0,8'; Size='6,6,8'; Blend=2; Color=$tan })
    (B @{ Shape='Cone'; Position='0,0,3'; Size='6.5,6.5,4'; Blend=3; Color='0.85,0.6,0.32,1' })
    (B @{ Shape='Cylinder'; Position='0,0,15'; Size='3.5,3.5,2'; Rounding=1; Blend=1; Color=$twine })
) }

# ---- 32. Rope coil ----
$props += @{ file = 'rope-coil'; name = 'Rope Coil'; res = 72; brushes = @(
    (B @{ Shape='Spline'; SplineClosed=$true; Curvature=1; Blend=1; Color=$twine; Points=@(
        '11,0,3,2.5','7.8,7.8,3,2.5','0,11,3,2.5','-7.8,7.8,3,2.5','-11,0,3,2.5','-7.8,-7.8,3,2.5','0,-11,3,2.5','7.8,-7.8,3,2.5') })
    (B @{ Shape='Spline'; SplineClosed=$true; Curvature=1; Blend=1; Color='0.5,0.36,0.2,1'; Points=@(
        '7,0,6.5,2.2','4.9,4.9,6.5,2.2','0,7,6.5,2.2','-4.9,4.9,6.5,2.2','-7,0,6.5,2.2','-4.9,-4.9,6.5,2.2','0,-7,6.5,2.2','4.9,-4.9,6.5,2.2') })
) }

# ---- 33. Spear ----
$props += @{ file = 'spear'; name = 'Spear'; res = 64; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,40'; Size='1.3,1.3,40'; Blend=0.5; Color=$woodDk })
    (BM $steel @{ Shape='Cylinder'; Position='0,0,80'; Size='1.8,1.8,2'; Blend=0.5 })
    (BM $steel @{ Shape='Cone'; Position='0,0,86'; Size='2.4,2.4,7'; Blend=0.5 })
) }

# ---- 34. Bucket ----
$props += @{ file = 'bucket'; name = 'Bucket'; res = 72; brushes = @(
    (B @{ Shape='Cone'; Rotation=$flip; Position='0,0,10'; Size='9,9,9'; Blend=1; Color=$woodLt })
    (B @{ Shape='Cylinder'; Position='0,0,3'; Size='6,6,2.5'; Rounding=1; Blend=2; Color=$woodLt })
    (B @{ Shape='Cone'; Operation='Subtract'; Rotation=$flip; Position='0,0,12'; Size='7.5,7.5,9'; Blend=1 })
    (BM $iron @{ Shape='Cylinder'; Position='0,0,18'; Size='9.3,9.3,1'; Blend=0.5 })
    (BM $iron @{ Shape='Cylinder'; Position='0,0,8'; Size='8.2,8.2,1'; Blend=1 })
    (BM $iron @{ Shape='Spline'; Blend=0.5; Points=@('-9,0,17,0.8','0,0,27,0.8','9,0,17,0.8') })
) }

# ---- 35. Basket ----
$props += @{ file = 'basket'; name = 'Basket'; res = 72; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,9'; Size='11,11,9'; Rounding=3; Blend=1; Color=$tan })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Position='0,0,12'; Size='9,9,8'; Blend=1 })
    (B @{ Shape='Cylinder'; Position='0,0,5'; Size='11.3,11.3,1.5'; Rounding=0.8; Blend=1; Color='0.82,0.58,0.3,1' })
    (B @{ Shape='Cylinder'; Position='0,0,12'; Size='11.3,11.3,1.5'; Rounding=0.8; Blend=1; Color='0.82,0.58,0.3,1' })
    (B @{ Shape='Cylinder'; Position='0,0,17'; Size='11.5,11.5,1.2'; Rounding=0.8; Blend=1; Color=$woodDk })
    (B @{ Shape='Spline'; Blend=1; Color=$tan; Points=@('-11,0,16,1.3','0,0,28,1.2','11,0,16,1.3') })
) }

# ---- 36. Crown ----
$props += @{ file = 'crown'; name = 'Crown'; res = 80; brushes = @(
    (BM $gold @{ Shape='Spline'; SplineClosed=$true; Curvature=1; Blend=1; Points=@(
        '8,0,4,2','5.7,5.7,4,2','0,8,4,2','-5.7,5.7,4,2','-8,0,4,2','-5.7,-5.7,4,2','0,-8,4,2','5.7,-5.7,4,2') })
    (BM $gold @{ Shape='Cone'; Position='8,0,8'; Size='2.5,2.5,6'; Blend=2; MirrorX=$true })
    (BM $gold @{ Shape='Cone'; Position='0,8,8'; Size='2.5,2.5,6'; Blend=2; MirrorY=$true })
    (B @{ Shape='Sphere'; Position='8,0,5'; Size='1.5,1.5,1.5'; Blend=1; Color=$red; MirrorX=$true })
    (B @{ Shape='Sphere'; Position='0,8,5'; Size='1.5,1.5,1.5'; Blend=1; Color=$royal; MirrorY=$true })
) }

# ---- 37. Well ----
$props += @{ file = 'well'; name = 'Well'; res = 96; brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,14'; Size='16,16,14'; Rounding=2; Blend=1; Color=$stone })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Position='0,0,16'; Size='12,12,14'; Blend=1 })
    (B @{ Shape='Cylinder'; Position='0,0,27'; Size='17,17,2'; Rounding=1.5; Blend=1; Color=$stoneDk })
    (B @{ Shape='Box'; Position='13,0,40'; Size='2,2,14'; Rounding=0.8; Blend=0.5; Color=$woodDk; MirrorX=$true })
    (B @{ Shape='Box'; Position='0,7,52'; Rotation=(Qaxis 1 0 0 -30); Size='17,8,1.5'; Rounding=1; Blend=0.5; Color=$plank; MirrorY=$true })
    (B @{ Shape='Cylinder'; Rotation=$lieX; Position='0,0,53'; Size='1.5,1.5,13'; Blend=0.5; Color=$woodDk })
) }

Write-Host "Defined $($props.Count) props."

# =====================  EMIT  ==============================================
function NewComponentTail {
    @{
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
    }
}

function Emit($file, $name, $res, $brushes) {
    $sculpt = [ordered]@{
        __type = 'Mimiclay.SdfSculpture'; __guid = (NG); __enabled = $true; Flags = 0
        AutoRebuild = $false; BakedMesh = $null; Brushes = @($brushes)
        FlipFaces = $false; Material = 'materials/plasticine_vertex.vmat'
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        Resolution = 32
    }
    $model = [ordered]@{
        __type = 'Sandbox.ModelRenderer'; __guid = (NG); __enabled = $true; Flags = 0
        BodyGroups = 18446744073709551615; CreateAttachments = $false; LodOverride = $null
        MaterialGroup = $null; MaterialOverride = $null; Materials = $null
        Model = 'sbox_procedural_model.vmdl'
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        RenderOptions = [ordered]@{ GameLayer = $true; OverlayLayer = $false; BloomLayer = $false; AfterUILayer = $false }
        RenderType = 'ShadowsOnly'; Tint = '1,1,1,1'
    }
    $ray = [ordered]@{
        __type = 'Mimiclay.SdfRaymarchRenderer'; __guid = (NG); __enabled = $true; Flags = 0
        AdaptiveQuality = $true; BrushCulling = $true; CullRadii = 220; DebugBounds = $false
        DebugLiveField = $false; DebugLod = $false; DebugSwitchState = $false; DepthClamp = $false
        Displace = $true; DisplaceAmount = 0.35; DisplaceFrequency = 0.25; DistanceSwitching = $true
        Epsilon = 0.15; FarEpsilon = 3.5; FieldCacheOnly = $true; FieldNormalScale = 0.5
        FieldResolution = $res; FullQualityRadii = 2; LodHysteresis = 0.06
        Material = 'materials/plasticine.vmat'; MaxSteps = 50; MeshLod1Radii = 90; MeshLod2Radii = 140
        MeshMode = 'DepthProxy'; MinQualityRadii = 16; MinSteps = 6
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        OverdrawOptimization = $false; SparseField = $true; TightBounds = $false
        Transmission = $true; UseFieldCache = $true
    }
    $collider = [ordered]@{
        __type = 'Mimiclay.SdfCollider'; __guid = (NG); __enabled = $true; Flags = 0
        FootProbeSpacing = 12
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
    }
    $root = [ordered]@{
        RootObject = [ordered]@{
            __guid = (NG); __version = 2; Flags = 0; Name = $name
            Position = '0,0,0'; Rotation = '0,0,0,1'; Scale = '1,1,1'; Tags = ''; Enabled = $true
            NetworkMode = 2; NetworkFlags = 0; NetworkOrphaned = 0; NetworkTransmit = $true; OwnerTransfer = 1
            Components = @($sculpt, $model, $ray, $collider)
            Children = @()
        }
        ResourceVersion = 2; ShowInMenu = $false; MenuPath = $null; MenuIcon = $null
        DontBreakAsTemplate = $false; __references = @(); __version = 2
    }
    $json = $root | ConvertTo-Json -Depth 16
    [IO.File]::WriteAllText("$OutDir/$file.prefab", $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host "  wrote $file.prefab ($($brushes.Count) brushes)"
}

foreach ($p in $props) { Emit $p.file $p.name $p.res $p.brushes }
Write-Host "Done."
