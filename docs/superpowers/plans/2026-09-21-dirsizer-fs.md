# dirsizer.exe (filesystem enumeration) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `dirsizer.exe`, a fast, read-only, non-elevated folder-size scanner for any Windows filesystem (directory enumeration with a bounded parallel walk), as specified in [docs/design_fs.md](../../design_fs.md). This plan covers P0 (correct baseline) and P1 (parallel walk); the enumeration-API benchmark (P2) is a later phase.

**Architecture:** A new self-contained project `src\DirSizer.Fs` (assembly `dirsizer`). It shares no code with `DirSizer.Core` or the NTFS tools and changes none of them. One worker thread pool reads directories with `FindFirstFileExW` (`FindExInfoBasic` + `LARGE_FETCH`), pushes sub-directories on one shared LIFO stack, and records one `DirNode` per directory (ids handed out at discovery, so a parent's id is always smaller than its children's). Aggregation is one reverse loop over the id array. Files are never stored, only counted and fed to bounded top-N heaps.

**Tech Stack:** C# 12 / .NET 8 (`net8.0-windows`), NativeAOT, `DllImport` on `kernel32` with a blittable `WIN32_FIND_DATAW` (`[InlineArray]`, no unsafe code), `System.Text.Json` source generation. No NuGet packages, no LINQ in production code (the repo's convention).

---

## Ground rules for the executing agent

- **The code in this plan was built and run before it was written down.** It compiles with 0 warnings, and its 28 self-tests pass in the JIT build and in the NativeAOT build (2026-09-21, prototype in a scratch folder). Copy it exactly. If something does not compile or a test fails, that is new information: investigate, do not "fix" by rewriting. A change to the design (for example enabling `AllowUnsafeBlocks`) needs the user's approval.
- **The commands were rehearsed in a clone of this repository** (a copy in a scratch folder, not this working tree): the file placement and `dotnet sln add` of Task 1, the build, the JIT and NativeAOT self-tests, the checks of Task 6 Steps 2-4, the script of Task 8 with its default paths, the `release.ps1` edit of Task 9 Step 1, and the package check of Task 10 Step 2 (release script about 1 minute). Not rehearsed: the mutations of Task 7, the documentation edits of Task 9 Steps 2-8 (their `old` texts were checked to occur exactly once in the current files), the full-volume sweep, and any non-elevated start.
- **Run all commands in an unrestricted PowerShell from the repository root** (`C:\Users\user\source\repos\panzoux\dirsizer`). A sandboxed or restricted shell can ignore deny ACLs; then the "denied" self-test *skips itself with a message* instead of running (24 passed, 1 skipped). That is acceptable for a run in such a shell but the final verification (Task 10) needs the unrestricted run with 0 skipped.
- **Do not edit** anything under `src\DirSizer.Core`, `src\DirSizer.Bulk`, `src\DirSizer.Fsctl`, `src\DirSizer.Inspect`, `src\DirSizer.Compare` or `src\Shared`. Task 10 checks this with `git diff`.
- The output folder of a project is `artifacts\bin\<project>\release_win-x64\` (`Directory.Build.props`), so the tests run as `dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test`.
- Commit messages end with the line `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`. Commits are made on branch `feature/dirsizer-fs`; never on `master`. Do not push.
- Numbers quoted below (test counts, timings, byte counts) come from the prototype. Timings depend on the machine and cache state: compare your own with them, do not copy them.
- **Line endings.** The repository stores UTF-8 without BOM and **CRLF** (`git ls-files --eol` shows `i/crlf w/crlf` for every file). The Edit tool preserves CRLF in existing files (checked), but files created with the Write tool are LF. Define these two helpers once per PowerShell session and run `Normalize-FsProject` before every commit that adds files under `src\DirSizer.Fs`; the commit steps below call it.

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

## File structure

All new files are under `src\DirSizer.Fs\` unless stated otherwise.

| File | Responsibility | Task |
| --- | --- | --- |
| `DirSizer.Fs.csproj`, `app.manifest` | project (NativeAOT, `asInvoker`) | 1 |
| `Win32Find.cs` | `WIN32_FIND_DATAW`, the three `kernel32` calls, name and size helpers | 1 |
| `DirectoryReader.cs` | enumerate one directory, feed a sink, LARGE_FETCH fallback | 1 |
| `SelfTests.cs` | test harness, assertions, `TempTree` fixture helper | 1 |
| `SelfTests.Reader.cs` | tests: layout, reader, LARGE_FETCH | 1 |
| `DirModel.cs` | `DirNode`, `BoundedTop<T>`, `DirTable` (build, aggregate, paths) | 2 |
| `SelfTests.Model.cs` | tests: aggregation, table, top-N, paths (no file system) | 2 |
| `RootPath.cs` | display and extended-length forms of the root | 3 |
| `FsOptions.cs` | command line | 3 |
| `SelfTests.Cli.cs` | tests: root paths, options | 3 |
| `Walker.cs` | work queue, counters, worker, walk | 4 |
| `FsScanner.cs` | result types, phases, top-N selection, paths | 4 |
| `SelfTests.Walk.cs` | tests: the whole walk against an independent oracle | 4 |
| `FsOutput.cs` | text and JSON output | 5 |
| `SelfTests.Output.cs` | tests: output | 5 |
| `Program.cs` | entry point, exit codes, Ctrl+C | 1 (temporary), 6 (final) |
| `scripts\Compare-Fs.ps1` | oracle / bulk comparison, worker sweep | 8 |
| `DirSizer.sln`, `scripts\release.ps1`, `README.md`, `README-jp.md`, `docs\roadmap.md`, `docs\design.md`, `docs\design_mft.md` | registration, packaging, documentation | 1, 9 |

The tests register themselves through `static partial void Add...Tests(List<SelfTest>)` methods declared in `SelfTests.cs`. A file that implements one is picked up automatically, so each task only adds files. The expected test count therefore grows task by task: 5, 10, 13, 25, 28.

---

### Task 0: Branch and baseline

**Files:** none changed; `docs\design_fs.md` and this plan are committed.

- [ ] **Step 1: Check the starting state and create the branch**

```powershell
git status --short
git switch -c feature/dirsizer-fs
```

Expected: `git status` lists `docs/design_fs.md` and the directory `docs/superpowers/` (it contains this plan) as untracked (`??`) and nothing else. If other files are modified, stop and ask.

- [ ] **Step 2: Baseline of the existing tools**

```powershell
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 4
dotnet .\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test; "fsctl exit: $LASTEXITCODE"
dotnet .\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll --self-test; "bulk exit: $LASTEXITCODE"
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test; "inspect exit: $LASTEXITCODE"
```

Expected: `Build succeeded.` with `0 Error(s)`, each tool's self-test ends with a line saying its tests passed, and all three exit codes are `0`. Keep this output: Task 10 repeats it and the results must be the same.

- [ ] **Step 3: Commit the spec and the plan**

```powershell
ConvertTo-Crlf docs\design_fs.md, docs\superpowers\plans\2026-09-21-dirsizer-fs.md   # helper from "Ground rules"
git add docs/design_fs.md docs/superpowers/plans/2026-09-21-dirsizer-fs.md
git commit -m @'
Add the dirsizer.exe design spec and implementation plan

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 1: Project, Win32 layer, directory reader, test harness

**Files:**
- Create: `src\DirSizer.Fs\DirSizer.Fs.csproj`, `src\DirSizer.Fs\app.manifest`
- Create: `src\DirSizer.Fs\SelfTests.cs`, `src\DirSizer.Fs\SelfTests.Reader.cs`, `src\DirSizer.Fs\Program.cs` (temporary)
- Create: `src\DirSizer.Fs\Win32Find.cs`, `src\DirSizer.Fs\DirectoryReader.cs`
- Modify: `DirSizer.sln` (by `dotnet sln`)

This task also settles the one open technical question of the spec: a blittable `[InlineArray]` structure passed to `DllImport` works with `AllowUnsafeBlocks=false` in the NativeAOT build (the canary at the end).

- [ ] **Step 1: Create the project files**

`src\DirSizer.Fs\DirSizer.Fs.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- dirsizer.exe: folder-size scan by directory enumeration. Any filesystem, no elevation. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>dirsizer</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <DebugType>none</DebugType>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Speed</OptimizationPreference>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
</Project>
```

`src\DirSizer.Fs\app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
```

- [ ] **Step 2: Write the tests first: harness and reader tests**

`src\DirSizer.Fs\SelfTests.cs`:

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
    static partial void AddWalkTests(List<SelfTest> tests);
    static partial void AddOutputTests(List<SelfTest> tests);

    // Returns the process exit code: 0 if no test failed.
    public static int Run()
    {
        var tests = new List<SelfTest>();
        AddCliTests(tests);
        AddModelTests(tests);
        AddReaderTests(tests);
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

`src\DirSizer.Fs\SelfTests.Reader.cs`:

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

        public void OnEntry(in Win32FindData entry)
        {
            var name = Win32Find.NameString(in entry);
            Entries[name] = entry.FileAttributes;
            Sizes[name] = Win32Find.FileSize(in entry);
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
        var data = new Win32FindData();
        var result = new DirectoryReader(AlwaysFails(error, list)).Read(@"\\?\C:\anything", ref data, new CollectingSink());
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
        var data = new Win32FindData();
        var result = new DirectoryReader().Read(tree.Base, ref data, sink);
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
        var data = new Win32FindData();
        var missing = new DirectoryReader().Read(tree.Full("does-not-exist"), ref data, new CollectingSink());
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
        var reader = new DirectoryReader(rejecter.Call);
        Assert(reader.LargeFetch, "the flag starts on");
        var data = new Win32FindData();

        var first = reader.Read(tree.Base, ref data, new CollectingSink());
        AssertEqual(ReadOutcome.Complete, first.Outcome, "the retry without the flag succeeds");
        Assert(!reader.LargeFetch, "the flag is off after a successful retry");
        var second = reader.Read(tree.Base, ref data, new CollectingSink());
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
        var reader = new DirectoryReader(AlwaysInvalidParameter);
        var data = new Win32FindData();
        var result = reader.Read(@"\\?\C:\anything", ref data, new CollectingSink());
        AssertEqual(ReadOutcome.Failed, result.Outcome, "outcome");
        AssertEqual(Win32Find.ErrorInvalidParameter, result.Error, "error");
        AssertEqual("True,False", string.Join(',', calls), "exactly one retry");
        Assert(reader.LargeFetch, "the flag stays on: the retry failed too, so the flag was not the cause");
    }
}
```

`src\DirSizer.Fs\Program.cs` (temporary entry point; Task 6 replaces it):

```csharp
// Temporary entry point until the command line exists (Task 6).
return FsSelfTests.Run();
```

- [ ] **Step 3: Register the project in the solution**

```powershell
dotnet sln DirSizer.sln add src\DirSizer.Fs\DirSizer.Fs.csproj --solution-folder src
git diff --stat DirSizer.sln
```

Expected: `DirSizer.sln` shows only added lines (a project entry, its configuration lines, and its nesting under the existing `src` folder). If a second, duplicate `src` solution folder was created, undo with `git checkout -- DirSizer.sln` and add the project without `--solution-folder`.

- [ ] **Step 4: Run the build to see the tests fail**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error" | Select-Object -First 5
```

