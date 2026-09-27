//! Clearing file locks that stop a removal.
//!
//! The unelevated side, [`offer`], asks once per run and then reruns this program through `sudo` as the hidden
//! `unlock` subcommand. The elevated side, [`run_elevated`], lists every open handle with Sysinternals `handle.exe`,
//! keeps the ones under the locked folders, and offers to stop each process holding them or to close its handles.

use std::collections::{HashMap, HashSet};
use std::ffi::{OsStr, OsString};
use std::hash::BuildHasher;
use std::io::{ErrorKind, Write};
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::{Path, PathBuf};
use std::process::{Command, Output};
use std::time::Duration;

use anyhow::{Context, Result, anyhow, bail};
use dialoguer::{Confirm, Select};
use tracing::warn;
use windows::Win32::Foundation::{HANDLE, WAIT_OBJECT_0};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::Threading::{
    OpenProcess, PROCESS_SYNCHRONIZE, PROCESS_TERMINATE, TerminateProcess, WaitForSingleObject,
};

use crate::handle_csv::{self, Locker};

/// Exit code of the elevated side: nothing holds files under the paths any more.
pub const EXIT_ALL_CLEAR: u8 = 0;
/// Exit code of the elevated side: some processes still hold files, after at least one was stopped or had its
/// handles closed.
pub const EXIT_SOME_LEFT: u8 = 3;
/// Exit code of the elevated side: processes still hold files and nothing was acted on.
pub const EXIT_NOTHING_DONE: u8 = 4;

const HANDLE_INSTALL: &str = "winget install Microsoft.Sysinternals.Handle";
const STOP_WAIT: Duration = Duration::from_secs(5);
const SHOWN_HANDLES: usize = 5;

/// What the unlock flow did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UnlockOutcome {
    /// Every lock on the paths was cleared; the removals can be retried.
    Unlocked,
    /// Some locks were cleared; the removals can be retried and some may still fail.
    PartlyUnlocked,
    /// The flow ran but no lock was cleared.
    StillLocked,
    /// The locks were left in place without trying.
    Skipped,
}

/// What to do about one process holding files under the locked folders.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LockerAction {
    /// Terminate the process.
    Stop,
    /// Close the process's handles under the locked folders.
    CloseHandles,
    /// Leave the process alone.
    Skip,
    /// Stop offering; end the session.
    Done,
}

const ACTIONS: [LockerAction; 4] = [
    LockerAction::Stop,
    LockerAction::CloseHandles,
    LockerAction::Skip,
    LockerAction::Done,
];

/// One process from a process snapshot.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessEntry {
    /// The parent process id.
    pub parent: u32,
    /// The image name, such as `sudo.exe`.
    pub exe: String,
}

/// How `sudo` is configured, as `sudo config` reports it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SudoMode {
    /// The elevated process shares the console, input included. The only mode the unlock flow can use.
    Inline,
    /// The elevated process runs in a new console window, and sudo may return before it ends.
    ForceNewWindow,
    /// The elevated process shares the console but gets no input.
    DisableInput,
    /// sudo is turned off.
    Disabled,
    /// Output this program does not recognise.
    Unknown,
}

impl SudoMode {
    /// The mode's name as a sentence shows it.
    #[must_use]
    pub fn label(self) -> &'static str {
        match self {
            Self::Inline => "Inline",
            Self::ForceNewWindow => "Force New Window",
            Self::DisableInput => "Disable Input",
            Self::Disabled => "Disabled",
            Self::Unknown => "an unrecognised",
        }
    }
}

/// Reads the mode from `sudo config` output such as `Sudo is currently in Inline mode on this machine`.
#[must_use]
pub fn parse_sudo_mode(text: &str) -> SudoMode {
    let text = text.to_lowercase();
    if text.contains("disabled") {
        SudoMode::Disabled
    } else if text.contains("inline mode") {
        SudoMode::Inline
    } else if text.contains("new window") {
        SudoMode::ForceNewWindow
    } else if text.contains("disable input") || text.contains("input closed") {
        SudoMode::DisableInput
    } else {
        SudoMode::Unknown
    }
}

