## DirSizer v0.6.0

`dirsizer.exe` is back, and now means something new: point it at a path and it automatically picks the best available, validated scan method for that filesystem, so you never choose a backend yourself. Everything that name did before (the always-available directory walk on any filesystem, no elevation) is unchanged in behavior and is now `dirsizer-fs.exe`.

### What changed

- **New `dirsizer.exe`: automatic strategy selection.** On an elevated NTFS drive root it reads the master file table (MFT) directly, falling back to `FSCTL_GET_NTFS_FILE_RECORD` and then to a directory walk if MFT is unavailable; on any other path it walks the directory tree with `FindFirstFileExW`, which needs no elevation and works on any filesystem. Its own CLI is smaller than any dedicated tool's (`--top`, `--files`, `--dirs`, `--json`, `--workers`, `--strict`, `--benchmark`, `--self-test`) and has no option that names an implementation -- the strategy that ran is reported (`strategy` in JSON, first in the text Summary line) but is never chosen by the user. `asInvoker`: it never prompts for UAC on its own, but a `dirsizer.exe` launched from an already-elevated console inherits that token and can use MFT/FSCTL. The MFT-then-FSCTL-then-walk order is a documented prior, not a benchmarked result -- see [docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md](docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md).
- **Renamed executables, same implementations.** The previous, generic-filesystem `dirsizer.exe` is now `dirsizer-fs.exe` (identical behavior, CLI and output -- a pure rename). `dirsizer-bulk.exe` is now `dirsizer-mft.exe` (identical implementation). `dirsizer-fsctl.exe` and `dirsizer-inspect.exe` are unchanged.
- **`dirsizer-fs.exe`: `--enumerator=auto`.** A new, explicitly selectable enumerator mode: `handle:full:64` as the primary directory-listing method, with an automatic, one-way fallback to `find` (`FindFirstFileExW`) the first time that information class turns out to be unsupported on the filesystem being scanned. The default enumerator is unchanged (`find`); `auto` is opt-in. A fallback is reported through `enumerator_fallback`/`enumerator_fallback_reason` in JSON and a one-line stderr warning at the end of the scan.

### Compatibility notes

- If you invoke `dirsizer.exe` today expecting the old any-filesystem walk, use `dirsizer-fs.exe` instead -- same behavior, renamed.
- If you invoke `dirsizer-bulk.exe`, use `dirsizer-mft.exe` instead.
- `dirsizer.exe`'s own JSON/text output shape is new, and deliberately smaller than `dirsizer-fs.exe`'s (no per-strategy fields like `enum_ms_total` or `UNSTABLE`). Scripts that parsed the old `dirsizer.exe`'s JSON should target `dirsizer-fs.exe`, whose shape is unchanged, or adapt to the new, smaller `dirsizer.exe` shape.

This release contains all five self-contained NativeAOT `win-x64` executables (`dirsizer.exe`, `dirsizer-fs.exe`, `dirsizer-fsctl.exe`, `dirsizer-mft.exe`, `dirsizer-inspect.exe`), the README (English and Japanese), and the license.
