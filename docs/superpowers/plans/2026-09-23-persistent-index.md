# dirsizer-index: persistent, incremental NTFS index (roadmap I2-I5) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `dirsizer-index.exe`, an experimental tool. It saves the merged `$MFT` records of a volume (I2) and
brings them up to date from the USN change journal by reading again only the records the journal names (I3). It
answers size queries for any directory from that one index (I4), and it can list what shrank and grew since the
previous run (I5).

**Architecture:** This is a new project, `src\DirSizer.Index`. It reuses the existing pieces unchanged, compiled in the
same way `src\DirSizer\DirSizer.csproj` does it:
- the bulk MFT scan (`BulkScanner`) for full scans;
- `FSCTL_GET_NTFS_FILE_RECORD` (`Native.ReadRecord` in `FsctlScanner.cs`) to read single records again;
- the shared parser and pipeline stages (`RecordParser`, `RecordMerger`, `RelationshipResolver`, `SizeAggregator`,
  `ResultSelector`, `RecordPaths`);
- `RunList.cs` from `dirsizer-inspect`.

The index stores exactly what `RecordMerger` produces. Everything derived from it (parents, names, directory sizes) is
computed again by the same shared stages, so the index can always be checked record by record against a fresh scan
(`IndexVerifier`, `--verify`). No existing tool changes behaviour. The only change to shared code is test-only:
`RecordFixture` in Task 1.

**Tech Stack:** C# 12 / .NET 8, NativeAOT, P/Invoke (`DeviceIoControl` with `FSCTL_QUERY_USN_JOURNAL` and
`FSCTL_READ_USN_JOURNAL`, `CreateFile`, `GetFileInformationByHandle`), `System.Security.Cryptography.SHA256`, the
repository's own `--self-test` harness (no test framework), PowerShell 7 for the end-to-end scripts.

---

## Design decisions (the spec for this plan)

The roadmap left these choices open. The decisions below were made while writing the plan; the report lists them
for the user.

1. **A separate, experimental executable (`dirsizer-index.exe`), not a change to `dirsizer.exe`.** An index writes
   files and needs elevation. `dirsizer.exe` does neither by default, and its spec forbids strategy flags. This
   follows the same path the bulk reader took: experimental tool first, promotion later. It is not added to
   `scripts\release.ps1` in this plan.
2. **What is stored:** the merged records (reference, header sequence, directory flag, logical size, every
   `$FILE_NAME` in order), in dictionary order. Derived values are not stored, and neither are timestamps (roadmap
   I2 said "timestamps actually needed"; nothing reported needs one). After loading, `VolumeIndex.Recompute()` runs
   the shared stages again.
3. **File:** `%LOCALAPPDATA%\dirsizer\index\<volume serial, 16 hex digits>.dsix` (`--index-dir=` overrides it). It is
   a versioned binary format with a SHA-256 trailer, written to `.tmp` and then renamed over the old file.
4. **Journal position:** it is read before the scan, or before the update. Changes made during a run are replayed by
   the next run, and replaying is harmless because the new state always comes from the MFT (roadmap I3 rule).
5. **Records that change without a journal entry:** records 0-23 and the `$Extend` tree (NTFS metadata, for example
   `$MFT` growing) are read again on every update.
6. **Extension records:** they are found through the base record's `$ATTRIBUTE_LIST`, resident or non-resident (read
   through `RunList.Decode`). An unreadable list makes the run fall back to a full scan. The tool never guesses.
7. **Aggregation after an update:** the whole index is recomputed in memory, not only the ancestor chains. The result
   is identical to a scan by construction. Task 14 measures its cost. The roadmap's "ancestor chain only" item is
   rewritten as a measured follow-up, needed only if recompute dominates.
8. **Subtree queries (I4):** Windows itself resolves the path (`GetFileInformationByHandle` gives the volume serial and
   the file reference). The answer is a walk down the selected parents. The whole volume uses
   `ResultSelector.Collect`, so it equals `dirsizer-mft`.
9. **Changes (I5):** the baseline is the saved index, aggregated before the update is applied. With `--no-save` the
   baseline stays fixed. Directories are matched by record number and sequence number.
10. **Exit codes:** 0 ok; 1 error; 2 `--verify` found differences; 3 the full scan was unstable, so the index was not
    saved (the same meaning as `dirsizer-mft`).

## Ground rules for the executing agent

- Run commands with the **PowerShell tool** from `C:\Users\user\source\repos\panzoux\dirsizer`. Create and edit files
  with Write/Edit.
- Branch first: `git checkout -b feature/persistent-index`. Never push unless asked. Commit messages end with the
  attribution line from the session's instructions.
- **Elevation:** Tasks 7, 8, 12 (step 7), 13, 14, 16, 19, 20 (steps 2 and 4) open volumes and need an elevated shell. Check with
  `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')`.
  If it prints `False`, do not skip or fake those steps. Give the user the exact commands and expected results, and
  continue only with non-volume tasks.
- `T:` is the disposable test volume labelled `NTFSTEST`. The end-to-end script refuses any other label. Never write
  to `C:` except inside `%TEMP%\dirsizer-*` directories that the plan creates and deletes.
- **Line endings:** CRLF, UTF-8 without BOM. Before every commit that touches `src\`, run:

```powershell
function Normalize-Src {
    foreach ($f in (Get-ChildItem src -Recurse -File -Include *.cs, *.csproj, *.manifest, *.sln | ForEach-Object FullName)) {
        $t = [IO.File]::ReadAllText($f); $x = $t -replace "`r?`n", "`r`n"
        if ($x -ne $t) { [IO.File]::WriteAllText($f, $x, [Text.UTF8Encoding]::new($false)) }
    }
}
Normalize-Src
```

- **Build and test cycle:**
  - Build: `dotnet build DirSizer.sln -c Release`. Expected: `Build succeeded`, 0 errors.
  - Self-tests: `dotnet artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll --self-test`. Expected: the
    `P0 self-tests passed.` line, then `ok` per test and `N self-tests passed, 0 skipped.` with the N given in each
    task, and exit code 0.
- **Regression guard for the existing tools,** run at every phase checkpoint (Tasks 8, 14, 16, 20):
  - `dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll --self-test`
  - `dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test`
  - `dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test`

  All must pass, as before the branch.
- No LINQ in production code (repository rule, see roadmap P1). `SortedSet<T>.Reverse()` and `List<T>.Reverse()` are
  instance methods, not LINQ.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `src\Shared\SelfTests.cs` | nested `Fixture` becomes top-level `RecordFixture` (reused by the index tests) | 1 |
| `src\DirSizer.Index\DirSizer.Index.csproj` | the project; compiles the shared reader files like `DirSizer.csproj` | 2 |
| `src\DirSizer.Index\IndexProgram.cs` | entry point, exit codes | 2, 7 |
| `src\DirSizer.Index\IndexOptions.cs` | options and help | 2, 12, 15, 18 |
| `src\DirSizer.Index\IndexSelfTests.cs` | harness + option tests | 2 (list grows every task) |
| `src\DirSizer.Index\VolumeIndex.cs` | `VolumeIdentity`, `VolumeIndex`, `Recompute()` | 3 |
| `src\DirSizer.Index\IndexFile.cs` | on-disk format, save/load | 3 |
| `src\DirSizer.Index\IndexSelfTests.File.cs` | tests for the above + shared sample data | 3, 5 |
| `src\DirSizer.Index\UsnJournal.cs` | query and read the USN journal | 4, 9 |
| `src\DirSizer.Index\IndexVerifier.cs` | record-by-record comparison with a fresh scan | 5 |
| `src\DirSizer.Index\SubtreeQuery.cs` | whole-volume and subtree results | 6 |
| `src\DirSizer.Index\IndexSelfTests.Query.cs` | query, path, output tests | 6, 7, 15 |
| `src\DirSizer.Index\IndexRunner.cs` | one run: load/validate/update or scan, save, query, verify | 7, 12, 15, 18 |
| `src\DirSizer.Index\IndexOutput.cs` | text and JSON | 7, 18 |
| `src\DirSizer.Index\IndexSelfTests.Usn.cs` | USN tests | 9 |
| `src\DirSizer.Index\RecordSource.cs` | `IRecordSource`, `FsctlRecordSource`, `AttributeList` | 10 |
| `src\DirSizer.Index\IndexSelfTests.Update.cs` | `FakeRecordSource`, record builders, update tests | 10, 11, 12 |
| `src\DirSizer.Index\IndexUpdater.cs` | apply journal changes; metadata records | 11 |
| `src\DirSizer.Index\IndexValidity.cs` | when a saved index may be used | 12 |
| `src\DirSizer.Index\PathResolver.cs` | path → MFT record through Windows | 15 |
| `src\DirSizer.Index\ChangeReport.cs` | `IndexSnapshot`, `ChangeReporter` | 17 |
| `src\DirSizer.Index\IndexSelfTests.Changes.cs` | change-report tests | 17, 18 |
| `scripts\Test-IndexIncremental.ps1` | end-to-end on `T:` | 13, 16, 19 |
| `scripts\Measure-Index.ps1` | full vs incremental timings | 14 |
| `docs\design_index.md` | design, format, limitations | 8, 14, 16, 19 |
| `docs\roadmap.md`, `README.md` | results, usage | 8, 14, 16, 19 |

---

# Phase I2 — persistent index

### Task 1: Make the record fixture reusable

`src\Shared\SelfTests.cs` has a private nested `Fixture` class that builds synthetic MFT records. The index tests
need it too. Move it to a top-level `RecordFixture`, and add two things: a directory flag and 32-bit resident data
sizes. The existing tests keep their behaviour.

**Files:**
- Modify: `src\Shared\SelfTests.cs`

- [x] **Step 1: Baseline**

Run: `dotnet build DirSizer.sln -c Release; dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll --self-test; dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test`
Expected: `P0 self-tests passed.` from both, `P3 USA self-tests passed.` from dirsizer-mft, and exit code 0.

- [x] **Step 2: Move the class**

In `src\Shared\SelfTests.cs`, delete the whole nested block `    static class Fixture { ... }` (from the line
`    static class Fixture` to its closing brace, just before the file's final `}`). After the final `}` of `SelfTests`,
append:

```csharp

