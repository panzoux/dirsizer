# dirsizer.exe: unified automatic scan strategy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the design in [docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md](../specs/2026-09-23-unified-scan-strategy-design.md)
exactly, in the same three checkpointed steps the spec defines. Step 1 (Tasks 1-5): extract `DirSizer.Fs.Core`,
rename `dirsizer.exe`→`dirsizer-fs.exe`, introduce `IScanStrategy`/`UnifiedScanResult`/`ScanStrategySelector`
with one strategy, zero behavior change to `dirsizer-fs.exe`. Step 2 (Tasks 6-8): `MftScanner`, the probe,
`dirsizer-bulk.exe`→`dirsizer-mft.exe`. Step 3 (Tasks 9-10): `NtfsFsctlScanner`, `FsctlScanner.cs` moves to
`Shared`. Task 11: `release.ps1`/README/final verification.

**Architecture:** see the spec. In short: `IScanStrategy.Scan(root, UnifiedScanOptions) → UnifiedScanResult`,
thrown `StrategyUnavailableException` only from each strategy's own separate probe call (never from the shared
native code the dedicated executables use), `ScanStrategySelector` tries strategies in a fixed documented order
and catches only that exception type.

**Tech Stack:** C# 12 / .NET 8, NativeAOT, no new P/Invoke beyond what already exists in `BulkReader.cs`/
`FsctlScanner.cs` (this plan reuses their existing `OpenVolume` calls for the probe, does not add new ones).

---

## Ground rules for the executing agent

- Run every command with the **PowerShell tool** from the repository root
  (`C:\Users\user\source\repos\panzoux\dirsizer`), never Bash. Create/edit files with Write/Edit, never shell
  heredocs.
- Work on **`master`** directly is not this plan's call to make — **create a branch first**:
  `git checkout -b feature/unified-scan-strategy` before Task 1's first commit. Never push unasked.
  Commit messages end with `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`.
- **Line endings**: CRLF, UTF-8 no BOM. Run before every commit that touches `src\`:

```powershell
function Normalize-Src {
    foreach ($f in (Get-ChildItem src -Recurse -File -Include *.cs, *.csproj, *.manifest, *.sln | ForEach-Object FullName)) {
        $t = [IO.File]::ReadAllText($f); $x = $t -replace "`r?`n", "`r`n"
        if ($x -ne $t) { [IO.File]::WriteAllText($f, $x, [Text.UTF8Encoding]::new($false)) }
    }
}
```

- **Every file move in this plan is a `git mv`**, not delete+recreate — this preserves history and is how the
  "unchanged" claims in the spec are actually verified (a `git mv` with no further edit in the same commit has
  an empty diff beyond the rename). Verify with `git show --stat <commit>` after each such commit: files should
  show as renames (`R100` / `similarity index 100%`), not as delete+add pairs, wherever content did not change.
- Build/test cycle for the whole solution: `dotnet build DirSizer.sln -c Release`, then run each affected
  tool's `--self-test`. Do not run `dotnet build` on individual `.csproj` files once multiple projects
  reference each other — build the solution, or `dotnet build src\DirSizer\DirSizer.csproj -c Release` which
  pulls in its dependencies.
- **Elevated verification steps** in Tasks 8 and 10 must be run from a real elevated PowerShell window, not
  assumed. If the agent's own shell cannot be elevated, these steps are reported to the user to run by hand,
  with exact commands and expected output given — never silently skipped or claimed done without the output.
- Nothing outside `src\`, `docs\`, `scripts\release.ps1`, `README*.md` may change.

## File structure

| File | Change | Task |
| --- | --- | --- |
| `src\DirSizer.Fs.Core\DirSizer.Fs.Core.csproj` (new) | the extracted engine library | 1 |
| `src\DirSizer.Fs\*.cs` (moved) | `Enumerator*.cs`, `HandleEnumerators.cs`, `Win32Find.cs`, `Walker.cs`, `FsScanner.cs`, `DirModel.cs`, `RootPath.cs`, `FsOptions.cs`, `FsOutput.cs`, `SelfTests*.cs` → `DirSizer.Fs.Core` | 1 |
| `src\DirSizer.Fs\DirSizer.Fs.csproj` | references `DirSizer.Fs.Core`; `AssemblyName` → `dirsizer-fs` | 2 |
| `src\DirSizer.Core\StrategyTypes.cs` (new) | `StrategyUnavailableException`, `VolumeInfo`, `UnifiedScanResult`, `UnifiedItem`, `UnifiedScanOptions` | 3 |
| `src\DirSizer\DirSizer.csproj`, `Program.cs`, `app.manifest` (new project) | the unified `dirsizer.exe` host | 4 |
| `src\DirSizer\IScanStrategy.cs`, `ScanStrategySelector.cs`, `FileSystemStrategy.cs` (new) | Step 1 strategy plumbing | 4 |
| `src\DirSizer\SelfTests*.cs` (new) | Step 1 unified-exe tests | 5 |
| `src\DirSizer\MftStrategy.cs` (new), `ScanResultAdapter.cs` (new, in `DirSizer.Core`) | Step 2 | 6, 7 |
| `src\DirSizer.Bulk\DirSizer.Bulk.csproj` | `AssemblyName` → `dirsizer-mft` | 6 |
| `src\DirSizer\FsctlStrategy.cs` (new) | Step 3 | 9 |
| `src\Shared\FsctlScanner.cs` (moved from `src\DirSizer.Fsctl\`) | Step 3 | 9 |
| `scripts\release.ps1`, `README.md`, `README-jp.md` | new/renamed exe entries, usage text | 11 |

---

## Step 1

### Task 1: Extract `DirSizer.Fs.Core`

**Files:**
- Create: `src\DirSizer.Fs.Core\DirSizer.Fs.Core.csproj`
- Move (`git mv`): `Enumerator.cs`, `EnumeratorFallback.cs`, `EnumeratorSpec.cs`, `FindFirstEnumerator.cs`,
  `HandleEnumerators.cs`, `Win32Find.cs`, `Walker.cs`, `FsScanner.cs`, `DirModel.cs`, `RootPath.cs`,
  `FsOptions.cs`, `FsOutput.cs`, `SelfTests.cs`, `SelfTests.Cli.cs`, `SelfTests.Enumerators.cs`,
  `SelfTests.Output.cs`, `SelfTests.Reader.cs`, `SelfTests.Walk.cs`, from `src\DirSizer.Fs\` to
  `src\DirSizer.Fs.Core\`

- [ ] **Step 1: Confirm the exact file list before moving anything**

```powershell
Get-ChildItem src\DirSizer.Fs -File | Select-Object -ExpandProperty Name
```

Expected: `DirSizer.Fs.csproj`, `Program.cs`, `app.manifest`, plus exactly the 17 files listed above. If this
list differs from what is listed (a file added/removed since this plan was written), stop and reconcile —
`Program.cs`/`app.manifest`/`DirSizer.Fs.csproj` are the only three that stay; everything else moves.

- [ ] **Step 2: Create the new project**

`src\DirSizer.Fs.Core\DirSizer.Fs.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- The dirsizer.exe / dirsizer-fs.exe scan engine: any filesystem, no elevation. Referenced by the
       dedicated dirsizer-fs.exe host and by the unified dirsizer.exe's FileSystemStrategy. -->
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

