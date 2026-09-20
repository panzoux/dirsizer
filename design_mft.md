# P3 Raw MFT Bulk Reader

## Goal

`DirSizer.Bulk.csproj` builds a separate experimental executable, `DirSizer.Bulk.exe`. It tests whether bulk reads of raw `$MFT` bytes can outperform the existing FSCTL reference reader.

The FSCTL executable remains the correctness baseline. P3 does not replace its parser, aggregation, output semantics, or default behavior.

## Current implementation

The P3 probe currently implements:

- `FSCTL_GET_NTFS_VOLUME_DATA` geometry discovery
- `$MFT` handle acquisition with `SeBackupPrivilege`
- `FSCTL_GET_RETRIEVAL_POINTERS` extent discovery
- VCN-to-LCN extent validation and logical mapping
- 8 MiB reusable raw-read buffer
- extent-boundary-aware reads from the raw volume handle
- MFT slot iteration without filesystem traversal
- unused, deleted, in-use, malformed, and USA-failure counters
- multisector Update Sequence Array validation and in-place fixup
- raw-read timing and MB/s metrics
- record-aligned MFT tail reporting
- descending MFT traversal (see "Record order")
- the shared `DirSizer.Core` pipeline: `RecordParser` -> `RecordMerger` -> `RelationshipResolver` -> `SizeAggregator` -> `ResultSelector` / `RecordPaths`
- the same text listing as the FSCTL reader, plus `--benchmark` phase timings

`DirSizer.Bulk.exe` runs the complete folder-size pipeline. Results go to stdout in the reference reader's format; reader diagnostics, `--benchmark`, and `--diagnostics` go to stderr. `--root-children` additionally lists the root's direct children, which the FSCTL reader reports only in `--json`.

## Record normalization

Raw bytes are never sent directly to the existing parser. Each record follows:

```text
raw volume bytes
    -> unused-slot check (all-zero header)
    -> FILE signature/header checks
    -> in-use flag check (header flags bit 0)
    -> USA validation and fixup
    -> canonical FILE record
```

`BulkScan.Classify` performs these steps and returns one of `Unused`, `BadSignature`, `Deleted`, `FixupFailed`, or `InUse`. Only `InUse` records reach the shared parser.

A freed MFT slot keeps its `FILE` signature and stale attributes, with header flags bit 0 (`IN_USE`) cleared. These are deleted files (`flags=0x0000`) and deleted directories (`flags=0x0002`). `FSCTL_GET_NTFS_FILE_RECORD` never returns them, so the FSCTL reader never sees them; the raw reader does and must classify them itself. They are not malformed. The in-use check runs before USA fixup because the flags field lies inside the first sector, and a freed slot's update sequence is not validated or modified.

### Slot terminology

Every MFT slot is exactly one of these. The definitions are what `BulkScan.Classify` actually checks; they do not assume anything else about the slot.

| Term | What is checked | Counter |
| --- | --- | --- |
| unused | first byte is `0` and the header sequence number is `0` (no `FILE` signature) | `unused_slots` |
| deleted | `FILE` signature present and header flags bit 0 (`IN_USE`) clear; the stale contents of a freed record. Flags bit 1 (`0x0002`) distinguishes a deleted directory | `deleted_slots` (`deleted_directories` counts the directory subset) |
| bad signature | neither unused nor a `FILE` signature | `signature_malformed` |
| USA failure | in use, but the update sequence array does not validate | `fixup_failures` |
| in use | `FILE` signature, `IN_USE` set, USA validated and fixed up | `in_use_slots` |

`unused` and `deleted` are both "not in use" and neither is returned by `FSCTL_GET_NTFS_FILE_RECORD`; they are kept apart because only `deleted` slots carry a `FILE` signature and stale record contents. In-use state (`0x0001`) and directory state (`0x0002`) are independent header flag bits.

Every slot must land in exactly one bucket, checked at runtime:

```text
mft_slots = unused_slots + signature_malformed + deleted_slots + fixup_failures + in_use_slots
in_use_slots = parse_successful + malformed
```