Expected: FAIL to compile, with `error CS0246` / `CS0103` naming `Win32FindData`, `DirectoryReader`, `IEntrySink` and `Win32Find` (they do not exist yet).

- [ ] **Step 5: Write the Win32 layer and the reader**

`src\DirSizer.Fs\Win32Find.cs`:

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

// The one call that the self-test replaces to inject failures (see DirectoryReader). Not an enumerator abstraction.
delegate FindFirstResult FindFirstFn(string pattern, ref Win32FindData data, bool largeFetch);

interface IEntrySink
{
    // Called for every entry except "." and "..". The reference is only valid during the call.
    void OnEntry(in Win32FindData entry);
}

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

    public static bool IsDotEntry(in Win32FindData entry)
    {
        ReadOnlySpan<char> name = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)entry.FileName);
        return name[0] == '.' && (name[1] == '\0' || name[1] == '.' && name[2] == '\0');
    }

    public static string NameString(in Win32FindData entry)
    {
        ReadOnlySpan<char> name = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)entry.FileName);
        var length = name.IndexOf('\0');
        return new string(length < 0 ? name : name[..length]);
    }

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

`src\DirSizer.Fs\DirectoryReader.cs`:

```csharp
// NotRead is the default value on purpose: a result that was never filled in must not look like a successful read.
enum ReadOutcome { NotRead, Complete, Denied, Failed }

readonly record struct ReadResult(ReadOutcome Outcome, int Error);

// Enumerates one directory with FindFirstFileExW / FindNextFileW and hands every entry to a sink. One reader is shared by all
// workers; it holds the LARGE_FETCH switch, which is one flag for the whole run.
sealed class DirectoryReader(FindFirstFn? findFirst = null)
{
    readonly FindFirstFn _findFirst = findFirst ?? Win32Find.FindFirst;
    volatile bool _largeFetch = true;

    public bool LargeFetch => _largeFetch;

    // directoryPath is the extended-length path of the directory. The buffer is the caller's, so no worker allocates per call.
    public ReadResult Read(string directoryPath, ref Win32FindData data, IEntrySink sink)
    {
        var pattern = directoryPath.EndsWith('\\') ? directoryPath + "*" : directoryPath + "\\*";
        var largeFetch = _largeFetch;
        var first = _findFirst(pattern, ref data, largeFetch);
        if (first.Handle == Win32Find.InvalidHandle && largeFetch && first.Error == Win32Find.ErrorInvalidParameter)
        {
            // Some filesystems reject the flag. Retry once without it; only if that works is the flag switched off for the run,
            // because the same error can also come from the path itself.
            first = _findFirst(pattern, ref data, false);
            if (first.Handle != Win32Find.InvalidHandle) _largeFetch = false;
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
                if (!Win32Find.IsDotEntry(in data)) sink.OnEntry(in data);
                if (Win32Find.TryFindNext(first.Handle, ref data, out var error)) continue;
                return error == Win32Find.ErrorNoMoreFiles ? new ReadResult(ReadOutcome.Complete, 0) : new ReadResult(ReadOutcome.Failed, error);
            }
        }
        finally
        {
            Win32Find.Close(first.Handle);
        }
    }
}
```

