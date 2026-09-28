//! The interactive picker: a checkbox list of the candidates, and the text saying what removing a pick would lose
//! ([`loss_text`], [`loss_sentence`]).

use std::io::IsTerminal;
use std::path::{Path, PathBuf};

use anyhow::{Result, bail};
use dialoguer::MultiSelect;
use dialoguer::console::Term;

use crate::discover::{Orphan, OrphanKind};
use crate::report::{self, Candidate, RegisteredCandidate, Report};
use crate::signals::{Dirty, MergeState, Upstream};

/// Which candidates the picker starts ticked: exactly the released worktrees.
#[must_use]
pub fn default_picks(candidates: &[&Candidate]) -> Vec<bool> {
    candidates
        .iter()
        .map(|candidate| {
            matches!(candidate, Candidate::Registered(registered) if registered.released.is_some())
        })
        .collect()
}

/// Fails unless standard input and standard error are both terminals, so the picker never runs without a person
/// to answer it.
///
/// # Errors
///
/// When either is redirected.
pub fn ensure_interactive() -> Result<()> {
    if std::io::stdin().is_terminal() && std::io::stderr().is_terminal() {
        Ok(())
    } else {
        bail!(
            "picking what to remove needs an interactive terminal; use --list to print the table or --json for \
             the machine-readable report"
        )
    }
}

/// Shows the candidates as a checkbox list with the released worktrees pre-ticked ([`default_picks`]), writes a
/// `Picked <path>` line for every tick, and returns the ticked candidates in list order (none on Esc).
///
/// # Errors
///
/// When the terminal cannot be read or written.
pub fn pick(report: &Report, now_unix: i64) -> Result<Vec<&Candidate>> {
    let candidates = report::ordered(report);
    let items = report::picker_items(report, now_unix);
    let defaults = default_picks(&candidates);
    let chosen = MultiSelect::new()
        .with_prompt("Pick what to remove (space toggles, enter accepts, esc cancels)")
        .items(&items)
        .defaults(&defaults)
        .report(false)
        .interact_opt()?
        .unwrap_or_default();
    let term = Term::stderr();
    let mut picks = Vec::with_capacity(chosen.len());
    for index in chosen {
        let Some(candidate) = candidates.get(index).copied() else {
            continue;
        };
        term.write_line(&format!(
            "Picked {}",
            report::relative_path(candidate.path(), &report.root)
        ))?;
        picks.push(candidate);
    }
    Ok(picks)
}

/// The confirmation for a pick as `(context, question)`: the context names the pick's path and what removing it
/// loses, and the question is short and names no path. `None` when nothing is lost. Asked for uncommitted files,
/// commits not on the default branch, a branch with no commits of its own, a detached HEAD not on it, unpushed
/// commits, a git lock, an orphan another repo still registers, and a link (whose target is kept).
#[must_use]
pub fn loss_sentence(candidate: &Candidate, root: &Path) -> Option<(String, String)> {
    let name = report::relative_path(candidate.path(), root);
    raw_loss(candidate).map(|(_, text, question)| (format!("{name}: {text}"), question.to_owned()))
}

/// Which confirmation a pick needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LossKind {
    /// Removing it loses work, or leaves a registration behind.
    Loss,
    /// It is a link; only the link goes.
    Link,
}

/// What removing a pick loses, without its path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Loss {
    /// Which confirmation it is.
    pub kind: LossKind,
    /// The loss as a sentence with a capital first letter and no path of the pick
    /// (`Remove the link only; X:\dev\yaat is not touched.`).
    pub text: String,
    /// The short question, naming no path.
    pub question: &'static str,
}

/// The loss [`loss_sentence`] describes, without the pick's path; `None` when nothing is lost.
#[must_use]
pub fn loss_text(candidate: &Candidate) -> Option<Loss> {
    raw_loss(candidate).map(|(kind, text, question)| Loss {
        kind,
        text: capitalise(&text),
        question,
    })
}

fn raw_loss(candidate: &Candidate) -> Option<(LossKind, String, &'static str)> {
    match candidate {
        Candidate::Registered(registered) => {
            registered_loss(registered).map(|loss| (LossKind::Loss, loss, "Remove anyway?"))
        }
        Candidate::Orphan(orphan) => orphan_notice(&orphan.orphan).map(|(notice, question)| {
            let kind = if orphan.orphan.orphan_kind == OrphanKind::Link {
                LossKind::Link
            } else {
                LossKind::Loss
            };
            (kind, notice, question)
        }),
    }
}

