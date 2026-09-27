//! The scan report, its table and its JSON form.

use std::fmt::Write as _;
use std::io::Write;
use std::path::{Component, Path, PathBuf};

use anyhow::{Context, Result};
use serde::Serialize;

use crate::discover::{Orphan, OrphanKind, path_key};
use crate::signals::{DefaultBranches, MergeState, SizeInfo, Upstream, WorktreeSignals};

/// Everything a scan found, as printed by `--json`.
#[derive(Debug, Clone, Serialize)]
pub struct Report {
    /// The folder scanned.
    pub root: PathBuf,
    /// Repos at depth 1 of the root.
    pub repos: Vec<RepoReport>,
    /// Folders the tool offers to remove.
    pub candidates: Vec<Candidate>,
}

/// A repo and its default branches.
#[derive(Debug, Clone, Serialize)]
pub struct RepoReport {
    /// The repo's main worktree.
    pub path: PathBuf,
    /// The branches merge states are measured against.
    pub default_branches: DefaultBranches,
}

/// A folder the tool offers to remove.
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Candidate {
    /// A linked worktree some repo registers.
    Registered(RegisteredCandidate),
    /// A folder in a container dir that no repo registers.
    Orphan(OrphanCandidate),
}

impl Candidate {
    /// The candidate's folder or link.
    #[must_use]
    pub fn path(&self) -> &Path {
        match self {
            Self::Registered(registered) => &registered.path,
            Self::Orphan(orphan) => &orphan.orphan.path,
        }
    }

    /// Bytes on disk; `None` when unknown (a prunable registration). A link counts as zero.
    #[must_use]
    pub fn size_bytes(&self) -> Option<u64> {
        match self {
            Self::Registered(registered) => registered.signals.size.map(|size| size.bytes),
            Self::Orphan(orphan) => Some(orphan.size.bytes),
        }
    }
}

/// A registered worktree and its signals.
#[derive(Debug, Clone, Serialize)]
pub struct RegisteredCandidate {
    /// The worktree folder.
    pub path: PathBuf,
    /// The repo that registers it.
    pub repo: PathBuf,
    /// The branch checked out; `None` when detached.
    pub branch: Option<String>,
    /// The commit checked out.
    pub head: Option<String>,
    /// Why git considers the registration prunable; `None` when its folder exists.
    pub prunable: Option<String>,
    /// The `git worktree lock` reason; `Some("")` when locked without one.
    pub git_lock: Option<String>,
    /// Merge state, dirty counts, upstream, last activity and size.
    #[serde(flatten)]
    pub signals: WorktreeSignals,
}

/// An orphan folder or link and its size.
#[derive(Debug, Clone, Serialize)]
pub struct OrphanCandidate {
    /// Where it is and what it is.
    #[serde(flatten)]
    pub orphan: Orphan,
    /// Disk usage; a link measures as empty, its target never counted.
    pub size: SizeInfo,
}

/// Writes the report as one pretty-printed JSON document followed by a newline.
///
/// # Errors
///
/// When serialising or writing fails.
pub fn write_json(report: &Report, out: &mut impl Write) -> Result<()> {
    serde_json::to_writer_pretty(&mut *out, report).context("cannot write the JSON report")?;
    writeln!(out).context("cannot write the JSON report")?;
    Ok(())
}

/// The widest line the table is laid out for.
pub const TABLE_WIDTH: usize = 150;
const PICKER_WIDTH: usize = 140;
const SEPARATOR: &str = "  ";
const MIN_PATH_WIDTH: usize = 24;
const MAX_BRANCH_WIDTH: usize = 28;

/// The candidates in table order: registered worktrees grouped by repo, then orphans, each by path.
#[must_use]
pub fn ordered(report: &Report) -> Vec<&Candidate> {
    let mut candidates: Vec<&Candidate> = report.candidates.iter().collect();
    candidates.sort_by_cached_key(|candidate| match candidate {
        Candidate::Registered(registered) => {
            (0, path_key(&registered.repo), path_key(&registered.path))
        }
        Candidate::Orphan(orphan) => (1, String::new(), path_key(&orphan.orphan.path)),
    });
    candidates
}

