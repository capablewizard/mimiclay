# Train concept: low blue boiler, tall square cab, red roof and chassis,
# mustard chimney, two hollow clay wagons, broad dark wheels with inset hubs.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=Join-Path $root 'Assets/Prefabs/Props/Toys'
[IO.Directory]::CreateDirectory($out)|Out-Null
$template=[IO.File]::ReadAllText((Join-Path $root 'Assets/Prefabs/Props/Kitchen/mug.prefab'))
$inv=[Globalization.CultureInfo]::InvariantCulture
function V($a){($a|ForEach-Object{([double]$_).ToString('0.######',$inv)}) -join ','}
function Q($axis,[double]$deg){$t=$deg*[Math]::PI/360; V @(($axis[0]*[Math]::Sin($t)),($axis[1]*[Math]::Sin($t)),($axis[2]*[Math]::Sin($t)),([Math]::Cos($t)))}
$qx=Q @(1,0,0) 90
$qy=Q @(0,1,0) 90
$blue='0.09,0.27,0.44,1'; $red='0.70,0.19,0.085,1'; $gold='0.88,0.53,0.065,1'
$olive='0.30,0.37,0.16,1'; $rubber='0.27,0.235,0.175,1'; $hub='0.39,0.34,0.25,1'
function Brush($shape,$pos,$size,$color,[double]$round=1,[double]$blend=0,$op='Add',$rot='0,0,0,1'){
 [ordered]@{Shape=$shape;Operation=$op;CrossSection='Triangle';Text='clay';Font='Super Joyful';Enabled=$true;Position=(V $pos);Rotation=$rot;Size=(V $size);Points=$null;Curvature=1;SplineClosed=$false;SplinePerPointRadius=$false;Slice=0;Blend=$blend;Rounding=$round;Color=$color;Metallic=0;Roughness=0.65;MirrorX=$false;MirrorY=$false;MirrorZ=$false}
}
function Wheel([double]$x,[double]$radius,[double]$y=17){
 foreach($side in @(-1,1)){
  Brush 'Cylinder' @($x,($side*$y),$radius) @($radius,$radius,3.6) $rubber 1.8 0 'Add' $qx
  # Recess the face first, then set a broad hub into it.
  Brush 'Cylinder' @($x,($side*($y+3.7)),$radius) @(($radius*0.70),($radius*0.70),0.8) $rubber 0.6 0.15 'Subtract' $qx
  Brush 'Cylinder' @($x,($side*($y+3.25)),$radius) @(($radius*0.37),($radius*0.37),1.05) $hub 0.85 0 'Add' $qx
 }
}
function Prefab($name,$brushes){
 $md5=[Security.Cryptography.MD5]::Create()
 try{$s=[regex]::Replace($template,'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}',{param($m) ([Guid]::new($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("toy-train/${name}/$($m.Value)")))).ToString()})}finally{$md5.Dispose()}
 $p=$s.Replace('"__type"','"preservedType"')|ConvertFrom-Json
 $p.RootObject.Name=$name; $p.RootObject.Components[0].Brushes=@($brushes)
 return $p
}
function WritePrefab($name,$p){[IO.File]::WriteAllText((Join-Path $out "$name.prefab"),($p|ConvertTo-Json -Depth 60).Replace('"preservedType"','"__type"'),[Text.UTF8Encoding]::new($false))}
$engine=@(
 # Engine local front is -X. Two different wheel sizes, both touching Z=0.
 (Brush 'Box' @(-5,0,13.8) @(33,16.4,4.3) $red 3.6)
 (Brush 'Box' @(-34,0,11) @(10,17.3,5.3) $red 3.5 0.6 'Add' (Q @(0,1,0) -12))
 (Brush 'Box' @(13,0,38) @(14,14.4,23) $blue 2.4 0.7)
 (Brush 'Cylinder' @(-16,0,28) @(14.5,14.5,18.5) $blue 3.6 0.7 'Add' $qy)
 # Subtle flat boiler face, with two small handmade rivets.
 (Brush 'Cylinder' @(-34.3,0,28) @(11.5,11.5,0.5) '0.085,0.25,0.38,1' 0.5 0 'Add' $qy)
 (Brush 'Sphere' @(-34.9,-4.5,31.2) @(0.8,1.1,1.1) $hub 0.5)
 (Brush 'Sphere' @(-34.9,4.5,24.5) @(0.8,1.1,1.1) $hub 0.5)
 # Recessed mustard-green windows on both sides of the cab.
 (Brush 'Box' @(14,-14.6,45) @(5.6,1.3,7.5) '0.04,0.15,0.24,1' 2.2 0.35 'Subtract')
 (Brush 'Box' @(14,-13.7,45) @(4.8,0.65,6.6) '0.40,0.48,0.13,1' 1.9)
 (Brush 'Box' @(14,14.6,45) @(5.6,1.3,7.5) '0.04,0.15,0.24,1' 2.2 0.35 'Subtract')
 (Brush 'Box' @(14,13.7,45) @(4.8,0.65,6.6) '0.40,0.48,0.13,1' 1.9)
 (Brush 'Box' @(12.7,0,62.3) @(18,18,5.2) $red 3.8)
 # Short fat chimney, flattened cap and small raised crown.
 (Brush 'Cylinder' @(-21,0,47.7) @(5.5,5.5,8.4) $gold 1.1 0.4)
 (Brush 'Cylinder' @(-21,0,56.1) @(8,8,4.9) $gold 2.7 0.3)
 (Brush 'Sphere' @(-21.4,0,61) @(4.2,4.2,1.5) $gold 0.75 0.5)
 # Red wheel arch above the big driving wheel, merging into the frame.
 (Brush 'Cylinder' @(15,0,13.5) @(14,14,16) $red 1.5 0.4 'Add' $qx)
)
$engine+=Wheel -18 9.2
$engine+=Wheel 15 12.2
# Coupling sits low between engine and wagon, clearly below their bodies.
$engine+=Brush 'Box' @(33,0,12) @(8,3.5,2.2) $gold 1.5
$ep=Prefab 'toy_train_engine' $engine
WritePrefab 'toy_train_engine' $ep

