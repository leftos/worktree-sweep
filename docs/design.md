# worktree-sweep: design

How the tool finds and cleans up stale git worktrees, and the rulings that govern it. The code's layout is in [`ARCHITECTURE.md`](ARCHITECTURE.md); the agent `remove` path's lock measurements are in [`agent-path.md`](agent-path.md).

## Context

A drive that holds many repos and their worktrees collects worktrees nobody uses any more. On the owner's `D:\` that meant registered worktrees across several parent repos, empty leftover folders, a 29 GB folder with a broken `.git` directory, a 16 GB build-output folder, and an old Claude agent worktree under a repo's `.claude/worktrees`.

Deleting these on Windows often fails because files are locked: a shell whose current folder is inside, an IDE, or a language server. The tool:

1. Finds every non-main worktree plus orphan folders under a root.
2. Shows the signals needed to decide what to remove.
3. Deletes the picks recoverably.
4. When deletion hits a lock, offers to escalate with `sudo` to run `handle.exe`, show the locking processes, and stop them or (opt-in) force-close the handles.
5. Offers agents a non-interactive `remove <PATH> --json` for their own worktrees.

Rulings:

- **Scope:** scan, git signals, `--list`, `--json`, recycle, removal, the unlock flow, `remove <PATH> --json` for agents, and a WPF window for the interactive mode.
- **Candidates:** every non-main worktree with its signals, picked by the user, plus orphans found by scanning worktree container folders.
- **Merge check:** covers cherry-picks and squashes as well as ancestry; a branch whose changes are already on the default branch counts as merged.
- **Uncommitted work:** a dirty or unmerged pick needs a second, per-item question that names what would be lost.
- **Locks:** stopping the locking process is the default; force-closing a handle is a separate, warned choice.
- **Deleting:** Recycle Bin first. When it cannot take the folder, Review offers a permanent delete or a skip.
- **Branches:** deletion is offered only when the branch is merged, cherry-picked, content-contained or has no commits.
- **Runtime:** C# on .NET 10 LTS, target `net10.0-windows`, framework-dependent. The version is `0.1.0`, set in the exe's csproj.
- **Launch:** from a terminal only. One console-subsystem exe (`OutputType` `Exe` with `UseWPF`): `--list`, `--json`, `remove` and `unlock` print to standard output and never open a window; the default mode opens the window. There is no double-click mode.
- **Command line:** System.CommandLine; a usage error exits 2. `--json` with `--list` is a usage error.
- **Install:** `tools/install.ps1` runs `dotnet publish src/WorktreeSweep/WorktreeSweep.csproj -c Release -o %LOCALAPPDATA%\worktree-sweep` and adds that folder to the user PATH once. It fails with `winget install Microsoft.DotNet.DesktopRuntime.10` when the .NET 10 Desktop runtime is missing, refuses while a `worktree-sweep` from that folder is running, and only warns about an old `~\.cargo\bin\worktree-sweep.exe`.
- **After exit:** the window mode prints nothing; the Results screen is the only record.

## Measured constraints

