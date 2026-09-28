//! The picker's state and its pure `update`. [`Loading`] covers the scan (Scanning, Empty, Scan failed); [`App`]
//! borrows the scanned [`Report`] and covers the list, the help, the review, the removal and its results.
//! `update` never touches the terminal, the disk, the registry or git: it returns the [`Effect`]s its caller
//! performs, and the caller feeds their results back as [`Event`]s.

use std::collections::HashMap;
use std::path::PathBuf;

use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyEventKind, KeyModifiers};

use crate::pick;
use crate::recycle::BinCapacity;
use crate::remove::{self, Decision, Outcome, Plan};
use crate::report::{self, Candidate, Report, Row};
use crate::tui::review::{Review, StepKind, Totals};
use crate::unlock::UnlockOutcome;

/// The narrowest terminal the picker draws in.
pub const MIN_WIDTH: u16 = 80;
/// The shortest terminal the picker draws in.
pub const MIN_HEIGHT: u16 = 20;
/// The detail pane's height, in lines.
pub(crate) const DETAIL_LINES: u16 = 7;
/// The list screen's lines outside the table rows: the header, the table's borders and column header, the detail
/// pane and the footer.
const LIST_CHROME: u16 = 1 + 3 + DETAIL_LINES + 1;

pub(crate) const NOTHING_PICKED: &str = "Nothing picked; press Space to tick a row.";

/// Something that happened: a key, a resize, a timer tick, or the result of an [`Effect`].
#[derive(Debug)]
pub enum Event {
    /// A key; only presses act.
    Key(KeyEvent),
    /// The terminal's new width and height.
    Resize(u16, u16),
    /// The spinner's timer.
    Tick,
    /// The Recycle Bin settings asked for by [`Effect::ReadCapacities`], by path; `None` when unreadable.
    Capacities(HashMap<PathBuf, Option<BinCapacity>>),
    /// Progress of the removal started by [`Effect::StartRemoval`].
    Removal(RemovalEvent),
    /// What the unlock hand-off asked for by [`Effect::Suspend`] did.
    UnlockDone(UnlockOutcome),
    /// A warning logged while the screen is up.
    Warning(String),
}

/// Progress of a removal, by index into the decisions handed to [`Effect::StartRemoval`].
#[derive(Debug)]
pub enum RemovalEvent {
    /// Removing decision `i` started.
    Started(usize),
    /// A decision is finished.
    Done {
        /// Which decision.
        index: usize,
        /// What happened to it.
        outcome: Outcome,
        /// Follow-up lines: pruned, branch deleted or kept.
        notes: Vec<String>,
    },
    /// Decision `i` is locked and waits for the unlock step.
    Locked(usize),
    /// These locked picks need the unlock hand-off.
    NeedUnlock(Vec<PathBuf>),
    /// The removal is over; the last line is the total.
    Finished {
        /// `remove::summary`'s lines.
        summary: Vec<String>,
    },
}

/// Something for the caller to do.
#[derive(Debug)]
pub enum Effect<'a> {
    /// Read the Recycle Bin settings of these picks' volumes and answer with [`Event::Capacities`].
    ReadCapacities(Vec<PathBuf>),
    /// Remove these decisions on a worker, reporting [`Event::Removal`].
    StartRemoval(Vec<Decision<'a>>),
    /// Stop the removal after the current item.
    Cancel,
    /// Leave the full screen, run the unlock flow on these paths, come back and answer with [`Event::UnlockDone`].
    Suspend(Vec<PathBuf>),
    /// Restore the terminal and exit with this code.
    Quit(u8),
}

/// How many table rows the list shows in a terminal `height` lines tall; at least 1.
pub(crate) fn table_rows(height: u16) -> usize {
    usize::from(height.saturating_sub(LIST_CHROME)).max(1)
}

/// The Removing and Results screens' lines outside the body: the heading, the body's borders, the total line and
/// the footer.
const REMOVAL_CHROME: u16 = 1 + 2 + 1 + 1;

/// How many body lines the Removing and Results screens show in a terminal `height` lines tall; at least 1.
pub(crate) fn removal_body_rows(height: u16) -> usize {
    usize::from(height.saturating_sub(REMOVAL_CHROME)).max(1)
}

/// One review answer and the question it answered, so a replay stops where the questions differ.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct Answer {
    pub(crate) pick: usize,
    pub(crate) kind: StepKind,
    pub(crate) yes: bool,
}

/// One line of the Removing and Results body.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct BodyLine {
    pub(crate) text: String,
    pub(crate) bold: bool,
}

impl BodyLine {
    fn plain(text: String) -> Self {
        Self { text, bold: false }
    }

    fn bold(text: String) -> Self {
        Self { text, bold: true }
    }
}

/// Whether a terminal of `size` (width, height) is below the minimum.
pub(crate) fn too_small(size: (u16, u16)) -> bool {
    size.0 < MIN_WIDTH || size.1 < MIN_HEIGHT
}

fn is_ctrl_c(key: &KeyEvent) -> bool {
    key.code == KeyCode::Char('c') && key.modifiers.contains(KeyModifiers::CONTROL)
}

/// Which scan screen is up.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LoadingScreen {
    /// The scan is running.
    Scanning,
    /// The scan found nothing.
    Empty,
    /// The scan failed with this error chain.
    Failed(String),
}

/// The screens before a report exists.
#[derive(Debug)]
pub struct Loading {
    pub(crate) root: PathBuf,
    pub(crate) screen: LoadingScreen,
    pub(crate) spinner: usize,
    pub(crate) size: (u16, u16),
    /// Whole seconds since the scan started; the caller keeps it current.
    pub elapsed_secs: u64,
    /// Warnings logged during the scan, for the caller to hand to the [`App`].
    pub warnings: Vec<String>,
}

