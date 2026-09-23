<#
.SYNOPSIS
  End-to-end test of dirsizer-index on a disposable NTFSTEST volume.

.DESCRIPTION
  Covers the full scan, incremental updates after real changes (checked against a fresh scan with --verify), and the
  fallbacks to a full scan: journal recreated, journal disabled, damaged index file, --rebuild. One step creates empty
  files until $MFT grows (NTFS writes no journal entry for that, so it checks the metadata re-reads); the MFT never
  shrinks, so every run leaves it somewhat larger.
  Writes only inside <Volume>\index-test. It deletes and recreates the volume's USN journal to test the fallbacks; at
  the end the journal is active (with a new id). Index files go to a temporary directory. Refuses volumes that are not
  NTFS or not labelled NTFSTEST (-AllowAnyLabel overrides). Needs an elevated shell and a Release build. Nothing else
  may write to the volume while it runs, because --verify compares with a fresh scan. fsutil output is localized, so
  only its exit codes are used.
  Exit code 0 only if every check passed.
#>
param(
    [string]$Volume = 'T:',
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll'),
    [string]$InspectTool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll'),
    [switch]$AllowAnyLabel
)
$ErrorActionPreference = 'Stop'
$Volume = $Volume.TrimEnd('\')
$vol = Get-Volume -DriveLetter $Volume.TrimEnd(':')
if ($vol.FileSystem -ne 'NTFS') { throw "$Volume is not NTFS." }
if (-not $AllowAnyLabel -and $vol.FileSystemLabel -ne 'NTFSTEST') { throw "$Volume is labelled '$($vol.FileSystemLabel)', not NTFSTEST. Pass -AllowAnyLabel to override." }

$root = "$Volume\index-test"
$indexDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dirsizer-index-test-' + [guid]::NewGuid().ToString('N'))
$failures = 0

function Invoke-Index([string]$Target, [string[]]$Extra = @()) {
    $stderrFile = [IO.Path]::GetTempFileName()
    try {
        $cliArguments = @($Target, '--json', "--index-dir=$indexDirectory") + $Extra
        $stdout = if ($Tool -like '*.exe') { & $Tool @cliArguments 2> $stderrFile } else { & dotnet $Tool @cliArguments 2> $stderrFile }
        [pscustomobject]@{ Exit = $LASTEXITCODE; Json = $(if ($stdout) { $stdout | ConvertFrom-Json } else { $null }); Stderr = (Get-Content -Raw $stderrFile) }
    } finally { Remove-Item $stderrFile -ErrorAction SilentlyContinue }
}
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { "  PASS  $Name" } else { "  FAIL  $Name  $Detail"; $script:failures++ }
}
function Check-Mode($Result, [string]$Mode, [string]$Name) {
    Check "${Name}: mode $Mode" ($Result.Json -and $Result.Json.index.mode -eq $Mode) "exit=$($Result.Exit) mode=$($Result.Json.index.mode) reason=$($Result.Json.index.rebuild_reason) $($Result.Stderr)"
}
function Check-Verified($Result, [string]$Name) {
    Check "${Name}: verify 0 differences, exit 0" ($Result.Exit -eq 0 -and $Result.Json.verify.differences -eq 0) "exit=$($Result.Exit) $($Result.Stderr)"
}
function Write-Bytes([string]$Path, [int]$Size) {
    $null = New-Item -ItemType Directory -Force (Split-Path $Path)
    [IO.File]::WriteAllBytes($Path, [byte[]]::new($Size))
}
# MFT record slots, from dirsizer-inspect's "MFT valid length : N bytes = S record slots" line (not localized).
function Get-MftSlots {
    $text = if ($InspectTool -like '*.exe') { & $InspectTool $Volume --volume } else { & dotnet $InspectTool $Volume --volume }
    $line = $text | Where-Object { $_ -match '^MFT valid length' }
    if ($line -notmatch '= ([\d,]+) record slots') { throw "cannot read the MFT size from dirsizer-inspect: $text" }
    [long]($Matches[1] -replace ',', '')
}
function Test-Journal { $null = fsutil usn queryjournal $Volume 2>&1; $LASTEXITCODE -eq 0 }
function Set-Journal([bool]$Active) {
    $null = fsutil usn deletejournal /d $Volume 2>&1
    for ($i = 0; $i -lt 50 -and (Test-Journal); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not $Active) { return }
    for ($i = 0; $i -lt 50; $i++) {
        $null = fsutil usn createjournal m=33554432 a=4194304 $Volume 2>&1
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 200   # the old journal may still be being deleted
    }
    throw "fsutil usn createjournal $Volume kept failing"
}

