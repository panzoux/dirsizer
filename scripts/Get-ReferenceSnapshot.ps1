<#
.SYNOPSIS
  Captures a deterministic snapshot of a usage-scan tool's output for regression checks.

.DESCRIPTION
  Runs dirsizer-fsctl (or dirsizer-bulk) with --json --dirs --files --diagnostics and a very large --top on a QUIESCENT
  volume, so the output lists every directory, every file with data, all root children, all counters, and every
  unresolved record. Timing and memory measurements are removed because they differ from run to run; everything else
  (paths, sizes, ordering, relationship counters, samples) is kept. Two snapshots of an unchanged volume must be
  identical, and a refactor that does not change behaviour must not change the snapshot.

  Use -Out to write the snapshot, then compare snapshots with Compare-Object or fc.exe.
#>
param(
    [string]$Volume = 'T:',
    [ValidateSet('fsctl', 'bulk')][string]$Tool = 'fsctl',
    # A .dll is run through the dotnet host, an .exe (for example a NativeAOT publish) directly. Default: the Release build of -Tool.
    [string]$Path = '',
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
# The bulk reader's project is DirSizer.Bulk, but its assembly has been dirsizer-mft since 0.6.0.
if (-not $Path) { $Path = Join-Path $PSScriptRoot $(if ($Tool -eq 'bulk') { '..\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll' } else { '..\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll' }) }

$stderrFile = [IO.Path]::GetTempFileName()
try {
    $cliArguments = @($Volume, '--json', '--dirs', '--files', '--top=1000000', '--diagnostics')
    $stdout = if ($Path -like '*.exe') { & $Path @cliArguments 2> $stderrFile } else { & dotnet $Path @cliArguments 2> $stderrFile }
    if ($LASTEXITCODE -ne 0) { throw "$Path exited with $LASTEXITCODE" }
    $document = $stdout | ConvertFrom-Json
    $performance = $document.statistics.performance
    foreach ($name in @($performance.PSObject.Properties.Name)) {
        if ($name -match '_ms$|managed_allocated_bytes|peak_working_set_bytes|per_second') { $performance.PSObject.Properties.Remove($name) }
    }
    # The bulk object holds this run's timings; its remaining fields (stability, counts) are deterministic.
    if ($document.bulk) { foreach ($name in @($document.bulk.PSObject.Properties.Name)) { if ($name -match '_ms$|per_sec') { $document.bulk.PSObject.Properties.Remove($name) } } }
    $normalized = $document | ConvertTo-Json -Depth 10
    # Progress is written with carriage returns; only the diagnostics lines are deterministic.
    $diagnostics = (Get-Content -Raw $stderrFile) -split "[\r\n]+" | Where-Object { $_ -like 'unresolved*' }
    Set-Content -Path $Out -Value ($normalized, '--- diagnostics ---', ($diagnostics -join "`n")) -Encoding utf8
} finally {
    Remove-Item $stderrFile -ErrorAction SilentlyContinue
}
"snapshot written: $Out ($((Get-Item $Out).Length) bytes)"