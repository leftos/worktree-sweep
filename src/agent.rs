//! `worktree-sweep remove <PATH> --json`: removes one worktree without asking, or reports what holds it and marks
//! it released for the next interactive sweep.
//!
//! The order is fixed: resolve the path, refuse lost work, refuse when the caller's own shell holds the folder,
//! check the Recycle Bin can take it, recycle it on a thread with a timeout, and on a lock find the holders, stop
//! the allowlisted ones when asked, and retry once. A worktree that is still there is marked released.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, anyhow};
use serde::{Deserialize, Serialize};
use tracing::{debug, warn};

use crate::holders::{self, Holder, HolderReport, MayHold};
use crate::recycle::{self, ComApartment, Decision};
use crate::remove::{self, RemoveError};
use crate::report::{self, Candidate, RegisteredCandidate};
use crate::signals::{MergeState, Upstream};
use crate::{RefusalReason, Resolved, discover, pick, resolve_one, unlock};

/// The marker file written into a worktree's admin dir (`<repo>/.git/worktrees/<id>`) when it is released.
pub const MARKER_FILE: &str = "worktree-sweep-released.json";
/// How long one Shell recycle may take before the worktree is released instead.
const SHELL_TIMEOUT: Duration = Duration::from_secs(30);
/// How long a stopped build server may take to exit.
const STOP_WAIT: Duration = Duration::from_secs(5);

/// What `remove` did.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Status {
    /// The worktree is gone and its registration pruned.
    Removed,
    /// The worktree is still there and marked released for the next interactive sweep.
    Released,
    /// Nothing was touched.
    Refused,
}

/// Why the worktree was released or refused.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Reason {
    /// A process holds it (or one we cannot see does).
    Locked,
    /// Only processes that could not be fully inspected may hold it.
    MayHold,
    /// It cannot go to the Recycle Bin.
    TooBigForRecycleBin,
    /// The Shell did not finish the recycle in time.
    ShellTimeout,
    /// Removing it would lose work.
    WouldLose,
    /// A process in the caller's own parent chain holds it.
    CallerHolds,
    /// The path is not a removable worktree.
    #[serde(untagged)]
    NotRemovable(RefusalReason),
}

/// A process, by pid and image name.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProcessRef {
    /// The process id.
    pub pid: u32,
    /// The image name.
    pub exe: String,
}

/// The [`MARKER_FILE`] a released worktree carries in its admin dir: written by `remove`, read by the scan.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Released {
    /// When it was released, in Unix seconds.
    pub released_at: i64,
    /// Why it was released.
    pub reason: Reason,
    /// The processes that held it, or may have.
    pub holders: Vec<ProcessRef>,
}

/// Reads the [`MARKER_FILE`] in a worktree's admin dir; `None` when there is none.
///
/// # Errors
///
/// When the file exists but cannot be read or does not parse as a marker.
pub fn read_marker(admin: &Path) -> Result<Option<Released>> {
    let path = admin.join(MARKER_FILE);
    let text = match fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => {
            return Err(error).with_context(|| format!("cannot read {}", path.display()));
        }
    };
    let released = serde_json::from_str(&text)
        .with_context(|| format!("{} is not a released marker", path.display()))?;
    Ok(Some(released))
}

/// The flags of `remove`.
#[derive(Debug, Clone, Copy, Default)]
pub struct Options {
    /// Remove even when work would be lost; lifts a git lock first.
    pub force: bool,
    /// Stop allowlisted build and language servers that hold the worktree, then retry.
    pub stop_build_servers: bool,
}

