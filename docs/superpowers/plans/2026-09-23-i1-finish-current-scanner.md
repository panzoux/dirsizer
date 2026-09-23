# I1: finish the current scanner — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close roadmap phase I1 ([docs/roadmap.md](../../roadmap.md), "I1 - Finish the current scanner"): correctness of
`dirsizer-mft` against `dirsizer-fsctl` on NTFS volumes with a different geometry and with a strongly fragmented `$MFT`,
and cold-cache performance numbers. No scanner code changes. Only scripts and the roadmap change.

**Architecture:** Two disposable VHDX volumes are created with a new script (`New-TestVolume.ps1`). Volume U: has 64 KiB
clusters and 4 KiB MFT records. Volume V: has 4 KiB clusters, 1 KiB records and a `$MFT` fragmented on purpose by a new
script (`New-FragmentedMft.ps1`). The existing checks run on both: `New-AbFixture.ps1`, `Compare-Readers.ps1` and
`DirSizer.Compare.exe`. `Compare-Benchmark.ps1` gets a `-ColdDismount` switch that dismounts the volume before every run,
which empties the NTFS cache for that volume.

**Tech Stack:** PowerShell 7, diskpart, the Storage module (`Mount-DiskImage`, `Initialize-Disk`, `New-Partition`,
`Format-Volume -UseLargeFRS`), `fsutil`, the existing .NET 8 tools.

---

## Ground rules for the executing agent

- Run every command with the **PowerShell tool** from the repository root (`C:\Users\user\source\repos\panzoux\dirsizer`),
  in an **elevated** shell. Check with `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')`.
  It must print `True`. If it prints `False`, stop and ask the user to run the remaining steps from an elevated window.
- Create a branch first: `git checkout -b feature/i1-finish-scanner`. Never push unless asked. Commit messages end with
  the attribution line from the session's instructions.
- Scripts are ASCII-only and use CRLF line endings, like the existing ones.
- Never point a script at `C:` or at the user's real data volumes. Every writing script refuses volumes not labelled `NTFSTEST`.
- Build once before the volume tasks: `dotnet build DirSizer.sln -c Release`. Expected: `Build succeeded` with 0 errors.

## File structure

| File | Change | Task |
| --- | --- | --- |
| `scripts\Compare-Readers.ps1`, `scripts\Compare-Benchmark.ps1`, `scripts\Compare-Fs.ps1`, `scripts\Get-ReferenceSnapshot.ps1`, `scripts\Test-BulkInstability.ps1` | default paths `dirsizer-bulk.dll` → `dirsizer-mft.dll` (the 0.6.0 rename left them stale) | 1 |
| `scripts\New-TestVolume.ps1` (new) | create/remove a VHDX-backed NTFSTEST volume | 2 |
| `scripts\New-FragmentedMft.ps1` (new) | fragment the `$MFT` of an NTFSTEST volume | 3 |
| `scripts\Compare-Benchmark.ps1` | `-ColdDismount` switch | 5 |
| `docs\roadmap.md` | results and checkboxes | 4, 6, 7 |

---

### Task 1: Fix the stale `dirsizer-bulk.dll` defaults in the scripts

The 0.6.0 release renamed the bulk tool's assembly to `dirsizer-mft` (`src\DirSizer.Bulk\DirSizer.Bulk.csproj`,
`<AssemblyName>dirsizer-mft</AssemblyName>`), but five scripts still default to `...\dirsizer-bulk.dll`, a file that is no longer built.

**Files:**
- Modify: `scripts\Compare-Readers.ps1:15`, `scripts\Compare-Benchmark.ps1:16`, `scripts\Compare-Fs.ps1:27`, `scripts\Test-BulkInstability.ps1:29`, `scripts\Get-ReferenceSnapshot.ps1` (default `$Path` line)

- [x] **Step 1: Show that the default path is broken**

Run: `Test-Path artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.dll; Test-Path artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll`
Expected: `False` then `True`. If `dirsizer-bulk.dll` exists, it is a stale leftover. Delete it
(`Remove-Item artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-bulk.*`) so it cannot hide the bug. Then run
`.\scripts\Compare-Readers.ps1 -Volume T:`. Expected: it fails because it cannot find `dirsizer-bulk.dll`.

