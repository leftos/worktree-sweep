//! The scan report and its JSON form.

use std::io::Write;
use std::path::PathBuf;

use anyhow::{Context, Result};
use serde::Serialize;

use crate::discover::Orphan;
use crate::signals::{DefaultBranches, SizeInfo, WorktreeSignals};

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