// Builds synthetic MFT records for self-tests. Shared by SelfTests (P0) and dirsizer-index's IndexSelfTests.
static class RecordFixture
{
    public static byte[] Record(ulong number, ulong baseReference = 0, bool directory = false, int size = 512)
    {
        var record = new byte[size];
        record[0] = (byte)'F';
        record[1] = (byte)'I';
        record[2] = (byte)'L';
        record[3] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), (ushort)(directory ? 3 : 1));
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), baseReference);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(48), uint.MaxValue);
        return record;
    }

    public static void AddName(byte[] record, ulong parent, string name, byte nameSpace)
    {
        var offset = NextAttribute(record);
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var valueLength = 66 + nameBytes.Length;
        var attributeLength = Align8(24 + valueLength);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), (uint)attributeLength);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16), (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(offset + 24), parent);
        record[offset + 24 + 64] = (byte)name.Length;
        record[offset + 24 + 65] = nameSpace;
        nameBytes.CopyTo(record, offset + 24 + 66);
        EndAttribute(record, offset + attributeLength);
    }

    // A resident unnamed $DATA attribute whose value length is `size` (the value bytes themselves are not written).
    public static void AddData(byte[] record, long size)
    {
        var offset = NextAttribute(record);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
        EndAttribute(record, offset + 24);
    }

    public static int NextAttribute(byte[] record)
    {
        var offset = 48;
        while (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset)) != uint.MaxValue)
            offset += (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4));
        return offset;
    }

    public static void EndAttribute(byte[] record, int offset) => BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), uint.MaxValue);
    public static int Align8(int value) => (value + 7) & ~7;
}
```

The old `AddName` wrote the value length as one byte (`record[offset + 16] = (byte)valueLength;`), and so did
`AddData`. Both now write the full 32-bit field. For the existing tests' values (below 256) the bytes are identical.

- [x] **Step 3: Update the eight references**

In the same file, replace every `Fixture.Record(`, `Fixture.AddData(` and `Fixture.AddName(` with
`RecordFixture.Record(`, `RecordFixture.AddData(` and `RecordFixture.AddName(`. They are at lines 30, 38, 39, 49, 50,
51, 67 and 68 of the original file. `src\DirSizer.Bulk\BulkSelfTests.cs` has its own nested `Fixture`. Leave it alone.

- [x] **Step 4: Verify nothing changed**

Run step 1's command again. Expected: the same output, exit code 0.
Run: `Select-String -Path src\Shared\SelfTests.cs -Pattern '\bFixture\.'`
Expected: no output.

- [x] **Step 5: Commit**

```powershell
Normalize-Src
git add src\Shared\SelfTests.cs
git commit -m "SelfTests: top-level RecordFixture (directory flag, 32-bit value lengths) for reuse by the index tests"
```

---

### Task 2: Project skeleton, options, test harness

**Files:**
- Create: `src\DirSizer.Index\DirSizer.Index.csproj`, `src\DirSizer.Index\IndexOptions.cs`, `src\DirSizer.Index\IndexSelfTests.cs`, `src\DirSizer.Index\IndexProgram.cs`
- Modify: `DirSizer.sln`

- [x] **Step 1: The project file**

`src\DirSizer.Index\DirSizer.Index.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- dirsizer-index.exe: EXPERIMENTAL. A per-volume index built from the $MFT, kept up to date from the USN journal,
       answering size queries for any directory. See docs\design_index.md and docs\roadmap.md (I2-I5). -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>dirsizer-index</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <DebugType>none</DebugType>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Speed</OptimizationPreference>
    <ApplicationManifest>..\Shared\app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\DirSizer.Core\DirSizer.Core.csproj" />
    <Compile Include="..\Shared\ConsolePause.cs" />
    <Compile Include="..\Shared\Cli.cs" />
    <Compile Include="..\Shared\DriveRoot.cs" />
    <Compile Include="..\Shared\SelfTests.cs" />
    <Compile Include="..\Shared\FsctlScanner.cs" />
    <Compile Include="..\Shared\BulkReader\BulkReader.cs" />
    <Compile Include="..\Shared\BulkReader\BulkStability.cs" />
    <Compile Include="..\Shared\BulkReader\BulkScanner.cs" />
    <Compile Include="..\Shared\BulkReader\BulkReport.cs" />
    <Compile Include="..\DirSizer.Bulk\BulkIntegration.cs" />
    <Compile Include="..\DirSizer.Inspect\RunList.cs" />
  </ItemGroup>
</Project>
```

`BulkIntegration.cs` is compiled in for its `ScanProgress` class, which prints the same `Scanning MFT: n/N (p%)`
progress line on stderr.

- [x] **Step 2: Options**

`src\DirSizer.Index\IndexOptions.cs`:

```csharp
// dirsizer-index's options. EXPERIMENTAL tool; see docs\design_index.md.
sealed class IndexOptions
{
    public string Target { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Json { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool NoSave { get; private set; }
    public bool Verify { get; private set; }
    public string IndexDirectory { get; private set; } = IndexFile.DefaultDirectory;

    public static IndexOptions Parse(string[] args)
    {
        var result = new IndexOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--self-test") { result.SelfTest = true; continue; }
            if (arg == "--benchmark") { result.Benchmark = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg == "--no-save") { result.NoSave = true; continue; }
            if (arg == "--verify") { result.Verify = true; continue; }
            if (arg.StartsWith("--index-dir=", StringComparison.Ordinal) && arg.Length > "--index-dir=".Length) { result.IndexDirectory = Path.GetFullPath(arg["--index-dir=".Length..]); continue; }
            if (arg.StartsWith("--index-dir", StringComparison.Ordinal)) throw new ArgumentException("Use --index-dir=DIRECTORY.");
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("--top needs a whole number: --top=N or --top N.");
            if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Target.Length != 0) throw new ArgumentException("Only one path is supported.");
            // "C:" alone would mean the current directory on C:; it is taken as the drive root, as the other tools do.
            result.Target = arg.Length == 2 && arg[1] == ':' ? arg + "\\" : arg;
        }
        if (!result.Help && !result.SelfTest && result.Target.Length == 0) throw new ArgumentException("A drive root is required, for example C:\\.");
        if (result.Verify && result.NoSave) throw new ArgumentException("--verify loads the saved index back; it cannot be combined with --no-save.");
        return result;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            dirsizer-index - EXPERIMENTAL: folder sizes from a saved, per-volume NTFS index

            Usage: dirsizer-index C:\ [--top=N] [--files] [--json] [--verify] [--no-save] [--index-dir=DIR] [--benchmark]

            --top=N          Show the largest N results (default: 25)
            --files          Include largest files
            --json           Write machine-readable JSON to stdout
            --verify         Load the saved index back and compare it with the scan (exit code 2 if they differ)
            --no-save        Do not write the index
            --index-dir=DIR  Where index files are kept (default: %LOCALAPPDATA%\dirsizer\index)
            --benchmark      Print phase timings to stderr
            --self-test      Run the built-in tests
            -h               Show this help

            Reads the whole $MFT like dirsizer-mft and saves the records as <volume serial>.dsix.
            The index file lists every file and directory name on the volume. In the default location only
            the current user, SYSTEM and Administrators can read it.
            Requires Administrator: the executable asks for elevation when it starts.

            Exit codes: 0 ok; 1 error; 2 --verify found differences; 3 the MFT changed while it was read,
            so the index was not saved.
            """);
    }
}
```

`IndexFile.DefaultDirectory` is added in Task 3. Until then the project does not build; steps 3-5 come first, and
the build happens in Task 3.

- [x] **Step 3: Test harness with the option tests**

`src\DirSizer.Index\IndexSelfTests.cs`:

```csharp
// dirsizer-index's own tests. Record-level tests use synthetic records (RecordFixture, shared with the P0 self-tests);
// the path tests use a temporary directory; nothing here opens a volume, so --self-test needs no elevation to pass.
readonly record struct IndexSelfTest(string Name, Action Body);

static partial class IndexSelfTests
{
    // Returns the process exit code: 0 if no test failed.
    public static int Run()
    {
        var tests = new List<IndexSelfTest>
        {
            new("options: defaults, flags, a bare drive letter becomes its root", OptionsDefaultsAndFlags),
            new("options: bad input is rejected", OptionsRejectBadInput),
        };

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"ok    {test.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
            }
        }
        Console.WriteLine(failed == 0 ? $"{tests.Count} self-tests passed, 0 skipped." : $"{failed} of {tests.Count} self-tests FAILED.");
        return failed == 0 ? 0 : 1;
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
        try { action(); }
        catch (TException) { return; }
        throw new Exception($"{what}: expected {typeof(TException).Name}, nothing was thrown");
    }

    static void AssertThrowsWithMessage<TException>(Action action, string fragment, string what) where TException : Exception
    {
        try { action(); }
        catch (TException exception)
        {
            if (!exception.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"{what}: the message \"{exception.Message}\" does not mention \"{fragment}\"");
            return;
        }
        throw new Exception($"{what}: expected {typeof(TException).Name}, nothing was thrown");
    }

    static void AssertContains(string? text, string fragment, string what)
    {
        if (text is null || !text.Contains(fragment, StringComparison.OrdinalIgnoreCase)) throw new Exception($"{what}: expected text containing \"{fragment}\", got \"{text}\"");
    }

    static void OptionsDefaultsAndFlags()
    {
        var defaults = IndexOptions.Parse(["C:"]);
        AssertEqual("C:\\", defaults.Target, "a bare drive letter becomes its root");
        AssertEqual(25, defaults.Top, "default top");
        Assert(!defaults.Files && !defaults.Json && !defaults.Benchmark && !defaults.NoSave && !defaults.Verify, "default flags");
        AssertEqual(IndexFile.DefaultDirectory, defaults.IndexDirectory, "default index directory");
        Assert(defaults.IndexDirectory.EndsWith("dirsizer\\index", StringComparison.OrdinalIgnoreCase), $"under LOCALAPPDATA: {defaults.IndexDirectory}");

        var all = IndexOptions.Parse(["D:\\", "--top=7", "--files", "--json", "--benchmark", "--verify", "--index-dir=X:\\idx"]);
        AssertEqual(7, all.Top, "--top=N");
        Assert(all.Files && all.Json && all.Benchmark && all.Verify, "flags");
        AssertEqual("X:\\idx", all.IndexDirectory, "--index-dir");
        Assert(IndexOptions.Parse(["D:\\", "--no-save"]).NoSave, "--no-save");
    }

    static void OptionsRejectBadInput()
    {
        AssertThrows<ArgumentException>(() => IndexOptions.Parse([]), "no path");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "D:\\"]), "two paths");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "--strategy=mft"]), "an unknown option");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "--top=x"]), "--top without a number");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "--index-dir"]), "--index-dir without a directory");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "--verify", "--no-save"]), "--verify needs the saved index");
    }
}
```

- [x] **Step 4: Entry point (scanning is wired in Task 7)**

`src\DirSizer.Index\IndexProgram.cs`:

```csharp
ConsolePause.Register();
IndexOptions options;
try
{
    options = IndexOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    IndexOptions.PrintHelp();
    return 0;
}
if (options.SelfTest)
{
    SelfTests.Run();
    return IndexSelfTests.Run();
}
Console.Error.WriteLine("error: scanning is not wired in yet (Task 7 of the plan).");
return 1;
```

- [x] **Step 5: Add the project to the solution**

Run: `dotnet sln DirSizer.sln add src\DirSizer.Index\DirSizer.Index.csproj`
Then `git diff DirSizer.sln`. Expected: one new `Project(...) = "DirSizer.Index", "src\DirSizer.Index\DirSizer.Index.csproj"`
entry, its configuration lines, and a nesting line under the existing `src` folder. If a second `src` folder
appears, run `git checkout DirSizer.sln` and use
`dotnet sln DirSizer.sln add src\DirSizer.Index\DirSizer.Index.csproj --solution-folder src` instead.

- [x] **Step 6: No commit yet**

The project does not build until Task 3 adds `IndexFile`. Task 3 commits both.

---

### Task 3: `VolumeIndex` and the index file format

**Files:**
- Create: `src\DirSizer.Index\VolumeIndex.cs`, `src\DirSizer.Index\IndexFile.cs`, `src\DirSizer.Index\IndexSelfTests.File.cs`
- Modify: `src\DirSizer.Index\IndexSelfTests.cs` (test list)

- [x] **Step 1: Write the failing tests**

`src\DirSizer.Index\IndexSelfTests.File.cs`:

```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;

static partial class IndexSelfTests
{
    static readonly VolumeIdentity TestIdentity = new(0x1234_5678_9ABC_DEF0, 1024, 4096, 786432);

    static FileRef Ref(ulong number, ushort sequence = 1) => new(number | ((ulong)sequence << 48));

    static ParsedRecord Parsed(ulong number, bool directory, long size, ulong baseNumber = 0, params FileName[] names) =>
        new(Ref(number), 1, baseNumber == 0 ? default : Ref(baseNumber), directory, size, [.. names]);

    static FileName Name(string name, ulong parent = 5, byte nameSpace = 1) => new(Ref(parent), name, nameSpace);

    // Root 5: A (30) with B (31) holding b.bin (40, 100 bytes) and a.bin (41, 20); C (32) with a non-ASCII name file (42, 7);
    // r.bin (43, 3) at the root; x.bin (44, 50) hard-linked in A and C, with extension record 45 adding its DOS name.
    // Merged in descending record order, as a scan does. Sizes after aggregation: root 180, A 170, B 100, C 7.
    static Dictionary<ulong, FileRecord> SampleRecords()
    {
        var records = new Dictionary<ulong, FileRecord>();
        foreach (var parsed in new[]
        {
            Parsed(45, false, 0, 44, Name("X~1.BIN", 30, 2)),
            Parsed(44, false, 50, 0, Name("x.bin", 30), Name("x.bin", 32)),
            Parsed(43, false, 3, 0, Name("r.bin")),
            Parsed(42, false, 7, 0, Name("\u65E5\u672C\u8A9E\U0001F600.txt", 32)),
            Parsed(41, false, 20, 0, Name("a.bin", 30)),
            Parsed(40, false, 100, 0, Name("b.bin", 31)),
            Parsed(32, true, 0, 0, Name("C")),
            Parsed(31, true, 0, 0, Name("B", 30)),
            Parsed(30, true, 0, 0, Name("A")),
            Parsed(5, true, 0, 0, Name(".", 5)),
        })
            RecordMerger.Merge(records, parsed);
        return records;
    }

    static VolumeIndex Aggregated(Dictionary<ulong, FileRecord> records)
    {
        var index = new VolumeIndex(TestIdentity, 7, 1000, new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc), records);
        index.Recompute();
        return index;
    }

    static void IndexFileRoundTripsRecordsInOrder()
    {
        var original = new VolumeIndex(TestIdentity, 0xABCDEF, 123456789, new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc), SampleRecords());
        var loaded = IndexFile.Read(IndexFile.Serialize(original));
        AssertEqual(original.Identity, loaded.Identity, "identity");
        AssertEqual(original.JournalId, loaded.JournalId, "journal id");
        AssertEqual(original.NextUsn, loaded.NextUsn, "next USN");
        AssertEqual(original.WrittenUtc, loaded.WrittenUtc, "written time");
        AssertEqual(string.Join(',', original.Records.Keys), string.Join(',', loaded.Records.Keys), "record order");
        foreach (var (number, expected) in original.Records)
        {
            var actual = loaded.Records[number];
            AssertEqual(expected.Reference, actual.Reference, $"record {number} reference");
            AssertEqual(expected.SequenceNumber, actual.SequenceNumber, $"record {number} sequence");
            AssertEqual(expected.IsDirectory, actual.IsDirectory, $"record {number} directory flag");
            AssertEqual(expected.LogicalSize, actual.LogicalSize, $"record {number} logical size");
            AssertEqual(expected.Names.Count, actual.Names.Count, $"record {number} name count");
            for (var i = 0; i < expected.Names.Count; i++) AssertEqual(expected.Names[i], actual.Names[i], $"record {number} name {i}");
        }
    }

    static void LoadedIndexAggregatesLikeTheScan()
    {
        var scanned = Aggregated(SampleRecords());
        var loaded = IndexFile.Read(IndexFile.Serialize(scanned));
        loaded.Recompute();
        AssertEqual(180L, loaded.Records[5].Size, "root size: 100 + 20 + 7 + 3 + 50");
        AssertEqual(170L, loaded.Records[30].Size, "A: B (100) + a.bin (20) + x.bin (50, whose selected parent is A)");
        AssertEqual(7L, loaded.Records[32].Size, "C: only its own file; the hard link counts once, under A");
        foreach (var (number, expected) in scanned.Records)
        {
            var actual = loaded.Records[number];
            AssertEqual(expected.Parent, actual.Parent, $"record {number} parent");
            AssertEqual(expected.DisplayName, actual.DisplayName, $"record {number} display name");
            AssertEqual(expected.Size, actual.Size, $"record {number} size");
        }
    }

    static void RecomputeCanRunTwice()
    {
        var index = Aggregated(SampleRecords());
        index.Recompute();
        AssertEqual(180L, index.Records[5].Size, "root size after a second Recompute (sizes are reset, not added twice)");
    }

    static void DamagedIndexFileIsRejected()
    {
        var bytes = IndexFile.Serialize(Aggregated(SampleRecords()));
        var flipped = (byte[])bytes.Clone();
        flipped[bytes.Length / 2] ^= 0x40;
        AssertThrowsWithMessage<InvalidDataException>(() => IndexFile.Read(flipped), "checksum", "one flipped byte");
        AssertThrows<InvalidDataException>(() => IndexFile.Read(bytes[..^1]), "a truncated file");
        AssertThrows<InvalidDataException>(() => IndexFile.Read(new byte[10]), "a 10-byte file");

        // A newer format version with a valid checksum must be refused by its version, not misread.
        var body = bytes[..^32];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), IndexFile.Version + 1);
        var future = new byte[bytes.Length];
        body.CopyTo(future, 0);
        SHA256.HashData(body).CopyTo(future, body.Length);
        AssertThrowsWithMessage<InvalidDataException>(() => IndexFile.Read(future), "version", "a newer format version");
    }

    static void SaveReplacesTheFileAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-index-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = IndexFile.PathFor(directory, TestIdentity.SerialNumber);
            AssertEqual("123456789ABCDEF0.dsix", Path.GetFileName(path), "file name is the volume serial in hex");
            var index = Aggregated(SampleRecords());
            IndexFile.Save(index, path);
            index.NextUsn = 2000;
            IndexFile.Save(index, path);
            AssertEqual(2000L, IndexFile.Read(File.ReadAllBytes(path)).NextUsn, "the second save replaced the first");
            Assert(!File.Exists(path + ".tmp"), "no temporary file is left behind");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    static void TryLoadExplainsWhy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-index-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = IndexFile.PathFor(directory, 1);
            Assert(IndexFile.TryLoad(path, out var problem) is null, "a missing file loads nothing");
            AssertEqual("no saved index", problem, "reason for a missing file");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, new byte[100]);
            Assert(IndexFile.TryLoad(path, out problem) is null, "a damaged file loads nothing");
            AssertContains(problem, "cannot be used", "reason for a damaged file");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
```

In `src\DirSizer.Index\IndexSelfTests.cs`, add at the end of the `tests` list initializer (after the
`OptionsRejectBadInput` line):

```csharp
            new("index file: records, names and header survive a round trip, in order", IndexFileRoundTripsRecordsInOrder),
            new("index file: a loaded index aggregates exactly like the scan that wrote it", LoadedIndexAggregatesLikeTheScan),
            new("index: Recompute can run twice", RecomputeCanRunTwice),
            new("index file: damage, truncation and a newer version are rejected", DamagedIndexFileIsRejected),
            new("index file: saving replaces the file and leaves no temporary file", SaveReplacesTheFileAtomically),
            new("index file: TryLoad says why nothing was loaded", TryLoadExplainsWhy),
```

- [x] **Step 2: Run to see it fail**

Run: `dotnet build DirSizer.sln -c Release`
Expected: FAIL with errors naming `VolumeIdentity`, `VolumeIndex` and `IndexFile` (they do not exist yet).

- [x] **Step 3: Implement `VolumeIndex.cs`**

```csharp
// The persistent index of one NTFS volume (docs\design_index.md). Records holds the merged MFT records exactly as a
// scan's RecordMerger leaves them -- the input of relationship resolution and aggregation -- so a loaded index goes
// through the same shared stages as a fresh scan. Size, Parent and DisplayName are derived and never stored.

// What must be unchanged for a saved index to describe this volume at all.
sealed record VolumeIdentity(long SerialNumber, int RecordSize, long BytesPerCluster, long MftStartLcn)
{
    public static VolumeIdentity From(BulkNative.VolumeData data) => new(data.SerialNumber, data.RecordSize, data.BytesPerCluster, data.MftStartLcn);
}

// JournalId 0 means the volume had no active USN journal when the index was written: it cannot be brought up to date
// incrementally. NextUsn is the journal position the index is known to be current up to; later changes may or may not
// be in it already, and applying them again is harmless (IndexUpdater reads the current state from the MFT).
sealed class VolumeIndex(VolumeIdentity identity, ulong journalId, long nextUsn, DateTime writtenUtc, Dictionary<ulong, FileRecord> records)
{
    public VolumeIdentity Identity { get; } = identity;
    public ulong JournalId { get; set; } = journalId;
    public long NextUsn { get; set; } = nextUsn;
    public DateTime WrittenUtc { get; set; } = writtenUtc;
    public Dictionary<ulong, FileRecord> Records { get; } = records;
    public RelationshipResult? Relationships { get; set; }

    // Resolves parents and names and aggregates directory sizes from scratch, with the shared stages a scan uses.
    public void Recompute()
    {
        foreach (var record in Records.Values)
        {
            record.Size = 0;
            record.Parent = default;
            record.DisplayName = null;
        }
        Relationships = RelationshipResolver.Resolve(Records);
        SizeAggregator.AddFileSizesToParents(Records);
        SizeAggregator.AggregateDirectories(Records);
    }
}
```

- [x] **Step 4: Implement `IndexFile.cs`**

```csharp
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// The on-disk form of a VolumeIndex (docs\design_index.md). Version 1, little-endian throughout:
//   u32 magic "DSIX", u32 version
//   i64 volume serial, i32 MFT record size, i64 bytes per cluster, i64 MFT start LCN
//   u64 USN journal id (0 = none), i64 next USN, i64 time written (UTC ticks)
//   i32 record count, then for each record, in dictionary order:
//     u64 reference, u16 header sequence, u8 flags (bit 0 = directory), i64 logical size, u16 name count,
//     and for each name: u64 parent reference, u8 namespace, u16 character count, UTF-16LE characters
//   32 bytes: SHA-256 of everything before them
// Records are written and read in dictionary order, so a loaded index enumerates exactly like the scan that wrote it.
static class IndexFile
{
    public const uint Magic = 0x58495344;   // "DSIX" read as a little-endian u32
    public const uint Version = 1;
    const int HashLength = 32;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dirsizer", "index");

    public static string PathFor(string directory, long serialNumber) => Path.Combine(directory, $"{serialNumber:X16}.dsix");

    // Writes a temporary file, flushes it to disk, then renames it over the old index: a crash leaves the old or the new file.
    public static void Save(VolumeIndex index, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var bytes = Serialize(index);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    // Null if there is no usable file; `problem` then says why (it becomes the reason for a full scan).
    public static VolumeIndex? TryLoad(string path, out string? problem)
    {
        if (!File.Exists(path))
        {
            problem = "no saved index";
            return null;
        }
        try
        {
            var index = Read(File.ReadAllBytes(path));
            problem = null;
            return index;
        }
        catch (InvalidDataException exception)
        {
            problem = $"the saved index cannot be used: {exception.Message}";
            return null;
        }
    }

    public static byte[] Serialize(VolumeIndex index)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(index.Identity.SerialNumber);
            writer.Write(index.Identity.RecordSize);
            writer.Write(index.Identity.BytesPerCluster);
            writer.Write(index.Identity.MftStartLcn);
            writer.Write(index.JournalId);
            writer.Write(index.NextUsn);
            writer.Write(index.WrittenUtc.Ticks);
            writer.Write(index.Records.Count);
            foreach (var record in index.Records.Values)
            {
                writer.Write(record.Reference.FullReference);
                writer.Write(record.SequenceNumber);
                writer.Write((byte)(record.IsDirectory ? 1 : 0));
                writer.Write(record.LogicalSize);
                writer.Write(checked((ushort)record.Names.Count));
                foreach (var name in record.Names)
                {
                    writer.Write(name.Parent.FullReference);
                    writer.Write(name.Namespace);
                    writer.Write(checked((ushort)name.Name.Length));
                    foreach (var character in name.Name) writer.Write((ushort)character);
                }
            }
        }
        var hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        stream.Write(hash);
        return stream.ToArray();
    }

    public static VolumeIndex Read(byte[] bytes)
    {
        if (bytes.Length < HashLength + 8) throw new InvalidDataException("The index file is truncated.");
        var body = bytes.AsSpan(0, bytes.Length - HashLength);
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength)))
            throw new InvalidDataException("The index file checksum does not match.");
        var reader = new IndexReader(body);
        if (reader.U32() != Magic) throw new InvalidDataException("Not a dirsizer index file.");
        var version = reader.U32();
        if (version != Version) throw new InvalidDataException($"Index format version {version} is not supported (this build reads version {Version}).");
        var identity = new VolumeIdentity(reader.I64(), reader.I32(), reader.I64(), reader.I64());
        var journalId = reader.U64();
        var nextUsn = reader.I64();
        var written = new DateTime(reader.I64(), DateTimeKind.Utc);
        var count = reader.I32();
        if (count < 0) throw new InvalidDataException("The record count is negative.");
        var records = new Dictionary<ulong, FileRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var reference = new FileRef(reader.U64());
            var sequence = reader.U16();
            var flags = reader.U8();
            var record = new FileRecord(reference, sequence, (flags & 1) != 0) { LogicalSize = reader.I64() };
            var names = reader.U16();
            for (var n = 0; n < names; n++)
            {
                var parent = new FileRef(reader.U64());
                var nameSpace = reader.U8();
                var characters = reader.U16();
                record.Names.Add(new FileName(parent, reader.Utf16(characters), nameSpace));
            }
            if (!records.TryAdd(reference.RecordNumber, record)) throw new InvalidDataException($"Record {reference.RecordNumber} appears twice.");
        }
        if (reader.Position != body.Length) throw new InvalidDataException("There are bytes after the last record.");
        return new VolumeIndex(identity, journalId, nextUsn, written, records);
    }

    // Bounds-checked little-endian reads; running past the end is a damaged file, not an exception of another type.
    ref struct IndexReader
    {
        readonly ReadOnlySpan<byte> data;
        int position;

        public IndexReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            position = 0;
        }

        public readonly int Position => position;

        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - position) throw new InvalidDataException("The index file is truncated.");
            var slice = data.Slice(position, count);
            position += count;
            return slice;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public string Utf16(int characters) => new(MemoryMarshal.Cast<byte, char>(Take(characters * 2)));
    }
}
```

- [x] **Step 5: Run the tests**

Run: `dotnet build DirSizer.sln -c Release; dotnet artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll --self-test; "exit=$LASTEXITCODE"`
Expected: `P0 self-tests passed.`, eight `ok` lines, `8 self-tests passed, 0 skipped.`, `exit=0`.

- [x] **Step 6: Mutation check (the tests must be able to fail)**

In `VolumeIndex.Recompute`, comment out `record.Size = 0;` and rebuild. Run the self-tests. Expected: `FAIL  index:
Recompute can run twice` (root 360 instead of 180). Restore the line, rebuild, and run again: 8 passed. Record both
outputs in the task report.

- [x] **Step 7: Commit**

```powershell
Normalize-Src
git add DirSizer.sln src\DirSizer.Index
git commit -m "dirsizer-index: project, options, VolumeIndex and the versioned index file with a SHA-256 trailer"
```

---

### Task 4: Read the USN journal state

The index records the journal position from I2 on, so I3 can continue from it. This needs a volume handle, so
there is no self-test. It is checked on a real volume in Task 7.

**Files:**
- Create: `src\DirSizer.Index\UsnJournal.cs`

- [x] **Step 1: Confirm the error codes on this machine**

Run: `foreach ($c in 38, 1178, 1179, 1181) { '{0} {1}' -f $c, [ComponentModel.Win32Exception]::new($c).Message }`
Expected (in the OS language): 38 end of file; 1178 the journal is being deleted; 1179 the journal is not active; 1181
the journal entry has been deleted.

- [x] **Step 2: Implement**

```csharp
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The volume's USN change journal. The index uses it only to learn *which* MFT records changed; their new state is
// always read from the MFT itself (docs\design_index.md, roadmap I3).
readonly record struct JournalState(ulong JournalId, long FirstUsn, long NextUsn);

static class UsnJournal
{
    const uint FsctlQueryUsnJournal = 0x000900F4;
    public const int ErrorHandleEof = 38;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    // Null if the volume has no active journal (or it is being deleted). The first three fields are the same in every
    // version of USN_JOURNAL_DATA: journal id, first USN still in the journal, next USN to be written.
    public static JournalState? Query(SafeFileHandle volume)
    {
        var output = new byte[80];
        if (!DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorJournalNotActive or ErrorJournalDeleteInProgress) return null;
            throw new Win32Exception(error, $"Cannot query the USN journal (Win32 error {error})");
        }
        if (returned < 24) throw new IOException("Invalid USN journal query response");
        return new JournalState(BinaryPrimitives.ReadUInt64LittleEndian(output), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
```

- [x] **Step 3: Build**

Run: `dotnet build DirSizer.sln -c Release`
Expected: `Build succeeded`.

- [x] **Step 4: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index\UsnJournal.cs
git commit -m "dirsizer-index: read the USN journal id and position"
```

---

### Task 5: `IndexVerifier`: compare an index with a fresh scan

**Files:**
- Create: `src\DirSizer.Index\IndexVerifier.cs`
- Modify: `src\DirSizer.Index\IndexSelfTests.File.cs` (one test), `src\DirSizer.Index\IndexSelfTests.cs` (list)

- [x] **Step 1: Write the failing test**

Append this method inside `static partial class IndexSelfTests` in `IndexSelfTests.File.cs`:

```csharp
    static void VerifierFindsEachKindOfDifference()
    {
        var fresh = Aggregated(SampleRecords()).Records;
        AssertEqual(0, IndexVerifier.Compare(Aggregated(SampleRecords()).Records, fresh).Differences, "identical indexes");

        void ExpectOne(Action<Dictionary<ulong, FileRecord>> change, string fragment, string what)
        {
            var index = Aggregated(SampleRecords()).Records;
            change(index);
            var result = IndexVerifier.Compare(index, fresh);
            AssertEqual(1, result.Differences, what);
            AssertContains(result.Samples[0], fragment, what);
        }
        ExpectOne(index => index[41].LogicalSize = 21, "logical size", "a changed file size");
        ExpectOne(index => index[43].Names[0] = Name("s.bin"), "name 0", "a changed name");
        ExpectOne(index => index.Remove(43), "missing from the index", "a missing record");
        ExpectOne(index => index.Add(99, new FileRecord(Ref(99), 1, false)), "no longer on the volume", "an extra record");
        ExpectOne(index => index[30].Size += 1, "directory size", "a wrong directory total");
        ExpectOne(index => index[40].Reference = Ref(40, 2), "reference", "a reused record");
    }
```

Add to the test list in `IndexSelfTests.cs`:

```csharp
            new("verifier: finds size, name, missing, extra, total and reference differences", VerifierFindsEachKindOfDifference),
```

- [x] **Step 2: Run to see it fail**

Run: `dotnet build DirSizer.sln -c Release`
Expected: FAIL, `IndexVerifier` does not exist.

- [x] **Step 3: Implement `IndexVerifier.cs`**

```csharp
// Compares an index with a fresh scan record by record: identity, logical size, every name (in order), and the derived
// parent, display name and directory size. Both dictionaries must be aggregated (VolumeIndex.Recompute, or a scan).
// Dictionary order is not compared: after an incremental update it differs from a scan's, and no size depends on it.
sealed record VerifyResult(int Differences, string[] Samples);

static class IndexVerifier
{
    const int MaxSamples = 20;

    public static VerifyResult Compare(Dictionary<ulong, FileRecord> index, Dictionary<ulong, FileRecord> fresh)
    {
        var differences = 0;
        var samples = new List<string>();
        void Add(string text)
        {
            differences++;
            if (samples.Count < MaxSamples) samples.Add(text);
        }

        foreach (var (number, expected) in fresh)
        {
            if (!index.TryGetValue(number, out var actual))
            {
                Add($"record {number} ({expected.DisplayName}): on the volume, missing from the index");
                continue;
            }
            var problem = Describe(expected, actual);
            if (problem is not null) Add($"record {number} ({expected.DisplayName}): {problem}");
        }
        foreach (var (number, actual) in index)
        {
            if (!fresh.ContainsKey(number)) Add($"record {number} ({actual.DisplayName}): in the index, no longer on the volume");
        }
        return new VerifyResult(differences, samples.ToArray());
    }

    static string? Describe(FileRecord expected, FileRecord actual)
    {
        if (expected.Reference != actual.Reference) return $"reference {Ref(expected.Reference)} on the volume, {Ref(actual.Reference)} in the index";
        if (expected.SequenceNumber != actual.SequenceNumber) return $"sequence {expected.SequenceNumber} on the volume, {actual.SequenceNumber} in the index";
        if (expected.IsDirectory != actual.IsDirectory) return $"directory={expected.IsDirectory} on the volume, {actual.IsDirectory} in the index";
        if (expected.LogicalSize != actual.LogicalSize) return $"logical size {expected.LogicalSize} on the volume, {actual.LogicalSize} in the index";
        if (expected.Names.Count != actual.Names.Count) return $"{expected.Names.Count} names on the volume, {actual.Names.Count} in the index";
        for (var i = 0; i < expected.Names.Count; i++)
        {
            if (expected.Names[i] != actual.Names[i])
                return $"name {i} is \"{expected.Names[i].Name}\" (parent {Ref(expected.Names[i].Parent)}) on the volume, \"{actual.Names[i].Name}\" (parent {Ref(actual.Names[i].Parent)}) in the index";
        }
        if (expected.Parent != actual.Parent) return $"parent {Ref(expected.Parent)} on the volume, {Ref(actual.Parent)} in the index";
        if (expected.DisplayName != actual.DisplayName) return $"display name \"{expected.DisplayName}\" on the volume, \"{actual.DisplayName}\" in the index";
        if (expected.Size != actual.Size) return $"directory size {expected.Size} on the volume, {actual.Size} in the index";
        return null;
    }

    static string Ref(FileRef reference) => $"{reference.RecordNumber}:{reference.SequenceNumber}";
}
```

- [x] **Step 4: Run the tests**

Build and run the self-tests. Expected: `9 self-tests passed, 0 skipped.`, exit 0.

- [x] **Step 5: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: IndexVerifier compares an index with a fresh scan record by record"
```

---

### Task 6: `SubtreeQuery`: whole-volume and subtree results

The whole code goes in now: I2 and I3 query only the whole volume, and I4 adds path resolution.

**Files:**
- Create: `src\DirSizer.Index\SubtreeQuery.cs`, `src\DirSizer.Index\IndexSelfTests.Query.cs`
- Modify: `src\DirSizer.Index\IndexSelfTests.cs` (list)

- [x] **Step 1: Write the failing tests**

`src\DirSizer.Index\IndexSelfTests.Query.cs`:

```csharp
static partial class IndexSelfTests
{
    static string Join(UnifiedItem[] items) => string.Join(',', Array.ConvertAll(items, item => $"{item.Path}={item.Size}"));

    static void WholeVolumeQueryMatchesTheScanSelection()
    {
        var index = Aggregated(SampleRecords());
        var result = SubtreeQuery.Query(index.Records, "T:", index.Records[5], 3);
        AssertEqual("T:\\", result.Root.Path, "root path");
        AssertEqual(180L, result.Root.Size, "root size");
        AssertEqual("T:\\=180,T:\\A=170,T:\\A\\B=100", Join(result.Directories), "largest 3 directories");
        AssertEqual("T:\\A\\B\\b.bin=100,T:\\A\\x.bin=50,T:\\A\\a.bin=20", Join(result.Files), "largest 3 files");
        AssertEqual("T:\\A=170,T:\\C=7,T:\\r.bin=3", Join(result.RootChildren), "root children, largest first");
        AssertEqual(4L, result.DirectoriesScanned, "directories: root, A, B, C");
        AssertEqual(5L, result.FileCount, "files: 40-44 (45 is merged into 44)");
        AssertEqual("index", result.Strategy, "strategy name");
    }

    static void SubtreeQueryCoversOnlyTheDirectory()
    {
        var index = Aggregated(SampleRecords());
        var result = SubtreeQuery.Query(index.Records, "T:", index.Records[30], 10);
        AssertEqual("T:\\A=170", $"{result.Root.Path}={result.Root.Size}", "root");
        AssertEqual("T:\\A=170,T:\\A\\B=100", Join(result.Directories), "directories at or below A");
        AssertEqual("T:\\A\\B\\b.bin=100,T:\\A\\x.bin=50,T:\\A\\a.bin=20", Join(result.Files), "files below A");
        AssertEqual("T:\\A\\B=100,T:\\A\\x.bin=50,T:\\A\\a.bin=20", Join(result.RootChildren), "A's children, largest first");
        AssertEqual(2L, result.DirectoriesScanned, "directories: A and B");
        AssertEqual(3L, result.FileCount, "files: b.bin, a.bin, x.bin");
        AssertEqual(1, SubtreeQuery.Descendants(index.Records, 32).Count, "below C: only its own file; x.bin's selected parent is A");
    }
}
```

Add to the test list:

```csharp
            new("query: the whole volume equals the scan's own selection", WholeVolumeQueryMatchesTheScanSelection),
            new("query: a subtree covers only that directory", SubtreeQueryCoversOnlyTheDirectory),
```

- [x] **Step 2: Run to see it fail**

Build. Expected: FAIL, `SubtreeQuery` does not exist.

- [x] **Step 3: Implement `SubtreeQuery.cs`**

```csharp
// Answers a size query for one directory from an aggregated index: the directory itself, its direct children, and the
// largest directories and files at or below it (roadmap I4). The whole volume (record 5) uses the scan's own
// ResultSelector.Collect, so that answer is exactly dirsizer-mft's; any other directory is a walk down the selected parents.
static class SubtreeQuery
{
    public const ulong RootRecord = 5;

    public static UnifiedScanResult Query(Dictionary<ulong, FileRecord> records, string volume, FileRecord root, int top)
    {
        List<FileRecord> directories;
        List<FileRecord> files;
        List<FileRecord> rootChildren;
        long directoryCount = 0;
        long fileCount = 0;
        if (root.Reference.RecordNumber == RootRecord)
        {
            var candidates = ResultSelector.Collect(records);
            (directories, files, rootChildren) = (candidates.Directories, candidates.Files, candidates.RootChildren);
            foreach (var record in records.Values)
            {
                if (record.IsDirectory) directoryCount++;
                else fileCount++;
            }
        }
        else
        {
            directories = [root];
            files = [];
            rootChildren = [];
            directoryCount = 1;
            foreach (var record in Descendants(records, root.Reference.RecordNumber))
            {
                if (record.IsDirectory)
                {
                    directories.Add(record);
                    directoryCount++;
                }
                else
                {
                    fileCount++;
                    if (record.LogicalSize > 0) files.Add(record);
                }
                if (record.Parent.RecordNumber == root.Reference.RecordNumber) rootChildren.Add(record);
            }
            rootChildren.Sort((left, right) => ResultSelector.SizeOf(right).CompareTo(ResultSelector.SizeOf(left)));
        }
        var topDirectories = ResultSelector.SelectTop(directories, top, record => record.Size);
        var topFiles = ResultSelector.SelectTop(files, top, record => record.LogicalSize);
        return new UnifiedScanResult(
            RecordPaths.Build(volume, records, root), top,
            Item(volume, records, root), Items(volume, records, rootChildren), Items(volume, records, topDirectories), Items(volume, records, topFiles),
            directoryCount, 0, fileCount, [], "index", null, null, 0);
    }

    // Every record below `root` (not including it), following each record's selected parent. Unresolved records have
    // no parent and are below nothing. O(records) to build the child lists, then O(subtree).
    public static List<FileRecord> Descendants(Dictionary<ulong, FileRecord> records, ulong root)
    {
        var children = new Dictionary<ulong, List<FileRecord>>();
        foreach (var record in records.Values)
        {
            if (record.Reference.RecordNumber == RootRecord || record.DisplayName is null) continue;
            if (!RecordLookup.TryGetDirectory(records, record.Parent, out _)) continue;
            if (!children.TryGetValue(record.Parent.RecordNumber, out var list)) children[record.Parent.RecordNumber] = list = [];
            list.Add(record);
        }
        var result = new List<FileRecord>();
        var pending = new Stack<ulong>();
        var visited = new HashSet<ulong> { root };
        pending.Push(root);
        while (pending.Count > 0)
        {
            if (!children.TryGetValue(pending.Pop(), out var list)) continue;
            foreach (var child in list)
            {
                result.Add(child);
                if (child.IsDirectory && visited.Add(child.Reference.RecordNumber)) pending.Push(child.Reference.RecordNumber);
            }
        }
        return result;
    }

    static UnifiedItem Item(string volume, Dictionary<ulong, FileRecord> records, FileRecord record) =>
        new(RecordPaths.Build(volume, records, record), ResultSelector.SizeOf(record));

    static UnifiedItem[] Items(string volume, Dictionary<ulong, FileRecord> records, IReadOnlyList<FileRecord> list)
    {
        var items = new UnifiedItem[list.Count];
        for (var i = 0; i < list.Count; i++) items[i] = Item(volume, records, list[i]);
        return items;
    }
}
```

- [x] **Step 4: Run the tests**

Expected: `11 self-tests passed, 0 skipped.`

- [x] **Step 5: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: SubtreeQuery answers whole-volume and subtree size queries from the index"
```

---

### Task 7: Runner, output, entry point (I2: full scan, save, verify by reloading)

**Files:**
- Create: `src\DirSizer.Index\IndexRunner.cs`, `src\DirSizer.Index\IndexOutput.cs`
- Modify: `src\DirSizer.Index\IndexProgram.cs`, `src\DirSizer.Index\IndexSelfTests.Query.cs`, `src\DirSizer.Index\IndexSelfTests.cs`

- [x] **Step 1: Write the failing output test**

Append to `IndexSelfTests.Query.cs`, inside the class:

```csharp
    static IndexRun SampleRun(VerifyResult? verify = null)
    {
        var result = new UnifiedScanResult("T:\\", 25, new UnifiedItem("T:\\", 180), [new UnifiedItem("T:\\A", 170)],
            [new UnifiedItem("T:\\", 180), new UnifiedItem("T:\\A", 170)], [new UnifiedItem("T:\\A\\B\\b.bin", 100)],
            4, 0, 5, [], "index", null, null, 12.5);
        return new IndexRun(result, "full", "no saved index", "X:\\idx\\0000000000000001.dsix", 4096, true, true, 10, 7, 1000, null, verify, new IndexTimings());
    }

    static void OutputHasTheSummaryAndTheIndexObject()
    {
        var text = new StringWriter();
        var error = new StringWriter();
        IndexOutput.Write(SampleRun(), IndexOptions.Parse(["T:"]), text, error);
        AssertContains(text.ToString(), "strategy=index mode=full path=T:\\ directories=4 files=5 usn_changes=0 records_reread=0 saved=yes", "text summary line");
        AssertContains(error.ToString(), "index: full scan (no saved index)", "the reason for the full scan is on stderr");

        var json = new StringWriter();
        IndexOutput.Write(SampleRun(new VerifyResult(0, [])), IndexOptions.Parse(["T:", "--json", "--verify"]), json, new StringWriter());
        using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
        var index = document.RootElement.GetProperty("index");
        AssertEqual("full", index.GetProperty("mode").GetString(), "json index.mode");
        AssertEqual("no saved index", index.GetProperty("rebuild_reason").GetString(), "json index.rebuild_reason");
        AssertEqual(7UL, index.GetProperty("journal_id").GetUInt64(), "json index.journal_id");
        AssertEqual(0, document.RootElement.GetProperty("verify").GetProperty("differences").GetInt32(), "json verify.differences");
        AssertEqual(180L, document.RootElement.GetProperty("root").GetProperty("size").GetInt64(), "json root.size");
    }
```

Add to the test list:

```csharp
            new("output: text summary line, stderr reason, JSON index and verify objects", OutputHasTheSummaryAndTheIndexObject),
```

- [x] **Step 2: Run to see it fail**

Build. Expected: FAIL, `IndexRun`, `IndexTimings` and `IndexOutput` do not exist.

- [x] **Step 3: Implement `IndexRunner.cs` (I2 form)**

```csharp
using System.Diagnostics;

// Phase timings of one run. Phases that did not run stay zero.
sealed class IndexTimings
{
    public TimeSpan Load, Usn, Update, Scan, Recompute, Save, Query, Verify, Total;
}

// What an incremental update did (roadmap I3): Changes = USN records read; Reread = distinct MFT records read again
// (journal-named plus NTFS metadata); Replaced / Removed = index entries rewritten / dropped; ExtensionReads = extension
// records read through attribute lists. RebuildReason is set when the update could not be completed.
sealed record UpdateResult(int Changes, int Reread, int Replaced, int Removed, int ExtensionReads, string? RebuildReason);

sealed record IndexRun(
    UnifiedScanResult Result, string Mode, string? RebuildReason, string IndexPath, long IndexBytes, bool Saved, bool Stable,
    int RecordCount, ulong JournalId, long NextUsn, UpdateResult? Update, VerifyResult? Verify, IndexTimings Timings)
{
    // 0 = result written; 2 = --verify found differences; 3 = the full scan was unstable, so the index was not saved.
    public int ExitCode => Verify is { Differences: > 0 } ? 2 : Stable ? 0 : 3;
}

static class IndexRunner
{
    // I2: always a full scan; the index is saved, and --verify loads it back and compares it with that scan.
    public static IndexRun Run(IndexOptions options)
    {
        var timings = new IndexTimings();
        var total = Stopwatch.StartNew();
        var timer = new Stopwatch();
        var volume = DriveRoot.Validate(options.Target);
        BulkNative.VolumeData data;
        JournalState? journal;
        using (var handle = BulkNative.OpenVolume($"\\\\.\\{volume}"))
        {
            data = BulkNative.ReadVolumeData(handle, volume);
            // Read before the scan: whatever changes during the scan is seen again by the next update (harmless).
            journal = UsnJournal.Query(handle);
        }
        var identity = VolumeIdentity.From(data);
        var indexPath = IndexFile.PathFor(options.IndexDirectory, identity.SerialNumber);

        timer.Restart();
        var outcome = BulkScanner.Scan(new BulkScanSettings(volume, Progress: new ScanProgress()));
        timings.Scan = timer.Elapsed;
        var index = new VolumeIndex(identity, journal?.JournalId ?? 0, journal?.NextUsn ?? 0, DateTime.UtcNow, outcome.Pipeline.Records)
        {
            Relationships = outcome.Pipeline.Relationships,
        };

        var saved = false;
        long indexBytes = 0;
        if (!options.NoSave && outcome.IsStable)
        {
            timer.Restart();
            IndexFile.Save(index, indexPath);
            timings.Save = timer.Elapsed;
            saved = true;
            indexBytes = new FileInfo(indexPath).Length;
        }

        VerifyResult? verify = null;
        if (options.Verify && saved)
        {
            timer.Restart();
            var loaded = IndexFile.Read(File.ReadAllBytes(indexPath));
            timings.Load = timer.Elapsed;
            timer.Restart();
            loaded.Recompute();
            timings.Recompute = timer.Elapsed;
            timer.Restart();
            verify = IndexVerifier.Compare(loaded.Records, index.Records);
            timings.Verify = timer.Elapsed;
        }

        timer.Restart();
        var result = SubtreeQuery.Query(index.Records, volume, index.Records[SubtreeQuery.RootRecord], options.Top);
        timings.Query = timer.Elapsed;
        timings.Total = total.Elapsed;
        return new IndexRun(result with { TotalMs = timings.Total.TotalMilliseconds }, "full", null, indexPath, indexBytes, saved, outcome.IsStable,
            index.Records.Count, index.JournalId, index.NextUsn, null, verify, timings);
    }
}
```

- [x] **Step 4: Implement `IndexOutput.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

// dirsizer-index's text and JSON output. The listing has the same layout as dirsizer.exe's (UnifiedOutput); the Summary
// line and the JSON "index" object add what the index did. Warnings and the reason for a full scan go to stderr.
static class IndexOutput
{
    public static void Write(IndexRun run, IndexOptions options, TextWriter output, TextWriter error)
    {
        if (run.RebuildReason is not null) error.WriteLine($"index: full scan ({run.RebuildReason})");
        if (!run.Stable) error.WriteLine("warning: scan_stability=UNSTABLE; the MFT layout changed while it was read, so the index was not saved (exit code 3)");
        if (options.Verify)
        {
            if (run.Verify is null) error.WriteLine("verify: skipped (the index was not saved)");
            else
            {
                error.WriteLine($"verify: differences={run.Verify.Differences}");
                foreach (var sample in run.Verify.Samples) error.WriteLine($"  {sample}");
            }
        }
        if (options.Json) output.WriteLine(JsonSerializer.Serialize(ToJson(run, options.Top), IndexJsonContext.Default.JsonIndexOutput));
        else WriteText(run, options, output);
        if (options.Benchmark)
        {
            var t = run.Timings;
            error.WriteLine($"benchmark: mode={run.Mode}, records={run.RecordCount}, index_bytes={run.IndexBytes}, load_ms={Ms(t.Load):F1}, usn_ms={Ms(t.Usn):F1}, " +
                $"update_ms={Ms(t.Update):F1}, scan_ms={Ms(t.Scan):F1}, recompute_ms={Ms(t.Recompute):F1}, save_ms={Ms(t.Save):F1}, query_ms={Ms(t.Query):F1}, " +
                $"verify_ms={Ms(t.Verify):F1}, total_ms={Ms(t.Total):F1}");
        }
    }

    static void WriteText(IndexRun run, IndexOptions options, TextWriter output)
    {
        var result = run.Result;
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
        output.WriteLine();
        output.WriteLine("Summary");
        output.WriteLine($"strategy=index mode={run.Mode} path={result.Volume} directories={result.DirectoriesScanned} files={result.FileCount} " +
            $"usn_changes={run.Update?.Changes ?? 0} records_reread={run.Update?.Reread ?? 0} saved={(run.Saved ? "yes" : "no")}");
    }

    static double Ms(TimeSpan time) => time.TotalMilliseconds;

    static JsonIndexOutput ToJson(IndexRun run, int top)
    {
        var result = run.Result;
        var t = run.Timings;
        var update = run.Update;
        return new JsonIndexOutput(
            result.Volume, top, "logical", Item(result.Root), Items(result.RootChildren), Items(result.Directories), Items(result.Files),
            new JsonIndexStatistics(result.DirectoriesScanned, result.FileCount),
            new JsonIndexInfo(run.Mode, run.RebuildReason, run.IndexPath, run.Saved, run.Stable ? "stable" : "unstable", run.RecordCount, run.IndexBytes,
                run.JournalId, run.NextUsn, update?.Changes ?? 0, update?.Reread ?? 0, update?.Replaced ?? 0, update?.Removed ?? 0, update?.ExtensionReads ?? 0,
                Ms(t.Load), Ms(t.Usn), Ms(t.Update), Ms(t.Scan), Ms(t.Recompute), Ms(t.Save), Ms(t.Query), Ms(t.Verify), Ms(t.Total)),
            run.Verify is null ? null : new JsonVerify(run.Verify.Differences, run.Verify.Samples));
    }

    static JsonItem Item(UnifiedItem item) => new(item.Path, item.Size);

    static JsonItem[] Items(UnifiedItem[] items)
    {
        var result = new JsonItem[items.Length];
        for (var i = 0; i < items.Length; i++) result[i] = Item(items[i]);
        return result;
    }
}

sealed record JsonIndexOutput(string Path, int Top, string SizeMode, JsonItem Root, JsonItem[] RootChildren, JsonItem[] Directories, JsonItem[] Files,
    JsonIndexStatistics Statistics, JsonIndexInfo Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonVerify? Verify = null);
sealed record JsonIndexStatistics(long Directories, long Files);
sealed record JsonIndexInfo(string Mode, string? RebuildReason, string File, bool Saved, string ScanStability, int Records, long IndexBytes,
    ulong JournalId, long NextUsn, int UsnChanges, int RecordsReread, int RecordsReplaced, int RecordsRemoved, int ExtensionReads,
    double LoadMs, double UsnMs, double UpdateMs, double ScanMs, double RecomputeMs, double SaveMs, double QueryMs, double VerifyMs, double TotalMs);
sealed record JsonVerify(int Differences, string[] Samples);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonIndexOutput))]
partial class IndexJsonContext : JsonSerializerContext;
```

`JsonItem` is the existing `sealed record JsonItem(string Path, long Size)` from `src\Shared\Cli.cs`.

- [x] **Step 5: Wire the entry point**

In `IndexProgram.cs`, replace the last two lines:

```csharp
Console.Error.WriteLine("error: scanning is not wired in yet (Task 7 of the plan).");
return 1;
```

with:

```csharp
try
{
    var run = IndexRunner.Run(options);
    IndexOutput.Write(run, options, Console.Out, Console.Error);
    return run.ExitCode;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    if (exception is UnauthorizedAccessException || exception is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode is 5 or 1314)
        Console.Error.WriteLine("Open the terminal as Administrator. dirsizer-index reads NTFS metadata and requires volume access.");
    return 1;
}
```

- [x] **Step 6: Self-tests**

Build and run the self-tests. Expected: `12 self-tests passed, 0 skipped.`, exit 0.

- [x] **Step 7: Real volume T: (elevated)**

```powershell
$dir = Join-Path $env:TEMP 'dirsizer-index-i2'
dotnet artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll T:\ --verify "--index-dir=$dir"; "exit=$LASTEXITCODE"
```

Expected: the directory listing and `strategy=index mode=full path=T:\ ... saved=yes` on stdout, `verify:
differences=0` on stderr, `exit=0`. A file `$dir\<serial>.dsix` exists. The serial must equal the `serial number` line
of `dotnet artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll T: --volume`, without the `0x` prefix.

Then compare with `dirsizer-mft` on the same quiescent volume:

```powershell
$index = dotnet artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll T:\ --json --top=1000000 --files "--index-dir=$dir" 2>$null | ConvertFrom-Json
$mft = dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll T: --json --top=1000000 --files 2>$null | ConvertFrom-Json
"root: index=$($index.root.size) mft=$($mft.root.size)"
"directories equal: $(($index.directories | ConvertTo-Json -Compress) -eq ($mft.directories | ConvertTo-Json -Compress))"
"files equal: $(($index.files | ConvertTo-Json -Compress) -eq ($mft.files | ConvertTo-Json -Compress))"
```

Expected: equal root sizes, then `directories equal: True` and `files equal: True`. The whole-volume query uses the same
selection code on the same scan.

- [x] **Step 8: Mutation check of `--verify`**

In `IndexFile.Serialize`, change `writer.Write(record.LogicalSize);` to `writer.Write(record.LogicalSize + 1);`,
rebuild, and run the step 7 `--verify` command again. Expected: `verify: differences=N` with N > 0, samples naming
`logical size`, and `exit=2`. The self-test `LoadedIndexAggregatesLikeTheScan` must fail too. Restore the line,
rebuild, and run step 7 again: `differences=0`, `exit=0`.

- [x] **Step 9: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: full scan, save, --verify by reloading; text and JSON output"
```

---

### Task 8: I2 checkpoint: measurements on C:, design doc, roadmap

**Files:**
- Create: `docs\design_index.md`
- Modify: `docs\roadmap.md`

- [x] **Step 1: Measure on C: (elevated; the index goes to a temporary directory)**

```powershell
$dir = Join-Path $env:TEMP 'dirsizer-index-i2'
foreach ($run in 1..3) {
    $json = dotnet artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll C:\ --verify --json "--index-dir=$dir" 2>$null | ConvertFrom-Json
    "run $run exit=$LASTEXITCODE differences=$($json.verify.differences) " + ($json.index | Select-Object records, index_bytes, scan_ms, save_ms, load_ms, recompute_ms, verify_ms, total_ms | ConvertTo-Json -Compress)
}
Remove-Item -Recurse -Force $dir
```

Expected: `exit=0 differences=0` three times. On a live C:, `--verify` in I2 compares the file with the scan that wrote
it, so live changes do not matter. Record `records`, `index_bytes` and the medians of `scan_ms`, `save_ms`, `load_ms` and
`recompute_ms`.

- [x] **Step 2: Write `docs\design_index.md`**

```markdown
# dirsizer-index design

EXPERIMENTAL tool, `src\DirSizer.Index` (`dirsizer-index.exe`). Roadmap phases I2-I5 in [roadmap.md](roadmap.md);
implementation plan: [2026-09-23-persistent-index.md](superpowers/plans/2026-09-23-persistent-index.md).
Not part of the release zip.

## What is stored (I2)

One file per NTFS volume, `<volume serial number as 16 hex digits>.dsix`, by default in
`%LOCALAPPDATA%\dirsizer\index` (`--index-dir=` overrides it). It holds the merged MFT records exactly as a scan's
`RecordMerger` leaves them: file reference and header sequence number, directory flag, logical size of the unnamed
`$DATA`, and every `$FILE_NAME` (parent reference, namespace, name), in the scan's dictionary order.

Derived values (selected parent, display name, directory sizes) are not stored. After loading, the same shared
`RelationshipResolver` and `SizeAggregator` stages run again (`VolumeIndex.Recompute`), so a loaded index is aggregated
by the same code as a scan and can be compared with one record by record (`IndexVerifier`). No timestamps are stored:
nothing reported needs them.

The header holds the format version, the volume identity (serial number, MFT record size, cluster size, MFT start LCN),
the USN journal id and position the index is current up to, and the time it was written. The file ends with a SHA-256
of everything before it. The exact layout is in the comment at the top of `src\DirSizer.Index\IndexFile.cs`.

Saving writes `<file>.tmp`, flushes it to disk, then renames it over the old file. A full scan whose MFT layout changed
while it was read (exit code 3, as with `dirsizer-mft`) is never saved.

## When a saved index is not used

The file is ignored and the whole `$MFT` is scanned again when it does not exist, or when its checksum, magic or
version is wrong. The reason is printed on stderr and in JSON `index.rebuild_reason`.

## Privacy

The index lists every file and directory name on the volume, including names in directories that the user could
not list without elevation. In the default location only the user, SYSTEM and Administrators can read it.
`--index-dir` puts it somewhere else, with that directory's permissions.

## Measurements

| Volume | Records | Index file | Full scan | Save | Load | Recompute |
| --- | --- | --- | --- | --- | --- | --- |
| C: (I2, warm cache, 3 runs, medians) | <records> | <MiB> | <scan_ms> | <save_ms> | <load_ms> | <recompute_ms> |
```

Fill the table row with step 1's numbers. MiB = `index_bytes / 1MB`, one decimal.

- [x] **Step 3: Update the roadmap's I2 section**

In `docs\roadmap.md`, under `### I2 - Persistent metadata index`, tick the three items and replace their text:

```markdown
- [x] Specify the on-disk format, versioning, location, and invalidation rules: [design_index.md](design_index.md) (`src\DirSizer.Index\IndexFile.cs`, format version 1, SHA-256 trailer, `%LOCALAPPDATA%\dirsizer\index\<serial>.dsix`).
- [x] Save and load the index; the loaded result must equal a fresh scan: `dirsizer-index --verify` loads the saved file back, aggregates it with the shared stages and compares it record by record with the scan (T: and C:, 0 differences; the check was shown to fail when the file writes a wrong size). The whole-volume listing equals `dirsizer-mft --json` on T:.
- [x] Measure load time and file size against scan time on `C:`: <records> records, <MiB> MiB, full scan <scan_ms> ms, save <save_ms> ms, load <load_ms> ms, recompute <recompute_ms> ms (medians of 3, warm cache, one machine).
```

- [x] **Step 4: Regression guard and commit**

Run the three regression-guard self-tests from the ground rules. Expected: all pass.

```powershell
git add docs\design_index.md docs\roadmap.md
git commit -m "I2 checkpoint: dirsizer-index design doc and C: measurements"
```

---

# Phase I3 — incremental update from the USN journal

### Task 9: Read and parse USN records

**Files:**
- Modify: `src\DirSizer.Index\UsnJournal.cs` (replace the whole file)
- Create: `src\DirSizer.Index\IndexSelfTests.Usn.cs`
- Modify: `src\DirSizer.Index\IndexSelfTests.cs` (list)

- [x] **Step 1: Write the failing tests**

`src\DirSizer.Index\IndexSelfTests.Usn.cs`:

```csharp
using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;

static partial class IndexSelfTests
{
    // One USN_RECORD_V2 (60-byte header, UTF-16 name, padded to 8 bytes).
    static byte[] UsnRecord(ulong reference, long usn, uint reason, string name = "f.txt", ushort major = 2)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (60 + nameBytes.Length + 7) & ~7;
        var bytes = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), major);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), reference);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), Ref(5).FullReference);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(58), 60);
        nameBytes.CopyTo(bytes, 60);
        return bytes;
    }

    // One FSCTL_READ_USN_JOURNAL output buffer: the USN to continue from, then the records.
    static UsnJournal.ReadResponse Page(long next, params byte[][] records)
    {
        var size = 8;
        foreach (var record in records) size += record.Length;
        var output = new byte[size];
        BinaryPrimitives.WriteInt64LittleEndian(output, next);
        var offset = 8;
        foreach (var record in records)
        {
            record.CopyTo(output, offset);
            offset += record.Length;
        }
        return new UsnJournal.ReadResponse(0, output, size);
    }

    static void UsnRecordsAreParsed()
    {
        var changes = new List<UsnChange>();
        var page = Page(0, UsnRecord(Ref(40, 3).FullReference, 1000, 0x100), UsnRecord(Ref(41).FullReference, 1100, 0x200, "a much longer file name.txt"));
        UsnJournal.ParseRecords(page.Output.AsSpan(8), changes);
        AssertEqual(2, changes.Count, "two records");
        AssertEqual(new UsnChange(40, 1000, 0x100), changes[0], "first record: the record number without its sequence");
        AssertEqual(new UsnChange(41, 1100, 0x200), changes[1], "second record, after a longer name");
    }

    static void DamagedUsnRecordsAreErrors()
    {
        var record = UsnRecord(Ref(40).FullReference, 1, 1);
        AssertThrows<IOException>(() => UsnJournal.ParseRecords(record.AsSpan(0, 40), new List<UsnChange>()), "a record cut short");
        var badLength = (byte[])record.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badLength, 8);
        AssertThrows<IOException>(() => UsnJournal.ParseRecords(badLength, new List<UsnChange>()), "a record length below the header size");
        AssertThrows<IOException>(() => UsnJournal.ParseRecords(UsnRecord(Ref(40).FullReference, 1, 1, major: 3), new List<UsnChange>()), "a version 3 record");
    }

    static void ReadChangesFollowsTheJournalToItsEnd()
    {
        var pages = new Dictionary<long, UsnJournal.ReadResponse>
        {
            [100] = Page(200, UsnRecord(Ref(40).FullReference, 100, 1), UsnRecord(Ref(41).FullReference, 150, 1)),
            [200] = Page(300, UsnRecord(Ref(42).FullReference, 200, 1)),
            [300] = Page(300),
        };
        var changes = new List<UsnChange>();
        var next = UsnJournal.ReadChanges(start => pages[start], 100, changes);
        AssertEqual((long?)300, next, "continues from the USN after the last record");
        AssertEqual(3, changes.Count, "every record of every page");
        AssertEqual((long?)100, UsnJournal.ReadChanges(start => new UsnJournal.ReadResponse(UsnJournal.ErrorHandleEof, new byte[8], 0), 100, new List<UsnChange>()), "ERROR_HANDLE_EOF: nothing new");
    }

    static void ReadChangesReportsWrapsAndErrors()
    {
        AssertEqual((long?)null, UsnJournal.ReadChanges(start => new UsnJournal.ReadResponse(UsnJournal.ErrorJournalEntryDeleted, new byte[8], 0), 100, new List<UsnChange>()),
            "ERROR_JOURNAL_ENTRY_DELETED: the saved position is gone");
        AssertThrows<Win32Exception>(() => UsnJournal.ReadChanges(start => new UsnJournal.ReadResponse(5, new byte[8], 0), 100, new List<UsnChange>()), "any other error");
        AssertThrows<IOException>(() => UsnJournal.ReadChanges(start => Page(start, UsnRecord(Ref(40).FullReference, start, 1)), 100, new List<UsnChange>()), "records without progress");
    }
}
```

Add to the test list:

```csharp
            new("usn: V2 records are parsed to record numbers", UsnRecordsAreParsed),
            new("usn: short, undersized and non-V2 records are errors", DamagedUsnRecordsAreErrors),
            new("usn: reading follows the journal to its end", ReadChangesFollowsTheJournalToItsEnd),
            new("usn: a wrapped journal gives null; other errors and no progress throw", ReadChangesReportsWrapsAndErrors),
```

- [x] **Step 2: Run to see it fail**

Build. Expected: FAIL, `UsnChange`, `UsnJournal.ReadResponse`, `ParseRecords` and `ReadChanges` do not exist.

- [x] **Step 3: Replace `UsnJournal.cs` with the full reader**

```csharp
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The volume's USN change journal. The index uses it only to learn *which* MFT records changed; their new state is
// always read from the MFT itself (docs\design_index.md, roadmap I3).
readonly record struct JournalState(ulong JournalId, long FirstUsn, long NextUsn);

// One USN record reduced to what the index needs: whose record changed. Usn and Reason are kept for diagnostics only.
readonly record struct UsnChange(ulong RecordNumber, long Usn, uint Reason);

static class UsnJournal
{
    const uint FsctlQueryUsnJournal = 0x000900F4;
    const uint FsctlReadUsnJournal = 0x000900BB;
    const int UsnRecordV2HeaderSize = 60;
    public const int ErrorHandleEof = 38;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    // Null if the volume has no active journal (or it is being deleted). The first three fields are the same in every
    // version of USN_JOURNAL_DATA: journal id, first USN still in the journal, next USN to be written.
    public static JournalState? Query(SafeFileHandle volume)
    {
        var output = new byte[80];
        if (!DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorJournalNotActive or ErrorJournalDeleteInProgress) return null;
            throw new Win32Exception(error, $"Cannot query the USN journal (Win32 error {error})");
        }
        if (returned < 24) throw new IOException("Invalid USN journal query response");
        return new JournalState(BinaryPrimitives.ReadUInt64LittleEndian(output), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)));
    }

    public readonly record struct ReadResponse(int Error, byte[] Output, int Returned);
    public delegate ReadResponse ReadFetch(long startUsn);

    // One FSCTL_READ_USN_JOURNAL call with READ_USN_JOURNAL_DATA_V0 (so the records are USN_RECORD_V2): every reason,
    // not only on close, and never waiting for new records.
    public static ReadResponse Fetch(SafeFileHandle volume, ulong journalId, long startUsn, byte[] output)
    {
        var input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);   // ReasonMask: every reason
        // ReturnOnlyOnClose (12), Timeout (16) and BytesToWaitFor (24) stay 0.
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        var succeeded = DeviceIoControl(volume, FsctlReadUsnJournal, input, input.Length, output, output.Length, out var returned, IntPtr.Zero);
        return new ReadResponse(succeeded ? 0 : Marshal.GetLastWin32Error(), output, returned);
    }

    // Reads from startUsn to the end of the journal. Returns the USN to continue from next time, or null if the journal
    // no longer holds startUsn (it wrapped). Any other failure throws: the caller must not treat it as "nothing changed".
    public static long? ReadChanges(ReadFetch fetch, long startUsn, List<UsnChange> changes)
    {
        var usn = startUsn;
        while (true)
        {
            var response = fetch(usn);
            if (response.Error == ErrorJournalEntryDeleted) return null;
            if (response.Error == ErrorHandleEof) return usn;
            if (response.Error != 0) throw new Win32Exception(response.Error, $"Cannot read the USN journal (Win32 error {response.Error})");
            if (response.Returned < 8) throw new IOException("Invalid USN journal read response");
            var next = BinaryPrimitives.ReadInt64LittleEndian(response.Output);
            if (response.Returned == 8) return next;
            ParseRecords(response.Output.AsSpan(8, response.Returned - 8), changes);
            if (next <= usn) throw new IOException($"The USN journal read made no progress at USN {usn}");
            usn = next;
        }
    }

    // Only V2 records are expected (the V0 read request asks for them); anything else is an error, not something to skip.
    public static void ParseRecords(ReadOnlySpan<byte> data, List<UsnChange> changes)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < UsnRecordV2HeaderSize) throw new IOException("Truncated USN record");
            var length = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            if (length < UsnRecordV2HeaderSize || length > data.Length - offset) throw new IOException($"Invalid USN record length {length}");
            var major = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
            if (major != 2) throw new IOException($"Unsupported USN record version {major}");
            var reference = new FileRef(BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]));
            changes.Add(new UsnChange(reference.RecordNumber, BinaryPrimitives.ReadInt64LittleEndian(data[(offset + 24)..]), BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 40)..])));
            offset += length;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
```

- [x] **Step 4: Run the tests**

Expected: `16 self-tests passed, 0 skipped.`

- [x] **Step 5: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: read and parse the USN journal (V2 records, wrap detection)"
```

---

### Task 10: Reading records again, and their extension records

**Files:**
- Create: `src\DirSizer.Index\RecordSource.cs`, `src\DirSizer.Index\IndexSelfTests.Update.cs`
- Modify: `src\DirSizer.Index\IndexSelfTests.cs` (list)

- [x] **Step 1: Write the failing tests (and the fake record source the update tests reuse)**

`src\DirSizer.Index\IndexSelfTests.Update.cs`:

```csharp
using System.Buffers.Binary;

// A volume in memory: MFT records by number (bytes as FSCTL_GET_NTFS_FILE_RECORD returns them) and clusters by LCN.
sealed class FakeRecordSource : IRecordSource
{
    public Dictionary<ulong, byte[]> Records { get; } = new();
    public Dictionary<long, byte[]> Clusters { get; } = new();
    public long BytesPerCluster => 4096;

    public byte[]? Read(ulong recordNumber) => Records.TryGetValue(recordNumber, out var bytes) ? (byte[])bytes.Clone() : null;

    public byte[]? ReadClusters(long lcn, long clusters) => clusters == 1 && Clusters.TryGetValue(lcn, out var bytes) ? bytes : null;

    // What a full scan of these records produces: every record parsed and merged in descending record order.
    public Dictionary<ulong, FileRecord> Scan()
    {
        var numbers = new List<ulong>(Records.Keys);
        numbers.Sort();
        numbers.Reverse();
        var records = new Dictionary<ulong, FileRecord>();
        foreach (var number in numbers)
        {
            var parsed = RecordParser.Parse(number, Records[number]);
            if (parsed is not null) RecordMerger.Merge(records, parsed);
        }
        return records;
    }
}

static partial class IndexSelfTests
{
    static void SetSequence(byte[] record, ushort sequence) => BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), sequence);

    static byte[] DirBytes(ulong number, ulong parent, string name, ushort sequence = 1)
    {
        var record = RecordFixture.Record(number, directory: true);
        SetSequence(record, sequence);
        RecordFixture.AddName(record, Ref(parent).FullReference, name, 1);
        return record;
    }

    static byte[] FileBytes(ulong number, ulong parent, string name, long size, ushort sequence = 1)
    {
        var record = RecordFixture.Record(number);
        SetSequence(record, sequence);
        RecordFixture.AddName(record, Ref(parent).FullReference, name, 1);
        RecordFixture.AddData(record, size);
        return record;
    }

    static byte[] ExtensionBytes(ulong number, ulong baseNumber, ulong parent, string name, long size)
    {
        var record = RecordFixture.Record(number, baseReference: Ref(baseNumber).FullReference);
        RecordFixture.AddName(record, Ref(parent).FullReference, name, 2);
        RecordFixture.AddData(record, size);
        return record;
    }

    // $ATTRIBUTE_LIST entries (32 bytes each, for $DATA), naming the given MFT records.
    static void WriteListEntries(byte[] target, int start, ulong[] segments)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            var entry = start + 32 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(entry), 0x80);
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(entry + 4), 32);
            target[entry + 7] = 26;
            BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(entry + 16), Ref(segments[i]).FullReference);
        }
    }

    static void AddAttributeList(byte[] record, params ulong[] segments)
    {
        var offset = RecordFixture.NextAttribute(record);
        var valueLength = 32 * segments.Length;
        var length = RecordFixture.Align8(24 + valueLength);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16), (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
        WriteListEntries(record, offset + 24, segments);
        RecordFixture.EndAttribute(record, offset + length);
    }

    // A non-resident $ATTRIBUTE_LIST in one cluster at `lcn` (below 128, so its one-byte signed run offset is positive).
    static void AddNonResidentAttributeList(byte[] record, FakeRecordSource source, long lcn, params ulong[] segments)
    {
        var offset = RecordFixture.NextAttribute(record);
        var dataSize = 32 * segments.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), 72);
        record[offset + 8] = 1;                                                                        // non-resident
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 32), 64);                      // run list offset
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 40), source.BytesPerCluster);   // allocated size
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 48), dataSize);                 // data size
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 56), dataSize);                 // initialized size
        record[offset + 64] = 0x11;         // one length byte, one offset byte
        record[offset + 65] = 1;            // one cluster
        record[offset + 66] = (byte)lcn;
        RecordFixture.EndAttribute(record, offset + 72);
        var cluster = new byte[source.BytesPerCluster];
        WriteListEntries(cluster, 0, segments);
        source.Clusters[lcn] = cluster;
    }

    static void ResidentAttributeListNamesExtensionRecords()
    {
        var source = new FakeRecordSource();
        var record = RecordFixture.Record(50);
        AddAttributeList(record, 50, 51, 52, 51);
        RecordFixture.AddName(record, Ref(5).FullReference, "big.bin", 1);
        AssertEqual("51,52", string.Join(',', AttributeList.ExtensionRecords(record, 50, source)!), "the other records, once each, without the base record");
        AssertEqual("", string.Join(',', AttributeList.ExtensionRecords(FileBytes(60, 5, "plain.bin", 1), 60, source)!), "no attribute list: none");
    }

    static void DamagedAttributeListIsNotGuessed()
    {
        var record = RecordFixture.Record(50);
        AddAttributeList(record, 51);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48 + 24 + 4), 8);   // first entry's length below the minimum
        Assert(AttributeList.ExtensionRecords(record, 50, new FakeRecordSource()) is null, "a damaged entry gives null");
    }

    static void NonResidentAttributeListIsReadFromItsClusters()
    {
        var source = new FakeRecordSource();
        var record = RecordFixture.Record(50);
        AddNonResidentAttributeList(record, source, 10, 50, 53);
        AssertEqual("53", string.Join(',', AttributeList.ExtensionRecords(record, 50, source)!), "entries read from LCN 10");
        source.Clusters.Clear();
        Assert(AttributeList.ExtensionRecords(record, 50, source) is null, "unreadable clusters give null");
    }
}
```

Add to the test list:

```csharp
            new("attribute list: a resident list names the extension records", ResidentAttributeListNamesExtensionRecords),
            new("attribute list: a damaged entry is not guessed", DamagedAttributeListIsNotGuessed),
            new("attribute list: a non-resident list is read through its run list", NonResidentAttributeListIsReadFromItsClusters),
```

- [x] **Step 2: Run to see it fail**

Build. Expected: FAIL, `IRecordSource` and `AttributeList` do not exist.

- [x] **Step 3: Implement `RecordSource.cs`**

```csharp
using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

// Reads one MFT record as it is now. Read returns the record's bytes (update-sequence fixup already applied), or null if
// that record number is not in use. Behind an interface so IndexUpdater can be tested without a volume.
interface IRecordSource
{
    byte[]? Read(ulong recordNumber);
    byte[]? ReadClusters(long lcn, long clusters);
    long BytesPerCluster { get; }
}

// The real source: FSCTL_GET_NTFS_FILE_RECORD (the reference reader's call) for records, raw volume reads for clusters.
sealed class FsctlRecordSource(SafeFileHandle volume, int recordSize, long bytesPerCluster) : IRecordSource
{
    readonly byte[] input = new byte[8];
    readonly byte[] output = new byte[Math.Max(4096, recordSize + 16)];

    public long BytesPerCluster => bytesPerCluster;

    // The FSCTL returns the nearest in-use record at or below the requested number, so any other number means "not in use".
    public byte[]? Read(ulong recordNumber)
    {
        var record = Native.ReadRecord(volume, recordNumber, input, output);
        if (record is null || record.Value.ReturnedRecordNumber != recordNumber) return null;
        return record.Value.Buffer.AsSpan(record.Value.Offset, record.Value.Length).ToArray();
    }

    public byte[]? ReadClusters(long lcn, long clusters)
    {
        try
        {
            var buffer = new byte[checked(clusters * bytesPerCluster)];
            BulkNative.ReadAt(volume, buffer, buffer.Length, checked(lcn * bytesPerCluster));
            return buffer;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or OverflowException) { return null; }
    }
}

// Finds the extension records of a base record from its $ATTRIBUTE_LIST (type 0x20). A record without an attribute list
// has none. A non-resident list is read through its run list (RunList.cs, shared with dirsizer-inspect). Returns null
// if the list cannot be read or is damaged: the caller then falls back to a full scan instead of guessing.
static class AttributeList
{
    const int MaxNonResidentBytes = 4 * 1024 * 1024;
    const int MinEntryLength = 26;

    public static SortedSet<ulong>? ExtensionRecords(byte[] record, ulong baseRecord, IRecordSource source)
    {
        var result = new SortedSet<ulong>();
        if (record.Length < 24) return null;
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
        while (offset + 4 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset));
            if (type == uint.MaxValue) return result;
            if (offset + 16 > record.Length) return null;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4));
            if (length < 16 || offset + (long)length > record.Length) return null;
            if (type == 0x20)
            {
                if (record[offset + 8] == 0)
                {
                    if (length < 24) return null;
                    var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 16));
                    var valueStart = offset + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 20));
                    if (valueLength < 0 || valueStart + (long)valueLength > offset + length) return null;
                    if (!AddEntries(record, valueStart, valueLength, baseRecord, result)) return null;
                }
                else
                {
                    var list = ReadNonResident(record, offset, source);
                    if (list is null || !AddEntries(list, 0, list.Length, baseRecord, result)) return null;
                }
            }
            offset += (int)length;
        }
        return null;   // no end marker
    }

    static bool AddEntries(byte[] list, int start, int size, ulong baseRecord, SortedSet<ulong> result)
    {
        var position = start;
        var end = start + size;
        while (position < end)
        {
            if (end - position < MinEntryLength) return false;
            var entryLength = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(position + 4));
            if (entryLength < MinEntryLength || position + entryLength > end) return false;
            var record = BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(position + 16)) & 0x0000FFFFFFFFFFFFUL;
            if (record != baseRecord) result.Add(record);
            position += entryLength;
        }
        return true;
    }

    static byte[]? ReadNonResident(byte[] record, int attributeOffset, IRecordSource source)
    {
        if (attributeOffset + 64 > record.Length) return null;
        var size = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attributeOffset + 48));
        if (size <= 0 || size > MaxNonResidentBytes) return null;
        var runs = RunList.Decode(record, attributeOffset);
        if (runs is null) return null;
        var stream = new byte[size];
        var filled = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var take = (int)Math.Min(clusters * source.BytesPerCluster, stream.Length - filled);
            if (take <= 0) break;
            if (lcn < 0) return null;   // an attribute list is never sparse
            var data = source.ReadClusters(lcn, (take + source.BytesPerCluster - 1) / source.BytesPerCluster);
            if (data is null || data.Length < take) return null;
            Array.Copy(data, 0, stream, filled, take);
            filled += take;
        }
        return filled == stream.Length ? stream : null;
    }
}
```

- [x] **Step 4: Run the tests**

Expected: `19 self-tests passed, 0 skipped.`

- [x] **Step 5: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: read records again through FSCTL and find extension records via \$ATTRIBUTE_LIST"
```

---

### Task 11: `IndexUpdater`: apply journal changes

**Files:**
- Create: `src\DirSizer.Index\IndexUpdater.cs`
- Modify: `src\DirSizer.Index\IndexSelfTests.Update.cs`, `src\DirSizer.Index\IndexSelfTests.cs` (list)

- [x] **Step 1: Write the failing tests**

Append inside `static partial class IndexSelfTests` in `IndexSelfTests.Update.cs`:

```csharp
    // Root 5; A (30) with B (31) holding b.bin (40, 100) and a.bin (41, 20); r.bin (43, 3) at the root.
    static FakeRecordSource SampleVolume()
    {
        var source = new FakeRecordSource();
        source.Records[5] = DirBytes(5, 5, ".");
        source.Records[30] = DirBytes(30, 5, "A");
        source.Records[31] = DirBytes(31, 30, "B");
        source.Records[40] = FileBytes(40, 31, "b.bin", 100);
        source.Records[41] = FileBytes(41, 30, "a.bin", 20);
        source.Records[43] = FileBytes(43, 5, "r.bin", 3);
        return source;
    }

    // Applies the changes, then checks the index against what a full scan of the changed volume produces.
    static UpdateResult ApplyAndCompare(FakeRecordSource source, Dictionary<ulong, FileRecord> records, params ulong[] changed)
    {
        var changes = new List<UsnChange>();
        foreach (var number in changed) changes.Add(new UsnChange(number, 0, 0));
        var result = IndexUpdater.Apply(records, changes, new SortedSet<ulong>(), source);
        AssertEqual((string?)null, result.RebuildReason, "no rebuild needed");
        var verify = IndexVerifier.Compare(Aggregated(records).Records, Aggregated(source.Scan()).Records);
        AssertEqual(0, verify.Differences, $"differences from a fresh scan ({string.Join("; ", verify.Samples)})");
        return result;
    }

    static void UpdateAppliesCreateModifyDeleteRenameAndMove()
    {
        var source = SampleVolume();
        var records = source.Scan();
        source.Records[41] = FileBytes(41, 30, "a.bin", 90);    // modified: 20 -> 90 bytes
        source.Records.Remove(43);                               // deleted
        source.Records[44] = FileBytes(44, 31, "new.bin", 5);    // created in B
        source.Records[32] = DirBytes(32, 5, "C");               // new directory C
        source.Records[31] = DirBytes(31, 32, "B-renamed");      // B renamed and moved from A into C, with its content
        var result = ApplyAndCompare(source, records, 41, 43, 44, 32, 31, 41);
        AssertEqual(6, result.Changes, "journal entries");
        AssertEqual(5, result.Reread, "distinct records read again (41 is in the journal twice)");
        AssertEqual(1, result.Removed, "r.bin removed");
        AssertEqual(4, result.Replaced, "41, 44, 32, 31 rewritten");
        var index = Aggregated(records);
        AssertEqual(195L, index.Records[5].Size, "root: a.bin 90 + b.bin 100 + new.bin 5");
        AssertEqual(105L, index.Records[32].Size, "C: the moved B with b.bin and new.bin");
        AssertEqual(90L, index.Records[30].Size, "A: only a.bin is left");
    }

    static void UpdateSeesAReusedRecordAsANewFile()
    {
        var source = SampleVolume();
        var records = source.Scan();
        source.Records[43] = FileBytes(43, 30, "other.bin", 9, sequence: 2);   // r.bin deleted, its record reused
        ApplyAndCompare(source, records, 43);
        AssertEqual((ushort)2, records[43].Reference.SequenceNumber, "the new sequence number");
    }

    static void UpdateReadsExtensionRecordsThroughTheAttributeList()
    {
        var source = SampleVolume();
        var baseRecord = FileBytes(50, 30, "big.bin", 0);
        AddAttributeList(baseRecord, 50, 51);
        source.Records[50] = baseRecord;
        source.Records[51] = ExtensionBytes(51, 50, 30, "BIG~1.BIN", 10);
        var records = source.Scan();
        source.Records[51] = ExtensionBytes(51, 50, 30, "BIG~1.BIN", 70);   // the data lives in the extension record
        var result = ApplyAndCompare(source, records, 50);                 // the journal names only the base record
        AssertEqual(1, result.ExtensionReads, "extension record 51 read through the attribute list");
        AssertEqual(70L, records[50].LogicalSize, "the size from the extension record");
    }

    static void UpdateDropsARecordThatBecameAnExtension()
    {
        var source = SampleVolume();
        var records = source.Scan();
        source.Records[43] = ExtensionBytes(43, 41, 30, "A~1.BIN", 0);   // record 43 is now an extension record of a.bin
        var baseRecord = FileBytes(41, 30, "a.bin", 20);
        AddAttributeList(baseRecord, 41, 43);
        source.Records[41] = baseRecord;
        ApplyAndCompare(source, records, 43, 41);
        Assert(!records.ContainsKey(43), "43 is no longer an entry of its own");
    }

    static void UnreadableExtensionAsksForARebuild()
    {
        var source = SampleVolume();
        var records = source.Scan();
        var baseRecord = FileBytes(50, 30, "big.bin", 0);
        AddAttributeList(baseRecord, 50, 51);
        source.Records[50] = baseRecord;   // record 51 does not exist
        var result = IndexUpdater.Apply(records, [new UsnChange(50, 0, 0)], new SortedSet<ulong>(), source);
        AssertContains(result.RebuildReason, "51", "the rebuild reason names the missing extension record");
    }

    static void MetadataRecordsAreAlwaysReread()
    {
        var source = SampleVolume();
        source.Records[0] = FileBytes(0, 5, "$MFT", 100);
        source.Records[11] = DirBytes(11, 5, "$Extend");
        source.Records[24] = DirBytes(24, 11, "$RmMetadata");
        source.Records[25] = FileBytes(25, 24, "$Repair", 1);
        var records = source.Scan();
        var metadata = IndexUpdater.MetadataRecords(records);
        Assert(metadata.Contains(0) && metadata.Contains(11) && metadata.Contains(23), "records 0-23 are metadata");
        Assert(metadata.Contains(24) && metadata.Contains(25), "the $Extend tree is metadata");
        Assert(!metadata.Contains(30) && !metadata.Contains(43), "user directories and files are not");
        source.Records[0] = FileBytes(0, 5, "$MFT", 200);   // $MFT grew; NTFS writes no USN record for that
        IndexUpdater.Apply(records, [], metadata, source);
        AssertEqual(200L, records[0].LogicalSize, "the $MFT size is current without a journal entry");
    }
```

Add to the test list:

```csharp
            new("update: create, modify, delete, rename and move equal a fresh scan", UpdateAppliesCreateModifyDeleteRenameAndMove),
            new("update: a reused record is a new file", UpdateSeesAReusedRecordAsANewFile),
            new("update: extension records are read through the base record's attribute list", UpdateReadsExtensionRecordsThroughTheAttributeList),
            new("update: a record that became an extension record is dropped", UpdateDropsARecordThatBecameAnExtension),
            new("update: an unreadable extension record asks for a full scan", UnreadableExtensionAsksForARebuild),
            new("update: NTFS metadata records are read again without journal entries", MetadataRecordsAreAlwaysReread),
```

- [x] **Step 2: Run to see it fail**

Build. Expected: FAIL, `IndexUpdater` does not exist.

- [x] **Step 3: Implement `IndexUpdater.cs`**

```csharp
// Brings a loaded index up to date: every record the USN journal names (plus the NTFS metadata records, which change
// without journal entries) is read again from the MFT and replaces or removes its index entry. The journal only says
// *which* records to look at; their new state always comes from the MFT (docs\design_index.md, roadmap I3). Applying a
// change twice is therefore harmless.
static class IndexUpdater
{
    const ulong FirstUserRecord = 24;   // records 0-23 are reserved for NTFS metadata
    const ulong ExtendDirectory = 11;

    public static UpdateResult Apply(Dictionary<ulong, FileRecord> records, List<UsnChange> changes, SortedSet<ulong> alwaysReread, IRecordSource source)
    {
        var numbers = new SortedSet<ulong>(alwaysReread);
        foreach (var change in changes) numbers.Add(change.RecordNumber);
        var replaced = 0;
        var removed = 0;
        var extensionReads = 0;
        // Descending, like a scan. Each entry depends only on its own MFT records, so the order does not change the result.
        foreach (var number in numbers.Reverse())
        {
            var bytes = source.Read(number);
            var parsed = bytes is null ? null : RecordParser.Parse(number, bytes);
            if (parsed is null || parsed.BaseReference.RecordNumber != 0)
            {
                // Not in use any more, not parseable (a scan skips it too), or now an extension record of another file
                // (that file's own journal entry brings it in): in every case no longer an entry of its own.
                if (records.Remove(number)) removed++;
                continue;
            }
            var extensions = AttributeList.ExtensionRecords(bytes!, number, source);
            if (extensions is null) return Failed($"the attribute list of MFT record {number} could not be read");
            var parts = new List<ParsedRecord> { parsed };
            foreach (var extension in extensions)
            {
                extensionReads++;
                var extensionBytes = source.Read(extension);
                var extensionParsed = extensionBytes is null ? null : RecordParser.Parse(extension, extensionBytes);
                if (extensionParsed is null || extensionParsed.BaseReference.RecordNumber != number)
                    return Failed($"extension record {extension} of MFT record {number} could not be read");
                parts.Add(extensionParsed);
            }
            // The order a full scan meets them in (descending record number), so the names end up in the same order.
            parts.Sort((left, right) => right.Reference.RecordNumber.CompareTo(left.Reference.RecordNumber));
            var merged = new Dictionary<ulong, FileRecord>();
            foreach (var part in parts) RecordMerger.Merge(merged, part);
            records[number] = merged[number];
            replaced++;
        }
        return new UpdateResult(changes.Count, numbers.Count, replaced, removed, extensionReads, null);

        UpdateResult Failed(string reason) => new(changes.Count, numbers.Count, replaced, removed, extensionReads, reason);
    }

    // Records 0-23 and every record in the $Extend tree ($ObjId, $Quota, $Reparse, $UsnJrnl, $RmMetadata...). Their
    // changes, such as $MFT growing, are not written to the USN journal, so they are read again on every update.
    public static SortedSet<ulong> MetadataRecords(Dictionary<ulong, FileRecord> records)
    {
        var result = new SortedSet<ulong>();
        for (ulong number = 0; number < FirstUserRecord; number++) result.Add(number);
        bool added;
        do
        {
            added = false;
            foreach (var record in records.Values)
            {
                if (result.Contains(record.Reference.RecordNumber)) continue;
                foreach (var name in record.Names)
                {
                    var parent = name.Parent.RecordNumber;
                    if (parent == ExtendDirectory || (parent >= FirstUserRecord && result.Contains(parent)))
                    {
                        result.Add(record.Reference.RecordNumber);
                        added = true;
                        break;
                    }
                }
            }
        } while (added);
        return result;
    }
}
```

- [x] **Step 4: Run the tests**

Expected: `25 self-tests passed, 0 skipped.`

- [x] **Step 5: Mutation check**

In `IndexUpdater.Apply`, delete the line `parts.Sort(...)`, rebuild and run. Expected: `FAIL  update: extension
records are read through the base record's attribute list` with a `name 0` difference. The extension's DOS name
must come first, as in the scan. Restore the line: 25 passed.

- [x] **Step 6: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: IndexUpdater applies journal-named records from the MFT, plus NTFS metadata records"
```

---

### Task 12: Validity rules and the incremental runner

**Files:**
- Create: `src\DirSizer.Index\IndexValidity.cs`
- Modify: `src\DirSizer.Index\IndexRunner.cs` (replace the whole file), `src\DirSizer.Index\IndexOptions.cs` (replace the whole file), `src\DirSizer.Index\IndexSelfTests.Update.cs`, `src\DirSizer.Index\IndexSelfTests.cs`

- [x] **Step 1: Write the failing tests**

Append to `IndexSelfTests.Update.cs`, inside the class:

```csharp
    static void ValidityRulesDecideBetweenUpdateAndRebuild()
    {
        var index = new VolumeIndex(TestIdentity, 7, 1000, DateTime.UnixEpoch, new Dictionary<ulong, FileRecord>());
        var journal = new JournalState(7, 500, 2000);
        AssertEqual((string?)null, IndexValidity.Check(index, TestIdentity, journal), "same volume, same journal, position still held: update");
        AssertEqual((string?)null, IndexValidity.Check(index, TestIdentity, journal with { FirstUsn = 1000, NextUsn = 1000 }), "the edges are inclusive");
        AssertContains(IndexValidity.Check(index, TestIdentity with { SerialNumber = 1 }, journal), "not the one", "another volume");
        AssertContains(IndexValidity.Check(index, TestIdentity with { RecordSize = 4096 }, journal), "not the one", "another geometry");
        AssertContains(IndexValidity.Check(index, TestIdentity, null), "no active USN journal", "journal disabled");
        AssertContains(IndexValidity.Check(index, TestIdentity, journal with { JournalId = 8 }), "recreated", "journal recreated");
        AssertContains(IndexValidity.Check(index, TestIdentity, journal with { FirstUsn = 1001 }), "wrapped", "saved position no longer in the journal");
        AssertContains(IndexValidity.Check(index, TestIdentity, journal with { NextUsn = 999 }), "ahead", "saved position after the journal's end");
        index.JournalId = 0;
        AssertContains(IndexValidity.Check(index, TestIdentity, journal), "without a USN journal", "index written without a journal");
    }
```

Add to the test list:

```csharp
            new("validity: identity and journal rules decide between update and full scan", ValidityRulesDecideBetweenUpdateAndRebuild),
```

In `OptionsDefaultsAndFlags` (`IndexSelfTests.cs`), change
`Assert(!defaults.Files && !defaults.Json && !defaults.Benchmark && !defaults.NoSave && !defaults.Verify, "default flags");`
to
`Assert(!defaults.Files && !defaults.Json && !defaults.Benchmark && !defaults.NoSave && !defaults.Verify && !defaults.Rebuild, "default flags");`
and add after the `--no-save` line:

```csharp
        Assert(IndexOptions.Parse(["D:\\", "--rebuild"]).Rebuild, "--rebuild");
        Assert(IndexOptions.Parse(["D:\\", "--verify", "--no-save"]).Verify, "--verify with --no-save compares with a fresh scan");
```

In `OptionsRejectBadInput`, delete the line with `"--verify needs the saved index"`.

- [x] **Step 2: Run to see it fail**

Build. Expected: FAIL, `IndexValidity` and `IndexOptions.Rebuild` do not exist.

- [x] **Step 3: Implement `IndexValidity.cs`**

```csharp
// Whether a loaded index may be brought up to date from the journal. Null means yes; otherwise the reason a full scan
// is needed (shown on stderr and in JSON index.rebuild_reason). Order matters: the first failing rule is the reason.
static class IndexValidity
{
    public static string? Check(VolumeIndex index, VolumeIdentity identity, JournalState? journal)
    {
        if (index.Identity != identity) return "the volume is not the one the index was written for (serial number or geometry changed)";
        if (journal is null) return "the volume has no active USN journal";
        if (index.JournalId == 0) return "the index was written without a USN journal";
        if (index.JournalId != journal.Value.JournalId) return "the USN journal was recreated since the index was written";
        if (index.NextUsn < journal.Value.FirstUsn) return "the USN journal no longer holds the saved position (it wrapped)";
        if (index.NextUsn > journal.Value.NextUsn) return "the saved USN position is ahead of the journal";
        return null;
    }
}
```

- [x] **Step 4: Replace `IndexOptions.cs`**

```csharp
// dirsizer-index's options. EXPERIMENTAL tool; see docs\design_index.md.
sealed class IndexOptions
{
    public string Target { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Json { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool NoSave { get; private set; }
    public bool Verify { get; private set; }
    public bool Rebuild { get; private set; }
    public string IndexDirectory { get; private set; } = IndexFile.DefaultDirectory;

    public static IndexOptions Parse(string[] args)
    {
        var result = new IndexOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--self-test") { result.SelfTest = true; continue; }
            if (arg == "--benchmark") { result.Benchmark = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg == "--no-save") { result.NoSave = true; continue; }
            if (arg == "--verify") { result.Verify = true; continue; }
            if (arg == "--rebuild") { result.Rebuild = true; continue; }
            if (arg.StartsWith("--index-dir=", StringComparison.Ordinal) && arg.Length > "--index-dir=".Length) { result.IndexDirectory = Path.GetFullPath(arg["--index-dir=".Length..]); continue; }
            if (arg.StartsWith("--index-dir", StringComparison.Ordinal)) throw new ArgumentException("Use --index-dir=DIRECTORY.");
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("--top needs a whole number: --top=N or --top N.");
            if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Target.Length != 0) throw new ArgumentException("Only one path is supported.");
            // "C:" alone would mean the current directory on C:; it is taken as the drive root, as the other tools do.
            result.Target = arg.Length == 2 && arg[1] == ':' ? arg + "\\" : arg;
        }
        if (!result.Help && !result.SelfTest && result.Target.Length == 0) throw new ArgumentException("A drive root is required, for example C:\\.");
        return result;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            dirsizer-index - EXPERIMENTAL: folder sizes from a per-volume NTFS index kept up to date by the USN journal

            Usage: dirsizer-index C:\ [--top=N] [--files] [--json] [--rebuild] [--verify] [--no-save] [--index-dir=DIR] [--benchmark]

            --top=N          Show the largest N results (default: 25)
            --files          Include largest files
            --json           Write machine-readable JSON to stdout
            --rebuild        Ignore the saved index and scan the whole $MFT again
            --verify         Also scan the whole $MFT and compare it with the index, record by record
                             (exit code 2 if they differ; use on a volume nothing else is writing to)
            --no-save        Do not write the index back
            --index-dir=DIR  Where index files are kept (default: %LOCALAPPDATA%\dirsizer\index)
            --benchmark      Print phase timings to stderr
            --self-test      Run the built-in tests
            -h               Show this help

            The first run reads the whole $MFT (like dirsizer-mft) and saves an index of the volume. Later
            runs load it, read the USN change journal from where it left off, and read again only the MFT
            records the journal names. A full scan runs instead, with the reason on stderr, when there is no
            usable index: none yet, another volume, the journal was recreated, wrapped or disabled, or the
            file is damaged. Changes made while the volume was used by a system that does not write the USN
            journal (another operating system) are not seen: use --rebuild after that.

            The index file lists every file and directory name on the volume. In the default location only
            the current user, SYSTEM and Administrators can read it.
            Requires Administrator: the executable asks for elevation when it starts.

            Exit codes: 0 ok; 1 error; 2 --verify found differences; 3 the MFT changed while it was read,
            so the index was not saved.
            """);
    }
}
```

- [x] **Step 5: Replace `IndexRunner.cs`**

```csharp
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

// Phase timings of one run. Phases that did not run stay zero.
sealed class IndexTimings
{
    public TimeSpan Load, Usn, Update, Scan, Recompute, Save, Query, Verify, Total;
}

// What an incremental update did (roadmap I3): Changes = USN records read; Reread = distinct MFT records read again
// (journal-named plus NTFS metadata); Replaced / Removed = index entries rewritten / dropped; ExtensionReads = extension
// records read through attribute lists. RebuildReason is set when the update could not be completed.
sealed record UpdateResult(int Changes, int Reread, int Replaced, int Removed, int ExtensionReads, string? RebuildReason);

sealed record IndexRun(
    UnifiedScanResult Result, string Mode, string? RebuildReason, string IndexPath, long IndexBytes, bool Saved, bool Stable,
    int RecordCount, ulong JournalId, long NextUsn, UpdateResult? Update, VerifyResult? Verify, IndexTimings Timings)
{
    // 0 = result written; 2 = --verify found differences; 3 = the full scan was unstable, so the index was not saved.
    public int ExitCode => Verify is { Differences: > 0 } ? 2 : Stable ? 0 : 3;
}

static class IndexRunner
{
    public static IndexRun Run(IndexOptions options)
    {
        var timings = new IndexTimings();
        var total = Stopwatch.StartNew();
        var timer = new Stopwatch();
        var volume = DriveRoot.Validate(options.Target);
        using var handle = BulkNative.OpenVolume($"\\\\.\\{volume}");
        var data = BulkNative.ReadVolumeData(handle, volume);
        var identity = VolumeIdentity.From(data);
        // Read before anything else: whatever changes after this point is seen again by the next run (harmless).
        var journal = UsnJournal.Query(handle);
        var indexPath = IndexFile.PathFor(options.IndexDirectory, identity.SerialNumber);

        VolumeIndex? index = null;
        UpdateResult? update = null;
        string? reason;
        if (options.Rebuild) reason = "--rebuild was given";
        else
        {
            timer.Restart();
            index = IndexFile.TryLoad(indexPath, out reason);
            timings.Load = timer.Elapsed;
            if (index is not null) reason = IndexValidity.Check(index, identity, journal);
            if (index is not null && reason is null) reason = Update(index, handle, data, journal!.Value, timings, out update);
            if (reason is not null) index = null;
        }

        var mode = "incremental";
        var stable = true;
        if (index is null)
        {
            mode = "full";
            timer.Restart();
            var outcome = BulkScanner.Scan(new BulkScanSettings(volume, Progress: new ScanProgress()));
            timings.Scan = timer.Elapsed;
            stable = outcome.IsStable;
            index = new VolumeIndex(identity, journal?.JournalId ?? 0, journal?.NextUsn ?? 0, DateTime.UtcNow, outcome.Pipeline.Records)
            {
                Relationships = outcome.Pipeline.Relationships,
            };
        }

        var saved = false;
        long indexBytes = 0;
        if (!options.NoSave && stable)
        {
            timer.Restart();
            IndexFile.Save(index, indexPath);
            timings.Save = timer.Elapsed;
            saved = true;
            indexBytes = new FileInfo(indexPath).Length;
        }

        timer.Restart();
        var result = SubtreeQuery.Query(index.Records, volume, index.Records[SubtreeQuery.RootRecord], options.Top);
        timings.Query = timer.Elapsed;

        VerifyResult? verify = null;
        if (options.Verify)
        {
            // The index as it is now must equal a fresh full scan (on a quiescent volume; a live one changes in between).
            timer.Restart();
            var fresh = BulkScanner.Scan(new BulkScanSettings(volume, Progress: new ScanProgress()));
            verify = IndexVerifier.Compare(index.Records, fresh.Pipeline.Records);
            timings.Verify = timer.Elapsed;
        }
        timings.Total = total.Elapsed;
        return new IndexRun(result with { TotalMs = timings.Total.TotalMilliseconds }, mode, reason, indexPath, indexBytes, saved, stable,
            index.Records.Count, index.JournalId, index.NextUsn, update, verify, timings);
    }

    // Returns null when the index is now current, or the reason a full scan is needed instead.
    static string? Update(VolumeIndex index, SafeFileHandle handle, BulkNative.VolumeData data, JournalState journal, IndexTimings timings, out UpdateResult? update)
    {
        update = null;
        var timer = Stopwatch.StartNew();
        var changes = new List<UsnChange>();
        var buffer = new byte[1 << 20];
        var next = UsnJournal.ReadChanges(start => UsnJournal.Fetch(handle, journal.JournalId, start, buffer), index.NextUsn, changes);
        timings.Usn = timer.Elapsed;
        if (next is null) return "the USN journal no longer holds the saved position (it wrapped)";

        timer.Restart();
        update = IndexUpdater.Apply(index.Records, changes, IndexUpdater.MetadataRecords(index.Records), new FsctlRecordSource(handle, data.RecordSize, data.BytesPerCluster));
        timings.Update = timer.Elapsed;
        if (update.RebuildReason is not null) return update.RebuildReason;

        index.NextUsn = next.Value;
        index.WrittenUtc = DateTime.UtcNow;
        timer.Restart();
        index.Recompute();
        timings.Recompute += timer.Elapsed;
        return null;
    }
}
```

The I2 meaning of `--verify` (load the file back and compare) is still covered: every incremental run loads the file,
and `--verify` then compares the loaded and updated index with a fresh scan.

- [x] **Step 6: Run the tests**

Expected: `26 self-tests passed, 0 skipped.`

- [x] **Step 7: First real incremental run on T: (elevated)**

```powershell
fsutil usn queryjournal T: > $null 2>&1; if ($LASTEXITCODE -ne 0) { fsutil usn createjournal m=33554432 a=4194304 T: }
$dir = Join-Path $env:TEMP 'dirsizer-index-i3'
$tool = 'artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll'
dotnet $tool T:\ "--index-dir=$dir" --benchmark > $null; "first exit=$LASTEXITCODE"
New-Item -ItemType Directory -Force T:\index-smoke > $null; [IO.File]::WriteAllBytes('T:\index-smoke\x.bin', [byte[]]::new(12345))
dotnet $tool T:\ "--index-dir=$dir" --verify --benchmark > $null; "second exit=$LASTEXITCODE"
Remove-Item -Recurse -Force T:\index-smoke, $dir
```

Expected on stderr: first `index: full scan (no saved index)` and `benchmark: mode=full`. Then `benchmark:
mode=incremental`, `verify: differences=0`, and `second exit=0`. Creating the journal on T: is fine: T: is the
disposable test volume.

- [x] **Step 8: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: incremental runs from the USN journal with documented fallbacks to a full scan"
```

---

### Task 13: End-to-end test on T:

**Files:**
- Create: `scripts\Test-IndexIncremental.ps1`

- [x] **Step 1: Write the script**

```powershell
<#
.SYNOPSIS
  End-to-end test of dirsizer-index on a disposable NTFSTEST volume.

.DESCRIPTION
  Covers the full scan, incremental updates after real changes (checked against a fresh scan with --verify), and the
  fallbacks to a full scan: journal recreated, journal disabled, damaged index file, --rebuild.
  Writes only inside <Volume>\index-test. It deletes and recreates the volume's USN journal to test the fallbacks; at
  the end the journal is active (with a new id). Index files go to a temporary directory. Refuses volumes that are not
  NTFS or not labelled NTFSTEST (-AllowAnyLabel overrides). Needs an elevated shell and a Release build. Nothing else
  may write to the volume while it runs, because --verify compares with a fresh scan. fsutil output is localized, so
  only its exit codes are used.
  Exit code 0 only if every check passed.
#>
param(
    [string]$Volume = 'T:',
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll'),
    [string]$FsTool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer-fs.dll'),
    [switch]$AllowAnyLabel
)
$ErrorActionPreference = 'Stop'
$Volume = $Volume.TrimEnd('\')
$vol = Get-Volume -DriveLetter $Volume.TrimEnd(':')
if ($vol.FileSystem -ne 'NTFS') { throw "$Volume is not NTFS." }
if (-not $AllowAnyLabel -and $vol.FileSystemLabel -ne 'NTFSTEST') { throw "$Volume is labelled '$($vol.FileSystemLabel)', not NTFSTEST. Pass -AllowAnyLabel to override." }

$root = "$Volume\index-test"
$indexDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dirsizer-index-test-' + [guid]::NewGuid().ToString('N'))
$failures = 0

function Invoke-Index([string]$Target, [string[]]$Extra = @()) {
    $stderrFile = [IO.Path]::GetTempFileName()
    try {
        $cliArguments = @($Target, '--json', "--index-dir=$indexDirectory") + $Extra
        $stdout = if ($Tool -like '*.exe') { & $Tool @cliArguments 2> $stderrFile } else { & dotnet $Tool @cliArguments 2> $stderrFile }
        [pscustomobject]@{ Exit = $LASTEXITCODE; Json = $(if ($stdout) { $stdout | ConvertFrom-Json } else { $null }); Stderr = (Get-Content -Raw $stderrFile) }
    } finally { Remove-Item $stderrFile -ErrorAction SilentlyContinue }
}
function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { "  PASS  $Name" } else { "  FAIL  $Name  $Detail"; $script:failures++ }
}
function Check-Mode($Result, [string]$Mode, [string]$Name) {
    Check "${Name}: mode $Mode" ($Result.Json -and $Result.Json.index.mode -eq $Mode) "exit=$($Result.Exit) mode=$($Result.Json.index.mode) reason=$($Result.Json.index.rebuild_reason) $($Result.Stderr)"
}
function Check-Verified($Result, [string]$Name) {
    Check "${Name}: verify 0 differences, exit 0" ($Result.Exit -eq 0 -and $Result.Json.verify.differences -eq 0) "exit=$($Result.Exit) $($Result.Stderr)"
}
function Write-Bytes([string]$Path, [int]$Size) {
    $null = New-Item -ItemType Directory -Force (Split-Path $Path)
    [IO.File]::WriteAllBytes($Path, [byte[]]::new($Size))
}
function Test-Journal { $null = fsutil usn queryjournal $Volume 2>&1; $LASTEXITCODE -eq 0 }
function Set-Journal([bool]$Active) {
    $null = fsutil usn deletejournal /d $Volume 2>&1
    for ($i = 0; $i -lt 50 -and (Test-Journal); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not $Active) { return }
    for ($i = 0; $i -lt 50; $i++) {
        $null = fsutil usn createjournal m=33554432 a=4194304 $Volume 2>&1
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 200   # the old journal may still be being deleted
    }
    throw "fsutil usn createjournal $Volume kept failing"
}

try {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    if (-not (Test-Journal)) { "creating a USN journal on $Volume"; Set-Journal $true }

    '--- first run: full scan, index saved'
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'full' 'first run'
    Check 'first run: reason "no saved index"' ($r.Json.index.rebuild_reason -eq 'no saved index') $r.Json.index.rebuild_reason
    Check 'first run: saved' ($r.Json.index.saved -eq $true)
    Check 'first run: journal id recorded' ($r.Json.index.journal_id -ne 0)
    Check-Verified $r 'first run'

    '--- no changes: incremental'
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'no changes'
    Check-Verified $r 'no changes'

    '--- created: files, directories, and a file with 20 long hard links (its names need extension records)'
    Write-Bytes "$root\plain\a.bin" 1000
    Write-Bytes "$root\plain\sub\b.bin" 20000
    Write-Bytes "$root\plain\sub\c.bin" 300
    Write-Bytes "$root\move-me\m.bin" 4096
    Write-Bytes "$root\doomed\d1.bin" 5000
    Write-Bytes "$root\doomed\deeper\d2.bin" 6000
    Write-Bytes "$root\links\target.bin" 7777
    $longName = 'L' * 120
    for ($i = 0; $i -lt 20; $i++) {
        $null = fsutil hardlink create "$root\links\$longName$i.bin" "$root\links\target.bin"
        if ($LASTEXITCODE -ne 0) { throw "fsutil hardlink create failed" }
    }
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after creating'
    Check-Verified $r 'after creating'
    Check 'after creating: journal entries applied' ($r.Json.index.usn_changes -gt 0) "usn_changes=$($r.Json.index.usn_changes)"
    Check 'after creating: extension records read' ($r.Json.index.extension_reads -gt 0) "extension_reads=$($r.Json.index.extension_reads)"

    '--- edited: grow, shrink, rename, move, delete a tree, grow a linked file, drop a link'
    [IO.File]::WriteAllBytes("$root\plain\a.bin", [byte[]]::new(3000))
    [IO.File]::WriteAllBytes("$root\plain\sub\c.bin", [byte[]]::new(10))
    Rename-Item "$root\plain\sub\b.bin" 'b-renamed.bin'
    Move-Item "$root\move-me" "$root\plain\moved"
    Remove-Item -Recurse -Force "$root\doomed"
    [IO.File]::AppendAllText("$root\links\${longName}3.bin", ('x' * 1000))
    Remove-Item "$root\links\${longName}5.bin"
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after editing'
    Check-Verified $r 'after editing'
    Check 'after editing: records removed' ($r.Json.index.records_removed -gt 0) "records_removed=$($r.Json.index.records_removed)"

    '--- journal recreated: full scan, then incremental again'
    $oldJournal = $r.Json.index.journal_id
    Set-Journal $true
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'journal recreated'
    Check 'journal recreated: reason' ($r.Json.index.rebuild_reason -match 'recreated') $r.Json.index.rebuild_reason
    Check 'journal recreated: new journal id' ($r.Json.index.journal_id -ne $oldJournal)
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'after the recreated journal'
    Check-Verified $r 'after the recreated journal'

    '--- journal disabled: full scans until it is back'
    Set-Journal $false
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'no journal'
    Check 'no journal: reason' ($r.Json.index.rebuild_reason -match 'no active USN journal') $r.Json.index.rebuild_reason
    Check 'no journal: saved with journal id 0' ($r.Json.index.saved -eq $true -and $r.Json.index.journal_id -eq 0)
    Set-Journal $true
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'journal back'
    Check 'journal back: reason' ($r.Json.index.rebuild_reason -match 'without a USN journal') $r.Json.index.rebuild_reason
    $r = Invoke-Index "$Volume\" @('--verify')
    Check-Mode $r 'incremental' 'journal back, second run'
    Check-Verified $r 'journal back, second run'

    '--- damaged index file: full scan'
    $file = $r.Json.index.file
    $bytes = [IO.File]::ReadAllBytes($file)
    $middle = [int]($bytes.Length / 2)
    $bytes[$middle] = $bytes[$middle] -bxor 0x40
    [IO.File]::WriteAllBytes($file, $bytes)
    $r = Invoke-Index "$Volume\"
    Check-Mode $r 'full' 'damaged file'
    Check 'damaged file: reason' ($r.Json.index.rebuild_reason -match 'checksum') $r.Json.index.rebuild_reason

    '--- --rebuild'
    $r = Invoke-Index "$Volume\" @('--rebuild')
    Check-Mode $r 'full' '--rebuild'
    Check '--rebuild: reason' ($r.Json.index.rebuild_reason -match 'rebuild') $r.Json.index.rebuild_reason
} finally {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    if (Test-Path $indexDirectory) { Remove-Item -Recurse -Force $indexDirectory }
    if (-not (Test-Journal)) { Set-Journal $true }
}
''
if ($failures -eq 0) { 'ALL CHECKS PASSED'; exit 0 } else { "$failures CHECK(S) FAILED"; exit 1 }
```

- [x] **Step 2: Run it (elevated)**

Run: `.\scripts\Test-IndexIncremental.ps1 -Volume T:; "exit=$LASTEXITCODE"`
Expected: every line `PASS`, then `ALL CHECKS PASSED`, `exit=0`. If a `verify` check fails, the samples in the FAIL line
name the records. Do not relax the check. Follow the superpowers:systematic-debugging skill on the named records,
using `dirsizer-inspect T: --record=<n>` to see them.

- [x] **Step 3: Make sure the checks can fail**

> Done 2026-09-23: with metadata re-reads disabled the checks as written all passed (T:'s `$MFT` did not grow), so a step was added that creates empty files until `$MFT` grows; then "after $MFT grew" and "after deleting them" failed on record 0. Skipping extension reads failed every verify after the hard links were created. Restored: ALL CHECKS PASSED (36 checks).

In `IndexUpdater.MetadataRecords`, change `number < FirstUserRecord` to `number < 0UL` (no metadata re-reads), rebuild,
and run the script. Expected: at least one `after creating` or `after editing` verify check FAILs, naming record 0
(`$MFT` grew) or an `$Extend` record. If no check fails, the volume's `$MFT` did not grow. Then also change
`IndexUpdater.Apply` to skip extension records (`foreach (var extension in extensions)` → `foreach (var extension in new SortedSet<ulong>())`),
which must fail the linked-file checks. Record which mutation made which check fail, restore the code, rebuild,
and run the script again: `ALL CHECKS PASSED`.

- [x] **Step 4: Commit**

```powershell
git add scripts\Test-IndexIncremental.ps1
git commit -m "scripts: Test-IndexIncremental.ps1, end-to-end incremental updates and fallbacks on T:"
```

---

### Task 14: I3 checkpoint: C: measurements, a decision gate, docs

**Files:**
- Create: `scripts\Measure-Index.ps1`
- Modify: `docs\design_index.md`, `docs\roadmap.md`

- [x] **Step 1: Write the measurement script**

```powershell
<#
.SYNOPSIS
  Measures dirsizer-index: full scans against incremental runs on one volume.

.DESCRIPTION
  After one discarded warm-up, runs -Runs full scans (--rebuild), then -Runs incremental runs (each loads the index the
  run before saved and reads only what the USN journal names), all with --json, and prints min / median / max of every
  phase, the number of changes applied, and the index size. Index files go to a temporary directory, never to the
  default location. A live volume keeps changing, so incremental runs also show how many changes they applied.
#>
param(
    [string]$Volume = 'C:',
    [int]$Runs = 3,
    [string]$Tool = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll')
)
$ErrorActionPreference = 'Stop'
$target = "$($Volume.TrimEnd('\'))\"
$indexDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dirsizer-index-measure-' + [guid]::NewGuid().ToString('N'))

function Invoke-Index([string[]]$Extra) {
    $cliArguments = @($target, '--json', "--index-dir=$indexDirectory") + $Extra
    $stdout = if ($Tool -like '*.exe') { & $Tool @cliArguments 2> $null } else { & dotnet $Tool @cliArguments 2> $null }
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited with $LASTEXITCODE" }
    ($stdout | ConvertFrom-Json).index
}
function Get-Stats($samples) {
    $sorted = @($samples | Sort-Object)
    $median = if ($sorted.Count % 2) { $sorted[($sorted.Count - 1) / 2] } else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
    [pscustomobject]@{ Min = $sorted[0]; Median = $median; Max = $sorted[-1] }
}

try {
    'warm-up (discarded)...'
    $null = Invoke-Index @('--rebuild')
    $full = @(); $incremental = @()
    foreach ($run in 1..$Runs) { "full run ${run}/${Runs}"; $full += , (Invoke-Index @('--rebuild')) }
    foreach ($run in 1..$Runs) { "incremental run ${run}/${Runs}"; $incremental += , (Invoke-Index @()) }
    foreach ($row in $incremental) { if ($row.mode -ne 'incremental') { throw "an incremental run did a full scan: $($row.rebuild_reason)" } }
    ''
    "$Runs full and $Runs incremental runs on $target (min / median / max; milliseconds unless the name says otherwise)"
    foreach ($field in 'load_ms', 'usn_ms', 'update_ms', 'scan_ms', 'recompute_ms', 'save_ms', 'query_ms', 'total_ms', 'usn_changes', 'records_reread', 'records', 'index_bytes') {
        $f = Get-Stats ($full | ForEach-Object { $_.$field })
        $i = Get-Stats ($incremental | ForEach-Object { $_.$field })
        '{0,-15} FULL {1,14:N1} / {2,14:N1} / {3,14:N1}    INCREMENTAL {4,14:N1} / {5,14:N1} / {6,14:N1}' -f $field, $f.Min, $f.Median, $f.Max, $i.Min, $i.Median, $i.Max
    }
    $fullMedian = (Get-Stats ($full | ForEach-Object { $_.total_ms })).Median
    $incrementalMedian = (Get-Stats ($incremental | ForEach-Object { $_.total_ms })).Median
    'median total: incremental / full = {0:P0}' -f ($incrementalMedian / $fullMedian)
} finally {
    if (Test-Path $indexDirectory) { Remove-Item -Recurse -Force $indexDirectory }
}
```

- [x] **Step 2: Run on C: (elevated)**

Run: `.\scripts\Measure-Index.ps1 -Volume C: -Runs 3`
Expected: the table and a final `median total: incremental / full = N%` line. Keep the whole output.

- [x] **Step 3: Decision gate. Stop and report if the index does not pay for itself**

> Done 2026-09-23: incremental median 4,529 ms = 55 % of full (8,219 ms), so the gate is not met. The largest phases are save_ms 1,914 and load_ms 1,473 (recompute_ms 483). Stopped before I4 and reported to the user. The user chose "write only the changed part": an incremental run now saves a delta file (commit "save only a delta after an incremental run"); C: incremental 2,630 ms = 31 % of full (8,418 ms), so the gate is met.

If the incremental median total is **50 % or more** of the full median, stop before Phase I4. Report the table to the
user with the dominant incremental phase (`load_ms`, `recompute_ms` or `save_ms`), and ask how to proceed. Likely
remedies: store aggregated sizes to skip `Recompute`; skip saving when nothing changed; update only the ancestor
chains. They are not planned here because they are not needed unless the numbers say so. If it is below 50 %,
continue.

- [x] **Step 4: Append the I3 section to `docs\design_index.md`**

Insert before `## Privacy`:

```markdown
## Incremental update (I3)

1. The journal state (id, first USN, next USN) is read before anything else.
2. The saved index is used only if the volume identity is the same, the volume has an active journal, the index was
   written with one, the journal id is the same (not recreated), and the saved position is still in the journal (not
   wrapped: `>= FirstUsn`) and not ahead of it (`IndexValidity`). Otherwise a full scan runs, and the reason is
   reported.
3. Every USN record from the saved position to the end of the journal is read (`FSCTL_READ_USN_JOURNAL`, V2 records).
   Only the file reference is used. The reason flags are never used to infer the new state.
4. Each named record is read again with `FSCTL_GET_NTFS_FILE_RECORD`. So are records 0-23 and the `$Extend` tree: NTFS
   metadata changes without journal entries, for example when `$MFT` grows. A record that is no longer in use, is not
   parseable, or is now an extension record is removed. Otherwise its extension records are found through its
   `$ATTRIBUTE_LIST` (resident, or non-resident and read through its run list), read, and merged in descending record
   order as a scan would, and the result replaces the entry. If an attribute list or extension record cannot be read,
   the run falls back to a full scan (`IndexUpdater`).
5. Parents, names and directory sizes are computed again for the whole index (`Recompute`). Then the new journal
   position is stored and the index is saved.

Replaying a change twice is harmless, because step 4 reads the current state. That is why the position stored after
a full scan is the one read before the scan started, and why changes made during a run are simply seen again next
time.

Not detected: changes made while the volume was mounted by a system that does not write the USN journal (for example
another operating system). Use `--rebuild` after such a mount.

End-to-end test: `scripts\Test-IndexIncremental.ps1` (T:). Timings: `scripts\Measure-Index.ps1`.
```

In the section `## When a saved index is not used`, replace its paragraph with:

```markdown
The file is ignored and the whole `$MFT` is scanned again when: it does not exist; its checksum, magic or version is
wrong; the volume identity differs; the volume has no active USN journal, or the index was written without one; the
journal was recreated (different id); the saved position is no longer in the journal (it wrapped) or is ahead of it;
an attribute list or extension record could not be read during the update; or `--rebuild` was given. The reason is
printed on stderr and in JSON `index.rebuild_reason`.
```

Add a row to the measurements table: `| C: (I3, warm cache, 3 + 3 runs, medians) | <records> | <MiB> | full <total_ms> | <save_ms> | <load_ms> | <recompute_ms> |`,
and below the table one sentence: `Incremental run: <total_ms> ms total (<usn_changes> journal entries, <records_reread> records read again), <N>% of a full run.`

- [x] **Step 5: Update the roadmap's I3 section**

Replace the four I3 items with:

```markdown
- [x] Read the journal from the saved USN; handle journal wrap (`ERROR_JOURNAL_ENTRY_DELETED`), journal recreation (ID change), and a disabled journal by falling back to a full rescan. `UsnJournal`, `IndexValidity`; self-tests with synthetic journal pages; on T:, `Test-IndexIncremental.ps1` recreated and disabled the journal and damaged the index file, and each gave a full scan with the stated reason. A wrap was not forced on a real volume (only the self-test covers it).
- [x] Apply create, delete, rename/move (parent change), size change, and hard-link changes; re-read records by FRN instead of trusting event payloads. `IndexUpdater` (FSCTL re-read, attribute lists for extension records, NTFS metadata re-read every time); `Test-IndexIncremental.ps1`: every `--verify` after real changes on T: had 0 differences, and the check failed when metadata or extension re-reads were disabled.
- [x] Aggregation after an update: the whole index is recomputed in memory (`Recompute`, <recompute_ms> ms on C:), not only the ancestor chains. The result is identical to a scan by construction.
- [ ] Only if `recompute_ms` dominates an incremental run: update the ancestor chains instead of recomputing everything.
- [x] Verify: after a scripted set of changes on the `T:` fixture, the incrementally updated index equals a fresh full scan (`Test-IndexIncremental.ps1`, ALL CHECKS PASSED). C: timings: <incremental / full %> (`Measure-Index.ps1`, one machine, warm cache).
```

- [x] **Step 6: Regression guard and commit**

Run the three regression-guard self-tests. Expected: all pass.

```powershell
git add scripts\Measure-Index.ps1 docs\design_index.md docs\roadmap.md
git commit -m "I3 checkpoint: Measure-Index.ps1, C: timings, incremental update design"
```

---

# Phase I4 — subtree queries

### Task 15: Resolve a path to its MFT record; query any directory

**Files:**
- Create: `src\DirSizer.Index\PathResolver.cs`
- Modify: `src\DirSizer.Index\IndexRunner.cs`, `src\DirSizer.Index\IndexOptions.cs`, `src\DirSizer.Index\IndexSelfTests.Query.cs`, `src\DirSizer.Index\IndexSelfTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `IndexSelfTests.Query.cs`, inside the class:

```csharp
    static void PathResolverFindsTheDirectoryRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-index-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllBytes(Path.Combine(directory, "f.bin"), [1, 2, 3]);
        try
        {
            var (serial, reference) = PathResolver.Identify(directory);
            var (_, sub) = PathResolver.Identify(Path.Combine(directory, "sub"));
            Assert(reference != sub && reference.RecordNumber != 0, "two directories, two records");
            var records = new Dictionary<ulong, FileRecord> { [reference.RecordNumber] = new FileRecord(reference, reference.SequenceNumber, true) };
            var index = new VolumeIndex(TestIdentity with { SerialNumber = serial }, 1, 1, DateTime.UnixEpoch, records);
            AssertEqual(reference, PathResolver.Find(index, directory).Reference, "found by its record and sequence number");
            AssertThrowsWithMessage<ArgumentException>(() => PathResolver.Find(index, Path.Combine(directory, "sub")), "not in the index", "a directory the index does not have");
            var (_, file) = PathResolver.Identify(Path.Combine(directory, "f.bin"));
            records[file.RecordNumber] = new FileRecord(file, file.SequenceNumber, false);
            AssertThrowsWithMessage<ArgumentException>(() => PathResolver.Find(index, Path.Combine(directory, "f.bin")), "is a file", "a file");
            var otherVolume = new VolumeIndex(TestIdentity with { SerialNumber = serial + 1L }, 1, 1, DateTime.UnixEpoch, records);
            AssertThrowsWithMessage<ArgumentException>(() => PathResolver.Find(otherVolume, directory), "not on the indexed volume", "another volume's index");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
```

Add to the test list:

```csharp
            new("path: resolved by Windows to the record; missing, file and other-volume cases are refused", PathResolverFindsTheDirectoryRecord),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL, `PathResolver` does not exist.

- [ ] **Step 3: Implement `PathResolver.cs`**

```csharp
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Turns a path into its MFT record by asking Windows (GetFileInformationByHandle): exactly the object Windows opens for
// that path, following junctions and mount points as Windows does. The result must be on the indexed volume and in the
// index with the same sequence number (roadmap I4).
static class PathResolver
{
    const uint FileReadAttributes = 0x80;
    const uint FileShareAll = 7;   // read, write, delete
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;   // needed to open a directory

    public static FileRecord Find(VolumeIndex index, string path)
    {
        var (serial, reference) = Identify(path);
        if (serial != (uint)index.Identity.SerialNumber)
            throw new ArgumentException($"{path} is not on the indexed volume (it may lead through a junction or mount point to another volume).");
        if (!index.Records.TryGetValue(reference.RecordNumber, out var record) || record.Reference != reference)
            throw new ArgumentException($"{path} is not in the index (MFT record {reference.RecordNumber}:{reference.SequenceNumber}); it may have been created after the journal was read. Run again.");
        if (!record.IsDirectory) throw new ArgumentException($"{path} is a file, not a directory.");
        return record;
    }

    // The volume serial number (low 32 bits of the NTFS serial) and the full file reference, as Windows reports them.
    internal static (uint Serial, FileRef Reference) Identify(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Cannot open {path} (Win32 error {error})");
        }
        var info = new byte[52];   // BY_HANDLE_FILE_INFORMATION
        if (!GetFileInformationByHandle(handle, info))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Cannot read the file id of {path} (Win32 error {error})");
        }
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
        var fileIndex = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(44)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48));
        return (serial, new FileRef(fileIndex));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, byte[] information);
}
```

- [ ] **Step 4: Runner: accept any directory on a local drive**

In `IndexRunner.cs`, replace:

```csharp
        var volume = DriveRoot.Validate(options.Target);
```

with:

```csharp
        var target = Path.GetFullPath(options.Target);
        var pathRoot = Path.GetPathRoot(target);
        if (pathRoot is null || pathRoot.Length != 3 || pathRoot[1] != ':')
            throw new ArgumentException($"{options.Target}: dirsizer-index works on local drives only (a path such as C:\\Users).");
        var volume = DriveRoot.Validate(pathRoot);
```

and replace:

```csharp
        var result = SubtreeQuery.Query(index.Records, volume, index.Records[SubtreeQuery.RootRecord], options.Top);
```

with:

```csharp
        var root = PathResolver.Find(index, target);
        var result = SubtreeQuery.Query(index.Records, volume, root, options.Top);
```

- [ ] **Step 5: Options: help and error text**

In `IndexOptions.cs`:
- Replace `"A drive root is required, for example C:\\."` with `"A directory is required, for example C:\\ or C:\\Users."`.
- In `PrintHelp`, replace the usage line with
  `            Usage: dirsizer-index <directory> [--top=N] [--files] [--json] [--rebuild] [--verify] [--no-save] [--index-dir=DIR] [--benchmark]`.
- Insert after the `-h               Show this help` line and its following blank line:

```text
            <directory> is any directory on a local NTFS drive (C:\, C:\Users\me\Downloads). The index always
            covers the whole volume; the answer covers only that directory. Junctions and mount points are
            followed as Windows follows them, but the directory must be on the same volume.

```

- [ ] **Step 6: Run the tests**

Expected: `27 self-tests passed, 0 skipped.`

- [ ] **Step 7: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: query any directory; the path is resolved to its MFT record by Windows"
```

---

### Task 16: I4 checkpoint: subtree checks on T:, docs

**Files:**
- Modify: `scripts\Test-IndexIncremental.ps1`, `docs\design_index.md`, `docs\roadmap.md`

- [ ] **Step 1: Add the subtree section to the end-to-end script**

In `scripts\Test-IndexIncremental.ps1`, insert this block right before the line `    '--- journal recreated: full scan, then incremental again'`:

```powershell
    '--- subtree queries'
    $expected = (Get-ChildItem -Recurse -File "$root\plain" | Measure-Object Length -Sum).Sum   # no hard links under plain
    $r = Invoke-Index "$root\plain"
    Check-Mode $r 'incremental' 'subtree'
    Check 'subtree: root path' ($r.Json.root.path -eq "$root\plain") $r.Json.root.path
    Check "subtree: size = sum of its files ($expected)" ($r.Json.root.size -eq $expected) "index=$($r.Json.root.size)"
    $fsJson = if ($FsTool -like '*.exe') { & $FsTool "$root\plain" --json 2> $null } else { & dotnet $FsTool "$root\plain" --json 2> $null }
    $fs = $fsJson | ConvertFrom-Json
    Check 'subtree: equal to dirsizer-fs (root)' ($fs.root.size -eq $r.Json.root.size) "fs=$($fs.root.size) index=$($r.Json.root.size)"
    # Children are compared by name and size: the two tools need not format the parent part of a path the same way.
    $fsChildren = ($fs.root_children | ForEach-Object { "$(Split-Path -Leaf $_.path)=$($_.size)" } | Sort-Object) -join ','
    $indexChildren = ($r.Json.root_children | ForEach-Object { "$(Split-Path -Leaf $_.path)=$($_.size)" } | Sort-Object) -join ','
    Check 'subtree: equal to dirsizer-fs (children)' ($fsChildren -eq $indexChildren) "fs=$fsChildren index=$indexChildren"
    $whole = Invoke-Index "$Volume\" @('--top=100000')
    $fromWhole = @($whole.Json.directories | Where-Object path -eq "$root\plain")
    Check 'subtree: same size as in the whole-volume listing' ($fromWhole.Count -eq 1 -and $fromWhole[0].size -eq $r.Json.root.size)
    $r = Invoke-Index "$root\plain\a.bin"
    Check 'subtree: a file is refused' ($r.Exit -eq 1 -and $r.Stderr -match 'is a file') $r.Stderr
```

- [ ] **Step 2: Run the script (elevated)**

Run: `.\scripts\Test-IndexIncremental.ps1 -Volume T:; "exit=$LASTEXITCODE"`
Expected: `ALL CHECKS PASSED`, `exit=0`. `dirsizer-fs` is the independent oracle here: a directory walk that shares no
code with the index.

- [ ] **Step 3: Docs**

Append to `docs\design_index.md`, before `## Privacy`:

```markdown
## Subtree queries (I4)

The target can be any directory on the indexed volume. Windows resolves the path itself (`GetFileInformationByHandle`
gives the volume serial and the file reference), so junctions and mount points are followed as Windows follows them.
The result must be on the indexed volume, and its reference, sequence number included, must be in the index. The
answer is a walk down the selected parents from that record (`SubtreeQuery`). The whole volume uses the scan's own
`ResultSelector.Collect`, so it equals `dirsizer-mft`. One index per volume serves every query on that volume.
Checked against `dirsizer-fs` (an independent directory walk) on a folder without hard links in
`Test-IndexIncremental.ps1`.
```

In `docs\roadmap.md`, replace the two I4 items with:

```markdown
- [x] Path → FRN resolution on the index; subtree totals equal to a fresh scan of that subtree (and explain differences with `dirsizer-fs.exe`, as in design_fs.md). Windows resolves the path (`PathResolver`); on T:, a subtree's size equals the sum of its files, `dirsizer-fs` (root and every child), and the same directory in the whole-volume listing (`Test-IndexIncremental.ps1`). The differences with `dirsizer-fs` on trees with hard links are the ones design_fs.md already lists for the NTFS tools.
- [x] Share one index across whole-volume and subtree analyses: one `<serial>.dsix` per volume; every query loads and updates the same file.
```

- [ ] **Step 4: Regression guard and commit**

Run the three regression-guard self-tests. Expected: all pass.

```powershell
git add scripts\Test-IndexIncremental.ps1 docs\design_index.md docs\roadmap.md
git commit -m "I4 checkpoint: subtree queries checked against dirsizer-fs on T:"
```

---

# Phase I5 — repeated analysis

### Task 17: `IndexSnapshot` and `ChangeReporter`

**Files:**
- Create: `src\DirSizer.Index\ChangeReport.cs`, `src\DirSizer.Index\IndexSelfTests.Changes.cs`
- Modify: `src\DirSizer.Index\IndexSelfTests.cs` (list)

- [ ] **Step 1: Write the failing tests**

`src\DirSizer.Index\IndexSelfTests.Changes.cs`:

```csharp
static partial class IndexSelfTests
{
    static string Changes(DirectoryChange[] changes) => string.Join(',', Array.ConvertAll(changes, change => $"{change.Path}={change.Before}->{change.After}"));

    static void ChangesShowWhatShrankAndGrew()
    {
        var source = SampleVolume();
        source.Records[32] = DirBytes(32, 5, "C");
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records.Remove(40);                              // b.bin (100) deleted: B, A and the root shrink
        source.Records[42] = FileBytes(42, 32, "c.bin", 30);    // c.bin (30) created in C: C grows
        IndexUpdater.Apply(before.Records, [new UsnChange(40, 0, 0), new UsnChange(42, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var report = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        AssertEqual(123L, report.RootBefore, "root before: 100 + 20 + 3");
        AssertEqual(53L, report.RootAfter, "root after: 20 + 3 + 30");
        AssertEqual("T:\\A=120->20,T:\\A\\B=100->0,T:\\=123->53", Changes(report.Shrunk), "shrunk, largest change first");
        AssertEqual("T:\\C=0->30", Changes(report.Grew), "grew");
        var scoped = ChangeReporter.Compare(snapshot, after, "T:", after.Records[30], 10);
        AssertEqual("T:\\A=120->20,T:\\A\\B=100->0", Changes(scoped.Shrunk), "only at or below A");
        AssertEqual("", Changes(scoped.Grew), "C is not below A");
        AssertEqual(1, ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 1).Shrunk.Length, "--top limits each list");
    }

    static void ChangesListADeletedDirectoryByItsOldPath()
    {
        var source = SampleVolume();
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records.Remove(40);
        source.Records.Remove(31);   // B and its file deleted
        IndexUpdater.Apply(before.Records, [new UsnChange(40, 0, 0), new UsnChange(31, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var report = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        // Three changes of -100; equal changes are ordered by record number (root 5, A 30, B 31).
        AssertEqual("T:\\=123->23,T:\\A=120->20,T:\\A\\B=100->0", Changes(report.Shrunk), "B is listed by its old path");
    }

    static void ChangesTreatAReusedRecordAsGoneAndNew()
    {
        var source = SampleVolume();
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records.Remove(40);
        source.Records[31] = DirBytes(31, 5, "D", sequence: 2);   // B deleted; its record reused by a new, empty directory D
        IndexUpdater.Apply(before.Records, [new UsnChange(40, 0, 0), new UsnChange(31, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var report = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        AssertEqual("T:\\=123->23,T:\\A=120->20,T:\\A\\B=100->0", Changes(report.Shrunk), "B is gone, by its old path");
        AssertEqual("", Changes(report.Grew), "D is new but empty: nothing to report");
    }
}
```

Add to the test list:

```csharp
            new("changes: shrunk and grew, largest first, scoped to the queried directory", ChangesShowWhatShrankAndGrew),
            new("changes: a deleted directory is listed by its old path", ChangesListADeletedDirectoryByItsOldPath),
            new("changes: a reused record is one directory gone and one new", ChangesTreatAReusedRecordAsGoneAndNew),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL, `IndexSnapshot`, `ChangeReporter` and `DirectoryChange` do not exist.

- [ ] **Step 3: Implement `ChangeReport.cs`**

```csharp
// "What changed since the previous run" (roadmap I5). The baseline is the saved index, aggregated before the journal's
// changes are applied; only directories are kept. A directory is the same directory only if its record number *and*
// sequence number match, so a reused record counts as one directory gone and one new.
readonly record struct SnapshotEntry(FileRef Reference, long Size, ulong Parent, string? Name);

sealed class IndexSnapshot(DateTime writtenUtc, Dictionary<ulong, SnapshotEntry> directories)
{
    const int MaxDepth = 10000;

    public DateTime WrittenUtc { get; } = writtenUtc;
    public Dictionary<ulong, SnapshotEntry> Directories { get; } = directories;

    // The index must be aggregated (Recompute, or a scan).
    public static IndexSnapshot Capture(VolumeIndex index)
    {
        var directories = new Dictionary<ulong, SnapshotEntry>();
        foreach (var record in index.Records.Values)
        {
            if (record.IsDirectory)
                directories[record.Reference.RecordNumber] = new SnapshotEntry(record.Reference, record.Size, record.Parent.RecordNumber, record.DisplayName);
        }
        return new IndexSnapshot(index.WrittenUtc, directories);
    }

    public bool IsUnder(ulong number, ulong scope)
    {
        var current = number;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (current == scope) return true;
            if (current == SubtreeQuery.RootRecord || !Directories.TryGetValue(current, out var entry) || entry.Name is null) return false;
            current = entry.Parent;
        }
        return false;
    }

    public string PathOf(string volume, ulong number)
    {
        var names = new Stack<string>();
        var current = number;
        for (var depth = 0; depth < MaxDepth && current != SubtreeQuery.RootRecord; depth++)
        {
            if (!Directories.TryGetValue(current, out var entry) || entry.Name is null) return $"{volume}\\[record {number}]";
            names.Push(entry.Name);
            current = entry.Parent;
        }
        return current == SubtreeQuery.RootRecord ? volume + "\\" + string.Join('\\', names) : $"{volume}\\[record {number}]";
    }
}

readonly record struct DirectoryChange(string Path, long Before, long After)
{
    public long Delta => After - Before;
}

sealed record ChangeReport(DateTime SinceUtc, long RootBefore, long RootAfter, DirectoryChange[] Shrunk, DirectoryChange[] Grew);

static class ChangeReporter
{
    readonly record struct Candidate(ulong Number, bool Gone, long Before, long After)
    {
        public long Delta => After - Before;
    }

    // Compares every directory at or below `scope` now with the same directory in the snapshot. A directory's change
    // includes everything below it, so the parents of a deleted folder are listed too.
    public static ChangeReport Compare(IndexSnapshot before, VolumeIndex after, string volume, FileRecord scope, int top)
    {
        var records = after.Records;
        var scopeNumber = scope.Reference.RecordNumber;
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
            var old = SameDirectoryBefore(before, directory, scopeNumber) ? before.Directories[number].Size : 0;
            if (directory.Size != old) candidates.Add(new Candidate(number, false, old, directory.Size));
        }
        foreach (var (number, entry) in before.Directories)
        {
            if (entry.Size == 0 || !before.IsUnder(number, scopeNumber)) continue;
            if (present.Contains(number) && records[number].Reference == entry.Reference) continue;
            candidates.Add(new Candidate(number, true, entry.Size, 0));
        }
        var rootBefore = SameDirectoryBefore(before, scope, scopeNumber) ? before.Directories[scopeNumber].Size : 0;
        return new ChangeReport(before.WrittenUtc, rootBefore, scope.Size,
            Select(candidates, top, shrunk: true, before, records, volume),
            Select(candidates, top, shrunk: false, before, records, volume));
    }

    static bool SameDirectoryBefore(IndexSnapshot before, FileRecord directory, ulong scopeNumber) =>
        before.Directories.TryGetValue(directory.Reference.RecordNumber, out var entry)
        && entry.Reference == directory.Reference
        && before.IsUnder(directory.Reference.RecordNumber, scopeNumber);

    static DirectoryChange[] Select(List<Candidate> candidates, int top, bool shrunk, IndexSnapshot before, Dictionary<ulong, FileRecord> records, string volume)
    {
        var chosen = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            if (shrunk ? candidate.Delta < 0 : candidate.Delta > 0) chosen.Add(candidate);
        }
        // Largest change first; equal changes by record number, so the order is reproducible. Paths are built only for
        // the entries that are shown.
        chosen.Sort((left, right) =>
        {
            var byChange = shrunk ? left.Delta.CompareTo(right.Delta) : right.Delta.CompareTo(left.Delta);
            return byChange != 0 ? byChange : left.Number.CompareTo(right.Number);
        });
        var count = Math.Min(top, chosen.Count);
        var result = new DirectoryChange[count];
        for (var i = 0; i < count; i++)
        {
            var candidate = chosen[i];
            var path = candidate.Gone ? before.PathOf(volume, candidate.Number) : RecordPaths.Build(volume, records, records[candidate.Number]);
            result[i] = new DirectoryChange(path, candidate.Before, candidate.After);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the tests**

Expected: `30 self-tests passed, 0 skipped.`

- [ ] **Step 5: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: ChangeReporter compares directory sizes with the previous index"
```

---

### Task 18: `--changes`: options, runner, output

**Files:**
- Modify: `src\DirSizer.Index\IndexOptions.cs`, `src\DirSizer.Index\IndexRunner.cs`, `src\DirSizer.Index\IndexOutput.cs`, `src\DirSizer.Index\IndexSelfTests.cs`, `src\DirSizer.Index\IndexSelfTests.Changes.cs`

- [ ] **Step 1: Write the failing tests**

In `IndexSelfTests.cs`, `OptionsDefaultsAndFlags`: change `&& !defaults.Rebuild, "default flags");` to
`&& !defaults.Rebuild && !defaults.Changes, "default flags");` and add
`        Assert(IndexOptions.Parse(["D:\\", "--changes"]).Changes, "--changes");` after the `--rebuild` line.

Append to `IndexSelfTests.Changes.cs`, inside the class:

```csharp
    static void OutputListsTheChanges()
    {
        var report = new ChangeReport(new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc), 123, 53,
            [new DirectoryChange("T:\\A", 120, 20)], [new DirectoryChange("T:\\C", 0, 30)]);
        var run = SampleRun() with { Changes = report };
        var text = new StringWriter();
        IndexOutput.Write(run, IndexOptions.Parse(["T:", "--changes"]), text, new StringWriter());
        AssertContains(text.ToString(), "Changes since 2026-09-23 10:00:00 UTC", "text heading");
        AssertContains(text.ToString(), "-100\t120\t20\tT:\\A", "text shrunk row");
        AssertContains(text.ToString(), "+30\t0\t30\tT:\\C", "text grew row");

        var json = new StringWriter();
        IndexOutput.Write(run, IndexOptions.Parse(["T:", "--json", "--changes"]), json, new StringWriter());
        using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
        var changes = document.RootElement.GetProperty("changes");
        AssertEqual(-70L, changes.GetProperty("root_delta").GetInt64(), "json changes.root_delta");
        AssertEqual(-100L, changes.GetProperty("shrunk")[0].GetProperty("delta").GetInt64(), "json changes.shrunk[0].delta");
        AssertEqual("T:\\C", changes.GetProperty("grew")[0].GetProperty("path").GetString(), "json changes.grew[0].path");

        var none = new StringWriter();
        IndexOutput.Write(SampleRun(), IndexOptions.Parse(["T:", "--changes"]), new StringWriter(), none);
        AssertContains(none.ToString(), "no earlier index", "stderr when there is nothing to compare with");
    }
```

Add to the test list:

```csharp
            new("output: changes in text and JSON; a note when there is no earlier index", OutputListsTheChanges),
```

- [ ] **Step 2: Run to see it fail**

Build. Expected: FAIL, `IndexOptions.Changes` and `IndexRun.Changes` do not exist.

- [ ] **Step 3: Options**

In `IndexOptions.cs`:
- After `public bool Rebuild { get; private set; }` add `public bool Changes { get; private set; }`.
- After `if (arg == "--rebuild") { result.Rebuild = true; continue; }` add `if (arg == "--changes") { result.Changes = true; continue; }`.
- In `PrintHelp`, change the usage line's `[--rebuild]` to `[--rebuild] [--changes]`, and after the `--rebuild` line add:

```text
            --changes        Also list the directories that shrank or grew since the index was last written
                             (with --no-save the comparison baseline stays the same run after run)
```

- [ ] **Step 4: Runner**

In `IndexRunner.cs`:
1. In the `IndexRun` record, replace `UpdateResult? Update, VerifyResult? Verify, IndexTimings Timings)` with
   `UpdateResult? Update, VerifyResult? Verify, IndexTimings Timings, ChangeReport? Changes = null)`.
2. Replace

```csharp
        UpdateResult? update = null;
        string? reason;
```

with

```csharp
        UpdateResult? update = null;
        IndexSnapshot? before = null;
        string? reason;
```

3. Replace

```csharp
            timings.Load = timer.Elapsed;
            if (index is not null) reason = IndexValidity.Check(index, identity, journal);
```

with

```csharp
            timings.Load = timer.Elapsed;
            if (index is not null && options.Changes && index.Identity == identity)
            {
                // The baseline for --changes: the saved index aggregated as it was, before anything is applied to it.
                timer.Restart();
                index.Recompute();
                timings.Recompute += timer.Elapsed;
                before = IndexSnapshot.Capture(index);
            }
            if (index is not null) reason = IndexValidity.Check(index, identity, journal);
```

4. Replace

```csharp
        var result = SubtreeQuery.Query(index.Records, volume, root, options.Top);
        timings.Query = timer.Elapsed;
```

with

```csharp
        var result = SubtreeQuery.Query(index.Records, volume, root, options.Top);
        var changes = before is null ? null : ChangeReporter.Compare(before, index, volume, root, options.Top);
        timings.Query = timer.Elapsed;
```

5. Replace `index.Records.Count, index.JournalId, index.NextUsn, update, verify, timings);` with
   `index.Records.Count, index.JournalId, index.NextUsn, update, verify, timings, changes);`.

If the saved index was not usable but had the same identity, the full scan's result is compared with the old index.
That is still a correct "since the last index" report.

- [ ] **Step 5: Output**

In `IndexOutput.cs`:
1. In `Write`, after the `if (options.Verify) { ... }` block, add:

```csharp
        if (options.Changes && run.Changes is null) error.WriteLine("changes: there is no earlier index of this volume to compare with");
