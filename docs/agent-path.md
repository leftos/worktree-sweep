# Agent path for locked worktrees

Design record for the agent path, a finished item in [plans/MAIN.md](./plans/MAIN.md). Goal: `worktree-sweep remove <PATH> --json`, run unelevated and non-interactively by an agent, removes one worktree, or reports which processes hold it and marks it "released" for the next interactive sweep. Rulings so far are on the MAIN.md line.

## Measurements

Measured by a probe run on build 26200, at Medium integrity (not admin), with 450–590 processes and about 180k handles. Each lock kind was a real process whose folder was a fixture. The probe's source is kept untracked at `.tmp/lockprobe/` (`lockprobe fixtures|teardown|scan|handles|hang-test|inspect|fpid|fs-test|delete-test`).

### What blocks what

A recycle on the same volume is a rename of the folder root. Win32 codes are in brackets.

| Lock kind | Rename (recycle) | Permanent delete |
|---|---|---|
| pwsh started with its cwd at the root | fails (32) | fails (32) |
| process cwd in a subfolder | fails (5) | fails (32) |
| Git Bash `cd` to the root | OK | OK |
| Git Bash `cd` to a subfolder | fails (5) | OK |
| pwsh `Set-Location` into the root | OK | OK |
| file open, share none | fails (5) | fails (32) |
| file open, share read/write/delete (build tools such as rustc and MSBuild) | fails (5) | OK |
| file open, share read | fails (5) | fails (32) |
| exe running from inside | OK | fails (5) |
| DLL loaded from inside | OK | fails (5) |
| folder handle on a subfolder (a watcher) | fails (5) | OK |
| folder handle on the root, delete sharing | OK | OK |
| mapped view, file handles closed | OK | OK |

What surprised us: a running exe or loaded DLL doesn't block a recycle; a delete-sharing handle below the root still does; pwsh `Set-Location` holds nothing, because it doesn't change the process cwd.

### A recycle that outlives its timeout

On one volume, `IFileOperation` moves a folder into the Recycle Bin as one atomic rename about 30% of the way through the call; for a tree of 100,000 small files the rename lands after 3-5 s and the call returns at about 13 s. A folder is therefore whole where it was or whole in the Bin, never split. The operation runs in the caller's process, so nothing finishes it after that process exits.

So when the agent removal's 30 s recycle timeout fires, the folder has usually already gone. `AgentRemover` checks again before and after writing the released marker, and reports a folder that is gone as `removed`, with the note `the move to the Recycle Bin finished after the recycle timed out`, pruning the record and handling the branch as for any removal.

A rename that lands in the last instant before the process exits can still leave a stale marker and an unpruned record; the next `remove` or sweep prunes both.

A volume whose Recycle Bin is set to delete permanently (`NukeOnDelete`) never reaches the recycle: `RecycleDecider` asks for a permanent delete, which the agent path reports as `released` with `too_big_for_recycle_bin`. This is deliberate: an agent never deletes permanently, so its worktrees on such a volume (the repo drive X: on this machine) wait, pre-ticked, for the owner's interactive sweep, where the permanent delete is approved.

### What finds holders unelevated

| Method | Finds | Time |
|---|---|---|
| PEB cwd (`NtQueryInformationProcess` + `ReadProcessMemory`) | every cwd kind | 6 ms over ~450 processes; ~20 can't be opened (this user's elevated processes) |
| Per-process handles (`ProcessHandleInformation`, duplicate File handles, keep `GetFileType == DISK`, name with `GetFinalPathNameByHandleW` on 4 workers, 200 ms timeout each) | open files, folder handles | 280–613 ms, 1 s during a parallel build; independent of tree size. Without the `GetFileType` filter it takes 68 s |
| Image path (`QueryFullProcessImageNameW`) | exe running from inside | 8–19 ms |
| Restart Manager, every file in the tree | files, exe, DLL; never a cwd or folder handle | 335–520 ms for a real build tree, 1.9–2.7 s for 21k files; folders fail |
| `FileProcessIdsUsingFileInformation` | everything, including processes we can't open | 20–85 ms per path: root only is cheap, the whole tree is not (33 s for 1k files) |
| Module list / mapped views | exe, DLL | 1.6–2 s |
| System handle table (`SystemExtendedHandleInformation`) | same as per-process handles | 4.5 s; kernel addresses are zeroed unelevated |

**Hang risk:** `GetFinalPathNameByHandleW` and `NtQueryObject` block on a handle with synchronous I/O pending (a pipe read, a synchronous `ReadDirectoryChangesW`), and `CancelSynchronousIo` rarely frees them. `GetFileType` returns immediately on both. A stuck worker thread is abandoned; the process still exits normally.

**Real processes, read-only:** idle MSBuild nodes and VBCSCompiler held nothing on D: (their cwd is in the SDK folder). `rust-analyzer` and its proc-macro server had their cwd at the repo plus ~18 folder handles under it. A Claude plugin's Roslyn LSP had its cwd at its project.

**Win32 access:** every call above comes from the CsWin32 source generator (`src/WorktreeSweep.Core/NativeMethods.txt`). The PEB offsets (`CurrentDirectory`, `CommandLine`, and the 32-bit `PEB32` fields of a WOW64 process) are declared by hand as constants in `Holders/Peb.cs`, because the Win32 metadata hides them, and they are right for a 64-bit process only.

