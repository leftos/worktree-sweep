//! The Recycle Bin: whether a folder fits in its volume's bin, and moving it there through the Shell.

use std::ffi::OsStr;
use std::marker::PhantomData;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

use anyhow::{Context, Result, anyhow};
use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS};
use windows::Win32::Storage::FileSystem::{GetVolumeNameForVolumeMountPointW, GetVolumePathNameW};
use windows::Win32::System::Com::{
    CLSCTX_ALL, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx, CoUninitialize,
};
use windows::Win32::System::Registry::{HKEY_CURRENT_USER, RRF_RT_REG_DWORD, RegGetValueW};
use windows::Win32::UI::Shell::{
    FILEOPERATION_FLAGS, FOF_ALLOWUNDO, FOF_NOCONFIRMMKDIR, FOF_SILENT, FOF_WANTNUKEWARNING,
    FOFX_RECYCLEONDELETE, FileOperation, IFileOperation, IShellItem, SHCreateItemFromParsingName,
};
use windows::core::{HRESULT, PCWSTR};

use crate::remove::RemoveError;
use crate::report::human_bytes;

const BIT_BUCKET_VOLUMES: &str =
    r"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

/// A volume's Recycle Bin settings, from `HKCU\...\Explorer\BitBucket\Volume\{GUID}`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BinCapacity {
    /// `MaxCapacity`: the most the bin holds, in MB (MiB).
    pub max_capacity_mb: u64,
    /// `NukeOnDelete`: deleted files skip the bin.
    pub nuke_on_delete: bool,
}

/// How a folder can be removed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Decision {
    /// It fits in the Recycle Bin.
    Recycle,
    /// It cannot go to the Recycle Bin, for the reason given; a permanent delete needs the user's yes.
    AskPermanent(String),
}

/// Whether a folder of `size_bytes` can go to a Recycle Bin with `capacity` (`None` when unknown). A folder
/// exactly the bin's size still fits.
#[must_use]
pub fn recycle_decision(size_bytes: u64, capacity: Option<BinCapacity>) -> Decision {
    let Some(capacity) = capacity else {
        return Decision::AskPermanent("the Recycle Bin size of its volume is unknown".to_owned());
    };
    if capacity.nuke_on_delete {
        return Decision::AskPermanent(
            "the Recycle Bin of its volume is set to delete files immediately (NukeOnDelete)"
                .to_owned(),
        );
    }
    let max_bytes = capacity.max_capacity_mb.saturating_mul(1024 * 1024);
    if size_bytes > max_bytes {
        Decision::AskPermanent(format!(
            "its {} is more than the {} the Recycle Bin of its volume holds",
            human_bytes(size_bytes),
            human_bytes(max_bytes)
        ))
    } else {
        Decision::Recycle
    }
}

/// Reads the Recycle Bin settings of the volume `path` is on; `None` when either value is missing.
///
/// # Errors
///
/// When the volume cannot be named or the registry cannot be read.
pub fn bin_capacity(path: &Path) -> Result<Option<BinCapacity>> {
    let guid = volume_guid(path)?;
    let key = format!(r"{BIT_BUCKET_VOLUMES}\{guid}");
    let max_capacity = read_dword(&key, "MaxCapacity")?;
    let nuke_on_delete = read_dword(&key, "NukeOnDelete")?;
    Ok(match (max_capacity, nuke_on_delete) {
        (Some(max_capacity), Some(nuke_on_delete)) => Some(BinCapacity {
            max_capacity_mb: u64::from(max_capacity),
            nuke_on_delete: nuke_on_delete != 0,
        }),
        _ => None,
    })
}

/// The `{GUID}` of the volume `path` is on.
fn volume_guid(path: &Path) -> Result<String> {
    let path_wide = wide(path.as_os_str());
    let mut mount_point = [0_u16; 1024];
    // SAFETY: `path_wide` is NUL-terminated and outlives the call; the output buffer is a live slice.
    unsafe { GetVolumePathNameW(PCWSTR(path_wide.as_ptr()), &mut mount_point) }
        .with_context(|| format!("cannot find the volume of {}", path.display()))?;
    let mut volume = [0_u16; 128];
    // SAFETY: `mount_point` was NUL-terminated by the call above and outlives this one.
    unsafe { GetVolumeNameForVolumeMountPointW(PCWSTR(mount_point.as_ptr()), &mut volume) }
        .with_context(|| {
            format!(
                "cannot name the volume mounted at {}",
                from_wide(&mount_point)
            )
        })?;
    let name = from_wide(&volume);
    let start = name.find('{');
    let end = name.find('}');
    match (start, end) {
        (Some(start), Some(end)) if start < end => Ok(name[start..=end].to_owned()),
        _ => Err(anyhow!("volume name {name:?} has no {{GUID}}")),
    }
}

/// A `REG_DWORD` under `HKEY_CURRENT_USER`; `None` when the key or the value is missing.
fn read_dword(key: &str, value: &str) -> Result<Option<u32>> {
    let key_wide = wide(OsStr::new(key));
    let value_wide = wide(OsStr::new(value));
    let mut data: u32 = 0;
    let mut size = u32::try_from(size_of::<u32>()).context("DWORD size")?;
    // SAFETY: both names are NUL-terminated and outlive the call; `data` and `size` are live locals of the sizes
    // the call is told.
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            PCWSTR(key_wide.as_ptr()),
            PCWSTR(value_wide.as_ptr()),
            RRF_RT_REG_DWORD,
            None,
            Some((&raw mut data).cast()),
            Some(&raw mut size),
        )
    };
    if status == ERROR_SUCCESS {
        Ok(Some(data))
    } else if status == ERROR_FILE_NOT_FOUND {
        Ok(None)
    } else {
        Err(anyhow!(
            "cannot read HKCU\\{key}\\{value}: {}",
            windows::core::Error::from_hresult(status.to_hresult())
        ))
    }
}