/// Renders the candidates as a plain-text table no wider than [`TABLE_WIDTH`], its rows in [`ordered`] order and
/// numbered from 1. A path too long for its column keeps its end and loses its start to `…`. `now_unix` is the
/// time ages are measured from.
#[must_use]
pub fn render_table(report: &Report, now_unix: i64) -> String {
    let candidates = ordered(report);
    if candidates.is_empty() {
        return format!(
            "No worktrees or orphan folders found under {}.\n",
            report.root.display()
        );
    }
    let rows: Vec<Row> = candidates
        .iter()
        .map(|candidate| Row::new(candidate, &report.root, now_unix))
        .collect();
    let mut columns = vec![Column::new(
        "#",
        true,
        (1..=rows.len()).map(|index| index.to_string()).collect(),
    )];
    columns.extend(row_columns(&rows));
    let mut table = String::new();
    for line in layout(&columns, 1, TABLE_WIDTH, true) {
        table.push_str(&line);
        table.push('\n');
    }
    table
}

/// One line per candidate, in [`ordered`] order, for the picker: the table's columns without the index and the
/// header, laid out to fit a checkbox list.
#[must_use]
pub fn picker_items(report: &Report, now_unix: i64) -> Vec<String> {
    let rows: Vec<Row> = ordered(report)
        .iter()
        .map(|candidate| Row::new(candidate, &report.root, now_unix))
        .collect();
    layout(&row_columns(&rows), 0, PICKER_WIDTH, false)
}

/// `path` relative to `root` when it lies under it (compared case-insensitively on Windows), else `path` whole.
#[must_use]
pub fn relative_path(path: &Path, root: &Path) -> String {
    let mut rest = path.components();
    for root_component in root.components() {
        match rest.next() {
            Some(component) if same_component(component, root_component) => {}
            _ => return path.display().to_string(),
        }
    }
    let relative = rest.as_path();
    if relative.as_os_str().is_empty() {
        path.display().to_string()
    } else {
        relative.display().to_string()
    }
}

fn same_component(a: Component<'_>, b: Component<'_>) -> bool {
    let a = a.as_os_str().to_string_lossy();
    let b = b.as_os_str().to_string_lossy();
    if cfg!(windows) {
        a.to_lowercase() == b.to_lowercase()
    } else {
        a == b
    }
}

/// A byte count with one decimal in binary units (`512 B`, `1.5 KB`, `28.9 GB`).
#[must_use]
pub fn human_bytes(bytes: u64) -> String {
    const UNITS: [&str; 5] = ["KB", "MB", "GB", "TB", "PB"];
    if bytes < 1024 {
        return format!("{bytes} B");
    }
    let mut unit_size: u128 = 1024;
    let mut unit = UNITS[0];
    for (index, name) in UNITS.iter().enumerate().skip(1) {
        let next = 1_u128 << (10 * (index + 1));
        if u128::from(bytes) < next {
            break;
        }
        unit_size = next;
        unit = name;
    }
    let tenths = (u128::from(bytes) * 10 + unit_size / 2) / unit_size;
    format!("{}.{} {unit}", tenths / 10, tenths % 10)
}

/// How long ago `then_unix` was, in the largest fitting unit: `5m`, `7h`, `3d`, `5w`, `2y`.
#[must_use]
pub fn age(then_unix: i64, now_unix: i64) -> String {
    const HOUR: i64 = 3600;
    const DAY: i64 = 24 * HOUR;
    let elapsed = now_unix.saturating_sub(then_unix).max(0);
    if elapsed < HOUR {
        format!("{}m", elapsed / 60)
    } else if elapsed < 2 * DAY {
        format!("{}h", elapsed / HOUR)
    } else if elapsed < 14 * DAY {
        format!("{}d", elapsed / DAY)
    } else if elapsed < 365 * DAY {
        format!("{}w", elapsed / (7 * DAY))
    } else {
        format!("{}y", elapsed / (365 * DAY))
    }
}

/// The table cells of one candidate.
struct Row {
    path: String,
    kind: &'static str,
    branch: String,
    merge: String,
    dirty: String,
    upstream: String,
    active: String,
    size: String,
    flags: String,
}

impl Row {
    fn new(candidate: &Candidate, root: &Path, now_unix: i64) -> Self {
        match candidate {
            Candidate::Registered(registered) => Self::registered(registered, root, now_unix),
            Candidate::Orphan(orphan) => Self::orphan(orphan, root, now_unix),
        }
    }

