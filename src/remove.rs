//! Removing picks: a link, a folder (to the Recycle Bin or permanently), a registered worktree's registration and
//! its branch. Nothing here asks a question: each pick arrives as a [`Decision`] already answered.

use std::fmt;
use std::fs::{self, Metadata};
use std::io;
use std::path::{Path, PathBuf};

use anyhow::{Result, anyhow};
use tracing::warn;

use crate::discover::{self, OrphanKind};
use crate::git;
use crate::recycle::{self, BinCapacity};
use crate::report::{self, Candidate, RegisteredCandidate, human_bytes};
use crate::signals::MergeState;
use crate::unlock::UnlockOutcome;

/// Why a removal failed.
#[derive(Debug)]
pub enum RemoveError {
    /// Another process holds a file open (sharing violation or access denied, Win32 32 or 5).
    Locked {
        /// The pick being removed.
        path: PathBuf,
        /// The first file found locked, when known.
        first_locked_file: Option<PathBuf>,
    },
    /// Any other failure, with its context.
    Other(anyhow::Error),
}

impl fmt::Display for RemoveError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Locked {
                path,
                first_locked_file: Some(file),
            } => write!(
                f,
                "{} is locked by another process ({})",
                path.display(),
                file.display()
            ),
            Self::Locked { path, .. } => {
                write!(f, "{} is locked by another process", path.display())
            }
            Self::Other(error) => write!(f, "{error:#}"),
        }
    }
}

impl std::error::Error for RemoveError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Locked { .. } => None,
            Self::Other(error) => Some(error.as_ref()),
        }
    }
}

impl From<anyhow::Error> for RemoveError {
    fn from(error: anyhow::Error) -> Self {
        Self::Other(error)
    }
}

/// How a folder is deleted.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Method {
    /// Moved to the Recycle Bin.
    Recycle,
    /// Deleted for good.
    Permanent,
}

/// What removing a pick does.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Action {
    /// Delete the junction or symbolic link only; its target is not touched.
    RemoveLink,
    /// The folder is already gone: `git worktree prune` in its repo.
    PruneRegistration,
    /// Delete the folder.
    Delete(Method),
}

/// A pick's action, or why it is skipped.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Plan {
    /// Remove it this way.
    Run(Action),
    /// Leave it, for this reason.
    Skip(String),
}

/// What happens to a removed worktree's branch.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BranchChoice {
    /// No branch deletion applies.
    NotOffered,
    /// Delete it once the worktree is gone.
    Delete(BranchOffer),
    /// The user kept it.
    Keep(BranchOffer),
}

/// One pick with every answer about it: how to remove it (or why not) and what to do with its branch.
#[derive(Debug, Clone)]
pub struct Decision<'a> {
    /// The pick.
    pub candidate: &'a Candidate,
    /// Its action, or why it is skipped.
    pub plan: Plan,
    /// Its branch, applied only when the removal succeeds.
    pub branch: BranchChoice,
}

/// How a pick can be removed before any question is asked.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlanNeed {
    /// Remove it this way.
    Run(Action),
    /// It cannot go to the Recycle Bin, for the reason given; a permanent delete needs the user's yes.
    AskPermanent(String),
}

/// Where [`remove_picks`] is, by index into its decisions. A retried pick reports both again.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Progress {
    /// Removing decision `i` started.
    Started(usize),
    /// Decision `i` is finished, removed or failed.
    Done(usize),
}

/// What happened to one pick.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    /// Moved to the Recycle Bin.
    Recycled {
        /// Its size.
        bytes: u64,
    },
    /// Deleted for good.
    Permanent {
        /// Its size.
        bytes: u64,
    },
    /// The link was deleted; its target was not touched.
    LinkRemoved,
    /// Its registration was pruned (the folder was already gone).
    Pruned,
    /// Left in place.
    Skipped(String),
    /// Removing it failed.
    Failed(String),
}

