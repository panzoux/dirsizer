## DirSizer v0.5.0

DirSizer is now **three tools**, and there are changes from v0.3.0 that affect existing use. Please read "Changes from v0.3.0" first.

### The tools

- **`dirsizer-fsctl.exe`** – the folder-size scan, reading MFT records one at a time with `FSCTL_GET_NTFS_FILE_RECORD`. This is the same method as v0.3.0 and the reference implementation.
- **`dirsizer-bulk.exe`** (**experimental**) – the same scan and the same output, reading the raw `$MFT` in large blocks. About 1.4x faster in our measurements (median 1.37x to 1.52x over five sessions on one machine, live C: volume, warm cache). It never falls back to `dirsizer-fsctl`, checks that the MFT layout did not change while it was read (one rescan; exit code 3 if it still changed), and shows progress while it scans. It has only been tested on one machine.
- **`dirsizer-inspect.exe`** – a read-only tool for looking inside the NTFS `$MFT`: one record's attributes (including the extension records a large `$ATTRIBUTE_LIST` points to), the `$MFT` extents, slot counts, and a raw-vs-FSCTL comparison of a record. For investigating and developing, not for measuring usage. `dirsizer-inspect --help` explains everything.

### Changes from v0.3.0

- **The executable is no longer `DirSizer.exe`.** Use `dirsizer-fsctl.exe` (same scan as before) or `dirsizer-bulk.exe`.
- **`--allocated` is not in this release.** Allocated (on-disk) size needs a defined behaviour for resident, sparse, compressed, and hard-linked files first; until then only logical size is reported (`size_mode` is always `logical`).
- **Every tool asks for elevation (UAC) when it starts**, through its manifest. In an already elevated terminal nothing changes. Windows cannot start such a program in place from a non-elevated console, so `> result.json` and pipes need an Administrator terminal. (This Windows behaviour has not been tested with these executables.)
- New options: `--benchmark` (phase timings and memory), `--diagnostics` (unresolved records), `--self-test`.
- JSON gained `root` and `root_children`, `statistics.performance`, and a `reader` field (`fsctl` or `bulk`); `dirsizer-bulk` adds a `bulk` object with its stability result and phase timings.
- Extension MFT records are now merged into their base record, so files whose attributes overflow the base record (for example files with many hard links) are named and sized correctly.
- A bad command-line option now prints an error and exits with 1 instead of crashing.
- Text-mode `--files` printed 0 for every size; it now prints the logical size.

### Things to know about `dirsizer-bulk`

- Experimental. The result was identical to `dirsizer-fsctl` on the NTFS test volume we used (in the JIT and NativeAOT builds); it has not been tried on other volumes, on a strongly fragmented MFT, or with a cold file cache.
- Exit codes: 0 ok, 1 error, 3 the result was produced but the MFT layout changed during the scan, so it is not a consistent snapshot. Changes inside existing records during a scan are not detected (that is true of both scan tools).
- If `dirsizer-bulk` fails, it says so and stops. Run `dirsizer-fsctl` to get the result the other way.

This release contains the three self-contained NativeAOT `win-x64` executables, the README, and the license.