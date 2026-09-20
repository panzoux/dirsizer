<#
.SYNOPSIS
  End-to-end test of the bulk reader's live-instability detection on a real NTFS test volume.

.DESCRIPTION
  Holds the window between the scan and the MFT layout re-check open (a test-only delay) and creates enough files during
  that window to grow the MFT beyond its captured length.
    A. nothing changes                     -> exit 0, stable, 1 attempt
    B. MFT grows, no retry                 -> exit 3, UNSTABLE, changes include Grew, results still printed
    C. MFT grows during the first attempt  -> exit 0, stable after 1 retry (2 attempts)

  Two ways to run it:
    default   the DirSizer.Bulk test harness (its --test-delay-after-scan-ms and --no-retry options)
    -Product  the real CLI, `dirsizer --reader=bulk --json`, using the test-only environment variables
              DIRSIZER_TEST_BULK_DELAY_AFTER_SCAN_MS and DIRSIZER_TEST_BULK_NO_RETRY; results are read from the JSON
              `bulk` object and the exit code. Those variables are honoured only by the "TestHooks" build configuration,
              which this mode builds for itself (dotnet build -c TestHooks); Debug and Release builds and publishes
              ignore them and do not contain them.

  Files are created under <Volume>\instab-test and removed afterwards. The volume must be a disposable NTFS test volume
  (label NTFSTEST unless -AllowAnyLabel).

  Side effects that cannot be undone: the MFT never shrinks, so every run makes it larger (one 1 MiB step per round,
  two rounds per run), and the deleted records stay behind as free MFT slots. The $MFT file is part of the folder-size
  listing, so a listing snapshot taken before this test is no longer valid afterwards: take a new one. Because free
  slots accumulate, each round creates (free slots + 1) files, which takes longer every run; -MaxFilesPerRound stops
  a run that would need more.
#>
[CmdletBinding()]
param(
    [string]$Volume = 'T:',
    [switch]$Product,
    [string]$BulkExe = (Join-Path $PSScriptRoot '..\bin\Bulk\Release\net8.0-windows\win-x64\DirSizer.Bulk.exe'),
    [string]$ProductDll = (Join-Path $PSScriptRoot '..\bin\TestHooks\net8.0-windows\win-x64\DirSizer.dll'),
    [int]$MaxFilesPerRound = 12000,
    [switch]$AllowAnyLabel
)
$ErrorActionPreference = 'Stop'

$letter = $Volume.TrimEnd(':', '\')
$vol = Get-Volume -DriveLetter $letter
if ($vol.FileSystem -ne 'NTFS') { throw "$Volume is not NTFS." }
if (-not $AllowAnyLabel -and $vol.FileSystemLabel -ne 'NTFSTEST') { throw "$Volume is labelled '$($vol.FileSystemLabel)', not NTFSTEST. Pass -AllowAnyLabel to override." }
$work = "${letter}:\instab-test"
$failures = 0
if ($Product) {
    "building the TestHooks configuration of dirsizer..."
    & dotnet build (Join-Path $PSScriptRoot '..\DirSizer.csproj') -c TestHooks --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'building the TestHooks configuration failed' }
}

# Runs the reader once. Returns exit code, stderr, stdout, and (product mode) the parsed JSON.
function Run-Reader([int]$DelayMs, [bool]$NoRetry, [scriptblock]$WhileWaiting = $null) {
    $stderr = [IO.Path]::GetTempFileName(); $stdout = [IO.Path]::GetTempFileName()
    if ($Product) {
        $env:DIRSIZER_TEST_BULK_DELAY_AFTER_SCAN_MS = "$DelayMs"
        $env:DIRSIZER_TEST_BULK_NO_RETRY = if ($NoRetry) { '1' } else { '' }
        $file = 'dotnet'; $arguments = @($ProductDll, $Volume, '--reader=bulk', '--json', '--top=1', '--benchmark')
    } else {
        $file = $BulkExe; $arguments = @($Volume, '--top=1', '--benchmark', "--test-delay-after-scan-ms=$DelayMs")
        if ($NoRetry) { $arguments += '--no-retry' }
    }
    try {
        $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardError $stderr -RedirectStandardOutput $stdout
        if ($WhileWaiting) { Start-Sleep -Milliseconds 1500; & $WhileWaiting | Out-Host }   # the first scan of a small volume is done well within 1.5 s
        $process.WaitForExit()
    } finally { Remove-Item Env:\DIRSIZER_TEST_BULK_DELAY_AFTER_SCAN_MS, Env:\DIRSIZER_TEST_BULK_NO_RETRY -ErrorAction SilentlyContinue }
    $out = Get-Content -Raw $stdout
    $result = [pscustomobject]@{ ExitCode = $process.ExitCode; Stderr = (Get-Content -Raw $stderr); Stdout = $out; Json = $null }
    if ($Product -and $out) { $result.Json = $out | ConvertFrom-Json }
    Remove-Item $stderr, $stdout -ErrorAction SilentlyContinue
    $result
}

