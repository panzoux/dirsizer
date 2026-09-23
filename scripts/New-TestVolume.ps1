<#
.SYNOPSIS
  Creates (or removes) a disposable NTFS test volume backed by a VHDX file.

.DESCRIPTION
  For correctness and cold-cache runs on a volume whose geometry differs from T:. Creates an expandable VHDX with
  diskpart, attaches it with Mount-DiskImage, and formats one GPT partition as NTFS with the given cluster size.
  With -LargeFrs, the MFT records are 4 KiB instead of 1 KiB (Format-Volume -UseLargeFRS). The volume is labelled
  NTFSTEST, so the other test scripts accept it. -Remove detaches the VHDX and deletes the file.
  Needs an elevated shell. Windows may briefly show a "format disk" prompt between partitioning and formatting;
  ignore it (the script formats the volume itself).

.EXAMPLE
  .\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\u.vhdx -Letter U -AllocationUnitSize 65536 -LargeFrs
  .\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\u.vhdx -Remove
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Letter = 'U',
    [int]$SizeMB = 2048,
    [int]$AllocationUnitSize = 4096,
    [switch]$LargeFrs,
    [string]$Label = 'NTFSTEST',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$Path = [IO.Path]::GetFullPath($Path)
$Letter = $Letter.TrimEnd(':', '\')

if ($Remove) {
    if (Test-Path $Path) {
        Dismount-DiskImage -ImagePath $Path -ErrorAction SilentlyContinue | Out-Null
        Remove-Item $Path
    }
    "removed $Path"
    return
}
if (Test-Path $Path) { throw "$Path already exists; run with -Remove first." }
if (Get-Volume -DriveLetter $Letter -ErrorAction SilentlyContinue) { throw "Drive letter $Letter is already in use." }
$null = New-Item -ItemType Directory -Force (Split-Path $Path)

$diskpartScript = [IO.Path]::GetTempFileName()
try {
    Set-Content -Path $diskpartScript -Encoding ascii -Value "create vdisk file=`"$Path`" maximum=$SizeMB type=expandable"
    $diskpartOutput = diskpart /s $diskpartScript
    if ($LASTEXITCODE -ne 0) { throw "diskpart failed: $($diskpartOutput -join ' ')" }
} finally { Remove-Item $diskpartScript -ErrorAction SilentlyContinue }

$disk = Mount-DiskImage -ImagePath $Path -PassThru | Get-Disk
Initialize-Disk -Number $disk.Number -PartitionStyle GPT
$partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter $Letter
$null = Format-Volume -Partition $partition -FileSystem NTFS -AllocationUnitSize $AllocationUnitSize -UseLargeFRS:$LargeFrs -NewFileSystemLabel $Label -Confirm:$false -Force
Get-Volume -DriveLetter $Letter | Format-List DriveLetter, FileSystemLabel, FileSystem, Size, AllocationUnitSize
