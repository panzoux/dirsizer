<#
.SYNOPSIS
  Fragments the $MFT of a disposable NTFSTEST volume, so the bulk reader's multi-extent code runs on real data.

.DESCRIPTION
  Each round has three steps. First it fills the free space with 4 MiB filler files. Then it deletes every other
  filler, which leaves 4 MiB holes all over the volume, including the MFT zone. Finally it creates -FilesPerRound
  empty files, so the $MFT has to grow into those holes. The script stops when dirsizer-inspect --volume reports at
  least -TargetExtents MFT extents, and fails after -MaxRounds rounds.
  Writes only inside <Volume>\mft-frag. Refuses volumes that are not NTFS or not labelled NTFSTEST (-AllowAnyLabel
  overrides). Needs an elevated shell and a Release build of dirsizer-inspect.
#>
param(
    [string]$Volume = 'V:',
    [int]$TargetExtents = 16,
    [int]$MaxRounds = 8,
    [int]$FilesPerRound = 50000,
    [string]$InspectDll = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll'),
    [switch]$AllowAnyLabel
)
$ErrorActionPreference = 'Stop'
$Volume = $Volume.TrimEnd('\')
$vol = Get-Volume -DriveLetter $Volume.TrimEnd(':')
if ($vol.FileSystem -ne 'NTFS') { throw "$Volume is not NTFS." }
if (-not $AllowAnyLabel -and $vol.FileSystemLabel -ne 'NTFSTEST') { throw "$Volume is labelled '$($vol.FileSystemLabel)', not NTFSTEST. Pass -AllowAnyLabel to override." }

function Get-MftExtents {
    $text = (& dotnet $InspectDll $Volume --volume) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "dirsizer-inspect failed: $text" }
    $match = [regex]::Match($text, 'MFT extents\s*:\s*(\d+)')
    if (-not $match.Success) { throw "no 'MFT extents' line in: $text" }
    [int]$match.Groups[1].Value
}

$root = Join-Path "$Volume\" 'mft-frag'
$null = New-Item -ItemType Directory -Force $root
$extents = Get-MftExtents
"round 0: MFT extents = $extents"
for ($round = 1; $round -le $MaxRounds -and $extents -lt $TargetExtents; $round++) {
    $fillDirectory = Join-Path $root "fill$round"
    $null = New-Item -ItemType Directory -Force $fillDirectory
    $fillers = [Collections.Generic.List[string]]::new()
    for ($i = 0; ; $i++) {
        $file = Join-Path $fillDirectory ('f{0:D6}.bin' -f $i)
        $null = fsutil file createnew $file 4194304 2>&1
        if ($LASTEXITCODE -ne 0) { Remove-Item $file -ErrorAction SilentlyContinue; break }
        $fillers.Add($file)
    }
    for ($i = 0; $i -lt $fillers.Count; $i += 2) { Remove-Item $fillers[$i] }
    $filesDirectory = Join-Path $root "files$round"
    $null = New-Item -ItemType Directory -Force $filesDirectory
    for ($i = 0; $i -lt $FilesPerRound; $i++) { [IO.File]::Create((Join-Path $filesDirectory ('e{0:D6}' -f $i))).Dispose() }
    $extents = Get-MftExtents
    "round ${round}: fillers=$($fillers.Count) created=$FilesPerRound MFT extents = $extents"
}
if ($extents -lt $TargetExtents) { throw "The MFT has only $extents extents after $MaxRounds rounds (target $TargetExtents)." }
"done: MFT extents = $extents"
