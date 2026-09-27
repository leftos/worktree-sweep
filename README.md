# worktree-sweep

A Windows CLI that finds stale git worktrees and leftover worktree folders under a folder, shows what each one still holds, and removes the ones you pick. Everything goes to the Recycle Bin first. When a folder is locked by another process, it escalates once with `sudo` to find the process with Sysinternals `handle.exe` and stop it.

## Install

```
cargo install --git https://github.com/leftos/worktree-sweep
```

Requirements:

- Windows 11 with `sudo` turned on in **Inline** mode (Settings → System → Advanced → Enable sudo; `sudo config --enable normal`). Without it, the tool prints the elevated command for you to run by hand.
- Sysinternals `handle.exe` on `PATH` (`winget install Microsoft.Sysinternals.Handle`). Only the unlock step needs it.
- `git` on `PATH`.

## Usage

```
worktree-sweep [ROOT]           # scan, show the table, pick, confirm, remove
worktree-sweep [ROOT] --list    # show the table and exit
worktree-sweep [ROOT] --json    # print the report as JSON and exit
```

`ROOT` defaults to the current folder. The tool looks at:

- **Repos:** folders directly under `ROOT` with a `.git` folder. Every linked worktree `git worktree list` reports is a candidate; the main worktree never is.
- **Container folders:** folders directly under `ROOT` named `*.wt`, `*-wt` or `*worktrees`, plus each repo's `.claude/worktrees`. A folder in one that no repo registers is an **orphan** candidate, such as the leftover of a failed `git worktree remove`.

For each candidate the table shows:

| Column | Meaning |
|---|---|
| `KIND` | `worktree` (registered), `orphan`, or `link` (a junction or symbolic link orphan) |
| `MERGE` | `merged` (the branch is in the default branch), `no commits` (a branch nothing was committed on), `cherry-picked`, `squashed`, `unmerged N`, or `detached` |
| `DIRTY` | modified (`M`) and untracked (`?`) files, not counting ignored ones |
| `UPSTREAM` | `+N` commits the upstream lacks, or `gone` when the upstream branch was deleted |
| `ACTIVE` | time since the last commit or index write, whichever is later; for an orphan, its last file write |
| `SIZE` | disk usage, without following links |
| `FLAGS` | `git-locked`, `prunable` (its folder is gone), `stale .git` (an orphan whose `.git` file points nowhere), `registered elsewhere` |

Merge checks run against both the local and the `origin` default branch, and the better result counts.

The interactive mode needs a terminal. Nothing is picked by default.

## Safety

- **The scan writes nothing.** Git runs with optional locks and fsmonitor off, and the squash-merge check writes its objects to a scratch folder.
- **Second confirmation for anything that would lose work.** A pick that is dirty, unmerged, unpushed, has no commits yet (another session may be about to use it), or is locked with `git worktree lock` asks again and names what would be lost.
- **Recycle Bin first.** Before recycling, the tool checks the item against the volume's Recycle Bin size limit. An item that is too big, or a volume set to delete immediately, asks for a permanent delete instead; saying no skips it. The Shell is never left to delete permanently on its own.
- **Links are removed as links.** A junction or symbolic link orphan is deleted as a link; its target is never entered, sized or deleted.
- **Branches are deleted only when merged.** After removing a registered worktree, the tool prunes its registration and offers `git branch -d` when the branch is merged, cherry-picked, squashed or has no commits.

### Locked folders

Windows refuses to move a folder while a process holds a file in it or has it as its current folder. The tool first retries a lock briefly, because antivirus scanners hold new files for a second or two. Then:

1. It finishes every other pick, and collects the locked ones.
2. It asks once, then runs itself elevated through `sudo` for all of them together. That is one UAC prompt per run.
3. The elevated step lists every process holding a handle inside a locked folder, by name and PID. For each one you choose: **stop process** (the default), **close its handles** (the app may crash), **skip**, or **done**. The shell you started the tool from is marked, and skip is its default.
4. It repeats until nothing holds the folders or you pick done, then retries each locked pick once.

## JSON report

`--json` prints one document, written for scripts and agents:

```json
{
  "root": "D:\\",
  "repos": [{ "path": "D:\\app", "default_branches": { "local": "main", "origin": "main" } }],
  "candidates": [
    {
      "kind": "registered",
      "path": "D:\\app.wt\\feature",
      "repo": "D:\\app",
      "branch": "feature",
      "head": "4518aff…",
      "prunable": null,
      "git_lock": null,
      "merge_state": { "state": "unmerged", "commits": 3 },
      "merge_state_against": "main",
      "dirty": { "modified": 6, "untracked": 2 },
      "upstream": { "state": "tracking", "ahead": 1 },
      "last_activity_unix": 1790549381,
      "size": { "bytes": 2933151, "files": 404, "unreadable": 0, "last_write_unix": 1790549401 }
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
      "size": { "bytes": 0, "files": 0, "unreadable": 0, "last_write_unix": 1790137538 }
    }
  ]
}
```

- `merge_state.state` is `ancestor`, `no_commits`, `patches_applied`, `content_contained`, `unmerged` (with `commits`) or `detached` (with `contained`).
- `upstream.state` is `none`, `gone` or `tracking` (with `ahead`).
- `orphan_kind` is `folder` or `link`. A `link` has its `link_target` set.
- A registered candidate whose signals could not all be read carries an `errors` array, and the affected fields are `null`.

## Development

```
cargo test
cargo clippy --all-targets --all-features -- -D warnings
cargo deny check
```

Terms used in the code and plans are defined in [docs/README.md](docs/README.md).

## License

MIT
