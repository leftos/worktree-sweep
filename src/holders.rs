//! Finding which processes hold a folder, without elevation.
//!
//! [`find_holders`] looks at every process running as the current user: its current folder, read from its PEB, and
//! its open disk handles, named on worker threads with a timeout because naming a handle can block. A process that
//! cannot be inspected, or whose handle cannot be named, is reported as one that may hold the folder.

use std::collections::{BTreeSet, HashMap, HashSet};
use std::ffi::{OsStr, OsString, c_void};
use std::fs::{self, File};
use std::hash::BuildHasher;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use anyhow::{Context, Result, anyhow, bail};
use serde::Serialize;
use tracing::{debug, warn};
use windows::Wdk::Storage::FileSystem::{
    FileProcessIdsUsingFileInformation, NtQueryInformationFile,
};
use windows::Wdk::System::Threading::{
    NtQueryInformationProcess, PROCESSINFOCLASS, ProcessBasicInformation, ProcessHandleInformation,
    ProcessWow64Information,
};
use windows::Win32::Foundation::{
    DUPLICATE_SAME_ACCESS, DuplicateHandle, FILETIME, HANDLE, STATUS_BUFFER_OVERFLOW,
    STATUS_BUFFER_TOO_SMALL, STATUS_INFO_LENGTH_MISMATCH, WAIT_OBJECT_0,
};
use windows::Win32::Security::{EqualSid, GetTokenInformation, TOKEN_QUERY, TOKEN_USER, TokenUser};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAG_BACKUP_SEMANTICS, FILE_NAME_NORMALIZED, FILE_READ_ATTRIBUTES,
    FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, FILE_TYPE_DISK, GetFileType,
    GetFinalPathNameByHandleW, GetLongPathNameW, OPEN_EXISTING,
};
use windows::Win32::System::Diagnostics::Debug::ReadProcessMemory;
use windows::Win32::System::IO::IO_STATUS_BLOCK;
use windows::Win32::System::Threading::{
    GetCurrentProcess, GetProcessTimes, OpenProcess, OpenProcessToken, PROCESS_ACCESS_RIGHTS,
    PROCESS_DUP_HANDLE, PROCESS_NAME_WIN32, PROCESS_QUERY_INFORMATION,
    PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, PROCESS_VM_READ,
    QueryFullProcessImageNameW, WaitForSingleObject,
};
use windows::core::{PCWSTR, PWSTR};

use crate::unlock::{self, matches_locked_path};

const _: () = assert!(
    size_of::<usize>() == 8,
    "the PEB offsets below are for a 64-bit process"
);

/// How long one handle's name lookup may take before the handle counts as unnamed.
const NAME_TIMEOUT: Duration = Duration::from_millis(200);
/// Threads naming handles at once.
const NAME_WORKERS: usize = 4;
/// The idle and System processes, which are never inspected.
const SYSTEM_PIDS: [u32; 2] = [0, 4];
/// `PEB.ProcessParameters` on x64.
const PEB_PROCESS_PARAMETERS: usize = 0x20;
/// `RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath` on x64.
const PARAMS_CURRENT_DIRECTORY: usize = 0x38;
/// `RTL_USER_PROCESS_PARAMETERS.CommandLine` on x64: 16 bytes and ten pointers, then `ImagePathName`, then this.
const PARAMS_COMMAND_LINE: usize = 0x70;
/// `PEB32.ProcessParameters` of a WOW64 process.
const PEB32_PROCESS_PARAMETERS: usize = 0x10;
/// `RTL_USER_PROCESS_PARAMETERS32.CurrentDirectory.DosPath` of a WOW64 process.
const PARAMS32_CURRENT_DIRECTORY: usize = 0x24;
/// Header of a `PROCESS_HANDLE_SNAPSHOT_INFORMATION`: the handle count and a reserved word.
const HANDLE_LIST_HEADER: usize = 16;
/// One `PROCESS_HANDLE_TABLE_ENTRY_INFO`.
const HANDLE_ENTRY_SIZE: usize = 40;
/// `PROCESS_HANDLE_TABLE_ENTRY_INFO.ObjectTypeIndex`.
const HANDLE_ENTRY_TYPE: usize = 28;

/// A process that holds something inside the folder.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Holder {
    /// The process id.
    pub pid: u32,
    /// The image name, such as `cargo.exe`.
    pub exe: String,
    /// The full image path, when it could be read.
    pub image: Option<PathBuf>,
    /// The creation time as a FILETIME count, or 0 when it could not be read.
    pub started: u64,
    /// The command line, when it could be read; never read for a 32-bit process.
    pub command_line: Option<String>,
    /// What the process holds inside the folder.
    pub holds: Vec<Hold>,
}

/// One thing a process holds inside the folder.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Hold {
    /// The process's current folder is the folder or lies under it.
    CurrentFolder {
        /// The current folder, with 8.3 names expanded.
        path: PathBuf,
    },
    /// The process has a file or folder open there.
    OpenHandle {
        /// The open file or folder.
        path: PathBuf,
    },
}

/// A process that may hold the folder, but could not be fully inspected.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct MayHold {
    /// The process id.
    pub pid: u32,
    /// The image name.
    pub exe: String,
    /// Why it could not be ruled out.
    pub why: MayHoldWhy,
}

