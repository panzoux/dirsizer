# dirsizer-index design

EXPERIMENTAL tool, `src\DirSizer.Index` (`dirsizer-index.exe`). Roadmap phases I2-I5 in [roadmap.md](roadmap.md);
implementation plan: [2026-09-23-persistent-index.md](superpowers/plans/2026-09-23-persistent-index.md).
Not part of the release zip.

## What is stored (I2)

One file per NTFS volume, `<volume serial number as 16 hex digits>.dsix`, by default in
`%LOCALAPPDATA%\dirsizer\index` (`--index-dir=` overrides it). It holds the merged MFT records exactly as a scan's
`RecordMerger` leaves them: file reference and header sequence number, directory flag, logical size of the unnamed
`$DATA`, and every `$FILE_NAME` (parent reference, namespace, name), in the scan's dictionary order.

Derived values (selected parent, display name, directory sizes) are not stored. After loading, the same shared
`RelationshipResolver` and `SizeAggregator` stages run again (`VolumeIndex.Recompute`), so a loaded index is aggregated
by the same code as a scan and can be compared with one record by record (`IndexVerifier`). No timestamps are stored:
nothing reported needs them.

The header holds the format version, the volume identity (serial number, MFT record size, cluster size, MFT start LCN),
the USN journal id and position the index is current up to, and the time it was written. The file ends with a SHA-256
of everything before it. The exact layout is in the comment at the top of `src\DirSizer.Index\IndexFile.cs`.

Saving writes `<file>.tmp`, flushes it to disk, then renames it over the old file. A full scan whose MFT layout changed
while it was read (exit code 3, as with `dirsizer-mft`) is never saved.

## When a saved index is not used

The file is ignored and the whole `$MFT` is scanned again when it does not exist, or when its checksum, magic or
version is wrong. The reason is printed on stderr and in JSON `index.rebuild_reason`.

## Privacy

The index lists every file and directory name on the volume, including names in directories that the user could
not list without elevation. In the default location only the user, SYSTEM and Administrators can read it.
`--index-dir` puts it somewhere else, with that directory's permissions.

## Measurements

| Volume | Records | Index file | Full scan | Save | Load | Recompute |
| --- | --- | --- | --- | --- | --- | --- |
| C: (I2, warm cache, 3 runs, medians) | 923,854 | 111.6 MiB | 5,843 ms | 2,051 ms | 1,703 ms | 374 ms |

Loading the file and recomputing parents, names and sizes took about 2.1 s, against 5.8 s for the full scan: a reload
is about 2.8x cheaper than a warm scan on this machine (a cold scan is much slower, see roadmap P3). `--verify` itself
(the record-by-record comparison) took a median of 240 ms. JIT Release build, one machine, `C:` live, index written to
`%TEMP%`.