fn capitalise(text: &str) -> String {
    let mut chars = text.chars();
    chars.next().map_or_else(String::new, |first| {
        first.to_uppercase().chain(chars).collect()
    })
}

fn registered_loss(registered: &RegisteredCandidate) -> Option<String> {
    let signals = &registered.signals;
    let against = signals
        .merge_state_against
        .as_deref()
        .unwrap_or("the default branch");
    let mut lost = Vec::new();
    if let Some(dirty) = signals.dirty.and_then(dirty_phrase) {
        lost.push(dirty);
    }
    match signals.merge_state {
        Some(MergeState::Unmerged { commits }) => {
            lost.push(format!("{} not on {against}", counted(commits, "commit")));
        }
        Some(MergeState::NoCommits) => {
            lost.push(
                "a branch with no commits of its own (it may be new work in progress)".to_owned(),
            );
        }
        Some(MergeState::Detached { contained: false }) => {
            let head = registered.head.as_deref().unwrap_or("HEAD");
            let short = head.get(..7).unwrap_or(head);
            lost.push(format!(
                "detached HEAD {short} and its commits not on {against}"
            ));
        }
        _ => {}
    }
    if let Some(Upstream::Tracking { ahead }) = signals.upstream
        && ahead > 0
    {
        lost.push(format!("{} not pushed", counted(ahead, "commit")));
    }

    let mut sentences = Vec::new();
    if !lost.is_empty() {
        sentences.push(format!("{} will be lost.", join_and(&lost)));
    }
    if let Some(reason) = &registered.git_lock {
        let reason = reason.trim().trim_end_matches('.');
        sentences.push(if reason.is_empty() {
            "It is git-locked (no reason given).".to_owned()
        } else {
            format!("It is git-locked: {reason}.")
        });
    }
    (!sentences.is_empty()).then(|| sentences.join(" "))
}

fn orphan_notice(orphan: &Orphan) -> Option<(String, &'static str)> {
    if orphan.orphan_kind == OrphanKind::Link {
        let target = orphan.link_target.as_ref().map_or_else(
            || "its target".to_owned(),
            |target| target.display().to_string(),
        );
        return Some((
            format!("remove the link only; {target} is not touched."),
            "Remove the link?",
        ));
    }
    let gitdir = orphan.live_gitdir.as_ref()?;
    Some((
        format!(
            "still registered in {}; removing leaves a prunable registration there.",
            repo_of_gitdir(gitdir).display()
        ),
        "Remove anyway?",
    ))
}

/// The repo a worktree's git dir (`<repo>/.git/worktrees/<id>`) belongs to; the common dir itself for a bare
/// repo, and the git dir unchanged when it has no such shape.
fn repo_of_gitdir(gitdir: &Path) -> PathBuf {
    let common = gitdir
        .parent()
        .filter(|parent| parent.file_name().is_some_and(|name| name == "worktrees"))
        .and_then(Path::parent);
    match common {
        Some(common) if common.file_name().is_some_and(|name| name == ".git") => common
            .parent()
            .map_or_else(|| common.to_path_buf(), Path::to_path_buf),
        Some(common) => common.to_path_buf(),
        None => gitdir.to_path_buf(),
    }
}

fn dirty_phrase(dirty: Dirty) -> Option<String> {
    let files = |count: u32| if count == 1 { "file" } else { "files" };
    match (dirty.modified, dirty.untracked) {
        (0, 0) => None,
        (modified, 0) => Some(format!("{modified} modified {}", files(modified))),
        (0, untracked) => Some(format!("{untracked} untracked {}", files(untracked))),
        (modified, untracked) => Some(format!(
            "{modified} modified, {untracked} untracked {}",
            files(untracked)
        )),
    }
}

fn counted(count: u32, noun: &str) -> String {
    if count == 1 {
        format!("1 {noun}")
    } else {
        format!("{count} {noun}s")
    }
}

