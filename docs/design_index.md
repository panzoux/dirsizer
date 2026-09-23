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

## Delta file (after I3)

Rewriting the whole base file after every incremental run cost about 1.9 s on `C:`, for a few dozen changed records.
So an incremental run writes only `<serial>.dsix.delta` next to the base file. It holds the base file's SHA-256 trailer,
the new journal id, position and write time, and the current state of every entry changed since the base file was
written: the record, or the record number if the entry was removed. `VolumeIndex.Dirty` tracks those entries;
`IndexUpdater` adds every entry it replaces or removes. The delta is cumulative, so each run replaces it (again through
`.tmp` and a rename) instead of appending to it. It has its own SHA-256 trailer.

- Loading reads the base file, then applies the delta: removals first, then records in ascending order.
- A delta whose base hash is not the base file's is ignored. It is left over from a crash between a full save's rename
  and its deletion of the delta, and the base file is newer than it.
- A damaged delta (checksum, magic, version, duplicate or trailing data) makes the whole index unusable, because the
  base file alone is older than the journal position the delta recorded. A full scan runs.
- A full save is used after a full scan, and after an incremental run once the delta would hold more than
  max(1,000, 5 % of the records) entries (`IndexFile.ShouldSaveDelta`). A full save deletes the delta, so loading never
  applies a large one.

JSON `index.save_kind` is `full`, `delta` or `none`, with `delta_records` and `delta_bytes`.

## Loading fast

Loading the base file of `C:` (112 MiB, 924,000 records) took about 1.6 s: reading the file about 0.1-0.25 s, SHA-256
about 0.6 s (this CPU, an i5-7300U, has no SHA instructions), and parsing about 0.9 s, of which about 0.5 s were
garbage collections promoting the million objects the parse creates (all of them survive). Changes:

- **Server GC** (`ServerGarbageCollection` in `DirSizer.Index.csproj`, this tool only): GC pauses during the load fell
  from about 500 ms to about 90 ms. It also made full scans faster (scan 5.8 s to 4.7-5.0 s) and raised the peak working
  set of a full run from 468 to 598 MiB (an incremental run: 532 to 519 MiB).
- **Hashing while reading and parsing:** the file is read in 4 MiB pieces into an array that is not zeroed first; a
  second thread hashes each piece as soon as it is in memory, while the first thread, once the file is read, parses
  it. The index is returned only after the checksum matched, and a wrong checksum is reported even if parsing failed
  first. So parsing never trusts the data: every count is bounded by the bytes that could hold it before anything is
  allocated, and any error is an `InvalidDataException` (a time out of range was an `ArgumentOutOfRangeException` before,
  and a huge record count an allocation failure, both behind a valid checksum).
- The file format is unchanged.

SHA-256 is now the floor: the load ends when the last piece is hashed. A non-cryptographic checksum (it only has to
catch damage, since whoever can write the file can also write a matching SHA-256) would remove most of that, at the cost
of a format version and a package dependency; not done.

## When a saved index is not used

The file is ignored and the whole `$MFT` is scanned again when: it does not exist; its checksum, magic or version is
wrong; the delta file is damaged; the volume identity differs; the volume has no active USN journal, or the index was
written without one; the journal was recreated (different id); the saved position is no longer in the journal (it
wrapped) or is ahead of it; an attribute list or extension record could not be read during the update; or `--rebuild`
was given. The reason is printed on stderr and in JSON `index.rebuild_reason`.

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
   position is stored and the index is saved, as a delta file while few entries changed (see "Delta file").

Replaying a change twice is harmless, because step 4 reads the current state. That is why the position stored after
a full scan is the one read before the scan started, and why changes made during a run are simply seen again next
time.

Not detected: changes made while the volume was mounted by a system that does not write the USN journal (for example
another operating system). Use `--rebuild` after such a mount.