/// The JSON `remove` prints.
#[derive(Debug, Clone, Serialize)]
pub struct RemoveReport {
    /// What happened.
    pub status: Status,
    /// Why it was released or refused; `None` when removed.
    pub reason: Option<Reason>,
    /// The worktree (the path as given, made absolute, when it did not resolve).
    pub path: PathBuf,
    /// The repo's main worktree, when known.
    pub repo: Option<PathBuf>,
    /// The worktree's branch; `None` when detached or unknown.
    pub branch: Option<String>,
    /// Whether the branch was deleted.
    pub branch_deleted: bool,
    /// What removing it would lose, for `would_lose`.
    pub loss: Option<String>,
    /// Where the caller should `cd` before retrying, for `caller_holds`.
    pub cd_to: Option<PathBuf>,
    /// Processes that hold something inside the worktree.
    pub holders: Vec<Holder>,
    /// Processes that may hold it but could not be fully inspected.
    pub may_hold: Vec<MayHold>,
    /// Build servers stopped with `--stop-build-servers`.
    pub stopped: Vec<ProcessRef>,
    /// Whether the released marker was written.
    pub released: bool,
    /// Follow-ups and anything that failed along the way without stopping the run.
    pub notes: Vec<String>,
}

impl RemoveReport {
    fn new(path: PathBuf, repo: Option<PathBuf>, branch: Option<String>) -> Self {
        Self {
            status: Status::Removed,
            reason: None,
            path,
            repo,
            branch,
            branch_deleted: false,
            loss: None,
            cd_to: None,
            holders: Vec::new(),
            may_hold: Vec::new(),
            stopped: Vec::new(),
            released: false,
            notes: Vec::new(),
        }
    }

    fn for_candidate(candidate: &RegisteredCandidate) -> Self {
        Self::new(
            candidate.path.clone(),
            Some(candidate.repo.clone()),
            candidate.branch.clone(),
        )
    }

    fn refuse(&mut self, reason: Reason) {
        self.status = Status::Refused;
        self.reason = Some(reason);
    }

    fn take_scan(&mut self, scan: HolderReport) {
        self.holders = scan.holders;
        self.may_hold = scan.may_hold;
    }
}

/// What to do with a removed worktree's branch.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BranchAction {
    /// Delete it with `git branch -d`.
    Delete(String),
    /// Keep it, with a note when the reader should know why.
    Keep(Option<String>),
}

/// The exit code for a status: 0 removed, 5 released, 6 refused.
#[must_use]
pub fn exit_code(status: Status) -> u8 {
    match status {
        Status::Removed => 0,
        Status::Released => 5,
        Status::Refused => 6,
    }
}

/// Writes the report as one pretty-printed JSON document followed by a newline.
///
/// # Errors
///
/// When serialising or writing fails.
pub fn write_json(report: &RemoveReport, out: &mut impl Write) -> Result<()> {
    serde_json::to_writer_pretty(&mut *out, report).context("cannot write the JSON result")?;
    writeln!(out).context("cannot write the JSON result")?;
    Ok(())
}

/// Removes the worktree at `path`, or reports why not; see the module docs for the order.
///
/// # Errors
///
/// When the path cannot be resolved, git fails listing the repo, the current folder cannot be changed, a git lock
/// cannot be lifted, the caller's holders cannot be listed, or the recycle fails for a reason other than a lock.
pub fn run(path: &Path, options: Options) -> Result<RemoveReport> {
    let resolved = match resolve_one(path)? {
        Ok(resolved) => resolved,
        Err(refusal) => {
            let absolute = std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf());
            let mut report = RemoveReport::new(absolute, refusal.repo, None);
            report.refuse(Reason::NotRemovable(refusal.reason));
            return Ok(report);
        }
    };
    std::env::set_current_dir(&resolved.main_worktree).with_context(|| {
        format!(
            "cannot change the current folder to {}",
            resolved.main_worktree.display()
        )
    })?;
    let candidate = &resolved.candidate;
    let mut report = RemoveReport::for_candidate(candidate);
    if candidate.prunable.is_some() {
        return remove_prunable(candidate, options, report);
    }
    if !options.force
        && let Some(loss) = would_lose(candidate)
    {
        report.refuse(Reason::WouldLose);
        report.loss = Some(loss);
        return Ok(report);
    }
    if let Some(scan) = caller_scan(&candidate.path)? {
        report.refuse(Reason::CallerHolds);
        report.cd_to = Some(resolved.main_worktree.clone());
        report.take_scan(scan);
        return Ok(report);
    }
    remove_folder(&resolved, options, report)
}

