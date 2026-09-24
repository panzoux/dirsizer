# dirsizer.exe: the index strategy (--keep-index, --changes, --drop-index) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dirsizer.exe` answers from the persistent per-volume index once the user opts in per drive with
`--keep-index`, for the drive root and any folder on an NTFS drive, lists what changed with `--changes`, deletes
the index with `--drop-index`, and behaves byte for byte as v0.7.0 without those options.

**Architecture:** The spec is [2026-09-24-index-in-dirsizer-design.md](../specs/2026-09-24-index-in-dirsizer-design.md).
A new `IndexStrategy` comes first in `ScanStrategySelector` on local NTFS paths. It runs dirsizer-index's own
`IndexRunner`, compiled into `dirsizer.exe` with `<Compile Include>` (the repository's convention; see how
`src\DirSizer\DirSizer.csproj` compiles `..\DirSizer.Bulk\BulkIntegration.cs`). `IndexRunner` first gets a request
record instead of dirsizer-index's `IndexOptions`, a lock file, and a "do not create" mode. `ChangeReporter` leaves
out the index directory's own change.

**Tech Stack:** C# 12 / .NET 8, NativeAOT, the repository's `--self-test` harnesses (no test framework), PowerShell 7
end-to-end scripts on the disposable `T:` volume.

---

## Ground rules for the executing agent

- Work on branch `feature/index-in-dirsizer` (it exists and holds the spec). Never push unless asked. End commit
  messages with the attribution line from the session's instructions.
- Run commands with the **PowerShell tool** from `C:\Users\user\source\repos\panzoux\dirsizer`. Use absolute paths
  in .NET calls (`[IO.File]::...`): .NET's current directory is not PowerShell's location.
- **Elevation:** Tasks 8, 9, 11 (steps 2-3) open volumes. Check with
  `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')`.
  If `False`, do not skip or fake those steps: give the user the commands and continue with the rest.
- `T:` is the disposable test volume labelled `NTFSTEST`; the scripts refuse any other label. On `C:`, write only
  what Task 9 lists (a temporary index in `%LOCALAPPDATA%\dirsizer\index`, deleted at the end).
- **Line endings:** CRLF, UTF-8 without BOM. Script-based rewrites have turned files into LF before. Run this before
  every commit that touches `src\`:

```powershell
function Normalize-Src {
    foreach ($f in (Get-ChildItem src -Recurse -File -Include *.cs, *.csproj, *.manifest, *.sln | ForEach-Object FullName)) {
        $t = [IO.File]::ReadAllText($f); $x = $t -replace "`r?`n", "`r`n"
        if ($x -ne $t) { [IO.File]::WriteAllText($f, $x, [Text.UTF8Encoding]::new($false)) }
    }
}
Normalize-Src
```

- **Build and test:**
  - Build: `dotnet build DirSizer.sln -c Release`. Expected: 0 warnings, 0 errors.
  - Index tests: `dotnet artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll --self-test`. Expected:
    `P0 self-tests passed.`, then `N self-tests passed, 0 skipped.` with N given in each task (41 before Task 1).
  - dirsizer.exe tests: `dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test`. Expected:
    `N self-tests passed, 0 skipped.` (7 before Task 4).
- No LINQ in production code (repository rule).
- **Mutation checks:** where a step says "mutation check", make the stated break, rebuild, confirm the named test
  fails, then restore the file exactly (`git diff` must show only the intended change afterwards).

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `src\DirSizer.Index\IndexRunner.cs` | `IndexRequest`, `IndexUnavailableException`; `Run(IndexRequest)`, lock, no-create mode, ignored directory | 1, 2, 3 |
| `src\DirSizer.Index\IndexOptions.cs` | `ToRequest()` | 1 |
| `src\DirSizer.Index\IndexProgram.cs` | passes the request; reports `IndexUnavailableException` | 1, 2 |
| `src\DirSizer.Index\IndexLock.cs` (new) | one process at a time per index | 2 |
| `src\DirSizer.Index\ChangeReport.cs` | ignored directory; `ChangeText`, `JsonChanges`, `JsonChange` (moved from IndexOutput) | 3 |
| `src\DirSizer.Index\IndexOutput.cs` | uses the moved change output | 3 |
| `src\DirSizer.Index\IndexSelfTests*.cs` | tests for the above | 1, 2, 3 |
| `src\DirSizer\DirSizer.csproj` | compiles the index sources | 4 |
| `src\DirSizer\IndexStrategy.cs` (new) | the `index` strategy | 4 |
| `src\DirSizer\ScanStrategySelector.cs` | candidates per path; `LocalDrive`, `IsDriveRoot` | 4, 5 |
| `src\DirSizer\UnifiedCliOptions.cs` | `--keep-index`, `--changes`, `--drop-index`, help | 5 |
| `src\DirSizer\Program.cs` | wiring | 5, 7 |
| `src\DirSizer\UnifiedOutput.cs` | changes, index notes, benchmark | 6 |
| `src\DirSizer\IndexDrop.cs` (new) | `--drop-index` | 7 |
| `src\DirSizer\SelfTests.cs`, `src\DirSizer\SelfTests.Index.cs` (new) | tests | 4-7 |
| `scripts\Test-DirsizerIndex.ps1` (new) | end-to-end on T: | 8 |
| `docs\design_index.md`, `docs\superpowers\specs\2026-09-23-unified-scan-strategy-design.md`, `README.md`, `README-jp.md`, `docs\roadmap.md` | docs | 10 |
| `Directory.Build.props`, `release-notes.md` | 0.8.0 | 11 |

---

### Task 1: `IndexRequest`: IndexRunner no longer takes dirsizer-index's options

**Files:**
- Modify: `src\DirSizer.Index\IndexRunner.cs`, `src\DirSizer.Index\IndexOptions.cs`, `src\DirSizer.Index\IndexProgram.cs`, `src\DirSizer.Index\IndexSelfTests.cs`

- [ ] **Step 1: Write the failing test**

In `src\DirSizer.Index\IndexSelfTests.cs`, add after `OptionsRejectBadInput`:

```csharp
    static void OptionsBecomeARequest()
    {
        var request = IndexOptions.Parse(["D:\\x", "--top=7", "--no-save", "--verify", "--rebuild", "--changes", "--index-dir=X:\\idx"]).ToRequest();
        AssertEqual(new IndexRequest("D:\\x", 7, "X:\\idx", NoSave: true, Verify: true, Rebuild: true, Changes: true), request, "every option reaches the request");
        AssertEqual(new IndexRequest("C:\\", 25, IndexFile.DefaultDirectory), IndexOptions.Parse(["C:"]).ToRequest(), "defaults");
        Assert(request.CreateIfMissing, "dirsizer-index always creates an index where there is none");
    }
```

Add to the test list, after the `"options: bad input is rejected"` line:

```csharp
            new("options: they become an IndexRequest", OptionsBecomeARequest),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL to compile, `IndexRequest` and `ToRequest` do not exist.

- [ ] **Step 3: Implement**

In `src\DirSizer.Index\IndexRunner.cs`, add before `static class IndexRunner`:

```csharp
// What one run is asked to do. dirsizer-index builds it from its options (IndexOptions.ToRequest); dirsizer.exe's
// IndexStrategy builds it from its own. CreateIfMissing = false: no index is created where there is none.
sealed record IndexRequest(
    string Target, int Top, string IndexDirectory,
    bool NoSave = false, bool Verify = false, bool Rebuild = false, bool Changes = false, bool CreateIfMissing = true);
```

In the same file, change `public static IndexRun Run(IndexOptions options)` to `public static IndexRun Run(IndexRequest request)`,
then replace every `options.` in the file with `request.` (they are all inside `Run`: `Target` twice, `Rebuild`,
`Changes`, `IndexDirectory`, `NoSave`, `Top` twice, `Verify`). Check: `Select-String -Path src\DirSizer.Index\IndexRunner.cs -Pattern 'options\.'` prints nothing.

In `src\DirSizer.Index\IndexOptions.cs`, add after `public string IndexDirectory { get; private set; } = IndexFile.DefaultDirectory;`:

```csharp

    public IndexRequest ToRequest() => new(Target, Top, IndexDirectory, NoSave, Verify, Rebuild, Changes);
```

In `src\DirSizer.Index\IndexProgram.cs`, replace `var run = IndexRunner.Run(options);` with
`var run = IndexRunner.Run(options.ToRequest());`.

- [ ] **Step 4: Run the tests**

Build, then the index tests. Expected: `42 self-tests passed, 0 skipped.`

