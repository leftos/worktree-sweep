use anyhow::{Result, ensure};
use worktree_sweep::signals::MergeState;

use crate::fixture::{Fixture, add_worktree, commit_file, git, registered};

#[test]
fn merged_branch_is_ancestor() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    commit_file(&wt, "b.txt", "b\n", "b")?;
    git(&repo, &["merge", "-q", "--ff-only", "feat"])?;

    let report = fx.scan()?;
    let state = registered(&report, &wt)?.signals.merge_state;
    ensure!(state == Some(MergeState::Ancestor), "got {state:?}");
    Ok(())
}

#[test]
fn cherry_picked_branch_is_patches_applied() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    let first = commit_file(&wt, "b.txt", "b\n", "b")?;
    let second = commit_file(&wt, "c.txt", "c\n", "c")?;
    commit_file(&repo, "x.txt", "x\n", "unrelated")?;
    git(&repo, &["cherry-pick", &first, &second])?;

    let report = fx.scan()?;
    let state = registered(&report, &wt)?.signals.merge_state;
    ensure!(state == Some(MergeState::PatchesApplied), "got {state:?}");
    Ok(())
}

#[test]
fn squash_merged_branch_is_content_contained() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    commit_file(&wt, "b.txt", "b\n", "b")?;
    commit_file(&wt, "c.txt", "c\n", "c")?;
    git(&repo, &["merge", "-q", "--squash", "feat"])?;
    git(&repo, &["commit", "-q", "-m", "squash feat"])?;
    commit_file(&repo, "x.txt", "x\n", "unrelated")?;

    let report = fx.scan()?;
    let state = registered(&report, &wt)?.signals.merge_state;
    ensure!(state == Some(MergeState::ContentContained), "got {state:?}");
    Ok(())
}

#[test]
fn divergent_branch_is_unmerged_with_count() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    commit_file(&wt, "b.txt", "b\n", "b")?;
    commit_file(&wt, "c.txt", "c\n", "c")?;
    commit_file(&repo, "x.txt", "x\n", "unrelated")?;

    let report = fx.scan()?;
    let state = registered(&report, &wt)?.signals.merge_state;
    ensure!(
        state == Some(MergeState::Unmerged { commits: 2 }),
        "got {state:?}"
    );
    Ok(())
}

#[test]
fn conflicting_branch_is_not_content_contained() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    commit_file(&wt, "README.md", "from feat\n", "feat edit")?;
    commit_file(&repo, "README.md", "from main\n", "main edit")?;

    let report = fx.scan()?;
    let state = registered(&report, &wt)?.signals.merge_state;
    ensure!(
        state == Some(MergeState::Unmerged { commits: 1 }),
        "got {state:?}"
    );
    Ok(())
}

#[test]
fn detached_head_is_detached() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let at_main = fx.path("repo.wt/at-main");
    let moved_on = fx.path("repo.wt/moved-on");
    git(
        &repo,
        &[
            "worktree",
            "add",
            "-q",
            "--detach",
            &at_main.to_string_lossy(),
            "HEAD",
        ],
    )?;
    git(
        &repo,
        &[
            "worktree",
            "add",
            "-q",
            "--detach",
            &moved_on.to_string_lossy(),
            "HEAD",
        ],
    )?;
    commit_file(&moved_on, "b.txt", "b\n", "b")?;

    let report = fx.scan()?;
    let contained = registered(&report, &at_main)?;
    ensure!(
        contained.branch.is_none(),
        "got branch {:?}",
        contained.branch
    );
    let state = contained.signals.merge_state;
    ensure!(
        state == Some(MergeState::Detached { contained: true }),
        "at main: got {state:?}"
    );
    let state = registered(&report, &moved_on)?.signals.merge_state;
    ensure!(
        state == Some(MergeState::Detached { contained: false }),
        "moved on: got {state:?}"
    );
    Ok(())
}

#[test]
fn origin_default_is_used_when_local_is_behind() -> Result<()> {
    let fx = Fixture::new()?;
    let origin = fx.path("origin.git");
    git(
        fx.root(),
        &[
            "init",
            "-q",
            "--bare",
            "-b",
            "main",
            &origin.to_string_lossy(),
        ],
    )?;
    let repo = fx.repo("repo")?;
    git(
        &repo,
        &["remote", "add", "origin", &origin.to_string_lossy()],
    )?;
    git(&repo, &["push", "-q", "origin", "main"])?;
    git(&repo, &["remote", "set-head", "origin", "main"])?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    commit_file(&wt, "b.txt", "b\n", "b")?;
    git(&repo, &["push", "-q", "origin", "feat:main"])?;
    git(&repo, &["fetch", "-q", "origin"])?;

    let report = fx.scan()?;
    let candidate = registered(&report, &wt)?;
    let state = candidate.signals.merge_state;
    ensure!(state == Some(MergeState::Ancestor), "got {state:?}");
    let against = candidate.signals.merge_state_against.as_deref();
    ensure!(
        against == Some("origin/main"),
        "measured against {against:?}"
    );
    Ok(())
}