/// One pick and what happened to it.
#[derive(Debug, Clone)]
pub struct Swept<'a> {
    /// The pick.
    pub candidate: &'a Candidate,
    /// What happened.
    pub outcome: Outcome,
    /// What the follow-ups (prune, branch) did.
    pub notes: Vec<String>,
}

/// Removes items one index at a time; the unit [`sweep`] drives.
pub trait Sweeper {
    /// Removes item `index`.
    ///
    /// # Errors
    ///
    /// [`RemoveError::Locked`] when a file is in use, else [`RemoveError::Other`].
    fn remove(&mut self, index: usize) -> Result<(), RemoveError>;
    /// Runs item `index`'s follow-ups once it is gone.
    fn finish(&mut self, index: usize);
    /// Offers, once, to clear the locks on `paths`.
    ///
    /// # Errors
    ///
    /// When the unlock flow fails.
    fn offer_unlock(&mut self, paths: &[PathBuf]) -> Result<UnlockOutcome>;
}

/// Removes items `0..count` in two passes. Pass 1 removes each and collects the locked ones; then, when any are
/// locked, the unlock flow is offered once for all of them, and on [`UnlockOutcome::Unlocked`] or
/// [`UnlockOutcome::PartlyUnlocked`] each locked item is retried once. An item's follow-ups run right after it is
/// removed, in whichever pass that happens.
pub fn sweep(count: usize, sweeper: &mut impl Sweeper) -> Vec<Result<(), RemoveError>> {
    let mut results: Vec<Result<(), RemoveError>> =
        (0..count).map(|index| attempt(sweeper, index)).collect();
    let locked: Vec<(usize, PathBuf)> = results
        .iter()
        .enumerate()
        .filter_map(|(index, result)| match result {
            Err(RemoveError::Locked { path, .. }) => Some((index, path.clone())),
            _ => None,
        })
        .collect();
    if locked.is_empty() {
        return results;
    }
    let paths: Vec<PathBuf> = locked.iter().map(|(_, path)| path.clone()).collect();
    let outcome = sweeper.offer_unlock(&paths).unwrap_or_else(|error| {
        warn!("the unlock flow failed: {error:#}");
        UnlockOutcome::Skipped
    });
    if matches!(
        outcome,
        UnlockOutcome::Unlocked | UnlockOutcome::PartlyUnlocked
    ) {
        for (index, _) in locked {
            results[index] = attempt(sweeper, index);
        }
    }
    results
}

fn attempt(sweeper: &mut impl Sweeper, index: usize) -> Result<(), RemoveError> {
    let result = sweeper.remove(index);
    if result.is_ok() {
        sweeper.finish(index);
    }
    result
}

/// Removes and follows up every decided pick with the two-pass [`sweep`]; a [`Plan::Skip`] decision becomes
/// [`Outcome::Skipped`] without touching the disk and reports no [`Progress`]. `on_progress` hears each runnable
/// pick start and finish; `offer_unlock` is called at most once, for the locked ones. COM must be initialised on
/// this thread ([`recycle::ComApartment`]). A pick that fails to go is its [`Outcome::Failed`].
pub fn remove_picks<'a>(
    decisions: &[Decision<'a>],
    on_progress: &mut dyn FnMut(Progress),
    offer_unlock: &mut dyn FnMut(&[PathBuf]) -> Result<UnlockOutcome>,
) -> Vec<Swept<'a>> {
    let mut swept = Vec::with_capacity(decisions.len());
    let mut runnable = Vec::new();
    for decision in decisions {
        let outcome = match &decision.plan {
            Plan::Run(action) => {
                runnable.push((swept.len(), *action));
                Outcome::Failed("not attempted".to_owned())
            }
            Plan::Skip(reason) => Outcome::Skipped(reason.clone()),
        };
        swept.push(Swept {
            candidate: decision.candidate,
            outcome,
            notes: Vec::new(),
        });
    }

    let mut sweeper = LiveSweeper {
        items: runnable
            .iter()
            .map(|&(index, action)| (index, decisions[index].candidate, action))
            .collect(),
        branches: runnable
            .iter()
            .map(|&(index, _)| decisions[index].branch.clone())
            .collect(),
        git_unlocked: vec![false; runnable.len()],
        notes: vec![Vec::new(); runnable.len()],
        on_progress,
        offer_unlock,
    };
    let results = sweep(runnable.len(), &mut sweeper);
    for (item, ((index, action), result)) in runnable.into_iter().zip(results).enumerate() {
        let entry = &mut swept[index];
        entry.outcome = match result {
            Ok(()) => done(action, entry.candidate),
            Err(RemoveError::Locked {
                path,
                first_locked_file,
            }) => Outcome::Failed(format!(
                "locked ({})",
                first_locked_file.unwrap_or(path).display()
            )),
            Err(RemoveError::Other(error)) => Outcome::Failed(format!("{error:#}")),
        };
        entry.notes = std::mem::take(&mut sweeper.notes[item]);
    }
    swept
}

