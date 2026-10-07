# C# rewrite with a WPF window: design

The whole tool is rewritten in C# on .NET 10 with a WPF window for the interactive mode, in this repo on `main` beside the Rust crate, which is deleted once the C# version does everything it does. Linear project "C# rewrite" (team WTS); the owner's rulings are on WTS-4 and repeated under "Rulings".

## Rulings

- **Scope:** every module is ported: scan, git signals, `--list`, `--json`, recycle, removal, the unlock flow, `remove {path} --json` for agents, and a WPF window in place of the `dialoguer` picker. The ratatui picker (`src/tui/app.rs`, `view.rs`) is not ported; `review.rs`'s question sequence is.
- **Where:** this repo, on `main`, item by item; the Rust crate keeps working until the cutover item deletes it.
- **Runtime:** .NET 10 LTS, `net10.0-windows`, framework-dependent.
- **Launch:** from a terminal only. One console-subsystem exe (`OutputType` `Exe` with `UseWPF`): `--list`, `--json`, `remove` and `unlock` print to stdout and never open a window; the default mode opens the window. No double-click mode.
- **Git:** shell out to `git.exe` exactly as `src/git.rs` does: the 15 repo-local `GIT_*` variables removed, `GIT_OPTIONAL_LOCKS=0`, `-c core.fsmonitor=false`, stdin null, both streams read concurrently, `merge-tree --write-tree` into a scratch object directory with the repo's objects as an alternate. No LibGit2Sharp.
- **JSON:** both reports are redesigned, the scan report (`--json`) and the agent contract (`remove {path} --json` and its exit codes). Each shape is settled in its item's decision round; README and `docs/agent-path.md` are rewritten at the cutover.
- **Win32:** the CsWin32 source generator (`NativeMethods.txt`). An API its metadata lacks (some `Nt*` information classes, the PEB offsets) is declared by hand with `[LibraryImport]` in one file, with a comment naming why.
- **Window:** CommunityToolkit.Mvvm view models, testable without a window.
- **Command line:** System.CommandLine 2.0; usage errors exit 2 as clap's do.
- **Tests:** xUnit v3.
- **Install:** `tools/install.ps1` runs `dotnet publish -c Release` into `%LOCALAPPDATA%\worktree-sweep\` and adds that folder to the user PATH once. Until the cutover the C# exe is not installed; tests run the built exe from its output folder.
- **Unlock step:** runs in the terminal the tool was launched from, with plain numbered line prompts (`[Y/n]`, `1) Stop process  2) Close its handles  3) Skip  4) Done [1]:`) read by a pure parser. While it runs the window shows "Finish the unlock step in the terminal" and takes no input; then it shows the retry. `sudo` must be in Inline mode, as today.
- **After exit:** nothing is printed; the Results view is the only record.
- **Merge state differs from Rust on purpose:**
  - A branch with no history in common with a default branch is "not contained", and no `merge-tree` runs.
  - A git failure against one default branch is traced while another default still answers.
  - An unexpected `merge-tree` exit is a signal error, not a silent "not contained" (WTS-25).
  - Each worktree uses one scratch object folder for all its defaults (WTS-27).
- **Scan JSON (C4):** snake_case keys (`JsonNamingPolicy.SnakeCaseLower`); one flat `candidates` array tagged by `"kind": "registered"|"orphan"`; every timestamp an ISO 8601 UTC string whose key drops the `_unix` suffix (`last_activity`, `size.last_write`, `released.released_at`).

Carried over from the full-screen picker's design (`archive/tui-picker.md`, "Rulings from the review"): every question is asked up front in Review (loss, then permanent delete, then branch, per pick in list order), and a branch answer applies only if its removal succeeds.

A final "Remove N items?" confirmation has Cancel as the default. Also carried over: risk tints on MERGE, DIRTY and UPSTREAM; the scan runs inside the window behind a progress indicator, with Empty and Scan failed states; during removal a Cancel button stops after the current item, marks the rest `skipped (cancelled)` and skips the unlock step; no filter, sort or extra keys in the first version.

## Invariants the port keeps

Every brief quotes the ones its module enforces, from the Rust source it ports:

- **Read-only against the real `D:\`:** tests use `tempfile`-style fixtures under the repo's `.tmp\` (a Dev Drive) or `%TEMP%`; the removing mode never runs against a real folder from an agent session.
- **The scan writes nothing** (the git rules above).
- **Junctions are links, never trees:** never sized, walked, recycled or deleted through; removed with a directory removal on the link after re-checking it is still a reparse point. `EnumerationOptions { AttributesToSkip = 0 }`, since the default skips hidden and system entries.
- **Recycle Bin capacity is read before recycling** (`HKCU\...\BitBucket\Volume\{GUID}` `MaxCapacity`, `NukeOnDelete`); an item over it asks for a permanent delete.
- **Recycling goes through `IFileOperation`** on an STA thread with the flags of `src/recycle.rs:252-259`; locked HRESULTs and the 250/500/1000 ms retries as there.
- **One elevation per run:** removal is two passes; the locked picks go to a single unlock offer, one `sudo`, one `handle.exe` dump filtered by every locked path, then one retry pass.
- **The released marker keeps its file shape** (`worktree-sweep-released.json` in the worktree's `.git/worktrees/{id}` folder: `released_at`, `reason`, `holders`): it is state already on disk, and the Rust and C# tools read each other's during coexistence.
- **Output:** UTF-8, `\n` line ends (`Console.Out.NewLine`, `JsonSerializerOptions` with relaxed escaping), children sorted with `StringComparer.Ordinal`, path keys case-folded.
- `--list` keeps the Rust table's columns and order (`src/report.rs:371-383`); only the JSON is redesigned.

## Layout

| Path | What |
|---|---|
| `WorktreeSweep.slnx` | The solution. |
| `src/WorktreeSweep.Core/` | Class library: git runner, discovery, signals, report, recycle, removal, holders, unlock, agent removal. No WPF reference. |
| `src/WorktreeSweep/` | The exe (`AssemblyName` `worktree-sweep`, `UseWPF`): `Program.cs` (command line), the window, its view models. |
| `tests/WorktreeSweep.Tests/` | xUnit v3; fixtures port `tests/integration/fixture.rs`; `tests/fixtures/handle*.csv` are reused as they are. |
| `.editorconfig`, `.csharpierrc`, `Directory.Build.props`, `.config/dotnet-tools.json` | Copied from the user-level C# conventions (`language-conventions/csharp/`). |

Cargo ignores these folders (`tests/WorktreeSweep.Tests/` has no `main.rs`), and the layout is final once the `.rs` files go.

## Gates

Rust gates stay until the cutover. C# gates, from the repo root: `dotnet build WorktreeSweep.slnx -c Release` (warnings as errors), `dotnet test WorktreeSweep.slnx -c Release --no-build`, `dotnet csharpier check .`, `dotnet format style WorktreeSweep.slnx --verify-no-changes --severity info`, `dotnet format analyzers WorktreeSweep.slnx --verify-no-changes --severity info`. The prek hooks gain the C# conventions' fixer hooks in the scaffold item.

## Items

Each lands green and testable; leaf first. Rust files named are the ones the item ports, and their tests come along.

| # | Item | Ports | Lands |
|---|---|---|---|
| C1 | Scaffold, conventions, git runner | `git.rs`, `tests/integration/fixture.rs`, `git_env.rs` | solution, config, prek hooks, `GitRunner`, fixtures; `dotnet test` green |
| C2 | Discovery | `discover.rs`, `discovery.rs` tests | repos, worktree records, containers, orphans, junction rule |
| C3 | Signals | `signals.rs`, `merge_state.rs`, `worktree_status.rs`, `size.rs` | merge state, dirty, upstream, activity, size |
| C4 | Report, table and scan JSON | `report.rs`, the released marker reader | `--list` table, the redesigned scan JSON (decision round) |
| C5 | Scan and command line | `lib.rs` scan, `main.rs` modes, `released.rs` tests | `worktree-sweep [ROOT] --list/--json` runs |
| C6 | Process primitives | `unlock.rs` process table, locked-path matching | ToolHelp table, parent chain, `TerminateProcess` |
| C7 | Holders: process census | `holders.rs` PEB and cwd half | cwd/image holders, allowlist |
| C8 | Holders: handles | `holders.rs` handle half | handle naming with abandonable workers, `FileProcessIdsUsingFileInformation` |
| C9 | Recycle | `recycle.rs` | Bin capacity, `IFileOperation` on STA with retries |
| C10 | Delete core | `remove.rs` delete and link removal | link-safe permanent delete, read-only clearing, Locked classification |
| C11 | Plan, sweep, review | `remove.rs` rest, `pick.rs` loss text, `tui/review.rs` | two-pass sweep with progress and unlock callbacks, `Review` |
| C12 | Unlock flow | `unlock.rs` offer and elevated loop, `handle_csv.rs` | hidden `unlock` subcommand, numbered prompts; manual elevated check by the owner |
| C13 | Agent removal | `agent.rs`, `lib.rs::resolve_one`, `agent_remove.rs` | `remove {path} --json` with the redesigned report (decision round) |
| C14 | Window view models | `tui/app.rs` behaviour, not its code | list, detail, Review, Removing, Results state, tested headless |
| C15 | Window and wiring | `main.rs` interactive path | XAML, scan and removal workers, unlock hand-off, Cancel |
| C16 | Cutover | — | `tools/install.ps1`, README, `docs/agent-path.md`, `docs/design.md`, ARCHITECTURE, glossary, nextup profile and CLAUDE.md gates; the crate, `Cargo.*`, `deny.toml` and Rust hooks deleted |

C2–C5 are a chain, as are C6–C8 and C9–C11; the chains meet at C11–C13, and C14–C15 need C11. Each chain runs in table order; up to three implementers run items from different chains side by side, each in its own worktree.

## Open decisions, asked in the item's decision round

- C13: the agent report's shape and exit codes.
- C15: the window's layout (a `DataGrid` with a checkbox column above a detail pane is the default), and whether it remembers its size.
