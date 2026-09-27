# worktree-sweep: find and clean up stale git worktrees

## Context

D:\ has built up worktrees that are no longer used. Measured on 2026-09-27:
- **Registered:** 7 worktrees across three parent repos (`in-the-sky`, `yaat`, `yaat-server`).
- **Unregistered leftovers:** empty folders (`in-the-sky.wt/x1-content`, `delve-the-dungeon.wt/stillbots`, `yaat.wt/eram-qx`), a 29 GB `yaat-server.wt/yaat` with a broken `.git` dir, a 16 GB cargo target dir `rustling-tulip.wt/.target-p1-7b`, and an old Claude agent worktree at `lang-tutor/.claude/worktrees/reverent-chebyshev-6108fc`.

Deleting these on Windows often fails because files are locked: a shell cd'd into the folder, an IDE, or a language server. The goal is a Rust CLI that:
1. Finds every non-main worktree plus orphan folders under a root.
2. Shows the signals you need to decide what to remove.
3. Deletes your picks recoverably.
4. When deletion hits a lock, offers to escalate with `sudo` to run `handle.exe`, show the locking processes, and stop them or (opt-in) force-close the handles.

Decisions from the interview:
- **Candidates:** show every non-main worktree with its signals and let you pick; also scan worktree container dirs for orphans.
- **Merge check:** covers cherry-picks and squashes as well as ancestry (a branch whose changes are already on the default branch counts as merged).
- **Uncommitted work:** a dirty or unmerged pick needs a second, per-item confirmation.
- **Locks:** stopping the locking process is the default; force-closing a handle is a separate, warned choice.
- **Deleting:** Recycle Bin first. When it can't take the folder, ask before a permanent delete.
- **Branches:** offer `git branch -d` only when the branch is merged, cherry-picked or content-contained.
- **Form:** Rust CLI in `D:\worktree-sweep` with a table plus `dialoguer` checkbox picker, `--list` and `--json`.

**Measured constraint:** D:'s Recycle Bin `MaxCapacity` is 14844 MB (registry `HKCU\...\Explorer\BitBucket\Volume\{22bbaec6-…}`, D:'s volume GUID). Both big leftovers exceed it. The tool must detect this ahead of time: the Shell's recycle call with no UI can silently delete permanently when an item is too large.

**Measured 2026-09-27:** recycling a folder that contains a junction (Shell `SendToRecycleBin` via `Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory`) removed the folder and the link and left the junction's target and its files intact.

## Tooling (measured)

- **Rust / git:** rustc 1.98.1; git 2.55 (`merge-tree --write-tree` is available).
- **handle.exe:** Sysinternals v5.0 on PATH (winget). Flags: `-nobanner -v` gives CSV output, and `-c <handle> -p <pid> -y` closes one handle.
- **sudo:** Windows `sudo.exe` in **Inline** mode, so an elevated child shares the console and can prompt the user directly.

## Design

Git is driven by shelling out to `git` (no gix/libgit2), because `worktree list --porcelain`, `cherry` and `merge-tree` are what we need. Windows APIs come from the `windows` crate.

### CLI
```
worktree-sweep [ROOT]            # default: current dir; scan, table, picker, act
worktree-sweep [ROOT] --list     # table only, exit
worktree-sweep [ROOT] --json     # machine-readable report, exit (agent-native)
worktree-sweep unlock <PATH>...  # internal: runs elevated via sudo
```