fn done(action: Action, candidate: &Candidate) -> Outcome {
    let bytes = candidate.size_bytes().unwrap_or_default();
    match action {
        Action::RemoveLink => Outcome::LinkRemoved,
        Action::PruneRegistration => Outcome::Pruned,
        Action::Delete(Method::Recycle) => Outcome::Recycled { bytes },
        Action::Delete(Method::Permanent) => Outcome::Permanent { bytes },
    }
}

/// Removal state per runnable item: `(decision index, candidate, action)` and that item's branch choice.
struct LiveSweeper<'a, 'p> {
    items: Vec<(usize, &'a Candidate, Action)>,
    branches: Vec<BranchChoice>,
    git_unlocked: Vec<bool>,
    notes: Vec<Vec<String>>,
    on_progress: &'p mut dyn FnMut(Progress),
    offer_unlock: &'p mut dyn FnMut(&[PathBuf]) -> Result<UnlockOutcome>,
}

impl LiveSweeper<'_, '_> {
    fn try_remove(&mut self, index: usize) -> Result<(), RemoveError> {
        let (_, candidate, action) = self.items[index];
        if let Candidate::Registered(registered) = candidate
            && !self.git_unlocked[index]
        {
            git_unlock(registered)?;
            self.git_unlocked[index] = true;
        }
        remove_candidate(candidate, action)
    }
}

impl Sweeper for LiveSweeper<'_, '_> {
    fn remove(&mut self, index: usize) -> Result<(), RemoveError> {
        let decision = self.items[index].0;
        (self.on_progress)(Progress::Started(decision));
        let result = self.try_remove(index);
        if result.is_err() {
            (self.on_progress)(Progress::Done(decision));
        }
        result
    }

    fn finish(&mut self, index: usize) {
        let (decision, candidate, _) = self.items[index];
        self.notes[index] = after_removed(candidate, &self.branches[index]);
        (self.on_progress)(Progress::Done(decision));
    }

