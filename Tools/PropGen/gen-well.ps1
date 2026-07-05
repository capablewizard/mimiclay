# Generates a detailed Castle Courtyard WELL prefab, matching the market well in the
# concept art: brick/stone circular base, coping rim, wooden posts with metal bolts,
# a roller spindle + crank, a rope & bucket, and a blue-tiled pitched roof.
#
# IMPORTANT: the SDF renderer + field baker cap at 64 brushes per sculpture
# (SdfRaymarchRenderer/SdfFieldGpu: const int MaxBrushes = 64). A detailed well needs
# far more, so it is authored as a PARENT GameObject with several CHILD sub-sculptures,
# each kept under 64 brushes. This mirrors how the map is built (many small sculptures).
#
# Conventions: Z up, sits on ground at z=0.
#   Cylinder: radius=Size.x, half-height=Size.z, axis Z.  Cone: base r=Size.x at -Size.z, apex +Size.z.
#   Box: Size=half-extents.  Sphere: Size=per-axis radii.  Spline: Points="x,y,z,w" (w=radius).

$ErrorActionPreference = 'Stop'
$OutDir = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/CastleCourtyard'
$inv = [Globalization.CultureInfo]::InvariantCulture

function NG { [guid]::NewGuid().ToString() }
function F([double]$v) { $v.ToString('0.######', $inv) }
function Qaxis([double]$ax, [double]$ay, [double]$az, [double]$deg) {
    $r = [math]::Sqrt($ax * $ax + $ay * $ay + $az * $az)
    if ($r -lt 1e-9) { return '0,0,0,1' }
    $ax /= $r; $ay /= $r; $az /= $r
    $h = [math]::PI * $deg / 360.0; $s = [math]::Sin($h); $c = [math]::Cos($h)
    "$(F ($ax*$s)),$(F ($ay*$s)),$(F ($az*$s)),$(F $c)"
}
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
    $sz = $d.Size.Split(','); $mx = 0.0
    foreach ($c in $sz) { $f = [math]::Abs([double]$c); if ($f -gt $mx) { $mx = $f } }
    $d.BoundingRadius = [double](F ($mx * 1.74 + $d.Blend * 0.25))
    $d
}
function BM([hashtable]$metal, [hashtable]$o) {
    $m = @{}; foreach ($k in $metal.Keys) { $m[$k] = $metal[$k] }
    foreach ($k in $o.Keys) { $m[$k] = $o[$k] }
    B $m
}

# ---- palette --------------------------------------------------------------
$mortar   = '0.5,0.49,0.46,1'
$stoneA   = '0.64,0.61,0.55,1'
$stoneB   = '0.56,0.52,0.46,1'
$coping   = '0.71,0.68,0.61,1'
$wood     = '0.55,0.36,0.2,1'
$woodDk   = '0.42,0.27,0.16,1'
$woodLt   = '0.68,0.46,0.27,1'
$tileA    = '0.29,0.46,0.73,1'
$tileB    = '0.22,0.37,0.63,1'
$ridgeCol = '0.2,0.32,0.55,1'
$rope     = '0.72,0.6,0.4,1'
$water    = '0.13,0.28,0.38,1'
$iron     = @{ Color = '0.3,0.31,0.33,1'; Metallic = 1; Roughness = 0.5 }
$dark     = @{ Color = '0.18,0.18,0.2,1'; Metallic = 1; Roughness = 0.55 }
$D2R = [math]::PI / 180.0

# ============================================================================
#  CHILD 1 — STONEWORK (structural wall, water, coping rim)
# ============================================================================
$stonework = New-Object System.Collections.ArrayList
function AddSt([hashtable]$o) { [void]$stonework.Add((B $o)) }
AddSt @{ Shape='Cylinder'; Position='0,0,13'; Size='20,20,13'; Rounding=2; Blend=1; Color=$mortar }
AddSt @{ Shape='Cylinder'; Operation='Subtract'; Position='0,0,15'; Size='16,16,14'; Blend=1 }
AddSt @{ Shape='Cylinder'; Position='0,0,6'; Size='16,16,1.2'; Blend=0.5; Color=$water }
$cn = 14
for ($i = 0; $i -lt $cn; $i++) {
    $ang = $i * 360.0 / $cn; $a = $ang * $D2R
    $x = 22.0 * [math]::Cos($a); $y = 22.0 * [math]::Sin($a)
    AddSt @{ Shape='Box'; Position="$(F $x),$(F $y),28.5"; Rotation=(Qaxis 0 0 1 $ang)
        Size='5,4.6,1.7'; Rounding=1.3; Blend=0.8; Color=$coping }
}