- [ ] **Step 6: Build and run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test
```

Expected: `0 Warning(s)`, `0 Error(s)`, then

```
ok    Win32FindData has the 592-byte native layout and a 64-bit file size
ok    reader lists entries, skips . and .., reports names and attributes
ok    reader classifies find-first errors: missing, empty, denied, other
ok    LARGE_FETCH: rejected once, retried without, and off from then on
ok    LARGE_FETCH: a failing retry is an ordinary failure and keeps the flag
5 self-tests passed, 0 skipped.
```

- [ ] **Step 7: NativeAOT canary**

The whole design rests on this working in the shipped build type, so check it now, before more code exists.

```powershell
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) { $env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer" }
dotnet publish src\DirSizer.Fs\DirSizer.Fs.csproj -c Release -r win-x64 2>&1 | Select-Object -Last 3
.\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe --self-test | Select-Object -Last 1
```

Expected: the publish ends without errors (the prototype took about 15 s and produced a 3.3 MB `dirsizer.exe`), and the last line is `5 self-tests passed, 0 skipped.` If publish or a test fails only here, stop and report: the spec's fallback (`AllowUnsafeBlocks`) is a design change that needs the user's decision.

- [ ] **Step 8: Commit**

```powershell
Normalize-FsProject
git add DirSizer.sln src\DirSizer.Fs
git commit -m @'
Add the dirsizer.exe project: Win32 enumeration, directory reader, self-test harness

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 2: Directory model

**Files:**
- Create: `src\DirSizer.Fs\SelfTests.Model.cs`
- Create: `src\DirSizer.Fs\DirModel.cs`

- [ ] **Step 1: Write the tests first**

`src\DirSizer.Fs\SelfTests.Model.cs`:

```csharp
// The directory model on synthetic data: no file system involved.
static partial class FsSelfTests
{
    static partial void AddModelTests(List<SelfTest> tests)
    {
        tests.Add(new("aggregation rolls sizes up to the root", AggregationRollsUp));
        tests.Add(new("aggregation rejects a parent id that is not smaller", AggregationRejectsBadParent));
        tests.Add(new("table build rejects a duplicated id", TableRejectsDuplicateId));
        tests.Add(new("bounded top-N keeps the largest, and merges", BoundedTopKeepsLargest));
        tests.Add(new("paths are built from the root name and node names", PathsFromNodes));
    }

    // 0 root(own 1) -> 1 a(own 2) -> 2 b(own 4);  0 -> 3 c(own 8)
    static DirNode[] SyntheticNodes()
    {
        var nodes = new DirNode[]
        {
            new(0, -1, @"C:\r"),
            new(1, 0, "a"),
            new(2, 1, "b"),
            new(3, 0, "c"),
        };
        nodes[0].OwnFileSize = 1;
        nodes[1].OwnFileSize = 2;
        nodes[2].OwnFileSize = 4;
        nodes[3].OwnFileSize = 8;
        return nodes;
    }

    static void AggregationRollsUp()
    {
        var nodes = SyntheticNodes();
        DirTable.Aggregate(nodes);
        AssertEqual(4L, nodes[2].Total, "b");
        AssertEqual(6L, nodes[1].Total, "a = own 2 + b 4");
        AssertEqual(8L, nodes[3].Total, "c");
        AssertEqual(15L, nodes[0].Total, "root = own 1 + a 6 + c 8");

        // The commonest real input: a table with only the root (an empty directory), through Build and then Aggregate.
        var alone = DirTable.Build(new DirNode(0, -1, @"C:\empty"), []);
        AssertEqual(1, alone.Length, "Build with no other nodes");
        alone[0].OwnFileSize = 7;
        DirTable.Aggregate(alone);
        AssertEqual(7L, alone[0].Total, "a table with only the root");
    }

    static void AggregationRejectsBadParent()
    {
        var nodes = SyntheticNodes();
        nodes[2] = new DirNode(2, 3, "b");   // parent id 3 is larger than the node's own id 2
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(nodes), "parent id larger than id");
        var selfParent = SyntheticNodes();
        selfParent[1] = new DirNode(1, 1, "a");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(selfParent), "node that is its own parent");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate([]), "an empty table");
        var rootWithParent = SyntheticNodes();
        rootWithParent[0] = new DirNode(0, 3, @"C:\r");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(rootWithParent), "a root that has a parent");
        var wrongId = SyntheticNodes();
        wrongId[2] = new DirNode(9, 1, "b");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(wrongId), "a node whose id is not its index");
    }

    static void TableRejectsDuplicateId()
    {
        var root = new DirNode(0, -1, @"C:\r");
        var first = new List<DirNode> { new(1, 0, "a") };
        var second = new List<DirNode> { new(1, 0, "b") };
        AssertThrows<InvalidOperationException>(() => DirTable.Build(root, [first, second]), "duplicate id");
        var outOfRange = new List<DirNode> { new(5, 0, "a") };
        AssertThrows<InvalidOperationException>(() => DirTable.Build(root, [outOfRange]), "id beyond the table");
        var secondRoot = new List<DirNode> { new(0, 0, "x") };
        AssertThrows<InvalidOperationException>(() => DirTable.Build(root, [secondRoot]), "a second node with id 0");
        var ok = DirTable.Build(root, [new List<DirNode> { new(2, 0, "b") }, new List<DirNode> { new(1, 0, "a") }]);
        AssertEqual("a", ok[1].Name, "nodes are placed by id, whatever list they came from");
        AssertEqual("b", ok[2].Name, "nodes are placed by id, whatever list they came from");
    }

    static void BoundedTopKeepsLargest()
    {
        var top = new BoundedTop<string>(3);
        var sizes = new long[] { 5, 1, 9, 7, 3, 8 };
        foreach (var size in sizes) top.Add("n" + size, size);
        AssertEqual(3, top.Count, "bounded");
        Assert(!top.WouldAccept(7), "a size equal to the smallest kept is not accepted");
        Assert(top.WouldAccept(8), "a size above the smallest kept is accepted");
        var other = new BoundedTop<string>(3);
        other.Add("n10", 10);
        other.Add("n2", 2);
        top.AddAll(other);
        AssertEqual("n10,n9,n8", string.Join(',', top.ToDescendingArray()), "merged, largest first");
        AssertEqual(0, top.Count, "ToDescendingArray empties the heap");

        var roomy = new BoundedTop<string>(5);
        roomy.Add("zero", 0);
        Assert(roomy.WouldAccept(0), "a heap that is not full accepts any size");
        var none = new BoundedTop<string>(0);
        none.Add("x", 100);
        AssertEqual(0, none.Count, "a limit of 0 keeps nothing");
        var same = new BoundedTop<string>(2);
        same.Add("a", 1);
        same.Add("b", 2);
        same.AddAll(same);
        AssertEqual("b,a", string.Join(',', same.ToDescendingArray()), "merging a heap into itself changes nothing");

        // Against a sorted-list oracle, with a fixed seed and many ties (sizes 0-5).
        var random = new Random(12345);
        for (var round = 0; round < 500; round++)
        {
            var limit = random.Next(1, 8);
            var count = random.Next(0, 30);
            var heap = new BoundedTop<FileHit>(limit);
            var all = new List<long>();
            for (var index = 0; index < count; index++)
            {
                var size = (long)random.Next(0, 6);
                all.Add(size);
                heap.Add(new FileHit(0, "n" + index, size), size);
            }
            all.Sort();
            all.Reverse();
            var expected = new List<long>();
            for (var index = 0; index < Math.Min(limit, all.Count); index++) expected.Add(all[index]);
            var actual = new List<long>();
            foreach (var hit in heap.ToDescendingArray()) actual.Add(hit.Size);
            AssertEqual(string.Join(',', expected), string.Join(',', actual), $"round {round} (limit {limit}, {count} entries): kept sizes, largest first");
        }
    }

    static void PathsFromNodes()
    {
        var nodes = SyntheticNodes();
        AssertEqual(@"C:\r", DirTable.PathOf(nodes, 0), "root");
        AssertEqual(@"C:\r\a\b", DirTable.PathOf(nodes, 2), "nested");
        AssertEqual(@"a\b", DirTable.RelativePath(nodes, 2), "relative");
        AssertEqual("", DirTable.RelativePath(nodes, 0), "relative root");
        var driveRoot = new DirNode[] { new(0, -1, @"C:\"), new(1, 0, "x") };
        AssertEqual(@"C:\x", DirTable.PathOf(driveRoot, 1), "child of a drive root has no doubled backslash");
        AssertEqual(@"C:\x\f.bin", DirTable.Combine(DirTable.PathOf(driveRoot, 1), "f.bin"), "file path");
        var share = new DirNode[] { new(0, -1, @"\\server\share"), new(1, 0, "x") };
        AssertEqual(@"\\server\share\x", DirTable.PathOf(share, 1), "child of a share root");
        var corrupt = SyntheticNodes();
        corrupt[1] = new DirNode(1, 2, "a");   // parent id 2 is larger than 1: a loop between 1 and 2
        AssertThrows<InvalidOperationException>(() => DirTable.RelativePath(corrupt, 2), "a corrupt table gives an error, not an endless loop");
    }
}
```

- [ ] **Step 2: Run the build to see it fail**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error" | Select-Object -First 4
```

Expected: FAIL to compile, `error CS0246` naming `DirNode`, `DirTable`, `BoundedTop`.

- [ ] **Step 3: Write the model**

`src\DirSizer.Fs\DirModel.cs`:

```csharp
// The directory model: one node per directory, ids handed out at discovery, sizes rolled up after the walk. No file entries are
// kept here; files only enter the bounded top-N heaps.

sealed class DirNode(int id, int parentId, string name)
{
    public int Id { get; } = id;                 // dense, 0 is the root
    public int ParentId { get; } = parentId;     // -1 for the root; for every other node ParentId < Id
    public string Name { get; } = name;          // the root: the root path as displayed
    public long OwnFileSize;                     // files directly in this directory; written by the one worker that enumerates it
    public long Total;                           // OwnFileSize plus everything below; filled by DirTable.Aggregate
}

readonly record struct FileHit(int DirId, string Name, long Size);

// Keeps the `limit` entries with the largest size. Order among equal sizes is unspecified. Not thread-safe: every worker has its own
// instance and they are merged after the join. Which sizes are eligible at all (for example only files larger than 0) is the
// caller's decision; the item and its size are not tied together, because the caller creates the item (a name string) only after
// WouldAccept has said that it will be kept.
sealed class BoundedTop<T>(int limit)
{
    readonly PriorityQueue<T, long> _queue = new();

    public int Count => _queue.Count;

    // True if an entry of this size would be kept, so the caller can avoid creating the item (for example a name string) if not.
    public bool WouldAccept(long size)
    {
        if (_queue.Count < limit) return true;
        return _queue.TryPeek(out _, out var smallest) && size > smallest;
    }

    public void Add(T item, long size)
    {
        if (!WouldAccept(size)) return;
        _queue.Enqueue(item, size);
        if (_queue.Count > limit) _queue.Dequeue();
    }

    public void AddAll(BoundedTop<T> other)
    {
        if (ReferenceEquals(other, this)) return;   // Add would change the queue while it is being enumerated
        foreach (var (item, size) in other._queue.UnorderedItems) Add(item, size);
    }

    // Largest first. Empties the heap.
    public T[] ToDescendingArray()
    {
        var result = new T[_queue.Count];
        for (var index = result.Length - 1; index >= 0; index--) result[index] = _queue.Dequeue();
        return result;
    }
}

static class DirTable
{
    // Puts the root and the nodes each worker created into one array indexed by id. Ids are dense and unique by construction; a
    // violation is an internal error.
    public static DirNode[] Build(DirNode root, List<List<DirNode>> created)
    {
        var count = 1;
        foreach (var list in created) count += list.Count;
        var nodes = new DirNode[count];
        nodes[0] = root;
        foreach (var list in created)
        {
            foreach (var node in list)
            {
                if (node.Id <= 0 || node.Id >= count || nodes[node.Id] is not null)
                    throw new InvalidOperationException($"directory id {node.Id} is out of range or used twice (table of {count})");
                nodes[node.Id] = node;
            }
        }
        return nodes;
    }

    // Fills Total for every node. Relies on one property only: every node's parent has a smaller id, so walking the ids downwards
    // visits every child before its parent. Linear, no queue, no recursion.
    public static void Aggregate(DirNode[] nodes)
    {
        if (nodes.Length == 0 || nodes[0].Id != 0 || nodes[0].ParentId != -1) throw new InvalidOperationException("directory table has no root at id 0");
        foreach (var node in nodes) node.Total = node.OwnFileSize;
        for (var id = nodes.Length - 1; id >= 1; id--)
        {
            var node = nodes[id];
            if (node.Id != id || node.ParentId < 0 || node.ParentId >= id)
                throw new InvalidOperationException($"directory table invariant violated at id {id}: parent id {node.ParentId}");
            nodes[node.ParentId].Total += node.Total;
        }
    }

    public static string Combine(string directory, string name) => directory.EndsWith('\\') ? directory + name : directory + "\\" + name;

    // Full display path. Only called for the few directories that are reported.
    public static string PathOf(DirNode[] nodes, int id)
    {
        if (id == 0) return nodes[0].Name;
        return Combine(nodes[0].Name, RelativePath(nodes, id));
    }

    // Names below the root joined with backslashes; empty for the root.
    public static string RelativePath(DirNode[] nodes, int id)
    {
        var names = new Stack<string>();
        for (var current = id; current != 0; current = nodes[current].ParentId)
        {
            // Aggregate has already validated every table that reaches the output; this turns a corrupt table into an error
            // instead of an endless loop.
            if (nodes[current].ParentId < 0 || nodes[current].ParentId >= current)
                throw new InvalidOperationException($"directory table invariant violated at id {current}: parent id {nodes[current].ParentId}");
            names.Push(nodes[current].Name);
        }
        return string.Join('\\', names);
    }
}
```

- [ ] **Step 4: Build and run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 8
```

Expected: `0 Warning(s)`, `0 Error(s)`; the last lines show the five model tests as `ok` (aggregation rolls sizes up, aggregation rejects a parent id that is not smaller, table build rejects a duplicated id, bounded top-N, paths) followed by `10 self-tests passed, 0 skipped.`

- [ ] **Step 5: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs
git commit -m @'
dirsizer.exe: directory model, aggregation by reverse id order, bounded top-N

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 3: Root path and command line

**Files:**
- Create: `src\DirSizer.Fs\SelfTests.Cli.cs`
- Create: `src\DirSizer.Fs\RootPath.cs`, `src\DirSizer.Fs\FsOptions.cs`

- [ ] **Step 1: Write the tests first**

`src\DirSizer.Fs\SelfTests.Cli.cs`:

```csharp
// Root path handling and command-line options.
static partial class FsSelfTests
{
    static partial void AddCliTests(List<SelfTest> tests)
    {
        tests.Add(new("root paths are normalized to display and extended forms", RootPathForms));
        tests.Add(new("options: defaults and values", OptionsAccepted));
        tests.Add(new("options: bad input is rejected", OptionsRejected));
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

- [ ] **Step 2: Run the build to see it fail**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error" | Select-Object -First 4
```

Expected: FAIL to compile, `error CS0103`/`CS0246` naming `RootPath` and `FsOptions`.

- [ ] **Step 3: Write the root path and the options**

`src\DirSizer.Fs\RootPath.cs`:

```csharp
// The root the user names, in the two forms the tool needs: the display form for output ("C:\Users", "\\server\share") and the
// extended-length form for the API ("\\?\C:\Users", "\\?\UNC\server\share"), which works for paths over 260 characters whatever
// the LongPathsEnabled setting is.
readonly record struct RootPath(string Display, string Extended)
{
    // On .NET Core Path.GetInvalidPathChars() lists only '|' and the control characters; a quote, '<' and '>' cannot be in a Windows
    // path either, and a quote is what a trailing backslash before a closing quote leaves behind.
    static readonly char[] InvalidPathChars = [.. Path.GetInvalidPathChars(), '"', '<', '>'];

    // Throws ArgumentException for an empty path, a character that cannot be in a path, a device path, or a network path without a
    // server and a share. Does not check that the directory exists.
    public static RootPath Normalize(string argument)
    {
        var text = argument.Trim();
        if (text.Length == 0) throw new ArgumentException("A directory path is required, for example C:\\.");
        var invalid = text.IndexOfAny(InvalidPathChars);
        if (invalid >= 0)
        {
            throw new ArgumentException(text[invalid] == '"'
                ? "The path contains a quote character. A backslash right before a closing quote escapes it: leave the trailing backslash out, for example \"C:\\Program Files\"."
                : $"The path contains a character that is not allowed in a path: {text[invalid]}");
        }

        // \\?\Volume{guid}\ names a volume that has no drive letter. It is already an extended-length path and stripping its prefix
        // would leave something that is not a path, so it is used as typed, apart from the trailing backslash. "." and ".." in it
        // are not resolved.
        if (text.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            var closing = text.IndexOf('}');
            var volume = closing < 0 ? "" : text[..(closing + 1)];
            var below = closing < 0 ? "" : text[(closing + 1)..].TrimEnd('\\');
            if (closing < 0 || below.Length > 0 && below[0] != '\\') throw new ArgumentException($"Invalid volume path: {argument}");
            var volumePath = below.Length == 0 ? volume + "\\" : volume + below;
            return new RootPath(volumePath, volumePath);
        }

        // Reduce an extended-length argument to its ordinary form first. GetFullPath does not normalise "\\?\" paths, and the extended
        // form is built again below from the normalised text, so "." or ".." or "/" in the argument are resolved, never taken literally.
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) text = @"\\" + text[8..];
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal) && text.Length >= 6 && char.IsAsciiLetter(text[4]) && text[5] == ':') text = text[4..];
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException($"Device paths are not supported: {argument}");

        // "C:" alone means the drive root here, as in the NTFS tools (Windows itself would take it as the current directory of C:).
        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') text += "\\";
        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Invalid path: {argument}", exception);
        }
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // A device name such as C:\con comes back from GetFullPath as \\.\con.
            var parts = full[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0] is "." or "?") throw new ArgumentException($"Device paths are not supported: {argument}");
            if (parts.Length < 2) throw new ArgumentException($"A network path must name a server and a share, for example \\\\server\\share: {argument}");
        }
        var display = ToDisplay(full);
        var rootLength = Path.GetPathRoot(display)?.Length ?? 0;
        if (display.Length > rootLength) display = display.TrimEnd('\\');
        return new RootPath(display, ToExtended(display));
    }

    public static string ToExtended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    public static string ToDisplay(string path)
    {
        if (path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) return path;   // a volume path has no shorter form
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }
}
```

`src\DirSizer.Fs\FsOptions.cs`:

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

- [ ] **Step 4: Build and run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 5
```

Expected: `0 Warning(s)`, `0 Error(s)`; the last lines include `ok    root paths are normalized to display and extended forms`, `ok    options: defaults and values`, `ok    options: bad input is rejected` and `13 self-tests passed, 0 skipped.`

- [ ] **Step 5: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs
git commit -m @'
dirsizer.exe: root path forms and command-line options

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 4: The parallel walk and the scanner

**Files:**
- Create: `src\DirSizer.Fs\SelfTests.Walk.cs`
- Create: `src\DirSizer.Fs\Walker.cs`, `src\DirSizer.Fs\FsScanner.cs`

The walk tests build real folders under `%TEMP%` (a 10,000-directory fan-out, an 800-deep chain, a path over 260 characters, a junction, a hard link, a deny ACL) and compare the tool with the framework's own directory enumeration. The whole suite takes several seconds.

- [ ] **Step 1: Write the tests first**

`src\DirSizer.Fs\SelfTests.Walk.cs`:

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
        FsScanner.Scan(root, new ScanSettings(workers, top, files, false, cancel, findFirst));

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
            new Walker(Slow).Run(root, tree.Base, 2, 5, false, default, (directories, files) => throw new InvalidOperationException("progress failed"));
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

- [ ] **Step 2: Run the build to see it fail**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error" | Select-Object -First 4
```

Expected: FAIL to compile, `error CS0246` naming `FsScanner`, `ScanSettings`, `FsResult`.

- [ ] **Step 3: Write the walker and the scanner**

`src\DirSizer.Fs\Walker.cs`:

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
    readonly DirectoryReader _reader;
    readonly BoundedTop<FileHit>? _files;
    readonly BoundedTop<FileHit> _rootFiles;
    readonly List<(DirNode Node, string Path)> _children = [];
    Win32FindData _data;
    DirNode _node = null!;
    string _path = null!;
    long _ownSize;

    public readonly Counters Counters = new();
    public readonly List<DirNode> Created = [];
    public readonly List<string> ErrorSamples = [];
    public BoundedTop<FileHit>? Files => _files;
    public BoundedTop<FileHit> RootFiles => _rootFiles;

    public Worker(Walker walker, WorkQueue queue, DirectoryReader reader, int top, bool collectFiles)
    {
        _walker = walker;
        _queue = queue;
        _reader = reader;
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
        var result = _reader.Read(path, ref _data, this);
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

    public void OnEntry(in Win32FindData entry)
    {
        Counters.Entries++;
        var attributes = entry.FileAttributes;
        if ((attributes & Win32Find.DirectoryAttribute) != 0)
        {
            // A reparse-point directory (junction, directory symlink, mount point, ...) is never entered and contributes nothing.
            if ((attributes & Win32Find.ReparsePointAttribute) != 0)
            {
                Counters.ReparseSkipped++;
                return;
            }
            var name = Win32Find.NameString(in entry);
            var child = new DirNode(_walker.NextId(), _node.Id, name);
            Created.Add(child);
            _children.Add((child, DirTable.Combine(_path, name)));
            return;
        }

        var size = Win32Find.FileSize(in entry);
        Counters.Files++;
        Counters.Bytes += size;
        _ownSize += size;
        if (size <= 0) return;
        var forFiles = _files is not null && _files.WouldAccept(size);
        var forRoot = _node.Id == 0 && _rootFiles.WouldAccept(size);
        if (!forFiles && !forRoot) return;
        // The name is created only for a file that is actually kept.
        var hit = new FileHit(_node.Id, Win32Find.NameString(in entry), size);
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
    bool LargeFetch,
    ReadResult RootRead,
    string[] ErrorSamples);

sealed class Walker(FindFirstFn? findFirst = null)
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
        var reader = new DirectoryReader(findFirst);
        var all = new List<Worker>(workers);
        var threads = new List<Thread>(workers);
        for (var index = 0; index < workers; index++)
        {
            var worker = new Worker(this, queue, reader, top, collectFiles);
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
            reader.LargeFetch,
            RootRead,
            samples.ToArray());
    }
}
```

`src\DirSizer.Fs\FsScanner.cs`:

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

sealed record ScanSettings(int Workers, int Top, bool CollectFiles, bool ShowProgress, CancellationToken Cancel = default, FindFirstFn? FindFirst = null);

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
            walk = new Walker(settings.FindFirst).Run(root, rootPath.Extended, settings.Workers, settings.Top, settings.CollectFiles, settings.Cancel, progress);
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

- [ ] **Step 4: Build and run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test
```

