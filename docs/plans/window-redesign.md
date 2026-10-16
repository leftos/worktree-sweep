# Window redesign: build plan

## Context

The window's UI is rough. Columns don't sort, there is no way to filter or to tick only the orphans, the folder can only be given on the command line, and a long scan shows an indeterminate bar with no explanation. The owner approved a redesign drawn on a Design canvas with four screens: Choose a folder, Scanning, Pick (interactive) and Removing. The canvas is saved at [canvases/window-redesign/](canvases/window-redesign/index.html) (published at https://claude.ai/artifact/UtruL71yEcPB1Ej3Y6fPew).

Interview rulings:
- **Stop scan:** real cancellation, a token threaded through the scan and git.
- **Scan progress:** steps, counts and a "working on now" list naming each candidate's current sub-step and running file count.
- **Recent folders:** stored in `%LOCALAPPDATA%\worktree-sweep\recent.json`, up to 5 entries.
- **Gate sweep:** WTS-54 runs first.

## Order of work

0. **WTS-54, gate sweep** (on `main`, before anything else):
   - Rewrite the Gates lines at `.claude/skills/worktree-sweep-nextup/SKILL.md:46` and `:73` to use `gate run --log … --timeout 600 --slot heavy -- …`, with `--cd <worktree root>` for a worktree, and add one line on installing gate from `X:\dev\gate` (`pwsh tools/install.ps1`).
   - Delete `tools/gate.ps1`.
   - Run the whole gate through `gate run`, commit, and send the gate repo a one-line inbox note naming the commit.
1. **Book the plan:**
   - Create a Linear project "Window redesign" with items 2–6 below as issues, in order, each linking the design doc.
   - Write `docs/plans/window-redesign.md` (the design: the rulings above and the per-item specs below).
   - Save the canvas to `docs/plans/canvases/window-redesign/`: read `project/canvas.json`, `project/*.dc.html` and `artifact-type/dc-runtime.js` into it, then run `canvas_offline`. Commit it.
   - Feature marker `branch: feat/window-redesign`, with a draft PR into `main`. Each item lands onto it from its own worktree, per the profile's landing path.
2. **WTS-56, scan progress and cancellation (Core).** Brief this one to the Opus implementer: it is tier 3, with a concurrency and cancellation boundary.
   - `Scanner.Scan(string root, Action<ScanProgress> onProgress, CancellationToken token)`, both arguments required. Update every caller:
     - CLI: `Program.cs:81` passes a no-op and `CancellationToken.None`.
     - Tests: `Fixture.cs:193` and `ScanTests.cs:48`.
     - Window: `WindowSeams.Scan`.
   - New `ScanProgress` records in `src/WorktreeSweep.Core/Scan/`:
     - `Discovering(int Child, int Children)`, from the loop at `Discoverer.cs:95`.
     - `Discovered(int Repos, int Containers, int Worktrees, int Orphans, int Links)`.
     - `RepoLookedUp(int Done, int Total)`.
     - `CandidateStep(string Path, ScanStep Step, long Files)`, where `ScanStep` is merge, status, upstream, activity or size. It wraps each `reads.Read` call in `SignalReader.ReadWorktreeSignals` (`:78-82`) and is reported every N folders popped in `WalkSize` (`SignalReader.Size.cs:47`).
     - `CandidateRead(int Done, int Total)`, counted with `Interlocked` in the `ParallelMap` lambda (`SignalReader.cs:178`).
     - The callback runs on worker threads, which the doc comments say.
   - Cancellation:
     - `ParallelMap` gains `.WithCancellation(token)`.
     - `Discover` and `WalkSize` check the token per child or folder.
     - `GitRunner` takes the token and kills git's process tree on cancel, reusing the timeout kill path. The only token there today is `CancellationToken.None` at `GitRunner.cs:267`.
     - A cancelled scan throws `OperationCanceledException`, unwrapped as `ParallelMap` already unwraps.
   - Tests:
     - Event order and totals against a `Fixture` tree.
     - Cancelling mid-walk throws, and leaves no git process (the fixture's process check).
     - CLI output is unchanged.
3. **WTS-57, window shell: folder, rescan, Scanning screen.**
   - `Program.cs:39-43`: the window mode's ROOT becomes optional. With no ROOT, the window opens on a new `Choose` screen prefilled with the current directory. `--list` and `--json` keep defaulting to the current directory.
   - `MainViewModel` gains:
     - `Folder` (bound to the root bar);
     - `ScanCommand` and `RescanCommand`, refused on Review and Removing;
     - `StopScanCommand`;
     - `ScanningViewModel` (steps, counts, percent, a working-now list keyed by path and dropped on `CandidateRead`; no stalled-volume notice or discovery-error line, by owner ruling);
     - an explicit `BeginScan(root)` that sets `Screen = Scanning`.
   - `WindowWorkers`:
     - owns one scan at a time and its `CancellationTokenSource`; a new scan or a close cancels the old one;
     - posts progress through `IUiDispatcher`, coalesced to one post per 100 ms;
     - drops a stale scan's results by a generation counter;
     - closing while scanning cancels and exits, as now.
   - `MainWindow`: the title and scanning text are bound to view-model state instead of being set in the constructor (`MainWindow.xaml.cs:26-27`). Browse… uses `Microsoft.Win32.OpenFolderDialog` (.NET 8+ WPF) behind a seam.
   - `RecentFolders` in `src/WorktreeSweep/`:
     - loads and saves `recent.json`, with its path through `WindowSeams` so tests use `%TEMP%`;
     - each entry holds the folder, the scan time (UTC), and the candidate count and total size;
     - most recent first, at most 5;
     - a corrupt file is traced as a warning and read as empty, a warning not a silent default;
     - written after each completed scan.
   - Tests:
     - `MainViewModel` screen moves: Choose → Scanning → List, then Rescan, Stop and Failed.
     - `WindowWorkers`: cancel, a stale result is dropped, posts are coalesced. Uses the `ImmediateDispatcher` and fake seams.
     - `RecentFolders` round trip, the 5 cap, the corrupt file.
     - The `MainWindowTests` load test still passes.
4. **WTS-58, Pick screen: sort, filter, ticks, detail pane.**
   - `ListViewModel`:
     - `Rows` stays fixed in table order, since Review's replay keys on table-order indexes (`ListViewModel.cs:90`, `MainViewModel.cs:107`, `ReviewViewModel.cs:160`).
     - A new `Shown` list is rebuilt from `SortKey`/`SortDirection` (asc → desc → off), `KindFilter` (all/worktree/orphan/link, with counts), `NothingLost` (`LossText.For(candidate) is null`) and `Search` (path or branch, case-insensitive).
     - Sort orders: Merge/Dirty/Upstream sort by `CellRisk` and then by text; Active and Size sort by their numbers; the rest by ordinal-ignore-case text.
     - `ReportRow` gains the raw sort values the cells drop: last-activity Unix time, bytes, and kind as an enum.
   - Tick commands:
     - `TickShown` and `UntickAll`, plus a header checkbox: ticked when every shown row is, and pressing it ticks or unticks the shown rows.
     - `HiddenTicked` holds the count and its sentence. A `ClearFilters` command clears the filters.
     - `Review` still sends every ticked row, hidden ones included, in table order.
   - Header and button:
     - The header becomes `{N} candidates · {shown} shown · {K} ticked · {size} selected for removal`.
     - The Review button reads `Review K picks…`.
   - Detail pane:
     - `DetailText.Lines` is replaced by `DetailText.Fields`, which returns labelled fields: What it is, State, Activity and size, Marks, Removing it loses, Unreadable signals. `Lines`' only production user is `ListViewModel.Detail`, so `Lines` is removed and `DetailTextTests` is rewritten onto `Fields`.
     - `Selected` is cleared when the filter hides the selected row.
   - XAML:
     - The DataGrid binds `Shown`. Its `Sorting` event is handled in code-behind and forwarded to the view model's sort command, with `e.Handled = true` and the column's `SortDirection` set from the view model.
     - Kind buttons are a ToggleButton group, plus the Nothing-lost toggle, the search box, Tick shown and Untick all.
     - The discovery-error notice above the grid.
     - The labelled detail grid.
     - Risk cells keep `RiskBrushConverter` and gain pill backgrounds.
   - Tests:
     - `ListViewModelTests`: each sort key and the tri-state cycle; each filter and their combination; ticking shown rows; hidden-ticked counting; Review's table order with a hidden ticked row; selection cleared by a filter.
     - The rewritten `DetailTextTests`.
5. **WTS-59, Removing screen progress.**
   - `RemovingViewModel` gains:
     - `Done` and `Total`, for a determinate bar;
     - `BytesDone` and `BytesTotal`, from each decision's known size;
     - per-row action text from `Decision.Plan` (recycle, permanent delete, link, prune) and its outcome on `Done`.
   - The Cancel button reads "Stop after this item", with the explanatory line.
   - The heading becomes `Removing {done} of {total}`.
   - Tests in `RemovingViewModelTests`.
6. **WTS-60, docs and changelog:**
   - `docs/design.md`:
     - CLI: ROOT is optional for the window.
     - Window: replace "There is no filter or sort" (`:109`); describe the detail pane fields (`:107`), the scan progress steps, Stop, Rescan and recent folders.
   - `docs/ARCHITECTURE.md`:
     - Task Index rows for "report scan progress or rescan from the window" and "change the List screen (sort, filter, ticks, detail)", using the explorers' file orders.
     - `:68`: "seven lines" becomes fields.
   - `docs/README.md` glossary: Pick, Screen, View model, Shown rows, Recent folders, in sorted places.
   - README usage.
   - `CHANGELOG.md` `[Unreleased]` bullets, per `changelog-and-commit`.
   - Then `/ship`, which merges `feat/window-redesign`.

## Not included

These are drawn on the canvas, but this plan does not build them:
- **File counts during removal.** The mock shows "5,630 of 9,118 files" during a permanent delete. `PermanentDelete.DeleteTree` has no callback; this plan shows the action and the byte totals from the scan instead.
- **Per-item elapsed times on the Scanning steps.** The steps show counts and an overall elapsed time only.

## Verification

- Each item runs the profile gates through `gate run`: `dotnet build WorktreeSweep.slnx -c Release`, `dotnet test WorktreeSweep.slnx -c Release --no-build`, `dotnet csharpier check .`, and `dotnet format style` and `analyzers --verify-no-changes`. A test filter uses the wildcard form.
- Each item ends with `code-review`.
- Item 2 proves cancellation with a red-first test, and the `--list` output against a fixture is unchanged.
- The window is never opened against a real folder from an agent session. For a check by eye, the owner runs `dotnet run --project src/WorktreeSweep` against a `%TEMP%` fixture tree that the brief creates, and with no ROOT to see the Choose screen.
