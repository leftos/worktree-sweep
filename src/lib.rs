//! Finds stale git worktrees and orphan folders under a root, with the signals needed to decide what to remove.

pub mod discover;
pub mod git;
pub mod pick;
pub mod recycle;
pub mod remove;
pub mod report;
pub mod signals;
pub mod unlock;

use std::path::Path;

use anyhow::Result;
use tracing::warn;

use crate::discover::{Orphan, WorktreeRecord};
use crate::report::{Candidate, OrphanCandidate, RegisteredCandidate, RepoReport, Report};
use crate::signals::DefaultBranches;

enum Job<'a> {
    Registered {
        repo: &'a Path,
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

    let mut jobs = Vec::new();
    for (repo, repo_defaults) in discovery.repos.iter().zip(&defaults) {
        for record in repo.worktrees.iter().skip(1) {
            jobs.push(Job::Registered {
                repo: &repo.path,
                defaults: repo_defaults,
                record,
            });
        }
    }
    jobs.extend(discovery.orphans.iter().map(Job::Orphan));

    let candidates = signals::parallel_map(&jobs, |job| match job {
        Job::Registered {
            repo,
            defaults,
            record,
        } => Candidate::Registered(RegisteredCandidate {
            path: record.path.clone(),
            repo: repo.to_path_buf(),
            branch: record.branch.clone(),
            head: record.head.clone(),
            prunable: record.prunable.clone(),
            git_lock: record.locked.clone(),
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
