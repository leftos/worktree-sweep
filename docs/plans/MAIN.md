# worktree-sweep plan
<!-- plan-doc-hygiene: 2026-09-27 7e6ad89 -->

Design: [v1-design.md](./v1-design.md). Execution ledger (untracked): `.tmp/plan-ledger.md`.

## Current focus: v1

- [x] Scaffold: crate, lints, cargo-deny, prek hooks, docs skeleton
- [x] Public GitHub repo `leftos/worktree-sweep` (user, 2026-09-27)
- [x] Brief 1: discovery (repos, registered worktrees, container dirs, orphans) and signals (merge state, dirty, unpushed, last activity, size), with fixture-repo tests
- [x] Brief 2: report table and `--json`, picker with second confirmation, removal (Recycle Bin capacity check, recycle, permanent delete, prune, branch delete)

### Wave 1: unlock — `src/unlock.rs`, `src/handle_csv.rs`, `src/main.rs` — gate: `code-review`

- [ ] Brief 3 — see [brief-3-unlock.md](./brief-3-unlock.md), written and ready to dispatch: `handle.exe` CSV parser, elevated `unlock` subcommand, retry-after-unlock in removal. One elevation per run (user, 2026-09-27): locked picks are collected, one `sudo` session with a single unfiltered `handle.exe` dump handles all of them, then every locked pick is retried. Acceptance: the profile's gates (command); the elevated end-to-end lock test (human, the user runs it with `!`)

### Wave 2: release readiness — `README.md`, `CHANGELOG.md` — gate: human check

- [ ] Verify against real D:\ (`--list`, `--json`) and the manual lock test from the design
- [ ] README usage and safety section; CHANGELOG entry

## Backlog

(empty)