- [x] **Step 2: Replace the four literal defaults**

In each of `Compare-Readers.ps1`, `Compare-Benchmark.ps1` and `Compare-Fs.ps1`, change `\dirsizer-bulk.dll'` to
`\dirsizer-mft.dll'` in the default-path line only. Do not change the comments or the output text: the tool is still
called "bulk" in those reports. In `Test-BulkInstability.ps1` change
`testhooks_win-x64\dirsizer-bulk.dll'` to `testhooks_win-x64\dirsizer-mft.dll'`.

- [x] **Step 3: Replace the computed default in `Get-ReferenceSnapshot.ps1`**

Replace this line:

```powershell
if (-not $Path) { $Path = Join-Path $PSScriptRoot "..\artifacts\bin\DirSizer.$((Get-Culture).TextInfo.ToTitleCase($Tool))\release_win-x64\dirsizer-$Tool.dll" }
```

with:

```powershell
# The bulk reader's project is DirSizer.Bulk, but its assembly has been dirsizer-mft since 0.6.0.
if (-not $Path) { $Path = Join-Path $PSScriptRoot $(if ($Tool -eq 'bulk') { '..\artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll' } else { '..\artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll' }) }
```

- [x] **Step 4: Verify**

Run: `Select-String -Path scripts\*.ps1 -Pattern 'dirsizer-bulk\.dll'`
Expected: no output.
Run: `.\scripts\Compare-Readers.ps1 -Volume T:; $LASTEXITCODE`
Expected: every line `EQUAL`, and exit code `0`. Paste the output into the task report.

- [x] **Step 5: Commit**

```powershell
git add scripts\Compare-Readers.ps1 scripts\Compare-Benchmark.ps1 scripts\Compare-Fs.ps1 scripts\Test-BulkInstability.ps1 scripts\Get-ReferenceSnapshot.ps1
git commit -m "scripts: default to dirsizer-mft.dll, the bulk tool's assembly name since 0.6.0"
```

---

### Task 2: `New-TestVolume.ps1`: a disposable NTFS volume with a chosen geometry

**Files:**
- Create: `scripts\New-TestVolume.ps1`

- [x] **Step 1: Write the script**

```powershell
<#
.SYNOPSIS
  Creates (or removes) a disposable NTFS test volume backed by a VHDX file.

.DESCRIPTION
  For correctness and cold-cache runs on a volume whose geometry differs from T:. Creates an expandable VHDX with
  diskpart, attaches it with Mount-DiskImage, and formats one GPT partition as NTFS with the given cluster size.
  With -LargeFrs, the MFT records are 4 KiB instead of 1 KiB (Format-Volume -UseLargeFRS). The volume is labelled
  NTFSTEST, so the other test scripts accept it. -Remove detaches the VHDX and deletes the file.
  Needs an elevated shell. Windows may briefly show a "format disk" prompt between partitioning and formatting;
  ignore it (the script formats the volume itself).

.EXAMPLE
  .\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\u.vhdx -Letter U -AllocationUnitSize 65536 -LargeFrs
  .\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\u.vhdx -Remove
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Letter = 'U',
    [int]$SizeMB = 2048,
    [int]$AllocationUnitSize = 4096,
    [switch]$LargeFrs,
    [string]$Label = 'NTFSTEST',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$Path = [IO.Path]::GetFullPath($Path)
$Letter = $Letter.TrimEnd(':', '\')

if ($Remove) {
    if (Test-Path $Path) {
        Dismount-DiskImage -ImagePath $Path -ErrorAction SilentlyContinue | Out-Null
        Remove-Item $Path
    }
    "removed $Path"
    return
}
if (Test-Path $Path) { throw "$Path already exists; run with -Remove first." }
if (Get-Volume -DriveLetter $Letter -ErrorAction SilentlyContinue) { throw "Drive letter $Letter is already in use." }
$null = New-Item -ItemType Directory -Force (Split-Path $Path)

$diskpartScript = [IO.Path]::GetTempFileName()
try {
    Set-Content -Path $diskpartScript -Encoding ascii -Value "create vdisk file=`"$Path`" maximum=$SizeMB type=expandable"
    $diskpartOutput = diskpart /s $diskpartScript
    if ($LASTEXITCODE -ne 0) { throw "diskpart failed: $($diskpartOutput -join ' ')" }
} finally { Remove-Item $diskpartScript -ErrorAction SilentlyContinue }