- [ ] **Step 5: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: IndexRunner takes an IndexRequest instead of the command-line options"
```

---

### Task 2: A lock per index, and a mode that does not create an index

**Files:**
- Create: `src\DirSizer.Index\IndexLock.cs`, `src\DirSizer.Index\IndexSelfTests.Lock.cs`
- Modify: `src\DirSizer.Index\IndexRunner.cs`, `src\DirSizer.Index\IndexProgram.cs`, `src\DirSizer.Index\IndexSelfTests.cs`

- [ ] **Step 1: Write the failing test**

`src\DirSizer.Index\IndexSelfTests.Lock.cs`:

```csharp
static partial class IndexSelfTests
{
    static void IndexLockAllowsOneHolderAtATime()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-lock-test-" + Guid.NewGuid().ToString("N"));
        var indexPath = Path.Combine(directory, "0000000000000001.dsix");
        try
        {
            using (var first = IndexLock.Acquire(indexPath, TimeSpan.Zero))
            {
                Assert(first is not null, "the first holder gets the lock");
                Assert(File.Exists(IndexLock.PathFor(indexPath)), "the lock file exists while it is held");
                var waited = System.Diagnostics.Stopwatch.StartNew();
                Assert(IndexLock.Acquire(indexPath, TimeSpan.FromMilliseconds(300)) is null, "a second holder does not get it");
                Assert(waited.ElapsedMilliseconds >= 250, $"it waited for the timeout ({waited.ElapsedMilliseconds} ms)");
            }
            Assert(!File.Exists(IndexLock.PathFor(indexPath)), "the lock file is deleted when it is released");
            using var again = IndexLock.Acquire(indexPath, TimeSpan.Zero);
            Assert(again is not null, "after the release it can be taken again");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
```

Add to the test list, after the `"options: they become an IndexRequest"` line:

```csharp
            new("lock: one holder at a time; a second one gives up after the timeout", IndexLockAllowsOneHolderAtATime),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL to compile, `IndexLock` does not exist.

- [ ] **Step 3: Implement `IndexLock.cs`**

```csharp
using System.Diagnostics;

// One process at a time loads, updates and saves an index (docs\design_index.md, "In dirsizer.exe"). The lock is a file
// next to the index, opened without sharing and deleted when it is closed; Windows closes it if the process dies.
static class IndexLock
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    const int SharingViolation = 32;

    public static string PathFor(string indexPath) => indexPath + ".lock";

    // Null if someone else (another process, or another handle in this one) still holds it after `timeout`.
    public static FileStream? Acquire(string indexPath, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(PathFor(indexPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) == SharingViolation)
            {
                if (waited.Elapsed >= timeout) return null;
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (waited.Elapsed < timeout)
            {
                Thread.Sleep(100);   // the previous holder's file is still being deleted
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Expected: `43 self-tests passed, 0 skipped.`

- [ ] **Step 5: Use the lock and the no-create mode in `IndexRunner`**

In `src\DirSizer.Index\IndexRunner.cs`:

1. Replace the `IndexRequest` record with:

```csharp
// What one run is asked to do. dirsizer-index builds it from its options (IndexOptions.ToRequest); dirsizer.exe's
// IndexStrategy builds it from its own. CreateIfMissing = false: IndexUnavailableException instead of creating an index
// where there is none. LockTimeout: how long to wait for another process's run on the same index (default 5 s).
sealed record IndexRequest(
    string Target, int Top, string IndexDirectory,
    bool NoSave = false, bool Verify = false, bool Rebuild = false, bool Changes = false, bool CreateIfMissing = true,
    TimeSpan? LockTimeout = null);

// The index cannot be used on this run, and nothing has been read or written yet: there is none and the request does not
// create one, or another process holds it. dirsizer-index reports it as an error; dirsizer.exe moves to the next strategy.
sealed class IndexUnavailableException(string message) : Exception(message);
```

2. Replace

```csharp
        var indexPath = IndexFile.PathFor(request.IndexDirectory, identity.SerialNumber);
```

with

```csharp
        var indexPath = IndexFile.PathFor(request.IndexDirectory, identity.SerialNumber);
        if (!request.CreateIfMissing && !File.Exists(indexPath))
            throw new IndexUnavailableException($"there is no index of {volume} yet");
        // Held until the run ends: nothing else loads, updates or saves this index in between.
        using var indexLock = IndexLock.Acquire(indexPath, request.LockTimeout ?? IndexLock.DefaultTimeout)
            ?? throw new IndexUnavailableException($"another process is using the index of {volume} ({IndexLock.PathFor(indexPath)})");
```

In `src\DirSizer.Index\IndexProgram.cs`, in the `catch` filter, replace `or InvalidDataException)` with
`or InvalidDataException or IndexUnavailableException)`.

- [ ] **Step 6: Build and run the tests**

Expected: 0 warnings, `43 self-tests passed, 0 skipped.` (The runner's use of the lock is checked on a real volume in
Task 8.)

- [ ] **Step 7: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: one process at a time per index (lock file); a request can refuse to create an index"
```

---

### Task 3: `--changes` leaves out the index's own directory; shared change output

**Files:**
- Modify: `src\DirSizer.Index\ChangeReport.cs`, `src\DirSizer.Index\IndexOutput.cs`, `src\DirSizer.Index\IndexRunner.cs`, `src\DirSizer.Index\IndexSelfTests.Changes.cs`, `src\DirSizer.Index\IndexSelfTests.cs`

- [ ] **Step 1: Write the failing test**

Append inside the class in `src\DirSizer.Index\IndexSelfTests.Changes.cs`:

```csharp
    static void ChangesLeaveOutTheIndexOwnDirectory()
    {
        var source = SampleVolume();
        source.Records[33] = DirBytes(33, 30, "idx");               // the index's directory, below A
        source.Records[44] = FileBytes(44, 33, "x.dsix", 50);
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records[44] = FileBytes(44, 33, "x.dsix", 80);       // the index file grew by 30 ...
        source.Records.Remove(40);                                  // ... and b.bin (100) was deleted
        IndexUpdater.Apply(before.Records, [new UsnChange(44, 0, 0), new UsnChange(40, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var plain = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        AssertEqual("T:\\A\\idx=50->80", Changes(plain.Grew), "without ignoring it, the index directory grows");
        var report = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10, ignored: 33);
        // A: 170 -> 100 on disk, 70 without the index's +30; the root: 173 -> 103, 73 without it. Equal changes by record number.
        AssertEqual("T:\\=173->73,T:\\A=170->70,T:\\A\\B=100->0", Changes(report.Shrunk), "only b.bin's change");
        AssertEqual("", Changes(report.Grew), "the index directory is not listed");
        AssertEqual(-100L, report.RootAfter - report.RootBefore, "the root's change without the index's own");
    }
```

Add to the test list, after the `"changes: a directory moved into the queried one counts from 0"` line:

```csharp
            new("changes: the index's own directory is left out, also from the directories above it", ChangesLeaveOutTheIndexOwnDirectory),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL to compile, `Compare` has no parameter `ignored`.

- [ ] **Step 3: Implement in `ChangeReport.cs`**

Replace the whole `Compare` method (from its comment through its closing brace) with:

```csharp
    // Compares every directory at or below `scope` now with the same directory in the snapshot. A directory's change
    // includes everything below it, so the parents of a deleted folder are listed too. `ignored`: a directory whose own
    // change is left out -- the index's own directory when it is on this volume, whose files change on every save. It
    // is not listed, and its change is subtracted from every directory above it.
    public static ChangeReport Compare(IndexSnapshot before, VolumeIndex after, string volume, FileRecord scope, int top, ulong? ignored = null)
    {
        var records = after.Records;
        var scopeNumber = scope.Reference.RecordNumber;
        var (ignoredChange, aboveIgnored) = IgnoredChange(before, records, ignored);
        var present = new HashSet<ulong>();
        var candidates = new List<Candidate>();
        var inScope = new List<FileRecord> { scope };
        foreach (var record in SubtreeQuery.Descendants(records, scopeNumber))
        {
            if (record.IsDirectory) inScope.Add(record);
        }
        foreach (var directory in inScope)
        {
            var number = directory.Reference.RecordNumber;
            present.Add(number);
            if (number == ignored) continue;
            var old = SameDirectoryBefore(before, directory, scopeNumber) ? before.Directories[number].Size : 0;
            var now = SizeNow(directory, aboveIgnored, ignoredChange);
            if (now != old) candidates.Add(new Candidate(number, false, old, now));
        }
        foreach (var (number, entry) in before.Directories)
        {
            if (entry.Size == 0 || number == ignored || !before.IsUnder(number, scopeNumber)) continue;
            if (present.Contains(number) && records[number].Reference == entry.Reference) continue;
            candidates.Add(new Candidate(number, true, entry.Size, 0));
        }
        var rootBefore = SameDirectoryBefore(before, scope, scopeNumber) ? before.Directories[scopeNumber].Size : 0;
        return new ChangeReport(before.WrittenUtc, rootBefore, SizeNow(scope, aboveIgnored, ignoredChange),
            Select(candidates, top, shrunk: true, before, records, volume),
            Select(candidates, top, shrunk: false, before, records, volume));
    }

    static long SizeNow(FileRecord directory, HashSet<ulong> aboveIgnored, long ignoredChange) =>
        aboveIgnored.Contains(directory.Reference.RecordNumber) ? directory.Size - ignoredChange : directory.Size;

    // The ignored directory's change since the snapshot, and the directories above it now (its parent up to the root).
    static (long Change, HashSet<ulong> Above) IgnoredChange(IndexSnapshot before, Dictionary<ulong, FileRecord> records, ulong? ignored)
    {
        var above = new HashSet<ulong>();
        if (ignored is not { } number || !records.TryGetValue(number, out var directory) || !directory.IsDirectory || directory.DisplayName is null)
            return (0, above);
        var old = before.Directories.TryGetValue(number, out var entry) && entry.Reference == directory.Reference ? entry.Size : 0;
        var current = directory;
        for (var depth = 0; depth < 10000 && current.Reference.RecordNumber != SubtreeQuery.RootRecord; depth++)
        {
            if (!RecordLookup.TryGetDirectory(records, current.Parent, out var parent)) break;
            above.Add(parent.Reference.RecordNumber);
            current = parent;
        }
        return (directory.Size - old, above);
    }
```

At the end of `ChangeReport.cs`, add the change output that dirsizer-index and dirsizer.exe will share (moved from
`IndexOutput.cs`, unchanged in what it prints):

```csharp

// The change report as text and JSON, shared by dirsizer-index and dirsizer.exe.
static class ChangeText
{
    public static void Write(ChangeReport changes, int top, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"Changes since {changes.SinceUtc:yyyy-MM-dd HH:mm:ss} UTC (when the previous index was written)");
        output.WriteLine("Change\tBefore\tAfter\tPath");
        output.WriteLine($"{Signed(changes.RootAfter - changes.RootBefore)}\t{changes.RootBefore}\t{changes.RootAfter}\t(the directory queried)");
        WriteList($"Shrunk (largest {top})", changes.Shrunk, output);
        WriteList($"Grew (largest {top})", changes.Grew, output);
    }

    static void WriteList(string title, DirectoryChange[] list, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine(title);
        output.WriteLine("Change\tBefore\tAfter\tPath");
        foreach (var change in list) output.WriteLine($"{Signed(change.Delta)}\t{change.Before}\t{change.After}\t{change.Path}");
    }

    // Byte counts without separators, so the columns can be parsed; the sign is always shown.
    static string Signed(long value) => value.ToString("+0;-0;0");
}

sealed record JsonChanges(string SinceUtc, long RootBefore, long RootAfter, long RootDelta, JsonChange[] Shrunk, JsonChange[] Grew)
{
    public static JsonChanges From(ChangeReport changes) => new(
        changes.SinceUtc.ToString("o"), changes.RootBefore, changes.RootAfter, changes.RootAfter - changes.RootBefore,
        Array.ConvertAll(changes.Shrunk, JsonChange.From), Array.ConvertAll(changes.Grew, JsonChange.From));
}

