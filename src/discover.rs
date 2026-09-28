//! Finds the repos under a root, their registered worktrees, the container dirs and the orphan folders in them.
//!
//! The walk never follows a symbolic link or junction: a link inside a container is reported as an orphan of
//! its own kind and never entered.

use std::collections::HashSet;
use std::fs::{self, Metadata};
use std::io;
use std::path::{MAIN_SEPARATOR, Path, PathBuf};

use anyhow::{Context, Result};
use serde::Serialize;
use tracing::{debug, warn};

use crate::git;

/// One record of `git worktree list --porcelain`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct WorktreeRecord {
    /// The worktree folder, with the platform's separators.
    pub path: PathBuf,
    /// The commit checked out, when git reports one.
    pub head: Option<String>,
    /// The branch checked out, without `refs/heads/`; `None` when detached.
    pub branch: Option<String>,
    /// Whether HEAD is detached.
    pub detached: bool,
    /// Whether the record is a bare repository.
    pub bare: bool,
    /// The `git worktree lock` reason; `Some("")` when locked without one.
    pub locked: Option<String>,
    /// Why git considers the registration prunable (its folder is gone); `None` when it is not.
    pub prunable: Option<String>,
}

/// A repo found at depth 1 of the root.
#[derive(Debug, Clone)]
pub struct Repo {
    /// The repo's main worktree folder.
    pub path: PathBuf,
    /// Every worktree git lists for it; the first is the main worktree.
    pub worktrees: Vec<WorktreeRecord>,
}

/// What an orphan entry is on disk.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum OrphanKind {
    /// A plain folder.
    Folder,
    /// A junction or symbolic link; removing it removes the link, never its target.
    Link,
}

/// A folder in a container dir that no repo under the root registers as a worktree.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Orphan {
    /// The orphan's path.
    pub path: PathBuf,
    /// The container dir it was found in.
    pub container: PathBuf,
    /// Whether it is a folder or a link.
    pub orphan_kind: OrphanKind,
    /// Where a link points; `None` for a folder.
    pub link_target: Option<PathBuf>,
    /// Whether its `.git` is a file whose `gitdir:` target no longer exists.
    pub stale_gitdir: bool,
    /// The git dir its `.git` file points to when that git dir still exists: a repo outside the root still
    /// registers the folder as a worktree. `None` for a link, a folder without a `.git` file, or a stale one.
    pub live_gitdir: Option<PathBuf>,
    /// Whether its `.git` is a directory.
    pub has_git_dir: bool,
}

/// Everything found under a root.
#[derive(Debug, Clone)]
pub struct Discovery {
    /// The root, made absolute.
    pub root: PathBuf,
    /// Repos at depth 1 of the root.
    pub repos: Vec<Repo>,
    /// Container dirs that were walked for orphans.
    pub containers: Vec<PathBuf>,
    /// Orphans found in the container dirs.
    pub orphans: Vec<Orphan>,
}

impl Discovery {
    /// Every registered worktree that is a candidate, with its repo; a repo's main worktree never is.
    pub fn registered(&self) -> impl Iterator<Item = (&Repo, &WorktreeRecord)> {
        self.repos.iter().flat_map(|repo| {
            repo.worktrees
                .iter()
                .skip(1)
                .map(move |record| (repo, record))
        })
    }
}

/// Finds the repos, registered worktrees, container dirs and orphans under `root`.
///
/// A child that cannot be read (such as `System Volume Information`) is skipped with a debug log, and a repo
/// whose worktrees git cannot list is skipped with a warning.
///
/// # Errors
///
/// When `root` itself cannot be listed.
pub fn discover(root: &Path) -> Result<Discovery> {
    let root = std::path::absolute(root)
        .with_context(|| format!("cannot make {} absolute", root.display()))?;
    let children = read_children(&root)
        .with_context(|| format!("cannot list the root folder {}", root.display()))?;

    let mut repos = Vec::new();
    let mut containers = Vec::new();
    for child in children {
        let Some(meta) = metadata_or_skip(&child) else {
            continue;
        };
        if !meta.is_dir() || is_link(&meta) {
            continue;
        }
        if is_git_dir(&child.join(".git")) {
            match list_worktrees(&child) {
                Ok(worktrees) => {
                    let claude = child.join(".claude").join("worktrees");
                    if metadata_or_skip(&claude).is_some_and(|m| m.is_dir() && !is_link(&m)) {
                        containers.push(claude);
                    }
                    repos.push(Repo {
                        path: child,
                        worktrees,
                    });
                }
                Err(error) => warn!("skipping repo {}: {error:#}", child.display()),
            }
        } else if is_container_name(&child) {
            containers.push(child);
        }
    }

    let known: HashSet<String> = repos
        .iter()
        .flat_map(|repo| repo.worktrees.iter().map(|record| path_key(&record.path)))
        .collect();
    containers.retain(|container| !known.contains(&path_key(container)));

    let mut orphans = Vec::new();
    for container in &containers {
        walk_container(container, container, &known, &mut orphans);
    }
    Ok(Discovery {
        root,
        repos,
        containers,
        orphans,
    })
}

