# dirsizer.exe - filesystem-enumeration folder size scanner

## Goal

`dirsizer.exe` is the general-purpose tool: a fast, read-only folder-size scan for **any Windows
filesystem** (NTFS, ReFS, exFAT, FAT, network shares), started **without elevation**. It walks the
directory tree with Win32 directory enumeration and adds up the sizes the enumeration returns.

It is the counterpart of the NTFS tools, not a replacement:

| Tool | Purpose | Backend |
| --- | --- | --- |
| `dirsizer.exe` | Ordinary usage scan of any filesystem | directory enumeration (`FindFirstFileExW`), parallel |
| `dirsizer-bulk.exe` | Fast NTFS scan (experimental) | raw `$MFT` |
| `dirsizer-fsctl.exe` | NTFS scan, reference implementation | `FSCTL_GET_NTFS_FILE_RECORD` |
| `dirsizer-inspect.exe` | NTFS `$MFT` investigation | MFT / extents / USA / compare |

`dirsizer.exe` also works on NTFS. There it is expected to be slower than `dirsizer-bulk`, and its
totals differ from the MFT tools by design (see "Differences from the NTFS tools").

"Any filesystem" means the same enumeration strategy everywhere, not identical behaviour: what a
filesystem reports for sizes, errors, virtualized or reparse-like entries, and whether `LARGE_FETCH`
is honoured can differ, network filesystems most of all. Only local NTFS is verified in this work;
other filesystems are supported by design and unverified until they are run.

The principle behind it (the opposite of the MFT tools, which avoid traversal):

> Obtain file metadata from directory enumeration in batches; never open an individual file only to
> get its size. Parallelize independent directory enumerations with a bounded worker pool, and
> optimize only against measured, filesystem-specific workloads.

This replaces the roadmap's earlier placeholder for a combined `dirsizer.exe` (bulk with an fsctl
fallback). That idea is dropped; the two NTFS tools stay separate and explicit.

## Scope

