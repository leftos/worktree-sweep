//! Review: the pure sequence of questions asked about the ticked picks before anything is removed. Per pick, in
//! order: what it would lose (or that only a link goes), whether to delete it permanently when it cannot go to the
//! Recycle Bin, and whether to delete its branch. A declined answer skips that pick's later questions. The answers
//! become one [`Decision`] per pick for [`remove::remove_picks`](crate::remove::remove_picks).

use std::path::{Path, PathBuf};

use crate::pick::{self, Loss, LossKind};
use crate::recycle::BinCapacity;
use crate::remove::{self, Action, BranchChoice, BranchOffer, Decision, Method, Plan, PlanNeed};
use crate::report::{self, Candidate, human_bytes};

/// Which question a [`Step`] asks.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StepKind {
    /// Removing the pick loses work, or leaves a registration behind.
    Loss,
    /// The pick is a link; only the link goes.
    Link,
    /// The pick cannot go to the Recycle Bin.
    Permanent,
    /// The pick's branch can be deleted once it is gone.
    Branch,
}

/// One question.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Step {
    /// The pick it is about, as an index into the candidates given to [`Review::new`].
    pub pick: usize,
    /// Which question it is.
    pub kind: StepKind,
    /// What the answer is about, shown before the question.
    pub body: String,
    /// The short question, naming no path.
    pub question: &'static str,
    /// The answer Enter gives.
    pub default: bool,
}

/// What the answered review would do.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Totals {
    /// Folders going to the Recycle Bin.
    pub recycle: usize,
    /// Their size; an unknown size counts 0.
    pub recycle_bytes: u64,
    /// Folders deleted for good.
    pub permanent: usize,
    /// Their size; an unknown size counts 0.
    pub permanent_bytes: u64,
    /// Links deleted.
    pub links: usize,
    /// Registrations pruned.
    pub prunes: usize,
    /// Branches deleted.
    pub branches: usize,
    /// Picks left in place.
    pub skipped: usize,
}

#[derive(Debug)]
struct Entry<'a> {
    candidate: &'a Candidate,
    loss: Option<Loss>,
    need: PlanNeed,
    offer: Option<BranchOffer>,
    loss_answer: Option<bool>,
    permanent_answer: Option<bool>,
    branch_answer: Option<bool>,
}

impl Entry<'_> {
    fn plan(&self) -> Plan {
        if self.loss.is_some() && self.loss_answer != Some(true) {
            return Plan::Skip("not confirmed".to_owned());
        }
        match &self.need {
            PlanNeed::Run(action) => Plan::Run(*action),
            PlanNeed::AskPermanent(_) if self.permanent_answer == Some(true) => {
                Plan::Run(Action::Delete(Method::Permanent))
            }
            PlanNeed::AskPermanent(reason) => {
                Plan::Skip(format!("permanent delete declined; {reason}"))
            }
        }
    }

    fn branch(&self, plan: &Plan) -> BranchChoice {
        match (&self.offer, plan) {
            (Some(offer), Plan::Run(_)) if self.branch_answer == Some(false) => {
                BranchChoice::Keep(offer.clone())
            }
            (Some(offer), Plan::Run(_)) => BranchChoice::Delete(offer.clone()),
            _ => BranchChoice::NotOffered,
        }
    }
}

/// The questions about a set of picks, answered one at a time.
#[derive(Debug)]
pub struct Review<'a> {
    root: PathBuf,
    entries: Vec<Entry<'a>>,
    current: Option<Step>,
}