/// Capacity, recycle, and on a lock the holders, a retry and the release; the follow-ups on success.
fn remove_folder(
    resolved: &Resolved,
    options: Options,
    mut report: RemoveReport,
) -> Result<RemoveReport> {
    let candidate = &resolved.candidate;
    if let Some(why) = too_big(candidate) {
        report
            .notes
            .push(format!("cannot go to the Recycle Bin: {why}"));
        release(&mut report, candidate, Reason::TooBigForRecycleBin);
        return Ok(report);
    }
    if options.force {
        remove::git_unlock(candidate)?;
    }
    match recycle_with_timeout(&candidate.path)? {
        Recycled::Done => {}
        Recycled::TimedOut => {
            release(&mut report, candidate, Reason::ShellTimeout);
            return Ok(report);
        }
        Recycled::Locked => {
            if !retry_locked(candidate, options, &mut report)? {
                return Ok(report);
            }
        }
    }
    finish_removed(candidate, &mut report);
    Ok(report)
}

/// A prunable record: prune it and deal with its branch. A git-locked one needs `--force`, since prune skips it.
fn remove_prunable(
    candidate: &RegisteredCandidate,
    options: Options,
    mut report: RemoveReport,
) -> Result<RemoveReport> {
    if candidate.git_lock.is_some() {
        if !options.force {
            report.refuse(Reason::WouldLose);
            report.loss = loss_text(candidate);
            return Ok(report);
        }
        remove::git_unlock(candidate)?;
    }
    finish_removed(candidate, &mut report);
    Ok(report)
}

/// What removing the worktree would lose, as the picker's loss sentence: uncommitted files, commits not on the
/// default branch, a detached HEAD not on it, unpushed commits, a git lock, or a signal that could not be read.
/// A branch with no commits of its own is not a loss. `None` when nothing would be lost.
#[must_use]
pub fn would_lose(candidate: &RegisteredCandidate) -> Option<String> {
    let signals = &candidate.signals;
    let mut unreadable = Vec::new();
    if signals.dirty.is_none() {
        unreadable.push("uncommitted changes");
    }
    if signals.merge_state.is_none() {
        unreadable.push("merge state");
    }
    if candidate.branch.is_some() && signals.upstream.is_none() {
        unreadable.push("upstream");
    }
    let dirty = signals
        .dirty
        .is_some_and(|dirty| dirty.modified > 0 || dirty.untracked > 0);
    let unmerged = matches!(
        signals.merge_state,
        Some(MergeState::Unmerged { .. } | MergeState::Detached { contained: false })
    );
    let unpushed = matches!(signals.upstream, Some(Upstream::Tracking { ahead }) if ahead > 0);
    let lost = dirty || unmerged || unpushed || candidate.git_lock.is_some();
    if !lost && unreadable.is_empty() {
        return None;
    }
    let mut sentences: Vec<String> = if lost {
        loss_text(candidate).into_iter().collect()
    } else {
        vec![format!(
            "{}:",
            report::relative_path(&candidate.path, loss_root(candidate))
        )]
    };
    if !unreadable.is_empty() {
        sentences.push(format!("Its {} could not be read.", unreadable.join(", ")));
    }
    Some(sentences.join(" "))
}

fn loss_text(candidate: &RegisteredCandidate) -> Option<String> {
    pick::loss_sentence(
        &Candidate::Registered(candidate.clone()),
        loss_root(candidate),
    )
    .map(|(context, _question)| context)
}

/// The folder a loss sentence names the worktree relative to: the repo's parent.
fn loss_root(candidate: &RegisteredCandidate) -> &Path {
    candidate.repo.parent().unwrap_or(&candidate.repo)
}

