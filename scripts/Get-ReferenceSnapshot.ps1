<#
.SYNOPSIS
  Captures a deterministic snapshot of the FSCTL reference executable's output for regression checks.

.DESCRIPTION
  Runs DirSizer.dll with --json --dirs --files --diagnostics and a very large --top on a QUIESCENT volume, so the output
  lists every directory, every file with data, all root children, all counters, and every unresolved record. Timing and
  memory measurements are removed because they differ from run to run; everything else (paths, sizes, ordering,
  relationship counters, samples) is kept. Two snapshots of an unchanged volume must be identical, and a refactor that
  does not change behaviour must not change the snapshot.

  Use -Out to write the snapshot, then compare snapshots with Compare-Object or fc.exe.
#>
param(
    [string]$Volume = 'T:',
    [string]$Dll = (Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\win-x64\DirSizer.dll'),
    [ValidateSet('', 'fsctl', 'bulk')][string]$Reader = '',   # empty = the CLI default
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'

$stderrFile = [IO.Path]::GetTempFileName()
try {
    # A path ending in .exe (for example a NativeAOT publish) is run directly; a .dll is run through the dotnet host.
    $cliArguments = @($Volume, '--json', '--dirs', '--files', '--top=1000000', '--diagnostics')
    if ($Reader) { $cliArguments += "--reader=$Reader" }
    $stdout = if ($Dll -like "*.exe") { & $Dll @cliArguments 2> $stderrFile } else { & dotnet $Dll @cliArguments 2> $stderrFile }
    if ($LASTEXITCODE -ne 0) { throw "DirSizer exited with $LASTEXITCODE" }
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
