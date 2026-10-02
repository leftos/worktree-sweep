# CLAUDE.md

`worktree-sweep` is a Windows CLI (Rust) that scans a root folder for stale git worktrees and orphan worktree folders, lets the user pick which to remove, deletes them recoverably, and escalates once per run with `sudo` + Sysinternals `handle.exe` to find and stop whatever holds a locked folder.

- The plan lives in Linear: every task is a Linear issue in team WTS, grouped into projects worked in order. `docs/plans/MAIN.md` is a generated snapshot of it, never edited by hand: change Linear, then regenerate it. A steer or finding mid-task gets an **add** first, before any reply in prose. The operations (**add**, **land**, **triage** and the rest) are in `~/.claude/docs/plan-operations.md`. A design for open work stays in `docs/plans/<name>.md`, linked from its issue or project.
- Design: `docs/design.md`; glossary: `docs/README.md`. `/nextup` works from Linear with the `worktree-sweep-nextup` profile in `.claude/skills/`, which also holds the rulings, gates, traps and landing path.
- Commands: `cargo run -- D:\ --list` (table), `cargo run -- D:\ --json` (report), `cargo test`, `cargo clippy --all-targets --all-features -- -D warnings`, `cargo deny check`.
- Never run the interactive (removing) mode against a real folder from an agent session; use `--list` / `--json` there and `tempfile` fixtures in tests.
