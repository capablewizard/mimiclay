# Generates the BLOCK SET reverse-engineered from Assets/models/buildingblocks.obj:
#   - one prefab per unique shape class (17 classes) in Assets/Prefabs/BlockSet/
#   - blockset_arrangement.prefab reproducing the OBJ scene (50 instances, inline children)
#
# Shape dims / positions / yaws come from Tools/PropGen analysis of the OBJ (min-area-rect
# fit per group + vertex-cluster probes). Everything is scaled by $S = 0.35 so the set fits
# the bedroom-scene toy scale. Axis mapping OBJ(Maya, Y-up) -> s&box(Z-up):
#   sbox = (x_obj, -z_obj, y_obj); instance yaw_sbox = -yaw_fit; prefab local = (lx, -lz, ly).
# Each block prefab: plan-centre at origin, bottom resting on z=0.
#
# Brush conventions (SdfBrush.cs): Box Size=half-extents; Cylinder axis Z r=Size.x hh=Size.z;
# Cone base-pivot (apex +2*Size.z); Extruded Triangle apex +Size.y; Sphere per-axis radii,
# Slice cuts local +Z; Spline points sculpture-local "x,y,z,w".

$ErrorActionPreference = 'Stop'
$OutDir = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/BlockSet'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }
$inv = [Globalization.CultureInfo]::InvariantCulture
$S = 0.35   # global scale OBJ units -> game units

function NG { [guid]::NewGuid().ToString() }
function F([double]$v) { $v.ToString('0.######', $inv) }

# ---- quaternion helpers (return double[4] x,y,z,w) -------------------------
function QAxis([double]$ax, [double]$ay, [double]$az, [double]$deg) {
    $r = [math]::Sqrt($ax*$ax + $ay*$ay + $az*$az)
    if ($r -lt 1e-9) { return [double[]]@(0,0,0,1) }
    $ax /= $r; $ay /= $r; $az /= $r
    $h = [math]::PI * $deg / 360.0; $sn = [math]::Sin($h); $cs = [math]::Cos($h)
    [double[]]@(($ax*$sn), ($ay*$sn), ($az*$sn), $cs)
}
function QMul([double[]]$a, [double[]]$b) {  # Hamilton a*b (b applied first)
    $x = $a[3]*$b[0] + $a[0]*$b[3] + $a[1]*$b[2] - $a[2]*$b[1]
    $y = $a[3]*$b[1] - $a[0]*$b[2] + $a[1]*$b[3] + $a[2]*$b[0]
    $z = $a[3]*$b[2] + $a[0]*$b[1] - $a[1]*$b[0] + $a[2]*$b[3]
    $w = $a[3]*$b[3] - $a[0]*$b[0] - $a[1]*$b[1] - $a[2]*$b[2]
    [double[]]@($x, $y, $z, $w)
}
function QStr([double[]]$q) { "$(F $q[0]),$(F $q[1]),$(F $q[2]),$(F $q[3])" }
function RotZ([string]$rot) { QStr (QAxis 0 0 1 $rot) }

