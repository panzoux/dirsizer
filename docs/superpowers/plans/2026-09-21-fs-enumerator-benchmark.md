# Directory enumerator comparison Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put directory enumeration behind a small contract, add two alternative enumerators (B: `GetFileInformationByHandleEx`, C: `NtQueryDirectoryFileEx`) next to the baseline (A: `FindFirstFileExW`), and compare the three on the same workloads, as specified in [docs/design_fs.md](../../design_fs.md), section "Enumerator comparison (P4)".

**Architecture:** `IDirectoryEnumerator.Read(path, sink)` with an API-neutral `IEntrySink.OnEntry(attributes, size, name)`; one enumerator instance per worker owns its buffers; `Walker` receives an `IEnumeratorFactory`. `find` is the old `DirectoryReader` moved behind the contract, unchanged in behaviour (proved by snapshot equality). B and C share one buffered, handle-based base class and differ only in the call that fills the buffer. `--enumerator=NAME[:CLASS[:KiB]]` selects the enumerator; the resolved name is reported in the benchmark line and in `performance.enumerator` of the JSON.

**Tech Stack:** C# 12 / .NET 8, NativeAOT, `DllImport` (`kernel32`, `ntdll`) with blittable arguments and no unsafe code, PowerShell scripts. No packages, no LINQ in production code.

---

## Ground rules for the executing agent

- **The code in this plan was built and run before it was written down**: 34 self-tests pass in the JIT and in the NativeAOT build, the Step 1 state passes 28 and gives snapshots identical to the pre-change build, and all measurements quoted come from that prototype. Copy the code exactly. If something does not compile or a test fails, that is new information: investigate, do not "fix" by rewriting. Design changes need the user's approval.
- **Run all commands with the PowerShell tool from the repository root** (`C:\Users\user\source\repos\panzoux\dirsizer`), never with the Bash tool (its shell is a restricted sandbox and mangles backslashes). Create files with the Write tool (absolute paths), never with shell heredocs. A command that the harness rejects is reported, not worked around. For cleanup use `[IO.Directory]::Delete(<path>, $true)`, not `Remove-Item`.
- The branch is `feature/fs-enumerator-benchmark` (never commit to `master`, never push). Commit messages end with the line `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`.
- **Line endings.** The repository stores UTF-8 without BOM and **CRLF**. Files created with the Write tool are LF. Define these helpers once per PowerShell command that needs them and run `Normalize-FsProject` before every commit that adds or changes files under `src\DirSizer.Fs`; use `ConvertTo-Crlf <path>` for scripts and documents you create.

```powershell
function ConvertTo-Crlf([string[]]$Path) {
    foreach ($p in $Path) {
        $full = (Resolve-Path -LiteralPath $p).ProviderPath    # .NET file APIs do not follow Set-Location
        $text = [IO.File]::ReadAllText($full)
        $fixed = $text -replace "`r?`n", "`r`n"
        if ($fixed -ne $text) { [IO.File]::WriteAllText($full, $fixed, [Text.UTF8Encoding]::new($false)) }
    }
}
function Normalize-FsProject {
    ConvertTo-Crlf (Get-ChildItem src\DirSizer.Fs -Recurse -File -Include *.cs, *.csproj, *.manifest | ForEach-Object FullName)
}
```

- Test runs: `dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release` then `dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test`. In a sandboxed shell the deny-ACL tests skip themselves with a message; the PowerShell tool runs them for real.
- Nothing outside `src\DirSizer.Fs`, `scripts\` and `docs\` (and `README*.md`) may change. Task 6 checks that the NTFS tools are untouched.
- The synthetic workloads are created under `%TEMP%` (`enum-fixture-many`, `enum-fixture-large`) and are removed by Task 6.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `scripts\Get-FsSnapshot.ps1` | canonical, comparable snapshot of a scan | 1 |
| `Enumerator.cs` | the contract: `IEntrySink`, `IDirectoryEnumerator`, `IEnumeratorFactory`, `ReadOutcome`, `ReadResult`, `EntryNames` | 2 |
| `FindFirstEnumerator.cs` | enumerator A (`find`) and its factory; replaces `DirectoryReader.cs` | 2 |
| `Win32Find.cs`, `Walker.cs`, `FsScanner.cs`, `SelfTests.Reader.cs`, `SelfTests.Walk.cs` | adapted to the contract | 2 |
| `EnumeratorSpec.cs` | `--enumerator` grammar, canonical names, factory selection | 3 |
| `HandleEnumerators.cs` | `DirectoryHandle`, `BufferedEnumerator`, B (`HandleInfoEnumerator`), C (`NtQueryEnumerator`), `BufferedFactory` | 3 |
| `FsOptions.cs`, `FsOutput.cs`, `Program.cs`, `SelfTests.Output.cs`, `SelfTests.Cli.cs`, `SelfTests.cs` | option, reporting, wiring | 3 |
| `SelfTests.Enumerators.cs` | conformance tests for every enumerator | 3 |
| `scripts\Compare-Enumerators.ps1`, `scripts\New-EnumFixture.ps1` | equality across enumerators and volumes, timed comparison, synthetic workloads | 4 |
| `docs\design_fs.md`, `docs\roadmap.md`, `README.md`, `README-jp.md` | results, capability matrix, decision, usage | 5, 6 |

The set of files that each task changes was computed from the actual difference between the states, so it is complete.

---

### Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1: Check the branch and take the baseline of the code as it is**

```powershell
"branch: $(git branch --show-current)"; git status --short
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 3
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: branch `feature/fs-enumerator-benchmark`, a clean tree (or only the documents of the spec), `0 Error(s)` and `28 self-tests passed, 0 skipped.`. This build is the "before" of every comparison: **do not change any code before Task 1 Step 3 has written the baseline snapshots**.

---

### Task 1: Snapshot script and the baseline

**Files:**
- Create: `scripts\Get-FsSnapshot.ps1`
- Local artifacts (git-ignored): `artifacts\snapshots\baseline-*.json`

- [ ] **Step 1: Write the script**

`scripts\Get-FsSnapshot.ps1`:

```powershell
<#
.SYNOPSIS
  Writes a canonical snapshot of a dirsizer scan, so that two builds or two enumerators can be compared byte for byte.

.DESCRIPTION
  Runs dirsizer.exe (or dirsizer.dll) on -Path with --json and a --top larger than any tree, and writes a normalised JSON file:
  the volume and root, the counters (directories_scanned, directories_denied, directories_failed, reparse_skipped,
  directories, files, bytes), every directory with its size and, with -Files, every file, and the root's children. The lists
  are sorted by path (ordinal), because entries of equal size come in no defined order. The timing, memory and enumerator
  fields, and the error samples (operating-system language), are left out: they are not what a comparison is about.
  Use a QUIESCENT tree; a tree that changes between two scans gives two different snapshots.
  Exit code 0 when the snapshot was written; 1 when the tool failed.

.EXAMPLE
  .\scripts\Get-FsSnapshot.ps1 -Path T:\ -Out artifacts\snapshots\before-T.json
  .\scripts\Get-FsSnapshot.ps1 -Path T:\ -Enumerator handle:idextd:64 -Out artifacts\snapshots\handle-T.json
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Out,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll'),
    [string]$Enumerator,
    [int]$Workers = 8,
    [switch]$Files
)
$ErrorActionPreference = 'Stop'

$arguments = @($Path, '--json', '--top=100000000', '--workers', "$Workers")
if ($Files) { $arguments += '--files' }
if ($Enumerator) { $arguments += "--enumerator=$Enumerator" }

$errorFile = [IO.Path]::GetTempFileName()
try {
    $text = if ($Tool -like '*.dll') { & dotnet $Tool @arguments 2>$errorFile } else { & $Tool @arguments 2>$errorFile }
    $code = $LASTEXITCODE
    if ($code -notin 0, 3) { Write-Error "$Tool exited with code ${code}: $((Get-Content -Raw $errorFile).Trim())"; exit 1 }
} finally { [IO.File]::Delete($errorFile) }
$json = ($text -join "`n") | ConvertFrom-Json