Expected: `0 Warning(s)`, `0 Error(s)`; all twelve walk tests are `ok` and the last line is `25 self-tests passed, 0 skipped.` The wide fan-out test also prints two informational lines, `workers=1: peak_queued_dirs=10000, peak_working_set=<n> MiB` and the same for 8 workers (the prototype: 10000 and about 35-40 MiB in the JIT build). If a `walk skips a directory it may not read` line reads `skip`, your shell ignores the deny ACL (see Ground rules); the count is then `24 passed, 1 skipped`.

- [ ] **Step 5: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs
git commit -m @'
dirsizer.exe: parallel directory walk, scanner and phase accounting

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 5: Output

**Files:**
- Create: `src\DirSizer.Fs\SelfTests.Output.cs`
- Create: `src\DirSizer.Fs\FsOutput.cs`

- [ ] **Step 1: Write the tests first**

`src\DirSizer.Fs\SelfTests.Output.cs`:

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
        var metrics = new FsMetrics(zero, TimeSpan.FromSeconds(1), zero, zero, TimeSpan.FromSeconds(1), zero, zero, 4, true, 0, 30, 8, 1234, 0, 0);
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
        Assert(error.ToString().Contains("benchmark: workers=2"), "--benchmark prints the timings to stderr");
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
        foreach (var name in new[] { "open_ms", "walk_ms", "aggregation_ms", "finalize_ms", "other_ms", "total_ms", "phase_sum_ms", "enum_ms_total", "idle_ms_total", "workers", "large_fetch", "peak_queued_dirs", "managed_allocated_bytes", "peak_working_set_bytes", "entries_per_sec", "directories_per_sec", "logical_mib_per_sec" })
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

- [ ] **Step 2: Run the build to see it fail**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error" | Select-Object -First 3
```

Expected: FAIL to compile, `error CS0103` naming `FsOutput`.

- [ ] **Step 3: Write the output**

`src\DirSizer.Fs\FsOutput.cs`:

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
        $"benchmark: workers={m.Workers}, large_fetch={(m.LargeFetch ? "on" : "off")}, open_ms={m.Open.TotalMilliseconds:F1}, walk_ms={m.Walk.TotalMilliseconds:F1}, " +
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
                    m.EnumTotal.TotalMilliseconds, m.IdleTotal.TotalMilliseconds, m.Workers, m.LargeFetch, m.PeakQueuedDirs,
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
sealed record JsonFsPerformance(double OpenMs, double WalkMs, double AggregationMs, double FinalizeMs, double OtherMs, double TotalMs, double PhaseSumMs, double EnumMsTotal, double IdleMsTotal, int Workers, bool LargeFetch, int PeakQueuedDirs, long ManagedAllocatedBytes, long PeakWorkingSetBytes, double EntriesPerSec, double DirectoriesPerSec, double LogicalMibPerSec);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonFsOutput))]
partial class FsJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Build and run the tests**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 5
```

Expected: `0 Warning(s)`, `0 Error(s)`; the last lines show `ok    text output has the tables and the summary`, `ok    JSON output has the documented fields`, `ok    unreadable directories give a warning, the counters and the error samples` and `28 self-tests passed, 0 skipped.`

- [ ] **Step 5: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs
git commit -m @'
dirsizer.exe: text and JSON output

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 6: Entry point, exit codes, and a run on real trees

**Files:**
- Modify (replace): `src\DirSizer.Fs\Program.cs`

- [ ] **Step 1: Replace the temporary entry point**

`src\DirSizer.Fs\Program.cs`:

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

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
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
        cancel.Token);
    var result = FsScanner.Scan(options.Root, settings);
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

- [ ] **Step 2: Publish the NativeAOT build and run all tests in it**

```powershell
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) { $env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer" }
dotnet publish src\DirSizer.Fs\DirSizer.Fs.csproj -c Release -r win-x64 2>&1 | Select-Object -Last 3
$exe = (Resolve-Path ".\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe").Path
& $exe --self-test | Select-Object -Last 1; "exit: $LASTEXITCODE"
```

Expected: no publish errors, `28 self-tests passed, 0 skipped.` and `exit: 0`.

- [ ] **Step 3: Command-line behaviour**

```powershell
& $exe --help | Select-Object -First 3
& $exe C:\definitely-missing; "exit: $LASTEXITCODE"
& $exe C:\Windows --workers 0; "exit: $LASTEXITCODE"
& $exe C:\Windows --bogus; "exit: $LASTEXITCODE"
```

Expected: the help starts with `dirsizer - fast, read-only folder size scanner for any Windows filesystem`; the three failing calls print `error: Not a directory, or not found: C:\definitely-missing`, `error: --workers must be between 1 and 256.` and `error: Unknown option: --bogus`, each followed by `exit: 1`.

- [ ] **Step 4: A real tree, and the manifest**

```powershell
& $exe $PWD.Path --top=3 --files --benchmark; "exit: $LASTEXITCODE"
$text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($exe))
"asInvoker: " + $text.Contains('asInvoker'); "requireAdministrator: " + $text.Contains('requireAdministrator')
```

Expected: a table of the 3 largest directories (the first is the repository root) and files, a `Summary` with `directories_denied=0 directories_failed=0`, a `benchmark:` line on stderr in which `phase_sum_ms` equals `total_ms`, and `exit: 0`. The manifest check prints `asInvoker: True` and `requireAdministrator: False`. (The prototype scanned this repository in about 22 ms, 295 directories, 1,478 files.)