/// Why a process may hold the folder.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum MayHoldWhy {
    /// One of its disk handles could not be named in time.
    UnnamedHandle,
    /// It uses the folder, but it cannot be opened to see how.
    CannotOpen,
}

/// What [`find_holders`] found.
#[derive(Debug, Default, Serialize)]
pub struct HolderReport {
    /// Processes that hold something inside the folder, by pid.
    pub holders: Vec<Holder>,
    /// Processes that may hold it, by pid.
    pub may_hold: Vec<MayHold>,
}

/// One process from a snapshot, with its creation time.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TimedProcess {
    /// The parent process id.
    pub parent: u32,
    /// The image name.
    pub exe: String,
    /// The creation time as a FILETIME count, when it could be read.
    pub started: Option<u64>,
}

/// Every running process by pid, with its parent and creation time.
pub type ProcessTimes = HashMap<u32, TimedProcess>;

/// Lists the current user's processes that hold `folder` or anything under it, other than this process and the
/// processes in `exclude`.
///
/// # Errors
///
/// When `folder` cannot be resolved, the process list cannot be read, or this process cannot read its own user.
pub fn find_holders(folder: &Path, exclude: &[u32]) -> Result<HolderReport> {
    let roots = folder_forms(folder)?;
    let table = unlock::process_table()?;
    let own_user = token_user(current_process()).context("cannot read this process's user")?;
    let file_type = file_type_index()?;
    let own_pid = std::process::id();
    let mut census = Census::default();
    for (&pid, entry) in &table {
        if SYSTEM_PIDS.contains(&pid) || pid == own_pid || exclude.contains(&pid) {
            continue;
        }
        census.inspect(pid, &entry.exe, &own_user, &roots, file_type);
    }
    let handles = std::mem::take(&mut census.handles);
    let mut unnamed = BTreeSet::new();
    for (pid, name) in name_handles(handles)? {
        match name {
            Some(path) if matches_locked_path(&path, &roots) => {
                census.add_hold(
                    pid,
                    Hold::OpenHandle {
                        path: without_verbatim_prefix(path),
                    },
                );
            }
            Some(_) => {}
            None => {
                unnamed.insert(pid);
            }
        }
    }
    let using = pids_using(&roots[0]).unwrap_or_else(|error| {
        warn!(
            "cannot list the processes using {}: {error:#}",
            roots[0].display()
        );
        Vec::new()
    });
    Ok(census.into_report(&table, &unnamed, &using, exclude))
}

/// Every spelling of `folder` a holder's path may use: absolute as given (a process's current folder keeps the
/// subst drive or junction it was set through), with 8.3 names expanded, and fully resolved (handle names are). The
/// resolved form comes first.
fn folder_forms(folder: &Path) -> Result<Vec<PathBuf>> {
    let canonical =
        fs::canonicalize(folder).with_context(|| format!("cannot resolve {}", folder.display()))?;
    let absolute = std::path::absolute(folder)
        .with_context(|| format!("cannot make {} absolute", folder.display()))?;
    let long = long_path(&absolute.as_os_str().encode_wide().collect::<Vec<u16>>());
    let mut forms = vec![canonical];
    for form in [absolute, long] {
        if !forms.contains(&form) {
            forms.push(form);
        }
    }
    Ok(forms)
}

/// The holders an agent may stop: those holding something whose program is a build server or language server that
/// restarts on its own (`rust-analyzer*.exe`, `cargo.exe`, `MSBuild.exe`, `VBCSCompiler.exe`, and `dotnet.exe`
/// running an `MSBuild` node or the compiler server).
#[must_use]
pub fn stoppable(report: &HolderReport) -> Vec<&Holder> {
    report
        .holders
        .iter()
        .filter(|holder| !holder.holds.is_empty() && allowlisted(holder))
        .collect()
}

fn allowlisted(holder: &Holder) -> bool {
    let exe = holder.exe.to_lowercase();
    let is_exe = Path::new(&exe)
        .extension()
        .is_some_and(|extension| extension.eq_ignore_ascii_case("exe"));
    if exe.starts_with("rust-analyzer") && is_exe {
        return true;
    }
    if ["cargo.exe", "msbuild.exe", "vbcscompiler.exe"].contains(&exe.as_str()) {
        return true;
    }
    if exe != "dotnet.exe" {
        return false;
    }
    let Some(command_line) = &holder.command_line else {
        return false;
    };
    let command_line = command_line.to_lowercase();
    let msbuild_node = command_line.contains("msbuild.dll")
        && (command_line.contains("/nodemode") || command_line.contains("-nodemode"));
    msbuild_node || command_line.contains("vbcscompiler.dll")
}

