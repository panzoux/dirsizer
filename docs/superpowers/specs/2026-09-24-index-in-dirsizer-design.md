# dirsizer.exe: using the persistent index (after I5)

Status: **proposal, not started.** Decisions marked **(decide)** need the user's answer before the task-level
implementation plan is written.

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

## Options

**A. Automatic.** Every elevated NTFS run creates and keeps an index; the user never knows.
Simplest to use, but a silent 112 MiB write that holds every name on the volume, the first run about 40 % slower,
and the "read-only" description gone. Not recommended.

**B. Opt in once per drive, then automatic (recommended).** One behaviour option (it names what the user wants,
not an implementation, so it fits the unified spec's rule):

```
dirsizer C:\ --remember     # scan as today, and keep an index of C: for later runs
dirsizer C:\Users\me        # from now on: answered from the index (elevated), kept up to date
dirsizer C:\ --changes      # what shrank and grew since the previous run
dirsizer C:\ --forget       # delete the index of C:
```

Without `--remember` nothing is ever written, and behaviour stays exactly as in v0.7.0. Once a drive is
remembered, every elevated run on that drive, root or subtree, uses the index. **(decide: option names;
`--remember`/`--forget` proposed, alternatives `--keep-index`/`--drop-index`.)**

**C. Keep them separate.** `dirsizer.exe` stays stateless; `dirsizer-index.exe` stays the tool for repeated
analysis. No work, but the fastest path and `--changes` stay in an experimental side tool. This is the fallback if
B's validation gates (below) are not met.

## Design of option B

### A new strategy: `index`

`IndexStrategy : IScanStrategy`, ahead of the others in `ScanStrategySelector`:

| Path | Candidates (in order) |
| --- | --- |
| NTFS drive root | `index`, `mft`, `fsctl`, `filesystem` |
| NTFS subtree | `index`, `filesystem` (new: today only `filesystem`) |
| anything else | `filesystem` |

`IndexStrategy` throws `StrategyUnavailableException`, so the selector moves on, when:
- the volume cannot be opened (no administrator token): the same probe as `MftStrategy`;
- there is no index for this volume and `--remember` was not given.

With an index, or with `--remember`, everything after that is the existing `dirsizer-index` code path
(`IndexRunner`): load, validate, update from the journal, or a full scan with the reason (that full scan is inside
the strategy and is not a strategy fallback). A real error while doing so is an error, not a reason to try the
next strategy (unified spec, "availability/failure boundary").

**Subtree answers change meaning.** Today a subtree is walked, which has `dirsizer-fs` semantics; from the index it
has the NTFS tools' semantics (hard links, metadata files, reparse points: the differences listed in
design_fs.md). This affects only remembered drives with an administrator token, and it is already the
semantics of `dirsizer.exe C:\` on such a drive. **(decide: accept it for subtrees, recommended, because root
and subtree answers then agree with each other and `--changes` works for subtrees; or keep walking subtrees and
use the index only for the drive root.)** A small folder answered from the index takes about 2 s instead of
0.2 s; the recommendation accepts that rather than adding a size guess.

### Options

- `--remember`: create the index if there is none (full scan plus save), then as above. Needs an administrator
  token; without one: `error: --remember needs an administrator terminal`, exit 1, nothing written.
- `--forget`: delete the index file (and `.delta`) of the path's volume, print what was deleted, exit 0 (also
  when there was none). The file name uses the 64-bit NTFS serial, which needs the volume handle; without
  elevation, the low 32 bits from `GetVolumeInformation` are matched against the file names instead (the
  files are the user's own, so no elevation is needed to delete them).
- `--changes`: as in `dirsizer-index`. When there is nothing to compare with (not remembered, not elevated, or a
  first run), the normal result is printed with one stderr note, exit 0: never an error, because whether the
  index could be used depends on state the user does not see (the unified spec's rule for `--workers`).
- Not added: `--verify`, `--rebuild`, `--no-save`, `--index-dir`. They are development and diagnosis options and
  stay on `dirsizer-index.exe`, the dedicated tool for this strategy, as `--enumerator` stays on
  `dirsizer-fs.exe`.

### Output

- `strategy` gains the value `"index"`. `--benchmark` adds `index_mode=full|incremental` and the reason for a
  full scan.
- JSON gains `changes` (the same shape as `dirsizer-index`) only with `--changes`; otherwise the schema is
  unchanged.
- A drive that is remembered but used without elevation falls through to `filesystem` with
  `strategy_fallback_reason` set, as for MFT today; the index is simply not updated on that run.

### Things the index code must handle before it runs inside dirsizer.exe

1. **Two processes at once.** Two `dirsizer.exe` runs (or `dirsizer.exe` and `dirsizer-index.exe`) on the same
   volume can both update and save the index. Proposed: an exclusive lock file next to the index for the
   load-update-save sequence. A second process waits up to a few seconds, then answers without the index
   (the next strategy) and prints a stderr note. Needs a test with two real processes on T:.
2. **The index on the drive it describes.** Saving the index changes the volume, and the next run then sees its
   own file in the journal and in `--changes` (seen in the I5 measurement: a new 118 MB folder in round 1). Leave
   out the index directory's own entries from `--changes` and document the rest.
3. **Disk space.** The first `--remember` on `C:` writes about 112 MiB. The run says so on stderr the first time.
4. **Library split.** `dirsizer.exe` must compile the index code without its `Program`/options/output:
   `src\DirSizer.Index.Core` (library) plus a thin `dirsizer-index.exe`, as `DirSizer.Fs.Core` did for
   `dirsizer-fs`. Zero behaviour change, checked with the same 41 self-tests and `Test-IndexIncremental.ps1`.

### Validation gates (all before it ships)

- `dirsizer.exe` with `strategy=index` equals `dirsizer-mft.exe` on T: (root, every directory, top files;
  `Compare-Readers.ps1` extended with a third reader), full and incremental.
- Subtrees from `dirsizer.exe` equal `dirsizer-index.exe` on T:, and differ from `dirsizer-fs` only by the
  documented kinds.
- Unchanged behaviour without `--remember`: the v0.7.0 `dirsizer.exe` output and exit codes, byte for byte, on T:
  (root and a subtree) and `C:\`, elevated and not; and nothing written to `%LOCALAPPDATA%\dirsizer`.
- Non-elevated run on a remembered drive: falls through to `filesystem`, index file untouched (the Explorer
  launch trick from P5 gives a standard token).
- Two processes at once (item 1 above).
- `C:` timings: first `--remember` run, later runs, `--changes`; compared with the MFT strategy.

## Phases

1. `DirSizer.Index.Core` library split and the lock file (inside `dirsizer-index.exe` first; no `dirsizer.exe`
   change). Gate: 41 self-tests, `Test-IndexIncremental.ps1`, a two-process test.
2. `IndexStrategy` for the drive root and `--remember`/`--forget`. Gate: the equality and unchanged-behaviour
   checks above.
3. Subtrees through the index (if accepted) and `--changes`. Gate: the subtree checks, `C:` timings.
4. Docs (README, unified spec addendum, roadmap), release as 0.8.0. `dirsizer-index.exe` stays as the dedicated
   tool for this strategy.

## Not part of this

Automatic elevation; ReFS or USN journals on non-NTFS volumes; an index for network shares; I6 (search,
listing); making the index format smaller or its checksum faster (design_index.md "Loading fast"); storing
directory sizes in the index to make `--changes` cheaper (roadmap I5 follow-up).