(No `OutputType`, so it defaults to a library — matches `DirSizer.Core.csproj`'s own shape. No
`RuntimeIdentifier`/`PublishAot`/`ApplicationManifest`: those are host-project concerns, not a library's.)

- [ ] **Step 3: Move the files**

```powershell
foreach ($f in 'Enumerator.cs','EnumeratorFallback.cs','EnumeratorSpec.cs','FindFirstEnumerator.cs','HandleEnumerators.cs','Win32Find.cs','Walker.cs','FsScanner.cs','DirModel.cs','RootPath.cs','FsOptions.cs','FsOutput.cs','SelfTests.cs','SelfTests.Cli.cs','SelfTests.Enumerators.cs','SelfTests.Output.cs','SelfTests.Reader.cs','SelfTests.Walk.cs') {
    git mv "src\DirSizer.Fs\$f" "src\DirSizer.Fs.Core\$f"
}
Get-ChildItem src\DirSizer.Fs -File | Select-Object -ExpandProperty Name
```

Expected: only `DirSizer.Fs.csproj`, `Program.cs`, `app.manifest` remain in `src\DirSizer.Fs\`.

- [ ] **Step 4: Add the new project to the solution**

```powershell
dotnet sln DirSizer.sln add src\DirSizer.Fs.Core\DirSizer.Fs.Core.csproj
```

- [ ] **Step 5: `DirSizer.Fs.csproj` references it**

Read the current file first (it may have drifted from what an earlier session saw — this plan's Task 2
updates its `AssemblyName`; do that in the same edit if convenient, or in Task 2, but the `ProjectReference`
must be added now for Step 6 below to compile):

```powershell
Get-Content src\DirSizer.Fs\DirSizer.Fs.csproj
```

Add, inside the existing `<Project>` element (create an `<ItemGroup>` if none exists):

```xml
  <ItemGroup>
    <ProjectReference Include="..\DirSizer.Fs.Core\DirSizer.Fs.Core.csproj" />
  </ItemGroup>
```

- [ ] **Step 6: Build and test — this is the acceptance check for the whole task**

```powershell
Normalize-Src
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, `39 self-tests passed, 0 skipped.` — identical to before the move, because nothing but
file location changed. If there are compile errors about types not found, the moved files most likely relied
on something that stayed behind (there should be nothing — `Program.cs` only calls into public/internal types
the moved files define) — read the error and fix the reference, do not move files back to paper over it.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m @'
Extract DirSizer.Fs.Core: the filesystem-scan engine as a library, referenced by DirSizer.Fs

Pure move, no behavior change -- 39 self-tests still pass. Sets up Step 1's dirsizer-fs.exe rename and the
new unified dirsizer.exe, both of which need this engine as a library, not baked into one exe project.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

Verify the move was recorded as renames, not delete+add:

```powershell
git show --stat HEAD | Select-String "DirSizer.Fs"
```

---

### Task 2: `dirsizer-fs.exe` rename

**Files:**
- Modify: `src\DirSizer.Fs\DirSizer.Fs.csproj`

- [ ] **Step 1: Change `AssemblyName`**

Read the current file, then replace the `<AssemblyName>dirsizer</AssemblyName>` line with
`<AssemblyName>dirsizer-fs</AssemblyName>`. Nothing else in this file changes (the `ProjectReference` from
Task 1 Step 5 stays; `ApplicationManifest`, `PublishAot`, etc. are unaffected by the name).

- [ ] **Step 2: Build, publish, test — the "zero behavior change" acceptance check**

```powershell
Normalize-Src
dotnet build src\DirSizer.Fs\DirSizer.Fs.csproj -c Release 2>&1 | Select-Object -Last 4
Get-ChildItem artifacts\bin\DirSizer.Fs\release_win-x64\*.exe
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer-fs.dll --self-test | Select-Object -Last 1
$env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish src\DirSizer.Fs\DirSizer.Fs.csproj -c Release -r win-x64 2>&1 | Select-Object -Last 3
$exe = (Resolve-Path .\artifacts\publish\DirSizer.Fs\release_win-x64\dirsizer-fs.exe).Path
& $exe --self-test | Select-Object -Last 1; "exit: $LASTEXITCODE"
& $exe 'C:\Windows\System32\drivers' --json --top=1 | ConvertFrom-Json | Select-Object -ExpandProperty root
```

Expected: the built DLL is `dirsizer-fs.dll` (not `dirsizer.dll`); `39 self-tests passed, 0 skipped.` in both
JIT and NativeAOT; a real scan runs and prints a `root` object exactly as `dirsizer.exe` used to.

- [ ] **Step 3: Commit**

```powershell
git add src\DirSizer.Fs\DirSizer.Fs.csproj
git commit -m @'
Rename dirsizer.exe to dirsizer-fs.exe

Pure AssemblyName change. The unified dirsizer.exe (Task 4) is the new home for the unqualified name.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 3: `DirSizer.Core` additions

**Files:**
- Create: `src\DirSizer.Core\StrategyTypes.cs`

- [ ] **Step 1: Write the file**

```csharp
using System.Runtime.InteropServices;
using System.Text;

// Thrown only from a strategy's own availability probe (see the unified-scan-strategy design spec, "The
// availability/failure boundary"). Never thrown for any other reason, never caught anywhere but
// ScanStrategySelector, and never used to convert a real scan failure into "try something else".
public sealed class StrategyUnavailableException(string strategy, Exception cause) : Exception($"{strategy}: {cause.Message}", cause)
{
    public string Strategy { get; } = strategy;
}

// "Is this drive NTFS", extracted from the same GetVolumeInformation P/Invoke FsctlScanner.cs already
// declares privately for its own error messages (that copy stays there until Step 3 moves the whole file and
// switches it to call this instead -- see the design spec).
public static class VolumeInfo
{
    public static bool IsNtfs(string driveRoot)
    {
        var name = new StringBuilder(261);
        return GetVolumeInformation($"{driveRoot}\\", null, 0, out _, out _, out _, name, name.Capacity)
            && name.ToString() == "NTFS";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string rootPath, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint filesystemFlags, StringBuilder filesystemName, int filesystemNameSize);
}

// dirsizer.exe's own, uniform result -- every IScanStrategy adapts its native result into this. Dedicated
// executables are unaffected: they call their native Scan/Run method directly and print their own, unchanged,
// richer output. See the design spec, "IScanStrategy".
public sealed record UnifiedScanResult(
    string Volume, int Top,
    UnifiedItem Root, UnifiedItem[] RootChildren, UnifiedItem[] Directories, UnifiedItem[] Files,
    long DirectoriesScanned, long Unreadable, long FileCount, long Bytes, string[] ErrorSamples,
    string Strategy, string? StrategyFallback, string? StrategyFallbackReason, double TotalMs);

public readonly record struct UnifiedItem(string Path, long Size);

// What dirsizer.exe itself accepts (see the design spec, "Common CLI options"), before each strategy's own
// adapter translates it into that strategy's native settings type.
public sealed record UnifiedScanOptions(int Top, bool Files, bool Dirs, bool Strict, int Workers);
```

- [ ] **Step 2: Write a self-test for `VolumeInfo.IsNtfs`**

`DirSizer.Core` has no existing self-test file or `--self-test` entry point of its own (it is a pure library,
tested only through the tools that reference it). Add the test to `DirSizer.Fsctl`'s self-tests instead (it
already references `DirSizer.Core` and already runs on every `--self-test`), in `src\DirSizer.Fsctl\SelfTests.cs`
— read that file first to match its exact `SelfTest`-registration pattern (it is a different, older harness
than `DirSizer.Fs.Core`'s `FsSelfTests`, with its own `Add`/assertion helpers; use whatever that file already
uses, do not introduce a second convention). The test: `VolumeInfo.IsNtfs("C:")` is `true` on this development
machine's `C:` (NTFS, established fact from every previous phase of this project); a drive letter that does
not exist (`"ZZ:"` is safe — `DriveRoot.Validate`-shaped strings only, two characters, colon) returns `false`
rather than throwing (`GetVolumeInformation` fails gracefully for a non-existent drive, it does not throw).

- [ ] **Step 3: Build and test**

```powershell
Normalize-Src
dotnet build src\DirSizer.Fsctl\DirSizer.Fsctl.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, one more passing test than `DirSizer.Fsctl` had before (check its current count with
`git show HEAD:src\DirSizer.Fsctl\SelfTests.cs` if unsure, or just confirm the new test's name appears in the
output and nothing failed).

- [ ] **Step 4: Commit**

```powershell
git add src\DirSizer.Core\StrategyTypes.cs src\DirSizer.Fsctl\SelfTests.cs
git commit -m @'
DirSizer.Core: StrategyUnavailableException, VolumeInfo.IsNtfs, UnifiedScanResult/UnifiedItem/UnifiedScanOptions

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 4: The unified `dirsizer.exe`, Step 1 (one strategy)

**Files:**
- Create: `src\DirSizer\DirSizer.csproj`, `src\DirSizer\Program.cs`, `src\DirSizer\app.manifest`
- Create: `src\DirSizer\IScanStrategy.cs`, `src\DirSizer\ScanStrategySelector.cs`, `src\DirSizer\FileSystemStrategy.cs`

- [ ] **Step 1: The project file**

`src\DirSizer\DirSizer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- dirsizer.exe: the unified, automatic entry point. Picks the best available, validated scan strategy for
       the path's file system and runs it -- see docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md. -->
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
  <ItemGroup>
    <ProjectReference Include="..\DirSizer.Fs.Core\DirSizer.Fs.Core.csproj" />
    <ProjectReference Include="..\DirSizer.Core\DirSizer.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: The manifest (non-elevated, same as today's `dirsizer.exe`)**

`src\DirSizer\app.manifest`: identical content to `src\DirSizer.Fs\app.manifest` (read it first, copy exactly
— `asInvoker`, not `requireAdministrator`). This is the file whose meaning the design spec's "Executable
structure" section explains: it does not force elevation, but does not prevent running with an already-elevated
token either.

- [ ] **Step 3: `IScanStrategy`**

`src\DirSizer\IScanStrategy.cs`:

```csharp
// See docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md, "IScanStrategy".
interface IScanStrategy
{
    string Name { get; }   // "mft", "fsctl", "filesystem"

    // Throws StrategyUnavailableException if this strategy cannot run here at all (see the design spec's
    // availability/failure boundary). Any other exception is a real scan failure.
    UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options);
}
```

- [ ] **Step 4: `FileSystemStrategy`, the adapter from `FsResult`**

First, read `src\DirSizer.Fs.Core\FsScanner.cs` and `src\DirSizer.Fs.Core\FsOptions.cs` in full (they moved in
Task 1, this step needs their exact current field names) to confirm the field names used below still match —
this plan was written against the versions read earlier in this session; if `FsResult`/`FsCounters`/
`ScanSettings` have changed since, adapt the mapping, do not guess.

`src\DirSizer\FileSystemStrategy.cs`:

```csharp
sealed class FileSystemStrategy : IScanStrategy
{
    public string Name => "filesystem";

    public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var settings = new ScanSettings(
            options.Workers == 0 ? FsOptions.DefaultWorkers : options.Workers,
            options.Top,
            options.Files,
            ShowProgress: !Console.IsErrorRedirected);
        var result = FsScanner.Scan(rootPath, settings);
        return Adapt(result, options.Top);
    }

    static UnifiedScanResult Adapt(FsResult result, int top)
    {
        var c = result.Counters;
        return new UnifiedScanResult(
            result.RootPath, top,
            new UnifiedItem(result.Root.Path, result.Root.Size),
            Items(result.RootChildren), Items(result.Directories), Items(result.Files),
            c.DirectoriesScanned, c.DirectoriesDenied + c.DirectoriesFailed, c.Files, c.Bytes, result.ErrorSamples,
            "filesystem", null, null, result.Metrics.Total.TotalMilliseconds);
    }

    static UnifiedItem[] Items(ResultItem[] items)
    {
        var result = new UnifiedItem[items.Length];
        for (var i = 0; i < items.Length; i++) result[i] = new UnifiedItem(items[i].Path, items[i].Size);
        return result;
    }
}
```

(`ScanSettings`'s real constructor also takes `CancellationToken`/`IEnumeratorFactory?` with default values —
omitted here since the unified exe does not expose `--enumerator` or cancellation in Step 1; if `ScanSettings`
requires them positionally rather than as defaults when read in this step, adjust to pass `default`/`null`
explicitly. `FsOptions.DefaultWorkers` is `public static` — confirm when reading the file; if it is not, this
step also makes it `public`, since `DirSizer.Fs.csproj`'s own `Program.cs` is in a different assembly now too
after Task 1 and needs the same access.)

- [ ] **Step 5: `ScanStrategySelector`, Step 1 shape (one strategy)**

`src\DirSizer\ScanStrategySelector.cs`:

```csharp
// See the design spec, "ScanStrategySelector". Step 1: exactly one strategy, always chosen -- no NTFS check,
// no drive-letter check, no probing. Step 2/3 add MftScanner/NtfsFsctlScanner ahead of FileSystemStrategy in
// the list below, plus the drive-letter/NTFS pre-checks; nothing about this method's shape needs to change,
// only the strategies list and the checks before the loop.
static class ScanStrategySelector
{
    public static UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        IScanStrategy[] strategies = [new FileSystemStrategy()];
        Exception? lastUnavailable = null;
        foreach (var strategy in strategies)
        {
            try
            {
                return strategy.Scan(rootPath, options);
            }
            catch (StrategyUnavailableException unavailable)
            {
                lastUnavailable = unavailable;
            }
        }
        // Unreachable in Step 1 (FileSystemStrategy never throws StrategyUnavailableException -- it has no
        // capability requirement), kept because Step 2/3 make it reachable and the shape should not change then.
        throw new InvalidOperationException("No scan strategy is available.", lastUnavailable);
    }
}
```

- [ ] **Step 6: `Program.cs`**

Read `src\DirSizer.Fs\Program.cs`'s current content (Task 1/2 did not change it) for the exact error-handling
pattern to match. `src\DirSizer\Program.cs`:

```csharp
UnifiedCliOptions options;
try
{
    options = UnifiedCliOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    UnifiedCliOptions.PrintHelp();
    return 0;
}
if (options.SelfTest) return DirSizerSelfTests.Run();

try
{
    var result = ScanStrategySelector.Scan(options.Root, options.ToUnifiedScanOptions());
    UnifiedOutput.Write(result, options, Console.Out, Console.Error);
    return options.Strict && result.Unreadable > 0 ? 3 : 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
```

This references `UnifiedCliOptions` and `UnifiedOutput`, written next.

- [ ] **Step 7: `UnifiedCliOptions`**

`src\DirSizer\UnifiedCliOptions.cs` (new file, add to the "Files" list mentally — the plan's table above
covers it under "Program.cs" since it is part of the same host):

```csharp
// dirsizer.exe's own option set -- see the design spec, "Common CLI options". Deliberately smaller than
// FsOptions: no --enumerator, no --strategy, nothing that names an implementation.
sealed class UnifiedCliOptions
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
    public int Workers { get; private set; }   // 0 = automatic

    public static UnifiedCliOptions Parse(string[] args)
    {
        var result = new UnifiedCliOptions();
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

    public UnifiedScanOptions ToUnifiedScanOptions() => new(Top, Files, Dirs, Strict, Workers);

    public static void PrintHelp()
    {
        Console.WriteLine($"""
            dirsizer - fast, read-only folder size scanner: automatically picks the best available scan method

            Usage: dirsizer <path> [--top=N] [--files] [--dirs] [--json] [--workers N] [--strict]

            <path>      Directory to scan: a drive (C:\), a share (\\server\share), or any folder.
            --top=N     Show the largest N results (default: 25)
            --files     Include largest files
            --dirs      Include largest directories (default)
            --json      Write machine-readable JSON to stdout
            --workers N Only meaningful when the generic filesystem strategy runs (default: automatic; 1-256)
            --strict    Exit with code 3 if part of the scan could not be completed
            --benchmark Print total time and the strategy used to stderr
            --self-test Run the built-in tests (uses a temporary folder)
            -h          Show this help

            Reads the whole drive's $MFT directly when NTFS and an administrator token are both available
            (the fastest method); otherwise reads NTFS through FSCTL_GET_NTFS_FILE_RECORD if that is
            available; otherwise walks the directory tree, which works on any file system without elevation.
            The strategy used is reported with --benchmark or --json, never required to understand the result.
            To force one specific method (for comparison, diagnosis, or an automated script that must not
            depend on which one ran), use dirsizer-fs.exe, dirsizer-mft.exe or dirsizer-fsctl.exe directly.

            A path that is not a whole drive always uses the filesystem strategy (the other two only read an
            entire volume).

            Exit codes: 0 result written; 1 error; 3 with --strict, part of the scan could not be completed.
            """);
    }
}
```

- [ ] **Step 8: `UnifiedOutput`**

`src\DirSizer\UnifiedOutput.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

static class UnifiedOutput
{
    public static void Write(UnifiedScanResult result, UnifiedCliOptions options, TextWriter output, TextWriter error)
    {
        if (options.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(ToJson(result, options.Top), UnifiedJsonContext.Default.JsonUnifiedOutput));
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
            output.WriteLine();
            output.WriteLine("Summary");
            output.WriteLine($"strategy={result.Strategy} directories_scanned={result.DirectoriesScanned} unreadable={result.Unreadable} files={result.FileCount} bytes={result.Bytes}");
        }
        if (result.Unreadable > 0)
        {
            error.WriteLine($"warning: part of the scan could not be completed (unreadable={result.Unreadable}); the sizes are a lower bound.");
            foreach (var sample in result.ErrorSamples) error.WriteLine($"  failed: {sample}");
        }
        if (result.StrategyFallback is not null)
            error.WriteLine($"warning: strategy {result.Strategy} fell back to {result.StrategyFallback}: {result.StrategyFallbackReason}");
        if (options.Benchmark)
        {
            var fallback = result.StrategyFallback is null ? "" : $" (fallback: {result.StrategyFallback}, reason={result.StrategyFallbackReason})";
            error.WriteLine($"benchmark: strategy={result.Strategy}{fallback}, total_ms={result.TotalMs:F1}");
        }
    }

    static JsonUnifiedOutput ToJson(UnifiedScanResult result, int top) => new(
        result.Volume, top, "logical",
        new JsonUnifiedItem(result.Root.Path, result.Root.Size),
        Items(result.RootChildren), Items(result.Directories), Items(result.Files),
        new JsonUnifiedStatistics(result.DirectoriesScanned, result.Unreadable, result.FileCount, result.Bytes, result.ErrorSamples,
            new JsonUnifiedPerformance(result.Strategy, result.StrategyFallback, result.StrategyFallbackReason, result.TotalMs)));

    static JsonUnifiedItem[] Items(UnifiedItem[] items)
    {
        var result = new JsonUnifiedItem[items.Length];
        for (var i = 0; i < items.Length; i++) result[i] = new JsonUnifiedItem(items[i].Path, items[i].Size);
        return result;
    }
}

sealed record JsonUnifiedOutput(string Volume, int Top, string SizeMode, JsonUnifiedItem Root, JsonUnifiedItem[] RootChildren, JsonUnifiedItem[] Directories, JsonUnifiedItem[] Files, JsonUnifiedStatistics Statistics);
sealed record JsonUnifiedItem(string Path, long Size);
sealed record JsonUnifiedStatistics(long DirectoriesScanned, long Unreadable, long FileCount, long Bytes, string[] ErrorSamples, JsonUnifiedPerformance Performance);
sealed record JsonUnifiedPerformance(string Strategy, string? StrategyFallback, string? StrategyFallbackReason, double TotalMs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonUnifiedOutput))]
partial class UnifiedJsonContext : JsonSerializerContext;
```

- [ ] **Step 9: Add to the solution and build**

```powershell
dotnet sln DirSizer.sln add src\DirSizer\DirSizer.csproj
Normalize-Src
dotnet build src\DirSizer\DirSizer.csproj -c Release 2>&1 | Select-Object -Last 10
```

Expected: compile errors are likely on the first attempt (field-name mismatches between this plan's sketch and
the real `FsResult`/`ScanSettings`/`FsOptions` shapes) — fix `FileSystemStrategy.cs`/`Program.cs` against
whatever `src\DirSizer.Fs.Core\FsScanner.cs`/`FsOptions.cs` actually declare (read them, do not guess again),
until `0 Error(s)`.

- [ ] **Step 10: Manual smoke test (self-tests come in Task 5)**

```powershell
$exe = (Resolve-Path .\artifacts\bin\DirSizer\release_win-x64\dirsizer.dll -ErrorAction SilentlyContinue)
dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll 'C:\Windows\System32\drivers' --json --top=1 --benchmark
```

Expected: valid JSON on stdout with `"strategy":"filesystem"`; the benchmark line on stderr also says
`strategy=filesystem`; the root size is the same as `dirsizer-fs.exe`'s own scan of the same path (spot check
by running that tool too, per Task 2 Step 2's build).

- [ ] **Step 11: Commit**

```powershell
Normalize-Src
git add -A
git commit -m @'
Add the unified dirsizer.exe: IScanStrategy, ScanStrategySelector, FileSystemStrategy (Step 1, one strategy)

No --enumerator, no strategy flag -- UnifiedCliOptions is deliberately smaller than dirsizer-fs.exe's own
options, per the design spec. Self-tests are Task 5.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 5: Step 1 self-tests, NativeAOT canary, verification

**Files:**
- Create: `src\DirSizer\SelfTests.cs`

- [ ] **Step 1: Write the self-test harness and the Step 1 acceptance tests**

`src\DirSizer\SelfTests.cs` — a minimal harness in the same style as `DirSizer.Fsctl`'s (read
`src\DirSizer.Fsctl\SelfTests.cs` first and match its `SelfTest`/assertion pattern, since `DirSizer` is a new,
separate project with its own tiny `--self-test`, not a reuse of `FsSelfTests` — `DirSizer.Fs.Core`'s own 39
tests already cover the engine and run via `dirsizer-fs.exe --self-test`; this file only tests the
selector/adapter/CLI layer that is unique to `DirSizer`). Tests:

1. `ScanStrategySelectorPicksFileSystem`: scan a small real temp directory (create one, a couple of files),
   confirm `result.Strategy == "filesystem"`.
2. `UnifiedResultMatchesFileSystemScannerSubstance`: scan the same temp directory two ways —
   `FileSystemStrategy().Scan(...)` (this project) and, directly, `FsScanner.Scan(...)` from
   `DirSizer.Fs.Core` (add a `ProjectReference` is already present via `DirSizer.Fs.Core`) — assert
   `result.Root.Size == fsResult.Root.Size`, `result.DirectoriesScanned == fsResult.Counters.DirectoriesScanned`,
   `result.FileCount == fsResult.Counters.Files`, `result.Bytes == fsResult.Counters.Bytes`, and that
   `result.Directories`/`result.Files` (path, size pairs) match `fsResult.Directories`/`fsResult.Files`
   one-to-one after sorting by path. This is the "no behavior change" acceptance check the design spec's
   Testing section names for Step 1.
3. `UnifiedCliOptionsRejectsEnumeratorAndStrategyFlags`: `UnifiedCliOptions.Parse(["x", "--enumerator=find"])`
   throws `ArgumentException` (it is simply not a recognized option, same as any other unknown flag) — proves
   the CLI surface really does not accept it, not just that nothing currently reads it.
4. `UnifiedCliOptionsAcceptsTheDocumentedSet`: parse one of everything (`--top`, `--files`, `--dirs`, `--json`,
   `--workers`, `--strict`, `--benchmark`) and confirm each field is set; confirm defaults (`Top == 25`,
   `Dirs == true`, everything else `false`/`0`) with no arguments beyond a path.

- [ ] **Step 2: Build and run**

```powershell
Normalize-Src
dotnet build src\DirSizer\DirSizer.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, all 4 tests pass (exact count depends on how Step 1 split them; report the real number
reached, do not assume 4 if the harness naturally groups them differently).

- [ ] **Step 3: NativeAOT canary**

```powershell
$env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish src\DirSizer\DirSizer.csproj -c Release -r win-x64 2>&1 | Select-Object -Last 3
$exe = (Resolve-Path .\artifacts\publish\DirSizer\release_win-x64\dirsizer.exe).Path
& $exe --self-test | Select-Object -Last 1; "exit: $LASTEXITCODE"
```

Expected: publish succeeds, all tests pass natively, `exit: 0`.

- [ ] **Step 4: Full-solution verification (Step 1's real finish line)**

```powershell
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 3
dotnet artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer-fs.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.dll --self-test | Select-Object -Last 1
git diff --stat master -- src/DirSizer.Bulk src/DirSizer.Fsctl src/DirSizer.Inspect src/DirSizer.Compare src/Shared
```

Expected: solution builds clean; `dirsizer-fs.dll` 39/39; `dirsizer.dll` (unified) all Step-1 tests pass;
`dirsizer-fsctl.dll` 1 more than before Task 3 (the `VolumeInfo.IsNtfs` test); `dirsizer-bulk.dll`,
`dirsizer-inspect.dll`, `DirSizer.Compare.dll` completely unchanged output (Step 1 never touches them —
`git diff --stat` for those five paths must print nothing).

- [ ] **Step 5: Commit**

```powershell
Normalize-Src
git add -A
git commit -m @'
Step 1 self-tests: selector/adapter/CLI coverage for the unified dirsizer.exe, NativeAOT verified

No behavior change confirmed at the scan-substance level against dirsizer-fs.exe (not raw JSON, which
differs by design). This completes Step 1 of the unified-scan-strategy plan.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

## Step 2

### Task 6: `MftScanner` strategy and the probe

**Files:**
- Create: `src\DirSizer\MftStrategy.cs`
- Modify: `src\DirSizer\DirSizer.csproj` (add the shared `BulkReader` compile-includes and a `ProjectReference`
  or `<Compile Include>` for `BulkIntegration.cs`/`ScanProgress` — see Step 1 below, which resolves the exact
  mechanism by reading the real files first)
- Modify: `src\DirSizer.Bulk\DirSizer.Bulk.csproj` (`AssemblyName` → `dirsizer-mft`)
- Modify: `src\DirSizer\ScanStrategySelector.cs`, `src\DirSizer\Program.cs` (drive-letter/NTFS pre-checks)
- Create: `src\DirSizer.Core\ScanResultAdapter.cs` (the shared `ScanResult → UnifiedScanResult` adapter)

- [ ] **Step 1: Work out exactly how `DirSizer` reaches `BulkIntegration.Run`/`Options`/`ScanResult`**

Read, in full, at the start of this task (not from memory of an earlier planning pass):
`src\DirSizer.Bulk\DirSizer.Bulk.csproj`, `src\DirSizer.Bulk\BulkIntegration.cs`, `src\Shared\Cli.cs`,
`src\Shared\BulkReader\BulkReader.cs`, `src\Shared\BulkReader\BulkScanner.cs`. Confirm: `BulkIntegration.Run`
takes the shared `Options` (from `Cli.cs`) and returns `ScanResult` (same file). `Options` has only private
setters, populated by `Options.Parse(string[])` — there is no public way to construct one field-by-field today.
**Add one**, in `src\Shared\Cli.cs` (this is the one small, additive, non-behavior-changing edit `Shared\Cli.cs`
needs — it does not change `Parse` or anything `dirsizer-fsctl.exe`/`dirsizer-bulk.exe` already do):

```csharp
    // For a caller that already has validated values, not raw argv (the unified dirsizer.exe's MftStrategy).
    // Does not go through Parse's validation (the caller is responsible for a sane volume string).
    public static Options From(string volume, int top, bool files, bool dirs) =>
        new() { Volume = volume, Top = top, Files = files, Dirs = dirs };
```

Add this as a new `public static` method on `Options` in `src\Shared\Cli.cs`, immediately after `Parse`. Verify
it compiles for all three existing consumers of `Cli.cs` (`DirSizer.Fsctl`, `DirSizer.Bulk`, `DirSizer.Compare`
if it includes this file — check) before moving on:

```powershell
Normalize-Src
dotnet build src\DirSizer.Fsctl\DirSizer.Fsctl.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet build src\DirSizer.Bulk\DirSizer.Bulk.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)` for both, self-test counts unchanged from before this step (this is purely additive).

- [ ] **Step 2: Wire `DirSizer.csproj` to the same shared source `DirSizer.Bulk` uses**

Read `src\DirSizer.Bulk\DirSizer.Bulk.csproj`'s `<ItemGroup>` (already read earlier in this session — it
`<Compile Include>`s `..\Shared\ConsolePause.cs`, `..\Shared\Cli.cs`, `..\Shared\DriveRoot.cs`,
`..\Shared\SelfTests.cs`, `..\Shared\BulkReader\BulkReader.cs`, `..\Shared\BulkReader\BulkStability.cs`,
`..\Shared\BulkReader\BulkScanner.cs`, `..\Shared\BulkReader\BulkReport.cs`, and references
`DirSizer.Core.csproj`). `DirSizer.csproj` needs the same shared files **except** `..\Shared\SelfTests.cs` (that
is `DirSizer.Bulk`'s own self-test harness scaffolding, a different, incompatible one from what Task 5 built
for `DirSizer`; do not include both) and **except** `..\Shared\ConsolePause.cs` (a Windows-Terminal-focused
console-pause-on-exit helper for the elevated, double-click-launched NTFS tools — `dirsizer.exe` is
`asInvoker`/normal, launched like any other CLI tool, and should not pause). It also needs `BulkIntegration.cs`
itself — but that file lives in `src\DirSizer.Bulk\`, not `Shared\`, and referencing it via `<Compile Include>`
from a sibling project's own folder is the same pattern already used for `Shared\` files, just pointed at
`DirSizer.Bulk`'s folder instead: `<Compile Include="..\DirSizer.Bulk\BulkIntegration.cs" />`.

Add to `src\DirSizer\DirSizer.csproj`'s `<ItemGroup>` (alongside the existing `ProjectReference`s):

```xml
    <Compile Include="..\Shared\Cli.cs" />
    <Compile Include="..\Shared\DriveRoot.cs" />
    <Compile Include="..\Shared\BulkReader\BulkReader.cs" />
    <Compile Include="..\Shared\BulkReader\BulkStability.cs" />
    <Compile Include="..\Shared\BulkReader\BulkScanner.cs" />
    <Compile Include="..\Shared\BulkReader\BulkReport.cs" />
    <Compile Include="..\DirSizer.Bulk\BulkIntegration.cs" />
```

`DirSizer.csproj` already references `DirSizer.Core.csproj` (Task 4 Step 1), which these files also need — no
further reference change required there.

- [ ] **Step 3: The shared `ScanResult → UnifiedScanResult` adapter**

`src\DirSizer.Core\ScanResultAdapter.cs`:

```csharp
// Shared by MftStrategy and (Step 3) FsctlStrategy: both produce the same native ScanResult (see the design
// spec, "MftScanner (Step 2) and NtfsFsctlScanner (Step 3) share one adapter"). Read src\Shared\Cli.cs's
// ScanResult/FileRecord/ScanMetrics definitions before trusting the field names below -- confirm, don't guess.
public static class ScanResultAdapter
{
    public static UnifiedScanResult Adapt(ScanResult result, string strategy, int top, double totalMs)
    {
        return new UnifiedScanResult(
            result.Volume, top,
            new UnifiedItem(RecordPaths.Build(result.Volume, result.Records, result.Root), result.Root.Size),
            Items(result, result.RootChildren, record => record.IsDirectory ? record.Size : record.LogicalSize),
            Items(result, result.Directories, record => record.Size),
            Items(result, result.Files, record => record.LogicalSize),
            result.Scanned, result.Skipped, CountFiles(result), result.Root.Size, [],
            strategy, null, null, totalMs);
    }

    static long CountFiles(ScanResult result)
    {
        long count = 0;
        foreach (var record in result.Records.Values)
            if (!record.IsDirectory) count++;
        return count;
    }

    static UnifiedItem[] Items(ScanResult result, FileRecord[] records, Func<FileRecord, long> size)
    {
        var items = new UnifiedItem[records.Length];
        for (var i = 0; i < records.Length; i++) items[i] = new UnifiedItem(RecordPaths.Build(result.Volume, result.Records, records[i]), size(records[i]));
        return items;
    }
}
```

(`result.Root.Size` used as `Bytes` above assumes the root's own size already equals the whole-volume total —
confirm this against `FileSystemStrategy`'s own `Adapt`, which uses `c.Bytes` from a running total, not the
root's size; if `ScanResult` has no equivalent running total separate from the root record's own `Size` field,
using the root's size is correct and equivalent — `FsResult.Counters.Bytes` and `FsResult.Root.Size` are
likewise expected to be equal for a root scan, per `FsScanner.cs`'s own construction. Verify this assumption
against the real `ScanResult`/`FileRecord` fields when writing this step; do not carry the assumption into the
adapter uncritically if the fields say otherwise.)

- [ ] **Step 4: `MftStrategy`, with its own separate probe**

`src\DirSizer\MftStrategy.cs`:

```csharp
using System.ComponentModel;

// See the design spec, "The availability/failure boundary". The probe is a separate, redundant OpenVolume
// call -- BulkIntegration.Run/BulkScanner.Scan are never modified, so dirsizer-mft.exe's behavior cannot
// regress from this file existing.
sealed class MftStrategy : IScanStrategy
{
    public string Name => "mft";

    public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var drive = DriveRoot.Validate(rootPath);
        try
        {
            using var probe = BulkNative.OpenVolume($"\\\\.\\{drive}");
        }
        catch (Win32Exception cause)
        {
            throw new StrategyUnavailableException("mft", cause);
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = BulkIntegration.Run(Options.From(drive, options.Top, options.Files, options.Dirs));
        return ScanResultAdapter.Adapt(result, "mft", options.Top, started.Elapsed.TotalMilliseconds);
    }
}
```

`BulkNative` is `internal`/file-scoped in the shared `BulkReader.cs` today — check its accessibility when
reading that file in Step 1 above; if it is not visible from `DirSizer`'s assembly (it should be, since
`BulkReader.cs` is compiled directly into `DirSizer` via `<Compile Include>`, making `BulkNative` part of the
same assembly, not a cross-assembly reference — confirm this reasoning holds once actually built, since it is
exactly why `<Compile Include>` was chosen over a `ProjectReference` to `DirSizer.Bulk` itself).

- [ ] **Step 5: Wire into the selector**

Update `src\DirSizer\ScanStrategySelector.cs`: the strategies list becomes
`IScanStrategy[] strategies = [new MftStrategy(), new FileSystemStrategy()];` when the pre-checks below say
NTFS; otherwise the list is just `[new FileSystemStrategy()]`. Concretely, change the method to:

```csharp
    public static UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var strategies = CandidateStrategies(rootPath);
        Exception? lastUnavailable = null;
        foreach (var strategy in strategies)
        {
            try
            {
                return strategy.Scan(rootPath, options);
            }
            catch (StrategyUnavailableException unavailable)
            {
                lastUnavailable = unavailable;
            }
        }
        throw new InvalidOperationException("No scan strategy is available.", lastUnavailable);
    }

    static IScanStrategy[] CandidateStrategies(string rootPath)
    {
        string drive;
        try { drive = DriveRoot.Validate(rootPath); }
        catch (ArgumentException) { return [new FileSystemStrategy()]; }
        return VolumeInfo.IsNtfs(drive) ? [new MftStrategy(), new FileSystemStrategy()] : [new FileSystemStrategy()];
    }