    fn offer_unlock(&mut self, paths: &[PathBuf]) -> Result<UnlockOutcome> {
        (self.offer_unlock)(paths)
    }
}

/// How a pick can be removed, given the Recycle Bin `capacity` of its volume (`None` when unknown): a link loses
/// only the link, a prunable registration is pruned, and a folder goes to the Recycle Bin when it fits (see
/// [`recycle::recycle_decision`]; an unknown size never fits), else needs the user's yes to delete permanently.
#[must_use]
pub fn plan_action(candidate: &Candidate, capacity: Option<BinCapacity>) -> PlanNeed {
    match candidate {
        Candidate::Orphan(orphan) if orphan.orphan.orphan_kind == OrphanKind::Link => {
            return PlanNeed::Run(Action::RemoveLink);
        }
        Candidate::Registered(registered) if registered.prunable.is_some() => {
            return PlanNeed::Run(Action::PruneRegistration);
        }
        _ => {}
    }
    let size = candidate.size_bytes().unwrap_or(u64::MAX);
    match recycle::recycle_decision(size, capacity) {
        recycle::Decision::Recycle => PlanNeed::Run(Action::Delete(Method::Recycle)),
        recycle::Decision::AskPermanent(reason) => PlanNeed::AskPermanent(reason),
    }
}

/// Whether [`plan_action`] reads the Recycle Bin capacity for this pick: `false` for a link and a prunable
/// registration.
#[must_use]
pub fn needs_capacity(candidate: &Candidate) -> bool {
    match candidate {
        Candidate::Orphan(orphan) => orphan.orphan.orphan_kind != OrphanKind::Link,
        Candidate::Registered(registered) => registered.prunable.is_none(),
    }
}

/// The Recycle Bin settings of the volume `path` is on; `None`, with a warning, when they cannot be read.
#[must_use]
pub fn read_capacity(path: &Path) -> Option<BinCapacity> {
    recycle::bin_capacity(path).unwrap_or_else(|error| {
        warn!(
            "cannot read the Recycle Bin size for {}: {error:#}",
            path.display()
        );
        None
    })
}

/// Carries out `action` on a pick's folder or link. A registered worktree's git lock must already be lifted
/// ([`git_unlock`]).
///
/// # Errors
///
/// [`RemoveError::Locked`] when a file is in use; otherwise a generic error with context.
pub fn remove_candidate(candidate: &Candidate, action: Action) -> Result<(), RemoveError> {
    let path = candidate.path();
    match action {
        Action::RemoveLink => remove_link(path),
        Action::PruneRegistration => match candidate {
            Candidate::Registered(registered) => Ok(prune(&registered.repo)?),
            Candidate::Orphan(_) => Err(anyhow!(
                "{} is not a registered worktree; there is nothing to prune",
                path.display()
            )
            .into()),
        },
        Action::Delete(Method::Recycle) => {
            reject_link(path)?;
            recycle::recycle(path)
        }
        Action::Delete(Method::Permanent) => permanent_delete(path),
    }
}

/// Lifts a registered worktree's git lock with `git worktree unlock`; does nothing when it is not git-locked.
///
/// # Errors
///
/// When git fails.
pub fn git_unlock(registered: &RegisteredCandidate) -> Result<()> {
    if registered.git_lock.is_some() {
        let path = registered.path.to_string_lossy();
        git::run(&registered.repo, &["worktree", "unlock", &path])?;
    }
    Ok(())
}

/// Runs `git worktree prune` in `repo`.
///
/// # Errors
///
/// When git fails.
pub fn prune(repo: &Path) -> Result<()> {
    git::run(repo, &["worktree", "prune"])?;
    Ok(())
}

/// The follow-ups once a pick is gone, returned as notes for the summary: a registered worktree's registration is
/// pruned and `branch` carried out (deleted, or noted as kept); an orphan still registered elsewhere gets a
/// reminder. A failure is a note, never fatal.
#[must_use]
pub fn after_removed(candidate: &Candidate, branch: &BranchChoice) -> Vec<String> {
    let mut notes = Vec::new();
    match candidate {
        Candidate::Registered(registered) => {
            if let Err(error) = prune(&registered.repo) {
                notes.push(format!("prune failed: {error:#}"));
            }
            match branch {
                BranchChoice::Delete(offer) => notes.push(
                    match delete_branch(&registered.repo, &offer.branch, offer.force) {
                        Ok(()) => format!("branch {} deleted", offer.branch),
                        Err(error) => format!("branch {} kept: {error:#}", offer.branch),
                    },
                ),
                BranchChoice::Keep(offer) => notes.push(format!("branch {} kept", offer.branch)),
                BranchChoice::NotOffered => {}
            }
        }
        Candidate::Orphan(orphan) => {
            if let Some(gitdir) = &orphan.orphan.live_gitdir {
                notes.push(format!(
                    "still registered at {}; `git worktree prune` in that repo clears it",
                    gitdir.display()
                ));
            }
        }
    }
    notes
}

/// An offer to delete a removed worktree's branch.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BranchOffer {
    /// The branch.
    pub branch: String,
    /// Whether it needs `git branch -D` (git's `-d` refuses cherry-picked and squash-merged branches).
    pub force: bool,
    /// The branch and why it can go (`Branch <name>: <reason>`), shown before the question.
    pub context: String,
    /// The fixed question, which names no branch.
    pub question: &'static str,
}

