# Brief: WTS-20 (C14) part B, the window's view models

Part A is on main (2c97060, 5d4d607): `ReportTable.Rows` / `ReportRow` / `CellRisk` and `DetailText.Lines` / `FormatLocal`. This brief builds the view models on them. The design is settled in the WTS-20 description ("Settled (orchestrator, owner-delegated)", part B); read it first, it is the spec. C15 (WTS-21) later adds the XAML, the workers and the wiring; C14 leaves it nothing else.

Route: the Opus `implementer` through `Agent` (tier 3). Worktree: `git worktree add ../worktree-sweep.wt/wts-20-view-models -b wts-20-view-models main`, base and landOn recorded per the user-level `nextup` §3.

## Facts measured on main (re-check before dispatch)

- Projects: `src/WorktreeSweep` (exe, `net10.0-windows`, `UseWPF true`, `AssemblyName worktree-sweep`, references Core and `System.CommandLine 2.0.12`; `Program.cs:75-78` prints "the window is not built yet" and exits 2 for the default mode), `src/WorktreeSweep.Core` (no WPF), `tests/WorktreeSweep.Tests` (references both, xUnit v3 4.0.1 on Microsoft.Testing.Platform). No `Directory.Packages.props`: package versions go inline in the csproj. `Directory.Build.props`: nullable, warnings as errors, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild`.
- CommunityToolkit.Mvvm is referenced nowhere yet. Add it to `src/WorktreeSweep/WorktreeSweep.csproj` at the current stable version, looked up at dispatch (`dotnet package search CommunityToolkit.Mvvm --exact-match`), never from memory.
- Core API the view models consume (signatures on main):
  - `Scanner.Scan(string root) : ScanReport` (blocking; throws `IOException` or `ArgumentException`, the Failed state). `ScanReport.Candidates.Count == 0` is the Empty state; its text is `No worktrees or orphan folders found under {root}.`
  - `ReportTable.Rows(ScanReport, long nowUnix) : IReadOnlyList<ReportRow>` (in `Ordered()` order), `ReportTable.HumanBytes(long)`, `ReportTable.SizeText(long bytes, bool atLeast)`, `Candidate.KnownSize`.
  - `DetailText.Lines(Candidate, long nowUnix, Func<long, TimeSpan> utcOffset)` (seven lines).
  - `RemovalPlanner.NeedsCapacity(Candidate)`, `RemovalPlanner.ReadCapacity(string path) : BinCapacity?` (blocking registry read; C15 calls it on a worker).
  - `ReviewSession(IReadOnlyList<Candidate> candidates, string root, Func<string, BinCapacity?> capacity, IReadOnlyList<DiscoveryError> discoveryErrors)`: `Current : ReviewStep?`, `Answer(bool)`, `Totals() : ReviewTotals?`, `Decisions() : IReadOnlyList<Decision>?`. Forward-only, no undo. `ReviewStep(int Pick, StepKind Kind, string Body, string Question, bool DefaultAnswer)`, `StepKind` Loss, Link, Permanent, Branch; the Permanent body already names the path.
  - `ReviewTotals.FinalSentence()`; its `Recycle + Permanent + Links + Prunes == 0` means nothing runs.
  - `Remover.RemovePicks(IReadOnlyList<Decision>, Action<Progress>, Func<IReadOnlyList<string>, UnlockOutcome>, CancellationToken) : IReadOnlyList<Swept>` (blocking, callbacks on the sweeping thread; C15 runs it and marshals). `Progress.Started(int Index)` / `Progress.Done(int Index)`, indexes into the decisions list; skip decisions report nothing.
  - `SweepSummary.Lines(IReadOnlyList<Swept>, string root)`: per-pick lines then the total line last.
  - `UnlockOutcome { Unlocked, PartlyUnlocked, StillLocked, Skipped }`.
- Test fixtures: `tests/WorktreeSweep.Tests/ReportSamples.cs` builds candidates and reports by hand with no git; use it.
- Rust behaviour to carry (read, not port as code): `src/tui/app.rs` (state and transitions, answer replay at :603-606 and :642-651), `src/tui/view.rs:498-509` (dialog titles and answer labels), `src/tui/review.rs`.

## The change

Folder `src/WorktreeSweep/ViewModels/`, namespace `WorktreeSweep.ViewModels`, CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`), no `System.Windows` types (headless tests construct them directly). No threads, no dispatcher: every state change is a method C15 calls on the UI thread.

- `MainViewModel`: `Screen` (an enum: Scanning, Empty, Failed, List, Review, Removing, Results), the root, and the screens' view models.
  - `ScanCompleted(ScanReport report, long nowUnix)` → Empty or List; `ScanFailed(string message)` → Failed with the message.
