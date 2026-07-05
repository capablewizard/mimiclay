$ErrorActionPreference = "Stop"
$prefabDir = "d:\SBox\Projects\mimiclay\Assets\Prefabs"
$guidRe = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

# Extract the RootObject "Components": [...] block from a prefab as RAW TEXT
# (PowerShell's JSON parser mangles __-prefixed keys, so we never ConvertFrom-Json).
# Every GUID found is replaced with a fresh one so repeated props don't collide.
function Get-ComponentsText($file) {
  $raw = Get-Content (Join-Path $prefabDir $file) -Raw
  $ci  = $raw.IndexOf('"Components":')
  if ($ci -lt 0) { throw "no Components in $file" }
  $chi = $raw.IndexOf('"Children":', $ci)
  if ($chi -lt 0) { throw "no Children in $file" }
  $seg = $raw.Substring($ci, $chi - $ci).TrimEnd()
  if ($seg.EndsWith(',')) { $seg = $seg.Substring(0, $seg.Length - 1) }
  $found = [regex]::Matches($seg, $guidRe) | ForEach-Object { $_.Value } | Select-Object -Unique
  foreach ($g in $found) { $seg = $seg.Replace($g, [guid]::NewGuid().ToString()) }
  return $seg
}

$goTemplate = @'
    {{
      "__guid": "{0}",
      "__version": 2,
      "Flags": 0,
      "Name": "{1}",
      "Position": "{2}",
      "Rotation": "{3}",
      "Scale": "1,1,1",
      "Tags": "prop",
      "Enabled": true,
      "NetworkMode": 2,
      "NetworkFlags": 0,
      "NetworkOrphaned": 0,
      "NetworkTransmit": true,
      "OwnerTransfer": 1,
      {4},
      "Children": []
    }}
'@

function New-GO($name, $pos, $rot, $compText) {
  return ($goTemplate -f ([guid]::NewGuid().ToString()), $name, $pos, $rot, $compText)
}

# --- prop placement table: file | name | position | rotation(quat) ---
$props = @(
  # furniture
  @('bed-frame.prefab',       'Bed',             '190,-150,0',  '0,0,0,1'),
  @('pillow.prefab',          'Pillow',          '205,-235,30', '0,0,0,1'),
  @('blanket.prefab',         'Blanket',         '190,-110,30', '0,0,0,1'),
  @('side-table.prefab',      'Side Table',      '150,-250,0',  '0,0,0,1'),
  @('lamp.prefab',            'Lamp',            '150,-250,75', '0,0,0,1'),
  @('toy-bin.prefab',         'Toy Bin A',       '215,-40,0',   '0,0,0,1'),
  @('toy-bin.prefab',         'Toy Bin B',       '215,25,0',    '0,0,0,1'),
  @('desk.prefab',            'Desk',            '205,165,0',   '0,0,0,1'),
  @('drawer.prefab',          'Drawer 1',        '172,180,32',  '0,0,0,1'),
  @('drawer.prefab',          'Drawer 2',        '172,180,60',  '0,0,0,1'),
  @('chair.prefab',           'Chair',           '135,160,0',   '0,0,1,0'),
  # electronics
  @('retro-tv.prefab',        'Retro TV',        '185,70,0',    '0,0,0,1'),
  @('speaker.prefab',         'Speaker',         '205,115,0',   '0,0,0,1'),
  # toys on the floor
  @('trex.prefab',            'T-Rex',           '95,-10,0',    '0,0,-0.2588,0.9659'),
  @('robot.prefab',           'Robot',           '150,95,0',    '0,0,0.3827,0.9239'),
  @('spinning-top.prefab',    'Spinning Top',    '45,130,0',    '0,0,0,1'),
  @('building-blocks.prefab', 'Building Blocks', '70,-120,0',   '0,0,0.1305,0.9914'),
  @('xylophone.prefab',       'Xylophone',       '80,40,0',     '0,0,0.2588,0.9659'),
  @('letter-r.prefab',        'Letter R',        '40,5,0',      '0,0,0,1'),
  @('letter-a.prefab',        'Letter A',        '115,-55,0',   '0,0,0.3827,0.9239'),
  @('dinosaur.prefab',        'Brontosaurus',    '120,150,0',   '0,0,0.7071,0.7071'),
  @('dino-plush.prefab',      'Dino Plush',      '215,-40,35',  '0,0,0,1'),
  @('giraffe.prefab',         'Giraffe',         '215,25,40',   '0,0,0,1'),
  @('teddy-bear.prefab',      'Teddy Bear',      '200,-220,35', '0,0,-0.3,0.954'),
  # crayons
  @('crayon.prefab',          'Crayon 1',        '120,25,0',    '0,0,0.2,0.98'),
  @('crayon.prefab',          'Crayon 2',        '130,5,0',     '0,0,-0.5,0.866'),
  @('crayon.prefab',          'Crayon 3',        '108,50,0',    '0,0,0.7071,0.7071'),
  # books & desk clutter
  @('pencil-cup.prefab',      'Pencil Cup',      '210,195,92',  '0,0,0,1'),
  @('open-book.prefab',       'Open Book',       '195,140,92',  '0,0,0.1,0.995'),
  @('book.prefab',            'Book Shelf 1',    '228,150,118', '0,0,0,1'),
  @('book.prefab',            'Book Shelf 2',    '228,170,118', '0,0,0,1'),
  @('book.prefab',            'Book Floor 1',    '228,95,0',    '0,0,0.05,0.999'),
  @('book.prefab',            'Book Floor 2',    '228,95,9',    '0,0,-0.05,0.999'),
  @('book.prefab',            'Book Floor 3',    '228,96,18',   '0,0,0.1,0.995'),
  # wall decor - north wall (+X), face -X
  @('wall-clock.prefab',      'Wall Clock',      '242,-180,265','0,0,0,1'),
  @('framed-poster.prefab',   'Poster A',        '242,-110,250','0,0,0,1'),
  @('framed-poster.prefab',   'Poster B',        '242,-50,255', '0,0,0,1'),
  @('pennant.prefab',         'Pennant',         '242,5,210',   '0,0,0,1'),
  # wall decor - east wall (+Y), face -Y (yaw +90)
  @('cork-board.prefab',      'Cork Board',      '165,242,255', '0,0,0.70710678,0.70710678'),
  @('framed-poster.prefab',   'Poster C',        '60,242,250',  '0,0,0.70710678,0.70710678'),
  @('framed-poster.prefab',   'Poster D',        '-30,242,255', '0,0,0.70710678,0.70710678'),
  # rug
  @('rug.prefab',             'Rug',             '90,5,0',      '0,0,0,1')
)

