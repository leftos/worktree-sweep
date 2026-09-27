//! The signals shown for each candidate: merge state, dirty counts, upstream, last activity and size.

use std::fs;
use std::num::NonZeroUsize;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::Serialize;
use tracing::{debug, warn};

use crate::discover::{self, WorktreeRecord};
use crate::git;

/// How far a worktree's branch has made it into the default branch, best first.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum MergeState {
    /// The branch tip is reachable from the default branch.
    Ancestor,
    /// Every commit on the branch has a patch-equivalent commit on the default branch.
    PatchesApplied,
    /// Merging the branch into the default branch would change nothing.
    ContentContained,
    /// The branch has commits the default branch lacks.
    Unmerged {
        /// Commits on the branch that are not on the default branch (the fewest over the defaults checked).
        commits: u32,
    },
    /// HEAD is detached.
    Detached {
        /// Whether HEAD is reachable from a default branch.
        contained: bool,
    },
}

impl MergeState {
    /// Lower is better; used to keep the best result over the local and origin default.
    fn rank(self) -> (u8, u32) {
        match self {
            Self::Ancestor | Self::Detached { contained: true } => (0, 0),
            Self::PatchesApplied => (1, 0),
            Self::ContentContained => (2, 0),
            Self::Unmerged { commits } => (3, commits),
            Self::Detached { contained: false } => (4, 0),
        }
    }
}

/// Uncommitted work in a worktree; ignored files are not counted.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
pub struct Dirty {
    /// Tracked entries with staged or unstaged changes.
    pub modified: u32,
    /// Untracked entries; an untracked folder counts once.
    pub untracked: u32,
}

/// The state of a branch's upstream.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum Upstream {
    /// No upstream is configured.
    None,
    /// An upstream is configured but its ref no longer exists.
    Gone,
    /// The upstream exists; `ahead` commits on the branch are not on it.
    Tracking {
        /// Commits on the branch that the upstream lacks.
        ahead: u32,
    },
}

/// Disk usage of a folder, measured without following links.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
pub struct SizeInfo {
    /// Total bytes of the files.
    pub bytes: u64,
    /// Number of files.
    pub files: u64,
    /// Entries that could not be read; their size is not counted.
    pub unreadable: u32,
    /// Newest modification time seen (the folder itself included), in unix seconds.
    pub last_write_unix: Option<i64>,
}

/// A repo's default branches, as short names (`main`, `origin/main`).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct DefaultBranches {
    /// The local default branch.
    pub local: Option<String>,
    /// The remote-tracking default branch `refs/remotes/origin/HEAD` points to.
    pub origin: Option<String>,
}

impl DefaultBranches {
    /// Each default that exists, as (short name, full ref name).
    fn refs(&self) -> Vec<(String, String)> {
        let local = self
            .local
            .iter()
            .map(|name| (name.clone(), format!("refs/heads/{name}")));
        let origin = self
            .origin
            .iter()
            .map(|name| (name.clone(), format!("refs/remotes/{name}")));
        local.chain(origin).collect()
    }
}

/// The signals of one registered worktree. Every field is `None` for a prunable registration, whose folder is gone.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct WorktreeSignals {
    /// The best merge state over the local and origin default.
    pub merge_state: Option<MergeState>,
    /// The default branch that gave `merge_state`.
    pub merge_state_against: Option<String>,
    /// Uncommitted work.
    pub dirty: Option<Dirty>,
    /// Upstream state; `None` when detached.
    pub upstream: Option<Upstream>,
    /// The later of the HEAD commit time and the worktree index's modification time, in unix seconds.
    pub last_activity_unix: Option<i64>,
    /// Disk usage of the worktree folder.
    pub size: Option<SizeInfo>,
    /// Signals that could not be read, one message each.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub errors: Vec<String>,
}