function Wagon($name,$color,$inside){
 $b=@(
  (Brush 'Box' @(0,0,12.1) @(21.5,13.8,3.4) $gold 2)
  (Brush 'Box' @(0,0,28.2) @(22,16,14) $color 4.7)
  # Deep open tub; the subtraction rounds its inner corners and colours the inside.
  (Brush 'Box' @(0,0,36.4) @(17.3,11.5,13) $inside 3.2 0.5 'Subtract')
  (Brush 'Box' @(26.5,0,11.7) @(6.5,3,2) $gold 1.3)
 )
 $b+=Wheel -12.5 9.2 16
 $b+=Wheel 12.5 9.2 16
 return Prefab $name $b
}
$wp=Wagon 'toy_train_wagon_red' '0.75,0.29,0.09,1' '0.55,0.14,0.045,1'
$gp=Wagon 'toy_train_wagon_green' $olive '0.22,0.28,0.10,1'
# The final wagon does not need a trailing coupling.
$gp.RootObject.Components[0].Brushes=@($gp.RootObject.Components[0].Brushes|Where-Object { $_.Position -ne '26.5,0,11.7' })
WritePrefab 'toy_train_wagon_red' $wp
WritePrefab 'toy_train_wagon_green' $gp
$assembly=Prefab 'toy_train' @()
$assembly.RootObject.Components=@()
$ep.RootObject.Position='-55,0,0'; $wp.RootObject.Position='10,0,0'; $gp.RootObject.Position='63,0,0'
$assembly.RootObject.Children=@($ep.RootObject,$wp.RootObject,$gp.RootObject)
WritePrefab 'toy_train' $assembly
Write-Output "Created toy_train and three editable car prefabs in $out"
