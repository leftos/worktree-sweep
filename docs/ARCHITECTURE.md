# worktree-sweep — architecture

`worktree-sweep` is a single Rust crate for Windows: a library (`src/lib.rs`) with a thin binary (`src/main.rs`) on top. The library scans a root for stale git worktrees and orphan folders, removes picks recoverably, escalates once per run through `sudo` and Sysinternals `handle.exe` to clear file locks, and offers an unattended `remove <PATH> --json` path for agents. It is being rewritten in C# with a WPF window ([`plans/csharp-rewrite.md`](plans/csharp-rewrite.md)).

The rule that shapes it: the scan writes nothing, and the parts of the interactive front end (`src/tui/`) are pure state machines that never touch the terminal, disk or git. Terms used in a project sense (candidate, orphan, locker, holder, released, Review) are in the glossary in [`README.md`](README.md).

## Task Index

| Task | Files, in order | Deep doc |
|---|---|---|
| Add or change a scan signal (merge state, dirty, upstream, size) | `src/signals.rs` → `src/report.rs` (`RegisteredCandidate`, table, JSON) → `src/pick.rs` (`loss_text`) → `tests/integration/worktree_status.rs` or `merge_state.rs` | [`design.md`](design.md) |
| Change what counts as a container dir or an orphan | `src/discover.rs` → `src/lib.rs` (`scan`, `container_repo`) → `tests/integration/discovery.rs` | [`README.md`](README.md) (glossary) |
| Change the `git` calls the scan makes | `src/git.rs` → `tests/integration/git_env.rs` | [`design.md`](design.md) |
| Change the table or the `--json` report | `src/report.rs` → `README.md` (`JSON report`) → `CHANGELOG.md` | none |
| Change what removing a pick loses, or the Review questions | `src/pick.rs` (`loss_text`) → `src/tui/review.rs` → `src/remove.rs` (`Plan`, `Decision`) | [`plans/csharp-rewrite.md`](plans/csharp-rewrite.md) |
| Change how a pick is removed (recycle, permanent delete, prune, branch) | `src/remove.rs` → `src/recycle.rs` → `tests/integration/removal.rs` | [`design.md`](design.md) |
| Change the unlock flow (elevated `handle.exe`) | `src/unlock.rs` → `src/handle_csv.rs` → `src/main.rs` (`Command::Unlock`) | [`design.md`](design.md) |
| Change how lock holders are found or which ones may be stopped | `src/holders.rs` (`find_holders`, `stoppable`) → `src/agent.rs` → `tests/integration/holders.rs` | [`agent-path.md`](agent-path.md) |
| Change the agent `remove` command (statuses, refusals, exit codes, marker) | `src/agent.rs` → `src/lib.rs` (`resolve_one`, `RefusalReason`) → `src/main.rs` (`Command::Remove`) → `tests/integration/agent_remove.rs`, `released.rs` | [`agent-path.md`](agent-path.md) |
| Port a module to C#, or list the tool's external surface (flags, subcommands, JSON, exit codes) | `README.md` (usage, JSON, exit codes) → `src/main.rs` → `src/report.rs` → `src/agent.rs` → `src/unlock.rs` → `src/holders.rs` → `tests/integration/main.rs` | [`plans/csharp-rewrite.md`](plans/csharp-rewrite.md) |
| Add a CLI flag or subcommand | `src/main.rs` (`Cli`, `Command`) → `README.md` usage → `CHANGELOG.md` | none |

## Layers

One crate, `worktree-sweep` (`Cargo.toml`), with a library and a binary. Modules are declared in `src/lib.rs`; the dependencies below are the `use crate::` lines in each file.

