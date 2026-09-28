use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use anyhow::{Context, Result, bail, ensure};
use worktree_sweep::agent::{MARKER_FILE, Reason};
use worktree_sweep::discover::read_gitdir_file;
use worktree_sweep::resolve_one;

use crate::fixture::{Fixture, add_worktree, git, registered};

/// A repo with one linked worktree, and that worktree's admin dir.
fn worktree_with_admin(fx: &Fixture) -> Result<(PathBuf, PathBuf)> {
    let repo = fx.repo("repo")?;
    let worktree = fx.path("repo.wt/feat");
    add_worktree(&repo, &worktree, "feat")?;
    let admin = read_gitdir_file(&worktree).context("the worktree has no admin dir")?;
    Ok((worktree, admin))
}

#[test]
fn marker_in_admin_dir_marks_worktree_released() -> Result<()> {
    let fx = Fixture::new()?;
    let (worktree, admin) = worktree_with_admin(&fx)?;
    fs::write(
        admin.join(MARKER_FILE),
        r#"{"released_at": 1790000000, "reason": "locked", "holders": [{"pid": 42, "exe": "devenv.exe"}]}"#,
    )?;

    let report = fx.scan()?;
    let candidate = registered(&report, &worktree)?;
    let released = candidate
        .released
        .as_ref()
        .with_context(|| format!("not released: {candidate:?}"))?;
    ensure!(released.reason == Reason::Locked, "{released:?}");
    ensure!(released.released_at == 1_790_000_000, "{released:?}");
    ensure!(
        released.holders.len() == 1 && released.holders[0].exe == "devenv.exe",
        "{released:?}"
    );
    Ok(())
}

#[test]
fn worktree_without_marker_is_not_released() -> Result<()> {
    let fx = Fixture::new()?;
    let (worktree, _admin) = worktree_with_admin(&fx)?;

    let report = fx.scan()?;
    let candidate = registered(&report, &worktree)?;
    ensure!(candidate.released.is_none(), "released: {candidate:?}");
    Ok(())
}

const MARKER: &str = r#"{"released_at": 1790000000, "reason": "locked", "holders": []}"#;

/// Writes a marker into the worktree's admin dir, then deletes the worktree folder so git calls it prunable.
fn mark_and_delete(worktree: &Path) -> Result<()> {
    let admin = read_gitdir_file(worktree).context("the worktree has no admin dir")?;
    fs::write(admin.join(MARKER_FILE), MARKER)?;
    fs::remove_dir_all(worktree)?;
    Ok(())
}

/// Whether the installed git knows `worktree.useRelativePaths` (git 2.48 and later).
fn git_has_relative_worktrees() -> Result<bool> {
    let output = Command::new("git").arg("--version").output()?;
    let text = String::from_utf8_lossy(&output.stdout);
    let mut numbers = text
        .split_whitespace()
        .nth(2)
        .unwrap_or_default()
        .split('.')
        .map(|part| part.parse::<u32>().unwrap_or(0));
    let major = numbers.next().unwrap_or(0);
    let minor = numbers.next().unwrap_or(0);
    Ok((major, minor) >= (2, 48))
}

#[test]
fn prunable_worktree_keeps_its_marker() -> Result<()> {
    let fx = Fixture::new()?;
    let (worktree, _admin) = worktree_with_admin(&fx)?;
    mark_and_delete(&worktree)?;

    let report = fx.scan()?;
    let candidate = registered(&report, &worktree)?;
    ensure!(candidate.prunable.is_some(), "not prunable: {candidate:?}");
    ensure!(candidate.released.is_some(), "not released: {candidate:?}");
    Ok(())
}

#[test]
fn prunable_worktree_with_relative_paths_keeps_its_marker() -> Result<()> {
    if !git_has_relative_worktrees()? {
        tracing::warn!(
            "skipped: the installed git has no worktree.useRelativePaths (needs 2.48 or later)"
        );
        return Ok(());
    }
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let worktree = fx.path("repo.wt/feat");
    git(
        &repo,
        &[
            "-c",
            "worktree.useRelativePaths=true",
            "worktree",
            "add",
            "-q",
            "-b",
            "feat",
            &worktree.to_string_lossy(),
        ],
    )?;
    mark_and_delete(&worktree)?;

    let report = fx.scan()?;
    let candidate = registered(&report, &worktree)?;
    ensure!(candidate.prunable.is_some(), "not prunable: {candidate:?}");
    ensure!(candidate.released.is_some(), "not released: {candidate:?}");
    Ok(())
}

#[test]
fn prunable_worktree_of_bare_repo_keeps_its_marker() -> Result<()> {
    let fx = Fixture::new()?;
    let source = fx.repo("source")?;
    let bare = fx.path("bare");
    git(
        fx.root(),
        &[
            "clone",
            "-q",
            "--bare",
            &source.to_string_lossy(),
            &bare.to_string_lossy(),
        ],
    )?;
    let worktree = fx.path("bare.wt/feat");
    add_worktree(&bare, &worktree, "feat")?;
    mark_and_delete(&worktree)?;

    let resolved = match resolve_one(&worktree)? {
        Ok(resolved) => resolved,
        Err(refusal) => bail!("{} refused: {refusal:?}", worktree.display()),
    };
    let candidate = &resolved.candidate;
    ensure!(candidate.prunable.is_some(), "not prunable: {candidate:?}");
    ensure!(candidate.released.is_some(), "not released: {candidate:?}");
    Ok(())
}

#[test]
fn malformed_marker_is_ignored() -> Result<()> {
    let fx = Fixture::new()?;
    let (worktree, admin) = worktree_with_admin(&fx)?;
    fs::write(admin.join(MARKER_FILE), "{not json")?;

    let report = fx.scan()?;
    let candidate = registered(&report, &worktree)?;
    ensure!(candidate.released.is_none(), "released: {candidate:?}");
    Ok(())
}