function Get-Sorted($items) {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $items) { $list.Add([pscustomobject][ordered]@{ path = $item.path; size = $item.size }) }
    $list.Sort([Comparison[object]]{ param($a, $b) [string]::CompareOrdinal($a.path, $b.path) })
    , $list.ToArray()
}
$s = $json.statistics
$snapshot = [ordered]@{
    volume        = $json.volume
    size_mode     = $json.size_mode
    root          = [ordered]@{ path = $json.root.path; size = $json.root.size }
    counters      = [ordered]@{
        directories_scanned = $s.directories_scanned; directories_denied = $s.directories_denied; directories_failed = $s.directories_failed
        reparse_skipped = $s.reparse_skipped; directories = $s.directories; files = $s.files; bytes = $s.bytes
    }
    directories   = Get-Sorted $json.directories
    root_children = Get-Sorted $json.root_children
    files         = Get-Sorted $json.files
}
$folder = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
if (-not (Test-Path $folder)) { [void](New-Item -ItemType Directory $folder) }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out), ($snapshot | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
"snapshot: $Out  ($($json.statistics.directories) directories, $($json.statistics.files) files, $($json.root.size) bytes)"
exit 0
```

- [ ] **Step 2: Take the baseline snapshots with the current build (before any change to the code)**

The trees must be quiescent: `T:\` (the NTFSTEST fixture volume), `C:\Program Files\dotnet` and `C:\Windows\System32\drivers`.

```powershell
ConvertTo-Crlf scripts\Get-FsSnapshot.ps1   # helper from the ground rules, defined in this same command
foreach ($p in @(@('T:\', 'T'), @('C:\Program Files\dotnet', 'dotnet'), @('C:\Windows\System32\drivers', 'drivers'))) {
    .\scripts\Get-FsSnapshot.ps1 -Path $p[0] -Out "artifacts\snapshots\baseline-$($p[1]).json"
    .\scripts\Get-FsSnapshot.ps1 -Path $p[0] -Out "artifacts\snapshots\baseline-again-$($p[1]).json" | Out-Null
    $a = [IO.File]::ReadAllText((Resolve-Path "artifacts\snapshots\baseline-$($p[1]).json")); $b = [IO.File]::ReadAllText((Resolve-Path "artifacts\snapshots\baseline-again-$($p[1]).json"))
    "  two runs of the same build give the same snapshot: " + ($a -ceq $b)
}
```

Expected: one `snapshot: ...` line per tree (the prototype: `T:\` 27 directories, 377 files, 22,808,487 bytes; `dotnet` 1969 directories, 13,939 files, 2,453,520,418 bytes; `drivers` 10 directories, 602 files, 173,524,197 bytes; your numbers may differ if those trees changed) and `two runs of the same build give the same snapshot: True` three times. If a `False` appears, that tree is not quiescent: use another one and say so.

- [ ] **Step 3: Commit the script**

```powershell
git add scripts\Get-FsSnapshot.ps1
git commit -m @'
Add scripts\Get-FsSnapshot.ps1: canonical scan snapshot for before/after and cross-enumerator comparison

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 2: The contract; `find` behind it (behaviour unchanged)

**Files (from the difference between the current code and the Step 1 state):**

- Create: `src\DirSizer.Fs\Enumerator.cs`
- Create: `src\DirSizer.Fs\FindFirstEnumerator.cs`
- Modify (replace whole file): `src\DirSizer.Fs\FsScanner.cs`
- Modify (replace whole file): `src\DirSizer.Fs\SelfTests.Reader.cs`
- Modify (replace whole file): `src\DirSizer.Fs\SelfTests.Walk.cs`
- Modify (replace whole file): `src\DirSizer.Fs\Walker.cs`
- Modify (replace whole file): `src\DirSizer.Fs\Win32Find.cs`
- Delete: `src\DirSizer.Fs\DirectoryReader.cs`

- [ ] **Step 1: Write the files**

Write each file below with the Write tool, exactly as shown (create or overwrite), then delete the removed ones with `git rm`.

`src\DirSizer.Fs\Enumerator.cs` (new):

```csharp
// The contract between the walk and a directory-enumeration API. The walk asks an enumerator to read one directory and receives
// every entry through a sink. Nothing API-specific crosses this boundary: an API may return more (reparse tag, file id, allocation
// size, times, short name), but only what the walk needs is passed on.

// NotRead is the default value on purpose: a result that was never filled in must not look like a successful read.
enum ReadOutcome { NotRead, Complete, Denied, Failed }

readonly record struct ReadResult(ReadOutcome Outcome, int Error);

interface IEntrySink
{
    // Called for every entry except "." and "..". `attributes` are the Win32 file attributes (the walk looks at
    // FILE_ATTRIBUTE_DIRECTORY and FILE_ATTRIBUTE_REPARSE_POINT), `size` is the logical size (end of file). The name is only
    // valid during the call.
    void OnEntry(uint attributes, long size, ReadOnlySpan<char> name);
}

interface IDirectoryEnumerator
{
    // directoryPath is the extended-length path of the directory. A file-system problem is not thrown: the result says what
    // happened. An enumerator instance belongs to one worker; it owns that worker's buffers and is not thread-safe.
    ReadResult Read(string directoryPath, IEntrySink sink);
}

static class EntryNames
{
    public static bool IsDot(ReadOnlySpan<char> name) => name.Length is 1 or 2 && name[0] == '.' && (name.Length == 1 || name[1] == '.');
}

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

`src\DirSizer.Fs\FindFirstEnumerator.cs` (new):

```csharp
using System.Runtime.InteropServices;

// Shared by all workers of a run: the LARGE_FETCH switch. It only ever goes from on to off, so a plain volatile flag is enough.
sealed class LargeFetchState(bool on)
{
    volatile bool _on = on;

    public bool On => _on;

    public void TurnOff() => _on = false;
}

// A: FindFirstFileExW / FindNextFileW, the enumerator of the first version and the baseline of every comparison.
sealed class FindFirstEnumerator(LargeFetchState largeFetch, FindFirstFn findFirst) : IDirectoryEnumerator
{
    Win32FindData _data;    // this worker's buffer

    public ReadResult Read(string directoryPath, IEntrySink sink)
    {
        var pattern = directoryPath.EndsWith('\\') ? directoryPath + "*" : directoryPath + "\\*";
        var useLargeFetch = largeFetch.On;
        var first = findFirst(pattern, ref _data, useLargeFetch);
        if (first.Handle == Win32Find.InvalidHandle && useLargeFetch && first.Error == Win32Find.ErrorInvalidParameter)
        {
            // Some filesystems reject the flag. Retry once without it; only if that works is the flag switched off for the run,
            // because the same error can also come from the path itself.
            first = findFirst(pattern, ref _data, false);
            if (first.Handle != Win32Find.InvalidHandle) largeFetch.TurnOff();
        }
        // A directory with no entries at all. The root of a FAT or exFAT volume has no "." or ".." entries, so listing an empty one
        // fails with ERROR_FILE_NOT_FOUND: that is an empty directory, not a failure. (A path that does not exist gives ERROR_PATH_NOT_FOUND.)
        if (first.Handle == Win32Find.InvalidHandle && first.Error == Win32Find.ErrorFileNotFound)
            return new ReadResult(ReadOutcome.Complete, 0);
        if (first.Handle == Win32Find.InvalidHandle)
            return new ReadResult(first.Error == Win32Find.ErrorAccessDenied ? ReadOutcome.Denied : ReadOutcome.Failed, first.Error);

        try
        {
            while (true)
            {
                ReadOnlySpan<char> all = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)_data.FileName);
                var length = all.IndexOf('\0');
                var name = length < 0 ? all : all[..length];
                if (!EntryNames.IsDot(name)) sink.OnEntry(_data.FileAttributes, Win32Find.FileSize(in _data), name);
                if (Win32Find.TryFindNext(first.Handle, ref _data, out var error)) continue;
                return error == Win32Find.ErrorNoMoreFiles ? new ReadResult(ReadOutcome.Complete, 0) : new ReadResult(ReadOutcome.Failed, error);
            }
        }
        finally
        {
            Win32Find.Close(first.Handle);
        }
    }
}

sealed class FindFirstFactory(bool largeFetch = true, FindFirstFn? findFirst = null) : IEnumeratorFactory
{
    readonly LargeFetchState _state = new(largeFetch);
    readonly FindFirstFn _findFirst = findFirst ?? Win32Find.FindFirst;

    public string Name { get; } = largeFetch ? "find" : "find:nolarge";

    public bool LargeFetch => _state.On;

    public IDirectoryEnumerator Create() => new FindFirstEnumerator(_state, _findFirst);
}
```

`src\DirSizer.Fs\FsScanner.cs` (replace the whole file):

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

readonly record struct ResultItem(string Path, long Size);

sealed record FsCounters(long DirectoriesScanned, long DirectoriesDenied, long DirectoriesFailed, long ReparseSkipped, long Directories, long Files, long Bytes)
{
    public long Unreadable => DirectoriesDenied + DirectoriesFailed;
}

// Wall-clock phases (open, walk, aggregation, finalize, other) add up to Total exactly. EnumTotal and IdleTotal are summed over
// all workers while they run in parallel inside `walk`, so they are diagnostics and not phases.
sealed record FsMetrics(
    TimeSpan Open, TimeSpan Walk, TimeSpan Aggregation, TimeSpan Finalize, TimeSpan Total,
    TimeSpan EnumTotal, TimeSpan IdleTotal, int Workers, bool LargeFetch, int PeakQueuedDirs,
    long Entries, long Directories, long Bytes, long ManagedAllocatedBytes, long PeakWorkingSetBytes)
{
    public TimeSpan Other => TimeSpan.FromTicks(Math.Max(0, Total.Ticks - Open.Ticks - Walk.Ticks - Aggregation.Ticks - Finalize.Ticks));
    public TimeSpan PhaseSum => Open + Walk + Aggregation + Finalize + Other;
    public double EntriesPerSec => Walk.TotalSeconds == 0 ? 0 : Entries / Walk.TotalSeconds;
    public double DirectoriesPerSec => Walk.TotalSeconds == 0 ? 0 : Directories / Walk.TotalSeconds;
    public double LogicalMibPerSec => Walk.TotalSeconds == 0 ? 0 : Bytes / 1048576.0 / Walk.TotalSeconds;
}

sealed record FsResult(
    string RootPath,
    ResultItem Root,
    ResultItem[] RootChildren,
    ResultItem[] Directories,
    ResultItem[] Files,
    FsCounters Counters,
    FsMetrics Metrics,
    string[] ErrorSamples,
    DirNode[] Nodes);

sealed record ScanSettings(int Workers, int Top, bool CollectFiles, bool ShowProgress, CancellationToken Cancel = default, IEnumeratorFactory? Enumerators = null);

readonly record struct RootChild(DirNode? Directory, FileHit File);

static class FsScanner
{
    // Throws ArgumentException (bad settings, bad or missing root), IOException (root cannot be read), OperationCanceledException.
    public static FsResult Scan(string rootArgument, ScanSettings settings)
    {
        // With no worker nothing would be read; the walk would end at once and the result would look like an empty tree.
        if (settings.Workers < 1) throw new ArgumentOutOfRangeException(nameof(settings), "The number of workers must be at least 1.");
        var total = Stopwatch.StartNew();
        var allocatedAtStart = GC.GetTotalAllocatedBytes();

        var rootPath = RootPath.Normalize(rootArgument);
        if (!Directory.Exists(rootPath.Extended)) throw new ArgumentException($"Not a directory, or not found: {rootPath.Display}");
        var open = total.Elapsed;

        var root = new DirNode(0, -1, rootPath.Display);
        Action<long, long>? progress = settings.ShowProgress
            ? (directories, files) => Console.Error.Write($"\rScanning: {directories:N0} directories, {files:N0} files")
            : null;
        WalkResult walk;
        try
        {
            walk = new Walker(settings.Enumerators).Run(root, rootPath.Extended, settings.Workers, settings.Top, settings.CollectFiles, settings.Cancel, progress);
        }
        finally
        {
            // Also when the walk failed or was canceled: the error message must not follow a half-written progress line.
            if (settings.ShowProgress) Console.Error.Write("\r" + new string(' ', 60) + "\r");
        }
        if (walk.RootRead.Outcome == ReadOutcome.NotRead) throw new InvalidOperationException("The root directory was never read (internal error).");
        if (walk.RootRead.Outcome != ReadOutcome.Complete)
        {
            var hint = walk.RootRead.Outcome == ReadOutcome.Denied ? " Choose a directory you can read, or start the tool from an elevated terminal." : "";
            throw new IOException($"Cannot read {rootPath.Display}: {Marshal.GetPInvokeErrorMessage(walk.RootRead.Error).TrimEnd()} (error {walk.RootRead.Error}).{hint}");
        }

        var mark = total.Elapsed;
        var nodes = walk.Nodes;
        DirTable.Aggregate(nodes);
        var aggregation = total.Elapsed - mark;

        mark = total.Elapsed;
        var top = settings.Top;
        var directoryTop = new BoundedTop<DirNode>(top);
        var rootChildTop = new BoundedTop<RootChild>(top);
        foreach (var node in nodes)
        {
            directoryTop.Add(node, node.Total);
            if (node.ParentId == 0) rootChildTop.Add(new RootChild(node, default), node.Total);
        }
        foreach (var hit in walk.RootFiles) rootChildTop.Add(new RootChild(null, hit), hit.Size);

        var directories = new List<ResultItem>();
        foreach (var node in directoryTop.ToDescendingArray()) directories.Add(new ResultItem(DirTable.PathOf(nodes, node.Id), node.Total));
        var rootChildren = new List<ResultItem>();
        foreach (var child in rootChildTop.ToDescendingArray())
        {
            rootChildren.Add(child.Directory is { } directory
                ? new ResultItem(DirTable.PathOf(nodes, directory.Id), directory.Total)
                : new ResultItem(DirTable.Combine(nodes[0].Name, child.File.Name), child.File.Size));
        }
        var files = new List<ResultItem>();
        foreach (var hit in walk.Files) files.Add(new ResultItem(DirTable.Combine(DirTable.PathOf(nodes, hit.DirId), hit.Name), hit.Size));
        var finalize = total.Elapsed - mark;

        var totals = walk.Totals;
        var counters = new FsCounters(totals.Scanned, totals.Denied, totals.Failed, totals.ReparseSkipped, nodes.Length, totals.Files, totals.Bytes);
        using var process = Process.GetCurrentProcess();
        var metrics = new FsMetrics(
            open, walk.WalkTime, aggregation, finalize, total.Elapsed,
            TimeSpan.FromSeconds((double)totals.EnumTicks / Stopwatch.Frequency),
            TimeSpan.FromSeconds((double)totals.IdleTicks / Stopwatch.Frequency),
            settings.Workers, walk.LargeFetch, walk.PeakQueuedDirs,
            totals.Entries, nodes.Length, totals.Bytes,
            GC.GetTotalAllocatedBytes() - allocatedAtStart, process.PeakWorkingSet64);
        return new FsResult(rootPath.Display, new ResultItem(rootPath.Display, nodes[0].Total), rootChildren.ToArray(), directories.ToArray(), files.ToArray(), counters, metrics, walk.ErrorSamples, nodes);
    }
}
```

`src\DirSizer.Fs\SelfTests.Reader.cs` (replace the whole file):

```csharp
using System.Runtime.CompilerServices;

// The native structure, the directory reader against a real temporary folder, and its LARGE_FETCH fallback against an injected find-first call.
static partial class FsSelfTests
{
    static partial void AddReaderTests(List<SelfTest> tests)
    {
        tests.Add(new("Win32FindData has the 592-byte native layout and a 64-bit file size", Win32FindDataLayout));
        tests.Add(new("reader lists entries, skips . and .., reports names and attributes", ReaderListsEntries));
        tests.Add(new("reader classifies find-first errors: missing, empty, denied, other", ReaderClassifiesErrors));
        tests.Add(new("LARGE_FETCH: rejected once, retried without, and off from then on", LargeFetchFallsBack));
        tests.Add(new("LARGE_FETCH: a failing retry is an ordinary failure and keeps the flag", LargeFetchKeptWhenRetryFails));
    }

    static void Win32FindDataLayout()
    {
        // 4 (attributes) + 3 * 8 (times) + 4 * 4 (sizes, reserved) + 260 * 2 (name) + 14 * 2 (alternate name)
        AssertEqual(592, Unsafe.SizeOf<Win32FindData>(), "WIN32_FIND_DATAW size");
        // The two halves of the size combine into 64 bits (a file over 4 GiB), without needing a huge file.
        var data = new Win32FindData { FileSizeHigh = 1, FileSizeLow = 5 };
        AssertEqual(4294967301L, Win32Find.FileSize(in data), "64-bit file size");
    }

    sealed class CollectingSink : IEntrySink
    {
        public readonly Dictionary<string, uint> Entries = [];
        public readonly Dictionary<string, long> Sizes = [];

        public void OnEntry(uint attributes, long size, ReadOnlySpan<char> name)
        {
            var key = new string(name);
            Entries[key] = attributes;
            Sizes[key] = size;
        }
    }

    // A find-first call that always fails with the given error, recording whether the LARGE_FETCH flag was set on each call.
    static FindFirstFn AlwaysFails(int error, List<bool> calls) =>
        (string pattern, ref Win32FindData data, bool largeFetch) =>
        {
            calls.Add(largeFetch);
            return new FindFirstResult(Win32Find.InvalidHandle, error);
        };

    static ReadResult ReadWithError(int error, out string calls)
    {
        var list = new List<bool>();
        var result = new FindFirstFactory(true, AlwaysFails(error, list)).Create().Read(@"\\?\C:\anything", new CollectingSink());
        calls = string.Join(',', list);
        return result;
    }

    // Stands in for a filesystem that rejects FIND_FIRST_EX_LARGE_FETCH with the given error; every other call is the real one.
    sealed class LargeFetchRejecter(int error)
    {
        public readonly List<bool> Calls = [];

        public FindFirstResult Call(string pattern, ref Win32FindData data, bool largeFetch)
        {
            lock (Calls) Calls.Add(largeFetch);
            return largeFetch ? new FindFirstResult(Win32Find.InvalidHandle, error) : Win32Find.FindFirst(pattern, ref data, false);
        }
    }

    static void ReaderListsEntries()
    {
        using var tree = new TempTree();
        tree.MakeFile("one.txt", 10);
        tree.MakeFile("\u65e5\u672c\u8a9e.txt", 20);
        tree.MakeDir("sub");
        tree.MakeFile("hidden.txt", 5);
        File.SetAttributes(tree.Full("hidden.txt"), FileAttributes.Hidden);

        var sink = new CollectingSink();
        var result = new FindFirstFactory().Create().Read(tree.Base, sink);
        AssertEqual(ReadOutcome.Complete, result.Outcome, "outcome");
        AssertEqual("hidden.txt,one.txt,sub,\u65e5\u672c\u8a9e.txt", string.Join(',', SortedNames(sink.Entries)), "entries (no . or ..), in ordinal order");
        Assert((sink.Entries["sub"] & Win32Find.DirectoryAttribute) != 0, "sub is a directory");
        Assert((sink.Entries["one.txt"] & Win32Find.DirectoryAttribute) == 0, "one.txt is not a directory");
        Assert((sink.Entries["hidden.txt"] & (uint)FileAttributes.Hidden) != 0, "hidden files are listed, with their attribute");
        AssertEqual(10L, sink.Sizes["one.txt"], "size of one.txt (the size fields are read from the right offsets)");
        AssertEqual(20L, sink.Sizes["日本語.txt"], "size of the Unicode-named file");
    }

    static string[] SortedNames(Dictionary<string, uint> entries)
    {
        var names = new List<string>(entries.Keys);
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    static void ReaderClassifiesErrors()
    {
        using var tree = new TempTree();
        var missing = new FindFirstFactory().Create().Read(tree.Full("does-not-exist"), new CollectingSink());
        AssertEqual(ReadOutcome.Failed, missing.Outcome, "a directory that does not exist");
        AssertEqual(3, missing.Error, "ERROR_PATH_NOT_FOUND");

        var empty = ReadWithError(Win32Find.ErrorFileNotFound, out var emptyCalls);
        AssertEqual(ReadOutcome.Complete, empty.Outcome, "ERROR_FILE_NOT_FOUND means an empty directory (a FAT or exFAT root has no dot entries)");
        AssertEqual("True", emptyCalls, "an empty directory is not retried");

        var denied = ReadWithError(Win32Find.ErrorAccessDenied, out var deniedCalls);
        AssertEqual(ReadOutcome.Denied, denied.Outcome, "ERROR_ACCESS_DENIED");
        AssertEqual("True", deniedCalls, "a denied directory is not retried");

        var other = ReadWithError(32, out var otherCalls);   // ERROR_SHARING_VIOLATION
        AssertEqual(ReadOutcome.Failed, other.Outcome, "any other error");
        AssertEqual(32, other.Error, "the error is kept");
        AssertEqual("True", otherCalls, "another error is not retried");
    }

    static void LargeFetchFallsBack()
    {
        using var tree = new TempTree();
        tree.MakeFile("one.txt", 10);
        var rejecter = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var factory = new FindFirstFactory(true, rejecter.Call);
        var reader = factory.Create();
        Assert(factory.LargeFetch, "the flag starts on");

        var first = reader.Read(tree.Base, new CollectingSink());
        AssertEqual(ReadOutcome.Complete, first.Outcome, "the retry without the flag succeeds");
        Assert(!factory.LargeFetch, "the flag is off after a successful retry");
        var second = reader.Read(tree.Base, new CollectingSink());
        AssertEqual(ReadOutcome.Complete, second.Outcome, "later reads work");
        AssertEqual("True,False,False", string.Join(',', rejecter.Calls), "calls: with the flag (rejected), retry without, then never with the flag again");
    }

    static void LargeFetchKeptWhenRetryFails()
    {
        var calls = new List<bool>();
        FindFirstResult AlwaysInvalidParameter(string pattern, ref Win32FindData data, bool largeFetch)
        {
            calls.Add(largeFetch);
            return new FindFirstResult(Win32Find.InvalidHandle, Win32Find.ErrorInvalidParameter);
        }
        var factory = new FindFirstFactory(true, AlwaysInvalidParameter);
        var result = factory.Create().Read(@"\\?\C:\anything", new CollectingSink());
        AssertEqual(ReadOutcome.Failed, result.Outcome, "outcome");
        AssertEqual(Win32Find.ErrorInvalidParameter, result.Error, "error");
        AssertEqual("True,False", string.Join(',', calls), "exactly one retry");
        Assert(factory.LargeFetch, "the flag stays on: the retry failed too, so the flag was not the cause");
    }
}
```

`src\DirSizer.Fs\SelfTests.Walk.cs` (replace the whole file):

```csharp
using System.Security.Principal;
using System.Text;

// The whole walk on real temporary folders, checked against an independent computation.
static partial class FsSelfTests
{
    const int WideFanOut = 10_000;
    const int DeepChain = 800;

    static partial void AddWalkTests(List<SelfTest> tests)
    {
        tests.Add(new("walk matches the independent oracle for 1, 3 and 8 workers", WalkMatchesOracle));
        tests.Add(new("walk does not enter a junction, but enters one named as the root", WalkJunction));
        tests.Add(new("walk counts a hard link in every directory that holds a name", WalkHardLink));
        tests.Add(new("walk skips a directory it may not read and counts it", WalkDenied));
        tests.Add(new("walk finishes on a very deep chain (1 and 8 workers)", WalkDeepChain));
        tests.Add(new("walk finishes on a very wide fan-out and reports the queue peak", WalkWideFanOut));
        tests.Add(new("walk with LARGE_FETCH rejected: one worker fails once, eight at most eight times", WalkLargeFetchRejected));
        tests.Add(new("walk counts a directory that fails and keeps the rest", WalkFailedDirectory));
        tests.Add(new("walk reports a bad root or bad settings as an error", WalkBadRoot));
        tests.Add(new("walk stops when canceled, before and while it runs", WalkCanceled));
        tests.Add(new("walk stops and rethrows when a worker throws", WalkWorkerThrows));
        tests.Add(new("walk leaves no worker running when the caller fails", WalkStopsWorkersWhenTheCallerFails));
    }

    static FsResult Scan(string root, int workers, bool files = false, int top = 25, CancellationToken cancel = default, FindFirstFn? findFirst = null) =>
        FsScanner.Scan(root, new ScanSettings(workers, top, files, false, cancel, findFirst is null ? null : new FindFirstFactory(true, findFirst)));

    // Nested and empty directories, a zero-byte file, Unicode names, a path over 260 characters, and files of distinct sizes.
    static TempTree StandardTree()
    {
        var tree = new TempTree();
        tree.MakeFile("a.bin", 1001);
        tree.MakeDir("empty");
        tree.MakeFile("zero.txt", 0);
        tree.MakeFile("Ünï\\日本語.txt", 2003);
        tree.MakeFile("deep\\l1\\l2\\l3\\f.bin", 4007);
        for (var index = 0; index < 50; index++) tree.MakeFile($"wide\\d{index:000}\\f.bin", 5000 + index);
        var segment = new string('x', 60);
        var nested = new StringBuilder(segment);
        for (var index = 0; index < 5; index++) nested.Append('\\').Append(segment);
        tree.MakeFile($"long\\{nested}\\deep.bin", 7011);
        return tree;
    }

    // The independent computation: the framework's own directory enumeration, no code from the tool. Keys are paths below the root.
    static (Dictionary<string, long> Totals, long Files) Oracle(string extendedRoot)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long files = 0;
        long Walk(DirectoryInfo directory, string relative)
        {
            long sum = 0;
            foreach (var file in directory.EnumerateFiles())
            {
                sum += file.Length;
                files++;
            }
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                sum += Walk(child, relative.Length == 0 ? child.Name : relative + "\\" + child.Name);
            }
            totals[relative] = sum;
            return sum;
        }
        Walk(new DirectoryInfo(extendedRoot), "");
        return (totals, files);
    }

    static Dictionary<string, long> TotalsOf(DirNode[] nodes)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var node in nodes) totals[DirTable.RelativePath(nodes, node.Id)] = node.Total;
        return totals;
    }

    static void AssertSameTotals(Dictionary<string, long> expected, Dictionary<string, long> actual, string label)
    {
        AssertEqual(expected.Count, actual.Count, $"{label}: number of directories");
        foreach (var (path, total) in expected)
        {
            Assert(actual.TryGetValue(path, out var found), $"{label}: directory missing: '{path}'");
            AssertEqual(total, found, $"{label}: total of '{path}'");
        }
    }

    static long[] Sizes(ResultItem[] items)
    {
        var sizes = new long[items.Length];
        for (var index = 0; index < items.Length; index++) sizes[index] = items[index].Size;
        return sizes;
    }

    static void WalkMatchesOracle()
    {
        using var tree = StandardTree();
        var (expected, fileCount) = Oracle(tree.Base);
        foreach (var workers in new[] { 1, 3, 8 })
        {
            var label = $"workers={workers}";
            var result = Scan(tree.Root, workers, files: true, top: 5);
            AssertSameTotals(expected, TotalsOf(result.Nodes), label);
            AssertEqual(fileCount, result.Counters.Files, $"{label}: files");
            AssertEqual((long)expected.Count, result.Counters.Directories, $"{label}: directories");
            AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the root total equals the sum of every file size");
            AssertEqual(result.Counters.Directories, result.Counters.DirectoriesScanned, $"{label}: every directory was read");
            AssertEqual(0L, result.Counters.Unreadable + result.Counters.ReparseSkipped, $"{label}: nothing skipped");
            AssertEqual(tree.Root, result.Root.Path, $"{label}: root path");
            AssertEqual(result.Metrics.Total, result.Metrics.PhaseSum, $"{label}: phases add up to the total");
            Assert(result.Metrics.Other >= TimeSpan.Zero && result.Metrics.Walk <= result.Metrics.Total, $"{label}: the residual is not negative and the walk fits in the total");

            // Distinct file sizes, so the order is fully defined.
            AssertEqual("7011,5049,5048,5047,5046", string.Join(',', Sizes(result.Files)), $"{label}: largest files");
            AssertEqual(result.Root.Size, result.Directories[0].Size, $"{label}: the root is the largest directory");
            AssertEqual(tree.Root, result.Directories[0].Path, $"{label}: and is listed first");
            // root children: wide (50 files 5000..5049), long, deep, the Unicode directory, then a.bin; the empty directory and zero.txt are below the cut.
            AssertEqual("251225,7011,4007,2003,1001", string.Join(',', Sizes(result.RootChildren)), $"{label}: root children");
            Assert(result.RootChildren[4].Path.EndsWith("\\a.bin"), $"{label}: a root-level file appears among the root's children");
        }

        // The boundary of the bounded selections: one result each.
        var single = Scan(tree.Root, 2, files: true, top: 1);
        AssertEqual("7011", string.Join(',', Sizes(single.Files)), "top=1: the largest file");
        AssertEqual(1, single.Directories.Length, "top=1: one directory");
        AssertEqual(tree.Root, single.Directories[0].Path, "top=1: the root");
        AssertEqual("251225", string.Join(',', Sizes(single.RootChildren)), "top=1: the largest root child");
    }

    static void WalkJunction()
    {
        using var tree = new TempTree();
        using var outside = new TempTree();
        outside.MakeFile("target.bin", 9013);
        tree.MakeFile("inside.bin", 100);
        var link = tree.Root + "\\link";
        if (RunProgram("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Root}\"") != 0) throw new SkipException("cannot create a junction here");
        tree.OnDispose(() => Directory.Delete(tree.Full("link")));

        var scan = Scan(tree.Root, 4);
        AssertEqual(100L, scan.Root.Size, "the junction's target is not counted");
        AssertEqual(1L, scan.Counters.ReparseSkipped, "the junction is counted as skipped");
        AssertEqual(1L, scan.Counters.Directories, "the junction is not a directory node");

        var viaRoot = Scan(link, 4);
        AssertEqual(9013L, viaRoot.Root.Size, "a junction named as the root is entered");
        AssertEqual(0L, viaRoot.Counters.ReparseSkipped, "nothing is skipped below it");
    }

    static void WalkHardLink()
    {
        using var tree = new TempTree();
        tree.MakeFile("a\\f.bin", 3000);
        tree.MakeDir("b");
        if (RunProgram("cmd.exe", $"/c mklink /H \"{tree.Root}\\b\\g.bin\" \"{tree.Root}\\a\\f.bin\"") != 0) throw new SkipException("cannot create a hard link here (not NTFS?)");
        var result = Scan(tree.Root, 2);
        var totals = TotalsOf(result.Nodes);
        AssertEqual(3000L, totals["a"], "directory a");
        AssertEqual(3000L, totals["b"], "directory b: the other name of the same file counts too");
        AssertEqual(6000L, totals[""], "the root counts the file once per name");
    }

    static void WalkDenied()
    {
        using var tree = new TempTree();
        tree.MakeFile("open\\f.bin", 111);
        tree.MakeFile("locked\\g.bin", 222);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new SkipException("cannot determine the current user");
        var locked = tree.Root + "\\locked";
        if (RunProgram("icacls.exe", $"\"{locked}\" /deny \"*{sid}:(RD)\"") != 0) throw new SkipException("cannot set a deny ACL here");
        tree.OnDispose(() => RunProgram("icacls.exe", $"\"{locked}\" /remove:d \"*{sid}\""));
        // The precondition of the whole test: the ACL must really keep this process out. Some environments (a restricted or sandboxed
        // token) ignore the deny entry, and then there is nothing to test.
        try
        {
            Directory.GetFileSystemEntries(locked);
            throw new SkipException("the deny ACL has no effect for this process (restricted token?)");
        }
        catch (UnauthorizedAccessException)
        {
        }

        var result = Scan(tree.Root, 4);
        var counters = result.Counters;
        AssertEqual(1L, counters.DirectoriesDenied, $"the locked directory is counted (scanned {counters.DirectoriesScanned}, failed {counters.DirectoriesFailed}; {string.Join(" | ", result.ErrorSamples)})");
        AssertEqual(0L, result.Counters.DirectoriesFailed, "and is not a failure");
        AssertEqual(111L, result.Root.Size, "the readable part of the tree is complete; the locked file is not counted");
        AssertEqual(3L, result.Counters.Directories, "the locked directory is still a node");

        var rootError = ThrownIOException(() => Scan(locked, 2));
        Assert(rootError.Message.Contains("Cannot read"), "an unreadable root is an error, not a result");
    }

    static IOException ThrownIOException(Action action)
    {
        try
        {
            action();
        }
        catch (IOException exception)
        {
            return exception;
        }
        throw new Exception("expected IOException, nothing was thrown");
    }

    static void WalkDeepChain()
    {
        using var tree = new TempTree();
        var chain = new StringBuilder("d");
        for (var index = 1; index < DeepChain; index++) chain.Append("\\d");
        tree.MakeFile(chain + "\\bottom.bin", 12345);
        foreach (var workers in new[] { 1, 8 })
        {
            var result = Scan(tree.Root, workers);
            AssertEqual(12345L, result.Root.Size, $"workers={workers}: the file at the bottom reaches the root");
            AssertEqual((long)DeepChain + 1, result.Counters.Directories, $"workers={workers}: directories");
        }
    }

    static void WalkWideFanOut()
    {
        using var tree = new TempTree();
        tree.MakeFile("many\\f.bin", 7);
        for (var index = 0; index < WideFanOut; index++) tree.MakeDir($"many\\e{index:00000}");
        foreach (var workers in new[] { 1, 8 })
        {
            var result = Scan(tree.Root, workers);
            AssertEqual(7L, result.Root.Size, $"workers={workers}: total");
            AssertEqual((long)WideFanOut + 2, result.Counters.Directories, $"workers={workers}: directories");
            // One worker pushes all the sub-directories before any of them is taken: the stack really is unbounded.
            Assert(result.Metrics.PeakQueuedDirs >= WideFanOut, $"workers={workers}: peak queued directories was {result.Metrics.PeakQueuedDirs}, expected at least {WideFanOut}");
            Console.WriteLine($"      workers={workers}: peak_queued_dirs={result.Metrics.PeakQueuedDirs}, peak_working_set={result.Metrics.PeakWorkingSetBytes / 1048576} MiB");
        }
    }

    static void WalkLargeFetchRejected()
    {
        using var tree = StandardTree();
        var (expected, _) = Oracle(tree.Base);

        var one = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var single = Scan(tree.Root, 1, findFirst: one.Call);
        AssertSameTotals(expected, TotalsOf(single.Nodes), "workers=1");
        AssertEqual(1, CountTrue(one.Calls), "one worker: exactly one call with the flag, and it failed");
        AssertEqual(true, one.Calls[0], "the first call is the one with the flag");
        Assert(!single.Metrics.LargeFetch, "the flag is reported off");
        Assert(one.Calls.Count > 2, "the walk went on making calls");

        var many = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var parallel = Scan(tree.Root, 8, findFirst: many.Call);
        AssertSameTotals(expected, TotalsOf(parallel.Nodes), "workers=8");
        Assert(CountTrue(many.Calls) <= 8, $"eight workers: at most eight calls with the flag, got {CountTrue(many.Calls)}");
        Assert(!parallel.Metrics.LargeFetch, "the flag is reported off");
    }

    static int CountTrue(List<bool> values)
    {
        var count = 0;
        foreach (var value in values)
        {
            if (value) count++;
        }
        return count;
    }

    // Find-first fails with ERROR_SHARING_VIOLATION (32) for the directory "bad" and is the real call for every other directory.
    static void WalkFailedDirectory()
    {
        using var tree = new TempTree();
        tree.MakeFile("ok\\f.bin", 111);
        tree.MakeFile("bad\\g.bin", 222);
        tree.MakeFile("z.bin", 5);
        FindFirstResult FailBad(string pattern, ref Win32FindData data, bool largeFetch) =>
            pattern.Contains("\\bad\\") ? new FindFirstResult(Win32Find.InvalidHandle, 32) : Win32Find.FindFirst(pattern, ref data, largeFetch);
        foreach (var workers in new[] { 1, 4 })
        {
            var label = $"workers={workers}";
            var result = Scan(tree.Root, workers, findFirst: FailBad);
            AssertEqual(116L, result.Root.Size, $"{label}: everything except the failed directory is counted");
            AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the total still equals the sum of the file sizes");
            AssertEqual(1L, result.Counters.DirectoriesFailed, $"{label}: one failed directory");
            AssertEqual(0L, result.Counters.DirectoriesDenied, $"{label}: and it is not counted as denied");
            AssertEqual(2L, result.Counters.DirectoriesScanned, $"{label}: the root and ok were read");
            AssertEqual(3L, result.Counters.Directories, $"{label}: the failed directory is still a node");
            AssertEqual(1, result.ErrorSamples.Length, $"{label}: one error sample");
            Assert(result.ErrorSamples[0].Contains("\\bad") && result.ErrorSamples[0].Contains("error 32"), $"{label}: the sample names the directory and the error: {result.ErrorSamples[0]}");
        }
    }

    static void WalkBadRoot()
    {
        using var tree = new TempTree();
        tree.MakeFile("a.bin", 1);
        AssertThrows<ArgumentException>(() => Scan(tree.Root + "\\missing", 2), "missing directory");
        AssertThrows<ArgumentException>(() => Scan(tree.Root + "\\a.bin", 2), "a file is not a directory");
        // No worker would read anything and the result would look like an empty tree.
        AssertThrows<ArgumentException>(() => Scan(tree.Root, 0), "zero workers");
        AssertThrows<ArgumentException>(() => Scan(tree.Root, -1), "a negative number of workers");
        AssertEqual(ReadOutcome.NotRead, default(ReadResult).Outcome, "a read result that was never filled in is not a success");
    }

    static void WalkCanceled()
    {
        using var tree = StandardTree();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        AssertThrows<OperationCanceledException>(() => Scan(tree.Root, 4, cancel: canceled.Token), "canceled token");

        // Canceled while the walk is running: every find-first call is slow, so the workers are busy or waiting when the token fires.
        FindFirstResult Slow(string pattern, ref Win32FindData data, bool largeFetch)
        {
            Thread.Sleep(40);
            return Win32Find.FindFirst(pattern, ref data, largeFetch);
        }
        using var late = new CancellationTokenSource();
        late.CancelAfter(150);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        AssertThrows<OperationCanceledException>(() => Scan(tree.Root, 4, cancel: late.Token, findFirst: Slow), "canceled while running");
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert(elapsed < TimeSpan.FromSeconds(10), $"the walk stopped promptly after the cancel ({elapsed.TotalSeconds:N1} s)");
    }

    static void WalkWorkerThrows()
    {
        using var tree = StandardTree();
        FindFirstResult Boom(string pattern, ref Win32FindData data, bool largeFetch) => throw new InvalidOperationException("boom");
        try
        {
            Scan(tree.Root, 4, findFirst: Boom);
            throw new Exception("an exception in a worker must reach the caller, but nothing was thrown");
        }
        catch (InvalidOperationException exception)
        {
            AssertEqual("boom", exception.Message, "the worker's own exception reaches the caller (and the walk does not hang)");
        }
    }

    // If Walker.Run is left early (here: the progress callback throws) no worker may go on walking in the background.
    static void WalkStopsWorkersWhenTheCallerFails()
    {
        using var tree = StandardTree();
        var calls = 0;
        FindFirstResult Slow(string pattern, ref Win32FindData data, bool largeFetch)
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(100);
            return Win32Find.FindFirst(pattern, ref data, largeFetch);
        }
        var root = new DirNode(0, -1, tree.Root);
        try
        {
            new Walker(new FindFirstFactory(true, Slow)).Run(root, tree.Base, 2, 5, false, default, (directories, files) => throw new InvalidOperationException("progress failed"));
            throw new Exception("the failing progress callback must reach the caller, but nothing was thrown");
        }
        catch (InvalidOperationException exception)
        {
            AssertEqual("progress failed", exception.Message, "the callback's exception reaches the caller");
        }
        var atThrow = Volatile.Read(ref calls);
        Thread.Sleep(500);
        AssertEqual(atThrow, Volatile.Read(ref calls), "no worker is still walking after Run has thrown");
        Assert(atThrow < 65, $"the walk was cut short ({atThrow} of 65 directories were read)");
    }
}
```

`src\DirSizer.Fs\Walker.cs` (replace the whole file):

```csharp
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

// The parallel directory walk: a fixed number of worker threads take directories from one shared LIFO stack, enumerate them,
// and push the sub-directories they find. The stack is intentionally not capacity-bounded (a worker is also the producer of
// new tasks and would deadlock on a full stack); the number of workers is what is bounded.

sealed class WorkQueue
{
    readonly Stack<(DirNode Node, string Path)> _stack = new();
    readonly object _lock = new();
    int _pending;       // directories on the stack plus directories being enumerated
    int _peakQueued;
    bool _canceled;

    public int PeakQueued
    {
        get { lock (_lock) return _peakQueued; }
    }

    public void Seed(DirNode node, string path)
    {
        lock (_lock)
        {
            _stack.Push((node, path));
            _pending++;
            _peakQueued = Math.Max(_peakQueued, _stack.Count);
        }
    }

    // Blocks until a directory is available. Returns false when the walk is over: nothing is queued or in progress, or it was canceled.
    public bool TryTake(out (DirNode Node, string Path) task)
    {
        lock (_lock)
        {
            while (true)
            {
                if (_canceled)
                {
                    task = default;
                    return false;
                }
                if (_stack.Count > 0)
                {
                    task = _stack.Pop();
                    return true;
                }
                if (_pending == 0)
                {
                    task = default;
                    return false;
                }
                Monitor.Wait(_lock);
            }
        }
    }

    // Called once for every directory taken, with the sub-directories it produced. The new tasks are counted before the finished
    // one is subtracted, so _pending cannot reach zero while work remains.
    public void Finish(List<(DirNode Node, string Path)> children)
    {
        lock (_lock)
        {
            foreach (var child in children) _stack.Push(child);
            _pending += children.Count - 1;
            _peakQueued = Math.Max(_peakQueued, _stack.Count);
            if (children.Count > 0 || _pending == 0) Monitor.PulseAll(_lock);
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _canceled = true;
            Monitor.PulseAll(_lock);
        }
    }
}

// Counters kept by one worker and summed after the join. The main thread may read them while the walk runs (progress); each has
// exactly one writer.
sealed class Counters
{
    public long Scanned, Denied, Failed, ReparseSkipped, Files, Entries, Bytes, EnumTicks, IdleTicks;

    public void Add(Counters other)
    {
        Scanned += other.Scanned;
        Denied += other.Denied;
        Failed += other.Failed;
        ReparseSkipped += other.ReparseSkipped;
        Files += other.Files;
        Entries += other.Entries;
        Bytes += other.Bytes;
        EnumTicks += other.EnumTicks;
        IdleTicks += other.IdleTicks;
    }
}

sealed class Worker : IEntrySink
{
    public const int MaxErrorSamples = 20;

    readonly Walker _walker;
    readonly WorkQueue _queue;
    readonly IDirectoryEnumerator _enumerator;
    readonly BoundedTop<FileHit>? _files;
    readonly BoundedTop<FileHit> _rootFiles;
    readonly List<(DirNode Node, string Path)> _children = [];
    DirNode _node = null!;
    string _path = null!;
    long _ownSize;

    public readonly Counters Counters = new();
    public readonly List<DirNode> Created = [];
    public readonly List<string> ErrorSamples = [];
    public BoundedTop<FileHit>? Files => _files;
    public BoundedTop<FileHit> RootFiles => _rootFiles;

    public Worker(Walker walker, WorkQueue queue, IDirectoryEnumerator enumerator, int top, bool collectFiles)
    {
        _walker = walker;
        _queue = queue;
        _enumerator = enumerator;
        _files = collectFiles ? new BoundedTop<FileHit>(top) : null;
        _rootFiles = new BoundedTop<FileHit>(top);
    }

    public void Run()
    {
        try
        {
            while (true)
            {
                var waitStart = Stopwatch.GetTimestamp();
                var taken = _queue.TryTake(out var task);
                Counters.IdleTicks += Stopwatch.GetTimestamp() - waitStart;
                if (!taken) return;
                Process(task.Node, task.Path);
            }
        }
        catch (Exception exception)
        {
            _walker.Fail(exception, _queue);
        }
    }

    void Process(DirNode node, string path)
    {
        _node = node;
        _path = path;
        _ownSize = 0;
        _children.Clear();
        var start = Stopwatch.GetTimestamp();
        var result = _enumerator.Read(path, this);
        Counters.EnumTicks += Stopwatch.GetTimestamp() - start;
        node.OwnFileSize = _ownSize;
        switch (result.Outcome)
        {
            case ReadOutcome.Complete:
                Counters.Scanned++;
                break;
            case ReadOutcome.Denied:
                Counters.Denied++;
                break;
            default:
                Counters.Failed++;
                if (ErrorSamples.Count < MaxErrorSamples)
                    ErrorSamples.Add($"{RootPath.ToDisplay(path)}: {Marshal.GetPInvokeErrorMessage(result.Error).TrimEnd()} (error {result.Error})");
                break;
        }
        if (node.Id == 0) _walker.RootRead = result;
        _queue.Finish(_children);
    }

    public void OnEntry(uint attributes, long size, ReadOnlySpan<char> name)
    {
        Counters.Entries++;
        if ((attributes & Win32Find.DirectoryAttribute) != 0)
        {
            // A reparse-point directory (junction, directory symlink, mount point, ...) is never entered and contributes nothing.
            if ((attributes & Win32Find.ReparsePointAttribute) != 0)
            {
                Counters.ReparseSkipped++;
                return;
            }
            var childName = new string(name);
            var child = new DirNode(_walker.NextId(), _node.Id, childName);
            Created.Add(child);
            _children.Add((child, DirTable.Combine(_path, childName)));
            return;
        }

        Counters.Files++;
        Counters.Bytes += size;
        _ownSize += size;
        if (size <= 0) return;
        var forFiles = _files is not null && _files.WouldAccept(size);
        var forRoot = _node.Id == 0 && _rootFiles.WouldAccept(size);
        if (!forFiles && !forRoot) return;
        // The name is created only for a file that is actually kept.
        var hit = new FileHit(_node.Id, new string(name), size);
        if (forFiles) _files!.Add(hit, size);
        if (forRoot) _rootFiles.Add(hit, size);
    }
}

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

sealed class Walker(IEnumeratorFactory? enumerators = null)
{
    int _nextId;                       // the root is 0; the first id handed out is 1
    Exception? _failure;

    public ReadResult RootRead;        // written by the worker that enumerates the root

    public int NextId() => Interlocked.Increment(ref _nextId);

    // Called from a worker thread that hit an unexpected exception: remember the first one and stop everybody.
    public void Fail(Exception exception, WorkQueue queue)
    {
        Interlocked.CompareExchange(ref _failure, exception, null);
        queue.Cancel();
    }

    public WalkResult Run(DirNode root, string rootExtendedPath, int workers, int top, bool collectFiles, CancellationToken cancel, Action<long, long>? progress)
    {
        var queue = new WorkQueue();
        var factory = enumerators ?? new FindFirstFactory();
        var all = new List<Worker>(workers);
        var threads = new List<Thread>(workers);
        for (var index = 0; index < workers; index++)
        {
            var worker = new Worker(this, queue, factory.Create(), top, collectFiles);
            all.Add(worker);
            // Background threads: if Run is left early, the workers must not keep the process alive.
            threads.Add(new Thread(worker.Run) { Name = $"dirsizer-worker-{index}", IsBackground = true });
        }
        queue.Seed(root, rootExtendedPath);
        using var registration = cancel.Register(queue.Cancel);

        var start = Stopwatch.GetTimestamp();
        var started = 0;
        try
        {
            foreach (var thread in threads)
            {
                thread.Start();
                started++;
            }
            foreach (var thread in threads)
            {
                while (!thread.Join(250))
                {
                    if (progress is null) continue;
                    long directoriesSoFar = 0, filesSoFar = 0;
                    foreach (var worker in all)
                    {
                        directoriesSoFar += Volatile.Read(ref worker.Counters.Scanned) + Volatile.Read(ref worker.Counters.Denied) + Volatile.Read(ref worker.Counters.Failed);
                        filesSoFar += Volatile.Read(ref worker.Counters.Files);
                    }
                    progress(directoriesSoFar, filesSoFar);
                }
            }
        }
        catch
        {
            // A thread that could not be started, or a progress callback that threw: nobody may be left walking. Stop the workers,
            // wait for the ones that did start, and let the exception go on.
            queue.Cancel();
            for (var index = 0; index < started; index++) threads[index].Join();
            throw;
        }
        var walkTime = Stopwatch.GetElapsedTime(start);

        var failure = _failure;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        cancel.ThrowIfCancellationRequested();

        var totals = new Counters();
        var created = new List<List<DirNode>>(workers);
        var files = collectFiles ? new BoundedTop<FileHit>(top) : null;
        var rootFiles = new BoundedTop<FileHit>(top);
        var samples = new List<string>();
        foreach (var worker in all)
        {
            totals.Add(worker.Counters);
            created.Add(worker.Created);
            if (files is not null && worker.Files is not null) files.AddAll(worker.Files);
            rootFiles.AddAll(worker.RootFiles);
            foreach (var sample in worker.ErrorSamples)
            {
                if (samples.Count < Worker.MaxErrorSamples) samples.Add(sample);
            }
        }
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
    }
}
```

`src\DirSizer.Fs\Win32Find.cs` (replace the whole file):

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// WIN32_FIND_DATAW, field for field (592 bytes). Every field is an integer, so the struct is blittable and is passed to the API by
// reference with no marshalling code and no unsafe code. The file name is an inline array of ushort rather than char: a char
// field would make the marshaller depend on the struct's CharSet. The FILETIMEs are pairs of uints on purpose: a long would be
// aligned to 8 bytes and shift every field after it.
[InlineArray(260)]
struct FileNameBuffer
{
    ushort _element0;
}

[InlineArray(14)]
struct AlternateNameBuffer
{
    ushort _element0;
}

struct Win32FindData
{
    public uint FileAttributes;
    public uint CreationTimeLow, CreationTimeHigh;
    public uint LastAccessTimeLow, LastAccessTimeHigh;
    public uint LastWriteTimeLow, LastWriteTimeHigh;
    public uint FileSizeHigh, FileSizeLow;
    public uint Reserved0, Reserved1;
    public FileNameBuffer FileName;
    public AlternateNameBuffer AlternateFileName;
}

readonly record struct FindFirstResult(nint Handle, int Error);

// The one call that the self-test replaces to inject failures (see FindFirstEnumerator). Not the enumerator abstraction: that is
// IDirectoryEnumerator.
delegate FindFirstResult FindFirstFn(string pattern, ref Win32FindData data, bool largeFetch);

static class Win32Find
{
    public const uint DirectoryAttribute = 0x10;
    public const uint ReparsePointAttribute = 0x400;
    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorInvalidParameter = 87;
    public const nint InvalidHandle = -1;

    const int FindExInfoBasic = 1;          // no 8.3 short-name lookup
    const int FindExSearchNameMatch = 0;
    const int FindFirstExLargeFetch = 2;    // larger directory query buffer

    public static FindFirstResult FindFirst(string pattern, ref Win32FindData data, bool largeFetch)
    {
        var handle = FindFirstFileExW(pattern, FindExInfoBasic, ref data, FindExSearchNameMatch, 0, largeFetch ? FindFirstExLargeFetch : 0);
        return handle == InvalidHandle ? new FindFirstResult(handle, Marshal.GetLastPInvokeError()) : new FindFirstResult(handle, 0);
    }

    public static bool TryFindNext(nint handle, ref Win32FindData data, out int error)
    {
        if (FindNextFileW(handle, ref data))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    public static void Close(nint handle) => FindClose(handle);

    public static long FileSize(in Win32FindData entry) => ((long)entry.FileSizeHigh << 32) | entry.FileSizeLow;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint FindFirstFileExW(string lpFileName, int fInfoLevelId, ref Win32FindData lpFindFileData, int fSearchOp, nint lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindNextFileW(nint hFindFile, ref Win32FindData lpFindFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindClose(nint hFindFile);
}
```


```powershell
git rm src\DirSizer.Fs\DirectoryReader.cs
```

- [ ] **Step 2: Build and run the tests**

```powershell
Normalize-FsProject   # helper from the ground rules, defined in this same command
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Warning(s)`, `0 Error(s)` and `28 self-tests passed, 0 skipped.` (the same tests as before; only their calls to the reader changed).

- [ ] **Step 3: The acceptance of this step: the snapshots are identical to the baseline**

```powershell
foreach ($p in @(@('T:\', 'T'), @('C:\Program Files\dotnet', 'dotnet'), @('C:\Windows\System32\drivers', 'drivers'))) {
    .\scripts\Get-FsSnapshot.ps1 -Path $p[0] -Out "artifacts\snapshots\step2-$($p[1]).json" | Out-Null
    "{0,-28} before == after: {1}" -f $p[0], ([IO.File]::ReadAllText((Resolve-Path "artifacts\snapshots\baseline-$($p[1]).json")) -ceq [IO.File]::ReadAllText((Resolve-Path "artifacts\snapshots\step2-$($p[1]).json")))
}
.\scripts\Get-FsSnapshot.ps1 -Path 'C:\Windows\System32\drivers' -Files -Out artifacts\snapshots\step2-drivers-files.json | Out-Null
```

Expected: `before == after: True` for all three. If any is `False`, stop: the contract changed behaviour, which is exactly what this step must not do; report the first difference.

- [ ] **Step 4: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs
git commit -m @'
Enumerator contract: find (FindFirstFileExW) behind IDirectoryEnumerator, behaviour unchanged

The walk sees only attributes, size and name through an API-neutral sink; snapshots of three
quiescent trees are identical before and after.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 3: `--enumerator`, enumerators B and C, conformance tests

**Files (from the difference between the Step 1 state and the final state):**

- Create: `src\DirSizer.Fs\EnumeratorSpec.cs`
- Create: `src\DirSizer.Fs\HandleEnumerators.cs`
- Create: `src\DirSizer.Fs\SelfTests.Enumerators.cs`
- Modify (replace whole file): `src\DirSizer.Fs\FsOptions.cs`
- Modify (replace whole file): `src\DirSizer.Fs\FsOutput.cs`
- Modify (replace whole file): `src\DirSizer.Fs\FsScanner.cs`
- Modify (replace whole file): `src\DirSizer.Fs\Program.cs`
- Modify (replace whole file): `src\DirSizer.Fs\SelfTests.Cli.cs`
- Modify (replace whole file): `src\DirSizer.Fs\SelfTests.Output.cs`
- Modify (replace whole file): `src\DirSizer.Fs\SelfTests.Walk.cs`
- Modify (replace whole file): `src\DirSizer.Fs\SelfTests.cs`

- [ ] **Step 1: Write the files**

Write each file below with the Write tool, exactly as shown (create or overwrite).

`src\DirSizer.Fs\EnumeratorSpec.cs` (new):

```csharp
enum EnumeratorKind { Find, Handle, Nt }

// The information class of a handle-based enumerator: what each directory entry contains. Only what the walk needs is read from it.
enum EntryClass { None, Dir, Full, IdExtd }

// --enumerator=<name>[:<class>[:<KiB>]]: which API reads the directories, and how. `find` is the default and the baseline.
sealed record EnumeratorSpec(EnumeratorKind Kind, EntryClass Class, bool LargeFetch, int BufferKiB)
{
    public const int DefaultBufferKiB = 64;
    public const int MinBufferKiB = 4;
    public const int MaxBufferKiB = 1024;

    public static EnumeratorSpec Default { get; } = new(EnumeratorKind.Find, EntryClass.None, true, 0);

    // The canonical form, which is what the benchmark line and the JSON report: find, find:nolarge, handle:idextd:64, nt:dir:4.
    public string Canonical => Kind switch
    {
        EnumeratorKind.Find => LargeFetch ? "find" : "find:nolarge",
        EnumeratorKind.Handle => $"handle:{ClassName(Class)}:{BufferKiB}",
        _ => $"nt:{ClassName(Class)}:{BufferKiB}",
    };

    // Throws ArgumentException with a message that says what is accepted.
    public static EnumeratorSpec Parse(string text)
    {
        var parts = text.Trim().ToLowerInvariant().Split(':');
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
    }

    static EnumeratorSpec ParseBuffered(EnumeratorKind kind, string[] parts, EntryClass[] accepted, string message)
    {
        if (parts.Length > 3) throw new ArgumentException($"--enumerator {kind.ToString().ToLowerInvariant()} takes at most a class and a buffer size in KiB.");

        // The default is the lightest class that also works on exFAT (idextd does not: it fails with ERROR_INVALID_PARAMETER there).
        var entryClass = kind == EnumeratorKind.Handle ? EntryClass.Full : EntryClass.Dir;
        if (parts.Length >= 2)
        {
            entryClass = parts[1] switch { "dir" => EntryClass.Dir, "full" => EntryClass.Full, "idextd" => EntryClass.IdExtd, _ => EntryClass.None };
            if (Array.IndexOf(accepted, entryClass) < 0) throw new ArgumentException($"--enumerator {message}.");
        }
        var bufferKiB = DefaultBufferKiB;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], out bufferKiB) || bufferKiB < MinBufferKiB || bufferKiB > MaxBufferKiB)
                throw new ArgumentException($"--enumerator buffer size must be a whole number of KiB from {MinBufferKiB} to {MaxBufferKiB}.");
        }
        return new EnumeratorSpec(kind, entryClass, false, bufferKiB);
    }

    static string ClassName(EntryClass entryClass) => entryClass switch
    {
        EntryClass.Dir => "dir",
        EntryClass.Full => "full",
        _ => "idextd",
    };

    // The find-first delegate is the self-test's injection point and only means something for `find`.
    public IEnumeratorFactory CreateFactory(FindFirstFn? findFirst = null) =>
        Kind == EnumeratorKind.Find ? new FindFirstFactory(LargeFetch, findFirst) : new BufferedFactory(this);
}
```

`src\DirSizer.Fs\HandleEnumerators.cs` (new):

```csharp
using System.Buffers.Binary;
using System.Runtime.InteropServices;

// The two enumerators that read a directory through a handle (B: GetFileInformationByHandleEx, C: NtQueryDirectoryFileEx). They
// share everything except the one call that fills the buffer: open the directory once, ask for as many entries as fit in the
// worker's buffer, follow the NextEntryOffset chain, close the handle. No file is ever opened.

static class DirectoryHandle
{
    const uint FileListDirectory = 0x1;
    const uint ShareAll = 0x7;                  // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint BackupSemantics = 0x02000000;    // FILE_FLAG_BACKUP_SEMANTICS, required to open a directory

    public const int ErrorInvalidData = 13;
    public const int ErrorInsufficientBuffer = 122;

    // Returns the handle, or InvalidHandle with the Win32 error.
    public static nint Open(string extendedPath, out int error)
    {
        var handle = CreateFileW(extendedPath, FileListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        error = handle == Win32Find.InvalidHandle ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    public static void Close(nint handle) => CloseHandle(handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint hObject);
}

abstract class BufferedEnumerator(int bufferKiB, int nameOffset) : IDirectoryEnumerator
{
    // This worker's buffer, allocated once.
    protected readonly byte[] Buffer = new byte[bufferKiB * 1024];

    // The offset of the file name inside an entry: 64 for FileDirectoryInformation, 68 for FileFullDirectoryInformation and
    // 88 for FileIdExtdDirectoryInformation (the Win32 structures of B have the same layout).
    protected static int NameOffset(EntryClass entryClass) => entryClass switch
    {
        EntryClass.Dir => 64,
        EntryClass.Full => 68,
        _ => 88,
    };

    // One query for as many entries as fit in the buffer. `first` is true for the first query of a directory. Returns 0 when the
    // buffer holds entries (`bytes` of it are valid), ERROR_NO_MORE_FILES at the end of the directory, or another Win32 error.
    protected abstract int Query(nint handle, bool first, out int bytes);

    public ReadResult Read(string directoryPath, IEntrySink sink)
    {
        var handle = DirectoryHandle.Open(directoryPath, out var openError);
        // For find, ERROR_FILE_NOT_FOUND means "no entries"; here it would mean that the directory does not exist, so only
        // access denied is singled out and every other open error is a failure.
        if (handle == Win32Find.InvalidHandle)
            return new ReadResult(openError == Win32Find.ErrorAccessDenied ? ReadOutcome.Denied : ReadOutcome.Failed, openError);
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
        finally
        {
            DirectoryHandle.Close(handle);
        }
    }

    // Follows the chain of entries (NextEntryOffset 0 marks the last one). Layout of the part that all used classes share:
    // NextEntryOffset (0), EndOfFile (40), FileAttributes (56), FileNameLength in bytes (60); the name starts at nameOffset.
    // Returns false if an entry does not fit in the bytes that the query returned.
    bool Parse(int bytes, IEntrySink sink)
    {
        var data = new ReadOnlySpan<byte>(Buffer, 0, bytes);
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > data.Length - nameOffset) return false;
            var entry = data[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var size = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);
            if (nameBytes > entry.Length - nameOffset || (nameBytes & 1) != 0) return false;
            var name = MemoryMarshal.Cast<byte, char>(entry.Slice(nameOffset, (int)nameBytes));
            if (!EntryNames.IsDot(name)) sink.OnEntry(attributes, size, name);
            if (next == 0) return true;
            if (next > (uint)data.Length) return false;
            offset += (int)next;
        }
    }
}

// B: GetFileInformationByHandleEx. The first query of a directory uses the Restart class, every later query the plain class.
sealed class HandleInfoEnumerator(EntryClass entryClass, int bufferKiB) : BufferedEnumerator(bufferKiB, NameOffset(entryClass))
{
    // FILE_INFO_BY_HANDLE_CLASS: FileFullDirectoryInfo 14, FileFullDirectoryRestartInfo 15,
    // FileIdExtdDirectoryInfo 19, FileIdExtdDirectoryRestartInfo 20.
    readonly int _restartClass = entryClass == EntryClass.IdExtd ? 20 : 15;
    readonly int _nextClass = entryClass == EntryClass.IdExtd ? 19 : 14;

    protected override int Query(nint handle, bool first, out int bytes)
    {
        // The call does not say how many bytes it wrote; the chain of entries ends with NextEntryOffset 0.
        bytes = Buffer.Length;
        return GetFileInformationByHandleEx(handle, first ? _restartClass : _nextClass, ref Buffer[0], (uint)Buffer.Length) ? 0 : Marshal.GetLastPInvokeError();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(nint hFile, int fileInformationClass, ref byte lpFileInformation, uint dwBufferSize);
}

// C: NtQueryDirectoryFileEx (ntdll.dll), a documented WDK Native System Service, Windows 10 version 1709 or later.
sealed class NtQueryEnumerator(EntryClass entryClass, int bufferKiB) : BufferedEnumerator(bufferKiB, NameOffset(entryClass))
{
    // FILE_INFORMATION_CLASS: FileDirectoryInformation 1, FileFullDirectoryInformation 2, FileIdExtdDirectoryInformation 60.
    // QueryFlags: SL_RESTART_SCAN (0x1) on the first query of a directory only. SL_RETURN_SINGLE_ENTRY (0x2) is never used: it
    // makes the file system return one entry per query.
    const uint RestartScan = 0x1;
    const int StatusNoMoreFiles = unchecked((int)0x80000006);

    readonly int _class = entryClass switch { EntryClass.Dir => 1, EntryClass.Full => 2, _ => 60 };
    IoStatusBlock _status;

    protected override int Query(nint handle, bool first, out int bytes)
    {
        bytes = 0;
        var status = NtQueryDirectoryFileEx(handle, 0, 0, 0, ref _status, ref Buffer[0], (uint)Buffer.Length, _class, first ? RestartScan : 0, 0);
        if (status == StatusNoMoreFiles) return Win32Find.ErrorNoMoreFiles;
        if (status < 0)
        {
            var error = (int)RtlNtStatusToDosError(status);
            return error == 0 ? DirectoryHandle.ErrorInvalidData : error;
        }
        bytes = (int)_status.Information;
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryDirectoryFileEx(nint fileHandle, nint evt, nint apcRoutine, nint apcContext, ref IoStatusBlock ioStatusBlock, ref byte fileInformation, uint length, int fileInformationClass, uint queryFlags, nint fileName);

    [DllImport("ntdll.dll")]
    static extern uint RtlNtStatusToDosError(int status);
}

sealed class BufferedFactory(EnumeratorSpec spec) : IEnumeratorFactory
{
    public string Name => spec.Canonical;

    public bool LargeFetch => false;

    public IDirectoryEnumerator Create() => spec.Kind == EnumeratorKind.Handle
        ? new HandleInfoEnumerator(spec.Class, spec.BufferKiB)
        : new NtQueryEnumerator(spec.Class, spec.BufferKiB);
}
```

`src\DirSizer.Fs\SelfTests.Enumerators.cs` (new):

```csharp
using System.Security.Principal;

// Every enumerator against the same fixtures and the same independent oracle (the framework's own directory enumeration).
static partial class FsSelfTests
{
    // A (find, find:nolarge), then B and C in their classes, at the default 64 KiB buffer and at the 4 KiB minimum, where a
    // directory of a few hundred entries needs many refills of the buffer: the boundary at which a parser goes wrong.
    static readonly string[] EnumeratorSpecTexts =
    [
        "find", "find:nolarge",
        "handle:idextd:64", "handle:full:64", "handle:idextd:4", "handle:full:4",
        "nt:dir:64", "nt:full:64", "nt:idextd:64", "nt:dir:4", "nt:full:4", "nt:idextd:4",
    ];

    const uint AttributeMask = 0x17;    // read-only, hidden, system, directory: what the framework and the API report alike

    static partial void AddEnumeratorTests(List<SelfTest> tests)
    {
        tests.Add(new("every enumerator lists a directory like the framework does (300 entries, several buffer refills)", EnumeratorsListLikeTheFramework));
        tests.Add(new("every enumerator reports a missing directory as failed and an empty one as complete", EnumeratorsClassifyDirectories));
        tests.Add(new("every enumerator reports a directory that may not be read as denied", EnumeratorsReportDenied));
        tests.Add(new("every enumerator shows the reparse attribute of a junction entry", EnumeratorsShowJunctions));
        tests.Add(new("every enumerator walks the standard tree like the oracle, with 1, 3 and 8 workers", EnumeratorsWalkLikeTheOracle));
    }

    static void EnumeratorsListLikeTheFramework()
    {
        using var tree = new TempTree();
        for (var index = 0; index < 300; index++) tree.MakeFile($"file-{index:000}.txt", index * 7 + 1);
        for (var index = 0; index < 20; index++) tree.MakeDir($"dir-{index:00}");
        tree.MakeFile("日本語.txt", 4242);
        tree.MakeFile("hidden.txt", 9);
        File.SetAttributes(tree.Full("hidden.txt"), FileAttributes.Hidden);

        var expected = new Dictionary<string, (uint Attributes, long Size)>(StringComparer.Ordinal);
        foreach (var info in new DirectoryInfo(tree.Base).EnumerateFileSystemInfos())
            expected[info.Name] = ((uint)info.Attributes & AttributeMask, info is FileInfo file ? file.Length : 0);

        foreach (var text in EnumeratorSpecTexts)
        {
            var sink = new CollectingSink();
            var result = EnumeratorSpec.Parse(text).CreateFactory().Create().Read(tree.Base, sink);
            AssertEqual(ReadOutcome.Complete, result.Outcome, $"{text}: outcome (error {result.Error})");
            AssertEqual(expected.Count, sink.Entries.Count, $"{text}: number of entries");
            foreach (var (name, want) in expected)
            {
                Assert(sink.Entries.TryGetValue(name, out var attributes), $"{text}: entry missing: {name}");
                AssertEqual(want.Attributes, attributes & AttributeMask, $"{text}: attributes of {name}");
                AssertEqual(want.Size, sink.Sizes[name], $"{text}: size of {name}");
            }
        }
    }

    static void EnumeratorsClassifyDirectories()
    {
        using var tree = new TempTree();
        tree.MakeDir("empty");
        foreach (var text in EnumeratorSpecTexts)
        {
            var reader = EnumeratorSpec.Parse(text).CreateFactory().Create();
            var missing = reader.Read(tree.Full("does-not-exist"), new CollectingSink());
            AssertEqual(ReadOutcome.Failed, missing.Outcome, $"{text}: a directory that does not exist (error {missing.Error})");
            var sink = new CollectingSink();
            var empty = reader.Read(tree.Full("empty"), sink);
            AssertEqual(ReadOutcome.Complete, empty.Outcome, $"{text}: an empty directory (error {empty.Error})");
            AssertEqual(0, sink.Entries.Count, $"{text}: an empty directory has no entries");
        }
    }

    static void EnumeratorsReportDenied()
    {
        using var tree = new TempTree();
        tree.MakeFile("locked\\g.bin", 222);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new SkipException("cannot determine the current user");
        var locked = tree.Root + "\\locked";
        if (RunProgram("icacls.exe", $"\"{locked}\" /deny \"*{sid}:(RD)\"") != 0) throw new SkipException("cannot set a deny ACL here");
        tree.OnDispose(() => RunProgram("icacls.exe", $"\"{locked}\" /remove:d \"*{sid}\""));
        // The precondition: the ACL must really keep this process out (a restricted or sandboxed token can ignore it).
        try
        {
            Directory.GetFileSystemEntries(locked);
            throw new SkipException("the deny ACL has no effect for this process (restricted token?)");
        }
        catch (UnauthorizedAccessException)
        {
        }
        foreach (var text in EnumeratorSpecTexts)
        {
            var result = EnumeratorSpec.Parse(text).CreateFactory().Create().Read(tree.Full("locked"), new CollectingSink());
            AssertEqual(ReadOutcome.Denied, result.Outcome, $"{text}: a directory that may not be read (error {result.Error})");
        }
    }

    static void EnumeratorsShowJunctions()
    {
        using var tree = new TempTree();
        using var outside = new TempTree();
        outside.MakeFile("target.bin", 1);
        tree.MakeFile("inside.bin", 1);
        if (RunProgram("cmd.exe", $"/c mklink /J \"{tree.Root}\\link\" \"{outside.Root}\"") != 0) throw new SkipException("cannot create a junction here");
        tree.OnDispose(() => Directory.Delete(tree.Full("link")));
        foreach (var text in EnumeratorSpecTexts)
        {
            var sink = new CollectingSink();
            EnumeratorSpec.Parse(text).CreateFactory().Create().Read(tree.Base, sink);
            Assert(sink.Entries.TryGetValue("link", out var attributes), $"{text}: the junction is listed");
            Assert((attributes & 0x400) != 0 && (attributes & 0x10) != 0, $"{text}: the junction entry has the directory and the reparse attribute (0x{attributes:X})");
        }
    }

    static void EnumeratorsWalkLikeTheOracle()
    {
        using var tree = StandardTree();
        var (expected, fileCount) = Oracle(tree.Base);
        foreach (var text in EnumeratorSpecTexts)
        {
            var spec = EnumeratorSpec.Parse(text);
            foreach (var workers in new[] { 1, 3, 8 })
            {
                var label = $"{text}, workers={workers}";
                var result = Scan(tree.Root, workers, files: true, top: 5, enumerators: spec.CreateFactory());
                AssertSameTotals(expected, TotalsOf(result.Nodes), label);
                AssertEqual(fileCount, result.Counters.Files, $"{label}: files");
                AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the root total equals the sum of the file sizes");
                AssertEqual("7011,5049,5048,5047,5046", string.Join(',', Sizes(result.Files)), $"{label}: largest files");
                AssertEqual(spec.Canonical, result.Metrics.Enumerator, $"{label}: the enumerator is reported");
            }
        }
    }
}
```

`src\DirSizer.Fs\FsOptions.cs` (replace the whole file):

```csharp
sealed class FsOptions
{
    public string Root { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Dirs { get; private set; } = true;
    public bool Json { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool Strict { get; private set; }
    public int Workers { get; private set; }    // 0 = automatic
    public EnumeratorSpec Enumerator { get; private set; } = EnumeratorSpec.Default;

    public static int DefaultWorkers => Math.Min(Environment.ProcessorCount, 8);

    public static FsOptions Parse(string[] args)
    {
        var result = new FsOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--self-test") { result.SelfTest = true; continue; }
            if (arg == "--benchmark") { result.Benchmark = true; continue; }
            if (arg == "--strict") { result.Strict = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--dirs") { result.Dirs = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("--top needs a whole number: --top=N or --top N.");
            if (arg.StartsWith("--workers=", StringComparison.Ordinal) && int.TryParse(arg[10..], out var workers)) { result.Workers = CheckWorkers(workers); continue; }
            if (arg == "--workers" && index + 1 < args.Length && int.TryParse(args[++index], out workers)) { result.Workers = CheckWorkers(workers); continue; }
            if (arg.StartsWith("--workers", StringComparison.Ordinal)) throw new ArgumentException("--workers needs a whole number from 1 to 256: --workers=N or --workers N.");
            if (arg.StartsWith("--enumerator=", StringComparison.Ordinal)) { result.Enumerator = EnumeratorSpec.Parse(arg[13..]); continue; }
            if (arg == "--enumerator" && index + 1 < args.Length) { result.Enumerator = EnumeratorSpec.Parse(args[++index]); continue; }
            if (arg.StartsWith("--enumerator", StringComparison.Ordinal)) throw new ArgumentException("--enumerator needs a value: --enumerator=NAME or --enumerator NAME.");
            if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Root.Length != 0) throw new ArgumentException("Only one path is supported.");
            result.Root = arg;
        }
        if (!result.Help && !result.SelfTest && result.Root.Length == 0) throw new ArgumentException("A directory path is required, for example C:\\.");
        return result;
    }

    static int CheckWorkers(int workers) =>
        workers is >= 1 and <= 256 ? workers : throw new ArgumentException("--workers must be between 1 and 256.");

    public static void PrintHelp()
    {
        Console.WriteLine($"""
            dirsizer - fast, read-only folder size scanner for any Windows filesystem

            Usage: dirsizer <path> [--top=N] [--files] [--dirs] [--json] [--workers N] [--strict]

            <path>      Directory to scan: a drive (C:\), a share (\\server\share), or any folder.
            --top=N     Show the largest N results (default: 25)
            --files     Include largest files
            --dirs      Include largest directories (default)
            --json      Write machine-readable JSON to stdout
            --workers N Directories are read by N threads in parallel (default: {DefaultWorkers}; 1-256)
            --strict    Exit with code 3 if a directory could not be read (result is still written)
            --enumerator=NAME[:CLASS[:KiB]]
                        Advanced, for comparisons: how directories are read. find (default),
                        find:nolarge, handle[:full|idextd[:KiB]], nt[:dir|full|idextd[:KiB]]
                        (idextd is not supported on exFAT)
            --benchmark Print phase timings and memory measurements to stderr
            --self-test Run the built-in tests (uses a temporary folder)
            -h          Show this help

            Reads directories with FindFirstFileExW; never opens individual files. Runs without
            elevation: directories you cannot read are skipped and counted. Sizes are logical
            (the file size the directory listing reports). A hard-linked file counts in every
            directory that holds a name for it. Reparse-point directories (junctions, symbolic
            links, mount points) are not entered. Alternate data streams are not included.

            Exit codes: 0 result written; 1 error; 3 with --strict, a directory could not be read.
            """);
    }
}
```

`src\DirSizer.Fs\FsOutput.cs` (replace the whole file):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

static class FsOutput
{
    public static void Write(FsResult result, FsOptions options, TextWriter output, TextWriter error)
    {
        if (options.Json)
        {
            // The default encoder writes ASCII only (non-ASCII characters become \uXXXX escapes), so the JSON is exact even when stdout
            // is redirected through a console code page that cannot represent every character in a path.
            output.WriteLine(JsonSerializer.Serialize(ToJson(result, options.Top), FsJsonContext.Default.JsonFsOutput));
        }
        else
        {
            output.WriteLine($"Showing up to {options.Top} largest directories by logical size");
            if (options.Files) output.WriteLine($"Showing up to {options.Top} largest files by logical size");
            output.WriteLine();
            output.WriteLine($"Directories (largest {options.Top})");
            output.WriteLine("Size\tPath");
            foreach (var item in result.Directories) output.WriteLine($"{item.Size,12:N0}\t{item.Path}");
            if (options.Files)
            {
                output.WriteLine();
                output.WriteLine($"Files (largest {options.Top})");
                output.WriteLine("Size\tPath");
                foreach (var item in result.Files) output.WriteLine($"{item.Size,12:N0}\t{item.Path}");
            }
            var c = result.Counters;
            output.WriteLine();
            output.WriteLine("Summary");
            output.WriteLine($"directories_scanned={c.DirectoriesScanned} directories_denied={c.DirectoriesDenied} directories_failed={c.DirectoriesFailed} reparse_skipped={c.ReparseSkipped} files={c.Files} bytes={c.Bytes}");
        }
        if (result.Counters.Unreadable > 0)
        {
            error.WriteLine($"warning: {result.Counters.Unreadable:N0} directories could not be read (denied {result.Counters.DirectoriesDenied:N0}, failed {result.Counters.DirectoriesFailed:N0}); the sizes are a lower bound.");
            foreach (var sample in result.ErrorSamples) error.WriteLine($"  failed: {sample}");
            var notListed = result.Counters.DirectoriesFailed - result.ErrorSamples.Length;
            if (notListed > 0) error.WriteLine($"  ... and {notListed:N0} more failed directories that are not listed");
        }
        if (options.Benchmark) error.WriteLine(BenchmarkLine(result.Metrics));
    }

    // Plain numbers (F, not N): no thousands separators, so the line can be split on "," and "=" whatever the size of the scan.
    static string BenchmarkLine(FsMetrics m) =>
        $"benchmark: workers={m.Workers}, enumerator={m.Enumerator}, large_fetch={(m.LargeFetch ? "on" : "off")}, open_ms={m.Open.TotalMilliseconds:F1}, walk_ms={m.Walk.TotalMilliseconds:F1}, " +
        $"aggregation_ms={m.Aggregation.TotalMilliseconds:F1}, finalize_ms={m.Finalize.TotalMilliseconds:F1}, other_ms={m.Other.TotalMilliseconds:F1}, " +
        $"total_ms={m.Total.TotalMilliseconds:F1}, phase_sum_ms={m.PhaseSum.TotalMilliseconds:F1}, enum_ms_total={m.EnumTotal.TotalMilliseconds:F1}, " +
        $"idle_ms_total={m.IdleTotal.TotalMilliseconds:F1}, peak_queued_dirs={m.PeakQueuedDirs}, entries_per_sec={m.EntriesPerSec:F0}, " +
        $"directories_per_sec={m.DirectoriesPerSec:F0}, logical_mib_per_sec={m.LogicalMibPerSec:F1}, managed_allocated={m.ManagedAllocatedBytes}, peak_working_set={m.PeakWorkingSetBytes}";

    public static JsonFsOutput ToJson(FsResult result, int top)
    {
        var c = result.Counters;
        var m = result.Metrics;
        return new JsonFsOutput(
            result.RootPath,
            top,
            "logical",
            new JsonFsItem(result.Root.Path, result.Root.Size),
            Items(result.RootChildren),
            Items(result.Directories),
            Items(result.Files),
            new JsonFsStatistics(
                c.DirectoriesScanned, c.DirectoriesDenied, c.DirectoriesFailed, c.ReparseSkipped, c.Directories, c.Files, c.Bytes,
                result.ErrorSamples,
                new JsonFsPerformance(
                    m.Open.TotalMilliseconds, m.Walk.TotalMilliseconds, m.Aggregation.TotalMilliseconds, m.Finalize.TotalMilliseconds,
                    m.Other.TotalMilliseconds, m.Total.TotalMilliseconds, m.PhaseSum.TotalMilliseconds,
                    m.EnumTotal.TotalMilliseconds, m.IdleTotal.TotalMilliseconds, m.Workers, m.LargeFetch, m.Enumerator, m.PeakQueuedDirs,
                    m.ManagedAllocatedBytes, m.PeakWorkingSetBytes, m.EntriesPerSec, m.DirectoriesPerSec, m.LogicalMibPerSec)),
            "win32-find");
    }

    static JsonFsItem[] Items(ResultItem[] items)
    {
        var result = new JsonFsItem[items.Length];
        for (var index = 0; index < items.Length; index++) result[index] = new JsonFsItem(items[index].Path, items[index].Size);
        return result;
    }
}

sealed record JsonFsOutput(string Volume, int Top, string SizeMode, JsonFsItem Root, JsonFsItem[] RootChildren, JsonFsItem[] Directories, JsonFsItem[] Files, JsonFsStatistics Statistics, string Reader);
sealed record JsonFsItem(string Path, long Size);
sealed record JsonFsStatistics(long DirectoriesScanned, long DirectoriesDenied, long DirectoriesFailed, long ReparseSkipped, long Directories, long Files, long Bytes, string[] ErrorSamples, JsonFsPerformance Performance);
sealed record JsonFsPerformance(double OpenMs, double WalkMs, double AggregationMs, double FinalizeMs, double OtherMs, double TotalMs, double PhaseSumMs, double EnumMsTotal, double IdleMsTotal, int Workers, bool LargeFetch, string Enumerator, int PeakQueuedDirs, long ManagedAllocatedBytes, long PeakWorkingSetBytes, double EntriesPerSec, double DirectoriesPerSec, double LogicalMibPerSec);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonFsOutput))]
partial class FsJsonContext : JsonSerializerContext;
```

`src\DirSizer.Fs\FsScanner.cs` (replace the whole file):

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

readonly record struct ResultItem(string Path, long Size);

sealed record FsCounters(long DirectoriesScanned, long DirectoriesDenied, long DirectoriesFailed, long ReparseSkipped, long Directories, long Files, long Bytes)
{
    public long Unreadable => DirectoriesDenied + DirectoriesFailed;
}

// Wall-clock phases (open, walk, aggregation, finalize, other) add up to Total exactly. EnumTotal and IdleTotal are summed over
// all workers while they run in parallel inside `walk`, so they are diagnostics and not phases.
sealed record FsMetrics(
    TimeSpan Open, TimeSpan Walk, TimeSpan Aggregation, TimeSpan Finalize, TimeSpan Total,
    TimeSpan EnumTotal, TimeSpan IdleTotal, int Workers, bool LargeFetch, string Enumerator, int PeakQueuedDirs,
    long Entries, long Directories, long Bytes, long ManagedAllocatedBytes, long PeakWorkingSetBytes)
{
    public TimeSpan Other => TimeSpan.FromTicks(Math.Max(0, Total.Ticks - Open.Ticks - Walk.Ticks - Aggregation.Ticks - Finalize.Ticks));
    public TimeSpan PhaseSum => Open + Walk + Aggregation + Finalize + Other;
    public double EntriesPerSec => Walk.TotalSeconds == 0 ? 0 : Entries / Walk.TotalSeconds;
    public double DirectoriesPerSec => Walk.TotalSeconds == 0 ? 0 : Directories / Walk.TotalSeconds;
    public double LogicalMibPerSec => Walk.TotalSeconds == 0 ? 0 : Bytes / 1048576.0 / Walk.TotalSeconds;
}

sealed record FsResult(
    string RootPath,
    ResultItem Root,
    ResultItem[] RootChildren,
    ResultItem[] Directories,
    ResultItem[] Files,
    FsCounters Counters,
    FsMetrics Metrics,
    string[] ErrorSamples,
    DirNode[] Nodes);

sealed record ScanSettings(int Workers, int Top, bool CollectFiles, bool ShowProgress, CancellationToken Cancel = default, IEnumeratorFactory? Enumerators = null);

readonly record struct RootChild(DirNode? Directory, FileHit File);

static class FsScanner
{
    // Throws ArgumentException (bad settings, bad or missing root), IOException (root cannot be read), OperationCanceledException.
    public static FsResult Scan(string rootArgument, ScanSettings settings)
    {
        // With no worker nothing would be read; the walk would end at once and the result would look like an empty tree.
        if (settings.Workers < 1) throw new ArgumentOutOfRangeException(nameof(settings), "The number of workers must be at least 1.");
        var total = Stopwatch.StartNew();
        var allocatedAtStart = GC.GetTotalAllocatedBytes();

        var rootPath = RootPath.Normalize(rootArgument);
        if (!Directory.Exists(rootPath.Extended)) throw new ArgumentException($"Not a directory, or not found: {rootPath.Display}");
        var open = total.Elapsed;

        var root = new DirNode(0, -1, rootPath.Display);
        Action<long, long>? progress = settings.ShowProgress
            ? (directories, files) => Console.Error.Write($"\rScanning: {directories:N0} directories, {files:N0} files")
            : null;
        WalkResult walk;
        try
        {
            walk = new Walker(settings.Enumerators).Run(root, rootPath.Extended, settings.Workers, settings.Top, settings.CollectFiles, settings.Cancel, progress);
        }
        finally
        {
            // Also when the walk failed or was canceled: the error message must not follow a half-written progress line.
            if (settings.ShowProgress) Console.Error.Write("\r" + new string(' ', 60) + "\r");
        }
        if (walk.RootRead.Outcome == ReadOutcome.NotRead) throw new InvalidOperationException("The root directory was never read (internal error).");
        if (walk.RootRead.Outcome != ReadOutcome.Complete)
        {
            var hint = walk.RootRead.Outcome == ReadOutcome.Denied ? " Choose a directory you can read, or start the tool from an elevated terminal." : "";
            throw new IOException($"Cannot read {rootPath.Display}: {Marshal.GetPInvokeErrorMessage(walk.RootRead.Error).TrimEnd()} (error {walk.RootRead.Error}).{hint}");
        }

        var mark = total.Elapsed;
        var nodes = walk.Nodes;
        DirTable.Aggregate(nodes);
        var aggregation = total.Elapsed - mark;

        mark = total.Elapsed;
        var top = settings.Top;
        var directoryTop = new BoundedTop<DirNode>(top);
        var rootChildTop = new BoundedTop<RootChild>(top);
        foreach (var node in nodes)
        {
            directoryTop.Add(node, node.Total);
            if (node.ParentId == 0) rootChildTop.Add(new RootChild(node, default), node.Total);
        }
        foreach (var hit in walk.RootFiles) rootChildTop.Add(new RootChild(null, hit), hit.Size);

        var directories = new List<ResultItem>();
        foreach (var node in directoryTop.ToDescendingArray()) directories.Add(new ResultItem(DirTable.PathOf(nodes, node.Id), node.Total));
        var rootChildren = new List<ResultItem>();
        foreach (var child in rootChildTop.ToDescendingArray())
        {
            rootChildren.Add(child.Directory is { } directory
                ? new ResultItem(DirTable.PathOf(nodes, directory.Id), directory.Total)
                : new ResultItem(DirTable.Combine(nodes[0].Name, child.File.Name), child.File.Size));
        }
        var files = new List<ResultItem>();
        foreach (var hit in walk.Files) files.Add(new ResultItem(DirTable.Combine(DirTable.PathOf(nodes, hit.DirId), hit.Name), hit.Size));
        var finalize = total.Elapsed - mark;

        var totals = walk.Totals;
        var counters = new FsCounters(totals.Scanned, totals.Denied, totals.Failed, totals.ReparseSkipped, nodes.Length, totals.Files, totals.Bytes);
        using var process = Process.GetCurrentProcess();
        var metrics = new FsMetrics(
            open, walk.WalkTime, aggregation, finalize, total.Elapsed,
            TimeSpan.FromSeconds((double)totals.EnumTicks / Stopwatch.Frequency),
            TimeSpan.FromSeconds((double)totals.IdleTicks / Stopwatch.Frequency),
            settings.Workers, walk.LargeFetch, walk.Enumerator, walk.PeakQueuedDirs,
            totals.Entries, nodes.Length, totals.Bytes,
            GC.GetTotalAllocatedBytes() - allocatedAtStart, process.PeakWorkingSet64);
        return new FsResult(rootPath.Display, new ResultItem(rootPath.Display, nodes[0].Total), rootChildren.ToArray(), directories.ToArray(), files.ToArray(), counters, metrics, walk.ErrorSamples, nodes);
    }
}
```

`src\DirSizer.Fs\Program.cs` (replace the whole file):

```csharp
FsOptions options;
try
{
    options = FsOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    FsOptions.PrintHelp();
    return 0;
}
if (options.SelfTest) return FsSelfTests.Run();

// Not disposed on purpose: the Ctrl+C handler can still run while the process ends, and cancelling a disposed source throws.
var cancel = new CancellationTokenSource();
var scanning = true;
Console.CancelKeyPress += (_, e) =>
{
    // Only the scan observes the token. Once it is over, Ctrl+C keeps its usual meaning, so that a long output can still be stopped.
    if (!Volatile.Read(ref scanning)) return;
    e.Cancel = true;
    cancel.Cancel();
};
try
{
    var settings = new ScanSettings(
        options.Workers == 0 ? FsOptions.DefaultWorkers : options.Workers,
        options.Top,
        options.Files,
        ShowProgress: !Console.IsErrorRedirected,
        cancel.Token,
        options.Enumerator.CreateFactory());
    var result = FsScanner.Scan(options.Root, settings);
    Volatile.Write(ref scanning, false);
    FsOutput.Write(result, options, Console.Out, Console.Error);
    return options.Strict && result.Counters.Unreadable > 0 ? 3 : 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("error: canceled");
    return 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
```

`src\DirSizer.Fs\SelfTests.Cli.cs` (replace the whole file):

```csharp
// Root path handling and command-line options.
static partial class FsSelfTests
{
    static partial void AddCliTests(List<SelfTest> tests)
    {
        tests.Add(new("root paths are normalized to display and extended forms", RootPathForms));
        tests.Add(new("options: defaults and values", OptionsAccepted));
        tests.Add(new("options: bad input is rejected", OptionsRejected));
        tests.Add(new("--enumerator: specs give their canonical form, bad ones are rejected", EnumeratorSpecs));
    }

    static void EnumeratorSpecs()
    {
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
        AssertEqual("nt:dir:4", FsOptions.Parse(["x", "--enumerator=nt:dir:4"]).Enumerator.Canonical, "--enumerator=SPEC");
        AssertEqual("handle:idextd:64", FsOptions.Parse(["x", "--enumerator", "handle:idextd"]).Enumerator.Canonical, "--enumerator SPEC");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["x", "--enumerator"]), "--enumerator without a value");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["x", "--enumerator=bogus"]), "--enumerator with a bad value");
    }

    static void RootPathForms()
    {
        AssertEqual(@"C:\", RootPath.Normalize("C:").Display, "drive letter alone is the drive root");
        AssertEqual(@"C:\", RootPath.Normalize(@"C:\").Display, "drive root keeps its backslash");
        AssertEqual(@"C:\Users", RootPath.Normalize(@"C:\Users\").Display, "trailing backslash is trimmed");
        AssertEqual(@"C:\a\c", RootPath.Normalize(@"C:\a\b\..\c").Display, ".. is resolved");
        AssertEqual(@"\\?\C:\Users", RootPath.Normalize(@"C:\Users").Extended, "extended form of a drive path");
        AssertEqual(@"\\?\C:\", RootPath.Normalize(@"C:\").Extended, "extended form of a drive root");
        AssertEqual(@"\\server\share", RootPath.Normalize(@"\\server\share\").Display, "share root display");
        AssertEqual(@"\\?\UNC\server\share", RootPath.Normalize(@"\\server\share").Extended, "share root extended form");
        AssertEqual(@"C:\x", RootPath.Normalize(@"\\?\C:\x").Display, "an extended-length argument is shown without the prefix");

        // An extended-length argument is reduced to the ordinary form first, because the API takes "\\?\" paths literally.
        AssertEqual(@"C:\b", RootPath.Normalize(@"\\?\C:\a\..\b").Display, ".. in an extended-length argument is resolved");
        AssertEqual(@"\\?\C:\b", RootPath.Normalize(@"\\?\C:\a\..\b").Extended, "and the extended form is rebuilt from the result");
        AssertEqual(@"C:\", RootPath.Normalize(@"\\?\C:\\").Display, "an extended drive root keeps its backslash");
        AssertEqual(@"\\server\share\d", RootPath.Normalize(@"\\?\UNC\server\share\d\").Display, "an extended UNC argument");
        AssertEqual(@"C:\x\y", RootPath.Normalize("C:/x//y").Display, "forward and doubled separators");
        AssertEqual(Path.GetFullPath("src"), RootPath.Normalize("src").Display, "a relative path is resolved against the current directory");

        // A volume with no drive letter is named by its GUID; that form is kept as typed.
        const string volume = @"\\?\Volume{12345678-1234-1234-1234-123456789abc}";
        AssertEqual(volume + @"\", RootPath.Normalize(volume).Display, "a volume root gets its trailing backslash");
        AssertEqual(volume + @"\", RootPath.Normalize(volume + @"\").Extended, "and is its own extended form");
        AssertEqual(volume + @"\dir", RootPath.Normalize(volume + @"\dir\").Display, "a folder on a volume");

        // The extended form is what makes paths over 260 characters work.
        var longName = new string('x', 300);
        var longRoot = RootPath.Normalize(@"C:\" + longName);
        AssertEqual(@"\\?\C:\" + longName, longRoot.Extended, "a 300-character name keeps the extended prefix and its full length");

        AssertThrows<ArgumentException>(() => RootPath.Normalize(""), "empty path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize("  "), "blank path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\.\C:\"), "a device path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\?\GLOBALROOT\Device\x"), "an extended-length device path");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\server"), "a network path without a share");
        AssertThrows<ArgumentException>(() => RootPath.Normalize(@"\\?\Volume{no-closing-brace"), "a broken volume path");
        var quote = MessageOfArgumentException(() => RootPath.Normalize("C:\\dir\""));
        Assert(quote.Contains("quote"), "a quote in the path is explained (a trailing backslash before a closing quote escapes it)");
    }

    // The message of the ArgumentException that the action must throw.
    static string MessageOfArgumentException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
        throw new Exception("expected ArgumentException, nothing was thrown");
    }

    static void OptionsAccepted()
    {
        var defaults = FsOptions.Parse([@"C:\"]);
        AssertEqual(@"C:\", defaults.Root, "path");
        AssertEqual(25, defaults.Top, "default top");
        AssertEqual(0, defaults.Workers, "default workers (automatic)");
        Assert(defaults.Dirs && !defaults.Files && !defaults.Json && !defaults.Strict && !defaults.Benchmark, "default flags");

        var all = FsOptions.Parse([@"D:\data", "--top=7", "--files", "--json", "--strict", "--benchmark", "--workers", "3"]);
        AssertEqual(7, all.Top, "--top=N");
        AssertEqual(3, all.Workers, "--workers N");
        Assert(all.Files && all.Json && all.Strict && all.Benchmark, "flags");

        AssertEqual(5, FsOptions.Parse(["--top", "5", "x"]).Top, "--top N");
        AssertEqual(4, FsOptions.Parse(["x", "--workers=4"]).Workers, "--workers=N");
        Assert(FsOptions.Parse(["--help"]).Help, "help needs no path");
        Assert(FsOptions.Parse(["--self-test"]).SelfTest, "self-test needs no path");
        Assert(FsOptions.Parse(["-h"]).Help, "-h");
        AssertEqual(1, FsOptions.Parse(["x", "--top", "0"]).Top, "--top 0 is clamped to 1, as in the NTFS tools");
        AssertEqual(256, FsOptions.Parse(["x", "--workers=256"]).Workers, "the largest number of workers");
    }

    static void OptionsRejected()
    {
        AssertThrows<ArgumentException>(() => FsOptions.Parse([]), "no path");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "b"]), "two paths");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--bogus"]), "unknown option");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers", "0"]), "zero workers");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers=999"]), "too many workers");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers"]), "workers without a value");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--top"]), "top without a value");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--top=abc"]), "top that is not a number");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--top", "--files"]), "top followed by another option");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers=abc"]), "workers that is not a number");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers=257"]), "one worker too many");
        AssertThrows<ArgumentException>(() => FsOptions.Parse(["a", "--workers", "-1"]), "a negative number of workers");
    }
}
```

`src\DirSizer.Fs\SelfTests.Output.cs` (replace the whole file):

```csharp
using System.Text.Json;

// Text and JSON output of a real scan.
static partial class FsSelfTests
{
    static partial void AddOutputTests(List<SelfTest> tests)
    {
        tests.Add(new("text output has the tables and the summary", TextOutput));
        tests.Add(new("JSON output has the documented fields", JsonOutput));
        tests.Add(new("unreadable directories give a warning, the counters and the error samples", UnreadableDirectoriesAreReported));
    }

    // A result made by hand, so the reporting of denied and failed directories is tested without a file system.
    static FsResult SyntheticResult(long denied, long failed, string[] samples)
    {
        var zero = TimeSpan.Zero;
        var counters = new FsCounters(5, denied, failed, 1, 8, 20, 1234);
        var metrics = new FsMetrics(zero, TimeSpan.FromSeconds(1), zero, zero, TimeSpan.FromSeconds(1), zero, zero, 4, true, "find", 0, 30, 8, 1234, 0, 0);
        return new FsResult(@"C:\r", new ResultItem(@"C:\r", 1234), [], [new ResultItem(@"C:\r", 1234)], [], counters, metrics, samples, [new DirNode(0, -1, @"C:\r")]);
    }

    static void UnreadableDirectoriesAreReported()
    {
        var sample = @"C:\r\x: The network path was not found. (error 53)";
        var text = new StringWriter();
        var error = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 2, failed: 1, [sample]), FsOptions.Parse([@"C:\r"]), text, error);
        Assert(error.ToString().Contains("warning: 3 directories could not be read (denied 2, failed 1); the sizes are a lower bound."), "the warning gives the counts");
        Assert(error.ToString().Contains("  failed: " + sample), "the error sample is listed");
        Assert(text.ToString().Contains("directories_denied=2 directories_failed=1"), "the summary shows both counters");

        var json = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 2, failed: 1, [sample]), FsOptions.Parse([@"C:\r", "--json"]), json, new StringWriter());
        using var document = JsonDocument.Parse(json.ToString());
        var statistics = document.RootElement.GetProperty("statistics");
        AssertEqual(2L, statistics.GetProperty("directories_denied").GetInt64(), "json denied");
        AssertEqual(1L, statistics.GetProperty("directories_failed").GetInt64(), "json failed");
        AssertEqual(sample, statistics.GetProperty("error_samples")[0].GetString(), "json error sample");

        var quietError = new StringWriter();
        FsOutput.Write(SyntheticResult(0, 0, []), FsOptions.Parse([@"C:\r"]), new StringWriter(), quietError);
        AssertEqual("", quietError.ToString(), "nothing on stderr when every directory was read");
        Assert(!error.ToString().Contains("more failed"), "no 'more failed' line when every failed directory has a sample");

        // More failed directories than samples: the reader is told that the list is cut.
        var cutError = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 0, failed: 25, [sample]), FsOptions.Parse([@"C:\r"]), new StringWriter(), cutError);
        Assert(cutError.ToString().Contains("... and 24 more failed directories that are not listed"), "the sample list says that it is cut");

        // JSON mode with --benchmark: stdout stays exactly one JSON document; the warning and the benchmark line go to stderr.
        var jsonOut = new StringWriter();
        var jsonErr = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 2, failed: 1, [sample]), FsOptions.Parse([@"C:\r", "--json", "--benchmark"]), jsonOut, jsonErr);
        using (JsonDocument.Parse(jsonOut.ToString())) { }   // throws unless stdout holds one JSON document and nothing else
        Assert(jsonErr.ToString().Contains("warning:") && jsonErr.ToString().Contains("benchmark: workers=4"), "the warning and the benchmark line are on stderr");
        Assert(!jsonOut.ToString().Contains("warning:") && !jsonOut.ToString().Contains("benchmark:"), "and not in the JSON");

        // The benchmark line can be split on "," and "=" whatever the size of the numbers (no thousands separators).
        var big = SyntheticResult(0, 0, []);
        big = big with { Metrics = big.Metrics with { Walk = TimeSpan.FromMilliseconds(19973.4), Total = TimeSpan.FromMilliseconds(20000), Entries = 2_500_000 } };
        var bigError = new StringWriter();
        FsOutput.Write(big, FsOptions.Parse([@"C:\r", "--benchmark"]), new StringWriter(), bigError);
        var line = bigError.ToString().Trim();
        Assert(line.Contains("walk_ms=19973.4"), "a walk of 19973.4 ms is written without a thousands separator");
        AssertEqual(line.Split(',').Length, line.Split('=').Length - 1, "every field between commas has exactly one '=': no comma inside a value");

        // Non-ASCII and volume paths survive a round trip through JSON, and the JSON itself is ASCII only.
        var volumePath = "\\\\?\\Volume{12345678-1234-1234-1234-123456789abc}\\\u65e5\u672c\u8a9e & 'x'";
        var unicode = SyntheticResult(0, 0, []) with { Directories = [new ResultItem(volumePath, 1)] };
        var unicodeOut = new StringWriter();
        FsOutput.Write(unicode, FsOptions.Parse([@"C:\r", "--json"]), unicodeOut, new StringWriter());
        using var unicodeDocument = JsonDocument.Parse(unicodeOut.ToString());
        AssertEqual(volumePath, unicodeDocument.RootElement.GetProperty("directories")[0].GetProperty("path").GetString(), "the path comes back unchanged");
        foreach (var character in unicodeOut.ToString()) Assert(character < 128, "the JSON text is ASCII only (non-ASCII characters are escaped)");
    }

    static void TextOutput()
    {
        using var tree = StandardTree();
        var options = FsOptions.Parse([tree.Root, "--files", "--top=3", "--benchmark"]);
        var result = Scan(tree.Root, 2, files: true, top: 3);
        var text = new StringWriter();
        var error = new StringWriter();
        FsOutput.Write(result, options, text, error);
        var output = text.ToString();
        Assert(output.Contains("Directories (largest 3)"), "directory table");
        Assert(output.Contains("Files (largest 3)"), "file table");
        Assert(output.Contains($"bytes={result.Root.Size}"), "summary shows the byte total");
        Assert(output.Contains("directories_denied=0 directories_failed=0"), "summary shows the error counters");
        Assert(output.Contains("7,011"), "sizes are grouped");
        Assert(error.ToString().Contains("benchmark: workers=2, enumerator=find, large_fetch=on"), "--benchmark prints the enumerator and the timings to stderr");
        Assert(!error.ToString().Contains("warning"), "no warning when every directory was read");

        var plain = new StringWriter();
        FsOutput.Write(Scan(tree.Root, 2, top: 3), FsOptions.Parse([tree.Root, "--top=3"]), plain, new StringWriter());
        Assert(plain.ToString().Contains("Directories (largest 3)"), "the directory table is always shown");
        Assert(!plain.ToString().Contains("Files (largest"), "the file table only with --files");
    }

    static void JsonOutput()
    {
        using var tree = StandardTree();
        var options = FsOptions.Parse([tree.Root, "--json", "--top=3"]);
        var result = Scan(tree.Root, 2, top: 3);
        var text = new StringWriter();
        FsOutput.Write(result, options, text, new StringWriter());
        using var document = JsonDocument.Parse(text.ToString());
        var root = document.RootElement;
        AssertEqual("win32-find", root.GetProperty("reader").GetString(), "reader");
        AssertEqual("logical", root.GetProperty("size_mode").GetString(), "size_mode");
        AssertEqual(3, root.GetProperty("top").GetInt32(), "top");
        AssertEqual(result.Root.Size, root.GetProperty("root").GetProperty("size").GetInt64(), "root size is a number");
        AssertEqual(tree.Root, root.GetProperty("volume").GetString(), "volume is the root path");
        AssertEqual(3, root.GetProperty("directories").GetArrayLength(), "directories limited to top");
        AssertEqual(3, root.GetProperty("root_children").GetArrayLength(), "root_children limited to top");
        AssertEqual(0, root.GetProperty("files").GetArrayLength(), "files are empty without --files");
        var statistics = root.GetProperty("statistics");
        AssertEqual(result.Counters.Directories, statistics.GetProperty("directories").GetInt64(), "statistics.directories");
        AssertEqual(result.Root.Size, statistics.GetProperty("bytes").GetInt64(), "statistics.bytes equals root.size");
        foreach (var name in new[] { "directories_scanned", "directories_denied", "directories_failed", "reparse_skipped", "files", "error_samples" })
            Assert(statistics.TryGetProperty(name, out _), $"statistics.{name} is present");
        var performance = statistics.GetProperty("performance");
        foreach (var name in new[] { "open_ms", "walk_ms", "aggregation_ms", "finalize_ms", "other_ms", "total_ms", "phase_sum_ms", "enum_ms_total", "idle_ms_total", "workers", "large_fetch", "enumerator", "peak_queued_dirs", "managed_allocated_bytes", "peak_working_set_bytes", "entries_per_sec", "directories_per_sec", "logical_mib_per_sec" })
            Assert(performance.TryGetProperty(name, out _), $"performance.{name} is present");
        AssertEqual(2, performance.GetProperty("workers").GetInt32(), "performance.workers");
        Assert(performance.GetProperty("large_fetch").ValueKind is JsonValueKind.True or JsonValueKind.False, "performance.large_fetch is a JSON boolean");
        Assert(performance.GetProperty("total_ms").ValueKind == JsonValueKind.Number, "timings are JSON numbers");

        // root_children: contents and order (wide 251225, long 7011, deep 4007 for the standard tree).
        var children = root.GetProperty("root_children");
        AssertEqual("251225,7011,4007", string.Join(',', new[] { children[0].GetProperty("size").GetInt64(), children[1].GetProperty("size").GetInt64(), children[2].GetProperty("size").GetInt64() }), "root_children sizes, largest first");
        Assert(children[0].GetProperty("path").GetString()!.EndsWith("\\wide"), "the largest root child is the wide directory");

        // --files with --json: a non-empty, descending files array.
        var withFiles = new StringWriter();
        FsOutput.Write(Scan(tree.Root, 2, files: true, top: 3), FsOptions.Parse([tree.Root, "--json", "--files", "--top=3"]), withFiles, new StringWriter());
        using var filesDocument = JsonDocument.Parse(withFiles.ToString());
        var fileItems = filesDocument.RootElement.GetProperty("files");
        AssertEqual(3, fileItems.GetArrayLength(), "--files: three files");
        AssertEqual("7011,5049,5048", string.Join(',', new[] { fileItems[0].GetProperty("size").GetInt64(), fileItems[1].GetProperty("size").GetInt64(), fileItems[2].GetProperty("size").GetInt64() }), "--files: largest first");
    }
}
```

`src\DirSizer.Fs\SelfTests.Walk.cs` (replace the whole file):

```csharp
using System.Security.Principal;
using System.Text;