    fn registered(registered: &RegisteredCandidate, root: &Path, now_unix: i64) -> Self {
        let signals = &registered.signals;
        let mut flags = Vec::new();
        if registered.git_lock.is_some() {
            flags.push("git-locked");
        }
        if registered.prunable.is_some() {
            flags.push("prunable");
        }
        Self {
            path: relative_path(&registered.path, root),
            kind: "worktree",
            branch: truncate_end(
                registered.branch.as_deref().unwrap_or_default(),
                MAX_BRANCH_WIDTH,
            ),
            merge: signals.merge_state.map(merge_word).unwrap_or_default(),
            dirty: signals
                .dirty
                .map(|dirty| dirty_cell(dirty.modified, dirty.untracked))
                .unwrap_or_default(),
            upstream: match signals.upstream {
                Some(Upstream::Gone) => "gone".to_owned(),
                Some(Upstream::Tracking { ahead }) if ahead > 0 => format!("+{ahead}"),
                _ => String::new(),
            },
            active: signals
                .last_activity_unix
                .map(|then| age(then, now_unix))
                .unwrap_or_default(),
            size: signals
                .size
                .map(|size| human_bytes(size.bytes))
                .unwrap_or_default(),
            flags: flags.join(", "),
        }
    }

    fn orphan(candidate: &OrphanCandidate, root: &Path, now_unix: i64) -> Self {
        let orphan = &candidate.orphan;
        let is_link = orphan.orphan_kind == OrphanKind::Link;
        let mut flags = Vec::new();
        if orphan.stale_gitdir {
            flags.push("stale .git");
        }
        if orphan.live_gitdir.is_some() {
            flags.push("registered elsewhere");
        }
        Self {
            path: relative_path(&orphan.path, root),
            kind: if is_link { "link" } else { "orphan" },
            branch: String::new(),
            merge: String::new(),
            dirty: String::new(),
            upstream: String::new(),
            active: candidate
                .size
                .last_write_unix
                .map(|then| age(then, now_unix))
                .unwrap_or_default(),
            size: if is_link {
                String::new()
            } else {
                human_bytes(candidate.size.bytes)
            },
            flags: flags.join(", "),
        }
    }
}

fn merge_word(state: MergeState) -> String {
    match state {
        MergeState::Ancestor => "merged".to_owned(),
        MergeState::NoCommits => "no commits".to_owned(),
        MergeState::PatchesApplied => "cherry-picked".to_owned(),
        MergeState::ContentContained => "squashed".to_owned(),
        MergeState::Unmerged { commits } => format!("unmerged {commits}"),
        MergeState::Detached { .. } => "detached".to_owned(),
    }
}

fn dirty_cell(modified: u32, untracked: u32) -> String {
    let mut parts = Vec::new();
    if modified > 0 {
        parts.push(format!("{modified}M"));
    }
    if untracked > 0 {
        parts.push(format!("{untracked}?"));
    }
    parts.join(" ")
}

struct Column {
    header: &'static str,
    right_aligned: bool,
    cells: Vec<String>,
}

impl Column {
    fn new(header: &'static str, right_aligned: bool, cells: Vec<String>) -> Self {
        Self {
            header,
            right_aligned,
            cells,
        }
    }
}

fn row_columns(rows: &[Row]) -> Vec<Column> {
    let cells = |cell: fn(&Row) -> String| rows.iter().map(cell).collect::<Vec<_>>();
    vec![
        Column::new("PATH", false, cells(|row| row.path.clone())),
        Column::new("KIND", false, cells(|row| row.kind.to_owned())),
        Column::new("BRANCH", false, cells(|row| row.branch.clone())),
        Column::new("MERGE", false, cells(|row| row.merge.clone())),
        Column::new("DIRTY", false, cells(|row| row.dirty.clone())),
        Column::new("UPSTREAM", false, cells(|row| row.upstream.clone())),
        Column::new("ACTIVE", true, cells(|row| row.active.clone())),
        Column::new("SIZE", true, cells(|row| row.size.clone())),
        Column::new("FLAGS", false, cells(|row| row.flags.clone())),
    ]
}

