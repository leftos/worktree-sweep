use std::fs;

use anyhow::{Context, Result, ensure};
use worktree_sweep::discover::OrphanKind;

use crate::fixture::{
    Fixture, add_worktree, candidate_paths, make_junction, orphans, registered, same_path,
};

#[test]
fn main_worktree_is_never_a_candidate() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;

    let report = fx.scan()?;
    let paths = candidate_paths(&report);
    ensure!(
        paths.iter().any(|path| same_path(path, &wt)),
        "linked worktree missing: {paths:?}"
    );
    ensure!(
        !paths.iter().any(|path| same_path(path, &repo)),
        "main worktree listed: {paths:?}"
    );
    ensure!(report.repos.len() == 1, "repos: {:?}", report.repos);
    Ok(())
}

#[test]
fn nested_registered_worktree_in_container_is_not_an_orphan() -> Result<()> {
    let fx = Fixture::new()?;
    let a = fx.repo("a")?;
    let b = fx.repo("b")?;
    let wt_a = fx.path("x.wt/eram-am/a");
    let wt_b = fx.path("x.wt/eram-am/b");
    add_worktree(&a, &wt_a, "eram-am")?;
    add_worktree(&b, &wt_b, "eram-am")?;

    let report = fx.scan()?;
    registered(&report, &wt_a)?;
    registered(&report, &wt_b)?;
    let found = orphans(&report);
    ensure!(found.is_empty(), "unexpected orphans: {found:?}");
    Ok(())
}

#[test]
fn unregistered_sibling_folder_is_an_orphan() -> Result<()> {
    let fx = Fixture::new()?;
    let a = fx.repo("a")?;
    add_worktree(&a, &fx.path("x.wt/eram-am/a"), "eram-am")?;
    let leftover = fx.path("x.wt/eram-qx/b");
    fs::create_dir_all(&leftover)?;
    fs::write(leftover.join("file.txt"), "left behind\n")?;

    let report = fx.scan()?;
    let found = orphans(&report);
    ensure!(found.len() == 1, "expected one orphan: {found:?}");
    ensure!(
        same_path(&found[0].orphan.path, &fx.path("x.wt/eram-qx")),
        "orphan: {found:?}"
    );
    ensure!(
        found[0].orphan.orphan_kind == OrphanKind::Folder,
        "orphan: {found:?}"
    );
    Ok(())
}

#[test]
fn empty_container_child_is_an_orphan() -> Result<()> {
    let fx = Fixture::new()?;
    let empty = fx.path("x.wt/empty");
    fs::create_dir_all(&empty)?;

    let report = fx.scan()?;
    let found = orphans(&report);
    ensure!(
        found.len() == 1 && same_path(&found[0].orphan.path, &empty),
        "orphans: {found:?}"
    );
    ensure!(
        found[0].size.files == 0 && found[0].size.bytes == 0,
        "size: {:?}",
        found[0].size
    );
    Ok(())
}

#[test]
fn orphan_with_missing_gitdir_is_flagged() -> Result<()> {
    let fx = Fixture::new()?;
    let stale = fx.path("x.wt/stale");
    fs::create_dir_all(&stale)?;
    let missing = fx.path("gone/.git/worktrees/stale");
    fs::write(
        stale.join(".git"),
        format!("gitdir: {}\n", missing.to_string_lossy().replace('\\', "/")),
    )?;
    let plain = fx.path("x.wt/plain");
    fs::create_dir_all(&plain)?;

    let report = fx.scan()?;
    let found = orphans(&report);
    let stale_orphan = found
        .iter()
        .find(|o| same_path(&o.orphan.path, &stale))
        .context("stale orphan missing")?;
    ensure!(
        stale_orphan.orphan.stale_gitdir,
        "stale orphan: {stale_orphan:?}"
    );
    ensure!(
        !stale_orphan.orphan.has_git_dir,
        "stale orphan: {stale_orphan:?}"
    );
    let plain_orphan = found
        .iter()
        .find(|o| same_path(&o.orphan.path, &plain))
        .context("plain orphan missing")?;
    ensure!(
        !plain_orphan.orphan.stale_gitdir,
        "plain orphan: {plain_orphan:?}"
    );
    Ok(())
}

#[test]
fn prunable_registration_is_a_candidate() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/gone");
    add_worktree(&repo, &wt, "gone")?;
    fs::remove_dir_all(&wt)?;

    let report = fx.scan()?;
    let candidate = registered(&report, &wt)?;
    ensure!(candidate.prunable.is_some(), "not prunable: {candidate:?}");
    ensure!(
        candidate.signals.merge_state.is_none() && candidate.signals.size.is_none(),
        "signals read: {candidate:?}"
    );
    let found = orphans(&report);
    ensure!(found.is_empty(), "unexpected orphans: {found:?}");
    Ok(())
}

#[test]
fn claude_worktrees_dir_is_a_container() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let agent = repo.join(".claude").join("worktrees").join("agent-1");
    fs::create_dir_all(&agent)?;

    let report = fx.scan()?;
    let found = orphans(&report);
    ensure!(
        found.len() == 1 && same_path(&found[0].orphan.path, &agent),
        "orphans: {found:?}"
    );
    Ok(())
}

#[test]
fn junction_in_container_is_not_followed() -> Result<()> {
    let fx = Fixture::new()?;
    let target = fx.path("target");
    fs::create_dir_all(target.join("inner"))?;
    fs::write(target.join("inner").join("big.bin"), vec![0_u8; 100])?;
    let container = fx.path("x.wt");
    fs::create_dir_all(&container)?;
    let link = container.join("link");

    if !make_junction(&link, &target)? {
        return Ok(());
    }

    let report = fx.scan()?;
    let found = orphans(&report);
    ensure!(found.len() == 1, "expected only the link: {found:?}");
    let orphan = found[0];
    ensure!(same_path(&orphan.orphan.path, &link), "orphan: {orphan:?}");
    ensure!(
        orphan.orphan.orphan_kind == OrphanKind::Link,
        "orphan: {orphan:?}"
    );
    ensure!(
        orphan.size.files == 0 && orphan.size.bytes == 0,
        "target was walked: {orphan:?}"
    );
    let link_target = orphan
        .orphan
        .link_target
        .as_deref()
        .context("link target missing")?;
    ensure!(
        same_path(link_target, &target),
        "link target: {link_target:?}"
    );
    Ok(())
}