# ============================================================================
#  CHILD 2 — BRICKS (running-bond courses around the outer face)
# ============================================================================
$bricks = New-Object System.Collections.ArrayList
$courses = 4
for ($cc = 0; $cc -lt $courses; $cc++) {
    $z = 3.0 + $cc * 6.4
    $count = 15
    $rr = 22.0
    $off = if ($cc % 2) { 0.5 } else { 0.0 }
    for ($i = 0; $i -lt $count; $i++) {
        $ang = ($i + $off) * 360.0 / $count; $a = $ang * $D2R
        $x = $rr * [math]::Cos($a); $y = $rr * [math]::Sin($a)
        $col = if ((($i + $cc) % 2) -eq 0) { $stoneA } else { $stoneB }
        [void]$bricks.Add((B @{ Shape='Box'; Position="$(F $x),$(F $y),$(F $z)"; Rotation=(Qaxis 0 0 1 $ang)
            Size='4,3.4,2.75'; Rounding=1; Blend=0.7; Color=$col }))
    }
}

# ============================================================================
#  CHILD 3 — FRAME (posts + bolts, spindle + crank, rope + bucket, ridge purlin)
# ============================================================================
$frame = New-Object System.Collections.ArrayList
function AddFr([hashtable]$o) { [void]$frame.Add((B $o)) }
function AddFrM([hashtable]$m, [hashtable]$o) { [void]$frame.Add((BM $m $o)) }
# Posts (along Y, +/-22) + iron foot brackets.
AddFr @{ Shape='Box'; Position='0,22,50'; Size='3.5,3.5,23'; Rounding=1; Blend=0.6; Color=$wood; MirrorY=$true }
AddFrM $iron @{ Shape='Box'; Position='0,22,30'; Size='4.2,4.2,2'; Rounding=0.6; Blend=0.4; MirrorY=$true }
# Metal bolts in the posts (MirrorX faces, MirrorY both posts -> 4 each).
foreach ($bz in 38, 50, 62) {
    AddFrM $dark @{ Shape='Sphere'; Position="3.7,22,$bz"; Size='1.1,0.9,1.1'; Blend=0.4; MirrorX=$true; MirrorY=$true }
}
# Roller spindle + iron caps + crank.
AddFr @{ Shape='Cylinder'; Rotation=(Qaxis 1 0 0 90); Position='0,0,52'; Size='3,3,19'; Rounding=1; Blend=0.6; Color=$woodLt }
AddFrM $iron @{ Shape='Cylinder'; Rotation=(Qaxis 1 0 0 90); Position='0,18,52'; Size='3.3,3.3,1'; Blend=0.4; MirrorY=$true }
AddFrM $dark @{ Shape='Sphere'; Position='0,18.6,52'; Size='1,1,1'; Blend=0.4; MirrorY=$true }
AddFr @{ Shape='Box'; Position='6,22,52'; Size='6,1.1,1.1'; Rounding=0.5; Blend=0.4; Color=$woodDk }
AddFr @{ Shape='Cylinder'; Rotation=(Qaxis 1 0 0 90); Position='11.5,22,49.5'; Size='1.2,1.2,3.5'; Rounding=0.6; Blend=0.4; Color=$woodLt }
AddFrM $dark @{ Shape='Sphere'; Position='6,22,52'; Size='0.9,0.9,0.9'; Blend=0.3 }
# Rope + bucket.
AddFr @{ Shape='Cylinder'; Position='0,0,37'; Size='0.6,0.6,15'; Blend=0.3; Color=$rope }
AddFr @{ Shape='Cone'; Rotation=(Qaxis 1 0 0 180); Position='0,0,17'; Size='4.5,4.5,4.5'; Blend=0.8; Color=$woodLt }
AddFr @{ Shape='Cone'; Operation='Subtract'; Rotation=(Qaxis 1 0 0 180); Position='0,0,19'; Size='3.3,3.3,4.5'; Blend=0.8 }
AddFrM $iron @{ Shape='Cylinder'; Position='0,0,21'; Size='4.7,4.7,0.7'; Blend=0.4 }
AddFrM $iron @{ Shape='Spline'; Blend=0.3; Points=@('-4.5,0,21,0.5','0,0,26,0.5','4.5,0,21,0.5') }
# Ridge purlin resting on the posts (also lives here so the roof child stays the pure tile skin).
AddFr @{ Shape='Box'; Position='0,0,72'; Size='2,24,2'; Rounding=0.8; Blend=0.6; Color=$woodDk }
AddFrM $dark @{ Shape='Sphere'; Position='0,22,72'; Size='1,1,1'; Blend=0.4; MirrorY=$true }