// The whole walk on real temporary folders, checked against an independent computation.
static partial class FsSelfTests
{
    const int WideFanOut = 10_000;
    const int DeepChain = 800;

    static partial void AddWalkTests(List<SelfTest> tests)
    {
        tests.Add(new("walk matches the independent oracle for 1, 3 and 8 workers", WalkMatchesOracle));
        tests.Add(new("walk does not enter a junction, but enters one named as the root", WalkJunction));
        tests.Add(new("walk counts a hard link in every directory that holds a name", WalkHardLink));
        tests.Add(new("walk skips a directory it may not read and counts it", WalkDenied));
        tests.Add(new("walk finishes on a very deep chain (1 and 8 workers)", WalkDeepChain));
        tests.Add(new("walk finishes on a very wide fan-out and reports the queue peak", WalkWideFanOut));
        tests.Add(new("walk with LARGE_FETCH rejected: one worker fails once, eight at most eight times", WalkLargeFetchRejected));
        tests.Add(new("walk counts a directory that fails and keeps the rest", WalkFailedDirectory));
        tests.Add(new("walk reports a bad root or bad settings as an error", WalkBadRoot));
        tests.Add(new("walk stops when canceled, before and while it runs", WalkCanceled));
        tests.Add(new("walk stops and rethrows when a worker throws", WalkWorkerThrows));
        tests.Add(new("walk leaves no worker running when the caller fails", WalkStopsWorkersWhenTheCallerFails));
    }