/// The branch follow-up once a worktree is gone: a branch merged into the default branch, or with no commits of its
/// own, is deleted; a cherry-picked or squash-merged one is kept with a note (`git branch -d` refuses it); any
/// other is kept silently.
#[must_use]
pub fn branch_action(candidate: &RegisteredCandidate) -> BranchAction {
    let Some(branch) = &candidate.branch else {
        return BranchAction::Keep(None);
    };
    match candidate.signals.merge_state {
        Some(MergeState::Ancestor | MergeState::NoCommits) => BranchAction::Delete(branch.clone()),
        Some(MergeState::PatchesApplied) => BranchAction::Keep(Some(format!(
            "branch {branch} kept: cherry-picked, delete it with git branch -D"
        ))),
        Some(MergeState::ContentContained) => BranchAction::Keep(Some(format!(
            "branch {branch} kept: squash-merged, delete it with git branch -D"
        ))),
        _ => BranchAction::Keep(None),
    }
}

/// Whether a process in `chain` (the caller's own parent chain) holds something in the folder.
#[must_use]
pub fn caller_holds(chain: &[u32], scan: &HolderReport) -> bool {
    scan.holders
        .iter()
        .any(|holder| !holder.holds.is_empty() && chain.contains(&holder.pid))
}

/// The release reason once a retry is still locked: `may_hold` when only processes that could not be inspected
/// remain, otherwise `locked` (also when nothing was found: a holder we cannot see).
#[must_use]
pub fn locked_reason(scan: &HolderReport) -> Reason {
    if scan.holders.is_empty() && !scan.may_hold.is_empty() {
        Reason::MayHold
    } else {
        Reason::Locked
    }
}

/// The holders scan, when the caller's own parent chain holds the folder; `None` when it does not.
fn caller_scan(folder: &Path) -> Result<Option<HolderReport>> {
    let own = std::process::id();
    let times = holders::process_times()?;
    let chain = holders::ancestors(own, &times);
    let scan = holders::find_holders(folder, &[own])?;
    Ok(caller_holds(&chain, &scan).then_some(scan))
}

/// Why the folder cannot go to the Recycle Bin; `None` when it fits.
fn too_big(candidate: &RegisteredCandidate) -> Option<String> {
    let capacity = recycle::bin_capacity(&candidate.path).unwrap_or_else(|error| {
        warn!(
            "cannot read the Recycle Bin size for {}: {error:#}",
            candidate.path.display()
        );
        None
    });
    let size = candidate.signals.size.map_or(u64::MAX, |size| size.bytes);
    match recycle::recycle_decision(size, capacity) {
        Decision::Recycle => None,
        Decision::AskPermanent(why) => Some(why),
    }
}

enum Recycled {
    Done,
    Locked,
    TimedOut,
}

/// Recycles `path` on its own COM thread, waiting at most [`SHELL_TIMEOUT`]; a thread still stuck in the Shell is
/// abandoned.
fn recycle_with_timeout(path: &Path) -> Result<Recycled> {
    let (sender, receiver) = mpsc::channel();
    let target = path.to_path_buf();
    let worker = thread::Builder::new()
        .name("recycle".to_owned())
        .spawn(move || {
            let result = ComApartment::init()
                .map_err(RemoveError::from)
                .and_then(|_com| recycle::recycle(&target));
            if sender.send(result).is_err() {
                debug!("the recycle result arrived after the wait ended");
            }
        })
        .context("cannot start the recycle thread")?;
    match receiver.recv_timeout(SHELL_TIMEOUT) {
        Ok(Ok(())) => Ok(Recycled::Done),
        Ok(Err(RemoveError::Locked { .. })) => Ok(Recycled::Locked),
        Ok(Err(RemoveError::Other(error))) => Err(error),
        Err(RecvTimeoutError::Timeout) => {
            warn!(
                "moving {} to the Recycle Bin did not finish within {} s",
                path.display(),
                SHELL_TIMEOUT.as_secs()
            );
            drop(worker);
            Ok(Recycled::TimedOut)
        }
        Err(RecvTimeoutError::Disconnected) => Err(anyhow!(
            "the recycle thread for {} ended without a result",
            path.display()
        )),
    }
}

