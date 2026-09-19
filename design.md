# C# NTFS Folder Size Scanner Spec

## Goal

Build a fast, read-only Windows tool for analyzing folder sizes on NTFS volumes.

The primary goal is **fast folder-size analysis without recursively traversing the filesystem through Win32 file APIs**.

The scanner should read NTFS metadata/MFT information, construct the required directory relationships in memory, and calculate folder sizes from the collected metadata.

This is a practical disk-usage analyzer, not a general-purpose NTFS forensic/parser library.

---

## Scope

### Supported

* Windows only
* NTFS volumes only
* Read-only
* Administrative privileges required
* Input: volume root such as `C:\`
* Folder size analysis
* Optional largest-files / largest-folders reporting
* JSON output

### Explicitly out of scope for the initial implementation

* Writing/modifying NTFS metadata
* File deletion
* NTFS repair
* Forensic recovery
* USN Journal history analysis
* Deleted-file recovery
* Alternate Data Stream reporting
* Physical disk allocation analysis
* Following reparse points
* Network drives
* Non-NTFS filesystems

`--deleted` should not be implemented in the initial version. It introduces substantially different semantics and is not required for normal folder-size analysis.

---

# Measurement Semantics

The default reported size is:

> Logical size of the unnamed `$DATA` attribute of files contained by the directory.

This is **not physical disk allocation size**.

With `--allocated`, report the allocated length of the unnamed `$DATA`
attribute. For nonresident attributes this is the NTFS allocated length; for
resident attributes use the stored value length because no separate cluster
allocation is recorded.

By default:

* unnamed `$DATA` is included
* alternate data streams are excluded
* directories themselves contribute no size
* directory size is the sum of files below that directory
* each physical file record is counted only once
* reparse-point targets are not followed

The tool should document these semantics clearly in `--help`.

---

# Core Approach

Do not recursively enumerate directories using APIs such as:

```text
FindFirstFile
FindNextFile
Directory.EnumerateFiles
Directory.EnumerateDirectories
```

Instead:

```text
Open volume
    ↓
Obtain NTFS metadata / MFT records
    ↓
Parse relevant NTFS attributes
    ↓
Build compact in-memory model
    ↓
Resolve directory relationships
    ↓
Aggregate file sizes bottom-up
    ↓
Sort/report results
```

The implementation should minimize filesystem API calls and avoid one Win32 operation per file.

---

# NTFS Access

Open the target volume using a volume handle such as:

```text
\\.\C:
```

Use appropriate NTFS filesystem control codes through `DeviceIoControl` to enumerate/read MFT information.

The implementation must explicitly document which Windows control codes are used and why.

Do not assume that USN Journal enumeration is equivalent to MFT scanning.

The scanner should be designed around a **single metadata scan** of the target volume.

---

# Data Model

Do not model an NTFS file record and a directory entry as the same object.

A file can have multiple `$FILE_NAME` attributes because NTFS supports hard links.

Use separate concepts.

## FileRecord

Represents one NTFS file record.

```text
FileRecord
    FileRef
    IsDirectory
    LogicalSize
    FileNames[]
```

Where:

```text
FileRef
    MFT record number
    sequence number
```

`FileRef` must preserve the complete NTFS file reference, not just the MFT record number.

---

## FileName

Represents one `$FILE_NAME` attribute.

```text
FileName
    ParentRef
    Name
    Namespace
```

The scanner should prefer a normal Win32-compatible filename namespace when selecting a display name.

Do not assume that one file record always has exactly one filename.

---

## DirectoryEntry

The aggregation model may use a lightweight directory-entry representation:

```text
DirectoryEntry
    ParentRef
    FileRef
    Name