/// Parses `git worktree list --porcelain` output into records, in git's order (the main worktree first).
#[must_use]
pub fn parse_worktree_porcelain(text: &str) -> Vec<WorktreeRecord> {
    let mut records = Vec::new();
    let mut current: Option<WorktreeRecord> = None;
    for line in text.lines() {
        if line.is_empty() {
            records.extend(current.take());
            continue;
        }
        let (key, value) = line
            .split_once(' ')
            .map_or((line, None), |(key, value)| (key, Some(value)));
        if key == "worktree" {
            records.extend(current.take());
            current = Some(WorktreeRecord {
                path: from_git_path(value.unwrap_or_default()),
                ..WorktreeRecord::default()
            });
            continue;
        }
        let Some(record) = current.as_mut() else {
            debug!("worktree porcelain line outside a record: {line}");
            continue;
        };
        match key {
            "HEAD" => record.head = value.map(str::to_owned),
            "branch" => {
                record.branch =
                    value.map(|v| v.strip_prefix("refs/heads/").unwrap_or(v).to_owned());
            }
            "detached" => record.detached = true,
            "bare" => record.bare = true,
            "locked" => record.locked = Some(value.unwrap_or_default().to_owned()),
            "prunable" => record.prunable = Some(value.unwrap_or_default().to_owned()),
            _ => debug!("unknown worktree porcelain key: {line}"),
        }
    }
    records.extend(current);
    records
}

/// A comparison key for a path: separators unified and, on Windows, case folded; no trailing separator.
#[must_use]
pub fn path_key(path: &Path) -> String {
    let text = path.to_string_lossy();
    let mut key = if cfg!(windows) {
        text.replace('/', "\\").to_lowercase()
    } else {
        text.into_owned()
    };
    while key.len() > 1 && key.ends_with(MAIN_SEPARATOR) && !key.ends_with(":\\") {
        key.pop();
    }
    key
}

/// Converts a path git printed (forward slashes on Windows too) to one with the platform's separators.
#[must_use]
pub fn from_git_path(text: &str) -> PathBuf {
    if cfg!(windows) {
        PathBuf::from(text.replace('/', "\\"))
    } else {
        PathBuf::from(text)
    }
}

/// The git dir a worktree's `.git` file points to (`gitdir: <path>`), resolved against the worktree when relative.
/// `None` when `.git` is missing, is a directory, or does not hold a `gitdir:` line.
#[must_use]
pub fn read_gitdir_file(worktree: &Path) -> Option<PathBuf> {
    let dot_git = worktree.join(".git");
    let text = match fs::read_to_string(&dot_git) {
        Ok(text) => text,
        Err(error) => {
            debug!(
                "cannot read {} as a gitdir file: {error}",
                dot_git.display()
            );
            return None;
        }
    };
    let target = text
        .lines()
        .find_map(|line| line.strip_prefix("gitdir:"))?
        .trim();
    let target = from_git_path(target);
    Some(if target.is_absolute() {
        target
    } else {
        worktree.join(target)
    })
}

/// Whether metadata (read without following links) describes a junction, a symbolic link, or a folder that is a
/// reparse point of another kind; none of these is entered by a walk.
#[must_use]
pub fn is_link(meta: &Metadata) -> bool {
    meta.file_type().is_symlink() || (meta.is_dir() && is_reparse_point(meta))
}

