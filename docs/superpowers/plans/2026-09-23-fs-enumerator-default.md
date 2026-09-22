# dirsizer.exe: default enumerator with a fallback (B, safety net to A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `--enumerator=auto` (B, `handle:full:64`, with a narrow, automatic fallback to A, `find`, when B's
information class is unsupported), fully tested and reported, **without changing the shipped default**
(`EnumeratorSpec.Default` stays `find`). A final task re-measures and, only if the 10% bar is confirmed,
flips the default in a separate, clearly-marked commit.

**Architecture:** `ReadResult` gains a `FirstQuery` fact. `BufferedEnumerator` (B and C's shared base) reports
it, uninterpreted. A new `FallbackEnumerator` wraps a primary (B) and secondary (A) `IDirectoryEnumerator`,
switching to the secondary, scan-wide and one-way, the first time a primary read fails on its first query with
`ERROR_INVALID_PARAMETER`. `AutoFactory` wires B and A together behind one shared `FallbackState`, selectable
as `--enumerator=auto`. Full spec: [docs/superpowers/specs/2026-09-22-fs-enumerator-default-design.md](../specs/2026-09-22-fs-enumerator-default-design.md).

**Tech Stack:** C# 12 / .NET 8, NativeAOT, no new P/Invoke (this task adds no Win32/ntdll calls — it only
wraps the existing A and B enumerators from `feature/fs-enumerator-benchmark`).

---

## Ground rules for the executing agent

- Run every command with the **PowerShell tool** from the repository root
  (`C:\Users\user\source\repos\panzoux\dirsizer`), never Bash (its shell mangles backslashes). Create or edit
  files with the Write/Edit tools, never shell heredocs.
- Branch is `feature/fs-enumerator-default` (already checked out, created from `feature/fs-enumerator-benchmark`
  at commit `722509a`). Never commit to `master`, never push. Commit messages end with
  `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`.
- **Line endings**: the repo is CRLF, UTF-8 no BOM. Files written by the Write tool are LF. Run this before
  every commit that touches `src\DirSizer.Fs`:

```powershell
function Normalize-FsProject {
    foreach ($f in (Get-ChildItem src\DirSizer.Fs -Recurse -File -Include *.cs, *.csproj, *.manifest | ForEach-Object FullName)) {
        $t = [IO.File]::ReadAllText($f); $x = $t -replace "`r?`n", "`r`n"
        if ($x -ne $t) { [IO.File]::WriteAllText($f, $x, [Text.UTF8Encoding]::new($false)) }
    }
}
```

- Build/test cycle: `dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release` then
  `dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test`. A deny-ACL test skips itself
  in a sandboxed shell (fine); the PowerShell tool runs it for real.
- Nothing outside `src\DirSizer.Fs`, `docs\`, `README*.md` may change (Task 6 checks this with `git diff
  --stat master`).
- **Do not change `EnumeratorSpec.Default`** in Tasks 1-5. That is Task 7's job, and only conditionally.

## File structure

| File | Change | Task |
| --- | --- | --- |
| `Enumerator.cs` | `ReadResult.FirstQuery`; `IEnumeratorFactory.FallbackEnumerator`/`FallbackReason` | 1, 3 |
| `HandleEnumerators.cs` | `BufferedEnumerator.Read()` sets `FirstQuery`; `BufferedFactory` returns `null` for the two new members | 1, 3 |
| `FindFirstEnumerator.cs` | `FindFirstFactory` returns `null` for the two new members | 3 |
| `EnumeratorFallback.cs` (new) | `FallbackState`, `FallbackEnumerator` | 2 |
| `EnumeratorSpec.cs` | `AutoFactory`; `EnumeratorKind.Auto`; `Parse`/`Canonical`/`CreateFactory` for `auto` (Default untouched) | 3, 4 |
| `Walker.cs` | `WalkResult` gains `EnumeratorFallback`/`EnumeratorFallbackReason`, read from the factory after the join | 5 |
| `FsScanner.cs` | `FsMetrics` gains the same two fields | 5 |
| `FsOutput.cs` | JSON `performance.enumerator_fallback`/`enumerator_fallback_reason`; benchmark line; stderr warning | 5 |
| `SelfTests.Enumerators.cs` | 7 new tests (fakes, `FallbackEnumerator`, `AutoFactory` wiring, `Parse("auto")`) | 6 |
| `SelfTests.Output.cs` | extend `SyntheticResult`/existing assertions for the two new metrics fields | 6 |
| `scripts\Compare-Enumerators.ps1` | no change (already accepts any `--enumerator` value) | 8 |
| `docs\design_fs.md`, `docs\roadmap.md`, `README.md`, `README-jp.md` | usage text now (Task 6); results and (conditionally) the default change later (Task 7) | 6, 7 |

---

### Task 1: `ReadResult.FirstQuery`

**Files:**
- Modify: `src\DirSizer.Fs\Enumerator.cs`
- Modify: `src\DirSizer.Fs\HandleEnumerators.cs`

- [ ] **Step 1: Write a failing test for the new field**

A test that forces `BufferedEnumerator` itself to hit a genuinely undersized first query would need a buffer
below `EnumeratorSpec.MinBufferKiB` (4 KiB), which `EnumeratorSpec` refuses to construct by design — not
worth bypassing here, since `BufferedEnumerator`'s own behaviour (including this new field, end to end) is
already fully exercised by Task 5's whole-walk test via the fake enumerators. This task only needs to prove
the field itself — default value and that the two-argument constructor is unaffected — which does not need a
real directory at all. Add to `src\DirSizer.Fs\SelfTests.Enumerators.cs`, inside the `FsSelfTests` class
(after `EnumeratorsClassifyDirectories`, before `EnumeratorsReportDenied`):

```csharp
    static void ReadResultFirstQueryDefaultsToFalse()
    {
        AssertEqual(false, default(ReadResult).FirstQuery, "a default ReadResult has FirstQuery false");
        AssertEqual(false, new ReadResult(ReadOutcome.Complete, 0).FirstQuery, "the two-argument constructor defaults FirstQuery to false");
        AssertEqual(true, new ReadResult(ReadOutcome.Failed, 87, true).FirstQuery, "the three-argument constructor sets it");
    }
```

Register it in `AddEnumeratorTests` (`SelfTests.Enumerators.cs`), right after the existing five entries:

```csharp
        tests.Add(new("ReadResult.FirstQuery defaults to false and the two-argument constructor is unaffected", ReadResultFirstQueryDefaultsToFalse));
```

- [ ] **Step 2: Run it to see it fail (does not compile yet — `FirstQuery` does not exist)**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error"
```

Expected: a compiler error naming `FirstQuery` as not existing on `ReadResult`.

- [ ] **Step 3: Add the field**

In `src\DirSizer.Fs\Enumerator.cs`, replace:

```csharp
readonly record struct ReadResult(ReadOutcome Outcome, int Error);
```

with:

```csharp
// FirstQuery is true only when a Failed result came from the very first Query() call BufferedEnumerator.Read()
// issues for a directory (before any entry from that call could have reached the sink). It says nothing about
// *why* the query failed — that is for the caller (see FallbackEnumerator) to decide. Meaningless (always
// false) for an enumerator with no such internal query structure, i.e. FindFirstEnumerator.
readonly record struct ReadResult(ReadOutcome Outcome, int Error, bool FirstQuery = false);
```

- [ ] **Step 4: Make `BufferedEnumerator` report it**

In `src\DirSizer.Fs\HandleEnumerators.cs`, in `BufferedEnumerator.Read`, replace:

```csharp
        try
        {
            var first = true;
            while (true)
            {
                var error = Query(handle, first, out var bytes);
                first = false;
                if (error == Win32Find.ErrorNoMoreFiles) return new ReadResult(ReadOutcome.Complete, 0);
                if (error != 0) return new ReadResult(ReadOutcome.Failed, error);
                // A query that succeeds without an entry means that the buffer is too small, not that the directory is finished.
                if (bytes <= 0) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInsufficientBuffer);
                if (!Parse(bytes, sink)) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInvalidData);
            }
        }
```

with:

```csharp
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
                // A query that succeeds without an entry means that the buffer is too small, not that the directory is finished.
                if (bytes <= 0) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInsufficientBuffer, wasFirst);
                if (!Parse(bytes, sink)) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInvalidData, wasFirst);
            }
        }
```

