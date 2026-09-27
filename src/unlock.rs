//! Clearing file locks that stop a removal.

use std::path::PathBuf;

use anyhow::Result;
use tracing::warn;

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

/// Offers, once for the whole run, to clear the locks other processes hold on files under `paths`. This build
/// cannot clear them yet: it logs each locked path and skips.
///
/// # Errors
///
/// None in this build.
pub fn offer(paths: &[PathBuf]) -> Result<UnlockOutcome> {
    for path in paths {
        warn!(
            "{} is locked by another process; clearing locks is not available in this build",
            path.display()
        );
    }
    Ok(UnlockOutcome::Skipped)
}