/// Lays the columns out as lines no wider than `width` where possible: every column is as wide as its widest
/// cell, except the path column at `path_index`, which takes the room left and truncates from the left.
fn layout(columns: &[Column], path_index: usize, width: usize, header: bool) -> Vec<String> {
    let mut widths: Vec<usize> = columns
        .iter()
        .map(|column| {
            let header_width = if header { char_len(column.header) } else { 0 };
            column
                .cells
                .iter()
                .map(|cell| char_len(cell))
                .max()
                .unwrap_or(0)
                .max(header_width)
        })
        .collect();
    let others: usize = widths
        .iter()
        .enumerate()
        .filter(|(index, _)| *index != path_index)
        .map(|(_, width)| width)
        .sum::<usize>()
        + SEPARATOR.len() * columns.len().saturating_sub(1);
    let room = width.saturating_sub(others).max(MIN_PATH_WIDTH);
    if let Some(path_width) = widths.get_mut(path_index) {
        *path_width = (*path_width).min(room);
    }

    let rows = columns.first().map_or(0, |column| column.cells.len());
    let mut lines = Vec::with_capacity(rows + 1);
    if header {
        let cells: Vec<String> = columns
            .iter()
            .map(|column| column.header.to_owned())
            .collect();
        lines.push(join_cells(columns, &widths, &cells));
    }
    for row in 0..rows {
        let cells: Vec<String> = columns
            .iter()
            .enumerate()
            .map(|(index, column)| {
                let cell = column.cells.get(row).map_or("", String::as_str);
                if index == path_index {
                    truncate_start(cell, widths[index])
                } else {
                    cell.to_owned()
                }
            })
            .collect();
        lines.push(join_cells(columns, &widths, &cells));
    }
    lines
}

fn join_cells(columns: &[Column], widths: &[usize], cells: &[String]) -> String {
    let mut line = String::new();
    for (index, ((column, width), cell)) in columns.iter().zip(widths).zip(cells).enumerate() {
        if index > 0 {
            line.push_str(SEPARATOR);
        }
        let padding = width.saturating_sub(char_len(cell));
        if column.right_aligned {
            let _ = write!(line, "{}{cell}", " ".repeat(padding));
        } else {
            let _ = write!(line, "{cell}{}", " ".repeat(padding));
        }
    }
    line.trim_end().to_owned()
}

fn char_len(text: &str) -> usize {
    text.chars().count()
}

/// Keeps the last `width - 1` characters behind a `…` when `text` is wider than `width`.
fn truncate_start(text: &str, width: usize) -> String {
    let len = char_len(text);
    if len <= width {
        return text.to_owned();
    }
    let keep = width.saturating_sub(1);
    let tail: String = text.chars().skip(len - keep).collect();
    format!("…{tail}")
}