(Only the `var wasFirst = first;` line and the three `wasFirst` arguments are new; nothing else moves.)

- [ ] **Step 5: Run the test, confirm it passes, and the whole suite is unaffected**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "FirstQuery|passed|FAIL"
```

Expected: `0 Error(s)`, the new test's `ok` line, and `35 self-tests passed, 0 skipped.` (34 inherited from
`feature/fs-enumerator-benchmark` plus the one test this task adds).

- [ ] **Step 6: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs\Enumerator.cs src\DirSizer.Fs\HandleEnumerators.cs src\DirSizer.Fs\SelfTests.Enumerators.cs
git commit -m @'
ReadResult.FirstQuery: a plain fact about which query failed, for the fallback wrapper to use

BufferedEnumerator (B and C) now reports whether a Failed result came from a directory's first query, without
interpreting the error itself. FindFirstEnumerator (A) is unaffected: it never sets it.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 2: `FallbackState` and `FallbackEnumerator`

**Files:**
- Create: `src\DirSizer.Fs\EnumeratorFallback.cs`

- [ ] **Step 1: Write the failing tests**

Add to `src\DirSizer.Fs\SelfTests.Enumerators.cs`, after `ReadResultFirstQueryDefaultsToFalse`:

```csharp
    // A fake IDirectoryEnumerator whose Read() returns a pre-programmed sequence of results, one per call,
    // repeating the last one after the sequence is exhausted. Used only to drive FallbackEnumerator without
    // touching real Win32/ntdll calls.
    sealed class ScriptedEnumerator(params ReadResult[] results) : IDirectoryEnumerator
    {
        int _calls;
        public int Calls => _calls;

        public ReadResult Read(string directoryPath, IEntrySink sink)
        {
            var index = Math.Min(_calls, results.Length - 1);
            _calls++;
            return results[index];
        }
    }

    static void FallbackTriggersOnlyOnFirstQueryUnsupportedError()
    {
        // Case 1: the first query fails with FirstQuery=true, Error=87 -> falls back, not counted Failed by
        // the wrapper itself (Worker's own counting is exercised in the whole-walk test below).
        var state1 = new FallbackState();
        var primary1 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, Win32Find.ErrorInvalidParameter, true));
        var secondary1 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var sink = new CollectingSink();
        var result1 = new FallbackEnumerator(primary1, secondary1, state1).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Complete, result1.Outcome, "case 1: the secondary's result is returned, not the primary's Failed");
        AssertEqual(1, secondary1.Calls, "case 1: the secondary was used for this directory");
        Assert(state1.Triggered, "case 1: the shared state is now triggered");
        AssertEqual(Win32Find.ErrorInvalidParameter, state1.Reason, "case 1: the reason is recorded");

        // Case 2: a later query (not the first) fails with the same error code -> ordinary Failed, no trigger.
        var state2 = new FallbackState();
        var primary2 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, Win32Find.ErrorInvalidParameter, false));
        var secondary2 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result2 = new FallbackEnumerator(primary2, secondary2, state2).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Failed, result2.Outcome, "case 2: a non-first-query failure is returned as-is");
        AssertEqual(Win32Find.ErrorInvalidParameter, result2.Error, "case 2: the error is kept");
        AssertEqual(0, secondary2.Calls, "case 2: the secondary was never used");
        Assert(!state2.Triggered, "case 2: the shared state is not triggered");

        // Case 3: a fresh FallbackEnumerator whose shared state is already triggered never calls its primary.
        var state3 = new FallbackState();
        state3.TryTrigger(Win32Find.ErrorInvalidParameter);
        var primary3 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, 999, true));   // would fail loudly if ever called
        var secondary3 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result3 = new FallbackEnumerator(primary3, secondary3, state3).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Complete, result3.Outcome, "case 3: goes straight to the secondary");
        AssertEqual(0, primary3.Calls, "case 3: the primary is never called once the state is triggered");

        // Other outcomes and other errors on the first query never trigger: Denied, and a first-query error
        // that is not 87.
        var state4 = new FallbackState();
        var primary4 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Denied, Win32Find.ErrorAccessDenied, true));
        var secondary4 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result4 = new FallbackEnumerator(primary4, secondary4, state4).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Denied, result4.Outcome, "case 4: Denied passes through untouched");
        Assert(!state4.Triggered, "case 4: Denied never triggers");

        var state5 = new FallbackState();
        var primary5 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, 32, true));   // ERROR_SHARING_VIOLATION, first query
        var secondary5 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result5 = new FallbackEnumerator(primary5, secondary5, state5).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Failed, result5.Outcome, "case 5: a first-query error that is not 87 is not treated as unsupported");
        AssertEqual(32, result5.Error, "case 5: the real error is kept");
        Assert(!state5.Triggered, "case 5: not triggered");
    }
```

Register it in `AddEnumeratorTests`:

```csharp
        tests.Add(new("FallbackEnumerator falls back only on a first-query ERROR_INVALID_PARAMETER, never re-probes once triggered", FallbackTriggersOnlyOnFirstQueryUnsupportedError));
```

- [ ] **Step 2: Run it to see it fail to compile**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error"
```

Expected: `FallbackState`/`FallbackEnumerator` do not exist.

- [ ] **Step 3: Write `EnumeratorFallback.cs`**

```csharp
using System.Threading;

// Shared by all workers of a run: once B is found unsupported, every Read that STARTS after that point uses
// A instead. A B.Read() already in progress when the flag flips is not interrupted: it is allowed to finish
// (the check is made once, at the start of Read, not partway through). One-way, like LargeFetchState in
// FindFirstEnumerator.cs: never re-probes B afterwards.
sealed class FallbackState
{
    int _reasonError;   // 0 = not triggered; Interlocked, first writer wins

    public bool Triggered => Volatile.Read(ref _reasonError) != 0;

    public int Reason => Volatile.Read(ref _reasonError);

    // Returns true if this call is the one that triggered the fallback (only the first caller gets true; the
    // return value is not needed by FallbackEnumerator, every caller falls through to the secondary either way).
    public bool TryTrigger(int error) => Interlocked.CompareExchange(ref _reasonError, error, 0) == 0;
}

// auto: B (handle:full:64) with a fallback to A (find) when B's information class is not supported on this
// file system. One instance per worker, wrapping one primary and one secondary enumerator instance.
sealed class FallbackEnumerator(IDirectoryEnumerator primary, IDirectoryEnumerator secondary, FallbackState state) : IDirectoryEnumerator
{
    public ReadResult Read(string directoryPath, IEntrySink sink)
    {
        if (state.Triggered) return secondary.Read(directoryPath, sink);
        var result = primary.Read(directoryPath, sink);
        // Only the first query of a directory, only this one error: GetFileInformationByHandleEx (and
        // NtQueryDirectoryFileEx, mapped the same way through RtlNtStatusToDosError) report an unsupported
        // information class this way. A later query failing with the same code is a real, unrelated error.
        if (result.Outcome == ReadOutcome.Failed && result.FirstQuery && result.Error == Win32Find.ErrorInvalidParameter)
        {
            state.TryTrigger(result.Error);
            return secondary.Read(directoryPath, sink);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "Fallback|passed|FAIL"
```

Expected: `0 Error(s)`, the new test's `ok` line, `36 self-tests passed, 0 skipped.`

- [ ] **Step 5: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs\EnumeratorFallback.cs src\DirSizer.Fs\SelfTests.Enumerators.cs
git commit -m @'
Add FallbackState and FallbackEnumerator: B with a narrow, scan-wide, one-way fallback to A

Triggers only on a directory'"'"'s first query failing with ERROR_INVALID_PARAMETER (an unsupported
information class); any other failure, or the same error later in a directory, passes through untouched.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 3: `IEnumeratorFactory.FallbackEnumerator`/`FallbackReason`, `AutoFactory`

