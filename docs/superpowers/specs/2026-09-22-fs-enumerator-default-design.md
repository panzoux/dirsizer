# dirsizer.exe: default enumerator with a fallback (B, safety net to A)

## Goal

Make `handle:full:64` (B, `GetFileInformationByHandleEx`) usable as the enumerator behind `dirsizer.exe`'s
default, with an automatic, narrowly-scoped fallback to `find` (A, `FindFirstFileExW`) when B's information
class is not supported on the file system being scanned — without hiding any other kind of failure behind
that fallback. Whether the *shipped default* actually changes from `find` to this pair is decided later in
this branch, by a re-measurement (see "Rollout gate"); this design and its implementation do not depend on
that outcome.

## Background (frozen baseline — not revised by this work)

On branch `feature/fs-enumerator-benchmark` (merged into this branch's history), three enumerators were
compared behind `IDirectoryEnumerator`: A = `find`, B = `handle`, C = `nt`. Finalists, workers fixed at 8, 5
rounds, median `walk_ms` against `find` on W1 (real `C:\`): B (`handle:full:64`) **-9.1%**, C (`nt:dir:64`)
**-10.2%**. The adoption rule's bar is -10%. B missed it; C met it but is native/experimental and is never
promoted by the rule alone (see design_fs.md, "Standing"). Decision recorded there: `find` stays the
default. **These numbers and that decision are not changed by this document.** What follows is: (a) a
critique of whether -9.1% is solid enough to trust, without re-litigating the bar itself, and (b) the
mechanism that would be needed if a re-run does clear the bar.

**Methodology critique** (analysis only, no new runs performed for this spec): 5 rounds on W1 is thin
against an effect size (-9.1%) only about 2x the width of the observed min-max spread (≈900 ms out of
≈20 s, ≈4.5%). A separate before/after check on the same volume during that work showed swings of
+4.7%/-3.5%/-20% between otherwise-identical runs, i.e. single-digit-to-low-double-digit noise on `C:\` is
already demonstrated. Cache was assumed warm (plausible, since many equality runs preceded the timed ones)
but not enforced by an explicit warm-up pass. One machine, one session, one day; no repeat-day or
second-machine check. Conclusion: the direction (B and C both consistently faster, on all three workloads,
across every screened variant of each) is credible; the exact magnitude relative to the 10% bar is not
proven at 5 rounds. This is why the rollout gate (below) requires more rounds on the same `handle:full:64`
before the shipped default may change.

## Scope

**In scope:** a fallback wrapper enumerator and its shared state, exposed as the explicitly-selectable
`--enumerator=auto` (`EnumeratorSpec.Default` itself is untouched by this — see "Selecting `auto`");
reporting; self-tests with a controllable fake plus one wiring test on the real `AutoFactory`; a
real-hardware smoke check; the re-measurement that gates flipping `EnumeratorSpec.Default`, and flipping it
only if that re-measurement clears the bar.

**Out of scope:** any fallback for C (never a default, see "Background"); re-probing B later in the same run
once a fallback has triggered; per-volume or per-root fallback state (one shared state per scan, see
"`FallbackState`" below); changing the 10% bar itself; cold-cache measurement, a second machine, ReFS,
network shares (still unverified, as recorded in design_fs.md's capability matrix).

## User-facing behavior

This section is the CLI contract in one place; "Architecture" below is how it is built, not what it
promises. Everything here is a restatement of detail specified elsewhere in this document, not new scope.

**`--enumerator` values.** Unchanged from the benchmark branch except for one new value:

```
--enumerator=find              A, FindFirstFileExW, with LARGE_FETCH
--enumerator=find:nolarge      A, without LARGE_FETCH
--enumerator=handle[:class[:KiB]]   B, GetFileInformationByHandleEx (unchanged, no fallback)
--enumerator=nt[:class[:KiB]]       C, NtQueryDirectoryFileEx (unchanged, no fallback)
--enumerator=auto               NEW: B (handle:full:64) with a narrow, automatic fallback to A
```

**What `auto` does.** Tries `handle:full:64` first. If the *very first* directory-listing call for a
directory fails with `ERROR_INVALID_PARAMETER` (87) — B's information class not supported on this file
system — every directory read after that point in the scan uses `find` instead, and the directory that
revealed the problem is itself retried immediately with `find` (it is never counted as a failed directory
because of this). No other condition triggers it: access denied, a later query failing mid-directory, a
malformed buffer, or any other error is an ordinary directory failure, exactly as it is for every other
enumerator, and does not switch anything. `--enumerator=handle...` and `--enumerator=nt...` never fall back
— the same unsupported-class error there is just `Failed`, as on the benchmark branch today.

**The default, unqualified `dirsizer.exe <path>`.** During this branch's main implementation:
`dirsizer.exe <path>` behaves exactly as `dirsizer.exe --enumerator=find <path>` (nothing changes here — see
"Selecting `auto`"). *Only if* the rollout gate later confirms the 10% bar does this change, and the only
change it makes is: `dirsizer.exe <path>` becomes equivalent to `dirsizer.exe --enumerator=auto <path>`.
`--enumerator=find` keeps working, unqualified or not, regardless of which outcome ships.

**What the user sees when a fallback happens.** The scan still completes normally: no new exit code exists
for a fallback by itself (exit codes stay 0 / 1 / 3 with `--strict`, unchanged, and are driven only by
`directories_failed`/`directories_denied` as before — the directory that triggered the fallback is not one
of those). With `--json`, `statistics.performance.enumerator_fallback` is `"find"` (else `null`) and
`enumerator_fallback_reason` describes why (else `null`). Without `--json`, one stderr line, printed once
after the scan finishes (never mid-scan): `warning: enumerator auto fell back to find: <reason>`. With
`--benchmark`, the stderr benchmark line's `enumerator=` segment additionally shows `(fallback: find,
reason=...)` when it happened. If nothing triggers, none of this appears — output is indistinguishable from
an ordinary `auto` run.

**Not covered here:** the exact `--help`/README wording (written once the rollout gate is resolved, per
"Selecting `auto`", since it depends on which mode ships as the default) — this section states the behavior
those docs will describe, not their final phrasing.

## Architecture

### `ReadResult.FirstQuery`

`Enumerator.cs` currently defines `readonly record struct ReadResult(ReadOutcome Outcome, int Error);`. Add
a third, defaulted field:

```csharp
readonly record struct ReadResult(ReadOutcome Outcome, int Error, bool FirstQuery = false);
```

`FirstQuery` is true when the failure occurred during the *first* `Query()` call `BufferedEnumerator.Read()`
issues for that directory (the Restart-class call) — a statement about query ordinal only. It does **not**
assert that the sink received zero entries: a first-query failure can also come from `Parse()` rejecting a
malformed buffer *after* `Query()` itself succeeded (`bytes > 0` but `Parse` returns false), by which point
some entries from that same first query may already have reached the sink. This does not affect the
fallback: it only ever reacts to `FirstQuery && Error == 87`, and error 87 is always a `Query()`-level
failure (never a `Parse()` one), so by the time the fallback wrapper sees `FirstQuery=true` with that
specific error, nothing has been sunk yet for that directory. `BufferedEnumerator` does not interpret the
error code itself. Existing two-argument call sites (`new ReadResult(ReadOutcome.Complete, 0)` etc.)
are unaffected — a trailing parameter with a default value may still be omitted in a positional record
constructor call. `FindFirstEnumerator` (A) never sets it (default `false`): its own "no entries at all" case
is already `Complete`, not `Failed`, so the field is meaningless there.

`HandleEnumerators.cs`, `BufferedEnumerator.Read()`, capture whether the failing call was the first one:

```csharp
public ReadResult Read(string directoryPath, IEntrySink sink)
{
    var handle = DirectoryHandle.Open(directoryPath, out var openError);
    if (handle == Win32Find.InvalidHandle)
        return new ReadResult(openError == Win32Find.ErrorAccessDenied ? ReadOutcome.Denied : ReadOutcome.Failed, openError);
    try
    {
        var first = true;
        while (true)
        {
            var wasFirst = first;
            var error = Query(handle, first, out var bytes);
            first = false;
            if (error == Win32Find.ErrorNoMoreFiles) return new ReadResult(ReadOutcome.Complete, 0);
            if (error != 0) return new ReadResult(ReadOutcome.Failed, error, wasFirst);
            if (bytes <= 0) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInsufficientBuffer, wasFirst);
            if (!Parse(bytes, sink)) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInvalidData, wasFirst);
        }
    }
    finally
    {
        DirectoryHandle.Close(handle);
    }
}
```

(Only the three lines that now pass `wasFirst` and the new `var wasFirst = first;` line change; nothing else
in this method moves.)

### `FallbackState`

New file `EnumeratorFallback.cs`. Mirrors the existing `LargeFetchState` pattern in `FindFirstEnumerator.cs`
(shared, thread-safe, one-way, one instance per scan):

```csharp
// Shared by all workers of a run: once B is found unsupported, every Read that STARTS after that point uses
// A instead. A B.Read() already in progress when the flag flips is not interrupted — it is allowed to finish
// (the check is made once, at the start of Read, not partway through). One-way, like LargeFetchState: never
// re-probes B afterwards.
sealed class FallbackState
{
    int _reasonError;   // 0 = not triggered; Interlocked, first writer wins