### Modules (`src/`)
- **`main.rs`:** clap parsing and dispatch; `anyhow`; `tracing` to stderr.
- **`discover.rs`:** finds candidates.
  - **Repos:** depth-1 dirs of ROOT that have a `.git` *dir*.
  - **Registered worktrees:** `git worktree list --porcelain` for each repo. Skip the main worktree. Keep `locked <reason>` (git's own lock) and `prunable`.
  - **Container dirs:** depth-1 dirs matching `*.wt`, `*-wt`, `*worktrees`, plus `<repo>/.claude/worktrees`.
  - **Orphans:** walk each container. A child that is a registered worktree is skipped. A child that contains registered worktrees is recursed into; this covers the `yaat.wt/eram-am/{yaat,yaat-server}` layout. Any other child is an orphan candidate, with a note when it holds a `.git` file whose `gitdir:` no longer exists.
- **`signals.rs`:** per registered worktree, run in parallel with `std::thread::scope`.
  - **Default branch:** `refs/remotes/origin/HEAD`, else local `main`/`master`. Each check below runs against both local and origin default, and the better result wins.
  - **Merge state, as an enum `MergeState`:**
    - `Ancestor`: `merge-base --is-ancestor`.
    - `PatchesApplied`: `git cherry <default> <branch>` has no `+` lines, meaning every commit has a patch-equivalent on the default branch (cherry-picks).
    - `ContentContained`: `git merge-tree --write-tree <default> <branch>` gives a tree equal to `<default>^{tree}`, meaning squash-merged or otherwise already in.
    - `Unmerged { commits }`.
    - `Detached`: no branch.
  - **Dirty:** `git status --porcelain` counts of modified and untracked files; ignored files are left out.
  - **Unpushed:** `rev-list @{u}..HEAD` count; "upstream gone" when the tracking ref was deleted.
  - **Last activity:** the later of the HEAD commit time and the mtime of the worktree's `$GIT_DIR/worktrees/<id>/index`.
  - **Size:** a directory walk. It is shared with orphans, which get size, last-write time and a file count.
- **`report.rs`:** the table and the `--json` output (serde).
- **`pick.rs`:** `dialoguer::MultiSelect`, with nothing selected by default.
  - A pick that is dirty, `Unmerged`, unpushed, or git-locked gets a second `Confirm` that spells out what would be lost.
  - A git-locked pick also names the lock reason.
- **`remove.rs`:** removes one pick.
  1. Check the size against the volume's Recycle Bin capacity (the next bullet). If it doesn't fit, or `NukeOnDelete=1` is set, ask for a permanent delete; a no skips the item.
  2. Recycle through `IFileOperation`, with the flags set so the Shell never silently turns it into a permanent delete (`FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING`, no `FOF_NOCONFIRMATION`). A permanent delete uses `std::fs::remove_dir_all` after clearing read-only attributes, which git object files carry.
  3. On a sharing violation or access denied (Win32 32/5), hand off to the unlock flow and retry once.
  4. For a registered worktree: `git worktree unlock` if it was git-locked, then `git worktree prune`. If the branch is `Ancestor`, `PatchesApplied` or `ContentContained`, offer `git branch -d`. For the last two, git may refuse `-d`, so offer `-D` with the reason shown.
- **Recycle Bin capacity** (inside `remove.rs` unless it grows): `GetVolumeNameForVolumeMountPointW` gives the volume GUID, and `HKCU\...\BitBucket\Volume\{GUID}` holds `MaxCapacity` in MB and `NukeOnDelete`.
- **`unlock.rs`:** the lock flow.
  - **Parent side:** explains what will happen, asks, then runs `sudo <self> unlock <paths>` and waits. If sudo is missing or disabled, it prints the elevated command to run by hand.
  - **Elevated side:** runs `handle.exe -nobanner -v <path>` and parses the CSV.
    - Ignore its own PID and its sudo parent.
    - Group results by process: name, PID, handle count, a sample path.
    - Flag the process that launched the tool (for example the pwsh you are typing in), because stopping it would close your own shell.
    - Prompt per process: **stop process** (`TerminateProcess`), **close handles** (warned: may crash the app; runs `handle -c <h> -p <pid> -y` for each handle), or **skip**.
    - Exit code: 0 when every lock is cleared.
- **`handle_csv.rs`:** a pure parser for handle.exe output, unit tested. The exact column order is measured from a real run during implementation (test with a folder the implementer holds open) and recorded as a fixture.

### Repo scaffolding
- **`Cargo.toml`:** edition 2024; the full `[lints.clippy]` block from the language-conventions skill.
  - **Deps:** `anyhow`, `clap` (derive), `dialoguer`, `serde` and `serde_json`, `tracing` and `tracing-subscriber`, `windows` (Storage_FileSystem, UI_Shell, System_Threading, System_Registry, System_Com). Versions are looked up at the time of adding.
  - **Dev-deps:** `tempfile`.
- **Other config:** `deny.toml` (licenses: MIT, Apache-2.0, Unicode-3.0, ISC, BSD-3-Clause, Zlib), `rust-toolchain.toml` (stable), `.gitignore` (`/target`, `.tmp/`).
- **Hooks:** `prek` config running `cargo fmt --check`, `cargo clippy --all-targets --all-features -- -D warnings` and `cargo test`.
- **Docs:** `README.md` (usage, and a safety section on what the elevated step does); `docs/README.md` glossary defining *orphan*, *container dir*, *content-contained*, *patches applied* and *git-locked vs file-locked*; `docs/plans/MAIN.md` with this work as checkboxes; `CHANGELOG.md`.

## Tests

Integration tests build throwaway repos with real `git` in `tempfile` dirs.
- **Merge states:**
  - a merged branch gives `Ancestor`;
  - a cherry-picked branch gives `PatchesApplied`;
  - a squash-merged branch gives `ContentContained`;
  - a divergent branch gives `Unmerged { commits: n }`;
  - a detached HEAD gives `Detached`.
- **Dirty and unpushed:** untracked and modified files are counted; a deleted upstream is reported as gone.
- **Discovery:**
  - a registered worktree nested two levels deep inside a container;
  - an empty orphan;
  - an orphan whose `.git` file points to a missing gitdir;
  - a prunable registration;
  - the main worktree is never a candidate.
- **Parsers and helpers:** `handle_csv` against the recorded fixture, plus empty output and malformed lines; the Recycle Bin fit decision for size under the cap, over the cap, and with `NukeOnDelete` set (pure function; the registry read is kept at the edge).
- **Removal:** permanent delete clears read-only files; a locked file (the test holds it open with no sharing) produces the `Locked` error variant rather than a generic failure.
- **Not automated:** the sudo and handle path, which needs UAC. It is checked by hand (next section).

## Verification
1. `cargo fmt --check`, `cargo clippy --all-targets --all-features -- -D warnings`, `nice`-level `cargo test`, and `cargo deny check`, all clean.
2. `worktree-sweep D:\ --list` against the real D:\. Expected:
   - the 7 registered worktrees with sensible merge states;
   - the orphans listed in Context;
   - no main repos.
3. `--json` output parses (`jq`).
4. **Manual lock test:** make a scratch worktree of this repo under `D:\worktree-sweep.wt\lock-test`, open a pwsh with its cwd there, pick it in the tool, and confirm that:
   - the delete fails as locked;
   - sudo prompts once;
   - the pwsh is listed by name and PID;
   - "stop process" clears it;
   - the retry sends the folder to the Recycle Bin and prunes the registration.
5. **Over-capacity path:** run it on a scratch folder only. Real deletion on D:\ happens with you at the keyboard.

## Execution
Following your plan-execution flow: I scaffold the repo and write the docs and plan files, and the `implementer` agent writes the Rust source and tests in bundled briefs:
1. Scaffold, discovery and signals, with their tests.
2. Report, picker and removal.
3. Unlock and the handle parser.

I review each report and run the gate. No commits until you say so.
