# worktree-sweep docs

Start here. Plans live in [plans/MAIN.md](./plans/MAIN.md).

## Glossary

- **Brief**: a written, self-contained implementation step (files, change, proving commands) handed to an implementer agent; open briefs live in `docs/plans/`.
- **Candidate**: a folder the tool offers to remove, either a registered worktree or an orphan.
- **Registered worktree**: a linked worktree that `git worktree list` in some repo under the root reports. The repo's main worktree is never a candidate.
- **Container dir**: a folder that holds worktrees: a depth-1 folder of the root named `*.wt`, `*-wt` or `*worktrees`, or a repo's `.claude/worktrees`.
- **Orphan**: a folder inside a container dir that no repo registers as a worktree, such as the leftover of a failed `git worktree remove`.
- **Junction orphan**: an orphan that is a junction or symlink to a folder elsewhere (e.g. `D:\yaat-server.wt\yaat` → `X:\dev\yaat`). Removing it deletes the link only; the target is never entered, sized or deleted.
- **Prunable**: a registration whose folder no longer exists; `git worktree prune` drops it.
- **Ancestor** (merge state): the branch tip is reachable from the default branch.
- **No commits** (merge state): the branch tip is reachable from the default branch, but the branch's complete reflog records nothing except its creation and operations that make no commit (fast-forwards, resets, rebases), as for a branch just created for work not yet started. The picker asks twice before removing it, because another session may be about to use it.
- **Patches applied** (merge state): every commit on the branch has a patch-equivalent commit on the default branch (`git cherry` reports no `+`), as after cherry-picks or a rebase-merge.
- **Content-contained** (merge state): merging the branch into the default branch would change nothing (`git merge-tree` result equals the default branch's tree), as after a squash merge.
- **Git-locked**: a worktree marked with `git worktree lock`; git refuses to prune or remove it until unlocked.
- **File-locked**: a folder that Windows refuses to delete because some process holds a handle in it (an open file, or a shell's current directory).
- **Unlock flow**: the elevated step (`sudo worktree-sweep unlock`) that lists file-locking processes with `handle.exe` and stops them or closes their handles. It runs at most once per run, for every file-locked pick together.
- **Locker**: a process that holds at least one handle (`File` or `Section`) on a path inside a file-locked pick; the unlock flow offers one prompt per locker.
- **Holder**: a process found without elevation to hold a folder, through its current directory or an open disk handle inside it; the agent path's counterpart of a locker. A holder whose handle can't be named, or that can't be opened, is reported as "may hold".
- **Lock kind**: how a process holds a folder (its current directory, an open file, a folder handle, a running exe or loaded DLL). Some kinds block a recycle and others only a permanent delete; the table is in [agent-path.md](./agent-path.md).
- **Released**: a worktree an agent asked to remove but couldn't, marked so the next interactive sweep lists it first and pre-picks it.
- **Review**: the questions asked after picking and before anything is removed: per pick, what it would lose, a permanent delete, its branch; then one final confirmation.
- **Caller**: the shell that started worktree-sweep; it is flagged as a locker, never stopped by default.
- **Feature marker**: `branch: feat/<name>` on a `docs/plans/MAIN.md` line; every item under it lands on the `feat/<name>` branch instead of `main` (user-level `nextup` §3, "Feature branches").
- **Feature PR**: the draft pull request from a marker's `feat/<name>` into `main`, opened with the marker and merged with `--rebase` by `/ship` once every line under the marker is ticked.
