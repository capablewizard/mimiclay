# Shared shaft + ring grip for the sand-toy set (spade, fork, ...). Numbers from the user-tuned spade (2026-11-11):
# shaft tilts ~8.85 deg up toward the ring, everything Blend 0 (crisp parts, no blobby joins), no collar.
# The fork's user tune used ShaftRounding 8 (fully rounded bar) and a slightly shorter shaft.
# Dot-source after SdfPropKit, then:  $brushes += SandToyHandle $colour [-ShaftRounding 8] [-NoRing]
function SandToyHandle([string]$col, [double]$ShaftRounding = 3.4, [double]$ShaftHalfLength = 23.116,
                       [double]$ShaftX = -9.65, [switch]$NoRing) {
    $shaft = B @{ Shape = 'Box'; Position = (V3 $ShaftX 0 13.785); Rotation = (Qaxis 0 1 0 8.852); Size = (V3 $ShaftHalfLength 4.704 3.92)
                  Rounding = $ShaftRounding; Blend = 0; Color = $col; Roughness = 0.4 }
    if ($NoRing) { return @($shaft) }
    # ring: circle tilted to follow the shaft (u leans down toward +X)
    $pts = ArcPoints @(-40.48, 0, 18.784) @(0.9912, 0, -0.1318) @(0, 1, 0) 9.04 8.961 0 360 12 3.659
    $ring = B @{ Shape = 'Spline'; Points = $pts[0..11]; SplineClosed = $true; Size = '1,1,1'
                 Blend = 0; Rounding = 0.75; Color = $col; Roughness = 0.4 }
    @($shaft, $ring)
}
