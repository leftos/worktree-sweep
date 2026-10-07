# Brief T2: the TUI core, not wired in

Design: [tui-picker.md](./tui-picker.md). Read it in full first; this brief settles what it leaves open. T2 builds the picker's state machine and renderer as pure, tested code. Nothing calls it yet: T3 adds the terminal guard, the worker threads, the event loop and the wiring in `main.rs`, and removes `dialoguer`. `dialoguer` stays in T2.

Worktree root: `D:\worktree-sweep` (main checkout), branch `feat/t2-tui-core` (the orchestrator creates it before dispatch; confirm with `git branch --show-current`). Do not commit, do not edit `docs/`, `README.md` or `CHANGELOG.md`.

## Measured facts (main after T1, mapped 2026-09-27)

- `src/report.rs`: `Report { root, repos, candidates }` :16 (`Send + Sync`); `Candidate::{Registered, Orphan}`, `path()` :47, `size_bytes()` :56; `ordered(&Report) -> Vec<&Candidate>` :116 (released first); private `struct Row { path, kind: &'static str, branch, merge, dirty, upstream, active, size, flags }` :239 with `Row::new(&Candidate, root, now)` :253.

  Private `truncate_start` :462 / `truncate_end` :473; `MAX_BRANCH_WIDTH` 28 :112; `human_bytes` :200, `age(then, now)` :221 (`5m/7h/3d/5w/2y`), `relative_path` :172 are pub.

  `picker_items`/`PICKER_WIDTH` stay (T3 deletes them).
- `src/tui/review.rs` (T1): `Review<'a>::new(candidates: &[&'a Candidate], root, capacity: &mut dyn FnMut(&Path) -> Option<BinCapacity>)` :112 (calls `capacity` once per folder pick, at construction).

  Its methods: `step()` :151, `answer(bool)` :157, `totals() -> Option<Totals>` :172, `decisions(self) -> Option<Vec<Decision<'a>>>` :202 (consumes), `final_sentence(&Totals)` :289; `Step { pick /* index into the slice given to new */, kind: StepKind, body, question, default }` :28; `StepKind { Loss, Link, Permanent, Branch }` :15.

  No `Clone`, no rewind.
- `src/remove.rs`: `Decision<'a>` :110, `Outcome` :139 (`Recycled{bytes}`, `Permanent{bytes}`, `LinkRemoved`, `Pruned`, `Skipped(String)`, `Failed(String)`), `Progress { Started(usize), Done(usize) }` :130, `remove_picks(decisions, on_progress, offer_unlock) -> Vec<Swept>` :235, `summary(&[Swept], root)` :658.
- `src/unlock.rs`: `UnlockOutcome { Unlocked, PartlyUnlocked, StillLocked, Skipped }` :45.
- `src/pick.rs`: `default_picks(&[&Candidate]) -> Vec<bool>` :17; `loss_text(&Candidate) -> Option<Loss>` :108 (`Loss { kind, text, question }`); private `repo_of_gitdir` :211; the detached short head is `head.get(..7)` inline :160.
- Candidate fields for the detail pane: `RegisteredCandidate` (report.rs:66) `path, repo, branch: Option<String>, head: Option<String>, prunable: Option<String>, git_lock: Option<String> /* Some("") = no reason */, released: Option<agent::Released>, signals: WorktreeSignals`.

  `WorktreeSignals` (signals.rs:117) `merge_state, merge_state_against: Option<String>, dirty: Option<Dirty{modified, untracked}>, upstream: Option<Upstream::{None, Gone, Tracking{ahead}}>, last_activity_unix: Option<i64>, size: Option<SizeInfo{bytes, files, unreadable, last_write_unix}>, errors: Vec<String>`.

  `MergeState` (signals.rs:19) `Ancestor | NoCommits | PatchesApplied | ContentContained | Unmerged{commits} | Detached{contained}`; `OrphanCandidate { orphan: discover::Orphan{path, container, orphan_kind: Folder|Link, link_target, stale_gitdir, live_gitdir, has_git_dir}, size: SizeInfo }`.

  `agent::Released { released_at: i64, reason: agent::Reason, holders }` (agent.rs:77), `Reason` (agent.rs:48) `Locked | MayHold | TooBigForRecycleBin | ShellTimeout | WouldLose | CallerHolds | NotRemovable(RefusalReason)`, no human label today.
