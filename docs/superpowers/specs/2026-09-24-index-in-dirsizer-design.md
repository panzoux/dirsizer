# dirsizer.exe: using the persistent index (after I5)

Status: **accepted 2026-09-24, not implemented.** Implementation plan:
[2026-09-24-index-in-dirsizer.md](../plans/2026-09-24-index-in-dirsizer.md).

Decisions made by the user (2026-09-24):
1. Opt in once per drive, then automatic (option B below).
2. Option names: `--keep-index` and `--drop-index`.
3. On NTFS, subtrees are answered from the index.

## Where things stand (v0.7.0)

- `dirsizer.exe` (unified entry point, [2026-09-23-unified-scan-strategy-design.md](2026-09-23-unified-scan-strategy-design.md)):
  `asInvoker`, read-only, no option names an implementation. Drive root on NTFS with an administrator token:
  MFT, then FSCTL, then the directory walk. Any other path: always the directory walk.
- `dirsizer-index.exe` (experimental, [design_index.md](../../design_index.md)): per-volume index file, updated from
  the USN journal, any directory on the volume, `--changes`. `requireAdministrator`. Measured on `C:` (one
  machine, warm): incremental run about 1.9 s against about 7.8 s for a full run; after deleting 30,000 files
  2.8 s (3.8 s with `--changes`).
- Directory walk of subtrees of `C:` (`dirsizer-fs`, warm, wall clock, 2026-09-24): a 454-directory repository
  0.2 s; `AppData\Local` (18,000 directories) 4.6 s; `C:\Users` (54,000) 5.1 s; `C:\Windows` (141,000) 9.7 s.
  Loading and updating the index costs about 1.9 s whatever the subtree, so the index is faster from roughly
  10,000 directories up and slower for small folders.

## What the index can add to dirsizer.exe

