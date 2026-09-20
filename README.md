# dirsizer

`dirsizer` is a small, read-only Windows CLI that calculates NTFS folder sizes from MFT metadata instead of recursively enumerating files and directories.

## License

DirSizer is released under the MIT License. See [LICENSE](LICENSE).

## Status

This is the first practical baseline. It targets NTFS volumes, requires an elevated terminal, and reads metadata with:

- `FSCTL_GET_NTFS_VOLUME_DATA` for MFT length and record size.
- `FSCTL_GET_NTFS_FILE_RECORDS` for active MFT records.

The scanner parses only the record header, `$FILE_NAME`, and unnamed `$DATA`. It does not use `FindFirstFile`, `Directory.EnumerateFiles`, the USN journal, or path traversal.

## Build

Install the .NET 8 SDK and publish a small NativeAOT executable:

```powershell
dotnet publish -c Release -r win-x64
```

The executable is under `bin\Release\net8.0-windows\win-x64\publish\`. NativeAOT removes the runtime dependency and enables trimming.

For a fast local compile:

```powershell
dotnet build DirSizer.csproj -c Release
```

Build the experimental raw-MFT executable separately:

```powershell
dotnet build DirSizer.Bulk.csproj -c Release
dotnet bin\Bulk\Release\net8.0-windows\win-x64\DirSizer.Bulk.dll T:
```

The bulk executable is experimental and does not fall back to the FSCTL reader.
It runs the same merge, relationship, and aggregation code as the FSCTL reader
(`DirSizer.Core`) and prints the same listing: `--top=N`, `--files`, `--dirs`,
plus `--benchmark` (phase timings), `--diagnostics` (unresolved records) and
`--root-children`. Reader diagnostics go to stderr, results to stdout.
`--diagnose` prints a per-reason breakdown of records the parser rejected.
The scan compares the MFT layout before and after reading it and rescans once if
it changed. Exit code 0 means the layout was unchanged; 3 means it changed even
after the retry, so the printed results are not a consistent snapshot. This
detects MFT growth and relocation, not changes inside existing records.
See [design_mft.md](design_mft.md) for its extent, USA-fixup, record-order, and
acceptance design and the measured results.

Verify the bulk reader against the FSCTL reader on a quiescent test volume, and
compare their speed on any volume:

```powershell
.\scripts\Get-ReferenceSnapshot.ps1 -Volume T: -Out ref.txt          # FSCTL output, timings removed
.\scripts\Compare-BulkToSnapshot.ps1 -Volume T: -Snapshot ref.txt    # exit code 0 = EQUAL
.\scripts\Compare-Benchmark.ps1 -Volume C: -Runs 5                   # alternating, min/median/max
.\scripts\Test-BulkInstability.ps1 -Volume T:                        # grows the MFT of a disposable NTFSTEST volume; see the script header
```

The snapshot script is also the regression check for refactoring the shared
code: take a snapshot before, take one after, and they must be identical.

To compare the two readers record by record on a quiescent test volume (a small
NTFS volume labelled `NTFSTEST`), build the fixture and run the developer tool:

```powershell
.\scripts\New-AbFixture.ps1 -Volume T:
dotnet build DirSizer.Compare.csproj -c Release
dotnet bin\Compare\Release\net8.0-windows\win-x64\DirSizer.Compare.exe T:
```

Run the deterministic parser fixtures with:

```powershell
dotnet .\bin\Release\net8.0-windows\win-x64\DirSizer.dll --self-test
```

## Releases

The version is defined in `DirSizer.csproj`. To build and package the current
version without publishing it:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1
```

This creates `dist\DirSizer-v<version>-win-x64.zip` containing the NativeAOT
executable and this README. To create the GitHub release, install and sign in
with GitHub CLI (`gh auth login`), create a Markdown release-notes file, and
run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 `
  -Publish -NotesFile release-notes.md
```

Publishing requires a clean `master` branch and an unused version tag. The
script pushes `master` and `v<version>`, then attaches the ZIP to the GitHub
release.

## Usage

Run from an Administrator PowerShell:

```powershell
.\dirsizer.exe C:\
.\dirsizer.exe D:\ --top=50 --files
.\dirsizer.exe C:\ --top=100 --json > result.json
.\dirsizer.exe C:\ --top=1 --json --benchmark
.\dirsizer.exe C:\ --reader=bulk          # experimental raw-MFT reader, see "Readers"
```

The default is `--top=25`. The table output identifies the selected limit and
separates directories from files. Use `--top=N` to change it. With `--files`,
the output includes both sections; otherwise it shows only the largest
directories. Known NTFS metadata records are labeled under
`[NTFS metadata]` (for example `$MFT` and `$Bitmap`). An `[unresolved record
...]` path means the MFT record had data but its usable parent/name relationship
could not be reconstructed and it was not a known NTFS metadata record.

Progress is rendered on one updating line to stderr as `current/estimated (percent%)`. This keeps stdout suitable for table output or JSON redirection.

## Readers

