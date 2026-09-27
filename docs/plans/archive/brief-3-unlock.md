# Brief 3: handle.exe parser and the elevated unlock flow

Worktree root: `D:\worktree-sweep`, branch `feat/unlock` (briefs 1 and 2 merged on main). Read `docs/plans/v1-design.md` (`unlock.rs`, `handle_csv.rs`) and the existing `src/unlock.rs` stub brief 2 left (`unlock::offer(paths) -> Result<UnlockOutcome>`, called by removal on `RemoveError::Locked`, which retries once on `Unlocked`). Do not edit docs, lints, deny.toml or hook config.

## Measured facts (2026-09-27, handle.exe v5.0, this machine)

Corrected during implementation: the unfiltered dump uses a 7-column layout, and an elevated dump takes 1.4 s. See `v1-design.md` § Tooling.

- **Unelevated runs see nothing.** They print `No matching handles found.` even for a folder the caller's own shell has open. Elevation is required.
- **Output of an elevated `handle -nobanner -accepteula -v 'D:\worktree-sweep\.tmp\lockprobe'`**, with a pwsh whose cwd was that folder and which held `held.txt` open. It is saved verbatim at `tests/fixtures/handle-sample.csv` (committed):
  ```
  Process,PID,Type,Handle,Name
  pwsh.exe,64336,File,0x00000054,D:\worktree-sweep\.tmp\lockprobe 
  pwsh.exe,64336,File,0x00000748,D:\worktree-sweep\.tmp\lockprobe\held.txt 
  ```
  - Every Name has a trailing space.
  - The first row is the shell's current directory, held as a `File` handle on the folder itself.
  - Names are not quoted, so a path containing a comma runs past the 4th comma. The Name is everything after the 4th comma, trimmed.
