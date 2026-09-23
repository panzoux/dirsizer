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

The file is ignored and the whole `$MFT` is scanned again when: it does not exist; its checksum, magic or version is
wrong; the volume identity differs; the volume has no active USN journal, or the index was written without one; the
journal was recreated (different id); the saved position is no longer in the journal (it wrapped) or is ahead of it;
an attribute list or extension record could not be read during the update; or `--rebuild` was given. The reason is
printed on stderr and in JSON `index.rebuild_reason`.

## Incremental update (I3)

1. The journal state (id, first USN, next USN) is read before anything else.
2. The saved index is used only if the volume identity is the same, the volume has an active journal, the index was
   written with one, the journal id is the same (not recreated), and the saved position is still in the journal (not
   wrapped: `>= FirstUsn`) and not ahead of it (`IndexValidity`). Otherwise a full scan runs, and the reason is
   reported.
3. Every USN record from the saved position to the end of the journal is read (`FSCTL_READ_USN_JOURNAL`, V2 records).
   Only the file reference is used. The reason flags are never used to infer the new state.
4. Each named record is read again with `FSCTL_GET_NTFS_FILE_RECORD`. So are records 0-23 and the `$Extend` tree: NTFS
   metadata changes without journal entries, for example when `$MFT` grows. A record that is no longer in use, is not
   parseable, or is now an extension record is removed. Otherwise its extension records are found through its
   `$ATTRIBUTE_LIST` (resident, or non-resident and read through its run list), read, and merged in descending record
   order as a scan would, and the result replaces the entry. If an attribute list or extension record cannot be read,
   the run falls back to a full scan (`IndexUpdater`).
5. Parents, names and directory sizes are computed again for the whole index (`Recompute`). Then the new journal
   position is stored and the index is saved.

Replaying a change twice is harmless, because step 4 reads the current state. That is why the position stored after
a full scan is the one read before the scan started, and why changes made during a run are simply seen again next
time.

Not detected: changes made while the volume was mounted by a system that does not write the USN journal (for example
another operating system). Use `--rebuild` after such a mount.

End-to-end test: `scripts\Test-IndexIncremental.ps1` (T:). Besides creating, editing, renaming, moving and deleting
files and hard links, it creates empty files until `$MFT` grows, because only that exercises the metadata re-read of
step 4 (NTFS writes no journal entry for `$MFT` itself). It was shown to fail when the metadata re-read or the
extension-record reads were disabled. Timings: `scripts\Measure-Index.ps1`.

## Privacy

The index lists every file and directory name on the volume, including names in directories that the user could
not list without elevation. In the default location only the user, SYSTEM and Administrators can read it.
`--index-dir` puts it somewhere else, with that directory's permissions.

## Measurements

| Volume | Records | Index file | Full scan | Save | Load | Recompute |
| --- | --- | --- | --- | --- | --- | --- |
| C: (I2, warm cache, 3 runs, medians) | 923,854 | 111.6 MiB | 5,843 ms | 2,051 ms | 1,703 ms | 374 ms |
| C: (I3, warm cache, 3 + 3 runs, medians) | 924,034 | 111.6 MiB | 5,916 ms | 1,743 ms | 1,473 ms | 483 ms |

Loading the file and recomputing parents, names and sizes took about 2.1 s, against 5.8 s for the full scan: a reload
is about 2.8x cheaper than a warm scan on this machine (a cold scan is much slower, see roadmap P3). `--verify` itself
(the record-by-record comparison) took a median of 240 ms. JIT Release build, one machine, `C:` live, index written to
`%TEMP%`.

I3 incremental run on `C:`: 4,529 ms total (median; 10 journal entries, 40 records read again), 55 % of a full run
(8,219 ms, which includes saving and the query). The incremental run is dominated by saving the whole file again
(1,914 ms) and loading it (1,473 ms); reading the journal took 1.5 ms, re-reading the records 278 ms, and `Recompute`
483 ms. This misses the plan's 50 % gate for starting I4. JIT Release build, one machine, `C:` live and warm,
`scripts\Measure-Index.ps1 -Runs 3`.
