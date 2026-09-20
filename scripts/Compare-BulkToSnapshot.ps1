<#
.SYNOPSIS
  Compares the raw bulk reader's full output with a snapshot of the FSCTL reference reader (Get-ReferenceSnapshot.ps1).

.DESCRIPTION
  Both must be taken on the same QUIESCENT volume. Checks, in order and exactly:
    root size, every directory (path, size), every file with data (path, size), the root's direct children,
    record/relationship counters, unresolved records, and relationship mismatch samples.
  Exit code 0 only if every check is equal.
#>
param(
    [string]$Volume = 'T:',
    [string]$BulkExe = (Join-Path $PSScriptRoot '..\bin\Bulk\Release\net8.0-windows\win-x64\DirSizer.Bulk.exe'),
    [Parameter(Mandatory)][string]$Snapshot
)
$ErrorActionPreference = 'Stop'

$snapshotText = Get-Content -Raw $Snapshot
$parts = $snapshotText -split "--- diagnostics ---"
$reference = $parts[0] | ConvertFrom-Json
$referenceDiagnostics = @(($parts[1] -split "[\r\n]+") | Where-Object { $_ -like 'unresolved*' })

# The listing contains Unicode names (including surrogate pairs); capture stdout as UTF-8 so console code pages cannot alter it.
$previousEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$stderrFile = [IO.Path]::GetTempFileName()
try {
    $stdout = & $BulkExe $Volume --dirs --files --root-children --top=1000000 --diagnostics --benchmark 2> $stderrFile
    if ($LASTEXITCODE -ne 0) { throw "Bulk exited with $LASTEXITCODE" }
    $stderr = Get-Content $stderrFile
} finally { [Console]::OutputEncoding = $previousEncoding; Remove-Item $stderrFile -ErrorAction SilentlyContinue }

# Parse the "Size<TAB>Path" sections of stdout.
$sections = @{}
$current = $null
foreach ($line in $stdout) {
    if ($line -match '^(Directories|Files) \(largest|^Root children$') { $current = ($line -split ' ')[0]; if ($current -eq 'Root') { $current = 'RootChildren' }; $sections[$current] = New-Object System.Collections.Generic.List[object]; continue }
    if ($null -eq $current -or $line -eq '' -or $line -eq "Size`tPath") { continue }
    $tab = $line.IndexOf("`t")
    if ($tab -lt 0) { continue }
    $sections[$current].Add([pscustomobject]@{ path = $line.Substring($tab + 1); size = [int64](($line.Substring(0, $tab).Trim()) -replace ',', '') })
}

$failures = 0
function Check([string]$name, [bool]$equal, [string]$detail = '') {
    if ($equal) { "  EQUAL      $name" } else { "  DIFFERENT  $name  $detail"; $script:failures++ }
}
function CompareItems([string]$name, $expected, $actual) {
    $e = @($expected); $a = @($actual)
    $first = -1
    for ($i = 0; $i -lt [Math]::Min($e.Count, $a.Count); $i++) {
        if ($e[$i].path -ne $a[$i].path -or [int64]$e[$i].size -ne [int64]$a[$i].size) { $first = $i; break }
    }
    if ($e.Count -eq $a.Count -and $first -lt 0) { Check "$name ($($e.Count) items)" $true; return }
    $detail = "reference=$($e.Count) bulk=$($a.Count)"
    if ($first -ge 0) { $detail += " first_difference_at=$first reference='$($e[$first].path)':$($e[$first].size) bulk='$($a[$first].path)':$($a[$first].size)" }
    Check $name $false $detail
}

foreach ($key in @($sections.Keys)) { $sections[$key] = $sections[$key].ToArray() }
"Bulk vs FSCTL reference snapshot on $Volume"
Check "root size" ($sections['Directories'][0].path -eq $reference.root.path -and $sections['Directories'][0].size -eq $reference.root.size) "reference=$($reference.root.size) bulk=$($sections['Directories'][0].size)"
CompareItems 'directories (path, size, order)' $reference.directories $sections['Directories']
CompareItems 'files (path, size, order)' $reference.files $sections['Files']
CompareItems 'root children (path, size, order)' $reference.root_children $sections['RootChildren']

# Counters from the benchmark line.
$benchmark = $stderr | Where-Object { $_ -like 'benchmark:*' } | Select-Object -First 1
$bulk = @{}
foreach ($pair in ($benchmark -replace '^benchmark:\s*', '') -split ',\s*') { $kv = $pair -split '=', 2; if ($kv.Count -eq 2) { $bulk[$kv[0].Trim()] = $kv[1].Trim() } }
$p = $reference.statistics.performance
$counters = [ordered]@{
    'records_accepted' = @($reference.statistics.records_accepted, $bulk['records_accepted'])
    'files' = @($reference.statistics.files, $bulk['files'])
    'directories' = @($reference.statistics.directories, $bulk['directories'])
    'returned' = @($p.returned_records, $bulk['returned'])
    'parsed' = @($p.parse_successful_records, $bulk['parsed'])
    'extensions' = @($p.extension_records, $bulk['extensions'])
    'logical_records' = @($p.logical_records, $bulk['logical_records'])
    'exact relationships' = @($p.exact_relationships, $bulk['exact'])
    'fallback relationships' = @($p.fallback_relationships, $bulk['fallback'])
    'fallback zero-sequence' = @($p.fallback_zero_sequence_relationships, $bulk['fallback_zero'])
    'fallback mismatch' = @($p.fallback_mismatch_relationships, $bulk['fallback_mismatch'])
    'unresolved relationships' = @($p.unresolved_relationships, $bulk['unresolved'])
}
foreach ($name in $counters.Keys) {
    $pair = $counters[$name]
    Check "counter $name = $($pair[0])" ("$($pair[0])" -eq "$($pair[1])") "reference=$($pair[0]) bulk=$($pair[1])"
}

$bulkDiagnostics = @($stderr | Where-Object { $_ -like 'unresolved*' })
Check "unresolved records ($($referenceDiagnostics.Count - 1) listed)" (($referenceDiagnostics -join "`n") -eq ($bulkDiagnostics -join "`n")) "reference=[$($referenceDiagnostics -join ' | ')] bulk=[$($bulkDiagnostics -join ' | ')]"
$bulkSamples = @($stderr | Where-Object { $_ -like 'relationship_sample:*' } | ForEach-Object { $_ -replace '^relationship_sample:\s*', '' })
$referenceSamples = @($p.relationship_samples)
Check "relationship samples ($($referenceSamples.Count))" (($referenceSamples -join "`n") -eq ($bulkSamples -join "`n")) "reference=$($referenceSamples.Count) bulk=$($bulkSamples.Count)"

if ($failures -eq 0) { "RESULT: EQUAL (all checks)"; exit 0 } else { "RESULT: DIFFERENT ($failures checks failed)"; exit 2 }
