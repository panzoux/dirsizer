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

**In scope:** a fallback wrapper enumerator and its shared state; wiring it in as the new default's
implementation (selectable explicitly as `--enumerator=auto` regardless of what the shipped default ends up
being); reporting; self-tests with a controllable fake; a real-hardware smoke check; the re-measurement that
gates flipping `EnumeratorSpec.Default`, and flipping it only if that re-measurement clears the bar.

**Out of scope:** any fallback for C (never a default, see "Background"); re-probing B later in the same run
once a fallback has triggered; per-volume or per-root fallback state (one shared state per scan, see
"Detection scope" below); changing the 10% bar itself; cold-cache measurement, a second machine, ReFS,
network shares (still unverified, as recorded in design_fs.md's capability matrix).

## Architecture

### `ReadResult.FirstQuery`

`Enumerator.cs` currently defines `readonly record struct ReadResult(ReadOutcome Outcome, int Error);`. Add
a third, defaulted field:

```csharp
readonly record struct ReadResult(ReadOutcome Outcome, int Error, bool FirstQuery = false);
```

`FirstQuery` is true only when a `Failed` result came from the *very first* query `BufferedEnumerator.Read()`
issued for that directory (the Restart-class call), before any entry could have been handed to the sink. It
is a plain fact about *when* the failure happened, not a judgement about *why*; `BufferedEnumerator` does not
interpret the error code. Existing two-argument call sites (`new ReadResult(ReadOutcome.Complete, 0)` etc.)
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
// Shared by all workers of a run: once B is found unsupported, every worker (including ones already running)
// switches to A for the rest of the scan. One-way, like LargeFetchState: never re-probes B afterwards.
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
one final `ReadResult` per directory.

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
}
```

(`Marshal.GetPInvokeErrorMessage` is already used the same way in `Worker.Process` and `FsScanner.Scan` for
error samples — same formatting convention.)

`Walker.Run` already reads `factory.Name` and `factory.LargeFetch` *after* the whole walk has joined (this
is exactly how the existing `LargeFetch` fallback-to-off is reported truthfully today); read
`factory.FallbackEnumerator` and `factory.FallbackReason` the same way, at the same point, and add them to
`WalkResult`.

## Selecting `auto`

`EnumeratorSpec.cs`: `EnumeratorKind` gains `Auto`. `EnumeratorSpec.Default` becomes
`new(EnumeratorKind.Auto, EntryClass.None, true, 0)` (the `LargeFetch` field only matters for `Find` and is
otherwise ignored, exactly as today for `Handle`/`Nt`). `Parse` gains a case `"auto"` (no class, no buffer
size — same shape restriction as `"find"`) so it is explicitly selectable, e.g. for
`Compare-Enumerators.ps1` and the self-tests, independent of whatever the shipped default is. `Canonical`
returns `"auto"` for `EnumeratorKind.Auto`. `CreateFactory` returns `new AutoFactory()` for
`EnumeratorKind.Auto`.

Every other spelling (`find...`, `find:nolarge`, `handle...`, `nt...`) is unaffected and keeps meaning
exactly what it says: **no wrapper, no shared state, a real failure is `Failed`.** This is what "explicit
selection never falls back" means concretely — `--enumerator=handle:full:64` on an unsupported file system
still fails the directory and is counted, exactly as it does on `feature/fs-enumerator-benchmark` today.

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

```
enumerator=auto, large_fetch=on, ...                                           (no fallback)
enumerator=auto (fallback: find, reason=handle:full:64 not supported here: ... (error 87)), large_fetch=on, ...   (fallback happened)
```

i.e. `enumerator={m.Enumerator}{(m.EnumeratorFallback is null ? "" : $" (fallback: {m.EnumeratorFallback}, reason={m.EnumeratorFallbackReason})")}`.

`FsOutput.Write`: one more stderr warning, printed once, right where the existing `Unreadable` warning is
printed (same block, same style, so both can appear together without looking unrelated):

```csharp
if (result.Metrics.EnumeratorFallback is not null)
    error.WriteLine($"warning: enumerator {result.Metrics.Enumerator} fell back to {result.Metrics.EnumeratorFallback}: {result.Metrics.EnumeratorFallbackReason}");
```

This prints regardless of `--benchmark`, so it is visible in ordinary use, not only in diagnostics.

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

**Real-hardware smoke check** (manual, via `Compare-Enumerators.ps1`, part of the implementation plan, not
a self-test): `--enumerator=auto` against exFAT `D:\` (where `handle:full` already works — no trigger
expected, snapshot equal to `find`) and against every quiescent tree used on the benchmark branch (`T:\`,
`C:\Program Files\dotnet`, `C:\Windows\System32\drivers`, the two synthetic trees) — snapshot-equal to
`find` in every case, and `enumerator_fallback` is `null` in all of them (nothing in this environment is
expected to trigger it; the mechanism is exercised for real only by the fakes above).

## Rollout gate: does the shipped default actually change?

After the mechanism is implemented, tested, and committed: re-run `Compare-Enumerators.ps1 -Time` for
`find` vs `handle:full:64` (the same variant `auto`'s primary uses) on W1 (`C:\`, workers=8), with **more
rounds than the original 5** (implementer's judgement, at least 10, spread if practical rather than all
back-to-back) to address the methodology critique above. Two outcomes:

- **Median `walk_ms` is ≥10% lower than `find`, confirmed:** change `EnumeratorSpec.Default` to
  `EnumeratorKind.Auto`. Update `docs/design_fs.md` ("Enumerator comparison (P4)", "Results" and "Adoption
  rule" sections) and `docs/roadmap.md` with the new numbers and the decision. `find` remains fully
  supported via `--enumerator=find`.
- **It does not confirm (stays below 10%, or is inconsistent):** `EnumeratorSpec.Default` stays `find`. The
  mechanism, `--enumerator=auto`, and its tests are still merged (they are correct, tested, and useful on
  their own for anyone who opts in) — this is a valid, documented outcome of the branch, not a failure to
  fix. Record the re-measured numbers and this decision in the same two documents.

Either way, the branch's own self-tests (all of them, including the six cases above) must be green, and a
NativeAOT `--self-test` run must pass, before either document is updated with the final decision.

## Not part of this work

A fallback for C; re-probing B after a fallback has triggered, later in the same run; per-volume or
per-root fallback state (the scan-wide, one-way scope is intentional: workers do not cross volumes
mid-scan, since reparse-point directories are never entered); distinguishing a genuine, unrelated
`ERROR_INVALID_PARAMETER` on a first query from an unsupported-class one (the restriction to *first query
only* narrows this risk but does not eliminate it; no known real-world cause of a false trigger exists, and
none is fabricated here to "solve" it); cold-cache measurement, a second machine, ReFS, network shares
(still unverified per design_fs.md's capability matrix); changing the 10% adoption bar itself.
