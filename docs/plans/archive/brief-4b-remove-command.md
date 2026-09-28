# Brief 4b: `worktree-sweep remove <PATH> --json`

Part of the agent path, see [agent-path.md](../../agent-path.md): read every ruling there, numbered and "Settled from the code and the rulings". This brief adds the non-interactive command and writes the "released" marker. Brief 4c makes the interactive sweep read the marker; do not touch `report::ordered` or `pick` here.

## Tree and branch

- Root: `D:\worktree-sweep` (the main checkout). First: `git switch -c feat/remove-command` from an up-to-date `main` (which carries brief 4a's `src/holders.rs`).
- Files: `src/main.rs` (subcommand), `src/agent.rs` (new: the command's logic and its JSON types), `src/lib.rs` (module list; a single-path resolver, see step 1), `src/discover.rs` (make `list_worktrees` pub if you use it), `tests/integration/` (a new `agent_remove.rs` mod).

## Measured facts (code on `main`, 2026-09-27; re-check the holders API after 4a lands)

- `main.rs:15-31` `struct Cli` is clap derive with `args_conflicts_with_subcommands = true`, so `remove` carries its own `--json`. `enum Command` (:33-50) has only the hidden `Unlock`. `main() -> Result<ExitCode>`. Output goes through a locked stdout writer (:75-76).
- Resolving: `discover::parse_worktree_porcelain` (pub, :162), `discover::list_worktrees` (private, :376), `discover::path_key` (:204), `discover::read_gitdir_file(worktree) -> Option<PathBuf>` (:230), `discover::is_link` (:257), `signals::default_branches(repo)` (:141), `signals::worktree_signals(&DefaultBranches, &WorktreeRecord) -> WorktreeSignals` (:175), `report::RegisteredCandidate` (report.rs:65), built as in lib.rs:70-78. `git rev-parse --path-format=absolute --git-common-dir` is already used at signals.rs:344.
- Loss: `pick::loss_sentence(&Candidate, root) -> Option<(String, String)>` (pick.rs:106), whose text comes from `registered_loss` (:116). It fires for dirty, Unmerged, NoCommits, an uncontained Detached, unpushed and a git lock.
- Removal: `recycle::bin_capacity(path)` (:86), `recycle::recycle_decision(size, cap) -> Decision { Recycle | AskPermanent(String) }` (:59), `recycle::recycle(path) -> Result<(), RemoveError>` (:198, which needs `ComApartment::init()` (:168) on the same thread and already retries a lock at 250/500/1000 ms), `remove::git_unlock` (:378), `remove::prune(repo)` (:391), `remove::branch_offer` (:448), `remove::delete_branch(repo, branch, force)` (:488). `RemoveError` is `Locked { path, first_locked_file } | Other`. Do not use `remove::plan`, `after_removed`, `remove_picks` or `unlock::offer`, because they prompt.
- Holders (brief 4a, landed as #7): `holders::find_holders(folder: &Path, exclude: &[u32]) -> Result<HolderReport { holders, may_hold }>` (src/holders.rs:161), `stoppable(&HolderReport) -> Vec<&Holder>` (:224), `ancestors(pid, &ProcessTimes) -> Vec<u32>` (:258), `process_times() -> Result<ProcessTimes>` (:284), `still_same(&Holder) -> bool` (:305), and `unlock::stop_process(pid, wait)`. `find_holders` always excludes its own PID. It takes 0.4–1 s, with outliers of several seconds under load.
- **`may_hold` is noisy.** On this machine a scan of any folder usually lists a few unrelated processes (svchost, taskhostw, GoogleDriveFS, dllhost) as `unnamed_handle`, because their handles can't be named, so it can't be known whether those handles are in the folder. That is why `may_hold` never decides whether the folder counts as held. The reason is `locked` when `holders` is non-empty, `may_hold` when `holders` is empty and only `may_hold` entries remain, and `locked` again when both lists are empty (a holder we can't see). In integration tests, assert only on the PIDs the test started, never on list lengths: a real `dotnet` LSP was seen holding a fresh `%TEMP%` folder.
- A file that a test churns (create/delete) goes under the repo's `.tmp\` (the Dev Drive), not `%TEMP%` on C:, where scanners hold fresh files for 1–3 s.
- A Shell call runs on a thread with a timeout in tests (tests/integration/removal.rs:235-245). The real recycle test there is `#[ignore]`.

## Step 1: resolve one path

`pub fn resolve_one(path: &Path) -> Result<Resolved, Refusal>`, where `Resolved` carries the `RegisteredCandidate` and the main worktree's path, and `Refusal` carries a snake_case `reason`: `not_found`, `not_a_worktree`, `main_worktree`, `bare_repo`, `subfolder` (the path is inside a worktree but is not its root), `link`, `orphan` (it has a `.git` file whose worktree is not registered).

Steps: make the path absolute (canonicalize when it exists), get the common dir from `git -C <path> rev-parse --path-format=absolute --git-common-dir` through the crate's git runner (which clears the repo env), list the worktrees of that repo, match on `path_key`, refuse record 0 and bare records, then build the candidate with `default_branches` and `worktree_signals`. A registered record whose folder is gone resolves as prunable.

Proving: integration tests on fixtures, one per refusal reason, plus a linked worktree that resolves with the right branch and merge state, and a prunable one. `cargo test --test integration agent_remove`.

## Step 2: the command

`worktree-sweep remove <PATH> --json [--force] [--stop-build-servers]`, where `--json` is a required flag. The output is one pretty-printed JSON object, `to_writer_pretty` plus a newline, as `report::write_json` writes it:

```json
{
  "status": "removed" | "released" | "refused",
  "reason": null | "locked" | "may_hold" | "too_big_for_recycle_bin" | "shell_timeout" | "would_lose" | "caller_holds" | <step 1 reasons>,
  "path": "D:\\x.wt\\feat", "repo": "D:\\x", "branch": "feat" | null,
  "branch_deleted": false,
  "loss": null | "<the picker's loss sentence>",
  "cd_to": null | "D:\\x",
  "holders": [ <holders::Holder> ],
  "may_hold": [ <holders::MayHold> ],
  "stopped": [ { "pid": 1, "exe": "cargo.exe" } ],
  "released": false,
  "notes": [ "branch feat kept: squash-merged, delete it with git branch -D" ]
}
```

Exit codes: 0 `removed`, 5 `released`, 6 `refused`, 1 error (as today), 2 clap usage.

Order (agent-path.md, "Order"):

1. `resolve_one`, which may refuse with its reason.
2. `std::env::set_current_dir(main worktree)`, so the tool's own cwd is never a holder.
3. `would_lose`, unless `--force`: dirty, Unmerged, unpushed, an uncontained Detached, a git lock, or any signal that is `None` because it could not be read. `NoCommits` is **not** a loss. The `loss` field carries the picker's loss sentence (reuse `loss_sentence`, and gate on the signals above). With `--force` and a git lock, run `git_unlock` first.
4. `caller_holds`: take `holders::ancestors(own pid)`, run `find_holders(folder, &[own pid])`, and if any ancestor PID is among the `holders` (with a definite `Hold`), refuse with `caller_holds`, `cd_to` = the main worktree, and the holders listed.
5. Capacity: a `bin_capacity` + `recycle_decision` of `AskPermanent` gives `released` with reason `too_big_for_recycle_bin`, and no holder scan.
6. Recycle, on a thread that initializes COM, with a 30 s timeout. A timeout gives `released` with reason `shell_timeout` (the stuck thread is abandoned by dropping its handle).
7. On `Locked`: `find_holders`. With `--stop-build-servers`, for each holder in `stoppable`, stop it (`still_same` first; skip it and add a note when it is no longer the same process) and list it in `stopped`. Then retry the recycle once, as in step 6. If it is still locked, give `released` with reason `locked`, or `may_hold` when `holders` is empty but `may_hold` is not.
8. On success: `prune(repo)`, then the branch. It is deleted with `delete_branch(repo, branch, false)` only when the merge state is `Ancestor` or `NoCommits`, with `--force` or not. A `PatchesApplied`/`ContentContained` branch is kept with a note, and any other branch is kept silently. A branch-delete failure is a note, not an error.
9. A prunable resolution skips 3–7: prune, then do the branch as in 8, then `removed`.

Every `released` outcome writes the marker `<admin dir>/worktree-sweep-released.json` (admin dir = `read_gitdir_file(worktree)`): `{"released_at": <unix seconds>, "reason": <reason>, "holders": [{"pid", "exe"}]}` (holders and may_hold together). Write it to a temp name in the same dir and rename it over the old one. `released: true` in the output says it was written. A failure to write it becomes a note and leaves `released: false`.

Keep the decision logic in pure functions over the resolved candidate and the probe results (`would_lose(&RegisteredCandidate) -> Option<String>`, `branch_action(&RegisteredCandidate) -> BranchAction`, the outcome to exit code mapping), so it is tested without processes or the Shell.

Proving:
- unit tests for `would_lose`: one test per loss signal, NoCommits → none, a `None` signal → loss;
- unit tests for `branch_action`: Ancestor/NoCommits delete, PatchesApplied/ContentContained keep with a note, Unmerged keep;
- unit tests for status → exit code;
- integration (the binary via `env!("CARGO_BIN_EXE_worktree-sweep")`, on `tempfile` fixtures, with the child env cleared through `git::clear_repo_env`):
  - `remove_refuses_main_worktree` (exit 6, `main_worktree`);
  - `remove_refuses_dirty_without_force` (exit 6, `would_lose`, the loss sentence present, and the folder still there);
  - `remove_reports_caller_holds`: spawn the binary from a pwsh child whose cwd is the worktree (pwsh → the binary, so pwsh is an ancestor). Exit 6, `caller_holds`, `cd_to` = the main worktree, and the folder still there;
  - `remove_releases_locked_worktree`: a separate pwsh child (not an ancestor) holds a file in the worktree with share none. Exit 5, `released`, reason `locked`, that child in `holders`, the marker file present with its PID, and the folder still there. **This test reaches the Shell recycle.** Make it `#[ignore]` like the existing real-recycle test, and run it once yourself only through the 30 s thread timeout. If a dialog would appear, it must fail, not hang;
  - `remove_prunable_record` (the folder was deleted by the test first): exit 0, `removed`, the record gone from `git worktree list`, and a merged branch deleted.
  - A successful recycle of a real folder is `#[ignore]` as well; run it once and report the result.

## Gates (all green before `done`)

From the repo root, each wrapped as `cmd > .tmp/<name>.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/<name>.log -Tail 20; "rc=$rc"`, test runs at BelowNormal:

- `cargo fmt --all -- --check`
- `cargo clippy --all-targets --all-features -- -D warnings`
- `cargo test`, and again with `$env:GIT_INDEX_FILE='.git/index.lock'; $env:GIT_DIR='D:\nonexistent'` set in the same pwsh call
- `cargo deny check` if Cargo.toml changed

## Constraints

- Never run `remove` against D:\ or anything outside `tempfile`/`.tmp\` fixtures.
- Do not commit. Report FILES, GATES with each rc, RULINGS, SURFACES (the CLI and JSON shape, which the orchestrator documents in README), and CALLS.
