# Generates a family of classic wooden-toy BUILDING BLOCKS to sit alongside the existing
# buildingblock_arc.prefab, so a kid can stack them into towers like the reference photo.
#
# SCALE / STACKING: everything is built on a 24-unit grid (the arc block is 24 tall). A base
# "cell" is 24x24x24. Bricks/planks are 2x1 in plan; columns are radius-12; roofs/ramps/domes
# all fit the 24x24 footprint and are 24 (or 12) tall. Each block's pivot (0,0,0) sits at the
# CENTRE of its bottom face (bottom rests at z=0), so placed on a surface they line up and stack.
# NOTE: the hand-authored arc has its pivot 4 units below its base (box centred at z=16) — a
# cosmetic authoring offset; in-game these are dynamic props that settle by physics anyway.
#
# Conventions (from SdfBrush.cs):
#   Box:      Size = half-extents.
#   Cylinder: axis local Z, radius = Size.x, half-height = Size.z.
#   Cone:     base sits at Position, base radius = Size.x, apex at +2*Size.z (base-pivoted).
#   Extruded/Triangle: isosceles, apex (0,+Size.y), base (+/-Size.x,-Size.y), swept +/-Size.z.
#   Sphere:   per-axis radii; Slice 0.5 cuts a hemisphere flat off the local +Z top.
# Each prefab mirrors buildingblock_arc.prefab's component set + material verbatim.

$ErrorActionPreference = 'Stop'
$OutDir = 'd:/SBox/Projects/mimiclay/Assets/Prefabs/saved'
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
# Brush builder: fills the defaults every brush needs, overlays $o, and computes a (harmless,
# recomputed-on-load) BoundingRadius so the JSON matches the shape of a hand-saved brush.
function B([hashtable]$o) {
    $d = [ordered]@{
        Shape = 'Box'; Operation = 'Add'; CrossSection = 'Triangle'
        Text = 'clay'; Font = 'Super Joyful'; Enabled = $true
        Position = '0,0,0'; Rotation = '0,0,0,1'; Size = '12,12,12'
        Points = $null; Curvature = 1; SplineClosed = $false; Slice = 0
        LocalCentre = '0,0,0'; Blend = 2; Rounding = 2; Color = '1,1,1,1'
        Metallic = 0; Roughness = 0.5; MirrorX = $false; MirrorY = $false; MirrorZ = $false
        IsSplineLoop = $false; BoundingRadius = 24
    }
    foreach ($k in $o.Keys) { $d[$k] = $o[$k] }
    $sz = "$($d.Size)".Split(','); $mx = 0.0
    foreach ($c in $sz) { $f = [math]::Abs([double]$c); if ($f -gt $mx) { $mx = $f } }
    $d.BoundingRadius = [double](F ($mx * 1.74 + [double]$d.Blend * 0.25))
    $d
}

# ---- classic primary palette (the Melissa & Doug wooden-block colours) ----
$red    = '0.85,0.16,0.13,1'
$blue   = '0.17,0.35,0.72,1'
$green  = '0.3,0.62,0.24,1'
$yellow = '0.96,0.78,0.13,1'

# ============================================================================
#  BLOCK DEFINITIONS — each is a name + SdfSculpture Resolution + brush list.
#  All fit the 24-grid so they stack. Bottom face rests on z=0.
# ============================================================================
$blocks = @()

# 1. CUBE — one 24x24x24 cell.
$blocks += @{ Name = 'buildingblock_cube'; Res = 24; Brushes = @(
    (B @{ Shape='Box'; Position='0,0,12'; Size='12,12,12'; Rounding=2.5; Color=$red })
) }

# 2. BRICK — double cube, 48x24x24 (2x1 plan).
$blocks += @{ Name = 'buildingblock_brick'; Res = 32; Brushes = @(
    (B @{ Shape='Box'; Position='0,0,12'; Size='24,12,12'; Rounding=2.5; Color=$blue })
) }

# 3. PLANK — flat board, 48x24x12 (half-height brick, good for floors/lintels).
$blocks += @{ Name = 'buildingblock_plank'; Res = 32; Brushes = @(
    (B @{ Shape='Box'; Position='0,0,6'; Size='24,12,6'; Rounding=2; Color=$green })
) }

# 4. COLUMN — short round pillar, radius 12, 24 tall.
$blocks += @{ Name = 'buildingblock_column'; Res = 24; Brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,12'; Size='12,12,12'; Rounding=1; Color=$yellow })
) }

# 5. COLUMN_TALL — round pillar, radius 12, 48 tall.
$blocks += @{ Name = 'buildingblock_column_tall'; Res = 40; Brushes = @(
    (B @{ Shape='Cylinder'; Position='0,0,24'; Size='12,12,24'; Rounding=1; Color=$red })
) }

