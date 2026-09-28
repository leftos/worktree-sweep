//! `resolve_one` on fixtures, and the `remove` command run as a child process.

use std::ffi::OsStr;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail, ensure};
use serde_json::Value;
use tempfile::TempDir;
use worktree_sweep::git::clear_repo_env;
use worktree_sweep::signals::MergeState;
use worktree_sweep::{RefusalReason, Resolved, resolve_one};

use crate::fixture::{Fixture, add_worktree, commit_file, git, same_path};

/// How long one `remove` run may take; its own Shell timeout is 30 s, so this only catches a hang.
const RUN_TIMEOUT: Duration = Duration::from_secs(120);
const READY_TIMEOUT: Duration = Duration::from_secs(30);

/// A repo `x` under the fixture root with a clean worktree at `x.wt\feat` on a new branch `feat`.
fn repo_with_worktree(fx: &Fixture) -> Result<(PathBuf, PathBuf)> {
    let repo = fx.repo("x")?;
    let worktree = fx.path(r"x.wt\feat");
    add_worktree(&repo, &worktree, "feat")?;
    Ok((repo, worktree))
}

fn refusal(path: &Path) -> Result<RefusalReason> {
    match resolve_one(path)? {
        Ok(resolved) => bail!(
            "{} resolved to {:?}, expected a refusal",
            path.display(),
            resolved.candidate
        ),
        Err(refusal) => Ok(refusal.reason),
    }
}

fn resolved(path: &Path) -> Result<Resolved> {
    match resolve_one(path)? {
        Ok(resolved) => Ok(resolved),
        Err(refusal) => bail!("{} refused: {refusal:?}", path.display()),
    }
}

/// Makes a directory junction; `false` (with a logged message) when `mklink /J` is unavailable.
fn make_junction(link: &Path, target: &Path) -> Result<bool> {
    let output = Command::new("cmd")
        .arg("/c")
        .arg("mklink")
        .arg("/J")
        .arg(link)
        .arg(target)
        .output()
        .context("cannot start cmd")?;
    if output.status.success() {
        return Ok(true);
    }
    tracing::warn!(
        "mklink /J failed: {}",
        String::from_utf8_lossy(&output.stderr).trim()
    );
    Ok(false)
}

#[test]
fn resolve_refuses_missing_path() -> Result<()> {
    let fx = Fixture::new()?;
    repo_with_worktree(&fx)?;
    ensure!(refusal(&fx.path(r"nothing\here"))? == RefusalReason::NotFound);
    ensure!(refusal(&fx.path(r"x.wt\other"))? == RefusalReason::NotFound);
    Ok(())
}

#[test]
fn resolve_refuses_plain_folder() -> Result<()> {
    let fx = Fixture::new()?;
    let plain = fx.path("plain");
    fs::create_dir(&plain)?;
    ensure!(refusal(&plain)? == RefusalReason::NotAWorktree);
    Ok(())
}

#[test]
fn resolve_refuses_main_worktree() -> Result<()> {
    let fx = Fixture::new()?;
    let (repo, _) = repo_with_worktree(&fx)?;
    ensure!(refusal(&repo)? == RefusalReason::MainWorktree);
    Ok(())
}

#[test]
fn resolve_refuses_bare_repo() -> Result<()> {
    let fx = Fixture::new()?;
    let bare = fx.path("bare.git");
    git(
        fx.root(),
        &["init", "-q", "--bare", &bare.to_string_lossy()],
    )?;
    ensure!(refusal(&bare)? == RefusalReason::BareRepo);
    Ok(())
}

#[test]
fn resolve_refuses_subfolder() -> Result<()> {
    let fx = Fixture::new()?;
    let (_, worktree) = repo_with_worktree(&fx)?;
    let sub = worktree.join("sub");
    fs::create_dir(&sub)?;
    ensure!(refusal(&sub)? == RefusalReason::Subfolder);
    Ok(())
}