# ---- brush builder ---------------------------------------------------------
# Recipe fields: Position/Size = @(x,y,z) UNSCALED; Rotation = double[4] quat or omitted;
# Points = @(@(x,y,z,w),...) for splines. Builder applies $S to all lengths.
function B([hashtable]$o) {
    $pos = if ($o.ContainsKey('Position')) { $o.Position } else { @(0,0,0) }
    $sz  = if ($o.ContainsKey('Size'))     { $o.Size }     else { @(10,10,10) }
    $rotStr = if ($o.ContainsKey('Rotation')) { QStr $o.Rotation } else { '0,0,0,1' }
    $blend = if ($o.ContainsKey('Blend')) { [double]$o.Blend } else { 4.0 }
    $round = if ($o.ContainsKey('Rounding')) { [double]$o.Rounding } else { 2.0 }
    $ptsJson = $null
    if ($o.ContainsKey('Points')) {
        $ptsJson = @()
        foreach ($pt in $o.Points) { $ptsJson += "$(F ($pt[0]*$S)),$(F ($pt[1]*$S)),$(F ($pt[2]*$S)),$(F ($pt[3]*$S))" }
    }
    $sx = [double]$sz[0]*$S; $sy = [double]$sz[1]*$S; $szz = [double]$sz[2]*$S
    $mx = [math]::Max([math]::Abs($sx), [math]::Max([math]::Abs($sy), [math]::Abs($szz)))
    $d = [ordered]@{
        Shape = 'Box'; Operation = 'Add'; CrossSection = 'Triangle'
        Text = 'clay'; Font = 'Super Joyful'; Enabled = $true
        Position = "$(F ([double]$pos[0]*$S)),$(F ([double]$pos[1]*$S)),$(F ([double]$pos[2]*$S))"
        Rotation = $rotStr
        Size = "$(F $sx),$(F $sy),$(F $szz)"
        Points = $ptsJson; Curvature = 1; SplineClosed = $false; Slice = 0
        LocalCentre = '0,0,0'
        Blend = [double](F ([math]::Max(0.05, $blend*$S)))
        Rounding = [double](F ([math]::Max(0.75, $round*$S)))
        Color = '1,1,1,1'; Metallic = 0; Roughness = 0.5
        MirrorX = $false; MirrorY = $false; MirrorZ = $false
        IsSplineLoop = $false; BoundingRadius = [double](F ($mx*1.74 + 2))
    }
    foreach ($k in 'Shape','Operation','CrossSection','Color','Slice','Curvature') {
        if ($o.ContainsKey($k)) { $d[$k] = $o[$k] }
    }
    $d
}

# ---- palette ---------------------------------------------------------------
$red    = '0.85,0.16,0.13,1'
$blue   = '0.17,0.35,0.72,1'
$green  = '0.3,0.62,0.24,1'
$yellow = '0.96,0.78,0.13,1'
$wood   = '0.87,0.69,0.44,1'

# ============================================================================
#  SHAPE RECIPES (unscaled local units, bottom at z=0, plan-centred)
# ============================================================================
$Recipes = @{}

