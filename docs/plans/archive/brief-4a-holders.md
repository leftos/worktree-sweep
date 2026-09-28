# Brief 4a: `src/holders.rs`, unelevated lock-holder detection

Part of the agent path, see [agent-path.md](../../agent-path.md) (read all of it: the measurements, the rulings and "Settled from the code and the rulings"). This brief builds only the detection module and its tests. The `remove` command that uses it is brief 4b; do not add the subcommand here.

## Tree and branch

- Root: `D:\worktree-sweep` (the main checkout). First: `git switch -c feat/holders` from an up-to-date `main`.
- Files: `Cargo.toml` (windows features), `src/holders.rs` (new), `src/lib.rs` (module list), `src/unlock.rs` (only the shared path comparison, see step 1), `tests/integration/` (a new `holders.rs` mod, registered in `tests/integration/main.rs`).

## Measured facts (from the code on `main`, 2026-09-27)

- `unlock::matches_locked_path(name: &Path, paths: &[PathBuf]) -> bool` (src/unlock.rs:407) goes through a private `comparable` (:418), which unifies `/` to `\`, strips `\\?\`, trims a trailing `\`, lowercases, and requires a `\` boundary. It does not strip `\??\`.
- `unlock::process_table() -> Result<HashMap<u32, ProcessEntry { parent, exe }>>` (:648) uses Toolhelp. It has no start time.
- `unlock::stop_process(pid, wait)` (:621).
- `windows = "0.62.2"` already has `Win32_Foundation`, `Win32_Storage_FileSystem`, `Win32_System_Threading`, `Win32_System_Diagnostics_ToolHelp` and others. Missing, and probably needed: `Wdk_System_Threading` (`NtQueryInformationProcess`), `Win32_System_Diagnostics_Debug` (`ReadProcessMemory`), `Win32_Security` (token user), and for `FileProcessIdsUsingFileInformation`, `Wdk_Storage_FileSystem`, `Win32_System_IO`, and possibly `Wdk_Foundation`. Add the minimal set that builds.
- Reference implementation, untracked, measured working: `.tmp/lockprobe/src/main.rs`: `census` :378, `token_sid` :352, `read_mem` :411, `peb_cwd` :422-451 (x64: PEB+0x20 → ProcessParameters; `CurrentDirectory.DosPath` length +0x38, buffer +0x40; WOW64: PEB32+0x10, then +0x24/+0x28), `query_buffer` :632, `process_handles` :667 (info class 51, 40-byte entries: value@0, access@24, type@28), `file_type_index` :685, `handle_scan` :709, `pids_using` :805. `.tmp/lockprobe/src/bench.rs` has the 4-worker naming with the `GetFileType == FILE_TYPE_DISK` filter (:91) and the 200 ms timeout (:64, :114). The probe uses `expect`, `panic`, `mem::forget` and `#![allow(clippy::all)]`; the crate denies all of them. A stuck worker is abandoned by dropping its `JoinHandle` (which detaches the thread), never with `mem::forget`.
- The command line is `RTL_USER_PROCESS_PARAMETERS.CommandLine` at ProcessParameters+0x70 on x64 (a `UNICODE_STRING`, as `CurrentDirectory.DosPath` is). Verify the offset against the `windows` crate's struct or a live read of a known process before relying on it. For a WOW64 target, a missing command line is acceptable; report it as `None`.
- Lints are strict: pedantic, no `unwrap`/`expect`/`panic`/print/`exit`/`mem_forget`. `unsafe` blocks are allowed (src/unlock.rs and src/recycle.rs use them); keep each one small with a `// SAFETY:` comment, as those files do. Tests return `anyhow::Result<()>` and assert with `anyhow::ensure!`. Fixture helpers go through `git::clear_repo_env`.

## Step 1: shared path comparison strips `\??\`

Make `comparable` strip a leading `\??\` as well as `\\?\` (and `\\?\UNC\` → `\\`). Keep `matches_locked_path` pub, and have holders.rs reuse it.

Proving: new unit tests in unlock.rs: `\??\D:\a\b` matches `D:\a`; `\\?\UNC\srv\share\x` matches `\\srv\share`; `D:\ab` does not match `D:\a` (the boundary). `cargo test --lib unlock`.

## Step 2: `src/holders.rs`

Public API. Small shape changes are yours to make; record each in RULINGS.

```rust
#[derive(Debug, Clone, Serialize)]
pub struct Holder { pub pid: u32, pub exe: String, pub image: Option<PathBuf>, pub started: u64 /* creation FILETIME */, pub command_line: Option<String>, pub holds: Vec<Hold> }

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Hold { CurrentFolder { path: PathBuf }, OpenHandle { path: PathBuf } }

#[derive(Debug, Clone, Serialize)]
pub struct MayHold { pub pid: u32, pub exe: String, pub why: MayHoldWhy }   // why: unnamed_handle | cannot_open

#[derive(Debug, Default, Serialize)]
pub struct HolderReport { pub holders: Vec<Holder>, pub may_hold: Vec<MayHold> }