    public bool Triggered => Volatile.Read(ref _reasonError) != 0;

    public int Reason => Volatile.Read(ref _reasonError);

    // Returns true if this call is the one that triggered the fallback (only the first caller gets true).
    public bool TryTrigger(int error) => Interlocked.CompareExchange(ref _reasonError, error, 0) == 0;
}
```

### `FallbackEnumerator`

Also in `EnumeratorFallback.cs`:

```csharp
// auto: B (handle:full:64) with a fallback to A (find) when B's information class is not supported on this
// file system. One instance per worker, wrapping one primary and one secondary enumerator instance.
sealed class FallbackEnumerator(IDirectoryEnumerator primary, IDirectoryEnumerator secondary, FallbackState state) : IDirectoryEnumerator
{
    public ReadResult Read(string directoryPath, IEntrySink sink)
    {
        if (state.Triggered) return secondary.Read(directoryPath, sink);
        var result = primary.Read(directoryPath, sink);
        // Only the first query of a directory, only this one error: GetFileInformationByHandleEx (and
        // NtQueryDirectoryFileEx, mapped the same way) report an unsupported information class this way. A
        // later query failing with the same code is a real, unrelated error and is not treated specially.
        if (result.Outcome == ReadOutcome.Failed && result.FirstQuery && result.Error == Win32Find.ErrorInvalidParameter)
        {
            state.TryTrigger(result.Error);
            return secondary.Read(directoryPath, sink);
        }
        return result;
    }
}
```

`state.TryTrigger` is called even by a worker that lost the race (its return value is unused here): every
caller still falls through to `secondary.Read`, so the directory that revealed the problem is retried with A
immediately and is never counted as `Failed`. Other workers already mid-directory with B may independently
hit the same error on their own directory before the flag is visible to them; each just falls through the
same way — harmless, no directory is double-read or double-counted, since `Walker`/`Worker` only ever sees
one final `ReadResult` per directory. Precisely: **a `Read` call already past the `state.Triggered` check
when the flag flips runs to completion with B**, whatever it returns; only a `Read` call that has not yet
started (the next directory a worker takes off the queue) is affected. This is a deliberate, harmless race —
not a bug to close — and does not change the "no double-read, no double-count" guarantee.

### Factory and reporting hook

`IEnumeratorFactory` (`Enumerator.cs`) gains two members, next to the existing `LargeFetch`:

```csharp
interface IEnumeratorFactory
{
    string Name { get; }
    bool LargeFetch { get; }
    // Non-null once a fallback has actually triggered during this run: the enumerator that was used instead
    // of Name, and why. Null for every factory that has no fallback (every one except AutoFactory).
    string? FallbackEnumerator { get; }
    string? FallbackReason { get; }
    IDirectoryEnumerator Create();
}
```

`FindFirstFactory` and `BufferedFactory` both return `null` for both (no behaviour change there — this
mirrors how they already return `false` for `LargeFetch` when it does not apply to them).

New `AutoFactory` in `EnumeratorSpec.cs` (or `EnumeratorFallback.cs` — implementer's choice, either file
already imports what it needs):

```csharp
sealed class AutoFactory : IEnumeratorFactory
{
    readonly BufferedFactory _primary = new(new EnumeratorSpec(EnumeratorKind.Handle, EntryClass.Full, false, EnumeratorSpec.DefaultBufferKiB));
    readonly FindFirstFactory _secondary = new();
    readonly FallbackState _state = new();