`--reader=fsctl` (the default) asks NTFS for one MFT record at a time with
`FSCTL_GET_NTFS_FILE_RECORD`. It is the reference implementation: correctness is
defined by it.

`--reader=bulk` is **experimental**. It reads the raw `$MFT` from the volume in
large blocks (the record offsets come from the `$MFT` extent map) and then runs
exactly the same parsing, merging, relationship, aggregation, and output code as
the FSCTL reader. Like the FSCTL reader it needs an elevated terminal and an NTFS
volume. In measurements on one machine it was about 1.4x faster on a live C:
volume (median 1.37x to 1.52x in four benchmark sessions; per run 1.36x to
1.59x). All of the saving is in reading the MFT; parsing and merging cost the
same. Do not treat the figure as a guarantee: it has only been measured on one
machine and one volume, with a warm file cache.

- **No fallback.** If the raw read fails, `--reader=bulk` reports the error and exits
  with 1. It never switches to the FSCTL reader on its own.
- **Same result.** On a quiescent NTFS test volume the bulk reader's complete
  output (root size, every directory and file with path, size, and order, the
  root's children, counters, unresolved records) is identical to the FSCTL
  reader's, including in the NativeAOT build. `scripts\Compare-Readers.ps1` checks
  this through the CLI.
- **Live-volume check.** The bulk reader records the MFT layout (volume serial
  number, geometry, valid data length, and extent map) before and after reading
  and rescans once if it changed. This detects the MFT growing or being
  relocated while it is read. It does **not** detect files being created,
  deleted, or changed inside existing records during the scan; a live volume can
  always change under either reader.

Exit codes:

| Code | Meaning |
| ---: | --- |
| 0 | The result was produced. For `--reader=bulk` the MFT layout did not change during the scan. |
| 1 | An error (bad option, invalid volume, not NTFS, no access, I/O failure); no result. |
| 3 | `--reader=bulk` only. The result was produced, but the MFT layout changed during the scan even after one rescan, so the result should not be treated as a stable snapshot. A warning is written to stderr. This is not an error like exit code 1: the output is complete, only its consistency is not guaranteed. |

With `--json`, the output has a `reader` field (`fsctl` or `bulk`). For `bulk`
there is also a `bulk` object with the scan stability (`stable` or `unstable`),
the number of attempts, the layout change that was seen, the phase timings, and
the slot counts. For `bulk`, `records_scanned` is the number of MFT slots
examined, `records_skipped` counts slots that could not be read or parsed, and
`statistics.performance.query_ms` is the whole acquisition phase (extents, raw
read, USA fixup, layout re-check).

## Semantics and limitations

- **Logical size** = the file size: the amount of file content.
- Reported size is logical bytes in the unnamed NTFS `$DATA` attribute.
- Directories contribute no bytes; alternate data streams are excluded.
- Deleted records and reparse targets are excluded.
- Hard-linked records are counted once, using one selected parent/name relationship.
- MFT records are queried in descending order and parsed from a shared output buffer.
- Extension records are merged into their base record before paths and sizes are resolved.
- Malformed records are skipped and counters are reported in JSON.
- Only local NTFS drive roots such as `C:\` are accepted.
- `--self-test` runs parser fixtures without opening a volume.
- `--benchmark` adds phase timings and memory measurements to JSON and stderr.

The initial version does not implement `--allocated`, `--deleted`, `--ads`, or
non-NTFS fallback scanning. Physical allocation semantics are deferred until
resident, sparse, compressed, and hard-link cases have dedicated tests.

## JSON

JSON contains the selected `top` limit, the selected `size_mode`, numeric byte
values, paths, and scan counters:

```json
{
  "volume": "C:",
  "top": 25,
  "size_mode": "logical",
  "root": { "path": "C:\\", "size": 123456789 },
  "root_children": [{ "path": "C:\\Users", "size": 987654321 }],
  "directories": [{ "path": "C:\\Users", "size": 123 }],
  "files": [],
  "statistics": {
    "records_scanned": 100,
    "records_accepted": 90,
    "records_skipped": 10,
    "files": 70,
    "directories": 20
  }
}
```

`size_mode` is always `logical` in the current version.
`root` reports the aggregated volume root, while `root_children` reports its
direct children before the top-N filter. Directories and files in the
`directories` and `files` arrays are selected with a bounded priority queue,
then sorted only within the requested top-N result.
The counters are diagnostic and do not change the requested top limit:

- `records_scanned`: MFT metadata queries issued.
- `records_accepted`: active, parseable records retained for the result.
- `records_skipped`: slots that could not be read or parsed.
- `files` and `directories`: accepted records by type.

They are not required to calculate the table, but they show whether the scan
was complete. A nonzero `records_skipped` value means the reported sizes may be
incomplete. JSON currently has no automated test project; it is validated by
the release build and manual `--json` runs.

## Performance baseline

Run `C:\ --top=1 --json --benchmark` from an elevated terminal. The following
baseline was measured on September 20, 2026 against the local C: NTFS volume:

| Metric | Expected property | Measured result |
| --- | --- | ---: |
| MFT queries | One descending query per returned active record | 949,287-949,292 |
| Skipped records | Zero on a healthy volume | 0 |
| Query time | Dominant cost in the FSCTL reader | 5,032-5,288 ms |
| Parser time | Separate CPU phase using shared output buffer | 1,527-1,537 ms |
| Relationship resolution | Linear in retained records | 485-567 ms |
| Aggregation | Linear leaf-to-root pass | 281-302 ms |
| Total scan time | Sum of phases plus setup/output | 8,369-8,772 ms |
| Managed allocation | Lower than per-record buffer-copy design | about 736.5 MB |
| Peak working set | Bounded by retained model and buffers | about 460 MB |

The measured allocation is primarily the retained model (`FileRecord`, filename
strings, lists, and dictionaries), not the reused DeviceIoControl buffers.
Therefore no additional `ArrayPool` layer was introduced in P1; the next
allocation reduction should target model representation after profiling.

## P0/P1/P2 comparison

The comparison below uses live `C:` scans. File activity continued between
runs, so byte totals and record counts are expected to move slightly; the
structural and performance properties are the meaningful comparison.

| Stage | Should be | Real output |
| --- | --- | --- |
| P0 correctness | valid JSON, root aggregation, zero skipped records | root `203,707,362,244`, accepted `916,568`, skipped `0`; top file `pagefile.sys` |
| P1 performance | phase metrics, shared-buffer scan, no per-record copy | `949,287-949,292` queries, `8,369-8,772 ms`, about `736.5 MB` managed allocation |
| P2 output | root/direct children plus bounded top-N | root `203,710,737,592`; `root_children` present; top=3 returned 3 directories and 3 files in descending size |

The latest classified scan reported `949,503` queries, `949,503` parse-successful
records, `32,692` extension records, `916,811` logical records, `0` skipped
records, `10,177 ms` total time, and `161,585` query/second. `query_qps` is
calculated from query time; `overall_records_per_sec` is calculated from total
scan time.

The benchmark now includes `open_ms`, `volume_metadata_ms`, `other_ms`, and
`phase_sum_ms`. `phase_sum_ms` is asserted equal to `total_ms` at runtime. The
latest run measured `total_ms=9,570.3`, `phase_sum_ms=9,570.3`, and
`other_ms=107.4`.

Full 64-bit file references, including sequence numbers, remain in the model.
For live-volume relationship resolution, lookup uses the record number because
metadata can change between the parent `$FILE_NAME` and directory record reads;
strict sequence rejection caused valid current scans to lose the entire tree.
The full reference is still available for a future consistency policy or
snapshot reader.

The earlier aggregate relationship counters were superseded by the A/B/C
diagnostics below. The current classification treats A/B agreement as exact
and reports B/C disagreement separately.

## Sequence diagnostics

The scanner now preserves the MFT record-header sequence (`B`) and compares it
with the `$FILE_NAME` parent sequence (`A`). The FSCTL output is used only for
the returned record number; its upper sequence bits (`C`) are not used for
relationship validation. It reports A/B mismatch samples without using LINQ or
logging every record. On the prepared T: fixture, the result was:

| Check | Result |
| --- | ---: |
| A/B exact parent relationships | 32 |
| A/B mismatch | 0 |
| A=0 fallback | 0 |
| Unresolved relationships | 4 |

The first raw samples showed `A=1, B=1, C=0`. C is deliberately excluded from
the correctness decision: Microsoft documents that the FSCTL returned
identifier can differ from the requested identifier, but does not specify its
upper sequence bits as the record-header sequence. Sequence zero is not treated
as a valid NTFS reference; the reader reconstructs identity from the returned
record number and header sequence instead.

The production and self-test sources contain no LINQ calls. The controlled T:
scan produced `32` A/B exact relationships, `0` A/B mismatches, and `4`
unresolved relationships in `17.99 ms`. An isolated C: scan produced `917,442`
A/B exact relationships and `4` unresolved relationships. Its `42.36 s` runtime
was much slower than earlier C: runs and should be treated as an environmental
measurement, not as a code-regression result; the scan was otherwise valid and
`phase_sum_ms` equaled `total_ms`.

The C: `--diagnostics` run identified all four unresolved records:

| Record | Directory | Logical size | `$FILE_NAME` count | Parent |
|---:|:---:|---:|---:|:---:|
| 12 (`$Quota`) | false | 0 | 0 | none |
| 13 (`$ObjId`) | false | 0 | 0 | none |
| 14 (`$Reparse`) | false | 0 | 0 | none |
| 15 (`$UsnJrnl`) | false | 0 | 0 | none |

They are zero-size NTFS metadata records, so the unresolved relationships do
not reduce reported capacity. The FSCTL reader is now the correctness and
benchmark reference baseline for P3.

## Development direction

The next correctness work should add parser fixtures for long and Unicode names, reparse points, sparse/compressed files, and ADS exclusion. P1 performance measurements are available through `--benchmark`; future allocation work should target the retained model before introducing pooling or parallelism.