- [ ] **Step 5: A full volume, and `--strict`**

`C:\System Volume Information` cannot be read even by an administrator, so a full-volume scan has at least one denied directory: it shows `--strict` on a real case.

```powershell
& $exe C:\ --top=3 --files --strict --benchmark; "exit: $LASTEXITCODE"
```

Expected (takes tens of seconds): the tables, then a `Summary` with `directories_denied` of 1 or more (the prototype: 17) and `directories_failed=0`, the stderr line `warning: N directories could not be read (denied N, failed 0); the sizes are a lower bound.`, and `exit: 3`. The largest file is `C:\pagefile.sys` on most machines. Repeat without `--strict` and confirm `exit: 0`.

- [ ] **Step 6: Not testable from an elevated shell: run it un-elevated (manual, needs the user)**

Ask the user to open a normal (non-Administrator) terminal and run `artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer.exe --self-test` and `dirsizer.exe C:\Users --top=5`. Expected: no UAC prompt, the output appears in that terminal, `> result.json` works. Record the answer in Task 9's roadmap entry; if the user has not run it, the entry must say "not tested non-elevated". Do not claim it as verified.

- [ ] **Step 7: Commit**

```powershell
Normalize-FsProject
git add src\DirSizer.Fs
git commit -m @'
dirsizer.exe: entry point, exit codes, Ctrl+C

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 7: Mutation checks

The repo's convention is that a test that guards something important is shown to fail when the guarded code is deliberately broken. Do the following eight mutations, one at a time. Each: edit, build, run, confirm the named test fails, then restore with `git checkout`.

The restore command is always `git checkout -- <file>`. The files are committed, so this is safe; run `git status --short` after each restore and expect it to be empty.

| # | File | Change | Test that must FAIL |
| --- | --- | --- | --- |
| M1 | `src\DirSizer.Fs\DirModel.cs` | in `Aggregate`, change `nodes[node.ParentId].Total += node.Total;` to `nodes[node.ParentId].Total += node.OwnFileSize;` | `aggregation rolls sizes up to the root` and `walk matches the independent oracle...` |
| M2 | `src\DirSizer.Fs\DirModel.cs` | in `Aggregate`, change `node.ParentId >= id` to `node.ParentId > id` in the invariant check | `aggregation rejects a parent id that is not smaller` |
| M3 | `src\DirSizer.Fs\Walker.cs` | in `Worker.OnEntry`, change `if ((attributes & Win32Find.ReparsePointAttribute) != 0)` to `if ((attributes & Win32Find.ReparsePointAttribute) != 0 && false)` | `walk does not enter a junction, but enters one named as the root` |
| M4 | `src\DirSizer.Fs\DirectoryReader.cs` | change `if (first.Handle != Win32Find.InvalidHandle) _largeFetch = false;` to `_largeFetch = false;` | `LARGE_FETCH: a failing retry is an ordinary failure and keeps the flag` |
| M5 | `src\DirSizer.Fs\Walker.cs` | in `WorkQueue.Finish`, delete the line `_peakQueued = Math.Max(_peakQueued, _stack.Count);` | `walk finishes on a very wide fan-out and reports the queue peak` |
| M6 | `src\DirSizer.Fs\Walker.cs` | in `Worker.OnEntry`, change `Counters.Bytes += size;` to `Counters.Bytes += size + 1;` | `walk matches the independent oracle...` (the root total no longer equals the sum of file sizes) |
| M7 | `src\DirSizer.Fs\FsScanner.cs` | change `if (settings.Workers < 1) throw` to `if (false) throw` | `walk reports a bad root or bad settings as an error` (the internal "never read" check then fires instead of the argument error) |
| M8 | `src\DirSizer.Fs\Walker.cs` | in `Walker.Run`, in the `catch` block, delete the `queue.Cancel();` line and the `for (...) threads[index].Join();` line so that only `throw;` remains | `walk leaves no worker running when the caller fails` (this mutation leaves the workers walking in the background; the run still ends, because the workers are background threads) |

- [ ] **Step 1: For each mutation M1 to M8**

Apply the change with the Edit tool, then:

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-String "error|Build succeeded"
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-String "FAIL|passed|FAILED"
git checkout -- <the file you changed>
git status --short
```

Expected for each: `Build succeeded.`, at least the named test on a `FAIL` line, a final `FAILED` line, and an empty `git status` after the restore. (M3 may compile with an unreachable-code warning; that is fine.) If a mutation does **not** make its test fail, that test does not protect what it claims: report it, do not continue.

- [ ] **Step 2: Confirm the restore**

```powershell
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Warning(s)`, `0 Error(s)` and `28 self-tests passed, 0 skipped.` No commit is needed (nothing changed).

---

### Task 8: Real-volume comparison and the worker sweep

**Files:**
- Create: `scripts\Compare-Fs.ps1`

- [ ] **Step 1: Write the script**

`scripts\Compare-Fs.ps1`:

```powershell
<#
.SYNOPSIS
  Checks dirsizer.exe against an independent computation and against dirsizer-bulk, and measures worker counts.

.DESCRIPTION
  -Oracle  Sums the files under -Path with the framework's own directory enumeration (no code from dirsizer) and compares the
           root and every direct child directory with the --json output of dirsizer. Reparse-point directories are not entered,
           directories that cannot be read count as 0, like dirsizer. Slow (one thread): use a QUIESCENT tree of moderate size.
           Exit code 0 only if everything is equal.
  -Bulk    Runs dirsizer and dirsizer-bulk on -Path (a drive root, for example T:\) with a huge --top and lists every directory
           whose size differs and every directory only one tool reports. Differences are EXPECTED for hard links, NTFS metadata
           files and directories the caller cannot read; classify each one against docs\design_fs.md, section "Differences from
           the NTFS tools". dirsizer-bulk needs an elevated terminal. Not a pass/fail check.
  -Sweep   Runs dirsizer on -Path for each worker count in -Workers, -Runs times, alternating the counts, and prints the minimum,
           median and maximum of walk_ms. The default worker count of dirsizer is decided from this measurement.

  -Tool and -BulkTool are a .dll (run with dotnet) or an .exe.
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [switch]$Oracle,
    [switch]$Bulk,
    [switch]$Sweep,
    [int[]]$Workers = @(1, 2, 4, 8),
    [int]$Runs = 3,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll'),
    [string]$BulkTool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll')
)
$ErrorActionPreference = 'Stop'
if (-not ($Oracle -or $Bulk -or $Sweep)) { throw 'Choose at least one of -Oracle, -Bulk, -Sweep.' }

function Invoke-Tool([string]$Program, [string[]]$Arguments) {
    if ($Program -like '*.dll') { & dotnet $Program @Arguments } else { & $Program @Arguments }
}
function Get-Json([string]$Program, [string[]]$Arguments) {
    $text = Invoke-Tool $Program $Arguments 2>$null
    if ($LASTEXITCODE -notin 0, 3) { throw "$Program exited with code $LASTEXITCODE" }
    ($text -join "`n") | ConvertFrom-Json
}

$failures = 0

if ($Oracle) {
    function Get-DirectoryTotal([IO.DirectoryInfo]$Directory) {
        $sum = 0L
        try {
            foreach ($file in $Directory.EnumerateFiles()) { $sum += $file.Length }
            foreach ($child in $Directory.EnumerateDirectories()) {
                if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
                $sum += Get-DirectoryTotal $child
            }
        } catch [UnauthorizedAccessException] { }
        $sum
    }
    "dirsizer against the framework's directory enumeration on $Path"
    $json = Get-Json $Tool @($Path, '--json', '--top=1000000')
    $rootDirectory = [IO.DirectoryInfo]::new($json.root.path)
    $expectedRoot = Get-DirectoryTotal $rootDirectory
    if ($expectedRoot -eq $json.root.size) { "  EQUAL      root  $($json.root.size)" }
    else { "  DIFFERENT  root  dirsizer=$($json.root.size) oracle=$expectedRoot"; $failures++ }
    $byPath = @{}
    foreach ($item in $json.root_children) { $byPath[$item.path] = $item.size }
    foreach ($child in $rootDirectory.EnumerateDirectories()) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $expected = Get-DirectoryTotal $child
        if (-not $byPath.ContainsKey($child.FullName)) { "  MISSING    $($child.FullName) is not among the root's children"; $failures++ }
        elseif ($byPath[$child.FullName] -eq $expected) { "  EQUAL      $($child.FullName)  $expected" }
        else { "  DIFFERENT  $($child.FullName)  dirsizer=$($byPath[$child.FullName]) oracle=$expected"; $failures++ }
    }
}

if ($Bulk) {
    "dirsizer against dirsizer-bulk on $Path (differences are expected; see docs\design_fs.md)"
    $volume = $Path.TrimEnd('\')
    $mine = Get-Json $Tool @($Path, '--json', '--top=10000000')
    $theirs = Get-Json $BulkTool @($volume, '--json', '--top=10000000')
    $mineBySize = @{}; foreach ($item in $mine.directories) { $mineBySize[$item.path] = $item.size }
    $theirsBySize = @{}; foreach ($item in $theirs.directories) { $theirsBySize[$item.path] = $item.size }
    $different = @(); $onlyMine = @(); $onlyTheirs = @(); $metadata = 0
    foreach ($key in $mineBySize.Keys) {
        if (-not $theirsBySize.ContainsKey($key)) { $onlyMine += $key }
        elseif ($theirsBySize[$key] -ne $mineBySize[$key]) { $different += "{0}  dirsizer={1:N0} bulk={2:N0}" -f $key, $mineBySize[$key], $theirsBySize[$key] }
    }
    foreach ($key in $theirsBySize.Keys) {
        if ($mineBySize.ContainsKey($key)) { continue }
        # NTFS system files and folders: listed under [NTFS metadata], or as a first path component that starts with '$' ($Extend).
        if ($key -like '*[[]NTFS metadata]*' -or $key -match '^.:\\\$') { $metadata++ } else { $onlyTheirs += $key }
    }
    "  directories: dirsizer $($mineBySize.Count), bulk $($theirsBySize.Count)"
    "  root: dirsizer {0:N0}, bulk {1:N0}" -f $mine.root.size, $theirs.root.size
    "  same path, different size: $($different.Count)"; $different | Select-Object -First 30 | ForEach-Object { "    $_" }
    "  only in dirsizer: $($onlyMine.Count)"; $onlyMine | Select-Object -First 30 | ForEach-Object { "    $_" }
    "  only in bulk (not NTFS metadata): $($onlyTheirs.Count)"; $onlyTheirs | Select-Object -First 30 | ForEach-Object { "    $_" }
    "  only in bulk, NTFS metadata: $metadata"
    "  dirsizer counters: denied=$($mine.statistics.directories_denied) failed=$($mine.statistics.directories_failed) reparse_skipped=$($mine.statistics.reparse_skipped)"
}

if ($Sweep) {
    "dirsizer worker sweep on $Path ($Runs runs per count, alternating)"
    $times = @{}; foreach ($w in $Workers) { $times[$w] = @() }
    for ($run = 1; $run -le $Runs; $run++) {
        foreach ($w in $Workers) {
            $json = Get-Json $Tool @($Path, '--json', '--top=1', '--workers', "$w")
            $times[$w] += [double]$json.statistics.performance.walk_ms
        }
    }
    foreach ($w in $Workers) {
        $sorted = @($times[$w] | Sort-Object)
        "  workers={0,-3} walk_ms  min={1,10:N1}  median={2,10:N1}  max={3,10:N1}" -f $w, $sorted[0], $sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)], $sorted[-1]
    }
}