**Files:**
- Modify: `src\DirSizer.Fs\Enumerator.cs`
- Modify: `src\DirSizer.Fs\HandleEnumerators.cs` (`BufferedFactory`)
- Modify: `src\DirSizer.Fs\FindFirstEnumerator.cs` (`FindFirstFactory`)
- Modify: `src\DirSizer.Fs\EnumeratorSpec.cs` (`AutoFactory` added here, next to `BufferedFactory`'s sibling factories — actually add it to `EnumeratorFallback.cs` instead, since that file already exists for fallback-specific code and keeps `EnumeratorSpec.cs` about parsing, not construction)

- [ ] **Step 1: Write the failing test (the wiring test from the spec)**

Add to `src\DirSizer.Fs\SelfTests.Enumerators.cs`, after `FallbackTriggersOnlyOnFirstQueryUnsupportedError`:

```csharp
    static void AutoFactoryWiring()
    {
        using var tree = new TempTree();
        tree.MakeFile("one.txt", 10);
        tree.MakeDir("sub");

        var factory = new AutoFactory();
        AssertEqual("auto", factory.Name, "AutoFactory.Name");
        AssertEqual(false, factory.LargeFetch, "AutoFactory.LargeFetch is always false");
        AssertEqual(null, factory.FallbackEnumerator, "not yet triggered: null");
        AssertEqual(null, factory.FallbackReason, "not yet triggered: null");

        // Two instances, created before the shared state is triggered, simulating two workers.
        var e1 = factory.Create();
        var e2 = factory.Create();
        factory.TestOnlyState.TryTrigger(Win32Find.ErrorInvalidParameter);

        var expected = new FindFirstFactory().Create().Read(tree.Base, new CollectingSink());
        var sink1 = new CollectingSink();
        var result1 = e1.Read(tree.Base, sink1);
        var sink2 = new CollectingSink();
        var result2 = e2.Read(tree.Base, sink2);
        // Both instances observe the same (already-triggered) state, proving they share one FallbackState:
        // two separate, unshared states would leave at least one of them untriggered and still trying B.
        AssertEqual(expected.Outcome, result1.Outcome, "e1: matches a plain find read (the secondary really is find)");
        AssertEqual(expected.Outcome, result2.Outcome, "e2: same, proving the state is shared, not per-instance");
        AssertEqual(2, sink1.Entries.Count, "e1: both entries listed");
        AssertEqual(2, sink2.Entries.Count, "e2: both entries listed");
        Assert(factory.FallbackEnumerator == "find", "FallbackEnumerator is find once triggered");
        Assert(factory.FallbackReason!.Contains("87"), $"FallbackReason mentions the error code: {factory.FallbackReason}");
    }
```

Register it:

```csharp
        tests.Add(new("AutoFactory wires handle:full:64 and find behind one shared FallbackState", AutoFactoryWiring));
```

- [ ] **Step 2: Run it to see it fail to compile**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error"
```

Expected: `AutoFactory` does not exist, `IEnumeratorFactory` has no `FallbackEnumerator`/`FallbackReason`.

- [ ] **Step 3: Extend `IEnumeratorFactory`**

In `src\DirSizer.Fs\Enumerator.cs`, replace:

```csharp
interface IEnumeratorFactory
{
    // The canonical name, for example "find" or "handle:idextd:64"; it is what the benchmark line and the JSON report.
    string Name { get; }

    // find only: whether FIND_FIRST_EX_LARGE_FETCH is still in use (it is switched off for the rest of a run when the file system
    // rejects it). False for every other enumerator.
    bool LargeFetch { get; }

    // One instance per worker.
    IDirectoryEnumerator Create();
}
```

with:

```csharp
interface IEnumeratorFactory
{
    // The canonical name, for example "find" or "handle:idextd:64"; it is what the benchmark line and the JSON report.
    string Name { get; }

    // find only: whether FIND_FIRST_EX_LARGE_FETCH is still in use (it is switched off for the rest of a run when the file system
    // rejects it). False for every other enumerator.
    bool LargeFetch { get; }

    // Non-null once a fallback has actually triggered during this run: the enumerator that was used instead of
    // Name, and why. Null for every factory that has no fallback (every one except AutoFactory).
    string? FallbackEnumerator { get; }
    string? FallbackReason { get; }

    // One instance per worker.
    IDirectoryEnumerator Create();
}
```

- [ ] **Step 4: `BufferedFactory` and `FindFirstFactory` answer `null`**

In `src\DirSizer.Fs\HandleEnumerators.cs`, in `BufferedFactory`, add two lines next to the existing `LargeFetch`:

```csharp
    public bool LargeFetch => false;

    public string? FallbackEnumerator => null;
    public string? FallbackReason => null;

```

In `src\DirSizer.Fs\FindFirstEnumerator.cs`, in `FindFirstFactory`, add the same two lines next to its
`LargeFetch`:

```csharp
    public bool LargeFetch => _state.On;

    public string? FallbackEnumerator => null;
    public string? FallbackReason => null;

```

- [ ] **Step 5: `AutoFactory`, in `EnumeratorFallback.cs`**

Append to `src\DirSizer.Fs\EnumeratorFallback.cs`:

```csharp
using System.Runtime.InteropServices;

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

    // Self-tests only (see SelfTests.Enumerators.cs, AutoFactoryWiring): lets a test force the shared state
    // without a real unsupported file system, to prove the wiring (which factory is primary, which is
    // secondary, that both share one state) independently of B's own already-proven correctness.
    internal FallbackState TestOnlyState => _state;
}
```

Move the `using System.Runtime.InteropServices;` to the top of the file if the file already has other `using`
directives; do not duplicate it.

- [ ] **Step 6: Run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "AutoFactory|passed|FAIL"
```

Expected: `0 Error(s)`, the new test's `ok` line, `37 self-tests passed, 0 skipped.`

- [ ] **Step 7: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs\Enumerator.cs src\DirSizer.Fs\HandleEnumerators.cs src\DirSizer.Fs\FindFirstEnumerator.cs src\DirSizer.Fs\EnumeratorFallback.cs src\DirSizer.Fs\SelfTests.Enumerators.cs
git commit -m @'
Add AutoFactory: wires handle:full:64 (primary) and find (secondary) behind one shared FallbackState

IEnumeratorFactory gains FallbackEnumerator/FallbackReason, null for every factory except AutoFactory.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 4: `--enumerator=auto`

**Files:**
- Modify: `src\DirSizer.Fs\EnumeratorSpec.cs`

- [ ] **Step 1: Write the failing tests**

In `src\DirSizer.Fs\SelfTests.Cli.cs`, extend `EnumeratorSpecs`: add `("auto", "auto")` to the `good` array,
and `"auto:full"`, `"auto:64"` to the rejected-texts array. Replace:

```csharp
        var good = new (string Text, string Canonical)[]
        {
            ("find", "find"), ("FIND", "find"), ("find:nolarge", "find:nolarge"),
            ("handle", "handle:full:64"), ("handle:idextd", "handle:idextd:64"), ("handle:idextd:4", "handle:idextd:4"), ("handle:full:1024", "handle:full:1024"),
            ("nt", "nt:dir:64"), ("nt:idextd", "nt:idextd:64"), ("nt:full:256", "nt:full:256"), ("NT:Dir:4", "nt:dir:4"),
        };
        foreach (var (text, canonical) in good) AssertEqual(canonical, EnumeratorSpec.Parse(text).Canonical, $"--enumerator {text}");
        foreach (var text in new[] { "", "bogus", "find:full", "find:nolarge:4", "handle:dir", "handle:", "handle::64", "handle:full:3", "handle:full:1025", "handle:full:abc", "nt:foo", "nt:full:64:1" })
            AssertThrows<ArgumentException>(() => EnumeratorSpec.Parse(text), $"--enumerator '{text}' is rejected");

        AssertEqual("find", FsOptions.Parse(["x"]).Enumerator.Canonical, "the default enumerator is find");
```

with:

```csharp
        var good = new (string Text, string Canonical)[]
        {
            ("find", "find"), ("FIND", "find"), ("find:nolarge", "find:nolarge"),
            ("handle", "handle:full:64"), ("handle:idextd", "handle:idextd:64"), ("handle:idextd:4", "handle:idextd:4"), ("handle:full:1024", "handle:full:1024"),
            ("nt", "nt:dir:64"), ("nt:idextd", "nt:idextd:64"), ("nt:full:256", "nt:full:256"), ("NT:Dir:4", "nt:dir:4"),
            ("auto", "auto"), ("AUTO", "auto"),
        };
        foreach (var (text, canonical) in good) AssertEqual(canonical, EnumeratorSpec.Parse(text).Canonical, $"--enumerator {text}");
        foreach (var text in new[] { "", "bogus", "find:full", "find:nolarge:4", "handle:dir", "handle:", "handle::64", "handle:full:3", "handle:full:1025", "handle:full:abc", "nt:foo", "nt:full:64:1", "auto:full", "auto:64" })
            AssertThrows<ArgumentException>(() => EnumeratorSpec.Parse(text), $"--enumerator '{text}' is rejected");

        AssertEqual("find", FsOptions.Parse(["x"]).Enumerator.Canonical, "the default enumerator is still find (this branch does not change it)");
        AssertEqual("auto", EnumeratorSpec.Parse("auto").CreateFactory().Name, "auto resolves to a factory named auto");
        Assert(EnumeratorSpec.Parse("auto").CreateFactory() is AutoFactory, "auto's factory is really an AutoFactory");
```