/// The chain of processes from `pid` up through its parents, `pid` first. A parent is followed only when it was
/// created before its child, so a reused pid ends the chain, as do a missing entry and a cycle.
#[must_use]
pub fn ancestors<S: BuildHasher>(pid: u32, table: &HashMap<u32, TimedProcess, S>) -> Vec<u32> {
    let mut chain = vec![pid];
    let mut current = pid;
    while let Some(child) = table.get(&current) {
        let parent = child.parent;
        let Some(parent_entry) = table.get(&parent) else {
            break;
        };
        let older = matches!(
            (parent_entry.started, child.started),
            (Some(parent_started), Some(child_started)) if parent_started < child_started
        );
        if !older || chain.contains(&parent) {
            break;
        }
        chain.push(parent);
        current = parent;
    }
    chain
}

/// Every running process's parent, image name and creation time, by pid.
///
/// # Errors
///
/// When the process snapshot cannot be taken.
pub fn process_times() -> Result<ProcessTimes> {
    Ok(unlock::process_table()?
        .into_iter()
        .map(|(pid, entry)| {
            let started = open_process(pid, PROCESS_QUERY_LIMITED_INFORMATION)
                .and_then(|process| creation_time(raw(&process)));
            (
                pid,
                TimedProcess {
                    parent: entry.parent,
                    exe: entry.exe,
                    started,
                },
            )
        })
        .collect())
}

/// Whether `holder.pid` still names the same running process: same creation time and same image. False when that
/// cannot be checked.
#[must_use]
pub fn still_same(holder: &Holder) -> bool {
    if holder.started == 0 {
        return false;
    }
    let Some(process) = open_process(
        holder.pid,
        PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
    ) else {
        return false;
    };
    // SAFETY: `process` is open with PROCESS_SYNCHRONIZE; a zero timeout only polls.
    let exited = unsafe { WaitForSingleObject(raw(&process), 0) } == WAIT_OBJECT_0;
    !exited
        && creation_time(raw(&process)) == Some(holder.started)
        && image_path(raw(&process)) == holder.image
}

/// What the census learnt, before the handles are named.
#[derive(Default)]
struct Census {
    /// Same-user processes, each with what it was found holding so far.
    candidates: HashMap<u32, Holder>,
    /// Processes that could not be opened, or not fully inspected.
    unopened: HashSet<u32>,
    /// File handles of the inspected processes, still to be checked.
    handles: Vec<HandleJob>,
}

impl Census {
    fn inspect(
        &mut self,
        pid: u32,
        exe: &str,
        own_user: &[u64],
        roots: &[PathBuf],
        file_type: usize,
    ) {
        let Some(query) = open_process(pid, PROCESS_QUERY_LIMITED_INFORMATION) else {
            self.unopened.insert(pid);
            return;
        };
        let Some(user) = token_user(raw(&query)) else {
            self.unopened.insert(pid);
            return;
        };
        if !same_user(&user, own_user) {
            return;
        }
        let mut holder = Holder {
            pid,
            exe: exe.to_owned(),
            image: image_path(raw(&query)),
            started: creation_time(raw(&query)).unwrap_or(0),
            command_line: None,
            holds: Vec::new(),
        };
        let peb = open_process(pid, PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ)
            .and_then(|process| peb_strings(raw(&process)));
        if let Some(peb) = peb {
            holder.command_line = peb.command_line.map(|line| String::from_utf16_lossy(&line));
            let cwd = long_path(&peb.current_folder);
            if matches_locked_path(&cwd, roots) {
                holder.holds.push(Hold::CurrentFolder { path: cwd });
            }
        } else {
            debug!("cannot read the current folder of {exe} (pid {pid})");
            self.unopened.insert(pid);
        }
        if let Some(jobs) = file_handle_jobs(pid, file_type) {
            self.handles.extend(jobs);
        } else {
            debug!("cannot list the handles of {exe} (pid {pid})");
            self.unopened.insert(pid);
        }
        self.candidates.insert(pid, holder);
    }

    fn add_hold(&mut self, pid: u32, hold: Hold) {
        if let Some(holder) = self.candidates.get_mut(&pid)
            && !holder.holds.contains(&hold)
        {
            holder.holds.push(hold);
        }
    }

    fn into_report(
        self,
        table: &HashMap<u32, unlock::ProcessEntry>,
        unnamed: &BTreeSet<u32>,
        using: &[u32],
        exclude: &[u32],
    ) -> HolderReport {
        let mut holders: Vec<Holder> = self
            .candidates
            .into_values()
            .filter(|holder| !holder.holds.is_empty())
            .collect();
        holders.sort_by_key(|holder| holder.pid);
        let held: HashSet<u32> = holders.iter().map(|holder| holder.pid).collect();
        let exe = |pid: u32| {
            table
                .get(&pid)
                .map(|entry| entry.exe.clone())
                .unwrap_or_default()
        };
        let mut may_hold: Vec<MayHold> = unnamed
            .iter()
            .filter(|pid| !held.contains(pid))
            .map(|&pid| MayHold {
                pid,
                exe: exe(pid),
                why: MayHoldWhy::UnnamedHandle,
            })
            .collect();
        for &pid in using {
            let listed = held.contains(&pid) || may_hold.iter().any(|may| may.pid == pid);
            if self.unopened.contains(&pid) && !exclude.contains(&pid) && !listed {
                may_hold.push(MayHold {
                    pid,
                    exe: exe(pid),
                    why: MayHoldWhy::CannotOpen,
                });
            }
        }
        may_hold.sort_by_key(|may| may.pid);
        HolderReport { holders, may_hold }
    }
}

