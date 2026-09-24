## DirSizer v0.7.0

New experimental tool: **`dirsizer-index.exe`**, for repeated analysis. It saves an index of the volume's `$MFT` once, keeps it current from the NTFS USN change journal, and answers folder-size questions for any directory on the drive, including what shrank and grew since the previous run. The other five tools are unchanged in behavior.

### What is new

- **A persistent per-volume index.** The first run reads the whole `$MFT` (the same reader as `dirsizer-mft`) and saves the merged records to `%LOCALAPPDATA%\dirsizer\index\<volume serial>.dsix` (versioned format with a SHA-256 checksum; readable only by the user, SYSTEM and Administrators). `--index-dir=` puts it elsewhere.
- **Incremental updates from the USN journal.** Later runs read the journal from the saved position and re-read only the MFT records it names (plus NTFS metadata records, which change without journal entries). The new state always comes from the MFT, never from the journal entries alone. While few records changed, only a small `.delta` file is written next to the index. If the index cannot be brought up to date (no index yet, another volume, the journal was recreated, wrapped or disabled, or the file is damaged), a full scan runs instead and the reason is printed to stderr.
- **Any directory, one index.** `dirsizer-index C:\Users\me\Downloads` answers from the same whole-volume index; Windows itself resolves the path to its MFT record.
- **`--changes`: before/after report.** Lists the directories that shrank or grew since the index was last written, largest change first, in text and JSON. Deleted folders are listed by their old path. With `--no-save` the baseline stays fixed across several cleanup steps.
- **`--verify`** compares the index record by record with a fresh full scan (exit code 2 on any difference); `--rebuild` forces a full scan.

### Measurements (one machine, `C:`, about 924,000 MFT records, warm cache)

- Incremental run with a few changes: about 1.9 s, 25 % of a full run (about 7.8 s).
- After deleting 30,000 files: 2.8 s incremental (38 % of a full run of 7.4 s); 3.8 s with `--changes` (51 %). The deleted folder was reported with exactly -30,720,000 bytes.
- The first run costs a full scan plus writing the index (about 112 MiB for `C:`).

### Limitations

- **Experimental.** Its CLI and index format may change. `dirsizer.exe` does not use the index yet.
- NTFS local drives only; requires Administrator (UAC prompt, like the other NTFS tools).
- Changes made while the volume was used by a system that does not write the USN journal (another operating system) are not seen: run with `--rebuild` after that.
- The index file lists every file and directory name on the volume; keep that in mind before moving it with `--index-dir`.

Design and measurements: [docs/design_index.md](docs/design_index.md).

This release contains six self-contained NativeAOT `win-x64` executables (`dirsizer.exe`, `dirsizer-fs.exe`, `dirsizer-fsctl.exe`, `dirsizer-mft.exe`, `dirsizer-inspect.exe`, and the experimental `dirsizer-index.exe`), the README (English and Japanese), and the license.