- `lib.rs:13` has `pub mod tui;`; `src/tui/mod.rs` holds `pub mod review;`.
- Toolchain: edition 2024, rust-version 1.98, rustc 1.98.1; ratatui 0.30.2 (MSRV 1.88) and its tree are already in the local registry. `deny.toml:8` allow list lacks `Zlib`, which `foldhash` 0.2.0 (via ratatui) needs.
- No test helper is shared across modules today; every builder is private inside its module's tests (report.rs:490-604 has the fullest set).
- Lints: pedantic warn; deny unwrap/panic/print/`allow_attributes`. `TestBackend::assert_buffer_lines` panics, so compare buffers with `anyhow::ensure!(backend.buffer() == &Buffer::with_lines([...]))` or line-by-line strings.

## Decisions settled (orchestrator and user, 2026-09-27)

1. **Dependencies.** `ratatui = { version = "0.30.2", default-features = false, features = ["crossterm"] }`; use crossterm only as `ratatui::crossterm`. Add `"Zlib"` to `deny.toml` `[licenses] allow`. Add the `windows` feature `Win32_System_Time` for decision 11.
2. **Two phases, so `App` can borrow the report** (Review, Decision and Swept all borrow `&'a Candidate`):
   - `Loading` (in `app.rs`, no lifetime): screens **Scanning** (root, spinner frame, elapsed seconds), **Empty** (`No worktrees or orphan folders found under <root>.`), **Scan failed** (the `{:#}` error string).

     `Loading::update(&mut self, Event) -> Vec<Effect<'static>>`; the caller (T3) moves it to Empty/Failed with `Loading::empty()` / `Loading::failed(String)` or builds an `App` when the scan returns candidates. Scanning: `q`/Esc/Ctrl+C → `Quit(0)`; Empty: any key → `Quit(0)`; Scan failed: any key → `Quit(1)`.
   - `App<'a>` borrows `&'a Report` (owned by T3's `run` frame): screens **List**, **Help**, **Review**, **Removing**, **Results**, plus **Too small** as an overlay state that replaces drawing whenever the size is below the minimum and keeps the state underneath.
3. **`Event`** (`pub enum`, in `app.rs`): `Key(KeyEvent)`, `Resize(u16, u16)`, `Tick`, `Capacities(HashMap<PathBuf, Option<BinCapacity>>)`, `Removal(RemovalEvent)`, `UnlockDone(UnlockOutcome)`, `Warning(String)`.

   `RemovalEvent { Started(usize), Done { index: usize, outcome: Outcome, notes: Vec<String> }, Locked(usize), NeedUnlock(Vec<PathBuf>), Finished { summary: Vec<String> } }`, indices into the decisions handed to `StartRemoval`. T3 produces these from the worker; T2 only consumes them. Act on `KeyEventKind::Press` only.
4. **`Effect<'a>`**: `ReadCapacities(Vec<PathBuf>)`, `StartRemoval(Vec<Decision<'a>>)`, `Cancel`, `Suspend(Vec<PathBuf>)`, `Quit(u8)` (exit code). `update` never touches the terminal, disk, registry or git.
5. **Starting Review.** Enter on List with ticks → `Effect::ReadCapacities(paths of the ticked picks that remove::needs_capacity)` and a *preparing* sub-state; `Event::Capacities(map)` then builds `Review::new(&ticked, root, &mut |p| map.get(p).copied().flatten())`. Enter with nothing ticked sets the footer message `Nothing picked; press Space to tick a row.` and stays on List. App keeps `ticked_rows: Vec<usize>` so `step.pick` maps back to its row for highlighting.
6. **Esc in Review** returns to List with ticks kept. App records the answers given (`Vec<bool>`); entering Review again with the **same** ticked set replays them into the new Review (and re-reads capacities); a changed ticked set starts the answers afresh.
7. **Dialogs.** Per `StepKind`, answer labels and defaults from the design's Dialogs table: Loss `Remove anyway` / **`Keep`**; Link `Remove the link` / **`Keep`**; Permanent `Delete permanently` / **`Skip it`**; Branch **`Delete branch after removing`** / `Keep branch`. The dialog shows the pick's path (relative to the root) above `step.body`. ←/→/Tab move the highlight, Enter takes it, `y`/`n` answer directly.

   After the last step, the **final confirmation** (App-owned, not Review): body `final_sentence(&totals)`, answers `Remove` / **`Cancel`**; Cancel returns to List with ticks and answers kept; when nothing is runnable (recycle + permanent + links + prunes == 0) it is skipped and `StartRemoval` goes out directly, as `main.rs` does today.
8. **Removing / Results.** One row per decision: `pending`, `removing…` (on `Started`), then the outcome text (`removed (recycled)`, `deleted permanently`, `link removed`, `pruned`, `skipped: <reason>`, `failed: <reason>`), or `locked` (on `Locked`), with its notes indented beneath. `Plan::Skip` decisions start as `skipped: <reason>`. Progress line `k/N` over runnable decisions. There is no `waiting…` state (the Shell's window cannot be detected).

   `NeedUnlock(paths)` → a one-frame notice `N picks are locked; leaving the full screen to find what holds them…` and `Effect::Suspend(paths)`; `UnlockDone(outcome)` stores the banner (`Unlocked`, `Partly unlocked`, `Still locked`, `Skipped`).

   Keys: only Ctrl+C, which returns `Effect::Cancel` once and marks the screen `stopping after the current item…`. `Finished { summary }` → Results: rows in final form, the banner, the warnings, and the summary's total line (last element); Enter/`q`/Esc → `Quit(0)`; ↑/↓/PgUp/PgDn scroll.
9. **Keys** (List): ↑/↓ and `k`/`j` move; PgUp/PgDn move by the visible table rows; Home/End; Space toggles; `a` ticks all; `n` unticks all; Enter starts Review; `?` opens Help (any key closes it); `q`/Esc/Ctrl+C → `Quit(0)`. In a dialog `q` does nothing; Ctrl+C → `Quit(0)` (nothing removed). The footer message clears on the next key press. Released rows start ticked from `pick::default_picks`.
10. **Minimum size 80×20** (user, 2026-09-27): below it render only `Terminal too small: need 80×20, have W×H.`. Detail pane 7 lines, fixed.
11. **Dates** (user, 2026-09-27): the detail pane shows `YYYY-MM-DD HH:MM (<age> ago)` in local time. Pure `format_local(unix: i64, offset_minutes: i32, now: i64) -> String` (civil-from-days, no crate) plus an edge `utc_offset_minutes(unix: i64) -> i32` using `SystemTimeToTzSpecificLocalTime` (so DST is the one in force at that date); it returns 0 and logs a `warn!` on failure. `App` holds `offset: fn(i64) -> i32`, which tests set to a fixed function. Put both in a new `src/tui/when.rs`.
12. **Detail pane lines** per the design's table. Reuse `report::Row` cells (make `Row`, its fields, `Row::new`, `truncate_start`, `truncate_end` `pub(crate)`), `pick::loss_text` for line 6 (`Nothing is lost.` when `None`), and make `pick::repo_of_gitdir` `pub(crate)` for `still registered in <repo>`.

    Released reason labels: `Locked` "locked", `MayHold` "may be held", `TooBigForRecycleBin` "too big for the Recycle Bin", `ShellTimeout` "the Shell timed out", `WouldLose` "would lose work", `CallerHolds` "held by the calling shell", `NotRemovable(_)` "not removable"; line 5 reads `released <date>: <label>`. A git lock with an empty reason reads `git-locked`. Detached branch reads `detached <7-char head>`.
13. **Colour.** `App.color: bool` (T3 sets it from `NO_COLOR`); when false, no colours, only bold and reverse. Tints on MERGE: red for `Unmerged` and `Detached{contained: false}`, yellow for `NoCommits`, green for `Ancestor`/`PatchesApplied`/`ContentContained`; DIRTY non-empty and UPSTREAM `+N` yellow; `released` in FLAGS bold. Constants at the top of `view.rs`. Tint from the candidate's `MergeState`, not the cell text.
14. **Help overlay**: the List key table from decision 9, one key per line.
15. **Test fixtures** in a new `#[cfg(test)] pub(crate) mod fixtures` at `src/tui/fixtures.rs` (candidates and a `Report` built without git, following report.rs:490-604's builders; `NOW = 1_790_000_000`).
16. `src/tui/mod.rs`: `pub mod app; pub mod review; pub mod view; pub mod when;` and `#[cfg(test)] mod fixtures;`. No `run` yet.

## Steps and proving commands

1. Cargo.toml / deny.toml (decision 1). Proving: `cargo deny check` rc=0 (the duplicate `hashbrown` warning is expected); `cargo build` rc=0.
2. `report.rs` visibility; `pick.rs` `repo_of_gitdir` `pub(crate)`. Proving: existing tests unchanged and green.
3. `when.rs`. Named tests: `format_local_utc_epoch_day` (a fixed timestamp at offset 0 gives the right date and time), `format_local_applies_offset_across_midnight`, `format_local_leap_day` (2028-02-29), `format_local_age_suffix`.
4. `app.rs` + `fixtures.rs`.

   Named tests, each asserting through `update` with synthetic events: `space_toggles_tick`, `tick_all_and_untick_all`, `selection_clamps_at_both_ends`, `page_down_moves_by_visible_rows`, `released_rows_start_ticked`, `enter_with_nothing_ticked_shows_footer_and_stays`, `footer_message_clears_on_next_key`, `key_release_is_ignored`, `enter_asks_for_capacities_of_folder_picks_only`.

   Also: `capacities_start_review_at_first_step`, `esc_in_review_keeps_ticks_and_replays_answers`, `changed_ticks_reset_answers`, `final_confirmation_defaults_to_cancel`, `final_confirmation_skipped_when_nothing_runnable`, `remove_answer_emits_start_removal`.

   And: `need_unlock_emits_suspend`, `ctrl_c_during_removal_cancels_once`, `finished_moves_to_results`, `resize_below_minimum_shows_too_small_and_back`, `loading_scan_failed_any_key_quits_with_1`, `loading_empty_any_key_quits_with_0`.
5. `view.rs`. `TestBackend` tests on fixed states: `list_at_100x24_shows_pre_ticked_released_row_and_truncated_path`, `loss_dialog_shows_path_body_and_default_keep`, `too_small_at_60x10`, `results_shows_total_line`, `no_color_draws_no_colours` (every cell's fg is `Reset` when `color` is false), `scanning_shows_root_and_spinner`.

Write each named test before its code and watch it fail once where that is practical; report which ones you saw red.

## Gates (from the repo root, pwsh, each wrapped)

```
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'; cargo fmt --all -- --check > .tmp/fmt.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/fmt.log -Tail 20; "rc=$rc"
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'; cargo clippy --all-targets --all-features -- -D warnings > .tmp/clippy.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/clippy.log -Tail 20; "rc=$rc"
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'; cargo test > .tmp/test.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/test.log -Tail 20; "rc=$rc"
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'; $env:GIT_INDEX_FILE='.git/index.lock'; $env:GIT_DIR='D:\nonexistent'; cargo test > .tmp/test-hook.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/test-hook.log -Tail 20; "rc=$rc"
cargo deny check > .tmp/deny.log 2>&1; $rc = $LASTEXITCODE; Get-Content .tmp/deny.log -Tail 20; "rc=$rc"
```

## Rulings every brief carries

- Never run the interactive mode against a real folder; T2 code is not reachable from the binary anyway.
- Functions ≤100 lines, complexity ≤8: split `update` per screen and `render` per screen.
- Output through a locked writer; tests assert with `anyhow::ensure!`; `#[expect(.., reason = ..)]`, never `allow`.

## Report

`done` / `partial` / `underspecified`, the five gate results with test counts, files changed, the tests you saw red, and a `SURFACES` line (expected: none reachable by a user yet; list every user-facing string the new code defines so the orchestrator can check them against the design).
