# worktree-sweep plan
<!-- plan-doc-hygiene: 2026-09-27 7e6ad89 -->

Design: [v1-design.md](./v1-design.md). Execution ledger (untracked): `.tmp/plan-ledger.md`.

## Current focus: v1

- [x] Scaffold: crate, lints, cargo-deny, prek hooks, docs skeleton
- [x] Public GitHub repo `leftos/worktree-sweep` (user, 2026-09-27)
- [x] Brief 1: discovery (repos, registered worktrees, container dirs, orphans) and signals (merge state, dirty, unpushed, last activity, size), with fixture-repo tests
- [x] Brief 2: report table and `--json`, picker with second confirmation, removal (Recycle Bin capacity check, recycle, permanent delete, prune, branch delete)

### Wave 1: unlock — `src/unlock.rs`, `src/handle_csv.rs`, `src/main.rs` — gate: `code-review`

- [x] Brief 3 — see [archive/brief-3-unlock.md](./archive/brief-3-unlock.md): `handle.exe` CSV parser, elevated `unlock` subcommand, retry-after-unlock in removal. One elevation per run (user, 2026-09-27): locked picks are collected, one `sudo` session with a single unfiltered `handle.exe` dump handles all of them, then every locked pick is retried. Gates green; the elevated end-to-end run is the manual lock test in Wave 2

### Wave 2: release readiness — `README.md`, `CHANGELOG.md` — gate: human check

- [x] Verify against real D:\ (`--list`, `--json`): both run clean (2026-09-27, 10 candidates, `--list` 9.7 s)
- [ ] Manual lock test from the design (elevated, the user runs it)
- [x] Recycling a locked folder never reaches the unlock flow. Manual lock test (2026-09-27): the Shell showed its own "Folder In Use" dialog, and after Cancel, removal failed with `cannot move … to the Recycle Bin: 0x80270000` instead of `RemoveError::Locked`. Fixed: no Shell error UI, COPYENGINE lock codes map to `Locked`, and a lock is retried at 250/500/1000 ms (user, 2026-09-27) before it counts as locked.
- [x] A branch with no commits of its own reads as `merged`: on D:\ (2026-09-27), four live worktrees of other sessions, 0 commits ahead of `main` and active minutes ago, showed as merged. Ruling (user, 2026-09-27): a new merge state `NoCommits` ("no commits"). It applies when the branch is an ancestor of the default branch and its reflog has no `commit` entries. The picker asks a second confirmation for it, as for unmerged work.
- [ ] README usage and safety section; CHANGELOG entry

## Backlog

- [ ] Agent path for locked worktrees (user, 2026-09-27): Claude Code agents report that they can't trash their own worktrees when they're done, because something holds them. The tool could give an agent a non-interactive way in (no picker, no UAC): remove one named worktree, or report what holds it, as JSON. Rulings (user, 2026-09-27):
  - **Remove, else release.** `worktree-sweep remove <PATH> --json` recycles one worktree, prunes it, and deletes its branch when that is merged or has no commits. On a lock it prints JSON naming the lock and marks the worktree "released by an agent" (a small file in the repo's `.git`). The next interactive sweep lists released worktrees first and pre-picks them, behind the usual confirmations and its one elevation.
  - **Allowlisted stops only.** A flag lets the agent stop known build servers (MSBuild nodes, VBCSCompiler, rust-analyzer, cargo) running as the user, and only when their current folder or open files are inside that worktree. Nothing else is ever stopped unelevated.
  - Needs a measurement before a brief: how to see, unelevated and fast, which of the user's processes hold a folder (Restart Manager, or the process's cwd through the PEB). Unelevated `handle.exe` takes about 141 s.