/// Takes ownership of a handle a Win32 call just returned.
///
/// # Safety
///
/// `handle` must be a valid, open handle that nothing else owns or closes.
unsafe fn owned(handle: HANDLE) -> OwnedHandle {
    // SAFETY: the caller guarantees `handle` is open and owned by no one else; `OwnedHandle` closes it once.
    unsafe { OwnedHandle::from_raw_handle(handle.0) }
}

fn raw(handle: &OwnedHandle) -> HANDLE {
    HANDLE(handle.as_raw_handle())
}

fn current_process() -> HANDLE {
    // SAFETY: a plain call returning a pseudo handle, which needs no closing.
    unsafe { GetCurrentProcess() }
}

fn open_process(pid: u32, access: PROCESS_ACCESS_RIGHTS) -> Option<OwnedHandle> {
    // SAFETY: a plain call with no pointers.
    let handle = unsafe { OpenProcess(access, false, pid) }.ok()?;
    // SAFETY: `handle` was just opened and nothing else owns it.
    Some(unsafe { owned(handle) })
}

fn wide_nul(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain(Some(0)).collect()
}

/// The process's `TOKEN_USER`, in a buffer aligned for it.
fn token_user(process: HANDLE) -> Option<Vec<u64>> {
    let mut token = HANDLE::default();
    // SAFETY: `process` is open with query access and `token` is a live out-pointer.
    unsafe { OpenProcessToken(process, TOKEN_QUERY, &raw mut token) }.ok()?;
    // SAFETY: `token` was just opened and nothing else owns it.
    let token = unsafe { owned(token) };
    let mut buffer = vec![0_u64; 64];
    let size = u32::try_from(buffer.len() * size_of::<u64>()).ok()?;
    let mut needed = 0_u32;
    // SAFETY: `buffer` is live, writable and `size` bytes long; `needed` is a live out-pointer.
    unsafe {
        GetTokenInformation(
            raw(&token),
            TokenUser,
            Some(buffer.as_mut_ptr().cast()),
            size,
            &raw mut needed,
        )
    }
    .ok()?;
    Some(buffer)
}

fn same_user(first: &[u64], second: &[u64]) -> bool {
    // SAFETY: both buffers hold a TOKEN_USER written by GetTokenInformation and are aligned for it; each SID pointer
    // points into its own buffer, which outlives the comparison.
    unsafe {
        let first = first.as_ptr().cast::<TOKEN_USER>().read();
        let second = second.as_ptr().cast::<TOKEN_USER>().read();
        EqualSid(first.User.Sid, second.User.Sid).is_ok()
    }
}

fn image_path(process: HANDLE) -> Option<PathBuf> {
    for capacity in [1024_usize, 32_768] {
        let mut buffer = vec![0_u16; capacity];
        let mut len = u32::try_from(capacity).ok()?;
        // SAFETY: `buffer` is live and writable for `len` units; `len` is a live in/out pointer.
        let read = unsafe {
            QueryFullProcessImageNameW(
                process,
                PROCESS_NAME_WIN32,
                PWSTR(buffer.as_mut_ptr()),
                &raw mut len,
            )
        };
        if read.is_ok() {
            let len = usize::try_from(len).ok()?;
            return Some(PathBuf::from(OsString::from_wide(buffer.get(..len)?)));
        }
    }
    None
}

fn creation_time(process: HANDLE) -> Option<u64> {
    let (mut created, mut exited, mut kernel, mut user) = (
        FILETIME::default(),
        FILETIME::default(),
        FILETIME::default(),
        FILETIME::default(),
    );
    // SAFETY: `process` is open with query access and the four FILETIMEs are live out-pointers.
    unsafe {
        GetProcessTimes(
            process,
            &raw mut created,
            &raw mut exited,
            &raw mut kernel,
            &raw mut user,
        )
    }
    .ok()?;
    let started = (u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime);
    (started != 0).then_some(started)
}

fn bytes_at<const N: usize>(bytes: &[u8], offset: usize) -> Option<[u8; N]> {
    bytes.get(offset..offset.checked_add(N)?)?.try_into().ok()
}

fn u16_at(bytes: &[u8], offset: usize) -> Option<usize> {
    Some(usize::from(u16::from_le_bytes(bytes_at(bytes, offset)?)))
}

fn u32_at(bytes: &[u8], offset: usize) -> Option<usize> {
    usize::try_from(u32::from_le_bytes(bytes_at(bytes, offset)?)).ok()
}

fn u64_at(bytes: &[u8], offset: usize) -> Option<usize> {
    usize::try_from(u64::from_le_bytes(bytes_at(bytes, offset)?)).ok()
}

fn read_memory(process: HANDLE, address: usize, len: usize) -> Option<Vec<u8>> {
    let mut buffer = vec![0_u8; len];
    // SAFETY: `buffer` is live and `len` bytes long; the call fails cleanly on an unreadable remote address.
    unsafe {
        ReadProcessMemory(
            process,
            std::ptr::with_exposed_provenance::<c_void>(address),
            buffer.as_mut_ptr().cast(),
            len,
            None,
        )
    }
    .ok()?;
    Some(buffer)
}