$disk = Mount-DiskImage -ImagePath $Path -PassThru | Get-Disk
Initialize-Disk -Number $disk.Number -PartitionStyle GPT
$partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter $Letter
$null = Format-Volume -Partition $partition -FileSystem NTFS -AllocationUnitSize $AllocationUnitSize -UseLargeFRS:$LargeFrs -NewFileSystemLabel $Label -Confirm:$false -Force
Get-Volume -DriveLetter $Letter | Format-List DriveLetter, FileSystemLabel, FileSystem, Size, AllocationUnitSize
```

- [x] **Step 2: Create volume U: (64 KiB clusters, 4 KiB MFT records)**

Run: `.\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\u.vhdx -Letter U -AllocationUnitSize 65536 -LargeFrs`
Expected: `FileSystemLabel : NTFSTEST`, `FileSystem : NTFS`, `AllocationUnitSize : 65536`.

- [x] **Step 3: Confirm the geometry with an independent tool**

Run: `fsutil fsinfo ntfsinfo U:`
Expected: `Bytes Per Cluster : 65536` and `Bytes Per FileRecord Segment : 4096`. If the record size is 1024,
`-UseLargeFRS` was ignored. Record that in the report and continue; the volume still differs from T: in cluster size.

Run: `dotnet artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll U: --volume`
Expected: the same cluster size and MFT record size as `fsutil`.

- [x] **Step 4: Test `-Remove` on a throwaway volume, then keep U:**

Run: `.\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\x.vhdx -Letter X -SizeMB 256; .\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\x.vhdx -Remove; Test-Path C:\dirsizer-test\x.vhdx; Get-Volume -DriveLetter X -ErrorAction SilentlyContinue`
Expected: the volume listing, then `removed C:\dirsizer-test\x.vhdx`, then `False`, and nothing for `Get-Volume`.

- [x] **Step 5: Commit**

```powershell
git add scripts\New-TestVolume.ps1
git commit -m "scripts: New-TestVolume.ps1 creates a disposable VHDX-backed NTFSTEST volume with a chosen geometry"
```

---

### Task 3: `New-FragmentedMft.ps1`: a strongly fragmented `$MFT`

**Files:**
- Create: `scripts\New-FragmentedMft.ps1`

- [x] **Step 1: Write the script**

```powershell
<#
.SYNOPSIS
  Fragments the $MFT of a disposable NTFSTEST volume, so the bulk reader's multi-extent code runs on real data.

.DESCRIPTION
  Each round has three steps. First it fills the free space with 4 MiB filler files. Then it deletes every other
  filler, which leaves 4 MiB holes all over the volume, including the MFT zone. Finally it creates -FilesPerRound
  empty files, so the $MFT has to grow into those holes. The script stops when dirsizer-inspect --volume reports at
  least -TargetExtents MFT extents, and fails after -MaxRounds rounds.
  Writes only inside <Volume>\mft-frag. Refuses volumes that are not NTFS or not labelled NTFSTEST (-AllowAnyLabel
  overrides). Needs an elevated shell and a Release build of dirsizer-inspect.
#>
param(
    [string]$Volume = 'V:',
    [int]$TargetExtents = 16,
    [int]$MaxRounds = 8,
    [int]$FilesPerRound = 50000,
    [string]$InspectDll = (Join-Path $PSScriptRoot '..\artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll'),
    [switch]$AllowAnyLabel
)
$ErrorActionPreference = 'Stop'
$Volume = $Volume.TrimEnd('\')
$vol = Get-Volume -DriveLetter $Volume.TrimEnd(':')
if ($vol.FileSystem -ne 'NTFS') { throw "$Volume is not NTFS." }
if (-not $AllowAnyLabel -and $vol.FileSystemLabel -ne 'NTFSTEST') { throw "$Volume is labelled '$($vol.FileSystemLabel)', not NTFSTEST. Pass -AllowAnyLabel to override." }

function Get-MftExtents {
    $text = (& dotnet $InspectDll $Volume --volume) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "dirsizer-inspect failed: $text" }
    $match = [regex]::Match($text, 'MFT extents\s*:\s*(\d+)')
    if (-not $match.Success) { throw "no 'MFT extents' line in: $text" }
    [int]$match.Groups[1].Value
}

