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
selects the **best known strategy, according to the documented selection policy** below, among the ones that
are both **validated** and **available** for that path's file system — the user never names a backend. That
policy is a fixed, documented preference order (MFT, then FSCTL, then `FileSystemScanner`), not a measured
claim — unlike `FileSystemScanner`'s own internal enumerator choice,
which *is* backed by the `feature/fs-enumerator-benchmark`/`feature/fs-enumerator-default` measurements, MFT vs
FSCTL vs `FileSystemScanner` has never been benchmarked against each other. The order below is a reasonable
prior (raw `$MFT` reads and FSCTL both avoid the walk's per-directory syscalls, so both are expected to beat
`FileSystemScanner` on a large NTFS tree) recorded as a decision to revisit once real numbers exist, not
asserted as proven — see "Selection policy is a prior, not a measurement" below. Existing result semantics and
correctness guarantees are preserved regardless of which strategy runs. Dedicated executables (one per strategy)
remain available, unchanged in behavior, for users who already know which implementation they want, and for
diagnosis, benchmarking, and development — `--enumerator` (and any future `--strategy`-shaped option) lives on
those, as a diagnostic/expert surface, **not** on `dirsizer.exe`.

## Scope of this document, and phasing

One spec, phased implementation (three increments of one feature, not three independent sub-projects — each still
gets its own build-and-test checkpoint before the next starts, the same shape as the benchmark work):

- **Step 1**: rename today's `dirsizer.exe` to `dirsizer-fs.exe` (pure rename, zero behavior change to
  `dirsizer-fs.exe` itself — same CLI, same JSON shape, same 39 self-tests), extract its engine into a small
  library, introduce `IScanStrategy`/`UnifiedScanResult`/`ScanStrategySelector` with exactly one strategy
  (`FileSystemScanner`) that is always chosen. The new `dirsizer.exe` exists after this step and always scans the
  same way `dirsizer-fs.exe` does (same directory walk, same sizes, same counts — no NTFS strategy exists yet to
  differ from), but its own CLI and output are the smaller, uniform shapes specified below ("Common CLI options",
  "`IScanStrategy`") from the very start, **not** byte-identical to `dirsizer-fs.exe`'s own richer JSON — see
  "Testing" for what "no behavior change" is actually checked against in this step.
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

### Selection policy is a prior, not a measurement

MFT > FSCTL > `FileSystemScanner` is a documented assumption (see "Goal"), not a result. If Step 2/3's
implementation later measures the three head to head and the order turns out wrong, the fix is a one-line
change to this policy, not a redesign — the `IScanStrategy` boundary does not encode the ordering anywhere
else.

**FSCTL's place in the automatic chain may rarely or never actually trigger in practice**, and that is stated
honestly rather than glossed over: `MftScanner` and `NtfsFsctlScanner`'s availability checks both come down to
"can this process open `\\.\<drive>:` with `GENERIC_READ`", which needs the same administrator token for both.
On real hardware, they are available or unavailable *together* — if MFT works, the selector never reaches
FSCTL; if MFT doesn't, FSCTL almost certainly doesn't either. FSCTL is kept in the selector for two honest
reasons, not because a real gap between them is known today: (1) architectural future-proofing, in case a
future Windows version or a locked-down environment ever *does* separate the two capabilities; (2) it is the
reference implementation and the cheapest possible strategy to wire in, once `MftScanner`'s wiring already
exists in Step 2 — Step 3 is small precisely because it reuses the same pattern. If this turns out to be dead
code in practice, that is an acceptable, disclosed outcome of this design, not a hidden one.

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

**`BulkNative.OpenVolume`, `EnableBackupPrivilege`, and `Native.OpenVolume` themselves are never modified.** An
earlier draft of this design had them throw `StrategyUnavailableException` directly — wrong, because those are
the exact same shared low-level calls `BulkScanner.Scan` and `Scanner.Run` already use internally, which the
dedicated `dirsizer-mft.exe`/`dirsizer-fsctl.exe` depend on for their own, unrelated, unchanged error handling
and self-tests. Changing what they throw would be a real behavior change smuggled into "unchanged" executables.

Instead, **each new `IScanStrategy` wrapper (`MftScanner`, `NtfsFsctlScanner`) performs its own, separate,
redundant probe** before calling the real, untouched scan entry point:

```csharp
// MftScanner.Scan, sketch:
try { using var probe = BulkNative.OpenVolume($"\\\\.\\{drive}"); }
catch (Win32Exception cause) { throw new StrategyUnavailableException("mft", cause); }
// Past this point, nothing here is caught specially: BulkScanner.Scan runs exactly as it does for
// dirsizer-mft.exe, including its own internal (unmodified) call to the same OpenVolume/EnableBackupPrivilege.
return Adapt(BulkScanner.Scan(settings));
```

The probe's own handle is opened and immediately closed (`using`); the real scan then opens its own handle
again inside `BulkScanner.Scan`/`Scanner.Run`, unchanged. This costs one extra, cheap `CreateFile`+`CloseHandle`
round trip, only on the `dirsizer.exe` path, never on the dedicated executables' path (they never call the
probe). In exchange: zero risk to existing dedicated-executable behavior, and the availability check is
unambiguous by construction — it is *only* ever the wrapper's own probe call, nothing inside the black-box
`BulkScanner.Scan`/`Scanner.Run` calls is inspected or intercepted.

