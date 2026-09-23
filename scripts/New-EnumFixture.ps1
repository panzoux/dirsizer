<#
.SYNOPSIS
  Creates a deterministic synthetic tree for comparing directory enumerators.

.DESCRIPTION
  -Kind Many   many small directories (default 20,000 directories with 3 files each): the cost per directory dominates.
  -Kind Large  few large directories (default 10 directories with 20,000 files each): the cost per entry dominates.
  Names and sizes depend only on the position (directory d00000, file f00000.dat, size (index mod 7) * 100 bytes), so the same
  parameters always give the same tree. -Root must not exist yet: the script never writes into an existing folder. Remove the
  tree afterwards with [IO.Directory]::Delete(<root>, $true).

.EXAMPLE
  .\scripts\New-EnumFixture.ps1 -Root $env:TEMP\enum-many -Kind Many
  .\scripts\New-EnumFixture.ps1 -Root $env:TEMP\enum-large -Kind Large
#>
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][ValidateSet('Many', 'Large')][string]$Kind,
    [int]$Directories = $(if ($Kind -eq 'Many') { 20000 } else { 10 }),
    [int]$FilesPerDirectory = $(if ($Kind -eq 'Many') { 3 } else { 20000 })
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
if (Test-Path $Root) { throw "$Root already exists; choose a folder that does not exist." }

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
[void][IO.Directory]::CreateDirectory($Root)
$bytes = 0L
for ($d = 0; $d -lt $Directories; $d++) {
    $directory = [IO.Path]::Combine($Root, ('d{0:00000}' -f $d))
    [void][IO.Directory]::CreateDirectory($directory)
    for ($f = 0; $f -lt $FilesPerDirectory; $f++) {
        $size = ($f % 7) * 100
        $stream = [IO.File]::Create([IO.Path]::Combine($directory, ('f{0:00000}.dat' -f $f)))
        if ($size -gt 0) { $stream.SetLength($size) }
        $stream.Dispose()
        $bytes += $size
    }
}
"{0}: {1:N0} directories, {2:N0} files, {3:N0} bytes, created in {4:N0} s" -f $Root, ($Directories + 1), ($Directories * $FilesPerDirectory), $bytes, $stopwatch.Elapsed.TotalSeconds
