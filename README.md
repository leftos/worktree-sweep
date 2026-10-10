# worktree-sweep

A Windows tool that finds stale git worktrees and leftover worktree folders under a folder, shows what each one still holds, and removes the ones you pick, in a window. Everything goes to the Recycle Bin first. When a folder is locked by another process, it escalates once with `sudo` to find the process with Sysinternals `handle.exe` and stop it.

## Install

```
pwsh tools/install.ps1
```

The script publishes the tool, framework-dependent, with `dotnet publish src/WorktreeSweep/WorktreeSweep.csproj -c Release -o %LOCALAPPDATA%\worktree-sweep`, and adds that folder to your user `PATH` once. It stops with a clear message while a `worktree-sweep` running from that folder is open, and it warns about an old `~\.cargo\bin\worktree-sweep.exe` without removing it (`cargo uninstall worktree-sweep` does that). `-WhatIf` prints what it would do and changes nothing.

Requirements:

- Windows 11 and the .NET 10 Desktop runtime (`winget install Microsoft.DotNet.DesktopRuntime.10`). The install script fails with that command when the runtime is missing. Building from source also needs the .NET 10 SDK.
- `sudo` turned on in **Inline** mode (Settings → System → Advanced → Enable sudo; `sudo config --enable normal`). Without it, the tool prints the elevated command for you to run by hand.
- Sysinternals `handle.exe` on `PATH` (`winget install Microsoft.Sysinternals.Handle`). Only the unlock step needs it.
- `git` on `PATH`.

## Usage

```
worktree-sweep [ROOT]           # open the window: scan, pick, review, remove
worktree-sweep [ROOT] --list    # print the table and exit
worktree-sweep [ROOT] --json    # print the report as JSON and exit
worktree-sweep remove <PATH> --json [--force] [--stop-build-servers]
                                # remove one worktree without prompts (for agents)
```

`ROOT` defaults to the current folder. Run the tool from a terminal: with neither `--list` nor `--json` it opens the window and exits 0 when the window closes; it prints nothing afterwards, because the window's Results screen is the record. `--list` and `--json` print to standard output and never open a window. Giving both is a usage error (exit 2). A failed scan exits 1 with the message on standard error.

`WORKTREE_SWEEP_LOG=debug` adds debug traces to standard error; warnings and errors always go there as `warning:` and `error:` lines.

The tool looks at:

- **Repos:** folders directly under `ROOT` with a `.git` folder. Every linked worktree `git worktree list` reports is a candidate; the main worktree never is.
- **Container folders:** folders directly under `ROOT` named `*.wt`, `*-wt` or `*worktrees`, plus each repo's `.claude/worktrees`. A folder in one that no repo registers is an **orphan** candidate, such as the leftover of a failed `git worktree remove`.

For each candidate the table (`--list`) and the window's list show these columns, in this order: `#, PATH, KIND, BRANCH, MERGE, DIRTY, UPSTREAM, ACTIVE, SIZE, FLAGS`. `#` is the row number, `PATH` is relative to `ROOT` where it lies under it, and `BRANCH` is the worktree's branch. The others:

| Column | Meaning |
|---|---|
| `KIND` | `worktree` (registered), `orphan`, or `link` (a junction or symbolic link orphan) |
| `MERGE` | `merged` (the branch is in the default branch), `no commits` (a branch nothing was committed on), `cherry-picked`, `squashed`, `unmerged N`, or `detached` |
| `DIRTY` | modified (`M`) and untracked (`?`) files, not counting ignored ones |
| `UPSTREAM` | `+N` commits the upstream lacks, or `gone` when the upstream branch was deleted |
| `ACTIVE` | time since the last commit or index write, whichever is later; for an orphan, its last file write |
| `SIZE` | disk usage, without following links |
| `FLAGS` | `released` (an agent's `remove` couldn't finish it; listed first), `git-locked`, `prunable` (its folder is gone), `stale .git` (an orphan whose `.git` file points nowhere), `registered elsewhere` |

Merge checks run against both the local and the `origin` default branch, and the better result counts. Released worktrees come first, then the other registered worktrees by repo and path, then orphans.

Under the table, each problem the scan met listing a repo's worktrees is a `discovery error: <path>: <message>` line. A worktree git's list leaves out would otherwise look like an orphan, so the tool names it.

### The window

The window has a list of candidates with a checkbox column above a detail pane for the selected row. Nothing is ticked by default, except worktrees an agent released, which are listed first and ticked in advance; the usual confirmations still apply to them. The MERGE, DIRTY and UPSTREAM cells are tinted by risk.

