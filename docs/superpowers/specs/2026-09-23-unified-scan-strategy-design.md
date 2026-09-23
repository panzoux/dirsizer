# dirsizer.exe: unified automatic scan strategy

## Relationship to the previous spec

[2026-09-22-fs-enumerator-default-design.md](2026-09-22-fs-enumerator-default-design.md) (the B-with-a-fallback-to-A
design, implemented and merged on `feature/fs-enumerator-default`) is **left as-is**: an accurate historical
record of that branch, not revised here. Nothing in it was wrong for its own scope. This document is the next
layer up, and inverts that document's framing: there, `--enumerator=auto` was the product; here, it becomes one
internal implementation detail of one of three interchangeable strategies, and the product is "the user never
picks anything."

```
feature/fs-enumerator-benchmark    A vs B vs C, behind IDirectoryEnumerator          (done, merged)
feature/fs-enumerator-default      B with a fallback to A, --enumerator=auto         (done, merged)
this document                     dirsizer.exe picks A/B/C's whole scan METHOD      (not started)
```

## Goal

`dirsizer.exe` becomes the single normal entry point for folder-size scanning. Given a path, it automatically
selects the fastest **validated** and **available** scan strategy for that path's file system — the user never
names a backend. Existing result semantics and correctness guarantees are preserved regardless of which strategy
runs. Dedicated executables (one per strategy) remain available, unchanged in behavior, for users who already
know which implementation they want, and for diagnosis, benchmarking, and development — `--enumerator` (and any
future `--strategy`-shaped option) lives on those, as a diagnostic/expert surface, **not** on `dirsizer.exe`.

## Scope of this document, and phasing

One spec, phased implementation (three increments of one feature, not three independent sub-projects — each still
gets its own build-and-test checkpoint before the next starts, the same shape as the benchmark work):

- **Step 1**: rename today's `dirsizer.exe` to `dirsizer-fs.exe` (pure rename, zero behavior change), extract its
  engine into a small library, introduce `IScanStrategy` and a `ScanStrategySelector` with exactly one strategy
  (`FileSystemScanner`) that is always chosen. The new `dirsizer.exe` exists after this step, behaves exactly like
  `dirsizer-fs.exe` (same CLI, same output — see "Open question carried from Step 1" below), and nothing about NTFS
  is touched yet.
- **Step 2**: add `MftScanner` (wraps the existing `dirsizer-bulk` implementation) as a real, selectable strategy.
  `dirsizer-bulk.exe` is renamed to `dirsizer-mft.exe` (naming only, same implementation) as part of this step, not
  before — a bare rename with nothing yet using it would be pointless churn.
- **Step 3**: add `NtfsFsctlScanner` (wraps the existing `dirsizer-fsctl` implementation) as the second real
  strategy, selected when MFT is unavailable.

**Out of scope for all three steps**: `dirsizer-inspect.exe` (stays exactly as it is, a separate diagnostic tool,
not a strategy); automatic elevation; cold-cache/cross-machine measurement; file/directory search (the strategy
boundary is kept suitable for it later, per "Not part of this work", but nothing here builds it).

## User-facing contract

Normal use takes no strategy-related flag at all:

```
dirsizer.exe C:\
dirsizer.exe D:\some\directory
```

The contract is `path → scan → result`, never `path → choose an enumerator`. The selected strategy is diagnostic
metadata (see "Reporting"), never something the user must understand to get a correct answer.

**A non-drive-root path (`D:\some\directory`) always uses `FileSystemScanner`.** `dirsizer-mft.exe` and
`dirsizer-fsctl.exe` only ever accept a whole drive (`DriveRoot.Validate` in `src\Shared\DriveRoot.cs` rejects
anything else today), because their scan is a whole-volume MFT/FSCTL read, not a subtree walk. Extending them to
filter to a subtree is real, separate scope — explicitly not part of this work (see "Not part of this work"). The
selector checks "is the root a drive letter" **before** anything filesystem- or capability-related, and skips
straight to `FileSystemScanner` if it isn't; no NTFS strategy is even probed for a subdirectory path.