impl Loading {
    /// The Scanning screen for `root` in a terminal of `size` (width, height).
    #[must_use]
    pub fn new(root: PathBuf, size: (u16, u16)) -> Self {
        Self {
            root,
            screen: LoadingScreen::Scanning,
            spinner: 0,
            size,
            elapsed_secs: 0,
            warnings: Vec::new(),
        }
    }

    /// Moves to the Empty screen: the scan found nothing.
    pub fn empty(&mut self) {
        self.screen = LoadingScreen::Empty;
    }

    /// Moves to the Scan failed screen with `error`, as `{:#}` prints it.
    pub fn failed(&mut self, error: String) {
        self.screen = LoadingScreen::Failed(error);
    }

    /// Applies `event`.
    pub fn update(&mut self, event: Event) -> Vec<Effect<'static>> {
        match event {
            Event::Key(key) if key.kind == KeyEventKind::Press => self.on_key(&key),
            Event::Resize(width, height) => {
                self.size = (width, height);
                Vec::new()
            }
            Event::Tick => {
                self.spinner = self.spinner.wrapping_add(1);
                Vec::new()
            }
            Event::Warning(warning) => {
                self.warnings.push(warning);
                Vec::new()
            }
            _ => Vec::new(),
        }
    }

    fn on_key(&self, key: &KeyEvent) -> Vec<Effect<'static>> {
        let code = match self.screen {
            LoadingScreen::Scanning
                if is_ctrl_c(key) || matches!(key.code, KeyCode::Char('q') | KeyCode::Esc) =>
            {
                0
            }
            LoadingScreen::Scanning => return Vec::new(),
            LoadingScreen::Empty => 0,
            LoadingScreen::Failed(_) => 1,
        };
        vec![Effect::Quit(code)]
    }
}

/// Where one decision's removal is.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum Status {
    Pending,
    Removing,
    Locked,
    Finished(Outcome),
}

/// One decision on the Removing and Results screens.
#[derive(Debug)]
pub(crate) struct RemovalRow {
    /// Its path relative to the root.
    pub(crate) path: String,
    /// Whether it is removed at all (a skipped decision is not).
    pub(crate) runnable: bool,
    pub(crate) status: Status,
    pub(crate) notes: Vec<String>,
}

impl RemovalRow {
    fn new(decision: &Decision<'_>, root: &std::path::Path) -> Self {
        let path = report::relative_path(decision.candidate.path(), root);
        match &decision.plan {
            Plan::Run(_) => Self {
                path,
                runnable: true,
                status: Status::Pending,
                notes: Vec::new(),
            },
            Plan::Skip(reason) => Self {
                path,
                runnable: false,
                status: Status::Finished(Outcome::Skipped(reason.clone())),
                notes: Vec::new(),
            },
        }
    }
}

/// The removal's rows and what is shown around them.
#[derive(Debug, Default)]
pub(crate) struct Removal {
    pub(crate) rows: Vec<RemovalRow>,
    /// Ctrl+C was pressed: the removal stops after the current item.
    pub(crate) stopping: bool,
    /// The one-frame notice before the unlock hand-off.
    pub(crate) notice: Option<String>,
    pub(crate) banner: Option<UnlockOutcome>,
    /// The summary's total line.
    pub(crate) total: Option<String>,
    /// The first line shown on Results.
    pub(crate) scroll: usize,
}

impl Removal {
    /// Finished runnable decisions, and all runnable decisions.
    pub(crate) fn progress(&self) -> (usize, usize) {
        let runnable = self.rows.iter().filter(|row| row.runnable);
        let finished = runnable
            .clone()
            .filter(|row| matches!(row.status, Status::Finished(_)))
            .count();
        (finished, runnable.count())
    }

    /// Every line the Removing and Results body draws, in order: the notice, each row and its notes, the unlock
    /// banner, and `warnings`.
    pub(crate) fn body_lines(&self, warnings: &[String]) -> Vec<BodyLine> {
        let mut lines = Vec::new();
        if let Some(notice) = &self.notice {
            lines.push(BodyLine::bold(notice.clone()));
        }
        for row in &self.rows {
            lines.push(BodyLine::plain(format!(
                "{}  {}",
                row.path,
                status_text(&row.status)
            )));
            lines.extend(
                row.notes
                    .iter()
                    .map(|note| BodyLine::plain(format!("    {note}"))),
            );
        }
        if let Some(banner) = self.banner {
            lines.push(BodyLine::plain(String::new()));
            lines.push(BodyLine::bold(format!("Unlock: {}", unlock_label(banner))));
        }
        if !warnings.is_empty() {
            lines.push(BodyLine::plain(String::new()));
            lines.extend(
                warnings
                    .iter()
                    .map(|warning| BodyLine::plain(format!("warning: {warning}"))),
            );
        }
        lines
    }

    /// The body line row `index` is drawn on.
    fn row_line(&self, index: usize) -> usize {
        let notice = usize::from(self.notice.is_some());
        notice
            + self
                .rows
                .iter()
                .take(index)
                .map(|row| 1 + row.notes.len())
                .sum::<usize>()
    }

    /// Scrolls so row `index` is within a body `visible` lines tall.
    fn reveal(&mut self, index: usize, visible: usize) {
        let line = self.row_line(index);
        if line < self.scroll {
            self.scroll = line;
        } else if line >= self.scroll + visible {
            self.scroll = line + 1 - visible;
        }
    }

    fn set(&mut self, index: usize, status: Status) {
        if let Some(row) = self.rows.get_mut(index) {
            row.status = status;
        }
    }
}

/// A row's status as the Removing and Results screens show it.
pub(crate) fn status_text(status: &Status) -> String {
    match status {
        Status::Pending => "pending".to_owned(),
        Status::Removing => "removing…".to_owned(),
        Status::Locked => "locked".to_owned(),
        Status::Finished(Outcome::Recycled { .. }) => "removed (recycled)".to_owned(),
        Status::Finished(Outcome::Permanent { .. }) => "deleted permanently".to_owned(),
        Status::Finished(Outcome::LinkRemoved) => "link removed".to_owned(),
        Status::Finished(Outcome::Pruned) => "pruned".to_owned(),
        Status::Finished(Outcome::Skipped(reason)) => format!("skipped: {reason}"),
        Status::Finished(Outcome::Failed(reason)) => format!("failed: {reason}"),
    }
}