$Recipes['cube'] = { param($c) @(
    (B @{ Shape='Box'; Position=@(0,0,22.34); Size=@(22.34,22.34,22.34); Rounding=6; Blend=3; Color=$c })
) }
$Recipes['cylinder'] = { param($c) @(
    (B @{ Shape='Cylinder'; Position=@(0,0,22.34); Size=@(24.03,24.03,22.34); Rounding=5; Blend=3; Color=$c })
) }
$Recipes['cylinder_slim'] = { param($c) @(
    (B @{ Shape='Cylinder'; Position=@(0,0,26.7); Size=@(13.67,13.67,26.7); Rounding=4; Blend=3; Color=$c })
) }
$Recipes['plank_long'] = { param($c) @(
    (B @{ Shape='Box'; Position=@(0,0,11.95); Size=@(45.24,22.34,11.95); Rounding=6; Blend=3; Color=$c })
) }
$Recipes['plank_square'] = { param($c) @(
    (B @{ Shape='Box'; Position=@(0,0,11.95); Size=@(22.34,22.34,11.95); Rounding=6; Blend=3; Color=$c })
) }
# Thin wide panel + small parallel post (faithful to the OBJ pair; stacked 3-high in two towers).
$Recipes['panelpost'] = { param($c) @(
    (B @{ Shape='Box'; Position=@(0,13.4,22.34);  Size=@(29.04,1.7,22.34); Rounding=3; Blend=2; Color=$wood })
    (B @{ Shape='Box'; Position=@(0,-13.5,22.34); Size=@(2.6,1.6,22.34);   Rounding=3; Blend=2; Color=$c })
) }
$Recipes['roof'] = { param($c) @(
    (B @{ Shape='Extruded'; CrossSection='Triangle'; Rotation=(QAxis 1 0 0 90)
          Position=@(0,0,15.53); Size=@(29.04,15.53,22.56); Rounding=4; Blend=3; Color=$c })
) }
# Solid half-moon hump: cylinder centred below ground, floor cut.
$Recipes['archump'] = { param($c) @(
    (B @{ Shape='Cylinder'; Rotation=(QAxis 1 0 0 90); Position=@(0,0,-6.2); Size=@(37.3,37.3,22.34); Rounding=5; Blend=3; Color=$c })
    (B @{ Shape='Box'; Operation='Subtract'; Position=@(0,0,-42); Size=@(48,30,42); Blend=1.5; Color=$c })
) }
# Toy bridge: thin arched deck (cylinder shell landing at |x|~41) + two end walls.
$Recipes['bridge'] = { param($c) @(
    (B @{ Shape='Cylinder'; Rotation=(QAxis 1 0 0 90); Position=@(0,0,-8.6); Size=@(42.6,42.6,22.34); Rounding=3; Blend=2; Color=$c })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Rotation=(QAxis 1 0 0 90); Position=@(0,0,-8.6); Size=@(36.6,36.6,26); Blend=2; Color=$c })
    (B @{ Shape='Box'; Operation='Subtract'; Position=@(0,0,-45); Size=@(75,30,45); Blend=1; Color=$c })
    (B @{ Shape='Box'; Position=@(-63.7,0,22.34); Size=@(3.35,22.34,22.34); Rounding=4; Blend=2; Color=$blue })
    (B @{ Shape='Box'; Position=@(63.7,0,22.34);  Size=@(3.35,22.34,22.34); Rounding=4; Blend=2; Color=$blue })
) }
# Fence: two chevron-lattice halves (4 bars each) + mid rail + end posts, 8.7 thick.
$Recipes['fence'] = { param($c)
    $out = @(
        (B @{ Shape='Box'; Position=@(0,0,20.35); Size=@(21.5,4.35,3.95); Rounding=2; Blend=2; Color=$c })
        (B @{ Shape='Box'; Position=@(-61.7,0,20.3); Size=@(1.7,4.35,20.3); Rounding=2; Blend=2; Color=$c })
        (B @{ Shape='Box'; Position=@(61.7,0,20.3);  Size=@(1.7,4.35,20.3); Rounding=2; Blend=2; Color=$c })
    )
    # bar endpoints in the x-z(height) plane, per half; mirrored for +x
    $bars = @(
        @(-63.0,14.5,-43.5,38.5), @(-43.5,38.5,-24.0,14.5),
        @(-63.0,26.0,-43.5,1.5),  @(-43.5,1.5,-24.0,26.0)
    )
    foreach ($sgn in 1,-1) {
        foreach ($bar in $bars) {
            $x1 = $sgn*$bar[0]; $z1 = $bar[1]; $x2 = $sgn*$bar[2]; $z2 = $bar[3]
            $dx = $x2-$x1; $dz = $z2-$z1
            $len = [math]::Sqrt($dx*$dx + $dz*$dz)
            $ang = -[math]::Atan2($dz, $dx) * 180.0 / [math]::PI
            $out += (B @{ Shape='Box'; Rotation=(QAxis 0 1 0 $ang)
                Position=@((($x1+$x2)/2.0), 0, (($z1+$z2)/2.0))
                Size=@(($len/2.0), 4.35, 2.2); Rounding=2; Blend=2; Color=$wood })
        }
    }
    $out
}
# Flat plate with two rounded oval openings.
$Recipes['holedpanel'] = { param($c) @(
    (B @{ Shape='Box'; Position=@(0,0,9.15); Size=@(83.73,34.46,9.15); Rounding=6; Blend=2; Color=$c })
    (B @{ Shape='Box'; Operation='Subtract'; Position=@(-41.5,0,9.15); Size=@(8.75,13,12); Rounding=17; Blend=2; Color=$c })
    (B @{ Shape='Box'; Operation='Subtract'; Position=@(41.5,0,9.15);  Size=@(8.75,13,12); Rounding=17; Blend=2; Color=$c })
) }
# Arched vault (cylinder lying along X), floor cut, round crown hole.
$Recipes['vault'] = { param($c) @(
    (B @{ Shape='Cylinder'; Rotation=(QAxis 0 1 0 90); Position=@(0,0,-2.8); Size=@(37.8,37.8,49.08); Rounding=4; Blend=3; Color=$c })
    (B @{ Shape='Box'; Operation='Subtract'; Position=@(0,0,-42); Size=@(58,44,42); Blend=1; Color=$c })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Position=@(0,0,30); Size=@(18,18,22); Blend=2; Color=$c })
) }
# Big cube with round tunnels through all three axes.
$Recipes['holedcube'] = { param($c) @(
    (B @{ Shape='Box'; Position=@(0,0,42.25); Size=@(42.25,42.25,42.25); Rounding=9; Blend=3; Color=$c })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Position=@(0,0,42.25); Size=@(20.5,20.5,50); Blend=2; Color=$c })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Rotation=(QAxis 0 1 0 90); Position=@(0,0,42.25); Size=@(20.5,20.5,50); Blend=2; Color=$c })
    (B @{ Shape='Cylinder'; Operation='Subtract'; Rotation=(QAxis 1 0 0 90); Position=@(0,0,42.25); Size=@(20.5,20.5,50); Blend=2; Color=$c })
) }
# Wooden bat: fat shaft tapering to a bulb knob (variable-radius spline).
$Recipes['bat'] = { param($c) @(
    (B @{ Shape='Spline'; Curvature=0.7; Blend=2; Rounding=2; Color=$c
          Points=@( @(-97,0,16.3,4.5), @(-90,0,16.3,9.5), @(-65,0,16.3,4.2), @(-5,0,16.3,16.3), @(95,0,16.3,16.3) ) })
) }
# Flat paddle: round head + long handle.
$Recipes['paddle'] = { param($c) @(
    (B @{ Shape='Cylinder'; Position=@(-73,0,6.15); Size=@(35,35,6.15); Rounding=5; Blend=6; Color=$c })
    (B @{ Shape='Box'; Position=@(26,1.25,6.15); Size=@(38.2,12.75,6.15); Rounding=5; Blend=6; Color=$wood })
) }
# Rounded ornament (whale-ish mound + top lip) + its small floating companion block.
$Recipes['ornament'] = { param($c) @(
    (B @{ Shape='Sphere'; Position=@(-55,14,26); Size=@(29,27,26); Blend=8; Color=$c })
    (B @{ Shape='Sphere'; Position=@(-54,-8,41); Size=@(15,9,8); Blend=14; Color=$c })
    (B @{ Shape='Box'; Position=@(72.5,32.8,144.5); Size=@(9.5,10.7,8.6); Rounding=5; Blend=1; Color=$yellow })
) }
# Winding S-curve road deck (blended box segments) with end ramps + 5 round columns w/ finials.
$Recipes['road'] = { param($c)
    $path = @( @(-380,27), @(-300,13.6), @(-220,-8), @(-140,-26.6), @(-60,-17),
               @(20,6.6), @(100,27), @(180,13.7), @(260,-8.6), @(340,-28), @(380,-33.7) )
    $out = @()
    for ($i = 0; $i -lt $path.Count - 1; $i++) {
        $x1 = $path[$i][0];   $y1 = -$path[$i][1]     # sbox y = -z_obj
        $x2 = $path[$i+1][0]; $y2 = -$path[$i+1][1]
        $dx = $x2-$x1; $dy = $y2-$y1
        $len = [math]::Sqrt($dx*$dx + $dy*$dy)
        $ang = [math]::Atan2($dy, $dx) * 180.0 / [math]::PI
        $out += (B @{ Shape='Box'; Rotation=(QAxis 0 0 1 $ang)
            Position=@((($x1+$x2)/2.0), (($y1+$y2)/2.0), 17)
            Size=@(($len/2.0 + 6), 40, 17); Rounding=14; Blend=6; Color=$c })
    }
    # +x end: deck descends to ground (cut above plane (345,34)->(385,16.5))
    $out += (B @{ Shape='Box'; Operation='Subtract'; Rotation=(QAxis 0 1 0 23.6)
        Position=@(371.4, 31.6, 39.9); Size=@(35,45,16); Blend=3; Color=$c })
    # -x end: upswept underside (cut below plane (-345,0)->(-390,18.5))
    $out += (B @{ Shape='Box'; Operation='Subtract'; Rotation=(QAxis 0 1 0 22.4)
        Position=@(-373.6, -24.9, -5.55); Size=@(35,45,16); Blend=3; Color=$c })
    # columns (x, y, r, shaft top, finial top, colour)
    $cols = @(
        @(-203, 19.5, 18.5, 146, 158, $red),
        @(-40,  11,   13.5, 114, 124, $blue),
        @(40,  -12,   13.5, 114, 124, $red),
        @(180, -17.7, 13.7, 116, 128, $blue),
        @(-297,-13.5, 13.5, 78,  86.4, $red)
    )
    foreach ($col in $cols) {
        $shaftHalf = ($col[3] - 30) / 2.0
        $out += (B @{ Shape='Cylinder'; Position=@($col[0], $col[1], (30 + $shaftHalf)); Size=@($col[2], $col[2], $shaftHalf); Rounding=3; Blend=3; Color=$col[5] })
        $coneHalf = ($col[4] - $col[3]) / 2.0
        $out += (B @{ Shape='Cone'; Position=@($col[0], $col[1], $col[3]); Size=@(($col[2]+2.5), ($col[2]+2.5), $coneHalf); Rounding=2; Blend=3; Color=$yellow })
    }
    $out
}

