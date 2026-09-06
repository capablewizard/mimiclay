# Generates a family of pot variation prefabs from prefabs/props/kitchen/pot.prefab.
#
# The pot prefab is used as the TEMPLATE: its full component stack (SdfSculpture,
# shadow ModelRenderer, SdfRaymarchRenderer, SdfCollider, SculptBounds, DamageProfile,
# ClayBoil) is kept verbatim; only the Name, the GUIDs and the Brushes array change.
# GUIDs are deterministic (MD5 of "<potname>:<template guid>") so re-running the
# script rewrites identical files - no churn.
#
# Authoring vocabulary (matches sdf_eval.hlsl / SdfBrush):
#   Sphere    Size = ellipsoid radii x/y/z
#   Cylinder  Size.x = radius, Size.z = half-height (y ignored)
#   Cone      BASE-pivot: base radius Size.x at Position, apex at +2*Size.z along
#             local Z; rot (1,0,0,0) flips it apex-down
#   Spline    Points are SCULPTURE-LOCAL "x,y,z,w" (w = radius); Position unused
#   Cutout    shell-subtract groove at the brush BOUNDARY (Blend = groove size,
#             0 = pure recolour) + repaint inside: stamped dots / bands / grooves
#
# Radial stamps reuse gen-pot-polkadot's quaternion: q = Rz(theta) * Ry(90) aims the
# brush's local +Z along the outward radial.

$ErrorActionPreference = 'Stop'
$Src    = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/Props/Kitchen/pot.prefab'
$OutDir = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/Props/Kitchen'
$inv    = [Globalization.CultureInfo]::InvariantCulture
function F([double]$v) { $v.ToString('0.#########', $inv) }

$md5 = [Security.Cryptography.MD5]::Create()
function DetGuid([string]$seed) {
    $h = $md5.ComputeHash([Text.Encoding]::UTF8.GetBytes($seed))
    return ([Guid]::new($h)).ToString()
}

# ---- quaternions (x,y,z,w) --------------------------------------------------
$D2R = [math]::PI / 180.0
function QRadial([double]$deg) {
    # Rz(theta) * Ry(90): local +Z -> outward radial at angle theta around Z.
    $half = 1.0 / [math]::Sqrt(2.0)
    $th = $deg * $D2R
    $hs = [math]::Sin($th * 0.5); $hc = [math]::Cos($th * 0.5)
    return @( (-$half * $hs), ($half * $hc), ($half * $hs), ($half * $hc) )
}
function QAxis([double[]]$axis, [double]$deg) {
    $a = $deg * $D2R * 0.5
    $s = [math]::Sin($a)
    $l = [math]::Sqrt($axis[0]*$axis[0] + $axis[1]*$axis[1] + $axis[2]*$axis[2])
    return @( ($axis[0]/$l*$s), ($axis[1]/$l*$s), ($axis[2]/$l*$s), ([math]::Cos($a)) )
}
$QId    = @(0.0, 0.0, 0.0, 1.0)
$QFlipX = @(1.0, 0.0, 0.0, 0.0)          # 180deg about X: cones point apex-down
$QText  = @(0.5, 0.5, 0.5, 0.5)          # text quad faces a viewer on +X, upright