/// Fills `out` with one fixed-size piece of process information.
fn query_process<T>(process: HANDLE, class: PROCESSINFOCLASS, out: &mut T) -> Option<()> {
    let size = u32::try_from(size_of::<T>()).ok()?;
    // SAFETY: `out` is a live, writable `T` of exactly `size` bytes.
    let status = unsafe {
        NtQueryInformationProcess(
            process,
            class,
            std::ptr::from_mut(out).cast(),
            size,
            std::ptr::null_mut(),
        )
    };
    status.is_ok().then_some(())
}

/// A `UNICODE_STRING` inside `header` at `at`, read from the process; `pointer` is the process's pointer size.
fn remote_string(process: HANDLE, header: &[u8], at: usize, pointer: usize) -> Option<Vec<u16>> {
    let len = u16_at(header, at)?;
    let address = if pointer == 8 {
        u64_at(header, at + 8)?
    } else {
        u32_at(header, at + 4)?
    };
    if len == 0 || address == 0 {
        return None;
    }
    let bytes = read_memory(process, address, len)?;
    Some(
        bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|&pair| u16::from_le_bytes(pair))
            .collect(),
    )
}

struct PebStrings {
    current_folder: Vec<u16>,
    command_line: Option<Vec<u16>>,
}

/// The process's current folder and, for a 64-bit process, its command line, read from its PEB.
fn peb_strings(process: HANDLE) -> Option<PebStrings> {
    let mut wow64_peb = 0_usize;
    query_process(process, ProcessWow64Information, &mut wow64_peb)?;
    if wow64_peb != 0 {
        let peb = read_memory(process, wow64_peb, PEB32_PROCESS_PARAMETERS + 4)?;
        let params = u32_at(&peb, PEB32_PROCESS_PARAMETERS)?;
        let params = read_memory(process, params, PARAMS32_CURRENT_DIRECTORY + 8)?;
        return Some(PebStrings {
            current_folder: remote_string(process, &params, PARAMS32_CURRENT_DIRECTORY, 4)?,
            command_line: None,
        });
    }
    // PROCESS_BASIC_INFORMATION: exit status, PEB address, affinity, priority, pid, parent pid.
    let mut basic = [0_usize; 6];
    query_process(process, ProcessBasicInformation, &mut basic)?;
    let peb = read_memory(process, basic[1], PEB_PROCESS_PARAMETERS + 8)?;
    let params = u64_at(&peb, PEB_PROCESS_PARAMETERS)?;
    let params = read_memory(process, params, PARAMS_COMMAND_LINE + 16)?;
    Some(PebStrings {
        current_folder: remote_string(process, &params, PARAMS_CURRENT_DIRECTORY, 8)?,
        command_line: remote_string(process, &params, PARAMS_COMMAND_LINE, 8),
    })
}

/// `path` with 8.3 names expanded, or as given when that fails.
fn long_path(path: &[u16]) -> PathBuf {
    let given = PathBuf::from(OsString::from_wide(path));
    let wide = wide_nul(given.as_os_str());
    let mut buffer = vec![0_u16; 1024];
    for _ in 0..2 {
        // SAFETY: `wide` is NUL-terminated and `buffer` is a live, writable slice.
        let len = unsafe { GetLongPathNameW(PCWSTR(wide.as_ptr()), Some(&mut buffer)) };
        let Ok(len) = usize::try_from(len) else {
            break;
        };
        if len == 0 {
            break;
        }
        if let Some(long) = buffer.get(..len) {
            return PathBuf::from(OsString::from_wide(long));
        }
        buffer.resize(len, 0);
    }
    given
}

/// `\\?\D:\x` as `D:\x`; any other path as given.
fn without_verbatim_prefix(path: PathBuf) -> PathBuf {
    let text = path.to_string_lossy();
    match text.strip_prefix(r"\\?\") {
        Some(rest) if rest.as_bytes().get(1) == Some(&b':') => PathBuf::from(rest),
        _ => path,
    }
}

/// A variable-size piece of process information, in a buffer grown until it fits.
fn query_growing(process: HANDLE, class: PROCESSINFOCLASS) -> Option<Vec<u8>> {
    let mut len = 64 * 1024;
    for _ in 0..8 {
        let mut buffer = vec![0_u8; len];
        let size = u32::try_from(len).ok()?;
        let mut needed = 0_u32;
        // SAFETY: `buffer` is live, writable and `size` bytes long; `needed` is a live out-pointer.
        let status = unsafe {
            NtQueryInformationProcess(
                process,
                class,
                buffer.as_mut_ptr().cast(),
                size,
                &raw mut needed,
            )
        };
        if [
            STATUS_INFO_LENGTH_MISMATCH,
            STATUS_BUFFER_TOO_SMALL,
            STATUS_BUFFER_OVERFLOW,
        ]
        .contains(&status)
        {
            len = usize::try_from(needed).ok()?.max(len * 2);
            continue;
        }
        return status.is_ok().then_some(buffer);
    }
    None
}

