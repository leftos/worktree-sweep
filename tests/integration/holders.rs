use std::fs;
use std::io::Write;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail, ensure};
use tempfile::TempDir;
use windows::Win32::Storage::FileSystem::GetShortPathNameW;
use windows::core::PCWSTR;
use worktree_sweep::git::clear_repo_env;
use worktree_sweep::holders::{Hold, Holder, HolderReport, MayHoldWhy, find_holders, still_same};
use worktree_sweep::unlock::matches_locked_path;

use crate::fixture::make_junction;

const READY_TIMEOUT: Duration = Duration::from_secs(30);

/// A pwsh child that is killed and waited on when the guard drops.
struct Pwsh {
    child: Child,
}

impl Pwsh {
    fn pid(&self) -> u32 {
        self.child.id()
    }

    fn stop(&mut self) -> Result<()> {
        self.child.kill().context("cannot kill the pwsh child")?;
        self.child
            .wait()
            .context("cannot wait for the pwsh child")?;
        Ok(())
    }
}

impl Drop for Pwsh {
    fn drop(&mut self) {
        if let Err(error) = self.stop() {
            let _ = writeln!(std::io::stderr(), "cleanup: {error:#}");
        }
    }
}

fn quoted(path: &Path) -> String {
    format!("'{}'", path.display().to_string().replace('\'', "''"))
}

/// Starts `pwsh` running `script` in `cwd`, then waits until the script has written `ready`.
fn start_pwsh(script: &str, cwd: &Path, ready: &Path) -> Result<Pwsh> {
    let full = format!(
        "{script}; Set-Content -LiteralPath {} ready; Start-Sleep 120",
        quoted(ready)
    );
    spawn_pwsh(&full, cwd, ready)
}