# 6. ROOF — isosceles triangular-prism gable. Base 24 wide, 24 tall, 24 deep. The triangle
#    stands in XY (apex +Y) extruded along local Z; rotate +90 about X so the apex points up
#    (+world Z) and the ridge runs along world Y. Centre lifted so the base rests on z=0.
$blocks += @{ Name = 'buildingblock_roof'; Res = 32; Brushes = @(
    (B @{ Shape='Extruded'; CrossSection='Triangle'; Rotation=(Qaxis 1 0 0 90);
          Position='0,0,12'; Size='12,12,12'; Rounding=1.5; Color=$green })
) }

# 7. RAMP — right-angle wedge: a 24-cube with the top corner sliced off along the diagonal
#    (vertical face at x=-12, sloping down to x=+12 at z=0). The cut is a big box rotated -45
#    about Y whose -X face lies on the plane x+z=12; everything on its +X side is subtracted.
$blocks += @{ Name = 'buildingblock_ramp'; Res = 40; Brushes = @(
    (B @{ Shape='Box'; Position='0,0,12'; Size='12,12,12'; Rounding=1.5; Color=$yellow })
    (B @{ Shape='Box'; Operation='Subtract'; Rotation=(Qaxis 0 1 0 -45);
          Position='28.284271,0,40.284271'; Size='40,40,40'; Blend=1; Rounding=0.75; Color=$yellow })
) }

# 8. HALFROUND — half-cylinder ridge (a rounded-top bridge block). Cylinder radius 12 laid on
#    its side (axis along world Y, length 24), lower half subtracted so it sits flat on z=0.
$blocks += @{ Name = 'buildingblock_halfround'; Res = 32; Brushes = @(
    (B @{ Shape='Cylinder'; Rotation=(Qaxis 1 0 0 90); Position='0,0,0'; Size='12,12,12'; Rounding=1; Color=$blue })
    (B @{ Shape='Box'; Operation='Subtract'; Position='0,0,-20'; Size='24,24,20'; Blend=0.5; Rounding=0.75; Color=$blue })
) }

# 9. DOME — half-sphere cap (radius 12). Sphere sliced at 0.5 (hemisphere) then flipped 180
#    about X so the flat cut faces down and the dome sits round-side-up on z=0.
$blocks += @{ Name = 'buildingblock_dome'; Res = 32; Brushes = @(
    (B @{ Shape='Sphere'; Rotation=(Qaxis 1 0 0 180); Position='0,0,0'; Size='12,12,12';
          Slice=0.5; Rounding=1.5; Color=$blue })
) }

# 10. CONE — pointed spire, base radius 12, 24 tall (base-pivoted, so Position sits on z=0).
$blocks += @{ Name = 'buildingblock_cone'; Res = 32; Brushes = @(
    (B @{ Shape='Cone'; Position='0,0,0'; Size='12,12,12'; Rounding=1; Color=$yellow })
) }

# ============================================================================
#  COMPONENT TEMPLATE — copied from buildingblock_arc.prefab so look/behaviour match.
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
function NewRay {
    [ordered]@{
        __type = 'Mimiclay.SdfRaymarchRenderer'; __guid = (NG); __enabled = $true; Flags = 0
        AdaptiveQuality = $true; BrushCulling = $true; CullRadii = 80; DebugBounds = $false
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
function NewPrefab($name, $brushes, $res) {
    [ordered]@{
        RootObject = [ordered]@{
            __guid = (NG); __version = 2; Flags = 0; Name = $name
            Position = '0,0,0'; Rotation = '0,0,0,1'; Scale = '1,1,1'; Tags = ''; Enabled = $true
            NetworkMode = 1; NetworkFlags = 0; NetworkOrphaned = 0; NetworkTransmit = $true; OwnerTransfer = 1
            Components = [object[]]@((NewSculpt $brushes $res), (NewModel), (NewRay), (NewSdfCollider), (NewModelCollider))
            Children = @()
            __properties = (NewProps)
            __variables = @()
        }
        ResourceVersion = 2; ShowInMenu = $false; MenuPath = $null; MenuIcon = $null
        DontBreakAsTemplate = $false; __references = @(); __version = 2
    }
}

# ============================================================================
#  EMIT
# ============================================================================
foreach ($b in $blocks) {
    $prefab = NewPrefab $b.Name $b.Brushes $b.Res
    $json = $prefab | ConvertTo-Json -Depth 24
    [IO.File]::WriteAllText("$OutDir/$($b.Name).prefab", $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host ("Wrote {0}.prefab ({1} brush(es))" -f $b.Name, $b.Brushes.Count)
}
Write-Host ("Done: {0} blocks on the 24-unit grid." -f $blocks.Count)
