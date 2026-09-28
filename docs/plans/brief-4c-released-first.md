# Brief 4c: the sweep lists released worktrees first and pre-picks them

Part of the agent path, see [agent-path.md](./agent-path.md) (ruling 10, "Marker", and the "Remove, else release" line in MAIN.md). Brief 4b writes the marker; this brief makes the scan read it and the interactive sweep act on it. It runs after 4b has landed, because both edit `src/lib.rs`.

## Tree and branch

- Root: `D:\worktree-sweep` (the main checkout). First: `git switch -c feat/released-first` from an up-to-date `main`.
- Files: `src/report.rs` (field, ordering, FLAGS), `src/lib.rs` (the scan fills the field), `src/pick.rs` (defaults), a marker reader wherever 4b put its writer (reuse its type; see below), and the test constructors of `RegisteredCandidate` at report.rs:485, pick.rs:243 and remove.rs:797 (the line numbers are from before 4b; re-find them).

## Measured facts (code on `main` before 4b; re-check after 4b lands)

- `report::RegisteredCandidate` (report.rs:65) derives `Debug, Clone, Serialize`, not `Default`. Its fields are `path, repo, branch, head, prunable, git_lock` and a flattened `signals`. The scan builds it at lib.rs:75-83 inside `signals::parallel_map`.
- `report::ordered(&Report) -> Vec<&Candidate>` (report.rs:113) sorts by `(0 | 1, path_key(repo), path_key(path))`, with registered before orphans. `render_table` (:128) and `picker_items` (:157) both use it, and `pick::pick` (pick.rs:48) indexes into it.
- FLAGS (report.rs:256-261) is built from `git-locked` and `prunable` for registered rows and joined with `", "`. README.md:40 documents the FLAGS values.
- `pick::pick` uses `MultiSelect::new().with_prompt(..).items(&items).report(false).interact_opt()` (pick.rs:55-60), with no `.defaults(..)`. Its doc comment says "with nothing ticked". The loss-sentence confirmations then run per tick.
- The marker is written by 4b to `<admin dir>/worktree-sweep-released.json`, where the admin dir is `discover::read_gitdir_file(worktree)`. It holds `{"released_at": <unix seconds>, "reason": "...", "holders": [{"pid", "exe"}]}`. Before writing a reader, look in `src/agent.rs` (4b) for the marker type and its file name constant, and reuse them. If 4b made them private, make them `pub(crate)`. Do not write a second copy of the format.

## Step 1: the scan reads the marker

- Add `pub released: Option<Released>` to `RegisteredCandidate`, where `Released` is 4b's marker type or a `Serialize` view of it. It serializes as `null` when absent, as the other `Option` fields do.
- In the scan (lib.rs:75), read the marker for each registered worktree that has an admin dir. A missing file gives `None`. An unreadable or malformed file gives `None` plus a `tracing::warn!` naming the file and the error. It never fails the scan. The scan still writes nothing.
- A prunable record, whose folder is gone, still reads its marker, because the admin dir outlives the folder until prune.

Proving: integration tests on fixtures:
- a linked worktree with a hand-written marker in its admin dir has `released` set in `fx.scan()`, with its reason;
- the same worktree without a marker has `None`;
- a malformed marker (`{not json`) gives `None`, and the scan still succeeds.

## Step 2: released first, flagged, pre-picked

- `report::ordered`: released registered candidates come first. The key becomes `(0 released | 1 registered | 2 orphan, repo key, path key)`.
- FLAGS: a released row gets `released` as its first flag.
- A pure `pub fn default_picks(candidates: &[&Candidate]) -> Vec<bool>` in `pick.rs` is true exactly for released candidates. `pick::pick` passes it to `MultiSelect::defaults`. Update its doc comment: released worktrees are pre-ticked, and the loss confirmations still run for them.
- `--list` shows the new order and flag. `--json` gains the `released` field.

Proving:
- a unit test for `ordered`: two repos, one released worktree in the second repo, and an orphan. The released one sorts first, then the registered ones by repo and path, then the orphan;
- a unit test for `default_picks`: `[released, plain, orphan]` → `[true, false, false]`;
- a unit test for the FLAGS cell of a released and git-locked row: `released, git-locked`;
- a unit test that `write_json` of a report with a released candidate contains `"released": {` and `"reason"`, and one without contains `"released": null`.

## Gates (all green before `done`)

From the repo root, each wrapped as `cmd > .tmp/<name>.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/<name>.log -Tail 20; "rc=$rc"`, test runs at BelowNormal:

- `cargo fmt --all -- --check`
- `cargo clippy --all-targets --all-features -- -D warnings`
- `cargo test`, and again with `$env:GIT_INDEX_FILE='.git/index.lock'; $env:GIT_DIR='D:\nonexistent'` set in the same pwsh call

## Constraints

- The scan stays read-only. Do not run the interactive mode anywhere. `--list`/`--json` against D:\ are allowed as a smoke check.
- Do not commit. Report FILES, GATES with each rc, RULINGS, SURFACES (FLAGS value, JSON field, order: the orchestrator documents them in the README) and CALLS.
