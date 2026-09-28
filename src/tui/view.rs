//! Draws the picker's state. A function of the state only, so it is tested on ratatui's `TestBackend`.

use std::fmt::Write as _;

use ratatui::Frame;
use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Cell, Clear, Paragraph, Row as TableRow, Table, TableState, Wrap};

use crate::agent::Reason;
use crate::discover::OrphanKind;
use crate::pick;
use crate::report::{
    self, Candidate, OrphanCandidate, RegisteredCandidate, Row, human_bytes, truncate_start,
};
use crate::signals::{MergeState, SizeInfo, Upstream};
use crate::tui::app::{
    App, DETAIL_LINES, Loading, LoadingScreen, MIN_HEIGHT, MIN_WIDTH, Removal, ReviewState, Screen,
    too_small,
};
use crate::tui::review::{StepKind, final_sentence};
use crate::tui::when::format_local;

/// MERGE of work that removing loses: unmerged, or a detached HEAD no default branch contains.
const RISK: Color = Color::Red;
/// MERGE of a branch with no commits of its own; DIRTY and UPSTREAM `+N`.
const CAUTION: Color = Color::Yellow;
/// MERGE of a merged, cherry-picked or squashed branch.
const SAFE: Color = Color::Green;

const SPINNER: [&str; 4] = ["|", "/", "-", "\\"];
const HEADERS: [&str; 9] = [
    "PATH", "KIND", "BRANCH", "MERGE", "DIRTY", "UPSTREAM", "ACTIVE", "SIZE", "FLAGS",
];
const TICK_WIDTH: usize = 3;
const DIALOG_WIDTH: u16 = 72;
const LIST_HINTS: &str = "↑/↓ move  Space tick  a all  n none  Enter review  ? help  q quit";
const HELP: [(&str, &str); 10] = [
    ("↑/↓, k/j", "Move"),
    ("PgUp/PgDn", "Move a page"),
    ("Home/End", "First / last row"),
    ("Space", "Tick or untick the row"),
    ("a", "Tick all"),
    ("n", "Untick all"),
    ("Enter", "Review the ticked rows"),
    ("?", "This help"),
    ("q, Esc", "Quit"),
    ("Ctrl+C", "Quit"),
];

/// Draws the screen `app` is on, or the Too small notice below the minimum size.
pub fn render(app: &App<'_>, frame: &mut Frame<'_>) {
    let area = frame.area();
    if app.too_small() {
        draw_too_small(frame, app.size);
        return;
    }
    match &app.screen {
        Screen::List => draw_list(app, frame, area),
        Screen::Help => {
            draw_list(app, frame, area);
            draw_help(frame, area);
        }
        Screen::Review(state) => {
            draw_list(app, frame, area);
            draw_review(app, state, frame, area);
        }
        Screen::Removing(removal) => draw_removal(app, removal, false, frame),
        Screen::Results(results) => draw_removal(app, results, true, frame),
    }
}

/// Draws the scan's screen, or the Too small notice below the minimum size.
pub fn render_loading(loading: &Loading, frame: &mut Frame<'_>) {
    if too_small(loading.size) {
        draw_too_small(frame, loading.size);
        return;
    }
    let root = loading.root.display();
    let lines: Vec<Line<'_>> = match &loading.screen {
        LoadingScreen::Scanning => vec![
            Line::from(format!(
                "{} Scanning {root}… {}s",
                SPINNER[loading.spinner % SPINNER.len()],
                loading.elapsed_secs
            )),
            Line::from(""),
            Line::from("q or Esc stops."),
        ],
        LoadingScreen::Empty => vec![
            Line::from(format!(
                "No worktrees or orphan folders found under {root}."
            )),
            Line::from(""),
            Line::from("Press any key to exit."),
        ],
        LoadingScreen::Failed(error) => vec![
            Line::from(Span::styled("Scan failed", bold())),
            Line::from(""),
            Line::from(error.as_str()),
            Line::from(""),
            Line::from("Press any key to exit."),
        ],
    };
    let block = Block::bordered().title(" worktree-sweep ");
    frame.render_widget(
        Paragraph::new(lines)
            .wrap(Wrap { trim: false })
            .block(block),
        frame.area(),
    );
}

fn bold() -> Style {
    Style::new().add_modifier(Modifier::BOLD)
}

