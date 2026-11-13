# Flower pot — the user's gardenpot.prefab (3 brushes, copied exactly, same frame: origin mid-pot, rim top z~31)
# plus soil and three clay daisies (refs: chunky 4-petal flowers on thick stalks, fat leaves spilling over the rim).
#   soil   = a dark disc sitting ~7 below the rim inside the hollow;
#   leaves = carrot-top style splines (thin at the soil, fat in the middle, rounded tip), fanned round the pot;
#   flower = thick spline stalk + 4 flat rounded Cylinder petals in the head plane + a sliced-Sphere dome centre.
#            Heads tip outward/up (normal n); petals are rotated with the head frame, spun a little per flower.
# Variants (2026-11-12, matched to the user's three reference shots; 'default' = the user-approved original, KEEP):
#   default -> flower_pot.prefab          coral 1.2 + cream 0.8 + yellow 1.0, terracotta, 5 leaves (one hand-placed)
#   sunny   -> flower_pot_sunny.prefab    2 small yellow (orange centres) + 1 big coral (tan centre), terracotta, 6 leaves
#   daisies -> flower_pot_daisies.prefab  3 cream (yellow centres) at staggered heights, RED pot, 5 tall leaves
#   blue    -> flower_pot_blue.prefab     1 big + 1 smaller coral (yellow centres), BLUE pot, 5 short leaves
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File Tools/PropGen/props/flower_pot.ps1 [-Variant default|sunny|daisies|blue] [-Out <path>]
param([string]$Out, [ValidateSet('default', 'sunny', 'daisies', 'blue')][string]$Variant = 'default')
. "$PSScriptRoot/../SdfPropKit.ps1"
$Name = if ($Variant -eq 'default') { 'flower_pot' } else { "flower_pot_$Variant" }
if (-not $Out) { $Out = "$ProjectRoot/Assets/Prefabs/Props/Garden/$Name.prefab" }

# ---- pot (gardenpot.prefab verbatim; colour per variant) ----
$terra  = '0.66047,0.42393,0.33791,1'
$terraIn = '0.53953,0.31503,0.23338,1'
if ($Variant -eq 'daisies') { $terra = '0.74,0.27,0.25,1'; $terraIn = Hollow $terra }   # red pot (crimson; 0.76,0.33,0.27 read orange under the warm sun)
if ($Variant -eq 'blue')    { $terra = '0.45,0.63,0.72,1'; $terraIn = Hollow $terra }   # light blue pot
$QPot = '0.389752835,-0.920920193,0,0'   # the pot's flip (widens upward)

# ---- palette ----
$soil   = '0.47907,0.33201,0.23842,1'   # user: lighter soil
$green  = '0.48606,0.63721,0.36158,1'   # user: lighter, fresher green (leaves + stalks)
$coral  = '0.85,0.42,0.36,1'
$cream  = '0.89,0.86,0.79,1'
$yellow = '0.88,0.72,0.3,1'
$tan    = '0.66,0.55,0.4,1'
$orange = '0.86,0.55,0.28,1'   # sunny ref: the yellow flowers' centres

# ---- vector helpers ----
function Norm([double[]]$v) { $l = [math]::Sqrt($v[0]*$v[0] + $v[1]*$v[1] + $v[2]*$v[2]); , @(($v[0]/$l), ($v[1]/$l), ($v[2]/$l)) }
function QRot([string]$q, [double[]]$v) {   # rotate v by quaternion "x,y,z,w"
    $p = $q.Split(','); $qx = [double]$p[0]; $qy = [double]$p[1]; $qz = [double]$p[2]; $qw = [double]$p[3]
    $tx = 2 * ($qy * $v[2] - $qz * $v[1]); $ty = 2 * ($qz * $v[0] - $qx * $v[2]); $tz = 2 * ($qx * $v[1] - $qy * $v[0])
    , @(($v[0] + $qw * $tx + $qy * $tz - $qz * $ty), ($v[1] + $qw * $ty + $qz * $tx - $qx * $tz), ($v[2] + $qw * $tz + $qx * $ty - $qy * $tx))
}
function ZTo([double[]]$n) {                # quaternion taking +Z onto unit n
    $ang = [math]::Acos([math]::Max(-1.0, [math]::Min(1.0, [double]$n[2]))) * 180 / [math]::PI
    if ($ang -lt 1e-4) { return '0,0,0,1' }
    Qaxis (-$n[1]) $n[0] 0 $ang             # axis = Z x n
}