**Explicit strategy executables** (all thin hosts over the same library code as `dirsizer.exe` — see "Executable
structure"): `dirsizer-fs.exe` (generic filesystem, `--enumerator` and everything else unchanged from today),
`dirsizer-mft.exe` (NTFS MFT, renamed from `dirsizer-bulk.exe`, Step 2), `dirsizer-fsctl.exe` (NTFS FSCTL, Step 3),
`dirsizer-inspect.exe` (investigation, untouched, not a strategy). A dedicated executable uses its one strategy
directly; it never runs the selector.

**Open question carried from Step 1**, resolved here per this document's own framing: `dirsizer.exe`'s CLI has
**no `--enumerator` and no strategy-selection flag of any kind, in any step**, including Step 1 (superseding
what was tentatively proposed before this correction). If you want to force a specific enumerator or strategy,
you use the matching dedicated executable. `dirsizer.exe`'s own options are exactly the subset that makes sense
without naming an implementation: `--top`, `--files`, `--dirs`, `--json`, `--workers`, `--strict`, `--benchmark`,
`--self-test`.

## `ScanStrategySelector`

Runs once per scan (at the start, not per-directory — this is a coarser decision than the enumerator-level
fallback, and does not need `FirstQuery`-style per-call precision):

```
root is not a drive letter
    → FileSystemScanner

root is a drive letter, GetVolumeInformation says the file system is not NTFS
    → FileSystemScanner

root is an NTFS drive letter:
    try MftScanner
        StrategyUnavailableException  → try NtfsFsctlScanner
            StrategyUnavailableException → FileSystemScanner
    (any other exception, or a completed-but-UNSTABLE result, is returned/thrown as-is: never caught here)
```

The selector needs "is this drive NTFS" starting at **Step 2** (Step 1's selector has only one strategy and never
checks file-system type at all). A small helper extracted from the existing `GetVolumeInformation` P/Invoke
(currently declared inside `FsctlScanner.cs`'s `Native` class, used there only to name the file system in an
error message) — e.g. `static class VolumeInfo { public static bool IsNtfs(string driveRoot); }` — moves into
`DirSizer.Core` **in Step 2**, alongside `StrategyUnavailableException`. This is deliberately *not* the same
step as moving the rest of `FsctlScanner.cs` into `Shared` (that's Step 3, see "Strategies" below) — Step 2 only
needs this one small piece early. `FsctlScanner.cs` keeps its own local `GetVolumeInformation` declaration for
its own error-message use until Step 3, when the whole file moves and that duplication is what Step 3 cleans up
by switching it to call the shared `VolumeInfo.IsNtfs` too.

## The availability/failure boundary

This is the one piece of new logic with real correctness risk, so it is specified precisely, the same way
`FirstQuery` was for the enumerator fallback.

**New type**, in `DirSizer.Core` (already referenced by both `DirSizer.Bulk` and `DirSizer.Fsctl`, and will be
referenced by the new unified project too):

```csharp
// Thrown only from the one call, inside a strategy, that first requires privileged access. Never thrown for any
// other reason. Caught only by ScanStrategySelector, deciding whether to try the next strategy; never caught
// anywhere else, and never thrown to convert a real scan failure into "try something else".
sealed class StrategyUnavailableException(string strategy, Exception cause) : Exception($"{strategy}: {cause.Message}", cause)
{
    public string Strategy { get; } = strategy;
}
```

**Exactly two call sites throw it**, both already the first privileged operation their strategy performs today
(traced in the existing code, not assumed):

- `BulkNative.OpenVolume` — `src\Shared\BulkReader\BulkReader.cs`, called at `BulkScanner.Scan`'s line 42, before
  anything else (before `OpenMft`/`EnableBackupPrivilege` at line 48). Opening `\\.\<drive>:` with `GENERIC_READ`
  needs administrator rights; on a non-elevated token this is the very first thing that fails, before any MFT
  data has been touched. Wrap: catch the existing `Win32Exception`-throwing helper's failure here specifically
  (not a blanket try/catch around the whole scan) and re-throw as `StrategyUnavailableException("mft", ...)`.
- `Native.OpenVolume` — `src\DirSizer.Fsctl\FsctlScanner.cs` (moving to `Shared`, see below), called as the first
  line of `Scanner.Run()`. Same reasoning, same wrap, `StrategyUnavailableException("fsctl", ...)`.

**Everything after that point in either strategy is a real result or a real error, full stop.** In particular:
`BulkScanOutcome`'s `Change`/`Attempts` (the existing MFT-layout-changed / `UNSTABLE` signal) is an ordinary
returned value, not an exception — it already flows through untouched today, and nothing here changes that. A
`FSCTL` call failing partway through a real scan, a corrupt record, a later access-denied on a specific file — all
of these propagate as errors exactly as they do in the dedicated executables today. The selector's `catch` clause
is narrow (`catch (StrategyUnavailableException)` around the call into each strategy's entry point only), not a
general exception handler.

## Strategies

### `MftScanner` (Step 2)

Wraps `BulkScanner.Scan` unchanged. NTFS only, raw `$MFT` read, read-only, existing stability detection and
one-time retry, existing `UNSTABLE` result reporting — none of this is redesigned. `dirsizer-bulk.exe` is renamed
to `dirsizer-mft.exe` in this step (its `DirSizer.Bulk.csproj`'s `AssemblyName` changes; the project folder name
is left as `DirSizer.Bulk` — renaming the folder too is cosmetic churn with no behavioral value and is not done
here, matching how `DirSizer.Fs`'s folder name does not change in Step 1 either, only its `AssemblyName`).

### `NtfsFsctlScanner` (Step 3)

Wraps `Scanner.Run()` from `FsctlScanner.cs` (`DirSizer.Fsctl`) unchanged. `FsctlScanner.cs` moves from
`src\DirSizer.Fsctl\` into `src\Shared\` (mirroring where `BulkReader.cs` already lives), so both `DirSizer.Fsctl`
(the dedicated `dirsizer-fsctl.exe`, unchanged behavior) and the new unified project can compile the same source
without duplication. `dirsizer-fsctl.exe`'s name is unchanged (it already names the mechanism, not "ntfs" or
similar, so there is nothing to rename).

### `FileSystemScanner`

The existing `FsScanner.Scan`, unchanged. Internally still selects among `find`/`handle`/`nt`/`auto` exactly as
`feature/fs-enumerator-default` built it — that whole mechanism (contract, benchmark results, `--enumerator`
grammar, the B-to-A fallback) stays exactly where it is, now correctly scoped as **this one strategy's own
internal implementation detail**, invisible above `IScanStrategy`. The always-available, no-elevation-required
fallback of last resort: if MFT and FSCTL are both unavailable, or the file system isn't NTFS, or the root isn't a
drive letter, this is what runs, exactly as it does today when invoked directly.

## `IScanStrategy`

```csharp
interface IScanStrategy
{
    string Name { get; }             // "mft", "fsctl", "filesystem" — reported, see "Reporting"
    ScanOutcome Scan(string rootPath, ScanOptions options);
}
```

`ScanOutcome`/`ScanOptions` are not new generic types invented for this interface — each strategy's existing
result/settings types (`BulkScanOutcome`/`BulkScanSettings`, `ScanResult`/`Options`, `FsResult`/`ScanSettings`)
are reused as-is inside each strategy's implementation. The interface's job is only to let
`ScanStrategySelector` try strategies in order and catch `StrategyUnavailableException` uniformly; it does not
attempt to unify the three existing result shapes into one type in this work (their output formatting stays
strategy-specific, same as today's three separate `--json` shapes) — a shared result model is future work if and
when file/directory search (which does need one) is built.

## Executable structure

New library **`DirSizer.Fs.Core`**: everything `DirSizer.Fs` owns today except `Program.cs` and `app.manifest`
(`Enumerator*.cs`, `HandleEnumerators.cs`, `Win32Find.cs`, `Walker.cs`, `FsScanner.cs`, `DirModel.cs`,
`RootPath.cs`, `FsOptions.cs`, `FsOutput.cs`, all of `SelfTests*.cs`) — mirrors how `DirSizer.Core` already backs
`DirSizer.Bulk`/`DirSizer.Fsctl`. `DirSizer.Fs` shrinks to `Program.cs` + `app.manifest`, references
`DirSizer.Fs.Core`, `AssemblyName` becomes `dirsizer-fs` (Step 1's one user-visible rename, zero behavior change,
same 39 self-tests, same CLI).

New project **`DirSizer`** (`dirsizer.exe`, unified): references `DirSizer.Fs.Core`, `DirSizer.Core`, and — via
the same `<Compile Include="..\Shared\...">` convention `DirSizer.Bulk`/`DirSizer.Fsctl` already use, not a new
exe-to-exe project reference — the shared `BulkReader\*.cs` files (Step 2) and `FsctlScanner.cs` once it moves to
`Shared` (Step 3). Its own `Program.cs` is thin: parse the reduced option set, run `ScanStrategySelector`, print
the result. Its own `app.manifest` stays `asInvoker` (non-elevated) — exactly like today's `dirsizer.exe`. This
is what makes the earlier confusion ("ran it elevated, expected NTFS") resolve correctly once Step 2/3 land:
`asInvoker` means "don't force a UAC prompt", not "never run with an elevated token" — a `dirsizer.exe` launched
from an already-elevated console inherits that token, `OpenVolume`/`EnableBackupPrivilege` succeed, and the
selector picks `MftScanner`. Launched from a normal console, those calls throw `StrategyUnavailableException`,
and it falls through to `FileSystemScanner` — silently and correctly, exactly as "Access and elevation" in the
original proposal specified.

`DirSizer.Bulk` and `DirSizer.Fsctl` keep their own `Program.cs`/`app.manifest` (`requireAdministrator`,
unchanged) and keep building `dirsizer-mft.exe`/`dirsizer-fsctl.exe` as today's fully independent, elevation-
enforced executables — the unified `dirsizer.exe` does not replace them, it adds a fourth way to reach the same
underlying code.

`scripts\release.ps1`'s `$Tools` table gains the new `dirsizer.exe` entry and the `dirsizer-bulk.exe` →
`dirsizer-mft.exe` rename, in whichever step actually introduces each exe.

## Reporting

Diagnostic output (not the normal user path) may show which strategy ran. `--benchmark`/`--json` on
`dirsizer.exe` report `strategy=mft|fsctl|filesystem` (and, only for `filesystem`, the existing
`enumerator=...`/`enumerator_fallback=...` fields underneath it, unchanged in shape). If a fallback between
strategies happened (an `StrategyUnavailableException` was caught), the result records which strategy was
actually used and why — same spirit as `enumerator_fallback`/`enumerator_fallback_reason`, one level up. A real
scan error is reported as an error, never silently turned into "tried a different strategy".

## Testing

Every strategy keeps satisfying its own existing independent-oracle tests, unchanged (`DirSizer.Fs.Core`'s 39,
`DirSizer.Bulk`'s, `DirSizer.Fsctl`'s). New tests, one set per step:

- **Step 1**: `dirsizer.exe`'s result is identical to `dirsizer-fs.exe`'s for the same input (the "no behavior
  change" acceptance criterion — snapshot equality, the same technique used for the very first enumerator-
  contract step); the selector reports `strategy=filesystem`.
- **Step 2**: automated, always runs, no elevation needed — a fake/injectable
  `StrategyUnavailableException`-throwing `IScanStrategy` proves the selector moves to the next strategy on
  exactly that exception and nothing else (mirrors `FallbackEnumerator`'s test shape). **Separately, manual**:
  setting a deny ACL on one's own file needs only `WRITE_DAC` (which an owner already has) and is unrelated to
  the administrator token `OpenVolume`/`SeBackupPrivilege` actually require — so, unlike the existing deny-ACL
  test, real MFT selection is *not* reachable by an ordinary `--self-test` run and must not be asserted as one.
  Verified by hand instead, the same way the `--enumerator=auto` real-hardware smoke check was done: run
  `dirsizer.exe C:\` from a genuinely elevated console and confirm `strategy=mft` plus a result identical to
  `dirsizer-mft.exe C:\`'s (snapshot comparison); also run it from a normal, non-elevated console and confirm
  `strategy=filesystem` with no error.
- **Step 3**: same split — automated fake-based selector test (a fake MFT strategy throws
  `StrategyUnavailableException`, a fake FSCTL strategy succeeds, proving the two-level chain end to end,
  including that FSCTL is genuinely reachable when MFT is not — this is the only place that combination is
  exercised, since no known real environment has MFT unavailable while FSCTL is available: both need the same
  administrator token, so on real hardware they are available or unavailable together). Manual: from an
  elevated console, `dirsizer-fsctl.exe C:\`'s result matches `NtfsFsctlScanner`'s output as wrapped inside
  `dirsizer.exe` (called directly via a small internal test hook, since the selector itself would pick `mft`
  first on real hardware and never reach the FSCTL branch there) — confirming the wrapping is correct, not
  that the selection *policy* prefers FSCTL over MFT on real hardware, which it deliberately never does.

`scripts\Compare-Fs.ps1`/`Compare-Enumerators.ps1` and the existing `FileSystemScanner` benchmark results are
migrated by reference, not redone: they already established that `find`/`handle`/`nt`/`auto` are correct and
which is fastest *within* `FileSystemScanner`; nothing here reopens that question, it only adds two more
strategies above it.

## Not part of this work

Automatic elevation (a normal `dirsizer.exe` invocation never tries to elevate itself); filtering MFT/FSCTL scans
to an arbitrary subdirectory (drive-root-only, as today); a unified `ScanOutcome` type shared by all three
strategies (deferred to when search needs it); cold-cache or cross-machine measurement; `dirsizer-inspect.exe`
changes; changing which enumerator `FileSystemScanner` defaults to (still `find`, per the frozen rollout-gate
result); renaming any project folder (only `AssemblyName`s change).
