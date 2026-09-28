//! Candidates and reports for the TUI tests, built without git.

use std::path::PathBuf;

use crate::agent::{Reason, Released};
use crate::discover::{Orphan, OrphanKind};
use crate::recycle::BinCapacity;
use crate::report::{Candidate, OrphanCandidate, RegisteredCandidate, Report};
use crate::signals::{Dirty, MergeState, SizeInfo, Upstream, WorktreeSignals};

/// The time every test measures ages from.
pub(crate) const NOW: i64 = 1_790_000_000;
pub(crate) const DAY: i64 = 86_400;

/// A Recycle Bin roomy enough for every fixture.
pub(crate) const ROOMY: BinCapacity = BinCapacity {
    max_capacity_mb: 1024,
    nuke_on_delete: false,
};

pub(crate) fn root() -> PathBuf {
    PathBuf::from(r"D:\")
}

/// UTC, whatever the machine's time zone.
pub(crate) fn utc(_unix: i64) -> i32 {
    0
}

pub(crate) fn size(bytes: u64) -> SizeInfo {
    SizeInfo {
        bytes,
        files: 3,
        unreadable: 0,
        last_write_unix: Some(NOW - DAY),
    }
}

/// Signals of a merged, clean, pushed worktree.
pub(crate) fn merged_signals() -> WorktreeSignals {
    WorktreeSignals {
        merge_state: Some(MergeState::Ancestor),
        merge_state_against: Some("main".to_owned()),
        dirty: Some(Dirty::default()),
        upstream: Some(Upstream::Tracking { ahead: 0 }),
        last_activity_unix: Some(NOW - 3 * DAY),
        size: Some(size(1536)),
        errors: Vec::new(),
    }
}

pub(crate) fn registered(path: &str, branch: &str, signals: WorktreeSignals) -> Candidate {
    Candidate::Registered(RegisteredCandidate {
        path: root().join(path),
        repo: root().join("yaat"),
        branch: Some(branch.to_owned()),
        head: Some("0123456789abcdef".to_owned()),
        prunable: None,
        git_lock: None,
        released: None,
        signals,
    })
}

/// A merged worktree `remove` released because a process held it.
pub(crate) fn released(path: &str) -> Candidate {
    let mut candidate = registered(path, "held", merged_signals());
    if let Candidate::Registered(registered) = &mut candidate {
        registered.released = Some(Released {
            released_at: NOW - DAY,
            reason: Reason::Locked,
            holders: Vec::new(),
        });
    }
    candidate
}

/// A worktree with uncommitted work and commits not on main: removing it asks the loss question.
pub(crate) fn dirty(path: &str) -> Candidate {
    registered(
        path,
        "wip",
        WorktreeSignals {
            merge_state: Some(MergeState::Unmerged { commits: 4 }),
            dirty: Some(Dirty {
                modified: 3,
                untracked: 2,
            }),
            ..merged_signals()
        },
    )
}

pub(crate) fn orphan(path: &str, orphan_kind: OrphanKind) -> Candidate {
    Candidate::Orphan(OrphanCandidate {
        orphan: Orphan {
            path: root().join(path),
            container: root().join("yaat.wt"),
            orphan_kind,
            link_target: (orphan_kind == OrphanKind::Link).then(|| root().join("target")),
            stale_gitdir: false,
            live_gitdir: None,
            has_git_dir: false,
        },
        size: size(if orphan_kind == OrphanKind::Link {
            0
        } else {
            2048
        }),
    })
}

pub(crate) fn report(candidates: Vec<Candidate>) -> Report {
    Report {
        root: root(),
        repos: Vec::new(),
        candidates,
    }
}

/// In list order: a released worktree (pre-ticked), a dirty one (loss question) with a long path, a merged one
/// (branch question only), and an orphan folder.
pub(crate) fn sample_report() -> Report {
    let long = format!(r"yaat.wt\{}\wip", "deeply-nested-folder".repeat(4));
    report(vec![
        orphan(r"yaat.wt\stray", OrphanKind::Folder),
        dirty(&long),
        registered(r"yaat.wt\merged", "merged", merged_signals()),
        released(r"yaat.wt\held"),
    ])
}