pub fn find_holders(folder: &Path, exclude: &[u32]) -> anyhow::Result<HolderReport>;
pub fn stoppable<'a>(report: &'a HolderReport) -> Vec<&'a Holder>;          // pure
pub fn ancestors(pid: u32, table: &ProcessTimes) -> Vec<u32>;               // pure over a table
pub fn process_times() -> anyhow::Result<ProcessTimes>;                     // pid -> (parent, exe, started)
pub fn still_same(holder: &Holder) -> bool;                                 // re-check before a stop
```

What `find_holders` does, in this order (the "Recommended route" in agent-path.md):

1. Canonicalize `folder` with `std::fs::canonicalize` (this expands 8.3 names) and strip the verbatim prefix for comparison.
2. Census: every process whose token user is the current user (Toolhelp plus the token SID), minus `exclude`. A process that cannot be opened is skipped here, and step 5 covers it.
3. For each process: read the PEB cwd, normalize it with `GetLongPathNameW` (keep the raw string if that fails), and if it is at or under the folder (`matches_locked_path`), add `Hold::CurrentFolder`. Also read the image path (`QueryFullProcessImageNameW`), the creation time (`GetProcessTimes`) and the command line.
4. Per-process handles: duplicate the File-type handles, keep only those with `GetFileType == FILE_TYPE_DISK`, and name them with `GetFinalPathNameByHandleW` on 4 worker threads, allowing 200 ms per name. A match gives `Hold::OpenHandle`. A disk handle whose name lookup times out or fails gives a `MayHold` with `why: unnamed_handle` for that process, once per process. Abandon a stuck worker and start a new one.
5. `FileProcessIdsUsingFileInformation` on the folder root only. Each PID it returns that is neither in `holders` nor in `exclude`, and that step 2 could not open, becomes a `MayHold` with `why: cannot_open` (exe from Toolhelp).

Only processes with at least one `Hold` go into `holders`. The whole call should take about a second on this machine. Do not add Restart Manager, the module list or the system handle table (measured slower, see the table).

`stoppable` (pure) returns the holders with at least one `Hold` whose process is on the allowlist:

| Matches | Does not match |
|---|---|
| `rust-analyzer.exe`, `rust-analyzer-x86_64-pc-windows-msvc.exe` (`rust-analyzer*.exe`) | `rust-analyzer-helper.txt` (not an exe) |
| `rust-analyzer-proc-macro-srv.exe` | `code.exe`, `devenv.exe`, `pwsh.exe`, `bash.exe`, `node.exe` |
| `cargo.exe`, `MSBuild.exe`, `VBCSCompiler.exe` (case-insensitive) | `cargo-watch.exe`, `rustc.exe` |
| `dotnet.exe` whose command line contains `MSBuild.dll` and `/nodemode` (or `-nodemode`) | `dotnet.exe` with `MSBuild.dll` but no `nodemode` (a foreground build) |
| `dotnet.exe` whose command line contains `VBCSCompiler.dll` | `dotnet.exe` with no command line read (`None`) |
| | any holder listed only in `may_hold` |

`ancestors` (pure) walks parent links from `pid` and returns the chain, `pid` first. It follows a parent only when that parent's `started` is earlier than its child's, so a reused PID ends the chain. It stops at a missing entry or a cycle.

`still_same` returns true when the process with `holder.pid` still exists with the same `started` and the same image.

Proving (unit tests in holders.rs, pure, with no processes):
- the `stoppable` table above, one test per row;
- `ancestors`: a three-deep chain; a parent started after its child ends the chain; a cycle ends it; a missing parent ends it.

## Step 3: integration tests against real processes

In `tests/integration/holders.rs`, each test uses a `tempfile` folder, spawns `pwsh -NoProfile -Command ...` children, and always kills them (a guard struct whose `Drop` kills and waits):
- `cwd_holder_is_found`: a pwsh child started with `current_dir` set to a subfolder of the temp folder. `find_holders(temp root, &[])` lists its PID with `Hold::CurrentFolder`. Wait for the child to be ready (it writes a ready file) before scanning.
- `share_none_handle_is_found`: a pwsh child opens a file under the folder with `[IO.File]::Open(path, 'Open', 'ReadWrite', 'None')` and sleeps. It is listed with `Hold::OpenHandle` naming that file.
- `short_name_folder_matches`: the scan is given the 8.3 form of the temp folder (take it from `GetShortPathNameW`; skip the test if it has none), and still finds the cwd holder.
- `excluded_pid_is_not_listed`: the same as the first test, with the child's PID in `exclude`.
- `no_holders_in_idle_folder`: a fresh temp folder with no children has an empty `holders` list, and no `may_hold` entry from this test's own process.
- `still_same_detects_exit`: `still_same` is true for a live child and false once it has been killed and waited on.

Each of the first two is red until step 2 exists. Run them red first (they cannot build before step 2, and a compile failure counts), then green.

Proving: `cargo test --test integration holders`.

## Gates (all green before reporting `done`)

From the repo root, each wrapped as `cmd > .tmp/<name>.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/<name>.log -Tail 20; "rc=$rc"`, with test runs at BelowNormal (`(Get-Process -Id $PID).PriorityClass = 'BelowNormal'` in the same call):

- `cargo fmt --all -- --check`
- `cargo clippy --all-targets --all-features -- -D warnings`
- `cargo test`, and once more with `$env:GIT_INDEX_FILE='.git/index.lock'; $env:GIT_DIR='D:\nonexistent'` set in the same pwsh call
- `cargo deny check` (Cargo.toml changes)

## Constraints

- Read-only against D:\. Nothing in this brief stops a process except the test's own children. Do not call `stop_process` from holders.rs.
- Do not commit. Report the files changed, the gate results, RULINGS (every API shape change or offset decision), and SURFACES (the user-visible or doc-visible things that changed: expected none in this brief besides the new dependency features).