$root = Join-Path "$Volume\" 'mft-frag'
$null = New-Item -ItemType Directory -Force $root
$extents = Get-MftExtents
"round 0: MFT extents = $extents"
for ($round = 1; $round -le $MaxRounds -and $extents -lt $TargetExtents; $round++) {
    $fillDirectory = Join-Path $root "fill$round"
    $null = New-Item -ItemType Directory -Force $fillDirectory
    $fillers = [Collections.Generic.List[string]]::new()
    for ($i = 0; ; $i++) {
        $file = Join-Path $fillDirectory ('f{0:D6}.bin' -f $i)
        $null = fsutil file createnew $file 4194304 2>&1
        if ($LASTEXITCODE -ne 0) { Remove-Item $file -ErrorAction SilentlyContinue; break }
        $fillers.Add($file)
    }
    for ($i = 0; $i -lt $fillers.Count; $i += 2) { Remove-Item $fillers[$i] }
    $filesDirectory = Join-Path $root "files$round"
    $null = New-Item -ItemType Directory -Force $filesDirectory
    for ($i = 0; $i -lt $FilesPerRound; $i++) { [IO.File]::Create((Join-Path $filesDirectory ('e{0:D6}' -f $i))).Dispose() }
    $extents = Get-MftExtents
    "round ${round}: fillers=$($fillers.Count) created=$FilesPerRound MFT extents = $extents"
}
if ($extents -lt $TargetExtents) { throw "The MFT has only $extents extents after $MaxRounds rounds (target $TargetExtents)." }
"done: MFT extents = $extents"
```

- [x] **Step 2: Create volume V: and fragment it**

Run: `.\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\v.vhdx -Letter V -SizeMB 2048 -AllocationUnitSize 4096`
Run: `.\scripts\New-FragmentedMft.ps1 -Volume V:`
Expected: the last line is `done: MFT extents = N` with N ≥ 16. If the script fails because the target was not
reached, run it again with `-MaxRounds 16`. If it still fails, record the extent count it reached, lower
`-TargetExtents` to that count (it must be at least 4), and say so in the report.

- [x] **Step 3: List the extents**

Run: `dotnet artifacts\bin\DirSizer.Inspect\release_win-x64\dirsizer-inspect.dll V: --mft-extents`
Expected: the first line says `N extent(s) map the $MFT data stream`, with the N from step 2, and the table lists
extents with different LCNs.

- [x] **Step 4: Commit**

```powershell
git add scripts\New-FragmentedMft.ps1
git commit -m "scripts: New-FragmentedMft.ps1 fragments the MFT of an NTFSTEST volume"
```

---

### Task 4: Correctness on U: and V:

The roadmap's promotion criteria require "correctness on another NTFS volume (different size, cluster size, or MFT
record size)" and "correctness with a strongly fragmented MFT". This task runs the same comparisons that were
run on T: before.

**Files:**
- Modify: `docs\roadmap.md` ("Promotion criteria" and "I1")

- [x] **Step 1: Add the standard fixture to both volumes**

Run: `.\scripts\New-AbFixture.ps1 -Volume U:; .\scripts\New-AbFixture.ps1 -Volume V:`
Expected: both finish without an error. The fixture includes hard links, streams, sparse and compressed files, and
deleted files. NTFS compression does not work with clusters larger than 4 KiB, so on U: the compressed-file step may
fail. If it does, record the exact error, edit nothing, and continue: the rest of the fixture is enough.

- [x] **Step 2: Reader against reader**

Run: `.\scripts\Compare-Readers.ps1 -Volume U:; "exit=$LASTEXITCODE"; .\scripts\Compare-Readers.ps1 -Volume V:; "exit=$LASTEXITCODE"`
Expected: every line `EQUAL` and `exit=0`, twice. A `DIFFERENT` line is a real finding: stop, keep the snapshot files
(the script prints their paths in `$env:TEMP`), and report them to the user instead of continuing.

- [x] **Step 3: Record-level comparison and the extent map read with small buffers**

Run: `artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.exe U: --max-slots=10000000; "exit=$LASTEXITCODE"`
Run: `artifacts\bin\DirSizer.Compare\release_win-x64\DirSizer.Compare.exe V: --max-slots=10000000; "exit=$LASTEXITCODE"`
Expected: `exit=0` for both. On V:, the `extent_map buffer=32` and `buffer=48` lines report `calls` greater than 1 and
`more_data_responses` greater than 0, and every `extent_map` line says `equal_to_normal_read=true`. That is the multi-call
`ERROR_MORE_DATA` path running on a real fragmented map. If the `.exe` does not exist, list
`artifacts\bin\DirSizer.Compare\release_win-x64\` and use the executable found there.

- [x] **Step 4: Stability**

Run `.\scripts\Compare-Readers.ps1 -Volume V:` two more times. Expected: `EQUAL` each time. This matches the three
stable runs recorded for T:.

- [x] **Step 5: Record the results in the roadmap**

In `docs\roadmap.md`, under "Promotion criteria", tick the first two items and add the evidence after each one. Use
the numbers from the runs; this example shows the form:

```markdown
- [x] Correctness on another NTFS volume (different size, cluster size, or MFT record size) with the same reader-vs-reader comparison. U: (VHDX, 2 GiB, 64 KiB clusters, 4 KiB MFT records, `New-TestVolume.ps1`, `New-AbFixture.ps1`): `Compare-Readers.ps1` EQUAL; `DirSizer.Compare` 0 mismatches over <slots> slots.
- [x] Correctness with a strongly fragmented MFT (...). V: (VHDX, 2 GiB, 4 KiB clusters, `New-FragmentedMft.ps1`): <N> MFT extents; `Compare-Readers.ps1` EQUAL in 3 runs; `DirSizer.Compare` 0 mismatches; extent map read with 32/48/64/100-byte buffers took <calls> calls with <more_data> ERROR_MORE_DATA responses and was equal to the normal read.
```

Under "I1", tick "Correctness and stability on a large MFT and on another volume" only if both runs passed. Write
"large MFT" as "fragmented MFT (<N> extents)", because a VHDX cannot hold a larger MFT than C: has.

- [x] **Step 6: Commit**

```powershell
git add docs\roadmap.md
git commit -m "roadmap: bulk reader equal to FSCTL on a 64 KiB-cluster/4 KiB-record volume and on a fragmented MFT"
```

---

### Task 5: `Compare-Benchmark.ps1 -ColdDismount`

`fsutil volume dismount` drops the NTFS cache of a non-system volume. The next open mounts the volume again with an
empty cache. The script must refuse the system drive, because dismounting it is not possible and must not be tried.

**Files:**
- Modify: `scripts\Compare-Benchmark.ps1`

- [x] **Step 1: Add the parameter**

In the `param(...)` block, after `[int]$Runs = 5,`, add:

```powershell
    # Dismount the volume before every run, so each run starts with an empty NTFS cache for it. Not for the system drive.
    [switch]$ColdDismount,