```

This allows hard links to be represented without duplicating the underlying file record.

The exact internal representation may be optimized after measurement.

---

# Required Information From Each MFT Record

Parse only information required by the analyzer.

Required:

* file reference
* record validity
* in-use/deleted state
* directory flag
* `$FILE_NAME`

  * parent file reference
  * filename
  * namespace
* unnamed `$DATA` logical size

Do not parse unrelated NTFS attributes unless required by a later feature.

---

# Filename Selection

A file record may contain multiple `$FILE_NAME` attributes.

For normal display purposes:

1. Prefer a Win32-compatible namespace.
2. Ignore DOS-only aliases when a normal filename exists.
3. Preserve all relevant parent relationships needed to represent hard links.
4. Do not arbitrarily discard additional valid parent/name relationships merely because one display name has been selected.

The selected display name is a presentation concern; the underlying file-record relationships must remain available for aggregation.

---

# Hard Links

Hard links must not cause the same physical file record to be counted multiple times as file data.

Example:

```text
dirA\file.txt
dirB\file.txt
```

may refer to the same NTFS file record.

Therefore:

```text
FileRecord.FileRef
```

is the identity used for file-size deduplication.

However, directory relationships are represented separately through `$FILE_NAME` / directory entries.

The implementation must define and test how hard-linked files contribute to directory totals.

For the initial practical analyzer, the recommended policy is:

> A file's logical size is counted once globally for disk-usage reporting, and hard-link aliases are not treated as independent copies.

If per-directory accounting becomes ambiguous because of hard links, report the limitation rather than silently double-counting.

---

# Directory Tree

Normal, active records are used to construct the directory tree.

Process:

### Pass 1 — Parse

Read the MFT and create the minimal `FileRecord` representation.

### Pass 2 — Resolve relationships

Use `$FILE_NAME.ParentRef` to establish parent/child relationships.

### Pass 3 — Aggregate

Calculate directory sizes from contained files.

The implementation may combine passes or use more efficient structures if measurements demonstrate a benefit, but correctness must remain equivalent.

---

# Size Aggregation

For a directory:

```text
DirectorySize =
    sum of logical sizes of files contained below it
```

Aggregation must be bottom-up.

Do not recursively walk the filesystem.

Use either:

* explicit parent/child processing
* post-order traversal
* depth/order-based aggregation
* equivalent iterative algorithm

Avoid deep recursive C# call stacks.

---

# Reparse Points

Reparse points must not cause traversal into another filesystem namespace.

Because the scanner reads NTFS metadata directly rather than recursively opening paths, the normal implementation should not follow the reparse target.

The initial implementation should report the reparse-point status if available but should not attempt target resolution.

---

# Deleted Records

Deleted records are excluded from normal analysis.

Do not expose:

```text
--deleted
```

in the initial CLI.

Reason:

* deleted records do not necessarily form a normal directory tree
* parent/name relationships may no longer represent the original path
* deleted-file analysis is a separate feature with different semantics

If deleted-record analysis is added later, it should be specified separately.

---

# Root Directory

The scanner must identify the NTFS root directory record and use it as the root of aggregation.

For:

```text
ntfs-scan C:\
```

the output should represent the contents of that volume.

The implementation must not depend on the root having a conventional filesystem path lookup.

---

# Error Handling

The scanner should be resilient to individual malformed records.

Rules:

* Invalid MFT record → skip record
* Invalid attribute → skip affected attribute/record where safely possible
* Missing filename → record may be unusable for tree construction
* Missing data attribute → treat logical file size as zero unless NTFS semantics require another handling
* Unexpected/corrupt metadata → continue scanning where possible

The final result should include diagnostic counters such as:

```text
records scanned
records accepted
records skipped
directories found
files found
invalid records
```

These counters are useful for determining whether an apparently successful scan was complete.

A malformed individual record must not abort the entire volume scan unless continuing would make the result unsafe or meaningless.

---

# Access Errors

If the volume cannot be opened:

* report the Windows error
* clearly state that administrative privileges may be required
* exit with a non-zero status

If the volume is not NTFS:

```text
Unsupported filesystem: <filesystem>
Only NTFS volumes are supported.
```

Do not attempt recursive fallback scanning in the initial implementation.

---

# Performance Requirements

Performance is a primary requirement.

### Avoid

* per-file Win32 enumeration
* opening every file
* opening every directory
* recursive filesystem traversal
* unnecessary string/path construction
* unnecessary allocations for records that do not contribute to the result
* repeated tree traversal

### Prefer

* sequential MFT scanning
* compact structures
* `Span<T>` / pooled buffers where appropriate
* `ArrayPool<T>` where measurements justify it
* integer file references instead of path strings internally
* deferred path construction
* iterative aggregation

Do not optimize blindly.

Benchmark:

1. MFT read throughput
2. parsing throughput
3. allocation volume
4. memory usage
5. relationship-building time
6. aggregation time
7. total scan time

The first implementation should prioritize a correct baseline that can be measured.

---

# Memory

The entire directory/file relationship model may be held in memory.

This is acceptable for the initial implementation because the goal is fast interactive analysis.

However, avoid storing full filesystem paths for every file.

Prefer:

```text
FileRef
ParentRef
Name
Size
flags
```

and construct paths only when required for output.

If memory consumption becomes significant on very large volumes, optimize based on measurements rather than prematurely introducing a streaming external-sort architecture.

---

# Output

Default output should provide useful folder-size information without requiring path reconstruction for every file.

Example conceptual output:

```text
Size        Path
----------- --------------------------------
512.3 GB    C:\
231.4 GB    C:\Users
120.8 GB    C:\Windows
 85.2 GB    C:\Users\foo