Review asks every question before anything is removed, per pick in list order: what it would lose, a permanent delete when it cannot be recycled, and its branch. A final "Remove N items?" confirmation sums up what will be removed; its default is Cancel. While removing, Cancel stops after the current item and marks the rest `skipped (cancelled)`.

The unlock step runs in the terminal the tool was launched from, as plain numbered prompts, while the window shows a notice and takes no input.

## Safety

- **The scan writes nothing.** Git runs with optional locks and fsmonitor off, and the squash-merge check writes its objects to a scratch folder.
- **Every question comes before anything is removed.** A last confirmation then sums up what will be removed; its default is Cancel.
- **Second confirmation for anything that would lose work.** A pick that is dirty, unmerged, unpushed, has no commits yet (another session may be about to use it), or is locked with `git worktree lock` asks again and names what would be lost.
- **Recycle Bin first.** Before recycling, the tool checks the item against the volume's Recycle Bin size limit. An item that is too big, or a volume set to delete immediately, is never handed to the Shell: Review offers a permanent delete or a skip. The Shell is never left to delete permanently on its own.
- **Links are removed as links.** A junction or symbolic link orphan is deleted as a link; its target is never entered, sized or deleted.
- **Branch deletion is offered up front.** Once the worktree is removed and its registration pruned, a merged branch or one with no commits is deleted with `git branch -d`. A cherry-picked or squash-merged branch is offered `git branch -D`, with the reason shown. An unmerged branch is never offered.

### Locked folders

Windows refuses to move a folder while a process holds a file in it or has it as its current folder. The tool first retries a lock briefly, because antivirus scanners hold new files for a second or two. Then:

1. It finishes every other pick, and collects the locked ones.
2. It asks once, then runs itself elevated through `sudo` for all of them together. That is one UAC prompt per run.
3. The elevated step lists every process holding a handle inside a locked folder, by name and PID. For each one you choose: **stop process** (the default), **close its handles** (the app may crash), **skip**, or **done**. The shell you started the tool from is marked, and skip is its default.
4. It repeats until nothing holds the folders or you pick done, then retries each locked pick once.

## Removing one worktree from an agent

An agent that is done with its own worktree runs `worktree-sweep remove <PATH> --json`. The command never prompts and never elevates. `PATH` must be the root folder of a linked worktree, and `--json` is required.

1. **Refusals.** It refuses a main worktree, a bare repo, a subfolder, an orphan (including a worktree folder whose `.git` file is gone), a link, or a folder that is not a worktree. A worktree whose folder is already gone is pruned.

   Without `--force`, it also refuses a worktree that would lose work (dirty, unmerged, unpushed, a detached HEAD found nowhere else, git-locked, or a signal it could not read). A worktree with no commits is not a loss. `--force` overrides the refusal and lifts a git lock first.
2. **Your own shell.** If the shell that ran the command (or any of its parents) holds the folder, the command stops and names the folder to `cd` to first.
3. **Remove.** It recycles the folder, prunes the registration, and deletes the branch with `git branch -d` when the branch is merged or has no commits. A squash-merged or cherry-picked branch is kept, and a note in the output gives the `git branch -D` to run.
4. **Release.** If the folder is still locked, or too big for the Recycle Bin, nothing is deleted. The worktree is marked **released**, and the next interactive sweep lists it first and ticks it in advance, behind the usual confirmations and its one elevation. The marker is `worktree-sweep-released.json` in the worktree's folder under `.git/worktrees`, and pruning the worktree removes it.

The command finds holding processes without elevation: each process's current folder and its open files, for processes running as you. With `--stop-build-servers`, it stops rust-analyzer, cargo, MSBuild nodes and VBCSCompiler when they hold something inside the worktree, then tries once more. Nothing else is ever stopped.

The output is one JSON object, snake_case, indented, with `null` for an absent field:

```json
{
  "status": "released",
  "reason": "locked",
  "path": "D:\\app.wt\\feature",
  "repo": "D:\\app",
  "branch": "feature",
  "branch_deleted": false,
  "loss": null,
  "cd_to": null,
  "holders": [
    {
      "pid": 4242,
      "exe": "pwsh.exe",
      "image": "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
      "started": "2026-09-21T14:13:20Z",
      "command_line": "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"",
      "holds": [{ "kind": "current_folder", "path": "D:\\app.wt\\feature\\src" }]
    }
  ],
  "may_hold": [],
  "stopped": [],
  "released": {
    "released_at": "2026-09-21T14:15:02Z",
    "reason": "locked",
    "holders": [{ "pid": 4242, "exe": "pwsh.exe" }]
  },
  "notes": []
}
```