/// Finds a repo's default branches: `refs/remotes/origin/HEAD` for the origin default; for the local default, the
/// local branch of the same name, else `main`, else `master`.
///
/// # Errors
///
/// When git cannot be started or fails unexpectedly.
pub fn default_branches(repo: &Path) -> Result<DefaultBranches> {
    let symref = git::run_status(
        repo,
        &["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"],
    )?;
    let origin = if symref.success() {
        symref
            .stdout
            .trim()
            .strip_prefix("refs/remotes/")
            .map(str::to_owned)
    } else {
        None
    };
    let mut names: Vec<String> = origin
        .as_deref()
        .and_then(|name| name.strip_prefix("origin/"))
        .map(str::to_owned)
        .into_iter()
        .collect();
    names.extend(["main".to_owned(), "master".to_owned()]);
    let mut local = None;
    for name in names {
        if ref_exists(repo, &format!("refs/heads/{name}"))? {
            local = Some(name);
            break;
        }
    }
    Ok(DefaultBranches { local, origin })
}

/// Reads every signal of a registered worktree. A signal that fails is left `None` and its error recorded, so one
/// broken worktree never hides the others.
#[must_use]
pub fn worktree_signals(defaults: &DefaultBranches, record: &WorktreeRecord) -> WorktreeSignals {
    if record.prunable.is_some() {
        return WorktreeSignals::default();
    }
    let dir = record.path.as_path();
    let mut errors = Vec::new();
    let merge = keep(&mut errors, merge_state(dir, defaults, record)).flatten();
    let dirty = keep(&mut errors, dirty(dir));
    let upstream = match &record.branch {
        Some(branch) => keep(&mut errors, upstream(dir, branch)),
        None => None,
    };
    let last_activity_unix = keep(&mut errors, last_activity(dir)).flatten();
    for error in &errors {
        warn!("worktree {}: {error}", dir.display());
    }
    WorktreeSignals {
        merge_state: merge.as_ref().map(|(state, _)| *state),
        merge_state_against: merge.map(|(_, against)| against),
        dirty,
        upstream,
        last_activity_unix,
        size: Some(walk_size(dir)),
        errors,
    }
}

/// The best merge state of a worktree over its repo's default branches, with the default that gave it; `None`
/// when the repo has no default branch.
///
/// # Errors
///
/// When a git command fails unexpectedly.
pub fn merge_state(
    dir: &Path,
    defaults: &DefaultBranches,
    record: &WorktreeRecord,
) -> Result<Option<(MergeState, String)>> {
    let defaults = defaults.refs();
    let Some(branch) = &record.branch else {
        let head = record.head.as_deref().unwrap_or("HEAD");
        for (name, default_ref) in &defaults {
            if is_ancestor(dir, head, default_ref)? {
                return Ok(Some((
                    MergeState::Detached { contained: true },
                    name.clone(),
                )));
            }
        }
        return Ok(defaults
            .first()
            .map(|(name, _)| (MergeState::Detached { contained: false }, name.clone())));
    };
    let branch_ref = format!("refs/heads/{branch}");
    let mut best: Option<(MergeState, String)> = None;
    for (name, default_ref) in &defaults {
        let state = state_against(dir, &branch_ref, default_ref)?;
        if best
            .as_ref()
            .is_none_or(|(current, _)| state.rank() < current.rank())
        {
            best = Some((state, name.clone()));
        }
        if state == MergeState::Ancestor {
            break;
        }
    }
    Ok(best)
}

fn state_against(dir: &Path, branch: &str, default: &str) -> Result<MergeState> {
    if is_ancestor(dir, branch, default)? {
        return Ok(MergeState::Ancestor);
    }
    let cherry = git::run(dir, &["cherry", default, branch])?;
    if !cherry.is_empty() && !cherry.lines().any(|line| line.starts_with('+')) {
        return Ok(MergeState::PatchesApplied);
    }
    if content_contained(dir, branch, default)? {
        return Ok(MergeState::ContentContained);
    }
    let commits = count(dir, &format!("{default}..{branch}"))?;
    Ok(MergeState::Unmerged { commits })
}

fn is_ancestor(dir: &Path, commit: &str, default: &str) -> Result<bool> {
    let args = ["merge-base", "--is-ancestor", commit, default];
    let status = git::run_status(dir, &args)?;
    match status.code {
        Some(0) => Ok(true),
        Some(1) => Ok(false),
        _ => Err(git::failure(dir, &args, &status)),
    }
}