    public string Name => "auto";
    public bool LargeFetch => false;

    public string? FallbackEnumerator => _state.Triggered ? _secondary.Name : null;
    public string? FallbackReason => _state.Triggered
        ? $"{_primary.Name} not supported here: {Marshal.GetPInvokeErrorMessage(_state.Reason).TrimEnd()} (error {_state.Reason})"
        : null;

    public IDirectoryEnumerator Create() => new FallbackEnumerator(_primary.Create(), _secondary.Create(), _state);

    // Self-tests only (see "Testing"): lets a test force the shared state without going through a real
    // unsupported file system, to prove the wiring (which factory is primary, which is secondary, that both
    // share one state) independently of B's own already-proven correctness.
    internal FallbackState TestOnlyState => _state;
}
```

(`Marshal.GetPInvokeErrorMessage` is already used the same way in `Worker.Process` and `FsScanner.Scan` for
error samples — same formatting convention.)

`Walker.Run` already reads `factory.Name` and `factory.LargeFetch` *after* the whole walk has joined (this
is exactly how the existing `LargeFetch` fallback-to-off is reported truthfully today); read
`factory.FallbackEnumerator` and `factory.FallbackReason` the same way, at the same point, and add them to
`WalkResult`.

## Selecting `auto`

`EnumeratorSpec.cs`: `EnumeratorKind` gains `Auto`. **`EnumeratorSpec.Default` is *not* touched by the main
implementation task — it stays `new(EnumeratorKind.Find, EntryClass.None, true, 0)`, exactly as it is on
`feature/fs-enumerator-benchmark` today.** `auto` is added purely as a new, explicitly-selectable value
(`--enumerator=auto`), on equal footing with `find`/`handle`/`nt`, until the rollout gate says otherwise.
Changing `EnumeratorSpec.Default` to `Auto` is the *only* code change the rollout gate's "confirmed" branch
makes (see "Rollout gate" below) — it is not part of this section's task. This keeps the meaning of the
branch unambiguous while it is being built: **a branch that adds a fallback mechanism and `--enumerator=auto`,
not a branch that necessarily changes the default.**

`Parse` gains a case `"auto"` (no class, no buffer size — same shape restriction as `"find"`) so it is
explicitly selectable, e.g. for `Compare-Enumerators.ps1` and the self-tests. `Canonical` returns `"auto"`
for `EnumeratorKind.Auto`. `CreateFactory` returns `new AutoFactory()` for `EnumeratorKind.Auto`.

Every other spelling (`find...`, `find:nolarge`, `handle...`, `nt...`) is unaffected and keeps meaning
exactly what it says: **no `FallbackEnumerator` wrapper, no `FallbackState`, a real failure is `Failed`**
(`find` still has its own, unrelated `LargeFetchState` — "no shared state" would overstate this). This is
what "explicit selection never falls back" means concretely — `--enumerator=handle:full:64` on an
unsupported file system still fails the directory and is counted, exactly as it does on
`feature/fs-enumerator-benchmark` today.

`EnumeratorSpec.CreateFactory(FindFirstFn? findFirst = null)`'s injection parameter exists for self-tests
that need a fake find-first delegate under the plain `find` spec; `AutoFactory`'s internal
`FindFirstFactory()` does not need it wired through — the self-tests for `auto`'s own behaviour (below) use
a fully-faked `IEnumeratorFactory`/`IDirectoryEnumerator` pair instead of the real production classes, so
there is nothing in this design that exercises `AutoFactory`'s real secondary through a test seam.

`--help` (`FsOptions.PrintHelp`) and both READMEs must mention `auto` and its fallback in the
`--enumerator` text, next to the existing `find`/`handle`/`nt` line. The exact default-related wording (does
it say "auto, the default" or "find, the default, auto available") depends on the rollout gate's outcome, so
write it once the gate is resolved, alongside the `design_fs.md`/`roadmap.md` updates.

## Reporting

`FsMetrics` (`FsScanner.cs`) and `JsonFsPerformance`/`ToJson` (`FsOutput.cs`) each gain two fields, placed
right after `Enumerator`/`enumerator`: `EnumeratorFallback` (`string?`) and `EnumeratorFallbackReason`
(`string?`), fed from `WalkResult.EnumeratorFallback`/`EnumeratorFallbackReason`. JSON keys (snake_case,
same `JsonNamingPolicy` as every other field): `enumerator_fallback`, `enumerator_fallback_reason`. Both
`null` unless a fallback actually happened during that run.

`FsOutput.BenchmarkLine`: the existing `enumerator={m.Enumerator}` segment becomes conditional —

`large_fetch` reports `find`'s own `LARGE_FETCH` state and is otherwise `off` (`AutoFactory.LargeFetch` is
`false`, same as `BufferedFactory` today — it only ever means something for `find` itself; `auto`'s `find`
fallback path, if it triggers, still uses `LARGE_FETCH` internally, this field just doesn't surface it):

```
enumerator=auto, large_fetch=off, ...                                          (no fallback)
enumerator=auto (fallback: find, reason=handle:full:64 not supported here: ... (error 87)), large_fetch=off, ...   (fallback happened)
```

i.e. `enumerator={m.Enumerator}{(m.EnumeratorFallback is null ? "" : $" (fallback: {m.EnumeratorFallback}, reason={m.EnumeratorFallbackReason})")}`.

`FsOutput.Write`: one more stderr warning, printed once, right where the existing `Unreadable` warning is
printed (same block, same style, so both can appear together without looking unrelated):

```csharp
if (result.Metrics.EnumeratorFallback is not null)
    error.WriteLine($"warning: enumerator {result.Metrics.Enumerator} fell back to {result.Metrics.EnumeratorFallback}: {result.Metrics.EnumeratorFallbackReason}");