/// Each open handle of a process: its value and its object type index.
fn handle_entries(process: HANDLE) -> Option<Vec<(usize, usize)>> {
    let buffer = query_growing(process, ProcessHandleInformation)?;
    let count = u64_at(&buffer, 0)?;
    (0..count)
        .map(|index| {
            let at = HANDLE_LIST_HEADER + index * HANDLE_ENTRY_SIZE;
            Some((
                u64_at(&buffer, at)?,
                u32_at(&buffer, at + HANDLE_ENTRY_TYPE)?,
            ))
        })
        .collect()
}

/// The object type index of File handles, learnt from a file this process opens itself.
fn file_type_index() -> Result<usize> {
    let exe = std::env::current_exe().context("cannot find this program's path")?;
    let file = File::open(&exe).with_context(|| format!("cannot open {}", exe.display()))?;
    let value = file.as_raw_handle().addr();
    let entries =
        handle_entries(current_process()).context("cannot list this process's own handles")?;
    entries
        .into_iter()
        .find(|&(handle, _)| handle == value)
        .map(|(_, type_index)| type_index)
        .context("cannot find the File object type among this process's handles")
}

/// One File handle of another process, still to be checked.
struct HandleJob {
    pid: u32,
    /// The process, open with `PROCESS_DUP_HANDLE`, shared by every job from it.
    source: Arc<OwnedHandle>,
    /// The handle's value in that process.
    value: usize,
}

/// The process's File handles, as jobs for the naming threads; `None` when they cannot be listed. Nothing is
/// duplicated yet: a duplicate keeps the other process's file open and would make its delete or rename fail.
fn file_handle_jobs(pid: u32, file_type: usize) -> Option<Vec<HandleJob>> {
    let source = Arc::new(open_process(
        pid,
        PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE,
    )?);
    let entries = handle_entries(raw(&source))?;
    Some(
        entries
            .into_iter()
            .filter(|&(_, type_index)| type_index == file_type)
            .map(|(value, _)| HandleJob {
                pid,
                source: Arc::clone(&source),
                value,
            })
            .collect(),
    )
}

/// What checking one handle found.
enum Checked {
    /// It could not be duplicated, or it is not a file on disk.
    Skipped,
    /// A disk file's name, or `None` when the lookup failed.
    Named(Option<PathBuf>),
}

/// Duplicates one handle, names it when it is a file on disk, and closes the duplicate before returning.
fn check_handle(source: &OwnedHandle, value: usize) -> Checked {
    match duplicate_disk_handle(raw(source), value) {
        Some(duplicate) => Checked::Named(final_path(&duplicate)),
        None => Checked::Skipped,
    }
}

fn duplicate_disk_handle(source: HANDLE, value: usize) -> Option<OwnedHandle> {
    let mut duplicate = HANDLE::default();
    // SAFETY: `source` is open with PROCESS_DUP_HANDLE; `value` is only a number in its handle table, and
    // `duplicate` is a live out-pointer.
    unsafe {
        DuplicateHandle(
            source,
            HANDLE(std::ptr::with_exposed_provenance_mut(value)),
            current_process(),
            &raw mut duplicate,
            0,
            false,
            DUPLICATE_SAME_ACCESS,
        )
    }
    .ok()?;
    // SAFETY: `duplicate` was just created and nothing else owns it.
    let duplicate = unsafe { owned(duplicate) };
    // SAFETY: `duplicate` is open; GetFileType returns at once even when I/O is pending on the handle.
    (unsafe { GetFileType(raw(&duplicate)) } == FILE_TYPE_DISK).then_some(duplicate)
}

fn final_path(handle: &OwnedHandle) -> Option<PathBuf> {
    let mut buffer = vec![0_u16; 512];
    for _ in 0..2 {
        // SAFETY: `handle` is open and `buffer` is a live, writable slice.
        let len =
            unsafe { GetFinalPathNameByHandleW(raw(handle), &mut buffer, FILE_NAME_NORMALIZED) };
        let len = usize::try_from(len).ok()?;
        if len == 0 {
            return None;
        }
        if len < buffer.len() {
            return Some(PathBuf::from(OsString::from_wide(buffer.get(..len)?)));
        }
        buffer.resize(len + 1, 0);
    }
    None
}

/// A thread checking handles one at a time, so at most one duplicate per thread is open. Dropping it detaches the
/// thread, which ends once its current lookup returns, if it ever does; until then the duplicate it is naming stays
/// open, at worst until this process exits.
struct Namer {
    jobs: mpsc::Sender<(Arc<OwnedHandle>, usize)>,
    names: mpsc::Receiver<Checked>,
    _thread: JoinHandle<()>,
}

impl Namer {
    fn start() -> Result<Self> {
        let (jobs, job_queue) = mpsc::channel::<(Arc<OwnedHandle>, usize)>();
        let (name_sender, names) = mpsc::channel();
        let thread = thread::Builder::new()
            .name("handle-namer".to_owned())
            .spawn(move || {
                for (source, value) in job_queue {
                    if name_sender.send(check_handle(&source, value)).is_err() {
                        break;
                    }
                }
            })
            .context("cannot start a handle-naming thread")?;
        Ok(Self {
            jobs,
            names,
            _thread: thread,
        })
    }
}