/// Whether `git merge-tree --write-tree <default> <branch>` gives the default branch's own tree. The objects the
/// merge writes go to a scratch object directory, so the repo is left untouched.
fn content_contained(dir: &Path, branch: &str, default: &str) -> Result<bool> {
    let default_tree = git::run(dir, &["rev-parse", &format!("{default}^{{tree}}")])?;
    let scratch = ScratchObjects::create(dir)?;
    let args = ["merge-tree", "--write-tree", default, branch];
    let envs = [
        ("GIT_OBJECT_DIRECTORY", scratch.dir.as_os_str()),
        (
            "GIT_ALTERNATE_OBJECT_DIRECTORIES",
            scratch.alternate.as_os_str(),
        ),
    ];
    let status = git::run_status_with_env(dir, &args, &envs)?;
    match status.code {
        Some(0) => Ok(status.stdout.lines().next() == Some(default_tree.as_str())),
        Some(1) => Ok(false),
        _ => {
            debug!(
                "{:#}; treating the branch as not content-contained",
                git::failure(dir, &args, &status)
            );
            Ok(false)
        }
    }
}

/// A temporary object directory that borrows the repo's objects as an alternate; removed on drop.
struct ScratchObjects {
    dir: PathBuf,
    alternate: PathBuf,
}

impl ScratchObjects {
    fn create(worktree: &Path) -> Result<Self> {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let common = git::run(
            worktree,
            &["rev-parse", "--path-format=absolute", "--git-common-dir"],
        )?;
        let alternate = discover::from_git_path(&common).join("objects");
        let name = format!(
            "worktree-sweep-objects-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let dir = std::env::temp_dir().join(name);
        fs::create_dir_all(&dir)
            .with_context(|| format!("cannot create scratch object directory {}", dir.display()))?;
        Ok(Self { dir, alternate })
    }
}

impl Drop for ScratchObjects {
    fn drop(&mut self) {
        if let Err(error) = fs::remove_dir_all(&self.dir) {
            warn!(
                "cannot remove scratch object directory {}: {error}",
                self.dir.display()
            );
        }
    }
}

/// Counts modified and untracked entries with `git status --porcelain=v1 -z`.
///
/// # Errors
///
/// When git fails.
pub fn dirty(dir: &Path) -> Result<Dirty> {
    let text = git::run_raw(
        dir,
        &["status", "--porcelain=v1", "-z", "--untracked-files=normal"],
    )?;
    Ok(parse_status_z(&text))
}

/// Parses `git status --porcelain=v1 -z` output into dirty counts.
#[must_use]
pub fn parse_status_z(text: &str) -> Dirty {
    let mut dirty = Dirty::default();
    let mut fields = text.split('\0').filter(|field| !field.is_empty());
    while let Some(entry) = fields.next() {
        let code = entry.get(..2).unwrap_or(entry);
        match code {
            "??" => dirty.untracked = dirty.untracked.saturating_add(1),
            "!!" => {}
            _ => {
                dirty.modified = dirty.modified.saturating_add(1);
                if code.contains(['R', 'C']) {
                    fields.next();
                }
            }
        }
    }
    dirty
}

/// The upstream state of a local branch.
///
/// # Errors
///
/// When git fails unexpectedly.
pub fn upstream(dir: &Path, branch: &str) -> Result<Upstream> {
    let upstream_spec = format!("{branch}@{{upstream}}");
    let resolved = git::run_status(dir, &["rev-parse", "--abbrev-ref", &upstream_spec])?;
    if resolved.success() {
        let ahead = count(dir, &format!("{upstream_spec}..refs/heads/{branch}"))?;
        return Ok(Upstream::Tracking { ahead });
    }
    let configured = git::run_status(dir, &["config", "--get", &format!("branch.{branch}.merge")])?;
    Ok(if configured.success() {
        Upstream::Gone
    } else {
        Upstream::None
    })
}

/// The later of the HEAD commit time and the modification time of the worktree's index, in unix seconds.
///
/// # Errors
///
/// Only when git cannot be started.
pub fn last_activity(dir: &Path) -> Result<Option<i64>> {
    let log = git::run_status(dir, &["log", "-1", "--format=%ct"])?;
    let commit_time = if log.success() {
        log.stdout.trim().parse::<i64>().ok()
    } else {
        None
    };
    let index_time = discover::read_gitdir_file(dir)
        .and_then(|gitdir| fs::metadata(gitdir.join("index")).ok())
        .and_then(|meta| unix_seconds(meta.modified().ok()?));
    Ok(commit_time.max(index_time))
}

/// Measures a folder without following junctions or symbolic links; an unreadable entry is counted, not fatal.
/// A link given as `root` measures as empty.
#[must_use]
pub fn walk_size(root: &Path) -> SizeInfo {
    let mut info = SizeInfo::default();
    match fs::symlink_metadata(root) {
        Ok(meta) => {
            info.last_write_unix = meta.modified().ok().and_then(unix_seconds);
            if discover::is_link(&meta) {
                return info;
            }
        }
        Err(error) => {
            debug!("cannot read {}: {error}", root.display());
            info.unreadable = 1;
            return info;
        }
    }
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let entries = match fs::read_dir(&dir) {
            Ok(entries) => entries,
            Err(error) => {
                debug!("cannot list {}: {error}", dir.display());
                info.unreadable = info.unreadable.saturating_add(1);
                continue;
            }
        };
        for entry in entries {
            let Ok((path, meta)) = entry.and_then(|entry| Ok((entry.path(), entry.metadata()?)))
            else {
                info.unreadable = info.unreadable.saturating_add(1);
                continue;
            };
            info.last_write_unix = info
                .last_write_unix
                .max(meta.modified().ok().and_then(unix_seconds));
            if discover::is_link(&meta) {
                continue;
            }
            if meta.is_dir() {
                stack.push(path);
            } else {
                info.files += 1;
                info.bytes += meta.len();
            }
        }
    }
    info
}

