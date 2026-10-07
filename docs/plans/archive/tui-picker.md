# Full-screen picker (ratatui + crossterm): design for review

Design for the Linear project "Full-screen picker" (team WTS). The user's answers to its eight open questions are under "Rulings from the review", and the sections above them already reflect those answers.

## Rulings this design takes as fixed

- Unlock step: leave the alternate screen and restore the terminal, run `unlock::offer` as today (plain prompts, UAC), re-enter, show the retry results in-screen; a guard restores the terminal on any exit or panic.
- `--list` and `--json` keep their plain output. The TUI runs only in interactive mode on a real terminal, and interactive mode refuses to start when stdout is not a TTY.
- `dialoguer` is removed entirely; no `--plain` fallback.
- Picking and confirmation logic stay in pure functions, testable without a terminal; the new dependencies pass `cargo deny`.
- Brief 4c (queued): released worktrees sort first (`report::ordered` key `0 released | 1 registered | 2 orphan`), carry a `released` flag (first in FLAGS), and are pre-ticked through a pure `pick::default_picks`; the loss confirmations still run for them. This design builds on 4c having landed.

## Today's interactive path, and where each piece goes

Every prompt and line of output the interactive mode produces today (working tree of 2026-09-27), in order:

| # | Today (source) | Stream | In the TUI |
|---|---|---|---|
| 1 | `pick::ensure_interactive` refuses unless stdin and stderr are terminals (pick.rs:31) | error exit | Kept as a plain error before anything is drawn, but the check becomes stdin and **stdout** (the TUI draws on stdout; the Ruling names stdout). |
| 2 | `worktree_sweep::scan` (about 10 s on D:\), and any `tracing::warn!` it emits | stderr | Runs **inside the TUI** on a worker thread, behind a **Scanning** screen with a spinner and the root (ruling 6). Its `warn!`s are captured and shown in the list's footer count and in Results. A scan error shows a **Scan failed** screen with the error chain; any key exits with code 1. |
| 3 | `report::render_table` printed to stdout, or `No worktrees or orphan folders found under <root>.` (report.rs:128) | stdout | Table: replaced by the list view. Empty: an **Empty** screen with the same sentence; any key exits 0. `--list` keeps `render_table`. |
| 4 | `MultiSelect` "Pick what to remove (space toggles, enter accepts, esc cancels)" over `report::picker_items` (pick.rs:55) | stderr | The list view itself; the hint moves to the footer. |
| 5 | `Picked <path>` echo per tick (pick.rs:67) | stderr | Removed: the tick mark in the row is the echo. |
| 6 | Per tick with a loss: context line + `Confirm` "Remove anyway?" / "Remove the link?" default no (`pick::loss_sentence`, `TermPrompter`) | stderr | Review dialog "Would lose work" (see Dialogs), one per such pick, default No. |
| 7 | `Nothing picked; nothing removed.` (main.rs:139) | stdout | Enter with nothing ticked shows it in the footer and does nothing; `q` with nothing ticked exits without printing anything. |
| 8 | Per confirmed pick that cannot be recycled: `<name> cannot go to the Recycle Bin: <reason>; deleting it permanently cannot be undone.` + "Delete it permanently?" default no (`remove::plan`, `recycle::recycle_decision`) | stderr | Review dialog "Permanent delete", default No. |
| 9 | `warn!("cannot read the Recycle Bin size for …")` (remove.rs:322) | stderr (tracing) | Captured (see Architecture, tracing) and shown in the pick's result line; the decision is unchanged (unknown size asks for a permanent delete). |
| 10 | Pass 1: recycle; the Shell's own permanent-delete warning can still appear (`FOF_WANTNUKEWARNING`) as a GUI window | GUI | Unchanged; the progress row reads `waiting…` while the Shell's window is up. |
| 11 | After each removal: prune, then "Branch <b>: <reason>" + "Delete the branch?" default **yes** (`remove::after_removed`, `branch_offer`), asked mid-sweep, and again in the retry pass | stderr | Asked up front in the review as a "Delete branch" dialog, default Yes, and applied only if the removal succeeds (see Q2). |
| 12 | `unlock::offer`: `Another process holds files open under:` + paths; sudo-mode messages or the manual command; `Confirm` "Run an elevated scan with sudo to find what holds them?" default yes; then the elevated child's `Scanning open handles…`, per-locker description, `Select` "What should happen to X?", close-handles warning + `Confirm`, result lines | stdout, console shared with `sudo` | The one hand-off: the TUI suspends (leaves the alternate screen, raw mode off), `unlock::offer` runs as plain console output, then the TUI resumes. See "Unlock hand-off" and Q1 (the elevated side uses `dialoguer` too). |
| 13 | `warn!("the unlock flow failed: …")` (remove.rs:181) | stderr (tracing) | Printed plainly, since it happens while suspended; also shown as a results-view banner. |
| 14 | Retry pass, branch offers for the retried picks (see 11) | stderr | Results view updates each retried row in place. |
| 15 | `remove::summary` lines: `<path>: removed (recycled)` … `N removed, N skipped, N failed; X freed (Y of it in the Recycle Bin)` | stdout | Shown in the results view only. Nothing is printed after the terminal is restored (ruling 7). |

