<#
.SYNOPSIS
  Compares dirsizer's directory enumerators: whether they give identical results, and how long they take.

.DESCRIPTION
  -Equal  For every -Path, takes a canonical snapshot (Get-FsSnapshot.ps1) with each enumerator in -Specs and compares it with the
          snapshot of the first one (the reference, normally `find`). Identical means byte for byte: the root, every directory and
          its size, the counters. Use QUIESCENT trees. Exit code 0 only if every snapshot is identical.
  -Time   For every -Path, runs each enumerator -Rounds times, alternating them and rotating the order in each round, with a fixed
          number of workers (-Workers, default 8: the enumerator is the only variable). Prints the min / median / max of walk_ms
          (wall clock, the primary metric) and the median of enum_ms_total (worker time, a diagnostic, not the elapsed time) and of
          entries per second. Warm file cache; cold cache is not measured.

  -Specs are --enumerator values: find, find:nolarge, handle:idextd:64, handle:full:4, nt:dir:64 ...
  -Tool is a .dll (run with dotnet) or an .exe.

.EXAMPLE
  .\scripts\Compare-Enumerators.ps1 -Path T:\, D:\ -Equal -Specs find, handle:idextd:64, nt:dir:64
  .\scripts\Compare-Enumerators.ps1 -Path C:\ -Time -Rounds 5 -Specs find, handle:full:64, nt:dir:64
#>
param(
    [Parameter(Mandatory)][string[]]$Path,
    [string[]]$Specs = @('find', 'handle:idextd:64', 'nt:dir:64'),
    [switch]$Equal,
    [switch]$Time,
    [int]$Rounds = 5,
    [int]$Workers = 8,
    [switch]$Files,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll')
)
$ErrorActionPreference = 'Stop'
if (-not ($Equal -or $Time)) { throw 'Choose -Equal, -Time or both.' }
if ($Specs.Count -lt 1) { throw '-Specs needs at least one enumerator.' }

function Invoke-Scan([string]$Target, [string]$Spec) {
    $arguments = @($Target, '--json', '--top=1', '--workers', "$Workers", "--enumerator=$Spec")
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $text = if ($Tool -like '*.dll') { & dotnet $Tool @arguments 2>$errorFile } else { & $Tool @arguments 2>$errorFile }
        $code = $LASTEXITCODE
        if ($code -notin 0, 3) { throw "$Tool exited with code ${code} for '$Spec' on ${Target}: $((Get-Content -Raw $errorFile).Trim())" }
    } finally { [IO.File]::Delete($errorFile) }
    ($text -join "`n") | ConvertFrom-Json
}

$failures = 0

if ($Equal) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) "enumerator-snapshots-$([guid]::NewGuid().ToString('N'))"
    [void](New-Item -ItemType Directory $temp)
    try {
        "Snapshot equality (workers=$Workers): the first spec is the reference"
        foreach ($target in $Path) {
            "  $target"
            $reference = $null
            $index = 0
            foreach ($spec in $Specs) {
                $file = Join-Path $temp ("snapshot-{0}.json" -f $index++)
                $snapshotArguments = @{ Path = $target; Tool = $Tool; Enumerator = $spec; Workers = $Workers; Out = $file }
                if ($Files) { $snapshotArguments.Files = $true }
                # An enumerator that does not work on this file system (for example a class it does not support) is a result, not a
                # reason to stop: say what it reported and go on. It counts as a failure of the comparison.
                try { $summary = & "$PSScriptRoot\Get-FsSnapshot.ps1" @snapshotArguments }
                catch { "    FAILED     {0}: {1}" -f $spec, ($_.Exception.Message -replace '^.*exited with code \d+: ', ''); $failures++; continue }
                $text = [IO.File]::ReadAllText($file)
                if ($null -eq $reference) { $reference = $text; "    reference  {0,-20} {1}" -f $spec, ($summary -replace '^snapshot: .*?\s{2}', '') }
                elseif ($text -ceq $reference) { "    EQUAL      {0}" -f $spec }
                else {
                    "    DIFFERENT  {0}" -f $spec
                    $a = $reference -split "`n"; $b = $text -split "`n"
                    Compare-Object $a $b | Select-Object -First 6 | ForEach-Object { "               {0} {1}" -f $_.SideIndicator, $_.InputObject.Trim() }
                    $failures++
                }
            }
        }
    } finally { if (Test-Path $temp) { [IO.Directory]::Delete($temp, $true) } }
}

if ($Time) {
    foreach ($target in $Path) {
        "Timing on $target (workers=$Workers, $Rounds rounds, alternating, order rotated each round)"
        $walk = @{}; $enum = @{}; $rate = @{}
        foreach ($spec in $Specs) { $walk[$spec] = @(); $enum[$spec] = @(); $rate[$spec] = @() }
        $roots = @{}
        for ($round = 0; $round -lt $Rounds; $round++) {
            for ($i = 0; $i -lt $Specs.Count; $i++) {
                $spec = $Specs[($i + $round) % $Specs.Count]
                $json = Invoke-Scan $target $spec
                $performance = $json.statistics.performance
                $walk[$spec] += [double]$performance.walk_ms
                $enum[$spec] += [double]$performance.enum_ms_total
                $rate[$spec] += [double]$performance.entries_per_sec
                $roots[$spec] = $json.root.size
            }
        }
        function Get-Median($values) { $s = @($values | Sort-Object); $s[[int][Math]::Floor(($s.Count - 1) / 2)] }
        foreach ($spec in $Specs) {
            $sorted = @($walk[$spec] | Sort-Object)
            "  {0,-20} walk_ms min={1,9:N1} median={2,9:N1} max={3,9:N1}   enum_ms_total median={4,10:N1}   entries/s median={5,9:N0}   root={6:N0}" -f `
                $spec, $sorted[0], (Get-Median $walk[$spec]), $sorted[-1], (Get-Median $enum[$spec]), (Get-Median $rate[$spec]), $roots[$spec]
        }
        $base = Get-Median $walk[$Specs[0]]
        foreach ($spec in $Specs | Select-Object -Skip 1) {
            $change = ((Get-Median $walk[$spec]) - $base) / $base * 100
            "  {0,-20} vs {1}: {2:+0.0;-0.0} % walk_ms (median)" -f $spec, $Specs[0], $change
        }
    }
}

if ($failures -gt 0) { "$failures snapshot(s) differ"; exit 1 }
exit 0