/// `a`, `a and b`, `a, b and c`.
fn join_and(parts: &[String]) -> String {
    match parts {
        [] => String::new(),
        [only] => only.clone(),
        [init @ .., last] => format!("{} and {last}", init.join(", ")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::report::OrphanCandidate;
    use crate::signals::{SizeInfo, WorktreeSignals};

    fn root() -> PathBuf {
        PathBuf::from(r"D:\")
    }

    fn candidate(signals: WorktreeSignals) -> RegisteredCandidate {
        RegisteredCandidate {
            path: root().join(r"yaat.wt\eram-co\yaat"),
            repo: root().join("yaat"),
            branch: Some("eram-co".to_owned()),
            head: Some("0123456789abcdef".to_owned()),
            prunable: None,
            git_lock: None,
            released: None,
            signals,
        }
    }

    #[test]
    fn default_picks_ticks_only_released_worktrees() {
        let mut released = candidate(signals(MergeState::Ancestor));
        released.released = Some(crate::agent::Released {
            released_at: 0,
            reason: crate::agent::Reason::Locked,
            holders: Vec::new(),
        });
        let released = Candidate::Registered(released);
        let plain = Candidate::Registered(candidate(signals(MergeState::Ancestor)));
        let orphan = Candidate::Orphan(OrphanCandidate {
            orphan: Orphan {
                path: root().join(r"yaat.wt\stray"),
                container: root().join("yaat.wt"),
                orphan_kind: OrphanKind::Folder,
                link_target: None,
                stale_gitdir: false,
                live_gitdir: None,
                has_git_dir: false,
            },
            size: SizeInfo::default(),
        });
        assert_eq!(
            default_picks(&[&released, &plain, &orphan]),
            [true, false, false]
        );
    }

    fn signals(merge_state: MergeState) -> WorktreeSignals {
        WorktreeSignals {
            merge_state: Some(merge_state),
            merge_state_against: Some("main".to_owned()),
            dirty: Some(Dirty::default()),
            upstream: Some(Upstream::Tracking { ahead: 0 }),
            last_activity_unix: None,
            size: Some(SizeInfo::default()),
            errors: Vec::new(),
        }
    }

    fn sentence(registered: RegisteredCandidate) -> Option<(String, String)> {
        loss_sentence(&Candidate::Registered(registered), &root())
    }

    fn remove_anyway(context: &str) -> (String, String) {
        (context.to_owned(), "Remove anyway?".to_owned())
    }

    #[test]
    fn loss_sentence_dirty() {
        let mut signals = signals(MergeState::Ancestor);
        signals.dirty = Some(Dirty {
            modified: 1,
            untracked: 0,
        });
        assert_eq!(
            sentence(candidate(signals)),
            Some(remove_anyway(
                r"yaat.wt\eram-co\yaat: 1 modified file will be lost."
            ))
        );
    }

    #[test]
    fn loss_sentence_unmerged() {
        let mut signals = signals(MergeState::Unmerged { commits: 4 });
        signals.dirty = Some(Dirty {
            modified: 3,
            untracked: 2,
        });
        assert_eq!(
            sentence(candidate(signals)),
            Some(remove_anyway(
                r"yaat.wt\eram-co\yaat: 3 modified, 2 untracked files and 4 commits not on main will be lost."
            ))
        );
    }

    #[test]
    fn loss_sentence_detached_uncontained() {
        let mut registered = candidate(signals(MergeState::Detached { contained: false }));
        registered.branch = None;
        registered.signals.upstream = None;
        assert_eq!(
            sentence(registered),
            Some(remove_anyway(
                r"yaat.wt\eram-co\yaat: detached HEAD 0123456 and its commits not on main will be lost."
            ))
        );
        let mut contained = candidate(signals(MergeState::Detached { contained: true }));
        contained.branch = None;
        assert_eq!(sentence(contained), None);
    }

    #[test]
    fn loss_sentence_no_commits() {
        assert_eq!(
            sentence(candidate(signals(MergeState::NoCommits))),
            Some(remove_anyway(
                r"yaat.wt\eram-co\yaat: a branch with no commits of its own (it may be new work in progress) will be lost."
            ))
        );
    }

    #[test]
    fn loss_sentence_unpushed() {
        let mut signals = signals(MergeState::Ancestor);
        signals.upstream = Some(Upstream::Tracking { ahead: 1 });
        assert_eq!(
            sentence(candidate(signals)),
            Some(remove_anyway(
                r"yaat.wt\eram-co\yaat: 1 commit not pushed will be lost."
            ))
        );
    }

    fn orphan(orphan_kind: OrphanKind, live_gitdir: Option<PathBuf>) -> Candidate {
        Candidate::Orphan(OrphanCandidate {
            orphan: Orphan {
                path: root().join(LONG_PATH),
                container: root().join(r"a-rather-long-repository-name.wt"),
                orphan_kind,
                link_target: (orphan_kind == OrphanKind::Link)
                    .then(|| root().join(r"elsewhere\a-rather-long-target-folder")),
                stale_gitdir: false,
                live_gitdir,
                has_git_dir: false,
            },
            size: SizeInfo::default(),
        })
    }

    const LONG_PATH: &str =
        r"a-rather-long-repository-name.wt\a-feature-branch-with-a-long-descriptive-name\nested";

    #[test]
    fn loss_question_is_short_and_pathless() -> anyhow::Result<()> {
        let long = |mut registered: RegisteredCandidate| {
            registered.path = root().join(LONG_PATH);
            Candidate::Registered(registered)
        };
        let mut dirty = signals(MergeState::Ancestor);
        dirty.dirty = Some(Dirty {
            modified: 3,
            untracked: 2,
        });
        let mut unpushed = signals(MergeState::Ancestor);
        unpushed.upstream = Some(Upstream::Tracking { ahead: 5 });
        let mut locked = candidate(signals(MergeState::Unmerged { commits: 9 }));
        locked.git_lock = Some(r"on a USB drive mounted at E:\backups\worktrees".to_owned());
        let mut detached = candidate(signals(MergeState::Detached { contained: false }));
        detached.branch = None;
        let registered_elsewhere = root().join(r"other\repo\.git\worktrees\nested");
        let candidates = [
            long(candidate(dirty)),
            long(candidate(signals(MergeState::Unmerged { commits: 4 }))),
            long(candidate(signals(MergeState::NoCommits))),
            long(candidate(unpushed)),
            long(locked),
            long(detached),
            orphan(OrphanKind::Link, None),
            orphan(OrphanKind::Folder, Some(registered_elsewhere)),
        ];
        for candidate in &candidates {
            let loss = loss_text(candidate)
                .ok_or_else(|| anyhow::anyhow!("no confirmation for {candidate:?}"))?;
            let question = loss.question;
            anyhow::ensure!(
                !question.contains('\\') && !question.contains('/'),
                "question names a path: {question}"
            );
            anyhow::ensure!(
                question.chars().count() <= 40,
                "question is {} chars: {question}",
                question.chars().count()
            );
            anyhow::ensure!(
                !loss.text.contains(LONG_PATH),
                "text names the pick: {}",
                loss.text
            );
        }
        Ok(())
    }

    #[test]
    fn loss_text_link_is_capitalised_and_pathless() -> anyhow::Result<()> {
        let link = orphan(OrphanKind::Link, None);
        let loss = loss_text(&link).ok_or_else(|| anyhow::anyhow!("no loss for a link"))?;
        let target = root().join(r"elsewhere\a-rather-long-target-folder");
        anyhow::ensure!(
            loss.text == format!("Remove the link only; {} is not touched.", target.display()),
            "text: {}",
            loss.text
        );
        anyhow::ensure!(loss.kind == LossKind::Link, "kind: {:?}", loss.kind);
        anyhow::ensure!(
            loss.question == "Remove the link?",
            "question: {}",
            loss.question
        );
        anyhow::ensure!(!loss.text.contains(LONG_PATH), "text: {}", loss.text);

        let no_commits = Candidate::Registered(candidate(signals(MergeState::NoCommits)));
        let loss = loss_text(&no_commits).ok_or_else(|| anyhow::anyhow!("no loss"))?;
        anyhow::ensure!(
            loss.text
                == "A branch with no commits of its own (it may be new work in progress) will be lost.",
            "text: {}",
            loss.text
        );
        anyhow::ensure!(loss.kind == LossKind::Loss, "kind: {:?}", loss.kind);
        Ok(())
    }

    #[test]
    fn loss_text_none_when_nothing_lost() {
        let merged = Candidate::Registered(candidate(signals(MergeState::Ancestor)));
        assert_eq!(loss_text(&merged), None);
        assert_eq!(loss_text(&orphan(OrphanKind::Folder, None)), None);
    }

    #[test]
    fn loss_sentence_git_locked() {
        let mut registered = candidate(signals(MergeState::Ancestor));
        registered.git_lock = Some("on a USB drive".to_owned());
        assert_eq!(
            sentence(registered),
            Some(remove_anyway(
                r"yaat.wt\eram-co\yaat: It is git-locked: on a USB drive."
            ))
        );
    }
}
