<#
.SYNOPSIS
  Creates a deterministic NTFS fixture for FSCTL-vs-Bulk record comparison.

.DESCRIPTION
  Writes <Volume>\ab-fixture with files chosen to exercise the record parser: resident and non-resident data,
  nested directories, Unicode/surrogate-pair/long names, hard links (including a file with enough links to overflow
  its base record into extension records), alternate data streams, a sparse file, a compressed file, a fragmented
  file, junction/symlink reparse points, and deleted files/directories.

  It only writes inside <Volume>\ab-fixture and refuses volumes that are not NTFS or not labelled NTFSTEST
  (use -AllowAnyLabel to override). The script is ASCII-only; Unicode names are built from code points.
#>
param(
    [string]$Volume = 'T:',
    [switch]$Recreate,
    [switch]$AllowAnyLabel
)
$ErrorActionPreference = 'Stop'

$letter = $Volume.TrimEnd(':', '\')
$vol = Get-Volume -DriveLetter $letter
if ($vol.FileSystem -ne 'NTFS') { throw "$Volume is not NTFS." }
if (-not $AllowAnyLabel -and $vol.FileSystemLabel -ne 'NTFSTEST') { throw "$Volume is labelled '$($vol.FileSystemLabel)', not NTFSTEST. Pass -AllowAnyLabel to override." }

$root = "${letter}:\ab-fixture"
if (Test-Path $root) {
    if (-not $Recreate) { throw "$root already exists. Pass -Recreate to delete and rebuild it." }
    Remove-Item $root -Recurse -Force
}
New-Item -ItemType Directory $root | Out-Null

function New-Data([string]$Path, [int]$Bytes) {
    [IO.File]::WriteAllBytes($Path, [byte[]]::new($Bytes))
}

# Resident and non-resident sizes, including empty.
New-Item -ItemType Directory "$root\sizes" | Out-Null
foreach ($size in 0, 1, 700, 5000, 100000, 1048576) { New-Data "$root\sizes\size-$size.bin" $size }

# Nested directories.
New-Item -ItemType Directory "$root\deep\a\b\c\d\e" -Force | Out-Null
New-Data "$root\deep\a\top.bin" 10
New-Data "$root\deep\a\b\c\d\e\bottom.bin" 12345

# Unicode, surrogate-pair, and maximum-length names.
New-Item -ItemType Directory "$root\names" | Out-Null
$japanese = -join ([char[]](0x65E5, 0x672C, 0x8A9E))
$emoji = [char]::ConvertFromUtf32(0x1F600)
$cyrillic = -join ([char[]](0x0444, 0x0430, 0x0439, 0x043B))
New-Data "$root\names\$japanese.txt" 111
New-Data "$root\names\emoji-$emoji.txt" 222
New-Data "$root\names\$cyrillic.txt" 333
New-Data "$root\names\$('L' * 251).txt" 444

# Hard links across directories.
New-Item -ItemType Directory "$root\links\d1", "$root\links\d2" -Force | Out-Null
New-Data "$root\links\orig.bin" 2000
New-Item -ItemType HardLink -Path "$root\links\d1\link1.bin" -Target "$root\links\orig.bin" | Out-Null
New-Item -ItemType HardLink -Path "$root\links\d2\link2.bin" -Target "$root\links\orig.bin" | Out-Null

# One file with many long-named hard links: the $FILE_NAME attributes overflow the 1 KiB base record.
New-Item -ItemType Directory "$root\manylinks" | Out-Null
New-Data "$root\manylinks\base.bin" 3000
foreach ($i in 1..150) {
    New-Item -ItemType HardLink -Path ("$root\manylinks\{0}-{1:D4}.bin" -f ('m' * 60), $i) -Target "$root\manylinks\base.bin" | Out-Null
}

# Alternate data streams: only the unnamed stream counts toward logical size.
New-Item -ItemType Directory "$root\ads" | Out-Null
New-Data "$root\ads\host.bin" 300
Set-Content -Path "$root\ads\host.bin" -Stream 'extra' -Value ('a' * 5000)
Set-Content -Path "$root\ads\host.bin" -Stream 'tiny' -Value 'b'

# Sparse file: 8 MiB logical size, mostly unallocated.
New-Item -ItemType Directory "$root\sparse" | Out-Null
fsutil file createnew "$root\sparse\sparse.bin" 8388608 | Out-Null
fsutil sparse setflag "$root\sparse\sparse.bin" | Out-Null
fsutil file setzerodata offset=0 length=8388608 "$root\sparse\sparse.bin" | Out-Null

# Compressed file: logical size is unchanged by compression.
New-Item -ItemType Directory "$root\compressed" | Out-Null
[IO.File]::WriteAllBytes("$root\compressed\zeros.bin", [byte[]]::new(300000))
compact /c "$root\compressed\zeros.bin" | Out-Null

# Fragmented files: two files appended alternately in small flushed chunks.
New-Item -ItemType Directory "$root\fragmented" | Out-Null
$chunk = [byte[]]::new(4096)
$a = [IO.File]::Create("$root\fragmented\a.bin")
$b = [IO.File]::Create("$root\fragmented\b.bin")
try {
    foreach ($i in 1..1500) { $a.Write($chunk, 0, $chunk.Length); $a.Flush($true); $b.Write($chunk, 0, $chunk.Length); $b.Flush($true) }
} finally { $a.Dispose(); $b.Dispose() }

# Reparse points are recorded but never followed.
New-Item -ItemType Directory "$root\reparse" | Out-Null
New-Item -ItemType Junction -Path "$root\reparse\junction" -Target "$root\sizes" | Out-Null
New-Item -ItemType SymbolicLink -Path "$root\reparse\symlink.bin" -Target "$root\sizes\size-5000.bin" | Out-Null

# Deleted files and directories leave freed (not-in-use) MFT slots behind.
New-Item -ItemType Directory "$root\gone" | Out-Null
foreach ($i in 1..50) { New-Data "$root\gone\f$i.bin" ($i * 50) }
foreach ($i in 1..10) { New-Item -ItemType Directory "$root\gone\d$i" | Out-Null; New-Data "$root\gone\d$i\inner.bin" 5 }
Remove-Item "$root\gone" -Recurse -Force

Write-VolumeCache -DriveLetter $letter
"fixture ready: $root"