if ($failures -gt 0) { "$failures difference(s)"; exit 1 }
exit 0
```

- [ ] **Step 2: Compare with an independent computation on a quiescent tree**

```powershell
.\scripts\Compare-Fs.ps1 -Path "$PWD\src" -Oracle; "exit: $LASTEXITCODE"
```

Expected: `EQUAL` for `root` and for every direct child directory of `src`, and `exit: 0`. (Use a tree nothing is writing to.) Any `DIFFERENT` is a bug in the tool or in the script: investigate before going on.

- [ ] **Step 3: Compare with `dirsizer-bulk` on the `NTFSTEST` fixture volume**

The volume `T:` (label `NTFSTEST`, created by `scripts\New-AbFixture.ps1`) must exist and the shell must be elevated (`dirsizer-bulk` reads raw NTFS data). Build the bulk tool if needed (`dotnet build src\DirSizer.Bulk\DirSizer.Bulk.csproj -c Release`).

```powershell
.\scripts\Compare-Fs.ps1 -Path T:\ -Bulk
```

Every line listed under "same path, different size" or "only in ..." must be explainable by the table in `docs\design_fs.md`, "Differences from the NTFS tools": hard links, NTFS metadata, a directory the caller cannot read, a reparse-point directory. The prototype run on 2026-09-21 (the fixture may have changed since; compare by kind, not by number) reported:

```
  directories: dirsizer 27, bulk 33
  same path, different size: 9
    T:\ab-fixture\links  dirsizer=6,000 bulk=2,000                      hard links
    T:\ab-fixture\manylinks  dirsizer=453,000 bulk=3,000                hard links
    T:\ab-fixture\links\d1, ...\d2  dirsizer=2,000 bulk=0               hard link names bulk did not select
    T:\DirSizer-P1-...  and  ...\C  dirsizer is 502 bytes larger        one hard link (A\file1.txt = C\file1-link.txt)
    T:\ab-fixture, T:\  dirsizer differs                                sums of the above, plus NTFS metadata at the root
    T:\System Volume Information  dirsizer=0 bulk=12                    denied
  only in dirsizer: 0
  only in bulk (not NTFS metadata): 1        T:\ab-fixture\reparse\junction   (reparse directory, listed by bulk only)
  only in bulk, NTFS metadata: 5
  dirsizer counters: denied=1 failed=0 reparse_skipped=1
```

A difference that fits none of the four kinds is a finding: report it, do not explain it away.

- [ ] **Step 4: Worker sweep on a real volume**

```powershell
.\scripts\Compare-Fs.ps1 -Path C:\ -Sweep -Runs 3 -Workers 1,2,4,8
```

It runs 12 scans of `C:\` (about 20-40 s each on the prototype machine), so it takes several minutes. Expected: a `min / median / max` line of `walk_ms` per worker count. The prototype (4 logical processors, warm cache, `C:` with about 228,000 directories and 785,000 files): 1 worker about 42 s, 4 workers about 19 s (2.2x), 8 workers about 18 s, 16 workers about 19 s. The workers spent almost all of their time inside the enumeration calls (`idle_ms_total` was under 0.1 s at 4 workers), so the shared lock was not the limit.

Decision rule for the default (`FsOptions.DefaultWorkers`, now `min(ProcessorCount, 8)`): keep it unless the median of some other count is more than 10 % faster than the median at the default count on this machine. If it is, change `DefaultWorkers` and the `--workers` help text follows automatically (it prints the property), and note the reason in the roadmap. This is measured on one machine; say so.

- [ ] **Step 5: Commit**

```powershell
ConvertTo-Crlf scripts\Compare-Fs.ps1
git add scripts\Compare-Fs.ps1
git commit -m @'
Add scripts\Compare-Fs.ps1: oracle and dirsizer-bulk comparison, worker sweep

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

(If Step 4 changed `DirSizer.Fs\FsOptions.cs`, add it and rerun the self-test first.)

---

### Task 9: Packaging and documentation

**Files:**
- Modify: `scripts\release.ps1`, `README.md`, `README-jp.md`, `docs\roadmap.md`, `docs\design.md`, `docs\design_mft.md`, `docs\design_fs.md`

All edits below are exact: use the Edit tool with the `old` text as the match. Each `old` text occurs once in its file at the time of the edit.

- [ ] **Step 1: Release script publishes four tools**

`scripts\release.ps1`, description block. Old:

```
    Without -Publish, publishes the NativeAOT win-x64 executable and creates:

      dist\DirSizer-v<version>-win-x64.zip

    The ZIP contains DirSizer.exe and README.md. With -Publish, the script also
```

New:

```
    Without -Publish, publishes the four NativeAOT win-x64 executables and creates:

      dist\DirSizer-v<version>-win-x64.zip

    The ZIP contains dirsizer.exe, dirsizer-fsctl.exe, dirsizer-bulk.exe,
    dirsizer-inspect.exe, README.md, README-jp.md and LICENSE. With -Publish, the script also
```

Tool list. Old:

```
# The release contains three tools. Each is published as its own NativeAOT executable.
$Tools = @(
    @{ Project = 'src\DirSizer.Fsctl\DirSizer.Fsctl.csproj';     Exe = 'dirsizer-fsctl.exe' },
```

New:

```
# The release contains four tools. Each is published as its own NativeAOT executable.
$Tools = @(
    @{ Project = 'src\DirSizer.Fs\DirSizer.Fs.csproj';           Exe = 'dirsizer.exe' },
    @{ Project = 'src\DirSizer.Fsctl\DirSizer.Fsctl.csproj';     Exe = 'dirsizer-fsctl.exe' },
```

- [ ] **Step 2: README.md, status and tool table**

Old:

```
This is the first practical baseline. It targets NTFS volumes and reads the master file table (MFT) directly. It does not use `FindFirstFile`, `Directory.EnumerateFiles`, the USN journal, or path traversal.
```

New:

```
This is the first practical baseline. The three NTFS tools (`dirsizer-bulk`, `dirsizer-fsctl`, `dirsizer-inspect`) read the master file table (MFT) directly; they do not use `FindFirstFile`, `Directory.EnumerateFiles`, the USN journal, or path traversal. `dirsizer.exe` is the general-purpose counterpart: it walks the directory tree of any filesystem with `FindFirstFileExW` and needs no elevation.
```

Old:

```
DirSizer is three separate executables:

| Tool | What it is for | Notes |
| --- | --- | --- |
```

New:

```
DirSizer is four separate executables:

| Tool | What it is for | Notes |
| --- | --- | --- |
| `dirsizer.exe` | Folder-size scan of **any** filesystem, without elevation | Reads directory listings with `FindFirstFileExW` on several threads and never opens individual files. Slower than `dirsizer-bulk` on NTFS, and its totals differ from the NTFS tools by design; see [dirsizer.exe](#dirsizerexe-any-filesystem-no-elevation) below and [docs/design_fs.md](docs/design_fs.md). |
```

Old:

```
`dirsizer-bulk` and `dirsizer-fsctl` take the same options and print the same results; you choose the one you want, and neither ever falls back to the other. Run any tool with `--help` (`dirsizer-inspect --help` is the most detailed).
```

New:

```
`dirsizer-bulk` and `dirsizer-fsctl` take the same options and print the same results; you choose the one you want, and neither ever falls back to the other. `dirsizer.exe` has its own options (`--workers`, `--strict`). Run any tool with `--help` (`dirsizer-inspect --help` is the most detailed).
```

- [ ] **Step 3: README.md, elevation, build, scripts, self-tests, layout, releases, usage**

Old (Elevation, first sentence only):

```
All three tools read raw NTFS metadata, so each executable carries a manifest that requires Administrator (`requireAdministrator`)
```

New:

```
The three NTFS tools read raw NTFS metadata, so each of those executables carries a manifest that requires Administrator (`requireAdministrator`)
```

Old (end of the Elevation section):

```
The manifest is the single file `app.manifest`; changing `level` there to `asInvoker` removes the requirement for all three.
```

New:

```
The manifest is the single file `src\Shared\app.manifest`; changing `level` there to `asInvoker` removes the requirement for all three. `dirsizer.exe` needs none of this: it has its own manifest with `asInvoker`, runs in any terminal, and `> file` and pipes work.
```

Old (build table):

```
| `src\DirSizer.Inspect\DirSizer.Inspect.csproj` | `dirsizer-inspect` |
```

New:

```
| `src\DirSizer.Inspect\DirSizer.Inspect.csproj` | `dirsizer-inspect` |
| `src\DirSizer.Fs\DirSizer.Fs.csproj` | `dirsizer` |
```

Old (scripts block):

```
.\scripts\Test-BulkInstability.ps1 -Volume T:                         # grows the MFT of a disposable NTFSTEST volume; see the script header
```

New:

```
.\scripts\Test-BulkInstability.ps1 -Volume T:                         # grows the MFT of a disposable NTFSTEST volume; see the script header
.\scripts\Compare-Fs.ps1 -Path .\src -Oracle                          # dirsizer.exe vs an independent sum, on a quiescent tree
.\scripts\Compare-Fs.ps1 -Path T:\ -Bulk                              # dirsizer.exe vs dirsizer-bulk: lists the expected differences (docs\design_fs.md)
.\scripts\Compare-Fs.ps1 -Path C:\ -Sweep -Runs 3                     # walk time for 1, 2, 4 and 8 workers
```

Old (self-tests):

```
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test
```

New:

```
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test
dotnet .\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test       # builds a fixture in %TEMP%; needs no elevation
```

Old (layout):

```
src\DirSizer.Inspect\        dirsizer-inspect
```

New:

```
src\DirSizer.Inspect\        dirsizer-inspect
src\DirSizer.Fs\             dirsizer.exe (directory enumeration, any filesystem, no elevation; independent of Core)
```

Old (releases; the sentence wraps across two lines):

```
containing the three NativeAOT
executables (dirsizer-bulk.exe, dirsizer-fsctl.exe, dirsizer-inspect.exe), this README, and the license.
```

