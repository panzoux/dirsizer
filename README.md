# dirsizer

`dirsizer` is a small, read-only Windows CLI that calculates NTFS folder sizes from MFT metadata instead of recursively enumerating files and directories.

## License

DirSizer is released under the MIT License. See [LICENSE](LICENSE).

## Status

This is the first practical baseline. It targets NTFS volumes, requires an elevated terminal, and reads metadata with:

- `FSCTL_GET_NTFS_VOLUME_DATA` for MFT length and record size.
- `FSCTL_GET_NTFS_FILE_RECORDS` for active MFT records.

The scanner parses only the record header, `$FILE_NAME`, and unnamed `$DATA`. It does not use `FindFirstFile`, `Directory.EnumerateFiles`, the USN journal, or path traversal.

## Build

Install the .NET 8 SDK and publish a small NativeAOT executable:

```powershell
dotnet publish -c Release -r win-x64
```

The executable is under `bin\Release\net8.0-windows\win-x64\publish\`. NativeAOT removes the runtime dependency and enables trimming.

For a fast local compile:

```powershell
dotnet build -c Release
```

## Releases

The version is defined in `DirSizer.csproj`. To build and package the current
version without publishing it:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1
```

This creates `dist\DirSizer-v<version>-win-x64.zip` containing the NativeAOT
executable and this README. To create the GitHub release, install and sign in
with GitHub CLI (`gh auth login`), create a Markdown release-notes file, and
run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\release.ps1 `
  -Publish -NotesFile release-notes.md
```

Publishing requires a clean `master` branch and an unused version tag. The
script pushes `master` and `v<version>`, then attaches the ZIP to the GitHub
release.

## Usage

Run from an Administrator PowerShell:

```powershell
.\dirsizer.exe C:\
.\dirsizer.exe D:\ --top=50 --files
.\dirsizer.exe C:\ --top=50 --files --allocated
.\dirsizer.exe C:\ --top=100 --json > result.json
```

The default is `--top=25`. The table output identifies the selected limit and
separates directories from files. Use `--top=N` to change it. With `--files`,
the output includes both sections; otherwise it shows only the largest
directories. Known NTFS metadata records are labeled under
`[NTFS metadata]` (for example `$MFT` and `$Bitmap`). An `[unresolved record
...]` path means the MFT record had data but its usable parent/name relationship
could not be reconstructed and it was not a known NTFS metadata record.

Progress is rendered on one updating line to stderr as `current/estimated (percent%)`. This keeps stdout suitable for table output or JSON redirection.

## Semantics and limitations

- **Logical size** = the file size: the amount of file content.
- **Allocated size** = the size on disk: the space reserved for that content.

- Reported size is logical bytes in the unnamed NTFS `$DATA` attribute.
- Use `--allocated` to report NTFS allocated bytes instead. For nonresident
  data, this is the allocated length, typically rounded to clusters. Resident
  data has no separate cluster allocation, so its stored value length is used.
- Directories contribute no bytes; alternate data streams are excluded.
- Deleted records and reparse targets are excluded.
- Hard-linked records are counted once, using the first discovered parent/name relationship.
- The initial implementation uses one metadata query per MFT record. Future performance work should measure and improve query throughput.
- Malformed records are skipped and counters are reported in JSON.
- Only local NTFS drive roots such as `C:\` are accepted.

The initial version does not implement `--deleted`, `--ads`, or non-NTFS fallback scanning.

## JSON

JSON contains the selected `top` limit, the selected `size_mode`, numeric byte
values, paths, and scan counters:

```json
{
  "volume": "C:",
  "top": 25,
  "size_mode": "logical",
  "directories": [{ "path": "C:\\Users", "size": 123 }],
  "files": [],
  "statistics": {
    "records_scanned": 100,
    "records_accepted": 90,
    "records_skipped": 10,
    "files": 70,
    "directories": 20
  }
}
```

`size_mode` is `logical` by default and `allocated` when `--allocated` is used.
The counters are diagnostic and do not change the requested top limit:

- `records_scanned`: MFT record slots queried.
- `records_accepted`: active, parseable records retained for the result.
- `records_skipped`: slots that could not be read or parsed.
- `files` and `directories`: accepted records by type.

They are not required to calculate the table, but they show whether the scan
was complete. A nonzero `records_skipped` value means the reported sizes may be
incomplete. JSON currently has no automated test project; it is validated by
the release build and manual `--json` runs.

## Development direction

The next correctness work should add parser fixtures for malformed records, hard links, long and Unicode names, reparse points, sparse/compressed files, ADS exclusion, and root handling. Performance work should measure MFT reads, parsing, allocations, relationship resolution, aggregation, and total elapsed time before introducing pooling or parallelism.