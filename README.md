# dirsizer

**English** | [日本語](README-jp.md)

`dirsizer` is a small, read-only Windows CLI that calculates NTFS folder sizes from MFT metadata instead of recursively enumerating files and directories.

## License

DirSizer is released under the MIT License. See [LICENSE](LICENSE).

## Status

This is the first practical baseline. The three NTFS tools (`dirsizer-bulk`, `dirsizer-fsctl`, `dirsizer-inspect`) read the master file table (MFT) directly; they do not use `FindFirstFile`, `Directory.EnumerateFiles`, the USN journal, or path traversal. `dirsizer.exe` is the general-purpose counterpart: it walks the directory tree of any filesystem with `FindFirstFileExW` and needs no elevation.

## The tools

DirSizer is four separate executables:

| Tool | What it is for | Notes |
| --- | --- | --- |
| `dirsizer.exe` | Folder-size scan of **any** filesystem, without elevation | Reads directory listings with `FindFirstFileExW` on several threads and never opens individual files. Slower than `dirsizer-bulk` on NTFS, and its totals differ from the NTFS tools by design; see [dirsizer.exe](#dirsizerexe-any-filesystem-no-elevation) below and [docs/design_fs.md](docs/design_fs.md). |
| `dirsizer-bulk.exe` | Fast folder-size scan | **Experimental.** Reads the raw `$MFT` in large blocks. About 1.4x faster than `dirsizer-fsctl` in measurements. |
| `dirsizer-fsctl.exe` | The same folder-size scan through `FSCTL_GET_NTFS_FILE_RECORD` | The reference implementation. Use it to cross-check `dirsizer-bulk`, to benchmark, or when you want the conservative method. |
| `dirsizer-inspect.exe` | Looking inside the NTFS `$MFT` | For investigating and developing: one record's attributes, the `$MFT` extents, slot counts, raw-vs-FSCTL comparison. Not a usage scan. |

`dirsizer-bulk` and `dirsizer-fsctl` take the same options and print the same results; you choose the one you want, and neither ever falls back to the other. `dirsizer.exe` has its own options (`--workers`, `--strict`). Run any tool with `--help` (`dirsizer-inspect --help` is the most detailed).

### Elevation

The three NTFS tools read raw NTFS metadata, so each of those executables carries a manifest that requires Administrator (`requireAdministrator`): Windows asks for elevation (UAC) when you start one. There is no `runas` logic in the code. In an already elevated terminal they simply run. `dirsizer-bulk` and `dirsizer-inspect` also enable `SeBackupPrivilege` inside the process, and report a clear error if the account does not hold it.

One consequence you should know about: Windows cannot start a program that requires elevation *in place* from a non-elevated console. PowerShell reports that the operation requires elevation, and other launchers open a separate elevated window, where you cannot redirect or pipe the output. To use `> result.json` or a pipe, open an Administrator terminal first. (This behaviour comes from Windows and was observed with these executables.) The elevated window closes when the process exits, so when a tool finds it is the only process attached to its console it waits for Enter before exiting (`ConsolePause.cs`); in a terminal shared with a shell it does not wait. The manifest is the single file `src\Shared\app.manifest`; changing `level` there to `asInvoker` removes the requirement for all three. `dirsizer.exe` needs none of this: it has its own manifest with `asInvoker`, runs in any terminal, and `> file` and pipes work.

## Build

Install the .NET 8 SDK. Each tool is its own project:

| Project | Executable |
| --- | --- |
| `src\DirSizer.Bulk\DirSizer.Bulk.csproj` | `dirsizer-bulk` |
| `src\DirSizer.Fsctl\DirSizer.Fsctl.csproj` | `dirsizer-fsctl` |
| `src\DirSizer.Inspect\DirSizer.Inspect.csproj` | `dirsizer-inspect` |
| `src\DirSizer.Fs\DirSizer.Fs.csproj` | `dirsizer` |

Publish a small NativeAOT executable (this needs the Visual Studio C++ build tools):

```powershell
dotnet publish src\DirSizer.Bulk\DirSizer.Bulk.csproj -c Release -r win-x64
```

The executable is under `artifacts\publish\DirSizer.Bulk\release_win-x64\`. NativeAOT removes the runtime dependency and enables trimming. `scripts\release.ps1` publishes all four (see "Releases").

For a fast local compile, build the solution (`DirSizer.sln`, every tool and the developer tool) or one project. Each project builds into its own folder, `artifacts\bin\<project>\release_win-x64\`:

```powershell
dotnet build -c Release
dotnet build src\DirSizer.Bulk\DirSizer.Bulk.csproj -c Release
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll T:
```

See [docs/design_mft.md](docs/design_mft.md) for how the bulk reader works (extents, USA fixup, record order, live-volume check), the layout of the three tools, and the measured results.

Verify the two scan tools against each other on a quiescent test volume (a small NTFS volume labelled `NTFSTEST`), and compare their speed on any volume:

```powershell
.\scripts\New-AbFixture.ps1 -Volume T:                                # creates a fixture with links, streams, sparse and compressed files, deleted files
.\scripts\Compare-Readers.ps1 -Volume T:                              # dirsizer-fsctl vs dirsizer-bulk, JSON and text; exit code 0 = EQUAL
.\scripts\Compare-Benchmark.ps1 -Volume C: -Runs 5                    # alternating, min/median/max per phase
.\scripts\Test-BulkInstability.ps1 -Volume T:                         # grows the MFT of a disposable NTFSTEST volume; see the script header
.\scripts\Compare-Fs.ps1 -Path .\src -Oracle                          # dirsizer.exe vs an independent sum, on a quiescent tree
.\scripts\Compare-Fs.ps1 -Path T:\ -Bulk                              # dirsizer.exe vs dirsizer-bulk: lists the expected differences (docs\design_fs.md)
.\scripts\Compare-Fs.ps1 -Path C:\ -Sweep -Runs 3                     # walk time for 1, 2, 4 and 8 workers
```

`scripts\Get-ReferenceSnapshot.ps1` captures a tool's complete output with timings removed; it is also the regression check for refactoring the shared code: take a snapshot before, take one after, and they must be identical. To compare the two readers record by record, build and run the developer tool:

```powershell
dotnet build src\DirSizer.Compare\DirSizer.Compare.csproj -c Release
dotnet artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.exe T:
```

Each tool has built-in tests that need no volume:

```powershell
dotnet .\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test
dotnet .\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll --self-test
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test
dotnet .\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test       # builds a fixture in %TEMP%; needs no elevation
```

## Repository layout

```
DirSizer.sln                 all projects
Directory.Build.props        the version (one place) and the shared build output settings
src\DirSizer.Fsctl\          dirsizer-fsctl
src\DirSizer.Bulk\           dirsizer-bulk
src\DirSizer.Inspect\        dirsizer-inspect
src\DirSizer.Fs\             dirsizer.exe (directory enumeration, any filesystem, no elevation; independent of Core)
src\DirSizer.Compare\        developer tool: record-by-record comparison of the two readers
src\DirSizer.Core\           the reader-independent pipeline (merge, relationships, aggregation)
src\Shared\                  files compiled into more than one tool (options and output, ConsolePause, app.manifest, ...)
src\Shared\BulkReader\       the raw $MFT reader, shared by dirsizer-bulk, dirsizer-inspect and the developer tool
scripts\                     benchmark, comparison and release scripts
docs\                        design notes and the roadmap
artifacts\                   build output (git-ignored)
dist\                        release packages (git-ignored)
```

## Releases

The version is defined once, in `Directory.Build.props`. To build and package the current
version without publishing it:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1
```

This creates `dist\DirSizer-v<version>-win-x64.zip` containing the four NativeAOT
executables (dirsizer.exe, dirsizer-bulk.exe, dirsizer-fsctl.exe, dirsizer-inspect.exe), this README, and the license. To create the GitHub release, install and sign in
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

Run the NTFS tools from an Administrator terminal (see "Elevation"). `dirsizer-bulk` and `dirsizer-fsctl` share these options; `dirsizer-inspect` has its own (`dirsizer-inspect --help`); `dirsizer.exe` is described in its own section below:

```powershell
.\dirsizer-bulk.exe C:\
.\dirsizer-bulk.exe D:\ --top=50 --files
.\dirsizer-bulk.exe C:\ --top=100 --json > result.json
.\dirsizer-fsctl.exe C:\ --top=1 --json --benchmark
.\dirsizer-inspect.exe C: --record 5
```

The default is `--top=25`. The table output identifies the selected limit and
separates directories from files. Use `--top=N` to change it. With `--files`,
the output includes both sections; otherwise it shows only the largest
directories. Known NTFS metadata records are labeled under
`[NTFS metadata]` (for example `$MFT` and `$Bitmap`). An `[unresolved record
...]` path means the MFT record had data but its usable parent/name relationship
could not be reconstructed and it was not a known NTFS metadata record.

Progress is rendered on one updating line to stderr as `current/estimated (percent%)`. This keeps stdout suitable for table output or JSON redirection.

## dirsizer.exe (any filesystem, no elevation)

`dirsizer.exe` measures folder sizes on **any** Windows filesystem (NTFS, ReFS, exFAT, FAT, network shares) by reading directory listings with `FindFirstFileExW`. It never opens individual files, reads directories on several threads at once, and runs without elevation: a directory you cannot read is skipped and counted.

```powershell
.\dirsizer.exe C:\Users
.\dirsizer.exe \\server\share --top=50 --files
.\dirsizer.exe D:\ --json > result.json
.\dirsizer.exe C:\ --strict --workers 8 --benchmark
```

The options are `--top`, `--files`, `--dirs`, `--json`, `--benchmark` and `--self-test` as in the NTFS tools (`--diagnostics` is not offered), plus `--workers N` (default: the number of processors, at most 8) and `--strict` (exit code 3 if a directory could not be read; the result is still written). The path may be any directory, not only a drive root. `--enumerator=NAME[:CLASS[:KiB]]` is an advanced option for comparisons: it chooses the API that reads the directories (`find`, the default, is `FindFirstFileExW`; `handle` is `GetFileInformationByHandleEx`; `nt` is `NtQueryDirectoryFileEx`, experimental; `auto` is `handle:full:64` with an automatic fallback to `find` if that information class is not supported on the file system being scanned); the measurements are in [docs/design_fs.md](docs/design_fs.md), "Enumerator comparison (P4)" and "Default enumerator with a fallback". The `idextd` class does not work on exFAT. Exit codes: 0 result written; 1 error; 3 with `--strict`, at least one directory could not be read. Progress is one updating line on stderr (`Scanning: N directories, M files`), shown only when stderr is a terminal. The JSON output is ASCII only (non-ASCII characters in paths are escaped), so it is exact under any console code page; the table output follows the console code page, so use `--json` when paths must be exact. The JSON has the same top-level layout as the NTFS tools' but different `statistics` and `performance` keys; see [docs/design_fs.md](docs/design_fs.md).

Its results are **not identical** to those of the NTFS tools, by design (details and measured differences: [docs/design_fs.md](docs/design_fs.md)):

| Case | `dirsizer.exe` | NTFS tools |
| --- | --- | --- |
| Hard-linked file | counted in every directory that holds a name | counted once |
| NTFS metadata (`$MFT`, `$Bitmap`, ...) | not visible, not counted | counted |
| Directory you cannot read | skipped, counted in `directories_denied` | counted |
| Junction, symbolic link or mount point directory | not entered, counted in `reparse_skipped` | listed as an empty directory |

Sizes are logical (the size the directory listing reports); alternate data streams are not included. On NTFS `dirsizer.exe` is expected to be slower than `dirsizer-bulk`: use the NTFS tools when you want speed and can run elevated. Only local NTFS has been verified; other filesystems and network shares are supported by design and unverified until they are run.

## dirsizer-bulk (experimental)

`dirsizer-bulk` reads the raw `$MFT` from the volume in large blocks (the record offsets come from the `$MFT` extent map) and then runs exactly the same parsing, merging, relationship, aggregation, and output code as `dirsizer-fsctl`. In measurements on one machine it was about 1.4x faster on a live C: volume (median 1.37x to 1.52x in five benchmark sessions; per pair mostly 1.36x to 1.59x, and 2.14x once when the FSCTL run in that pair was unusually slow). All of the saving is in reading the MFT; parsing and merging cost the same. Do not treat the figure as a guarantee: it has only been measured on one machine and one volume, with a warm file cache.

- **No fallback.** If the raw read fails, `dirsizer-bulk` reports the error and exits with 1. It never switches to `dirsizer-fsctl` on its own.
- **Same result.** On a quiescent NTFS test volume the complete output of `dirsizer-bulk` (root size, every directory and file with path, size, and order, the root's children, counters, unresolved records) is identical to that of `dirsizer-fsctl`, in the JIT and the NativeAOT build. `scripts\Compare-Readers.ps1` checks this.
- **Progress.** Like `dirsizer-fsctl`, it writes progress to stderr on one updating line (`Scanning MFT: n/N (p%)`, then `Calculating folder sizes...`), so a scan that takes several seconds is not silent. stdout, and so `> result.json` and pipes, is unaffected.
- **Live-volume check.** `dirsizer-bulk` records the MFT layout (volume serial number, geometry, valid data length, and extent map) before and after reading and rescans once if it changed. This detects the MFT growing or being relocated while it is read. It does **not** detect files being created, deleted, or changed inside existing records during the scan; a live volume can always change under either tool.

Exit codes of `dirsizer-bulk` (`dirsizer-fsctl` uses 0 and 1):

| Code | Meaning |
| ---: | --- |
| 0 | The result was produced and the MFT layout did not change during the scan. |
| 1 | An error (bad option, invalid volume, not NTFS, no access, I/O failure); no result. |
| 3 | The result was produced, but the MFT layout changed during the scan even after one rescan, so the result should not be treated as a stable snapshot. A warning is written to stderr. This is not an error like exit code 1: the output is complete, only its consistency is not guaranteed. |

With `--json`, both tools write a `reader` field (`fsctl` or `bulk`). `dirsizer-bulk` also writes a `bulk` object with the scan stability (`stable` or `unstable`), the number of attempts, the layout change that was seen, the phase timings, and the slot counts. For `dirsizer-bulk`, `records_scanned` is the number of MFT slots examined, `records_skipped` counts slots that could not be read or parsed, and `statistics.performance.query_ms` is the whole acquisition phase (extents, raw read, USA fixup, layout re-check).

## dirsizer-inspect

A read-only tool for looking inside the NTFS master file table. `dirsizer-inspect --help` lists everything with the terms explained. In short:

```powershell
dirsizer-inspect C:                         # volume geometry, MFT size, number of record slots and extents
dirsizer-inspect C: --mft-extents           # every extent of the $MFT
dirsizer-inspect C: --slots [--diagnose]    # count slots: in use, deleted, unused, damaged
dirsizer-inspect C: --record 5 [--dump] [--raw]   # one record: header, update sequence array, every attribute
dirsizer-inspect C: --compare 12345         # the same record through the raw read and through FSCTL
```

`--record` shows a record's `$FILE_NAME` names with parent and namespace, its `$DATA` streams, and its `$ATTRIBUTE_LIST` entries, including the extension records a large list (for example a file with many hard links) points to, together with the model the shared parser builds from the record. A record number is decimal or `0x`-prefixed hexadecimal; `fsutil file queryFileID` gives a file's record number (its low 48 bits). Exit codes: 0 ok, 1 error, 2 `--compare` found a difference.
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