- **Recycle Bin capacity:** a volume's `MaxCapacity` can be smaller than the folders to remove, and a volume can be set to delete immediately (`NukeOnDelete`). The Shell's recycle call with no UI can silently delete permanently when an item is too big, so the tool reads the capacity first and asks for a permanent delete instead of calling the Shell.
- **Junctions:** recycling a folder that contains a junction removes the folder and the link and leaves the junction's target and its files intact. The tool still treats junctions as links of its own (see Invariants), so it never relies on the Shell for that.
- **Shell failures** are `COPYENGINE_E_*` codes (`0x8027xxxx`), never Win32 codes: `SHARING_VIOLATION_SRC` (`0x80270027`, a process's current folder is the folder), `SHARING_VIOLATION_DEST` (`0x80270028`, a file held open or a current folder in a subfolder) and `ACCESS_DENIED_SRC` (`0x80270021`) all mean locked. `0x80270000` is the user's Cancel or No.
- **Lock retries:** on `C:`, where `%TEMP%` is, a scanner can hold a freshly written tree for a second or two, so a lock is retried after 250, 500 and 1000 ms before it counts.
- **The Shell moves the folder in one step or not at all;** it was never seen to recycle part of a locked tree.
- **handle.exe:** Sysinternals v5.0 on PATH. `-nobanner -v` gives CSV output, and `-c <handle> -p <pid> -y` closes one handle. With a name argument the CSV has 5 columns (`Process,PID,Type,Handle,Name`).

  With none, it dumps every handle on the system in 7 columns (`Process,PID,User,Handle,Type,Share Flags,Name`), both `File` and `Section` rows. Names carry a trailing space and are unquoted. An elevated unfiltered dump takes about 1.4 s (about 19,500 rows); unelevated it takes about 141 s and sees only the caller's own processes.
- **sudo:** Windows `sudo.exe` must be in **Inline** mode, so an elevated child shares the console and can prompt the user directly.

## Design

### CLI

```
worktree-sweep [ROOT]            # default: current dir; opens the window
worktree-sweep [ROOT] --list     # table only, exit
worktree-sweep [ROOT] --json     # machine-readable report, exit (agent-native)
worktree-sweep remove <PATH> --json [--force] [--stop-build-servers]
worktree-sweep unlock <PATH>...  # hidden: runs elevated via sudo
```

`WORKTREE_SWEEP_LOG=debug` adds debug traces to standard error; warnings and errors always go there as `warning:` and `error:` lines.

### Git

Git is driven by shelling out to `git.exe` (no LibGit2Sharp), because `worktree list --porcelain`, `cherry` and `merge-tree` are what the tool needs.

- Each call removes the 15 repo-local `GIT_*` variables from the child's environment, sets `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`, passes `-c core.fsmonitor=false`, gives the child a null stdin and reads both output streams concurrently.
- A call times out after 60 s; git's process tree is then killed and a timeout exception thrown.
- `merge-tree --write-tree` writes into a scratch object folder with the repo's own objects as an alternate, so the scan writes nothing to the repo.

### Discovery

- **Repos:** depth-1 folders of ROOT that have a `.git` *folder*.
- **Registered worktrees:** `git worktree list --porcelain` for each repo. The main worktree is skipped. `locked <reason>` (git's own lock) and `prunable` are kept.
- **Container folders:** depth-1 folders matching `*.wt`, `*-wt`, `*worktrees`, plus `<repo>/.claude/worktrees`.
- **Orphans:** walk each container. A child that is a registered worktree is skipped. A child that contains registered worktrees is recursed into; this covers a `yaat.wt/eram-am/{yaat,yaat-server}` layout. Any other child is an orphan candidate, with a note when it holds a `.git` file whose `gitdir:` no longer exists, or one that a repo registers elsewhere.
- **Links:** a junction or symbolic link is never followed: a junctioned `.claude` is not a container, a gitdir that is a dangling link reads as stale, and a link orphan is its own kind.
- **Path spellings:** a worktree is matched across a subst drive, an 8.3 name, a `\\?\` prefix and a loopback admin share of this machine (`\\localhost\X$\...`), so a worktree git registered under one spelling is not reported again as an orphan under another. A path that fails to resolve never adds an orphan.
- **Discovery errors:** a repo whose `git worktree list` fails, and a `.git\worktrees\{id}` whose `gitdir` file is missing, unreadable or empty (git's list silently leaves that worktree out), are reported as discovery errors. A worktree they hide would otherwise look like an orphan.

### Signals

Per registered worktree, read in parallel.

- **Default branch:** `refs/remotes/origin/HEAD`, else local `main` or `master`. Each merge check runs against both the local and the origin default, and the better result wins; a git failure against one default is traced while another still answers. An unexpected `merge-tree` exit is a signal error, never a silent "not contained".
- **Merge state:**
  - `Ancestor`: `merge-base --is-ancestor`.
  - `NoCommits`: an `Ancestor` branch whose reflog (`git reflog show --format=%gs refs/heads/<b>`) is complete (its oldest entry is `branch: Created from …`) and holds only entries that make no commit: `reset: moving to`, `branch: Renamed`, `rebase (finish)`, and fast-forward merges or pulls. Any other entry, or a reflog that is empty or partly expired, leaves the state as `Ancestor`. A branch just created by a live session is therefore not shown as merged.
  - `PatchesApplied`: `git cherry <default> <branch>` has no `+` lines, meaning every commit has a patch-equivalent on the default branch (cherry-picks).
  - `ContentContained`: `git merge-tree --write-tree <default> <branch>` gives a tree equal to `<default>^{tree}`, meaning squash-merged or otherwise already in. A branch with no history in common with a default branch is "not contained", and no `merge-tree` runs for it.
  - `Unmerged { commits }`, and `Detached` for no branch.
- **One scratch object folder per worktree** serves all its defaults.
- **Dirty:** `git status --porcelain` counts of modified and untracked files; ignored files are left out.
- **Unpushed:** `rev-list @{u}..HEAD` count; "upstream gone" when the tracking ref was deleted.
- **Last activity:** the later of the HEAD commit time and the mtime of the worktree's `$GIT_DIR/worktrees/<id>/index`.
- **Size:** a directory walk that never enters a link. Orphans get size, last-write time and a file count.
- **Stalls:** the first git timeout on a volume marks it stalled for the rest of the scan, and every later git call on that volume is skipped with a notice, so one hung share costs one timeout rather than one per repo. A timeout skips the rest of that worktree's signals.

### Report

- **Table (`--list`):** columns `#, PATH, KIND, BRANCH, MERGE, DIRTY, UPSTREAM, ACTIVE, SIZE, FLAGS`, rows ordered released worktrees first, then other registered worktrees by repo and path, then orphans. Discovery errors follow as `discovery error:` lines.
- **Scan JSON (`--json`):** snake_case keys; one flat `candidates` array tagged by `"kind": "registered"|"orphan"`; every timestamp an ISO 8601 UTC string whose key has no `_unix` suffix (`last_activity`, `size.last_write`, `released.released_at`); a top-level `discovery_errors` array (`[{repo, path, message}]`, between `repos` and `candidates`, `[]` when empty).
- **Agent report (`remove <PATH> --json`):** flat, in the scan JSON's style (snake_case, indented, `null` for absent, `\n` line ends), with the fields `status` (`removed`, `released`, `refused`), `reason`, `path` (resolved when the target resolves, else the absolute path given), `repo`, `branch`, `branch_deleted`, `loss`, `cd_to`, `holders`, `may_hold`, `stopped`, `released` and `notes`.
  - A holder's `started` is an ISO 8601 UTC string. `released` is an object `{released_at, reason, holders}` when a marker was written, else `null`.
  - A holder has `image`, `command_line` and `holds` (`{kind: current_folder|open_handle, path}`); a `may_hold` entry is `{pid, exe, why}`, `why` being `unnamed_handle` or `cannot_open`.
  - Exit codes: 0 removed, 5 released, 6 refused, 2 usage. Any other failure, an unexpected exception included, prints `error: <message>` on standard error, nothing on standard output, and exits 1.
  - The marker file on disk keeps its own shape (`released_at` in Unix seconds), so a marker written by any run is readable by any other.
- **Output encoding:** UTF-16 through `WriteConsoleW` to a console and UTF-8 without a BOM to a redirected stream, with `\n` line ends; children sorted with ordinal comparison and path keys case-folded.

### Window

A `DataGrid` with a checkbox column above a detail pane holding the selected row's detail text (path, kind, state, activity and size, marks, what removing it loses, unreadable signals). The size and position are not remembered. Risk tints colour the MERGE, DIRTY and UPSTREAM cells.

- **Picker:** nothing is ticked by default, except released worktrees, which are listed first and pre-ticked. There is no filter or sort.
- **Review:** every question is asked up front, per pick in list order: what it would lose (or that only a link goes), then a permanent delete when the pick cannot be recycled, then its branch. A branch answer applies only if the removal succeeds. A pick that is dirty, `Unmerged`, `NoCommits`, unpushed or git-locked gets the loss question that spells out what would be lost, and a git-locked pick names the lock reason.

  A final "Remove N items?" confirmation has Cancel as the default. An orphan is planned as a skip, and asked nothing, when a discovery error names its repo's worktree list, its own `gitdir` file or its repo's `.git\worktrees` folder, since git may still use that worktree.
- **Removing:** a Cancel button stops after the current item, marks the rest `skipped (cancelled)` and skips the unlock step.
- **Threads:** `[STAThread]` on `Program.Main`, and the default mode exits 0 once the window closes. The scan runs on a background thread, removal on its own STA thread, and the unlock offer on the removal thread, never the UI thread. Closing while scanning exits; while removing, it acts as Cancel and the window stays open until Results; while the unlock step runs, it is refused. The scan shows a progress indicator, with Empty and Scan failed states.
- **Unlock step:** runs in the terminal the tool was launched from, with plain numbered line prompts (`[Y/n]`, `1) Stop process  2) Close its handles  3) Skip  4) Done [1]:`) read by a pure parser. While it runs the window shows a notice to finish the unlock step in the terminal and takes no input; then it shows the retry.
- **Shell prompts:** a recycled pick's Shell prompts are owned by the window; the agent command passes no owner.

### Removal

Removal works on already-answered decisions, and removes one pick as follows.

1. Check the size against the volume's Recycle Bin capacity (below). If it does not fit, or `NukeOnDelete=1` is set, the pick needs a permanent delete, which Review asked for; a no skips it. An over-cap pick is never handed to the Shell.
2. Recycle through `IFileOperation`, with the flags set so the Shell never silently turns it into a permanent delete (`FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING`, no `FOF_NOCONFIRMATION`). `FOF_NOERRORUI | FOFX_EARLYFAILURE` keep the Shell's "Folder In Use" dialog away and make `PerformOperations` return the failure itself. The only prompt left is the permanent-delete warning.
3. A permanent delete is the tool's own walk: it never enters a link, clears the read-only attribute (which git object files carry) before each delete, counts a child that vanishes mid-walk as gone, and reports a sharing violation or access denied (Win32 32 or 5) as locked, naming the first locked path.
4. On a lock, the pick joins the unlock flow and is retried once.
5. For a registered worktree: `git worktree unlock` if it was git-locked, then `git worktree prune`. A git-locked worktree whose removal ends locked, failed or cancelled gets its lock and reason back.
6. If the branch is `Ancestor` or `NoCommits`, `git branch -d` is offered. For `PatchesApplied` and `ContentContained` git may refuse `-d`, so `-D` is offered with the reason shown. An unmerged or detached worktree offers nothing.

A link loses only the link: a junction or symbolic link is removed with a directory removal on the link, after re-checking it is still a reparse point. A folder that became a link since the scan is left in place.

**Recycle Bin capacity:** `GetVolumeNameForVolumeMountPointW` gives the volume GUID, and `HKCU\...\BitBucket\Volume\{GUID}` holds `MaxCapacity` in MB and `NukeOnDelete`. A folder exactly the Bin's size still fits. When the settings cannot be read, the size is unknown and a permanent delete is asked for.

### Unlock flow

- **One elevation per run.** Removal is two passes. Every pick that hits a lock is collected, and one unlock offer is made for all of them. On an unlocked or partly unlocked outcome, each locked pick is retried once.
- **Parent side:** lists the locked folders, asks, then runs `sudo <self> unlock --caller-pid <shell> --sweep-pid <self> <paths>` and waits. If sudo is missing, disabled or not in Inline mode, or the program runs under `dotnet.exe`, it prints the elevated command to run by hand and skips.
- **Elevated side:** makes one unfiltered `handle.exe -nobanner -accepteula -v` dump per round. It keeps the rows whose Name is a locked path or lies under one, compared case-insensitively on backslash paths.
  - It never lists its own process chain up to `sudo.exe`, the unelevated worktree-sweep, or that process's children.
  - Results are grouped by process: name, PID, and the handles with their type.
  - The process that launched the tool (for example the pwsh you are typing in) is flagged, because stopping it would close your own shell. Skip is its default.
  - Prompt per process: **stop process** (`TerminateProcess`), **close its handles** (warned that the app may crash; runs `handle -c <h> -p <pid> -y` for each handle), **skip**, **done**.
  - Before acting, it checks that the PID still belongs to the same process (same start time and image), since Windows reuses a PID once its process exits. Before closing a handle it re-dumps that process and closes only handles that still name a locked file.
  - It re-dumps and re-offers until nothing is left, the user picks Done, or a round acts on nothing.
  - A path with characters outside the ANSI, OEM and console code pages never shows up in a `handle.exe` dump; the session warns about each before the first dump, and one that then finds no locker exits 3, never 0, so the sweep retries every locked pick.
  - Exit code: 0 when all clear, 3 when locks remain after acting, 4 when nothing was acted on, 1 on error. The parent maps these to unlocked, partly unlocked and still locked; any other exit, a declined UAC prompt included, skips.
- **handle.exe CSV** is parsed by a pure parser, tested against both recorded layouts (`tests/fixtures/handle-sample.csv`, `tests/fixtures/handle-dump-sample.csv`). Column positions come from the header line, and the Name is everything after the last fixed column, so a comma in a path survives.

### Agent removal

[`agent-path.md`](agent-path.md) holds the rulings and measurements. Three rules beyond them:

- With `--force`, a git lock lifted for a worktree that then ends released is put back with its reason, as the interactive removal does. A holder is stopped only while its PID still names the process found, and once stopped it is reported under `stopped` alone, never under `holders` or in the marker.
- A recycle failure that is not a lock puts back a lifted git lock before it ends the run with exit 1, and its message names the processes already stopped. The 30 s recycle timeout runs the recycle on a dedicated thread, abandoned on timeout.
- The branch rule differs from the interactive one: `-d` for `Ancestor` and `NoCommits` only, a squash-merged branch kept with a note.

## Invariants

- **The scan writes nothing:** git runs with optional locks and fsmonitor off, and every object `merge-tree` writes goes to a scratch folder.
- **Read-only against a real drive in tests:** tests use fixtures under the repo's `.tmp\` or `%TEMP%`; the removing mode never runs against a real folder from an agent session.
- **Junctions are links, never trees:** never sized, walked, recycled or deleted through; removed with a directory removal on the link after re-checking it is still a reparse point. Directory walks use `EnumerationOptions { AttributesToSkip = 0 }`, since the default skips hidden and system entries.
- **Recycle Bin capacity is read before recycling;** an item over it asks for a permanent delete.
- **Recycling goes through `IFileOperation`** on an STA thread with the flags above; locked HRESULTs count as a lock only from `PerformOperations`, and the retries are 250, 500 and 1000 ms.
- **Handle naming:** a lookup that fails at once (a volume handle, a file pending delete) counts against its process only when the file system lists that process as using the folder; a timed-out lookup is cancelled with `CancelSynchronousIo` and its worker kept if it then returns; and the 200 ms budget starts when the worker begins the lookup, not when the handle is posted.
- **One elevation per run:** the locked picks go to a single unlock offer, one `sudo`, one `handle.exe` dump filtered by every locked path, then one retry pass.
- **The released marker keeps its file shape** (`worktree-sweep-released.json` in the worktree's `.git/worktrees/{id}` folder: `released_at` in Unix seconds, `reason`, `holders`).
- **Output:** see "Output encoding" under Report.
- **The table's columns and order are fixed;** the scan JSON and the agent report are the two machine-readable contracts.

## Tests

Integration tests build throwaway repos with real `git` in folders under the repo's `.tmp\`. Where the tests live is in [`ARCHITECTURE.md`](ARCHITECTURE.md), "Test locations".

- **Merge states:** a merged branch gives `Ancestor`; a cherry-picked branch `PatchesApplied`; a squash-merged branch `ContentContained`; a divergent branch `Unmerged { commits: n }`; a detached HEAD `Detached`.
- **Dirty and unpushed:** untracked and modified files are counted; a deleted upstream is reported as gone.
- **Discovery:** a registered worktree nested two levels deep inside a container; an empty orphan; an orphan whose `.git` file points to a missing gitdir; a prunable registration; the main worktree is never a candidate; link and gitdir edge cases.
- **Parsers and helpers:** the `handle.exe` CSV against the recorded fixtures, plus empty output and malformed lines; the Recycle Bin fit decision for size under the cap, over the cap, and with `NukeOnDelete` set (a pure function; the registry read is kept at the edge).
- **Removal:** permanent delete clears read-only files and never enters a junction; a locked file (the test holds it open with no sharing) produces a locked error rather than a generic failure.
- **Not automated:** the sudo and handle path, which needs UAC, and the real Shell recycle; both are checked by hand.

## Verification

1. The gates in the README's Development section, all clean.
2. `worktree-sweep <root> --list` against a real drive: the registered worktrees with sensible merge states, the orphans, and no main repos. `--json` output parses.
3. **Manual lock test:** make a scratch worktree of a repo under a scratch container, open a `cmd /k "cd /d <path>"` there, pick it in the window, and confirm that the delete fails as locked, sudo prompts once, the cmd is listed by name and PID, "stop process" clears it, and the retry sends the folder to the Recycle Bin and prunes the registration.
4. **Over-capacity path:** run it on a scratch folder only. Real deletion on a real drive happens with the owner at the keyboard.
