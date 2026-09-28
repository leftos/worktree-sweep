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
- [x] Manual lock test from the design (elevated, the user ran it 2026-09-27): a `cmd` with its cwd in the worktree was reported as locked, one `sudo` session listed `cmd.exe` by PID, "Stop process" cleared it, and the retry recycled the folder, pruned it and deleted the branch. A `pwsh -WorkingDirectory` shell did not lock the folder at all
- [x] Recycling a locked folder never reaches the unlock flow. Manual lock test (2026-09-27): the Shell showed its own "Folder In Use" dialog, and after Cancel, removal failed with `cannot move … to the Recycle Bin: 0x80270000` instead of `RemoveError::Locked`. Fixed: no Shell error UI, COPYENGINE lock codes map to `Locked`, and a lock is retried at 250/500/1000 ms (user, 2026-09-27) before it counts as locked.
- [x] A branch with no commits of its own reads as `merged`: on D:\ (2026-09-27), four live worktrees of other sessions, 0 commits ahead of `main` and active minutes ago, showed as merged. Ruling (user, 2026-09-27): a new merge state `NoCommits` ("no commits"). It applies when the branch is an ancestor of the default branch and its reflog has no `commit` entries. The picker asks a second confirmation for it, as for unmerged work.
- [x] A prompt wider than the terminal prints twice: in the manual lock test (2026-09-27) the second confirmation showed as `…Remove anyway? [y/N…Remove anyway? yes` and the picker's echo line came out cut short. `dialoguer` clears one line after an answer, and a prompt that wraps takes two. Shorten the prompts (put the path on its own line, or trim it to the terminal width)
- [x] README usage and safety section; CHANGELOG entry. Rulings (user, 2026-09-27): install with `cargo install --git https://github.com/leftos/worktree-sweep`, prerequisites Windows 11 `sudo` (Inline mode) and Sysinternals `handle.exe` on PATH; the CHANGELOG starts with `## [Unreleased]`, renamed to 0.1.0 at the release cut

## Backlog

- [ ] Full-screen picker with `ratatui` + `crossterm` (user, 2026-09-27). It replaces the table-then-`MultiSelect` flow: the candidate table is the picker (scroll, tick rows in place), a detail pane shows the selected row's full path, what removing it loses and the link target, and confirmations are in-screen dialogs, so nothing wraps or redraws. Rulings (user, 2026-09-27):
  - **Unlock step:** leave the alternate screen and restore the terminal, run `unlock::offer` as it runs today (plain prompts, UAC), then re-enter and show the retry results in-screen; a guard restores the terminal on any exit or panic.
  - **`--list` and `--json` keep the plain output**; the TUI starts only in interactive mode on a real terminal, and interactive mode refuses to start when stdout is not a TTY.
  - **`dialoguer` is removed entirely**: the TUI is the only interactive picker, with no `--plain` fallback.
  - Picking and confirmation logic stay in pure functions, testable without a terminal; the two new dependencies pass `cargo deny`.
  - Design: [tui-picker.md](./tui-picker.md), drafted and reviewed 2026-09-27, with its eight open questions answered (unlock line prompts, every question up front, essentials-only keys, risk tints with `NO_COLOR`, Ctrl+C stops after the current item, the scan inside the TUI with a spinner, nothing printed after exit, a final confirmation). The "Proposed brief split" in the design lists each brief's files. Three briefs, in order:
  - [x] T1 — [archive/brief-t1-decisions.md](./archive/brief-t1-decisions.md): every question asked up front through a pure `Review` (`src/tui/review.rs`), a final confirmation, `Prompter` removed; `main.rs` drives Review with `dialoguer` until T3
  - [ ] T2: the TUI core, not wired in (`src/tui/mod.rs` already exists from T1 and holds `pub mod review;`)
  - [ ] T3: wiring it in and removing `dialoguer`; `remove_picks` takes `on_progress` and `offer_unlock` callbacks ready for the worker thread

- [x] Test cleanup: `make_junction` is copied in three integration test files (`tests/integration/removal.rs:33`, `holders.rs:215`, `agent_remove.rs:52`), and `tests/integration/discovery.rs:176-199` inlines a fourth `mklink /J`; move one copy into `tests/integration/fixture.rs` and use it in all four

### Done from the backlog

- [x] Agent path for locked worktrees (user, 2026-09-27): Claude Code agents report that they can't trash their own worktrees when they're done, because something holds them. The tool could give an agent a non-interactive way in (no picker, no UAC): remove one named worktree, or report what holds it, as JSON. Rulings (user, 2026-09-27):
  - **Remove, else release.** `worktree-sweep remove <PATH> --json` recycles one worktree, prunes it, and deletes its branch when that is merged or has no commits. On a lock it prints JSON naming the lock and marks the worktree "released by an agent" (a small file in the repo's `.git`). The next interactive sweep lists released worktrees first and pre-picks them, behind the usual confirmations and its one elevation.
  - **Allowlisted stops only.** A flag lets the agent stop known build servers (MSBuild nodes, VBCSCompiler, rust-analyzer, cargo) running as the user, and only when their current folder or open files are inside that worktree. Nothing else is ever stopped unelevated.
  - Measured and decided — see [agent-path.md](../agent-path.md) (promoted to `docs/` once done): unelevated holders are found in about 0.35–0.65 s from each process's cwd plus its disk handles, and six more rulings (allowlist, unseen holders, caller's shell, lost work, too big) are recorded there, with the decision round's answers (exit codes, `would_lose`, branch, marker). Three briefs, in order:
  - [x] 4a — [archive/brief-4a-holders.md](./archive/brief-4a-holders.md): `src/holders.rs` finds holders unelevated (cwd, disk handles, the root's PID list), the allowlist, the ancestor walk, and matches the folder as given as well as resolved (subst, junction, 8.3)
  - [x] 4b — [archive/brief-4b-remove-command.md](./archive/brief-4b-remove-command.md): the `remove <PATH> --json` command, its refusals (including a worktree whose `.git` file is gone), the marker write, and finding a deleted worktree beside its repo
  - [x] 4c — [archive/brief-4c-released-first.md](./archive/brief-4c-released-first.md): the scan reads the marker (found from the common dir, relative `gitdir` included), lists released worktrees first with a `released` flag, and pre-picks them (`pick::default_picks`)