/// The unlock outcome as the Results banner names it.
pub(crate) fn unlock_label(outcome: UnlockOutcome) -> &'static str {
    match outcome {
        UnlockOutcome::Unlocked => "Unlocked",
        UnlockOutcome::PartlyUnlocked => "Partly unlocked",
        UnlockOutcome::StillLocked => "Still locked",
        UnlockOutcome::Skipped => "Skipped",
    }
}

fn locked_notice(count: usize) -> String {
    if count == 1 {
        "1 pick is locked; leaving the full screen to find what holds them…".to_owned()
    } else {
        format!("{count} picks are locked; leaving the full screen to find what holds them…")
    }
}

/// Where the review is.
#[derive(Debug)]
pub(crate) enum ReviewState<'a> {
    /// Waiting for [`Event::Capacities`] with exactly these paths, as [`Effect::ReadCapacities`] asked.
    Preparing { paths: Vec<PathBuf> },
    /// A question is up; `yes` is whether its yes answer is highlighted.
    Asking { review: Review<'a>, yes: bool },
    /// The final confirmation is up.
    Confirming {
        decisions: Vec<Decision<'a>>,
        totals: Totals,
        yes: bool,
    },
}

/// The screen being drawn.
#[derive(Debug)]
pub(crate) enum Screen<'a> {
    List,
    Help,
    Review(ReviewState<'a>),
    Removing(Removal),
    Results(Removal),
}

/// The picker over a scanned report.
pub struct App<'a> {
    pub(crate) report: &'a Report,
    /// The candidates in list order.
    pub(crate) candidates: Vec<&'a Candidate>,
    /// Their table cells.
    pub(crate) rows: Vec<Row>,
    pub(crate) now: i64,
    pub(crate) color: bool,
    /// Minutes east of UTC at a given Unix time.
    pub(crate) offset: fn(i64) -> i32,
    /// Warnings logged while the screen is up.
    pub warnings: Vec<String>,
    pub(crate) size: (u16, u16),
    pub(crate) selected: usize,
    /// The first table row shown.
    pub(crate) scroll: usize,
    pub(crate) ticks: Vec<bool>,
    /// The footer message; cleared by the next key.
    pub(crate) message: Option<&'static str>,
    pub(crate) screen: Screen<'a>,
    /// The rows under review, in list order; a step's `pick` indexes it.
    pub(crate) ticked_rows: Vec<usize>,
    /// The answers given in review, replayed when the same rows are reviewed again.
    pub(crate) answers: Vec<Answer>,
    answered_for: Vec<usize>,
}

impl<'a> App<'a> {
    /// The list over `report`, ages measured from `now_unix`, in a terminal of `size` (width, height). `color` is
    /// false under `NO_COLOR`; `offset` gives the local time zone's minutes east of UTC at a Unix time.
    #[must_use]
    pub fn new(
        report: &'a Report,
        now_unix: i64,
        size: (u16, u16),
        color: bool,
        offset: fn(i64) -> i32,
    ) -> Self {
        let candidates = report::ordered(report);
        let rows = candidates
            .iter()
            .map(|candidate| Row::new(candidate, &report.root, now_unix))
            .collect();
        let ticks = pick::default_picks(&candidates);
        Self {
            report,
            candidates,
            rows,
            now: now_unix,
            color,
            offset,
            warnings: Vec::new(),
            size,
            selected: 0,
            scroll: 0,
            ticks,
            message: None,
            screen: Screen::List,
            ticked_rows: Vec::new(),
            answers: Vec::new(),
            answered_for: Vec::new(),
        }
    }

