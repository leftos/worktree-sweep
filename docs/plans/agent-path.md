# Agent path for locked worktrees

Backlog item in [MAIN.md](./MAIN.md). Goal: `worktree-sweep remove <PATH> --json`, run unelevated and non-interactively by an agent, removes one worktree, or reports which processes hold it and marks it "released" for the next interactive sweep. Rulings so far are on the MAIN.md line.

## Measurements (2026-09-27)

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
| file open, share read/write/delete (Rust std, cargo) | fails (5) | OK |
| file open, share read | fails (5) | fails (32) |
| exe running from inside | OK | fails (5) |
| DLL loaded from inside | OK | fails (5) |
| folder handle on a subfolder (a watcher) | fails (5) | OK |
| folder handle on the root, delete sharing | OK | OK |
| mapped view, file handles closed | OK | OK |

What surprised us: a running exe or loaded DLL doesn't block a recycle; a delete-sharing handle below the root still does; pwsh `Set-Location` holds nothing, because it doesn't change the process cwd.

### What finds holders unelevated

| Method | Finds | Time |
|---|---|---|
| PEB cwd (`NtQueryInformationProcess` + `ReadProcessMemory`) | every cwd kind | 6 ms over ~450 processes; ~20 can't be opened (this user's elevated processes) |
| Per-process handles (`ProcessHandleInformation`, duplicate File handles, keep `GetFileType == DISK`, name with `GetFinalPathNameByHandleW` on 4 workers, 200 ms timeout each) | open files, folder handles | 280–613 ms, 1 s during a parallel cargo build; independent of tree size. Without the `GetFileType` filter it takes 68 s |
| Image path (`QueryFullProcessImageNameW`) | exe running from inside | 8–19 ms |
| Restart Manager, every file in the tree | files, exe, DLL; never a cwd or folder handle | 335–520 ms for a real Rust tree, 1.9–2.7 s for 21k files; folders fail |
| `FileProcessIdsUsingFileInformation` | everything, including processes we can't open | 20–85 ms per path: root only is cheap, the whole tree is not (33 s for 1k files) |
| Module list / mapped views | exe, DLL | 1.6–2 s |
| System handle table (`SystemExtendedHandleInformation`) | same as per-process handles | 4.5 s; kernel addresses are zeroed unelevated |

**Hang risk:** `GetFinalPathNameByHandleW` and `NtQueryObject` block on a handle with synchronous I/O pending (a pipe read, a synchronous `ReadDirectoryChangesW`), and `CancelSynchronousIo` rarely frees them. `GetFileType` returns immediately on both. A stuck worker thread is abandoned; the process still exits normally.

**Real processes, read-only:** idle MSBuild nodes and VBCSCompiler held nothing on D: (their cwd is in the SDK folder). `rust-analyzer` and its proc-macro server had their cwd at the repo plus ~18 folder handles under it. A Claude plugin's Roslyn LSP had its cwd at its project.

**Crate:** `windows` 0.62.2, already a dependency, has every call; needed features include `Wdk_System_Threading`, `Win32_System_Diagnostics_Debug`, `Win32_Security` (the minimal set isn't verified).

### Recommended route

Same-user process census (Toolhelp + token user, ~20 ms) → PEB cwd → per-process disk handles, excluding worktree-sweep itself. That covers every kind that blocks a recycle, in about 0.35–0.65 s. An optional image-path check flags an exe running from inside, which matters only for a permanent delete. `FileProcessIdsUsingFile` on the root alone names holders we can't open.

Sketch of `src/holders.rs`: `find_holders(folder, exclude) -> HolderReport { holders: Vec<Holder { pid, exe, image, holds: Vec<Hold> }>, uninspectable }`, where `Hold` is `CurrentFolder`, `OpenHandle { path }` or `Unnamed`; a pure `collect` (reusing `unlock::matches_locked_path`) and a pure `stoppable(holders, allowlist)`. The red tests: a pwsh child with its cwd in a temp subfolder, and a pwsh child holding a file there with share none, are both found by `find_holders`; the folder is canonicalized first, so 8.3 temp paths match.

## Open decisions

Answered (user, 2026-09-27):

1. **Allowlist:** `rust-analyzer*` and its proc-macro server, `cargo`, MSBuild nodes and `VBCSCompiler`, each stopped only when it actually holds something inside the worktree. Editors' other LSP and MCP servers are never stopped.
2. **Detection:** cwd plus per-process disk handles, the recommended route above.
3. **Unseen holders:** a handle whose name can't be read, or a process we can't open, is reported as `may_hold` with PID and exe (unopenable ones named through `FileProcessIdsUsingFile` on the root), never stopped, and the worktree is marked released.
4. **Caller's own shell:** before touching anything, if a process in `remove`'s own parent chain holds the worktree, it exits with `caller_holds` and the folder to `cd` to (the repo's main worktree).
5. **Lost work:** a dirty, unmerged or unpushed worktree is refused with `would_lose` and the picker's loss sentence, unless `--force`; the branch is deleted only when merged or with no commits, `--force` or not.
6. **Too big for the Recycle Bin:** `remove` deletes nothing, reports `too_big_for_recycle_bin`, and marks the worktree released, so the interactive sweep asks about the permanent delete.

Settled from the measurements:

- **PID reuse:** holders are found and stopped in the same run, and each is re-checked against its start time and image before it is stopped.
- **Timeout and workers:** 200 ms per name lookup, 4 workers, as measured.