End-to-end test: `scripts\Test-IndexIncremental.ps1` (T:). Besides creating, editing, renaming, moving and deleting
files and hard links, it creates empty files until `$MFT` grows, because only that exercises the metadata re-read of
step 4 (NTFS writes no journal entry for `$MFT` itself). It checks which kind of save each run made, and it reloads the
delta right after deleting a tree that is in the base file (before new files can reuse the freed records). It was shown
to fail when the metadata re-read, the extension-record reads, the delta's removals, or the recording of replaced
entries were disabled. Timings: `scripts\Measure-Index.ps1`.

## Subtree queries (I4)

The target can be any directory on the indexed volume. Windows resolves the path itself (`GetFileInformationByHandle`
gives the volume serial and the file reference), so junctions and mount points are followed as Windows follows them.
The result must be on the indexed volume, and its reference, sequence number included, must be in the index. The
answer is a walk down the selected parents from that record (`SubtreeQuery`). The whole volume uses the scan's own
`ResultSelector.Collect`, so it equals `dirsizer-mft`. One index per volume serves every query on that volume.
Checked against `dirsizer-fs` (an independent directory walk) on a folder without hard links in
`Test-IndexIncremental.ps1`.

## Privacy

The index lists every file and directory name on the volume, including names in directories that the user could
not list without elevation. In the default location only the user, SYSTEM and Administrators can read it.
`--index-dir` puts it somewhere else, with that directory's permissions.

## Measurements

| Volume | Records | Index file | Full scan | Save | Load | Recompute |
| --- | --- | --- | --- | --- | --- | --- |
| C: (I2, warm cache, 3 runs, medians) | 923,854 | 111.6 MiB | 5,843 ms | 2,051 ms | 1,703 ms | 374 ms |
| C: (I3, warm cache, 3 + 3 runs, medians) | 924,034 | 111.6 MiB | 5,916 ms | 1,743 ms | 1,473 ms | 483 ms |
| C: (I3 + delta file, warm cache, 3 + 3 runs, medians) | 924,103 | 111.6 MiB | 5,795 ms | 2,155 ms (full) / 7 ms (delta) | 1,508 ms | 480 ms |
| C: (fast load: server GC, hashing while reading, 3 + 3 runs, medians) | 924,240 | 111.6 MiB | 5,044 ms | 2,299 ms (full) / 7 ms (delta) | 752 ms | 500 ms |

Loading the file and recomputing parents, names and sizes took about 2.1 s, against 5.8 s for the full scan: a reload
is about 2.8x cheaper than a warm scan on this machine (a cold scan is much slower, see roadmap P3). `--verify` itself
(the record-by-record comparison) took a median of 240 ms. JIT Release build, one machine, `C:` live, index written to
`%TEMP%`.

I3 incremental run on `C:`: 4,529 ms total (median; 10 journal entries, 40 records read again), 55 % of a full run
(8,219 ms, which includes saving and the query). The incremental run is dominated by saving the whole file again
(1,914 ms) and loading it (1,473 ms); reading the journal took 1.5 ms, re-reading the records 278 ms, and `Recompute`
483 ms. This misses the plan's 50 % gate for starting I4. JIT Release build, one machine, `C:` live and warm,
`scripts\Measure-Index.ps1 -Runs 3`.

With the delta file: an incremental run on `C:` took 2,630 ms (median; 7 journal entries, 39 records read again, a
delta of 32 entries and 1.8 KB written in 7 ms), 31 % of a full run (8,418 ms). It is now dominated by loading the
base file (1,508 ms), then `Recompute` (480 ms), the query (365 ms) and re-reading records (275 ms). Same machine,
build and script as above.

With the fast load: an incremental run on `C:` took 1,939 ms (median), 25 % of a full run (7,809 ms). Load 752 ms,
`Recompute` 500 ms, query 368 ms, re-reading records 293 ms. Same machine, build and script as above, one day later
(the unchanged build measured load at 1.6-1.7 s that day).