/// Runs `f` over `items` on at most `available_parallelism()` scoped threads, keeping the input order.
pub fn parallel_map<T, R, F>(items: &[T], f: F) -> Vec<R>
where
    T: Sync,
    R: Send,
    F: Fn(&T) -> R + Sync,
{
    let workers = std::thread::available_parallelism()
        .map_or(1, NonZeroUsize::get)
        .min(items.len())
        .max(1);
    let next = AtomicUsize::new(0);
    let mut results: Vec<(usize, R)> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..workers)
            .map(|_| {
                scope.spawn(|| {
                    let mut done = Vec::new();
                    loop {
                        let index = next.fetch_add(1, Ordering::Relaxed);
                        let Some(item) = items.get(index) else {
                            break;
                        };
                        done.push((index, f(item)));
                    }
                    done
                })
            })
            .collect();
        handles
            .into_iter()
            .flat_map(|handle| {
                handle
                    .join()
                    .unwrap_or_else(|payload| std::panic::resume_unwind(payload))
            })
            .collect()
    });
    results.sort_by_key(|(index, _)| *index);
    results.into_iter().map(|(_, result)| result).collect()
}

fn ref_exists(dir: &Path, refname: &str) -> Result<bool> {
    let args = ["rev-parse", "--verify", "--quiet", refname];
    let status = git::run_status(dir, &args)?;
    match status.code {
        Some(0) => Ok(true),
        Some(1) => Ok(false),
        _ => Err(git::failure(dir, &args, &status)),
    }
}

fn count(dir: &Path, range: &str) -> Result<u32> {
    let text = git::run(dir, &["rev-list", "--count", range])?;
    text.parse().with_context(|| {
        format!(
            "`git rev-list --count {range}` in {} printed {text:?}",
            dir.display()
        )
    })
}

fn keep<T>(errors: &mut Vec<String>, result: Result<T>) -> Option<T> {
    result
        .map_err(|error| errors.push(format!("{error:#}")))
        .ok()
}

fn unix_seconds(time: SystemTime) -> Option<i64> {
    let seconds = time.duration_since(UNIX_EPOCH).ok()?.as_secs();
    i64::try_from(seconds).ok()
}