#[test]
fn resolve_refuses_link() -> Result<()> {
    let fx = Fixture::new()?;
    let (_, worktree) = repo_with_worktree(&fx)?;
    let link = fx.path("link");
    if !make_junction(&link, &worktree)? {
        return Ok(());
    }
    ensure!(refusal(&link)? == RefusalReason::Link);
    Ok(())
}

#[test]
fn resolve_refuses_orphan() -> Result<()> {
    let fx = Fixture::new()?;
    repo_with_worktree(&fx)?;
    let orphan = fx.path(r"x.wt\gone");
    fs::create_dir_all(&orphan)?;
    let missing = fx.path(r"x\.git\worktrees\gone");
    fs::write(
        orphan.join(".git"),
        format!("gitdir: {}\n", missing.display()),
    )?;
    ensure!(refusal(&orphan)? == RefusalReason::Orphan);
    Ok(())
}

#[test]
fn resolve_finds_linked_worktree() -> Result<()> {
    let fx = Fixture::new()?;
    let (repo, worktree) = repo_with_worktree(&fx)?;
    commit_file(&worktree, "work.txt", "work\n", "work")?;
    let found = resolved(&worktree)?;
    let candidate = &found.candidate;
    ensure!(same_path(&candidate.path, &worktree), "{candidate:?}");
    ensure!(same_path(&candidate.repo, &repo), "{candidate:?}");
    ensure!(same_path(&found.main_worktree, &repo), "{found:?}");
    ensure!(candidate.branch.as_deref() == Some("feat"), "{candidate:?}");
    ensure!(candidate.prunable.is_none(), "{candidate:?}");
    ensure!(
        candidate.signals.merge_state == Some(MergeState::Unmerged { commits: 1 }),
        "{candidate:?}"
    );
    Ok(())
}

#[test]
fn resolve_finds_prunable_record() -> Result<()> {
    let (_root, _, worktree) = tmp_repo_with_worktree(r"x.wt\feat", "feat")?;
    fs::remove_dir_all(&worktree)?;
    let found = resolved(&worktree)?;
    let candidate = &found.candidate;
    ensure!(candidate.prunable.is_some(), "{candidate:?}");
    ensure!(candidate.branch.as_deref() == Some("feat"), "{candidate:?}");
    ensure!(
        candidate.signals.merge_state == Some(MergeState::NoCommits),
        "{candidate:?}"
    );
    Ok(())
}

/// The exit code and the parsed JSON of one run.
struct Run {
    code: i32,
    json: Value,
}

impl Run {
    fn text(&self, key: &str) -> Option<&str> {
        self.json.get(key).and_then(Value::as_str)
    }

    fn pids(&self, key: &str) -> Vec<u64> {
        self.json
            .get(key)
            .and_then(Value::as_array)
            .map(|items| {
                items
                    .iter()
                    .filter_map(|item| item.get("pid").and_then(Value::as_u64))
                    .collect()
            })
            .unwrap_or_default()
    }
}

fn binary() -> &'static str {
    env!("CARGO_BIN_EXE_worktree-sweep")
}

/// Runs `remove <worktree> --json` with `cwd` as the current folder.
fn remove(worktree: &Path, cwd: &Path) -> Result<Run> {
    let mut command = Command::new(binary());
    command
        .args([
            OsStr::new("remove"),
            worktree.as_os_str(),
            OsStr::new("--json"),
        ])
        .current_dir(cwd);
    finish(command)
}

/// Starts `command` with piped output and waits for it at most [`RUN_TIMEOUT`], killing it on the timeout.
fn finish(mut command: Command) -> Result<Run> {
    clear_repo_env(&mut command);
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("cannot start the child")?;
    let stdout = read_in_background(child.stdout.take());
    let stderr = read_in_background(child.stderr.take());
    let status = wait_or_kill(&mut child)?;
    let stdout = stdout.recv().context("stdout reader ended")?;
    let stderr = stderr.recv().context("stderr reader ended")?;
    let code = status
        .code()
        .with_context(|| format!("no exit code; stderr: {stderr}"))?;
    let json = serde_json::from_str(&stdout)
        .with_context(|| format!("exit {code}, stdout is not JSON: {stdout}\nstderr: {stderr}"))?;
    Ok(Run { code, json })
}

