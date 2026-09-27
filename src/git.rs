//! Runs `git` as a child process.
//!
//! Every call sets `GIT_OPTIONAL_LOCKS=0` and `core.fsmonitor=false`, so reading a repo never rewrites its index
//! or starts a file-system monitor daemon in it.

use std::ffi::OsStr;
use std::path::Path;
use std::process::{Command, Stdio};

use anyhow::{Context, Result, anyhow};

/// The result of a git command whose non-zero exit status is an answer rather than a failure.
#[derive(Debug, Clone)]
pub struct GitStatus {
    /// Exit code, or `None` when git was ended by a signal.
    pub code: Option<i32>,
    /// Standard output, decoded as UTF-8 (lossily) and not trimmed.
    pub stdout: String,
    /// Standard error, decoded as UTF-8 (lossily) and trimmed.
    pub stderr: String,
}

impl GitStatus {
    /// Whether git exited with status 0.
    #[must_use]
    pub fn success(&self) -> bool {
        self.code == Some(0)
    }
}

/// Runs `git -C <dir> <args>` and returns its trimmed standard output.
///
/// # Errors
///
/// When git cannot be started or exits with a non-zero status; the error names the command, the exit code and
/// git's standard error.
pub fn run(dir: &Path, args: &[&str]) -> Result<String> {
    Ok(run_raw(dir, args)?.trim().to_owned())
}

/// Runs `git -C <dir> <args>` and returns its standard output untrimmed, for output where leading spaces or NUL
/// separators carry meaning.
///
/// # Errors
///
/// When git cannot be started or exits with a non-zero status; the error names the command, the exit code and
/// git's standard error.
pub fn run_raw(dir: &Path, args: &[&str]) -> Result<String> {
    let status = run_status(dir, args)?;
    if status.success() {
        Ok(status.stdout)
    } else {
        Err(failure(dir, args, &status))
    }
}

/// Runs `git -C <dir> <args>` and returns its exit status and output without treating a non-zero exit as an error.
///
/// # Errors
///
/// Only when git cannot be started.
pub fn run_status(dir: &Path, args: &[&str]) -> Result<GitStatus> {
    run_status_with_env(dir, args, &[])
}

/// Like [`run_status`], with extra environment variables set for the child.
///
/// # Errors
///
/// Only when git cannot be started.
pub fn run_status_with_env(
    dir: &Path,
    args: &[&str],
    envs: &[(&str, &OsStr)],
) -> Result<GitStatus> {
    let mut command = Command::new("git");
    command
        .arg("-C")
        .arg(dir)
        .args(["-c", "core.fsmonitor=false"])
        .args(args)
        .env("GIT_OPTIONAL_LOCKS", "0")
        .stdin(Stdio::null());
    clear_repo_env(&mut command);
    for (key, value) in envs {
        command.env(key, value);
    }
    let output = command
        .output()
        .with_context(|| format!("failed to start {}; is git on PATH?", describe(dir, args)))?;
    Ok(GitStatus {
        code: output.status.code(),
        stdout: String::from_utf8_lossy(&output.stdout).into_owned(),
        stderr: String::from_utf8_lossy(&output.stderr).trim().to_owned(),
    })
}

/// The repo-local environment variables, as `git rev-parse --local-env-vars` lists them (git 2.55). A git hook,
/// or any process git starts, inherits them pointing at the calling repo; a child git that kept them would read
/// or write that repo instead of the one it was pointed at with `-C`.
pub const REPO_LOCAL_ENV_VARS: [&str; 15] = [
    "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    "GIT_CONFIG",
    "GIT_CONFIG_PARAMETERS",
    "GIT_CONFIG_COUNT",
    "GIT_OBJECT_DIRECTORY",
    "GIT_DIR",
    "GIT_WORK_TREE",
    "GIT_IMPLICIT_WORK_TREE",
    "GIT_GRAFT_FILE",
    "GIT_INDEX_FILE",
    "GIT_NO_REPLACE_OBJECTS",
    "GIT_REPLACE_REF_BASE",
    "GIT_PREFIX",
    "GIT_SHALLOW_FILE",
    "GIT_COMMON_DIR",
];

/// Removes every [`REPO_LOCAL_ENV_VARS`] entry from a command's environment.
pub fn clear_repo_env(command: &mut Command) {
    for key in REPO_LOCAL_ENV_VARS {
        command.env_remove(key);
    }
}

/// The error for a git command that exited with a status the caller does not accept.
#[must_use]
pub fn failure(dir: &Path, args: &[&str], status: &GitStatus) -> anyhow::Error {
    let code = status.code.map_or_else(
        || "none (ended by a signal)".to_owned(),
        |code| code.to_string(),
    );
    anyhow!(
        "{} exited with code {code}: {}",
        describe(dir, args),
        status.stderr
    )
}

fn describe(dir: &Path, args: &[&str]) -> String {
    format!("`git -C {} {}`", dir.display(), args.join(" "))
}