/// Checks every handle on [`NAME_WORKERS`] threads, each allowed [`NAME_TIMEOUT`], and returns the disk files'
/// names by pid; a lookup that fails or times out gives `None`.
fn name_handles(handles: Vec<HandleJob>) -> Result<Vec<(u32, Option<PathBuf>)>> {
    let queue = Mutex::new(handles);
    thread::scope(|scope| {
        let mut drivers = Vec::with_capacity(NAME_WORKERS);
        for _ in 0..NAME_WORKERS {
            drivers.push(
                thread::Builder::new()
                    .spawn_scoped(scope, || drive_namer(&queue))
                    .context("cannot start a handle-naming thread")?,
            );
        }
        let mut names = Vec::new();
        for driver in drivers {
            let driven = driver
                .join()
                .map_err(|_| anyhow!("a handle-naming thread panicked"))?;
            names.extend(driven?);
        }
        Ok(names)
    })
}

fn drive_namer(queue: &Mutex<Vec<HandleJob>>) -> Result<Vec<(u32, Option<PathBuf>)>> {
    let mut namer = Namer::start()?;
    let mut names = Vec::new();
    loop {
        let next = queue
            .lock()
            .map_err(|_| anyhow!("the handle queue is poisoned"))?
            .pop();
        let Some(HandleJob { pid, source, value }) = next else {
            break;
        };
        if namer.jobs.send((source, value)).is_err() {
            bail!("a handle-naming thread stopped");
        }
        if let Ok(checked) = namer.names.recv_timeout(NAME_TIMEOUT) {
            if let Checked::Named(name) = checked {
                names.push((pid, name));
            }
        } else {
            debug!("naming a handle of pid {pid} timed out; abandoning its thread");
            names.push((pid, None));
            namer = Namer::start()?;
        }
    }
    Ok(names)
}

/// The processes the file system reports as using `path` itself.
fn pids_using(path: &Path) -> Result<Vec<u32>> {
    let wide = wide_nul(path.as_os_str());
    // SAFETY: `wide` is NUL-terminated and outlives the call; the handle it returns is owned right below.
    let file = unsafe {
        CreateFileW(
            PCWSTR(wide.as_ptr()),
            FILE_READ_ATTRIBUTES.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            None,
        )
    }
    .with_context(|| format!("cannot open {}", path.display()))?;
    // SAFETY: `file` was just opened and nothing else owns it.
    let file = unsafe { owned(file) };
    let mut len = 4096;
    for _ in 0..8 {
        let mut buffer = vec![0_u8; len];
        let size = u32::try_from(len)?;
        let mut status_block = IO_STATUS_BLOCK::default();
        // SAFETY: `file` is open, `buffer` is live, writable and `size` bytes long, `status_block` is a live
        // out-pointer.
        let status = unsafe {
            NtQueryInformationFile(
                raw(&file),
                &raw mut status_block,
                buffer.as_mut_ptr().cast(),
                size,
                FileProcessIdsUsingFileInformation,
            )
        };
        if status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_OVERFLOW {
            len *= 4;
            continue;
        }
        status
            .ok()
            .with_context(|| format!("cannot list the processes using {}", path.display()))?;
        let count = u32_at(&buffer, 0).context("short process id list")?;
        return (0..count)
            .map(|index| u64_at(&buffer, 8 + index * 8).and_then(|pid| u32::try_from(pid).ok()))
            .collect::<Option<Vec<u32>>>()
            .context("malformed process id list");
    }
    bail!("the process id list for {} kept growing", path.display())
}

#[cfg(test)]
mod tests {
    use anyhow::ensure;

    use super::*;

    fn holder(exe: &str, command_line: Option<&str>) -> Holder {
        Holder {
            pid: 42,
            exe: exe.to_owned(),
            image: None,
            started: 1,
            command_line: command_line.map(str::to_owned),
            holds: vec![Hold::CurrentFolder {
                path: PathBuf::from(r"D:\repo.wt\x"),
            }],
        }
    }

    fn is_stoppable(exe: &str, command_line: Option<&str>) -> bool {
        let report = HolderReport {
            holders: vec![holder(exe, command_line)],
            may_hold: Vec::new(),
        };
        stoppable(&report).len() == 1
    }

    #[test]
    fn rust_analyzer_variants_are_stoppable() -> Result<()> {
        ensure!(is_stoppable("rust-analyzer.exe", None));
        ensure!(is_stoppable(
            "rust-analyzer-x86_64-pc-windows-msvc.exe",
            None
        ));
        ensure!(!is_stoppable("rust-analyzer-helper.txt", None));
        Ok(())
    }

    #[test]
    fn proc_macro_server_is_stoppable() -> Result<()> {
        ensure!(is_stoppable("rust-analyzer-proc-macro-srv.exe", None));
        Ok(())
    }

    #[test]
    fn editors_and_shells_are_not_stoppable() -> Result<()> {
        for exe in ["code.exe", "devenv.exe", "pwsh.exe", "bash.exe", "node.exe"] {
            ensure!(!is_stoppable(exe, None), "{exe}");
        }
        Ok(())
    }