```

2. At the end of `WriteText`, after the Summary line, add:

```csharp
        if (run.Changes is { } changes) WriteChanges(changes, options.Top, output);
```

3. Add these methods to `IndexOutput`:

```csharp
    static void WriteChanges(ChangeReport changes, int top, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"Changes since {changes.SinceUtc:yyyy-MM-dd HH:mm:ss} UTC (when the previous index was written)");
        output.WriteLine("Change\tBefore\tAfter\tPath");
        output.WriteLine($"{Signed(changes.RootAfter - changes.RootBefore)}\t{changes.RootBefore}\t{changes.RootAfter}\t(the directory queried)");
        WriteChangeList($"Shrunk (largest {top})", changes.Shrunk, output);
        WriteChangeList($"Grew (largest {top})", changes.Grew, output);
    }

    static void WriteChangeList(string title, DirectoryChange[] list, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine(title);
        output.WriteLine("Change\tBefore\tAfter\tPath");
        foreach (var change in list) output.WriteLine($"{Signed(change.Delta)}\t{change.Before}\t{change.After}\t{change.Path}");
    }

    // Byte counts without separators, so the columns can be parsed; the sign is always shown.
    static string Signed(long value) => value.ToString("+0;-0;0");

    static JsonChanges ToJson(ChangeReport changes) => new(
        changes.SinceUtc.ToString("o"), changes.RootBefore, changes.RootAfter, changes.RootAfter - changes.RootBefore,
        Array.ConvertAll(changes.Shrunk, ToJson), Array.ConvertAll(changes.Grew, ToJson));

    static JsonChange ToJson(DirectoryChange change) => new(change.Path, change.Before, change.After, change.Delta);
