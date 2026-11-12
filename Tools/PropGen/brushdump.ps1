param([string[]]$Files)
function Walk($o, $path) {
  if ($null -eq $o) { return }
  if ($o -is [System.Management.Automation.PSCustomObject]) {
    foreach ($p in $o.PSObject.Properties) {
      if ($p.Name -eq 'Brushes' -and $p.Value -is [array]) {
        Write-Output "  [$path] $($p.Value.Count) brushes"
        foreach ($b in $p.Value) {
          $extra = @()
          if ($b.Taper) { $extra += "taper=$($b.Taper)" }
          if ($b.Slice -and $b.Shape -eq 'Sphere') { $extra += "slice=$($b.Slice)/$($b.SlicePlaneN)" }
          if ($b.MirrorX -or $b.MirrorY -or $b.MirrorZ) { $extra += "mirror=$($b.MirrorX)$($b.MirrorY)$($b.MirrorZ)" }
          if ($b.Shape -eq 'Extruded') { $extra += "xs=$($b.CrossSection)" }
          if ($b.Points) { $extra += "closed=$($b.SplineClosed) pts=" + (($b.Points | ForEach-Object { $_ }) -join ' | ') }
          if ($b.Smooth) { $extra += "smooth=$($b.Smooth)" }
          if ($b.Enabled -eq $false) { $extra += "DISABLED" }
          Write-Output ("   {0,-9} {1,-8} pos=({2}) rot=({3}) size=({4}) blend={5} round={6} col=({7}) rough={8} met={9} {10}" -f $b.Shape,$b.Operation,$b.Position,$b.Rotation,$b.Size,$b.Blend,$b.Rounding,$b.Color,$b.Roughness,$b.Metallic,($extra -join ' '))
        }
      } elseif ($p.Name -eq 'Name' -and $p.Value -is [string]) {
        $path = "$path/$($p.Value)"
      } else { Walk $p.Value $path }
    }
  } elseif ($o -is [array]) { foreach ($x in $o) { Walk $x $path } }
}
foreach ($f in $Files) {
  Write-Output "== $f"
  $j = Get-Content -Raw $f | ConvertFrom-Json
  Walk $j ''
}
