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
- [ ] Benchmark a cold file cache and another volume.
- [ ] Define consistency and failure behavior before making the bulk reader the default.

## P4 - Product layout: three tools

- [x] Split the product into `dirsizer-bulk`, `dirsizer-fsctl`, and `dirsizer-inspect` (separate projects, one shared core). The `--reader` option was removed from the CLI. `dirsizer-fsctl`'s output was byte-identical to the frozen baseline taken before the split; `dirsizer-fsctl` and `dirsizer-bulk` are EQUAL through `scripts\Compare-Readers.ps1` in the JIT and NativeAOT builds.
- [x] All three tools require elevation through one shared `app.manifest` (`requireAdministrator`), with no `runas` code. Present in the NativeAOT executables and the release zip (checked by binary search with a negative control). **Not tested:** what a non-elevated console does when it launches one; documented in the README as a Windows behaviour to expect (no `> file` or pipes from a non-elevated console).
- [x] `dirsizer-inspect`: `--volume`, `--mft-extents`, `--slots`, `--record` (with non-resident `$ATTRIBUTE_LIST` expansion), `--compare`, extensive `--help`. Formatter and run-list decoder tested without a volume (mutation-checked); results cross-checked against independent sources on the test volume (including 74 extension records that all point back at their base record).
- [x] Fixed a shared-code bug found by `dirsizer-inspect`: `BulkNative.MapLogicalToPhysical` dropped the position inside the cluster for offsets that are not cluster-aligned. Two tests reproduced it before the fix; the bulk reader (aligned blocks) was unaffected.
- [x] Benchmark of the shipped NativeAOT tools (`dirsizer-fsctl` vs `dirsizer-bulk`, live C:, 5 pairs): median speedup 1.47x (per pair 1.42x to 2.14x; the 2.14x is one unusually slow FSCTL run). The five session medians so far are 1.37x, 1.42x, 1.47x, 1.52x, 1.52x.
- [x] Release script publishes the three tools and packages them with the README and license (`dist\DirSizer-v0.3.0-win-x64.zip`, checked: three executables, each with the manifest, none containing the test-hook names, each passing its `--self-test`). The version now lives in `Directory.Build.props`.
- [ ] Optional, only if needed: a combined `dirsizer.exe` that tries bulk and falls back to fsctl. Not built.
- [ ] Benchmark and inspect features that were not done: `dirsizer-inspect --benchmark` (use `scripts\Compare-Benchmark.ps1`), and expanding `--record` for records other than by number (for example by path).

## Promotion criteria: bulk from experimental to default candidate

Current product state: `dirsizer-fsctl` is the conservative reference, `dirsizer-bulk` is a separate, experimental tool, and there is no automatic fallback between them (and no combined `dirsizer.exe`). Bulk should become a default candidate only after all of these:

- [ ] Correctness on another NTFS volume (different size, cluster size, or MFT record size) with the same reader-vs-reader comparison.
- [ ] Correctness with a strongly fragmented MFT (several extents, so the multi-extent and multi-call `ERROR_MORE_DATA` paths run on real data, not only on a small extent map).
- [ ] Failure paths that could not be executed here: a failing raw read, a corrupt extent map on a real volume.
- [ ] Performance re-measured on more than one machine, and with a cold file cache.

Cold-cache and multi-machine numbers are needed to promote it, not to offer it as an option.

## Deferred semantics

`--allocated` is intentionally not part of the current CLI. Resident, sparse, compressed, and hard-linked allocation semantics need a separate specification and test matrix before physical usage is reported.