/// Starts `pwsh -Command <full>` in `cwd`, then waits until `ready` exists.
fn spawn_pwsh(full: &str, cwd: &Path, ready: &Path) -> Result<Pwsh> {
    let mut command = Command::new("pwsh");
    command
        .args(["-NoProfile", "-NonInteractive", "-Command", full])
        .current_dir(cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    clear_repo_env(&mut command);
    let child = Pwsh {
        child: command.spawn().context("cannot start pwsh")?,
    };
    wait_for(ready).with_context(|| format!("pwsh {} did not become ready", child.pid()))?;
    Ok(child)
}

fn wait_for(path: &Path) -> Result<()> {
    let started = Instant::now();
    while !path.exists() {
        if started.elapsed() > READY_TIMEOUT {
            bail!("{} did not appear", path.display());
        }
        thread::sleep(Duration::from_millis(50));
    }
    Ok(())
}

/// A scanned folder with a subfolder, and a separate folder for ready files and children's cwd.
struct Folders {
    scanned: TempDir,
    side: TempDir,
}

impl Folders {
    fn new() -> Result<Self> {
        let scanned = tempfile::Builder::new()
            .prefix("holders long folder name ")
            .tempdir()?;
        fs::create_dir(scanned.path().join("sub folder"))?;
        Ok(Self {
            scanned,
            side: tempfile::tempdir()?,
        })
    }

    fn ready(&self, name: &str) -> PathBuf {
        self.side.path().join(name)
    }

    /// A pwsh child whose process cwd is the scanned folder's subfolder.
    fn cwd_child(&self) -> Result<Pwsh> {
        start_pwsh(
            "$null = 1",
            &self.scanned.path().join("sub folder"),
            &self.ready("cwd"),
        )
    }
}

fn holder(report: &HolderReport, pid: u32) -> Option<&Holder> {
    report.holders.iter().find(|holder| holder.pid == pid)
}

fn holds_current_folder(report: &HolderReport, pid: u32) -> bool {
    holder(report, pid).is_some_and(|holder| {
        holder
            .holds
            .iter()
            .any(|hold| matches!(hold, Hold::CurrentFolder { .. }))
    })
}

fn short_path(path: &Path) -> Result<Option<PathBuf>> {
    let wide: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut buffer = vec![0_u16; 1024];
    // SAFETY: `wide` is NUL-terminated and `buffer` is a live, writable slice for the call.
    let len = unsafe { GetShortPathNameW(PCWSTR(wide.as_ptr()), Some(&mut buffer)) } as usize;
    ensure!(
        len > 0 && len < buffer.len(),
        "GetShortPathNameW failed for {}",
        path.display()
    );
    let short = PathBuf::from(std::ffi::OsString::from_wide(&buffer[..len]));
    let same = short
        .to_string_lossy()
        .eq_ignore_ascii_case(&path.to_string_lossy());
    Ok((!same).then_some(short))
}

#[test]
fn cwd_holder_is_found() -> Result<()> {
    let folders = Folders::new()?;
    let child = folders.cwd_child()?;
    let report = find_holders(folders.scanned.path(), &[])?;
    ensure!(
        holds_current_folder(&report, child.pid()),
        "pid {} not listed with its current folder: {report:?}",
        child.pid()
    );
    Ok(())
}

#[test]
fn share_none_handle_is_found() -> Result<()> {
    let folders = Folders::new()?;
    let file = folders.scanned.path().join("sub folder").join("held.txt");
    fs::write(&file, "held")?;
    let script = format!(
        "$f = [IO.File]::Open({}, 'Open', 'ReadWrite', 'None')",
        quoted(&file)
    );
    let child = start_pwsh(&script, folders.side.path(), &folders.ready("handle"))?;
    let report = find_holders(folders.scanned.path(), &[])?;
    let expected = fs::canonicalize(&file)?;
    let names_file = holder(&report, child.pid()).is_some_and(|holder| {
        holder.holds.iter().any(|hold| match hold {
            Hold::OpenHandle { path } => {
                matches_locked_path(path, std::slice::from_ref(&expected))
                    && matches_locked_path(&expected, std::slice::from_ref(path))
            }
            Hold::CurrentFolder { .. } => false,
        })
    });
    ensure!(
        names_file,
        "pid {} not listed with an open handle on {}: {report:?}",
        child.pid(),
        file.display()
    );
    Ok(())
}

#[test]
fn short_name_folder_matches() -> Result<()> {
    let folders = Folders::new()?;
    let Some(short) = short_path(folders.scanned.path())? else {
        writeln!(
            std::io::stderr(),
            "skipping: {} has no 8.3 name",
            folders.scanned.path().display()
        )?;
        return Ok(());
    };
    let child = folders.cwd_child()?;
    let report = find_holders(&short, &[])?;
    ensure!(
        holds_current_folder(&report, child.pid()),
        "pid {} not found through {}: {report:?}",
        child.pid(),
        short.display()
    );
    Ok(())
}

#[test]
fn cwd_through_junction_matches_folder_as_given() -> Result<()> {
    let folders = Folders::new()?;
    let link = folders.side.path().join("link");
    if !make_junction(&link, folders.scanned.path())? {
        return Ok(());
    }
    let child = start_pwsh(
        "$null = 1",
        &link.join("sub folder"),
        &folders.ready("junction"),
    )?;
    let report = find_holders(&link, &[])?;
    ensure!(
        holds_current_folder(&report, child.pid()),
        "pid {} with its cwd under {} not found: {report:?}",
        child.pid(),
        link.display()
    );
    Ok(())
}

#[test]
fn excluded_pid_is_not_listed() -> Result<()> {
    let folders = Folders::new()?;
    let child = folders.cwd_child()?;
    let report = find_holders(folders.scanned.path(), &[child.pid()])?;
    ensure!(
        holder(&report, child.pid()).is_none(),
        "excluded pid {} listed: {report:?}",
        child.pid()
    );
    ensure!(
        report.may_hold.iter().all(|may| may.pid != child.pid()),
        "excluded pid {} listed as may_hold: {report:?}",
        child.pid()
    );
    Ok(())
}

/// A temporary folder under the repo's gitignored `.tmp/`, so it lies on the Dev Drive: on `C:` (where `%TEMP%`
/// is) a scanner holds a freshly written file for seconds, which would look like our scan blocking a delete.
fn repo_tmp_dir() -> Result<TempDir> {
    let tmp = Path::new(env!("CARGO_MANIFEST_DIR")).join(".tmp");
    fs::create_dir_all(&tmp).with_context(|| format!("cannot create {}", tmp.display()))?;
    tempfile::Builder::new()
        .prefix("churn ")
        .tempdir_in(&tmp)
        .with_context(|| format!("cannot make a temporary folder in {}", tmp.display()))
}

fn read_or_empty(path: &Path) -> Result<String> {
    match fs::read_to_string(path) {
        Ok(text) => Ok(text),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(String::new()),
        Err(error) => Err(error).with_context(|| format!("cannot read {}", path.display())),
    }
}

/// A pwsh child creates a share-none file, holds it 20 ms, closes it and deletes it, over and over, while the
/// folder is scanned five times. A duplicate our scan keeps open makes the delete fail; a delete is retried five
/// times 10 ms apart, so a duplicate closed within a millisecond never makes it fail, and one held across the
/// naming phase always does. Only a delete that failed every try is logged; first-try failures are counted.
#[test]
fn scan_does_not_block_another_process_deleting_its_files() -> Result<()> {
    let folders = Folders::new()?;
    let churn = repo_tmp_dir()?;
    let churned = churn.path().join("churn.tmp");
    let failures = folders.ready("failures.log");
    let first_try_failures = folders.ready("first-try-failures");
    let stop = folders.ready("stop");
    let done = folders.ready("done");
    let ready = folders.ready("churn-ready");
    let script = format!(
        "$p = {file}; $first = 0; Set-Content -LiteralPath {ready} ready; \
         while (-not (Test-Path -LiteralPath {stop})) {{ \
           try {{ \
             $f = [IO.File]::Open($p, 'CreateNew', 'ReadWrite', 'None'); Start-Sleep -Milliseconds 20; $f.Close(); \
             $try = 0; \
             while ($true) {{ \
               try {{ [IO.File]::Delete($p); break }} \
               catch {{ $try++; if ($try -eq 1) {{ $first++ }}; if ($try -ge 5) {{ throw }}; Start-Sleep -Milliseconds 10 }} \
             }} \
           }} \
           catch {{ Add-Content -LiteralPath {log} $_.Exception.Message; Start-Sleep -Milliseconds 10 }} \
         }}; Set-Content -LiteralPath {count} $first; Set-Content -LiteralPath {done} done; Start-Sleep 120",
        file = quoted(&churned),
        ready = quoted(&ready),
        stop = quoted(&stop),
        log = quoted(&failures),
        count = quoted(&first_try_failures),
        done = quoted(&done),
    );
    let _child = spawn_pwsh(&script, folders.side.path(), &ready)?;
    let mut timings = Vec::new();
    for _ in 0..5 {
        let started = Instant::now();
        find_holders(folders.scanned.path(), &[])?;
        timings.push(started.elapsed().as_millis());
    }
    fs::write(&stop, "stop")?;
    wait_for(&done)?;
    writeln!(
        std::io::stderr(),
        "find_holders took {timings:?} ms; first-try delete failures: {}",
        read_or_empty(&first_try_failures)?.trim()
    )?;
    let logged = read_or_empty(&failures)?;
    ensure!(
        logged.is_empty(),
        "the churning child failed while the folder was scanned:\n{logged}"
    );
    Ok(())
}

#[test]
fn own_process_is_never_listed() -> Result<()> {
    let folders = Folders::new()?;
    let bystander = start_pwsh(
        "$null = 1",
        folders.side.path(),
        &folders.ready("bystander"),
    )?;
    let report = find_holders(folders.scanned.path(), &[])?;
    let own = std::process::id();
    ensure!(
        holder(&report, own).is_none(),
        "own process (pid {own}) listed as a holder: {report:?}"
    );
    ensure!(
        report.may_hold.iter().all(|may| may.pid != own),
        "own process (pid {own}) listed as may_hold: {report:?}"
    );
    // An unnamed-handle entry is not about the folder, so any process may show up with one; only a holder or a
    // cannot-open entry would claim our own, always openable child uses the folder.
    let child = bystander.pid();
    ensure!(
        holder(&report, child).is_none(),
        "child outside the folder (pid {child}) listed as a holder: {report:?}"
    );
    ensure!(
        !report
            .may_hold
            .iter()
            .any(|may| may.pid == child && may.why == MayHoldWhy::CannotOpen),
        "child outside the folder (pid {child}) listed as may_hold cannot_open: {report:?}"
    );
    Ok(())
}

#[test]
fn still_same_detects_exit() -> Result<()> {
    let folders = Folders::new()?;
    let mut child = folders.cwd_child()?;
    let report = find_holders(folders.scanned.path(), &[])?;
    let found = holder(&report, child.pid())
        .cloned()
        .with_context(|| format!("pid {} not found: {report:?}", child.pid()))?;
    ensure!(still_same(&found), "live child reported as gone: {found:?}");
    child.stop()?;
    ensure!(
        !still_same(&found),
        "exited child reported as live: {found:?}"
    );
    Ok(())
}