fn read_in_background(pipe: Option<impl Read + Send + 'static>) -> mpsc::Receiver<String> {
    let (sender, receiver) = mpsc::channel();
    thread::spawn(move || {
        let mut text = String::new();
        if let Some(mut pipe) = pipe
            && let Err(error) = pipe.read_to_string(&mut text)
        {
            text = format!("<cannot read: {error}>");
        }
        let _ = sender.send(text);
    });
    receiver
}

fn wait_or_kill(child: &mut Child) -> Result<std::process::ExitStatus> {
    let started = Instant::now();
    loop {
        if let Some(status) = child.try_wait()? {
            return Ok(status);
        }
        if started.elapsed() > RUN_TIMEOUT {
            child.kill().context("cannot kill the hung child")?;
            bail!(
                "the child did not finish within {} s",
                RUN_TIMEOUT.as_secs()
            );
        }
        thread::sleep(Duration::from_millis(50));
    }
}

/// A pwsh child that is killed and waited on when the guard drops.
struct Pwsh {
    child: Child,
}

impl Drop for Pwsh {
    fn drop(&mut self) {
        if self.child.kill().is_ok() {
            let _ = self.child.wait();
        }
    }
}

fn quoted(path: &Path) -> String {
    format!("'{}'", path.display().to_string().replace('\'', "''"))
}

