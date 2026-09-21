## DirSizer v0.5.2

There are no changes to the tools in this release: the scan, the options and the output are the same as in v0.5.1. See the v0.5.0 notes for the three tools and the changes from v0.3.0, and the v0.5.1 notes for the console window that now waits for Enter.

### What changed

- **Japanese README.** `README-jp.md` is a Japanese version of the README, linked from `README.md`, and it is included in the release ZIP.
- **Source layout.** The repository is reorganized: each tool has its own folder under `src/`, shared files are in `src/Shared/`, design notes are in `docs/`, and `DirSizer.sln` builds everything. Each project now builds into its own folder under `artifacts/` instead of all tools sharing one output folder. Anyone building from source should use the new paths shown in the README.
- **Release script.** `scripts\release.ps1` finds `vswhere.exe` when the Visual Studio Installer folder is not on PATH, which was making the NativeAOT link step fail from some shells.

This release contains the three self-contained NativeAOT `win-x64` executables, the README (English and Japanese), and the license.