```

4. In `ToJson(IndexRun run, int top)`, replace the last argument line
   `            run.Verify is null ? null : new JsonVerify(run.Verify.Differences, run.Verify.Samples));` with

```csharp
            run.Verify is null ? null : new JsonVerify(run.Verify.Differences, run.Verify.Samples),
            run.Changes is null ? null : ToJson(run.Changes));
```

5. In the `JsonIndexOutput` record, replace
   `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonVerify? Verify = null);` with

```csharp
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonVerify? Verify = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonChanges? Changes = null);
```

and add after `sealed record JsonVerify(...)`:

```csharp
sealed record JsonChanges(string SinceUtc, long RootBefore, long RootAfter, long RootDelta, JsonChange[] Shrunk, JsonChange[] Grew);
sealed record JsonChange(string Path, long Before, long After, long Delta);
```

- [ ] **Step 6: Run the tests**

Expected: `31 self-tests passed, 0 skipped.`

- [ ] **Step 7: Commit**

```powershell
Normalize-Src
git add src\DirSizer.Index
git commit -m "dirsizer-index: --changes lists what shrank and grew since the previous index"
```

---

### Task 19: I5 checkpoint: end-to-end cleanup scenario, C: measurement, docs

**Files:**
- Modify: `scripts\Test-IndexIncremental.ps1`, `docs\design_index.md`, `docs\roadmap.md`, `README.md`

- [ ] **Step 1: Add the changes section to the end-to-end script**

Insert right before `    '--- journal recreated: full scan, then incremental again'`:

```powershell
    '--- changes since the previous run (the cleanup workflow)'
    $null = Invoke-Index "$Volume\"                                    # baseline: the saved index matches the volume
    Remove-Item "$root\plain\sub\b-renamed.bin"                        # 20000 bytes
    $r = Invoke-Index "$root\plain" @('--changes', '--no-save')
    Check 'changes: root delta -20000' ($r.Json.changes.root_delta -eq -20000) "root_delta=$($r.Json.changes.root_delta)"
    $sub = @($r.Json.changes.shrunk | Where-Object path -eq "$root\plain\sub")
    Check 'changes: sub shrank by 20000' ($sub.Count -eq 1 -and $sub[0].delta -eq -20000)
    Check 'changes: nothing grew below plain' (@($r.Json.changes.grew).Count -eq 0)
    $r = Invoke-Index "$root\plain" @('--changes')
    Check 'changes with --no-save before: same baseline' ($r.Json.changes.root_delta -eq -20000) "root_delta=$($r.Json.changes.root_delta)"
    $r = Invoke-Index "$root\plain" @('--changes')
    Check 'changes after saving: nothing left' ($r.Json.changes.root_delta -eq 0 -and @($r.Json.changes.shrunk).Count -eq 0) "root_delta=$($r.Json.changes.root_delta)"
