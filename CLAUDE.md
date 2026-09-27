# CLAUDE.md

`worktree-sweep` is a Windows CLI (Rust) that scans a root folder for stale git worktrees and orphan worktree folders, lets the user pick which to remove, deletes them recoverably, and escalates once per run with `sudo` + Sysinternals `handle.exe` to find and stop whatever holds a locked folder.

- Plan index: `docs/plans/MAIN.md`; design: `docs/plans/v1-design.md`; glossary: `docs/README.md`. `/nextup` works from the index with the `worktree-sweep-nextup` profile in `.claude/skills/`, which also holds the rulings, gates, traps and landing path.
- Commands: `cargo run -- D:\ --list` (table), `cargo run -- D:\ --json` (report), `cargo test`, `cargo clippy --all-targets --all-features -- -D warnings`, `cargo deny check`.
- Never run the interactive (removing) mode against a real folder from an agent session; use `--list` / `--json` there and `tempfile` fixtures in tests.