`malformed` therefore counts only in-use records the shared parser rejected. `--diagnose` prints a per-reason breakdown, the header-flags histogram of those rejects, and up to 10 detailed samples per reason.

The MFT record number comes from logical position. The sequence comes from the record header. The FSCTL output sequence bits are not used for raw-reader identity.

USA failures are counted separately. A failed volume read, invalid extent map, failed MFT handle, or unstable geometry is fatal; a malformed individual record is recoverable.

## Extents and reads

`MftStartLcn` is not treated as the complete MFT layout. The extent map is obtained from the `$MFT` unnamed data stream using `FSCTL_GET_RETRIEVAL_POINTERS`, following `NextVcn` until the captured MFT valid-data length is covered.

Raw bytes are read through the volume handle using physical byte offsets. A logical block is split at extent boundaries and never assumes that one extent continues into the next. The initial buffer target is 8 MiB and is not a user-facing option.

`FSCTL_GET_RETRIEVAL_POINTERS` returns `ERROR_MORE_DATA` when the extent map does not fit the output buffer. That is not a failure: the response holds the extents that fit, and the caller continues at the last `NextVcn`. `Native.ReadMftExtents` does this (64 KiB buffer, so a typical MFT needs one call), fails on any other error, and fails on a response that makes no progress instead of looping. It is tested two ways: volume-independent tests with a fake IOCTL (continuation at each `NextVcn`, single-call maps, no-progress, other errors, truncated responses), and on real volumes by `DirSizer.Compare`, which re-reads the map with 32, 48, 64, 100, and 4096-byte buffers. On C: (5 extents) a 32-byte buffer took 5 calls, 4 of them `ERROR_MORE_DATA`, and produced the same 5 extents as the normal read.

The current probe uses ordinary synchronous cached reads. `NO_BUFFERING`, overlapped I/O, double buffering, and alternative block sizes are benchmark experiments, not assumptions.

## Live-volume policy

A live raw MFT scan is best effort, not an atomic snapshot. The bulk reader captures the MFT layout before the scan (volume serial number, geometry, `MftStartLcn`, `MftValidDataLength`, and the extent map), scans exactly the captured record slots, and captures the layout again right after the scan (the `stability` phase: 0.1 ms on AOT, 1.5 ms on JIT, on C:). `MftLayout.Compare` reports:

| Change | Meaning |
| --- | --- |
| `Volume` | different serial number or geometry: not the volume that was opened |
| `Grew` / `Shrank` | `MftValidDataLength` increased or decreased; records added after the captured range are not in the result |
| `ExtentsMoved` | the physical cluster mapping of the captured range changed (for example a defragmentation), so bytes may have been read through a stale map |

Extents are compared as physical mappings, clipped to the captured range, with contiguous extents merged, so growth inside the last extent is reported as growth, not as relocation, and the same clusters split into different extents are not a change.

On a change the scan is retried once. If the layout is unchanged in the final attempt the result is stable (exit code 0, `scan_stability=stable attempts=N` on stderr). If it changed again, the results are still printed but stderr says `scan_stability=UNSTABLE ... the results are not a consistent snapshot` and the exit code is 3. The time of a discarded first attempt is reported as `discarded_attempt_ms` and is not folded into any phase, so `phase_sum == total` still holds for the final attempt.

What this does and does not detect: it detects changes to the MFT's layout and length. It does not detect files being created, deleted, resized, or renamed inside existing records while the scan runs; that is inherent to reading a live volume, and the same is true of the FSCTL reader. Growth only shows up when NTFS runs out of free record slots and extends the MFT (observed on T: in steps of 1 MiB, that is 1,024 records).

Tested against a real volume by `scripts\Test-BulkInstability.ps1`: the test-only options `--test-delay-after-scan-ms=N` and `--no-retry` hold the window open while files are created on the test volume until the MFT grows. Verified: no change gives exit 0 and one attempt; growth with `--no-retry` gives exit 3, `UNSTABLE`, `Grew`, with the listing still printed; growth during the first attempt gives a second, stable attempt and exit 0. `ExtentsMoved` and `Volume` cannot be provoked on a small test volume and are covered by the layout-comparison unit tests only (including that clipping and merging are needed: mutating either makes a test fail).