impl<'a> Review<'a> {
    /// Prepares the questions for `candidates` (paths shown relative to `root`). `capacity` reads the Recycle Bin
    /// settings of a pick's volume; it is called once for each pick that is a folder, and for no other.
    #[must_use]
    pub fn new(
        candidates: &[&'a Candidate],
        root: &Path,
        capacity: &mut dyn FnMut(&Path) -> Option<BinCapacity>,
    ) -> Self {
        let entries = candidates
            .iter()
            .map(|&candidate| {
                let bin = if remove::needs_capacity(candidate) {
                    capacity(candidate.path())
                } else {
                    None
                };
                let offer = match candidate {
                    Candidate::Registered(registered) => remove::branch_offer(registered),
                    Candidate::Orphan(_) => None,
                };
                Entry {
                    candidate,
                    loss: pick::loss_text(candidate),
                    need: remove::plan_action(candidate, bin),
                    offer,
                    loss_answer: None,
                    permanent_answer: None,
                    branch_answer: None,
                }
            })
            .collect();
        let mut review = Self {
            root: root.to_path_buf(),
            entries,
            current: None,
        };
        review.current = review.next_step();
        review
    }

    /// The question to answer now; `None` once every question is answered.
    #[must_use]
    pub fn step(&self) -> Option<&Step> {
        self.current.as_ref()
    }

    /// Answers the current question and moves to the next one, skipping those the answer made moot. Does nothing
    /// when every question is answered.
    pub fn answer(&mut self, yes: bool) {
        let Some(step) = self.current.take() else {
            return;
        };
        let entry = &mut self.entries[step.pick];
        match step.kind {
            StepKind::Loss | StepKind::Link => entry.loss_answer = Some(yes),
            StepKind::Permanent => entry.permanent_answer = Some(yes),
            StepKind::Branch => entry.branch_answer = Some(yes),
        }
        self.current = self.next_step();
    }

    /// What the answers add up to; `None` until every question is answered.
    #[must_use]
    pub fn totals(&self) -> Option<Totals> {
        if self.current.is_some() {
            return None;
        }
        let mut totals = Totals::default();
        for entry in &self.entries {
            let plan = entry.plan();
            if matches!(entry.branch(&plan), BranchChoice::Delete(_)) {
                totals.branches += 1;
            }
            let bytes = entry.candidate.size_bytes().unwrap_or_default();
            match plan {
                Plan::Run(Action::Delete(Method::Recycle)) => {
                    totals.recycle += 1;
                    totals.recycle_bytes += bytes;
                }
                Plan::Run(Action::Delete(Method::Permanent)) => {
                    totals.permanent += 1;
                    totals.permanent_bytes += bytes;
                }
                Plan::Run(Action::RemoveLink) => totals.links += 1,
                Plan::Run(Action::PruneRegistration) => totals.prunes += 1,
                Plan::Skip(_) => totals.skipped += 1,
            }
        }
        Some(totals)
    }

    /// One decision per pick, in candidate order; `None` until every question is answered.
    #[must_use]
    pub fn decisions(self) -> Option<Vec<Decision<'a>>> {
        if self.current.is_some() {
            return None;
        }
        Some(
            self.entries
                .iter()
                .map(|entry| {
                    let plan = entry.plan();
                    let branch = entry.branch(&plan);
                    Decision {
                        candidate: entry.candidate,
                        plan,
                        branch,
                    }
                })
                .collect(),
        )
    }

    fn next_step(&self) -> Option<Step> {
        self.entries
            .iter()
            .enumerate()
            .find_map(|(index, entry)| self.step_for(index, entry))
    }

    /// The first unanswered question about one pick, or `None` when it has none left.
    fn step_for(&self, pick: usize, entry: &Entry<'_>) -> Option<Step> {
        if let Some(loss) = &entry.loss {
            match entry.loss_answer {
                None => {
                    let kind = match loss.kind {
                        LossKind::Loss => StepKind::Loss,
                        LossKind::Link => StepKind::Link,
                    };
                    return Some(step(pick, kind, loss.text.clone(), loss.question, false));
                }
                Some(false) => return None,
                Some(true) => {}
            }
        }
        if let PlanNeed::AskPermanent(reason) = &entry.need {
            match entry.permanent_answer {
                None => {
                    let name = report::relative_path(entry.candidate.path(), &self.root);
                    let body = format!(
                        "{name} cannot go to the Recycle Bin: {reason}; deleting it permanently cannot be undone."
                    );
                    return Some(step(
                        pick,
                        StepKind::Permanent,
                        body,
                        "Delete it permanently?",
                        false,
                    ));
                }
                Some(false) => return None,
                Some(true) => {}
            }
        }
        match (&entry.offer, entry.branch_answer) {
            (Some(offer), None) => Some(step(
                pick,
                StepKind::Branch,
                offer.context.clone(),
                offer.question,
                true,
            )),
            _ => None,
        }
    }
}