/// A pwsh child, started in `cwd`, that holds `file` open with no sharing until killed.
fn hold_file(file: &Path, cwd: &Path) -> Result<Pwsh> {
    let ready = cwd.join("ready");
    let script = format!(
        "$f = [IO.File]::Open({}, 'Open', 'Read', 'None'); Set-Content -LiteralPath {} ready; Start-Sleep 120",
        quoted(file),
        quoted(&ready)
    );
    let mut command = Command::new("pwsh");
    command
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
        .current_dir(cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    clear_repo_env(&mut command);
    let child = Pwsh {
        child: command.spawn().context("cannot start pwsh")?,
    };
    let started = Instant::now();
    while !ready.exists() {
        ensure!(
            started.elapsed() < READY_TIMEOUT,
            "pwsh did not open {}",
            file.display()
        );
        thread::sleep(Duration::from_millis(50));
    }
    Ok(child)
}

fn worktree_list(repo: &Path) -> Result<String> {
    git(repo, &["worktree", "list", "--porcelain"])
}

fn branch_exists(repo: &Path, branch: &str) -> Result<bool> {
    Ok(!git(repo, &["branch", "--list", branch])?.is_empty())
}

#[test]
fn remove_refuses_main_worktree() -> Result<()> {
    let fx = Fixture::new()?;
    let (repo, _) = repo_with_worktree(&fx)?;
    let run = remove(&repo, fx.root())?;
    ensure!(run.code == 6, "exit {}: {}", run.code, run.json);
    ensure!(run.text("status") == Some("refused"), "{}", run.json);
    ensure!(run.text("reason") == Some("main_worktree"), "{}", run.json);
    Ok(())
}

#[test]
fn remove_refuses_dirty_without_force() -> Result<()> {
    let fx = Fixture::new()?;
    let (_, worktree) = repo_with_worktree(&fx)?;
    fs::write(worktree.join("scratch.txt"), "unsaved\n")?;
    let run = remove(&worktree, fx.root())?;
    ensure!(run.code == 6, "exit {}: {}", run.code, run.json);
    ensure!(run.text("status") == Some("refused"), "{}", run.json);
    ensure!(run.text("reason") == Some("would_lose"), "{}", run.json);
    ensure!(
        run.text("loss").is_some_and(
            |loss| loss.contains("1 untracked file") && loss.ends_with("will be lost.")
        ),
        "{}",
        run.json
    );
    ensure!(
        worktree.join("scratch.txt").exists(),
        "the worktree was touched"
    );
    Ok(())
}

#[test]
fn remove_reports_caller_holds() -> Result<()> {
    let fx = Fixture::new()?;
    let (repo, worktree) = repo_with_worktree(&fx)?;
    let script = format!(
        "& {} remove {} --json; exit $LASTEXITCODE",
        quoted(Path::new(binary())),
        quoted(&worktree)
    );
    let mut command = Command::new("pwsh");
    command
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
        .current_dir(&worktree);
    let run = finish(command)?;
    ensure!(run.code == 6, "exit {}: {}", run.code, run.json);
    ensure!(run.text("reason") == Some("caller_holds"), "{}", run.json);
    ensure!(
        run.text("cd_to")
            .is_some_and(|cd_to| same_path(Path::new(cd_to), &repo)),
        "{}",
        run.json
    );
    ensure!(!run.pids("holders").is_empty(), "{}", run.json);
    ensure!(
        worktree.join("README.md").exists(),
        "the worktree was touched"
    );
    Ok(())
}

/// Reaches the Shell recycle, which fails on the lock: run by hand with
/// `cargo test --test integration agent_remove -- --ignored`.
#[test]
#[ignore = "calls the Shell's recycle on a locked fixture"]
fn remove_releases_locked_worktree() -> Result<()> {
    let fx = Fixture::new()?;
    let (repo, worktree) = repo_with_worktree(&fx)?;
    // An ignored file, so the lock does not make git report it modified (which would refuse with would_lose).
    let info = repo.join(".git").join("info");
    fs::create_dir_all(&info)?;
    fs::write(info.join("exclude"), "held.txt\n")?;
    let held = worktree.join("held.txt");
    fs::write(&held, "held\n")?;
    let side = tempfile::tempdir()?;
    let holder = hold_file(&held, side.path())?;
    let run = remove(&worktree, fx.root())?;
    let pid = u64::from(holder.child.id());
    ensure!(run.code == 5, "exit {}: {}", run.code, run.json);
    ensure!(run.text("status") == Some("released"), "{}", run.json);
    ensure!(run.text("reason") == Some("locked"), "{}", run.json);
    ensure!(
        run.pids("holders").contains(&pid),
        "pid {pid}: {}",
        run.json
    );
    ensure!(
        run.json.get("released") == Some(&Value::Bool(true)),
        "{}",
        run.json
    );
    let marker = repo
        .join(".git")
        .join("worktrees")
        .join("feat")
        .join("worktree-sweep-released.json");
    let marker: Value = serde_json::from_str(
        &fs::read_to_string(&marker)
            .with_context(|| format!("cannot read {}", marker.display()))?,
    )?;
    ensure!(
        marker.get("reason").and_then(Value::as_str) == Some("locked"),
        "{marker}"
    );
    ensure!(
        marker.get("released_at").and_then(Value::as_i64).is_some(),
        "{marker}"
    );
    let marker_pids: Vec<u64> = marker
        .get("holders")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| item.get("pid").and_then(Value::as_u64))
                .collect()
        })
        .unwrap_or_default();
    ensure!(marker_pids.contains(&pid), "pid {pid}: {marker}");
    ensure!(
        worktree.join("README.md").exists(),
        "part of the worktree was recycled"
    );
    Ok(())
}

#[test]
fn remove_prunable_record() -> Result<()> {
    let (root, repo, worktree) = tmp_repo_with_worktree(r"x.wt\feat", "feat")?;
    fs::remove_dir_all(&worktree)?;
    let run = remove(&worktree, root.path())?;
    ensure!(run.code == 0, "exit {}: {}", run.code, run.json);
    ensure!(run.text("status") == Some("removed"), "{}", run.json);
    ensure!(
        run.json.get("branch_deleted") == Some(&Value::Bool(true)),
        "{}",
        run.json
    );
    ensure!(!worktree_list(&repo)?.contains("feat"), "still registered");
    ensure!(!branch_exists(&repo, "feat")?, "branch feat still there");
    Ok(())
}

