# worktree-sweep docs

Start here. Plans live in [plans/MAIN.md](./plans/MAIN.md).

## Glossary

- **Candidate**: a folder the tool offers to remove, either a registered worktree or an orphan.
- **Registered worktree**: a linked worktree that `git worktree list` in some repo under the root reports. The repo's main worktree is never a candidate.
- **Container dir**: a folder that holds worktrees: a depth-1 folder of the root named `*.wt`, `*-wt` or `*worktrees`, or a repo's `.claude/worktrees`.
- **Orphan**: a folder inside a container dir that no repo registers as a worktree, such as the leftover of a failed `git worktree remove`.
- **Junction orphan**: an orphan that is a junction or symlink to a folder elsewhere (e.g. `D:\yaat-server.wt\yaat` → `X:\dev\yaat`). Removing it deletes the link only; the target is never entered, sized or deleted.
- **Prunable**: a registration whose folder no longer exists; `git worktree prune` drops it.
- **Ancestor** (merge state): the branch tip is reachable from the default branch.
- **Patches applied** (merge state): every commit on the branch has a patch-equivalent commit on the default branch (`git cherry` reports no `+`), as after cherry-picks or a rebase-merge.
- **Content-contained** (merge state): merging the branch into the default branch would change nothing (`git merge-tree` result equals the default branch's tree), as after a squash merge.
- **Git-locked**: a worktree marked with `git worktree lock`; git refuses to prune or remove it until unlocked.
- **File-locked**: a folder that Windows refuses to delete because some process holds a handle in it (an open file, or a shell's current directory).
- **Unlock flow**: the elevated step (`sudo worktree-sweep unlock`) that lists file-locking processes with `handle.exe` and stops them or closes their handles.