```

(`DriveRoot.Validate` throws `ArgumentException` for a non-drive-root path — caught here specifically to mean
"not a drive letter", per the design spec's "root is not a drive letter → FileSystemScanner" rule; this reuses
the exact existing validation rather than re-implementing "is this a drive letter".)

- [ ] **Step 6: Rename `dirsizer-bulk.exe`**

In `src\DirSizer.Bulk\DirSizer.Bulk.csproj`, change `<AssemblyName>dirsizer-bulk</AssemblyName>` to
`<AssemblyName>dirsizer-mft</AssemblyName>`. Nothing else in that project changes.

- [ ] **Step 7: Build**

```powershell
Normalize-Src
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 10
```

Expected: `0 Error(s)` across the whole solution. Fix any field-name mismatches in `ScanResultAdapter`/
`MftStrategy` against the real `ScanResult`/`BulkNative`/`Options` now (this is very likely on the first
attempt, given how much of this task's code was written from earlier reads rather than a fresh read at
Step 1 — Step 1 explicitly says to re-read first; if that was followed, fewer surprises are expected here).

- [ ] **Step 8: Dedicated-executable non-regression check (the most important check in this task)**

```powershell
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
git diff master -- src/Shared/BulkReader src/DirSizer.Bulk/BulkIntegration.cs src/DirSizer.Bulk/BulkCliProgram.cs src/DirSizer.Bulk/BulkSelfTests.cs
```

Expected: `dirsizer-mft.dll`'s self-test output is identical in substance to what `dirsizer-bulk.dll` printed
before this task (same test names, same pass count — only the filename changed); `dirsizer-fsctl.dll`
unaffected; the `git diff` for those specific files is **empty** — Step 1 of this task added `Options.From` to
`Cli.cs` (which does show a diff, expected) but touched nothing in `BulkReader\`, `BulkIntegration.cs`, or the
CLI/self-test files themselves. If any of those show a diff, something in this task edited a file it should
only have read — find it and revert that specific change.

- [ ] **Step 9: Commit**

```powershell
Normalize-Src
git add -A
git commit -m @'
Add MftScanner (mft strategy): a separate probe, BulkIntegration.Run reused unchanged, dirsizer-bulk.exe
renamed to dirsizer-mft.exe