```

- [ ] **Step 2: Run the script (elevated)**

Run: `.\scripts\Test-IndexIncremental.ps1 -Volume T:; "exit=$LASTEXITCODE"`
Expected: `ALL CHECKS PASSED`, `exit=0`.

- [ ] **Step 3: Measure the cleanup workflow on C: (elevated; only a temporary folder of our own is written)**

```powershell
$tool = 'artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll'
$dir = Join-Path $env:TEMP 'dirsizer-index-i5'
$work = Join-Path $env:TEMP 'dirsizer-cleanup-test'
New-Item -ItemType Directory -Force $work > $null
foreach ($i in 0..29999) { [IO.File]::WriteAllBytes((Join-Path $work ("f{0:D5}.bin" -f $i)), [byte[]]::new(1024)) }
dotnet $tool C:\ "--index-dir=$dir" --json 2>$null > $null                       # baseline
Remove-Item -Recurse -Force $work                                                 # the "cleanup": 30,000 files, 30 MiB
$after = dotnet $tool C:\ "--index-dir=$dir" --changes --json 2>$null | ConvertFrom-Json
$full = dotnet $tool C:\ "--index-dir=$dir" --rebuild --json 2>$null | ConvertFrom-Json
"incremental after cleanup: mode=$($after.index.mode) total_ms=$($after.index.total_ms) usn_changes=$($after.index.usn_changes) records_reread=$($after.index.records_reread) root_delta=$($after.changes.root_delta)"
"full scan: total_ms=$($full.index.total_ms)"
($after.changes.shrunk | Select-Object -First 5 | Format-Table path, delta | Out-String)
Remove-Item -Recurse -Force $dir
```

Expected: `mode=incremental`; `root_delta` about `-30720000`, give or take whatever else changed on the live C: in
between; the shrunk list includes the `...\Temp` ancestors of the deleted folder; and the incremental `total_ms` is
clearly below the full scan's. Record both totals.

- [ ] **Step 4: Docs**

Append to `docs\design_index.md`, before `## Privacy`:

```markdown
## Changes since the previous run (I5)

With `--changes`, the saved index is aggregated before it is updated, and every directory's size is kept (record
number and sequence, size, parent, name). After the update, each directory at or below the queried one is compared
with the same directory before: same record *and* sequence number. A directory that is gone, or whose record was
reused, is listed by its old path with size 0 after. Changes are listed as "shrunk" and "grew", largest first. A
directory's change includes everything below it, so the parents of a deleted folder are listed too. The baseline is
the index as last written. Run with `--no-save` to keep comparing against the same baseline, for example across
several cleanup steps.
```

Add the C: cleanup numbers to the measurements section:
`Cleanup workflow on C: (30,000 files deleted): incremental <total_ms> ms (<usn_changes> journal entries) vs full scan <total_ms> ms.`

In `docs\roadmap.md`, replace the two I5 items with:

```markdown
- [x] Before/after report: which directories shrank or grew, by how much, since the previous run. `dirsizer-index --changes` (`ChangeReporter`; self-tests; on T:, deleting a 20,000-byte file showed -20,000 for the folder and its parent, and `--no-save` kept the baseline).
- [x] Measure the second run against a full rescan after a realistic cleanup: C:, 30,000 files (30 MiB) deleted, incremental <ms> vs full <ms> (one machine, warm cache).
```

