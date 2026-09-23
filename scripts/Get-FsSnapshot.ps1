<#
.SYNOPSIS
  Writes a canonical snapshot of a dirsizer scan, so that two builds or two enumerators can be compared byte for byte.

.DESCRIPTION
  Runs dirsizer.exe (or dirsizer.dll) on -Path with --json and a --top larger than any tree, and writes a normalised JSON file:
  the volume and root, the counters (directories_scanned, directories_denied, directories_failed, reparse_skipped,
  directories, files, bytes), every directory with its size and, with -Files, every file, and the root's children. The lists
  are sorted by path (ordinal), because entries of equal size come in no defined order. The timing, memory and enumerator
  fields, and the error samples (operating-system language), are left out: they are not what a comparison is about.
  Use a QUIESCENT tree; a tree that changes between two scans gives two different snapshots.
  Exit code 0 when the snapshot was written; 1 when the tool failed.

.EXAMPLE
  .\scripts\Get-FsSnapshot.ps1 -Path T:\ -Out artifacts\snapshots\before-T.json
  .\scripts\Get-FsSnapshot.ps1 -Path T:\ -Enumerator handle:idextd:64 -Out artifacts\snapshots\handle-T.json
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Out,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll'),
    [string]$Enumerator,
    [int]$Workers = 8,
    [switch]$Files
)
$ErrorActionPreference = 'Stop'

$arguments = @($Path, '--json', '--top=100000000', '--workers', "$Workers")
if ($Files) { $arguments += '--files' }
if ($Enumerator) { $arguments += "--enumerator=$Enumerator" }

$errorFile = [IO.Path]::GetTempFileName()
try {
    $text = if ($Tool -like '*.dll') { & dotnet $Tool @arguments 2>$errorFile } else { & $Tool @arguments 2>$errorFile }
    $code = $LASTEXITCODE
    if ($code -notin 0, 3) { Write-Error "$Tool exited with code ${code}: $((Get-Content -Raw $errorFile).Trim())"; exit 1 }
} finally { [IO.File]::Delete($errorFile) }
$json = ($text -join "`n") | ConvertFrom-Json

function Get-Sorted($items) {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $items) { $list.Add([pscustomobject][ordered]@{ path = $item.path; size = $item.size }) }
    $list.Sort([Comparison[object]]{ param($a, $b) [string]::CompareOrdinal($a.path, $b.path) })
    , $list.ToArray()
}
$s = $json.statistics
$snapshot = [ordered]@{
    volume        = $json.volume
    size_mode     = $json.size_mode
    root          = [ordered]@{ path = $json.root.path; size = $json.root.size }
    counters      = [ordered]@{
        directories_scanned = $s.directories_scanned; directories_denied = $s.directories_denied; directories_failed = $s.directories_failed
        reparse_skipped = $s.reparse_skipped; directories = $s.directories; files = $s.files; bytes = $s.bytes
    }
    directories   = Get-Sorted $json.directories
    root_children = Get-Sorted $json.root_children
    files         = Get-Sorted $json.files
}
$folder = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
if (-not (Test-Path $folder)) { [void](New-Item -ItemType Directory $folder) }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out), ($snapshot | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
"snapshot: $Out  ($($json.statistics.directories) directories, $($json.statistics.files) files, $($json.root.size) bytes)"
exit 0