fn reversed() -> Style {
    Style::new().add_modifier(Modifier::REVERSED)
}

fn draw_too_small(frame: &mut Frame<'_>, (width, height): (u16, u16)) {
    let text = format!("Terminal too small: need {MIN_WIDTH}×{MIN_HEIGHT}, have {width}×{height}.");
    frame.render_widget(
        Paragraph::new(text).wrap(Wrap { trim: false }),
        frame.area(),
    );
}

fn draw_list(app: &App<'_>, frame: &mut Frame<'_>, area: Rect) {
    let [header, table, detail, footer] = Layout::vertical([
        Constraint::Length(1),
        Constraint::Min(0),
        Constraint::Length(DETAIL_LINES),
        Constraint::Length(1),
    ])
    .areas(area);
    frame.render_widget(Paragraph::new(header_line(app)), header);
    draw_table(app, frame, table);
    let lines: Vec<Line<'_>> = fit_detail(
        &detail_lines(app, app.highlighted_row()),
        usize::from(detail.width),
        usize::from(detail.height),
    )
    .into_iter()
    .map(Line::from)
    .collect();
    frame.render_widget(Paragraph::new(lines), detail);
    frame.render_widget(Paragraph::new(footer_line(app)), footer);
}

fn header_line(app: &App<'_>) -> Line<'static> {
    let ticked: Vec<&Candidate> = app
        .candidates
        .iter()
        .zip(&app.ticks)
        .filter_map(|(candidate, &tick)| tick.then_some(*candidate))
        .collect();
    let bytes: u64 = ticked
        .iter()
        .filter_map(|candidate| candidate.size_bytes())
        .sum();
    Line::from(vec![
        Span::styled("worktree-sweep", bold()),
        Span::raw(format!(
            "  {}  {} candidates, {} ticked, {} selected for removal",
            app.report.root.display(),
            app.candidates.len(),
            ticked.len(),
            human_bytes(bytes)
        )),
    ])
}

fn footer_line(app: &App<'_>) -> String {
    let mut footer = app.message.unwrap_or(LIST_HINTS).to_owned();
    match app.warnings.len() {
        0 => {}
        1 => footer.push_str("  · 1 warning"),
        count => {
            let _ = write!(footer, "  · {count} warnings");
        }
    }
    footer
}

fn draw_table(app: &App<'_>, frame: &mut Frame<'_>, area: Rect) {
    let widths = column_widths(&app.rows, area.width);
    let rows = app
        .rows
        .iter()
        .enumerate()
        .map(|(index, row)| table_row(app, index, row, widths[1]));
    let header = TableRow::new(std::iter::once("").chain(HEADERS)).style(bold());
    let highlighted = app.highlighted_row();
    let position = format!("{}/{}", highlighted + 1, app.rows.len());
    let table = Table::new(
        rows,
        widths.map(|width| Constraint::Length(u16::try_from(width).unwrap_or(u16::MAX))),
    )
    .header(header)
    .column_spacing(1)
    .row_highlight_style(reversed())
    .block(Block::bordered().title_bottom(Line::from(position).right_aligned()));
    let mut state = TableState::new()
        .with_offset(app.scroll)
        .with_selected(Some(highlighted));
    frame.render_stateful_widget(table, area, &mut state);
}

/// A row's cells after PATH, in [`HEADERS`] order.
fn cells(row: &Row) -> [&str; 8] {
    [
        row.kind,
        &row.branch,
        &row.merge,
        &row.dirty,
        &row.upstream,
        &row.active,
        &row.size,
        &row.flags,
    ]
}

/// The tick box, PATH and the other columns: each as wide as its widest cell, PATH taking what is left.
fn column_widths(rows: &[Row], width: u16) -> [usize; 10] {
    let mut widths = [0; 10];
    widths[0] = TICK_WIDTH;
    for (column, header) in HEADERS.iter().enumerate().skip(1) {
        widths[column + 1] = rows
            .iter()
            .map(|row| cells(row)[column - 1].chars().count())
            .max()
            .unwrap_or(0)
            .max(header.chars().count());
    }
    let borders_and_spacing = 2 + widths.len() - 1;
    let others: usize = widths.iter().sum();
    widths[1] = usize::from(width)
        .saturating_sub(others + borders_and_spacing)
        .max(HEADERS[0].len());
    widths
}

fn table_row<'r>(app: &App<'_>, index: usize, row: &'r Row, path_width: usize) -> TableRow<'r> {
    let tint = |color: Color| {
        if app.color {
            Style::new().fg(color)
        } else {
            Style::new()
        }
    };
    let caution_if = |yes: bool| if yes { tint(CAUTION) } else { Style::new() };
    let merge = app
        .candidates
        .get(index)
        .and_then(|candidate| merge_color(candidate))
        .map_or_else(Style::new, tint);
    let tick = if app.ticks.get(index).copied().unwrap_or_default() {
        "[x]"
    } else {
        "[ ]"
    };
    TableRow::new(vec![
        Cell::from(tick),
        Cell::from(truncate_start(&row.path, path_width)),
        Cell::from(row.kind),
        Cell::from(row.branch.as_str()),
        Cell::from(row.merge.as_str()).style(merge),
        Cell::from(row.dirty.as_str()).style(caution_if(!row.dirty.is_empty())),
        Cell::from(row.upstream.as_str()).style(caution_if(row.upstream.starts_with('+'))),
        Cell::from(Line::from(row.active.as_str()).right_aligned()),
        Cell::from(Line::from(row.size.as_str()).right_aligned()),
        Cell::from(flags_line(&row.flags)),
    ])
}

fn flags_line(flags: &str) -> Line<'_> {
    match flags.strip_prefix("released") {
        Some(rest) => Line::from(vec![Span::styled("released", bold()), Span::raw(rest)]),
        None => Line::from(flags),
    }
}

