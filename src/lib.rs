//! Finds stale git worktrees and orphan folders under a root, with the signals needed to decide what to remove.

pub mod agent;
pub mod discover;
pub mod git;
pub mod handle_csv;
pub mod holders;
pub mod pick;
pub mod recycle;
pub mod remove;
pub mod report;
pub mod signals;
pub mod tui;
pub mod unlock;

use std::fs;
use std::io;
use std::path::{Component, MAIN_SEPARATOR, Path, PathBuf};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use tracing::warn;

use crate::agent::Released;
use crate::discover::{Orphan, WorktreeRecord, path_key};
use crate::report::{Candidate, OrphanCandidate, RegisteredCandidate, RepoReport, Report};
use crate::signals::DefaultBranches;

enum Job<'a> {
    Registered {
        repo: &'a Path,
        common: Option<&'a Path>,
        defaults: &'a DefaultBranches,
        record: &'a WorktreeRecord,
    },
    Orphan(&'a Orphan),
}

/// Scans `root`: discovers repos, registered worktrees and orphans, then reads every candidate's signals in
/// parallel. Read-only: nothing under `root` is written.
///
/// # Errors
///
/// When `root` cannot be listed.
pub fn scan(root: &Path) -> Result<Report> {
    let discovery = discover::discover(root)?;
    let defaults: Vec<DefaultBranches> = discovery
        .repos
        .iter()
        .map(|repo| {
            signals::default_branches(&repo.path).unwrap_or_else(|error| {
                warn!(
                    "cannot find the default branches of {}: {error:#}",
                    repo.path.display()
                );
                DefaultBranches::default()
            })
        })
        .collect();
    let commons: Vec<Option<PathBuf>> = discovery
        .repos
        .iter()
        .map(|repo| {
            common_dir(&repo.path).unwrap_or_else(|error| {
                warn!(
                    "cannot find the git dir of {}: {error:#}",
                    repo.path.display()
                );
                None
            })
        })
        .collect();

    let mut jobs = Vec::new();
    for ((repo, repo_defaults), common) in discovery.repos.iter().zip(&defaults).zip(&commons) {
        for record in repo.worktrees.iter().skip(1) {
            jobs.push(Job::Registered {
                repo: &repo.path,
                common: common.as_deref(),
                defaults: repo_defaults,
                record,
            });
        }
    }
    jobs.extend(discovery.orphans.iter().map(Job::Orphan));

    let candidates = signals::parallel_map(&jobs, |job| match job {
        Job::Registered {
            repo,
            common,
            defaults,
            record,
        } => Candidate::Registered(RegisteredCandidate {
            path: record.path.clone(),
            repo: repo.to_path_buf(),
            branch: record.branch.clone(),
            head: record.head.clone(),
            prunable: record.prunable.clone(),
            git_lock: record.locked.clone(),
            released: released_marker(*common, record),
            signals: signals::worktree_signals(defaults, record),
        }),
        Job::Orphan(orphan) => Candidate::Orphan(OrphanCandidate {
            orphan: (*orphan).clone(),
            size: signals::walk_size(&orphan.path),
        }),
    });

    let repos = discovery
        .repos
        .iter()
        .zip(defaults)
        .map(|(repo, default_branches)| RepoReport {
            path: repo.path.clone(),
            default_branches,
        })
        .collect();
    Ok(Report {
        root: discovery.root,
        repos,
        candidates,
    })
}

/// Why a path given to [`resolve_one`] is not a removable worktree.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RefusalReason {
    /// Nothing is there, and no repo registers a worktree there.
    NotFound,
    /// It is not in a git repo, or is a file or a git dir rather than a worktree.
    NotAWorktree,
    /// It is a repo's main worktree.
    MainWorktree,
    /// It is a bare repo.
    BareRepo,
    /// It lies inside a worktree but is not its root.
    Subfolder,
    /// It is a junction or symbolic link.
    Link,
    /// It has a `.git` file no repo registers as a worktree, or a registration git calls prunable (its `.git`
    /// file is gone) while the folder remains.
    Orphan,
}

/// A path [`resolve_one`] refused, with the repo when one was found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Refusal {
    /// Why it was refused.
    pub reason: RefusalReason,
    /// The repo's main worktree (or bare folder), when the path was found in one.
    pub repo: Option<PathBuf>,
}

/// A registered linked worktree, resolved from one path.
#[derive(Debug, Clone)]
pub struct Resolved {
    /// The worktree and its signals.
    pub candidate: RegisteredCandidate,
    /// The repo's main worktree (the bare folder for a bare repo).
    pub main_worktree: PathBuf,
}