# Mesh resolution per class
$Res = @{ cube=24; cylinder=24; cylinder_slim=24; plank_long=32; plank_square=24; panelpost=48
          roof=32; archump=32; bridge=48; fence=64; holedpanel=48; vault=48; holedcube=48
          bat=48; paddle=48; ornament=64; road=64 }

# ============================================================================
#  INSTANCES (from OBJ analysis: cx, cz, ymin in OBJ units; yaw = min-rect fit)
#  Extra: '' | 'stand' (flat panel stood upright) | 'flip' (vault upside-down)
# ============================================================================
$X0 = -504.781; $Z0 = -1190.59; $Y0 = 8.133
function I([string]$id, [string]$cls, [double]$cx, [double]$cz, [double]$ymin, [double]$yaw, $col, [string]$extra = '') {
    @{ Id=$id; Class=$cls; Cx=$cx; Cz=$cz; Ymin=$ymin; Yaw=$yaw; Col=$col; Extra=$extra }
}
$Instances = @(
    (I '49_575' 'cube' -678.495 -1492.973 11.951 143.63 $red)
    (I '50_972' 'cube' -675.714 -1492.196 56.827 28.63  $blue)
    (I '51_93'  'cube' -676.118 -1490.565 101.352 143.63 $yellow)
    (I '52_147' 'cube' -676.636 -1491.355 146.053 13.63 $green)
    (I '54_367' 'cube' -814.608 -1394.416 190.886 143.63 $red)
    (I '47_829' 'cube' -770.632 -1428.528 101.284 143.63 $blue)
    (I '44_406' 'cube' -858.822 -1365.99 101.313 143.63 $yellow)
    (I '42_852' 'cylinder' -859.118 -1365.83 11.951 171.75 $blue)
    (I '43_40'  'cylinder' -859.118 -1365.83 56.617 148.0 $red)
    (I '45_248' 'cylinder' -769.535 -1427.749 11.891 94.25 $green)
    (I '46_710' 'cylinder' -769.535 -1427.749 56.44 118.0 $yellow)
    (I '55_910' 'cylinder' -815.802 -1396.158 235.593 36.75 $blue)
    (I '56_261' 'cylinder' -757.535 -1437.674 235.468 171.75 $red)
    (I '57_683' 'cylinder' -699.312 -1480.552 235.468 171.75 $green)
    (I '10_957' 'cylinder_slim' -624.184 -1190.002 23.634 117.95 $yellow)
    (I '14_328' 'cylinder_slim' -384.678 -1190.002 23.126 117.95 $red)
    (I '58_313' 'plank_long' -779.762 -1422.996 280.137 143.63 $green)
    (I '68_212' 'plank_long' -808.515 -1216.632 146.024 123.63 $yellow)
    (I '59_925' 'plank_square' -725.294 -1462.97 280.137 143.63 $red)
    (I '62_975' 'panelpost' -782.792 -1253.722 11.777 33.63 $red)
    (I '63_697' 'panelpost' -782.792 -1253.722 56.61 33.63 $blue)
    (I '64_206' 'panelpost' -782.792 -1253.722 101.396 33.63 $yellow)
    (I '65_167' 'panelpost' -833.538 -1177.429 11.777 33.63 $blue)
    (I '66_15'  'panelpost' -833.538 -1177.429 56.61 33.63 $red)
    (I '67_642' 'panelpost' -833.538 -1177.429 101.396 33.63 $green)
    (I '69_888' 'roof' -805.985 -1219.683 169.376 123.98 $red)
    (I '61_579' 'archump' -761.879 -1435.656 349.719 143.63 $green)
    (I '48_253' 'bridge' -813.251 -1397.468 145.95 143.63 $yellow)
    (I '53_270' 'bridge' -726.627 -1461.296 190.747 143.63 $yellow)
    (I '60_781' 'bridge' -761.874 -1436.672 304.91 143.63 $yellow)
    (I '6_262'  'fence' -505.981 -1150.038 10.129 179.7 $green)
    (I '9_12'   'fence' -508.455 -1229.605 9.819 0.23 $green)
    (I '8_127'  'fence' -308.012 -1286.293 10.723 148.07 $green)
    (I '1_741'  'fence' -741.305 -1167.826 12.174 148.86 $green)
    (I '7_991'  'fence' -262.032 -1221.703 9.841 147.36 $green)
    (I '2_677'  'fence' -697.264 -1101.285 9.981 148.33 $green)
    (I '13_109' 'holedpanel' -626.718 -1016.362 10.504 106.8 $blue)
    (I '19_263' 'holedpanel' -504.56 -1189.758 55.693 0.0 $red)
    (I '17_799' 'holedpanel' -365.105 -973.823 10.584 60.59 $green 'stand')
    (I '16_940' 'vault' -256.004 -1282.597 141.415 100.54 $green)
    (I '18_11'  'vault' -493.774 -990.75 41.701 80.58 $blue)
    (I '15_663' 'vault' -501.435 -993.888 8.133 138.41 $yellow 'flip')
    (I '12_635' 'holedcube' -257.862 -1282.356 56.79 155.97 $yellow)
    (I '5_300'  'holedcube' -704.596 -1153.595 56.064 50.05 $red)
    (I '3_92'   'holedcube' -282.23 -1447.52 10.616 124.04 $blue)
    (I '11_208' 'holedcube' -264.002 -1092.978 10.556 79.91 $green)
    (I '38_426' 'bat' -198.526 -1014.811 10.526 165.47 $wood)
    (I '39_732' 'paddle' -182.204 -949.499 10.643 166.8 $red)
    (I '35_177' 'ornament' -198.545 -1117.814 9.935 177.45 $blue)
    (I '4_730'  'road' -504.781 -1190.59 22.214 163.78 $wood)
)