- [ ] **Step 2: Run it to see it fail**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "enumerator|FAIL"
```

Expected: the `EnumeratorSpecs` test fails (`"auto"` is not accepted; `EnumeratorSpec.Default` unaffected —
this step should NOT fail on the default-is-find assertion, since nothing has changed that yet).

- [ ] **Step 3: Add `EnumeratorKind.Auto` and its parsing**

In `src\DirSizer.Fs\EnumeratorSpec.cs`, replace:

```csharp
enum EnumeratorKind { Find, Handle, Nt }
```

with:

```csharp
enum EnumeratorKind { Find, Handle, Nt, Auto }
```

Replace the `Canonical` property:

```csharp
    public string Canonical => Kind switch
    {
        EnumeratorKind.Find => LargeFetch ? "find" : "find:nolarge",
        EnumeratorKind.Handle => $"handle:{ClassName(Class)}:{BufferKiB}",
        _ => $"nt:{ClassName(Class)}:{BufferKiB}",
    };
```

with:

```csharp
    public string Canonical => Kind switch
    {
        EnumeratorKind.Find => LargeFetch ? "find" : "find:nolarge",
        EnumeratorKind.Handle => $"handle:{ClassName(Class)}:{BufferKiB}",
        EnumeratorKind.Auto => "auto",
        _ => $"nt:{ClassName(Class)}:{BufferKiB}",
    };
```

Replace the `Parse` method's `switch`:

```csharp
        switch (parts[0])
        {
            case "find":
                if (parts.Length == 1) return Default;
                if (parts.Length == 2 && parts[1] == "nolarge") return Default with { LargeFetch = false };
                throw new ArgumentException("--enumerator find takes no class or buffer size; use find or find:nolarge.");
            case "handle":
                return ParseBuffered(EnumeratorKind.Handle, parts, [EntryClass.Full, EntryClass.IdExtd], "handle: classes are full and idextd");
            case "nt":
                return ParseBuffered(EnumeratorKind.Nt, parts, [EntryClass.Dir, EntryClass.Full, EntryClass.IdExtd], "nt: classes are dir, full and idextd");
            default:
                throw new ArgumentException($"Unknown enumerator '{text}'. Use find, find:nolarge, handle[:class[:KiB]] or nt[:class[:KiB]].");
        }
```

with:

```csharp
        switch (parts[0])
        {
            case "find":
                if (parts.Length == 1) return new EnumeratorSpec(EnumeratorKind.Find, EntryClass.None, true, 0);
                if (parts.Length == 2 && parts[1] == "nolarge") return new EnumeratorSpec(EnumeratorKind.Find, EntryClass.None, false, 0);
                throw new ArgumentException("--enumerator find takes no class or buffer size; use find or find:nolarge.");
            case "handle":
                return ParseBuffered(EnumeratorKind.Handle, parts, [EntryClass.Full, EntryClass.IdExtd], "handle: classes are full and idextd");
            case "nt":
                return ParseBuffered(EnumeratorKind.Nt, parts, [EntryClass.Dir, EntryClass.Full, EntryClass.IdExtd], "nt: classes are dir, full and idextd");
            case "auto":
                if (parts.Length == 1) return new EnumeratorSpec(EnumeratorKind.Auto, EntryClass.None, false, 0);
                throw new ArgumentException("--enumerator auto takes no class or buffer size.");
            default:
                throw new ArgumentException($"Unknown enumerator '{text}'. Use find, find:nolarge, handle[:class[:KiB]], nt[:class[:KiB]] or auto.");
        }
```

**Important, easy to miss:** the original code's `"find"` case returned the shared `Default` singleton
(`return Default;` / `return Default with { LargeFetch = false };`) — safe only because `Default` was always
the `find` spec. Task 7 may later change `Default` to `Auto`. The replacement above **constructs the `find`
spec directly instead of referring to `Default`**, so parsing `"find"` keeps meaning `find` regardless of
what `Default` is at the time — this decoupling must happen in this task, before Task 7 exists, not be left
as a latent bug for it to trip over.

Replace the `CreateFactory` method:

```csharp
    // The find-first delegate is the self-test's injection point and only means something for `find`.
    public IEnumeratorFactory CreateFactory(FindFirstFn? findFirst = null) =>
        Kind == EnumeratorKind.Find ? new FindFirstFactory(LargeFetch, findFirst) : new BufferedFactory(this);
```

with:

```csharp
    // The find-first delegate is the self-test's injection point and only means something for `find` (and,
    // through it, for auto's internal secondary, which does not accept one — see the design spec).
    public IEnumeratorFactory CreateFactory(FindFirstFn? findFirst = null) => Kind switch
    {
        EnumeratorKind.Find => new FindFirstFactory(LargeFetch, findFirst),
        EnumeratorKind.Auto => new AutoFactory(),
        _ => new BufferedFactory(this),
    };
```

**Do not touch `EnumeratorSpec.Default`.** It stays `new(EnumeratorKind.Find, EntryClass.None, true, 0)`.

- [ ] **Step 4: Run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "enumerator|passed|FAIL"
```