/// The MERGE tint of a candidate, from its merge state; `None` for none.
fn merge_color(candidate: &Candidate) -> Option<Color> {
    let Candidate::Registered(registered) = candidate else {
        return None;
    };
    match registered.signals.merge_state? {
        MergeState::Unmerged { .. } | MergeState::Detached { contained: false } => Some(RISK),
        MergeState::NoCommits => Some(CAUTION),
        MergeState::Ancestor | MergeState::PatchesApplied | MergeState::ContentContained => {
            Some(SAFE)
        }
        MergeState::Detached { contained: true } => None,
    }
}

/// The detail pane's seven lines for list row `index`.
fn detail_lines(app: &App<'_>, index: usize) -> Vec<String> {
    let Some(&candidate) = app.candidates.get(index) else {
        return Vec::new();
    };
    let date = |unix: i64| format_local(unix, (app.offset)(unix), app.now);
    let (mut lines, errors) = match candidate {
        Candidate::Registered(registered) => (
            registered_detail(registered, &date),
            registered.signals.errors.join("; "),
        ),
        Candidate::Orphan(orphan) => (orphan_detail(orphan, &date), String::new()),
    };
    lines.push(
        pick::loss_text(candidate).map_or_else(|| "Nothing is lost.".to_owned(), |loss| loss.text),
    );
    lines.push(errors);
    lines
}

fn registered_detail(
    registered: &RegisteredCandidate,
    date: &dyn Fn(i64) -> String,
) -> Vec<String> {
    let signals = &registered.signals;
    let branch = match (&registered.branch, &registered.head) {
        (Some(branch), _) => format!("branch {branch}"),
        (None, Some(head)) => format!("detached {}", head.get(..7).unwrap_or(head)),
        (None, None) => "detached".to_owned(),
    };
    let against = signals
        .merge_state_against
        .as_deref()
        .unwrap_or("the default branch");
    let mut state = Vec::new();
    state.extend(signals.merge_state.map(|merge| merge_words(merge, against)));
    state.extend(
        signals
            .dirty
            .map(|dirty| match (dirty.modified, dirty.untracked) {
                (0, 0) => "clean".to_owned(),
                (modified, untracked) => format!("{modified} modified, {untracked} untracked"),
            }),
    );
    state.extend(signals.upstream.map(|upstream| match upstream {
        Upstream::None => "no upstream".to_owned(),
        Upstream::Gone => "upstream gone".to_owned(),
        Upstream::Tracking { ahead: 0 } => "pushed".to_owned(),
        Upstream::Tracking { ahead } => format!("+{ahead} not pushed"),
    }));
    let mut activity = Vec::new();
    activity.extend(
        signals
            .last_activity_unix
            .map(|then| format!("last activity {}", date(then))),
    );
    activity.extend(signals.size.map(size_words));
    let mut marks = Vec::new();
    marks.extend(registered.git_lock.as_deref().map(|reason| match reason {
        "" => "git-locked".to_owned(),
        reason => format!("git-locked: {reason}"),
    }));
    marks.extend(
        registered
            .prunable
            .as_deref()
            .map(|why| format!("prunable: {why}")),
    );
    marks.extend(registered.released.as_ref().map(|released| {
        format!(
            "released {}: {}",
            date(released.released_at),
            reason_label(released.reason)
        )
    }));
    vec![
        registered.path.display().to_string(),
        format!("repo {}; {branch}", registered.repo.display()),
        state.join("; "),
        activity.join("; "),
        marks.join("; "),
    ]
}