### Recommended route

Same-user process census (Toolhelp + token user, ~20 ms) → PEB cwd → per-process disk handles, excluding worktree-sweep itself. That covers every kind that blocks a recycle, in about 0.35–0.65 s. An optional image-path check flags an exe running from inside, which matters only for a permanent delete. `FileProcessIdsUsingFile` on the root alone names holders we can't open.

`HolderFinder.Find(folder, exclude)` returns a `HolderReport` of `Holder`s (pid, exe, image, start time, command line, and its `Hold`s: `CurrentFolder` or `OpenHandle` with a path) and `MayHold` entries for the processes it could not fully inspect.

`StopAllowlist.Stoppable` picks the holders that may be stopped, and `LockedPaths` does the path matching. The tests start a pwsh child with its cwd in a temp subfolder, and a pwsh child holding a file there with share none, and expect `Find` to name both; the folder is resolved first, so 8.3 temp paths match.

## Open decisions

Answered:

1. **Allowlist:** `rust-analyzer*` and its proc-macro server, `cargo`, MSBuild nodes and `VBCSCompiler`, each stopped only when it actually holds something inside the worktree. Editors' other LSP and MCP servers are never stopped.
2. **Detection:** cwd plus per-process disk handles, the recommended route above.
3. **Unseen holders:** a handle whose name can't be read, or a process we can't open, is reported as `may_hold` with PID and exe (unopenable ones named through `FileProcessIdsUsingFile` on the root), never stopped, and the worktree is marked released.
4. **Caller's own shell:** before touching anything, if a process in `remove`'s own parent chain holds the worktree, it exits with `caller_holds` and the folder to `cd` to (the repo's main worktree).
5. **Lost work:** a dirty, unmerged or unpushed worktree is refused with `would_lose` and the picker's loss sentence, unless `--force`; the branch is deleted only when merged or with no commits, `--force` or not.
6. **Too big for the Recycle Bin:** `remove` deletes nothing, reports `too_big_for_recycle_bin`, and marks the worktree released, so the interactive sweep asks about the permanent delete.

Answered in the decision round:

7. **Exit codes:** 0 removed, 1 error, 2 usage, 5 released (locked, too big, or an unseen holder), 6 refused (`would_lose`, `caller_holds`, not a removable worktree). The JSON `status` carries the detail.
8. **`would_lose`:** dirty, unmerged, unpushed, a detached HEAD not contained in the default branch, a git-locked worktree, or a signal that could not be read. `NoCommits` is not a loss. `--force` overrides it and runs `git worktree unlock` first. The text is the picker's loss sentence.
9. **Branch:** deleted with `git branch -d` only when the merge state is `Ancestor` or `NoCommits`. A squash-merged branch (`PatchesApplied`, `ContentContained`) is kept, and the JSON notes it, even with `--force`.
10. **Marker:** `<repo>/.git/worktrees/<id>/worktree-sweep-released.json` holds `{released_at, reason, holders}`. `git worktree prune` removes it with the admin dir. The scan only reads it.

Settled from the code and the rulings:

- **Command:** `worktree-sweep remove <PATH> --json [--force] [--stop-build-servers]`. `--json` is required, and the output is always one pretty-printed JSON object, in the style of the scan JSON (`ReportJson`; the agent report is `RemoveReportJson`), with a snake_case `status` tag: `removed`, `released`, `refused`.
- **What is refused:** the path must be the root of a registered linked worktree. A main worktree, a bare repo, a subfolder, an orphan folder, a link and a non-worktree are refused with a `reason`. A prunable record, whose folder is already gone, is pruned and reported `removed`.
- **Allowlist matching:** by image name (`rust-analyzer*.exe`, `rust-analyzer-proc-macro-srv.exe`, `cargo.exe`, `MSBuild.exe`, `VBCSCompiler.exe`), and for `dotnet.exe` by command line (`MSBuild.dll` with `/nodemode`, or `VBCSCompiler.dll`), read from the PEB next to the cwd.
- **`caller_holds`:** a full ancestor walk from `remove`'s own PID. A parent counts only if it was created before its child, so a reused PID ends the chain. worktree-sweep sets its own cwd to the main worktree before anything else, and excludes only its own PID from holders.
- **Path forms:** the target is resolved by `PathResolver` (which expands 8.3 names, subst drives and junctions in its parents). A PEB cwd goes through `GetLongPathNameW`. The shared path comparison also strips `\??\`.
- **Order:** resolve → refusals → `would_lose` → `caller_holds` → capacity (too big → release, with no holder scan) → recycle on a thread with a timeout (timeout → release) → on `Locked`: find holders, stop allowlisted ones if the flag is set (re-checking start time and image first), retry once, else release → on success: prune and delete the branch, and report no `holders` or `may_hold`.
- **Where the scan meets the marker:** the scan reads the marker (`ReleasedMarker`), `ScanReport.Ordered` lists released worktrees first, and the window's `CandidateRowViewModel` ticks them in advance.

Settled from the measurements:

- **PID reuse:** holders are found and stopped in the same run, and each is re-checked against its start time and image before it is stopped.
- **Timeout and workers:** 200 ms per name lookup, 4 workers, as measured.