dirsizer-mft.exe and dirsizer-fsctl.exe self-tests confirmed unaffected: only Cli.cs gained an additive
Options.From factory, nothing in the shared scan code changed.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 7: Step 2 tests

**Files:**
- Modify: `src\DirSizer\SelfTests.cs`

- [ ] **Step 1: Automated fake-based selector test**

Add a test using a small fake `IScanStrategy` (in the same file, or a nested test-only class) that throws
`StrategyUnavailableException` from its `Scan` method: register it ahead of `FileSystemStrategy` via a
test-only overload of `ScanStrategySelector.Scan` that accepts an explicit strategy list (add one, e.g.
`internal static UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options, IScanStrategy[] strategies)`
that the public `Scan` calls with `CandidateStrategies(rootPath)` — this makes the list injectable for tests
without changing the production entry point's signature). Assert: the result comes from the second (real)
strategy, not the first (fake); a fake that throws anything else (`InvalidOperationException`) propagates
instead of being caught.

- [ ] **Step 2: Build and run — no elevation needed for this test**

```powershell
Normalize-Src
dotnet build src\DirSizer\DirSizer.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, one more passing test than Task 5 left.

- [ ] **Step 3: Manual, elevated real-hardware verification (report exact commands and output — do not claim
  this step done without them, per the design spec's Testing section for Step 2)**

Run from a genuinely elevated PowerShell window:

```powershell
$exe = (Resolve-Path .\artifacts\publish\DirSizer\release_win-x64\dirsizer.exe).Path
$mft = (Resolve-Path .\artifacts\publish\DirSizer.Bulk\release_win-x64\dirsizer-mft.exe).Path
& $exe C:\ --json --top=1 --benchmark | Tee-Object -Variable unifiedOut
& $mft C:\ --json --top=1 | Tee-Object -Variable mftOut
($unifiedOut | ConvertFrom-Json).statistics.performance.strategy
($unifiedOut | ConvertFrom-Json).root.size
($mftOut | ConvertFrom-Json).root.size
```

(Publish `DirSizer`/`DirSizer.Bulk` first with `dotnet publish ... -c Release -r win-x64` if not already done
in this session, matching Task 5 Step 3's pattern.) Expected: `strategy` is `"mft"`; the two `root.size` values
are equal; the `--benchmark` line's `total_ms` includes the probe (record what fraction of it the probe was,
from the stderr timing, to confirm it is negligible against a multi-second `C:\` scan, per the design spec).

Then, from a normal, non-elevated console:

```powershell
& $exe C:\ --json --top=1 | ConvertFrom-Json | Select-Object -ExpandProperty statistics | Select-Object -ExpandProperty performance
```

Expected: `strategy` is `"filesystem"`, no error, no crash.

- [ ] **Step 4: Commit**

```powershell
Normalize-Src
git add -A
git commit -m @'
Step 2 tests: automated fake-based selector fallback, manual elevated verification of real MFT selection

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

## Step 3

### Task 8: Verify Step 2 is a solid foundation before starting Step 3

**Files:** none changed.

- [ ] **Step 1: Full-solution build and every tool's self-test, once more, before adding FSCTL**

```powershell
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 3
foreach ($t in @(
    @{ Name='dirsizer-fs';      Path='artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer-fs.dll' },
    @{ Name='dirsizer (unified)'; Path='artifacts\bin\DirSizer\release_win-x64\dirsizer.dll' },
    @{ Name='dirsizer-mft';     Path='artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll' },
    @{ Name='dirsizer-fsctl';   Path='artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll' },
    @{ Name='dirsizer-inspect'; Path='artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll' },
    @{ Name='DirSizer.Compare'; Path='artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.dll' }
)) { "$($t.Name): $((dotnet $t.Path --self-test | Select-Object -Last 1))" }
```

Expected: `0 Error(s)`, every line ends in a passing summary, nothing failed. This is the checkpoint the design
spec's phasing calls for between steps — do not start Task 9 if anything here is red.

---

### Task 9: `NtfsFsctlScanner` strategy

**Files:**
- Move (`git mv`): `src\DirSizer.Fsctl\FsctlScanner.cs` → `src\Shared\FsctlScanner.cs`
- Modify: `src\DirSizer.Fsctl\DirSizer.Fsctl.csproj` (add `<Compile Include="..\Shared\FsctlScanner.cs" />`,
  remove the old in-project file reference if the csproj lists files explicitly — it does not today per the
  earlier read, SDK globbing picks up whatever is physically in the folder, so removing the physical file is
  enough)
- Modify: `src\DirSizer\DirSizer.csproj` (add the same `<Compile Include>`)
- Create: `src\DirSizer\FsctlStrategy.cs`
- Modify: `src\DirSizer\ScanStrategySelector.cs`

- [ ] **Step 1: Move the file and switch its local `GetVolumeInformation` use to `VolumeInfo.IsNtfs`**

```powershell
git mv src\DirSizer.Fsctl\FsctlScanner.cs src\Shared\FsctlScanner.cs
```

Read the moved file's `Native` class (its `GetVolumeInformation` P/Invoke and whatever calls it for an error
message, per the design spec's "Selection policy" section tracing this exact duplication). Replace that call
site with `VolumeInfo.IsNtfs(...)` from `DirSizer.Core` (already referenced by `DirSizer.Fsctl` today) and
delete the now-unused private `GetVolumeInformation` P/Invoke declaration from this file — this is the cleanup
the design spec's Step 2 section said Step 3 would do. Keep everything else in the file byte-for-byte.

- [ ] **Step 2: Wire `DirSizer.Fsctl.csproj` to the new location**

Add to its `<ItemGroup>`: `<Compile Include="..\Shared\FsctlScanner.cs" />` (the SDK's default implicit glob for
`src\DirSizer.Fsctl\*.cs` no longer finds it there since it moved, so this explicit include is required — unlike
`DirSizer.Fs`/`DirSizer`, which have no explicit `<Compile Include>` list at all and rely purely on implicit
globbing of their own folder, `DirSizer.Fsctl` already has an explicit `<ItemGroup>` with several `<Compile
Include>` entries from `Shared\`, per the earlier read — add this one alongside them, same pattern).

- [ ] **Step 3: Build and test `DirSizer.Fsctl` alone, before touching `DirSizer`**

```powershell
Normalize-Src
dotnet build src\DirSizer.Fsctl\DirSizer.Fsctl.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, self-test count and result unchanged from Task 8's checkpoint — the file moved and one
internal call site changed, behavior did not.

- [ ] **Step 4: `FsctlStrategy`**

`src\DirSizer\FsctlStrategy.cs`:

```csharp
using System.ComponentModel;

// See MftStrategy.cs and the design spec's "availability/failure boundary" -- same shape, different native
// entry point. FsctlScanner.cs's own OpenVolume is reused as the probe; Scanner.Run() itself is never modified.
sealed class FsctlStrategy : IScanStrategy
{
    public string Name => "fsctl";

    public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var drive = DriveRoot.Validate(rootPath);
        try
        {
            using var probe = Native.OpenVolume($"\\\\.\\{drive}");
        }
        catch (Win32Exception cause)
        {
            throw new StrategyUnavailableException("fsctl", cause);
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = new Scanner(Options.From(drive, options.Top, options.Files, options.Dirs)).Run();
        return ScanResultAdapter.Adapt(result, "fsctl", options.Top, started.Elapsed.TotalMilliseconds);
    }
}
```

Add `<Compile Include="..\Shared\FsctlScanner.cs" />` to `src\DirSizer\DirSizer.csproj` too (`Scanner`/`Native`
live in this file; `DirSizer` needs it in its own compilation the same way `DirSizer.Fsctl` does, per the
"source-level sharing, not a project reference" convention this whole plan follows).

- [ ] **Step 5: Wire into the selector, after `MftStrategy`**

`src\DirSizer\ScanStrategySelector.cs`, `CandidateStrategies`:

```csharp
        return VolumeInfo.IsNtfs(drive) ? [new MftStrategy(), new FsctlStrategy(), new FileSystemStrategy()] : [new FileSystemStrategy()];
```

- [ ] **Step 6: Build**

```powershell
Normalize-Src
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 10
```

Expected: `0 Error(s)`. Resolve any remaining field mismatches against the real `Scanner`/`Options`/`Native`
declarations now, same discipline as Task 6.

- [ ] **Step 7: Dedicated-executable non-regression check**

```powershell
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll --self-test | Select-Object -Last 1
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll --self-test | Select-Object -Last 1
git diff master -- src/DirSizer.Fsctl/FsctlProgram.cs src/DirSizer.Fsctl/DirSizer.Fsctl.csproj
```

Expected: both self-tests pass, same counts as Task 8's checkpoint; the `git diff` on `FsctlProgram.cs` is
empty (the CLI entry point never changed) and `DirSizer.Fsctl.csproj`'s diff is only the one added
`<Compile Include>` line.

- [ ] **Step 8: Commit**

```powershell
Normalize-Src
git add -A
git commit -m @'
Add NtfsFsctlScanner (fsctl strategy): FsctlScanner.cs moves to Shared, its own local GetVolumeInformation
replaced by DirSizer.Core's VolumeInfo.IsNtfs, dirsizer-fsctl.exe behavior confirmed unaffected

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

### Task 10: Step 3 tests

**Files:**
- Modify: `src\DirSizer\SelfTests.cs`

- [ ] **Step 1: Automated two-level fallback test**

Using the injectable-strategy-list test seam from Task 7: a fake MFT-shaped strategy throws
`StrategyUnavailableException`, a fake FSCTL-shaped strategy succeeds — assert the result comes from the
second strategy, proving the chain tries strategies in order past more than one failure, not just one.

- [ ] **Step 2: Build and run**

```powershell
Normalize-Src
dotnet build src\DirSizer\DirSizer.csproj -c Release 2>&1 | Select-Object -Last 4
dotnet artifacts\bin\DirSizer\release_win-x64\dirsizer.dll --self-test | Select-Object -Last 1
```

Expected: `0 Error(s)`, one more passing test than Task 7 left.

- [ ] **Step 3: Manual, elevated verification (per the design spec: confirms the wrapping is correct, not that
  the policy prefers FSCTL over MFT on real hardware, which it never does)**

Run from a genuinely elevated PowerShell window, using the injectable-strategy-list overload directly (this is
why Task 7's test seam is `internal`, not test-only-by-convention — a small script or an ad hoc self-test-mode
addition can call `ScanStrategySelector.Scan(root, options, [new FsctlStrategy(), new FileSystemStrategy()])`
directly, skipping `MftStrategy`, to reach the FSCTL branch on a machine where MFT would otherwise always win):

```powershell
$exe = (Resolve-Path .\artifacts\publish\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.exe).Path
& $exe C:\ --json --top=1 | ConvertFrom-Json | Select-Object -ExpandProperty root
```

Compare that `root.size` by hand against the unified `dirsizer.exe`'s own `mft`-strategy result from Task 7
Step 3 (same volume, should already be known to match) — this confirms FSCTL and MFT agree with each other on
this volume (they always have, per every previous phase of this project), which combined with Task 9's
automated adapter test is sufficient evidence the `FsctlStrategy` wrapper itself is correct, without needing a
real "MFT unavailable, FSCTL available" environment (the design spec already explains why none is expected to
exist).

- [ ] **Step 4: Commit**

```powershell
Normalize-Src
git add -A
git commit -m @'
Step 3 tests: automated two-level fallback chain, manual verification that fsctl and mft agree

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

---

## Task 11: `release.ps1`, READMEs, final verification

**Files:**
- Modify: `scripts\release.ps1`, `README.md`, `README-jp.md`

- [ ] **Step 1: `release.ps1`**

Read the current `$Tools` table (Task 6/earlier changed `AssemblyName`s but not this script). Replace:

```powershell
$Tools = @(
    @{ Project = 'src\DirSizer.Fs\DirSizer.Fs.csproj';           Exe = 'dirsizer.exe' },
    @{ Project = 'src\DirSizer.Fsctl\DirSizer.Fsctl.csproj';     Exe = 'dirsizer-fsctl.exe' },
    @{ Project = 'src\DirSizer.Bulk\DirSizer.Bulk.csproj';       Exe = 'dirsizer-bulk.exe' },
    @{ Project = 'src\DirSizer.Inspect\DirSizer.Inspect.csproj'; Exe = 'dirsizer-inspect.exe' }
)
```

with:

```powershell
$Tools = @(
    @{ Project = 'src\DirSizer\DirSizer.csproj';                 Exe = 'dirsizer.exe' },
    @{ Project = 'src\DirSizer.Fs\DirSizer.Fs.csproj';           Exe = 'dirsizer-fs.exe' },
    @{ Project = 'src\DirSizer.Fsctl\DirSizer.Fsctl.csproj';     Exe = 'dirsizer-fsctl.exe' },
    @{ Project = 'src\DirSizer.Bulk\DirSizer.Bulk.csproj';       Exe = 'dirsizer-mft.exe' },
    @{ Project = 'src\DirSizer.Inspect\DirSizer.Inspect.csproj'; Exe = 'dirsizer-inspect.exe' }
)
```

Update the docstring at the top of the file (currently lists "dirsizer.exe, dirsizer-fsctl.exe,
dirsizer-bulk.exe, dirsizer-inspect.exe") to the new five-tool list.

- [ ] **Step 2: Verify the release script end to end, same discipline as the 0.6.0 version bump earlier**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 2>&1 | Select-Object -Last 50
Expand-Archive -Path dist\DirSizer-v*-win-x64.zip -DestinationPath "$env:TEMP\release-check-unified" -Force
Get-ChildItem "$env:TEMP\release-check-unified" | Select-Object Name
foreach ($exe in 'dirsizer.exe','dirsizer-fs.exe','dirsizer-fsctl.exe','dirsizer-mft.exe','dirsizer-inspect.exe') {
    & "$env:TEMP\release-check-unified\$exe" --self-test | Select-Object -Last 1
}
[IO.Directory]::Delete("$env:TEMP\release-check-unified", $true)
```

Expected: all five exes present in the zip; each `--self-test` passes when run straight from the extracted
archive, matching the standard set earlier in this project's history for the 0.6.0 verification.

- [ ] **Step 3: READMEs**

`README.md`/`README-jp.md`: add a short new top section (before the existing per-tool sections, or wherever the
document's own structure best fits — read the current file first) introducing `dirsizer.exe` as the normal
entry point (automatic strategy selection, no flag needed) and pointing to `dirsizer-fs.exe`/`dirsizer-mft.exe`/
`dirsizer-fsctl.exe` for explicit control, per the design spec's "User-facing contract". Update every existing
mention of "`dirsizer.exe`" that actually refers to the old filesystem-only tool to say "`dirsizer-fs.exe`"
instead (the existing "dirsizer.exe: any filesystem, no elevation" section in particular) — read both files in
full before editing, since this touches several places, and each must end up correct, not just the first match.

- [ ] **Step 4: Final full-solution verification**

```powershell
dotnet build DirSizer.sln -c Release 2>&1 | Select-Object -Last 3
foreach ($t in @(
    'artifacts\bin\DirSizer\release_win-x64\dirsizer.dll',
    'artifacts\bin\DirSizer.Fs\release_win-x64\dirsizer-fs.dll',
    'artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll',
    'artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll',
    'artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll',
    'artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.dll'
)) { dotnet $t --self-test | Select-Object -Last 1 }
git status --short
```

Expected: `0 Error(s)`, every tool's self-test passes, working tree clean (everything committed).

- [ ] **Step 5: Commit**

```powershell
Normalize-Src
git add -A
git commit -m @'
release.ps1 and README updates for the unified dirsizer.exe and the dirsizer-fs.exe/dirsizer-mft.exe renames

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
'@
```

- [ ] **Step 6: Report**

Summarize to the user: final self-test counts for all six build outputs, confirmation that
`dirsizer-fs.exe`/`dirsizer-mft.exe`/`dirsizer-fsctl.exe`/`dirsizer-inspect.exe` are behaviorally unchanged
(the `git diff` checks from Tasks 5/6/9), the elevated manual-verification results from Tasks 7/10, and that
this is ready for `superpowers:finishing-a-development-branch` — ask before merging or pushing, do not do
either unasked.

---

## Self-review against the spec

| Spec section | Task |
| --- | --- |
| Step 1: rename, `DirSizer.Fs.Core` extraction, `IScanStrategy`/`ScanStrategySelector` with one strategy, no behavior change | 1-5 |
| No `--enumerator`/strategy flag on `dirsizer.exe`, common CLI options, `--workers` honestly strategy-specific | 4 (`UnifiedCliOptions`), Common CLI options section matched field-for-field |
| `UnifiedScanResult`/`UnifiedItem`/`UnifiedScanOptions`, two adapters not three | 3, 4 (`FileSystemStrategy.Adapt`), 6 (`ScanResultAdapter`, shared by Step 2 and 3) |
| `StrategyUnavailableException` only from a strategy's own separate probe, native code never modified | 6 (`MftStrategy`), 9 (`FsctlStrategy`) — both probe via the existing `OpenVolume`, both call the existing native entry point unmodified, both have an explicit non-regression check step |
| TOCTOU stated, not solved; probe cost tracked | 7 Step 3 (records the probe's share of total time) |
| Selection policy is a prior; FSCTL may rarely trigger | 6 Step 5, 9 Step 5 (fixed order in `CandidateStrategies`); 10 Step 3 (explains why no real FSCTL-only environment is expected) |
| Non-drive-root path always `FileSystemScanner` | 6 Step 5 (`CandidateStrategies`' `DriveRoot.Validate` catch) |
| `MftScanner`/`NtfsFsctlScanner` wrap `BulkIntegration.Run`/`Scanner.Run` unchanged, one shared adapter | 6 Step 1-4, 9 Step 4 |
| `dirsizer-bulk.exe`→`dirsizer-mft.exe` in Step 2, `FsctlScanner.cs`→`Shared` in Step 3, `dirsizer-fsctl.exe` name unchanged | 6 Step 6, 9 Step 1-2 |
| Testing: Step 1 substance-level equality, Step 2/3 automated fake + manual elevated | 5, 7, 10 |
| `release.ps1`, README | 11 |
| Dedicated executables unaffected (the central correctness claim of the whole design) | explicit non-regression `git diff`/self-test checks in 5 Step 4, 6 Step 8, 9 Step 7, 11 Step 4 |