/// Offers, once for the whole run, to find and clear the locks other processes hold on files under `paths`: prints
/// the paths, checks that `sudo` is in Inline mode, asks (default yes), and runs
/// `sudo <this program> unlock --caller-pid <parent> --sweep-pid <this pid> <paths>` with the console shared, so the
/// elevated prompts reach the user. When `sudo` is missing or in any other mode, prints the command to run by hand
/// in an administrator terminal and skips.
///
/// # Errors
///
/// When the terminal cannot be used, `sudo` cannot be started, or the elevated run fails.
pub fn offer(paths: &[PathBuf]) -> Result<UnlockOutcome> {
    let stdout = std::io::stdout();
    let mut out = stdout.lock();
    writeln!(out, "Another process holds files open under:")?;
    for path in paths {
        writeln!(out, "  {}", path.display())?;
    }
    out.flush()?;
    let exe = std::env::current_exe().context("cannot find the path of this program")?;
    match sudo_mode()? {
        Some(SudoMode::Inline) => {}
        Some(mode) => {
            writeln!(
                out,
                "sudo is set to {} mode; worktree-sweep needs inline mode (sudo config --enable normal).",
                mode.label()
            )?;
            write_manual_command(&exe, paths, &mut out)?;
            return Ok(UnlockOutcome::Skipped);
        }
        None => {
            writeln!(out, "sudo is not available on this machine.")?;
            write_manual_command(&exe, paths, &mut out)?;
            return Ok(UnlockOutcome::Skipped);
        }
    }
    let proceed = Confirm::new()
        .with_prompt("Run an elevated scan with sudo to find what holds them?")
        .default(true)
        .interact()?;
    if !proceed {
        return Ok(UnlockOutcome::Skipped);
    }
    let argv = sudo_argv(&exe, own_parent_pid(), std::process::id(), paths);
    let status = Command::new("sudo")
        .args(&argv)
        .status()
        .context("cannot run sudo")?;
    outcome_for_exit(status.code()).ok_or_else(|| {
        anyhow!(
            "the elevated scan failed ({status}); to retry, run in an administrator terminal: {}",
            manual_command(&exe, paths)
        )
    })
}

/// Maps the elevated side's exit code to an outcome: [`EXIT_ALL_CLEAR`] to [`UnlockOutcome::Unlocked`],
/// [`EXIT_SOME_LEFT`] to [`UnlockOutcome::PartlyUnlocked`], [`EXIT_NOTHING_DONE`] to [`UnlockOutcome::StillLocked`];
/// `None` for anything else, including no code at all.
#[must_use]
pub fn outcome_for_exit(code: Option<i32>) -> Option<UnlockOutcome> {
    let code = u8::try_from(code?).ok()?;
    match code {
        EXIT_ALL_CLEAR => Some(UnlockOutcome::Unlocked),
        EXIT_SOME_LEFT => Some(UnlockOutcome::PartlyUnlocked),
        EXIT_NOTHING_DONE => Some(UnlockOutcome::StillLocked),
        _ => None,
    }
}

/// The arguments to give `sudo`: `<exe> unlock [--caller-pid <pid>] --sweep-pid <pid> <paths>`, every path with `\`
/// separators. `sweep_pid` is the unelevated worktree-sweep, which the elevated side must never offer to stop.
#[must_use]
pub fn sudo_argv(
    exe: &Path,
    caller_pid: Option<u32>,
    sweep_pid: u32,
    paths: &[PathBuf],
) -> Vec<OsString> {
    let mut argv = vec![exe.as_os_str().to_owned(), OsString::from("unlock")];
    if let Some(pid) = caller_pid {
        argv.push(OsString::from("--caller-pid"));
        argv.push(OsString::from(pid.to_string()));
    }
    argv.push(OsString::from("--sweep-pid"));
    argv.push(OsString::from(sweep_pid.to_string()));
    argv.extend(paths.iter().map(|path| backslashed(path.as_os_str())));
    argv
}