# The MFT only grows once every free slot (deleted or never-used) is taken, so create one more file than there are free slots.
function Get-FreeSlots {
    $stderr = [IO.Path]::GetTempFileName()
    & $BulkExe $Volume --top=1 > $null 2> $stderr
    $text = Get-Content -Raw $stderr; Remove-Item $stderr
    if ($text -notmatch 'unused_slots=(\d+)' -or $text -notmatch 'deleted_slots=(\d+)') { throw 'could not read free slot counts from the bulk reader' }
    [int]([regex]::Match($text, 'unused_slots=(\d+)').Groups[1].Value) + [int]([regex]::Match($text, 'deleted_slots=(\d+)').Groups[1].Value)
}
# Sized before the reader starts, so the re-check window can be made long enough for the file creation to finish.
function Get-GrowthPlan {
    $count = (Get-FreeSlots) + 1
    if ($count -gt $MaxFilesPerRound) { throw "growing the MFT would need $count files (limit $MaxFilesPerRound); this test volume has accumulated too many free MFT slots" }
    [pscustomobject]@{ Count = $count; DelayMs = 3000 + 6 * $count }
}
function Grow-Mft([string]$Tag, [int]$count) {
    "  creating $count files to exceed the free MFT slots"
    New-Item -ItemType Directory -Force "$work\$Tag" | Out-Null
    1..$count | ForEach-Object { [IO.File]::WriteAllBytes("$work\$Tag\f$_.bin", [byte[]]::new(10)) }
}
function Expect([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:failures++ }
}
function Line($result, [string]$prefix) { ($result.Stderr -split "[\r\n]+" | Where-Object { $_ -like "$prefix*" }) -join ' | ' }
# Stability as reported by the reader under test.
function Stability($result) {
    if ($Product) { "$($result.Json.bulk.scan_stability) attempts=$($result.Json.bulk.attempts) changes=$($result.Json.bulk.layout_changes)" }
    else { Line $result 'scan_stability' }
}

"mode: $(if ($Product) { 'product CLI (dirsizer --reader=bulk --json)' } else { 'DirSizer.Bulk test harness' })"
try {
    'A. no change during the window'
    $a = Run-Reader 500 $false
    Expect 'exit code 0' ($a.ExitCode -eq 0) "exit=$($a.ExitCode)"
    Expect 'reported stable in 1 attempt' ((Stability $a) -like '*stable attempts=1*') (Stability $a)

    'B. MFT grows during the window, no retry'
    $plan = Get-GrowthPlan
    $b = Run-Reader $plan.DelayMs $true { Grow-Mft 'round1' $plan.Count }
    Expect 'exit code 3' ($b.ExitCode -eq 3) "exit=$($b.ExitCode)"
    Expect 'reported UNSTABLE with Grew' ((Stability $b) -match 'UNSTABLE|unstable' -and (Stability $b) -match 'Grew') (Stability $b)
    Expect 'results were still printed' ($(if ($Product) { $null -ne $b.Json.root } else { $b.Stdout -like '*Directories (largest*' })) 'no result on stdout'
    if ($Product) { Expect 'a warning was written to stderr' ((Line $b 'warning: scan_stability=UNSTABLE') -ne '') $b.Stderr }

    'C. MFT grows during the first attempt, one retry allowed'
    $plan = Get-GrowthPlan
    $c = Run-Reader $plan.DelayMs $false { Grow-Mft 'round2' $plan.Count }
    Expect 'first attempt reported as changed' ((Line $c 'scan_stability=changed') -like '*Grew*') (Line $c 'scan_stability')
    Expect 'second attempt stable, exit code 0' ($c.ExitCode -eq 0 -and (Stability $c) -match 'stable.*attempts=2') "exit=$($c.ExitCode) $(Stability $c)"
    $discarded = if ($Product) { [double]$c.Json.bulk.discarded_attempt_ms } else { [double]([regex]::Match((Line $c 'benchmark:'), 'discarded_attempt_ms=([\d,\.]+)').Groups[1].Value -replace ',', '') }
    Expect 'discarded attempt is reported, not hidden in a phase' ($discarded -ge 1) "discarded_attempt_ms=$discarded"
} finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
}
''
if ($failures -eq 0) { 'RESULT: PASS'; exit 0 } else { "RESULT: FAIL ($failures)"; exit 1 }