```

- [x] **Step 2: Refuse the system drive and dismount before each run**

Right after `$ErrorActionPreference = 'Stop'`, add:

```powershell
if ($ColdDismount -and $Volume.TrimEnd('\') -ieq $env:SystemDrive) { throw "-ColdDismount cannot be used on the system drive $env:SystemDrive." }
```

As the first line inside `function Invoke-Benchmark([string]$Dll, [string[]]$Arguments) {`, add:

```powershell
    if ($ColdDismount) { $null = fsutil volume dismount $Volume; if ($LASTEXITCODE -ne 0) { throw "fsutil volume dismount $Volume failed" } }
```

Change the heading line from `"$Runs alternating runs on $Volume  (each cell: ...)"` to:

```powershell
"$Runs alternating runs on $Volume$(if ($ColdDismount) { ', volume dismounted before every run (cold NTFS cache)' })  (each cell: min / median / max, milliseconds unless noted)"
```

- [x] **Step 3: Verify the refusal**

Run: `.\scripts\Compare-Benchmark.ps1 -Volume C: -ColdDismount -Runs 1`
Expected: it stops immediately with `-ColdDismount cannot be used on the system drive C:.` before running anything.

- [x] **Step 4: Commit**

```powershell
git add scripts\Compare-Benchmark.ps1
git commit -m "Compare-Benchmark.ps1: -ColdDismount empties the NTFS cache of a test volume before every run"
```

---

### Task 6: Cold-cache and warm-cache numbers on U: and V:

**Files:**
- Modify: `docs\roadmap.md`

- [x] **Step 1: Warm, then cold, on each volume**

Run, one at a time, and keep the full output of each:

```powershell
.\scripts\Compare-Benchmark.ps1 -Volume U: -Runs 5
.\scripts\Compare-Benchmark.ps1 -Volume U: -Runs 5 -ColdDismount
.\scripts\Compare-Benchmark.ps1 -Volume V: -Runs 5
.\scripts\Compare-Benchmark.ps1 -Volume V: -Runs 5 -ColdDismount
```

Expected: each ends with `phase accounting: phase_sum_ms == total_ms in all 10 measured runs` and a
`per-pair total speedup` line.

- [x] **Step 2: Check that the cold runs really were cold**

Compare the `raw read MB/s` median of the cold run with that of the warm run on the same volume. The cold run is
colder only if its MB/s is clearly lower (below 80 % of the warm median). If the two are within 20 % of each other,
the host is probably caching the VHDX file itself. Then the result is **not** a cold-cache measurement. Record it as
"dismount run, host cache not excluded" and do not tick the cold-cache item.

- [x] **Step 3: Record in the roadmap**

Under the P3 item "Benchmark a cold file cache and another volume", add the medians (FSCTL total, bulk total,
speedup; raw MB/s warm vs cold) for U: and V:, the conclusion from step 2, and "one machine". Tick the item only if
step 2 showed a real cold cache. The C: cold numbers come from Task 7.

- [x] **Step 4: Commit**

```powershell
git add docs\roadmap.md
git commit -m "roadmap: warm and dismount-cold benchmarks on U: and V:"
```

---

### Task 7: Steps only the user can run (C: after a reboot, a second machine)

The agent cannot reboot the machine or reach another one. It must not claim these results. Hand the user the
exact commands, and record their output only if the user sends it back.

- [x] **Step 1: Give the user this procedure, word for word**

````markdown
Cold cache on C:. Only the first scan after a reboot is cold, so each tool needs its own reboot. After each
reboot, run the one command as the first thing in an elevated PowerShell:

```powershell
cd C:\Users\user\source\repos\panzoux\dirsizer
dotnet artifacts\bin\DirSizer.Fsctl\release_win-x64\dirsizer-fsctl.dll C: --benchmark > $null 2> $env:TEMP\cold-fsctl.txt
```

reboot, then:

```powershell
cd C:\Users\user\source\repos\panzoux\dirsizer
dotnet artifacts\bin\DirSizer.Bulk\release_win-x64\dirsizer-mft.dll C: --benchmark > $null 2> $env:TEMP\cold-mft.txt
```

Send me the `benchmark:` line from each file.

Second machine: copy the release zip (`.\scripts\release.ps1` output in `dist\`), and in an elevated PowerShell
there run `dirsizer-fsctl C: --benchmark` and `dirsizer-mft C: --benchmark` three times each (alternating) and send
the `benchmark:` lines.
````

- [ ] **Step 2: If the user sends numbers, record them**

Put them under the P3 cold-cache item and "Promotion criteria" (performance item), with the machine described as
the user describes it. If the user sends nothing, leave both items unticked and write "not run: needs a reboot / a
second machine" next to them.

- [ ] **Step 3: Remove the test volumes when I1 is closed (ask first)**

Removing the VHDX files deletes the test volumes, and I2-I5 can reuse them. Ask the user before running:
`.\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\u.vhdx -Remove; .\scripts\New-TestVolume.ps1 -Path C:\dirsizer-test\v.vhdx -Remove`

---

## Self-review notes

- Coverage: roadmap I1 has two items. The first ("large MFT and another volume") is Tasks 2-4. The second ("cold
  cache and a second machine") is Tasks 5-7. The two promotion items that I1 does not reach ("failing raw read,
  corrupt extent map on a real volume") stay open. Forcing them needs a damaged disk and is out of scope.
- Nothing here changes a scanner. Any `DIFFERENT` result is reported, not "fixed" in this plan.