fn step(pick: usize, kind: StepKind, body: String, question: &'static str, default: bool) -> Step {
    Step {
        pick,
        kind,
        body,
        question,
        default,
    }
}

/// The final confirmation's sentence, naming only the non-zero parts: `Remove N items: R to the Recycle Bin (X), P
/// permanently (Y), L links, U registrations pruned; B branches deleted. S skipped.`
#[must_use]
pub fn final_sentence(totals: &Totals) -> String {
    let count = totals.recycle + totals.permanent + totals.links + totals.prunes;
    let mut parts = Vec::new();
    if totals.recycle > 0 {
        parts.push(format!(
            "{} to the Recycle Bin ({})",
            totals.recycle,
            human_bytes(totals.recycle_bytes)
        ));
    }
    if totals.permanent > 0 {
        parts.push(format!(
            "{} permanently ({})",
            totals.permanent,
            human_bytes(totals.permanent_bytes)
        ));
    }
    if totals.links > 0 {
        parts.push(counted(totals.links, "link", "links"));
    }
    if totals.prunes > 0 {
        parts.push(format!(
            "{} pruned",
            counted(totals.prunes, "registration", "registrations")
        ));
    }
    let mut sentence = format!("Remove {}", counted(count, "item", "items"));
    if !parts.is_empty() {
        sentence.push_str(": ");
        sentence.push_str(&parts.join(", "));
    }
    if totals.branches > 0 {
        sentence.push_str("; ");
        sentence.push_str(&counted(totals.branches, "branch", "branches"));
        sentence.push_str(" deleted");
    }
    sentence.push('.');
    if totals.skipped > 0 {
        sentence.push(' ');
        sentence.push_str(&totals.skipped.to_string());
        sentence.push_str(" skipped.");
    }
    sentence
}