- **`main`** (`src/main.rs`): the CLI (`clap`) and the interactive flow that still drives `dialoguer`. References the library only; the library never references it.
- **`lib`** (`src/lib.rs`): `scan` (read-only: discover, then read every candidate's signals in parallel) and `resolve_one` (one path to one registered worktree, or a `Refusal`). References `discover`, `git`, `signals`, `report`, `agent`.
- **`git`** (`src/git.rs`): owns running `git` as a child process with the repo-local env cleared and optional locks and fsmonitor off. References nothing in the crate.
- **`discover`** (`src/discover.rs`): repos, registered worktrees, container dirs and orphans, plus the path-comparison helpers (`path_key`, `strip_verbatim`, `is_link`). References `git`. Never follows a link or junction.
- **`signals`** (`src/signals.rs`): merge state, dirty counts, upstream, last activity, size, and `parallel_map`. References `discover`, `git`.
- **`report`** (`src/report.rs`): the `Report` and `Candidate` types, the table and the JSON form. References `agent` (for `Released`), `discover`, `signals`.
- **`pick`** (`src/pick.rs`): the checkbox picker and the loss text (`loss_text`, `loss_sentence`), `default_picks`. References `discover`, `report`, `signals`.
- **`tui`** (`src/tui/`): the picker's pure parts: `app` (state and `update`), `view` (ratatui renderer), `review` (the question sequence), `when` (local dates), `fixtures` (test-only). References `pick`, `remove`, `report`, `recycle`, `unlock`, `agent`. Not reachable from the binary except `tui::review`.
- **`remove`** (`src/remove.rs`): removing picks from already-answered `Decision`s: link, folder, registration, branch. Asks nothing. References `discover`, `git`, `recycle`, `report`, `signals`, `unlock`.
- **`recycle`** (`src/recycle.rs`): Recycle Bin capacity and moving a folder there through the Shell. References `remove` (`RemoveError`) and `report`.
- **`unlock`** (`src/unlock.rs`): the unelevated `offer` and the elevated `run_elevated`; also owns `matches_locked_path`. References `handle_csv`.
- **`handle_csv`** (`src/handle_csv.rs`): parses `handle.exe -nobanner -v` CSV. References nothing in the crate.
- **`holders`** (`src/holders.rs`): finds holders without elevation (PEB current folder, disk handles) and the stop allowlist. References `unlock` (for `matches_locked_path` and `process_table`).
- **`agent`** (`src/agent.rs`): the `remove <PATH> --json` flow and the released marker. References `holders`, `recycle`, `remove`, `report`, `signals`, `pick`, `unlock`, `discover` and `lib`.

The C# rewrite (`WorktreeSweep.slnx`) grows beside the crate, one module at a time:

- **`WorktreeSweep.Core`** (`src/WorktreeSweep.Core/`): the class library the ports land in, never referencing WPF. `Git/GitRunner` runs `git` with the same rules as `src/git.rs` (`RepoLocalEnvVars` cleared, `GIT_OPTIONAL_LOCKS=0`, `core.fsmonitor=false`).

Rules the docs or code state: the scan writes nothing and every git call clears the repo-local env (`git::clear_repo_env`); junctions are links and are never sized, walked or deleted through; one elevation per run, for every locked pick together (see `.claude/skills/worktree-sweep-nextup/SKILL.md`, "Rulings every brief carries").

## Integration Footguns

- **Change a field of `Candidate`, `RegisteredCandidate`, `OrphanCandidate` or a signal type** → also update the `--json` section of `README.md`, which agents read; `report::tests::write_json_carries_the_released_marker` and `no_commits_branch_shows_in_table_and_json` pin part of the shape.
- **Change the `unlock` subcommand's arguments in `src/main.rs`** → also change `unlock::sudo_argv`, which builds the elevated command line; `sudo_argv_passes_caller_pid_and_backslash_paths` pins it. `handle.exe` matches only backslash paths.
- **Change an exit code** → also change `agent::exit_code` and the codes in `agent-path.md` and `README.md`; `agent::tests::status_maps_to_exit_code` pins the mapping. The unlock side has its own codes (`unlock::EXIT_ALL_CLEAR`, `EXIT_SOME_LEFT`, `EXIT_NOTHING_DONE`) mapped by `unlock::outcome_for_exit`, pinned by `exit_code_maps_to_outcome`.
- **Change the released marker's file or fields** (`agent::MARKER_FILE`, `Released`) → the scan reads it through `released_marker` and `admin_dir` in `src/lib.rs`, and `tests/integration/released.rs` covers the read.
- **A test that runs `git`** must build its command through `git::clear_repo_env` (the fixture helper in `tests/integration/fixture.rs` does); `tests/integration/git_env.rs` pins that the runner ignores an inherited repo env.
- **`holders.rs` reads another process's PEB at fixed offsets**, guarded by a 64-bit `const` assertion at the top of the file; `tests/integration/holders.rs` exercises it against real child processes.
- **Add a way to stop a process** → it must pass `holders::stoppable` and the allowlist in `holders::allowlisted`; nothing else is stopped unelevated (`agent-path.md`, "Open decisions").

## Test locations

- Unit tests sit in a `#[cfg(test)] mod tests` at the bottom of each module: `agent`, `holders`, `handle_csv`, `pick`, `remove`, `recycle`, `signals`, `report`, `unlock`, and the `tui` files (`app`, `review`, `view`, `when`).
- `src/tui/fixtures.rs`: candidates and reports for the TUI tests, built without git.
- `tests/integration/` (one binary, `main.rs`, with real `git` in `tempfile` folders): `discovery`, `worktree_status`, `merge_state`, `size` pin the scan; `removal` pins `remove`; `holders` pins `find_holders`; `agent_remove` and `released` pin the agent path; `git_env` pins the git runner. Shared repo builders (`git`, `commit_file`, `add_worktree`, `make_junction`) live in `tests/integration/fixture.rs`.
- `tests/fixtures/`: sample `handle.exe` CSV files.
- `tests/WorktreeSweep.Tests/` (xUnit v3, the C# rewrite): `Fixture.cs` ports the Rust repo builders; `GitRunnerTests` and `GitEnvTests` pin the C# git runner. Tests that set process environment variables sit in the non-parallel "process environment" collection.
- Never run the removing mode against a real folder from an agent session; use `--list` / `--json` or `tempfile` fixtures (`CLAUDE.md`).

## Deep docs

- [`README.md`](README.md): glossary, start page.
- [`agent-path.md`](agent-path.md): the agent `remove` path, lock-kind measurements, rulings.
- [`design.md`](design.md): the approved v1 design, module list and test plan.
- [`plans/csharp-rewrite.md`](plans/csharp-rewrite.md): the C# rewrite with a WPF window, its rulings and items; the full-screen picker it replaces is archived in [`plans/archive/tui-picker.md`](plans/archive/tui-picker.md).
- [`plans/MAIN.md`](plans/MAIN.md): the generated snapshot of the plan in Linear (team WTS).
