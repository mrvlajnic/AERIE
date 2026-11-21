# Turns the bench-*.json reports into a table for the job summary.
param([string]$Folder = ".")
$rows = @()
$reports = Get-ChildItem $Folder -Filter "bench-*.json" | Sort-Object Name
if (-not $reports) { "No benchmark reports were written." ; exit 0 }
$labels = @()
foreach ($r in $reports) {
  $j = Get-Content $r.FullName -Raw | ConvertFrom-Json
  foreach ($s in $j.samples) { if ($labels -notcontains $s.label) { $labels += $s.label } }
}
$header = "| Setup | " + ($labels -join " | ") + " |"
$sep = "|---|" + (($labels | ForEach-Object { "---:" }) -join "|") + "|"
"### Memory, whole browser (private MB · working set MB · processes)"
""
$header
$sep
foreach ($r in $reports) {
  $j = Get-Content $r.FullName -Raw | ConvertFrom-Json
  $name = $r.BaseName -replace '^bench-', ''
  $cells = foreach ($l in $labels) {
    $s = $j.samples | Where-Object { $_.label -eq $l } | Select-Object -First 1
    if ($s) { "{0:N0} · {1:N0} · {2}" -f $s.privateMB, $s.workingSetMB, $s.processes } else { "–" }
  }
  "| $name ($($j.engine)) | " + ($cells -join " | ") + " |"
}
""
"### Page loads (ms; ✗ = didn't finish in 30 s)"
""
foreach ($r in $reports) {
  $j = Get-Content $r.FullName -Raw | ConvertFrom-Json
  $name = $r.BaseName -replace '^bench-', ''
  $loads = ($j.loads | ForEach-Object { $h = ([uri]$_.site).Host; if ($_.finished) { "$h $($_.ms)" } else { "$h ✗" } }) -join ", "
  "- **$name**: $loads"
}
""
"Working set and per-process breakdown are in the bench-*.json artifacts."