# ---- brush emitter ----------------------------------------------------------
function Brush([hashtable]$p) {
    $shape = $p.Shape
    $op    = 'Add';      if ($p.ContainsKey('Op'))       { $op = $p.Op }
    $xs    = 'Triangle'; if ($p.ContainsKey('XSection')) { $xs = $p.XSection }
    $text  = 'clay';     if ($p.ContainsKey('Text'))     { $text = $p.Text }
    $pos   = @(0.0,0.0,0.0); if ($p.ContainsKey('Pos'))  { $pos = $p.Pos }
    $rot   = $QId;       if ($p.ContainsKey('Rot'))      { $rot = $p.Rot }
    $size  = @(10.0,10.0,10.0); if ($p.ContainsKey('Size')) { $size = $p.Size }
    $blend = 4.0;        if ($p.ContainsKey('Blend'))    { $blend = $p.Blend }
    $round = 0.75;       if ($p.ContainsKey('Round'))    { $round = $p.Round }
    $col   = $p.Col
    $met   = 0;          if ($p.ContainsKey('Met'))      { $met = $p.Met }
    $rough = 0.5;        if ($p.ContainsKey('Rough'))    { $rough = $p.Rough }
    $mx    = 'false';    if ($p.MirrorX)                 { $mx = 'true' }
    $curv  = 1.0;        if ($p.ContainsKey('Curv'))     { $curv = $p.Curv }
    $pts   = @();        if ($p.ContainsKey('Points'))   { $pts = $p.Points }
    $sppr  = 'false';    if ($pts.Count -gt 0)           { $sppr = 'true' }
    $closed = 'false';   if ($p.Closed)                  { $closed = 'true' }

    $sx = [double]$size[0]; $sy = [double]$size[1]; $sz = [double]$size[2]

    # Conservative bounding radius per shape (mirrors SdfBrush.BoundingRadius).
    switch ($shape) {
        'Sphere'   { $br = [math]::Max($sx, [math]::Max($sy, $sz)) }
        'Box'      { $br = [math]::Sqrt($sx*$sx + $sy*$sy + $sz*$sz) }
        'Cylinder' { $br = [math]::Sqrt($sx*$sx + $sz*$sz) }
        'Cone'     { $br = [math]::Sqrt($sx*$sx + 4.0*$sz*$sz) }
        'Extruded' { $br = [math]::Sqrt($sx*$sx + $sz*$sz) }
        'Text'     { $br = [math]::Sqrt($sx*$sx + $sy*$sy + $sz*$sz) }
        'Spline'   {
            # centroid-relative reach of the control points
            $cx = 0.0; $cy = 0.0; $cz = 0.0
            foreach ($q in $pts) { $cx += $q[0]; $cy += $q[1]; $cz += $q[2] }
            $n = [math]::Max($pts.Count, 1)
            $cx /= $n; $cy /= $n; $cz /= $n
            $br = 1.0
            foreach ($q in $pts) {
                $dx = $q[0]-$cx; $dy = $q[1]-$cy; $dz = $q[2]-$cz
                $r = [math]::Sqrt($dx*$dx + $dy*$dy + $dz*$dz) + $q[3]
                if ($r -gt $br) { $br = $r }
            }
        }
        default    { $br = [math]::Max($sx, [math]::Max($sy, $sz)) }
    }
    $br += $blend * 0.25

    $lc = '0,0,0'
    if ($shape -eq 'Cone') { $lc = '0,0,' + (F $sz) }

    if ($pts.Count -eq 0) {
        $ptsJson = '[]'
    } else {
        $lines = @()
        foreach ($q in $pts) { $lines += ('              "' + (F $q[0]) + ',' + (F $q[1]) + ',' + (F $q[2]) + ',' + (F $q[3]) + '"') }
        $ptsJson = "[`n" + ($lines -join ",`n") + "`n            ]"
    }

    return @"
          {
            "Shape": "$shape",
            "Operation": "$op",
            "CrossSection": "$xs",
            "Text": "$text",
            "Font": "Super Joyful",
            "Enabled": true,
            "Position": "$(F $pos[0]),$(F $pos[1]),$(F $pos[2])",
            "Rotation": "$(F $rot[0]),$(F $rot[1]),$(F $rot[2]),$(F $rot[3])",
            "Size": "$(F $sx),$(F $sy),$(F $sz)",
            "Points": $ptsJson,
            "Curvature": $(F $curv),
            "SplineClosed": $closed,
            "SplinePerPointRadius": $sppr,
            "Slice": 0,
            "SlicePlaneN": 1,
            "LocalCentre": "$lc",
            "Blend": $(F $blend),
            "Rounding": $(F $round),
            "Color": "$col",
            "Metallic": $(F $met),
            "Roughness": $(F $rough),
            "MirrorX": $mx,
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
}

# Ring of radial cutout stamps (polka-dot style) at height z on wall radius wallR.
function DotRing([double]$wallR, [double]$z, [int]$count, [double]$dotR, [string]$col, [double]$offDeg) {
    $out = @()
    $step = 360.0 / $count
    for ($i = 0; $i -lt $count; $i++) {
        $deg = $offDeg + $i * $step
        $th = $deg * $D2R
        $out += Brush @{ Shape='Cylinder'; Op='Cutout';
            Pos=@( ($wallR*[math]::Cos($th)), ($wallR*[math]::Sin($th)), $z );
            Rot=(QRadial $deg); Size=@( $dotR, $dotR, 2.0 ); Blend=1.4; Round=0; Col=$col }
    }
    return $out
}

# Painted band with scored edges: large-radius thin cutout cylinder - its flat caps
# cross the wall as two groove rings, the space between repaints in the band colour.
function BandRing([double]$r, [double]$z, [double]$halfW, [string]$col) {
    return Brush @{ Shape='Cylinder'; Op='Cutout'; Pos=@(0.0, 0.0, $z);
        Size=@( $r, $r, $halfW ); Blend=1.0; Round=0; Col=$col }
}

# ---- palette ----------------------------------------------------------------
$terra     = '0.72,0.38,0.22,1'
$terraDark = '0.5,0.25,0.14,1'
$terraDeep = '0.35,0.18,0.1,1'
$cream     = '0.93,0.87,0.74,1'
$gold      = '0.8,0.68627,0.34902,1'

# ---- the pots ---------------------------------------------------------------
$pots = [ordered]@{}

# 1. Tall teal urn with cream loop handles - large (~80 tall)
$teal = '0.22,0.55,0.55,1'; $tealDark = '0.14,0.38,0.4,1'; $tealDeep = '0.1,0.28,0.3,1'
$pots['pot_urn'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-34.0); Size=@(16.0,16.0,5.0); Blend=4; Round=3;  Col=$tealDark })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-26.0); Size=@(10.0,10.0,6.0); Blend=6; Round=2;  Col=$teal })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-2.0);  Size=@(24.0,24.0,20.0); Blend=8; Round=12; Col=$teal })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,16.0);  Size=@(24.0,24.0,12.0); Blend=8; Col=$teal })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,30.0);  Size=@(11.0,11.0,7.0); Blend=6; Round=2;  Col=$teal })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,39.0);  Size=@(15.0,15.0,3.0); Blend=3; Round=2;  Col=$cream })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,42.0); Size=@(8.0,8.0,8.0); Blend=1; Round=0; Col=$tealDeep })
    (Brush @{ Shape='Spline'; MirrorX=$true; Blend=3; Round=0.75; Col=$cream; Size=@(12.0,12.0,12.0);
        Points=@( @(16.0,0.0,26.0,3.2), @(26.0,0.0,22.0,2.8), @(26.0,0.0,8.0,2.8), @(19.0,0.0,2.0,3.2) ) })
    (BandRing 27.0 -10.0 3.0 $cream)
)

