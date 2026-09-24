# DirSizer Roadmap

## Current baseline

The FSCTL reader is the initial production path. It scans active NTFS records in descending order, parses records from a shared output buffer, preserves full file references, merges extension records into their base record, resolves one consistent parent/name pair, and aggregates directory sizes iteratively.

The reported metric is logical bytes in the unnamed `$DATA` attribute. The scanner is read-only, avoids filesystem traversal, and emits table or source-generated JSON output.

## P0 - Correctness

- [x] Enumerate `FSCTL_GET_NTFS_FILE_RECORD` in descending order.
- [x] Preserve record and parent sequence numbers internally.
- [x] Merge extension records into the base file record.
- [x] Select parent and display name from the same `$FILE_NAME` entry.
- [x] Treat MFT record 5 as the aggregation root, never as its own child.
- [x] Skip malformed records while propagating unexpected volume I/O failures.
- [x] Add parser fixtures for malformed attributes, extension records, hard links, and root self-reference (`--self-test`).
- [x] Validate the scanner against a live NTFS volume and JSON output (`C:\`, `--top=1`, `--json`, 0 skipped records).

## P1 - Performance and memory

- [x] Remove per-request input and record-copy allocations.
- [x] Avoid temporary `"FILE"` strings during parsing.
- [x] Keep one record dictionary keyed by MFT record number.
- [x] Replace directory depth sorting with leaf-to-root aggregation.
- [x] Add measurements for query throughput, parser time, allocation volume, and peak memory (`--benchmark`).
- [x] Split benchmark time into query, parser, merge, relationships, aggregation, finalize, and total; report query throughput separately from overall scan throughput.
- [x] Separate returned, parse-successful, extension, skipped, and logical-record counters.
- [x] Remove LINQ from production and self-test code; hot-path relationship selection and output materialization use explicit loops.
- [x] Make phase accounting exhaustive with an explicit `other` residual and runtime `phase_sum == total` validation.
- [x] Report parent relationships as exact, fallback-zero-sequence, fallback-mismatch, and unresolved.
- [x] Validate parent sequence consistency using A/B on T: and C:; T: produced 32/32 A/B exact, and isolated C: produced 917,442 A/B exact with 4 unresolved records.
- [x] Use only the FSCTL returned record number for identity; reconstruct full `FileRef` from the returned ordinal plus the MFT record-header sequence. Validate returned ordinal <= requested ordinal.
- [x] Inspect all unresolved records with `--diagnostics`; C: produced records 12-15, all zero-size NTFS metadata records with no `$FILE_NAME` and no parent, so they do not affect capacity aggregation.
- [ ] Optional: document the observed zero upper bits in FSCTL output `FileReferenceNumber`; its sequence is intentionally excluded from relationship validation because the API does not define it as the record-header sequence.
- [x] Evaluate pooled buffers: no additional pool was added because the input/output buffers are already reused; measured allocations are dominated by retained model objects, names, dictionaries, and strings.

## P2 - Output and product surface

- [x] Use `System.Text.Json` source generation for JSON output.
- [x] Keep the initial metric to logical unnamed `$DATA` bytes.
- [x] Document and expose a clear root/direct-children summary in JSON.
- [x] Replace full sorting with a bounded top-N priority queue; only the requested N records are retained for final ranking.
- [x] Fixed a bug found while extracting the pipeline: text-mode `--files` printed `record.Size`, which is 0 for files, while `--json` was right. It now prints the logical size. `scripts\Compare-Readers.ps1` checks each reader's text listing against its own JSON (proven able to fail by reverting the fix), so text mode is no longer untested.

## P3 - Bulk MFT reader

- [x] Add a separate `DirSizer.Bulk.csproj` experimental raw-MFT reader (now the tool `dirsizer-bulk`).
- [x] Implement volume geometry and `$MFT` extent-discovery code using `FSCTL_GET_RETRIEVAL_POINTERS`.
- [x] Implement extent-boundary-aware 8 MiB raw volume reads.
- [x] Implement unused/active/malformed counters and USA validation/fixup.
- [x] Add USA self-test fixtures for valid fixup, wrong sequence, truncated array, invalid offset, and invalid size.
- [x] Resolve `$MFT` user-mode handle access: `$MFT::$DATA` with `FILE_READ_ATTRIBUTES` succeeds after enabling `SeBackupPrivilege`; `GENERIC_READ` on `$MFT` returned error 5.
- [x] Run the raw probe on T: and C:; T: read 262,144 bytes in 1 operation at 35.2 MB/s, C: read 982,253,568 bytes in 121 operations at 273.3 MB/s, with zero USA failures.
- [x] Extract the canonical parser/model into `DirSizer.Core` and reference it from both readers.
- [x] Read USA-normalized bulk records through the shared `RecordParser`; C: produced 951,416 parse-successful records, 32,696 extensions, and 918,720 logical records.
- [x] Explain the 7,690 `malformed_active` C: records: all were freed MFT slots (in-use flag clear, deleted files/directories), not malformed. `BulkScan.Classify` now separates `Deleted` from `InUse` before USA fixup and the shared parser; `--diagnose` reports per-reason rejects, flags, and samples; slot accounting is asserted at runtime. Verified on quiescent T: (241 in-use = FSCTL 241; 240 deleted) and live C: (0 malformed; logical/extension counts equal to an adjacent FSCTL run).
- [x] Handle `ERROR_MORE_DATA` from `FSCTL_GET_RETRIEVAL_POINTERS`: the partial map is valid and the next call continues at the last `NextVcn`; other errors and no-progress responses still fail. Tested with a fake IOCTL (mutating the fix makes the tests fail with the old `Win32 error 234`) and on real volumes with 32-100 byte buffers (C: 5 calls, 4 of them `ERROR_MORE_DATA`, identical 5 extents).
- [x] Live-instability detection: capture the MFT layout (serial, geometry, `MftValidDataLength`, extent map) before and after the scan, compare as clipped and merged physical mappings, rescan once on a change, report `scan_stability=stable|UNSTABLE` and exit code 0 or 3. Layout-comparison unit tests (mutation-checked) plus a real-volume test that grows the MFT during the scan (`scripts\Test-BulkInstability.ps1`): exit 3 with `Grew` and no retry, stable second attempt with retry. Detects layout changes only, not changes inside existing records.
- [x] Compare stable T: FSCTL and bulk records byte-for-byte and through the shared parser (`DirSizer.Compare`, `scripts\New-AbFixture.ps1`): 354 in-use records incl. 74 extension records, 0 mismatches, 3 stable runs. Only raw difference: the saved USA entries of 81 records changed since mount (zero on the FSCTL side); after `fsutil volume dismount` + remount all 355 records are byte-identical with no masking. The detector has its own mutation self-test.
- [x] Give each project its own `obj\` folder (`Directory.Build.props`) so alternating builds no longer clobber restores; rename bulk counters to `in_use_slots` / `deleted_slots` / `malformed`.
- [x] Extract merge, relationship resolution, size aggregation, top-N selection, and path building from the reference scanner into `DirSizer.Core` (`CorePipeline.cs`), one stage at a time with a frozen-output regression check (`scripts\Get-ReferenceSnapshot.ps1`) after each: all byte-identical to baseline, each stage's check proven able to fail by mutation. Added volume-independent pipeline self-tests.
- [x] Run that pipeline in the bulk executable (descending record order, same phase timers, same text listing) and compare it with the FSCTL reference on the T: fixture (`scripts\Compare-BulkToSnapshot.ps1`): root size, all 33 directories, 234 files, 16 root children, all counters, unresolved records, relationship samples: EQUAL. Ascending order was shown to change tie ordering, so descending is required for identical output.
- [x] First benchmark with identical phases (`scripts\Compare-Benchmark.ps1`, live C:, JIT builds, warm cache, 5 alternating pairs): FSCTL total median 7,997 ms, bulk 5,617 ms, per-pair speedup 1.38x-1.49x (median 1.42x). All the saving is in acquisition (about 4.8 s -> 2.4 s); parser, merge, relationships, aggregation, and finalize are the same in both.
- [x] Benchmark the NativeAOT builds (verified identical in behaviour first): FSCTL total median 7,213 ms, bulk 4,793 ms, per-pair speedup 1.45x-1.53x (median 1.52x). The JIT builds in the same session gave 1.45x-1.59x (median 1.52x); an earlier JIT session gave 1.42x, so the honest range is 1.4x-1.5x. Per-run `phase_sum == total` is now checked; raw read is a steady ~420 MB/s over 121 reads.
- [x] Product integration (first as `--reader=fsctl|bulk` in one executable, since split into two tools, see below; the checks in this item were made on that version and repeated after the split with `Compare-Readers.ps1`). The scan, retry, and reporting live in shared files (`BulkScanner.cs`, `BulkReport.cs`); `BulkIntegration.cs` maps the result onto the reference `ScanResult`, so listing and JSON come from the same output code. JSON gained `reader` (always) and a `bulk` object (bulk only). The default output was verified against the frozen baseline at every step (identical, then exactly one added JSON property). Through the CLI, `--reader=fsctl` and `--reader=bulk` are EQUAL on the test volume in JIT and NativeAOT builds (`scripts\Compare-Readers.ps1`, proven able to fail with the ascending-order mutation); exit codes 0 / 1 / 3 verified through the CLI, including the unstable path on a real volume (`Test-BulkInstability.ps1 -Product`). The environment-variable test hooks that make this possible are compiled only into a separate `TestHooks` build configuration; the Release build ignores them (checked: a 2.5 s requested delay is honoured by TestHooks and ignored by Release) and neither the Release DLL nor the NativeAOT executable contains the variable names (checked with a positive control).
- [x] Failure-path review: unused drive letter, non-drive-root path, missing colon, two volumes, exFAT and RAW volumes, trailing backslash, and bad options give clean `error:` lines and exit 1 in both readers. Fixed two gaps found: bad options crashed with a stack trace (pre-existing, both readers), and the bulk reader reported a non-NTFS volume as `Win32 error 1` instead of naming the file system. Not executed: running without elevation (the `runas` de-elevation did not start the child here; by code review both readers throw a `Win32Exception` with native error 5, which triggers the Administrator hint), and a failing raw read or a corrupt extent map on a real volume (covered by unit tests of the parsers only).
- [x] Benchmark a cold file cache and another volume. Another volume: U: and V: (VHDX, one machine, `Compare-Benchmark.ps1 -Runs 5`, medians). Cold file cache: `C:` after a reboot; a dismount run on a VHDX does not exclude the host's cache of the VHDX file.
  - Cold `C:` (this machine, JIT Release builds, first scan after a reboot, one reboot and one run per tool): FSCTL total 38,150 ms (query 35,639 ms), bulk 6,086 ms (raw read 3,383 ms, 360 MB/s over 156 reads), speedup 6.27x. Warm `C:` for comparison (JIT, medians above): FSCTL 7,997 ms, bulk 5,617 ms, 1.42x. A cold cache costs FSCTL about 30 s more and bulk about 0.5 s more. One run per tool, so no spread.
  - U: (2 GiB, 64 KiB clusters, 256 slots) warm: FSCTL 19.0 ms, bulk 35.9 ms, speedup 0.49x; raw read 169 MB/s. With `-ColdDismount`: FSCTL 52.3 ms, bulk 61.3 ms, speedup 0.89x; raw read 173 MB/s.
  - V: (2 GiB, 4 KiB clusters, fragmented MFT, 100,608 slots) warm: FSCTL 871 ms, bulk 564 ms, speedup 1.51x; raw read 456 MB/s. With `-ColdDismount`: FSCTL 4,267 ms, bulk 539 ms, speedup 7.92x; raw read 461 MB/s.
  - Conclusion: dismounting empties the NTFS cache (FSCTL gets 2.7x to 4.9x slower), but bulk raw-read MB/s is unchanged (within 3 %), so the host still caches the VHDX file. These are "dismount runs, host cache not excluded", not cold-cache numbers.
  - Second machine (hardware not described; shipped 0.6.0 NativeAOT `dirsizer-fsctl.exe` / `dirsizer-mft.exe`; two data volumes, T: and U:, about 104,000 MFT slots of 1 KiB each; 3 FSCTL runs, then 3 bulk runs, not alternated): FSCTL cold first run U: 15,561 ms / T: 16,729 ms, warm (mean of runs 2-3) U: 1,293 ms / T: 1,463 ms; bulk median U: 1,316 ms / T: 1,312 ms (all runs 1.2-1.5 s, raw read 97-121 MB/s over 13 reads). Warm speedup 0.98x / 1.12x, so without a cold cache bulk is no faster there; FSCTL cold first run against the bulk median 11.8x / 12.8x. Whether the first bulk run was cold is not established: the FSCTL runs before it warm the $MFT file cache, not the volume reads bulk makes, and its raw read (97 / 106 MB/s) was only a little slower than the later runs. Not measured there: `C:`, alternated pairs.
- [ ] Define consistency and failure behavior before making the bulk reader the default.

## P4 - Product layout: three tools

- [x] Split the product into `dirsizer-bulk`, `dirsizer-fsctl`, and `dirsizer-inspect` (separate projects, one shared core). The `--reader` option was removed from the CLI. `dirsizer-fsctl`'s output was byte-identical to the frozen baseline taken before the split; `dirsizer-fsctl` and `dirsizer-bulk` are EQUAL through `scripts\Compare-Readers.ps1` in the JIT and NativeAOT builds.
- [x] All three tools require elevation through one shared `app.manifest` (`requireAdministrator`), with no `runas` code. Present in the NativeAOT executables and the release zip (checked by binary search with a negative control). **Not tested:** what a non-elevated console does when it launches one; documented in the README as a Windows behaviour to expect (no `> file` or pipes from a non-elevated console).
- [x] `dirsizer-inspect`: `--volume`, `--mft-extents`, `--slots`, `--record` (with non-resident `$ATTRIBUTE_LIST` expansion), `--compare`, extensive `--help`. Formatter and run-list decoder tested without a volume (mutation-checked); results cross-checked against independent sources on the test volume (including 74 extension records that all point back at their base record).
- [x] Fixed a shared-code bug found by `dirsizer-inspect`: `BulkNative.MapLogicalToPhysical` dropped the position inside the cluster for offsets that are not cluster-aligned. Two tests reproduced it before the fix; the bulk reader (aligned blocks) was unaffected.
- [x] Benchmark of the shipped NativeAOT tools (`dirsizer-fsctl` vs `dirsizer-bulk`, live C:, 5 pairs): median speedup 1.47x (per pair 1.42x to 2.14x; the 2.14x is one unusually slow FSCTL run). The five session medians so far are 1.37x, 1.42x, 1.47x, 1.52x, 1.52x.
- [x] Release script publishes the three tools and packages them with the README and license (`dist\DirSizer-v0.3.0-win-x64.zip`, checked: three executables, each with the manifest, none containing the test-hook names, each passing its `--self-test`). The version now lives in `Directory.Build.props`.
- [x] Dropped: a combined `dirsizer.exe` that tries bulk and falls back to fsctl. The name now belongs to the any-filesystem tool of P5.
- [ ] Benchmark and inspect features that were not done: `dirsizer-inspect --benchmark` (use `scripts\Compare-Benchmark.ps1`), and expanding `--record` for records other than by number (for example by path).

## P5 - dirsizer.exe: any filesystem, no elevation

Specified in [design_fs.md](design_fs.md). Independent of `DirSizer.Core`; none of the three NTFS tools was changed.

- [x] `src\DirSizer.Fs` (`dirsizer.exe`): `FindFirstFileExW` with `FindExInfoBasic` and `LARGE_FETCH`, no per-file open, extended-length paths, reparse-point directories not entered, access denied skipped and counted, `--strict` exit 3, `asInvoker` manifest.
- [x] Parallel walk: dedicated threads, one shared LIFO stack (intentionally unbounded; peak queue measured), `pending` counter for termination, directory ids allocated at discovery so `ParentId < Id`, aggregation as one reverse loop.
- [x] `--self-test` (28 tests, JIT and NativeAOT builds): independent-oracle comparison for 1, 3 and 8 workers (nested and empty directories, zero-byte file, Unicode names, a path over 260 characters), junction, hard link, deny ACL, an 800-deep chain, a 10,000-wide fan-out (peak queued directories 10,000), the LARGE_FETCH fallback, the classification of find-first errors (missing, empty, denied, other), a failing directory in the middle of a walk, cancellation before and during a walk, a throwing worker, a failing caller (no worker left running), rejected settings, output and JSON.
- [x] Mutation checks (eight deliberate breakages, each made its named test fail).
- [x] `scripts\Compare-Fs.ps1 -Oracle` on `src\`: root and every direct child equal to the framework's enumeration.
- [x] `scripts\Compare-Fs.ps1 -Bulk` on the `T:` fixture against `dirsizer-bulk`: every difference explained by hard links, NTFS metadata, a directory that cannot be read, or a reparse-point directory that bulk lists (see design_fs.md). At the root, the NTFS metadata files that bulk lists sum to 11,749,216 bytes; the difference left after subtracting the hard-link and denied-directory differences is 11,749,316 bytes, so 100 bytes are not accounted for (probably a file that changed between the two runs; not investigated).
- [x] Worker sweep, `C:` (4 logical processors, warm cache, about 228,000 directories and 786,000 files, 3 runs per count, medians): 1 worker 44.0 s, 2 workers 25.7 s, 4 workers 20.3 s (2.2x), 8 workers 19.2 s (5 % faster than 4, below the 10 % rule for changing the default). In an earlier single run the workers spent nearly all their time inside the enumeration calls (`idle_ms_total` under 0.1 s at 4 workers), so the shared lock was not the limit. Default `min(ProcessorCount, 8)` kept. One machine, one volume.
- [ ] Not measured: cold file cache, a second machine, a network share, exFAT/FAT, ReFS. Run before claiming anything for those.
- [x] Non-elevated start: the packaged `dirsizer.exe` was started through `explorer.exe` from an elevated shell, so that it ran with a standard token (Medium Mandatory Level, Administrators "deny only"). No UAC prompt; `C:\ --json --strict --benchmark > file 2> file` gave exit 3 (182 directories denied without elevation, 17 with), stdout one valid JSON document, the warning and the benchmark on stderr; a text run redirected to a file gave exit 0. This was a script-launched process, not a person typing in an interactive terminal.
- [x] Ctrl+C: a real console event sent to a running scan stops it (the packaged exe, about 4 s into a scan of `C:\`): `error: canceled` on stderr, nothing on stdout, the process gone; Ctrl+Break gave exit 1. The event was ignored by a scan started as a descendant of the agent's own shell (consistent with an inherited "ignore Ctrl+C" flag, not checked further) and worked for one started through Explorer. Not tested: Ctrl+C during the output phase after the scan (the code lets it end the process).
- [x] Enumerator comparison (branch `feature/fs-enumerator-benchmark`; spec and results in design_fs.md, "Enumerator comparison (P4)"): `find` behind an enumerator contract with no change in behaviour (snapshots of three trees identical before and after), `GetFileInformationByHandleEx` (B) and `NtQueryDirectoryFileEx` (C) behind `--enumerator`, 34 self-tests (JIT and NativeAOT), `scripts\Get-FsSnapshot.ps1`, `scripts\Compare-Enumerators.ps1`, `scripts\New-EnumFixture.ps1`. Snapshots of all variants are identical to `find` on `T:`, `C:\Program Files\dotnet`, drivers and two synthetic trees (on exFAT all but the `idextd` variants, which fail with error 87). Finalists, workers 8, 5 rounds, median `walk_ms` against `find`: `C:` B -9.1 %, C -10.2 %; 20,000 small directories B -9.1 %, C -13.8 %; 10 large directories B -34.6 %, C -34.2 %. Decision: `find` stays the default (B misses the 10 % bar, C is experimental and never promoted by the rule); the code review found one issue (empty directory on the first query), fixed. One machine, one volume, warm cache.
- [x] Default enumerator with a fallback (branch `feature/fs-enumerator-default`; spec and results in design_fs.md, "Default enumerator with a fallback"): `--enumerator=auto` (B `handle:full:64`, falling back to `find` only on a directory's first query failing with `ERROR_INVALID_PARAMETER`, scan-wide, one-way, never re-probes B), 39 self-tests (JIT and NativeAOT), real-hardware smoke check (`auto` identical to `find` on every tree tested, no fallback triggered anywhere here). Re-measurement, 10 rounds (more than the original 5, one warm-up pass), `C:`, workers 8: `find` median 18,761.7 ms, `handle:full:64` median 17,545.8 ms (-6.5 %), `auto` median 17,292.4 ms (-7.8 %, confirming no wrapper overhead). `mh/mf` = 0.9352, above the 0.90 pass bar. Decision: the default stays `find`; `EnumeratorSpec.Default` was not changed. `--enumerator=auto` remains available and fully tested.
- [ ] Enumerators, not done: the worker sweep for a winner (none qualified, on either branch), cold cache, ReFS and network shares (a share root was not tried), a second machine.

## Promotion criteria: bulk from experimental to default candidate

Current product state: `dirsizer-fsctl` is the conservative reference, `dirsizer-bulk` is a separate, experimental tool, and there is no automatic fallback between them (and no combined tool that tries bulk and falls back to fsctl). Bulk should become a default candidate only after all of these:

- [x] Correctness on another NTFS volume (different size, cluster size, or MFT record size) with the same reader-vs-reader comparison. U: (VHDX, 2 GiB, 64 KiB clusters, 4 KiB MFT records, `New-TestVolume.ps1`, `New-AbFixture.ps1`; `compact /c` does not compress with 64 KiB clusters, so U: has no compressed file): `Compare-Readers.ps1` EQUAL; `DirSizer.Compare` 0 mismatches over 256 slots (84 in-use records, 15 extension records).
- [x] Correctness with a strongly fragmented MFT (several extents, so the multi-extent and multi-call `ERROR_MORE_DATA` paths run on real data, not only on a small extent map). V: (VHDX, 2 GiB, 4 KiB clusters, 1 KiB records, `New-FragmentedMft.ps1`, `New-AbFixture.ps1`): 28 MFT extents, 100,608 slots; `Compare-Readers.ps1` EQUAL in 3 runs; `DirSizer.Compare` 0 mismatches over 100,520 in-use records; the extent map read with 32/48/64/100-byte buffers took 28/14/10/6 calls with 27/13/9/5 `ERROR_MORE_DATA` responses and was equal to the normal read.
- [ ] Failure paths that could not be executed here: a failing raw read, a corrupt extent map on a real volume.
- [x] Performance re-measured on more than one machine, and with a cold file cache. Cold cache: `C:` of this machine, bulk 6.27x faster than FSCTL. Second machine: two data volumes, bulk about as fast as a warm FSCTL (0.98x-1.12x) and about 12x faster than a cold one (see P3). The advantage of bulk is mainly a cold cache.

Cold-cache and multi-machine numbers are needed to promote it, not to offer it as an option.

## Direction: from a one-shot scanner to a persistent, incremental index

Today every run is a one-shot scan: read the MFT (or walk the tree), aggregate, print, discard. The natural next step is an NTFS metadata index that is built once from the MFT, persisted, and kept current from the USN change journal. The phases below are ordered; each one is only started after the previous one is verified. They are labelled I1-I6 so they do not collide with the P0-P5 sections above.

Implementation plans: I1 in [2026-09-23-i1-finish-current-scanner.md](superpowers/plans/2026-09-23-i1-finish-current-scanner.md); I2-I5 in [2026-09-23-persistent-index.md](superpowers/plans/2026-09-23-persistent-index.md) (an experimental `dirsizer-index.exe`, not a change to `dirsizer.exe`).

```text
now
│
├─ I1  MFT direct scan        → fast, correct one-shot size analysis
├─ I2  persistent index       → cheaper second and later runs
├─ I3  USN journal            → incremental update
├─ I4  subtree query          → analysis of one directory from the shared index
├─ I5  repeated analysis      → fast before/after comparison around a cleanup
└─ I6  metadata engine        → candidates only: search, fzf-like search, listing, file manager
```

### I1 - Finish the current scanner

Do not change the design here; finish what is open. MFT direct scan, whole-volume file/folder aggregation, and the FSCTL cross-check are done (P0-P4); `dirsizer.exe` already picks MFT, FSCTL, or a directory walk automatically. Remaining:

- [x] Correctness and stability on a fragmented MFT (28 extents) and on another volume (see "Promotion criteria" above). A VHDX cannot hold a larger MFT than `C:` has.
- [x] Performance with a cold file cache and on a second machine (P3). Cold cache on `C:`: FSCTL 38.1 s, bulk 6.1 s. Second machine, two data volumes: warm FSCTL 1.3-1.5 s, cold FSCTL 15.6-16.7 s, bulk 1.3 s.

Deliverable: a fast, correct one-shot NTFS size analyzer.

### I2 - Persistent metadata index

Reuse a previous result without rescanning the MFT.

```text
MFT scan → in-memory index → on-disk cache
```

Candidate fields per record: FRN (record number + sequence), parent FRN, name, logical size, directory/file flag, the timestamps actually needed, and volume identity (serial number, MFT geometry). Per volume: the USN journal ID and the last processed USN, recorded at scan time so I3 can resume from it.

No USN-based updates yet. Goal: measure load cost against scan cost, and define when a cache is invalid (different volume serial, journal ID changed or journal deleted, format version changed).

- [x] Specify the on-disk format, versioning, location, and invalidation rules: [design_index.md](design_index.md) (`src\DirSizer.Index\IndexFile.cs`, format version 1, SHA-256 trailer, `%LOCALAPPDATA%\dirsizer\index\<serial>.dsix`).
- [x] Save and load the index; the loaded result must equal a fresh scan: `dirsizer-index --verify` loads the saved file back, aggregates it with the shared stages and compares it record by record with the scan (T: and C:, 0 differences; the check was shown to fail when the file writes a wrong size: 283 differences on T:, exit code 2). The whole-volume listing equals `dirsizer-mft --json` on T: (root, 35 directories, 234 files, root children).
- [x] Measure load time and file size against scan time on `C:`: 923,854 records, 111.6 MiB, full scan 5,843 ms, save 2,051 ms, load 1,703 ms, recompute 374 ms (medians of 3, warm cache, one machine, JIT Release build). Load + recompute (about 2.1 s) is about 2.8x cheaper than a warm full scan.

### I3 - Incremental update from the USN journal

```text
initial MFT scan → persistent index → USN journal (create/delete/rename/modify)
                → index update → folder-size update
```

Rule: **do not infer the final state from USN events alone.** Use the journal as a change notification and a list of affected FRNs, then establish the current state from the MFT:

```text
USN event → affected FRN → re-read the MFT record if needed → current state
```

- [x] Read the journal from the saved USN; handle journal wrap (`ERROR_JOURNAL_ENTRY_DELETED`), journal recreation (ID change), and a disabled journal by falling back to a full rescan. `UsnJournal`, `IndexValidity`; self-tests with synthetic journal pages; on T:, `Test-IndexIncremental.ps1` recreated and disabled the journal and damaged the index file, and each gave a full scan with the stated reason. A wrap was not forced on a real volume (only the self-test covers it).
- [x] Apply create, delete, rename/move (parent change), size change, and hard-link changes; re-read records by FRN instead of trusting event payloads. `IndexUpdater` (FSCTL re-read, attribute lists for extension records, NTFS metadata re-read every time); `Test-IndexIncremental.ps1`: every `--verify` after real changes on T: had 0 differences, including after 5,000-6,000 new files grew `$MFT` and after they were deleted. The checks failed when metadata re-reads were disabled (record 0, `$MFT`, once the MFT grew; without that step no check failed, so the step was added) and when extension-record reads were disabled (the hard-linked file, whose `$DATA` is in an extension record).
- [x] Aggregation after an update: the whole index is recomputed in memory (`Recompute`, 483 ms on C:), not only the ancestor chains. The result is identical to a scan by construction.
- [ ] Only if `recompute_ms` dominates an incremental run: update the ancestor chains instead of recomputing everything. On C: it does not (480 of 2,630 ms with the delta file); loading the base file (1,508 ms) does.
- [x] Verify: after a scripted set of changes on the `T:` fixture, the incrementally updated index equals a fresh full scan (`Test-IndexIncremental.ps1`, ALL CHECKS PASSED, 36 checks). C: timings: an incremental run takes 55 % of a full run (4,529 ms against 8,219 ms, medians of 3, `Measure-Index.ps1`, one machine, warm cache, JIT Release build).
- [x] Decision gate before I4 (plan: incremental below 50 % of a full run): first 55 %, dominated by rewriting the whole 112 MiB file (1,914 ms). Remedy chosen by the user: write only the changed part. An incremental run now writes a delta file next to the base (the entries changed since the base, cumulative, with its own checksum, bound to the base by the base's SHA-256; a full save folds it in once it exceeds max(1,000, 5 % of the records); design_index.md "Delta file"). C:: save 7 ms, incremental 2,630 ms = **31 %** of a full run (8,418 ms). `Test-IndexIncremental.ps1` (55 checks) checks the kind of each save and reloads a delta that removes base-file records; it failed when the delta's removals were ignored on load or replaced entries were not recorded. Load then took 1,508 ms, the largest phase.
- [x] Faster load of the base file (design_index.md "Loading fast"): server GC for `dirsizer-index` (GC pauses during the load about 500 -> 90 ms; full runs also faster; peak memory of a full run 468 -> 598 MiB), and SHA-256 computed on a second thread piece by piece while the file is read and parsed, with parsing hardened so a damaged file is still reported by its checksum (self-tests, mutation-checked: reporting the parse error first, and hashing only the first piece, each fail a test). Format unchanged. C:: load 752 ms (was 1.6-1.7 s the same day), incremental 1,939 ms = 25 % of a full run. SHA-256 is now the floor of the load; a non-cryptographic checksum would need a format version (not done).
### I4 - Subtree-scoped analysis

Analysing `C:\Users\foo\Downloads` becomes a query on the index: resolve the path to its FRN, take its descendants, aggregate. It is not a separate scanner.

```text
whole-volume MFT index
├── C:\Users
├── D:\Games
└── E:\Archive
```

Today a non-root path always uses the directory walk (unified-strategy spec); with an index, any subtree of an indexed volume can be answered without walking it.

- [x] Path → FRN resolution on the index; subtree totals equal to a fresh scan of that subtree (and explain differences with `dirsizer-fs.exe`, as in design_fs.md). Windows resolves the path (`PathResolver`; a record reused with another sequence number is refused, mutation-checked); on T:, a subtree's size equals the sum of its files, `dirsizer-fs` (root and every child), and the same directory in the whole-volume listing (`Test-IndexIncremental.ps1`, ALL CHECKS PASSED). The differences with `dirsizer-fs` on trees with hard links are the ones design_fs.md already lists for the NTFS tools.
- [x] Share one index across whole-volume and subtree analyses: one `<serial>.dsix` per volume; every query loads and updates the same file.

### I5 - Fast repeated analysis

The typical cleanup loop is: analyse → delete unwanted files → analyse again. Today the second step is a full MFT scan and a full re-aggregation. With I2-I4 it becomes: previous index → USN delta → update only the changed parts → re-aggregate.

- [x] Before/after report: which directories shrank or grew, by how much, since the previous run. `dirsizer-index --changes` (`ChangeReporter`, design_index.md "Changes since the previous run"; 5 self-tests, mutation-checked: ignoring the sequence number, dropping deleted directories, and counting a directory moved in with its old size each fail a test). On T:, deleting a 20,000-byte file showed -20,000 for the queried folder and its subfolder, `--no-save` kept the baseline for the next run, and a saved run left nothing to report (`Test-IndexIncremental.ps1`, ALL CHECKS PASSED).
- [x] Measure the second run against a full rescan after a realistic cleanup: C:, 30,000 files (30 MiB) deleted, incremental with `--changes` 3,793 ms vs full 7,369 ms (51 %; without `--changes` 2,787 ms, 38 %); the deleted folder listed with exactly -30,720,000 bytes (medians of 3, one machine, warm cache, JIT Release build).
- [ ] Only if `--changes` becomes the common case: its cost is about 1 s on C: (a second `Recompute` for the baseline, and a comparison that walks the whole volume for the root). Storing the directory sizes in the index would remove the first; not done.

### I6 - Candidates after the index exists (not planned)

Not planned work. Re-evaluate only after I2-I3 are complete and verified. The same index could serve:

```text
NTFS index
├── folder size
├── file search
├── fzf-like search
├── recent files
└── directory listing
```

Candidates: file search, fzf-like interactive search, fast directory listing, and eventually a file manager (`zurari for Win`). None of these changes the current purpose of the project, which is folder-size analysis.

## Deferred semantics

`--allocated` is intentionally not part of the current CLI. Resident, sparse, compressed, and hard-linked allocation semantics need a separate specification and test matrix before physical usage is reported.