/// The branch deletion offered for a registered worktree: merged branches and branches with no commits of their
/// own with `-d`, cherry-picked and
/// content-contained ones with `-D`; `None` for an unmerged or detached worktree.
#[must_use]
pub fn branch_offer(registered: &RegisteredCandidate) -> Option<BranchOffer> {
    let branch = registered.branch.clone()?;
    let against = registered
        .signals
        .merge_state_against
        .as_deref()
        .unwrap_or("the default branch");
    let (force, reason) = match registered.signals.merge_state? {
        MergeState::Ancestor => (false, format!("it is merged into {against}.")),
        MergeState::NoCommits => (false, "it has no commits of its own.".to_owned()),
        MergeState::PatchesApplied => (
            true,
            format!(
                "every commit on it is cherry-picked onto {against}; git branch -d refuses it, so this uses git branch \
                 -D."
            ),
        ),
        MergeState::ContentContained => (
            true,
            format!(
                "its changes are already in {against} (squash-merged); git branch -d refuses it, so this uses git \
                 branch -D."
            ),
        ),
        MergeState::Unmerged { .. } | MergeState::Detached { .. } => return None,
    };
    let context = format!("Branch {branch}: {reason}");
    Some(BranchOffer {
        branch,
        force,
        context,
        question: "Delete the branch?",
    })
}

/// Deletes `branch` in `repo` with `git branch -d`, or `-D` when `force`.
///
/// # Errors
///
/// When git refuses (for example, the branch is checked out in another worktree); the error carries git's message.
pub fn delete_branch(repo: &Path, branch: &str, force: bool) -> Result<()> {
    let flag = if force { "-D" } else { "-d" };
    git::run(repo, &["branch", flag, branch])?;
    Ok(())
}

/// Deletes a folder for good. The walk never enters a junction or symbolic link: each is deleted as a link. Files
/// marked read-only (git's object files are) are cleared first.
///
/// # Errors
///
/// [`RemoveError::Locked`] naming the first file in use; otherwise a generic error with context.
pub fn permanent_delete(path: &Path) -> Result<(), RemoveError> {
    let meta =
        fs::symlink_metadata(path).map_err(|error| io_error(path, path, error, "cannot read"))?;
    if discover::is_link(&meta) {
        return unlink(path, path, &meta);
    }
    if meta.is_dir() {
        delete_tree(path, path, &meta)
    } else {
        delete_file(path, path, &meta)
    }
}

/// Deletes a junction or symbolic link, never its target, after checking it still is one.
///
/// # Errors
///
/// When `path` is no longer a link, or deleting it fails.
pub fn remove_link(path: &Path) -> Result<(), RemoveError> {
    let meta =
        fs::symlink_metadata(path).map_err(|error| io_error(path, path, error, "cannot read"))?;
    if !discover::is_link(&meta) {
        return Err(anyhow!("{} is no longer a link; left in place", path.display()).into());
    }
    unlink(path, path, &meta)
}

fn reject_link(path: &Path) -> Result<(), RemoveError> {
    let meta =
        fs::symlink_metadata(path).map_err(|error| io_error(path, path, error, "cannot read"))?;
    if discover::is_link(&meta) {
        return Err(anyhow!(
            "{} became a link since the scan; left in place",
            path.display()
        )
        .into());
    }
    Ok(())
}