# 2. Terracotta amphora: egg body, pointed foot, spline handles - large (~77 tall)
$pots['pot_amphora'] = @(
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,2.0);   Size=@(21.0,21.0,28.0); Blend=6; Col=$terra })
    (Brush @{ Shape='Cone'; Rot=$QFlipX; Pos=@(0.0,0.0,-20.0); Size=@(9.0,9.0,6.0); Blend=6; Col=$terra })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-32.0); Size=@(12.0,12.0,2.5); Blend=3; Round=1.5; Col=$terraDark })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,32.0);  Size=@(8.0,8.0,8.0); Blend=6; Round=1; Col=$terra })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,41.0);  Size=@(12.0,12.0,2.5); Blend=2.5; Round=1.5; Col=$terraDark })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,42.0); Size=@(5.5,5.5,6.0); Blend=1; Round=0; Col=$terraDeep })
    (Brush @{ Shape='Spline'; MirrorX=$true; Blend=2.5; Round=0.75; Col=$terra; Size=@(12.0,12.0,12.0);
        Points=@( @(10.0,0.0,36.0,2.6), @(19.0,0.0,32.0,2.4), @(19.0,0.0,20.0,2.4), @(14.0,0.0,14.0,2.6) ) })
    (BandRing 24.0 6.0 1.8 '0.45,0.2,0.12,1')
    (BandRing 24.0 -10.0 1.8 '0.45,0.2,0.12,1')
)

# 3. White porcelain ginger jar: blue bands + dots, gold knob - medium (~64 tall)
$white = '0.92,0.93,0.9,1'; $china = '0.2,0.35,0.7,1'
$pots['pot_ginger'] = @(
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,-2.0);  Size=@(26.0,26.0,22.0); Blend=6; Col=$white; Rough=0.25 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-24.0); Size=@(12.0,12.0,3.5); Blend=4; Round=1.5; Col=$white; Rough=0.25 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,20.0);  Size=@(11.0,11.0,4.0); Blend=5; Round=1; Col=$white; Rough=0.25 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,27.0);  Size=@(13.5,13.5,4.0); Blend=3; Round=3; Col=$white; Rough=0.25 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,33.0);  Size=@(4.5,4.5,4.0); Blend=2.5; Col=$gold; Met=1; Rough=0.35 })
    (BandRing 29.0 10.0 2.0 $china)
    (BandRing 29.0 -14.0 2.0 $china)
) + (DotRing 26.0 -2.0 8 3.5 $china 0.0)