# ---- flower ----
$PetalR = 7.6; $PetalOff = 9.6; $PetalCup = 12; $PetalHH = 2.32; $PetalRound = 3; $PetalLift = 0.6   # user (pink flower, 2nd pass): petals shrunk ~0.78 + spread so they clear the centre (gap ~2.4), thinner, lifted 0.6 along n
$DomeR = 5.545; $DomeH = 3.882; $DomeLift = 2.087   # user-tuned centre (pink flower): smaller, flatter, raised along n
$StalkR = 3.4; $SoilZ = 22
$Rng = [System.Random]::new(3)   # petal count 4 or 5 per flower; fixed seed (3 = pink 4, cream 5, yellow 5) so regenerating is stable
function Flower([double[]]$head, [double[]]$nIn, [double]$spin, [string]$petal, [string]$centre, [double[]]$bend, [double]$size = 1, [int]$petals = 0) {   # size scales the head: petal radius + spread, dome (petal thickness stays); petals 0 = seeded 4/5
    $n = Norm $nIn
    $q = Qmul (ZTo $n) (Qaxis 0 0 1 $spin)
    $out = @()
    # stalk: from the soil (under the head, pulled toward the middle) up to just behind the head
    $b0 = @(($head[0] * 0.35), ($head[1] * 0.35), ($SoilZ - 2))
    $top = @(($head[0] - $n[0] * 2), ($head[1] - $n[1] * 2), ($head[2] - $n[2] * 2))
    $mid = @((($b0[0] + $top[0]) / 2 + $bend[0]), (($b0[1] + $top[1]) / 2 + $bend[1]), (($b0[2] + $top[2]) / 2))
    $out += , (B @{ Shape = 'Spline'; Size = '1,1,1'; Blend = 1; Color = $green
                    Points = @((P4 $b0[0] $b0[1] $b0[2] $StalkR), (P4 $mid[0] $mid[1] $mid[2] $StalkR), (P4 $top[0] $top[1] $top[2] ($StalkR * 0.9))) })
    # petals: 4 or 5 flat discs round the head, each cupped: tipped about its own tangent so the OUTER edge lifts toward n
    $count = if ($petals -gt 0) { $petals } else { $Rng.Next(4, 6) }
    Write-Host "  flower $petal : $count petals"
    for ($k = 0; $k -lt $count; $k++) {
        $r = 2 * [math]::PI * $k / $count
        $o = QRot $q @(($PetalOff * $size * [math]::Cos($r)), ($PetalOff * $size * [math]::Sin($r)), 0)
        $qp = Qmul $q (Qaxis (-[math]::Sin($r)) ([math]::Cos($r)) 0 (-$PetalCup))   # -angle about the tangent = outer edge up
        $out += , (B @{ Shape = 'Cylinder'; Position = (V3 ($head[0] + $o[0] + $n[0] * $PetalLift) ($head[1] + $o[1] + $n[1] * $PetalLift) ($head[2] + $o[2] + $n[2] * $PetalLift)); Rotation = $qp
                        Size = (V3 ($PetalR * $size) ($PetalR * $size) $PetalHH); Rounding = $PetalRound; Blend = 0; Color = $petal })
    }
    # centre: sphere sliced at its mid-plane, dome pointing along n (sphere -Z = n), flat face raised DomeLift above the head
    $qd = Qmul $q (Qaxis 1 0 0 180)
    $dc = @(($head[0] + $n[0] * ($DomeLift * $size)), ($head[1] + $n[1] * ($DomeLift * $size)), ($head[2] + $n[2] * ($DomeLift * $size)))
    $out += , (B @{ Shape = 'Sphere'; Position = (V3 $dc[0] $dc[1] $dc[2]); Rotation = $qd; Size = (V3 ($DomeR * $size) ($DomeR * $size) ($DomeH * $size))
                    Slice = 0.5; Rounding = 1; Blend = 0.6; Color = $centre })
    , $out
}

