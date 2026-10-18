# Changelog

## [Unreleased]

### Added

- Scan a folder for linked git worktrees and orphan worktree folders, and list them with `--list` or as a JSON report with `--json`.
- Show each worktree's merge state (merged, no commits, cherry-picked, squashed, unmerged), uncommitted files, unpushed commits, last activity and size.
- Install with `pwsh tools/install.ps1`, which needs the .NET 10 Desktop runtime and adds the tool to your user PATH.
- Pick worktrees to remove in a window that opens without `--list` or `--json`; every question comes first, ending with one confirmation.
- Open the window without a folder to type one, browse to one, or rescan one of your last five scanned folders.
- Follow a scan step by step, see which worktrees it is reading, stop it at any time, and rescan the folder.
- Sort, filter and search the candidate list, and tick or untick every shown row at once.
- Problems the scan met listing a repo's worktrees show in the window, even when it found nothing to remove.
- Removal shows its progress in items and bytes, and can stop after the current item.
- A second confirmation guards any pick that would lose work or has no commits yet.
- Removed folders go to the Recycle Bin; a folder too big for it asks for a permanent delete instead.
- Junction and symbolic link orphans are removed as links, leaving their targets untouched.
- Prune removed worktrees' registrations and offer to delete their branches when merged.
- When folders are locked, one `sudo` prompt finds the processes holding them, whatever characters their paths hold, and lets you stop them or close their handles, then retries.
- `remove <PATH> --json` lets an agent remove its own worktree without prompts; a locked one is reported with its holders, and the next sweep lists it first, already picked.