Expected: `0 Error(s)`, `37 self-tests passed, 0 skipped.` (the `EnumeratorSpecs` test itself was extended, not
added, so the count is unchanged from Task 3's end).

- [ ] **Step 5: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs\EnumeratorSpec.cs src\DirSizer.Fs\SelfTests.Cli.cs
git commit -m @'
--enumerator=auto: explicitly selectable, resolves to AutoFactory. EnumeratorSpec.Default is unchanged (find)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 5: Whole-walk wiring and reporting

**Files:**
- Modify: `src\DirSizer.Fs\Walker.cs`
- Modify: `src\DirSizer.Fs\FsScanner.cs`
- Modify: `src\DirSizer.Fs\FsOutput.cs`

- [ ] **Step 1: Write the failing whole-walk test**

Add to `src\DirSizer.Fs\SelfTests.Enumerators.cs`, after `AutoFactoryWiring`:

```csharp
    static void WalkReportsFallbackWhenAutoTriggers()
    {
        using var tree = StandardTree();
        var (expected, fileCount) = Oracle(tree.Base);

        // A test-only factory shaped like AutoFactory but built from fakes: its very first Read() call fails
        // the way an unsupported class would; every worker shares the wrapper's state, exactly as AutoFactory
        // wires real workers, so the whole tree still gets read correctly, entirely through the secondary.
        var state = new FallbackState();
        IDirectoryEnumerator MakeWrapped() => new FallbackEnumerator(
            new FirstCallFailsEnumerator(state), new FindFirstFactory().Create(), state);
        var factory = new TestFallbackFactory(MakeWrapped);

        foreach (var workers in new[] { 1, 3, 8 })
        {
            var label = $"workers={workers}";
            var result = Scan(tree.Root, workers, files: true, top: 5, enumerators: factory);
            AssertSameTotals(expected, TotalsOf(result.Nodes), label);
            AssertEqual(fileCount, result.Counters.Files, $"{label}: files");
            AssertEqual(0L, result.Counters.Unreadable, $"{label}: nothing counted as failed because of the fallback");
            AssertEqual("find", result.Metrics.EnumeratorFallback, $"{label}: the fallback is reported");
            Assert(result.Metrics.EnumeratorFallbackReason!.Contains("87"), $"{label}: the reason mentions the error code");
        }

        // No trigger: both fields stay null.
        var clean = Scan(tree.Root, 4, enumerators: new FindFirstFactory());
        AssertEqual(null, clean.Metrics.EnumeratorFallback, "no fallback: null");
        AssertEqual(null, clean.Metrics.EnumeratorFallbackReason, "no fallback: null");
    }

    // Fails the first Read() call of the whole run (Complete/first-query/87), succeeds every call after that
    // (state.Triggered will be true by then, so FallbackEnumerator never calls this again in practice, but a
    // real success response is here too in case a worker's already-in-flight call reaches it).
    sealed class FirstCallFailsEnumerator(FallbackState state) : IDirectoryEnumerator
    {
        public ReadResult Read(string directoryPath, IEntrySink sink) =>
            state.Triggered ? new ReadResult(ReadOutcome.Complete, 0) : new ReadResult(ReadOutcome.Failed, Win32Find.ErrorInvalidParameter, true);
    }

    sealed class TestFallbackFactory(Func<IDirectoryEnumerator> create) : IEnumeratorFactory
    {
        readonly FallbackState _state = new();
        public string Name => "test-auto";
        public bool LargeFetch => false;
        public string? FallbackEnumerator => _state.Triggered ? "find" : null;
        public string? FallbackReason => _state.Triggered ? $"test trigger (error {Win32Find.ErrorInvalidParameter})" : null;
        public IDirectoryEnumerator Create() => create();
    }
```

Wait — `TestFallbackFactory` needs its own `_state` to answer `FallbackEnumerator`/`FallbackReason` for
`Walker.Run` to read after the join, but the `state` used inside `MakeWrapped()` (captured from the test
method) must be the *same* instance, or the factory's own view of "triggered" will never update. Fix: change
`TestFallbackFactory` to take the shared `FallbackState` directly instead of re-deriving it:

```csharp
    sealed class TestFallbackFactory(Func<IDirectoryEnumerator> create, FallbackState state) : IEnumeratorFactory
    {
        public string Name => "test-auto";
        public bool LargeFetch => false;
        public string? FallbackEnumerator => state.Triggered ? "find" : null;
        public string? FallbackReason => state.Triggered ? $"test trigger (error {Win32Find.ErrorInvalidParameter})" : null;
        public IDirectoryEnumerator Create() => create();
    }
```

and in `WalkReportsFallbackWhenAutoTriggers`, change the factory construction line to:

```csharp
        var factory = new TestFallbackFactory(MakeWrapped, state);
```

Register the test:

```csharp
        tests.Add(new("the whole walk reports enumerator_fallback when auto's primary is found unsupported", WalkReportsFallbackWhenAutoTriggers));
```

- [ ] **Step 2: Run it to see it fail to compile**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error"
```

Expected: `WalkResult`/`FsMetrics` have no `EnumeratorFallback`/`EnumeratorFallbackReason` members yet.

- [ ] **Step 3: `Walker.cs`**

Replace:

```csharp
sealed record WalkResult(
    DirNode[] Nodes,
    Counters Totals,
    FileHit[] Files,
    FileHit[] RootFiles,
    int PeakQueuedDirs,
    TimeSpan WalkTime,
    string Enumerator,
    bool LargeFetch,
    ReadResult RootRead,
    string[] ErrorSamples);
```

with:

```csharp
sealed record WalkResult(
    DirNode[] Nodes,
    Counters Totals,
    FileHit[] Files,
    FileHit[] RootFiles,
    int PeakQueuedDirs,
    TimeSpan WalkTime,
    string Enumerator,
    bool LargeFetch,
    string? EnumeratorFallback,
    string? EnumeratorFallbackReason,
    ReadResult RootRead,
    string[] ErrorSamples);
```

Replace the `return new WalkResult(...)` at the end of `Walker.Run`:

```csharp
        return new WalkResult(
            DirTable.Build(root, created),
            totals,
            files?.ToDescendingArray() ?? [],
            rootFiles.ToDescendingArray(),
            queue.PeakQueued,
            walkTime,
            factory.Name,
            factory.LargeFetch,
            RootRead,
            samples.ToArray());
```

with:

```csharp
        return new WalkResult(
            DirTable.Build(root, created),
            totals,
            files?.ToDescendingArray() ?? [],
            rootFiles.ToDescendingArray(),
            queue.PeakQueued,
            walkTime,
            factory.Name,
            factory.LargeFetch,
            factory.FallbackEnumerator,
            factory.FallbackReason,
            RootRead,
            samples.ToArray());
```

(Read at the same point as `factory.Name`/`factory.LargeFetch` already are — after every worker thread has
joined — so it is truthful, exactly like the existing `LargeFetch` fallback-to-off reporting.)

- [ ] **Step 4: `FsScanner.cs`**

Replace the `FsMetrics` record:

```csharp
sealed record FsMetrics(
    TimeSpan Open, TimeSpan Walk, TimeSpan Aggregation, TimeSpan Finalize, TimeSpan Total,
    TimeSpan EnumTotal, TimeSpan IdleTotal, int Workers, bool LargeFetch, string Enumerator, int PeakQueuedDirs,
    long Entries, long Directories, long Bytes, long ManagedAllocatedBytes, long PeakWorkingSetBytes)
```

with:

```csharp
sealed record FsMetrics(
    TimeSpan Open, TimeSpan Walk, TimeSpan Aggregation, TimeSpan Finalize, TimeSpan Total,
    TimeSpan EnumTotal, TimeSpan IdleTotal, int Workers, bool LargeFetch, string Enumerator,
    string? EnumeratorFallback, string? EnumeratorFallbackReason, int PeakQueuedDirs,
    long Entries, long Directories, long Bytes, long ManagedAllocatedBytes, long PeakWorkingSetBytes)
```

Replace the `FsMetrics` construction in `FsScanner.Scan`:

```csharp
        var metrics = new FsMetrics(
            open, walk.WalkTime, aggregation, finalize, total.Elapsed,
            TimeSpan.FromSeconds((double)totals.EnumTicks / Stopwatch.Frequency),
            TimeSpan.FromSeconds((double)totals.IdleTicks / Stopwatch.Frequency),
            settings.Workers, walk.LargeFetch, walk.Enumerator, walk.PeakQueuedDirs,
            totals.Entries, nodes.Length, totals.Bytes,
            GC.GetTotalAllocatedBytes() - allocatedAtStart, process.PeakWorkingSet64);
```

with:

```csharp
        var metrics = new FsMetrics(
            open, walk.WalkTime, aggregation, finalize, total.Elapsed,
            TimeSpan.FromSeconds((double)totals.EnumTicks / Stopwatch.Frequency),
            TimeSpan.FromSeconds((double)totals.IdleTicks / Stopwatch.Frequency),
            settings.Workers, walk.LargeFetch, walk.Enumerator, walk.EnumeratorFallback, walk.EnumeratorFallbackReason, walk.PeakQueuedDirs,
            totals.Entries, nodes.Length, totals.Bytes,
            GC.GetTotalAllocatedBytes() - allocatedAtStart, process.PeakWorkingSet64);
```

- [ ] **Step 5: Fix `SyntheticResult` in `SelfTests.Output.cs` now, in the same build as Step 4**

`FsMetrics`'s constructor arity just changed (Step 4), and `SyntheticResult` is its only other call site
(besides `FsScanner.Scan`, already updated). **This must happen before the next build**, not after — a build
attempted between Step 4 and this step would fail to compile, not merely fail a test, because
`SyntheticResult` still passes the old 16 positional arguments to what is now an 18-parameter constructor.

Replace:

```csharp
        var metrics = new FsMetrics(zero, TimeSpan.FromSeconds(1), zero, zero, TimeSpan.FromSeconds(1), zero, zero, 4, true, "find", 0, 30, 8, 1234, 0, 0);
```

with:

```csharp
        var metrics = new FsMetrics(zero, TimeSpan.FromSeconds(1), zero, zero, TimeSpan.FromSeconds(1), zero, zero, 4, true, "find", null, null, 0, 30, 8, 1234, 0, 0);
```

- [ ] **Step 6: Run the tests — this should compile *and* pass now**

The internal plumbing (`Walker` → `FsScanner` → `FsMetrics`) is complete end to end after Steps 3-5; the new
test reads `result.Metrics` directly, not JSON, so it does not need Step 7 (JSON/benchmark-line surface,
below) to pass.

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "fallback|passed|FAIL"
```

Expected: `0 Error(s)`, the new test's `ok` line, `38 self-tests passed, 0 skipped.` If either the build or
the new test fails here, the wiring in Steps 3-5 has a mistake — fix it now, before moving on to Step 7,
which is a separate concern (the JSON/benchmark-line surface) and cannot fix a plumbing bug underneath it.

- [ ] **Step 7: `FsOutput.cs` — JSON, benchmark line, stderr warning**

Replace the `JsonFsPerformance` record:

```csharp
sealed record JsonFsPerformance(double OpenMs, double WalkMs, double AggregationMs, double FinalizeMs, double OtherMs, double TotalMs, double PhaseSumMs, double EnumMsTotal, double IdleMsTotal, int Workers, bool LargeFetch, string Enumerator, int PeakQueuedDirs, long ManagedAllocatedBytes, long PeakWorkingSetBytes, double EntriesPerSec, double DirectoriesPerSec, double LogicalMibPerSec);
```

with:

```csharp
sealed record JsonFsPerformance(double OpenMs, double WalkMs, double AggregationMs, double FinalizeMs, double OtherMs, double TotalMs, double PhaseSumMs, double EnumMsTotal, double IdleMsTotal, int Workers, bool LargeFetch, string Enumerator, string? EnumeratorFallback, string? EnumeratorFallbackReason, int PeakQueuedDirs, long ManagedAllocatedBytes, long PeakWorkingSetBytes, double EntriesPerSec, double DirectoriesPerSec, double LogicalMibPerSec);
```

Replace the `JsonFsPerformance` construction in `ToJson`:

```csharp
                new JsonFsPerformance(
                    m.Open.TotalMilliseconds, m.Walk.TotalMilliseconds, m.Aggregation.TotalMilliseconds, m.Finalize.TotalMilliseconds,
                    m.Other.TotalMilliseconds, m.Total.TotalMilliseconds, m.PhaseSum.TotalMilliseconds,
                    m.EnumTotal.TotalMilliseconds, m.IdleTotal.TotalMilliseconds, m.Workers, m.LargeFetch, m.Enumerator, m.PeakQueuedDirs,
                    m.ManagedAllocatedBytes, m.PeakWorkingSetBytes, m.EntriesPerSec, m.DirectoriesPerSec, m.LogicalMibPerSec)),
```

with:

```csharp
                new JsonFsPerformance(
                    m.Open.TotalMilliseconds, m.Walk.TotalMilliseconds, m.Aggregation.TotalMilliseconds, m.Finalize.TotalMilliseconds,
                    m.Other.TotalMilliseconds, m.Total.TotalMilliseconds, m.PhaseSum.TotalMilliseconds,
                    m.EnumTotal.TotalMilliseconds, m.IdleTotal.TotalMilliseconds, m.Workers, m.LargeFetch, m.Enumerator, m.EnumeratorFallback, m.EnumeratorFallbackReason, m.PeakQueuedDirs,
                    m.ManagedAllocatedBytes, m.PeakWorkingSetBytes, m.EntriesPerSec, m.DirectoriesPerSec, m.LogicalMibPerSec)),
```

Replace `BenchmarkLine`:

```csharp
    static string BenchmarkLine(FsMetrics m) =>
        $"benchmark: workers={m.Workers}, enumerator={m.Enumerator}, large_fetch={(m.LargeFetch ? "on" : "off")}, open_ms={m.Open.TotalMilliseconds:F1}, walk_ms={m.Walk.TotalMilliseconds:F1}, " +
        $"aggregation_ms={m.Aggregation.TotalMilliseconds:F1}, finalize_ms={m.Finalize.TotalMilliseconds:F1}, other_ms={m.Other.TotalMilliseconds:F1}, " +
        $"total_ms={m.Total.TotalMilliseconds:F1}, phase_sum_ms={m.PhaseSum.TotalMilliseconds:F1}, enum_ms_total={m.EnumTotal.TotalMilliseconds:F1}, " +
        $"idle_ms_total={m.IdleTotal.TotalMilliseconds:F1}, peak_queued_dirs={m.PeakQueuedDirs}, entries_per_sec={m.EntriesPerSec:F0}, " +
        $"directories_per_sec={m.DirectoriesPerSec:F0}, logical_mib_per_sec={m.LogicalMibPerSec:F1}, managed_allocated={m.ManagedAllocatedBytes}, peak_working_set={m.PeakWorkingSetBytes}";
```

with:

```csharp
    static string BenchmarkLine(FsMetrics m)
    {
        var enumerator = m.Enumerator + (m.EnumeratorFallback is null ? "" : $" (fallback: {m.EnumeratorFallback}, reason={m.EnumeratorFallbackReason})");
        return $"benchmark: workers={m.Workers}, enumerator={enumerator}, large_fetch={(m.LargeFetch ? "on" : "off")}, open_ms={m.Open.TotalMilliseconds:F1}, walk_ms={m.Walk.TotalMilliseconds:F1}, " +
        $"aggregation_ms={m.Aggregation.TotalMilliseconds:F1}, finalize_ms={m.Finalize.TotalMilliseconds:F1}, other_ms={m.Other.TotalMilliseconds:F1}, " +
        $"total_ms={m.Total.TotalMilliseconds:F1}, phase_sum_ms={m.PhaseSum.TotalMilliseconds:F1}, enum_ms_total={m.EnumTotal.TotalMilliseconds:F1}, " +
        $"idle_ms_total={m.IdleTotal.TotalMilliseconds:F1}, peak_queued_dirs={m.PeakQueuedDirs}, entries_per_sec={m.EntriesPerSec:F0}, " +
        $"directories_per_sec={m.DirectoriesPerSec:F0}, logical_mib_per_sec={m.LogicalMibPerSec:F1}, managed_allocated={m.ManagedAllocatedBytes}, peak_working_set={m.PeakWorkingSetBytes}";
    }
```

(The benchmark line's own self-test, `TextOutput` in `SelfTests.Output.cs`, checks for
`"benchmark: workers=2, enumerator=find, large_fetch=on"` — `find` never has a fallback, so that substring
still appears unchanged; no test edit needed for this specific assertion, but Task 6 adds new tests for the
fallback-present case.)

Add the stderr warning in `Write`, right after the existing `Unreadable` warning block:

```csharp
        if (result.Counters.Unreadable > 0)
        {
            error.WriteLine($"warning: {result.Counters.Unreadable:N0} directories could not be read (denied {result.Counters.DirectoriesDenied:N0}, failed {result.Counters.DirectoriesFailed:N0}); the sizes are a lower bound.");
            foreach (var sample in result.ErrorSamples) error.WriteLine($"  failed: {sample}");
            var notListed = result.Counters.DirectoriesFailed - result.ErrorSamples.Length;
            if (notListed > 0) error.WriteLine($"  ... and {notListed:N0} more failed directories that are not listed");
        }
        if (options.Benchmark) error.WriteLine(BenchmarkLine(result.Metrics));
```

with:

```csharp
        if (result.Counters.Unreadable > 0)
        {
            error.WriteLine($"warning: {result.Counters.Unreadable:N0} directories could not be read (denied {result.Counters.DirectoriesDenied:N0}, failed {result.Counters.DirectoriesFailed:N0}); the sizes are a lower bound.");
            foreach (var sample in result.ErrorSamples) error.WriteLine($"  failed: {sample}");
            var notListed = result.Counters.DirectoriesFailed - result.ErrorSamples.Length;
            if (notListed > 0) error.WriteLine($"  ... and {notListed:N0} more failed directories that are not listed");
        }
        // Printed once, after the scan finishes (not at the instant the fallback triggers — there is no
        // mid-scan notification path from a worker thread to stderr, and this design does not add one).
        if (result.Metrics.EnumeratorFallback is not null)
            error.WriteLine($"warning: enumerator {result.Metrics.Enumerator} fell back to {result.Metrics.EnumeratorFallback}: {result.Metrics.EnumeratorFallbackReason}");
        if (options.Benchmark) error.WriteLine(BenchmarkLine(result.Metrics));
```

- [ ] **Step 8: Run everything**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, `38 self-tests passed, 0 skipped.` (unchanged from Step 6 — Step 7 added no new test,
only the JSON/benchmark/warning surface).

- [ ] **Step 9: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs\Walker.cs src\DirSizer.Fs\FsScanner.cs src\DirSizer.Fs\FsOutput.cs src\DirSizer.Fs\SelfTests.Enumerators.cs src\DirSizer.Fs\SelfTests.Output.cs
git commit -m @'
Report enumerator_fallback/enumerator_fallback_reason: JSON, benchmark line, one end-of-scan stderr warning

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 6: JSON field test, `--help`/README text, NativeAOT canary, real-hardware smoke check

**Files:**
- Modify: `src\DirSizer.Fs\SelfTests.Output.cs`
- Modify: `src\DirSizer.Fs\FsOptions.cs`
- Modify: `README.md`, `README-jp.md`

- [ ] **Step 1: Add a JSON-shape test for the two new fields**

In `src\DirSizer.Fs\SelfTests.Output.cs`, in `JsonOutput`, extend the `performance` field-presence loop.
Replace:

```csharp
        var performance = statistics.GetProperty("performance");
        foreach (var name in new[] { "open_ms", "walk_ms", "aggregation_ms", "finalize_ms", "other_ms", "total_ms", "phase_sum_ms", "enum_ms_total", "idle_ms_total", "workers", "large_fetch", "enumerator", "peak_queued_dirs", "managed_allocated_bytes", "peak_working_set_bytes", "entries_per_sec", "directories_per_sec", "logical_mib_per_sec" })
            Assert(performance.TryGetProperty(name, out _), $"performance.{name} is present");
```

with:

```csharp
        var performance = statistics.GetProperty("performance");
        foreach (var name in new[] { "open_ms", "walk_ms", "aggregation_ms", "finalize_ms", "other_ms", "total_ms", "phase_sum_ms", "enum_ms_total", "idle_ms_total", "workers", "large_fetch", "enumerator", "enumerator_fallback", "enumerator_fallback_reason", "peak_queued_dirs", "managed_allocated_bytes", "peak_working_set_bytes", "entries_per_sec", "directories_per_sec", "logical_mib_per_sec" })
            Assert(performance.TryGetProperty(name, out _), $"performance.{name} is present");
        AssertEqual(JsonValueKind.Null, performance.GetProperty("enumerator_fallback").ValueKind, "enumerator_fallback is null when nothing fell back");
        AssertEqual(JsonValueKind.Null, performance.GetProperty("enumerator_fallback_reason").ValueKind, "enumerator_fallback_reason is null when nothing fell back");
```

Add a new test, registered in `AddOutputTests`, that a triggered fallback shows up in JSON as non-null. Add to
`AddOutputTests`:

```csharp
        tests.Add(new("JSON reports a non-null enumerator_fallback when one happened", JsonReportsTriggeredFallback));
```

Add the method:

```csharp
    static void JsonReportsTriggeredFallback()
    {
        var m = SyntheticResult(0, 0, []).Metrics with { Enumerator = "auto", EnumeratorFallback = "find", EnumeratorFallbackReason = "handle:full:64 not supported here: The parameter is incorrect. (error 87)" };
        var result = SyntheticResult(0, 0, []) with { Metrics = m };
        var text = new StringWriter();
        var error = new StringWriter();
        FsOutput.Write(result, FsOptions.Parse([@"C:\r", "--json"]), text, error);
        using var document = JsonDocument.Parse(text.ToString());
        var performance = document.RootElement.GetProperty("statistics").GetProperty("performance");
        AssertEqual("auto", performance.GetProperty("enumerator").GetString(), "enumerator");
        AssertEqual("find", performance.GetProperty("enumerator_fallback").GetString(), "enumerator_fallback");
        Assert(performance.GetProperty("enumerator_fallback_reason").GetString()!.Contains("87"), "enumerator_fallback_reason");
        Assert(error.ToString().Contains("warning: enumerator auto fell back to find:"), "the stderr warning names both the requested and the fallback enumerator");
    }
```

(This test is specifically about the reporting plumbing with synthetic, realistic data — `auto` falling back
to `find` — not a real scan; the whole-walk test in Task 5 already covers `auto` end-to-end against a real
fixture.)

- [ ] **Step 2: Run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, `39 self-tests passed, 0 skipped.`

- [ ] **Step 3: `--help` text**

In `src\DirSizer.Fs\FsOptions.cs`, `PrintHelp`, replace:

```
            --enumerator=NAME[:CLASS[:KiB]]
                        Advanced, for comparisons: how directories are read. find (default),
                        find:nolarge, handle[:full|idextd[:KiB]], nt[:dir|full|idextd[:KiB]]
                        (idextd is not supported on exFAT)
```

with:

```
            --enumerator=NAME[:CLASS[:KiB]]
                        Advanced, for comparisons: how directories are read. find (default),
                        find:nolarge, handle[:full|idextd[:KiB]], nt[:dir|full|idextd[:KiB]]
                        (idextd is not supported on exFAT), or auto (handle:full:64, falling
                        back to find if that class is not supported here)
```

- [ ] **Step 4: READMEs**

In `README.md`, replace:

```
The path may be any directory, not only a drive root. `--enumerator=NAME[:CLASS[:KiB]]` is an advanced option for comparisons: it chooses the API that reads the directories (`find`, the default, is `FindFirstFileExW`; `handle` is `GetFileInformationByHandleEx`; `nt` is `NtQueryDirectoryFileEx`, experimental); the measurements are in [docs/design_fs.md](docs/design_fs.md), "Enumerator comparison (P4)". The `idextd` class does not work on exFAT. Exit codes:
```

with:

```
The path may be any directory, not only a drive root. `--enumerator=NAME[:CLASS[:KiB]]` is an advanced option for comparisons: it chooses the API that reads the directories (`find`, the default, is `FindFirstFileExW`; `handle` is `GetFileInformationByHandleEx`; `nt` is `NtQueryDirectoryFileEx`, experimental; `auto` is `handle:full:64` with an automatic fallback to `find` if that information class is not supported on the file system being scanned); the measurements are in [docs/design_fs.md](docs/design_fs.md), "Enumerator comparison (P4)" and "Default enumerator with a fallback". The `idextd` class does not work on exFAT. Exit codes:
```

In `README-jp.md`, replace:

```
パスはドライブのルートに限らず、任意のディレクトリを指定できます。`--enumerator=NAME[:CLASS[:KiB]]` は比較用の上級者向けオプションで、ディレクトリを読む API を選びます（`find` が既定で `FindFirstFileExW`、`handle` は `GetFileInformationByHandleEx`、`nt` は `NtQueryDirectoryFileEx`（実験的））。測定結果は [docs/design_fs.md](docs/design_fs.md)（英語）の "Enumerator comparison (P4)" にあります。`idextd` クラスは exFAT では使えません。終了コード:
```

with:

```
パスはドライブのルートに限らず、任意のディレクトリを指定できます。`--enumerator=NAME[:CLASS[:KiB]]` は比較用の上級者向けオプションで、ディレクトリを読む API を選びます（`find` が既定で `FindFirstFileExW`、`handle` は `GetFileInformationByHandleEx`、`nt` は `NtQueryDirectoryFileEx`（実験的）、`auto` は `handle:full:64` を使い、対応していないファイルシステムでは自動的に `find` に切り替わります）。測定結果は [docs/design_fs.md](docs/design_fs.md)（英語）の "Enumerator comparison (P4)" と "Default enumerator with a fallback" にあります。`idextd` クラスは exFAT では使えません。終了コード:
```

- [ ] **Step 5: NativeAOT publish canary**

```powershell
$env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish src\DirSizer.Fs\DirSizer.Fs.csproj -c Release -r win-x64 2>&1 | Select-Object -Last 3
$exe = (Resolve-Path .\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe).Path
& $exe --self-test | Select-Object -Last 1; "exit: $LASTEXITCODE"
```

Expected: publish succeeds, `39 self-tests passed, 0 skipped.`, `exit: 0`.

- [ ] **Step 6: Real-hardware smoke check: `auto` against quiescent trees and exFAT, compared to `find`**

```powershell
$specs = 'find', 'auto'
.\scripts\Compare-Enumerators.ps1 -Path 'T:\', 'C:\Program Files\dotnet', 'C:\Windows\System32\drivers' -Equal -Specs $specs -Tool $exe
if (Test-Path 'D:\') { .\scripts\Compare-Enumerators.ps1 -Path 'D:\' -Equal -Specs $specs -Tool $exe } else { "D: not present, skipped" }
$j = & $exe 'C:\Windows\System32\drivers' --json --top=1 --workers 8 --enumerator=auto | ConvertFrom-Json
"enumerator={0} fallback={1} reason={2}" -f $j.statistics.performance.enumerator, $j.statistics.performance.enumerator_fallback, $j.statistics.performance.enumerator_fallback_reason
```

Expected: `EQUAL` for `auto` against `find` on every tree, including `D:\` (exFAT) — `handle:full:64` already
works there (see the benchmark branch's capability matrix), so `auto` is not expected to trigger anywhere in
this environment; `enumerator=auto`, `fallback=` (empty/null), `reason=` (empty/null). If `D:\` is absent,
that line is skipped and said so, not silently omitted.

- [ ] **Step 7: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs\SelfTests.Output.cs src\DirSizer.Fs\FsOptions.cs README.md README-jp.md
git commit -m @'
--help and README text for auto; JSON field tests for enumerator_fallback/enumerator_fallback_reason

NativeAOT publish and a real-hardware smoke check (auto vs find, quiescent trees and exFAT) both verified
manually; see the task notes for the commands and expected output.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 7: Rollout gate — re-measure, and only then decide the default

**Files:** none until the decision is written (see Step 3).

Do this only after Tasks 1-6 are committed and green.

- [ ] **Step 1: The fixed, reproducible re-measurement procedure**

```powershell
$exe = (Resolve-Path .\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe).Path
& $exe 'C:\' --json --top=1 --workers 8 --enumerator=find | Out-Null   # one untimed warm-up pass
.\scripts\Compare-Enumerators.ps1 -Path 'C:\' -Time -Rounds 10 -Workers 8 -Specs find, handle:full:64, auto -Tool $exe
```

This alternates `find`, `handle:full:64` and `auto` with the order rotated each of the 10 rounds (the script
already does this — see `scripts\Compare-Enumerators.ps1`'s `-Time` mode), on the same NativeAOT build used
throughout this plan. Keep the full printed table (min/median/max `walk_ms` for all three, plus the `vs find`
lines).

- [ ] **Step 2: Apply the pass condition exactly**

Let `mh` = the printed median `walk_ms` of `handle:full:64`, `mf` = the printed median `walk_ms` of `find`.
Compute `mh / mf`. The rule passes when this is `<= 0.90`.

- [ ] **Step 3a: If it passes — flip the default**

In `src\DirSizer.Fs\EnumeratorSpec.cs`, change:

```csharp
    public static EnumeratorSpec Default { get; } = new(EnumeratorKind.Find, EntryClass.None, true, 0);
```

to:

```csharp
    public static EnumeratorSpec Default { get; } = new(EnumeratorKind.Auto, EntryClass.None, false, 0);
```

Then fix the one self-test that pins the old default: in `src\DirSizer.Fs\SelfTests.Cli.cs`, in
`EnumeratorSpecs`, replace:

```csharp
        AssertEqual("find", FsOptions.Parse(["x"]).Enumerator.Canonical, "the default enumerator is still find (this branch does not change it)");
```

with:

```csharp
        AssertEqual("auto", FsOptions.Parse(["x"]).Enumerator.Canonical, "the default enumerator is now auto (the rollout gate confirmed the bar)");
```

Rebuild, retest, publish, retest the native build (same commands as Task 6 Step 5) — expect `39 self-tests
passed, 0 skipped.` in both, then commit:

```powershell
Normalize-FsProject
git add src\DirSizer.Fs\EnumeratorSpec.cs src\DirSizer.Fs\SelfTests.Cli.cs
git commit -m @'
Default enumerator is now auto: the re-measurement confirmed >=10% median walk_ms on W1

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

- [ ] **Step 3b: If it does not pass — no code change**

State plainly (in the report to the user, and in the doc update of Step 4) that `EnumeratorSpec.Default`
remains `find`, with the measured `mh/mf` ratio. Skip to Step 4.

- [ ] **Step 4: Update the documents with the measured numbers and the decision (both outcomes)**

In `docs\design_fs.md`, append a new section after "Enumerator comparison (P4)"'s "Results" subsection (i.e.
after the existing "Known differences and limits" paragraph added by the benchmark branch):

