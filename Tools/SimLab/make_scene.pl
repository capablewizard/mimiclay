#!/usr/bin/perl
# Generates Assets/scenes/Tests/SimLab.scene — the networked-sim collision lab (see Tools/SimLab/README.md).
# One flat floor (top at z = 0) with a STATION per scenario: an empty GameObject "SimLab.<scenario>" whose children are
# MARKERS (A, A.to, B, L, H, ...: where each actor starts / walks to, read by code/Debug/SimLab.cs) plus the station's
# own static geometry (walls, a ledge, a traffic cone) and clay footballs that SimLab turns into loose props.
# Floor, sun and sky come from DefaultMap.scene; the mode card (direct play = Creative), camera prefab, spawn points
# and the clay prop prefabs from FlatGrass.scene. Re-run after editing the station table below:
#   perl Tools/SimLab/make_scene.pl
use strict; use warnings; use JSON::PP;

my $root = $0; $root =~ s#[\\/]Tools[\\/]SimLab[\\/]make_scene\.pl$##; $root = '.' if $root eq $0;
my $json = JSON::PP->new->relaxed->canonical->pretty;
sub load { my $f = "$root/$_[0]"; local $/; open my $h, '<:raw', $f or die "$f: $!"; return $json->decode(<$h>); }
my $default = load('Assets/scenes/Maps/DefaultMap.scene');
my $flat = load('Assets/scenes/Maps/FlatGrass/FlatGrass.scene');
my $dev = load('Assets/scenes/Maps/DevPropHunt.scene'); # (the garden stool + its cushion, as the user placed them with physics)

