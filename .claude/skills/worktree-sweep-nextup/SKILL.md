---
name: worktree-sweep-nextup
description: Profile for the user-level `nextup` skill in the worktree-sweep repo — loaded by `nextup` at its step 0 for this project's plan convention, agents, gates, docs map and landing path. Not a loop of its own; invoke `/nextup`.
---

# worktree-sweep profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is worktree-sweep-specific.

## Plan and tracker

- Index: `docs/plans/MAIN.md`, section `## Current focus: v1`, top to bottom, then `## Backlog`. The approved design is `docs/plans/v1-design.md`; every brief reads its section for the module it touches.
- A written brief waiting in `docs/plans/` (e.g. `brief-3-unlock.md`) is dispatched as written. Re-check its "Measured facts" against the code on `main` first (brief 2 may have changed a signature it names), and fix the brief, not the implementer's result.
- Siblings: none.
- Pre-loop hooks: none.
- Finished-item convention: **tick the line** (`- [x]`). A dispatched brief file moves to `docs/plans/archive/` in the landing commit.
- Tracker: `gh issue list --repo leftos/worktree-sweep --state open --json number,title`. No triage skill; place issues by the step-0 rule.
- Hotspots: `src/main.rs` (CLI dispatch), `src/remove.rs` (removal orchestration that the unlock flow plugs into), `src/lib.rs` (module list).

## Rulings every brief carries

- **Read-only against the real D:\.** A run against D:\ uses `--list` or `--json` only. The interactive mode and any removal run only against `tempfile` fixtures or folders under `.tmp\`. Real cleanup of D:\ happens with the user at the keyboard.
- **One elevation per run**: removal is two passes; locked picks go to a single `unlock::offer(&[paths])`, one `sudo` session, one unfiltered `handle.exe` dump filtered by every locked path, a loop until clear or Done, then one retry pass.
- **Junctions are links, never trees**: remove with `remove_dir` on the link after re-checking it is still a reparse point; never size, walk, recycle or recursively delete through one (`D:\yaat-server.wt\yaat` → `X:\dev\yaat` is real).
- **The scan writes nothing**: every git call clears the repo-local env vars (`git::clear_repo_env`) and runs with `GIT_OPTIONAL_LOCKS=0`, `core.fsmonitor=false`; `merge-tree` writes to a scratch object dir.
- **Recycle Bin capacity is checked before recycling** (D:'s cap measured at 14844 MB); an item over it asks for a permanent delete instead of letting the Shell nuke it silently.
- Lints are strict (pedantic, no unwrap/panic/print): output through a locked stdout writer, tests assert with `anyhow::ensure!`.

## Agents and gates

- Explore: `Explore`. Rust design second opinion: `oracle`.
- Implementer: the Opus `implementer` through `Agent` (module API shapes are still being settled, so briefs leave small design calls to it).
- Reviewers: `code-review` for every item.
- Gates, each wrapped as `cmd > .tmp/<name>.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/<name>.log -Tail 20; "rc=$rc"` from the repo root, test runs at BelowNormal priority:
  - `cargo fmt --all -- --check`
  - `cargo clippy --all-targets --all-features -- -D warnings`
  - `cargo test`, and once with `$env:GIT_INDEX_FILE='.git/index.lock'; $env:GIT_DIR='D:\nonexistent'` set in the same pwsh call (the prek hook runs tests inside `git commit`, where git exports those)
  - `cargo deny check` when `Cargo.toml` or `Cargo.lock` changed
- Needs the user: anything that runs elevated. `handle.exe` sees nothing unelevated, and a UAC prompt needs a person. Ask the user to run the command with `!`, quoting a Windows path in single quotes (`'D:\x\y'`): bash eats unquoted backslashes, and `handle.exe` matches only backslash paths.
- Parent-side gate: `git status --short` in the repo.

## Traps

- **A test that runs `git` inherits the hook environment.** Fixture helpers must go through `git::clear_repo_env`; a test that passes on its own and fails in `git commit` is this.
- **D:\ changes under you.** Other sessions add and remove worktrees there all the time; a brief never hard-codes counts from D:\, only paths that must or must not appear.
- **Cargo's `did not finalize incremental compilation session directory … Access is denied`** is environmental and harmless.
- **`dialoguer` needs a terminal**: interactive code sits at the edge; logic is tested through pure functions with explicit choices.
- **A Shell call can put a dialog on the user's desktop.** A test or probe that reaches `IFileOperation` runs it on a thread with a timeout, so a dialog fails the test rather than hanging it. Never run such a test red on purpose while the user is at the machine.
- **D: is a Dev Drive (ReFS); C: and `%TEMP%` are not.** On C:, a freshly written, unlocked tree can fail a rename or recycle for 1–3 s while a scanner holds it. A test there that expects success can flake; a test that expects `Locked` cannot.
- **Unelevated `handle.exe` is slow (about 141 s for a dump) and sees only some of the caller's own processes.** Elevated, it takes 1.4 s. Never judge the unlock flow's speed from an unelevated run.

## Concurrency

- Ceiling: **one** implementer. The crate is small and every item so far touches `main.rs` or `remove.rs`.
- Branch: `git switch -c feat/<slug>` in the main checkout (no worktree needed at ceiling one).
- Context: read the status bar's figure at every landing (`jq .context_window.used_percentage <scratchpad>/statusline.json`); past 40% stop refilling, per the user-level `nextup`.

## Docs map

| What changed | Owning docs |
|---|---|
| CLI flag, subcommand, output or behaviour a user sees | `README.md` usage and safety sections, `CHANGELOG.md` |
| A term used in a project-specific sense | `docs/README.md` glossary |
| JSON report shape | `README.md` (the `--json` section), since agents read it |
| A design decision that changes the approved design | `docs/plans/v1-design.md` |
| An item finished | tick it in `docs/plans/MAIN.md`; brief file to `docs/plans/archive/` |

## Landing

- A feature item (multi-file, from a brief): commit on `feat/<slug>` (the prek hook runs fmt, clippy and tests), `git push -u origin feat/<slug>`, `gh pr create` with the agent-authored marker line as the body's first line, then `gh pr merge --squash --delete-branch`, `git switch main`, `git pull --ff-only`. Merge PRs yourself as you go.
- A small change (docs, plan, config, one-line fix): commit on `main` and `git push`. A session running from a worktree offers `/ship` instead of pushing (user-level `nextup`, "A worktree session offers a ship instead of a push").
- Commit messages: ≤4-char type tag, imperative, ≤72-char subject, the session's attribution trailers.