# 4. Coral glazed teapot: spline spout + handle, gold knob - medium (~50 tall)
$coral = '0.9,0.45,0.35,1'
$pots['pot_teapot'] = @(
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,-4.0);  Size=@(25.0,25.0,19.0); Blend=6; Col=$coral; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-24.0); Size=@(13.0,13.0,3.5); Blend=4; Round=1.5; Col=$coral; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,16.0);  Size=@(14.0,14.0,3.5); Blend=4; Round=3; Col=$cream })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,23.0);  Size=@(4.5,4.5,4.0); Blend=2; Col=$gold; Met=1; Rough=0.35 })
    (Brush @{ Shape='Spline'; Blend=4; Round=0.75; Col=$coral; Rough=0.3; Size=@(12.0,12.0,12.0);
        Points=@( @(20.0,0.0,-6.0,5.5), @(30.0,0.0,0.0,4.0), @(35.0,0.0,8.0,3.2) ) })
    (Brush @{ Shape='Spline'; Blend=3; Round=0.75; Col=$coral; Rough=0.3; Size=@(12.0,12.0,12.0);
        Points=@( @(-20.0,0.0,4.0,3.0), @(-32.0,0.0,0.0,2.7), @(-32.0,0.0,-12.0,2.7), @(-20.0,0.0,-16.0,3.0) ) })
)

# 5. Deep blue glazed pitcher: cream lip, pour spout, loop handle - medium (~57 tall)
$navy = '0.18,0.28,0.55,1'; $navyDeep = '0.1,0.16,0.32,1'
$pots['pot_pitcher'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-10.0); Size=@(18.0,18.0,14.0); Blend=7; Round=9; Col=$navy; Rough=0.3 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,-8.0);  Size=@(20.0,20.0,11.0); Blend=8; Col=$navy; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,10.0);  Size=@(10.0,10.0,7.0); Blend=7; Round=1; Col=$navy; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,19.0);  Size=@(13.0,13.0,2.5); Blend=2.5; Round=1.5; Col=$cream })
    (Brush @{ Shape='Sphere';   Pos=@(12.0,0.0,20.0); Size=@(5.0,3.5,4.0); Blend=4; Col=$cream })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,21.0); Size=@(7.0,7.0,5.0); Blend=1; Round=0; Col=$navyDeep })
    (Brush @{ Shape='Spline'; Blend=2.5; Round=0.75; Col=$navy; Rough=0.3; Size=@(12.0,12.0,12.0);
        Points=@( @(-11.0,0.0,16.0,2.8), @(-22.0,0.0,12.0,2.5), @(-22.0,0.0,-4.0,2.5), @(-14.0,0.0,-10.0,2.8) ) })
    (BandRing 21.5 -18.0 2.0 $cream)
)

# 6. Wide sage planter bowl: cream interior, stamped dots - squat (~36 tall, 64 wide)
$sage = '0.55,0.65,0.45,1'; $sageDark = '0.4,0.5,0.32,1'
$pots['pot_bowl'] = @(
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,0.0);   Size=@(32.0,32.0,18.0); Blend=6; Col=$sage })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,22.0); Size=@(40.0,40.0,8.0); Blend=1.5; Round=0; Col=$sage })
    (Brush @{ Shape='Sphere';   Op='Subtract'; Pos=@(0.0,0.0,8.0); Size=@(26.0,26.0,13.0); Blend=2; Col='0.9,0.85,0.7,1' })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-18.0); Size=@(13.0,13.0,4.0); Blend=4; Round=2; Col=$sageDark })
    (BandRing 34.0 4.0 2.2 $cream)
) + (DotRing 28.7 -8.0 6 4.0 $cream 30.0)