/// Keeps the first `width - 1` characters before a `…` when `text` is wider than `width`.
fn truncate_end(text: &str, width: usize) -> String {
    if char_len(text) <= width {
        return text.to_owned();
    }
    let head: String = text.chars().take(width.saturating_sub(1)).collect();
    format!("{head}…")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::signals::Dirty;

    const NOW: i64 = 1_790_000_000;
    const DAY: i64 = 86_400;

    fn root() -> PathBuf {
        PathBuf::from(r"D:\")
    }

    fn registered(path: &str, branch: Option<&str>, signals: WorktreeSignals) -> Candidate {
        Candidate::Registered(RegisteredCandidate {
            path: root().join(path),
            repo: root().join("yaat"),
            branch: branch.map(str::to_owned),
            head: Some("0123456789abcdef".to_owned()),
            prunable: None,
            git_lock: None,
            signals,
        })
    }

    fn size(bytes: u64, last_write_unix: i64) -> SizeInfo {
        SizeInfo {
            bytes,
            files: 1,
            unreadable: 0,
            last_write_unix: Some(last_write_unix),
        }
    }

    fn orphan(path: &str, kind: OrphanKind, size: SizeInfo) -> Candidate {
        Candidate::Orphan(OrphanCandidate {
            orphan: Orphan {
                path: root().join(path),
                container: root().join("yaat.wt"),
                orphan_kind: kind,
                link_target: None,
                stale_gitdir: false,
                live_gitdir: None,
                has_git_dir: false,
            },
            size,
        })
    }

    fn report(candidates: Vec<Candidate>) -> Report {
        Report {
            root: root(),
            repos: Vec::new(),
            candidates,
        }
    }

    #[test]
    fn render_table_formats_signals() {
        let merged = WorktreeSignals {
            merge_state: Some(MergeState::Ancestor),
            merge_state_against: Some("main".to_owned()),
            dirty: Some(Dirty::default()),
            upstream: Some(Upstream::Gone),
            last_activity_unix: Some(NOW - 3 * DAY),
            size: Some(size(31_030_000_000, NOW)),
            errors: Vec::new(),
        };
        let unmerged = WorktreeSignals {
            merge_state: Some(MergeState::Unmerged { commits: 4 }),
            merge_state_against: Some("main".to_owned()),
            dirty: Some(Dirty {
                modified: 3,
                untracked: 2,
            }),
            upstream: Some(Upstream::Tracking { ahead: 2 }),
            last_activity_unix: Some(NOW - 35 * DAY),
            size: Some(size(1536, NOW)),
            errors: Vec::new(),
        };
        let mut locked = registered(r"yaat.wt\eram-am\yaat", Some("eram-am"), unmerged);
        if let Candidate::Registered(candidate) = &mut locked {
            candidate.git_lock = Some("on a USB drive".to_owned());
        }
        let mut stale = orphan(
            r"yaat.wt\eram-qx",
            OrphanKind::Folder,
            size(0, NOW - 400 * DAY),
        );
        if let Candidate::Orphan(candidate) = &mut stale {
            candidate.orphan.stale_gitdir = true;
        }
        let report = report(vec![
            stale,
            orphan(
                r"yaat-server.wt\yaat",
                OrphanKind::Link,
                size(0, NOW - 2 * 3600),
            ),
            locked,
            registered(r"yaat.wt\eram-co\yaat", Some("eram-co"), merged),
        ]);

        let expected = concat!(
            "#  PATH                  KIND      BRANCH   MERGE       DIRTY  UPSTREAM  ACTIVE     SIZE  FLAGS\n",
            "1  yaat.wt\\eram-am\\yaat  worktree  eram-am  unmerged 4  3M 2?  +2            5w   1.5 KB  git-locked\n",
            "2  yaat.wt\\eram-co\\yaat  worktree  eram-co  merged             gone          3d  28.9 GB\n",
            "3  yaat-server.wt\\yaat   link                                                2h\n",
            "4  yaat.wt\\eram-qx       orphan                                              1y      0 B  stale .git\n",
        );
        assert_eq!(render_table(&report, NOW), expected);
    }

    #[test]
    fn no_commits_branch_shows_in_table_and_json() -> Result<()> {
        let signals = WorktreeSignals {
            merge_state: Some(MergeState::NoCommits),
            merge_state_against: Some("main".to_owned()),
            dirty: Some(Dirty::default()),
            upstream: None,
            last_activity_unix: Some(NOW - 3 * DAY),
            size: Some(size(1536, NOW)),
            errors: Vec::new(),
        };
        let report = report(vec![registered(
            r"yaat.wt\eram-co\yaat",
            Some("eram-co"),
            signals,
        )]);

        let table = render_table(&report, NOW);
        let row = table.lines().nth(1).unwrap_or_default();
        anyhow::ensure!(row.contains("eram-co  no commits"), "row: {row}");
        let mut json = Vec::new();
        write_json(&report, &mut json)?;
        let json = String::from_utf8(json)?;
        anyhow::ensure!(json.contains(r#""state": "no_commits""#), "json: {json}");
        Ok(())
    }

    #[test]
    fn render_table_truncates_long_paths() {
        let long = format!(
            r"yaat.wt\{}\tail-segment",
            "deeply-nested-folder".repeat(10)
        );
        let report = report(vec![orphan(&long, OrphanKind::Folder, size(10, NOW))]);

        let table = render_table(&report, NOW);
        for line in table.lines() {
            assert!(char_len(line) <= TABLE_WIDTH, "line too wide: {line}");
        }
        let row = table.lines().nth(1).unwrap_or_default();
        assert!(row.starts_with("1  …"), "row: {row}");
        assert!(row.contains(r"folder\tail-segment  orphan"), "row: {row}");
    }
}
