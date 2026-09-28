//! Throwaway repos built with real `git` in temporary folders, isolated from the user's and the system's git
//! config.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;

use anyhow::{Context, Result, anyhow, bail};
use tempfile::TempDir;
use worktree_sweep::discover::path_key;
use worktree_sweep::git::clear_repo_env;
use worktree_sweep::report::{Candidate, OrphanCandidate, RegisteredCandidate, Report};

/// Makes every git process this test binary starts, the code under test's included, ignore the system and global
/// config: sets `GIT_CONFIG_NOSYSTEM` and points `GIT_CONFIG_GLOBAL` at an empty file. Runs once per process.
fn isolate_git_config() -> Result<()> {
    static DONE: OnceLock<Result<(), String>> = OnceLock::new();
    DONE.get_or_init(|| {
        let empty = Path::new(env!("CARGO_TARGET_TMPDIR")).join("empty-gitconfig");
        fs::write(&empty, "")
            .map_err(|error| format!("cannot write {}: {error}", empty.display()))?;
        // SAFETY: every test calls this before it starts any git process, `OnceLock` runs it on one thread while
        // the others wait, and on Windows the process environment is guarded by the OS, so no read races the write.
        unsafe {
            std::env::set_var("GIT_CONFIG_NOSYSTEM", "1");
            std::env::set_var("GIT_CONFIG_GLOBAL", &empty);
        }
        Ok(())
    })
    .clone()
    .map_err(|error| anyhow!(error))
}

/// A temporary root folder to scan.
pub struct Fixture {
    dir: TempDir,
}

impl Fixture {
    pub fn new() -> Result<Self> {
        isolate_git_config()?;
        Ok(Self {
            dir: TempDir::new().context("cannot create a temporary folder")?,
        })
    }

    pub fn root(&self) -> &Path {
        self.dir.path()
    }

    /// A path under the root.
    pub fn path(&self, relative: &str) -> PathBuf {
        self.root().join(relative)
    }

    /// Creates `<root>/<name>` with `git init -b main` and one commit adding `README.md`.
    pub fn repo(&self, name: &str) -> Result<PathBuf> {
        let repo = self.path(name);
        fs::create_dir_all(&repo).with_context(|| format!("cannot create {}", repo.display()))?;
        git(&repo, &["init", "-b", "main"])?;
        commit_file(&repo, "README.md", "readme\n", "initial")?;
        Ok(repo)
    }

    /// Scans the root.
    pub fn scan(&self) -> Result<Report> {
        worktree_sweep::scan(self.root())
    }
}

/// Runs git in `dir` with a fixed identity and no signing, without the repo-local variables a calling git hook
/// exports; returns trimmed stdout.
pub fn git(dir: &Path, args: &[&str]) -> Result<String> {
    let mut command = Command::new("git");
    clear_repo_env(&mut command);
    let output = command
        .arg("-C")
        .arg(dir)
        .args([
            "-c",
            "user.name=t",
            "-c",
            "user.email=t@t",
            "-c",
            "commit.gpgsign=false",
        ])
        .args(args)
        .output()
        .with_context(|| format!("cannot start git {args:?}"))?;
    if !output.status.success() {
        bail!(
            "git {args:?} in {} failed: {}",
            dir.display(),
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }
    Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned())
}

/// Writes `file` in `dir`, stages it and commits it; returns the new commit id.
pub fn commit_file(dir: &Path, file: &str, content: &str, message: &str) -> Result<String> {
    let path = dir.join(file);
    fs::write(&path, content).with_context(|| format!("cannot write {}", path.display()))?;
    git(dir, &["add", file])?;
    git(dir, &["commit", "-q", "-m", message])?;
    git(dir, &["rev-parse", "HEAD"])
}

/// Adds a worktree at `path` on a new branch `branch` from the repo's HEAD.
pub fn add_worktree(repo: &Path, path: &Path, branch: &str) -> Result<()> {
    let path_text = path.to_string_lossy();
    git(repo, &["worktree", "add", "-q", "-b", branch, &path_text])?;
    Ok(())
}

/// The registered candidate at `path`.
pub fn registered<'a>(report: &'a Report, path: &Path) -> Result<&'a RegisteredCandidate> {
    report
        .candidates
        .iter()
        .find_map(|candidate| match candidate {
            Candidate::Registered(registered) if same_path(&registered.path, path) => {
                Some(registered)
            }
            _ => None,
        })
        .with_context(|| {
            format!(
                "no registered candidate at {}; report: {report:#?}",
                path.display()
            )
        })
}

/// Every orphan in the report.
pub fn orphans(report: &Report) -> Vec<&OrphanCandidate> {
    report
        .candidates
        .iter()
        .filter_map(|candidate| match candidate {
            Candidate::Orphan(orphan) => Some(orphan),
            Candidate::Registered(_) => None,
        })
        .collect()
}

/// The paths of every candidate in the report.
pub fn candidate_paths(report: &Report) -> Vec<&Path> {
    report
        .candidates
        .iter()
        .map(|candidate| match candidate {
            Candidate::Registered(registered) => registered.path.as_path(),
            Candidate::Orphan(orphan) => orphan.orphan.path.as_path(),
        })
        .collect()
}

/// Makes a directory junction; `false` (with a logged message) when `mklink /J` is unavailable.
pub fn make_junction(link: &Path, target: &Path) -> Result<bool> {
    let output = Command::new("cmd")
        .arg("/c")
        .arg("mklink")
        .arg("/J")
        .arg(link)
        .arg(target)
        .output();
    match output {
        Ok(output) if output.status.success() => Ok(true),
        Ok(output) => {
            writeln!(
                std::io::stderr(),
                "skipping: mklink /J failed: {}",
                String::from_utf8_lossy(&output.stderr).trim()
            )?;
            Ok(false)
        }
        Err(error) => {
            writeln!(
                std::io::stderr(),
                "skipping: cannot run cmd /c mklink /J: {error}"
            )?;
            Ok(false)
        }
    }
}

pub fn same_path(a: &Path, b: &Path) -> bool {
    path_key(a) == path_key(b)
}