New:

```
containing the four NativeAOT
executables (dirsizer.exe, dirsizer-bulk.exe, dirsizer-fsctl.exe, dirsizer-inspect.exe), this README, and the license.
```

Old (usage intro):

```
Run from an Administrator terminal (see "Elevation"). `dirsizer-bulk` and `dirsizer-fsctl` share these options; `dirsizer-inspect` has its own (`dirsizer-inspect --help`):
```

New:

```
Run the NTFS tools from an Administrator terminal (see "Elevation"). `dirsizer-bulk` and `dirsizer-fsctl` share these options; `dirsizer-inspect` has its own (`dirsizer-inspect --help`); `dirsizer.exe` is described in its own section below:
```

- [ ] **Step 4: README.md, the new section**

Insert this section immediately before the heading `## dirsizer-bulk (experimental)`:

````markdown
## dirsizer.exe (any filesystem, no elevation)

`dirsizer.exe` measures folder sizes on **any** Windows filesystem (NTFS, ReFS, exFAT, FAT, network shares) by reading directory listings with `FindFirstFileExW`. It never opens individual files, reads directories on several threads at once, and runs without elevation: a directory you cannot read is skipped and counted.

```powershell
.\dirsizer.exe C:\Users
.\dirsizer.exe \\server\share --top=50 --files
.\dirsizer.exe D:\ --json > result.json
.\dirsizer.exe C:\ --strict --workers 8 --benchmark
```

The options are those of the NTFS tools plus `--workers N` (default: the number of processors, at most 8) and `--strict` (exit code 3 if a directory could not be read; the result is still written). The path may be any directory, not only a drive root. Exit codes: 0 result written; 1 error; 3 with `--strict`, at least one directory could not be read. Progress is one updating line on stderr (`Scanning: N directories, M files`), shown only when stderr is a terminal. The JSON output is ASCII only (non-ASCII characters in paths are escaped), so it is exact under any console code page; the table output follows the console code page, so use `--json` when paths must be exact. The JSON has the same top-level layout as the NTFS tools' but different `statistics` and `performance` keys; see [docs/design_fs.md](docs/design_fs.md).

Its results are **not identical** to those of the NTFS tools, by design (details and measured differences: [docs/design_fs.md](docs/design_fs.md)):

| Case | `dirsizer.exe` | NTFS tools |
| --- | --- | --- |
| Hard-linked file | counted in every directory that holds a name | counted once |
| NTFS metadata (`$MFT`, `$Bitmap`, ...) | not visible, not counted | counted |
| Directory you cannot read | skipped, counted in `directories_denied` | counted |
| Junction, symbolic link or mount point directory | not entered, counted in `reparse_skipped` | listed as an empty directory |

Sizes are logical (the size the directory listing reports); alternate data streams are not included. On NTFS `dirsizer.exe` is expected to be slower than `dirsizer-bulk`: use the NTFS tools when you want speed and can run elevated. Only local NTFS has been verified; other filesystems and network shares are supported by design and unverified until they are run.

````

- [ ] **Step 5: README-jp.md**

Old (status paragraph):

```
現在、初期の実用版（ベースライン）です。NTFS ボリュームの MFT を直接読み込む設計になっており、`FindFirstFile` や `Directory.EnumerateFiles` によるパス走査、および USN ジャーナルは使用していません。
```

New:

```
現在、初期の実用版（ベースライン）です。NTFS 向けの 3 つのツール（`dirsizer-bulk`、`dirsizer-fsctl`、`dirsizer-inspect`）は NTFS ボリュームの MFT を直接読み込む設計になっており、`FindFirstFile` や `Directory.EnumerateFiles` によるパス走査、および USN ジャーナルは使用していません。`dirsizer.exe` はその汎用版で、`FindFirstFileExW` で任意のファイルシステムのディレクトリを走査し、管理者権限を必要としません。
```

Old:

```
DirSizer は、用途に応じた 3 つの独立した実行ファイルで構成されています。

| ツール | 用途 | 備考 |
| --- | --- | --- |
```

New:

```
DirSizer は、用途に応じた 4 つの独立した実行ファイルで構成されています。

| ツール | 用途 | 備考 |
| --- | --- | --- |
| `dirsizer.exe` | **任意の**ファイルシステムのフォルダーサイズスキャン（管理者権限不要） | `FindFirstFileExW` で複数スレッドを使ってディレクトリ一覧を読み取り、個々のファイルは開きません。NTFS では `dirsizer-bulk` より遅く、合計値も NTFS 向けツールとは仕様上異なります。下の「dirsizer.exe」節と [docs/design_fs.md](docs/design_fs.md)（英語）を参照してください。 |
```

Old:

```
`dirsizer-bulk` と `dirsizer-fsctl` は同じコマンドラインオプションを受け付け、同一の結果を出力します。
```

New:

```
`dirsizer-bulk` と `dirsizer-fsctl` は同じコマンドラインオプションを受け付け、同一の結果を出力します。`dirsizer.exe` は独自のオプション（`--workers`、`--strict`）を持ちます。
```

Old:

```
全ツール共通で NTFS のローメタデータへアクセスするため、実行ファイルには管理者権限を要求するマニフェスト（`requireAdministrator`）が埋め込まれています。
```

New:

```
NTFS 向けの 3 つのツールは NTFS のローメタデータへアクセスするため、実行ファイルには管理者権限を要求するマニフェスト（`requireAdministrator`）が埋め込まれています。`dirsizer.exe` は専用のマニフェスト（`asInvoker`）を持ち、管理者権限は不要で、通常のターミナルから実行でき、`> file` やパイプも使えます。
```

Old (build table):

```
| `src\DirSizer.Inspect\DirSizer.Inspect.csproj` | `dirsizer-inspect.exe` |
```

New:

```
| `src\DirSizer.Inspect\DirSizer.Inspect.csproj` | `dirsizer-inspect.exe` |
| `src\DirSizer.Fs\DirSizer.Fs.csproj` | `dirsizer.exe` |
```

Old (scripts block):

```
.\scripts\Test-BulkInstability.ps1 -Volume T:  # テスト用ボリュームの MFT を拡張させて挙動を検証（詳細はスクリプトヘッダー参照）
```

New:

```
.\scripts\Test-BulkInstability.ps1 -Volume T:  # テスト用ボリュームの MFT を拡張させて挙動を検証（詳細はスクリプトヘッダー参照）
.\scripts\Compare-Fs.ps1 -Path .\src -Oracle   # dirsizer.exe と独立した集計の比較（更新のないツリーで実行）
.\scripts\Compare-Fs.ps1 -Path T:\ -Bulk       # dirsizer.exe と dirsizer-bulk の比較（想定される差異を一覧表示。docs\design_fs.md 参照）
.\scripts\Compare-Fs.ps1 -Path C:\ -Sweep -Runs 3  # ワーカー数 1/2/4/8 での走査時間
```

Old (self-tests):

```
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test
```

New:

```
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test
dotnet .\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test       # %TEMP% にテスト用ツリーを作成。管理者権限は不要
```

Old (layout):

```
src\DirSizer.Inspect\        dirsizer-inspect
```

New:

```
src\DirSizer.Inspect\        dirsizer-inspect
src\DirSizer.Fs\             dirsizer.exe（ディレクトリ列挙、任意のファイルシステム、管理者権限不要。Core には依存しない）
```

Old (releases):

```
3 つの NativeAOT 実行ファイル、README、ライセンスが同梱されます。
```

New:

```
4 つの NativeAOT 実行ファイル（dirsizer.exe、dirsizer-bulk.exe、dirsizer-fsctl.exe、dirsizer-inspect.exe）、README、ライセンスが同梱されます。
```

Old (usage intro):

```
管理者権限のターミナルから実行してください（詳細は「管理者権限（昇格）について」を参照）。`dirsizer-bulk` と `dirsizer-fsctl` は同じオプションを共有しています。
```

New:

```
NTFS 向けツールは管理者権限のターミナルから実行してください（詳細は「管理者権限（昇格）について」を参照）。`dirsizer-bulk` と `dirsizer-fsctl` は同じオプションを共有しています。`dirsizer.exe` は下の専用の節を参照してください。
```

Insert this section immediately before the heading `## dirsizer-bulk（実験的実装）`:

````markdown
## dirsizer.exe（任意のファイルシステム、管理者権限不要）

`dirsizer.exe` は、`FindFirstFileExW` でディレクトリ一覧を読み取り、**任意の** Windows ファイルシステム（NTFS、ReFS、exFAT、FAT、ネットワーク共有）でフォルダーサイズを計算します。個々のファイルは開かず、複数のスレッドでディレクトリを並列に読み取り、管理者権限なしで動作します。読み取れないディレクトリはスキップされ、件数として報告されます。

```powershell
.\dirsizer.exe C:\Users
.\dirsizer.exe \\server\share --top=50 --files
.\dirsizer.exe D:\ --json > result.json
.\dirsizer.exe C:\ --strict --workers 8 --benchmark
```

オプションは NTFS 向けツールと同じもの（`--top`、`--files`、`--dirs`、`--json`、`--benchmark`、`--self-test`）に加え、`--workers N`（既定はプロセッサ数、最大 8）と `--strict`（読み取れないディレクトリがあれば終了コード 3。結果は出力されます）があります。パスはドライブのルートに限らず、任意のディレクトリを指定できます。終了コード: 0 = 結果を出力、1 = エラー、3 = `--strict` 指定時に読み取れないディレクトリがあった。進捗は stderr に 1 行で更新表示され（`Scanning: N directories, M files`）、stderr がターミナルのときだけ表示されます。JSON 出力は ASCII のみ（パス中の非 ASCII 文字はエスケープされます）なので、コンソールのコードページに関係なく正確です。表形式の出力はコンソールのコードページに従うため、パスを正確に得たいときは `--json` を使ってください。JSON の最上位の構成は NTFS 向けツールと同じですが、`statistics` と `performance` のキーは異なります（[docs/design_fs.md](docs/design_fs.md)（英語）を参照）。

結果は NTFS 向けツールと**同一にはなりません**（仕様です。詳細と実測した差異は [docs/design_fs.md](docs/design_fs.md)（英語）を参照）:

| ケース | `dirsizer.exe` | NTFS 向けツール |
| --- | --- | --- |
| ハードリンクされたファイル | 名前を持つすべてのディレクトリで加算 | 1 回だけ加算 |
| NTFS メタデータ（`$MFT`、`$Bitmap` など） | 列挙されず、加算されない | 加算される |
| 読み取り権限のないディレクトリ | スキップし `directories_denied` に計上 | 加算される |
| ジャンクション、シンボリックリンク、マウントポイントのディレクトリ | 辿らず `reparse_skipped` に計上 | 空のディレクトリとして一覧に出る |