- `CandidateRowViewModel`: the `ReportRow` texts and risks, `IsTicked` (observable). Released worktrees are ticked by default; no row is locked out.
- `ListViewModel`: `Rows`, `Selected` (its `Detail` = `DetailText.Lines` for the selected candidate, with the injected `Func<long, TimeSpan>` offset), `Header` = `{N} candidates, {K} ticked, {size} selected for removal` (size by `ReportTable.SizeText` of the ticked known sizes, "at least" when any ticked size is partial or unknown), commands `TickAll`, `TickNone`, `Review`. `Review` with nothing ticked sets `Message` = `Nothing picked; tick a row first.` and stays on the List.
- `ReviewViewModel`: states Preparing, Asking, Confirming.
  - Entering Review lists `CapacityPaths` (the ticked candidates' paths for which `RemovalPlanner.NeedsCapacity` holds) and is Preparing; `CapacitiesRead(IReadOnlyDictionary<string, BinCapacity?>)` builds the `ReviewSession` with that lookup and the report's discovery errors, then replays kept answers.
  - Asking exposes `Title`, `PickPath` (relative to the root), `Body`, `Question`, `YesLabel`, `NoLabel`, `DefaultAnswer`, and `Answer(bool)`. Titles and labels: Loss "Would lose work" / Remove anyway / Keep; Link "Link" / Remove the link / Keep; Permanent "Permanent delete" / Delete permanently / Skip it; Branch "Delete branch" / Delete branch after removing / Keep branch.
  - `Back` returns to the List keeping ticks and answers. Answers are kept per ticked set: when Review is entered again with the same ticked set, stored answers replay while each step's (pick, kind) matches the stored one, and the rest are cut; a different ticked set clears them.
  - After the last answer: if nothing runs, removal starts at once; otherwise Confirming shows `FinalSentence()` with Cancel the default, `Confirm` starts the removal, `Cancel` returns to the List.
- `RemovingViewModel`: built from the decisions; rows of the runnable decisions with status `pending` → `removing…` (`Progress.Started`) → `done` (`Progress.Done`); `Heading` = `Removing {done}/{runnable}`, with `  stopping after the current item…` appended after `Cancel`. It owns the `CancellationTokenSource` whose token C15 passes to `RemovePicks`; `Cancel` cancels it once. `UnlockStarted(int pickCount)` shows `1 pick locked; finish the unlock step in the terminal.` / `{n} picks locked; finish the unlock step in the terminal.` and disables Cancel; `UnlockDone(UnlockOutcome)` sets `Banner` = `Unlock: Unlocked|Partly unlocked|Still locked|Skipped` and clears the notice. `Finished(IReadOnlyList<Swept>)` → Results.
- `ResultsViewModel`: `Lines` = `SweepSummary.Lines(swept, root)` without the last line, `Total` = the last line, plus the unlock banner when there was one.

Out of scope: the XAML, workers, keyboard handling, a Help screen, colours (C15 maps `CellRisk`).

## Proving tests (red first; new `tests/WorktreeSweep.Tests/ViewModels/*Tests.cs`, from `ReportSamples`)

One named test per rule above, at least:

- `ScanCompletedWithNoCandidatesIsEmpty`, `ScanFailedShowsTheMessage`.
- `ReleasedWorktreesAreTickedByDefault`, `HeaderCountsTickedAndTheirSize`, `HeaderSaysAtLeastWhenATickedSizeIsPartial`, `ReviewWithNothingTickedSaysNothingPicked`.
- `SelectedRowShowsItsSevenDetailLines`.
- `ReviewStartsPreparingWithTheCapacityPaths`, `CapacitiesReadAsksTheFirstStep`, `StepTitlesAndLabelsFollowTheKind` (theory over the four kinds), `BackKeepsAnswersForTheSameTickedSet`, `ChangedTickedSetClearsAnswers`, `ReplayStopsAtTheFirstChangedStep`.
- `LastAnswerShowsTheFinalSentenceWithCancelDefault`, `NothingToRunStartsRemovalAtOnce`, `CancelOnConfirmReturnsToTheList`.
- `ProgressMovesRowsThroughRemovingToDone`, `CancelStopsAfterTheCurrentItem` (token cancelled once, heading suffix), `UnlockNoticeAndBanner`.
- `FinishedShowsTheSummaryLinesAndTotal`.

## Gates

Each through `pwsh -NoProfile -File tools/gate.ps1 -Log .tmp/<name>.log -TimeoutSeconds 600 -Slot heavy -- <command>`: `dotnet build WorktreeSweep.slnx -c Release`; the scoped `dotnet test WorktreeSweep.slnx -c Release --no-build --filter-class *ViewModels*` while iterating (wildcard form; exit 8 means zero tests ran); the full suite; `dotnet csharpier check .`; `dotnet format style` and `analyzers` with `--verify-no-changes --severity info`. `cargo deny` is not involved; a new NuGet package is named in the report's SURFACES.

Size: about 100 implementer calls. If the implementer reaches that before the Results screen, it hands back `partial`; the remainder (Removing, Results) goes out as a fresh brief against the same tree.

On landing: archive this file to `docs/plans/archive/`, add the Task Index row "Add or change a window view model" (`src/tui/app.rs`, `view.rs`, `review.rs` behaviour → `Report/ReportTable.cs`, `DetailText.cs` → `Review/ReviewSession.cs` → `Removal/Remover.cs`, `SweepSummary.cs` → `src/WorktreeSweep/ViewModels/` → `tests/WorktreeSweep.Tests/ViewModels/`), and glossary entries for "View model" and the screens (Removing, Results) if the docs use them.
