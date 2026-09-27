# worktree-sweep plan

Design: [v1-design.md](./v1-design.md). Execution ledger (untracked): `.tmp/plan-ledger.md`.

## Current focus: v1

- [x] Scaffold: crate, lints, cargo-deny, prek hooks, docs skeleton
- [x] Public GitHub repo `leftos/worktree-sweep` (user, 2026-09-27)
- Workflow (user, 2026-09-27): commit and push as each piece lands, one branch and PR per brief, merged by the agent once the gate is green
- [x] Brief 1: discovery (repos, registered worktrees, container dirs, orphans) and signals (merge state, dirty, unpushed, last activity, size), with fixture-repo tests
- [ ] Brief 2: report table and `--json`, picker with second confirmation, removal (Recycle Bin capacity check, recycle, permanent delete, prune, branch delete)
- [ ] Brief 3: `handle.exe` CSV parser, elevated `unlock` subcommand, retry-after-unlock in removal
- [ ] Verify against real D:\ (`--list`, `--json`) and the manual lock test from the design
- [ ] README usage and safety section; CHANGELOG entry

## Backlog

(empty)