#[cfg(windows)]
fn is_reparse_point(meta: &Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
    meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

#[cfg(not(windows))]
fn is_reparse_point(_meta: &Metadata) -> bool {
    false
}

fn walk_container(
    container: &Path,
    dir: &Path,
    known: &HashSet<String>,
    orphans: &mut Vec<Orphan>,
) {
    let children = match read_children(dir) {
        Ok(children) => children,
        Err(error) => {
            debug!("skipping unreadable folder {}: {error}", dir.display());
            return;
        }
    };
    for child in children {
        let Some(meta) = metadata_or_skip(&child) else {
            continue;
        };
        let key = path_key(&child);
        if known.contains(&key) {
            continue;
        }
        if is_link(&meta) {
            orphans.push(link_orphan(container, child));
            continue;
        }
        if !meta.is_dir() {
            debug!("skipping file {} in a container", child.display());
            continue;
        }
        let prefix = format!("{key}{MAIN_SEPARATOR}");
        if known.iter().any(|known_key| known_key.starts_with(&prefix)) {
            walk_container(container, &child, known, orphans);
        } else {
            orphans.push(folder_orphan(container, child));
        }
    }
}

fn link_orphan(container: &Path, path: PathBuf) -> Orphan {
    let link_target = match fs::read_link(&path) {
        Ok(target) => Some(strip_verbatim(target)),
        Err(error) => {
            debug!("cannot read the target of link {}: {error}", path.display());
            None
        }
    };
    Orphan {
        path,
        container: container.to_path_buf(),
        orphan_kind: OrphanKind::Link,
        link_target,
        stale_gitdir: false,
        live_gitdir: None,
        has_git_dir: false,
    }
}

fn folder_orphan(container: &Path, path: PathBuf) -> Orphan {
    let dot_git = path.join(".git");
    let git_meta = metadata_or_skip(&dot_git);
    let has_git_dir = git_meta.as_ref().is_some_and(Metadata::is_dir);
    let gitdir = if git_meta.as_ref().is_some_and(Metadata::is_file) {
        read_gitdir_file(&path)
    } else {
        None
    };
    let (stale_gitdir, live_gitdir) = match gitdir {
        None => (false, None),
        Some(gitdir) => match gitdir.try_exists() {
            Ok(false) => (true, None),
            Ok(true) => (false, Some(gitdir)),
            Err(error) => {
                debug!(
                    "cannot check gitdir {}: {error}; treating it as still registered",
                    gitdir.display()
                );
                (false, Some(gitdir))
            }
        },
    };
    Orphan {
        path,
        container: container.to_path_buf(),
        orphan_kind: OrphanKind::Folder,
        link_target: None,
        stale_gitdir,
        live_gitdir,
        has_git_dir,
    }
}

/// Removes the `\\?\` or `\??\` prefix Windows puts on some link targets.
#[must_use]
pub fn strip_verbatim(path: PathBuf) -> PathBuf {
    let text = path.to_string_lossy();
    match text
        .strip_prefix(r"\\?\")
        .or_else(|| text.strip_prefix(r"\??\"))
    {
        Some(rest) if !rest.starts_with("UNC\\") => PathBuf::from(rest),
        _ => path,
    }
}

/// Lists the worktrees git registers for the repo at `repo` (any folder git resolves to it), main worktree first.
///
/// # Errors
///
/// When git fails.
pub fn list_worktrees(repo: &Path) -> Result<Vec<WorktreeRecord>> {
    let text = git::run(repo, &["worktree", "list", "--porcelain"])?;
    Ok(parse_worktree_porcelain(&text))
}

fn is_git_dir(dot_git: &Path) -> bool {
    metadata_or_skip(dot_git).is_some_and(|meta| meta.is_dir() && !is_link(&meta))
}

fn is_container_name(path: &Path) -> bool {
    path.file_name().is_some_and(|name| {
        let name = name.to_string_lossy().to_lowercase();
        let wt_suffix = name
            .strip_suffix("wt")
            .is_some_and(|rest| rest.ends_with(['.', '-']));
        wt_suffix || name.ends_with("worktrees")
    })
}

/// The entries of a folder, sorted by path; an entry that cannot be read is skipped with a debug log.
fn read_children(dir: &Path) -> io::Result<Vec<PathBuf>> {
    let mut children = Vec::new();
    for entry in fs::read_dir(dir)? {
        match entry {
            Ok(entry) => children.push(entry.path()),
            Err(error) => debug!("skipping an unreadable entry of {}: {error}", dir.display()),
        }
    }
    children.sort();
    Ok(children)
}

/// Metadata without following links; `None` (with a debug log unless the path is simply missing) when unreadable.
fn metadata_or_skip(path: &Path) -> Option<Metadata> {
    match fs::symlink_metadata(path) {
        Ok(meta) => Some(meta),
        Err(error) => {
            if error.kind() != io::ErrorKind::NotFound {
                debug!("skipping unreadable {}: {error}", path.display());
            }
            None
        }
    }
}