fn orphan_detail(candidate: &OrphanCandidate, date: &dyn Fn(i64) -> String) -> Vec<String> {
    let orphan = &candidate.orphan;
    let is_link = orphan.orphan_kind == OrphanKind::Link;
    let mut notes = Vec::new();
    notes.extend(
        orphan
            .link_target
            .as_ref()
            .map(|target| format!("→ {}, not touched", target.display())),
    );
    notes.extend(orphan.live_gitdir.as_deref().map(|gitdir| {
        format!(
            "still registered in {}",
            pick::repo_of_gitdir(gitdir).display()
        )
    }));
    if orphan.stale_gitdir {
        notes.push("stale .git".to_owned());
    }
    let mut activity = Vec::new();
    activity.extend(
        candidate
            .size
            .last_write_unix
            .map(|then| format!("last write {}", date(then))),
    );
    if !is_link {
        activity.push(size_words(candidate.size));
    }
    vec![
        orphan.path.display().to_string(),
        format!(
            "container {}; {}",
            orphan.container.display(),
            if is_link { "link" } else { "folder" }
        ),
        notes.join("; "),
        activity.join("; "),
        String::new(),
    ]
}

fn merge_words(state: MergeState, against: &str) -> String {
    match state {
        MergeState::Ancestor => format!("merged into {against}"),
        MergeState::NoCommits => format!("no commits of its own (against {against})"),
        MergeState::PatchesApplied => format!("cherry-picked into {against}"),
        MergeState::ContentContained => format!("squashed into {against}"),
        MergeState::Unmerged { commits: 1 } => format!("1 commit not on {against}"),
        MergeState::Unmerged { commits } => format!("{commits} commits not on {against}"),
        MergeState::Detached { contained: true } => format!("detached, contained in {against}"),
        MergeState::Detached { contained: false } => {
            format!("detached, not contained in {against}")
        }
    }
}

fn size_words(size: SizeInfo) -> String {
    let mut words = format!("{}, {} files", human_bytes(size.bytes), size.files);
    if size.unreadable > 0 {
        let _ = write!(words, ", {} unreadable", size.unreadable);
    }
    words
}

fn reason_label(reason: Reason) -> &'static str {
    match reason {
        Reason::Locked => "locked",
        Reason::MayHold => "may be held",
        Reason::TooBigForRecycleBin => "too big for the Recycle Bin",
        Reason::ShellTimeout => "the Shell timed out",
        Reason::WouldLose => "would lose work",
        Reason::CallerHolds => "held by the calling shell",
        Reason::NotRemovable(_) => "not removable",
    }
}

fn draw_help(frame: &mut Frame<'_>, area: Rect) {
    let mut lines: Vec<Line<'_>> = HELP
        .iter()
        .map(|(key, what)| {
            Line::from(vec![
                Span::styled(format!("{key:<12}"), bold()),
                Span::raw(*what),
            ])
        })
        .collect();
    lines.push(Line::from(""));
    lines.push(Line::from("Any key closes this."));
    let height = u16::try_from(lines.len() + 2).unwrap_or(u16::MAX);
    let box_area = area.centered(Constraint::Length(44), Constraint::Length(height));
    frame.render_widget(Clear, box_area);
    frame.render_widget(
        Paragraph::new(lines).block(Block::bordered().title(" Keys ")),
        box_area,
    );
}

/// A dialog's title, its text, its yes and no answers, and which of them is the default and which highlighted.
struct Dialog {
    title: &'static str,
    text: Vec<String>,
    answers: Option<Answers>,
}

struct Answers {
    yes: &'static str,
    no: &'static str,
    default_yes: bool,
    highlight_yes: bool,
}