Side effect of that test: the MFT never shrinks, so each run makes the test volume's MFT larger (T: went from 512 to 2,560 slots over the test runs). `$MFT` is part of the folder-size listing, so the root size and `$MFT` rows changed by exactly the growth (+2,097,152 bytes) and any earlier listing snapshot of that volume became stale; the reference and bulk readers still agree with each other and the record-level comparison is still EQUAL.
## Acceptance criteria

On a quiescent T: volume, the bulk reader must eventually share the canonical parser/model with the FSCTL reader and match:

- root logical size
- logical record count
- directory/file identities and sizes
- top-N output
- unresolved count
- A/B parent sequence consistency

On C:, exact equality is not required because the volume is live. The run must report raw read errors, USA failures, extent validity, and instability status separately.

Status: every item above is EQUAL on the quiescent T: fixture (see "Bulk output vs the FSCTL reference"). The parent-sequence check is covered by the exact / fallback / mismatch relationship counters, all equal.

## Access result

Opening `T:\$MFT` with `GENERIC_READ` returned Win32 error 5. Opening
`T:\$MFT::$DATA` with `FILE_READ_ATTRIBUTES` succeeded after enabling
`SeBackupPrivilege`. The same pattern succeeds on C:. No speculative
`SeManageVolumePrivilege` requirement was added. The `$MFT` stream handle is
used only for `FSCTL_GET_RETRIEVAL_POINTERS`; the volume handle performs raw
`ReadFile` operations.

Observed raw-reader probes:

| Volume | MFT bytes | Extents | Slots | Active (probe before the fix: in-use plus deleted) | USA failures | Reads | Raw MB/s | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T: | 262,144 | 1 | 256 | 40 | 0 | 1 | 126.3 | 4.7 ms |
| C: | 982,253,568 | 5 | 959,232 | 959,106 | 0 | 121 | 391.8 | 3,279.2 ms |

After shared parser integration, the first C: probe reported 951,416 parse-successful
records, 32,696 extension records, 918,720 logical records, 7,690 "malformed active"
records, and 0 USA failures. Those 7,690 were not malformed: they were freed MFT
slots (see "Record normalization"), which the probe had counted as active because it
checked only the `FILE` signature and USA. Diagnosis:

- Reject breakdown on C: 100% `NotInUse` (flags `0x0000` deleted files, `0x0002`
  deleted directories); zero attribute-level, signature, or USA rejects.
- On quiescent T: after creating and deleting 200 files and 20 directories (240
  freed records including files inside the directories), the probe reported exactly
  240 `NotInUse` rejects (220 with flags `0x0000`, 20 with `0x0002`). Querying
  `FSCTL_GET_NTFS_FILE_RECORD` for all 512 slots returned 241 in-use records
  (equal to the bulk parse-successful count); the other 271 slots (240 deleted plus
  31 unused) were not returned. FSCTL-returned records never had bit 0 clear.
- On live C:, the reject count fell 7,690 -> 7,371 -> 7,119 across successive runs
  while the old "active" count (in-use plus deleted) stayed 959,106 and `parse_successful` rose by the same
  amount: NTFS reuses free slots, so a slot sampled as deleted can be in use again
  minutes later. Comparing individual sampled slots between a bulk run and a later
  FSCTL query is therefore not a valid test on a live volume.

After separating deleted slots, C: reports 952,166 in-use records (all parse
successfully), 126 unused, 6,940 deleted (350 directories), and 0 malformed. A bulk
run immediately before an FSCTL run matched it exactly on logical records (919,471)
and extension records (32,696); the next bulk run drifted by 34 records, which is
live-volume churn. These are still not folder-size results because merge,
relationship, aggregation, and A/B output comparison are not wired into the bulk
executable yet.

## Shared pipeline and how it was extracted

Both readers feed the same reader-independent stages in `CorePipeline.cs`:

```text
reader (FSCTL | raw bulk)  ->  RecordParser  ->  RecordMerger  ->  RelationshipResolver
                           ->  SizeAggregator (file sizes, then directories)  ->  ResultSelector  ->  RecordPaths
```

The stages were moved out of the reference scanner one at a time with no behaviour change and no clean-up (no type changes, no data-structure changes, no renames). After each move the reference executable's complete output was compared with a frozen baseline (`scripts\Get-ReferenceSnapshot.ps1`: `--json --dirs --files --diagnostics` with an unbounded `--top` on the quiescent test volume, timing and memory fields removed; 284 paths, every size and ordering, all counters, unresolved records). Every step produced a byte-identical snapshot. The check was proven able to fail: a deliberate mutation in each stage (merge, resolver tie-break, aggregation, root-children selection, path building) changed the snapshot by 26 to 1,105 lines and restoring the code returned it to identical. `--self-test` also has small volume-independent tests for merge order, Win32-over-DOS name preference, aggregation roll-up, and top-N ordering.

The benchmark phases keep their original boundaries: "relationships" is `RelationshipResolver.Resolve` plus `AddFileSizesToParents`; "aggregation" is `AggregateDirectories`; "finalize" is `ResultSelector.Collect`. Top-N selection runs after the total is taken and is in no phase, in both readers.

## Record order

The reference reader visits records in descending record number because `FSCTL_GET_NTFS_FILE_RECORD` returns the next lower in-use record. `RecordMerger` appends names in arrival order, name selection takes the first of equally scored names, and results are built from dictionary insertion order, so the arrival order is part of the output. The bulk reader therefore reads its planned blocks from the end of the MFT to the start and walks the records of each block from the end (`BulkScan.PlanBlocks` plans the blocks; extent boundaries and record alignment are unit-tested).

Checked, not assumed: with a temporary ascending traversal the bulk output differed from the reference in four checks on the fixture volume: the order of equally sized directories (`$TxfLog` and `$RmMetadata`, both 4,259,940 bytes), of files, of the root's children, and of the unresolved-records list. Sizes were unchanged; the visible difference was tie ordering. Restoring descending order returned to fully equal. Hard-link name selection is order-dependent by construction, but the fixture did not demonstrate it, so that is unverified.

Whether reading blocks in reverse costs anything compared with a forward scan was not measured separately. Reading forward would require making the merge order canonical, which changes the shared stages and is a separate, deliberate change.

## Bulk output vs the FSCTL reference

`scripts\Compare-BulkToSnapshot.ps1` runs the bulk executable on the same quiescent volume and checks it against the reference snapshot: root size; every directory (path, size, order); every file with data (path, size, order); the root's children; the record, file, directory, extension, relationship (exact, fallback, unresolved) counters; the unresolved records; and the relationship mismatch samples. Bulk stdout is captured as UTF-8, because a legacy console code page turns surrogate-pair names into `?`.

Result on T: (fixture): `RESULT: EQUAL (all checks)`: root 29,384,721 bytes, 33 directories, 234 files, 16 root children, 281 logical records, 276 exact relationships, 4 unresolved records (12-15, the known zero-size NTFS metadata records).

## Benchmark: FSCTL vs bulk with identical phases

`scripts\Compare-Benchmark.ps1` alternates the two readers (one discarded warm-up pair, then five measured pairs) and reports min / median / max per phase, plus raw read throughput and operation count and the stability re-check. It checks `phase_sum == total` on every run of both readers and warns if a bulk run had to rescan. Live C: (about 953,000 in-use records, 5 extents), warm file cache, milliseconds. NativeAOT builds, which are what `PublishAot` ships, built with `dotnet publish`:

| Phase | FSCTL min / median / max | Bulk min / median / max |
| --- | ---: | ---: |
| acquire (FSCTL query, or extents + raw read + fixup) | 4,664 / 4,732 / 4,777 | 2,372 / 2,380 / 2,487 |
| - extents | | 0.7 / 0.8 / 0.9 |
| - raw read | | 2,223 / 2,228 / 2,337 |
| - raw read throughput (MB/s) / operations | | 401 / 421 / 422 MB/s, 121 reads |
| - fixup (classify + USA) | | 146 / 149 / 157 |
| stability re-check | | 0.1 / 0.1 / 0.1 |
| parser | 1,207 / 1,353 / 1,435 | 1,167 / 1,253 / 1,476 |
| merge | 649 / 690 / 895 | 516 / 746 / 851 |
| relationships | 151 / 158 / 168 | 146 / 158 / 161 |
| aggregation | 140 / 141 / 154 | 140 / 147 / 153 |
| finalize | 56 / 58 / 59 | 52 / 54 / 55 |
| other | 75 / 77 / 79 | 64 / 65 / 66 |
| **total** | **7,148 / 7,213 / 7,302** | **4,726 / 4,793 / 4,937** |
| managed allocation / peak working set | 561 MB / 429-435 MB | 569 MB / 436 MB |

Per-pair speedup (FSCTL total / bulk total), AOT: min 1.45x, median 1.52x, max 1.53x. The AOT builds were first verified to behave identically to the JIT builds (self-tests, reference output byte-identical to the baseline, bulk EQUAL to the reference on the test volume).

The same script on the JIT builds (through `dotnet`), same session (see also "Product integration" for a later run of the single product executable): FSCTL total 7,459 / 7,720 / 8,106, bulk 5,035 / 5,068 / 5,232, per-pair speedup min 1.45x, median 1.52x, max 1.59x; acquire 4,850 vs 2,397 (raw read 2,222 at 421 MB/s, fixup 166). An earlier JIT session the same day measured a median of 1.42x (7,997 vs 5,617 ms), so run-to-run variation between sessions is about 0.1x; the honest summary is 1.4x to 1.5x, not a single figure.

Reading the results:

- The bulk reader removes about 2.4 to 2.7 s, all of it in acquisition. Everything after acquisition (parser, merge, relationships, aggregation, finalize) costs the same in both readers: about 2.4 s on AOT and 2.6 to 3.4 s on JIT depending on the session. The earlier raw-read-only figure (about 3.3 s against 8.8 s) overstated the gain because it left that shared work out.
- Raw read is 2.2 s at a steady ~420 MB/s over 121 reads in every run, so bulk's acquisition time is stable; most of the run-to-run variation is in the shared CPU phases. Whether the remaining 2.2 s is limited by the device, the file cache copy, or the single synchronous read has not been separated.
- On AOT, parser (1.2 to 1.4 s) and merge (0.7 s) are the largest shared costs after raw read.
- Bulk peak working set and allocation are about 1 to 2% higher, not lower: it holds the same record dictionary plus an 8 MiB buffer.

Caveats: a warm file cache (cold-cache behaviour was not measured); a live volume that drifts slightly between runs; per-record `Stopwatch` instrumentation on both sides (three timers in bulk, two plus the query timer in FSCTL), so the fixup and parser rows include some timer overhead; one machine and one volume.
## Product integration (`--reader=bulk`)

`dirsizer.exe --reader=fsctl|bulk` (default fsctl). The bulk sources are compiled into the main executable (`BulkReader.cs`, `BulkStability.cs`, `BulkScanner.cs`, `BulkReport.cs`); the bulk reader's low-level class was renamed `BulkNative` so the reference `Native` class stays untouched. `BulkScanner.Scan` runs one scan with the layout check and one rescan; `BulkIntegration.Run` maps the outcome onto the reference `ScanResult` (acquisition time takes the FSCTL "query" slot, raw read operations the query count, `records_scanned` is the MFT slots examined, `records_skipped` is the unparseable slots), and the reference `Output` prints it, so there is one implementation of the listing and JSON. `DirSizer.Bulk.exe` remains as a test harness (`--diagnose`, `--root-children`, and the instability test options) sharing the same scanner; it is not shipped. The live-instability test can also drive the real CLI through two environment variables (`DIRSIZER_TEST_BULK_DELAY_AFTER_SCAN_MS`, `DIRSIZER_TEST_BULK_NO_RETRY`); they are read only under `#if DIRSIZER_TEST_HOOKS`, which is defined only by the separate `TestHooks` build configuration that `scripts\Test-BulkInstability.ps1 -Product` builds for itself. Debug and Release builds and publishes do not contain them.