    /// Applies `event` and returns what the caller must do.
    pub fn update(&mut self, event: Event) -> Vec<Effect<'a>> {
        match event {
            Event::Key(key) if key.kind == KeyEventKind::Press => self.on_key(&key),
            Event::Key(_) | Event::Tick => Vec::new(),
            Event::Resize(width, height) => {
                self.size = (width, height);
                self.select(self.selected);
                Vec::new()
            }
            Event::Capacities(capacities) => self.on_capacities(&capacities),
            Event::Removal(removal) => self.on_removal(removal),
            Event::UnlockDone(outcome) => {
                if let Screen::Removing(removal) = &mut self.screen {
                    removal.banner = Some(outcome);
                    removal.notice = None;
                }
                Vec::new()
            }
            Event::Warning(warning) => {
                self.warnings.push(warning);
                Vec::new()
            }
        }
    }

    /// Whether the terminal is below the minimum; only Ctrl+C acts then.
    pub(crate) fn too_small(&self) -> bool {
        too_small(self.size)
    }

    /// The list row to highlight: the pick under review, else the selection.
    pub(crate) fn highlighted_row(&self) -> usize {
        match &self.screen {
            Screen::Review(ReviewState::Asking { review, .. }) => review
                .step()
                .and_then(|step| self.ticked_rows.get(step.pick).copied())
                .unwrap_or(self.selected),
            _ => self.selected,
        }
    }

    fn on_key(&mut self, key: &KeyEvent) -> Vec<Effect<'a>> {
        if is_ctrl_c(key) {
            return self.on_ctrl_c();
        }
        if self.too_small() {
            return Vec::new();
        }
        match self.screen {
            Screen::List => self.on_list_key(key.code),
            Screen::Help => {
                self.screen = Screen::List;
                Vec::new()
            }
            Screen::Review(_) => self.on_review_key(key.code),
            Screen::Removing(_) => Vec::new(),
            Screen::Results(_) => self.on_results_key(key.code),
        }
    }

    fn on_ctrl_c(&mut self) -> Vec<Effect<'a>> {
        match &mut self.screen {
            Screen::Removing(removal) if !removal.stopping => {
                removal.stopping = true;
                vec![Effect::Cancel]
            }
            Screen::Removing(_) => Vec::new(),
            _ => vec![Effect::Quit(0)],
        }
    }

    fn on_list_key(&mut self, code: KeyCode) -> Vec<Effect<'a>> {
        self.message = None;
        if self.move_selection(code) {
            return Vec::new();
        }
        match code {
            KeyCode::Char(' ') => {
                if let Some(tick) = self.ticks.get_mut(self.selected) {
                    *tick = !*tick;
                }
            }
            KeyCode::Char('a') => self.ticks.fill(true),
            KeyCode::Char('n') => self.ticks.fill(false),
            KeyCode::Enter => return self.start_review(),
            KeyCode::Char('?') => self.screen = Screen::Help,
            KeyCode::Char('q') | KeyCode::Esc => return vec![Effect::Quit(0)],
            _ => {}
        }
        Vec::new()
    }

    /// Moves the selection for a movement key; `false` for any other key.
    fn move_selection(&mut self, code: KeyCode) -> bool {
        let page = table_rows(self.size.1);
        let target = match code {
            KeyCode::Up | KeyCode::Char('k') => self.selected.saturating_sub(1),
            KeyCode::Down | KeyCode::Char('j') => self.selected + 1,
            KeyCode::PageUp => self.selected.saturating_sub(page),
            KeyCode::PageDown => self.selected + page,
            KeyCode::Home => 0,
            KeyCode::End => self.rows.len(),
            _ => return false,
        };
        self.select(target);
        true
    }

    /// Selects row `target`, clamped to the list, and scrolls to keep it visible.
    fn select(&mut self, target: usize) {
        self.selected = target.min(self.rows.len().saturating_sub(1));
        let visible = table_rows(self.size.1);
        if self.selected < self.scroll {
            self.scroll = self.selected;
        } else if self.selected >= self.scroll + visible {
            self.scroll = self.selected + 1 - visible;
        }
    }

    fn start_review(&mut self) -> Vec<Effect<'a>> {
        let ticked: Vec<usize> = self
            .ticks
            .iter()
            .enumerate()
            .filter_map(|(row, &tick)| tick.then_some(row))
            .collect();
        if ticked.is_empty() {
            self.message = Some(NOTHING_PICKED);
            return Vec::new();
        }
        if ticked != self.answered_for {
            self.answers.clear();
            self.answered_for.clone_from(&ticked);
        }
        let paths: Vec<PathBuf> = ticked
            .iter()
            .map(|&row| self.candidates[row])
            .filter(|candidate| remove::needs_capacity(candidate))
            .map(|candidate| candidate.path().to_path_buf())
            .collect();
        self.ticked_rows = ticked;
        self.screen = Screen::Review(ReviewState::Preparing {
            paths: paths.clone(),
        });
        vec![Effect::ReadCapacities(paths)]
    }

    /// Builds the review from a reply to the pending capacity request; a reply for any other set of paths is stale
    /// and ignored.
    fn on_capacities(
        &mut self,
        capacities: &HashMap<PathBuf, Option<BinCapacity>>,
    ) -> Vec<Effect<'a>> {
        let Screen::Review(ReviewState::Preparing { paths }) = &self.screen else {
            return Vec::new();
        };
        if capacities.len() != paths.len()
            || !paths.iter().all(|path| capacities.contains_key(path))
        {
            return Vec::new();
        }
        let ticked: Vec<&'a Candidate> = self
            .ticked_rows
            .iter()
            .map(|&row| self.candidates[row])
            .collect();
        let mut review = Review::new(&ticked, &self.report.root, &mut |path| {
            capacities.get(path).copied().flatten()
        });
        let mut replayed = 0;
        for answer in &self.answers {
            match review.step() {
                Some(step) if step.pick == answer.pick && step.kind == answer.kind => {}
                _ => break,
            }
            review.answer(answer.yes);
            replayed += 1;
        }
        self.answers.truncate(replayed);
        self.advance(review)
    }

    fn on_review_key(&mut self, code: KeyCode) -> Vec<Effect<'a>> {
        let answer = match code {
            KeyCode::Esc => {
                self.screen = Screen::List;
                return Vec::new();
            }
            KeyCode::Left => return self.highlight(|_| true),
            KeyCode::Right => return self.highlight(|_| false),
            KeyCode::Tab | KeyCode::BackTab => return self.highlight(|yes| !yes),
            KeyCode::Enter => None,
            KeyCode::Char('y') => Some(true),
            KeyCode::Char('n') => Some(false),
            _ => return Vec::new(),
        };
        self.answer_dialog(answer)
    }

    fn highlight(&mut self, to: fn(bool) -> bool) -> Vec<Effect<'a>> {
        if let Screen::Review(
            ReviewState::Asking { yes, .. } | ReviewState::Confirming { yes, .. },
        ) = &mut self.screen
        {
            *yes = to(*yes);
        }
        Vec::new()
    }

    /// Answers the dialog up: `answer`, or the highlighted answer when `None`.
    fn answer_dialog(&mut self, answer: Option<bool>) -> Vec<Effect<'a>> {
        match std::mem::replace(&mut self.screen, Screen::List) {
            Screen::Review(ReviewState::Asking { mut review, yes }) => {
                let answer = answer.unwrap_or(yes);
                if let Some(step) = review.step() {
                    self.answers.push(Answer {
                        pick: step.pick,
                        kind: step.kind,
                        yes: answer,
                    });
                }
                review.answer(answer);
                self.advance(review)
            }
            Screen::Review(ReviewState::Confirming { decisions, yes, .. }) => {
                if answer.unwrap_or(yes) {
                    self.start_removal(decisions)
                } else {
                    Vec::new()
                }
            }
            other => {
                self.screen = other;
                Vec::new()
            }
        }
    }

    /// Shows the review's next question, or once it is finished the final confirmation; with nothing to remove,
    /// starts the removal at once.
    fn advance(&mut self, review: Review<'a>) -> Vec<Effect<'a>> {
        if let Some(yes) = review.step().map(|step| step.default) {
            self.screen = Screen::Review(ReviewState::Asking { review, yes });
            return Vec::new();
        }
        let totals = review.totals().unwrap_or_default();
        let decisions = review.decisions().unwrap_or_default();
        if totals.recycle + totals.permanent + totals.links + totals.prunes == 0 {
            return self.start_removal(decisions);
        }
        self.screen = Screen::Review(ReviewState::Confirming {
            decisions,
            totals,
            yes: false,
        });
        Vec::new()
    }

    fn start_removal(&mut self, decisions: Vec<Decision<'a>>) -> Vec<Effect<'a>> {
        let rows = decisions
            .iter()
            .map(|decision| RemovalRow::new(decision, &self.report.root))
            .collect();
        self.screen = Screen::Removing(Removal {
            rows,
            ..Removal::default()
        });
        vec![Effect::StartRemoval(decisions)]
    }

    fn on_removal(&mut self, event: RemovalEvent) -> Vec<Effect<'a>> {
        let visible = removal_body_rows(self.size.1);
        let Screen::Removing(removal) = &mut self.screen else {
            return Vec::new();
        };
        match event {
            RemovalEvent::Started(index) => {
                removal.set(index, Status::Removing);
                removal.reveal(index, visible);
            }
            RemovalEvent::Done {
                index,
                outcome,
                notes,
            } => {
                removal.set(index, Status::Finished(outcome));
                if let Some(row) = removal.rows.get_mut(index) {
                    row.notes = notes;
                }
            }
            RemovalEvent::Locked(index) => removal.set(index, Status::Locked),
            RemovalEvent::NeedUnlock(paths) => {
                removal.notice = Some(locked_notice(paths.len()));
                return vec![Effect::Suspend(paths)];
            }
            RemovalEvent::Finished { summary } => {
                removal.total = summary.last().cloned();
                removal.notice = None;
                let finished = std::mem::take(removal);
                self.screen = Screen::Results(finished);
            }
        }
        Vec::new()
    }

    fn on_results_key(&mut self, code: KeyCode) -> Vec<Effect<'a>> {
        let page = removal_body_rows(self.size.1);
        let Screen::Results(results) = &mut self.screen else {
            return Vec::new();
        };
        let last = results
            .body_lines(&self.warnings)
            .len()
            .saturating_sub(page);
        let scroll = match code {
            KeyCode::Enter | KeyCode::Char('q') | KeyCode::Esc => return vec![Effect::Quit(0)],
            KeyCode::Up | KeyCode::Char('k') => results.scroll.saturating_sub(1),
            KeyCode::Down | KeyCode::Char('j') => results.scroll + 1,
            KeyCode::PageUp => results.scroll.saturating_sub(page),
            KeyCode::PageDown => results.scroll + page,
            _ => return Vec::new(),
        };
        results.scroll = scroll.min(last);
        Vec::new()
    }
}