$blocks = New-Object System.Collections.ArrayList

# --- static scene objects (literal JSON) ---
$null = $blocks.Add(@'
    {
      "__guid": "aaaa0001-0000-4000-8000-000000000001",
      "__version": 2, "Flags": 0, "Name": "Scene Information",
      "Position": "0,0,0", "Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": "", "Enabled": true,
      "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": true, "OwnerTransfer": 1,
      "Components": [ { "__type": "Sandbox.SceneInformation", "__guid": "aaaa0001-0000-4000-8000-000000000002", "__enabled": true, "Flags": 0, "Title": "robot room" } ],
      "Children": []
    }
'@)
$null = $blocks.Add(@'
    {
      "__guid": "aaaa0001-0000-4000-8000-000000000010",
      "__version": 2, "Flags": 0, "Name": "Sun",
      "Position": "0,0,0", "Rotation": "-0.688729584,0.160160467,0.356659502,0.610568404", "Scale": "1,1,1",
      "Tags": "light_directional,light", "Enabled": true,
      "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": true, "OwnerTransfer": 1,
      "Components": [ { "__type": "Sandbox.DirectionalLight", "__guid": "aaaa0001-0000-4000-8000-000000000011", "__enabled": true, "Flags": 0, "FogMode": "Enabled", "FogStrength": 1, "LightColor": "0.94419,0.97767,1,1", "Shadows": true, "ShadowBias": 0.0005, "ShadowCascadeCount": 4, "ShadowCascadeSplitRatio": 0.91, "ShadowHardness": 0, "SkyColor": "0.2532,0.32006,0.35349,1" } ],
      "Children": []
    }
'@)
$null = $blocks.Add(@'
    {
      "__guid": "aaaa0001-0000-4000-8000-000000000020",
      "__version": 2, "Flags": 0, "Name": "2D Skybox",
      "Position": "0,0,0", "Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": "skybox", "Enabled": true,
      "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": true, "OwnerTransfer": 1,
      "Components": [ { "__type": "Sandbox.SkyBox2D", "__guid": "aaaa0001-0000-4000-8000-000000000021", "__enabled": true, "Flags": 0, "SkyIndirectLighting": true, "SkyMaterial": "materials/skybox/skybox_day_01.vmat", "Tint": "1,1,1,1" }, { "__type": "Sandbox.EnvmapProbe", "__guid": "aaaa0001-0000-4000-8000-000000000022", "__enabled": true, "Flags": 0, "__version": 1, "Bounds": { "Mins": "-512,-512,-512", "Maxs": "512,512,512" }, "Projection": "Sphere", "Texture": "textures/cubemaps/default2.vtex", "TintColor": "1,1,1,1", "UpdateStrategy": "OnEnabled", "ZFar": 4096, "ZNear": 16 } ],
      "Children": []
    }
'@)
$null = $blocks.Add(@'
    {
      "__guid": "aaaa0001-0000-4000-8000-000000000030",
      "__version": 2, "Flags": 0, "Name": "Floor",
      "Position": "0,0,0", "Rotation": "0,0,0,1", "Scale": "7,7,7", "Tags": "floor", "Enabled": true,
      "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": true, "OwnerTransfer": 1,
      "Components": [ { "__type": "Sandbox.ModelRenderer", "__guid": "aaaa0001-0000-4000-8000-000000000031", "__enabled": true, "Flags": 0, "BodyGroups": 18446744073709551615, "MaterialOverride": "materials/plasticine_floor.vmat", "Model": "models/dev/plane.vmdl", "RenderType": "On", "Tint": "0.82,0.6,0.4,1" }, { "__type": "Sandbox.BoxCollider", "__guid": "aaaa0001-0000-4000-8000-000000000032", "__enabled": true, "Flags": 0, "Center": "0,0,-5", "Scale": "100,100,10", "Static": true } ],
      "Children": []
    }
'@)
$null = $blocks.Add(@'
    {
      "__guid": "aaaa0001-0000-4000-8000-000000000040",
      "__version": 2, "Flags": 0, "Name": "North Wall",
      "Position": "255,0,170", "Rotation": "0,-0.70710678,0,0.70710678", "Scale": "6,6,6", "Tags": "wall", "Enabled": true,
      "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": true, "OwnerTransfer": 1,
      "Components": [ { "__type": "Sandbox.ModelRenderer", "__guid": "aaaa0001-0000-4000-8000-000000000041", "__enabled": true, "Flags": 0, "BodyGroups": 18446744073709551615, "MaterialOverride": "materials/plasticine_floor.vmat", "Model": "models/dev/plane.vmdl", "RenderType": "On", "Tint": "0.52,0.66,0.9,1" } ],
      "Children": []
    }
'@)
$null = $blocks.Add(@'
    {
      "__guid": "aaaa0001-0000-4000-8000-000000000050",
      "__version": 2, "Flags": 0, "Name": "East Wall",
      "Position": "0,255,170", "Rotation": "0.70710678,0,0,0.70710678", "Scale": "6,6,6", "Tags": "wall", "Enabled": true,
      "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": true, "OwnerTransfer": 1,
      "Components": [ { "__type": "Sandbox.ModelRenderer", "__guid": "aaaa0001-0000-4000-8000-000000000051", "__enabled": true, "Flags": 0, "BodyGroups": 18446744073709551615, "MaterialOverride": "materials/plasticine_floor.vmat", "Model": "models/dev/plane.vmdl", "RenderType": "On", "Tint": "0.52,0.66,0.9,1" } ],
      "Children": []
    }
'@)
$null = $blocks.Add(@'
    {
      "__guid": "aaaa0001-0000-4000-8000-000000000060",
      "__version": 2, "Flags": 0, "Name": "Camera",
      "Position": "-345,-470,360", "Rotation": "-0.144858196,0.286023915,0.427963108,0.845017076", "Scale": "1,1,1", "Tags": "", "Enabled": true,
      "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": true, "OwnerTransfer": 1,
      "Components": [ { "__type": "Sandbox.CameraComponent", "__guid": "aaaa0001-0000-4000-8000-000000000061", "__enabled": true, "Flags": 0, "BackgroundColor": "0.4,0.55,0.62,1", "ClearFlags": "All", "EnablePostProcessing": true, "FieldOfView": 70, "FovAxis": "Horizontal", "IsMainCamera": true, "Orthographic": false, "Priority": 1, "RenderTags": "", "TargetEye": "None", "Viewport": "0,0,1,1", "ZFar": 10000, "ZNear": 10 }, { "__type": "Sandbox.Tonemapping", "__guid": "aaaa0001-0000-4000-8000-000000000062", "__enabled": true, "Flags": 0, "__version": 3, "AutoExposureEnabled": true, "ExposureCompensation": 0, "ExposureMethod": "RGB", "MaximumExposure": 2, "MinimumExposure": 1, "Mode": "AgX", "Rate": 1 } ],
      "Children": []
    }
'@)

# --- prop game objects ---
foreach ($p in $props) {
  $comp = Get-ComponentsText $p[0]
  $null = $blocks.Add( (New-GO $p[1] $p[2] $p[3] $comp) )
}

$go = ($blocks -join ",`r`n")

$scene = @"
{
  "__guid": "aaaa0001-0000-4000-8000-0000000000ff",
  "GameObjects": [
$go
  ],
  "SceneProperties": {
    "NetworkInterpolation": true,
    "TimeScale": 1,
    "WantsSystemScene": true,
    "Metadata": { "Title": "robot room" }
  },
  "ResourceVersion": 3,
  "Title": "robot room",
  "Description": null,
  "__references": [],
  "__version": 3
}
"@

$out = "d:\SBox\Projects\mimiclay\Assets\scenes\bedroom.scene"
Set-Content -Path $out -Value $scene -Encoding UTF8
Write-Output ("Wrote {0}" -f $out)
Write-Output ("Prop GameObjects: {0}  Total GameObjects: {1}" -f $props.Count, $blocks.Count)