fn delete_tree(root: &Path, dir: &Path, meta: &Metadata) -> Result<(), RemoveError> {
    let entries = fs::read_dir(dir).map_err(|error| io_error(root, dir, error, "cannot list"))?;
    for entry in entries {
        let path = entry
            .map_err(|error| io_error(root, dir, error, "cannot list"))?
            .path();
        let child = fs::symlink_metadata(&path)
            .map_err(|error| io_error(root, &path, error, "cannot read"))?;
        if discover::is_link(&child) {
            unlink(root, &path, &child)?;
        } else if child.is_dir() {
            delete_tree(root, &path, &child)?;
        } else {
            delete_file(root, &path, &child)?;
        }
    }
    clear_readonly(root, dir, meta)?;
    fs::remove_dir(dir).map_err(|error| io_error(root, dir, error, "cannot delete folder"))
}

fn delete_file(root: &Path, path: &Path, meta: &Metadata) -> Result<(), RemoveError> {
    clear_readonly(root, path, meta)?;
    fs::remove_file(path).map_err(|error| io_error(root, path, error, "cannot delete"))
}

/// Deletes a link itself: `RemoveDirectoryW` for a directory junction or link (it deletes the reparse point, not
/// the target), `DeleteFileW` for a file link.
fn unlink(root: &Path, path: &Path, meta: &Metadata) -> Result<(), RemoveError> {
    let result = if is_directory_entry(meta) {
        fs::remove_dir(path)
    } else {
        fs::remove_file(path)
    };
    result.map_err(|error| io_error(root, path, error, "cannot delete link"))
}

#[cfg(windows)]
fn is_directory_entry(meta: &Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_DIRECTORY: u32 = 0x10;
    meta.file_attributes() & FILE_ATTRIBUTE_DIRECTORY != 0
}

#[cfg(not(windows))]
fn is_directory_entry(meta: &Metadata) -> bool {
    meta.is_dir()
}

#[expect(
    clippy::permissions_set_readonly_false,
    reason = "on Windows this clears FILE_ATTRIBUTE_READONLY, which git sets on object files and which blocks deleting them"
)]
fn clear_readonly(root: &Path, path: &Path, meta: &Metadata) -> Result<(), RemoveError> {
    let mut permissions = meta.permissions();
    if !permissions.readonly() {
        return Ok(());
    }
    permissions.set_readonly(false);
    fs::set_permissions(path, permissions)
        .map_err(|error| io_error(root, path, error, "cannot clear read-only on"))
}

/// Maps an I/O error on `path` while removing `root`: a sharing violation (32) or access denied (5) is `Locked`.
fn io_error(root: &Path, path: &Path, error: io::Error, what: &str) -> RemoveError {
    match error.raw_os_error() {
        Some(32 | 5) => RemoveError::Locked {
            path: root.to_path_buf(),
            first_locked_file: Some(path.to_path_buf()),
        },
        _ => RemoveError::Other(
            anyhow::Error::new(error).context(format!("{what} {}", path.display())),
        ),
    }
}