```markdown
### Default enumerator with a fallback

Specified in [superpowers/specs/2026-09-22-fs-enumerator-default-design.md](superpowers/specs/2026-09-22-fs-enumerator-default-design.md).
`--enumerator=auto` (B, `handle:full:64`, falling back to A, `find`, only when the very first directory-listing
query for a directory fails with `ERROR_INVALID_PARAMETER`) is available regardless of the outcome below.

Re-measurement (10 rounds, one untimed warm-up pass, `find`/`handle:full:64`/`auto` alternated with the order
rotated each round, workers=8, real `C:\`, NativeAOT build): [FILL IN: the printed min/median/max walk_ms for
all three, and the enum_ms_total medians]. `mh/mf` = [FILL IN THE RATIO]. auto vs handle:full:64: [FILL IN:
confirm the wrapper's overhead, if any].

**Decision:** [FILL IN: "the pass condition (mh <= 0.90 * mf) is met; EnumeratorSpec.Default is now auto" OR
"the pass condition is not met (mh/mf = X); EnumeratorSpec.Default stays find; auto remains available via
--enumerator=auto"].
```

In `docs\roadmap.md`, extend the `## P4 - Directory enumerators` entry (added by the benchmark branch) with
one more `[x]` line naming this branch, the re-measured ratio, and the decision, in the same style as the
existing lines there.