- [ ] **Step 5: README**

In `README.md`, after the `dirsizer-inspect.exe` row of the tools table, add a row:

```markdown
| `dirsizer-index.exe` | **Experimental, not in the release zip.** Repeated analysis from a saved per-volume index | Full `$MFT` scan the first time, then only the records the USN journal names. Any directory on the drive; `--changes` lists what shrank and grew since the previous run. See [docs/design_index.md](docs/design_index.md). |
```

In the project table (the one that maps `src\...\*.csproj` to tool names), add:

```markdown
| `src\DirSizer.Index\DirSizer.Index.csproj` | `dirsizer-index` (experimental) |
```

- [ ] **Step 6: Commit**

```powershell
git add scripts\Test-IndexIncremental.ps1 docs\design_index.md docs\roadmap.md README.md
git commit -m "I5 checkpoint: --changes end to end on T:, cleanup workflow measured on C:, README"
```

---

### Task 20: Final verification

- [ ] **Step 1: Clean build and every self-test**

```powershell
dotnet build DirSizer.sln -c Release
dotnet artifacts\bin\DirSizer.Index\release_win-x64\dirsizer-index.dll --self-test; "index exit=$LASTEXITCODE"
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll --self-test; "mft exit=$LASTEXITCODE"
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test; "fsctl exit=$LASTEXITCODE"
dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test; "dirsizer exit=$LASTEXITCODE"
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer-fs.dll --self-test; "fs exit=$LASTEXITCODE"
```