- **No match:** stdout is `No matching handles found.` and the exit code is 1. That is the empty answer, not an error.
- **Name matching is by substring on backslash paths.** A forward-slash argument matches nothing, so always pass the path with `\` separators and no `\\?\` prefix.
- **Output encoding:** decode stdout lossily (`String::from_utf8_lossy`) and compare paths case-insensitively.

## One elevation per run (user requirement, 2026-09-27)

The run prompts UAC at most once, however many picks are locked:
- **Removal is two passes.** Pass 1 removes every confirmed pick unelevated and collects the ones that hit `RemoveError::Locked`. If any did, `unlock::offer(all_locked_paths)` is called **once**; on `Unlocked` or `PartlyUnlocked` every locked pick is retried once, and a pick still locked after that is reported as failed with its path. Brief 2 already implements this orchestration and its test against a stub `offer(&[PathBuf]) -> Result<UnlockOutcome>`; you replace the stub's body.
- **The elevated session makes one unfiltered handle.exe dump for all paths.** It runs `handle.exe -nobanner -accepteula -v` with **no name argument** (dumps every file handle on the system) and keeps rows whose Name equals a locked path or starts with `<path>\`. Compare case-insensitively on backslash paths, and never match a mere prefix: `D:\a.wt\x` must not match `D:\a.wt\xy`. Test `row_filter_matches_path_and_children_only`.
- **The session loops until done.** After acting, it re-dumps and re-offers any lockers left, until none remain or the user picks **Done** (a Select option on each round). Respawning processes are therefore handled without another prompt.
- **Exit codes and outcomes.** Exit code 0 = all clear, 3 = some lockers left after at least one was stopped or had handles closed, 4 = lockers left and nothing was acted on (every locker skipped, or Done on the first round), 1 = error. The unelevated side maps 0 → `Unlocked`, 3 → `PartlyUnlocked` (still triggers the retry pass), 4 → `StillLocked` (no retry), anything else → an error with context.

## Step 1: `src/handle_csv.rs` (pure)

`parse(stdout: &str) -> Result<Vec<HandleRow>>` returns `HandleRow { process: String, pid: u32, kind: String, handle: u64 /* from 0x hex */, name: PathBuf }`. The rules:
- Skip the header, blank lines, and the `No matching handles found.` line.
- A line with fewer than 5 fields, or a bad pid or handle, is skipped with a `warn!`. One bad line never fails the whole parse.
- `group_by_process(rows) -> Vec<Locker { process, pid, handles: Vec<(u64, PathBuf)> }>`, sorted by pid.

Tests: the fixture; `No matching handles found.`; empty input; a malformed line in the middle; a Name containing a comma; CRLF line endings.

## Step 2: the elevated side (`worktree-sweep unlock <PATH>...`, replaces the hidden stub)

The hidden `Unlock { paths }` subcommand already exists in `src/main.rs` and bails with "not implemented"; add the hidden `--caller-pid <n>` flag to it and route it to the new code.

1. Run `handle.exe -nobanner -accepteula -v` once, with no name argument, and keep the rows the path filter above selects for any of the paths. `handle.exe` not on PATH is an actionable error: install with `winget install Microsoft.Sysinternals.Handle`.
2. Parse, filter and group the output. Drop our own PID and our parent chain up to and including `sudo.exe`: walk parents with the ToolHelp snapshot (`CreateToolhelp32Snapshot`, `Process32FirstW/NextW`), which is already a `windows` feature.
3. Mark the process that launched the unelevated tool, i.e. the parent of the unelevated worktree-sweep. The unelevated side passes it as `--caller-pid <n>`, a hidden flag. Stopping it would close the user's own shell, so its prompt says so, and "stop" is not its default.
4. For each locker, `dialoguer::Select` offers:
   - **Stop process** (default, except for the caller): `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess(h, 1)`, then wait up to 5 s for exit.
   - **Close its handles in this folder**: first a `Confirm` (default no) warning that the program may crash or lose data; then `handle.exe -nobanner -c <hex> -p <pid> -y` per handle.
   - **Skip**.
5. Re-dump and loop as described under "One elevation per run", then exit with the code that section defines. Print a one-line summary for each process.

## Step 3: the unelevated side (`unlock::offer`)

1. Print each locked path. `offer(&[PathBuf])` receives only the paths; keep that signature (`remove::sweep` and its `FakeSweeper` tests call it), and do not add the first locked file.
2. `Confirm` (default yes): "Run an elevated scan with sudo to find what holds them?"
3. Run `sudo <current_exe> unlock --caller-pid <our parent pid> <paths...>` with inherited stdio. Inline sudo shares the console, so the elevated prompts reach the user.
4. Map its exit code as "Exit codes and outcomes" above says.
5. If `sudo.exe` is missing, or it reports sudo is disabled (non-zero exit before our process starts; check `sudo config` output only if needed), return `Skipped`. Print the exact elevated command to run by hand in an admin terminal.

Keep the process-launch edges thin and the decision logic pure, so tests reach it without UAC: map an exit code to an outcome; choose the default action given `is_caller`; build the sudo argv.

## Tests

- **handle_csv:** the tests listed in Step 1.
- **Decision logic:**
  - `exit_code_maps_to_outcome`
  - `caller_process_is_not_stopped_by_default`
  - `sudo_argv_passes_caller_pid_and_backslash_paths` (a forward-slash input path comes out with backslashes)
  - `own_parent_chain_is_excluded` (a pure function over a fake pid→parent map)
- **Process termination,** without elevation: `stop_process_terminates_a_child`. Spawn `pwsh -NoProfile -Command Start-Sleep 60`, stop it with the Step 2 stop function, assert it exits.
- **Manual, reported and not automated:** with a hidden pwsh holding a file open in a folder under `.tmp\`, run `cargo run -- unlock <that folder>` from an **elevated** context only if you have one. You do not; the user will run the end-to-end check. So instead dry-run the parse path against the fixture, and say in the report that elevation was not exercised.

## Proving commands (from `D:\worktree-sweep`, below-normal priority)

1. `cargo fmt --all -- --check`
2. `cargo clippy --all-targets --all-features -- -D warnings`
3. `cargo test`, plus one run with `$env:GIT_INDEX_FILE='.git/index.lock'; $env:GIT_DIR='D:\nonexistent'` set in the same pwsh call.
4. `cargo run --quiet -- unlock D:\worktree-sweep\.tmp\nonexistent-probe` run **unelevated**. It must fail cleanly or report no handles, without a panic. Report its output.

Report: status, files, RULINGS, proving results, CALLS.