/// A worktree beside its repo (`x-feat` next to `x`), its folder deleted, is found through the sibling repo.
#[test]
fn remove_prunable_sibling_worktree() -> Result<()> {
    let (root, repo, worktree) = tmp_repo_with_worktree("x-feat", "feat")?;
    fs::remove_dir_all(&worktree)?;
    let run = remove(&worktree, root.path())?;
    ensure!(run.code == 0, "exit {}: {}", run.code, run.json);
    ensure!(run.text("status") == Some("removed"), "{}", run.json);
    ensure!(
        run.json.get("branch_deleted") == Some(&Value::Bool(true)),
        "{}",
        run.json
    );
    ensure!(
        !worktree_list(&repo)?.contains("x-feat"),
        "still registered"
    );
    ensure!(!branch_exists(&repo, "feat")?, "branch feat still there");
    Ok(())
}

/// A worktree folder whose `.git` file is gone is prunable to git but still on disk: it is refused as an orphan,
/// and neither the record nor the branch is touched.
#[test]
fn remove_refuses_prunable_record_whose_folder_remains() -> Result<()> {
    let (root, repo, worktree) = tmp_repo_with_worktree(r"x\.claude\worktrees\a", "a")?;
    fs::remove_file(worktree.join(".git"))?;
    let run = remove(&worktree, root.path())?;
    ensure!(run.code == 6, "exit {}: {}", run.code, run.json);
    ensure!(run.text("status") == Some("refused"), "{}", run.json);
    ensure!(run.text("reason") == Some("orphan"), "{}", run.json);
    ensure!(
        worktree.join("README.md").exists(),
        "the folder was touched"
    );
    ensure!(
        worktree_list(&repo)?.contains(".claude/worktrees/a"),
        "the record was pruned"
    );
    ensure!(branch_exists(&repo, "a")?, "branch a was deleted");
    Ok(())
}

/// A repo `x` in a fresh folder under `.tmp/` with a worktree at `relative` (to that folder) on a new branch.
fn tmp_repo_with_worktree(relative: &str, branch: &str) -> Result<(TempDir, PathBuf, PathBuf)> {
    let _isolated = Fixture::new()?;
    let root = repo_tmp_dir()?;
    let repo = root.path().join("x");
    fs::create_dir(&repo)?;
    git(&repo, &["init", "-q", "-b", "main"])?;
    commit_file(&repo, "README.md", "readme\n", "initial")?;
    let worktree = root.path().join(relative);
    add_worktree(&repo, &worktree, branch)?;
    Ok((root, repo, worktree))
}

/// A folder under the repo's gitignored `.tmp/` (the Dev Drive), whose Recycle Bin the test uses.
fn repo_tmp_dir() -> Result<TempDir> {
    let tmp = Path::new(env!("CARGO_MANIFEST_DIR")).join(".tmp");
    fs::create_dir_all(&tmp).with_context(|| format!("cannot create {}", tmp.display()))?;
    tempfile::Builder::new()
        .prefix("agent-remove-")
        .tempdir_in(&tmp)
        .with_context(|| format!("cannot make a temporary folder in {}", tmp.display()))
}

/// Moves a real worktree to the Recycle Bin: run by hand with
/// `cargo test --test integration agent_remove -- --ignored`.
#[test]
#[ignore = "moves a fixture worktree to the real Recycle Bin"]
fn remove_recycles_clean_worktree() -> Result<()> {
    let (root, repo, worktree) = tmp_repo_with_worktree(r"x.wt\feat", "feat")?;
    let run = remove(&worktree, root.path())?;
    ensure!(run.code == 0, "exit {}: {}", run.code, run.json);
    ensure!(run.text("status") == Some("removed"), "{}", run.json);
    ensure!(!worktree.exists(), "{} still there", worktree.display());
    ensure!(!worktree_list(&repo)?.contains("feat"), "still registered");
    ensure!(!branch_exists(&repo, "feat")?, "branch feat still there");
    Ok(())
}