**Everything from `BulkScanner.Scan`/`Scanner.Run` onward is a real result or a real error, full stop**, exactly
as it is for the dedicated executables today. In particular: `BulkScanOutcome`'s `Change`/`Attempts` (the
existing MFT-layout-changed / `UNSTABLE` signal) is an ordinary returned value, not an exception — nothing here
touches that. A later FSCTL call failing partway through a real scan, a corrupt record, a later access-denied on
a specific file — all of these propagate as errors exactly as they do in the dedicated executables today,
because that code path is, byte for byte, the same code path. The selector's `catch` clause is narrow
(`catch (StrategyUnavailableException)` around the call into each strategy's `Scan` method), and
`StrategyUnavailableException` can only ever originate from the two probe call sites above.

**The probe decides availability once, before the real scan starts — it is not re-checked, and a failure
inside the real scan is never treated as "try the next strategy" even if it happens to be the same kind of
error the probe would have caught** (for example, rights revoked between the probe and the real open — a
TOCTOU race, accepted as out of scope: the probe's job is to skip the common case cheaply, not to guarantee
the real scan can never fail for a related reason). This is a restatement of "everything after the probe is a
real result or a real error", not a new rule, but stated explicitly here because it is the one place an
implementer might be tempted to add a second layer of fallback inside the strategy itself — that must not
happen. Step 2's manual verification also records the probe's own cost (one extra `CreateFile`+`CloseHandle`
against `OpenVolume`) relative to a full MFT scan, to confirm it is negligible rather than assuming it.

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

An earlier draft of this section said the three strategies' existing result/settings types were reused as-is
*through* `IScanStrategy`, while also saying `dirsizer.exe`'s output should not vary by strategy. Those two
statements can't both be true — `FsResult`, `ScanResult`, and `BulkScanOutcome` are genuinely different shapes,
so something has to adapt between them and one uniform output. Resolved here: **each strategy's native
scan method stays completely unchanged** (still what the dedicated executables call directly, with their own,
unchanged, full-detail output); **each `IScanStrategy` implementation is a thin adapter** that calls its native
method and maps the result into one small, genuinely common type that `dirsizer.exe` — and only
`dirsizer.exe` — serializes uniformly.

```csharp
interface IScanStrategy
{
    string Name { get; }                          // "mft", "fsctl", "filesystem" — reported, see "Reporting"
    UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options);   // throws StrategyUnavailableException
}

// dirsizer.exe's own, uniform result: every strategy adapts its native result into this. Dedicated executables
// are completely unaffected -- they still call their native Scan/Run method directly and print their own,
// richer, strategy-specific output exactly as they do today. This type lives in DirSizer.Core.
sealed record UnifiedScanResult(
    string Volume, int Top,
    UnifiedItem Root, UnifiedItem[] RootChildren, UnifiedItem[] Directories, UnifiedItem[] Files,
    long DirectoriesScanned, long Unreadable, long FileCount, long Bytes, string[] ErrorSamples,
    string Strategy, string? StrategyFallback, string? StrategyFallbackReason, double TotalMs);

readonly record struct UnifiedItem(string Path, long Size);

// What dirsizer.exe itself accepts, before being translated into each strategy's own native settings type by
// that strategy's adapter (see "Common CLI options" below).
sealed record UnifiedScanOptions(int Top, bool Files, bool Dirs, bool Strict, int Workers);
```

Each adapter's mapping is small because the three native shapes are already close: `FsResult`'s
`ResultItem[]`/`Counters` and MFT/FSCTL's `ResultCandidates`/`ScanResult` (built from the same
`DirSizer.Core.ResultSelector`/`SizeAggregator` pipeline both already share) both reduce to path/size pairs plus
a handful of counters. `FileSystemScanner`'s adapter maps `Counters.DirectoriesScanned` directly,
`DirectoriesDenied + DirectoriesFailed` into `Unreadable`; `MftScanner`/`NtfsFsctlScanner`'s adapters map their
existing `Scanned`/`Skipped` fields the same way (`Skipped` → `Unreadable` — the closest existing analogue to
"could not be fully accounted for", used consistently even though the underlying reason differs from a denied
directory). `TotalMs` and the strategy-specific extras that do *not* fit this common shape (`enum_ms_total`,
`entries_per_sec`, the enumerator name and its own fallback fields, MFT's `UNSTABLE`/`Attempts`, and so on) are
**not** part of `UnifiedScanResult` — a user who needs that level of detail uses the matching dedicated
executable's own `--benchmark`/`--json`, which is unchanged and already has it. `dirsizer.exe`'s own output is
deliberately smaller than any one dedicated executable's, not a superset — this is what "the user never needs
to understand internals" means concretely: less to look at, not a bigger merged schema.

A shared result model covering every strategy's full detail (not just this common subset) is explicitly not
attempted here — future work if and when file/directory search needs one, as the previous draft already said.

## Common CLI options

**`dirsizer.exe` accepts exactly**: `--top=N`, `--files`, `--dirs`, `--json`, `--strict`, `--benchmark`,
`--workers N`, `--self-test`, `-h`. Every option except `--workers` has **identical meaning regardless of which
strategy ran** — because, per `IScanStrategy` above, `--top`/`--files`/`--dirs`/`--json`/`--strict`/`--benchmark`
all act on the one common `UnifiedScanResult`, not on a strategy's native result, so there is nothing for them
to disagree about between strategies.

**`--workers N` is accepted for every strategy, but is only meaningful for `FileSystemScanner`** (passed through
to it exactly as today). Neither `BulkScanner.Scan` nor `Scanner.Run` has a worker-count concept at all — both
read the MFT/volume with a single sequential I/O stream, not a parallel directory walk — so `MftScanner`'s and
`NtfsFsctlScanner`'s adapters simply ignore `UnifiedScanOptions.Workers`. This is stated plainly rather than
either rejecting the flag for those strategies (the user does not know in advance which strategy will run, so a
flag that sometimes errors depending on invisible internal state would violate "the user never needs to
understand internals") or pretending it does something it doesn't.

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

`dirsizer.exe`'s `--json` output is the `UnifiedScanResult` shape from "`IScanStrategy`" above, serialized the
same uniform way regardless of which strategy ran — `strategy` (`"mft"`/`"fsctl"`/`"filesystem"`) is just one
more field in it, not a switch that changes the rest of the schema. If a fallback between strategies happened (a
`StrategyUnavailableException` was caught), `strategy_fallback`/`strategy_fallback_reason` are set — same spirit
as `enumerator_fallback`/`enumerator_fallback_reason` one level down, at the `FileSystemScanner` strategy's own
internal level, which `dirsizer.exe`'s output does not surface (see `IScanStrategy`: that level of detail stays
on the dedicated executables). `--benchmark` prints one text line built from the same `UnifiedScanResult`,
`strategy=...` included. A real scan error is reported as an error, never silently turned into "tried a
different strategy" — nothing in this section changes the availability/failure boundary already specified
above.

## Testing

Every strategy keeps satisfying its own existing independent-oracle tests, unchanged (`DirSizer.Fs.Core`'s 39,
`DirSizer.Bulk`'s, `DirSizer.Fsctl`'s). New tests, one set per step:

- **Step 1**: `dirsizer.exe`'s `UnifiedScanResult` (root/root_children/directories/files paths and sizes,
  `directories_scanned`, `unreadable`, `file_count`, `bytes`) matches what `dirsizer-fs.exe`'s own `FsResult`
  gives for the same input, once passed through the `FileSystemScanner` adapter's mapping — this is the "no
  behavior change" acceptance criterion for this step, checked at the level of scan substance, **not** raw JSON
  equality (the two JSON shapes are deliberately different from Step 1 onward, per "`IScanStrategy`"). The
  selector reports `strategy=filesystem`.