fn backslashed(path: &OsStr) -> OsString {
    let wide: Vec<u16> = path
        .encode_wide()
        .map(|unit| {
            if unit == u16::from(b'/') {
                u16::from(b'\\')
            } else {
                unit
            }
        })
        .collect();
    OsString::from_wide(&wide)
}

fn manual_command(exe: &Path, paths: &[PathBuf]) -> String {
    let quoted: Vec<String> = paths
        .iter()
        .map(|path| {
            format!(
                "\"{}\"",
                PathBuf::from(backslashed(path.as_os_str())).display()
            )
        })
        .collect();
    format!("\"{}\" unlock {}", exe.display(), quoted.join(" "))
}

fn write_manual_command(exe: &Path, paths: &[PathBuf], out: &mut impl Write) -> Result<()> {
    writeln!(
        out,
        "To find and clear the locks, run this in an administrator terminal, then run worktree-sweep again:"
    )?;
    writeln!(out, "  {}", manual_command(exe, paths))?;
    out.flush()?;
    Ok(())
}

/// The mode `sudo config` reports; `None` when `sudo` is not installed.
fn sudo_mode() -> Result<Option<SudoMode>> {
    match Command::new("sudo").arg("config").output() {
        Ok(output) => Ok(Some(parse_sudo_mode(&format!(
            "{}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        )))),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error).context("cannot run `sudo config`"),
    }
}

fn own_parent_pid() -> Option<u32> {
    match process_table() {
        Ok(table) => table.get(&std::process::id()).map(|entry| entry.parent),
        Err(error) => {
            warn!("cannot find the process that started worktree-sweep: {error:#}");
            None
        }
    }
}

/// The elevated side: finds the processes holding files under `paths` and offers, for each, to stop it, close its
/// handles there, skip it, or end the session. After acting it scans again and offers what is left, until nothing
/// holds files there or the user picks Done. `caller_pid` is the process that started the unelevated run, usually
/// the user's shell; it is marked, and stopping it is not the default. `sweep_pid` is the unelevated worktree-sweep;
/// it and its children are never offered.
///
/// Returns the exit code: [`EXIT_ALL_CLEAR`], [`EXIT_SOME_LEFT`] or [`EXIT_NOTHING_DONE`].
///
/// # Errors
///
/// When `handle.exe` is missing or fails, processes cannot be listed, or the terminal cannot be used.
pub fn run_elevated(
    paths: &[PathBuf],
    caller_pid: Option<u32>,
    sweep_pid: Option<u32>,
) -> Result<u8> {
    let excluded = excluded_pids(std::process::id(), sweep_pid, &process_table()?);
    let stdout = std::io::stdout();
    let mut out = stdout.lock();
    let mut acted = false;
    let mut finished = false;
    loop {
        writeln!(out, "Scanning open handles…")?;
        out.flush()?;
        let lockers = find_lockers(paths, &excluded)?;
        if lockers.is_empty() {
            writeln!(out, "Nothing holds files under those folders.")?;
            out.flush()?;
            return Ok(exit_code(true, acted));
        }
        if finished {
            writeln!(
                out,
                "{} process(es) still hold files under those folders.",
                lockers.len()
            )?;
            out.flush()?;
            return Ok(exit_code(false, acted));
        }
        let round = offer_round(&lockers, caller_pid, &mut out)?;
        acted |= round.acted;
        finished = round.done || !round.acted;
    }
}

/// The elevated side's exit code once the session ends.
fn exit_code(clear: bool, acted: bool) -> u8 {
    match (clear, acted) {
        (true, _) => EXIT_ALL_CLEAR,
        (false, true) => EXIT_SOME_LEFT,
        (false, false) => EXIT_NOTHING_DONE,
    }
}

/// The processes excluded from the offer: this one and its parents up to and including `sudo.exe` (only this one
/// when no `sudo.exe` is among the parents), plus the unelevated worktree-sweep `sweep_pid` and its children, such
/// as the `sudo.exe` it started.
#[must_use]
pub fn excluded_pids<S: BuildHasher>(
    own_pid: u32,
    sweep_pid: Option<u32>,
    table: &HashMap<u32, ProcessEntry, S>,
) -> HashSet<u32> {
    let mut excluded = parent_chain(own_pid, table);
    if let Some(sweep) = sweep_pid {
        excluded.insert(sweep);
        excluded.extend(
            table
                .iter()
                .filter(|(_, entry)| entry.parent == sweep)
                .map(|(&pid, _)| pid),
        );
    }
    excluded
}

/// Whether `pid` is still a process named `process` (compared case-insensitively), so a pid reused by another
/// program since the scan is never acted on.
#[must_use]
pub fn is_same_process<S: BuildHasher>(
    pid: u32,
    process: &str,
    table: &HashMap<u32, ProcessEntry, S>,
) -> bool {
    table
        .get(&pid)
        .is_some_and(|entry| entry.exe.to_lowercase() == process.to_lowercase())
}

fn still_running(locker: &Locker) -> bool {
    match process_table() {
        Ok(table) => is_same_process(locker.pid, &locker.process, &table),
        Err(error) => {
            warn!(
                "cannot check that pid {} is still {}: {error:#}",
                locker.pid, locker.process
            );
            false
        }
    }
}

fn parent_chain<S: BuildHasher>(
    own_pid: u32,
    table: &HashMap<u32, ProcessEntry, S>,
) -> HashSet<u32> {
    let mut chain = HashSet::from([own_pid]);
    let mut current = own_pid;
    while let Some(entry) = table.get(&current) {
        let parent = entry.parent;
        let Some(parent_entry) = table.get(&parent) else {
            break;
        };
        if !chain.insert(parent) {
            break;
        }
        if parent_entry.exe.eq_ignore_ascii_case("sudo.exe") {
            return chain;
        }
        current = parent;
    }
    HashSet::from([own_pid])
}

/// Whether a handle's object `name` is one of `paths` or lies under one. Paths compare case-insensitively with `\`
/// separators, without a `\\?\` prefix or a trailing `\`; `D:\a\x` does not cover `D:\a\xy`.
#[must_use]
pub fn matches_locked_path(name: &Path, paths: &[PathBuf]) -> bool {
    let name = comparable(name);
    paths.iter().any(|path| {
        let path = comparable(path);
        name == path
            || name
                .strip_prefix(&path)
                .is_some_and(|rest| rest.starts_with('\\'))
    })
}

fn comparable(path: &Path) -> String {
    let text = path.to_string_lossy().replace('/', "\\");
    let text = text.strip_prefix(r"\\?\").unwrap_or(&text);
    text.trim_end_matches('\\').to_lowercase()
}

/// The action offered first for a process: Skip for the caller, whose stopping would close the user's shell, and
/// Stop for any other.
#[must_use]
pub fn default_action(is_caller: bool) -> LockerAction {
    if is_caller {
        LockerAction::Skip
    } else {
        LockerAction::Stop
    }
}

fn find_lockers(paths: &[PathBuf], excluded: &HashSet<u32>) -> Result<Vec<Locker>> {
    let dump = dump_handles()?;
    let rows = handle_csv::parse(&dump)?
        .into_iter()
        .filter(|row| !excluded.contains(&row.pid) && matches_locked_path(&row.name, paths))
        .collect();
    Ok(handle_csv::group_by_process(rows))
}

fn run_handle_exe(args: &[&str]) -> Result<Output> {
    Command::new("handle.exe")
        .args(args)
        .output()
        .map_err(|error| {
            if error.kind() == ErrorKind::NotFound {
                anyhow!("handle.exe is not on PATH; install it with `{HANDLE_INSTALL}`")
            } else {
                anyhow!(error).context("cannot run handle.exe")
            }
        })
}

/// Every open file handle on the system, as `handle.exe -nobanner -accepteula -v` prints them.
fn dump_handles() -> Result<String> {
    let output = run_handle_exe(&["-nobanner", "-accepteula", "-v"])?;
    let stdout = String::from_utf8_lossy(&output.stdout).into_owned();
    if output.status.success() || stdout.contains(handle_csv::NO_MATCH) {
        return Ok(stdout);
    }
    bail!(
        "handle.exe failed ({}): {} {}",
        output.status,
        stdout.trim(),
        String::from_utf8_lossy(&output.stderr).trim()
    )
}

struct Round {
    acted: bool,
    done: bool,
}

fn exited_line(label: &str) -> String {
    format!("{label} has exited; skipped.")
}

/// Stops the locker unless its pid no longer names the same program; returns the summary line and whether it
/// stopped.
fn stop_locker(locker: &Locker, label: &str) -> (String, bool) {
    if !still_running(locker) {
        return (exited_line(label), false);
    }
    match stop_process(locker.pid, STOP_WAIT) {
        Ok(()) => (format!("Stopped {label}."), true),
        Err(error) => (format!("Could not stop {label}: {error:#}"), false),
    }
}

fn offer_round(lockers: &[Locker], caller_pid: Option<u32>, out: &mut impl Write) -> Result<Round> {
    let mut round = Round {
        acted: false,
        done: false,
    };
    for locker in lockers {
        let is_caller = caller_pid == Some(locker.pid);
        describe_locker(locker, is_caller, out)?;
        let label = format!("{} (pid {})", locker.process, locker.pid);
        let line = match choose_action(&label, is_caller)? {
            LockerAction::Stop => {
                let (line, stopped) = stop_locker(locker, &label);
                round.acted |= stopped;
                line
            }
            LockerAction::CloseHandles => {
                let (line, closed_any) = close_handles(locker, &label)?;
                round.acted |= closed_any;
                line
            }
            LockerAction::Skip => format!("Skipped {label}."),
            LockerAction::Done => {
                round.done = true;
                writeln!(out, "Done; leaving the rest alone.")?;
                break;
            }
        };
        writeln!(out, "{line}")?;
        out.flush()?;
    }
    Ok(round)
}

fn describe_locker(locker: &Locker, is_caller: bool, out: &mut impl Write) -> Result<()> {
    writeln!(
        out,
        "{} (pid {}) holds {} handle(s) there:",
        locker.process,
        locker.pid,
        locker.handles.len()
    )?;
    for held in locker.handles.iter().take(SHOWN_HANDLES) {
        writeln!(out, "  {:<8} {}", held.kind, held.name.display())?;
    }
    if locker.handles.len() > SHOWN_HANDLES {
        writeln!(out, "  … and {} more", locker.handles.len() - SHOWN_HANDLES)?;
    }
    if is_caller {
        writeln!(
            out,
            "  This is the shell you started worktree-sweep from; stopping it closes that shell."
        )?;
    }
    out.flush()?;
    Ok(())
}

fn choose_action(label: &str, is_caller: bool) -> Result<LockerAction> {
    let stop = if is_caller {
        "Stop process (closes your shell)"
    } else {
        "Stop process"
    };
    let items = [
        stop,
        "Close its handles in this folder",
        "Skip",
        "Done (leave the rest alone)",
    ];
    let default = ACTIONS
        .iter()
        .position(|&action| action == default_action(is_caller))
        .unwrap_or_default();
    let chosen = Select::new()
        .with_prompt(format!("What should happen to {label}?"))
        .items(items)
        .default(default)
        .interact()?;
    Ok(ACTIONS.get(chosen).copied().unwrap_or(LockerAction::Skip))
}

/// Closes the locker's handles after a warning (default no); returns the summary line and whether any closed.
fn close_handles(locker: &Locker, label: &str) -> Result<(String, bool)> {
    let proceed = Confirm::new()
        .with_prompt(format!(
            "Closing handles behind a program's back can make it crash or lose data. Close {} handle(s) of {label}?",
            locker.handles.len()
        ))
        .default(false)
        .interact()?;
    if !proceed {
        return Ok((format!("Left {label} alone."), false));
    }
    if !still_running(locker) {
        return Ok((exited_line(label), false));
    }
    let pid = locker.pid.to_string();
    let mut closed = 0_usize;
    for held in &locker.handles {
        let hex = format!("{:X}", held.handle);
        match run_handle_exe(&["-nobanner", "-c", &hex, "-p", &pid, "-y"]) {
            Ok(output) if output.status.success() => closed += 1,
            Ok(output) => warn!(
                "handle.exe could not close {} handle {hex} ({}) of {label}: {}",
                held.kind,
                held.name.display(),
                String::from_utf8_lossy(&output.stdout).trim()
            ),
            Err(error) => warn!("cannot close handle {hex} of {label}: {error:#}"),
        }
    }
    Ok((
        format!(
            "Closed {closed} of {} handle(s) of {label}.",
            locker.handles.len()
        ),
        closed > 0,
    ))
}

/// Terminates process `pid` with exit code 1 and waits up to `wait` for it to exit.
///
/// # Errors
///
/// When the process cannot be opened or terminated, or has not exited within `wait`.
pub fn stop_process(pid: u32, wait: Duration) -> Result<()> {
    // SAFETY: a plain call with no pointers; the handle it returns is owned right below.
    let raw = unsafe { OpenProcess(PROCESS_TERMINATE | PROCESS_SYNCHRONIZE, false, pid) }
        .with_context(|| format!("cannot open process {pid}"))?;
    // SAFETY: `raw` is a valid process handle nothing else owns; `OwnedHandle` closes it once.
    let owned = unsafe { OwnedHandle::from_raw_handle(raw.0) };
    let handle = HANDLE(owned.as_raw_handle());
    // SAFETY: `handle` is open with PROCESS_TERMINATE and outlives the call.
    unsafe { TerminateProcess(handle, 1) }.with_context(|| format!("cannot stop process {pid}"))?;
    let millis = u32::try_from(wait.as_millis()).unwrap_or(u32::MAX);
    // SAFETY: `handle` is open with PROCESS_SYNCHRONIZE and outlives the call.
    let waited = unsafe { WaitForSingleObject(handle, millis) };
    if waited == WAIT_OBJECT_0 {
        Ok(())
    } else {
        bail!(
            "process {pid} did not exit within {} s of being stopped",
            wait.as_secs()
        )
    }
}

/// Every running process's parent and image name, by pid.
///
/// # Errors
///
/// When the process snapshot cannot be taken.
pub fn process_table() -> Result<HashMap<u32, ProcessEntry>> {
    // SAFETY: a plain call with no pointers; the handle it returns is owned right below.
    let raw = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }
        .context("cannot take a snapshot of the running processes")?;
    // SAFETY: `raw` is a valid snapshot handle nothing else owns; `OwnedHandle` closes it once.
    let owned = unsafe { OwnedHandle::from_raw_handle(raw.0) };
    let snapshot = HANDLE(owned.as_raw_handle());
    let mut entry = PROCESSENTRY32W {
        dwSize: u32::try_from(size_of::<PROCESSENTRY32W>()).context("PROCESSENTRY32W size")?,
        ..PROCESSENTRY32W::default()
    };
    let mut table = HashMap::new();
    // SAFETY: `snapshot` is open and `entry` is a live, correctly sized PROCESSENTRY32W.
    let mut more = unsafe { Process32FirstW(snapshot, &raw mut entry) }.is_ok();
    while more {
        table.insert(
            entry.th32ProcessID,
            ProcessEntry {
                parent: entry.th32ParentProcessID,
                exe: from_wide_nul(&entry.szExeFile),
            },
        );
        // SAFETY: as for Process32FirstW above.
        more = unsafe { Process32NextW(snapshot, &raw mut entry) }.is_ok();
    }
    Ok(table)
}

fn from_wide_nul(wide: &[u16]) -> String {
    let len = wide
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(wide.len());
    OsString::from_wide(&wide[..len])
        .to_string_lossy()
        .into_owned()
}

#[cfg(test)]
mod tests {
    use anyhow::ensure;

    use super::*;

    #[test]
    fn exit_code_maps_to_outcome() -> Result<()> {
        ensure!(outcome_for_exit(Some(0)) == Some(UnlockOutcome::Unlocked));
        ensure!(outcome_for_exit(Some(3)) == Some(UnlockOutcome::PartlyUnlocked));
        ensure!(outcome_for_exit(Some(4)) == Some(UnlockOutcome::StillLocked));
        for other in [
            Some(1),
            Some(2),
            Some(5),
            Some(-1),
            Some(259),
            Some(1223),
            None,
        ] {
            ensure!(outcome_for_exit(other).is_none(), "{other:?}");
        }
        let round_trip = |clear, acted| outcome_for_exit(Some(i32::from(exit_code(clear, acted))));
        ensure!(round_trip(true, false) == Some(UnlockOutcome::Unlocked));
        ensure!(round_trip(true, true) == Some(UnlockOutcome::Unlocked));
        ensure!(round_trip(false, true) == Some(UnlockOutcome::PartlyUnlocked));
        ensure!(round_trip(false, false) == Some(UnlockOutcome::StillLocked));
        Ok(())
    }

    #[test]
    fn caller_process_is_not_stopped_by_default() -> Result<()> {
        ensure!(default_action(true) == LockerAction::Skip);
        ensure!(default_action(false) == LockerAction::Stop);
        Ok(())
    }

    #[test]
    fn sudo_argv_passes_caller_pid_and_backslash_paths() -> Result<()> {
        let exe = Path::new(r"C:\tools\worktree-sweep.exe");
        let paths = [PathBuf::from("D:/a.wt/x"), PathBuf::from(r"D:\b")];
        let argv = sudo_argv(exe, Some(4242), 5150, &paths);
        let expected: Vec<OsString> = [
            r"C:\tools\worktree-sweep.exe",
            "unlock",
            "--caller-pid",
            "4242",
            "--sweep-pid",
            "5150",
            r"D:\a.wt\x",
            r"D:\b",
        ]
        .iter()
        .map(OsString::from)
        .collect();
        ensure!(argv == expected, "{argv:?}");
        let without = sudo_argv(exe, None, 5150, &paths);
        ensure!(
            !without.contains(&OsString::from("--caller-pid")),
            "{without:?}"
        );
        Ok(())
    }

    fn entry(parent: u32, exe: &str) -> ProcessEntry {
        ProcessEntry {
            parent,
            exe: exe.to_owned(),
        }
    }

    #[test]
    fn own_parent_chain_is_excluded() -> Result<()> {
        let table = HashMap::from([
            (1, entry(0, "explorer.exe")),
            (2, entry(1, "pwsh.exe")),
            (3, entry(2, "worktree-sweep.exe")),
            (4, entry(3, "sudo.exe")),
            (5, entry(700, "sudo.exe")),
            (6, entry(5, "conhost.exe")),
            (7, entry(6, "worktree-sweep.exe")),
            (8, entry(2, "code.exe")),
        ]);
        ensure!(excluded_pids(7, None, &table) == HashSet::from([7, 6, 5]));
        let unelevated = excluded_pids(3, None, &table);
        ensure!(unelevated == HashSet::from([3]), "{unelevated:?}");
        let cycle = HashMap::from([(10, entry(11, "a.exe")), (11, entry(10, "b.exe"))]);
        ensure!(excluded_pids(10, None, &cycle) == HashSet::from([10]));
        ensure!(excluded_pids(99, None, &HashMap::new()) == HashSet::from([99]));
        Ok(())
    }

    #[test]
    fn sweep_process_and_its_children_are_excluded() -> Result<()> {
        let table = HashMap::from([
            (2, entry(1, "pwsh.exe")),
            (3, entry(2, "worktree-sweep.exe")),
            (4, entry(3, "sudo.exe")),
            (5, entry(700, "sudo.exe")),
            (7, entry(5, "worktree-sweep.exe")),
            (8, entry(2, "code.exe")),
            (9, entry(4, "conhost.exe")),
        ]);
        let excluded = excluded_pids(7, Some(3), &table);
        ensure!(excluded == HashSet::from([7, 5, 3, 4]), "{excluded:?}");
        let gone = excluded_pids(7, Some(42), &table);
        ensure!(gone == HashSet::from([7, 5, 42]), "{gone:?}");
        Ok(())
    }

    #[test]
    fn reused_pid_is_not_the_same_process() -> Result<()> {
        let table = HashMap::from([(10, entry(1, "pwsh.exe")), (11, entry(1, "notepad.exe"))]);
        ensure!(is_same_process(10, "pwsh.exe", &table));
        ensure!(is_same_process(10, "PWSH.EXE", &table));
        ensure!(!is_same_process(11, "pwsh.exe", &table));
        ensure!(!is_same_process(12, "pwsh.exe", &table));
        Ok(())
    }

    #[test]
    fn sudo_mode_is_read_from_sudo_config() -> Result<()> {
        let cases = [
            (
                "Sudo is currently in Inline mode on this machine\r\n",
                SudoMode::Inline,
            ),
            (
                "Sudo is currently in Force New Window mode on this machine\r\n",
                SudoMode::ForceNewWindow,
            ),
            (
                "Sudo is currently in Disable Input mode on this machine\r\n",
                SudoMode::DisableInput,
            ),
            (
                "Sudo is disabled on this machine. To enable it, go to the Developer Settings page in the Settings app",
                SudoMode::Disabled,
            ),
            ("", SudoMode::Unknown),
            ("something else entirely", SudoMode::Unknown),
        ];
        for (text, mode) in cases {
            ensure!(parse_sudo_mode(text) == mode, "{text:?}");
        }
        Ok(())
    }

    #[test]
    fn row_filter_matches_path_and_children_only() -> Result<()> {
        let paths = [PathBuf::from(r"D:\a.wt\x"), PathBuf::from("D:/other/")];
        let matches = |name: &str| matches_locked_path(Path::new(name), &paths);
        ensure!(matches(r"D:\a.wt\x"));
        ensure!(matches(r"D:\a.wt\x\"));
        ensure!(matches(r"D:\a.wt\x\held.txt"));
        ensure!(matches(r"d:\A.WT\X\Sub\file.rs"));
        ensure!(matches(r"\\?\D:\a.wt\x\f"));
        ensure!(matches(r"D:\other\f"));
        ensure!(!matches(r"D:\a.wt\xy"));
        ensure!(!matches(r"D:\a.wt\xy\f"));
        ensure!(!matches(r"D:\a.wt"));
        ensure!(!matches(r"E:\a.wt\x"));
        ensure!(!matches(r"D:\otherwise"));
        Ok(())
    }

    #[test]
    fn stop_process_terminates_a_child() -> Result<()> {
        let mut child = Command::new("pwsh")
            .args(["-NoProfile", "-Command", "Start-Sleep 60"])
            .spawn()
            .context("cannot start pwsh")?;
        let stopped = stop_process(child.id(), STOP_WAIT);
        let exited = child.try_wait()?;
        if exited.is_none() {
            child.kill()?;
        }
        stopped?;
        ensure!(
            exited.is_some_and(|status| status.code() == Some(1)),
            "{exited:?}"
        );
        Ok(())
    }
}