try {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    if (-not (Test-Journal)) { "creating a USN journal on $Volume"; Set-Journal $true }

    '--- first run: full scan, index saved'
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'full' 'first run'
    Check 'first run: reason "no saved index"' ($r.Json.index.rebuild_reason -eq 'no saved index') $r.Json.index.rebuild_reason
    Check 'first run: saved' ($r.Json.index.saved -eq $true)
    Check 'first run: journal id recorded' ($r.Json.index.journal_id -ne 0)
    Check-Verified $r 'first run'

    '--- no changes: incremental'
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'no changes'
    Check-Verified $r 'no changes'

    '--- created: files, directories, and a file with 20 long hard links (its names need extension records)'
    Write-Bytes "$root\plain\a.bin" 1000
    Write-Bytes "$root\plain\sub\b.bin" 20000
    Write-Bytes "$root\plain\sub\c.bin" 300
    Write-Bytes "$root\move-me\m.bin" 4096
    Write-Bytes "$root\doomed\d1.bin" 5000
    Write-Bytes "$root\doomed\deeper\d2.bin" 6000
    Write-Bytes "$root\links\target.bin" 7777
    $longName = 'L' * 120
    for ($i = 0; $i -lt 20; $i++) {
        $null = fsutil hardlink create "$root\links\$longName$i.bin" "$root\links\target.bin"
        if ($LASTEXITCODE -ne 0) { throw "fsutil hardlink create failed" }
    }
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after creating'
    Check-Verified $r 'after creating'
    Check 'after creating: journal entries applied' ($r.Json.index.usn_changes -gt 0) "usn_changes=$($r.Json.index.usn_changes)"
    Check 'after creating: extension records read' ($r.Json.index.extension_reads -gt 0) "extension_reads=$($r.Json.index.extension_reads)"

    '--- edited: grow, shrink, rename, move, delete a tree, grow a linked file, drop a link'
    [IO.File]::WriteAllBytes("$root\plain\a.bin", [byte[]]::new(3000))
    [IO.File]::WriteAllBytes("$root\plain\sub\c.bin", [byte[]]::new(10))
    Rename-Item "$root\plain\sub\b.bin" 'b-renamed.bin'
    Move-Item "$root\move-me" "$root\plain\moved"
    Remove-Item -Recurse -Force "$root\doomed"
    [IO.File]::AppendAllText("$root\links\${longName}3.bin", ('x' * 1000))
    Remove-Item "$root\links\${longName}5.bin"
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after editing'
    Check-Verified $r 'after editing'
    Check 'after editing: records removed' ($r.Json.index.records_removed -gt 0) "records_removed=$($r.Json.index.records_removed)"

    '--- $MFT grows (no journal entry for $MFT itself), then its new records are deleted again'
    $slots = Get-MftSlots
    $null = New-Item -ItemType Directory -Force "$root\many"
    $created = 0
    while ((Get-MftSlots) -le $slots) {
        if ($created -ge 200000) { throw "`$MFT did not grow after $created files" }
        for ($i = 0; $i -lt 500; $i++) { [IO.File]::Create("$root\many\f$created.bin").Dispose(); $created++ }
    }
    "  created $created empty files; MFT slots $slots -> $(Get-MftSlots)"
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after $MFT grew'
    Check-Verified $r 'after $MFT grew'
    Remove-Item -Recurse -Force "$root\many"
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after deleting them'
    Check-Verified $r 'after deleting them'
    Check 'after deleting them: records removed' ($r.Json.index.records_removed -ge $created) "records_removed=$($r.Json.index.records_removed) created=$created"

    '--- journal recreated: full scan, then incremental again'
    $oldJournal = $r.Json.index.journal_id
    Set-Journal $true
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'journal recreated'
    Check 'journal recreated: reason' ($r.Json.index.rebuild_reason -match 'recreated') $r.Json.index.rebuild_reason
    Check 'journal recreated: new journal id' ($r.Json.index.journal_id -ne $oldJournal)
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after the recreated journal'
    Check-Verified $r 'after the recreated journal'

    '--- journal disabled: full scans until it is back'
    Set-Journal $false
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'no journal'
    Check 'no journal: reason' ($r.Json.index.rebuild_reason -match 'no active USN journal') $r.Json.index.rebuild_reason
    Check 'no journal: saved with journal id 0' ($r.Json.index.saved -eq $true -and $r.Json.index.journal_id -eq 0)
    Set-Journal $true
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'journal back'
    Check 'journal back: reason' ($r.Json.index.rebuild_reason -match 'without a USN journal') $r.Json.index.rebuild_reason
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'journal back, second run'
    Check-Verified $r 'journal back, second run'

    '--- damaged index file: full scan'
    $file = $r.Json.index.file
    $bytes = [IO.File]::ReadAllBytes($file)
    $middle = [int]($bytes.Length / 2)
    $bytes[$middle] = $bytes[$middle] -bxor 0x40
    [IO.File]::WriteAllBytes($file, $bytes)
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'damaged file'
    Check 'damaged file: reason' ($r.Json.index.rebuild_reason -match 'checksum') $r.Json.index.rebuild_reason

    '--- --rebuild'
    $r = Invoke-Index "$Volume\" @('--rebuild')
    Check-Mode $r 'full' '--rebuild'
    Check '--rebuild: reason' ($r.Json.index.rebuild_reason -match 'rebuild') $r.Json.index.rebuild_reason
} finally {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    if (Test-Path $indexDirectory) { Remove-Item -Recurse -Force $indexDirectory }
    if (-not (Test-Journal)) { Set-Journal $true }
}
''
if ($failures -eq 0) { 'ALL CHECKS PASSED'; exit 0 } else { "$failures CHECK(S) FAILED"; exit 1 }