/// Resolves `path` to the registered linked worktree whose root it is. A registered record whose folder is gone
/// resolves as prunable, with the merge state of its branch read from the main worktree.
///
/// # Errors
///
/// The outer error when `path` cannot be made absolute or resolved, or git fails listing a repo's worktrees; the
/// inner [`Refusal`] when the path is not a removable worktree.
pub fn resolve_one(path: &Path) -> Result<Result<Resolved, Refusal>> {
    let absolute = std::path::absolute(path)
        .with_context(|| format!("cannot make {} absolute", path.display()))?;
    let meta = match fs::symlink_metadata(&absolute) {
        Ok(meta) => meta,
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            return resolve_missing(&absolute);
        }
        Err(error) => {
            return Err(error).with_context(|| format!("cannot read {}", absolute.display()));
        }
    };
    if discover::is_link(&meta) {
        return Ok(Err(refusal(RefusalReason::Link, None)));
    }
    if !meta.is_dir() {
        return Ok(Err(refusal(RefusalReason::NotAWorktree, None)));
    }
    let target = discover::strip_verbatim(
        fs::canonicalize(&absolute)
            .with_context(|| format!("cannot resolve {}", absolute.display()))?,
    );
    let has_git_file = target.join(".git").is_file();
    let Some(common) = common_dir(&target)? else {
        let reason = if has_git_file {
            RefusalReason::Orphan
        } else {
            RefusalReason::NotAWorktree
        };
        return Ok(Err(refusal(reason, None)));
    };
    let records = discover::list_worktrees(&common)?;
    let Some(main) = records.first() else {
        return Ok(Err(refusal(RefusalReason::NotAWorktree, None)));
    };
    let main_worktree = main.path.clone();
    let key = discover::path_key(&target);
    if let Some(index) = records.iter().position(|record| record_key(record) == key) {
        if records[index].prunable.is_some() {
            // Git calls it prunable (its `.git` file is gone) while the folder is still here: no live registration
            // backs the folder, so removing it is not a prune.
            return Ok(Err(refusal(RefusalReason::Orphan, Some(main_worktree))));
        }
        return Ok(resolve_record(&records, index, &common));
    }
    let under_record = records
        .iter()
        .any(|record| key.starts_with(&format!("{}{MAIN_SEPARATOR}", record_key(record))));
    let reason = if has_git_file {
        RefusalReason::Orphan
    } else if under_record {
        RefusalReason::Subfolder
    } else {
        RefusalReason::NotAWorktree
    };
    Ok(Err(refusal(reason, Some(main_worktree))))
}

/// Resolves a path with nothing on disk: a prunable record of the repo containing its nearest existing ancestor,
/// or of the repo a container ancestor (`<repo>.wt`, `<repo>-wt`, `<repo>worktrees`) sits beside.
fn resolve_missing(absolute: &Path) -> Result<Result<Resolved, Refusal>> {
    let Some(existing) = absolute.ancestors().skip(1).find(|dir| dir.is_dir()) else {
        return Ok(Err(refusal(RefusalReason::NotFound, None)));
    };
    let rest = absolute
        .strip_prefix(existing)
        .with_context(|| format!("{} is not under {}", absolute.display(), existing.display()))?;
    let canonical = discover::strip_verbatim(
        fs::canonicalize(existing)
            .with_context(|| format!("cannot resolve {}", existing.display()))?,
    );
    let key = discover::path_key(&canonical.join(rest));
    let mut repos: Vec<PathBuf> = [Some(canonical.clone()), container_repo(&canonical)]
        .into_iter()
        .flatten()
        .collect();
    for child in child_repos(&canonical) {
        if !repos
            .iter()
            .any(|repo| discover::path_key(repo) == discover::path_key(&child))
        {
            repos.push(child);
        }
    }
    for repo in &repos {
        let Some(common) = common_dir(repo)? else {
            continue;
        };
        let records = match discover::list_worktrees(&common) {
            Ok(records) => records,
            Err(error) => {
                warn!("skipping repo {}: {error:#}", repo.display());
                continue;
            }
        };
        if let Some(index) = records.iter().position(|record| record_key(record) == key) {
            return Ok(resolve_record(&records, index, &common));
        }
    }
    Ok(Err(refusal(RefusalReason::NotFound, None)))
}

/// The repos that are direct children of `dir` (a child folder with a `.git` folder, as discovery finds them),
/// by path; a folder that cannot be listed has none.
fn child_repos(dir: &Path) -> Vec<PathBuf> {
    let entries = match fs::read_dir(dir) {
        Ok(entries) => entries,
        Err(error) => {
            warn!("cannot list {}: {error}", dir.display());
            return Vec::new();
        }
    };
    let is_plain_dir = |path: &Path| {
        fs::symlink_metadata(path).is_ok_and(|meta| meta.is_dir() && !discover::is_link(&meta))
    };
    let mut repos: Vec<PathBuf> = entries
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| is_plain_dir(path) && is_plain_dir(&path.join(".git")))
        .collect();
    repos.sort();
    repos
}