Supported: Windows, read-only, no elevation, any local or network directory as the root
(`C:\`, `E:\`, `\\server\share`, `C:\Users`), folder-size analysis, largest directories and files,
JSON output.

Out of scope for this version: `--elevate`, `--unique-files` (deduplication by file ID),
allocated/physical size, alternate data stream reporting, following reparse points, automatic
worker-count selection by device type. (The alternative enumeration APIs
`GetFileInformationByHandleEx` and `NtQueryDirectoryFileEx` were out of scope of the first version;
they are compared in "Enumerator comparison (P4)".)

## Measurement semantics

- **Size = logical size**: the file size the directory enumeration returns (`nFileSizeHigh/Low` of
  `WIN32_FIND_DATAW`, i.e. end-of-file). Allocated size is deferred for the same reason as in the
  NTFS tools: its meaning differs per filesystem.
- A directory's size is the sum of the files below it. Directories themselves contribute nothing.
- **Directory-entry accounting**: every directory entry is counted where it appears. A hard-linked
  file that has two names in two directories contributes its size to both, and to the root twice.
  This is path-level usage, not physical usage. (The NTFS tools count such a file once.)
- **Reparse points**: a directory with `FILE_ATTRIBUTE_REPARSE_POINT` (junction, directory symlink,
  mount point, and any other reparse directory) is not entered, contributes 0, and is counted in
  `reparse_skipped`. A reparse *file* is never opened; its enumerated size is used as it is (for
  example a cloud placeholder counts with its logical size, a file symlink with the link's own
  size). The root that the user names is entered even if it is itself a reparse point.
- Alternate data streams are not included (enumeration reports only the unnamed stream's size).
- The result is a snapshot of a tree that may change while it is scanned; nothing is locked.

## Enumeration

Per directory: `FindFirstFileExW(path + "\*", FindExInfoBasic, &data, FindExSearchNameMatch, null,
FIND_FIRST_EX_LARGE_FETCH)`, then `FindNextFileW` until `ERROR_NO_MORE_FILES`, then `FindClose`.

- `FindExInfoBasic` skips the 8.3 short-name lookup; `LARGE_FETCH` asks for a larger query buffer.
  If `FindFirstFileExW` fails with `ERROR_INVALID_PARAMETER` while `LARGE_FETCH` is set (some
  network filesystems), retry once without it and keep it off for the rest of the run; the state is
  shown in `--benchmark`. The flag is one shared flag read by all workers, so several workers that
  fail at the same moment may each retry once. A worker can also have one call in flight that read
  the flag just before another worker turned it off, so the number of failing calls is bounded by
  the number of workers, not by 1; a single worker fails exactly once. The same `ERROR_INVALID_PARAMETER` on a call made *without* `LARGE_FETCH`
  is an ordinary failure (`directories_failed`), not a reason to retry.
- The "find first" call sits behind a single delegate (a parameter of the walker, defaulting to the
  real `FindFirstFileExW` call) so that the self-test can inject failures and observe the flags of
  every call. This is a test seam for one call, not an enumerator abstraction (the abstraction was
  introduced when a second implementation was added; see "Enumerator comparison (P4)").
- Entries `.` and `..` are skipped. No file or directory handle is opened for any entry. The number
  of open calls is one `FindFirstFileExW` per directory, never per file.
- Paths use the extended-length form (`\\?\C:\...`, `\\?\UNC\server\share\...`), built from
  `Path.GetFullPath(root)`, so long paths work whatever the `LongPathsEnabled` setting is. Output
  paths never show the prefix.
- Two strings are created per *directory*: its path, and the short-lived search pattern (`path\*`)
  that the API needs. Nothing is created per file except a name string for the few files that enter a
  top-N heap.
- P/Invoke uses `DllImport` with a blittable `WIN32_FIND_DATAW` whose `cFileName` is an
  `[InlineArray(260)]` of `ushort` (read as `char` through a span; a `char` field would make the marshaller depend on the struct's `CharSet`), so the project keeps `AllowUnsafeBlocks=false` and the file name is
  read as a span with no per-entry allocation. (To be confirmed as the first step of the
  implementation, in the NativeAOT build; if it does not hold, the fallback is `AllowUnsafeBlocks`
  for this project only.)

## Parallel walk

- **N dedicated worker threads**, default `min(Environment.ProcessorCount, 8)`, override
  `--workers N`. "Bounded" refers to the number of workers. The task queue is not capacity-bounded,
  because a worker is also the producer of new tasks and would deadlock on a full queue.
- **Shared work stack**: a `Stack<(DirNode Node, string Path)>` guarded by one lock. The path string
  lives only in the queue entry, is dropped when the directory has been enumerated, and is not kept
  in the node. A directory's enumeration costs microseconds to milliseconds of I/O, so one lock per
  directory is negligible; this is to be confirmed by the worker-scaling measurement below, not
  assumed.
- **The stack is intentionally unbounded.** LIFO order makes the walk depth-first, which keeps the
  queue short for depth-heavy trees, but it does **not** bound the queue: a directory with 500,000
  sub-directories puts 500,000 tasks on the stack at once. The peak number of queued directories
  (`peak_queued_dirs`, sampled where tasks are pushed) and the peak working set are measured in the
  wide fan-out test and in `--benchmark`. Work stealing or a spill strategy is added only if that
  measurement shows a real problem.
- **Termination**: a `pending` counter (queued + being processed). Pushing the sub-directories of a
  directory increments it *before* that directory is completed; completing a directory decrements it;
  when it reaches 0 every waiting worker is woken and exits. Idle workers wait on the lock's monitor.
- Cancellation: Ctrl+C sets a flag that the workers check between directories; the tool prints
  `error: canceled` and exits 1 without a result. This applies while the scan runs. Once the scan is
  over nothing observes the flag any more, so Ctrl+C keeps its usual meaning (it ends the process),
  which lets a long output be stopped.
- The main thread starts the workers, shows progress, and joins them. After the join it is the only
  thread touching the model.

The default of `min(ProcessorCount, 8)` is a starting value. The implementation must measure 1, 2, 4
and 8 workers on the local test volume(s) and record the results in the roadmap before the default is
considered decided. A wrong default is easy to override (`--workers`); it is not a correctness issue.

## Model and aggregation

```text
DirNode
    int    Id           dense, 0 = the root
    int    ParentId     (root: -1)
    string Name         (root: the root path as displayed, after normalisation)
    long   OwnFileSize  files directly in this directory
    long   Total        own + everything below; filled by aggregation
```

- **Ids**: the worker that finds a sub-directory allocates its id with `Interlocked.Increment` and
  creates the node (with `ParentId` = the id of the directory being enumerated). The worker that
  later enumerates the node writes its `OwnFileSize`; that is the only writer.
- **Invariant: `ParentId < Id` for every node.** A parent's id exists before any child is found, so
  this holds under any scheduling; it does not depend on the order in which directories are found.
  It is the only property the aggregation relies on, and a violation throws (an internal bug, not a
  data problem).
- Each worker appends the nodes it created to its own list. After the join the lists are combined
  into one array indexed by id.
- **Aggregation**, after all workers finished: `Total[i] = OwnFileSize[i]`, then for `i = n-1 down to
  1`: `Total[ParentId[i]] += Total[i]`. Linear, iterative, no queue, no recursion, no path strings.
- **Files** are not stored. With `--files`, each worker keeps a bounded min-heap of the N largest
  files (`DirId`, name, size), considered only when the size exceeds the heap's current minimum and
  is greater than 0; the name string is created only for entries that enter the heap. The heaps are
  merged at the end.
- **Directories**: top-N from the node array (the root is included, like the NTFS tools).
- **Root children**: the direct sub-directories of the root (nodes with `ParentId == 0`) and the
  files directly in the root, merged into **one bounded top-N selection** (N = `--top`), largest
  first. The root's direct files go through their own bounded min-heap (size N, files larger than 0)
  filled by the worker that enumerates the root; it is always active, because `root_children` is
  always produced, whereas the per-worker file heaps (see **Files** above) exist only with `--files`. No file
  entries are retained beyond a heap, not even for the root: pointing the tool at a directory with
  millions of files must not retain them. (The NTFS tools list all direct children before the top-N
  filter; this tool limits `root_children` to `--top`, a deliberate difference.) Files of size 0 are
  never root children, while directories with a total of 0 can be.
- Paths are built at output time, only for the selected results, by walking `ParentId` up to the
  root. Reparse directories that were skipped are not nodes.
- Order among entries of equal size is unspecified and can differ between runs and worker counts;
  which of several equal-size entries fills the last top-N slot can differ too.

## Errors

| Situation | Behaviour |
| --- | --- |
| Root does not exist / is not a directory | `error: ...`, exit 1 |
| Root cannot be enumerated (access denied, etc.) | `error: ...`, exit 1 |
| `FindFirstFileExW` fails with `ERROR_FILE_NOT_FOUND` | an empty directory, not an error: the root of a FAT or exFAT volume has no `.` or `..` entries, so listing an empty one fails this way (a path that does not exist gives `ERROR_PATH_NOT_FOUND`, which is a failure) |
| A directory returns `ERROR_ACCESS_DENIED` | skipped, `directories_denied++` |
| Any other failure of `FindFirstFileExW` on a directory (path vanished, sharing violation, network error) | skipped, `directories_failed++`, up to 20 error samples kept |
| `FindNextFileW` fails with anything but `ERROR_NO_MORE_FILES` | the directory keeps the entries read so far and is counted in `directories_failed` |
| Unknown option, bad number | `error: ...`, exit 1 |

A denied directory contributes nothing, and a directory that failed part-way contributes only what
was read before it failed, so the totals are then a lower bound; a warning line
says so on stderr and the counters are always printed. **Exit codes**: 0 = result produced; 1 =
error, no result; 3 = only with `--strict`: the result was produced but at least one directory was
denied or failed (same meaning as `dirsizer-bulk`'s exit 3: complete output, not a complete scan).
Without `--strict`, denied directories do not change the exit code, because on a normal drive they
are expected without elevation.

## CLI

```text
dirsizer <path> [--top=N] [--files] [--dirs] [--json] [--workers N] [--strict]
                [--benchmark] [--self-test] [-h]
```

`--top`, `--files`, `--dirs`, `--json`, `--benchmark`, `--self-test` behave as in the NTFS tools
(default `--top=25`, directories shown by default). `--workers N` and `--strict` are new. There is no
reader option and nothing about enumeration internals is exposed. One path only.

The path is normalised before use: `C:` means the drive root `C:\` (as in the NTFS tools), `.`, `..`
and forward slashes are resolved, and an extended-length argument (`\\?\C:\...`, `\\?\UNC\...`) is
first reduced to its ordinary form, because the API takes `\\?\` paths literally and would not resolve
them. A volume with no drive letter can be named as `\\?\Volume{guid}\` (kept as typed). Device paths
(`\\.\...`, `\\?\GLOBALROOT\...`), a network path without a share, and a path containing a quote,
`<`, `>`, `|` or a control character are rejected with an error; the quote case says that a trailing
backslash before a closing quote escapes it (`"C:\dir\"` reaches the program as `C:\dir"`).

The executable has its own manifest with `requestedExecutionLevel level="asInvoker"`. It does not
use `ConsolePause` (that exists only because elevated consoles vanish on exit). It does not use
`Shared\app.manifest`, and none of the existing projects change.

## Output

Text: the same two sections as the NTFS tools (directories, and files with `--files`), followed by a
summary line: `directories_scanned`, `directories_denied`, `directories_failed`, `reparse_skipped`,
`files`, `bytes`. Progress is one updating line on stderr (`Scanning: N directories, M files`, no
percentage because the total is unknown), shown only if stderr is not redirected.

JSON (snake_case, same layout as the NTFS tools where the meaning is the same):

```json
{
  "volume": "C:\\Users",
  "top": 25,
  "size_mode": "logical",
  "root": { "path": "C:\\Users", "size": 123456789 },
  "root_children": [ { "path": "C:\\Users\\foo", "size": 987654321 } ],
  "directories": [ ],
  "files": [ ],
  "statistics": {
    "directories_scanned": 0, "directories_denied": 0, "directories_failed": 0,
    "reparse_skipped": 0, "directories": 0, "files": 0, "bytes": 0,
    "error_samples": [ ],
    "performance": { }
  },
  "reader": "win32-find"
}
```

`performance` is always present. Its keys, all numbers except `large_fetch` (a JSON boolean):
`open_ms`, `walk_ms`, `aggregation_ms`, `finalize_ms`, `other_ms`, `total_ms` and `phase_sum_ms`
(milliseconds; the phases add up to `total_ms`), `enum_ms_total` and `idle_ms_total` (milliseconds
summed over all workers), `workers`, `peak_queued_dirs`, `managed_allocated_bytes`,
`peak_working_set_bytes`, `entries_per_sec`, `directories_per_sec`, `logical_mib_per_sec`.

Differences from the NTFS tools' JSON: the top-level layout is the same, so a consumer that reads
`volume`, `root`, `root_children`, `directories`, `files`, `statistics.directories`, `statistics.files`
and `reader` works with both. There are no `records_*` counters and no `bulk` object; `statistics`
has `directories_scanned`, `directories_denied`, `directories_failed`, `reparse_skipped`, `bytes` and
`error_samples` instead; `performance` has the keys above instead of the query, parser and
relationship timings, and its rates are named `*_per_sec` (the NTFS tools use `*_per_second`), so
`scripts\Get-ReferenceSnapshot.ps1`, which removes the volatile fields of the NTFS tools by name, does
not apply to this tool. The JSON text is ASCII only (non-ASCII characters in paths are escaped as
`\uXXXX`), so it is exact under any console code page; the table output follows the console code page,
so use `--json` when paths must be exact. The numbers of the `--benchmark` line on stderr have no
thousands separators, so that the line can be split on `,` and `=`.

`error_samples` holds up to 20 messages for directories that failed for a reason other than access
denied (denied directories are only counted). If more directories failed than there are samples, the
warning on stderr says how many are not listed. `root_children` holds at most `top` entries
(directories and files mixed, largest first). `volume`
is the root path as given after normalisation (it is not necessarily a drive). `directories`
in `statistics` is the number of directory nodes found (including the root and any that were denied
or failed); `bytes` equals `root.size`.

## Performance measurement

Phases are wall-clock and exhaustive, with the same rule as the NTFS tools: `open` (root check),
`walk` (all workers, first task to last), `aggregation`, `finalize` (top-N selection, path building),
`other` (the residual), `total`; `phase_sum == total` holds by construction (`other` is the residual)
and the self-test checks it.

Work happens in parallel inside `walk`, so its parts cannot be added up as wall time. They are
reported separately as **summed worker time**: `enum_ms_total` (reading directories: the
`FindFirstFileExW` / `FindNextFileW` / `FindClose` calls plus the per-entry accounting that runs while
they are open, that is creating nodes and names and updating the heaps; timing each native call
separately would cost more than it measures) and `idle_ms_total` (waiting for a task). These are diagnostics, not
phases. Also reported: `workers`, `large_fetch` (on/off), `peak_queued_dirs`, managed allocation,
peak working set,
`entries_per_sec` and `directories_per_sec` (over `walk`), and `logical_mib_per_sec`
(`bytes` over `walk`). There is no `query_qps`.

## Testing

Conventions of the repo apply: `--self-test` needs no elevation and no volume, and the important
tests are shown to fail when the code is deliberately broken (mutation check).

Self-test (uses a temporary directory that is removed afterwards):

1. **Tree oracle**: a fixture with nested and empty directories, zero-byte files, Unicode names, a
   path longer than 260 characters, and files of distinct sizes. Every directory total and the root
   total equal an independent computation with `Directory.EnumerateFiles` and `FileInfo.Length`.
   Run with 1 worker and with 8 workers; the results must be identical (ties aside; the fixture uses
   distinct sizes).
2. **Junction** (`mklink /J`, needs no privilege): not entered, counted in `reparse_skipped`; used as
   the root it is entered. Skipped with a visible message if it cannot be created.
3. **Access denied**: a directory with a deny ACL for the current user is counted in
   `directories_denied`, the rest of the tree is intact. Skipped with a message if it cannot be
   set up.
4. **Aggregation and top-N on synthetic nodes**, no filesystem: roll-up, `ParentId < Id`
   enforcement, top-N bounded heap, merging of per-worker heaps.
5. **Termination and queue size**: a walk of a tree with a single very deep chain and one with a very
   wide fan-out (one directory with 10,000 empty sub-directories) both finish, with
   workers = 1 and workers = 8 (guards the `pending` protocol). The wide case also asserts that
   `peak_queued_dirs` is at least the fan-out (the stack really is unbounded, so the number
   reported in the documentation is true) and records the peak working set.
6. **`LARGE_FETCH` fallback**, with the injected find-first delegate and no real failing filesystem
   needed: the first call *with* `LARGE_FETCH` returns `ERROR_INVALID_PARAMETER`, the retry *without*
   it succeeds. With 1 worker: exactly one failing call, `large_fetch` reports off, and every later
   call in the walk is made without the flag; the walk's totals are correct. With 8 workers: the
   number of failing calls is at most 8, `large_fetch` reports off, and the totals are correct. A
   second case: `ERROR_INVALID_PARAMETER` returned for a call without `LARGE_FETCH` is counted in
   `directories_failed` and is not retried.

7. **A failing directory in the middle of a walk** (find-first injected to fail for one directory with
   1 and with 4 workers): counted in `directories_failed` with an error sample, everything else
   counted, `bytes` still equal to `root.size`. **Cancellation while running** (slow find-first, token
   fired mid-walk) and **a caller that fails** (the progress callback throws): the walk stops promptly
   and no worker is left walking. **Settings**: zero or negative workers are rejected, and a read
   result that was never filled in is not a success.
8. **Each test has a time limit** (120 s) so that a deadlock fails the run instead of hanging it.

Known gap: a `FindNextFileW` failure half-way through a directory is not tested, because the
injection seam covers only the first call.

Real volumes (manual, recorded in the roadmap):

- Compare against an independent recursive PowerShell sum on a quiescent directory tree.
- Compare with `dirsizer-bulk` on the `NTFSTEST` fixture volume: directories without hard links must
  match exactly; the documented differences below must appear where the fixture contains them, and
  nothing else may differ.
- Measure 1/2/4/8 workers on C: (warm and, if possible, cold cache), to set the default.

## Differences from the NTFS tools

Expected, documented, and not bugs. They also mean the existing byte-for-byte reader comparison
(`Compare-Readers.ps1`) does not apply to this tool.

| Case | `dirsizer.exe` | `dirsizer-bulk` / `dirsizer-fsctl` |
| --- | --- | --- |
| Hard link with several names | counted in every directory that holds a name | counted once, via one selected parent |
| NTFS metadata files (`$MFT`, `$LogFile`, `$Bitmap`, ...) | not visible, not counted | counted under `[NTFS metadata]` |
| Directory the caller cannot read (for example `System Volume Information` without elevation) | skipped, counted in `directories_denied` | counted |
| Reparse-point directory (junction, mount point, ...) | not entered, not listed, counted in `reparse_skipped` | listed as a directory of size 0 |
| Deleted records | not applicable | excluded |
| Alternate data streams, sparse and compressed files | unnamed-stream logical size | same |

One behaviour is to be verified with the fixture, not assumed: NTFS may keep a hard-linked file's size
in the *directory entry* of a name that was not used to write the file until that entry is refreshed.
If enumeration then shows a stale size for some hard-linked names, that is a limitation of the method
and will be documented with the measured example.

## Status

Implemented as `src\DirSizer.Fs`; see P5 in [roadmap.md](roadmap.md) for what was verified and what was not.

## Deliverables of this work

1. `src\DirSizer.Fs\` project (`AssemblyName` `dirsizer`, NativeAOT, its own `asInvoker` manifest),
   added to `DirSizer.sln`.
2. `--self-test` as above; real-volume checks recorded in the roadmap.
3. `scripts\release.ps1` publishes and packages the fourth executable.
4. Documentation: this file, roadmap (new phase, the old combined-`dirsizer.exe` item replaced),
   `README.md` and `README-jp.md` (tool table, build table, layout, usage, differences,
   no-elevation note), a pointer from `design.md` and `design_mft.md`, which describe the NTFS tools.
5. The version bump happens at release time, not as part of this work.

## Enumerator comparison (P4)

Everything above describes the first version, whose only enumerator is `FindFirstFileExW`. This
section specifies the next piece of work, done on its own branch: put the enumeration behind a small
contract, add two alternative enumerators, and compare the three on the same workloads. "Lower level,
therefore faster" is a hypothesis, not a fact; only measurements decide.

### The three enumerators and their standing

| | Name | API | Standing |
| --- | --- | --- | --- |
| A | `find` | `FindFirstFileExW` / `FindNextFileW` | **production baseline**: the behaviour of the first version, unchanged |
| B | `handle` | `CreateFileW` on the directory, then `GetFileInformationByHandleEx` | Win32 alternative (needs Windows 8 or later for the classes used) |
| C | `nt` | `CreateFileW` on the directory, then `NtQueryDirectoryFileEx` from `ntdll.dll` | **native / experimental** alternative: a documented WDK Native System Service (Windows 10 version 1709 or later), not a Win32 API |

C is never promoted to the default on speed alone. Even if it wins, whether it becomes the default
and whether it stays only as an experimental option are two separate decisions; a capability matrix
records the standing of each enumerator per filesystem.

### The contract

```text
IEntrySink.OnEntry(uint attributes, long size, ReadOnlySpan<char> name)
IDirectoryEnumerator.Read(string directoryPath, IEntrySink sink) -> ReadResult
```

- The sink sees only what the walker needs: the file attributes (the walker looks at
  `FILE_ATTRIBUTE_DIRECTORY` and `FILE_ATTRIBUTE_REPARSE_POINT`; it never needs the reparse tag), the
  logical size (end of file) and the name. Everything an API can additionally return (reparse tag,
  file id, allocation size, times, short name) stays inside the enumerator and is not passed on.
- `.` and `..` are skipped by the enumerator, never passed to the sink.
- `ReadResult` and `ReadOutcome` (`NotRead`, `Complete`, `Denied`, `Failed`, with the error code) are
  unchanged, and so are the rules of the "Errors" section, except where "Handle-based enumerators"
  below says otherwise.
- One enumerator instance belongs to one worker and owns that worker's buffers, so nothing is
  allocated per directory or per entry. State that all workers share (the `LARGE_FETCH` switch of
  `find`) lives in a shared object handed to every instance. The walker receives a factory. Each
  enumerator has a canonical name (for example `find`, `handle:idextd:64`) that is reported in the
  benchmark line and in `performance.enumerator` of the JSON output; for `find` the report also says
  whether `LARGE_FETCH` is on.
- The test seam of `find` (the injectable find-first delegate) stays inside `find`.

**Step 1 changes no behaviour.** The existing `DirectoryReader` becomes the `find` enumerator behind
the contract; the walker, the tests and the output are adapted to the neutral sink. Acceptance: the
JSON output of the new build on fixed quiescent trees (`T:\`, `C:\Program Files\dotnet`, with a large
`--top`), with the timing and memory fields removed, is identical to that of the build before the
change, and every existing self-test still passes.

### Handle-based enumerators (B and C)

- **Opening.** One `CreateFileW` per directory, on the extended-length path, with `FILE_LIST_DIRECTORY`,
  `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, `OPEN_EXISTING` and
  `FILE_FLAG_BACKUP_SEMANTICS`, and `CloseHandle` when the directory is done. No file is ever opened;
  the principle "no per-file open" holds, but there is now one more open per directory than with `find`,
  which is exactly what the workloads below are chosen to expose.
- **Errors at the open.** `ERROR_ACCESS_DENIED` is `Denied`. Any other error, **including
  `ERROR_FILE_NOT_FOUND` and `ERROR_PATH_NOT_FOUND`, is `Failed`**: for `find`, `ERROR_FILE_NOT_FOUND`
  means "no entries" (an empty FAT/exFAT root), but here it would mean that the directory does not
  exist. An empty directory is an open that succeeds followed by an immediate end of enumeration:
  `STATUS_NO_MORE_FILES` or, when the very first query finds no entry at all, `STATUS_NO_SUCH_FILE`
  (B sees it as `ERROR_FILE_NOT_FOUND`); on the first query both mean `Complete`. An empty directory
  on the exFAT volume `D:` was scanned and gave `Complete` for all three enumerators, so the second
  form was not observed there; it is handled because the native API documents it.
- **Buffer.** One per-worker `byte[]` (default 64 KiB, configurable, at least 4 KiB). Each query asks
  for as many entries as fit in the buffer. Entries are read by following `NextEntryOffset` (0 marks
  the last one), and the name is `FileNameLength` bytes of UTF-16 at the end of the entry.
- **Entry layouts** (same for the Win32 and the native structures): all three used classes start with
  `NextEntryOffset` (0), `FileIndex` (4), four times (8-39), `EndOfFile` (40), `AllocationSize` (48),
  `FileAttributes` (56) and `FileNameLength` (60). The name starts at offset 64 for `FileDirectoryInformation`,
  at 68 for `FileFullDirectoryInformation` (an `EaSize` at 64), and at 88 for
  `FileIdExtdDirectoryInformation` (`EaSize` at 64, `ReparsePointTag` at 68, a 128-bit `FileId` at 72).
  The conformance tests are what proves these offsets.
- Both APIs return data from the same directory index as `FindNextFileW`, so identical sizes and
  attributes are expected; the tests and the comparison scripts verify that instead of assuming it.

### B: `GetFileInformationByHandleEx`

A state machine of two classes per variant: the first call of a directory uses the **Restart** class,
every later call the plain class, until the call fails with `ERROR_NO_MORE_FILES`.

| Variant | First call | Later calls |
| --- | --- | --- |
| `idextd` | `FileIdExtdDirectoryRestartInfo` | `FileIdExtdDirectoryInfo` |
| `full` | `FileFullDirectoryRestartInfo` | `FileFullDirectoryInfo` |

A class that the filesystem or the Windows version does not support makes the call fail (typically
`ERROR_INVALID_PARAMETER`); the directory is then `Failed` with that error, and the failure is recorded
in the capability matrix. There is no automatic fallback while comparing.

### C: `NtQueryDirectoryFileEx`

- The first query of a directory has `QueryFlags = SL_RESTART_SCAN` (0x1); every later query has
  `QueryFlags = 0`. **`SL_RETURN_SINGLE_ENTRY` (0x2) is never used**: it makes the file system return one
  entry per query, which is inefficient. Each query asks for as many entries as the buffer holds.
  `SL_NO_CURSOR_UPDATE_QUERY` is not used either: a handle is used by one worker only. The file name
  argument is null (all entries).
- Variants: `dir` = `FileDirectoryInformation` (1), `full` = `FileFullDirectoryInformation` (2),
  `idextd` = `FileIdExtdDirectoryInformation` (60).
- The end of a directory is the status `STATUS_NO_MORE_FILES` (0x80000006). **A later query that
  succeeds but returns no entry (`IoStatusBlock.Information` is 0) means that the buffer is too small,
  not that the directory is finished**; the directory is `Failed` with `ERROR_INSUFFICIENT_BUFFER`
  (the buffer floor of 4 KiB makes this impossible for ordinary names). Any other failing NTSTATUS
  (`STATUS_BUFFER_OVERFLOW` and `STATUS_BUFFER_TOO_SMALL` included) makes the directory `Failed`, with
  the Win32 error that `RtlNtStatusToDosError` gives for the error code and message.
- Because C is a native API, it is only ever selected explicitly (`--enumerator=nt...`); a failure of
  the `ntdll.dll` entry point itself (for example on a Windows before 1709) is reported as an error,
  not hidden.

### Selecting an enumerator

`--enumerator=<name>[:<class>[:<KiB>]]`, an advanced option for diagnostics and comparison, like
`--workers`; the default is `find`.

| Value | Meaning |
| --- | --- |
| `find` | `FindFirstFileExW` with `LARGE_FETCH` (the default) |
| `find:nolarge` | the same without `LARGE_FETCH` (a comparison point) |
| `handle`, `handle:idextd`, `handle:full`, `handle:full:256` | B; classes `full` (default) and `idextd`; default buffer 64 KiB |
| `nt`, `nt:dir`, `nt:full`, `nt:idextd:256` | C; classes `dir` (default), `full` and `idextd`; default buffer 64 KiB |

The default class is the lightest one that works on every file system that was available (NTFS and
exFAT): **`idextd` is not supported on exFAT** and fails there with `ERROR_INVALID_PARAMETER`, for B
and for C alike (measured on `D:`, see the capability matrix in the results).

An unknown name or class, or a buffer below 4 KiB or above 1024 KiB, is an option error (exit 1).
The canonical form is what is reported (`performance.enumerator`, the `--benchmark` line).

### Correctness

- **Conformance tests, per enumerator** (`find`, `find:nolarge` and a set of B and C variants,
  including a 4 KiB buffer): the same standard tree as the walk tests, compared with the framework's
  own enumeration: names, attributes and sizes of every entry (Unicode names, a path over 260
  characters, hidden and system files, a junction whose entry carries the reparse attribute), an empty
  directory, a directory that does not exist (`Failed`), a directory that cannot be read (`Denied`,
  skipped where the shell ignores the deny ACL), and a directory with enough entries to need several
  buffer refills at the small buffer (the boundary where a parser goes wrong).
- **The whole walk**, every enumerator with 1, 3 and 8 workers, against the same independent oracle
  as the walk tests.
- **Real volumes**, by script (`scripts\Compare-Enumerators.ps1 -Equal`): the same comparison of the
  canonical snapshots (`scripts\Get-FsSnapshot.ps1`) of A, B and C on `T:\` (NTFS), `D:\` (exFAT), static
  trees on `C:\` and the two synthetic trees. Everything must be identical, not only the root size: every
  directory and its size, and the counters. A variant that does not work on a file system is not
  hidden: it is reported as failed with the error it returned, and goes into the capability matrix.

### Measurement

- The enumerator is the only variable. The workers are **fixed at 8** for the comparison; a worker
  sweep (1, 2, 4, 8) is done afterwards, for the winner only.
- Workloads: **W1** the real `C:\` (about 228,000 directories, 786,000 files); **W2** a synthetic tree of
  many small directories (20,000 directories with 3 files each, 60,000 files); **W3** a synthetic tree of
  few large directories (10 directories with 20,000 files each, 200,000 files). They separate the cost
  per directory (one more open for B and C) from the cost per entry. The synthetic trees are created by
  `scripts\New-EnumFixture.ps1` under `%TEMP%` (deterministic names and sizes) and removed afterwards.
  W2 and W3 run for tens to hundreds of milliseconds, so their numbers are noisy and only show a
  direction; W1 decides.
- Method: warm file cache, the enumerators alternated in each round (the order rotated), the median.
  **Primary metric: `walk_ms` (wall clock).** Diagnostics reported next to it: `enum_ms_total` (summed
  worker time, not the elapsed time), `entries_per_sec`, `idle_ms_total`, managed allocation, peak
  working set. Cold cache is not measured in this work. `C:` is a live volume, so the byte totals of two
  runs differ slightly; equality is checked on quiescent trees only.
- Order: **screening** of every variant (A with and without `LARGE_FETCH`; B and C in each class, at 4,
  64 and 1024 KiB) with 2 rounds; the best variant of A, B and C **and the reference `find`** then get 5
  rounds on W1, W2 and W3 (the finalists); then the worker sweep (1, 2, 4, 8) for the winner only.
- Recorded per run: the resolved enumerator, `large_fetch` for `find`, the class and buffer for B and C.

### Adoption rule

An alternative is a **candidate for the default** only if all of this holds:

1. **Correctness:** its output is identical to A's on every volume and fixture above.
2. **Primary performance:** its median `walk_ms` is at least 10 % lower than A's on W1.
3. **Diagnostic:** `enum_ms_total` is reported next to it (it explains a difference, it does not decide it).

W2 and W3 are reported, and a candidate that is clearly slower than A on a workload class is
flagged in the recommendation. B, if it is a candidate, becomes the default only together with a
defined fallback to A (an unsupported class or a failure at the first directory switches the run to A
and says so), implemented as the last step. C is never made the default by this rule alone (see
"Standing"). If nothing qualifies, A stays the default. Either way the full result table and the
capability matrix (which enumerator worked on NTFS and exFAT here; ReFS and network shares were not
available and stay unverified) are written to `roadmap.md` and to this file.

### Deliverables of the enumerator work

1. The contract, `find` behind it (Step 1, behaviour unchanged), the snapshot script and its result.
2. B, then C, each with its conformance and walk tests, and a native-AOT run of the self-tests after the
   first `ntdll.dll` import.
3. `--enumerator`, the reporting, `--help` and README text.
4. `scripts\Compare-Enumerators.ps1` (snapshot equality across enumerators and volumes, and the timed
   comparison) and `scripts\New-EnumFixture.ps1`.
5. The results, the adoption decision and the capability matrix.

### Not part of this work

Cold-cache measurement, ReFS and network shares (not available), other operating systems, asynchronous
or overlapped directory queries, several queries in flight on one handle, and any change to the
aggregation or the output.