# ============================================================================
#  COMPONENT TEMPLATES (mirroring buildingblock_arc.prefab)
# ============================================================================
function NewSculpt($brushes, $res) {
    [ordered]@{
        __type = 'Mimiclay.SdfSculpture'; __guid = (NG); __enabled = $true; Flags = 0
        AutoRebuild = $true; BakedMesh = $null; Brushes = [object[]]$brushes
        FlipFaces = $false; Material = 'materials/plasticine_vertex.vmat'
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        Resolution = $res
    }
}
function NewModel {
    [ordered]@{
        __type = 'Sandbox.ModelRenderer'; __guid = (NG); __enabled = $true; Flags = 0
        BodyGroups = 18446744073709551615; CreateAttachments = $false; LodOverride = $null
        MaterialGroup = $null; MaterialOverride = $null; Materials = $null; Model = 'sbox_procedural_model.vmdl'
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        RenderOptions = [ordered]@{ GameLayer = $true; OverlayLayer = $false; BloomLayer = $false; AfterUILayer = $false }
        RenderType = 'ShadowsOnly'; Tint = '1,1,1,1'
    }
}
function NewRay($cull) {
    [ordered]@{
        __type = 'Mimiclay.SdfRaymarchRenderer'; __guid = (NG); __enabled = $true; Flags = 0
        AdaptiveQuality = $true; BrushCulling = $true; CullRadii = $cull; DebugBounds = $false
        DebugLiveField = $false; DebugLod = $false; DebugSwitchState = $false; DepthClamp = $false
        Displace = $false; DisplaceAmount = 0.96202534; DisplaceFrequency = 0.12755275; DistanceSwitching = $true
        Epsilon = 0.18; FarEpsilon = 0.5; FieldCacheOnly = $true; FieldNormalScale = 0.5
        FieldResolution = 256; FullQualityRadii = 2; LodHysteresis = 0.06
        Material = 'materials/plasticine.vmat'; MaxSteps = 50; MeshLod1Radii = 30; MeshLod2Radii = 40
        MeshMode = 'DepthProxy'; MinQualityRadii = 12; MinSteps = 6
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        OverdrawOptimization = $false; SparseField = $true; TightBounds = $false
        Transmission = $true; UseFieldCache = $true
    }
}
function NewSdfCollider {
    [ordered]@{
        __type = 'Mimiclay.SdfCollider'; __guid = (NG); __enabled = $true; Flags = 0; FootProbeSpacing = 6
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
    }
}
function NewModelCollider {
    [ordered]@{
        __type = 'Sandbox.ModelCollider'; __guid = (NG); __enabled = $true; Flags = 0
        ColliderFlags = 0; Elasticity = $null; Friction = $null; IsTrigger = $false
        Model = 'sbox_procedural_model.vmdl'
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        OnObjectTriggerEnter = $null; OnObjectTriggerExit = $null; OnTriggerEnter = $null; OnTriggerExit = $null
        RollingResistance = $null; Static = $false; Surface = $null; SurfaceVelocity = '0,0,0'
    }
}
function Components($brushes, $res, $cull) {
    [object[]]@((NewSculpt $brushes $res), (NewModel), (NewRay $cull), (NewSdfCollider), (NewModelCollider))
}
function NewProps {
    [ordered]@{
        NetworkInterpolation = $true; TimeScale = 1; WantsSystemScene = $true; Metadata = [ordered]@{}
        NavMesh = [ordered]@{
            Enabled = $false; IncludeStaticBodies = $true; IncludeKeyframedBodies = $true
            EditorAutoUpdate = $false; AgentHeight = 64; AgentRadius = 16; AgentStepSize = 18
            AgentMaxSlope = 40; ExcludedBodies = ''; IncludedBodies = ''; DeferGeneration = $false; CustomBounds = $false
        }
    }
}