    #[test]
    fn build_servers_are_stoppable_in_any_case() -> Result<()> {
        for exe in [
            "cargo.exe",
            "MSBuild.exe",
            "VBCSCompiler.exe",
            "CARGO.EXE",
            "msbuild.exe",
        ] {
            ensure!(is_stoppable(exe, None), "{exe}");
        }
        Ok(())
    }

    #[test]
    fn cargo_lookalikes_are_not_stoppable() -> Result<()> {
        ensure!(!is_stoppable("cargo-watch.exe", None));
        ensure!(!is_stoppable("rustc.exe", None));
        Ok(())
    }

    #[test]
    fn dotnet_msbuild_node_is_stoppable() -> Result<()> {
        let sdk = r#""C:\Program Files\dotnet\sdk\9.0.100\MSBuild.dll""#;
        ensure!(is_stoppable(
            "dotnet.exe",
            Some(&format!(
                "dotnet.exe {sdk} /nologo /nodemode:1 /nodeReuse:true"
            ))
        ));
        ensure!(is_stoppable(
            "dotnet.exe",
            Some(&format!("dotnet.exe {sdk} -nodemode:8"))
        ));
        Ok(())
    }

    #[test]
    fn dotnet_foreground_build_is_not_stoppable() -> Result<()> {
        ensure!(!is_stoppable(
            "dotnet.exe",
            Some(r"dotnet.exe C:\sdk\MSBuild.dll -restore D:\repo\app.csproj")
        ));
        Ok(())
    }

    #[test]
    fn dotnet_compiler_server_is_stoppable() -> Result<()> {
        ensure!(is_stoppable(
            "dotnet.exe",
            Some(r"dotnet.exe C:\sdk\Roslyn\bincore\VBCSCompiler.dll -pipename:abc")
        ));
        Ok(())
    }

    #[test]
    fn dotnet_without_command_line_is_not_stoppable() -> Result<()> {
        ensure!(!is_stoppable("dotnet.exe", None));
        Ok(())
    }

    #[test]
    fn may_hold_only_is_not_stoppable() -> Result<()> {
        let report = HolderReport {
            holders: Vec::new(),
            may_hold: vec![MayHold {
                pid: 42,
                exe: "rust-analyzer.exe".to_owned(),
                why: MayHoldWhy::UnnamedHandle,
            }],
        };
        ensure!(stoppable(&report).is_empty());
        Ok(())
    }

    #[test]
    fn holder_without_holds_is_not_stoppable() -> Result<()> {
        let mut idle = holder("cargo.exe", None);
        idle.holds.clear();
        let report = HolderReport {
            holders: vec![idle],
            may_hold: Vec::new(),
        };
        ensure!(stoppable(&report).is_empty());
        Ok(())
    }

    fn table(entries: &[(u32, u32, Option<u64>)]) -> ProcessTimes {
        entries
            .iter()
            .map(|&(pid, parent, started)| {
                (
                    pid,
                    TimedProcess {
                        parent,
                        exe: format!("p{pid}.exe"),
                        started,
                    },
                )
            })
            .collect()
    }

    #[test]
    fn ancestors_follow_a_three_deep_chain() -> Result<()> {
        let processes = table(&[(30, 20, Some(300)), (20, 10, Some(200)), (10, 1, Some(100))]);
        let chain = ancestors(30, &processes);
        ensure!(chain == [30, 20, 10], "{chain:?}");
        Ok(())
    }

    #[test]
    fn ancestors_stop_at_a_parent_started_later() -> Result<()> {
        let processes = table(&[(30, 20, Some(300)), (20, 10, Some(200)), (10, 5, Some(250))]);
        let chain = ancestors(30, &processes);
        ensure!(chain == [30, 20], "{chain:?}");
        let unknown = table(&[(30, 20, Some(300)), (20, 10, None)]);
        let chain = ancestors(30, &unknown);
        ensure!(chain == [30], "{chain:?}");
        Ok(())
    }

    #[test]
    fn ancestors_stop_at_a_cycle() -> Result<()> {
        let processes = table(&[(30, 20, Some(300)), (20, 30, Some(200))]);
        let chain = ancestors(30, &processes);
        ensure!(chain == [30, 20], "{chain:?}");
        let own_parent = table(&[(7, 7, Some(1))]);
        let chain = ancestors(7, &own_parent);
        ensure!(chain == [7], "{chain:?}");
        Ok(())
    }

    #[test]
    fn ancestors_stop_at_a_missing_parent() -> Result<()> {
        let processes = table(&[(30, 20, Some(300)), (20, 10, Some(200))]);
        let chain = ancestors(30, &processes);
        ensure!(chain == [30, 20], "{chain:?}");
        let chain = ancestors(99, &processes);
        ensure!(chain == [99], "{chain:?}");
        Ok(())
    }

    #[test]
    fn verbatim_drive_prefix_is_dropped() -> Result<()> {
        ensure!(without_verbatim_prefix(PathBuf::from(r"\\?\D:\a\b")) == Path::new(r"D:\a\b"));
        ensure!(
            without_verbatim_prefix(PathBuf::from(r"\\?\UNC\srv\s\x"))
                == Path::new(r"\\?\UNC\srv\s\x")
        );
        Ok(())
    }
}
