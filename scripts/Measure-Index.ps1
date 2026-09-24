<#
.SYNOPSIS
  Measures dirsizer-index: full scans against incremental runs on one volume.

.DESCRIPTION
  After one discarded warm-up, runs -Runs full scans (--rebuild), then -Runs incremental runs (each loads the index the
  run before saved and reads only what the USN journal names), all with --json, and prints min / median / max of every
  phase, the number of changes applied, and the index size. Index files go to a temporary directory, never to the
  default location. A live volume keeps changing, so incremental runs also show how many changes they applied.
#>
param(
    [string]$Volume = 'C:',
    [int]$Runs = 3,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll')
)
$ErrorActionPreference = 'Stop'
$target = "$($Volume.TrimEnd('\'))\"
$indexDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dirsizer-index-measure-' + [guid]::NewGuid().ToString('N'))

function Invoke-Index([string[]]$Extra) {
    $cliArguments = @($target, '--json', "--index-dir=$indexDirectory") + $Extra
    $stdout = if ($Tool -like '*.exe') { & $Tool @cliArguments 2> $null } else { & dotnet $Tool @cliArguments 2> $null }
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited with $LASTEXITCODE" }
    ($stdout | ConvertFrom-Json).index
}
function Get-Stats($samples) {
    $sorted = @($samples | Sort-Object)
    $median = if ($sorted.Count % 2) { $sorted[($sorted.Count - 1) / 2] } else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
    [pscustomobject]@{ Min = $sorted[0]; Median = $median; Max = $sorted[-1] }
}

try {
    'warm-up (discarded)...'
    $null = Invoke-Index @('--rebuild')
    $full = @(); $incremental = @()
    foreach ($run in 1..$Runs) { "full run ${run}/${Runs}"; $full += , (Invoke-Index @('--rebuild')) }
    foreach ($run in 1..$Runs) { "incremental run ${run}/${Runs}"; $incremental += , (Invoke-Index @()) }
    foreach ($row in $incremental) { if ($row.mode -ne 'incremental') { throw "an incremental run did a full scan: $($row.rebuild_reason)" } }
    'incremental runs saved as: ' + (($incremental | ForEach-Object { $_.save_kind }) -join ', ')
    ''
    "$Runs full and $Runs incremental runs on $target (min / median / max; milliseconds unless the name says otherwise)"
    foreach ($field in 'load_ms', 'usn_ms', 'update_ms', 'scan_ms', 'recompute_ms', 'save_ms', 'query_ms', 'total_ms', 'usn_changes', 'records_reread', 'records', 'index_bytes', 'delta_records', 'delta_bytes') {
        $f = Get-Stats ($full | ForEach-Object { $_.$field })
        $i = Get-Stats ($incremental | ForEach-Object { $_.$field })
        '{0,-15} FULL {1,14:N1} / {2,14:N1} / {3,14:N1}    INCREMENTAL {4,14:N1} / {5,14:N1} / {6,14:N1}' -f $field, $f.Min, $f.Median, $f.Max, $i.Min, $i.Median, $i.Max
    }
    $fullMedian = (Get-Stats ($full | ForEach-Object { $_.total_ms })).Median
    $incrementalMedian = (Get-Stats ($incremental | ForEach-Object { $_.total_ms })).Median
    'median total: incremental / full = {0:P0}' -f ($incrementalMedian / $fullMedian)
} finally {
    if (Test-Path $indexDirectory) { Remove-Item -Recurse -Force $indexDirectory }
}