fn counted(count: usize, one: &str, many: &str) -> String {
    format!("{count} {}", if count == 1 { one } else { many })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::discover::{Orphan, OrphanKind};
    use crate::report::{OrphanCandidate, RegisteredCandidate};
    use crate::signals::{Dirty, MergeState, SizeInfo, Upstream, WorktreeSignals};

    fn root() -> PathBuf {
        PathBuf::from(r"D:\")
    }

    const MB: u64 = 1024 * 1024;

    fn registered(merge_state: MergeState, bytes: u64) -> RegisteredCandidate {
        RegisteredCandidate {
            path: root().join(r"repo.wt\feat"),
            repo: root().join("repo"),
            branch: Some("feat".to_owned()),
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
                size: Some(SizeInfo {
                    bytes,
                    ..SizeInfo::default()
                }),
                errors: Vec::new(),
            },
        }
    }

    fn orphan(orphan_kind: OrphanKind) -> Candidate {
        Candidate::Orphan(OrphanCandidate {
            orphan: Orphan {
                path: root().join(r"repo.wt\stray"),
                container: root().join("repo.wt"),
                orphan_kind,
                link_target: (orphan_kind == OrphanKind::Link).then(|| root().join("target")),
                stale_gitdir: false,
                live_gitdir: None,
                has_git_dir: false,
            },
            size: SizeInfo::default(),
        })
    }

    const ROOMY: BinCapacity = BinCapacity {
        max_capacity_mb: 1024,
        nuke_on_delete: false,
    };

    const SMALL: BinCapacity = BinCapacity {
        max_capacity_mb: 1,
        nuke_on_delete: false,
    };

    fn only_decision(review: Review<'_>) -> anyhow::Result<Decision<'_>> {
        let mut decisions = review
            .decisions()
            .ok_or_else(|| anyhow::anyhow!("review not finished"))?;
        anyhow::ensure!(decisions.len() == 1, "decisions: {decisions:?}");
        decisions
            .pop()
            .ok_or_else(|| anyhow::anyhow!("no decision"))
    }

    fn current(review: &Review<'_>) -> anyhow::Result<Step> {
        review
            .step()
            .cloned()
            .ok_or_else(|| anyhow::anyhow!("no question left"))
    }

    #[test]
    fn review_dirty_unmerged_asks_loss_first_default_no() -> anyhow::Result<()> {
        let mut dirty = registered(MergeState::Unmerged { commits: 4 }, 10);
        dirty.signals.dirty = Some(Dirty {
            modified: 3,
            untracked: 2,
        });
        let candidate = Candidate::Registered(dirty);
        let mut review = Review::new(&[&candidate], &root(), &mut |_| Some(ROOMY));

        let step = current(&review)?;
        anyhow::ensure!(
            step.kind == StepKind::Loss && !step.default && step.pick == 0,
            "{step:?}"
        );
        anyhow::ensure!(
            step.body == "3 modified, 2 untracked files and 4 commits not on main will be lost."
                && step.question == "Remove anyway?",
            "{step:?}"
        );
        review.answer(true);
        anyhow::ensure!(review.step().is_none(), "{:?}", review.step());
        let decision = only_decision(review)?;
        anyhow::ensure!(
            decision.plan == Plan::Run(Action::Delete(Method::Recycle))
                && decision.branch == BranchChoice::NotOffered,
            "{decision:?}"
        );
        Ok(())
    }

    #[test]
    fn review_declined_loss_skips_permanent_and_branch() -> anyhow::Result<()> {
        let mut unpushed = registered(MergeState::Ancestor, 10);
        unpushed.signals.upstream = Some(Upstream::Tracking { ahead: 1 });
        let candidate = Candidate::Registered(unpushed);
        let mut review = Review::new(&[&candidate], &root(), &mut |_| None);

        anyhow::ensure!(
            current(&review)?.kind == StepKind::Loss,
            "{:?}",
            review.step()
        );
        review.answer(false);
        anyhow::ensure!(review.step().is_none(), "{:?}", review.step());
        let decision = only_decision(review)?;
        anyhow::ensure!(
            decision.plan == Plan::Skip("not confirmed".to_owned())
                && decision.branch == BranchChoice::NotOffered,
            "{decision:?}"
        );
        Ok(())
    }

    #[test]
    fn review_over_capacity_asks_permanent_default_no() -> anyhow::Result<()> {
        let candidate = Candidate::Registered(registered(MergeState::Ancestor, 2 * MB));
        let mut review = Review::new(&[&candidate], &root(), &mut |_| Some(SMALL));

        let step = current(&review)?;
        anyhow::ensure!(
            step.kind == StepKind::Permanent
                && !step.default
                && step.question == "Delete it permanently?",
            "{step:?}"
        );
        anyhow::ensure!(
            step.body
                .starts_with(r"repo.wt\feat cannot go to the Recycle Bin: ")
                && step
                    .body
                    .ends_with("; deleting it permanently cannot be undone."),
            "{step:?}"
        );
        review.answer(false);
        anyhow::ensure!(review.step().is_none(), "{:?}", review.step());
        let decision = only_decision(review)?;
        anyhow::ensure!(
            matches!(&decision.plan, Plan::Skip(reason) if reason.starts_with("permanent delete declined; "))
                && decision.branch == BranchChoice::NotOffered,
            "{decision:?}"
        );
        Ok(())
    }

    #[test]
    fn review_squashed_branch_offers_force_delete_default_yes() -> anyhow::Result<()> {
        let candidate = Candidate::Registered(registered(MergeState::ContentContained, 10));
        let mut review = Review::new(&[&candidate], &root(), &mut |_| Some(ROOMY));

        let step = current(&review)?;
        anyhow::ensure!(
            step.kind == StepKind::Branch
                && step.default
                && step.question == "Delete the branch?"
                && step.body.starts_with("Branch feat: ")
                && step.body.contains("git branch -D"),
            "{step:?}"
        );
        review.answer(true);
        let decision = only_decision(review)?;
        anyhow::ensure!(
            matches!(&decision.branch, BranchChoice::Delete(offer) if offer.force && offer.branch == "feat"),
            "{decision:?}"
        );
        anyhow::ensure!(
            decision.plan == Plan::Run(Action::Delete(Method::Recycle)),
            "{decision:?}"
        );
        Ok(())
    }

    #[test]
    fn review_declined_branch_keeps_it() -> anyhow::Result<()> {
        let candidate = Candidate::Registered(registered(MergeState::Ancestor, 10));
        let mut review = Review::new(&[&candidate], &root(), &mut |_| Some(ROOMY));

        anyhow::ensure!(
            current(&review)?.kind == StepKind::Branch,
            "{:?}",
            review.step()
        );
        review.answer(false);
        let decision = only_decision(review)?;
        anyhow::ensure!(
            matches!(&decision.branch, BranchChoice::Keep(offer) if offer.branch == "feat" && !offer.force),
            "{decision:?}"
        );
        Ok(())
    }

    #[test]
    fn review_link_asks_link_question() -> anyhow::Result<()> {
        let link = orphan(OrphanKind::Link);
        let mut review = Review::new(&[&link], &root(), &mut |_| Some(ROOMY));

        let step = current(&review)?;
        anyhow::ensure!(
            step.kind == StepKind::Link
                && !step.default
                && step.question == "Remove the link?"
                && step.body.starts_with("Remove the link only; "),
            "{step:?}"
        );
        review.answer(true);
        let decision = only_decision(review)?;
        anyhow::ensure!(
            decision.plan == Plan::Run(Action::RemoveLink),
            "{decision:?}"
        );
        Ok(())
    }

    #[test]
    fn review_reads_capacity_only_for_folders() {
        let link = orphan(OrphanKind::Link);
        let folder = orphan(OrphanKind::Folder);
        let mut prunable = registered(MergeState::Ancestor, 10);
        prunable.prunable = Some("gitdir file points to non-existent location".to_owned());
        let prunable = Candidate::Registered(prunable);
        let live = Candidate::Registered(registered(MergeState::Ancestor, 10));
        let mut calls = Vec::new();
        let _review = Review::new(&[&link, &folder, &prunable, &live], &root(), &mut |path| {
            calls.push(path.to_path_buf());
            Some(ROOMY)
        });
        assert_eq!(
            calls,
            [folder.path().to_path_buf(), live.path().to_path_buf()]
        );
    }

    #[test]
    fn review_decisions_none_until_finished() -> anyhow::Result<()> {
        let candidate = Candidate::Registered(registered(MergeState::Ancestor, 10));
        let unfinished = Review::new(&[&candidate], &root(), &mut |_| Some(ROOMY));
        anyhow::ensure!(unfinished.step().is_some(), "no question asked");
        anyhow::ensure!(unfinished.totals().is_none(), "totals before the end");
        anyhow::ensure!(unfinished.decisions().is_none(), "decisions before the end");

        let mut finished = Review::new(&[&candidate], &root(), &mut |_| Some(ROOMY));
        finished.answer(true);
        let totals = finished
            .totals()
            .ok_or_else(|| anyhow::anyhow!("no totals once finished"))?;
        anyhow::ensure!(
            totals
                == Totals {
                    recycle: 1,
                    recycle_bytes: 10,
                    branches: 1,
                    ..Totals::default()
                },
            "{totals:?}"
        );
        anyhow::ensure!(finished.decisions().is_some(), "no decisions once finished");
        Ok(())
    }

    #[test]
    fn final_sentence_omits_zero_parts() {
        let totals = Totals {
            recycle: 2,
            recycle_bytes: 3 * MB,
            links: 3,
            ..Totals::default()
        };
        assert_eq!(
            final_sentence(&totals),
            format!(
                "Remove 5 items: 2 to the Recycle Bin ({}), 3 links.",
                human_bytes(3 * MB)
            )
        );
        let all = Totals {
            recycle: 2,
            recycle_bytes: 3 * MB,
            permanent: 2,
            permanent_bytes: 5 * MB,
            links: 2,
            prunes: 2,
            branches: 3,
            skipped: 4,
        };
        assert_eq!(
            final_sentence(&all),
            format!(
                "Remove 8 items: 2 to the Recycle Bin ({}), 2 permanently ({}), 2 links, 2 registrations pruned; 3 \
                 branches deleted. 4 skipped.",
                human_bytes(3 * MB),
                human_bytes(5 * MB)
            )
        );
    }

    #[test]
    fn final_sentence_singular_forms() {
        let totals = Totals {
            prunes: 1,
            branches: 1,
            skipped: 1,
            ..Totals::default()
        };
        assert_eq!(
            final_sentence(&totals),
            "Remove 1 item: 1 registration pruned; 1 branch deleted. 1 skipped."
        );
        let link = Totals {
            links: 1,
            ..Totals::default()
        };
        assert_eq!(final_sentence(&link), "Remove 1 item: 1 link.");
    }
}