/// COM initialised as a single-threaded apartment on the current thread, until dropped.
#[derive(Debug)]
pub struct ComApartment {
    _not_send: PhantomData<*const ()>,
}

impl ComApartment {
    /// Initialises COM on the current thread; the Shell's file operations need it.
    ///
    /// # Errors
    ///
    /// When the thread already runs COM in another mode.
    pub fn init() -> Result<Self> {
        // SAFETY: no reserved pointer; the matching `CoUninitialize` runs in `drop` on the same thread, as the
        // type is not `Send`.
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
            .ok()
            .context("cannot initialise COM on this thread")?;
        Ok(Self {
            _not_send: PhantomData,
        })
    }
}

impl Drop for ComApartment {
    fn drop(&mut self) {
        // SAFETY: balances the successful `CoInitializeEx` in `init`, on the same thread.
        unsafe { CoUninitialize() };
    }
}

/// Moves `path` to the Recycle Bin with the Shell's `IFileOperation`. The flags never allow a silent permanent
/// delete: when the Shell decides the item cannot be recycled, it shows its own warning. COM must be initialised
/// on the calling thread ([`ComApartment`]).
///
/// # Errors
///
/// [`RemoveError::Locked`] when a file is in use (sharing violation or access denied); otherwise a generic error,
/// including when the user cancels the Shell's dialog or the folder is still there afterwards.
pub fn recycle(path: &Path) -> Result<(), RemoveError> {
    let path_wide = wide(path.as_os_str());
    match shell_delete(&path_wide) {
        Ok(false) => {}
        Ok(true) => {
            return Err(anyhow!(
                "moving {} to the Recycle Bin was cancelled or aborted",
                path.display()
            )
            .into());
        }
        Err(error) if is_locked_hresult(error.code()) => {
            return Err(RemoveError::Locked {
                path: path.to_path_buf(),
                first_locked_file: None,
            });
        }
        Err(error) => {
            return Err(anyhow::Error::new(error)
                .context(format!("cannot move {} to the Recycle Bin", path.display()))
                .into());
        }
    }
    if std::fs::symlink_metadata(path).is_ok() {
        return Err(anyhow!(
            "{} is still there after moving it to the Recycle Bin",
            path.display()
        )
        .into());
    }
    Ok(())
}

/// Runs one Shell delete with undo; returns whether any operation was aborted.
fn shell_delete(path_wide: &[u16]) -> windows::core::Result<bool> {
    let flags = FILEOPERATION_FLAGS(
        FOF_ALLOWUNDO.0
            | FOFX_RECYCLEONDELETE.0
            | FOF_WANTNUKEWARNING.0
            | FOF_NOCONFIRMMKDIR.0
            | FOF_SILENT.0,
    );
    // SAFETY: COM is initialised on this thread by the caller; `path_wide` is NUL-terminated and outlives the
    // calls; every interface is released when it drops.
    unsafe {
        let operation: IFileOperation = CoCreateInstance(&FileOperation, None, CLSCTX_ALL)?;
        operation.SetOperationFlags(flags)?;
        let item: IShellItem = SHCreateItemFromParsingName(PCWSTR(path_wide.as_ptr()), None)?;
        operation.DeleteItem(&item, None)?;
        operation.PerformOperations()?;
        Ok(operation.GetAnyOperationsAborted()?.as_bool())
    }
}

fn is_locked_hresult(code: HRESULT) -> bool {
    code == HRESULT::from_win32(32) || code == HRESULT::from_win32(5)
}

fn wide(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain(std::iter::once(0)).collect()
}

fn from_wide(buffer: &[u16]) -> String {
    let end = buffer
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(buffer.len());
    String::from_utf16_lossy(&buffer[..end])
}

#[cfg(test)]
mod tests {
    use super::*;

    const MB: u64 = 1024 * 1024;

    fn capacity(max_capacity_mb: u64) -> BinCapacity {
        BinCapacity {
            max_capacity_mb,
            nuke_on_delete: false,
        }
    }

    #[test]
    fn recycle_decision_under() {
        assert_eq!(
            recycle_decision(100 * MB, Some(capacity(14_844))),
            Decision::Recycle
        );
    }

    #[test]
    fn recycle_decision_over() {
        let decision = recycle_decision(14_845 * MB, Some(capacity(14_844)));
        assert!(
            matches!(&decision, Decision::AskPermanent(reason) if reason.contains("14.5 GB")),
            "{decision:?}"
        );
    }

    #[test]
    fn recycle_decision_equal_boundary() {
        assert_eq!(
            recycle_decision(14_844 * MB, Some(capacity(14_844))),
            Decision::Recycle
        );
        assert!(matches!(
            recycle_decision(14_844 * MB + 1, Some(capacity(14_844))),
            Decision::AskPermanent(_)
        ));
    }

    #[test]
    fn recycle_decision_nuke() {
        let decision = recycle_decision(
            1,
            Some(BinCapacity {
                max_capacity_mb: 14_844,
                nuke_on_delete: true,
            }),
        );
        assert!(
            matches!(&decision, Decision::AskPermanent(reason) if reason.contains("NukeOnDelete")),
            "{decision:?}"
        );
    }

    #[test]
    fn recycle_decision_unknown() {
        let decision = recycle_decision(1, None);
        assert!(
            matches!(&decision, Decision::AskPermanent(reason) if reason.contains("unknown")),
            "{decision:?}"
        );
    }
}