/// On a lock: lists the holders, stops the allowlisted ones when asked, and retries once. Returns whether the
/// folder is gone; when it is not, the report is released.
fn retry_locked(
    candidate: &RegisteredCandidate,
    options: Options,
    report: &mut RemoveReport,
) -> Result<bool> {
    let scan = holders::find_holders(&candidate.path, &[]).unwrap_or_else(|error| {
        report
            .notes
            .push(format!("cannot list the processes holding it: {error:#}"));
        HolderReport::default()
    });
    if options.stop_build_servers {
        stop_build_servers(&scan, report);
    }
    let reason = locked_reason(&scan);
    report.take_scan(scan);
    match recycle_with_timeout(&candidate.path)? {
        Recycled::Done => Ok(true),
        Recycled::Locked => {
            release(report, candidate, reason);
            Ok(false)
        }
        Recycled::TimedOut => {
            release(report, candidate, Reason::ShellTimeout);
            Ok(false)
        }
    }
}

fn stop_build_servers(scan: &HolderReport, report: &mut RemoveReport) {
    for holder in holders::stoppable(scan) {
        let name = format!("pid {} ({})", holder.pid, holder.exe);
        if !holders::still_same(holder) {
            report.notes.push(format!(
                "{name} not stopped: it is no longer the same process"
            ));
            continue;
        }
        match unlock::stop_process(holder.pid, STOP_WAIT) {
            Ok(()) => report.stopped.push(ProcessRef {
                pid: holder.pid,
                exe: holder.exe.clone(),
            }),
            Err(error) => report.notes.push(format!("{name} not stopped: {error:#}")),
        }
    }
}

/// Marks the report released and writes the marker; a marker that cannot be written is a note.
fn release(report: &mut RemoveReport, candidate: &RegisteredCandidate, reason: Reason) {
    report.status = Status::Released;
    report.reason = Some(reason);
    let processes: Vec<ProcessRef> = report
        .holders
        .iter()
        .map(|holder| ProcessRef {
            pid: holder.pid,
            exe: holder.exe.clone(),
        })
        .chain(report.may_hold.iter().map(|may| ProcessRef {
            pid: may.pid,
            exe: may.exe.clone(),
        }))
        .collect();
    match write_marker(&candidate.path, reason, processes) {
        Ok(()) => report.released = true,
        Err(error) => report
            .notes
            .push(format!("cannot mark it released: {error:#}")),
    }
}

/// Writes [`MARKER_FILE`] into the worktree's admin dir, through a temporary file renamed over the old marker.
fn write_marker(worktree: &Path, reason: Reason, processes: Vec<ProcessRef>) -> Result<()> {
    let admin = discover::read_gitdir_file(worktree).with_context(|| {
        format!(
            "{} has no .git file naming its admin folder",
            worktree.display()
        )
    })?;
    let marker = Released {
        released_at: now_unix(),
        reason,
        holders: processes,
    };
    let mut text = serde_json::to_string_pretty(&marker).context("cannot serialise the marker")?;
    text.push('\n');
    let temp = admin.join(format!("{MARKER_FILE}.tmp"));
    let target = admin.join(MARKER_FILE);
    fs::write(&temp, text).with_context(|| format!("cannot write {}", temp.display()))?;
    fs::rename(&temp, &target)
        .with_context(|| format!("cannot rename {} to {}", temp.display(), target.display()))?;
    Ok(())
}

fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|elapsed| i64::try_from(elapsed.as_secs()).ok())
        .unwrap_or_default()
}