| `status` | Exit code | `reason` |
|---|---|---|
| `removed` | 0 | `null` |
| `released` | 5 | `locked`, `may_hold` (only processes whose handles could not be read may hold it), `too_big_for_recycle_bin`, `shell_timeout` |
| `refused` | 6 | `would_lose` (with `loss`), `caller_holds` (with `cd_to`), `main_worktree`, `bare_repo`, `subfolder`, `orphan`, `link`, `not_a_worktree`, `not_found` |

A usage error exits 2. Any other failure, an unexpected exception included, exits 1 with an `error: <message>` line on standard error and nothing on standard output.

- `holds[].kind` is `current_folder` or `open_handle`. `started` is the process's creation time as an ISO 8601 UTC string, or `null` when it is unknown.
- `may_hold` lists processes that may hold the folder without it being certain: `why` is `unnamed_handle` (a handle whose name could not be read) or `cannot_open` (the process could not be inspected). Unrelated system processes often show up here.
- On a `removed` report `holders` and `may_hold` are empty, even when a locked recycle listed holders before its retry went through: nothing holds a folder that is gone.
- `stopped` lists the build servers `--stop-build-servers` stopped. A stopped process appears under `stopped` only, never under `holders` or in the marker.
- `released` is an object, `{ "released_at", "reason", "holders" }`, when the run wrote a marker, else `null`. The marker file on disk holds the same fields with `released_at` in Unix seconds.
- `notes` explains anything kept or skipped, such as a branch that was not deleted.

## JSON report

`--json` prints one document, written for scripts and agents. Keys are snake_case, absent fields are `null`, and every timestamp is an ISO 8601 UTC string in whole seconds.

```json
{
  "root": "D:\\",
  "repos": [{ "path": "D:\\app", "default_branches": { "local": "main", "origin": "main" } }],
  "discovery_errors": [],
  "candidates": [
    {
      "kind": "registered",
      "path": "D:\\app.wt\\feature",
      "repo": "D:\\app",
      "branch": "feature",
      "head": "4518aff…",
      "prunable": null,
      "git_lock": null,
      "released": null,
      "merge_state": { "state": "unmerged", "commits": 3 },
      "merge_state_against": "main",
      "dirty": { "modified": 6, "untracked": 2 },
      "upstream": { "state": "tracking", "ahead": 1 },
      "last_activity": "2026-09-27T09:29:41Z",
      "size": { "bytes": 2933151, "files": 404, "unreadable": 0, "last_write": "2026-09-27T09:30:01Z" },
      "errors": []
    },
    {
      "kind": "orphan",
      "path": "D:\\app.wt\\old",
      "container": "D:\\app.wt",
      "orphan_kind": "folder",
      "link_target": null,
      "stale_gitdir": false,
      "live_gitdir": null,
      "has_git_dir": false,
      "size": { "bytes": 0, "files": 0, "unreadable": 0, "last_write": "2026-09-23T09:05:38Z" }
    }
  ]
}
```

- `candidates` is one flat array tagged by `kind`: `registered` or `orphan`, in the order the scan found them.
- `discovery_errors` lists `{ "repo", "path", "message" }` for each repo whose worktree list failed and each worktree git's list left out; it is `[]` when empty.
- `merge_state.state` is `ancestor`, `no_commits`, `patches_applied`, `content_contained`, `unmerged` (with `commits`) or `detached` (with `contained`).
- `upstream.state` is `none`, `gone` or `tracking` (with `ahead`).
- `orphan_kind` is `folder` or `link`. A `link` has its `link_target` set.
- `released` is `null`, or `{ "released_at", "reason", "holders" }` from the marker an agent's `remove` left (see above).
- A registered candidate carries an `errors` array naming the signals that could not be read, and the affected fields are `null`.

## Development

Requires the .NET 10 SDK; the tests run on Microsoft.Testing.Platform (`global.json`).

```
dotnet build WorktreeSweep.slnx -c Release
dotnet test WorktreeSweep.slnx -c Release --no-build
dotnet csharpier check .
dotnet format style WorktreeSweep.slnx --verify-no-changes --severity info
dotnet format analyzers WorktreeSweep.slnx --verify-no-changes --severity info
```

The build treats warnings as errors. `WORKTREE_SWEEP_LOG=debug` shows the tool's debug traces on standard error.

Terms used in the code and plans are defined in [docs/README.md](docs/README.md); the code's layout is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## License

MIT
