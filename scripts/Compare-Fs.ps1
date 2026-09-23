<#
.SYNOPSIS
  Checks dirsizer.exe against an independent computation and against dirsizer-bulk, and measures worker counts.

.DESCRIPTION
  -Oracle  Sums the files under -Path with the framework's own directory enumeration (no code from dirsizer) and compares the
           root and every direct child directory with the --json output of dirsizer. Reparse-point directories are not entered,
           directories that cannot be read count as 0, like dirsizer. Slow (one thread): use a QUIESCENT tree of moderate size.
           Exit code 0 only if everything is equal.
  -Bulk    Runs dirsizer and dirsizer-bulk on -Path (a drive root, for example T:\) with a huge --top and lists every directory
           whose size differs and every directory only one tool reports. Differences are EXPECTED for hard links, NTFS metadata
           files and directories the caller cannot read; classify each one against docs\design_fs.md, section "Differences from
           the NTFS tools". dirsizer-bulk needs an elevated terminal. Not a pass/fail check.
  -Sweep   Runs dirsizer on -Path for each worker count in -Workers, -Runs times, alternating the counts, and prints the minimum,
           median and maximum of walk_ms. The default worker count of dirsizer is decided from this measurement.

  -Tool and -BulkTool are a .dll (run with dotnet) or an .exe.
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [switch]$Oracle,
    [switch]$Bulk,
    [switch]$Sweep,
    [int[]]$Workers = @(1, 2, 4, 8),
    [int]$Runs = 3,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll'),
    [string]$BulkTool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll')
)
$ErrorActionPreference = 'Stop'
if (-not ($Oracle -or $Bulk -or $Sweep)) { throw 'Choose at least one of -Oracle, -Bulk, -Sweep.' }

function Invoke-Tool([string]$Program, [string[]]$Arguments) {
    if ($Program -like '*.dll') { & dotnet $Program @Arguments } else { & $Program @Arguments }
}
function Get-Json([string]$Program, [string[]]$Arguments) {
    # The tool's own error message goes to stderr; keep it, so that a failure says why.
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $text = Invoke-Tool $Program $Arguments 2>$errorFile
        $code = $LASTEXITCODE
        if ($code -notin 0, 3) { throw "$Program exited with code ${code}: $((Get-Content -Raw $errorFile).Trim())" }
    } finally { [IO.File]::Delete($errorFile) }
    ($text -join "`n") | ConvertFrom-Json
}

$failures = 0

if ($Oracle) {
    function Get-DirectoryTotal([IO.DirectoryInfo]$Directory) {
        $sum = 0L
        try {
            foreach ($file in $Directory.EnumerateFiles()) { $sum += $file.Length }
            foreach ($child in $Directory.EnumerateDirectories()) {
                if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
                $sum += Get-DirectoryTotal $child
            }
        } catch [UnauthorizedAccessException], [IO.IOException] { }   # unreadable or vanished: counts as 0, like dirsizer
        $sum
    }
    "dirsizer against the framework's directory enumeration on $Path"
    $json = Get-Json $Tool @($Path, '--json', '--top=1000000')
    $rootDirectory = [IO.DirectoryInfo]::new($json.root.path)
    $expectedRoot = Get-DirectoryTotal $rootDirectory
    if ($expectedRoot -eq $json.root.size) { "  EQUAL      root  $($json.root.size)" }
    else { "  DIFFERENT  root  dirsizer=$($json.root.size) oracle=$expectedRoot"; $failures++ }
    $byPath = @{}
    foreach ($item in $json.root_children) { $byPath[$item.path] = $item.size }
    foreach ($child in $rootDirectory.EnumerateDirectories()) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $expected = Get-DirectoryTotal $child
        if (-not $byPath.ContainsKey($child.FullName)) { "  MISSING    $($child.FullName) is not among the root's children"; $failures++ }
        elseif ($byPath[$child.FullName] -eq $expected) { "  EQUAL      $($child.FullName)  $expected" }
        else { "  DIFFERENT  $($child.FullName)  dirsizer=$($byPath[$child.FullName]) oracle=$expected"; $failures++ }
    }
}

if ($Bulk) {
    "dirsizer against dirsizer-bulk on $Path (differences are expected; see docs\design_fs.md)"
    $volume = $Path.TrimEnd('\')
    $mine = Get-Json $Tool @($Path, '--json', '--top=10000000')
    $theirs = Get-Json $BulkTool @($volume, '--json', '--top=10000000')
    $mineBySize = @{}; foreach ($item in $mine.directories) { $mineBySize[$item.path] = $item.size }
    $theirsBySize = @{}; foreach ($item in $theirs.directories) { $theirsBySize[$item.path] = $item.size }
    $different = @(); $onlyMine = @(); $onlyTheirs = @(); $metadata = 0
    foreach ($key in $mineBySize.Keys) {
        if (-not $theirsBySize.ContainsKey($key)) { $onlyMine += $key }
        elseif ($theirsBySize[$key] -ne $mineBySize[$key]) { $different += "{0}  dirsizer={1:N0} bulk={2:N0}" -f $key, $mineBySize[$key], $theirsBySize[$key] }
    }
    foreach ($key in $theirsBySize.Keys) {
        if ($mineBySize.ContainsKey($key)) { continue }
        # NTFS system files and folders: listed under [NTFS metadata], or as a first path component that starts with '$' ($Extend).
        if ($key -like '*[[]NTFS metadata]*' -or $key -match '^.:\\\$') { $metadata++ } else { $onlyTheirs += $key }
    }
    "  directories: dirsizer $($mineBySize.Count), bulk $($theirsBySize.Count)"
    "  root: dirsizer {0:N0}, bulk {1:N0}" -f $mine.root.size, $theirs.root.size
    "  same path, different size: $($different.Count)"; $different | Select-Object -First 30 | ForEach-Object { "    $_" }
    "  only in dirsizer: $($onlyMine.Count)"; $onlyMine | Select-Object -First 30 | ForEach-Object { "    $_" }
    "  only in bulk (not NTFS metadata): $($onlyTheirs.Count)"; $onlyTheirs | Select-Object -First 30 | ForEach-Object { "    $_" }
    "  only in bulk, NTFS metadata: $metadata"
    "  dirsizer counters: denied=$($mine.statistics.directories_denied) failed=$($mine.statistics.directories_failed) reparse_skipped=$($mine.statistics.reparse_skipped)"
}

if ($Sweep) {
    "dirsizer worker sweep on $Path ($Runs runs per count, alternating)"
    $times = @{}; foreach ($w in $Workers) { $times[$w] = @() }
    for ($run = 1; $run -le $Runs; $run++) {
        foreach ($w in $Workers) {
            $json = Get-Json $Tool @($Path, '--json', '--top=1', '--workers', "$w")
            $times[$w] += [double]$json.statistics.performance.walk_ms
        }
    }
    foreach ($w in $Workers) {
        $sorted = @($times[$w] | Sort-Object)
        "  workers={0,-3} walk_ms  min={1,10:N1}  median={2,10:N1}  max={3,10:N1}" -f $w, $sorted[0], $sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)], $sorted[-1]
    }
}

if ($failures -gt 0) { "$failures difference(s)"; exit 1 }
exit 0