/// A step's dialog title and its yes and no answers.
fn step_labels(kind: StepKind) -> (&'static str, &'static str, &'static str) {
    match kind {
        StepKind::Loss => ("Would lose work", "Remove anyway", "Keep"),
        StepKind::Link => ("Link", "Remove the link", "Keep"),
        StepKind::Permanent => ("Permanent delete", "Delete permanently", "Skip it"),
        StepKind::Branch => (
            "Delete branch",
            "Delete branch after removing",
            "Keep branch",
        ),
    }
}

fn dialog(app: &App<'_>, state: &ReviewState<'_>) -> Option<Dialog> {
    match state {
        ReviewState::Preparing { .. } => Some(Dialog {
            title: "Review",
            text: vec!["Reading the Recycle Bin settings…".to_owned()],
            answers: None,
        }),
        ReviewState::Asking { review, yes } => {
            let step = review.step()?;
            let (title, yes_label, no_label) = step_labels(step.kind);
            let path = app
                .ticked_rows
                .get(step.pick)
                .and_then(|&row| app.candidates.get(row))
                .map(|candidate| report::relative_path(candidate.path(), &app.report.root))
                .unwrap_or_default();
            let mut text = Vec::new();
            if step.kind != StepKind::Permanent {
                text.extend([path, String::new()]);
            }
            text.extend([step.body.clone(), String::new(), step.question.to_owned()]);
            Some(Dialog {
                title,
                text,
                answers: Some(Answers {
                    yes: yes_label,
                    no: no_label,
                    default_yes: step.default,
                    highlight_yes: *yes,
                }),
            })
        }
        ReviewState::Confirming { totals, yes, .. } => Some(Dialog {
            title: "Remove?",
            text: vec![final_sentence(totals)],
            answers: Some(Answers {
                yes: "Remove",
                no: "Cancel",
                default_yes: false,
                highlight_yes: *yes,
            }),
        }),
    }
}

fn answer_span(label: &str, default: bool, highlighted: bool) -> Span<'static> {
    let mut style = Style::new();
    if default {
        style = style.add_modifier(Modifier::BOLD);
    }
    if highlighted {
        style = style.add_modifier(Modifier::REVERSED);
    }
    Span::styled(format!("[ {label} ]"), style)
}

fn draw_review(app: &App<'_>, state: &ReviewState<'_>, frame: &mut Frame<'_>, area: Rect) {
    let Some(dialog) = dialog(app, state) else {
        return;
    };
    let width = DIALOG_WIDTH.min(area.width.saturating_sub(4));
    let inner = usize::from(width.saturating_sub(2));
    let text: Vec<Line<'_>> = dialog
        .text
        .iter()
        .flat_map(|line| wrap_text(line, inner))
        .map(Line::from)
        .collect();
    // The answers get their own line below a blank one, so a text too tall for the screen is cut, never them.
    let answer_rows = if dialog.answers.is_some() { 2 } else { 0 };
    let height = u16::try_from(text.len() + answer_rows + 2)
        .unwrap_or(u16::MAX)
        .min(area.height);
    let box_area = area.centered(Constraint::Length(width), Constraint::Length(height));
    let block = Block::bordered().title(format!(" {} ", dialog.title));
    let inside = block.inner(box_area);
    frame.render_widget(Clear, box_area);
    frame.render_widget(block, box_area);
    let [text_area, answers_area] = Layout::vertical([
        Constraint::Min(0),
        Constraint::Length(u16::try_from(answer_rows).unwrap_or_default()),
    ])
    .areas(inside);
    frame.render_widget(Paragraph::new(text), text_area);
    if let Some(answers) = dialog.answers {
        let line = Line::from(vec![
            answer_span(answers.yes, answers.default_yes, answers.highlight_yes),
            Span::raw("  "),
            answer_span(answers.no, !answers.default_yes, !answers.highlight_yes),
        ])
        .centered();
        frame.render_widget(Paragraph::new(vec![Line::from(""), line]), answers_area);
    }
}