Failure behaviour: no automatic fallback; errors exit 1 with the same `error:` line format as the FSCTL reader; an unstable scan prints its results, warns on stderr, and exits 3.

Verified through the product CLI on the quiescent test volume, in both the JIT and the NativeAOT build: `scripts\Compare-Readers.ps1` compares `--reader=fsctl --json` with `--reader=bulk --json` on 23 items (root, all directories and files with order, root children, all counters, unresolved records, relationship samples, reader field, and that the bulk scan was stable): EQUAL. It was proven able to fail: with the temporary ascending record order it reports four differing checks. The default (FSCTL) output was compared with the frozen baseline after each change: identical, and after adding the `reader` JSON property the only difference is that one property.

Failure paths executed on real volumes, both readers:

| Case | Result |
| --- | --- |
| unused drive letter (`Z:`) | `error: Cannot open ... (Win32 error 2)`, exit 1 |
| folder path, or no colon | `error: The volume must be a drive root such as C:\.`, exit 1 |
| two volumes | `error: Only one volume is supported.`, exit 1 |
| exFAT volume, RAW/empty drive | `error: ... filesystem is exFAT|unknown; only NTFS volumes are supported.`, exit 1 (the bulk reader used to say only `Win32 error 1`) |
| `T:\` with trailing backslash | works, exit 0 |
| bad option or bad `--reader` value | `error: ...`, exit 1 (both readers used to crash with a stack trace) |
| MFT grows during the scan | exit 3 with `--no-retry`, stable second attempt otherwise |

Not executed: running without elevation, a failing raw read, and a corrupt extent map on a real volume.

Product benchmark, NativeAOT `dirsizer.exe`, `--reader=fsctl` against `--reader=bulk`, live C:, 5 alternating pairs: FSCTL total 7,666 / 7,857 / 8,166 ms, bulk 5,608 / 5,703 / 5,738 ms, per-pair speedup 1.36x / 1.37x / 1.44x; acquisition 4,815 vs 2,386 ms. Both readers' shared CPU phases were slower in this session than in the earlier AOT run (parser about 1.8 s against 1.25 s), which is why the ratio is lower. Across the four sessions so far the median speedup was 1.42x, 1.52x, 1.52x, and 1.37x; the honest summary is "about 1.4x", not a single figure.

## Record-level FSCTL vs bulk comparison

`DirSizer.Compare.csproj` builds a developer tool (not part of the product) that compares the two readers record by record on a quiescent NTFS volume. It links `BulkReader.cs`, so the bulk side is the real reader, and it has its own minimal `FSCTL_GET_NTFS_FILE_RECORD` wrapper that mirrors the reference reader's error handling; the bulk executable itself contains no FSCTL record access.

```powershell
dotnet build DirSizer.Compare.csproj -c Release
dotnet bin\Compare\Release\net8.0-windows\win-x64\DirSizer.Compare.exe --self-test
dotnet bin\Compare\Release\net8.0-windows\win-x64\DirSizer.Compare.exe T:
```

It runs a bulk pass, then queries FSCTL for every slot, then a second bulk pass; if the two bulk passes differ the volume was not quiescent and the result is reported unstable. For each slot in use it compares:

- record sets (`only_in_bulk`, `only_in_fsctl`, `record_count`)
- raw record bytes: differences inside the used region (`raw_bytes_within_used`), in the slack after it (`raw_bytes_slack_only`), and record length
- the shared parser's model of each side: identity (full `FileRef`), header sequence, base reference, directory flag, logical size, and every `$FILE_NAME` (parent, name, namespace)

`--self-test` proves the detector works: an identical pair yields zero mismatches and each mutation (sequence, directory flag, base reference, data size, name character, name parent, slack byte, length, missing fixup) is reported in the expected category.

### Update sequence array entries

After fixup, the saved sector-tail entries in the update sequence array (the array bytes after the update sequence number itself) are not record content, and the parser never reads them. They can still differ between the readers, and the differences follow a clear rule that was measured rather than assumed:- Right after files were created on T:, 81 of 354 records differed only in these entries: the FSCTL side was zero and the on-disk side (bulk) held the protected value. All 74 extension records and the directories and files just created were in that set. 58 records that NTFS had not touched since mounting carried the same non-zero values on both sides.- After `fsutil volume dismount T:` and a fresh mount, 80 of the 81 differences disappeared and FSCTL showed non-zero entries in 139 records (58 + 81). One record (36, a directory) still differed; after a second dismount it matched too, and all 140 non-zero records were identical with 0 differences.- The most likely explanation, consistent with all three observations but not separately proven, is that FSCTL returns NTFS's in-memory copy: for a record NTFS created or rewrote since the mount, the in-memory saved entries are zero, and they are filled in only when the record is written to disk with fixup protection. Records read from disk keep the on-disk values.The comparison therefore treats a difference confined to those entries as information (`usa_saved_entries_differ_records`) and reports a mismatch (`usa_entries_fsctl_nonzero`) if the FSCTL side is ever non-zero where it differs. Every other byte, including the sector-tail positions where fixup writes the restored data, is compared strictly, and no difference was found at those positions: FSCTL returns records with fixup applied. `--dump-usa` lists both sides' entries per record. For a strict comparison with no masking at all, dismount the volume first (`fsutil volume dismount T:`; it remounts on next access) so every FSCTL record is read from disk.

### Fixture

`scripts\New-AbFixture.ps1` builds `<volume>\ab-fixture` on a volume labelled `NTFSTEST`: resident and non-resident files (including empty), nested directories, Unicode, surrogate-pair and 255-character names, hard links, a file with 150 long-named hard links (overflows its base record into extension records), alternate data streams, a sparse file, a compressed file, two interleaved fragmented files, a junction and a symbolic link, and deleted files and directories. The FSCTL reference reader reports 22,149,650 logical bytes for `ab-fixture`, matching the sum computed by hand (alternate streams excluded, sparse and compressed files at logical size, hard links counted once, reparse points zero).

### Results on T: (fixture plus earlier test files)

Right after fixture creation (three identical runs):

```text
mft_slots=512  bulk_in_use=354  fsctl_in_use=354  bulk_deleted=127  bulk_unused=31
compared_records=354  extension_records=74  directories=33  multi_name_records=77  names=429
usa_saved_entries_differ_records=81  fsctl_records_with_nonzero_saved_usa_entries=58   (information only)
result=EQUAL mismatches=0   volume_stable_during_run=true
```

After dismounting and remounting T: twice (FSCTL reads every record from disk, no masking needed):

```text
mft_slots=512  bulk_in_use=355  fsctl_in_use=355  bulk_deleted=126  bulk_unused=31
compared_records=355  extension_records=74  directories=33  multi_name_records=77  names=430
usa_saved_entries_differ_records=0  fsctl_records_with_nonzero_saved_usa_entries=140
result=EQUAL mismatches=0   volume_stable_during_run=true
```

This establishes that, on this volume, USA-normalized bulk records and FSCTL records are byte-identical (outside saved USA entries that only differ for records changed since the mount), and that the shared parser produces the same model from both. It does not yet compare merge, relationships, aggregation, root size, top-N, or unresolved records, because the bulk executable does not run them yet.

## Next steps

- Measure a cold file cache and another volume (the benchmark script takes any volume; a cold cache needs a way to empty the standby list or a large uncached read).
- Decide whether bulk is faster and safe enough to remain experimental or become selectable. Its failure behaviour is now defined (exit codes 0, 1, 3 above); what remains is the product decision.
- After that decision, and only then: optimise the shared parser and merge, the largest remaining costs. They are common to both readers, so verify each change with the reference snapshot (`before == after`) and the bulk comparison.
- Separate change, if wanted: make the merge order canonical so the raw reader can scan forward (changes the shared stages).