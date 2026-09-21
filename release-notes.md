## DirSizer v0.5.1

A fix for starting the tools from a non-elevated console. There are no changes to the scan itself, the options, or the output. See the v0.5.0 notes for the three tools and the changes from v0.3.0.

### What changed

- **The window no longer closes before you can read the result.** Every tool asks for elevation (UAC) through its manifest. Started from a non-elevated prompt, Windows opens a new elevated console window and closes it as soon as the process exits, so the output was lost. Each tool now waits for **Enter** before exiting when it is the only process attached to its console. In an Administrator terminal, in Windows Terminal, or with output redirected, nothing changes and nothing waits.
- The v0.5.0 note that this Windows behaviour had not been tested is out of date: it was observed, and is what this release addresses.

`> result.json` and pipes still need an Administrator terminal, because a program that requires elevation cannot be started in place from a non-elevated console.

This release contains the three self-contained NativeAOT `win-x64` executables, the README, and the license.