# ---- leaf ----
# The user's hand-tuned leaf (2026-11-12): FAT at the soil, tapering to a point, rising ~50 deg and sweeping ~15 deg
# sideways as it goes. Stored as absolute points; copies are re-expressed in cylindrical terms about the pot axis
# (radius, azimuth relative to the base, height) and placed at a new azimuth, scaled in length, sweep mirrored.
$LeafTpl = @(@(5.90884495, -0.290527344, 22.2045403, 5.79251003), @(13.7472553, 2.03320312, 33.54319, 5.40991926),
             @(20.0580463, 3.81689453, 40.8011131, 4.13461542), @(24.1230507, 5.33056641, 47.6030197, 2.22166014))
$LeafAz0 = [math]::Atan2($LeafTpl[0][1], $LeafTpl[0][0]) * 180 / [math]::PI
function LeafPoints([double]$azDeg, [double]$scale, [double]$mirror) {
    $b = $LeafTpl[0]; $rb = [math]::Sqrt($b[0] * $b[0] + $b[1] * $b[1]); $zb = $b[2]
    $pts = @()
    foreach ($p in $LeafTpl) {
        $r = [math]::Sqrt($p[0] * $p[0] + $p[1] * $p[1])
        $da = [math]::Atan2($p[1], $p[0]) * 180 / [math]::PI - $LeafAz0
        $a = [math]::PI * ($azDeg + $mirror * $da) / 180
        $r2 = $rb + ($r - $rb) * $scale; $z2 = $zb + ($p[2] - $zb) * $scale
        $pts += , @(($r2 * [math]::Cos($a)), ($r2 * [math]::Sin($a)), $z2, $p[3])
    }
    , $pts
}
# A leaf whose tip would have to bend more than MaxTipBend to clear a head gets SHORTER instead (user: a severe hook
# looked wrong) - shrink 5% at a time down to MinLeafScale, then take whatever bend is left.
$MaxTipBend = 6; $MinLeafScale = 0.65
function Leaf([double]$azDeg, [double]$scale, [double]$mirror) {
    $s = $scale
    while ($true) {
        $pts = AvoidHeads (LeafPoints $azDeg $s $mirror)
        if ($script:LastBend -le $MaxTipBend -or $s -le $MinLeafScale + 1e-6) { break }
        $s = [math]::Max($MinLeafScale, $s - 0.05)
    }
    if ($script:LastBend -gt 0.01 -or $s -lt $scale) {
        Write-Host ("  leaf at {0:N0} deg: length {1:N2} -> {2:N2}, tip bend {3:N1}" -f $azDeg, $scale, $s, $script:LastBend)
    }
    B @{ Shape = 'Spline'; Size = '1,1,1'; Blend = 0.3; Color = $green; Points = @($pts | ForEach-Object { P4 $_[0] $_[1] $_[2] $_[3] }) }
}