# ============================================================================
#  CHILD 4 — ROOF (blue tile skin, ridge cap, eave fascia)
# ============================================================================
$roof = New-Object System.Collections.ArrayList
$eaveX = 30.0; $eaveZ = 56.0; $ridgeZ = 74.0
$dx = $eaveX; $dz = $ridgeZ - $eaveZ
$L = [math]::Sqrt($dx * $dx + $dz * $dz)
$uux = -$dx / $L; $uuz = $dz / $L
$nx = $dz / $L; $nz = $dx / $L
$beta = [math]::Atan2($dx, $dz) / $D2R
$rotTile = (Qaxis 0 1 0 $beta)
$lift = 0.9
$rowStep = 5.0; $colStep = 7.0; $nRows = 8; $nCols = 7
for ($r = 0; $r -lt $nRows; $r++) {
    $a = -2.0 + $r * $rowStep
    $px = $eaveX + $uux * $a + $nx * $lift
    $pz = $eaveZ + $uuz * $a + $nz * $lift
    $stagger = if ($r % 2) { $colStep * 0.5 } else { 0.0 }
    for ($c = 0; $c -lt $nCols; $c++) {
        $y = -(($nCols - 1) * $colStep) / 2.0 + $c * $colStep + $stagger
        if ([math]::Abs($y) -gt 27) { continue }
        $col = if ((($r + $c) % 2) -eq 0) { $tileA } else { $tileB }
        [void]$roof.Add((B @{ Shape='Box'; Rotation=$rotTile; Position="$(F $px),$(F $y),$(F $pz)"
            Size='3,3.4,0.9'; Rounding=0.7; Blend=0.8; Color=$col; MirrorX=$true }))
    }
}
[void]$roof.Add((B @{ Shape='Cylinder'; Rotation=(Qaxis 1 0 0 90); Position='0,0,74.6'; Size='2,2,27'; Rounding=1.2; Blend=1; Color=$ridgeCol }))
[void]$roof.Add((B @{ Shape='Box'; Position='29,0,54.5'; Size='1.4,27,1.6'; Rounding=0.6; Blend=0.5; Color=$woodDk; MirrorX=$true }))

# ============================================================================
#  EMIT
# ============================================================================
function NewSculpt($brushes) {
    [ordered]@{
        __type = 'Mimiclay.SdfSculpture'; __guid = (NG); __enabled = $true; Flags = 0
        AutoRebuild = $false; BakedMesh = $null; Brushes = @($brushes.ToArray())
        FlipFaces = $false; Material = 'materials/plasticine_vertex.vmat'
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
        Resolution = 32
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
function NewRay($res) {
    [ordered]@{
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
}
function NewCollider {
    [ordered]@{
        __type = 'Mimiclay.SdfCollider'; __guid = (NG); __enabled = $true; Flags = 0; FootProbeSpacing = 12
        OnComponentDestroy = $null; OnComponentDisabled = $null; OnComponentEnabled = $null
        OnComponentFixedUpdate = $null; OnComponentStart = $null; OnComponentUpdate = $null
    }
}
function NewChild($name, $res, $brushes) {
    [ordered]@{
        __guid = (NG); __version = 2; Flags = 0; Name = $name
        Position = '0,0,0'; Rotation = '0,0,0,1'; Scale = '1,1,1'; Tags = ''; Enabled = $true
        NetworkMode = 2; NetworkFlags = 0; NetworkOrphaned = 0; NetworkTransmit = $true; OwnerTransfer = 1
        Components = @((NewSculpt $brushes), (NewModel), (NewRay $res), (NewCollider))
        Children = @()
    }
}

$children = @(
    (NewChild 'Stonework' 96 $stonework)
    (NewChild 'Bricks' 128 $bricks)
    (NewChild 'Frame' 96 $frame)
    (NewChild 'Roof' 128 $roof)
)
$root = [ordered]@{
    RootObject = [ordered]@{
        __guid = (NG); __version = 2; Flags = 0; Name = 'Well'
        Position = '0,0,0'; Rotation = '0,0,0,1'; Scale = '1,1,1'; Tags = ''; Enabled = $true
        NetworkMode = 2; NetworkFlags = 0; NetworkOrphaned = 0; NetworkTransmit = $true; OwnerTransfer = 1
        Components = @()
        Children = $children
    }
    ResourceVersion = 2; ShowInMenu = $false; MenuPath = $null; MenuIcon = $null
    DontBreakAsTemplate = $false; __references = @(); __version = 2
}
$json = $root | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText("$OutDir/well.prefab", $json, (New-Object Text.UTF8Encoding($false)))
Write-Host ("Wrote well.prefab: Stonework={0}, Bricks={1}, Frame={2}, Roof={3} (each <= 64)" -f `
    $stonework.Count, $bricks.Count, $frame.Count, $roof.Count)