## Screens and states

One `Screen` enum; the renderer draws exactly one of these (dialogs draw over the list).

| Screen | Shows | Leaves by |
|---|---|---|
| **List** | Header line (`worktree-sweep  <root>  N candidates, K ticked, X selected for removal`), the candidate table, the detail pane, a footer of key hints. | Enter (to Review), `q`/Esc (quit), `?` (Help). |
| **Help** | Overlay listing the key bindings. | Any key. |
| **Review** | The dialogs below, one at a time, for the ticked picks in list order, then the final confirmation. The list stays visible behind, the pick under review highlighted. | Answering the last dialog (to Removing), Esc (back to List, ticks and answers kept). |
| **Removing** | One row per pick being removed: `pending`, `recycling…`, `waiting…`, `removed (recycled)`, `locked`, `failed: …`, plus its follow-up notes (pruned, branch deleted/kept). A progress line `3/7`. | Automatic: to Suspended when locks were collected, else to Results. |
| **Suspended** (unlock hand-off) | Nothing: the terminal is back to normal and `unlock::offer` owns it. | `offer` returning; the TUI re-enters, drains queued key events, and goes back to Removing for the retry pass. |
| **Results** | The Removing rows in their final state, the unlock outcome as a banner (`Unlocked`, `Partly unlocked`, `Still locked`, `Skipped`), captured warnings, and the total line from `remove::summary`. | Any of Enter/`q`/Esc: restore the terminal and exit; nothing is printed (ruling 7). |
| **Scanning** | A spinner, the root, and the elapsed seconds while the scan runs on its worker thread. | Automatic: to List, Empty or Scan failed. `q`/Esc/Ctrl+C quits; the scan thread is abandoned (the scan writes nothing). |
| **Empty** | `No worktrees or orphan folders found under <root>.` | Any key: exit 0. |
| **Scan failed** | The error and its causes, as `anyhow`'s `{:#}` prints them. | Any key: exit 1. |
| **Too small** | `Terminal too small: need 80×20, have W×H.` in place of any screen. Minimum 80×20 (ruling 10). | A resize to at least the minimum; the state underneath is untouched. |

A candidate's own signal errors (`signals.errors`) are shown in its detail pane.

### List view

- Columns: tick box (`[x]`/`[ ]`), then the table's own columns without `#`: PATH, KIND, BRANCH, MERGE, DIRTY, UPSTREAM, ACTIVE, SIZE, FLAGS. Cell text comes from the existing `report::Row` (made `pub(crate)`), so the list and `--list` never disagree. PATH takes the remaining width and truncates from the left with `…` (`truncate_start`); BRANCH keeps its 28-char cap.
- Order: `report::ordered` as-is: released first (4c), then registered worktrees grouped by repo and path, then orphans. No group header rows in v1; the detail pane names the repo.
- Selected row: reverse video. Ticked rows: the box shows `[x]`. Released rows are pre-ticked from `pick::default_picks` and carry `released` in FLAGS.
- Scrolling: the table keeps the selected row visible (ratatui `TableState` offset); a `12/40` position indicator sits in the table's bottom border.
- Colour (see Q4): MERGE tinted by risk (unmerged, detached-uncontained: red; no commits: yellow; merged, cherry-picked, squashed: green), DIRTY and UPSTREAM `+N` yellow, `released` bold.