1. Faster repeated runs on the same NTFS drive: about 2 s instead of 5-8 s for `C:\`.
2. Fast subtree answers on NTFS (today a subtree is always walked: 5-10 s for large ones).
3. "What changed since last time" (`--changes`): the cleanup loop of roadmap I5. Only possible with state.

## The conflict to resolve

`dirsizer.exe` is described as read-only and never writes anything. An index writes about 112 MiB per volume
(for `C:`) to `%LOCALAPPDATA%\dirsizer\index` and adds about 2 s to the first run (the save). The file lists every
name on the volume, including names in folders the user cannot list without elevation. Creating it silently
changes what the tool is.

## Options considered

**A. Automatic.** Every elevated NTFS run creates and keeps an index. A silent 112 MiB write holding every name on
the volume, the first run about 40 % slower, and the "read-only" description gone. Rejected.

**B. Opt in once per drive, then automatic. Chosen.** One behaviour option (it names what the user wants, not an
implementation, so it fits the unified spec's rule):

```
dirsizer C:\ --keep-index    # scan as today, and keep an index of C: for later runs
dirsizer C:\Users\me         # from now on: answered from the index (administrator terminal), kept up to date
dirsizer C:\ --changes       # what shrank and grew since the previous run that used the index
dirsizer C:\ --drop-index    # delete the index of C:
```

Without `--keep-index` nothing is ever written, and behaviour stays exactly as in v0.7.0. Once a drive has an
index, every elevated run on that drive, root or subtree, uses it.

**C. Keep them separate.** No work, but the fastest path and `--changes` stay in an experimental side tool. The
fallback if B's validation gates (below) are not met.

## Design

### A new strategy: `index`

`IndexStrategy : IScanStrategy`, ahead of the others in `ScanStrategySelector`:

| Path | Candidates (in order) |
| --- | --- |
| NTFS local drive root | `index`, `mft`, `fsctl`, `filesystem` |
| NTFS local subtree | `index`, `filesystem` (new: today only `filesystem`) |
| anything else | `filesystem` |

`IndexStrategy` throws `StrategyUnavailableException`, so the selector moves on, when:
- the volume cannot be opened (no administrator token): the same probe as `MftStrategy`;
- there is no index for this volume and `--keep-index` was not given;
- another process holds the index (see "Two processes at once").

Everything after that is the existing `dirsizer-index` code path (`IndexRunner`): load, validate, update from the
journal, or a full scan with the reason (that full scan is inside the strategy, not a strategy fallback). A real
error while doing so is an error, not a reason to try the next strategy (unified spec, "availability/failure
boundary"). The two "no index" and "busy" cases are decided before anything is read or written, which is why they
count as availability.

A drive root is reported as the drive as typed (`C:`), the way the mft and fsctl strategies report it; the index
code itself reports `C:\`.

**Subtree answers change meaning.** Today a subtree is walked, which has `dirsizer-fs` semantics; from the index it
has the NTFS tools' semantics (hard links, metadata files, reparse points: the differences listed in
design_fs.md). This affects only drives with an index, from an administrator terminal, and it is already the
semantics of `dirsizer.exe C:\` on such a drive. Accepted: root and subtree answers then agree with each other, and
`--changes` works for subtrees. A small folder answered from the index takes about 2 s instead of 0.2 s; accepted
rather than adding a size guess.

### Options

- `--keep-index`: create the index if there is none (full scan plus save), then as above. Only on a local NTFS
  drive (`error:`, exit 1, otherwise). Needs an administrator token; without one: `error: --keep-index needs an
  administrator terminal`, exit 1, nothing written.
- `--drop-index`: delete the index files of the path's drive, name each one on stdout, exit 0 (also when there was
  none), and do nothing else; it cannot be combined with `--keep-index` or `--changes`. The file name holds the
  64-bit NTFS serial, which needs a volume handle; the low 32 bits, which Windows gives without elevation
  (`GetFileInformationByHandle`), are the last 8 hex digits of the name and identify the files. The files are the
  user's own, so no elevation is needed to delete them.
- `--changes`: as in `dirsizer-index`. When there is nothing to compare with (no index, not elevated, a first run),
  the normal result is printed with a stderr note, exit 0: never an error, because whether the index could be used
  depends on state the user does not see (the unified spec's rule for `--workers`).
- Not added: `--verify`, `--rebuild`, `--no-save`, `--index-dir`. They are development and diagnosis options and
  stay on `dirsizer-index.exe`, the dedicated tool for this strategy, as `--enumerator` stays on `dirsizer-fs.exe`.

### Output

- `strategy` gains the value `"index"`. `--benchmark` appends `index_mode=full|incremental, index_save=...` when the
  index answered.
- JSON gains `changes` (the same shape as `dirsizer-index`) only with `--changes`; otherwise stdout is byte for byte
  what v0.7.0 prints.
- stderr notes: the reason for a full scan of the index, the first save (path and size, and how to delete it), an
  unstable MFT read (index not saved), and, with `--keep-index` or `--changes`, why the index was not used.
- A drive with an index used without elevation falls through silently, as MFT does today. Note: the unified spec
  and README say `strategy_fallback`/`strategy_fallback_reason` are set on a fallback, but no code sets them
  (checked 2026-09-24). That is a separate, existing gap; this work does not depend on it.

### Things the index code must handle before it runs inside dirsizer.exe

1. **Two processes at once.** Two runs (`dirsizer.exe` or `dirsizer-index.exe`) on the same volume could both update
   and save the index. An exclusive lock file next to the index (`<index>.lock`, deleted when released) is held for
   the whole run; a second process waits up to 5 s, then `dirsizer.exe` answers without the index (next strategy,
   with a note when `--keep-index` or `--changes` was given) and `dirsizer-index.exe` stops with an error.
2. **The index on the drive it describes.** Saving the index changes the volume, so the next run saw its own file
   in `--changes` (the I5 measurement: a new 118 MB folder). The index directory's own change is taken out of the
   report: the directory is not listed, and its change is subtracted from every directory above it.
3. **Disk space.** The first `--keep-index` on `C:` writes about 112 MiB; the run says where and how much.
4. **Sharing the code.** `dirsizer.exe` compiles the index source files with `<Compile Include>`, the way it already
   compiles `BulkIntegration.cs` and the bulk reader. The one obstacle, checked by a trial build: `IndexRunner.Run`
   takes dirsizer-index's `IndexOptions`. It gets its own request record instead.

### Validation gates (all before it ships)

- `dirsizer.exe` with `strategy=index` equals the mft strategy on T: (root, children, directories, files, and the
  `volume` field).
- Subtrees from `dirsizer.exe` equal `dirsizer-index.exe` on T:, and equal the sum of their files on a tree without
  hard links.
- Unchanged behaviour without `--keep-index`: the v0.7.0 `dirsizer.exe` output (stdout, JSON without `total_ms`,
  exit code) on T: (root and a folder, with and without `--files`); and nothing written to `%LOCALAPPDATA%\dirsizer`.
- Standard (non-elevated) token on a drive with an index: falls through to `filesystem`, index files untouched;
  `--keep-index` is an error.
- Two processes at once: a held lock makes `dirsizer.exe` fall back after the wait and `dirsizer-index.exe` fail.
- `C:` timings: first `--keep-index` run, later runs (root and subtrees), `--changes`; compared with the mft
  strategy and the walk. Workstation against server GC for `dirsizer.exe` (dirsizer-index uses server GC for a
  faster load).

## Not part of this

Automatic elevation; ReFS or USN journals on non-NTFS volumes; an index for network shares; I6 (search, listing);
making the index format smaller or its checksum faster (design_index.md "Loading fast"); storing directory sizes
in the index to make `--changes` cheaper (roadmap I5 follow-up); setting `strategy_fallback` (existing gap, above).