/// One line per pick (`<path>: removed (recycled)`, `removed (permanent)`, `removed (link only)`, `removed
/// (registration pruned)`, `skipped (<why>)`, `failed: <why>`), its follow-up notes after `;`, then a total.
#[must_use]
pub fn summary(swept: &[Swept<'_>], root: &Path) -> Vec<String> {
    let mut lines = Vec::with_capacity(swept.len() + 1);
    let (mut removed, mut skipped, mut failed) = (0_usize, 0_usize, 0_usize);
    let (mut freed, mut recycled) = (0_u64, 0_u64);
    for entry in swept {
        let what = match &entry.outcome {
            Outcome::Recycled { bytes } => {
                removed += 1;
                freed += bytes;
                recycled += bytes;
                "removed (recycled)".to_owned()
            }
            Outcome::Permanent { bytes } => {
                removed += 1;
                freed += bytes;
                "removed (permanent)".to_owned()
            }
            Outcome::LinkRemoved => {
                removed += 1;
                "removed (link only)".to_owned()
            }
            Outcome::Pruned => {
                removed += 1;
                "removed (registration pruned)".to_owned()
            }
            Outcome::Skipped(reason) => {
                skipped += 1;
                format!("skipped ({reason})")
            }
            Outcome::Failed(reason) => {
                failed += 1;
                format!("failed: {reason}")
            }
        };
        let mut line = format!(
            "{}: {what}",
            report::relative_path(entry.candidate.path(), root)
        );
        for note in &entry.notes {
            line.push_str("; ");
            line.push_str(note);
        }
        lines.push(line);
    }
    let mut total = format!(
        "{removed} removed, {skipped} skipped, {failed} failed; {} freed",
        human_bytes(freed)
    );
    if recycled > 0 {
        total = format!(
            "{total} ({} of it in the Recycle Bin)",
            human_bytes(recycled)
        );
    }
    lines.push(total);
    lines
}

#[cfg(test)]
mod tests {
    use std::collections::HashSet;

    use super::*;

    struct FakeSweeper {
        locked_until_unlock: HashSet<usize>,
        always_locked: HashSet<usize>,
        failing: HashSet<usize>,
        outcome: UnlockOutcome,
        unlocked: bool,
        removes: Vec<usize>,
        finished: Vec<usize>,
        offers: Vec<Vec<PathBuf>>,
    }

    impl FakeSweeper {
        fn new(outcome: UnlockOutcome) -> Self {
            Self {
                locked_until_unlock: HashSet::from([1]),
                always_locked: HashSet::from([2]),
                failing: HashSet::from([3]),
                outcome,
                unlocked: false,
                removes: Vec::new(),
                finished: Vec::new(),
                offers: Vec::new(),
            }
        }
    }

    impl Sweeper for FakeSweeper {
        fn remove(&mut self, index: usize) -> Result<(), RemoveError> {
            self.removes.push(index);
            let locked = self.always_locked.contains(&index)
                || (self.locked_until_unlock.contains(&index) && !self.unlocked);
            if locked {
                Err(RemoveError::Locked {
                    path: PathBuf::from(format!("item{index}")),
                    first_locked_file: None,
                })
            } else if self.failing.contains(&index) {
                Err(anyhow!("broken").into())
            } else {
                Ok(())
            }
        }

        fn finish(&mut self, index: usize) {
            self.finished.push(index);
        }

        fn offer_unlock(&mut self, paths: &[PathBuf]) -> Result<UnlockOutcome> {
            self.offers.push(paths.to_vec());
            self.unlocked = matches!(
                self.outcome,
                UnlockOutcome::Unlocked | UnlockOutcome::PartlyUnlocked
            );
            Ok(self.outcome)
        }
    }

    #[test]
    fn locked_picks_trigger_one_offer_and_are_retried() {
        let mut sweeper = FakeSweeper::new(UnlockOutcome::PartlyUnlocked);
        let results = sweep(4, &mut sweeper);

        assert_eq!(
            sweeper.offers,
            vec![vec![PathBuf::from("item1"), PathBuf::from("item2")]]
        );
        assert_eq!(sweeper.removes, vec![0, 1, 2, 3, 1, 2]);
        assert_eq!(sweeper.finished, vec![0, 1]);
        assert!(results[0].is_ok() && results[1].is_ok(), "{results:?}");
        assert!(
            matches!(results[2], Err(RemoveError::Locked { .. })),
            "{results:?}"
        );
        assert!(
            matches!(results[3], Err(RemoveError::Other(_))),
            "{results:?}"
        );
    }

    #[test]
    fn skipped_unlock_does_not_retry() {
        let mut sweeper = FakeSweeper::new(UnlockOutcome::Skipped);
        let results = sweep(4, &mut sweeper);

        assert_eq!(sweeper.offers.len(), 1);
        assert_eq!(sweeper.removes, vec![0, 1, 2, 3]);
        assert!(
            matches!(results[1], Err(RemoveError::Locked { .. })),
            "{results:?}"
        );
    }

    #[test]
    fn no_locks_means_no_offer() {
        let mut sweeper = FakeSweeper::new(UnlockOutcome::Unlocked);
        sweeper.locked_until_unlock.clear();
        sweeper.always_locked.clear();
        let _ = sweep(4, &mut sweeper);
        assert_eq!(sweeper.offers, Vec::<Vec<PathBuf>>::new());
        assert_eq!(sweeper.finished, vec![0, 1, 2]);
    }

    fn folder_orphan(orphan_kind: OrphanKind, bytes: u64) -> Candidate {
        use crate::discover::Orphan;
        use crate::report::OrphanCandidate;
        use crate::signals::SizeInfo;

        Candidate::Orphan(OrphanCandidate {
            orphan: Orphan {
                path: PathBuf::from(r"D:\repo.wt\stray"),
                container: PathBuf::from(r"D:\repo.wt"),
                orphan_kind,
                link_target: None,
                stale_gitdir: false,
                live_gitdir: None,
                has_git_dir: false,
            },
            size: SizeInfo {
                bytes,
                ..SizeInfo::default()
            },
        })
    }

    const ONE_MB: BinCapacity = BinCapacity {
        max_capacity_mb: 1,
        nuke_on_delete: false,
    };

    #[test]
    fn plan_action_link_needs_no_capacity() {
        let link = folder_orphan(OrphanKind::Link, 0);
        assert!(!needs_capacity(&link));
        assert_eq!(plan_action(&link, None), PlanNeed::Run(Action::RemoveLink));
    }

    #[test]
    fn plan_action_over_capacity_asks_permanent() {
        let big = folder_orphan(OrphanKind::Folder, 2 * 1024 * 1024);
        assert!(needs_capacity(&big));
        assert!(
            matches!(plan_action(&big, Some(ONE_MB)), PlanNeed::AskPermanent(reason) if reason.contains("more than")),
        );
    }

    #[test]
    fn plan_action_unknown_capacity_asks_permanent() {
        let small = folder_orphan(OrphanKind::Folder, 10);
        assert!(
            matches!(plan_action(&small, None), PlanNeed::AskPermanent(reason) if reason.contains("unknown")),
        );
    }

    #[test]
    fn plan_action_fits_recycles() {
        let exact = folder_orphan(OrphanKind::Folder, 1024 * 1024);
        assert_eq!(
            plan_action(&exact, Some(ONE_MB)),
            PlanNeed::Run(Action::Delete(Method::Recycle))
        );
    }

    #[test]
    fn branch_question_is_short_and_nameless() -> Result<()> {
        use crate::signals::{Dirty, SizeInfo, Upstream, WorktreeSignals};

        const BRANCH: &str =
            "feature/JIRA-1234-rework-the-candidate-table-rendering-for-narrow-terminals";
        let kinds = [
            MergeState::Ancestor,
            MergeState::NoCommits,
            MergeState::PatchesApplied,
            MergeState::ContentContained,
        ];
        for merge_state in kinds {
            let registered = RegisteredCandidate {
                path: PathBuf::from(r"D:\repo.wt\narrow"),
                repo: PathBuf::from(r"D:\repo"),
                branch: Some(BRANCH.to_owned()),
                head: Some("0123456789abcdef".to_owned()),
                prunable: None,
                git_lock: None,
                released: None,
                signals: WorktreeSignals {
                    merge_state: Some(merge_state),
                    merge_state_against: Some("main".to_owned()),
                    dirty: Some(Dirty::default()),
                    upstream: Some(Upstream::Tracking { ahead: 0 }),
                    last_activity_unix: None,
                    size: Some(SizeInfo::default()),
                    errors: Vec::new(),
                },
            };
            let offer = branch_offer(&registered)
                .ok_or_else(|| anyhow!("no branch offer for {merge_state:?}"))?;
            anyhow::ensure!(
                !offer.question.contains("JIRA") && !offer.question.contains('/'),
                "question names the branch: {}",
                offer.question
            );
            anyhow::ensure!(
                offer.question.chars().count() <= 40,
                "question is {} chars: {}",
                offer.question.chars().count(),
                offer.question
            );
            anyhow::ensure!(
                offer.context.contains(BRANCH),
                "context does not name the branch: {}",
                offer.context
            );
        }
        Ok(())
    }
}