サイズは論理サイズ（ディレクトリ一覧が報告するサイズ）で、代替データストリームは含みません。NTFS では `dirsizer.exe` は `dirsizer-bulk` より遅い想定です。速度が必要で管理者権限で実行できる場合は NTFS 向けツールを使ってください。検証済みなのはローカルの NTFS のみです。他のファイルシステムやネットワーク共有は設計上は対応していますが、実行して確認するまでは未検証です。

````

- [ ] **Step 6: docs\design.md, design_mft.md**

`docs\design.md`. Old:

```
# C# NTFS Folder Size Scanner Spec

## Goal
```

New:

```
# C# NTFS Folder Size Scanner Spec

> This document specifies the NTFS tools (`dirsizer-bulk`, `dirsizer-fsctl`, `dirsizer-inspect`). The general-purpose `dirsizer.exe` (directory enumeration, any filesystem, no elevation) is specified in [design_fs.md](design_fs.md). Statements below such as "no recursive fallback scanning", "NTFS only" and "administrative privileges required" concern the NTFS tools only.

## Goal
```

`docs\design_mft.md`. Old:

```
The product is three executables that share `DirSizer.Core`. This replaced an earlier layout with one executable and a `--reader=fsctl|bulk` option; the decision and its reasons:
```

New:

```
The product is three executables that share `DirSizer.Core`. (A fourth, independent tool, `dirsizer.exe`, was added later for any filesystem; it shares no code with `DirSizer.Core` and is specified in [design_fs.md](design_fs.md).) This replaced an earlier layout with one executable and a `--reader=fsctl|bulk` option; the decision and its reasons:
```

Old:

```
- A combined `dirsizer.exe` that tries bulk and falls back to fsctl was considered and not built. It can be added later without changing the three tools.
```

New:

```
- A combined `dirsizer.exe` that tries bulk and falls back to fsctl was considered and dropped. The name `dirsizer.exe` now belongs to the separate any-filesystem tool described in [design_fs.md](design_fs.md).
```

- [ ] **Step 7: docs\roadmap.md**

Old:

```
- [ ] Optional, only if needed: a combined `dirsizer.exe` that tries bulk and falls back to fsctl. Not built.
```

New:

```
- [x] Dropped: a combined `dirsizer.exe` that tries bulk and falls back to fsctl. The name now belongs to the any-filesystem tool of P5.
```

Insert this section immediately before the heading `## Promotion criteria: bulk from experimental to default candidate`. **Use the numbers your own runs printed** in the places marked by the examples; keep a claim only if you verified it, and delete or reword the ones you did not (for the non-elevated run see Task 6 Step 6).

```markdown
## P5 - dirsizer.exe: any filesystem, no elevation

Specified in [design_fs.md](design_fs.md). Independent of `DirSizer.Core`; none of the three NTFS tools was changed.

- [x] `src\DirSizer.Fs` (`dirsizer.exe`): `FindFirstFileExW` with `FindExInfoBasic` and `LARGE_FETCH`, no per-file open, extended-length paths, reparse-point directories not entered, access denied skipped and counted, `--strict` exit 3, `asInvoker` manifest.
- [x] Parallel walk: dedicated threads, one shared LIFO stack (intentionally unbounded; peak queue measured), `pending` counter for termination, directory ids allocated at discovery so `ParentId < Id`, aggregation as one reverse loop.
- [x] `--self-test` (28 tests, JIT and NativeAOT builds): independent-oracle comparison for 1, 3 and 8 workers (nested and empty directories, zero-byte file, Unicode names, a path over 260 characters), junction, hard link, deny ACL, an 800-deep chain, a 10,000-wide fan-out (peak queued directories 10,000), the LARGE_FETCH fallback, the classification of find-first errors (missing, empty, denied, other), a failing directory in the middle of a walk, cancellation before and during a walk, a throwing worker, a failing caller (no worker left running), rejected settings, output and JSON.
- [x] Mutation checks (eight deliberate breakages, each made its named test fail).
- [x] `scripts\Compare-Fs.ps1 -Oracle` on `src\`: root and every direct child equal to the framework's enumeration.
- [x] `scripts\Compare-Fs.ps1 -Bulk` on the `T:` fixture against `dirsizer-bulk`: every difference explained by hard links, NTFS metadata, a directory that cannot be read, or a reparse-point directory that bulk lists (see design_fs.md); nothing unexplained.
- [x] Worker sweep, `C:` (4 logical processors, warm cache, about 228,000 directories and 785,000 files): 1 worker 42 s, 4 workers 19 s (2.2x), 8 workers 18 s, 16 workers 19 s. Workers spent nearly all their time inside the enumeration calls; the shared lock was not the limit. Default `min(ProcessorCount, 8)` kept. One machine, one volume.
- [ ] Not measured: cold file cache, a second machine, a network share, exFAT/FAT, ReFS. Run before claiming anything for those.
- [ ] Not tested by the agent: starting `dirsizer.exe` from a non-elevated terminal (expected: no UAC prompt, output and `> file` work). Needs the user.
- [ ] P2 (later): benchmark `FindFirstFileExW` against `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)` and `NtQueryDirectoryFileEx` on the same fixtures; adopt only what measures faster. On the prototype machine `dirsizer.exe` reached about 53,000 entries per second on `C:`, far below what the NTFS tools reach per record, so the enumeration API is the place to look.

```

- [ ] **Step 8: docs\design_fs.md, a status line**

`docs\design_fs.md`. Old:

```
## Deliverables of this work
```

New:

```
## Status

Implemented as `src\DirSizer.Fs`; see P5 in [roadmap.md](roadmap.md) for what was verified and what was not.

## Deliverables of this work
```

- [ ] **Step 9: Commit**

```powershell
git add scripts\release.ps1 README.md README-jp.md docs
git commit -m @'
Package dirsizer.exe with the release and document it

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 10: Final verification

Everything the plan claims must be shown here with output, not inferred.

- [ ] **Step 1: The existing tools are untouched and still pass**

```powershell
git diff --stat master -- src/DirSizer.Core src/DirSizer.Bulk src/DirSizer.Fsctl src/DirSizer.Inspect src/DirSizer.Compare src/Shared
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 4
dotnet .\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test; "fsctl exit: $LASTEXITCODE"
dotnet .\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll --self-test; "bulk exit: $LASTEXITCODE"
dotnet .\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test; "inspect exit: $LASTEXITCODE"
dotnet .\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: the `git diff --stat` prints **nothing**; the solution builds with `0 Error(s)`; the three existing self-tests give the same result as in Task 0 Step 2; the last line is `28 self-tests passed, 0 skipped.` Skipped must be 0 here: run from an unrestricted PowerShell.

- [ ] **Step 2: The release package**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1
```

Expected (several minutes: four NativeAOT publishes): it ends with `Packaged release. Not published (no -Publish).` Then check the package:

```powershell
$zip = Get-ChildItem dist\DirSizer-v*-win-x64.zip | Sort-Object LastWriteTime | Select-Object -Last 1
$out = Join-Path $env:TEMP "dirsizer-package-check"
if (Test-Path $out) { [IO.Directory]::Delete($out, $true) }
Expand-Archive $zip.FullName $out
Get-ChildItem $out | Select-Object Name, Length
foreach ($name in 'dirsizer.exe', 'dirsizer-bulk.exe', 'dirsizer-fsctl.exe', 'dirsizer-inspect.exe') {
    $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $out $name)))
    "{0,-22} asInvoker={1,-5} requireAdministrator={2}" -f $name, $text.Contains('asInvoker'), $text.Contains('requireAdministrator')
}
& (Join-Path $out 'dirsizer.exe') --self-test | Select-Object -Last 1
```

Expected: four executables plus `README.md`, `README-jp.md`, `LICENSE`; `dirsizer.exe` shows `asInvoker=True requireAdministrator=False`, and each of the three NTFS tools shows `asInvoker=False requireAdministrator=True` (this is also the positive control for the check); the last line is `28 self-tests passed, 0 skipped.` Remove the temporary folder afterwards: `[IO.Directory]::Delete($out, $true)`.

- [ ] **Step 3: Report**

Give the user: the branch name and commits (`git log --oneline master..HEAD`), the outputs of Steps 1 and 2, the worker-sweep table, the differences listed by `-Bulk`, and the two things that were **not** verified: the non-elevated start (Task 6 Step 6) unless the user did it, and everything under "Not measured" in the roadmap. Ask whether to merge or open a PR (`superpowers:finishing-a-development-branch`). Do not merge or push without being asked.

---

## Self-review against the spec

Checked when the plan was written; each spec section and where the plan covers it:

| Spec section | Covered by |
| --- | --- |
| Goal, roles of the four tools | Task 9 (README tables), `design_fs.md` status |
| Scope (no elevation, any directory as root, JSON) | Tasks 3 (root path), 6 (entry point), 9 |
| Measurement semantics: logical size, directory-entry accounting, reparse, ADS | Task 4 tests (oracle, junction, hard link), Task 8 (`-Bulk`) |
| Enumeration: `FindFirstFileExW` flags, no per-file open, extended paths, single delegate seam, InlineArray without unsafe | Task 1 (reader, canary), Task 4 tests |
| LARGE_FETCH fallback, deterministic test | Task 1 tests (reader level), Task 4 test (1 and 8 workers), Task 7 M4 |
| Parallel walk: dedicated threads, unbounded LIFO stack, `pending`, cancellation, exception in a worker | Task 4 (`Walker.cs`, tests for cancel and throwing worker), Task 7 M5 |
| Worker default measured, not assumed | Task 8 Step 4 |
| Model: ids, `ParentId < Id`, reverse aggregation, no stored files, bounded top-N, root children bounded | Tasks 2 and 4 (`DirModel.cs`, `FsScanner.cs`), tests, Task 7 M1, M2 |
| Errors and exit codes (0 / 1 / 3, denied vs failed, samples) | Tasks 4, 5, 6 (steps 3 and 5) |
| CLI, `asInvoker` manifest, no `ConsolePause` | Tasks 1, 3, 6, 10 (manifest check with a positive control) |
| Output: text summary, JSON, progress | Task 5, Task 6 |
| Performance measurement: phases, `phase_sum == total`, worker-time diagnostics, `peak_queued_dirs` | Tasks 4, 5 (tests assert them), Task 6 Step 4 |
| Testing: oracle, junction, denied, aggregation, termination and queue size, LARGE_FETCH | Task 4 (all), Task 1 (LARGE_FETCH) |
| Real volumes: oracle, `dirsizer-bulk`, worker sweep | Task 8 |
| Differences table | Task 9 (README), Task 8 Step 3 checks it on real data |
| Deliverables 1-5 (project, tests, release script, docs, no version bump) | Tasks 1-9; no version bump anywhere in this plan |
| Later phase (P2) | roadmap entry, Task 9 Step 7; explicitly not implemented |