# 7. Modern charcoal planter with cream stripes - medium (~47 tall)
$charcoal = '0.25,0.25,0.28,1'
$pots['pot_striped'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,0.0);   Size=@(22.0,22.0,20.0); Blend=6; Round=8; Col=$charcoal })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,25.0); Size=@(28.0,28.0,6.0); Blend=1.2; Round=0; Col=$charcoal })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,17.0); Size=@(17.0,17.0,9.0); Blend=0.8; Round=0; Col='0.12,0.12,0.14,1' })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-23.0); Size=@(16.0,16.0,4.0); Blend=4; Round=2; Col=$charcoal })
    (BandRing 25.0 -14.0 2.5 $cream)
    (BandRing 25.0 -4.0 2.5 $cream)
    (BandRing 25.0 6.0 2.5 $cream)
)

# 8. Honey pot: amber interior, drips, stamped "honey" - small (~42 tall)
$tan = '0.82,0.6,0.34,1'; $amber = '0.92,0.62,0.18,1'
$pots['pot_honey'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-6.0);  Size=@(19.0,19.0,11.0); Blend=7; Round=8; Col=$tan })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,-4.0);  Size=@(21.0,21.0,9.0); Blend=8; Col=$tan })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,8.0);   Size=@(14.0,14.0,3.0); Blend=5; Round=1; Col=$tan })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,13.0);  Size=@(19.0,19.0,3.5); Blend=3; Round=2.5; Col=$tan })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,16.0); Size=@(13.0,13.0,5.0); Blend=1; Round=0; Col=$amber; Rough=0.25 })
    (Brush @{ Shape='Sphere';   Pos=@(17.5,4.0,9.0);  Size=@(5.0,3.5,6.0); Blend=3; Col=$amber; Rough=0.22 })
    (Brush @{ Shape='Sphere';   Pos=@(-14.0,-11.0,7.0); Size=@(4.0,3.0,8.0); Blend=3; Col=$amber; Rough=0.22 })
    (Brush @{ Shape='Sphere';   Pos=@(2.0,18.0,10.0); Size=@(3.0,2.5,5.0); Blend=3; Col=$amber; Rough=0.22 })
    (Brush @{ Shape='Text'; Op='Cutout'; Text='honey'; Pos=@(19.5,0.0,-4.0); Rot=$QText;
        Size=@(9.0,4.5,2.0); Blend=0.6; Round=0.4; Col='0.4,0.26,0.12,1' })
)

# 9. Small terracotta cactus pot - small (~52 tall with cactus)
$cactus = '0.35,0.62,0.32,1'
$pots['pot_cactus'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-18.0); Size=@(15.0,15.0,8.0); Blend=5; Round=5; Col=$terra })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-7.0);  Size=@(18.0,18.0,4.5); Blend=4; Round=2; Col=$terra })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-3.0);  Size=@(19.0,19.0,2.0); Blend=2; Round=1.5; Col=$terraDark })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-1.0);  Size=@(15.0,15.0,1.5); Blend=1.5; Round=0.75; Col='0.28,0.2,0.14,1' })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,10.0);  Size=@(7.5,7.5,11.0); Blend=5; Round=7; Col=$cactus })
    (Brush @{ Shape='Spline'; MirrorX=$true; Blend=2.5; Round=0.75; Col=$cactus; Size=@(12.0,12.0,12.0);
        Points=@( @(9.0,0.0,8.0,3.4), @(15.0,0.0,10.0,3.2), @(15.0,0.0,18.0,3.0) ) })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,23.0);  Size=@(4.0,4.0,3.5); Blend=2.5; Col='0.9,0.5,0.65,1' })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,25.8);  Size=@(1.8,1.8,1.6); Blend=1; Col='0.95,0.8,0.3,1' })
)