### Detail pane

Below the table, fixed height (about 7 lines), for the selected row:

| Line | Registered worktree | Orphan / link |
|---|---|---|
| 1 | Full path (never truncated; wraps if needed) | Full path |
| 2 | Repo path; branch (or `detached <short head>`) | Container dir; kind |
| 3 | Merge state in words, against which branch (`merge_state_against`); dirty counts; upstream (`+N not pushed` / `gone`) | Link target for a link (`→ X:\dev\yaat, not touched`); `still registered in <repo>` when `live_gitdir` is set; `stale .git` |
| 4 | Last activity as a date and age; size and file count (`unreadable` count when non-zero) | Last write; size and file count |
| 5 | Git lock with its reason; `prunable: <why>`; `released <when>: <reason>` (4c's marker) | — |
| 6 | **What removing loses**: the context of `pick::loss_sentence` without the path prefix, or `Nothing is lost.` | Same, from `loss_sentence` (link / registered-elsewhere notice) |
| 7 | Signal errors, if any (`errors`) | — |

`loss_sentence` today returns `"<name>: <loss>"`; the brief splits it so the pane and the dialog get the loss text without the path (the path is already on line 1).

### Dialogs (Review)

Centred boxes over the list, one question each, default answer highlighted. The order per ticked pick is fixed: loss, then permanent delete, then branch; picks go in list order; the final confirmation comes last.

| Dialog | When | Body | Answers |
|---|---|---|---|
| **Would lose work** | `pick::loss_sentence` is `Some` | Pick path, then the loss text (`3 modified, 2 untracked files and 4 commits not on main will be lost. It is git-locked: on a USB drive.`) | `Remove anyway` / **`Keep`** (default) |
| **Link** | Link orphan | `Remove the link only; <target> is not touched.` | `Remove the link` / **`Keep`** (default, as today) |
| **Permanent delete** | `recycle::recycle_decision` is `AskPermanent(reason)` (capacity read up front for each ticked pick's volume) | `<path> cannot go to the Recycle Bin: <reason>. Deleting it permanently cannot be undone.` | `Delete permanently` / **`Skip it`** (default) |
| **Delete branch** | `remove::branch_offer` is `Some` | `Branch <b>: <reason>` (includes the `-D` explanation for cherry-picked / squashed) | **`Delete branch after removing`** (default, as today) / `Keep branch` |
| **Final confirmation** (new; today there is none) | Always, after the per-pick dialogs | `Remove N items: R to the Recycle Bin (X), P permanently (Y), L links, U registrations pruned; B branches deleted. S skipped.` | `Remove` / **`Cancel`** (default; returns to List with everything kept) |

Bold marks the default. Every default matches today's `confirm(.., default)` argument: no for loss, link, permanent delete; yes for the branch.

A pick answered `Keep` or `Skip it` stays ticked in the list but is shown as `skipped` in Review's running tally and in Results, as `remove_picks` does today with `confirmed: false`.

### Removing, unlock hand-off and return

- Removing runs the two-pass `remove::sweep` unchanged. Every decision was taken in Review, so pass 1 asks nothing.
- When pass 1 collected locked picks, the TUI shows `2 picks are locked; leaving the full screen to find what holds them…` for one frame, then suspends: raw mode off, alternate screen left, cursor shown. `unlock::offer(&paths)` runs exactly as today, printing to the normal screen and (in Inline mode) sharing the console with the elevated child.
- On return, the TUI re-enters the alternate screen, clears, drains any key events typed during the hand-off (so a stray Enter does not answer anything), and continues in Removing with the retry pass. Results then shows the outcome banner. The plain text `offer` printed stays in the normal screen's scrollback.

### Results

The Removing rows in final form, the unlock banner, captured warnings, and `remove::summary`'s total line. Results is the only record: nothing is printed after the terminal is restored (ruling 7). The plain text `unlock::offer` printed during the hand-off stays in the normal screen's scrollback.

## Key bindings

Essentials are proposed for v1; rows marked *optional* are for the user to decide (Q3).

| Key | List | Review dialog | Removing | Results |
|---|---|---|---|---|
| ↑/↓, `k`/`j` | Move selection | — | — | Scroll |
| PgUp/PgDn, Home/End | Page / first / last | — | — | Scroll |
| Space | Toggle tick | — | — | — |
| `a` | Tick all | — | — | — |
| `n` | Untick all | — | — | — |
| Enter | Start Review (nothing ticked: footer message) | Take the highlighted answer | — | Exit |
| ←/→, Tab | — | Move between answers | — | — |
| `y` / `n` | — | Choose the yes / no answer directly | — | — |
| Esc | Quit | Back to List, ticks kept | — (see Q5) | Exit |
| `q` | Quit | — | — (see Q5) | Exit |
| Ctrl+C | Quit | Quit (nothing removed) | See Q5 | Exit |
| `?` | Help overlay | — | — | — |
| `/` *optional* | Filter rows by substring of path/branch; Esc clears | — | — | — |
| `s` *optional* | Cycle sort (path, size, age) | — | — | — |
| Mouse *optional* | Click to select, click box to tick, wheel to scroll | Click an answer | — | Wheel |

Windows note for the brief: crossterm on Windows reports key **press and release** events; `update` must act on `KeyEventKind::Press` only, or every key toggles twice.

## Architecture

### Modules

| File | Role | Pure? |
|---|---|---|
| `src/tui/mod.rs` | `pub fn run(report, now) -> Result<Vec<String>>` (returns the summary lines): the event loop wiring the pieces below. | No (edge) |
| `src/tui/app.rs` | `App` state (`screen`, `rows`, `selected`, `ticks: Vec<bool>`, `offset`, review queue and answers, per-pick progress, unlock outcome, warnings, terminal size) and `update(&mut App, Event) -> Vec<Effect>`. | Yes |
| `src/tui/review.rs` | `Review`, an incremental question sequence over the ticked picks (brief T1, 2026-09-27): `Review::new(candidates, root, capacity)`, `step()`, `answer(yes)`, and, once finished, `decisions()` and `totals()`; `final_sentence(&Totals)`. Incremental because a declined loss skips that pick's later questions. | Yes |
| `src/tui/view.rs` | `render(&App, &mut Frame)`: list, detail pane, dialogs, progress, results, too-small. | Yes (a function of state; tested on `TestBackend`) |
| `src/tui/terminal.rs` | `TerminalGuard`: enter (raw mode, alternate screen, hidden cursor), `suspend`/`resume`, restore on `Drop`, panic hook. | No (edge) |
| `src/tui/worker.rs` | Runs the removal on a worker thread and reports `Progress` messages over a channel. | No (edge) |

`Event` = `Key(KeyEvent)` | `Resize(w, h)` | `Tick` (spinner) | `ScanDone(Result<Report>)` | `Progress(Progress)` | `UnlockDone(UnlockOutcome)`. The scan runs on its own worker thread, started before the first frame, and sends `ScanDone`; tracing is captured from the moment the guard is entered, so the scan's `warn!`s never draw over the screen. `Effect` = `StartRemoval(Vec<Decision>)` | `Suspend(Vec<PathBuf>)` (run the unlock hand-off) | `Quit`.

`update` never touches the terminal, the file system or git; `mod.rs` performs the effects and feeds their results back as events.

As built in brief T2 (2026-09-27): a lifetime-free `Loading` owns Scanning, Empty and Scan failed, and `App<'a>` borrows the `Report`; `Event` adds `Capacities` and `Warning`, `Removal(RemovalEvent)` replaces `Progress`, and `Effect` adds `ReadCapacities` and `Cancel`, with `Quit(exit code)`. The exact shapes are in [archive/brief-t2-tui-core.md](./brief-t2-tui-core.md), decisions 2–8.

### Reused pure functions

`report::ordered`, `report::Row` (cells), `report::relative_path`, `human_bytes`, `age`, `truncate_start`/`truncate_end`; `pick::default_picks` (4c); `pick::loss_sentence` (split into path and loss text); `remove::branch_offer`; `recycle::recycle_decision`; `remove::sweep` and its `Sweeper` trait; `remove::summary`; `unlock::outcome_for_exit`.

### Decisions up front; `Prompter` goes

Today `remove_picks` asks the permanent-delete question in `plan` and the branch question in `after_removed`, both through `Prompter`, and the branch question arrives mid-sweep. The proposal (Q2) moves every question into Review, so removal needs no prompter:

- `remove::plan` splits into a pure `plan_action(candidate, capacity: Option<BinCapacity>) -> PlanNeed` (`Run(Action)` or `AskPermanent(reason)`) and the capacity read at the edge (`remove::read_capacity`).
- A `Decision { candidate, plan: Plan /* Run(Action) | Skip(reason) */, branch: BranchChoice /* NotOffered | Delete(offer) | Keep(offer) */ }` per pick is what Review produces; `Keep` keeps today's `branch X kept` note.
- `remove_picks(decisions, root, on_progress: &mut dyn FnMut(Progress), offer_unlock: &mut dyn FnMut(&[PathBuf]) -> Result<UnlockOutcome>)`, `Progress::{Started(i), Done(i)}` indexed into the decisions; `after_removed(candidate, &BranchChoice)` takes the answer instead of asking.
- The loss text without the path comes from `pick::loss_text`; `loss_sentence` stays as the prefixed form the agent JSON uses.
- The `Prompter` trait and `TermPrompter` are deleted; their tests move to `review.rs` and to `remove.rs` tests with explicit decisions.

### Where removal runs

On a worker thread inside `std::thread::scope` (the picks borrow the `Report`), because a recycle blocks: the Shell moves large trees for seconds, a lock is retried at 250/500/1000 ms, and the Shell's permanent-delete window can sit open. The UI thread keeps drawing and reading keys.

- The worker creates its own `recycle::ComApartment` (COM is per thread; `IFileOperation` runs on the worker's STA).
- It sends `Progress::{Started(i), Done(i, Outcome, notes), Locked(i)}` over `std::sync::mpsc`.
- The unlock hand-off: `sweep` calls `offer_unlock` on the worker; the worker's implementation sends `Progress::NeedUnlock(paths)` and blocks on a reply channel. The UI thread, which owns the terminal, suspends, calls `unlock::offer`, resumes, and replies with the outcome. So `unlock::offer` always runs on the thread that restored the terminal.
- The UI loop: `crossterm::event::poll(100 ms)`, then `try_recv` on the worker channel, then draw.

Synchronous removal between frames (no thread) is the alternative: simpler, but the screen freezes during every recycle and Ctrl+C cannot be seen. Not recommended.

### Terminal guard

- `TerminalGuard::enter()`: `enable_raw_mode`, `EnterAlternateScreen`, `Hide` cursor, then `Terminal::new(CrosstermBackend::new(stdout()))`. Uses crossterm directly rather than `ratatui::init()`, because the hand-off must suspend and resume, and `init()` installs a new panic hook each call.
- `Drop`: `disable_raw_mode`, `LeaveAlternateScreen`, `Show`, errors ignored after logging. Runs on normal return, on `?` errors, and on unwinding.
- Panic hook, installed once before `enter`: restore the terminal, then call the previous hook, so the panic message lands on the normal screen. (The crate denies `panic`, but a dependency or an index bug can still panic.)
- `suspend()` = the Drop steps; `resume()` = the enter steps plus `terminal.clear()` and a drain of pending events (`while poll(0) { read() }`).

### Tracing while the screen is up

`init_tracing` writes to stderr, and a `warn!` during the TUI (remove.rs:181, :322, the 4c marker reader) would draw over the alternate screen. The subscriber's writer becomes a switch: while the guard is active, lines go to an in-memory buffer (shown in Results and flushed to stderr after restore); while suspended and outside the TUI, they go to stderr as today. A small `MakeWriter` behind an `AtomicBool` in `main.rs`.

### TTY check

`pick::ensure_interactive` moves to the TUI edge and checks `stdin().is_terminal() && stdout().is_terminal()`, with today's message (use `--list` or `--json`). It runs before the scan, as today.

## Testing

| Layer | How | Examples |
|---|---|---|
| State machine | Unit tests on `update` with synthetic `Event`s, no terminal | Space toggles; `a`/`n`; selection clamps at ends and pages; released rows start ticked; Enter with nothing ticked stays on List; Esc in Review returns with ticks kept; key release events are ignored; a resize below the minimum shows Too small and back; `Progress::NeedUnlock` yields `Effect::Suspend`. |
| Review | Unit tests on `review_steps` / `decisions` | Dirty + unmerged pick asks loss first, default Keep; an over-capacity pick asks permanent delete, default Skip; a squashed branch gets a `-D` offer, default Delete; a declined loss skips the permanent and branch questions; final counts. |
| Removal | Existing `sweep` tests with a fake `Sweeper`; `remove_picks` with explicit decisions on `tempfile` fixtures | Branch deleted only after a successful removal; skipped decisions never touch the disk. |
| Rendering | `ratatui::backend::TestBackend` + `assert_buffer_lines` on a handful of fixed states, not a snapshot of every screen | List at 100×20 with a released pre-ticked row and a truncated path; a loss dialog; Too small at 60×10; Results total line. No new snapshot crate. |
| Manual | Real terminal, by the user | Resize while on each screen; the unlock hand-off with the manual lock test from `../design.md` (sudo prompt, return, retry shown in-screen); a forced panic restores the terminal; Windows Terminal and conhost; `NO_COLOR`. |

## What is removed, and doc changes

- `dialoguer` from `Cargo.toml` and every use: `pick.rs` (`MultiSelect`, `Confirm`, `Term`), `unlock.rs` (`Confirm`, `Select`, `Term`; see Q1).
- `report::picker_items` and `PICKER_WIDTH` (only the picker uses them); the `Picked <path>` echo; `pick::pick`, `TermPrompter` and the `Prompter` trait.
- README: Usage line for the default mode (`scan, then pick in a full-screen list, confirm, remove`); a short "Interactive mode" section with the key table and the Review order; "The interactive mode needs a terminal. Nothing is picked by default." becomes "…needs a terminal on stdin and stdout. Nothing is picked by default, except released worktrees."; Locked folders step 2 notes that the full screen is left for the elevated step and comes back for the retry.
- CHANGELOG `[Unreleased]`: one Changed bullet for the full-screen picker, one for the final confirmation if kept; `dialoguer` removal needs no bullet.
- Glossary (`docs/README.md`): **Picker** (the full-screen list), **Detail pane**, **Review** (the dialogs after Enter), **Hand-off** (leaving the full screen for the unlock flow).
- `../design.md`: its `dialoguer` lines (22, 67, 101) are history; the orchestrator adds a pointer to this doc rather than rewriting them.
- `.claude/skills/worktree-sweep-nextup/SKILL.md:47` ("`dialoguer` needs a terminal") becomes "the TUI needs a terminal".

## Dependencies

Looked up 2026-09-27 with `cargo search` and `cargo info` against crates.io, and resolved in a scratch crate (outside the repo) that mirrors `Cargo.toml` with `dialoguer` swapped for the two new crates; `cargo deny check` (cargo-deny 0.20.2) run there with the repo's `deny.toml`.

| Crate | Version | License | Notes |
|---|---|---|---|
| `ratatui` | 0.30.2 | MIT | rust-version 1.88. Default features `all-widgets, crossterm, layout-cache, macros, underline-color`. Proposed: `default-features = false, features = ["crossterm"]` (123 → 115 lines in the resolved tree). |
| `crossterm` | 0.29.0 | MIT | Pulled by `ratatui-crossterm` 0.1.2 (default feature `crossterm_0_29`). Use it through `ratatui::crossterm` (re-export) so the versions cannot drift, rather than a second direct dependency. On Windows it brings `winapi` 0.3.9 and `crossterm_winapi` 0.9.1, a second Windows binding family beside `windows` 0.62.2; no version duplicate of `windows`. |

`cargo deny check` result with the unchanged `deny.toml`: **licenses FAILED**, one error: `foldhash` 0.2.0 is `Zlib`, pulled by `hashbrown` 0.16.1 (via `kasuari` 0.4.12 → `ratatui-core`) and `hashbrown` 0.17.1 (via `lru`, `ratatui-core`, `ratatui-widgets`). It fails with `default-features = false` too, since `kasuari` is always there.

The fix is adding `"Zlib"` to `[licenses] allow`, which `../design.md` line 103 already lists but `deny.toml` does not. Advisories, bans and sources pass. New warning: duplicate `hashbrown` (0.16.1 and 0.17.1); `bans.multiple-versions = "warn"`, so it does not fail. The existing warnings (duplicate `syn` 2/3, `ISC` and `BSD-3-Clause` allowances unmatched) are there on the repo today.

Binary size, measured on minimal release builds in the scratchpad (default profile): a program using `dialoguer` 0.12.0 (`MultiSelect` + `Confirm`) is 194,048 bytes; one using `ratatui` 0.30.2 defaults (a `List`, a `Paragraph`, one `crossterm` event read) is 363,008 bytes. Roughly +165 KiB for the swap; the real binary's figure is unmeasured.

## Rulings from the review

1. **Unlock prompts:** plain numbered line prompts on stdin, with a pure parser tested without a terminal (option a). `dialoguer` goes from both sides of the unlock step.
2. **Question timing:** every question is asked up front in Review (option a). A branch answer takes effect only if its removal succeeds, and `Prompter` is removed.
3. **Keys:** essentials only in v1 (option a): no filter, sort or mouse.
4. **Colour:** risk tints on MERGE, DIRTY and UPSTREAM, and `NO_COLOR` is honoured (option a).
5. **During removal:** keys are ignored except Ctrl+C, which stops after the current item, marks the rest `skipped (cancelled)` and skips the unlock step (option a).
6. **Scan:** inside the TUI, with a spinner (option b). This adds the Scanning, Empty and Scan failed screens, a scan worker thread, and tracing captured from the first frame.
7. **After exit:** nothing is printed, and Results is the only record (option b).
8. **Final confirmation:** a final "Remove N items?" is kept, with Cancel as the default (option a).

9. **Dates in the detail pane** (T2 round): local date and age, `2026-09-25 14:02 (2d ago)`, through the Windows time-zone API the crate already links; no new dependency.
10. **Minimum size** (T2 round): 80×20, about 8 table rows beside the 7-line detail pane; below it the Too small notice (`Terminal too small: need 80×20, have W×H.`).

Settled by the orchestrator: `"Zlib"` is added to `deny.toml`'s allowed licenses, which `v1-design.md` already lists.

## The questions as asked

1. **The unlock step's own prompts.** `unlock::offer` asks with `dialoguer::Confirm`, and the elevated side asks with `dialoguer::Select` and `Confirm` (unlock.rs:166, :579, :592), so "run `offer` as today" and "remove `dialoguer` entirely" conflict.
   - a. (recommended) Replace them with plain numbered line prompts on stdin (`[Y/n]`, `1) Stop process  2) Close its handles  3) Skip  4) Done [1]:`), a small pure parser tested without a terminal. Worst case: a slightly plainer elevated step than today's arrow-key menu.
   - b. Move the parent's "Run an elevated scan?" question into the TUI as a dialog before suspending, and use plain line prompts only on the elevated side. Worst case: the sudo-mode messages and manual command still print outside the TUI, so the hand-off is split across two places.
   - c. Keep `dialoguer` for the elevated `unlock` subcommand only. Worst case: breaks the "removed entirely" Ruling and keeps the dependency.
2. **When the branch and permanent-delete questions are asked.**
   - a. (recommended) All in Review, before anything is removed; the branch answer applies only if the removal succeeds; `Prompter` is removed. Worst case: the user answers a branch question for a worktree that then fails to remove (the answer is simply unused).
   - b. Keep today's timing with a channel-backed `Prompter`: the worker asks the UI mid-sweep. Worst case: dialogs pop up during removal and during the retry pass, and `remove.rs` keeps its prompt plumbing.
3. **Optional keys in v1** (`/` filter, `s` sort, mouse).
   - a. (recommended) None in v1; essentials only. Worst case: on a large root (40+ rows) the user pages instead of filtering.
   - b. `/` filter only. Worst case: filter plus ticks raise "does Tick all tick hidden rows?", which needs its own rule.
   - c. All three. Worst case: more state and tests for features not yet asked for.
4. **Colour.**
   - a. (recommended) Risk tints on MERGE/DIRTY/UPSTREAM as above, honouring `NO_COLOR`. Worst case: a theme where red/green read badly; `NO_COLOR` is the way out.
   - b. Monochrome (bold/reverse only). Worst case: risky rows are harder to spot.
5. **Keys during removal.**
   - a. (recommended) Ignored, except Ctrl+C, which stops after the current item (the rest are shown as `skipped (cancelled)`) and skips the unlock step. Worst case: a long recycle still has to finish before the stop takes effect.
   - b. All input ignored until Results. Worst case: no way out of a long run short of closing the window, which leaves the current item half-way in the Shell's hands.
6. **Where the scan runs.**
   - a. (recommended) Before the TUI, behind a plain `Scanning <root>…` line on stderr; scan errors stay plain errors. Worst case: about 10 s of a static line on D:\.
   - b. Inside the TUI with a spinner. Worst case: the scan's own `warn!`s and errors need an in-screen error state, and the scan moves onto a thread.
7. **What stays on screen after exit.**
   - a. (recommended) Print `remove::summary` to stdout after restoring the terminal. Worst case: the text also shown in Results appears twice across the session.
   - b. Nothing printed; Results is the only record. Worst case: the outcome is gone from scrollback once the TUI exits.
8. **The new final "Remove N items?" confirmation** (today there is none).
   - a. (recommended) Keep it, default Cancel. Worst case: one more keypress per run.
   - b. Drop it; Enter on the list plus the per-pick dialogs are enough. Worst case: a pre-ticked released worktree with no loss is removed with no question at all.

## Proposed brief split

In order; each after 4b and 4c have landed (4c changes `ordered`, `pick.rs` and FLAGS).

1. **Brief T1: decisions up front** (Q2a). `src/remove.rs` (pure `plan_action`, `Decision`, `remove_picks` with progress and unlock callbacks, `after_removed` taking the answer, `Prompter` removed), `src/pick.rs` (loss text split from the path; `default_picks` kept), a new pure `src/tui/review.rs`, `src/main.rs` (temporarily drives Review with the existing `dialoguer` prompts so the tool keeps working between briefs), their tests. About 5 files.
2. **Brief T2: the TUI core, not wired.** `Cargo.toml` (ratatui, `Zlib` via `deny.toml`), `src/tui/mod.rs` (module only), `src/tui/app.rs` (state and `update`, including the Scanning, Empty and Scan failed states), `src/tui/view.rs` (render), `src/report.rs` (`Row` and truncation helpers `pub(crate)`), with `update` unit tests and `TestBackend` render tests. 6 files.
3. **Brief T3: wiring and removal of `dialoguer`.**

   `src/tui/terminal.rs` (guard, panic hook, suspend/resume), `src/tui/worker.rs` (the scan and removal threads, channels, the unlock reply), `src/tui/mod.rs` (event loop), `src/main.rs` (TTY check on stdout before anything is drawn, tracing switch, calls `tui::run`, prints nothing afterwards), `src/unlock.rs` (Q1's prompts), `Cargo.toml` (drop `dialoguer`), `src/report.rs`/`src/pick.rs` deletions (`picker_items`, `PICKER_WIDTH`, `pick::pick`, `ensure_interactive` moved).

   About 7 files; if the brief must stay at 6, `src/unlock.rs` (Q1) goes in its own small brief T3b first.
4. **Docs (orchestrator, no brief):** README, CHANGELOG, glossary, the nextup profile line, a pointer in `../design.md`; then the manual checks in the Testing table.