Replace every `[FILL IN...]` placeholder with the actual Step 1 output before committing — a plan step that
still contains `[FILL IN` when committed is incomplete, not done.

```powershell
Normalize-FsProject
git add docs\design_fs.md docs\roadmap.md
git commit -m @'
Record the auto rollout-gate re-measurement and the default-enumerator decision

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 8: Final verification

**Files:** none changed.

- [ ] **Step 1: Confirm nothing outside the allowed paths changed**

```powershell
git diff --stat master -- src/DirSizer.Core src/DirSizer.Bulk src/DirSizer.Fsctl src/DirSizer.Inspect src/DirSizer.Compare src/Shared
```

Expected: no output.

- [ ] **Step 2: Full solution build and every tool's self-test**

```powershell
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 3
dotnet .\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
dotnet .\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll --self-test | Select-Object -Last 1
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test | Select-Object -Last 1
dotnet .\artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.dll --self-test | Select-Object -Last 1
dotnet .\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`; all four other tools' self-tests pass unchanged; `dirsizer.dll` prints `39 self-tests
passed, 0 skipped.`

- [ ] **Step 3: Report**

Summarize to the user: the final self-test count, whether the rollout gate passed and what `mh/mf` was, and
that this is ready for `superpowers:finishing-a-development-branch` (ask before merging or pushing — do not
do either unasked).

---

## Self-review against the spec

| Spec section | Task |
| --- | --- |
| `ReadResult.FirstQuery` (query-ordinal fact, not a zero-entries guarantee) | 1 |
| `FallbackState` (one-way, first-writer-wins) and `FallbackEnumerator` (first-query-only, error 87 only) | 2 |
| In-flight `Read` allowed to finish with B when the flag flips | 2 (`FallbackEnumerator`'s own check-once-at-entry structure already gives this; no extra code needed) |
| `IEnumeratorFactory.FallbackEnumerator`/`FallbackReason`, null for every other factory | 3 |
| `AutoFactory`, `TestOnlyState`, wiring test with two instances | 3 |
| `EnumeratorKind.Auto`, `Parse("auto")`, `Canonical`, `CreateFactory`; `Default` untouched | 4 |
| `WalkResult`/`FsMetrics` gain the two fields, read after the join like `LargeFetch` | 5 |
| JSON `enumerator_fallback`/`enumerator_fallback_reason`, `large_fetch=off` for auto, benchmark line, one end-of-scan stderr warning | 5, 6 |
| The seven self-test cases from the spec's "Testing" section | 1 (FirstQuery default), 2 (cases 1-3, plus 4-5 for Denied/other-error), 3 (AutoFactory wiring), 4 (`Parse("auto")`), 5 (whole-walk) — all present |
| Real-hardware smoke check (auto vs find, quiescent trees + exFAT) | 6 |
| `--help`/README text | 6 |
| Rollout gate: fixed 10-round procedure, warm-up, exact pass condition, conditional default change, doc updates either way | 7 |
| Nothing outside `src\DirSizer.Fs`/`docs`/`README*.md` changes | 8 |