# 10. Blush pot with a stamped face - medium (~45 tall)
$pink = '0.9,0.62,0.6,1'
$eyeCol = '0.25,0.16,0.12,1'
$e = 20.0 * $D2R   # eyes at +/-20deg around the front
$ex = 24.0 * [math]::Cos($e); $ey = 24.0 * [math]::Sin($e)
$pots['pot_face'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,0.0);   Size=@(24.0,24.0,18.0); Blend=6; Round=12; Col=$pink })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,23.0); Size=@(30.0,30.0,6.0); Blend=1.2; Round=0; Col='0.95,0.72,0.68,1' })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,14.0); Size=@(19.0,19.0,8.0); Blend=0.8; Round=0; Col='0.62,0.38,0.36,1' })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-21.0); Size=@(17.0,17.0,4.0); Blend=4; Round=2; Col=$pink })
    (Brush @{ Shape='Sphere'; Op='Cutout'; Pos=@( $ex, $ey, 6.0 );      Size=@(2.8,2.8,2.8); Blend=0.8; Round=0; Col=$eyeCol })
    (Brush @{ Shape='Sphere'; Op='Cutout'; Pos=@( $ex, (-$ey), 6.0 );   Size=@(2.8,2.8,2.8); Blend=0.8; Round=0; Col=$eyeCol })
    (Brush @{ Shape='Cylinder'; Op='Cutout'; Pos=@( (24.0*[math]::Cos(38.0*$D2R)), (24.0*[math]::Sin(38.0*$D2R)), 1.0 );
        Rot=(QRadial 38.0); Size=@(3.2,3.2,1.5); Blend=0; Round=0; Col='0.95,0.5,0.5,1' })
    (Brush @{ Shape='Cylinder'; Op='Cutout'; Pos=@( (24.0*[math]::Cos(-38.0*$D2R)), (24.0*[math]::Sin(-38.0*$D2R)), 1.0 );
        Rot=(QRadial -38.0); Size=@(3.2,3.2,1.5); Blend=0; Round=0; Col='0.95,0.5,0.5,1' })
    (Brush @{ Shape='Spline'; Op='Cutout'; Blend=0.9; Round=0.75; Col=$eyeCol; Size=@(12.0,12.0,12.0);
        Points=@( @(23.8,-6.0,-1.0,1.2), @(24.6,0.0,-4.0,1.2), @(23.8,6.0,-1.0,1.2) ) })
)

# ---- cookware wave: kitchen COOKING pots ------------------------------------
$steel      = '0.72,0.73,0.76,1'
$steelLight = '0.82,0.83,0.86,1'
$steelDark  = '0.5,0.51,0.55,1'
$black      = '0.15,0.15,0.16,1'
$copper     = '0.85,0.5,0.32,1'
$iron       = '0.2,0.2,0.22,1'

# 11. Stainless stockpot: tall drum, twin loop handles, flat lid - large (~60 tall)
$pots['pot_stockpot'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,0.0);  Size=@(24.0,24.0,24.0); Blend=4; Round=3; Col=$steel; Met=1; Rough=0.35 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,23.0); Size=@(25.0,25.0,1.5); Blend=2; Round=1; Col=$steelLight; Met=1; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,26.5); Size=@(25.5,25.5,2.2); Blend=2; Round=1.5; Col=$steel; Met=1; Rough=0.35 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,30.5); Size=@(3.5,3.5,3.0); Blend=2; Col=$black; Rough=0.6 })
    (Brush @{ Shape='Spline'; MirrorX=$true; Blend=2; Round=0.75; Col=$steelDark; Met=1; Rough=0.35; Size=@(12.0,12.0,12.0);
        Points=@( @(24.0,-7.0,14.0,1.8), @(29.0,0.0,14.0,1.8), @(24.0,7.0,14.0,1.8) ) })
)

# 12. Stainless saucepan: open, hollow, long black handle - medium (~r18)
$pots['pot_saucepan'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,0.0);  Size=@(18.0,18.0,10.0); Blend=3; Round=2.5; Col=$steel; Met=1; Rough=0.35 })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,5.0); Size=@(15.5,15.5,8.0); Blend=0.8; Round=0; Col=$steelDark; Met=1; Rough=0.4 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,9.5);  Size=@(19.0,19.0,1.2); Blend=1.5; Round=0.8; Col=$steelLight; Met=1; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Rot=(QAxis @(0.0,1.0,0.0) 80.0); Pos=@(29.0,0.0,10.0); Size=@(2.2,2.2,13.0); Blend=2; Round=1.5; Col=$black; Rough=0.6 })
)

# 13. Flame-orange enamel dutch oven: squat, lid dome, gold knob, ear handles
$flame = '0.9,0.35,0.15,1'
$pots['pot_dutchoven'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-4.0); Size=@(26.0,26.0,12.0); Blend=5; Round=6; Col=$flame; Rough=0.3 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,-6.0); Size=@(27.0,27.0,10.0); Blend=7; Col=$flame; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,10.0); Size=@(27.0,27.0,3.0); Blend=3; Round=2.5; Col=$flame; Rough=0.3 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,12.0); Size=@(24.0,24.0,6.0); Blend=5; Col=$flame; Rough=0.3 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,19.0); Size=@(4.0,4.0,3.2); Blend=2; Col=$gold; Met=1; Rough=0.35 })
    (Brush @{ Shape='Spline'; MirrorX=$true; Blend=2; Round=0.75; Col=$flame; Rough=0.3; Size=@(12.0,12.0,12.0);
        Points=@( @(26.0,-6.0,2.0,2.0), @(30.0,0.0,2.0,2.0), @(26.0,6.0,2.0,2.0) ) })
    (BandRing 30.0 7.5 0.8 '0.6,0.2,0.08,1')
)

