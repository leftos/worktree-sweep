# worktree-sweep docs

Start here. Plans live in [plans/MAIN.md](./plans/MAIN.md).

- [`ARCHITECTURE.md`](ARCHITECTURE.md): the architecture entry point: Task Index, layers, integration footguns, test locations and the deep docs.
- [`design.md`](design.md): the design, its rulings and invariants.
- [`agent-path.md`](agent-path.md): the agent `remove` path and its lock measurements.

## Glossary

- **Abandonable worker**: one of the holder finder's four threads that name open handles; a lookup that takes over 200 ms is cancelled, its process reported as "may hold", and the worker abandoned for a fresh one if the lookup still does not return.
- **Ancestor** (merge state): the branch tip is reachable from the default branch.
- **Brief**: a written, self-contained implementation step (files, change, proving commands) handed to an implementer agent; open briefs live in `docs/plans/`.
- **Caller**: the shell that started worktree-sweep; it is flagged as a locker, never stopped by default.
- **Candidate**: a folder the tool offers to remove, either a registered worktree or an orphan.
- **Container dir**: a folder that holds worktrees: a depth-1 folder of the root named `*.wt`, `*-wt` or `*worktrees`, or a repo's `.claude/worktrees`.
- **Content-contained** (merge state): merging the branch into the default branch would change nothing (`git merge-tree` result equals the default branch's tree), as after a squash merge.
- **Decision**: a pick with its plan and its branch choice, the unit the sweep works through.
- **Decision round**: the step before an item's briefs where every open design choice its exploration found is settled, from the docs and code or by asking; the orchestrator settles them (owner-delegated).
- **Discovery error**: a repo whose `git worktree list` failed, or a linked worktree git's list left out because its `.git\worktrees\{id}\gitdir` file is missing, unreadable or empty. The scan reports each; a worktree it hides would otherwise look like an orphan.
- **Feature marker**: `branch: feat/<name>` in a Linear project's content; every item under it lands on the `feat/<name>` branch instead of `main` (user-level `nextup` §3, "Feature branches").
- **Feature PR**: the draft pull request from a marker's `feat/<name>` into `main`, opened with the marker and merged with `--rebase` by `/ship` once every issue in the marker's project has landed.
- **File type index**: the number Windows gives the `File` object type on this boot; the holder finder learns it from one of its own handles and keeps only handles of that type.
- **File-locked**: a folder that Windows refuses to delete because some process holds a handle in it (an open file, or a shell's current directory).
- **Git-locked**: a worktree marked with `git worktree lock`; git refuses to prune or remove it until unlocked.
- **Holder**: a process found without elevation to hold a folder, through its current directory or an open disk handle inside it; the agent path's counterpart of a locker. A holder whose handle can't be named, or that can't be opened, is reported as "may hold".
- **Junction orphan**: an orphan that is a junction or symlink to a folder elsewhere (e.g. `D:\yaat-server.wt\yaat` → `X:\dev\yaat`). Removing it deletes the link only; the target is never entered, sized or deleted.
- **Lock kind**: how a process holds a folder (its current directory, an open file, a folder handle, a running exe or loaded DLL). Some kinds block a recycle and others only a permanent delete; the table is in [agent-path.md](./agent-path.md).
- **Locker**: a process that holds at least one handle (`File` or `Section`) on a path inside a file-locked pick; the unlock flow offers one prompt per locker.
- **No commits** (merge state): the branch tip is reachable from the default branch, but the branch's complete reflog records nothing except its creation and operations that make no commit (fast-forwards, resets, rebases), as for a branch just created for work not yet started. Review asks twice before removing it, because another session may be about to use it.
- **Orphan**: a folder inside a container dir that no repo registers as a worktree, such as the leftover of a failed `git worktree remove`.
- **Parent chain**: a process's parent, its parent's parent, and so on up. The tool never offers its own chain as lockers or holders, and `remove` refuses with `caller_holds` when a process in it holds the folder. A link counts as ancestry only if the parent was created before its child, since a reused PID ends the chain.
- **Patches applied** (merge state): every commit on the branch has a patch-equivalent commit on the default branch (`git cherry` reports no `+`), as after cherry-picks or a rebase-merge.
- **PEB**: a Windows process's environment block, an in-memory structure that holds its current folder and command line. The holder finder reads another process's PEB at fixed offsets (64-bit and WOW64 layouts) to find which processes sit inside a folder.
- **Plan** (removal): what will happen to one pick once Review is answered: an action to run (remove the link, prune the registration, recycle, or delete permanently) or a reason to skip it.
- **Prunable**: a registration whose folder no longer exists; `git worktree prune` drops it.
- **Registered worktree**: a linked worktree that `git worktree list` in some repo under the root reports. The repo's main worktree is never a candidate.
- **Released**: a worktree an agent asked to remove but couldn't, marked so the next interactive sweep lists it first and pre-ticks it.
- **Review**: the questions asked after picking and before anything is removed: per pick, what it would lose, a permanent delete, its branch; then one final confirmation.
- **Scan JSON**: the document `--json` prints: the root, each repo's default branches, the discovery errors and every candidate with its signals. Keys are snake_case, candidates carry a `kind` tag, and times are ISO 8601 UTC strings; the shape is in the README's "JSON report".
- **Scratch object folder**: a temporary object directory (`worktree-sweep-objects-*` under the temp folder) that borrows a repo's objects as an alternate, so `git merge-tree --write-tree` writes its objects there and the scan leaves the repo untouched. One serves all of a worktree's default branches and is deleted when its signals are read.
- **Seam**: a small interface or record of delegates (`AgentSeams`, `WindowSeams`, `ISweeper`, `IHandleExe`) through which code reaches outside the process (the Shell, other processes, the registry, the UI thread), so tests substitute fakes.
- **Settle**: record a ruling in the Linear issue's description (and the design doc it belongs to), replacing the open question, so later sessions read it there.
- **Share-none**: a file opened with no sharing (`FileShare.None`), so no other process can open, rename or delete it while it is held.
- **Stall set**: `VolumeStalls`, the volumes where a git call timed out during one scan. Every later git call for a path on a stalled volume is skipped with a notice, so one hung share costs one timeout rather than one per repo.
- **Start time** (process): the creation time Windows records for a process. The tool keeps the one it read for a caller, parent or locker, and acts on that PID later only while the start time still matches, since Windows reuses a PID once its process exits (**PID reuse**).
- **Unlock flow**: the elevated step (`sudo worktree-sweep unlock`) that lists file-locking processes with `handle.exe` and stops them or closes their handles. It runs at most once per run, for every file-locked pick together.
- **Unlock step**: the terminal part of the unlock flow, asked as plain numbered line prompts while the window waits.