#[cfg(test)]
mod tests {
    use anyhow::{Result, bail, ensure};

    use super::*;
    use crate::discover::OrphanKind;
    use crate::remove::{Action, Method};
    use crate::tui::fixtures::{
        NOW, ROOMY, merged_signals, orphan, registered, report, root, sample_report, utc,
    };
    use crate::tui::review::StepKind;

    fn app(report: &Report) -> App<'_> {
        App::new(report, NOW, (100, 24), true, utc)
    }

    fn press(code: KeyCode) -> Event {
        Event::Key(KeyEvent::new(code, KeyModifiers::NONE))
    }

    fn ctrl_c() -> Event {
        Event::Key(KeyEvent::new(KeyCode::Char('c'), KeyModifiers::CONTROL))
    }

    fn keys(app: &mut App<'_>, codes: &[KeyCode]) {
        for &code in codes {
            app.update(press(code));
        }
    }

    /// Presses Enter and answers the capacity request with a roomy Recycle Bin for every path.
    fn enter_review(app: &mut App<'_>) -> Result<()> {
        enter_review_with(app, Some(ROOMY))
    }

    /// Presses Enter and answers the capacity request with `capacity` for every path.
    fn enter_review_with(app: &mut App<'_>, capacity: Option<BinCapacity>) -> Result<()> {
        let paths = request_capacities(app)?;
        let effects = app.update(Event::Capacities(
            paths.into_iter().map(|path| (path, capacity)).collect(),
        ));
        ensure!(effects.is_empty(), "{effects:?}");
        Ok(())
    }

    /// Presses Enter and returns the paths of the capacity request.
    fn request_capacities(app: &mut App<'_>) -> Result<Vec<PathBuf>> {
        let mut effects = app.update(press(KeyCode::Enter));
        let Some(Effect::ReadCapacities(paths)) = effects.pop() else {
            bail!("no capacity request: {effects:?}");
        };
        Ok(paths)
    }

    /// A branch answer about pick `pick`.
    fn answer(pick: usize, yes: bool) -> Answer {
        Answer {
            pick,
            kind: StepKind::Branch,
            yes,
        }
    }

    #[test]
    fn replay_stops_when_a_new_question_appears() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(
            &mut app,
            &[KeyCode::Down, KeyCode::Down, KeyCode::Char(' ')],
        );
        enter_review(&mut app)?;
        keys(&mut app, &[KeyCode::Char('n'), KeyCode::Esc]);
        ensure!(app.answers == [answer(0, false)], "{:?}", app.answers);
        enter_review_with(&mut app, None)?;
        let (pick, kind, yes) = asking(&app)?;
        ensure!(
            pick == 0 && kind == StepKind::Permanent && !yes,
            "{pick} {kind:?} {yes}"
        );
        ensure!(app.answers.is_empty(), "{:?}", app.answers);
        Ok(())
    }

    #[test]
    fn stale_capacities_reply_is_ignored() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        let first = request_capacities(&mut app)?;
        keys(
            &mut app,
            &[
                KeyCode::Esc,
                KeyCode::Down,
                KeyCode::Down,
                KeyCode::Char(' '),
            ],
        );
        let second = request_capacities(&mut app)?;
        ensure!(
            first.len() == 1 && second.len() == 2,
            "{first:?} {second:?}"
        );
        let stale = first.into_iter().map(|path| (path, Some(ROOMY))).collect();
        let effects = app.update(Event::Capacities(stale));
        ensure!(effects.is_empty(), "{effects:?}");
        ensure!(
            matches!(app.screen, Screen::Review(ReviewState::Preparing { .. })),
            "{:?}",
            app.screen
        );
        let fresh = second.into_iter().map(|path| (path, Some(ROOMY))).collect();
        app.update(Event::Capacities(fresh));
        let (pick, kind, _) = asking(&app)?;
        ensure!(pick == 0 && kind == StepKind::Branch, "{pick} {kind:?}");
        Ok(())
    }

    #[test]
    fn results_can_scroll_to_the_last_warning() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        app.warnings = (0..30)
            .map(|index| format!("warning number {index}"))
            .collect();
        app.screen = Screen::Results(Removal {
            rows: vec![RemovalRow {
                path: r"yaat.wt\held".to_owned(),
                runnable: true,
                status: Status::Finished(Outcome::Pruned),
                notes: Vec::new(),
            }],
            banner: Some(UnlockOutcome::Unlocked),
            ..Removal::default()
        });
        keys(
            &mut app,
            &[KeyCode::PageDown, KeyCode::PageDown, KeyCode::PageDown],
        );
        let visible = removal_body_rows(24);
        let Screen::Results(results) = &app.screen else {
            bail!("not on results");
        };
        let lines = results.body_lines(&app.warnings);
        ensure!(lines.len() == 1 + 2 + 1 + 30, "{}", lines.len());
        ensure!(
            results.scroll == lines.len() - visible,
            "scroll {} of {} lines, {visible} visible",
            results.scroll,
            lines.len()
        );
        let last_shown = lines
            .get(results.scroll + visible - 1)
            .map(|line| line.text.as_str());
        ensure!(
            last_shown == Some("warning: warning number 29"),
            "{last_shown:?}"
        );
        Ok(())
    }

    /// The pick and kind of the question up, and whether its yes answer is highlighted.
    fn asking(app: &App<'_>) -> Result<(usize, StepKind, bool)> {
        match &app.screen {
            Screen::Review(ReviewState::Asking { review, yes }) => review
                .step()
                .map(|step| (step.pick, step.kind, *yes))
                .ok_or_else(|| anyhow::anyhow!("no step")),
            other => bail!("not asking: {other:?}"),
        }
    }

    /// Removes the pre-ticked released worktree (a branch question, then the final confirmation).
    fn start_removing(app: &mut App<'_>) -> Result<()> {
        enter_review(app)?;
        app.update(press(KeyCode::Char('y')));
        let effects = app.update(press(KeyCode::Char('y')));
        ensure!(
            matches!(effects.as_slice(), [Effect::StartRemoval(_)]),
            "{effects:?}"
        );
        Ok(())
    }

    fn removal<'r>(app: &'r App<'_>) -> Result<&'r Removal> {
        match &app.screen {
            Screen::Removing(removal) => Ok(removal),
            other => bail!("not removing: {other:?}"),
        }
    }

    #[test]
    fn space_toggles_tick() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Down, KeyCode::Char(' ')]);
        ensure!(app.ticks == [true, true, false, false], "{:?}", app.ticks);
        keys(&mut app, &[KeyCode::Char(' ')]);
        ensure!(app.ticks == [true, false, false, false], "{:?}", app.ticks);
        Ok(())
    }

    #[test]
    fn tick_all_and_untick_all() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Char('a')]);
        ensure!(app.ticks.iter().all(|&tick| tick), "{:?}", app.ticks);
        keys(&mut app, &[KeyCode::Char('n')]);
        ensure!(app.ticks.iter().all(|&tick| !tick), "{:?}", app.ticks);
        Ok(())
    }

    #[test]
    fn selection_clamps_at_both_ends() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Up, KeyCode::Char('k')]);
        ensure!(app.selected == 0, "{}", app.selected);
        keys(&mut app, &[KeyCode::End]);
        ensure!(app.selected == 3, "{}", app.selected);
        keys(
            &mut app,
            &[KeyCode::Down, KeyCode::Char('j'), KeyCode::PageDown],
        );
        ensure!(app.selected == 3, "{}", app.selected);
        keys(&mut app, &[KeyCode::Home]);
        ensure!(app.selected == 0, "{}", app.selected);
        Ok(())
    }

    #[test]
    fn page_down_moves_by_visible_rows() -> Result<()> {
        let report = report(
            (0..20)
                .map(|index| orphan(&format!(r"yaat.wt\stray-{index:02}"), OrphanKind::Folder))
                .collect(),
        );
        let mut app = App::new(&report, NOW, (80, 20), true, utc);
        let page = table_rows(20);
        ensure!(page == 8, "{page}");
        keys(&mut app, &[KeyCode::PageDown]);
        ensure!(
            app.selected == 8 && app.scroll == 1,
            "{} {}",
            app.selected,
            app.scroll
        );
        keys(&mut app, &[KeyCode::PageDown, KeyCode::PageDown]);
        ensure!(
            app.selected == 19 && app.scroll == 12,
            "{} {}",
            app.selected,
            app.scroll
        );
        keys(&mut app, &[KeyCode::PageUp]);
        ensure!(
            app.selected == 11 && app.scroll == 11,
            "{} {}",
            app.selected,
            app.scroll
        );
        Ok(())
    }

    #[test]
    fn released_rows_start_ticked() -> Result<()> {
        let report = sample_report();
        let app = app(&report);
        ensure!(app.ticks == [true, false, false, false], "{:?}", app.ticks);
        ensure!(
            app.rows[0].flags.starts_with("released"),
            "{}",
            app.rows[0].flags
        );
        Ok(())
    }

    #[test]
    fn enter_with_nothing_ticked_shows_footer_and_stays() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Char('n')]);
        let effects = app.update(press(KeyCode::Enter));
        ensure!(effects.is_empty(), "{effects:?}");
        ensure!(matches!(app.screen, Screen::List), "{:?}", app.screen);
        ensure!(app.message == Some(NOTHING_PICKED), "{:?}", app.message);
        Ok(())
    }

    #[test]
    fn footer_message_clears_on_next_key() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Char('n'), KeyCode::Enter]);
        ensure!(app.message.is_some(), "no message");
        keys(&mut app, &[KeyCode::Down]);
        ensure!(app.message.is_none(), "{:?}", app.message);
        Ok(())
    }

    #[test]
    fn key_release_is_ignored() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        let release = |code| {
            Event::Key(KeyEvent::new_with_kind(
                code,
                KeyModifiers::NONE,
                KeyEventKind::Release,
            ))
        };
        app.update(release(KeyCode::Char(' ')));
        ensure!(app.ticks == [true, false, false, false], "{:?}", app.ticks);
        let effects = app.update(release(KeyCode::Char('q')));
        ensure!(effects.is_empty(), "{effects:?}");
        Ok(())
    }

    #[test]
    fn enter_asks_for_capacities_of_folder_picks_only() -> Result<()> {
        let mut prunable = registered(r"yaat.wt\gone", "gone", merged_signals());
        if let Candidate::Registered(registered) = &mut prunable {
            registered.prunable = Some("gitdir file points to non-existent location".to_owned());
        }
        let report = report(vec![
            orphan(r"yaat.wt\link", OrphanKind::Link),
            orphan(r"yaat.wt\stray", OrphanKind::Folder),
            prunable,
            registered(r"yaat.wt\merged", "merged", merged_signals()),
        ]);
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Char('a')]);
        let effects = app.update(press(KeyCode::Enter));
        let expected = [
            root().join(r"yaat.wt\merged"),
            root().join(r"yaat.wt\stray"),
        ];
        ensure!(
            matches!(effects.as_slice(), [Effect::ReadCapacities(paths)] if paths == &expected),
            "{effects:?}"
        );
        ensure!(
            matches!(app.screen, Screen::Review(ReviewState::Preparing { .. })),
            "{:?}",
            app.screen
        );
        Ok(())
    }

    #[test]
    fn capacities_start_review_at_first_step() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Down, KeyCode::Char(' ')]);
        enter_review(&mut app)?;
        let (pick, kind, yes) = asking(&app)?;
        ensure!(
            pick == 0 && kind == StepKind::Branch && yes,
            "{pick} {kind:?} {yes}"
        );
        ensure!(app.ticked_rows == [0, 1], "{:?}", app.ticked_rows);
        ensure!(app.highlighted_row() == 0, "{}", app.highlighted_row());
        keys(&mut app, &[KeyCode::Char('y')]);
        let (pick, kind, yes) = asking(&app)?;
        ensure!(
            pick == 1 && kind == StepKind::Loss && !yes,
            "{pick} {kind:?} {yes}"
        );
        ensure!(app.highlighted_row() == 1, "{}", app.highlighted_row());
        Ok(())
    }

    #[test]
    fn esc_in_review_keeps_ticks_and_replays_answers() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(
            &mut app,
            &[KeyCode::Down, KeyCode::Down, KeyCode::Char(' ')],
        );
        enter_review(&mut app)?;
        keys(&mut app, &[KeyCode::Char('n'), KeyCode::Esc]);
        ensure!(matches!(app.screen, Screen::List), "{:?}", app.screen);
        ensure!(app.ticks == [true, false, true, false], "{:?}", app.ticks);
        ensure!(app.answers == [answer(0, false)], "{:?}", app.answers);
        enter_review(&mut app)?;
        let (pick, kind, _) = asking(&app)?;
        ensure!(pick == 1 && kind == StepKind::Branch, "{pick} {kind:?}");
        ensure!(app.answers == [answer(0, false)], "{:?}", app.answers);
        Ok(())
    }

    #[test]
    fn changed_ticks_reset_answers() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(
            &mut app,
            &[KeyCode::Down, KeyCode::Down, KeyCode::Char(' ')],
        );
        enter_review(&mut app)?;
        keys(
            &mut app,
            &[
                KeyCode::Char('n'),
                KeyCode::Esc,
                KeyCode::Up,
                KeyCode::Char(' '),
            ],
        );
        enter_review(&mut app)?;
        let (pick, _, _) = asking(&app)?;
        ensure!(pick == 0, "{pick}");
        ensure!(app.answers.is_empty(), "{:?}", app.answers);
        Ok(())
    }

    #[test]
    fn final_confirmation_defaults_to_cancel() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        enter_review(&mut app)?;
        let effects = app.update(press(KeyCode::Enter));
        ensure!(effects.is_empty(), "{effects:?}");
        ensure!(
            matches!(
                app.screen,
                Screen::Review(ReviewState::Confirming { yes: false, .. })
            ),
            "{:?}",
            app.screen
        );
        let effects = app.update(press(KeyCode::Enter));
        ensure!(effects.is_empty(), "{effects:?}");
        ensure!(matches!(app.screen, Screen::List), "{:?}", app.screen);
        ensure!(
            app.ticks[0] && app.answers == [answer(0, true)],
            "{:?} {:?}",
            app.ticks,
            app.answers
        );
        Ok(())
    }

    #[test]
    fn final_confirmation_skipped_when_nothing_runnable() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(
            &mut app,
            &[KeyCode::Char('n'), KeyCode::Down, KeyCode::Char(' ')],
        );
        enter_review(&mut app)?;
        let effects = app.update(press(KeyCode::Enter));
        ensure!(
            matches!(effects.as_slice(), [Effect::StartRemoval(decisions)]
                if matches!(decisions.as_slice(), [decision] if matches!(decision.plan, Plan::Skip(_)))),
            "{effects:?}"
        );
        let removal = removal(&app)?;
        ensure!(
            removal.rows[0].status
                == Status::Finished(Outcome::Skipped("not confirmed".to_owned())),
            "{removal:?}"
        );
        ensure!(removal.progress() == (0, 0), "{:?}", removal.progress());
        Ok(())
    }

    #[test]
    fn remove_answer_emits_start_removal() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        enter_review(&mut app)?;
        keys(&mut app, &[KeyCode::Enter, KeyCode::Left]);
        let effects = app.update(press(KeyCode::Enter));
        ensure!(
            matches!(effects.as_slice(), [Effect::StartRemoval(decisions)]
                if matches!(decisions.as_slice(),
                    [decision] if decision.plan == Plan::Run(Action::Delete(Method::Recycle)))),
            "{effects:?}"
        );
        ensure!(removal(&app)?.progress() == (0, 1), "{:?}", app.screen);
        Ok(())
    }

    #[test]
    fn need_unlock_emits_suspend() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        start_removing(&mut app)?;
        app.update(Event::Removal(RemovalEvent::Locked(0)));
        let held = root().join(r"yaat.wt\held");
        let effects = app.update(Event::Removal(RemovalEvent::NeedUnlock(vec![held.clone()])));
        ensure!(
            matches!(effects.as_slice(), [Effect::Suspend(paths)] if paths == &[held]),
            "{effects:?}"
        );
        let removal_now = removal(&app)?;
        ensure!(
            removal_now.rows[0].status == Status::Locked
                && removal_now.notice.as_deref()
                    == Some("1 pick is locked; leaving the full screen to find what holds them…"),
            "{removal_now:?}"
        );
        app.update(Event::UnlockDone(UnlockOutcome::PartlyUnlocked));
        let removal_now = removal(&app)?;
        ensure!(
            removal_now.banner == Some(UnlockOutcome::PartlyUnlocked)
                && removal_now.notice.is_none(),
            "{removal_now:?}"
        );
        Ok(())
    }

    #[test]
    fn ctrl_c_during_removal_cancels_once() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        start_removing(&mut app)?;
        let effects = app.update(ctrl_c());
        ensure!(
            matches!(effects.as_slice(), [Effect::Cancel]),
            "{effects:?}"
        );
        let effects = app.update(ctrl_c());
        ensure!(effects.is_empty(), "{effects:?}");
        let effects = app.update(press(KeyCode::Char('q')));
        ensure!(effects.is_empty(), "{effects:?}");
        ensure!(removal(&app)?.stopping, "not stopping");
        Ok(())
    }

    #[test]
    fn finished_moves_to_results() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        start_removing(&mut app)?;
        app.update(Event::Removal(RemovalEvent::Started(0)));
        ensure!(
            removal(&app)?.rows[0].status == Status::Removing,
            "{:?}",
            app.screen
        );
        app.update(Event::Removal(RemovalEvent::Done {
            index: 0,
            outcome: Outcome::Recycled { bytes: 1536 },
            notes: vec!["branch held deleted".to_owned()],
        }));
        ensure!(removal(&app)?.progress() == (1, 1), "{:?}", app.screen);
        let total =
            "1 removed, 0 skipped, 0 failed; 1.5 KB freed (1.5 KB of it in the Recycle Bin)";
        app.update(Event::Removal(RemovalEvent::Finished {
            summary: vec![
                r"yaat.wt\held: removed (recycled)".to_owned(),
                total.to_owned(),
            ],
        }));
        let Screen::Results(results) = &app.screen else {
            bail!("not on results: {:?}", app.screen);
        };
        ensure!(results.total.as_deref() == Some(total), "{results:?}");
        ensure!(
            results.rows[0].notes == ["branch held deleted"],
            "{results:?}"
        );
        let effects = app.update(press(KeyCode::Enter));
        ensure!(
            matches!(effects.as_slice(), [Effect::Quit(0)]),
            "{effects:?}"
        );
        Ok(())
    }

    #[test]
    fn resize_below_minimum_shows_too_small_and_back() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report);
        keys(&mut app, &[KeyCode::Down]);
        app.update(Event::Resize(60, 10));
        ensure!(app.too_small(), "not too small at 60x10");
        keys(&mut app, &[KeyCode::Char(' '), KeyCode::Down]);
        ensure!(
            app.ticks == [true, false, false, false] && app.selected == 1,
            "keys acted while too small"
        );
        app.update(Event::Resize(100, 24));
        ensure!(!app.too_small(), "too small at 100x24");
        ensure!(
            matches!(app.screen, Screen::List) && app.selected == 1,
            "{:?}",
            app.screen
        );
        Ok(())
    }

    #[test]
    fn loading_scan_failed_any_key_quits_with_1() -> Result<()> {
        let mut loading = Loading::new(root(), (100, 24));
        let effects = loading.update(press(KeyCode::Char('x')));
        ensure!(effects.is_empty(), "a key quit the scan: {effects:?}");
        loading.failed("cannot read D:\\: access denied".to_owned());
        let effects = loading.update(press(KeyCode::Char('x')));
        ensure!(
            matches!(effects.as_slice(), [Effect::Quit(1)]),
            "{effects:?}"
        );
        Ok(())
    }

    #[test]
    fn loading_empty_any_key_quits_with_0() -> Result<()> {
        let mut loading = Loading::new(root(), (100, 24));
        loading.empty();
        let effects = loading.update(press(KeyCode::Char('x')));
        ensure!(
            matches!(effects.as_slice(), [Effect::Quit(0)]),
            "{effects:?}"
        );
        Ok(())
    }
}
