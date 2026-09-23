<#
.SYNOPSIS
  Alternating FSCTL / bulk benchmark with min, median, and max per phase.

.DESCRIPTION
  Runs dirsizer-fsctl and dirsizer-bulk alternately (after one discarded warm-up pair) and prints the
  phase timings each reports in its `benchmark:` line. Both are run through the same `dotnet` host. The FSCTL "query"
  phase corresponds to the bulk extents + raw_read + fixup phases ("acquire" row).

  A live volume changes between runs, so record counts drift slightly; use a quiescent volume for equality checks.
#>
param(
    [string]$Volume = 'C:',
    [int]$Runs = 5,
    # Dismount the volume before every run, so each run starts with an empty NTFS cache for it. Not for the system drive.
    [switch]$ColdDismount,
    [string]$FsctlDll = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll'),
    [string]$BulkDll = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll'),
    # Extra command-line arguments for each side (none are needed for the two tools).
    [string[]]$FsctlArguments = @(),
    [string[]]$BulkArguments = @()
)
# A path ending in .exe (for example a NativeAOT publish) is run directly; a .dll is run through the dotnet host.
$ErrorActionPreference = 'Stop'
if ($ColdDismount -and $Volume.TrimEnd('\') -ieq $env:SystemDrive) { throw "-ColdDismount cannot be used on the system drive $env:SystemDrive." }

function Invoke-Benchmark([string]$Dll, [string[]]$Arguments) {
    if ($ColdDismount) { $null = fsutil volume dismount $Volume; if ($LASTEXITCODE -ne 0) { throw "fsutil volume dismount $Volume failed" } }
    $stderrFile = [IO.Path]::GetTempFileName()
    try {
        if ($Dll -like '*.exe') { & $Dll @Arguments > $null 2> $stderrFile } else { & dotnet $Dll @Arguments > $null 2> $stderrFile }
        if ($LASTEXITCODE -ne 0) { throw "$Dll exited with $LASTEXITCODE" }
        $line = (Get-Content -Raw $stderrFile) -split "[\r\n]+" | Where-Object { $_ -like 'benchmark:*' } | Select-Object -First 1
        if (-not $line) { throw "no benchmark line from $Dll" }
    } finally { Remove-Item $stderrFile -ErrorAction SilentlyContinue }
    # Values are formatted with thousands separators (e.g. total_ms=8,821.5), so split on key=number, not on commas.
    $values = @{}
    foreach ($match in [regex]::Matches($line, '(\w+)=(\d[\d,]*(?:\.\d+)?)')) {
        $values[$match.Groups[1].Value] = [double]::Parse(($match.Groups[2].Value -replace ',', ''), [Globalization.CultureInfo]::InvariantCulture)
    }
    $values
}

function Get-Stats($samples) {
    $sorted = @($samples | Sort-Object)
    $median = if ($sorted.Count % 2) { $sorted[($sorted.Count - 1) / 2] } else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
    [pscustomobject]@{ Min = $sorted[0]; Median = $median; Max = $sorted[-1] }
}

$fsctl = @(); $bulk = @()
"warm-up pair (discarded)..."
$null = Invoke-Benchmark $FsctlDll (@($Volume, '--benchmark') + $FsctlArguments)
$null = Invoke-Benchmark $BulkDll (@($Volume, '--benchmark') + $BulkArguments)
foreach ($run in 1..$Runs) {
    "run ${run}/${Runs}: FSCTL, then bulk"
    $fsctl += , (Invoke-Benchmark $FsctlDll (@($Volume, '--benchmark') + $FsctlArguments))
    $bulk += , (Invoke-Benchmark $BulkDll (@($Volume, '--benchmark') + $BulkArguments))
}

function Row([string]$label, $rows, [scriptblock]$value, [string]$unit = 'ms') {
    $samples = $rows | ForEach-Object { & $value $_ }
    if ($null -eq ($samples | Where-Object { $null -ne $_ } | Select-Object -First 1)) { return $null }
    $s = Get-Stats $samples
    [pscustomobject]@{ Phase = $label; Min = $s.Min; Median = $s.Median; Max = $s.Max }
}
function Show([string]$title, $rowsFsctl, $rowsBulk, $definitions) {
    $title
    foreach ($d in $definitions) {
        $f = Row $d.Name $rowsFsctl $d.Fsctl
        $b = Row $d.Name $rowsBulk $d.Bulk
        '{0,-22} FSCTL {1,9:N1} / {2,9:N1} / {3,9:N1}    BULK {4,9:N1} / {5,9:N1} / {6,9:N1}' -f $d.Name,
            $(if ($f) { $f.Min } else { [double]::NaN }), $(if ($f) { $f.Median } else { [double]::NaN }), $(if ($f) { $f.Max } else { [double]::NaN }),
            $(if ($b) { $b.Min } else { [double]::NaN }), $(if ($b) { $b.Median } else { [double]::NaN }), $(if ($b) { $b.Max } else { [double]::NaN })
    }
}

''
"$Runs alternating runs on $Volume$(if ($ColdDismount) { ', volume dismounted before every run (cold NTFS cache)' })  (each cell: min / median / max, milliseconds unless noted)"
$definitions = @(
    @{ Name = 'open';                 Fsctl = { $args[0]['open_ms'] };          Bulk = { $args[0]['open_ms'] } },
    @{ Name = 'volume metadata';      Fsctl = { $args[0]['volume_ms'] };        Bulk = { $args[0]['volume_ms'] } },
    @{ Name = 'acquire (query | extents+read+fixup)'; Fsctl = { $args[0]['query_ms'] }; Bulk = { $args[0]['extents_ms'] + $args[0]['raw_read_ms'] + $args[0]['fixup_ms'] } },
    @{ Name = '  extents';            Fsctl = { $null };                        Bulk = { $args[0]['extents_ms'] } },
    @{ Name = '  raw_read';           Fsctl = { $null };                        Bulk = { $args[0]['raw_read_ms'] } },
    @{ Name = '  fixup (classify+USA)'; Fsctl = { $null };                      Bulk = { $args[0]['fixup_ms'] } },
    @{ Name = '  raw read MB/s (not ms)'; Fsctl = { $null };                    Bulk = { $args[0]['raw_MB_per_sec'] } },
    @{ Name = '  raw read operations (count)'; Fsctl = { $null };               Bulk = { $args[0]['raw_reads'] } },
    @{ Name = 'stability re-check';   Fsctl = { $null };                        Bulk = { $args[0]['stability_ms'] } },
    @{ Name = 'parser';               Fsctl = { $args[0]['parser_ms'] };        Bulk = { $args[0]['parser_ms'] } },
    @{ Name = 'merge';                Fsctl = { $args[0]['merge_ms'] };         Bulk = { $args[0]['merge_ms'] } },
    @{ Name = 'relationships';        Fsctl = { $args[0]['relationships_ms'] }; Bulk = { $args[0]['relationships_ms'] } },
    @{ Name = 'aggregation';          Fsctl = { $args[0]['aggregation_ms'] };   Bulk = { $args[0]['aggregation_ms'] } },
    @{ Name = 'finalize';             Fsctl = { $args[0]['finalize_ms'] };      Bulk = { $args[0]['finalize_ms'] } },
    @{ Name = 'other';                Fsctl = { $args[0]['other_ms'] };         Bulk = { $args[0]['other_ms'] } },
    @{ Name = 'TOTAL';                Fsctl = { $args[0]['total_ms'] };         Bulk = { $args[0]['total_ms'] } },
    @{ Name = 'managed alloc (MB)';   Fsctl = { $args[0]['managed_allocated'] / 1MB }; Bulk = { $args[0]['managed_allocated'] / 1MB } },
    @{ Name = 'peak working set (MB)'; Fsctl = { $args[0]['peak_working_set'] / 1MB }; Bulk = { $args[0]['peak_working_set'] / 1MB } }
)
Show '' $fsctl $bulk $definitions
''
# Every run must account for all of its time: phase_sum_ms must equal total_ms in both readers.
$bad = 0
foreach ($run in @($fsctl) + @($bulk)) { if ([math]::Abs($run['phase_sum_ms'] - $run['total_ms']) -gt 0.11) { $bad++ } }
if ($bad -eq 0) { "phase accounting: phase_sum_ms == total_ms in all $($fsctl.Count + $bulk.Count) measured runs" } else { "phase accounting FAILED in $bad runs" }
$retried = @($bulk | Where-Object { $_['attempts'] -gt 1 }).Count
if ($retried) { "WARNING: $retried bulk runs were rescanned because the MFT layout changed; their totals cover only the final attempt" }
$ratios = for ($i = 0; $i -lt $Runs; $i++) { $fsctl[$i]['total_ms'] / $bulk[$i]['total_ms'] }
$r = Get-Stats $ratios
'per-pair total speedup (FSCTL total / bulk total): min {0:N2}x  median {1:N2}x  max {2:N2}x' -f $r.Min, $r.Median, $r.Max