# ============================================================================
#  EMIT — individual prefabs
# ============================================================================
$classCols = @{ cube=$red; cylinder=$blue; cylinder_slim=$yellow; plank_long=$green; plank_square=$red
                panelpost=$red; roof=$red; archump=$green; bridge=$yellow; fence=$green
                holedpanel=$blue; vault=$green; holedcube=$yellow; bat=$wood; paddle=$red
                ornament=$blue; road=$wood }
foreach ($cls in ($Recipes.Keys | Sort-Object)) {
    $brushes = @(& $Recipes[$cls] $classCols[$cls])
    $cull = if ($cls -eq 'road') { 220 } else { 80 }
    $prefab = [ordered]@{
        RootObject = [ordered]@{
            __guid = (NG); __version = 2; Flags = 0; Name = "block_$cls"
            Position = '0,0,0'; Rotation = '0,0,0,1'; Scale = '1,1,1'; Tags = ''; Enabled = $true
            NetworkMode = 1; NetworkFlags = 0; NetworkOrphaned = 0; NetworkTransmit = $true; OwnerTransfer = 1
            Components = (Components $brushes $Res[$cls] $cull)
            Children = @()
            __properties = (NewProps)
            __variables = @()
        }
        ResourceVersion = 2; ShowInMenu = $false; MenuPath = $null; MenuIcon = $null
        DontBreakAsTemplate = $false; __references = @(); __version = 2
    }
    $json = $prefab | ConvertTo-Json -Depth 24
    [IO.File]::WriteAllText("$OutDir/block_$cls.prefab", $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host ("block_{0}.prefab  ({1} brushes)" -f $cls, $brushes.Count)
}

# ============================================================================
#  EMIT — arrangement (inline children at OBJ placements)
# ============================================================================
$children = @()
foreach ($inst in $Instances) {
    $cls = $inst.Class
    $brushes = @(& $Recipes[$cls] $inst.Col)
    $yawS = -[double]$inst.Yaw
    $px = ([double]$inst.Cx - $X0) * $S
    $py = -(([double]$inst.Cz - $Z0)) * $S
    $pz = ([double]$inst.Ymin - $Y0) * $S
    $rot = QAxis 0 0 1 $yawS

    if ($inst.Extra -eq 'stand') {
        # flat panel stood upright: pitch 90 about local X, recentre thickness, lift half-depth
        $rot = QMul $rot (QAxis 1 0 0 90)
        $rad = $yawS * [math]::PI / 180.0
        $ox = 9.15 * $S * -[math]::Sin($rad)   # yaw-rotated (0, +9.15S) offset
        $oy = 9.15 * $S *  [math]::Cos($rad)
        $px += $ox; $py += $oy
        $pz += 34.46 * $S
    }
    elseif ($inst.Extra -eq 'flip') {
        # vault upside-down: roll 180 about local X, lift by its height
        $rot = QMul $rot (QAxis 1 0 0 180)
        $pz += 35.12 * $S
    }

    $cull = if ($cls -eq 'road') { 220 } else { 80 }
    $children += [ordered]@{
        __guid = (NG); __version = 2; Flags = 0; Name = "$($cls)_$($inst.Id)"
        Position = "$(F $px),$(F $py),$(F $pz)"
        Rotation = (QStr $rot)
        Scale = '1,1,1'; Tags = ''; Enabled = $true
        NetworkMode = 2; NetworkFlags = 0; NetworkOrphaned = 0; NetworkTransmit = $true; OwnerTransfer = 1
        Components = (Components $brushes $Res[$cls] $cull)
        Children = @()
    }
}
$arr = [ordered]@{
    RootObject = [ordered]@{
        __guid = (NG); __version = 2; Flags = 0; Name = 'blockset_arrangement'
        Position = '0,0,0'; Rotation = '0,0,0,1'; Scale = '1,1,1'; Tags = ''; Enabled = $true
        NetworkMode = 2; NetworkFlags = 0; NetworkOrphaned = 0; NetworkTransmit = $true; OwnerTransfer = 1
        Components = @()
        Children = [object[]]$children
        __properties = (NewProps)
        __variables = @()
    }
    ResourceVersion = 2; ShowInMenu = $false; MenuPath = $null; MenuIcon = $null
    DontBreakAsTemplate = $false; __references = @(); __version = 2
}
$json = $arr | ConvertTo-Json -Depth 24
[IO.File]::WriteAllText("$OutDir/blockset_arrangement.prefab", $json, (New-Object Text.UTF8Encoding($false)))
Write-Host ("blockset_arrangement.prefab  ({0} instances)" -f $children.Count)
