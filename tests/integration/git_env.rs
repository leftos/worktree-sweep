use std::fs;
use std::process::Command;

use anyhow::{Context, Result, ensure};
use serde_json::Value;

use crate::fixture::{Fixture, add_worktree, same_path};

/// A git hook exports `GIT_DIR`, `GIT_INDEX_FILE` and the like to everything it starts; the scanner, run under
/// such an environment, must still read the repos it was pointed at.
#[test]
fn git_runner_ignores_inherited_repo_env() -> Result<()> {
    let fx = Fixture::new()?;
    let repo = fx.repo("repo")?;
    let wt = fx.path("repo.wt/feat");
    add_worktree(&repo, &wt, "feat")?;
    fs::write(wt.join("README.md"), "changed\n")?;

    let output = Command::new(env!("CARGO_BIN_EXE_worktree-sweep"))
        .arg(fx.root())
        .arg("--json")
        .env("GIT_DIR", fx.path("nonexistent"))
        .env("GIT_INDEX_FILE", ".git/index.lock")
        .output()
        .context("cannot run worktree-sweep")?;
    let stderr = String::from_utf8_lossy(&output.stderr);
    ensure!(output.status.success(), "worktree-sweep failed: {stderr}");
    let report: Value = serde_json::from_slice(&output.stdout).context("report is not JSON")?;

    let candidates = report["candidates"]
        .as_array()
        .context("no candidates array")?;
    ensure!(
        candidates.len() == 1,
        "expected one candidate: {report:#}\nstderr: {stderr}"
    );
    let candidate = &candidates[0];
    let path = candidate["path"]
        .as_str()
        .context("candidate has no path")?;
    ensure!(same_path(path.as_ref(), &wt), "candidate: {candidate:#}");
    ensure!(
        candidate["merge_state"]["state"] == "no_commits",
        "candidate: {candidate:#}"
    );
    ensure!(
        candidate["dirty"]["modified"] == 1,
        "candidate: {candidate:#}"
    );
    ensure!(
        candidate.get("errors").is_none(),
        "candidate: {candidate:#}\nstderr: {stderr}"
    );
    Ok(())
}