/// Prunes the registration and deals with the branch; each failure is a note.
fn finish_removed(candidate: &RegisteredCandidate, report: &mut RemoveReport) {
    report.status = Status::Removed;
    report.reason = None;
    if let Err(error) = remove::prune(&candidate.repo) {
        report.notes.push(format!("prune failed: {error:#}"));
    }
    match branch_action(candidate) {
        BranchAction::Delete(branch) => {
            match remove::delete_branch(&candidate.repo, &branch, false) {
                Ok(()) => report.branch_deleted = true,
                Err(error) => report
                    .notes
                    .push(format!("branch {branch} kept: {error:#}")),
            }
        }
        BranchAction::Keep(Some(note)) => report.notes.push(note),
        BranchAction::Keep(None) => {}
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use super::*;
    use crate::holders::{Hold, MayHoldWhy};
    use crate::signals::{Dirty, WorktreeSignals};

    fn clean(merge_state: MergeState) -> RegisteredCandidate {
        RegisteredCandidate {
            path: PathBuf::from(r"D:\x.wt\feat"),
            repo: PathBuf::from(r"D:\x"),
            branch: Some("feat".to_owned()),
            head: Some("0123456789abcdef".to_owned()),
            prunable: None,
            git_lock: None,
            released: None,
            signals: WorktreeSignals {
                merge_state: Some(merge_state),
                merge_state_against: Some("main".to_owned()),
                dirty: Some(Dirty::default()),
                upstream: Some(Upstream::None),
                ..WorktreeSignals::default()
            },
        }
    }

    fn holder(pid: u32, holds: Vec<Hold>) -> Holder {
        Holder {
            pid,
            exe: "pwsh.exe".to_owned(),
            image: None,
            started: 1,
            command_line: None,
            holds,
        }
    }

    fn cwd_hold() -> Vec<Hold> {
        vec![Hold::CurrentFolder {
            path: PathBuf::from(r"D:\x.wt\feat"),
        }]
    }

    fn may(pid: u32) -> MayHold {
        MayHold {
            pid,
            exe: "svchost.exe".to_owned(),
            why: MayHoldWhy::UnnamedHandle,
        }
    }

    #[test]
    fn clean_merged_worktree_loses_nothing() {
        assert_eq!(would_lose(&clean(MergeState::Ancestor)), None);
        assert_eq!(
            would_lose(&clean(MergeState::Detached { contained: true })),
            None
        );
    }

    #[test]
    fn no_commits_is_not_a_loss() {
        assert_eq!(would_lose(&clean(MergeState::NoCommits)), None);
    }

    #[test]
    fn modified_files_are_a_loss() {
        let mut candidate = clean(MergeState::Ancestor);
        candidate.signals.dirty = Some(Dirty {
            modified: 2,
            untracked: 0,
        });
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: 2 modified files will be lost.")
        );
    }

    #[test]
    fn untracked_files_are_a_loss() {
        let mut candidate = clean(MergeState::Ancestor);
        candidate.signals.dirty = Some(Dirty {
            modified: 0,
            untracked: 1,
        });
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: 1 untracked file will be lost.")
        );
    }

    #[test]
    fn unmerged_commits_are_a_loss() {
        let candidate = clean(MergeState::Unmerged { commits: 3 });
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: 3 commits not on main will be lost.")
        );
    }

    #[test]
    fn uncontained_detached_head_is_a_loss() {
        let mut candidate = clean(MergeState::Detached { contained: false });
        candidate.branch = None;
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: detached HEAD 0123456 and its commits not on main will be lost.")
        );
    }

    #[test]
    fn unpushed_commits_are_a_loss() {
        let mut candidate = clean(MergeState::Ancestor);
        candidate.signals.upstream = Some(Upstream::Tracking { ahead: 1 });
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: 1 commit not pushed will be lost.")
        );
    }

    #[test]
    fn git_lock_is_a_loss() {
        let mut candidate = clean(MergeState::Ancestor);
        candidate.git_lock = Some("in use".to_owned());
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: It is git-locked: in use.")
        );
    }

    #[test]
    fn unreadable_signal_is_a_loss() {
        let mut candidate = clean(MergeState::Ancestor);
        candidate.signals.dirty = None;
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: Its uncommitted changes could not be read.")
        );
        let mut candidate = clean(MergeState::Ancestor);
        candidate.signals.merge_state = None;
        candidate.signals.upstream = None;
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(r"x.wt\feat: Its merge state, upstream could not be read.")
        );
    }

    #[test]
    fn unreadable_signal_adds_to_the_loss_sentence() {
        let mut candidate = clean(MergeState::Unmerged { commits: 1 });
        candidate.signals.dirty = None;
        assert_eq!(
            would_lose(&candidate).as_deref(),
            Some(
                r"x.wt\feat: 1 commit not on main will be lost. Its uncommitted changes could not be read."
            )
        );
    }

    #[test]
    fn merged_and_empty_branches_are_deleted() {
        assert_eq!(
            branch_action(&clean(MergeState::Ancestor)),
            BranchAction::Delete("feat".to_owned())
        );
        assert_eq!(
            branch_action(&clean(MergeState::NoCommits)),
            BranchAction::Delete("feat".to_owned())
        );
    }

    #[test]
    fn squash_merged_branches_are_kept_with_a_note() {
        assert_eq!(
            branch_action(&clean(MergeState::ContentContained)),
            BranchAction::Keep(Some(
                "branch feat kept: squash-merged, delete it with git branch -D".to_owned()
            ))
        );
        assert_eq!(
            branch_action(&clean(MergeState::PatchesApplied)),
            BranchAction::Keep(Some(
                "branch feat kept: cherry-picked, delete it with git branch -D".to_owned()
            ))
        );
    }

    #[test]
    fn unmerged_and_detached_branches_are_kept_silently() {
        assert_eq!(
            branch_action(&clean(MergeState::Unmerged { commits: 1 })),
            BranchAction::Keep(None)
        );
        let mut detached = clean(MergeState::Detached { contained: true });
        detached.branch = None;
        assert_eq!(branch_action(&detached), BranchAction::Keep(None));
        let mut unknown = clean(MergeState::Ancestor);
        unknown.signals.merge_state = None;
        assert_eq!(branch_action(&unknown), BranchAction::Keep(None));
    }

    #[test]
    fn status_maps_to_exit_code() {
        assert_eq!(exit_code(Status::Removed), 0);
        assert_eq!(exit_code(Status::Released), 5);
        assert_eq!(exit_code(Status::Refused), 6);
    }

    #[test]
    fn caller_holds_only_through_its_own_chain() {
        let scan = HolderReport {
            holders: vec![holder(10, cwd_hold()), holder(20, Vec::new())],
            may_hold: vec![may(30)],
        };
        assert!(caller_holds(&[1, 10, 100], &scan));
        assert!(!caller_holds(&[1, 20, 30], &scan));
        assert!(!caller_holds(&[1, 2], &scan));
    }

    #[test]
    fn locked_reason_prefers_definite_holders() {
        let definite = HolderReport {
            holders: vec![holder(10, cwd_hold())],
            may_hold: vec![may(30)],
        };
        assert_eq!(locked_reason(&definite), Reason::Locked);
        let unseen = HolderReport {
            holders: Vec::new(),
            may_hold: vec![may(30)],
        };
        assert_eq!(locked_reason(&unseen), Reason::MayHold);
        assert_eq!(locked_reason(&HolderReport::default()), Reason::Locked);
    }

    #[test]
    fn reasons_serialise_as_snake_case_strings() -> Result<()> {
        let reasons = [
            Reason::TooBigForRecycleBin,
            Reason::CallerHolds,
            Reason::NotRemovable(RefusalReason::MainWorktree),
            Reason::NotRemovable(RefusalReason::NotAWorktree),
        ];
        let text = serde_json::to_string(&reasons)?;
        if text != r#"["too_big_for_recycle_bin","caller_holds","main_worktree","not_a_worktree"]"# {
            return Err(anyhow!("serialised as {text}"));
        }
        Ok(())
    }
}