my $seq = 0; srand(20261107);
sub guid { sprintf '%08x-%04x-4%03x-a%03x-%012x', int(rand(2**32)), int(rand(2**16)), int(rand(2**12)), int(rand(2**12)), int(rand(2**48)) }
sub find_named { my ($scene, $name) = @_; for my $o (@{$scene->{GameObjects}}) { return $o if ($o->{Name} // '') eq $name; } die "no '$name'"; }
sub find_prefab { my ($scene, $path) = @_; my @todo = @{$scene->{GameObjects}}; while (my $o = shift @todo) { return $o if ref $o->{__Prefab} && $o->{__Prefab}{Path} eq $path; push @todo, @{$o->{Children} // []}; } die "no prefab $path"; }
# deep copy with fresh guids for every __guid (objects and components)
sub regen { my ($x) = @_; my $c = $json->decode($json->encode($x)); my $walk; $walk = sub { my $o = shift; if (ref $o eq 'HASH') { $o->{__guid} = guid() if exists $o->{__guid}; $walk->($_) for values %$o; } elsif (ref $o eq 'ARRAY') { $walk->($_) for @$o; } }; $walk->($c); return $c; }
sub v3 { join ',', map { sprintf '%g', $_ } @_ }
sub empty_go { my ($name, @pos) = @_; return { __guid => guid(), __version => 2, Flags => 0, Name => $name, Position => v3(@pos), Rotation => '0,0,0,1', Scale => '1,1,1', Tags => '', Enabled => JSON::PP::true, NetworkMode => 2, NetworkTransmit => JSON::PP::true, NetworkFlags => 0, NetworkOrphaned => 0, OwnerTransfer => 1, Components => [], Children => [] }; }

# A static box (dev box model + box collider): size in units, centre in station-local coordinates.
sub box_go {
	my ($name, $size, $centre) = @_;
	my $go = empty_go($name, @$centre);
	$go->{Scale} = v3(map { $_ / 50 } @$size);
	push @{$go->{Components}}, { __type => 'Sandbox.ModelRenderer', __guid => guid(), __enabled => JSON::PP::true, Flags => 0, BodyGroups => 18446744073709551615, CreateAttachments => JSON::PP::false, MaterialOverride => 'materials/simlab/simlab_box.vmat', Model => 'models/dev/box.vmdl', RenderType => 'On', Tint => '1,1,1,1' };
	push @{$go->{Components}}, { __type => 'Sandbox.BoxCollider', __guid => guid(), __enabled => JSON::PP::true, Flags => 0, Center => '0,0,0', IsTrigger => JSON::PP::false, Scale => '50,50,50', Static => JSON::PP::true };
	return $go;
}

# A prefab instance copied from FlatGrass (or the scene given; fresh instance ids), renamed and placed.
sub prefab_go {
	my ($path, $name, $pos, $from) = @_;
	my $src = find_prefab($from // $flat, $path);
	my $c = $json->decode($json->encode($src));
	my ($rootId) = map { $_->{Target}{IdValue} } grep { $_->{Property} eq 'Position' } @{$c->{__PrefabInstancePatch}{PropertyOverrides}};
	$c->{__PrefabIdToInstanceId}{$_} = guid() for keys %{$c->{__PrefabIdToInstanceId}};
	$c->{__guid} = $c->{__PrefabIdToInstanceId}{$rootId};
	$c->{__PrefabInstancePatch}{PropertyOverrides} = [
		{ Property => 'Name', Target => { IdValue => $rootId, Type => 'GameObject' }, Value => $name },
		{ Property => 'Position', Target => { IdValue => $rootId, Type => 'GameObject' }, Value => v3(@$pos) },
		{ Property => 'Rotation', Target => { IdValue => $rootId, Type => 'GameObject' }, Value => '0,0,0,1' },
	];
	return $c;
}

# ── The stations ────────────────────────────────────────────────────────────────────────────────────────
# name => [ origin x, origin y, { marker => [x,y], ... }, [ statics ] ]   (markers are station-local, z = 0 = the floor)
# Markers: A / B / H = start; A.to, A.to2, B.to = walk targets (SimLab.cs owns the timing); L = a clay football made loose;
# Hobs = where an idle hunter watches from. Statics: ['box', name, [sx,sy,sz], [cx,cy,cz]] or ['cone', name, [x,y]].
my @grid_x = (-1350, -450, 450, 1350); my @grid_y = (-1800, -600, 600, 1800);
my @stations = (
	[ loose       => { L => [0, 0], A => [0, -280], 'A.to' => [0, 400] }, [] ],
	[ static      => { A => [0, 230], 'A.to' => [0, -500], 'A.to2' => [270, 50], Cone => [0, 0] }, [ [ 'cone', 'Cone', [0, 0] ] ] ],
	[ push        => { B => [0, 0], A => [0, -280], 'A.to' => [0, 400] }, [] ],
	[ headon      => { A => [0, -250], B => [0, 150], 'A.to' => [0, 400], 'B.to' => [0, -500] }, [] ],
	[ held        => { H => [0, 250], L => [0, 110], A => [0, -250], 'A.to' => [0, 400] }, [] ],
	[ swing       => { H => [0, 250], L => [0, 110], A => [0, 100], 'A.wait' => [150, -300] }, [] ],
	[ throw       => { H => [0, 250], L => [0, 110], A => [88, -10] }, [] ],
	[ jump        => { B => [0, 0], A => [0, -280], 'A.to' => [0, 400] }, [] ],
	[ lying       => { B => [0, 0], A => [0, -280], 'A.to' => [0, 400] }, [] ],
	[ wall        => { A => [-150, -150], 'A.to' => [300, 450] }, [ [ 'box', 'Wall', [400, 20, 120], [100, 150, 60] ] ] ],
	[ ledge       => { A => [0, -200], 'A.to' => [0, 400], Ledge => [0, 150] }, [ [ 'box', 'Ledge', [200, 200, 40], [0, 150, 20] ] ] ],
	[ pushwall    => { L => [0, 0], A => [0, -280], 'A.to' => [0, 600] }, [ [ 'box', 'Wall', [300, 20, 120], [0, 260, 60] ] ] ],
	[ twopush     => { L => [0, 0], A => [0, -250], 'A.to' => [0, 400], B => [0, 250], 'B.to' => [0, -400] }, [] ],
	[ throwplayer => { H => [0, 250], B => [0, 110], A => [88, -10] }, [] ],
	# climbing (2026-11-07): a wall to climb and mantle, a pillar to climb round (outer corners), an L of walls (inner), a pillar for the hunter.
	# An optional 4th element is the station's origin (the grid's last row is where the park's strays go).
	[ climb       => { A => [0, -150], 'A.to' => [0, 150] }, [ [ 'box', 'Wall', [300, 60, 110], [0, 150, 55] ] ] ],
	[ corner      => { A => [0, -220], 'A.to' => [0, 0] }, [ [ 'box', 'Pillar', [100, 100, 260], [0, 0, 130] ] ], [-1350, 2700] ],
	[ inner       => { A => [0, -120], 'A.to' => [0, 150] }, [ [ 'box', 'WallA', [300, 30, 220], [0, 150, 110] ], [ 'box', 'WallB', [30, 330, 220], [165, 0, 110] ] ], [-450, 2700] ],
	[ hcorner     => { H => [0, -100] }, [ [ 'box', 'Pillar', [120, 120, 300], [0, 0, 150] ] ], [450, 2700] ],
	# grab (2026-11-13): H lifts devprophunt's garden stool with its cushion resting on it (scene clay, touching — the wake,
	# the grip, held friction). The stool's pivot sits 42.13u above its feet; the cushion rests 41.155u above the stool.
	[ stack       => { H => [0, 85], Stool => [0, 0], Hobs => [300, 200] }, [ [ 'stool', 'Stool', [0, 0] ] ], [1350, 2700] ],
);

my @gos;
push @gos, regen(find_named($default, 'Scene Information'));
push @gos, regen(find_named($default, 'Sun'));
push @gos, regen(find_named($default, '2D Skybox'));
my $floor = regen(find_named($default, 'Plane')); $floor->{Name} = 'Floor'; $floor->{Position} = '0,0,0'; $floor->{Scale} = '60,60,5'; $_->{MaterialOverride} = 'materials/simlab/simlab_floor.vmat' for grep { ($_->{__type} // '') eq 'Sandbox.ModelRenderer' } @{$floor->{Components}}; push @gos, $floor;
my $card = regen(find_named($flat, 'Map Mode Card')); $card->{Components}[0]{DirectPlayGame} = 'Creative'; push @gos, $card;
my $cam = $json->decode($json->encode(find_prefab($flat, 'prefabs/main_camera.prefab'))); $cam->{__PrefabIdToInstanceId}{$_} = guid() for keys %{$cam->{__PrefabIdToInstanceId}};
{ my ($rid) = keys %{$cam->{__PrefabIdToInstanceId}}; } # (camera keeps its patch; ids regenerated)
$cam->{__guid} = (values %{$cam->{__PrefabIdToInstanceId}})[0]; push @gos, $cam;

# spawn points: a ring near the middle, on the floor
my $spawnSrc = (grep { ($_->{Name} // '') eq 'spawns' } @{$flat->{GameObjects}})[0]{Children}[0];
my $spawns = empty_go('Spawns', 0, 0, 0);
for my $i (0 .. 5) { my $s = regen($spawnSrc); $s->{Name} = "Spawn $i"; my $a = $i * 3.14159 / 3; $s->{Position} = v3(150 * cos($a), 150 * sin($a), 10); push @{$spawns->{Children}}, $s; }
push @gos, $spawns;

my $i = 0;
for my $st (@stations) {
	my ($name, $markers, $statics, $origin) = @$st;
	my ($ox, $oy) = $origin ? @$origin : ($grid_x[$i % 4], $grid_y[int($i / 4)]); $i++;
	my $sgo = empty_go("SimLab.$name", $ox, $oy, 0);
	$markers->{Hobs} //= [300, -100];
	for my $m (sort keys %$markers) { push @{$sgo->{Children}}, empty_go($m, @{$markers->{$m}}, 0); }
	for my $s (@$statics) {
		if ($s->[0] eq 'box') { push @{$sgo->{Children}}, box_go($s->[1], $s->[2], $s->[3]); }
		elsif ($s->[0] eq 'cone') { push @gos, prefab_go('prefabs/props/misc/trafficcone.prefab', "SimLab.$name.$s->[1]", [ $ox + $s->[2][0], $oy + $s->[2][1], 0 ]); }
		elsif ($s->[0] eq 'stool') {
			my ($x, $y) = ($ox + $s->[2][0], $oy + $s->[2][1]);
			push @gos, prefab_go('prefabs/props/garden/gardenstool.prefab', "SimLab.$name.Stool", [ $x, $y, 42.13 ], $dev);
			push @gos, prefab_go('prefabs/props/garden/gardenstoolcushion.prefab', "SimLab.$name.Cushion", [ $x, $y, 42.13 + 41.155 ], $dev);
		}
	}
	push @gos, prefab_go('football.prefab', "SimLab.$name.L", [ $ox + $markers->{L}[0], $oy + $markers->{L}[1], 30 ]) if $markers->{L};
	push @gos, $sgo;
}
# parking for actors a scenario does not use
my $park = empty_go('SimLab.park', 2600, 2600, 0); push @{$park->{Children}}, empty_go('A', 0, 0, 0), empty_go('B', 200, 0, 0), empty_go('H', 400, 0, 0); push @gos, $park;

my $scene = { __guid => guid(), __version => $default->{__version} // 3, ResourceVersion => 3, Title => 'SimLab', Description => 'Networked sim collision lab (Tools/SimLab)', __references => [], GameObjects => \@gos, SceneProperties => $json->decode($json->encode($flat->{SceneProperties})) };
$scene->{SceneProperties}{GameObjectSystems} = { 'Sandbox.SelectionSetsSystem' => { Data => { SelectionSets => [] } } };
$scene->{SceneProperties}{Metadata} = { Title => 'SimLab' };
$scene->{SceneProperties}{NavMesh}{Enabled} = JSON::PP::false if ref $scene->{SceneProperties}{NavMesh};
mkdir "$root/Assets/scenes/Tests";
open my $out, '>:raw', "$root/Assets/scenes/Tests/SimLab.scene" or die $!; print $out $json->encode($scene); close $out;
printf "wrote Assets/scenes/Tests/SimLab.scene: %d top-level objects, %d stations\n", scalar(@gos), scalar(@stations);