/// The record at `index` of the repo whose common git dir is `common`, refused when it is the main worktree or
/// bare, else built into a candidate.
fn resolve_record(
    records: &[WorktreeRecord],
    index: usize,
    common: &Path,
) -> Result<Resolved, Refusal> {
    let main_worktree = records[0].path.clone();
    let record = &records[index];
    if record.bare {
        return Err(refusal(RefusalReason::BareRepo, Some(main_worktree)));
    }
    if index == 0 {
        return Err(refusal(RefusalReason::MainWorktree, Some(main_worktree)));
    }
    Ok(Resolved {
        candidate: build_candidate(&main_worktree, common, record),
        main_worktree,
    })
}

fn build_candidate(repo: &Path, common: &Path, record: &WorktreeRecord) -> RegisteredCandidate {
    let defaults = signals::default_branches(repo).unwrap_or_else(|error| {
        warn!(
            "cannot find the default branches of {}: {error:#}",
            repo.display()
        );
        DefaultBranches::default()
    });
    let mut worktree_signals = signals::worktree_signals(&defaults, record);
    if record.prunable.is_some() {
        match signals::merge_state(repo, &defaults, record) {
            Ok(Some((state, against))) => {
                worktree_signals.merge_state = Some(state);
                worktree_signals.merge_state_against = Some(against);
            }
            Ok(None) => {}
            Err(error) => worktree_signals.errors.push(format!("{error:#}")),
        }
    }
    RegisteredCandidate {
        path: record.path.clone(),
        repo: repo.to_path_buf(),
        branch: record.branch.clone(),
        head: record.head.clone(),
        prunable: record.prunable.clone(),
        git_lock: record.locked.clone(),
        released: released_marker(Some(common), record),
        signals: worktree_signals,
    }
}

/// The released marker in a worktree's admin dir, `common` being its repo's common git dir. A missing admin dir
/// or marker is `None`; so is a marker that cannot be read or parsed, with a warning.
fn released_marker(common: Option<&Path>, record: &WorktreeRecord) -> Option<Released> {
    let admin = admin_dir(common, &record.path)?;
    agent::read_marker(&admin).unwrap_or_else(|error| {
        warn!("ignoring the released marker: {error:#}");
        None
    })
}

/// A worktree's admin dir: from its `.git` file, or, when the folder or that file is gone (a prunable
/// registration), the entry under `<common>/worktrees` whose `gitdir` file points at `<worktree>/.git`. A
/// relative `gitdir` (`worktree.useRelativePaths`) is resolved against the entry.
fn admin_dir(common: Option<&Path>, worktree: &Path) -> Option<PathBuf> {
    if let Some(admin) = discover::read_gitdir_file(worktree) {
        return Some(admin);
    }
    let wanted = path_key(worktree);
    let entries = fs::read_dir(common?.join("worktrees")).ok()?;
    entries.flatten().map(|entry| entry.path()).find(|admin| {
        fs::read_to_string(admin.join("gitdir")).is_ok_and(|text| {
            let target = discover::from_git_path(text.trim());
            let target = lexical_normalize(&if target.is_absolute() {
                target
            } else {
                admin.join(target)
            });
            target
                .parent()
                .is_some_and(|dot_git_parent| path_key(dot_git_parent) == wanted)
        })
    })
}

/// `path` with `.` dropped and each `..` taking off the component before it, without touching the disk.
fn lexical_normalize(path: &Path) -> PathBuf {
    let mut normal = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normal.pop();
            }
            other => normal.push(other),
        }
    }
    normal
}

/// The repo's common git dir as seen from `dir`; `None` when git finds no repo there.
fn common_dir(dir: &Path) -> Result<Option<PathBuf>> {
    let status = git::run_status(
        dir,
        &["rev-parse", "--path-format=absolute", "--git-common-dir"],
    )?;
    Ok(status
        .success()
        .then(|| discover::from_git_path(status.stdout.trim())))
}

/// A record's comparison key, from its canonical path when the folder exists.
fn record_key(record: &WorktreeRecord) -> String {
    let path = fs::canonicalize(&record.path)
        .map_or_else(|_| record.path.clone(), discover::strip_verbatim);
    discover::path_key(&path)
}

/// The repo a container folder sits beside: `D:\x` for `D:\x.wt`, `D:\x-wt` or `D:\x.worktrees`.
fn container_repo(dir: &Path) -> Option<PathBuf> {
    let name = dir.file_name()?.to_string_lossy().into_owned();
    let lower = name.to_lowercase();
    let stem_len = [".wt", "-wt", ".worktrees", "-worktrees", "worktrees"]
        .iter()
        .find(|suffix| lower.ends_with(*suffix))
        .map(|suffix| name.len() - suffix.len())?;
    let stem = name.get(..stem_len).filter(|stem| !stem.is_empty())?;
    Some(dir.with_file_name(stem))
}

fn refusal(reason: RefusalReason, repo: Option<PathBuf>) -> Refusal {
    Refusal { reason, repo }
}