    static FsResult Scan(string root, int workers, bool files = false, int top = 25, CancellationToken cancel = default, FindFirstFn? findFirst = null, IEnumeratorFactory? enumerators = null) =>
        FsScanner.Scan(root, new ScanSettings(workers, top, files, false, cancel, findFirst is null ? enumerators : new FindFirstFactory(true, findFirst)));

    // Nested and empty directories, a zero-byte file, Unicode names, a path over 260 characters, and files of distinct sizes.
    static TempTree StandardTree()
    {
        var tree = new TempTree();
        tree.MakeFile("a.bin", 1001);
        tree.MakeDir("empty");
        tree.MakeFile("zero.txt", 0);
        tree.MakeFile("Ünï\\日本語.txt", 2003);
        tree.MakeFile("deep\\l1\\l2\\l3\\f.bin", 4007);
        for (var index = 0; index < 50; index++) tree.MakeFile($"wide\\d{index:000}\\f.bin", 5000 + index);
        var segment = new string('x', 60);
        var nested = new StringBuilder(segment);
        for (var index = 0; index < 5; index++) nested.Append('\\').Append(segment);
        tree.MakeFile($"long\\{nested}\\deep.bin", 7011);
        return tree;
    }

    // The independent computation: the framework's own directory enumeration, no code from the tool. Keys are paths below the root.
    static (Dictionary<string, long> Totals, long Files) Oracle(string extendedRoot)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long files = 0;
        long Walk(DirectoryInfo directory, string relative)
        {
            long sum = 0;
            foreach (var file in directory.EnumerateFiles())
            {
                sum += file.Length;
                files++;
            }
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                sum += Walk(child, relative.Length == 0 ? child.Name : relative + "\\" + child.Name);
            }
            totals[relative] = sum;
            return sum;
        }
        Walk(new DirectoryInfo(extendedRoot), "");
        return (totals, files);
    }

    static Dictionary<string, long> TotalsOf(DirNode[] nodes)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var node in nodes) totals[DirTable.RelativePath(nodes, node.Id)] = node.Total;
        return totals;
    }

    static void AssertSameTotals(Dictionary<string, long> expected, Dictionary<string, long> actual, string label)
    {
        AssertEqual(expected.Count, actual.Count, $"{label}: number of directories");
        foreach (var (path, total) in expected)
        {
            Assert(actual.TryGetValue(path, out var found), $"{label}: directory missing: '{path}'");
            AssertEqual(total, found, $"{label}: total of '{path}'");
        }
    }

    static long[] Sizes(ResultItem[] items)
    {
        var sizes = new long[items.Length];
        for (var index = 0; index < items.Length; index++) sizes[index] = items[index].Size;
        return sizes;
    }

    static void WalkMatchesOracle()
    {
        using var tree = StandardTree();
        var (expected, fileCount) = Oracle(tree.Base);
        foreach (var workers in new[] { 1, 3, 8 })
        {
            var label = $"workers={workers}";
            var result = Scan(tree.Root, workers, files: true, top: 5);
            AssertSameTotals(expected, TotalsOf(result.Nodes), label);
            AssertEqual(fileCount, result.Counters.Files, $"{label}: files");
            AssertEqual((long)expected.Count, result.Counters.Directories, $"{label}: directories");
            AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the root total equals the sum of every file size");
            AssertEqual(result.Counters.Directories, result.Counters.DirectoriesScanned, $"{label}: every directory was read");
            AssertEqual(0L, result.Counters.Unreadable + result.Counters.ReparseSkipped, $"{label}: nothing skipped");
            AssertEqual(tree.Root, result.Root.Path, $"{label}: root path");
            AssertEqual(result.Metrics.Total, result.Metrics.PhaseSum, $"{label}: phases add up to the total");
            Assert(result.Metrics.Other >= TimeSpan.Zero && result.Metrics.Walk <= result.Metrics.Total, $"{label}: the residual is not negative and the walk fits in the total");

            // Distinct file sizes, so the order is fully defined.
            AssertEqual("7011,5049,5048,5047,5046", string.Join(',', Sizes(result.Files)), $"{label}: largest files");
            AssertEqual(result.Root.Size, result.Directories[0].Size, $"{label}: the root is the largest directory");
            AssertEqual(tree.Root, result.Directories[0].Path, $"{label}: and is listed first");
            // root children: wide (50 files 5000..5049), long, deep, the Unicode directory, then a.bin; the empty directory and zero.txt are below the cut.
            AssertEqual("251225,7011,4007,2003,1001", string.Join(',', Sizes(result.RootChildren)), $"{label}: root children");
            Assert(result.RootChildren[4].Path.EndsWith("\\a.bin"), $"{label}: a root-level file appears among the root's children");
        }

        // The boundary of the bounded selections: one result each.
        var single = Scan(tree.Root, 2, files: true, top: 1);
        AssertEqual("7011", string.Join(',', Sizes(single.Files)), "top=1: the largest file");
        AssertEqual(1, single.Directories.Length, "top=1: one directory");
        AssertEqual(tree.Root, single.Directories[0].Path, "top=1: the root");
        AssertEqual("251225", string.Join(',', Sizes(single.RootChildren)), "top=1: the largest root child");
    }

    static void WalkJunction()
    {
        using var tree = new TempTree();
        using var outside = new TempTree();
        outside.MakeFile("target.bin", 9013);
        tree.MakeFile("inside.bin", 100);
        var link = tree.Root + "\\link";
        if (RunProgram("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Root}\"") != 0) throw new SkipException("cannot create a junction here");
        tree.OnDispose(() => Directory.Delete(tree.Full("link")));

        var scan = Scan(tree.Root, 4);
        AssertEqual(100L, scan.Root.Size, "the junction's target is not counted");
        AssertEqual(1L, scan.Counters.ReparseSkipped, "the junction is counted as skipped");
        AssertEqual(1L, scan.Counters.Directories, "the junction is not a directory node");

        var viaRoot = Scan(link, 4);
        AssertEqual(9013L, viaRoot.Root.Size, "a junction named as the root is entered");
        AssertEqual(0L, viaRoot.Counters.ReparseSkipped, "nothing is skipped below it");
    }

    static void WalkHardLink()
    {
        using var tree = new TempTree();
        tree.MakeFile("a\\f.bin", 3000);
        tree.MakeDir("b");
        if (RunProgram("cmd.exe", $"/c mklink /H \"{tree.Root}\\b\\g.bin\" \"{tree.Root}\\a\\f.bin\"") != 0) throw new SkipException("cannot create a hard link here (not NTFS?)");
        var result = Scan(tree.Root, 2);
        var totals = TotalsOf(result.Nodes);
        AssertEqual(3000L, totals["a"], "directory a");
        AssertEqual(3000L, totals["b"], "directory b: the other name of the same file counts too");
        AssertEqual(6000L, totals[""], "the root counts the file once per name");
    }

    static void WalkDenied()
    {
        using var tree = new TempTree();
        tree.MakeFile("open\\f.bin", 111);
        tree.MakeFile("locked\\g.bin", 222);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new SkipException("cannot determine the current user");
        var locked = tree.Root + "\\locked";
        if (RunProgram("icacls.exe", $"\"{locked}\" /deny \"*{sid}:(RD)\"") != 0) throw new SkipException("cannot set a deny ACL here");
        tree.OnDispose(() => RunProgram("icacls.exe", $"\"{locked}\" /remove:d \"*{sid}\""));
        // The precondition of the whole test: the ACL must really keep this process out. Some environments (a restricted or sandboxed
        // token) ignore the deny entry, and then there is nothing to test.
        try
        {
            Directory.GetFileSystemEntries(locked);
            throw new SkipException("the deny ACL has no effect for this process (restricted token?)");
        }
        catch (UnauthorizedAccessException)
        {
        }

        var result = Scan(tree.Root, 4);
        var counters = result.Counters;
        AssertEqual(1L, counters.DirectoriesDenied, $"the locked directory is counted (scanned {counters.DirectoriesScanned}, failed {counters.DirectoriesFailed}; {string.Join(" | ", result.ErrorSamples)})");
        AssertEqual(0L, result.Counters.DirectoriesFailed, "and is not a failure");
        AssertEqual(111L, result.Root.Size, "the readable part of the tree is complete; the locked file is not counted");
        AssertEqual(3L, result.Counters.Directories, "the locked directory is still a node");

        var rootError = ThrownIOException(() => Scan(locked, 2));
        Assert(rootError.Message.Contains("Cannot read"), "an unreadable root is an error, not a result");
    }

    static IOException ThrownIOException(Action action)
    {
        try
        {
            action();
        }
        catch (IOException exception)
        {
            return exception;
        }
        throw new Exception("expected IOException, nothing was thrown");
    }

    static void WalkDeepChain()
    {
        using var tree = new TempTree();
        var chain = new StringBuilder("d");
        for (var index = 1; index < DeepChain; index++) chain.Append("\\d");
        tree.MakeFile(chain + "\\bottom.bin", 12345);
        foreach (var workers in new[] { 1, 8 })
        {
            var result = Scan(tree.Root, workers);
            AssertEqual(12345L, result.Root.Size, $"workers={workers}: the file at the bottom reaches the root");
            AssertEqual((long)DeepChain + 1, result.Counters.Directories, $"workers={workers}: directories");
        }
    }

    static void WalkWideFanOut()
    {
        using var tree = new TempTree();
        tree.MakeFile("many\\f.bin", 7);
        for (var index = 0; index < WideFanOut; index++) tree.MakeDir($"many\\e{index:00000}");
        foreach (var workers in new[] { 1, 8 })
        {
            var result = Scan(tree.Root, workers);
            AssertEqual(7L, result.Root.Size, $"workers={workers}: total");
            AssertEqual((long)WideFanOut + 2, result.Counters.Directories, $"workers={workers}: directories");
            // One worker pushes all the sub-directories before any of them is taken: the stack really is unbounded.
            Assert(result.Metrics.PeakQueuedDirs >= WideFanOut, $"workers={workers}: peak queued directories was {result.Metrics.PeakQueuedDirs}, expected at least {WideFanOut}");
            Console.WriteLine($"      workers={workers}: peak_queued_dirs={result.Metrics.PeakQueuedDirs}, peak_working_set={result.Metrics.PeakWorkingSetBytes / 1048576} MiB");
        }
    }

    static void WalkLargeFetchRejected()
    {
        using var tree = StandardTree();
        var (expected, _) = Oracle(tree.Base);

        var one = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var single = Scan(tree.Root, 1, findFirst: one.Call);
        AssertSameTotals(expected, TotalsOf(single.Nodes), "workers=1");
        AssertEqual(1, CountTrue(one.Calls), "one worker: exactly one call with the flag, and it failed");
        AssertEqual(true, one.Calls[0], "the first call is the one with the flag");
        Assert(!single.Metrics.LargeFetch, "the flag is reported off");
        Assert(one.Calls.Count > 2, "the walk went on making calls");

        var many = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var parallel = Scan(tree.Root, 8, findFirst: many.Call);
        AssertSameTotals(expected, TotalsOf(parallel.Nodes), "workers=8");
        Assert(CountTrue(many.Calls) <= 8, $"eight workers: at most eight calls with the flag, got {CountTrue(many.Calls)}");
        Assert(!parallel.Metrics.LargeFetch, "the flag is reported off");
    }

    static int CountTrue(List<bool> values)
    {
        var count = 0;
        foreach (var value in values)
        {
            if (value) count++;
        }
        return count;
    }

    // Find-first fails with ERROR_SHARING_VIOLATION (32) for the directory "bad" and is the real call for every other directory.
    static void WalkFailedDirectory()
    {
        using var tree = new TempTree();
        tree.MakeFile("ok\\f.bin", 111);
        tree.MakeFile("bad\\g.bin", 222);
        tree.MakeFile("z.bin", 5);
        FindFirstResult FailBad(string pattern, ref Win32FindData data, bool largeFetch) =>
            pattern.Contains("\\bad\\") ? new FindFirstResult(Win32Find.InvalidHandle, 32) : Win32Find.FindFirst(pattern, ref data, largeFetch);
        foreach (var workers in new[] { 1, 4 })
        {
            var label = $"workers={workers}";
            var result = Scan(tree.Root, workers, findFirst: FailBad);
            AssertEqual(116L, result.Root.Size, $"{label}: everything except the failed directory is counted");
            AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the total still equals the sum of the file sizes");
            AssertEqual(1L, result.Counters.DirectoriesFailed, $"{label}: one failed directory");
            AssertEqual(0L, result.Counters.DirectoriesDenied, $"{label}: and it is not counted as denied");
            AssertEqual(2L, result.Counters.DirectoriesScanned, $"{label}: the root and ok were read");
            AssertEqual(3L, result.Counters.Directories, $"{label}: the failed directory is still a node");
            AssertEqual(1, result.ErrorSamples.Length, $"{label}: one error sample");
            Assert(result.ErrorSamples[0].Contains("\\bad") && result.ErrorSamples[0].Contains("error 32"), $"{label}: the sample names the directory and the error: {result.ErrorSamples[0]}");
        }
    }

    static void WalkBadRoot()
    {
        using var tree = new TempTree();
        tree.MakeFile("a.bin", 1);
        AssertThrows<ArgumentException>(() => Scan(tree.Root + "\\missing", 2), "missing directory");
        AssertThrows<ArgumentException>(() => Scan(tree.Root + "\\a.bin", 2), "a file is not a directory");
        // No worker would read anything and the result would look like an empty tree.
        AssertThrows<ArgumentException>(() => Scan(tree.Root, 0), "zero workers");
        AssertThrows<ArgumentException>(() => Scan(tree.Root, -1), "a negative number of workers");
        AssertEqual(ReadOutcome.NotRead, default(ReadResult).Outcome, "a read result that was never filled in is not a success");
    }

    static void WalkCanceled()
    {
        using var tree = StandardTree();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        AssertThrows<OperationCanceledException>(() => Scan(tree.Root, 4, cancel: canceled.Token), "canceled token");

        // Canceled while the walk is running: every find-first call is slow, so the workers are busy or waiting when the token fires.
        FindFirstResult Slow(string pattern, ref Win32FindData data, bool largeFetch)
        {
            Thread.Sleep(40);
            return Win32Find.FindFirst(pattern, ref data, largeFetch);
        }
        using var late = new CancellationTokenSource();
        late.CancelAfter(150);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        AssertThrows<OperationCanceledException>(() => Scan(tree.Root, 4, cancel: late.Token, findFirst: Slow), "canceled while running");
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert(elapsed < TimeSpan.FromSeconds(10), $"the walk stopped promptly after the cancel ({elapsed.TotalSeconds:N1} s)");
    }

    static void WalkWorkerThrows()
    {
        using var tree = StandardTree();
        FindFirstResult Boom(string pattern, ref Win32FindData data, bool largeFetch) => throw new InvalidOperationException("boom");
        try
        {
            Scan(tree.Root, 4, findFirst: Boom);
            throw new Exception("an exception in a worker must reach the caller, but nothing was thrown");
        }
        catch (InvalidOperationException exception)
        {
            AssertEqual("boom", exception.Message, "the worker's own exception reaches the caller (and the walk does not hang)");
        }
    }

    // If Walker.Run is left early (here: the progress callback throws) no worker may go on walking in the background.
    static void WalkStopsWorkersWhenTheCallerFails()
    {
        using var tree = StandardTree();
        var calls = 0;
        FindFirstResult Slow(string pattern, ref Win32FindData data, bool largeFetch)
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(100);
            return Win32Find.FindFirst(pattern, ref data, largeFetch);
        }
        var root = new DirNode(0, -1, tree.Root);
        try
        {
            new Walker(new FindFirstFactory(true, Slow)).Run(root, tree.Base, 2, 5, false, default, (directories, files) => throw new InvalidOperationException("progress failed"));
            throw new Exception("the failing progress callback must reach the caller, but nothing was thrown");
        }
        catch (InvalidOperationException exception)
        {
            AssertEqual("progress failed", exception.Message, "the callback's exception reaches the caller");
        }
        var atThrow = Volatile.Read(ref calls);
        Thread.Sleep(500);
        AssertEqual(atThrow, Volatile.Read(ref calls), "no worker is still walking after Run has thrown");
        Assert(atThrow < 65, $"the walk was cut short ({atThrow} of 65 directories were read)");
    }
}
```

`src\DirSizer.Fs\SelfTests.cs` (replace the whole file):

```csharp
using System.Diagnostics;
using System.Runtime.ExceptionServices;