...
```

Default sort:

```text
descending size
```

Optional:

```text
--top N
--files
--dirs
--json
```

---

# CLI

Basic:

```text
ntfs-scan C:\
```

Options:

```text
--top N       Show largest N results
--files       Include largest files
--dirs        Include largest directories
--json        Output machine-readable JSON
```

Possible future options:

```text
--allocated   Report physical allocation instead of logical size
--ads         Include alternate data streams
```

These should not be implemented until their semantics are explicitly defined.

---

# JSON

JSON output should contain machine-readable values rather than formatted strings.

Example:

```json
{
  "volume": "C:",
  "filesystem": "NTFS",
  "logical_size": 123456789,
  "directories": [
    {
      "path": "C:\\Users",
      "size": 987654321
    }
  ],
  "statistics": {
    "records_scanned": 1234567,
    "records_accepted": 1200000,
    "records_skipped": 34567,
    "files": 900000,
    "directories": 300000
  }
}
```

The exact schema should be finalized before implementation of the JSON output.

---

# Testing

Correctness must be tested against ordinary filesystem APIs.

Create test volumes/directories containing:

* empty directories
* normal files
* nested directories
* large files
* zero-byte files
* long filenames
* Unicode filenames
* hard links
* symbolic links / junctions
* sparse files
* compressed files
* files with ADS

Compare the scanner's results with independently obtained filesystem information.

Do not assume Explorer's displayed size is always equivalent to the scanner's logical-size definition.

Tests should explicitly verify:

* parent/child resolution
* hard-link handling
* root handling
* malformed records
* missing attributes
* reparse points
* aggregation correctness

---

# Development Strategy

Implement in stages.

## Stage 1 — NTFS/MFT reader

Goal:

```text
C:\ → MFT records → basic statistics
```

Verify:

* record count
* file/directory count
* basic filenames
* file references
* logical sizes

## Stage 2 — Directory relationships

Build:

```text
FileRef → ParentRef
```

and verify the resulting hierarchy against filesystem enumeration.

## Stage 3 — Size aggregation

Implement bottom-up directory size calculation.

Validate totals against an independent filesystem traversal.

## Stage 4 — CLI/output

Add:

```text
--top
--files
--dirs
--json
```

## Stage 5 — Performance work

Measure before optimizing.

Record:

* total scan time
* MFT parsing time
* aggregation time
* peak memory
* allocation count if practical

Only then optimize hot paths.

---

# Design Principle

The tool is a **fast disk-usage analyzer**, not a complete NTFS parser.

Therefore:

> Parse the minimum NTFS metadata required to produce correct folder-size results, keep the internal model compact, avoid filesystem traversal, and optimize based on measurements.

Correctness of the reported sizes and directory relationships is more important than exposing every NTFS feature.