Expected: `31 self-tests passed, 0 skipped.` for the index, and every exit code 0.

- [ ] **Step 2: Existing tools unchanged (elevated)**

Run: `.\scripts\Compare-Readers.ps1 -Volume T:; "exit=$LASTEXITCODE"`
Expected: all `EQUAL`, `exit=0`. The only shared change on this branch is `RecordFixture`, which is test-only.
Check that with `git diff master --stat -- src\Shared src\DirSizer.Core src\DirSizer src\DirSizer.Bulk src\DirSizer.Fsctl src\DirSizer.Fs src\DirSizer.Fs.Core src\DirSizer.Inspect`:
only `src/Shared/SelfTests.cs` may appear.

- [ ] **Step 3: NativeAOT publish**

Run: `dotnet publish src\DirSizer.Index\DirSizer.Index.csproj -c Release -o $env:TEMP\dirsizer-index-aot`
Expected: no AOT or trim warnings from `DirSizer.Index` sources. Then run
`& $env:TEMP\dirsizer-index-aot\dirsizer-index.exe --self-test`. Expected: 31 passed. Then `Remove-Item -Recurse $env:TEMP\dirsizer-index-aot`.

- [ ] **Step 4: End-to-end once more on the final build (elevated)**

Run: `.\scripts\Test-IndexIncremental.ps1 -Volume T:`. Expected: `ALL CHECKS PASSED`.

- [ ] **Step 5: Hand-off**

Use superpowers:finishing-a-development-branch. Report every recorded number and every check that could not be
run. If the shell was not elevated, list the volume steps as **not run**, not as passed.

---

## Self-review notes

- **Roadmap coverage.** Each roadmap item maps to a task:
  - I2 (format, save/load equals a scan, C: measurement): Tasks 3, 7, 8.
  - I3 (journal handling and fallbacks; apply every change kind by re-reading; verification on T:): Tasks 9-13.
  - I3 "ancestor chain only": rewritten as a measured follow-up (design decision 7, Task 14 gate).
  - I4 (path → FRN, subtree equal to an oracle, one shared index): Tasks 15-16.
  - I5 (before/after report, cleanup measurement): Tasks 17-19.
  - I1 is in its own plan, [2026-09-23-i1-finish-current-scanner.md](2026-09-23-i1-finish-current-scanner.md).
- **Not covered, stated rather than hidden.**
  - A journal wrap on a real volume: only the self-test forces it.
  - Offline changes made by another OS: documented limitation, with `--rebuild` as the answer.
  - ReFS, and more than one machine.
  - Folding the index into `dirsizer.exe`: a promotion decision for later, like bulk's.
- **Type consistency, checked across tasks.**
  - `IndexRun` gains `Changes` only as a last parameter with a default, so `SampleRun` (Task 7) still compiles.
  - `UpdateResult` is defined in Task 7 and first produced in Task 11.
  - `SubtreeQuery.RootRecord` is used by `IndexSnapshot`.
  - `IRecordSource.BytesPerCluster` is used by `AttributeList` and `FakeRecordSource`.
  - Test counts run 2 → 8 → 9 → 11 → 12 → 16 → 19 → 25 → 26 → 27 → 30 → 31.