// The built-in tests (dirsizer --self-test). They need no elevation and no volume: file-system tests build a fixture in a
// temporary folder and remove it afterwards. Each group of tests lives in its own file and registers itself through one of
// the partial methods below; a partial method with no body in the build is simply not called.

readonly record struct SelfTest(string Name, Action Body);

// A test that cannot run in this environment (for example no permission to create a junction) skips itself visibly.
sealed class SkipException(string reason) : Exception(reason);

static partial class FsSelfTests
{
    static partial void AddCliTests(List<SelfTest> tests);
    static partial void AddModelTests(List<SelfTest> tests);
    static partial void AddReaderTests(List<SelfTest> tests);
    static partial void AddEnumeratorTests(List<SelfTest> tests);
    static partial void AddWalkTests(List<SelfTest> tests);
    static partial void AddOutputTests(List<SelfTest> tests);

    // Returns the process exit code: 0 if no test failed.
    public static int Run()
    {
        var tests = new List<SelfTest>();
        AddCliTests(tests);
        AddModelTests(tests);
        AddReaderTests(tests);
        AddEnumeratorTests(tests);
        AddWalkTests(tests);
        AddOutputTests(tests);

        var failed = 0;
        var skipped = 0;
        foreach (var test in tests)
        {
            try
            {
                RunWithTimeout(test);
                Console.WriteLine($"ok    {test.Name}");
            }
            catch (SkipException skip)
            {
                skipped++;
                Console.WriteLine($"skip  {test.Name}: {skip.Message}");
            }
            catch (Exception exception)
            {
                failed++;
                // A failed assertion is a plain Exception; anything else is a surprise, so its type is worth seeing.
                var text = exception.GetType() == typeof(Exception) ? exception.Message : $"{exception.GetType().Name}: {exception.Message}";
                Console.WriteLine($"FAIL  {test.Name}: {text}");
            }
        }
        Console.WriteLine(failed == 0
            ? $"{tests.Count - skipped} self-tests passed, {skipped} skipped."
            : $"{failed} of {tests.Count} self-tests FAILED.");
        return failed == 0 ? 0 : 1;
    }

    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);

    // A test that hangs (a deadlock in the walk would) must fail, not stop the whole run. The body runs on its own background
    // thread; if it does not finish in time the test fails and the run goes on.
    static void RunWithTimeout(SelfTest test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test.Body();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true, Name = "self-test" };
        thread.Start();
        if (!thread.Join(TestTimeout)) throw new Exception($"timed out after {TestTimeout.TotalSeconds:N0} s (a hang or a deadlock)");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static void AssertThrows<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"{what}: expected {typeof(TException).Name}, nothing was thrown");
    }

    // Runs a program and returns its exit code, or -1 if it cannot be started.
    static int RunProgram(string fileName, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info);
            if (process is null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}