- **Step 2**: automated, always runs, no elevation needed — a fake/injectable
  `StrategyUnavailableException`-throwing `IScanStrategy` proves the selector moves to the next strategy on
  exactly that exception and nothing else (mirrors `FallbackEnumerator`'s test shape). **Separately, manual**:
  setting a deny ACL on one's own file needs only `WRITE_DAC` (which an owner already has) and is unrelated to
  the administrator token `OpenVolume`/`SeBackupPrivilege` actually require — so, unlike the existing deny-ACL
  test, real MFT selection is *not* reachable by an ordinary `--self-test` run and must not be asserted as one.
  Verified by hand instead, the same way the `--enumerator=auto` real-hardware smoke check was done: run
  `dirsizer.exe C:\` from a genuinely elevated console and confirm `strategy=mft` plus a `UnifiedScanResult`
  that matches `dirsizer-mft.exe C:\`'s own result at the substance level (root/directory sizes, counts — via
  the `MftScanner` adapter's mapping, same "substance, not raw JSON" comparison as Step 1, not a snapshot
  equality check); also run it from a normal, non-elevated console and confirm `strategy=filesystem` with no
  error. Same elevated run also records the probe's own cost (`--benchmark`'s `open_ms`-equivalent for the
  probe, or a manual timer around it) against the real scan's total time, to confirm it is negligible rather
  than assumed — expected to be milliseconds against a multi-second MFT scan, but this is stated as something
  to check, not asserted in advance.
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
to an arbitrary subdirectory (drive-root-only, as today); a **full-detail** shared result type covering every
strategy-specific field each native result has (`UnifiedScanResult` is deliberately a small common subset, not
this — see "`IScanStrategy`"; a full-detail shared model is deferred to when search needs one); cold-cache or
cross-machine measurement; `dirsizer-inspect.exe` changes; changing which enumerator `FileSystemScanner`
defaults to (still `find`, per the frozen rollout-gate result); renaming any project folder (only
`AssemblyName`s change); benchmarking MFT vs FSCTL vs `FileSystemScanner` against each other (the selection
order is a documented prior, not a measurement — see "Selection policy is a prior, not a measurement").