```

This prints regardless of `--benchmark`, so it is visible in ordinary use, not only in diagnostics. **It is
printed once, after the whole scan finishes** (same place and timing as the existing `Unreadable` warning),
**not at the instant the fallback triggers** — there is no new mid-scan notification path from a worker
thread to stderr, and this design does not add one; that would be additional scope this branch does not
need.

## Testing

**Fake test double** (in the self-test source, e.g. `SelfTests.Enumerators.cs` — not in production code):
a small `IDirectoryEnumerator`/`IEnumeratorFactory` pair whose `Read` returns a caller-programmed sequence of
`ReadResult`s (by directory path or by call count), so `FallbackEnumerator` can be driven deterministically
without touching real Win32/ntdll calls. Cases to cover:

1. First query fails with `FirstQuery=true, Error=87` → `FallbackEnumerator` returns the secondary's result
   for that directory (not the primary's `Failed`); `state.Triggered` becomes true; the directory is not
   double-counted (one `Worker.Process` call sees exactly one `ReadResult`).
2. A later query (not the first) fails with `Error=87, FirstQuery=false` → returned as-is, `Failed`,
   `state.Triggered` stays false.
3. Once triggered, a fresh `FallbackEnumerator` instance (simulating another worker) with `state.Triggered`
   already true never calls its own primary at all — goes straight to secondary.
4. Whole-walk test (`FsScanner.Scan` with an `AutoFactory`-shaped test factory) against the existing oracle:
   a fixture where one branch's first directory read simulates the unsupported error — the walk's result
   (sizes, counts) matches what a pure-A run of the same fixture gives, and `WalkResult.EnumeratorFallback`
   is set.
5. `--enumerator=handle:full:64` (no `auto`) against the same simulated-unsupported fake primary → ordinary
   `Failed`, counted, no fallback — proves explicit selection really has no wrapper.
6. `EnumeratorSpec.Parse("auto")` round-trips to `Canonical == "auto"`; `"auto:full"` and similar are
   rejected the same way `"find:full"` already is.
7. **`AutoFactory` wiring** (the real production class, not a fake — this is the one thing the fakes above
   cannot prove): give `AutoFactory` an `internal` accessor to its `FallbackState`
   (`internal FallbackState TestOnlyState => _state;` — self-tests live in the same assembly, so `internal`
   is enough, no public surface added). A test creates **two** `IDirectoryEnumerator` instances from one
   `AutoFactory` (`var e1 = factory.Create(); var e2 = factory.Create();`, simulating two workers), *then*
   calls `factory.TestOnlyState.TryTrigger(87)`, *then* reads a real, existing directory (the standard
   `TempTree` fixture) with both `e1` and `e2`: both results must match a plain `find` read of the same
   directory exactly. Because the two enumerator instances were created *before* the state was triggered and
   still both observe it, this proves they were handed the *same* `FallbackState` instance (two separate,
   unshared states would leave both untriggered) and that the secondary really is `find`, without needing to
   re-verify B itself (already proven correct by the benchmark branch's own conformance tests) or add any
   further test-only surface to assert the primary's identity — the constructor already states it, in code
   that does not change based on runtime conditions.

**Real-hardware smoke check** (manual, via `Compare-Enumerators.ps1`, part of the implementation plan, not
a self-test): `--enumerator=auto` against exFAT `D:\` (where `handle:full` already works — no trigger
expected, snapshot equal to `find`) and against every quiescent tree used on the benchmark branch (`T:\`,
`C:\Program Files\dotnet`, `C:\Windows\System32\drivers`, the two synthetic trees) — snapshot-equal to
`find` in every case, and `enumerator_fallback` is `null` in all of them (nothing in this environment is
expected to trigger it; the mechanism is exercised for real only by the fakes above).

## Rollout gate: does the shipped default actually change?

After the mechanism is implemented, tested, and committed, re-run the measurement with a fixed, reproducible
procedure (this is deliberately more prescriptive than "implementer's judgement", to address the methodology
critique above rather than just gesture at it):

- Same workload (W1, real `C:\`), same worker count (8), same build (the NativeAOT `dirsizer.exe`, matching
  how the original finalists were measured).
- One untimed warm-up pass first (one full scan, result discarded, to bring the volume's metadata into cache
  before any timed round — the original run had no explicit warm-up; this run does).
- **10 timed rounds minimum**, alternating `find`, `handle:full:64` and `auto` with the order rotated each
  round (the same alternation `Compare-Enumerators.ps1 -Time` already does for however many `-Specs` it is
  given — pass all three together in one `-Specs` list and one `-Rounds 10` call, not three separate runs,
  so the alternation is real).
- Record every individual round's `walk_ms` for all three (`Compare-Enumerators.ps1 -Time`'s own output
  already lists min/median/max; keep the full table, not just the summary line, in whatever this step writes
  to `docs/design_fs.md`).
- The decision uses the median only, exactly as the frozen baseline's adoption rule already does. `auto` is
  measured in the same run purely to confirm its wrapper overhead is negligible against `handle:full:64`
  (its `state.Triggered` check on every call is expected to cost nothing measurable) — it does not enter the
  pass/fail decision itself, which is made on `handle:full:64` vs `find` alone.

**Pass condition, stated exactly** (same "at least 10% lower" wording as `design_fs.md`'s adoption rule,
made unambiguous): let `mh` = median `walk_ms` of `handle:full:64` and `mf` = median `walk_ms` of `find`
over this re-run. The rule passes when `mh <= 0.90 * mf` (a reduction of exactly 10% counts as passing; less
than 10% does not).

Two outcomes:

- **`mh <= 0.90 * mf`, confirmed:** change `EnumeratorSpec.Default` to `EnumeratorKind.Auto` — this is the
  only code change this outcome makes; everything else was already built and merged in the main
  implementation task. Update `docs/design_fs.md` ("Enumerator comparison (P4)", "Results" and "Adoption
  rule" sections) and `docs/roadmap.md` with the new numbers (including `auto`'s own median, to show the
  wrapper added no measurable overhead) and the decision. `find` remains fully supported via
  `--enumerator=find`.
- **`mh > 0.90 * mf`:** `EnumeratorSpec.Default` stays `find` — no code change. The mechanism,
  `--enumerator=auto`, and its tests are still merged (they are correct, tested, and useful on their own for
  anyone who opts in) — this is a valid, documented outcome of the branch, not a failure to fix. Record the
  re-measured numbers and this decision in the same two documents.

Either way, the branch's own self-tests (all of them, including the seven cases above) must be green, and a
NativeAOT `--self-test` run must pass, before either document is updated with the final decision.

## Not part of this work

A fallback for C; re-probing B after a fallback has triggered, later in the same run; per-volume or
per-root fallback state (the scan-wide, one-way scope is intentional: workers do not cross volumes
mid-scan, since reparse-point directories are never entered); distinguishing a genuine, unrelated
`ERROR_INVALID_PARAMETER` on a first query from an unsupported-class one (the restriction to *first query
only* narrows this risk but does not eliminate it; no known real-world cause of a false trigger exists, and
none is fabricated here to "solve" it); cold-cache measurement, a second machine, ReFS, network shares
(still unverified per design_fs.md's capability matrix); changing the 10% adoption bar itself.
