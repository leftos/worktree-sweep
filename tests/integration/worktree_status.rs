use std::fs;

use anyhow::{Result, ensure};
use worktree_sweep::signals::{Dirty, Upstream};

use crate::fixture::{Fixture, add_worktree, commit_file, git, registered};

#[test]
fn dirty_counts_modified_and_untracked() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    fs::write(wt.join("README.md"), "changed\n")?;
    fs::write(wt.join("staged.txt"), "staged\n")?;
    git(&wt, &["add", "staged.txt"])?;
    fs::write(wt.join("new-1.txt"), "1\n")?;
    fs::write(wt.join("new-2.txt"), "2\n")?;

    let report = fx.scan()?;
    let dirty = registered(&report, &wt)?.signals.dirty;
    ensure!(
        dirty
            == Some(Dirty {
                modified: 2,
                untracked: 2
            }),
        "got {dirty:?}"
    );
    Ok(())
}

#[test]
fn ignored_files_are_not_dirty() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    commit_file(&repo, ".gitignore", "*.log\nbuild/\n", "ignore logs")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    fs::write(wt.join("debug.log"), "log\n")?;
    fs::create_dir(wt.join("build"))?;
    fs::write(wt.join("build").join("out.bin"), "bin")?;

    let report = fx.scan()?;
    let dirty = registered(&report, &wt)?.signals.dirty;
    ensure!(dirty == Some(Dirty::default()), "got {dirty:?}");
    Ok(())
}

#[test]
fn upstream_gone_is_reported() -> Result<()> {
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
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    commit_file(&wt, "b.txt", "b\n", "b")?;
    git(&wt, &["push", "-q", "-u", "origin", "feat"])?;
    git(&wt, &["push", "-q", "origin", "--delete", "feat"])?;

    let report = fx.scan()?;
    let upstream = registered(&report, &wt)?.signals.upstream;
    ensure!(upstream == Some(Upstream::Gone), "got {upstream:?}");
    Ok(())
}

#[test]
fn unpushed_commits_are_counted() -> Result<()> {
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
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    commit_file(&wt, "b.txt", "b\n", "b")?;
    git(&wt, &["push", "-q", "-u", "origin", "feat"])?;
    commit_file(&wt, "c.txt", "c\n", "c")?;
    commit_file(&wt, "d.txt", "d\n", "d")?;

    let report = fx.scan()?;
    let upstream = registered(&report, &wt)?.signals.upstream;
    ensure!(
        upstream == Some(Upstream::Tracking { ahead: 2 }),
        "got {upstream:?}"
    );
    Ok(())
}