// A temporary directory tree. Root is the display form; every file-system operation of a fixture goes through Full(), which uses
// the extended-length form so that fixtures can have paths over 260 characters.
sealed class TempTree : IDisposable
{
    readonly List<Action> _cleanup = [];

    public string Root { get; }
    public string Base { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "dirsizer-fs-selftest-" + Guid.NewGuid().ToString("N"));
        Base = @"\\?\" + Root;
        Directory.CreateDirectory(Base);
    }

    public string Full(string relative) => relative.Length == 0 ? Base : Base + "\\" + relative;

    public void MakeDir(string relative) => Directory.CreateDirectory(Full(relative));

    // A file whose end-of-file is `size` (no data is written; the size is what the directory listing reports).
    public void MakeFile(string relative, long size)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(size);
    }

    // Runs when the tree is disposed, before it is deleted (last registered first). For undoing things that block deletion.
    public void OnDispose(Action cleanup) => _cleanup.Add(cleanup);

    public void Dispose()
    {
        for (var index = _cleanup.Count - 1; index >= 0; index--)
        {
            try
            {
                _cleanup[index]();
            }
            catch (Exception)
            {
                // best effort: a failed cleanup must not hide the test result
            }
        }
        try
        {
            Directory.Delete(Base, true);
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
```


- [ ] **Step 2: Build and run the tests**

```powershell
Normalize-FsProject   # helper from the ground rules, defined in this same command
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "enumerator|passed|FAIL|skip"
```

Expected: `0 Warning(s)`, `0 Error(s)`; among the `ok` lines `--enumerator: specs give their canonical form, bad ones are rejected` and the five `every enumerator ...` tests; the last line `34 self-tests passed, 0 skipped.` (in a sandboxed shell the deny-ACL test skips: `33 passed, 1 skipped`).

- [ ] **Step 3: NativeAOT canary (the first `ntdll.dll` import) and the default's snapshots**

```powershell
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) { $env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer" }
dotnet publish src\DirSizer.Fs\DirSizer.Fs.csproj -c Release -r win-x64 2>&1 | Select-Object -Last 3
$exe = (Resolve-Path .\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe).Path
& $exe --self-test | Select-Object -Last 1; "exit: $LASTEXITCODE"
foreach ($e in 'find', 'handle:full:64', 'nt:dir:64') {
    $j = & $exe 'C:\Windows\System32\drivers' --json --top=1 --workers 8 --enumerator=$e | ConvertFrom-Json
    "{0,-16} root={1:N0} dirs={2} files={3} enumerator={4}" -f $e, $j.root.size, $j.statistics.directories, $j.statistics.files, $j.statistics.performance.enumerator
}
foreach ($p in @(@('T:\', 'T'), @('C:\Program Files\dotnet', 'dotnet'), @('C:\Windows\System32\drivers', 'drivers'))) {
    .\scripts\Get-FsSnapshot.ps1 -Path $p[0] -Out "artifacts\snapshots\step3-$($p[1]).json" | Out-Null
    "{0,-28} baseline == default now: {1}" -f $p[0], ([IO.File]::ReadAllText((Resolve-Path "artifacts\snapshots\baseline-$($p[1]).json")) -ceq [IO.File]::ReadAllText((Resolve-Path "artifacts\snapshots\step3-$($p[1]).json")))
}
```

Expected: the publish succeeds (no ILC error about the `ntdll.dll` import), the native `--self-test` ends with `34 self-tests passed, 0 skipped.` and `exit: 0`; the three enumerators print the same root, directory and file counts (the prototype: 173,524,197 bytes, 10 directories, 602 files) and their own names; all three `baseline == default now: True` (adding B and C must not change what the default gives). If the native build fails only here, stop and report.

- [ ] **Step 4: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs
git commit -m @'
Add --enumerator with handle (GetFileInformationByHandleEx) and nt (NtQueryDirectoryFileEx) enumerators

One buffered, handle-based base class; the two differ only in the call that fills the buffer.
Conformance tests run every enumerator against the framework's own enumeration and the walk oracle.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 4: Comparison scripts, the synthetic workloads, equality on real volumes

**Files:**
- Create: `scripts\Compare-Enumerators.ps1`, `scripts\New-EnumFixture.ps1`

- [ ] **Step 1: Write the scripts**

`scripts\Compare-Enumerators.ps1`:

```powershell
<#
.SYNOPSIS
  Compares dirsizer's directory enumerators: whether they give identical results, and how long they take.

.DESCRIPTION
  -Equal  For every -Path, takes a canonical snapshot (Get-FsSnapshot.ps1) with each enumerator in -Specs and compares it with the
          snapshot of the first one (the reference, normally `find`). Identical means byte for byte: the root, every directory and
          its size, the counters. Use QUIESCENT trees. Exit code 0 only if every snapshot is identical.
  -Time   For every -Path, runs each enumerator -Rounds times, alternating them and rotating the order in each round, with a fixed
          number of workers (-Workers, default 8: the enumerator is the only variable). Prints the min / median / max of walk_ms
          (wall clock, the primary metric) and the median of enum_ms_total (worker time, a diagnostic, not the elapsed time) and of
          entries per second. Warm file cache; cold cache is not measured.

  -Specs are --enumerator values: find, find:nolarge, handle:idextd:64, handle:full:4, nt:dir:64 ...
  -Tool is a .dll (run with dotnet) or an .exe.

.EXAMPLE
  .\scripts\Compare-Enumerators.ps1 -Path T:\, D:\ -Equal -Specs find, handle:idextd:64, nt:dir:64
  .\scripts\Compare-Enumerators.ps1 -Path C:\ -Time -Rounds 5 -Specs find, handle:full:64, nt:dir:64
#>
param(
    [Parameter(Mandatory)][string[]]$Path,
    [string[]]$Specs = @('find', 'handle:idextd:64', 'nt:dir:64'),
    [switch]$Equal,
    [switch]$Time,
    [int]$Rounds = 5,
    [int]$Workers = 8,
    [switch]$Files,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll')
)
$ErrorActionPreference = 'Stop'
if (-not ($Equal -or $Time)) { throw 'Choose -Equal, -Time or both.' }
if ($Specs.Count -lt 1) { throw '-Specs needs at least one enumerator.' }

function Invoke-Scan([string]$Target, [string]$Spec) {
    $arguments = @($Target, '--json', '--top=1', '--workers', "$Workers", "--enumerator=$Spec")
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $text = if ($Tool -like '*.dll') { & dotnet $Tool @arguments 2>$errorFile } else { & $Tool @arguments 2>$errorFile }
        $code = $LASTEXITCODE
        if ($code -notin 0, 3) { throw "$Tool exited with code ${code} for '$Spec' on ${Target}: $((Get-Content -Raw $errorFile).Trim())" }
    } finally { [IO.File]::Delete($errorFile) }
    ($text -join "`n") | ConvertFrom-Json
}

$failures = 0

if ($Equal) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) "enumerator-snapshots-$([guid]::NewGuid().ToString('N'))"
    [void](New-Item -ItemType Directory $temp)
    try {
        "Snapshot equality (workers=$Workers): the first spec is the reference"
        foreach ($target in $Path) {
            "  $target"
            $reference = $null
            $index = 0
            foreach ($spec in $Specs) {
                $file = Join-Path $temp ("snapshot-{0}.json" -f $index++)
                $snapshotArguments = @{ Path = $target; Tool = $Tool; Enumerator = $spec; Workers = $Workers; Out = $file }
                if ($Files) { $snapshotArguments.Files = $true }
                # An enumerator that does not work on this file system (for example a class it does not support) is a result, not a
                # reason to stop: say what it reported and go on. It counts as a failure of the comparison.
                try { $summary = & "$PSScriptRoot\Get-FsSnapshot.ps1" @snapshotArguments }
                catch { "    FAILED     {0}: {1}" -f $spec, ($_.Exception.Message -replace '^.*exited with code \d+: ', ''); $failures++; continue }
                $text = [IO.File]::ReadAllText($file)
                if ($null -eq $reference) { $reference = $text; "    reference  {0,-20} {1}" -f $spec, ($summary -replace '^snapshot: .*?\s{2}', '') }
                elseif ($text -ceq $reference) { "    EQUAL      {0}" -f $spec }
                else {
                    "    DIFFERENT  {0}" -f $spec
                    $a = $reference -split "`n"; $b = $text -split "`n"
                    Compare-Object $a $b | Select-Object -First 6 | ForEach-Object { "               {0} {1}" -f $_.SideIndicator, $_.InputObject.Trim() }
                    $failures++
                }
            }
        }
    } finally { if (Test-Path $temp) { [IO.Directory]::Delete($temp, $true) } }
}

if ($Time) {
    foreach ($target in $Path) {
        "Timing on $target (workers=$Workers, $Rounds rounds, alternating, order rotated each round)"
        $walk = @{}; $enum = @{}; $rate = @{}
        foreach ($spec in $Specs) { $walk[$spec] = @(); $enum[$spec] = @(); $rate[$spec] = @() }
        $roots = @{}
        for ($round = 0; $round -lt $Rounds; $round++) {
            for ($i = 0; $i -lt $Specs.Count; $i++) {
                $spec = $Specs[($i + $round) % $Specs.Count]
                $json = Invoke-Scan $target $spec
                $performance = $json.statistics.performance
                $walk[$spec] += [double]$performance.walk_ms
                $enum[$spec] += [double]$performance.enum_ms_total
                $rate[$spec] += [double]$performance.entries_per_sec
                $roots[$spec] = $json.root.size
            }
        }
        function Get-Median($values) { $s = @($values | Sort-Object); $s[[int][Math]::Floor(($s.Count - 1) / 2)] }
        foreach ($spec in $Specs) {
            $sorted = @($walk[$spec] | Sort-Object)
            "  {0,-20} walk_ms min={1,9:N1} median={2,9:N1} max={3,9:N1}   enum_ms_total median={4,10:N1}   entries/s median={5,9:N0}   root={6:N0}" -f `
                $spec, $sorted[0], (Get-Median $walk[$spec]), $sorted[-1], (Get-Median $enum[$spec]), (Get-Median $rate[$spec]), $roots[$spec]
        }
        $base = Get-Median $walk[$Specs[0]]
        foreach ($spec in $Specs | Select-Object -Skip 1) {
            $change = ((Get-Median $walk[$spec]) - $base) / $base * 100
            "  {0,-20} vs {1}: {2:+0.0;-0.0} % walk_ms (median)" -f $spec, $Specs[0], $change
        }
    }
}

if ($failures -gt 0) { "$failures snapshot(s) differ"; exit 1 }
exit 0
```

`scripts\New-EnumFixture.ps1`:

```powershell
<#
.SYNOPSIS
  Creates a deterministic synthetic tree for comparing directory enumerators.

.DESCRIPTION
  -Kind Many   many small directories (default 20,000 directories with 3 files each): the cost per directory dominates.
  -Kind Large  few large directories (default 10 directories with 20,000 files each): the cost per entry dominates.
  Names and sizes depend only on the position (directory d00000, file f00000.dat, size (index mod 7) * 100 bytes), so the same
  parameters always give the same tree. -Root must not exist yet: the script never writes into an existing folder. Remove the
  tree afterwards with [IO.Directory]::Delete(<root>, $true).

.EXAMPLE
  .\scripts\New-EnumFixture.ps1 -Root $env:TEMP\enum-many -Kind Many
  .\scripts\New-EnumFixture.ps1 -Root $env:TEMP\enum-large -Kind Large
#>
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][ValidateSet('Many', 'Large')][string]$Kind,
    [int]$Directories = $(if ($Kind -eq 'Many') { 20000 } else { 10 }),
    [int]$FilesPerDirectory = $(if ($Kind -eq 'Many') { 3 } else { 20000 })
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
if (Test-Path $Root) { throw "$Root already exists; choose a folder that does not exist." }

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
[void][IO.Directory]::CreateDirectory($Root)
$bytes = 0L
for ($d = 0; $d -lt $Directories; $d++) {
    $directory = [IO.Path]::Combine($Root, ('d{0:00000}' -f $d))
    [void][IO.Directory]::CreateDirectory($directory)
    for ($f = 0; $f -lt $FilesPerDirectory; $f++) {
        $size = ($f % 7) * 100
        $stream = [IO.File]::Create([IO.Path]::Combine($directory, ('f{0:00000}.dat' -f $f)))
        if ($size -gt 0) { $stream.SetLength($size) }
        $stream.Dispose()
        $bytes += $size
    }
}
"{0}: {1:N0} directories, {2:N0} files, {3:N0} bytes, created in {4:N0} s" -f $Root, ($Directories + 1), ($Directories * $FilesPerDirectory), $bytes, $stopwatch.Elapsed.TotalSeconds
```

- [ ] **Step 2: Create the two synthetic workloads**

```powershell
ConvertTo-Crlf scripts\Compare-Enumerators.ps1, scripts\New-EnumFixture.ps1   # helper from the ground rules, defined in this same command
.\scripts\New-EnumFixture.ps1 -Root "$env:TEMP\enum-fixture-many" -Kind Many
.\scripts\New-EnumFixture.ps1 -Root "$env:TEMP\enum-fixture-large" -Kind Large
```

Expected (the prototype): `20,001 directories, 60,000 files, 6,000,000 bytes` (about 25 s) and `11 directories, 200,000 files, 59,997,000 bytes` (about 60 s). The script refuses to write into an existing folder.

- [ ] **Step 3: Equality of all enumerator variants on quiescent trees**

Use the NativeAOT executable of Task 3 as the tool (it is the shipped build type and starts faster).

```powershell
$exe = (Resolve-Path .\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe).Path
$specs = 'find','find:nolarge','handle:idextd:64','handle:full:64','handle:idextd:4','handle:full:4','nt:dir:64','nt:full:64','nt:idextd:64','nt:dir:4','nt:full:4','nt:idextd:4'
.\scripts\Compare-Enumerators.ps1 -Path 'T:\', 'C:\Program Files\dotnet', 'C:\Windows\System32\drivers', "$env:TEMP\enum-fixture-many", "$env:TEMP\enum-fixture-large" -Equal -Specs $specs -Tool $exe; "exit: $LASTEXITCODE"
```

Expected (the prototype): for every tree the first line `reference  find ...` and then `EQUAL` for all 11 other variants, `exit: 0`. Any `DIFFERENT` is a bug in an enumerator: report the printed lines.

- [ ] **Step 4: The exFAT volume `D:` (capability matrix)**

```powershell
.\scripts\Compare-Enumerators.ps1 -Path 'D:\' -Equal -Specs $specs -Tool $exe; "exit: $LASTEXITCODE"
```

Expected (the prototype, `D:` is exFAT): `find` is the reference; `find:nolarge`, all `handle:full:*`, all `nt:dir:*` and all `nt:full:*` are `EQUAL`; **all `idextd` variants (`handle:idextd:*`, `nt:idextd:*`) are `FAILED` with `error 87`** (`ERROR_INVALID_PARAMETER`: the class is not supported on exFAT); `exit: 1`. That exit code is expected here and is not an error of the task; keep the printed lines, they are the capability matrix. If `D:` is not present, say so and skip this step. If a variant that is not an `idextd` one fails or differs, report it.

- [ ] **Step 5: Commit**

```powershell
git add scripts\Compare-Enumerators.ps1 scripts\New-EnumFixture.ps1
git commit -m @'
Add scripts\Compare-Enumerators.ps1 and scripts\New-EnumFixture.ps1

Snapshot equality across enumerators and volumes (failures are reported, not hidden), the timed
comparison, and deterministic synthetic workloads.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 5: Measurement, results and the adoption rule

**Files:** none changed except the documents written in Task 6. This task produces the numbers; keep every printed table.

The design's method (docs\design_fs.md, "Measurement"): workers fixed at 8, warm cache, enumerators alternated with the order rotated each round, the median of `walk_ms` as the primary metric, `enum_ms_total` as a diagnostic. `C:` is a live volume: run nothing heavy in parallel while measuring. Each command below runs for minutes: use the PowerShell tool timeout of 600000 ms and run them one at a time.

- [ ] **Step 1: Screening (all variants, few rounds)**

```powershell
$exe = (Resolve-Path .\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe).Path
$screen = 'find','find:nolarge','handle:full:4','handle:full:64','handle:full:1024','handle:idextd:64','nt:dir:4','nt:dir:64','nt:dir:1024','nt:full:64','nt:idextd:64'
.\scripts\Compare-Enumerators.ps1 -Path "$env:TEMP\enum-fixture-many" -Time -Rounds 3 -Specs $screen -Tool $exe
.\scripts\Compare-Enumerators.ps1 -Path "$env:TEMP\enum-fixture-large" -Time -Rounds 3 -Specs $screen -Tool $exe
```

Expected (the prototype, W2 then W3; small absolute numbers, so noisy): W2 (`many`) `find` about 950 ms, the best alternatives about 750-810 ms; W3 (`large`) `find` about 56 ms, the best alternatives about 42-50 ms; the buffer size of 64 KiB was faster than 4 KiB and than 1024 KiB for most variants on W2.

Then W1, the real volume (8 variants, 2 rounds, about 5 minutes):

```powershell
.\scripts\Compare-Enumerators.ps1 -Path 'C:\' -Time -Rounds 2 -Specs find,find:nolarge,handle:full:64,handle:idextd:64,nt:dir:64,nt:full:64,nt:idextd:64,nt:dir:1024 -Tool $exe
```

Expected (the prototype): `find` about 15 s; the alternatives 8-10 % lower; `find:nolarge` about 4 % higher; `nt:dir:1024` about 4.5 % lower than `find`. With 2 rounds the "median" is the minimum: the screening only chooses the finalists.

- [ ] **Step 2: Choose the finalists**

Rule: from the screening on W1, take the variant with the lowest median `walk_ms` of each family, **restricted to variants that work on every available file system** (so no `idextd`, which fails on exFAT): the best `handle:*` and the best `nt:*`. The reference is `find` (with `LARGE_FETCH`). Write the three names down (for the prototype run they would have been `handle:full:64` and `nt:full:64` or `nt:dir:64`; use your own numbers).

- [ ] **Step 3: The finalists, 5 rounds, on all three workloads**

```powershell
$finalists = 'find', '<best handle variant>', '<best nt variant>'   # replace the two names with the ones chosen in Step 2
.\scripts\Compare-Enumerators.ps1 -Path 'C:\' -Time -Rounds 5 -Specs $finalists -Tool $exe
.\scripts\Compare-Enumerators.ps1 -Path "$env:TEMP\enum-fixture-many" -Time -Rounds 5 -Specs $finalists -Tool $exe
.\scripts\Compare-Enumerators.ps1 -Path "$env:TEMP\enum-fixture-large" -Time -Rounds 5 -Specs $finalists -Tool $exe
```

The W1 run takes about 5 minutes. Keep the three printed tables (min / median / max of `walk_ms`, the median `enum_ms_total`, `entries/s`, and the percentage against `find`).

- [ ] **Step 4: Apply the adoption rule (docs\design_fs.md, "Adoption rule") literally**

For each of B and C, on the W1 result of Step 3: (1) correctness: identical to A on every volume and fixture (Task 4: yes for the variants without `idextd`); (2) primary performance: is its median `walk_ms` at least 10 % lower than `find`'s? (3) diagnostic: `enum_ms_total` next to it. Also state for W2 and W3 whether it is clearly slower than `find`. Then write down, in plain words: which variants are **candidates for the default**, and that **C is never made the default by this rule alone** (native, experimental).

**If B, or C, is a candidate: do not change the default and do not implement a fallback in this task.** Report the numbers and the recommendation to the controller, who decides; the default change (with a fallback to `find`) would be a separate, specified step.

- [ ] **Step 5: The worker sweep for the best variant, only if one is a candidate**

```powershell
foreach ($w in 1, 2, 4, 8) { "workers=$w"; .\scripts\Compare-Enumerators.ps1 -Path 'C:\' -Time -Rounds 3 -Workers $w -Specs 'find', '<the candidate>' -Tool $exe | Select-Object -Skip 1 }
```

If no variant is a candidate, skip this step and say so. (The sweep is only about the winner: it must not be mixed with the enumerator comparison.)

- [ ] **Step 6: Report**

No commit in this task. Report to the controller: the three tables of Step 3, the screening tables, the verdict of Step 4 for B and for C, and the sweep if it was done. The results go into the documents in Task 6, written by whoever has decided.

---

### Task 6: Results into the documents, and the final verification

**Files:**
- Modify: `docs\roadmap.md`, `docs\design_fs.md`, `README.md`, `README-jp.md`

Do this task after the controller has answered Task 5 Step 4.

- [ ] **Step 1: README.md and README-jp.md**

`README.md`, the `dirsizer.exe` section. Old:

```
The path may be any directory, not only a drive root.
```

New:

```
The path may be any directory, not only a drive root. `--enumerator=NAME[:CLASS[:KiB]]` is an advanced option for comparisons: it chooses the API that reads the directories (`find`, the default, is `FindFirstFileExW`; `handle` is `GetFileInformationByHandleEx`; `nt` is `NtQueryDirectoryFileEx`); see [docs/design_fs.md](docs/design_fs.md), "Enumerator comparison (P4)", for what was measured. The `idextd` class is not supported on exFAT.
```

`README-jp.md`. Old:

```
パスはドライブのルートに限らず、任意のディレクトリを指定できます。
```

New:

```
パスはドライブのルートに限らず、任意のディレクトリを指定できます。`--enumerator=NAME[:CLASS[:KiB]]` は比較用の上級者向けオプションで、ディレクトリを読む API を選びます（`find` が既定で `FindFirstFileExW`、`handle` は `GetFileInformationByHandleEx`、`nt` は `NtQueryDirectoryFileEx`）。測定結果は [docs/design_fs.md](docs/design_fs.md)（英語）の "Enumerator comparison (P4)" を参照してください。`idextd` クラスは exFAT では使えません。
```

(Both Old texts occur exactly once in their files.)

- [ ] **Step 2: docs\design_fs.md: the results**

Append to the section "Enumerator comparison (P4)", after "Not part of this work", a subsection `### Results` written from the printed tables of Task 5: the machine (4 logical processors), the date, the workloads with their sizes, the finalists' table (min / median / max of `walk_ms`, median `enum_ms_total`, entries/s, percentage against `find`) for W1, W2 and W3, the verdict for B and for C in the words of the adoption rule, and the **capability matrix** below. Do not write a number that a run did not print, and do not write a conclusion the rule does not give.

```markdown
#### Capability matrix (what worked, measured here)

| Variant | NTFS (`C:`, `T:`) | exFAT (`D:`) | ReFS, network share |
| --- | --- | --- | --- |
| `find`, `find:nolarge` | works, identical | works, identical | not available here, unverified |
| `handle:full`, `nt:dir`, `nt:full` (4 to 1024 KiB) | works, identical to `find` | works, identical to `find` | not available here, unverified |
| `handle:idextd`, `nt:idextd` | works, identical to `find` | **fails with error 87** (`ERROR_INVALID_PARAMETER`) | not available here, unverified |
```

- [ ] **Step 3: docs\roadmap.md**

Insert immediately before the heading `## Promotion criteria: bulk from experimental to default candidate` a section `## P4 - Directory enumerators` with: `[x]` items for what was done and verified in this branch (the contract with the snapshot proof, B and C with the conformance tests and the native-AOT run, `--enumerator`, the scripts, the equality on the volumes, the measurements, the capability matrix) and an item for the decision made in Task 5 Step 4 (`[x]` recorded, or `[ ]` if a default change was recommended and is still to be specified). Keep `[ ]` for what was not done: cold cache, ReFS and network shares, a second machine. Every `[x]` must be backed by an output that was printed in Tasks 1-5.

- [ ] **Step 4: Remove the synthetic workloads and check the tree**

```powershell
foreach ($n in 'enum-fixture-many', 'enum-fixture-large') { $p = Join-Path $env:TEMP $n; if (Test-Path $p) { [IO.Directory]::Delete($p, $true) } }
git status --short
```

Expected: only the four documents of this task are modified. (The snapshots under `artifacts\snapshots` are git-ignored and may stay.)

- [ ] **Step 5: Final verification**

```powershell
git diff --stat master -- src/DirSizer.Core src/DirSizer.Bulk src/DirSizer.Fsctl src/DirSizer.Inspect src/DirSizer.Compare src/Shared
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 3
dotnet .\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
dotnet .\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll --self-test | Select-Object -Last 1
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test | Select-Object -Last 1
dotnet .\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: the `git diff --stat` prints **nothing**; the solution builds with `0 Error(s)`; the three NTFS tools' self-tests pass as before; `34 self-tests passed, 0 skipped.`

- [ ] **Step 6: Commit**

```powershell
ConvertTo-Crlf docs\design_fs.md, docs\roadmap.md   # existing CRLF files edited with the Edit tool keep CRLF; this is a safeguard
git add README.md README-jp.md docs\design_fs.md docs\roadmap.md
git commit -m @'
Record the enumerator comparison: results, capability matrix, decision

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

## Self-review against the spec

| Spec section | Task |
| --- | --- |
| The three enumerators and their standing (A baseline, B Win32 alternative, C native/experimental; C not promoted on speed alone) | 2, 3; rule in Task 5 Step 4 |
| The contract (neutral sink, per-worker instance, factory, canonical names) and Step 1 without behaviour change, proved by snapshots | 1, 2 |
| Handle-based enumerators: open per directory, errors at the open (not-found is a failure), buffer, entry layouts | 3 (conformance tests prove the offsets) |
| B: Restart class first, plain class after; C: `SL_RESTART_SCAN` first only, never `SL_RETURN_SINGLE_ENTRY`, empty successful query is an error, `STATUS_NO_MORE_FILES` | 3 (code and comments; tests at the 4 KiB boundary) |
| `--enumerator` grammar, defaults, bad values, reporting in the benchmark line and JSON | 3 |
| Correctness: per-enumerator conformance, whole walk 1/3/8 workers, real volumes | 3, 4 |
| Measurement: workers fixed at 8, W1-W3, alternating rounds, `walk_ms` primary, `enum_ms_total` diagnostic, screening then finalists, sweep for the winner only | 4, 5 |
| Adoption rule, C separate, default change only with a fallback | 5 Step 4 (stops and asks) |
| Capability matrix, results, roadmap, README | 4 Step 4, 6 |
| Not part of this work (cold cache, ReFS/network, async queries) | stated in the results |