sealed record JsonChange(string Path, long Before, long After, long Delta)
{
    public static JsonChange From(DirectoryChange change) => new(change.Path, change.Before, change.After, change.Delta);
}
```

In `src\DirSizer.Index\IndexOutput.cs`:
- Delete the methods `WriteChanges`, `WriteChangeList`, `Signed`, `ToJson(ChangeReport changes)` and
  `ToJson(DirectoryChange change)`, and the two records `JsonChanges` and `JsonChange` at the end of the file.
- Replace `if (run.Changes is { } changes) WriteChanges(changes, options.Top, output);` with
  `if (run.Changes is { } changes) ChangeText.Write(changes, options.Top, output);`.
- Replace `run.Changes is null ? null : ToJson(run.Changes));` with `run.Changes is null ? null : JsonChanges.From(run.Changes));`.

In `src\DirSizer.Index\IndexRunner.cs`, replace

```csharp
        var changes = before is null ? null : ChangeReporter.Compare(before, index, volume, root, request.Top);
```

with

```csharp
        var changes = before is null ? null : ChangeReporter.Compare(before, index, volume, root, request.Top, IndexDirectoryRecord(index, request.IndexDirectory));
```

and add this method to `IndexRunner`, after `Run`:

```csharp
    // The index directory's MFT record when it is on this volume and in the index (its own change is left out of
    // --changes), otherwise null.
    static ulong? IndexDirectoryRecord(VolumeIndex index, string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var (serial, reference) = PathResolver.Identify(directory);
        return serial == (uint)index.Identity.SerialNumber && index.Records.TryGetValue(reference.RecordNumber, out var record) && record.Reference == reference
            ? reference.RecordNumber
            : null;
    }
```

- [ ] **Step 4: Run the tests**

Expected: `44 self-tests passed, 0 skipped.` The existing test `"output: changes in text and JSON; a note when there
is no earlier index"` must still pass: it proves the moved output prints the same.

- [ ] **Step 5: Mutation check**

In `SizeNow`, return `directory.Size` unconditionally. Expected: `"changes: the index's own directory is left out..."`
fails (A would be 170->100). Restore.

- [ ] **Step 6: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: --changes leaves out the index directory's own change; change output shared"
```

---

### Task 4: `IndexStrategy` in dirsizer.exe

**Files:**
- Modify: `src\DirSizer\DirSizer.csproj`, `src\DirSizer\ScanStrategySelector.cs`, `src\DirSizer\SelfTests.cs`
- Create: `src\DirSizer\IndexStrategy.cs`, `src\DirSizer\SelfTests.Index.cs`

- [ ] **Step 1: Compile the index sources into dirsizer.exe**

In `src\DirSizer\DirSizer.csproj`, after `<Compile Include="..\Shared\FsctlScanner.cs" />`, add:

```xml
    <!-- The index strategy runs dirsizer-index's own code (docs\superpowers\specs\2026-09-24-index-in-dirsizer-design.md). -->
    <Compile Include="..\DirSizer.Inspect\RunList.cs" />
    <Compile Include="..\DirSizer.Index\VolumeIndex.cs" />
    <Compile Include="..\DirSizer.Index\IndexFile.cs" />
    <Compile Include="..\DirSizer.Index\IndexLock.cs" />
    <Compile Include="..\DirSizer.Index\UsnJournal.cs" />
    <Compile Include="..\DirSizer.Index\IndexVerifier.cs" />
    <Compile Include="..\DirSizer.Index\SubtreeQuery.cs" />
    <Compile Include="..\DirSizer.Index\IndexRunner.cs" />
    <Compile Include="..\DirSizer.Index\RecordSource.cs" />
    <Compile Include="..\DirSizer.Index\IndexUpdater.cs" />
    <Compile Include="..\DirSizer.Index\IndexValidity.cs" />
    <Compile Include="..\DirSizer.Index\PathResolver.cs" />
    <Compile Include="..\DirSizer.Index\ChangeReport.cs" />
```

Build. Expected: 0 warnings, 0 errors (a trial build before this plan found no clashing type names; its only error
was `IndexOptions`, removed in Task 1). dirsizer.exe tests: `7 self-tests passed, 0 skipped.`

- [ ] **Step 2: Write the failing tests**

In `src\DirSizer\SelfTests.cs`, change `static class DirSizerSelfTests` to `static partial class DirSizerSelfTests`,
and add to the test list after the last entry:

```csharp
            new("index strategy: unavailable without volume access or an index; --keep-index without access is an error", IndexStrategyIsUnavailableWithoutAccessOrIndex),
            new("index strategy: the request it makes, and a drive root reported as the drive", IndexStrategyMakesTheRightRequest),
```

Create `src\DirSizer\SelfTests.Index.cs`:

```csharp
using System.ComponentModel;

// Tests of the index strategy and its options and output. The strategy's volume probe and its run are replaced by fakes,
// so nothing here opens a volume or needs elevation. The index code itself is tested by dirsizer-index --self-test.
static partial class DirSizerSelfTests
{
    static UnifiedScanOptions Options() => new(25, false, true, false, 0);

    static IndexRun SampleIndexRun(string volume, string mode = "incremental", ChangeReport? changes = null) =>
        new(FixedResult("index") with { Volume = volume }, mode, mode == "full" ? "no saved index" : null, "X:\\idx\\0000000000000001.dsix",
            123L * 1024 * 1024, true, true, 10, 7, 1000, null, null, new IndexTimings(), mode == "full" ? "full" : "delta", 0, 0, changes);

    static void AssertThrowsUnavailable(Action action, string what)
    {
        try
        {
            action();
        }
        catch (StrategyUnavailableException)
        {
            return;
        }
        throw new Exception($"{what}: expected StrategyUnavailableException, nothing was thrown");
    }

    static void AssertContains(string text, string fragment, string what)
    {
        if (!text.Contains(fragment, StringComparison.Ordinal)) throw new Exception($"{what}: expected text containing \"{fragment}\", got \"{text}\"");
    }

    static void IndexStrategyIsUnavailableWithoutAccessOrIndex()
    {
        var denied = new IndexStrategy(false, false, _ => throw new Win32Exception(5), _ => throw new Exception("must not run"));
        AssertThrowsUnavailable(() => denied.Scan("C:\\", Options()), "no volume access");
        Assert(denied.UnavailableReason is not null && denied.LastRun is null, "the reason is kept, there is no run");

        var none = new IndexStrategy(false, false, _ => { }, _ => throw new IndexUnavailableException("there is no index of C: yet"));
        AssertThrowsUnavailable(() => none.Scan("C:\\", Options()), "no index");
        AssertEqual("there is no index of C: yet", none.UnavailableReason, "the reason");

        var keep = new IndexStrategy(true, false, _ => throw new Win32Exception(5), _ => throw new Exception("must not run"));
        try
        {
            keep.Scan("C:\\", Options());
            throw new Exception("--keep-index without volume access: expected UnauthorizedAccessException, nothing was thrown");
        }
        catch (UnauthorizedAccessException exception)
        {
            AssertContains(exception.Message, "--keep-index needs an administrator terminal", "the message");
        }
    }

    static void IndexStrategyMakesTheRightRequest()
    {
        IndexRequest? asked = null;
        string? probed = null;
        var strategy = new IndexStrategy(true, true, drive => probed = drive, request => { asked = request; return SampleIndexRun("C:\\"); });
        var result = strategy.Scan("C:\\", new UnifiedScanOptions(7, false, true, false, 0));
        AssertEqual("C:", probed, "the drive is probed");
        AssertEqual(new IndexRequest("C:\\", 7, IndexFile.DefaultDirectory, Changes: true, CreateIfMissing: true), asked, "the request");
        AssertEqual("index", result.Strategy, "strategy");
        AssertEqual("C:", result.Volume, "a drive root is reported as the drive, as mft reports it");
        Assert(strategy.LastRun is not null, "the run is kept for the output");

        strategy.Scan("C:", Options());
        AssertEqual("C:\\", asked!.Target, "a bare drive letter means the whole drive, not the current directory on it");

        var subtree = new IndexStrategy(false, false, _ => { }, request => { asked = request; return SampleIndexRun("C:\\Users"); });
        AssertEqual("C:\\Users", subtree.Scan("C:\\Users", Options()).Volume, "a folder keeps its path");
        AssertEqual("C:\\Users", asked!.Target, "the folder is the target");
        Assert(!asked.CreateIfMissing, "without --keep-index no index is created");
    }
}
```

- [ ] **Step 3: Run to see it fail**

Build. Expected: FAIL to compile, `IndexStrategy` does not exist.

- [ ] **Step 4: Implement**

Create `src\DirSizer\IndexStrategy.cs`:

```csharp
using System.ComponentModel;

// The persistent index (docs\design_index.md) as one of dirsizer.exe's strategies: used on a local NTFS drive once
// --keep-index has created an index of it (docs\superpowers\specs\2026-09-24-index-in-dirsizer-design.md). Unavailable --
// the selector moves on -- without an administrator token, without an index of the drive (unless --keep-index), or while
// another process holds the index. Everything after that is IndexRunner, dirsizer-index's own code path; its errors are
// real errors, not a reason to try the next strategy. The probe and the run can be replaced for the self-tests.
sealed class IndexStrategy(bool keepIndex, bool changes, Action<string>? probeVolume = null, Func<IndexRequest, IndexRun>? runIndex = null) : IScanStrategy
{
    readonly Action<string> probe = probeVolume ?? ProbeVolume;
    readonly Func<IndexRequest, IndexRun> run = runIndex ?? IndexRunner.Run;

    public string Name => "index";
    public bool KeepIndex => keepIndex;

    // The run that answered (null if the index was not used), and why it was not used.
    public IndexRun? LastRun { get; private set; }
    public string? UnavailableReason { get; private set; }