# 14. Green enamel casserole (the collage's green set): squat, steel knob
$green = '0.35,0.55,0.42,1'
$pots['pot_casserole'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-2.0); Size=@(24.0,24.0,10.0); Blend=4; Round=4; Col=$green; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,10.0); Size=@(25.0,25.0,2.5); Blend=2.5; Round=2; Col=$green; Rough=0.3 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,11.5); Size=@(21.0,21.0,5.0); Blend=5; Col=$green; Rough=0.3 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,17.0); Size=@(3.8,3.8,3.0); Blend=2; Col=$steel; Met=1; Rough=0.35 })
    (Brush @{ Shape='Spline'; MirrorX=$true; Blend=2; Round=0.75; Col=$green; Rough=0.3; Size=@(12.0,12.0,12.0);
        Points=@( @(24.0,-6.0,2.0,2.0), @(28.0,0.0,2.0,2.0), @(24.0,6.0,2.0,2.0) ) })
    (BandRing 28.0 7.0 0.8 '0.24,0.4,0.3,1')
)

# 15. Unglazed terracotta cooking pot with lid (the big clay one in the collage)
$pots['pot_claypot'] = @(
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,-2.0);  Size=@(25.0,25.0,17.0); Blend=6; Col=$terra; Rough=0.55 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-20.0); Size=@(13.0,13.0,3.0); Blend=4; Round=1.5; Col=$terraDark; Rough=0.55 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,13.0);  Size=@(16.0,16.0,3.0); Blend=4; Round=1.5; Col=$terra; Rough=0.55 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,16.0);  Size=@(16.0,16.0,7.0); Blend=4; Col='0.62,0.32,0.18,1'; Rough=0.55 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,24.5);  Size=@(5.0,5.0,1.8); Blend=2.5; Round=1.5; Col='0.62,0.32,0.18,1'; Rough=0.55 })
    (BandRing 28.0 2.0 1.0 $terraDark)
)

# 16. Donabe: dark glazed clay bowl, cream lid with a ring knob - squat
$donabe = '0.32,0.22,0.16,1'
$pots['pot_donabe'] = @(
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,-4.0); Size=@(26.0,26.0,14.0); Blend=6; Col=$donabe; Rough=0.35 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,6.0);  Size=@(27.0,27.0,2.0); Blend=3; Round=1.5; Col=$donabe; Rough=0.35 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,8.0);  Size=@(23.0,23.0,9.0); Blend=5; Col=$cream; Rough=0.35 })
    (Brush @{ Shape='Spline'; Closed=$true; Blend=2.5; Round=0.75; Col=$cream; Rough=0.35; Size=@(12.0,12.0,12.0);
        Points=@( @(4.5,0.0,18.5,1.6), @(0.0,4.5,18.5,1.6), @(-4.5,0.0,18.5,1.6), @(0.0,-4.5,18.5,1.6) ) })
)

# 17. Copper saucepan: tin-lined, brass handle, rivets - medium (~r16)
$pots['pot_copperpot'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,0.0);  Size=@(16.0,16.0,11.0); Blend=3; Round=2.5; Col=$copper; Met=1; Rough=0.28 })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,5.0); Size=@(13.8,13.8,9.0); Blend=0.7; Round=0; Col='0.78,0.79,0.8,1'; Met=1; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,10.5); Size=@(17.0,17.0,1.0); Blend=1.5; Round=0.7; Col='0.92,0.6,0.4,1'; Met=1; Rough=0.28 })
    (Brush @{ Shape='Cylinder'; Rot=(QAxis @(0.0,1.0,0.0) 78.0); Pos=@(27.0,0.0,12.0); Size=@(2.0,2.0,12.0); Blend=2; Round=1.4; Col=$gold; Met=1; Rough=0.35 })
    (Brush @{ Shape='Sphere';   Pos=@(15.8,2.6,8.0);  Size=@(1.4,1.4,1.4); Blend=1; Col=$gold; Met=1; Rough=0.35 })
    (Brush @{ Shape='Sphere';   Pos=@(15.8,-2.6,8.0); Size=@(1.4,1.4,1.4); Blend=1; Col=$gold; Met=1; Rough=0.35 })
)