# Leaves veer away from flower heads: each head is a disc (centre, normal n, radius PetalOff+PetalR). While any
# leaf point is closer than its own radius + LeafGap to a disc, bend the leaf away from it - the base stays planted,
# the TIP moves (weights 0, 0, .25, 1): user preferred the leaves' placement untouched and just the tips bending off.
# The push is horizontal only, away from the head centre (pushing along the disc's nearest-point direction drooped
# the leaves down instead of veering).
$LeafGap = 3
$LeafDroop = 0.6   # downward share of the veer (0 = flat sideways jut, which looked like escaping)
$Heads = @()   # filled from the flower specs below, before the leaves are built
function DiscGap([double[]]$p, $h) {
    $v = @(($p[0] - $h.c[0]), ($p[1] - $h.c[1]), ($p[2] - $h.c[2]))
    $hn = $v[0] * $h.n[0] + $v[1] * $h.n[1] + $v[2] * $h.n[2]
    $rad = @(($v[0] - $hn * $h.n[0]), ($v[1] - $hn * $h.n[1]), ($v[2] - $hn * $h.n[2]))
    $rl = [math]::Sqrt($rad[0] * $rad[0] + $rad[1] * $rad[1] + $rad[2] * $rad[2])
    $k = if ($rl -gt $h.R) { $h.R / $rl } else { 1.0 }
    $q = @(($h.c[0] + $rad[0] * $k), ($h.c[1] + $rad[1] * $k), ($h.c[2] + $rad[2] * $k))   # nearest point on the disc
    $d = @(($p[0] - $q[0]), ($p[1] - $q[1]), ($p[2] - $q[2]))
    $dl = [math]::Sqrt($d[0] * $d[0] + $d[1] * $d[1] + $d[2] * $d[2])
    @{ gap = ($dl - $PetalHH); dir = $d; len = $dl }
}
function AvoidHeads($pts) {
    $w = @(0, 0, 0.25, 1.0)   # user: only the TIP veers (the last point, with a touch on the one before so the bend stays smooth)
    $t0 = @($pts[$pts.Count - 1][0], $pts[$pts.Count - 1][1], $pts[$pts.Count - 1][2])
    for ($it = 0; $it -lt 60; $it++) {
        $worst = 0; $wi = -1; $wdir = $null
        for ($i = $pts.Count - 1; $i -lt $pts.Count; $i++) {   # only the tip is tested
            foreach ($h in $Heads) {
                $g = DiscGap $pts[$i] $h
                $def = ($pts[$i][3] + $LeafGap) - $g.gap
                if ($def -gt $worst) { $worst = $def; $wi = $i; $wdir = @(($pts[$i][0] - $h.c[0]), ($pts[$i][1] - $h.c[1]), 0) }
            }
        }
        if ($wi -lt 0) { break }
        # away direction = horizontal, away from the head's centre seen from above (a real veer, not a droop);
        # if the point is right under the centre, push radially outward from the pot axis
        $dx = $wdir[0]; $dy = $wdir[1]
        $hl = [math]::Sqrt($dx * $dx + $dy * $dy)
        if ($hl -lt 1e-3) { $dx = $pts[$wi][0]; $dy = $pts[$wi][1]; $hl = [math]::Sqrt($dx * $dx + $dy * $dy) }
        $dz = -$LeafDroop * $hl                       # bow outward AND a little down, like a real leaf bending off
        $l = [math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
        $step = [math]::Min($worst, 2.0) / $w[$wi]
        for ($j = 1; $j -lt $pts.Count; $j++) {
            $pts[$j][0] += $dx / $l * $step * $w[$j]; $pts[$j][1] += $dy / $l * $step * $w[$j]; $pts[$j][2] += $dz / $l * $step * $w[$j]
        }
    }
    $t1 = $pts[$pts.Count - 1]
    $script:LastBend = [math]::Sqrt(($t1[0] - $t0[0]) * ($t1[0] - $t0[0]) + ($t1[1] - $t0[1]) * ($t1[1] - $t0[1]) + ($t1[2] - $t0[2]) * ($t1[2] - $t0[2]))
    , $pts
}

$brushes = @(
    (B @{ Shape = 'Cylinder'; Position = '-0.08323,-0.00011,-0.3892'; Rotation = $QPot; Size = '28.89998,24,25.78476'; Taper = 0.22978
          Rounding = 3.45; Blend = 6; Color = $terra; Roughness = 0.55 })
    (B @{ Shape = 'Cylinder'; Position = '-0.0106974747,0,25.5191345'; Rotation = $QPot; Size = '31.3123531,27.0656796,5.62215853'
          Rounding = 0.6; Blend = 0; Color = $terra; Roughness = 0.55 })
    (B @{ Shape = 'Cylinder'; Operation = 'Subtract'; Position = '-0.01054,0,5.65635'; Rotation = $QPot; Size = '25.24745,21.01413,27.60027'
          Taper = 0.33954; Rounding = 2.256837; Blend = 0.8; Color = $terraIn; Roughness = 0.55 })
)
# ---- per-variant flowers + leaves (front = +X; the viewer's LEFT = -Y) ----
# leaves: @(azimuth offset from the user's leaf, length scale, sweep mirror)
switch ($Variant) {
    'default' {
        # tall coral front, cream back-left, yellow back-right (user-approved; leave as is)
        $FlowerSpecs = @(
            @{ head = @(6, 0, 73);   n = @(0.62, 0.05, 0.78);  spin = 20;  petal = $coral;  centre = $yellow; bend = @(3, -2);  size = 1.2 }
            @{ head = @(-2, 21, 60); n = @(0.6, 0.32, 0.73);   spin = -10; petal = $cream;  centre = $yellow; bend = @(-2, 2);  size = 0.8 }
            @{ head = @(0, -21, 55); n = @(0.6, -0.36, 0.71);  spin = 35;  petal = $yellow; centre = $tan;    bend = @(1, -3);  size = 1.0 }
        )
        $LeafSpecs = @(@(0, 1.0, 1), @(68, 0.86, -1), @(140, 0.95, 1), @(212, 0.82, -1))
    }
    'sunny' {
        # ref 1: two smaller yellows on the left (one low in front, one higher behind), one big coral on the right
        $FlowerSpecs = @(
            @{ head = @(8, -15, 60);  n = @(0.62, -0.33, 0.71); spin = 10;  petal = $yellow; centre = $orange; bend = @(2, -1);  size = 0.85; petals = 5 }
            @{ head = @(-6, -6, 71);  n = @(0.5, -0.25, 0.83);  spin = 40;  petal = $yellow; centre = $orange; bend = @(-1, -2); size = 0.8;  petals = 5 }
            @{ head = @(5, 16, 64);   n = @(0.6, 0.32, 0.73);   spin = -15; petal = $coral;  centre = $tan;    bend = @(1, 2);   size = 1.15; petals = 5 }
        )
        $LeafSpecs = @(@(0, 1.0, 1), @(55, 0.9, -1), @(115, 0.95, 1), @(175, 0.85, -1), @(240, 1.0, 1), @(300, 0.88, -1))
    }
    'daisies' {
        # ref 2: three cream daisies with yellow centres in a loose row, middle one highest; red pot
        $FlowerSpecs = @(
            @{ head = @(3, -19, 62);  n = @(0.6, -0.35, 0.72);  spin = 5;   petal = $cream; centre = $yellow; bend = @(1, -2);  size = 0.9;  petals = 5 }
            @{ head = @(-3, 1, 72);   n = @(0.6, 0, 0.8);       spin = 30;  petal = $cream; centre = $yellow; bend = @(2, 1);   size = 1.0;  petals = 5 }
            @{ head = @(2, 20, 64);   n = @(0.6, 0.35, 0.72);   spin = -20; petal = $cream; centre = $yellow; bend = @(-1, 2);  size = 0.95; petals = 5 }
        )
        $LeafSpecs = @(@(10, 1.05, 1), @(80, 0.95, -1), @(150, 1.0, 1), @(220, 0.9, -1), @(290, 1.0, 1))
    }
    'blue' {
        # ref 3: one big coral front-left, one smaller coral to the right, both yellow-centred; blue pot
        $FlowerSpecs = @(
            @{ head = @(6, -9, 66);   n = @(0.62, -0.2, 0.76);  spin = 12;  petal = $coral; centre = $yellow; bend = @(2, -1);  size = 1.15; petals = 5 }
            @{ head = @(1, 17, 58);   n = @(0.55, 0.38, 0.74);  spin = -25; petal = $coral; centre = $yellow; bend = @(-1, 2);  size = 0.85; petals = 5 }
        )
        $LeafSpecs = @(@(0, 0.9, 1), @(70, 0.85, -1), @(145, 0.95, 1), @(215, 0.8, -1), @(290, 0.9, 1))
    }
}
# ---- heads must not clip each other (user: the daisies clipped badly) ----
# Sample each head's disc (centre + rings at half and full radius) and measure every sample's gap to every other
# head's disc. A sample on A's mid-plane needs gap >= PetalHH (A's own half-thickness) + HeadGap, where HeadGap
# also covers the cup (outer petal edges lift ~R*sin(12deg) toward n). While any pair clips, push the two heads
# apart along the line between their centres, the smaller head moving more. Stalks + leaves are built from the
# final head positions. 'default' is the user-approved pot and is left exactly as it was.
$HeadGap = 3.5
$MinHeadZ = 50     # heads never pushed below this (they'd sink into the leaves)
$SepMode = if ($Variant -eq 'daisies') { 'height' } else { 'spread' }   # daisies: user wanted them kept close, so raise/lower
function HeadInfo($f) { @{ c = @([double]$f.head[0], [double]$f.head[1], [double]$f.head[2]); n = (Norm $f.n); R = (($PetalOff + $PetalR) * $f.size) } }
function HeadClip($a, $b) {   # worst clearance deficit of disc a against disc b (> 0 = clipping)
    $u = Norm @((-$a.n[1]), $a.n[0], 0); if ([double]::IsNaN($u[0])) { $u = @(1.0, 0, 0) }
    $v = @(($a.n[1] * $u[2] - $a.n[2] * $u[1]), ($a.n[2] * $u[0] - $a.n[0] * $u[2]), ($a.n[0] * $u[1] - $a.n[1] * $u[0]))
    $worst = -1e9
    foreach ($rf in 0, 0.5, 1.0) {
        $steps = if ($rf -eq 0) { 1 } else { 16 }
        for ($k = 0; $k -lt $steps; $k++) {
            $t = 2 * [math]::PI * $k / $steps; $rr = $a.R * $rf
            $p = @(($a.c[0] + $rr * ([math]::Cos($t) * $u[0] + [math]::Sin($t) * $v[0])),
                   ($a.c[1] + $rr * ([math]::Cos($t) * $u[1] + [math]::Sin($t) * $v[1])),
                   ($a.c[2] + $rr * ([math]::Cos($t) * $u[2] + [math]::Sin($t) * $v[2])))
            $def = ($PetalHH + $HeadGap) - (DiscGap $p $b).gap
            if ($def -gt $worst) { $worst = $def }
        }
    }
    $worst
}
if ($Variant -ne 'default') {
    for ($it = 0; $it -lt 200; $it++) {
        $moved = $false
        for ($i = 0; $i -lt $FlowerSpecs.Count; $i++) {
            for ($j = $i + 1; $j -lt $FlowerSpecs.Count; $j++) {
                $fa = $FlowerSpecs[$i]; $fb = $FlowerSpecs[$j]
                $ha = HeadInfo $fa; $hb = HeadInfo $fb
                $def = [math]::Max((HeadClip $ha $hb), (HeadClip $hb $ha))
                if ($def -le 0) { continue }
                $wa = $fb.size / ($fa.size + $fb.size); $wb = 1 - $wa      # smaller head moves more
                $st = [math]::Min($def, 1.0)
                if ($SepMode -eq 'height') {
                    # HEIGHT ONLY (user, daisies: keep them close together sideways, fix clips by raising/lowering):
                    # the higher head goes up, the lower one down; nothing sinks below MinHeadZ (its partner rises instead)
                    $sa = if ($ha.c[2] -ge $hb.c[2]) { 1 } else { -1 }
                    $za = $ha.c[2] + $sa * $st * $wa; $zb = $hb.c[2] - $sa * $st * $wb
                    if ($za -lt $MinHeadZ) { $zb += ($MinHeadZ - $za); $za = $MinHeadZ }
                    if ($zb -lt $MinHeadZ) { $za += ($MinHeadZ - $zb); $zb = $MinHeadZ }
                    $fa.head = @($ha.c[0], $ha.c[1], $za)
                    $fb.head = @($hb.c[0], $hb.c[1], $zb)
                } else {
                    # SPREAD: push apart along the line between the centres
                    $d = Norm @(($ha.c[0] - $hb.c[0]), ($ha.c[1] - $hb.c[1]), ($ha.c[2] - $hb.c[2]))
                    $fa.head = @(($ha.c[0] + $d[0] * $st * $wa), ($ha.c[1] + $d[1] * $st * $wa), ($ha.c[2] + $d[2] * $st * $wa))
                    $fb.head = @(($hb.c[0] - $d[0] * $st * $wb), ($hb.c[1] - $d[1] * $st * $wb), ($hb.c[2] - $d[2] * $st * $wb))
                }
                $moved = $true
            }
        }
        if (-not $moved) { break }
    }
    foreach ($f in $FlowerSpecs) { Write-Host ("  head {0}: ({1:N1}, {2:N1}, {3:N1})" -f $f.petal, $f.head[0], $f.head[1], $f.head[2]) }
    for ($i = 0; $i -lt $FlowerSpecs.Count; $i++) { for ($j = $i + 1; $j -lt $FlowerSpecs.Count; $j++) {
        $ha = HeadInfo $FlowerSpecs[$i]; $hb = HeadInfo $FlowerSpecs[$j]
        $def = [math]::Max((HeadClip $ha $hb), (HeadClip $hb $ha))
        if ($def -gt 0.05) { Write-Warning ("heads {0} and {1} still clip by {2:N1}" -f $i, $j, $def) }
    } }
}
foreach ($f in $FlowerSpecs) { $Heads += @{ c = $f.head; n = (Norm $f.n); R = (($PetalOff + $PetalR) * $f.size) } }
# leaves: copies of the user's leaf round the pot; every leaf then veers away from any head its tip gets close to
foreach ($l in $LeafSpecs) { $brushes += , (Leaf ($LeafAz0 + $l[0]) $l[1] $l[2]) }
if ($Variant -eq 'default') {
    # the leaf under the yellow flower: hand-placed by the user (shorter, slimmer, tucked under) - exact, no auto-veer
    $brushes += , (B @{ Shape = 'Spline'; Size = '1,1,1'; Blend = 0.3; Color = $green
                        Points = @('1.24868774,-5.7827158,22.2045403,5.79251003', '4.47546482,-10.3358164,29.8014412,5.02732801',
                                   '7.72207737,-13.5808115,34.0345192,3.75202417', '11.7247334,-17.6351337,40.0921936,2.2216599') })
}
foreach ($f in $FlowerSpecs) {
    $np = if ($f.ContainsKey('petals')) { $f.petals } else { 0 }
    $brushes += Flower $f.head $f.n $f.spin $f.petal $f.centre $f.bend $f.size $np
}
# soil LAST (user): its Blend 2.9 smooths it into the bases of every leaf and stalk above it
$brushes += , (B @{ Shape = 'Cylinder'; Position = (V3 0 0 ($SoilZ - 2)); Size = '24,24,3'; Rounding = 2.5; Blend = 2.9; Color = $soil })

Write-SdfProp $Out $Name $brushes '0,0,22'