/// `text` split into lines at most `width` characters wide: at spaces where it can, inside a word where the word
/// alone is wider. Always at least one line.
fn wrap_text(text: &str, width: usize) -> Vec<String> {
    let width = width.max(1);
    let mut lines = Vec::new();
    let mut line = String::new();
    let mut len = 0;
    for word in text.split(' ') {
        let word_len = word.chars().count();
        if len > 0 && len + 1 + word_len > width {
            lines.push(std::mem::take(&mut line));
            len = 0;
        }
        if len > 0 {
            line.push(' ');
            len += 1;
        }
        for character in word.chars() {
            if len == width {
                lines.push(std::mem::take(&mut line));
                len = 0;
            }
            line.push(character);
            len += 1;
        }
    }
    lines.push(line);
    lines
}

/// The detail pane's lines wrapped to `width` and fitted into `height` rows. Line 6 (what removing loses) is always
/// kept: lines 7, 5 and 4 go first, in that order, then the end of what comes before line 6.
fn fit_detail(lines: &[String], width: usize, height: usize) -> Vec<String> {
    const LOSS: usize = 5;
    let mut wrapped: Vec<Option<Vec<String>>> = lines
        .iter()
        .map(|line| Some(wrap_text(line, width)))
        .collect();
    let total =
        |wrapped: &[Option<Vec<String>>]| wrapped.iter().flatten().map(Vec::len).sum::<usize>();
    for drop in [6, 4, 3] {
        if total(&wrapped) <= height {
            break;
        }
        if let Some(slot) = wrapped.get_mut(drop) {
            *slot = None;
        }
    }
    let after: Vec<String> = wrapped
        .iter()
        .skip(LOSS)
        .flatten()
        .flatten()
        .cloned()
        .collect();
    let budget = height.saturating_sub(after.len());
    let mut fitted: Vec<String> = wrapped
        .iter()
        .take(LOSS)
        .flatten()
        .flatten()
        .take(budget)
        .cloned()
        .collect();
    fitted.extend(after);
    fitted
}

fn draw_removal(app: &App<'_>, removal: &Removal, finished: bool, frame: &mut Frame<'_>) {
    let [title, body, total, footer] = Layout::vertical([
        Constraint::Length(1),
        Constraint::Min(0),
        Constraint::Length(1),
        Constraint::Length(1),
    ])
    .areas(frame.area());
    let (done, runnable) = removal.progress();
    let heading = if finished {
        "Results".to_owned()
    } else if removal.stopping {
        format!("Removing  {done}/{runnable}  stopping after the current item…")
    } else {
        format!("Removing  {done}/{runnable}")
    };
    frame.render_widget(Paragraph::new(Span::styled(heading, bold())), title);
    let lines = removal_lines(app, removal);
    let scroll = u16::try_from(removal.scroll).unwrap_or(u16::MAX);
    frame.render_widget(
        Paragraph::new(lines)
            .block(Block::bordered())
            .scroll((scroll, 0)),
        body,
    );
    if finished {
        let total_line = removal.total.clone().unwrap_or_default();
        frame.render_widget(Paragraph::new(total_line), total);
    }
    let hint = if finished {
        "Enter, q or Esc exits  ↑/↓ PgUp/PgDn scroll"
    } else {
        "Ctrl+C stops after the current item"
    };
    frame.render_widget(Paragraph::new(hint), footer);
}