    public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var drive = ScanStrategySelector.LocalDrive(rootPath)
            ?? throw Unavailable(new ArgumentException($"{rootPath} is not on a local drive"));
        try
        {
            probe(drive);
        }
        catch (Win32Exception cause)
        {
            if (keepIndex) throw new UnauthorizedAccessException($"--keep-index needs an administrator terminal ({cause.Message}).", cause);
            throw Unavailable(cause);
        }
        // A drive root ("C:" or "C:\") is the whole drive; IndexRunner needs "C:\" for that ("C:" alone is the current
        // directory on C:).
        var driveRoot = ScanStrategySelector.IsDriveRoot(rootPath) ? DriveRoot.Validate(rootPath) : null;
        IndexRun indexRun;
        try
        {
            indexRun = run(new IndexRequest(driveRoot is null ? rootPath : driveRoot + "\\", options.Top, IndexFile.DefaultDirectory,
                Changes: changes, CreateIfMissing: keepIndex));
        }
        catch (IndexUnavailableException cause)
        {
            throw Unavailable(cause);
        }
        LastRun = indexRun;
        // A drive root is reported as typed ("C:"), like the mft and fsctl strategies; the index code reports "C:\".
        return driveRoot is null ? indexRun.Result : indexRun.Result with { Volume = driveRoot };
    }

    StrategyUnavailableException Unavailable(Exception cause)
    {
        UnavailableReason = cause.Message;
        return new StrategyUnavailableException("index", cause);
    }

    static void ProbeVolume(string drive)
    {
        using var volume = BulkNative.OpenVolume($"\\\\.\\{drive}");
    }
}
```

In `src\DirSizer\ScanStrategySelector.cs`, add these two methods inside the class, after `CandidateStrategies`:

```csharp
    // "C:" for a path on a local drive letter, null otherwise (a share, a device path).
    public static string? LocalDrive(string rootPath)
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(rootPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return root is { Length: 3 } && root[1] == ':' && root[2] == '\\' ? root[..2].ToUpperInvariant() : null;
    }

    public static bool IsDriveRoot(string rootPath)
    {
        try
        {
            DriveRoot.Validate(rootPath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
```

- [ ] **Step 5: Run the tests**

Build. Expected: 0 warnings; dirsizer.exe `9 self-tests passed, 0 skipped.`; dirsizer-index still 44.

- [ ] **Step 6: Mutation check**

In `IndexStrategy.Scan`, replace the last line with `return indexRun.Result;`. Expected: `"index strategy: the
request it makes..."` fails (`volume` C:\ instead of C:). Restore.

- [ ] **Step 7: Commit**

```powershell
Normalize-Src
git add src\DirSizer
git commit -m "dirsizer: IndexStrategy runs the persistent index (not selected yet)"
```

---

### Task 5: Options, selection and wiring

**Files:**
- Modify: `src\DirSizer\UnifiedCliOptions.cs`, `src\DirSizer\ScanStrategySelector.cs`, `src\DirSizer\Program.cs`, `src\DirSizer\SelfTests.cs`, `src\DirSizer\SelfTests.Index.cs`

- [ ] **Step 1: Write the failing tests**

Append inside the class in `src\DirSizer\SelfTests.Index.cs`:

```csharp
    static string Names(IScanStrategy[] strategies) => string.Join(',', Array.ConvertAll(strategies, strategy => strategy.Name));

    static void IndexOptionsAreParsed()
    {
        var keep = UnifiedCliOptions.Parse([@"C:\", "--keep-index", "--changes"]);
        Assert(keep.KeepIndex && keep.Changes && !keep.DropIndex, "--keep-index --changes");
        Assert(UnifiedCliOptions.Parse([@"C:\", "--drop-index"]).DropIndex, "--drop-index");
        var defaults = UnifiedCliOptions.Parse([@"C:\"]);
        Assert(!defaults.KeepIndex && !defaults.Changes && !defaults.DropIndex, "all off by default");
        AssertThrows(() => UnifiedCliOptions.Parse([@"C:\", "--drop-index", "--keep-index"]), "--drop-index with --keep-index");
        AssertThrows(() => UnifiedCliOptions.Parse([@"C:\", "--drop-index", "--changes"]), "--drop-index with --changes");
    }

    static void CandidatesPutTheIndexFirstOnNtfs()
    {
        var index = new IndexStrategy(false, false);
        AssertEqual("index,mft,fsctl,filesystem", Names(ScanStrategySelector.CandidateStrategies(@"C:\", index, _ => true)), "NTFS drive root");
        AssertEqual("index,filesystem", Names(ScanStrategySelector.CandidateStrategies(@"C:\Users", index, _ => true)), "NTFS folder");
        AssertEqual("filesystem", Names(ScanStrategySelector.CandidateStrategies(@"C:\", index, _ => false)), "not NTFS");
        AssertEqual("filesystem", Names(ScanStrategySelector.CandidateStrategies(@"\\server\share\x", index, _ => true)), "a share");
        AssertThrows(() => ScanStrategySelector.CandidateStrategies(@"\\server\share\x", new IndexStrategy(true, false), _ => true), "--keep-index on a share");
        AssertThrows(() => ScanStrategySelector.CandidateStrategies(@"C:\", new IndexStrategy(true, false), _ => false), "--keep-index on a drive that is not NTFS");
        AssertEqual("C:", ScanStrategySelector.LocalDrive(@"c:\Users"), "LocalDrive: the drive in upper case");
        AssertEqual((string?)null, ScanStrategySelector.LocalDrive(@"\\server\share"), "LocalDrive: a share has none");
    }
```

In `src\DirSizer\SelfTests.cs`, add to the test list:

```csharp
            new("options: --keep-index, --changes, --drop-index; --drop-index stands alone", IndexOptionsAreParsed),
            new("selector: the index first on local NTFS paths; mft and fsctl only on a drive root", CandidatesPutTheIndexFirstOnNtfs),
```

and change the first test, which today runs the production selector (with the index first, its result would depend
on whether this machine has an index of the temporary folder's drive). In `ScanStrategySelectorPicksFileSystem`,
replace the line

```csharp
        var result = ScanStrategySelector.Scan(tree.Root, new UnifiedScanOptions(25, false, true, false, 0));
```

with

```csharp
        var strategies = ScanStrategySelector.CandidateStrategies(tree.Root, new IndexStrategy(false, false), _ => false);
        var result = ScanStrategySelector.Scan(tree.Root, new UnifiedScanOptions(25, false, true, false, 0), strategies);
```

and rename its list entry to
`new("selector on a path that is not NTFS picks filesystem", ScanStrategySelectorPicksFileSystem),`.

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL to compile (`KeepIndex`, `DropIndex`, `Changes`, the new `CandidateStrategies` signature).

- [ ] **Step 3: Options**

In `src\DirSizer\UnifiedCliOptions.cs`:
- After `public int Workers { get; private set; }   // 0 = automatic` add:

```csharp
    public bool KeepIndex { get; private set; }
    public bool DropIndex { get; private set; }
    public bool Changes { get; private set; }
```

- After `if (arg == "--json") { result.Json = true; continue; }` add:

```csharp
            if (arg == "--keep-index") { result.KeepIndex = true; continue; }
            if (arg == "--drop-index") { result.DropIndex = true; continue; }
            if (arg == "--changes") { result.Changes = true; continue; }
```

- After the `if (!result.Help && !result.SelfTest && result.Root.Length == 0) ...` line add:

```csharp
        if (result.DropIndex && (result.KeepIndex || result.Changes))
            throw new ArgumentException("--drop-index only deletes the index; it cannot be combined with --keep-index or --changes.");
```

- In `PrintHelp`, replace the usage line with
  `Usage: dirsizer <path> [--top=N] [--files] [--dirs] [--json] [--workers N] [--strict] [--keep-index] [--changes] [--drop-index]`,
  add after the `--strict` line:

```text
            --keep-index Keep an index of this drive (local NTFS, administrator terminal): later runs on the
                        drive or any folder on it are answered from the index (see below)
            --changes   Also list the folders that shrank or grew since the previous run that used the index
            --drop-index Delete the index of this path's drive, and do nothing else
```

  replace the paragraph `A path that is not a whole drive always uses the filesystem strategy (the other two only read an
  entire volume).` (two lines) with:

```text
            A path that is not a whole drive uses the filesystem strategy (the other two only read an entire
            volume), unless the drive has an index.

            Index: nothing is written unless --keep-index is given. With it, the first run reads the whole
            drive and saves an index in %LOCALAPPDATA%\dirsizer\index (about 125 bytes per file and folder:
            112 MiB for a drive with 924,000 of them; the run prints the path and size). From then on every
            run on that drive from an administrator terminal, for the drive or any folder on it, reads the
            index and only what the NTFS change journal reports as changed (strategy=index), with the NTFS
            semantics of the whole-drive methods. From a normal terminal the index is not used or changed.
            The index lists every file and folder name on the drive. --drop-index deletes it.
```

- [ ] **Step 4: Selection**

In `src\DirSizer\ScanStrategySelector.cs`, replace

```csharp
    public static UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options) =>
        Scan(rootPath, options, CandidateStrategies(rootPath));
```

with

```csharp
    public static UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options, IndexStrategy index) =>
        Scan(rootPath, options, CandidateStrategies(rootPath, index, VolumeInfo.IsNtfs));
```

and replace the whole `CandidateStrategies` method with:

```csharp
    // The index first on any local NTFS path; mft and fsctl only on a drive root (they read a whole drive); the directory
    // walk always last. isNtfs is replaceable for the self-tests.
    internal static IScanStrategy[] CandidateStrategies(string rootPath, IndexStrategy index, Func<string, bool> isNtfs)
    {
        var drive = LocalDrive(rootPath);
        if (drive is null || !isNtfs(drive))
        {
            if (index.KeepIndex) throw new ArgumentException($"{rootPath}: --keep-index works on local NTFS drives only.");
            return [new FileSystemStrategy()];
        }
        if (IsDriveRoot(rootPath)) return [index, new MftStrategy(), new FsctlStrategy(), new FileSystemStrategy()];
        return [index, new FileSystemStrategy()];
    }
```

Also update the class comment at the top of the file: replace its three lines with
`// See the unified-scan-strategy design spec, "ScanStrategySelector", and the index design spec
// (2026-09-24-index-in-dirsizer-design.md): the candidates depend on the path; the try/catch loop does not.`

- [ ] **Step 5: Wiring**

In `src\DirSizer\Program.cs`, replace

```csharp
    var result = ScanStrategySelector.Scan(options.Root, options.ToUnifiedScanOptions());
```

with

```csharp
    var index = new IndexStrategy(options.KeepIndex, options.Changes);
    var result = ScanStrategySelector.Scan(options.Root, options.ToUnifiedScanOptions(), index);
```

and in the `catch` filter replace `exception is IOException or UnauthorizedAccessException or ArgumentException)` with
`exception is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or InvalidDataException)`
(the index code reports volume and file problems with these two; before, they would have ended the process with a
stack trace).

- [ ] **Step 6: Run the tests**

Build. Expected: 0 warnings; dirsizer.exe `11 self-tests passed, 0 skipped.`

- [ ] **Step 7: Commit**

```powershell
Normalize-Src
git add src\DirSizer
git commit -m "dirsizer: --keep-index, --changes, --drop-index options; the index strategy first on local NTFS paths"
```

---

### Task 6: Output: changes, what the index did, benchmark

**Files:**
- Modify: `src\DirSizer\UnifiedOutput.cs`, `src\DirSizer\Program.cs`, `src\DirSizer\SelfTests.cs`, `src\DirSizer\SelfTests.Index.cs`

- [ ] **Step 1: Write the failing tests**

Append inside the class in `src\DirSizer\SelfTests.Index.cs`:

```csharp
    static IndexStrategy RanIndex(IndexRun run)
    {
        var strategy = new IndexStrategy(false, true, _ => { }, _ => run);
        strategy.Scan(@"C:\", Options());
        return strategy;
    }

    static void OutputShowsWhatTheIndexDid()
    {
        var report = new ChangeReport(new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc), 123, 53,
            [new DirectoryChange("C:\\A", 120, 20)], [new DirectoryChange("C:\\C", 0, 30)]);
        var index = RanIndex(SampleIndexRun("C:", "full", report));
        var text = new StringWriter();
        var error = new StringWriter();
        UnifiedOutput.Write(FixedResult("index"), UnifiedCliOptions.Parse([@"C:\", "--changes", "--benchmark"]), text, error, index);
        AssertContains(text.ToString(), "-100\t120\t20\tC:\\A", "text: a shrunk row");
        AssertContains(error.ToString(), "index: full scan (no saved index)", "the reason for the full scan");
        AssertContains(error.ToString(), "index: saved X:\\idx\\0000000000000001.dsix (123.0 MiB)", "the save, with its size");
        AssertContains(error.ToString(), "index_mode=full, index_save=full", "the benchmark line");

        var json = new StringWriter();
        UnifiedOutput.Write(FixedResult("index"), UnifiedCliOptions.Parse([@"C:\", "--json", "--changes"]), json, new StringWriter(), index);
        using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
        AssertEqual(-70L, document.RootElement.GetProperty("changes").GetProperty("root_delta").GetInt64(), "json changes.root_delta");
    }

    static void OutputWithoutTheIndexIsUnchanged()
    {
        var plain = new StringWriter();
        UnifiedOutput.Write(FixedResult("mft"), UnifiedCliOptions.Parse([@"C:\", "--json"]), plain, new StringWriter());
        var withUnusedIndex = new StringWriter();
        var error = new StringWriter();
        UnifiedOutput.Write(FixedResult("mft"), UnifiedCliOptions.Parse([@"C:\", "--json"]), withUnusedIndex, error, new IndexStrategy(false, false));
        AssertEqual(plain.ToString(), withUnusedIndex.ToString(), "an index that was not used adds nothing to stdout");
        AssertEqual("", error.ToString(), "nor to stderr");
        Assert(!plain.ToString().Contains("changes"), "no changes property without --changes");

        var unavailable = new IndexStrategy(false, true, _ => { }, _ => throw new IndexUnavailableException("there is no index of C: yet"));
        try { unavailable.Scan(@"C:\", Options()); } catch (StrategyUnavailableException) { }
        var note = new StringWriter();
        UnifiedOutput.Write(FixedResult("mft"), UnifiedCliOptions.Parse([@"C:\", "--changes"]), new StringWriter(), note, unavailable);
        AssertContains(note.ToString(), "index: not used on this run (there is no index of C: yet)", "why --changes had no effect");
        AssertContains(note.ToString(), "changes: there is no earlier index", "the changes note");
    }
```

In `src\DirSizer\SelfTests.cs`, add to the test list:

```csharp
            new("output: changes, the full scan's reason, the save and the benchmark fields of the index", OutputShowsWhatTheIndexDid),
            new("output: unchanged when the index was not used; a note when --changes had nothing to compare with", OutputWithoutTheIndexIsUnchanged),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL to compile, `UnifiedOutput.Write` takes no `IndexStrategy`.

- [ ] **Step 3: Implement in `UnifiedOutput.cs`**

1. Change the signature to
   `public static void Write(UnifiedScanResult result, UnifiedCliOptions options, TextWriter output, TextWriter error, IndexStrategy? index = null)`
   and add as its first line `var run = index?.LastRun;`.
2. Replace
   `output.WriteLine(JsonSerializer.Serialize(ToJson(result, options.Top), UnifiedJsonContext.Default.JsonUnifiedOutput));` with
   `output.WriteLine(JsonSerializer.Serialize(ToJson(result, options.Top, run?.Changes), UnifiedJsonContext.Default.JsonUnifiedOutput));`
3. After the `output.WriteLine($"strategy={result.Strategy} path=...` line (inside the text branch) add:
   `if (run?.Changes is { } changes) ChangeText.Write(changes, options.Top, output);`
4. Replace the benchmark block

```csharp
        if (options.Benchmark)
        {
            var fallback = result.StrategyFallback is null ? "" : $" (fallback: {result.StrategyFallback}, reason={result.StrategyFallbackReason})";
            error.WriteLine($"benchmark: strategy={result.Strategy}{fallback}, total_ms={result.TotalMs:F1}");
        }
```

with

```csharp
        WriteIndexNotes(index, options, error);
        if (options.Benchmark)
        {
            var fallback = result.StrategyFallback is null ? "" : $" (fallback: {result.StrategyFallback}, reason={result.StrategyFallbackReason})";
            var indexFields = run is null ? "" : $", index_mode={run.Mode}, index_save={run.SaveKind}";
            error.WriteLine($"benchmark: strategy={result.Strategy}{fallback}{indexFields}, total_ms={result.TotalMs:F1}");
        }
```

5. Replace the `ToJson` method with:

```csharp
    static JsonUnifiedOutput ToJson(UnifiedScanResult result, int top, ChangeReport? changes) => new(
        result.Volume, top, "logical",
        new JsonUnifiedItem(result.Root.Path, result.Root.Size),
        Items(result.RootChildren), Items(result.Directories), Items(result.Files),
        new JsonUnifiedStatistics(result.DirectoriesScanned, result.Unreadable, result.FileCount, result.ErrorSamples,
            new JsonUnifiedPerformance(result.Strategy, result.StrategyFallback, result.StrategyFallbackReason, result.TotalMs)),
        changes is null ? null : JsonChanges.From(changes));

    // What the index did, when it matters to the user: a full scan and why, a save of the whole index (it is large), a
    // result that could not be saved, and why --keep-index or --changes had no effect on this run.
    static void WriteIndexNotes(IndexStrategy? index, UnifiedCliOptions options, TextWriter error)
    {
        var run = index?.LastRun;
        if (run is not null)
        {
            if (run.Mode == "full" && run.RebuildReason is not null) error.WriteLine($"index: full scan ({run.RebuildReason})");
            if (run.Mode == "full" && run.Saved)
                error.WriteLine($"index: saved {run.IndexPath} ({run.IndexBytes / 1048576.0:F1} MiB); later runs on this drive use it (--drop-index deletes it)");
            if (!run.Stable) error.WriteLine("warning: the MFT changed while it was read, so the index was not saved; the next run scans again");
        }
        else if ((options.KeepIndex || options.Changes) && index?.UnavailableReason is { } reason)
        {
            error.WriteLine($"index: not used on this run ({reason})");
        }
        if (options.Changes && run?.Changes is null) error.WriteLine("changes: there is no earlier index of this drive to compare with");
    }
```

6. Replace the `JsonUnifiedOutput` record with:

```csharp
sealed record JsonUnifiedOutput(string Volume, int Top, string SizeMode, JsonUnifiedItem Root, JsonUnifiedItem[] RootChildren, JsonUnifiedItem[] Directories, JsonUnifiedItem[] Files, JsonUnifiedStatistics Statistics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonChanges? Changes = null);
```

In `src\DirSizer\Program.cs`, replace `UnifiedOutput.Write(result, options, Console.Out, Console.Error);` with
`UnifiedOutput.Write(result, options, Console.Out, Console.Error, index);`.

- [ ] **Step 4: Run the tests**

Build. Expected: 0 warnings; dirsizer.exe `13 self-tests passed, 0 skipped.`

- [ ] **Step 5: Commit**

```powershell
Normalize-Src
git add src\DirSizer
git commit -m "dirsizer: --changes output and notes on what the index did"
```

---

### Task 7: `--drop-index`

**Files:**
- Create: `src\DirSizer\IndexDrop.cs`
- Modify: `src\DirSizer\Program.cs`, `src\DirSizer\SelfTests.cs`, `src\DirSizer\SelfTests.Index.cs`

- [ ] **Step 1: Write the failing test**

Append inside the class in `src\DirSizer\SelfTests.Index.cs`:

```csharp
    static void DropIndexDeletesOnlyThatDrivesFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-drop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (serial, _) = PathResolver.Identify(Path.GetPathRoot(directory)!);
            var mine = $"ABCD1234{serial:X8}.dsix";
            var other = $"ABCD1234{serial + 1:X8}.dsix";
            foreach (var name in new[] { mine, mine + ".delta", mine + ".tmp", other, "notes.txt" }) File.WriteAllText(Path.Combine(directory, name), "x");
            var output = new StringWriter();
            AssertEqual(0, IndexDrop.Run(directory, directory, output), "exit code");
            var left = new List<string>();
            foreach (var file in Directory.GetFiles(directory)) left.Add(Path.GetFileName(file));
            left.Sort(StringComparer.Ordinal);
            AssertEqual($"{other},notes.txt", string.Join(',', left), "only this drive's index files are deleted");
            AssertContains(output.ToString(), "deleted ", "each deleted file is named");
            var again = new StringWriter();
            IndexDrop.Run(directory, directory, again);
            AssertContains(again.ToString(), "no index of ", "nothing left to delete");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
```

In `src\DirSizer\SelfTests.cs`, add to the test list:

```csharp
            new("--drop-index deletes the index files of the path's drive and nothing else", DropIndexDeletesOnlyThatDrivesFiles),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL to compile, `IndexDrop` does not exist.

- [ ] **Step 3: Implement `src\DirSizer\IndexDrop.cs`**

```csharp
// --drop-index: deletes the index of a path's drive (docs\superpowers\specs\2026-09-24-index-in-dirsizer-design.md).
// Index files are named by the 64-bit NTFS serial, which only a volume handle (elevation) can read. Windows gives the low
// 32 bits to anyone (GetFileInformationByHandle); they are the last 8 hex digits of the name, so the drive's files are
// found without elevation. Two drives whose serials share the low 32 bits would both lose their index.
static class IndexDrop
{
    public static int Run(string path, string directory, TextWriter output)
    {
        var drive = ScanStrategySelector.LocalDrive(path) ?? throw new ArgumentException($"{path}: --drop-index works on local drives only.");
        var (serial, _) = PathResolver.Identify(drive + "\\");
        var serialHex = serial.ToString("X8");
        var deleted = 0;
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(file);
                // <16 hex digits>.dsix, and the .delta, .tmp and .lock files next to it.
                if (name.Length < 21 || !name.AsSpan(8, 8).Equals(serialHex, StringComparison.OrdinalIgnoreCase)
                    || !name.AsSpan(16).StartsWith(".dsix", StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(file);
                output.WriteLine($"deleted {file}");
                deleted++;
            }
        }
        if (deleted == 0) output.WriteLine($"no index of {drive} in {directory}");
        return 0;
    }
}
```

In `src\DirSizer\Program.cs`, add as the first line inside the `try` block:

```csharp
    if (options.DropIndex) return IndexDrop.Run(options.Root, IndexFile.DefaultDirectory, Console.Out);
```

- [ ] **Step 4: Run the tests**

Build. Expected: 0 warnings; dirsizer.exe `14 self-tests passed, 0 skipped.`

- [ ] **Step 5: Mutation check**

Remove the `!name.AsSpan(8, 8).Equals(...) ||` condition (keep the length and suffix checks). Expected: the
`--drop-index` test fails (the other drive's file is deleted too). Restore.

- [ ] **Step 6: Commit**

```powershell
Normalize-Src
git add src\DirSizer
git commit -m "dirsizer: --drop-index deletes the index of the path's drive"
```

---

### Task 8: End-to-end on T:

**Files:**
- Create: `scripts\Test-DirsizerIndex.ps1`

- [ ] **Step 1: Build the v0.7.0 reference**

```powershell
git worktree add $env:TEMP\dirsizer-v070 v0.7.0
dotnet build $env:TEMP\dirsizer-v070\src\DirSizer\DirSizer.csproj -c Release
Test-Path $env:TEMP\dirsizer-v070\artifacts\bin\DirSizer\release_win-x64\dirsizer.dll
```

Expected: `True`.

- [ ] **Step 2: Write `scripts\Test-DirsizerIndex.ps1`**

```powershell
<#
.SYNOPSIS
  End-to-end test of dirsizer.exe's index strategy (--keep-index, --changes, --drop-index) on a disposable NTFSTEST volume.

.DESCRIPTION
  Checks on <Volume>:
  - without --keep-index, stdout and exit code equal the reference dirsizer (v0.7.0; JSON compared without total_ms) for
    the drive root and a folder, and nothing is written to the index directory;
  - with --keep-index, the drive root equals the mft strategy, a folder equals dirsizer-index and the sum of its files,
    later runs are incremental;
  - --changes after a deletion; a held lock makes dirsizer fall back and dirsizer-index fail;
  - a standard (non-elevated) token, started through Explorer, walks the tree, leaves the index alone, and refuses
    --keep-index;
  - --drop-index.
  dirsizer.exe has no option to move the index, so this uses %LOCALAPPDATA%\dirsizer\index; only the test volume's
  files there are created and deleted (--drop-index at the start and the end). Writes on the volume only inside
  <Volume>\dsx-test. Needs an elevated shell and Release builds; nothing else may write to the volume while it runs.
  Exit code 0 only if every check passed.
#>
param(
    [string]$Volume = 'T:',
    [Parameter(Mandatory)] [string]$ReferenceTool,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer\release_win-x64\dirsizer.dll'),
    [string]$IndexTool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll'),
    [switch]$AllowAnyLabel
)
$ErrorActionPreference = 'Stop'
$Volume = $Volume.TrimEnd('\')
$vol = Get-Volume -DriveLetter $Volume.TrimEnd(':')
if ($vol.FileSystem -ne 'NTFS') { throw "$Volume is not NTFS." }
if (-not $AllowAnyLabel -and $vol.FileSystemLabel -ne 'NTFSTEST') { throw "$Volume is labelled '$($vol.FileSystemLabel)', not NTFSTEST. Pass -AllowAnyLabel to override." }
$Tool = (Resolve-Path $Tool).Path
$root = "$Volume\dsx-test"
$indexDirectory = Join-Path $env:LOCALAPPDATA 'dirsizer\index'
$refDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dsx-index-ref-' + [guid]::NewGuid().ToString('N'))
$failures = 0

function Invoke-Tool([string]$Path, [string[]]$Arguments) {
    $stderrFile = [IO.Path]::GetTempFileName()
    try {
        $stdout = if ($Path -like '*.exe') { & $Path @Arguments 2> $stderrFile } else { & dotnet $Path @Arguments 2> $stderrFile }
        [pscustomobject]@{ Exit = $LASTEXITCODE; Out = ($stdout -join "`n"); Err = [string](Get-Content -Raw $stderrFile) }
    } finally { Remove-Item $stderrFile -ErrorAction SilentlyContinue }
}
function Invoke-Dirsizer([string[]]$Arguments) { Invoke-Tool $Tool $Arguments }
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { "  PASS  $Name" } else { "  FAIL  $Name  $Detail"; $script:failures++ }
}
function Json($Result) { $Result.Out | ConvertFrom-Json }
function Strategy($Result) { (Json $Result).statistics.performance.strategy }
# JSON without the timing, which differs on every run.
function Stable-Json([string]$Text) {
    $o = $Text | ConvertFrom-Json
    $o.statistics.performance.total_ms = 0
    $o | ConvertTo-Json -Depth 10 -Compress
}
# Root, root children, directories and files as one comparable string (dirsizer and dirsizer-index use the same names).
function Listing($Json) {
    $parts = foreach ($list in @(@($Json.root), $Json.root_children, $Json.directories, $Json.files)) { (@($list) | ForEach-Object { "$($_.path)=$($_.size)" }) -join ';' }
    $parts -join ' | '
}
function Index-State { (Get-ChildItem -LiteralPath $indexDirectory -File -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n" }
function Write-Bytes([string]$Path, [int]$Size) {
    $null = New-Item -ItemType Directory -Force (Split-Path $Path)
    [IO.File]::WriteAllBytes($Path, [byte[]]::new($Size))
}

try {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    $null = Invoke-Dirsizer @("$Volume\", '--drop-index')
    Write-Bytes "$root\plain\a.bin" 1000
    Write-Bytes "$root\plain\sub\b.bin" 20000
    Write-Bytes "$root\plain\sub\c.bin" 300
    Write-Bytes "$root\other\d.bin" 4096

    '--- without --keep-index: the same as v0.7.0, nothing written'
    $dirBefore = Test-Path $indexDirectory
    $stateBefore = Index-State
    foreach ($target in @("$Volume\", "$root\plain")) {
        foreach ($extra in @(@(), @('--files'))) {
            $new = Invoke-Dirsizer (@($target, '--json') + $extra)
            $old = Invoke-Tool $ReferenceTool (@($target, '--json') + $extra)
            Check "unchanged JSON: $target $extra" ($new.Exit -eq $old.Exit -and (Stable-Json $new.Out) -eq (Stable-Json $old.Out)) "exit $($new.Exit)/$($old.Exit)"
            $new = Invoke-Dirsizer (@($target) + $extra)
            $old = Invoke-Tool $ReferenceTool (@($target) + $extra)
            Check "unchanged text: $target $extra" ($new.Exit -eq $old.Exit -and $new.Out -eq $old.Out)
        }
    }
    $mft = Invoke-Dirsizer @("$Volume\", '--json', '--files')
    Check 'no index: the drive root uses mft' ((Strategy $mft) -eq 'mft') (Strategy $mft)
    $r = Invoke-Dirsizer @("$root\plain", '--json')
    Check 'no index: a folder uses filesystem' ((Strategy $r) -eq 'filesystem') (Strategy $r)
    $r = Invoke-Dirsizer @("$Volume\", '--changes')
    Check 'no index: --changes gives the result, exit 0 and a note' ($r.Exit -eq 0 -and $r.Err -match 'no earlier index') $r.Err
    Check 'no index: nothing written to the index directory' ((Test-Path $indexDirectory) -eq $dirBefore -and (Index-State) -eq $stateBefore)

    '--- --keep-index: the first run scans and saves'
    $r = Invoke-Dirsizer @("$Volume\", '--json', '--files', '--keep-index')
    Check 'first --keep-index: exit 0, strategy index' ($r.Exit -eq 0 -and (Strategy $r) -eq 'index') "exit=$($r.Exit) $($r.Err)"
    $saved = [regex]::Match($r.Err, 'index: saved (.+?\.dsix) \(')
    Check 'first --keep-index: the save is reported' $saved.Success $r.Err
    $indexFile = $saved.Groups[1].Value
    Check 'the index file exists' ($indexFile -and (Test-Path -LiteralPath $indexFile))
    Check 'drive root from the index equals mft' ((Listing (Json $r)) -eq (Listing (Json $mft))) "index: $(Listing (Json $r))`n        mft:   $(Listing (Json $mft))"
    Check 'drive root: the volume field as mft reports it' ((Json $r).volume -eq (Json $mft).volume) "$((Json $r).volume) / $((Json $mft).volume)"

    '--- later runs: incremental, any folder'
    $r = Invoke-Dirsizer @("$Volume\", '--json', '--benchmark')
    Check 'second run: index, incremental' ((Strategy $r) -eq 'index' -and $r.Err -match 'index_mode=incremental') $r.Err
    $sub = Invoke-Dirsizer @("$root\plain", '--json', '--files')
    Check 'folder: strategy index' ((Strategy $sub) -eq 'index') (Strategy $sub)
    $ref = Invoke-Tool $IndexTool @("$root\plain", '--json', '--files', "--index-dir=$refDirectory")
    Check 'folder equals dirsizer-index' ((Listing (Json $sub)) -eq (Listing (Json $ref))) "dirsizer: $(Listing (Json $sub))`n        index:    $(Listing (Json $ref))"
    $expected = (Get-ChildItem -Recurse -File "$root\plain" | Measure-Object Length -Sum).Sum
    Check "folder size = sum of its files ($expected)" ((Json $sub).root.size -eq $expected) "$((Json $sub).root.size)"

    '--- --changes after a cleanup'
    $null = Invoke-Dirsizer @("$Volume\")
    Remove-Item "$root\plain\sub\b.bin"
    $r = Invoke-Dirsizer @("$root\plain", '--json', '--changes')
    $changes = (Json $r).changes
    Check 'changes: root delta -20000' ($changes.root_delta -eq -20000) "root_delta=$($changes.root_delta) $($r.Err)"
    $shrunk = @($changes.shrunk | Where-Object path -eq "$root\plain\sub")
    Check 'changes: sub shrank by 20000' ($shrunk.Count -eq 1 -and $shrunk[0].delta -eq -20000)
    $r = Invoke-Dirsizer @("$root\plain", '--json', '--changes')
    Check 'changes: nothing left on the next run' ((Json $r).changes.root_delta -eq 0) "root_delta=$((Json $r).changes.root_delta)"
    $r = Invoke-Dirsizer @("$root\plain", '--changes')
    Check 'changes: text report' ($r.Out -match 'Changes since' -and $r.Out -match 'Shrunk') $r.Out

    '--- another process holds the index'
    $lock = [IO.File]::Open("$indexFile.lock", 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-Dirsizer @("$Volume\", '--json', '--changes')
        Check 'locked: dirsizer falls back to mft after the wait' ((Strategy $r) -eq 'mft' -and $timer.Elapsed.TotalSeconds -ge 4) "strategy=$(Strategy $r) seconds=$($timer.Elapsed.TotalSeconds)"
        Check 'locked: dirsizer says why' ($r.Err -match 'not used on this run \(another process') $r.Err
        $r = Invoke-Tool $IndexTool @("$Volume\", '--json')
        Check 'locked: dirsizer-index fails with exit 1' ($r.Exit -eq 1 -and $r.Err -match 'another process') $r.Err
    } finally { $lock.Dispose() }
    Remove-Item -LiteralPath "$indexFile.lock" -ErrorAction SilentlyContinue
    $r = Invoke-Dirsizer @("$Volume\", '--json')
    Check 'released: the index again' ((Strategy $r) -eq 'index') (Strategy $r)

    '--- standard token (started through Explorer)'
    $stateBefore = Index-State
    $work = Join-Path ([IO.Path]::GetTempPath()) ('dsx-medium-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory $work
    $dotnet = (Get-Command dotnet).Source
    Set-Content -LiteralPath "$work\run.cmd" -Encoding ascii -Value @(
        "@`"$dotnet`" `"$Tool`" $Volume\ --json > `"$work\out.json`" 2> `"$work\err.txt`"",
        "@echo %ERRORLEVEL% > `"$work\exit.txt`"",
        "@`"$dotnet`" `"$Tool`" $Volume\ --keep-index > `"$work\out2.txt`" 2> `"$work\err2.txt`"",
        "@echo %ERRORLEVEL% > `"$work\exit2.txt`"")
    Start-Process explorer.exe "$work\run.cmd"
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (Test-Path "$work\exit2.txt") -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 500
    if (Test-Path "$work\exit2.txt") {
        $medium = Get-Content -Raw "$work\out.json" | ConvertFrom-Json
        Check 'standard token: filesystem, exit 0' ($medium.statistics.performance.strategy -eq 'filesystem' -and (Get-Content "$work\exit.txt").Trim() -eq '0') "$($medium.statistics.performance.strategy) $(Get-Content -Raw "$work\err.txt")"
        Check 'standard token: --keep-index is an error' ((Get-Content "$work\exit2.txt").Trim() -eq '1' -and (Get-Content -Raw "$work\err2.txt") -match 'needs an administrator terminal') (Get-Content -Raw "$work\err2.txt")
        Check 'standard token: the index files are untouched' ((Index-State) -eq $stateBefore)
    } else { Check 'standard token: the Explorer-started run finished' $false 'no exit2.txt after 3 minutes' }
    Remove-Item -Recurse -Force $work

    '--- --drop-index'
    $r = Invoke-Dirsizer @("$Volume\", '--drop-index')
    Check '--drop-index: deletes, exit 0' ($r.Exit -eq 0 -and $r.Out -match 'deleted ' -and -not (Test-Path -LiteralPath $indexFile)) $r.Out
    $r = Invoke-Dirsizer @("$Volume\", '--json')
    Check 'after --drop-index: mft again' ((Strategy $r) -eq 'mft') (Strategy $r)
    $r = Invoke-Dirsizer @("$Volume\", '--drop-index', '--changes')
    Check '--drop-index with --changes: exit 1' ($r.Exit -eq 1) $r.Err
} finally {
    $null = Invoke-Dirsizer @("$Volume\", '--drop-index')
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    if (Test-Path $refDirectory) { Remove-Item -Recurse -Force $refDirectory }
}
''
if ($failures -eq 0) { 'ALL CHECKS PASSED'; exit 0 } else { "$failures CHECK(S) FAILED"; exit 1 }
```

- [ ] **Step 3: Run it (elevated)**

```powershell
.\scripts\Test-DirsizerIndex.ps1 -Volume T: -ReferenceTool $env:TEMP\dirsizer-v070\artifacts\bin\DirSizer\release_win-x64\dirsizer.dll; "exit=$LASTEXITCODE"
```

Expected: `ALL CHECKS PASSED`, `exit=0`. If a check fails, use superpowers:systematic-debugging before changing code.

- [ ] **Step 4: Mutation checks (elevated)**

Each: make the break, rebuild, run the script, confirm the named check fails, restore, rebuild.
1. `IndexLock.Acquire`: `FileShare.None` → `FileShare.ReadWrite`. Expected: the `locked:` checks fail.
2. `IndexStrategy.Scan`: last line → `return indexRun.Result;`. Expected: `drive root: the volume field as mft reports it` fails.
3. `IndexRunner.Run`: delete the `if (!request.CreateIfMissing && ...) throw ...` line. Expected: `no index: nothing
   written to the index directory` fails (a plain run creates an index).

- [ ] **Step 5: Also run the existing end-to-end script (the index code changed in Tasks 1-3)**

`.\scripts\Test-IndexIncremental.ps1 -Volume T:; "exit=$LASTEXITCODE"`. Expected: `ALL CHECKS PASSED`, `exit=0`.

- [ ] **Step 6: Commit**

```powershell
git add scripts\Test-DirsizerIndex.ps1
git commit -m "scripts: Test-DirsizerIndex.ps1, dirsizer.exe's index strategy end to end on T:"
```

---

### Task 9: C: measurements and the GC decision (elevated)

This writes an index of `C:` (about 112 MiB) to `%LOCALAPPDATA%\dirsizer\index` and deletes it at the end.

- [ ] **Step 1: Measure**

```powershell
$tool = (Resolve-Path artifacts\bin\DirSizer\release_win-x64\dirsizer.dll).Path
$indexTool = (Resolve-Path artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll).Path
$err = [IO.Path]::GetTempFileName()
function Run([string]$Label, [string]$Path, [string[]]$Arguments) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet $Path @Arguments 2> $err > $null
    $bench = (Get-Content $err | Select-String '^benchmark:' | Select-Object -Last 1).Line
    "{0,-28} wall_ms={1,7} {2}" -f $Label, $sw.ElapsedMilliseconds, $bench
}
$small = (Resolve-Path .).Path
& dotnet $tool C:\ --drop-index
foreach ($i in 1..3) { Run 'mft C:\' $tool @('C:\', '--benchmark') }
foreach ($i in 1..3) { Run 'walk C:\Users' $tool @('C:\Users', '--benchmark') }
foreach ($i in 1..3) { Run 'walk repo' $tool @($small, '--benchmark') }
Run 'first --keep-index C:\' $tool @('C:\', '--keep-index', '--benchmark')
foreach ($i in 1..3) { Run 'index C:\' $tool @('C:\', '--benchmark') }
foreach ($i in 1..3) { Run 'index C:\Users' $tool @('C:\Users', '--benchmark') }
foreach ($i in 1..3) { Run 'index repo' $tool @($small, '--benchmark') }
foreach ($i in 1..3) { Run 'index C:\ --changes' $tool @('C:\', '--changes', '--benchmark') }
foreach ($i in 1..3) { Run 'dirsizer-index C:\ (server GC)' $indexTool @('C:\', '--benchmark') }
& dotnet $tool C:\ --drop-index
Remove-Item $err
```

Record every line. For each label take the median of `total_ms` from the benchmark line.

- [ ] **Step 2: GC decision**

dirsizer-index uses server GC (its load is about 2x faster with it, design_index.md "Loading fast"); dirsizer.exe
uses the default workstation GC. Rule: if the median of `index C:\` is more than 1.25x the median of
`dirsizer-index C:\ (server GC)`, add `<ServerGarbageCollection>true</ServerGarbageCollection>` to
`src\DirSizer\DirSizer.csproj` (with the same comment as in `DirSizer.Index.csproj`), rebuild, and repeat Step 1.
Keep server GC only if `index C:\` improves and neither `mft C:\` nor `walk C:\Users` is more than 10 % slower than
before; otherwise remove it again. Record the decision and both sets of numbers.

- [ ] **Step 3: Commit (only if the csproj changed)**

```powershell
Normalize-Src
git add src\DirSizer\DirSizer.csproj
git commit -m "dirsizer: server GC (the index strategy loads <x> ms faster on C:; mft and walk unchanged within 10 %)"
```

(Replace `<x>` with the measured difference.)

---

### Task 10: Docs

**Files:**
- Modify: `docs\design_index.md`, `docs\superpowers\specs\2026-09-23-unified-scan-strategy-design.md`, `docs\superpowers\specs\2026-09-24-index-in-dirsizer-design.md`, `README.md`, `README-jp.md`, `docs\roadmap.md`

- [ ] **Step 1: `docs\design_index.md`**

Insert before `## Privacy`:

```markdown
## In dirsizer.exe (v0.8.0)

`dirsizer.exe` uses this index as its `index` strategy
([2026-09-24-index-in-dirsizer-design.md](superpowers/specs/2026-09-24-index-in-dirsizer-design.md)). `--keep-index`
creates the index of a drive; from then on every run on that drive from an administrator terminal, root or folder,
is answered from it; `--drop-index` deletes it. It always uses the default location. A lock file
(`<index>.lock`) lets one process at a time load, update and save an index; a second one waits up to 5 s, then
`dirsizer.exe` answers without the index and `dirsizer-index` stops with an error. `--changes` leaves out the index
directory's own change (its files change on every save): the directory is not listed and its change is subtracted
from every directory above it. Checked end to end on T: by `scripts\Test-DirsizerIndex.ps1`.
```

Then append to the Measurements section one paragraph built from Task 9's medians, in this form (every value from
Task 9, none estimated): "dirsizer.exe on `C:` (Task 9 of the plan, medians of 3, warm): mft strategy A ms; first
`--keep-index` run B ms; index strategy C ms for `C:\`, D ms for `C:\Users` (walk: E ms), F ms for a 454-directory
folder (walk: G ms); `--changes` H ms; dirsizer-index I ms. GC: <decision from Task 9 Step 2>."

- [ ] **Step 2: Unified spec addendum**

Append to `docs\superpowers\specs\2026-09-23-unified-scan-strategy-design.md`:

```markdown
## Addendum 2026-09-24: the index strategy

[2026-09-24-index-in-dirsizer-design.md](2026-09-24-index-in-dirsizer-design.md) adds a fourth strategy, `index`,
first in the chain on any local NTFS path once the user has opted in per drive with `--keep-index`. Two statements
above change with it: a path that is not a whole drive can now use the `index` strategy (it answers any folder of an
indexed drive), and the option set gains `--keep-index`, `--changes` and `--drop-index`, which name what the user
wants (keep an index, show changes, delete it), not an implementation. The availability/failure boundary is
unchanged: the index is unavailable only without volume access, without an index of the drive, or while another
process holds it, all decided before anything is read or written.
```

- [ ] **Step 3: The index spec's status**

In `docs\superpowers\specs\2026-09-24-index-in-dirsizer-design.md`, replace `Status: **accepted 2026-09-24, not implemented.**`
with `Status: **implemented** (branch feature/index-in-dirsizer; verification in the plan).`

- [ ] **Step 4: README.md**

In the `## dirsizer.exe (the unified entry point)` section:
- In the "Selection policy" paragraph, replace `or a path that is not a whole drive (the other two strategies only read an entire
volume) --` with `or a path that is not a whole drive on a drive without an index (the other two strategies only read
an entire volume) --`.
- In the "Options" paragraph, replace `` `--workers N` (only meaningful
when the filesystem strategy runs), `` with `` `--workers N` (only meaningful
when the filesystem strategy runs), `--keep-index`, `--changes`, `--drop-index`, `` and replace ``(`"mft"`, `"fsctl"` or `"filesystem"`)``
with ``(`"index"`, `"mft"`, `"fsctl"` or `"filesystem"`)``.
- Add at the end of the section:

````markdown
### Keeping an index (`--keep-index`)

`dirsizer.exe` writes nothing unless you ask it to. On a local NTFS drive, from an administrator terminal:

```powershell
.\dirsizer.exe C:\ --keep-index      # a normal scan, then an index of C: is saved
.\dirsizer.exe C:\Users\me           # later runs, on the drive or any folder on it: answered from the index
.\dirsizer.exe C:\ --changes         # also list what shrank and grew since the previous run
.\dirsizer.exe C:\ --drop-index      # delete the index of C:
```

The first run saves the index in `%LOCALAPPDATA%\dirsizer\index` (about 112 MiB for a drive with 924,000 files and
folders; the run prints the path and size). After that, every run on that drive from an administrator terminal
reads the index and only the records the NTFS change journal names, instead of the whole drive (`strategy=index`).
Folders are then answered with the NTFS semantics of the whole-drive methods (hard links, metadata files and
reparse points counted as the NTFS tools count them), not by walking them. From a normal terminal the index is
neither used nor changed, and `dirsizer.exe` walks the tree as before. The index lists every file and folder name
on the drive. Changes made while the drive was used by another operating system are not seen: run `--drop-index`
and `--keep-index` again after that. Details: [docs/design_index.md](docs/design_index.md).
````

- [ ] **Step 5: README-jp.md**

In `## dirsizer.exe（統合エントリポイント）`, make the same three edits in Japanese: in the selection policy text,
the "a path that is not a whole drive" condition becomes 「インデックスのないドライブで、ドライブ全体ではないパス」; add
`--keep-index`、`--changes`、`--drop-index` to the option list; add `"index"` to the strategy values. Add at the end of
the section:

````markdown
### インデックスの保持（`--keep-index`）

`dirsizer.exe` は、指定しない限り何も書き込みません。ローカルの NTFS ドライブで、管理者権限のターミナルから:

```powershell
.\dirsizer.exe C:\ --keep-index      # 通常のスキャンの後、C: のインデックスを保存
.\dirsizer.exe C:\Users\me           # 以降は、ドライブ全体でもその中のフォルダーでもインデックスから回答
.\dirsizer.exe C:\ --changes         # 前回の実行から縮小・増加したものも一覧表示
.\dirsizer.exe C:\ --drop-index      # C: のインデックスを削除
```

最初の実行でインデックスを `%LOCALAPPDATA%\dirsizer\index` に保存します（ファイルとフォルダーが 924,000 個のドライブで約
112 MiB。保存先とサイズは実行時に表示されます）。以降、そのドライブに対する管理者権限での実行は、ドライブ全体を読む代わりに
インデックスと NTFS 変更ジャーナルが示すレコードだけを読みます（`strategy=index`）。フォルダーも走査ではなく、ドライブ全体の
方式と同じ NTFS の数え方（ハードリンク、メタデータファイル、再解析ポイントの扱い）で答えます。通常のターミナルからはインデックスを
使わず変更もせず、これまでどおりディレクトリを走査します。インデックスにはドライブ上のすべてのファイル名とフォルダー名が含まれます。
別の OS でドライブを使った間の変更は検出されないため、その後は `--drop-index` と `--keep-index` をやり直してください。詳細:
[docs/design_index.md](docs/design_index.md)（英語）。
````

- [ ] **Step 6: Roadmap**

In `docs\roadmap.md`, replace the heading `### Next: the index in dirsizer.exe (proposal)` and its paragraph with:

```markdown
### The index in dirsizer.exe

Design: [2026-09-24-index-in-dirsizer-design.md](superpowers/specs/2026-09-24-index-in-dirsizer-design.md); plan: [2026-09-24-index-in-dirsizer.md](superpowers/plans/2026-09-24-index-in-dirsizer.md).

- [x] `--keep-index` (opt in per drive), then the `index` strategy first on local NTFS paths, root and folders; `--changes`; `--drop-index`. Self-tests (dirsizer.exe 14, dirsizer-index 44, mutation-checked); `Test-DirsizerIndex.ps1` on T: ALL CHECKS PASSED: output unchanged from v0.7.0 without `--keep-index` and nothing written; root equal to the mft strategy; folders equal to dirsizer-index; `--changes`; a held lock; a standard token; `--drop-index`.
- [x] Measured on C: (Task 9): <the paragraph's numbers from Step 1, in one sentence>.
```

(The second item's sentence repeats Step 1's measured numbers; it is written after Task 9, never before.)

- [ ] **Step 7: Commit**

```powershell
git add docs README.md README-jp.md
git commit -m "docs: dirsizer.exe's index strategy (--keep-index, --changes, --drop-index)"
```

---

### Task 11: Final verification and the 0.8.0 package

- [ ] **Step 1: Clean build and every self-test**

```powershell
dotnet build DirSizer.sln -c Release --no-incremental
foreach ($t in 'DirSizer.Index\release_win-x64\dirsizer-index.dll','DirSizer\release_win-x64\dirsizer.dll','DirSizer.Bulk\release_win-x64\dirsizer-mft.dll','DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll','DirSizer.Fs\release_win-x64\dirsizer-fs.dll') { $null = dotnet "artifacts\bin\$t" --self-test; "$t exit=$LASTEXITCODE" }
```

Expected: 0 warnings; every exit code 0 (index 44, dirsizer 14).

- [ ] **Step 2: Other tools unchanged (elevated)**

`.\scripts\Compare-Readers.ps1 -Volume T:; "exit=$LASTEXITCODE"`. Expected: `RESULT: EQUAL`, `exit=0`. And
`git diff master --stat -- src\Shared src\DirSizer.Core src\DirSizer.Bulk src\DirSizer.Fsctl src\DirSizer.Fs src\DirSizer.Fs.Core src\DirSizer.Inspect`
prints nothing.

- [ ] **Step 3: End-to-end on the final build (elevated)**

Run `Test-DirsizerIndex.ps1` (Task 8 Step 3) and `Test-IndexIncremental.ps1` again. Expected: both ALL CHECKS PASSED.
Then remove the reference worktree: `git worktree remove --force $env:TEMP\dirsizer-v070`.

- [ ] **Step 4: Version 0.8.0 and the package**

- `Directory.Build.props`: `<Version>0.7.0</Version>` → `<Version>0.8.0</Version>`.
- `release-notes.md`: replace with the v0.8.0 notes: headline "dirsizer.exe can keep an index of a drive"; what
  `--keep-index`, `--changes`, `--drop-index` do; that nothing is written without `--keep-index` and output is
  unchanged; the size of the index and where it is; the Task 9 numbers; the limitations from the README section
  (administrator terminal, another OS, the names it holds); the six executables.
- Run `pwsh -NoProfile -File scripts\release.ps1` (package only). Then check the zip as for v0.7.0: six executables,
  `dirsizer.exe` and `dirsizer-fs.exe` with `asInvoker` and the other four with `requireAdministrator` in the
  manifest, no `DIRSIZER_TEST` string in any of them, every `--self-test` passing, and the packaged `dirsizer.exe`
  run on T: with `--keep-index` then `--changes` then `--drop-index` (exit 0 each).
- Commit: `git add Directory.Build.props release-notes.md; git commit -m "Version 0.8.0: dirsizer.exe keeps an index of a drive on request"`.

- [ ] **Step 5: Hand-off**

Use superpowers:finishing-a-development-branch. **Do not merge, push or publish without the user's go-ahead.**
Report every recorded number, every mutation check, and every check that could not be run.

---

## Self-review notes

- **Spec coverage.** Strategy and candidates: Tasks 4-5. Availability cases (no access, no index, busy): Tasks 2, 4.
  `--keep-index` errors (not NTFS, not elevated): Tasks 4-5. `--drop-index` (low 32 bits): Task 7. `--changes`
  notes: Task 6. Output (strategy `index`, benchmark fields, JSON `changes` only with `--changes`, stdout unchanged
  otherwise): Task 6, checked against v0.7.0 in Task 8. Drive root reported as typed: Task 4. Lock: Tasks 2, 8.
  Index directory left out of `--changes`: Task 3. Code sharing: Tasks 1, 4. Validation gates: Tasks 8, 9, 11.
- **Not covered, stated.** `strategy_fallback` stays unset (existing gap, spec). The lock is tested with a held
  handle, not with two real concurrent scans. Two drives with equal low 32-bit serials are not handled.
- **Types across tasks.** `IndexRequest` (Task 1; `LockTimeout` added in Task 2, a defaulted last parameter, so the
  Task 1 test still compiles); `IndexUnavailableException` (Task 2) used in Tasks 4 and 6; `ChangeText`,
  `JsonChanges.From` (Task 3) used in Task 6; `ScanStrategySelector.LocalDrive`/`IsDriveRoot` (Task 4) used in Tasks
  5 and 7; `IndexStrategy.KeepIndex`, `LastRun`, `UnavailableReason` (Task 4) used in Tasks 5 and 6.
