<#
.SYNOPSIS
  Compares `dirsizer --reader=fsctl` with `dirsizer --reader=bulk` (both --json) on a QUIESCENT volume.

.DESCRIPTION
  Takes a snapshot with each reader through the product CLI (Get-ReferenceSnapshot.ps1 -Reader) and compares everything
  that must be the same: root size, every directory and file (path, size, order), the root's children, record and
  relationship counters, unresolved records, and relationship samples. It also checks that each snapshot names its
  reader and that the bulk scan reported itself stable. Reader-specific fields (records_scanned, query_count, timings,
  the `bulk` object) are not compared. Exit code 0 only if everything is equal.
#>
param(
    [string]$Volume = 'T:',
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Release\net8.0-windows\win-x64\DirSizer.dll'),
    [string]$OutDirectory = ([IO.Path]::GetTempPath())
)
$ErrorActionPreference = 'Stop'

$snapshotScript = Join-Path $PSScriptRoot 'Get-ReferenceSnapshot.ps1'
$fsctlFile = Join-Path $OutDirectory 'readers-fsctl.txt'
$bulkFile = Join-Path $OutDirectory 'readers-bulk.txt'
$null = & $snapshotScript -Volume $Volume -Dll $Exe -Reader fsctl -Out $fsctlFile
$null = & $snapshotScript -Volume $Volume -Dll $Exe -Reader bulk -Out $bulkFile

function Load([string]$file) {
    $parts = (Get-Content -Raw $file) -split '--- diagnostics ---'
    [pscustomobject]@{
        Json = ($parts[0] | ConvertFrom-Json)
        Diagnostics = @(($parts[1] -split "[\r\n]+") | Where-Object { $_ -like 'unresolved*' })
    }
}
$f = Load $fsctlFile
$b = Load $bulkFile

$failures = 0
function Check([string]$name, $expected, $actual) {
    $equal = (($expected | ConvertTo-Json -Depth 10 -Compress) -eq ($actual | ConvertTo-Json -Depth 10 -Compress))
    if ($equal) { "  EQUAL      $name" } else { "  DIFFERENT  $name  fsctl=$($expected | ConvertTo-Json -Depth 3 -Compress) bulk=$($actual | ConvertTo-Json -Depth 3 -Compress)"; $script:failures++ }
}
function CheckList([string]$name, $expected, $actual) {
    $e = @($expected); $a = @($actual)
    $first = -1
    for ($i = 0; $i -lt [Math]::Min($e.Count, $a.Count); $i++) { if ($e[$i].path -ne $a[$i].path -or $e[$i].size -ne $a[$i].size) { $first = $i; break } }
    if ($e.Count -eq $a.Count -and $first -lt 0) { "  EQUAL      $name ($($e.Count) items)" }
    else { "  DIFFERENT  $name  fsctl=$($e.Count) bulk=$($a.Count) first_difference_at=$first"; $script:failures++ }
}

"fsctl vs bulk through the dirsizer CLI on $Volume"
Check 'reader field (fsctl snapshot)' 'fsctl' $f.Json.reader
Check 'reader field (bulk snapshot)' 'bulk' $b.Json.reader
Check 'bulk scan reported stable in 1 attempt' @('stable', 1, 'None') @($b.Json.bulk.scan_stability, $b.Json.bulk.attempts, $b.Json.bulk.layout_changes)
Check 'volume, top, size_mode' @($f.Json.volume, $f.Json.top, $f.Json.size_mode) @($b.Json.volume, $b.Json.top, $b.Json.size_mode)
Check 'root' $f.Json.root $b.Json.root
CheckList 'directories (path, size, order)' $f.Json.directories $b.Json.directories
CheckList 'files (path, size, order)' $f.Json.files $b.Json.files
CheckList 'root children (path, size, order)' $f.Json.root_children $b.Json.root_children
foreach ($name in 'records_accepted', 'records_skipped', 'files', 'directories') { Check "statistics.$name" $f.Json.statistics.$name $b.Json.statistics.$name }
foreach ($name in 'returned_records', 'parse_successful_records', 'extension_records', 'logical_records', 'exact_relationships', 'fallback_relationships', 'fallback_zero_sequence_relationships', 'fallback_mismatch_relationships', 'unresolved_relationships') {
    Check "performance.$name" $f.Json.statistics.performance.$name $b.Json.statistics.performance.$name
}
Check 'relationship samples' @($f.Json.statistics.performance.relationship_samples) @($b.Json.statistics.performance.relationship_samples)
Check 'unresolved records' $f.Diagnostics $b.Diagnostics

# Text mode has its own code path: each reader's text listing must show the same sizes as its own JSON, and the two
# listings must be identical. (Text --files once printed 0 for every size while the JSON was right.)
function Get-TextListing([string]$reader) {
    $previous = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    try {
        $lines = if ($Exe -like '*.exe') { & $Exe $Volume --dirs --files --top=1000000 "--reader=$reader" 2> $null } else { & dotnet $Exe $Volume --dirs --files --top=1000000 "--reader=$reader" 2> $null }
    } finally { [Console]::OutputEncoding = $previous }
    $sections = @{}; $current = $null
    foreach ($line in $lines) {
        if ($line -match '^(Directories|Files) \(largest') { $current = $Matches[1]; $sections[$current] = New-Object System.Collections.Generic.List[string]; continue }
        if ($null -eq $current -or $line -eq '' -or $line -eq "Size`tPath") { continue }
        $sections[$current].Add($line)
    }
    $sections
}
function AsText($items) { @($items | ForEach-Object { "{0,12:N0}`t{1}" -f $_.size, $_.path }) }
$textFsctl = Get-TextListing 'fsctl'
$textBulk = Get-TextListing 'bulk'
Check 'text directories match the fsctl JSON' (AsText $f.Json.directories) @($textFsctl['Directories'])
Check 'text files match the fsctl JSON' (AsText $f.Json.files) @($textFsctl['Files'])
Check 'text directories match the bulk JSON' (AsText $b.Json.directories) @($textBulk['Directories'])
Check 'text files match the bulk JSON' (AsText $b.Json.files) @($textBulk['Files'])

if ($failures -eq 0) { 'RESULT: EQUAL (all checks)'; exit 0 } else { "RESULT: DIFFERENT ($failures checks)"; exit 2 }