fn removal_lines(app: &App<'_>, removal: &Removal) -> Vec<Line<'static>> {
    removal
        .body_lines(&app.warnings)
        .into_iter()
        .map(|line| {
            if line.bold {
                Line::from(Span::styled(line.text, bold()))
            } else {
                Line::from(line.text)
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use anyhow::{Result, ensure};
    use ratatui::Terminal;
    use ratatui::backend::TestBackend;
    use ratatui::buffer::Buffer;
    use ratatui::crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
    use ratatui::style::{Color, Modifier};

    use super::*;
    use crate::remove::Outcome;
    use crate::report::Report;
    use crate::tui::app::{Event, Removal, RemovalEvent, RemovalRow, Screen, Status};
    use crate::tui::fixtures::{
        NOW, ROOMY, dirty, merged_signals, registered, report, root, sample_report, utc,
    };

    fn draw(width: u16, height: u16, paint: impl FnOnce(&mut Frame<'_>)) -> Result<Buffer> {
        let mut terminal = Terminal::new(TestBackend::new(width, height))?;
        terminal.draw(paint)?;
        Ok(terminal.backend().buffer().clone())
    }

    fn lines(buffer: &Buffer) -> Vec<String> {
        (0..buffer.area.height)
            .map(|y| {
                (0..buffer.area.width)
                    .map(|x| buffer[(x, y)].symbol())
                    .collect()
            })
            .collect()
    }

    fn line_with<'l>(lines: &'l [String], needle: &str) -> Result<&'l String> {
        lines
            .iter()
            .find(|line| line.contains(needle))
            .ok_or_else(|| anyhow::anyhow!("no line contains {needle:?}:\n{}", lines.join("\n")))
    }

    fn app(report: &Report, color: bool) -> App<'_> {
        App::new(report, NOW, (100, 24), color, utc)
    }

    fn press(app: &mut App<'_>, code: KeyCode) {
        app.update(Event::Key(KeyEvent::new(code, KeyModifiers::NONE)));
    }

    #[test]
    fn list_at_100x24_shows_pre_ticked_released_row_and_truncated_path() -> Result<()> {
        let report = sample_report();
        let app = app(&report, true);
        let buffer = draw(100, 24, |frame| render(&app, frame))?;
        let lines = lines(&buffer);
        ensure!(
            lines[0].starts_with(
                r"worktree-sweep  D:\  4 candidates, 1 ticked, 1.5 KB selected for removal"
            ),
            "{}",
            lines[0]
        );
        let held = line_with(&lines, r"yaat.wt\held")?;
        ensure!(held.contains("[x]") && held.contains("released"), "{held}");
        let dirty = line_with(&lines, "unmerged 4")?;
        ensure!(
            dirty.contains("[ ] …") && dirty.contains(r"-folder\wip"),
            "{dirty}"
        );
        line_with(&lines, "Nothing is lost.")?;
        line_with(&lines, "released 2026-09-20 14:13 (24h ago): locked")?;
        Ok(())
    }

    #[test]
    fn loss_dialog_shows_path_body_and_default_keep() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report, true);
        press(&mut app, KeyCode::Char('n'));
        press(&mut app, KeyCode::Down);
        press(&mut app, KeyCode::Char(' '));
        press(&mut app, KeyCode::Enter);
        let dirty = root().join(format!(r"yaat.wt\{}\wip", "deeply-nested-folder".repeat(4)));
        app.update(Event::Capacities(
            [(dirty, Some(ROOMY))].into_iter().collect(),
        ));
        let buffer = draw(100, 24, |frame| render(&app, frame))?;
        let lines = lines(&buffer);
        line_with(&lines, "Would lose work")?;
        line_with(&lines, r"yaat.wt\deeply-nested-folder")?;
        line_with(&lines, "3 modified, 2 untracked files")?;
        let answers = line_with(&lines, "[ Keep ]")?;
        ensure!(answers.contains("[ Remove anyway ]"), "{answers}");
        let row = lines
            .iter()
            .position(|line| line == answers)
            .unwrap_or_default();
        let byte = answers.find("[ Keep ]").unwrap_or_default();
        let column = answers[..byte].chars().count();
        let keep = &buffer[(u16::try_from(column + 2)?, u16::try_from(row)?)];
        ensure!(keep.symbol() == "K", "{keep:?}");
        ensure!(
            keep.modifier.contains(Modifier::REVERSED),
            "Keep is not highlighted: {keep:?}"
        );
        Ok(())
    }

    #[test]
    fn too_small_at_60x10() -> Result<()> {
        let report = sample_report();
        let app = App::new(&report, NOW, (60, 10), true, utc);
        let buffer = draw(60, 10, |frame| render(&app, frame))?;
        let lines = lines(&buffer);
        ensure!(
            lines[0].trim_end() == "Terminal too small: need 80×20, have 60×10.",
            "{}",
            lines.join("\n")
        );
        ensure!(
            lines[1..].iter().all(|line| line.trim().is_empty()),
            "{}",
            lines.join("\n")
        );
        Ok(())
    }

    #[test]
    fn results_shows_total_line() -> Result<()> {
        let report = sample_report();
        let mut app = app(&report, true);
        let total =
            "1 removed, 0 skipped, 0 failed; 1.5 KB freed (1.5 KB of it in the Recycle Bin)";
        app.screen = Screen::Results(Removal {
            rows: vec![RemovalRow {
                path: r"yaat.wt\held".to_owned(),
                runnable: true,
                status: Status::Finished(Outcome::Recycled { bytes: 1536 }),
                notes: vec!["branch held deleted".to_owned()],
            }],
            total: Some(total.to_owned()),
            ..Removal::default()
        });
        let buffer = draw(100, 24, |frame| render(&app, frame))?;
        let lines = lines(&buffer);
        line_with(&lines, total)?;
        let row = line_with(&lines, r"yaat.wt\held")?;
        ensure!(row.contains("removed (recycled)"), "{row}");
        line_with(&lines, "    branch held deleted")?;
        Ok(())
    }

    #[test]
    fn no_color_draws_no_colours() -> Result<()> {
        let report = sample_report();
        let tinted = app(&report, true);
        let buffer = draw(100, 24, |frame| render(&tinted, frame))?;
        ensure!(
            buffer.content().iter().any(|cell| cell.fg != Color::Reset),
            "the coloured list has no colour"
        );
        let plain = app(&report, false);
        let buffer = draw(100, 24, |frame| render(&plain, frame))?;
        let coloured: Vec<_> = buffer
            .content()
            .iter()
            .filter(|cell| cell.fg != Color::Reset)
            .collect();
        ensure!(coloured.is_empty(), "coloured cells: {coloured:?}");
        Ok(())
    }

    #[test]
    fn scanning_shows_root_and_spinner() -> Result<()> {
        let mut loading = Loading::new(root(), (80, 20));
        loading.update(Event::Tick);
        loading.elapsed_secs = 3;
        let buffer = draw(80, 20, |frame| render_loading(&loading, frame))?;
        let lines = lines(&buffer);
        line_with(&lines, r"/ Scanning D:\… 3s")?;
        Ok(())
    }

    #[test]
    fn permanent_dialog_keeps_answers_visible_at_80x20() -> Result<()> {
        let long = format!(
            r"yaat.wt\{}\big",
            "a-rather-long-folder-name ".repeat(12).trim_end()
        );
        let report = report(vec![registered(&long, "big", merged_signals())]);
        let mut app = App::new(&report, NOW, (80, 20), true, utc);
        press(&mut app, KeyCode::Char(' '));
        press(&mut app, KeyCode::Enter);
        app.update(Event::Capacities(
            [(root().join(&long), None)].into_iter().collect(),
        ));
        let buffer = draw(80, 20, |frame| render(&app, frame))?;
        let lines = lines(&buffer);
        line_with(&lines, "Permanent delete")?;
        let answers = line_with(&lines, "[ Delete permanently ]")?;
        ensure!(answers.contains("[ Skip it ]"), "{answers}");
        let path_starts = lines
            .iter()
            .filter(|line| line.contains(r"│yaat.wt\a-rather"))
            .count();
        ensure!(
            path_starts == 1,
            "the path is shown {path_starts} times:\n{}",
            lines.join("\n")
        );
        Ok(())
    }

    #[test]
    fn detail_pane_keeps_loss_line_with_deep_path() -> Result<()> {
        let deep = format!(r"yaat.wt\{}\wip", "deeply-nested-folder".repeat(10));
        let report = report(vec![dirty(&deep)]);
        let app = App::new(&report, NOW, (80, 20), true, utc);
        let buffer = draw(80, 20, |frame| render(&app, frame))?;
        let lines = lines(&buffer);
        line_with(
            &lines,
            "3 modified, 2 untracked files and 4 commits not on main will be lost.",
        )?;
        line_with(&lines, r"D:\yaat.wt\deeply-nested-folder")?;
        Ok(())
    }

    #[test]
    fn removing_keeps_the_started_row_visible() -> Result<()> {
        let report = sample_report();
        let mut app = App::new(&report, NOW, (80, 20), true, utc);
        app.screen = Screen::Removing(Removal {
            rows: (0..25)
                .map(|index| RemovalRow {
                    path: format!(r"yaat.wt\stray-{index:02}"),
                    runnable: true,
                    status: Status::Pending,
                    notes: Vec::new(),
                })
                .collect(),
            ..Removal::default()
        });
        app.update(Event::Removal(RemovalEvent::Started(24)));
        let buffer = draw(80, 20, |frame| render(&app, frame))?;
        let lines = lines(&buffer);
        let row = line_with(&lines, r"yaat.wt\stray-24")?;
        ensure!(row.contains("removing…"), "{row}");
        Ok(())
    }
}
