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
worker-count selection by device type, and the alternative enumeration APIs
(`GetFileInformationByHandleEx`, `NtQueryDirectoryFileEx`; see "Later phase").

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
  every call. This is a test seam for one call, not an enumerator abstraction (that waits for a
  second real implementation; see "Later phase").
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
  `error: canceled` and exits 1 without a result.
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
  filter; this tool limits `root_children` to `--top`, a deliberate difference.)
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

A denied or failed directory contributes nothing, so the totals are then a lower bound; a warning line
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

`error_samples` holds up to 20 messages for directories that failed for a reason other than access
denied (denied directories are only counted). `root_children` holds at most `top` entries
(directories and files mixed, largest first). `volume`
is the root path as given after normalisation (it is not necessarily a drive). `directories`
in `statistics` is the number of directory nodes found (including the root and any that were denied
or failed); `bytes` equals `root.size`.

## Performance measurement

Phases are wall-clock and exhaustive, with the same rule as the NTFS tools: `open` (root check),
`walk` (all workers, first task to last), `aggregation`, `finalize` (top-N selection, path building),
`other` (the residual), `total`; the runtime checks `phase_sum == total`.

Work happens in parallel inside `walk`, so its parts cannot be added up as wall time. They are
reported separately as **summed worker time**: `enum_ms_total` (inside `FindFirstFileExW` /
`FindNextFileW` / `FindClose`) and `idle_ms_total` (waiting for a task). These are diagnostics, not
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

## Deliverables of this work

1. `src\DirSizer.Fs\` project (`AssemblyName` `dirsizer`, NativeAOT, its own `asInvoker` manifest),
   added to `DirSizer.sln`.
2. `--self-test` as above; real-volume checks recorded in the roadmap.
3. `scripts\release.ps1` publishes and packages the fourth executable.
4. Documentation: this file, roadmap (new phase, the old combined-`dirsizer.exe` item replaced),
   `README.md` and `README-jp.md` (tool table, build table, layout, usage, differences,
   no-elevation note), a pointer from `design.md` and `design_mft.md`, which describe the NTFS tools.
5. The version bump happens at release time, not as part of this work.

## Later phase (not part of this work)

Benchmark three enumerators on the same fixture and filesystems: A `FindFirstFileExW` (this work),
B `GetFileInformationByHandleEx` with `FileIdExtdDirectoryInfo`, C `NtQueryDirectoryFileEx`. Adopt
only what measures faster on the same workload. "Lower level, therefore faster" is a hypothesis, not a
fact. An enumerator abstraction is introduced then, when there is a second implementation.