# 18. Cream enamel stovetop kettle: dome body, spline spout, black arch handle
$pots['pot_kettle'] = @(
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,0.0);   Size=@(21.0,21.0,15.0); Blend=6; Col=$cream; Rough=0.3 })
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,-12.0); Size=@(17.0,17.0,3.0); Blend=5; Round=1.5; Col=$cream; Rough=0.3 })
    (Brush @{ Shape='Sphere';   Pos=@(0.0,0.0,16.0);  Size=@(3.5,3.5,3.0); Blend=2; Col=$black; Rough=0.6 })
    (Brush @{ Shape='Spline'; Blend=3.5; Round=0.75; Col=$cream; Rough=0.3; Size=@(12.0,12.0,12.0);
        Points=@( @(16.0,0.0,-2.0,4.5), @(26.0,0.0,6.0,3.0), @(30.0,0.0,12.0,2.4) ) })
    (Brush @{ Shape='Spline'; Blend=2.5; Round=0.75; Col=$black; Rough=0.6; Size=@(12.0,12.0,12.0);
        Points=@( @(-13.0,0.0,12.0,2.2), @(-8.0,0.0,22.0,2.0), @(8.0,0.0,22.0,2.0), @(13.0,0.0,12.0,2.2) ) })
)

# 19. Cast iron skillet: low, wide, long handle + helper stub
$pots['pot_skillet'] = @(
    (Brush @{ Shape='Cylinder'; Pos=@(0.0,0.0,0.0);  Size=@(22.0,22.0,4.0); Blend=2.5; Round=2; Col=$iron; Met=0.4; Rough=0.5 })
    (Brush @{ Shape='Cylinder'; Op='Subtract'; Pos=@(0.0,0.0,3.0); Size=@(19.5,19.5,3.0); Blend=0.6; Round=0; Col='0.16,0.16,0.18,1'; Met=0.4; Rough=0.5 })
    (Brush @{ Shape='Cylinder'; Rot=(QAxis @(0.0,1.0,0.0) 86.0); Pos=@(31.0,0.0,5.0); Size=@(2.2,2.2,11.0); Blend=2; Round=1.5; Col=$iron; Met=0.4; Rough=0.5 })
    (Brush @{ Shape='Spline'; Blend=1.8; Round=0.75; Col=$iron; Met=0.4; Rough=0.5; Size=@(12.0,12.0,12.0);
        Points=@( @(-22.0,-5.0,3.0,1.8), @(-26.0,0.0,3.0,1.8), @(-22.0,5.0,3.0,1.8) ) })
)

# ---- emit prefabs -----------------------------------------------------------
$template = [IO.File]::ReadAllText($Src)
$rx = [regex]'(?s)"Brushes": \[\r?\n(.*?)\r?\n        \],'
if ($rx.Matches($template).Count -ne 1) { throw 'pot.prefab: expected exactly 1 Brushes array' }

# Every GUID in the template gets a per-pot deterministic replacement.
$guidRx = [regex]'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
$templateGuids = @($guidRx.Matches($template) | ForEach-Object { $_.Value } | Select-Object -Unique)

foreach ($name in $pots.Keys) {
    $out = $template
    foreach ($g in $templateGuids) { $out = $out.Replace($g, (DetGuid ($name + ':' + $g))) }
    $out = $out.Replace('"Name": "pot"', '"Name": "' + $name + '"')

    $brushList = $pots[$name]
    $newBrushes = "`"Brushes`": [`r`n" + (($brushList | ForEach-Object { $_ }) -join ",`r`n") + "`r`n        ],"
    $out = $rx.Replace($out, { param($mm) $newBrushes }, 1)

    $out = ($out -replace "`r`n", "`n") -replace "`n", "`r`n"
    $dst = Join-Path $OutDir ($name + '.prefab')
    [IO.File]::WriteAllText($dst, $out, (New-Object Text.UTF8Encoding($false)))
    Write-Host ("Wrote {0} ({1} brushes)" -f $dst, $brushList.Count)
}
